---
name: scene-list-opened
description: "List every scene currently open in the Godot editor as a shallow snapshot (res:// path, and whether it is the active/edited scene). The active scene additionally carries its root Node's name and instanceId. Use 'scene-get-data' for the deep scene-tree view."
---

# Scene / List Opened

List every scene currently open in the Godot editor as a shallow snapshot (res:// path, and whether it is the active/edited scene). The active scene additionally carries its root Node's name and instanceId. Use 'scene-get-data' for the deep scene-tree view.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8427/api/tools/scene-list-opened \
  -H "Content-Type: application/json" \
  -d '{
  "nothing": "string_value"
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8427/api/tools/scene-list-opened -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8427/api/tools/scene-list-opened \
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
      "$ref": "#/$defs/System.Collections.Generic.List(com.IvanMurzak.Godot.MCP.Data.SceneData)"
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
    },
    "System.Collections.Generic.List(com.IvanMurzak.Godot.MCP.Data.SceneData)": {
      "type": "array",
      "items": {
        "$ref": "#/$defs/com.IvanMurzak.Godot.MCP.Data.SceneData",
        "description": "Shallow snapshot of an open Godot scene: res:// path, root-node name/instanceId, and whether it is the editor's active (edited) scene."
      }
    }
  },
  "required": [
    "result"
  ]
}
```

