using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Polyglot.Helpers;
using Jellyfin.Plugin.Polyglot.Models;
using MediaBrowser.Common;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Polyglot.Services;

/// <summary>
/// Service for generating debug reports for troubleshooting.
/// </summary>
public partial class DebugReportService : IDebugReportService
{
    private readonly IApplicationHost _applicationHost;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<DebugReportService> _logger;

    // Static circular buffer for recent logs (accessible across the plugin)
    private static readonly ConcurrentQueue<LogEntryInfo> LogBuffer = new();
    private const int MaxLogEntries = 500;
    private static readonly TimeSpan MaxLogAge = TimeSpan.FromHours(1);

    /// <summary>
    /// Static method to log to the buffer without requiring a service instance.
    /// Used by extension methods and other components.
    /// </summary>
    /// <param name="level">Log level.</param>
    /// <param name="message">Log message.</param>
    /// <param name="exception">Optional exception message.</param>
    public static void LogToBufferStatic(string level, string message, string? exception = null)
    {
        var entry = new LogEntryInfo
        {
            Timestamp = DateTime.UtcNow,
            Level = level,
            Message = SanitizeLogMessage(message),
            Exception = exception != null ? SanitizeLogMessage(exception) : null
        };

        LogBuffer.Enqueue(entry);

        // Trim old entries
        while (LogBuffer.Count > MaxLogEntries)
        {
            LogBuffer.TryDequeue(out _);
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DebugReportService"/> class.
    /// </summary>
    public DebugReportService(
        IApplicationHost applicationHost,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILogger<DebugReportService> logger)
    {
        _applicationHost = applicationHost;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public void LogToBuffer(string level, string message, string? exception = null)
    {
        LogToBufferStatic(level, message, exception);
    }

    /// <inheritdoc />
    public async Task<DebugReport> GenerateReportAsync(DebugReportOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new DebugReportOptions();

        var report = new DebugReport
        {
            GeneratedAt = DateTime.UtcNow,
            Options = options,
            Environment = GetEnvironmentInfo(),
            Configuration = GetConfigurationSummary(),
            MirrorHealth = await GetMirrorHealthAsync(options, cancellationToken).ConfigureAwait(false),
            UserDistribution = GetUserDistribution(options),
            Libraries = GetLibrarySummaries(options),
            OtherPlugins = GetOtherPlugins(),
            RecentLogs = GetRecentLogs(options)
        };

        // Add filesystem diagnostics if requested
        if (options.IncludeFilesystemDiagnostics)
        {
            report.FilesystemInfo = GetFilesystemDiagnostics(options);
        }

        // Add hardlink verification if requested
        if (options.IncludeHardlinkVerification)
        {
            report.HardlinkVerification = await VerifyHardlinksAsync(options, cancellationToken).ConfigureAwait(false);
        }

        // Add user details if requested
        if (options.IncludeUserNames)
        {
            report.UserDetails = GetUserDetails(options);
        }

        return report;
    }

    /// <inheritdoc />
    public async Task<string> GenerateMarkdownReportAsync(DebugReportOptions? options = null, CancellationToken cancellationToken = default)
    {
        var report = await GenerateReportAsync(options, cancellationToken).ConfigureAwait(false);
        return FormatAsMarkdown(report);
    }

    private EnvironmentInfo GetEnvironmentInfo()
    {
        var pluginVersion = Plugin.Instance?.Version?.ToString() ?? "Unknown";
        var jellyfinVersion = _applicationHost.ApplicationVersionString;

        return new EnvironmentInfo
        {
            PluginVersion = pluginVersion,
            JellyfinVersion = jellyfinVersion,
            OperatingSystem = RuntimeInformation.OSDescription,
            DotNetVersion = Environment.Version.ToString(),
            Architecture = RuntimeInformation.ProcessArchitecture.ToString()
        };
    }

    private ConfigurationSummary GetConfigurationSummary()
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return new ConfigurationSummary();
        }

        var totalMirrors = config.LanguageAlternatives.Sum(a => a.MirroredLibraries.Count);
        var managedUsers = config.UserLanguages.Count(u => u.IsPluginManaged);

        return new ConfigurationSummary
        {
            LanguageAlternativeCount = config.LanguageAlternatives.Count,
            TotalMirrorCount = totalMirrors,
            ManagedUserCount = managedUsers,
            AutoManageNewUsers = config.AutoManageNewUsers,
            SyncAfterLibraryScan = config.SyncMirrorsAfterLibraryScan,
            LdapIntegrationEnabled = config.EnableLdapIntegration,
            ExcludedExtensionCount = config.ExcludedExtensions.Count,
            ExcludedDirectoryCount = config.ExcludedDirectories.Count
        };
    }

    private async Task<List<MirrorHealthInfo>> GetMirrorHealthAsync(DebugReportOptions options, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return new List<MirrorHealthInfo>();
        }

        var results = new List<MirrorHealthInfo>();
        var existingLibraryIds = GetExistingLibraryIds();
        var virtualFolders = _libraryManager.GetVirtualFolders();
        var altIndex = 0;

        foreach (var alternative in config.LanguageAlternatives)
        {
            altIndex++;
            var mirrorIndex = 0;

            foreach (var mirror in alternative.MirroredLibraries)
            {
                mirrorIndex++;
                cancellationToken.ThrowIfCancellationRequested();

                var sourceExists = existingLibraryIds.Contains(mirror.SourceLibraryId);
                var targetExists = mirror.TargetLibraryId.HasValue && existingLibraryIds.Contains(mirror.TargetLibraryId.Value);
                var targetPathExists = !string.IsNullOrEmpty(mirror.TargetPath) && Directory.Exists(mirror.TargetPath);

                // Get source library paths
                var sourceFolder = virtualFolders.FirstOrDefault(f =>
                    Guid.TryParse(f.ItemId, out var id) && id == mirror.SourceLibraryId);
                var sourcePaths = sourceFolder?.Locations ?? Array.Empty<string>();
                var sourcePathStr = sourcePaths.Length > 0 ? string.Join("; ", sourcePaths) : null;
                var sourcePathExists = sourcePaths.Length > 0 && sourcePaths.All(Directory.Exists);

                var lastSync = mirror.LastSyncedAt.HasValue
                    ? FormatTimeAgo(mirror.LastSyncedAt.Value)
                    : "Never";

                // Check if target path is writable
                bool? targetPathWritable = null;
                if (targetPathExists && !string.IsNullOrEmpty(mirror.TargetPath))
                {
                    targetPathWritable = IsPathWritable(mirror.TargetPath);
                }

                // Determine names based on options
                var altName = options.IncludeLibraryNames ? alternative.Name : $"Alt_{altIndex}";
                var libName = options.IncludeLibraryNames ? mirror.SourceLibraryName : $"Library_{mirrorIndex}";
                var targetPath = options.IncludeFilePaths ? mirror.TargetPath : (targetPathExists ? "[path exists]" : "[path missing]");
                var sourcePath = options.IncludeFilePaths ? sourcePathStr : (sourcePathExists ? "[path exists]" : "[path missing/unknown]");

                results.Add(new MirrorHealthInfo
                {
                    AlternativeName = altName,
                    SourceLibrary = libName,
                    SourcePath = sourcePath,
                    SourcePathExists = sourcePaths.Length > 0 ? sourcePathExists : null,
                    TargetPath = targetPath,
                    Status = mirror.Status.ToString(),
                    LastSync = lastSync,
                    FileCount = mirror.LastSyncFileCount,
                    SourceExists = sourceExists,
                    TargetExists = targetExists,
                    TargetPathExists = targetPathExists,
                    TargetPathWritable = targetPathWritable,
                    LastError = options.IncludeFilePaths ? mirror.LastError : SanitizeErrorMessage(mirror.LastError)
                });
            }
        }

        return await Task.FromResult(results).ConfigureAwait(false);
    }

    private static bool IsPathWritable(string path)
    {
        try
        {
            var testFile = Path.Combine(path, $".polyglot_write_test_{Guid.NewGuid():N}");
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private List<UserDistributionInfo> GetUserDistribution(DebugReportOptions options)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return new List<UserDistributionInfo>();
        }

        var distribution = new List<UserDistributionInfo>();

        // Count users per alternative
        var managedUsers = config.UserLanguages.Where(u => u.IsPluginManaged).ToList();

        // Count users with no specific alternative (default)
        var defaultCount = managedUsers.Count(u => u.SelectedAlternativeId == null);
        if (defaultCount > 0)
        {
            distribution.Add(new UserDistributionInfo
            {
                Language = "Default (source libraries)",
                UserCount = defaultCount
            });
        }

        // Group by non-null alternatives
        var usersByAlt = managedUsers
            .Where(u => u.SelectedAlternativeId.HasValue)
            .GroupBy(u => u.SelectedAlternativeId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        // Per alternative
        var altIndex = 0;
        foreach (var alt in config.LanguageAlternatives)
        {
            altIndex++;
            var count = usersByAlt.GetValueOrDefault(alt.Id, 0);
            var langName = options.IncludeLibraryNames
                ? $"{alt.Name} ({alt.LanguageCode})"
                : $"Alt_{altIndex} ({alt.LanguageCode})";
            distribution.Add(new UserDistributionInfo
            {
                Language = langName,
                UserCount = count
            });
        }

        // Not managed
        var notManagedCount = config.UserLanguages.Count(u => !u.IsPluginManaged);
        if (notManagedCount > 0)
        {
            distribution.Add(new UserDistributionInfo
            {
                Language = "Not managed by plugin",
                UserCount = notManagedCount
            });
        }

        return distribution;
    }

    private List<UserDetailInfo> GetUserDetails(DebugReportOptions options)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return new List<UserDetailInfo>();
        }

        var details = new List<UserDetailInfo>();
        var altIndex = 0;
        var altMap = config.LanguageAlternatives.ToDictionary(
            a => a.Id,
            a => new { Index = ++altIndex, Alt = a });

        // Build a lookup of user IDs to usernames
        var userLookup = new Dictionary<Guid, string>();
        try
        {
            var users = _userManager.Users;
            foreach (var user in users)
            {
                userLookup[user.Id] = user.Username;
            }
        }
        catch
        {
            // If we can't get users, we'll fall back to showing IDs
        }

        foreach (var userConfig in config.UserLanguages)
        {
            string language;
            if (userConfig.SelectedAlternativeId == null)
            {
                language = "Default (source libraries)";
            }
            else if (altMap.TryGetValue(userConfig.SelectedAlternativeId.Value, out var altInfo))
            {
                language = options.IncludeLibraryNames
                    ? $"{altInfo.Alt.Name} ({altInfo.Alt.LanguageCode})"
                    : $"Alt_{altInfo.Index} ({altInfo.Alt.LanguageCode})";
            }
            else
            {
                language = "Unknown (alternative deleted?)";
            }

            // Get username from lookup, fall back to ID if not found
            var userName = userLookup.TryGetValue(userConfig.UserId, out var name)
                ? name
                : $"[Unknown: {userConfig.UserId}]";

            // Anonymize username if not including user names
            if (!options.IncludeUserNames)
            {
                userName = $"User_{details.Count + 1}";
            }

            details.Add(new UserDetailInfo
            {
                UserName = userName,
                AssignedLanguage = language,
                IsManaged = userConfig.IsPluginManaged,
                AssignmentSource = userConfig.ManuallySet ? "Manual" : (userConfig.SetBy ?? "Auto")
            });
        }

        return details;
    }

    private List<LibrarySummaryInfo> GetLibrarySummaries(DebugReportOptions options)
    {
        var config = Plugin.Instance?.Configuration;
        var virtualFolders = _libraryManager.GetVirtualFolders();

        // Build set of mirror library IDs
        var mirrorIds = new HashSet<Guid>();
        if (config != null)
        {
            foreach (var alt in config.LanguageAlternatives)
            {
                foreach (var mirror in alt.MirroredLibraries)
                {
                    if (mirror.TargetLibraryId.HasValue)
                    {
                        mirrorIds.Add(mirror.TargetLibraryId.Value);
                    }
                }
            }
        }

        var results = new List<LibrarySummaryInfo>();
        var libIndex = 0;

        foreach (var folder in virtualFolders)
        {
            libIndex++;
            var folderId = Guid.TryParse(folder.ItemId, out var id) ? id : Guid.Empty;
            var isMirror = mirrorIds.Contains(folderId);
            var metadataLang = folder.LibraryOptions?.PreferredMetadataLanguage ?? "default";
            var libName = options.IncludeLibraryNames ? folder.Name : $"Library_{libIndex}";

            results.Add(new LibrarySummaryInfo
            {
                Name = libName,
                Type = folder.CollectionType?.ToString() ?? "mixed",
                IsMirror = isMirror,
                MetadataLanguage = metadataLang
            });
        }

        return results;
    }

    private List<PluginSummaryInfo> GetOtherPlugins()
    {
        try
        {
            var plugins = _applicationHost.GetExports<IPlugin>();
            var polyglotId = Plugin.Instance?.Id ?? Guid.Empty;

            return plugins
                .Where(p => p.Id != polyglotId)
                .Select(p => new PluginSummaryInfo
                {
                    Name = p.Name,
                    Version = p.Version?.ToString() ?? "Unknown"
                })
                .OrderBy(p => p.Name)
                .ToList();
        }
        catch (Exception ex)
        {
            // Log and return empty list if we can't enumerate plugins (e.g., DI loop)
            _logger.PolyglotWarning(ex, "Could not enumerate other plugins for debug report");
            return new List<PluginSummaryInfo>
            {
                new PluginSummaryInfo
                {
                    Name = "(Plugin enumeration unavailable)",
                    Version = ex.Message
                }
            };
        }
    }

    private static List<LogEntryInfo> GetRecentLogs(DebugReportOptions options)
    {
        var cutoff = DateTime.UtcNow - MaxLogAge;

        var logs = LogBuffer
            .Where(e => e.Timestamp >= cutoff)
            .OrderByDescending(e => e.Timestamp)
            .ToList();

        // If paths are not being included, sanitize the logs
        if (!options.IncludeFilePaths)
        {
            foreach (var log in logs)
            {
                log.Message = SanitizeLogMessage(log.Message);
                if (log.Exception != null)
                {
                    log.Exception = SanitizeLogMessage(log.Exception);
                }
            }
        }

        return logs;
    }

    private HashSet<Guid> GetExistingLibraryIds()
    {
        return _libraryManager.GetVirtualFolders()
            .Select(f => Guid.TryParse(f.ItemId, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToHashSet();
    }

    private List<FilesystemDiagnostics> GetFilesystemDiagnostics(DebugReportOptions options)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return new List<FilesystemDiagnostics>();
        }

        var results = new List<FilesystemDiagnostics>();
        var checkedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var virtualFolders = _libraryManager.GetVirtualFolders();

        // Check source library paths first
        foreach (var alt in config.LanguageAlternatives)
        {
            foreach (var mirror in alt.MirroredLibraries)
            {
                // Get source library paths
                var sourceFolder = virtualFolders.FirstOrDefault(f =>
                    Guid.TryParse(f.ItemId, out var id) && id == mirror.SourceLibraryId);

                if (sourceFolder?.Locations != null)
                {
                    foreach (var sourcePath in sourceFolder.Locations)
                    {
                        if (string.IsNullOrEmpty(sourcePath) || checkedPaths.Contains(sourcePath))
                        {
                            continue;
                        }

                        checkedPaths.Add(sourcePath);
                        var libName = options.IncludeLibraryNames ? mirror.SourceLibraryName : "Source Library";
                        results.Add(GetPathDiagnostics(sourcePath, $"Source ({libName})", options));
                    }
                }
            }
        }

        // Check all mirror target paths
        foreach (var alt in config.LanguageAlternatives)
        {
            foreach (var mirror in alt.MirroredLibraries)
            {
                if (string.IsNullOrEmpty(mirror.TargetPath) || checkedPaths.Contains(mirror.TargetPath))
                {
                    continue;
                }

                checkedPaths.Add(mirror.TargetPath);
                var libName = options.IncludeLibraryNames ? mirror.SourceLibraryName : "Mirror";
                results.Add(GetPathDiagnostics(mirror.TargetPath, $"Target ({libName})", options));
            }

            // Also check the base destination path
            if (!string.IsNullOrEmpty(alt.DestinationBasePath) && !checkedPaths.Contains(alt.DestinationBasePath))
            {
                checkedPaths.Add(alt.DestinationBasePath);
                results.Add(GetPathDiagnostics(alt.DestinationBasePath, "Destination Base", options));
            }
        }

        return results;
    }

    private static FilesystemDiagnostics GetPathDiagnostics(string path, string pathType, DebugReportOptions options)
    {
        var diag = new FilesystemDiagnostics
        {
            Path = options.IncludeFilePaths ? path : $"[{pathType.ToLowerInvariant().Replace(" ", "_")}]",
            PathType = pathType,
            Exists = Directory.Exists(path)
        };

        if (!diag.Exists)
        {
            return diag;
        }

        try
        {
            // Get drive info for disk space
            var driveInfo = new DriveInfo(Path.GetPathRoot(path) ?? path);
            if (driveInfo.IsReady)
            {
                diag.TotalSpaceBytes = driveInfo.TotalSize;
                diag.AvailableSpaceBytes = driveInfo.AvailableFreeSpace;
                diag.TotalSpace = FormatBytes(driveInfo.TotalSize);
                diag.AvailableSpace = FormatBytes(driveInfo.AvailableFreeSpace);
                diag.FilesystemType = driveInfo.DriveFormat;

                // Check if hardlinks are supported (NTFS, ext4, etc. support them; FAT32 doesn't)
                var fsType = driveInfo.DriveFormat.ToUpperInvariant();
                diag.HardlinksSupported = fsType switch
                {
                    "NTFS" => true,
                    "EXT4" => true,
                    "EXT3" => true,
                    "EXT2" => true,
                    "XFS" => true,
                    "BTRFS" => true,
                    "ZFS" => true,
                    "APFS" => true,
                    "HFS+" => true,
                    "FAT32" => false,
                    "FAT" => false,
                    "EXFAT" => false,
                    _ => null // Unknown
                };
            }
        }
        catch
        {
            // Ignore errors getting drive info
        }

        return diag;
    }

    private async Task<HardlinkVerification?> VerifyHardlinksAsync(DebugReportOptions options, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return null;
        }

        var verification = new HardlinkVerification();
        var samples = new List<HardlinkSample>();

        // Find some files in mirror directories to verify
        foreach (var alt in config.LanguageAlternatives)
        {
            foreach (var mirror in alt.MirroredLibraries)
            {
                if (string.IsNullOrEmpty(mirror.TargetPath) || !Directory.Exists(mirror.TargetPath))
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    // Get up to 3 sample files from this mirror
                    var files = Directory.EnumerateFiles(mirror.TargetPath, "*", SearchOption.AllDirectories)
                        .Where(f => !f.EndsWith(".nfo", StringComparison.OrdinalIgnoreCase) &&
                                    !f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) &&
                                    !f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                        .Take(3);

                    foreach (var file in files)
                    {
                        var sample = VerifyHardlink(file, options);
                        samples.Add(sample);

                        if (samples.Count >= 10) // Max 10 samples total
                        {
                            break;
                        }
                    }
                }
                catch
                {
                    // Ignore errors enumerating files
                }

                if (samples.Count >= 10)
                {
                    break;
                }
            }

            if (samples.Count >= 10)
            {
                break;
            }
        }

        verification.Samples = samples;
        verification.SamplesChecked = samples.Count;
        verification.ValidHardlinks = samples.Count(s => s.IsValid);
        verification.BrokenHardlinks = samples.Count(s => !s.IsValid && s.Error == null);

        if (samples.Count == 0)
        {
            verification.Success = true;
            verification.Message = "No mirror files found to verify";
        }
        else if (verification.ValidHardlinks == samples.Count)
        {
            verification.Success = true;
            verification.Message = $"All {samples.Count} sampled files are valid hardlinks";
        }
        else if (verification.ValidHardlinks > 0)
        {
            verification.Success = false;
            verification.Message = $"{verification.ValidHardlinks}/{samples.Count} files are valid hardlinks, {verification.BrokenHardlinks} appear to be copies";
        }
        else
        {
            verification.Success = false;
            verification.Message = "No valid hardlinks found - files may be copies instead of hardlinks";
        }

        return await Task.FromResult(verification).ConfigureAwait(false);
    }

    private static HardlinkSample VerifyHardlink(string filePath, DebugReportOptions options)
    {
        var sample = new HardlinkSample
        {
            FilePath = options.IncludeFilePaths ? filePath : $"[file: {Path.GetExtension(filePath)}]"
        };

        try
        {
            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists)
            {
                sample.Error = "File not found";
                return sample;
            }

            // On Unix, we can check the link count
            // On Windows, we need to use platform-specific APIs
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                sample.LinkCount = GetWindowsHardlinkCount(filePath);
            }
            else
            {
                sample.LinkCount = GetUnixHardlinkCount(filePath);
            }

            sample.IsValid = sample.LinkCount > 1;
        }
        catch (Exception ex)
        {
            sample.Error = ex.Message;
        }

        return sample;
    }

    private static int GetUnixHardlinkCount(string filePath)
    {
        // On Unix/Linux/macOS, we can use the stat command to get link count
        // This is a simple approach that works across platforms without Mono.Unix
        try
        {
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "stat",
                    Arguments = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                        ? $"-f %l \"{filePath}\""  // macOS format
                        : $"-c %h \"{filePath}\"", // Linux format
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();

            if (int.TryParse(output, out var linkCount))
            {
                return linkCount;
            }
        }
        catch
        {
            // Ignore errors
        }

        return 1; // Assume single link if we can't check
    }

    private static int GetWindowsHardlinkCount(string filePath)
    {
        // On Windows, getting accurate link count requires P/Invoke to GetFileInformationByHandle
        // For simplicity, we'll use fsutil which requires admin rights, or just return unknown
        try
        {
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "fsutil",
                    Arguments = $"hardlink list \"{filePath}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            // Count the lines (each line is a hardlink path)
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return lines.Length > 0 ? lines.Length : 1;
        }
        catch
        {
            // fsutil requires admin rights, fallback to assuming it's a hardlink if file exists
            return new FileInfo(filePath).Exists ? 2 : 0;
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int suffixIndex = 0;
        double size = bytes;

        while (size >= 1024 && suffixIndex < suffixes.Length - 1)
        {
            size /= 1024;
            suffixIndex++;
        }

        return $"{size:F1} {suffixes[suffixIndex]}";
    }

    private static string FormatTimeAgo(DateTime time)
    {
        var span = DateTime.UtcNow - time;

        if (span.TotalMinutes < 1)
        {
            return "Just now";
        }

        if (span.TotalHours < 1)
        {
            return $"{(int)span.TotalMinutes}m ago";
        }

        if (span.TotalDays < 1)
        {
            return $"{(int)span.TotalHours}h ago";
        }

        return $"{(int)span.TotalDays}d ago";
    }

    private static string? SanitizeErrorMessage(string? error)
    {
        if (string.IsNullOrEmpty(error))
        {
            return null;
        }

        // Remove file paths
        error = PathPattern().Replace(error, "[path]");

        // Truncate long messages
        if (error.Length > 200)
        {
            error = error.Substring(0, 197) + "...";
        }

        return error;
    }

    private static string SanitizeLogMessage(string message)
    {
        // Remove file paths
        message = PathPattern().Replace(message, "[path]");

        // Remove potential usernames in common patterns
        message = UsernamePattern().Replace(message, "$1[user]$2");

        // Remove GUIDs that might be user IDs (but keep for context)
        // We'll leave GUIDs as they're not PII on their own

        return message;
    }

    [GeneratedRegex(@"[A-Za-z]:\\[^\s""'<>|]+|/(?:home|Users|media|mnt|data|var)[^\s""'<>|]+", RegexOptions.IgnoreCase)]
    private static partial Regex PathPattern();

    [GeneratedRegex(@"(user[_\s]*[:=]?\s*)[^\s,;]+(\s|,|;|$)", RegexOptions.IgnoreCase)]
    private static partial Regex UsernamePattern();

    private static string FormatAsMarkdown(DebugReport report)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Polyglot Debug Report");
        sb.AppendLine($"Generated: {report.GeneratedAt:yyyy-MM-ddTHH:mm:ssZ}");
        sb.AppendLine();

        // Environment
        sb.AppendLine("## Environment");
        sb.AppendLine($"- **Plugin Version:** {report.Environment.PluginVersion}");
        sb.AppendLine($"- **Jellyfin Version:** {report.Environment.JellyfinVersion}");
        sb.AppendLine($"- **OS:** {report.Environment.OperatingSystem}");
        sb.AppendLine($"- **.NET:** {report.Environment.DotNetVersion}");
        sb.AppendLine($"- **Architecture:** {report.Environment.Architecture}");
        sb.AppendLine();

        // Configuration
        sb.AppendLine("## Configuration Summary");
        sb.AppendLine($"- Language Alternatives: {report.Configuration.LanguageAlternativeCount}");
        sb.AppendLine($"- Total Mirrors: {report.Configuration.TotalMirrorCount}");
        sb.AppendLine($"- Managed Users: {report.Configuration.ManagedUserCount}");
        sb.AppendLine($"- Auto-manage new users: {(report.Configuration.AutoManageNewUsers ? "Yes" : "No")}");
        sb.AppendLine($"- Sync after library scan: {(report.Configuration.SyncAfterLibraryScan ? "Yes" : "No")}");
        sb.AppendLine($"- LDAP Integration: {(report.Configuration.LdapIntegrationEnabled ? "Enabled" : "Disabled")}");
        sb.AppendLine($"- Excluded extensions: {report.Configuration.ExcludedExtensionCount}");
        sb.AppendLine($"- Excluded directories: {report.Configuration.ExcludedDirectoryCount}");
        sb.AppendLine();

        // Mirror Health
        if (report.MirrorHealth.Count > 0)
        {
            sb.AppendLine("## Mirror Health");

            // Include path columns if paths are included
            if (report.Options.IncludeFilePaths)
            {
                sb.AppendLine("| Alternative | Source | Source Path | Target Path | Status | Last Sync | Files | SrcLib? | SrcPath? | TgtLib? | TgtPath? | Writable? | Error |");
                sb.AppendLine("|-------------|--------|-------------|-------------|--------|-----------|-------|---------|----------|---------|----------|-----------|-------|");
            }
            else
            {
                sb.AppendLine("| Alternative | Source | Status | Last Sync | Files | SrcLib? | SrcPath? | TgtLib? | TgtPath? | Writable? | Error |");
                sb.AppendLine("|-------------|--------|--------|-----------|-------|---------|----------|---------|----------|-----------|-------|");
            }

            foreach (var mirror in report.MirrorHealth)
            {
                var statusIcon = mirror.Status switch
                {
                    "Synced" => "✓",
                    "Error" => "✗",
                    "Syncing" => "↻",
                    _ => "○"
                };

                var writable = mirror.TargetPathWritable switch
                {
                    true => "✓",
                    false => "✗",
                    null => "-"
                };

                var srcPathExists = mirror.SourcePathExists switch
                {
                    true => "✓",
                    false => "✗",
                    null => "-"
                };

                if (report.Options.IncludeFilePaths)
                {
                    sb.AppendLine($"| {mirror.AlternativeName} | {mirror.SourceLibrary} | {mirror.SourcePath ?? "-"} | {mirror.TargetPath ?? "-"} | {statusIcon} {mirror.Status} | {mirror.LastSync} | {mirror.FileCount?.ToString() ?? "-"} | {(mirror.SourceExists ? "✓" : "✗")} | {srcPathExists} | {(mirror.TargetExists ? "✓" : "✗")} | {(mirror.TargetPathExists ? "✓" : "✗")} | {writable} | {mirror.LastError ?? "-"} |");
                }
                else
                {
                    sb.AppendLine($"| {mirror.AlternativeName} | {mirror.SourceLibrary} | {statusIcon} {mirror.Status} | {mirror.LastSync} | {mirror.FileCount?.ToString() ?? "-"} | {(mirror.SourceExists ? "✓" : "✗")} | {srcPathExists} | {(mirror.TargetExists ? "✓" : "✗")} | {(mirror.TargetPathExists ? "✓" : "✗")} | {writable} | {mirror.LastError ?? "-"} |");
                }
            }

            sb.AppendLine();
        }

        // Filesystem Diagnostics
        if (report.FilesystemInfo.Count > 0)
        {
            sb.AppendLine("## Filesystem Diagnostics");
            sb.AppendLine("| Path | Type | Exists | Filesystem | Total | Available | Hardlinks? |");
            sb.AppendLine("|------|------|--------|------------|-------|-----------|------------|");

            foreach (var fs in report.FilesystemInfo)
            {
                var hardlinks = fs.HardlinksSupported switch
                {
                    true => "✓ Yes",
                    false => "✗ No",
                    null => "Unknown"
                };

                sb.AppendLine($"| {fs.Path} | {fs.PathType} | {(fs.Exists ? "✓" : "✗")} | {fs.FilesystemType ?? "-"} | {fs.TotalSpace ?? "-"} | {fs.AvailableSpace ?? "-"} | {hardlinks} |");
            }

            sb.AppendLine();
        }

        // Hardlink Verification
        if (report.HardlinkVerification != null)
        {
            sb.AppendLine("## Hardlink Verification");
            var hl = report.HardlinkVerification;
            sb.AppendLine($"- **Status:** {(hl.Success ? "✓ OK" : "✗ Issues Found")}");
            sb.AppendLine($"- **Message:** {hl.Message}");
            sb.AppendLine($"- **Samples Checked:** {hl.SamplesChecked}");
            sb.AppendLine($"- **Valid Hardlinks:** {hl.ValidHardlinks}");
            sb.AppendLine($"- **Broken/Copies:** {hl.BrokenHardlinks}");

            if (hl.Samples.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("<details>");
                sb.AppendLine("<summary>Sample Details</summary>");
                sb.AppendLine();
                sb.AppendLine("| File | Valid | Link Count | Error |");
                sb.AppendLine("|------|-------|------------|-------|");

                foreach (var sample in hl.Samples)
                {
                    sb.AppendLine($"| {sample.FilePath} | {(sample.IsValid ? "✓" : "✗")} | {sample.LinkCount} | {sample.Error ?? "-"} |");
                }

                sb.AppendLine();
                sb.AppendLine("</details>");
            }

            sb.AppendLine();
        }

        // User Distribution
        if (report.UserDistribution.Count > 0)
        {
            sb.AppendLine("## User Distribution");
            foreach (var dist in report.UserDistribution)
            {
                sb.AppendLine($"- {dist.Language}: {dist.UserCount} users");
            }

            sb.AppendLine();
        }

        // User Details (if requested)
        if (report.UserDetails != null && report.UserDetails.Count > 0)
        {
            sb.AppendLine("## User Details");
            sb.AppendLine("| User ID | Language | Managed | Assignment |");
            sb.AppendLine("|---------|----------|---------|------------|");

            foreach (var user in report.UserDetails)
            {
                sb.AppendLine($"| {user.UserName} | {user.AssignedLanguage} | {(user.IsManaged ? "✓" : "✗")} | {user.AssignmentSource} |");
            }

            sb.AppendLine();
        }

        // Libraries
        if (report.Libraries.Count > 0)
        {
            sb.AppendLine("## Libraries");
            sb.AppendLine("| Name | Type | Is Mirror | Metadata Lang |");
            sb.AppendLine("|------|------|-----------|---------------|");

            foreach (var lib in report.Libraries)
            {
                sb.AppendLine($"| {lib.Name} | {lib.Type} | {(lib.IsMirror ? "Yes" : "No")} | {lib.MetadataLanguage} |");
            }

            sb.AppendLine();
        }

        // Other Plugins
        if (report.OtherPlugins.Count > 0)
        {
            sb.AppendLine("## Other Installed Plugins");
            foreach (var plugin in report.OtherPlugins)
            {
                sb.AppendLine($"- {plugin.Name}: {plugin.Version}");
            }

            sb.AppendLine();
        }

        // Recent Logs
        if (report.RecentLogs.Count > 0)
        {
            sb.AppendLine("<details>");
            sb.AppendLine("<summary>Recent Logs (click to expand)</summary>");
            sb.AppendLine();
            sb.AppendLine("```");

            foreach (var log in report.RecentLogs.OrderBy(l => l.Timestamp))
            {
                var levelShort = log.Level switch
                {
                    "Information" => "INF",
                    "Warning" => "WRN",
                    "Error" => "ERR",
                    "Debug" => "DBG",
                    "Critical" => "CRT",
                    _ => log.Level.Substring(0, Math.Min(3, log.Level.Length)).ToUpperInvariant()
                };

                sb.AppendLine($"[{log.Timestamp:HH:mm:ss} {levelShort}] {log.Message}");

                if (!string.IsNullOrEmpty(log.Exception))
                {
                    sb.AppendLine($"    Exception: {log.Exception}");
                }
            }

            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("</details>");
        }

        return sb.ToString();
    }
}

