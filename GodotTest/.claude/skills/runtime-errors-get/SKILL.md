---
name: runtime-errors-get
description: |-
  Retrieve errors raised inside the RUNNING game — GDScript runtime errors, push_error/push_warning, shader errors (Godot 4.5+ engine hook), and C# unhandled / unobserved-Task exceptions (with full managed stack traces). This is the in-game counterpart to 'console-get-logs' / 'script-validate' (which are editor-side): it lets an agent driving a live game detect real gameplay/runtime bugs instead of assuming the game is healthy because the editor console is quiet.
  Poll loop: pass the 'highestSequence' from the previous call as 'sinceSequence' to get ONLY newer errors. Start with sinceSequence=0 (default) for everything captured so far.
  Inputs:
    - 'sinceSequence' (default 0): return only errors with a sequence number greater than this.
    - 'maxEntries' (default 100, min 1): cap the returned page (newest kept; 'truncated' flags a cap).
  Result (RuntimeErrorsResult): 'available' (false when the game did not enable runtime error capture — then an empty list proves nothing), 'ok' (no error-severity entries), the…
---

# Runtime Errors / Get

Retrieve errors raised inside the RUNNING game — GDScript runtime errors, push_error/push_warning, shader errors (Godot 4.5+ engine hook), and C# unhandled / unobserved-Task exceptions (with full managed stack traces). This is the in-game counterpart to 'console-get-logs' / 'script-validate' (which are editor-side): it lets an agent driving a live game detect real gameplay/runtime bugs instead of assuming the game is healthy because the editor console is quiet.
Poll loop: pass the 'highestSequence' from the previous call as 'sinceSequence' to get ONLY newer errors. Start with sinceSequence=0 (default) for everything captured so far.
Inputs:
  - 'sinceSequence' (default 0): return only errors with a sequence number greater than this.
  - 'maxEntries' (default 100, min 1): cap the returned page (newest kept; 'truncated' flags a cap).
Result (RuntimeErrorsResult): 'available' (false when the game did not enable runtime error capture — then an empty list proves nothing), 'ok' (no error-severity entries), the 'errors' [{ sequence, message, type, source, file, line, function, stackTrace, frames, timestamp }] oldest-first, counts, 'highestSequence' (poll with it next), and a 'truncated' flag. On Godot 4.5+ a GDScript runtime error also carries 'frames' (the deep multi-frame call stack, innermost-first, each { function, file, line }) and a formatted 'stackTrace'.
SECURITY: messages and stack traces are forwarded verbatim and may contain sensitive runtime data (absolute filesystem paths, machine/user names, or a secret that appeared in an exception). Capture is OFF by default and the developer must enable it via WithRuntimeErrorCapture(); enable it only on a trusted loopback + token connection.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8427/api/tools/runtime-errors-get \
  -H "Content-Type: application/json" \
  -d '{
  "sinceSequence": 0,
  "maxEntries": 0
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8427/api/tools/runtime-errors-get -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8427/api/tools/runtime-errors-get \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "sinceSequence": 0,
  "maxEntries": 0
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `sinceSequence` | `integer` | No | Return only errors with a sequence number greater than this. Pass the previous result's 'highestSequence' to poll only new errors. Default 0 (all captured). |
| `maxEntries` | `integer` | No | Maximum number of errors to return. Minimum 1, default 100. Newest kept when capped. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "sinceSequence": {
      "type": "integer",
      "description": "Return only errors with a sequence number greater than this. Pass the previous result's 'highestSequence' to poll only new errors. Default 0 (all captured)."
    },
    "maxEntries": {
      "type": "integer",
      "description": "Maximum number of errors to return. Minimum 1, default 100. Newest kept when capped."
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
      "$ref": "#/$defs/com.IvanMurzak.Godot.MCP.Data.RuntimeErrorsResult",
      "description": "Result of 'runtime-errors-get': whether capture is available, the captured runtime errors newer than the requested sequence, counts, and the highest sequence seen (poll with it next time)."
    }
  },
  "$defs": {
    "System.Collections.Generic.List(com.IvanMurzak.Godot.MCP.Data.RuntimeError)": {
      "type": "array",
      "items": {
        "$ref": "#/$defs/com.IvanMurzak.Godot.MCP.Data.RuntimeError",
        "description": "A single captured in-game runtime error: message, type, origin (file/line/function), source, an optional managed stack trace, a monotonic sequence number, and a UTC timestamp."
      }
    },
    "com.IvanMurzak.Godot.MCP.Data.RuntimeError": {
      "type": "object",
      "properties": {
        "sequence": {
          "type": "integer",
          "description": "Monotonic 1-based sequence number assigned when captured. Pass the largest you have seen as 'sinceSequence' to 'runtime-errors-get' to poll only NEWER errors."
        },
        "message": {
          "type": "string",
          "description": "The error message text."
        },
        "type": {
          "type": "string",
          "description": "The error type/severity: for engine errors one of Error / Warning / Script / Shader; for managed faults the CLR exception type name (e.g. 'System.NullReferenceException')."
        },
        "source": {
          "type": "string",
          "enum": [
            "Engine",
            "UnhandledException",
            "UnobservedTaskException"
          ],
          "description": "Where the error came from: Engine (Godot error stream) / UnhandledException / UnobservedTaskException (managed C# faults with a full stack trace)."
        },
        "file": {
          "type": "string",
          "description": "Origin file of an engine error (e.g. 'res://scripts/player.gd'); empty for managed faults (the origin is in the stack trace instead)."
        },
        "line": {
          "type": "integer",
          "description": "1-based origin line of an engine error, or -1 when unknown / for managed faults."
        },
        "function": {
          "type": "string",
          "description": "Origin function of an engine error; empty when unknown / for managed faults."
        },
        "stackTrace": {
          "type": "string",
          "description": "Stack trace string: for a C# fault (UnhandledException / UnobservedTaskException) the full managed stack; for a Godot 4.5+ engine GDScript error the formatted multi-frame backtrace (see 'frames'); null for engine errors with no tracked backtrace (Godot < 4.5 or release builds without call-stack tracking)."
        },
        "frames": {
          "$ref": "#/$defs/System.Collections.Generic.List(com.IvanMurzak.Godot.MCP.Data.RuntimeErrorFrame)",
          "description": "Structured deep backtrace for a Godot 4.5+ engine GDScript error: ordered stack frames (innermost-first), each with function / file / line. Null for managed C# faults (their stack is in 'stackTrace') and for engine errors with no tracked backtrace (Godot < 4.5 or release builds without call-stack tracking)."
        },
        "timestamp": {
          "type": "string",
          "format": "date-time",
          "description": "UTC timestamp when the error was captured."
        }
      },
      "required": [
        "sequence",
        "source",
        "line",
        "timestamp"
      ],
      "description": "A single captured in-game runtime error: message, type, origin (file/line/function), source, an optional managed stack trace, a monotonic sequence number, and a UTC timestamp."
    },
    "System.Collections.Generic.List(com.IvanMurzak.Godot.MCP.Data.RuntimeErrorFrame)": {
      "type": "array",
      "items": {
        "$ref": "#/$defs/com.IvanMurzak.Godot.MCP.Data.RuntimeErrorFrame",
        "description": "One frame of a captured script-language (e.g. GDScript) backtrace: function, file, line."
      }
    },
    "com.IvanMurzak.Godot.MCP.Data.RuntimeErrorFrame": {
      "type": "object",
      "properties": {
        "function": {
          "type": "string",
          "description": "The function called at this stack frame (e.g. '_process'); empty when the engine omitted it."
        },
        "file": {
          "type": "string",
          "description": "The source file of this stack frame's call site (e.g. 'res://scripts/player.gd'); empty when the engine omitted it."
        },
        "line": {
          "type": "integer",
          "description": "The 1-based line number of this stack frame's call site, or -1 when unknown."
        }
      },
      "required": [
        "line"
      ],
      "description": "One frame of a captured script-language (e.g. GDScript) backtrace: function, file, line."
    },
    "com.IvanMurzak.Godot.MCP.Data.RuntimeErrorsResult": {
      "type": "object",
      "properties": {
        "available": {
          "type": "boolean",
          "description": "True when in-game runtime error capture is wired up (the runtime was initialized with capture enabled). When false, the runtime was never started with capture, so an empty 'errors' list proves nothing — enable capture via GodotMcpRuntime's WithRuntimeErrorCapture()."
        },
        "ok": {
          "type": "boolean",
          "description": "True when no error-severity runtime errors are present in 'errors' (warnings do not flip this to false). Always true when capture is unavailable or the buffer is empty."
        },
        "count": {
          "type": "integer",
          "description": "Number of runtime errors returned in 'errors' (after the 'sinceSequence' filter and the 'maxEntries' cap)."
        },
        "errorCount": {
          "type": "integer",
          "description": "Number of error-severity entries in 'errors' (managed faults + engine Error/Script/Shader)."
        },
        "warningCount": {
          "type": "integer",
          "description": "Number of warning-severity entries in 'errors' (engine Warning)."
        },
        "highestSequence": {
          "type": "integer",
          "description": "The highest sequence number across ALL captured runtime errors (not just the returned page). Pass this as 'sinceSequence' on the next call to poll only newer errors. 0 when none have ever been captured."
        },
        "truncated": {
          "type": "boolean",
          "description": "True when more matching errors existed than the 'maxEntries' cap returned. The NEWEST are kept; call again (the buffer is bounded, so very old errors may already have been evicted)."
        },
        "errors": {
          "$ref": "#/$defs/System.Collections.Generic.List(com.IvanMurzak.Godot.MCP.Data.RuntimeError)",
          "description": "The captured runtime errors newer than 'sinceSequence', oldest-first within the page so they read in chronological order. Empty when 'ok' is true / capture is unavailable."
        },
        "note": {
          "type": "string",
          "description": "Human-readable summary, e.g. 'No new runtime errors (capture active).' or '2 runtime error(s) since sequence 5.' or 'Runtime error capture is not enabled in this game.'."
        }
      },
      "required": [
        "available",
        "ok",
        "count",
        "errorCount",
        "warningCount",
        "highestSequence",
        "truncated"
      ],
      "description": "Result of 'runtime-errors-get': whether capture is available, the captured runtime errors newer than the requested sequence, counts, and the highest sequence seen (poll with it next time)."
    }
  },
  "required": [
    "result"
  ]
}
```

