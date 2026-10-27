# SoC Pixel Upscale

A BepInEx plugin for **Songs of Conquest** that smooths the chunky pixel art of everything you interact with —
troops, heroes, buildings, points of interest, mines, pickups, battle obstacles — using the **Scale2x** pixel-art
upscaler. Backgrounds (trees, grass, terrain, ambient props) stay as they are, so units and points of interest
stand out from the scenery. The interface is never touched.

* Scale2x keeps the original palette: no blur, no "oil painting" look — the stair-steps on outlines become clean diagonals.
* **X4** (two Scale2x passes, recommended) or **X2** (one pass, lighter).
* Nothing in the game files is modified. Delete the plugin and the game is vanilla again.
* No game assets are distributed: the smoothed sprites are generated on your own PC.

## Install

1. Install **BepInEx 5 (x64)**, version 5.4.23 or newer 5.4.x:
   https://github.com/BepInEx/BepInEx/releases → `BepInEx_win_x64_5.4.23.x.zip`.
   Extract it into the game folder (the one with `SongsOfConquest.exe`). Steam, GOG and Epic all work.
2. Run the game once and quit (BepInEx creates its folders).
3. Copy the `BepInEx` folder from this archive into the game folder (merge).
4. Recommended: in `BepInEx\config\BepInEx.cfg` set `HideManagerGameObject = true`.

## In-game settings window

Press **F9** (gamepad: hold **Select/View** and click **both sticks**). Everything can be changed there and applies
immediately — no restart, no config editing:

* mod on/off and a quick *original / smoothed* comparison,
* smoothing **2x / 4x**,
* conversion mode (on the fly / all at once / cache only),
* what to smooth: units & heroes, buildings & points of interest, decorations, effects,
* **Convert everything now** (with a progress bar), **Clear cache**, **Open cache folder**,
* live stats: sheets in memory, conversions in progress, cache size on disk.

Mouse, touch screen (handhelds) or D-pad + A; B closes the window. The window follows the system language
(English / Українська / Русский), or set `MenuLanguage`.
If you use [BepInEx ConfigurationManager](https://github.com/BepInEx/BepInEx.ConfigurationManager), the same settings
also appear in its F1 window.

## Two ways to build the smoothed sprites

Pick it in the in-game window (F9) or set `CacheMode` in `BepInEx\config\ivan.soc.pixelupscale.cfg`:

| Mode | What happens |
|---|---|
| `Realtime` (default) | Each sprite sheet is converted in the background the first time the game shows it (fractions of a second on a modern CPU). The very first time you meet a unit it may look original for a moment. Everything is cached, so next time it is instant. |
| `Prebake` | In the main menu a progress bar appears and the plugin converts everything at once (a few minutes, once per game version). Stay in the main menu until it says *Done*, then play fully smoothed from the first turn. |
| `CacheOnly` | Never converts, only uses what is already cached. |

To convert everything again (e.g. after switching scale) use **Convert everything now** in the F9 window.

## Settings

| Setting | Default | |
|---|---|---|
| `Enabled` | `true` | Master switch. |
| `Scale` | `X4` | `X2` or `X4`. Each scale keeps its own cache. |
| `UnitsAndHeroes` | `true` | Troops, heroes and their effect layers. |
| `MapObjects` | `true` | Buildings, settlements, points of interest, mines, pickups, battle obstacles, hero pets. |
| `Decorations` | `false` | Torches, ruins, lava pools, fish, festive decor. |
| `Effects` | `false` | Spell and particle sprites. |
| `ToggleKey` | `Home` | Switch original / smoothed in game to compare. `End` also works; on a gamepad press **both sticks** (L3+R3). |
| `MenuKey` | `F9` | Opens the settings window. |
| `MenuScale` | `1.0` | Size of the window (0.6–2). |

The cache lives in `BepInEx\cache\SoCPixelUpscale\<game version>\x4` (or `x2`). After a game update a new folder is used
automatically, so updated art is reconverted.

Note: if you use ReShade, its overlay key is also `Home` by default — use `End` or the gamepad combo instead.

## Uninstall

* Turn it off: `Enabled = false`, or delete `BepInEx\plugins\SoCPixelUpscale`.
* Free the disk space: delete `BepInEx\cache\SoCPixelUpscale`.
* Remove BepInEx completely: delete `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version` and the `BepInEx` folder from the game folder.

## Performance & memory

Smoothed sheets are 4× (X2) or 16× (X4) larger in video memory than the originals, but only sheets used on the current
map/battle are loaded. `X4` was played through a campaign mission on a ROG Ally without issues; if you run low on memory use `X2`.

## Troubleshooting

`BepInEx\LogOutput.log` — the plugin writes a heartbeat every 30 s (`atlases`, `sprites`, `converted`, `pending`).

## How it works (for the curious)

The game animates units through its own `SpriteAnimation` assets and buildings through `SimpleSpriteAnimator`-style
components. The plugin builds a smoothed twin of every sprite on a separate 4× texture (same world size, same mesh) and
swaps the references in those frame arrays. The UI keeps original sprites (Harmony prefixes on `UISprite.Sprite` and
`SpriteImageLayer.SetSprite`), because the interface sizes images by their pixel rect.

Source code is in [`src/`](src/); see [Building](#building). License: MIT.

## Download

Grab the latest `SoCPixelUpscale_vX.Y.Z.zip` from [Releases](../../releases). Changes are listed in [CHANGELOG.md](CHANGELOG.md).

## Building

The plugin is a single C# file compiled against the game's own Unity assemblies and BepInEx (none of them are in this
repository). With Mono's `mcs` (or any C# 7 compiler):

```sh
GAME_DIR="/path/to/SongsOfConquest" ./scripts/build.sh     # -> dist/SoCPixelUpscale.dll
GAME_DIR="/path/to/SongsOfConquest" ./scripts/package.sh   # -> dist/SoCPixelUpscale_v<version>.zip
```

`GAME_DIR` must contain `SongsOfConquest_Data/Managed` and an installed `BepInEx/core`.
The version lives in one place: `Plugin.Version` in `src/SoCPixelUpscale.cs`.
