"""SessionStart: inject repo state (stack, version, branch, dirty files, last tag) as context."""
from __future__ import annotations

import glob
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _common import emit, git, project_dir, read_input  # noqa: E402


def read(path: str) -> str:
    try:
        with open(path, encoding="utf-8") as f:
            return f.read()
    except Exception:
        return ""


def detect(root: str) -> tuple[list[str], str | None, str | None]:
    stacks, version, source = [], None, None
    pkg = os.path.join(root, "package.json")
    if os.path.exists(pkg):
        stacks.append("web(node)")
        try:
            version = json.loads(read(pkg)).get("version")
            source = "package.json"
        except Exception:
            pass
    py = os.path.join(root, "pyproject.toml")
    if os.path.exists(py):
        stacks.append("python")
        m = re.search(r'^\s*version\s*=\s*"([^"]+)"', read(py), re.M)
        if m and not version:
            version, source = m.group(1), "pyproject.toml"
    cs = glob.glob(os.path.join(root, "*.sln")) + glob.glob(os.path.join(root, "*.slnx")) \
        + [p for depth in ("", "*", "*/*", "*/*/*") for p in glob.glob(os.path.join(root, depth, "*.csproj"))]
    cs = [p for p in cs if not any(s in p.replace("\\", "/").split("/") for s in ("bin", "obj", "node_modules"))]
    if cs:
        stacks.append("csharp(.NET)")
        props = os.path.join(root, "Directory.Build.props")
        for f in [props] + [c for c in cs if c.endswith(".csproj")]:
            m = re.search(r"<Version>([^<]+)</Version>", read(f))
            if m and not version:
                version, source = m.group(1).strip(), os.path.relpath(f, root)
                break
    vfile = os.path.join(root, "VERSION")
    if not version and os.path.exists(vfile):
        version, source = read(vfile).strip(), "VERSION"
    return stacks, version, source


def main() -> None:
    data = read_input()
    root = project_dir(data)
    stacks, version, source = detect(root)
    lines = [f"[세션 컨텍스트] 프로젝트: {os.path.basename(root)}"]
    lines.append(f"- 스택: {', '.join(stacks) if stacks else '미확인'}")
    lines.append(f"- 현재 버전: {version} ({source})" if version else "- 현재 버전: 버전 파일 없음")

    branch = git(["rev-parse", "--abbrev-ref", "HEAD"], root)
    if branch is not None:
        status = git(["status", "--porcelain"], root) or ""
        dirty = len([l for l in status.splitlines() if l.strip()])
        tag = git(["describe", "--tags", "--abbrev=0"], root) or "없음"
        lines.append(f"- Git 브랜치: {branch} / 미커밋 파일 {dirty}개 / 최근 태그 {tag}")
        if version and tag != "없음" and tag.lstrip("v") != version:
            lines.append(f"  ⚠ 버전 파일({version})과 최근 태그({tag})가 다릅니다.")
    else:
        lines.append("- Git 저장소 아님")
    if not os.path.exists(os.path.join(root, "CLAUDE.md")):
        lines.append("- 프로젝트 CLAUDE.md 없음 → 필요하면 project-init 스킬로 생성 제안")

    emit({"hookSpecificOutput": {"hookEventName": "SessionStart", "additionalContext": "\n".join(lines)}})


if __name__ == "__main__":
    main()
