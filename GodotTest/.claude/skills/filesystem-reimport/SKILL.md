---
name: filesystem-reimport
description: |-
  Re-scan the Godot project's res:// filesystem and/or refresh specific files, then wait for the import to settle before returning. The Godot analog of Unity's AssetDatabase.Refresh. Two modes:
    - Pass 'files' (a list of res:// paths) to refresh exactly those files — use this after editing a file's bytes outside the editor.
    - Omit 'files' (or pass an empty list) to trigger a full EditorFileSystem.Scan — use this after adding/removing files on disk so Godot picks up the change.
  Imported assets (textures, meshes, audio, fonts — anything with a '.import' sidecar) go through EditorFileSystem.ReimportFiles. Godot's NATIVE formats (.tscn/.tres/.gd/.cs/.gdshader) have no importer and are refreshed with EditorFileSystem.UpdateFile instead — queueing them for import would make the editor log "importer for type '' not found" while this tool reported success. The returned status names both groups.
  The call blocks until scanning completes (bounded), so a subsequent resource-find/get-data sees the settled state. Returns…
---

# FileSystem / Reimport

Re-scan the Godot project's res:// filesystem and/or refresh specific files, then wait for the import to settle before returning. The Godot analog of Unity's AssetDatabase.Refresh. Two modes:
  - Pass 'files' (a list of res:// paths) to refresh exactly those files — use this after editing a file's bytes outside the editor.
  - Omit 'files' (or pass an empty list) to trigger a full EditorFileSystem.Scan — use this after adding/removing files on disk so Godot picks up the change.
Imported assets (textures, meshes, audio, fonts — anything with a '.import' sidecar) go through EditorFileSystem.ReimportFiles. Godot's NATIVE formats (.tscn/.tres/.gd/.cs/.gdshader) have no importer and are refreshed with EditorFileSystem.UpdateFile instead — queueing them for import would make the editor log "importer for type '' not found" while this tool reported success. The returned status names both groups.
The call blocks until scanning completes (bounded), so a subsequent resource-find/get-data sees the settled state. Returns a short status string.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8427/api/tools/filesystem-reimport \
  -H "Content-Type: application/json" \
  -d '{
  "files": "string_value"
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8427/api/tools/filesystem-reimport -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8427/api/tools/filesystem-reimport \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "files": "string_value"
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `files` | `any` | No | Optional list of res:// file paths to refresh. Each is reimported when it is an imported asset (it has a '.import' sidecar) and refreshed via EditorFileSystem.UpdateFile when it is one of Godot's native formats (.tscn/.tres/.gd/.cs/...), which have no importer. When omitted/empty, a full filesystem scan is run instead. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "files": {
      "$ref": "#/$defs/System.Collections.Generic.List(System.String)",
      "description": "Optional list of res:// file paths to refresh. Each is reimported when it is an imported asset (it has a '.import' sidecar) and refreshed via EditorFileSystem.UpdateFile when it is one of Godot's native formats (.tscn/.tres/.gd/.cs/...), which have no importer. When omitted/empty, a full filesystem scan is run instead."
    }
  },
  "$defs": {
    "System.Collections.Generic.List(System.String)": {
      "type": "array",
      "items": {
        "type": "string"
      }
    }
  }
}
```

## Output

### Output JSON Schema

```json
{
  "type": "object",
  "properties": {
    "result": {
      "type": "string"
    }
  },
  "required": [
    "result"
  ]
}
```

