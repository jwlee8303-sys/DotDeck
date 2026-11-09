"""Shared helpers for Claude Code hooks (Windows/macOS/Linux, Python 3.9+)."""
from __future__ import annotations

import json
import os
import subprocess
import sys
from typing import Any

# Windows consoles default to cp949; force UTF-8 so Korean messages survive.
for _stream in (sys.stdin, sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8")  # type: ignore[attr-defined]
    except Exception:
        pass


def read_input() -> dict[str, Any]:
    try:
        return json.load(sys.stdin)
    except Exception:
        return {}


def emit(obj: dict[str, Any]) -> None:
    sys.stdout.write(json.dumps(obj, ensure_ascii=False))
    sys.stdout.flush()


def pre_tool_decision(decision: str, reason: str) -> None:
    """decision: 'allow' | 'deny' | 'ask'"""
    emit({
        "hookSpecificOutput": {
            "hookEventName": "PreToolUse",
            "permissionDecision": decision,
            "permissionDecisionReason": reason,
        }
    })
    sys.exit(0)


def project_dir(data: dict[str, Any]) -> str:
    return os.environ.get("CLAUDE_PROJECT_DIR") or data.get("cwd") or os.getcwd()


def git(args: list[str], cwd: str, timeout: int = 10) -> str | None:
    try:
        r = subprocess.run(
            ["git", *args], cwd=cwd, capture_output=True, text=True,
            encoding="utf-8", errors="replace", timeout=timeout,
        )
        return r.stdout.strip() if r.returncode == 0 else None
    except Exception:
        return None
