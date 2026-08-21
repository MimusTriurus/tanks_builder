---
name: resource-find
description: |-
  Find resources in the Godot project's res:// filesystem. Combine any of:
    - 'resourcePath' — an exact res:// path to resolve (also accepts a 'uid://' which is mapped to its path).
    - 'uid' — a 'uid://' identifier to resolve to its res:// path.
    - 'typeFilter' — a Godot type name (e.g. 'PackedScene', 'Texture2D', 'Resource'); only files whose importer-assigned type equals or derives from it are returned.
    - 'directory' — a res:// directory to scope the type-filtered scan (defaults to the whole project).
  With 'resourcePath' or 'uid', returns that single resource (a structured error if it does not exist). With 'typeFilter' (and optional 'directory'), recursively scans the editor filesystem and returns every match. Each hit carries res:// path, uid, and type.
  NOTE: 'resourcePath' and 'uid' are mutually exclusive — when both are supplied 'uid' takes precedence and 'resourcePath' is ignored. For a direct path/uid lookup, 'typeFilter' and 'directory' are ignored (they only apply to the type-filtered scan).
---

# Resource / Find

Find resources in the Godot project's res:// filesystem. Combine any of:
  - 'resourcePath' — an exact res:// path to resolve (also accepts a 'uid://' which is mapped to its path).
  - 'uid' — a 'uid://' identifier to resolve to its res:// path.
  - 'typeFilter' — a Godot type name (e.g. 'PackedScene', 'Texture2D', 'Resource'); only files whose importer-assigned type equals or derives from it are returned.
  - 'directory' — a res:// directory to scope the type-filtered scan (defaults to the whole project).
With 'resourcePath' or 'uid', returns that single resource (a structured error if it does not exist). With 'typeFilter' (and optional 'directory'), recursively scans the editor filesystem and returns every match. Each hit carries res:// path, uid, and type.
NOTE: 'resourcePath' and 'uid' are mutually exclusive — when both are supplied 'uid' takes precedence and 'resourcePath' is ignored. For a direct path/uid lookup, 'typeFilter' and 'directory' are ignored (they only apply to the type-filtered scan).

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8427/api/tools/resource-find \
  -H "Content-Type: application/json" \
  -d '{
  "resourcePath": "string_value",
  "uid": "string_value",
  "typeFilter": "string_value",
  "directory": "string_value"
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8427/api/tools/resource-find -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8427/api/tools/resource-find \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "resourcePath": "string_value",
  "uid": "string_value",
  "typeFilter": "string_value",
  "directory": "string_value"
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `resourcePath` | `string` | No | Optional exact res:// path (or uid://) to resolve to a single resource. |
| `uid` | `string` | No | Optional uid:// identifier to resolve to a single resource. |
| `typeFilter` | `string` | No | Optional Godot type name to filter by (e.g. 'PackedScene', 'Texture2D'). Matches the file's importer-assigned type, including subclasses. |
| `directory` | `string` | No | Optional res:// directory to scope a type-filtered scan. Defaults to the project root. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "resourcePath": {
      "type": "string",
      "description": "Optional exact res:// path (or uid://) to resolve to a single resource."
    },
    "uid": {
      "type": "string",
      "description": "Optional uid:// identifier to resolve to a single resource."
    },
    "typeFilter": {
      "type": "string",
      "description": "Optional Godot type name to filter by (e.g. 'PackedScene', 'Texture2D'). Matches the file's importer-assigned type, including subclasses."
    },
    "directory": {
      "type": "string",
      "description": "Optional res:// directory to scope a type-filtered scan. Defaults to the project root."
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
      "$ref": "#/$defs/com.IvanMurzak.Godot.MCP.Data.ResourceFindResult",
      "description": "Result of resource-find: a count plus the list of matching resources (path/uid/type)."
    }
  },
  "$defs": {
    "System.Collections.Generic.List(com.IvanMurzak.Godot.MCP.Data.ResourceInfo)": {
      "type": "array",
      "items": {
        "$ref": "#/$defs/com.IvanMurzak.Godot.MCP.Data.ResourceInfo",
        "description": "Identity of a resource on disk: its res:// path, uid:// (when assigned), and Godot type."
      }
    },
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
    },
    "com.IvanMurzak.Godot.MCP.Data.ResourceFindResult": {
      "type": "object",
      "properties": {
        "count": {
          "type": "integer",
          "description": "Number of matching resources found."
        },
        "resources": {
          "$ref": "#/$defs/System.Collections.Generic.List(com.IvanMurzak.Godot.MCP.Data.ResourceInfo)",
          "description": "The matching resources, each with its res:// path, uid, and type."
        }
      },
      "required": [
        "count"
      ],
      "description": "Result of resource-find: a count plus the list of matching resources (path/uid/type)."
    }
  },
  "required": [
    "result"
  ]
}
```

