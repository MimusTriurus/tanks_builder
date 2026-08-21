---
name: filesystem-list
description: "Browse the Godot project's res:// filesystem one directory at a time. Pass a 'path' (a res:// directory, e.g. 'res://materials' — a trailing slash is optional); omit it (or pass 'res://') to list the project root. Returns the directory's immediate sub-directories and files. Each file entry includes the importer-assigned resource type (e.g. 'PackedScene', 'Texture2D') and uid (when assigned) — read straight from the editor's filesystem index, so no resource is loaded. A non-existent or non-res:// path yields a structured error."
---

# FileSystem / List

Browse the Godot project's res:// filesystem one directory at a time. Pass a 'path' (a res:// directory, e.g. 'res://materials' — a trailing slash is optional); omit it (or pass 'res://') to list the project root. Returns the directory's immediate sub-directories and files. Each file entry includes the importer-assigned resource type (e.g. 'PackedScene', 'Texture2D') and uid (when assigned) — read straight from the editor's filesystem index, so no resource is loaded. A non-existent or non-res:// path yields a structured error.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8427/api/tools/filesystem-list \
  -H "Content-Type: application/json" \
  -d '{
  "path": "string_value"
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8427/api/tools/filesystem-list -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8427/api/tools/filesystem-list \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "path": "string_value"
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `path` | `string` | No | res:// directory to list (a trailing slash is optional). Omit or pass 'res://' for the project root. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "path": {
      "type": "string",
      "description": "res:// directory to list (a trailing slash is optional). Omit or pass 'res://' for the project root."
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
      "$ref": "#/$defs/com.IvanMurzak.Godot.MCP.Data.FileSystemListing",
      "description": "A res:// directory listing: the directory's path, its immediate sub-directories, and its immediate files."
    }
  },
  "$defs": {
    "System.Collections.Generic.List(com.IvanMurzak.Godot.MCP.Data.FileSystemEntry)": {
      "type": "array",
      "items": {
        "$ref": "#/$defs/com.IvanMurzak.Godot.MCP.Data.FileSystemEntry",
        "description": "One entry in a res:// directory listing: a sub-directory or a file, with its res:// path, name, kind, and (for files) resource type + uid."
      }
    },
    "com.IvanMurzak.Godot.MCP.Data.FileSystemEntry": {
      "type": "object",
      "properties": {
        "name": {
          "type": "string",
          "description": "The entry's leaf name (last path segment), e.g. 'wood.tres' or 'materials'."
        },
        "path": {
          "type": "string",
          "description": "Full res:// path of the entry. Directories end with a trailing '/', e.g. 'res://materials/'."
        },
        "isDirectory": {
          "type": "boolean",
          "description": "True when this entry is a directory; false when it is a file."
        },
        "resourceType": {
          "type": "string",
          "description": "For files: the Godot resource type the importer assigned (e.g. 'PackedScene', 'Texture2D', 'Resource'), or null for directories / unimported files."
        },
        "uid": {
          "type": "string",
          "description": "For files: the resource UID string (e.g. 'uid://abc123') when the file has one, else null. Directories have no uid."
        }
      },
      "required": [
        "isDirectory"
      ],
      "description": "One entry in a res:// directory listing: a sub-directory or a file, with its res:// path, name, kind, and (for files) resource type + uid."
    },
    "com.IvanMurzak.Godot.MCP.Data.FileSystemListing": {
      "type": "object",
      "properties": {
        "path": {
          "type": "string",
          "description": "res:// path of the listed directory (always ends with '/'), e.g. 'res://' or 'res://materials/'."
        },
        "directoryCount": {
          "type": "integer",
          "description": "Number of immediate sub-directories in this directory."
        },
        "fileCount": {
          "type": "integer",
          "description": "Number of immediate files in this directory."
        },
        "entries": {
          "$ref": "#/$defs/System.Collections.Generic.List(com.IvanMurzak.Godot.MCP.Data.FileSystemEntry)",
          "description": "Immediate children of the directory (sub-directories first, then files). Each entry carries its res:// path, kind, and — for files — resource type and uid."
        }
      },
      "required": [
        "directoryCount",
        "fileCount"
      ],
      "description": "A res:// directory listing: the directory's path, its immediate sub-directories, and its immediate files."
    }
  },
  "required": [
    "result"
  ]
}
```

