using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.Polyglot.Helpers;
using Jellyfin.Plugin.Polyglot.Models;
using Jellyfin.Plugin.Polyglot.Services;
using MediaBrowser.Controller.Events;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Polyglot.EventConsumers;

/// <summary>
/// Handles user creation events to potentially assign language based on LDAP groups.
/// Uses IConfigurationService for all config access.
/// </summary>
public class UserCreatedConsumer : IEventConsumer<UserCreatedEventArgs>
{
    private readonly IUserLanguageService _userLanguageService;
    private readonly ILdapIntegrationService _ldapIntegrationService;
    private readonly IConfigurationService _configService;
    private readonly ILogger<UserCreatedConsumer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserCreatedConsumer"/> class.
    /// </summary>
    public UserCreatedConsumer(
        IUserLanguageService userLanguageService,
        ILdapIntegrationService ldapIntegrationService,
        IConfigurationService configService,
        ILogger<UserCreatedConsumer> logger)
    {
        _userLanguageService = userLanguageService;
        _ldapIntegrationService = ldapIntegrationService;
        _configService = configService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OnEvent(UserCreatedEventArgs eventArgs)
    {
        var user = eventArgs.Argument;
        var userEntity = new LogUser(user.Id, user.Username);
        _logger.PolyglotInfo("UserCreatedConsumer: User created: {0}", userEntity);

        var config = _configService.GetConfiguration();
        if (config == null)
        {
            _logger.PolyglotWarning("UserCreatedConsumer: Configuration not available");
            return;
        }

        // First, try LDAP-based assignment if enabled
        bool ldapLookupFailed = false;
        if (config.EnableLdapIntegration && _ldapIntegrationService.IsLdapPluginAvailable())
        {
            try
            {
                var languageId = await _ldapIntegrationService.DetermineLanguageFromGroupsAsync(
                    user.Username, CancellationToken.None).ConfigureAwait(false);

                if (languageId.HasValue)
                {
                    await _userLanguageService.AssignLanguageAsync(
                        user.Id,
                        languageId.Value,
                        "ldap",
                        manuallySet: false,
                        isPluginManaged: true,
                        CancellationToken.None).ConfigureAwait(false);

                    // Get language alternative for logging
                    var alt = _configService.GetAlternative(languageId.Value);
                    if (alt != null)
                    {
                        _logger.PolyglotInfo(
                            "UserCreatedConsumer: Assigned language {0} to new user {1} via LDAP",
                            new LogAlternative(alt.Id, alt.Name, alt.LanguageCode),
                            userEntity);
                    }
                    else
                    {
                        _logger.PolyglotInfo(
                            "UserCreatedConsumer: Assigned language {0} to new user {1} via LDAP",
                            _configService.CreateLogAlternative(languageId.Value),
                            userEntity);
                    }

                    return;
                }

                _logger.PolyglotDebug("UserCreatedConsumer: No LDAP group match for new user {0}", userEntity);
            }
            catch (Exception ex)
            {
                // LDAP lookup failed - behavior depends on FallbackOnLdapFailure setting
                _logger.PolyglotError(ex,
                    "UserCreatedConsumer: Failed to check LDAP for user {0}.",
                    userEntity);
                ldapLookupFailed = true;
            }
        }

        // Fall back to auto-manage if enabled, but check LDAP failure behavior setting
        // When FallbackOnLdapFailure is false, we don't auto-assign on LDAP failure
        // to avoid assigning the wrong language when LDAP should have determined it
        if (ldapLookupFailed && !config.FallbackOnLdapFailure)
        {
            _logger.PolyglotWarning(
                "UserCreatedConsumer: Skipping auto-assign for user {0} due to LDAP lookup failure. " +
                "Set 'FallbackOnLdapFailure' to true in settings to auto-assign despite LDAP errors.",
                userEntity);
            return;
        }

        if (config.AutoManageNewUsers)
        {
            try
            {
                // Log warning only when we're actually going to assign (AutoManageNewUsers is true)
                if (ldapLookupFailed && config.FallbackOnLdapFailure)
                {
                    _logger.PolyglotWarning(
                        "UserCreatedConsumer: LDAP lookup failed for user {0}, falling back to auto-assignment. " +
                        "Set 'FallbackOnLdapFailure' to false to require manual assignment on LDAP errors.",
                        userEntity);
                }

                await _userLanguageService.AssignLanguageAsync(
                    user.Id,
                    config.DefaultLanguageAlternativeId,
                    "auto",
                    manuallySet: false,
                    isPluginManaged: true,
                    CancellationToken.None).ConfigureAwait(false);

                // Get language alternative from fresh config lookup
                if (config.DefaultLanguageAlternativeId.HasValue)
                {
                    var alt = _configService.GetAlternative(config.DefaultLanguageAlternativeId.Value);
                    if (alt != null)
                    {
                        _logger.PolyglotInfo(
                            "UserCreatedConsumer: Auto-assigned {0} to new user {1}",
                            new LogAlternative(alt.Id, alt.Name, alt.LanguageCode),
                            userEntity);
                    }
                    else
                    {
                        _logger.PolyglotInfo(
                            "UserCreatedConsumer: Auto-assigned default language to new user {0}",
                            userEntity);
                    }
                }
                else
                {
                    _logger.PolyglotInfo(
                        "UserCreatedConsumer: Auto-assigned default libraries to new user {0}",
                        userEntity);
                }
            }
            catch (Exception ex)
            {
                _logger.PolyglotError(ex, "UserCreatedConsumer: Failed to auto-assign for user {0}", userEntity);
            }
        }
        else if (ldapLookupFailed && config.FallbackOnLdapFailure)
        {
            // FallbackOnLdapFailure is enabled (default) but AutoManageNewUsers is disabled
            // Log to clarify that no assignment occurred despite the fallback setting
            _logger.PolyglotDebug(
                "UserCreatedConsumer: User {0} was not auto-assigned because AutoManageNewUsers is disabled.",
                userEntity);
        }
    }
}
