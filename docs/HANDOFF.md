# 인수인계 — DotDeck

- 작성: 2026-11-09 (Claude Code)
- 버전: `1.1.0`(`Directory.Build.props`, 미변경) · 브랜치 `feat/ai-cli` @ `2c979f8`(+ 5b 문서 커밋)
- 한 줄 요약: **Phase 5a(비대화형 Claude 세션)와 5b(ConPTY + xterm.js 대화형 터미널, PowerShell 터미널, 대화형 Claude 세션·범위 칩) 구현과 단계별 Codex 검증·커밋을 마쳤다.** 다음은 사용자 5b 수동 확인 → 5c(MCP·Named Pipe·컨텍스트 고도화·Codex 지원·세션 폴더 정리).

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
| 진행 단계 | Phase 5b 완료(5b-1 ~ 5b-5), 사용자 확인 대기 |
| 빌드 | `dotnet build DotDeck.slnx` 경고 0·오류 0 (2026-11-09) |
| 테스트 | 합계 1284 / 실패 0 / 건너뜀 2(심볼릭 링크 생성 권한, 기존) |
| Codex 검증 | 5b-1·5b-3·5b-4 코드 단계마다 `codex-verifier`로 검증·재검증 후 커밋(5b-4는 4회). 5b-2(원본 자산 번들)·5b-5(문서)는 해시·문서 확인만 |
| 원격 동기화 | upstream 없음(push 안 함). push·PR은 사용자 확인 후 |
| 미커밋 변경 | 없음(문서 커밋 후). `.claude/`는 사용자 로컬 설정 — 커밋·수정 금지 |

### 5b 커밋
| 커밋 | 내용 |
|---|---|
| 453518d | docs: 5a 변경 이력과 인수인계 갱신 |
| c027f28 | 5b-1 ConPTY 터미널 실행기(Job Object, 의사 콘솔 닫기는 별도 작업 항목) |
| 4f0d611 | 5b-2 xterm.js 6.0.0·addon-fit 0.11.0 번들(SHA-256 고정, `.gitattributes -text`, 고지 파일) |
| 67d3e79 | 5b-3 터미널 패널(WebView2 가상 호스트)·PowerShell 터미널 |
| 2c979f8 | 5b-4 대화형 Claude 세션·범위 칩·같은 대화 재시작 |
| (이번) | 5b-5 docs: 5b 변경 이력과 인수인계 갱신 |

5a 커밋 목록은 `git log --oneline v1.1.0..453518d`로 본다.

### 진행 중
- 없음.

## 4. 다음 할 일
1. **사용자 5b 확인** — 2절 순서. 완료 기준: 한글 입출력·크기 조절·테마·복사/붙여넣기 정상, 대화형 세션에서 범위 밖·비밀 파일 차단, 열린 탭 반영, 변경 검토·같은 대화 재시작 동작(SPEC 5b 완료 기준).
2. **5c** — 내장 MCP 서버 / Named Pipe 호스트·브리지 / 컨텍스트 고도화 / Codex 대화형 지원 / 세션 폴더 정리(DESIGN의 7일·최근 20세션 정책) / 4개 시나리오 수동 확인. 시작 전에 단계 계획을 보여 주고 확인받는다.
3. 릴리스는 `release` 스킬로만(버전 파일 직접 수정 금지). 5a+5b를 먼저 낼지는 사용자 결정.

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
- 세션 폴더 정리(7일·최근 20세션)는 5c에서 구현 예정.
- 알려진 한계: 하드링크, 검사↔쓰기 사이 링크 교체(TOCTOU) — 변경 요약으로 사후 탐지.

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
브랜치는 feat/ai-cli, Phase 5a·5b는 커밋까지 끝났다. `git status --short`와 `git log --oneline -15`로 상태를 확인하고,
`dotnet build DotDeck.slnx`와 `dotnet test --solution DotDeck.slnx`가 통과하는지 먼저 확인하라.
HANDOFF "다음 할 일" 1번(사용자 5b 확인 결과 반영)부터 시작하고, 5c로 넘어가기 전에 단계 계획을 보여 주고 확인받아라.
실제 ~/.claude·~/.codex를 수정하지 말고 --home 샌드박스만 쓴다. push·PR은 사용자 확인 후에만 한다.
```

## 11. 문서 이력
- 2026-11-08: Codex 전용 편집기(2a~2b, 보안 재설계 ②~⑤, 리뷰 반영 A~C) 진행 기록 — v1.1.0까지 반영, 상세는 git 이력.
- 2026-11-09: Phase 5a(AI CLI 연동) 완료 상태로 전면 갱신, 이전 기록은 한 줄로 축약.
- 2026-11-09: Phase 5b(대화형 터미널) 완료 반영 — 5b 커밋·결정·함정·수동 확인 순서 추가.
