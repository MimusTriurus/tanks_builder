---
name: editor-selection-set
description: Set the Godot editor's node selection to the provided nodes (replacing any current selection). The Godot analog of Unity's 'editor-selection-set'. Each entry is a NodeRef (instanceId preferred, else scene-tree path relative to the edited scene root). All refs must resolve to live scene-tree nodes; otherwise the call throws and the selection is left unchanged. Pass an empty list to clear the selection. Use 'editor-selection-get' to inspect the current selection first. Returns the post-change SelectionData.
---

# Editor / Selection / Set

Set the Godot editor's node selection to the provided nodes (replacing any current selection). The Godot analog of Unity's 'editor-selection-set'. Each entry is a NodeRef (instanceId preferred, else scene-tree path relative to the edited scene root). All refs must resolve to live scene-tree nodes; otherwise the call throws and the selection is left unchanged. Pass an empty list to clear the selection. Use 'editor-selection-get' to inspect the current selection first. Returns the post-change SelectionData.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8427/api/tools/editor-selection-set \
  -H "Content-Type: application/json" \
  -d '{
  "select": "string_value"
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8427/api/tools/editor-selection-set -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8427/api/tools/editor-selection-set \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "select": "string_value"
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `select` | `any` | Yes | Nodes to select, each identified by instanceId (preferred) or scene-tree path. An empty list clears the selection. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "select": {
      "$ref": "#/$defs/System.Collections.Generic.List(com.IvanMurzak.Godot.MCP.Data.NodeRef)",
      "description": "Nodes to select, each identified by instanceId (preferred) or scene-tree path. An empty list clears the selection."
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
    },
    "System.Collections.Generic.List(com.IvanMurzak.Godot.MCP.Data.NodeRef)": {
      "type": "array",
      "items": {
        "$ref": "#/$defs/com.IvanMurzak.Godot.MCP.Data.NodeRef",
        "description": "Reference to a Godot Node in the scene tree, located by scene-tree path or instance id."
      }
    }
  },
  "required": [
    "select"
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
      "$ref": "#/$defs/com.IvanMurzak.Godot.MCP.Data.SelectionData",
      "description": "Snapshot of the Godot editor node selection: the selected nodes and the active (last-selected) node, each as structured NodeData."
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
    },
    "com.IvanMurzak.Godot.MCP.Data.SelectionData": {
      "type": "object",
      "properties": {
        "nodes": {
          "$ref": "#/$defs/System.Collections.Generic.List(com.IvanMurzak.Godot.MCP.Data.NodeData)",
          "description": "All currently-selected scene-tree nodes, in selection order."
        },
        "activeNode": {
          "$ref": "#/$defs/com.IvanMurzak.Godot.MCP.Data.NodeData",
          "description": "The active (last-selected) node, or null when the selection is empty. Godot has no first-class active-object concept; the last selected node is reported here."
        },
        "count": {
          "type": "integer",
          "description": "Number of selected nodes."
        }
      },
      "required": [
        "count"
      ],
      "description": "Snapshot of the Godot editor node selection: the selected nodes and the active (last-selected) node, each as structured NodeData."
    }
  },
  "required": [
    "result"
  ]
}
```

