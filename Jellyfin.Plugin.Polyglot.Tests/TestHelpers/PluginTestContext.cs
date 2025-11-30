using System.Text.Json;
using Jellyfin.Plugin.Polyglot.Configuration;
using Jellyfin.Plugin.Polyglot.Models;
using Jellyfin.Plugin.Polyglot.Services;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
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
    /// Gets the mock application host used when constructing the plugin.
    /// Useful for configuring service resolution during tests.
    /// </summary>
    public Mock<IApplicationHost> ApplicationHostMock { get; }

    /// <summary>
    /// Gets the mock mirror service resolved via the application host.
    /// Useful for verifying interactions such as uninstall cleanup.
    /// </summary>
    public Mock<IMirrorService> MirrorServiceMock { get; }

    /// <summary>
    /// Gets the mock configuration service resolved via the application host.
    /// </summary>
    public Mock<IConfigurationService> ConfigurationServiceMock { get; }

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

        // Create the shared configuration - this will be updated when SaveConfiguration is called
        var currentConfig = new PluginConfiguration();
        
        var xmlSerializerMock = new Mock<IXmlSerializer>();
        // DeserializeFromFile returns the current config (updated by SerializeToFile)
        xmlSerializerMock.Setup(s => s.DeserializeFromFile(It.IsAny<Type>(), It.IsAny<string>()))
            .Returns(() => currentConfig);
        // SerializeToFile captures the saved config so subsequent deserializations return it
        xmlSerializerMock.Setup(s => s.SerializeToFile(It.IsAny<object>(), It.IsAny<string>()))
            .Callback<object, string>((config, path) => 
            {
                if (config is PluginConfiguration pluginConfig)
                {
                    currentConfig = pluginConfig;
                }
            });

        // Set up application host with service resolution
        ApplicationHostMock = new Mock<IApplicationHost>();
        MirrorServiceMock = new Mock<IMirrorService>();
        
        // Create ConfigurationService mock that delegates to the live plugin config
        // Note: We'll update this after plugin is created to use the actual Configuration
        ConfigurationServiceMock = new Mock<IConfigurationService>();
        
        // Wire up Resolve<IMirrorService>() to return our mock
        ApplicationHostMock
            .Setup(h => h.Resolve<IMirrorService>())
            .Returns(MirrorServiceMock.Object);
        
        // Wire up Resolve<IConfigurationService>() to return our mock
        ApplicationHostMock
            .Setup(h => h.Resolve<IConfigurationService>())
            .Returns(ConfigurationServiceMock.Object);

        PluginLoggerMock = new Mock<ILogger<Plugin>>();

        _plugin = new Plugin(
            applicationPathsMock.Object,
            xmlSerializerMock.Object,
            ApplicationHostMock.Object,
            PluginLoggerMock.Object);
        
        // Verify Plugin.Instance is set correctly
        if (!ReferenceEquals(Plugin.Instance, _plugin))
        {
            throw new InvalidOperationException("Plugin.Instance was not set correctly");
        }

        // Set up ConfigurationServiceMock to use the live plugin Configuration
        // This is needed for tests that use the mock config service alongside PluginTestContext
        SetupConfigurationServiceMock();
    }

    /// <summary>
    /// Creates a deep clone of the configuration using JSON serialization.
    /// Matches the behavior of the real ConfigurationService.
    /// </summary>
    private static PluginConfiguration CloneConfig(PluginConfiguration config)
    {
        var json = JsonSerializer.Serialize(config);
        return JsonSerializer.Deserialize<PluginConfiguration>(json)!;
    }

    private void SetupConfigurationServiceMock()
    {
        // Setup Read to return from a cloned snapshot of plugin configuration
        // This matches the real ConfigurationService behavior (lines 44-46)
        // Use DynamicInvoke to handle value types (bool, int, etc.)
        ConfigurationServiceMock.Setup(m => m.Read(It.IsAny<Func<PluginConfiguration, It.IsAnyType>>()))
            .Returns((Delegate selector) =>
            {
                var snapshot = CloneConfig(_plugin.Configuration);
                return selector.DynamicInvoke(snapshot);
            });

        // Setup Update(Action) to apply mutations to a cloned config
        ConfigurationServiceMock.Setup(m => m.Update(It.IsAny<Action<PluginConfiguration>>()))
            .Callback((Action<PluginConfiguration> mutation) =>
            {
                // Clone before mutation (matches real service line 65)
                var snapshot = CloneConfig(_plugin.Configuration);
                mutation(snapshot);
                // Clone again to break references from objects added during mutation (matches line 78)
                var toSave = CloneConfig(snapshot);
                _plugin.UpdateConfiguration(toSave);
            });

        // Setup Update(Func<bool>) to apply mutations conditionally
        ConfigurationServiceMock.Setup(m => m.Update(It.IsAny<Func<PluginConfiguration, bool>>()))
            .Returns((Func<PluginConfiguration, bool> mutation) =>
            {
                // Clone before mutation (matches real service line 65)
                var snapshot = CloneConfig(_plugin.Configuration);
                if (!mutation(snapshot))
                {
                    return false;
                }
                // Clone again to break references from objects added during mutation (matches line 78)
                var toSave = CloneConfig(snapshot);
                _plugin.UpdateConfiguration(toSave);
                return true;
            });
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

    /// <summary>
    /// Adds a library mirror to an alternative by ID.
    /// </summary>
    public LibraryMirror AddMirrorToAlternative(
        Guid alternativeId,
        Guid sourceLibraryId,
        string sourceLibraryName,
        Guid? targetLibraryId = null,
        string targetPath = "/media/mirror")
    {
        var alternative = Configuration.LanguageAlternatives.FirstOrDefault(a => a.Id == alternativeId);
        if (alternative == null)
        {
            throw new InvalidOperationException($"Language alternative {alternativeId} not found");
        }

        return AddMirror(alternative, sourceLibraryId, sourceLibraryName, targetLibraryId, targetPath);
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
