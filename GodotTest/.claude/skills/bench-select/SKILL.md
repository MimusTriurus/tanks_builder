---
name: bench-select
description: Take control of one of the tanks by index, as the number keys 1-3 do. The other two stay on the board in whatever state they were left.
---

# Bench / Select Tank

Take control of one of the tanks by index, as the number keys 1-3 do. The other two stay on the board in whatever state they were left.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8427/api/tools/bench-select \
  -H "Content-Type: application/json" \
  -d '{
  "index": 0
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8427/api/tools/bench-select -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8427/api/tools/bench-select \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "index": 0
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `index` | `integer` | Yes | Zero-based tank index, in the order bench-state lists them. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "index": {
      "type": "integer",
      "description": "Zero-based tank index, in the order bench-state lists them."
    }
  },
  "required": [
    "index"
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
      "type": "string"
    }
  },
  "required": [
    "result"
  ]
}
```

