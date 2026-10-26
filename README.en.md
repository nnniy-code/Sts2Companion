# STS2 Companion

![version](https://img.shields.io/badge/version-0.4.0-blue)
![license](https://img.shields.io/badge/license-MIT-green)
![game](https://img.shields.io/badge/Slay%20the%20Spire%202-v0.111.0-orange)

[中文说明](README.md) | English

A **local AI teammates** mod for *Slay the Spire 2*.

No networking, no second human player, no external AI service. The game creates AI-controlled teammates that travel, fight, pick events, and collect rewards with you.

> This is **v0.4.0, experimental**. 3-player runs have been launched into combat; 4-player mode is still experimental.  
> See [Known Issues](#known-issues) before playing.

---

## What This Mod Does

The mod creates a 2–4 character run:

- **Player 1**: you
- **Player 2–4**: local AI teammates

Default is **3 players** (you + 2 AI); use `＋` / `－` in character select for 2–4.

AI teammates act **in netId order** (2 → 3 → 4) and handle:

- Combat cards, targets, and multi-hit attacks
- Post-combat gold, potions, and card rewards
- Event choices and shared-event voting
- Map routing
- Rest sites (heal / smith / other available options)
- Treasure relic selection
- Card selection, discard, and scry prompts
- Act-transition readiness

The main menu gets a **`和 AI 玩`** (*Play with AI*) entry:

1. Pick your character.
2. Pick teammate 1.
3. Pick teammate 2 (3-player run).
4. Pick teammate 3 (4-player run).
5. Start.

Bottom controls:

```text
－   2 teammate(s) / 3 total   ＋
```

- 2 players: only `＋`
- 3 players: both
- 4 players: only `－`

**Each teammate can use a different character.** The list comes from the vanilla character-select screen, so **base-game characters and registered mod characters both appear**.

> The in-game UI is currently Chinese. The two confirmation buttons are:
>
> - `下一步：选队友（点这里）` — *Next: choose teammate (click here)*
> - `开始双人（点这里）` — *Start co-op (click here)*

---

## Supported Game Version

| Item | Value |
|---|---|
| Developed / tested on | **Slay the Spire 2 v0.111.0** |
| Manifest minimum | `0.107.1` |
| Engine | Godot 4.5 / .NET 9 |
| Mod type | DLL-only mod (`has_dll: true`) |

Notes:

- Development and hands-on testing were done on **v0.111.0**.
- The manifest declares `0.107.1` as the minimum, but older versions were **not individually tested**.
- Game updates can rename internal APIs and break the mod. If that happens, please open an issue with a log.

---

## Dependencies

**Required dependencies: none.**

The mod only uses the game's own `sts2.dll` / `GodotSharp.dll`; it does not call BaseLib, MinionLib, or RitsuLib code.

- **RitsuLib**: optional. If installed, the `和 AI 玩` button is integrated into its scrolling main-menu column; without it, the vanilla main-menu layout is used.
- **BaseLib / MinionLib**: not required by this mod. You only need them if a **modded character you select** requires them.
- The `dependencies` field in `Sts2Companion.json` stays empty; no prerequisite is forced.

---

## Installation

### 1. Download

Download the latest zip from Releases:

**https://github.com/nnniy-code/Sts2Companion/releases/latest**

The zip contains only:

```text
Sts2Companion.dll
Sts2Companion.json
```

### 2. Extract into the Mods folder

Put both files here:

```text
<your game folder>\Slay the Spire 2\mods\Sts2Companion\
```

Steam default example:

```text
D:\steam\steamapps\common\Slay the Spire 2\mods\Sts2Companion\
```

Correct layout:

```text
Slay the Spire 2\
└── mods\
    └── Sts2Companion\
        ├── Sts2Companion.dll
        └── Sts2Companion.json
```

Do not add an extra folder level, and do not rename the files.

### 3. Launch

- If your game has a mod manager, make sure `STS2 Companion` is enabled.
- Fully restart the game (returning to the main menu is not enough).
- A **`和 AI 玩`** entry should appear.

If **RitsuLib** is installed, the button is inserted into its scrolling main-menu column. Without RitsuLib, the vanilla menu layout is used (this combination has not been fully tested in-game).

### Uninstall

Delete the folder:

```text
Slay the Spire 2\mods\Sts2Companion\
```

---

## How to Start

1. On the main menu, click **`和 AI 玩`**.
2. Pick your character, then click **`下一步：选队友（点这里）`**.
3. Pick the AI teammate, then click **`开始双人（点这里）`**.
4. The run starts.

The AI acts automatically; play your own character normally.

### Controls

| Key | Action |
|---|---|
| `F10` | Switch view: temporarily take over the AI, then switch back |
| `T` | Open the right-side panel / command input |

### Language: Follow Game / Chinese / English

The panel has a **Language** dropdown:

- **Follow game**: reads the current game language; Chinese shows Chinese, other languages fall back to English.
- **中文**
- **English**

Changes apply immediately and are saved locally. Runtime game-language changes are followed automatically.

### AI Source: Local Rules or API

Press **T** to open the right-side panel, or click **AI 设置** in the top-right of the character-select screen. The **AI Source** dropdown has:

- **Local rules**: default, offline, uses the mod's built-in combat heuristics.
- **API (combat)**: calls an OpenAI-compatible `/chat/completions` endpoint to choose the AI's **combat card**.

API fields:

| Field | Example |
|---|---|
| Base URL | `https://api.openai.com/v1` |
| API Key | `sk-...` (leave empty for local compatible endpoints) |
| Model | `gpt-4o-mini` / `deepseek-chat` |

DeepSeek example:

```text
Base URL: https://api.deepseek.com
Model: deepseek-chat
```

Notes:

- API mode currently controls **combat card choice only**; events, map, rest sites, treasure, and rewards still use local rules.
- On timeout, HTTP error, or invalid card, the mod **falls back to local rules**; combat will not hang.
- The API is called once per card played, so expect latency and token cost.
- The API key is stored in plain text locally:

```text
D:\sts2_companion\state\companion_ai.json
```

or:

```text
%APPDATA%\SlayTheSpire2\sts2_companion\companion_ai.json
```

Do not share or commit this file. The repository `.gitignore` excludes the state directory.

### If Something Gets Stuck

Press **F10** to switch to the AI's view, complete the pending choice manually, then press F10 again.

Log file:

```text
D:\sts2_companion\state\mod_error.log
```

If there is no D: drive, the mod falls back to:

```text
%APPDATA%\SlayTheSpire2\sts2_companion\mod_error.log
```

---

## Known Issues

This is **v0.4.0, experimental**.

### 0. 3/4-player mode

- **3 players**: character selection, run creation, and combat have been tested.
- **4 players**: creation and combat work, but some extra-AI event choices may still fail. Treat it as experimental.
- Test 3 players first; send logs if a 4-player run stalls.

### 1. Shops

The AI does not buy items and **does not leave for you**.

- On entering a shop, the AI stands by and does not click anything.
- Buy what you need, then click the vanilla leave button yourself.
- After you leave, automatic map travel resumes and the AI follows your route.
- The old 15-second auto-leave and shop map guard are removed.

### 2. Boss Relics

AI act-transition readiness is handled, but **Boss relic selection can still stall if it uses the remote-choice path**.

- Press F10 and choose a relic manually.
- This is not fully solved yet.

### 3. Special Events / Minigames

Some events use their own UI or the underlying `PlayerChoiceSynchronizer`; these are not fully covered:

- Special card-selection minigames
- Some multi-page events
- UIs that require complex confirmation

If one gets stuck, press F10 and take over.

### 4. The AI Is Not a Expert Player

The AI aims to **finish content**, not to play optimally:

- Card order follows simple rules
- It may waste potions/gold
- It may not take the optimal map route
- It may perform poorly with complex modded cards

### 5. Mod Character Compatibility

Characters are read from `ModelDb.AllCharacters`, so modded characters appear.

However, different mods implement `CharacterModel` differently:

- Most characters work
- A few may fail during `Player.CreateForNewRun`
- On failure, the mod falls back to Ironclad/Silent and writes the error to the log

### 6. Chat Box Temporarily Disabled

The in-game chat box and AI conversation input are temporarily disabled for performance optimization. The AI teammate still acts automatically; chat will return later.

### 7. Not a Network Multiplayer Mod

This is a **local, simulated two-player run**:

- No second human is required
- No network co-op
- No two-human lobby

---

## Build From Source

Requirements:

- .NET 9 SDK
- *Slay the Spire 2* v0.111.0 (or a compatible version)
- `data_sts2_windows_x86_64\sts2.dll` and `GodotSharp.dll` from your game install

```powershell
git clone https://github.com/nnniy-code/Sts2Companion.git
cd Sts2Companion

dotnet build -c Release
```

If the game is not at the default path, override `GameDir`:

```powershell
dotnet build -c Release `
  -p:GameDir="E:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64"
```

Deploy to the game's Mods folder:

```powershell
.\deploy.ps1
```

`deploy.ps1` auto-detects the Steam game folder; you can also pass an explicit path.

> The repository does not contain `sts2.dll`, `GodotSharp.dll`, or game assets. Build against your own game installation.

---

## Release Download

**Latest release:**

https://github.com/nnniy-code/Sts2Companion/releases/latest

Release asset:

```text
Sts2Companion-0.4.0.zip
├── Sts2Companion.dll
└── Sts2Companion.json
```

If the Releases page has no asset yet, build from source using the instructions above.

---

## Project Layout

```text
Sts2Companion/
├── Mod.cs                 Entry point, UI, run setup
├── CoopAI.cs              Event/map/rest/treasure logic
├── CoopCombat.cs          Combat AI, card selection, rewards
├── CoopView.cs            F10 view switching
├── CoopWatch.cs           Stall diagnostics
├── Sts2Companion.csproj   C# project
├── Sts2Companion.json     Mod manifest
├── deploy.ps1             Build + deploy helper
├── HANDOFF.md             Developer handoff notes
├── MOD_NOTES.md           API / blocker notes
├── tools/                 Read-only investigation tools
├── README.md              Chinese README
├── README.en.md           English README
├── LICENSE
└── CHANGELOG.md
```

---

## Contributing / Feedback

Issues and pull requests are welcome.

When reporting a bug, include:

- Game version
- Whether RitsuLib or other UI mods are installed
- The last 50 lines of `mod_error.log`
- The room type where it got stuck (combat / event / rest / shop / treasure / Boss)

---

## License

MIT License. See [LICENSE](LICENSE).

You may use, modify, and redistribute this project, including commercially, as long as the copyright notice is retained.

---

## Disclaimer

This is an unofficial mod and is not affiliated with Mega Crit.

*Slay the Spire 2* and its assets are the property of Mega Crit. This project does not contain game files.
