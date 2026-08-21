---
name: bench-drive
description: Order the driven tank to a cell, as a left click on empty ground does. The cell is clamped to the board. Another tank's cell is not a destination.
---

# Bench / Drive To Cell

Order the driven tank to a cell, as a left click on empty ground does. The cell is clamped to the board. Another tank's cell is not a destination.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8427/api/tools/bench-drive \
  -H "Content-Type: application/json" \
  -d '{
  "col": 0,
  "row": 0
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8427/api/tools/bench-drive -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8427/api/tools/bench-drive \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "col": 0,
  "row": 0
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `col` | `integer` | Yes | Column of the target cell. |
| `row` | `integer` | Yes | Row of the target cell. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "col": {
      "type": "integer",
      "description": "Column of the target cell."
    },
    "row": {
      "type": "integer",
      "description": "Row of the target cell."
    }
  },
  "required": [
    "col",
    "row"
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

