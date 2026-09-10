using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace Winland.Services;

/// <summary>
/// Per-user "launch at sign-in" registration via the standard
/// <c>HKCU\...\CurrentVersion\Run</c> key — the same mechanism the Windows
/// Settings app's own "Startup apps" list reads, and the one every
/// unpackaged desktop app uses (no admin rights needed, unlike a Task
/// Scheduler entry or HKLM). Re-pointing the value at the *current* exe
/// path on every call (not just the first time it's turned on) is
/// deliberate: this app is published as a self-contained,
/// architecture-specific exe (see the csproj's RuntimeIdentifier), so a
/// reinstall/update to a different output folder would otherwise leave a
/// stale Run entry pointing at a path that no longer exists.
/// </summary>
public sealed class StartupService : IStartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Winland";

    public void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return;
            }

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return;
            }

            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath))
            {
                return;
            }

            key.SetValue(ValueName, $"\"{exePath}\"", RegistryValueKind.String);
        }
        catch
        {
            // Best-effort, same posture as AppSettingsService/AccentColorService's
            // registry reads — a failed write here just means the preference
            // doesn't take effect until the next successful attempt, not a
            // reason to take the app down.
        }
    }
}
