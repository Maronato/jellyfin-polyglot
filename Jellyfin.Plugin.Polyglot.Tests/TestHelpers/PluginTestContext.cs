using Jellyfin.Plugin.Polyglot.Configuration;
using Jellyfin.Plugin.Polyglot.Models;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.Polyglot.Tests.TestHelpers;

/// <summary>
/// Test context that sets up a real Plugin.Instance for testing.
/// This allows testing services that depend on Plugin.Instance.Configuration.
/// 
/// IMPORTANT: Tests using this context should not run in parallel with each other
/// because they share the static Plugin.Instance. Use xunit.runner.json to disable
/// parallelization or use [Collection] attributes.
/// </summary>
public class PluginTestContext : IDisposable
{
    private readonly string _tempConfigPath;
    private readonly Plugin _plugin;
    private static readonly object _lock = new();

    public PluginConfiguration Configuration => _plugin.Configuration;

    /// <summary>
    /// Gets the mock library manager used when constructing the plugin.
    /// Useful for verifying interactions such as uninstall cleanup.
    /// </summary>
    public Mock<ILibraryManager> LibraryManagerMock { get; }

    /// <summary>
    /// Gets the mock logger used when constructing the plugin.
    /// </summary>
    public Mock<ILogger<Plugin>> PluginLoggerMock { get; }
    
    /// <summary>
    /// Gets the Plugin instance created by this context.
    /// Should be the same as Plugin.Instance after construction.
    /// </summary>
    public Plugin PluginInstance => _plugin;

    public PluginTestContext()
    {
        // Lock to prevent race conditions with Plugin.Instance during parallel test execution
        Monitor.Enter(_lock);
        
        _tempConfigPath = Path.Combine(Path.GetTempPath(), "polyglot_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempConfigPath);

        var applicationPathsMock = new Mock<IApplicationPaths>();
        applicationPathsMock.Setup(p => p.PluginConfigurationsPath).Returns(_tempConfigPath);
        applicationPathsMock.Setup(p => p.PluginsPath).Returns(_tempConfigPath);
        applicationPathsMock.Setup(p => p.DataPath).Returns(_tempConfigPath);

        var xmlSerializerMock = new Mock<IXmlSerializer>();
        xmlSerializerMock.Setup(s => s.DeserializeFromFile(It.IsAny<Type>(), It.IsAny<string>()))
            .Returns(new PluginConfiguration());
        xmlSerializerMock.Setup(s => s.SerializeToFile(It.IsAny<object>(), It.IsAny<string>()));

        LibraryManagerMock = new Mock<ILibraryManager>();
        PluginLoggerMock = new Mock<ILogger<Plugin>>();

        _plugin = new Plugin(
            applicationPathsMock.Object,
            xmlSerializerMock.Object,
            LibraryManagerMock.Object,
            PluginLoggerMock.Object);
        
        // Verify Plugin.Instance is set correctly
        if (!ReferenceEquals(Plugin.Instance, _plugin))
        {
            throw new InvalidOperationException("Plugin.Instance was not set correctly");
        }
    }

    /// <summary>
    /// Adds a language alternative to the configuration.
    /// </summary>
    public LanguageAlternative AddLanguageAlternative(
        string name = "Portuguese",
        string languageCode = "pt-BR",
        string destinationBasePath = "/media/portuguese")
    {
        var alternative = new LanguageAlternative
        {
            Id = Guid.NewGuid(),
            Name = name,
            LanguageCode = languageCode,
            MetadataLanguage = languageCode.Split('-')[0],
            MetadataCountry = languageCode.Contains('-') ? languageCode.Split('-')[1] : string.Empty,
            DestinationBasePath = destinationBasePath,
            CreatedAt = DateTime.UtcNow
        };
        Configuration.LanguageAlternatives.Add(alternative);
        return alternative;
    }

    /// <summary>
    /// Adds a user language assignment to the configuration.
    /// </summary>
    public UserLanguageConfig AddUserLanguage(
        Guid userId,
        Guid? alternativeId,
        bool manuallySet = false,
        string setBy = "admin",
        bool isPluginManaged = true) // Default to managed
    {
        // Remove existing config for user if present
        Configuration.UserLanguages.RemoveAll(u => u.UserId == userId);

        var userConfig = new UserLanguageConfig
        {
            UserId = userId,
            Username = $"user_{userId.ToString()[..8]}",
            SelectedAlternativeId = alternativeId,
            ManuallySet = manuallySet,
            SetAt = DateTime.UtcNow,
            SetBy = setBy,
            IsPluginManaged = isPluginManaged
        };
        Configuration.UserLanguages.Add(userConfig);
        return userConfig;
    }

    /// <summary>
    /// Adds an LDAP group mapping to the configuration.
    /// </summary>
    public LdapGroupMapping AddLdapGroupMapping(
        string groupDn,
        Guid languageAlternativeId,
        int priority = 100)
    {
        var mapping = new LdapGroupMapping
        {
            Id = Guid.NewGuid(),
            LdapGroupDn = groupDn,
            LdapGroupName = groupDn,
            LanguageAlternativeId = languageAlternativeId,
            Priority = priority
        };
        Configuration.LdapGroupMappings.Add(mapping);
        return mapping;
    }

    /// <summary>
    /// Adds a library mirror to an alternative.
    /// </summary>
    public LibraryMirror AddMirror(
        LanguageAlternative alternative,
        Guid sourceLibraryId,
        string sourceLibraryName,
        Guid? targetLibraryId = null,
        string targetPath = "/media/portuguese/movies")
    {
        var mirror = new LibraryMirror
        {
            Id = Guid.NewGuid(),
            SourceLibraryId = sourceLibraryId,
            SourceLibraryName = sourceLibraryName,
            TargetLibraryId = targetLibraryId ?? Guid.NewGuid(),
            TargetLibraryName = $"{sourceLibraryName} ({alternative.Name})",
            TargetPath = targetPath,
            Status = SyncStatus.Synced
        };
        alternative.MirroredLibraries.Add(mirror);
        return mirror;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempConfigPath))
            {
                Directory.Delete(_tempConfigPath, true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }
}
