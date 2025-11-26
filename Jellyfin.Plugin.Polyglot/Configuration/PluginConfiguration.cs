using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Polyglot.Models;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Polyglot.Configuration;

/// <summary>
/// Plugin configuration for Multi-Language Library.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        LanguageAlternatives = new List<LanguageAlternative>();
        UserLanguages = new List<UserLanguageConfig>();
        LdapGroupMappings = new List<LdapGroupMapping>();
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
}

