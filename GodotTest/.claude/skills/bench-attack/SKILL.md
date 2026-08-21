---
name: bench-attack
description: Order the driven tank to attack another by index, as a right click on it does. It lays its gun and opens fire on its own once it has a firing lane, a clear one, a standstill, a laid gun and a loaded round - not before.
---

# Bench / Attack Tank

Order the driven tank to attack another by index, as a right click on it does. It lays its gun and opens fire on its own once it has a firing lane, a clear one, a standstill, a laid gun and a loaded round - not before.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8427/api/tools/bench-attack \
  -H "Content-Type: application/json" \
  -d '{
  "index": 0
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8427/api/tools/bench-attack -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8427/api/tools/bench-attack \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "index": 0
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `index` | `integer` | Yes | Zero-based index of the tank to attack. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "index": {
      "type": "integer",
      "description": "Zero-based index of the tank to attack."
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

