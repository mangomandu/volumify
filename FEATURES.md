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
- [x] **Spotify 창에 붙기(Dock)** 모드 + **볼륨 슬라이더 위 오버레이** 모드 (상호배타)
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
