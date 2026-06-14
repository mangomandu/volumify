<div align="center">

<img src="assets/thumbnail.png" alt="Volumify — 스포티파이 볼륨 슬라이더, 제대로" width="860">

<br>

[English](README.md) &nbsp;·&nbsp; **한국어**

<br>

[![최신 릴리스 다운로드](https://img.shields.io/github/v/release/mangomandu/volumify?label=Download%20.exe&logo=github&color=1ed760)](https://github.com/mangomandu/volumify/releases/latest)
[![Windows 10 | 11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6?logo=windows&logoColor=white)](#)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)](#)
[![무손실 & 업데이트 안전](https://img.shields.io/badge/Spotify-lossless%20%26%20update%20safe-1ed760?logo=spotify&logoColor=white)](#-안전한-설계)
[![License: MIT](https://img.shields.io/badge/License-MIT-1ed760)](LICENSE)

### [⬇️ 최신 `.exe` 다운로드](https://github.com/mangomandu/volumify/releases/latest) &nbsp;·&nbsp; 설치 없이 바로 실행

</div>

---

스포티파이 데스크톱의 볼륨은 **위쪽에 쏠려** 있어요. 슬라이더 아래 절반은 거의 무음이고 `80 → 100%` 구간은 절벽이죠. **Volumify**는 이걸 조절 가능한 거듭제곱 곡선으로 다시 매핑해서 **슬라이더 전 구간을 쓸모 있게** 만드는 작은 트레이 앱이에요 — Windows UI 자동화로 **스포티파이 자체 볼륨**을 움직여서요. 최종으로 설정되는 값은 스포티파이의 *진짜* 볼륨이라 **어디서나 동기화**돼요 — 폰, Connect 스피커, Windows 믹서까지. 그리고 스포티파이 클라이언트는 *절대* 건드리지 않아요.

> 클라이언트 패치 없음 — **자동 업데이트에도 멀쩡하고, 스포티파이 무손실(Lossless)도 그대로.** 폰·Connect 기기에 동기화돼요.

## ✨ 미리보기

스포티파이의 **자체** 볼륨 슬라이더 위에 겹쳐요 — 창 크기가 바뀌어도 위치·너비가 맞춰지고, 옆 버튼은 안 가려요. 둘 중 아무거나 움직여도 **양방향으로** 같이 움직여요:

<div align="center"><img src="assets/overlay.png" width="380" alt="스포티파이 볼륨 레일 위에 정확히 얹힌 초록 오버레이"></div>

창이 좁아서 작은 레일을 잡기 힘들 땐? **올려놓으면 넉넉한 팝업**이 실시간 %와 함께 떠요:

<div align="center"><img src="assets/popup.png" width="380" alt="호버 팝업: 실시간 퍼센트가 있는 넉넉한 슬라이더"></div>

## 🎯 작동 방식

슬라이더는 하나만 보이지만 앱이 그걸 다시 매핑해요. 위치 `x`(0–1)로 옮기면 **스포티파이 자체 볼륨**을 이렇게 설정해요:

```
gain = x ^ p
```

스포티파이 기본 곡선은 **위쪽에 쏠려** 있어서(≈ `x⁴` — 아래 절반은 거의 안 들리고 위쪽 20%가 대부분을 함), `p`가 **1보다 작으면** 그걸 펴줘요: 낮은 위치가 끌어올려져 슬라이더 전체가 쓸모 있어져요. **`p ≈ 0.4`면 체감 음량이 슬라이더에 비례(고름)**, `p = 1`은 스포티파이 그대로(위쪽 쏠림), `p`가 더 크면 오히려 더 나빠져요. 트레이나 패널의 **실시간 곡선 그래프**로 감으로 고르세요:

<div align="center"><img src="assets/curve.png" width="640" alt="스포티파이의 위쪽 쏠림을 펴서 슬라이더 전체를 쓸모 있게 만드는 거듭제곱 곡선"></div>

| 프리셋 | `p` | 느낌 |
|--------|----:|------|
| **평탄** · *Flat* | 0.3 | 가장 평탄 — 초반부터 큼 |
| **고름** · *Even* | 0.4 | 체감 음량 ≈ 슬라이더 위치 (**추천**) |
| **살짝 쏠림** · *Slight ramp* | 0.6 | 약간 위쪽 쏠림 |
| **스포티파이 그대로** · *Spotify native* | 1.0 | 스포티파이 원본 위쪽 쏠림 |

> 출발점일 뿐이니 취향껏 조절하세요. 설정되는 값이 스포티파이의 *진짜* 볼륨이라 내부는 아무것도 안 건드리고, 그 레벨이 모든 기기로 따라가요.

## 🚀 기능

- 🎚️ **조절 가능한 청감 곡선** — *평탄 / Flat (0.3)*부터 *고름 / Even (0.4, 추천)*을 거쳐 *스포티파이 그대로 / Spotify native (1.0)*까지 프리셋 + **실시간 곡선 그래프**.
- 🔁 **양방향 동기화** — 스포티파이 자체 슬라이더(또는 미디어 키, 폰)를 움직이면 Volumify가 따라오고, Volumify를 움직이면 스포티파이가 따라와요. 항상 같이 움직여요.
- 📱 **모든 기기에 동기화** — 스포티파이 자체 볼륨을 움직이니 폰과 Connect 스피커도 함께 와요 (OS 전용 게인 아님).
- 🌐 **English & 한국어** — 첫 실행 때 Windows 언어를 자동 감지하고, 트레이에서 언제든 전환할 수 있어요.
- 🧲 **스포티파이에 붙이는 두 가지 방식** (택1):
  - **오버레이** — 네이티브 레일 위의 얇은 바. 레일이 너무 작아지면 뜨는 **호버 팝업** 옵션 포함.
  - **컴팩트 독** — 스포티파이 창을 따라다니는 작은 패널.
- 💾 **설정 기억** (`%APPDATA%\SpotifyLinearVolume\settings.json`) + **Windows 시작 시 자동 실행** 옵션.
- 📦 **단일 자체 포함 `.exe`** — 설치 관리자도, 챙길 런타임도 없음.

## 🔒 안전한 설계

Volumify는 스포티파이 클라이언트를 절대 패치하지 않아요 — Windows UI 자동화로 스포티파이의 **자체** 볼륨 슬라이더를 바깥에서 살짝 움직일 뿐이에요. 그래서 스포티파이는 계속 자유롭게 업데이트해도 곡선은 그대로 동작하고, **무손실(Lossless)도 그대로**, 업데이트 후 다시 설치할 것도 없어요.

## 🛠️ 빌드 & 실행

> **그냥 쓰고 싶다면?** [`.exe` 다운로드](https://github.com/mangomandu/volumify/releases/latest) — 자체 포함이라 빌드 필요 없어요. 실행하면 트레이에 상주하면서 스포티파이 볼륨을 조절해줘요.
>
> _첫 실행:_ 서명이 안 된 오픈소스라(유료 인증서 없음) Windows SmartScreen이 경고할 수 있어요 — **추가 정보 → 실행**을 누르세요. Windows 11에서 *스마트 앱 제어*가 켜져 있으면 끄기 전까지 서명 안 된 앱은 차단돼요.

Windows 앱은 [`windows/`](windows)에 있어요. 소스에서 빌드하려면 [.NET 8 SDK](https://dotnet.microsoft.com/download)가 필요해요:

```powershell
cd windows
dotnet build -c Release
.\bin\Release\net8.0-windows\SpotifyLinearVolume.exe
```

<details>
<summary><b>단일 파일, 자체 포함 릴리스 (.exe, 의존성 없음)</b></summary>

```powershell
cd windows
dotnet publish -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true
```

독립 실행형 `SpotifyLinearVolume.exe`는 `windows\bin\Release\net8.0-windows\win-x64\publish\`에 생겨요. `v*` 태그를 push하면 [GitHub Actions](.github/workflows/release.yml)가 자동으로 빌드·릴리스도 해줘요.
</details>

## 🧩 기술

C# / .NET 8 · WinForms (+ UI 자동화용 WPF) · Windows 믹서용 [NAudio](https://github.com/naudio/NAudio). **UI 자동화**가 스포티파이 네이티브 볼륨 슬라이더(RangeValue 패턴)를 움직이고, 양방향 동기화를 위해 다시 읽고, 오버레이 위치를 잡아요 — 로컬에서 변경당 ~1 ms, Web API·OAuth 없이, 클라이언트도 안 건드려요. 설계 노트와 (고생해서 얻은) 오버레이 정렬 발견은 [`windows/FEATURES.md`](windows/FEATURES.md) 참고.

## 📄 라이선스

[MIT](LICENSE) — 마음대로 쓰세요.

<div align="center"><sub>스포티파이와 무관합니다. “Spotify”는 Spotify AB의 상표입니다.</sub></div>
