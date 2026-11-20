# 파일 아이콘

탐색기 트리와 문서 탭에 파일 종류별 컬러 아이콘을 표시한다. 메뉴·도구 모음 같은 UI 아이콘은 계속 Windows 글꼴(Segoe Fluent Icons)을 쓴다.

## 구성

| 위치 | 역할 |
|---|---|
| `build/icons/update-file-icons.ps1` | npm `material-icon-theme`을 받아 SVG를 XAML로 변환하고 매핑을 만든다(멱등). |
| `build/icons/file-icon-exclude.json` | 상표·로고 아이콘 제외 목록. 제외된 키는 같은 색 계열의 일반 아이콘으로 대체된다. |
| `src/DotDeck.App/Assets/FileIcons/icons/*.xaml` | 생성된 아이콘(직접 고치지 않는다). |
| `src/DotDeck.App/Assets/FileIcons/material-icons.json` | 생성된 Material 매핑. |
| `src/DotDeck.App/Assets/FileIcons/dotdeck/*.svg` | DotDeck 전용 원본 SVG(직접 관리). |
| `src/DotDeck.App/Assets/FileIcons/dotdeck-icons.json` | DotDeck 전용 매핑(직접 관리). |
| `src/DotDeck.App/Assets/FileIcons/SOURCE.md` | 출처·버전·무결성·변환 결과(생성). |
| `src/DotDeck.Core/Icons/` | 아이콘 키를 정하는 UI 없는 리졸버. |
| `src/DotDeck.App/Themes/FileIcons.cs` | 이미지 지연 로드·Freeze·캐시, 사용자 매핑 감시. |

## 키 결정 순서

1. 사용자 매핑 `%APPDATA%\DotDeck\icons\associations.json`
2. DotDeck 매핑(`dotdeck-icons.json`): 파일은 고유 파일 이름 → 도구 폴더 안 규칙(scoped), 폴더는 도구 폴더 안 규칙 → 고유 폴더 이름
3. Material: 파일 이름 → 가장 긴 확장자 → 확장자로 정한 언어 ID → 기본 아이콘 / 폴더 이름(열림·닫힘) → 기본 폴더

이름은 대소문자를 구분하지 않고, `.gitignore` 같은 점 파일은 이름으로 찾는다. 잘못된 항목은 로그에 경고를 남기고 다음 단계로 넘어간다.

## DotDeck 전용 아이콘

제3자 로고·별·반짝임 모양을 쓰지 않은 자체 도형이다.

| 도구 | 색 | 문양 | 예 |
|---|---|---|---|
| Claude Code | 주황 | 원 | `CLAUDE.md`, `.claude/` |
| Codex | 초록 | 사각형 | `.codex/`, Codex 폴더의 `config.toml` |
| agy(Antigravity CLI) | 파랑 | 삼각형 | `GEMINI.md`, `.gemini/` |
| 공유 | 회색 | 연결된 노드 | `AGENTS.md`, `SKILL.md`, `.agents/` |

`settings.json`·`config.toml`·`agents`·`hooks`처럼 흔한 이름은 해당 도구의 설정 폴더(작업 폴더의 `.claude` 등, 홈의 `~/.claude` 등) 안에서만 전용 아이콘이 된다.

## 사용자 매핑

`%APPDATA%\DotDeck\icons\associations.json` (1,048,576자 이하):

```json
{
  "fileExtensions": { "tpl": "html" },
  "fileNames": { "Makefile.local": "makefile" },
  "folderNames": { "infra": "folder-server" }
}
```

- 쓸 수 있는 항목은 `fileExtensions`·`fileNames`·`folderNames`뿐이다. 폴더를 펼치면 지정한 키의 `-open` 아이콘(있을 때)을 자동으로 쓴다.
- 값은 이미 있는 아이콘 키만 쓸 수 있다(`src/DotDeck.App/Assets/FileIcons/icons`의 파일 이름). `custom:`으로 시작하는 키는 예약되어 무시된다.
- 파일을 저장하면 바로 다시 읽는다. 저장 중 잠겨 있으면 이전 매핑을 유지하고 잠시 뒤 다시 시도한다.
- JSON이 깨졌거나 키가 없으면 경고 후 그 항목(또는 파일)을 무시한다.

## 표식

| 위치 | 표식 | 의미 |
|---|---|---|
| 탭 | ● | 저장하지 않은 변경(기존) |
| 탭 | ⚠ | 검사 경고·오류가 있는 문서 |
| 트리 | ● | 열려 있고 저장하지 않은 파일 |
| 트리 | 자물쇠 | 비밀 정보 파일(기존) |

## 아이콘 갱신

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build/icons/update-file-icons.ps1
```

버전은 스크립트 안에 고정되어 있다. 올릴 때는 버전과 무결성 값을 함께 바꾸고, `SOURCE.md`·`THIRD-PARTY-NOTICES.txt`의 버전도 맞춘 뒤 브랜드 제외 목록에 새 로고 아이콘이 없는지 확인한다.

## 라이선스

- Material Icon Theme: MIT. 라이선스 원문은 `licenses/LICENSE-material-icon-theme.txt`로 배포물에 함께 들어간다.
- vscode-icons, "Material Theme Icons"는 쓰지 않는다.
- 모든 고지는 `THIRD-PARTY-NOTICES.txt`에 있고 도움말 → DotDeck 정보에서 볼 수 있다.

## 성능(참고 측정)

| 항목 | 결과 |
|---|---|
| 매핑 초기화(Material JSON 파싱) | 약 35ms |
| 첫 화면 아이콘 40개(첫 XAML 파서 준비 포함) | 약 51ms, 준비 후 약 7ms |
| 서로 다른 아이콘 1,000개 | 약 51–62ms |
| 흔한 이름 1,000개 | 약 22ms |
