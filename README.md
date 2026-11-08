# DotDeck

개발 환경 설정 파일(`~/.claude`, `~/.codex`, 환경변수, PATH, hosts 등)과 문서를 한 창에서 찾고·보고·수정하는 Windows 데스크톱 에디터입니다. Visual Studio 2022 스타일의 도킹 UI(다크/라이트 테마)를 씁니다.

## 주요 기능
- **프로젝트 = 탐색기**: Claude·Codex 설정 폴더와 시스템 프로젝트가 기본 제공되고, 원하는 폴더를 프로젝트로 추가할 수 있습니다. 트리에서 만들기·이름 바꾸기·이동·복사·삭제(휴지통)·되돌리기를 지원합니다.
- **안전한 편집**: 모든 저장은 diff 미리보기 → 백업(`%LOCALAPPDATA%\DotDeck\backups`) → 원자적 쓰기로 처리하며, 인코딩·BOM·줄바꿈을 보존합니다. 백업에서 복원할 수 있습니다.
- **편집기**: 구문 강조, 찾기/바꾸기, 탭 분할·플로팅, 세션·레이아웃 복원, Markdown 미리보기와 목차.
- **타입별 폼 편집기**: agents·skills(frontmatter), Claude Code `settings.json`(권한·훅·env)을 폼으로 편집하고 실시간 검증합니다. 훅 테스트 실행기와 템플릿에서 새 파일 만들기를 제공합니다.
- **시스템 프로젝트**: 사용자/시스템 환경변수 표(비밀 값 마스킹, 변경 기록·되돌리기), PATH 편집기, 중요 파일 목록, Windows ACL 읽기 전용 보기, 관리자 권한으로 다시 시작.
- **전체 검색**(Ctrl+Shift+F), **설정**(테마·편집기 글꼴 크기·백업 보관 개수).

비밀 파일(`.env*`, `*.pem`, `*.key`, `*.pfx`, `id_rsa*`, `id_ed25519*`)은 열거나 검색하지 않으며, 비밀 값은 로그·백업에 평문으로 남기지 않습니다.

## 요구 사항
- Windows 10 20H1 이상 / Windows 11 (x64)
- Microsoft Edge WebView2 런타임(Markdown 미리보기, Windows 11에는 기본 설치)
- 빌드 시: .NET 10 SDK

## 실행
```powershell
# 개발 실행
dotnet run --project src/DotDeck.App

# 실제 설정을 건드리지 않는 샌드박스 실행(홈·앱 데이터·환경변수를 테스트 폴더로 격리)
dotnet run --project src/DotDeck.App -- --home D:\tmp\dotdeck-home
```
`--home <폴더>` 또는 환경변수 `DOTDECK_HOME`을 주면 샌드박스 모드가 됩니다. 이때 앱 데이터는 `<home>\.dotdeck`, 환경변수는 레지스트리 대신 `<home>\.dotdeck-environment.json`을 쓰고, hosts 등 중요 파일도 홈 아래 경로를 가리킵니다. `--data <폴더>`로 앱 데이터 위치만 따로 바꿀 수도 있습니다.

## 빌드·테스트·배포
```powershell
dotnet build DotDeck.slnx
dotnet test --solution DotDeck.slnx
dotnet publish src/DotDeck.App -c Release -r win-x64 -p:PublishSingleFile=true
```
배포 결과는 자체 포함(self-contained) 단일 `DotDeck.exe`입니다(.NET 런타임 설치 불필요).

## 데이터 위치
| 항목 | 위치 |
|---|---|
| 프로젝트 목록·설정·템플릿 | `%APPDATA%\DotDeck\` (`projects.json`, `settings.json`, `templates\`, `important-files.json`) |
| 백업·로그·레이아웃·세션 | `%LOCALAPPDATA%\DotDeck\` (`backups\`, `logs\`, `layout\`, `state\`) |

## 구조
- `src/DotDeck.App` — WPF 앱(MVVM, AvalonDock, AvalonEdit, WebView2)
- `src/DotDeck.Core` — UI 비의존 라이브러리(파일 작업·이력, 파서, 검증기, 검색, 환경변수 추상화)
- `tests/DotDeck.Core.Tests` — xUnit v3 테스트
- 문서: `docs/SPEC.md`(사양), `docs/DESIGN.md`(설계 결정), `CHANGELOG.md`

## 알려진 제한
- 라이트 테마에서도 구문 강조와 Markdown 미리보기 색은 다크 기준입니다.
- 폼에서 settings.json을 고치면 2칸 들여쓰기로 다시 직렬화되고 JSON 주석은 사라집니다.
- 비밀 이름의 환경변수는 값을 기록하지 않으므로 되돌리기 대상이 아닙니다.
