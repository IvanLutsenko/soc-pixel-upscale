# Changelog

Format: [Keep a Changelog](https://keepachangelog.com), versions: [SemVer](https://semver.org).

## [1.1.0] - 2026-10-27
### Added
- In-game settings window (F9, or Select/View + both sticks): on/off, compare, 2x/4x, conversion mode, categories,
  "Convert everything now" with progress, clear cache, open cache folder, live stats. Mouse, touch and gamepad.
- Window language: English / Ukrainian / Russian (follows the system, or `MenuLanguage`).
- BepInEx ConfigurationManager (F1) support: ordered settings, hidden one-shot flags.
- Settings apply immediately: switching scale or categories restores originals and rebuilds without a restart.

## [1.0.0] - 2026-10-26
### Added
- Scale2x smoothing (X2 / X4) for units, heroes, buildings, points of interest; backgrounds and UI untouched.
- Realtime conversion (GPU readback + background thread), Prebake mode with progress, CacheOnly mode.
- Per-game-version, per-scale cache in `BepInEx/cache/SoCPixelUpscale`.
- Original / smoothed toggle: Home / End / L3+R3.
