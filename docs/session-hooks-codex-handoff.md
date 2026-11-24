# AI 세션 훅 관리: Codex 검증 인계

작성: 2026-11-23 · 구현: Claude Code · 검증 담당: Codex

Claude Code는 `dotnet build DotDeck.slnx`(경고 0, 오류 0)만 확인했다. 테스트 작성·실행과 수동 검증은 하지 않았다. 버전과 CHANGELOG는 수정하지 않았다.
"AI 승인 요청"(`docs/ai-approval-codex-handoff.md`)과 일부 파일이 겹친다. 그 기능에서 자동 허용을 제거하면서(2026-11-23 범위 변경) 이 문서의 자동 허용·timeout 계산 관련 내용도 함께 정리했다.

## 1. 확정한 설계(사용자 승인)

- **저장 위치**: `AppSettings.SessionHooks`에 `Guard`(기본 true), `Approval`(기본 true), `UserHooks`를 둔다. `UserHooks`는 Claude settings.json의 `hooks`와 같은 형식이고 null이면 없음이다.
- **병합**: 이벤트마다 DotDeck 기본 그룹을 먼저 두고, 사용자 그룹을 같은 이벤트 배열 뒤에 **별도 그룹으로 추가**한다(복사본). 기존 그룹을 합치거나 바꾸지 않는다.
- **하트비트(SessionStart)**: 항상 포함되고 끌 수 없다. 세션 설정이 적용됐는지 확인하는 장치다.
- **가드 끄기**: PreToolUse 가드 훅만 뺀다. `permissions`(비밀 파일 deny, 범위 밖 쓰기·읽기 차단)와 scope.json은 그대로 둔다.
- **승인 훅 끄기**: `HasApprovalHooks=false`가 되어 해당 세션은 화면 감지로만 동작한다.
- **승인 훅 timeout**: 1830초(대기 상한 30분 + 30초)로 고정이며 읽기 전용으로 표시한다.
- **훅 테스트**: 환경 설정 창이 모달이므로 ▷를 누르면 별도 모달 `HookTestWindow`가 열린다. 이 창은 기존 `HookTestPaneViewModel`과 템플릿을 그대로 쓰고, 실행 전에 확인을 받는다.
- **전역 훅**: `ClaudeHome\settings.json`을 **읽기만** 한다. 명령은 `SecretMasker`로 가려서 보여 준다.
- **환경 설정 창**: 900×680 크기로 바꾸고 크기 조절이 되게 했다. 탭은 일반, 백업, 탐색기, AI 승인 요청, AI 세션 훅이다.

## 2. 변경·추가 파일

| 파일 | 역할 |
|---|---|
| `src/DotDeck.Core/AiCli/Launch/SessionHookSettings.cs` (신규) | `SessionHookSettings`(저장 값)와 `SessionHookPlan`(한 세션에 쓸 훅 계획) |
| `src/DotDeck.Core/AiCli/Launch/SessionFilesWriter.cs` | `BuildSettings`/`Write`에 `SessionHookPlan` 오버로드를 추가했다(기존 bool 오버로드는 유지). 가드를 선택할 수 있게 했고, timeout 지정과 `AppendUserHooks` 병합을 넣었다. |
| `src/DotDeck.Core/AiCli/Sessions/AiSessionManager.cs` | 환경에 `SessionHooks`와 `ApprovalHookTimeoutSeconds`를 추가했다. 세션에는 `GuardEnabled`, `HasUserHooks`, `ApprovalHookTimeoutSeconds`를 기록한다. 설정은 세션을 시작할 때 한 번만 읽는다. |
| `src/DotDeck.Core/Settings/AppSettings.cs` | `SessionHooks` 속성과 Normalize |
| `src/DotDeck.App/ViewModels/Dialogs/SessionHooksViewModel.cs` (신규) | 기본 훅 표, 켜기/끄기, 사용자 훅(`HookTableViewModel` 재사용), 미리보기, 전역 훅, 기본값 되돌리기 |
| `src/DotDeck.App/Views/Dialogs/SettingsDialog.xaml(.cs)` | 탭 구성(일반, 백업, 탐색기, AI 승인 요청, AI 세션 훅) |
| `src/DotDeck.App/Views/Dialogs/HookTestWindow.xaml(.cs)` (신규) | 모달 창에서 쓰는 훅 테스트 창 |
| `src/DotDeck.App/Services/IDialogService.cs`, `Infrastructure/Windows/DialogService.cs` | `ShowSettings`에 `SessionHooksViewModel` 인자를 추가했다. |
| `src/DotDeck.App/ViewModels/ShellViewModel.System.cs` | 설정 창을 열 때 `SessionHooksViewModel`을 만든다. |
| `src/DotDeck.App/App.xaml.cs` | 현재 `SessionHooks`를 세션 관리자에 전달한다(설정이 바뀌면 갱신). |
| `src/DotDeck.App/ViewModels/Tools/AiMonitorPaneViewModel.cs`, `Views/PaneTemplates.xaml` | `IsGuardOff`와 "가드 꺼짐" 배지 |
| `docs/FEATURES.md` | 기능 설명 |

## 3. 검증 포인트

### 가드 끄기 반영
- [ ] 가드를 끄고 새 Claude 세션을 시작하면 settings.json에 PreToolUse 가드가 없고 SessionStart, permissions, scope.json은 그대로 있다. 세션은 하트비트로 정상 시작된다.
- [ ] 그 세션의 AI 모니터 행에 "가드 꺼짐" 배지가 보인다. 가드가 켜진 세션과 Codex 세션에는 보이지 않는다.
- [ ] 승인 훅을 끄고 시작한 대화형 Claude 세션은 settings.json에 PermissionRequest와 PostToolUse가 없고, 승인 요청은 화면 감지로 표시된다.

### 훅 병합 결과(단위 테스트 권장: `SessionFilesWriter.BuildSettings` 또는 `AppendUserHooks`)
- [ ] 사용자 PreToolUse 그룹은 가드 그룹 **뒤에** 별도 그룹으로 붙고, 가드 그룹(matcher `*`, onFailure block)은 바뀌지 않는다.
- [ ] 사용자 PermissionRequest와 PostToolUse도 승인 훅 뒤에 붙는다. 사용자 SessionStart도 하트비트를 대체하지 않는다.
- [ ] DotDeck이 쓰지 않는 이벤트(예: Stop)는 새 배열로 추가된다.
- [ ] 배열이 아닌 값이나 빈 이벤트 이름은 건너뛴다. 사용자 JSON을 고쳐도 이미 만든 settings에는 반영되지 않는다(DeepClone).
- [ ] 훅 표가 다루지 않는 필드(http/prompt 형식, `args`, 알 수 없는 필드)가 저장 후 다시 열어도 보존된다.
- [ ] 미리보기는 실제로 쓰는 `BuildSettings` 결과와 같다. 다른 점은 세션 ID 자리(`{세션 ID}`)와 프로젝트가 없는 예시 범위라서 permissions 차단 경로가 적다는 것이며, 창에 그렇게 안내한다(2차 검증 P3 반영).
- [ ] 사용자 PermissionRequest 훅이 있으면 경고 문구가 표시된다. 문구는 "승인 결과에 영향을 줄 수 있음"이며 결합 방식은 미확인이라고 밝힌다(2차 검증 P3 반영).

### 실행 중 세션에 영향 없음
- [ ] 세션 실행 중에 가드나 승인 훅을 끄거나 사용자 훅을 추가해도, 그 세션의 settings.json과 `GuardEnabled`, `HasApprovalHooks` 값은 바뀌지 않는다. 다음 세션부터 반영된다.
- [ ] 승인 훅 timeout은 1830이고 `ApprovalSettings.HookWaitLimit`(30분)보다 길다.

### 설정 창
- [ ] 탭 다섯 개에 기존 항목이 모두 있고 저장·검증(글꼴, 백업, 확장자)이 그대로 동작한다. 자동 허용 관련 입력이 없다.
- [ ] 기본값으로 되돌리기: 사용자 훅이 있으면 확인을 받고, 두 기본 훅을 켜고 사용자 훅을 지운다.
- [ ] ▷ 훅 테스트: 모달 창이 열리고, 실행 전에 확인을 받으며, 창을 닫으면 실행 중인 훅이 중지된다.
- [ ] 전역 settings.json이 없거나 깨졌을 때 안내 문구만 나오고 창은 정상 동작한다. 전역 파일은 수정되지 않는다(수정 시각으로 확인).
- [ ] 라이트·다크 테마 모두 표시가 정상이다.

## 4. 미검증·주의

- 사용자 PermissionRequest 훅이 DotDeck 승인 훅과 함께 실행될 때 Claude가 결정을 어떻게 합치는지(먼저 끝난 쪽인지, deny가 우선하는지)는 실측하지 않았다. 지금은 경고 문구만 표시한다.
- 사용자 훅은 DotDeck이 검증하지 않고 그대로 넣는다(명령의 안전성은 사용자 책임). 사용자 PreToolUse 훅이 `allow`를 반환해도 가드의 deny가 우선하는지 실측이 필요하다.
