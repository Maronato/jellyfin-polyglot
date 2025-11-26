using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.Polyglot.Services;
using MediaBrowser.Controller.Events;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Polyglot.EventConsumers;

/// <summary>
/// Handles user updated events to sync username changes and optionally re-evaluate LDAP groups.
/// </summary>
public class UserUpdatedConsumer : IEventConsumer<UserUpdatedEventArgs>
{
    private readonly IUserLanguageService _userLanguageService;
    private readonly ILdapIntegrationService _ldapIntegrationService;
    private readonly ILibraryAccessService _libraryAccessService;
    private readonly ILogger<UserUpdatedConsumer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserUpdatedConsumer"/> class.
    /// </summary>
    public UserUpdatedConsumer(
        IUserLanguageService userLanguageService,
        ILdapIntegrationService ldapIntegrationService,
        ILibraryAccessService libraryAccessService,
        ILogger<UserUpdatedConsumer> logger)
    {
        _userLanguageService = userLanguageService;
        _ldapIntegrationService = ldapIntegrationService;
        _libraryAccessService = libraryAccessService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OnEvent(UserUpdatedEventArgs eventArgs)
    {
        var user = eventArgs.Argument;
        _logger.LogDebug("User updated: {Username} ({UserId})", user.Username, user.Id);

        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return;
        }

        // Update username in our config if it changed
        var userConfig = config.UserLanguages.FirstOrDefault(u => u.UserId == user.Id);
        if (userConfig != null)
        {
            if (userConfig.Username != user.Username)
            {
                userConfig.Username = user.Username;
                Plugin.Instance?.SaveConfiguration();
                _logger.LogDebug("Updated username in config for user {UserId}", user.Id);
            }
        }

        // If LDAP integration is enabled and user was NOT manually set, re-check LDAP groups
        // This handles cases where a user's LDAP group membership may have changed
        if (config.EnableLdapIntegration &&
            !_userLanguageService.IsManuallySet(user.Id) &&
            _ldapIntegrationService.IsLdapPluginAvailable())
        {
            try
            {
                var languageId = await _ldapIntegrationService.DetermineLanguageFromGroupsAsync(
                    user.Username,
                    System.Threading.CancellationToken.None).ConfigureAwait(false);

                var currentConfig = _userLanguageService.GetUserLanguage(user.Id);
                var currentLanguageId = currentConfig?.SelectedAlternativeId;

                // Only update if the language changed
                if (languageId != currentLanguageId)
                {
                    if (languageId.HasValue)
                    {
                        await _userLanguageService.AssignLanguageAsync(
                            user.Id,
                            languageId.Value,
                            "ldap",
                            manuallySet: false,
                            isPluginManaged: true,
                            System.Threading.CancellationToken.None).ConfigureAwait(false);

                        _logger.LogInformation(
                            "Updated language for user {Username} based on LDAP groups",
                            user.Username);
                    }
                    else if (currentLanguageId.HasValue)
                    {
                        // User no longer matches any LDAP groups, clear their language
                        await _userLanguageService.ClearLanguageAsync(
                            user.Id,
                            System.Threading.CancellationToken.None).ConfigureAwait(false);

                        _logger.LogInformation(
                            "Cleared language for user {Username} - no longer matches LDAP groups",
                            user.Username);
                    }
                }
            }
            catch (System.Exception ex)
            {
                _logger.LogWarning(ex, "Failed to re-evaluate LDAP groups for user {Username}", user.Username);
            }
        }
    }
}
