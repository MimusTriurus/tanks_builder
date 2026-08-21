---
name: bench-destroy
description: Destroy a tank outright, as a middle click on it does. Reversible with bench-reset.
---

# Bench / Destroy Tank

Destroy a tank outright, as a middle click on it does. Reversible with bench-reset.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8427/api/tools/bench-destroy \
  -H "Content-Type: application/json" \
  -d '{
  "index": 0
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8427/api/tools/bench-destroy -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8427/api/tools/bench-destroy \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "index": 0
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `index` | `integer` | Yes | Zero-based index of the tank to destroy. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "index": {
      "type": "integer",
      "description": "Zero-based index of the tank to destroy."
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

