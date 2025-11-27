using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Polyglot.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Polyglot.EventConsumers;

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
        _libraryManager.ItemRemoved += OnItemRemoved;

        _logger.LogInformation("Library change consumer initialized");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemRemoved -= OnItemRemoved;

        _logger.LogInformation("Library change consumer stopped");
        return Task.CompletedTask;
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

        // Check if this is a library-related folder being removed
        // CollectionFolder = individual virtual folder (library) in Jellyfin
        // Note: When a library is deleted via RemoveVirtualFolder with refreshLibrary=true,
        // Jellyfin calls ValidateTopLibraryFolders which may delete the CollectionFolder
        // directly from the database without firing ItemRemoved. So this handler may not
        // always be called. Periodic cleanup via scheduled tasks is more reliable.
        if (e.Item is CollectionFolder || e.Item is AggregateFolder)
        {
            _logger.LogDebug("Library folder removed: {FolderName} (Type: {FolderType})", e.Item.Name, e.Item.GetType().Name);

            // Run async cleanup in background
            _ = Task.Run(async () =>
            {
                try
                {
                    // Small delay to ensure Jellyfin has finished processing the deletion
                    await Task.Delay(500).ConfigureAwait(false);
                    await CleanupOrphanedMirrorsAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to cleanup orphaned mirrors after library removal");
                }
            });
        }
    }

    /// <summary>
    /// Cleans up orphaned mirrors using the shared service method.
    /// </summary>
    private async Task CleanupOrphanedMirrorsAsync(CancellationToken cancellationToken)
    {
        var result = await _mirrorService.CleanupOrphanedMirrorsAsync(cancellationToken).ConfigureAwait(false);

        if (result.TotalCleaned == 0)
        {
            return;
        }

        _logger.LogInformation("Cleaned up {Count} orphaned mirrors", result.TotalCleaned);

        // Ensure users have access to sources that no longer have mirrors
        if (result.SourcesWithoutMirrors.Count > 0)
        {
            var config = Plugin.Instance?.Configuration;
            if (config != null)
            {
                foreach (var userConfig in config.UserLanguages.Where(u => u.IsPluginManaged))
                {
                    try
                    {
                        await _libraryAccessService.AddLibrariesToUserAccessAsync(
                            userConfig.UserId,
                            result.SourcesWithoutMirrors,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to add sources to user {UserId}", userConfig.UserId);
                    }
                }
            }
        }

        // Reconcile user access after cleanup
        try
        {
            await _libraryAccessService.ReconcileAllUsersAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Reconciled user access after cleanup");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reconcile user access after cleanup");
        }
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
