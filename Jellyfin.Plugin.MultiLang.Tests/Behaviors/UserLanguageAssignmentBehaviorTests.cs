using FluentAssertions;
using Jellyfin.Data.Entities;
using Jellyfin.Plugin.MultiLang.Models;
using Jellyfin.Plugin.MultiLang.Services;
using Jellyfin.Plugin.MultiLang.Tests.TestHelpers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MultiLang.Tests.Behaviors;

/// <summary>
/// Behavior tests for user language assignment.
/// These test the business rules around assigning languages to users.
/// </summary>
public class UserLanguageAssignmentBehaviorTests : IDisposable
{
    private readonly PluginTestContext _context;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<ILibraryAccessService> _libraryAccessServiceMock;
    private readonly UserLanguageService _userLanguageService;
    private readonly LibraryAccessService _libraryAccessService;

    public UserLanguageAssignmentBehaviorTests()
    {
        _context = new PluginTestContext();
        _userManagerMock = new Mock<IUserManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _libraryAccessServiceMock = new Mock<ILibraryAccessService>();
        var userLogger = new Mock<ILogger<UserLanguageService>>();
        var accessLogger = new Mock<ILogger<LibraryAccessService>>();

        _userLanguageService = new UserLanguageService(
            _userManagerMock.Object,
            _libraryAccessServiceMock.Object,
            userLogger.Object);

        _libraryAccessService = new LibraryAccessService(
            _userManagerMock.Object,
            _libraryManagerMock.Object,
            accessLogger.Object);
    }

    public void Dispose() => _context.Dispose();

    /// <summary>
    /// Creates a mock Jellyfin user that the UserManager will return.
    /// </summary>
    private User CreateMockUser(Guid userId, string username = "testuser")
    {
        var user = new User(username, "Test", "test");
        // Use reflection to set the Id since it's normally set by EF
        typeof(User).GetProperty("Id")?.SetValue(user, userId);
        _userManagerMock.Setup(m => m.GetUserById(userId)).Returns(user);
        return user;
    }

    #region Behavior: Assigning a language to a user

    [Fact]
    public async Task AssigningLanguage_ShouldRecordWhoAndWhen()
    {
        // Scenario: When an admin assigns Portuguese to a user,
        // we should record who made the change and when

        // Arrange
        var userId = Guid.NewGuid();
        CreateMockUser(userId); // User must exist in Jellyfin
        var portuguese = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var beforeAssignment = DateTime.UtcNow;

        // Act
        await _userLanguageService.AssignLanguageAsync(
            userId,
            portuguese.Id,
            setBy: "admin",
            manuallySet: true,
            isPluginManaged: true);

        // Assert
        var userConfig = _context.Configuration.UserLanguages
            .FirstOrDefault(u => u.UserId == userId);

        userConfig.Should().NotBeNull();
        userConfig!.SelectedAlternativeId.Should().Be(portuguese.Id);
        userConfig.SetBy.Should().Be("admin");
        userConfig.ManuallySet.Should().BeTrue();
        userConfig.SetAt.Should().BeOnOrAfter(beforeAssignment);
        userConfig.IsPluginManaged.Should().BeTrue();
    }

    [Fact]
    public async Task AssigningLanguage_TwiceToSameUser_ShouldUpdateNotDuplicate()
    {
        // Scenario: If a user is reassigned from Portuguese to Spanish,
        // there should still be only one entry for that user

        // Arrange
        var userId = Guid.NewGuid();
        CreateMockUser(userId); // User must exist
        var portuguese = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var spanish = _context.AddLanguageAlternative("Spanish", "es-ES");

        // First assignment
        await _userLanguageService.AssignLanguageAsync(userId, portuguese.Id, "admin", true, true);

        // Act - Reassign to Spanish
        await _userLanguageService.AssignLanguageAsync(userId, spanish.Id, "admin", true, true);

        // Assert - Only one entry, updated to Spanish
        var userEntries = _context.Configuration.UserLanguages
            .Where(u => u.UserId == userId)
            .ToList();

        userEntries.Should().HaveCount(1, "should not create duplicate entries");
        userEntries[0].SelectedAlternativeId.Should().Be(spanish.Id);
    }

    [Fact]
    public async Task ClearingLanguage_ShouldSetAlternativeToNull()
    {
        // Scenario: An admin can clear a user's language assignment,
        // putting them back to "default"

        // Arrange
        var userId = Guid.NewGuid();
        CreateMockUser(userId); // User must exist
        var portuguese = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        await _userLanguageService.AssignLanguageAsync(userId, portuguese.Id, "admin", true, true);

        // Act - Clear the assignment
        await _userLanguageService.AssignLanguageAsync(userId, null, "admin", true, true);

        // Assert
        var userConfig = _context.Configuration.UserLanguages
            .FirstOrDefault(u => u.UserId == userId);

        userConfig.Should().NotBeNull();
        userConfig!.SelectedAlternativeId.Should().BeNull("language should be cleared");
    }

    [Fact]
    public async Task AssigningLanguage_ToNonExistentUser_ShouldThrow()
    {
        // Scenario: Trying to assign a language to a user that doesn't
        // exist in Jellyfin should fail

        // Arrange
        var nonExistentUserId = Guid.NewGuid();
        // Don't create mock user - they don't exist
        var portuguese = _context.AddLanguageAlternative("Portuguese", "pt-BR");

        // Act & Assert
        var act = async () => await _userLanguageService.AssignLanguageAsync(
            nonExistentUserId, portuguese.Id, "admin", true, true);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*not found*");
    }

    #endregion

    #region Behavior: Manual vs LDAP assignments

    [Fact]
    public async Task ManualAssignment_ShouldBeMarkedAsManual()
    {
        // Scenario: When an admin manually assigns a language via UI,
        // it should be marked as manual (so LDAP doesn't override it)

        // Arrange
        var userId = Guid.NewGuid();
        CreateMockUser(userId);
        var portuguese = _context.AddLanguageAlternative("Portuguese", "pt-BR");

        // Act
        await _userLanguageService.AssignLanguageAsync(
            userId, portuguese.Id, "admin", 
            manuallySet: true, // Manual!
            isPluginManaged: true);

        // Assert
        var isManual = _userLanguageService.IsManuallySet(userId);
        isManual.Should().BeTrue("manual assignments should be marked as such");
    }

    [Fact]
    public async Task LdapAssignment_ShouldBeMarkedAsAutomatic()
    {
        // Scenario: When LDAP auto-assigns a language,
        // it should NOT be marked as manual

        // Arrange
        var userId = Guid.NewGuid();
        CreateMockUser(userId);
        var portuguese = _context.AddLanguageAlternative("Portuguese", "pt-BR");

        // Act
        await _userLanguageService.AssignLanguageAsync(
            userId, portuguese.Id, "ldap-sync",
            manuallySet: false, // Automatic!
            isPluginManaged: true);

        // Assert
        var isManual = _userLanguageService.IsManuallySet(userId);
        isManual.Should().BeFalse("LDAP assignments should not be marked as manual");
    }

    #endregion

    #region Behavior: Getting user's current language

    [Fact]
    public async Task GetUserLanguage_ShouldReturnCurrentAssignment()
    {
        // Arrange
        var userId = Guid.NewGuid();
        CreateMockUser(userId);
        var portuguese = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        await _userLanguageService.AssignLanguageAsync(userId, portuguese.Id, "admin", true, true);

        // Act
        var config = _userLanguageService.GetUserLanguage(userId);

        // Assert
        config.Should().NotBeNull();
        config!.SelectedAlternativeId.Should().Be(portuguese.Id);
    }

    [Fact]
    public void GetUserLanguage_UnknownUser_ShouldReturnNull()
    {
        // Arrange - user never assigned
        var unknownUserId = Guid.NewGuid();

        // Act
        var config = _userLanguageService.GetUserLanguage(unknownUserId);

        // Assert
        config.Should().BeNull("unknown users have no assignment");
    }

    [Fact]
    public async Task GetUserLanguageAlternative_ShouldReturnFullAlternativeObject()
    {
        // Scenario: We should be able to get the full alternative details
        // for a user's assignment (not just the ID)

        // Arrange
        var userId = Guid.NewGuid();
        CreateMockUser(userId);
        var portuguese = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        await _userLanguageService.AssignLanguageAsync(userId, portuguese.Id, "admin", true, true);

        // Act
        var alternative = _userLanguageService.GetUserLanguageAlternative(userId);

        // Assert
        alternative.Should().NotBeNull();
        alternative!.Name.Should().Be("Portuguese");
        alternative.LanguageCode.Should().Be("pt-BR");
    }

    #endregion

    #region Behavior: Removing users from config

    [Fact]
    public async Task RemoveUser_ShouldClearAllUserData()
    {
        // Scenario: When a user is deleted from Jellyfin,
        // their data should be removed from plugin config

        // Arrange
        var userId = Guid.NewGuid();
        CreateMockUser(userId);
        var portuguese = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        await _userLanguageService.AssignLanguageAsync(userId, portuguese.Id, "admin", true, true);

        // Verify user exists in config
        _context.Configuration.UserLanguages.Should().Contain(u => u.UserId == userId);

        // Act
        _userLanguageService.RemoveUser(userId);

        // Assert
        _context.Configuration.UserLanguages.Should().NotContain(u => u.UserId == userId,
            "user should be removed from config");
    }

    [Fact]
    public void RemoveUser_NonExistentUser_ShouldNotThrow()
    {
        // Scenario: Removing a user that doesn't exist should be safe

        // Arrange
        var nonExistentUserId = Guid.NewGuid();

        // Act & Assert - Should not throw
        var act = () => _userLanguageService.RemoveUser(nonExistentUserId);
        act.Should().NotThrow();
    }

    #endregion
}

/// <summary>
/// Behavior tests for the IsPluginManaged flag.
/// This flag controls whether the plugin actively manages a user's library access.
/// </summary>
public class PluginManagedBehaviorTests : IDisposable
{
    private readonly PluginTestContext _context;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<ILibraryAccessService> _libraryAccessServiceMock;
    private readonly UserLanguageService _userLanguageService;
    private readonly LibraryAccessService _libraryAccessService;

    public PluginManagedBehaviorTests()
    {
        _context = new PluginTestContext();
        _userManagerMock = new Mock<IUserManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _libraryAccessServiceMock = new Mock<ILibraryAccessService>();
        var userLogger = new Mock<ILogger<UserLanguageService>>();
        var accessLogger = new Mock<ILogger<LibraryAccessService>>();

        _userLanguageService = new UserLanguageService(
            _userManagerMock.Object,
            _libraryAccessServiceMock.Object,
            userLogger.Object);

        _libraryAccessService = new LibraryAccessService(
            _userManagerMock.Object,
            _libraryManagerMock.Object,
            accessLogger.Object);
    }

    public void Dispose() => _context.Dispose();

    private User CreateMockUser(Guid userId, string username = "testuser")
    {
        var user = new User(username, "Test", "test");
        typeof(User).GetProperty("Id")?.SetValue(user, userId);
        _userManagerMock.Setup(m => m.GetUserById(userId)).Returns(user);
        return user;
    }

    [Fact]
    public async Task NewlyAssignedUser_ShouldBeManaged_WhenFlagIsTrue()
    {
        // Scenario: When enabling plugin management for a user,
        // they should be marked as managed

        // Arrange
        var userId = Guid.NewGuid();
        CreateMockUser(userId);
        var portuguese = _context.AddLanguageAlternative("Portuguese", "pt-BR");

        // Act
        await _userLanguageService.AssignLanguageAsync(
            userId, portuguese.Id, "admin", true, 
            isPluginManaged: true);

        // Assert
        var config = _userLanguageService.GetUserLanguage(userId);
        config!.IsPluginManaged.Should().BeTrue();
    }

    [Fact]
    public async Task User_CanBeAssignedLanguage_WithoutBeingManaged()
    {
        // Scenario: An admin might want to record a user's preferred language
        // without actually managing their library access

        // Arrange
        var userId = Guid.NewGuid();
        CreateMockUser(userId);
        var portuguese = _context.AddLanguageAlternative("Portuguese", "pt-BR");

        // Act
        await _userLanguageService.AssignLanguageAsync(
            userId, portuguese.Id, "admin", true,
            isPluginManaged: false); // Has language but NOT managed

        // Assert
        var config = _userLanguageService.GetUserLanguage(userId);
        config!.SelectedAlternativeId.Should().Be(portuguese.Id);
        config.IsPluginManaged.Should().BeFalse();
    }

    [Fact]
    public async Task UnmanagedUser_HasNoExpectedLibraryAccess()
    {
        // Scenario: Even if a user has a language assigned,
        // if they're not managed, we should return empty library list
        // 
        // NOTE: This test verifies DESIRED behavior. If it fails, 
        // the implementation may have a bug in checking IsPluginManaged.

        // Arrange
        var userId = Guid.NewGuid();
        CreateMockUser(userId);
        var portuguese = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var sourceId = Guid.NewGuid();
        var mirrorId = Guid.NewGuid();
        _context.AddMirror(portuguese, sourceId, "Movies", mirrorId);

        // Setup library manager to return the libraries
        _libraryManagerMock.Setup(m => m.GetVirtualFolders())
            .Returns(new List<VirtualFolderInfo>
            {
                new VirtualFolderInfo { ItemId = sourceId.ToString("N"), Name = "Movies" },
                new VirtualFolderInfo { ItemId = mirrorId.ToString("N"), Name = "Filmes" }
            });

        await _userLanguageService.AssignLanguageAsync(
            userId, portuguese.Id, "admin", true,
            isPluginManaged: false);

        // Act
        var libraries = _libraryAccessService.GetExpectedLibraryAccess(userId).ToList();

        // Assert - Empty because user is not managed
        // If this fails, GetExpectedLibraryAccess doesn't check IsPluginManaged!
        libraries.Should().BeEmpty(
            "unmanaged users should have no expected libraries");
    }
}

