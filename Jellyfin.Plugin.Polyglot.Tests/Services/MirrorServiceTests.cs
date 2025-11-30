using System.Text.Json;
using FluentAssertions;
using Jellyfin.Plugin.Polyglot.Models;
using PluginConfig = Jellyfin.Plugin.Polyglot.Configuration.PluginConfiguration;
using Jellyfin.Plugin.Polyglot.Services;
using Jellyfin.Plugin.Polyglot.Tests.TestHelpers;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Polyglot.Tests.Services;

/// <summary>
/// Tests for MirrorService focusing on validation and file operations.
/// </summary>
public class MirrorServiceValidationTests
{
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IProviderManager> _providerManagerMock;
    private readonly Mock<IFileSystem> _fileSystemMock;
    private readonly Mock<IConfigurationService> _configServiceMock;
    private readonly MirrorService _service;

    public MirrorServiceValidationTests()
    {
        _libraryManagerMock = new Mock<ILibraryManager>();
        _providerManagerMock = new Mock<IProviderManager>();
        _fileSystemMock = new Mock<IFileSystem>();
        _configServiceMock = TestHelpers.MockFactory.CreateConfigurationService();
        var logger = new Mock<ILogger<MirrorService>>();
        _service = new MirrorService(
            _libraryManagerMock.Object,
            _providerManagerMock.Object,
            _fileSystemMock.Object,
            _configServiceMock.Object,
            logger.Object);
    }

    #region ValidateMirrorConfiguration - Input validation

    [Fact]
    public void ValidateMirrorConfiguration_SourceLibraryNotFound_ReturnsInvalid()
    {
        // Arrange
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>());

        // Act
        var (isValid, errorMessage) = _service.ValidateMirrorConfiguration(Guid.NewGuid(), "/media/target");

        // Assert
        isValid.Should().BeFalse();
        errorMessage.Should().Contain("not found");
    }

    [Fact]
    public void ValidateMirrorConfiguration_SourceLibraryNoPaths_ReturnsInvalid()
    {
        // Arrange
        var sourceId = Guid.NewGuid();
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>
        {
            new() { ItemId = sourceId.ToString(), Name = "Movies", Locations = Array.Empty<string>() }
        });

        // Act
        var (isValid, errorMessage) = _service.ValidateMirrorConfiguration(sourceId, "/media/target");

        // Assert
        isValid.Should().BeFalse();
        errorMessage.Should().Contain("no paths");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateMirrorConfiguration_EmptyTargetPath_ReturnsInvalid(string? targetPath)
    {
        // Arrange
        var sourceId = Guid.NewGuid();
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>
        {
            new() { ItemId = sourceId.ToString(), Name = "Movies", Locations = new[] { "/media/movies" } }
        });

        // Act
        var (isValid, errorMessage) = _service.ValidateMirrorConfiguration(sourceId, targetPath!);

        // Assert
        isValid.Should().BeFalse();
        errorMessage.Should().Contain("required");
    }

    [Fact]
    public void ValidateMirrorConfiguration_PathTraversal_ReturnsInvalid()
    {
        // Arrange
        var sourceId = Guid.NewGuid();
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>
        {
            new() { ItemId = sourceId.ToString(), Name = "Movies", Locations = new[] { "/media/movies" } }
        });

        // Act
        var (isValid, errorMessage) = _service.ValidateMirrorConfiguration(sourceId, "/media/../../../etc/passwd");

        // Assert
        isValid.Should().BeFalse();
        errorMessage.Should().Contain("traversal");
    }

    #endregion
}

/// <summary>
/// Integration tests for MirrorService file operations.
/// These tests use real filesystem operations to verify behavior.
/// </summary>
public class MirrorServiceFileOperationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IProviderManager> _providerManagerMock;
    private readonly Mock<IFileSystem> _fileSystemMock;
    private readonly Mock<IConfigurationService> _configServiceMock;
    private readonly MirrorService _service;

    public MirrorServiceFileOperationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "polyglot_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _libraryManagerMock = new Mock<ILibraryManager>();
        _providerManagerMock = new Mock<IProviderManager>();
        _fileSystemMock = new Mock<IFileSystem>();
        _configServiceMock = TestHelpers.MockFactory.CreateConfigurationService();
        var logger = new Mock<ILogger<MirrorService>>();
        _service = new MirrorService(
            _libraryManagerMock.Object,
            _providerManagerMock.Object,
            _fileSystemMock.Object,
            _configServiceMock.Object,
            logger.Object);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    /// <summary>
    /// Creates a deep clone of the configuration using JSON serialization.
    /// Matches the behavior of the real ConfigurationService.
    /// </summary>
    private static PluginConfig CloneConfig(PluginConfig config)
    {
        var json = JsonSerializer.Serialize(config);
        return JsonSerializer.Deserialize<PluginConfig>(json)!;
    }

    /// <summary>
    /// Copies all configuration properties from source to destination.
    /// Must be kept in sync with MockFactory.CopyConfigTo and PluginConfiguration properties.
    /// </summary>
    private static void CopyConfigTo(PluginConfig source, PluginConfig dest)
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

    private void SetupMirrorConfig(LibraryMirror mirror, LanguageAlternative? alternative = null)
    {
        // Create an alternative if not provided
        alternative ??= new LanguageAlternative
        {
            Id = Guid.NewGuid(),
            Name = "TestAlternative",
            LanguageCode = "en-US",
            MirroredLibraries = new List<LibraryMirror> { mirror }
        };

        // Ensure mirror is in the alternative's list
        if (!alternative.MirroredLibraries.Contains(mirror))
        {
            alternative.MirroredLibraries.Add(mirror);
        }

        var config = new PluginConfig
        {
            LanguageAlternatives = new List<LanguageAlternative> { alternative }
        };

        // Setup Read to return from a cloned snapshot (matches real ConfigurationService lines 44-46)
        _configServiceMock.Setup(m => m.Read(It.IsAny<Func<PluginConfig, It.IsAnyType>>()))
            .Returns((Delegate selector) =>
            {
                var snapshot = CloneConfig(config);
                return selector.DynamicInvoke(snapshot);
            });

        // Setup Update(Action) to apply mutations to a cloned config
        _configServiceMock.Setup(m => m.Update(It.IsAny<Action<PluginConfig>>()))
            .Callback((Action<PluginConfig> mutation) =>
            {
                // Clone before mutation (matches real service line 65)
                var snapshot = CloneConfig(config);
                mutation(snapshot);
                // Clone again to break references from objects added during mutation (matches line 78)
                var toSave = CloneConfig(snapshot);
                // Update the live config - copy ALL properties, not just LanguageAlternatives
                CopyConfigTo(toSave, config);
            });

        // Setup Update(Func<bool>) to apply mutations conditionally
        _configServiceMock.Setup(m => m.Update(It.IsAny<Func<PluginConfig, bool>>()))
            .Returns((Func<PluginConfig, bool> mutation) =>
            {
                // Clone before mutation (matches real service line 65)
                var snapshot = CloneConfig(config);
                if (!mutation(snapshot))
                {
                    return false;
                }
                // Clone again to break references from objects added during mutation (matches line 78)
                var toSave = CloneConfig(snapshot);
                // Update the live config - copy ALL properties, not just LanguageAlternatives
                CopyConfigTo(toSave, config);
                return true;
            });
    }

    [Fact]
    public async Task SyncMirrorAsync_NewVideoFile_CreatesHardlink()
    {
        // Arrange
        var sourceDir = Path.Combine(_tempDir, "source");
        var targetDir = Path.Combine(_tempDir, "target");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(targetDir);

        var sourceFile = Path.Combine(sourceDir, "movie.mkv");
        File.WriteAllText(sourceFile, "video content");

        var sourceId = Guid.NewGuid();
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>
        {
            new() { ItemId = sourceId.ToString(), Name = "Movies", Locations = new[] { sourceDir } }
        });

        var mirror = new LibraryMirror
        {
            Id = Guid.NewGuid(),
            SourceLibraryId = sourceId,
            SourceLibraryName = "Movies",
            TargetPath = targetDir,
            Status = SyncStatus.Pending
        };
        SetupMirrorConfig(mirror);

        // Act
        await _service.SyncMirrorAsync(mirror.Id);

        // Assert
        var targetFile = Path.Combine(targetDir, "movie.mkv");
        File.Exists(targetFile).Should().BeTrue("video file should be hardlinked");
        File.ReadAllText(targetFile).Should().Be("video content");
        // Re-read from config since Update works with cloned snapshots (proper isolation)
        var updatedMirror = _configServiceMock.Object.Read(c =>
            c.LanguageAlternatives.SelectMany(a => a.MirroredLibraries).FirstOrDefault(m => m.Id == mirror.Id));
        updatedMirror.Should().NotBeNull();
        updatedMirror!.Status.Should().Be(SyncStatus.Synced);
    }

    [Fact]
    public async Task SyncMirrorAsync_MetadataFile_NotHardlinked()
    {
        // Arrange
        var sourceDir = Path.Combine(_tempDir, "source");
        var targetDir = Path.Combine(_tempDir, "target");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(targetDir);

        File.WriteAllText(Path.Combine(sourceDir, "movie.mkv"), "video");
        File.WriteAllText(Path.Combine(sourceDir, "movie.nfo"), "metadata");
        File.WriteAllText(Path.Combine(sourceDir, "poster.jpg"), "image");

        var sourceId = Guid.NewGuid();
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>
        {
            new() { ItemId = sourceId.ToString(), Name = "Movies", Locations = new[] { sourceDir } }
        });

        var mirror = new LibraryMirror
        {
            Id = Guid.NewGuid(),
            SourceLibraryId = sourceId,
            SourceLibraryName = "Movies",
            TargetPath = targetDir,
            Status = SyncStatus.Pending
        };
        SetupMirrorConfig(mirror);

        // Act
        await _service.SyncMirrorAsync(mirror.Id);

        // Assert
        File.Exists(Path.Combine(targetDir, "movie.mkv")).Should().BeTrue("video should be hardlinked");
        File.Exists(Path.Combine(targetDir, "movie.nfo")).Should().BeFalse("NFO should NOT be hardlinked");
        File.Exists(Path.Combine(targetDir, "poster.jpg")).Should().BeFalse("images should NOT be hardlinked");
    }

    [Fact]
    public async Task SyncMirrorAsync_DeletedSourceFile_RemovedFromTarget()
    {
        // Arrange
        var sourceDir = Path.Combine(_tempDir, "source");
        var targetDir = Path.Combine(_tempDir, "target");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(targetDir);

        var sourceFile = Path.Combine(sourceDir, "movie.mkv");
        File.WriteAllText(sourceFile, "video");

        var sourceId = Guid.NewGuid();
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>
        {
            new() { ItemId = sourceId.ToString(), Name = "Movies", Locations = new[] { sourceDir } }
        });

        var mirror = new LibraryMirror
        {
            Id = Guid.NewGuid(),
            SourceLibraryId = sourceId,
            SourceLibraryName = "Movies",
            TargetPath = targetDir,
            Status = SyncStatus.Pending
        };
        SetupMirrorConfig(mirror);

        // First sync - creates hardlink
        await _service.SyncMirrorAsync(mirror.Id);
        File.Exists(Path.Combine(targetDir, "movie.mkv")).Should().BeTrue();

        // Delete source file
        File.Delete(sourceFile);

        // Second sync - should remove from target
        await _service.SyncMirrorAsync(mirror.Id);

        // Assert
        File.Exists(Path.Combine(targetDir, "movie.mkv")).Should().BeFalse("deleted file should be removed from mirror");
    }

    [Fact]
    public async Task SyncMirrorAsync_FileModifiedInSource_RecreatesHardlink()
    {
        // Arrange
        var sourceDir = Path.Combine(_tempDir, "source");
        var targetDir = Path.Combine(_tempDir, "target");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(targetDir);

        var fileName = "updated.srt";
        var initialContent = "subtitle version 1";
        var sourceFile = Path.Combine(sourceDir, fileName);
        var targetFile = Path.Combine(targetDir, fileName);

        File.WriteAllText(sourceFile, initialContent);
        File.WriteAllText(targetFile, initialContent);

        var initialTime = DateTime.UtcNow.AddHours(-1);
        File.SetLastWriteTimeUtc(sourceFile, initialTime);
        File.SetLastWriteTimeUtc(targetFile, initialTime);

        var sourceId = Guid.NewGuid();
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>
        {
            new() { ItemId = sourceId.ToString(), Name = "Movies", Locations = new[] { sourceDir } }
        });

        var mirror = new LibraryMirror
        {
            Id = Guid.NewGuid(),
            SourceLibraryId = sourceId,
            SourceLibraryName = "Movies",
            TargetPath = targetDir,
            Status = SyncStatus.Synced
        };
        SetupMirrorConfig(mirror);

        // Scenario A: Timestamp change ONLY
        File.SetLastWriteTimeUtc(sourceFile, DateTime.UtcNow);
        await _service.SyncMirrorAsync(mirror.Id);
        File.Exists(targetFile).Should().BeTrue("Target should exist after timestamp update");

        // Scenario B: Content change
        var newContent = "subtitle version 2 - significantly updated content with different length";
        File.WriteAllText(sourceFile, newContent);
        await _service.SyncMirrorAsync(mirror.Id);

        // Assert
        File.Exists(targetFile).Should().BeTrue("Target file should exist");
        File.ReadAllText(targetFile).Should().Be(newContent, "Mirror content should be updated to match source");
    }

    [Fact]
    public async Task SyncMirrorAsync_NestedDirectories_PreservesStructure()
    {
        // Arrange
        var sourceDir = Path.Combine(_tempDir, "source");
        var targetDir = Path.Combine(_tempDir, "target");
        Directory.CreateDirectory(Path.Combine(sourceDir, "Show", "Season 1"));
        Directory.CreateDirectory(targetDir);

        File.WriteAllText(Path.Combine(sourceDir, "Show", "Season 1", "episode.mkv"), "episode");

        var sourceId = Guid.NewGuid();
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>
        {
            new() { ItemId = sourceId.ToString(), Name = "TV", Locations = new[] { sourceDir } }
        });

        var mirror = new LibraryMirror
        {
            Id = Guid.NewGuid(),
            SourceLibraryId = sourceId,
            SourceLibraryName = "TV",
            TargetPath = targetDir,
            Status = SyncStatus.Pending
        };
        SetupMirrorConfig(mirror);

        // Act
        await _service.SyncMirrorAsync(mirror.Id);

        // Assert
        var expectedPath = Path.Combine(targetDir, "Show", "Season 1", "episode.mkv");
        File.Exists(expectedPath).Should().BeTrue("nested structure should be preserved");
    }

    [Fact]
    public async Task SyncMirrorAsync_ExcludedDirectories_Skipped()
    {
        // Arrange
        var sourceDir = Path.Combine(_tempDir, "source");
        var targetDir = Path.Combine(_tempDir, "target");
        Directory.CreateDirectory(Path.Combine(sourceDir, ".trickplay"));
        Directory.CreateDirectory(Path.Combine(sourceDir, "extrafanart"));
        Directory.CreateDirectory(targetDir);

        File.WriteAllText(Path.Combine(sourceDir, "movie.mkv"), "video");
        File.WriteAllText(Path.Combine(sourceDir, ".trickplay", "data.bif"), "trickplay");
        File.WriteAllText(Path.Combine(sourceDir, "extrafanart", "art.jpg"), "art");

        var sourceId = Guid.NewGuid();
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>
        {
            new() { ItemId = sourceId.ToString(), Name = "Movies", Locations = new[] { sourceDir } }
        });

        var mirror = new LibraryMirror
        {
            Id = Guid.NewGuid(),
            SourceLibraryId = sourceId,
            SourceLibraryName = "Movies",
            TargetPath = targetDir,
            Status = SyncStatus.Pending
        };
        SetupMirrorConfig(mirror);

        // Act
        await _service.SyncMirrorAsync(mirror.Id);

        // Assert
        File.Exists(Path.Combine(targetDir, "movie.mkv")).Should().BeTrue();
        Directory.Exists(Path.Combine(targetDir, ".trickplay")).Should().BeFalse("excluded directories should not be created");
        Directory.Exists(Path.Combine(targetDir, "extrafanart")).Should().BeFalse();
    }

    [Fact]
    public async Task SyncMirrorAsync_ReportsProgress()
    {
        // Arrange
        var sourceDir = Path.Combine(_tempDir, "source");
        var targetDir = Path.Combine(_tempDir, "target");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(targetDir);

        File.WriteAllText(Path.Combine(sourceDir, "movie1.mkv"), "video1");
        File.WriteAllText(Path.Combine(sourceDir, "movie2.mkv"), "video2");

        var sourceId = Guid.NewGuid();
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>
        {
            new() { ItemId = sourceId.ToString(), Name = "Movies", Locations = new[] { sourceDir } }
        });

        var mirror = new LibraryMirror
        {
            Id = Guid.NewGuid(),
            SourceLibraryId = sourceId,
            SourceLibraryName = "Movies",
            TargetPath = targetDir
        };
        SetupMirrorConfig(mirror);

        var progressValues = new List<double>();
        var progress = new SynchronousProgress<double>(v => progressValues.Add(v));

        // Act
        await _service.SyncMirrorAsync(mirror.Id, progress);

        // Assert
        progressValues.Should().NotBeEmpty("progress should be reported");
        progressValues.Should().HaveCount(2, "progress should be reported once per file");
        progressValues.Should().EndWith(100, "final progress should be 100%");
    }
}

/// <summary>
/// Synchronous implementation of IProgress that invokes the callback immediately.
/// </summary>
internal sealed class SynchronousProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;

    public SynchronousProgress(Action<T> handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public void Report(T value) => _handler(value);
}

/// <summary>
/// Tests for GetJellyfinLibraries - library information mapping.
/// </summary>
public class MirrorServiceLibraryInfoTests : IDisposable
{
    private readonly PluginTestContext _context;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IProviderManager> _providerManagerMock;
    private readonly Mock<IFileSystem> _fileSystemMock;
    private readonly Mock<IConfigurationService> _configServiceMock;
    private readonly MirrorService _service;

    public MirrorServiceLibraryInfoTests()
    {
        _context = new PluginTestContext();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _providerManagerMock = new Mock<IProviderManager>();
        _fileSystemMock = new Mock<IFileSystem>();
        _configServiceMock = TestHelpers.MockFactory.CreateConfigurationService(_context.Configuration);
        var logger = new Mock<ILogger<MirrorService>>();
        _service = new MirrorService(
            _libraryManagerMock.Object,
            _providerManagerMock.Object,
            _fileSystemMock.Object,
            _configServiceMock.Object,
            logger.Object);
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public void GetJellyfinLibraries_MapsBasicInfo()
    {
        // Arrange
        var libraryId = Guid.NewGuid();
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>
        {
            new()
            {
                ItemId = libraryId.ToString(),
                Name = "Movies",
                CollectionType = CollectionTypeOptions.movies,
                Locations = new[] { "/media/movies" },
                LibraryOptions = new MediaBrowser.Model.Configuration.LibraryOptions
                {
                    PreferredMetadataLanguage = "en",
                    MetadataCountryCode = "US"
                }
            }
        });

        // Act
        var libraries = _service.GetJellyfinLibraries().ToList();

        // Assert
        libraries.Should().ContainSingle();
        var library = libraries[0];
        library.Id.Should().Be(libraryId);
        library.Name.Should().Be("Movies");
        library.CollectionType.Should().Be("movies");
        library.PreferredMetadataLanguage.Should().Be("en");
        library.MetadataCountryCode.Should().Be("US");
    }

    [Fact]
    public void GetJellyfinLibraries_IdentifiesMirrorLibraries()
    {
        // Arrange
        var sourceId = Guid.NewGuid();
        var mirrorId = Guid.NewGuid();

        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        _context.AddMirror(alternative, sourceId, "Movies", mirrorId);

        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>
        {
            new() { ItemId = sourceId.ToString(), Name = "Movies", Locations = new[] { "/media/movies" } },
            new() { ItemId = mirrorId.ToString(), Name = "Filmes", Locations = new[] { "/media/portuguese/movies" } }
        });

        // Act
        var libraries = _service.GetJellyfinLibraries().ToList();

        // Assert
        var source = libraries.Single(l => l.Id == sourceId);
        var mirror = libraries.Single(l => l.Id == mirrorId);

        source.IsMirror.Should().BeFalse();
        mirror.IsMirror.Should().BeTrue();
        mirror.LanguageAlternativeId.Should().Be(alternative.Id);
    }
}

/// <summary>
/// Tests for library refresh behavior after mirror creation.
/// </summary>
public class MirrorServiceRefreshTests : IDisposable
{
    private readonly string _tempDir;
    private readonly PluginTestContext _context;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IProviderManager> _providerManagerMock;
    private readonly Mock<IFileSystem> _fileSystemMock;
    private readonly Mock<IConfigurationService> _configServiceMock;
    private readonly MirrorService _service;

    public MirrorServiceRefreshTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "polyglot_refresh_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _context = new PluginTestContext();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _providerManagerMock = new Mock<IProviderManager>();
        _fileSystemMock = new Mock<IFileSystem>();
        _configServiceMock = TestHelpers.MockFactory.CreateConfigurationService(_context.Configuration);
        var logger = new Mock<ILogger<MirrorService>>();
        _service = new MirrorService(
            _libraryManagerMock.Object,
            _providerManagerMock.Object,
            _fileSystemMock.Object,
            _configServiceMock.Object,
            logger.Object);
    }

    public void Dispose()
    {
        _context.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task CreateMirrorAsync_QueuesLibraryRefresh_WithFullRefreshAndLowPriority()
    {
        // Arrange
        var sourceDir = Path.Combine(_tempDir, "source");
        var targetDir = Path.Combine(_tempDir, "target");
        Directory.CreateDirectory(sourceDir);

        File.WriteAllText(Path.Combine(sourceDir, "movie.mkv"), "video content");

        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();

        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>
        {
            new()
            {
                ItemId = sourceId.ToString(),
                Name = "Movies",
                CollectionType = CollectionTypeOptions.movies,
                Locations = new[] { sourceDir },
                LibraryOptions = new MediaBrowser.Model.Configuration.LibraryOptions()
            }
        });

        _libraryManagerMock
            .Setup(m => m.AddVirtualFolder(
                It.IsAny<string>(),
                It.IsAny<CollectionTypeOptions?>(),
                It.IsAny<MediaBrowser.Model.Configuration.LibraryOptions>(),
                It.IsAny<bool>()))
            .Returns(Task.CompletedTask);

        var callCount = 0;
        _libraryManagerMock
            .Setup(m => m.GetVirtualFolders())
            .Returns(() =>
            {
                callCount++;
                var folders = new List<VirtualFolderInfo>
                {
                    new()
                    {
                        ItemId = sourceId.ToString(),
                        Name = "Movies",
                        CollectionType = CollectionTypeOptions.movies,
                        Locations = new[] { sourceDir },
                        LibraryOptions = new MediaBrowser.Model.Configuration.LibraryOptions()
                    }
                };

                if (callCount > 1)
                {
                    folders.Add(new VirtualFolderInfo
                    {
                        ItemId = targetId.ToString(),
                        Name = "Movies (Portuguese)",
                        CollectionType = CollectionTypeOptions.movies,
                        Locations = new[] { targetDir },
                        LibraryOptions = new MediaBrowser.Model.Configuration.LibraryOptions()
                    });
                }

                return folders;
            });

        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var mirror = new LibraryMirror
        {
            Id = Guid.NewGuid(),
            SourceLibraryId = sourceId,
            SourceLibraryName = "Movies",
            TargetPath = targetDir,
            TargetLibraryName = "Movies (Portuguese)",
            CollectionType = "movies",
            Status = SyncStatus.Pending
        };
        alternative.MirroredLibraries.Add(mirror);

        // Act
        await _service.CreateMirrorAsync(alternative.Id, mirror.Id);

        // Assert
        _providerManagerMock.Verify(
            m => m.QueueRefresh(
                targetId,
                It.Is<MetadataRefreshOptions>(o =>
                    o.MetadataRefreshMode == MetadataRefreshMode.FullRefresh &&
                    o.ImageRefreshMode == MetadataRefreshMode.FullRefresh &&
                    o.ReplaceAllMetadata == true &&
                    o.ReplaceAllImages == true),
                RefreshPriority.Low),
            Times.Once,
            "QueueRefresh should be called with FullRefresh options and Low priority");
    }
}

/// <summary>
/// Tests for CleanupOrphanedMirrorsAsync.
/// </summary>
public class MirrorServiceCleanupTests : IDisposable
{
    private readonly PluginTestContext _context;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IProviderManager> _providerManagerMock;
    private readonly Mock<IFileSystem> _fileSystemMock;
    private readonly Mock<IConfigurationService> _configServiceMock;
    private readonly MirrorService _service;

    public MirrorServiceCleanupTests()
    {
        _context = new PluginTestContext();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _providerManagerMock = new Mock<IProviderManager>();
        _fileSystemMock = new Mock<IFileSystem>();
        _configServiceMock = TestHelpers.MockFactory.CreateConfigurationService(_context.Configuration);
        var logger = new Mock<ILogger<MirrorService>>();
        _service = new MirrorService(
            _libraryManagerMock.Object,
            _providerManagerMock.Object,
            _fileSystemMock.Object,
            _configServiceMock.Object,
            logger.Object);
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public async Task CleanupOrphanedMirrors_TargetLibraryMissing_RemovesMirrorConfig()
    {
        // Arrange
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();

        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");
        var mirror = _context.AddMirror(
            alternative,
            sourceId,
            "Movies",
            targetLibraryId: targetId,
            targetPath: "/media/portuguese/movies");

        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>
        {
            new()
            {
                ItemId = sourceId.ToString(),
                Name = "Movies",
                CollectionType = CollectionTypeOptions.movies,
                Locations = new[] { "/media/movies" },
                LibraryOptions = new MediaBrowser.Model.Configuration.LibraryOptions()
            }
        });

        // Act
        var result = await _service.CleanupOrphanedMirrorsAsync();

        // Assert
        result.TotalCleaned.Should().Be(1);
        result.CleanedUpMirrors.Should().ContainSingle()
            .Which.Should().Contain(mirror.TargetLibraryName)
            .And.Contain("mirror deleted");

        _context.Configuration.LanguageAlternatives.First().MirroredLibraries.Should().BeEmpty("orphaned mirror config should be removed");
        result.SourcesWithoutMirrors.Should().ContainSingle().Which.Should().Be(sourceId);
    }

    [Fact]
    public async Task CleanupOrphanedMirrors_SourceLibraryMissing_DeletesMirrorFilesAndConfig()
    {
        // Arrange
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();

        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");

        var tempDir = Path.Combine(Path.GetTempPath(), "polyglot_cleanup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var mirror = _context.AddMirror(
            alternative,
            sourceId,
            "Movies",
            targetLibraryId: targetId,
            targetPath: tempDir);

        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>());

        // Act
        var result = await _service.CleanupOrphanedMirrorsAsync();

        // Assert
        result.TotalCleaned.Should().Be(1);
        result.CleanedUpMirrors.Should().ContainSingle()
            .Which.Should().Contain(mirror.TargetLibraryName)
            .And.Contain("source deleted");

        _context.Configuration.LanguageAlternatives.First().MirroredLibraries.Should().BeEmpty();
        Directory.Exists(tempDir).Should().BeFalse("mirror files should be deleted when source library is deleted");
    }

    [Fact]
    public async Task CleanupOrphanedMirrors_SourceDeleted_MirrorExists_DeletesMirrorLibraryInJellyfin()
    {
        // Arrange
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();

        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");

        var tempDir = Path.Combine(Path.GetTempPath(), "polyglot_cleanup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var mirror = _context.AddMirror(
            alternative,
            sourceId,
            "Movies",
            targetLibraryId: targetId,
            targetPath: tempDir);

        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>
        {
            new()
            {
                ItemId = targetId.ToString(),
                Name = mirror.TargetLibraryName,
                CollectionType = CollectionTypeOptions.movies,
                Locations = new[] { tempDir },
                LibraryOptions = new MediaBrowser.Model.Configuration.LibraryOptions()
            }
        });

        // Act
        var result = await _service.CleanupOrphanedMirrorsAsync();

        // Assert
        result.TotalCleaned.Should().Be(1);
        result.CleanedUpMirrors.Should().ContainSingle()
            .Which.Should().Contain(mirror.TargetLibraryName)
            .And.Contain("source deleted");

        _context.Configuration.LanguageAlternatives.First().MirroredLibraries.Should().BeEmpty();

        _libraryManagerMock.Verify(
            m => m.RemoveVirtualFolder(mirror.TargetLibraryName, true),
            Times.Once,
            "Mirror library should be deleted from Jellyfin when source library is deleted");

        Directory.Exists(tempDir).Should().BeFalse("mirror files should be deleted when source library is deleted");
    }
}
