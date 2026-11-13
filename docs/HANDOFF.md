# 인수인계 — DotDeck

## 현재 상태 (2026-11-12 00:38 KST, 자동 인계 — 요청 bc7f448b)

### 1. 작업 요약
- 경로 `D:\project\DotDeck`, 버전 `1.4.0`(`Directory.Build.props`), `main@45ae55d`(chore(release): v1.4.0).
- 이 세션에서는 새 개발 작업이 없었다(인계 요청만 수행). 진행 중 작업 없음.
- 완료: Phase 5a·5b·5c, 통합 경로 제외 설정 → v1.4.0 릴리스.
- 중단 지점: 릴리스 이후 사용자 수동 확인 단계. 수동 확인 실행 여부는 (확인 필요).
- 다음 첫 행동: 사용자에게 수동 확인 결과(아래 3절 1·2) 또는 다음 작업 지시를 묻는다.

### 2. 변경·검증 상태
- 미커밋 변경: `docs/HANDOFF.md`(이 인계 절 추가분)만. untracked·스태시 없음(`git status -sb`, `git stash list`, 2026-11-12 00:38 확인). 코드 변경 없음.
- 원격: `origin/main`과 차이 0/0 — fetch 없이 로컬 추적 정보 기준.
- 마지막 검증(2026-11-09, v1.4.0 릴리스 전): Release 빌드 경고·오류 0, 테스트 1380개 중 통과 1378·실패 0·건너뜀 2(링크 권한). 이후 코드 변경 없음. 이번 자동 인계에서는 빌드·테스트 미실행.
- 실제 WPF 조작·실제 모델 시나리오: 미실행.

### 3. 다음 할 일
1. 5c 수동 시나리오 4종 / `docs/PHASE5C-VALIDATION.md` / 샌드박스 `--home`에서 사용자가 4개 시나리오 확인.
2. 제외 설정 UI 수동 확인 / `ExclusionSettingsDialog`, 저장 미리보기·AI 변경검토 체크박스 / 메뉴 "자동백업·변경검토 제외항목"과 두 체크박스 즉시 등록 동작 확인.
3. 보류 P2: 설정 손상 시 전체 덮어쓰기 / `src/DotDeck.Core/Settings/AppSettings.cs` / 필드 단위 관대한 파싱 또는 `.bak` 보존 + 테스트, PATCH 릴리스(아래 7절).
- 문제 발견 시 `fix/…` 브랜치에서 수정, 릴리스는 `release` 스킬로만.

### 4. 결정·주의사항
- 아래 "릴리스 1.4.0"·"통합 경로 제외 설정" 절이 그 아래 과거 인계보다 우선한다(시스템 기록 자동 분류는 폐기됨).
- 사용자 앱을 강제 종료하지 않는다. 실제 `~/.claude`·`~/.codex` 수정 금지. 커밋·push·릴리스는 사용자 요청 후에만.

### 5. 실행·참고
- 빌드 `dotnet build DotDeck.slnx`, 테스트 `dotnet test --solution DotDeck.slnx`, 실행 `dotnet run --project src/DotDeck.App -c Release -- --home <샌드박스>` (환경변수 `DOTDECK_HOME` 대체 가능).
- 문서: `CLAUDE.md`, `docs/SPEC.md`, `docs/DESIGN.md`, `docs/PHASE5C-VALIDATION.md`, `CHANGELOG.md`.

### 6. 재개 프롬프트
```text
D:\project\DotDeck 의 CLAUDE.md와 docs/HANDOFF.md "현재 상태" 절을 읽어라.
git status·브랜치·HEAD(기록: main@45ae55d, v1.4.0, 미커밋은 docs/HANDOFF.md뿐)를 다시 확인하고 다른 점을 반영하라.
'다음 첫 행동'부터 이어가되, 기존 변경을 보존하고 승인된 범위에서만 진행하라.
```

## 릴리스 1.4.0 (2026-11-09)

- 사용자 요청에 따라 5c·Codex 연동·통합 제외 설정을 main에 통합하고 v1.4.0으로 릴리스한다. 아래의 미커밋·버전 미변경 설명은 작업 당시 기록이다.
- 릴리스 전 독립 검토 P2 1건 반영: 제외 설정 손상 시 앱은 경고 후 시작하고 원본을 보존한다. 복구 후 재시작 전까지 제외 설정 저장은 차단한다. 가드의 설정 읽기는 계속 엄격하게 실패한다.
- Release 빌드 경고·오류 0, 전체 테스트 1380개 중 통과 1378·실패 0·링크 권한 관련 건너뜀 2. 공백 린트 통과. 실제 WPF 조작·실제 모델 시나리오는 미실행이다.

## 최신 변경 — 통합 경로 제외 설정

- 사용자 최종 지시: 메뉴는 "자동백업·변경검토 제외항목" 하나, 내부 두 목록은 독립적으로 적용. 파일/폴더 전체 경로 직접 입력·선택·제거, 폴더 하위 항목 제외, 저장 전 미리보기/AI 변경검토의 좌측 체크박스로 즉시 등록한다.
- Core `Settings/PathExclusions.cs`: 경로 모델·정책·원자적 저장. `<roaming>/exclusions.json`의 automaticBackup/changeReview 배열, 초기 목록 없음, 알 수 없는 키 보존. 잘못된 설정을 빈 값으로 덮어쓰지 않는다.
- App `ExclusionSettingsViewModel`, `ExclusionSettingsDialog`, `ShellViewModel.Exclusions.cs`와 메뉴·검토 체크박스 추가. 파일 쓰기는 UI 밖에서 수행한다. 백업 제외 등록 완료 전 실제 저장을 기다리며, 검토 제외 등록 중인 행은 되돌릴 수 없다.
- 자동백업 제외: SafeWriter 저장/복원 전, 파일 덮어쓰기, AI 시작/쓰기 전 캡처. 변경검토 제외: 저장 전 diff, AI 요약·diff·되돌리기. 두 목록은 서로의 기능을 차단하지 않는다. 기존 백업은 보존한다.
- Claude·Codex 전용 SystemRecordPolicy, system-records.json 2개, 전용 테스트·App 로더·가드 경로 전달을 삭제했다. 기존 카탈로그와 아래 과거 인계의 자동 분류 설명은 현재 적용되지 않는다. 인증 파일 비밀 정책은 별도로 유지한다.
- 핵심 변경 파일: Core PathExclusions/BackupStore/SafeWriter/SessionSnapshot/ScopeIndex/ChangeSummary/AiSessionManager, App ExclusionSettingsDialog/ExclusionSettingsViewModel/ShellViewModel.Exclusions/DiffReview/ChangeReview/Program, 관련 Core·App 테스트.
- 실행 중인 기존 앱은 종료하지 않았다. 빌드·테스트 출력은 `obj/exclusions-validation/`로 격리했다. 새 실행 파일은 `obj/exclusions-validation/bin/DotDeck.App/release/DotDeck.exe`다. 기존 앱을 닫고 동일한 `--home`으로 실행해 통합 설정과 두 체크박스를 수동 확인한다.
- 버전·의존성·프로젝트 설정·커밋·푸시는 변경하지 않았다. 아래 5c 인계와 시스템 기록 분류는 과거 기록이며 이 절이 우선한다.
- 검증: 격리 출력 경로 Release 빌드 경고·오류 0, 전체 테스트 1377개 중 성공 1375·실패 0·기존 링크 권한 관련 건너뜀 2. 새 정책과 독립 적용·즉시 등록·등록 중 저장 대기·강제 되돌리기 차단을 검증했다. 폐기된 전용 분류 테스트는 경로 기반 테스트로 대체했다. 실제 WPF 화면 수동 조작은 미실행이다.

## 현재 인계 — Phase 5c (2026-11-09)

사용자 피드백 보완:

- 프로젝트 제목 우클릭에 Codex 대화형 빠른 시작 추가. 시작 화면에서 Codex도 주 프로젝트 + 여러 추가 프로젝트 선택 유지(두 모드·CLI 전환 테스트). 선택 범위를 `--add-dir` 및 sandbox writable_roots에 모두 전달한다.
- 비대화형 `test.md` 미수정의 실제 샌드박스 최종 응답: 프로젝트 조회·검색이 `approval policy is never`로 거부됨. 원인은 자체 MCP 도구 승인 정책 누락. 내장 5개 도구에만 `approval_mode="approve"`, 서버 기본 `prompt`·도구 허용 목록·`required=true`를 설정했다. 조회 도구 readOnlyHint도 명시했다. 실제 Codex 0.167.0 `app-server`의 `config/read`로 5개 설정 인식을 확인했으며 모델 호출·인증 파일 접근은 하지 않았다.
- 알려진 시스템 기록은 AI 도구가 직접 썼어도 백업·검토에서 제외한다. scope.json에 제외 경로/전체 제외 여부를 전달해 가드 사전 캡처에도 적용. 기존 시스템 캡처·도구 기록도 검토에서 제외한다. Codex 상태 DB·WAL·SHM, 이력·캐시·임시 기록 카탈로그를 보강했다.
- Codex 실패 도구의 종료 코드·실패 상태·안전한 권한/sandbox 오류 분류를 표시한다. 모델 응답 완료를 파일 수정 성공으로 표현하지 않는다.
- 실행 중 Debug 앱은 종료하지 않았다. 검증은 `Release`로 분리했다. 새 코드로 재확인하려면 기존 앱을 사용자가 닫고 `dotnet run --project src/DotDeck.App -c Release -- --home D:\Temp\DotDeckTest`로 실행한다. 실제 모델에서 동일 요청 재실행은 사용자 확인 대기다.

- 브랜치: `feat/ai-cli-5c`, 기준 `main` = `36651f4`(v1.3.0). 작업 시작 시 미커밋 변경 없음.
- 5c-1~7 구현 및 자동 검증 완료. 5c-8 문서 작성 완료, **실제 모델을 쓰는 4개 수동 시나리오는 사용자 확인 대기**. 절차: [PHASE5C-VALIDATION.md](PHASE5C-VALIDATION.md).
- 버전 파일·패키지·프로젝트 설정은 변경하지 않았다. 커밋·푸시·PR·릴리스도 하지 않았다. 기존 인계의 “5c는 1.3.0”은 이미 나온 시스템 기록 보완 릴리스 때문에 유효하지 않다. 다음 버전은 별도 승인 시 정한다.

| 항목 | 구현 위치·동작 |
|---|---|
| MCP 프로토콜·도구 | Core `AiCli/Mcp/{JsonRpcFraming,McpServer,SessionTools}.cs`. initialize·ping·tools/list·tools/call, 도구 5종, 줄 단위 UTF-8, 메시지·결과 상한, 오류에 원문 경로·본문 노출 금지 |
| 범위·편집기 | 현재 세션 범위 검사, 최종 경로·비밀 파일 제외, 탐색기 표시 필터·제외 폴더, 활성 문서 선택 영역 마스킹, 파일 열기 전·비동기 처리 후 재검증 |
| 보안 브리지 | App `Infrastructure/Windows/NamedPipeMcpHost.cs`, `Program.cs`의 `--dotdeck-mcp`. 현재 사용자 전용 파이프, DPAPI 토큰 인증(고정 시간 비교), 종료 시 취소. 범위 변경 중 나온 도구 결과는 폐기 |
| CLI 연결 | Claude 세션별 `mcp.json` + `--mcp-config`. 비대화형 `--strict-mcp-config`, 자체 MCP 허용·외부 MCP ask는 기존 가드 재사용. Codex는 `-c mcp_servers.dotdeck_session=...`로 세션 서버만 주입(전역 파일 수정 없음) |
| 컨텍스트 | 프로젝트 이름↔루트·활성 문서 우선·모호하면 후보·제외/표시 규칙·MCP 안내. 상한 12,000자, 최종 경로로 열린 문서 필터 |
| Codex | UI 선택, `workspace-write`, 명시적 Windows sandbox 설정 검사·고정, inherited writable_roots 제거, JSONL 요약·thread ID 저장·비대화형 resume. 비대화형은 sandbox 미설정 시 비활성. 범위 축소 시 중지 후 재시작 |
| 정리 | Core `SessionRetention.cs`. 수락·닫기 표시 후 7일 또는 최근 20개 초과, 앱 시작 시 백그라운드. lease로 다른 앱 인스턴스의 실행 세션도 보호. 미검토·링크 포함 폴더 보존 |

검증:

- `dotnet build DotDeck.slnx -c Release`: 경고 0·오류 0.
- `dotnet test --solution DotDeck.slnx -c Release`: 1393개 중 성공 1391, 실패 0, 기존 심볼릭 링크 권한 관련 건너뜀 2. 기준 실행 1345개에서 5c 및 피드백 테스트 48개를 추가했다.
- 실제 WinExe 브리지 인증·한글 JSON 왕복·정상 EOF, 위조 인증 차단을 자동 테스트했다. Codex exec resume 옵션은 설치 CLI 도움말로 문법을 확인했다.
- 별도 `/codex-review` 도구/독립 검증 에이전트 실행은 하지 않았다. 자체 변경 검토와 빌드·테스트를 수행했다.

다음 작업은 수동 확인이다. 실제 홈의 인증 파일을 읽거나 복사하지 않고 `--home` 샌드박스에서 사용자가 로그인한다. 수동 확인에서 문제가 발견되면 이 브랜치에서 수정하고, 릴리스·버전 변경·커밋·푸시는 사용자의 별도 요청 후에만 진행한다.

알려진 제한:

- Codex에는 Claude PreToolUse 가드·비밀 파일 차단·읽기 제한이 없다(A9). 내장 MCP의 응답·파일 열기 제한은 두 CLI에 동일하게 적용한다. Codex 네이티브 파일 도구·셸까지 같은 가드가 적용된다고 안내하지 않는다.
- Windows sandbox 설정 감지는 `config.toml`의 명시적 `windows.sandbox` 값만 확인한다. OS 준비 완료·로그인은 CLI가 검증하며 실패를 우회하지 않는다. 프로필/관리형 설정만으로 모드가 공급되면 비대화형이 보수적으로 비활성일 수 있다.
- 실제 모델·Claude의 MCP 로드/접두·외부 MCP 승인 화면, Codex TUI·프로비저닝 확인은 수동 대기다.
- 실행 중단·충돌로 수락/닫기 표시가 없는 세션은 자동 삭제하지 않는다. 링크 교체 경합(TOCTOU)·하드링크는 기존 한계다.

## 이전 인계 기록 — 5b 완료 당시

아래는 이전 인계 원문이며 현재 상태·다음 단계·버전 안내는 위 **현재 인계**가 우선한다. 특히 main은 이후 v1.3.0으로 릴리스되었다.

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

## 10. 세션 재개 프롬프트 (과거 — 상단 "현재 상태" 6절 사용)
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
