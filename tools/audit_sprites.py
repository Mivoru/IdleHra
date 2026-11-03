"""Measures how cleanly every served sprite is cut out, and draws the worst.

Owner, 2026-11-03: "many monsters and items are not cropped well, it looks
sloppy and sometimes badly keyed". This turns that into numbers per file so a
fix can be checked against the same numbers, not against an impression:

  halo      share of the outline that is pale (white left from the background)
  speck     visible pixels in islands detached from the subject (dust, bits
            of background the cut-out left behind)
  corner    the corners are not transparent (the background was never removed)
  fill      how much of the canvas the subject's bounding box covers
  offset    how far the subject's centre sits from the canvas centre

Writes a CSV and a contact sheet of the worst files on a dark background (the
game's own backdrop), which is where a pale fringe shows.

Run:  python tools/audit_sprites.py [folder] [--out DIR] [--top N]
"""

from __future__ import annotations

import argparse
import csv
import sys
from collections import deque
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

REPO = Path(__file__).resolve().parent.parent
DEFAULT_ROOT = REPO / "client" / "Assets" / "Images" / "SpritesWeb"
# Full-screen art is not a cut-out; nothing here applies to it.
SKIP_PARTS = {"Backgrounds"}


def components(mask: np.ndarray) -> list[int]:
    """Sizes of the 8-connected components of a boolean mask, largest first."""
    h, w = mask.shape
    seen = np.zeros(mask.shape, bool)
    sizes = []
    for y0, x0 in zip(*np.nonzero(mask)):
        if seen[y0, x0]:
            continue
        size = 0
        queue = deque([(y0, x0)])
        seen[y0, x0] = True
        while queue:
            y, x = queue.popleft()
            size += 1
            for dy in (-1, 0, 1):
                for dx in (-1, 0, 1):
                    ny, nx = y + dy, x + dx
                    if 0 <= ny < h and 0 <= nx < w and mask[ny, nx] and not seen[ny, nx]:
                        seen[ny, nx] = True
                        queue.append((ny, nx))
        sizes.append(size)
    return sorted(sizes, reverse=True)


def measure(path: Path) -> dict:
    rgba = np.array(Image.open(path).convert("RGBA")).astype(np.int16)
    alpha = rgba[..., 3]
    h, w = alpha.shape
    visible = alpha > 24

    # The outline: visible pixels that touch a transparent one.
    transparent = ~visible
    near = np.zeros_like(transparent)
    near[1:, :] |= transparent[:-1, :]
    near[:-1, :] |= transparent[1:, :]
    near[:, 1:] |= transparent[:, :-1]
    near[:, :-1] |= transparent[:, 1:]
    outline = visible & near
    rgb = rgba[..., :3]
    pale = (rgb.min(axis=-1) >= 200) & ((rgb.max(axis=-1) - rgb.min(axis=-1)) <= 30)
    halo = float((outline & pale).sum()) / max(1, int(outline.sum()))

    # Islands apart from the subject. Measured on a half-size mask: specks are
    # what we look for, and a 512px walk per file is slow in pure Python.
    small = visible[::2, ::2]
    sizes = components(small) if small.any() else []
    main = sizes[0] if sizes else 0
    speck = sum(s for s in sizes[1:] if s < main * 0.02) * 4

    c = 3
    corners = [alpha[:c, :c], alpha[:c, -c:], alpha[-c:, :c], alpha[-c:, -c:]]
    corner = sum(int((q > 24).any()) for q in corners)

    if visible.any():
        rows = np.nonzero(visible.any(axis=1))[0]
        cols = np.nonzero(visible.any(axis=0))[0]
        bw, bh = cols[-1] - cols[0] + 1, rows[-1] - rows[0] + 1
        fill = (bw * bh) / float(w * h)
        cx, cy = (cols[0] + cols[-1]) / 2, (rows[0] + rows[-1]) / 2
        offset = max(abs(cx - (w - 1) / 2) / w, abs(cy - (h - 1) / 2) / h)
        margins = (int(cols[0]), int(w - 1 - cols[-1]), int(rows[0]), int(h - 1 - rows[-1]))
    else:
        fill, offset, margins = 0.0, 0.0, (0, 0, 0, 0)

    score = halo * 100 + min(speck, 4000) / 100 + corner * 10 + offset * 40
    return {
        "file": str(path),
        "w": w, "h": h,
        "halo_pct": round(halo * 100, 1),
        "speck_px": speck,
        "corners": corner,
        "fill_pct": round(fill * 100, 1),
        "offset_pct": round(offset * 100, 1),
        "margins_lrtb": "/".join(map(str, margins)),
        "score": round(score, 1),
    }


def sheet(rows: list[dict], root: Path, out: Path, title: str) -> None:
    cell, label = 168, 30
    cols = 6
    n = len(rows)
    grid = Image.new("RGB", (cols * cell, ((n + cols - 1) // cols) * (cell + label) + 30), (24, 22, 28))
    draw = ImageDraw.Draw(grid)
    draw.text((8, 8), title, fill=(230, 220, 200))
    for i, r in enumerate(rows):
        x, y = (i % cols) * cell, 30 + (i // cols) * (cell + label)
        img = Image.open(r["file"]).convert("RGBA")
        img.thumbnail((cell - 12, cell - 12), Image.LANCZOS)
        # A mid-grey frame shows the canvas edge, so cropping is visible.
        draw.rectangle([x + 4, y + 4, x + cell - 5, y + cell - 5], outline=(70, 66, 80))
        grid.paste(img, (x + (cell - img.width) // 2, y + (cell - img.height) // 2), img)
        name = Path(r["file"]).relative_to(root).as_posix()
        draw.text((x + 4, y + cell), name[-30:], fill=(200, 190, 170))
        draw.text((x + 4, y + cell + 13), f"halo {r['halo_pct']}% speck {r['speck_px']} c{r['corners']} off {r['offset_pct']}%", fill=(150, 145, 160))
    grid.save(out)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("folder", nargs="?", default=str(DEFAULT_ROOT))
    ap.add_argument("--out", default=str(REPO / "sprite-audit"))
    ap.add_argument("--top", type=int, default=48)
    a = ap.parse_args()
    root = Path(a.folder)
    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)

    files = [p for p in sorted(root.rglob("*")) if p.suffix.lower() in (".webp", ".png") and not (SKIP_PARTS & set(p.relative_to(root).parts))]
    rows = []
    for p in files:
        try:
            rows.append(measure(p))
        except Exception as e:  # a broken file is itself a finding
            print(f"  could not read {p}: {e}", file=sys.stderr)
    rows.sort(key=lambda r: r["score"], reverse=True)

    with open(out / "sprite-audit.csv", "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=list(rows[0].keys()))
        w.writeheader()
        w.writerows(rows)
    sheet(rows[: a.top], root, out / "worst.png", f"Worst {min(a.top, len(rows))} of {len(rows)} by score (halo, specks, corners, offset)")

    def count(pred):
        return sum(1 for r in rows if pred(r))
    print(f"{len(rows)} sprites measured -> {out}")
    print(f"  halo > 10% of outline:   {count(lambda r: r['halo_pct'] > 10)}")
    print(f"  specks > 200 px:         {count(lambda r: r['speck_px'] > 200)}")
    print(f"  opaque corners:          {count(lambda r: r['corners'] > 0)}")
    print(f"  off-centre > 5%:         {count(lambda r: r['offset_pct'] > 5)}")
    print(f"  median halo {np.median([r['halo_pct'] for r in rows]):.1f}%, median fill {np.median([r['fill_pct'] for r in rows]):.1f}%")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
