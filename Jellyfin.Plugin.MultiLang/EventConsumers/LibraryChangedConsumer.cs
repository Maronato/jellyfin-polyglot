using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MultiLang.Models;
using Jellyfin.Plugin.MultiLang.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MultiLang.EventConsumers;

/// <summary>
/// Handles library item changes to track library lifecycle and detect orphaned mirrors.
/// Real-time sync is handled by ILibraryPostScanTask, this consumer focuses on
/// detecting when libraries are deleted so mirrors can be marked as orphaned.
/// </summary>
public class LibraryChangedConsumer : IHostedService, IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly IMirrorService _mirrorService;
    private readonly ILibraryAccessService _libraryAccessService;
    private readonly ILogger<LibraryChangedConsumer> _logger;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryChangedConsumer"/> class.
    /// </summary>
    public LibraryChangedConsumer(
        ILibraryManager libraryManager,
        IMirrorService mirrorService,
        ILibraryAccessService libraryAccessService,
        ILogger<LibraryChangedConsumer> logger)
    {
        _libraryManager = libraryManager;
        _mirrorService = mirrorService;
        _libraryAccessService = libraryAccessService;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        _libraryManager.ItemRemoved += OnItemRemoved;

        _logger.LogInformation("Library change consumer initialized");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _libraryManager.ItemRemoved -= OnItemRemoved;

        _logger.LogInformation("Library change consumer stopped");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles item added events.
    /// Note: Real-time sync is handled by ILibraryPostScanTask after library scans complete.
    /// This is here for logging/debugging purposes.
    /// </summary>
    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item == null)
        {
            return;
        }

        // We mainly care about media files, not metadata
        if (!IsMediaItem(e.Item))
        {
            return;
        }

        var libraryId = GetLibraryId(e.Item);
        if (!libraryId.HasValue)
        {
            return;
        }

        _logger.LogDebug("Item added in library {LibraryId}: {ItemName}", libraryId.Value, e.Item.Name);

        // Note: Sync is handled by ILibraryPostScanTask after the library scan completes.
        // This provides better batching and integrates with Jellyfin's native scanning.
    }

    /// <summary>
    /// Handles item removed events.
    /// </summary>
    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item == null)
        {
            return;
        }

        // Check if this is a virtual folder (library) being removed
        if (e.Item is AggregateFolder)
        {
            CheckForOrphanedMirrors();
        }
    }

    /// <summary>
    /// Checks for and cleans up orphaned mirrors when a library is deleted.
    /// - If source library is deleted: Delete the entire mirror (config + files)
    /// - If mirror library is deleted: Remove the mirror config
    /// </summary>
    private void CheckForOrphanedMirrors()
    {
        // Run async cleanup in background
        _ = Task.Run(async () =>
        {
            try
            {
                await CleanupOrphanedMirrorsAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cleanup orphaned mirrors");
            }
        });
    }

    /// <summary>
    /// Async implementation of orphaned mirror cleanup.
    /// </summary>
    private async Task CleanupOrphanedMirrorsAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return;
        }

        var existingLibraryIds = _mirrorService.GetJellyfinLibraries()
            .Select(l => l.Id)
            .ToHashSet();

        var mirrorsToDelete = new List<(LanguageAlternative Alternative, LibraryMirror Mirror, string Reason)>();

        foreach (var alternative in config.LanguageAlternatives)
        {
            foreach (var mirror in alternative.MirroredLibraries)
            {
                // Check if source library still exists
                if (!existingLibraryIds.Contains(mirror.SourceLibraryId))
                {
                    _logger.LogWarning(
                        "Source library {SourceLibraryId} for mirror {MirrorId} no longer exists - will delete mirror",
                        mirror.SourceLibraryId,
                        mirror.Id);

                    mirrorsToDelete.Add((alternative, mirror, "source deleted"));
                    continue;
                }

                // Check if target library still exists (if it was created)
                if (mirror.TargetLibraryId.HasValue && !existingLibraryIds.Contains(mirror.TargetLibraryId.Value))
                {
                    _logger.LogWarning(
                        "Target library {TargetLibraryId} for mirror {MirrorId} no longer exists - removing mirror config",
                        mirror.TargetLibraryId.Value,
                        mirror.Id);

                    mirrorsToDelete.Add((alternative, mirror, "mirror deleted"));
                }
            }
        }

        // Delete orphaned mirrors
        foreach (var (alternative, mirror, reason) in mirrorsToDelete)
        {
            try
            {
                // Delete files only if source was deleted (files are now orphaned)
                // If only the mirror library was deleted, the source still has the files
                var deleteFiles = reason == "source deleted";

                // Don't try to delete Jellyfin library - it's already gone
                await _mirrorService.DeleteMirrorAsync(mirror, deleteLibrary: false, deleteFiles: deleteFiles, cancellationToken)
                    .ConfigureAwait(false);

                // Remove from alternative's mirror list
                alternative.MirroredLibraries.Remove(mirror);

                _logger.LogInformation(
                    "Removed orphaned mirror {MirrorName} ({Reason})",
                    mirror.TargetLibraryName,
                    reason);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete orphaned mirror {MirrorId}", mirror.Id);
            }
        }

        if (mirrorsToDelete.Count > 0)
        {
            Plugin.Instance?.SaveConfiguration();

            // Reconcile user access after cleanup
            try
            {
                await _libraryAccessService.ReconcileAllUsersAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Reconciled user access after cleaning up {Count} orphaned mirrors", mirrorsToDelete.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reconcile user access after cleanup");
            }
        }
    }

    /// <summary>
    /// Checks if an item is a media item (video, audio, etc.).
    /// </summary>
    private static bool IsMediaItem(BaseItem item)
    {
        return item is MediaBrowser.Controller.Entities.Video
            || item is MediaBrowser.Controller.Entities.Audio.Audio
            || item is MediaBrowser.Controller.Entities.Movies.Movie
            || item is MediaBrowser.Controller.Entities.TV.Episode
            || item is MediaBrowser.Controller.Entities.TV.Series;
    }

    /// <summary>
    /// Gets the library ID for an item.
    /// </summary>
    private static Guid? GetLibraryId(BaseItem item)
    {
        var parent = item;
        while (parent != null)
        {
            if (parent is AggregateFolder folder)
            {
                return folder.Id;
            }

            parent = parent.GetParent();
        }

        return null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases unmanaged and optionally managed resources.
    /// </summary>
    /// <param name="disposing">True to release both managed and unmanaged resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
    }
}
