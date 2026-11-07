# DotDeck 설계안 (Phase 0)

> 상태: **승인됨 (2026-11-07, 모든 질문 권장안 채택)**.
> 근거: `docs/SPEC.md`, `CLAUDE.md`. 패키지 버전은 2026-11-07 nuget.org 기준. "확인 필요"는 아직 검증하지 못한 항목이다.

## 확정된 결정 (Q1~Q18 권장안)
| # | 결정 |
|---|---|
| Q1 | 단일 exe는 framework-dependent (.NET 10 Desktop Runtime 필요) |
| Q2 | WebView2 런타임이 없으면 안내 문구와 텍스트 보기만 제공 |
| Q3 | `--home`/`DOTDECK_HOME` 지정 시 데이터 폴더도 `<home>\.dotdeck\{roaming,local}`로 격리. `--data <dir>`로 덮어쓰기 가능 |
| Q4 | 프로젝트 간 드래그는 이동, Ctrl은 복사. 다른 볼륨 간에는 확인. Windows 탐색기에서 끌어오면 복사 |
| Q5 | 기본 제공 프로젝트별 기본 제외 목록(`.claude`: projects, todos, shell-snapshots, statsig, file-history, debug / `.codex`: sessions, log). 설정에서 해제 |
| Q6 | "설정·문서만" 필터에 확장자 없는 dotfile, `.gitignore`, `.editorconfig`, csproj, slnx, ini, xml, cmd, bat, sh, psm1 추가 |
| Q7 | 휴지통 복원은 `$I` 파싱, 실패하면 안내 |
| Q8 | 백업 보관은 파일별 50개 |
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
| 트리 | `NodeInfo`, `ExplorerFilter`, `FileKindClassifier`, `ExcludedFolderPolicy`, `SecretFilePolicy` | 노드 모델, 표시 필터, 제외 폴더, 비밀 파일 판별 |
| 파일 작업 | `FileOperationService`, `FileOperationHistory` | 생성·이름 변경·이동·복사·복제·휴지통 삭제. 이름 유효성 검사. Undo 스택(세션, 상한 100). Undo 전 사전조건 검사 |
| 읽기 | `TextFileReader` → `TextFileSnapshot` | BOM → 엄격 UTF-8 → cp949 순 감지. 줄바꿈, 바이너리(앞 8KB NUL), 1MB 플래그, 해시, mtime |
| 안전 쓰기 | `SafeWriter` | `Prepare → WritePlan(diff)` → `Commit`: 외부 변경 검사 → 백업 → 같은 폴더 임시 파일 → `File.Replace`. 원래 인코딩으로 표현 못 하는 문자 경고. 변경 없이 저장하면 바이트가 동일해야 함 |
| 백업 | `BackupStore` | `backups\<ts>[-n]\<드라이브>\<경로>` + `manifest.json`. 목록·복원(복원 전 현재본 백업). 보관 정책 |
| Diff | `TextDiffService` | DiffPlex 줄 단위 diff 모델 |
| 외부 변경 | `ExternalChangeMonitor` | FSW와 창 활성화 시 재확인. 자기 쓰기는 해시로 무시 |
| 마스킹 | `SecretMasker` | `KEY|TOKEN|SECRET|PASSWORD|PWD|CREDENTIAL`. 로그에 파일 본문과 환경변수 값을 남기지 않음 |
| 설정 | `AppSettings`, `SettingsStore` | `%APPDATA%\DotDeck\settings.json` |

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
| Dirkster.AvalonDock, .Themes.VS2013 | 5.0.1 | App |
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
