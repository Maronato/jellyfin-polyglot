using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Polyglot.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Polyglot.Tasks;

/// <summary>
/// Scheduled task that synchronizes all library mirrors.
/// </summary>
public class MirrorSyncTask : IScheduledTask
{
    private readonly IMirrorService _mirrorService;
    private readonly ILogger<MirrorSyncTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MirrorSyncTask"/> class.
    /// </summary>
    public MirrorSyncTask(IMirrorService mirrorService, ILogger<MirrorSyncTask> logger)
    {
        _mirrorService = mirrorService;
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
        var intervalHours = Plugin.Instance?.Configuration.MirrorSyncIntervalHours ?? 6;

        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerInterval,
                IntervalTicks = TimeSpan.FromHours(intervalHours).Ticks
            }
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting mirror sync task");

        // First, cleanup any orphaned mirrors (e.g., if a library was deleted externally)
        // This is important because the ItemRemoved event may not fire when a library is
        // deleted via Jellyfin's UI (ValidateTopLibraryFolders deletes directly from DB)
        try
        {
            var cleanupResult = await _mirrorService.CleanupOrphanedMirrorsAsync(cancellationToken).ConfigureAwait(false);
            if (cleanupResult.TotalCleaned > 0)
            {
                _logger.LogInformation("Cleaned up {Count} orphaned mirrors before sync", cleanupResult.TotalCleaned);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cleanup orphaned mirrors, continuing with sync");
        }

        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            _logger.LogWarning("Plugin configuration not available");
            return;
        }

        var alternatives = config.LanguageAlternatives;
        if (alternatives.Count == 0)
        {
            _logger.LogInformation("No language alternatives configured, nothing to sync");
            return;
        }

        var totalAlternatives = alternatives.Count;
        var completedAlternatives = 0;

        foreach (var alternative in alternatives)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _logger.LogInformation("Syncing mirrors for language alternative: {Name}", alternative.Name);

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
                _logger.LogError(ex, "Failed to sync mirrors for language alternative: {Name}", alternative.Name);
                // Continue with other alternatives
            }

            completedAlternatives++;
        }

        progress.Report(100);
        _logger.LogInformation("Mirror sync task completed");
    }
}

