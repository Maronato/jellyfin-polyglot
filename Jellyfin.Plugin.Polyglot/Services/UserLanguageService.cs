using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Polyglot.Helpers;
using Jellyfin.Plugin.Polyglot.Models;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Polyglot.Services;

/// <summary>
/// Service for managing user language assignments.
/// Uses IConfigurationService for all config modifications to prevent stale reference bugs.
/// </summary>
public class UserLanguageService : IUserLanguageService
{
    private readonly IUserManager _userManager;
    private readonly ILibraryAccessService _libraryAccessService;
    private readonly IConfigurationService _configService;
    private readonly ILogger<UserLanguageService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserLanguageService"/> class.
    /// </summary>
    public UserLanguageService(
        IUserManager userManager,
        ILibraryAccessService libraryAccessService,
        IConfigurationService configService,
        ILogger<UserLanguageService> logger)
    {
        _userManager = userManager;
        _libraryAccessService = libraryAccessService;
        _configService = configService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task AssignLanguageAsync(Guid userId, Guid? alternativeId, string setBy, bool manuallySet = false, bool isPluginManaged = true, CancellationToken cancellationToken = default)
    {
        _logger.PolyglotDebug("AssignLanguageAsync: Assigning language to user {0}", userId);

        var user = _userManager.GetUserById(userId);
        if (user == null)
        {
            throw new ArgumentException($"User {userId} not found", nameof(userId));
        }

        // Validate alternative exists if specified
        string? alternativeName = null;
        if (alternativeId.HasValue)
        {
            var alternative = _configService.GetAlternative(alternativeId.Value);
            if (alternative == null)
            {
                throw new ArgumentException($"Language alternative {alternativeId} not found", nameof(alternativeId));
            }

            alternativeName = alternative.Name;
        }

        // Update or create user language config atomically
        _configService.UpdateOrCreateUserLanguage(userId, userConfig =>
        {
            userConfig.SelectedAlternativeId = alternativeId;
            userConfig.ManuallySet = manuallySet;
            userConfig.IsPluginManaged = isPluginManaged;
            userConfig.SetAt = DateTime.UtcNow;
            userConfig.SetBy = setBy;
        });

        _logger.PolyglotInfo(
            "AssignLanguageAsync: Assigned language {0} to user {1} (by: {2}, manual: {3}, managed: {4})",
            alternativeName ?? "Default",
            user.Username,
            setBy,
            manuallySet,
            isPluginManaged);

        // Update user's library access (only if managed)
        if (isPluginManaged)
        {
            await _libraryAccessService.UpdateUserLibraryAccessAsync(userId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public UserLanguageConfig? GetUserLanguage(Guid userId)
    {
        return _configService.GetUserLanguage(userId);
    }

    /// <inheritdoc />
    public LanguageAlternative? GetUserLanguageAlternative(Guid userId)
    {
        var userConfig = _configService.GetUserLanguage(userId);
        if (userConfig == null || !userConfig.SelectedAlternativeId.HasValue)
        {
            return null;
        }

        return _configService.GetAlternative(userConfig.SelectedAlternativeId.Value);
    }

    /// <inheritdoc />
    public async Task ClearLanguageAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        _logger.PolyglotDebug("ClearLanguageAsync: Clearing language for user {0}", userId);

        var updated = _configService.UpdateUserLanguage(userId, userConfig =>
        {
            userConfig.SelectedAlternativeId = null;
            userConfig.SetAt = DateTime.UtcNow;
            userConfig.SetBy = "admin";
        });

        if (updated)
        {
            _logger.PolyglotInfo("ClearLanguageAsync: Cleared language assignment for user {0}", userId);

            // Update user's library access to show all libraries
            await _libraryAccessService.UpdateUserLibraryAccessAsync(userId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _logger.PolyglotDebug("ClearLanguageAsync: No language assignment found for user {0}", userId);
        }
    }

    /// <inheritdoc />
    public IEnumerable<UserInfo> GetAllUsersWithLanguages()
    {
        var users = _userManager.Users;
        var userLanguages = _configService.GetUserLanguages();
        var alternatives = _configService.GetAlternatives();

        foreach (var user in users)
        {
            var userInfo = new UserInfo
            {
                Id = user.Id,
                Username = user.Username,
                IsAdministrator = user.HasPermission(Jellyfin.Data.Enums.PermissionKind.IsAdministrator)
            };

            var userConfig = userLanguages.FirstOrDefault(u => u.UserId == user.Id);
            if (userConfig != null)
            {
                userInfo.IsPluginManaged = userConfig.IsPluginManaged;
                userInfo.AssignedAlternativeId = userConfig.SelectedAlternativeId;
                userInfo.ManuallySet = userConfig.ManuallySet;
                userInfo.SetBy = userConfig.SetBy;
                userInfo.SetAt = userConfig.SetAt;

                if (userConfig.SelectedAlternativeId.HasValue)
                {
                    var alt = alternatives.FirstOrDefault(a => a.Id == userConfig.SelectedAlternativeId.Value);
                    userInfo.AssignedAlternativeName = alt?.Name;
                }
            }

            yield return userInfo;
        }
    }

    /// <inheritdoc />
    public bool IsManuallySet(Guid userId)
    {
        var userConfig = _configService.GetUserLanguage(userId);
        return userConfig?.ManuallySet ?? false;
    }

    /// <inheritdoc />
    public void RemoveUser(Guid userId)
    {
        _logger.PolyglotDebug("RemoveUser: Removing language assignment for user {0}", userId);

        if (_configService.RemoveUserLanguage(userId))
        {
            _logger.PolyglotInfo("RemoveUser: Removed language assignment for deleted user {0}", userId);
        }
        else
        {
            _logger.PolyglotDebug("RemoveUser: No language assignment found for user {0}", userId);
        }
    }
}
