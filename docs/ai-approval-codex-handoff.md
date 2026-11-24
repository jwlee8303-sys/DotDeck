# AI 승인 요청 기능: Codex 검증 인계

작성: 2026-11-23 · 구현: Claude Code · 검증 담당: Codex
개정: 2026-11-23, **범위 변경으로 자동 허용을 완전히 제거했다**(나중에 별도로 다시 검토). §7에 제거 내역을 적었다.

Claude Code가 한 확인은 `dotnet build DotDeck.slnx`(경고 0, 오류 0)와 승인 관련 테스트 실행까지다(§8). 수동 검증은 하지 않았다. 버전과 CHANGELOG는 수정하지 않았다.

## 1. 구현 전 실측 결과

스크래치 폴더에서 ConPTY 하네스로 실제 CLI를 실행해 확인했다. 저장소 코드에는 포함하지 않았다.

| 항목 | 결과 |
|---|---|
| Claude 2.1.330: 훅 대기 중 터미널 화면 | PermissionRequest 훅이 실행 중이어도 **터미널 승인 대화상자가 바로 함께 표시**된다. |
| Claude: 큰 timeout | `timeout: 3700`을 지정하고 650초 대기한 훅의 `allow`가 적용됐다. 기본값 600초를 넘는 값이 실제로 적용된다. 지금 값은 1830초(30분 + 30초)이며, 끝까지 기다려 측정하지는 않았다. |
| Claude: 대화상자가 떠 있을 때 훅이 `allow` 반환 | 대화상자가 닫히고 도구가 실행된다. |
| Claude: 훅 대기 중 터미널에서 "1"(Yes) | 도구는 실행되지만 **훅 프로세스는 종료되지 않는다**. 그래서 PostToolUse로 감지한다. |
| Claude: 훅 대기 중 터미널에서 Esc(거부) | **훅 프로세스가 종료된다**(파이프 끊김으로 감지). 이후 `allow`는 적용되지 않는다. |
| Codex 0.171.0 | "Would you like to run the following command?" / "1. Yes, proceed (y)" / "… (esc)". `y`는 허용, Esc는 취소로 확인했다. |
| agy 1.5.15 | "Allow creation of this file?" / "1. Yes, allow creation" / "2. No, deny creation". `1`은 허용, Esc는 거부와 턴 중단으로 확인했다. 명령 실행 프롬프트는 사용자 전역 agy 훅이 명령을 막아 보지 못했다. `2` 키는 시도하지 않았다. |

## 2. 변경·추가 파일

### Core (`src/DotDeck.Core`)
| 파일 | 역할 |
|---|---|
| `AiCli/Approvals/ApprovalRequest.cs` | 승인 요청 모델(고유 ID, 세션 키, CLI, 출처, 도구, 명령, 작업 폴더, 요청 시각, 응답 가능 여부, 대기 상한, 상태)과 상태·결정·응답자 enum |
| `AiCli/Approvals/ApprovalService.cs` | 요청 상태 관리와 응답 전달만 맡는다. 응답은 `RespondAsync` 한 경로만 거치고, 대기 상한이 지나면 터미널로 넘긴다. |
| `AiCli/Approvals/ApprovalSettings.cs` | 환경 설정 값(감지, CLI별, 화면 감지, 맨 위로, 알림 카드와 소리)과 `HookWaitLimit`(30분) |
| `AiCli/Approvals/IApprovalProvider.cs` | `IApprovalProvider`(CLI별 차이), `IApprovalChannel`(응답 통로), `IApprovalLog` |
| `AiCli/Approvals/ClaudeApprovalHook.cs` | 훅 입력 해석, 파이프 메시지, 훅 출력 JSON, `HookTimeoutSeconds`(1830), `ClaudeHookApprovalProvider` |
| `AiCli/Approvals/ScreenApprovalProvider.cs` | 화면 패턴 정의 로딩, 패턴 판정, CLI 버전별 응답 키 결정 |
| `AiCli/Approvals/ApprovalRecordLog.cs` | 세션 기록: 세션 폴더의 `approvals.jsonl`. 터미널 탭에서 직접 실행한 CLI는 앱 로그에 남기며 명령은 마스킹한다. |
| `Catalogs/Approvals/screen-prompts.json` | CLI별 화면 패턴과 키, 확인한 버전 |
| `AiCli/Launch/SessionFilesWriter.cs` | 대화형 Claude 세션의 세션 전용 settings.json에 PermissionRequest 훅(timeout 1830, `onFailure` 없음)과 PostToolUse 훅(timeout 10)을 추가한다. |
| `AiCli/Sessions/AiSessionManager.cs` | `StartApprovalHost`, `HasApprovalHooks`. 대화형 Claude만 훅을 주입하고, 세션이 끝나면 호스트를 정리한다. |
| `AiCli/Sessions/TerminalConnection.cs` | `RecentOutput()`: 구독하지 않고 재생 버퍼를 복사한다. |
| `Settings/AppSettings.cs` | `AiApproval` 설정 추가 |

### App (`src/DotDeck.App`)
| 파일 | 역할 |
|---|---|
| `Infrastructure/Windows/NamedPipeApprovalHost.cs` | 세션별 승인 파이프 서버(현재 사용자 전용, DPAPI 키 인증, 동시 연결 최대 8개)와 훅 클라이언트(`--dotdeck-approval`, `--dotdeck-approval-ran`) |
| `Program.cs` | 훅 진입점 분기 |
| `Infrastructure/Windows/ProcessInspector.cs` | `ImagePath()`: CLI 실행 파일 경로 조회(버전 확인용) |
| `Infrastructure/Windows/CliVersionReader.cs` | `<exe> --version` 실행(셸 없음, 5초 제한) |
| `ViewModels/Approvals/ApprovalCoordinator.cs` | 훅과 화면 감지를 서비스에 연결하고, 터미널 탭 넘김 규칙, 표시 갱신, 카드 대상 계산을 맡는다. |
| `ViewModels/Approvals/ApprovalItemViewModel.cs` | 세션별 승인 영역 VM(AI 모니터와 카드 공용) |
| `ViewModels/ShellViewModel.Approvals.cs` | 셸 연결 |
| `ViewModels/Tools/AiMonitorPaneViewModel.cs` | 승인 대기 정렬, "승인 대기 N건" 필터, 탭 배지, CLI 라벨, `Reveal` |
| `ViewModels/Tools/TerminalPaneViewModel.cs` | `ScreenReader`(화면 읽기) |
| `Views/Approvals/ApprovalPanel.xaml(.cs)` | 승인 영역 UserControl. 포커스가 안에 있을 때만 Y/N이 동작한다. |
| `Views/Approvals/ApprovalCardsWindow.xaml(.cs)` | 알림 카드 창(`WS_EX_NOACTIVATE`와 `WS_EX_TOOLWINDOW`, 작업 영역 오른쪽 아래, 최대 3장)과 `ApprovalCardsController` |
| `Views/Tools/TerminalScreenProbe.cs` | 보이지 않는 탭의 화면을 화면 밖 창의 xterm.js로 재생해서 읽는다(추정). |
| `Views/Tools/TerminalView.xaml.cs`, `Assets/Terminal/terminal.js` | 화면 읽기(`screen`/`probe` 메시지) |
| `Views/PaneTemplates.xaml` | AI 모니터 섹션: 테두리, CLI 라벨, 승인 영역, 상단 "승인 대기 N건" |
| `Views/Dialogs/SettingsDialog.xaml(.cs)` | 환경 설정의 "AI 승인 요청" 탭 |
| `App.xaml.cs` | 서비스·코디네이터·카드·프로브 생성, 활성 상태와 설정 반영, 종료 시 정리 |

`docs/FEATURES.md`에는 기능 설명과 "외부 창 CLI 미지원"을 추가했다.

## 3. 세션 종류별 실제 구현

| 세션 | 감지 | 허용/거부 |
|---|---|---|
| DotDeck 대화형 Claude | PermissionRequest 훅에서 Named Pipe로 알림 | 훅이 `allow`/`deny` 출력. 실패하면 출력 없음(CLI 프롬프트) |
| DotDeck 비대화형 Claude | 없음(`dontAsk`이고 훅도 주입하지 않음) | — |
| DotDeck Codex | 화면 감지 | 키 전송: 확인한 버전이고 탭이 보이며 직전 재확인을 통과한 경우만 |
| 터미널 탭에서 직접 실행한 Claude·Codex·agy | 화면 감지 | 위와 같음 |
| 외부 창 | 미지원(문서에 명시) | — |

**Claude 훅 대기 규칙(하나로 통일)**
- 요청 시점에 DotDeck이 활성이고 해당 세션의 터미널 탭이 선택·표시 상태면 훅을 즉시 결정 없이 끝낸다. 상태는 "터미널에서 응답 중"으로 바뀌고 대화상자가 응답을 받는다.
- 대기 중에 사용자가 그 터미널 탭으로 이동해도 같은 방식으로 넘긴다. 직접 이동한 경우와 [터미널로 이동]을 누른 경우 모두 해당하며, 1초 주기로 검사한다.
- 대기 상한은 30분(`ApprovalSettings.HookWaitLimit`)이다. 상한이 지나면 "대기 시간 초과 · 터미널에서 응답"으로 넘긴다.
- 훅 timeout은 1830초(대기 상한 + 30초)로 명시한다. 훅 클라이언트 자체 상한은 1810초다.
- 파이프 연결은 2초 안에 해야 한다. 대기 중 연결이 끊기면 훅은 출력 없이 끝낸다.
- 가드가 ask하는 호출도 일반 승인 대기로만 표시한다(별도 처리 없음).

**작업 전 보고와 달라진 점**
- 실측 결과 터미널 대화상자가 훅 대기 중에도 표시되므로, 넘김은 훅을 결정 없이 끝내는 동작이다(대화상자는 이미 떠 있음).
- 터미널에서 응답한 경우는 이렇게 감지한다. 허용은 PostToolUse(도구 이름과 입력 JSON 일치)로, 거부·Esc는 훅 프로세스 종료(파이프 끊김)로 감지한다. 넘긴 뒤 다음 요청이 오면 이전 요청은 "터미널에서 응답됨"으로 정리한다.
- 결정 3번에 따라 화면은 xterm.js로 읽는다. 다만 **보이지 않는 탭은 뷰(WebView)가 해제**되므로 화면 밖 프로브 창에 재생 버퍼(최대 1MB)를 다시 그려 읽는다. 이렇게 감지한 요청은 `CanRespond=false`라서 [터미널로 이동]만 둔다.

## 4. 특히 봐야 할 위험 지점

1. **Fail-safe**: `NamedPipeApprovalHost.RunRequestHookAsync`는 모든 예외에서 출력 없이 exit 0으로 끝나야 한다. `SessionFilesWriter`의 승인 훅에 `onFailure`가 없어야 한다(있으면 실패 시 거부됨). `ApprovalService.Dispose`는 남은 훅을 모두 `none`으로 해제한다.
2. **중복 응답**: `ApprovalService.RespondAsync`는 lock 안에서 `Pending`을 `Responding`으로 바꿔 한 번만 통과시킨다. `ApprovalHookConnection.WriteOnceAsync`는 첫 쓰기만 성공시킨다. 카드와 AI 모니터에서 동시에 응답하는 경우를 확인해야 한다.
3. **터미널 허용 후 감지 지연**: PostToolUse는 도구 **실행이 끝난 뒤** 온다. 오래 걸리는 명령을 터미널에서 허용하면 끝날 때까지 DotDeck에는 "대기"로 남는다. 이 사이에 [허용]이 늦게 전달되어 "DotDeck에서 허용"으로 잘못 기록되는 문제(Codex 지적 6번)는 이렇게 막는다.
   - 훅 요청이 도착할 때 세션 터미널의 입력 횟수(`TerminalConnection.InputCount`)를 기록해 둔다. 기록은 요청을 서비스에 등록하기 전에 하고, 1초 타이머는 기록 후 10초가 지났고 서비스에 요청이 더 이상 없을 때만 지운다(2차 검증에서 지적된 등록 전 삭제 경쟁 수정).
   - [허용]/[거부]를 누른 시점에 입력 횟수가 달라져 있으면 응답을 보내지 않고 "터미널에서 응답 중"으로 넘긴다(`ApprovalCoordinator.RespondAsync`).
   - 입력에는 키, 붙여넣기, 마우스·포커스 보고가 모두 포함된다. 따라서 응답하지 않고 터미널을 건드리기만 해도 넘어간다. 대화상자가 결정하게 되는 안전한 쪽이다.
4. **오탐(화면 감지)**: 패턴 일치와 출력 2초 정지를 둘 다 만족해야 한다.
   - 질문은 하단 30줄에서 마지막 질문 줄을 찾고, 선택지는 **그 질문 줄 다음 줄부터** 찾는다. 질문 줄 자체에 있는 선택지는 세지 않는다(Codex 지적 2번 수정, 2차 보완).
   - 키를 보내기 직전에 출력이 2초 이상 멈춰 있는지 확인하고, 라이브 화면을 다시 읽어 같은 지문인지, 읽는 동안 새 출력이 없었는지, 여전히 2초 정지 상태인지 확인한다(Codex 지적 4번 수정).
   - 재생 버퍼 렌더링은 프레임 중간부터 시작될 수 있어 부정확할 수 있다.
5. **CLI별 응답 키**: Claude `1`/Esc, Codex `y`/Esc, agy `1`/Esc. agy의 Esc는 턴 전체를 중단한다. 버전이 정확히 같을 때만 키를 보낸다. `ParseVersion`은 시험판·빌드 접미사(`0.171.0-beta.1`, `+build`)를 보존하므로, 시험판은 확인한 정식판과 같은 버전으로 보지 않는다(Codex 지적 3번 수정).
6. **설정 변경**: 감지를 끄면 대기 중인 요청이 모두 터미널로 넘어간다.
7. **PostToolUse 비용**: 세션 폴더에 `approval-pending` 표식이 있을 때만 파이프에 연결한다. 표식이 없으면 즉시 종료한다.

## 5. 단위 검증 항목(Codex)

- CLI별 화면 패턴 판정(`ScreenApprovalProvider.Match`, `KeysFor`, `CanAnswer`, `ParseVersion`)과 승인 요청 상태 전이(`ApprovalService`)
- IPC 타임아웃이나 DotDeck 장애 시 허용이 아니라 CLI 기본 프롬프트로 넘어가는지(Fail-safe: `RunRequestHookAsync` 출력 없음, `HookOutput("none")`이 null인지)
- 훅 대기 상한(30분)이 지나면 결정 없이 넘기는지, 훅 timeout(1830)이 대기 상한보다 긴지
- 이미 응답한 요청에 대한 중복 응답 차단(동시에 두 응답이 들어오는 경우 포함)
- **자동 허용 관련 코드와 설정 키가 남아 있지 않음**(§7의 검색으로 확인)
- `TerminalConnection.InputCount`가 `Send`와 `SendBinary`에서 증가하는지, 그리고 `ApprovalCoordinator.RespondAsync`가 입력 횟수가 바뀌었을 때 응답 대신 넘기는지(지적 6번 수정)

## 6. 수동 검증 체크리스트(Codex)

원 지시의 체크리스트에서 자동 허용 항목 3개("자동 허용 1분 설정", "제외 패턴", "[자동 허용 취소]")를 뺐다. 남은 항목 중 자동 허용을 언급하던 부분("타이머", "자동 허용 없음", "자동 허용 미적용 확인")도 지웠다.

- [ ] Claude 2개 + Codex 2개 + agy 1개 세션을 동시에 실행하던 중 1건이 승인 대기 → 해당 섹션에 승인 영역 표시, 맨 위로 이동, 탭 배지, 허용 동작
- [ ] DotDeck 활성 상태에서는 알림 카드가 뜨지 않음
- [ ] 다른 앱 사용 중(비활성)과 트레이 상태에서 알림 카드가 포커스를 빼앗지 않고 뜨며, [허용] [거부] [열기]가 동작함
- [ ] 알림 카드 기능을 끄면 비활성·트레이 상태에서도 카드가 뜨지 않음
- [ ] 터미널에서 직접 응답 → 승인 영역, 알림 카드 모두 정리
- [ ] DotDeck 강제 종료 중 승인 요청 → CLI 기본 프롬프트로 정상 동작 (3종 모두)
- [ ] 일반 PowerShell 탭에서 직접 실행한 Claude / Codex / agy의 대체 감지
- [ ] agy 승인 프롬프트에서 [허용] / [거부] 키 전송 결과가 의도대로 반영됨

추가로 확인할 것:
- [ ] DotDeck 활성 상태에서 세션 터미널 탭을 보고 있으면 훅이 즉시 넘어가 "터미널에서 응답 중"으로 바뀌고 버튼이 사라짐
- [ ] 대기 중에 그 탭으로 이동하면(직접 이동 또는 [터미널로 이동]) 같은 방식으로 넘어감
- [ ] 보이지 않는 터미널 탭(프로브)에서 감지한 요청은 [터미널로 이동]만 표시됨
- [ ] 터미널 대화상자에서 먼저 허용한 뒤(오래 걸리는 명령) AI 모니터에서 [허용]을 누르면 아무것도 보내지 않고 "터미널에서 응답 중"으로 바뀌며, 명령이 끝나면 "허용됨(터미널)"으로 기록됨
- [ ] 화면 감지 요청에서 출력이 계속 나오는 중에 [허용]을 누르면 키를 보내지 않음
- [ ] 세션 폴더 `approvals.jsonl` 기록 내용(누가·언제·무엇)과 비밀값 마스킹

## 7. 자동 허용 제거 내역

| 위치 | 제거·변경 내용 |
|---|---|
| `ApprovalSettings.cs` | `AutoAllow`, `AutoAllowMinutes`, `AutoAllowExclusions`, `EffectiveExclusions`, `AutoAllowDelay`, 기본 제외 목록, 최소·최대 분, `AutoAllowGrace`, `Normalize`, `ApprovalExclusions` 클래스(패턴 판정)를 제거했다. `ManualWaitLimit`은 `HookWaitLimit`으로 이름을 바꿨다. |
| `ApprovalRequest.cs` | `AutoAllowEligible`, `GuardAsked`, `ExcludedBy`, `AutoAllowAt`, `HookDeadline`, `BeyondHookDeadline`, 상태 `AutoAllowed`, 응답자 `Auto`를 제거했다. |
| `ApprovalService.cs` | 자동 허용 타이머, `CancelAutoAllow`, `AutoAllowCancelled` 이벤트, 제외 패턴 판정, 자동 응답 경로를 제거했다. `Tick`은 대기 상한만 처리한다. |
| `ClaudeApprovalHook.cs` | 가드 재평가 결과(`guardAsk`) 메시지 필드, `TimeoutFor`/`DeadlineFor`/`TimeoutMargin`을 제거했다. timeout은 1830으로 고정했다. |
| `IApprovalProvider.cs`, `ScreenApprovalProvider.cs` | `SupportsAutoAllow`를 제거했다. |
| `Program.cs`, `NamedPipeApprovalHost.cs` | 훅의 가드 재평가(`GuardAsks`)를 제거했다. |
| `SessionHookSettings.cs`, `SessionFilesWriter.cs`, `AiSessionManager.cs`, `App.xaml.cs` | 세션별 승인 훅 timeout(`ApprovalTimeoutSeconds`, `ApprovalHookTimeoutSeconds`)을 제거하고 고정값을 쓴다. |
| `ApprovalItemViewModel.cs` | 카운트다운, [자동 허용 취소], [터미널에서 응답] 명령, "자동 허용 제외" 같은 안내 문구를 제거했다. 안내는 "DotDeck에서 응답할 수 없는 이유"만 남겼다. |
| `ApprovalCoordinator.cs` | 자동 허용 취소 처리, 터미널 막대 갱신을 제거했다. 넘김 규칙은 모든 훅 요청에 적용한다. |
| `ApprovalPanel.xaml(.cs)` | 카운트다운, [자동 허용 취소], [터미널에서 응답], `ShowHandOff`를 제거했다. |
| `TerminalView.xaml`, `TerminalPaneViewModel.cs` | 터미널 탭 위 승인 막대와 `Approval` 속성을 제거했다. |
| `AiMonitorPaneViewModel.cs`, `PaneTemplates.xaml` | "자동 허용 켜짐 · N분" 표시를 제거했다. |
| `SettingsDialog.xaml(.cs)` | 자동 허용 켜기/끄기, 대기 시간(분), 제외 명령 목록과 그 검증을 제거했다. |
| `SessionHooksViewModel.cs` | 자동 허용에 따라 바뀌던 timeout 표시를 고정 표시("1830 (대기 상한 30분 + 30초)")로 바꿨다. |
| `tests/…/ApprovalTests.cs`(Codex 작성) | 자동 허용 전용 테스트를 삭제했다: 타이머, 취소와 끄기, 제외 패턴 3건, 가드 ask, 버튼과 타이머 동시 응답. 같은 영역을 확인하도록 "대기 상한 도달 시 넘김"과 "동시 응답 시 한 번만 전송"으로 바꿨다. |
| `tests/…/ApprovalHookTests.cs`(Codex 작성) | `RunRequestHookAsync`의 가드 인자를 제거했다. |

남은 키워드 확인 명령(결과가 없어야 한다. `hook-events.json`의 Claude 이벤트 예시 문구 "자동 허용"은 이 기능과 무관한 카탈로그 설명이다):

```
rg -n -i "autoallow|auto_allow|자동 허용|GuardAsk|ExcludedBy|ApprovalExclusions|HookDeadline|CancelAutoAllow|AutoAllowed" src tests --glob "!**/bin/**" --glob "!**/obj/**"
```

## 8. Codex 1차 검증 결과와 현재 상태

| Codex 지적 | 현재 상태 |
|---|---|
| 1. 긴 명령에서 자동 허용 제외 패턴 누락(P1) | 자동 허용을 제거해 해당 없음 |
| 2. 서로 떨어진 질문과 선택지를 프롬프트로 감지(P2) | **수정**: 선택지는 마지막 질문 줄 아래에서만 찾는다(`ScreenApprovalProvider.Match`). 해당 테스트 통과 |
| 3. 시험판 버전에도 응답 키 활성화(P2) | **수정**: `ParseVersion`이 접미사를 보존한다. 해당 테스트 통과 |
| 4. 키 전송 직전 2초 정지 재검사 누락(P2) | **수정**: `ScreenChannel.SendAsync`가 화면을 읽기 전과 후에 모두 2초 정지를 확인한다. 단위 테스트는 없다(WPF·WebView 경로) |
| 5. `TerminalView` 테스트의 `DD.Text.Secondary` 리소스 오류(P2) | 터미널 승인 막대를 제거해 해소. `AiTerminalPane` 테스트 통과 |
| 6. 터미널에서 허용한 요청이 DotDeck 허용으로 기록될 수 있음(P2) | **수정**: 요청 이후 터미널 입력이 있으면 넘긴다(§4-3). 단위 테스트는 없다 |

수정 후 실행: `dotnet test --filter "Approval|AiTerminalPane|TerminalConnection"` → 41건 모두 통과. 필터에 걸린 테스트가 없는 IconGen 프로젝트 때문에 종료 코드는 8이다.

### 2차 검증 지적과 수정(2026-11-24)

| 지적 | 수정 |
|---|---|
| P2: 질문 줄에 선택지가 같이 있으면 프롬프트로 판정 | 선택지는 질문 다음 줄부터 찾는다. 테스트 `ScreenPromptOptionsOnTheQuestionLineDoNotFormAnApprovalPrompt` 추가 |
| P2: 입력 기준값이 요청 등록 전에 타이머에 의해 삭제될 수 있음 | 기록 시각을 함께 저장하고, 10초 유예 후 서비스에 요청이 없을 때만 삭제 |
| P3: 자동 허용·타이머를 설명하던 주석 | `App.xaml.cs`, `ApprovalCoordinator.cs`, `ClaudeApprovalHook.cs`, `ScreenApprovalProvider.cs` 주석 정리 |

수정 후 전체 `dotnet test`: 1,690건 중 성공 1,687, 실패 0, 건너뜀 3.
