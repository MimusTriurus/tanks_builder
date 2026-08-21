---
name: script-attach-to-node
description: "Attach a script (C# '.cs' or GDScript '.gd') to a Node in the currently edited Godot scene — the Godot analog of adding a MonoBehaviour to a GameObject. Identify the target with 'nodeRef' (instanceId preferred, else scene-tree path) and the script with 'scriptPath' (a res:// script file that must exist). Pass an empty/whitespace/null 'scriptPath' to DETACH (clear) the node's script. The script resource is loaded and set via Node.SetScript; the scene is marked unsaved (persist with 'scene-save'). Returns the script's structured ScriptInfo on attach, or an identity record with a 'detached' status on clear."
---

# Script / Attach To Node

Attach a script (C# '.cs' or GDScript '.gd') to a Node in the currently edited Godot scene — the Godot analog of adding a MonoBehaviour to a GameObject. Identify the target with 'nodeRef' (instanceId preferred, else scene-tree path) and the script with 'scriptPath' (a res:// script file that must exist). Pass an empty/whitespace/null 'scriptPath' to DETACH (clear) the node's script. The script resource is loaded and set via Node.SetScript; the scene is marked unsaved (persist with 'scene-save'). Returns the script's structured ScriptInfo on attach, or an identity record with a 'detached' status on clear.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8427/api/tools/script-attach-to-node \
  -H "Content-Type: application/json" \
  -d '{
  "nodeRef": "string_value",
  "scriptPath": "string_value"
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8427/api/tools/script-attach-to-node -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8427/api/tools/script-attach-to-node \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "nodeRef": "string_value",
  "scriptPath": "string_value"
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `nodeRef` | `any` | Yes | Reference to the target Node (instanceId preferred, else scene-tree path). |
| `scriptPath` | `string` | No | res:// path of the script to attach, ending in '.cs' or '.gd'. Pass empty/whitespace/null to DETACH the node's current script. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "nodeRef": {
      "$ref": "#/$defs/com.IvanMurzak.Godot.MCP.Data.NodeRef",
      "description": "Reference to the target Node (instanceId preferred, else scene-tree path)."
    },
    "scriptPath": {
      "type": "string",
      "description": "res:// path of the script to attach, ending in '.cs' or '.gd'. Pass empty/whitespace/null to DETACH the node's current script."
    }
  },
  "$defs": {
    "com.IvanMurzak.Godot.MCP.Data.NodeRef": {
      "type": "object",
      "properties": {
        "instanceId": {
          "type": "integer",
          "description": "Instance id of the Node (Godot GodotObject.GetInstanceId()). If '0', treated as unset. Priority: 1."
        },
        "path": {
          "type": "string",
          "description": "Scene-tree path of the Node, e.g. '/root/Main/Player' or 'Main/Player'. Priority: 2."
        }
      },
      "required": [
        "instanceId"
      ],
      "description": "Reference to a Godot Node in the scene tree, located by scene-tree path or instance id."
    }
  },
  "required": [
    "nodeRef"
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

