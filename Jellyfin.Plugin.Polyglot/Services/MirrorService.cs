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
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Polyglot.Services;

/// <summary>
/// Service implementation for managing library mirroring operations.
/// </summary>
public class MirrorService : IMirrorService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<MirrorService> _logger;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _mirrorLocks = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="MirrorService"/> class.
    /// </summary>
    public MirrorService(ILibraryManager libraryManager, ILogger<MirrorService> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task CreateMirrorAsync(LanguageAlternative alternative, LibraryMirror mirror, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Creating mirror for library {SourceLibrary} to {TargetPath}",
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

            _logger.LogInformation("Mirror created successfully with {FileCount} files", fileCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create mirror for library {SourceLibrary}", mirror.SourceLibraryName);
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
        _logger.LogInformation("Syncing mirror {MirrorId} ({SourceLibrary})", mirror.Id, mirror.SourceLibraryName);

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
            var sourceFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sourcePath in sourcePaths)
            {
                foreach (var file in EnumerateFilesForMirroring(sourcePath))
                {
                    var relativePath = Path.GetRelativePath(sourcePath, file);
                    sourceFiles.Add(relativePath);
                }
            }

            var targetFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in EnumerateFilesForMirroring(mirror.TargetPath))
            {
                var relativePath = Path.GetRelativePath(mirror.TargetPath, file);
                targetFiles.Add(relativePath);
            }

            // Calculate differences
            var filesToAdd = sourceFiles.Except(targetFiles).ToList();
            var filesToRemove = targetFiles.Except(sourceFiles).ToList();

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
                        _logger.LogDebug("Deleted file {File}", targetFile);
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
                    _logger.LogWarning(ex, "Failed to delete file {File}", targetFile);
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
                        _logger.LogDebug("Created hardlink for {File}", relativePath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to create hardlink for {File}", relativePath);
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

            _logger.LogInformation("Mirror sync completed: {Added} added, {Removed} removed",
                filesToAdd.Count, filesToRemove.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync mirror {MirrorId}", mirror.Id);
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
        _logger.LogInformation("Deleting mirror {MirrorId} (deleteLibrary: {DeleteLibrary}, deleteFiles: {DeleteFiles})",
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
                    _libraryManager.RemoveVirtualFolder(mirror.TargetLibraryName, false);
                    _logger.LogInformation("Removed Jellyfin library {LibraryName}", mirror.TargetLibraryName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to remove Jellyfin library {LibraryName}", mirror.TargetLibraryName);
                }
            }

            // Delete mirror files
            if (deleteFiles && Directory.Exists(mirror.TargetPath))
            {
                try
                {
                    Directory.Delete(mirror.TargetPath, true);
                    _logger.LogInformation("Deleted mirror directory {Path}", mirror.TargetPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete mirror directory {Path}", mirror.TargetPath);
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
        _logger.LogInformation("Syncing all mirrors for language alternative {Name}", alternative.Name);

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
                _logger.LogError(ex, "Failed to sync mirror {MirrorId}", mirror.Id);
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
                        _logger.LogInformation(
                            "Source library {SourceLibraryId} ({SourceName}) has no more mirrors",
                            mirror.SourceLibraryId,
                            mirror.SourceLibraryName);
                    }
                }

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

        foreach (var sourceFile in EnumerateFilesForMirroring(sourcePath))
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
    /// Enumerates files that should be mirrored (excludes metadata).
    /// </summary>
    private IEnumerable<string> EnumerateFilesForMirroring(string path)
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

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            // Check if file is within an excluded directory
            var fileDir = Path.GetDirectoryName(file);
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

            if (!isInExcludedDir && FileClassifier.ShouldHardlink(file, excludedExtensions, excludedDirectoryNames))
            {
                yield return file;
            }
        }
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
        }

        _logger.LogInformation("Created Jellyfin library {LibraryName} with ID {LibraryId}",
            mirror.TargetLibraryName, mirror.TargetLibraryId);
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

