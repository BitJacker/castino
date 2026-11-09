"""Genera assets/lindo.ico e assets/logo.png (eseguire: python3 assets/make_icon.py)."""
import math, os
from PIL import Image, ImageDraw, ImageFilter

S = 1024
here = os.path.dirname(os.path.abspath(__file__))

def lerp(a, b, t): return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))

def make():
    c1, c2 = (124, 92, 255), (34, 211, 238)
    grad = Image.new("RGB", (S, S))
    px = grad.load()
    for y in range(S):
        for x in range(S):
            t = (x + y) / (2 * S)
            px[x, y] = lerp(c1, c2, t)
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle((40, 40, S - 40, S - 40), radius=230, fill=255)
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    img.paste(grad, (0, 0), mask)

    # luce morbida in alto
    glow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(glow).ellipse((-200, -560, S + 200, 420), fill=(255, 255, 255, 40))
    glow = glow.filter(ImageFilter.GaussianBlur(70))
    glow_masked = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    glow_masked.paste(glow, (0, 0), mask)
    img = Image.alpha_composite(img, glow_masked)

    d = ImageDraw.Draw(img)
    # "L" spessa con angoli arrotondati
    W = (255, 255, 255, 255)
    d.rounded_rectangle((300, 250, 430, 790), radius=65, fill=W)
    d.rounded_rectangle((300, 660, 690, 790), radius=65, fill=W)

    # scintilla a 4 punte (pulito!)
    def sparkle(x, y, R, rr):
        pts = []
        for i in range(8):
            ang = math.radians(i * 45 - 90)
            rad = R if i % 2 == 0 else rr
            pts.append((x + rad * math.cos(ang), y + rad * math.sin(ang)))
        d.polygon(pts, fill=(255, 255, 255, 255))
    sparkle(690, 360, 165, 36)
    sparkle(790, 560, 66, 16)
    return img

img = make()
img.resize((512, 512), Image.LANCZOS).save(os.path.join(here, "logo.png"))
sizes = [(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)]
img.resize((256, 256), Image.LANCZOS).save(os.path.join(here, "lindo.ico"), sizes=sizes)
print("ok")
