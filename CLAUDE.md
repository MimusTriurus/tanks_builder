# Board of Tanks — two workshops and the contract between them

Turn-based tank tactics on a hex board. The repository holds two workshops; each
has its own CLAUDE.md, loaded by directory.

**Talk to the user in Russian.** Code identifiers, logs and these instruction
files are English; `docs/`, the GDD, commit messages and player text stay as
they are (Russian docs, both languages in `godot/data/lang/`).

## Hard rules

- **Never report success from a return value.** The pipeline: composite the
  layers and look at the picture. The bench: a capture or `--selftest`.
- **Never `git stash`.** The Blender pipeline writes `Sprites/` in parallel;
  for an A/B, copy the one file out of `HEAD`.
- **Never commit `godot/project.godot`** and never `git checkout` it: it carries
  the user's own uncommitted edit. Stage files by name, not `git add -A`.
- **Commit only when asked, never push.** By default a commit goes straight to
  `main` (see "Task pipeline" for the exception).
- **No `python` on this machine** — it is the Microsoft Store stub: it prints
  "Python", exits 49 and does nothing. Edit files with Edit; for a script use
  `py -3`.
- **Don't touch the user's running Godot or Blender processes.**
- **Fibery means `fibery-studio` only** (stendhal-syndrome-studio.fibery.io,
  from `.mcp.json` and `godot/.mcp.json`). Any other Fibery connection in the
  session — e.g. the claude.ai connector to the wargaming workspace — is off
  limits for this project: no reads, no writes.

## Map

| path | what | instructions |
|---|---|---|
| `pipeline/` | Blender sprite/model pipeline: from generator `.glb` to layered isometric atlases and 3D game variants | [pipeline/CLAUDE.md](pipeline/CLAUDE.md) |
| `godot/` | Godot 4.7 mono (C#) bench: tanks drive, shoot and burn on the board | [godot/CLAUDE.md](godot/CLAUDE.md) |
| `docs/gdd/` | game rules (GDD), shared by both workshops | [docs/gdd/index.md](docs/gdd/index.md) |
| `assets/` | sources: `Input3D/` (.glb), `Parts3D/`, `Scenes/` (.blend, LFS), `Images/`, `Sounds/` (not in git); `Models/Trees/`, `Models/Mine/`, `Models/Bunker/` for the 3D bench; `Fonts/` with licences | [pipeline/docs/assets.md](pipeline/docs/assets.md) |
| `Icons/` | HUD action icons: `<name>.vectorcraft` source, chosen `<name>.svg` (read by the bench via `AssetRoot.Icons`), PNG `_64`/`_128`; `out/` is drafts | [godot/docs/hud3d-plan.md](godot/docs/hud3d-plan.md) |
| `Models/` | **3D contract (main line)**: `<TAG>/{tank.glb,tank.json}`; the pipeline writes it (`export_game`), `Tank3D` reads it; `.glb` via Git LFS, sidecar as text | [pipeline/docs/repro.md](pipeline/docs/repro.md), [godot/docs/tank3d.md](godot/docs/tank3d.md) |
| `Sprites/` | **2D contract (legacy)**: atlases the pipeline writes and the sprite scenes read | [pipeline/docs/pipeline.md](pipeline/docs/pipeline.md), «Контракт `Sprites/`» |

A change to a contract (`Models/`, `Sprites/`) is read on the other side, so it
lands in one commit on both sides.

## 3D is the main line; sprites are legacy

Decided 2026-09-27: the bench moved from sprite atlases to 3D units (cel
shading, up to ten tanks on screen, tracks as links). **Main work is on 3D
tanks**: the `Tank3D.tscn` scene and `Models/<TAG>/`, built by `/repro`
(`build_game` → `export_game`).

The 2D variant — tank sprites from `Sprites/` (the `Main` harness, `TankTest*`,
the events bench and the other sprite scenes; `/tank` and `/sprites` in the
pipeline) — is **legacy**. It stays in the repo, nothing new is built for it;
an effect, mechanic or fix a tank needs is made on the 3D model. If a task
doesn't say which scene, it means 3D.

## Where things go

- **A new section goes into the doc whose subject it touches.** A CLAUDE.md
  gets only what is true before you know what you are touching.
- `.claude/commands/`: `/tank`, `/sprites`, `/repro` (pipeline), `/gdd-sync`
  (GDD), `/task` (task pipeline). `.claude/hooks/godot_status.py`: after a
  command that launched Godot, reports engine rewrites of `project.godot` /
  `.csproj` (godot/CLAUDE.md, "Launch trap").
- `out/` anywhere is scratch output, not in git.

## Task pipeline (only when asked)

Only when the user asks for the pipeline does a change become a `BoT/Work Item`
card (`BOT-N`). It reaches `main` through a `feat/` or `fix/BOT-N-slug` branch
and a pull request; every commit there starts with `BOT-N: `, checked by the
hooks in `.githooks/` (which also call the Git LFS hooks). Enable them once per
clone: `git config core.hooksPath .githooks`. Steps, card fields and template —
[docs/workflow.md](docs/workflow.md); driven by `/task`.

## GDD mirror in Fibery

The GDD is mirrored into Fibery — space BoT, folder GDD, one document per
`docs/gdd/*.md`. Git is the source of truth: `/gdd-sync` overwrites the copies
whole, so an edit made in Fibery is lost at the next sync. How it works and the
file → document map — [.claude/commands/gdd-sync.md](.claude/commands/gdd-sync.md).
