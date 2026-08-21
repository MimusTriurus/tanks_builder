---
name: godot-skill-generate
description: "Regenerate every `SKILL.md` from the project's currently-registered MCP tools into the selected AI agent's skills folder (or a project-relative override path). Writes the YAML `description:` from `[AiSkillDescription]` and the body from `[AiSkillBody]`."
---

# Skill (Tool) / Generate All

Generate all skills from the tools registered in the Godot project. Writes a SKILL.md per registered MCP tool into the selected AI agent's configured skills folder, or into 'path' when a project-relative override is supplied. Returns the resolved destination folder, the agent it was resolved from, and the resulting SKILL.md count.

Generate all skills from the tools currently registered in the Godot project.

## Inputs

- `path` (optional) — project-relative skills folder (e.g. `.claude/skills`). Absolute paths, `res://` paths, and `..` traversal segments are rejected. When null/empty, the folder configured for the editor's SELECTED AI agent is used — which is the normal case.

## Behavior

Creates the destination folder if missing, then drives the McpPlugin skill generator to emit a `SKILL.md` per registered MCP tool. The plugin's `SkillsPath` / `ProjectRootPath` are temporarily swapped to the target folder and restored in a `finally`, so the on-disk configuration is unchanged after the call returns. Returns the resolved destination, the agent it came from, and the resulting `SKILL.md` count.

Note that the addon ALSO auto-generates skills on plugin boot when the dock's "Auto-generate" toggle is on; this tool is the explicit, on-demand path.

## How to Call

### HTTP API (Direct Tool Execution)

Execute this tool directly via the MCP Plugin HTTP API:

```bash
curl -X POST http://localhost:8427/api/system-tools/godot-skill-generate \
  -H "Content-Type: application/json" \
  -d '{
  "path": "string_value"
}'
```

> For complex input (multi-line strings, code), save the JSON to a file and use `-d @args.json`.
>
> Or pipe via stdin:
> ```bash
> curl -X POST http://localhost:8427/api/system-tools/godot-skill-generate -H "Content-Type: application/json" -d @- <<'EOF'
> {"param": "value"}
> EOF
> ```

#### With Authorization (if required)

```bash
curl -X POST http://localhost:8427/api/system-tools/godot-skill-generate \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
  "path": "string_value"
}'
```

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `path` | `string` | No | Optional project-relative path to the skills folder, e.g. '.claude/skills'. Absolute paths, res:// paths, and '..' traversal segments are rejected. If null or empty, the selected AI agent's configured skills folder is used. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "path": {
      "type": "string",
      "description": "Optional project-relative path to the skills folder, e.g. '.claude/skills'. Absolute paths, res:// paths, and '..' traversal segments are rejected. If null or empty, the selected AI agent's configured skills folder is used."
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
      "$ref": "#/$defs/com.IvanMurzak.Godot.MCP.Data.SkillsGenerateInfo",
      "description": "Outcome of a skill-file generation: the absolute destination folder, the AI agent it was resolved from, and the number of SKILL.md files present afterwards."
    }
  },
  "$defs": {
    "com.IvanMurzak.Godot.MCP.Data.SkillsGenerateInfo": {
      "type": "object",
      "properties": {
        "skillsFolder": {
          "type": "string",
          "description": "Absolute path of the folder the SKILL.md files were written into."
        },
        "agentId": {
          "type": "string",
          "description": "Id of the AI agent whose configured skills folder was used (e.g. 'claude-code'), or null when an explicit 'path' override was supplied instead."
        },
        "skillCount": {
          "type": "integer",
          "description": "Number of SKILL.md files found under the destination folder after generation."
        },
        "status": {
          "type": "string",
          "description": "Short human-readable status note about the generation outcome."
        }
      },
      "required": [
        "skillCount"
      ],
      "description": "Outcome of a skill-file generation: the absolute destination folder, the AI agent it was resolved from, and the number of SKILL.md files present afterwards."
    }
  },
  "required": [
    "result"
  ]
}
```

