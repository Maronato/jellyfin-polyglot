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

namespace Jellyfin.Plugin.Polyglot.Services;

/// <summary>
/// Service implementation for managing library mirroring operations.
/// </summary>
public class MirrorService : IMirrorService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<MirrorService> _logger;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _mirrorLocks = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="MirrorService"/> class.
    /// </summary>
    public MirrorService(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILogger<MirrorService> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task CreateMirrorAsync(LanguageAlternative alternative, LibraryMirror mirror, CancellationToken cancellationToken = default)
    {
        _logger.PolyglotInfo("Creating mirror for library {0} to {1}",
            mirror.SourceLibraryName, mirror.TargetPath);

        var mirrorLock = _mirrorLocks.GetOrAdd(mirror.Id, _ => new SemaphoreSlim(1, 1));
        await mirrorLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            mirror.Status = SyncStatus.Syncing;

            // Get source library paths
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

            // Validate filesystem compatibility
            foreach (var sourcePath in sourcePaths)
            {
                if (!FileSystemHelper.AreOnSameFilesystem(sourcePath, mirror.TargetPath))
                {
                    throw new InvalidOperationException(
                        $"Source path {sourcePath} and target path {mirror.TargetPath} are on different filesystems. " +
                        "Hardlinks require both paths to be on the same filesystem.");
                }
            }

            // Create target directory
            Directory.CreateDirectory(mirror.TargetPath);

            // Mirror each source path
            int fileCount = 0;
            foreach (var sourcePath in sourcePaths)
            {
                fileCount += await MirrorDirectoryAsync(sourcePath, mirror.TargetPath, cancellationToken).ConfigureAwait(false);
            }

            // Create Jellyfin library if not already created
            if (mirror.TargetLibraryId == null)
            {
                await CreateJellyfinLibraryAsync(alternative, mirror, cancellationToken).ConfigureAwait(false);
            }

            mirror.Status = SyncStatus.Synced;
            mirror.LastSyncedAt = DateTime.UtcNow;
            mirror.LastSyncFileCount = fileCount;
            mirror.LastError = null;

            SaveConfiguration();

            _logger.PolyglotInfo("Mirror created successfully with {0} files", fileCount);
        }
        catch (Exception ex)
        {
            _logger.PolyglotError(ex, "Failed to create mirror for library {0}", mirror.SourceLibraryName);
            mirror.Status = SyncStatus.Error;
            mirror.LastError = ex.Message;
            SaveConfiguration();
            throw;
        }
        finally
        {
            mirrorLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task SyncMirrorAsync(LibraryMirror mirror, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        _logger.PolyglotInfo("Syncing mirror {0} ({1})", mirror.Id, mirror.SourceLibraryName);

        var mirrorLock = _mirrorLocks.GetOrAdd(mirror.Id, _ => new SemaphoreSlim(1, 1));
        await mirrorLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            mirror.Status = SyncStatus.Syncing;

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
                    // We check Size and LastWriteTime
                    if (sourceSig.Size != targetSig.Size || sourceSig.ModifiedTicks != targetSig.ModifiedTicks)
                    {
                        _logger.PolyglotDebug("File modified: {0} (Source: {1}/{2}, Target: {3}/{4})",
                            relativePath, sourceSig.Size, sourceSig.ModifiedTicks, targetSig.Size, targetSig.ModifiedTicks);
                        
                        // Treat as remove + add to update the hardlink
                        filesToRemove.Add(relativePath);
                        filesToAdd.Add(relativePath);
                    }
                }
                else
                {
                    // New file
                    filesToAdd.Add(relativePath);
                }
            }

            // Check for deleted files (exists in target but not source)
            foreach (var kvp in targetFiles)
            {
                if (!sourceFiles.ContainsKey(kvp.Key))
                {
                    filesToRemove.Add(kvp.Key);
                }
            }

            var totalOperations = filesToAdd.Count + filesToRemove.Count;
            var completedOperations = 0;

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
                        _logger.PolyglotDebug("Deleted file {0}", targetFile);
                    }

                    // Clean up empty directories
                    var dir = Path.GetDirectoryName(targetFile);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        FileSystemHelper.CleanupEmptyDirectories(dir, mirror.TargetPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.PolyglotWarning(ex, "Failed to delete file {0}", targetFile);
                }

                completedOperations++;
                progress?.Report((double)completedOperations / totalOperations * 100);
            }

            // Add new files
            foreach (var relativePath in filesToAdd)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Find source file in any of the source paths
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
                        _logger.PolyglotDebug("Created hardlink for {0}", relativePath);
                    }
                    catch (Exception ex)
                    {
                        _logger.PolyglotWarning(ex, "Failed to create hardlink for {0}", relativePath);
                    }
                }

                completedOperations++;
                progress?.Report((double)completedOperations / totalOperations * 100);
            }

            mirror.Status = SyncStatus.Synced;
            mirror.LastSyncedAt = DateTime.UtcNow;
            mirror.LastSyncFileCount = sourceFiles.Count;
            mirror.LastError = null;

            SaveConfiguration();

            _logger.PolyglotInfo("Mirror sync completed: {0} added, {1} removed",
                filesToAdd.Count, filesToRemove.Count);
        }
        catch (Exception ex)
        {
            _logger.PolyglotError(ex, "Failed to sync mirror {0}", mirror.Id);
            mirror.Status = SyncStatus.Error;
            mirror.LastError = ex.Message;
            SaveConfiguration();
            throw;
        }
        finally
        {
            mirrorLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task DeleteMirrorAsync(LibraryMirror mirror, bool deleteLibrary = true, bool deleteFiles = true, CancellationToken cancellationToken = default)
    {
        _logger.PolyglotInfo("Deleting mirror {0} (deleteLibrary: {1}, deleteFiles: {2})",
            mirror.Id, deleteLibrary, deleteFiles);

        var mirrorLock = _mirrorLocks.GetOrAdd(mirror.Id, _ => new SemaphoreSlim(1, 1));
        await mirrorLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Delete Jellyfin library
            if (deleteLibrary && mirror.TargetLibraryId.HasValue)
            {
                try
                {
                    // Use refreshLibrary: true to trigger Jellyfin's internal cleanup
                    // This ensures all references to the library are properly removed
                    // (similar to what happens when deleting via Jellyfin's UI)
                    _libraryManager.RemoveVirtualFolder(mirror.TargetLibraryName, true);
                    _logger.PolyglotInfo("Removed Jellyfin library {0}", mirror.TargetLibraryName);
                }
                catch (Exception ex)
                {
                    _logger.PolyglotWarning(ex, "Failed to remove Jellyfin library {0}", mirror.TargetLibraryName);
                }
            }

            // Delete mirror files
            if (deleteFiles && Directory.Exists(mirror.TargetPath))
            {
                try
                {
                    Directory.Delete(mirror.TargetPath, true);
                    _logger.PolyglotInfo("Deleted mirror directory {0}", mirror.TargetPath);
                }
                catch (Exception ex)
                {
                    _logger.PolyglotWarning(ex, "Failed to delete mirror directory {0}", mirror.TargetPath);
                }
            }

            _mirrorLocks.TryRemove(mirror.Id, out _);
        }
        finally
        {
            mirrorLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task SyncAllMirrorsAsync(LanguageAlternative alternative, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        _logger.PolyglotInfo("Syncing all mirrors for language alternative {0}", alternative.Name);

        var mirrors = alternative.MirroredLibraries.ToList();
        var totalMirrors = mirrors.Count;

        for (int i = 0; i < mirrors.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var mirror = mirrors[i];
            var mirrorProgress = new Progress<double>(p =>
            {
                var overallProgress = ((i * 100.0) + p) / totalMirrors;
                progress?.Report(overallProgress);
            });

            try
            {
                await SyncMirrorAsync(mirror, mirrorProgress, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.PolyglotError(ex, "Failed to sync mirror {0}", mirror.Id);
                // Continue with other mirrors
            }
        }
    }

    /// <inheritdoc />
    public (bool IsValid, string? ErrorMessage) ValidateMirrorConfiguration(Guid sourceLibraryId, string targetPath)
    {
        // Check source library exists
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

        // Check target path is valid
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return (false, "Target path is required");
        }

        // Check for path traversal
        if (targetPath.Contains(".."))
        {
            return (false, "Target path cannot contain path traversal sequences");
        }

        // Check filesystem compatibility
        foreach (var sourcePath in sourcePaths)
        {
            if (!FileSystemHelper.AreOnSameFilesystem(sourcePath, targetPath))
            {
                return (false, $"Source path '{sourcePath}' and target path are on different filesystems. Hardlinks require the same filesystem.");
            }
        }

        // Check target isn't inside source
        foreach (var sourcePath in sourcePaths)
        {
            var fullSource = Path.GetFullPath(sourcePath);
            var fullTarget = Path.GetFullPath(targetPath);
            if (fullTarget.StartsWith(fullSource, StringComparison.OrdinalIgnoreCase))
            {
                return (false, "Target path cannot be inside the source library path");
            }
        }

        return (true, null);
    }

    /// <inheritdoc />
    public IEnumerable<LibraryInfo> GetJellyfinLibraries()
    {
        var virtualFolders = _libraryManager.GetVirtualFolders();
        var config = Plugin.Instance?.Configuration;

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
            if (config != null)
            {
                foreach (var alt in config.LanguageAlternatives)
                {
                    var mirror = alt.MirroredLibraries.FirstOrDefault(m => m.TargetLibraryId == libraryInfo.Id);
                    if (mirror != null)
                    {
                        libraryInfo.IsMirror = true;
                        libraryInfo.LanguageAlternativeId = alt.Id;
                        break;
                    }
                }
            }

            yield return libraryInfo;
        }
    }

    /// <inheritdoc />
    public async Task<OrphanCleanupResult> CleanupOrphanedMirrorsAsync(CancellationToken cancellationToken = default)
    {
        var result = new OrphanCleanupResult();

        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return result;
        }

        var existingLibraryIds = GetJellyfinLibraries()
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
                    _logger.PolyglotWarning(
                        "Source library {0} for mirror {1} no longer exists - will delete mirror",
                        mirror.SourceLibraryId,
                        mirror.Id);

                    mirrorsToDelete.Add((alternative, mirror, "source deleted"));
                    continue;
                }

                // Check if target library still exists (if it was created)
                if (mirror.TargetLibraryId.HasValue && !existingLibraryIds.Contains(mirror.TargetLibraryId.Value))
                {
                    _logger.PolyglotWarning(
                        "Target library {0} for mirror {1} no longer exists - removing mirror config",
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
                await DeleteMirrorAsync(mirror, deleteLibrary: false, deleteFiles: deleteFiles, cancellationToken)
                    .ConfigureAwait(false);

                // Remove from alternative's mirror list
                alternative.MirroredLibraries.Remove(mirror);

                result.CleanedUpMirrors.Add($"{mirror.TargetLibraryName} ({reason})");

                // Check if this source now has NO mirrors from any language
                if (reason == "mirror deleted")
                {
                    var sourceHasOtherMirrors = config.LanguageAlternatives
                        .SelectMany(a => a.MirroredLibraries)
                        .Any(m => m.SourceLibraryId == mirror.SourceLibraryId);

                    if (!sourceHasOtherMirrors && existingLibraryIds.Contains(mirror.SourceLibraryId))
                    {
                        result.SourcesWithoutMirrors.Add(mirror.SourceLibraryId);
                        _logger.PolyglotInfo(
                            "Source library {0} ({1}) has no more mirrors",
                            mirror.SourceLibraryId,
                            mirror.SourceLibraryName);
                    }
                }

                _logger.PolyglotInfo(
                    "Removed orphaned mirror {0} ({1})",
                    mirror.TargetLibraryName,
                    reason);
            }
            catch (Exception ex)
            {
                _logger.PolyglotError(ex, "Failed to delete orphaned mirror {0}", mirror.Id);
            }
        }

        if (mirrorsToDelete.Count > 0)
        {
            SaveConfiguration();
        }

        result.TotalCleaned = result.CleanedUpMirrors.Count;
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

        // Get configured exclusions or use defaults
        var config = Plugin.Instance?.Configuration;
        var excludedExtensions = config?.ExcludedExtensions;
        var excludedDirectoryNames = config?.ExcludedDirectories;

        // Build set of excluded directory paths
        var excludedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
        {
            if (FileClassifier.ShouldExcludeDirectory(directory, excludedDirectoryNames))
            {
                excludedDirs.Add(directory);
            }
        }

        // Use DirectoryInfo to get file metadata efficiently
        var dirInfo = new DirectoryInfo(path);
        foreach (var fileInfo in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            // Check if file is within an excluded directory
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
    /// used to detect if a file has been modified even if the name is the same.
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
    /// </summary>
    private async Task CreateJellyfinLibraryAsync(LanguageAlternative alternative, LibraryMirror mirror, CancellationToken cancellationToken)
    {
        // Get source library options to copy settings from
        var sourceLibrary = GetVirtualFolderById(mirror.SourceLibraryId);
        var sourceOptions = sourceLibrary?.LibraryOptions;

        var options = new MediaBrowser.Model.Configuration.LibraryOptions
        {
            // Language settings - set from the alternative (the whole point of mirrors!)
            PreferredMetadataLanguage = alternative.MetadataLanguage,
            MetadataCountryCode = alternative.MetadataCountry,

            // MUST be false for mirrors - hardlinked files share the same physical location,
            // so saving metadata/subtitles next to files would cause conflicts between libraries
            SaveLocalMetadata = false,
            SaveSubtitlesWithMedia = false,
            SaveLyricsWithMedia = false,

            // Enable monitoring for changes
            EnableRealtimeMonitor = true,
            Enabled = true
        };

        // Copy settings from source library if available
        if (sourceOptions != null)
        {
            // TypeOptions - critical! Contains metadata fetchers, image fetchers, their order
            options.TypeOptions = sourceOptions.TypeOptions;

            // Metadata handling
            options.MetadataSavers = sourceOptions.MetadataSavers;
            options.DisabledLocalMetadataReaders = sourceOptions.DisabledLocalMetadataReaders;
            options.LocalMetadataReaderOrder = sourceOptions.LocalMetadataReaderOrder;

            // Subtitle settings
            options.DisabledSubtitleFetchers = sourceOptions.DisabledSubtitleFetchers;
            options.SubtitleFetcherOrder = sourceOptions.SubtitleFetcherOrder;
            options.SubtitleDownloadLanguages = sourceOptions.SubtitleDownloadLanguages;
            options.SkipSubtitlesIfEmbeddedSubtitlesPresent = sourceOptions.SkipSubtitlesIfEmbeddedSubtitlesPresent;
            options.SkipSubtitlesIfAudioTrackMatches = sourceOptions.SkipSubtitlesIfAudioTrackMatches;
            options.RequirePerfectSubtitleMatch = sourceOptions.RequirePerfectSubtitleMatch;
            options.AllowEmbeddedSubtitles = sourceOptions.AllowEmbeddedSubtitles;

            // Lyric settings (music)
            options.DisabledLyricFetchers = sourceOptions.DisabledLyricFetchers;
            options.LyricFetcherOrder = sourceOptions.LyricFetcherOrder;

            // Media segment providers (intro/credits detection)
            options.DisabledMediaSegmentProviders = sourceOptions.DisabledMediaSegmentProviders;
            options.MediaSegmentProvideOrder = sourceOptions.MediaSegmentProvideOrder;

            // Image/chapter extraction
            options.EnablePhotos = sourceOptions.EnablePhotos;
            options.EnableChapterImageExtraction = sourceOptions.EnableChapterImageExtraction;
            options.ExtractChapterImagesDuringLibraryScan = sourceOptions.ExtractChapterImagesDuringLibraryScan;
            options.EnableTrickplayImageExtraction = sourceOptions.EnableTrickplayImageExtraction;
            options.ExtractTrickplayImagesDuringLibraryScan = sourceOptions.ExtractTrickplayImagesDuringLibraryScan;
            options.SaveTrickplayWithMedia = sourceOptions.SaveTrickplayWithMedia;

            // Organization settings
            options.AutomaticallyAddToCollection = sourceOptions.AutomaticallyAddToCollection;
            options.EnableAutomaticSeriesGrouping = sourceOptions.EnableAutomaticSeriesGrouping;
            options.SeasonZeroDisplayName = sourceOptions.SeasonZeroDisplayName;

            // Embedded info
            options.EnableEmbeddedTitles = sourceOptions.EnableEmbeddedTitles;
            options.EnableEmbeddedExtrasTitles = sourceOptions.EnableEmbeddedExtrasTitles;
            options.EnableEmbeddedEpisodeInfos = sourceOptions.EnableEmbeddedEpisodeInfos;

            // Music tag parsing
            options.PreferNonstandardArtistsTag = sourceOptions.PreferNonstandardArtistsTag;
            options.UseCustomTagDelimiters = sourceOptions.UseCustomTagDelimiters;
            options.CustomTagDelimiters = sourceOptions.CustomTagDelimiters;
            options.DelimiterWhitelist = sourceOptions.DelimiterWhitelist;

            // Refresh settings
            options.AutomaticRefreshIntervalDays = sourceOptions.AutomaticRefreshIntervalDays;
            options.EnableLUFSScan = sourceOptions.EnableLUFSScan;
            options.EnableInternetProviders = true; // Always enable - mirrors need to fetch metadata
        }

        // Parse collection type from string to enum
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

        // Add the path to the library
        var createdLibrary = _libraryManager.GetVirtualFolders()
            .FirstOrDefault(f => string.Equals(f.Name, mirror.TargetLibraryName, StringComparison.OrdinalIgnoreCase));

        if (createdLibrary != null)
        {
            mirror.TargetLibraryId = Guid.Parse(createdLibrary.ItemId);

            // Add the media path
            _libraryManager.AddMediaPath(mirror.TargetLibraryName, new MediaBrowser.Model.Configuration.MediaPathInfo
            {
                Path = mirror.TargetPath
            });

            // Trigger a full library scan for the newly created library
            // This is necessary because AddVirtualFolder doesn't always trigger a scan
            await RefreshLibraryAsync(mirror.TargetLibraryId.Value, cancellationToken).ConfigureAwait(false);
        }

        _logger.PolyglotInfo("Created Jellyfin library {0} with ID {1}",
            mirror.TargetLibraryName, mirror.TargetLibraryId);
    }

    /// <summary>
    /// Triggers a library scan to discover files on the filesystem and fetch metadata.
    /// This uses QueueRefresh with FullRefresh options, which matches the behavior of
    /// POST /Items/{id}/Refresh?MetadataRefreshMode=FullRefresh&amp;ImageRefreshMode=FullRefresh&amp;...
    /// </summary>
    private Task RefreshLibraryAsync(Guid libraryId, CancellationToken cancellationToken)
    {
        _logger.PolyglotInfo("Queueing library refresh for {0}", libraryId);

        try
        {
            // Use the same approach as Jellyfin's ItemRefreshController:
            // QueueRefresh with FullRefresh mode discovers new files AND fetches metadata.
            // This matches the working API call:
            // POST /Items/{id}/Refresh?MetadataRefreshMode=FullRefresh&ImageRefreshMode=FullRefresh&ReplaceAllMetadata=true&ReplaceAllImages=true
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

            // QueueRefresh queues the refresh to be processed asynchronously (like the API does)
            // Using Low priority to let the filesystem/Jellyfin settle after hardlink creation
            _providerManager.QueueRefresh(libraryId, refreshOptions, RefreshPriority.Low);

            _logger.PolyglotInfo("Successfully queued library refresh for {0}", libraryId);
        }
        catch (Exception ex)
        {
            // Log but don't fail the mirror creation - the library exists, just the scan didn't start
            _logger.PolyglotWarning(ex, "Failed to queue refresh for library {0}. The library was created but may need a manual scan.", libraryId);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets a virtual folder by its ID.
    /// </summary>
    private MediaBrowser.Model.Entities.VirtualFolderInfo? GetVirtualFolderById(Guid id)
    {
        // Compare Guids directly - VirtualFolderInfo.ItemId is a string but represents a Guid
        return _libraryManager.GetVirtualFolders()
            .FirstOrDefault(f => Guid.TryParse(f.ItemId, out var folderId) && folderId == id);
    }

    /// <summary>
    /// Saves the plugin configuration.
    /// </summary>
    private void SaveConfiguration()
    {
        Plugin.Instance?.SaveConfiguration();
    }
}


