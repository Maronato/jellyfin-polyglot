using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Polyglot.Services;

/// <summary>
/// Service for LDAP integration to determine user language from group membership.
/// </summary>
public interface ILdapIntegrationService
{
    /// <summary>
    /// Checks if the LDAP authentication plugin is installed and available.
    /// </summary>
    /// <returns>True if LDAP plugin is available.</returns>
    bool IsLdapPluginAvailable();

    /// <summary>
    /// Gets the LDAP configuration status.
    /// </summary>
    /// <returns>Status information about LDAP configuration.</returns>
    LdapStatus GetLdapStatus();

    /// <summary>
    /// Gets the LDAP groups that a user belongs to.
    /// </summary>
    /// <param name="username">The username to query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Collection of group DNs or CNs.</returns>
    Task<IEnumerable<string>> GetUserGroupsAsync(string username, CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines the appropriate language alternative for a user based on their LDAP groups.
    /// </summary>
    /// <param name="username">The username.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The language alternative ID, or null if no match.</returns>
    Task<Guid?> DetermineLanguageFromGroupsAsync(string username, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tests the LDAP connection and optionally looks up a user.
    /// </summary>
    /// <param name="testUsername">Optional username to test lookup.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Test result with details.</returns>
    Task<LdapTestResult> TestConnectionAsync(string? testUsername = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents the status of LDAP integration.
/// </summary>
public class LdapStatus
{
    /// <summary>
    /// Gets or sets a value indicating whether the LDAP plugin is installed.
    /// </summary>
    public bool IsPluginInstalled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether LDAP is configured in the plugin.
    /// </summary>
    public bool IsConfigured { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether LDAP integration is enabled in this plugin.
    /// </summary>
    public bool IsIntegrationEnabled { get; set; }

    /// <summary>
    /// Gets or sets the LDAP server address if configured.
    /// </summary>
    public string? ServerAddress { get; set; }

    /// <summary>
    /// Gets or sets any error message related to LDAP status.
    /// </summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Result of an LDAP connection test.
/// </summary>
public class LdapTestResult
{
    /// <summary>
    /// Gets or sets a value indicating whether the test was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the result message.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the groups found for the test user (if provided).
    /// </summary>
    public IEnumerable<string>? UserGroups { get; set; }

    /// <summary>
    /// Gets or sets the matched language alternative (if any).
    /// </summary>
    public string? MatchedLanguage { get; set; }
}

