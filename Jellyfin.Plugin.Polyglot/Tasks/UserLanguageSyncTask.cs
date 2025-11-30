using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Polyglot.Helpers;
using Jellyfin.Plugin.Polyglot.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Polyglot.Tasks;

/// <summary>
/// Scheduled task that reconciles user library access with their language assignments.
/// Uses IConfigurationService for config access.
/// </summary>
public class UserLanguageSyncTask : IScheduledTask
{
    private readonly ILibraryAccessService _libraryAccessService;
    private readonly IUserLanguageService _userLanguageService;
    private readonly IConfigurationService _configService;
    private readonly ILogger<UserLanguageSyncTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserLanguageSyncTask"/> class.
    /// </summary>
    public UserLanguageSyncTask(
        ILibraryAccessService libraryAccessService,
        IUserLanguageService userLanguageService,
        IConfigurationService configService,
        ILogger<UserLanguageSyncTask> logger)
    {
        _libraryAccessService = libraryAccessService;
        _userLanguageService = userLanguageService;
        _configService = configService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Polyglot User Library Sync";

    /// <inheritdoc />
    public string Key => "PolyglotUserSync";

    /// <inheritdoc />
    public string Description => "Reconciles user library access permissions with their language assignments.";

    /// <inheritdoc />
    public string Category => "Polyglot";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Parse the configured time (default 3:00 AM)
        var config = _configService.GetConfiguration();
        var timeString = config?.UserReconciliationTime ?? "03:00";

        if (!TimeSpan.TryParse(timeString, out var time))
        {
            time = new TimeSpan(3, 0, 0);
        }

        return new[]
        {
            JellyfinCompatibility.CreateDailyTrigger(time.Ticks)
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.PolyglotInfo("UserLanguageSyncTask: Starting user sync");

        var config = _configService.GetConfiguration();
        if (config == null)
        {
            _logger.PolyglotWarning("UserLanguageSyncTask: Configuration not available");
            return;
        }

        SafeReportProgress(progress, 0);

        try
        {
            // Get all users with language assignments
            var users = _userLanguageService.GetAllUsersWithLanguages();
            var userList = new List<Models.UserInfo>(users);
            var totalUsers = userList.Count;
            var processedUsers = 0;
            var reconciledUsers = 0;

            _logger.PolyglotDebug("UserLanguageSyncTask: Processing {0} users", totalUsers);

            foreach (var user in userList)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    // Reconcile all plugin-managed users
                    if (user.IsPluginManaged)
                    {
                        var wasReconciled = await _libraryAccessService.ReconcileUserAccessAsync(user.Id, cancellationToken)
                            .ConfigureAwait(false);

                        if (wasReconciled)
                        {
                            reconciledUsers++;
                            _logger.PolyglotInfo("UserLanguageSyncTask: Reconciled access for user {0}", user.Username);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.PolyglotError(ex, "UserLanguageSyncTask: Failed to reconcile user {0}", user.Username);
                }

                processedUsers++;
                if (totalUsers > 0)
                {
                    SafeReportProgress(progress, (double)processedUsers / totalUsers * 100);
                }
            }

            _logger.PolyglotInfo(
                "UserLanguageSyncTask: Completed - {0} users processed, {1} reconciled",
                totalUsers,
                reconciledUsers);
        }
        catch (Exception ex)
        {
            _logger.PolyglotError(ex, "UserLanguageSyncTask: Failed");
            throw;
        }

        SafeReportProgress(progress, 100);
    }

    /// <summary>
    /// Safely reports progress without throwing if the callback fails.
    /// </summary>
    private void SafeReportProgress(IProgress<double> progress, double value)
    {
        try
        {
            progress.Report(value);
        }
        catch (Exception ex)
        {
            _logger.PolyglotDebug("UserLanguageSyncTask: Progress callback failed: {0}", ex.Message);
        }
    }
}
