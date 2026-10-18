---
description: Sync the GDD (docs/gdd/*.md) into its copies in Fibery, space BoT - only the files whose content changed, git is the source of truth
argument-hint: [nothing = only what changed | file names, e.g. "units classes" | "all" | "status"]
---

Bring the Fibery copies of the GDD in line with `docs/gdd/*.md`.

**Git is the source of truth; Fibery is a mirror.** Every copy is overwritten
whole, and any edit made in Fibery is lost - each copy says so in its top
callout. Fibery only through `fibery-studio` (root `CLAUDE.md`).

Arguments: `$ARGUMENTS`

## What lives where

- `.claude/gdd-fibery.json` - map: file name -> view id, document `secret`,
  URL. Documents were created once (2026-10-18) and are only ever rewritten,
  never recreated: a recreated document changes its URL and breaks every
  link to it, the GDD's own cross-links included.
- `.claude/scripts/gdd_fibery.mjs` - the deterministic part: `status` prints
  the git blob sha per file; `prepare [names]` writes the Fibery-ready chunks
  to `out/gdd-fibery/<name>/NN.md` and `out/gdd-fibery/manifest.json`. It
  joins wrapped lines (a single newline is a hard break in Fibery), points
  GDD cross-links at the Fibery documents and other relative links at
  GitHub `main`, prepends the "this is a copy" callout, and cuts at headings
  into <=12 KB chunks.
- **The sync state is the view's description**: it ends in `blob <sha>` - the
  `git hash-object` of the file that was last uploaded. No sha, or a
  different one, means the copy is stale.

## Steps

1. **Find what is stale.** `node .claude/scripts/gdd_fibery.mjs status`, and
   one `execute_code` that runs `query_views({space: "BoT", viewType:
   "document", withConfig: false})` and logs `name` + `description`. Stale =
   the description's `blob` differs from the file's sha.
   - A file in `docs/gdd/` with `mapped: false` is new: create a `document`
     view in space `BoT` named after its `# ` title, read its
     `config.documentSecret` with `query_views({id})`, add it to the map, and
     tell the user it has to be dragged into the `GDD` folder by hand (the
     MCP cannot place views in folders). Re-run `prepare` for every file
     linking to it, so their links point at Fibery, not GitHub.
   - A map entry with no file: the file was deleted or renamed. Do not
     delete the document; report it and ask.
   - Argument `status`: print the table (file, sha, Fibery blob, stale?) and
     stop. `all`: treat everything as stale. Names: only those.
   - Nothing stale: say so and stop.
2. **Prepare.** `node .claude/scripts/gdd_fibery.mjs prepare <stale names>`.
   Uncommitted changes upload too; say so if `git status docs/gdd` is dirty,
   because the description's sha then names a blob that is in no commit.
3. **Upload each file** by the procedure below. Several files: one subagent
   (`general-purpose`, model `sonnet`) per file, all in parallel, each given
   this file's path and told to run "Per-file procedure" for its name, to
   copy chunks exactly (Cyrillic as is, no `\u` escapes, typos kept) and to
   report the fingerprint. One or two small files: do it here.
4. **Verify and report.** Do not take the subagents' word: the first run's
   subagents reported "headings match" over a dropped table and a silently
   fixed typo. `status` prints every file's `fingerprint`; one `execute_code`
   computes the same over each mapped view's `content` (code in step (c)).
   Every pair equal and every `blob` current is the success claim. A
   mismatched one: run the per-file procedure for it again. Give the URLs of
   what changed.

## Per-file procedure

Input: `name`; from `out/gdd-fibery/manifest.json` its `secret`, `view`,
`sha`, `chunks`, `headings`, `fingerprint`.

**The procedure is idempotent: on any failure, run it again from (a).** (a)
records every block in the document - old copy and a half-sent new one alike
- and (c) deletes all of them, so a re-run never duplicates. Never resume
mid-way by hand.

a. **Remember the current blocks.** `execute_code`:
   ```js
   const d = await read_document({secret: "<secret>"});
   log(d.document.match(/^rootBlockIds\[\d+\]: (.*)$/m)[1]);
   ```
   Keep that comma-separated id list. Do not print the document: the big
   ones run to 60 KB.
b. **Append the chunks in order**: `Read` `out/gdd-fibery/<name>/01.md`, then
   `insert_document_blocks` with `inserts: [{parent: {blockId: null,
   position: "end"}, content: <the file, verbatim>}]`; then `02.md`, and so on,
   **one call at a time** - parallel calls append in whatever order they land.
   Verbatim: the file's text as read, Cyrillic as Cyrillic - **never `\uXXXX`
   escapes**, they triple the call's length and that is what cut calls off
   mid-chunk in the first run. No retyping from memory, no tidying. The
   insert's reply is huge (every created block); do not read it back. A
   failed insert: stop and run again from (a).
c. **Drop the old blocks, check, stamp.** One `execute_code`, filling in the
   ids from (a) and the manifest's values:
   ```js
   const ids = "<ids from (a)>".split(",");
   await delete_document_blocks({secret: "<secret>", deletions: ids.map(blockId => ({blockId}))});
   const md = (await query_views({id: "<view>"})).views[0].content;
   // same function as fingerprint() in .claude/scripts/gdd_fibery.mjs
   const i = md.search(/^# /m);
   const s = (i < 0 ? md : md.slice(i)).replace(/[^\p{L}\p{N}]/gu, "");
   let h = 0x811c9dc5;
   for (let k = 0; k < s.length; k++) { h ^= s.charCodeAt(k); h = Math.imul(h, 0x01000193) >>> 0; }
   log(`fingerprint ${h.toString(16)}/${s.length} expected <hash>/<len>`);
   log(md.split("\n").filter(l => /^#{1,6} /.test(l)).join("\n"));
   if (h.toString(16) === "<hash>" && s.length === <len>)
     await update_view({viewType: "document", id: "<view>", description:
       "Копия docs/gdd/<name>.md из репозитория tanks_builder. Правится в git, сюда синхронизируется командой /gdd-sync. blob <sha>"});
   ```
   The stamp is written only when the fingerprint matches.
d. **Report**: name, chunks sent, fingerprint found/expected. On a mismatch,
   compare the logged headings with the manifest's `headings` to say where
   text went missing - a mismatch is a failure even though every call
   returned success.

## Known limits

- Folders: the MCP has no folder tool. The `GDD` folder in space BoT and the
  order of documents in it are kept by hand in the Fibery sidebar; syncing
  never touches either.
- Section links (`file.md#anchor`) do not occur in the GDD now; if they
  appear, the script drops the anchor and links to the document.
- Comments left in Fibery on rewritten text are lost with the old blocks.
