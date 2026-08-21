---
name: scene-open
description: "Open a Godot scene asset (a res://*.tscn / *.scn PackedScene) in the editor and make it the active/edited scene. Pass 'resourcePath' as the res:// path. Returns the opened scene's structured data (the now-active scene). Use 'scene-list-opened' to see all open scenes afterwards."
---

# Scene / Open

Open a Godot scene asset (a res://*.tscn / *.scn PackedScene) in the editor and make it the active/edited scene. Pass 'resourcePath' as the res:// path. Returns the opened scene's structured data (the now-active scene). Use 'scene-list-opened' to see all open scenes afterwards.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8427/api/tools/scene-open \
  -H "Content-Type: application/json" \
  -d '{
  "resourcePath": "string_value"
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8427/api/tools/scene-open -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8427/api/tools/scene-open \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "resourcePath": "string_value"
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `resourcePath` | `string` | Yes | res:// path of the scene file to open, e.g. 'res://levels/level_1.tscn'. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "resourcePath": {
      "type": "string",
      "description": "res:// path of the scene file to open, e.g. 'res://levels/level_1.tscn'."
    }
  },
  "required": [
    "resourcePath"
  ]
}
```

## Output

### Output JSON Schema

```json
{
  "type": "object",
  "properties": {
    "result": {
      "$ref": "#/$defs/com.IvanMurzak.Godot.MCP.Data.SceneData",
      "description": "Shallow snapshot of an open Godot scene: res:// path, root-node name/instanceId, and whether it is the editor's active (edited) scene."
    }
  },
  "$defs": {
    "com.IvanMurzak.Godot.MCP.Data.SceneData": {
      "type": "object",
      "properties": {
        "resourcePath": {
          "type": "string",
          "description": "res:// path of the scene's .tscn file, or null for an unsaved/new scene."
        },
        "rootName": {
          "type": "string",
          "description": "Name of the scene's root Node, or null when the scene has no root yet."
        },
        "rootInstanceId": {
          "type": "integer",
          "description": "Instance id of the scene's root Node (0 when no root / not currently instanced in the editor)."
        },
        "isActive": {
          "type": "boolean",
          "description": "True when this scene is the editor's currently-edited (active) scene."
        }
      },
      "required": [
        "rootInstanceId",
        "isActive"
      ],
      "description": "Shallow snapshot of an open Godot scene: res:// path, root-node name/instanceId, and whether it is the editor's active (edited) scene."
    }
  },
  "required": [
    "result"
  ]
}
```

