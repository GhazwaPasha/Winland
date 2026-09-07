using System;
using System.Threading.Tasks;
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

    event EventHandler? MediaChanged;

    Task TogglePlayPauseAsync();
    Task SkipNextAsync();
    Task SkipPreviousAsync();
}
