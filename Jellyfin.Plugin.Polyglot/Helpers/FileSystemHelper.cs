using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Polyglot.Helpers;

/// <summary>
/// Cross-platform helper for file system operations including hardlinks.
/// </summary>
public static class FileSystemHelper
{
    /// <summary>
    /// Creates a hardlink at the specified path pointing to the source file.
    /// </summary>
    /// <param name="sourcePath">The source file to link to.</param>
    /// <param name="linkPath">The path where the hardlink will be created.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <returns>True if the hardlink was created successfully.</returns>
    public static bool CreateHardLink(string sourcePath, string linkPath, ILogger? logger = null)
    {
        if (string.IsNullOrEmpty(sourcePath))
        {
            throw new ArgumentNullException(nameof(sourcePath));
        }

        if (string.IsNullOrEmpty(linkPath))
        {
            throw new ArgumentNullException(nameof(linkPath));
        }

        if (!File.Exists(sourcePath))
        {
            logger?.LogWarning("Source file does not exist: {SourcePath}", sourcePath);
            return false;
        }

        // Ensure target directory exists
        var targetDir = Path.GetDirectoryName(linkPath);
        if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        // Remove existing file if present
        if (File.Exists(linkPath))
        {
            File.Delete(linkPath);
        }

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return CreateHardLinkWindows(sourcePath, linkPath, logger);
            }
            else
            {
                return CreateHardLinkUnix(sourcePath, linkPath, logger);
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to create hardlink from {Source} to {Link}", sourcePath, linkPath);
            return false;
        }
    }

    /// <summary>
    /// Creates a hardlink on Windows.
    /// </summary>
    private static bool CreateHardLinkWindows(string sourcePath, string linkPath, ILogger? logger)
    {
        bool result = NativeMethods.CreateHardLink(linkPath, sourcePath, IntPtr.Zero);
        if (!result)
        {
            int error = Marshal.GetLastWin32Error();
            logger?.LogError("Windows CreateHardLink failed with error code {ErrorCode}", error);
        }

        return result;
    }

    /// <summary>
    /// Creates a hardlink on Unix/Linux/macOS.
    /// </summary>
    private static bool CreateHardLinkUnix(string sourcePath, string linkPath, ILogger? logger)
    {
        try
        {
            int result = NativeMethods.link(sourcePath, linkPath);
            if (result != 0)
            {
                int error = Marshal.GetLastWin32Error();
                logger?.LogError("Unix link() syscall failed with errno {Errno}", error);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Unix hardlink creation failed");
            return false;
        }
    }

    /// <summary>
    /// Checks if two paths are on the same filesystem (required for hardlinks).
    /// Uses a test hardlink approach for reliability across platforms.
    /// </summary>
    /// <param name="path1">First path (should exist or its parent should exist).</param>
    /// <param name="path2">Second path (target directory for mirrors).</param>
    /// <returns>True if both paths are on the same filesystem.</returns>
    public static bool AreOnSameFilesystem(string path1, string path2)
    {
        if (string.IsNullOrEmpty(path1) || string.IsNullOrEmpty(path2))
        {
            return false;
        }

        // On Windows, compare drive letters as a quick check
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return AreOnSameFilesystemWindows(path1, path2);
        }

        // On Unix, try to create a test hardlink
        return TestHardlinkCapability(path1, path2);
    }

    /// <summary>
    /// Checks filesystem on Windows by comparing volume root paths.
    /// </summary>
    private static bool AreOnSameFilesystemWindows(string path1, string path2)
    {
        try
        {
            var root1 = Path.GetPathRoot(Path.GetFullPath(path1));
            var root2 = Path.GetPathRoot(Path.GetFullPath(path2));
            return string.Equals(root1, root2, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Tests if hardlinks can be created between two paths by attempting a test hardlink.
    /// This is the most reliable cross-platform method.
    /// </summary>
    private static bool TestHardlinkCapability(string sourcePath, string targetPath)
    {
        // Find an existing file in source path to test with
        string? testSourceFile = null;
        var sourceDir = Directory.Exists(sourcePath) ? sourcePath : Path.GetDirectoryName(sourcePath);

        if (string.IsNullOrEmpty(sourceDir) || !Directory.Exists(sourceDir))
        {
            // Source doesn't exist yet, find parent that exists
            sourceDir = GetExistingParent(sourcePath);
        }

        if (string.IsNullOrEmpty(sourceDir))
        {
            return false;
        }

        // Try to find any file in the source directory to use as a test
        try
        {
            foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.TopDirectoryOnly))
            {
                testSourceFile = file;
                break;
            }
        }
        catch
        {
            // Can't enumerate, can't test
        }

        // If no file found, create a temporary test file
        bool createdTestFile = false;
        if (testSourceFile == null)
        {
            testSourceFile = Path.Combine(sourceDir, $".polyglot_test_{Guid.NewGuid():N}");
            try
            {
                File.WriteAllText(testSourceFile, "test");
                createdTestFile = true;
            }
            catch
            {
                return false;
            }
        }

        // Ensure target directory exists
        var targetDir = Directory.Exists(targetPath) ? targetPath : Path.GetDirectoryName(targetPath);
        if (string.IsNullOrEmpty(targetDir))
        {
            if (createdTestFile)
            {
                TryDeleteFile(testSourceFile);
            }

            return false;
        }

        try
        {
            Directory.CreateDirectory(targetDir);
        }
        catch
        {
            if (createdTestFile)
            {
                TryDeleteFile(testSourceFile);
            }

            return false;
        }

        // Try to create a test hardlink
        var testLinkPath = Path.Combine(targetDir, $".polyglot_test_{Guid.NewGuid():N}");
        bool canHardlink = false;

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                canHardlink = NativeMethods.CreateHardLink(testLinkPath, testSourceFile, IntPtr.Zero);
            }
            else
            {
                canHardlink = NativeMethods.link(testSourceFile, testLinkPath) == 0;
            }
        }
        catch
        {
            canHardlink = false;
        }
        finally
        {
            // Clean up test files
            TryDeleteFile(testLinkPath);
            if (createdTestFile)
            {
                TryDeleteFile(testSourceFile);
            }
        }

        return canHardlink;
    }

    /// <summary>
    /// Tries to delete a file, ignoring any errors.
    /// </summary>
    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    /// <summary>
    /// Gets the first existing parent directory.
    /// </summary>
    private static string? GetExistingParent(string path)
    {
        var current = path;
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(current))
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        return null;
    }

    /// <summary>
    /// Validates that a path doesn't contain path traversal attempts.
    /// </summary>
    /// <param name="path">Path to validate.</param>
    /// <param name="basePath">The base path that the path should be within.</param>
    /// <returns>True if the path is safe; false if it attempts traversal.</returns>
    public static bool IsPathSafe(string path, string basePath)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(basePath))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var fullBasePath = Path.GetFullPath(basePath);

            // Ensure fullPath starts with fullBasePath
            return fullPath.StartsWith(fullBasePath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Recursively deletes empty directories up to the base path.
    /// </summary>
    /// <param name="directoryPath">Directory to start cleaning from.</param>
    /// <param name="basePath">Base path to stop at (won't delete this directory).</param>
    public static void CleanupEmptyDirectories(string directoryPath, string basePath)
    {
        if (string.IsNullOrEmpty(directoryPath) || string.IsNullOrEmpty(basePath))
        {
            return;
        }

        var fullBasePath = Path.GetFullPath(basePath);
        var current = Path.GetFullPath(directoryPath);

        while (!string.IsNullOrEmpty(current) &&
               !string.Equals(current, fullBasePath, StringComparison.OrdinalIgnoreCase) &&
               current.StartsWith(fullBasePath, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (Directory.Exists(current) && IsDirectoryEmpty(current))
                {
                    Directory.Delete(current);
                    current = Path.GetDirectoryName(current);
                }
                else
                {
                    break;
                }
            }
            catch
            {
                break;
            }
        }
    }

    /// <summary>
    /// Checks if a directory is empty.
    /// </summary>
    private static bool IsDirectoryEmpty(string path)
    {
        try
        {
            foreach (var _ in Directory.EnumerateFileSystemEntries(path))
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Native method declarations for P/Invoke.
    /// </summary>
    private static class NativeMethods
    {
        /// <summary>
        /// Windows CreateHardLink function.
        /// </summary>
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        /// <summary>
        /// Unix link syscall.
        /// </summary>
        [DllImport("libc", SetLastError = true)]
        public static extern int link(string oldpath, string newpath);
    }
}

