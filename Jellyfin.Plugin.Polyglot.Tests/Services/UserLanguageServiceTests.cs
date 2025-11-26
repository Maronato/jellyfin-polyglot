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
        var logger = new Mock<ILogger<UserLanguageService>>();

        _service = new UserLanguageService(
            _userManagerMock.Object,
            _libraryAccessServiceMock.Object,
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
    public void IsManuallySet_LdapAssignment_ReturnsFalse()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var alternative = _context.AddLanguageAlternative();
        _context.AddUserLanguage(userId, alternative.Id, manuallySet: false, setBy: "ldap");

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
}
