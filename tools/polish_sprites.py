"""Second pass over the served sprites: removes stray specks and re-crops.

Owner, 2026-11-03: "many monsters and items are not cropped well". Measured
with tools/audit_sprites.py: 50 sprites sat more than 5% off-centre, and the
cause was not the subject - it was a speck far from it. The image generator
left a small four-point sparkle (its watermark) and bits of dust in a corner;
the cut-out kept them, trim() in clean_sprites.py cropped to a box that
included them, and the subject ended up small and pushed to one side of its
icon.

WHAT THIS DOES, to every cut-out sprite in SpritesWeb:
  1. Drops small islands (under 2% of the subject's area) that are either FAR
     from the subject (outside its bounding box grown by 12%) or are the
     generator's WATERMARK - a faint (alpha < 90), colourless sparkle, which
     often sits inside that margin. Coloured or opaque sparkles, embers and
     snowflakes drawn around a subject stay.
  2. Clears near-invisible alpha noise (alpha <= 24) outside the subject's
     grown box, which a trim at alpha > 8 used to count as content.
  3. Re-crops to the visible subject with a margin proportional to its size
     (2% of the long edge, not a fixed 4px), so small and large sprites frame
     alike.

WHAT IT LEAVES ALONE: Backgrounds (not cut-outs) and anything with opaque
corners - the skill icons, avatars and boss portraits are square framed
pictures on purpose. It never generates or repaints pixels; it only removes
specks and crops. Idempotent: a second run changes nothing.

Run AFTER tools/clean_sprites.py (which rewrites the master-derived files):
    python tools/clean_sprites.py && python tools/polish_sprites.py
    python tools/polish_sprites.py --dry-run     # report only
"""

from __future__ import annotations

import argparse
import os
import sys
from collections import deque
from pathlib import Path

import numpy as np
from PIL import Image

REPO = Path(__file__).resolve().parent.parent
ROOT = REPO / "client" / "Assets" / "Images" / "SpritesWeb"
SKIP_PARTS = {"Backgrounds"}

VISIBLE_ALPHA = 24
SPECK_MAX_SHARE = 0.02      # of the subject's area
SUBJECT_MARGIN = 0.12       # grow the subject's box by this share of its long edge
CROP_MARGIN = 0.02          # crop margin, share of the subject's long edge
# The watermark test: a component whose strongest pixel is this faint and
# whose colour is this flat is generator residue, not art.
WATERMARK_MAX_ALPHA = 90
WATERMARK_MAX_SPREAD = 14
WEBP_QUALITY = 92
WEBP_ALPHA_QUALITY = 100


def label(mask: np.ndarray):
    """8-connected components: returns (labels, list of (size, y0, x0, y1, x1))."""
    h, w = mask.shape
    labels = np.zeros(mask.shape, np.int32)
    stats = []
    current = 0
    for y0, x0 in zip(*np.nonzero(mask)):
        if labels[y0, x0]:
            continue
        current += 1
        labels[y0, x0] = current
        queue = deque([(y0, x0)])
        size, ty, tx, by, bx = 0, y0, x0, y0, x0
        while queue:
            y, x = queue.popleft()
            size += 1
            ty, tx, by, bx = min(ty, y), min(tx, x), max(by, y), max(bx, x)
            for dy in (-1, 0, 1):
                for dx in (-1, 0, 1):
                    ny, nx = y + dy, x + dx
                    if 0 <= ny < h and 0 <= nx < w and mask[ny, nx] and not labels[ny, nx]:
                        labels[ny, nx] = current
                        queue.append((ny, nx))
        stats.append((size, ty, tx, by, bx))
    return labels, stats


def framed(alpha: np.ndarray) -> bool:
    c = 3
    corners = [alpha[:c, :c], alpha[:c, -c:], alpha[-c:, :c], alpha[-c:, -c:]]
    return sum(int((q > VISIBLE_ALPHA).all()) for q in corners) >= 2


def polish(rgba: np.ndarray) -> tuple[np.ndarray, int, bool]:
    """Returns (image, specks removed in px, whether anything changed)."""
    alpha = rgba[..., 3]
    visible = alpha > VISIBLE_ALPHA
    if not visible.any():
        return rgba, 0, False
    labels, stats = label(visible)
    order = sorted(range(len(stats)), key=lambda i: stats[i][0], reverse=True)
    main_size, my0, mx0, my1, mx1 = stats[order[0]]

    # The subject is the largest island plus every island that is not a
    # speck - big pieces (a detached sword hilt, a second fish) belong to it.
    subject = [i for i in order if stats[i][0] >= main_size * SPECK_MAX_SHARE]
    sy0 = min(stats[i][1] for i in subject); sx0 = min(stats[i][2] for i in subject)
    sy1 = max(stats[i][3] for i in subject); sx1 = max(stats[i][4] for i in subject)
    grow = int(round(max(sy1 - sy0, sx1 - sx0) * SUBJECT_MARGIN))
    gy0, gx0, gy1, gx1 = sy0 - grow, sx0 - grow, sy1 + grow, sx1 + grow

    out = rgba.copy()
    removed = 0
    for i in order:
        size, y0, x0, y1, x1 = stats[i]
        if size >= main_size * SPECK_MAX_SHARE:
            continue
        outside = y1 < gy0 or y0 > gy1 or x1 < gx0 or x0 > gx1
        pixels = labels == i + 1
        # The generator's watermark: a faint, colourless four-point sparkle,
        # often INSIDE the margin kept for real particles. Real sparkles and
        # snowflakes are near-opaque or coloured; this one is grey at alpha
        # under ~90, so it is dropped wherever it sits.
        rgb = rgba[..., :3][pixels].astype(np.int16)
        spread = float((rgb.max(axis=-1) - rgb.min(axis=-1)).mean()) if size else 0.0
        watermark = int(alpha[pixels].max()) < WATERMARK_MAX_ALPHA and spread < WATERMARK_MAX_SPREAD
        if outside or watermark:
            # With the faint rim around it, which sits below the visible cut.
            ry0, rx0 = max(0, y0 - 3), max(0, x0 - 3)
            rim = out[ry0:y1 + 4, rx0:x1 + 4]
            rim_rgb = rim[..., :3].astype(np.int16)
            faint_grey = (rim[..., 3] < WATERMARK_MAX_ALPHA) & ((rim_rgb.max(axis=-1) - rim_rgb.min(axis=-1)) < WATERMARK_MAX_SPREAD)
            rim[..., 3][faint_grey] = 0
            out[..., 3][pixels] = 0
            removed += size

    # Faint noise outside the subject's grown box.
    h, w = alpha.shape
    yy, xx = np.mgrid[0:h, 0:w]
    far = (yy < gy0) | (yy > gy1) | (xx < gx0) | (xx > gx1)
    faint = far & (out[..., 3] > 0) & (out[..., 3] <= VISIBLE_ALPHA)
    out[..., 3][faint] = 0

    # Crop to what is visible now, with a proportional margin.
    vis = out[..., 3] > VISIBLE_ALPHA
    rows = np.nonzero(vis.any(axis=1))[0]
    cols = np.nonzero(vis.any(axis=0))[0]
    pad = max(2, int(round(max(rows[-1] - rows[0], cols[-1] - cols[0]) * CROP_MARGIN)))
    top, bottom = max(0, rows[0] - pad), min(h, rows[-1] + 1 + pad)
    left, right = max(0, cols[0] - pad), min(w, cols[-1] + 1 + pad)
    cropped = out[top:bottom, left:right]
    changed = removed > 0 or bool(faint.any()) or cropped.shape != rgba.shape
    return cropped, removed, changed


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--root", default=str(ROOT))
    a = ap.parse_args()
    root = Path(a.root)

    files = [p for p in sorted(root.rglob("*.webp")) if not (SKIP_PARTS & set(p.relative_to(root).parts))]
    changed = skipped = specks = 0
    for p in files:
        rgba = np.array(Image.open(p).convert("RGBA"))
        if framed(rgba[..., 3]):
            skipped += 1
            continue
        out, removed, did = polish(rgba)
        if not did:
            continue
        changed += 1
        specks += removed
        rel = p.relative_to(root).as_posix()
        print(f"  {rel}: {rgba.shape[1]}x{rgba.shape[0]} -> {out.shape[1]}x{out.shape[0]}" + (f", {removed} px of specks" if removed else ""))
        if not a.dry_run:
            # Atomic: a run stopped mid-write must not leave a truncated sprite
            # (it happened once - the server then serves a file no browser reads).
            tmp = p.with_suffix(".tmp.webp")
            Image.fromarray(out, "RGBA").save(tmp, "WEBP", quality=WEBP_QUALITY, alpha_quality=WEBP_ALPHA_QUALITY, method=6)
            os.replace(tmp, p)
    print()
    print(f"{len(files)} sprites, {skipped} framed (left alone), {changed} {'would change' if a.dry_run else 'polished'}, {specks:,} px of specks removed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
