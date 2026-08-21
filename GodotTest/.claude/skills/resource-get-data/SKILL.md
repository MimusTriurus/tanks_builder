---
name: resource-get-data
description: "Get the serialized data of a Godot Resource (.tres/.res asset) — every serializable property — via ReflectorNet. Identify the resource with 'resourceRef' (a res:// path is preferred; an instance id of an already-loaded resource also works). Use 'resource-find' to locate the resource first. Returns a ReflectorNet SerializedMember describing the resource; a resource that cannot be resolved yields a structured error."
---

# Resource / Get Data

Get the serialized data of a Godot Resource (.tres/.res asset) — every serializable property — via ReflectorNet. Identify the resource with 'resourceRef' (a res:// path is preferred; an instance id of an already-loaded resource also works). Use 'resource-find' to locate the resource first. Returns a ReflectorNet SerializedMember describing the resource; a resource that cannot be resolved yields a structured error.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8427/api/tools/resource-get-data \
  -H "Content-Type: application/json" \
  -d '{
  "resourceRef": "string_value"
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8427/api/tools/resource-get-data -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8427/api/tools/resource-get-data \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "resourceRef": "string_value"
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `resourceRef` | `any` | Yes | Reference to the resource to read (res:// path preferred, else a loaded instance id). |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "resourceRef": {
      "$ref": "#/$defs/com.IvanMurzak.Godot.MCP.Data.ResourceRef",
      "description": "Reference to the resource to read (res:// path preferred, else a loaded instance id)."
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
      "$ref": "#/$defs/com.IvanMurzak.ReflectorNet.Model.SerializedMember"
    }
  },
  "$defs": {
    "com.IvanMurzak.ReflectorNet.Model.SerializedMemberList": {
      "type": "array",
      "items": {
        "$ref": "#/$defs/com.IvanMurzak.ReflectorNet.Model.SerializedMember"
      }
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
    }
  },
  "required": [
    "result"
  ]
}
```

