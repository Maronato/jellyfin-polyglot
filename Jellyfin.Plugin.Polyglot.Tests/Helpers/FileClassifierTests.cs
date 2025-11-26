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
    [InlineData(".trickplay")]
    [InlineData("metadata")]
    [InlineData(".actors")]
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
        // Arrange
        var filePath = "/media/movies/Movie/.trickplay/segment/tile.bif";

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

    #region Static Property Tests

    [Fact]
    public void VideoExtensions_ContainsCommonFormats()
    {
        // Assert
        FileClassifier.VideoExtensions.Should().Contain(".mkv");
        FileClassifier.VideoExtensions.Should().Contain(".mp4");
        FileClassifier.VideoExtensions.Should().Contain(".avi");
    }

    [Fact]
    public void AudioExtensions_ContainsCommonFormats()
    {
        // Assert
        FileClassifier.AudioExtensions.Should().Contain(".mp3");
        FileClassifier.AudioExtensions.Should().Contain(".flac");
        FileClassifier.AudioExtensions.Should().Contain(".aac");
    }

    [Fact]
    public void SubtitleExtensions_ContainsCommonFormats()
    {
        // Assert
        FileClassifier.SubtitleExtensions.Should().Contain(".srt");
        FileClassifier.SubtitleExtensions.Should().Contain(".ass");
        FileClassifier.SubtitleExtensions.Should().Contain(".vtt");
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
}

