using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Polyglot.Configuration;
using Jellyfin.Plugin.Polyglot.Helpers;
using Jellyfin.Plugin.Polyglot.Models;
using Microsoft.Extensions.Logging;

// Aliases for log entity types to avoid conflict with model types
using LogAlternativeEntity = Jellyfin.Plugin.Polyglot.Models.LogAlternative;
using LogMirrorEntity = Jellyfin.Plugin.Polyglot.Models.LogMirror;
using LogUserEntity = Jellyfin.Plugin.Polyglot.Models.LogUser;

namespace Jellyfin.Plugin.Polyglot.Services;

/// <summary>
/// Service for atomic configuration updates.
/// All modifications perform: lock → fresh lookup → apply change → save → unlock.
/// This prevents race conditions where multiple threads read-modify-write the config.
/// </summary>
/// <remarks>
/// This service is registered as a singleton, so the instance lock is sufficient.
/// Using an instance lock instead of static lock allows proper isolation in test scenarios
/// and prevents issues if Jellyfin ever reloads plugins or creates multiple service contexts.
/// </remarks>
public class ConfigurationService : IConfigurationService
{
    private readonly ILogger<ConfigurationService> _logger;
    private readonly object _configLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationService"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public ConfigurationService(ILogger<ConfigurationService> logger)
    {
        _logger = logger;
    }

    #region Mirror Operations

    /// <inheritdoc />
    public LibraryMirror? GetMirror(Guid mirrorId)
    {
        lock (_configLock)
        {
            var config = Plugin.Instance?.Configuration;
            var mirror = config?.LanguageAlternatives
                .SelectMany(a => a.MirroredLibraries)
                .FirstOrDefault(m => m.Id == mirrorId);

            // Return a deep copy to prevent callers from modifying config state
            return mirror?.DeepClone();
        }
    }

    /// <inheritdoc />
    public (LibraryMirror Mirror, Guid AlternativeId)? GetMirrorWithAlternative(Guid mirrorId)
    {
        lock (_configLock)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return null;

            foreach (var alt in config.LanguageAlternatives)
            {
                var mirror = alt.MirroredLibraries.FirstOrDefault(m => m.Id == mirrorId);
                if (mirror != null)
                {
                    // Return a deep copy to prevent callers from modifying config state
                    return (mirror.DeepClone(), alt.Id);
                }
            }

            return null;
        }
    }

    /// <inheritdoc />
    public bool UpdateMirror(Guid mirrorId, Action<LibraryMirror> update)
    {
        lock (_configLock)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                _logger.PolyglotWarning("UpdateMirror: Plugin configuration is null");
                return false;
            }

            // Fresh lookup
            var mirror = config.LanguageAlternatives
                .SelectMany(a => a.MirroredLibraries)
                .FirstOrDefault(m => m.Id == mirrorId);

            if (mirror == null)
            {
                _logger.PolyglotDebug("UpdateMirror: Mirror {0} not found",
                    this.CreateLogMirror(mirrorId));
                return false;
            }

            var mirrorEntity = new LogMirrorEntity(mirrorId, mirror.SourceLibraryName, mirror.TargetLibraryName);
            _logger.PolyglotDebug("UpdateMirror: Applying update to mirror {0}", mirrorEntity);

            // Apply change
            update(mirror);

            // Save immediately
            Plugin.Instance?.SaveConfiguration();

            _logger.PolyglotDebug("UpdateMirror: Saved configuration after updating mirror {0}", mirrorEntity);
            return true;
        }
    }

    /// <inheritdoc />
    public bool AddMirror(Guid alternativeId, LibraryMirror mirror)
    {
        lock (_configLock)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                _logger.PolyglotWarning("AddMirror: Plugin configuration is null");
                return false;
            }

            // Fresh lookup
            var alternative = config.LanguageAlternatives.FirstOrDefault(a => a.Id == alternativeId);
            if (alternative == null)
            {
                _logger.PolyglotWarning("AddMirror: Alternative {0} not found",
                    this.CreateLogAlternative(alternativeId));
                return false;
            }

            var alternativeEntity = new LogAlternativeEntity(alternativeId, alternative.Name, alternative.LanguageCode);

            // Atomic duplicate check - prevents race conditions where two threads
            // could both pass an earlier check and both add mirrors for the same source
            if (alternative.MirroredLibraries.Any(m => m.SourceLibraryId == mirror.SourceLibraryId))
            {
                _logger.PolyglotWarning("AddMirror: Duplicate mirror for source library {0} already exists in alternative {1}",
                    new LogMirrorEntity(mirror.Id, mirror.SourceLibraryName, mirror.TargetLibraryName), alternativeEntity);
                return false;
            }

            var mirrorEntity = new LogMirrorEntity(mirror.Id, mirror.SourceLibraryName, mirror.TargetLibraryName);
            _logger.PolyglotDebug("AddMirror: Adding mirror {0} to alternative {1}",
                mirrorEntity, alternativeEntity);

            alternative.MirroredLibraries.Add(mirror);
            Plugin.Instance?.SaveConfiguration();

            _logger.PolyglotInfo("AddMirror: Added mirror {0} to alternative {1}", mirrorEntity, alternativeEntity);
            return true;
        }
    }

    /// <inheritdoc />
    public bool RemoveMirror(Guid mirrorId)
    {
        lock (_configLock)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                _logger.PolyglotWarning("RemoveMirror: Plugin configuration is null");
                return false;
            }

            foreach (var alternative in config.LanguageAlternatives)
            {
                var mirror = alternative.MirroredLibraries.FirstOrDefault(m => m.Id == mirrorId);
                if (mirror != null)
                {
                    var mirrorEntity = new LogMirrorEntity(mirrorId, mirror.SourceLibraryName, mirror.TargetLibraryName);
                    var alternativeEntity = new LogAlternativeEntity(alternative.Id, alternative.Name, alternative.LanguageCode);
                    _logger.PolyglotDebug("RemoveMirror: Removing mirror {0} from alternative {1}",
                        mirrorEntity, alternativeEntity);

                    alternative.MirroredLibraries.Remove(mirror);
                    Plugin.Instance?.SaveConfiguration();

                    _logger.PolyglotInfo("RemoveMirror: Removed mirror {0} from alternative {1}", mirrorEntity, alternativeEntity);
                    return true;
                }
            }

            _logger.PolyglotDebug("RemoveMirror: Mirror {0} not found",
                this.CreateLogMirror(mirrorId));
            return false;
        }
    }

    #endregion

    #region Alternative Operations

    /// <inheritdoc />
    public LanguageAlternative? GetAlternative(Guid alternativeId)
    {
        lock (_configLock)
        {
            var config = Plugin.Instance?.Configuration;
            var alternative = config?.LanguageAlternatives.FirstOrDefault(a => a.Id == alternativeId);

            // Return a deep copy to prevent callers from modifying config state
            return alternative?.DeepClone();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<LanguageAlternative> GetAlternatives()
    {
        lock (_configLock)
        {
            var config = Plugin.Instance?.Configuration;

            // Return deep copies to prevent callers from modifying config state
            return config?.LanguageAlternatives?.Select(a => a.DeepClone()).ToList()
                ?? new List<LanguageAlternative>();
        }
    }

    /// <inheritdoc />
    public bool UpdateAlternative(Guid alternativeId, Action<LanguageAlternative> update)
    {
        lock (_configLock)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                _logger.PolyglotWarning("UpdateAlternative: Plugin configuration is null");
                return false;
            }

            // Fresh lookup
            var alternative = config.LanguageAlternatives.FirstOrDefault(a => a.Id == alternativeId);
            if (alternative == null)
            {
                _logger.PolyglotDebug("UpdateAlternative: Alternative {0} not found",
                    this.CreateLogAlternative(alternativeId));
                return false;
            }

            var alternativeEntity = new LogAlternativeEntity(alternativeId, alternative.Name, alternative.LanguageCode);
            _logger.PolyglotDebug("UpdateAlternative: Applying update to alternative {0}", alternativeEntity);

            // Apply change
            update(alternative);
            alternative.ModifiedAt = DateTime.UtcNow;

            // Save immediately
            Plugin.Instance?.SaveConfiguration();

            _logger.PolyglotDebug("UpdateAlternative: Saved configuration after updating alternative {0}", alternativeEntity);
            return true;
        }
    }

    /// <inheritdoc />
    public bool AddAlternative(LanguageAlternative alternative)
    {
        lock (_configLock)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                _logger.PolyglotWarning("AddAlternative: Plugin configuration is null");
                return false;
            }

            // Atomic duplicate name check - prevents race conditions where two threads
            // could both pass an earlier check and both add alternatives with the same name
            if (config.LanguageAlternatives.Any(a => string.Equals(a.Name, alternative.Name, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.PolyglotWarning("AddAlternative: Duplicate name already exists for alternative {0}",
                    new LogAlternativeEntity(alternative.Id, alternative.Name, alternative.LanguageCode));
                return false;
            }

            var alternativeEntity = new LogAlternativeEntity(alternative.Id, alternative.Name, alternative.LanguageCode);
            _logger.PolyglotDebug("AddAlternative: Adding alternative {0}", alternativeEntity);

            config.LanguageAlternatives.Add(alternative);
            Plugin.Instance?.SaveConfiguration();

            _logger.PolyglotInfo("AddAlternative: Added alternative {0}", alternativeEntity);
            return true;
        }
    }

    /// <inheritdoc />
    public bool RemoveAlternative(Guid alternativeId)
    {
        lock (_configLock)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                _logger.PolyglotWarning("RemoveAlternative: Plugin configuration is null");
                return false;
            }

            var alternative = config.LanguageAlternatives.FirstOrDefault(a => a.Id == alternativeId);
            if (alternative == null)
            {
                _logger.PolyglotDebug("RemoveAlternative: Alternative {0} not found",
                    this.CreateLogAlternative(alternativeId));
                return false;
            }

            var alternativeEntity = new LogAlternativeEntity(alternativeId, alternative.Name, alternative.LanguageCode);
            _logger.PolyglotDebug("RemoveAlternative: Removing alternative {0}", alternativeEntity);

            config.LanguageAlternatives.Remove(alternative);

            // Clear DefaultLanguageAlternativeId if it references the deleted alternative
            // This prevents dangling references that would cause new user assignment failures
            if (config.DefaultLanguageAlternativeId == alternativeId)
            {
                _logger.PolyglotInfo("RemoveAlternative: Clearing DefaultLanguageAlternativeId (was pointing to deleted alternative {0})", alternativeEntity);
                config.DefaultLanguageAlternativeId = null;
            }

            // Remove LDAP group mappings that reference this alternative
            var removedMappings = config.LdapGroupMappings.RemoveAll(m => m.LanguageAlternativeId == alternativeId);
            if (removedMappings > 0)
            {
                _logger.PolyglotInfo("RemoveAlternative: Removed {0} LDAP group mapping(s) that referenced deleted alternative {1}", removedMappings, alternativeEntity);
            }

            Plugin.Instance?.SaveConfiguration();

            _logger.PolyglotInfo("RemoveAlternative: Removed alternative {0}", alternativeEntity);
            return true;
        }
    }

    /// <inheritdoc />
    public RemoveAlternativeResult TryRemoveAlternativeAtomic(Guid alternativeId, IReadOnlySet<Guid> expectedMirrorIds)
    {
        lock (_configLock)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                _logger.PolyglotWarning("TryRemoveAlternativeAtomic: Plugin configuration is null");
                return RemoveAlternativeResult.ConfigUnavailable();
            }

            var alternative = config.LanguageAlternatives.FirstOrDefault(a => a.Id == alternativeId);
            if (alternative == null)
            {
                _logger.PolyglotDebug("TryRemoveAlternativeAtomic: Alternative {0} not found",
                    this.CreateLogAlternative(alternativeId));
                return RemoveAlternativeResult.NotFound();
            }

            var alternativeEntity = new LogAlternativeEntity(alternativeId, alternative.Name, alternative.LanguageCode);

            // Check for unexpected mirrors (new mirrors added during deletion)
            var currentMirrorIds = alternative.MirroredLibraries.Select(m => m.Id).ToHashSet();
            var unexpectedMirrorIds = currentMirrorIds.Except(expectedMirrorIds).ToList();

            if (unexpectedMirrorIds.Count > 0)
            {
                _logger.PolyglotWarning(
                    "TryRemoveAlternativeAtomic: {0} new mirrors were added during deletion of alternative {1}. Aborting.",
                    unexpectedMirrorIds.Count, alternativeEntity);
                return RemoveAlternativeResult.NewMirrorsFound(unexpectedMirrorIds);
            }

            _logger.PolyglotDebug("TryRemoveAlternativeAtomic: Removing alternative {0}", alternativeEntity);

            config.LanguageAlternatives.Remove(alternative);

            // Clear DefaultLanguageAlternativeId if it references the deleted alternative
            // This prevents dangling references that would cause new user assignment failures
            if (config.DefaultLanguageAlternativeId == alternativeId)
            {
                _logger.PolyglotInfo("TryRemoveAlternativeAtomic: Clearing DefaultLanguageAlternativeId (was pointing to deleted alternative {0})", alternativeEntity);
                config.DefaultLanguageAlternativeId = null;
            }

            // Remove LDAP group mappings that reference this alternative
            var removedMappings = config.LdapGroupMappings.RemoveAll(m => m.LanguageAlternativeId == alternativeId);
            if (removedMappings > 0)
            {
                _logger.PolyglotInfo("TryRemoveAlternativeAtomic: Removed {0} LDAP group mapping(s) that referenced deleted alternative {1}", removedMappings, alternativeEntity);
            }

            Plugin.Instance?.SaveConfiguration();

            _logger.PolyglotInfo("TryRemoveAlternativeAtomic: Removed alternative {0}", alternativeEntity);
            return RemoveAlternativeResult.Succeeded();
        }
    }

    #endregion

    #region User Language Operations

    /// <inheritdoc />
    public UserLanguageConfig? GetUserLanguage(Guid userId)
    {
        lock (_configLock)
        {
            var config = Plugin.Instance?.Configuration;
            var userConfig = config?.UserLanguages.FirstOrDefault(u => u.UserId == userId);

            // Return a deep copy to prevent callers from modifying config state
            return userConfig?.DeepClone();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<UserLanguageConfig> GetUserLanguages()
    {
        lock (_configLock)
        {
            var config = Plugin.Instance?.Configuration;

            // Return deep copies to prevent callers from modifying config state
            return config?.UserLanguages?.Select(u => u.DeepClone()).ToList()
                ?? new List<UserLanguageConfig>();
        }
    }

    /// <inheritdoc />
    public bool UpdateOrCreateUserLanguage(Guid userId, Action<UserLanguageConfig> update)
    {
        lock (_configLock)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                _logger.PolyglotWarning("UpdateOrCreateUserLanguage: Plugin configuration is null");
                return false;
            }

            // Fresh lookup
            var userConfig = config.UserLanguages.FirstOrDefault(u => u.UserId == userId);
            bool isNew = userConfig == null;

            var userEntity = LogEntityFactory.CreateLogUserById(userId);
            if (isNew)
            {
                userConfig = new UserLanguageConfig { UserId = userId };
                config.UserLanguages.Add(userConfig);
                _logger.PolyglotDebug("UpdateOrCreateUserLanguage: Created new config for user {0}", userEntity);
            }
            else
            {
                _logger.PolyglotDebug("UpdateOrCreateUserLanguage: Updating existing config for user {0}", userEntity);
            }

            // Apply change
            update(userConfig!);

            // Save immediately
            Plugin.Instance?.SaveConfiguration();

            _logger.PolyglotDebug("UpdateOrCreateUserLanguage: Saved configuration for user {0} (new: {1})", userEntity, isNew);
            return isNew;
        }
    }

    /// <inheritdoc />
    public bool UpdateUserLanguage(Guid userId, Action<UserLanguageConfig> update)
    {
        lock (_configLock)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                _logger.PolyglotWarning("UpdateUserLanguage: Plugin configuration is null");
                return false;
            }

            // Fresh lookup
            var userConfig = config.UserLanguages.FirstOrDefault(u => u.UserId == userId);
            var userEntity = LogEntityFactory.CreateLogUserById(userId);
            if (userConfig == null)
            {
                _logger.PolyglotDebug("UpdateUserLanguage: User config for {0} not found", userEntity);
                return false;
            }

            _logger.PolyglotDebug("UpdateUserLanguage: Applying update to user {0}", userEntity);

            // Apply change
            update(userConfig);

            // Save immediately
            Plugin.Instance?.SaveConfiguration();

            _logger.PolyglotDebug("UpdateUserLanguage: Saved configuration for user {0}", userEntity);
            return true;
        }
    }

    /// <inheritdoc />
    public bool RemoveUserLanguage(Guid userId)
    {
        lock (_configLock)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                _logger.PolyglotWarning("RemoveUserLanguage: Plugin configuration is null");
                return false;
            }

            var userEntity = LogEntityFactory.CreateLogUserById(userId);
            var removed = config.UserLanguages.RemoveAll(u => u.UserId == userId);
            if (removed > 0)
            {
                Plugin.Instance?.SaveConfiguration();
                _logger.PolyglotInfo("RemoveUserLanguage: Removed config for user {0}", userEntity);
                return true;
            }

            _logger.PolyglotDebug("RemoveUserLanguage: User config for {0} not found", userEntity);
            return false;
        }
    }

    #endregion

    #region LDAP Group Mapping Operations

    /// <inheritdoc />
    public IReadOnlyList<LdapGroupMapping> GetLdapGroupMappings()
    {
        lock (_configLock)
        {
            var config = Plugin.Instance?.Configuration;

            // Return deep copies to prevent callers from modifying config state
            return config?.LdapGroupMappings?.Select(m => m.DeepClone()).ToList()
                ?? new List<LdapGroupMapping>();
        }
    }

    /// <inheritdoc />
    public bool AddLdapGroupMapping(LdapGroupMapping mapping)
    {
        lock (_configLock)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                _logger.PolyglotWarning("AddLdapGroupMapping: Plugin configuration is null");
                return false;
            }

            // Atomic duplicate check - prevents adding the same group DN multiple times
            if (config.LdapGroupMappings.Any(m => string.Equals(m.LdapGroupDn, mapping.LdapGroupDn, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.PolyglotWarning("AddLdapGroupMapping: Duplicate group DN already exists for mapping {0}", mapping.Id);
                return false;
            }

            _logger.PolyglotDebug("AddLdapGroupMapping: Adding mapping {0}", mapping.Id);

            config.LdapGroupMappings.Add(mapping);
            Plugin.Instance?.SaveConfiguration();

            _logger.PolyglotInfo("AddLdapGroupMapping: Added LDAP mapping {0} -> alternative {1}",
                mapping.Id, this.CreateLogAlternative(mapping.LanguageAlternativeId));
            return true;
        }
    }

    /// <inheritdoc />
    public bool RemoveLdapGroupMapping(Guid mappingId)
    {
        lock (_configLock)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                _logger.PolyglotWarning("RemoveLdapGroupMapping: Plugin configuration is null");
                return false;
            }

            var mapping = config.LdapGroupMappings.FirstOrDefault(m => m.Id == mappingId);
            if (mapping == null)
            {
                _logger.PolyglotDebug("RemoveLdapGroupMapping: Mapping {0} not found", mappingId);
                return false;
            }

            _logger.PolyglotDebug("RemoveLdapGroupMapping: Removing mapping {0}", mappingId);

            config.LdapGroupMappings.Remove(mapping);
            Plugin.Instance?.SaveConfiguration();

            _logger.PolyglotInfo("RemoveLdapGroupMapping: Removed LDAP mapping {0}", mappingId);
            return true;
        }
    }

    #endregion

    #region Global Settings

    /// <inheritdoc />
    public bool UpdateSettings(Func<PluginConfiguration, bool> update)
    {
        lock (_configLock)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                _logger.PolyglotWarning("UpdateSettings: Plugin configuration is null");
                return false;
            }

            _logger.PolyglotDebug("UpdateSettings: Applying settings update with validation");

            // Apply change and check if we should save
            var shouldSave = update(config);

            if (shouldSave)
            {
                // Save immediately
                Plugin.Instance?.SaveConfiguration();
                _logger.PolyglotDebug("UpdateSettings: Saved configuration after settings update");
            }
            else
            {
                _logger.PolyglotDebug("UpdateSettings: Update function returned false, skipping save");
            }

            return shouldSave;
        }
    }

    /// <inheritdoc />
    public void UpdateSettings(Action<PluginConfiguration> update)
    {
        UpdateSettings(config =>
        {
            update(config);
            return true; // Always save for the Action overload
        });
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// WARNING: This returns a shallow reference to the live configuration object.
    /// Use ONLY for reading simple scalar properties (EnableLdapIntegration, AutoManageNewUsers, etc.).
    /// </para>
    /// <para>
    /// DO NOT access collection properties (LanguageAlternatives, UserLanguages, LdapGroupMappings,
    /// ExcludedExtensions, ExcludedDirectories) through this method - use the specific Get* methods
    /// instead which return thread-safe copies.
    /// </para>
    /// <para>
    /// DO NOT modify any properties through this reference - use UpdateSettings() or specific Update* methods.
    /// </para>
    /// </remarks>
    public PluginConfiguration? GetConfiguration()
    {
        // Note: We intentionally don't lock here because:
        // 1. The reference itself is thread-safe to read
        // 2. Callers should only read simple scalar properties which are atomic
        // 3. For collection access, callers MUST use GetAlternatives(), GetUserLanguages(), etc.
        return Plugin.Instance?.Configuration;
    }

    /// <inheritdoc />
    public IReadOnlySet<string> GetExcludedExtensions()
    {
        lock (_configLock)
        {
            var config = Plugin.Instance?.Configuration;
            return config?.ExcludedExtensions?.ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <inheritdoc />
    public IReadOnlySet<string> GetExcludedDirectories()
    {
        lock (_configLock)
        {
            var config = Plugin.Instance?.Configuration;
            return config?.ExcludedDirectories?.ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <inheritdoc />
    public IReadOnlySet<string> GetDefaultExcludedExtensions()
    {
        lock (_configLock)
        {
            var config = Plugin.Instance?.Configuration;
            return config?.DefaultExcludedExtensions?.ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <inheritdoc />
    public IReadOnlySet<string> GetDefaultExcludedDirectories()
    {
        lock (_configLock)
        {
            var config = Plugin.Instance?.Configuration;
            return config?.DefaultExcludedDirectories?.ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    #endregion

    #region Batch Operations

    /// <inheritdoc />
    public void ClearAllConfiguration()
    {
        lock (_configLock)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                _logger.PolyglotWarning("ClearAllConfiguration: Plugin configuration is null");
                return;
            }

            _logger.PolyglotInfo("ClearAllConfiguration: Clearing all configuration data");

            config.LanguageAlternatives.Clear();
            config.UserLanguages.Clear();
            config.LdapGroupMappings.Clear();

            Plugin.Instance?.SaveConfiguration();

            _logger.PolyglotInfo("ClearAllConfiguration: All configuration data cleared");
        }
    }

    #endregion
}

