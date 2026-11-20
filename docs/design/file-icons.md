# 파일 아이콘 시스템 설계 (기준 문서)

> 2026-11-19 승인. SPEC.md가 없으므로 이 문서가 파일 아이콘 작업의 기준이다. 사용 방법은 `docs/icons.md`.

## 범위
- 탐색기 트리와 문서 탭에 파일 종류별 컬러 아이콘. UI 아이콘(메뉴·툴바·패널)은 Segoe Fluent Icons 단색 유지.
- 원본: npm `material-icon-theme@5.40.0` (MIT), integrity
  `sha512-IrvgusbwtqxhUtVgUy0nToJcfN7CYVRdbBxKMfbo0vab9flVBHA5INZ9UiE5SgRj+PM6opCl5nn5TZdpm2UaxA==`.
  vscode-icons, "Material Theme Icons" 등 다른 세트는 섞지 않는다.
- 비목표: 아이콘 테마 전환 UI, 사용자 SVG(`custom:` 키는 예약만), 내용 기반 언어 판별, 트리 행별 검증 오류.

## 결정 사항
| 항목 | 결정 |
|---|---|
| 기본 제공 agy 프로젝트 | "Gemini 전역" → "agy 전역"(`builtin.agy`, 루트 `~/.gemini`). 옛 ID는 읽을 때 이전 |
| 생성 도구 위치 | `build/icons/` (기존 앱 아이콘 생성 도구와 같은 위치, 배포물과 분리) |
| SVG → XAML | 외부 라이브러리 없이 직접 구현. 미지원 요소가 있으면 실패 보고 + 기본 아이콘 |
| 지원 SVG 기능 | path, rect, circle, ellipse, g, defs, use, linearGradient, radialGradient, stop, clipPath / fill, fill-rule, clip-rule, opacity, fill-opacity, stroke 계열, transform, style(인라인 속성), viewBox (5.40.0 스캔 결과 기준) |
| 저장 형식 | 아이콘 하나당 `<key>.xaml`(DrawingImage) EmbeddedResource, 처음 쓸 때 로드 + Freeze + 캐시. 측정 기준(첫 화면 아이콘 로드 50ms 이상 또는 1,000개 폴더 펼침 20% 이상 회귀)을 넘으면 단일 팩으로 전환 |
| Material SVG 원본 | 커밋하지 않음(고정 버전 + integrity로 재현). DotDeck 전용 SVG만 커밋 |
| 브랜드 제외 | `build/icons/file-icon-exclude.json`에 AI 도구 브랜드 + Microsoft·Visual Studio 계열을 처음부터 등록. 항목마다 대체 키(`replaceWith`)를 명시 |
| 서드파티 고지 | 기존 `THIRD-PARTY-NOTICES.txt` 확장. 정보 대화상자에서 읽기 전용으로 표시 |
| 교차 검증 | `/codex-review` 대신 `codex:rescue`(Codex 에이전트) 사용 |

## 해석 순서 (Core `DotDeck.Core.Icons`)
`Resolve(name, isFolder, isExpanded, theme, IconScope? scope)` → 아이콘 키 문자열.
1. 사용자 매핑 `%APPDATA%\DotDeck\icons\associations.json` (`AppPaths.IconAssociationsFile`, 경로 조건 없음)
2. DotDeck 전용 매핑 `dotdeck-icons.json` — 흔한 이름(agents, hooks, rules, config, settings.json 등)은 `scope`가 있을 때만
3. Material: 파일명 정확 일치 → 확장자 긴 것부터(`a.d.ts` → `d.ts` → `ts`) → `file` / 폴더명 → 열림·닫힘 → `folder`·`folder-open`. Light 테마면 `light` 절 우선
- 대소문자 무시. 점 파일은 파일명 매핑. languageIds는 확장자 표에서 못 찾을 때만 보조로 사용.
- 잘못된 매핑은 로드할 때 한 번 경고(`IAppLog.Warning` → 출력 패널)하고 그 항목만 버린다.
- 매핑은 불변 스냅샷으로 만들고 다시 읽을 때 통째로 교체한다. 결과는 이름(+범위) 기준으로 캐시한다.

## 범위(IconScope) 판정
- `AppPaths.ClaudeHome`, `CodexHome`, `AntigravityHome` 아래 → 해당 도구. 그 밖에서는 가장 가까운 조상 `.claude`·`.codex`·`.gemini`·`.agents` 폴더.
- 경로 문자열만 비교한다(IO 없음). agy 하위 경로는 코드가 아니라 `dotdeck-icons.json` 데이터에 둔다.

## 전용 아이콘 디자인
- 원본 SVG를 새로 만든다. 제3자 로고를 쓰거나 흉내 내지 않고, 별·반짝임 기호를 쓰지 않는다.
- Claude Code = 주황 + 원, Codex = 초록 + 사각형, agy = 파랑 + 삼각형, 공유(AGENTS.md·SKILL.md) = 회색 + 연결 노드. 도구 루트 폴더도 같은 색 체계.
- Material과 같은 16×16 격자, 비슷한 여백과 선 굵기.

## App
- `FileIconProvider`: 키 → DrawingImage(지연 로드, Freeze, 캐시). `FileIconKeyConverter`.
- 트리 행 `IconKey`는 지연 계산(가상화된 행만). 16×16, 행 높이 22px 유지.
- 배지: 탭 = 미저장 ●, 활성 문서 검증 오류 ⚠ / 트리 = 비밀 파일, 폴더 열기 오류 ⚠, 열린 미저장 버퍼 ●. 동작은 바꾸지 않는다.
- 가상 노드(환경 변수, PATH)는 기존 Segoe 글리프 유지.
