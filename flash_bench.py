"""
A bench for tuning the muzzle flash, with no tank in it.

    import flash_bench
    flash_bench.sheet()          # writes out/_flash_bench.png and returns numbers

Renders the flash alone against a stub barrel, at every phase and at the three
headings that differ - across the screen, at the camera, away from it - and
assembles them into one contact sheet. Twenty-seven small renders, a couple of
seconds, against a minute for a full `tank_pipeline.run` that drags a million
vertices of tank through ninety-six frames to show the same change.

Nothing here is a separate asset
--------------------------------
The obvious idea is to build the flash once in its own .blend and link it into
each tank scene. It is the wrong shape for this problem. The flash has no
absolute size: it is specified in bore radii, so an asset would have to be
rescaled per tank anyway - and the numbers doing the rescaling *are* the asset.
Keeping it as code means one definition, no append step, and no three copies in
three files to drift apart. `muzzle_flash.CONFIG` is the shared thing, and this
bench reads exactly the same CONFIG the tank scenes do; a value that looks right
here is the value that renders there.

One pixel here is one pixel there
---------------------------------
The camera is sized so a render pixel equals an atlas pixel at the tank's
`units_per_pixel`. Sizes read off this sheet are therefore the sizes that land
in the atlas, and the tile-edge marker shows where the sprite frame will cut the
flash off - which is the one thing that genuinely differs between tanks, because
each gun reaches a different distance from the spin axis.

The bench borrows `render_atlas`'s habit of working in the current scene and
putting everything back, rather than making a scene of its own. A scene left
behind in MT.blend would be saved into it by the next Ctrl+S.
"""

import json
import math
import os

import bmesh
import bpy
import numpy as np
from mathutils import Matrix, Vector

REPO = r"D:\Projects\AgentCoding\BlenderMCP"

CONFIG = {
    "repo": REPO,
    "output": os.path.join(REPO, "out", "_flash_bench.png"),
    # where to read the tank's scale from, so the sheet is in atlas pixels.
    # None for both -> fall back to the stamped turret in this scene and to the
    # last rendered atlas.
    "atlas_json": os.path.join(REPO, "out", "hull_atlas.json"),
    "turret": "Turret",
    "bore_radius": None,
    "units_per_pixel": None,

    "cell": 200,                # render pixels per cell, i.e. atlas pixels
    "phases": [0, 1, 2, 3, 4, 5, 6, 7],
    # camera azimuth -> what the gun is doing. The stub always points -Y; it is
    # the camera that moves, which is one fewer transform to get wrong.
    "views": [(90.0, "across the screen"),
              (0.0, "at the camera"),
              (180.0, "away from the camera")],
    "elevation": 30.0,
    "stub_length": 7.0,         # bore radii of barrel behind the muzzle
    "background": 0.16,

    # the tile budget: how far the muzzle sits from the spin axis, and how much
    # room the frame has. None -> measured from the stamps in this scene.
    "reach_px": None,
    "half_tile": 128.0,
    "samples": 32,
}


def _load(cfg, name):
    import importlib.util
    spec = importlib.util.spec_from_file_location(
        name, os.path.join(cfg["repo"], "%s.py" % name))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def _scale(cfg):
    """Bore radius, units per pixel, and the muzzle's reach from the spin axis."""
    radius, upp, reach = cfg["bore_radius"], cfg["units_per_pixel"], cfg["reach_px"]
    turret = _load(cfg, "tank_parts").mesh(cfg["turret"], required=False)

    if upp is None and cfg["atlas_json"] and os.path.exists(cfg["atlas_json"]):
        with open(cfg["atlas_json"], encoding="utf-8") as fh:
            upp = json.load(fh)["units_per_pixel"]
    if upp is None:
        raise RuntimeError("no units_per_pixel: render a tank once, or set it")

    if radius is None:
        if turret is None or "muzzle_radius" not in turret.keys():
            raise RuntimeError("no bore_radius and no stamped turret to read it "
                               "from - run muzzle_point.set_muzzle() first")
        radius = float(turret["muzzle_radius"])

    if reach is None and turret is not None and "muzzle_point" in turret.keys() \
            and "ring_axis" in turret.keys():
        point = np.array([float(v) for v in turret["muzzle_point"]])
        ring = np.array([float(v) for v in turret["ring_axis"]])
        reach = float(np.hypot(*(point[:2] - ring))) / upp
    return float(radius), float(upp), reach


def _stub(name, radius, length):
    """A length of barrel, so the flash has something to be judged against."""
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=24,
                          radius1=radius, radius2=radius, depth=length)
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    ob = bpy.data.objects.new(name, me)
    # built along +Z about its middle; stand it along -Y with its face on the
    # origin, which is where the flash's muzzle is
    ob.matrix_world = (Matrix.Translation(Vector((0.0, length * 0.5, 0.0)))
                       @ Vector((0.0, 0.0, 1.0))
                       .rotation_difference(Vector((0.0, -1.0, 0.0)))
                       .to_matrix().to_4x4())
    return ob


def sheet(cfg=None):
    cfg = dict(CONFIG, **(cfg or {}))
    scene = bpy.context.scene
    r = scene.render
    flash_mod = _load(cfg, "muzzle_flash")
    atlas = _load(cfg, "sprite_atlas")

    radius, upp, reach = _scale(cfg)
    cell = int(cfg["cell"])
    phases, views = cfg["phases"], cfg["views"]

    muzzle = ((0.0, 0.0, 0.0), (0.0, -1.0, 0.0), radius)
    flash_cfg = {"name": "_bench_flash", "muzzle": muzzle}
    smoke_cfg = {"name": "_bench_smoke", "muzzle": muzzle}

    saved = {"engine": r.engine, "camera": scene.camera, "filepath": r.filepath,
             "res": (r.resolution_x, r.resolution_y, r.resolution_percentage),
             "transparent": r.film_transparent,
             "samples": scene.eevee.taa_render_samples,
             "hidden": {o: o.hide_render for o in scene.objects}}
    temp = []
    frames = os.path.join(os.path.dirname(cfg["output"]), "bench")
    os.makedirs(frames, exist_ok=True)

    try:
        flash = flash_mod.build(flash_cfg)
        temp.append(flash)
        smoke = flash_mod.build_smoke(smoke_cfg)
        temp.append(smoke)
        stub = _stub("_bench_stub", radius, radius * cfg["stub_length"])
        scene.collection.objects.link(stub)
        temp.append(stub)

        cam_data = bpy.data.cameras.new("_bench_cam")
        cam_data.type = "ORTHO"
        # this is the whole point: one render pixel is one atlas pixel
        cam_data.ortho_scale = upp * cell
        cam_data.clip_start = 0.001
        cam_data.clip_end = 100.0
        cam = bpy.data.objects.new("_bench_cam", cam_data)
        scene.collection.objects.link(cam)
        temp.append(cam)
        scene.camera = cam

        r.resolution_x = r.resolution_y = cell
        r.resolution_percentage = 100
        r.film_transparent = True
        scene.eevee.taa_render_samples = int(cfg["samples"])

        def aim(azimuth):
            rot = (Matrix.Rotation(math.radians(azimuth), 4, "Z")
                   @ Matrix.Rotation(math.pi / 2 - math.radians(cfg["elevation"]),
                                     4, "X"))
            forward = (rot @ Vector((0.0, 0.0, -1.0))).normalized()
            # centred on the muzzle, so the marker below is measured from a
            # known pixel rather than from wherever the silhouette landed
            cam.matrix_world = Matrix.Translation(-forward * 10.0) @ rot

        def shoot(tag):
            path = os.path.join(frames, "%s.png" % tag)
            r.filepath = path
            bpy.ops.render.render(write_still=True)
            return atlas.read_rgba(path)

        rows, cols = len(views), len(phases)
        board = np.zeros((rows * cell, cols * cell, 4), dtype=np.float32)
        board[:, :, :3] = cfg["background"]
        board[:, :, 3] = 1.0
        sizes = {}
        reach_side = {}

        for ri, (azimuth, label) in enumerate(views):
            aim(azimuth)
            for o in scene.objects:
                if o.type == "MESH":
                    o.hide_render = o is not stub
            r.engine = "BLENDER_WORKBENCH"
            barrel = shoot("stub_%03d" % ri)

            r.engine = "BLENDER_EEVEE"
            for ci, phase in enumerate(phases):
                flash_mod.set_phase(phase, len(phases), flash_cfg)
                flash_mod.set_smoke_phase(phase, len(phases), smoke_cfg)
                bpy.context.view_layer.update()

                for o in scene.objects:
                    if o.type == "MESH":
                        o.hide_render = o is not smoke
                fume = shoot("smoke_%03d_%03d" % (ri, ci))
                for o in scene.objects:
                    if o.type == "MESH":
                        o.hide_render = o is not flash
                fire = shoot("flash_%03d_%03d" % (ri, ci))

                y0 = (rows - ri - 1) * cell
                x0 = ci * cell
                cellbuf = board[y0:y0 + cell, x0:x0 + cell]
                for layer in (barrel, fume):
                    # smoke occludes: normal alpha, as the game will draw it
                    a = layer[:, :, 3:4]
                    cellbuf[:, :, :3] = (layer[:, :, :3] * a
                                         + cellbuf[:, :, :3] * (1.0 - a))
                # fire adds light: additive, as the game will draw it
                cellbuf[:, :, :3] = np.clip(
                    cellbuf[:, :, :3] + fire[:, :, :3] * fire[:, :, 3:4], 0.0, 1.0)

                mask = fire[:, :, 3] > 0.06
                if mask.any():
                    ys, xs = np.nonzero(mask)
                    # Reach is the longer arm from the muzzle, not the right
                    # one: whether the gun projects left or right depends on
                    # the azimuth, and deriving that sign is a way to get it
                    # backwards. Measured, it read 5px against a 51px wide
                    # flash - the whole thing was extending the other way.
                    half = cell // 2
                    arm = max(int(xs.max()) - half, half - int(xs.min()))
                    side = 1 if int(xs.max()) - half >= half - int(xs.min()) else -1
                    sizes.setdefault(label, {})[phase] = {
                        "w": int(xs.max() - xs.min() + 1),
                        "h": int(ys.max() - ys.min() + 1),
                        "reach_px": arm,
                    }
                    # from the biggest phase, not the last one: the final
                    # frames are a five-pixel ember whose two arms are equal to
                    # within noise, and taking the sign off those put the tile
                    # marker on the wrong side of the gun
                    if arm >= reach_side.get(label, (0, 1))[0]:
                        reach_side[label] = (arm, side)

            # where the sprite frame will cut. Only meaningful across the
            # screen, which is where the muzzle is furthest from the axis.
            if reach is not None and label.startswith("across"):
                side = reach_side.get(label, (0, 1))[1]
                edge = int(round(cell / 2.0
                                 + side * (cfg["half_tile"] - reach)))
                if 1 <= edge < cell - 1:
                    for ci in range(cols):
                        board[(rows - ri - 1) * cell:(rows - ri) * cell,
                              ci * cell + edge:ci * cell + edge + 2,
                              :3] = (0.9, 0.12, 0.10)

        img = bpy.data.images.new("_flash_bench", cols * cell, rows * cell,
                                  alpha=True, float_buffer=False)
        try:
            img.colorspace_settings.name = "Non-Color"
            img.alpha_mode = "CHANNEL_PACKED"
            img.pixels.foreach_set(board.reshape(-1))
            img.file_format = "PNG"
            img.filepath_raw = cfg["output"]
            img.save()
        finally:
            bpy.data.images.remove(img)

    finally:
        for o, hidden in saved["hidden"].items():
            o.hide_render = hidden
        for o in temp:
            data = o.data
            bpy.data.objects.remove(o)
            if isinstance(data, bpy.types.Camera):
                bpy.data.cameras.remove(data)
            else:
                bpy.data.meshes.remove(data)
        r.engine = saved["engine"]
        scene.camera = saved["camera"]
        r.filepath = saved["filepath"]
        r.resolution_x, r.resolution_y, r.resolution_percentage = saved["res"]
        r.film_transparent = saved["transparent"]
        scene.eevee.taa_render_samples = saved["samples"]
        bpy.context.view_layer.update()

    across = sizes.get("across the screen", {})
    longest = max((v["reach_px"] for v in across.values()), default=0)
    room = None if reach is None else cfg["half_tile"] - reach
    report = {
        "sheet": cfg["output"],
        "rows": [label for _, label in views],
        "columns": phases,
        "bore_radius": round(radius, 6),
        "bore_diameter_px": round(2.0 * radius / upp, 1),
        "units_per_pixel": upp,
        "muzzle_reach_px": None if reach is None else round(reach, 1),
        "room_in_tile_px": None if room is None else round(room, 1),
        "flash_reach_px": longest,
        "clips": None if room is None else bool(longest > room),
        "sizes": sizes,
    }
    if report["clips"]:
        print("[bench] the flash reaches %d px but the frame only has %d - it "
              "will be cut off at the tile edge" % (longest, room))
    print("[bench] %s" % cfg["output"])
    return report


if __name__ == "__main__":
    from pprint import pprint

    pprint(sheet(CONFIG))
