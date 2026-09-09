using System;
using System.Collections.ObjectModel;
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
    private static readonly Color ClaudeColor = Color.FromArgb(255, 0xD9, 0x77, 0x57);
    private static readonly Color WarningColor = Color.FromArgb(255, 0xE8, 0xA3, 0x3E);

    private readonly Dispatcher _dispatcher;
    private readonly IMediaService _mediaService;
    private readonly IBatteryService _batteryService;
    private readonly IPrivacyIndicatorService _privacyIndicatorService;
    private readonly ISystemVitalsService _systemVitalsService;
    private readonly IHeadphoneService _headphoneService;
    private readonly IShelfStorageService _shelfStorageService;
    private readonly IAppSettingsService _appSettingsService;
    private readonly IClaudeUsageProvider _claudeUsageProvider;
    private readonly IAccentColorService _accentColorService;
    private readonly DispatcherTimer _clockTimer;

    public NotchViewModel(
        IMediaService mediaService,
        IBatteryService batteryService,
        IPrivacyIndicatorService privacyIndicatorService,
        ISystemVitalsService systemVitalsService,
        IHeadphoneService headphoneService,
        IShelfStorageService shelfStorageService,
        IAppSettingsService appSettingsService,
        IClaudeUsageProvider claudeUsageProvider,
        IAccentColorService accentColorService)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _mediaService = mediaService;
        _batteryService = batteryService;
        _privacyIndicatorService = privacyIndicatorService;
        _systemVitalsService = systemVitalsService;
        _headphoneService = headphoneService;
        _shelfStorageService = shelfStorageService;
        _appSettingsService = appSettingsService;
        _claudeUsageProvider = claudeUsageProvider;
        _accentColorService = accentColorService;

        // Restore last session's pin state before anything else runs — this
        // assignment does trigger OnIsPinnedChanged below and re-save the
        // exact value it just loaded, a harmless no-op round-trip.
        IsPinned = _appSettingsService.Load().IsPinned;

        _mediaService.MediaChanged += (_, _) => RunOnUi(RefreshMedia);
        _batteryService.BatteryChanged += (_, _) => RunOnUi(RefreshBattery);
        _privacyIndicatorService.Changed += (_, _) => RunOnUi(RefreshPrivacyIndicators);
        _systemVitalsService.Changed += (_, _) => RunOnUi(RefreshVitals);
        _headphoneService.Changed += (_, _) => RunOnUi(RefreshHeadphone);
        _accentColorService.Changed += (_, _) => RunOnUi(RefreshAccentColor);
        ShelfItems.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsShelfEmpty));

        RefreshClock();
        RefreshMedia();
        RefreshBattery();
        RefreshPrivacyIndicators();
        RefreshVitals();
        RefreshHeadphone();
        RefreshClaudeUsage();
        LoadShelf();

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _clockTimer.Tick += (_, _) =>
        {
            RefreshClock();
            RefreshClaudeUsage();
            PruneMissingShelfItems();
        };
        _clockTimer.Start();
    }

    // ---- Notch shell state ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotchWidth))]
    [NotifyPropertyChangedFor(nameof(NotchHeight))]
    [NotifyPropertyChangedFor(nameof(NotchCornerRadius))]
    [NotifyPropertyChangedFor(nameof(IsCollapsedIconRowVisible))]
    [NotifyPropertyChangedFor(nameof(IsCollapsedMediaVisible))]
    private bool isExpanded;

    /// <summary>"Media", "Ai", "Vitals" or "Shelf" — the Quick Settings / Apps segments were dropped from scope.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotchHeight))]
    [NotifyPropertyChangedFor(nameof(IsMediaTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsAiTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsVitalsTabSelected))]
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
    }

    [ObservableProperty]
    private bool isPinned = true;

    /// <summary>CommunityToolkit.Mvvm calls this automatically after every IsPinned change — including the initial restore-on-startup one above.</summary>
    partial void OnIsPinnedChanged(bool value) => _appSettingsService.Save(new AppSettings(value));

    [ObservableProperty]
    private string timeText = string.Empty;

    [ObservableProperty]
    private string dateText = string.Empty;

    [ObservableProperty]
    private int batteryPercent = 100;

    [ObservableProperty]
    private bool isMicInUse;

    [ObservableProperty]
    private bool isCameraInUse;

    // ---- Vitals tab (CPU/RAM/disk/GPU/network) ----

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

    [ObservableProperty]
    private double networkDownKBs;

    [ObservableProperty]
    private double networkUpKBs;

    // The four Vitals rings are all the same size, unlike the AI tab's two
    // differently-sized concentric rings — so one shared radius/circumference
    // pair covers all of them, and each metric only needs its own DashOffset.
    // See OuterCircumference/OuterDashOffset above for how the
    // circumference-in-stroke-thickness-units trick works.
    public double VitalsRingRadius => 22;
    public double VitalsRingStrokeWidth => 5;
    public double VitalsRingCircumference => 2 * Math.PI * VitalsRingRadius / VitalsRingStrokeWidth;
    public double CpuDashOffset => VitalsRingCircumference * (1 - CpuPercent / 100.0);
    public double RamDashOffset => VitalsRingCircumference * (1 - RamPercent / 100.0);
    public double DiskDashOffset => VitalsRingCircumference * (1 - DiskPercent / 100.0);
    public double GpuDashOffset => VitalsRingCircumference * (1 - GpuPercent / 100.0);

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
    public bool IsAiTabSelected => SelectedTab == "Ai";
    public bool IsVitalsTabSelected => SelectedTab == "Vitals";
    public bool IsShelfTabSelected => SelectedTab == "Shelf";

    // The largest footprint the notch ever takes (AI tab, expanded). The
    // window itself is sized to exactly this, once, at startup, and never
    // resized again — see MainWindow's class doc for why. Every smaller
    // state just animates Shell within that fixed window.
    public const double MaxNotchWidth = 400;
    public const double MaxNotchHeight = 280;

    public double NotchWidth => IsExpanded ? MaxNotchWidth : 220;

    public double NotchHeight => !IsExpanded
        ? 34
        : SelectedTab == "Ai" ? MaxNotchHeight : 230;

    public double NotchCornerRadius => IsExpanded ? 20 : 18;

    /// <summary>
    /// When true, the collapsed pill swaps its usual time/battery/wifi/pin
    /// row for a "now playing" layout — thumbnail on one side, an animated
    /// waveform on the other — mirroring how a Dynamic Island reacts to
    /// active audio. Only while something is actually playing; a paused or
    /// absent session falls back to the normal collapsed content.
    /// </summary>
    public bool ShowMediaInCollapsedPill => HasActiveMediaSession && IsMediaPlaying;

    /// <summary>The normal collapsed row (wifi/battery/time/pin) — hidden while <see cref="ShowMediaInCollapsedPill"/> is true.</summary>
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

    // ---- AI tab (mocked Claude usage) ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OuterDashOffset))]
    [NotifyPropertyChangedFor(nameof(WeeklyRingBrush))]
    private int weeklyPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InnerDashOffset))]
    [NotifyPropertyChangedFor(nameof(FiveHourRingBrush))]
    private int fiveHourPercent;

    [ObservableProperty]
    private string weeklyResetText = string.Empty;

    [ObservableProperty]
    private string fiveHourResetText = string.Empty;

    [ObservableProperty]
    private bool isClaudeWorking;

    public double OuterRadius => 40;
    public double InnerRadius => 27;
    public double RingStrokeWidth => 7;

    // WPF's Shape.StrokeDashArray/StrokeDashOffset are expressed in
    // multiples of StrokeThickness (unlike SVG's stroke-dasharray, which is
    // in the same absolute units as the path) — dividing by RingStrokeWidth
    // converts the circle's circumference into that unit so the same
    // "one dash the length of the whole circle, offset to reveal pct%"
    // trick the prototype uses in SVG reproduces identically here.
    public double OuterCircumference => 2 * Math.PI * OuterRadius / RingStrokeWidth;
    public double InnerCircumference => 2 * Math.PI * InnerRadius / RingStrokeWidth;
    public double OuterDashOffset => OuterCircumference * (1 - WeeklyPercent / 100.0);
    public double InnerDashOffset => InnerCircumference * (1 - FiveHourPercent / 100.0);
    public Brush WeeklyRingBrush => new SolidColorBrush(WeeklyPercent >= 85 ? WarningColor : _accentColorService.Accent);
    public Brush FiveHourRingBrush => new SolidColorBrush(FiveHourPercent >= 85 ? WarningColor : ClaudeColor);

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
    private void RemoveShelfItem(ShelfItem item)
    {
        ShelfItems.Remove(item);
        PersistShelf();
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
    }

    private void RefreshBattery() => BatteryPercent = _batteryService.CurrentPercent;

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
        var missing = ShelfItems.Where(i => !File.Exists(i.Path) && !Directory.Exists(i.Path)).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        foreach (var item in missing)
        {
            ShelfItems.Remove(item);
        }

        PersistShelf();
    }

    // WeeklyRingBrush reads _accentColorService.Accent directly rather than
    // through an [ObservableProperty], so a live accent change needs an
    // explicit nudge — same trick NotifyPropertyChangedFor already uses for
    // WeeklyPercent driving the same property.
    private void RefreshAccentColor() => OnPropertyChanged(nameof(WeeklyRingBrush));

    private void RefreshClaudeUsage()
    {
        var snapshot = _claudeUsageProvider.GetSnapshot();
        WeeklyPercent = snapshot.WeeklyPercent;
        FiveHourPercent = snapshot.FiveHourPercent;
        WeeklyResetText = snapshot.WeeklyResetText;
        FiveHourResetText = snapshot.FiveHourResetText;
        IsClaudeWorking = snapshot.IsWorking;
    }

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
