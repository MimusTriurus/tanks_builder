"""PostToolUse hook for Bash/PowerShell.

After any command that launched the Godot engine, report whether it rewrote
the two tracked files it likes to rewrite: ``run/main_scene`` in
``project.godot`` (damage) and the engine version in ``project.godot`` /
``TankSpriteTest.csproj`` (a fix, not damage). The rule itself is in
godot/CLAUDE.md, "Ловушка запуска"; this only saves the manual
``git status`` the rule used to demand after every run.

Wired from .claude/settings.json::

    "hooks": {"PostToolUse": [{"matcher": "Bash|PowerShell", "hooks": [
        {"type": "command",
         "command": "python \\"$CLAUDE_PROJECT_DIR/.claude/hooks/godot_status.py\\""}]}]}
"""
import json
import os
import re
import subprocess
import sys

WATCHED = ("godot/project.godot", "godot/TankSpriteTest.csproj")
GODOT = re.compile(r"godot[^\s\"']*\.exe", re.IGNORECASE)


def launched_godot(payload):
    command = (payload.get("tool_input") or {}).get("command") or ""
    return bool(GODOT.search(command))


def changed_files(root):
    out = subprocess.run(
        ["git", "-C", root, "status", "--short", "--", *WATCHED],
        capture_output=True, text=True, check=False)
    return out.stdout.strip()


def message(changed):
    return (
        "Godot rewrote tracked files during that run:\n" + changed + "\n"
        "A changed run/main_scene is damage: name the scene positionally and "
        "restore the field. A changed engine version is a fix, not damage: do "
        "not revert it blindly (godot/CLAUDE.md, Ловушка запуска).")


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
    changed = changed_files(root)
    if changed:
        emit(changed)
    return 0


if __name__ == "__main__":
    sys.exit(main())
