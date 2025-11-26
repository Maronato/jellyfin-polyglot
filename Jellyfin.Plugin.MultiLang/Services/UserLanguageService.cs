using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MultiLang.Models;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MultiLang.Services;

/// <summary>
/// Service for managing user language assignments.
/// </summary>
public class UserLanguageService : IUserLanguageService
{
    private readonly IUserManager _userManager;
    private readonly ILibraryAccessService _libraryAccessService;
    private readonly ILogger<UserLanguageService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserLanguageService"/> class.
    /// </summary>
    public UserLanguageService(
        IUserManager userManager,
        ILibraryAccessService libraryAccessService,
        ILogger<UserLanguageService> logger)
    {
        _userManager = userManager;
        _libraryAccessService = libraryAccessService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task AssignLanguageAsync(Guid userId, Guid? alternativeId, string setBy, bool manuallySet = false, CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            throw new InvalidOperationException("Plugin configuration not available");
        }

        var user = _userManager.GetUserById(userId);
        if (user == null)
        {
            throw new ArgumentException($"User {userId} not found", nameof(userId));
        }

        // Validate alternative exists if specified
        LanguageAlternative? alternative = null;
        if (alternativeId.HasValue)
        {
            alternative = config.LanguageAlternatives.FirstOrDefault(a => a.Id == alternativeId.Value);
            if (alternative == null)
            {
                throw new ArgumentException($"Language alternative {alternativeId} not found", nameof(alternativeId));
            }
        }

        // Create or update user language config
        if (!config.UserLanguages.TryGetValue(userId, out var userConfig))
        {
            userConfig = new UserLanguageConfig
            {
                UserId = userId,
                Username = user.Username
            };
            config.UserLanguages[userId] = userConfig;
        }

        var previousAlternativeId = userConfig.SelectedAlternativeId;

        userConfig.SelectedAlternativeId = alternativeId;
        userConfig.ManuallySet = manuallySet;
        userConfig.SetAt = DateTime.UtcNow;
        userConfig.SetBy = setBy;
        userConfig.Username = user.Username;

        SaveConfiguration();

        _logger.LogInformation(
            "Assigned language {AlternativeName} to user {Username} (by: {SetBy}, manual: {ManuallySet})",
            alternative?.Name ?? "None",
            user.Username,
            setBy,
            manuallySet);

        // Update user's library access
        await _libraryAccessService.UpdateUserLibraryAccessAsync(userId, cancellationToken).ConfigureAwait(false);

        // Optionally sync user language preferences
        if (alternative != null && !string.IsNullOrEmpty(alternative.LanguageCode))
        {
            await _libraryAccessService.SyncUserLanguagePreferencesAsync(userId, alternative.LanguageCode, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public UserLanguageConfig? GetUserLanguage(Guid userId)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return null;
        }

        config.UserLanguages.TryGetValue(userId, out var userConfig);
        return userConfig;
    }

    /// <inheritdoc />
    public LanguageAlternative? GetUserLanguageAlternative(Guid userId)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return null;
        }

        if (!config.UserLanguages.TryGetValue(userId, out var userConfig))
        {
            return null;
        }

        if (!userConfig.SelectedAlternativeId.HasValue)
        {
            return null;
        }

        return config.LanguageAlternatives.FirstOrDefault(a => a.Id == userConfig.SelectedAlternativeId.Value);
    }

    /// <inheritdoc />
    public async Task ClearLanguageAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return;
        }

        if (config.UserLanguages.TryGetValue(userId, out var userConfig))
        {
            userConfig.SelectedAlternativeId = null;
            userConfig.SetAt = DateTime.UtcNow;
            userConfig.SetBy = "admin";
            SaveConfiguration();

            _logger.LogInformation("Cleared language assignment for user {UserId}", userId);

            // Update user's library access to show all libraries
            await _libraryAccessService.UpdateUserLibraryAccessAsync(userId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public IEnumerable<UserInfo> GetAllUsersWithLanguages()
    {
        var config = Plugin.Instance?.Configuration;
        var users = _userManager.Users;

        foreach (var user in users)
        {
            var userInfo = new UserInfo
            {
                Id = user.Id,
                Username = user.Username,
                IsAdministrator = user.HasPermission(Jellyfin.Data.Enums.PermissionKind.IsAdministrator)
            };

            if (config != null && config.UserLanguages.TryGetValue(user.Id, out var userConfig))
            {
                userInfo.AssignedAlternativeId = userConfig.SelectedAlternativeId;
                userInfo.ManuallySet = userConfig.ManuallySet;
                userInfo.SetBy = userConfig.SetBy;
                userInfo.SetAt = userConfig.SetAt;

                if (userConfig.SelectedAlternativeId.HasValue)
                {
                    var alt = config.LanguageAlternatives.FirstOrDefault(a => a.Id == userConfig.SelectedAlternativeId.Value);
                    userInfo.AssignedAlternativeName = alt?.Name;
                }
            }

            yield return userInfo;
        }
    }

    /// <inheritdoc />
    public bool IsManuallySet(Guid userId)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return false;
        }

        if (config.UserLanguages.TryGetValue(userId, out var userConfig))
        {
            return userConfig.ManuallySet;
        }

        return false;
    }

    /// <inheritdoc />
    public void RemoveUser(Guid userId)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return;
        }

        if (config.UserLanguages.Remove(userId))
        {
            SaveConfiguration();
            _logger.LogInformation("Removed language assignment for deleted user {UserId}", userId);
        }
    }

    /// <summary>
    /// Saves the plugin configuration.
    /// </summary>
    private void SaveConfiguration()
    {
        Plugin.Instance?.SaveConfiguration();
    }
}

