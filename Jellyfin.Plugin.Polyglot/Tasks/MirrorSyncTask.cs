using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Polyglot.Helpers;
using Jellyfin.Plugin.Polyglot.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Polyglot.Tasks;

/// <summary>
/// Scheduled task that synchronizes all library mirrors.
/// Uses IConfigurationService for config access and IDs for mirror operations.
/// </summary>
public class MirrorSyncTask : IScheduledTask
{
    private readonly IMirrorService _mirrorService;
    private readonly IConfigurationService _configService;
    private readonly ILogger<MirrorSyncTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MirrorSyncTask"/> class.
    /// </summary>
    public MirrorSyncTask(
        IMirrorService mirrorService,
        IConfigurationService configService,
        ILogger<MirrorSyncTask> logger)
    {
        _mirrorService = mirrorService;
        _configService = configService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Polyglot Mirror Sync";

    /// <inheritdoc />
    public string Key => "PolyglotMirrorSync";

    /// <inheritdoc />
    public string Description => "Synchronizes all language mirror libraries with their source libraries.";

    /// <inheritdoc />
    public string Category => "Polyglot";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            JellyfinCompatibility.CreateIntervalTrigger(TimeSpan.FromHours(6).Ticks)
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.PolyglotInfo("MirrorSyncTask: Starting mirror sync");

        // First, cleanup any orphaned mirrors
        try
        {
            var cleanupResult = await _mirrorService.CleanupOrphanedMirrorsAsync(cancellationToken).ConfigureAwait(false);
            if (cleanupResult.TotalCleaned > 0)
            {
                _logger.PolyglotInfo("MirrorSyncTask: Cleaned up {0} orphaned mirrors", cleanupResult.TotalCleaned);
            }
        }
        catch (Exception ex)
        {
            _logger.PolyglotWarning(ex, "MirrorSyncTask: Failed to cleanup orphaned mirrors, continuing");
        }

        // Get alternative IDs (not objects) for iteration
        var alternatives = _configService.GetAlternatives();
        var alternativeIds = alternatives.Select(a => a.Id).ToList();

        if (alternativeIds.Count == 0)
        {
            _logger.PolyglotInfo("MirrorSyncTask: No language alternatives configured");
            return;
        }

        var totalAlternatives = alternativeIds.Count;
        var completedAlternatives = 0;

        foreach (var alternativeId in alternativeIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Get fresh alternative data for logging
            var alternative = _configService.GetAlternative(alternativeId);
            if (alternative == null)
            {
                _logger.PolyglotDebug("MirrorSyncTask: Alternative {0} no longer exists, skipping", alternativeId);
                completedAlternatives++;
                continue;
            }

            _logger.PolyglotInfo("MirrorSyncTask: Syncing mirrors for alternative: {0}", alternative.Name);

            var altProgress = new Progress<double>(p =>
            {
                var overallProgress = ((completedAlternatives * 100.0) + p) / totalAlternatives;
                SafeReportProgress(progress, overallProgress);
            });

            try
            {
                // Use ID instead of object reference
                var result = await _mirrorService.SyncAllMirrorsAsync(alternativeId, altProgress, cancellationToken).ConfigureAwait(false);

                if (result.Status == SyncAllStatus.AlternativeNotFound)
                {
                    _logger.PolyglotWarning("MirrorSyncTask: Alternative {0} was deleted during sync", alternative.Name);
                }
                else if (result.MirrorsFailed > 0)
                {
                    _logger.PolyglotWarning("MirrorSyncTask: Alternative {0} synced with {1} failures out of {2} mirrors",
                        alternative.Name, result.MirrorsFailed, result.TotalMirrors);
                }
            }
            catch (Exception ex)
            {
                _logger.PolyglotError(ex, "MirrorSyncTask: Failed to sync alternative: {0}", alternative.Name);
            }

            completedAlternatives++;
        }

        SafeReportProgress(progress, 100);
        _logger.PolyglotInfo("MirrorSyncTask: Completed");
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
            _logger.PolyglotDebug("MirrorSyncTask: Progress callback failed: {0}", ex.Message);
        }
    }
}
