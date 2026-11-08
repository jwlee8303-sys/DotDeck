# 인수인계 (2026-11-08) — Claude Code → Codex

## 1. 현재 상태
- 브랜치 `feat/codex-editors` (main `b3325f5`에서 생성). 단계 0 사양·설계 문서 반영 완료, 제품 코드는 아직 변경 없음.
- main(로컬)에 `feat/explorer-visibility`(547ae6a), `feat/custom-files`(d508a3a), `feat/permission-arg-kinds`(b3325f5) 모두 `--no-ff` 병합 완료. **origin에는 아무것도 push 안 함.**
- 병합 직전 검증(2026-11-08): `dotnet build DotDeck.slnx` 오류 0 / Core.Tests 669 통과·2 건너뜀 / App.Tests 6 통과.
- 미커밋: `.claude/`(로컬 설정, 커밋 대상 아님)만 남음.
- 버전 미변경. 릴리스는 `release` 스킬로 0.2.0 예정(Codex 작업 완료 후).

## 2. 완료한 작업
| 커밋 | 내용 |
|---|---|
| 3e37460 | 탐색기 보이는 항목 규칙·보기 방식·붙여넣기 충돌 처리 |
| 18ccbcd | 모든 폴더 프로젝트에 사용자 지정 파일 노드 |
| 178fad7 | 권한 규칙 인수 형식(tools.json v2)·경고(PermissionLint)·MCP 선택지 표시 수정, App.Tests 프로젝트 추가 |
- Codex 설정 폼 작업: 공식 문서 조사, `architect` 설계안 작성, 사용자 결정 1~7 확정(아래). SPEC 7.5·DESIGN 확장 설계 반영 완료. **코드는 시작 전.**

## 3. 진행 중 / 남은 작업 (순서대로, 각 커밋마다 빌드 → 테스트 → 교차 검증 → 커밋)
| # | 커밋 메시지 초안 | 주요 파일 | 상태 |
|---|---|---|---|
| 0 | `docs(spec): Codex 전용 편집기 사양과 설계 추가` | `docs/SPEC.md`(§2.1 Codex 행, §7.0 문구, 신규 §7.5), `docs/DESIGN.md` | 완료 |
| 2a-1 | `refactor(editors): 편집기 등록소로 종류별 분기 통합` | 신규 `Core/Editors/Registry/*`, `App/ViewModels/Editor/FormRegistry.cs`; 수정 `EditorKind.cs`, `Roles/FileRoleCatalog.cs`, `Roles/EditorOverrideStore.cs`, `TextDocumentViewModel.cs`, `ShellViewModel.Validation.cs`, `.Roles.cs`, `.cs` | **다음 할 일** |
| 2a-2 | `refactor(hooks): 훅 모델·표·실행기를 제품 공용으로 분리` | 신규 `Core/Hooks/*`, `HookTableViewModel.cs`, `Views/Editor/HookTable.xaml`; 수정 `ClaudeSettingsDocument.cs`, `HookRunner.cs`, `SettingsFormViewModel.cs`, `HookTestPaneViewModel.cs`, `FormTemplates.xaml` | 남음 |
| 2a-3 | `refactor(forms): 키-값 표·문자열 목록·frontmatter 프로필 공통화` | `KeyValueTableViewModel.cs`, `StringListViewModel.cs`, `FormViewModels.cs`, `SettingsFormViewModel.cs` | 남음 |
| → | **2a 끝: Claude 편집기 동작이 같은지 사용자 확인** | | |
| 2b-0 | `build(core): Tomlyn 추가와 TOML 무손실 편집기` | `Directory.Packages.props`, `DotDeck.Core.csproj`, 신규 `Core/Toml/TomlEditDocument.cs` + 테스트 | 남음 |
| 2b-1 | `feat(codex): CODEX_HOME·Codex 파일 역할·런타임 파일 보호·스킬 연결 노드` | `Paths/AppPaths*.cs`, `Projects/BuiltinProjects.cs`, 신규 `Catalogs/Codex/file-roles.json`, `Catalogs/Codex/agents-file-roles.json`, `Explorer/LinkedFolders.cs`, `RuntimeFilePolicy.cs`, `ISecretFileClassifier`, `Documents/BackupStore.cs`, `ExplorerViewModel.cs` | 남음 |
| 2b-2 | `feat(codex-config): 스키마 기반 config.toml 폼·계층·신뢰 배너·검증` | `Catalogs/Codex/config-schema.json`, `Core/Codex/CodexConfigDocument.cs`, `CodexTrustResolver.cs`, `Core/Schema/*`, `App/ViewModels/Editor/Codex/CodexConfigFormViewModel.cs`, `SchemaGrid.xaml` | 남음 |
| 2b-3 | `feat(codex-config): MCP 서버 탭과 비밀값 마스킹` (+ DPAPI 백업) | `CodexSecretRules.cs`, `DiffReviewViewModel.cs`, 백업 보호기 | 남음 |
| → | **중간 확인: config.toml 폼 실행 방법/스크린샷 제공 후 사용자 확인** | | |
| 2b-4 | `feat(codex-hooks): hooks.json·인라인 훅 편집과 Codex 훅 테스트` | `Catalogs/Codex/hook-events.json`, `CodexHooksJsonStore`, `CodexInlineHookStore`, `CrossFileEdit.cs` | 남음 |
| 2b-5 | `feat(codex-agents): 사용자 지정 에이전트 TOML 폼과 템플릿` | `CodexAgentDocument.cs`, `Templates/Codex/*.toml`, `TemplateCatalog.cs` | 남음 |
| 2b-6 | `feat(codex-skills): Codex 스킬 검증·openai.yaml 폼·비활성화 토글` | `YamlBlockDocument.cs`, `OpenAiYamlFormViewModel.cs` | 남음 |
| 2b-7 | `feat(codex-docs): AGENTS.md override·크기 경고` | 검증 공급자 | 남음 |
| 2b-8 | `feat(codex-rules): 단순 prefix_rule 폼과 execpolicy 검사` | `Core/Rules/StarlarkRuleFile.cs`, `CodexRulesFormViewModel.cs` | 남음 |
- CHANGELOG `## [Unreleased]`에 커밋마다 누적. 보고 형식: 구현 내용 / 변경 파일 / 테스트 결과 / 검증 결과(반영·기각·보류) / 공식 문서와 지시서 차이 / 알려진 제한 / 다음 단계 확인 질문.

## 4. 사용자 확정 결정 (2026-11-08)
1. **Tomlyn 승인.** nuget.org 최신 안정 버전 확인 결과 **2.10.2** 존재(2.10.1, 2.10.0 이전). 파서(위치·trivia)로만 쓰고 수정은 자체 위치 패처. 2b-0 시험 구현에 **바이트 단위 왕복 테스트 필수**(CRLF, BOM, 주석, `[projects.'d:\project\dotdeck']`, 점 키, 인라인 테이블, 배열 테이블). 실패 시 자체 파서로 전환하고 보고. YAML은 새 의존성 없이 자체 `YamlBlockDocument`.
2. 카탈로그 덮어쓰기 위치는 **`%APPDATA%\DotDeck\catalogs\Codex\`** (지시서의 `schemas\` 아님). SPEC에도 이 경로로 적는다.
3. 스킬은 **Codex 전역 트리 안 가상 노드** `스킬 (~/.agents/skills)` (`CustomFilesProjectSource`와 같은 래퍼 패턴, 저장 안 하고 계산).
4. `auth.json` = 비밀 파일(열기·백업 금지). 런타임 데이터 = 기본 숨김 + 읽기 전용. `/plugins` = 표시 + 읽기 전용. `/skills/.system` = 표시 + "Codex 내장" 안내.
5. **비밀값이 든 파일은 DPAPI(CurrentUser)로 암호화해 백업.** `System.Security.Cryptography.ProtectedData` 패키지 추가 승인(최신 안정 10.0.14 확인, .NET 10 계열에 맞춰 CPM에 추가). manifest에 `protected: true` 등 표시, 복원 시 복호화. SPEC에 "로그·백업 목록·변경 기록·diff 표시에 비밀값 평문 금지" 규칙 명시. (참고: 현재 BackupStore는 원본 바이트를 그대로 복사하므로 Claude settings.json `env` 비밀값도 평문 백업되고 있음 → 이 결정으로 함께 해결.)
6. 에이전트 toml의 덮어쓸 수 없는 키(`sandbox_mode`, `approval_policy`, `model_provider`, `mcp_servers`)는 **문서를 따른다**: 폼에 노출하지 않고, 있으면 경고 + 값 보존.
7. 브랜치: 빌드·테스트 통과 확인 후 `feat/permission-arg-kinds`를 main에 병합하고 `feat/codex-editors` 생성 → **완료**. push 안 함.

## 5. 설계 핵심 요약
- **편집기 등록소:** `EditorKind` enum 유지 + Core `EditorRegistry`(`EditorDescriptor`: Kind, 저장 이름, 라벨, 제품, HasForm/HasOutline, Validate, CanShowForm) + App `FormRegistry`. 저장 이름(`settingsForm` 등)은 그대로 호환. 새 값: `CodexConfig`(`codexConfigForm`), `CodexHooks`(`codexHooksForm`), `CodexAgent`(`codexAgentForm`), `CodexSkillMetadata`(`openaiYamlForm`), `CodexRules`(`codexRulesForm`). SKILL.md는 `Skill`, AGENTS.md는 `Markdown` + `Role.Product`로 검증 분기.
- **호스트 분리:** `ITextDocumentHost`는 공통 멤버만, `IClaudeFormServices` / `ICodexFormServices`로 나눔.
- **역할 판별:** `FileRoleResolver`에 `[claude, codex, agents]` 카탈로그 3개 전달 + `configDirAliases`(CODEX_HOME 폴더명이 `.codex`가 아니어도 전역 판별).
- **TOML(`Core/Toml/TomlEditDocument`):** 값 토큰 범위만 교체, 루트 키는 첫 테이블 앞, 테이블 키는 해당 테이블 마지막 줄 뒤. 패치 후 재파싱 검증, 불일치면 저장 거부. 기존 따옴표 스타일 유지, 새 Windows 경로는 literal `'...'`, 여러 줄은 `"""`. **TOML 1.1 전용 구문은 쓰지 않음.** 파싱 오류는 줄·열 + 폼 전환 차단.
- **config.toml:** 계층 배너, 신뢰 배너(`CodexTrustResolver`: 사용자 config `[projects.<key>] trust_level`, 따옴표·구분자·대소문자·`\\?\` 무시, `project_root_markers` 고려, 불확실하면 "확인 불가"). 탭: 일반 / 권한·샌드박스 / MCP 서버 / 기능 플래그 / 에이전트 / 스킬 / **훅(인라인, 추가)** / TUI·기록 / 기타(읽기 전용 트리).
- **훅:** 공용 `HookHandlerModel` + 저장 어댑터 3종(`ClaudeSettingsHookStore`, `CodexHooksJsonStore`, `CodexInlineHookStore`). 원래 표현으로 저장, 변환은 메뉴로만(`CrossFileEdit`: 두 파일 diff·한 백업 세트). 저장 후 "`/hooks`에서 다시 신뢰 필요" 배너. 실행기는 Windows에서 `commandWindows` 우선, 카탈로그 `sample` stdin, "실제 Codex와 셸이 다를 수 있음" 표시.
- **마스킹:** `mcp_servers.*.env`, `http_headers`, `oauth.client_secret`, `model_providers.*.experimental_bearer_token`, 이름에 KEY/TOKEN/SECRET/PASSWORD/PWD/CREDENTIAL. `*_env_var`, `env_vars`, `env_http_headers`(변수 이름)는 가리지 않음.
- **CODEX_HOME:** 실제 홈 모드는 `CODEX_HOME`(절대경로) 또는 `%USERPROFILE%\.codex`. **`--home`/`DOTDECK_HOME` 샌드박스에서는 실제 CODEX_HOME 무시**하고 `<home>\.codex`. 테스트용 `--codex-home` 옵션.
- **런타임 제외 폴더:** `/sessions /log /cache /.sandbox /app-server-control /app-server-daemon /packages /thread-writer-locks /tmp /tui-thread-reference-capabilities`. 숨김·읽기 전용 파일: `*.sqlite*`, `*.lock`, `.sandbox_migration`, `installation_id`, `version.json`, `models_cache.json`, `history.jsonl`, `session_index.jsonl`, `config.toml.bak-*`.
- **rules:** 최상위 `prefix_rule(...)`+주석만일 때 폼, 그 외 "폼 사용 불가: N행 이유". `codex execpolicy check --pretty --rules <file> -- <cmd>`는 미리보기 → 확인 → 셸 없이(`ArgumentList`) 10초 제한.

## 6. 공식 문서 확인 사항 (2026-11-08, codex-cli 0.167.0)
- 문서 주소 `developers.openai.com/codex/*`는 `learn.chatgpt.com/docs/...`로 308 이동. 공식 스키마 `https://developers.openai.com/codex/config-schema.json`(개발 시 대조용, 앱은 네트워크 사용 안 함).
- 지시서와 다른 점: 에이전트 파일 덮어쓰기 불가 키(위 6번) / `experimental_bearer_token`은 `model_providers` 전용 / `max_depth`·`nickname_candidates`·이름 문자 규칙 문서에 없음, 동시 스레드는 `max_concurrent_threads_per_session`(`max_threads`는 예전 이름) / `[profiles.*]`·루트 `profile`은 0.134.0부터 미지원(`<이름>.config.toml` 사용) / `personality` deprecated / `approval_policy` `untrusted` 미지원·`on-failure` deprecated / `~/.codex/skills`는 문서에 없음(실제로는 `.system`만) / `codex_hooks`는 `hooks` 별칭.
- 문서에 없는 것(확인 불가로 처리): 신뢰 경로 키 형식, 훅 신뢰 해시 저장 위치, Windows 훅 셸·환경변수, agents 하위 폴더 탐색, rules 예시 토큰 분리, `[[skills.config]]` path 비교.
- 훅: 이벤트 14종, matcher 무시 이벤트 PreModelRequest/PostModelRequest/UserPromptSubmit/Stop/Interrupt, timeout 기본 600(SessionEnd·Interrupt 기본 1, 최대 3), SessionEnd에는 mcp_tool 불가, exit 2 = 차단.
- 실제 `~/.codex`의 신뢰 키 형식: `[projects.'d:\project\dotdeck']`(작은따옴표, 소문자 드라이브, 역슬래시). `~/.agents` 없음, `CODEX_HOME` 미설정.

## 7. 명령
- 빌드: `dotnet build DotDeck.slnx`
- 테스트: `dotnet test --project tests/DotDeck.Core.Tests`, `dotnet test --project tests/DotDeck.App.Tests` (전체: `dotnet test --solution DotDeck.slnx`)
- 실행: `dotnet run --project src/DotDeck.App -- --home <임시 홈>`
- 배포: `dotnet publish src/DotDeck.App -c Release -r win-x64 -p:PublishSingleFile=true`
- 경고는 오류(`TreatWarningsAsErrors`). xunit.v3 테스트에서 `CancellationToken`이 필요하면 `TestContext.Current.CancellationToken`(xUnit1051).

## 8. 반드시 지킬 규칙
- **실제 `~/.claude`, `~/.claude.json`, `~/.codex`, `~/.agents`, 환경변수, hosts 파일을 수정하지 않는다.** 테스트는 임시 폴더·가짜 구현만, 수동 확인은 `DOTDECK_HOME`/`--home`(+ `--codex-home`).
- 비밀 파일(`.env*`(example 제외), `*.pfx`, `*.pem`, `*.key`, `id_rsa*`, `id_ed25519*`, Codex `auth.json`)은 열지 않는다. 비밀값은 로그·백업 목록·변경 기록·diff 표시에 평문 금지.
- MCP 서버를 실제로 실행·연결하지 않는다.
- **main 직접 커밋 금지, push·PR·태그 금지**(사용자 확인 후에만). force push 금지.
- **새 NuGet은 사용자 승인 후**(승인된 것: Tomlyn, System.Security.Cryptography.ProtectedData). 버전은 `Directory.Packages.props`(CPM)만.
- 모든 쓰기: diff 미리보기 → 백업 → 원자적 쓰기(인코딩·BOM·줄바꿈 보존). 폼 왕복 시 모르는 키·값·주석 보존.
- Core는 WPF 참조 금지, ViewModel은 View 타입 참조 금지. 색은 리소스 키. UI 문구 한국어, 코드·주석 영어.
- 설명·형식 정보는 코드가 아니라 카탈로그 JSON(문서 주소·확인 날짜 포함).
- 커밋: Conventional Commits, 커밋 전 `git diff --staged` 비밀 점검, `.claude/`는 커밋하지 않음. Phase마다 사용자 확인 후 다음 단계.

## 9. 구현 팁
- 저장소는 LF, `core.autocrlf=true`(CRLF 경고는 무해). 편집 시 기존 줄바꿈·BOM 유지.
- WPF: ToggleButton OneWay IsChecked는 클릭 시 바인딩이 깨짐 → Button + Tag 트리거. ContextMenu 안에서는 TargetName 트리거 불가. `Themes/Controls.xaml`의 ContextMenu 템플릿은 ScrollViewer로 감싸져 있음(MaxHeight 초과 잘림 수정).
- JSON 쓰기는 `JavaScriptEncoder.Create(UnicodeRanges.All)`.
- 카탈로그 로딩은 `Catalogs/CatalogResources.cs`(내장 + `%APPDATA%\DotDeck\catalogs\<제품>\<이름>.json` 대체), `ClaudeToolCatalog.Parse`처럼 목록 항목 null·빈 값 검증.

## 10. Codex 진행 기록
- 2026-11-08 단계 0: SPEC §2.1·§6·§7.0·신규 §7.5, DESIGN 확장 설계, CHANGELOG Unreleased에 승인된 구현 범위·확인 지점 반영. 제품 코드·패키지·버전 변경 없음.
- 검증: `dotnet build DotDeck.slnx` 경고 0·오류 0, `dotnet test --solution DotDeck.slnx` 총 677 / 성공 675 / 실패 0 / 건너뜀 2(심볼릭 링크 생성 권한 필요). `git diff --check` 통과. 문서와 HANDOFF의 승인 결정 대조 완료.
- 공식 문서 차이: 이번 단계는 기존 조사 기록의 문서화이며 새로운 외부 문서 검증은 수행하지 않음. 구현 단계별 대조 예정.
