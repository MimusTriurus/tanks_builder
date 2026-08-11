"""Trim every frame to what it actually draws, and pack the results.

The atlases are a uniform grid, one cell per frame, and the cell is sized for
the *worst* frame the layer holds: 384px because the burning column needs it,
though the typical muzzle flash fills two per cent of that. Measured on MTP,
the whole eighteen-layer set is 741MB decoded and the tight bounding boxes of
its 2133 frames come to 41MB - six per cent. Packed with a one-pixel guard band
it lands at 47MB, a factor of 15.7, and the three tanks in the bench go from
2.21GB to 142MB.

This is the one thing Westwood's SHP did that transfers here unchanged. A frame
in that format carries its own x, y, width and height, so a blank frame costs a
header and a small explosion costs a small rectangle. Their other three tricks
do not survive a GPU: an eight-bit palette is replaced by block compression, the
XOR-against-the-previous-frame delta needs a decoded rectangle in video memory,
and colour remapping has nothing here to remap.

**The tile stays the coordinate system, and that is what keeps this small.**
Everything measured in tile pixels - the anchor, the muzzle point, the plate
table, hit offsets, the gauge - is untouched and stays true. Trimming changes
exactly two things: where the pixels are fetched from, and where the quad is
drawn. Two numbers per frame, `rect` and `off`, and a reader that has neither
falls back to the grid.

Pure numpy and no I/O, because it has two callers that cannot share one: the
renderer runs inside Blender where the only image API is `bpy`, and the repacker
runs in ordinary Python over atlases that already shipped. `repack_dir` is that
second entry point and it needs Pillow, which is why the import is inside it.

Top-down throughout. Blender hands out bottom-up buffers and `write_atlas`
flips on the way in and out; doing it anywhere else means two conventions in one
file, and `anchor_px` is already documented as pixels from the top-left corner.
"""

import json
import os

import numpy as np

#: Transparent pixels left between packed frames and around the edge.
#:
#: Not needed today - the bench sets `default_texture_filter=0`, nearest, which
#: is also why the grid gets away with no gap at all. It is here because a
#: trimmed frame has nonzero alpha on its own boundary **by construction**, so
#: the day anyone turns on linear filtering every frame starts bleeding into
#: whatever got packed beside it. Measured on MTP: 44.5MB at zero, 47.2 at one,
#: 49.3 at two. Six per cent is a cheap way to not have that conversation.
GAP = 1

#: Widths tried when packing. Powers of two because some drivers still care,
#: and the search picks whichever gives the smallest area rather than the
#: narrowest atlas - a tall thin atlas wastes the same memory as a wide one.
WIDTHS = (128, 256, 512, 1024, 2048, 4096, 8192)


def bbox(tile):
    """Tight box of the drawn pixels as (x, y, w, h), or None if empty.

    Empty is a real answer and worth having: 101 of MTP's 2133 frames are
    blank, all of them scars on a plate the hull is standing in front of, and
    under the grid each one costs a full cell.
    """
    rows = np.nonzero(tile[:, :, 3].any(axis=1))[0]
    if rows.size == 0:
        return None
    cols = np.nonzero(tile[:, :, 3].any(axis=0))[0]
    return (int(cols[0]), int(rows[0]),
            int(cols[-1] - cols[0] + 1), int(rows[-1] - rows[0] + 1))


def _shelf(boxes, width, gap):
    """Lay boxes out in rows of `width`, tallest first. Returns (height, xy).

    A shelf packer rather than anything cleverer because of what these frames
    are: within a layer they are the same object at twenty-four headings, so
    they are within a few pixels of each other in height and the shelves come
    out nearly full. Measured slack over the tight boxes is 3.5MB on 41 - eight
    per cent - which is not worth a skyline for.
    """
    order = sorted(boxes, key=lambda item: -item[1][3])
    place = {}
    x = y = tall = 0
    for index, (_, _, w, h) in order:
        w += gap
        h += gap
        if x + w > width:
            x = 0
            y += tall
            tall = 0
        place[index] = (x + gap, y + gap)
        x += w
        tall = max(tall, h)
    return y + tall + gap, place


def pack(tiles, gap=GAP):
    """Trim and pack `tiles` (top-down HxWx4 arrays, or None for a blank).

    Returns `(atlas, placements)`, where a placement is
    `{"rect": [x, y, w, h], "off": [x, y]}` - where the pixels ended up, and
    where the box sat inside its tile so the caller can put the quad back in
    the right place. A blank frame gets None.
    """
    boxes = []
    for index, tile in enumerate(tiles):
        box = None if tile is None else bbox(tile)
        if box is not None:
            boxes.append((index, box))

    if not boxes:
        # A layer with nothing in it at all. One transparent pixel rather than a
        # zero-sized image, which no texture API is happy to be handed.
        return (np.zeros((1, 1, 4), dtype=tiles[0].dtype if tiles else np.float32),
                [None] * len(tiles))

    widest = max(box[2] for _, box in boxes) + 2 * gap
    best = None
    for width in WIDTHS:
        if width < widest:
            continue
        height, place = _shelf(boxes, width, gap)
        if best is None or width * height < best[0] * best[1]:
            best = (width, height, place)
    width, height, place = best

    sample = next(t for t in tiles if t is not None)
    atlas = np.zeros((height, width, 4), dtype=sample.dtype)
    placements = [None] * len(tiles)
    for index, (bx, by, bw, bh) in boxes:
        x, y = place[index]
        atlas[y:y + bh, x:x + bw] = tiles[index][by:by + bh, bx:bx + bw]
        placements[index] = {"rect": [x, y, bw, bh], "off": [bx, by]}
    return atlas, placements


def alongside(tiles, placements, shape):
    """Lay a second set of the same frames out on placements already decided.

    The depth atlas is that second set, and it is not packed independently on
    purpose: two runs of a shelf packer over two sets of boxes agree only for as
    long as the boxes agree, and the boxes here **cannot** agree, because a depth
    frame is opaque wherever the colour frame drew anything at all - including
    where the colour frame drew something almost transparent. Packed on its own
    the depth atlas would come out with its own rectangles and its own size, and
    every frame would fetch depth from beside itself. Nothing about that reads as
    wrong in a number; it reads as a tank whose near track is at the depth of its
    far one.

    So the colour atlas decides, and this crops the depth frame with the colour
    frame's box. Which is also the only box worth keeping: the game samples depth
    only where it draws a pixel, and outside the colour box it draws none.
    """
    sample = next((t for t in tiles if t is not None), None)
    if sample is None:
        return np.zeros((1, 1, 4), dtype=np.float32)
    atlas = np.zeros((shape[0], shape[1], sample.shape[2]), dtype=sample.dtype)
    for tile, place in zip(tiles, placements):
        if tile is None or place is None:
            continue
        x, y, w, h = place["rect"]
        ox, oy = place["off"]
        atlas[y:y + h, x:x + w] = tile[oy:oy + h, ox:ox + w]
    return atlas


# ---------------------------------------------------------------------------
# reading a set that already shipped
# ---------------------------------------------------------------------------

def tiles_of(pixels, meta, top_down=True):
    """Split an assembled atlas back into tile-sized frames.

    Reads both layouts, which is what makes `repack_dir` safe to run twice: a
    packed set comes apart by its own rectangles and goes back together the same
    way. `pixels` is top-down unless told otherwise.
    """
    size = meta["tile"]
    tw, th = (size, size) if isinstance(size, int) else (size[0], size[1])
    cols = meta["grid"]["columns"]
    rows = meta["grid"]["rows"]
    if not top_down:
        pixels = pixels[::-1]
    out = []
    for frame in meta["frames"]:
        tile = np.zeros((th, tw, 4), dtype=pixels.dtype)
        rect = frame.get("rect")
        if rect is not None:
            x, y, w, h = rect
            ox, oy = frame.get("off", [0, 0])
            tile[oy:oy + h, ox:ox + w] = pixels[y:y + h, x:x + w]
        elif "rect" not in frame:
            col = frame.get("col", frame["index"] % cols)
            row = frame.get("row", frame["index"] // cols)
            y0 = row * th
            tile[:] = pixels[y0:y0 + th, col * tw:(col + 1) * tw]
        out.append(tile)
    return out


def repack_dir(path, gap=GAP, dry_run=False):
    """Repack every `*_atlas.png` in `path` in place, and report the saving.

    The point of having this separately from the renderer is that it needs no
    Blender and no re-render: the packing is a pure function of the pixels, so
    a set that took twelve minutes to make converts in seconds and the pixels
    that come out are the same pixels. Which is the only claim worth checking -
    see the composite test rather than these numbers.
    """
    from PIL import Image
    Image.MAX_IMAGE_PIXELS = None

    report = []
    for name in sorted(os.listdir(path)):
        if not name.endswith("_atlas.json"):
            continue
        meta_path = os.path.join(path, name)
        with open(meta_path, encoding="utf-8") as fh:
            meta = json.load(fh)
        png = os.path.join(path, meta["atlas"])
        pixels = np.asarray(Image.open(png).convert("RGBA"))
        before = pixels.shape[1] * pixels.shape[0]

        tiles = tiles_of(pixels, meta)
        atlas, placements = pack(tiles, gap)
        for frame, place in zip(meta["frames"], placements):
            if place is None:
                frame["rect"] = None
                frame.pop("off", None)
            else:
                frame["rect"] = place["rect"]
                frame["off"] = place["off"]
        meta["packed"] = True

        after = atlas.shape[1] * atlas.shape[0]
        report.append((meta["layer"], before, after))
        if dry_run:
            continue
        Image.fromarray(atlas, "RGBA").save(png)
        with open(meta_path, "w", encoding="utf-8") as fh:
            json.dump(meta, fh, indent=2)
    return report
