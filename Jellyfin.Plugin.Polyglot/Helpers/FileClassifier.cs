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
    /// File extensions to exclude from hardlinking (metadata and images).
    /// </summary>
    private static readonly HashSet<string> ExcludedExtensions = new(StringComparer.OrdinalIgnoreCase)
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
    /// Base filenames (without extension) that indicate metadata/artwork to exclude.
    /// </summary>
    private static readonly HashSet<string> ExcludedBaseNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Primary images
        "poster",
        "cover",
        "folder",
        "default",
        "movie",
        "show",
        "series",

        // Backdrop images
        "backdrop",
        "fanart",
        "background",
        "art",

        // Other artwork
        "banner",
        "logo",
        "clearlogo",
        "thumb",
        "landscape",
        "disc",
        "cdart",
        "discart",
        "clearart",

        // Season/episode specific
        "season",
        "episode",

        // NFO files
        "tvshow",
        "movie"
    };

    /// <summary>
    /// Filename suffixes that indicate metadata (e.g., "episode-thumb", "movie-poster").
    /// </summary>
    private static readonly string[] ExcludedSuffixes = new[]
    {
        "-thumb",
        "-poster",
        "-backdrop",
        "-fanart",
        "-banner",
        "-logo",
        "-clearlogo",
        "-clearart",
        "-landscape",
        "-disc",
        "-discart",
        "-cdart"
    };

    /// <summary>
    /// Prefixes that indicate numbered backdrops/fanart (e.g., "backdrop1", "fanart2").
    /// </summary>
    private static readonly string[] NumberedPrefixes = new[]
    {
        "backdrop",
        "fanart",
        "art"
    };

    /// <summary>
    /// Directory names that should be completely excluded from mirroring.
    /// </summary>
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "extrafanart",
        "extrathumbs",
        ".trickplay",
        "metadata",
        ".actors"
    };

    /// <summary>
    /// Determines whether a file should be hardlinked (included in the mirror).
    /// </summary>
    /// <param name="filePath">The full path to the file.</param>
    /// <returns>True if the file should be hardlinked; false if it should be excluded.</returns>
    public static bool ShouldHardlink(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return false;
        }

        // Check if file is within an excluded directory
        if (IsInExcludedDirectory(filePath))
        {
            return false;
        }

        var extension = Path.GetExtension(filePath);

        // Check extension - all files with excluded extensions should be excluded
        // This includes all NFO files (metadata) and all image files (artwork)
        // Per the plan: "NFO Metadata" and image files are language-specific content
        if (ExcludedExtensions.Contains(extension))
        {
            return false;
        }

        // All other files (video, audio, subtitles, etc.) should be hardlinked
        return true;
    }

    /// <summary>
    /// Determines whether a directory should be excluded from mirroring.
    /// </summary>
    /// <param name="directoryPath">The full path to the directory.</param>
    /// <returns>True if the directory should be excluded; false otherwise.</returns>
    public static bool ShouldExcludeDirectory(string directoryPath)
    {
        if (string.IsNullOrEmpty(directoryPath))
        {
            return false;
        }

        var dirName = Path.GetFileName(directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return ExcludedDirectories.Contains(dirName);
    }

    /// <summary>
    /// Checks if the file path is within an excluded directory.
    /// </summary>
    private static bool IsInExcludedDirectory(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        while (!string.IsNullOrEmpty(directory))
        {
            var dirName = Path.GetFileName(directory);
            if (ExcludedDirectories.Contains(dirName))
            {
                return true;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return false;
    }

    /// <summary>
    /// Checks if the extension is an image format.
    /// </summary>
    private static bool IsImageExtension(string extension)
    {
        return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tbn", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines if a base filename indicates an artwork file that should be excluded.
    /// </summary>
    private static bool IsArtworkFile(string baseName)
    {
        if (string.IsNullOrEmpty(baseName))
        {
            return false;
        }

        // Direct match with excluded base names
        if (ExcludedBaseNames.Contains(baseName))
        {
            return true;
        }

        // Check for excluded suffixes (e.g., "episode-thumb")
        foreach (var suffix in ExcludedSuffixes)
        {
            if (baseName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Check for numbered patterns (e.g., "backdrop1", "backdrop-1", "fanart2")
        foreach (var prefix in NumberedPrefixes)
        {
            if (baseName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var remainder = baseName.Substring(prefix.Length);
                // Check for patterns like "1", "-1", "_1"
                if (string.IsNullOrEmpty(remainder))
                {
                    return true;
                }

                if (remainder.StartsWith("-") || remainder.StartsWith("_"))
                {
                    remainder = remainder.Substring(1);
                }

                if (int.TryParse(remainder, out _))
                {
                    return true;
                }
            }
        }

        // Check for season-specific patterns (e.g., "season01-poster", "season1-banner")
        if (baseName.StartsWith("season", StringComparison.OrdinalIgnoreCase))
        {
            return true;
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

