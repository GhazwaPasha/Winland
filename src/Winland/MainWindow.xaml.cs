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
    private static readonly TimeSpan AnimationDuration = TimeSpan.FromMilliseconds(280);

    private const int WM_NCHITTEST = 0x0084;
    private const nint HTTRANSPARENT = -1;

    private readonly NotchViewModel _viewModel;
    private readonly KeySpline _easing = new(0.2, 0.8, 0.2, 1.0); // matches the design's CSS cubic-bezier(.2,.8,.2,1)
    private PinTopmostService? _pinTopmostService;
    private TaskbarIcon? _trayIcon;
    private MenuItem? _showHideMenuItem;
    private bool _trayVisible = true;
    private bool _loaded;
    private nint _hwnd;
    private Point _shelfDragStartPoint;
    private bool _shelfDragCandidate;
    private ForegroundWindowKind _lastForegroundWindowState = ForegroundWindowKind.Normal;

    public MainWindow(NotchViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

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

            // Pin only ever changes what happens for a *maximized* foreground
            // window (see ApplyTopmostState) — re-evaluate against the last
            // known state rather than waiting for the next poll, so toggling
            // pin while already looking at a maximized window takes effect
            // immediately.
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
        _pinTopmostService.ForegroundWindowStateChanged += OnForegroundWindowStateChanged;

        PositionWindowAtMaxSize();
        UpdateShellSize(animate: false);
        UpdateWaveformAnimation();
        _loaded = true;
    }

    /// <summary>
    /// Lets clicks fall through to whatever's underneath everywhere outside
    /// Shell's current (possibly mid-animation) rendered bounds, since most
    /// of this window's fixed, maximum-size footprint is normally just
    /// transparent margin around a smaller pill/flyout. See the class doc
    /// for why the window no longer tracks Shell's size natively.
    /// </summary>
    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
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

    private void OnForegroundWindowStateChanged(object? sender, ForegroundWindowKind state)
    {
        _lastForegroundWindowState = state;
        Dispatcher.BeginInvoke(ApplyTopmostState);
    }

    /// <summary>
    /// The notch is never hidden for any of this — it stays a real, always-
    /// "shown" window (see the class doc's HTTRANSPARENT-based click-through
    /// design) and this only moves it in or out of the topmost z-order band.
    ///
    /// Getting "covered" turned out to need more than <c>Topmost = false</c>.
    /// Confirmed live (pin-debug.log): the foreground-window classification
    /// and the Topmost assignment were both firing correctly, exactly once
    /// per real transition — yet the notch stayed visibly on top the whole
    /// time regardless. The reason is Windows' own documented behavior for
    /// HWND_NOTOPMOST (what Topmost=false uses under the hood): it places
    /// the window at the *front of the entire non-topmost band*, not
    /// wherever it'd naturally sit relative to whatever's currently
    /// focused. So dropping Topmost only ever stopped the notch from being
    /// above the topmost band — it kept rendering above the maximized/
    /// fullscreen window regardless, because it was never actually placed
    /// behind it. Explicitly inserting the notch directly behind the
    /// current foreground window's own handle (NativeMethods.PlaceBehind)
    /// is what actually achieves "covered by it".
    /// </summary>
    private void ApplyTopmostState()
    {
        var shouldStayOnTop = _lastForegroundWindowState switch
        {
            ForegroundWindowKind.Fullscreen => false,
            ForegroundWindowKind.Maximized => _viewModel.IsPinned,
            _ => true,
        };

        if (shouldStayOnTop)
        {
            Topmost = true;
        }
        else
        {
            Topmost = false;

            var fg = NativeMethods.GetForegroundWindow();
            if (fg != 0 && fg != _hwnd)
            {
                NativeMethods.PlaceBehind(_hwnd, fg);
            }
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
        var radius = _viewModel.NotchCornerRadius;

        Shell.CornerRadius = new CornerRadius(0, 0, radius, radius);

        if (!animate || !_loaded)
        {
            Shell.BeginAnimation(WidthProperty, null);
            Shell.BeginAnimation(HeightProperty, null);
            Shell.Width = width;
            Shell.Height = height;
            return;
        }

        AnimateShellDimension(WidthProperty, width);
        AnimateShellDimension(HeightProperty, height);
    }

    private void AnimateShellDimension(DependencyProperty property, double targetValue)
    {
        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(targetValue, KeyTime.FromTimeSpan(AnimationDuration), _easing));
        Shell.BeginAnimation(property, animation);
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
