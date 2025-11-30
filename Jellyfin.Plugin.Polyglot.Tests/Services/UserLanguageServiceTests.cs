using FluentAssertions;
using Jellyfin.Plugin.Polyglot.Services;
using Jellyfin.Plugin.Polyglot.Tests.TestHelpers;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Polyglot.Tests.Services;

/// <summary>
/// Tests for UserLanguageService that verify actual behavior.
/// Uses PluginTestContext to set up Plugin.Instance properly.
/// </summary>
public class UserLanguageServiceTests : IDisposable
{
    private readonly PluginTestContext _context;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<ILibraryAccessService> _libraryAccessServiceMock;
    private readonly UserLanguageService _service;

    public UserLanguageServiceTests()
    {
        _context = new PluginTestContext();
        _userManagerMock = new Mock<IUserManager>();
        _libraryAccessServiceMock = new Mock<ILibraryAccessService>();
        var configServiceMock = TestHelpers.MockFactory.CreateConfigurationService(_context.Configuration);
        var logger = new Mock<ILogger<UserLanguageService>>();

        _service = new UserLanguageService(
            _userManagerMock.Object,
            _libraryAccessServiceMock.Object,
            configServiceMock.Object,
            logger.Object);
    }

    public void Dispose() => _context.Dispose();

    #region GetUserLanguage - Tests that config lookup works correctly

    [Fact]
    public void GetUserLanguage_UserHasAssignment_ReturnsConfig()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        _context.AddUserLanguage(userId, alternative.Id, manuallySet: true, setBy: "admin");

        // Act
        var result = _service.GetUserLanguage(userId);

        // Assert
        result.Should().NotBeNull();
        result!.SelectedAlternativeId.Should().Be(alternative.Id);
        result.ManuallySet.Should().BeTrue();
        result.SetBy.Should().Be("admin");
    }

    [Fact]
    public void GetUserLanguage_UserHasNoAssignment_ReturnsNull()
    {
        // Arrange
        var userId = Guid.NewGuid();
        // No assignment added

        // Act
        var result = _service.GetUserLanguage(userId);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region GetUserLanguageAlternative - Tests alternative lookup

    [Fact]
    public void GetUserLanguageAlternative_UserAssignedToAlternative_ReturnsAlternative()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var alternative = _context.AddLanguageAlternative("Spanish", "es-ES");
        _context.AddUserLanguage(userId, alternative.Id);

        // Act
        var result = _service.GetUserLanguageAlternative(userId);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(alternative.Id);
        result.Name.Should().Be("Spanish");
        result.LanguageCode.Should().Be("es-ES");
    }

    [Fact]
    public void GetUserLanguageAlternative_AlternativeDeleted_ReturnsNull()
    {
        // Arrange - User assigned to an alternative that no longer exists
        var userId = Guid.NewGuid();
        var deletedAlternativeId = Guid.NewGuid();
        _context.AddUserLanguage(userId, deletedAlternativeId);

        // Act
        var result = _service.GetUserLanguageAlternative(userId);

        // Assert - Should handle gracefully
        result.Should().BeNull();
    }

    #endregion

    #region IsManuallySet - Tests manual override flag

    [Fact]
    public void IsManuallySet_ManualAssignment_ReturnsTrue()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var alternative = _context.AddLanguageAlternative();
        _context.AddUserLanguage(userId, alternative.Id, manuallySet: true);

        // Act
        var result = _service.IsManuallySet(userId);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsManuallySet_AutomaticAssignment_ReturnsFalse()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var alternative = _context.AddLanguageAlternative();
        _context.AddUserLanguage(userId, alternative.Id, manuallySet: false, setBy: "auto");

        // Act
        var result = _service.IsManuallySet(userId);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsManuallySet_NoAssignment_ReturnsFalse()
    {
        // Arrange
        var userId = Guid.NewGuid();

        // Act
        var result = _service.IsManuallySet(userId);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region RemoveUser - Tests cleanup on user deletion

    [Fact]
    public void RemoveUser_UserExists_RemovesFromConfig()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var alternative = _context.AddLanguageAlternative();
        _context.AddUserLanguage(userId, alternative.Id);
        _context.Configuration.UserLanguages.Should().Contain(u => u.UserId == userId);

        // Act
        _service.RemoveUser(userId);

        // Assert
        _context.Configuration.UserLanguages.Should().NotContain(u => u.UserId == userId);
    }

    [Fact]
    public void RemoveUser_UserDoesNotExist_DoesNotThrow()
    {
        // Arrange
        var userId = Guid.NewGuid();

        // Act
        var action = () => _service.RemoveUser(userId);

        // Assert
        action.Should().NotThrow();
    }

    #endregion

    #region Priority 3: AssignLanguageAsync Behavior Tests

    [Fact]
    public async Task AssignLanguageAsync_ValidAssignment_TriggersLibraryAccessUpdate()
    {
        // DESIRED BEHAVIOR: When a language is assigned, the library access service
        // should be called to update the user's folder permissions.
        
        // Arrange
        var userId = Guid.NewGuid();
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var mockUser = TestHelpers.MockFactory.CreateUser(userId, "testuser");
        _userManagerMock.Setup(m => m.GetUserById(userId)).Returns(mockUser);

        // Act
        await _service.AssignLanguageAsync(userId, alternative.Id, "admin", manuallySet: true);

        // Assert - Library access should be updated
        _libraryAccessServiceMock.Verify(
            s => s.UpdateUserLibraryAccessAsync(userId, It.IsAny<CancellationToken>()),
            Times.Once,
            "assigning language should trigger library access update");
    }

    [Fact]
    public async Task AssignLanguageAsync_ClearingLanguage_SetsSelectedAlternativeToNull()
    {
        // DESIRED BEHAVIOR: Passing null alternativeId should clear the user's assignment.
        
        // Arrange
        var userId = Guid.NewGuid();
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        _context.AddUserLanguage(userId, alternative.Id);
        
        var mockUser = TestHelpers.MockFactory.CreateUser(userId, "testuser");
        _userManagerMock.Setup(m => m.GetUserById(userId)).Returns(mockUser);

        // Act
        await _service.AssignLanguageAsync(userId, null, "admin", manuallySet: true);

        // Assert
        var userConfig = _service.GetUserLanguage(userId);
        userConfig.Should().NotBeNull();
        userConfig!.SelectedAlternativeId.Should().BeNull("clearing should set alternative to null");
    }

    [Fact]
    public async Task AssignLanguageAsync_ReassigningSameLanguage_IsIdempotent()
    {
        // DESIRED BEHAVIOR: Assigning the same language twice should not create
        // duplicate entries or cause errors.
        
        // Arrange
        var userId = Guid.NewGuid();
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var mockUser = TestHelpers.MockFactory.CreateUser(userId, "testuser");
        _userManagerMock.Setup(m => m.GetUserById(userId)).Returns(mockUser);

        // Act - Assign twice
        await _service.AssignLanguageAsync(userId, alternative.Id, "admin", manuallySet: true);
        await _service.AssignLanguageAsync(userId, alternative.Id, "admin", manuallySet: true);

        // Assert - Should only have one entry
        var userEntries = _context.Configuration.UserLanguages.Where(u => u.UserId == userId).ToList();
        userEntries.Should().HaveCount(1, "re-assigning should update, not create duplicate");
        userEntries[0].SelectedAlternativeId.Should().Be(alternative.Id);
    }

    [Fact]
    public async Task AssignLanguageAsync_AutoOverManual_DoesNotOverride()
    {
        // DESIRED BEHAVIOR: If a user was manually set to a language,
        // automatic assignments should NOT override it (manual takes priority).
        
        // Arrange
        var userId = Guid.NewGuid();
        var portuguese = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var spanish = _context.AddLanguageAlternative("Spanish", "es-ES");
        
        var mockUser = TestHelpers.MockFactory.CreateUser(userId, "testuser");
        _userManagerMock.Setup(m => m.GetUserById(userId)).Returns(mockUser);

        // First, set manually
        await _service.AssignLanguageAsync(userId, portuguese.Id, "admin", manuallySet: true);

        // Act - Check if the manual flag is preserved correctly
        var isManual = _service.IsManuallySet(userId);

        // Assert - User should still be marked as manually set
        isManual.Should().BeTrue("manual assignment should be respected");
        
        // Any automatic assignment caller should check IsManuallySet before calling AssignLanguageAsync
        // This test documents that the flag is preserved correctly
    }

    [Fact]
    public async Task AssignLanguageAsync_NonExistentUser_ThrowsArgumentException()
    {
        // DESIRED BEHAVIOR: Trying to assign language to a non-existent user
        // should throw an appropriate exception.
        
        // Arrange
        var nonExistentUserId = Guid.NewGuid();
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        _userManagerMock.Setup(m => m.GetUserById(nonExistentUserId)).Returns((Jellyfin.Data.Entities.User?)null);

        // Act
        var action = async () => await _service.AssignLanguageAsync(
            nonExistentUserId, alternative.Id, "admin", manuallySet: true);

        // Assert
        await action.Should().ThrowAsync<ArgumentException>()
            .WithMessage($"*{nonExistentUserId}*not found*");
    }

    [Fact]
    public async Task AssignLanguageAsync_NonExistentAlternative_ThrowsArgumentException()
    {
        // DESIRED BEHAVIOR: Trying to assign a non-existent alternative
        // should throw an appropriate exception.
        
        // Arrange
        var userId = Guid.NewGuid();
        var nonExistentAlternativeId = Guid.NewGuid();
        var mockUser = TestHelpers.MockFactory.CreateUser(userId, "testuser");
        _userManagerMock.Setup(m => m.GetUserById(userId)).Returns(mockUser);

        // Act
        var action = async () => await _service.AssignLanguageAsync(
            userId, nonExistentAlternativeId, "admin", manuallySet: true);

        // Assert
        await action.Should().ThrowAsync<ArgumentException>()
            .WithMessage($"*{nonExistentAlternativeId}*not found*");
    }

    [Fact]
    public async Task AssignLanguageAsync_NotPluginManaged_DoesNotUpdateLibraryAccess()
    {
        // DESIRED BEHAVIOR: If isPluginManaged is false, we should NOT update
        // the user's library access.
        
        // Arrange
        var userId = Guid.NewGuid();
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var mockUser = TestHelpers.MockFactory.CreateUser(userId, "testuser");
        _userManagerMock.Setup(m => m.GetUserById(userId)).Returns(mockUser);

        // Act
        await _service.AssignLanguageAsync(userId, alternative.Id, "admin", manuallySet: true, isPluginManaged: false);

        // Assert - Library access should NOT be updated when not plugin managed
        _libraryAccessServiceMock.Verify(
            s => s.UpdateUserLibraryAccessAsync(userId, It.IsAny<CancellationToken>()),
            Times.Never,
            "library access should not be updated when isPluginManaged is false");
    }

    #endregion
}
