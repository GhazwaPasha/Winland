using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace Winland.Services;

/// <summary>
/// Per-user "launch at sign-in" registration. Branches on whether the
/// process is running packaged (MSIX/Store) or not, because the two
/// environments need genuinely different mechanisms — see
/// <see cref="IsPackaged"/>.
///
/// Unpackaged: the standard <c>HKCU\...\CurrentVersion\Run</c> key, the
/// same mechanism the Windows Settings app's own "Startup apps" list reads,
/// and the one every unpackaged desktop app uses (no admin rights needed,
/// unlike a Task Scheduler entry or HKLM). Re-pointing the value at the
/// *current* exe path on every call (not just the first time it's turned
/// on) is deliberate: this app is published as a self-contained,
/// architecture-specific exe (see the csproj's RuntimeIdentifier), so a
/// reinstall/update to a different output folder would otherwise leave a
/// stale Run entry pointing at a path that no longer exists.
///
/// Packaged: a plain Run-key write doesn't work — MSIX gives packaged
/// processes a virtualized per-package view of that registry key, so the
/// entry would never be visible to the real shell at logon. The Store-
/// correct mechanism is the <c>windows.startupTask</c> manifest extension
/// (see Package.appxmanifest's Extensions block — its TaskId must match
/// <see cref="TaskId"/> below) plus the <see cref="StartupTask"/> WinRT API.
/// </summary>
public sealed class StartupService : IStartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Winland";

    // Must match Package.appxmanifest's <desktop:StartupTask TaskId="...">
    // exactly — this is how StartupTask.GetAsync finds the right task.
    private const string TaskId = "WinlandStartupTask";

    public void SetEnabled(bool enabled)
    {
        if (IsPackaged())
        {
            // StartupTask is WinRT-async-only; SetEnabled is called from
            // fire-and-forget contexts on both sides (App.xaml.cs startup,
            // SettingsViewModel's property-changed handler), so this
            // matches the existing "best effort, never take the app down"
            // posture rather than making the whole call chain async.
            _ = SetEnabledPackagedAsync(enabled);
        }
        else
        {
            SetEnabledUnpackaged(enabled);
        }
    }

    private static void SetEnabledUnpackaged(bool enabled)
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

    private static async Task SetEnabledPackagedAsync(bool enabled)
    {
        try
        {
            var task = await StartupTask.GetAsync(TaskId);
            if (enabled)
            {
                // Only Disabled is ours to flip — DisabledByUser/
                // DisabledByPolicy mean the user (or an admin policy)
                // turned it off explicitly via Task Manager, and the
                // platform refuses to let an app override that
                // programmatically. Leaving it alone is correct: the
                // Settings toggle just won't "win" here, the same way
                // Task Manager itself would show it staying off.
                if (task.State == StartupTaskState.Disabled)
                {
                    await task.RequestEnableAsync();
                }
            }
            else if (task.State == StartupTaskState.Enabled)
            {
                task.Disable();
            }
        }
        catch
        {
            // Best-effort — see SetEnabledUnpackaged's doc.
        }
    }

    /// <summary>
    /// The documented way to tell whether the current process has package
    /// identity (running from an installed MSIX) without throwing: ask
    /// kernel32 for the package full name and check for the "no package"
    /// error code, rather than probing <c>Package.Current</c> and catching
    /// the exception it throws when unpackaged.
    /// </summary>
    private static bool IsPackaged()
    {
        var length = 0;
        var result = GetCurrentPackageFullName(ref length, null);
        return result != ApiErrorNoPackage;
    }

    private const int ApiErrorNoPackage = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder? packageFullName);
}
