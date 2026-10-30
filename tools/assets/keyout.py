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

Modul: THE KEY IS PURE GREEN because no chest, ticket or icon in the asset
list is green, while magenta sits next to the epic chest's violet glow.
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


def keyout(src, dst, key, inner=60, outer=150):
    im = Image.open(src).convert('RGB')
    kr, kg, kb = key
    px = im.load()
    w, h = im.size
    out = Image.new('RGBA', im.size)
    po = out.load()
    for y in range(h):
        for x in range(w):
            r, g, b = px[x, y]
            d = ((r - kr) ** 2 + (g - kg) ** 2 + (b - kb) ** 2) ** 0.5
            a = 0 if d <= inner else 255 if d >= outer else int(255 * (d - inner) / (outer - inner))
            if a and g > max(r, b):  # despill: green may not exceed the other two
                g = max(r, b)
            po[x, y] = (r, g, b, a)
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
