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

- **Start menu:** new galaxy, **new patch** for an installed galaxy (the X4 galaxy with your DLCs, or a mod's galaxy), open project, recent projects. A patch shows that galaxy dimmed on the map; you place your clusters in free hexes and connect them to its sectors, with a new gate there or by reusing one of its unused gates. The export adds your sectors to the galaxy, for new games and (like a DLC) for saved games. Double-click a sector of that galaxy to look at it in 3D (read-only: gates, fields, resources, backdrop); the gates your patch adds there can be dragged and turned like your own.
- **Mods from other folders:** Menu → "Open a mod from another folder" reads a mod that is not in the game's extensions folder (downloaded or unpacked), as switched on: its galaxy is offered for a patch, its backdrops and objects appear in the lists. Also under Folders → "Other mod folders".
- **Spoken names:** the "Voice" button next to a cluster or sector name lists the location names the game has voice lines for (English and German), free ones first, with an optional Roman numeral. Listen to them before choosing; the game then says the name and shows it in each player's language.
- **Galaxy map (2D):** create clusters on the hex grid (up to 3 sectors each), drag them around, connect sectors with gate pairs, set name, sunlight, economy, security and the player's start sector. A check list warns about unconnected gates, sectors nobody can reach, gates that are too close together, and similar problems.
- **Sector view (3D):** the sector with its ecliptic (rings every 50 km), gates with their real models, and the cluster's real backdrop as the sky (planets, moons, rings, nebulae), with the sun's direction taken from that backdrop.
  - Place the sector anywhere inside its backdrop, for example right next to a gas giant. Vanilla does the same.
  - Choose from every backdrop the installed game has (base game and DLCs), and play the cluster's music.
- **Decoration:** a palette on the left like X4's station designer: one button per group, a grid of pictures (made by the editor from your installed game), a search box; click a picture, then click into the sector. The game's asteroid fields, gas clouds, nebulae, wreck fields, wrecks, landmarks, data vaults, nav beacons, single asteroids and anomalies, named as the game names them.
  - A field with ore, gas or scrap also adds the matching resource areas to the sector (since X4 9.00 a field only gives yield together with a resource area); the galaxy map shows each sector's resources as coloured dots, and the check list warns when field and resource area do not match.
  - Drag items with the mouse; Shift + drag changes the height.
  - The selected item gets move arrows (one per axis) and turn rings, as in X4's station designer.
  - Ships, gates and objects use the game's own textures with an X4-like shader: metal, smoothness, normal maps and glowing lights.
  - A 2D minimap of the sector.
  - Right-click menu: copy, cut, paste, delete, and connect gates and anomalies to another sector.
- **Controls:** W A S D / Q E to fly, right mouse button to turn, mouse wheel to zoom. Esc closes the front window.

- **AI and factions** (each sector, side panel): owner at game start (a claiming station of that faction is placed, then the game's own rules bring its factories, traders and patrols), a pirate station (pirate base, depot or anarchy port of a pirate faction; it claims nothing), "factions may claim / invade it", factions whose AI ignores the sector, and sector tags such as anarchy. Every list is read from your installed game and DLCs. The faction AI runs in patches of the X4 galaxy; in an own galaxy it does not run yet.
- **Export as a mod** (new, not yet tested in the game): **Export mod...** writes the galaxy into the export folder (normally the game's `extensions` folder): map, names, music, fields, objects, linked anomaly pairs, and a game start in the new galaxy. DLCs and mods the galaxy uses become dependencies of the mod. The editor never writes into a folder it did not create. In the project settings you choose the text page (the editor checks that no installed mod uses it) and the languages that get a text file.

Not yet: highways, stations and ships (a later, separate editor).

## Bug reports

**Debug...** in the top bar makes a debug report: one small zip (about 150 KB) on your desktop with the editor version,
system and graphics card, folder settings, the list of game extensions, the editor's notes and logs, a screenshot and,
if you want, your project. Your user name and X4 profile number are replaced in all paths; no game files go in.
Look inside, then send it with a few words on what you did: by e-mail to **debug@astrainferna.com** or as a
[GitHub issue](https://github.com/M-R-Reese/X4-Map-editor/issues). The window shows both, with Copy buttons.

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
