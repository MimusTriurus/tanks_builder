---
name: resource-delete
description: "Delete a resource file from the res:// filesystem. 'resourcePath' must be a res:// file path. The resource's identity (path, uid, type) is captured BEFORE deletion and returned for confirmation. The resource file is removed with DirAccess, along with its sidecar '.import' metadata when present, then the editor filesystem is rescanned. WARNING: other resources holding a hard reference to this file will break — verify with 'resource-find' first."
---

# Resource / Delete

Delete a resource file from the res:// filesystem. 'resourcePath' must be a res:// file path. The resource's identity (path, uid, type) is captured BEFORE deletion and returned for confirmation. The resource file is removed with DirAccess, along with its sidecar '.import' metadata when present, then the editor filesystem is rescanned. WARNING: other resources holding a hard reference to this file will break — verify with 'resource-find' first.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8427/api/tools/resource-delete \
  -H "Content-Type: application/json" \
  -d '{
  "resourcePath": "string_value"
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8427/api/tools/resource-delete -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8427/api/tools/resource-delete \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "resourcePath": "string_value"
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `resourcePath` | `string` | Yes | res:// path of the resource file to delete. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "resourcePath": {
      "type": "string",
      "description": "res:// path of the resource file to delete."
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

