using System;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Winland.Services;

/// <summary>Real system-wide "now playing" via Windows' System Media Transport Controls.</summary>
public interface IMediaService
{
    bool HasSession { get; }
    string Title { get; }
    string Artist { get; }
    bool IsPlaying { get; }
    double Progress { get; }
    BitmapImage? Thumbnail { get; }

    /// <summary>
    /// A vibrant color sampled from <see cref="Thumbnail"/>'s dominant hue,
    /// tuned for legibility against the notch's dark background — falls
    /// back to white when there's no thumbnail or it's effectively
    /// grayscale (nothing meaningful to sample a hue from).
    /// </summary>
    Color ThumbnailAccentColor { get; }

    event EventHandler? MediaChanged;

    Task TogglePlayPauseAsync();
    Task SkipNextAsync();
    Task SkipPreviousAsync();
}
