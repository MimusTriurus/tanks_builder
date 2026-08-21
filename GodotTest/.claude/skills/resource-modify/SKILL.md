---
name: resource-modify
description: |-
  Modify properties of a Godot Resource (.tres/.res asset) via ReflectorNet and save it back to disk. Identify the resource with 'resourceRef' (a res:// path is required — an instance id with no on-disk path cannot be saved). Two modification surfaces (supply at least one; both may be combined, applied jsonPatch first then pathPatches):
    1. 'pathPatches' — list of {path, value} entries routed through Reflector.TryModifyAt. Path syntax: 'fieldName', 'nested/field', 'arrayField/[i]', 'dictField/[key]'.
    2. 'jsonPatch' — a JSON Merge Patch (RFC 7396) string routed through Reflector.TryPatch.
  On success the resource is re-saved with ResourceSaver and the editor filesystem is updated (the .import sidecar is left untouched). Use 'resource-get-data' first to inspect the structure. Returns a log of what was changed and what was ignored.
---

# Resource / Modify

Modify properties of a Godot Resource (.tres/.res asset) via ReflectorNet and save it back to disk. Identify the resource with 'resourceRef' (a res:// path is required — an instance id with no on-disk path cannot be saved). Two modification surfaces (supply at least one; both may be combined, applied jsonPatch first then pathPatches):
  1. 'pathPatches' — list of {path, value} entries routed through Reflector.TryModifyAt. Path syntax: 'fieldName', 'nested/field', 'arrayField/[i]', 'dictField/[key]'.
  2. 'jsonPatch' — a JSON Merge Patch (RFC 7396) string routed through Reflector.TryPatch.
On success the resource is re-saved with ResourceSaver and the editor filesystem is updated (the .import sidecar is left untouched). Use 'resource-get-data' first to inspect the structure. Returns a log of what was changed and what was ignored.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8427/api/tools/resource-modify \
  -H "Content-Type: application/json" \
  -d '{
  "resourceRef": "string_value",
  "pathPatches": "string_value",
  "jsonPatch": "string_value"
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8427/api/tools/resource-modify -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8427/api/tools/resource-modify \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "resourceRef": "string_value",
  "pathPatches": "string_value",
  "jsonPatch": "string_value"
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `resourceRef` | `any` | Yes | Reference to the resource to modify (res:// path required; an unsaved instance cannot be persisted). |
| `pathPatches` | `any` | No | Optional list of path-scoped patches routed through Reflector.TryModifyAt. Each entry is a {path, value}. Path syntax: 'fieldName', 'nested/field', 'arrayField/[i]', 'dictField/[key]'. |
| `jsonPatch` | `string` | No | Optional JSON Merge Patch (RFC 7396) applied via Reflector.TryPatch. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "resourceRef": {
      "$ref": "#/$defs/com.IvanMurzak.Godot.MCP.Data.ResourceRef",
      "description": "Reference to the resource to modify (res:// path required; an unsaved instance cannot be persisted)."
    },
    "pathPatches": {
      "$ref": "#/$defs/System.Collections.Generic.List(com.IvanMurzak.Godot.MCP.Data.NodePropertyPatch)",
      "description": "Optional list of path-scoped patches routed through Reflector.TryModifyAt. Each entry is a {path, value}. Path syntax: 'fieldName', 'nested/field', 'arrayField/[i]', 'dictField/[key]'."
    },
    "jsonPatch": {
      "type": "string",
      "description": "Optional JSON Merge Patch (RFC 7396) applied via Reflector.TryPatch."
    }
  },
  "$defs": {
    "com.IvanMurzak.Godot.MCP.Data.ResourceRef": {
      "type": "object",
      "properties": {
        "instanceId": {
          "type": "integer",
          "description": "Instance id of an already-loaded Resource (Godot GodotObject.GetInstanceId()). If '0', treated as unset. Priority: 2."
        },
        "resourcePath": {
          "type": "string",
          "description": "res:// path of the Resource, e.g. 'res://materials/wood.tres'. Priority: 1 (Recommended)."
        }
      },
      "required": [
        "instanceId"
      ],
      "description": "Reference to a Godot Resource (.tres/.res asset), located by res:// path or instance id."
    },
    "com.IvanMurzak.Godot.MCP.Data.NodePropertyPatch": {
      "type": "object",
      "properties": {
        "path": {
          "type": "string",
          "description": "Path to the member to modify. Syntax: 'fieldName', 'nested/field', 'arrayField/[i]', 'dictField/[key]'. Leading '#/' is stripped."
        },
        "value": {
          "$ref": "#/$defs/com.IvanMurzak.ReflectorNet.Model.SerializedMember",
          "description": "New value for the member, as a ReflectorNet SerializedMember."
        }
      },
      "description": "A path-scoped property patch: 'path' locates the member, 'value' carries the new value as a ReflectorNet SerializedMember. Path syntax: 'fieldName', 'nested/field', 'arrayField/[i]', 'dictField/[key]'."
    },
    "com.IvanMurzak.ReflectorNet.Model.SerializedMember": {
      "type": "object",
      "properties": {
        "typeName": {
          "type": "string",
          "description": "Full type name. Eg: 'System.String', 'System.Int32', 'UnityEngine.Vector3', etc."
        },
        "name": {
          "type": "string",
          "description": "Object name."
        },
        "value": {
          "description": "Value of the object, serialized as a non stringified JSON element. Can be null if the value is not set. Can be default value if the value is an empty object or array json."
        },
        "fields": {
          "type": "array",
          "items": {
            "$ref": "#/$defs/com.IvanMurzak.ReflectorNet.Model.SerializedMember",
            "description": "Nested field value."
          },
          "description": "Fields of the object, serialized as a list of 'SerializedMember'."
        },
        "props": {
          "type": "array",
          "items": {
            "$ref": "#/$defs/com.IvanMurzak.ReflectorNet.Model.SerializedMember",
            "description": "Nested property value."
          },
          "description": "Properties of the object, serialized as a list of 'SerializedMember'."
        }
      },
      "required": [
        "typeName"
      ],
      "additionalProperties": false
    },
    "com.IvanMurzak.ReflectorNet.Model.SerializedMemberList": {
      "type": "array",
      "items": {
        "$ref": "#/$defs/com.IvanMurzak.ReflectorNet.Model.SerializedMember"
      }
    },
    "System.Collections.Generic.List(com.IvanMurzak.Godot.MCP.Data.NodePropertyPatch)": {
      "type": "array",
      "items": {
        "$ref": "#/$defs/com.IvanMurzak.Godot.MCP.Data.NodePropertyPatch",
        "description": "A path-scoped property patch: 'path' locates the member, 'value' carries the new value as a ReflectorNet SerializedMember. Path syntax: 'fieldName', 'nested/field', 'arrayField/[i]', 'dictField/[key]'."
      }
    }
  },
  "required": [
    "resourceRef"
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
      "$ref": "#/$defs/com.IvanMurzak.ReflectorNet.Model.Logs"
    }
  },
  "$defs": {
    "com.IvanMurzak.ReflectorNet.Model.LogEntry": {
      "type": "object",
      "properties": {
        "Depth": {
          "type": "integer"
        },
        "Message": {
          "type": "string"
        },
        "Type": {
          "type": "string",
          "enum": [
            "Trace",
            "Debug",
            "Info",
            "Success",
            "Warning",
            "Error",
            "Critical"
          ]
        }
      },
      "required": [
        "Depth",
        "Type"
      ]
    },
    "com.IvanMurzak.ReflectorNet.Model.Logs": {
      "type": "array",
      "items": {
        "$ref": "#/$defs/com.IvanMurzak.ReflectorNet.Model.LogEntry"
      }
    }
  },
  "required": [
    "result"
  ]
}
```

