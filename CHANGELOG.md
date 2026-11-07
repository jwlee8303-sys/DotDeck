# Changelog
이 프로젝트의 주요 변경사항을 기록합니다. 형식: Keep a Changelog, 버전: SemVer.

## [Unreleased]
### Added
- 프로젝트 초기 설정: CLAUDE.md, Directory.Build.props(버전 0.1.0), CHANGELOG.md, .gitignore
- Phase 0 설계안(`docs/DESIGN.md`)
- Core: 홈·데이터 경로 해석(`--home` > `DOTDECK_HOME` > 사용자 프로필, 홈 변경 시 데이터 폴더 격리, `--data`)
- Core: 파일 로그(일별 파일, 14일 보관, 비밀값 마스킹), 비밀 이름·값 마스킹(`SecretMasker`)

### Changed
- 솔루션을 `src/DotDeck.App`(WPF, net10.0-windows), `src/DotDeck.Core`(net10.0), `tests/DotDeck.Core.Tests`(xunit.v3) 구조로 재구성
- 중앙 패키지 관리, 경고를 오류로 처리, `.editorconfig`, 앱 매니페스트(asInvoker, PerMonitorV2, longPath) 추가
