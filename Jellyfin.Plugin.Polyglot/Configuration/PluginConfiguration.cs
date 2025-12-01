using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Polyglot.Helpers;
using Jellyfin.Plugin.Polyglot.Models;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Polyglot.Configuration;

/// <summary>
/// Plugin configuration for the Polyglot plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    private HashSet<string> _excludedExtensions = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _excludedDirectories = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _includedDirectories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        LanguageAlternatives = new List<LanguageAlternative>();
        UserLanguages = new List<UserLanguageConfig>();
        ExcludedExtensions = FileClassifier.DefaultExcludedExtensions;
        ExcludedDirectories = FileClassifier.DefaultExcludedDirectories;
        IncludedDirectories = FileClassifier.DefaultIncludedDirectories;
    }

    /// <summary>
    /// Gets or sets a value indicating whether new users should be automatically managed by the plugin.
    /// When enabled, newly created users will be added to plugin management with the default language.
    /// </summary>
    public bool AutoManageNewUsers { get; set; }

    /// <summary>
    /// Gets or sets the default language alternative ID for new users.
    /// Null means users get "Default libraries" (access to source libraries only).
    /// </summary>
    public Guid? DefaultLanguageAlternativeId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether mirrors should be synced automatically after library scans.
    /// When disabled, mirrors will only sync via the scheduled task or manual trigger.
    /// Default is true to maintain backward compatibility.
    /// </summary>
    public bool SyncMirrorsAfterLibraryScan { get; set; } = true;

    /// <summary>
    /// Gets or sets the list of configured language alternatives.
    /// </summary>
    public List<LanguageAlternative> LanguageAlternatives { get; set; }

    /// <summary>
    /// Gets or sets the per-user language assignments.
    /// </summary>
    public List<UserLanguageConfig> UserLanguages { get; set; }

    /// <summary>
    /// Gets or sets the file extensions to exclude from hardlinking (metadata and images).
    /// Extensions should include the leading dot (e.g., ".nfo", ".jpg").
    /// Values are normalized to lowercase and deduplicated.
    /// </summary>
    public HashSet<string> ExcludedExtensions
    {
        get => _excludedExtensions;
        set => _excludedExtensions = NormalizeToLowercaseSet(value);
    }

    /// <summary>
    /// Gets or sets the directory names to exclude from mirroring.
    /// These are directory names (not full paths) that will be skipped during mirroring.
    /// Values are normalized to lowercase and deduplicated.
    /// </summary>
    public HashSet<string> ExcludedDirectories
    {
        get => _excludedDirectories;
        set => _excludedDirectories = NormalizeToLowercaseSet(value);
    }

    /// <summary>
    /// Gets or sets the directory names where all files should be hardlinked regardless of extension.
    /// These directories contain language-independent content (e.g., trickplay images, actor photos).
    /// Values are normalized to lowercase and deduplicated.
    /// </summary>
    public HashSet<string> IncludedDirectories
    {
        get => _includedDirectories;
        set => _includedDirectories = NormalizeToLowercaseSet(value);
    }

    /// <summary>
    /// Gets the default excluded file extensions (static, from FileClassifier).
    /// </summary>
    public static HashSet<string> DefaultExcludedExtensions => NormalizeToLowercaseSet(FileClassifier.DefaultExcludedExtensions);

    /// <summary>
    /// Gets the default excluded directory names (static, from FileClassifier).
    /// </summary>
    public static HashSet<string> DefaultExcludedDirectories => NormalizeToLowercaseSet(FileClassifier.DefaultExcludedDirectories);

    /// <summary>
    /// Gets the default included directory names (static, from FileClassifier).
    /// </summary>
    public static HashSet<string> DefaultIncludedDirectories => NormalizeToLowercaseSet(FileClassifier.DefaultIncludedDirectories);

    /// <summary>
    /// Normalizes a collection of strings to a lowercase HashSet.
    /// </summary>
    /// <param name="values">The values to normalize.</param>
    /// <returns>A HashSet with all values converted to lowercase.</returns>
    private static HashSet<string> NormalizeToLowercaseSet(IEnumerable<string>? values)
    {
        if (values == null)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return new HashSet<string>(
            values.Select(v => v.ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);
    }
}
