using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Plugins;
using Microsoft.Extensions.Logging;
using Novell.Directory.Ldap;

namespace Jellyfin.Plugin.Polyglot.Services;

/// <summary>
/// Service for LDAP integration to determine user language from group membership.
/// </summary>
public class LdapIntegrationService : ILdapIntegrationService
{
    private const string LdapPluginId = "958aad66-3784-4f94-b0db-ff87df5c155e";

    private readonly IPluginManager _pluginManager;
    private readonly ILogger<LdapIntegrationService> _logger;

    // Cached LDAP configuration (read from LDAP plugin)
    private LdapConfiguration? _cachedConfig;
    private DateTime _configCacheTime;
    private readonly TimeSpan _configCacheDuration = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Initializes a new instance of the <see cref="LdapIntegrationService"/> class.
    /// </summary>
    public LdapIntegrationService(IPluginManager pluginManager, ILogger<LdapIntegrationService> logger)
    {
        _pluginManager = pluginManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsLdapPluginAvailable()
    {
        try
        {
            var ldapPlugin = GetLdapPlugin();
            return ldapPlugin != null;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public LdapStatus GetLdapStatus()
    {
        var status = new LdapStatus();

        try
        {
            var ldapPlugin = GetLdapPlugin();
            status.IsPluginInstalled = ldapPlugin != null;

            if (ldapPlugin != null)
            {
                var config = GetLdapConfiguration(ldapPlugin);
                status.IsConfigured = !string.IsNullOrEmpty(config?.LdapServer);
                status.ServerAddress = config?.LdapServer;
            }

            var pluginConfig = Plugin.Instance?.Configuration;
            status.IsIntegrationEnabled = pluginConfig?.EnableLdapIntegration ?? false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get LDAP status");
            status.ErrorMessage = ex.Message;
        }

        return status;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<string>> GetUserGroupsAsync(string username, CancellationToken cancellationToken = default)
    {
        var ldapPlugin = GetLdapPlugin();
        if (ldapPlugin == null)
        {
            _logger.LogWarning("LDAP plugin not available");
            return Enumerable.Empty<string>();
        }

        var ldapConfig = GetLdapConfiguration(ldapPlugin);
        if (ldapConfig == null || string.IsNullOrEmpty(ldapConfig.LdapServer))
        {
            _logger.LogWarning("LDAP not configured");
            return Enumerable.Empty<string>();
        }

        try
        {
            return await QueryLdapGroupsAsync(username, ldapConfig, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query LDAP groups for user {Username}", username);
            return Enumerable.Empty<string>();
        }
    }

    /// <inheritdoc />
    public async Task<Guid?> DetermineLanguageFromGroupsAsync(string username, CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null || !config.EnableLdapIntegration || config.LdapGroupMappings.Count == 0)
        {
            return null;
        }

        var userGroups = await GetUserGroupsAsync(username, cancellationToken).ConfigureAwait(false);
        var groupSet = new HashSet<string>(userGroups, StringComparer.OrdinalIgnoreCase);

        if (groupSet.Count == 0)
        {
            return null;
        }

        // Find matching mappings ordered by priority (highest first)
        // For equal priorities, preserve original mapping order (first in list wins)
        var matchingMappings = config.LdapGroupMappings
            .Select((mapping, index) => new { Mapping = mapping, Index = index })
            .Where(x => groupSet.Contains(x.Mapping.LdapGroupDn) || groupSet.Contains(x.Mapping.LdapGroupName))
            .OrderByDescending(x => x.Mapping.Priority)
            .ThenBy(x => x.Index)
            .Select(x => x.Mapping)
            .ToList();

        if (matchingMappings.Count == 0)
        {
            return null;
        }

        var bestMatch = matchingMappings.First();
        _logger.LogDebug(
            "User {Username} matched LDAP group {GroupDn} with priority {Priority}",
            username,
            bestMatch.LdapGroupDn,
            bestMatch.Priority);

        return bestMatch.LanguageAlternativeId;
    }

    /// <inheritdoc />
    public async Task<LdapTestResult> TestConnectionAsync(string? testUsername = null, CancellationToken cancellationToken = default)
    {
        var result = new LdapTestResult();

        try
        {
            var ldapPlugin = GetLdapPlugin();
            if (ldapPlugin == null)
            {
                result.Message = "LDAP authentication plugin is not installed";
                return result;
            }

            var ldapConfig = GetLdapConfiguration(ldapPlugin);
            if (ldapConfig == null || string.IsNullOrEmpty(ldapConfig.LdapServer))
            {
                result.Message = "LDAP is not configured in the authentication plugin";
                return result;
            }

            // Test connection
            using var connection = new LdapConnection();
            var port = ldapConfig.LdapPort > 0 ? ldapConfig.LdapPort : (ldapConfig.UseSsl ? 636 : 389);

            connection.Connect(ldapConfig.LdapServer, port);

            if (ldapConfig.UseSsl || ldapConfig.UseStartTls)
            {
                connection.StartTls();
            }

            // Bind with service account
            if (!string.IsNullOrEmpty(ldapConfig.LdapBindUser))
            {
                connection.Bind(ldapConfig.LdapBindUser, ldapConfig.LdapBindPassword);
            }

            result.Success = true;
            result.Message = $"Successfully connected to {ldapConfig.LdapServer}:{port}";

            // If test username provided, look up their groups
            if (!string.IsNullOrEmpty(testUsername))
            {
                var groups = await QueryLdapGroupsAsync(testUsername, ldapConfig, cancellationToken).ConfigureAwait(false);
                result.UserGroups = groups;
                result.Message += $" - Found {groups.Count()} groups for user {testUsername}";

                // Check for language match
                var languageId = await DetermineLanguageFromGroupsAsync(testUsername, cancellationToken).ConfigureAwait(false);
                if (languageId.HasValue)
                {
                    var config = Plugin.Instance?.Configuration;
                    var alt = config?.LanguageAlternatives.FirstOrDefault(a => a.Id == languageId.Value);
                    result.MatchedLanguage = alt?.Name;
                    result.Message += $" - Matched language: {alt?.Name}";
                }
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = $"Connection failed: {ex.Message}";
            _logger.LogError(ex, "LDAP connection test failed");
        }

        return result;
    }

    /// <summary>
    /// Gets the LDAP authentication plugin instance.
    /// </summary>
    private IPlugin? GetLdapPlugin()
    {
        var plugins = _pluginManager.Plugins;
        return plugins.FirstOrDefault(p =>
            string.Equals(p.Id.ToString(), LdapPluginId, StringComparison.OrdinalIgnoreCase) ||
            p.Name.Contains("LDAP", StringComparison.OrdinalIgnoreCase))?.Instance;
    }

    /// <summary>
    /// Property names expected in the LDAP plugin configuration.
    /// Used to validate schema compatibility.
    /// </summary>
    private static readonly string[] RequiredLdapProperties = new[]
    {
        "LdapServer",
        "LdapPort",
        "LdapBaseDn",
        "LdapBindUser",
        "LdapBindPassword",
        "LdapSearchFilter"
    };

    /// <summary>
    /// Reads LDAP configuration from the LDAP plugin via reflection.
    /// </summary>
    private LdapConfiguration? GetLdapConfiguration(IPlugin ldapPlugin)
    {
        // Check cache
        if (_cachedConfig != null && DateTime.UtcNow - _configCacheTime < _configCacheDuration)
        {
            return _cachedConfig;
        }

        try
        {
            // Use reflection to get the Configuration property
            var configProperty = ldapPlugin.GetType().GetProperty("Configuration");
            if (configProperty == null)
            {
                _logger.LogError("LDAP plugin does not have a Configuration property - plugin may be incompatible");
                return null;
            }

            var configObj = configProperty.GetValue(ldapPlugin);
            if (configObj == null)
            {
                _logger.LogWarning("LDAP plugin configuration is null");
                return null;
            }

            var configType = configObj.GetType();

            // Validate schema - check for required properties
            var missingProperties = RequiredLdapProperties
                .Where(p => configType.GetProperty(p, BindingFlags.Public | BindingFlags.Instance) == null)
                .ToList();

            if (missingProperties.Count > 0)
            {
                _logger.LogWarning(
                    "LDAP plugin configuration is missing expected properties: {MissingProperties}. " +
                    "The LDAP plugin version may be incompatible. Integration may not work correctly.",
                    string.Join(", ", missingProperties));
            }

            // Map properties via reflection
            var config = new LdapConfiguration();

            config.LdapServer = GetPropertyValue<string>(configType, configObj, "LdapServer", _logger) ?? string.Empty;
            config.LdapPort = GetPropertyValue<int>(configType, configObj, "LdapPort", _logger);
            config.UseSsl = GetPropertyValue<bool>(configType, configObj, "UseSsl", _logger);
            config.UseStartTls = GetPropertyValue<bool>(configType, configObj, "UseStartTls", _logger);
            config.SkipSslVerify = GetPropertyValue<bool>(configType, configObj, "SkipSslVerify", _logger);
            config.LdapBaseDn = GetPropertyValue<string>(configType, configObj, "LdapBaseDn", _logger) ?? string.Empty;
            config.LdapBindUser = GetPropertyValue<string>(configType, configObj, "LdapBindUser", _logger) ?? string.Empty;
            config.LdapBindPassword = GetPropertyValue<string>(configType, configObj, "LdapBindPassword", _logger) ?? string.Empty;
            config.LdapSearchFilter = GetPropertyValue<string>(configType, configObj, "LdapSearchFilter", _logger) ?? string.Empty;
            config.LdapUidAttribute = GetPropertyValue<string>(configType, configObj, "LdapUidAttribute", _logger) ?? "uid";

            _cachedConfig = config;
            _configCacheTime = DateTime.UtcNow;

            return config;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read LDAP plugin configuration");
            return null;
        }
    }

    /// <summary>
    /// Gets a property value from an object via reflection.
    /// </summary>
    private static T? GetPropertyValue<T>(Type type, object obj, string propertyName, ILogger logger)
    {
        var property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (property == null)
        {
            logger.LogDebug("LDAP config property {PropertyName} not found", propertyName);
            return default;
        }

        var value = property.GetValue(obj);
        if (value == null)
        {
            return default;
        }

        return (T)value;
    }

    /// <summary>
    /// Queries LDAP for a user's group memberships.
    /// </summary>
    private async Task<IEnumerable<string>> QueryLdapGroupsAsync(
        string username,
        LdapConfiguration config,
        CancellationToken cancellationToken)
    {
        var groups = new List<string>();

        await Task.Run(() =>
        {
            using var connection = new LdapConnection();
            var port = config.LdapPort > 0 ? config.LdapPort : (config.UseSsl ? 636 : 389);

            connection.Connect(config.LdapServer, port);

            if (config.UseSsl || config.UseStartTls)
            {
                connection.StartTls();
            }

            if (!string.IsNullOrEmpty(config.LdapBindUser))
            {
                connection.Bind(config.LdapBindUser, config.LdapBindPassword);
            }

            // Find the user first
            var userFilter = string.IsNullOrEmpty(config.LdapSearchFilter)
                ? $"({config.LdapUidAttribute}={EscapeLdapFilter(username)})"
                : config.LdapSearchFilter.Replace("{0}", EscapeLdapFilter(username));

            var userResults = connection.Search(
                config.LdapBaseDn,
                LdapConnection.ScopeSub,
                userFilter,
                new[] { "dn", "memberOf" },
                false);

            while (userResults.HasMore())
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var entry = userResults.Next();

                    // Get memberOf attribute (direct group memberships)
                    var memberOf = entry.GetAttribute("memberOf");
                    if (memberOf != null)
                    {
                        foreach (var groupDn in memberOf.StringValueArray)
                        {
                            groups.Add(groupDn);

                            // Also extract CN for easier matching
                            var cn = ExtractCnFromDn(groupDn);
                            if (!string.IsNullOrEmpty(cn))
                            {
                                groups.Add(cn);
                            }
                        }
                    }
                }
                catch (LdapException)
                {
                    break;
                }
            }
        }, cancellationToken).ConfigureAwait(false);

        return groups.Distinct();
    }

    /// <summary>
    /// Escapes special characters in LDAP filter values.
    /// </summary>
    private static string EscapeLdapFilter(string value)
    {
        return value
            .Replace("\\", "\\5c")
            .Replace("*", "\\2a")
            .Replace("(", "\\28")
            .Replace(")", "\\29")
            .Replace("\0", "\\00");
    }

    /// <summary>
    /// Extracts the CN (Common Name) from a DN (Distinguished Name).
    /// </summary>
    private static string? ExtractCnFromDn(string dn)
    {
        if (string.IsNullOrEmpty(dn))
        {
            return null;
        }

        // Simple parsing for CN=value,...
        var parts = dn.Split(',');
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed.Substring(3);
            }
        }

        return null;
    }

    /// <summary>
    /// Internal LDAP configuration model.
    /// </summary>
    private class LdapConfiguration
    {
        public string LdapServer { get; set; } = string.Empty;
        public int LdapPort { get; set; }
        public bool UseSsl { get; set; }
        public bool UseStartTls { get; set; }
        public bool SkipSslVerify { get; set; }
        public string LdapBaseDn { get; set; } = string.Empty;
        public string LdapBindUser { get; set; } = string.Empty;
        public string LdapBindPassword { get; set; } = string.Empty;
        public string LdapSearchFilter { get; set; } = string.Empty;
        public string LdapUidAttribute { get; set; } = "uid";
    }
}

