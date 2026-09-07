using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Winland.Services;

/// <summary>
/// Wraps <see cref="GlobalSystemMediaTransportControlsSessionManager"/> — the
/// same OS-level session Windows' own volume-flyout "now playing" UI uses —
/// so the Media tab reflects whatever is really playing (Spotify, a
/// browser tab, etc.) with working transport controls. No elevation or
/// package identity required.
/// </summary>
public sealed class MediaService : IMediaService, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;

    public bool HasSession { get; private set; }
    public string Title { get; private set; } = "Nothing playing";
    public string Artist { get; private set; } = string.Empty;
    public bool IsPlaying { get; private set; }
    public double Progress { get; private set; }
    public BitmapImage? Thumbnail { get; private set; }

    public event EventHandler? MediaChanged;

    public MediaService()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => _dispatcher.BeginInvoke(AttachToCurrentSession);
            AttachToCurrentSession();
        }
        catch
        {
            // Not available in this environment — tab just stays on "Nothing playing".
        }
    }

    private void AttachToCurrentSession()
    {
        DetachSession();
        _session = _manager?.GetCurrentSession();
        if (_session is null)
        {
            HasSession = false;
            RaiseChanged();
            return;
        }

        _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
        _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;

        HasSession = true;
        _ = RefreshMediaPropertiesAsync();
        RefreshPlaybackInfo();
        RefreshTimeline();
    }

    private void DetachSession()
    {
        if (_session is null)
        {
            return;
        }

        _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
        _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        _session = null;
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        => _ = RefreshMediaPropertiesAsync();

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        => _dispatcher.BeginInvoke(RefreshPlaybackInfo);

    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
        => _dispatcher.BeginInvoke(RefreshTimeline);

    private async Task RefreshMediaPropertiesAsync()
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        GlobalSystemMediaTransportControlsSessionMediaProperties props;
        try
        {
            props = await session.TryGetMediaPropertiesAsync();
        }
        catch
        {
            return;
        }

        var thumbnail = props.Thumbnail is not null ? await LoadThumbnailAsync(props.Thumbnail) : null;

        _ = _dispatcher.BeginInvoke(() =>
        {
            Title = string.IsNullOrWhiteSpace(props.Title) ? "Nothing playing" : props.Title;
            Artist = props.Artist ?? string.Empty;
            Thumbnail = thumbnail;
            RaiseChanged();
        });
    }

    private static async Task<BitmapImage?> LoadThumbnailAsync(IRandomAccessStreamReference reference)
    {
        try
        {
            using var winrtStream = await reference.OpenReadAsync();
            using var stream = winrtStream.AsStream();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            buffer.Position = 0;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = buffer;
            bitmap.EndInit();
            bitmap.Freeze(); // cross-thread safe once frozen
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private void RefreshPlaybackInfo()
    {
        if (_session is null)
        {
            return;
        }

        var info = _session.GetPlaybackInfo();
        IsPlaying = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        RaiseChanged();
    }

    private void RefreshTimeline()
    {
        if (_session is null)
        {
            return;
        }

        var timeline = _session.GetTimelineProperties();
        var span = timeline.EndTime - timeline.StartTime;
        Progress = span > TimeSpan.Zero
            ? Math.Clamp((timeline.Position - timeline.StartTime) / span, 0, 1)
            : 0;
        RaiseChanged();
    }

    private void RaiseChanged() => MediaChanged?.Invoke(this, EventArgs.Empty);

    public async Task TogglePlayPauseAsync()
    {
        if (_session is null)
        {
            return;
        }

        try { await _session.TryTogglePlayPauseAsync(); }
        catch { /* session may have gone away mid-click */ }
    }

    public async Task SkipNextAsync()
    {
        if (_session is null)
        {
            return;
        }

        try { await _session.TrySkipNextAsync(); }
        catch { }
    }

    public async Task SkipPreviousAsync()
    {
        if (_session is null)
        {
            return;
        }

        try { await _session.TrySkipPreviousAsync(); }
        catch { }
    }

    public void Dispose() => DetachSession();
}
