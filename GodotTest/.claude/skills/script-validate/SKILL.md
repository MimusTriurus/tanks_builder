---
name: script-validate
description: |-
  Validate GDScript ('.gd') files and return STRUCTURED parse/compile diagnostics — the on-demand 'is the project error-free?' query. Closes the gap where the agent gets NO feedback on GDScript parse errors and assumes the game runs fine.
  Inputs:
    - 'scriptPath' (optional): a single res:// '.gd' path to validate. Omit to scan every '.gd' under res:// (capped at FullScanFileCap).
  Result (ScriptDiagnosticsResult): 'ok' (true when no errors), 'diagnostics' [{ path, line, message, severity }], counts, a 'truncated' flag (true when a full scan hit the file cap, so 'ok' is NOT a whole-project all-clear), and a 'fidelity' note:
    - Godot 4.5+: 'Precise' — exact line + message captured via the engine Logger hook.
    - Godot < 4.5: 'Coarse' — per-file pass/fail with the engine error code (no line/message text).
  C# ('.cs') files are NOT validated here (Godot has no in-editor C# compiler to reach cheaply); their compile errors surface through the project build. Pair with 'console-get-logs', which on Godot 4.5+ now also…
---

# Script / Validate

Validate GDScript ('.gd') files and return STRUCTURED parse/compile diagnostics — the on-demand 'is the project error-free?' query. Closes the gap where the agent gets NO feedback on GDScript parse errors and assumes the game runs fine.
Inputs:
  - 'scriptPath' (optional): a single res:// '.gd' path to validate. Omit to scan every '.gd' under res:// (capped at FullScanFileCap).
Result (ScriptDiagnosticsResult): 'ok' (true when no errors), 'diagnostics' [{ path, line, message, severity }], counts, a 'truncated' flag (true when a full scan hit the file cap, so 'ok' is NOT a whole-project all-clear), and a 'fidelity' note:
  - Godot 4.5+: 'Precise' — exact line + message captured via the engine Logger hook.
  - Godot < 4.5: 'Coarse' — per-file pass/fail with the engine error code (no line/message text).
C# ('.cs') files are NOT validated here (Godot has no in-editor C# compiler to reach cheaply); their compile errors surface through the project build. Pair with 'console-get-logs', which on Godot 4.5+ now also captures engine GDScript parse errors passively.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8427/api/tools/script-validate \
  -H "Content-Type: application/json" \
  -d '{
  "scriptPath": "string_value"
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8427/api/tools/script-validate -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8427/api/tools/script-validate \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "scriptPath": "string_value"
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `scriptPath` | `string` | No | Optional single res:// '.gd' script path to validate, e.g. 'res://scripts/player.gd'. Omit (null/empty) to scan every '.gd' file under res://. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "scriptPath": {
      "type": "string",
      "description": "Optional single res:// '.gd' script path to validate, e.g. 'res://scripts/player.gd'. Omit (null/empty) to scan every '.gd' file under res://."
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
      "$ref": "#/$defs/com.IvanMurzak.Godot.MCP.Data.ScriptDiagnosticsResult",
      "description": "Result of 'script-validate': an Ok verdict, the scanned script paths, captured diagnostics, and a fidelity note about how precise the diagnostics are on the live Godot version."
    }
  },
  "$defs": {
    "System.Collections.Generic.List(System.String)": {
      "type": "array",
      "items": {
        "type": "string"
      }
    },
    "System.Collections.Generic.List(com.IvanMurzak.Godot.MCP.Data.ScriptDiagnostic)": {
      "type": "array",
      "items": {
        "$ref": "#/$defs/com.IvanMurzak.Godot.MCP.Data.ScriptDiagnostic",
        "description": "A single script diagnostic (parse/compile error or warning): the res:// path, 1-based line (-1 when unknown), message, and severity."
      }
    },
    "com.IvanMurzak.Godot.MCP.Data.ScriptDiagnostic": {
      "type": "object",
      "properties": {
        "path": {
          "type": "string",
          "description": "res:// path of the script the diagnostic belongs to, e.g. 'res://scripts/player.gd'."
        },
        "line": {
          "type": "integer",
          "description": "1-based source line of the diagnostic, or -1 when the live Godot version cannot report a line (Godot < 4.5 falls back to a per-file error code with no line)."
        },
        "message": {
          "type": "string",
          "description": "Human-readable diagnostic text (Godot 4.5+), or the engine Error code name (e.g. 'ParseError') on older versions where the message text is not reachable."
        },
        "severity": {
          "type": "string",
          "enum": [
            "Error",
            "Warning"
          ],
          "description": "Diagnostic severity: Error or Warning."
        }
      },
      "required": [
        "line",
        "severity"
      ],
      "description": "A single script diagnostic (parse/compile error or warning): the res:// path, 1-based line (-1 when unknown), message, and severity."
    },
    "com.IvanMurzak.Godot.MCP.Data.ScriptDiagnosticsResult": {
      "type": "object",
      "properties": {
        "ok": {
          "type": "boolean",
          "description": "True when no error-severity diagnostics were found across every scanned script."
        },
        "scannedCount": {
          "type": "integer",
          "description": "Number of script files that were validated."
        },
        "errorCount": {
          "type": "integer",
          "description": "Number of error-severity diagnostics in 'diagnostics'."
        },
        "warningCount": {
          "type": "integer",
          "description": "Number of warning-severity diagnostics in 'diagnostics'."
        },
        "scannedPaths": {
          "$ref": "#/$defs/System.Collections.Generic.List(System.String)",
          "description": "res:// paths of the script files that were validated."
        },
        "diagnostics": {
          "$ref": "#/$defs/System.Collections.Generic.List(com.IvanMurzak.Godot.MCP.Data.ScriptDiagnostic)",
          "description": "The captured diagnostics (errors first, then warnings). Empty when 'ok' is true."
        },
        "fidelity": {
          "type": "string",
          "enum": [
            "Coarse",
            "Precise"
          ],
          "description": "How precise the diagnostics are on the live Godot version: 'Precise' (4.5+: line + message captured) or 'Coarse' (Godot < 4.5: per-file pass/fail with the engine error code, no line)."
        },
        "truncated": {
          "type": "boolean",
          "description": "True when a full-project scan hit the file cap and only the first N '.gd' files were validated — so 'ok' is NOT a guaranteed all-clear for the whole project. Validate a specific 'scriptPath' (or sub-tree) to cover the rest. Always false for a single-path validation."
        },
        "note": {
          "type": "string",
          "description": "Human-readable summary, e.g. 'No script errors found (12 scanned).' or '2 errors in 1 script.'. Includes a fidelity caveat on older Godot versions and a truncation hint when the full-scan cap was hit."
        }
      },
      "required": [
        "ok",
        "scannedCount",
        "errorCount",
        "warningCount",
        "fidelity",
        "truncated"
      ],
      "description": "Result of 'script-validate': an Ok verdict, the scanned script paths, captured diagnostics, and a fidelity note about how precise the diagnostics are on the live Godot version."
    }
  },
  "required": [
    "result"
  ]
}
```

