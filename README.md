# X4 Map Editor

A map editor for **X4: Foundations** that builds custom galaxies: lay out clusters and sectors on a hex map, then decorate each sector in 3D with the game's real gates, backdrops, asteroid fields, nebulae, wrecks and landmarks.

Author: M.R. Reese. Work in progress. Licence: MIT (see `LICENSE`).

## Try the beta (no installation)

**[Pre-release 0.1 beta](https://github.com/M-R-Reese/X4-Map-editor/releases/tag/v0.1-beta)** · [direct download (zip, 69 MB)](https://github.com/M-R-Reese/X4-Map-editor/releases/download/v0.1-beta/X4MapEditor-0.1-beta-windows.zip)

1. Download `X4MapEditor-0.1-beta-windows.zip` from the [pre-release page](https://github.com/M-R-Reese/X4-Map-editor/releases/tag/v0.1-beta).
2. Unpack it anywhere and start `X4MapEditor.exe`. Nothing else is needed: .NET comes with it.
3. On the first start, check the folders the editor guessed (your X4 game folder, your profile folder) and click **Start**.

You need X4: Foundations installed (Windows, Steam). The beta has no mod export yet: it is for looking around,
building a galaxy and decorating sectors. Feedback is welcome in the Issues.

## What it does

- **Galaxy map (2D):** create clusters on the hex grid (up to 3 sectors each), drag them around, connect sectors with gate pairs, set name, sunlight, economy, security and the player's start sector. A check list warns about unconnected gates, sectors nobody can reach, gates that are too close together, and similar problems.
- **Sector view (3D):** the sector with its ecliptic (rings every 50 km), gates with their real models, and the cluster's real backdrop as the sky (planets, moons, rings, nebulae), with the sun's direction taken from that backdrop.
  - Place the sector anywhere inside its backdrop, for example right next to a gas giant. Vanilla does the same.
  - Choose from every backdrop the installed game has (base game and DLCs), and play the cluster's music.
- **Decoration:** dropdowns with the game's asteroid fields, gas clouds, nebulae, wreck fields, wrecks, landmarks, data vaults, nav beacons, single asteroids and anomalies, named as the game names them.
  - Drag items with the mouse; Shift + drag changes the height.
  - A 2D minimap of the sector.
  - Right-click menu: copy, cut, paste, delete, and connect gates and anomalies to another sector.
- **Controls:** W A S D / Q E to fly, right mouse button to turn, mouse wheel to zoom. Esc closes the front window.

Not yet: exporting the finished galaxy as a mod (planned next), stations and ships (a later, separate editor).

## Game files

The editor reads the models, textures, texts and music **straight from your own X4 installation**: from the packed `.cat/.dat` archives, from the DLCs, and from the mods you have switched on. **No game files are included here, and none are ever copied.**

Anything that comes from another author's mod is marked with ⚠ in the dropdowns, and the editor asks before you use it: your galaxy would then need that mod, and copying another author's files without permission is not allowed.

## Requirements

- X4: Foundations (Steam; the DLCs are optional)
- [Godot 4.7 **.NET** edition](https://godotengine.org/download) (not the standard one)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

## Start from the source

1. Open `Start_3D_Editor.bat` in a text editor and set `GODOT` to the path of your Godot .NET program file.
2. Double-click `Start_3D_Editor.bat`. It builds the editor and starts it.
3. On the first start, check the folders the editor guessed (game folder, profile folder) and click **Start**.

Project files (`*.galaxy.json`) are compatible with the older HTML galaxy editor.

## Folders

| Folder | Content |
|---|---|
| `X4Data/` | Game data library, no Godot needed: archives, extensions, index, models (`.xmf`), textures (DDS), materials, texts, backdrops, regions, decoration catalogue, music, galaxy project model |
| `editor/` | Godot project: galaxy map, side panel, 3D sector view, minimap, music player |
| `tests/` | Checks of the data library against the installed game: `dotnet run --project tests` |
| `probe/` | Developer tool that prints facts from the game data |

## Build the exe

With the Godot 4.7 .NET export templates installed (Godot: Editor > Manage Export Templates), from `editor/`:
`Godot_v4.7.2-stable_mono_win64_console.exe --headless --path . --export-release "Windows Desktop"`.
The program goes to `build/X4MapEditor/`.

## Licence

MIT, Copyright (c) 2026 M.R. Reese. X4: Foundations and its files belong to Egosoft; this editor contains none of them.
