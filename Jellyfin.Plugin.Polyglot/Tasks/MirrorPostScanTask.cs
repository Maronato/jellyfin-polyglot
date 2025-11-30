using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Polyglot.Helpers;
using Jellyfin.Plugin.Polyglot.Services;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Polyglot.Tasks;

/// <summary>
/// Post-scan task that synchronizes mirrors after library scans complete.
/// Uses IConfigurationService for config access and IDs for mirror operations.
/// </summary>
public class MirrorPostScanTask : ILibraryPostScanTask
{
    private readonly IMirrorService _mirrorService;
    private readonly IConfigurationService _configService;
    private readonly ILogger<MirrorPostScanTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MirrorPostScanTask"/> class.
    /// </summary>
    public MirrorPostScanTask(
        IMirrorService mirrorService,
        IConfigurationService configService,
        ILogger<MirrorPostScanTask> logger)
    {
        _mirrorService = mirrorService;
        _configService = configService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = _configService.GetConfiguration();
        if (config == null)
        {
            _logger.PolyglotWarning("MirrorPostScanTask: Configuration not available");
            return;
        }

        // Check if auto-sync after library scans is enabled
        if (!config.SyncMirrorsAfterLibraryScan)
        {
            _logger.PolyglotDebug("MirrorPostScanTask: Auto-sync disabled, skipping");
            return;
        }

        _logger.PolyglotInfo("MirrorPostScanTask: Library scan completed, syncing mirrors");

        // Get alternative IDs (not objects) for iteration
        var alternatives = _configService.GetAlternatives();
        var alternativeIds = alternatives
            .Where(a => a.MirroredLibraries.Count > 0) // Only sync alternatives with mirrors
            .Select(a => a.Id)
            .ToList();

        if (alternativeIds.Count == 0)
        {
            _logger.PolyglotDebug("MirrorPostScanTask: No alternatives with mirrors to sync");
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
                completedAlternatives++;
                continue;
            }

            _logger.PolyglotDebug("MirrorPostScanTask: Post-scan sync for alternative: {0}", alternative.Name);

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
                    _logger.PolyglotWarning("MirrorPostScanTask: Alternative {0} was deleted during sync", alternative.Name);
                }
                else if (result.MirrorsFailed > 0)
                {
                    _logger.PolyglotWarning("MirrorPostScanTask: Alternative {0} synced with {1} failures out of {2} mirrors",
                        alternative.Name, result.MirrorsFailed, result.TotalMirrors);
                }
            }
            catch (Exception ex)
            {
                _logger.PolyglotError(ex, "MirrorPostScanTask: Failed to sync alternative: {0}", alternative.Name);
            }

            completedAlternatives++;
        }

        SafeReportProgress(progress, 100);
        _logger.PolyglotInfo("MirrorPostScanTask: Completed");
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
            _logger.PolyglotDebug("MirrorPostScanTask: Progress callback failed: {0}", ex.Message);
        }
    }
}
