using System;

namespace Winland.Services;

/// <summary>System-wide (not just this app) mic/camera in-use state, for the privacy dots.</summary>
public interface IPrivacyIndicatorService
{
    bool IsMicInUse { get; }
    bool IsCameraInUse { get; }

    event EventHandler? Changed;
}
