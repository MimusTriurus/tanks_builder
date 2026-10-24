---
description: Run a large feature or bug through the full pipeline — a Fibery card in BoT/Work Item, a feat/ or fix/BOT-N branch, a plan the user approves, the work, a pull request, and the card's state kept in step. Only when the user invokes /task.
argument-hint: "<what to build or fix> | BOT-N | done BOT-N"
disable-model-invocation: true
---

# /task — the Fibery → GitHub pipeline for a large change

The rules are in `docs/workflow.md`; read it first. This command is the
procedure. Fibery is reached only through the `fibery-studio` MCP server
(`mcp__fibery-studio__*`), GitHub through `gh` (if it is not on `PATH`:
`"C:\Program Files\GitHub CLI\gh.exe"`). Card text, comments, commit
subjects and the PR are written in Russian, as the rest of the project.

Arguments: `$ARGUMENTS`

- **A description** → start a new card (step 1).
- **`BOT-N`** → pick up that card where it stands: read it (state, Branch,
  Pull Request, description, comments) and continue from the matching step.
- **`done BOT-N`** → step 7 only.

Constants: database `BoT/Work Item`; fields `BoT/Name`, `BoT/Type`,
`BoT/Area`, `BoT/Priority`, `BoT/Tags` (a collection: `add_collection_items`),
`BoT/Description` (a document: write it with `insert_document_blocks` through
its secret), `BoT/Branch`, `BoT/Pull Request`, `BoT/Epic`; states Backlog,
Ready, In Progress, In Review, Done, Won't Do (set with `set_state`). Look
option ids up by name with `query` on `BoT/Type_BoT/Work Item`,
`BoT/Area_BoT/Work Item`, `BoT/Priority_BoT/Work Item`,
`BoT/Tags_BoT/Work Item` — do not hard-code them.

## 1. Card

1. From the description decide Name (one line, without the number), Type
   (Feature/Bug), Area (3D bench, Match, Map editor, Pipeline, GDD, Tools,
   2D legacy), Priority (default Medium) and Tags (Performance, Gameplay,
   Graphics, Effects, UI, Tech debt, Tooling; one or more). Ask only for what
   the request does not settle.
2. Create the card and add its tags; its `fibery/public-id` *N* makes it
   `BOT-N`, and the rule *Prefix name with BOT-N* puts `BOT-N: ` before its
   name (check it did).
3. Fill the description with the template from `docs/workflow.md` (bug: Steps
   to reproduce / Expected / Actual / Notes; feature: Goal / Definition of
   Done / Notes). Put what is known; leave open questions under Notes.
4. Tell the user the card's link and `BOT-N`.

## 2. Plan — the user approves before any code

1. Research: the area's code, the GDD (`docs/gdd/`) where the change touches
   the rules, and the docs the shop's `CLAUDE.md` points to for the area
   (`godot/CLAUDE.md`, `pipeline/CLAUDE.md`). For a large task, use an
   Explore or Plan agent.
2. Write the plan: what changes in which files, departures from the GDD and
   why, how it will be checked (captures and what to look for in them,
   `--perf` against a baseline, rendered layers for the pipeline; the
   `--selftest` only if it is to be run), risks, and the order of the steps.
3. Append it to the card's description under `## Plan`, and present it to the
   user (`EnterPlanMode`/`ExitPlanMode` when available). **Stop until the user
   approves.** Changes they ask for go into the card's plan too.

## 3. Branch

1. `git status` must be clean apart from what the shops' CLAUDE.md says is
   the user's (`godot/project.godot`, untracked `.uid`): stop and ask
   otherwise. Never `git stash` here. `git checkout main`,
   `git pull --ff-only`. If `main` is ahead of `origin/main`, say so and ask
   whether to push it first: a branch cut from an unpushed `main` carries
   those commits into its PR. `git config core.hooksPath` must say `.githooks`
   (set it if not: the hooks keep `BOT-N: ` on every commit and call Git LFS).
2. `git checkout -b feat/BOT-N-<slug>` (`fix/` for a Bug); slug = a few
   lowercase latin words joined by `-`.
3. `git push -u origin <branch>`.
4. Card: state *In Progress*, `BoT/Branch` = the branch name.

## 4. Work

Follow the plan and the shop's `CLAUDE.md`. Commit in logical steps, the
subject `BOT-N: ` and then the repo's style (Russian, what the change does),
ending with the session's attribution trailer:

```
BOT-N: <что делает изменение>

<зачем, если это надо сказать>

Co-Authored-By: <as the session's attribution says>
```

The hooks add the prefix when it is missing and refuse another card's number
(`docs/workflow.md`, Шаги, 4); write it anyway.

When the plan turns out wrong, say so, update `## Plan` in the card, and agree
the change with the user before going on. Push as you go.

## 5. Check

Run what the plan named. A success is never reported from a return value: a
capture is looked at, a `--perf` run is read against its baseline. Record in
the card (a comment) and later in the PR what was run and what it showed, and
what could not be run. A failing check is reported as failing, not glossed.

## 6. Pull request

1. `gh pr create --base main --head <branch>`, titled with the card's name
   (it starts with `BOT-N: `), body: *Что и зачем*, *Как проверено*, the
   card's URL, then the session's PR attribution line.
2. Card: state *In Review*, `BoT/Pull Request` = the PR URL, a comment with
   the link and a two-line summary.
3. Bind the PR to the session (`mcp__ccd_pr__get_status`, then `bind_pr` if it
   is not bound).
4. **Stop.** The user reviews and merges, with a merge commit. Do not merge.

## 7. Close — `done BOT-N`, or when the user says the PR is merged

1. Confirm with `gh pr view <n> --json state,mergedAt` that it is MERGED.
2. Card → *Done* (the *Set Done Date* rule fills `Done Date`; check it did).
3. `git checkout main`, `git pull --ff-only`, `git branch -d <branch>`,
   `git push origin --delete <branch>`.

A task dropped on the way: card → *Won't Do* with a comment saying why, PR
closed unmerged with the same reason, branch deleted only if the user agrees.
