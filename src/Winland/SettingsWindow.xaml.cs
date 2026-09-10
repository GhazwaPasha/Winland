using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Winland.Interop;
using Winland.ViewModels;

namespace Winland;

/// <summary>
/// A normal, native-chrome top-level window — deliberately the odd one out
/// next to the borderless notch (see MainWindow's class doc): Mica requires
/// a real DWM-composed window, and a settings page benefits from ordinary
/// title bar/close-button affordances a permanent overlay has no room for
/// anyway.
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    /// <summary>
    /// The Mica/dark-title-bar attributes need a real hwnd to attach to,
    /// which only exists once the window's native source is created — same
    /// timing MainWindow's own OnSourceInitialized relies on for its hwnd-
    /// dependent setup.
    ///
    /// The CompositionTarget.BackgroundColor line is the other half
    /// DwmInterop.ApplyMicaBackdrop's own doc comment points at: WPF's
    /// HwndTarget paints an opaque background into this window's render
    /// target regardless of the XAML-declared Window.Background, and that
    /// opaque paint is exactly what was covering the real Mica surface
    /// DWM was already compositing underneath — forcing it to Transparent
    /// here is what actually lets that surface show through.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwndSource = (HwndSource)PresentationSource.FromVisual(this)!;
        hwndSource.CompositionTarget.BackgroundColor = Colors.Transparent;

        DwmInterop.ApplyMicaBackdrop(hwndSource.Handle);
    }
}
