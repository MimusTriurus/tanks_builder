---
name: reflection-method-call
description: |-
  Call a C# method by reflection — including private methods. The Godot analog of Unity's 'reflection-method-call'. Requires a method schema obtained via 'reflection-method-find'. Supports static methods, instance methods (with optional target deserialization), and main-thread / off-thread execution.
  Inputs:
    - 'targetObject' (optional) — for instance methods; { typeName, value } where value is deserialized to typeName. Null for static methods (or to construct a fresh instance). A Node target is written as { "typeName": "Godot.Control", "value": { "instanceId": 123 } } (or { "path": "/root/Main/Player" }); a Resource target as { "resourcePath": "res://x.tres" }.
    - 'inputParameters' (optional) — list of { typeName, name, value }; names/types are enhanced against the resolved signature when omitted.
    - 'executeInMainThread' (default true) — keep true for Godot-API-touching methods; set false only for thread-safe pure logic.
  Godot.Variant parameters: A Godot.Variant is written either as a bare JSON scalar…
---

# Method C# / Call

Call a C# method by reflection — including private methods. The Godot analog of Unity's 'reflection-method-call'. Requires a method schema obtained via 'reflection-method-find'. Supports static methods, instance methods (with optional target deserialization), and main-thread / off-thread execution.
Inputs:
  - 'targetObject' (optional) — for instance methods; { typeName, value } where value is deserialized to typeName. Null for static methods (or to construct a fresh instance). A Node target is written as { "typeName": "Godot.Control", "value": { "instanceId": 123 } } (or { "path": "/root/Main/Player" }); a Resource target as { "resourcePath": "res://x.tres" }.
  - 'inputParameters' (optional) — list of { typeName, name, value }; names/types are enhanced against the resolved signature when omitted.
  - 'executeInMainThread' (default true) — keep true for Godot-API-touching methods; set false only for thread-safe pure logic.
Godot.Variant parameters: A Godot.Variant is written either as a bare JSON scalar (null => Nil, true/false => Bool, integral number => Int, fractional number => Float, string => String) or as {"variantType":"<Type>","value":<json>} where <Type> is a Godot Variant.Type name. Composite types take a flat array of numbers in Godot's own constructor order (or, for Vector2/Vector2I/Vector3/Vector3I/Vector4/Vector4I/Quaternion/Plane/Color, an object with the named components): Vector2/Vector2I [x,y]; Vector3/Vector3I [x,y,z]; Vector4/Vector4I and Quaternion [x,y,z,w]; Color [r,g,b(,a)]; Plane [x,y,z,d] (the normal's xyz, then D); Rect2/Rect2I [x,y,width,height]; Aabb [x,y,z,width,height,depth]; Transform2D [xx,xy,yx,yy,ox,oy]; Basis [xx,yx,zx,xy,yy,zy,xz,yz,zz]; Transform3D = Basis then [ox,oy,oz]; Projection = four Vector4 columns flattened (16). String/StringName/NodePath take a string. Note: a parameter DECLARED as a concrete type (e.g. 'Godot.Vector3') keeps its own member-wise shape ({"x":1,"y":2,"z":3}); only a 'Godot.Variant' parameter uses this envelope — and its named-component object form matches that member-wise shape, so the same value can be written either way.
An argument whose value cannot be read is an ERROR — the method is never invoked with a silently defaulted argument.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8427/api/tools/reflection-method-call \
  -H "Content-Type: application/json" \
  -d '{
  "filter": "string_value",
  "knownNamespace": false,
  "typeNameMatchLevel": 0,
  "methodNameMatchLevel": 0,
  "parametersMatchLevel": 0,
  "targetObject": "string_value",
  "inputParameters": "string_value",
  "executeInMainThread": false
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8427/api/tools/reflection-method-call -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8427/api/tools/reflection-method-call \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "filter": "string_value",
  "knownNamespace": false,
  "typeNameMatchLevel": 0,
  "methodNameMatchLevel": 0,
  "parametersMatchLevel": 0,
  "targetObject": "string_value",
  "inputParameters": "string_value",
  "executeInMainThread": false
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `filter` | `any` | Yes | Method filter: Namespace / TypeName / MethodName / InputParameters identifying the method. |
| `knownNamespace` | `boolean` | No | Set true if 'filter.Namespace' is a known full namespace name; otherwise false. |
| `typeNameMatchLevel` | `integer` | No | Minimal match level for 'filter.TypeName' (0 ignore, 1 contains-ic [default], 2 contains-cs, 3 starts-ic, 4 starts-cs, 5 equals-ic, 6 equals-cs). |
| `methodNameMatchLevel` | `integer` | No | Minimal match level for 'filter.MethodName' (0 ignore, 1 contains-ic [default], 2 contains-cs, 3 starts-ic, 4 starts-cs, 5 equals-ic, 6 equals-cs). |
| `parametersMatchLevel` | `integer` | No | Minimal match level for 'filter.InputParameters' (0 ignore, 1 count matches, 2 equals [default]). |
| `targetObject` | `any` | No | Target object for an instance method ({ type, value }). Null for a static method, or to construct a fresh instance of the declaring type. |
| `inputParameters` | `any` | No | Method input parameters, each { type, name, value }. |
| `executeInMainThread` | `boolean` | No | Run the call on the editor main thread. Keep true for Godot-API methods; false for thread-safe pure logic. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "filter": {
      "$ref": "#/$defs/com.IvanMurzak.ReflectorNet.Model.MethodRef",
      "description": "Method filter: Namespace / TypeName / MethodName / InputParameters identifying the method."
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
      "description": "Minimal match level for 'filter.InputParameters' (0 ignore, 1 count matches, 2 equals [default])."
    },
    "targetObject": {
      "$ref": "#/$defs/com.IvanMurzak.ReflectorNet.Model.SerializedMember",
      "description": "Target object for an instance method ({ type, value }). Null for a static method, or to construct a fresh instance of the declaring type."
    },
    "inputParameters": {
      "$ref": "#/$defs/com.IvanMurzak.ReflectorNet.Model.SerializedMemberList",
      "description": "Method input parameters, each { type, name, value }."
    },
    "executeInMainThread": {
      "type": "boolean",
      "description": "Run the call on the editor main thread. Keep true for Godot-API methods; false for thread-safe pure logic."
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
    },
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

