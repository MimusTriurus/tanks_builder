---
name: bench-state
description: "Report every tank on the board: index, tag (LTP/MTP/HTP), which one is being driven, its cell, hull and turret facing in degrees, speed in px/s, penetrations taken out of the three that kill, and whether it is wrecked, burning, wading or engaging someone. The same figures --trace prints."
---

# Bench / Board State

Report every tank on the board: index, tag (LTP/MTP/HTP), which one is being driven, its cell, hull and turret facing in degrees, speed in px/s, penetrations taken out of the three that kill, and whether it is wrecked, burning, wading or engaging someone. The same figures --trace prints.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8427/api/tools/bench-state \
  -H "Content-Type: application/json" \
  -d '{
  "nothing": "string_value"
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8427/api/tools/bench-state -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8427/api/tools/bench-state \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "nothing": "string_value"
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `nothing` | `string` | No |  |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "nothing": {
      "type": "string"
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
      "type": "string"
    }
  },
  "required": [
    "result"
  ]
}
```

