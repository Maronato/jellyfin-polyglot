using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Polyglot.Models;

namespace Jellyfin.Plugin.Polyglot.Services;

/// <summary>
/// Service for generating debug reports for troubleshooting.
/// </summary>
public interface IDebugReportService
{
    /// <summary>
    /// Generates a comprehensive debug report.
    /// </summary>
    /// <param name="options">Options controlling what information to include.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The debug report.</returns>
    Task<DebugReport> GenerateReportAsync(DebugReportOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates the debug report as formatted Markdown for GitHub issues.
    /// </summary>
    /// <param name="options">Options controlling what information to include.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Markdown-formatted report string.</returns>
    Task<string> GenerateMarkdownReportAsync(DebugReportOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Logs a message to the circular buffer.
    /// </summary>
    /// <param name="level">Log level.</param>
    /// <param name="message">Log message.</param>
    /// <param name="exception">Optional exception.</param>
    void LogToBuffer(string level, string message, string? exception = null);
}

/// <summary>
/// Options for controlling debug report generation.
/// </summary>
public class DebugReportOptions
{
    /// <summary>
    /// Gets or sets whether to include actual file paths (not anonymized).
    /// Default: false (paths are anonymized).
    /// </summary>
    public bool IncludeFilePaths { get; set; }

    /// <summary>
    /// Gets or sets whether to include actual library names.
    /// Default: false (libraries are anonymized as Library_1, etc.).
    /// </summary>
    public bool IncludeLibraryNames { get; set; }

    /// <summary>
    /// Gets or sets whether to include actual user names.
    /// Default: false (users are anonymized).
    /// </summary>
    public bool IncludeUserNames { get; set; }

    /// <summary>
    /// Gets or sets whether to include filesystem diagnostics (disk space, filesystem type).
    /// Default: true.
    /// </summary>
    public bool IncludeFilesystemDiagnostics { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to include hardlink verification tests.
    /// Default: true.
    /// </summary>
    public bool IncludeHardlinkVerification { get; set; } = true;
}

/// <summary>
/// Debug report data structure.
/// </summary>
public class DebugReport
{
    /// <summary>
    /// Gets or sets the timestamp when the report was generated.
    /// </summary>
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets the options used to generate this report.
    /// </summary>
    public DebugReportOptions Options { get; set; } = new();

    /// <summary>
    /// Gets or sets the environment information.
    /// </summary>
    public EnvironmentInfo Environment { get; set; } = new();

    /// <summary>
    /// Gets or sets the configuration summary.
    /// </summary>
    public ConfigurationSummary Configuration { get; set; } = new();

    /// <summary>
    /// Gets or sets the mirror health information.
    /// </summary>
    public List<MirrorHealthInfo> MirrorHealth { get; set; } = new();

    /// <summary>
    /// Gets or sets the filesystem diagnostics.
    /// </summary>
    public List<FilesystemDiagnostics> FilesystemInfo { get; set; } = new();

    /// <summary>
    /// Gets or sets the hardlink verification results.
    /// </summary>
    public HardlinkVerification? HardlinkVerification { get; set; }

    /// <summary>
    /// Gets or sets the user distribution.
    /// </summary>
    public List<UserDistributionInfo> UserDistribution { get; set; } = new();

    /// <summary>
    /// Gets or sets the user details (only if IncludeUserNames is true).
    /// </summary>
    public List<UserDetailInfo>? UserDetails { get; set; }

    /// <summary>
    /// Gets or sets the library information.
    /// </summary>
    public List<LibrarySummaryInfo> Libraries { get; set; } = new();

    /// <summary>
    /// Gets or sets the other installed plugins.
    /// </summary>
    public List<PluginSummaryInfo> OtherPlugins { get; set; } = new();

    /// <summary>
    /// Gets or sets the recent log entries.
    /// </summary>
    public List<LogEntryInfo> RecentLogs { get; set; } = new();
}

/// <summary>
/// Environment information.
/// </summary>
public class EnvironmentInfo
{
    /// <summary>
    /// Gets or sets the plugin version.
    /// </summary>
    public string PluginVersion { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Jellyfin version.
    /// </summary>
    public string JellyfinVersion { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the operating system.
    /// </summary>
    public string OperatingSystem { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the .NET runtime version.
    /// </summary>
    public string DotNetVersion { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the processor architecture.
    /// </summary>
    public string Architecture { get; set; } = string.Empty;
}

/// <summary>
/// Configuration summary.
/// </summary>
public class ConfigurationSummary
{
    /// <summary>
    /// Gets or sets the number of language alternatives.
    /// </summary>
    public int LanguageAlternativeCount { get; set; }

    /// <summary>
    /// Gets or sets the total number of mirrors.
    /// </summary>
    public int TotalMirrorCount { get; set; }

    /// <summary>
    /// Gets or sets the number of managed users.
    /// </summary>
    public int ManagedUserCount { get; set; }

    /// <summary>
    /// Gets or sets whether auto-manage new users is enabled.
    /// </summary>
    public bool AutoManageNewUsers { get; set; }

    /// <summary>
    /// Gets or sets whether sync after library scan is enabled.
    /// </summary>
    public bool SyncAfterLibraryScan { get; set; }

    /// <summary>
    /// Gets or sets the number of excluded extensions.
    /// </summary>
    public int ExcludedExtensionCount { get; set; }

    /// <summary>
    /// Gets or sets the number of excluded directories.
    /// </summary>
    public int ExcludedDirectoryCount { get; set; }
}

/// <summary>
/// Mirror health information.
/// </summary>
public class MirrorHealthInfo
{
    /// <summary>
    /// Gets or sets the alternative name (may be anonymized based on options).
    /// </summary>
    public string AlternativeName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source library name (may be anonymized based on options).
    /// </summary>
    public string SourceLibrary { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source path(s) (may be anonymized based on options).
    /// </summary>
    public string? SourcePath { get; set; }

    /// <summary>
    /// Gets or sets whether the source path exists.
    /// </summary>
    public bool? SourcePathExists { get; set; }

    /// <summary>
    /// Gets or sets the target path (may be anonymized based on options).
    /// </summary>
    public string? TargetPath { get; set; }

    /// <summary>
    /// Gets or sets the mirror status.
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the last sync time.
    /// </summary>
    public string LastSync { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file count from last sync.
    /// </summary>
    public int? FileCount { get; set; }

    /// <summary>
    /// Gets or sets whether the source library exists.
    /// </summary>
    public bool SourceExists { get; set; }

    /// <summary>
    /// Gets or sets whether the target library exists.
    /// </summary>
    public bool TargetExists { get; set; }

    /// <summary>
    /// Gets or sets whether the target path exists.
    /// </summary>
    public bool TargetPathExists { get; set; }

    /// <summary>
    /// Gets or sets whether the target path is writable.
    /// </summary>
    public bool? TargetPathWritable { get; set; }

    /// <summary>
    /// Gets or sets the last error (if any).
    /// </summary>
    public string? LastError { get; set; }
}

/// <summary>
/// Filesystem diagnostics for a path.
/// </summary>
public class FilesystemDiagnostics
{
    /// <summary>
    /// Gets or sets the path (may be anonymized).
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the path type (source/target).
    /// </summary>
    public string PathType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether the path exists.
    /// </summary>
    public bool Exists { get; set; }

    /// <summary>
    /// Gets or sets the filesystem type (ext4, ntfs, etc.) if detectable.
    /// </summary>
    public string? FilesystemType { get; set; }

    /// <summary>
    /// Gets or sets the total disk space in bytes.
    /// </summary>
    public long? TotalSpaceBytes { get; set; }

    /// <summary>
    /// Gets or sets the available disk space in bytes.
    /// </summary>
    public long? AvailableSpaceBytes { get; set; }

    /// <summary>
    /// Gets or sets human-readable total space.
    /// </summary>
    public string? TotalSpace { get; set; }

    /// <summary>
    /// Gets or sets human-readable available space.
    /// </summary>
    public string? AvailableSpace { get; set; }

    /// <summary>
    /// Gets or sets whether hardlinks are supported on this filesystem.
    /// </summary>
    public bool? HardlinksSupported { get; set; }
}

/// <summary>
/// Hardlink verification results.
/// </summary>
public class HardlinkVerification
{
    /// <summary>
    /// Gets or sets whether hardlinks appear to be working.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the verification message.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of sample files checked.
    /// </summary>
    public int SamplesChecked { get; set; }

    /// <summary>
    /// Gets or sets the number of valid hardlinks found.
    /// </summary>
    public int ValidHardlinks { get; set; }

    /// <summary>
    /// Gets or sets the number of broken/invalid hardlinks.
    /// </summary>
    public int BrokenHardlinks { get; set; }

    /// <summary>
    /// Gets or sets detailed sample results.
    /// </summary>
    public List<HardlinkSample> Samples { get; set; } = new();
}

/// <summary>
/// Individual hardlink sample check.
/// </summary>
public class HardlinkSample
{
    /// <summary>
    /// Gets or sets the file path (may be anonymized).
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether the file is a valid hardlink.
    /// </summary>
    public bool IsValid { get; set; }

    /// <summary>
    /// Gets or sets the link count (should be > 1 for hardlinks).
    /// </summary>
    public int LinkCount { get; set; }

    /// <summary>
    /// Gets or sets any error message.
    /// </summary>
    public string? Error { get; set; }
}

/// <summary>
/// Detailed user information (only when IncludeUserNames is true).
/// </summary>
public class UserDetailInfo
{
    /// <summary>
    /// Gets or sets the user name.
    /// </summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the assigned language.
    /// </summary>
    public string AssignedLanguage { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether the user is managed by the plugin.
    /// </summary>
    public bool IsManaged { get; set; }

    /// <summary>
    /// Gets or sets how the assignment was made.
    /// </summary>
    public string AssignmentSource { get; set; } = string.Empty;
}

/// <summary>
/// User distribution by language.
/// </summary>
public class UserDistributionInfo
{
    /// <summary>
    /// Gets or sets the language name.
    /// </summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the user count.
    /// </summary>
    public int UserCount { get; set; }
}

/// <summary>
/// Library summary information.
/// </summary>
public class LibrarySummaryInfo
{
    /// <summary>
    /// Gets or sets the library name (anonymized).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the collection type.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether this is a mirror library.
    /// </summary>
    public bool IsMirror { get; set; }

    /// <summary>
    /// Gets or sets the metadata language.
    /// </summary>
    public string MetadataLanguage { get; set; } = string.Empty;
}

/// <summary>
/// Plugin summary information.
/// </summary>
public class PluginSummaryInfo
{
    /// <summary>
    /// Gets or sets the plugin name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the plugin version.
    /// </summary>
    public string Version { get; set; } = string.Empty;
}

/// <summary>
/// Log entry information with support for privacy-aware entity rendering.
/// </summary>
public class LogEntryInfo
{
    /// <summary>
    /// Gets or sets the timestamp.
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the log level.
    /// </summary>
    public string Level { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the message template with placeholders like {0}, {1}.
    /// </summary>
    public string MessageTemplate { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the pre-rendered message for stdout (full details).
    /// This is set at log time and includes all entity details.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the entity references for privacy-aware rendering.
    /// Each entity can be rendered differently based on privacy settings.
    /// </summary>
    public List<ILogEntity> Entities { get; set; } = new();

    /// <summary>
    /// Gets or sets the exception message (if any).
    /// </summary>
    public string? Exception { get; set; }

    /// <summary>
    /// Creates a shallow copy of the log entry.
    /// Entities list is shared but immutable so shallow copy is safe for rendering.
    /// </summary>
    /// <returns>A new LogEntryInfo instance.</returns>
    public LogEntryInfo Clone()
    {
        return new LogEntryInfo
        {
            Timestamp = Timestamp,
            Level = Level,
            MessageTemplate = MessageTemplate,
            Message = Message,
            Entities = Entities, // List reference copy is fine as we don't modify the list itself
            Exception = Exception
        };
    }

    /// <summary>
    /// Renders the message with privacy-aware entity formatting.
    /// </summary>
    /// <param name="options">The debug report options controlling privacy.</param>
    /// <param name="entityCounters">Counters for tracking entity indices across logs.</param>
    /// <param name="pathSanitizer">Optional function to sanitize paths in messages without entities.</param>
    /// <returns>The rendered message.</returns>
    public string RenderMessage(DebugReportOptions options, EntityPrivacyCounters entityCounters, Func<string, string>? pathSanitizer = null)
    {
        if (Entities.Count == 0)
        {
            // No entities - apply path sanitization to the raw message if paths should be hidden
            // This handles legacy logs created without entity references
            if (!options.IncludeFilePaths && pathSanitizer != null)
            {
                return pathSanitizer(Message);
            }

            return Message;
        }

        var args = new object[Entities.Count];
        for (int i = 0; i < Entities.Count; i++)
        {
            var entity = Entities[i];
            args[i] = RenderEntity(entity, options, entityCounters);
        }

        try
        {
            return string.Format(CultureInfo.InvariantCulture, MessageTemplate, args);
        }
        catch
        {
            return Message; // Fallback to pre-rendered if format fails
        }
    }

    /// <summary>
    /// Renders a single entity based on privacy settings.
    /// </summary>
    private static string RenderEntity(ILogEntity entity, DebugReportOptions options, EntityPrivacyCounters counters)
    {
        if (entity.EntityType == LogEntityType.Value)
        {
            return entity.RenderFull();
        }

        var showFull = entity.EntityType switch
        {
            LogEntityType.User => options.IncludeUserNames,
            LogEntityType.Library => options.IncludeLibraryNames,
            LogEntityType.Alternative => options.IncludeLibraryNames,
            LogEntityType.Mirror => options.IncludeLibraryNames,
            LogEntityType.Path => options.IncludeFilePaths,
            _ => false
        };

        if (showFull)
        {
            return entity.RenderFull();
        }

        var index = counters.GetOrCreateIndex(entity);
        return entity.RenderPrivate(index);
    }
}

/// <summary>
/// Tracks entity indices for consistent privacy anonymization across logs.
/// Each unique entity gets a consistent index (e.g., User_1 is always the same user).
/// </summary>
public class EntityPrivacyCounters
{
    // Separate counters for case-sensitive (paths) and case-insensitive (other) entity types
    private readonly Dictionary<LogEntityType, Dictionary<string, int>> _caseSensitiveCounters = new();
    private readonly Dictionary<LogEntityType, Dictionary<string, int>> _caseInsensitiveCounters = new();
    private readonly Dictionary<LogEntityType, int> _nextIndex = new();

    /// <summary>
    /// Gets or creates a consistent index for an entity.
    /// </summary>
    /// <param name="entity">The entity to get an index for.</param>
    /// <returns>The consistent index for this entity.</returns>
    public int GetOrCreateIndex(ILogEntity entity)
    {
        var type = entity.EntityType;
        var key = GetEntityKey(entity);

        // Use case-sensitive comparison for paths (filesystem paths on Unix/Linux are case-sensitive)
        // Use case-insensitive for other entities (usernames, library names are typically case-insensitive)
        var useCaseSensitive = type == LogEntityType.Path;
        var countersDict = useCaseSensitive ? _caseSensitiveCounters : _caseInsensitiveCounters;

        if (!countersDict.TryGetValue(type, out var typeCounters))
        {
            var comparer = useCaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
            typeCounters = new Dictionary<string, int>(comparer);
            countersDict[type] = typeCounters;

            if (!_nextIndex.ContainsKey(type))
            {
                _nextIndex[type] = 1;
            }
        }

        if (!typeCounters.TryGetValue(key, out var index))
        {
            index = _nextIndex[type]++;
            typeCounters[key] = index;
        }

        return index;
    }

    /// <summary>
    /// Gets a unique key for an entity for tracking purposes.
    /// </summary>
    private static string GetEntityKey(ILogEntity entity)
    {
        return entity switch
        {
            LogUser u => u.Id.ToString(),
            LogLibrary l => l.Id.ToString(),
            LogAlternative a => a.Id.ToString(),
            LogMirror m => m.Id.ToString(),
            LogPath p => p.FullPath,
            _ => entity.RenderFull()
        };
    }
}

