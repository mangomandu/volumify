# Regenerates the README banners — English (thumbnail.png) and Korean (thumbnail.ko.png).
# Composites REAL app screenshots (the volume panel + the floating lyrics window) onto a dark
# glow background with localized headline copy. Run from the repo root:
#   python3 assets/thumbnail.py   (needs Pillow + numpy + Segoe UI + Malgun Gothic fonts)
import numpy as np
from PIL import Image, ImageDraw, ImageFont, ImageFilter

W, H = 1280, 640
FZ = "/mnt/c/Windows/Fonts/"
def font(spec): return ImageFont.truetype(FZ + spec[0], spec[1])

GREEN = (30, 215, 96); WHITE = (242, 242, 245); SUB = (154, 154, 168)

def card_on(img, path, x, y, w, radius=13, blur=20, salpha=165, sdy=12):
    """Paste a screenshot with rounded corners + a soft drop shadow."""
    s = Image.open(path).convert("RGBA")
    h = round(s.height * w / s.width)
    s = s.resize((w, h), Image.LANCZOS)
    m = Image.new("L", (w, h), 0)
    ImageDraw.Draw(m).rounded_rectangle([0, 0, w - 1, h - 1], radius=radius, fill=255)
    s.putalpha(m)
    sh = Image.new("RGBA", img.size, (0, 0, 0, 0))
    ImageDraw.Draw(sh).rounded_rectangle([x, y + sdy, x + w, y + h + sdy], radius=radius, fill=(0, 0, 0, salpha))
    img.alpha_composite(sh.filter(ImageFilter.GaussianBlur(blur)))
    img.alpha_composite(s, (x, y))

def build(cfg):
    yy, xx = np.mgrid[0:H, 0:W]
    cx, cy = int(0.72 * W), int(0.42 * H)
    d2 = ((xx - cx) / (0.60 * W)) ** 2 + ((yy - cy) / (0.62 * H)) ** 2
    glow = np.clip(1 - d2, 0, 1) ** 1.6
    base = np.array([14, 14, 21.0]); tint = np.array([24, 26, 38.0])
    bg = base + (tint - base) * glow[:, :, None]
    img = Image.fromarray(np.clip(bg, 0, 255).astype("uint8"), "RGB").convert("RGBA")

    card_on(img, "assets/vol_panel.png", 690, 180, 250)
    card_on(img, "assets/lyrics.png", 980, 92, 228)

    d = ImageDraw.Draw(img)
    icon = Image.open("assets/icon.png").convert("RGBA").resize((74, 74), Image.LANCZOS)
    img.alpha_composite(icon, (94, 108))
    f_title = font(("segoeuib.ttf", 112))  # "Volumify" is Latin — Segoe looks best
    f_sub, f_desc, f_badge = font(cfg["sub"]), font(cfg["desc"]), font(cfg["badge"])
    d.text((90, 198), "Volumify", font=f_title, fill=WHITE)
    d.text((94, 338), cfg["tagline"], font=f_sub, fill=GREEN)
    d.text((94, 384), cfg["desc1"], font=f_desc, fill=SUB)
    d.text((94, 412), cfg["desc2"], font=f_desc, fill=SUB)

    bx, by, bh = 94, 458, 34
    for label in cfg["badges"]:
        tw = d.textlength(label, font=f_badge); bw = tw + 30
        d.rounded_rectangle([bx, by, bx + bw, by + bh], radius=bh / 2,
                            fill=(28, 28, 36, 255), outline=(52, 52, 64, 255), width=1)
        d.text((bx + 15, by + 7), label, font=f_badge, fill=(200, 200, 210))
        bx += bw + 14

    img.convert("RGB").save(cfg["out"])
    print("wrote", cfg["out"])

build(dict(
    out="assets/thumbnail.png",
    sub=("seguisb.ttf", 31), desc=("segoeui.ttf", 21), badge=("segoeui.ttf", 17),
    tagline="Two fixes for Spotify Desktop.",
    desc1="A volume curve that revives the dead bottom half,",
    desc2="and floating synced lyrics that follow along.",
    badges=["Lossless-safe", "Synced lyrics", "No client patching", "EN · KR"]))

build(dict(
    out="assets/thumbnail.ko.png",
    sub=("malgunbd.ttf", 30), desc=("malgun.ttf", 20), badge=("malgun.ttf", 16),
    tagline="스포티파이 데스크톱, 두 가지 해결.",
    desc1="죽어버린 슬라이더 아래 절반을 되살리는 볼륨 곡선,",
    desc2="그리고 떠서 따라오는 실시간 가사.",
    badges=["무손실 안전", "실시간 가사", "패치 없음", "EN · 한국어"]))
