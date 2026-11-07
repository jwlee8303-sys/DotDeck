# DotDeck

개발 환경 설정 파일(~/.claude, ~/.codex, 환경변수 등)과 문서를 한 창에서 찾고·보고·수정하는 Windows 데스크톱 에디터. VS 2022 다크 테마 기반 도킹 UI. 사양: `docs/SPEC.md`.

## 스택
- .NET 10 (LTS), UI: WPF
- 패턴: MVVM (CommunityToolkit.Mvvm)
- 도킹 Dirkster.AvalonDock, 에디터 AvalonEdit, Markdown Markdig, Diff DiffPlex (확정은 Phase 0 설계 승인 후)
- 테스트: xUnit
- 배포: 단일 파일 publish

## 명령어
- 빌드: `dotnet build DotDeck.slnx`
- 테스트: `dotnet test DotDeck.slnx`
- 실행: `dotnet run --project src/DotDeck.App -- --home <테스트용 홈 폴더>`
- 배포: `dotnet publish src/DotDeck.App -c Release -r win-x64 -p:PublishSingleFile=true`

## 구조 (Phase 0 재구성 목표)
- `src/DotDeck.App`: WPF 앱 (`net10.0-windows`) — Shell(도킹), 탐색기, 에디터, 테마, ViewModel
- `src/DotDeck.Core`: UI 의존성 없는 라이브러리 (`net10.0`) — 프로젝트 모델, 파일 작업·이력, 파서, 검증기, 환경변수 추상화
- `tests/DotDeck.Core.Tests`: xUnit
- 솔루션 파일은 `.slnx` 유지

## 프로젝트 규칙
- Core는 WPF/Windows UI 타입을 참조하지 않는다. ViewModel에 View 타입 참조 금지.
- 파일 시스템·레지스트리·환경변수 접근은 `IFileStore`, `IEnvironmentStore` 등 인터페이스 뒤에 둔다. 테스트는 임시 폴더와 가짜 구현만 쓴다.
- **개발·테스트 중 실제 `~/.claude`, `~/.codex`, 환경변수, hosts 파일을 수정하지 않는다.** 수동 테스트는 `DOTDECK_HOME` 환경변수 또는 `--home <dir>`로 홈을 바꿔서 한다.
- 모든 쓰기는 diff 미리보기 → 백업(`%LOCALAPPDATA%\DotDeck\backups\`) → 원자적 쓰기(인코딩·BOM·줄바꿈 보존).
- 비밀 파일(`.env*`(example 제외), `*.pfx`, `*.pem`, `*.key`, `id_rsa*`, `id_ed25519*`)은 열지 않는다. 비밀값은 로그·백업에 평문으로 남기지 않는다.
- 저장 위치: 프로젝트 정의 `%APPDATA%\DotDeck\projects.json`, 템플릿 `%APPDATA%\DotDeck\templates\`, 로그 `%LOCALAPPDATA%\DotDeck\logs\`
- 색은 모두 리소스 키로 관리(다크/라이트 테마 전환).
- SPEC 3절 외 NuGet 패키지는 추가 전 사용자 승인.
- Phase마다: 빌드 → 테스트 → `/codex-review` → 커밋 → 보고 → 사용자 확인 후 다음 Phase.
- 버전: `Directory.Build.props` `<Version>` (SemVer, 태그 v접두사)
