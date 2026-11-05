# Godot bench

Godot 4.7.2 mono, C#. Checks what a still picture can't: turret independence,
no axis "wander", behaviour while driving across the board. The pipeline that
makes its models is in [pipeline/CLAUDE.md](../pipeline/CLAUDE.md); the
contract between them and the repo-wide hard rules are in the
[root CLAUDE.md](../CLAUDE.md).

## Hard rules

- **Run `--selftest` only when the user explicitly asks.** To verify a change
  otherwise, use a capture (`--capture`, needs a window) or the scene itself.
- **Main work is the 3D tank: `res://scenes/Tank3D.tscn`, [docs/tank3d.md](docs/tank3d.md).**
  Scenes where the tank is an atlas sprite (`scenes/legacy/`) are the legacy
  2D line: nothing new for them. Boards, rules and shared modules (`HexField`,
  `Gunnery`, `MovementProfile`, `Terrain`, `Stage3D`…) are shared and current.
- **The board's main mode is relief; ramps are gone** (user's decision,
  2026-11-01). Levels join by slopes: up or down a level is an ordinary step
  from any side (`HexField.Slopes`). Walls, the caponier and the bunker stand on
  their cell's flat pad (`Relief.Pad`, BOT-12). Maps, their fairness and rules
  are judged in this mode. `Tank3D` and `Match3D` open on relief by default;
  prisms with ramps and walls are `--ramps`; the rules core (`--battle-run`)
  plays on the same board with sea around. Details — [docs/relief.md](docs/relief.md).
- **Player text only via keys** in `data/lang/ru.json` and `en.json` ("Game text").
- **Screens take colours, sizes and rims from `Ui` by role; the only tooltip is
  `UiTip`** ("UI — one theme").
- **Frame code allocates nothing; the 3D line prints with `Log.Print`**
  ("Frame code" below).

## Launch

```
dotnet build
"D:\Soft\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe" --path "D:\Projects\AgentCoding\BlenderMCP\godot" res://scenes/Tank3D.tscn
```

**Both paths absolute, the scene positional, always.** A relative `--path` is
right only from the repo root; from anywhere else Godot silently opens the
project manager with exit code 0, and `--selftest` reads as "the checks stopped
printing". The engine is not on `PATH`. Never rely on `run/main_scene` (see the
trap below). Selftest runs `--headless`; captures (`--capture`) need a window —
[docs/selftest.md](docs/selftest.md). A throw in `_Ready` under `--headless`
hangs the process instead of failing it: run long launches in the background.

Scenes, their flags and the legacy list — [docs/architecture.md](docs/architecture.md),
«Сцены и как их открыть». The ones used most: `Tank3D.tscn`; `Match3D.tscn`
(a match; `-- --quick` quick battle vs AI, `-- --hangar` with the hangar);
`Tree3D.tscn`; `Editor.tscn`. Language: `-- --lang ru|en` (default English).

## Launch trap

Godot started by a different engine build rewrites `project.godot` **and**
`TankSpriteTest.csproj`: it carries the version in `config/features` and in
`Godot.NET.Sdk`, and also moves `run/main_scene` to the last opened scene —
even on a positional launch.

- **The version is a fix, not damage.** Reverting it blindly points the project
  at an engine that isn't on the machine.
- **`run/main_scene` is damage.** A positional scene guards against the effect,
  not the write, so after a run restore that one line — never `git checkout`
  the file: it holds the user's edit (`run/main_run_args`).
- **The third version record is `NuGet.config`** (path to
  `GodotSharp/Tools/nupkgs` of the same build). The engine never touches it, so
  the pair drifts silently and `NU1301` speaks only on a clean machine without
  4.7.2 in `~/.nuget/packages`. Kept by hand, checked by eye against the other two.

The hook `.claude/hooks/godot_status.py` reports, after every command that
launched the engine, a changed `run/main_scene` or engine version (only those
lines; the user's edit is ignored) — inside Claude Code only.

## Layout

| dir | contents |
|---|---|
| `scenes/` | 3D line scenes and tools: `Tank3D`, `Match3D`, `Tree3D`, `Relief3D`, `Editor` |
| `scenes/legacy/` | legacy 2D sprite scenes |
| `scripts/` | C# by subject, not by line: `core` (infrastructure, `Ui`, `UiTip`, `L`), `rules` (GDD rules, `Battle`), `ai` (the AI on the core, `AiBrain`), `board` (field, maps, relief), `stage3d`, `water`, `props`, `walls`, `tank` (shared by both lines), `sprite` (2D), `tank3d` (`Tank3DBench.*`), `cel` (3D cel effects), `effects`, `editor`, `benches`, `test` (`SelfTest`) |
| `data/` | bench settings read from disk (`AssetRoot.Data`): `panel`, `bench`, `effects`, `events`, `terrain`, `classes`, `walls`, `editor_presets`, `match`, `lang/` |
| `maps/`, `perf/` | maps `maps/*.json` ([docs/editor.md](docs/editor.md)) and `--perf` baselines ([docs/perf.md](docs/perf.md)) |
| `tools/` | helper scripts outside the engine |

A new `.cs` goes in its subject's folder; the namespace is one, `TankSpriteTest`,
regardless of folder. Scene paths the code switches to itself (editor ↔ bench ↔
match) are constants in `Session`.

`scripts/test/SelfTest.cs` contains a NUL byte, so git treats it as binary and
won't normalise line endings: write it with LF or the diff churns 16k lines.

## Scene roots

**No shared module knows which scene started it.** Checked by grep: code that
reaches `Main.` from another file exists in exactly one place — `SelfTest`. The
one declared exception between roots is `Session`: the editor presses Run and
switches to the 3D bench (`Ctrl+F5` — to a match), and they return with the
`Edit  F5` button. The roots and the full breakdown — [docs/architecture.md](docs/architecture.md).

## Game text — `data/lang/`

Everything the player sees on the 3D line is a key in `data/lang/ru.json` and
`data/lang/en.json`; in code `L.T("key", …)` (`{0}` substitution) and
`L.N("key", n)` (plural forms: Russian one / few / many, English one / other);
loader `scripts/core/L.cs`. A key missing in a language falls back to Russian,
then to the key itself on screen with `lang: no "…"` in the log.

- **Each language has its font** — `"_font"` in its file, a file name in
  `assets/Fonts/` (licence alongside). English uses Black Ops One (OFL, Latin
  only), so `ru.json` has no font and keeps Godot's. `L.Apply()` at the start of
  `Tank3DBench.Build` sets it as `ThemeDB.FallbackFont` and the default theme
  font; `Ui.BoldFont` is the same face. It is wider: a non-wrapping line in a
  narrow frame must clip with an ellipsis (`TextOverrunBehavior`), not overflow.
- **New player text is a key in both files**, the prefix is part of the name
  (`hangar.`, `match.`, `squad.`…). A whole phrase is one key with
  placeholders, never glued pieces: English word order differs.
- **Code never compares displayed text**: an upgrade's kind is
  `Upgrade.Ability`/`Attack`, a branch header is found by `meta q_branch`.
- Not translated: logs (`Log.Print`, `GD.PrintErr`), exceptions, `--do` and
  `--quick-do` tokens, tags and ids (`LTP`, `raid`), the legacy 2D line and
  `panel.json`.

## UI — one theme, `Ui`

3D-line screens (lobby, squad, hangar, match HUD) take colours, type sizes,
rims and faces from `scripts/core/Ui.cs` by role — `Ui.Accent`, `Ui.Line`,
`Ui.Size(13)`, `Ui.DrawRim` — never their own literal: one colour means one
thing on every screen (olive = selected, amber = beyond base/buy). A new role
is added there, with what it means. The tooltip on a UI element is one —
`UiTip` (`scripts/core/UiTip.cs`: delay, beak, ring, body `UiTip.Box`), not a
local one and not the engine's. History of the move — [docs/tank3d.md](docs/tank3d.md),
«Тема — `Ui`».

## Frame code

What runs every frame (`_Process`, effect ticks, HUD) makes no garbage: every
~13 MB allocated, C# runs a gen0 collection of 10–25 ms mid-frame. After BOT-3
the median is ~6 KB per frame. To keep it so:

- shader parameter — `mat.Param("name", v)` (`ShaderParams`), not
  `SetShaderParameter("name", v)`: the string becomes a new `StringName` each time;
- no LINQ in a tick, no lambdas over locals (`Find`/`Exists`/`Where`), no
  `new[] { … }`, `new List`/`HashSet` — a field and `Clear()`, an index loop;
  `foreach` over `IReadOnlyList` boxes the enumerator;
- text is rebuilt when what it says changes (like `HudStamp`), not every frame;
- what is measured off a model is computed once per tag, like
  `Tank3DBench.MeasureRig`.

Who allocates shows per section: `kb:` in `frames.csv`, `section_alloc_kb` in
`summary.json`, KB in the F3 overlay ([docs/perf.md](docs/perf.md)).

**The 3D line prints with `Log.Print`, not `GD.Print`.** The debug build
flushes stdout after each `GD.Print` and waits for the pipe reader: one line in
a frame cost up to 300 ms (BOT-7, BOT-8). `Log.Print` queues the line for its
own thread, and the bench drains the queue in `_ExitTree`. This covers
`Tank3DBench*`, `Stage3D`, `Grass3D`, `BunkerProp` and all new 3D-line code.
`GD.PrintErr` goes direct.

## Controls

Five mouse gestures, all deciding by what stands on the cell, not by a
modifier; `TAB` — side panel; `R` reset, `F12` screenshot, `ESC` cancel order.
Full key list — [docs/ui.md](docs/ui.md), «Управление». **No free single keys
remain:** the next toggle needs a modifier or a panel row without a key.

## Docs — load on demand

Topic docs are in Russian; open one when its "when" matches.

| doc | when |
|---|---|
| [../docs/gdd/index.md](../docs/gdd/index.md) | changing game rules or checking the bench against them |
| [docs/architecture.md](docs/architecture.md) | finding where code lives, the scene list and roots, moving code between roots |
| [docs/tank3d.md](docs/tank3d.md) | anything on the 3D unit, `Tank3DBench`, match, quick battle, hangar, `--do` |
| [docs/hud3d-plan.md](docs/hud3d-plan.md) | selection, orders or aiming on `Tank3D` |
| [docs/relief.md](docs/relief.md) | relief or what stands on it |
| [docs/terrain.md](docs/terrain.md) | what a cell is or costs (`Foundation`, `Cover`, `terrain.json`) |
| [docs/board.md](docs/board.md) | `BoardMap`, maps, `--map-report`, sea level |
| [docs/editor.md](docs/editor.md) | the map editor, `MapRules`, `maps/*.json` |
| [docs/selftest.md](docs/selftest.md) | adding a check or reading a failure |
| [docs/perf.md](docs/perf.md) | measuring frame cost, `--perf`, baselines |
| [docs/combat.md](docs/combat.md) | shots, hits, ricochet, HE, pierce, ramming |
| [docs/motion.md](docs/motion.md) | how a tank drives and what moves meanwhile |
| [docs/blast.md](docs/blast.md) | explosions |
| [docs/props.md](docs/props.md) | trees, bushes, rocks, forest fire, `Tree3D` |
| [docs/water.md](docs/water.md) | anything wet |
| [docs/ui.md](docs/ui.md) | controls, keys, side panel |
| [docs/effects-2d-vs-3d.md](docs/effects-2d-vs-3d.md) | which sprite-line effect already exists on the 3D tank |
| [docs/new_style.md](docs/new_style.md) | photo assets of the new style (`--bed`, `--earth`…) |
| [docs/bench.md](docs/bench.md), [docs/events.md](docs/events.md), [docs/wall.md](docs/wall.md), [docs/wall-bench.md](docs/wall-bench.md), [docs/HEX_TILES_3D.md](docs/HEX_TILES_3D.md) | legacy 2D benches, walls, ramps |
| plans: [ai-plan](docs/ai-plan.md), [battle-core-plan](docs/battle-core-plan.md), [editor3d-plan](docs/editor3d-plan.md), [editor-ux-plan](docs/editor-ux-plan.md), [rocks-plan](docs/rocks-plan.md), [map-format3-plan](docs/map-format3-plan.md), [walls-relief-plan](docs/walls-relief-plan.md), [upgrade-icons-plan](docs/upgrade-icons-plan.md) | taking up a stage of that plan |
