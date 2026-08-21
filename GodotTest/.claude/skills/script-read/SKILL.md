---
name: script-read
description: "Read a Godot script file (C# '.cs' or GDScript '.gd') under res:// and return its content + metadata (res:// path, language, line count). Supports an optional 1-based 'lineFrom'/'lineTo' slice for partial reads (indices are clamped — out-of-range values read at-most the whole file). Pair with 'script-update'/'script-create' to write back. The file is read with Godot's FileAccess so it works on any res:// path (imported or not). Returns a structured ScriptInfo."
---

# Script / Read

Read a Godot script file (C# '.cs' or GDScript '.gd') under res:// and return its content + metadata (res:// path, language, line count). Supports an optional 1-based 'lineFrom'/'lineTo' slice for partial reads (indices are clamped — out-of-range values read at-most the whole file). Pair with 'script-update'/'script-create' to write back. The file is read with Godot's FileAccess so it works on any res:// path (imported or not). Returns a structured ScriptInfo.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8427/api/tools/script-read \
  -H "Content-Type: application/json" \
  -d '{
  "scriptPath": "string_value",
  "lineFrom": 0,
  "lineTo": 0
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8427/api/tools/script-read -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8427/api/tools/script-read \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "scriptPath": "string_value",
  "lineFrom": 0,
  "lineTo": 0
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `scriptPath` | `string` | Yes | res:// path of the script to read, e.g. 'res://scripts/player.gd' or 'res://scripts/Enemy.cs'. Must end in '.cs' or '.gd'. |
| `lineFrom` | `integer` | No | 1-based start line of the slice (default 1). Clamped into range. |
| `lineTo` | `integer` | No | 1-based inclusive end line of the slice (default -1 = end of file). Clamped into range. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "scriptPath": {
      "type": "string",
      "description": "res:// path of the script to read, e.g. 'res://scripts/player.gd' or 'res://scripts/Enemy.cs'. Must end in '.cs' or '.gd'."
    },
    "lineFrom": {
      "type": "integer",
      "description": "1-based start line of the slice (default 1). Clamped into range."
    },
    "lineTo": {
      "type": "integer",
      "description": "1-based inclusive end line of the slice (default -1 = end of file). Clamped into range."
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

