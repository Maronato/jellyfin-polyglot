using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Polyglot.Helpers;
using Jellyfin.Plugin.Polyglot.Services;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Polyglot.Tasks;

/// <summary>
/// Post-scan task that synchronizes mirrors after library scans complete.
/// This integrates with Jellyfin's native library scanning mechanism.
/// </summary>
public class MirrorPostScanTask : ILibraryPostScanTask
{
    private readonly IMirrorService _mirrorService;
    private readonly ILogger<MirrorPostScanTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MirrorPostScanTask"/> class.
    /// </summary>
    public MirrorPostScanTask(IMirrorService mirrorService, ILogger<MirrorPostScanTask> logger)
    {
        _mirrorService = mirrorService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            _logger.PolyglotWarning("Plugin configuration not available");
            return;
        }

        // Check if auto-sync after library scans is enabled
        if (!config.SyncMirrorsAfterLibraryScan)
        {
            _logger.PolyglotDebug("Auto-sync after library scans is disabled, skipping");
            return;
        }

        _logger.PolyglotInfo("Library scan completed, syncing mirrors...");

        var alternatives = config.LanguageAlternatives;
        if (alternatives.Count == 0)
        {
            _logger.PolyglotDebug("No language alternatives configured, nothing to sync");
            return;
        }

        var totalAlternatives = alternatives.Count;
        var completedAlternatives = 0;

        foreach (var alternative in alternatives)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Only sync mirrors that have been created
            if (alternative.MirroredLibraries.Count == 0)
            {
                completedAlternatives++;
                continue;
            }

            _logger.PolyglotDebug("Post-scan sync for language alternative: {0}", alternative.Name);

            var altProgress = new Progress<double>(p =>
            {
                var overallProgress = ((completedAlternatives * 100.0) + p) / totalAlternatives;
                progress.Report(overallProgress);
            });

            try
            {
                await _mirrorService.SyncAllMirrorsAsync(alternative, altProgress, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.PolyglotError(ex, "Failed to sync mirrors for language alternative: {0}", alternative.Name);
                // Continue with other alternatives
            }

            completedAlternatives++;
        }

        progress.Report(100);
        _logger.PolyglotInfo("Post-scan mirror sync completed");
    }
}

