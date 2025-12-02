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
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

// Aliases for log entity types
using LogAlternativeEntity = Jellyfin.Plugin.Polyglot.Models.LogAlternative;
using LogMirrorEntity = Jellyfin.Plugin.Polyglot.Models.LogMirror;
using LogUserEntity = Jellyfin.Plugin.Polyglot.Models.LogUser;

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
    private readonly IDebugReportService _debugReportService;
    private readonly IConfigurationService _configService;
    private readonly PolyglotUserManager _userManager;
    // Stored as dynamic to avoid compile-time binding to specific method signatures
    // that may change between Jellyfin versions (e.g., GetCultures, GetCountries)
    private readonly dynamic _localizationManager;
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly ILogger<PolyglotController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PolyglotController"/> class.
    /// </summary>
    public PolyglotController(
        IMirrorService mirrorService,
        IUserLanguageService userLanguageService,
        ILibraryAccessService libraryAccessService,
        IDebugReportService debugReportService,
        IConfigurationService configService,
        IUserManager userManager,
        ILocalizationManager localizationManager,
        IServerConfigurationManager serverConfigurationManager,
        ILogger<PolyglotController> logger)
    {
        _mirrorService = mirrorService;
        _userLanguageService = userLanguageService;
        _libraryAccessService = libraryAccessService;
        _debugReportService = debugReportService;
        _configService = configService;
        _userManager = userManager.ToPolyglot();
        _localizationManager = localizationManager;
        _serverConfigurationManager = serverConfigurationManager;
        _logger = logger;
    }

    #region UI Configuration

    /// <summary>
    /// Gets all configuration and data needed by the plugin UI in a single request.
    /// This endpoint returns libraries, alternatives, users, cultures, countries, and settings.
    /// </summary>
    [HttpGet("UIConfig")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<UIConfigResponse> GetUIConfig()
    {
        var libraries = _mirrorService.GetJellyfinLibraries().ToList();
        var users = _userLanguageService.GetAllUsersWithLanguages().ToList();

        // Use dynamic to avoid compile-time binding issues with API changes between Jellyfin versions
        var cultures = GetCulturesCompat();
        var countries = GetCountriesCompat();

        var serverConfig = _serverConfigurationManager.Configuration;

        // Get all config data in one atomic read
        var configData = _configService.Read(c => new
        {
            Alternatives = c.LanguageAlternatives.ToList(),
            ExcludedExtensions = c.ExcludedExtensions.ToList(),
            ExcludedDirectories = c.ExcludedDirectories.ToList(),
            IncludedDirectories = c.IncludedDirectories.ToList(),
            c.AutoManageNewUsers,
            c.DefaultLanguageAlternativeId,
            c.SyncMirrorsAfterLibraryScan
        });

        var response = new UIConfigResponse
        {
            Libraries = libraries,
            Alternatives = configData.Alternatives,
            Users = users,
            Cultures = cultures,
            Countries = countries,
            Settings = new UISettingsResponse
            {
                AutoManageNewUsers = configData.AutoManageNewUsers,
                DefaultLanguageAlternativeId = configData.DefaultLanguageAlternativeId,
                SyncMirrorsAfterLibraryScan = configData.SyncMirrorsAfterLibraryScan,
                ExcludedExtensions = configData.ExcludedExtensions,
                ExcludedDirectories = configData.ExcludedDirectories,
                IncludedDirectories = configData.IncludedDirectories,
                DefaultExcludedExtensions = PluginConfiguration.DefaultExcludedExtensions.ToList(),
                DefaultExcludedDirectories = PluginConfiguration.DefaultExcludedDirectories.ToList(),
                DefaultIncludedDirectories = PluginConfiguration.DefaultIncludedDirectories.ToList()
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

        var settings = request.Settings;
        string? validationError = null;

        var saved = _configService.Update(config =>
        {
            if (settings.DefaultLanguageAlternativeIdProvided && settings.DefaultLanguageAlternativeId.HasValue)
            {
                var altExists = config.LanguageAlternatives.Any(a => a.Id == settings.DefaultLanguageAlternativeId.Value);
                if (!altExists)
                {
                    validationError = "Invalid DefaultLanguageAlternativeId: language alternative not found";
                    return false;
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

            if (settings.IncludedDirectories != null)
            {
                config.IncludedDirectories = new HashSet<string>(settings.IncludedDirectories, StringComparer.OrdinalIgnoreCase);
            }

            return true;
        });

        if (!saved)
        {
            if (validationError != null)
            {
                return BadRequest(validationError);
            }

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
        var alternatives = _configService.Read(c => c.LanguageAlternatives.ToList());
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
        _logger.PolyglotDebug("CreateAlternative: Creating new alternative");

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

        var added = _configService.Update(c =>
        {
            if (c.LanguageAlternatives.Any(a => string.Equals(a.Name, alternative.Name, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            c.LanguageAlternatives.Add(alternative);
            return true;
        });

        if (!added)
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
        _logger.PolyglotDebug("DeleteAlternative: Deleting alternative {0}",
            _configService.CreateLogAlternative(id));

        var alternative = _configService.Read(c => c.LanguageAlternatives.FirstOrDefault(a => a.Id == id));
        if (alternative == null)
        {
            return NotFound();
        }

        var mirrorInfo = alternative.MirroredLibraries
            .Select(m => new { m.Id, m.TargetLibraryName })
            .ToList();
        var initialMirrorIds = mirrorInfo.Select(m => m.Id).ToHashSet();

        var deletedMirrors = new List<(Guid Id, string Name)>();
        var failedMirrors = new List<(Guid Id, string Name, string Error)>();

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
                    failedMirrors.Add((mirror.Id, mirror.TargetLibraryName, "Mirror was not removed from configuration"));
                }
            }
            catch (Exception ex)
            {
                _logger.PolyglotError(ex, "DeleteAlternative: Failed to delete mirror {0}",
                    _configService.CreateLogMirror(mirror.Id));
                failedMirrors.Add((mirror.Id, mirror.TargetLibraryName, ex.Message));
            }
        }

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

        // Remove alternative atomically, checking for new mirrors added during deletion
        var removeResult = _configService.Update(c =>
        {
            var alt = c.LanguageAlternatives.FirstOrDefault(a => a.Id == id);
            if (alt == null)
            {
                return true; // Already gone
            }

            var currentMirrorIds = alt.MirroredLibraries.Select(m => m.Id).ToHashSet();
            var newMirrors = currentMirrorIds.Except(initialMirrorIds).ToList();
            if (newMirrors.Count > 0)
            {
                return false; // New mirrors were added during deletion
            }

            c.LanguageAlternatives.Remove(alt);

            // Clear default if it was this alternative
            if (c.DefaultLanguageAlternativeId == id)
            {
                c.DefaultLanguageAlternativeId = null;
            }

            return true;
        });

        if (!removeResult)
        {
            _logger.PolyglotWarning(
                "DeleteAlternative: New mirrors were added during deletion of alternative {0}. Aborting.",
                id);
            return Conflict(new
            {
                Error = "Cannot delete alternative: new mirror(s) were added during deletion. Please retry the operation."
            });
        }

        _logger.PolyglotInfo("DeleteAlternative: Deleted alternative {0}",
            _configService.CreateLogAlternative(id, alternative?.Name, alternative?.LanguageCode));

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
        _logger.PolyglotDebug("AddLibraryMirror: Adding mirror to alternative {0}",
            _configService.CreateLogAlternative(id));

        var alternative = _configService.Read(c => c.LanguageAlternatives.FirstOrDefault(a => a.Id == id));
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

        // Add mirror atomically with duplicate check
        var added = _configService.Update(c =>
        {
            var alt = c.LanguageAlternatives.FirstOrDefault(a => a.Id == id);
            if (alt == null)
            {
                return false;
            }

            if (alt.MirroredLibraries.Any(m => m.SourceLibraryId == sourceLibraryId))
            {
                return false;
            }

            alt.MirroredLibraries.Add(mirror);
            return true;
        });

        if (!added)
        {
            return BadRequest($"Failed to add mirror: either the language alternative was deleted or a mirror for '{sourceLibrary.Name}' was created by another request");
        }

        var sourceLibraryEntity = new Models.LogLibrary(sourceLibrary.Id, sourceLibrary.Name);
        _logger.PolyglotInfo("AddLibraryMirror: Creating mirror for {0}", sourceLibraryEntity);

        try
        {
            await _mirrorService.CreateMirrorAsync(id, mirror.Id, cancellationToken);

            // Update library access for users assigned to this language alternative
            var usersWithThisLanguage = _configService.Read(c =>
                c.UserLanguages
                    .Where(u => u.SelectedAlternativeId == id && u.IsPluginManaged)
                    .Select(u => u.UserId)
                    .ToList());

            foreach (var userId in usersWithThisLanguage)
            {
                try
                {
                    await _libraryAccessService.UpdateUserLibraryAccessAsync(userId, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.PolyglotWarning(ex, "AddLibraryMirror: Failed to update access for user {0}",
                        _userManager.CreateLogUser(userId));
                }
            }

            _logger.PolyglotInfo("AddLibraryMirror: Completed mirror creation, updated access for {0} users", usersWithThisLanguage.Count);

            // Return fresh mirror data
            var updatedMirror = _configService.Read(c => c.LanguageAlternatives
                .SelectMany(a => a.MirroredLibraries)
                .FirstOrDefault(m => m.Id == mirror.Id));

            if (updatedMirror == null)
            {
                _logger.PolyglotError("AddLibraryMirror: Mirror {0} not found after creation - configuration may be corrupted",
                    new LogMirrorEntity(mirror.Id, sourceLibrary.Name, mirror.TargetLibraryName));
                return StatusCode(StatusCodes.Status500InternalServerError,
                    new { Error = "Mirror was created but could not be retrieved from configuration. Please check the configuration." });
            }

            var resourceUri = $"Alternatives/{id}/Libraries/{sourceLibraryId}";
            return Created(resourceUri, updatedMirror);
        }
        catch (Exception ex)
        {
            var mirrorEntity = new LogMirrorEntity(mirror.Id, sourceLibrary.Name, mirror.TargetLibraryName);
            _logger.PolyglotError(ex, "AddLibraryMirror: Failed to create mirror {0}", mirrorEntity);

            try
            {
                _configService.Update(c =>
                {
                    foreach (var alt in c.LanguageAlternatives)
                    {
                        var m = alt.MirroredLibraries.FirstOrDefault(x => x.Id == mirror.Id);
                        if (m != null)
                        {
                            alt.MirroredLibraries.Remove(m);
                            break;
                        }
                    }
                });
                _logger.PolyglotInfo("AddLibraryMirror: Removed failed mirror {0} from configuration", mirrorEntity);
            }
            catch (Exception cleanupEx)
            {
                _logger.PolyglotWarning(cleanupEx,
                    "AddLibraryMirror: Failed to clean up config entry for mirror {0}. Manual cleanup may be required.",
                    mirrorEntity);
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
        _logger.PolyglotDebug("SyncAlternative: Starting sync for alternative {0}",
            _configService.CreateLogAlternative(id));

        var alternative = _configService.Read(c => c.LanguageAlternatives.FirstOrDefault(a => a.Id == id));
        if (alternative == null)
        {
            return NotFound();
        }

        var alternativeEntity = new LogAlternativeEntity(id, alternative.Name, alternative.LanguageCode);

        try
        {
            var result = await _mirrorService.SyncAllMirrorsAsync(id, null, cancellationToken);

            _logger.PolyglotInfo("SyncAlternative: Completed sync for alternative {0} - status: {1}", alternativeEntity, result.Status);

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

            return Ok(new
            {
                Message = "Sync completed successfully",
                MirrorsSynced = result.MirrorsSynced,
                TotalMirrors = result.TotalMirrors
            });
        }
        catch (Exception ex)
        {
            _logger.PolyglotError(ex, "SyncAlternative: Sync failed for alternative {0}", alternativeEntity);
            return StatusCode(StatusCodes.Status500InternalServerError, new { Error = ex.Message });
        }
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
        [FromQuery] bool force = false,
        CancellationToken cancellationToken = default)
    {
        _logger.PolyglotDebug("DeleteLibraryMirror: Deleting mirror from alternative {0} (force: {1})",
            _configService.CreateLogAlternative(id),
            force);

        var mirrorData = _configService.Read(c =>
        {
            var alt = c.LanguageAlternatives.FirstOrDefault(a => a.Id == id);
            if (alt == null) return null;
            var m = alt.MirroredLibraries.FirstOrDefault(x => x.SourceLibraryId == sourceLibraryId);
            return m != null ? new { Mirror = m, AltExists = true } : null;
        });

        if (mirrorData == null)
        {
            return NotFound("Language alternative or mirror not found");
        }

        var mirror = mirrorData.Mirror;
        var mirrorId = mirror.Id;
        var mirrorName = mirror.TargetLibraryName;
        var mirrorEntity = new LogMirrorEntity(mirrorId, mirror.SourceLibraryName, mirrorName);

        DeleteMirrorResult deleteResult;
        try
        {
            deleteResult = await _mirrorService.DeleteMirrorAsync(mirrorId, deleteLibrary, deleteFiles, forceConfigRemoval: force, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.PolyglotError(ex, "DeleteLibraryMirror: Failed to delete mirror {0}", mirrorEntity);
            return BadRequest($"Failed to delete mirror: {ex.Message}. Use force=true to remove from config anyway.");
        }

        if (deleteResult.HasErrors)
        {
            _logger.PolyglotWarning("DeleteLibraryMirror: Mirror {0} removed with errors: {1} {2}",
                mirrorEntity, deleteResult.LibraryDeletionError, deleteResult.FileDeletionError);
        }

        // Update library access for users assigned to this language alternative
        var usersWithThisLanguage = _configService.Read(c =>
            c.UserLanguages
                .Where(u => u.SelectedAlternativeId == id && u.IsPluginManaged)
                .Select(u => u.UserId)
                .ToList());

        foreach (var userId in usersWithThisLanguage)
        {
            try
            {
                await _libraryAccessService.UpdateUserLibraryAccessAsync(userId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.PolyglotWarning(ex, "DeleteLibraryMirror: Failed to update access for user {0}",
                    _userManager.CreateLogUser(userId));
            }
        }

        _logger.PolyglotInfo("DeleteLibraryMirror: Deleted mirror {0}, updated access for {1} users", mirrorEntity, usersWithThisLanguage.Count);

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
            _logger.PolyglotError(ex, "SetUserLanguage: Failed for user {0}",
                _userManager.CreateLogUser(userId));
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

    #region Debug

    /// <summary>
    /// Gets a debug report for troubleshooting.
    /// </summary>
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

    /// <summary>
    /// Gets cultures from ILocalizationManager using dynamic dispatch to handle API changes.
    /// </summary>
    private List<CultureInfo> GetCulturesCompat()
    {
        var result = new List<CultureInfo>();
        try
        {
            // Use dynamic to handle different return types across Jellyfin versions
            foreach (dynamic culture in _localizationManager.GetCultures())
            {
                result.Add(new CultureInfo
                {
                    DisplayName = (string)culture.DisplayName,
                    Name = (string)culture.Name,
                    TwoLetterISOLanguageName = (string)culture.TwoLetterISOLanguageName,
                    ThreeLetterISOLanguageName = (string)culture.ThreeLetterISOLanguageName
                });
            }

            result.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get cultures from localization manager");
        }

        return result;
    }

    /// <summary>
    /// Gets countries from ILocalizationManager using dynamic dispatch to handle API changes.
    /// </summary>
    private List<CountryInfo> GetCountriesCompat()
    {
        var result = new List<CountryInfo>();
        try
        {
            // Use dynamic to handle different return types across Jellyfin versions
            foreach (dynamic country in _localizationManager.GetCountries())
            {
                result.Add(new CountryInfo
                {
                    DisplayName = (string)country.DisplayName,
                    Name = (string)country.Name,
                    TwoLetterISORegionName = (string)country.TwoLetterISORegionName,
                    ThreeLetterISORegionName = (string)country.ThreeLetterISORegionName
                });
            }

            result.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get countries from localization manager");
        }

        return result;
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
    /// Gets or sets the language code (e.g., "es-ES").
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
    /// Gets or sets the included directory names (directories where all files are synced regardless of extension).
    /// </summary>
    public List<string> IncludedDirectories { get; set; } = new();

    /// <summary>
    /// Gets or sets the default excluded extensions (read-only).
    /// </summary>
    public List<string> DefaultExcludedExtensions { get; set; } = new();

    /// <summary>
    /// Gets or sets the default excluded directories (read-only).
    /// </summary>
    public List<string> DefaultExcludedDirectories { get; set; } = new();

    /// <summary>
    /// Gets or sets the default included directories (read-only).
    /// </summary>
    public List<string> DefaultIncludedDirectories { get; set; } = new();
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
    /// </summary>
    public Guid? DefaultLanguageAlternativeId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether DefaultLanguageAlternativeId was explicitly provided.
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
    /// Gets or sets the included directory names (replaces existing list).
    /// </summary>
    public List<string>? IncludedDirectories { get; set; }
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
