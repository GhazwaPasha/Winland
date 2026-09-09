using System;
using System.Windows;
using System.Windows.Media;
using Winland.Services;
using Winland.ViewModels;

namespace Winland;

/// <summary>
/// Composition root: builds the (few) services the notch needs and the
/// single shared view model, then hands them to the main window.
/// </summary>
public partial class App : Application
{
    // The pill's original, accent-agnostic look (straight out of App.xaml)
    // — kept here as the blend base so "tinting off" restores exactly this,
    // not an approximation of it.
    private static readonly Color BasePanelColor = Color.FromArgb(0xF0, 0x20, 0x20, 0x20);
    private static readonly Color BaseStrokeColor = Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF);

    // Fixed blend strength rather than a contrast-adaptive one — the same
    // choice the taskbar's own acrylic tint makes (one recipe for every
    // accent color, not a per-color adjustment). 0.3 is the user-tuned value.
    private const double PanelTintAmount = 0.3;
    private const double StrokeTintAmount = 0.35;

    private MainWindow? _window;
    private IAccentColorService? _accentColorService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var mediaService = new MediaService();
        var privacyIndicatorService = new PrivacyIndicatorService();
        var systemVitalsService = new SystemVitalsService();
        var headphoneService = new HeadphoneService();
        var shelfStorageService = new ShelfStorageService();
        var appSettingsService = new AppSettingsService();
        var claudeCodeActivityService = new ClaudeCodeActivityService();
        var accentColorService = new AccentColorService();
        _accentColorService = accentColorService;

        accentColorService.Changed += (_, _) => Dispatcher.Invoke(ApplyAccentColors);
        ApplyAccentColors();

        var viewModel = new NotchViewModel(
            mediaService, privacyIndicatorService, systemVitalsService, headphoneService, shelfStorageService,
            appSettingsService, claudeCodeActivityService);

        _window = new MainWindow(viewModel);
        _window.Show();
    }

    /// <summary>
    /// Reproduces the same split Windows itself uses for the accent color:
    /// NotchAccentBrush (pin, progress bar, play button, status dot) always
    /// tracks the live accent, no opt-out — like the Calendar flyout's
    /// "today" marker or a Quick Settings toggle. NotchPanelBrush/
    /// NotchStrokeBrush (the pill body) only tint when the user has "Show
    /// accent color on Start and taskbar" on — exactly like the taskbar.
    ///
    /// Each call *replaces* the four resource entries with brand-new
    /// SolidColorBrush instances rather than mutating existing ones in
    /// place. Application-level resources are auto-frozen the instant
    /// they're (re-)inserted into the dictionary — even a freshly made,
    /// still-mutable clone comes back frozen the moment it's assigned to
    /// `Resources[key]` — so an in-place `.Color = ...` mutation can never
    /// work here, on the first call or any later one. MainWindow.xaml binds
    /// to these keys via DynamicResource specifically so it re-resolves to
    /// whatever the current entry is on every replacement.
    /// </summary>
    private void ApplyAccentColors()
    {
        if (_accentColorService is null)
        {
            return;
        }

        var accent = _accentColorService.Accent;

        Resources["NotchAccentBrush"] = new SolidColorBrush(accent);
        Resources["NotchOnAccentBrush"] = new SolidColorBrush(_accentColorService.IsAccentLight ? Colors.Black : Colors.White);

        var tint = _accentColorService.IsTintEnabled;
        Resources["NotchPanelBrush"] = new SolidColorBrush(tint ? Blend(BasePanelColor, accent, PanelTintAmount) : BasePanelColor);
        Resources["NotchStrokeBrush"] = new SolidColorBrush(tint ? Blend(BaseStrokeColor, accent, StrokeTintAmount) : BaseStrokeColor);
    }

    private static Color Blend(Color baseColor, Color accent, double amount)
    {
        byte Mix(byte from, byte to) => (byte)Math.Round(from + (to - from) * amount);
        return Color.FromArgb(baseColor.A, Mix(baseColor.R, accent.R), Mix(baseColor.G, accent.G), Mix(baseColor.B, accent.B));
    }
}
