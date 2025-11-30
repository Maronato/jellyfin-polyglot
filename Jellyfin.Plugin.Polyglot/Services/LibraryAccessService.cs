using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Polyglot.Helpers;
using Jellyfin.Plugin.Polyglot.Models;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Polyglot.Services;

/// <summary>
/// Service for managing user library access based on language assignments.
/// Uses IConfigurationService for all config access to prevent stale reference bugs.
/// </summary>
public class LibraryAccessService : ILibraryAccessService
{
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IConfigurationService _configService;
    private readonly ILogger<LibraryAccessService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryAccessService"/> class.
    /// </summary>
    public LibraryAccessService(
        IUserManager userManager,
        ILibraryManager libraryManager,
        IConfigurationService configService,
        ILogger<LibraryAccessService> logger)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _configService = configService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task UpdateUserLibraryAccessAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        _logger.PolyglotDebug("UpdateUserLibraryAccessAsync: Starting for user {0}", userId);

        var user = _userManager.GetUserById(userId);
        if (user == null)
        {
            _logger.PolyglotWarning("UpdateUserLibraryAccessAsync: User {0} not found", userId);
            return;
        }

        // Check if user is managed by the plugin
        var userConfig = _configService.GetUserLanguage(userId);
        if (userConfig == null || !userConfig.IsPluginManaged)
        {
            _logger.PolyglotDebug("UpdateUserLibraryAccessAsync: User {0} is not managed by plugin", user.Username);
            return;
        }

        // Get libraries that are managed by the plugin (sources + mirrors)
        var managedLibraries = GetManagedLibraryIds();

        // Get user's current library access (before we modify it)
        var currentAccess = GetCurrentLibraryAccess(user);

        // Get the libraries the plugin determines the user should see (only managed libraries)
        var expectedManagedLibraries = GetExpectedLibraryAccess(userId).ToHashSet();

        // Build final library list:
        // 1. For MANAGED libraries: use plugin's decision
        // 2. For UNMANAGED libraries: preserve user's current access
        var finalLibraries = new HashSet<Guid>();

        // Add managed libraries that the user should see
        foreach (var libId in expectedManagedLibraries)
        {
            finalLibraries.Add(libId);
        }

        // Preserve access to unmanaged libraries (like "Home Videos")
        foreach (var libId in currentAccess)
        {
            if (!managedLibraries.Contains(libId))
            {
                finalLibraries.Add(libId);
            }
        }

        // If no managed libraries exist yet, also preserve access to source libraries
        if (managedLibraries.Count == 0)
        {
            foreach (var libId in currentAccess)
            {
                finalLibraries.Add(libId);
            }

            _logger.PolyglotInfo(
                "UpdateUserLibraryAccessAsync: User {0} - no mirrors configured, preserving access to {1} libraries",
                user.Username,
                finalLibraries.Count);
        }
        else
        {
            _logger.PolyglotInfo(
                "UpdateUserLibraryAccessAsync: User {0} - {1} managed libraries, {2} unmanaged preserved",
                user.Username,
                expectedManagedLibraries.Count,
                finalLibraries.Count - expectedManagedLibraries.Count);
        }

        // Apply the access
        user.SetPermission(PermissionKind.EnableAllFolders, false);
        var libraryIds = finalLibraries.Select(g => g.ToString("N")).ToArray();
        user.SetPreference(PreferenceKind.EnabledFolders, libraryIds);

        await _userManager.UpdateUserAsync(user).ConfigureAwait(false);

        _logger.PolyglotDebug("UpdateUserLibraryAccessAsync: Completed for user {0}", userId);
    }

    /// <inheritdoc />
    public async Task<bool> ReconcileUserAccessAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = _userManager.GetUserById(userId);
        if (user == null)
        {
            return false;
        }

        // Check if user is managed by the plugin
        var userConfig = _configService.GetUserLanguage(userId);
        if (userConfig == null || !userConfig.IsPluginManaged)
        {
            return false;
        }

        // Get current and expected library access
        var expectedLibraries = GetExpectedLibraryAccess(userId).ToHashSet();
        var currentLibraries = GetCurrentLibraryAccess(user);

        // Also check if EnableAllFolders needs to be disabled
        var hasEnableAllFolders = user.HasPermission(PermissionKind.EnableAllFolders);

        // Check if reconciliation is needed
        if (!hasEnableAllFolders && expectedLibraries.SetEquals(currentLibraries))
        {
            return false;
        }

        _logger.PolyglotInfo(
            "ReconcileUserAccessAsync: Reconciling user {0} - expected {1}, current {2}, EnableAllFolders={3}",
            user.Username,
            expectedLibraries.Count,
            currentLibraries.Count,
            hasEnableAllFolders);

        await UpdateUserLibraryAccessAsync(userId, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<int> ReconcileAllUsersAsync(CancellationToken cancellationToken = default)
    {
        _logger.PolyglotDebug("ReconcileAllUsersAsync: Starting reconciliation");

        var userLanguages = _configService.GetUserLanguages();
        var changedCount = 0;

        foreach (var userLang in userLanguages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (await ReconcileUserAccessAsync(userLang.UserId, cancellationToken).ConfigureAwait(false))
                {
                    changedCount++;
                }
            }
            catch (Exception ex)
            {
                _logger.PolyglotError(ex, "ReconcileAllUsersAsync: Failed to reconcile user {0}", userLang.UserId);
            }
        }

        _logger.PolyglotInfo("ReconcileAllUsersAsync: Reconciled {0} users", changedCount);
        return changedCount;
    }

    /// <inheritdoc />
    public IEnumerable<Guid> GetExpectedLibraryAccess(Guid userId)
    {
        // Check if user has a language assigned
        var userConfig = _configService.GetUserLanguage(userId);

        // If user is not in config or not managed by plugin, return empty
        if (userConfig == null || !userConfig.IsPluginManaged)
        {
            yield break;
        }

        // Get all libraries that are part of the Polyglot-managed system
        var managedLibraries = GetManagedLibraryIds();

        // Get all Jellyfin libraries
        var allLibraries = _libraryManager.GetVirtualFolders();

        // Handle null library list
        if (allLibraries == null)
        {
            yield break;
        }

        // Get alternatives fresh from config service
        var alternatives = _configService.GetAlternatives();

        // Determine which alternative the user is assigned to (if any)
        LanguageAlternative? alternative = null;
        if (userConfig.SelectedAlternativeId.HasValue)
        {
            alternative = alternatives.FirstOrDefault(a => a.Id == userConfig.SelectedAlternativeId.Value);
        }

        // Get mirrors for the user's language (or empty if default/no language)
        var userMirrorIds = alternative?.MirroredLibraries
            .Where(m => m.TargetLibraryId.HasValue)
            .Select(m => m.TargetLibraryId!.Value)
            .ToHashSet() ?? new HashSet<Guid>();

        // Get source libraries that have mirrors for the user's language
        var userMirroredSourceIds = alternative?.MirroredLibraries
            .Where(m => m.TargetLibraryId.HasValue)
            .Select(m => m.SourceLibraryId)
            .ToHashSet() ?? new HashSet<Guid>();

        // Get ALL mirror IDs across all alternatives (to exclude other languages' mirrors)
        var allMirrorIds = alternatives
            .SelectMany(a => a.MirroredLibraries)
            .Where(m => m.TargetLibraryId.HasValue)
            .Select(m => m.TargetLibraryId!.Value)
            .ToHashSet();

        // Build set of library IDs that actually exist in Jellyfin
        var jellyfinLibraryIds = allLibraries
            .Select(lib => Guid.Parse(lib.ItemId))
            .ToHashSet();

        foreach (var library in allLibraries)
        {
            var libraryId = Guid.Parse(library.ItemId);
            var isManaged = managedLibraries.Contains(libraryId);

            if (!isManaged)
            {
                continue;
            }

            // Is this a mirror for the user's language?
            if (userMirrorIds.Contains(libraryId))
            {
                yield return libraryId;
                continue;
            }

            // Is this a mirror for a DIFFERENT language?
            if (allMirrorIds.Contains(libraryId))
            {
                continue;
            }

            // This is a source library - should it be shown?
            if (userMirroredSourceIds.Contains(libraryId))
            {
                // User has a mirror configured for this source
                // Verify the mirror actually exists in Jellyfin
                var mirrorExistsInJellyfin = alternative!.MirroredLibraries
                    .Where(m => m.SourceLibraryId == libraryId && m.TargetLibraryId.HasValue)
                    .Any(m => jellyfinLibraryIds.Contains(m.TargetLibraryId!.Value));

                if (mirrorExistsInJellyfin)
                {
                    continue;
                }

                _logger.PolyglotWarning(
                    "GetExpectedLibraryAccess: Mirror for source {0} was deleted. Showing source as fallback.",
                    libraryId);
            }

            yield return libraryId;
        }
    }

    /// <summary>
    /// Gets all library IDs that are managed by the plugin (sources with mirrors + all mirrors).
    /// </summary>
    private HashSet<Guid> GetManagedLibraryIds()
    {
        var managed = new HashSet<Guid>();
        var alternatives = _configService.GetAlternatives();

        foreach (var alternative in alternatives)
        {
            foreach (var mirror in alternative.MirroredLibraries)
            {
                managed.Add(mirror.SourceLibraryId);

                if (mirror.TargetLibraryId.HasValue)
                {
                    managed.Add(mirror.TargetLibraryId.Value);
                }
            }
        }

        return managed;
    }

    /// <summary>
    /// Gets the current library access for a user.
    /// </summary>
    private HashSet<Guid> GetCurrentLibraryAccess(Jellyfin.Data.Entities.User user)
    {
        var result = new HashSet<Guid>();

        if (user.HasPermission(PermissionKind.EnableAllFolders))
        {
            foreach (var folder in _libraryManager.GetVirtualFolders())
            {
                result.Add(Guid.Parse(folder.ItemId));
            }
        }
        else
        {
            var enabledFolders = user.GetPreference(PreferenceKind.EnabledFolders);
            if (enabledFolders != null && enabledFolders.Length > 0)
            {
                foreach (var idString in enabledFolders)
                {
                    if (Guid.TryParse(idString?.Trim(), out var id))
                    {
                        result.Add(id);
                    }
                }
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<int> EnableAllUsersAsync(CancellationToken cancellationToken = default)
    {
        _logger.PolyglotDebug("EnableAllUsersAsync: Starting");

        var allUsers = _userManager.Users.ToList();
        var enabledCount = 0;

        foreach (var user in allUsers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // Check existing state before update to determine if this is a new enablement
                var existingConfig = _configService.GetUserLanguage(user.Id);
                var wasAlreadyManaged = existingConfig?.IsPluginManaged ?? false;

                // Use atomic update/create through config service
                _configService.UpdateOrCreateUserLanguage(user.Id, userConfig =>
                {
                    if (!userConfig.IsPluginManaged)
                    {
                        userConfig.IsPluginManaged = true;
                        userConfig.SetAt = DateTime.UtcNow;
                        userConfig.SetBy = "bulk-enable";
                    }
                });

                // Count as "newly enabled" if the user wasn't already managed
                if (!wasAlreadyManaged)
                {
                    enabledCount++;
                }

                // Apply library access for this user
                await UpdateUserLibraryAccessAsync(user.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.PolyglotError(ex, "EnableAllUsersAsync: Failed to enable user {0}", user.Username);
            }
        }

        _logger.PolyglotInfo("EnableAllUsersAsync: Enabled plugin management for {0} users", enabledCount);
        return enabledCount;
    }

    /// <inheritdoc />
    public async Task DisableUserAsync(Guid userId, bool restoreFullAccess = false, CancellationToken cancellationToken = default)
    {
        _logger.PolyglotDebug("DisableUserAsync: Disabling user {0}", userId);

        var user = _userManager.GetUserById(userId);
        if (user == null)
        {
            _logger.PolyglotWarning("DisableUserAsync: User {0} not found", userId);
            return;
        }

        // Update user config atomically - check return value to verify the update succeeded
        var updated = _configService.UpdateUserLanguage(userId, userConfig =>
        {
            userConfig.IsPluginManaged = false;
            userConfig.SetAt = DateTime.UtcNow;
            userConfig.SetBy = "admin-disabled";
        });

        if (!updated)
        {
            // User was never in the plugin's config - this is not necessarily an error,
            // it just means they weren't being managed by the plugin
            _logger.PolyglotDebug("DisableUserAsync: User {0} ({1}) was not in plugin config, nothing to disable",
                userId, user.Username);
        }

        // Optionally restore full access (do this regardless of whether user was in config)
        if (restoreFullAccess)
        {
            user.SetPermission(PermissionKind.EnableAllFolders, true);
            await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
            _logger.PolyglotInfo("DisableUserAsync: Restored EnableAllFolders for user {0}", user.Username);
        }

        if (updated)
        {
            _logger.PolyglotInfo("DisableUserAsync: Disabled plugin management for user {0}", user.Username);
        }
    }

    /// <inheritdoc />
    public async Task AddLibrariesToUserAccessAsync(Guid userId, IEnumerable<Guid> libraryIds, CancellationToken cancellationToken = default)
    {
        var user = _userManager.GetUserById(userId);
        if (user == null)
        {
            _logger.PolyglotWarning("AddLibrariesToUserAccessAsync: User {0} not found", userId);
            return;
        }

        var libraryIdSet = libraryIds.ToHashSet();
        if (libraryIdSet.Count == 0)
        {
            return;
        }

        // Get current access
        var currentAccess = GetCurrentLibraryAccess(user);
        var newAccess = new HashSet<Guid>(currentAccess);

        // Add the specified libraries
        var addedCount = 0;
        foreach (var libraryId in libraryIdSet)
        {
            if (newAccess.Add(libraryId))
            {
                addedCount++;
            }
        }

        if (addedCount == 0)
        {
            return;
        }

        // Update user's access
        user.SetPermission(PermissionKind.EnableAllFolders, false);
        var libraryIdsArray = newAccess.Select(g => g.ToString("N")).ToArray();
        user.SetPreference(PreferenceKind.EnabledFolders, libraryIdsArray);

        await _userManager.UpdateUserAsync(user).ConfigureAwait(false);

        _logger.PolyglotInfo(
            "AddLibrariesToUserAccessAsync: Added {0} source libraries to user {1} (total: {2})",
            addedCount,
            user.Username,
            newAccess.Count);
    }
}
