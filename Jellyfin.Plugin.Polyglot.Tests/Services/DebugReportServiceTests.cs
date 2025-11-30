using System.Collections.Generic;
using FluentAssertions;
using Jellyfin.Data.Entities;
using Jellyfin.Plugin.Polyglot.Configuration;
using Jellyfin.Plugin.Polyglot.Models;
using Jellyfin.Plugin.Polyglot.Services;
using Jellyfin.Plugin.Polyglot.Tests.TestHelpers;
using MediaBrowser.Common;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Polyglot.Tests.Services;

/// <summary>
/// Tests for DebugReportService.
/// </summary>
public class DebugReportServiceTests : IDisposable
{
    private readonly PluginTestContext _context;
    private readonly Mock<IApplicationHost> _applicationHostMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<ILogger<DebugReportService>> _loggerMock;
    private readonly DebugReportService _service;

    public DebugReportServiceTests()
    {
        _context = new PluginTestContext();
        _applicationHostMock = new Mock<IApplicationHost>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _loggerMock = new Mock<ILogger<DebugReportService>>();

        _applicationHostMock.Setup(x => x.ApplicationVersionString).Returns("10.9.0");
        _applicationHostMock.Setup(x => x.GetExports<IPlugin>(It.IsAny<bool>())).Returns(Array.Empty<IPlugin>());
        _libraryManagerMock.Setup(x => x.GetVirtualFolders()).Returns(new List<MediaBrowser.Model.Entities.VirtualFolderInfo>());
        _userManagerMock.Setup(x => x.Users).Returns(Array.Empty<User>());

        var configServiceMock = TestHelpers.MockFactory.CreateConfigurationService(_context.Configuration);

        _service = new DebugReportService(
            _applicationHostMock.Object,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            configServiceMock.Object,
            _loggerMock.Object);
    }

    public void Dispose() => _context.Dispose();

    #region GenerateReportAsync

    [Fact]
    public async Task GenerateReportAsync_WithDefaultConfig_ReturnsValidReport()
    {
        // Act
        var report = await _service.GenerateReportAsync();

        // Assert
        report.Should().NotBeNull();
        report.GeneratedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        report.Environment.Should().NotBeNull();
        report.Environment.JellyfinVersion.Should().Be("10.9.0");
        report.Environment.PluginVersion.Should().NotBeEmpty();
        report.Environment.OperatingSystem.Should().NotBeEmpty();
        report.Environment.DotNetVersion.Should().NotBeEmpty();
        report.Environment.Architecture.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GenerateReportAsync_WithLanguageAlternatives_IncludesConfigurationSummary()
    {
        // Arrange
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR", "/media/pt");
        _context.AddMirror(alternative, Guid.NewGuid(), "Movies", null, "/media/pt/movies");

        // Act
        var report = await _service.GenerateReportAsync();

        // Assert
        report.Configuration.LanguageAlternativeCount.Should().Be(1);
        report.Configuration.TotalMirrorCount.Should().Be(1);
    }

    [Fact]
    public async Task GenerateReportAsync_WithManagedUsers_IncludesManagedUserCount()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR", "/media/pt");
        _context.AddUserLanguage(userId, alternative.Id, false, "test", true);

        // Act
        var report = await _service.GenerateReportAsync();

        // Assert
        report.Configuration.ManagedUserCount.Should().Be(1);
    }

    [Fact]
    public async Task GenerateReportAsync_WithNewUserDefaults_IncludesSettings()
    {
        // Arrange
        _context.Configuration.AutoManageNewUsers = true;
        _context.Configuration.SyncMirrorsAfterLibraryScan = false;

        // Act
        var report = await _service.GenerateReportAsync();

        // Assert
        report.Configuration.AutoManageNewUsers.Should().BeTrue();
        report.Configuration.SyncAfterLibraryScan.Should().BeFalse();
    }

    [Fact]
    public async Task GenerateReportAsync_WithMirrors_IncludesMirrorHealth()
    {
        // Arrange
        var sourceLibraryId = Guid.NewGuid();
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR", "/media/pt");
        var mirror = _context.AddMirror(alternative, sourceLibraryId, "Movies", null, "/media/pt/movies");
        mirror.Status = SyncStatus.Synced;
        mirror.LastSyncedAt = DateTime.UtcNow.AddHours(-2);
        mirror.LastSyncFileCount = 100;

        // Act
        var report = await _service.GenerateReportAsync();

        // Assert
        report.MirrorHealth.Should().HaveCount(1);
        report.MirrorHealth[0].Status.Should().Be("Synced");
        report.MirrorHealth[0].FileCount.Should().Be(100);
    }

    [Fact]
    public async Task GenerateReportAsync_WithErrors_SanitizesErrorPaths()
    {
        // Arrange
        var sourceLibraryId = Guid.NewGuid();
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR", "/media/pt");
        var mirror = _context.AddMirror(alternative, sourceLibraryId, "Movies", null, "/media/pt/movies");
        mirror.Status = SyncStatus.Error;
        mirror.LastError = "Failed to create hardlink at /home/user/media/movies/file.mkv: Permission denied";

        // Act
        var report = await _service.GenerateReportAsync();

        // Assert
        report.MirrorHealth[0].LastError.Should().Contain("[path]");
        report.MirrorHealth[0].LastError.Should().NotContain("/home/user");
    }

    #endregion

    #region GenerateMarkdownReportAsync

    [Fact]
    public async Task GenerateMarkdownReportAsync_ReturnsFormattedMarkdown()
    {
        // Arrange
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR", "/media/pt");
        _context.AddMirror(alternative, Guid.NewGuid(), "Movies", null, "/media/pt/movies");

        // Act
        var markdown = await _service.GenerateMarkdownReportAsync();

        // Assert
        markdown.Should().Contain("# Polyglot Debug Report");
        markdown.Should().Contain("## Environment");
        markdown.Should().Contain("## Configuration Summary");
        markdown.Should().Contain("## Mirror Health");
        markdown.Should().Contain("**Plugin Version:**");
        markdown.Should().Contain("**Jellyfin Version:** 10.9.0");
    }

    [Fact]
    public async Task GenerateMarkdownReportAsync_WithUserDistribution_IncludesUserSection()
    {
        // Arrange
        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR", "/media/pt");
        _context.AddUserLanguage(Guid.NewGuid(), alternative.Id, false, "test", true);
        _context.AddUserLanguage(Guid.NewGuid(), alternative.Id, false, "test", true);

        // Act
        var markdown = await _service.GenerateMarkdownReportAsync();

        // Assert
        markdown.Should().Contain("## User Distribution");
        markdown.Should().Contain("Alt_1 (pt-BR): 2 users");
    }

    #endregion

    #region LogToBuffer

    [Fact]
    public async Task LogToBuffer_AddsEntryToBuffer()
    {
        // Arrange & Act
        _service.LogToBuffer("Information", "Test message");

        // Assert
        var report = await _service.GenerateReportAsync();
        report.RecentLogs.Should().ContainSingle(l => l.Message.Contains("Test message"));
    }

    [Fact]
    public async Task LogToBuffer_WithEntities_SanitizesPathsBasedOnPrivacySettings()
    {
        // Arrange - log with a path entity
        var pathEntity = new LogPath("/home/user/media/file.mkv", "file");
        var entities = new List<ILogEntity> { pathEntity };
        DebugReportService.LogToBufferStatic(
            "Error",
            "Failed to read {0}",
            "Failed to read /home/user/media/file.mkv",
            entities);

        // Act - generate report without path privacy
        var report = await _service.GenerateReportAsync(new DebugReportOptions { IncludeFilePaths = false });

        // Assert - paths should be anonymized when IncludeFilePaths is false
        report.RecentLogs.Should().Contain(l => l.Message.Contains("[file_") && l.Message.Contains("Failed to read"));

        // Act again - generate report with path privacy enabled
        var reportWithPaths = await _service.GenerateReportAsync(new DebugReportOptions { IncludeFilePaths = true });

        // Assert - paths should be shown when IncludeFilePaths is true
        reportWithPaths.RecentLogs.Should().Contain(l => l.Message.Contains("/home/user/media/file.mkv"));
    }

    [Fact]
    public async Task LogToBuffer_WithException_IncludesExceptionMessage()
    {
        // Arrange & Act
        _service.LogToBuffer("Error", "Operation failed", "NullReferenceException: Object reference not set");

        // Assert
        var report = await _service.GenerateReportAsync();
        var logEntry = report.RecentLogs.First(l => l.Message.Contains("Operation failed"));
        logEntry.Exception.Should().Contain("NullReferenceException");
    }

    [Fact]
    public async Task LogToBufferStatic_CanBeCalledWithoutServiceInstance()
    {
        // Arrange & Act - static method should not throw
        DebugReportService.LogToBufferStatic("Information", "Static log test");

        // Assert - verify through the instance service
        var report = await _service.GenerateReportAsync();
        report.RecentLogs.Should().Contain(l => l.Message.Contains("Static log test"));
    }

    #endregion

    #region OtherPlugins

    [Fact]
    public async Task GenerateReportAsync_WithOtherPlugins_IncludesPluginList()
    {
        // Arrange
        var pluginMock = new Mock<IPlugin>();
        pluginMock.Setup(x => x.Id).Returns(Guid.NewGuid());
        pluginMock.Setup(x => x.Name).Returns("Test Plugin");
        pluginMock.Setup(x => x.Version).Returns(new Version(1, 2, 3));

        _applicationHostMock.Setup(x => x.GetExports<IPlugin>(It.IsAny<bool>()))
            .Returns(new List<IPlugin> { pluginMock.Object });

        // Act
        var report = await _service.GenerateReportAsync();

        // Assert
        report.OtherPlugins.Should().HaveCount(1);
        report.OtherPlugins[0].Name.Should().Be("Test Plugin");
        report.OtherPlugins[0].Version.Should().Be("1.2.3");
    }

    #endregion
}
