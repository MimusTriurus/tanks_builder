---
name: screenshot-isolated
description: "Render a target Node3D in ISOLATION (only the target is visible, in its own world) from a chosen camera angle, framed automatically to its bounding box, lit by a default directional light, and return the result as a PNG image for direct LLM inspection. Identify the target with 'nodeRef'. Choose the view with 'cameraView' (Front/Back/Left/Right/Top/Bottom). Pick a 'background' (SolidColor or Transparent) and, for SolidColor, a hex 'backgroundColor'. The target is rendered via a temporary off-screen copy, so the edited scene is never mutated. Output is a square 'resolution'x'resolution' PNG, capped to keep the response transportable. NOTE: rendering needs a real GPU — a Godot launched with '--headless' yields an empty image, surfaced here as a structured error rather than a blank PNG."
---

# Screenshot / Isolated Node

Render a target Node3D in ISOLATION (only the target is visible, in its own world) from a chosen camera angle, framed automatically to its bounding box, lit by a default directional light, and return the result as a PNG image for direct LLM inspection. Identify the target with 'nodeRef'. Choose the view with 'cameraView' (Front/Back/Left/Right/Top/Bottom). Pick a 'background' (SolidColor or Transparent) and, for SolidColor, a hex 'backgroundColor'. The target is rendered via a temporary off-screen copy, so the edited scene is never mutated. Output is a square 'resolution'x'resolution' PNG, capped to keep the response transportable. NOTE: rendering needs a real GPU — a Godot launched with '--headless' yields an empty image, surfaced here as a structured error rather than a blank PNG.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8427/api/tools/screenshot-isolated \
  -H "Content-Type: application/json" \
  -d '{
  "nodeRef": "string_value",
  "cameraView": "string_value",
  "background": "string_value",
  "backgroundColor": "string_value",
  "fieldOfView": 0,
  "nearClipPlane": 0,
  "farClipPlane": 0,
  "padding": 0,
  "resolution": 0
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8427/api/tools/screenshot-isolated -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8427/api/tools/screenshot-isolated \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "nodeRef": "string_value",
  "cameraView": "string_value",
  "background": "string_value",
  "backgroundColor": "string_value",
  "fieldOfView": 0,
  "nearClipPlane": 0,
  "farClipPlane": 0,
  "padding": 0,
  "resolution": 0
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `nodeRef` | `any` | Yes | Reference to the target Node3D to render in isolation (instanceId preferred, else path). |
| `cameraView` | `string` | No | Camera angle relative to the target's bounding box. Default: Front. |
| `background` | `string` | No | Background mode for the render: SolidColor (default) or Transparent. |
| `backgroundColor` | `string` | No | Hex background color (e.g. '#404040'). Used only when background is SolidColor. |
| `fieldOfView` | `number` | No | Camera vertical field of view in degrees. Default: 60. |
| `nearClipPlane` | `number` | No | Near clip plane distance. Default: 0.05. |
| `farClipPlane` | `number` | No | Far clip plane distance. Default: 4000. |
| `padding` | `number` | No | Framing multiplier around the object. 1.0 = tight fit, 1.5 = 50% extra space. Default: 1.2. |
| `resolution` | `integer` | No | Output image resolution in pixels (width = height). Default: 512. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "nodeRef": {
      "$ref": "#/$defs/com.IvanMurzak.Godot.MCP.Data.NodeRef",
      "description": "Reference to the target Node3D to render in isolation (instanceId preferred, else path)."
    },
    "cameraView": {
      "type": "string",
      "enum": [
        "Front",
        "Back",
        "Left",
        "Right",
        "Top",
        "Bottom"
      ],
      "description": "Camera angle relative to the target's bounding box. Default: Front."
    },
    "background": {
      "type": "string",
      "enum": [
        "SolidColor",
        "Transparent"
      ],
      "description": "Background mode for the render: SolidColor (default) or Transparent."
    },
    "backgroundColor": {
      "type": "string",
      "description": "Hex background color (e.g. '#404040'). Used only when background is SolidColor."
    },
    "fieldOfView": {
      "type": "number",
      "description": "Camera vertical field of view in degrees. Default: 60."
    },
    "nearClipPlane": {
      "type": "number",
      "description": "Near clip plane distance. Default: 0.05."
    },
    "farClipPlane": {
      "type": "number",
      "description": "Far clip plane distance. Default: 4000."
    },
    "padding": {
      "type": "number",
      "description": "Framing multiplier around the object. 1.0 = tight fit, 1.5 = 50% extra space. Default: 1.2."
    },
    "resolution": {
      "type": "integer",
      "description": "Output image resolution in pixels (width = height). Default: 512."
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

