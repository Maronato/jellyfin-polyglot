using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.MultiLang.Services;
using MediaBrowser.Controller.Events;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MultiLang.EventConsumers;

/// <summary>
/// Handles user creation events to potentially assign language based on LDAP groups.
/// </summary>
public class UserCreatedConsumer : IEventConsumer<UserCreatedEventArgs>
{
    private readonly IUserLanguageService _userLanguageService;
    private readonly ILdapIntegrationService _ldapIntegrationService;
    private readonly ILogger<UserCreatedConsumer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserCreatedConsumer"/> class.
    /// </summary>
    public UserCreatedConsumer(
        IUserLanguageService userLanguageService,
        ILdapIntegrationService ldapIntegrationService,
        ILogger<UserCreatedConsumer> logger)
    {
        _userLanguageService = userLanguageService;
        _ldapIntegrationService = ldapIntegrationService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OnEvent(UserCreatedEventArgs eventArgs)
    {
        var user = eventArgs.Argument;
        _logger.LogInformation("User created: {Username} ({UserId})", user.Username, user.Id);

        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return;
        }

        // Check if LDAP integration is enabled
        if (!config.EnableLdapIntegration)
        {
            _logger.LogDebug("LDAP integration disabled, skipping auto-assignment for {Username}", user.Username);
            return;
        }

        // Check if LDAP plugin is available
        if (!_ldapIntegrationService.IsLdapPluginAvailable())
        {
            _logger.LogDebug("LDAP plugin not available, skipping auto-assignment for {Username}", user.Username);
            return;
        }

        try
        {
            // Try to determine language from LDAP groups
            var languageId = await _ldapIntegrationService.DetermineLanguageFromGroupsAsync(user.Username, CancellationToken.None)
                .ConfigureAwait(false);

            if (languageId.HasValue)
            {
                await _userLanguageService.AssignLanguageAsync(
                    user.Id,
                    languageId.Value,
                    "ldap",
                    manuallySet: false,
                    isPluginManaged: true,
                    CancellationToken.None).ConfigureAwait(false);

                _logger.LogInformation(
                    "Assigned language {LanguageId} to new user {Username} based on LDAP groups",
                    languageId.Value,
                    user.Username);
            }
            else
            {
                _logger.LogDebug("No LDAP group match found for new user {Username}", user.Username);
            }
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "Failed to auto-assign language for new user {Username}", user.Username);
        }
    }
}

