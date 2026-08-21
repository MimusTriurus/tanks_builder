---
name: editor-application-get-state
description: "Return the current run/play state of the Godot editor: whether a scene is currently being run in a separate game process, the res:// path of that scene (if any), and the editor version string. The Godot analog of Unity's 'editor-application-get-state'. Use 'editor-application-set-state' to start/stop a play-run."
---

# Editor / Application / Get State

Return the current run/play state of the Godot editor: whether a scene is currently being run in a separate game process, the res:// path of that scene (if any), and the editor version string. The Godot analog of Unity's 'editor-application-get-state'. Use 'editor-application-set-state' to start/stop a play-run.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8427/api/tools/editor-application-get-state \
  -H "Content-Type: application/json" \
  -d '{
  "nothing": "string_value"
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8427/api/tools/editor-application-get-state -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8427/api/tools/editor-application-get-state \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "nothing": "string_value"
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `nothing` | `string` | No |  |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "nothing": {
      "type": "string"
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

