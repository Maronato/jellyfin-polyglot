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
/// Handles user creation events to potentially assign default language.
/// Uses IConfigurationService for all config access.
/// </summary>
public class UserCreatedConsumer : IEventConsumer<UserCreatedEventArgs>
{
    private readonly IUserLanguageService _userLanguageService;
    private readonly IConfigurationService _configService;
    private readonly ILogger<UserCreatedConsumer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserCreatedConsumer"/> class.
    /// </summary>
    public UserCreatedConsumer(
        IUserLanguageService userLanguageService,
        IConfigurationService configService,
        ILogger<UserCreatedConsumer> logger)
    {
        _userLanguageService = userLanguageService;
        _configService = configService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OnEvent(UserCreatedEventArgs eventArgs)
    {
        var user = eventArgs.Argument;
        var userEntity = new LogUser(user.Id, user.Username);
        _logger.PolyglotInfo("UserCreatedConsumer: User created: {0}", userEntity);

        // Get config values in one atomic read
        var (autoManageNewUsers, defaultLanguageAlternativeId) = _configService.Read(c =>
            (c.AutoManageNewUsers, c.DefaultLanguageAlternativeId));

        if (autoManageNewUsers)
        {
            try
            {
                await _userLanguageService.AssignLanguageAsync(
                    user.Id,
                    defaultLanguageAlternativeId,
                    "auto",
                    manuallySet: false,
                    isPluginManaged: true,
                    CancellationToken.None).ConfigureAwait(false);

                // Get language alternative from fresh config lookup
                if (defaultLanguageAlternativeId.HasValue)
                {
                    var alt = _configService.Read(c => c.LanguageAlternatives.FirstOrDefault(a => a.Id == defaultLanguageAlternativeId.Value));
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
    }
}
