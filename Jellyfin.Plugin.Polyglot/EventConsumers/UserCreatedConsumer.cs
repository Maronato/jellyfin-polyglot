using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.Polyglot.Services;
using MediaBrowser.Controller.Events;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Polyglot.EventConsumers;

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

        // First, try LDAP-based assignment if enabled
        if (config.EnableLdapIntegration && _ldapIntegrationService.IsLdapPluginAvailable())
        {
            try
            {
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
                    return;
                }

                _logger.LogDebug("No LDAP group match found for new user {Username}", user.Username);
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Failed to check LDAP groups for new user {Username}", user.Username);
            }
        }

        // Fall back to auto-manage if enabled
        if (config.AutoManageNewUsers)
        {
            try
            {
                await _userLanguageService.AssignLanguageAsync(
                    user.Id,
                    config.DefaultLanguageAlternativeId,
                    "auto",
                    manuallySet: false,
                    isPluginManaged: true,
                    CancellationToken.None).ConfigureAwait(false);

                var languageName = config.DefaultLanguageAlternativeId.HasValue
                    ? config.LanguageAlternatives.Find(a => a.Id == config.DefaultLanguageAlternativeId.Value)?.Name ?? "Unknown"
                    : "Default libraries";

                _logger.LogInformation(
                    "Auto-assigned {LanguageName} to new user {Username}",
                    languageName,
                    user.Username);
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Failed to auto-assign language for new user {Username}", user.Username);
            }
        }
    }
}

