using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Polyglot.Models;

namespace Jellyfin.Plugin.Polyglot.Services;

/// <summary>
/// Service for managing library mirroring operations.
/// All methods use IDs instead of object references to prevent stale configuration bugs.
/// </summary>
public interface IMirrorService
{
    /// <summary>
    /// Creates a new mirror for a library.
    /// Uses IDs to ensure fresh config lookup before each operation.
    /// </summary>
    /// <param name="alternativeId">The language alternative ID.</param>
    /// <param name="mirrorId">The mirror ID (must already be added to config).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task representing the async operation.</returns>
    Task CreateMirrorAsync(Guid alternativeId, Guid mirrorId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronizes an existing mirror with its source library.
    /// Uses ID to ensure fresh config lookup before each operation.
    /// </summary>
    /// <param name="mirrorId">The mirror ID to synchronize.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task representing the async operation.</returns>
    Task SyncMirrorAsync(Guid mirrorId, IProgress<double>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a mirror and optionally its Jellyfin library.
    /// Uses ID to ensure fresh config lookup.
    /// This method also removes the mirror from configuration - callers do not need to call RemoveMirror separately.
    /// </summary>
    /// <param name="mirrorId">The mirror ID to delete.</param>
    /// <param name="deleteLibrary">Whether to also delete the Jellyfin library.</param>
    /// <param name="deleteFiles">Whether to also delete the mirror files.</param>
    /// <param name="forceConfigRemoval">
    /// When true, removes the mirror from configuration even if library/file deletion fails.
    /// Use this to unstick mirrors that cannot be deleted due to filesystem issues.
    /// When false (default), the mirror remains in config if deletion fails, allowing retry.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result indicating what was deleted and any errors encountered.</returns>
    Task<DeleteMirrorResult> DeleteMirrorAsync(Guid mirrorId, bool deleteLibrary = true, bool deleteFiles = true, bool forceConfigRemoval = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronizes all mirrors for a language alternative.
    /// Uses ID to ensure fresh config lookup.
    /// </summary>
    /// <param name="alternativeId">The language alternative ID.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result indicating what happened during the sync operation.</returns>
    Task<SyncAllResult> SyncAllMirrorsAsync(Guid alternativeId, IProgress<double>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates that a mirror can be created (same filesystem, valid paths, etc.).
    /// </summary>
    /// <param name="sourceLibraryId">The source library ID.</param>
    /// <param name="targetPath">The proposed target path.</param>
    /// <returns>Validation result with any error messages.</returns>
    (bool IsValid, string? ErrorMessage) ValidateMirrorConfiguration(Guid sourceLibraryId, string targetPath);

    /// <summary>
    /// Gets all Jellyfin libraries with their metadata settings.
    /// </summary>
    /// <returns>Collection of library information.</returns>
    IEnumerable<LibraryInfo> GetJellyfinLibraries();

    /// <summary>
    /// Cleans up orphaned mirrors where source or target library no longer exists.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result of the cleanup operation.</returns>
    Task<OrphanCleanupResult> CleanupOrphanedMirrorsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of orphan mirror cleanup operation.
/// </summary>
public class OrphanCleanupResult
{
    /// <summary>
    /// Gets or sets the list of cleaned up mirrors with their reasons.
    /// </summary>
    public List<string> CleanedUpMirrors { get; set; } = new();

    /// <summary>
    /// Gets or sets the list of mirrors that failed to clean up with their error messages.
    /// </summary>
    public List<string> FailedCleanups { get; set; } = new();

    /// <summary>
    /// Gets or sets the total number of mirrors cleaned up successfully.
    /// </summary>
    public int TotalCleaned { get; set; }

    /// <summary>
    /// Gets or sets the total number of mirrors that failed to clean up.
    /// </summary>
    public int TotalFailed { get; set; }

    /// <summary>
    /// Gets or sets the source library IDs that now have no mirrors.
    /// </summary>
    public HashSet<Guid> SourcesWithoutMirrors { get; set; } = new();
}

/// <summary>
/// Result of syncing all mirrors for a language alternative.
/// </summary>
public class SyncAllResult
{
    /// <summary>
    /// Gets or sets the status of the sync operation.
    /// </summary>
    public SyncAllStatus Status { get; set; }

    /// <summary>
    /// Gets or sets the number of mirrors that were synced successfully.
    /// </summary>
    public int MirrorsSynced { get; set; }

    /// <summary>
    /// Gets or sets the number of mirrors that failed to sync.
    /// </summary>
    public int MirrorsFailed { get; set; }

    /// <summary>
    /// Gets or sets the total number of mirrors that were attempted.
    /// </summary>
    public int TotalMirrors { get; set; }
}

/// <summary>
/// Status of a sync all operation.
/// </summary>
public enum SyncAllStatus
{
    /// <summary>
    /// Sync completed (all mirrors synced successfully).
    /// </summary>
    Completed,

    /// <summary>
    /// Sync completed with some failures.
    /// </summary>
    CompletedWithErrors,

    /// <summary>
    /// The language alternative was not found.
    /// </summary>
    AlternativeNotFound,

    /// <summary>
    /// The operation was cancelled.
    /// </summary>
    Cancelled
}

/// <summary>
/// Result of deleting a mirror.
/// </summary>
public class DeleteMirrorResult
{
    /// <summary>
    /// Gets or sets a value indicating whether the mirror was removed from configuration.
    /// </summary>
    public bool RemovedFromConfig { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the Jellyfin library was deleted.
    /// </summary>
    public bool LibraryDeleted { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the mirror files were deleted.
    /// </summary>
    public bool FilesDeleted { get; set; }

    /// <summary>
    /// Gets or sets the error message if library deletion failed.
    /// </summary>
    public string? LibraryDeletionError { get; set; }

    /// <summary>
    /// Gets or sets the error message if file deletion failed.
    /// </summary>
    public string? FileDeletionError { get; set; }

    /// <summary>
    /// Gets a value indicating whether all requested operations succeeded.
    /// </summary>
    public bool FullySuccessful => RemovedFromConfig &&
        string.IsNullOrEmpty(LibraryDeletionError) &&
        string.IsNullOrEmpty(FileDeletionError);

    /// <summary>
    /// Gets a value indicating whether any errors occurred during deletion.
    /// </summary>
    public bool HasErrors => !string.IsNullOrEmpty(LibraryDeletionError) ||
        !string.IsNullOrEmpty(FileDeletionError);
}
