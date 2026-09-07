# Handoff: Windows Notch (WinUI3-style Dynamic Island)

## Overview
A macOS-notch-inspired always-on-top widget for Windows, built with Windows/Fluent visual language (Mica/acrylic panels, Segoe UI Variable, Fluent icon set). Sits top-center of the screen. Collapsed it's a small pill showing clock/wifi/battery; clicking expands it into a flyout with tabs: Quick settings, Media, Apps, AI (Claude usage tracker). Includes a Pin toggle that keeps it visible over fullscreen apps.

## About the Design Files
The files in this bundle are **design references built in HTML/CSS/JS** — they show intended look, layout, and interaction, not production code to copy directly. Recreate this in a native Windows app (WinUI 3 / C#, the target framework here) using its layout and control system (XAML), not by embedding a WebView.

## Fidelity
**High-fidelity.** Colors, blur/acrylic treatment, spacing, icon set, and animation intent are final; recreate pixel-close using WinUI 3 controls (Flyout/TeachingTip hosted in a top-most borderless window, XAML shapes/icons, `Microsoft.UI.Xaml.Media.AcrylicBrush` for the blur).

## Screens / Views

### 1. Collapsed pill (default state)
- **Purpose**: Always-visible status pill at top-center of screen, minimal footprint.
- **Layout**: Width 220px, height 34px, centered horizontally, anchored to top edge (y=0), no top border radius, bottom corners rounded 18px. Background: acrylic/Mica dark `rgba(32,32,32,0.82)` or light `rgba(249,249,249,0.85)`, backdrop blur ~30px + saturate(180%), 1px border `rgba(255,255,255,0.10)` (dark) / `rgba(0,0,0,0.08)` (light), no top border, drop shadow `0 12px 30px rgba(0,0,0,0.35)`.
- **Content** (horizontal row, centered, gap 10px): Wi-Fi icon (Fluent wifi glyph, 13px) → battery icon (16x10 outline) → time text (11px, weight 600) → pin icon (12px, toggle, accent-colored when pinned).
- **Interaction**: Click anywhere on the pill (except the pin icon) toggles to expanded state, animating width/height/border-radius (280ms, cubic-bezier(.2,.8,.2,1)).

### 2. Expanded flyout
- **Layout**: Width 400px, height varies by tab (Quick settings 350px, Media 190px, Apps 260px, AI 230px). Same acrylic background/border/shadow as collapsed, bottom corners 20px radius, no top radius. Internal padding 16px/18px/18px (top/sides/bottom), vertical stack gap 14px.
- **Header row**: time (20px, weight 600) + date (12px, secondary color) on left; wifi icon, pin icon (toggle), battery icon+percentage on right, all secondary-color, gap 8px.
- **Tab bar**: 4 segments (Quick settings / Media / Apps / AI) in a pill container (`background: rgba(0,0,0,.05)` light / `rgba(255,255,255,.06)` dark, padding 3px, radius 8px). Active tab: white (light) / `rgba(255,255,255,.14)` (dark) background, radius 6px, weight 600; inactive: transparent, weight 400. Font size 12px.

#### Quick settings tab
- 2x2 grid of toggle tiles (Wi-Fi, Bluetooth, Airplane mode, Battery saver), gap 8px. Each tile: icon in 30x30 rounded-6px swatch (accent blue `#3E93E8` bg + white icon when ON, neutral bg + secondary icon when OFF), label (12px, weight 600) + status subtext (10.5px, secondary) stacked. Tile background: accent tint `rgba(62,147,232,0.16)` when ON, neutral otherwise.
- Volume and brightness sliders below: icon + thin track (4px, rounded) with accent-blue fill bar reflecting %.

#### Media tab
- Row: 64x64 album-art placeholder (diagonal stripe pattern placeholder) + track info (title 13px/600, artist 11.5px secondary), thin progress bar, and transport controls (prev / play-pause circular accent button / next), gap 16px.

#### Apps tab
- 4-column grid of app icons: 36x36 rounded-8px colored square with single-letter glyph + label (9.5px) below, gap 10px. Example set: Explorer, Edge, Mail, Store, Photos, Settings, Music, Terminal — brand-ish colors per app.

#### AI tab (Claude usage tracker)
- Header: 28px Claude icon swatch (`#D97757`) + "Claude" label + live status dot (pulses via 1.4s opacity keyframe when "Working…", static when "Idle").
- **Dual concentric ring gauge** (SVG, 110x110 viewBox 100x100): outer ring = weekly limit usage %, inner ring = 5-hour limit usage %, both start at 12 o'clock (`rotate(-90 50 50)`), round line caps, stroke width 7, radii 40 (outer) / 27 (inner), track color = neutral `rgba(_,_,_,.12-.14)`. Center text: weekly % (17px bold) + "this week" (8px secondary). Ring colors: weekly = accent blue, 5-hour = Claude orange.
- Legend to the right of the rings (not below — sits side by side, gap 16px): two rows, each a colored dot + "Weekly limit – NN%" / "5-hour limit – NN%" (11.5px) with a reset countdown underneath (10px secondary, e.g. "Resets in 3d 4h" / "Resets in 2h 15m").

## Interactions & Behavior
- **Expand/collapse**: click toggles `isExpanded`; animate width/height/border-radius together.
- **Tab switching**: click a tab segment sets active tab; content and panel height change accordingly (no page reload/remount — just resize).
- **Pin toggle**: icon in both collapsed pill and expanded header. When ON (default), the notch window stays topmost/visible even when another app goes fullscreen/exclusive-fullscreen — implement via a topmost, click-through-avoiding WinUI 3 window plus handling `Windows.UI.ViewManagement` fullscreen notifications, or Win32 `SetWindowPos(HWND_TOPMOST)` + monitoring foreground-fullscreen state. When OFF, the notch should behave like a normal always-on-top-but-not-over-fullscreen widget (hide when another app takes exclusive fullscreen).
- **Theme**: light/dark toggle (demo-only control in the HTML prototype); the real app should follow Windows system theme (`Application.RequestedTheme` / accent color from `UISettings`).
- **Taskbar** (context only, not part of the notch): standard Windows 11 taskbar mockup for reference, not to be rebuilt — it's just to show the notch in situ.

## State Management
- `expanded: boolean`
- `tab: 'quick' | 'media' | 'apps' | 'ai'`
- `theme: 'light' | 'dark'` (should read from system in production)
- Quick settings toggle states: `wifi`, `bluetooth`, `airplane`, `saver` (booleans), `volume`, `brightness` (0–100)
- Media: `playing: boolean`, track metadata, progress
- AI tab: `working: boolean`, `weeklyPct`, `fiveHourPct`, `weeklyResetsIn`, `fiveHourResetsIn` — should be wired to the real Claude usage API/local tracking in production
- `pinned: boolean` — persists across app restarts (user setting)
- Live clock: update time/date at least once a minute

## Design Tokens

**Colors**
- Accent (interactive/active): `#3E93E8`
- Claude brand: `#D97757`
- Dark theme: panel `rgba(32,32,32,0.82)`, text `#FFFFFF`, secondary text `rgba(255,255,255,0.65)`, stroke `rgba(255,255,255,0.10)`, slider track `rgba(255,255,255,0.14)`
- Light theme: panel `rgba(249,249,249,0.85)`, text `#1B1B1B`, secondary text `rgba(0,0,0,0.6)`, stroke `rgba(0,0,0,0.08)`, slider track `rgba(0,0,0,0.12)`
- Warning (near usage limit ≥85%): `#E8A33E`

**Typography**: Segoe UI Variable Display / Segoe UI, system-ui fallback. Sizes used: 20px (clock), 13px (titles), 12px (body/tabs), 11.5px–11px (labels), 10.5px–10px (secondary/meta), 17px bold (ring center %).

**Radii**: Collapsed pill bottom corners 18px; expanded flyout bottom corners 20px; tiles/buttons 6–8px; circular icons/buttons 50%.

**Blur/Acrylic**: `backdrop-filter: blur(30px) saturate(180%)` for the notch/flyout; `blur(24px) saturate(150%)` for the taskbar.

**Shadow**: `0 12px 30px rgba(0,0,0,0.35)` under the notch/flyout.

## Assets
No external image assets — all icons are inline vector paths (Fluent-style outline icons for wifi/battery/bluetooth/airplane/volume/brightness/media transport/pin). App and AI tiles use flat color swatches with single-letter/glyph initials as placeholders; replace with real app icons in production.

## Files
- `Windows Notch.dc.html` — full interactive prototype (all states: collapsed, expanded × 4 tabs, light/dark theme toggle, pin toggle, fullscreen-overlay demo).
