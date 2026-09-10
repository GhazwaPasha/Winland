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

    // Overrides the usual tint/accent panel logic entirely once on — see
    // ApplyAccentColors. Mirrors the settings window's own BlackMode
    // property (SettingsViewModel), kept in sync by the PropertyChanged
    // subscription below rather than read from settingsViewModel directly
    // on every ApplyAccentColors call, since ApplyAccentColors also fires
    // from AccentColorService.Changed — a live OS accent-change callback
    // that has no view model in scope at all.
    private bool _blackModeEnabled;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var mediaService = new MediaService();
        var privacyIndicatorService = new PrivacyIndicatorService();
        var systemVitalsService = new SystemVitalsService();
        var headphoneService = new HeadphoneService();
        var shelfStorageService = new ShelfStorageService();
        var appSettingsService = new AppSettingsService();
        var accentColorService = new AccentColorService();
        var startupService = new StartupService();
        _accentColorService = accentColorService;

        // Loaded once, up front, rather than separately later — both the
        // panel's initial black-mode state below and the startup/minimized
        // wiring further down need it, and it's a cheap, side-effect-free
        // file read either way.
        var settings = appSettingsService.Load();
        _blackModeEnabled = settings.BlackMode;

        accentColorService.Changed += (_, _) => Dispatcher.Invoke(ApplyAccentColors);
        ApplyAccentColors();

        var viewModel = new NotchViewModel(
            mediaService, privacyIndicatorService, systemVitalsService, headphoneService, shelfStorageService,
            appSettingsService);
        var settingsViewModel = new SettingsViewModel(appSettingsService, startupService);

        // Re-applies the panel brush the instant Black Mode is toggled in
        // SettingsWindow, rather than only on the next launch — the same
        // "takes effect immediately" expectation IsPinned already meets.
        settingsViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SettingsViewModel.BlackMode))
            {
                _blackModeEnabled = settingsViewModel.BlackMode;
                ApplyAccentColors();
            }
        };

        // Re-applies the saved "start at startup" preference on every launch,
        // not just the first time it's toggled on — see StartupService's own
        // doc comment for why (this app is a self-contained, per-architecture
        // exe, so its own path can move between updates/reinstalls).
        startupService.SetEnabled(settings.StartAtStartup);

        _window = new MainWindow(viewModel, settingsViewModel, settings.StartMinimized);
        if (!settings.StartMinimized)
        {
            _window.Show();
        }
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
    ///
    /// NotchAccentBrush/NotchOnAccentBrush are untouched by Black Mode —
    /// same reasoning as the tint opt-out already gives them (see the class
    /// doc): the pin/progress/status-dot accent is a small-control signal
    /// that always stays live and visible, regardless of what's going on
    /// with the pill body's own background. Only NotchPanelBrush — the
    /// pill's fill — actually goes solid black; NotchStrokeBrush is left
    /// alone too, since its border is subtle enough to still read as "solid
    /// black with a hairline edge" rather than fighting the effect.
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

        if (_blackModeEnabled)
        {
            Resources["NotchPanelBrush"] = new SolidColorBrush(Colors.Black);
            Resources["NotchStrokeBrush"] = new SolidColorBrush(BaseStrokeColor);
            return;
        }

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
