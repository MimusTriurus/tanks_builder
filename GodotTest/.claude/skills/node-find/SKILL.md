---
name: node-find
description: "Find a Node in the currently edited Godot scene by instance id or scene-tree path and return its structured data (instanceId, name, path, type, attached script, child count). Provide a 'nodeRef' identifying the Node (instanceId is preferred; a path like '/root/Main/Player' or 'Main/Player' is resolved relative to the edited scene root). Set 'hierarchyDepth' > 0 to include children: 1 = direct children, 2 = grandchildren, etc. A node that cannot be resolved (bad ref, no edited scene, path not found) yields a structured error, not a null result."
---

# Node / Find

Find a Node in the currently edited Godot scene by instance id or scene-tree path and return its structured data (instanceId, name, path, type, attached script, child count). Provide a 'nodeRef' identifying the Node (instanceId is preferred; a path like '/root/Main/Player' or 'Main/Player' is resolved relative to the edited scene root). Set 'hierarchyDepth' > 0 to include children: 1 = direct children, 2 = grandchildren, etc. A node that cannot be resolved (bad ref, no edited scene, path not found) yields a structured error, not a null result.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8427/api/tools/node-find \
  -H "Content-Type: application/json" \
  -d '{
  "nodeRef": "string_value",
  "hierarchyDepth": 0
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8427/api/tools/node-find -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8427/api/tools/node-find \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "nodeRef": "string_value",
  "hierarchyDepth": 0
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `nodeRef` | `any` | Yes | Reference to the Node to find (instanceId preferred, else scene-tree path). |
| `hierarchyDepth` | `integer` | No | Depth of children to include. 0 = the target Node only; 1 = one layer of children; etc. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "nodeRef": {
      "$ref": "#/$defs/com.IvanMurzak.Godot.MCP.Data.NodeRef",
      "description": "Reference to the Node to find (instanceId preferred, else scene-tree path)."
    },
    "hierarchyDepth": {
      "type": "integer",
      "description": "Depth of children to include. 0 = the target Node only; 1 = one layer of children; etc."
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
      "$ref": "#/$defs/com.IvanMurzak.Godot.MCP.Data.NodeData",
      "description": "Structured snapshot of a Godot Node: identity (instanceId/name/path), type, optional attached-script path, sibling index, child count, and optional children."
    }
  },
  "$defs": {
    "System.Collections.Generic.List(com.IvanMurzak.Godot.MCP.Data.NodeData)": {
      "type": "array",
      "items": {
        "$ref": "#/$defs/com.IvanMurzak.Godot.MCP.Data.NodeData",
        "description": "Structured snapshot of a Godot Node: identity (instanceId/name/path), type, optional attached-script path, sibling index, child count, and optional children."
      }
    },
    "com.IvanMurzak.Godot.MCP.Data.NodeData": {
      "type": "object",
      "properties": {
        "instanceId": {
          "type": "integer",
          "description": "Instance id of the Node (Godot GodotObject.GetInstanceId()). Stable identity within the session."
        },
        "name": {
          "type": "string",
          "description": "Node name (the last segment of its scene-tree path)."
        },
        "path": {
          "type": "string",
          "description": "Absolute scene-tree path of the Node, e.g. '/root/Main/Player'."
        },
        "type": {
          "type": "string",
          "description": "Godot class name of the Node, e.g. 'Node3D', 'Sprite2D'. The Godot analog of a Unity component set."
        },
        "scriptResourcePath": {
          "type": "string",
          "description": "res:// path of the script attached to the Node, or null when no script is attached."
        },
        "index": {
          "type": "integer",
          "description": "0-based position of the Node among its parent's children, excluding internal children; this is the order a container (VBoxContainer/HBoxContainer/GridContainer) lays out and the order CanvasItems draw in. Never negative. Meaningless for the edited scene root, which the editor parents under its own tree — 'node-reorder' refuses it for that reason. Use 'node-reorder' (or 'node-create' with 'index') to change it."
        },
        "childCount": {
          "type": "integer",
          "description": "Number of direct children of the Node (excluding internal children)."
        },
        "children": {
          "$ref": "#/$defs/System.Collections.Generic.List(com.IvanMurzak.Godot.MCP.Data.NodeData)",
          "description": "Direct/recursive children, populated only when a hierarchy depth > 0 was requested. Null when no hierarchy was requested."
        }
      },
      "required": [
        "instanceId",
        "index",
        "childCount"
      ],
      "description": "Structured snapshot of a Godot Node: identity (instanceId/name/path), type, optional attached-script path, sibling index, child count, and optional children."
    }
  }
}
```

