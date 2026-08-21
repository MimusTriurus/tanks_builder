---
name: screenshot-viewport
description: "Capture the Godot editor's main viewport (the 3D or 2D editing surface) and return it as a PNG image for direct LLM inspection. Set 'mode' to '3d' (default) for the 3D editor viewport or '2d' for the 2D editor viewport. The longest output edge is capped to keep the response transportable; oversized captures are scaled down (aspect preserved). NOTE: the viewport must have a live GPU render — a Godot launched with '--headless' has no rendering device and yields an empty image, which surfaces here as a structured error rather than a blank PNG."
---

# Screenshot / Editor Viewport

Capture the Godot editor's main viewport (the 3D or 2D editing surface) and return it as a PNG image for direct LLM inspection. Set 'mode' to '3d' (default) for the 3D editor viewport or '2d' for the 2D editor viewport. The longest output edge is capped to keep the response transportable; oversized captures are scaled down (aspect preserved). NOTE: the viewport must have a live GPU render — a Godot launched with '--headless' has no rendering device and yields an empty image, which surfaces here as a structured error rather than a blank PNG.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8427/api/tools/screenshot-viewport \
  -H "Content-Type: application/json" \
  -d '{
  "mode": "string_value"
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8427/api/tools/screenshot-viewport -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8427/api/tools/screenshot-viewport \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "mode": "string_value"
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `mode` | `string` | No | Which editor viewport to capture: '3d' (the 3D editor surface, default) or '2d' (the 2D editor surface). Case-insensitive. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "mode": {
      "type": "string",
      "description": "Which editor viewport to capture: '3d' (the 3D editor surface, default) or '2d' (the 2D editor surface). Case-insensitive."
    }
  }
}
```

## Output

This tool does not return structured output.

