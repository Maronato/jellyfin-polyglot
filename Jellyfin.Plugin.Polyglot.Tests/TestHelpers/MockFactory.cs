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
public static class TestMockFactory
{
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
        string username = "testuser",
        Guid? alternativeId = null,
        bool manuallySet = false,
        string setBy = "admin")
    {
        return new UserLanguageConfig
        {
            UserId = userId ?? Guid.NewGuid(),
            Username = username,
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
            SyncUserDisplayLanguage = true,
            SyncUserSubtitleLanguage = true,
            SyncUserAudioLanguage = true,
            EnableLdapIntegration = false,
            LanguageAlternatives = new List<LanguageAlternative>(),
            UserLanguages = new List<UserLanguageConfig>(),
            LdapGroupMappings = new List<LdapGroupMapping>()
        };
    }
}

