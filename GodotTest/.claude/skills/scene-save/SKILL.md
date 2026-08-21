---
name: scene-save
description: "Save the currently edited Godot scene. With no 'path', saves back to the scene's existing res:// file (fails if the scene has never been saved — pass a 'path' in that case). With a 'path' (a res://*.tscn), saves the scene to that new location (save-as). Returns the saved scene's structured data."
---

# Scene / Save

Save the currently edited Godot scene. With no 'path', saves back to the scene's existing res:// file (fails if the scene has never been saved — pass a 'path' in that case). With a 'path' (a res://*.tscn), saves the scene to that new location (save-as). Returns the saved scene's structured data.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8427/api/tools/scene-save \
  -H "Content-Type: application/json" \
  -d '{
  "path": "string_value"
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8427/api/tools/scene-save -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8427/api/tools/scene-save \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "path": "string_value"
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `path` | `string` | No | Optional res:// destination path ending in '.tscn'/'.scn' for a save-as. When omitted, the scene is saved back to its existing file. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "path": {
      "type": "string",
      "description": "Optional res:// destination path ending in '.tscn'/'.scn' for a save-as. When omitted, the scene is saved back to its existing file."
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

