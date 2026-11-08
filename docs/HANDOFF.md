# 인수인계 (2026-11-08) — Claude Code → Codex

## Codex 리뷰 반영 진행 기록 (2026-11-08, 사용자 확정 범위 A→B→C)
- Codex 교차 검증(379d697..HEAD) 지적 5건 모두 반영 결정: P1 #1 파싱 실패 시 평문, P1 #2 링크로 마스킹 우회, P2 #3 rename 충돌 병합, P2 #4 배열 테이블 부모 rename 실패, P2 #5 MCP 혼합 args 타입 변경.
- **A `fix(security)` 완료**: 화면 값 기본 평문. `SecretPresentation.ScreenShareMode`(AppSettings `ScreenShareMode`, 기본 false, 상태줄 버튼·보기 메뉴)일 때만 가림, 공유 모드 파싱 실패 시 문서 전체 가림. 로그 필터용 `CollectSecrets`를 표시와 분리(열기·다시 읽기·저장 시점만 수집, 입력 중 접두어 등록 방지). `SecretFileResolver`가 파일 링크·폴더 링크·정션을 구간별로 끝까지 추적(최대 40홉, 순환·검사 실패는 비밀로 간주). `IFileStore.GetLinkTarget`(기본 구현 = ResolveLinkTarget) 추가. 탐색기 자물쇠(`NodeInfo.FromEntry(store)`·사용자 지정 파일)와 문서 가림이 같은 판정 사용. 설정 대화상자 "비밀값 기본 가림" 제거(옛 키 무시). SPEC §0·§2.3·§6·§7.5·§8 문구 수정.
- 알려진 제한: **하드링크는 일반 파일과 구분할 수 없어 이름으로만 판정**. 공유 모드 상태는 프로세스 전역 static이며 테스트는 `ScreenShareCollection`(병렬 비활성)으로 격리.
- A 검증: Debug `dotnet build DotDeck.slnx` 경고 0·오류 0, `dotnet test --solution DotDeck.slnx` 총 757 / 성공 755 / 실패 0 / 건너뜀 2(심볼릭 링크 권한). 실제 정션 생성 테스트 통과.

## 최신 인수인계 메모 (다음 담당자용, 2026-11-08)

### 현재 완료 상태
- 현재 작업 브랜치: `feat/codex-editors`. Codex 설정 전용 폼 재설계의 승인된 단계 ②~⑤ 구현을 마쳤다.
- 완료 커밋(작업 브랜치에 있음):
  - `e42795a feat(security): 비밀 파일 편집 허용과 기본 가림 및 표시 전환`
  - `a9c1bc2 feat(backups): DPAPI 투명 암호화와 자동 복호화 복원`
  - `f42f233 feat(codex-config): 전용 폼 5개 탭과 카탈로그 및 자동 연결`
  - `ee37a12 feat(codex-config): 고급 테이블 트리와 타입별 무손실 편집`
- 마지막 전체 검증: `dotnet build DotDeck.slnx -c Release` 성공(경고/오류 0), `dotnet test --solution DotDeck.slnx -c Release` 성공(총 739, 성공 737, 실패 0, 기존 심볼릭 링크 권한 이슈로 skip 2). Debug 구성은 당시 실행 중인 앱이 DLL을 잠가 실행하지 못했다. 앱을 강제 종료하지 않았다.
- 현재 git 상태 확인: 작업 변경은 커밋 완료. `?? .claude/`만 미추적 로컬 설정으로 남아 있으므로 열람·수정·추가·커밋하지 않는다. 버전 파일은 변경하지 않았다.
- 새 NuGet 의존성은 추가되지 않았다. ProtectedData 10.0.14는 확인했으나 Windows 타깃 프레임워크에서 제공되는 API이므로 불필요한 패키지 참조를 제거했다. 승인된 Tomlyn은 기존 단계에서 사용 중이다.

### 다음 담당자의 진행 순서
1. 먼저 `CLAUDE.md`, `docs/SPEC.md`, 이 파일의 최신 승인·인수인계 절 및 관련 `docs/` 설계 문서를 읽고, `git status --short`, 브랜치, 최근 커밋을 확인한다. `.claude/`는 사용자 로컬 설정이다.
2. 임시 홈의 fake fixture로 앱을 실행해 시각 확인한다(경로/명령은 아래 “수동 확인용 임시 홈” 참고). 특히 6탭 전환, 비밀 기본 가림 및 눈 표시/편집, MCP args/env 행 관리, 프로젝트 폴더 선택, auth.json 편집·저장·복원, 고급 트리, 문법 오류 시 텍스트 편집을 확인한다. 실사용자 홈·실제 비밀 파일은 읽거나 변경하지 않는다.
3. 확인된 결함과 남은 경계를 해결한다. 주요 후속 후보는 아래 “남은 확인/제한”에 기록되어 있다. 각 독립 단계는 테스트 우선으로 수정하고, 단계별 `dotnet build` → 전체 `dotnet test` → diff 검토 → Conventional Commit → 이 파일의 진행 기록 갱신 순으로 처리한다. 빌드/테스트는 Debug와 Release 중 현재 실행 프로세스가 잠그지 않는 구성을 사용하고, 사용자의 앱 프로세스를 임의로 종료하지 않는다.
4. 기능이 안정되면 릴리스 준비를 진행한다: 최신 SPEC·CHANGELOG·버전 정책 및 저장소 release 절차/스킬을 다시 확인하고, 릴리스 후보의 전체 검증과 publish 산출물을 점검한다. SemVer·태그 지침을 지키며 버전 파일은 사용자 지시 없이 수정하지 않는다. 릴리스 완료 여부와 산출물은 HANDOFF에 기록한다.
5. Git 작업은 `feat/codex-editors`에서만 한다. main에 직접 커밋하거나 merge하지 않는다. **push, PR, 태그 생성/공개 릴리스는 사용자 승인이 필요하다**. 현재 승인된 작업은 구현 및 인수인계까지이며 push/merge/release 승인은 받은 것으로 간주하지 않는다. 승인 전에는 로컬 브랜치와 검증 결과만 준비한다.

### 남은 확인/제한
- 제품 앱을 임시 홈으로 사람이 직접 실행한 시각적 확인은 아직 완료 기록이 없다(자동 STA 리소스/바인딩/Undo 검사는 통과).
- 다른 Windows 계정에서 복원하는 실계정 전환 확인은 하지 않았다. 읽을 수 없는 DPAPI payload에서 “복원 불가” 안내가 나오는 자동 검사는 통과했다.
- `config.toml` 자동 폼 연결은 현재 파일명 fallback 중심이다. 완전한 CODEX_HOME 별칭 및 실제 Codex 경로 기반 역할 판별(기존 2b-1)은 별도 작업이다. 동명 파일을 여는 다른 제품에서 오탐 가능성을 평가한다.
- 앱이 만든 새 파일 백업은 DPAPI(CurrentUser)로 암호화한다. 기존 평문 백업은 자동 변환하지 않는다. Core `BackupStore`의 protector는 선택 인자이므로 새로운 제품 Composition Root가 생기면 DPAPI 구현 주입 여부를 점검한다.
- 프로젝트 ZIP 내보내기는 기존 비밀 파일 제외 정책을 유지한다. 이번 DPAPI는 파일 저장/덮어쓰기/복원 백업에 적용된다.
- 카탈로그는 앱 시작 시 읽는다(실행 중 hot reload 없음). JSON 비밀 미리보기는 표시용 재직렬화이며 원본 편집 버퍼와 분리된다. diff 본문은 기본 전체 가림, 눈 버튼으로 표시한다. 알려진 비밀값 필터는 프로세스 메모리에 등록값을 유지한다.

### 임시 수동 확인 fixture
- 임시 홈: `C:\Users\jwlee\AppData\Local\Temp\DotDeck-codex-review-f79f9e5a`
- 실행: `dotnet run --project src/DotDeck.App -c Release -- --home "C:\Users\jwlee\AppData\Local\Temp\DotDeck-codex-review-f79f9e5a"`
- `.codex/config.toml`에는 fake API_KEY, 주석/프로필/MCP/projects/고급 값이 있고 `.codex/auth.json`에는 가짜 토큰만 들어 있다. 재생성 필요 시 실제 사용자 경로를 사용하지 말고 임시 홈만 쓴다.

### 관련 문서/코드 위치
- 최신 제품 원칙 및 동작 요구: `docs/SPEC.md`의 최신 보안 원칙과 Codex 설정 폼 절, 설계: `docs/DESIGN.md`.
- Core TOML/카탈로그/백업: `src/DotDeck.Core/Toml/TomlEditDocument.cs`, `Codex/ConfigFormCatalog.cs`, `Documents/BackupStore.cs`.
- Codex 폼: `src/DotDeck.App/ViewModels/Editor/CodexConfigFormViewModel.cs`, `AdvancedTomlViewModel.cs`, 대응 `Views/Editor/` XAML.
- 비밀 표시/DPAPI: `Security/SecretPresentation.cs`, `SecretMasker.cs`, `Views/Editor/SecretValueEditor.*`, `Infrastructure/Windows/DpapiBackupProtector.cs`.
- 회귀 테스트: `tests/DotDeck.Core.Tests/`와 `tests/DotDeck.App.Tests/`의 CodexConfigForm, AdvancedToml, SecretPresentation, ProtectedBackup, FormXaml 관련 테스트.

## 최신 사용자 승인 (Codex 전용 폼 재설계)
- 최신 요청이 과거 보안 결정보다 우선한다. 관리 도구인 DotDeck은 비밀 파일·값이라는 이유로 열기·편집·저장을 막지 않는다. auth.json도 자물쇠·기본 마스킹으로 열고 눈으로 즉시 편집한다. 사용자 지정 파일 등록 허용. 로그·출력·diff 요약·백업 목록 평문 금지, diff 본문은 눈으로 표시 가능. DPAPI(CurrentUser) 투명 백업, 다른 계정 복원 실패 안내. 실제 사용자 설정은 개발 중 수정하지 않는다.
- 화면 구성안 승인 완료. 단계별 커밋: ② 원칙/마스킹 → ③ DPAPI → ④ 모델·권한·프로필·MCP·신뢰 전용 탭 → ⑤ 고급 트리. `[profiles.*]`는 요청대로 편집하되 Codex 0.134.0+ 별도 프로필 파일 정책 경고만 표시. 기존 범용 폼 대체.
- 토큰 여유 약 5% 시 검증·커밋 가능한 지점에서 정리하고 다음 에이전트용 인수인계를 작성하라는 요청을 받음.
- 단계 ②: Core 비밀 파일 읽기/저장/등록 및 탐색기 열기 제한 해제, auth.json 분류 추가, 기본 가림 설정, 원본 버퍼와 분리된 텍스트 마스킹·눈 아이콘, 공유 SecretValueEditor(PasswordBox/눈/평문), env 표 연결, diff 기본 가림·눈 표시, 알려진 비밀값 로그 마스킹. 기존 TOML 문서 전체 편집 잠금 제거. DPAPI는 다음 커밋에서 연결한다.
- ② 검증: Release build 경고 0·오류 0, 전체 730 / 성공 728 / 실패 0 / 권한 건너뜀 2. Debug는 사용자 실행 DotDeck PID 23604 DLL 잠금. diff는 초기 구현상 본문 전체를 기본 가림(눈으로 즉시 원문 확인)하며 요약은 개수만 표시한다.
- 단계 ③: IBackupProtector(Core)와 DpapiBackupProtector(App) 연결. 앱 백업은 형식별 탐지 누락 방지를 위해 비밀값 포함 여부와 무관하게 모두 DPAPI(CurrentUser) 암호화. manifest `protected: true`, SHA는 암호문 기준, 원본 길이 별도 보존. 기존 비암호화 백업 읽기 호환. ReadContent에서 자동 복호화하고 복원은 기존 원자적 쓰기로 원본 바이트 그대로 반영. 복호화 실패 시 다른 Windows 계정/손상 안내. 백업 복원 diff도 기본 가림+눈 지원. 파일 덮어쓰기 백업의 비밀 파일 예외도 제거.
- ProtectedData 10.0.14가 NuGet에 존재함을 확인했으나 net10.0-windows에서 이미 제공되는 API라 명시 패키지 참조 시 NU1510(불필요 의존성) 발생. 참조를 제거하고 프레임워크 API로 구현해 패키지를 추가하지 않음. 실제 Windows DPAPI 암호문·auth 읽기/수정/저장·바이트 동일 복원·로그/출력/manifest 문자열 검색 검사 통과. 다른 계정은 실제 계정 전환 없이 읽을 수 없는 payload로 오류 안내 경로를 검증함.
- ③ 최종 검증: Release build 경고 0·오류 0, 전체 732 / 성공 730 / 실패 0 / 권한 건너뜀 2. 기존 평문 백업은 자동 변환하지 않으며 앱이 이후 새로 만드는 파일 백업부터 모두 암호화한다.
- 단계 ④: 전용 CodexConfigFormViewModel과 5개 탭(모델/권한·샌드박스/프로필/MCP/프로젝트 신뢰) 및 `Codex/config-form.json` 설명·허용값 카탈로그 구현. 사용자 카탈로그 덮어쓰기는 기존 CatalogResources를 통해 적용(앱 재시작 시 읽음). config.toml은 자동으로 전용 폼 종류 연결, 열 때 자동 폼 시도·문법 오류 시 줄/열 안내와 텍스트로 유지. 프로필·MCP 추가/삭제/이름 변경, 프로젝트 폴더 선택, args 문자열 목록·env 키값 표/눈, 미등록 값 경고·보존. 모든 변경은 TomlEditDocument를 통해 기존 버퍼에 한 번 Replace하여 Undo 공유. 테이블 rename/delete, 주석을 보존하는 문자열 배열 구조 편집 API 추가.
- ④ 검증: Release build 경고 0·오류 0, 전체 736 / 성공 734 / 실패 0 / 건너뜀 2. API_KEY 문서의 다른 필드·env 편집, 미지 키/주석/CRLF 보존, 프로필·서버 관리, 카탈로그 덮어쓰기, 자동 종류, 실제 STA 5탭 로드와 폼 변경 Ctrl+Z 원본 복원 확인. 다음은 단계 ⑤ 고급 트리.
- 단계 ⑤: AdvancedTomlViewModel/View로 기타(고급) 테이블 트리 추가. 전용 필드 중복 제거, 배열·인라인 값은 단일 노드, 오른쪽 문자열/불리언/숫자·날짜/배열/인라인 컨트롤과 접힌 직접 경로 편집. 혼합 배열도 위치 편집으로 주석 보존. 기본 가림 설정 변경을 현재 SecretValueEditor에도 적용하고 훅 출력도 마스킹. 실제 WPF 6탭/고급 탭 로드 및 폼 변경 Undo 검사 수행.
- ⑤ 최종 검증: `dotnet build DotDeck.slnx -c Release` 경고 0·오류 0, `dotnet test --solution DotDeck.slnx -c Release` 총 739 / 성공 737 / 실패 0 / 기존 심볼릭 링크 권한 건너뜀 2. 인라인 테이블 추가/삭제도 위치 편집으로 미수정 키의 공백·따옴표를 보존하도록 보완. 코드 자체 검토와 테스트를 수행했으며 별도 에이전트 교차 검증은 하지 않음. 임시 홈 준비 완료. 다음 사용자 수동 확인 결과에 따라 UI/경계 사례를 보완한다.

## 다음 에이전트를 위한 최신 인수인계
- 작업 브랜치 `feat/codex-editors`. 사용자 승인된 재설계의 ②·③·④는 각각 e42795a / a9c1bc2 / f42f233 커밋. ⑤는 이 기록과 함께 커밋한다. main 직접 commit/merge/push 안 함. 미추적 `.claude/`는 사용자 로컬 설정이며 건드리지 않음.
- 수동 확인용 임시 홈: `C:\Users\jwlee\AppData\Local\Temp\DotDeck-codex-review-f79f9e5a`. `.codex/config.toml`에는 주석·프로필·가짜 API_KEY MCP env·args 주석·projects·고급 혼합 배열/인라인 예제, `.codex/auth.json`에는 가짜 토큰만 있음. 실제 사용자 홈·환경변수는 수정하지 않음.
- 실행: `dotnet run --project src/DotDeck.App -c Release -- --home "C:\Users\jwlee\AppData\Local\Temp\DotDeck-codex-review-f79f9e5a"`. Debug는 사용자 실행 중인 기존 앱 DLL 잠금으로 실패했으므로 이번 구현 검증은 Release로 진행함. 기존 앱을 강제 종료하지 않음.
- 설계 원칙: 보안 사유 잠금 금지, 마스킹과 눈 표시/편집, DPAPI 투명 백업. 과거 docs/테스트 이름 일부의 "secret files never opened" 문구는 이전 정책의 역사이며 최신 승인 섹션과 SPEC §0이 우선한다. 개발자가 실제 비밀 설정을 조사하지 않는 규칙은 유지.
- 주요 위치: Core/Codex/ConfigFormCatalog.cs + Catalogs/Codex/config-form.json; App/ViewModels/Editor/CodexConfigFormViewModel.cs와 AdvancedTomlViewModel.cs; Views/Editor의 대응 View; SecretValueEditor/SecretPresentation; Core/Documents/IBackupProtector + App/Infrastructure/Windows/DpapiBackupProtector; BackupStore.Protected flag.
- 보호/복원: 앱 Composition Root는 모든 새 파일 백업에 DPAPI 주입. Core 단독 BackupStore는 테스트/다른 호스트용으로 provider가 optional이므로 제품의 추가 Composition Root가 생기면 반드시 주입. 기존 평문 백업 자동 마이그레이션은 하지 않음. 복호화 실패는 IO 오류 안내이며 원본 변경 전에 중단. 다른 Windows 계정 실제 전환 검사는 하지 않았고 실패 payload 안내 경로만 자동 검증함.
- 다음 확인: 사용자 시각적 수동 검사(6탭, 눈·기본 가림 설정, args/env 관리, 프로젝트 폴더 선택, auth 수정·저장·복원). 자동 테스트는 실제 UI 리소스/바인딩과 Undo를 검증하지만 사람이 실행한 제품 스크린샷 검사는 아직 없음.
- 남은 범위/제한: 완전한 CODEX_HOME 별칭/런타임 경로 역할 판별(원래 2b-1)은 별도 후속. 현재 config.toml 자동 연결은 파일명 fallback이며 다른 제품의 동명 파일에는 수동 텍스트 지정 가능. 카탈로그는 앱 시작 시 재로드. 인라인 테이블 항목 추가/삭제도 SetInlineValues 위치 편집을 사용해 미수정 키의 공백·따옴표를 보존한다. JSON 비밀값 마스킹 프리뷰는 표시용 재직렬화이며 원본 버퍼와 분리. diff는 현재 전체 본문 기본 가림(눈 표시), 요약은 개수만. 프로젝트 ZIP 내보내기는 기존 비밀 파일 제외 정책을 유지하며 이번 DPAPI는 저장/덮어쓰기/복원 파일 백업에 적용. 알려진 비밀값 로그 필터는 프로세스 수명 동안 메모리에 등록값을 유지함.

## 1. 현재 상태
- 브랜치 `feat/codex-editors` (main `b3325f5`에서 생성). 단계 0·2a 완료, 사용자 수동 확인 통과. 2b-0 TOML 무손실 편집기 완료.
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
- Codex 설정 폼 작업: 공식 문서 조사, `architect` 설계안 작성, 사용자 결정 1~7 확정(아래). SPEC 7.5·DESIGN 및 2a 공통화 반영 완료. Codex 전용 폼 자체는 아직 구현 전.

## 3. 진행 중 / 남은 작업 (순서대로, 각 커밋마다 빌드 → 테스트 → 교차 검증 → 커밋)
| # | 커밋 메시지 초안 | 주요 파일 | 상태 |
|---|---|---|---|
| 0 | `docs(spec): Codex 전용 편집기 사양과 설계 추가` | `docs/SPEC.md`(§2.1 Codex 행, §7.0 문구, 신규 §7.5), `docs/DESIGN.md` | 완료 |
| 2a-1 | `refactor(editors): 편집기 등록소로 종류별 분기 통합` | 신규 `Core/Editors/Registry/*`, `App/ViewModels/Editor/FormRegistry.cs`; 수정 `EditorKind.cs`, `Roles/FileRoleCatalog.cs`, `Roles/EditorOverrideStore.cs`, `TextDocumentViewModel.cs`, `ShellViewModel.Validation.cs`, `.Roles.cs`, `.cs` | 완료 |
| 2a-2 | `refactor(hooks): 훅 모델·표·실행기를 제품 공용으로 분리` | 신규 `Core/Hooks/*`, `HookTableViewModel.cs`, `Views/Editor/HookTable.xaml`; 수정 `ClaudeSettingsDocument.cs`, `HookRunner.cs`, `SettingsFormViewModel.cs`, `HookTestPaneViewModel.cs`, `FormTemplates.xaml` | 완료 |
| 2a-3 | `refactor(forms): 키-값 표·문자열 목록·frontmatter 프로필 공통화` | `KeyValueTableViewModel.cs`, `StringListViewModel.cs`, `FormViewModels.cs`, `SettingsFormViewModel.cs` | 완료 |
| → | **2a 끝: Claude 편집기 동작이 같은지 사용자 확인** | | |
| 2b-0 | `build(core): Tomlyn 추가와 TOML 무손실 편집기` | `Directory.Packages.props`, `DotDeck.Core.csproj`, 신규 `Core/Toml/TomlEditDocument.cs` + 테스트 | 완료 |
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
4. **재설계 승인으로 변경:** `auth.json` 등 비밀 파일도 열기·편집·등록·DPAPI 백업을 허용하고 자물쇠·기본 마스킹·눈 아이콘으로 표시한다. 런타임 데이터는 기본 숨김, plugins와 skills/.system은 출처를 안내하며 보안 사유로 편집을 잠그지 않는다.
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
- 개발 중 실제 비밀 설정은 조사하지 않으며 가짜 테스트 파일만 사용한다. 제품은 비밀 파일도 마스킹으로 열기·편집·등록·DPAPI 백업할 수 있다. 로그·백업 목록·변경 기록·diff 요약에는 비밀값 평문 금지, diff 본문은 눈으로 표시 가능.
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
- 2026-11-08 2a-1: Core `EditorDescriptor`·`EditorRegistry`, App `FormRegistry` 추가. 저장 이름·라벨·기능·검증·폼 생성·문서/탐색기 메뉴를 연결하고 Claude 전용 호스트 서비스를 `IClaudeFormServices`로 분리. Codex enum·저장 이름은 예약하되 폼·메뉴는 아직 비활성. `ICodexFormServices`는 실제 서비스 추가 시 정의 예정.
- 2a-1 검증: 빌드 경고 0·오류 0, 전체 테스트 695 / 성공 693 / 실패 0 / 건너뜀 2. 신규 18개 사례로 저장 이름 호환·진단·미지 필드 보존·메뉴 명령·미구현 폼 차단 검증. UI 직접 실행은 아직 하지 않음. 새 의존성·버전·실제 설정 변경 없음.
- 2026-11-08 2a-2: 공용 `HookHandlerModel`·`IHookStore`·`ClaudeSettingsHookStore`·`HookRunner`·`HookTestProfile`, `HookTableViewModel`·`HookTable.xaml` 분리. 샘플 생성과 실행 환경은 제품 프로필로 주입. 공유 폼 스타일·메뉴 동작은 `FormControls`로 이동. Codex 저장 어댑터는 2b-4에서 추가한다.
- 2a-2 검증: 빌드 경고 0·오류 0, 전체 테스트 699 / 성공 697 / 실패 0 / 건너뜀 2. 기존 실행기·matcher·MCP 테스트 통과, 새 회귀 검사로 미지 데이터·그룹·정규식 공백 보존, 행 이벤트 해제, 테스트 프로필 주입 확인. STA에서 WPF 테마·공유 훅 표·설정 템플릿을 실제 로드하고 바인딩 검증(제품 Startup 실행 안 함).
- 2026-11-08 2a-3: `KeyValueTableViewModel`·`StringListViewModel`·`FrontmatterProfile` 추가. Claude env 표·에이전트/스킬 도구 목록·제품별 제안/검증을 공용 모델로 연결. 회귀 테스트에서 확인된 기존 env 비문자열 값의 암묵적 문자열 변환도 수정(수정하지 않은 숫자·객체·null 타입 보존).
- 2a-3 최종 검증: 빌드 경고 0·오류 0, 전체 테스트 705 / 성공 703 / 실패 0 / 건너뜀 2. 신규 6개 사례로 도구 목록 단일 버퍼 쓰기·미지 필드·타입 보존·행 이벤트 해제·프로필 주입 확인. 단계 2a 전체 추가 사례는 28개. 코드 자체 검토·회귀 테스트 수행, 별도 에이전트 교차 검증은 수행하지 않음. 공식 문서 관련 새 동작·의존성·버전 변경 없음.

## 11. 2a 수동 확인 준비
- 임시 홈(테스트용 예제만 생성, 실제 사용자 설정은 수정하지 않음): `C:\Users\jwlee\AppData\Local\Temp\DotDeck-2a-review-1ba75241`.
- 저장소 루트에서 실행: `dotnet run --project src/DotDeck.App -- --home "C:\Users\jwlee\AppData\Local\Temp\DotDeck-2a-review-1ba75241"`.
- Claude 전역에서 `settings.json`, `agents/demo-reviewer.md`, `skills/demo-skill/SKILL.md`를 열고 다음 형식 메뉴·폼 전환·권한/훅/env 행 수정·이동·삭제·도구 선택·Undo를 확인한다. 저장 diff와 미지 필드(`demoUnknown`, `demo_unknown`) 보존도 확인한다. 훅 명령은 `echo dotdeck-demo`이며 실행은 앱의 확인 대화상자 후에만 한다.
- 제품 앱을 직접 실행한 시각적 수동 확인은 아직 하지 않았음. 사용자 확인 후 2b-0(Tomlyn 무손실 TOML 시험 구현)로 진행한다.

## 12. 2b-0 진행 기록 (2026-11-08)
- 후속 항목 4: 초기 `TomlFormViewModel` 구조 폼과 XAML 템플릿 연결. 문서/탐색기 "다음 형식으로 열기 → TOML 구조 폼 (Codex 설정)"으로 수동 지정 가능. 값 수정·기존 테이블 키 추가·테이블 생성·키 삭제·빈 테이블 정리 지원. 인라인/배열 원소 삭제 및 인라인 신규 키 추가는 CanExecute로 버튼 비활성화, Core API도 거부. 비밀값 가능성이 있는 문서는 모든 값을 가리고 폼 편집 전체 차단(암호화 백업 구현 전 보호). 새 비밀 키 추가도 버퍼 반영 전 차단. Debug 빌드 경고 0·오류 0, 전체 732 / 성공 730 / 실패 0 / 건너뜀 2. STA 실제 비활성 버튼 검사 및 비정상/비밀 패치 불변 검사 통과.
- 범위 주의: 위 폼은 TOML 구조 편집의 첫 단계이며, 2b-1 경로/역할/런타임 보호와 2b-2 스키마 기반 Codex 탭·신뢰 배너, 2b-3 DPAPI는 아직 미구현이다. 자동 Codex 역할 연결은 기존 계획대로 후속 단계에서 진행한다. 기존 문서의 "새 테이블 생성·삭제 미지원" 제한은 이번 후속 항목 2로 해소됨. 독립 코드 검토와 자동 테스트를 수행했으며 별도 에이전트 교차 검증은 하지 않음.
- 후속 항목 3: 비 UTF-8 TOML(CP949·UTF-16 LE/BE)은 기존 디코더로 표시하되 Snapshot/버퍼 읽기 전용 및 한국어 안내를 전달. 텍스트 입력·폼·찾아 바꾸기 차단, SafeWriter는 명시적 UTF-8 변환을 지정해도 저장 차단. Core 5개 인코딩 사례와 STA 실제 TextEditor 읽기 전용 바인딩·본문 불변 검사 통과. Debug 빌드 경고 0·오류 0, 전체 729 / 성공 727 / 실패 0 / 건너뜀 2.
- 후속 항목 2: 테스트 선작성·미구현 API 빌드 실패 확인 후 `CreateTable`(점 표기·인용 키), `DeleteKey`, `RemoveEmptyTables` 추가. 삭제는 대입문의 전체 물리 줄·인라인 주석·종료 개행을 제거하고 앞의 독립 주석·빈 줄은 보존. 빈 일반 테이블은 헤더 줄만 제거, 배열 테이블 및 하위 테이블이 남은 부모는 보존. 인라인/배열 원소 삭제는 미지원이며 `CanDeleteKey`로 조회 가능. 후보 재파싱·비대상 값 비교 후 적용. Debug 빌드 경고 0·오류 0, 전체 724 / 성공 722 / 건너뜀 2 / 실패 0.
- 후속 항목 1: 사용자가 실행 앱 종료 후 Debug 재검증 요청. `dotnet build DotDeck.slnx` 경고 0·오류 0, `dotnet test --solution DotDeck.slnx` 총 720 / 성공 718 / 실패 0 / 건너뜀 2. DLL 잠금 해소 확인.
- 사용자 2a 수동 확인 완료, 문제 없음. 2b-0 진행 승인 및 바이트 왕복 테스트 선작성 요청을 받음.
- Tomlyn 실시간 NuGet v3 flat-container 버전 목록에서 가장 높은 비프리릴리스 버전 2.10.2 확인. registration/catalog에서도 `listed: true`, 게시일 `2026-11-04T20:44:46.14Z` 확인, 실제 restore 성공. 검색 캐시의 2.10.1보다 실시간 피드를 우선함. 출처: https://api.nuget.org/v3-flatcontainer/tomlyn/index.json 및 https://api.nuget.org/v3/registration5-semver1/tomlyn/2.10.2.json
- `TomlEditDocumentTests`를 먼저 작성하고 미구현 타입으로 빌드 실패 확인 후 구현. 기존 승인에 따라 CPM에 Tomlyn 2.10.2 추가. 패키지의 net10.0 XML API 문서에 따라 `SyntaxParser.Parse(validate: true)` 사용. 전체 문서 직렬화 없이 원본 텍스트의 값 Span만 교체하고 별도 후보 문서를 재파싱·경로 대조 후 적용.
- UTF-8/BOM·LF/CRLF·한글·주석·Windows 작은따옴표 경로·점 키·인라인 테이블·배열 테이블·중첩 배열·여러 줄 문자열·동일 값 이스케이프 보존·파싱 위치·주입 거부 등 신규 15개 사례 통과. 자체 파서 전환 불필요.
- 지원: 기존 값 교체, 루트/기존 명시적 테이블에 키 추가. 제한: UTF-8만 지원하며 잘못된 UTF-8은 거부. 없는 테이블 생성·키 삭제·인라인 테이블 신규 키 추가는 아직 미지원(추정 편집 대신 거부). 신규 문자열은 TOML 1.0 호환 이스케이프를 사용하며 기존 따옴표는 표현 가능한 범위에서 유지.
- 검증: Debug는 사용자 실행 중 DotDeck PID 17900의 DLL 잠금으로 실패. 앱을 종료하지 않고 Release로 검증: `dotnet build DotDeck.slnx -c Release` 경고 0·오류 0, `dotnet test --solution DotDeck.slnx -c Release` 총 720 / 성공 718 / 실패 0 / 건너뜀 2(기존 심볼릭 링크 권한). `git diff --check` 통과. 실제 사용자 설정·환경변수·버전 파일 변경 없음.
- 다음 단계: 2b-1 CODEX_HOME·파일 역할·런타임 보호·스킬 연결 노드. 이번 요청 범위인 2b-0까지 완료.
