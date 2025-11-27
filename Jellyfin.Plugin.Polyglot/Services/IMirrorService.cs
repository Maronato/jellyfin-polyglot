using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Polyglot.Models;

namespace Jellyfin.Plugin.Polyglot.Services;

/// <summary>
/// Service for managing library mirroring operations.
/// </summary>
public interface IMirrorService
{
    /// <summary>
    /// Creates a new mirror for a library.
    /// </summary>
    /// <param name="alternative">The language alternative configuration.</param>
    /// <param name="mirror">The mirror configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task representing the async operation.</returns>
    Task CreateMirrorAsync(LanguageAlternative alternative, LibraryMirror mirror, CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronizes an existing mirror with its source library.
    /// </summary>
    /// <param name="mirror">The mirror to synchronize.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task representing the async operation.</returns>
    Task SyncMirrorAsync(LibraryMirror mirror, IProgress<double>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a mirror and optionally its Jellyfin library.
    /// </summary>
    /// <param name="mirror">The mirror to delete.</param>
    /// <param name="deleteLibrary">Whether to also delete the Jellyfin library.</param>
    /// <param name="deleteFiles">Whether to also delete the mirror files.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task representing the async operation.</returns>
    Task DeleteMirrorAsync(LibraryMirror mirror, bool deleteLibrary = true, bool deleteFiles = true, CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronizes all mirrors for a language alternative.
    /// </summary>
    /// <param name="alternative">The language alternative.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task representing the async operation.</returns>
    Task SyncAllMirrorsAsync(LanguageAlternative alternative, IProgress<double>? progress = null, CancellationToken cancellationToken = default);

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
    /// Gets or sets the total number of mirrors cleaned up.
    /// </summary>
    public int TotalCleaned { get; set; }

    /// <summary>
    /// Gets or sets the source library IDs that now have no mirrors.
    /// </summary>
    public HashSet<Guid> SourcesWithoutMirrors { get; set; } = new();
}

