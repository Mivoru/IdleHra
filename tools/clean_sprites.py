"""Repairs the generated artwork and produces web-sized copies.

WHAT WAS WRONG. The art was generated on a solid white background and cut out
afterwards with a flood fill from the image borders. That reaches the OUTSIDE
of the subject and stops at the first dark line, so every background region
ENCLOSED by the art stayed white and fully opaque - the hole inside a
necklace's cord, the gaps between a monster's arch of branches. On the dark UI
those read as bright white blobs, which is what a player notices first.

The second, subtler damage is a white fringe: the anti-aliased edge pixels were
composited over white before the cut, so they keep a pale wash that haloes the
subject against any darker background.

WHAT THIS DOES NOT DO. It never touches the originals. The masters in
client/Assets/Images/Sprites stay exactly as generated; this writes repaired
copies to client/Assets/Images/SpritesWeb, which is what the server serves.
Re-running is safe and idempotent.

WHY WHITE ART SURVIVES. Real white in this art - a wolf tooth, a skull, bone -
is SHADED: it has gradient and ink detail, so its luminance varies. Leftover
background is flat: it is the same pixel value everywhere it was painted. So
the discriminator is variance, not colour, and a bone stays while a blank hole
goes. The thresholds below were chosen against the two worst files
(hunter_amulet, Malakor) and then checked not to eat anything on the rest.

Run:  python tools/clean_sprites.py
"""

from __future__ import annotations

import sys
from collections import deque
from pathlib import Path

import numpy as np
from PIL import Image

REPO = Path(__file__).resolve().parent.parent
SOURCE = REPO / "client" / "Assets" / "Images" / "Sprites"
OUTPUT = REPO / "client" / "Assets" / "Images" / "SpritesWeb"

# A pixel is "background-like" if it is bright and almost colourless. Both
# bounds are deliberately loose - being in this set only makes a pixel a
# CANDIDATE; the variance test below is what decides.
BACKGROUND_MIN_CHANNEL = 205
BACKGROUND_MAX_SATURATION = 26

# Below this a region is too small to be a leftover hole and too likely to be a
# highlight. Expressed as a fraction of the image so it survives a resolution
# change.
MIN_REGION_FRACTION = 0.0004

# Flat means background. Shaded means art. Measured on the luminance of the
# region's own pixels.
MAX_BACKGROUND_STDDEV = 6.0
MIN_BACKGROUND_MEAN = 232.0

# The longest edge of a written sprite. The masters are 2048x2048, which is
# roughly two megabytes each and is being drawn at forty pixels - the browser
# downscales in one step with a cheap filter and the result is both worse
# looking and about four hundred megabytes of transfer across the set.
#
# 512 rather than 256 because the largest use is a 4.5rem combat portrait,
# which is about 216 device pixels on a 3x phone - and leaves headroom for a
# bigger display later without regenerating.
TARGET_LONG_EDGE = 512

# WebP, not PNG. This art is hand-painted with gradients and texture, which is
# the case PNG compresses worst: 277 KB for Malakor against 106 KB here, with
# no difference anyone can see at icon size. Every browser and both Capacitor
# WebViews have supported it for years.
#
# ALPHA IS KEPT LOSSLESS while the colour is not. Lossy alpha is what produces
# the muddy halo people blame WebP for, and it would undo the fringe repair
# above; the cutout is the part that has to stay exact.
WEBP_QUALITY = 90
WEBP_ALPHA_QUALITY = 100


def load(path: Path) -> np.ndarray:
    return np.array(Image.open(path).convert("RGBA")).astype(np.int16)


def background_candidates(rgba: np.ndarray) -> np.ndarray:
    rgb = rgba[..., :3]
    alpha = rgba[..., 3]
    lowest = rgb.min(axis=-1)
    highest = rgb.max(axis=-1)
    return (alpha > 16) & (lowest >= BACKGROUND_MIN_CHANNEL) & ((highest - lowest) <= BACKGROUND_MAX_SATURATION)


def label_regions(mask: np.ndarray):
    """Connected components, four-connected, iterative.

    Hand-rolled rather than pulled from scipy: this is the only thing that
    would need it, and an asset script that fails to run because a dependency
    is missing is an asset script nobody runs.
    """
    height, width = mask.shape
    labels = np.zeros(mask.shape, np.int32)
    current = 0
    for start_y in range(height):
        row = mask[start_y]
        for start_x in range(width):
            if not row[start_x] or labels[start_y, start_x]:
                continue
            current += 1
            queue = deque([(start_y, start_x)])
            labels[start_y, start_x] = current
            while queue:
                y, x = queue.popleft()
                for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    ny, nx = y + dy, x + dx
                    if 0 <= ny < height and 0 <= nx < width and mask[ny, nx] and not labels[ny, nx]:
                        labels[ny, nx] = current
                        queue.append((ny, nx))
    return labels, current


def remove_enclosed_background(rgba: np.ndarray) -> tuple[np.ndarray, int]:
    """Clears flat white regions the original cut-out could not reach."""
    candidates = background_candidates(rgba)
    if not candidates.any():
        return rgba, 0

    # Work at quarter resolution. These regions are large by definition, the
    # component walk is the expensive part, and the mask is upsampled back
    # before anything is written - so nothing is lost but time.
    small = candidates[::4, ::4]
    labels, count = label_regions(small)
    if count == 0:
        return rgba, 0

    luma = (0.299 * rgba[..., 0] + 0.587 * rgba[..., 1] + 0.114 * rgba[..., 2])[::4, ::4]
    minimum_area = max(16, int(small.size * MIN_REGION_FRACTION))

    doomed = np.zeros(small.shape, bool)
    cleared = 0
    for label in range(1, count + 1):
        region = labels == label
        area = int(region.sum())
        if area < minimum_area:
            continue
        values = luma[region]
        if values.std() > MAX_BACKGROUND_STDDEV or values.mean() < MIN_BACKGROUND_MEAN:
            # Shaded, or not bright enough: this is art, not a hole.
            continue
        doomed |= region
        cleared += area

    if cleared == 0:
        return rgba, 0

    full = np.kron(doomed, np.ones((4, 4), bool))[: rgba.shape[0], : rgba.shape[1]]

    # Grow by one pixel so the anti-aliased rim of the hole goes with it,
    # otherwise a one-pixel white outline is left exactly where the blob was.
    grown = full.copy()
    grown[1:, :] |= full[:-1, :]
    grown[:-1, :] |= full[1:, :]
    grown[:, 1:] |= full[:, :-1]
    grown[:, :-1] |= full[:, 1:]

    rgba = rgba.copy()
    rgba[..., 3][grown] = 0
    return rgba, cleared * 16


def unmultiply_white_fringe(rgba: np.ndarray) -> np.ndarray:
    """Recovers edge colour that was blended into the white background.

    An anti-aliased edge pixel was `art * a + white * (1 - a)` before the cut,
    so the art's own colour is recoverable exactly by inverting that. Without
    it every sprite keeps a pale halo, which is invisible on the white it was
    drawn against and obvious on a dark UI.

    Only partially transparent pixels are touched. A fully opaque pixel was
    never blended and inverting it would blow out real highlights.
    """
    rgba = rgba.copy()
    alpha = rgba[..., 3].astype(np.float32) / 255.0
    edge = (alpha > 0.05) & (alpha < 0.95)
    if not edge.any():
        return rgba

    a = alpha[edge][:, None]
    observed = rgba[..., :3][edge].astype(np.float32)
    recovered = (observed - 255.0 * (1.0 - a)) / a
    rgba[..., :3][edge] = np.clip(recovered, 0, 255).astype(np.int16)
    return rgba


def trim(rgba: np.ndarray) -> np.ndarray:
    """Crops to the visible bounding box.

    The generator centred every subject on a full square, so many sprites are
    mostly empty. Trimming makes the art fill its icon instead of floating in
    the middle of one at half size - a quality gain before any resampling
    happens.
    """
    visible = rgba[..., 3] > 8
    if not visible.any():
        return rgba
    rows = np.nonzero(visible.any(axis=1))[0]
    cols = np.nonzero(visible.any(axis=0))[0]
    pad = 4
    top = max(0, rows[0] - pad)
    bottom = min(rgba.shape[0], rows[-1] + 1 + pad)
    left = max(0, cols[0] - pad)
    right = min(rgba.shape[1], cols[-1] + 1 + pad)
    return rgba[top:bottom, left:right]


def downscale(image: Image.Image) -> Image.Image:
    if max(image.size) <= TARGET_LONG_EDGE:
        return image
    scale = TARGET_LONG_EDGE / max(image.size)
    size = (max(1, round(image.width * scale)), max(1, round(image.height * scale)))

    # Premultiply before resampling. Resampling straight RGBA averages the
    # colour of fully transparent pixels into visible ones, which drags a black
    # or white halo in from whatever happened to be stored under the
    # transparency - the classic "dark rim after resize".
    array = np.array(image).astype(np.float32)
    alpha = array[..., 3:4] / 255.0
    array[..., :3] *= alpha
    premultiplied = Image.fromarray(array.astype(np.uint8), "RGBA").resize(size, Image.LANCZOS)

    out = np.array(premultiplied).astype(np.float32)
    a = out[..., 3:4] / 255.0
    np.divide(out[..., :3], a, out=out[..., :3], where=a > 0.004)
    return Image.fromarray(np.clip(out, 0, 255).astype(np.uint8), "RGBA")


def main() -> int:
    if not SOURCE.is_dir():
        print(f"no sprite source at {SOURCE}", file=sys.stderr)
        return 1

    sources = sorted(SOURCE.rglob("*.png"))
    if not sources:
        print("no sprites found", file=sys.stderr)
        return 1

    total_before = 0
    total_after = 0
    repaired = 0

    for source in sources:
        relative = source.relative_to(SOURCE).with_suffix(".webp")
        destination = OUTPUT / relative
        destination.parent.mkdir(parents=True, exist_ok=True)

        rgba = load(source)
        rgba, cleared = remove_enclosed_background(rgba)
        if cleared:
            repaired += 1
        rgba = unmultiply_white_fringe(rgba)
        rgba = trim(rgba)

        image = downscale(Image.fromarray(rgba.astype(np.uint8), "RGBA"))
        image.save(
            destination,
            "WEBP",
            quality=WEBP_QUALITY,
            alpha_quality=WEBP_ALPHA_QUALITY,
            method=6,
        )

        total_before += source.stat().st_size
        total_after += destination.stat().st_size
        if cleared:
            print(f"  repaired {relative} ({cleared:,} px of leftover background)")

    print()
    print(f"{len(sources)} sprites -> {OUTPUT.relative_to(REPO)}")
    print(f"  {repaired} had enclosed background left by the original cut-out")
    print(f"  {total_before / 1_048_576:.1f} MB -> {total_after / 1_048_576:.1f} MB")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
