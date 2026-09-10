# Microsoft Store submission checklist — "Wisland"

Store listing name: **Wisland**. Internal project/namespace/assembly/exe stay
**Winland** — only the Store-facing identity (`DisplayName`, package name)
changes. See [`src/Winland/Package.appxmanifest`](src/Winland/Package.appxmanifest).

## Done in this repo

- [x] `src/Winland/Package.appxmanifest` — MSIX manifest packaging the
      existing WPF exe as a classic full-trust ("Desktop Bridge") app.
      DisplayName set to "Wisland"; capabilities set to just `runFullTrust`
      (no `internetClient` — see "Weather module" below, the shipped app
      makes no network calls).
- [x] `src/Winland/Assets/Store/*.png` — Store tile/logo/splash images,
      generated from the existing `AppIcon.ico`. These are a placeholder
      pass (upscaled from a 256×256 source, flat transparent background) —
      good enough to submit, but worth a real designer pass before your
      final listing, especially checking contrast on light backgrounds.
- [x] `build/Package-Msix.ps1` — builds `.msix` packages for x64 and ARM64
      directly with `dotnet publish` + the Windows SDK's `makeappx.exe`
      (found under `Windows Kits\10\bin\...`), no Visual Studio "Windows
      Application Packaging Project" workload required. Confirmed the
      underlying WPF project still builds clean (`dotnet build`, 0
      warnings/errors).
- [x] `.gitignore` updated to exclude packaging output (`artifacts/msix/`,
      `*.msix`, `*.pfx`, etc).
- [x] **"Start with Windows" fixed for packaged installs.**
      [`StartupService.cs`](src/Winland/Services/StartupService.cs) now
      branches: unpackaged (dev/debug) keeps the old `HKCU...\Run` key
      write; packaged detects package identity via
      `kernel32!GetCurrentPackageFullName` and uses the
      `Windows.ApplicationModel.StartupTask` API instead, backed by the
      `windows.startupTask` extension declared in `Package.appxmanifest`
      (`TaskId="WinlandStartupTask"`, must stay in sync between the two —
      there's a comment at each end pointing at the other). Rebuilt and
      confirmed the app still compiles clean.
- [x] **Weather module detached** (see its own section below).

## Weather module — detached, not deleted

The app made two kinds of network calls, both from
[`WeatherService.cs`](src/Winland/Services/WeatherService.cs): IP geolocation
(`ipwho.is`) and current-conditions weather (`api.openweathermap.org`, using
an API key baked into the binary as a plain string constant). Per your
request, this is disconnected from the shipped app for this submission and
picked back up in a future update, rather than fixed/removed outright.

**What actually changed** (all reversible, no deletions):
- `App.xaml.cs` no longer constructs `WeatherService` or passes one to
  `NotchViewModel`.
- `NotchViewModel` no longer takes an `IWeatherService` dependency and no
  longer exposes `WeatherHasData`/`WeatherTempText`/`WeatherIconGeometry`/etc.
  or a `"Weather"` value for `SelectedTab`.
- `MainWindow.xaml` no longer has the collapsed-pill weather block, the
  Weather tab button (tab bar went from 5 columns back to 4), or the Weather
  tab's content panel. `MainWindow.xaml.cs` lost the now-unused
  `WeatherTab_MouseLeftButtonUp` handler.
- `SettingsWindow.xaml` / `SettingsViewModel.cs` no longer show or bind a
  Weather API key field.
- `Package.appxmanifest` dropped the `internetClient` capability, since the
  app now makes no network calls at all.

**What did NOT change** (the reusable "work"):
- [`Services/WeatherService.cs`](src/Winland/Services/WeatherService.cs) and
  [`Services/IWeatherService.cs`](src/Winland/Services/IWeatherService.cs) —
  full implementation intact (geolocation, OpenWeatherMap fetch, condition →
  icon-key mapping), just uninstantiated. Each file has a comment marking it
  detached-not-deleted.
- The seven `WeatherXGeometry` icon resources in `App.xaml` (Sunny/Moon/
  Cloudy/Rain/Snow/Thunderstorm/Fog).
- `AppSettings.WeatherApiKey` in the settings-file schema — kept so any value
  a user already saved round-trips through `settings.json` untouched, and so
  re-enabling Weather later needs no settings-schema change.

**To bring it back in a future update**: reintroduce the `IWeatherService`
constructor param and `Changed`/`RefreshWeather` wiring in `NotchViewModel`,
re-add the collapsed-pill block + tab button (5th tab-bar column) + tab
content in `MainWindow.xaml`/`.xaml.cs`, re-add the Settings API-key row, and
put the `internetClient` capability back in `Package.appxmanifest`. Worth
addressing the hardcoded `DefaultApiKey` in `WeatherService.cs` at the same
time — the class's own doc comment already flags it as extractable from the
binary, and shipping to the Store is exactly the wider-distribution scenario
that makes the shared free-tier rate limit a real risk.

## What only you can do (needs your Microsoft/Partner Center account)

1. ~~Reserve the app name~~ — done. Reserved as **Wisland** under publisher
   **Pasha Foundry**.
2. ~~Get your package identity values~~ — done, and already filled into
   `Package.appxmanifest`:
   - Identity Name: `PashaFoundry.Wisland`
   - Publisher: `CN=ACA19620-2C81-4D92-848A-301F3950E48A`
   - Publisher display name: `Pasha Foundry`
   - (Reference only, not used in the manifest: Package Family Name
     `PashaFoundry.Wisland_0mq1c6mycnd66`, Store ID `9PB1J8CPDG8D`)
3. ~~Build the packages~~ — done: `artifacts/msix/Wisland_1.0.0.0_x64.msix`
   (78MB) and `..._arm64.msix` (74MB) are built and ready to upload. Rerun
   `./build/Package-Msix.ps1` any time you need a fresh build (e.g. after
   bumping the version). Don't sign these yourself for submission — Partner
   Center signs uploaded packages with its own certificate; the script's
   `-SignForLocalTesting` switch is only for sideloading on your own machine
   to sanity-check the package before upload.
4. **Store listing content** (all done in Partner Center, not this repo):
   - Description, short description, "what's new"
   - At least one screenshot per package (1366×768 or larger recommended) —
     take these from the running app yourself
   - Age rating questionnaire (IARC)
   - Category (likely "Utilities & tools" or "Personalization")
   - Privacy policy URL — see the disclosure points below for what's still
     worth covering even with Weather detached
   - Pricing and market selection
5. **Submit** the packages + listing content for certification.

## Suggested privacy policy disclosure points

With Weather detached the app makes no network calls at all; Partner Center
may still ask for a privacy policy URL depending on category/market, so if
you need one, the only things actually worth disclosing today are local-only:
- Reads Windows' own camera/microphone *usage-indicator* registry keys
  (`CapabilityAccessManager\ConsentStore`) to show privacy-dot state — does
  not itself access the camera or microphone
- Stores user-added shelf file paths locally in
  `%LOCALAPPDATA%\Winland\shelf.json` — never transmitted anywhere

(If/when Weather is reconnected, add back: IP-based geolocation via
`ipwho.is` and current-conditions weather via `api.openweathermap.org`.)
