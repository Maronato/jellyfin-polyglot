using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MultiLang.Models;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MultiLang.Services;

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
            _logger.LogWarning("User {UserId} not found", userId);
            return;
        }

        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return;
        }

        // Get expected library access
        var expectedLibraries = GetExpectedLibraryAccess(userId).ToList();

        if (expectedLibraries.Count == 0)
        {
            // No language assigned - give access to all non-mirror libraries
            user.SetPermission(PermissionKind.EnableAllFolders, true);
            _logger.LogInformation("User {Username} set to access all folders (no language assigned)", user.Username);
        }
        else
        {
            // Set specific library access
            user.SetPermission(PermissionKind.EnableAllFolders, false);

            var libraryIds = expectedLibraries.Select(g => g.ToString("N")).ToArray();
            user.SetPreference(PreferenceKind.EnabledFolders, libraryIds);

            _logger.LogInformation(
                "User {Username} library access updated to {Count} libraries",
                user.Username,
                expectedLibraries.Count);
        }

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

        // Check if user has a language assigned
        if (!config.UserLanguages.TryGetValue(userId, out var userConfig) || !userConfig.SelectedAlternativeId.HasValue)
        {
            return false;
        }

        // Get current and expected library access
        var expectedLibraries = GetExpectedLibraryAccess(userId).ToHashSet();
        var currentLibraries = GetCurrentLibraryAccess(user);

        // Check if reconciliation is needed
        if (expectedLibraries.SetEquals(currentLibraries))
        {
            return false;
        }

        _logger.LogInformation(
            "Reconciling user {Username} library access: expected {Expected}, current {Current}",
            user.Username,
            expectedLibraries.Count,
            currentLibraries.Count);

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

        foreach (var userId in config.UserLanguages.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (await ReconcileUserAccessAsync(userId, cancellationToken).ConfigureAwait(false))
                {
                    changedCount++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reconcile user {UserId}", userId);
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
        if (!config.UserLanguages.TryGetValue(userId, out var userConfig) || !userConfig.SelectedAlternativeId.HasValue)
        {
            yield break;
        }

        // Find the language alternative
        var alternative = config.LanguageAlternatives.FirstOrDefault(a => a.Id == userConfig.SelectedAlternativeId.Value);
        if (alternative == null)
        {
            yield break;
        }

        // Get all source library IDs that are mirrored
        var mirroredSourceIds = alternative.MirroredLibraries
            .Where(m => m.TargetLibraryId.HasValue)
            .Select(m => m.SourceLibraryId)
            .ToHashSet();

        // Get all Jellyfin libraries
        var allLibraries = _libraryManager.GetVirtualFolders();

        foreach (var library in allLibraries)
        {
            var libraryId = Guid.Parse(library.ItemId);

            // Check if this library is a mirror for our alternative
            var isMirrorForAlternative = alternative.MirroredLibraries.Any(m => m.TargetLibraryId == libraryId);
            if (isMirrorForAlternative)
            {
                // Include mirror libraries for the user's language
                yield return libraryId;
                continue;
            }

            // Check if this is a source library that has been mirrored
            if (mirroredSourceIds.Contains(libraryId))
            {
                // Exclude source libraries that have mirrors (user should see mirror instead)
                continue;
            }

            // Check if this library is a mirror for another alternative
            var isMirrorForOther = config.LanguageAlternatives
                .Where(a => a.Id != alternative.Id)
                .Any(a => a.MirroredLibraries.Any(m => m.TargetLibraryId == libraryId));

            if (isMirrorForOther)
            {
                // Exclude mirrors for other languages
                continue;
            }

            // Include non-mirrored source libraries (e.g., libraries not configured for mirroring)
            yield return libraryId;
        }
    }

    /// <inheritdoc />
    public async Task SyncUserLanguagePreferencesAsync(Guid userId, string languageCode, CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return;
        }

        var user = _userManager.GetUserById(userId);
        if (user == null)
        {
            return;
        }

        var changed = false;
        var languageBase = GetLanguageFromCode(languageCode);

        // Sync subtitle language - set as array with single value
        if (config.SyncUserSubtitleLanguage)
        {
            user.SubtitleLanguagePreference = languageBase;
            changed = true;
            _logger.LogDebug("Set subtitle language to {Language} for user {Username}", languageBase, user.Username);
        }

        // Sync audio language - set as array with single value
        if (config.SyncUserAudioLanguage)
        {
            user.AudioLanguagePreference = languageBase;
            changed = true;
            _logger.LogDebug("Set audio language to {Language} for user {Username}", languageBase, user.Username);
        }

        if (changed)
        {
            await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
        }

        // Note: SyncUserDisplayLanguage would require setting DisplayPreferences which
        // requires different API access. For now, we log a message indicating this limitation.
        if (config.SyncUserDisplayLanguage)
        {
            _logger.LogDebug(
                "Display language sync requested for user {Username} to {LanguageCode}. " +
                "UI language preferences are managed through Jellyfin's display preferences API.",
                user.Username,
                languageCode);
        }
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

    /// <summary>
    /// Extracts the language code from a locale code (e.g., "pt-BR" -> "pt").
    /// </summary>
    private static string GetLanguageFromCode(string languageCode)
    {
        if (string.IsNullOrEmpty(languageCode))
        {
            return string.Empty;
        }

        var dashIndex = languageCode.IndexOf('-');
        return dashIndex > 0 ? languageCode.Substring(0, dashIndex) : languageCode;
    }
}
