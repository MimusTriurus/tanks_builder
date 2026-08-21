---
name: node-set-parent
description: Reparent a Node under a new parent in the currently edited Godot scene, preserving its global transform by default (Godot's Node.Reparent). Identify the moving Node with 'nodeRef' and the destination with 'newParentNodeRef'. Set 'keepGlobalTransform' to false to keep the Node's local transform instead. Returns the reparented Node's updated structured data.
---

# Node / Set Parent

Reparent a Node under a new parent in the currently edited Godot scene, preserving its global transform by default (Godot's Node.Reparent). Identify the moving Node with 'nodeRef' and the destination with 'newParentNodeRef'. Set 'keepGlobalTransform' to false to keep the Node's local transform instead. Returns the reparented Node's updated structured data.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8427/api/tools/node-set-parent \
  -H "Content-Type: application/json" \
  -d '{
  "nodeRef": "string_value",
  "newParentNodeRef": "string_value",
  "keepGlobalTransform": false
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8427/api/tools/node-set-parent -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8427/api/tools/node-set-parent \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "nodeRef": "string_value",
  "newParentNodeRef": "string_value",
  "keepGlobalTransform": false
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `nodeRef` | `any` | Yes | Reference to the Node to reparent (instanceId preferred, else scene-tree path). |
| `newParentNodeRef` | `any` | Yes | Reference to the new parent Node. |
| `keepGlobalTransform` | `boolean` | No | When true (default), preserve the Node's global transform across the reparent. When false, keep its local transform. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "nodeRef": {
      "$ref": "#/$defs/com.IvanMurzak.Godot.MCP.Data.NodeRef",
      "description": "Reference to the Node to reparent (instanceId preferred, else scene-tree path)."
    },
    "newParentNodeRef": {
      "$ref": "#/$defs/com.IvanMurzak.Godot.MCP.Data.NodeRef",
      "description": "Reference to the new parent Node."
    },
    "keepGlobalTransform": {
      "type": "boolean",
      "description": "When true (default), preserve the Node's global transform across the reparent. When false, keep its local transform."
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
    "nodeRef",
    "newParentNodeRef"
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
  },
  "required": [
    "result"
  ]
}
```

