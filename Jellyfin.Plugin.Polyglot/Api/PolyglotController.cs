using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Polyglot.Configuration;
using Jellyfin.Plugin.Polyglot.Helpers;
using Jellyfin.Plugin.Polyglot.Models;
using Jellyfin.Plugin.Polyglot.Services;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Polyglot.Api;

/// <summary>
/// REST API controller for the Polyglot plugin.
/// Uses IConfigurationService for all config modifications to prevent stale reference bugs.
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
    private readonly IConfigurationService _configService;
    private readonly ILocalizationManager _localizationManager;
    private readonly IServerConfigurationManager _serverConfigurationManager;
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
        IConfigurationService configService,
        ILocalizationManager localizationManager,
        IServerConfigurationManager serverConfigurationManager,
        ILogger<PolyglotController> logger)
    {
        _mirrorService = mirrorService;
        _userLanguageService = userLanguageService;
        _libraryAccessService = libraryAccessService;
        _ldapIntegrationService = ldapIntegrationService;
        _debugReportService = debugReportService;
        _configService = configService;
        _localizationManager = localizationManager;
        _serverConfigurationManager = serverConfigurationManager;
        _logger = logger;
    }

    #region UI Configuration

    /// <summary>
    /// Gets all configuration and data needed by the plugin UI in a single request.
    /// This endpoint returns libraries, alternatives, users, cultures, countries, LDAP status, and settings.
    /// </summary>
    [HttpGet("UIConfig")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<UIConfigResponse> GetUIConfig()
    {
        var config = _configService.GetConfiguration();
        if (config == null)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, "Plugin not configured");
        }

        var libraries = _mirrorService.GetJellyfinLibraries().ToList();
        var users = _userLanguageService.GetAllUsersWithLanguages().ToList();

        var cultures = _localizationManager.GetCultures()
            .OrderBy(c => c.DisplayName)
            .Select(c => new CultureInfo
            {
                DisplayName = c.DisplayName,
                Name = c.Name,
                TwoLetterISOLanguageName = c.TwoLetterISOLanguageName,
                ThreeLetterISOLanguageName = c.ThreeLetterISOLanguageName
            })
            .ToList();

        var countries = _localizationManager.GetCountries()
            .OrderBy(c => c.DisplayName)
            .Select(c => new CountryInfo
            {
                DisplayName = c.DisplayName,
                Name = c.Name,
                TwoLetterISORegionName = c.TwoLetterISORegionName,
                ThreeLetterISORegionName = c.ThreeLetterISORegionName
            })
            .ToList();

        var ldapStatus = _ldapIntegrationService.GetLdapStatus();
        var serverConfig = _serverConfigurationManager.Configuration;

        // Use thread-safe deep copies for collection access to prevent
        // race conditions during JSON serialization
        var alternatives = _configService.GetAlternatives().ToList();
        var ldapMappings = _configService.GetLdapGroupMappings().ToList();
        var excludedExtensions = _configService.GetExcludedExtensions().ToList();
        var excludedDirectories = _configService.GetExcludedDirectories().ToList();
        var defaultExcludedExtensions = _configService.GetDefaultExcludedExtensions().ToList();
        var defaultExcludedDirectories = _configService.GetDefaultExcludedDirectories().ToList();

        var response = new UIConfigResponse
        {
            Libraries = libraries,
            Alternatives = alternatives,
            Users = users,
            Cultures = cultures,
            Countries = countries,
            LdapStatus = ldapStatus,
            Settings = new UISettingsResponse
            {
                AutoManageNewUsers = config.AutoManageNewUsers,
                DefaultLanguageAlternativeId = config.DefaultLanguageAlternativeId,
                SyncMirrorsAfterLibraryScan = config.SyncMirrorsAfterLibraryScan,
                ExcludedExtensions = excludedExtensions,
                ExcludedDirectories = excludedDirectories,
                DefaultExcludedExtensions = defaultExcludedExtensions,
                DefaultExcludedDirectories = defaultExcludedDirectories,
                EnableLdapIntegration = config.EnableLdapIntegration,
                FallbackOnLdapFailure = config.FallbackOnLdapFailure,
                LdapGroupMappings = ldapMappings
            },
            ServerConfig = new ServerConfigInfo
            {
                PreferredMetadataLanguage = serverConfig.PreferredMetadataLanguage,
                MetadataCountryCode = serverConfig.MetadataCountryCode
            }
        };

        return Ok(response);
    }

    /// <summary>
    /// Updates plugin settings. Accepts partial updates - only provide fields you want to change.
    /// Returns the full UI configuration after applying the update.
    /// </summary>
    [HttpPost("UIConfig")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<UIConfigResponse> UpdateUIConfig([FromBody] UIConfigUpdateRequest request)
    {
        _logger.PolyglotDebug("UpdateUIConfig: Processing settings update request");

        if (request.Settings == null)
        {
            return GetUIConfig();
        }

        // Capture request settings for use in closure
        var settings = request.Settings;
        string? validationError = null;

        // Apply all settings atomically - validation happens inside the lock to prevent race conditions
        // Using Func overload to avoid saving configuration if validation fails
        var saved = _configService.UpdateSettings(config =>
        {
            // Validate DefaultLanguageAlternativeId inside the lock to prevent race conditions
            // where another thread could delete the alternative between validation and update
            if (settings.DefaultLanguageAlternativeIdProvided && settings.DefaultLanguageAlternativeId.HasValue)
            {
                var altExists = config.LanguageAlternatives.Any(a => a.Id == settings.DefaultLanguageAlternativeId.Value);
                if (!altExists)
                {
                    validationError = "Invalid DefaultLanguageAlternativeId: language alternative not found";
                    return false; // Abort without saving
                }
            }

            if (settings.AutoManageNewUsers.HasValue)
            {
                config.AutoManageNewUsers = settings.AutoManageNewUsers.Value;
            }

            if (settings.DefaultLanguageAlternativeIdProvided)
            {
                config.DefaultLanguageAlternativeId = settings.DefaultLanguageAlternativeId;
            }

            if (settings.SyncMirrorsAfterLibraryScan.HasValue)
            {
                config.SyncMirrorsAfterLibraryScan = settings.SyncMirrorsAfterLibraryScan.Value;
            }

            if (settings.ExcludedExtensions != null)
            {
                config.ExcludedExtensions = new HashSet<string>(settings.ExcludedExtensions, StringComparer.OrdinalIgnoreCase);
            }

            if (settings.ExcludedDirectories != null)
            {
                config.ExcludedDirectories = new HashSet<string>(settings.ExcludedDirectories, StringComparer.OrdinalIgnoreCase);
            }

            if (settings.EnableLdapIntegration.HasValue)
            {
                config.EnableLdapIntegration = settings.EnableLdapIntegration.Value;
            }

            if (settings.FallbackOnLdapFailure.HasValue)
            {
                config.FallbackOnLdapFailure = settings.FallbackOnLdapFailure.Value;
            }

            return true; // Save changes
        });

        // Handle all failure cases:
        // 1. Validation failed (validationError is set)
        // 2. Config was null (UpdateSettings returned false, validationError is null)
        if (!saved)
        {
            if (validationError != null)
            {
                return BadRequest(validationError);
            }

            // Config was null - this shouldn't happen in normal operation
            _logger.PolyglotError("UpdateUIConfig: Failed to update settings - configuration unavailable");
            return StatusCode(StatusCodes.Status500InternalServerError, "Plugin configuration is unavailable");
        }

        _logger.PolyglotInfo("UpdateUIConfig: Plugin settings updated");
        return GetUIConfig();
    }

    #endregion

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
        var alternatives = _configService.GetAlternatives();
        return Ok(alternatives);
    }

    /// <summary>
    /// Creates a new language alternative.
    /// </summary>
    [HttpPost("Alternatives")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<LanguageAlternative> CreateAlternative([FromBody] CreateAlternativeRequest request)
    {
        _logger.PolyglotDebug("CreateAlternative: Creating new alternative '{0}'", request.Name);

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

        if (!Path.IsPathRooted(request.DestinationBasePath))
        {
            return BadRequest("Destination base path must be an absolute path");
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

        // AddAlternative performs atomic duplicate name check inside the lock
        if (!_configService.AddAlternative(alternative))
        {
            return BadRequest($"Failed to create alternative: either configuration is unavailable or a language alternative with the name '{request.Name}' already exists");
        }

        _logger.PolyglotInfo("CreateAlternative: Created alternative {0} ({1})", alternative.Name, alternative.LanguageCode);

        return CreatedAtAction(nameof(GetAlternatives), new { id = alternative.Id }, alternative);
    }

    /// <summary>
    /// Deletes a language alternative.
    /// </summary>
    [HttpDelete("Alternatives/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> DeleteAlternative(
        Guid id,
        [FromQuery] bool deleteLibraries = false,
        [FromQuery] bool deleteFiles = false,
        CancellationToken cancellationToken = default)
    {
        _logger.PolyglotDebug("DeleteAlternative: Deleting alternative {0}", id);

        var alternative = _configService.GetAlternative(id);
        if (alternative == null)
        {
            return NotFound();
        }

        // Get mirror info before deletion - capture both IDs and names for better error reporting
        var mirrorInfo = alternative.MirroredLibraries
            .Select(m => new { m.Id, m.TargetLibraryName })
            .ToList();
        var initialMirrorIds = mirrorInfo.Select(m => m.Id).ToHashSet();

        // Track both successes and failures for accurate reporting
        // On partial failure, we need to tell users exactly what was deleted and what remains
        var deletedMirrors = new List<(Guid Id, string Name)>();
        var failedMirrors = new List<(Guid Id, string Name, string Error)>();

        // Delete mirrors using IDs - DeleteMirrorAsync handles config removal on success
        foreach (var mirror in mirrorInfo)
        {
            try
            {
                var deleteResult = await _mirrorService.DeleteMirrorAsync(mirror.Id, deleteLibraries, deleteFiles, forceConfigRemoval: false, cancellationToken);
                if (deleteResult.RemovedFromConfig)
                {
                    deletedMirrors.Add((mirror.Id, mirror.TargetLibraryName));
                }
                else
                {
                    // This shouldn't happen with forceConfigRemoval=false (it throws instead)
                    failedMirrors.Add((mirror.Id, mirror.TargetLibraryName, "Mirror was not removed from configuration"));
                }
            }
            catch (Exception ex)
            {
                _logger.PolyglotError(ex, "DeleteAlternative: Failed to delete mirror {0} ({1})", mirror.Id, mirror.TargetLibraryName);
                failedMirrors.Add((mirror.Id, mirror.TargetLibraryName, ex.Message));
            }
        }

        // Only remove the alternative if all mirrors were deleted successfully
        if (failedMirrors.Count > 0)
        {
            _logger.PolyglotWarning(
                "DeleteAlternative: {0} of {1} mirrors failed to delete, keeping alternative {2} in config",
                failedMirrors.Count, mirrorInfo.Count, id);

            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new
                {
                    Error = $"Failed to delete {failedMirrors.Count} of {mirrorInfo.Count} mirrors. " +
                            $"{deletedMirrors.Count} mirror(s) were already deleted successfully. " +
                            "The alternative remains in configuration with only the failed mirrors. " +
                            "Retry to delete the remaining mirrors, or manually clean up.",
                    DeletedMirrors = deletedMirrors.Select(m => new { m.Id, m.Name }),
                    FailedMirrors = failedMirrors.Select(m => new { m.Id, m.Name, m.Error })
                });
        }

        // Atomically check for new mirrors and remove the alternative
        // This prevents race conditions where AddLibraryMirror could add a mirror
        // between a non-atomic check and remove, leaving orphaned resources
        var removeResult = _configService.TryRemoveAlternativeAtomic(id, initialMirrorIds);

        if (!removeResult.Success)
        {
            switch (removeResult.FailureReason)
            {
                case RemoveAlternativeFailureReason.NewMirrorsAdded:
                    _logger.PolyglotWarning(
                        "DeleteAlternative: {0} new mirrors were added during deletion of alternative {1}. Aborting to prevent orphaned resources.",
                        removeResult.UnexpectedMirrorIds.Count, id);
                    return Conflict(new
                    {
                        Error = $"Cannot delete alternative: {removeResult.UnexpectedMirrorIds.Count} new mirror(s) were added during deletion. Please retry the operation.",
                        NewMirrorIds = removeResult.UnexpectedMirrorIds
                    });

                case RemoveAlternativeFailureReason.AlternativeNotFound:
                    // Alternative was already deleted (possibly by concurrent request) - that's fine
                    _logger.PolyglotDebug("DeleteAlternative: Alternative {0} was already removed", id);
                    break;

                case RemoveAlternativeFailureReason.ConfigurationUnavailable:
                    return StatusCode(StatusCodes.Status500InternalServerError, "Plugin configuration is unavailable");
            }
        }

        _logger.PolyglotInfo("DeleteAlternative: Deleted alternative {0}", id);

        return NoContent();
    }

    /// <summary>
    /// Adds a library mirror to a language alternative.
    /// This is a synchronous operation that waits for completion.
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
        _logger.PolyglotDebug("AddLibraryMirror: Adding mirror to alternative {0}", id);

        var alternative = _configService.GetAlternative(id);
        if (alternative == null)
        {
            return NotFound("Language alternative not found");
        }

        if (!Guid.TryParse(request.SourceLibraryId, out var sourceLibraryId))
        {
            return BadRequest("Invalid source library ID format");
        }

        var validation = _mirrorService.ValidateMirrorConfiguration(sourceLibraryId, request.TargetPath);
        if (!validation.IsValid)
        {
            return BadRequest(validation.ErrorMessage);
        }

        var sourceLibrary = _mirrorService.GetJellyfinLibraries()
            .FirstOrDefault(l => l.Id == sourceLibraryId);

        if (sourceLibrary == null)
        {
            return BadRequest("Source library not found");
        }

        if (sourceLibrary.IsMirror)
        {
            return BadRequest("Cannot create a mirror of a mirror library. Please select a source library.");
        }

        // Note: We don't re-fetch alternative here - the atomic duplicate check in AddMirror
        // handles race conditions. This avoids the TOCTOU bug where the check and add aren't atomic.

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

        // Add mirror to config atomically (includes duplicate source library check)
        if (!_configService.AddMirror(id, mirror))
        {
            return BadRequest($"Failed to add mirror: either the language alternative was deleted or a mirror for '{sourceLibrary.Name}' was created by another request");
        }

        _logger.PolyglotInfo("AddLibraryMirror: Creating mirror {0} for {1}", mirror.Id, sourceLibrary.Name);

        try
        {
            // Create mirror synchronously - no more Task.Run
            await _mirrorService.CreateMirrorAsync(id, mirror.Id, cancellationToken);

            // Update library access for users assigned to this language alternative
            var userLanguages = _configService.GetUserLanguages();
            var usersWithThisLanguage = userLanguages
                .Where(u => u.SelectedAlternativeId == id && u.IsPluginManaged)
                .Select(u => u.UserId)
                .ToList();

            foreach (var userId in usersWithThisLanguage)
            {
                try
                {
                    await _libraryAccessService.UpdateUserLibraryAccessAsync(userId, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.PolyglotWarning(ex, "AddLibraryMirror: Failed to update access for user {0}", userId);
                }
            }

            _logger.PolyglotInfo("AddLibraryMirror: Completed mirror creation, updated access for {0} users", usersWithThisLanguage.Count);

            // Return fresh mirror data with 201 Created
            // Always fetch from config service to ensure complete data (including TargetLibraryId set by CreateMirrorAsync)
            var updatedMirror = _configService.GetMirror(mirror.Id);
            if (updatedMirror == null)
            {
                // This should not happen in normal operation, but handle defensively
                _logger.PolyglotError("AddLibraryMirror: Mirror {0} not found after creation - configuration may be corrupted", mirror.Id);
                return StatusCode(StatusCodes.Status500InternalServerError,
                    new { Error = "Mirror was created but could not be retrieved from configuration. Please check the configuration." });
            }

            // Use sourceLibraryId in the URI to match the DELETE endpoint route parameter
            var resourceUri = $"Alternatives/{id}/Libraries/{sourceLibraryId}";
            return Created(resourceUri, updatedMirror);
        }
        catch (Exception ex)
        {
            _logger.PolyglotError(ex, "AddLibraryMirror: Failed to create mirror {0}", mirror.Id);

            // Clean up the orphaned config entry to prevent accumulation of failed mirrors
            // that users would otherwise need to manually remove.
            // Wrap in try-catch to ensure we always return the error response to the client,
            // even if cleanup fails (e.g., I/O error during SaveConfiguration).
            try
            {
                _configService.RemoveMirror(mirror.Id);
                _logger.PolyglotInfo("AddLibraryMirror: Removed failed mirror {0} from configuration", mirror.Id);
            }
            catch (Exception cleanupEx)
            {
                _logger.PolyglotWarning(cleanupEx,
                    "AddLibraryMirror: Failed to clean up config entry for mirror {0}. Manual cleanup may be required.",
                    mirror.Id);
            }

            return StatusCode(StatusCodes.Status500InternalServerError, new { Error = ex.Message });
        }
    }

    /// <summary>
    /// Triggers sync for all mirrors in a language alternative.
    /// This is a synchronous operation that waits for completion.
    /// </summary>
    [HttpPost("Alternatives/{id:guid}/Sync")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SyncAlternative(Guid id, CancellationToken cancellationToken = default)
    {
        _logger.PolyglotDebug("SyncAlternative: Starting sync for alternative {0}", id);

        var alternative = _configService.GetAlternative(id);
        if (alternative == null)
        {
            return NotFound();
        }

        try
        {
            var result = await _mirrorService.SyncAllMirrorsAsync(id, null, cancellationToken);

            _logger.PolyglotInfo("SyncAlternative: Completed sync for alternative {0} - status: {1}", id, result.Status);

            if (result.Status == SyncAllStatus.AlternativeNotFound)
            {
                return NotFound("Language alternative was deleted during sync");
            }

            if (result.Status == SyncAllStatus.CompletedWithErrors)
            {
                return Ok(new
                {
                    Message = "Sync completed with some errors",
                    MirrorsSynced = result.MirrorsSynced,
                    MirrorsFailed = result.MirrorsFailed,
                    TotalMirrors = result.TotalMirrors
                });
            }

            if (result.Status == SyncAllStatus.Cancelled)
            {
                return Ok(new
                {
                    Message = "Sync was cancelled",
                    MirrorsSynced = result.MirrorsSynced,
                    TotalMirrors = result.TotalMirrors
                });
            }

            // SyncAllStatus.Completed or any other status
            return Ok(new
            {
                Message = "Sync completed successfully",
                MirrorsSynced = result.MirrorsSynced,
                TotalMirrors = result.TotalMirrors
            });
        }
        catch (Exception ex)
        {
            _logger.PolyglotError(ex, "SyncAlternative: Sync failed for alternative {0}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { Error = ex.Message });
        }
    }

    /// <summary>
    /// Deletes a library mirror from a language alternative.
    /// </summary>
    /// <param name="id">The language alternative ID.</param>
    /// <param name="sourceLibraryId">The source library ID of the mirror to delete.</param>
    /// <param name="deleteLibrary">Whether to delete the Jellyfin library.</param>
    /// <param name="deleteFiles">Whether to delete the mirror files.</param>
    /// <param name="force">Force removal from config even if library/file deletion fails.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpDelete("Alternatives/{id:guid}/Libraries/{sourceLibraryId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteLibraryMirror(
        Guid id,
        Guid sourceLibraryId,
        [FromQuery] bool deleteLibrary = false,
        [FromQuery] bool deleteFiles = false,
        [FromQuery] bool force = false,
        CancellationToken cancellationToken = default)
    {
        _logger.PolyglotDebug("DeleteLibraryMirror: Deleting mirror for source {0} from alternative {1} (force: {2})", sourceLibraryId, id, force);

        var alternative = _configService.GetAlternative(id);
        if (alternative == null)
        {
            return NotFound("Language alternative not found");
        }

        var mirror = alternative.MirroredLibraries.FirstOrDefault(m => m.SourceLibraryId == sourceLibraryId);
        if (mirror == null)
        {
            return NotFound("Mirror not found");
        }

        var mirrorId = mirror.Id;
        var mirrorName = mirror.TargetLibraryName;

        DeleteMirrorResult deleteResult;
        try
        {
            deleteResult = await _mirrorService.DeleteMirrorAsync(mirrorId, deleteLibrary, deleteFiles, forceConfigRemoval: force, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.PolyglotError(ex, "DeleteLibraryMirror: Failed to delete mirror {0}", mirrorId);
            return BadRequest($"Failed to delete mirror: {ex.Message}. Use force=true to remove from config anyway.");
        }

        // If force was used and there were errors, return them as warnings
        if (deleteResult.HasErrors)
        {
            _logger.PolyglotWarning("DeleteLibraryMirror: Mirror {0} removed with errors: {1} {2}",
                mirrorId, deleteResult.LibraryDeletionError, deleteResult.FileDeletionError);
        }

        // Update library access for users assigned to this language alternative
        var userLanguages = _configService.GetUserLanguages();
        var usersWithThisLanguage = userLanguages
            .Where(u => u.SelectedAlternativeId == id && u.IsPluginManaged)
            .Select(u => u.UserId)
            .ToList();

        foreach (var userId in usersWithThisLanguage)
        {
            try
            {
                await _libraryAccessService.UpdateUserLibraryAccessAsync(userId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.PolyglotWarning(ex, "DeleteLibraryMirror: Failed to update access for user {0}", userId);
            }
        }

        _logger.PolyglotInfo("DeleteLibraryMirror: Deleted mirror {0}, updated access for {1} users", mirrorName, usersWithThisLanguage.Count);

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
                isPluginManaged: true,
                cancellationToken);

            return NoContent();
        }
        catch (ArgumentException ex)
        {
            if (ex.ParamName == "userId")
            {
                return NotFound(ex.Message);
            }

            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.PolyglotError(ex, "SetUserLanguage: Failed for user {0}", userId);
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
        var mappings = _configService.GetLdapGroupMappings();
        return Ok(mappings);
    }

    /// <summary>
    /// Adds an LDAP group mapping.
    /// </summary>
    [HttpPost("LdapGroups")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<LdapGroupMapping> AddLdapGroupMapping([FromBody] AddLdapGroupMappingRequest request)
    {
        _logger.PolyglotDebug("AddLdapGroupMapping: Adding mapping for {0}", request.LdapGroupDn);

        if (string.IsNullOrWhiteSpace(request.LdapGroupDn))
        {
            return BadRequest("LDAP group DN is required");
        }

        var alternative = _configService.GetAlternative(request.LanguageAlternativeId);
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

        // AddLdapGroupMapping performs atomic duplicate check inside the lock
        if (!_configService.AddLdapGroupMapping(mapping))
        {
            return BadRequest($"Failed to add LDAP mapping: either configuration is unavailable or a mapping for '{request.LdapGroupDn}' already exists");
        }

        _logger.PolyglotInfo("AddLdapGroupMapping: Added mapping {0} -> {1}", mapping.LdapGroupDn, alternative.Name);

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
        _logger.PolyglotDebug("DeleteLdapGroupMapping: Deleting mapping {0}", id);

        if (!_configService.RemoveLdapGroupMapping(id))
        {
            return NotFound();
        }

        _logger.PolyglotInfo("DeleteLdapGroupMapping: Deleted mapping {0}", id);

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
        var config = _configService.GetConfiguration();
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
        _logger.PolyglotDebug("UpdateSettings: Updating LDAP integration to {0}", settings.EnableLdapIntegration);

        _configService.UpdateSettings(config =>
        {
            config.EnableLdapIntegration = settings.EnableLdapIntegration;
        });

        _logger.PolyglotInfo("UpdateSettings: Settings updated");

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

/// <summary>
/// Complete UI configuration response containing all data needed by the plugin frontend.
/// </summary>
public class UIConfigResponse
{
    /// <summary>
    /// Gets or sets the list of Jellyfin libraries.
    /// </summary>
    public List<LibraryInfo> Libraries { get; set; } = new();

    /// <summary>
    /// Gets or sets the list of language alternatives.
    /// </summary>
    public List<LanguageAlternative> Alternatives { get; set; } = new();

    /// <summary>
    /// Gets or sets the list of users with their language assignments.
    /// </summary>
    public List<UserInfo> Users { get; set; } = new();

    /// <summary>
    /// Gets or sets the available cultures/languages.
    /// </summary>
    public List<CultureInfo> Cultures { get; set; } = new();

    /// <summary>
    /// Gets or sets the available countries.
    /// </summary>
    public List<CountryInfo> Countries { get; set; } = new();

    /// <summary>
    /// Gets or sets the LDAP integration status.
    /// </summary>
    public LdapStatus LdapStatus { get; set; } = new();

    /// <summary>
    /// Gets or sets the plugin settings.
    /// </summary>
    public UISettingsResponse Settings { get; set; } = new();

    /// <summary>
    /// Gets or sets relevant server configuration.
    /// </summary>
    public ServerConfigInfo ServerConfig { get; set; } = new();
}

/// <summary>
/// Plugin settings as returned by the UI config endpoint.
/// </summary>
public class UISettingsResponse
{
    /// <summary>
    /// Gets or sets a value indicating whether new users are automatically managed.
    /// </summary>
    public bool AutoManageNewUsers { get; set; }

    /// <summary>
    /// Gets or sets the default language alternative ID for new users.
    /// </summary>
    public Guid? DefaultLanguageAlternativeId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether mirrors sync after library scans.
    /// </summary>
    public bool SyncMirrorsAfterLibraryScan { get; set; }

    /// <summary>
    /// Gets or sets the excluded file extensions.
    /// </summary>
    public List<string> ExcludedExtensions { get; set; } = new();

    /// <summary>
    /// Gets or sets the excluded directory names.
    /// </summary>
    public List<string> ExcludedDirectories { get; set; } = new();

    /// <summary>
    /// Gets or sets the default excluded extensions (read-only).
    /// </summary>
    public List<string> DefaultExcludedExtensions { get; set; } = new();

    /// <summary>
    /// Gets or sets the default excluded directories (read-only).
    /// </summary>
    public List<string> DefaultExcludedDirectories { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether LDAP integration is enabled.
    /// </summary>
    public bool EnableLdapIntegration { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to fall back to auto-assignment when LDAP lookup fails.
    /// When true (default), users are assigned the default language if LDAP lookup fails.
    /// When false, users remain unassigned if LDAP lookup fails, requiring manual assignment.
    /// </summary>
    public bool FallbackOnLdapFailure { get; set; }

    /// <summary>
    /// Gets or sets the LDAP group mappings.
    /// </summary>
    public List<LdapGroupMapping> LdapGroupMappings { get; set; } = new();
}

/// <summary>
/// Request to update UI configuration settings.
/// </summary>
public class UIConfigUpdateRequest
{
    /// <summary>
    /// Gets or sets the settings to update. Only provided fields will be updated.
    /// </summary>
    public UISettingsUpdateRequest? Settings { get; set; }
}

/// <summary>
/// Settings fields to update. All fields are optional - only provided fields will be updated.
/// </summary>
public class UISettingsUpdateRequest
{
    /// <summary>
    /// Gets or sets whether new users are automatically managed.
    /// </summary>
    public bool? AutoManageNewUsers { get; set; }

    /// <summary>
    /// Gets or sets the default language alternative ID for new users.
    /// Set to null to use "Default libraries" (source only).
    /// </summary>
    public Guid? DefaultLanguageAlternativeId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether DefaultLanguageAlternativeId was explicitly provided.
    /// This allows distinguishing between "not provided" and "explicitly set to null".
    /// </summary>
    public bool DefaultLanguageAlternativeIdProvided { get; set; }

    /// <summary>
    /// Gets or sets whether mirrors sync after library scans.
    /// </summary>
    public bool? SyncMirrorsAfterLibraryScan { get; set; }

    /// <summary>
    /// Gets or sets the excluded file extensions (replaces existing list).
    /// </summary>
    public List<string>? ExcludedExtensions { get; set; }

    /// <summary>
    /// Gets or sets the excluded directory names (replaces existing list).
    /// </summary>
    public List<string>? ExcludedDirectories { get; set; }

    /// <summary>
    /// Gets or sets whether LDAP integration is enabled.
    /// </summary>
    public bool? EnableLdapIntegration { get; set; }

    /// <summary>
    /// Gets or sets whether to fall back to auto-assignment when LDAP lookup fails.
    /// When true (default), users are assigned the default language if LDAP lookup fails.
    /// When false, users remain unassigned if LDAP lookup fails, requiring manual assignment.
    /// </summary>
    public bool? FallbackOnLdapFailure { get; set; }
}

/// <summary>
/// Culture/language information for UI dropdowns.
/// </summary>
public class CultureInfo
{
    /// <summary>
    /// Gets or sets the display name.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the two-letter ISO language name.
    /// </summary>
    public string TwoLetterISOLanguageName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the three-letter ISO language name.
    /// </summary>
    public string ThreeLetterISOLanguageName { get; set; } = string.Empty;
}

/// <summary>
/// Country information for UI dropdowns.
/// </summary>
public class CountryInfo
{
    /// <summary>
    /// Gets or sets the display name.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the two-letter ISO region name.
    /// </summary>
    public string TwoLetterISORegionName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the three-letter ISO region name.
    /// </summary>
    public string ThreeLetterISORegionName { get; set; } = string.Empty;
}

/// <summary>
/// Server configuration information relevant to the plugin.
/// </summary>
public class ServerConfigInfo
{
    /// <summary>
    /// Gets or sets the server's preferred metadata language.
    /// </summary>
    public string? PreferredMetadataLanguage { get; set; }

    /// <summary>
    /// Gets or sets the server's metadata country code.
    /// </summary>
    public string? MetadataCountryCode { get; set; }
}

#endregion
