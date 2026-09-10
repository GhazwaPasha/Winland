using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using System.Windows.Media;
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
    public Color ThumbnailAccentColor { get; private set; } = DefaultWaveColor;

    // Matches NotchTextPrimaryBrush (App.xaml) — the collapsed waveform's
    // look before/without a thumbnail to sample a hue from.
    private static readonly Color DefaultWaveColor = Colors.White;

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
        var accentColor = thumbnail is not null ? ExtractAccentColor(thumbnail) ?? DefaultWaveColor : DefaultWaveColor;

        _ = _dispatcher.BeginInvoke(() =>
        {
            Title = string.IsNullOrWhiteSpace(props.Title) ? "Nothing playing" : props.Title;
            Artist = props.Artist ?? string.Empty;
            Thumbnail = thumbnail;
            ThumbnailAccentColor = accentColor;
            RaiseChanged();
        });
    }

    /// <summary>
    /// Samples <paramref name="source"/> for a representative hue and
    /// returns it at a fixed, generous saturation/lightness so the
    /// collapsed waveform stays legible against the notch's dark background
    /// regardless of how dark, light, or washed-out the actual album art
    /// is — the same "vibrant accent" idea as Apple Music/Android Palette,
    /// simplified to a single circular-mean hue instead of full clustering.
    /// Returns null when the source is effectively grayscale (no pixel has
    /// enough saturation to contribute a meaningful hue), so the caller can
    /// fall back to the plain white the bars used before this existed.
    /// </summary>
    private static Color? ExtractAccentColor(BitmapSource source)
    {
        try
        {
            // A handful of samples is plenty to find a dominant hue —
            // downscale first so this stays cheap even for a large thumbnail.
            const int MaxDimension = 32;
            var longestSide = Math.Max(source.PixelWidth, source.PixelHeight);
            BitmapSource scaled = source;
            if (longestSide > MaxDimension)
            {
                var scale = MaxDimension / (double)longestSide;
                scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            }

            var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
            var width = converted.PixelWidth;
            var height = converted.PixelHeight;
            if (width == 0 || height == 0)
            {
                return null;
            }

            var stride = width * 4;
            var pixels = new byte[stride * height];
            converted.CopyPixels(pixels, stride, 0);

            double sumX = 0, sumY = 0, sumWeight = 0, sumSat = 0;
            for (var i = 0; i + 3 < pixels.Length; i += 4)
            {
                var b = pixels[i];
                var g = pixels[i + 1];
                var r = pixels[i + 2];
                var a = pixels[i + 3];
                if (a < 64)
                {
                    continue;
                }

                RgbToHsl(r, g, b, out var hue, out var saturation, out var lightness);

                // Near-black/near-white pixels carry no real hue and would
                // just drag the average toward gray.
                if (lightness is < 0.08 or > 0.92)
                {
                    continue;
                }

                var weight = saturation * saturation; // favor already-vivid pixels
                if (weight < 0.0001)
                {
                    continue;
                }

                var radians = hue * Math.PI / 180.0;
                sumX += weight * Math.Cos(radians);
                sumY += weight * Math.Sin(radians);
                sumSat += weight * saturation;
                sumWeight += weight;
            }

            if (sumWeight < 0.01)
            {
                return null;
            }

            var meanHue = Math.Atan2(sumY, sumX) * 180.0 / Math.PI;
            if (meanHue < 0)
            {
                meanHue += 360;
            }

            var meanSaturation = Math.Clamp(sumSat / sumWeight, 0.55, 0.9);

            // Fixed, generous lightness — legible against the dark notch
            // panel no matter how dark/light the source art is.
            return HslToRgb(meanHue, meanSaturation, 0.62);
        }
        catch
        {
            return null;
        }
    }

    private static void RgbToHsl(byte r, byte g, byte b, out double hue, out double saturation, out double lightness)
    {
        var rf = r / 255.0;
        var gf = g / 255.0;
        var bf = b / 255.0;
        var max = Math.Max(rf, Math.Max(gf, bf));
        var min = Math.Min(rf, Math.Min(gf, bf));
        var delta = max - min;

        lightness = (max + min) / 2;
        saturation = delta == 0 ? 0 : delta / (1 - Math.Abs(2 * lightness - 1));

        if (delta == 0)
        {
            hue = 0;
        }
        else if (max == rf)
        {
            hue = 60 * (((gf - bf) / delta) % 6);
        }
        else if (max == gf)
        {
            hue = 60 * (((bf - rf) / delta) + 2);
        }
        else
        {
            hue = 60 * (((rf - gf) / delta) + 4);
        }

        if (hue < 0)
        {
            hue += 360;
        }
    }

    private static Color HslToRgb(double hue, double saturation, double lightness)
    {
        var c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var x = c * (1 - Math.Abs((hue / 60 % 2) - 1));
        var m = lightness - c / 2;

        var (r1, g1, b1) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        byte ToByte(double v) => (byte)Math.Round((v + m) * 255);
        return Color.FromRgb(ToByte(r1), ToByte(g1), ToByte(b1));
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
