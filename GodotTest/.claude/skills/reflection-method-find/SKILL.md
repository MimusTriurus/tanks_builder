---
name: reflection-method-find
description: |-
  Find C# methods across every loaded assembly by name / declaring-type / parameters — including private methods. The Godot analog of Unity's 'reflection-method-find'. Returns serialized MethodData entries usable as schemas for 'reflection-method-call'.
  Match levels (apply to typeName / MethodName / Parameters):
    - typeNameMatchLevel / methodNameMatchLevel (default 1 = contains-ignoring-case): 0 ignore filter, 1 contains-ic, 2 contains-cs, 3 starts-with-ic, 4 starts-with-cs, 5 equals-ic, 6 equals-cs.
    - parametersMatchLevel (default 0 = ignore filter): 0 ignore, 1 count matches, 2 equals.
---

# Method C# / Find

Find C# methods across every loaded assembly by name / declaring-type / parameters — including private methods. The Godot analog of Unity's 'reflection-method-find'. Returns serialized MethodData entries usable as schemas for 'reflection-method-call'.
Match levels (apply to typeName / MethodName / Parameters):
  - typeNameMatchLevel / methodNameMatchLevel (default 1 = contains-ignoring-case): 0 ignore filter, 1 contains-ic, 2 contains-cs, 3 starts-with-ic, 4 starts-with-cs, 5 equals-ic, 6 equals-cs.
  - parametersMatchLevel (default 0 = ignore filter): 0 ignore, 1 count matches, 2 equals.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8427/api/tools/reflection-method-find \
  -H "Content-Type: application/json" \
  -d '{
  "filter": "string_value",
  "knownNamespace": false,
  "typeNameMatchLevel": 0,
  "methodNameMatchLevel": 0,
  "parametersMatchLevel": 0
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8427/api/tools/reflection-method-find -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8427/api/tools/reflection-method-find \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "filter": "string_value",
  "knownNamespace": false,
  "typeNameMatchLevel": 0,
  "methodNameMatchLevel": 0,
  "parametersMatchLevel": 0
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `filter` | `any` | Yes | Method filter: Namespace / TypeName / MethodName / InputParameters to match against. |
| `knownNamespace` | `boolean` | No | Set true if 'filter.Namespace' is a known full namespace name; otherwise false. |
| `typeNameMatchLevel` | `integer` | No | Minimal match level for 'filter.TypeName' (0 ignore, 1 contains-ic [default], 2 contains-cs, 3 starts-ic, 4 starts-cs, 5 equals-ic, 6 equals-cs). |
| `methodNameMatchLevel` | `integer` | No | Minimal match level for 'filter.MethodName' (0 ignore, 1 contains-ic [default], 2 contains-cs, 3 starts-ic, 4 starts-cs, 5 equals-ic, 6 equals-cs). |
| `parametersMatchLevel` | `integer` | No | Minimal match level for 'filter.InputParameters' (0 ignore [default], 1 count matches, 2 equals). |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "filter": {
      "$ref": "#/$defs/com.IvanMurzak.ReflectorNet.Model.MethodRef",
      "description": "Method filter: Namespace / TypeName / MethodName / InputParameters to match against."
    },
    "knownNamespace": {
      "type": "boolean",
      "description": "Set true if 'filter.Namespace' is a known full namespace name; otherwise false."
    },
    "typeNameMatchLevel": {
      "type": "integer",
      "description": "Minimal match level for 'filter.TypeName' (0 ignore, 1 contains-ic [default], 2 contains-cs, 3 starts-ic, 4 starts-cs, 5 equals-ic, 6 equals-cs)."
    },
    "methodNameMatchLevel": {
      "type": "integer",
      "description": "Minimal match level for 'filter.MethodName' (0 ignore, 1 contains-ic [default], 2 contains-cs, 3 starts-ic, 4 starts-cs, 5 equals-ic, 6 equals-cs)."
    },
    "parametersMatchLevel": {
      "type": "integer",
      "description": "Minimal match level for 'filter.InputParameters' (0 ignore [default], 1 count matches, 2 equals)."
    }
  },
  "$defs": {
    "System.Collections.Generic.List(com.IvanMurzak.ReflectorNet.Model.MethodRef-Parameter)": {
      "type": "array",
      "items": {
        "$ref": "#/$defs/com.IvanMurzak.ReflectorNet.Model.MethodRef-Parameter",
        "description": "Parameter of a method. Contains type and name of the parameter."
      }
    },
    "com.IvanMurzak.ReflectorNet.Model.MethodRef-Parameter": {
      "type": "object",
      "properties": {
        "typeName": {
          "type": "string",
          "description": "Type of the parameter including namespace. Sample: 'System.String', 'System.Int32', 'UnityEngine.GameObject', etc."
        },
        "name": {
          "type": "string",
          "description": "Name of the parameter. It may be empty if the name is unknown."
        }
      },
      "description": "Parameter of a method. Contains type and name of the parameter."
    },
    "com.IvanMurzak.ReflectorNet.Model.MethodRef": {
      "type": "object",
      "properties": {
        "namespace": {
          "type": "string",
          "description": "Namespace of the class. It may be empty if the class is in the global namespace or the namespace is unknown."
        },
        "typeName": {
          "type": "string",
          "description": "Class name, or substring a class name. It may be empty if the class is unknown."
        },
        "methodName": {
          "type": "string",
          "description": "Method name, or substring of the method name. It may be empty if the method is unknown."
        },
        "inputParameters": {
          "$ref": "#/$defs/System.Collections.Generic.List(com.IvanMurzak.ReflectorNet.Model.MethodRef-Parameter)",
          "description": "List of input parameters. Can be null if the method has no parameters or the parameters are unknown."
        }
      },
      "description": "Method reference. Used to find method in codebase of the project."
    }
  },
  "required": [
    "filter"
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

