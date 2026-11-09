"""PreToolUse(Bash): block destructive commands, ask before risky git operations."""
from __future__ import annotations

import re
import sys
import os

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _common import git, pre_tool_decision, project_dir, read_input  # noqa: E402

DENY = [
    (r"\brm\s+-[a-zA-Z]*r[a-zA-Z]*\s+(-[a-zA-Z]+\s+)*(/|~|\$HOME|/\*|[A-Za-z]:[\\/]?)(\s|$)",
     "루트/홈 디렉터리 재귀 삭제는 차단됩니다."),
    (r"\bgit\s+push\b.*(\s--force(?!-with-lease)\b|\s-f\b)", "force push는 금지되어 있습니다."),
    (r"\b(format|diskpart)\b\s+[A-Za-z]:", "디스크 포맷 명령은 차단됩니다."),
    (r"Remove-Item\b.*-Recurse.*\s([A-Za-z]:\\?|~|\$HOME)(\s|$)", "드라이브/홈 재귀 삭제는 차단됩니다."),
    (r"\b(cat|type|less|more|head|tail|Get-Content)\s+[^|;&]*\.env(\s|$|\.(?!example|sample|template))",
     ".env 파일 내용 출력은 차단됩니다 (.env.example은 허용)."),
    (r"\bgit\s+add\b[^|;&]*\.env(\s|$)", ".env 파일은 커밋할 수 없습니다."),
]

ASK = [
    (r"\bgit\s+reset\s+--hard\b", "git reset --hard는 작업 내용을 되돌릴 수 없게 지웁니다."),
    (r"\bgit\s+clean\s+-[a-zA-Z]*f", "git clean은 추적되지 않는 파일을 삭제합니다."),
    (r"\bgit\s+push\b.*--force-with-lease", "force-with-lease push입니다."),
    (r"\bgit\s+branch\s+-D\b", "브랜치 강제 삭제입니다."),
    (r"\bgit\s+checkout\s+--\s+\.", "작업 트리 변경을 모두 버립니다."),
    (r"\bgit\s+restore\s+(--worktree\s+)?\.(\s|$)", "작업 트리 변경을 모두 버립니다."),
    (r"--dangerously-bypass-approvals-and-sandbox|danger-full-access",
     "Codex를 샌드박스 없이 실행하려 합니다."),
]


def main() -> None:
    data = read_input()
    cmd = (data.get("tool_input") or {}).get("command", "")
    if not cmd:
        sys.exit(0)

    for pattern, reason in DENY:
        if re.search(pattern, cmd, re.IGNORECASE):
            pre_tool_decision("deny", f"[guard] {reason}")

    for pattern, reason in ASK:
        if re.search(pattern, cmd, re.IGNORECASE):
            pre_tool_decision("ask", f"[guard] {reason} 실행할까요?")

    # Commits directly on main/master need explicit approval.
    if re.search(r"\bgit\s+commit\b", cmd):
        branch = git(["rev-parse", "--abbrev-ref", "HEAD"], project_dir(data))
        if branch in ("main", "master"):
            pre_tool_decision("ask", f"[guard] '{branch}' 브랜치에 직접 커밋합니다. 진행할까요?")

    sys.exit(0)


if __name__ == "__main__":
    main()
