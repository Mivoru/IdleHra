"""Turns the hand-painted backgrounds and UI buttons into the WebP set the
web client actually serves.

Modul: the source art is 2752x1536 PNG, four to eight megabytes each. That
is a master, not something to ship - nine of them is 45 MB of page weight
for images that render at a fraction of the size. This crops the buttons to
their own content (both arrive on a mostly empty transparent canvas),
downscales everything to a sane display size and writes WebP.

Separate from clean_sprites.py on purpose: that script exists to REPAIR a
white background that a cut-out left behind, and none of these need it - the
scenes are full-bleed and the buttons already carry real alpha.
"""
import pathlib
from PIL import Image

SRC = pathlib.Path('client/Assets/Images/WithWhiteBackground/Background')
DST = pathlib.Path('client/Assets/Images/SpritesWeb/Backgrounds')

# The five locations, plus the hub. Wide enough to fill a desktop banner
# without being a master.
SCENES = {
    'Whispering Woods.png': ('whispering_woods', 1600),
    'The Murky Swamps.png': ('the_murky_swamps', 1600),
    'Craggy Highlands.png': ('craggy_highlands', 1600),
    'Ancient Ruins.png': ('ancient_ruins', 1600),
    'Abyssal Breach.png': ('abyssal_breach', 1600),
    'main_bg.png': ('main_hub', 1920),
}

# Buttons are cropped to their alpha bounds first - both sit in the middle of
# an otherwise empty 2752x1536 canvas, so shipping them uncropped would ship
# mostly nothing at all.
BUTTONS = {
    'curcular_button.png': ('button_round', 512),
    'rectangular_button.png': ('button_wide', 768),
}


def write(image: Image.Image, name: str, width: int) -> None:
    DST.mkdir(parents=True, exist_ok=True)
    if image.width > width:
        height = round(image.height * width / image.width)
        image = image.resize((width, height), Image.LANCZOS)
    out = DST / f'{name}.webp'
    image.save(out, 'WEBP', quality=88, method=6, alpha_quality=100)
    print(f'  {name:18s} {image.width}x{image.height}  {out.stat().st_size // 1024} KB')


def main() -> None:
    print('scenes:')
    for source, (name, width) in SCENES.items():
        path = SRC / source
        if not path.exists():
            print(f'  MISSING {source}')
            continue
        write(Image.open(path).convert('RGB'), name, width)

    print('buttons:')
    for source, (name, width) in BUTTONS.items():
        path = SRC / source
        if not path.exists():
            print(f'  MISSING {source}')
            continue
        image = Image.open(path).convert('RGBA')
        image = crop_to_plate(image)
        write(image, name, width)


def crop_to_plate(image: Image.Image) -> Image.Image:
    """Crops to the wooden plate, ignoring stray marks elsewhere on the canvas.

    Modul: getbbox() was cropping to EVERY non-transparent pixel, and the round
    button's canvas carries a tiny speck near the far right edge. So the bbox
    ran 1817px wide for a 1262px disc, the disc sat 276px left of the middle of
    its own file, and every label - which is centred on the button box - drew
    that far to the right of the wood. It looked like a CSS centring bug and
    was not one.

    Coverage per row and column instead: a speck a few pixels tall cannot reach
    the threshold, and a disc spanning most of the image comfortably does.
    """
    import numpy as np

    alpha = np.array(image)[:, :, 3] > 8
    rows = alpha.sum(axis=1)
    cols = alpha.sum(axis=0)

    # A tenth of the opposite dimension - far above any speck, far below any
    # real row of the plate.
    row_floor = alpha.shape[1] * 0.10
    col_floor = alpha.shape[0] * 0.10

    row_hits = np.nonzero(rows > row_floor)[0]
    col_hits = np.nonzero(cols > col_floor)[0]
    if row_hits.size == 0 or col_hits.size == 0:
        box = image.getbbox()
        return image.crop(box) if box else image

    return image.crop((int(col_hits[0]), int(row_hits[0]),
                       int(col_hits[-1]) + 1, int(row_hits[-1]) + 1))


if __name__ == '__main__':
    main()
