# Volumify — 기능 노트 / 백로그

## 확정 사항 (결정됨)
- **문제**: Spotify 데스크탑 볼륨 곡선이 상단(80~100) 쏠림 = 위쪽 과민, 아래쪽 죽음
- **제약**: 무손실(Lossless, ≥1.2.84) 양보 불가 → Spotify 자동업뎃 필수 → **Spicetify는 업뎃마다 깨져서 부적합**
- **방식**: OS레벨(Windows Core Audio, 앱별 세션 볼륨) 외부 제어 → Spotify 안 건드림 = 업뎃·무손실과 충돌 0
- **스택**: C#/.NET (Core Audio via NAudio/CSCore), 트레이앱
- **배포**: GitHub (오픈소스 + Releases 바이너리, 가능하면 winget/scoop)
- **선호 곡선**: power curve `vol = slider^p`, p ≈ 0.35~0.5 (강하게~균형)

## 핵심 기능 (음량부터 구현)
- [ ] 지각 볼륨 곡선: `vol = slider^p`, p 조절 가능
- [ ] 프리셋: 약하게 / 균형 / 강하게 (+ 커스텀 슬라이더)
- [ ] **라이브 프리뷰**: 드래그하면 곡선 그래프 + 예상 볼륨 실시간 표시 ← Spicetify엔 없는 우리 차별점
- [ ] 일반 사용자도 UI에서 즉시 조절 (파일편집·CLI 없이)

## 긍정 리스트 (데모에서 좋았던 것)
- ⭐ **가사 팝업** (floating, 다중 소스) — 네이티브 가사 있지만 "항상 위 떠다니는 창"이 차별점

## 볼륨 도메인 참고/경쟁 (직접 관련 — 우선 검토)
- 볼륨 % 표시 + 정밀 숫자 입력 — jeroentvb/spicetify-volume-percentage
- **볼륨 프로파일 저장/호출** — notPlancha/volume-profiles-v2 (예: 헤드폰/스피커 프리셋) ← 좋은 아이디어
- 자동 볼륨 / 트랙별 정규화 — amanharwara/spicetify-autoVolume
- 스크롤휠로 미세 조절
- 슬립 타이머 (오디오 인접)
- 구간 반복(Loopy Loop)

## UI 영감
- 앨범아트 색 기반 동적 강조색 (Default Dynamic 테마)
- Spotlight식 명령/검색 바 (power-bar)
- 강조색 테마 커스텀

## 무관 / 스킵
- 광고차단(adblockify), 친구 노래 큐 추가(Spotispy), 로컬파일 관리

## 구현 현황 (2026-06)
- [x] **v0.1.0 릴리스** — self-contained 단일 exe를 GitHub Releases에 배포 (빌드 불필요)
- [x] 볼륨 곡선 `gain = position^p` + 프리셋(0.35~2.0) + 트레이/핫키(Ctrl+Alt+↑/↓)
  - 화면 중앙 OSD(% 플래시)는 제거함 — 트레이 툴팁/오버레이 바/팝업 %로 피드백 충분
- [x] 설정 저장(%APPDATA%\SpotifyLinearVolume\settings.json), 시작프로그램 자동실행
- [x] 컨트롤 패널(둥근 보더리스+초록, 커브 그래프, 접기/펼치기, 드래그·리사이즈)
- [x] **Spotify 창에 붙기(Dock)** 모드 + **볼륨 슬라이더 위 오버레이** 모드 (이제 동시 사용 가능)
- [x] **오버레이 정렬 완성**: 아래 "오버레이 정렬" 참고. 좌우폭/버튼 가림 문제 해결됨.
- [x] **호버 팝업**(`VolumePopupForm`): 오버레이가 좁을 때 마우스 올리면 위로 넉넉한 슬라이더(+%) 플라이아웃. 옵션(`OverlayPopup`, 기본 켜짐).
- [ ] 가사 팝업 (보류), 폰 원격(보류), 볼륨 프로파일 저장/호출

## 오버레이 정렬 (해결 기록 — 다시 건드릴 때 필독)
- Spotify는 Chromium. UI Automation 슬라이더 좌표 중 **X(왼쪽)만 신뢰 가능**.
  - **Width**: UIA가 ~129px(히트영역)로 보고하지만 **실제 그려진 레일은 ~92px**. 남는 ~37px가
    오른쪽 미니플레이어 버튼을 덮음 → `SpotifyVolumeLocator.RailRightInset=37`로 잘라냄.
  - **Y**: 신뢰 불가(window.Bottom+47, 화면 밖). 창 하단에서 기하학적으로 계산(`PlaybarSliderOffset=54`).
- 레일은 사실상 **고정폭(~92px)**, 창 키워도 안 늘어나고 위치만 이동. 우→좌로 좁히면 슬라이더 오른쪽
  버튼 2개(미니플레이어/전체화면)가 순차로 숨고 레일이 그 공간으로 약간 이동 → 오버레이가 실시간 추적.
- 오버레이 창은 레일보다 좌우 `KnobMargin=6`씩 넓게(노브 안 잘리게), `VolumeBar.EdgePad=6`이라
  안쪽 초록 트랙이 정확히 레일(X..X+Width)에 일치.
- **검증법(중요)**: UIA 숫자 비교는 거짓양성(스테일 좌표). **PrintWindow(PW_RENDERFULLCONTENT=2)**로
  실제 창 픽셀을 떠서(가려져도 캡처됨) 눈으로 확인. CopyFromScreen은 위에 뜬 창(터미널)을 찍어 오염됨.

## 성능 (해결 기록 — Chromium 앱 위 UIA 오버레이 만들 때 필독)

> **한 줄 교훈:** Chromium 기반 앱(Spotify·Discord·VSCode·Electron…)에 UI Automation으로 **트리를 훑으면**
> 그 앱의 접근성 엔진이 켜지고, 켜진 동안 트리를 계속 유지·갱신해서 **상대 앱에 상당한 CPU·RAM을 유발**한다.
> 한 번 켜지면 클라이언트(우리)가 **완전히 종료되거나 ~30초 무접근 자동해제될 때까지** 안 꺼진다.

- **증상**: 오버레이 켜면 **Volumify 1.5% + Spotify 본체 7.2%**(12코어 기준, 합 ≈ 1코어)를 **정지 상태에서도** 계속 먹음.
  Spotify RAM도 세션 동안 ~400MB 부풀음(접근성 트리). 닫으면 CPU는 즉시 복구, RAM은 Spotify 재시작해야 리셋.
- **원인**: 오버레이가 레일 위치를 찾으려 `AutomationElement.FindAll(TreeScope.Descendants, …)`(전체 트리 워크)를
  호출하는데, **리사이즈 정착(settle) 상태머신이 종료를 못 하고 그 워크를 초당 ~5회 영원히 반복**했다.
  (probe가 거의 항상 in-flight → `_pendingRequery`가 계속 세팅 → `_resizeProbeBudget` 감소 안 됨 → 정지 조건 미도달.)
  5Hz 트리 워크가 Chromium a11y를 영구히 hot하게 유지 = 그 7%의 정체.
  - 비교: 볼륨 컨트롤러(`SpotifyVolumeController`)는 슬라이더를 **한 번** 찾아 `RangeValuePattern`을 캐시한 뒤
    `.Current.Value`만 읽음 → 트리 워크 1회뿐이라 a11y가 다시 잠듦(Spotify 0%). **오버레이의 반복 워크만** 문제였다.
- **고침**:
  1. **settle 루프 종료**(`ResizeSettleTick`): 안정 rect + probe 예산 소진 시 `Stop()`; probe in-flight면 재요청 안 쌓고 대기.
  2. **레일 위치 캐시(창 크기별)**(`SpotifyVolumeLocator`): 같은 창 크기면 워크 없이 캐시 재사용, 창 이동은 위치만 재계산.
     리사이즈 settle 중엔 `forceFresh`로 캐시 우회 — Spotify가 플레이바를 **비동기 리플로우**해서 첫 측정이 틀릴 수 있어
     안정될 때까지 몇 번 새로 측정해야 정렬이 맞는다.
- **결과**: Spotify **7.2% → 0.26%**, Volumify **1.5% → 0.02%** (idle, 재생 정지 동일 조건). 오버레이가 사실상 공짜.

### 측정 방법론 (이게 절반이었다)
- **상대 앱(Spotify)도 같이 재라.** 우리 1.5% 뒤에 숨은 7%가 거기 있었다. 우리 프로세스만 보면 절대 못 찾음.
- **재생 상태를 통제**: Spotify 창 제목이 `가수 – 곡`이면 재생 중, `Spotify Premium`이면 정지. CPU 비교는 **같은 상태**에서만.
- **RAM 기준선은 재시작으로**: 우리 앱 닫아도 안 내려가는 RAM은 상대 앱 자체 것. 우리 영향 = (우리 끈 채) **상대 앱 재시작** 후 값과 비교.
  (주의: Store 앱은 exe 경로로 `Start-Process` 불가 → `explorer.exe shell:AppsFolder\<AUMID>`로 띄운다.)
- **A/B 설정으로 격리**: 오버레이 on/off, "도킹만 on(훅은 쓰되 UIA 안 씀)" 등으로 어떤 요소가 비용인지 분리.
  → 도킹만=Spotify 0% 였으므로 **WinEvent 훅 자체는 무죄, UIA 질의가 범인**. (훅은 a11y를 안 켠다.)
- **micro-opt은 대부분 노이즈 이하**: 폰트 캐시·창레벨 이벤트필터·폴링 백오프(A/B) 전부 측정상 ≈0. ±0.2% 노이즈 속에서
  진짜 핫스팟(반복 트리 워크)을 집어낸 건 위 A/B 격리 + 상대앱 측정 덕분. **추측 말고 측정.**
- **CPU% 계산**: `Δ프로세스ProcessorTime(ms) / (Δ실시간(ms) × 논리코어수) × 100`. 한 코어 100%가 1/N%로 보이는 점 주의.

### 재사용 체크리스트 (비슷한 프로젝트 시작 전)
- [ ] UIA/MSAA를 쓰면 **상대 Chromium/Electron 앱**의 CPU·RAM을 처음부터 측정에 포함.
- [ ] 트리 워크(`FindAll(Descendants)`)는 **딱 필요할 때 1회**, 결과를 캐시. 폴링/타이머가 반복 워크하지 않게.
- [ ] 가능하면 넓은 `Descendants` 대신 좁은 스코프/조건. 한 번 찾은 패턴의 `.Current` 읽기는 싸다.
- [ ] 상태머신(정착/재시도 루프)은 **반드시 종료 조건이 실제로 도달되는지** 측정으로 확인(여기선 영구 루프가 핵심 버그였음).
