# 인수인계 — DotDeck

- 작성: 2026-11-09 (Claude Code)
- 버전: `1.2.0` 릴리스(5a + 5b + 편집기 자동 줄 바꿈) · `feat/ai-cli` → PR #3으로 `main` 병합
- 한 줄 요약: **Phase 5a(비대화형 Claude 세션)와 5b(ConPTY + xterm.js 대화형 터미널, PowerShell 터미널, 대화형 Claude 세션·범위 칩) 구현과 단계별 Codex 검증·커밋을 마쳤다.** `feat/editor-word-wrap`(Alt+Z 자동 줄 바꿈)을 병합해 v1.2.0으로 릴리스했다. **5c는 별도 개발로 넘긴다 — "4-1. 5c 인수인계" 절부터 읽는다.**

## 1. 프로젝트 개요
- 개발 환경 설정 파일(~/.claude, ~/.codex, 환경변수 등)과 문서를 한 창에서 찾고·보고·수정하는 Windows 데스크톱 에디터. VS 2022 다크 테마 도킹 UI.
- 스택: .NET 10, WPF + CommunityToolkit.Mvvm, AvalonDock, AvalonEdit, Markdig, DiffPlex, Tomlyn, WebView2, xunit.v3. 터미널 프런트엔드는 xterm.js 6.0.0 + @xterm/addon-fit 0.11.0(MIT, 내장 리소스).
- 핵심 문서: `CLAUDE.md`(규칙), `docs/SPEC.md`(사양, AI CLI는 11절), `docs/DESIGN.md`("AI CLI 연동 설계" 절에 5a·5b 단계별 구현 메모), `CHANGELOG.md`, `THIRD-PARTY-NOTICES.txt`.

## 2. 실행 방법
- 빌드: `dotnet build DotDeck.slnx` (경고는 오류)
- 테스트: `dotnet test --solution DotDeck.slnx`
- 실행(반드시 샌드박스 홈): `dotnet run --project src/DotDeck.App -- --home <임시 폴더>` 또는 환경변수 `DOTDECK_HOME`
- Claude 세션 수동 확인 준비: 샌드박스 홈의 `.claude`에 Claude Code 로그인이 필요하다(샌드박스에서만 `CLAUDE_CONFIG_DIR`을 넘긴다). 실제 `~/.claude`는 쓰지 않는다.
- 5b 수동 확인 순서:
  1. 도구 > 새 PowerShell 터미널(Ctrl+Alt+T) → 하단에 터미널 탭. `echo 안녕하세요`, 한글 IME 입력, 창·패널 크기 조절 후 `$Host.UI.RawUI.WindowSize`, 설정 대화상자에서 테마 전환, 텍스트 선택 후 Ctrl+C 복사·Ctrl+V 붙여넣기, `exit` 후 탭 제목 "(종료됨)".
  2. 터미널 안에서 `claude /login`(샌드박스 `CLAUDE_CONFIG_DIR`이 전달됨)으로 샌드박스 로그인.
  3. 프로젝트 우클릭 > "이 프로젝트에서 Claude 시작" → 대화형 Claude TUI 탭. 툴바의 범위 칩·"가드 적용됨" 확인.
  4. 범위 안 파일 수정 요청 → 해당 파일을 열어 둔 탭이 자동으로 새로 고쳐지는지, 범위 밖·비밀 파일 쓰기가 거부되는지 확인.
  5. 툴바 "변경 검토"로 diff·되돌리기, "프로젝트 추가/다시 시작…"으로 같은 대화가 이어지는지, 칩 우클릭 "범위에서 제외", "중지".

## 3. 현재 상태
| 항목 | 상태 |
|---|---|
| 진행 단계 | Phase 5a·5b 완료, 편집기 자동 줄 바꿈 병합, v1.2.0 릴리스. 5c 미착수 |
| 빌드 | `dotnet build DotDeck.slnx` 경고 0·오류 0 (2026-11-09) |
| 테스트 | 합계 1313 / 실패 0 / 건너뜀 2(심볼릭 링크 생성 권한, 기존) — 병합 후 기준 |
| Codex 검증 | 5b-1·5b-3·5b-4 코드 단계마다 `codex-verifier`로 검증·재검증 후 커밋(5b-4는 4회). 5b-2(원본 자산 번들)·5b-5(문서)는 해시·문서 확인만 |
| 원격 동기화 | `feat/ai-cli` push, PR #3(https://github.com/jwlee8303-sys/DotDeck/pull/3) → `main` 병합 |
| 미커밋 변경 | 없음(문서 커밋 후). `.claude/`는 사용자 로컬 설정 — 커밋·수정 금지 |

### 5b 커밋
| 커밋 | 내용 |
|---|---|
| 453518d | docs: 5a 변경 이력과 인수인계 갱신 |
| c027f28 | 5b-1 ConPTY 터미널 실행기(Job Object, 의사 콘솔 닫기는 별도 작업 항목) |
| 4f0d611 | 5b-2 xterm.js 6.0.0·addon-fit 0.11.0 번들(SHA-256 고정, `.gitattributes -text`, 고지 파일) |
| 67d3e79 | 5b-3 터미널 패널(WebView2 가상 호스트)·PowerShell 터미널 |
| 2c979f8 | 5b-4 대화형 Claude 세션·범위 칩·같은 대화 재시작 |
| bd363a9 | 5b-5 docs: 5b 변경 이력과 인수인계 갱신 |
| a5048ac | Merge `feat/editor-word-wrap`(소프트 랩 Alt+Z, 상태줄 재배치). 충돌 해결: CHANGELOG 통합, 생성자, 키 바인딩, AI 배지를 상태줄 왼쪽으로. Codex 검증 지적 없음 |

5a 커밋 목록은 `git log --oneline v1.1.0..453518d`로 본다.

### 진행 중
- 없음.

## 4. 다음 할 일
1. **사용자 수동 확인(5b·줄 바꿈)** — 2절 순서 + 터미널에 포커스가 있을 때 Alt+Z 동작. 완료 기준: SPEC 5b 완료 기준 충족. 문제가 나오면 `fix/…` 브랜치에서 PATCH(1.2.x).
2. **5c 개발** — 아래 "4-1. 5c 인수인계" 절. 새 브랜치 `feat/ai-cli-5c`(main에서 분기). 시작 전에 단계 계획을 사용자에게 보여 주고 확인받는다.
3. 릴리스는 `release` 스킬로만(버전 파일 직접 수정 금지). 5c 완료 시 MINOR(1.3.0).

## 4-1. 5c 인수인계 (별도 개발)
### 목표와 완료 기준
- SPEC 5c(`docs/SPEC.md` Phase 표): 11.6 컨텍스트 1단계 고도화 + **내장 MCP 서버**(11.6 2단계 도구), Codex 지원.
- 완료 기준: 경로를 말하지 않고 ① 열린 파일 수정 ② 특정 프로젝트에 파일 생성 ③ md 개수 세기 ④ 파일 분석 — 4가지 시나리오를 사용자가 샌드박스에서 수동 확인.
- 릴리스 번호: DESIGN A13은 "5a 1.2.0 / 5b 1.3.0 / 5c 1.4.0"이었으나 5a·5b를 1.2.0으로 함께 냈으므로 5c는 **1.3.0**(5c 시작 시 A13 문구 갱신).

### 제안 단계 (각 1커밋: 빌드 → 테스트 → `/codex-review` → 커밋)
| 단계 | 내용 | 시작점(현재 코드) |
|---|---|---|
| 5c-1 | MCP 프로토콜 코어: `JsonRpcFraming`(줄 단위 JSON-RPC 2.0 stdio), `McpServer`(initialize·tools/list·tools/call), `IAiSessionHost` 인터페이스. 새 NuGet 없음(A1) | 새 네임스페이스 `src/DotDeck.Core/AiCli/Mcp/`(기존 `src/DotDeck.Core/Mcp/`는 사용자 MCP 설정 탐색용 — 이름 충돌 주의) |
| 5c-2 | 도구 5종: `list_projects`, `get_open_documents`, `get_active_document`(선택 영역 포함), `find_files(project, pattern)`(표시 필터·제외 폴더 적용, 비밀 파일 제외), `open_in_editor(path)`. 범위 밖 경로는 반환·열기 금지(`ScopeChecker` 재사용), 출력은 `SecretMasker` | `ScopeChecker`, `ContextDocumentBuilder`, 탐색기 필터 |
| 5c-3 | 브리지: `DotDeck.exe --dotdeck-mcp <sessionId>` 하위 모드(stdio ↔ Named Pipe), 앱 쪽 `NamedPipeMcpHost`(현재 사용자 ACL, 세션별 파이프 이름, `bridge.key` DPAPI 토큰 인증, 외부 네트워크 없음). 세션 폴더에 `mcp.json` 작성 → `--mcp-config` 전달 | `src/DotDeck.App/Program.cs`(가드·heartbeat 분기 옆), `GuardHost`의 스위치 상수, `CliLaunch.McpConfigPath`와 `CliCommandBuilder`(인자 자리 이미 있음), `SessionFilesWriter` |
| 5c-4 | 가드: `mcp__dotdeck_session__*` 통과, 사용자 MCP 도구는 대화형 ask·비대화형 `--strict-mcp-config`(A7). 세션 종료·범위 축소 시 파이프 응답도 즉시 반영 | `GuardEvaluator`, `CliCommandBuilder` |
| 5c-5 | 컨텍스트 1단계 고도화: 프로젝트 이름↔루트 표, 열린·활성 문서, 제외 폴더·표시 필터 규칙, "모호하면 후보 제시", "활성 문서 프로젝트 우선", MCP 도구 안내. 12,000자 상한(A3) | `src/DotDeck.Core/AiCli/Context/ContextDocumentBuilder.cs` |
| 5c-6 | Codex 지원: `AiSessionManager.Start`의 Codex 거부 해제, `SessionLaunchViewModel.CodexNote` 제거. 비대화형 `codex exec … -s workspace-write --json -o <last.txt> -` + JSONL 파서, 대화형은 ConPTY 탭 `-a on-request --no-daemon`, 이어가기는 비대화형만(A10). `codex.cmd` 대신 `node.exe codex.js`(A9). Windows sandbox 미설정이면 경고하고 비대화형 비활성 | `AiSessionManager.cs`(`CliKind.Codex` 거부), `CliCommandBuilder.BuildCodex`, `CliLocator`, `SessionPresetStore`(`codex.threadId`) |
| 5c-7 | 세션 폴더 정리: 요약 수락·닫기 후 7일 / 최근 20세션(A15), 실행 중 세션 제외, 앱 시작 시 백그라운드 | `%LOCALAPPDATA%\DotDeck\sessions\<id>\` |
| 5c-8 | 문서·수동 확인: CHANGELOG, HANDOFF, DESIGN 5c 메모, 4개 시나리오 실행 방법 보고 | — |

### 확인 필요(스파이크)
- Claude Code `--mcp-config` 서버가 대화형·`-p` 모두에서 로드되는지, 도구 이름 접두(`mcp__<서버>__<도구>`)가 설계의 `mcp__dotdeck_session__*`와 맞는지 — 샌드박스 `claude --help`·실측.
- Codex `--json`의 thread id 필드, Windows sandbox 설정 여부 판별 방법(DESIGN "명령줄" 절의 확인 필요).
- Named Pipe 보안: 파이프 이름 추측·다른 사용자 접근 차단(ACL), 토큰 비교는 고정 시간.

### 지켜야 할 것
- 새 NuGet 금지(필요하면 사용자 승인). MCP는 직접 구현.
- 실제 `~/.claude`·`~/.codex` 수정 금지(`--home`), 권한 우회 옵션 금지(`ForbiddenOptions`), MCP 결과에 비밀 파일 경로·내용 금지.
- 설계 변경은 DESIGN "AI CLI 연동 설계"에 먼저 기록하고 사용자 확인 후 구현.

## 5. 결정 사항과 이유
| 결정 | 이유 | 기각한 대안 |
|---|---|---|
| CLI는 셸 없이 직접 실행, 프롬프트는 표준 입력 | 인용·주입 위험 제거, 32K 명령줄 제한 회피 | `cmd /c`, 명령줄 인수 |
| 범위 강제는 Claude PreToolUse 훅(exec-form `DotDeck.exe --dotdeck-guard`) + `permissions.deny` | 공식 확장점, 실측 콜드 스타트 p50 68ms | 파일 감시 후 사후 차단 |
| 범위 안 비밀 파일은 모든 모드에서 차단 | 사용자 결정 12 | 확인 후 허용 |
| 스냅샷은 세션 폴더 `backups\`(DPAPI)·사용자 백업과 분리 | 긴 세션이 사용자 백업을 밀어내지 않게 | 주 BackupStore 재사용 |
| 터미널은 ConPTY + WebView2의 xterm.js, 자산은 내장 리소스를 가상 호스트(`https://term.dotdeck.local`)로만 제공 | 실제 TUI 호환, 오프라인·CSP로 외부 로드 차단 | 자체 VT 렌더러, 파일 경로/CDN 로드 |
| xterm.js 원본 무수정·해시 고정·`-text` | autocrlf가 바이트를 바꾸지 않게, 출처 검증 | npm 빌드 단계 추가 |
| ClosePseudoConsole은 종료 콜백과 분리된 풀 작업 항목에서 | 24H2 이전 Windows의 출력 미소비 교착 회피 | 콜백 안에서 즉시 닫기 |
| 대화형 세션은 프롬프트 없이 시작, 빠른 실행은 대화상자 생략 | 대화형은 TUI에서 입력 | 5a처럼 항상 대화상자 |
| 한 대화는 CLI 하나: `(CLI, 대화 ID)` 원자적 예약, 중지됐어도 프로세스가 살아 있으면 소유 | 같은 대화 동시 실행 시 기록 손상 | 상태(Running)만 검사 |
| 재시작은 저장·확인 → 기존 중지 → 프로세스 종료 대기(10초) → 같은 대화 ID로 시작 | 취소 시 기존 세션 유지, 대화 연속성 | 먼저 중지 후 대화상자 |
| AI 세션 탭 닫기 = 세션 중지(확인 후) | 보이지 않는 실행 세션 방지 | 탭만 숨김 |
| 세션 중 열린 탭 반영은 기존 콘텐츠 감시기 재사용 | 모드와 무관하게 동일 경로, 편집 중 충돌 확인 유지 | 세션 전용 알림 |

## 6. 주의·함정
- **Git Bash heredoc은 `\`를 반으로 줄인다** — C# 소스나 Windows 경로가 든 문서를 heredoc/`sed`로 쓰면 깨진다. 스크래치패드에 Write로 node 스크립트를 쓰고 실행한다.
- 사용자 전역 PreToolUse 훅이 bash 명령 안의 `.env` 문자열을 막는다.
- 터미널 자산을 수정·갱신하면 `THIRD-PARTY-NOTICES.txt`의 해시와 `TerminalAssetsTests`를 같이 바꾼다. 줄바꿈 변환 금지(`.gitattributes`).
- `TerminalView` 단독 로드 테스트(STA)에서는 앱 리소스가 없으므로 XAML에서 `StaticResource`로 앱 스타일을 참조하면 실패한다 — `DynamicResource` 사용.
- UI 스레드에서 비동기 대기 후 UI를 만지면 `ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext)`처럼 컨텍스트 복귀를 명시한다(SuppressThrowing 단독은 복귀하지 않음).
- `AiSessionManager` 이벤트는 백그라운드 스레드에서 온다. VM은 `Post`로 UI 스레드에 넘긴다.
- 대화형 Claude는 `-p`가 아니다 — 프롬프트를 넘기지 않는다. 플래그는 샌드박스 `claude --help`(2.1.319)로 확인한 것만 쓴다.
- Debug 빌드는 실행 중인 DotDeck이 DLL을 잠그면 실패한다. **사용자의 앱을 강제 종료하지 않는다**(`taskkill /IM` 금지, 직접 띄운 PID만).
- 키 입력 전송·화면 캡처 자동화는 자동 모드 분류기가 거부한다 — 우회하지 말고 실행 방법을 안내한다.

## 7. 알려진 문제·미해결 질문
- 보류(Codex 지적, 영향 낮음): 5b-1 플러드+종료 교착 테스트는 시간 기반이며 이 OS(빌드 26200)에서는 24H2 이전 교착을 재현할 수 없음. 5a 보류 항목(세션 백업 `.lock` 10초, 2MB 초과 비강제 되돌리기, 정션 아래 읽기 사본 캡처, 대소문자 구분 폴더) 유지.
- 재시작 흐름(저장 → 중지 → 대기 → 시작)의 UI 스레드 복귀는 ShellViewModel 수준이라 단위 테스트 없음(Core의 예약·대기 규칙은 테스트됨).
- 수동 확인 미실행: 실제 Claude TUI 렌더링, 한글 IME, 크기 조절, 대화형 모드의 가드 거부 표시, 테마 전환 — 사용자 확인 필요(2절).
- 세션 폴더 정리(7일·최근 20세션)는 5c-7.
- 터미널(WebView2)에 포커스가 있을 때 Alt+Z 동작 미확인(활성 문서가 없으면 바인딩은 조용히 비활성).
- 알려진 한계: 하드링크, 검사↔쓰기 사이 링크 교체(TOCTOU) — 변경 요약으로 사후 탐지.

- 보류(v1.2.0 릴리스 전 Codex P2): `settings.json`의 `WordWrapExtensions`에 문자열이 아닌 원소가 있으면 `AppSettingsStore.Load`가 기본값을 반환하고, 다음 저장에서 테마·모르는 키 등 설정 전체를 덮어쓴다(손으로 손상시킨 파일에서만, 기존 손상 처리 경로). 다음 PATCH에서 필드 단위 관대한 파싱 또는 손상 파일 `.bak` 보존 + 테스트. 관련: `src/DotDeck.Core/Settings/AppSettings.cs`.

## 8. 참고
- TODO/FIXME: 없음.
- 스태시: 없음.
- 반드시 지킬 규칙(요약): 실제 `~/.claude`·`~/.codex`·환경변수·hosts 수정 금지(`--home` 사용), 권한 우회 옵션 사용 금지, 비밀 파일 내용 조사 금지(테스트는 가짜 값), 새 NuGet은 승인 후(CPM), main 직접 커밋·push·PR·태그는 사용자 확인 후, 커밋 전 `git diff --staged` 비밀 점검, `.claude/` 커밋 금지.
- 이전 작업 상세는 git 이력과 `CHANGELOG.md`.

## 9. 인계자 메모
- 각 sub-phase 보고 형식: 구현 내용 / 변경 파일 / 테스트 결과 / Codex 검증(반영·기각·보류) / 알려진 제한 / 다음 단계 확인 질문.
- 승인 조건(완료): 샌드박스에서만 CLAUDE_CONFIG_DIR·CODEX_HOME 전달 테스트, 훅 병합 실측, `--system-prompt-snapshot` 확인, 비밀 파일 전 모드 차단, 가드 콜드 스타트 200ms 이하, WinExe stdio 동작, xterm.js LICENSE 동봉·버전 고정.

## 10. 세션 재개 프롬프트
```text
D:\project\DotDeck 에서 작업을 이어받는다. 먼저 docs/HANDOFF.md와 CLAUDE.md, docs/SPEC.md 11절, docs/DESIGN.md "AI CLI 연동 설계"를 읽어라.
Phase 5a·5b는 v1.2.0으로 main에 릴리스되었다. 5c는 main에서 feat/ai-cli-5c 브랜치를 만들어 진행한다. `git status --short`와 `git log --oneline -15`로 상태를 확인하고,
`dotnet build DotDeck.slnx`와 `dotnet test --solution DotDeck.slnx`가 통과하는지 먼저 확인하라.
HANDOFF "4-1. 5c 인수인계" 절의 단계표로 계획을 보여 주고 확인받은 뒤 5c-1부터 시작하라. 스파이크 항목은 샌드박스에서 먼저 확인한다.
실제 ~/.claude·~/.codex를 수정하지 말고 --home 샌드박스만 쓴다. push·PR은 사용자 확인 후에만 한다.
```

## 11. 문서 이력
- 2026-11-08: Codex 전용 편집기(2a~2b, 보안 재설계 ②~⑤, 리뷰 반영 A~C) 진행 기록 — v1.1.0까지 반영, 상세는 git 이력.
- 2026-11-09: Phase 5a(AI CLI 연동) 완료 상태로 전면 갱신, 이전 기록은 한 줄로 축약.
- 2026-11-09: Phase 5b(대화형 터미널) 완료 반영 — 5b 커밋·결정·함정·수동 확인 순서 추가.
- 2026-11-09: feat/editor-word-wrap 병합·v1.2.0 릴리스 반영, 5c 인수인계 절(단계·시작점·스파이크) 추가.
