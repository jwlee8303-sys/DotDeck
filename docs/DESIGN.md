# DotDeck 설계안 (Phase 0)

> 상태: **승인됨 (2026-11-07, 모든 질문 권장안 채택)**.
> 근거: `docs/SPEC.md`, `CLAUDE.md`. 패키지 버전은 2026-11-07 nuget.org 기준. "확인 필요"는 아직 검증하지 못한 항목이다.

## 확정된 결정 (Q1~Q18 권장안)
| # | 결정 |
|---|---|
| Q1 | 단일 exe 배포. 초기 결정은 framework-dependent였으나 현재 `DotDeck.App.csproj`는 `PublishSingleFile=true`일 때 `SelfContained=true`(런타임 포함)로 게시한다 |
| Q2 | WebView2 런타임이 없으면 안내 문구와 텍스트 보기만 제공 |
| Q3 | `--home`/`DOTDECK_HOME` 지정 시 데이터 폴더도 `<home>\.dotdeck\{roaming,local}`로 격리. `--data <dir>`로 덮어쓰기 가능 |
| Q4 | 프로젝트 간 드래그는 이동, Ctrl은 복사. 다른 볼륨 간에는 확인. Windows 탐색기에서 끌어오면 복사 |
| Q5 | 기본 제공 프로젝트별 기본 제외 목록(`.claude`: projects, todos, shell-snapshots, statsig, file-history, debug / `.codex`: sessions, log). 설정에서 해제 |
| Q6 | "설정·문서만" 필터에 확장자 없는 dotfile, `.gitignore`, `.editorconfig`, csproj, slnx, ini, xml, cmd, bat, sh, psm1 추가 |
| Q7 | 휴지통 복원은 `$I` 파싱, 실패하면 안내 |
| Q8 | 백업 보관은 저장 세트 단위 최신 50개(`BackupStore.Retention`, 1분 유예). 파일별 개수가 아니다 |
| Q9 | 미리보기 탭은 VS 방식. 키보드로 이동할 때도 미리보기(설정, 기본 켬) |
| Q10 | 생성·복사도 Undo 대상. 이력은 세션 한정, 전역 스택 하나 |
| Q11 | (Phase 3) 비밀 환경변수 백업은 DPAPI 암호화 |
| Q12 | (Phase 3) 테스트 모드에서 환경변수·hosts는 Fake/사본 사용 |
| Q13 | VS Code: 설정 → PATH `code.cmd` → 기본 설치 경로. 터미널: `wt.exe` → `pwsh.exe` → `powershell.exe` |
| Q14 | Phase 1에서 시스템 프로젝트는 비활성 노드로 표시 |
| Q15 | `.env.example`, `.env.sample`, `.env.template`는 비밀 파일에서 제외 |
| Q16 | 미리보기 외부 링크는 확인 후 브라우저, 로컬 .md는 탭으로 열기 |
| Q17 | TreatWarningsAsErrors, CPM 도입, DI 패키지 미도입, 휴지통 P/Invoke 승인 |
| Q18 | main 초기 커밋 후 Phase별 `feat/phase1a-shell` 등. 원격·push는 별도 확인 |

---

## 0. 목표 / 비목표

| 목표 (Phase 0~1) | 비목표 (이번 설계 범위 밖) |
|---|---|
| 구조 재구성, 도킹 라이브러리와 미리보기 방식 확정 | 7절 타입별 폼 편집기, 훅 실행기 (Phase 2) |
| Core 인터페이스와 파일 안전 파이프라인 설계 | 8절 환경변수·PATH·중요 파일 쓰기 (Phase 3). 인터페이스 자리만 잡는다 |
| App 셸, 탐색기, 문서 버퍼, 테마 키 체계 | 전역 검색, 라이트 테마, 설정 화면 UI, 배포 (Phase 4) |
| Phase 1a/1b/1c 작업 분해와 테스트 전략 | ACL 수정 (영구 비목표) |

---

## 1. 재구성 계획

**현재 상태**
- `DotDeck.slnx`는 `DotDeck/DotDeck.csproj` 하나만 참조한다. 이 csproj는 net10.0-windows, UseWPF **클래스 라이브러리**(OutputType 없음)이고, 소스는 `Class1.cs`뿐이다.
- git 저장소가 아니다.

**기존 `DotDeck/` 폴더: 삭제한다.** 살릴 코드가 없다. 작업 전에 Visual Studio를 닫는다(`.vs/` 캐시가 옛 프로젝트를 참조).

**절차** (`dotnet sln`의 .slnx 지원은 SDK 9.0.200 이상. 1a에서 실제 동작 확인)
```
dotnet sln DotDeck.slnx remove DotDeck/DotDeck.csproj
Remove-Item -Recurse DotDeck
dotnet new wpf      -n DotDeck.App        -o src/DotDeck.App          -f net10.0
dotnet new classlib -n DotDeck.Core       -o src/DotDeck.Core         -f net10.0
dotnet new xunit    -n DotDeck.Core.Tests -o tests/DotDeck.Core.Tests -f net10.0   # xunit.v3 템플릿 여부 확인 필요
dotnet sln DotDeck.slnx add src/DotDeck.App/DotDeck.App.csproj src/DotDeck.Core/DotDeck.Core.csproj --solution-folder src
dotnet sln DotDeck.slnx add tests/DotDeck.Core.Tests/DotDeck.Core.Tests.csproj --solution-folder tests
dotnet add src/DotDeck.App reference src/DotDeck.Core
dotnet add tests/DotDeck.Core.Tests reference src/DotDeck.Core
```

**프로젝트 설정**
- App: `OutputType=WinExe`, `TargetFramework=net10.0-windows`, `UseWPF=true`, `ApplicationManifest=app.manifest`, `AssemblyName=DotDeck`, `RootNamespace=DotDeck.App`.
- Core: `net10.0`. WPF와 Windows 전용 API는 쓰지 않는다.
- 템플릿이 넣은 `Nullable`, `ImplicitUsings`는 csproj에서 지우고 `Directory.Build.props`로 일원화한다.

**app.manifest**
- `asInvoker`
- supportedOS: Win10/11
- `PerMonitorV2` DPI
- `longPathAware`

**Directory.Build.props 추가 속성**

| 속성 | 값 | 이유 |
|---|---|---|
| ImplicitUsings | enable | 일원화 |
| TreatWarningsAsErrors | true | 신규 프로젝트라 경고 0 유지 비용이 낮다. 전역 규칙 "새 경고 금지" 강제. 패키지 경고는 `WarningsNotAsErrors`에 코드별로만 추가 |
| AnalysisLevel | latest | CA1416 등으로 Core에 Windows API가 새는 것을 탐지 |
| SatelliteResourceLanguages | `en;ko` | 위성 어셈블리를 제거해 exe 크기 축소 |
| IsPackable | false (테스트) | |

- `.editorconfig`를 추가한다.
- 중앙 패키지 관리(`Directory.Packages.props`)를 도입한다. 패키지 승인을 한 파일에서 검토할 수 있다.

---

## 2. 도킹 라이브러리 — **Dirkster.AvalonDock 5.0.1 + Themes.VS2013 5.0.1 (권고)**

| 안 | 장점 | 단점 |
|---|---|---|
| **A. AvalonDock 5.0.1** | net10.0-windows 빌드가 있다(2026-10 릴리스). MVVM 바인딩, 분할·플로팅·자동 숨김, `XmlLayoutSerializer` 내장. VS2013 Dark 리소스 키를 덮어써서 VS2022 색 재현 | VS2022 세부 모양은 템플릿을 직접 수정해야 한다. 같은 문서를 두 그룹에 표시하는 기능과 미리보기 탭은 없다. 5.0 변경점 미확인 |
| B. 자체 구현 | 모양을 완전히 통제 | 드래그 도킹·직렬화를 새로 만들어야 한다(수 주) |
| C. 기타 | Dock(wieslawsoltes)은 Avalonia 전용. 상용 제품은 제외 | — |

- **같은 버퍼를 두 그룹에 표시**: 문서 VM 두 개가 AvalonEdit `TextDocument` 하나를 공유한다(VS "새 창" 방식).
- **1a 스파이크 검증 항목**: ① 다크 테마 ② 레이아웃 저장·복원 ③ 플로팅 후 재도킹. 하나라도 실패하면 4.7x 최신 버전으로 고정한다.

---

## 3. Markdown 미리보기 — **Markdig → WebView2 (권고)**

| 안 | 장점 | 단점 |
|---|---|---|
| **A. WebView2** (1.0.4314.35) | GFM 렌더링 충실도. CSS로 테마 적용. JS로 스크롤 동기화(`data-line`) | 런타임 필요(Win11은 기본 탑재). airspace 문제(자동 숨김 패널이 뒤에 가려짐). 탭 전환 시 컨트롤 재생성 |
| B. FlowDocument (Markdig.Wpf) | 순수 WPF, airspace 없음 | Markdig.Wpf는 2021년 이후 중단. 표·코드·이미지 지원이 약하고 스크롤 동기화가 어렵다 |

**보안·오프라인 설정**
1. Markdig `UseAdvancedExtensions()` + `DisableHtml()`. 원본 HTML과 스크립트를 제거한다.
2. 로컬 이미지는 `SetVirtualHostNameToFolderMapping`으로 문서 폴더만 연다. 큰 문서를 위해 `NavigateToString` 대신 가상 호스트의 임시 HTML을 쓴다.
3. 가상 호스트 외 요청은 모두 차단한다(`WebResourceRequested`). 외부 탐색과 `NewWindowRequested`는 취소한다.
4. DevTools, 컨텍스트 메뉴, 자동 채우기, 확대를 끈다. CSP: `default-src 'none'; img-src md.dotdeck.local; style-src 'unsafe-inline'`. 주입 스크립트와 CSP의 상호작용은 확인 필요.
5. 사용자 데이터 폴더는 `%LOCALAPPDATA%\DotDeck\WebView2`로 둔다.
6. 런타임이 없으면 안내 문구를 보여주고 텍스트 보기만 제공한다(대체 렌더러 없음).
7. 단일 exe에서 WebView2Loader를 처리하는 방식은 Phase 4에서 검증한다.

---

## 4. Core 아키텍처 (`src/DotDeck.Core`, net10.0)

- Windows 전용 구현(휴지통, 레지스트리, `WM_SETTINGCHANGE`, 셸 실행)은 `src/DotDeck.App/Infrastructure/Windows/`에 둔다.
- 플랫폼 중립 구현(`PhysicalFileStore`, `SafeWriter`, FSW 래퍼)은 Core에 둔다.

| 영역 | 타입 | 책임 |
|---|---|---|
| 추상화 | `IFileStore` / `PhysicalFileStore` | 열거·읽기·쓰기·이동·복사·속성 |
| | `IEnvironmentStore` | 사용자·시스템 환경변수. Phase 1은 읽기만. 구현 `RegistryEnvironmentStore`(App), `FakeEnvironmentStore`(테스트) |
| | `IRecycleBin` | `Recycle(paths) → RecycleTicket[]`, `TryRestore(ticket)` |
| | `TimeProvider`(.NET 내장), `IFileWatcher(Factory)`, `IAppLog` | 시간(구현 중 `IClock` 대신 내장 `TimeProvider` 채택, 테스트는 자체 `FakeTimeProvider`), 디바운스 FSW(overflow 시 재스캔), 파일 로그(14일 보관) |
| | `IProcessEnvironment` (`DotDeck.Core.Platform`) | 프로세스 환경변수·특수 폴더·작업 폴더 읽기 (경로 해석용) |
| 경로 | `AppPaths` / `AppPathsResolver` | 우선순위 `--home` > `DOTDECK_HOME` > `%USERPROFILE%`. Home, Roaming·Local 데이터 폴더 계산 |
| 프로젝트 | `ProjectDefinition`, `ProjectRegistry` | projects.json 로드·저장(SafeWriter 사용), 스키마 버전 관리, 손상 시 `.corrupt-<ts>`로 보존. 기본 제공 프로젝트 삭제 거부 |
| 소스 | `IProjectSource` → `FolderProjectSource`, `SystemProjectSource` | 노드 자식 열거. 시스템은 가상 노드 |
| 트리 | `NodeInfo`, `ExplorerFilter`, `FileKindClassifier`, `ExcludedFolderPolicy`, `SecretFilePolicy` | 노드 모델, 표시 필터, 제외 폴더, 비밀 파일 판별 (1b-2 구현 세부: 아래) |
| 파일 작업 | `FileOperationService`, `FileOperationHistory` | 생성·이름 변경·이동·복사·복제·휴지통 삭제. 이름 유효성 검사. Undo 스택(세션, 상한 100). Undo 전 사전조건 검사 |
| 읽기 | `TextFileReader` → `TextFileSnapshot` | BOM → 엄격 UTF-8 → cp949 순 감지. 줄바꿈, 바이너리(앞 8KB NUL), 1MB 플래그, 해시, mtime |
| 안전 쓰기 | `SafeWriter` | `Prepare → WritePlan(diff)` → `Commit`: 외부 변경 검사 → 백업 → 같은 폴더 임시 파일 → `File.Replace`. 원래 인코딩으로 표현 못 하는 문자 경고. 변경 없이 저장하면 바이트가 동일해야 함 |
| 백업 | `BackupStore` | `backups\<ts>[-n]\<드라이브>\<경로>` + `manifest.json`. 목록·복원(복원 전 현재본 백업). 보관 정책 |
| Diff | `TextDiffService` | DiffPlex 줄 단위 diff 모델 |
| 외부 변경 | `ExternalChangeMonitor` | FSW와 창 활성화 시 재확인. 자기 쓰기는 해시로 무시 |
| 마스킹 | `SecretMasker` | `KEY|TOKEN|SECRET|PASSWORD|PWD|CREDENTIAL`. 로그에 파일 본문과 환경변수 값을 남기지 않음 |
| 설정 | `AppSettings`, `SettingsStore` | `%APPDATA%\DotDeck\settings.json` |

**트리 규칙 (1b-2 구현 중 확정)**
- 제외 폴더 항목: 이름만 쓰면 모든 깊이에 적용(`node_modules`), `/`로 시작하거나 중간에 구분자를 포함하면 루트 기준 경로(`/projects`, `src/gen`). 끝에만 붙은 구분자(`debug/`)는 .gitignore처럼 이름으로 본다. 대소문자 무시.
  - 기본값: SPEC 공통 목록. 기본 제공 프로젝트는 공통 목록에 Q5 목록을 루트 기준으로 추가해, `skills\x\debug` 같은 하위 폴더는 숨기지 않는다. `excludedFolders`를 지정하면 기본값 전체를 대체한다.
- 비밀 판별: 파일 이름만 보고 대소문자를 무시한다. `.env*` 중 `.env.<…>.example|sample|template`은 제외(Q15). `id_rsa*` 패턴에 따라 `id_rsa.pub`도 비밀로 본다.
- "설정·문서만" 필터: SPEC 목록, Q6 추가분, 다른 `.`이 없는 dotfile, `.env*`를 표시한다. `.env*`는 자물쇠 표시가 보이도록 남긴다. 폴더는 항상 표시한다.
- 정렬: 폴더 먼저, 이름은 대소문자 무시.

**파일 작업 규칙 (1b-5 구현 중 확정)**
- 모든 작업을 기본 단계 세 가지(이동, 휴지통 보내기, 휴지통에서 복원)로 표현한다. 각 단계는 실행하면 역단계를 돌려준다. 실행 취소와 다시 실행은 같은 방식으로 역단계를 실행한다. 생성·복사의 역단계는 "휴지통으로 보내기"이므로, 생성을 취소한 뒤 다시 실행하면 같은 항목이 복원된다.
- 실행 전에 모든 단계의 사전조건을 검사한다. 하나라도 실패하면 아무것도 하지 않고 이력 항목도 그대로 둔다. 실행 중 실패하면 처리된 부분만 반대쪽 스택으로 옮기고, 나머지는 원래 스택에 남긴다.
- 덮어쓰기는 하지 않는다. 대소문자만 다른 이름 바꾸기는 허용한다. 복사·복제할 때 같은 이름이 있으면 "이름 - 복사본", "이름 - 복사본 (2)" 순으로 이름을 정한다.
- 영구 삭제는 하지 않는다. 휴지통이 없는 위치(네트워크, 이동식 드라이브, UNC)는 삭제와 "생성 취소"를 모두 거부한다. 셸이 그래도 영구 삭제하려 하면 `FOF_WANTNUKEWARNING`으로 확인을 받는다. 휴지통에 보낸 항목의 `$I` 위치를 찾지 못하면 경고하고, 그 항목은 실행 취소할 수 없다.
- 폴더를 다른 드라이브로 옮기는 것은 지원하지 않는다(복사 후 삭제를 안내). 파일은 옮길 수 있다.
- 이름 검사: Windows 금지 문자와 제어 문자, 끝의 마침표·공백, 예약어(`CON`, `NUL.txt`, `COM1`~`COM9`, `LPT1`~`LPT9`, 위첨자 숫자 포함), 255자 초과.
- 휴지통 항목 매칭: 삭제 직전에 `$I` 목록을 스냅샷하고, 삭제 후 새로 생긴 `$I` 중 원래 경로와 종류가 같은 것이 정확히 하나일 때만 복원할 수 있다고 본다.
- 보류(후속 강화): (1) 셸의 영구 삭제 확인에 대한 자동 거부(IFileOperation 진행 싱크 필요). (2) 단계가 경로로만 항목을 식별하는 문제(파일 ID 미기록). 외부 프로그램이 같은 경로를 바꿔 놓으면 그 항목이 이동되거나 휴지통으로 가지만, 덮어쓰지 않으니 복구할 수 있다. (3) 폴더 복사에서 대상 확인과 생성 사이의 아주 짧은 경합.

**안전 저장 규칙 (1c-1·1c-2 구현 중 확정)**
- 인코딩: BOM(UTF-8, UTF-16 LE/BE, UTF-32 LE) → 엄격 UTF-8 → 엄격 CP949 → CP949 대체 디코드(`HasDecodingErrors`, 인코딩을 직접 고르기 전에는 저장 차단). UTF-32 BE는 미지원(바이너리로 거부).
- 텍스트가 같고 인코딩이 같으면 저장은 아무것도 쓰지 않는다(디스크가 바뀌었으면 외부 변경으로 처리).
- 외부 변경은 SHA-256으로 판단한다. 백업 후 교체 직전에 한 번 더 확인한다. 두 번째 확인과 `File.Replace` 사이의 아주 짧은 경합은 보류(File.Replace로는 배타 잠금 불가).
- 비밀 파일 검사는 이름과 링크 최종 대상 모두에 적용한다(읽기·백업·저장·복원). 연 핸들 기준 검증은 보류.
- 백업 정리는 보관 개수를 넘는 세트 중 1분보다 오래된 것만 지운다(진행 중인 다른 저장 보호).

**편집기 탭 규칙 (1c-3 구현 중 확정)**
- 탭 고정은 VM에서 구현했다(닫기 버튼 숨김, 핀 표시, 미리보기 대상 제외). 고정 탭을 앞으로 정렬하지는 않는다.
- 미리보기 요청은 순번을 매겨, 늦게 끝난 이전 요청이 새 미리보기를 대체하지 않게 한다. 읽기 후 같은 파일 탭이 생겼으면 그 탭을 활성화한다.
- 저장은 버퍼별로 직렬화한다. 저장 중 텍스트가 바뀌면(입력·Undo) Undo 원본 표시를 버리고 dirty로 둔다. 저장 중에는 마지막 탭을 닫지 않는다.
- 종료 확인은 dirty 버퍼가 없어질 때까지 반복하고, "버리기"는 답한 시점의 변경 횟수에만 유효하다.
- 외부 변경(1c-4): 창 활성화 시에는 크기·수정 시각이 같으면 변경 없음으로 보고, 파일 감시 이벤트 뒤에는 항상 해시를 비교한다. 다른 대화상자가 열려 있으면 확인을 미루고 다음 활성화 때 다시 한다. 편집 중 파일은 디스크 버전마다 한 번만 묻는다("나중에"/"내 내용 유지"는 그 해시를 기억). 삭제는 상태줄로만 알린다.
- 다시 불러오기는 사용자가 비교 화면에서 본 텍스트의 변경 횟수와 현재가 같을 때만 적용한다(그 사이 입력이 있으면 취소). 백업 복원 중 입력이 있으면 디스크만 복원하고 편집 내용은 dirty로 남긴다.
- 모두 저장은 검토한 계획(텍스트)을 그대로 커밋한다.
- 보류: 백업 복원 대화상자의 백업 읽기·diff 계산은 UI 스레드에서 한다.
- 보류: 찾기·바꾸기는 UI 스레드에서 실행한다(정규식 매치별 2초 제한, 큰 파일은 열기 전 경고). 편집기 색은 뷰 생성 시 리소스에서 읽으므로 런타임 테마 전환은 Phase 4에서 다룬다.
**Markdown 미리보기 규칙 (1c-5 구현 중 확정)**
- 페이지는 Core가 만들어 `%LOCALAPPDATA%\DotDeck\preview\<guid>.html`에 쓰고 `https://app.dotdeck.local/`(폴더 매핑, Deny)로 띄운다. 시작 시 남은 페이지를 지운다.
- 문서 폴더는 가상 호스트로 매핑하지 않는다. `md.dotdeck.local` 요청은 `WebResourceRequested`에서 DotDeck이 직접 응답한다: 래스터 이미지 확장자, 폴더 안, 비밀 파일 이름 아님, 파일·상위 폴더가 링크 아님, 읽은 바이트의 이미지 서명 일치(교체 경합·하드 링크 방지). 그 외 요청은 모두 403.
- 스크립트 설정은 켜 두되 페이지 CSP가 페이지 스크립트를 막는다. DotDeck의 `ExecuteScriptAsync`(스크롤 위치 저장·복원)만 실행된다. base href가 md 호스트를 가리키므로 페이지 안 앵커 링크는 탐색 차단으로 동작하지 않는다.
- 렌더는 버전 번호로 비교해 늦게 끝난 이전 렌더가 새 페이지를 덮어쓰지 않게 하고, 파일 쓰기는 직렬화한다.
- 기각: 문서 폴더 자체가 정션인 경우의 차단(사용자가 직접 연 문서의 폴더이므로 허용).
- 보류: 단일 exe 배포 시 WebView2Loader 동작 확인은 Phase 4. 미리보기를 숨겨도 WebView는 탭이 언로드될 때까지 유지한다.
**레이아웃·세션 복원 규칙 (1c-6 구현 중 확정)**
- 시작 순서: 분리된 탐색기 패널 준비 → session.json의 편집기 탭 복원(파일 읽기는 백그라운드) → `last.xml` 복원 → 프로젝트 초기화. 복원 동안 탐색기가 비어 있으므로 사용자가 같은 파일을 여는 경합은 없다(Codex 지적 기각 사유).
- 탭은 저장된 보기 번호로 만든다(ContentId `doc:<경로>#<n>` 일치). 이후 새 보기는 가장 큰 번호 다음부터. 보기 번호 상한 9999.
- 종료 시 저장 전 diff 확인을 마쳤으므로 복원 대상은 모두 저장된 상태다. 미리보기 탭은 하나만 복원한다.
- 복원이 끝나기 전에 창을 닫으면 레이아웃·세션을 저장하지 않는다(다음 시작에 그대로 복원). 복원 중에는 레이아웃 메뉴 명령을 무시한다.
- 이름 붙인 레이아웃은 도킹 배치만 담는다. 불러올 때 열려 있지 않은 탭은 건너뛰고, 레이아웃에 없는 열린 탭은 AvalonDock이 다시 넣는다.

## Phase 2 편집기 규칙
- 폼은 파싱 모델을 고친 뒤 전체 텍스트를 버퍼에 한 번에 반영한다(버퍼가 단일 원본 → 저장·diff·undo 동일). settings.json은 폼 편집 시 2칸 들여쓰기로 다시 직렬화된다.
- settings 폼은 permissions·hooks·env만 다루고 나머지 키·모르는 필드·표시 불가 훅 데이터는 보존한다. 섹션이 객체가 아니면 폼을 열지 않는다.
- 훅 테스트는 사용자 확인 후 `cmd /d /s /c`로 실행, 제한 시간은 프로세스 시작부터 stdin 쓰기까지 포함, 취소·초과 시 프로세스 트리 종료.

## Phase 3 시스템 프로젝트 규칙
- 환경변수 저장소 `IEnvironmentStore`: 실제 실행은 레지스트리(`RegistryEnvironmentStore`, REG_EXPAND_SZ 유지, 적용 후 WM_SETTINGCHANGE), 샌드박스(`--home`)와 테스트는 `<home>\.dotdeck-environment.json`(`FileEnvironmentStore`). 샌드박스에서는 hosts 등 중요 파일 기본값도 홈 아래 경로를 쓴다.
- 적용 흐름: 연 시점 값 기준으로 변경 계산 → diff 확인 → 현재 값과 충돌 검사(다르면 중단) → 변경 기록(`backups\env-*.json`)을 먼저 저장 → 쓰기. 중간 실패 시 기록을 실제 적용분으로 줄인다.
- 이름에 KEY/TOKEN/SECRET/PASSWORD/PWD/CREDENTIAL이 있으면 화면·diff·기록 모두 마스킹. 따라서 비밀 변수는 되돌리기 대상에서 빠진다.
- 중요 파일 목록: `%APPDATA%\DotDeck\important-files.json`(추가/제거 목록). ACL은 보기만 하고 수정 기능은 없다.

## Phase 4 규칙
- 테마: `Themes/Dark.xaml`·`Light.xaml`은 같은 키를 정의하고 `ThemeManager.Apply`가 팔레트 사전([0])만 바꾼다. AvalonDock은 VS2022 Dark/Light 테마, 제목 표시줄은 DWM 다크 모드 플래그를 테마에 맞춘다. 편집기 글꼴 크기는 앱 리소스 `DD.FontSize.Editor`를 덮어쓴다.
- 전체 검색: `GlobalSearch`가 프로젝트 루트를 순회(링크·제외 폴더·비밀·NUL 포함·2MB 초과 파일 제외), 최대 2000건.
- 배포: `PublishSingleFile=true`일 때 자체 포함 + 네이티브 라이브러리 포함 + 임베디드 PDB로 `DotDeck.exe` 하나만 만든다.

## 사용자 요청 반영(Phase 4 이후)
- 단일 클릭 미리보기 제거: 파일은 더블클릭·Enter로만 연다.
- 보이는 항목: 아래 "탐색기 보이는 항목·보기 방식"으로 대체(`VisibleItems`·`filter`는 v1 마이그레이션에서만 읽음).
- 백업 폴더: `settings.json`의 `BackupDirectory`(없으면 기본 경로). 바꾸면 이후 백업부터 새 폴더에 저장하고 기존 백업은 옮기지 않는다. 환경변수 변경 기록도 같은 폴더를 쓴다.
- 프로젝트 백업: `ProjectBackup.CreateZip`이 선택한 프로젝트를 `DotDeck-projects-<시각>.zip` 하나로 저장(비밀 파일·링크·제외 폴더·출력 zip 자신 제외).

## 탐색기 보이는 항목·보기 방식 (SPEC 2.3)
- Core `Explorer/Visibility/`: `VisibilitySettings`(Rules: 상대 키 → Include/Exclude, `DefaultExtensions`(null = 전역 기본값), `ShowAllFiles`, 모르는 키 `Extra`), `ProjectViewSettings`(Mode 기본 Compact, FlatSort, FlatOrder, Pins). 메모리 키는 `\` 구분·루트 `""`·대소문자 무시(`RelativeKey`), 저장은 `/`.
- `VisibilityEvaluator`: 파일 ① 자기 규칙 ② 확장자(ShowAllFiles 아니면) ③ 가장 가까운 조상 폴더 규칙 ④ 보임. 폴더 상태 = 자기/조상 규칙(없으면 Include). 체크 상태: 자기 상태 + 아래에 반대 규칙이 있으면 Partial. 확장자 때문에만 숨는 파일은 Partial을 만들지 않는다. 생성 시 규칙 아래 플래그를 미리 계산해 판정은 사전 조회와 부모 따라가기만 한다.
- `VisibilityEditor`: 폴더 토글 = 아래 규칙 삭제 후 상속값과 다르면 자기 규칙, 파일 토글 = 상속값과 다르면 규칙. `PreviewFolder`가 지워질 "직접 체크한 파일" 수를 센다.
- 보기 방식은 `VisibleProjectSource`(IProjectSource 래퍼)로 구현해 `ExplorerTreeBuilder`·확장 키를 그대로 쓴다. 압축 행의 키는 사슬 마지막 폴더(`NodeInfo.Chain`), 평면 행은 `FlatDirectory`, 고정은 `IsPinned`. "보일 것이 있는 폴더" 판정은 첫 발견에서 멈추고, 아래에 Include가 없는 Exclude 폴더(`IsSubtreeDead`)는 열지 않으며, 항목 예산(20만)을 넘으면 내용이 있다고 보고 상태줄에 알린다. 인스턴스는 한 번의 빌드에만 쓴다(캐시).
- 편집 모드는 원본 `FolderProjectSource`(제외 폴더만 빠짐)를 쓰고 체크 상태만 계산한다. 펼친 폴더 목록에서 사라진 키를 지우는 일은 원본 목록을 볼 때만 한다(필터된 목록에서 숨은 폴더의 펼침 상태를 잃지 않도록).
- 파일 작업 결과 `FileOperationResult.Changes`(`PathChange` Moved/Removed/Created, 실행 취소·다시 실행 포함)를 `ExplorerPathRewriter`가 규칙·고정·평면 순서에 반영하고, `VisibilityAutoInclude`가 새 항목을 보이게 한다. App은 모든 폴더 프로젝트에 `ProjectRegistry.UpdateAll` 한 번으로 저장하고 열린 편집 세션에도 같은 변경을 적용한다.
- 붙여넣기: `PasteTargetResolver`(선택 → 대상 폴더) → `FileOperationService.PlanPaste`(자기·하위 폴더 금지, 다른 드라이브 폴더 이동 금지, 충돌 분류) → 충돌마다 덮어쓰기/둘 다 유지/건너뛰기 → `Paste`(실행 직전 재검증, 덮어쓰기는 파일만: 백업 → 휴지통 → 복사/이동, 기록 1건).
- `projects.json` schemaVersion 2. v1은 읽을 때 `.v1-<시각>.bak`으로 보존 후 변환(쓰기는 다음 변경 때). 보존하지 못하면 읽기 전용으로 연다. 편집 모드 종료 저장이 실패하면 편집 모드를 유지하고, 앱 종료 때는 레지스트리 게이트(`TryRunNow`, 5초)를 거쳐 저장한다. 잘못된 `visibility`·`view` 값을 가진 항목은 원문 그대로 보존하고 불러오지 않는다.
- 결정(설계 질문 기본값): 시스템 프로젝트·사용자 지정 파일 제외 / 기본 제공 Claude·Codex 포함(v1 `all` → ShowAllFiles) / 제외 폴더 우선 / 자기 Include 폴더는 비어 있어도 표시, 압축 사슬은 그 폴더에서 끝 / 고정 항목은 원래 자리에서 빠지고 고정 파일은 규칙 무시 / 폴더 충돌은 덮어쓰기 불가 / 편집 모드는 끝낼 때·앱 종료 때 저장 / 삭제하면 규칙도 지우고 실행 취소는 항목만 자동 포함(아래 규칙은 복원 안 함) / 다른 프로젝트로 이동하면 원본 프로젝트 규칙은 지우고 대상은 자동 포함 / 앱 밖에서 바꾼 이름의 고아 규칙은 자동 정리하지 않음.
- 사용자 지정 파일(폴더 프로젝트): `ProjectDefinition.CustomFiles`(전체 경로, 대소문자 무시 중복 제거) + `VisibilitySettings.HideCustomFiles`. 노드는 `CustomFilesProjectSource`(다른 소스를 감싸 맨 앞에 노드 추가)로 만들고 id는 `::custom-files`(':'는 파일 이름에 못 쓰므로 상대 키와 겹치지 않음). 목록 파일 노드는 `IsCustomFile`, `RelativePath = null`, 키는 전체 경로라 보이는 항목 규칙·체크박스 대상이 아니다. 일반 모드에서 숨김이면 감싸지 않고, 편집 모드는 항상 감싼다. 등록 검증은 시스템 프로젝트와 같은 `CustomFileRules.Validate`. 경로 추적은 `CustomFileRules.Rewrite`(Moved만, 삭제는 목록 유지)로 표시 규칙과 같은 `UpdateAll` 변경에 넣는다. 시스템 프로젝트 목록은 `important-files.json`을 그대로 쓰고 경로를 따라가지 않는다(기존 동작).
- 평면 보기 사용자 순서: 같은 프로젝트의 평면 파일을 다른 평면 파일 위에 끌어 놓으면 이동이 아니라 순서 변경(Ctrl을 누르면 복사). 이름순일 때는 기존처럼 그 파일의 폴더로 이동.

## Claude 파일 역할 판별 (SPEC 7.0, Phase 4 이후)
- `Core/Roles/FileRoleResolver`: 입력은 절대경로, 출력은 `FileRole`(역할·범위·`.claude` 폴더·기준 루트·편집기·규칙 id·안내). 판별 순서: 관리형 폴더(샌드박스에서는 끔) → 가장 가까운 `.claude`(그 폴더의 규칙만 적용, 부모가 홈이면 전역) → 홈 바로 아래 파일 → 앵커 폴더(`.claude`·`.git`이 있거나 등록된 폴더 프로젝트 루트) 파일 → 모든 폴더 규칙. 파일 시스템은 앵커 확인(`DirectoryExists`)에만 쓴다.
- 규칙 데이터 `Core/Catalogs/<제품>/file-roles.json`(EmbeddedResource). 규칙 = `{id, base(configDir|home|anchorDir|anyDir|managedDir), pattern(* ** /), role, editor, scope(auto|global|project|local|subdirectory|managed), onlyIn, notes}`. 제품 단위로 카탈로그를 추가하면 Codex도 같은 방식으로 판별한다. `CatalogResources.Load`는 `%APPDATA%\DotDeck\catalogs\<제품>\<이름>.json`이 있고 올바르면 그것을, 아니면 내장본과 경고를 돌려준다.
- 범위별 키 경고 `settings-keys.json`(그룹 = 키 목록 + `ignoredIn`/`appliesIn` + 선택 `values` + 메시지). 검증 패널과 settings 폼 상단에 표시.
- 수동 지정 `EditorOverrideStore`(`%APPDATA%\DotDeck\editor-overrides.json`, 경로 → `text|markdown|settingsForm|agentForm|skillForm`, 모르는 값은 보존). 문서의 `Kind`는 바꿀 수 있고, 바꾸면 폼을 다시 만든다. 수동 지정 후 폼 해석에 실패하면 `FormError`에 이유를 표시하고 텍스트로 남는다.
- 훅 스크립트 검증 `HookScriptResolver`: 프로젝트·로컬은 `$CLAUDE_PROJECT_DIR`·상대 경로를 프로젝트 루트로(상대 경로는 "추정"), `~`·`$HOME`·`%USERPROFILE%`는 홈, Git Bash `/c/..` 변환. 전역의 `$CLAUDE_PROJECT_DIR`·상대 경로와 모르는 변수는 "확인 불가"(Info).
- 제한: 경로 이름만 비교(링크·정션 대상은 따라가지 않음), `CLAUDE_CONFIG_DIR`로 옮긴 설정 폴더는 인식하지 않음, 플러그인 폴더(`~/.claude/plugins/**`) 안 파일은 판별하지 않음, 수동 지정은 파일 이름 바꾸기·이동을 따라가지 않음.

### settings.json 권한·훅 폼 (Part B)
- 카탈로그 `Catalogs/Claude/tools.json`(도구 이름·한국어 설명·인수 종류 `command|path|domain|agent|none|unverified|notMatched`·인수 도움말(placeholder, 설명, 예시 2~3개, `{slashBase}` 치환)·함께 적용되는 도구 `alsoCovers`, MCP 도움말)과 `hook-events.json`(33개 이벤트: 실행 시점·matcher 종류 `tools|values|none`·대상·값 목록·`pipeOnly`·할 수 있는 것·예시). 둘 다 `CatalogResources.Load`로 덮어쓰기 가능.
- `PermissionRule`: `^(도구)(\((인수)\))?$`로 나눈다. 폼 행은 원문(`Raw`)을 들고 있다가 도구·인수를 고치지 않으면 원문 그대로 저장한다. 형식이 맞지 않으면 한 칸 텍스트로 보여 주고 그대로 저장한다. 검사: 괄호가 붙은 `mcp__`, allow의 `mcp__*`·도구 이름 `*`, 일치하지 않는 `Write/Glob/Grep(...)`, Read/Edit의 Windows 경로, 목록에 없는 도구(정보).
- `HookMatcher`: 비움·`*` = 전체, 영문·숫자·`_-`·공백·`,|`만 있으면 이름 목록, 그 밖은 정규식(앞뒤 고정 없음). 정규식 항목(`mcp__서버__.*`)이 섞인 목록은 `^(A|B|mcp__서버__.*)$`로 써서 다른 도구가 함께 걸리지 않게 하고, 읽을 때 같은 형식을 다시 체크 상태로 되돌린다. 정규식 검사는 .NET 문법(경고만).
- 이벤트가 matcher를 쓰지 않으면 ☰ 버튼을 끄고 입력칸을 흐리게 표시하되 값은 지우지 않는다(검증 패널에 정보).
- `McpServerDiscovery`: `~/.claude.json`(최상위 `mcpServers` = 사용자, `projects[루트].mcpServers` = 로컬), `<루트>/.mcp.json`(프로젝트), `~/.claude/plugins/cache/<마켓>/<플러그인>/<버전>/`의 `.mcp.json`·`.claude-plugin/plugin.json`(이름 `plugin_<플러그인>_<서버>`), 관리형 `managed-mcp.json`(샌드박스 홈에서는 읽지 않음). `JsonDocument`로 `mcpServers`의 속성 이름만 읽고 값은 보지 않는다. 문제는 경로만 보고(내용 인용 없음). 서버는 실행·연결하지 않는다. 폼이 열릴 때와 새로고침 버튼으로 스레드 풀에서 읽는다.
- 선택한 행(키보드 포커스)의 도움말은 속성 창 상단 "도움말"에 표시(`ITextDocumentHost.ShowFormHelp`).
- 저장 시 `type`이 command가 아닌 훅에는 `command` 키를 새로 만들지 않는다.

### 권한 규칙 인수 형식·경고 (tools.json schemaVersion 2)
- `argumentKinds.<키>`: `label`, `input`(`text|prefixed|choice|disabled|mcp`), `prefix`(domain:), `choices`(Agent 기본 Explore·Plan), `separators`(복합 명령), `argumentWarning`, placeholder·help·examples. 도구: `parameters`·`excludedParameters`(매개변수 규칙 판정), 도구별 `separators`(PowerShell). `parameterRules`(도움말), `messages`(경고 문구, `{rule}` 등 치환). 필수 메시지 키가 없거나 v1이면 내장 카탈로그로 대체(로그 경고).
- 매개변수 규칙은 인수가 `이름:값`이고 이름이 그 도구의 `parameters`/`excludedParameters`에 있을 때만 인식한다(`Bash(git:*)`, `WebFetch(domain:x)`는 일반 인수).
- `McpRuleParts`: `mcp__서버[__도구]`를 찾은 서버 이름(긴 것 우선, `__` 포함 가능)으로 먼저 나누고, 없으면 첫 `__`에서 나눈다. 폼의 MCP 행은 도구 칸이 원본이고 범위·도구 이름은 그 위의 보기다.
- `PermissionLint.Check(규칙 목록)`: 행 경고와 검증 패널이 같은 결과를 쓴다. 중복·충돌은 두 행 모두 표시(`OtherIndex`), 패널에는 한 번. Read deny는 같은 경로의 Edit·Write도 막으므로(공식 문서) 요청의 "Read deny만" 경고 대신 "Edit deny만 있고 같은 경로(또는 전체) Read deny 없음 → 읽기는 막혀 있지 않음"(정보)을 쓴다.
- `AgentNameFinder`: 사용자 설정은 `~/.claude/agents/**/*.md`, 프로젝트·로컬은 `<루트>/.claude/agents`도(같은 이름은 프로젝트 우선). frontmatter `name`(앞 16KB), 없으면 파일 이름. 500개·깊이 8 제한, 링크 제외, 홈은 `AppPaths.Home`(--home).
- 메뉴(ContextMenu·하위 메뉴) 템플릿에 `ScrollViewer`: 항목을 담는 StackPanel에 스크롤이 없어 `MaxHeight`(480)를 넘는 도구 메뉴 끝의 MCP 그룹이 잘려 보이지 않던 문제.
- App ViewModel 테스트는 `tests/DotDeck.App.Tests`(net10.0-windows, 가짜 MCP·에이전트 검색만 사용).

**projects.json 초안**
```json
{
  "schemaVersion": 1,
  "projects": [
    { "id": "builtin.claude", "kind": "builtin", "builtinKey": "claude", "order": 0, "hidden": false, "filter": "all" },
    { "id": "builtin.codex",  "kind": "builtin", "builtinKey": "codex",  "order": 1, "hidden": false, "filter": "all" },
    { "id": "builtin.system", "kind": "builtin", "builtinKey": "system", "order": 2, "hidden": false },
    { "id": "8f3c…", "kind": "folder", "name": "DotDeck", "root": "D:\\project\\DotDeck",
      "color": "#68217A", "filter": "configDocs", "order": 3, "excludedFolders": null }
  ]
}
```
- 기본 제공 프로젝트의 루트는 저장하지 않고 `AppPaths.Home`에서 계산한다.
- 펼침·선택 상태는 `%LOCALAPPDATA%\DotDeck\state\session.json`에 분리 저장한다.

---

## Codex 편집기 확장 설계 (SPEC 7.5, 2026-11-08)

상태: HANDOFF의 승인된 결정 1~7을 반영한 구현 계획. 2a 공통화 구현 완료(2026-11-08), 2b Codex 전용 기능은 구현 전이다. 외부 제품 동작은 HANDOFF의 조사 기록을 기준으로 하며 구현 단계에서 공식 문서와 대조한다.

### 2a: 기존 편집기 공통화
- `EditorKind` enum과 기존 저장 이름(`settingsForm` 등)을 유지한다. Core `Editors/Registry/EditorRegistry`는 `EditorDescriptor`(Kind, 저장 이름, 라벨, 제품, HasForm/HasOutline, Validate, CanShowForm)를 관리한다. App `FormRegistry`는 폼 생성 책임을 갖는다. 역할 카탈로그·수동 지정·문서 VM·메뉴·검증의 종류별 분기를 등록소로 모은다.
- 추가 종류와 저장 이름: `CodexConfig` → `codexConfigForm`, `CodexHooks` → `codexHooksForm`, `CodexAgent` → `codexAgentForm`, `CodexSkillMetadata` → `openaiYamlForm`, `CodexRules` → `codexRulesForm`. SKILL.md는 기존 `Skill`, AGENTS.md는 `Markdown`을 사용하며 `Role.Product`로 검증을 구분한다.
- `ITextDocumentHost`는 공통 멤버만 유지하고 제품 의존 서비스를 `IClaudeFormServices` / `ICodexFormServices`로 분리한다. ViewModel은 View 타입을 참조하지 않는다.
- `Core/Hooks/HookHandlerModel`과 `HookTableViewModel`·`HookTable.xaml`을 공용으로 만들고 저장은 어댑터로 분리한다. 어댑터는 `ClaudeSettingsHookStore`, `CodexHooksJsonStore`, `CodexInlineHookStore` 세 종류다. 기존 Claude의 원문·알 수 없는 데이터 보존과 실행 동작을 유지한다.
- 키-값 표·문자열 목록은 `KeyValueTableViewModel`·`StringListViewModel`로 공통화한다. frontmatter 필드·검증은 제품 프로필로 분리한다. 2a 완료 후 기존 Claude 폼 동작을 사용자에게 확인받는다.

### 2b-0: TOML 위치 패처
- 후속 확장: 새 일반 테이블(점 표기 포함) 생성, 대입문 키 삭제, 빈 일반 테이블 헤더 정리를 지원한다. 삭제 시 독립된 앞 주석·빈 줄은 유지하며 대입문 전체 물리 줄·인라인 주석·끝 개행만 제거한다. 빈 테이블 정리는 헤더 줄만 제거하고 배열 테이블·남은 하위 테이블의 부모는 유지한다. 미지원 인라인 항목/배열 원소 삭제·인라인 신규 키 추가는 초기 구조 폼에서 비활성화한다. 비 UTF-8 TOML은 읽기 전용으로 열고 SafeWriter에서도 차단한다.
- 승인된 Tomlyn은 파서(위치·trivia)로만 사용하고 `Core/Toml/TomlEditDocument`가 원본 텍스트에 패치를 적용한다. 기존 값 토큰만 교체하며 루트 키는 첫 테이블 앞, 테이블 키는 해당 테이블 마지막 줄 뒤에 삽입한다.
- 기존 따옴표 스타일 유지, 새 Windows 경로는 literal `'...'`, 여러 줄은 `"""`를 사용한다. TOML 1.1 전용 구문은 생성하지 않는다. 파싱 오류는 줄·열을 반환하고 폼 전환을 막는다.
- 패치 후 재파싱해 의도한 값·구조와 대조하고 불일치하면 버퍼 반영·저장을 거부한다. 무편집 왕복 바이트 동일, 한 값 변경 시 비대상 영역 동일을 검증한다. BOM·CRLF·주석·`[projects.'d:\project\dotdeck']`·점 키·인라인 테이블·배열 테이블을 포함한다. 시험 실패 시 자체 파서로 전환하고 보고한다.
- YAML은 패키지 추가 없이 자체 `YamlBlockDocument`로 구현한다. 승인된 패키지 버전도 추가 시 실제 배포 여부·호환성을 확인하고 CPM에서만 관리한다.

### 2b-1: 경로·역할·보호·연결 노드
- `AppPaths`에 해석된 Codex 홈을 둔다. 실제 모드에서는 절대경로 CODEX_HOME 또는 사용자 홈의 `.codex`, 샌드박스에서는 실제 CODEX_HOME을 무시한 `<home>\.codex`. `--codex-home`으로 테스트용 경로를 지정하며 기존 데이터 폴더 격리를 유지한다. 환경변수는 기존 추상화를 통해 읽기만 한다.
- `FileRoleResolver`에 `[claude, codex, agents]` 카탈로그와 `configDirAliases`를 전달해 이름이 `.codex`가 아닌 CODEX_HOME도 전역으로 판별한다. `Catalogs/Codex/file-roles.json`·`agents-file-roles.json`을 추가한다. 기존 Claude 판별 규칙과 저장된 수동 편집기 지정은 호환성을 유지한다.
- `LinkedFolders`는 `CustomFilesProjectSource`와 같은 래퍼 패턴으로 해석된 홈의 `.agents/skills`를 Codex 트리의 계산된 가상 노드로 연결한다. 프로젝트 정의에 저장하지 않는다.
- `RuntimeFilePolicy`는 SPEC 7.5의 런타임 경로·파일을 기본 숨김·읽기 전용으로 분류한다. 표시 여부와 쓰기 가능 여부를 분리해 보이게 설정해도 편집·삭제·이동으로 보호를 우회하지 못하게 한다. `/plugins`는 표시·읽기 전용, `/skills/.system`은 "Codex 내장" 안내.
- `ISecretFileClassifier`를 읽기·검색·쓰기·백업·복원 경로에 일관되게 적용해 `auth.json`과 기존 비밀 파일을 차단한다. 기존 링크 최종 대상 검사도 유지한다. 수동 테스트는 임시 홈만 사용한다.

### 2b-2~3: 스키마 폼·신뢰·비밀값
- 내장 `Catalogs/Codex/config-schema.json`, `Core/Schema/*`, `CodexConfigDocument`, `CodexConfigFormViewModel`, `SchemaGrid.xaml`로 스키마 기반 폼을 만든다. 탭 구성은 SPEC 7.5를 따른다. 모르는 키·복잡한 값은 기타 읽기 전용 트리로 보존한다. 카탈로그 덮어쓰기는 `%APPDATA%\DotDeck\catalogs\Codex\`를 사용하며 잘못된 파일은 경고와 함께 내장본으로 대체한다.
- `CodexTrustResolver`는 사용자 config의 `[projects.<key>] trust_level`을 비교한다. 따옴표·구분자·대소문자·`\\?\` 차이를 정규화하고 `project_root_markers`를 고려한다. 문서로 확정하지 못하는 상태는 "확인 불가"다. 계층·신뢰 배너는 실제 Codex 실행 결과를 단정하지 않는다.
- `CodexSecretRules`는 SPEC 7.5의 비밀값 위치와 이름 패턴을 판정한다. `*_env_var`·`env_vars`·`env_http_headers`의 변수 이름은 예외다. `DiffReviewViewModel`, 로그·변경 기록·백업 목록까지 같은 정책을 적용한다. MCP 서버 실행·연결은 하지 않는다.
- 백업 보호기는 Windows App 구현에서 DPAPI(CurrentUser)를 사용하고 Core에는 인터페이스만 둔다. 승인된 `System.Security.Cryptography.ProtectedData`를 CPM으로 추가한다. 비밀값 포함 파일은 원본 바이트를 암호화하고 manifest에 `protected: true`를 남긴다. 복원은 복호화 후 기존 안전 저장 흐름을 따른다. 복호화 실패 시 평문으로 우회하지 않는다. 기존 Claude settings.json env의 평문 백업 문제도 함께 해결한다.
- config.toml 폼·MCP·보호 구현 후 임시 홈 실행 방법과 스크린샷을 제공해 사용자 확인을 받는다.

### 2b-4~8: 나머지 Codex 편집기
- 훅: 공용 모델에 저장 어댑터를 연결한다. 원래 표현으로 저장하고 명시적 변환 메뉴만 `CrossFileEdit`(두 파일 diff·한 백업 세트)을 사용한다. 구현 시 부분 실패·외부 변경 경로도 검증한다. 저장 후 `/hooks` 재신뢰 안내. 실행기는 Windows `commandWindows` 우선, 카탈로그 sample stdin, 실행 전 확인·제한 시간·출력 상한·프로세스 트리 종료를 유지한다.
- 에이전트: `CodexAgentDocument`와 `Templates/Codex/*.toml`. `sandbox_mode`, `approval_policy`, `model_provider`, `mcp_servers`는 폼 입력에서 제외하되 기존 값 보존·경고. 문서에 없는 이름·중첩·탐색 규칙을 임의로 강제하지 않는다.
- 스킬: 기존 스킬 폼의 Codex 프로필, `YamlBlockDocument`·`OpenAiYamlFormViewModel`, 비활성화 토글. `[[skills.config]]` 경로 비교 등 확인되지 않은 동작은 구현 시 별도 검증한다.
- 문서: AGENTS.md·AGENTS.override.md는 Markdown과 제품별 검증 공급자로 override·크기 경고를 제공한다.
- rules: `StarlarkRuleFile`은 최상위 `prefix_rule(...)`과 주석만 폼으로 처리한다. 지원하지 않는 구문은 줄·이유를 표시하고 텍스트 편집으로 남긴다. `codex execpolicy check --pretty --rules <file> -- <cmd>`는 명령 미리보기·사용자 확인 후 셸 없이 ArgumentList, 10초 제한으로 실행한다.

### 검증·커밋 경계
- 단계 0(사양·설계) → 2a-1(등록소) → 2a-2(훅) → 2a-3(폼 공통화) → 사용자 확인 → 2b-0~3 → config 폼 사용자 확인 → 2b-4~8 순서다. 상세 파일 목록·진행 상태는 HANDOFF를 따른다.
- 각 단계는 빌드 → 전체 테스트 → 변경 검증 → Conventional Commits 커밋 → HANDOFF 갱신. CHANGELOG Unreleased에 누적하며 버전 파일은 변경하지 않는다. main 직접 커밋·merge·push와 승인 없는 PR·태그를 금지한다.
- 테스트는 임시 폴더·가짜 구현만 사용한다. 기존 Claude 폼·저장 이름·미지 값 보존 회귀, TOML 바이트 왕복, 샌드박스 CODEX_HOME 격리, 런타임 보호 우회, 비밀값 표시·암호화·복원 실패를 중점 검증한다.
- HANDOFF의 공식 문서 차이·미확인 사항은 구현 단계에서 확인하며, 이번 문서 반영을 새로운 외부 검증으로 간주하지 않는다. 출처는 HANDOFF §6과 공식 스키마 `https://developers.openai.com/codex/config-schema.json`이다.

## 5. App 아키텍처 (`src/DotDeck.App`)

**셸·도킹 VM**
- `ShellViewModel`: `Documents`/`Tools`를 AvalonDock `DocumentsSource`/`AnchorablesSource`에 바인딩.
- 도구 패널 VM

  | VM | ContentId |
  |---|---|
  | `ProjectsPaneViewModel` | `tool.projects` |
  | `DetachedExplorerPaneViewModel` | `tool.explorer:<id>` |
  | `PropertiesPaneViewModel` | — |
  | 출력·검증·검색·훅 테스트 패널 VM | — |

- `TextDocumentViewModel`: ContentId `doc:<path>#<n>`.
- View 선택은 `LayoutItemTemplateSelector`, 컨테이너 바인딩은 `LayoutItemContainerStyleSelector`로 처리한다.
- VM은 View 타입을 참조하지 않는다(`IDialogService`, `IClipboardService`, `IShellLauncher` 사용).

**문서 버퍼**
- `DocumentBufferManager`가 경로별 `DocumentBuffer`를 관리한다(`TextDocument` + 스냅샷 + 참조 카운트).
- "새 창" 명령은 같은 버퍼를 쓰는 두 번째 VM을 만든다. Undo와 dirty 상태는 공유된다.
- 파일 읽기는 백그라운드에서 하고 `TextDocument`는 UI 스레드에서 만든다.

**미리보기 탭**
- 한 번 클릭하면 임시 탭 하나를 재사용한다.
- 더블클릭, 편집, 드래그 시 일반 탭으로 승격한다. 미리보기 탭 제목은 기울임꼴.
- 탭 고정은 AvalonDock 지원 여부를 확인해야 하고, 지원하지 않으면 VM에서 구현한다.

**다중 탐색기 — 평탄화 가상화 목록 하나 (권고)**

| 안 | 평가 |
|---|---|
| **A. 하나의 `ListBox`(가상화)에 `ProjectHeaderRow` + 들여쓴 `NodeRow`** | VS 솔루션 탐색기처럼 스크롤 하나. 가상화 유지. WPF TreeView에 없는 다중 선택·드래그를 구현하기 쉽다 |
| B. Expander + 섹션별 TreeView | 높이 배분이 번거롭다. 다중 선택을 직접 구현해야 한다 |
| C. 탐색기마다 LayoutAnchorable | 헤더만 남기고 접는 동작을 지원하지 않아 SPEC UX와 다르다 |

- 떼어내기: 섹션 헤더 명령으로 같은 `ExplorerViewModel`을 별도 도구 패널로 띄운다.
- 다시 붙이기: 패널 버튼을 누르거나 패널을 닫으면 원래 영역으로 돌아간다.
- 펼침·선택 상태는 유지된다. 헤더를 끌어서 바로 떼어내는 동작은 1b 선택 과제로 둔다.

**테마**
- 테마 사전: `Themes/Dark.xaml`, `Themes/Light.xaml`(Phase 4). 컨트롤 스타일: `Themes/Controls/*.xaml`. 모두 `DynamicResource`로 참조한다.
- 키 규칙: `DD.Brush.<영역>.<역할>[.<상태>]`
  - 배경: `DD.Brush.Editor.Background`(#1E1E1E), `DD.Brush.Panel.Background`(#252526), `DD.Brush.Chrome.Background`(#2D2D30)
  - 테두리·강조: `DD.Brush.Border`(#3F3F46), `DD.Brush.Accent`(#0078D4), `DD.Brush.StatusBar.Background`(#68217A)
  - 글꼴: `DD.Font.Ui`, `DD.Font.Editor`, `DD.Font.Icon`
  - 크기: `DD.Size.TreeRowHeight`(22)
- `ThemeManager`는 테마 사전과 AvalonDock 테마를 교체하고, AvalonDock `ResourceKeys`를 덮어쓴다.
- AvalonEdit xshd는 테마별로 둔다. TOML·YAML 구문 정의는 새로 작성한다.
- 타이틀바: 1a는 DWM 다크 모드(P/Invoke)를 쓴다. 커스텀 크롬은 Phase 4에서 다룬다.

**DI**: 패키지 없이 `App.OnStartup`에 수동 Composition Root를 둔다. Microsoft.Extensions.DependencyInjection은 Phase 3 이후에 재검토한다.

**레이아웃 저장**
- 저장 위치(`%LOCALAPPDATA%\DotDeck\`):
  - `layout\last.xml`
  - `layout\named\<이름>.xml`
  - `state\session.json`
- 복원 순서:
  1. session.json으로 VM을 생성한다.
  2. 직렬화 콜백에서 ContentId로 VM에 연결한다. 파일이 없으면 Cancel.
  3. 실패하면 기본 레이아웃을 쓴다.

**단축키**: Ctrl+Z/Y는 포커스에 따라 나뉜다. 에디터에서는 텍스트 Undo, 탐색기에서는 파일 작업 이력.

---

## 6. NuGet 패키지

**SPEC 3절 명시 패키지**

| 패키지 | 버전 | 프로젝트 |
|---|---|---|
| CommunityToolkit.Mvvm | 최신 안정판(1a에서 확인) | App |
| Dirkster.AvalonDock, .Themes.VS, .Serializer.Xml | 5.0.1 | App (2026-11-07 승인: 5.0에서 직렬화가 별도 패키지로 분리되고, VS2022 다크 테마가 Themes.VS에 포함되어 VS2013 패키지 대신 사용) |
| AvalonEdit | 6.3.1.120 | App |
| Markdig | 최신 안정판(1a에서 확인) | Core/App |
| Microsoft.Web.WebView2 | 1.0.4314.35 | App |
| DiffPlex | 1.9.0 | Core |
| xUnit (+ `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`) | v3 권고 | Tests |

**3절 외 수단 (패키지 추가 없음)**

| 수단 | 이유 / 대안 |
|---|---|
| 휴지통 삭제: `SHFileOperationW` P/Invoke | `Microsoft.VisualBasic`(프레임워크 내장)은 오류 UI 제어와 다중 항목 처리가 약하다. 대안: IFileOperation COM |
| 휴지통 복원: `$Recycle.Bin\<SID>\$I*` 파싱 | 공식 복원 API가 없다. 대안: Shell COM "undelete" 동사(이름이 지역화되어 취약) |
| 인코딩: `CodePagesEncodingProvider` | 프레임워크 내장. 감지 라이브러리는 쓰지 않는다 |
| 미도입: Microsoft.Extensions.DependencyInjection, DiffPlex.Wpf | 수동 조립과 자체 diff 렌더링으로 충분하다 |
| (Phase 2 예고) Tomlyn, YamlDotNet | TOML 편집과 frontmatter 파싱용. JSON은 `System.Text.Json` |
| (Phase 3 예고) DPAPI(ProtectedData 또는 P/Invoke) | 비밀 환경변수 되돌리기용 암호화 백업 |

---

## 7. 단계별 구현 계획 (각 단계 = 1커밋, 단계마다 `/codex-review`)

**Phase 0 마무리**
1. `git init` 후 초기 커밋
2. 승인된 이 문서를 커밋

**Phase 1a — 셸·테마·도킹** (`feat/phase1a-shell`)

| # | 내용 | 완료 기준 |
|---|---|---|
| 1a-1 | 3-프로젝트 재구성, slnx, props, CPM, `.editorconfig`, manifest | build/test 통과, 경고 0 |
| 1a-2 | Core: `AppPaths`, `IClock`, `IAppLog`, `SecretMasker` + 테스트 | 단위 테스트 통과 |
| 1a-3 | 패키지 도입, AvalonDock 셸(메뉴, 툴바, 상태줄, 패널 6종) | 이동·플로팅·자동 숨김 동작 |
| 1a-4 | `Themes/Dark.xaml`, 컨트롤 스타일, DWM 다크 타이틀바 (Codex 위임 가능) | **스크린샷 중간 확인** |

**Phase 1b — 프로젝트·탐색기·트리 조작** (`feat/phase1b-explorer`)

| # | 내용 |
|---|---|
| 1b-1 | `IFileStore`, `ProjectRegistry` |
| 1b-2 | 노드 모델, 필터, 비밀 판별 |
| 1b-3 | 프로젝트 영역(평탄화 목록), 헤더 툴바, breadcrumb, 자동 열기, 상태 복원 |
| 1b-4 | 프로젝트 추가·제거·변경, 떼어내기·붙이기 |
| 1b-5 | `FileOperationService`, 작업 이력, `IRecycleBin` |
| 1b-6 | 트리 조작 UI 전체(F2, Del, 복사·잘라내기·붙여넣기, 복제, 드래그, 다중 선택, 컨텍스트 메뉴) |
| 1b-7 | FSW 디바운스, 실시간 반영 |

**Phase 1c — 에디터·안전 저장·레이아웃** (`feat/phase1c-editor`)

| # | 내용 |
|---|---|
| 1c-1 | `TextFileReader`(인코딩·줄바꿈·바이너리·대용량) |
| 1c-2 | `BackupStore`, `SafeWriter`, `TextDiffService` |
| 1c-3 | 문서 탭, 공유 버퍼, 다크 xshd, 찾기·바꾸기, 미리보기 탭, 고정, 상태줄 |
| 1c-4 | diff 저장 대화상자, 모두 저장, 외부 변경 3지선다, 백업 복원 UI |
| 1c-5 | Markdown 미리보기(WebView2 보안 설정) |
| 1c-6 | 레이아웃·세션 저장/복원, 이름 붙인 레이아웃, 초기화 |

---

## 8. 테스트 전략

임시 폴더 픽스처와 Fake(`FakeEnvironmentStore`, `FakeRecycleBin`, `FakeClock`)만 쓴다. 실제 홈 경로에는 접근하지 않는다.

| 대상 | 핵심 케이스 |
|---|---|
| `AppPathsResolver` | 우선순위, 상대·존재하지 않는 경로, 데이터 폴더 격리 |
| `ProjectRegistry` | 왕복, 미지 필드 보존, 손상 복구, 기본 제공 프로젝트 삭제 거부 |
| 필터·비밀 판별 | 대소문자, 확장자 없는 dotfile, `.env.example` 허용, `id_rsa.pub` |
| `TextFileReader` | BOM 유무, UTF-16 LE/BE, cp949, 혼합 줄바꿈, NUL, 1MB 경계 |
| `SafeWriter` | 변경 없이 저장 시 바이트 동일, BOM·줄바꿈 보존, 외부 변경 충돌, 읽기 전용, 예외 시 임시 파일 정리 |
| `BackupStore` | 경로 매핑, 같은 초 충돌, 보관 정리, 복원 전 백업 |
| 파일 작업·이력 | Undo, 사전조건 충돌 거부, 예약어, 복사본 이름, 덮어쓰기 방지 |
| 마스킹·로그 | 로그에 값이 남지 않음 |
| 디바운스·외부 변경 | FakeClock 기반 병합, 자기 쓰기 무시 |

- FSW와 실제 파일 시스템 통합 테스트는 `[Trait("Category","Integration")]`로 분리한다.
- 수동 확인은 `--home <임시 폴더>`로 한다: 도킹, 테마, 트리와 Undo, 휴지통 복원, 같은 파일 두 그룹, 외부 변경, 미리보기 네트워크 차단, 레이아웃 복원.

---

## 9. 버전 영향
- Phase 0~3은 0.1.0을 유지하고 CHANGELOG `[Unreleased]`에 누적한다.
- Phase 4 `/release`에서 v0.1.0을 낸다.

## 10. 위험 요소

| 위험 | 대응 |
|---|---|
| AvalonDock 5.0 변경점, MVVM 직렬화 함정 | 1a 스파이크, 실패 시 4.7x 고정. ContentId 규칙 고정 |
| VS2022 재현 품질 | 키 체계를 먼저 확정하고 Codex 위임, 1a 스크린샷 확인 |
| WebView2 airspace, 재생성, 런타임 부재 | 탭 안 보기로 한정, 렌더 캐시, 안내 문구 |
| 개발 중 실제 `~/.claude` 등 수정 | `--home` 필수, 데이터 폴더 격리, 테스트는 임시 폴더만 |
| 휴지통 없는 드라이브에서 조용히 영구 삭제 | 삭제 전 확인, "영구 삭제" 경고, Undo 불가 표시 |
| 휴지통 `$I` 형식이 문서화되지 않음 | 형식 버전 검사, 실패 시 안내 |
| 인코딩 오감지 | 엄격 디코드, 바이트 동일 테스트, 상태줄 표시 |
| FSW overflow, 자기 쓰기 오인 | 디바운스, 재스캔, 해시 비교 |
| 대형 폴더(`~/.claude/projects`) | 지연 로딩, 가상화, 기본 제외 |
| 단일 exe와 네이티브 DLL | Phase 4 publish 검증 |

## 11. Codex 검증 포인트
1. SafeWriter의 원자성과 예외 경로 처리
2. 인코딩 보존
3. 휴지통에서 영구 삭제로 빠지는 경로
4. Undo로 다른 파일을 덮어쓰는지
5. 실제 홈 경로에 쓰는 코드가 있는지
6. 비밀 정책 우회 여부와 평문 유출
7. WebView2 차단 설정
8. UI 스레드 차단
9. Core에 Windows/WPF 타입이 새는지
10. 레이아웃 복원 시 ContentId 충돌

## 12. 미확인 사항 (해당 단계 스파이크에서 확인)
- `dotnet sln`의 slnx 동작
- xunit.v3 템플릿
- CPM에서 `dotnet add package` 동작
- AvalonDock 5.0 변경점과 탭 고정
- WebView2에서 CSP와 주입 스크립트
- 단일 exe의 WebView2Loader
- CommunityToolkit.Mvvm·Markdig 버전

---

## AI CLI 연동 설계 (SPEC 11, Phase 5, 2026-11-09 승인)
근거: SPEC 11절, 설치 CLI 도움말(Claude Code 2.1.319, codex-cli 0.167.0), Claude Code 공식 hooks·settings 문서(2026-11-09 조회). "확인 필요"는 해당 단계 스파이크에서 실측 후 이 절에 결과를 기록한다.

### 선행 조건 (1c 확인 결과)
- 충족: `SafeWriter`(`ExternalChangeKind`·`ExternalChangeException`), `ExternalChangeDetector`, `ShellViewModel.Files.cs`(`ResolveConflictAsync`·자동 다시 불러오기), content/트리 `FolderWatcher`, `BackupStore`+DPAPI, `SafeWriter.Restore`, `TextDiffService`/`DiffReviewDialog`.
- 5a에서 보완: 세션 스냅샷은 주 백업 Prune(세트 50개)과 격리된 별도 `BackupStore`(세션 폴더 `sessions\<id>\backups\`), `SaveAllCoreAsync(): Task<bool>` 분리, Job Object·ConPTY·Named Pipe 인프라 신설(`HookRunner`는 재사용하지 않음).

### 확정 결정
| # | 결정 |
|---|---|
| A1 | 새 NuGet 없음. ConPTY·Job Object·Named Pipe는 P/Invoke, MCP(JSON-RPC 2.0, 줄 단위 stdio)는 직접 구현. xterm.js 6.0.0(MIT)은 dist 파일과 **LICENSE를 함께** `src/DotDeck.App/Assets/Terminal/`에 번들, 버전·SHA-256을 `THIRD-PARTY-NOTICES.txt`와 이 문서에 고정 |
| A2 | Claude·Codex는 셸 없이 ConPTY(대화형)/파이프(비대화형)로 직접 실행. 일반 PowerShell 터미널은 PATH `pwsh.exe` → `%ProgramFiles%\PowerShell\7\pwsh.exe` → `%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe`(UTF-8 콘솔 인코딩 초기화) |
| A3 | 컨텍스트는 `--append-system-prompt` 문자열(12,000자 상한, 초과 시 "외 N개"). `context.md`는 기록용. 5c부터 MCP로 보완 |
| A4 | `--home`/`DOTDECK_HOME`일 때만 자식 환경에 `CLAUDE_CONFIG_DIR=<home>\.claude`, `CODEX_HOME=<home>\.codex`. 실제 홈 모드에서는 두 변수를 넘기지 않는다(테스트로 보장). 샌드박스 `CLAUDE_CONFIG_DIR`에 가짜 전역 훅(guard_bash·protect_files 흉내)을 두어 A5 병합 실측에 사용 |
| A5 | 범위 가드는 `--settings <세션 settings.json>`의 PreToolUse 훅으로 주입. 5a-4에서 전역 훅과 병합되는지 실측. **병합되지 않으면** 사용자 `<CLAUDE_CONFIG_DIR 또는 ~/.claude>\settings.json`의 `hooks`를 읽기 전용으로 읽어 세션 settings.json에 함께 넣는다(전역 파일은 수정하지 않음) |
| A6 | 범위 밖 읽기 차단 기본 켬(`permissions.blockReadsOutsideWorkingDirectories` + 가드 Read 판정). 대화상자 옵션 "범위 밖 읽기 허용" |
| A7 | 사용자 MCP 서버: 비대화형은 `--strict-mcp-config`로 끔, 대화형은 유지하되 해당 도구 호출은 가드가 ask |
| A8 | 이어가기 시에만 `--system-prompt-snapshot off`(설치 도움말에서 옵션 존재 확인. 기본 on은 이후 resume에서 기록된 프롬프트를 그대로 재사용) |
| A9 | Codex 범위 보호는 훅 없이 `-s workspace-write` + `-C`/`--add-dir` + 사후 변경 요약. 읽기 제한 없음, 범위 축소는 재시작. Windows sandbox 미설정이면 실행 전 경고하고 비대화형 Codex 비활성. `codex.cmd`는 실행하지 않고 `node.exe <npm>\node_modules\@openai\codex\bin\codex.js` |
| A10 | Codex 이어가기는 비대화형(`codex exec resume <id>`)만. 대화형은 새 세션 |
| A11 | 범위 외 허용: 훅 입력 `scratchpad_dir`(읽기·쓰기), `<CLAUDE_CONFIG_DIR>\{skills,agents,commands,plugins,rules}`(읽기만). 플랜 파일 경로는 실측 후 추가 |
| A12 | **범위 안 비밀 파일(SPEC 2.3 자물쇠 대상, 링크는 최종 대상 기준)은 대화형·비대화형 모두 deny**(Read·쓰기 도구·Glob/Grep 대상). 전역 protect_files와 동작을 맞춘다 |
| A13 | 릴리스: 5a → 1.2.0, 5b → 1.3.0, 5c → 1.4.0 (`/release`) |
| A14 | 비대화형 기본: `--max-budget-usd` 미지정(옵션), "읽기 전용 셸 허용" 끔 |
| A15 | 스냅샷 기준(설정 노출): 시작 시 전체 캡처는 텍스트 2,000개·합계 20MB 이하, 색인 상한 50,000개, 해시는 2MB 이하 파일, 세션 폴더 보관 7일/최근 20세션 |

### 프로세스 구조
- 가드·MCP 브리지는 **DotDeck.exe 하위 모드**: `DotDeck.exe --dotdeck-guard <scope.json>`, `--dotdeck-mcp <sessionId>`, `--dotdeck-heartbeat <sessionDir>`. 사용자 정의 `Program.Main`(`StartupObject`)이 인자를 먼저 보고 WPF를 초기화하지 않고 Core 로직을 실행한다. 훅은 exec form(`command`=`Environment.ProcessPath`, `args`)으로 등록해 셸을 거치지 않는다.
- 5a-4 확인: WinExe(GUI 서브시스템)에서 상속받은 stdin/stdout 파이프가 정상 동작하는지, 콜드 스타트를 **SelfContained 단일 파일 publish 기준**으로 측정. 200ms 초과 → ReadyToRun 적용 → 그래도 초과 → 별도 소형 가드 콘솔 exe(`src/DotDeck.ScopeGuard`, Core 참조, DotDeck.exe 옆 배치)로 분리(사전 승인됨). 측정값을 이 절에 기록.

### Core (`src/DotDeck.Core/AiCli/`)
| 네임스페이스 | 타입 |
|---|---|
| `Scope` | `SessionScope`, `ScopeEligibility`(11.2), `ScopeKey`(주 ID + 정렬된 추가 ID), `ScopeFile`(scope.json, 원자적 쓰기·공유 위반 재시도), `PathNormalizer`, `IFinalPathResolver`, `ScopeChecker`, `ScopeDecision` |
| `Guard` | `PreToolUseInput`, `HookOutput`, `ToolPathExtractor`, `ShellCommandAnalyzer`, `GuardEvaluator`(순수 함수), `GuardHost`(stdin/stdout, fail-closed) |
| `Launch` | `CliKind`, `LaunchMode`, `CliLaunchRequest`, `WindowsCommandLine`, `TomlString`, `ClaudeCommandBuilder`, `CodexCommandBuilder`, `ICliLocator`, `ForbiddenOptions`, `SessionFilesWriter` |
| `Presets` | `SessionPreset`, `SessionPresetStore`(sessions.json, 모르는 필드 보존, 손상 시 `.corrupt-<ts>`) |
| `Context` | `ContextDocumentBuilder`(비밀·시스템·범위 밖 제외, 길이 상한) |
| `Snapshots` | `ScopeIndex`, `SessionSnapshot`, `ChangeSummaryBuilder`, `RevertPlanner` |
| `Output` | `ClaudeStreamJsonParser`, `CodexJsonlParser` |
| `Mcp`(5c) | `JsonRpcFraming`, `McpServer`, `IAiSessionHost`, 도구 5종 |

### App
- `Program.cs`(모드 분기), `Infrastructure/Windows/{JobObject, NativeProcessLauncher, PseudoConsole, WindowsFinalPathResolver, NamedPipeMcpHost}`, `Services/AiSessionManager`.
- ViewModel: `SessionLaunchDialogViewModel`, `TerminalSessionPaneViewModel`(ContentId `tool.terminal:<id>`, 레이아웃 복원 제외, 원시 출력 재생 버퍼 2MB), `AiJobsPaneViewModel`(`tool.aijobs`), `ScopeChipViewModel`, `ChangeSummaryViewModel`.
- View: `Views/Tools/{TerminalPaneView, AiJobsPaneView}`, `Views/Dialogs/{SessionLaunchDialog, ChangeSummaryDialog}`(기존 `DiffView` 재사용). 터미널 자산은 EmbeddedResource, 가상 호스트 `term.dotdeck.local`을 `WebResourceRequested`로 응답(그 외 403, 외부 탐색·새 창·DevTools 차단).
- 리소스 키: `DD.Brush.Terminal.*`(배경·전경·커서·선택·ANSI 16색), `DD.Brush.StatusBar.AiRunning` (Dark/Light 동시 추가).

### 명령줄
- ConPTY는 `CreateProcessW(lpApplicationName=절대경로, lpCommandLine)`이 필요하므로 `ArgumentList`를 쓰지 않고 CommandLineToArgvW 규칙을 직접 구현(공백·탭·개행·`"` 포함 시 인용, `"` 앞 `\`는 2n+1, 닫는 따옴표 앞 `\`는 2n). 비대화형도 같은 실행기를 사용.
- `.cmd`/`.bat` 대상 금지, 현재 폴더 검색 금지. Codex `-c key=value` 값은 TOML 기본 문자열.
- `ForbiddenOptions`: `--dangerously-skip-permissions`, `--allow-dangerously-skip-permissions`, `bypassPermissions`, `--dangerously-bypass-approvals-and-sandbox`, `--dangerously-bypass-hook-trust`, `danger-full-access`, `--approve-for-me`, `--bg`, `--remote-control`은 빌더가 만들지 않음(테스트로 단언).
- 대화형 Claude: `claude.exe --add-dir <추가…> --settings <settings.json> [--mcp-config <mcp.json>] --session-id <uuid>|--resume <uuid> [--system-prompt-snapshot off] -n "<탭 제목>" --append-system-prompt "<컨텍스트>"`.
- 비대화형 Claude: `-p --output-format stream-json --permission-mode dontAsk --permission-prompts none --tools Read,Glob,Grep,Edit,Write,NotebookEdit --allowedTools Read Glob Grep Edit Write NotebookEdit --strict-mcp-config` + 공통 인자. "읽기 전용 셸 허용" 시 `--tools`에 Bash/PowerShell, allowedTools에 `Bash(git status)`, `Bash(git diff *)`, `Bash(git log *)`만.
- Codex 비대화형: `node.exe codex.js exec -C <주> --add-dir <추가…> -s workspace-write -c approval_policy="never" -c developer_instructions="…" --json -o <last.txt> -`(프롬프트 stdin). 대화형: `-a on-request --no-daemon`.
- 확인 필요: stream-json이 `--verbose`를 요구하는지, Claude `-p`의 stdin 프롬프트, Codex `--json`의 thread id.

### 범위 검사
1. 따옴표·공백 제거, 빈 값 deny. 2. `\\?\C:\`→`C:\`, `\\?\UNC\`→`\\`, `\\.\`·`GLOBALROOT`·`Volume{}`·예약 장치명(NUL 제외) deny. 3. ADS(`:stream`, `::$DATA`)는 호스트 파일 기준. 4. `Path.GetFullPath(p, cwd)`. 5. 존재하는 가장 깊은 상위를 `GetFinalPathNameByHandle`로 해석(링크·정션·8.3·대소문자·매핑 드라이브), 남은 부분 재결합. 실패 시 쓰기 deny / 셸 ask. 6. `OrdinalIgnoreCase`로 `root` 동일 또는 `root + "\"` 접두(형제 접두 방지). 7. 허용 집합은 roots + A11.
- 도구별: Write/Edit/MultiEdit/NotebookEdit 쓰기, Read 읽기, Glob `path`·절대 패턴의 고정 접두, Grep `path`, `mcp__dotdeck_session__*` 통과, 그 외 MCP ask.
- 셸(`ShellCommandAnalyzer`, 최선 노력): 경로 후보 중 범위 밖 확정 → deny, `cd`·변수·서브셸·`iex`·`-EncodedCommand`·중첩 셸·스크립트 실행·다운로드 쓰기 → ask, 모두 범위 안 → 결정 없음. 임의 코드 실행 우회는 막을 수 없으므로 비대화형은 셸 기본 제외 + 사후 요약.
- 가드 출력: 범위 안은 출력 없이 exit 0(**allow를 내지 않음**), 범위 밖·비밀 파일은 `permissionDecision: deny` JSON, 모든 예외는 deny JSON + exit 2. 등록 시 `timeout: 10`, `onFailure: "block"`. stdout은 BOM 없는 UTF-8.
- scope.json: `generation` 증가 + 원자적 교체, 가드는 매 호출 `FileShare.ReadWrite|Delete`로 읽고 공유 위반 시 20ms×3 재시도, 없음·손상·sessionId 불일치는 deny. 범위 제외·프로젝트 닫기/제거(`ProjectRegistry.Changed`) 시 즉시 갱신.
- `-p`에서 설정 검증 실패가 조용히 무시되므로 SessionStart 훅 heartbeat가 15초 안에 없으면 DotDeck이 세션을 종료하고 알린다(SessionStart의 `-p` 실행 여부 확인 필요).
- 알려진 한계: 하드링크, 검사↔쓰기 사이 링크 교체(TOCTOU), 디렉터리별 대소문자 구분 폴더. 변경 요약으로 사후 탐지.

### 스냅샷·변경 요약
- 시작: 모두 저장 후 실행 → 범위 색인(제외 폴더 적용, 링크 미추적, 상한 초과 시 "색인 생략" 표시) → 범위 내 열린 문서를 세션 BackupStore 한 세트로 → 소규모 범위는 전체 캡처.
- 실행 중: 가드가 범위 안 Write/Edit/NotebookEdit 직전 대상 원본을 `pre-write`로 경로별 1회 캡처(Claude만).
- 종료/`변경 검토`: 재스캔 비교로 Modified/Created/Deleted + 이전 내용 유무. 되돌리기는 `SafeWriter.Restore`(현재본 `before-restore` 백업), Created는 휴지통, 요약 후 재수정은 해시로 감지해 확인, 이전 내용이 없으면 비활성+사유. 비밀 파일 diff는 SecretPresentation 규칙, 1MB 초과·바이너리는 크기 변화만.
- 구현 (5a-8): 세션 BackupStore는 세션 폴더의 `backups`(DPAPI, 보관 무제한, 세션 폴더와 함께 삭제). 비밀·바이너리·2MB 초과 파일은 캡처하지 않는다.
  - 색인이 불완전하면(상한 도달 또는 읽을 수 없는 폴더) Deleted를 추정하지 않고, 시작 색인에 없던 Created는 `PossiblyPreexisting`으로 표시해 휴지통 되돌리기를 막는다. 시작 색인이 온전하면 세션 중 생성 후 다시 쓴 파일은 사전 캡처가 있어도 Created.
  - 시작 색인에 없지만 사전 캡처된 경로(색인 생략, 제외 폴더)도 요약에 포함. 단 색인이 온전하고 색인 대상 위치였던 경로는 세션 중 생긴 파일이므로 제외(생성→재쓰기→삭제가 "삭제 복원"이 되지 않게). 캡처된 파일은 크기·시간이 같아도 해시 비교.
  - 사전 캡처는 `<session>\backups\capture.lock` 프로세스 간 락(2초) 안에서 조회·읽기·생성. 시간 초과 시 캡처 없이 쓰기 허용(훅 10초 제한 보호).
  - 되돌리기 직전 대상이 세션 루트 안인지, 파일부터 루트 직전까지 reparse point가 없는지 다시 확인. 비강제 되돌리기는 `SafeWriter.Restore(..., RestoreExpectation)`로 요약 시점 해시(삭제는 부재)를 쓰기 직전에 재검증.
  - 보류: 세션 백업 내부 `.lock` 10초 대기(세션 저장소의 다른 쓰기 주체 없음), 요약 시 2MB 초과 파일의 비강제 되돌리기는 시간 비교만, 범위 안 정션 아래 열린 문서는 읽기 사본이 캡처될 수 있음(되돌리기는 차단).

### 데이터 파일
- `sessions.json`(`%APPDATA%\DotDeck\` / 샌드박스 `<home>\.dotdeck\roaming\`): `schemaVersion`, `presets[]`(프로젝트 **ID** 참조 → 이름·루트 변경 자동 반영, 없는 ID는 경고·실행 비활성), `lastAdditional{주ID: [추가ID]}`, `conversations{scopeKey: {claude.sessionId, codex.threadId}}`. 모르는 필드 보존.
- 세션 폴더 `%LOCALAPPDATA%\DotDeck\sessions\<id>\`: `scope.json`, `context.md`, `settings.json`, `mcp.json`, `bridge.key`(DPAPI), `guard.log`(경로·결정만, `SecretMasker` 적용), `heartbeat`, `index.json`, `summary.json`. 정리: 요약 수락·닫기 후 7일, 실행 중 세션 제외.

### 세션 시작 대화상자 (5a-9 구현)
- 진입점: 도구 메뉴 `Claude 세션 시작…`(Ctrl+Alt+C), 프로젝트 컨텍스트 메뉴 `이 프로젝트에서 Claude 시작…`. 5a의 비대화형은 프롬프트가 필요하므로 빠른 실행도 대화상자를 연다(주 프로젝트 미리 선택).
- 기본 주 프로젝트: 활성 문서가 속한 프로젝트(가장 깊은 루트) → 프로젝트 영역 선택 행. 전역(기본 제공) 프로젝트는 기본값이 되지 않는다(명시 지정 시에만 선택, 경고 표시). 비활성 프로젝트는 사유와 함께 표시하되 선택 불가, 시스템·숨김 프로젝트는 표시하지 않는다.
- 흐름: 모두 저장 → 미저장 변경이 남으면 확인 → 대화상자 → 작업 스레드에서 `AiSessionManager.Start` → 마지막 추가 프로젝트·프리셋 저장(`sessions.json`이 읽기 전용이면 프리셋 저장 비활성).
- 명령줄 미리보기는 파일을 쓰거나 실행하지 않으며 세션 폴더 자리에 `(새 세션)`을 쓴다. 프롬프트는 표준 입력으로 전달되므로 명령줄에 나오지 않는다.
- 준비 중(작업 스레드) 프로젝트가 제거되면 세션 등록 직후 범위를 다시 검사한다. 범위 갱신은 `_scopeSync` 락으로 직렬화.

### AI 작업 패널·변경 검토 (5a-10 구현)
- 패널 `tool.ai`(아래, 처음엔 숨김 → 세션이 시작되면 표시·선택). 세션 목록(시작 시각·상태·가드 상태) + 선택 세션의 출력, 중지(확인 후 Job 전체 종료), 변경 검토, 끝난 세션 지우기(다시 나타나지 않음). 상태줄 `AI n 실행 중` 클릭 시 패널 열기.
- 출력: `AiSession.AttachOutput`이 버퍼(최근 5,000줄·400만 자, 100만 자를 넘는 한 줄은 실시간 전달만 하고 보관하지 않음)와 구독을 원자적으로 넘겨 늦게 열린 패널도 줄을 잃거나 중복하지 않는다. `ClaudeStreamParser`가 stream-json을 요약: 텍스트, 도구 호출(경로·명령만, 쓴 내용 제외), 실패·거부된 도구 결과만(성공 결과는 파일 본문일 수 있어 숨김), 결과 줄(턴·비용·시간). 모든 표시 문자열은 자르기 전에 `SecretMasker`, 제어 문자 제거, 4,000자 제한. 프롬프트 표시도 마스킹.
- 가드 상태: heartbeat 파일을 250ms 간격으로 확인해 `GuardConnected`(“가드 적용됨”)를 표시, 제한 시간 안에 없으면 기존대로 실패 처리.
- 변경 검토 대화상자: 파일 목록 + diff(이전 → 현재), 이 파일 되돌리기 / 모두 되돌리기(한 번 확인, 요약 이후 다시 바뀐 파일은 모아서 다시 확인) / 수락. 비밀 파일(이름 또는 링크 최종 대상)·바이너리·2MB 초과는 diff 대신 사유 표시, 화면 공유 모드면 diff 가림. 되돌리기 후 열린 문서는 외부 변경 검사로 갱신.
- 세션이 끝나면 변경이 있을 때만 검토 대화상자를 자동으로 연다. UI 스레드 큐로 한 번에 하나씩, 다른 모달이 열려 있으면 상태 메시지로 대신 안내. 새 파일 되돌리기는 휴지통으로만(휴지통이 없으면 거부).

### 프로세스 정리
- 세션마다 Job(`KILL_ON_JOB_CLOSE`, breakaway 불허). `STARTUPINFOEX`에 `PROC_THREAD_ATTRIBUTE_JOB_LIST`(+ConPTY `PSEUDOCONSOLE`)로 생성과 동시에 할당, 대체는 `CREATE_SUSPENDED`→Assign→Resume. 중지: Ctrl+C → 3초 → `TerminateJobObject`. `ClosePseudoConsole`은 출력 읽기를 유지한 채 호출. 앱 종료 시 실행 중 세션이 있으면 확인. 상태줄 `AI n 실행 중`(클릭 시 세션 목록).

### 단계 (각 1커밋, 빌드 → 테스트 → `/codex-review` → 커밋)
- 5a: 0 설계 문서 / 1 범위 모델 / 2 경로 정규화·범위 검사기 / 3 가드 판정·셸 분석 / 4 가드 모드·scope.json 갱신(+stdio·콜드 스타트·병합 실측) / 5 명령줄 빌더·CLI 탐색 / 6 프리셋·컨텍스트 / 7 Job 실행기·세션 관리자 / 8 스냅샷·변경 요약·되돌리기 / 9 세션 시작 대화상자·진입점 / 10 AI 작업 패널·변경 요약 대화상자 / 11 CHANGELOG·HANDOFF.
- 5b: ConPTY / xterm 번들 / 터미널 패널 / 대화형 세션 탭·범위 칩 / PowerShell 터미널 / 변경 검토 연결 + 스크린샷 확인.
- 5c: MCP 서버 / Named Pipe 호스트·브리지 / 컨텍스트 고도화 / Codex 지원 / 4개 시나리오 수동 확인.

### 테스트
- Core: 명령줄(공백·한글·`"`·끝 `\`·`%`·`&`·`'`·개행, 추가 폴더 0/1/3, Claude/Codex·모드 분기, TOML 이스케이프, 금지 옵션 미포함, `.cmd` 거부), 범위 검사(가짜 resolver로 대소문자·`..`·링크 체인·UNC·`\\.\`·ADS·8.3·형제 접두·축소 후 즉시 deny·손상 deny), 가드(도구별 입력 픽스처, 비밀 파일 deny, 예외 exit 2, 셸 분석), 프리셋 왕복·ID 기반 갱신, 컨텍스트 제외, 스냅샷→변경→되돌리기, A4 환경 변수 전달/미전달.
- App: CommandLineToArgvW 왕복, 실제 정션 1건, `tests/DotDeck.FakeCli`(argv·cwd·환경 기록, stream-json/JSONL 출력, `--settings` PreToolUse 훅 실제 호출, 손자 프로세스로 Job 종료 검증).
- 수동(샌드박스 홈): 훅 병합, 가드 지연, TUI 한글 IME, 크기 조절, 실행 중 탭 반영, SPEC 5c 4개 시나리오.

### 실측 기록 (5a-4, 2026-11-09)
- **가드 지연**: `dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true`(SelfContained, 154MB) 산출물로 `--dotdeck-guard` 15회: p50 68ms(min 66, max 71). 새 빌드의 최초 1회는 6.9초(단일 파일 네이티브 라이브러리 추출·백신 검사로 추정, 이후 캐시). DotDeck 본체 실행 시 같은 추출이 먼저 일어나므로 세션 중 가드 호출에는 해당하지 않는다. 200ms 기준 충족 → ReadyToRun·별도 가드 exe 분리 불필요.
- **WinExe stdio**: GUI 서브시스템 DotDeck.exe가 상속받은 stdin/stdout/stderr 파이프로 정상 입출력(App.Tests `GuardProcessTests`: 범위 안 무출력, 범위 밖 deny, scope.json 없음 exit 2). WPF는 초기화하지 않는다(`Program.Main` 분기).
- **`--settings` 훅 병합(A5)**: 샌드박스 `CLAUDE_CONFIG_DIR`의 `settings.json`에 가짜 전역 SessionStart 훅 2개(guard_bash·protect_files 흉내), `--settings` 파일에 세션 훅 1개를 두고 Claude Code 2.1.319 `-p` 실행 → 세 훅의 마커 파일이 모두 생성됨(로그인 전 단계에서도 SessionStart 실행). **훅 목록은 병합되며 대체되지 않는다** → A5 대안(전역 hooks 복사) 불필요. PreToolUse 병합의 실제 모델 호출 확인은 샌드박스 로그인이 필요해 5a 수동 확인 항목으로 남긴다. 실행 후 샌드박스 설정 폴더에 `backups`, `plugins`, `projects`, `session-env`, `sessions`가 생겼으므로 A4 격리가 실제로 필요함을 확인.
- **보완 결정**: Grep이 폴더 대상으로 실행되면 가드는 폴더 안 비밀 파일 내용 노출을 막을 수 없다. 세션 settings.json에 `permissions.deny`로 비밀 파일 패턴(dotenv 계열, `*.pem`, `*.key`, `*.pfx`, `id_rsa*`, `id_ed25519*`, `auth.json`의 Read 규칙)을 함께 넣어 Claude 내장 Read 계열 규칙으로 보강한다(5a-5 `SessionFilesWriter`, 동작은 5a 수동 확인).
- **Codex 지적 보류**: 대소문자 구분이 켜진 디렉터리(`fsutil file setCaseSensitiveInfo`)에서 대소문자만 다른 형제 폴더는 범위 비교(OrdinalIgnoreCase)로 구분되지 않는다. 기본 NTFS에서는 발생하지 않아 알려진 한계로 둔다. 후속 후보: 루트·대상 폴더의 `FILE_CASE_SENSITIVE_INFO`를 조회해 켜져 있으면 Unresolved.
- **셸·검색 비밀 패턴(5a-3 보완)**: 비밀 표본 대조만으로는 `foo*.pem`, `.env.staging*` 같은 변형을 놓친다. 와일드카드를 빈 문자열·한 글자로 치환한 구체 이름도 `SecretFilePolicy`로 판정하고, 셸 경로 후보(`./auth.*`)에도 같은 판정을 적용한다. 정적 판정의 한계는 `permissions.deny` 비밀 규칙으로 보강한다.

### 실측 기록 (5a-5, 2026-11-09)
- **exec 형식 훅**: `command` + `args`(셸 없음) 훅이 공백·한글 경로의 실행 파일·인자로 정상 실행됨(Claude Code 2.1.319).
- **비대화형 출력**: `-p --output-format stream-json`은 `--verbose` 없이 거부된다 → 빌더가 항상 함께 넘긴다.
- **프롬프트 전달**: `-p`(Claude)와 `codex exec -`는 stdin 프롬프트를 받는다 → 사용자 프롬프트는 명령줄에 넣지 않는다(`-`로 시작하는 프롬프트가 옵션으로 해석되는 문제와 명령줄 길이 제한 회피).
- **명령줄 인용**: `WindowsCommandLine.Join` 결과를 실제 `CommandLineToArgvW`로 분해해 원래 인자와 같은지 검증(App.Tests `CommandLineRoundTripTests`: 공백·후행 역슬래시·따옴표·한글·탭·줄바꿈·셸 특수문자·TOML 값).
- **permissions.deny 비밀 규칙(Codex 5a-5 반영)**: 이름 패턴은 `SecretFilePolicy`가 비밀로 보는 이름을 모두 덮는다(`.env*`, `*.pem`, `*.key`, `*.pfx`, `id_rsa*`, `id_ed25519*`, `auth.json`). 상대 규칙(`**/`)은 기본 작업 폴더 아래에만 적용되므로 범위 루트마다 절대 규칙(`//c/<경로>/**/<패턴>`, 공식 문서: Windows 경로는 POSIX 형태로 정규화)을 함께 넣는다. UNC 루트와 규칙 문법 문자(`*?[]{}()"`)가 든 루트는 절대 규칙을 만들 수 없어 가드에만 의존한다. 권한 규칙의 `!` 예외는 같은 출처의 상대 규칙에만 적용되므로 쓰지 않는다 → dotenv 템플릿(`.example` 등)도 내장 Read/Edit에서 막힌다(안전 쪽 과차단, 가드는 허용).
- **npm shim 확인**: `.cmd`/`.bat` 옆에 패키지 스크립트가 있어도 배치 파일 본문이 `%dp0%\node_modules\<패키지 스크립트>`를 실행할 때만 `node.exe <스크립트>`로 바꾼다. 그 밖의 배치 파일은 거부한다.
