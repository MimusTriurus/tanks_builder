---
name: console-clear-logs
description: "Clear the captured Godot-MCP log cache (read by 'console-get-logs'). Useful for isolating logs to a specific action by clearing the slate first. NOTE: Godot's C# API exposes no managed hook to clear the editor's own Output panel, so (unlike Unity's analog) this clears only the MCP-side cache, not the Godot editor console window."
---

# Console / Clear Logs

Clear the captured Godot-MCP log cache (read by 'console-get-logs'). Useful for isolating logs to a specific action by clearing the slate first. NOTE: Godot's C# API exposes no managed hook to clear the editor's own Output panel, so (unlike Unity's analog) this clears only the MCP-side cache, not the Godot editor console window.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8427/api/tools/console-clear-logs \
  -H "Content-Type: application/json" \
  -d '{
  "nothing": "string_value"
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8427/api/tools/console-clear-logs -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8427/api/tools/console-clear-logs \
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

