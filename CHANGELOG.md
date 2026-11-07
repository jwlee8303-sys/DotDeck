# Changelog
이 프로젝트의 주요 변경사항을 기록합니다. 형식: Keep a Changelog, 버전: SemVer.

## [Unreleased]
### Added
- 프로젝트 초기 설정: CLAUDE.md, Directory.Build.props(버전 0.1.0), CHANGELOG.md, .gitignore
- Phase 0 설계안(`docs/DESIGN.md`)
- Core: 홈·데이터 경로 해석(`--home` > `DOTDECK_HOME` > 사용자 프로필, 홈 변경 시 데이터 폴더 격리, `--data`)
- Core: 파일 로그(일별 파일, 14일 보관, 비밀값 마스킹), 비밀 이름·값 마스킹(`SecretMasker`)
- App: AvalonDock 5 도킹 셸(메뉴·툴바·상태줄, 프로젝트·속성·출력·검증·검색·훅 테스트 패널, 시작 페이지), VS2022 다크 도킹 테마
- App: 레이아웃 저장(종료 시)·복원(시작 시)·초기화, 출력 패널에 앱 로그 표시, 샌드박스 모드 표시
- App: VS2022 다크 테마 리소스(`DD.*` 키 체계: 팔레트·글꼴·밀도), 메뉴·툴바·스크롤바·입력·목록·상태줄·툴팁 스타일, AvalonDock 색 정렬, 다크 타이틀바, 샌드박스 모드 주황색 상태줄
- Core: 파일 저장소 추상화(`IFileStore`, 덮어쓰지 않는 이동·복사, 정션 순환 방지, 원자적 쓰기, 엄격한 UTF-8 읽기, 프로세스 간 잠금)
- Core: 프로젝트 레지스트리(`projects.json` 스키마 v1, 기본 프로젝트 3종, 추가·삭제·이름·루트·색·필터·숨김·순서·제외 폴더, 알 수 없는 필드·항목 보존, 손상 파일 보존 후 복구, 새 스키마·읽기 불가 시 읽기 전용, 다중 인스턴스 충돌 감지)
- Core: 탐색기 노드 모델과 프로젝트 소스(폴더·시스템 가상 노드), 표시 필터(모든 파일 / 설정·문서만), 제외 폴더(이름·루트 기준 경로), 비밀 파일 판별, 파일 종류 분류
- App: 프로젝트 영역 — 모든 프로젝트를 하나의 가상화 목록에 섹션으로 표시(자동 열기), 백그라운드 트리 로딩, 키보드(←/→/Enter)·더블클릭·펼침 화살표, 헤더 툴바(설정·문서 필터 전환, 모두 접기, 새로고침, 루트 경로 복사, 탐색기에서 열기), 선택 경로 breadcrumb, "폴더 없음 — 만들기"
- Core/App: 탐색기 펼침·선택·접힘 상태를 `state\session.json`에 저장하고 시작 시 복원(사라진 폴더 정리)
- App: 프로젝트 추가·편집 대화상자(이름·루트·색·필터), 제거(확인), 숨김·숨긴 프로젝트 표시, 위·아래 순서 이동
- App: 프로젝트 탐색기를 별도 도구 패널로 떼어내기·다시 붙이기(세션에 저장·복원)
- Core: `ProjectRegistry.Update` — 이름·루트·색·필터를 한 번에 검증·저장
- Core: 파일 작업 서비스(새 파일·새 폴더·이름 바꾸기·이동·복사·복제·휴지통 삭제)와 실행 취소·다시 실행 이력(세션, 최대 100개). 사전조건 검사로 덮어쓰기 방지, 영구 삭제 없음, Windows 이름 규칙 검사, "복사본" 이름
- Core: 휴지통 `$I` 메타데이터 파서(v1·v2), App: `SHFileOperationW` 기반 휴지통 보내기와 `$R` 이동 복원

### Fixed
- 공용 ListBox 템플릿에서 UI 가상화가 꺼져 있던 문제(`CanContentScroll` 미전달)

### Changed
- 솔루션을 `src/DotDeck.App`(WPF, net10.0-windows), `src/DotDeck.Core`(net10.0), `tests/DotDeck.Core.Tests`(xunit.v3) 구조로 재구성
- 중앙 패키지 관리, 경고를 오류로 처리, `.editorconfig`, 앱 매니페스트(asInvoker, PerMonitorV2, longPath) 추가
- 레지스트리 쓰기(필터 전환 등)를 UI 스레드 밖에서 직렬화해 실행
- `projects.json`·`session.json`에 한글 등 비ASCII 문자를 이스케이프하지 않고 저장
