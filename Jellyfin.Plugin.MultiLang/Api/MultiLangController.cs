using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MultiLang.Models;
using Jellyfin.Plugin.MultiLang.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MultiLang.Api;

/// <summary>
/// REST API controller for the Multi-Language plugin.
/// </summary>
[ApiController]
[Route("MultiLang")]
[Authorize(Policy = "RequiresElevation")]
[Produces(MediaTypeNames.Application.Json)]
public class MultiLangController : ControllerBase
{
    private readonly IMirrorService _mirrorService;
    private readonly IUserLanguageService _userLanguageService;
    private readonly ILibraryAccessService _libraryAccessService;
    private readonly ILdapIntegrationService _ldapIntegrationService;
    private readonly ILogger<MultiLangController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MultiLangController"/> class.
    /// </summary>
    public MultiLangController(
        IMirrorService mirrorService,
        IUserLanguageService userLanguageService,
        ILibraryAccessService libraryAccessService,
        ILdapIntegrationService ldapIntegrationService,
        ILogger<MultiLangController> logger)
    {
        _mirrorService = mirrorService;
        _userLanguageService = userLanguageService;
        _libraryAccessService = libraryAccessService;
        _ldapIntegrationService = ldapIntegrationService;
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

        _logger.LogInformation("Created language alternative: {Name} ({LanguageCode})", alternative.Name, alternative.LanguageCode);

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
                _logger.LogError(ex, "Failed to delete mirror {MirrorId}", mirror.Id);
            }
        }

        config.LanguageAlternatives.Remove(alternative);
        Plugin.Instance?.SaveConfiguration();

        _logger.LogInformation("Deleted language alternative: {Name}", alternative.Name);

        return NoContent();
    }

    /// <summary>
    /// Adds a library mirror to a language alternative.
    /// </summary>
    [HttpPost("Alternatives/{id:guid}/Libraries")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LibraryMirror>> AddLibraryMirror(
        Guid id,
        [FromBody] AddLibraryMirrorRequest request,
        CancellationToken cancellationToken = default)
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

        // Create the mirror
        try
        {
            await _mirrorService.CreateMirrorAsync(alternative, mirror, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create mirror for {SourceLibrary}", sourceLibrary.Name);
            // Don't return error - mirror was added but sync can be retried
        }

        return CreatedAtAction(nameof(GetAlternatives), new { id = alternative.Id }, mirror);
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
                _logger.LogError(ex, "Background sync failed for alternative {Name}", alternative.Name);
            }
        }, cancellationToken);

        return Accepted();
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
            return NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set language for user {UserId}", userId);
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

        _logger.LogInformation("Added LDAP group mapping: {GroupDn} -> {LanguageName}", mapping.LdapGroupDn, alternative.Name);

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

        _logger.LogInformation("Deleted LDAP group mapping: {GroupDn}", mapping.LdapGroupDn);

        return NoContent();
    }

    /// <summary>
    /// Tests LDAP connection and optionally looks up a user.
    /// </summary>
    /// <param name="username">Optional username to test group lookup.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPost("TestLdap")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<LdapTestResult>> TestLdap(
        [FromQuery] string? username = null,
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
            SyncUserDisplayLanguage = config.SyncUserDisplayLanguage,
            SyncUserSubtitleLanguage = config.SyncUserSubtitleLanguage,
            SyncUserAudioLanguage = config.SyncUserAudioLanguage,
            EnableLdapIntegration = config.EnableLdapIntegration,
            MirrorSyncIntervalHours = config.MirrorSyncIntervalHours
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

        config.SyncUserDisplayLanguage = settings.SyncUserDisplayLanguage;
        config.SyncUserSubtitleLanguage = settings.SyncUserSubtitleLanguage;
        config.SyncUserAudioLanguage = settings.SyncUserAudioLanguage;
        config.EnableLdapIntegration = settings.EnableLdapIntegration;
        config.MirrorSyncIntervalHours = settings.MirrorSyncIntervalHours;

        Plugin.Instance?.SaveConfiguration();

        return NoContent();
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
    /// Gets or sets whether to sync display language.
    /// </summary>
    public bool SyncUserDisplayLanguage { get; set; }

    /// <summary>
    /// Gets or sets whether to sync subtitle language.
    /// </summary>
    public bool SyncUserSubtitleLanguage { get; set; }

    /// <summary>
    /// Gets or sets whether to sync audio language.
    /// </summary>
    public bool SyncUserAudioLanguage { get; set; }

    /// <summary>
    /// Gets or sets whether LDAP integration is enabled.
    /// </summary>
    public bool EnableLdapIntegration { get; set; }

    /// <summary>
    /// Gets or sets the mirror sync interval in hours.
    /// Note: Mirrors are also synced automatically after library scans.
    /// </summary>
    public int MirrorSyncIntervalHours { get; set; }
}

#endregion

