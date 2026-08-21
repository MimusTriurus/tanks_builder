---
name: screenshot-camera
description: "Capture a screenshot from a specific Camera3D (or Camera2D) in the edited scene and return it as a PNG image for direct LLM inspection. Identify the camera with 'nodeRef' (instanceId preferred, else a scene-tree path). The camera's own world is rendered into a temporary off-screen SubViewport at the requested 'width'x'height', so the editor's live selection/camera is left untouched. Output edges are capped to keep the response transportable. NOTE: rendering needs a real GPU — a Godot launched with '--headless' yields an empty image, surfaced here as a structured error rather than a blank PNG."
---

# Screenshot / Camera

Capture a screenshot from a specific Camera3D (or Camera2D) in the edited scene and return it as a PNG image for direct LLM inspection. Identify the camera with 'nodeRef' (instanceId preferred, else a scene-tree path). The camera's own world is rendered into a temporary off-screen SubViewport at the requested 'width'x'height', so the editor's live selection/camera is left untouched. Output edges are capped to keep the response transportable. NOTE: rendering needs a real GPU — a Godot launched with '--headless' yields an empty image, surfaced here as a structured error rather than a blank PNG.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8427/api/tools/screenshot-camera \
  -H "Content-Type: application/json" \
  -d '{
  "nodeRef": "string_value",
  "width": 0,
  "height": 0
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8427/api/tools/screenshot-camera -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8427/api/tools/screenshot-camera \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "nodeRef": "string_value",
  "width": 0,
  "height": 0
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `nodeRef` | `any` | Yes | Reference to the camera Node (Camera3D or Camera2D) to capture from (instanceId preferred, else scene-tree path). |
| `width` | `integer` | No | Output width in pixels. Default 1920. Must be > 0 and within the dimension cap. |
| `height` | `integer` | No | Output height in pixels. Default 1080. Must be > 0 and within the dimension cap. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "nodeRef": {
      "$ref": "#/$defs/com.IvanMurzak.Godot.MCP.Data.NodeRef",
      "description": "Reference to the camera Node (Camera3D or Camera2D) to capture from (instanceId preferred, else scene-tree path)."
    },
    "width": {
      "type": "integer",
      "description": "Output width in pixels. Default 1920. Must be > 0 and within the dimension cap."
    },
    "height": {
      "type": "integer",
      "description": "Output height in pixels. Default 1080. Must be > 0 and within the dimension cap."
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

This tool does not return structured output.

