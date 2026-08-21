---
name: node-create
description: |-
  Create a new Node in the currently edited Godot scene and return its structured data. Three creation modes (choose exactly one):
    1. Empty/typed — pass 'typeClassName' (a Godot class like 'Node3D', 'Sprite2D', 'Node'). Defaults to 'Node' when omitted.
    2. Instanced scene — pass 'instanceScenePath' (a res:// path to a .tscn/.scn PackedScene); the scene is instanced and added as a child.
  When both are supplied, 'instanceScenePath' wins. Optionally pass 'parentNodeRef' to parent the new Node (defaults to the edited scene root), 'name' to rename it, and 'index' to place it at a specific position among its siblings (child order is what a VBoxContainer/HBoxContainer lays out, so this removes the need to create an ordered UI tree strictly front-to-back). The new Node's owner is set to the edited scene root so it is saved with the scene. The returned data reports the resulting sibling 'index'.
---

# Node / Create

Create a new Node in the currently edited Godot scene and return its structured data. Three creation modes (choose exactly one):
  1. Empty/typed — pass 'typeClassName' (a Godot class like 'Node3D', 'Sprite2D', 'Node'). Defaults to 'Node' when omitted.
  2. Instanced scene — pass 'instanceScenePath' (a res:// path to a .tscn/.scn PackedScene); the scene is instanced and added as a child.
When both are supplied, 'instanceScenePath' wins. Optionally pass 'parentNodeRef' to parent the new Node (defaults to the edited scene root), 'name' to rename it, and 'index' to place it at a specific position among its siblings (child order is what a VBoxContainer/HBoxContainer lays out, so this removes the need to create an ordered UI tree strictly front-to-back). The new Node's owner is set to the edited scene root so it is saved with the scene. The returned data reports the resulting sibling 'index'.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8427/api/tools/node-create \
  -H "Content-Type: application/json" \
  -d '{
  "name": "string_value",
  "typeClassName": "string_value",
  "instanceScenePath": "string_value",
  "parentNodeRef": "string_value",
  "index": "string_value"
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8427/api/tools/node-create -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8427/api/tools/node-create \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "name": "string_value",
  "typeClassName": "string_value",
  "instanceScenePath": "string_value",
  "parentNodeRef": "string_value",
  "index": "string_value"
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `name` | `string` | No | Name for the new Node. When omitted, Godot's default name for the type is used. |
| `typeClassName` | `string` | No | Godot class name to instantiate (e.g. 'Node3D', 'Sprite2D', 'Node'). Used when 'instanceScenePath' is not provided. Defaults to 'Node'. |
| `instanceScenePath` | `string` | No | res:// path to a PackedScene (.tscn/.scn) to instance as the new Node. Takes precedence over 'typeClassName' when both are supplied. |
| `parentNodeRef` | `any` | No | Reference to the parent Node. When omitted, the Node is parented to the edited scene root. |
| `index` | `any` | No | 0-based position to insert the new Node at among its parent's children. Negative counts from the end (-1 = last). Out-of-range values clamp into range. When omitted, the Node is appended last (Godot's default). |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "name": {
      "type": "string",
      "description": "Name for the new Node. When omitted, Godot's default name for the type is used."
    },
    "typeClassName": {
      "type": "string",
      "description": "Godot class name to instantiate (e.g. 'Node3D', 'Sprite2D', 'Node'). Used when 'instanceScenePath' is not provided. Defaults to 'Node'."
    },
    "instanceScenePath": {
      "type": "string",
      "description": "res:// path to a PackedScene (.tscn/.scn) to instance as the new Node. Takes precedence over 'typeClassName' when both are supplied."
    },
    "parentNodeRef": {
      "$ref": "#/$defs/com.IvanMurzak.Godot.MCP.Data.NodeRef",
      "description": "Reference to the parent Node. When omitted, the Node is parented to the edited scene root."
    },
    "index": {
      "$ref": "#/$defs/System.Int32",
      "description": "0-based position to insert the new Node at among its parent's children. Negative counts from the end (-1 = last). Out-of-range values clamp into range. When omitted, the Node is appended last (Godot's default)."
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
    "System.Int32": {
      "type": "integer"
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

