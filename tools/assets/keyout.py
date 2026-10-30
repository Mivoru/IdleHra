"""Key colour helpers for tools/assets/gemini.mjs.

  python tools/assets/keyout.py flatten IN.png OUT.png [#00ff00] [scale]
      Put a transparent PNG on the flat key colour, so it can be sent to
      Gemini as a reference (a transparent reference reads as black). A scale
      below 1 shrinks the object into the LOWER middle of the canvas: Gemini
      keeps the reference's framing, so an opened lid needs the room above.
  python tools/assets/keyout.py key IN.png OUT.png [#rrggbb]
      Remove the key colour (default: sampled from the corners): alpha from the distance to it, then despill so
      gold edges do not keep a green fringe.
  python tools/assets/keyout.py preview OUT.jpg IN.png [IN.png ...]
      Each image on a dark and a light floor, side by side, for review.

Modul: THE KEY IS PURE GREEN because almost nothing in the asset list is
green, while magenta sits next to the epic chest's violet glow. Green things
(Koschei's soul light, the wisp) are painted on MAGENTA instead; the key is
sampled from the corners, so keyout follows whichever was used.
"""
import sys
from PIL import Image, ImageChops


def parse(hexcolour):
    h = hexcolour.lstrip('#')
    return tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))


def flatten(src, dst, key, scale=1.0):
    im = Image.open(src).convert('RGBA')
    floor = Image.new('RGBA', im.size, key + (255,))
    if scale < 1.0:
        obj = im.crop(im.getchannel('A').getbbox())
        target = int(min(im.size) * scale)
        obj.thumbnail((target, target))
        x = (im.width - obj.width) // 2
        y = im.height - obj.height - im.height // 12
        floor.alpha_composite(obj, (x, y))
    else:
        floor.alpha_composite(im)
    floor.convert('RGB').save(dst)


def sampled_key(src):
    """The key as the model actually painted it: the corners' mean colour.

    Modul: "pure green" comes back anywhere from #00ff00 to #04bd04, and a
    darker green sits outside a distance threshold built around #00ff00 -
    two tickets kept their whole background that way. The corners are
    background by the prompt's own margin rule, so they ARE the key.
    """
    im = Image.open(src).convert('RGB')
    w, h = im.size
    px = [im.getpixel(p) for p in ((4, 4), (w - 5, 4), (4, h - 5), (w - 5, h - 5))]
    return tuple(sum(p[i] for p in px) // 4 for i in range(3))


def keyout(src, dst, key, solid=0.25, clear=0.6):
    """Alpha from GREENNESS (g - max(r, b)) as a share of the key's own.

    Modul: NOT FROM RGB DISTANCE. A key painted as dark as (6, 151, 32) sits
    about 120 away from black, so a distance threshold keyed Koschei's black
    cloak half away. Greenness tells them apart at any brightness: the cloak,
    grey stone and gold all have g <= max(r, b), the key has g far above.
    """
    im = Image.open(src).convert('RGB')
    # The key's own channels (above half) against the rest: green -> g vs
    # (r, b); magenta -> (r, b) vs g, for assets that are green themselves.
    hot = [i for i in range(3) if key[i] >= 128] or [1]
    cold = [i for i in range(3) if i not in hot] or [0]
    keyness = lambda c: min(c[i] for i in hot) - max(c[i] for i in cold)
    kgreen = max(1, keyness(key))
    px = im.load()
    w, h = im.size
    out = Image.new('RGBA', im.size)
    po = out.load()
    for y in range(h):
        for x in range(w):
            r, g, b = px[x, y]
            c = [r, g, b]
            share = keyness(c) / kgreen
            a = 255 if share <= solid else 0 if share >= clear else int(255 * (clear - share) / (clear - solid))
            if a and share > 0:  # despill: the key's channels may not exceed the rest
                top = max(c[i] for i in cold)
                for i in hot:
                    c[i] = min(c[i], top)
            po[x, y] = (c[0], c[1], c[2], a)
    # Modul: A MODEL SOMETIMES IGNORES THE KEY and paints its own floor; then
    # nothing keys out and the "transparent" asset is a grey square. Say so.
    corners = [po[0, 0][3], po[w - 1, 0][3], po[0, h - 1][3], po[w - 1, h - 1][3]]
    if max(corners) > 0:
        print(f'WARNING {src}: the background is not the key colour (corner alpha {corners}); regenerate', file=sys.stderr)
    out = out.crop(out.getchannel('A').point(lambda v: 255 if v > 8 else 0).getbbox() or (0, 0, w, h))
    out.save(dst)


def preview(dst, sources):
    tiles = []
    for s in sources:
        im = Image.open(s).convert('RGBA')
        im.thumbnail((420, 420))
        tiles.append(im)
    width = sum(t.width + 20 for t in tiles) + 20
    sheet = Image.new('RGB', (width, 2 * 440 + 20), (24, 24, 30))
    light = Image.new('RGB', (width, 440), (236, 234, 224))
    sheet.paste(light, (0, 460))
    x = 20
    for t in tiles:
        sheet.paste(t, (x, 20), t)
        sheet.paste(t, (x, 480), t)
        x += t.width + 20
    sheet.save(dst, quality=88)


if __name__ == '__main__':
    mode = sys.argv[1]
    if mode == 'preview':
        preview(sys.argv[2], sys.argv[3:])
    else:
        key = parse(sys.argv[4]) if len(sys.argv) > 4 else (0, 255, 0)
        if mode == 'flatten':
            flatten(sys.argv[2], sys.argv[3], key, float(sys.argv[5]) if len(sys.argv) > 5 else 1.0)
        else:
            keyout(sys.argv[2], sys.argv[3], key if len(sys.argv) > 4 else sampled_key(sys.argv[2]))
