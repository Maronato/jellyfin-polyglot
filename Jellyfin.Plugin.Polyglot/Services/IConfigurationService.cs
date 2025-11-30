using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Polyglot.Configuration;
using Jellyfin.Plugin.Polyglot.Models;

namespace Jellyfin.Plugin.Polyglot.Services;

/// <summary>
/// Service for atomic configuration updates.
/// All modifications go through this service to prevent race conditions and stale reference bugs.
/// Each update method performs: lock → fresh lookup → apply change → save → unlock.
/// <para>
/// Thread Safety: All Get* methods return deep copies of the configuration data.
/// Callers can safely read/iterate the returned objects without holding locks.
/// To modify configuration, use the corresponding Update*/Add*/Remove* methods.
/// </para>
/// </summary>
public interface IConfigurationService
{
    #region Mirror Operations

    /// <summary>
    /// Gets a mirror by ID. Returns a deep copy that is safe to read without locks.
    /// </summary>
    /// <param name="mirrorId">The mirror ID.</param>
    /// <returns>A deep copy of the mirror if found, null otherwise.</returns>
    LibraryMirror? GetMirror(Guid mirrorId);

    /// <summary>
    /// Gets a mirror by ID along with its parent alternative ID.
    /// Returns a deep copy that is safe to read without locks.
    /// </summary>
    /// <param name="mirrorId">The mirror ID.</param>
    /// <returns>Tuple of (deep copy of mirror, alternativeId) if found, null otherwise.</returns>
    (LibraryMirror Mirror, Guid AlternativeId)? GetMirrorWithAlternative(Guid mirrorId);

    /// <summary>
    /// Atomically updates a mirror. Performs fresh lookup before applying changes.
    /// </summary>
    /// <param name="mirrorId">The mirror ID.</param>
    /// <param name="update">Action to apply to the mirror.</param>
    /// <returns>True if mirror was found and updated, false otherwise.</returns>
    bool UpdateMirror(Guid mirrorId, Action<LibraryMirror> update);

    /// <summary>
    /// Adds a new mirror to an alternative. The mirror object should be fully initialized.
    /// Atomically checks for duplicate source library mirrors within the lock.
    /// </summary>
    /// <param name="alternativeId">The alternative to add the mirror to.</param>
    /// <param name="mirror">The mirror to add.</param>
    /// <returns>True if alternative was found and mirror was added. False if alternative not found or duplicate source library already exists.</returns>
    bool AddMirror(Guid alternativeId, LibraryMirror mirror);

    /// <summary>
    /// Removes a mirror from its parent alternative.
    /// </summary>
    /// <param name="mirrorId">The mirror ID to remove.</param>
    /// <returns>True if mirror was found and removed, false otherwise.</returns>
    bool RemoveMirror(Guid mirrorId);

    #endregion

    #region Alternative Operations

    /// <summary>
    /// Gets an alternative by ID. Returns a deep copy that is safe to read without locks.
    /// </summary>
    /// <param name="alternativeId">The alternative ID.</param>
    /// <returns>A deep copy of the alternative if found, null otherwise.</returns>
    LanguageAlternative? GetAlternative(Guid alternativeId);

    /// <summary>
    /// Gets all alternatives. Returns deep copies that are safe to read/iterate without locks.
    /// </summary>
    /// <returns>List of deep copies of all alternatives.</returns>
    IReadOnlyList<LanguageAlternative> GetAlternatives();

    /// <summary>
    /// Atomically updates an alternative. Performs fresh lookup before applying changes.
    /// </summary>
    /// <param name="alternativeId">The alternative ID.</param>
    /// <param name="update">Action to apply to the alternative.</param>
    /// <returns>True if alternative was found and updated, false otherwise.</returns>
    bool UpdateAlternative(Guid alternativeId, Action<LanguageAlternative> update);

    /// <summary>
    /// Adds a new alternative. The alternative object should be fully initialized.
    /// Atomically checks for duplicate names within the lock.
    /// </summary>
    /// <param name="alternative">The alternative to add.</param>
    /// <returns>True if added successfully. False if config is null or duplicate name exists.</returns>
    bool AddAlternative(LanguageAlternative alternative);

    /// <summary>
    /// Removes an alternative by ID.
    /// </summary>
    /// <param name="alternativeId">The alternative ID to remove.</param>
    /// <returns>True if alternative was found and removed, false otherwise.</returns>
    bool RemoveAlternative(Guid alternativeId);

    /// <summary>
    /// Atomically removes an alternative only if its current mirrors match the expected set.
    /// This prevents race conditions where mirrors could be added during deletion.
    /// </summary>
    /// <param name="alternativeId">The alternative ID to remove.</param>
    /// <param name="expectedMirrorIds">The set of mirror IDs expected to be in the alternative (should be empty after deletion).</param>
    /// <returns>
    /// A result indicating success, failure reason, or list of unexpected mirror IDs if new mirrors were added.
    /// </returns>
    RemoveAlternativeResult TryRemoveAlternativeAtomic(Guid alternativeId, IReadOnlySet<Guid> expectedMirrorIds);

    #endregion

    #region User Language Operations

    /// <summary>
    /// Gets a user's language config by user ID. Returns a deep copy that is safe to read without locks.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <returns>A deep copy of the user config if found, null otherwise.</returns>
    UserLanguageConfig? GetUserLanguage(Guid userId);

    /// <summary>
    /// Gets all user language configs. Returns deep copies that are safe to read/iterate without locks.
    /// </summary>
    /// <returns>List of deep copies of all user configs.</returns>
    IReadOnlyList<UserLanguageConfig> GetUserLanguages();

    /// <summary>
    /// Atomically updates a user's language config. Creates if not exists.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="update">Action to apply to the user config.</param>
    /// <returns>True if a new config was created, false if an existing config was updated.</returns>
    bool UpdateOrCreateUserLanguage(Guid userId, Action<UserLanguageConfig> update);

    /// <summary>
    /// Atomically updates an existing user's language config.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="update">Action to apply to the user config.</param>
    /// <returns>True if user config was found and updated, false otherwise.</returns>
    bool UpdateUserLanguage(Guid userId, Action<UserLanguageConfig> update);

    /// <summary>
    /// Removes a user's language config.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <returns>True if user config was found and removed, false otherwise.</returns>
    bool RemoveUserLanguage(Guid userId);

    #endregion

    #region LDAP Group Mapping Operations

    /// <summary>
    /// Gets all LDAP group mappings. Returns deep copies that are safe to read/iterate without locks.
    /// </summary>
    /// <returns>List of deep copies of all LDAP group mappings.</returns>
    IReadOnlyList<LdapGroupMapping> GetLdapGroupMappings();

    /// <summary>
    /// Adds a new LDAP group mapping.
    /// Atomically checks for duplicate group DNs within the lock.
    /// </summary>
    /// <param name="mapping">The mapping to add.</param>
    /// <returns>True if added successfully. False if config is null or duplicate group DN exists.</returns>
    bool AddLdapGroupMapping(LdapGroupMapping mapping);

    /// <summary>
    /// Removes an LDAP group mapping by ID.
    /// </summary>
    /// <param name="mappingId">The mapping ID to remove.</param>
    /// <returns>True if mapping was found and removed, false otherwise.</returns>
    bool RemoveLdapGroupMapping(Guid mappingId);

    #endregion

    #region Global Settings

    /// <summary>
    /// Atomically updates global plugin settings.
    /// </summary>
    /// <param name="update">Action to apply to the configuration. Return true to save, false to abort without saving.</param>
    /// <returns>True if changes were saved, false if the update function returned false or config was null.</returns>
    bool UpdateSettings(Func<PluginConfiguration, bool> update);

    /// <summary>
    /// Atomically updates global plugin settings (always saves).
    /// </summary>
    /// <param name="update">Action to apply to the configuration.</param>
    void UpdateSettings(Action<PluginConfiguration> update);

    /// <summary>
    /// Gets the current plugin configuration for reading simple scalar properties only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WARNING: This returns a reference to the live configuration object.
    /// Use ONLY for reading simple scalar properties (EnableLdapIntegration, AutoManageNewUsers, etc.).
    /// </para>
    /// <para>
    /// DO NOT access collection properties (LanguageAlternatives, UserLanguages, LdapGroupMappings,
    /// ExcludedExtensions, ExcludedDirectories) through this method - use the specific Get* methods instead,
    /// which return thread-safe copies.
    /// </para>
    /// <para>
    /// DO NOT modify any properties through this reference - use UpdateSettings() or specific Update* methods.
    /// </para>
    /// </remarks>
    /// <returns>The current configuration reference for reading scalar properties.</returns>
    PluginConfiguration? GetConfiguration();

    /// <summary>
    /// Gets the excluded file extensions. Returns a thread-safe copy.
    /// </summary>
    /// <returns>Copy of the excluded extensions set.</returns>
    IReadOnlySet<string> GetExcludedExtensions();

    /// <summary>
    /// Gets the excluded directory names. Returns a thread-safe copy.
    /// </summary>
    /// <returns>Copy of the excluded directories set.</returns>
    IReadOnlySet<string> GetExcludedDirectories();

    /// <summary>
    /// Gets the default excluded file extensions. Returns a thread-safe copy.
    /// </summary>
    /// <returns>Copy of the default excluded extensions set.</returns>
    IReadOnlySet<string> GetDefaultExcludedExtensions();

    /// <summary>
    /// Gets the default excluded directory names. Returns a thread-safe copy.
    /// </summary>
    /// <returns>Copy of the default excluded directories set.</returns>
    IReadOnlySet<string> GetDefaultExcludedDirectories();

    #endregion

    #region Batch Operations

    /// <summary>
    /// Clears all configuration data (used during uninstall).
    /// </summary>
    void ClearAllConfiguration();

    #endregion
}

/// <summary>
/// Result of attempting to atomically remove an alternative.
/// </summary>
public class RemoveAlternativeResult
{
    /// <summary>
    /// Gets or sets a value indicating whether the removal was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the reason for failure if Success is false.
    /// </summary>
    public RemoveAlternativeFailureReason FailureReason { get; set; }

    /// <summary>
    /// Gets or sets the list of unexpected mirror IDs that were added during deletion.
    /// Only populated when FailureReason is NewMirrorsAdded.
    /// </summary>
    public IReadOnlyList<Guid> UnexpectedMirrorIds { get; set; } = Array.Empty<Guid>();

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static RemoveAlternativeResult Succeeded() => new() { Success = true };

    /// <summary>
    /// Creates a failure result for alternative not found.
    /// </summary>
    public static RemoveAlternativeResult NotFound() => new()
    {
        Success = false,
        FailureReason = RemoveAlternativeFailureReason.AlternativeNotFound
    };

    /// <summary>
    /// Creates a failure result for configuration unavailable.
    /// </summary>
    public static RemoveAlternativeResult ConfigUnavailable() => new()
    {
        Success = false,
        FailureReason = RemoveAlternativeFailureReason.ConfigurationUnavailable
    };

    /// <summary>
    /// Creates a failure result for new mirrors added during deletion.
    /// </summary>
    public static RemoveAlternativeResult NewMirrorsFound(IReadOnlyList<Guid> mirrorIds) => new()
    {
        Success = false,
        FailureReason = RemoveAlternativeFailureReason.NewMirrorsAdded,
        UnexpectedMirrorIds = mirrorIds
    };
}

/// <summary>
/// Reasons for failing to remove an alternative atomically.
/// </summary>
public enum RemoveAlternativeFailureReason
{
    /// <summary>
    /// No failure - operation succeeded.
    /// </summary>
    None = 0,

    /// <summary>
    /// The alternative was not found.
    /// </summary>
    AlternativeNotFound,

    /// <summary>
    /// Plugin configuration is unavailable.
    /// </summary>
    ConfigurationUnavailable,

    /// <summary>
    /// New mirrors were added to the alternative during the deletion process.
    /// </summary>
    NewMirrorsAdded
}

