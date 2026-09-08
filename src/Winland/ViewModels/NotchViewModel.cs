using System;
using System.Collections.ObjectModel;
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
        _claudeUsageProvider = claudeUsageProvider;
        _accentColorService = accentColorService;

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

    [ObservableProperty]
    private bool isPinned = true;

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

    // ---- Vitals tab (CPU/RAM/disk/network) ----

    [ObservableProperty]
    private double cpuPercent;

    [ObservableProperty]
    private double ramPercent;

    [ObservableProperty]
    private double diskPercent;

    [ObservableProperty]
    private double networkDownKBs;

    [ObservableProperty]
    private double networkUpKBs;

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
    /// came straight from a live OS drag-and-drop payload.
    /// </summary>
    [RelayCommand]
    private void AddShelfItem(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || ShelfItems.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        ShelfItems.Add(ShelfItem.Create(path));
        PersistShelf();
    }

    [RelayCommand]
    private void RemoveShelfItem(ShelfItem item)
    {
        ShelfItems.Remove(item);
        PersistShelf();
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
            ShelfItems.Add(ShelfItem.Create(path));
        }
    }

    private void PersistShelf() => _shelfStorageService.SavePaths(ShelfItems.Select(i => i.Path));

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
