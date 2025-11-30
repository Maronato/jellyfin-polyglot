using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Polyglot.Helpers;
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
/// Uses IConfigurationService for all config access.
/// </summary>
public class LibraryChangedConsumer : IHostedService, IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly IMirrorService _mirrorService;
    private readonly ILibraryAccessService _libraryAccessService;
    private readonly IConfigurationService _configService;
    private readonly ILogger<LibraryChangedConsumer> _logger;
    private CancellationTokenSource? _shutdownCts;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryChangedConsumer"/> class.
    /// </summary>
    public LibraryChangedConsumer(
        ILibraryManager libraryManager,
        IMirrorService mirrorService,
        ILibraryAccessService libraryAccessService,
        IConfigurationService configService,
        ILogger<LibraryChangedConsumer> logger)
    {
        _libraryManager = libraryManager;
        _mirrorService = mirrorService;
        _libraryAccessService = libraryAccessService;
        _configService = configService;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _shutdownCts = new CancellationTokenSource();
        _libraryManager.ItemRemoved += OnItemRemoved;

        _logger.PolyglotInfo("LibraryChangedConsumer: Initialized");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemRemoved -= OnItemRemoved;

        // Cancel any pending cleanup tasks to prevent accessing disposed services
        try
        {
            _shutdownCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed, ignore
        }

        _logger.PolyglotInfo("LibraryChangedConsumer: Stopped");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles item removed events.
    /// Note: This runs synchronously in the event handler to avoid Task.Run race conditions.
    /// The cleanup operations are designed to be quick and safe.
    /// </summary>
    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item == null)
        {
            return;
        }

        // Check if this is a library-related folder being removed
        if (e.Item is CollectionFolder || e.Item is AggregateFolder)
        {
            _logger.PolyglotDebug("LibraryChangedConsumer: Library folder removed: {0} (Type: {1})",
                e.Item.Name, e.Item.GetType().Name);

            // Get cancellation token - if shutdown has started or CTS is null, skip scheduling
            var shutdownToken = _shutdownCts?.Token ?? CancellationToken.None;
            if (shutdownToken.IsCancellationRequested)
            {
                _logger.PolyglotDebug("LibraryChangedConsumer: Skipping cleanup scheduling - shutdown in progress");
                return;
            }

            // Schedule cleanup via a timer to allow Jellyfin to finish processing
            // This avoids blocking the event handler while still avoiding Task.Run races
            // Use async delegate with Unwrap() to properly observe the inner task's exceptions
            // Wrap entire chain in try-catch to handle any scheduling failures
            try
            {
                _ = Task.Delay(500, shutdownToken).ContinueWith(
                    async _ =>
                    {
                        // Re-check cancellation before doing work
                        if (shutdownToken.IsCancellationRequested)
                        {
                            _logger.PolyglotDebug("LibraryChangedConsumer: Cleanup cancelled - shutdown in progress");
                            return;
                        }

                        try
                        {
                            await CleanupOrphanedMirrorsAsync(shutdownToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            _logger.PolyglotDebug("LibraryChangedConsumer: Cleanup cancelled during shutdown");
                        }
                        catch (Exception ex)
                        {
                            // Log any exceptions that would otherwise be silently swallowed
                            _logger.PolyglotError(ex, "LibraryChangedConsumer: Scheduled cleanup failed");
                        }
                    },
                    shutdownToken,
                    TaskContinuationOptions.OnlyOnRanToCompletion,
                    TaskScheduler.Default).Unwrap();
            }
            catch (OperationCanceledException)
            {
                // Task.Delay was cancelled - shutdown in progress
                _logger.PolyglotDebug("LibraryChangedConsumer: Cleanup scheduling cancelled - shutdown in progress");
            }
            catch (Exception ex)
            {
                // Extremely rare: Task.Delay or TaskScheduler failed
                _logger.PolyglotError(ex, "LibraryChangedConsumer: Failed to schedule cleanup task");
            }
        }
    }

    /// <summary>
    /// Cleans up orphaned mirrors using the shared service method.
    /// </summary>
    private async Task CleanupOrphanedMirrorsAsync(CancellationToken cancellationToken)
    {
        _logger.PolyglotDebug("LibraryChangedConsumer: Starting orphan cleanup");

        try
        {
            var result = await _mirrorService.CleanupOrphanedMirrorsAsync(cancellationToken).ConfigureAwait(false);

            if (result.TotalCleaned == 0 && result.TotalFailed == 0)
            {
                _logger.PolyglotDebug("LibraryChangedConsumer: No orphaned mirrors found");
                return;
            }

            if (result.TotalFailed > 0)
            {
                _logger.PolyglotWarning(
                    "LibraryChangedConsumer: Cleaned up {0} orphaned mirrors, {1} failed to clean up",
                    result.TotalCleaned, result.TotalFailed);
            }
            else
            {
                _logger.PolyglotInfo("LibraryChangedConsumer: Cleaned up {0} orphaned mirrors", result.TotalCleaned);
            }

            // Ensure users have access to sources that no longer have mirrors
            if (result.SourcesWithoutMirrors.Count > 0)
            {
                var userLanguages = _configService.GetUserLanguages();
                foreach (var userConfig in userLanguages.Where(u => u.IsPluginManaged))
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
                        _logger.PolyglotError(ex, "LibraryChangedConsumer: Failed to add sources to user {0}",
                            userConfig.UserId);
                    }
                }
            }

            // Reconcile user access after cleanup
            try
            {
                await _libraryAccessService.ReconcileAllUsersAsync(cancellationToken).ConfigureAwait(false);
                _logger.PolyglotInfo("LibraryChangedConsumer: Reconciled user access after cleanup");
            }
            catch (Exception ex)
            {
                _logger.PolyglotError(ex, "LibraryChangedConsumer: Failed to reconcile user access");
            }
        }
        catch (Exception ex)
        {
            _logger.PolyglotError(ex, "LibraryChangedConsumer: Failed to cleanup orphaned mirrors");
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

        if (disposing)
        {
            _shutdownCts?.Cancel();
            _shutdownCts?.Dispose();
            _shutdownCts = null;
        }

        _disposed = true;
    }
}
