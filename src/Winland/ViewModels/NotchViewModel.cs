using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
    private readonly IClaudeCodeActivityService _claudeCodeActivityService;
    private readonly DispatcherTimer _clockTimer;

    public NotchViewModel(
        IMediaService mediaService,
        IPrivacyIndicatorService privacyIndicatorService,
        ISystemVitalsService systemVitalsService,
        IHeadphoneService headphoneService,
        IShelfStorageService shelfStorageService,
        IAppSettingsService appSettingsService,
        IClaudeCodeActivityService claudeCodeActivityService)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _mediaService = mediaService;
        _privacyIndicatorService = privacyIndicatorService;
        _systemVitalsService = systemVitalsService;
        _headphoneService = headphoneService;
        _shelfStorageService = shelfStorageService;
        _appSettingsService = appSettingsService;
        _claudeCodeActivityService = claudeCodeActivityService;

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
        RefreshClaudeActivity();
        LoadShelf();

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _clockTimer.Tick += (_, _) =>
        {
            RefreshClock();
            RefreshClaudeActivity();
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

    // The four Vitals rings are all the same size — one shared
    // radius/circumference pair covers all of them, and each metric only
    // needs its own DashOffset.
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

    // The footprint every tab but AI shares. AI's own ceiling can run
    // taller (see MaxAiTabHeight below, and MainWindow's MeasureAiTabHeight
    // — the actual per-tab height math lives there now, not here, because
    // it needs real WPF layout measurement rather than a guessed row-height
    // constant; see that method's doc for why). The window itself is sized
    // to MaxWindowHeight, once, at startup, and never resized again — see
    // MainWindow's class doc for why. Every smaller state just animates
    // Shell within that fixed window.
    public const double MaxNotchWidth = 440;
    public const double MaxNotchHeight = 280;

    public double NotchWidth => IsExpanded ? MaxNotchWidth : 240;

    // How far past MaxNotchHeight the AI tab is allowed to grow for a long
    // session list (see MainWindow's MeasureAiTabHeight) before its session
    // list's own ScrollViewer takes back over — a safety net for a runaway
    // session count, not a value real content is expected to reach.
    public const double MaxAiTabHeight = 600;

    /// <summary>
    /// The tallest footprint any single tab can ever need when expanded —
    /// currently AI's own ceiling. This is what the real OS window reserves
    /// once at startup (see MainWindow's PositionWindowAtMaxSize); every
    /// shorter tab just leaves the rest of that reserved room empty.
    /// </summary>
    public const double MaxWindowHeight = MaxAiTabHeight;

    // Every non-AI expanded tab shares the same height, and it's
    // MaxNotchHeight — the window is already permanently reserved tall
    // enough for that (see MaxWindowHeight above), so there's no cost to
    // Shell actually using all of it. A smaller shared constant (230) used
    // to sit here instead, on the assumption every tab's content fit
    // comfortably under it; the AI tab's two ring columns and the Vitals
    // tab's rings + Download/Upload row actually run past that budget,
    // hard-clipping their bottom edge against ShellContent's per-frame Clip
    // geometry (see MainWindow's UpdateShellGeometry). Using the full
    // reserved footprint removes that clipping and, as a side effect, means
    // switching between two non-AI tabs never triggers a resize animation.
    // AI itself is the one exception — MainWindow overrides this value with
    // MeasureAiTabHeight's real measurement whenever AI is the selected,
    // expanded tab, so this is only ever actually used as the AI tab's
    // *fallback* (e.g. the very first frame, before a real measurement has
    // run).
    public double NotchHeight => !IsExpanded ? 34 : MaxNotchHeight;

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

    // ---- AI tab (Claude Code activity) ----

    /// <summary>
    /// How many Claude Code sessions are currently running (see
    /// ClaudeCodeActivityService) — this is what the status dot/text next
    /// to the Claude avatar reflects. There's no reliable signal anywhere
    /// for "is Claude actively generating a response right now" (an earlier
    /// version faked that as an always-true IsWorking bool), so this reports
    /// something real instead: whether Claude Code is open at all, and in
    /// how many places.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveClaudeSessions))]
    [NotifyPropertyChangedFor(nameof(SessionStatusText))]
    private int activeClaudeSessionCount;

    public bool HasActiveClaudeSessions => ActiveClaudeSessionCount > 0;

    public string SessionStatusText => ActiveClaudeSessionCount switch
    {
        0 => "Not running",
        1 => "1 session",
        _ => $"{ActiveClaudeSessionCount} sessions",
    };

    /// <summary>
    /// One flat row per active Claude Code session — "Project - Session",
    /// project name bold in the XAML, sorted by project so same-project
    /// rows still land next to each other even without a group header.
    /// Replaces the old Weekly/5-hour usage rings entirely (see
    /// RefreshClaudeActivity), rather than being patched in alongside them.
    /// Rebuilt wholesale on every refresh (Clear + re-Add) since this is
    /// read-only derived status, not something the UI can edit like
    /// ShelfItems — no need for the preserve-identity dance that collection
    /// needs.
    /// </summary>
    public ObservableCollection<ClaudeSessionRow> SessionRows { get; } = new();

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

    // A session whose transcript hasn't been touched in longer than this is
    // "Idle" rather than "Active" — long enough that normal think/tool-call
    // pauses between messages don't flicker a row idle mid-turn, short
    // enough that closing the loop on a conversation reads as idle again
    // within a couple of poll cycles.
    private static readonly TimeSpan ActiveThreshold = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Rebuilds the AI tab's session count and rows from scratch every
    /// call — cheap for a handful of sessions, and simpler than trying to
    /// diff the list in place for read-only derived status. Sorted by
    /// project then start time (oldest first) so same-project rows land
    /// together and the list doesn't reorder itself as sessions come and
    /// go. Each row's SessionLabel prefers the session's real title
    /// (DisplayTitle — what Claude Desktop's own sidebar shows, e.g.
    /// "Vitals tab icon styling") over the auto-derived short name,
    /// falling back only when a title hasn't been generated yet.
    /// IsActive is just LastActivityUtc thresholded — see
    /// ClaudeCodeActivityService for what that's actually measuring.
    /// </summary>
    private void RefreshClaudeActivity()
    {
        var sessions = _claudeCodeActivityService.GetSnapshot().Sessions;
        ActiveClaudeSessionCount = sessions.Count;

        var nowUtc = DateTime.UtcNow;
        SessionRows.Clear();
        foreach (var session in sessions
                     .OrderBy(s => s.ProjectName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(s => s.StartedAtUtc))
        {
            var sessionLabel = string.IsNullOrWhiteSpace(session.DisplayTitle) ? session.Name : session.DisplayTitle;
            var isInteractive = string.Equals(session.Kind, "interactive", StringComparison.OrdinalIgnoreCase);
            var isActive = nowUtc - session.LastActivityUtc < ActiveThreshold;
            SessionRows.Add(new ClaudeSessionRow(
                session.ProjectName,
                sessionLabel,
                FormatSessionDuration(nowUtc - session.StartedAtUtc),
                isActive,
                isInteractive ? null : session.Kind));
        }
    }

    private static string FormatSessionDuration(TimeSpan elapsed) => elapsed.TotalHours >= 1
        ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m"
        : $"{Math.Max(1, (int)elapsed.TotalMinutes)}m"; // never show "0m" for a session that just started

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
