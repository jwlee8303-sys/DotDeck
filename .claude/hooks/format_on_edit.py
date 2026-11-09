"""PostToolUse(Write|Edit|MultiEdit): auto-format the edited file when a formatter is available.

Never fails the turn: missing tools are skipped silently.
  .py                      -> ruff format + ruff check --fix
  .ts/.tsx/.js/.jsx/.css/.scss/.html/.vue/.json  -> project-local prettier (node_modules/.bin)
  .cs                      -> csharpier (dotnet tool) if installed
"""
from __future__ import annotations

import os
import shutil
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _common import project_dir, read_input  # noqa: E402

WEB_EXT = {".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".css", ".scss", ".html", ".vue", ".json"}


def run(cmd: list[str], cwd: str) -> None:
    try:
        subprocess.run(cmd, cwd=cwd, capture_output=True, timeout=45)
    except Exception:
        pass


def find_up(start: str, rel: str, stop: str) -> str | None:
    cur = os.path.abspath(start)
    stop = os.path.abspath(stop)
    while True:
        cand = os.path.join(cur, rel)
        if os.path.exists(cand):
            return cand
        parent = os.path.dirname(cur)
        if cur == stop or parent == cur:
            return None
        cur = parent


def main() -> None:
    data = read_input()
    path = (data.get("tool_input") or {}).get("file_path", "")
    if not path or not os.path.isfile(path):
        sys.exit(0)
    root = project_dir(data)
    ext = os.path.splitext(path)[1].lower()
    fdir = os.path.dirname(path)

    if ext == ".py":
        ruff = shutil.which("ruff")
        if ruff:
            run([ruff, "format", path], fdir)
            run([ruff, "check", "--fix", "--quiet", path], fdir)
    elif ext in WEB_EXT:
        if os.path.basename(path).endswith("-lock.json"):
            sys.exit(0)
        for name in ("prettier.cmd", "prettier"):
            local = find_up(fdir, os.path.join("node_modules", ".bin", name), root)
            if local:
                run([local, "--write", "--log-level", "warn", path], fdir)
                break
    elif ext == ".cs":
        cs = shutil.which("csharpier")
        if cs:
            run([cs, "format", path], fdir)

    sys.exit(0)


if __name__ == "__main__":
    main()
