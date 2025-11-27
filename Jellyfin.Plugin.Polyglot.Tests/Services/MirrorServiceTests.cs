using FluentAssertions;
using Jellyfin.Plugin.Polyglot.Models;
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
    private readonly MirrorService _service;

    public MirrorServiceValidationTests()
    {
        _libraryManagerMock = new Mock<ILibraryManager>();
        _providerManagerMock = new Mock<IProviderManager>();
        _fileSystemMock = new Mock<IFileSystem>();
        var logger = new Mock<ILogger<MirrorService>>();
        _service = new MirrorService(_libraryManagerMock.Object, _providerManagerMock.Object, _fileSystemMock.Object, logger.Object);
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
    private readonly MirrorService _service;

    public MirrorServiceFileOperationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "polyglot_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _libraryManagerMock = new Mock<ILibraryManager>();
        _providerManagerMock = new Mock<IProviderManager>();
        _fileSystemMock = new Mock<IFileSystem>();
        var logger = new Mock<ILogger<MirrorService>>();
        _service = new MirrorService(_libraryManagerMock.Object, _providerManagerMock.Object, _fileSystemMock.Object, logger.Object);
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

        // Act
        await _service.SyncMirrorAsync(mirror);

        // Assert
        var targetFile = Path.Combine(targetDir, "movie.mkv");
        File.Exists(targetFile).Should().BeTrue("video file should be hardlinked");
        File.ReadAllText(targetFile).Should().Be("video content");
        mirror.Status.Should().Be(SyncStatus.Synced);
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

        // Act
        await _service.SyncMirrorAsync(mirror);

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

        // Create initial file
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

        // First sync - creates hardlink
        await _service.SyncMirrorAsync(mirror);
        File.Exists(Path.Combine(targetDir, "movie.mkv")).Should().BeTrue();

        // Delete source file
        File.Delete(sourceFile);

        // Second sync - should remove from target
        await _service.SyncMirrorAsync(mirror);

        // Assert
        File.Exists(Path.Combine(targetDir, "movie.mkv")).Should().BeFalse("deleted file should be removed from mirror");
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

        // Act
        await _service.SyncMirrorAsync(mirror);

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

        // Act
        await _service.SyncMirrorAsync(mirror);

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

        var progressValues = new List<double>();
        // Use synchronous progress reporter instead of Progress<T> which posts callbacks asynchronously
        var progress = new SynchronousProgress<double>(v => progressValues.Add(v));

        // Act
        await _service.SyncMirrorAsync(mirror, progress);

        // Assert
        progressValues.Should().NotBeEmpty("progress should be reported");
        progressValues.Should().HaveCount(2, "progress should be reported once per file");
        progressValues.Should().EndWith(100, "final progress should be 100%");
    }
}

/// <summary>
/// Synchronous implementation of IProgress that invokes the callback immediately
/// rather than posting to a synchronization context like Progress&lt;T&gt; does.
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
    private readonly MirrorService _service;

    public MirrorServiceLibraryInfoTests()
    {
        _context = new PluginTestContext();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _providerManagerMock = new Mock<IProviderManager>();
        _fileSystemMock = new Mock<IFileSystem>();
        var logger = new Mock<ILogger<MirrorService>>();
        _service = new MirrorService(_libraryManagerMock.Object, _providerManagerMock.Object, _fileSystemMock.Object, logger.Object);
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
/// Tests for CleanupOrphanedMirrorsAsync - ensuring orphan mirrors are removed
/// when Jellyfin libraries are deleted externally (e.g., via Jellyfin's UI).
/// </summary>
public class MirrorServiceCleanupTests : IDisposable
{
    private readonly PluginTestContext _context;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IProviderManager> _providerManagerMock;
    private readonly Mock<IFileSystem> _fileSystemMock;
    private readonly MirrorService _service;

    public MirrorServiceCleanupTests()
    {
        _context = new PluginTestContext();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _providerManagerMock = new Mock<IProviderManager>();
        _fileSystemMock = new Mock<IFileSystem>();
        var logger = new Mock<ILogger<MirrorService>>();
        _service = new MirrorService(_libraryManagerMock.Object, _providerManagerMock.Object, _fileSystemMock.Object, logger.Object);
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

        // Simulate Jellyfin state: source library still exists, target library was deleted
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

        // Assert - mirror should be removed from configuration
        result.TotalCleaned.Should().Be(1);
        result.CleanedUpMirrors.Should().ContainSingle()
            .Which.Should().Contain(mirror.TargetLibraryName)
            .And.Contain("mirror deleted");

        alternative.MirroredLibraries.Should().BeEmpty("orphaned mirror config should be removed");

        // Source library still exists but now has no mirrors
        result.SourcesWithoutMirrors.Should().ContainSingle()
            .Which.Should().Be(sourceId);
    }

    [Fact]
    public async Task CleanupOrphanedMirrors_SourceLibraryMissing_DeletesMirrorFilesAndConfig()
    {
        // Arrange
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();

        var alternative = _context.AddLanguageAlternative("Portuguese", "pt-BR");

        // Use a real temp directory so we can verify deletion behavior
        var tempDir = Path.Combine(Path.GetTempPath(), "polyglot_cleanup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var mirror = _context.AddMirror(
            alternative,
            sourceId,
            "Movies",
            targetLibraryId: targetId,
            targetPath: tempDir);

        // Simulate Jellyfin state: both source & target libraries missing
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>());

        // Act
        var result = await _service.CleanupOrphanedMirrorsAsync();

        // Assert - mirror should be removed from configuration
        result.TotalCleaned.Should().Be(1);
        result.CleanedUpMirrors.Should().ContainSingle()
            .Which.Should().Contain(mirror.TargetLibraryName)
            .And.Contain("source deleted");

        alternative.MirroredLibraries.Should().BeEmpty();

        // Files for the mirror should be deleted when the source is gone
        Directory.Exists(tempDir).Should().BeFalse("mirror files should be deleted when source library is deleted");
    }
}

