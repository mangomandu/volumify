# Regenerates assets/thumbnail.png — the README banner. Composites REAL app screenshots
# (the volume panel + the floating lyrics window, both cropped from one capture) onto a
# dark glow background with the headline copy. Run from the repo root:
#   python3 assets/thumbnail.py   (needs Pillow + numpy + Segoe UI fonts)
# Rendered at 1x so the screenshots stay crisp (they're already low-res; upscaling blurs).
import numpy as np
from PIL import Image, ImageDraw, ImageFont, ImageFilter

W, H = 1280, 640
FZ = "/mnt/c/Windows/Fonts/"
def font(name, px): return ImageFont.truetype(FZ + name, px)
f_title = font("segoeuib.ttf", 112)
f_sub   = font("seguisb.ttf", 31)
f_desc  = font("segoeui.ttf", 21)
f_badge = font("segoeui.ttf", 17)

GREEN = (30, 215, 96); WHITE = (242, 242, 245); SUB = (154, 154, 168)

# ---- background: near-black with a soft glow behind the cards ----
yy, xx = np.mgrid[0:H, 0:W]
cx, cy = int(0.72 * W), int(0.42 * H)
d2 = ((xx - cx) / (0.60 * W)) ** 2 + ((yy - cy) / (0.62 * H)) ** 2
glow = np.clip(1 - d2, 0, 1) ** 1.6
base = np.array([14, 14, 21.0]); tint = np.array([24, 26, 38.0])
bg = base + (tint - base) * glow[:, :, None]
img = Image.fromarray(np.clip(bg, 0, 255).astype("uint8"), "RGB").convert("RGBA")

def card(path, x, y, w, radius=13, blur=20, salpha=165, sdy=12):
    """Paste a screenshot with rounded corners + a soft drop shadow; return its height."""
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
    return h

# ---- right side: the two real windows, floating ----
card("assets/vol_panel.png", 690, 180, 250)   # volume panel (real screenshot)
card("assets/lyrics.png",    980, 92, 228)     # lyrics window (real screenshot)

# ---- left content ----
d = ImageDraw.Draw(img)
icon = Image.open("assets/icon.png").convert("RGBA").resize((74, 74), Image.LANCZOS)
img.alpha_composite(icon, (94, 108))
d.text((90, 198), "Volumify", font=f_title, fill=WHITE)
d.text((94, 338), "Two fixes for Spotify Desktop.", font=f_sub, fill=GREEN)
d.text((94, 384), "A volume curve that revives the dead bottom half,", font=f_desc, fill=SUB)
d.text((94, 412), "and floating synced lyrics that follow along.", font=f_desc, fill=SUB)

bx, by, bh = 94, 458, 34
for label in ["Lossless-safe", "Synced lyrics", "No client patching", "EN · KR"]:
    tw = d.textlength(label, font=f_badge); bw = tw + 30
    d.rounded_rectangle([bx, by, bx + bw, by + bh], radius=bh / 2,
                        fill=(28, 28, 36, 255), outline=(52, 52, 64, 255), width=1)
    d.text((bx + 15, by + 7), label, font=f_badge, fill=(200, 200, 210))
    bx += bw + 14

img.convert("RGB").save("assets/thumbnail.png")
print("wrote assets/thumbnail.png")
