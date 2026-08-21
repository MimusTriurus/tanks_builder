---
name: ping
description: Lightweight readiness probe for the Godot-MCP connection. Returns the input 'message' echoed back, or 'pong' when omitted. Useful for verifying SignalR connectivity end-to-end after the editor plugin connects to the MCP server.
---

# Ping

Lightweight readiness probe for the Godot-MCP connection. Returns the input 'message' echoed back, or 'pong' when omitted. Useful for verifying SignalR connectivity end-to-end after the editor plugin connects to the MCP server.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8427/api/system-tools/ping \
  -H "Content-Type: application/json" \
  -d '{
  "message": "string_value"
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8427/api/system-tools/ping -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8427/api/system-tools/ping \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "message": "string_value"
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `message` | `string` | No | Optional message to echo back. When null or empty, the tool returns 'pong'. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "message": {
      "type": "string",
      "description": "Optional message to echo back. When null or empty, the tool returns 'pong'."
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

