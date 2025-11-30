using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Entities;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Polyglot.Configuration;
using Jellyfin.Plugin.Polyglot.Models;
using Jellyfin.Plugin.Polyglot.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.Polyglot.Tests.TestHelpers;

/// <summary>
/// Factory for creating mock objects used in tests.
/// </summary>
public static class MockFactory
{
    /// <summary>
    /// Creates a User entity for testing.
    /// </summary>
    public static User CreateUser(Guid? id = null, string username = "testuser")
    {
        return new User(username, "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider", "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider")
        {
            Id = id ?? Guid.NewGuid()
        };
    }

    /// <summary>
    /// Creates a mock ILibraryManager with basic setup.
    /// </summary>
    public static Mock<ILibraryManager> CreateLibraryManager(List<VirtualFolderInfo>? virtualFolders = null)
    {
        var mock = new Mock<ILibraryManager>();

        virtualFolders ??= new List<VirtualFolderInfo>();

        mock.Setup(m => m.GetVirtualFolders()).Returns(virtualFolders);

        return mock;
    }

    /// <summary>
    /// Creates a mock IUserManager with basic setup.
    /// </summary>
    public static Mock<IUserManager> CreateUserManager(List<User>? users = null)
    {
        var mock = new Mock<IUserManager>();

        users ??= new List<User>();

        mock.Setup(m => m.Users).Returns(users.AsQueryable());

        foreach (var user in users)
        {
            mock.Setup(m => m.GetUserById(user.Id)).Returns(user);
        }

        return mock;
    }

    /// <summary>
    /// Creates a mock ILogger.
    /// </summary>
    public static Mock<ILogger<T>> CreateLogger<T>()
    {
        return new Mock<ILogger<T>>();
    }

    /// <summary>
    /// Creates a mock IMirrorService.
    /// </summary>
    public static Mock<IMirrorService> CreateMirrorService()
    {
        var mock = new Mock<IMirrorService>();
        mock.Setup(m => m.ValidateMirrorConfiguration(It.IsAny<Guid>(), It.IsAny<string>()))
            .Returns((true, null));
        mock.Setup(m => m.CleanupOrphanedMirrorsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrphanCleanupResult());
        return mock;
    }

    /// <summary>
    /// Creates a mock IUserLanguageService.
    /// </summary>
    public static Mock<IUserLanguageService> CreateUserLanguageService()
    {
        return new Mock<IUserLanguageService>();
    }

    /// <summary>
    /// Creates a mock ILibraryAccessService.
    /// </summary>
    public static Mock<ILibraryAccessService> CreateLibraryAccessService()
    {
        return new Mock<ILibraryAccessService>();
    }

    /// <summary>
    /// Creates a mock ILdapIntegrationService.
    /// </summary>
    public static Mock<ILdapIntegrationService> CreateLdapIntegrationService(bool isAvailable = false)
    {
        var mock = new Mock<ILdapIntegrationService>();
        mock.Setup(m => m.IsLdapPluginAvailable()).Returns(isAvailable);
        mock.Setup(m => m.GetLdapStatus()).Returns(new LdapStatus
        {
            IsPluginInstalled = isAvailable,
            IsConfigured = isAvailable,
            IsIntegrationEnabled = isAvailable
        });
        return mock;
    }

    /// <summary>
    /// Creates a mock IConfigurationService with optional initial configuration.
    /// Returns deep copies from Get* methods to match real ConfigurationService behavior.
    /// </summary>
    public static Mock<IConfigurationService> CreateConfigurationService(PluginConfiguration? config = null)
    {
        var mock = new Mock<IConfigurationService>();
        config ??= CreatePluginConfiguration();

        // Setup GetConfiguration - returns live reference (matches real behavior for scalar properties)
        mock.Setup(m => m.GetConfiguration()).Returns(config);

        // Setup GetAlternatives - returns deep copies to match real service
        mock.Setup(m => m.GetAlternatives())
            .Returns(() => config.LanguageAlternatives.Select(a => a.DeepClone()).ToList());

        // Setup GetAlternative - returns deep copy to match real service
        mock.Setup(m => m.GetAlternative(It.IsAny<Guid>()))
            .Returns((Guid id) => config.LanguageAlternatives.FirstOrDefault(a => a.Id == id)?.DeepClone());

        // Setup GetMirror - returns deep copy to match real service
        mock.Setup(m => m.GetMirror(It.IsAny<Guid>()))
            .Returns((Guid id) => config.LanguageAlternatives
                .SelectMany(a => a.MirroredLibraries)
                .FirstOrDefault(m => m.Id == id)?.DeepClone());

        // Setup GetMirrorWithAlternative - returns deep copy to match real service
        mock.Setup(m => m.GetMirrorWithAlternative(It.IsAny<Guid>()))
            .Returns((Guid id) =>
            {
                foreach (var alt in config.LanguageAlternatives)
                {
                    var mirror = alt.MirroredLibraries.FirstOrDefault(m => m.Id == id);
                    if (mirror != null)
                    {
                        // Return deep copy to prevent test code from modifying config state
                        return (mirror.DeepClone(), alt.Id);
                    }
                }
                return null;
            });

        // Setup GetUserLanguages - returns deep copies to match real service
        mock.Setup(m => m.GetUserLanguages())
            .Returns(() => config.UserLanguages.Select(u => u.DeepClone()).ToList());

        // Setup GetUserLanguage - returns deep copy to match real service
        mock.Setup(m => m.GetUserLanguage(It.IsAny<Guid>()))
            .Returns((Guid id) => config.UserLanguages.FirstOrDefault(u => u.UserId == id)?.DeepClone());

        // Setup GetLdapGroupMappings - returns deep copies to match real service
        mock.Setup(m => m.GetLdapGroupMappings())
            .Returns(() => config.LdapGroupMappings.Select(m => m.DeepClone()).ToList());

        // Setup mutation methods to actually modify the config
        mock.Setup(m => m.UpdateMirror(It.IsAny<Guid>(), It.IsAny<Action<LibraryMirror>>()))
            .Returns((Guid id, Action<LibraryMirror> action) =>
            {
                var mirror = config.LanguageAlternatives.SelectMany(a => a.MirroredLibraries).FirstOrDefault(m => m.Id == id);
                if (mirror != null)
                {
                    action(mirror);
                    return true;
                }
                return false;
            });

        mock.Setup(m => m.UpdateAlternative(It.IsAny<Guid>(), It.IsAny<Action<LanguageAlternative>>()))
            .Returns((Guid id, Action<LanguageAlternative> action) =>
            {
                var alt = config.LanguageAlternatives.FirstOrDefault(a => a.Id == id);
                if (alt != null)
                {
                    action(alt);
                    return true;
                }
                return false;
            });

        mock.Setup(m => m.UpdateUserLanguage(It.IsAny<Guid>(), It.IsAny<Action<UserLanguageConfig>>()))
            .Returns((Guid id, Action<UserLanguageConfig> action) =>
            {
                var userConfig = config.UserLanguages.FirstOrDefault(u => u.UserId == id);
                if (userConfig != null)
                {
                    action(userConfig);
                    return true;
                }
                return false;
            });

        mock.Setup(m => m.UpdateOrCreateUserLanguage(It.IsAny<Guid>(), It.IsAny<Action<UserLanguageConfig>>()))
            .Returns((Guid userId, Action<UserLanguageConfig> action) =>
            {
                var userConfig = config.UserLanguages.FirstOrDefault(u => u.UserId == userId);
                bool isNew = userConfig == null;
                if (isNew)
                {
                    userConfig = new UserLanguageConfig { UserId = userId };
                    config.UserLanguages.Add(userConfig);
                }
                action(userConfig);
                return isNew;
            });

        mock.Setup(m => m.AddMirror(It.IsAny<Guid>(), It.IsAny<LibraryMirror>()))
            .Returns((Guid altId, LibraryMirror mirror) =>
            {
                var alt = config.LanguageAlternatives.FirstOrDefault(a => a.Id == altId);
                if (alt == null)
                {
                    return false;
                }

                // Atomic duplicate source library check to match real implementation
                if (alt.MirroredLibraries.Any(m => m.SourceLibraryId == mirror.SourceLibraryId))
                {
                    return false;
                }

                alt.MirroredLibraries.Add(mirror);
                return true;
            });

        mock.Setup(m => m.RemoveMirror(It.IsAny<Guid>()))
            .Returns((Guid id) =>
            {
                foreach (var alt in config.LanguageAlternatives)
                {
                    var mirror = alt.MirroredLibraries.FirstOrDefault(m => m.Id == id);
                    if (mirror != null)
                    {
                        alt.MirroredLibraries.Remove(mirror);
                        return true;
                    }
                }
                return false;
            });

        mock.Setup(m => m.RemoveAlternative(It.IsAny<Guid>()))
            .Returns((Guid id) =>
            {
                var alt = config.LanguageAlternatives.FirstOrDefault(a => a.Id == id);
                if (alt != null)
                {
                    config.LanguageAlternatives.Remove(alt);

                    // Clear DefaultLanguageAlternativeId if it references the deleted alternative
                    if (config.DefaultLanguageAlternativeId == id)
                    {
                        config.DefaultLanguageAlternativeId = null;
                    }

                    // Remove LDAP group mappings that reference this alternative
                    config.LdapGroupMappings.RemoveAll(m => m.LanguageAlternativeId == id);

                    return true;
                }
                return false;
            });

        mock.Setup(m => m.TryRemoveAlternativeAtomic(It.IsAny<Guid>(), It.IsAny<IReadOnlySet<Guid>>()))
            .Returns((Guid id, IReadOnlySet<Guid> expectedMirrorIds) =>
            {
                var alt = config.LanguageAlternatives.FirstOrDefault(a => a.Id == id);
                if (alt == null)
                {
                    return RemoveAlternativeResult.NotFound();
                }

                // Check for unexpected mirrors
                var currentMirrorIds = alt.MirroredLibraries.Select(m => m.Id).ToHashSet();
                var unexpectedMirrorIds = currentMirrorIds.Except(expectedMirrorIds).ToList();
                if (unexpectedMirrorIds.Count > 0)
                {
                    return RemoveAlternativeResult.NewMirrorsFound(unexpectedMirrorIds);
                }

                config.LanguageAlternatives.Remove(alt);

                // Clear DefaultLanguageAlternativeId if it references the deleted alternative
                if (config.DefaultLanguageAlternativeId == id)
                {
                    config.DefaultLanguageAlternativeId = null;
                }

                // Remove LDAP group mappings that reference this alternative
                config.LdapGroupMappings.RemoveAll(m => m.LanguageAlternativeId == id);

                return RemoveAlternativeResult.Succeeded();
            });

        mock.Setup(m => m.RemoveUserLanguage(It.IsAny<Guid>()))
            .Returns((Guid id) =>
            {
                var userConfig = config.UserLanguages.FirstOrDefault(u => u.UserId == id);
                if (userConfig != null)
                {
                    config.UserLanguages.Remove(userConfig);
                    return true;
                }
                return false;
            });

        mock.Setup(m => m.RemoveLdapGroupMapping(It.IsAny<Guid>()))
            .Returns((Guid id) =>
            {
                var mapping = config.LdapGroupMappings.FirstOrDefault(m => m.Id == id);
                if (mapping != null)
                {
                    config.LdapGroupMappings.Remove(mapping);
                    return true;
                }
                return false;
            });

        mock.Setup(m => m.AddAlternative(It.IsAny<LanguageAlternative>()))
            .Returns((LanguageAlternative alt) =>
            {
                // Atomic duplicate name check to match real implementation
                if (config.LanguageAlternatives.Any(a => string.Equals(a.Name, alt.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
                config.LanguageAlternatives.Add(alt);
                return true;
            });

        mock.Setup(m => m.AddLdapGroupMapping(It.IsAny<LdapGroupMapping>()))
            .Returns((LdapGroupMapping mapping) =>
            {
                // Atomic duplicate check to match real implementation
                if (config.LdapGroupMappings.Any(m => string.Equals(m.LdapGroupDn, mapping.LdapGroupDn, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
                config.LdapGroupMappings.Add(mapping);
                return true;
            });

        // Thread-safe collection getters
        mock.Setup(m => m.GetExcludedExtensions())
            .Returns(() => config.ExcludedExtensions?.ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        mock.Setup(m => m.GetExcludedDirectories())
            .Returns(() => config.ExcludedDirectories?.ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        mock.Setup(m => m.GetDefaultExcludedExtensions())
            .Returns(() => config.DefaultExcludedExtensions?.ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        mock.Setup(m => m.GetDefaultExcludedDirectories())
            .Returns(() => config.DefaultExcludedDirectories?.ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        // UpdateSettings with Action (always saves)
        mock.Setup(m => m.UpdateSettings(It.IsAny<Action<PluginConfiguration>>()))
            .Callback((Action<PluginConfiguration> action) => action(config));

        // UpdateSettings with Func<,bool> (saves only if function returns true)
        mock.Setup(m => m.UpdateSettings(It.IsAny<Func<PluginConfiguration, bool>>()))
            .Returns((Func<PluginConfiguration, bool> func) => func(config));

        mock.Setup(m => m.ClearAllConfiguration())
            .Callback(() =>
            {
                config.LanguageAlternatives.Clear();
                config.UserLanguages.Clear();
                config.LdapGroupMappings.Clear();
            });

        return mock;
    }

    /// <summary>
    /// Creates a sample VirtualFolderInfo.
    /// </summary>
    public static VirtualFolderInfo CreateVirtualFolder(
        Guid? id = null,
        string name = "Movies",
        string? collectionType = "movies",
        string[]? locations = null)
    {
        return new VirtualFolderInfo
        {
            ItemId = (id ?? Guid.NewGuid()).ToString(),
            Name = name,
            CollectionType = collectionType != null ? Enum.Parse<CollectionTypeOptions>(collectionType, true) : null,
            Locations = locations ?? new[] { "/media/movies" },
            LibraryOptions = new MediaBrowser.Model.Configuration.LibraryOptions
            {
                PreferredMetadataLanguage = "en",
                MetadataCountryCode = "US"
            }
        };
    }

    /// <summary>
    /// Creates a sample LanguageAlternative.
    /// </summary>
    public static LanguageAlternative CreateLanguageAlternative(
        Guid? id = null,
        string name = "Portuguese",
        string languageCode = "pt-BR",
        string destinationBasePath = "/media/portuguese")
    {
        return new LanguageAlternative
        {
            Id = id ?? Guid.NewGuid(),
            Name = name,
            LanguageCode = languageCode,
            MetadataLanguage = languageCode.Split('-')[0],
            MetadataCountry = languageCode.Contains('-') ? languageCode.Split('-')[1] : string.Empty,
            DestinationBasePath = destinationBasePath,
            MirroredLibraries = new List<LibraryMirror>(),
            CreatedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Creates a sample LibraryMirror.
    /// </summary>
    public static LibraryMirror CreateLibraryMirror(
        Guid? id = null,
        Guid? sourceLibraryId = null,
        string sourceLibraryName = "Movies",
        Guid? targetLibraryId = null,
        string targetLibraryName = "Filmes",
        string targetPath = "/media/portuguese/movies",
        SyncStatus status = SyncStatus.Pending)
    {
        return new LibraryMirror
        {
            Id = id ?? Guid.NewGuid(),
            SourceLibraryId = sourceLibraryId ?? Guid.NewGuid(),
            SourceLibraryName = sourceLibraryName,
            TargetLibraryId = targetLibraryId,
            TargetLibraryName = targetLibraryName,
            TargetPath = targetPath,
            CollectionType = "movies",
            Status = status
        };
    }

    /// <summary>
    /// Creates a sample UserLanguageConfig.
    /// </summary>
    public static UserLanguageConfig CreateUserLanguageConfig(
        Guid? userId = null,
        Guid? alternativeId = null,
        bool manuallySet = false,
        string setBy = "admin")
    {
        return new UserLanguageConfig
        {
            UserId = userId ?? Guid.NewGuid(),
            SelectedAlternativeId = alternativeId,
            ManuallySet = manuallySet,
            SetAt = DateTime.UtcNow,
            SetBy = setBy
        };
    }

    /// <summary>
    /// Creates a sample LdapGroupMapping.
    /// </summary>
    public static LdapGroupMapping CreateLdapGroupMapping(
        Guid? id = null,
        string ldapGroupDn = "CN=Portuguese Users,OU=Groups,DC=example,DC=com",
        string ldapGroupName = "Portuguese Users",
        Guid? languageAlternativeId = null,
        int priority = 100)
    {
        return new LdapGroupMapping
        {
            Id = id ?? Guid.NewGuid(),
            LdapGroupDn = ldapGroupDn,
            LdapGroupName = ldapGroupName,
            LanguageAlternativeId = languageAlternativeId ?? Guid.NewGuid(),
            Priority = priority
        };
    }

    /// <summary>
    /// Creates a sample PluginConfiguration.
    /// </summary>
    public static PluginConfiguration CreatePluginConfiguration()
    {
        return new PluginConfiguration
        {
            EnableLdapIntegration = false,
            LanguageAlternatives = new List<LanguageAlternative>(),
            UserLanguages = new List<UserLanguageConfig>(),
            LdapGroupMappings = new List<LdapGroupMapping>()
        };
    }
}

