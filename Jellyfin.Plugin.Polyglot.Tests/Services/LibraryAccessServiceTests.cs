using FluentAssertions;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Polyglot.Services;
using Jellyfin.Plugin.Polyglot.Tests.TestHelpers;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Polyglot.Tests.Services;

/// <summary>
/// Tests for LibraryAccessService focusing on the library access calculation algorithm.
/// The key behavior: Given a user's language assignment, which libraries should they see?
/// </summary>
public class LibraryAccessServiceTests : IDisposable
{
    private readonly PluginTestContext _context;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly LibraryAccessService _service;

    public LibraryAccessServiceTests()
    {
        _context = new PluginTestContext();
        _userManagerMock = new Mock<IUserManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        var logger = new Mock<ILogger<LibraryAccessService>>();

        _service = new LibraryAccessService(
            _userManagerMock.Object,
            _libraryManagerMock.Object,
            logger.Object);
    }

    public void Dispose() => _context.Dispose();

    #region GetExpectedLibraryAccess - Core algorithm tests

    [Fact]
    public void GetExpectedLibraryAccess_UserWithNoAssignment_ReturnsEmpty()
    {
        // Arrange
        var userId = Guid.NewGuid();
        // No language assignment for user

        // Act
        var result = _service.GetExpectedLibraryAccess(userId).ToList();

        // Assert - No assignment means no restrictions (returns empty, handled by caller)
        result.Should().BeEmpty();
    }

    [Fact]
    public void GetExpectedLibraryAccess_UserWithAssignment_GetsMirrorLibraries()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var sourceLibraryId = Guid.NewGuid();
        var targetLibraryId = Guid.NewGuid();

        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        _context.AddMirror(alternative, sourceLibraryId, "Movies", targetLibraryId);
        _context.AddUserLanguage(userId, alternative.Id);

        // Setup library manager to return the libraries
        var libraries = new List<VirtualFolderInfo>
        {
            CreateVirtualFolder(sourceLibraryId, "Movies"),
            CreateVirtualFolder(targetLibraryId, "Filmes (Portuguese)")
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns(libraries);

        // Act
        var result = _service.GetExpectedLibraryAccess(userId).ToList();

        // Assert - User should see the mirror, not the source
        result.Should().Contain(targetLibraryId, "user should see their language's mirror");
        result.Should().NotContain(sourceLibraryId, "user should NOT see the source library");
    }

    [Fact]
    public void GetExpectedLibraryAccess_UnmanagedLibraries_AreNotReturned()
    {
        // Arrange
        // The new design: GetExpectedLibraryAccess only returns MANAGED libraries.
        // Unmanaged libraries (like "Music" with no mirrors) are preserved separately
        // by UpdateUserLibraryAccessAsync based on user's current access.
        var userId = Guid.NewGuid();
        var moviesId = Guid.NewGuid();       // Source with mirror - MANAGED
        var musicId = Guid.NewGuid();         // No mirror - NOT MANAGED
        var moviesMirrorId = Guid.NewGuid();  // Mirror - MANAGED

        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        _context.AddMirror(alternative, moviesId, "Movies", moviesMirrorId);
        _context.AddUserLanguage(userId, alternative.Id);

        var libraries = new List<VirtualFolderInfo>
        {
            CreateVirtualFolder(moviesId, "Movies"),
            CreateVirtualFolder(musicId, "Music"),
            CreateVirtualFolder(moviesMirrorId, "Filmes (Portuguese)")
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns(libraries);

        // Act
        var result = _service.GetExpectedLibraryAccess(userId).ToList();

        // Assert
        result.Should().Contain(moviesMirrorId, "user should see their movie mirror");
        result.Should().NotContain(musicId, "unmanaged libraries are NOT returned by GetExpectedLibraryAccess");
        result.Should().NotContain(moviesId, "user should NOT see source of mirrored library");
    }

    [Fact]
    public void GetExpectedLibraryAccess_OtherLanguageMirrors_AreExcluded()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var moviesId = Guid.NewGuid();
        var ptMirrorId = Guid.NewGuid();
        var esMirrorId = Guid.NewGuid();

        var portuguese = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var spanish = _context.AddLanguageAlternative("Spanish", "es-ES");

        _context.AddMirror(portuguese, moviesId, "Movies", ptMirrorId);
        _context.AddMirror(spanish, moviesId, "Movies", esMirrorId);

        _context.AddUserLanguage(userId, portuguese.Id); // User assigned to Portuguese

        var libraries = new List<VirtualFolderInfo>
        {
            CreateVirtualFolder(moviesId, "Movies"),
            CreateVirtualFolder(ptMirrorId, "Filmes (Portuguese)"),
            CreateVirtualFolder(esMirrorId, "Películas (Spanish)")
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns(libraries);

        // Act
        var result = _service.GetExpectedLibraryAccess(userId).ToList();

        // Assert
        result.Should().Contain(ptMirrorId, "user should see their Portuguese mirror");
        result.Should().NotContain(esMirrorId, "user should NOT see Spanish mirror");
        result.Should().NotContain(moviesId, "user should NOT see source library");
    }

    [Fact]
    public void GetExpectedLibraryAccess_MirrorNotYetCreated_ShowsSource()
    {
        // DESIRED BEHAVIOR: When a mirror is configured but not yet created 
        // (TargetLibraryId is null), users should see the source library.
        // "Better to have the movie with foreign metadata than no movie at all"
        
        // Arrange
        var userId = Guid.NewGuid();
        var moviesId = Guid.NewGuid();

        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var mirror = _context.AddMirror(alternative, moviesId, "Movies", targetLibraryId: null);
        mirror.TargetLibraryId = null; // Force null - mirror not created yet

        _context.AddUserLanguage(userId, alternative.Id);

        var libraries = new List<VirtualFolderInfo>
        {
            CreateVirtualFolder(moviesId, "Movies")
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns(libraries);

        // Act
        var result = _service.GetExpectedLibraryAccess(userId).ToList();

        // Assert - When mirror doesn't exist yet, source should be shown
        result.Should().Contain(moviesId, "source should be shown when mirror is not yet created");
    }

    #endregion

    #region IsPluginManaged Tests

    [Fact]
    public void GetExpectedLibraryAccess_ManagedUserOnDefault_SeesSourceLibraries()
    {
        // Arrange - User is managed but has no specific language (default libraries)
        var userId = Guid.NewGuid();
        var moviesId = Guid.NewGuid();
        var ptMirrorId = Guid.NewGuid();

        var portuguese = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        _context.AddMirror(portuguese, moviesId, "Movies", ptMirrorId);
        
        // User is managed but with no language (default)
        _context.AddUserLanguage(userId, null);

        var libraries = new List<VirtualFolderInfo>
        {
            CreateVirtualFolder(moviesId, "Movies"),
            CreateVirtualFolder(ptMirrorId, "Filmes")
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns(libraries);

        // Act
        var result = _service.GetExpectedLibraryAccess(userId).ToList();

        // Assert - Default users see source, not mirrors
        result.Should().Contain(moviesId, "default user should see source library");
        result.Should().NotContain(ptMirrorId, "default user should NOT see language mirrors");
    }

    [Fact]
    public void GetExpectedLibraryAccess_PartialMirrorSetup_ShowsSourceForUnmirrored()
    {
        // Arrange - Movies has PT and ES mirrors, Shows only has PT mirror
        // Spanish user should see: Película, Shows (not Movies, not Filmes, not Series)
        var userId = Guid.NewGuid();
        var moviesId = Guid.NewGuid();
        var showsId = Guid.NewGuid();
        var ptMoviesMirror = Guid.NewGuid();
        var esMoviesMirror = Guid.NewGuid();
        var ptShowsMirror = Guid.NewGuid();

        var portuguese = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var spanish = _context.AddLanguageAlternative("Spanish", "es-ES");

        _context.AddMirror(portuguese, moviesId, "Movies", ptMoviesMirror);
        _context.AddMirror(portuguese, showsId, "Shows", ptShowsMirror);
        _context.AddMirror(spanish, moviesId, "Movies", esMoviesMirror);
        // Note: No Spanish mirror for Shows

        _context.AddUserLanguage(userId, spanish.Id);

        var libraries = new List<VirtualFolderInfo>
        {
            CreateVirtualFolder(moviesId, "Movies"),
            CreateVirtualFolder(showsId, "Shows"),
            CreateVirtualFolder(ptMoviesMirror, "Filmes"),
            CreateVirtualFolder(ptShowsMirror, "Series"),
            CreateVirtualFolder(esMoviesMirror, "Películas")
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns(libraries);

        // Act
        var result = _service.GetExpectedLibraryAccess(userId).ToList();

        // Assert
        result.Should().Contain(esMoviesMirror, "Spanish user sees Spanish movie mirror");
        result.Should().Contain(showsId, "Spanish user sees Shows source (no Spanish mirror)");
        result.Should().NotContain(moviesId, "Spanish user does NOT see Movies source (has mirror)");
        result.Should().NotContain(ptMoviesMirror, "Spanish user does NOT see Portuguese movie mirror");
        result.Should().NotContain(ptShowsMirror, "Spanish user does NOT see Portuguese show mirror");
    }

    [Fact]
    public void GetExpectedLibraryAccess_UnmanagedLibrary_NotReturnedByMethod()
    {
        // Arrange - "Home Videos" is not part of any mirror configuration
        var userId = Guid.NewGuid();
        var moviesId = Guid.NewGuid();
        var homeVideosId = Guid.NewGuid(); // Unmanaged
        var ptMirrorId = Guid.NewGuid();

        var portuguese = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        _context.AddMirror(portuguese, moviesId, "Movies", ptMirrorId);
        _context.AddUserLanguage(userId, portuguese.Id);

        var libraries = new List<VirtualFolderInfo>
        {
            CreateVirtualFolder(moviesId, "Movies"),
            CreateVirtualFolder(homeVideosId, "Home Videos"),
            CreateVirtualFolder(ptMirrorId, "Filmes")
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns(libraries);

        // Act
        var result = _service.GetExpectedLibraryAccess(userId).ToList();

        // Assert - Home Videos should NOT be in the result (preserved separately)
        result.Should().Contain(ptMirrorId, "user sees their mirror");
        result.Should().NotContain(homeVideosId, "unmanaged library is not returned by GetExpectedLibraryAccess");
        result.Should().NotContain(moviesId, "source with mirror is excluded");
    }

    #endregion

    #region Priority 3: EnableAllFolders Edge Case

    [Fact]
    public void GetExpectedLibraryAccess_UserWithEnableAllFoldersTrue_ReturnsExpectedLibraries()
    {
        // DESIRED BEHAVIOR: Even if a user previously had EnableAllFolders=true,
        // when they become managed, they should get the correct restricted set.
        // This test verifies the calculation is correct regardless of previous state.
        
        // Arrange
        var userId = Guid.NewGuid();
        var moviesId = Guid.NewGuid();
        var musicId = Guid.NewGuid();
        var ptMirrorId = Guid.NewGuid();

        var portuguese = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        _context.AddMirror(portuguese, moviesId, "Movies", ptMirrorId);
        _context.AddUserLanguage(userId, portuguese.Id);

        var libraries = new List<VirtualFolderInfo>
        {
            CreateVirtualFolder(moviesId, "Movies"),
            CreateVirtualFolder(musicId, "Music"),
            CreateVirtualFolder(ptMirrorId, "Filmes")
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns(libraries);

        // Act
        var result = _service.GetExpectedLibraryAccess(userId).ToList();

        // Assert - Even if user HAD EnableAllFolders=true before, the calculation
        // should return the correct managed set
        result.Should().Contain(ptMirrorId, "Portuguese user should see Portuguese mirror");
        result.Should().NotContain(moviesId, "Portuguese user should NOT see source Movies");
        // Music is unmanaged, so it won't be in the managed library list
    }

    [Fact]
    public void GetExpectedLibraryAccess_MirrorDeletedFromJellyfin_FallsBackToSource()
    {
        // DESIRED BEHAVIOR: If a mirror was deleted from Jellyfin but config still references it,
        // users should see the SOURCE library as a fallback.
        // "Better to have the movie with foreign metadata than no movie at all"
        
        // Arrange
        var userId = Guid.NewGuid();
        var moviesId = Guid.NewGuid();
        var deletedMirrorId = Guid.NewGuid(); // This mirror was deleted from Jellyfin

        var portuguese = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        _context.AddMirror(portuguese, moviesId, "Movies", deletedMirrorId);
        _context.AddUserLanguage(userId, portuguese.Id);

        // Only Movies (source) exists in Jellyfin - the mirror was deleted
        var libraries = new List<VirtualFolderInfo>
        {
            CreateVirtualFolder(moviesId, "Movies")
            // deletedMirrorId NOT in libraries - it was deleted from Jellyfin
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns(libraries);

        // Act
        var result = _service.GetExpectedLibraryAccess(userId).ToList();

        // Assert - User should see the source as fallback
        result.Should().NotContain(deletedMirrorId, "deleted mirror should not be in result");
        result.Should().Contain(moviesId, "source should be shown as fallback when mirror is missing");
    }

    [Fact]
    public void GetExpectedLibraryAccess_DeletedAlternativeReference_ReturnsEmpty()
    {
        // DESIRED BEHAVIOR: If user's assigned alternative was deleted,
        // GetExpectedLibraryAccess should return empty (user treated as unassigned).
        
        // Arrange
        var userId = Guid.NewGuid();
        var moviesId = Guid.NewGuid();
        var deletedAlternativeId = Guid.NewGuid();

        // User references an alternative that doesn't exist anymore
        _context.AddUserLanguage(userId, deletedAlternativeId);

        var libraries = new List<VirtualFolderInfo>
        {
            CreateVirtualFolder(moviesId, "Movies")
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns(libraries);

        // Act
        var result = _service.GetExpectedLibraryAccess(userId).ToList();

        // Assert - User with deleted alternative is treated as unassigned
        result.Should().BeEmpty("user with deleted alternative should be treated as unassigned");
    }

    [Fact]
    public void GetExpectedLibraryAccess_UnmanagedUser_ReturnsEmpty()
    {
        // DESIRED BEHAVIOR: If a user is not managed by the plugin (IsPluginManaged=false),
        // we should return empty and not touch their library access at all.
        
        // Arrange
        var userId = Guid.NewGuid();
        var moviesId = Guid.NewGuid();
        var ptMirrorId = Guid.NewGuid();

        var portuguese = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        _context.AddMirror(portuguese, moviesId, "Movies", ptMirrorId);
        
        // User has language but is NOT managed by plugin
        var userConfig = _context.AddUserLanguage(userId, portuguese.Id);
        userConfig.IsPluginManaged = false;

        var libraries = new List<VirtualFolderInfo>
        {
            CreateVirtualFolder(moviesId, "Movies"),
            CreateVirtualFolder(ptMirrorId, "Filmes")
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns(libraries);

        // Act
        var result = _service.GetExpectedLibraryAccess(userId).ToList();

        // Assert - Unmanaged users return empty, their access is preserved elsewhere
        result.Should().BeEmpty("unmanaged users should return empty - we don't control their access");
    }

    [Fact]
    public void GetExpectedLibraryAccess_MirrorRemovedButOtherLanguagesExist_ShowsSource()
    {
        // SCENARIO: Movies has mirrors for PT, ES, EN
        // PT mirror is deleted (config removed after cleanup)
        // PT users should now see Movies SOURCE (fallback)
        // Because Movies is still MANAGED (has ES and EN mirrors)
        
        // Arrange
        var userId = Guid.NewGuid();
        var moviesId = Guid.NewGuid();
        var esMirrorId = Guid.NewGuid();
        var enMirrorId = Guid.NewGuid();

        var portuguese = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var spanish = _context.AddLanguageAlternative("Spanish", "es-ES");
        var english = _context.AddLanguageAlternative("English", "en-US");
        
        // Movies has ES and EN mirrors, but NOT PT (simulating PT was deleted)
        _context.AddMirror(spanish, moviesId, "Movies", esMirrorId);
        _context.AddMirror(english, moviesId, "Movies", enMirrorId);
        // Note: No PT mirror for Movies!
        
        _context.AddUserLanguage(userId, portuguese.Id);

        var libraries = new List<VirtualFolderInfo>
        {
            CreateVirtualFolder(moviesId, "Movies"),
            CreateVirtualFolder(esMirrorId, "Películas"),
            CreateVirtualFolder(enMirrorId, "Films")
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns(libraries);

        // Act
        var result = _service.GetExpectedLibraryAccess(userId).ToList();

        // Assert - PT user should see Movies source (no PT mirror, but still managed)
        result.Should().Contain(moviesId, "PT user should see source when their mirror is gone");
        result.Should().NotContain(esMirrorId, "PT user should NOT see Spanish mirror");
        result.Should().NotContain(enMirrorId, "PT user should NOT see English mirror");
    }

    [Fact]
    public void GetExpectedLibraryAccess_AllMirrorsRemoved_SourceBecomesUnmanaged()
    {
        // SCENARIO: Movies had mirrors, but ALL were deleted
        // Movies is now completely UNMANAGED
        // GetExpectedLibraryAccess doesn't return unmanaged libraries
        // BUT: LibraryChangedConsumer.CleanupOrphanedMirrorsAsync will call
        // AddLibrariesToUserAccessAsync to preserve access to the source
        
        // Arrange
        var userId = Guid.NewGuid();
        var moviesId = Guid.NewGuid();
        var showsId = Guid.NewGuid();
        var ptShowsMirrorId = Guid.NewGuid();

        var portuguese = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        // Movies has NO mirrors from ANY language (all were deleted)
        // Shows still has a mirror (so we have some managed content)
        _context.AddMirror(portuguese, showsId, "Shows", ptShowsMirrorId);
        _context.AddUserLanguage(userId, portuguese.Id);

        var libraries = new List<VirtualFolderInfo>
        {
            CreateVirtualFolder(moviesId, "Movies"),
            CreateVirtualFolder(showsId, "Shows"),
            CreateVirtualFolder(ptShowsMirrorId, "Séries")
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns(libraries);

        // Act
        var result = _service.GetExpectedLibraryAccess(userId).ToList();

        // Assert - Movies is UNMANAGED in GetExpectedLibraryAccess
        // Note: The cleanup process will add Movies to user's access via AddLibrariesToUserAccessAsync
        result.Should().NotContain(moviesId, "unmanaged source not in GetExpectedLibraryAccess");
        result.Should().Contain(ptShowsMirrorId, "user's mirror should be visible");
        result.Should().NotContain(showsId, "source WITH mirror should NOT be visible");
    }

    [Fact]
    public async Task AddLibrariesToUserAccessAsync_AddsLibrariesToUserAccess()
    {
        // SCENARIO: When the last mirror for a source is deleted,
        // AddLibrariesToUserAccessAsync is called to preserve access
        
        // Arrange
        var userId = Guid.NewGuid();
        var moviesId = Guid.NewGuid();
        var showsId = Guid.NewGuid();

        var mockUser = TestHelpers.MockFactory.CreateUser(userId, "testuser");
        _userManagerMock.Setup(m => m.GetUserById(userId)).Returns(mockUser);

        // User currently has only Shows in their access
        mockUser.SetPreference(PreferenceKind.EnabledFolders, new[] { showsId.ToString("N") });

        // Act - Add Movies to user's access
        await _service.AddLibrariesToUserAccessAsync(userId, new[] { moviesId }, CancellationToken.None);

        // Assert - User should now have both Shows and Movies
        _userManagerMock.Verify(m => m.UpdateUserAsync(mockUser), Times.Once);
        var enabledFolders = mockUser.GetPreference(PreferenceKind.EnabledFolders);
        enabledFolders.Should().Contain(moviesId.ToString("N"));
        enabledFolders.Should().Contain(showsId.ToString("N"));
    }

    #endregion

    private static VirtualFolderInfo CreateVirtualFolder(Guid id, string name)
    {
        return new VirtualFolderInfo
        {
            ItemId = id.ToString(),
            Name = name,
            Locations = new[] { $"/media/{name.ToLowerInvariant()}" }
        };
    }
}
