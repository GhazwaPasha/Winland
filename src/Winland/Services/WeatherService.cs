using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Winland.Services;

// *** Detached, not deleted (see STORE_SUBMISSION.md's "Weather module —
// detached, not deleted" section) *** — App.xaml.cs no longer constructs
// this class, so it makes no network calls in the shipped app. Left as-is
// otherwise so a future update can reconnect it with no rework.

/// <summary>
/// Current-conditions weather for the collapsed pill's weather block and the
/// Weather tab, refreshed on a timer the same "own a DispatcherTimer, raise
/// one Changed per tick" shape SystemVitalsService uses.
///
/// Location comes from IP geolocation (ipwho.is — free, HTTPS, no signup),
/// not the Windows Location API: that avoids the OS location-permission
/// prompt and the "location capability" app-manifest entry entirely, at the
/// cost of city-level (not GPS-level) accuracy — a reasonable trade for a
/// weather widget. Re-resolved every refresh rather than cached forever, so
/// a laptop that moves networks between ticks picks up the new city instead
/// of being stuck wherever it started.
///
/// Conditions come from OpenWeatherMap's free "Current Weather" endpoint,
/// which needs an API key. <see cref="DefaultApiKey"/> ships baked into the
/// app so weather works for every install with zero setup; Settings' own
/// field (AppSettings.WeatherApiKey) only exists to let someone override it
/// with their own key — e.g. if the shared default ever gets rate-limited
/// under every install's combined usage. Snapshot only ever stays
/// <see cref="WeatherSnapshot.Empty"/> (HasData false) while offline or
/// between launch and the first successful fetch landing.
/// </summary>
public sealed class WeatherService : IWeatherService, IDisposable
{
    // Weather doesn't need CPU-tier freshness — a quarter-hour is the same
    // cadence most weather widgets/apps poll at, and it keeps both free
    // APIs this relies on well under any rate limit even on a machine left
    // running for days.
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);

    // Shipped with the app rather than requiring per-user signup — see the
    // class doc. A key embedded in a distributed client is extractable by
    // anyone who looks (this is a plain string constant, not a secret
    // store), so if this app is ever shared widely, everyone's combined
    // usage shares OpenWeatherMap's free-tier rate limit; Settings' override
    // field is the escape hatch if that ever becomes a real problem.
    private const string DefaultApiKey = "585703a3e45979a4d245a86321d152bb";

    private const string GeoLocationUrl = "https://ipwho.is/";
    private const string WeatherUrlTemplate =
        "https://api.openweathermap.org/data/2.5/weather?lat={0}&lon={1}&appid={2}&units={3}";

    // A shared, static HttpClient rather than one per call/instance — the
    // standard .NET guidance to avoid exhausting sockets under load. This
    // app only ever constructs one WeatherService anyway (see App.xaml.cs),
    // but static keeps that assumption from mattering.
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Fahrenheit for the US (the one major market that still expects it),
    // Celsius everywhere else — RegionInfo.CurrentRegion reads the OS's
    // configured region, not the display language, so this follows "Region"
    // in Windows Settings the same way the taskbar's own weather widget does.
    private static readonly bool UseMetric = RegionInfo.CurrentRegion.IsMetric;
    private static string Units => UseMetric ? "metric" : "imperial";
    private static string UnitSymbol => UseMetric ? "°C" : "°F";

    private readonly IAppSettingsService _appSettingsService;
    private readonly DispatcherTimer _timer;

    public WeatherSnapshot Snapshot { get; private set; } = WeatherSnapshot.Empty;

    public event EventHandler? Changed;

    public WeatherService(IAppSettingsService appSettingsService)
    {
        _appSettingsService = appSettingsService;

        _timer = new DispatcherTimer { Interval = RefreshInterval };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        // Fire the first fetch immediately rather than waiting a full
        // RefreshInterval for the first tick — otherwise the weather block
        // would just stay hidden (HasData false) for up to 15 minutes after
        // every launch.
        _ = RefreshAsync();
    }

    /// <summary>
    /// One full round trip: re-geolocate, then fetch conditions for that
    /// location. Every failure mode (no API key yet, offline, either API
    /// erroring) just leaves <see cref="Snapshot"/> exactly as it was and
    /// returns quietly — same "best-effort, no crash, no user-visible error
    /// plumbing" shape AppSettingsService/SystemVitalsService use — so a
    /// transient blip doesn't blank out the last good reading, and a
    /// same-tick UI listener never needs to special-case "this poll failed".
    /// </summary>
    private async Task RefreshAsync()
    {
        var savedApiKey = _appSettingsService.Load().WeatherApiKey;
        var apiKey = string.IsNullOrWhiteSpace(savedApiKey) ? DefaultApiKey : savedApiKey;

        try
        {
            var location = await HttpClient.GetFromJsonAsync<GeoLocationResponse>(GeoLocationUrl, JsonOptions);
            if (location is null || !location.Success)
            {
                return;
            }

            var lat = location.Latitude.ToString(CultureInfo.InvariantCulture);
            var lon = location.Longitude.ToString(CultureInfo.InvariantCulture);
            var url = string.Format(CultureInfo.InvariantCulture, WeatherUrlTemplate, lat, lon, apiKey, Units);

            var weather = await HttpClient.GetFromJsonAsync<OpenWeatherResponse>(url, JsonOptions);
            var condition = weather?.Weather?.Count > 0 ? weather.Weather[0] : null;
            if (weather?.Main is null || condition is null)
            {
                return;
            }

            Snapshot = new WeatherSnapshot(
                HasData: true,
                TempRounded: (int)Math.Round(weather.Main.Temp),
                HighRounded: (int)Math.Round(weather.Main.TempMax),
                LowRounded: (int)Math.Round(weather.Main.TempMin),
                UnitSymbol: UnitSymbol,
                Condition: CapitalizeFirst(condition.Description) ?? condition.Main ?? string.Empty,
                IconKey: MapIconKey(condition.Main, condition.Icon),
                LocationName: !string.IsNullOrWhiteSpace(weather.Name) ? weather.Name : location.City ?? string.Empty);

            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Network hiccup, DNS failure, bad/rate-limited key, malformed
            // response — none of these are actionable from here; the next
            // timer tick just tries again.
        }
    }

    /// <summary>
    /// OpenWeatherMap's "main" group (Thunderstorm/Drizzle/Rain/Snow/Clear/
    /// Clouds/Atmosphere — see their Weather Condition Codes reference)
    /// mapped down to the handful of hand-drawn icon geometries in App.xaml
    /// (WeatherSunnyGeometry etc. — same "no matching glyph in Segoe Fluent
    /// Icons, so use a real vector icon instead" reasoning as
    /// SurfaceEarbudsGeometry). "icon" ending in "n" is OpenWeatherMap's own
    /// day/night flag — only Clear distinguishes on it (Sunny vs Moon);
    /// every other condition reads the same after dark.
    /// </summary>
    private static string MapIconKey(string? main, string? icon)
    {
        var isNight = icon?.EndsWith('n') == true;

        return main switch
        {
            "Clear" => isNight ? "Moon" : "Sunny",
            "Clouds" => "Cloudy",
            "Rain" or "Drizzle" => "Rain",
            "Thunderstorm" => "Thunderstorm",
            "Snow" => "Snow",
            // Mist, Smoke, Haze, Dust, Fog, Sand, Ash, Squall, Tornado —
            // OpenWeatherMap's "Atmosphere" group, no icon of its own here.
            _ => "Fog",
        };
    }

    private static string? CapitalizeFirst(string? text) => string.IsNullOrEmpty(text)
        ? text
        : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];

    public void Dispose() => _timer.Stop();

    private sealed class GeoLocationResponse
    {
        public bool Success { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public string? City { get; set; }
    }

    private sealed class OpenWeatherResponse
    {
        public OpenWeatherMain? Main { get; set; }
        public List<OpenWeatherCondition>? Weather { get; set; }
        public string? Name { get; set; }
    }

    private sealed class OpenWeatherMain
    {
        public double Temp { get; set; }

        [JsonPropertyName("temp_min")]
        public double TempMin { get; set; }

        [JsonPropertyName("temp_max")]
        public double TempMax { get; set; }
    }

    private sealed class OpenWeatherCondition
    {
        public string? Main { get; set; }
        public string? Description { get; set; }
        public string? Icon { get; set; }
    }
}
