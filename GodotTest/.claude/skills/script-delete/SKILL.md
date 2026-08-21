---
name: script-delete
description: "Delete a Godot script file (C# '.cs' or GDScript '.gd') from the res:// filesystem. Fails if no file exists at the path. The script's identity (path, language) is captured BEFORE deletion and returned for confirmation. The file is removed with DirAccess, along with its Godot '.uid' sidecar when present, then the editor filesystem is rescanned and (for a '.cs' file) BOUNDED-settles. WARNING: nodes/resources referencing the deleted script will break — verify with 'script-read' first. Returns a structured ScriptInfo."
---

# Script / Delete

Delete a Godot script file (C# '.cs' or GDScript '.gd') from the res:// filesystem. Fails if no file exists at the path. The script's identity (path, language) is captured BEFORE deletion and returned for confirmation. The file is removed with DirAccess, along with its Godot '.uid' sidecar when present, then the editor filesystem is rescanned and (for a '.cs' file) BOUNDED-settles. WARNING: nodes/resources referencing the deleted script will break — verify with 'script-read' first. Returns a structured ScriptInfo.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8427/api/tools/script-delete \
  -H "Content-Type: application/json" \
  -d '{
  "scriptPath": "string_value"
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8427/api/tools/script-delete -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8427/api/tools/script-delete \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "scriptPath": "string_value"
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `scriptPath` | `string` | Yes | res:// path of the script file to delete, ending in '.cs' or '.gd'. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "scriptPath": {
      "type": "string",
      "description": "res:// path of the script file to delete, ending in '.cs' or '.gd'."
    }
  },
  "required": [
    "scriptPath"
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
      "$ref": "#/$defs/com.IvanMurzak.Godot.MCP.Data.ScriptInfo",
      "description": "Identity + (for reads) content of a script file: its res:// path, language, optional content, and a status note about any triggered compile/reload."
    }
  },
  "$defs": {
    "com.IvanMurzak.Godot.MCP.Data.ScriptInfo": {
      "type": "object",
      "properties": {
        "resourcePath": {
          "type": "string",
          "description": "res:// path of the script file, e.g. 'res://scripts/player.gd' or 'res://scripts/Enemy.cs'."
        },
        "language": {
          "type": "string",
          "description": "Script language: 'CSharp' for a .cs file, 'GDScript' for a .gd file."
        },
        "content": {
          "type": "string",
          "description": "The script's text content. Populated by 'script-read'; null on the write/delete/attach confirmation payloads (which echo identity only)."
        },
        "lineCount": {
          "type": "integer",
          "description": "Number of lines in 'content' when present (the read slice's line count), else 0."
        },
        "status": {
          "type": "string",
          "description": "Short human-readable status note, e.g. 'Script created; build settled.' or 'Script read.'. For C# writes/deletes this records the bounded compile/reload settle outcome; null when no status applies."
        }
      },
      "required": [
        "lineCount"
      ],
      "description": "Identity + (for reads) content of a script file: its res:// path, language, optional content, and a status note about any triggered compile/reload."
    }
  },
  "required": [
    "result"
  ]
}
```

