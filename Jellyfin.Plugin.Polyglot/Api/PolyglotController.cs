using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Polyglot.Helpers;
using Jellyfin.Plugin.Polyglot.Models;
using Jellyfin.Plugin.Polyglot.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Polyglot.Api;

/// <summary>
/// REST API controller for the Polyglot plugin.
/// </summary>
[ApiController]
[Route("Polyglot")]
[Authorize(Policy = "RequiresElevation")]
[Produces(MediaTypeNames.Application.Json)]
public class PolyglotController : ControllerBase
{
    private readonly IMirrorService _mirrorService;
    private readonly IUserLanguageService _userLanguageService;
    private readonly ILibraryAccessService _libraryAccessService;
    private readonly ILdapIntegrationService _ldapIntegrationService;
    private readonly IDebugReportService _debugReportService;
    private readonly ILogger<PolyglotController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PolyglotController"/> class.
    /// </summary>
    public PolyglotController(
        IMirrorService mirrorService,
        IUserLanguageService userLanguageService,
        ILibraryAccessService libraryAccessService,
        ILdapIntegrationService ldapIntegrationService,
        IDebugReportService debugReportService,
        ILogger<PolyglotController> logger)
    {
        _mirrorService = mirrorService;
        _userLanguageService = userLanguageService;
        _libraryAccessService = libraryAccessService;
        _ldapIntegrationService = ldapIntegrationService;
        _debugReportService = debugReportService;
        _logger = logger;
    }

    #region Libraries

    /// <summary>
    /// Gets all Jellyfin libraries with their language settings.
    /// </summary>
    [HttpGet("Libraries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<LibraryInfo>> GetLibraries()
    {
        var libraries = _mirrorService.GetJellyfinLibraries();
        return Ok(libraries);
    }

    #endregion

    #region Language Alternatives

    /// <summary>
    /// Gets all configured language alternatives.
    /// </summary>
    [HttpGet("Alternatives")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<LanguageAlternative>> GetAlternatives()
    {
        var config = Plugin.Instance?.Configuration;
        return Ok(config?.LanguageAlternatives ?? new List<LanguageAlternative>());
    }

    /// <summary>
    /// Creates a new language alternative.
    /// </summary>
    [HttpPost("Alternatives")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<LanguageAlternative> CreateAlternative([FromBody] CreateAlternativeRequest request)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, "Plugin not configured");
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest("Name is required");
        }

        if (string.IsNullOrWhiteSpace(request.LanguageCode))
        {
            return BadRequest("Language code is required");
        }

        if (string.IsNullOrWhiteSpace(request.DestinationBasePath))
        {
            return BadRequest("Destination base path is required");
        }

        // Validate destination base path - must be absolute path
        if (!Path.IsPathRooted(request.DestinationBasePath))
        {
            return BadRequest("Destination base path must be an absolute path");
        }

        // Check for duplicate name
        if (config.LanguageAlternatives.Any(a => string.Equals(a.Name, request.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return BadRequest($"A language alternative with the name '{request.Name}' already exists");
        }

        var alternative = new LanguageAlternative
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            LanguageCode = request.LanguageCode,
            MetadataLanguage = request.MetadataLanguage ?? GetLanguageFromCode(request.LanguageCode),
            MetadataCountry = request.MetadataCountry ?? GetCountryFromCode(request.LanguageCode),
            DestinationBasePath = request.DestinationBasePath,
            CreatedAt = DateTime.UtcNow
        };

        config.LanguageAlternatives.Add(alternative);
        Plugin.Instance?.SaveConfiguration();

        _logger.PolyglotInfo("Created language alternative: {0} ({1})", alternative.Name, alternative.LanguageCode);

        return CreatedAtAction(nameof(GetAlternatives), new { id = alternative.Id }, alternative);
    }

    /// <summary>
    /// Deletes a language alternative.
    /// </summary>
    [HttpDelete("Alternatives/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteAlternative(
        Guid id,
        [FromQuery] bool deleteLibraries = false,
        [FromQuery] bool deleteFiles = false,
        CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return NotFound();
        }

        var alternative = config.LanguageAlternatives.FirstOrDefault(a => a.Id == id);
        if (alternative == null)
        {
            return NotFound();
        }

        // Delete mirrors
        foreach (var mirror in alternative.MirroredLibraries.ToList())
        {
            try
            {
                await _mirrorService.DeleteMirrorAsync(mirror, deleteLibraries, deleteFiles, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.PolyglotError(ex, "Failed to delete mirror {0}", mirror.Id);
            }
        }

        config.LanguageAlternatives.Remove(alternative);
        Plugin.Instance?.SaveConfiguration();

        _logger.PolyglotInfo("Deleted language alternative: {0}", alternative.Name);

        return NoContent();
    }

    /// <summary>
    /// Adds a library mirror to a language alternative.
    /// Mirror creation happens in the background - check the mirror status for progress.
    /// </summary>
    [HttpPost("Alternatives/{id:guid}/Libraries")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<LibraryMirror> AddLibraryMirror(
        Guid id,
        [FromBody] AddLibraryMirrorRequest request)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, "Plugin not configured");
        }

        var alternative = config.LanguageAlternatives.FirstOrDefault(a => a.Id == id);
        if (alternative == null)
        {
            return NotFound("Language alternative not found");
        }

        // Parse source library ID (accepts both formats: with and without dashes)
        if (!Guid.TryParse(request.SourceLibraryId, out var sourceLibraryId))
        {
            return BadRequest("Invalid source library ID format");
        }

        // Validate mirror configuration
        var validation = _mirrorService.ValidateMirrorConfiguration(sourceLibraryId, request.TargetPath);
        if (!validation.IsValid)
        {
            return BadRequest(validation.ErrorMessage);
        }

        // Get source library info
        var sourceLibrary = _mirrorService.GetJellyfinLibraries()
            .FirstOrDefault(l => l.Id == sourceLibraryId);

        if (sourceLibrary == null)
        {
            return BadRequest("Source library not found");
        }

        // Prevent mirroring a mirror library
        if (sourceLibrary.IsMirror)
        {
            return BadRequest("Cannot create a mirror of a mirror library. Please select a source library.");
        }

        // Check if this alternative already has a mirror for this source library
        if (alternative.MirroredLibraries.Any(m => m.SourceLibraryId == sourceLibraryId))
        {
            return BadRequest($"This language alternative already has a mirror for '{sourceLibrary.Name}'. Each source library can only be mirrored once per language.");
        }

        var mirror = new LibraryMirror
        {
            Id = Guid.NewGuid(),
            SourceLibraryId = sourceLibraryId,
            SourceLibraryName = sourceLibrary.Name,
            TargetLibraryName = request.TargetLibraryName ?? $"{sourceLibrary.Name} ({alternative.Name})",
            TargetPath = request.TargetPath,
            CollectionType = sourceLibrary.CollectionType,
            Status = SyncStatus.Pending
        };

        alternative.MirroredLibraries.Add(mirror);
        Plugin.Instance?.SaveConfiguration();

        _logger.PolyglotInfo("Queued mirror creation for {0} -> {1}",
            sourceLibrary.Name, mirror.TargetLibraryName);

        // Create the mirror in the background
        _ = Task.Run(async () =>
        {
            try
            {
                await _mirrorService.CreateMirrorAsync(alternative, mirror, CancellationToken.None);

                // Update library access for all users assigned to this language alternative
                var currentConfig = Plugin.Instance?.Configuration;
                if (currentConfig != null)
                {
                    var usersWithThisLanguage = currentConfig.UserLanguages
                        .Where(u => u.SelectedAlternativeId == alternative.Id && u.IsPluginManaged)
                        .Select(u => u.UserId)
                        .ToList();

                    foreach (var userId in usersWithThisLanguage)
                    {
                        await _libraryAccessService.UpdateUserLibraryAccessAsync(userId, CancellationToken.None)
                            .ConfigureAwait(false);
                    }

                    _logger.PolyglotInfo("Updated library access for {0} users after creating mirror {1}",
                        usersWithThisLanguage.Count, mirror.TargetLibraryName);
                }
            }
            catch (Exception ex)
            {
                _logger.PolyglotError(ex, "Background mirror creation failed for {0}", sourceLibrary.Name);
            }
        });

        return Accepted(mirror);
    }

    /// <summary>
    /// Triggers sync for all mirrors in a language alternative.
    /// </summary>
    [HttpPost("Alternatives/{id:guid}/Sync")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SyncAlternative(Guid id, CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration;
        var alternative = config?.LanguageAlternatives.FirstOrDefault(a => a.Id == id);

        if (alternative == null)
        {
            return NotFound();
        }

        // Start sync in background
        _ = Task.Run(async () =>
        {
            try
            {
                await _mirrorService.SyncAllMirrorsAsync(alternative, null, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.PolyglotError(ex, "Background sync failed for alternative {0}", alternative.Name);
            }
        }, cancellationToken);

        return Accepted();
    }

    /// <summary>
    /// Deletes a library mirror from a language alternative.
    /// </summary>
    [HttpDelete("Alternatives/{id:guid}/Libraries/{sourceLibraryId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteLibraryMirror(
        Guid id,
        Guid sourceLibraryId,
        [FromQuery] bool deleteLibrary = false,
        [FromQuery] bool deleteFiles = false,
        CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return NotFound();
        }

        var alternative = config.LanguageAlternatives.FirstOrDefault(a => a.Id == id);
        if (alternative == null)
        {
            return NotFound("Language alternative not found");
        }

        var mirror = alternative.MirroredLibraries.FirstOrDefault(m => m.SourceLibraryId == sourceLibraryId);
        if (mirror == null)
        {
            return NotFound("Mirror not found");
        }

        try
        {
            await _mirrorService.DeleteMirrorAsync(mirror, deleteLibrary, deleteFiles, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.PolyglotError(ex, "Failed to delete mirror {0}", mirror.Id);
            return BadRequest($"Failed to delete mirror: {ex.Message}");
        }

        alternative.MirroredLibraries.Remove(mirror);
        Plugin.Instance?.SaveConfiguration();

        // Update library access for users assigned to this language alternative
        try
        {
            var usersWithThisLanguage = config.UserLanguages
                .Where(u => u.SelectedAlternativeId == alternative.Id && u.IsPluginManaged)
                .Select(u => u.UserId)
                .ToList();

            foreach (var userId in usersWithThisLanguage)
            {
                await _libraryAccessService.UpdateUserLibraryAccessAsync(userId, cancellationToken)
                    .ConfigureAwait(false);
            }

            _logger.PolyglotInfo("Updated library access for {0} users after deleting mirror {1}",
                usersWithThisLanguage.Count, mirror.TargetLibraryName);
        }
        catch (Exception ex)
        {
            _logger.PolyglotError(ex, "Failed to update user library access after deleting mirror");
        }

        _logger.PolyglotInfo("Deleted mirror: {0} from alternative {1}", 
            mirror.TargetLibraryName, alternative.Name);

        return NoContent();
    }

    #endregion

    #region Users

    /// <summary>
    /// Gets all users with their language assignments.
    /// </summary>
    [HttpGet("Users")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<UserInfo>> GetUsers()
    {
        var users = _userLanguageService.GetAllUsersWithLanguages();
        return Ok(users);
    }

    /// <summary>
    /// Sets a user's language assignment.
    /// </summary>
    [HttpPut("Users/{userId:guid}/Language")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetUserLanguage(
        Guid userId,
        [FromBody] SetUserLanguageRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Handle "disabled" option - user is not managed by plugin
            if (request.IsDisabled)
            {
                await _libraryAccessService.DisableUserAsync(userId, restoreFullAccess: false, cancellationToken);
                return NoContent();
            }

            await _userLanguageService.AssignLanguageAsync(
                userId,
                request.AlternativeId,
                "admin",
                manuallySet: request.ManuallySet,
                isPluginManaged: true, // Enable plugin management
                cancellationToken);

            return NoContent();
        }
        catch (ArgumentException ex)
        {
            // Distinguish between user not found (404) and invalid alternative ID (400)
            // User ID is in the URL path - if not found, it's a 404
            // Alternative ID is in the request body - if invalid, it's a 400
            if (ex.ParamName == "userId")
            {
                return NotFound(ex.Message);
            }

            // Invalid alternative ID or other argument errors are bad requests
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.PolyglotError(ex, "Failed to set language for user {0}", userId);
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Enables plugin management for all users, setting them to default language.
    /// </summary>
    [HttpPost("Users/EnableAll")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<EnableAllUsersResult>> EnableAllUsers(CancellationToken cancellationToken = default)
    {
        var count = await _libraryAccessService.EnableAllUsersAsync(cancellationToken);
        return Ok(new EnableAllUsersResult { UsersEnabled = count });
    }

    /// <summary>
    /// Disables plugin management for a user.
    /// </summary>
    [HttpPost("Users/{userId:guid}/Disable")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DisableUser(
        Guid userId,
        [FromQuery] bool restoreFullAccess = false,
        CancellationToken cancellationToken = default)
    {
        await _libraryAccessService.DisableUserAsync(userId, restoreFullAccess, cancellationToken);
        return NoContent();
    }

    #endregion

    #region LDAP

    /// <summary>
    /// Gets LDAP integration status.
    /// </summary>
    [HttpGet("LdapStatus")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<LdapStatus> GetLdapStatus()
    {
        var status = _ldapIntegrationService.GetLdapStatus();
        return Ok(status);
    }

    /// <summary>
    /// Gets LDAP group mappings.
    /// </summary>
    [HttpGet("LdapGroups")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<LdapGroupMapping>> GetLdapGroups()
    {
        var config = Plugin.Instance?.Configuration;
        return Ok(config?.LdapGroupMappings ?? new List<LdapGroupMapping>());
    }

    /// <summary>
    /// Adds an LDAP group mapping.
    /// </summary>
    [HttpPost("LdapGroups")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<LdapGroupMapping> AddLdapGroupMapping([FromBody] AddLdapGroupMappingRequest request)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, "Plugin not configured");
        }

        if (string.IsNullOrWhiteSpace(request.LdapGroupDn))
        {
            return BadRequest("LDAP group DN is required");
        }

        // Verify language alternative exists
        var alternative = config.LanguageAlternatives.FirstOrDefault(a => a.Id == request.LanguageAlternativeId);
        if (alternative == null)
        {
            return BadRequest("Language alternative not found");
        }

        var mapping = new LdapGroupMapping
        {
            Id = Guid.NewGuid(),
            LdapGroupDn = request.LdapGroupDn,
            LdapGroupName = request.LdapGroupName ?? request.LdapGroupDn,
            LanguageAlternativeId = request.LanguageAlternativeId,
            Priority = request.Priority
        };

        config.LdapGroupMappings.Add(mapping);
        Plugin.Instance?.SaveConfiguration();

        _logger.PolyglotInfo("Added LDAP group mapping: {0} -> {1}", mapping.LdapGroupDn, alternative.Name);

        return CreatedAtAction(nameof(GetLdapGroups), null, mapping);
    }

    /// <summary>
    /// Deletes an LDAP group mapping.
    /// </summary>
    [HttpDelete("LdapGroups/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult DeleteLdapGroupMapping(Guid id)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return NotFound();
        }

        var mapping = config.LdapGroupMappings.FirstOrDefault(m => m.Id == id);
        if (mapping == null)
        {
            return NotFound();
        }

        config.LdapGroupMappings.Remove(mapping);
        Plugin.Instance?.SaveConfiguration();

        _logger.PolyglotInfo("Deleted LDAP group mapping: {0}", mapping.LdapGroupDn);

        return NoContent();
    }

    /// <summary>
    /// Tests LDAP connection and optionally looks up a user.
    /// </summary>
    /// <param name="username">Optional username to test group lookup.</param>
    /// <param name="request">Optional request body (not used, but required for proper routing).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPost("TestLdap")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<LdapTestResult>> TestLdap(
        [FromQuery] string? username = null,
        [FromBody] TestLdapRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _ldapIntegrationService.TestConnectionAsync(username, cancellationToken);
        return Ok(result);
    }

    #endregion

    #region Settings

    /// <summary>
    /// Gets plugin settings.
    /// </summary>
    [HttpGet("Settings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PluginSettings> GetSettings()
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return NotFound();
        }

        return Ok(new PluginSettings
        {
            EnableLdapIntegration = config.EnableLdapIntegration
        });
    }

    /// <summary>
    /// Updates plugin settings.
    /// </summary>
    [HttpPut("Settings")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult UpdateSettings([FromBody] PluginSettings settings)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return NotFound();
        }

        config.EnableLdapIntegration = settings.EnableLdapIntegration;

        Plugin.Instance?.SaveConfiguration();

        return NoContent();
    }

    #endregion

    #region Debug

    /// <summary>
    /// Gets a debug report for troubleshooting.
    /// </summary>
    /// <param name="format">Output format: 'json' or 'markdown'. Default is 'markdown'.</param>
    /// <param name="includeFilePaths">Include actual file paths (default: false for privacy).</param>
    /// <param name="includeLibraryNames">Include actual library names (default: false for privacy).</param>
    /// <param name="includeUserNames">Include actual user names (default: false for privacy).</param>
    /// <param name="includeFilesystemDiagnostics">Include filesystem diagnostics like disk space (default: true).</param>
    /// <param name="includeHardlinkVerification">Include hardlink verification tests (default: true).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("DebugReport")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDebugReport(
        [FromQuery] string format = "markdown",
        [FromQuery] bool includeFilePaths = false,
        [FromQuery] bool includeLibraryNames = false,
        [FromQuery] bool includeUserNames = false,
        [FromQuery] bool includeFilesystemDiagnostics = true,
        [FromQuery] bool includeHardlinkVerification = true,
        CancellationToken cancellationToken = default)
    {
        var options = new DebugReportOptions
        {
            IncludeFilePaths = includeFilePaths,
            IncludeLibraryNames = includeLibraryNames,
            IncludeUserNames = includeUserNames,
            IncludeFilesystemDiagnostics = includeFilesystemDiagnostics,
            IncludeHardlinkVerification = includeHardlinkVerification
        };

        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            var report = await _debugReportService.GenerateReportAsync(options, cancellationToken);
            return Ok(report);
        }

        var markdown = await _debugReportService.GenerateMarkdownReportAsync(options, cancellationToken);
        return Ok(new DebugReportResponse { Markdown = markdown });
    }

    #endregion

    #region Helpers

    private static string GetLanguageFromCode(string code)
    {
        var dashIndex = code.IndexOf('-');
        return dashIndex > 0 ? code.Substring(0, dashIndex) : code;
    }

    private static string GetCountryFromCode(string code)
    {
        var dashIndex = code.IndexOf('-');
        return dashIndex > 0 ? code.Substring(dashIndex + 1) : string.Empty;
    }

    #endregion
}

#region Request/Response DTOs

/// <summary>
/// Request to create a language alternative.
/// </summary>
public class CreateAlternativeRequest
{
    /// <summary>
    /// Gets or sets the display name.
    /// </summary>
    [Required]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the language code (e.g., "pt-BR").
    /// </summary>
    [Required]
    public string LanguageCode { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the metadata language (defaults from language code).
    /// </summary>
    public string? MetadataLanguage { get; set; }

    /// <summary>
    /// Gets or sets the metadata country (defaults from language code).
    /// </summary>
    public string? MetadataCountry { get; set; }

    /// <summary>
    /// Gets or sets the destination base path.
    /// </summary>
    [Required]
    public string DestinationBasePath { get; set; } = string.Empty;
}

/// <summary>
/// Request to add a library mirror.
/// </summary>
public class AddLibraryMirrorRequest
{
    /// <summary>
    /// Gets or sets the source library ID (accepts GUID with or without dashes).
    /// </summary>
    [Required]
    public string SourceLibraryId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the target path.
    /// </summary>
    [Required]
    public string TargetPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the target library name (optional).
    /// </summary>
    public string? TargetLibraryName { get; set; }
}

/// <summary>
/// Request to set a user's language.
/// </summary>
public class SetUserLanguageRequest
{
    /// <summary>
    /// Gets or sets the language alternative ID (null = default language, shows source libraries).
    /// </summary>
    public Guid? AlternativeId { get; set; }

    /// <summary>
    /// Gets or sets whether this is a manual override.
    /// </summary>
    public bool ManuallySet { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the user should be disabled from plugin management.
    /// When true, the plugin will not manage this user's library access.
    /// </summary>
    public bool IsDisabled { get; set; }
}

/// <summary>
/// Result of enabling all users.
/// </summary>
public class EnableAllUsersResult
{
    /// <summary>
    /// Gets or sets the number of users that were enabled.
    /// </summary>
    public int UsersEnabled { get; set; }
}

/// <summary>
/// Request to add an LDAP group mapping.
/// </summary>
public class AddLdapGroupMappingRequest
{
    /// <summary>
    /// Gets or sets the LDAP group DN.
    /// </summary>
    [Required]
    public string LdapGroupDn { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the friendly group name.
    /// </summary>
    public string? LdapGroupName { get; set; }

    /// <summary>
    /// Gets or sets the language alternative ID.
    /// </summary>
    [Required]
    public Guid LanguageAlternativeId { get; set; }

    /// <summary>
    /// Gets or sets the priority.
    /// </summary>
    public int Priority { get; set; }
}

/// <summary>
/// Request to test LDAP.
/// </summary>
public class TestLdapRequest
{
    /// <summary>
    /// Gets or sets the username to test.
    /// </summary>
    public string? Username { get; set; }
}

/// <summary>
/// Plugin settings.
/// </summary>
public class PluginSettings
{
    /// <summary>
    /// Gets or sets whether LDAP integration is enabled.
    /// </summary>
    public bool EnableLdapIntegration { get; set; }
}

/// <summary>
/// Response containing the debug report in Markdown format.
/// </summary>
public class DebugReportResponse
{
    /// <summary>
    /// Gets or sets the Markdown-formatted debug report.
    /// </summary>
    public string Markdown { get; set; } = string.Empty;
}

#endregion


