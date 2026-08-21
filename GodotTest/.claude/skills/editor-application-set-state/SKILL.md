---
name: editor-application-set-state
description: |-
  Start or stop the Godot editor's play-run. Unlike Unity (an in-editor playmode toggle), Godot launches the game in a SEPARATE process. Use 'editor-application-get-state' to inspect the current state first.
  Inputs:
    - 'isPlaying' (default false): true starts a run, false stops any active run.
    - 'scene' (default 'main'): which scene to run when starting — 'main' runs the project's main scene (EditorInterface.PlayMainScene), 'current' runs the currently-edited scene (EditorInterface.PlayCurrentScene), or a res:// path runs that specific scene (EditorInterface.PlayCustomScene). Ignored when 'isPlaying' is false.
  Returns the post-change EditorStateData snapshot.
---

# Editor / Application / Set State

Start or stop the Godot editor's play-run. Unlike Unity (an in-editor playmode toggle), Godot launches the game in a SEPARATE process. Use 'editor-application-get-state' to inspect the current state first.
Inputs:
  - 'isPlaying' (default false): true starts a run, false stops any active run.
  - 'scene' (default 'main'): which scene to run when starting — 'main' runs the project's main scene (EditorInterface.PlayMainScene), 'current' runs the currently-edited scene (EditorInterface.PlayCurrentScene), or a res:// path runs that specific scene (EditorInterface.PlayCustomScene). Ignored when 'isPlaying' is false.
Returns the post-change EditorStateData snapshot.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8427/api/tools/editor-application-set-state \
  -H "Content-Type: application/json" \
  -d '{
  "isPlaying": false,
  "scene": "string_value"
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8427/api/tools/editor-application-set-state -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8427/api/tools/editor-application-set-state \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "isPlaying": false,
  "scene": "string_value"
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `isPlaying` | `boolean` | No | If true, start a play-run; if false, stop any active run. |
| `scene` | `string` | No | Which scene to run when starting: 'main' (project main scene), 'current' (the currently-edited scene), or a res:// path to a specific .tscn. Ignored when isPlaying is false. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "isPlaying": {
      "type": "boolean",
      "description": "If true, start a play-run; if false, stop any active run."
    },
    "scene": {
      "type": "string",
      "description": "Which scene to run when starting: 'main' (project main scene), 'current' (the currently-edited scene), or a res:// path to a specific .tscn. Ignored when isPlaying is false."
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
      "$ref": "#/$defs/com.IvanMurzak.Godot.MCP.Data.EditorStateData",
      "description": "Snapshot of the Godot editor run/play state: whether a play-run is active, the res:// path of the scene being run (if any), and the editor version string."
    }
  },
  "$defs": {
    "com.IvanMurzak.Godot.MCP.Data.EditorStateData": {
      "type": "object",
      "properties": {
        "isPlaying": {
          "type": "boolean",
          "description": "True when the editor is currently running a scene in a separate game process (EditorInterface.IsPlayingScene())."
        },
        "playingScene": {
          "type": "string",
          "description": "res:// path of the scene currently being run, or null when not playing (EditorInterface.GetPlayingScene())."
        },
        "editorVersion": {
          "type": "string",
          "description": "Godot editor version string, e.g. '4.5.1.stable.mono' (Engine.GetVersionInfo()['string'])."
        }
      },
      "required": [
        "isPlaying"
      ],
      "description": "Snapshot of the Godot editor run/play state: whether a play-run is active, the res:// path of the scene being run (if any), and the editor version string."
    }
  },
  "required": [
    "result"
  ]
}
```

