# 인수인계 — DotDeck

- 작성: 2026-11-09 (Claude Code)
- 버전: `1.1.0`(`Directory.Build.props`, 미변경) · 브랜치 `feat/ai-cli` @ `5931c70`
- 한 줄 요약: **Phase 5a(AI CLI 연동 — 범위 지정 Claude 비대화형 세션, 가드 훅, 스냅샷·변경 검토, AI 작업 패널) 구현과 단계별 Codex 검증·커밋을 마쳤다.** 다음은 사용자 5a 확인 → 5b(ConPTY + xterm.js 대화형 터미널).

## 1. 프로젝트 개요
- 개발 환경 설정 파일(~/.claude, ~/.codex, 환경변수 등)과 문서를 한 창에서 찾고·보고·수정하는 Windows 데스크톱 에디터. VS 2022 다크 테마 도킹 UI.
- 스택: .NET 10, WPF + CommunityToolkit.Mvvm, AvalonDock, AvalonEdit, Markdig, DiffPlex, Tomlyn, xunit.v3.
- 핵심 문서: `CLAUDE.md`(규칙), `docs/SPEC.md`(사양, AI CLI는 11절), `docs/DESIGN.md`("AI CLI 연동 설계" 절에 5a 단계별 구현 메모), `CHANGELOG.md`.

## 2. 실행 방법
- 빌드: `dotnet build DotDeck.slnx` (경고는 오류)
- 테스트: `dotnet test --solution DotDeck.slnx`
- 실행(반드시 샌드박스 홈): `dotnet run --project src/DotDeck.App -- --home <임시 폴더>` 또는 환경변수 `DOTDECK_HOME`
- 5a 수동 확인 준비: 샌드박스 홈의 `.claude`에 Claude Code 로그인이 필요하다(샌드박스에서만 `CLAUDE_CONFIG_DIR`을 넘긴다). 실제 `~/.claude`는 쓰지 않는다. **(확인 필요: 샌드박스 로그인 후 실제 `claude -p` 왕복은 아직 수동 확인하지 않음)**
- 확인 순서 제안: 임시 폴더 프로젝트 추가 → 도구 > Claude 세션 시작…(Ctrl+Alt+C) → 프롬프트 "README.md를 만들어 줘" → AI 작업 패널에서 출력·"가드 적용됨" 확인 → 범위 밖 경로 쓰기 요청이 거부되는지 확인 → 종료 후 변경 검토에서 diff·되돌리기 확인.

## 3. 현재 상태
| 항목 | 상태 |
|---|---|
| 진행 단계 | Phase 5a 완료(5a-0 ~ 5a-11), 사용자 확인 대기 |
| 빌드 | `dotnet build DotDeck.slnx` 경고 0·오류 0 (2026-11-09) |
| 테스트 | 합계 1217 / 실패 0 / 건너뜀 2(심볼릭 링크 생성 권한, 기존) |
| Codex 검증 | 5a-1 ~ 5a-10 단계마다 `codex-verifier`로 검증·재검증 후 커밋. `.git/codex-reviewed` 표식은 쓰지 않음 |
| 원격 동기화 | upstream 없음(push 안 함). push·PR은 사용자 확인 후 |
| 미커밋 변경 | `CHANGELOG.md`, `docs/HANDOFF.md`(5a-11 문서 커밋 예정). `.claude/`는 사용자 로컬 설정 — 커밋·수정 금지 |

### 5a 커밋 (v1.1.0 이후)
| 커밋 | 내용 |
|---|---|
| 1e8b413 | docs(spec): SPEC 11절과 Phase 5 계획 |
| ab51df1 | docs(design): AI CLI 연동 설계와 사용자 결정 |
| a0c7b3d | 5a-1 세션 범위 모델·범위 선택 규칙 |
| e78faf5 | 5a-2 경로 정규화·범위 검사기(정션·8.3·ADS·UNC) |
| f06ad12 | 5a-3 PreToolUse 가드 판정·셸 명령 분석기 |
| c07bade | 5a-4 `DotDeck.exe --dotdeck-guard` 가드 모드·scope.json 갱신 |
| 6607d9f | 5a-5 CLI 명령줄 빌더·CLI 탐색·세션 설정 파일 |
| 6d91724 | 5a-6 세션 프리셋 저장소(`sessions.json`)·컨텍스트 문서 |
| 3a67056 | 5a-7 Job Object 실행기·AI 세션 관리자 |
| a79ae33 | 5a-8 세션 스냅샷·변경 요약·되돌리기 |
| 5c51fb6 | 5a-9 세션 시작 대화상자·진입점 |
| 5931c70 | 5a-10 AI 작업 패널·변경 검토 대화상자 |

### 진행 중
- 5a-11: `CHANGELOG.md` `[Unreleased]`에 5a 항목 추가, 이 문서 갱신 → 커밋 `docs: 5a 변경 이력과 인수인계 갱신` 예정.

## 4. 다음 할 일
1. **사용자 5a 확인** — 위 2절 순서로 샌드박스에서 실제 세션 왕복. 완료 기준: 범위 안 쓰기 성공, 범위 밖·비밀 파일 차단, 변경 검토 되돌리기 동작(SPEC 5a 완료 기준).
2. **5b 대화형 터미널** — ConPTY / xterm.js 번들(LICENSE 포함, 버전 DESIGN에 고정) / 터미널 패널 / 대화형 세션 탭·범위 칩 / PowerShell 터미널 / 변경 검토 연결. 완료 기준: 단계마다 빌드·테스트·Codex 검증·커밋, 끝에 스크린샷 또는 실행 방법 보고. 관련: `src/DotDeck.App/Infrastructure/Windows/JobProcessLauncher.cs`(PSEUDOCONSOLE 속성 추가 지점), `CliCommandBuilder`의 Interactive 분기, `SessionLaunchDialog.xaml`(비활성 라디오).
3. **5c** — MCP 서버 / Named Pipe 호스트·브리지 / 컨텍스트 고도화 / Codex 지원 / 4개 시나리오 수동 확인.
4. 릴리스는 `release` 스킬로만(버전 파일 직접 수정 금지). 5a만 먼저 낼지는 사용자 결정.

## 5. 결정 사항과 이유
| 결정 | 이유 | 기각한 대안 |
|---|---|---|
| CLI는 셸 없이 직접 실행, 프롬프트는 표준 입력 | 인용·주입 위험 제거, 32K 명령줄 제한 회피 | `cmd /c`, 명령줄 인수 |
| 범위 강제는 Claude PreToolUse 훅(exec-form `DotDeck.exe --dotdeck-guard`) + `permissions.deny` | 공식 확장점, 실측 콜드 스타트 p50 68ms | 파일 감시 후 사후 차단 |
| 범위 안 비밀 파일은 모든 모드에서 차단 | 사용자 결정 12 | 확인 후 허용 |
| SessionStart heartbeat 없으면 세션 실패 처리 | `-p`에서 설정 오류가 조용히 무시됨 | 무시 |
| 스냅샷은 세션 폴더 `backups\`(DPAPI)·사용자 백업과 분리 | 긴 세션이 사용자 백업을 밀어내지 않게 | 주 BackupStore 재사용 |
| 불완전 색인이면 삭제 추정 금지, 새 파일은 "기존 파일일 수 있음"으로 휴지통 되돌리기 차단 | 오판 시 데이터 손실 | 그대로 Created 처리 |
| 되돌리기는 루트·reparse 재확인 + 요약 시점 해시 조건부 복원 | 요약 이후 정션 교체·재편집 보호 | 무조건 복원 |
| 패널은 stream-json 요약만, 성공한 도구 결과·쓴 내용은 숨김, 자르기 전 마스킹 | 파일 본문·비밀 노출 방지 | 원문 스트리밍 |
| 5a 빠른 실행도 대화상자를 연다 | 비대화형은 프롬프트가 필수 | 즉시 실행 |

## 6. 주의·함정
- **Git Bash heredoc은 `\`를 반으로 줄인다** — C# 소스나 Windows 경로가 든 문서를 heredoc/`sed`로 쓰면 깨진다(`\b`가 백스페이스로 들어간 사례). 스크래치패드에 Write로 node 스크립트를 쓰고 실행하는 방식을 쓴다.
- 사용자 전역 PreToolUse 훅이 bash 명령 안의 `.env` 문자열을 막는다. 테스트 데이터에 필요하면 파일로 쓰고 읽는다.
- 커밋 분리 시 `git checkout-index -a --prefix=<스크래치>/stageNN/`로 스테이징 상태만 따로 빌드·테스트했다(stash pop 충돌 경험 있음).
- xunit.v3: `Task.Delay` 등에 `TestContext.Current.CancellationToken` 필요(xUnit1051). App.Tests는 LibraryImport 때문에 `AllowUnsafeBlocks`.
- `AiSessionManager` 이벤트는 백그라운드 스레드에서 온다. VM은 `Post`로 UI 스레드에 넘긴다. 패널은 `AttachOutput`으로 버퍼+구독을 원자적으로 받는다.
- Debug 빌드는 실행 중인 DotDeck이 DLL을 잠그면 실패한다. 사용자의 앱을 강제 종료하지 않는다.

## 7. 알려진 문제·미해결 질문
- 보류(Codex 지적, 영향 낮음): 세션 백업 내부 `.lock` 10초 대기(세션 저장소의 다른 쓰기 주체 없음), 요약 시 2MB 초과 파일의 비강제 되돌리기는 시간 비교만, 범위 안 정션 아래 열린 문서의 읽기 사본 캡처(되돌리기는 차단), 5a-3 session_id 비교, 디렉터리별 대소문자 구분 폴더(5a-2).
- 기각: 세션 중 범위 루트 자체를 정션으로 바꾸는 경우(루트는 시작 시 해석된 값).
- 알려진 한계: 하드링크, 검사↔쓰기 사이 링크 교체(TOCTOU) — 변경 요약으로 사후 탐지.
- 자동 변경 검토의 오류 경로·링크 대상 비밀 검사는 ShellViewModel 수준이라 단위 테스트 없음(Core 리졸버는 기존 테스트로 검증).
- 실제 Claude CLI와의 수동 왕복 미실행(위 2절). 완료 여부는 사용자 확인 필요.
- 세션 폴더 정리(7일·최근 20세션) 정책은 DESIGN에 있으나 정리 작업 구현은 아직 없음 **(확인 필요: 5b/5c 범위에 넣을지)**.

## 8. 참고
- TODO/FIXME: 없음(코드 주석의 "Grep TODO"는 예시 문자열).
- 스태시: 없음.
- 반드시 지킬 규칙(요약): 실제 `~/.claude`·`~/.codex`·환경변수·hosts 수정 금지(`--home` 사용), 권한 우회 옵션 사용 금지, 비밀 파일 내용 조사 금지(테스트는 가짜 값), 새 NuGet은 승인 후(CPM), main 직접 커밋·push·PR·태그는 사용자 확인 후, 커밋 전 `git diff --staged` 비밀 점검, `.claude/` 커밋 금지.
- 이전 작업(Codex 편집기·v1.1.0)의 상세 기록은 git 이력(`git log v1.0.0..v1.1.0`)과 `CHANGELOG.md`를 본다.

## 9. 인계자 메모
- 각 sub-phase 보고 형식: 구현 내용 / 변경 파일 / 테스트 결과 / Codex 검증(반영·기각·보류) / 알려진 제한 / 다음 단계 확인 질문. 5b 끝에는 터미널 패널 스크린샷 또는 실행 방법을 제공한다.
- 승인 조건(완료): 샌드박스에서만 CLAUDE_CONFIG_DIR·CODEX_HOME 전달 테스트, 훅 병합 실측, `--system-prompt-snapshot` 확인, 비밀 파일 전 모드 차단, 가드 콜드 스타트 200ms 이하, WinExe stdio 동작.

## 10. 세션 재개 프롬프트
```text
D:\project\DotDeck 에서 작업을 이어받는다. 먼저 docs/HANDOFF.md와 CLAUDE.md, docs/SPEC.md 11절, docs/DESIGN.md "AI CLI 연동 설계"를 읽어라.
브랜치는 feat/ai-cli, Phase 5a는 커밋까지 끝났다. `git status --short`와 `git log --oneline -15`로 상태를 확인하고,
`dotnet build DotDeck.slnx`와 `dotnet test --solution DotDeck.slnx`가 통과하는지 먼저 확인하라.
HANDOFF "다음 할 일" 1번(사용자 5a 확인 결과 반영)부터 시작하고, 5b로 넘어가기 전에 단계 계획을 보여 주고 확인받아라.
실제 ~/.claude·~/.codex를 수정하지 말고 --home 샌드박스만 쓴다. push·PR은 사용자 확인 후에만 한다.
```

## 11. 문서 이력
- 2026-11-08: Codex 전용 편집기(2a~2b, 보안 재설계 ②~⑤, 리뷰 반영 A~C) 진행 기록 — v1.1.0까지 반영, 상세는 git 이력.
- 2026-11-09: Phase 5a(AI CLI 연동) 완료 상태로 전면 갱신, 이전 기록은 한 줄로 축약.
