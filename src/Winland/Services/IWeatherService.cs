using System;

namespace Winland.Services;

// *** Detached, not deleted (see STORE_SUBMISSION.md's "Weather module —
// detached, not deleted" section) ***
// Nothing in App.xaml.cs constructs WeatherService and no view model holds
// an IWeatherService reference right now, so none of this runs in the
// shipped app. The implementation below is otherwise untouched and ready to
// wire back into NotchViewModel/App.xaml.cs in a future update.

/// <summary>
/// One poll's worth of current-conditions data for the collapsed pill's
/// weather block and the Weather tab. <see cref="HasData"/> is false until
/// the first successful fetch (no API key yet, no network, IP geolocation
/// failed, ...) — every text/number field is meaningless while it's false,
/// callers gate on it rather than trusting a "0" or empty string to mean
/// anything.
/// </summary>
public sealed record WeatherSnapshot(
    bool HasData,
    int TempRounded,
    int HighRounded,
    int LowRounded,
    string UnitSymbol,
    string Condition,
    /// <summary>One of "Sunny"/"Moon"/"Cloudy"/"Rain"/"Thunderstorm"/"Snow"/"Fog" — the suffix of a WeatherXGeometry resource key defined in App.xaml.</summary>
    string IconKey,
    string LocationName)
{
    public static WeatherSnapshot Empty { get; } = new(false, 0, 0, 0, string.Empty, string.Empty, string.Empty, string.Empty);
}

/// <summary>
/// Current-conditions weather for whatever city the machine's public IP
/// geolocates to (see WeatherService's own doc for why IP-based rather than
/// the Windows Location API). Polls on its own timer, the same
/// poll-and-diff shape SystemVitalsService/PrivacyIndicatorService use for
/// the same reason: there's no push/event API for "the weather changed".
/// </summary>
public interface IWeatherService
{
    WeatherSnapshot Snapshot { get; }

    event EventHandler? Changed;
}
