---
name: resource-move
description: "Move or rename a resource file in the res:// filesystem. Both 'sourcePath' and 'destinationPath' must be res:// file paths. The resource file is moved with DirAccess; its sidecar '.import' metadata (when present) is moved alongside it so the import pipeline stays consistent. The editor filesystem is then rescanned so the new location is indexed. Returns the moved resource's identity at its new path. NOTE: hard-coded res:// path references in OTHER resources are not rewritten — prefer addressing assets by uid where stable references matter."
---

# Resource / Move

Move or rename a resource file in the res:// filesystem. Both 'sourcePath' and 'destinationPath' must be res:// file paths. The resource file is moved with DirAccess; its sidecar '.import' metadata (when present) is moved alongside it so the import pipeline stays consistent. The editor filesystem is then rescanned so the new location is indexed. Returns the moved resource's identity at its new path. NOTE: hard-coded res:// path references in OTHER resources are not rewritten — prefer addressing assets by uid where stable references matter.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8427/api/tools/resource-move \
  -H "Content-Type: application/json" \
  -d '{
  "sourcePath": "string_value",
  "destinationPath": "string_value"
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8427/api/tools/resource-move -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8427/api/tools/resource-move \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "sourcePath": "string_value",
  "destinationPath": "string_value"
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `sourcePath` | `string` | Yes | res:// path of the resource to move (the existing file). |
| `destinationPath` | `string` | Yes | res:// destination path (the new file location/name). |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "sourcePath": {
      "type": "string",
      "description": "res:// path of the resource to move (the existing file)."
    },
    "destinationPath": {
      "type": "string",
      "description": "res:// destination path (the new file location/name)."
    }
  },
  "required": [
    "sourcePath",
    "destinationPath"
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

