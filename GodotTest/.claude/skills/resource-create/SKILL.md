---
name: resource-create
description: "Create a new Godot Resource (.tres/.res) on disk. A fresh instance of the Godot class named by 'typeClassName' (e.g. 'StandardMaterial3D', 'Resource', 'Curve') is instantiated, then saved to 'resourcePath' (which must be a res:// path ending in '.tres' or '.res') via ResourceSaver. The editor filesystem is updated so the new asset is importable immediately. Use 'resource-modify' afterwards to set its properties. Returns the new resource's identity (res:// path, uid, type)."
---

# Resource / Create

Create a new Godot Resource (.tres/.res) on disk. A fresh instance of the Godot class named by 'typeClassName' (e.g. 'StandardMaterial3D', 'Resource', 'Curve') is instantiated, then saved to 'resourcePath' (which must be a res:// path ending in '.tres' or '.res') via ResourceSaver. The editor filesystem is updated so the new asset is importable immediately. Use 'resource-modify' afterwards to set its properties. Returns the new resource's identity (res:// path, uid, type).

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8427/api/tools/resource-create \
  -H "Content-Type: application/json" \
  -d '{
  "resourcePath": "string_value",
  "typeClassName": "string_value"
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8427/api/tools/resource-create -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8427/api/tools/resource-create \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "resourcePath": "string_value",
  "typeClassName": "string_value"
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `resourcePath` | `string` | Yes | res:// path for the new resource file, ending in '.tres' or '.res' (e.g. 'res://materials/wood.tres'). |
| `typeClassName` | `string` | No | Godot class to instantiate for the resource (must derive from Resource), e.g. 'StandardMaterial3D', 'Curve', 'Resource'. Defaults to 'Resource'. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "resourcePath": {
      "type": "string",
      "description": "res:// path for the new resource file, ending in '.tres' or '.res' (e.g. 'res://materials/wood.tres')."
    },
    "typeClassName": {
      "type": "string",
      "description": "Godot class to instantiate for the resource (must derive from Resource), e.g. 'StandardMaterial3D', 'Curve', 'Resource'. Defaults to 'Resource'."
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
      "$ref": "#/$defs/com.IvanMurzak.Godot.MCP.Data.ResourceInfo",
      "description": "Identity of a resource on disk: its res:// path, uid:// (when assigned), and Godot type."
    }
  },
  "$defs": {
    "com.IvanMurzak.Godot.MCP.Data.ResourceInfo": {
      "type": "object",
      "properties": {
        "resourcePath": {
          "type": "string",
          "description": "res:// path of the resource, e.g. 'res://materials/wood.tres'."
        },
        "uid": {
          "type": "string",
          "description": "uid:// identifier of the resource (e.g. 'uid://abc123'), or null when the resource has no uid."
        },
        "type": {
          "type": "string",
          "description": "Godot type recorded for the resource by the import pipeline (e.g. 'PackedScene', 'Texture2D', 'Resource'), or null when unknown."
        }
      },
      "description": "Identity of a resource on disk: its res:// path, uid:// (when assigned), and Godot type."
    }
  },
  "required": [
    "result"
  ]
}
```

