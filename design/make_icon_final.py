# -*- coding: utf-8 -*-
"""最终图标：午夜蓝渐变底 + 白色 Via（Yellowtail 字体，Apache 2.0）。
生成 ui/Assets/ViaTTS.ico（16~256）和 ViaTTS.png（256）。
运行：python design/make_icon_final.py（需要已安装 Yellowtail 字体）"""
import os

import numpy as np
from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.join(os.path.dirname(HERE), "ui", "Assets")
S = 1024
INSET, RADIUS = 40, 200
MIDNIGHT = (14, 22, 48)
LANCZOS = Image.Resampling.LANCZOS
FONT_CANDIDATES = [
    os.path.join(os.environ.get("LOCALAPPDATA", ""), "Microsoft", "Windows", "Fonts", "Yellowtail-Regular.ttf"),
    r"C:\Windows\Fonts\Yellowtail-Regular.ttf",
]


def gradient_tile(bg=MIDNIGHT, amount=0.22):
    """同色系的轻微渐变：左上稍亮、右下稍暗。"""
    base = np.array(bg, np.float32)
    light, dark = base + (255 - base) * amount * 0.6, base * (1 - amount)
    yy, xx = np.mgrid[0:S, 0:S].astype(np.float32)
    t = np.clip(((xx - S / 2) + (yy - S / 2)) / (S * 1.6) + 0.5, 0, 1)[..., None]
    rgb = (light + (dark - light) * t).astype(np.uint8)
    src = Image.fromarray(np.concatenate([rgb, np.full((S, S, 1), 255, np.uint8)], 2), "RGBA")
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([INSET, INSET, S - INSET, S - INSET], radius=RADIUS, fill=255)
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    img.paste(src, (0, 0), mask)
    return img


def via_mask(width_ratio=0.74):
    font = next(p for p in FONT_CANDIDATES if os.path.exists(p))
    size = 700
    f = ImageFont.truetype(font, size)
    probe = Image.new("L", (size * 4, size * 2), 0)
    ImageDraw.Draw(probe).text((size // 2, size // 4), "Via", font=f, fill=255)
    glyph = probe.crop(probe.getbbox())
    w = int((S - 2 * INSET) * width_ratio)
    return glyph.resize((w, int(glyph.height * w / glyph.width)), LANCZOS)


def master():
    img = gradient_tile()
    m = via_mask()
    img.paste(Image.new("RGBA", m.size, (255, 255, 255, 255)), ((S - m.width) // 2, (S - m.height) // 2 + 8), m)
    return img


if __name__ == "__main__":
    img = master()
    os.makedirs(ASSETS, exist_ok=True)
    img.resize((256, 256), LANCZOS).save(os.path.join(ASSETS, "ViaTTS.png"))
    img.save(os.path.join(ASSETS, "ViaTTS.ico"),
             sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)])
    img.resize((512, 512), LANCZOS).save(os.path.join(HERE, "icons", "final.png"))
    print("ok", ASSETS)
