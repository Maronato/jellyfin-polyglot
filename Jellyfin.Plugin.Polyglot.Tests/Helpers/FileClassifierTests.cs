using FluentAssertions;
using Jellyfin.Plugin.Polyglot.Helpers;
using Xunit;

namespace Jellyfin.Plugin.Polyglot.Tests.Helpers;

/// <summary>
/// Unit tests for the FileClassifier class.
/// Tests the file exclusion/inclusion logic for library mirroring.
/// </summary>
public class FileClassifierTests
{
    #region Video File Tests

    [Theory]
    [InlineData("/media/movies/Movie.mkv")]
    [InlineData("/media/movies/Movie.mp4")]
    [InlineData("/media/movies/Movie.avi")]
    [InlineData("/media/movies/Movie.m4v")]
    [InlineData("/media/movies/Movie.wmv")]
    [InlineData("/media/movies/Movie.mov")]
    [InlineData("/media/movies/Movie.ts")]
    [InlineData("/media/movies/Movie.m2ts")]
    [InlineData("/media/movies/Movie.flv")]
    [InlineData("/media/movies/Movie.webm")]
    [InlineData("/media/movies/Movie.mpg")]
    [InlineData("/media/movies/Movie.mpeg")]
    [InlineData("/media/movies/Movie.vob")]
    [InlineData("/media/movies/Movie.3gp")]
    public void ShouldHardlink_VideoFiles_ReturnsTrue(string filePath)
    {
        // Act
        var result = FileClassifier.ShouldHardlink(filePath);

        // Assert
        result.Should().BeTrue($"{filePath} is a video file and should be hardlinked");
    }

    #endregion

    #region Audio File Tests

    [Theory]
    [InlineData("/media/music/Song.mp3")]
    [InlineData("/media/music/Song.aac")]
    [InlineData("/media/music/Song.ac3")]
    [InlineData("/media/music/Song.dts")]
    [InlineData("/media/music/Song.flac")]
    [InlineData("/media/music/Song.m4a")]
    [InlineData("/media/music/Song.ogg")]
    [InlineData("/media/music/Song.wav")]
    [InlineData("/media/music/Song.wma")]
    [InlineData("/media/music/Song.opus")]
    [InlineData("/media/music/Song.ape")]
    [InlineData("/media/music/Song.mka")]
    public void ShouldHardlink_AudioFiles_ReturnsTrue(string filePath)
    {
        // Act
        var result = FileClassifier.ShouldHardlink(filePath);

        // Assert
        result.Should().BeTrue($"{filePath} is an audio file and should be hardlinked");
    }

    #endregion

    #region Subtitle File Tests

    [Theory]
    [InlineData("/media/movies/Movie.srt")]
    [InlineData("/media/movies/Movie.ass")]
    [InlineData("/media/movies/Movie.ssa")]
    [InlineData("/media/movies/Movie.sub")]
    [InlineData("/media/movies/Movie.idx")]
    [InlineData("/media/movies/Movie.vtt")]
    [InlineData("/media/movies/Movie.sup")]
    [InlineData("/media/movies/Movie.pgs")]
    [InlineData("/media/movies/Movie.en.srt")]
    [InlineData("/media/movies/Movie.pt-BR.srt")]
    public void ShouldHardlink_SubtitleFiles_ReturnsTrue(string filePath)
    {
        // Act
        var result = FileClassifier.ShouldHardlink(filePath);

        // Assert
        result.Should().BeTrue($"{filePath} is a subtitle file and should be hardlinked");
    }

    #endregion

    #region NFO Metadata File Tests

    [Theory]
    [InlineData("/media/movies/Movie/movie.nfo")]
    [InlineData("/media/movies/Movie/Movie.nfo")]
    [InlineData("/media/tvshows/Show/tvshow.nfo")]
    [InlineData("/media/tvshows/Show/Season 1/season.nfo")]
    [InlineData("/media/tvshows/Show/Season 1/episode.nfo")]
    public void ShouldHardlink_NfoFiles_ReturnsFalse(string filePath)
    {
        // Act
        var result = FileClassifier.ShouldHardlink(filePath);

        // Assert
        result.Should().BeFalse($"{filePath} is an NFO file and should be excluded");
    }

    #endregion

    #region Image File Tests (Artwork)

    [Theory]
    [InlineData("/media/movies/Movie/poster.jpg")]
    [InlineData("/media/movies/Movie/poster.png")]
    [InlineData("/media/movies/Movie/cover.jpg")]
    [InlineData("/media/movies/Movie/folder.jpg")]
    [InlineData("/media/movies/Movie/backdrop.jpg")]
    [InlineData("/media/movies/Movie/fanart.jpg")]
    [InlineData("/media/movies/Movie/banner.jpg")]
    [InlineData("/media/movies/Movie/logo.png")]
    [InlineData("/media/movies/Movie/thumb.jpg")]
    [InlineData("/media/movies/Movie/landscape.jpg")]
    [InlineData("/media/movies/Movie/disc.png")]
    [InlineData("/media/movies/Movie/clearart.png")]
    public void ShouldHardlink_ArtworkImages_ReturnsFalse(string filePath)
    {
        // Act
        var result = FileClassifier.ShouldHardlink(filePath);

        // Assert
        result.Should().BeFalse($"{filePath} is an artwork file and should be excluded");
    }

    [Theory]
    [InlineData("/media/movies/Movie/image.jpg")]
    [InlineData("/media/movies/Movie/screenshot.png")]
    [InlineData("/media/movies/Movie/photo.jpeg")]
    [InlineData("/media/movies/Movie/test.webp")]
    [InlineData("/media/movies/Movie/file.gif")]
    [InlineData("/media/movies/Movie/thumb.tbn")]
    public void ShouldHardlink_AllImageFiles_ReturnsFalse(string filePath)
    {
        // Act
        var result = FileClassifier.ShouldHardlink(filePath);

        // Assert
        result.Should().BeFalse($"{filePath} is an image file and should be excluded per the exclusion-based approach");
    }

    #endregion

    #region Episode Thumbnail Tests

    [Theory]
    [InlineData("/media/tvshows/Show/Season 1/episode-thumb.jpg")]
    [InlineData("/media/tvshows/Show/Season 1/S01E01-thumb.jpg")]
    [InlineData("/media/tvshows/Show/Season 1/episode-poster.jpg")]
    public void ShouldHardlink_EpisodeThumbnails_ReturnsFalse(string filePath)
    {
        // Act
        var result = FileClassifier.ShouldHardlink(filePath);

        // Assert
        result.Should().BeFalse($"{filePath} is an episode thumbnail and should be excluded");
    }

    #endregion

    #region Numbered Backdrop Tests

    [Theory]
    [InlineData("/media/movies/Movie/backdrop1.jpg")]
    [InlineData("/media/movies/Movie/backdrop-1.jpg")]
    [InlineData("/media/movies/Movie/fanart2.jpg")]
    [InlineData("/media/movies/Movie/fanart-2.jpg")]
    public void ShouldHardlink_NumberedBackdrops_ReturnsFalse(string filePath)
    {
        // Act
        var result = FileClassifier.ShouldHardlink(filePath);

        // Assert
        result.Should().BeFalse($"{filePath} is a numbered backdrop and should be excluded");
    }

    #endregion

    #region Season-Specific Image Tests

    [Theory]
    [InlineData("/media/tvshows/Show/season01-poster.jpg")]
    [InlineData("/media/tvshows/Show/season1-banner.jpg")]
    [InlineData("/media/tvshows/Show/season-all-poster.jpg")]
    public void ShouldHardlink_SeasonSpecificImages_ReturnsFalse(string filePath)
    {
        // Act
        var result = FileClassifier.ShouldHardlink(filePath);

        // Assert
        result.Should().BeFalse($"{filePath} is a season-specific image and should be excluded");
    }

    #endregion

    #region Excluded Directory Tests

    [Theory]
    [InlineData("extrafanart")]
    [InlineData("extrathumbs")]
    [InlineData("metadata")]
    public void ShouldExcludeDirectory_ExcludedDirectories_ReturnsTrue(string dirName)
    {
        // Arrange
        var directoryPath = $"/media/movies/Movie/{dirName}";

        // Act
        var result = FileClassifier.ShouldExcludeDirectory(directoryPath);

        // Assert
        result.Should().BeTrue($"{dirName} is an excluded directory");
    }

    [Theory]
    [InlineData("Movie")]
    [InlineData("Season 1")]
    [InlineData("behind the scenes")]
    [InlineData("trailers")]
    [InlineData("extras")]
    public void ShouldExcludeDirectory_NormalDirectories_ReturnsFalse(string dirName)
    {
        // Arrange
        var directoryPath = $"/media/movies/{dirName}";

        // Act
        var result = FileClassifier.ShouldExcludeDirectory(directoryPath);

        // Assert
        result.Should().BeFalse($"{dirName} is not an excluded directory");
    }

    [Fact]
    public void ShouldHardlink_FileInExcludedDirectory_ReturnsFalse()
    {
        // Arrange
        var filePath = "/media/movies/Movie/extrafanart/fanart1.mkv";

        // Act
        var result = FileClassifier.ShouldHardlink(filePath);

        // Assert
        result.Should().BeFalse("file is in an excluded directory (extrafanart)");
    }

    [Fact]
    public void ShouldHardlink_FileInNestedExcludedDirectory_ReturnsFalse()
    {
        // Arrange - using extrafanart which is still in excluded directories
        var filePath = "/media/movies/Movie/extrafanart/nested/fanart.mkv";

        // Act
        var result = FileClassifier.ShouldHardlink(filePath);

        // Assert
        result.Should().BeFalse("file is in a nested excluded directory");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void ShouldHardlink_NullPath_ReturnsFalse()
    {
        // Act
        var result = FileClassifier.ShouldHardlink(null!);

        // Assert
        result.Should().BeFalse("null path should return false");
    }

    [Fact]
    public void ShouldHardlink_EmptyPath_ReturnsFalse()
    {
        // Act
        var result = FileClassifier.ShouldHardlink(string.Empty);

        // Assert
        result.Should().BeFalse("empty path should return false");
    }

    [Fact]
    public void ShouldExcludeDirectory_NullPath_ReturnsFalse()
    {
        // Act
        var result = FileClassifier.ShouldExcludeDirectory(null!);

        // Assert
        result.Should().BeFalse("null path should return false");
    }

    [Fact]
    public void ShouldExcludeDirectory_EmptyPath_ReturnsFalse()
    {
        // Act
        var result = FileClassifier.ShouldExcludeDirectory(string.Empty);

        // Assert
        result.Should().BeFalse("empty path should return false");
    }

    [Theory]
    [InlineData("/media/movies/Movie/movie.MKV")]
    [InlineData("/media/movies/Movie/movie.Mkv")]
    public void ShouldHardlink_CaseInsensitiveExtension_ReturnsTrue(string filePath)
    {
        // Act
        var result = FileClassifier.ShouldHardlink(filePath);

        // Assert
        result.Should().BeTrue("extension matching should be case-insensitive");
    }

    [Theory]
    [InlineData("/media/movies/Movie/POSTER.JPG")]
    [InlineData("/media/movies/Movie/Poster.Jpg")]
    public void ShouldHardlink_CaseInsensitiveImageExclusion_ReturnsFalse(string filePath)
    {
        // Act
        var result = FileClassifier.ShouldHardlink(filePath);

        // Assert
        result.Should().BeFalse("image exclusion should be case-insensitive");
    }

    #endregion

    #region Extras Folder Tests

    [Theory]
    [InlineData("/media/movies/Movie/trailers/trailer.mkv")]
    [InlineData("/media/movies/Movie/behind the scenes/bts.mkv")]
    [InlineData("/media/movies/Movie/deleted scenes/deleted.mkv")]
    [InlineData("/media/movies/Movie/interviews/interview.mkv")]
    [InlineData("/media/movies/Movie/extras/extra.mkv")]
    public void ShouldHardlink_FilesInExtrasFolders_ReturnsTrue(string filePath)
    {
        // Act
        var result = FileClassifier.ShouldHardlink(filePath);

        // Assert
        result.Should().BeTrue($"{filePath} is in an extras folder but should be hardlinked");
    }

    #endregion

    #region Disc Structure Tests

    [Theory]
    [InlineData("/media/movies/Movie/VIDEO_TS/VIDEO_TS.VOB")]
    [InlineData("/media/movies/Movie/BDMV/STREAM/00000.m2ts")]
    public void ShouldHardlink_DiscStructureFiles_ReturnsTrue(string filePath)
    {
        // Act
        var result = FileClassifier.ShouldHardlink(filePath);

        // Assert
        result.Should().BeTrue($"{filePath} is a disc structure file and should be hardlinked");
    }

    #endregion

    #region Custom Extension Tests

    [Fact]
    public void ShouldHardlink_WithCustomExtensions_UsesCustomList()
    {
        // Arrange - custom list that includes .mkv as excluded
        var customExtensions = new[] { ".mkv", ".txt" };
        var filePath = "/media/movies/Movie.mkv";

        // Act
        var result = FileClassifier.ShouldHardlink(filePath, customExtensions, null);

        // Assert
        result.Should().BeFalse("custom exclusions include .mkv");
    }

    [Fact]
    public void ShouldHardlink_WithCustomExtensions_ExcludesJpgWhenRemoved()
    {
        // Arrange - custom list that does NOT include .jpg
        var customExtensions = new[] { ".nfo" };
        var filePath = "/media/movies/Movie/poster.jpg";

        // Act
        var result = FileClassifier.ShouldHardlink(filePath, customExtensions, null);

        // Assert
        result.Should().BeTrue("custom exclusions do not include .jpg");
    }

    [Fact]
    public void ShouldHardlink_WithEmptyCustomExtensions_HardlinksEverything()
    {
        // Arrange - empty custom list means nothing is excluded by extension
        var customExtensions = Array.Empty<string>();
        var filePath = "/media/movies/Movie/movie.nfo";

        // Act - pass empty for directories too so it doesn't get excluded by dir
        var result = FileClassifier.ShouldHardlink(filePath, customExtensions, Array.Empty<string>());

        // Assert
        result.Should().BeTrue("empty exclusions list means no files are excluded");
    }

    [Fact]
    public void ShouldHardlink_WithNullCustomExtensions_UsesDefaults()
    {
        // Arrange
        var filePath = "/media/movies/Movie/movie.nfo";

        // Act
        var result = FileClassifier.ShouldHardlink(filePath, null, null);

        // Assert
        result.Should().BeFalse(".nfo is in the default excluded extensions");
    }

    #endregion

    #region Custom Directory Tests

    [Fact]
    public void ShouldExcludeDirectory_WithCustomDirectories_UsesCustomList()
    {
        // Arrange - custom list that includes "custom_excluded"
        var customDirs = new[] { "custom_excluded", "another" };
        var directoryPath = "/media/movies/Movie/custom_excluded";

        // Act
        var result = FileClassifier.ShouldExcludeDirectory(directoryPath, customDirs);

        // Assert
        result.Should().BeTrue("custom exclusions include 'custom_excluded'");
    }

    [Fact]
    public void ShouldExcludeDirectory_WithCustomDirectories_DoesNotExcludeMetadataWhenRemoved()
    {
        // Arrange - custom list that does NOT include "metadata"
        var customDirs = new[] { "extrafanart" };
        var directoryPath = "/media/movies/Movie/metadata";

        // Act
        var result = FileClassifier.ShouldExcludeDirectory(directoryPath, customDirs);

        // Assert
        result.Should().BeFalse("custom exclusions do not include 'metadata'");
    }

    [Fact]
    public void ShouldExcludeDirectory_WithEmptyCustomDirectories_ExcludesNothing()
    {
        // Arrange
        var customDirs = Array.Empty<string>();
        var directoryPath = "/media/movies/Movie/metadata";

        // Act
        var result = FileClassifier.ShouldExcludeDirectory(directoryPath, customDirs);

        // Assert
        result.Should().BeFalse("empty exclusions list means no directories are excluded");
    }

    [Fact]
    public void ShouldExcludeDirectory_WithNullCustomDirectories_UsesDefaults()
    {
        // Arrange
        var directoryPath = "/media/movies/Movie/metadata";

        // Act
        var result = FileClassifier.ShouldExcludeDirectory(directoryPath, null);

        // Assert
        result.Should().BeTrue("'metadata' is in the default excluded directories");
    }

    [Fact]
    public void ShouldHardlink_WithCustomDirectories_FileInCustomExcludedDir_ReturnsFalse()
    {
        // Arrange
        var customDirs = new[] { "my_excluded_folder" };
        var filePath = "/media/movies/Movie/my_excluded_folder/video.mkv";

        // Act
        var result = FileClassifier.ShouldHardlink(filePath, null, customDirs);

        // Assert
        result.Should().BeFalse("file is in a custom excluded directory");
    }

    [Fact]
    public void ShouldHardlink_WithCustomDirectoriesRemovingMetadata_FileInMetadata_ReturnsTrue()
    {
        // Arrange - custom list that does NOT include "metadata"
        var customDirs = new[] { "extrafanart" };
        var filePath = "/media/movies/Movie/metadata/data.xml";

        // Act - also remove xml from excluded extensions
        var result = FileClassifier.ShouldHardlink(filePath, Array.Empty<string>(), customDirs);

        // Assert
        result.Should().BeTrue("custom exclusions do not include 'metadata' and no extensions are excluded");
    }

    #endregion

    #region Default Values Tests

    [Fact]
    public void DefaultExcludedExtensions_ContainsExpectedValues()
    {
        // Assert
        FileClassifier.DefaultExcludedExtensions.Should().Contain(".nfo");
        FileClassifier.DefaultExcludedExtensions.Should().Contain(".jpg");
        FileClassifier.DefaultExcludedExtensions.Should().Contain(".png");
        FileClassifier.DefaultExcludedExtensions.Should().Contain(".gif");
        FileClassifier.DefaultExcludedExtensions.Should().Contain(".webp");
        FileClassifier.DefaultExcludedExtensions.Should().Contain(".tbn");
        FileClassifier.DefaultExcludedExtensions.Should().Contain(".bmp");
        FileClassifier.DefaultExcludedExtensions.Should().Contain(".jpeg");
    }

    [Fact]
    public void DefaultExcludedDirectories_ContainsExpectedValues()
    {
        // Assert
        FileClassifier.DefaultExcludedDirectories.Should().Contain("extrafanart");
        FileClassifier.DefaultExcludedDirectories.Should().Contain("extrathumbs");
        FileClassifier.DefaultExcludedDirectories.Should().Contain("metadata");
        // .trickplay and .actors moved to included directories
        FileClassifier.DefaultExcludedDirectories.Should().NotContain(".trickplay");
        FileClassifier.DefaultExcludedDirectories.Should().NotContain(".actors");
    }

    [Fact]
    public void DefaultIncludedDirectories_ContainsExpectedValues()
    {
        // Assert - these directories contain language-independent content
        FileClassifier.DefaultIncludedDirectories.Should().Contain(".trickplay");
        FileClassifier.DefaultIncludedDirectories.Should().Contain(".actors");
    }

    #endregion

    #region Included Directory Tests

    [Theory]
    [InlineData(".trickplay")]
    [InlineData(".actors")]
    public void IsIncludedDirectory_IncludedDirectories_ReturnsTrue(string dirName)
    {
        // Arrange
        var directoryPath = $"/media/movies/Movie/{dirName}";

        // Act
        var result = FileClassifier.IsIncludedDirectory(directoryPath);

        // Assert
        result.Should().BeTrue($"{dirName} is an included directory");
    }

    [Theory]
    [InlineData("Movie")]
    [InlineData("extrafanart")]
    [InlineData("metadata")]
    public void IsIncludedDirectory_NotIncludedDirectories_ReturnsFalse(string dirName)
    {
        // Arrange
        var directoryPath = $"/media/movies/{dirName}";

        // Act
        var result = FileClassifier.IsIncludedDirectory(directoryPath);

        // Assert
        result.Should().BeFalse($"{dirName} is not an included directory");
    }

    [Theory]
    [InlineData("/media/movies/Movie/.trickplay/tile.jpg")]
    [InlineData("/media/movies/Movie/.trickplay/segment/tile.jpg")]
    [InlineData("/media/movies/Movie/.actors/actor.jpg")]
    public void ShouldHardlink_ImageFileInIncludedDirectory_ReturnsTrue(string filePath)
    {
        // Act
        var result = FileClassifier.ShouldHardlink(filePath);

        // Assert
        result.Should().BeTrue($"{filePath} is in an included directory and should be hardlinked regardless of extension");
    }

    [Fact]
    public void ShouldHardlink_FileInIncludedDirectory_BypassesExtensionExclusion()
    {
        // Arrange - .jpg would normally be excluded, but .trickplay is included
        var filePath = "/media/movies/Movie/.trickplay/preview.jpg";

        // Act
        var result = FileClassifier.ShouldHardlink(filePath);

        // Assert
        result.Should().BeTrue("files in included directories bypass extension exclusions");
    }

    [Fact]
    public void ShouldHardlink_WithCustomIncludedDirectories_UsesCustomList()
    {
        // Arrange - custom list that includes "custom_included"
        var customIncluded = new[] { "custom_included" };
        var filePath = "/media/movies/Movie/custom_included/image.jpg"; // .jpg normally excluded

        // Act
        var result = FileClassifier.ShouldHardlink(filePath, null, null, customIncluded);

        // Assert
        result.Should().BeTrue("custom included directories override extension exclusions");
    }

    [Fact]
    public void ShouldHardlink_WithEmptyIncludedDirectories_DoesNotBypassExclusions()
    {
        // Arrange - empty included list means no bypass
        var emptyIncluded = Array.Empty<string>();
        var filePath = "/media/movies/Movie/.trickplay/preview.jpg";

        // Act
        var result = FileClassifier.ShouldHardlink(filePath, null, null, emptyIncluded);

        // Assert
        result.Should().BeFalse(".jpg is excluded and included directories list is empty");
    }

    [Fact]
    public void ShouldHardlink_ExcludedDirectoryTakesPrecedenceOverIncluded()
    {
        // Arrange - if a directory is in BOTH lists, excluded wins (safety)
        var excludedDirs = new[] { "both" };
        var includedDirs = new[] { "both" };
        var filePath = "/media/movies/Movie/both/file.mkv";

        // Act
        var result = FileClassifier.ShouldHardlink(filePath, null, excludedDirs, includedDirs);

        // Assert
        result.Should().BeFalse("excluded directories take precedence over included directories");
    }

    #endregion
}

