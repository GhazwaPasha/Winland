using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winland.Models;
using Winland.Services;

namespace Winland.ViewModels;

/// <summary>
/// Single state/behavior hub for the notch, mirroring the prototype's
/// `state` object 1:1 (minus the Quick Settings / Apps tabs, which were
/// cut from scope). Owns the live services and marshals their background-
/// thread callbacks onto the UI thread.
/// </summary>
public partial class NotchViewModel : ObservableObject
{
    private readonly Dispatcher _dispatcher;
    private readonly IMediaService _mediaService;
    private readonly IPrivacyIndicatorService _privacyIndicatorService;
    private readonly ISystemVitalsService _systemVitalsService;
    private readonly IHeadphoneService _headphoneService;
    private readonly IShelfStorageService _shelfStorageService;
    private readonly IAppSettingsService _appSettingsService;
    private readonly DispatcherTimer _clockTimer;

    public NotchViewModel(
        IMediaService mediaService,
        IPrivacyIndicatorService privacyIndicatorService,
        ISystemVitalsService systemVitalsService,
        IHeadphoneService headphoneService,
        IShelfStorageService shelfStorageService,
        IAppSettingsService appSettingsService)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _mediaService = mediaService;
        _privacyIndicatorService = privacyIndicatorService;
        _systemVitalsService = systemVitalsService;
        _headphoneService = headphoneService;
        _shelfStorageService = shelfStorageService;
        _appSettingsService = appSettingsService;

        // Restore last session's pin state before anything else runs — this
        // assignment does trigger OnIsPinnedChanged below and re-save the
        // exact value it just loaded, a harmless no-op round-trip.
        IsPinned = _appSettingsService.Load().IsPinned;

        _mediaService.MediaChanged += (_, _) => RunOnUi(RefreshMedia);
        _privacyIndicatorService.Changed += (_, _) => RunOnUi(RefreshPrivacyIndicators);
        _systemVitalsService.Changed += (_, _) => RunOnUi(RefreshVitals);
        _headphoneService.Changed += (_, _) => RunOnUi(RefreshHeadphone);
        ShelfItems.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsShelfEmpty));

        RefreshClock();
        RefreshMedia();
        RefreshPrivacyIndicators();
        RefreshVitals();
        RefreshHeadphone();
        LoadShelf();

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _clockTimer.Tick += (_, _) =>
        {
            RefreshClock();
            PruneMissingShelfItems();
        };
        _clockTimer.Start();
    }

    // ---- Notch shell state ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotchWidth))]
    [NotifyPropertyChangedFor(nameof(NotchCornerRadius))]
    [NotifyPropertyChangedFor(nameof(IsCollapsedIconRowVisible))]
    [NotifyPropertyChangedFor(nameof(IsCollapsedMediaVisible))]
    private bool isExpanded;

    /// <summary>"Media", "Vitals", "Network" or "Shelf" — the Quick Settings / Apps segments were dropped from scope; the AI tab was removed later.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMediaTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsVitalsTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsNetworkTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsShelfTabSelected))]
    private string selectedTab = "Media";

    /// <summary>
    /// Re-check the shelf the moment the user actually looks at it, rather
    /// than waiting for the next 30-second clock tick (see PruneMissingShelfItems)
    /// — switching to a tab full of ghost chips for files moved since the
    /// last check would be a worse first impression than a same-tick prune.
    /// </summary>
    partial void OnSelectedTabChanged(string value)
    {
        if (value == "Shelf")
        {
            PruneMissingShelfItems();
        }

        UpdateNetworkSamplingState();
    }

    /// <summary>Network throughput is sampled on demand (see ISystemVitalsService.SetNetworkSamplingEnabled) — only while the Network tab is both selected and actually visible, i.e. the notch is expanded.</summary>
    partial void OnIsExpandedChanged(bool value) => UpdateNetworkSamplingState();

    private void UpdateNetworkSamplingState()
        => _systemVitalsService.SetNetworkSamplingEnabled(IsExpanded && IsNetworkTabSelected);

    [ObservableProperty]
    private bool isPinned = true;

    /// <summary>CommunityToolkit.Mvvm calls this automatically after every IsPinned change — including the initial restore-on-startup one above.</summary>
    partial void OnIsPinnedChanged(bool value) => _appSettingsService.Save(new AppSettings(value));

    [ObservableProperty]
    private string timeText = string.Empty;

    [ObservableProperty]
    private string dateText = string.Empty;

    [ObservableProperty]
    private bool isMicInUse;

    [ObservableProperty]
    private bool isCameraInUse;

    // ---- Vitals tab (CPU/RAM/disk/GPU — network moved to its own tab, see below) ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CpuDashOffset))]
    private double cpuPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RamDashOffset))]
    private double ramPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiskDashOffset))]
    private double diskPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GpuDashOffset))]
    private double gpuPercent;

    // The four Vitals rings are all the same size — one shared
    // radius/circumference pair covers all of them, and each metric only
    // needs its own DashOffset. 26 (52px diameter) rather than the original
    // 22 — with the Download/Upload row gone (see the Network tab below),
    // the rings have the row it used to share space with to grow into.
    public double VitalsRingRadius => 26;
    public double VitalsRingStrokeWidth => 5;
    public double VitalsRingCircumference => 2 * Math.PI * VitalsRingRadius / VitalsRingStrokeWidth;
    public double CpuDashOffset => VitalsRingCircumference * (1 - CpuPercent / 100.0);
    public double RamDashOffset => VitalsRingCircumference * (1 - RamPercent / 100.0);
    public double DiskDashOffset => VitalsRingCircumference * (1 - DiskPercent / 100.0);
    public double GpuDashOffset => VitalsRingCircumference * (1 - GpuPercent / 100.0);

    // ---- Network tab (live throughput) ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NetworkDownValueText))]
    [NotifyPropertyChangedFor(nameof(NetworkDownUnitText))]
    private double networkDownKBs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NetworkUpValueText))]
    [NotifyPropertyChangedFor(nameof(NetworkUpUnitText))]
    private double networkUpKBs;

    // 1024, not 1000 — SystemVitalsService's own KB/s is already a binary
    // kilobyte (bytes / 1024.0), so switching units at the binary boundary
    // is what keeps the two consistent; a decimal 1000 threshold would
    // have "999 KB/s" flip to "1.00 MB/s" one tick before the underlying
    // number actually reaches a full binary megabyte.
    private const double NetworkMBThresholdKBs = 1024;

    public string NetworkDownValueText => FormatNetworkRateValue(NetworkDownKBs);
    public string NetworkDownUnitText => FormatNetworkRateUnit(NetworkDownKBs);
    public string NetworkUpValueText => FormatNetworkRateValue(NetworkUpKBs);
    public string NetworkUpUnitText => FormatNetworkRateUnit(NetworkUpKBs);

    private static string FormatNetworkRateValue(double kbPerSecond) => kbPerSecond >= NetworkMBThresholdKBs
        ? (kbPerSecond / NetworkMBThresholdKBs).ToString("0.0", CultureInfo.InvariantCulture)
        : kbPerSecond.ToString("0.0", CultureInfo.InvariantCulture);

    private static string FormatNetworkRateUnit(double kbPerSecond) => kbPerSecond >= NetworkMBThresholdKBs ? "MB/s" : "KB/s";

    // ---- Headphone status ----

    [ObservableProperty]
    private bool isHeadphoneConnected;

    [ObservableProperty]
    private bool isHeadphoneWireless;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHeadphoneBattery))]
    private int? headphoneBatteryPercent;

    public bool HasHeadphoneBattery => HeadphoneBatteryPercent.HasValue;

    // ---- Shelf tab (persisted drag-and-drop file references) ----

    public ObservableCollection<ShelfItem> ShelfItems { get; } = new();

    public bool IsShelfEmpty => ShelfItems.Count == 0;

    public bool IsMediaTabSelected => SelectedTab == "Media";
    public bool IsVitalsTabSelected => SelectedTab == "Vitals";
    public bool IsNetworkTabSelected => SelectedTab == "Network";
    public bool IsShelfTabSelected => SelectedTab == "Shelf";

    // MaxNotchWidth: every tab shares this expanded width, always — only
    // height varies per tab. The window itself is sized to MaxWindowHeight,
    // once, at startup, and never resized again — see MainWindow's class
    // doc for why. Every smaller state just animates Shell within that
    // fixed window.
    public const double MaxNotchWidth = 440;

    public double NotchWidth => IsExpanded ? MaxNotchWidth : 240;

    // Floor and ceiling for MainWindow.MeasureExpandedContentHeight — every
    // expanded tab measures its *own* real content height now (a flat
    // MaxNotchHeight=280 for every tab regardless of content used to sit
    // here instead, which is exactly what produced a huge empty gap below
    // a short tab like Network's speed-only content; see that method's doc
    // for the measurement itself). MinExpandedContentHeight is a safety
    // floor only — comfortably below any real tab's natural minimum
    // (Network's, the shortest, measures upward of 180px even with just
    // its two speed tiles), it exists so a pathological/empty measurement
    // can't collapse the flyout to something broken-looking rather than
    // because any real tab is expected to need it.
    public const double MinExpandedContentHeight = 150;

    // How far past a tab's own natural content height any tab is still
    // allowed to grow (see MainWindow's MeasureExpandedContentHeight)
    // before its own ScrollViewer (Shelf's, if it ever holds enough chips)
    // takes back over — a safety net for runaway content, not a value real
    // content is expected to reach.
    public const double MaxExpandedContentHeight = 600;

    /// <summary>
    /// The tallest footprint any single tab can ever need when expanded —
    /// MaxExpandedContentHeight. This is what the real OS window reserves
    /// once at startup (see MainWindow's PositionWindowAtMaxSize); every
    /// shorter tab just leaves the rest of that reserved room empty
    /// (invisible margin outside Shell's own current bounds — see
    /// MainWindow's class doc for why the real window is never natively
    /// resized to match).
    /// </summary>
    public const double MaxWindowHeight = MaxExpandedContentHeight;

    /// <summary>
    /// The collapsed pill's height only — MainWindow measures every
    /// expanded tab's real content height directly off ExpandedPanel now
    /// (see MeasureExpandedContentHeight) rather than reading this for the
    /// expanded case, so this property only has one meaningful value left.
    /// </summary>
    public double NotchHeight => 34;

    public double NotchCornerRadius => IsExpanded ? 20 : 18;

    /// <summary>
    /// When true, the collapsed pill swaps its usual time/wifi/pin row for a
    /// "now playing" layout — thumbnail on one side, an animated waveform on
    /// the other — mirroring how a Dynamic Island reacts to active audio.
    /// Only while something is actually playing; a paused or absent session
    /// falls back to the normal collapsed content.
    /// </summary>
    public bool ShowMediaInCollapsedPill => HasActiveMediaSession && IsMediaPlaying;

    /// <summary>The normal collapsed row (wifi/time/pin) — hidden while <see cref="ShowMediaInCollapsedPill"/> is true.</summary>
    public bool IsCollapsedIconRowVisible => !IsExpanded && !ShowMediaInCollapsedPill;

    /// <summary>The "now playing" collapsed row (thumbnail + waveform).</summary>
    public bool IsCollapsedMediaVisible => !IsExpanded && ShowMediaInCollapsedPill;

    // ---- Media tab (real System Media Transport Controls session) ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMediaInCollapsedPill))]
    [NotifyPropertyChangedFor(nameof(IsCollapsedIconRowVisible))]
    [NotifyPropertyChangedFor(nameof(IsCollapsedMediaVisible))]
    private bool hasActiveMediaSession;

    [ObservableProperty]
    private string mediaTitle = "Nothing playing";

    [ObservableProperty]
    private string mediaArtist = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMediaInCollapsedPill))]
    [NotifyPropertyChangedFor(nameof(IsCollapsedIconRowVisible))]
    [NotifyPropertyChangedFor(nameof(IsCollapsedMediaVisible))]
    private bool isMediaPlaying;

    [ObservableProperty]
    private double mediaProgress;

    [ObservableProperty]
    private BitmapImage? mediaThumbnail;

    /// <summary>
    /// The collapsed waveform's fill — a vibrant color sampled from
    /// <see cref="MediaThumbnail"/> (see MediaService.ExtractAccentColor),
    /// so the bars draw from the album art instead of a flat white. A new
    /// frozen brush is built on every refresh rather than mutating one in
    /// place: WPF bindings only react to the bound property itself
    /// changing, not to a nested Color changing underneath an unchanged
    /// Brush instance.
    /// </summary>
    [ObservableProperty]
    private Brush waveformBrush = FrozenBrush(Colors.White);

    // ---- Commands ----

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void SelectTab(string tab) => SelectedTab = tab;

    [RelayCommand]
    private void TogglePin() => IsPinned = !IsPinned;

    [RelayCommand]
    private async Task MediaPlayPauseAsync() => await _mediaService.TogglePlayPauseAsync();

    [RelayCommand]
    private async Task MediaNextAsync() => await _mediaService.SkipNextAsync();

    [RelayCommand]
    private async Task MediaPreviousAsync() => await _mediaService.SkipPreviousAsync();

    /// <summary>
    /// Adds a dropped path to the shelf (deduped, case-insensitive) and
    /// persists. <paramref name="path"/> is trusted to exist already — it
    /// came straight from a live OS drag-and-drop payload. The chip appears
    /// immediately (<see cref="ShelfItem.CreatePending"/> — no icon yet,
    /// <see cref="ShelfItem.IsIconLoading"/> true) rather than waiting on the
    /// icon lookup, which is Shell/COM interop and can take a real moment;
    /// <see cref="LoadShelfIconAsync"/> fills the icon in once it resolves.
    /// </summary>
    [RelayCommand]
    private void AddShelfItem(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || ShelfItems.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var pending = ShelfItem.CreatePending(path);
        ShelfItems.Add(pending);
        PersistShelf();

        _ = LoadShelfIconAsync(pending);
    }

    [RelayCommand]
    private async Task RemoveShelfItemAsync(ShelfItem item) => await RemoveShelfItemWithExitAnimationAsync(item.Path);

    /// <summary>
    /// Marks the chip at <paramref name="path"/> IsRemoving (ListItemEnterStyle
    /// in MainWindow.xaml reacts with a fade+scale-out), waits out that
    /// animation's own duration, then actually removes it and persists.
    /// Re-locates by path at both ends rather than trusting a captured
    /// ShelfItem instance/index — <see cref="ShelfItem"/> is a record, and
    /// LoadShelfIconAsync can replace this exact item's record (a new
    /// instance, structurally unequal to whatever was captured at call time)
    /// with its resolved icon at any point during the 200ms this is
    /// waiting; matching by path is immune to that race the way matching by
    /// instance/structural-equality wouldn't be. Shared by
    /// RemoveShelfItemCommand and PruneMissingShelfItems so both removal
    /// paths animate the same way.
    /// </summary>
    private async Task RemoveShelfItemWithExitAnimationAsync(string path)
    {
        var index = ShelfItems.ToList().FindIndex(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return;
        }

        ShelfItems[index] = ShelfItems[index] with { IsRemoving = true };
        await Task.Delay(ListItemExitDelay);

        var current = ShelfItems.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
        if (current is not null)
        {
            ShelfItems.Remove(current);
            PersistShelf();
        }
    }

    /// <summary>
    /// Runs the actual icon/thumbnail lookup off the UI thread, then swaps
    /// the placeholder chip for a second record carrying the result — never
    /// mutates in place, since <see cref="ShelfItem"/> is an immutable
    /// record; replacing by index raises the CollectionChanged the
    /// ItemsControl needs to redraw just that one chip. If the chip was
    /// removed (e.g. dragged back out) while the lookup was still running,
    /// IndexOf comes back -1 and this is a no-op — nothing re-adds it.
    /// </summary>
    private async Task LoadShelfIconAsync(ShelfItem pending)
    {
        var icon = await Task.Run(() => ShelfItem.LoadIcon(pending.Path));

        RunOnUi(() =>
        {
            var index = ShelfItems.IndexOf(pending);
            if (index >= 0)
            {
                ShelfItems[index] = pending with { Icon = icon, IsIconLoading = false };
            }
        });
    }

    // ---- Refresh helpers ----

    private void RefreshClock()
    {
        var now = DateTime.Now;
        TimeText = now.ToString("h:mm tt");
        DateText = now.ToString("M/d/yyyy");
    }

    private void RefreshMedia()
    {
        HasActiveMediaSession = _mediaService.HasSession;
        MediaTitle = _mediaService.HasSession ? _mediaService.Title : "Nothing playing";
        MediaArtist = _mediaService.Artist;
        IsMediaPlaying = _mediaService.IsPlaying;
        MediaProgress = _mediaService.Progress;
        MediaThumbnail = _mediaService.Thumbnail;
        WaveformBrush = FrozenBrush(_mediaService.ThumbnailAccentColor);
    }

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void RefreshPrivacyIndicators()
    {
        IsMicInUse = _privacyIndicatorService.IsMicInUse;
        IsCameraInUse = _privacyIndicatorService.IsCameraInUse;
    }

    private void RefreshVitals()
    {
        var snapshot = _systemVitalsService.Snapshot;
        CpuPercent = snapshot.CpuPercent;
        RamPercent = snapshot.RamPercent;
        DiskPercent = snapshot.DiskPercent;
        GpuPercent = snapshot.GpuPercent;
        NetworkDownKBs = snapshot.NetworkDownKBs;
        NetworkUpKBs = snapshot.NetworkUpKBs;
    }

    private void RefreshHeadphone()
    {
        var snapshot = _headphoneService.Snapshot;
        IsHeadphoneConnected = snapshot.IsConnected;
        IsHeadphoneWireless = snapshot.IsWireless;
        HeadphoneBatteryPercent = snapshot.BatteryPercent;
    }

    private void LoadShelf()
    {
        foreach (var path in _shelfStorageService.LoadPaths())
        {
            var pending = ShelfItem.CreatePending(path);
            ShelfItems.Add(pending);
            _ = LoadShelfIconAsync(pending);
        }
    }

    private void PersistShelf() => _shelfStorageService.SavePaths(ShelfItems.Select(i => i.Path));

    /// <summary>
    /// Drops any shelf chip whose file no longer resolves — moved, renamed,
    /// or deleted since it was added. <see cref="ShelfStorageService"/> only
    /// ever did this re-validation once, at startup load; a chip for a file
    /// that disappeared mid-session used to just sit there as a ghost until
    /// someone noticed and hit its remove (x) button by hand. Called from
    /// the existing 30-second clock tick (a background backstop) and again
    /// the instant the Shelf tab is selected (so switching to it doesn't
    /// show stale chips for even one tick).
    /// </summary>
    private void PruneMissingShelfItems()
    {
        // !i.IsRemoving so a chip already mid-exit-animation (the user just
        // clicked its remove glyph) doesn't get a second removal kicked off
        // by the next prune tick landing before the first one finishes.
        var missingPaths = ShelfItems
            .Where(i => !i.IsRemoving && !File.Exists(i.Path) && !Directory.Exists(i.Path))
            .Select(i => i.Path)
            .ToList();

        foreach (var path in missingPaths)
        {
            _ = RemoveShelfItemWithExitAnimationAsync(path);
        }
    }

    // Matches the exit DataTrigger's Duration in ListItemEnterStyle
    // (MainWindow.xaml) — see that Style's own comment for why this can't
    // just be one shared constant. Used when a Shelf chip is removed.
    private static readonly TimeSpan ListItemExitDelay = TimeSpan.FromMilliseconds(200);

    private void RunOnUi(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action);
        }
    }
}
