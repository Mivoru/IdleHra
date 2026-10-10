"""Cuts the seasonal-event artwork out of its white background.

The event art (avatars, pets, the boss, the currency icon) arrives as JPEGs
painted on solid white, unlike the sprite masters that clean_sprites.py
repairs, which were already cut out. So this does the cut itself, then reuses
clean_sprites' repairs for what a border cut cannot reach.

THE CUT. Background is the near-white region CONNECTED TO THE IMAGE BORDER.
Connectivity is what keeps the art's own whites: a ghost's pale body is
enclosed by an ink outline, so the border region cannot reach it. JPEG noise
makes the white not quite flat, so the colour bounds are looser than an exact
match - the pale glow around a ghost goes with the background, which is right
on a dark UI.

THE EDGE. A hard 0/255 mask leaves a jagged, white-rimmed outline. The pixels
just inside the cut get an alpha from how far they are from white, and then
clean_sprites' unmultiply recovers their colour - the same fringe repair the
sprites get.

AVATARS ARE CROPPED TO THE HEAD (owner, 2026-10-09). A full figure drawn at
portrait size is a smudge; the face is what identifies it. The crop is a
square box given per file as fractions of the cut-out's bounding box, chosen
by looking at each one - a heuristic would put the box on a raised hand.

Never touches the masters. Writes WebP into SpritesWeb/Events, which is what
the server serves. Re-running is safe.

Run:  python tools/key_event_art.py
"""

from __future__ import annotations

import sys
from pathlib import Path

import numpy as np
from PIL import Image

try:
    from scipy import ndimage
except ImportError:  # pragma: no cover - a message beats a stack trace
    print("key_event_art.py needs scipy:  pip install scipy", file=sys.stderr)
    raise SystemExit(1)

sys.path.insert(0, str(Path(__file__).resolve().parent))
from clean_sprites import (  # noqa: E402
    WEBP_ALPHA_QUALITY,
    WEBP_QUALITY,
    downscale,
    trim,
    unmultiply_white_fringe,
)

REPO = Path(__file__).resolve().parent.parent
MASTERS = REPO / "client" / "Assets" / "Images" / "WithWhiteBackground"
OUTPUT = REPO / "client" / "Assets" / "Images" / "SpritesWeb" / "Events"

# Background candidates: bright and nearly colourless. Looser than
# clean_sprites' bounds because JPEG ringing puts colour noise into the white.
BACKGROUND_MIN_CHANNEL = 222
BACKGROUND_MAX_SATURATION = 34

# How many pixels inside the cut get a soft alpha, and the brightness at which
# such a pixel counts as fully opaque.
EDGE_BAND = 3
OPAQUE_BELOW = 200

# Event masters -> published name. Published names are slugs because the
# client builds URLs from them; the masters keep the owner's file names.
SAMHAIN = {
    "Halloween/currency/PumpkinCurrency.jpg": "samhain/currency/pumpkin.webp",
    "Halloween/boss/The Cailleach.jpg": "samhain/boss/cailleach.webp",
    "Halloween/pets/Black cat.jpg": "samhain/pets/black_cat.webp",
    "Halloween/pets/ghostie.jpg": "samhain/pets/ghostie.webp",
    "Halloween/pets/mini vampire.jpg": "samhain/pets/mini_vampire.webp",
    "Halloween/pets/Pixie.jpg": "samhain/pets/pixie.webp",
    "Halloween/pets/skeleton dog.jpg": "samhain/pets/skeleton_dog.webp",
    "Halloween/pets/Witch.jpg": "samhain/pets/witch.webp",
    "Halloween/pets/wolf pup.jpg": "samhain/pets/wolf_pup.webp",
    "Halloween/avatars/Banshee.jpg": "samhain/avatars_v2/banshee.webp",
    "Halloween/avatars/Dullahan.jpg": "samhain/avatars_v2/dullahan.webp",
    "Halloween/avatars/Jack-o'-lantern.jpg": "samhain/avatars_v2/jack_o_lantern.webp",
    "Halloween/avatars/Pooka.jpg": "samhain/avatars_v2/pooka.webp",
    "Halloween/avatars/pumpkin.jpg": "samhain/avatars_v2/pumpkin.webp",
    "Halloween/avatars/Skeleton.jpg": "samhain/avatars_v2/skeleton.webp",
    "Halloween/avatars/Vampire.jpg": "samhain/avatars_v2/vampire.webp",
    "Halloween/avatars/Warewolf.jpg": "samhain/avatars_v2/werewolf.webp",
}

# Head crops for avatars: (centre x, centre y, side), each a fraction of the
# cut-out's bounding box width (x) or height (y, side). Tuned by eye.
# Owner, 2026-10-10: the monster pets - permanent, found on any kill, not an
# event's. Their masters sit beside the other sprite masters rather than under
# WithWhiteBackground, and they are written to SpritesWeb/Pets (not Events),
# because they belong to no event. Keys are relative to client/Assets/Images.
PETS_ROOT = REPO / "client" / "Assets" / "Images"
PETS_OUTPUT = REPO / "client" / "Assets" / "Images" / "SpritesWeb" / "Pets"
PETS = {
    "Sprites/pets/Bäckahäst.jpg": "backahast.webp",
    "Sprites/pets/Cait Sídhe.jpg": "cait_sidhe.webp",
    "Sprites/pets/Cú Sídhe.jpg": "cu_sidhe.webp",
    "Sprites/pets/Dagda's Cauldron.jpg": "dagdas_cauldron.webp",
    "Sprites/pets/Hugin.jpg": "hugin.webp",
    "Sprites/pets/Ignis Fatuus.jpg": "ignis_fatuus.webp",
    "Sprites/pets/Kikimora.jpg": "kikimora.webp",
    "Sprites/pets/Llamhigyn y Dŵr.jpg": "llamhigyn_y_dwr.webp",
    "Sprites/pets/Nisse.jpg": "nisse.webp",
    "Sprites/pets/Ogham Monolith.jpg": "ogham_monolith.webp",
}

AVATAR_CROPS: dict[str, tuple[float, float, float]] = {
    "banshee": (0.5, 0.2, 0.4),
    "vampire": (0.5, 0.2, 0.4),
    # Re-centred 2026-10-10 against the round shop portrait (owner: the
    # pumpkins and the skeleton sat off-centre in the circle). Written to
    # avatars_v2/ because /sprites/* is served immutable - a re-cut under the
    # old name would never reach a browser that had the first one.
    "skeleton": (0.53, 0.12, 0.34),
    # The rider is headless; the box takes his torso and the head he holds up.
    "dullahan": (0.6, 0.2, 0.45),
    "jack_o_lantern": (0.41, 0.16, 0.4),
    "pooka": (0.85, 0.3, 0.46),
    # A face IS the pumpkin, low in the figure under its vine.
    "pumpkin": (0.47, 0.56, 0.7),
    "werewolf": (0.64, 0.24, 0.46),
}
DEFAULT_AVATAR_CROP = (0.5, 0.2, 0.4)


def cut_out(rgb: np.ndarray) -> np.ndarray:
    """Returns RGBA with the border-connected white made transparent."""
    lowest = rgb.min(axis=-1)
    highest = rgb.max(axis=-1)
    candidates = (lowest >= BACKGROUND_MIN_CHANNEL) & ((highest - lowest) <= BACKGROUND_MAX_SATURATION)

    labels, _ = ndimage.label(candidates)
    border = np.unique(np.concatenate([labels[0], labels[-1], labels[:, 0], labels[:, -1]]))
    border = border[border != 0]
    background = np.isin(labels, border)

    alpha = np.where(background, 0, 255).astype(np.float32)

    # Soft edge: a band just inside the cut fades by brightness.
    band = ndimage.binary_dilation(background, iterations=EDGE_BAND) & ~background
    fade = np.clip((255.0 - lowest.astype(np.float32)) / (255.0 - OPAQUE_BELOW), 0.0, 1.0)
    alpha[band] = 255.0 * fade[band]

    rgba = np.dstack([rgb.astype(np.int16), alpha.astype(np.int16)])
    return rgba


# Enclosed white: background trapped inside the art (between a goat's horns,
# under an arm). clean_sprites decides this by flatness at std <= 6, which
# JPEG noise defeats, so the bound here is looser; real white in this art is
# shaded and stays well above it.
ENCLOSED_MIN_MEAN = 236.0
ENCLOSED_MAX_STDDEV = 9.0
ENCLOSED_MIN_FRACTION = 0.0002
# Art whose own white is nearly flat - the ghost's head reads as a hole by the
# test above and came out with bites taken out of it. These keep every
# enclosed white; only the border cut applies.
KEEP_ENCLOSED_WHITE = {"ghostie"}


def remove_enclosed_white(rgba: np.ndarray) -> np.ndarray:
    rgb = rgba[..., :3]
    lowest = rgb.min(axis=-1)
    highest = rgb.max(axis=-1)
    candidates = (rgba[..., 3] > 16) & (lowest >= BACKGROUND_MIN_CHANNEL) & ((highest - lowest) <= BACKGROUND_MAX_SATURATION)
    labels, count = ndimage.label(candidates)
    if count == 0:
        return rgba
    luma = 0.299 * rgb[..., 0] + 0.587 * rgb[..., 1] + 0.114 * rgb[..., 2]
    index = np.arange(1, count + 1)
    area = ndimage.sum(candidates, labels, index)
    mean = ndimage.mean(luma, labels, index)
    std = ndimage.standard_deviation(luma, labels, index)
    doomed_ids = index[(area >= candidates.size * ENCLOSED_MIN_FRACTION) & (mean >= ENCLOSED_MIN_MEAN) & (std <= ENCLOSED_MAX_STDDEV)]
    if doomed_ids.size == 0:
        return rgba
    doomed = np.isin(labels, doomed_ids)
    band = ndimage.binary_dilation(doomed, iterations=EDGE_BAND) & ~doomed
    fade = np.clip((255.0 - lowest.astype(np.float32)) / (255.0 - OPAQUE_BELOW), 0.0, 1.0)
    rgba = rgba.copy()
    alpha = rgba[..., 3].astype(np.float32)
    alpha[doomed] = 0
    alpha[band] = np.minimum(alpha[band], 255.0 * fade[band])
    rgba[..., 3] = alpha.astype(np.int16)
    return rgba


def crop_avatar(rgba: np.ndarray, crop: tuple[float, float, float]) -> np.ndarray:
    visible = rgba[..., 3] > 8
    rows = np.nonzero(visible.any(axis=1))[0]
    cols = np.nonzero(visible.any(axis=0))[0]
    top, bottom, left, right = rows[0], rows[-1], cols[0], cols[-1]
    box_w, box_h = right - left, bottom - top

    cx, cy, side = crop
    size = int(side * box_h)
    centre_x = left + int(cx * box_w)
    centre_y = top + int(cy * box_h)
    # Pad with transparency rather than clamp, so a head near the edge stays
    # centred and the portrait stays square.
    padded = np.pad(rgba, ((size, size), (size, size), (0, 0)))
    y0 = centre_y - size // 2 + size
    x0 = centre_x - size // 2 + size
    return padded[y0:y0 + size, x0:x0 + size]


def main() -> int:
    only = sys.argv[1] if len(sys.argv) > 1 else None
    written = 0
    jobs = [(MASTERS / m, OUTPUT, o) for m, o in SAMHAIN.items()]
    jobs += [(PETS_ROOT / m, PETS_OUTPUT, o) for m, o in PETS.items()]
    for master, output, out_rel in jobs:
        if only and only not in out_rel:
            continue
        master_rel = str(master.relative_to(REPO))
        if not master.is_file():
            print(f"missing master: {master_rel}", file=sys.stderr)
            return 1

        rgb = np.array(Image.open(master).convert("RGB"))
        rgba = cut_out(rgb)
        if Path(out_rel).stem not in KEEP_ENCLOSED_WHITE:
            rgba = remove_enclosed_white(rgba)
        rgba = unmultiply_white_fringe(rgba)
        rgba = trim(rgba)
        if "/avatars" in out_rel:
            rgba = crop_avatar(rgba, AVATAR_CROPS.get(Path(out_rel).stem, DEFAULT_AVATAR_CROP))

        destination = output / out_rel
        destination.parent.mkdir(parents=True, exist_ok=True)
        image = downscale(Image.fromarray(np.clip(rgba, 0, 255).astype(np.uint8), "RGBA"))
        image.save(destination, "WEBP", quality=WEBP_QUALITY, alpha_quality=WEBP_ALPHA_QUALITY, method=6)
        written += 1
        print(f"  {out_rel}  {image.width}x{image.height}  {destination.stat().st_size // 1024} KB")

    print(f"{written} images -> {OUTPUT.relative_to(REPO)}, {PETS_OUTPUT.relative_to(REPO)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
