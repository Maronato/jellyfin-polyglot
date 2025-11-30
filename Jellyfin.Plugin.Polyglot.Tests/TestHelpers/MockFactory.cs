using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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
    /// Helper method to clone config using JSON serialization (matches real ConfigurationService).
    /// </summary>
    private static PluginConfiguration Clone(PluginConfiguration config)
    {
        var json = JsonSerializer.Serialize(config);
        return JsonSerializer.Deserialize<PluginConfiguration>(json)!;
    }

    /// <summary>
    /// Creates a mock IConfigurationService with the new generic Read/Update pattern.
    /// Uses JSON cloning to match real ConfigurationService behavior.
    /// </summary>
    public static Mock<IConfigurationService> CreateConfigurationService(PluginConfiguration? config = null)
    {
        var mock = new Mock<IConfigurationService>();
        config ??= CreatePluginConfiguration();

        // Setup Read<T> - returns result of selector on a JSON-cloned snapshot
        // Use reflection to properly invoke the delegate since we can't cast Func<,ValueType> to Func<,object>
        mock.Setup(m => m.Read(It.IsAny<Func<PluginConfiguration, It.IsAnyType>>()))
            .Returns((Delegate selector) =>
            {
                var snapshot = Clone(config);
                return selector.DynamicInvoke(snapshot);
            });

        // Setup Update(Action) - always executes and saves
        mock.Setup(m => m.Update(It.IsAny<Action<PluginConfiguration>>()))
            .Callback((Action<PluginConfiguration> mutation) =>
            {
                var snapshot = Clone(config);
                mutation(snapshot);
                CopyConfigTo(snapshot, config);
            });

        // Setup Update(Func<bool>) - executes mutation, saves only if returns true
        mock.Setup(m => m.Update(It.IsAny<Func<PluginConfiguration, bool>>()))
            .Returns((Func<PluginConfiguration, bool> mutation) =>
            {
                var snapshot = Clone(config);
                if (!mutation(snapshot))
                {
                    return false;
                }
                CopyConfigTo(snapshot, config);
                return true;
            });

        return mock;
    }

    /// <summary>
    /// Copies all configuration properties from source to destination.
    /// </summary>
    private static void CopyConfigTo(PluginConfiguration source, PluginConfiguration dest)
    {
        dest.AutoManageNewUsers = source.AutoManageNewUsers;
        dest.DefaultLanguageAlternativeId = source.DefaultLanguageAlternativeId;
        dest.SyncMirrorsAfterLibraryScan = source.SyncMirrorsAfterLibraryScan;
        dest.UserReconciliationTime = source.UserReconciliationTime;
        dest.ExcludedExtensions = source.ExcludedExtensions;
        dest.ExcludedDirectories = source.ExcludedDirectories;
        dest.LanguageAlternatives = source.LanguageAlternatives;
        dest.UserLanguages = source.UserLanguages;
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
    /// Creates a sample PluginConfiguration.
    /// </summary>
    public static PluginConfiguration CreatePluginConfiguration()
    {
        return new PluginConfiguration
        {
            LanguageAlternatives = new List<LanguageAlternative>(),
            UserLanguages = new List<UserLanguageConfig>()
        };
    }
}
