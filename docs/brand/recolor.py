#!/usr/bin/env python3
"""Recolor round-1 logo 3 (flat 4-colour art) into the Starlit Hollow palette, pixel-exact.

Each pixel is treated as a blend between the cream background and its nearest ink colour
(leaf green, text green, star gold), so the soft anti-aliased edges carry over cleanly.
Writes a version on the night background plus transparent copies for the launcher.
"""
from PIL import Image

SRC = "source-logo-3.png"
BG = (0xF3, 0xEF, 0xDE)
INKS = {"leaf": (0x5F, 0x6F, 0x34), "text": (0x23, 0x3E, 0x20), "star": None}  # star sampled below

NIGHT = (0x19, 0x17, 0x28)
DUSK = (0x28, 0x22, 0x3E)
IVORY = (0xF4, 0xEC, 0xDD)
LILAC = (0xB9, 0xAE, 0xCB)
LAVENDER = (0xB8, 0xA0, 0xDE)
GOLD = (0xE4, 0xBE, 0x66)

VARIANTS = {
    # name: (leaf, text, star)
    "a": (LAVENDER, IVORY, GOLD),
    "b": (IVORY, LAVENDER, GOLD),
    "c": (LAVENDER, GOLD, IVORY),
}


def dist2(a, b):
    return sum((x - y) ** 2 for x, y in zip(a, b))


def sample_star(im):
    """The most common clearly-yellow colour in the image."""
    counts = {}
    for p in im.getdata():
        r, g, b = p
        if r > 180 and g > 120 and b < 110:
            counts[p] = counts.get(p, 0) + 1
    return max(counts, key=counts.get)


def classify(im):
    """Per pixel: (ink name or None, blend amount 0..1 away from background)."""
    inks = {k: v for k, v in INKS.items()}
    w, h = im.size
    px = im.load()
    out = []
    for y in range(h):
        row = []
        for x in range(w):
            p = px[x, y]
            best, bt, bd = None, 0.0, None
            for name, col in inks.items():
                d = [c - b for c, b in zip(col, BG)]
                v = [c - b for c, b in zip(p, BG)]
                dd = sum(c * c for c in d)
                t = max(0.0, min(1.0, sum(a * b for a, b in zip(v, d)) / dd))
                proj = [b + t * c for b, c in zip(BG, d)]
                err = dist2(p, proj)
                if bd is None or err < bd:
                    best, bt, bd = name, t, err
            row.append((best, bt))
        out.append(row)
    return out


def render(cls, size, colours, background):
    w, h = size
    img = Image.new("RGBA", size)
    px = img.load()
    for y in range(h):
        for x in range(w):
            name, t = cls[y][x]
            ink = colours[name]
            if background is None:
                px[x, y] = (*ink, int(round(t * 255)))
            else:
                px[x, y] = tuple(int(round(b + t * (c - b))) for b, c in zip(background, ink)) + (255,)
    return img


def main():
    im = Image.open(SRC).convert("RGB")
    INKS["star"] = sample_star(im)
    print("star colour #%02X%02X%02X" % INKS["star"])
    cls = classify(im)
    for k, (leaf, text, star) in VARIANTS.items():
        colours = {"leaf": leaf, "text": text, "star": star}
        render(cls, im.size, colours, NIGHT).convert("RGB").save(f"logo-{k}.png")
        clear = render(cls, im.size, colours, None)
        clear.save(f"logo-{k}-transparent.png")
        # Mark only (the arch, star and leaf), no lettering: everything above the text.
        text_top = min(y for y in range(im.size[1]) for x in range(0, im.size[0], 4) if cls[y][x][0] == "text" and cls[y][x][1] > 0.5)
        mark = clear.crop((0, 0, im.size[0], text_top - 10))
        mark = mark.crop(mark.getbbox())
        side = max(mark.size) + 40
        sq = Image.new("RGBA", (side, side))
        sq.paste(mark, ((side - mark.width) // 2, (side - mark.height) // 2), mark)
        sq.save(f"mark-{k}-transparent.png")
        print(k, "ok", sq.size)


if __name__ == "__main__":
    main()
