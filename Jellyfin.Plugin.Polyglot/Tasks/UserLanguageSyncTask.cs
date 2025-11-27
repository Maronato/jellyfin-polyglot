using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Polyglot.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Polyglot.Tasks;

/// <summary>
/// Scheduled task that reconciles user library access with their language assignments.
/// </summary>
public class UserLanguageSyncTask : IScheduledTask
{
    private readonly ILibraryAccessService _libraryAccessService;
    private readonly IUserLanguageService _userLanguageService;
    private readonly ILogger<UserLanguageSyncTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserLanguageSyncTask"/> class.
    /// </summary>
    public UserLanguageSyncTask(
        ILibraryAccessService libraryAccessService,
        IUserLanguageService userLanguageService,
        ILogger<UserLanguageSyncTask> logger)
    {
        _libraryAccessService = libraryAccessService;
        _userLanguageService = userLanguageService;
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
        var config = Plugin.Instance?.Configuration;
        var timeString = config?.UserReconciliationTime ?? "03:00";

        if (!TimeSpan.TryParse(timeString, out var time))
        {
            time = new TimeSpan(3, 0, 0);
        }

        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerDaily,
                TimeOfDayTicks = time.Ticks
            }
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting user language sync task");

        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            _logger.LogWarning("Plugin configuration not available");
            return;
        }

        progress.Report(0);

        try
        {
            // Get all users with language assignments
            var users = _userLanguageService.GetAllUsersWithLanguages();
            var userList = new List<Models.UserInfo>(users);
            var totalUsers = userList.Count;
            var processedUsers = 0;
            var reconciledUsers = 0;

            foreach (var user in userList)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    // Reconcile all plugin-managed users (both with specific language and "Default")
                    if (user.IsPluginManaged)
                    {
                        var wasReconciled = await _libraryAccessService.ReconcileUserAccessAsync(user.Id, cancellationToken)
                            .ConfigureAwait(false);

                        if (wasReconciled)
                        {
                            reconciledUsers++;
                            _logger.LogInformation("Reconciled library access for user {Username}", user.Username);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to reconcile user {Username} ({UserId})", user.Username, user.Id);
                }

                processedUsers++;
                progress.Report((double)processedUsers / totalUsers * 100);
            }

            _logger.LogInformation(
                "User language sync completed: {Total} users processed, {Reconciled} reconciled",
                totalUsers,
                reconciledUsers);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "User language sync task failed");
            throw;
        }

        progress.Report(100);
    }
}

