<div align="center">

<img src="assets/thumbnail.png" alt="Volumify — Spotify's volume slider, fixed" width="860">

<br>

**English** &nbsp;·&nbsp; [한국어](README.ko.md)

<br>

[![Download latest release](https://img.shields.io/github/v/release/mangomandu/volumify?label=Download%20.exe&logo=github&color=1ed760)](https://github.com/mangomandu/volumify/releases/latest)
[![Windows 10 | 11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6?logo=windows&logoColor=white)](#)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)](#)
[![Lossless & auto‑update safe](https://img.shields.io/badge/Spotify-lossless%20%26%20update%20safe-1ed760?logo=spotify&logoColor=white)](#-safe-by-design)
[![License: MIT](https://img.shields.io/badge/License-MIT-1ed760)](LICENSE)

### [⬇️ Download the latest `.exe`](https://github.com/mangomandu/volumify/releases/latest) &nbsp;·&nbsp; no install, just run

</div>

---

Spotify Desktop's volume is **top‑heavy**: the bottom half of the slider does almost nothing, and `80 → 100%` is a cliff. **Volumify** is a tiny tray app that remaps it with a tunable power curve so **every part of the slider is useful** — by driving **Spotify's own volume** through Windows UI Automation. The level you land on is Spotify's *real* volume, so it **syncs everywhere** — your phone, Connect speakers, the Windows mixer — and the Spotify client is *never* patched.

> No client patching — **survives auto‑updates and keeps Spotify Lossless intact.** Syncs to your phone & Connect devices.

## ✨ See it

It overlays Spotify's **own** volume slider — matched to its position and width as the window resizes, and clear of the neighbouring buttons. Nudge either bar and they move together, **both ways**:

<div align="center"><img src="assets/overlay.png" width="380" alt="The green overlay sitting exactly on Spotify's native volume rail"></div>

Window too narrow to grab the little rail? **Hover it for a roomy fly‑out** with a live %:

<div align="center"><img src="assets/popup.png" width="380" alt="Hover fly-out: a roomy slider with a live percentage readout"></div>

## 🎯 How it works

You see one slider; the app remaps it. Move it to position `x` (0–1) and it sets **Spotify's own volume** to:

```
gain = x ^ p
```

Spotify's built‑in curve is **top‑heavy** (≈ `x⁴` — the bottom half is barely audible and the top ~20% does most of the work), so a `p` **below 1** flattens it: the low slider positions get lifted until the whole slider is usable. **`p ≈ 0.4` makes the perceived loudness track the slider evenly**; `p = 1` leaves Spotify's raw top‑heavy feel; higher `p` only makes it worse. Pick by feel from the tray or the panel's **live curve graph**:

<div align="center"><img src="assets/curve.png" width="640" alt="A power curve flattening Spotify's top-heavy response so the whole slider becomes usable"></div>

| preset | `p` | feel |
|--------|----:|------|
| **평탄** · *Flat* | 0.3 | flattest — loud early |
| **고름** · *Even* | 0.4 | perceived loudness ≈ slider position (**recommended**) |
| **살짝 쏠림** · *Slight ramp* | 0.6 | a little top‑heavy |
| **스포티파이 그대로** · *Spotify native* | 1.0 | Spotify's raw top‑heavy feel |

> Starting points — tune to taste. Because the value it sets is Spotify's *real* volume, nothing inside Spotify is touched and the level follows you to every device.

## 🚀 Features

- 🎚️ **Tunable perceptual curve** — presets from *평탄 / Flat (0.3)* through *고름 / Even (0.4, recommended)* to *스포티파이 그대로 / Spotify native (1.0)*, with a **live curve graph**.
- 🔁 **Two‑way sync** — move Spotify's own slider (or a media key, or your phone) and Volumify follows; move Volumify and Spotify follows. Everything stays in step.
- 📱 **Syncs to every device** — it moves Spotify's own volume, so your phone and Connect speakers come along (no separate OS‑only gain).
- 🌐 **English & 한국어** — auto‑detects your Windows language on first run; switch anytime from the tray.
- 🧲 **Two ways to stick to Spotify** (pick one):
  - **Overlay** — a slim bar right on the native rail, with an optional **hover fly‑out** that appears only when the rail gets too small to drag.
  - **Compact dock** — a small panel that follows the Spotify window.
- 💾 **Remembers everything** (`%APPDATA%\SpotifyLinearVolume\settings.json`) and optional **run at startup**.
- 📦 **Single self‑contained `.exe`** — no installer, no runtime to chase.

## 🔒 Safe by design

Volumify never patches the Spotify client — it only nudges Spotify's **own** volume slider from the outside, through Windows UI Automation. So Spotify is free to update itself forever and your curve just keeps working, **Spotify Lossless stays intact**, and there's nothing to re‑install after an update.

## 🛠️ Build & run

> **Just want to use it?** [Download the `.exe`](https://github.com/mangomandu/volumify/releases/latest) — it's self‑contained, no build required. Run it and it lives in your tray, driving Spotify's volume for you.
>
> _First run:_ it's unsigned (open‑source, no paid certificate), so Windows SmartScreen may warn — click **More info → Run anyway**. On Windows 11 with *Smart App Control* on, unsigned apps are blocked until that feature is turned off.

The Windows app lives in [`windows/`](windows). To build from source you need the [.NET 8 SDK](https://dotnet.microsoft.com/download):

```powershell
cd windows
dotnet build -c Release
.\bin\Release\net8.0-windows\SpotifyLinearVolume.exe
```

<details>
<summary><b>Single‑file, self‑contained release (.exe with no dependencies)</b></summary>

```powershell
cd windows
dotnet publish -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true
```

The standalone `SpotifyLinearVolume.exe` lands in `windows\bin\Release\net8.0-windows\win-x64\publish\`. Pushing a `v*` tag also builds and publishes a release automatically via [GitHub Actions](.github/workflows/release.yml).
</details>

## 🧩 Tech

C# / .NET 8 · WinForms (+ WPF for UI Automation) · [NAudio](https://github.com/naudio/NAudio) for the Windows mixer. **UI Automation** drives Spotify's native volume slider (the RangeValue pattern), reads it back for two‑way sync, and locates it for the overlay — local, ~1 ms per change, no Web API or OAuth, and it never patches the client. See [`windows/FEATURES.md`](windows/FEATURES.md) for design notes and the (hard‑won) overlay‑alignment findings.

## 📄 License

[MIT](LICENSE) — do whatever you like.

<div align="center"><sub>Not affiliated with Spotify. “Spotify” is a trademark of Spotify AB.</sub></div>
