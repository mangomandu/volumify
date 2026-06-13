<div align="center">

# 🎚️ Spotify Linear Volume

### Spotify's volume slider, fixed.

**A perceptual, _linear‑feeling_ volume curve for Spotify on Windows — without ever touching the app.**

[![Windows 10 | 11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6?logo=windows&logoColor=white)](#)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)](#)
[![Lossless & auto‑update safe](https://img.shields.io/badge/Spotify-lossless%20%26%20update%20safe-1ed760?logo=spotify&logoColor=white)](#why-not-just-spicetify)
[![License: MIT](https://img.shields.io/badge/License-MIT-1ed760)](LICENSE)

<img src="assets/curve.png" width="720" alt="Power curve vs. Spotify's top-heavy default — make the whole slider usable">

</div>

---

Spotify Desktop's volume is **top‑heavy**: the bottom half of the slider does almost nothing, and `80 → 100%` is a cliff. This tiny tray app remaps it with a tunable power curve so **every part of the slider is useful** — and it does it at the **OS level** (Windows Core Audio), so the Spotify client is *never* modified.

> No Spicetify. No patching. **Survives auto‑updates. Keeps Spotify Lossless intact.**

## ✨ See it

It overlays Spotify's **own** volume slider — matched to its position and width as the window resizes, and staying clear of the neighbouring buttons:

<div align="center"><img src="assets/overlay.png" width="560" alt="The green overlay sitting exactly on Spotify's native volume rail"></div>

Window too narrow to drag the little rail? **Hover it for a roomy fly‑out** with a live %:

<div align="center"><img src="assets/popup.png" width="430" alt="Hover fly-out: a roomy slider with a live percentage readout"></div>

## 🎯 How it works

The on‑screen position `x` (0–1) is remapped to the actual gain:

```
gain = x ^ p
```

| `p` | feel | use it when |
|----:|------|-------------|
| **0.35** | strong, loud low end | you mostly listen quiet‑to‑mid (★ recommended) |
| **0.5** | balanced | a gentle, all‑round fix |
| **1.0** | true linear | gain == slider position |
| **> 1** | fine control down low | you ride very low volumes |

Spotify's own volume stays at 100%; the app only sets the **Windows session volume** for the Spotify process (the per‑app level in the Volume Mixer). Nothing inside Spotify is touched.

## 🚀 Features

- 🎚️ **Tunable perceptual curve** — presets from *강하게 (0.35)* to *저음 더 조용 (2.0)*, with a **live curve graph**.
- ⌨️ **Global hotkeys** — `Ctrl+Alt+↑ / ↓` from anywhere, with an on‑screen readout.
- 🧲 **Two ways to stick to Spotify** (pick one):
  - **Overlay** — a slim bar right on the native rail, with the optional **hover fly‑out** above.
  - **Compact dock** — a small panel that follows the Spotify window.
- 💾 **Remembers everything** (`%APPDATA%\SpotifyLinearVolume\settings.json`) and optional **run at startup**.
- 📦 **Single self‑contained `.exe`** — no installer, no runtime to chase.

## 🤔 Why not just Spicetify?

|  | Spicetify volume tweaks | **Spotify Linear Volume** |
|---|:---:|:---:|
| Survives Spotify auto‑updates | ❌ silently reverts each update | ✅ never touches Spotify |
| Works with **Lossless** | ⚠️ risky / can block it | ✅ completely untouched |
| Curve | hard‑coded `x²` | ✅ tunable + live graph |
| Setup | edit JS / run a CLI | ✅ run one `.exe` |

Because the fix lives entirely in Windows audio, Spotify is free to update itself forever and your curve just keeps working.

## 🛠️ Build & run

> Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download). Then leave Spotify's own volume at 100% and let this shape it.

```powershell
dotnet build -c Release
.\bin\Release\net8.0-windows\SpotifyLinearVolume.exe
```

<details>
<summary><b>Single‑file, self‑contained release (.exe with no dependencies)</b></summary>

```powershell
dotnet publish -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The standalone `SpotifyLinearVolume.exe` lands in `bin\Release\net8.0-windows\win-x64\publish\`.
</details>

## 🧩 Tech

C# / .NET 8 · WinForms (+ WPF for UI Automation) · [NAudio](https://github.com/naudio/NAudio) for Core Audio session volume. UI Automation is used only to *locate* Spotify's native slider for the overlay — never to control it. See [`FEATURES.md`](FEATURES.md) for design notes and the (hard‑won) overlay‑alignment findings.

## 📄 License

[MIT](LICENSE) — do whatever you like.

<div align="center"><sub>Not affiliated with Spotify. “Spotify” is a trademark of Spotify AB.</sub></div>
