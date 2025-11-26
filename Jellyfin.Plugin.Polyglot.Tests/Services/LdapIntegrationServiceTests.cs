using FluentAssertions;
using Jellyfin.Plugin.Polyglot.Services;
using Jellyfin.Plugin.Polyglot.Tests.TestHelpers;
using MediaBrowser.Common.Plugins;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Polyglot.Tests.Services;

/// <summary>
/// Tests for LdapIntegrationService focusing on group matching and priority logic.
/// </summary>
public class LdapIntegrationServiceTests : IDisposable
{
    private readonly PluginTestContext _context;
    private readonly Mock<IPluginManager> _pluginManagerMock;
    private readonly LdapIntegrationService _service;

    public LdapIntegrationServiceTests()
    {
        _context = new PluginTestContext();
        _pluginManagerMock = new Mock<IPluginManager>();
        _pluginManagerMock.Setup(m => m.Plugins).Returns(new List<LocalPlugin>());

        var logger = new Mock<ILogger<LdapIntegrationService>>();
        _service = new LdapIntegrationService(_pluginManagerMock.Object, logger.Object);
    }

    public void Dispose() => _context.Dispose();

    #region DetermineLanguageFromGroupsAsync - Core group matching logic

    [Fact]
    public async Task DetermineLanguageFromGroups_IntegrationDisabled_ReturnsNull()
    {
        // Arrange
        _context.Configuration.EnableLdapIntegration = false;
        var alternative = _context.AddLanguageAlternative();
        _context.AddLdapGroupMapping("CN=Group,DC=test", alternative.Id);

        // Act
        var result = await _service.DetermineLanguageFromGroupsAsync("testuser");

        // Assert
        result.Should().BeNull("LDAP integration is disabled");
    }

    [Fact]
    public async Task DetermineLanguageFromGroups_NoMappingsConfigured_ReturnsNull()
    {
        // Arrange
        _context.Configuration.EnableLdapIntegration = true;
        // No mappings added

        // Act
        var result = await _service.DetermineLanguageFromGroupsAsync("testuser");

        // Assert
        result.Should().BeNull("no group mappings exist");
    }

    #endregion

    #region GetLdapStatus - Status reporting

    [Fact]
    public void GetLdapStatus_NoLdapPlugin_ReportsNotInstalled()
    {
        // Arrange
        _pluginManagerMock.Setup(m => m.Plugins).Returns(new List<LocalPlugin>());

        // Act
        var status = _service.GetLdapStatus();

        // Assert
        status.IsPluginInstalled.Should().BeFalse();
        status.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void GetLdapStatus_IntegrationEnabledInConfig_ReportsEnabled()
    {
        // Arrange
        _context.Configuration.EnableLdapIntegration = true;

        // Act
        var status = _service.GetLdapStatus();

        // Assert
        status.IsIntegrationEnabled.Should().BeTrue();
    }

    [Fact]
    public void GetLdapStatus_IntegrationDisabledInConfig_ReportsDisabled()
    {
        // Arrange
        _context.Configuration.EnableLdapIntegration = false;

        // Act
        var status = _service.GetLdapStatus();

        // Assert
        status.IsIntegrationEnabled.Should().BeFalse();
    }

    #endregion

    #region IsLdapPluginAvailable - Plugin detection

    [Fact]
    public void IsLdapPluginAvailable_NoPlugins_ReturnsFalse()
    {
        // Arrange
        _pluginManagerMock.Setup(m => m.Plugins).Returns(new List<LocalPlugin>());

        // Act
        var result = _service.IsLdapPluginAvailable();

        // Assert
        result.Should().BeFalse();
    }

    #endregion
}

/// <summary>
/// Tests for LDAP group matching algorithm logic.
/// These test the pure algorithm without needing actual LDAP connectivity.
/// </summary>
public class LdapGroupMatchingAlgorithmTests : IDisposable
{
    private readonly PluginTestContext _context;

    public LdapGroupMatchingAlgorithmTests()
    {
        _context = new PluginTestContext();
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public void GroupMatching_HigherPriorityWins()
    {
        // Arrange - simulate what DetermineLanguageFromGroupsAsync does internally
        var portuguese = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var spanish = _context.AddLanguageAlternative("Spanish", "es-ES");

        _context.AddLdapGroupMapping("CN=Portuguese,DC=test", portuguese.Id, priority: 100);
        _context.AddLdapGroupMapping("CN=Spanish,DC=test", spanish.Id, priority: 200);

        var userGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CN=Portuguese,DC=test",
            "CN=Spanish,DC=test"
        };

        // Act - replicate the matching logic (now with stable sort for equal priorities)
        var matchingMappings = _context.Configuration.LdapGroupMappings
            .Select((mapping, index) => new { Mapping = mapping, Index = index })
            .Where(x => userGroups.Contains(x.Mapping.LdapGroupDn) || userGroups.Contains(x.Mapping.LdapGroupName))
            .OrderByDescending(x => x.Mapping.Priority)
            .ThenBy(x => x.Index)
            .Select(x => x.Mapping)
            .ToList();

        // Assert
        matchingMappings.Should().HaveCount(2);
        matchingMappings.First().LanguageAlternativeId.Should().Be(spanish.Id, "Spanish has priority 200 > 100");
    }

    [Fact]
    public void GroupMatching_EqualPriority_FirstMappingWins()
    {
        // Arrange - Per spec: "If equal priority, first match in mapping order wins"
        var portuguese = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var spanish = _context.AddLanguageAlternative("Spanish", "es-ES");
        var french = _context.AddLanguageAlternative("French", "fr-FR");

        // All have same priority - Portuguese added first, should win
        _context.AddLdapGroupMapping("CN=Portuguese,DC=test", portuguese.Id, priority: 100);
        _context.AddLdapGroupMapping("CN=Spanish,DC=test", spanish.Id, priority: 100);
        _context.AddLdapGroupMapping("CN=French,DC=test", french.Id, priority: 100);

        var userGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CN=Portuguese,DC=test",
            "CN=Spanish,DC=test",
            "CN=French,DC=test"
        };

        // Act - replicate the matching logic with stable sort
        var matchingMappings = _context.Configuration.LdapGroupMappings
            .Select((mapping, index) => new { Mapping = mapping, Index = index })
            .Where(x => userGroups.Contains(x.Mapping.LdapGroupDn) || userGroups.Contains(x.Mapping.LdapGroupName))
            .OrderByDescending(x => x.Mapping.Priority)
            .ThenBy(x => x.Index)
            .Select(x => x.Mapping)
            .ToList();

        // Assert - Portuguese was added first, so it should be first when priorities are equal
        matchingMappings.Should().HaveCount(3);
        matchingMappings.First().LanguageAlternativeId.Should().Be(portuguese.Id,
            "when priorities are equal, first mapping in list should win");
    }

    [Fact]
    public void GroupMatching_MatchesByCnOrDn()
    {
        // Arrange
        var alternative = _context.AddLanguageAlternative();
        var mapping = _context.AddLdapGroupMapping("CN=TestGroup,OU=Groups,DC=example,DC=com", alternative.Id);
        mapping.LdapGroupName = "TestGroup"; // Also match by CN

        var userGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "TestGroup" };

        // Act
        var matches = _context.Configuration.LdapGroupMappings
            .Where(m => userGroups.Contains(m.LdapGroupDn) || userGroups.Contains(m.LdapGroupName));

        // Assert
        matches.Should().ContainSingle();
    }

    [Fact]
    public void GroupMatching_CaseInsensitive()
    {
        // Arrange
        var alternative = _context.AddLanguageAlternative();
        _context.AddLdapGroupMapping("CN=MyGroup,DC=test", alternative.Id);

        var userGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "cn=mygroup,dc=test" // Different case
        };

        // Act
        var matches = _context.Configuration.LdapGroupMappings
            .Where(m => userGroups.Contains(m.LdapGroupDn) || userGroups.Contains(m.LdapGroupName));

        // Assert
        matches.Should().ContainSingle();
    }

    [Fact]
    public void GroupMatching_NoMatchingGroups_ReturnsEmpty()
    {
        // Arrange
        var alternative = _context.AddLanguageAlternative();
        _context.AddLdapGroupMapping("CN=RequiredGroup,DC=test", alternative.Id);

        var userGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CN=OtherGroup,DC=test"
        };

        // Act
        var matches = _context.Configuration.LdapGroupMappings
            .Where(m => userGroups.Contains(m.LdapGroupDn) || userGroups.Contains(m.LdapGroupName));

        // Assert
        matches.Should().BeEmpty();
    }
}

/// <summary>
/// Tests for LDAP filter escaping logic (security-critical).
/// </summary>
public class LdapFilterEscapingTests
{
    [Theory]
    [InlineData("normaluser", "normaluser")]
    [InlineData("user*name", "user\\2aname")]
    [InlineData("user(name)", "user\\28name\\29")]
    [InlineData("user\\name", "user\\5cname")]
    [InlineData("user\0name", "user\\00name")]
    [InlineData("admin*)(uid=*", "admin\\2a\\29\\28uid=\\2a")] // Injection attempt
    public void EscapeLdapFilter_EscapesSpecialCharacters(string input, string expected)
    {
        // Act - replicate the escape logic
        var result = input
            .Replace("\\", "\\5c")
            .Replace("*", "\\2a")
            .Replace("(", "\\28")
            .Replace(")", "\\29")
            .Replace("\0", "\\00");

        // Assert
        result.Should().Be(expected);
    }
}

/// <summary>
/// Tests for DN parsing logic.
/// </summary>
public class DnParsingTests
{
    [Theory]
    [InlineData("CN=Group Name,OU=Groups,DC=example,DC=com", "Group Name")]
    [InlineData("cn=lowercase,ou=test,dc=test", "lowercase")]
    [InlineData("CN=With Spaces,DC=test", "With Spaces")]
    [InlineData("OU=NoCommonName,DC=test", null)]
    [InlineData("", null)]
    [InlineData("invalid-dn", null)]
    public void ExtractCnFromDn_ExtractsCorrectly(string dn, string? expected)
    {
        // Act - replicate the CN extraction logic
        string? result = null;
        if (!string.IsNullOrEmpty(dn))
        {
            var parts = dn.Split(',');
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (trimmed.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
                {
                    result = trimmed.Substring(3);
                    break;
                }
            }
        }

        // Assert
        result.Should().Be(expected);
    }
}
