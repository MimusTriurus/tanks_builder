---
name: runtime-errors-clear
description: "Clear the captured in-game runtime-error buffer (read by 'runtime-errors-get'). Useful for isolating errors to a specific in-game action by clearing the slate first, then performing the action, then polling 'runtime-errors-get'. NOTE: the monotonic sequence counter is NOT reset, so a 'sinceSequence' poll from before the clear still behaves correctly (no old rows reappear). A no-op when runtime error capture is not enabled in this game."
---

# Runtime Errors / Clear

Clear the captured in-game runtime-error buffer (read by 'runtime-errors-get'). Useful for isolating errors to a specific in-game action by clearing the slate first, then performing the action, then polling 'runtime-errors-get'. NOTE: the monotonic sequence counter is NOT reset, so a 'sinceSequence' poll from before the clear still behaves correctly (no old rows reappear). A no-op when runtime error capture is not enabled in this game.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8427/api/tools/runtime-errors-clear \
  -H "Content-Type: application/json" \
  -d '{
  "nothing": "string_value"
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8427/api/tools/runtime-errors-clear -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8427/api/tools/runtime-errors-clear \
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

This tool does not return structured output.

