# Claude Usage Monitor for Windows

Claude Code CLI 사용자의 **5시간/주간 사용률**(공식 수치)과 **일/주/월 토큰·USD 비용**(로컬 집계)을
표시하는 Windows 트레이/위젯/대시보드 앱.

## 기능

- 시스템 트레이 아이콘: 5시간 사용률 링 + 우클릭 메뉴(새로고침 / 임계값 / 대시보드 / 표시 모드 / 자동 시작 / 종료)
- **Taskbar 모드**: 작업표시줄 위 도킹 미니바 — 5시간·주간 게이지 + 리셋 카운트다운 동시 표시
- **Floating 모드**: 드래그 가능한 항상-위 위젯 (위치 기억)
- 대시보드: 사용률 게이지, 현재 사용 중인 프로젝트(라이브 세션), 일/주/월 토큰 차트 + USD 비용 라인, 모델별 분해
- 5시간 사용률 임계값 경고 알림 (리셋 윈도우당 1회)
- 전체화면 앱 실행 시 위젯 자동 숨김, 단일 인스턴스

## 데이터 소스

| 데이터 | 소스 |
|---|---|
| 5시간/주간 % | `api.anthropic.com/api/oauth/usage` (로컬 CLI 로그인 토큰 사용, 180초+ 폴링) |
| 토큰/비용 통계 | `~/.claude/projects/**/*.jsonl` 증분 파싱 + 자체 롤업(`%LOCALAPPDATA%\ClaudeUsageMonitor`) |
| 가격 | LiteLLM 가격 DB(7일 캐시) + 내장 폴백 테이블 |

이 앱은 **OAuth 토큰을 절대 갱신(refresh)하지 않으며**, 토큰을 저장/로깅하지 않습니다.
Claude Code가 30일(기본) 지난 로그를 자동 삭제하므로, 월간 통계는 앱 자체 롤업으로 보존됩니다
(최초 실행 시점 이전 데이터는 복원 불가 — "집계 시작" 라벨 표시).

## 요구 사항

- Windows 10/11, Claude Code CLI 로그인 상태 (`~/.claude/.credentials.json`)
- 개발: .NET 10 SDK

## 빌드 / 테스트 / 실행

```powershell
dotnet build ClaudeUsageMonitor.slnx
dotnet test tests\ClaudeUsageMonitor.Core.Tests
dotnet run --project src\ClaudeUsageMonitor.App           # 트레이로 시작
dotnet run --project src\ClaudeUsageMonitor.App -- --dashboard  # 대시보드 즉시 열기
```

## 배포 (무설치 단일 exe)

```powershell
.\publish.ps1
# → artifacts\publish\ClaudeUsageMonitor.App.exe (~70MB, .NET 설치 불필요)
```

## 구조

- `src/ClaudeUsageMonitor.Core` — 엔진 (JSONL 인제스트/중복 제거/롤업, usage API, 가격). UI 무의존
- `src/ClaudeUsageMonitor.App` — WPF UI (트레이/위젯/대시보드/설정)
- `tests/` — xUnit (실측 로그 기반 픽스처)

상세 설계: `../devtool/.claude/design/DESIGN.md`
