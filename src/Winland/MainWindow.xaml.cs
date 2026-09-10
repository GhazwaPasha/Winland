using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using H.NotifyIcon;
using Winland.Interop;
using Winland.Models;
using Winland.Services;
using Winland.ViewModels;

namespace Winland;

/// <summary>
/// The always-on-top notch window. It is sized to its maximum possible
/// footprint (<see cref="NotchViewModel.MaxNotchWidth"/> ×
/// <see cref="NotchViewModel.MaxWindowHeight"/>) exactly once at startup and
/// never natively resized or repositioned again — expand/collapse and tab
/// switches (each expanded tab sized to its own real content height, not
/// one flat height shared by every tab — see
/// <see cref="MeasureExpandedContentHeight"/>) are *purely* a Shell
/// property Storyboard from then on.
///
/// An earlier version kept the OS window snapped to Shell's exact current
/// size instead (so there was never an invisible margin that could swallow
/// clicks meant for whatever's underneath), resizing the real window before
/// growing and after shrinking. That native resize turned out to be
/// unfixably racy: WPF applies a Window's Width/Height/Left/Top as
/// *separate*, immediate native SetWindowPos calls rather than one atomic
/// move+resize, so mid-transition the window would briefly exist at a real,
/// visible-for-an-instant combination of old-size/new-position (or vice
/// versa) — a "ghost" flicker that just changed shape and side depending on
/// which part of the sequencing was tightened. Removing the native resize
/// from the transition entirely removes every version of that race.
///
/// The tradeoff is the invisible margin the old approach was designed to
/// avoid: most of this window is transparent empty space around whatever
/// Shell currently looks like. <see cref="WndProc"/> below solves that by
/// answering WM_NCHITTEST with HTTRANSPARENT outside Shell's current
/// (possibly mid-animation) bounds, so clicks in the margin fall through to
/// whatever is underneath instead of hitting this window.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly TimeSpan ExpandDuration = TimeSpan.FromMilliseconds(340);
    private static readonly TimeSpan CollapseDuration = TimeSpan.FromMilliseconds(220);

    // How far past the target size the shell overshoots before settling,
    // as a fraction of the size delta being animated. Two keyframes (fast
    // out to the overshoot point, gentle ease back to the real target)
    // fake the over/under-shoot-then-settle read of a spring without an
    // actual physics simulation — a real notch/Dynamic-Island grows with a
    // little bounce; a single monotonic ease curve reads as "a Width
    // property tweening" instead. Only applied when growing: shrinking
    // uses a plain, quicker ease-out (_collapseEasing) with no overshoot —
    // real UI chrome closes faster and more decisively than it opens.
    private const double OvershootFraction = 0.10;

    private const int WM_NCHITTEST = 0x0084;
    private const nint HTTRANSPARENT = -1;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const nint MA_NOACTIVATE = 3;

    // Vitals ring reveal/live-update timings — see the notch design audit,
    // §07. 80ms apart (CPU -> Memory -> Disk -> GPU) rather than a fixed
    // TimeSpan.FromMilliseconds(80) literal at each call site, so the
    // stagger is one number to tune, not four.
    private static readonly TimeSpan RingRevealDuration = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RingRevealStagger = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan RingLiveUpdateDuration = TimeSpan.FromMilliseconds(300);

    // True once the Vitals tab's rings have played their empty-to-value
    // sweep for the *current* time it's been open — reset back to false the
    // moment the tab is switched away from, so re-opening it always replays
    // the reveal rather than only ever happening once per app launch. Also
    // gates the live-update path below: a CpuDashOffset change that arrives
    // before the reveal has actually started has nothing to retarget yet.
    private bool _vitalsRingsRevealed;

    private readonly NotchViewModel _viewModel;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly KeySpline _collapseEasing = new(0.2, 0.8, 0.2, 1.0); // matches the design's CSS cubic-bezier(.2,.8,.2,1)
    private readonly KeySpline _expandOutEasing = new(0.16, 1.0, 0.3, 1.0); // fast out to the overshoot point
    private readonly KeySpline _expandSettleEasing = new(0.45, 0.0, 0.55, 1.0); // ease back down from the overshoot to the real target
    private PinTopmostService? _pinTopmostService;
    private TaskbarIcon? _trayIcon;
    private MenuItem? _showHideMenuItem;
    private SettingsWindow? _settingsWindow;
    private bool _trayVisible = true;
    private bool _loaded;
    private nint _hwnd;
    private Point _shelfDragStartPoint;
    private bool _shelfDragCandidate;
    private bool _lastIsFullscreen;

    /// <summary>
    /// <paramref name="startMinimized"/> is the persisted "Start minimized"
    /// setting (SettingsWindow) — when true, this constructor still builds
    /// the window and its tray icon exactly as normal, it just never calls
    /// Show(), so WPF never creates the real HWND (and OnSourceInitialized's
    /// pin/topmost/sizing setup never runs) until the user actually reveals
    /// it from the tray. See ToggleTrayVisibility for the other half of this
    /// — _trayVisible starts false to match, so the tray menu already reads
    /// "Show" instead of "Hide".
    /// </summary>
    public MainWindow(NotchViewModel viewModel, SettingsViewModel settingsViewModel, bool startMinimized = false)
    {
        _viewModel = viewModel;
        _settingsViewModel = settingsViewModel;
        _trayVisible = !startMinimized;
        DataContext = viewModel;
        InitializeComponent();

        // Width/Height animate frame-by-frame during expand/collapse (see
        // AnimateShellDimension) — rebuilding the outline here, not just
        // after UpdateShellSize sets its target size, is what keeps the
        // flared corners glued to the pill mid-animation instead of only
        // snapping to shape once the animation finishes.
        Shell.SizeChanged += (_, _) => UpdateShellGeometry();

        _viewModel.PropertyChanged += (_, e) =>
        {
            // IsShelfTabSelected specifically, not SelectedTab and not just
            // any of the four IsXTabSelected properties — every expanded
            // tab measures its own real content height on switch (see
            // MeasureExpandedContentHeight), so this needs to fire once per
            // tab switch, after every tab panel's Visibility binding
            // (MainWindow.xaml, each bound to its own IsXTabSelected) has
            // actually updated, or it undercounts the newly-selected tab's
            // content (still Collapsed at that instant) and the notch ends
            // up too short, clipping the header against Shell's own top
            // edge — the exact bug this whole approach exists to avoid.
            // CommunityToolkit.Mvvm raises SelectedTab's own PropertyChanged
            // first, then each NotifyPropertyChangedFor target in the order
            // declared on `selectedTab` (Media, Vitals, Network, Shelf) —
            // and WPF's bindings, wired up in InitializeComponent before
            // this handler is even attached, run ahead of this handler on
            // whichever specific property each one is listening to. So
            // keying off IsShelfTabSelected — the *last* one in that list —
            // guarantees every other panel's Visibility flip already
            // happened earlier in this same synchronous notification
            // sequence, not just the newly-selected one's.
            //
            // The Shelf tab also gets live-resize below, off
            // ShelfItems.CollectionChanged rather than a PropertyChanged
            // name (it's a collection, not a property) — so dropping or
            // removing a file while Shelf is already open re-measures too,
            // not just on the next tab switch.
            if (e.PropertyName is nameof(NotchViewModel.IsExpanded) or nameof(NotchViewModel.IsShelfTabSelected))
            {
                UpdateShellSize(animate: true);
            }

            if (e.PropertyName == nameof(NotchViewModel.ShowMediaInCollapsedPill))
            {
                UpdateWaveformAnimation();
            }

            // Re-evaluate immediately rather than waiting for the next poll,
            // so toggling pin takes effect the instant it's clicked.
            if (e.PropertyName == nameof(NotchViewModel.IsPinned))
            {
                ApplyTopmostState();
            }

            if (e.PropertyName == nameof(NotchViewModel.IsVitalsTabSelected))
            {
                if (_viewModel.IsVitalsTabSelected)
                {
                    RevealVitalsRings();
                }
                else
                {
                    // Re-opening the tab later should sweep in from empty
                    // again, not just resume wherever the numbers happen to
                    // be — see _vitalsRingsRevealed's own doc comment.
                    _vitalsRingsRevealed = false;
                }
            }

            // Only while the tab is both open and past its initial reveal —
            // a DashOffset change that arrives while the tab isn't visible
            // has nothing on screen to animate, and one that arrives before
            // RevealVitalsRings has run yet would just be racing it.
            if (_viewModel.IsVitalsTabSelected && _vitalsRingsRevealed)
            {
                switch (e.PropertyName)
                {
                    case nameof(NotchViewModel.CpuDashOffset):
                        AnimateRingLiveUpdate(CpuRing, _viewModel.CpuDashOffset);
                        break;
                    case nameof(NotchViewModel.RamDashOffset):
                        AnimateRingLiveUpdate(RamRing, _viewModel.RamDashOffset);
                        break;
                    case nameof(NotchViewModel.DiskDashOffset):
                        AnimateRingLiveUpdate(DiskRing, _viewModel.DiskDashOffset);
                        break;
                    case nameof(NotchViewModel.GpuDashOffset):
                        AnimateRingLiveUpdate(GpuRing, _viewModel.GpuDashOffset);
                        break;
                }
            }

            // MediaTitle only actually changes (CommunityToolkit's
            // [ObservableProperty] setters no-op on an unchanged value) on a
            // genuine new track — MediaChanged fires far more often than
            // that (roughly once a second, from playback-position ticks),
            // but re-setting MediaTitle to the same string along the way
            // never raises this, so this only ever fires on a real track
            // change, not every tick.
            if (e.PropertyName == nameof(NotchViewModel.MediaTitle))
            {
                AnimateMediaTrackChange();
            }

            if (e.PropertyName == nameof(NotchViewModel.MediaProgress))
            {
                AnimateMediaProgress(_viewModel.MediaProgress);
            }

        };

        // Live-resize while the Shelf tab is already open — otherwise a
        // file dropped (or a chip removed) while Shelf is the visible tab
        // wouldn't be reflected in Shell's height until the next tab
        // switch happened to re-measure it.
        _viewModel.ShelfItems.CollectionChanged += (_, _) =>
        {
            if (_viewModel.IsShelfTabSelected)
            {
                UpdateShellSize(animate: true);
            }
        };

        SetupTrayIcon();
        Closed += (_, _) =>
        {
            _pinTopmostService?.Dispose();
            _trayIcon?.Dispose();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;
        ((HwndSource)PresentationSource.FromVisual(this)!).AddHook(WndProc);

        _pinTopmostService = new PinTopmostService(_hwnd, Dispatcher);
        _pinTopmostService.FullscreenStateChanged += OnFullscreenStateChanged;

        // The restored IsPinned value (loaded in NotchViewModel's own
        // constructor, which runs before this window ever attaches its
        // PropertyChanged listener) never reaches ApplyTopmostState on its
        // own — without this call the window just sits at its XAML-declared
        // Topmost="True" until the user toggles pin once themselves.
        ApplyTopmostState();

        PositionWindowAtMaxSize();
        UpdateShellSize(animate: false);
        UpdateWaveformAnimation();

        // MediaProgressBar's Value is entirely code-behind-owned now (see
        // AnimateMediaProgress) rather than a live Binding — without this,
        // whatever MediaProgress already was by the time RefreshMedia() ran
        // in NotchViewModel's constructor (well before this window, or this
        // handler, existed) would sit unreflected at the XAML-declared 0
        // until the next genuine position tick happened to arrive.
        MediaProgressBar.Value = _viewModel.MediaProgress;
        _lastMediaProgressTarget = _viewModel.MediaProgress;

        _loaded = true;
    }

    /// <summary>
    /// Two unrelated native concerns share this hook because WPF only
    /// exposes one place to intercept raw window messages per HwndSource:
    ///
    /// <list type="bullet">
    /// <item><b>WM_NCHITTEST</b> — lets clicks fall through to whatever's
    /// underneath everywhere outside Shell's current (possibly mid-
    /// animation) rendered bounds, since most of this window's fixed,
    /// maximum-size footprint is normally just transparent margin around a
    /// smaller pill/flyout. See the class doc for why the window no longer
    /// tracks Shell's size natively.</item>
    /// <item><b>WM_MOUSEACTIVATE</b> — always answers MA_NOACTIVATE, so
    /// clicking anywhere on the notch (pin, tabs, transport controls, the
    /// shelf, all of it) never activates this window — the real OS
    /// foreground window stays whatever app the user was actually using.
    /// ApplyTopmostState doesn't strictly depend on this for correctness
    /// any more (it no longer reads GetForegroundWindow() itself), but it's
    /// still the right behavior for an always-on-top overlay: clicking the
    /// pin toggle shouldn't be able to steal focus from whatever the user
    /// was doing, the same non-activating behavior Windows' own Quick
    /// Settings/volume flyouts use.</item>
    /// </list>
    /// </summary>
    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return MA_NOACTIVATE;
        }

        if (msg != WM_NCHITTEST)
        {
            return 0;
        }

        // WM_NCHITTEST packs the cursor's *screen* position (physical
        // pixels) into lParam as two signed 16-bit values.
        var raw = lParam.ToInt64();
        var screenPoint = new Point(unchecked((short)(raw & 0xFFFF)), unchecked((short)((raw >> 16) & 0xFFFF)));

        // PointFromScreen converts through this visual's DPI for us, so the
        // comparison below is in the same device-independent units as
        // ActualWidth/ActualHeight regardless of monitor scaling.
        var local = Shell.PointFromScreen(screenPoint);
        var insideShell = local.X >= 0 && local.Y >= 0 && local.X <= Shell.ActualWidth && local.Y <= Shell.ActualHeight;

        if (insideShell)
        {
            return 0;
        }

        handled = true;
        return HTTRANSPARENT;
    }

    /// <summary>
    /// Sizes and positions the real window to its maximum possible
    /// footprint, once, at startup — see the class doc. A single native
    /// SetWindowPos call combining move+resize (rather than separately
    /// setting Width/Height/Left/Top, each of which is its own immediate
    /// native call under the hood) so there's no intermediate state for
    /// anything to observe even at startup. Height reserves MaxWindowHeight
    /// — every tab's own natural content height (see
    /// <see cref="MeasureExpandedContentHeight"/>) is smaller than that in
    /// the common case, sometimes much smaller, but since this window is
    /// never natively resized again, whatever room isn't reserved here now
    /// is never available later if some tab's content ever does grow to
    /// need it.
    /// </summary>
    private void PositionWindowAtMaxSize()
    {
        // Clamped to 0 rather than left to go negative: on a primary display
        // narrower than MaxNotchWidth (rare, but possible — a small secondary
        // panel repurposed as primary, an unusual low-res remote session),
        // the unclamped centering math pushes the window left of the screen
        // origin, and since this window is never natively repositioned again
        // (see the class doc), that would leave the notch permanently
        // unreachable rather than just off-center for one session.
        var left = Math.Max(0, (SystemParameters.PrimaryScreenWidth - NotchViewModel.MaxNotchWidth) / 2);

        var dpi = VisualTreeHelper.GetDpi(this);
        var x = (int)Math.Round(left * dpi.DpiScaleX);
        var cx = (int)Math.Round(NotchViewModel.MaxNotchWidth * dpi.DpiScaleX);
        var cy = (int)Math.Round(NotchViewModel.MaxWindowHeight * dpi.DpiScaleY);

        NativeMethods.MoveAndResizeWindow(_hwnd, x, 0, cx, cy);
    }

    private void OnFullscreenStateChanged(object? sender, bool isFullscreen)
    {
        _lastIsFullscreen = isFullscreen;
        Dispatcher.BeginInvoke(ApplyTopmostState);
    }

    /// <summary>
    /// The entire rule: stay on top whenever pinned, unless the foreground
    /// window is real exclusive fullscreen (a game, a video player) — that
    /// always wins over pin, since sharing screen space with something
    /// that owns exclusive fullscreen isn't a thing the notch can do
    /// cleanly regardless of preference. Unpinned, the notch never sits
    /// above anything but the bare desktop — not a special case for
    /// "maximized" windows, just never on top, full stop.
    ///
    /// Earlier versions of this tried to classify the foreground window
    /// (normal/maximized/fullscreen) and slot the notch in relative to
    /// whichever one currently had focus. That was solving the wrong
    /// problem — maximized state and focus aren't the same thing, and
    /// dynamically re-anchoring behind a moving target broke in several
    /// subtle ways. There's no such target here: "on top" is
    /// <c>Topmost = true</c>, unambiguous; "not on top" is
    /// <see cref="NativeMethods.SendToBottom"/>, a static placement that
    /// needs no re-anchoring, because nothing else contends for the very
    /// bottom of the z-order — see its doc comment.
    /// </summary>
    private void ApplyTopmostState()
    {
        var shouldStayOnTop = _viewModel.IsPinned && !_lastIsFullscreen;

        if (shouldStayOnTop)
        {
            Topmost = true;
        }
        else
        {
            // Also clear WPF's own Topmost property here, not just the
            // native state via SendToBottom — otherwise WPF's cached DP
            // value never actually changes (SendToBottom bypasses it via a
            // raw SetWindowPos call), so a *later* `Topmost = true` looks
            // like a no-op to WPF and it silently skips re-issuing the
            // native call: unpin once, re-pin, and the notch never
            // actually comes back on top even though nothing errors.
            Topmost = false;
            NativeMethods.SendToBottom(_hwnd);
        }
    }

    /// <summary>
    /// Grows/shrinks Shell to match the current content (collapsed pill or
    /// expanded flyout), animating smoothly rather than snapping — except
    /// on the very first call (startup), which snaps directly. The real
    /// window never moves or resizes here; see the class doc.
    /// </summary>
    private void UpdateShellSize(bool animate)
    {
        var width = _viewModel.NotchWidth;
        var height = _viewModel.IsExpanded ? MeasureExpandedContentHeight() : _viewModel.NotchHeight;

        if (!animate || !_loaded)
        {
            Shell.BeginAnimation(WidthProperty, null);
            Shell.BeginAnimation(HeightProperty, null);
            Shell.Width = width;
            Shell.Height = height;
            UpdateShellGeometry();
            return;
        }

        // Tab switches (and, for Shelf, content-count changes) go
        // through this same path. Width never moves — every tab shares the
        // same 440 expanded width — but height now usually *does*: every
        // expanded tab measures its own real content height (see
        // MeasureExpandedContentHeight), so switching between two tabs with
        // different natural heights (say, Network's two speed tiles vs.
        // Shelf's file grid) animates a resize where it didn't when every
        // tab shared one flat height. Bail out entirely when neither
        // dimension is actually moving, rather than kicking off a
        // same-value animation for no visual change.
        var widthChanging = Math.Abs(Shell.ActualWidth - width) > 0.5;
        var heightChanging = Math.Abs(Shell.ActualHeight - height) > 0.5;
        if (!widthChanging && !heightChanging)
        {
            return;
        }

        if (widthChanging)
        {
            AnimateShellDimension(WidthProperty, Shell.ActualWidth, width);
        }

        if (heightChanging)
        {
            AnimateShellDimension(HeightProperty, Shell.ActualHeight, height);
        }
    }

    private void AnimateShellDimension(DependencyProperty property, double currentValue, double targetValue)
    {
        var animation = new DoubleAnimationUsingKeyFrames();

        if (targetValue > currentValue)
        {
            var overshoot = targetValue + (targetValue - currentValue) * OvershootFraction;
            var overshootTime = TimeSpan.FromMilliseconds(ExpandDuration.TotalMilliseconds * 0.7);
            animation.KeyFrames.Add(new SplineDoubleKeyFrame(overshoot, KeyTime.FromTimeSpan(overshootTime), _expandOutEasing));
            animation.KeyFrames.Add(new SplineDoubleKeyFrame(targetValue, KeyTime.FromTimeSpan(ExpandDuration), _expandSettleEasing));
        }
        else
        {
            animation.KeyFrames.Add(new SplineDoubleKeyFrame(targetValue, KeyTime.FromTimeSpan(CollapseDuration), _collapseEasing));
        }

        Shell.BeginAnimation(property, animation);
    }

    /// <summary>
    /// Whichever expanded tab is currently selected's real target height —
    /// measured directly off ExpandedPanel rather than predicted from a
    /// per-row pixel constant — every tab shared one flat MaxNotchHeight
    /// regardless of how little content it actually had, which is exactly
    /// what left a large empty gap below a short tab like Network's two
    /// speed tiles. An earlier version tried to estimate this
    /// arithmetically (chrome height + row count × an assumed row height)
    /// and it clipped in practice: real Segoe UI line heights don't match
    /// a guessed round number, so the estimate landed short of what the
    /// content actually needed.
    ///
    /// Measuring with an infinite height constraint is what makes this
    /// exact instead of another guess: WPF's Grid sizes a Star row to its
    /// content's desired size whenever the available size is infinite
    /// (the same behavior Auto rows have — Star only means "share the
    /// leftover space" once there's a finite amount of it to share), so
    /// this Measure call reports ExpandedPanel's true natural height for
    /// whichever tab is actually visible right now, including the header,
    /// the tab bar, and every margin — no separate constants to keep in
    /// sync with the XAML at all.
    ///
    /// This only touches Measure, never Arrange, so it doesn't affect
    /// what's on screen — WPF's normal layout pass re-measures
    /// ExpandedPanel for real once Shell.Width/Height (set right after
    /// this returns) actually change. Clamped to MaxExpandedContentHeight
    /// purely as a runaway-content safety net (e.g. the Shelf's file grid,
    /// if it ever holds a lot of chips); past it, that tab's own
    /// ScrollViewer (MainWindow.xaml, no MaxHeight of its own) takes back
    /// over, since it scrolls automatically the moment it's arranged with
    /// less room than its content wants. MinExpandedContentHeight is a
    /// much smaller floor purely against a pathological/empty measurement
    /// — see its own doc comment for why no real tab is expected to need
    /// it.
    /// </summary>
    private double MeasureExpandedContentHeight()
    {
        ExpandedPanel.Measure(new Size(NotchViewModel.MaxNotchWidth, double.PositiveInfinity));
        return Math.Clamp(ExpandedPanel.DesiredSize.Height, NotchViewModel.MinExpandedContentHeight, NotchViewModel.MaxExpandedContentHeight);
    }

    /// <summary>
    /// Rebuilds Shell's silhouette (<see cref="ShellFillPath"/> / <see
    /// cref="ShellStrokePath"/>) for its current size. Hooked to Shell's
    /// SizeChanged (see the constructor) rather than called only from
    /// <see cref="UpdateShellSize"/>, because Width/Height animate smoothly
    /// frame-by-frame during expand/collapse — SizeChanged fires on every
    /// one of those frames, which is what keeps the flare/corner geometry
    /// glued to the pill instead of snapping to it only at the end.
    /// </summary>
    private void UpdateShellGeometry()
    {
        var width = Shell.ActualWidth;
        var height = Shell.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        BuildShellGeometry(width, height, _viewModel.NotchCornerRadius, out var fill, out var stroke);
        ShellFillPath.Data = fill;
        ShellStrokePath.Data = stroke;
        ShellDragHighlightPath.Data = stroke;
        ShellContent.Clip = fill;
    }

    /// <summary>
    /// Builds the notch outline. The top corners flare: the flush top edge
    /// runs the full declared width, while the body (the wall running down
    /// to the bottom corner) sits a little narrower, inset by
    /// <c>topFlareRadius</c> — so the very top reads as slightly wider than
    /// the pill beneath it.
    ///
    /// Each corner is a *single* ArcSegment, not two curves joined at an
    /// inflection point like the previous version — at this size, two
    /// visibly distinct curve segments (however smoothly they're joined)
    /// read as "curved twice" rather than one graceful bend. The whole
    /// flare/taper look doesn't actually need an inflection at all: it's
    /// just an ordinary quarter-circle corner like the bottom ones already
    /// use, centered at the *un-flared* corner point (topFlareRadius, 0)
    /// (top-left) rather than the usual inside-the-fill placement — tracing
    /// the far side of that circle from (0,0) to (topFlareRadius,
    /// topFlareRadius) bulges the material out to the true edge at the very
    /// top instead of cutting the corner off, which is the entire effect.
    ///
    /// The bottom corners are unchanged in style (still a plain ArcSegment)
    /// but now round off the inset wall rather than the full width, since
    /// the wall carries that inset the rest of the way down to them.
    ///
    /// The stroke path deliberately omits the flat top edge (mirroring the
    /// old BorderThickness="1,0,1,1" — no top edge) but still traces
    /// through both corners, so the curve itself still reads as an edge.
    /// </summary>
    private static void BuildShellGeometry(double width, double height, double cornerRadius, out Geometry fill, out Geometry stroke)
    {
        var topFlareRadius = cornerRadius * 0.4; // how much narrower the body is than the flush top edge (and the top corner's own radius)

        var wallLeft = topFlareRadius;
        var wallRight = width - topFlareRadius;

        var leftFlareEnd = new Point(wallLeft, topFlareRadius);
        var rightFlareEnd = new Point(wallRight, topFlareRadius);
        var rightWallEnd = new Point(wallRight, height - cornerRadius);
        var bottomRightCorner = new Point(wallRight - cornerRadius, height);
        var bottomLeftCorner = new Point(wallLeft + cornerRadius, height);
        var leftWallEnd = new Point(wallLeft, height - cornerRadius);

        // CounterClockwise (not Clockwise, which is what the bottom corners use) —
        // these arcs need horizontal tangent at the flush top edge and vertical
        // tangent at the wall, which for these two particular endpoints only
        // comes from the circle centered at (flush-edge x, topFlareRadius), not
        // the one at (wall x, 0) that Clockwise picks. Getting this backwards is
        // exactly what caused the top edge to meet the wall in a visible kink
        // (tangent came out vertical at the corner instead of horizontal) instead
        // of a smooth curve.
        var topRightArc = new ArcSegment(rightFlareEnd, new Size(topFlareRadius, topFlareRadius), 0, isLargeArc: false, SweepDirection.Counterclockwise, isStroked: true);
        var topLeftArc = new ArcSegment(new Point(0, 0), new Size(topFlareRadius, topFlareRadius), 0, isLargeArc: false, SweepDirection.Counterclockwise, isStroked: true);

        var fillFigure = new PathFigure { StartPoint = new Point(0, 0), IsClosed = true };
        fillFigure.Segments.Add(new LineSegment(new Point(width, 0), isStroked: true));
        fillFigure.Segments.Add(topRightArc);
        fillFigure.Segments.Add(new LineSegment(rightWallEnd, isStroked: true));
        fillFigure.Segments.Add(new ArcSegment(bottomRightCorner, new Size(cornerRadius, cornerRadius), 0, isLargeArc: false, SweepDirection.Clockwise, isStroked: true));
        fillFigure.Segments.Add(new LineSegment(bottomLeftCorner, isStroked: true));
        fillFigure.Segments.Add(new ArcSegment(leftWallEnd, new Size(cornerRadius, cornerRadius), 0, isLargeArc: false, SweepDirection.Clockwise, isStroked: true));
        fillFigure.Segments.Add(new LineSegment(leftFlareEnd, isStroked: true));
        fillFigure.Segments.Add(topLeftArc);

        var fillGeometry = new PathGeometry();
        fillGeometry.Figures.Add(fillFigure);
        fillGeometry.Freeze();
        fill = fillGeometry;

        // Same outline, minus the flat top edge (the first LineSegment above).
        var strokeFigure = new PathFigure { StartPoint = new Point(width, 0), IsClosed = false };
        strokeFigure.Segments.Add(topRightArc);
        strokeFigure.Segments.Add(new LineSegment(rightWallEnd, isStroked: true));
        strokeFigure.Segments.Add(new ArcSegment(bottomRightCorner, new Size(cornerRadius, cornerRadius), 0, isLargeArc: false, SweepDirection.Clockwise, isStroked: true));
        strokeFigure.Segments.Add(new LineSegment(bottomLeftCorner, isStroked: true));
        strokeFigure.Segments.Add(new ArcSegment(leftWallEnd, new Size(cornerRadius, cornerRadius), 0, isLargeArc: false, SweepDirection.Clockwise, isStroked: true));
        strokeFigure.Segments.Add(new LineSegment(leftFlareEnd, isStroked: true));
        strokeFigure.Segments.Add(topLeftArc);

        var strokeGeometry = new PathGeometry();
        strokeGeometry.Figures.Add(strokeFigure);
        strokeGeometry.Freeze();
        stroke = strokeGeometry;
    }

    /// <summary>
    /// The Vitals tab's "rings before we show actual things" reveal (see the
    /// notch design audit, §07): each ring sweeps from empty to its current
    /// value over <see cref="RingRevealDuration"/>, staggered
    /// <see cref="RingRevealStagger"/> apart left to right (CPU, Memory,
    /// Disk, GPU) via each animation's own BeginTime. Reads the target
    /// straight off the view model's own *DashOffset properties rather than
    /// re-deriving them here, so this and the live-update path below always
    /// animate toward the exact same number the (now unbound) XAML would
    /// have shown.
    /// </summary>
    private void RevealVitalsRings()
    {
        _vitalsRingsRevealed = true;
        AnimateRingReveal(CpuRing, _viewModel.CpuDashOffset, stagger: 0);
        AnimateRingReveal(RamRing, _viewModel.RamDashOffset, stagger: 1);
        AnimateRingReveal(DiskRing, _viewModel.DiskDashOffset, stagger: 2);
        AnimateRingReveal(GpuRing, _viewModel.GpuDashOffset, stagger: 3);
    }

    private void AnimateRingReveal(Ellipse ring, double targetDashOffset, int stagger)
    {
        var animation = new DoubleAnimation
        {
            From = _viewModel.VitalsRingCircumference, // full offset = empty ring, see DoubleToDashArrayConverter's doc comment
            To = targetDashOffset,
            BeginTime = TimeSpan.FromMilliseconds(RingRevealStagger.TotalMilliseconds * stagger),
            Duration = RingRevealDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        ring.BeginAnimation(Shape.StrokeDashOffsetProperty, animation);
    }

    /// <summary>
    /// A later poll tick's new value, once the tab is already open and past
    /// its reveal — eases to the new DashOffset from wherever the ring
    /// currently sits (no explicit From: a DoubleAnimation with none set
    /// interpolates from the property's current effective value, including
    /// one still mid-animation) rather than replaying the full staggered
    /// sweep on every tick.
    /// </summary>
    private void AnimateRingLiveUpdate(Ellipse ring, double targetDashOffset)
    {
        var animation = new DoubleAnimation
        {
            To = targetDashOffset,
            Duration = RingLiveUpdateDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        ring.BeginAnimation(Shape.StrokeDashOffsetProperty, animation);
    }

    // Reveal-tier timing (see the notch design audit, §07) shared by the
    // Media-track and Network-identity transitions below.
    private static readonly TimeSpan ContentFadeInDuration = TimeSpan.FromMilliseconds(180);

    // A position tick this size or larger is a seek or a track change, not
    // organic playback — snap instead of gliding the bar across most of its
    // length. Ordinary ~1s ticks move it a fraction of a percent even on a
    // short track, so this never mistakes real playback for a jump.
    private const double MediaProgressSeekThreshold = 0.15;
    private double _lastMediaProgressTarget;

    /// <summary>
    /// Fades MediaThumbnailGrid/MediaTitleText/MediaArtistText in on a real
    /// track change — snaps to Opacity 0 first (the bound content has
    /// already changed underneath by the time this runs; there's nothing to
    /// crossfade *from*, only something to reveal) rather than animating
    /// down from 1, which would show the new track's info for the entire
    /// fade-out half before the fade-in even started.
    /// </summary>
    private void AnimateMediaTrackChange()
    {
        var animation = FadeInFromZero();
        MediaThumbnailGrid.BeginAnimation(UIElement.OpacityProperty, animation);
        MediaTitleText.BeginAnimation(UIElement.OpacityProperty, animation);
        MediaArtistText.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    private static DoubleAnimationUsingKeyFrames FadeInFromZero()
    {
        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(1, KeyTime.FromTimeSpan(ContentFadeInDuration), new KeySpline(0.2, 0.8, 0.2, 1)));
        return animation;
    }

    /// <summary>
    /// Eases MediaProgressBar toward a new position-tick value instead of
    /// the plain Binding this used to be — see MediaProgressSeekThreshold's
    /// own comment for why a large jump snaps instead of gliding.
    /// </summary>
    private void AnimateMediaProgress(double target)
    {
        var jumped = Math.Abs(target - _lastMediaProgressTarget) > MediaProgressSeekThreshold;
        _lastMediaProgressTarget = target;

        if (jumped)
        {
            MediaProgressBar.BeginAnimation(RangeBase.ValueProperty, null);
            MediaProgressBar.Value = target;
            return;
        }

        var animation = new DoubleAnimation { To = target, Duration = TimeSpan.FromMilliseconds(900) };
        MediaProgressBar.BeginAnimation(RangeBase.ValueProperty, animation);
    }

    /// <summary>
    /// Starts (or stops) the four looping bar animations behind the
    /// collapsed "now playing" waveform. Each bar gets its own out-of-phase
    /// keyframe pattern so the group reads as an equalizer reacting to
    /// audio rather than four bars pulsing in lockstep.
    ///
    /// This lives in code-behind rather than a XAML Style trigger because
    /// Storyboard.TargetName can't address a sibling element from inside a
    /// plain Style/DataTrigger (WPF only allows that inside a
    /// ControlTemplate/DataTemplate, which NameScopes their target
    /// elements) — see the comment above WaveformBars in MainWindow.xaml.
    /// </summary>
    private void UpdateWaveformAnimation()
    {
        if (_viewModel.ShowMediaInCollapsedPill)
        {
            StartWaveformBar(WaveBar1, (0.0, 5), (0.3, 13), (0.6, 7), (0.9, 5));
            StartWaveformBar(WaveBar2, (0.0, 14), (0.35, 6), (0.7, 16), (1.1, 14));
            StartWaveformBar(WaveBar3, (0.0, 9), (0.25, 16), (0.5, 6), (0.8, 9));
            StartWaveformBar(WaveBar4, (0.0, 12), (0.4, 5), (0.7, 14), (1.0, 12));
        }
        else
        {
            WaveBar1.BeginAnimation(FrameworkElement.HeightProperty, null);
            WaveBar2.BeginAnimation(FrameworkElement.HeightProperty, null);
            WaveBar3.BeginAnimation(FrameworkElement.HeightProperty, null);
            WaveBar4.BeginAnimation(FrameworkElement.HeightProperty, null);
        }
    }

    private static void StartWaveformBar(Rectangle bar, params (double Seconds, double Value)[] keyframes)
    {
        var animation = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        foreach (var (seconds, value) in keyframes)
        {
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(value, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds))));
        }

        bar.BeginAnimation(FrameworkElement.HeightProperty, animation);
    }

    // ---- Mouse handlers (WPF has no Command property on plain panels, so
    // these just call the relevant RelayCommand directly) ----

    // Toggles expand/collapse for the whole pill/flyout. Every specific
    // interactive control below (tabs, transport buttons, pin) marks the
    // event Handled so its click doesn't also bubble up here and
    // immediately re-toggle right after — mirroring the original design's
    // stopPropagation() pattern.
    private void Shell_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => _viewModel.ToggleExpandedCommand.Execute(null);

    private void PinIcon_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _viewModel.TogglePinCommand.Execute(null);
    }

    private void SettingsIcon_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenSettingsWindow();
    }

    /// <summary>
    /// Lazily creates SettingsWindow once and re-shows/activates that same
    /// instance on every later click — same single-instance idea as
    /// _trayIcon above, so repeatedly clicking the gear can't stack up
    /// several settings windows. Cleared back to null on Closed so a later
    /// click after the user closes it builds a fresh one rather than trying
    /// to resurrect a disposed window.
    /// </summary>
    private void OpenSettingsWindow()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_settingsViewModel);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void MediaTab_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _viewModel.SelectTabCommand.Execute("Media");
    }

    private void VitalsTab_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _viewModel.SelectTabCommand.Execute("Vitals");
    }

    private void NetworkTab_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _viewModel.SelectTabCommand.Execute("Network");
    }

    private void ShelfTab_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _viewModel.SelectTabCommand.Execute("Shelf");
    }

    private void RemoveShelfItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: ShelfItem item })
        {
            _viewModel.RemoveShelfItemCommand.Execute(item);
        }
    }

    // ---- Shelf drag-out (a chip dragged back onto the desktop/Explorer) ----

    private void ShelfChip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _shelfDragStartPoint = e.GetPosition(null);
        _shelfDragCandidate = true;
    }

    private void ShelfChip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_shelfDragCandidate || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = e.GetPosition(null);
        var delta = _shelfDragStartPoint - current;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _shelfDragCandidate = false;

        if (sender is not FrameworkElement { DataContext: ShelfItem item } element)
        {
            return;
        }

        // DoDragDrop blocks until the drag actually finishes (dropped
        // somewhere, or cancelled) and returns what happened — previously
        // that result was just discarded, so the only thing that ever
        // caught a chip whose file got moved out this way was the 30-second
        // ghost-prune poll (see PruneMissingShelfItems), reading as a long,
        // inconsistent delay before the chip disappeared. Removing it the
        // instant the drop is actually accepted (Copy or Move — either way
        // the user just took it off the shelf) makes that immediate instead.
        var result = DragDrop.DoDragDrop(element, new DataObject(DataFormats.FileDrop, new[] { item.Path }), DragDropEffects.Copy | DragDropEffects.Move);
        if (result != DragDropEffects.None)
        {
            _viewModel.RemoveShelfItemCommand.Execute(item);
        }
    }

    // ---- Shelf drag-in (files dropped onto the notch from Explorer/desktop) ----

    private static readonly TimeSpan DragHighlightFadeDuration = TimeSpan.FromMilliseconds(150);

    private void Shell_DragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        e.Effects = DragDropEffects.Copy;

        // Auto-reveal the Shelf tab — same "reacts to activity" idea as the
        // collapsed pill swapping to the now-playing layout — so dropping a
        // file works without navigating there manually first.
        _viewModel.IsExpanded = true;
        _viewModel.SelectTabCommand.Execute("Shelf");

        AnimateDragHighlight(visible: true);
    }

    private void Shell_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    // WPF fires DragLeave when the cursor leaves Shell's bounds mid-drag
    // without dropping (the user changed their mind, or overshot) — without
    // this the highlight would stay lit until some *later* unrelated drag
    // happened to trigger Shell_Drop's fade-out, rather than clearing right
    // when the drag actually left.
    private void Shell_DragLeave(object sender, DragEventArgs e) => AnimateDragHighlight(visible: false);

    private void AnimateDragHighlight(bool visible)
    {
        var animation = new DoubleAnimation
        {
            To = visible ? 1.0 : 0.0,
            Duration = DragHighlightFadeDuration,
        };
        ShellDragHighlightPath.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    private void Shell_Drop(object sender, DragEventArgs e)
    {
        AnimateDragHighlight(visible: false);

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return;
        }

        foreach (var path in paths)
        {
            _viewModel.AddShelfItemCommand.Execute(path);
        }
    }

    private void PlayPause_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _viewModel.MediaPlayPauseCommand.Execute(null);
    }

    private void Previous_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _viewModel.MediaPreviousCommand.Execute(null);
    }

    private void Next_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _viewModel.MediaNextCommand.Execute(null);
    }

    // ---- Tray icon ----

    private void SetupTrayIcon()
    {
        _trayIcon = new TaskbarIcon { ToolTipText = "Winland" };

        try
        {
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (exePath is not null)
            {
                _trayIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            }
        }
        catch
        {
            // Falls back to the OS default glyph — not worth failing startup over.
        }

        // Header matches _trayVisible's starting value (see the constructor's
        // startMinimized parameter) rather than always "Hide" — otherwise a
        // start-minimized launch would show a menu that says "Hide" while
        // the window is, in fact, already hidden.
        _showHideMenuItem = new MenuItem { Header = _trayVisible ? "Hide" : "Show" };
        _showHideMenuItem.Click += (_, _) => ToggleTrayVisibility();

        var settingsItem = new MenuItem { Header = "Settings" };
        settingsItem.Click += (_, _) => OpenSettingsWindow();

        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => Application.Current.Shutdown();

        _trayIcon.ContextMenu = new ContextMenu
        {
            Items = { _showHideMenuItem, settingsItem, new Separator(), exitItem },
        };

        _trayIcon.ForceCreate();
    }

    private void ToggleTrayVisibility()
    {
        _trayVisible = !_trayVisible;
        if (_trayVisible)
        {
            Show();
            if (_showHideMenuItem is not null)
            {
                _showHideMenuItem.Header = "Hide";
            }
        }
        else
        {
            Hide();
            if (_showHideMenuItem is not null)
            {
                _showHideMenuItem.Header = "Show";
            }
        }
    }
}
