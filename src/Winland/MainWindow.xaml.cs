using System;
using System.Windows;
using System.Windows.Controls;
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
/// <see cref="NotchViewModel.MaxNotchHeight"/>) exactly once at startup and
/// never natively resized or repositioned again — expand/collapse and tab
/// switches are *purely* a Shell property Storyboard from then on.
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

    private readonly NotchViewModel _viewModel;
    private readonly KeySpline _collapseEasing = new(0.2, 0.8, 0.2, 1.0); // matches the design's CSS cubic-bezier(.2,.8,.2,1)
    private readonly KeySpline _expandOutEasing = new(0.16, 1.0, 0.3, 1.0); // fast out to the overshoot point
    private readonly KeySpline _expandSettleEasing = new(0.45, 0.0, 0.55, 1.0); // ease back down from the overshoot to the real target
    private PinTopmostService? _pinTopmostService;
    private TaskbarIcon? _trayIcon;
    private MenuItem? _showHideMenuItem;
    private bool _trayVisible = true;
    private bool _loaded;
    private nint _hwnd;
    private Point _shelfDragStartPoint;
    private bool _shelfDragCandidate;
    private bool _lastIsFullscreen;

    public MainWindow(NotchViewModel viewModel)
    {
        _viewModel = viewModel;
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
            if (e.PropertyName is nameof(NotchViewModel.IsExpanded) or nameof(NotchViewModel.SelectedTab))
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
    /// anything to observe even at startup.
    /// </summary>
    private void PositionWindowAtMaxSize()
    {
        var left = (SystemParameters.PrimaryScreenWidth - NotchViewModel.MaxNotchWidth) / 2;

        var dpi = VisualTreeHelper.GetDpi(this);
        var x = (int)Math.Round(left * dpi.DpiScaleX);
        var cx = (int)Math.Round(NotchViewModel.MaxNotchWidth * dpi.DpiScaleX);
        var cy = (int)Math.Round(NotchViewModel.MaxNotchHeight * dpi.DpiScaleY);

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
        var height = _viewModel.NotchHeight;

        if (!animate || !_loaded)
        {
            Shell.BeginAnimation(WidthProperty, null);
            Shell.BeginAnimation(HeightProperty, null);
            Shell.Width = width;
            Shell.Height = height;
            UpdateShellGeometry();
            return;
        }

        AnimateShellDimension(WidthProperty, Shell.ActualWidth, width);
        AnimateShellDimension(HeightProperty, Shell.ActualHeight, height);
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

    private void MediaTab_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _viewModel.SelectTabCommand.Execute("Media");
    }

    private void AiTab_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _viewModel.SelectTabCommand.Execute("Ai");
    }

    private void VitalsTab_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _viewModel.SelectTabCommand.Execute("Vitals");
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

        DragDrop.DoDragDrop(element, new DataObject(DataFormats.FileDrop, new[] { item.Path }), DragDropEffects.Copy | DragDropEffects.Move);
    }

    // ---- Shelf drag-in (files dropped onto the notch from Explorer/desktop) ----

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
    }

    private void Shell_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Shell_Drop(object sender, DragEventArgs e)
    {
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

        _showHideMenuItem = new MenuItem { Header = "Hide" };
        _showHideMenuItem.Click += (_, _) => ToggleTrayVisibility();

        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => Application.Current.Shutdown();

        _trayIcon.ContextMenu = new ContextMenu
        {
            Items = { _showHideMenuItem, new Separator(), exitItem },
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
