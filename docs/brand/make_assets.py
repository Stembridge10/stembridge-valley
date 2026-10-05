#!/usr/bin/env python3
"""Build the launcher's brand assets from the approved logo (round 4, variant A).

- logo-mark.png : the arch + star mark, transparent, tightly cropped (header art).
- app.ico       : window/taskbar/exe icon. The mark on a rounded dusk-purple tile so it
                  reads on both light and dark taskbars, at every standard icon size.
"""
import sys
from pathlib import Path
from PIL import Image, ImageDraw

SRC = Path(sys.argv[1])          # mark-a-transparent.png
OUT = Path(sys.argv[2])          # src/Launcher/Assets
DUSK = (0x28, 0x22, 0x3E, 255)


def tight(im):
    """Crop to pixels that are actually visible (ignores faint anti-alias haze)."""
    alpha = im.getchannel("A").point(lambda a: 255 if a > 24 else 0)
    return im.crop(alpha.getbbox())


def tile(mark, size):
    big = size * 4  # draw large, then shrink, for smooth corners
    t = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    ImageDraw.Draw(t).rounded_rectangle([0, 0, big - 1, big - 1], radius=int(big * 0.22), fill=DUSK)
    inner = int(big * (0.80 if size >= 32 else 0.88))
    m = mark.copy()
    m.thumbnail((inner, inner), Image.LANCZOS)
    t.alpha_composite(m, ((big - m.width) // 2, (big - m.height) // 2))
    return t.resize((size, size), Image.LANCZOS)


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    mark = tight(Image.open(SRC).convert("RGBA"))
    side = max(mark.size)
    sq = Image.new("RGBA", (side, side))
    sq.paste(mark, ((side - mark.width) // 2, (side - mark.height) // 2), mark)
    header = sq.copy()
    header.thumbnail((256, 256), Image.LANCZOS)
    header.save(OUT / "logo-mark.png")

    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    frames = [tile(sq, s) for s in sizes]
    frames[-1].save(OUT / "app.ico", format="ICO", sizes=[(s, s) for s in sizes],
                    append_images=frames[:-1])
    # preview strip for review
    strip = Image.new("RGBA", (sum(sizes) + 12 * len(sizes) + 12, 280), (240, 240, 240, 255))
    x = 12
    for f in frames:
        strip.alpha_composite(f, (x, 12))
        x += f.width + 12
    dark = Image.new("RGBA", strip.size, (32, 32, 36, 255))
    x = 12
    for f in frames:
        dark.alpha_composite(f, (x, 12))
        x += f.width + 12
    both = Image.new("RGBA", (strip.width, 560))
    both.paste(strip, (0, 0))
    both.paste(dark, (0, 280))
    both.save(OUT.parent.parent.parent.parent / "brand" / "round4" / "icon-preview.png")
    print("ok", header.size, sizes)


if __name__ == "__main__":
    main()
