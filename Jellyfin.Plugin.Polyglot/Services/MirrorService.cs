using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Polyglot.Helpers;
using Jellyfin.Plugin.Polyglot.Models;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

// Aliases for log entity types to avoid conflict with model types
using LogAlternativeEntity = Jellyfin.Plugin.Polyglot.Models.LogAlternative;
using LogMirrorEntity = Jellyfin.Plugin.Polyglot.Models.LogMirror;
using LogLibraryEntity = Jellyfin.Plugin.Polyglot.Models.LogLibrary;
using LogPathEntity = Jellyfin.Plugin.Polyglot.Models.LogPath;

namespace Jellyfin.Plugin.Polyglot.Services;

/// <summary>
/// Service implementation for managing library mirroring operations.
/// Uses IConfigurationService for all config modifications to prevent stale reference bugs.
/// </summary>
public class MirrorService : IMirrorService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly IConfigurationService _configService;
    private readonly ILogger<MirrorService> _logger;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _mirrorLocks = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="MirrorService"/> class.
    /// </summary>
    public MirrorService(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        IConfigurationService configService,
        ILogger<MirrorService> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _configService = configService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task CreateMirrorAsync(Guid alternativeId, Guid mirrorId, CancellationToken cancellationToken = default)
    {
        _logger.PolyglotDebug("CreateMirrorAsync: Starting for alternative {0}, mirror {1}",
            _configService.CreateLogAlternative(alternativeId),
            _configService.CreateLogMirror(mirrorId));

        // Get fresh snapshot of mirror data for the operation
        var mirrorData = _configService.GetMirrorWithAlternative(mirrorId);
        if (mirrorData == null)
        {
            _logger.PolyglotWarning("CreateMirrorAsync: Mirror {0} not found in configuration",
                _configService.CreateLogMirror(mirrorId));
            throw new InvalidOperationException($"Mirror {mirrorId} not found in configuration");
        }

        var (mirror, actualAlternativeId) = mirrorData.Value;
        var mirrorEntity = new LogMirrorEntity(mirrorId, mirror.SourceLibraryName, mirror.TargetLibraryName);

        // Validate the provided alternativeId matches the mirror's actual parent
        // Throw if mismatched to expose programming errors in callers rather than silently correcting
        if (actualAlternativeId != alternativeId)
        {
            _logger.PolyglotError(
                "CreateMirrorAsync: Mirror {0} belongs to alternative {1}, not provided alternative {2}. " +
                "This indicates a programming error in the caller.",
                mirrorEntity,
                _configService.CreateLogAlternative(actualAlternativeId),
                _configService.CreateLogAlternative(alternativeId));
            throw new ArgumentException(
                $"Mirror {mirrorId} belongs to alternative {actualAlternativeId}, not {alternativeId}. " +
                "This indicates a programming error - the caller provided an incorrect alternative ID.",
                nameof(alternativeId));
        }

        var alternative = _configService.GetAlternative(alternativeId);
        if (alternative == null)
        {
            _logger.PolyglotWarning("CreateMirrorAsync: Alternative {0} not found",
                _configService.CreateLogAlternative(alternativeId));
            throw new InvalidOperationException($"Alternative {alternativeId} not found");
        }

        _logger.PolyglotInfo("CreateMirrorAsync: Creating mirror for library {0} to {1}",
            new LogLibraryEntity(mirror.SourceLibraryId, mirror.SourceLibraryName),
            new LogPathEntity(mirror.TargetPath, "target"));

        var mirrorLock = _mirrorLocks.GetOrAdd(mirrorId, _ => new SemaphoreSlim(1, 1));
        await mirrorLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        // Track whether we created the directory - used for cleanup decisions on failure
        bool createdTargetDirectory = false;
        // Track whether the directory was empty when we started (validated empty directories are safe to clean)
        bool directoryWasEmpty = false;

        try
        {
            // Update status to syncing atomically
            _configService.UpdateMirror(mirrorId, m => m.Status = SyncStatus.Syncing);

            // Re-fetch mirror data after status update to get current state
            var currentMirror = _configService.GetMirror(mirrorId);
            if (currentMirror == null)
            {
                throw new InvalidOperationException($"Mirror {mirrorId} was removed during operation");
            }

            // Get source library paths
            var sourceLibrary = GetVirtualFolderById(currentMirror.SourceLibraryId);
            if (sourceLibrary == null)
            {
                throw new InvalidOperationException($"Source library {currentMirror.SourceLibraryId} not found");
            }

            var sourcePaths = sourceLibrary.Locations;
            if (sourcePaths == null || sourcePaths.Length == 0)
            {
                throw new InvalidOperationException($"Source library {currentMirror.SourceLibraryName} has no paths");
            }

            // Validate filesystem compatibility
            foreach (var sourcePath in sourcePaths)
            {
                if (!FileSystemHelper.AreOnSameFilesystem(sourcePath, currentMirror.TargetPath))
                {
                    throw new InvalidOperationException(
                        $"Source path {sourcePath} and target path {currentMirror.TargetPath} are on different filesystems. " +
                        "Hardlinks require both paths to be on the same filesystem.");
                }
            }

            // Create target directory if it doesn't exist
            // Track state for cleanup decisions on failure
            if (!Directory.Exists(currentMirror.TargetPath))
            {
                Directory.CreateDirectory(currentMirror.TargetPath);
                createdTargetDirectory = true;
                directoryWasEmpty = true; // New directory is by definition empty
                _logger.PolyglotDebug("CreateMirrorAsync: Created target directory {0}",
                    new LogPathEntity(currentMirror.TargetPath, "target"));
            }
            else
            {
                // Directory exists - check if it's empty (validation should have ensured this)
                // If empty, we can safely clean up any files we create on failure
                try
                {
                    directoryWasEmpty = !Directory.EnumerateFileSystemEntries(currentMirror.TargetPath).Any();
                }
                catch
                {
                    // If we can't check, assume not empty to be safe
                    directoryWasEmpty = false;
                }

                _logger.PolyglotDebug("CreateMirrorAsync: Target directory already exists {0} (empty: {1})",
                    new LogPathEntity(currentMirror.TargetPath, "target"), directoryWasEmpty);
            }

            // Mirror each source path
            int fileCount = 0;
            foreach (var sourcePath in sourcePaths)
            {
                fileCount += await MirrorDirectoryAsync(sourcePath, currentMirror.TargetPath, cancellationToken).ConfigureAwait(false);
            }

            _logger.PolyglotDebug("CreateMirrorAsync: Mirrored {0} files", fileCount);

            // Create Jellyfin library if not already created
            // Re-fetch to check current TargetLibraryId state
            currentMirror = _configService.GetMirror(mirrorId);
            if (currentMirror != null && currentMirror.TargetLibraryId == null)
            {
                await CreateJellyfinLibraryAsync(alternativeId, mirrorId, cancellationToken).ConfigureAwait(false);
            }

            // Update mirror with success status atomically
            _configService.UpdateMirror(mirrorId, m =>
            {
                m.Status = SyncStatus.Synced;
                m.LastSyncedAt = DateTime.UtcNow;
                m.LastSyncFileCount = fileCount;
                m.LastError = null;
            });

            _logger.PolyglotInfo("CreateMirrorAsync: Mirror created successfully with {0} files", fileCount);
        }
        catch (Exception ex)
        {
            _logger.PolyglotError(ex, "CreateMirrorAsync: Failed to create mirror for {0}",
                new LogMirrorEntity(mirrorId, mirror.SourceLibraryName, mirror.TargetLibraryName));

            // Clean up any orphaned resources that may have been created before the failure
            // Re-fetch to get the current state (may have been set even if later steps failed)
            var failedMirror = _configService.GetMirror(mirrorId);
            if (failedMirror != null)
            {
                // Clean up orphaned Jellyfin library if it was created
                // We try two approaches:
                // 1. If TargetLibraryId was saved, use that for cleanup
                // 2. If TargetLibraryId is null but the library may exist (created but ID not saved),
                //    try to find and remove it by name as a fallback
                if (failedMirror.TargetLibraryId.HasValue)
                {
                    try
                    {
                        _libraryManager.RemoveVirtualFolder(failedMirror.TargetLibraryName, true);
                        _logger.PolyglotInfo("CreateMirrorAsync: Cleaned up orphaned Jellyfin library {0} after failure",
                            new LogLibraryEntity(failedMirror.TargetLibraryId.Value, failedMirror.TargetLibraryName, isMirror: true));
                    }
                    catch (Exception cleanupEx)
                    {
                        _logger.PolyglotWarning(cleanupEx, "CreateMirrorAsync: Failed to clean up orphaned Jellyfin library {0}",
                            new LogLibraryEntity(failedMirror.TargetLibraryId.Value, failedMirror.TargetLibraryName, isMirror: true));
                    }
                }
                else if (!string.IsNullOrEmpty(failedMirror.TargetLibraryName))
                {
                    // Fallback: Library may have been created but TargetLibraryId wasn't saved yet
                    // This can happen if failure occurs between AddVirtualFolder and UpdateMirror
                    var orphanedLibrary = _libraryManager.GetVirtualFolders()
                        .FirstOrDefault(f => string.Equals(f.Name, failedMirror.TargetLibraryName, StringComparison.OrdinalIgnoreCase));

                    if (orphanedLibrary != null)
                    {
                        var orphanedLibraryId = Guid.TryParse(orphanedLibrary.ItemId, out var id) ? id : Guid.Empty;
                        try
                        {
                            _libraryManager.RemoveVirtualFolder(failedMirror.TargetLibraryName, true);
                            _logger.PolyglotInfo(
                                "CreateMirrorAsync: Cleaned up orphaned Jellyfin library {0} (found by name) after failure",
                                new LogLibraryEntity(orphanedLibraryId, failedMirror.TargetLibraryName, isMirror: true));
                        }
                        catch (Exception cleanupEx)
                        {
                            _logger.PolyglotWarning(cleanupEx,
                                "CreateMirrorAsync: Failed to clean up orphaned Jellyfin library {0} (found by name)",
                                new LogLibraryEntity(orphanedLibraryId, failedMirror.TargetLibraryName, isMirror: true));
                        }
                    }
                }

                // Clean up files/directory on failure
                // Safe to clean if: (1) we created the directory, OR (2) directory was empty when we started
                // The validation ensures non-empty directories are rejected, so any files present after
                // a failure in an originally-empty directory must be files we created
                if (!string.IsNullOrEmpty(failedMirror.TargetPath) && Directory.Exists(failedMirror.TargetPath))
                {
                    var targetPathEntity = new LogPathEntity(failedMirror.TargetPath, "target");
                    if (createdTargetDirectory)
                    {
                        // We created the directory - delete the entire directory
                        try
                        {
                            Directory.Delete(failedMirror.TargetPath, recursive: true);
                            _logger.PolyglotInfo("CreateMirrorAsync: Cleaned up directory {0} that we created after failure", targetPathEntity);
                        }
                        catch (Exception cleanupEx)
                        {
                            _logger.PolyglotWarning(cleanupEx, "CreateMirrorAsync: Failed to clean up directory {0}", targetPathEntity);
                        }
                    }
                    else if (directoryWasEmpty)
                    {
                        // Pre-existing but empty directory - clean up any files we created but keep the directory
                        try
                        {
                            foreach (var entry in Directory.EnumerateFileSystemEntries(failedMirror.TargetPath))
                            {
                                try
                                {
                                    if (File.Exists(entry))
                                    {
                                        File.Delete(entry);
                                    }
                                    else if (Directory.Exists(entry))
                                    {
                                        Directory.Delete(entry, recursive: true);
                                    }
                                }
                                catch (Exception entryEx)
                                {
                                    _logger.PolyglotWarning(entryEx, "CreateMirrorAsync: Failed to clean up {0}",
                                        new LogPathEntity(entry, "file"));
                                }
                            }

                            _logger.PolyglotInfo(
                                "CreateMirrorAsync: Cleaned up partial mirror files in pre-existing directory {0} after failure",
                                targetPathEntity);
                        }
                        catch (Exception cleanupEx)
                        {
                            _logger.PolyglotWarning(cleanupEx,
                                "CreateMirrorAsync: Failed to enumerate/clean up files in {0}",
                                targetPathEntity);
                        }
                    }
                    else
                    {
                        // Pre-existing directory that wasn't empty - don't touch it
                        _logger.PolyglotWarning(
                            "CreateMirrorAsync: Not cleaning up pre-existing non-empty directory {0} after failure to prevent data loss. " +
                            "Manual cleanup may be required.",
                            targetPathEntity);
                    }
                }
            }

            // Update mirror with error status atomically
            // Log if this fails (mirror may have been deleted), but still re-throw original exception
            var errorStatusUpdated = _configService.UpdateMirror(mirrorId, m =>
            {
                m.Status = SyncStatus.Error;
                m.LastError = ex.Message;
            });

            if (!errorStatusUpdated)
            {
                _logger.PolyglotWarning(
                    "CreateMirrorAsync: Could not update error status for mirror {0} (mirror may have been deleted)",
                    new LogMirrorEntity(mirrorId, mirror.SourceLibraryName, mirror.TargetLibraryName));
            }

            throw;
        }
        finally
        {
            mirrorLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task SyncMirrorAsync(Guid mirrorId, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        _logger.PolyglotDebug("SyncMirrorAsync: Starting for mirror {0}",
            _configService.CreateLogMirror(mirrorId));

        // Get fresh snapshot of mirror data
        var mirror = _configService.GetMirror(mirrorId);
        if (mirror == null)
        {
            _logger.PolyglotWarning("SyncMirrorAsync: Mirror {0} not found",
                _configService.CreateLogMirror(mirrorId));
            throw new InvalidOperationException($"Mirror {mirrorId} not found");
        }

        _logger.PolyglotInfo("SyncMirrorAsync: Syncing mirror {0}",
            new LogMirrorEntity(mirrorId, mirror.SourceLibraryName, mirror.TargetLibraryName));

        var mirrorLock = _mirrorLocks.GetOrAdd(mirrorId, _ => new SemaphoreSlim(1, 1));
        await mirrorLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Update status to syncing atomically
            _configService.UpdateMirror(mirrorId, m => m.Status = SyncStatus.Syncing);

            // Re-fetch for current state
            mirror = _configService.GetMirror(mirrorId);
            if (mirror == null)
            {
                throw new InvalidOperationException($"Mirror {mirrorId} was removed during operation");
            }

            var sourceLibrary = GetVirtualFolderById(mirror.SourceLibraryId);
            if (sourceLibrary == null)
            {
                throw new InvalidOperationException($"Source library {mirror.SourceLibraryId} not found");
            }

            var sourcePaths = sourceLibrary.Locations;
            if (sourcePaths == null || sourcePaths.Length == 0)
            {
                throw new InvalidOperationException($"Source library {mirror.SourceLibraryName} has no paths");
            }

            if (!Directory.Exists(mirror.TargetPath))
            {
                Directory.CreateDirectory(mirror.TargetPath);
                _logger.PolyglotDebug("SyncMirrorAsync: Created target directory {0}",
                    new LogPathEntity(mirror.TargetPath, "target"));
            }

            // Build file sets
            var sourceFiles = new Dictionary<string, FileSignature>(StringComparer.OrdinalIgnoreCase);
            foreach (var sourcePath in sourcePaths)
            {
                foreach (var (file, signature) in EnumerateFilesForMirroring(sourcePath))
                {
                    var relativePath = Path.GetRelativePath(sourcePath, file);
                    sourceFiles[relativePath] = signature;
                }
            }

            var targetFiles = new Dictionary<string, FileSignature>(StringComparer.OrdinalIgnoreCase);
            foreach (var (file, signature) in EnumerateFilesForMirroring(mirror.TargetPath))
            {
                var relativePath = Path.GetRelativePath(mirror.TargetPath, file);
                targetFiles[relativePath] = signature;
            }

            // Calculate differences
            var filesToAdd = new List<string>();
            var filesToRemove = new List<string>();

            // Check for new or modified files
            foreach (var kvp in sourceFiles)
            {
                var relativePath = kvp.Key;
                var sourceSig = kvp.Value;

                if (targetFiles.TryGetValue(relativePath, out var targetSig))
                {
                    // File exists in both - check if modified
                    if (sourceSig.Size != targetSig.Size || sourceSig.ModifiedTicks != targetSig.ModifiedTicks)
                    {
                        _logger.PolyglotDebug("SyncMirrorAsync: File modified: {0}",
                            new LogPathEntity(relativePath, "file"));
                        filesToRemove.Add(relativePath);
                        filesToAdd.Add(relativePath);
                    }
                }
                else
                {
                    filesToAdd.Add(relativePath);
                }
            }

            // Check for deleted files
            foreach (var kvp in targetFiles)
            {
                if (!sourceFiles.ContainsKey(kvp.Key))
                {
                    filesToRemove.Add(kvp.Key);
                }
            }

            var totalOperations = filesToAdd.Count + filesToRemove.Count;
            var completedOperations = 0;

            _logger.PolyglotDebug("SyncMirrorAsync: {0} files to add, {1} files to remove", filesToAdd.Count, filesToRemove.Count);

            // Remove deleted files
            foreach (var relativePath in filesToRemove)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var targetFile = Path.Combine(mirror.TargetPath, relativePath);
                try
                {
                    if (File.Exists(targetFile))
                    {
                        File.Delete(targetFile);
                        _logger.PolyglotDebug("SyncMirrorAsync: Deleted file {0}",
                            new LogPathEntity(targetFile, "file"));
                    }

                    var dir = Path.GetDirectoryName(targetFile);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        FileSystemHelper.CleanupEmptyDirectories(dir, mirror.TargetPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.PolyglotWarning(ex, "SyncMirrorAsync: Failed to delete file {0}",
                        new LogPathEntity(targetFile, "file"));
                }

                completedOperations++;
                if (totalOperations > 0)
                {
                    SafeReportProgress(progress, (double)completedOperations / totalOperations * 100);
                }
            }

            // Add new files
            foreach (var relativePath in filesToAdd)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string? sourceFile = null;
                foreach (var sourcePath in sourcePaths)
                {
                    var potentialSource = Path.Combine(sourcePath, relativePath);
                    if (File.Exists(potentialSource))
                    {
                        sourceFile = potentialSource;
                        break;
                    }
                }

                if (sourceFile != null)
                {
                    var targetFile = Path.Combine(mirror.TargetPath, relativePath);
                    try
                    {
                        FileSystemHelper.CreateHardLink(sourceFile, targetFile, _logger);
                        _logger.PolyglotDebug("SyncMirrorAsync: Created hardlink for {0}",
                            new LogPathEntity(relativePath, "file"));
                    }
                    catch (Exception ex)
                    {
                        _logger.PolyglotWarning(ex, "SyncMirrorAsync: Failed to create hardlink for {0}",
                            new LogPathEntity(relativePath, "file"));
                    }
                }

                completedOperations++;
                if (totalOperations > 0)
                {
                    SafeReportProgress(progress, (double)completedOperations / totalOperations * 100);
                }
            }

            // Update mirror with success status atomically
            _configService.UpdateMirror(mirrorId, m =>
            {
                m.Status = SyncStatus.Synced;
                m.LastSyncedAt = DateTime.UtcNow;
                m.LastSyncFileCount = sourceFiles.Count;
                m.LastError = null;
            });

            _logger.PolyglotInfo("SyncMirrorAsync: Sync completed - {0} added, {1} removed", filesToAdd.Count, filesToRemove.Count);
        }
        catch (Exception ex)
        {
            var mirrorEntity = new LogMirrorEntity(mirrorId, mirror.SourceLibraryName, mirror.TargetLibraryName);
            _logger.PolyglotError(ex, "SyncMirrorAsync: Failed to sync mirror {0}", mirrorEntity);

            // Update mirror with error status atomically
            // Log if this fails (mirror may have been deleted), but still re-throw original exception
            var errorStatusUpdated = _configService.UpdateMirror(mirrorId, m =>
            {
                m.Status = SyncStatus.Error;
                m.LastError = ex.Message;
            });

            if (!errorStatusUpdated)
            {
                _logger.PolyglotWarning(
                    "SyncMirrorAsync: Could not update error status for mirror {0} (mirror may have been deleted)",
                    mirrorEntity);
            }

            throw;
        }
        finally
        {
            mirrorLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<DeleteMirrorResult> DeleteMirrorAsync(Guid mirrorId, bool deleteLibrary = true, bool deleteFiles = true, bool forceConfigRemoval = false, CancellationToken cancellationToken = default)
    {
        _logger.PolyglotDebug("DeleteMirrorAsync: Starting for mirror {0} (deleteLibrary: {1}, deleteFiles: {2}, forceConfigRemoval: {3})",
            _configService.CreateLogMirror(mirrorId), deleteLibrary, deleteFiles, forceConfigRemoval);

        var result = new DeleteMirrorResult();

        // Get fresh snapshot of mirror data
        var mirror = _configService.GetMirror(mirrorId);
        if (mirror == null)
        {
            _logger.PolyglotWarning("DeleteMirrorAsync: Mirror {0} not found, nothing to delete",
                _configService.CreateLogMirror(mirrorId));
            result.RemovedFromConfig = true; // Already gone
            return result;
        }

        var mirrorEntity = new LogMirrorEntity(mirrorId, mirror.SourceLibraryName, mirror.TargetLibraryName);
        _logger.PolyglotInfo("DeleteMirrorAsync: Deleting mirror {0}", mirrorEntity);

        var mirrorLock = _mirrorLocks.GetOrAdd(mirrorId, _ => new SemaphoreSlim(1, 1));
        await mirrorLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Delete Jellyfin library
            if (deleteLibrary && mirror.TargetLibraryId.HasValue)
            {
                var targetLibraryEntity = new LogLibraryEntity(mirror.TargetLibraryId.Value, mirror.TargetLibraryName, isMirror: true);
                try
                {
                    _libraryManager.RemoveVirtualFolder(mirror.TargetLibraryName, true);
                    _logger.PolyglotInfo("DeleteMirrorAsync: Removed Jellyfin library {0}", targetLibraryEntity);
                    result.LibraryDeleted = true;
                }
                catch (Exception ex)
                {
                    // Check if library still exists after the failure
                    var libraryStillExists = _libraryManager.GetVirtualFolders()
                        .Any(f => Guid.TryParse(f.ItemId, out var id) && id == mirror.TargetLibraryId.Value);

                    if (libraryStillExists)
                    {
                        _logger.PolyglotError(ex,
                            "DeleteMirrorAsync: Failed to remove Jellyfin library {0}",
                            targetLibraryEntity);
                        result.LibraryDeletionError = $"Failed to delete Jellyfin library '{mirror.TargetLibraryName}': {ex.Message}";

                        if (!forceConfigRemoval)
                        {
                            throw new InvalidOperationException(
                                $"Failed to delete Jellyfin library '{mirror.TargetLibraryName}'. " +
                                "The library still exists. Use forceConfigRemoval=true to remove from config anyway.",
                                ex);
                        }
                    }
                    else
                    {
                        // Library doesn't exist anymore - probably deleted externally
                        _logger.PolyglotDebug(
                            "DeleteMirrorAsync: RemoveVirtualFolder failed for {0} but library no longer exists",
                            targetLibraryEntity);
                        result.LibraryDeleted = true;
                    }
                }
            }
            else if (!deleteLibrary)
            {
                result.LibraryDeleted = false; // Not requested
            }
            else
            {
                result.LibraryDeleted = true; // No library to delete (TargetLibraryId was null)
            }

            // Delete mirror files
            if (deleteFiles && Directory.Exists(mirror.TargetPath))
            {
                var targetPathEntity = new LogPathEntity(mirror.TargetPath, "target");
                try
                {
                    Directory.Delete(mirror.TargetPath, true);
                    _logger.PolyglotInfo("DeleteMirrorAsync: Deleted mirror directory {0}", targetPathEntity);
                    result.FilesDeleted = true;
                }
                catch (Exception ex)
                {
                    _logger.PolyglotError(ex,
                        "DeleteMirrorAsync: Failed to delete mirror directory {0}",
                        targetPathEntity);
                    result.FileDeletionError = $"Failed to delete directory '{mirror.TargetPath}': {ex.Message}";

                    if (!forceConfigRemoval)
                    {
                        throw new InvalidOperationException(
                            $"Failed to delete mirror directory '{mirror.TargetPath}'. " +
                            "Use forceConfigRemoval=true to remove from config anyway.",
                            ex);
                    }
                }
            }
            else if (!deleteFiles)
            {
                result.FilesDeleted = false; // Not requested
            }
            else
            {
                result.FilesDeleted = true; // Directory didn't exist
            }

            // Remove mirror from configuration
            // With forceConfigRemoval, we always remove even if above operations failed
            _configService.RemoveMirror(mirrorId);
            result.RemovedFromConfig = true;
            _logger.PolyglotDebug("DeleteMirrorAsync: Removed mirror {0} from configuration", mirrorEntity);

            _mirrorLocks.TryRemove(mirrorId, out _);

            if (result.HasErrors)
            {
                _logger.PolyglotWarning(
                    "DeleteMirrorAsync: Completed for mirror {0} with errors (forceConfigRemoval was used). " +
                    "LibraryError: {1}, FileError: {2}",
                    mirrorEntity, result.LibraryDeletionError ?? "none", result.FileDeletionError ?? "none");
            }
            else
            {
                _logger.PolyglotDebug("DeleteMirrorAsync: Completed successfully for mirror {0}", mirrorEntity);
            }

            return result;
        }
        finally
        {
            mirrorLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<SyncAllResult> SyncAllMirrorsAsync(Guid alternativeId, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        _logger.PolyglotDebug("SyncAllMirrorsAsync: Starting for alternative {0}",
            _configService.CreateLogAlternative(alternativeId));

        var result = new SyncAllResult();

        // Get fresh snapshot of alternative
        var alternative = _configService.GetAlternative(alternativeId);
        if (alternative == null)
        {
            _logger.PolyglotWarning("SyncAllMirrorsAsync: Alternative {0} not found",
                _configService.CreateLogAlternative(alternativeId));
            result.Status = SyncAllStatus.AlternativeNotFound;
            return result;
        }

        var alternativeEntity = new LogAlternativeEntity(alternativeId, alternative.Name, alternative.LanguageCode);
        _logger.PolyglotInfo("SyncAllMirrorsAsync: Syncing all mirrors for alternative {0}", alternativeEntity);

        // Get mirror IDs (not objects) to iterate
        var mirrorIds = alternative.MirroredLibraries.Select(m => m.Id).ToList();
        result.TotalMirrors = mirrorIds.Count;

        _logger.PolyglotDebug("SyncAllMirrorsAsync: Found {0} mirrors to sync", result.TotalMirrors);

        for (int i = 0; i < mirrorIds.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.PolyglotInfo("SyncAllMirrorsAsync: Cancelled after {0} mirrors", i);
                result.Status = SyncAllStatus.Cancelled;
                return result;
            }

            var mirrorId = mirrorIds[i];
            var mirrorProgress = new Progress<double>(p =>
            {
                var overallProgress = ((i * 100.0) + p) / result.TotalMirrors;
                SafeReportProgress(progress, overallProgress);
            });

            try
            {
                await SyncMirrorAsync(mirrorId, mirrorProgress, cancellationToken).ConfigureAwait(false);
                result.MirrorsSynced++;
            }
            catch (OperationCanceledException)
            {
                _logger.PolyglotInfo("SyncAllMirrorsAsync: Cancelled during mirror {0}",
                    _configService.CreateLogMirror(mirrorId));
                result.Status = SyncAllStatus.Cancelled;
                return result;
            }
            catch (Exception ex)
            {
                _logger.PolyglotError(ex, "SyncAllMirrorsAsync: Failed to sync mirror {0}",
                    _configService.CreateLogMirror(mirrorId));
                result.MirrorsFailed++;
                // Continue with other mirrors
            }
        }

        result.Status = result.MirrorsFailed > 0 ? SyncAllStatus.CompletedWithErrors : SyncAllStatus.Completed;

        _logger.PolyglotInfo("SyncAllMirrorsAsync: Completed for alternative {0} - {1} synced, {2} failed",
            alternativeEntity, result.MirrorsSynced, result.MirrorsFailed);

        return result;
    }

    /// <inheritdoc />
    public (bool IsValid, string? ErrorMessage) ValidateMirrorConfiguration(Guid sourceLibraryId, string targetPath)
    {
        var sourceLibrary = GetVirtualFolderById(sourceLibraryId);
        if (sourceLibrary == null)
        {
            return (false, "Source library not found");
        }

        var sourcePaths = sourceLibrary.Locations;
        if (sourcePaths == null || sourcePaths.Length == 0)
        {
            return (false, "Source library has no paths");
        }

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return (false, "Target path is required");
        }

        if (targetPath.Contains(".."))
        {
            return (false, "Target path cannot contain path traversal sequences");
        }

        foreach (var sourcePath in sourcePaths)
        {
            if (!FileSystemHelper.AreOnSameFilesystem(sourcePath, targetPath))
            {
                return (false, $"Source path '{sourcePath}' and target path are on different filesystems. Hardlinks require the same filesystem.");
            }
        }

        foreach (var sourcePath in sourcePaths)
        {
            var fullSource = Path.GetFullPath(sourcePath);
            var fullTarget = Path.GetFullPath(targetPath);
            if (fullTarget.StartsWith(fullSource, StringComparison.OrdinalIgnoreCase))
            {
                return (false, "Target path cannot be inside the source library path");
            }
        }

        // Prevent data loss: reject target paths that already contain files
        // This protects against accidental deletion of pre-existing content if mirror creation fails
        if (Directory.Exists(targetPath))
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(targetPath).Any())
                {
                    return (false, "Target path already exists and contains files. Please specify an empty or non-existent directory to prevent accidental data loss.");
                }
            }
            catch (UnauthorizedAccessException)
            {
                return (false, "Cannot access target path to verify it is empty. Please check permissions.");
            }
            catch (IOException ex)
            {
                return (false, $"Cannot verify target path is empty: {ex.Message}");
            }
        }

        return (true, null);
    }

    /// <inheritdoc />
    public IEnumerable<LibraryInfo> GetJellyfinLibraries()
    {
        var virtualFolders = _libraryManager.GetVirtualFolders();
        var alternatives = _configService.GetAlternatives();

        foreach (var folder in virtualFolders)
        {
            var libraryInfo = new LibraryInfo
            {
                Id = Guid.Parse(folder.ItemId),
                Name = folder.Name,
                CollectionType = folder.CollectionType?.ToString(),
                Paths = folder.Locations?.ToList() ?? new List<string>(),
                PreferredMetadataLanguage = folder.LibraryOptions?.PreferredMetadataLanguage,
                MetadataCountryCode = folder.LibraryOptions?.MetadataCountryCode
            };

            // Check if this is a mirror library
            foreach (var alt in alternatives)
            {
                var mirror = alt.MirroredLibraries.FirstOrDefault(m => m.TargetLibraryId == libraryInfo.Id);
                if (mirror != null)
                {
                    libraryInfo.IsMirror = true;
                    libraryInfo.LanguageAlternativeId = alt.Id;
                    break;
                }
            }

            yield return libraryInfo;
        }
    }

    /// <inheritdoc />
    public async Task<OrphanCleanupResult> CleanupOrphanedMirrorsAsync(CancellationToken cancellationToken = default)
    {
        _logger.PolyglotDebug("CleanupOrphanedMirrorsAsync: Starting orphan cleanup");

        var result = new OrphanCleanupResult();

        var existingLibraryIds = GetJellyfinLibraries()
            .Select(l => l.Id)
            .ToHashSet();

        // Build list of mirrors to delete (by ID, not reference)
        var mirrorsToDelete = new List<(Guid AlternativeId, Guid MirrorId, string MirrorName, Guid SourceLibraryId, string Reason)>();

        // Threshold for considering a pending mirror as "stuck" (failed creation)
        // If a mirror has been in Pending/Error state with no TargetLibraryId for more than this duration,
        // it's likely a ghost from a failed creation that wasn't cleaned up properly
        var stuckMirrorThreshold = TimeSpan.FromMinutes(30);
        var now = DateTime.UtcNow;

        foreach (var alternative in _configService.GetAlternatives())
        {
            foreach (var mirror in alternative.MirroredLibraries)
            {
                var mirrorEntity = new LogMirrorEntity(mirror.Id, mirror.SourceLibraryName, mirror.TargetLibraryName);

                if (!existingLibraryIds.Contains(mirror.SourceLibraryId))
                {
                    _logger.PolyglotWarning(
                        "CleanupOrphanedMirrorsAsync: Source library {0} for mirror {1} no longer exists",
                        new LogLibraryEntity(mirror.SourceLibraryId, mirror.SourceLibraryName), mirrorEntity);

                    mirrorsToDelete.Add((alternative.Id, mirror.Id, mirror.TargetLibraryName, mirror.SourceLibraryId, "source deleted"));
                    continue;
                }

                if (mirror.TargetLibraryId.HasValue && !existingLibraryIds.Contains(mirror.TargetLibraryId.Value))
                {
                    _logger.PolyglotWarning(
                        "CleanupOrphanedMirrorsAsync: Target library {0} for mirror {1} no longer exists",
                        new LogLibraryEntity(mirror.TargetLibraryId.Value, mirror.TargetLibraryName, isMirror: true), mirrorEntity);

                    mirrorsToDelete.Add((alternative.Id, mirror.Id, mirror.TargetLibraryName, mirror.SourceLibraryId, "mirror deleted"));
                    continue;
                }

                // Detect "ghost" mirrors: TargetLibraryId is null AND mirror is stuck in Pending/Error state
                // These are remnants from failed creation attempts where cleanup didn't complete
                if (!mirror.TargetLibraryId.HasValue &&
                    (mirror.Status == SyncStatus.Pending || mirror.Status == SyncStatus.Error))
                {
                    // Check if this mirror has been stuck for a while
                    // Use LastSyncedAt if available, otherwise assume it's old enough to clean up
                    var mirrorAge = mirror.LastSyncedAt.HasValue
                        ? now - mirror.LastSyncedAt.Value
                        : stuckMirrorThreshold; // Treat mirrors with no timestamp as old enough

                    if (mirrorAge >= stuckMirrorThreshold)
                    {
                        _logger.PolyglotWarning(
                            "CleanupOrphanedMirrorsAsync: Mirror {0} appears to be a ghost (no target library, status: {1}, age: {2})",
                            mirrorEntity, mirror.Status, mirrorAge);

                        mirrorsToDelete.Add((alternative.Id, mirror.Id, mirror.TargetLibraryName, mirror.SourceLibraryId, "ghost (failed creation)"));
                    }
                }
            }
        }

        _logger.PolyglotDebug("CleanupOrphanedMirrorsAsync: Found {0} orphaned mirrors", mirrorsToDelete.Count);

        // Delete orphaned mirrors - DeleteMirrorAsync now handles config removal
        // Use forceConfigRemoval=true for cleanup to ensure orphans are always removed from config
        foreach (var (alternativeId, mirrorId, mirrorName, sourceLibraryId, reason) in mirrorsToDelete)
        {
            // Get mirror info for logging before deletion
            var mirrorForLog = _configService.GetMirror(mirrorId);
            var mirrorEntity = mirrorForLog != null
                ? new LogMirrorEntity(mirrorId, mirrorForLog.SourceLibraryName, mirrorForLog.TargetLibraryName)
                : new LogMirrorEntity(mirrorId, mirrorName, mirrorName);

            try
            {
                // When source is deleted: delete the mirror's Jellyfin library and files
                // When mirror's Jellyfin library is deleted externally: just clean up the orphaned files
                // In both cases, we always clean up files to prevent disk space accumulation
                var deleteLibrary = reason == "source deleted";
                var deleteFiles = true; // Always clean up orphaned hardlink files

                // Use forceConfigRemoval=true for orphan cleanup - we always want to remove from config
                var deleteResult = await DeleteMirrorAsync(mirrorId, deleteLibrary: deleteLibrary, deleteFiles: deleteFiles, forceConfigRemoval: true, cancellationToken)
                    .ConfigureAwait(false);

                if (deleteResult.HasErrors)
                {
                    // Partial success - removed from config but some cleanup failed
                    result.CleanedUpMirrors.Add($"{mirrorName} ({reason}) [with warnings]");
                    if (!string.IsNullOrEmpty(deleteResult.LibraryDeletionError))
                    {
                        _logger.PolyglotWarning("CleanupOrphanedMirrorsAsync: Library cleanup warning for {0}: {1}", mirrorEntity, deleteResult.LibraryDeletionError);
                    }

                    if (!string.IsNullOrEmpty(deleteResult.FileDeletionError))
                    {
                        _logger.PolyglotWarning("CleanupOrphanedMirrorsAsync: File cleanup warning for {0}: {1}", mirrorEntity, deleteResult.FileDeletionError);
                    }
                }
                else
                {
                    result.CleanedUpMirrors.Add($"{mirrorName} ({reason})");
                }

                if (reason == "mirror deleted")
                {
                    // Re-check if source has other mirrors using fresh config
                    var sourceHasOtherMirrors = _configService.GetAlternatives()
                        .SelectMany(a => a.MirroredLibraries)
                        .Any(m => m.SourceLibraryId == sourceLibraryId);

                    if (!sourceHasOtherMirrors && existingLibraryIds.Contains(sourceLibraryId))
                    {
                        result.SourcesWithoutMirrors.Add(sourceLibraryId);
                        _logger.PolyglotInfo("CleanupOrphanedMirrorsAsync: Source library {0} has no more mirrors",
                            new LogLibraryEntity(sourceLibraryId, mirrorName));
                    }
                }

                _logger.PolyglotInfo("CleanupOrphanedMirrorsAsync: Removed orphaned mirror {0} ({1})", mirrorEntity, reason);
            }
            catch (Exception ex)
            {
                _logger.PolyglotError(ex, "CleanupOrphanedMirrorsAsync: Failed to delete orphaned mirror {0}", mirrorEntity);
                result.FailedCleanups.Add($"{mirrorName} ({reason}): {ex.Message}");
            }
        }

        result.TotalCleaned = result.CleanedUpMirrors.Count;
        result.TotalFailed = result.FailedCleanups.Count;

        if (result.TotalFailed > 0)
        {
            _logger.PolyglotWarning(
                "CleanupOrphanedMirrorsAsync: Cleaned up {0} orphaned mirrors, {1} failed to clean up",
                result.TotalCleaned, result.TotalFailed);
        }
        else
        {
            _logger.PolyglotInfo("CleanupOrphanedMirrorsAsync: Cleaned up {0} orphaned mirrors", result.TotalCleaned);
        }

        return result;
    }

    /// <summary>
    /// Mirrors a directory structure with hardlinks.
    /// </summary>
    private async Task<int> MirrorDirectoryAsync(string sourcePath, string targetPath, CancellationToken cancellationToken)
    {
        int fileCount = 0;

        foreach (var (sourceFile, _) in EnumerateFilesForMirroring(sourcePath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(sourcePath, sourceFile);
            var targetFile = Path.Combine(targetPath, relativePath);

            if (FileSystemHelper.CreateHardLink(sourceFile, targetFile, _logger))
            {
                fileCount++;
            }
        }

        return fileCount;
    }

    /// <summary>
    /// Enumerates files that should be mirrored (excludes metadata) and returns their signatures.
    /// </summary>
    private IEnumerable<(string Path, FileSignature Signature)> EnumerateFilesForMirroring(string path)
    {
        if (!Directory.Exists(path))
        {
            yield break;
        }

        // Use thread-safe getters that return copies of the collections
        // This prevents enumeration exceptions if collections are modified during iteration
        var excludedExtensions = _configService.GetExcludedExtensions();
        var excludedDirectoryNames = _configService.GetExcludedDirectories();

        var excludedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
        {
            if (FileClassifier.ShouldExcludeDirectory(directory, excludedDirectoryNames))
            {
                excludedDirs.Add(directory);
            }
        }

        var dirInfo = new DirectoryInfo(path);
        foreach (var fileInfo in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var fileDir = fileInfo.DirectoryName;
            var isInExcludedDir = false;

            if (!string.IsNullOrEmpty(fileDir))
            {
                foreach (var excludedDir in excludedDirs)
                {
                    if (fileDir.StartsWith(excludedDir, StringComparison.OrdinalIgnoreCase))
                    {
                        isInExcludedDir = true;
                        break;
                    }
                }
            }

            if (!isInExcludedDir && FileClassifier.ShouldHardlink(fileInfo.FullName, excludedExtensions, excludedDirectoryNames))
            {
                yield return (fileInfo.FullName, new FileSignature(fileInfo.Length, fileInfo.LastWriteTimeUtc.Ticks));
            }
        }
    }

    /// <summary>
    /// Represents a file's unique signature based on size and modification time.
    /// </summary>
    private readonly struct FileSignature
    {
        public FileSignature(long size, long modifiedTicks)
        {
            Size = size;
            ModifiedTicks = modifiedTicks;
        }

        public long Size { get; }
        public long ModifiedTicks { get; }
    }

    /// <summary>
    /// Creates a Jellyfin library for the mirror.
    /// Uses IDs to ensure fresh config lookup.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if alternative or mirror is not found (e.g., deleted concurrently).</exception>
    private async Task CreateJellyfinLibraryAsync(Guid alternativeId, Guid mirrorId, CancellationToken cancellationToken)
    {
        _logger.PolyglotDebug("CreateJellyfinLibraryAsync: Creating library for mirror {0}",
            _configService.CreateLogMirror(mirrorId));

        // Get fresh data
        var alternative = _configService.GetAlternative(alternativeId);
        var mirror = _configService.GetMirror(mirrorId);

        if (alternative == null)
        {
            _logger.PolyglotWarning("CreateJellyfinLibraryAsync: Alternative {0} not found (may have been deleted)",
                _configService.CreateLogAlternative(alternativeId));
            throw new InvalidOperationException($"Alternative {alternativeId} was deleted during mirror creation");
        }

        var alternativeEntity = new LogAlternativeEntity(alternativeId, alternative.Name, alternative.LanguageCode);

        if (mirror == null)
        {
            _logger.PolyglotWarning("CreateJellyfinLibraryAsync: Mirror {0} not found (may have been deleted)",
                _configService.CreateLogMirror(mirrorId));
            throw new InvalidOperationException($"Mirror {mirrorId} was deleted during library creation");
        }

        var mirrorEntity = new LogMirrorEntity(mirrorId, mirror.SourceLibraryName, mirror.TargetLibraryName);

        var sourceLibrary = GetVirtualFolderById(mirror.SourceLibraryId);
        var sourceOptions = sourceLibrary?.LibraryOptions;

        var options = new MediaBrowser.Model.Configuration.LibraryOptions
        {
            PreferredMetadataLanguage = alternative.MetadataLanguage,
            MetadataCountryCode = alternative.MetadataCountry,
            SaveLocalMetadata = false,
            SaveSubtitlesWithMedia = false,
            SaveLyricsWithMedia = false,
            EnableRealtimeMonitor = true,
            Enabled = true
        };

        if (sourceOptions != null)
        {
            options.TypeOptions = sourceOptions.TypeOptions;
            options.MetadataSavers = sourceOptions.MetadataSavers;
            options.DisabledLocalMetadataReaders = sourceOptions.DisabledLocalMetadataReaders;
            options.LocalMetadataReaderOrder = sourceOptions.LocalMetadataReaderOrder;
            options.DisabledSubtitleFetchers = sourceOptions.DisabledSubtitleFetchers;
            options.SubtitleFetcherOrder = sourceOptions.SubtitleFetcherOrder;
            options.SubtitleDownloadLanguages = sourceOptions.SubtitleDownloadLanguages;
            options.SkipSubtitlesIfEmbeddedSubtitlesPresent = sourceOptions.SkipSubtitlesIfEmbeddedSubtitlesPresent;
            options.SkipSubtitlesIfAudioTrackMatches = sourceOptions.SkipSubtitlesIfAudioTrackMatches;
            options.RequirePerfectSubtitleMatch = sourceOptions.RequirePerfectSubtitleMatch;
            options.AllowEmbeddedSubtitles = sourceOptions.AllowEmbeddedSubtitles;
            options.DisabledLyricFetchers = sourceOptions.DisabledLyricFetchers;
            options.LyricFetcherOrder = sourceOptions.LyricFetcherOrder;
            options.DisabledMediaSegmentProviders = sourceOptions.DisabledMediaSegmentProviders;
            JellyfinCompatibility.TryCopyProperty(sourceOptions, options, "MediaSegmentProviderOrder", "MediaSegmentProvideOrder");
            options.EnablePhotos = sourceOptions.EnablePhotos;
            options.EnableChapterImageExtraction = sourceOptions.EnableChapterImageExtraction;
            options.ExtractChapterImagesDuringLibraryScan = sourceOptions.ExtractChapterImagesDuringLibraryScan;
            options.EnableTrickplayImageExtraction = sourceOptions.EnableTrickplayImageExtraction;
            options.ExtractTrickplayImagesDuringLibraryScan = sourceOptions.ExtractTrickplayImagesDuringLibraryScan;
            options.SaveTrickplayWithMedia = sourceOptions.SaveTrickplayWithMedia;
            options.AutomaticallyAddToCollection = sourceOptions.AutomaticallyAddToCollection;
            options.EnableAutomaticSeriesGrouping = sourceOptions.EnableAutomaticSeriesGrouping;
            options.SeasonZeroDisplayName = sourceOptions.SeasonZeroDisplayName;
            options.EnableEmbeddedTitles = sourceOptions.EnableEmbeddedTitles;
            options.EnableEmbeddedExtrasTitles = sourceOptions.EnableEmbeddedExtrasTitles;
            options.EnableEmbeddedEpisodeInfos = sourceOptions.EnableEmbeddedEpisodeInfos;
            options.PreferNonstandardArtistsTag = sourceOptions.PreferNonstandardArtistsTag;
            options.UseCustomTagDelimiters = sourceOptions.UseCustomTagDelimiters;
            options.CustomTagDelimiters = sourceOptions.CustomTagDelimiters;
            options.DelimiterWhitelist = sourceOptions.DelimiterWhitelist;
            options.AutomaticRefreshIntervalDays = sourceOptions.AutomaticRefreshIntervalDays;
            options.EnableLUFSScan = sourceOptions.EnableLUFSScan;
            options.EnableInternetProviders = true;
        }

        MediaBrowser.Model.Entities.CollectionTypeOptions? collectionType = null;
        if (!string.IsNullOrEmpty(mirror.CollectionType) &&
            Enum.TryParse<MediaBrowser.Model.Entities.CollectionTypeOptions>(mirror.CollectionType, true, out var parsedType))
        {
            collectionType = parsedType;
        }

        await _libraryManager.AddVirtualFolder(
            mirror.TargetLibraryName,
            collectionType,
            options,
            true).ConfigureAwait(false);

        var createdLibrary = _libraryManager.GetVirtualFolders()
            .FirstOrDefault(f => string.Equals(f.Name, mirror.TargetLibraryName, StringComparison.OrdinalIgnoreCase));

        if (createdLibrary == null)
        {
            // Library creation failed - could be name conflict or other Jellyfin issue
            _logger.PolyglotError(
                "CreateJellyfinLibraryAsync: Failed to create Jellyfin library for mirror {0}. " +
                "Library may already exist with the same name or Jellyfin rejected the request.",
                mirrorEntity);
            throw new InvalidOperationException(
                $"Failed to create Jellyfin library '{mirror.TargetLibraryName}'. " +
                "The library may already exist with the same name.");
        }

        var targetLibraryId = Guid.Parse(createdLibrary.ItemId);
        var targetLibraryEntity = new LogLibraryEntity(targetLibraryId, mirror.TargetLibraryName, isMirror: true);

        // Update mirror with target library ID atomically
        _configService.UpdateMirror(mirrorId, m => m.TargetLibraryId = targetLibraryId);

        _libraryManager.AddMediaPath(mirror.TargetLibraryName, new MediaBrowser.Model.Configuration.MediaPathInfo
        {
            Path = mirror.TargetPath
        });

        await RefreshLibraryAsync(targetLibraryId, cancellationToken).ConfigureAwait(false);

        _logger.PolyglotInfo("CreateJellyfinLibraryAsync: Created Jellyfin library {0}",
            targetLibraryEntity);
    }

    /// <summary>
    /// Triggers a library scan to discover files on the filesystem and fetch metadata.
    /// </summary>
    private Task RefreshLibraryAsync(Guid libraryId, CancellationToken cancellationToken)
    {
        var libraryEntity = _libraryManager.CreateLogMirrorLibrary(libraryId);
        _logger.PolyglotDebug("RefreshLibraryAsync: Queueing refresh for {0}", libraryEntity);

        try
        {
            var refreshOptions = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllMetadata = true,
                ReplaceAllImages = true,
                ForceSave = true,
                IsAutomated = false,
                RemoveOldMetadata = true
            };

            _providerManager.QueueRefresh(libraryId, refreshOptions, RefreshPriority.Low);
            _logger.PolyglotInfo("RefreshLibraryAsync: Queued library refresh for {0}", libraryEntity);
        }
        catch (Exception ex)
        {
            _logger.PolyglotWarning(ex, "RefreshLibraryAsync: Failed to queue refresh for {0}", libraryEntity);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets a virtual folder by its ID.
    /// </summary>
    private MediaBrowser.Model.Entities.VirtualFolderInfo? GetVirtualFolderById(Guid id)
    {
        return _libraryManager.GetVirtualFolders()
            .FirstOrDefault(f => Guid.TryParse(f.ItemId, out var folderId) && folderId == id);
    }

    /// <summary>
    /// Safely reports progress without throwing if the callback fails.
    /// Progress reporting failures should not affect the core sync operation.
    /// </summary>
    private void SafeReportProgress(IProgress<double>? progress, double value)
    {
        if (progress == null)
        {
            return;
        }

        try
        {
            progress.Report(value);
        }
        catch (Exception ex)
        {
            // Progress callback failed (e.g., UI component disposed) - log but don't affect the operation
            _logger.PolyglotDebug("Progress callback failed: {0}", ex.Message);
        }
    }
}
