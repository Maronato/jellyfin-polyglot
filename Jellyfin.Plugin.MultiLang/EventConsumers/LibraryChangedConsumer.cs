using System;
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
    private readonly ILogger<LibraryChangedConsumer> _logger;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryChangedConsumer"/> class.
    /// </summary>
    public LibraryChangedConsumer(
        ILibraryManager libraryManager,
        IMirrorService mirrorService,
        ILogger<LibraryChangedConsumer> logger)
    {
        _libraryManager = libraryManager;
        _mirrorService = mirrorService;
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
    /// Checks for and marks orphaned mirrors when a library is deleted.
    /// </summary>
    private void CheckForOrphanedMirrors()
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return;
        }

        var existingLibraryIds = _mirrorService.GetJellyfinLibraries()
            .Select(l => l.Id)
            .ToHashSet();

        var hasChanges = false;

        foreach (var alternative in config.LanguageAlternatives)
        {
            foreach (var mirror in alternative.MirroredLibraries)
            {
                // Check if source library still exists
                if (!existingLibraryIds.Contains(mirror.SourceLibraryId))
                {
                    _logger.LogWarning(
                        "Source library {SourceLibraryId} for mirror {MirrorId} no longer exists - marking as orphaned",
                        mirror.SourceLibraryId,
                        mirror.Id);

                    mirror.Status = SyncStatus.Error;
                    mirror.LastError = "Source library no longer exists";
                    hasChanges = true;
                }

                // Check if target library still exists (if it was created)
                if (mirror.TargetLibraryId.HasValue && !existingLibraryIds.Contains(mirror.TargetLibraryId.Value))
                {
                    _logger.LogWarning(
                        "Target library {TargetLibraryId} for mirror {MirrorId} no longer exists - will recreate on next sync",
                        mirror.TargetLibraryId.Value,
                        mirror.Id);

                    mirror.TargetLibraryId = null;
                    mirror.Status = SyncStatus.Pending;
                    hasChanges = true;
                }
            }
        }

        if (hasChanges)
        {
            Plugin.Instance?.SaveConfiguration();
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
