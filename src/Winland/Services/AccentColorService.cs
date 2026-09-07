using System;
using System.IO;
using System.Windows.Media;
using Microsoft.Win32;
using Windows.UI.ViewManagement;

namespace Winland.Services;

/// <summary>
/// Reads the same personalization state the Windows shell itself reads:
/// the live accent color (<see cref="UISettings"/>, the same source the
/// Calendar flyout's "today" marker and Quick Settings toggles use — always
/// on, no opt-out) and the "Show accent color on Start and taskbar" flag
/// (the <c>ColorPrevalence</c> registry value, which only gates the
/// taskbar/Start *background* tint — there's no WinRT event for it, so it's
/// re-read on the general preference-changed signal instead).
/// </summary>
public sealed class AccentColorService : IAccentColorService, IDisposable
{
    private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string ColorPrevalenceValueName = "ColorPrevalence";

    private readonly UISettings _uiSettings = new();

    public Color Accent { get; private set; } = Color.FromArgb(255, 0x3E, 0x93, 0xE8);
    public bool IsAccentLight { get; private set; }
    public bool IsTintEnabled { get; private set; }

    public event EventHandler? Changed;

    public AccentColorService()
    {
        _uiSettings.ColorValuesChanged += OnColorValuesChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Refresh();
    }

    private void OnColorValuesChanged(UISettings sender, object args) => Refresh();

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        try
        {
            var accent = _uiSettings.GetColorValue(UIColorType.Accent);
            Accent = Color.FromArgb(accent.A, accent.R, accent.G, accent.B);
        }
        catch
        {
            // Leave the last known (or default) accent — a personalization
            // read failing is not worth taking the notch down over.
        }

        IsAccentLight = RelativeLuminance(Accent) > 0.5;
        IsTintEnabled = ReadColorPrevalence();
        LogDiagnostics();

        Changed?.Invoke(this, EventArgs.Empty);
    }

    // Plain-text breadcrumb next to the exe so a mismatch between "what the
    // running process resolved" and "what's actually in Settings" is a file
    // read away, instead of a guess from a screenshot — also doubles as
    // confirmation that a given running process is actually the build that
    // has this service in it at all.
    private void LogDiagnostics()
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  " +
                       $"Accent=#{Accent.A:X2}{Accent.R:X2}{Accent.G:X2}{Accent.B:X2}  " +
                       $"IsAccentLight={IsAccentLight}  IsTintEnabled={IsTintEnabled}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "accent-debug.log"), line);
        }
        catch
        {
            // Best-effort diagnostics only.
        }
    }

    private static bool ReadColorPrevalence()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
            return key?.GetValue(ColorPrevalenceValueName) is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    // Standard WCAG relative-luminance formula, used only to pick a black
    // or white glyph on top of the accent fill — the same problem Windows
    // solves with its TextOnAccentFillColorPrimary token.
    private static double RelativeLuminance(Color color)
    {
        double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    public void Dispose()
    {
        _uiSettings.ColorValuesChanged -= OnColorValuesChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }
}
