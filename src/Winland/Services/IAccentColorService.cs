using System;
using System.Windows.Media;

namespace Winland.Services;

/// <summary>
/// The two independent signals Windows itself uses for the accent color:
/// the live color (which small controls always track), and whether the
/// user has "Show accent color on Start and taskbar" turned on (which only
/// gates large surfaces like the taskbar's own tint).
/// </summary>
public interface IAccentColorService
{
    Color Accent { get; }

    /// <summary>True when <see cref="Accent"/> is light enough that a dark
    /// (not white) glyph should be drawn on top of it.</summary>
    bool IsAccentLight { get; }

    /// <summary>Mirrors the OS's "Show accent color on Start and taskbar" setting.</summary>
    bool IsTintEnabled { get; }

    event EventHandler? Changed;
}
