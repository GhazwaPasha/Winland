using CommunityToolkit.Mvvm.ComponentModel;
using Winland.Models;
using Winland.Services;

namespace Winland.ViewModels;

/// <summary>
/// Backs SettingsWindow. Small and separate from NotchViewModel on purpose —
/// it owns none of the notch's live-polling services, just the persisted
/// preferences a settings page toggles, following the same
/// load-then-save-on-change shape NotchViewModel's own IsPinned already
/// uses (see OnIsPinnedChanged there).
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly IAppSettingsService _appSettingsService;
    private readonly IStartupService _startupService;

    // Guards the OnXChanged handlers below while the constructor's initial
    // Load() assigns every property in turn — without it, restoring
    // StartMinimized would re-save a settings.json built from
    // StartAtStartup's still-default value (false) before that property's
    // own assignment ever runs, the same "assign in field-declaration order,
    // not all-at-once" hazard NotchViewModel's IsPinned doesn't have to
    // worry about only because it has just the one persisted field.
    private bool _isLoading = true;

    public SettingsViewModel(IAppSettingsService appSettingsService, IStartupService startupService)
    {
        _appSettingsService = appSettingsService;
        _startupService = startupService;

        var settings = _appSettingsService.Load();
        StartAtStartup = settings.StartAtStartup;
        StartMinimized = settings.StartMinimized;
        BlackMode = settings.BlackMode;

        _isLoading = false;
    }

    [ObservableProperty]
    private bool startAtStartup;

    /// <summary>CommunityToolkit.Mvvm calls this after every StartAtStartup change, including the initial restore-on-construction one above.</summary>
    partial void OnStartAtStartupChanged(bool value)
    {
        _startupService.SetEnabled(value);
        Persist();
    }

    [ObservableProperty]
    private bool startMinimized;

    partial void OnStartMinimizedChanged(bool value) => Persist();

    /// <summary>
    /// Solid-black notch panel, in place of the usual translucent/
    /// accent-tinted look. App.xaml.cs listens for this specific property's
    /// change (alongside its own AccentColorService.Changed hookup) and
    /// re-applies NotchPanelBrush immediately — same "takes effect the
    /// instant it's toggled" expectation IsPinned already sets, not just
    /// "on the next launch".
    /// </summary>
    [ObservableProperty]
    private bool blackMode;

    partial void OnBlackModeChanged(bool value) => Persist();

    // No Weather API key property here — the Settings UI row for it was
    // removed along with the rest of the module (see
    // STORE_SUBMISSION.md's "Weather module — detached, not deleted"
    // section). AppSettings.WeatherApiKey itself is untouched; Persist()
    // below round-trips whatever value is already on disk instead of this
    // view model owning it.

    private void Persist()
    {
        if (_isLoading)
        {
            return;
        }

        var current = _appSettingsService.Load();
        _appSettingsService.Save(current with
        {
            StartAtStartup = StartAtStartup,
            StartMinimized = StartMinimized,
            BlackMode = BlackMode,
        });
    }
}
