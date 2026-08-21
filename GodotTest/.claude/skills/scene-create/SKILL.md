---
name: scene-create
description: "Create a new Godot scene asset at a res://*.tscn path and open it as the active scene. A root Node is created (type given by 'rootTypeClassName', default 'Node'), packed into a PackedScene, saved to 'resourcePath', then opened in the editor. Pass 'rootName' to name the root Node. Returns the new scene's structured data. Use 'node-create' to add child nodes afterwards."
---

# Scene / Create

Create a new Godot scene asset at a res://*.tscn path and open it as the active scene. A root Node is created (type given by 'rootTypeClassName', default 'Node'), packed into a PackedScene, saved to 'resourcePath', then opened in the editor. Pass 'rootName' to name the root Node. Returns the new scene's structured data. Use 'node-create' to add child nodes afterwards.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8427/api/tools/scene-create \
  -H "Content-Type: application/json" \
  -d '{
  "resourcePath": "string_value",
  "rootTypeClassName": "string_value",
  "rootName": "string_value"
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8427/api/tools/scene-create -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8427/api/tools/scene-create \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "resourcePath": "string_value",
  "rootTypeClassName": "string_value",
  "rootName": "string_value"
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `resourcePath` | `string` | Yes | res:// path for the new scene file, ending in '.tscn' (e.g. 'res://levels/level_2.tscn'). |
| `rootTypeClassName` | `string` | No | Godot class for the scene's root Node (e.g. 'Node', 'Node2D', 'Node3D'). Defaults to 'Node'. |
| `rootName` | `string` | No | Name for the root Node. Defaults to the type's default name. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "resourcePath": {
      "type": "string",
      "description": "res:// path for the new scene file, ending in '.tscn' (e.g. 'res://levels/level_2.tscn')."
    },
    "rootTypeClassName": {
      "type": "string",
      "description": "Godot class for the scene's root Node (e.g. 'Node', 'Node2D', 'Node3D'). Defaults to 'Node'."
    },
    "rootName": {
      "type": "string",
      "description": "Name for the root Node. Defaults to the type's default name."
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

