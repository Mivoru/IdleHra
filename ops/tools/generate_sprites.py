"""Converts white-background reference art in client/Assets/Images/WithWhiteBackground
into transparent-background sprites under client/Assets/Images/Sprites.

Usage: python ops/tools/generate_sprites.py [--force]
  --force reprocesses files even if the destination already exists.

Requires: pip install pillow numpy

After running, re-import in Unity (assets-refresh) and set each new PNG's
TextureImporter to Sprite/Single (see set_sprite_import snippet run via
script-execute during the session that introduced this script - the
importer settings are NOT set by this script, only the pixels).
"""
import sys
from pathlib import Path
from collections import deque

import numpy as np
from PIL import Image

REPO_ROOT = Path(__file__).resolve().parents[2]
SRC_ROOT = REPO_ROOT / "client" / "Assets" / "Images" / "WithWhiteBackground"
DST_ROOT = REPO_ROOT / "client" / "Assets" / "Images" / "Sprites"


def remove_white_bg(img: Image.Image, near_white=240, full_white=252):
    """Flood-fills near-white pixels connected to the image border to find the
    true background region (so enclosed white highlights, e.g. armor shine,
    survive), feathers alpha at the boundary to preserve antialiasing, and
    decontaminates edge colors of their white bleed."""
    arr = np.asarray(img.convert("RGB"), dtype=np.float32)
    h, w, _ = arr.shape
    minc = arr.min(axis=2)
    candidate = minc >= near_white

    visited = np.zeros((h, w), dtype=bool)
    dq = deque()
    for x in range(w):
        for y in (0, h - 1):
            if candidate[y, x] and not visited[y, x]:
                visited[y, x] = True
                dq.append((y, x))
    for y in range(h):
        for x in (0, w - 1):
            if candidate[y, x] and not visited[y, x]:
                visited[y, x] = True
                dq.append((y, x))

    while dq:
        y, x = dq.popleft()
        for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            ny, nx = y + dy, x + dx
            if 0 <= ny < h and 0 <= nx < w and candidate[ny, nx] and not visited[ny, nx]:
                visited[ny, nx] = True
                dq.append((ny, nx))

    bg_mask = visited

    alpha = np.ones((h, w), dtype=np.float32)
    white_ramp = np.clip((minc - near_white) / max(1, (full_white - near_white)), 0, 1)
    alpha[bg_mask] = 1.0 - white_ramp[bg_mask]
    alpha = np.clip(alpha, 0, 1)

    a3 = alpha[..., None]
    safe_a = np.where(a3 < 0.04, 1.0, a3)
    fg = (arr - (1 - safe_a) * 255.0) / safe_a
    fg = np.clip(fg, 0, 255)

    out = np.dstack([fg, alpha * 255.0]).astype(np.uint8)
    return Image.fromarray(out, mode="RGBA"), bg_mask


def find_split_column(bg_mask, search_frac=(0.3, 0.7)):
    """Finds the gap column between two side-by-side subjects (used for the
    Characters folder's male/female pairs) by locating the column with the
    least foreground content within the central search window."""
    h, w = bg_mask.shape
    fg_col_count = (~bg_mask).sum(axis=0)
    lo, hi = int(w * search_frac[0]), int(w * search_frac[1])
    window = fg_col_count[lo:hi]
    return lo + int(np.argmin(window))


def autocrop(img: Image.Image, pad=15):
    arr = np.asarray(img)
    alpha = arr[..., 3]
    ys, xs = np.where(alpha > 8)
    if len(xs) == 0:
        return img
    x0, x1 = max(0, xs.min() - pad), min(arr.shape[1], xs.max() + pad + 1)
    y0, y1 = max(0, ys.min() - pad), min(arr.shape[0], ys.max() + pad + 1)
    return img.crop((x0, y0, x1, y1))


def process_single(src_path: Path, dst_path: Path):
    img = Image.open(src_path)
    rgba, _ = remove_white_bg(img)
    rgba = autocrop(rgba)
    dst_path.parent.mkdir(parents=True, exist_ok=True)
    rgba.save(dst_path)
    return rgba.size


def process_character_pair(src_path: Path, dst_dir: Path, stem: str):
    img = Image.open(src_path)
    rgba, bg_mask = remove_white_bg(img)
    split_x = find_split_column(bg_mask)
    left = autocrop(rgba.crop((0, 0, split_x, rgba.height)))
    right = autocrop(rgba.crop((split_x, 0, rgba.width, rgba.height)))
    dst_dir.mkdir(parents=True, exist_ok=True)
    left.save(dst_dir / f"{stem}_Male.png")
    right.save(dst_dir / f"{stem}_Female.png")
    return split_x, left.size, right.size


def main():
    force = "--force" in sys.argv
    report = []

    chars_dir = SRC_ROOT / "Characters"
    for f in sorted(chars_dir.glob("*.png")):
        stem = f.stem
        out_dir = DST_ROOT / "Characters"
        if not force and (out_dir / f"{stem}_Male.png").exists():
            continue
        split_x, lsize, rsize = process_character_pair(f, out_dir, stem)
        report.append(f"[char] {f.name}: split@{split_x} Male={lsize} Female={rsize}")

    locations_dir = SRC_ROOT / "Locations"
    if locations_dir.is_dir():
        for loc_dir in sorted(locations_dir.iterdir()):
            if not loc_dir.is_dir():
                continue
            for sub in ("Monsters", "Materials&rest"):
                sub_dir = loc_dir / sub
                if not sub_dir.is_dir():
                    continue
                for f in sorted(sub_dir.glob("*.png")):
                    rel = f.relative_to(SRC_ROOT)
                    dst = DST_ROOT / rel
                    if not force and dst.exists():
                        continue
                    size = process_single(f, dst)
                    report.append(f"[loc] {rel}: {size}")

    others_dir = SRC_ROOT / "Others"
    if others_dir.is_dir():
        for f in sorted(others_dir.glob("*.png")):
            rel = f.relative_to(SRC_ROOT)
            dst = DST_ROOT / rel
            if not force and dst.exists():
                continue
            size = process_single(f, dst)
            report.append(f"[other] {rel}: {size}")

    if not report:
        print("Nothing new to process (use --force to reprocess existing outputs).")
        return

    print("\n".join(report))
    print(f"\nTotal processed: {len(report)}")


if __name__ == "__main__":
    main()
