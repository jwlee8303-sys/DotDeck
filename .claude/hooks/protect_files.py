"""PreToolUse(Read|Write|Edit|MultiEdit): protect secrets, lock files and .git internals."""
from __future__ import annotations

import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _common import pre_tool_decision, read_input  # noqa: E402

SECRET = re.compile(
    r"(^|[\\/])(\.env(\.(?!example$|sample$|template$)[^\\/]+)?|id_(rsa|ed25519|ecdsa|dsa)[^\\/]*|.*\.(pfx|p12|pem|key|snk)"
    r"|secrets?\.(json|ya?ml|toml)|appsettings\.Production\.json)$",
    re.IGNORECASE,
)
LOCKFILE = re.compile(
    r"(^|[\\/])(package-lock\.json|pnpm-lock\.yaml|yarn\.lock|poetry\.lock|uv\.lock|packages\.lock\.json)$",
    re.IGNORECASE,
)
GIT_INTERNAL = re.compile(r"(^|[\\/])\.git[\\/]")


def main() -> None:
    data = read_input()
    tool = data.get("tool_name", "")
    path = (data.get("tool_input") or {}).get("file_path", "") or ""
    if not path:
        sys.exit(0)

    if SECRET.search(path):
        pre_tool_decision("deny", f"[protect] 비밀정보 파일은 {tool} 할 수 없습니다: {os.path.basename(path)}")

    if tool != "Read":
        if LOCKFILE.search(path):
            pre_tool_decision("deny", "[protect] lock 파일은 직접 편집하지 말고 패키지 매니저 명령을 사용하세요.")
        if GIT_INTERNAL.search(path):
            pre_tool_decision("deny", "[protect] .git 내부 파일은 직접 수정할 수 없습니다.")

    sys.exit(0)


if __name__ == "__main__":
    main()
