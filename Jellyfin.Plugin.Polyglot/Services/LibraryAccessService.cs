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
/// </summary>
public class LibraryAccessService : ILibraryAccessService
{
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LibraryAccessService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryAccessService"/> class.
    /// </summary>
    public LibraryAccessService(
        IUserManager userManager,
        ILibraryManager libraryManager,
        ILogger<LibraryAccessService> logger)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task UpdateUserLibraryAccessAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = _userManager.GetUserById(userId);
        if (user == null)
        {
            _logger.PolyglotWarning("User {0} not found", userId);
            return;
        }

        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return;
        }

        // Check if user is managed by the plugin
        var userConfig = config.UserLanguages.FirstOrDefault(u => u.UserId == userId);
        if (userConfig == null || !userConfig.IsPluginManaged)
        {
            _logger.PolyglotDebug("User {0} is not managed by plugin, skipping library access update", user.Username);
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
                // This library is not managed by the plugin - preserve access
                finalLibraries.Add(libId);
            }
        }

        // If no managed libraries exist yet, also preserve access to source libraries
        // (This handles the case where plugin is enabled but no mirrors are configured yet)
        if (managedLibraries.Count == 0)
        {
            foreach (var libId in currentAccess)
            {
                finalLibraries.Add(libId);
            }
            _logger.PolyglotInfo(
                "User {0} - no mirrors configured yet, preserving current access to {1} libraries",
                user.Username,
                finalLibraries.Count);
        }
        else
        {
            _logger.PolyglotInfo(
                "User {0} library access updated: {1} managed libraries, {2} unmanaged preserved",
                user.Username,
                expectedManagedLibraries.Count,
                finalLibraries.Count - expectedManagedLibraries.Count);
        }

        // Apply the access
        user.SetPermission(PermissionKind.EnableAllFolders, false);
        var libraryIds = finalLibraries.Select(g => g.ToString("N")).ToArray();
        user.SetPreference(PreferenceKind.EnabledFolders, libraryIds);

        await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> ReconcileUserAccessAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = _userManager.GetUserById(userId);
        if (user == null)
        {
            return false;
        }

        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return false;
        }

        // Check if user is managed by the plugin
        var userConfig = config.UserLanguages.FirstOrDefault(u => u.UserId == userId);
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
            "Reconciling user {0} library access: expected {1}, current {2}, EnableAllFolders={3}",
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
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return 0;
        }

        var changedCount = 0;

        foreach (var userLang in config.UserLanguages)
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
                _logger.PolyglotError(ex, "Failed to reconcile user {0}", userLang.UserId);
            }
        }

        return changedCount;
    }

    /// <inheritdoc />
    public IEnumerable<Guid> GetExpectedLibraryAccess(Guid userId)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            yield break;
        }

        // Check if user has a language assigned
        var userConfig = config.UserLanguages.FirstOrDefault(u => u.UserId == userId);

        // If user is not in config or not managed by plugin, return empty
        // This means we won't modify their library access at all
        if (userConfig == null || !userConfig.IsPluginManaged)
        {
            yield break;
        }

        // Get all libraries that are part of the Polyglot-managed system
        var managedLibraries = GetManagedLibraryIds();

        // Get all Jellyfin libraries
        var allLibraries = _libraryManager.GetVirtualFolders();

        // Determine which alternative the user is assigned to (if any)
        LanguageAlternative? alternative = null;
        if (userConfig?.SelectedAlternativeId.HasValue == true)
        {
            alternative = config.LanguageAlternatives.FirstOrDefault(a => a.Id == userConfig.SelectedAlternativeId.Value);
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
        var allMirrorIds = config.LanguageAlternatives
            .SelectMany(a => a.MirroredLibraries)
            .Where(m => m.TargetLibraryId.HasValue)
            .Select(m => m.TargetLibraryId!.Value)
            .ToHashSet();

        // Handle null library list
        if (allLibraries == null)
        {
            yield break;
        }

        // Build set of library IDs that actually exist in Jellyfin
        // This is used to detect when a mirror was deleted from Jellyfin but config still references it
        var jellyfinLibraryIds = allLibraries
            .Select(lib => Guid.Parse(lib.ItemId))
            .ToHashSet();

        foreach (var library in allLibraries)
        {
            var libraryId = Guid.Parse(library.ItemId);
            var isManaged = managedLibraries.Contains(libraryId);

            if (!isManaged)
            {
                // This library is NOT part of the Polyglot-managed system (like "Home Videos")
                // We don't include it here - it will be handled separately to preserve user's existing access
                continue;
            }

            // This library IS managed by the plugin

            // Is this a mirror for the user's language?
            if (userMirrorIds.Contains(libraryId))
            {
                yield return libraryId;
                continue;
            }

            // Is this a mirror for a DIFFERENT language?
            if (allMirrorIds.Contains(libraryId))
            {
                // Exclude mirrors for other languages
                continue;
            }

            // This is a source library - should it be shown?
            if (userMirroredSourceIds.Contains(libraryId))
            {
                // User has a mirror configured for this source
                // But we should verify the mirror actually exists in Jellyfin
                // If the mirror was deleted, fall back to showing the source
                // "Better to have the movie with foreign metadata than no movie at all"
                var mirrorExistsInJellyfin = alternative!.MirroredLibraries
                    .Where(m => m.SourceLibraryId == libraryId && m.TargetLibraryId.HasValue)
                    .Any(m => jellyfinLibraryIds.Contains(m.TargetLibraryId!.Value));

                if (mirrorExistsInJellyfin)
                {
                    // Mirror exists, hide the source
                    continue;
                }

                // Mirror was deleted from Jellyfin - show the source as fallback
                _logger.PolyglotWarning(
                    "Mirror for source library {0} was deleted from Jellyfin. Showing source as fallback.",
                    libraryId);
            }

            // Source library without a mirror for this language (or mirror was deleted) - show it
            yield return libraryId;
        }
    }

    /// <summary>
    /// Gets all library IDs that are managed by the plugin (sources with mirrors + all mirrors).
    /// Libraries not in this set should have their access preserved as-is.
    /// </summary>
    private HashSet<Guid> GetManagedLibraryIds()
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return new HashSet<Guid>();
        }

        var managed = new HashSet<Guid>();

        foreach (var alternative in config.LanguageAlternatives)
        {
            foreach (var mirror in alternative.MirroredLibraries)
            {
                // Add source library
                managed.Add(mirror.SourceLibraryId);

                // Add mirror library if it exists
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
            // User has access to all folders
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
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return 0;
        }

        var allUsers = _userManager.Users.ToList();
        var enabledCount = 0;

        foreach (var user in allUsers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // Check if user already has a config entry
                var existingConfig = config.UserLanguages.FirstOrDefault(u => u.UserId == user.Id);

                if (existingConfig != null)
                {
                    // Update existing entry to be managed
                    if (!existingConfig.IsPluginManaged)
                    {
                        existingConfig.IsPluginManaged = true;
                        existingConfig.SetAt = DateTime.UtcNow;
                        existingConfig.SetBy = "bulk-enable";
                        enabledCount++;
                    }
                }
                else
                {
                    // Create new entry for unmanaged user
                    config.UserLanguages.Add(new UserLanguageConfig
                    {
                        UserId = user.Id,
                        IsPluginManaged = true,
                        SelectedAlternativeId = null, // Default language
                        ManuallySet = false,
                        SetAt = DateTime.UtcNow,
                        SetBy = "bulk-enable"
                    });
                    enabledCount++;
                }

                // Apply library access for this user
                await UpdateUserLibraryAccessAsync(user.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.PolyglotError(ex, "Failed to enable user {0}", user.Username);
            }
        }

        // Save configuration
        Plugin.Instance?.SaveConfiguration();

        _logger.PolyglotInfo("Enabled plugin management for {0} users", enabledCount);
        return enabledCount;
    }

    /// <inheritdoc />
    public async Task DisableUserAsync(Guid userId, bool restoreFullAccess = false, CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return;
        }

        var user = _userManager.GetUserById(userId);
        if (user == null)
        {
            _logger.PolyglotWarning("User {0} not found", userId);
            return;
        }

        // Find and update user config
        var userConfig = config.UserLanguages.FirstOrDefault(u => u.UserId == userId);
        if (userConfig != null)
        {
            userConfig.IsPluginManaged = false;
            userConfig.SetAt = DateTime.UtcNow;
            userConfig.SetBy = "admin-disabled";
        }

        // Optionally restore full access
        if (restoreFullAccess)
        {
            user.SetPermission(PermissionKind.EnableAllFolders, true);
            await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
            _logger.PolyglotInfo("Restored EnableAllFolders for user {0}", user.Username);
        }

        Plugin.Instance?.SaveConfiguration();
        _logger.PolyglotInfo("Disabled plugin management for user {0}", user.Username);
    }

    /// <inheritdoc />
    public async Task AddLibrariesToUserAccessAsync(Guid userId, IEnumerable<Guid> libraryIds, CancellationToken cancellationToken = default)
    {
        var user = _userManager.GetUserById(userId);
        if (user == null)
        {
            _logger.PolyglotWarning("User {0} not found when adding libraries", userId);
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
            // User already has access to all specified libraries
            return;
        }

        // Update user's access
        user.SetPermission(PermissionKind.EnableAllFolders, false);
        var libraryIdsArray = newAccess.Select(g => g.ToString("N")).ToArray();
        user.SetPreference(PreferenceKind.EnabledFolders, libraryIdsArray);

        await _userManager.UpdateUserAsync(user).ConfigureAwait(false);

        _logger.PolyglotInfo(
            "Added {0} source libraries to user {1}'s access (total: {2})",
            addedCount,
            user.Username,
            newAccess.Count);
    }
}
