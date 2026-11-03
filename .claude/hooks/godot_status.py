"""PostToolUse hook for Bash/PowerShell.

After any command that launched the Godot engine, report whether it rewrote
the lines it likes to rewrite: ``run/main_scene`` in ``project.godot``
(damage) and the engine version in ``project.godot`` (``config/features``) or
``TankSpriteTest.csproj`` (``Godot.NET.Sdk``) - a fix, not damage. The rule
itself is in godot/CLAUDE.md, "Launch trap".

Only those keys are reported: ``project.godot`` usually carries the user's own
uncommitted edit (``run/main_run_args``), which is theirs and not a finding.

Wired from .claude/settings.json through the ``py`` launcher - ``python`` on
this machine is the Microsoft Store stub, which prints "Python" and exits 49::

    "hooks": {"PostToolUse": [{"matcher": "Bash|PowerShell", "hooks": [
        {"type": "command",
         "command": "py -3 \\"$CLAUDE_PROJECT_DIR/.claude/hooks/godot_status.py\\""}]}]}
"""
import json
import os
import re
import subprocess
import sys

WATCHED = ("godot/project.godot", "godot/TankSpriteTest.csproj")
GODOT = re.compile(r"godot[^\s\"']*\.exe", re.IGNORECASE)
KEYS = re.compile(r"^[+-](run/main_scene|config/features|.*Godot\.NET\.Sdk)")


def launched_godot(payload):
    command = (payload.get("tool_input") or {}).get("command") or ""
    return bool(GODOT.search(command))


def changed_lines(root):
    out = subprocess.run(
        ["git", "-C", root, "diff", "-U0", "--", *WATCHED],
        capture_output=True, text=True, check=False)
    return "\n".join(
        line for line in out.stdout.splitlines()
        if KEYS.match(line) and not line.startswith(("+++", "---")))


def message(changed):
    return (
        "Godot rewrote tracked lines during that run:\n" + changed + "\n"
        "A changed run/main_scene is damage: name the scene positionally and "
        "restore that one line (never git checkout the file - it holds the "
        "user's edit). A changed engine version is a fix, not damage: do not "
        "revert it blindly (godot/CLAUDE.md, Launch trap).")


def emit(changed):
    print(json.dumps({"hookSpecificOutput": {
        "hookEventName": "PostToolUse",
        "additionalContext": message(changed)}}))  # ASCII-safe on cp1252 consoles


def main():
    try:
        payload = json.load(sys.stdin)
    except ValueError:
        return 0
    if not launched_godot(payload):
        return 0
    here = os.path.dirname(os.path.abspath(__file__))
    root = os.path.normpath(os.path.join(here, "..", ".."))
    changed = changed_lines(root)
    if changed:
        emit(changed)
    return 0


if __name__ == "__main__":
    sys.exit(main())
