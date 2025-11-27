using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Polyglot.Models;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Polyglot.Configuration;

/// <summary>
/// Plugin configuration for the Polyglot plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Default file extensions to exclude from hardlinking (metadata and images).
    /// </summary>
    public static readonly string[] DefaultExcludedExtensions = { ".nfo", ".jpg", ".jpeg", ".png", ".gif", ".webp", ".tbn", ".bmp" };

    /// <summary>
    /// Default directory names to exclude from mirroring.
    /// </summary>
    public static readonly string[] DefaultExcludedDirectories = { "extrafanart", "extrathumbs", ".trickplay", "metadata", ".actors" };

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        LanguageAlternatives = new List<LanguageAlternative>();
        UserLanguages = new List<UserLanguageConfig>();
        LdapGroupMappings = new List<LdapGroupMapping>();
        ExcludedExtensions = new List<string>(DefaultExcludedExtensions);
        ExcludedDirectories = new List<string>(DefaultExcludedDirectories);
    }

    /// <summary>
    /// Gets or sets a value indicating whether to sync Jellyfin UI language when plugin language changes.
    /// </summary>
    public bool SyncUserDisplayLanguage { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to sync preferred subtitle language.
    /// </summary>
    public bool SyncUserSubtitleLanguage { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to sync preferred audio language.
    /// </summary>
    public bool SyncUserAudioLanguage { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether LDAP integration is enabled.
    /// </summary>
    public bool EnableLdapIntegration { get; set; }

    /// <summary>
    /// Gets or sets the list of configured language alternatives.
    /// </summary>
    public List<LanguageAlternative> LanguageAlternatives { get; set; }

    /// <summary>
    /// Gets or sets the per-user language assignments.
    /// </summary>
    public List<UserLanguageConfig> UserLanguages { get; set; }

    /// <summary>
    /// Gets or sets the LDAP group to language mappings.
    /// </summary>
    public List<LdapGroupMapping> LdapGroupMappings { get; set; }

    /// <summary>
    /// Gets or sets the interval in hours between scheduled mirror syncs.
    /// Note: Mirrors are also synced automatically after library scans via ILibraryPostScanTask.
    /// </summary>
    public int MirrorSyncIntervalHours { get; set; } = 6;

    /// <summary>
    /// Gets or sets the time for daily user reconciliation task (in 24-hour format, e.g., "03:00").
    /// </summary>
    public string UserReconciliationTime { get; set; } = "03:00";

    /// <summary>
    /// Gets or sets the file extensions to exclude from hardlinking (metadata and images).
    /// Extensions should include the leading dot (e.g., ".nfo", ".jpg").
    /// </summary>
    public List<string> ExcludedExtensions { get; set; }

    /// <summary>
    /// Gets or sets the directory names to exclude from mirroring.
    /// These are directory names (not full paths) that will be skipped during mirroring.
    /// </summary>
    public List<string> ExcludedDirectories { get; set; }
}

