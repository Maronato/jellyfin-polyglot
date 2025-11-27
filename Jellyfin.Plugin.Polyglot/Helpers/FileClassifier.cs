using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.Polyglot.Helpers;

/// <summary>
/// Classifies files to determine if they should be hardlinked or excluded from mirroring.
/// Uses an exclusion-based approach: hardlink everything except metadata files.
/// </summary>
public static class FileClassifier
{
    /// <summary>
    /// Default file extensions to exclude from hardlinking (metadata and images).
    /// </summary>
    public static readonly HashSet<string> DefaultExcludedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".nfo",
        ".jpg",
        ".jpeg",
        ".png",
        ".gif",
        ".webp",
        ".tbn",
        ".bmp"
    };

    /// <summary>
    /// Default directory names that should be completely excluded from mirroring.
    /// </summary>
    public static readonly HashSet<string> DefaultExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "extrafanart",
        "extrathumbs",
        ".trickplay",
        "metadata",
        ".actors"
    };

    /// <summary>
    /// Determines whether a file should be hardlinked (included in the mirror).
    /// Uses the default excluded extensions and directories.
    /// </summary>
    /// <param name="filePath">The full path to the file.</param>
    /// <returns>True if the file should be hardlinked; false if it should be excluded.</returns>
    public static bool ShouldHardlink(string filePath)
    {
        return ShouldHardlink(filePath, null, null);
    }

    /// <summary>
    /// Determines whether a file should be hardlinked (included in the mirror).
    /// </summary>
    /// <param name="filePath">The full path to the file.</param>
    /// <param name="excludedExtensions">Custom list of extensions to exclude. If null, uses defaults.</param>
    /// <param name="excludedDirectories">Custom list of directories to exclude. If null, uses defaults.</param>
    /// <returns>True if the file should be hardlinked; false if it should be excluded.</returns>
    public static bool ShouldHardlink(string filePath, IEnumerable<string>? excludedExtensions, IEnumerable<string>? excludedDirectories)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return false;
        }

        var excludedDirs = excludedDirectories != null
            ? new HashSet<string>(excludedDirectories, StringComparer.OrdinalIgnoreCase)
            : DefaultExcludedDirectories;

        var excludedExts = excludedExtensions != null
            ? new HashSet<string>(excludedExtensions, StringComparer.OrdinalIgnoreCase)
            : DefaultExcludedExtensions;

        // Check if file is within an excluded directory
        if (IsInExcludedDirectory(filePath, excludedDirs))
        {
            return false;
        }

        var extension = Path.GetExtension(filePath);

        // Check extension - all files with excluded extensions should be excluded
        // This includes all NFO files (metadata) and all image files (artwork)
        // Per the plan: "NFO Metadata" and image files are language-specific content
        if (excludedExts.Contains(extension))
        {
            return false;
        }

        // All other files (video, audio, subtitles, etc.) should be hardlinked
        return true;
    }

    /// <summary>
    /// Determines whether a directory should be excluded from mirroring.
    /// Uses the default excluded directories.
    /// </summary>
    /// <param name="directoryPath">The full path to the directory.</param>
    /// <returns>True if the directory should be excluded; false otherwise.</returns>
    public static bool ShouldExcludeDirectory(string directoryPath)
    {
        return ShouldExcludeDirectory(directoryPath, null);
    }

    /// <summary>
    /// Determines whether a directory should be excluded from mirroring.
    /// </summary>
    /// <param name="directoryPath">The full path to the directory.</param>
    /// <param name="excludedDirectories">Custom list of directories to exclude. If null, uses defaults.</param>
    /// <returns>True if the directory should be excluded; false otherwise.</returns>
    public static bool ShouldExcludeDirectory(string directoryPath, IEnumerable<string>? excludedDirectories)
    {
        if (string.IsNullOrEmpty(directoryPath))
        {
            return false;
        }

        var excludedDirs = excludedDirectories != null
            ? new HashSet<string>(excludedDirectories, StringComparer.OrdinalIgnoreCase)
            : DefaultExcludedDirectories;

        var dirName = Path.GetFileName(directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return excludedDirs.Contains(dirName);
    }

    /// <summary>
    /// Checks if the file path is within an excluded directory.
    /// </summary>
    private static bool IsInExcludedDirectory(string filePath, IReadOnlySet<string> excludedDirectories)
    {
        var directory = Path.GetDirectoryName(filePath);
        while (!string.IsNullOrEmpty(directory))
        {
            var dirName = Path.GetFileName(directory);
            if (excludedDirectories.Contains(dirName))
            {
                return true;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return false;
    }

    /// <summary>
    /// Gets the list of video file extensions that are commonly used.
    /// </summary>
    public static IReadOnlySet<string> VideoExtensions => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".m4v", ".wmv", ".mov", ".ts", ".m2ts",
        ".flv", ".webm", ".mpg", ".mpeg", ".vob", ".3gp", ".divx", ".xvid"
    };

    /// <summary>
    /// Gets the list of audio file extensions that are commonly used.
    /// </summary>
    public static IReadOnlySet<string> AudioExtensions => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".aac", ".ac3", ".dts", ".flac", ".m4a", ".ogg",
        ".wav", ".wma", ".opus", ".ape", ".mka"
    };

    /// <summary>
    /// Gets the list of subtitle file extensions.
    /// </summary>
    public static IReadOnlySet<string> SubtitleExtensions => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".sub", ".idx", ".vtt", ".sup", ".pgs"
    };
}
