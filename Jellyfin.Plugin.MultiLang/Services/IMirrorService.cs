using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MultiLang.Models;

namespace Jellyfin.Plugin.MultiLang.Services;

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
    /// Handles a file being added to a source library.
    /// </summary>
    /// <param name="sourceLibraryId">The source library ID.</param>
    /// <param name="filePath">The path of the added file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task representing the async operation.</returns>
    Task HandleFileAddedAsync(Guid sourceLibraryId, string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Handles a file being deleted from a source library.
    /// </summary>
    /// <param name="sourceLibraryId">The source library ID.</param>
    /// <param name="filePath">The path of the deleted file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task representing the async operation.</returns>
    Task HandleFileDeletedAsync(Guid sourceLibraryId, string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Handles a file being renamed in a source library.
    /// </summary>
    /// <param name="sourceLibraryId">The source library ID.</param>
    /// <param name="oldPath">The old file path.</param>
    /// <param name="newPath">The new file path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task representing the async operation.</returns>
    Task HandleFileRenamedAsync(Guid sourceLibraryId, string oldPath, string newPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all Jellyfin libraries with their metadata settings.
    /// </summary>
    /// <returns>Collection of library information.</returns>
    IEnumerable<LibraryInfo> GetJellyfinLibraries();
}

