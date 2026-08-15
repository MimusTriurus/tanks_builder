"""
Render a 360-degree isometric sprite atlas for an object hierarchy in Blender.

Run from Blender's Text Editor (Alt+P), or from the command line:

    blender scene.blend --background --python sprite_atlas.py

Nothing here is specific to any one model: `target` picks a hierarchy, and
everything else in the scene is hidden for the render.

Rendering parts separately - a hull and a turret that rotate independently -
needs three settings working together, or the sprites will not line up:

    exclude     hides objects that sit inside the target hierarchy
    fit_to      sizes the frame from a *different* set of objects, so every
                pass shares one scale instead of being fitted to its own part
    spin_pivot  the vertical axis both the spin and the camera are centred on,
                normally the turret ring, so one pixel means the same world
                point in every atlas

With those set the same way for each pass, the parts can simply be drawn on
top of each other at `anchor_px` and they fit.

Output:
    <OUTPUT_DIR>/<NAME>_atlas.png    the atlas itself (RGBA, transparent bg)
    <OUTPUT_DIR>/<NAME>_atlas.json   tile size, grid, per-frame angles, anchor
    <OUTPUT_DIR>/frames/*.png        individual sprites (KEEP_FRAMES)
"""

import json
import math
import os

import bpy
import numpy as np
from mathutils import Matrix, Vector

import atlas_pack

CONFIG = {
    # --- what to render -----------------------------------------------------
    # Root of the hierarchy to render, by name. Children come with it;
    # everything else in the scene is hidden. None -> the active object.
    "target": "world",
    # Name hints of objects to hide even though they are inside the target.
    "exclude": [],
    # Name hints of objects that are not drawn but still block: they render as
    # holdouts, punching zero alpha wherever they stand in front.
    #
    # A layer composited on top of the others is normally right, because the
    # thing it holds is in front. An effect is the exception - a muzzle flash
    # fired away from the camera is *behind* the turret, and drawn on top it
    # would sit on the turret roof like a sticker. Naming the tank here gets
    # the occlusion baked into the flash's own alpha, which is the only place
    # it can live: the game draws flat layers and has no depth to test.
    "holdout": [],
    # Name hints of objects that stay in the render but are invisible to the
    # camera: they light nothing and draw nothing, they only cast shadows.
    #
    # The opposite of a holdout and not a variant of it. A holdout is *seen* by
    # the camera and punches alpha where it stands; a caster is not seen at all,
    # and the only trace of it is the shadow it throws on whatever is being
    # rendered. That is the whole of a ground-shadow pass: the tile is the
    # target and the tank is a caster.
    #
    # Spun with the turntable like a holdout, and for the same reason - a
    # shadow cast by a tank frozen at angle zero is worse than no shadow.
    "casters": [],
    # Name hints of the objects that determine the frame size and centre.
    # None -> the rendered set. Point every pass at the same set to keep one
    # shared scale across separately rendered parts.
    "fit_to": None,
    # Objects that are framed but never spun - ground tiles, terrain. Keeping
    # them out of `fit_to` matters: the fitter sweeps `fit_to` through every
    # angle, and a hex tile swept around its own centre demands a frame far
    # larger than the tile itself, which costs resolution for nothing.
    "fit_static": [],
    # Angles used for fitting, when they differ from the frames being rendered.
    # A one-frame ground-tile pass needs the *turntable's* angles here, or its
    # frame will not match the pass that actually spins.
    "fit_angles": None,
    # [x, y] in world units: the axis to spin around and centre the frame on.
    # None -> the centre of the fitted bounds.
    "spin_pivot": None,

    "name": "sprite",
    "output_dir": r"D:\Projects\AgentCoding\BlenderMCP\out",

    # Import convention: these models always arrive nose along -Y, so the hull
    # faces 270 deg. This is a stated convention, not a measurement - the
    # headlights are the only real evidence in the geometry and reading them
    # needs eyes. split_turret.py stamps a `front_dir_guess` from the gun's
    # reach, which cross-checks this well enough to catch a model imported back
    # to front, but its margin is thin on a boxy turret, so it never overrides
    # the convention. Change this if the import orientation ever changes.
    "front_dir": 270.0,
    # Optional per-frame labels (len == steps) and extra keys, both copied
    # straight into the metadata JSON.
    "frame_labels": None,
    "meta_extra": None,

    # --- turntable ----------------------------------------------------------
    "steps": 16,               # frames over the full 360 deg (16 -> 22.5 deg)
    "start_angle": 0.0,        # deg, offset of the first frame
    "direction": "ccw",        # "ccw" or "cw", as seen from above
    # "object": the model spins, camera and lights stay put -> lighting is
    #           identical in every frame (what 2D games normally want).
    # "camera": the camera orbits the model -> lighting rotates with the view.
    "spin": "object",

    # --- effect phases ------------------------------------------------------
    # For a layer that also changes over time - a muzzle flash, a burning
    # wreck. Every phase is rendered at every angle, and the atlas comes out
    # angle across, phase down, so a game reads it as [phase][facing].
    #
    # `phase_hook(index, count)` is called once per phase to put the effect in
    # that state. It may do anything, including moving the layer's root: the
    # root transform is restored and re-read after every call, so the spin is
    # applied on top of whatever the hook left rather than fighting it.
    "phases": 1,
    "phase_hook": None,

    # --- isometric view -----------------------------------------------------
    # 30.0     = classic isometric
    # 26.565   = 2:1 "pixel" isometric, i.e. degrees(atan(1/2))
    # 35.264   = true isometric, a cube's body diagonal, degrees(atan(1/sqrt(2)))
    "elevation": 30.0,
    # Base horizontal angle of the camera. This has to line up with the tile
    # shape's mirror axes, or the tile projects skewed and stops reading as a
    # grid cell: 45 deg for square tiles, a multiple of 30 deg for hexes (their
    # axes sit every 30 deg, so 45 lands exactly between two of them).
    # On a flat-top hex, 0/60/120 render it flat-top on screen, 30/90/150
    # render it pointy-top; both tessellate.
    "azimuth": 0.0,
    "orthographic": True,      # False -> perspective at "focal_length"
    "focal_length": 85.0,
    "padding": 1.04,           # >1 leaves a margin around the silhouette
    "center_vertically": True, # fit the silhouette instead of the pivot point

    # --- image --------------------------------------------------------------
    "tile": 256,               # pixels per sprite, square
    # Per-layer frame size, as a multiple of `tile`. The camera widens by the
    # same factor, so `units_per_pixel` is untouched and the layer still
    # composites on the others - it just has more room around the same picture.
    #
    # This is the one thing that loosens `framing_identical`, so it is worth
    # saying what the invariant actually is. It was never "one ortho_scale and
    # one anchor in pixels"; it is "one world distance per pixel, and one
    # anchor at the same place in the frame". Those two survive a bigger tile;
    # the pixel numbers do not, so the check compares the real thing now.
    #
    # An effect needs it and the tank does not: the muzzle already sits 68.8px
    # out from the spin axis on MT, leaving 59 of the 128 half-tile, and a
    # flash worth looking at is longer than that.
    "tile_scale": 1.0,
    # None -> leave the scene's look alone, which is what every layer that is a
    # picture wants. "Raw" for a layer whose pixels are a measurement: see
    # sprite_height.py, where the byte is the height of the surface under it and
    # a tone curve would be a lie told in the units the reader trusts.
    "view_transform": None,
    "columns": None,           # None -> near-square grid
    "samples": 64,             # render samples
    "keep_frames": True,       # keep the per-frame PNGs next to the atlas

    # --- lighting -----------------------------------------------------------
    # True  -> hide the scene's lights and use a neutral 3-point rig fixed to
    #          the camera, so every sprite and every pass is lit the same.
    # False -> keep the scene's own lighting.
    "own_lighting": True,
    "key_energy": 4.0,
    "fill_energy": 1.2,
    "rim_energy": 2.0,
    "world_strength": 0.25,    # ambient; None -> leave the world alone
    # None -> the three-point rig above. Otherwise a list of
    # {tag, energy, azimuth, elevation, angle} replacing it wholesale.
    #
    # Wholesale rather than by tweaking the three, because the one pass that
    # wants something else wants *one* sun: three suns throw three shadows, and
    # the fill sits 20 deg above the horizon, so its shadow is nearly three
    # times the tank's height and would be blamed on the pass rather than on
    # the rig. See ground_shadow.py.
    "light_rig": None,

    # --- writing the frames -------------------------------------------------
    # How many times to try a frame whose *save* failed, and how long to wait
    # between tries. Not a render failure and not this code's fault: a full set
    # is around a thousand small PNGs written into one directory in a couple of
    # minutes, and on Windows something outside Blender - a scanner, an indexer,
    # a preview thumbnailer - holds a file it has just seen for long enough that
    # overwriting it raises "cannot save".
    #
    # Measured here: of three sixteen-layer runs one wrote all 1065 frames and
    # two died, at `flash` and at `smoke_008`, and the file that could not be
    # written was writable seconds later. So it is transient and it is per-file,
    # which is exactly what a retry fixes and what nothing else does.
    #
    # It is worth having because of what a failure costs: the exception comes out
    # of the middle of a ten-minute job, the atlases written so far stay on disk
    # looking finished, and the ones not reached keep the *previous* run's
    # timestamps - a half-and-half set that reads as complete. Ten minutes and a
    # misleading directory, against one retry.
    "save_retries": 4,
    "save_wait": 0.75,         # seconds, doubling each try
}


# ---------------------------------------------------------------------------
# helpers
# ---------------------------------------------------------------------------

def hierarchy(root):
    """root plus all of its descendants."""
    out, stack = [root], [root]
    while stack:
        for child in stack.pop().children:
            out.append(child)
            stack.append(child)
    return out


def by_hints(hints):
    """Every object whose name contains any of `hints`, plus its descendants."""
    found = []
    for hint in hints or ():
        needle = hint.lower()
        for ob in bpy.context.scene.objects:
            if needle in ob.name.lower():
                found.extend(hierarchy(ob))
    return set(found)


RENDERABLE = {"MESH", "CURVE", "SURFACE", "META", "FONT"}


def corners_of(objects):
    """World-space bounding-box corners of everything renderable in `objects`."""
    pts = []
    for ob in objects:
        if ob.type not in RENDERABLE:
            continue
        m = ob.matrix_world
        pts.extend(m @ Vector(c) for c in ob.bound_box)
    return pts


def bounds_of(points):
    lo = Vector(tuple(min(p[i] for p in points) for i in range(3)))
    hi = Vector(tuple(max(p[i] for p in points) for i in range(3)))
    return lo, hi


def camera_matrix(pivot, azimuth_deg, elevation_deg, distance):
    """A camera at `azimuth`/`elevation` on a sphere around `pivot`."""
    az = math.radians(azimuth_deg)
    el = math.radians(elevation_deg)
    rot = Matrix.Rotation(az, 4, "Z") @ Matrix.Rotation(math.pi / 2 - el, 4, "X")
    forward = (rot @ Vector((0.0, 0.0, -1.0))).normalized()   # cameras look -Z
    return Matrix.Translation(pivot - forward * distance) @ rot


def frame_angles(cfg):
    step = 360.0 / cfg["steps"]
    sign = -1.0 if cfg["direction"] == "cw" else 1.0
    return [cfg["start_angle"] + sign * i * step for i in range(cfg["steps"])]


def spin_matrix(pivot, angle_deg):
    return (Matrix.Translation(pivot)
            @ Matrix.Rotation(math.radians(angle_deg), 4, "Z")
            @ Matrix.Translation(-pivot))


# ---------------------------------------------------------------------------
# scene setup
# ---------------------------------------------------------------------------

def build_lighting(cfg, cam):
    """A 3-point rig oriented relative to the camera. Returns what it made.

    The tilts are given as a sun's angle above the horizon, which is what they
    mean, rather than as the rotation about X that produces it: a sun points
    down -Z at rest, so the two are complements and the rig's 35 deg of tilt is
    a sun 55 deg up. Getting that backwards costs nothing until something wants
    to reason about shadow length, and then it costs the whole argument.
    """
    made = []
    rig = cfg.get("light_rig") or (
        {"tag": "key",  "energy": cfg["key_energy"],
         "azimuth": -35.0, "elevation": 55.0},
        {"tag": "fill", "energy": cfg["fill_energy"],
         "azimuth": 60.0, "elevation": 20.0},
        {"tag": "rim",  "energy": cfg["rim_energy"],
         "azimuth": 160.0, "elevation": 40.0},
    )
    for spec in rig:
        tag = spec.get("tag", "sun")
        data = bpy.data.lights.new("_atlas_%s" % tag, type="SUN")
        data.energy = spec["energy"]
        data.angle = math.radians(spec.get("angle", 6.0))
        ob = bpy.data.objects.new("_atlas_%s" % tag, data)
        bpy.context.scene.collection.objects.link(ob)
        offset = (Matrix.Rotation(math.radians(spec["azimuth"]), 4, "Z")
                  @ Matrix.Rotation(math.radians(90.0 - spec["elevation"]), 4, "X"))
        ob.matrix_world = Matrix.Translation(cam.matrix_world.translation) @ offset
        made.append(ob)
    return made


def fit_camera(cfg, cam, pivot, spun_corners, static_corners, angles, distance):
    """Size the camera so every frame of every pass shares one framing."""
    ext_x = 0.0
    lo_y, hi_y = float("inf"), float("-inf")

    def account(points):
        nonlocal ext_x, lo_y, hi_y
        for p in points:
            ext_x = max(ext_x, abs(p.x))
            lo_y = min(lo_y, p.y)
            hi_y = max(hi_y, p.y)

    for angle in angles:
        spin = spin_matrix(pivot, angle)
        if cfg["spin"] == "object":
            view = cam.matrix_world.inverted()
            account(view @ (spin @ p) for p in spun_corners)
        else:
            view = camera_matrix(pivot, cfg["azimuth"] + angle,
                                 cfg["elevation"], distance).inverted()
            account(view @ p for p in spun_corners)
        account(view @ p for p in static_corners)

    shift_y = 0.0
    if cfg["center_vertically"]:
        shift_y = (hi_y + lo_y) / 2.0        # camera-space offset of the mass
        ext_y = (hi_y - lo_y) / 2.0
    else:
        ext_y = max(abs(lo_y), abs(hi_y))

    half = max(ext_x, ext_y) * cfg["padding"]
    cam.data.ortho_scale = 2.0 * half
    # shift_* is in units of the larger sensor dimension, which for a square
    # render is exactly ortho_scale
    cam.data.shift_y = shift_y / (2.0 * half) if half else 0.0
    return 2.0 * half, cam.data.shift_y


# ---------------------------------------------------------------------------
# atlas assembly
# ---------------------------------------------------------------------------

def read_rgba(path):
    """Load a PNG as raw bottom-up float RGBA, bypassing colour management."""
    img = bpy.data.images.load(path, check_existing=False)
    try:
        img.colorspace_settings.name = "Non-Color"   # no sRGB decode
        img.alpha_mode = "CHANNEL_PACKED"            # no premultiply
        w, h = img.size
        buf = np.empty(w * h * 4, dtype=np.float32)
        img.pixels.foreach_get(buf)
        return buf.reshape(h, w, 4)
    finally:
        bpy.data.images.remove(img)


def write_atlas(paths, tile, columns, out_path):
    """Assemble the rendered frames into one image.

    Each frame is trimmed to what it draws and the results are packed - see
    `atlas_pack`, which owns that and is shared with the repacker. The grid the
    frames were rendered on survives as `columns`/`rows` in the metadata,
    because it is still the frame *table*: which index is which phase at which
    heading. What stops being true is that the index says where the pixels are.

    Returns `(columns, rows, placements)`, one placement per frame in index
    order, None where the frame came out empty.
    """
    rows = math.ceil(len(paths) / columns)
    tiles = []
    for path in paths:
        pix = read_rgba(path)
        if pix.shape[0] != tile or pix.shape[1] != tile:
            raise RuntimeError("frame %s is %dx%d, expected %dx%d"
                               % (path, pix.shape[1], pix.shape[0], tile, tile))
        tiles.append(pix[::-1])          # Blender hands these out bottom-up

    atlas, placements = atlas_pack.pack(tiles)
    height, width = atlas.shape[0], atlas.shape[1]

    img = bpy.data.images.new("_atlas", width, height,
                              alpha=True, float_buffer=False)
    try:
        img.colorspace_settings.name = "Non-Color"
        img.alpha_mode = "CHANNEL_PACKED"
        img.pixels.foreach_set(atlas[::-1].reshape(-1))   # and want them back
        img.file_format = "PNG"
        img.filepath_raw = out_path
        img.save()
    finally:
        bpy.data.images.remove(img)
    return columns, rows, placements


def _render_still(path, retries, wait):
    """Render one frame, and try again if the *save* is what failed.

    Blender reports a refused write as `RuntimeError: Error: Render error (No
    error) cannot save: <path>`, and the "(No error)" is the tell - the render
    itself was fine and the file is what would not open. Retrying is therefore
    safe: nothing about the scene has changed, so the second attempt draws the
    same pixels.

    Anything else is re-raised untouched. A retry loop that swallowed real render
    failures would turn a broken scene into four broken renders and a longer
    wait, which is worse than the original failure by exactly the time it wastes.
    """
    import time
    delay = float(wait)
    for attempt in range(max(1, int(retries))):
        try:
            bpy.ops.render.render(write_still=True)
            return attempt
        except RuntimeError as exc:
            if "cannot save" not in str(exc) or attempt == int(retries) - 1:
                raise
            print("[atlas] could not write %s (try %d) - waiting %.2fs"
                  % (os.path.basename(path), attempt + 1, delay))
            time.sleep(delay)
            delay *= 2.0
    return 0


# ---------------------------------------------------------------------------
# main
# ---------------------------------------------------------------------------

def render_atlas(cfg):
    scene = bpy.context.scene
    root = bpy.data.objects[cfg["target"]] if cfg["target"] else bpy.context.active_object
    if root is None:
        raise RuntimeError("no target object")

    excluded = by_hints(cfg.get("exclude"))
    rendered = [o for o in hierarchy(root) if o not in excluded]
    corners = corners_of(rendered)
    if not corners:
        raise RuntimeError("nothing renderable under %r after exclusions"
                           % root.name)

    # explicit object lists win over name hints: render_set() resolves the sets
    # once for the whole job and passes them down, so every layer is fitted to
    # exactly the same geometry
    if cfg.get("fit_objects") is not None:
        fit_objects = set(cfg["fit_objects"])
    elif cfg.get("fit_to"):
        fit_objects = by_hints(cfg["fit_to"])
    else:
        fit_objects = set(rendered)
    if cfg.get("fit_static_objects") is not None:
        static_objects = set(cfg["fit_static_objects"])
    else:
        static_objects = by_hints(cfg.get("fit_static"))

    # A phase hook that edits mesh data - a track belt sliding along its loop -
    # changes the very bound boxes this reads, and it has already run by the
    # time the *next* layer starts fitting. So render_set() measures the corners
    # once for the whole job and hands them down, the same way it hands down the
    # spin pivot and for the same reason. Caught by the belts: the two layers
    # rendered after a phase sweep came out 0.29% smaller than the two before it.
    if cfg.get("fit_corners") is not None:
        spun_corners = [Vector(p) for p in cfg["fit_corners"]] or corners
        static_corners = [Vector(p) for p in (cfg.get("fit_static_corners") or ())]
    else:
        spun_corners = corners_of(fit_objects) or corners
        static_corners = corners_of(static_objects)
    fit_lo, fit_hi = bounds_of(spun_corners + static_corners)

    if cfg.get("spin_pivot"):
        px, py = (float(v) for v in cfg["spin_pivot"])
    else:
        px, py = (fit_lo.x + fit_hi.x) / 2.0, (fit_lo.y + fit_hi.y) / 2.0
    pivot = Vector((px, py, (fit_lo.z + fit_hi.z) / 2.0))

    angles = frame_angles(cfg)
    phases = max(1, int(cfg.get("phases") or 1))
    # a near-square grid packs a plain turntable tightly, but once there are
    # phases the grid means something: one row per phase, one column per angle
    columns = cfg["columns"] or (len(angles) if phases > 1
                                 else math.ceil(math.sqrt(len(angles))))
    frames_dir = os.path.join(cfg["output_dir"], "frames")
    os.makedirs(frames_dir, exist_ok=True)

    # ---- remember everything we are about to change ------------------------
    r = scene.render
    saved = {
        "camera": scene.camera,
        "res": (r.resolution_x, r.resolution_y, r.resolution_percentage),
        "aspect": (r.pixel_aspect_x, r.pixel_aspect_y),
        "transparent": r.film_transparent,
        "filepath": r.filepath,
        "format": r.image_settings.file_format,
        "color_mode": r.image_settings.color_mode,
        "depth": r.image_settings.color_depth,
        "view_transform": scene.view_settings.view_transform,
        "use_nodes": scene.use_nodes,
        "root_matrix": root.matrix_world.copy(),
        "hidden": {},
        "holdout": {},
        "camera_visible": {},
        "world_strength": None,
    }
    temp = []

    try:
        # ---- isolate what we render --------------------------------------
        keep = set(rendered)
        blockers = by_hints(cfg.get("holdout")) - keep
        # a caster that is also a holdout would be both seen and not seen, so
        # the two lists are made disjoint here rather than left to whichever
        # branch runs first
        # `exclude` bites here too, and it has to: a caster hint names a layer
        # root, and under a track root sit both the belt that is being drawn
        # and the original it replaced - two belts a phase apart, casting two
        # shadows. Excluding by name is how the belt layers already say which
        # of the two is real.
        casters = by_hints(cfg.get("casters")) - keep - blockers - excluded
        for ob in scene.objects:
            if ob in keep:
                continue
            if ob in blockers and ob.type in RENDERABLE:
                saved["holdout"][ob] = ob.is_holdout
                saved["hidden"][ob] = ob.hide_render
                ob.is_holdout = True
                ob.hide_render = False
                continue
            if ob in casters and ob.type in RENDERABLE:
                saved["camera_visible"][ob] = ob.visible_camera
                saved["hidden"][ob] = ob.hide_render
                ob.visible_camera = False
                ob.hide_render = False
                continue
            if cfg["own_lighting"] or ob.type != "LIGHT":
                saved["hidden"][ob] = ob.hide_render
                ob.hide_render = True

        # ---- camera ------------------------------------------------------
        cam_data = bpy.data.cameras.new("_atlas_cam")
        cam_data.type = "ORTHO" if cfg["orthographic"] else "PERSP"
        cam_data.lens = cfg["focal_length"]
        cam = bpy.data.objects.new("_atlas_cam", cam_data)
        scene.collection.objects.link(cam)
        temp.append(cam)

        reach = max((p - pivot).length
                    for p in spun_corners + static_corners) or 1.0
        distance = reach * 6.0 + 1.0
        cam.matrix_world = camera_matrix(pivot, cfg["azimuth"], cfg["elevation"],
                                         distance)
        cam_data.clip_start = max(distance * 0.01, 1e-4)
        cam_data.clip_end = distance * 4.0
        scene.camera = cam

        # ---- render settings --------------------------------------------
        r.resolution_x = r.resolution_y = cfg["tile"]
        r.resolution_percentage = 100
        r.pixel_aspect_x = r.pixel_aspect_y = 1.0
        r.film_transparent = True
        r.image_settings.file_format = "PNG"
        r.image_settings.color_mode = "RGBA"
        r.image_settings.color_depth = "8"
        # A layer that writes numbers instead of a picture says so, and this is
        # the only way it can: the view transform is the last thing between a
        # shaded value and the byte in the file, so a height ramp rendered under
        # the scene's filmic look comes out as a photograph of a height ramp.
        # Saved and restored like the format above, per layer, because the same
        # job renders both kinds - the tank wants its look and this one wants
        # none of it.
        if cfg.get("view_transform"):
            scene.view_settings.view_transform = cfg["view_transform"]
        scene.use_nodes = False          # skip any compositor setup
        if hasattr(scene, "eevee"):
            saved["eevee_samples"] = getattr(scene.eevee, "taa_render_samples", None)
            if saved["eevee_samples"] is not None:
                scene.eevee.taa_render_samples = cfg["samples"]
        if hasattr(scene, "cycles"):
            saved["cycles_samples"] = scene.cycles.samples
            scene.cycles.samples = cfg["samples"]

        # ---- lighting ----------------------------------------------------
        if cfg["own_lighting"]:
            temp.extend(build_lighting(cfg, cam))
            if cfg["world_strength"] is not None and scene.world \
                    and scene.world.use_nodes:
                bg = scene.world.node_tree.nodes.get("Background")
                if bg:
                    saved["world_strength"] = bg.inputs["Strength"].default_value
                    bg.inputs["Strength"].default_value = cfg["world_strength"]

        fit_angles = cfg.get("fit_angles") or angles
        ortho_scale, shift_y = fit_camera(cfg, cam, pivot, spun_corners,
                                          static_corners, fit_angles, distance)

        # widen the frame without changing what a pixel is worth. The factor is
        # recomputed from the rounded tile, so units_per_pixel comes out bit
        # identical to the unscaled layers rather than nearly so
        tile = cfg["tile"]
        if float(cfg.get("tile_scale") or 1.0) != 1.0:
            tile = max(1, int(round(tile * float(cfg["tile_scale"]))))
            ortho_scale *= tile / float(cfg["tile"])
            cam.data.ortho_scale = ortho_scale
            r.resolution_x = r.resolution_y = tile

        # ---- turntable ---------------------------------------------------
        # Blockers turn with the turntable, or the flash would be occluded by a
        # tank standing at angle zero while the flash swings round it. Only the
        # top-most ones are driven; anything parented under another blocker
        # follows it, and setting both would be two sources of one transform.
        # Casters turn for the same reason and by the same rule: the shadow on
        # the tile is the tank's, so it has to be the tank at *this* heading.
        turned = blockers | casters
        blocker_bases = {ob: ob.matrix_world.copy()
                         for ob in turned if ob.parent not in turned}
        saved["blocker_bases"] = blocker_bases

        paths = []
        hook = cfg.get("phase_hook")
        for phase in range(phases):
            # put the root back before the hook runs: the previous phase left
            # it spun to the last angle, and a hook that reads or edits the
            # transform must see the pose it was authored against
            root.matrix_world = saved["root_matrix"]
            if hook is not None:
                hook(phase, phases)
                bpy.context.view_layer.update()
            base = root.matrix_world.copy()

            for j, angle in enumerate(angles):
                if cfg["spin"] == "object":
                    spin = spin_matrix(pivot, angle)
                    root.matrix_world = spin @ base
                    for ob, rest in blocker_bases.items():
                        ob.matrix_world = spin @ rest
                else:
                    cam.matrix_world = camera_matrix(pivot, cfg["azimuth"] + angle,
                                                     cfg["elevation"], distance)
                bpy.context.view_layer.update()

                i = phase * len(angles) + j
                path = os.path.join(frames_dir, "%s_%03d.png" % (cfg["name"], i))
                r.filepath = path
                # `.get`, because `render_atlas` is also called with a job dict
                # built by hand - see hit_point - and a robustness fix that
                # raised KeyError on another caller would be no fix at all
                _render_still(path, cfg.get("save_retries", CONFIG["save_retries"]),
                              cfg.get("save_wait", CONFIG["save_wait"]))
                paths.append(path)
                print("[atlas] %s %d/%d  %6.1f deg  phase %d/%d"
                      % (cfg["name"], i + 1, phases * len(angles), angle,
                         phase + 1, phases))

        atlas_path = os.path.join(cfg["output_dir"], "%s_atlas.png" % cfg["name"])
        columns, rows, placements = write_atlas(paths, tile, columns, atlas_path)

        # the pivot projects to the same pixel in every frame and every pass
        anchor = [tile / 2.0, tile / 2.0 + shift_y * tile]
        meta = {
            "atlas": os.path.basename(atlas_path),
            "tile": [tile, tile],
            "grid": {"columns": columns, "rows": rows},
            # angles per phase, not tiles in the file: this is the number a
            # game divides 360 by to pick a facing, and it must not change
            # when a layer gains phases
            "count": len(angles),
            "phases": phases,
            "rendered": sorted(o.name for o in rendered if o.type in RENDERABLE),
            "excluded": sorted(o.name for o in excluded),
            "view": {
                "projection": "ortho" if cfg["orthographic"] else "persp",
                "elevation": cfg["elevation"],
                "azimuth": cfg["azimuth"],
                "spin": cfg["spin"],
                "direction": cfg["direction"],
            },
            "spin_pivot": [round(pivot.x, 6), round(pivot.y, 6), round(pivot.z, 6)],
            # world units covered by one tile, i.e. the sprite's scale
            "ortho_scale": ortho_scale,
            "units_per_pixel": ortho_scale / tile,
            # where spin_pivot sits inside a tile, in pixels from its top-left
            # corner - draw parts on top of each other at this point
            "anchor_px": anchor,
            "frames": [
                {"index": p * len(angles) + j, "angle": a % 360.0, "phase": p,
                 "col": (p * len(angles) + j) % columns,
                 "row": (p * len(angles) + j) // columns}
                for p in range(phases) for j, a in enumerate(angles)
            ],
        }
        # Where each frame's pixels ended up, and where its box sat inside the
        # tile it was rendered in. The tile stays the coordinate system - the
        # anchor, the muzzle and the plate table are all still in tile pixels -
        # so these two say only where to fetch from and where to put the quad.
        # A reader that finds neither falls back to col/row, which is what keeps
        # the sets in Sprites/Obsolete loading.
        for frame, place in zip(meta["frames"], placements):
            frame["rect"] = place["rect"] if place else None
            if place:
                frame["off"] = place["off"]
        meta["packed"] = True

        labels = cfg.get("frame_labels")
        if labels:
            # labels describe a facing, so every phase of an angle gets the
            # same one - zipping them against the frame list would label the
            # first phase and leave the rest blank
            for frame in meta["frames"]:
                frame["label"] = labels[frame["index"] % len(angles)]
        if cfg.get("meta_extra"):
            meta.update(cfg["meta_extra"])
        meta_path = os.path.join(cfg["output_dir"], "%s_atlas.json" % cfg["name"])
        with open(meta_path, "w", encoding="utf-8") as fh:
            json.dump(meta, fh, indent=2)

        if not cfg["keep_frames"]:
            for path in paths:
                os.remove(path)
            if os.path.isdir(frames_dir) and not os.listdir(frames_dir):
                os.rmdir(frames_dir)

        print("[atlas] %s  (%dx%d tiles of %dpx)"
              % (atlas_path, columns, rows, cfg["tile"]))
        return atlas_path, meta

    finally:
        # ---- put the scene back ------------------------------------------
        root.matrix_world = saved["root_matrix"]
        for ob, rest in saved.get("blocker_bases", {}).items():
            ob.matrix_world = rest
        for ob, was in saved["holdout"].items():
            ob.is_holdout = was
        for ob, was in saved["camera_visible"].items():
            ob.visible_camera = was
        for ob, hidden in saved["hidden"].items():
            ob.hide_render = hidden
        for ob in temp:
            data = ob.data
            bpy.data.objects.remove(ob)
            if isinstance(data, bpy.types.Camera):
                bpy.data.cameras.remove(data)
            elif isinstance(data, bpy.types.Light):
                bpy.data.lights.remove(data)
        if saved["world_strength"] is not None:
            scene.world.node_tree.nodes["Background"].inputs["Strength"] \
                .default_value = saved["world_strength"]
        scene.camera = saved["camera"]
        r.resolution_x, r.resolution_y, r.resolution_percentage = saved["res"]
        r.pixel_aspect_x, r.pixel_aspect_y = saved["aspect"]
        r.film_transparent = saved["transparent"]
        r.filepath = saved["filepath"]
        r.image_settings.file_format = saved["format"]
        r.image_settings.color_mode = saved["color_mode"]
        r.image_settings.color_depth = saved["depth"]
        scene.view_settings.view_transform = saved["view_transform"]
        scene.use_nodes = saved["use_nodes"]
        if saved.get("eevee_samples") is not None:
            scene.eevee.taa_render_samples = saved["eevee_samples"]
        if saved.get("cycles_samples") is not None:
            scene.cycles.samples = saved["cycles_samples"]
        bpy.context.view_layer.update()


# ---------------------------------------------------------------------------
# multi-layer jobs
# ---------------------------------------------------------------------------

def hex_facing_labels(angles, front_dir=270.0, orientation="flat"):
    """Label each turntable angle by the hex feature the model then faces.

    A flat-top hex has its edges at 30+60k deg and its vertices at 60k;
    a pointy-top hex is the other way round.
    """
    if orientation == "flat":
        edges = {(30 + 60 * i) % 360 for i in range(6)}
    else:
        edges = {(60 * i) % 360 for i in range(6)}
    labels = []
    for angle in angles:
        facing = round((front_dir + angle) % 360)
        labels.append("%s @ %d deg"
                      % ("side" if facing % 360 in edges else "corner", facing))
    return labels


def footprint_centre(objects):
    """Centre of the XY bounding box of some objects, in world space."""
    pts = corners_of(objects)
    lo, hi = bounds_of(pts)
    return ((lo.x + hi.x) / 2.0, (lo.y + hi.y) / 2.0)


def render_set(job):
    """Render several layers that share one camera, scale and anchor.

    job = {
      "shared": { ... any CONFIG key ... ,
                  # optional: take the spin axis from these objects instead of
                  # the centre of everything - for a tank, the turret ring
                  "spin_pivot_from": ["Turret"],
                  # optional: auto-label frames by hex facing
                  "hex_labels": {"front_dir": 270.0, "orientation": "flat"} },
      "layers": [ {"name": "hull",   "target": "world", "exclude": ["Turret"]},
                  {"name": "turret", "target": "Turret"},
                  {"name": "hex",    "target": "Hex", "static": True},
                  # an effect: rendered, but kept out of the fit, and with a
                  # time dimension of its own
                  {"name": "flash",  "target": "Flash", "fit": False,
                   "phases": 8, "phase_hook": some_callable} ],
    }

    Per-layer keys beyond the CONFIG ones:

        static  one frame, but fitted over the whole carousel's angles
        fit     False to render the layer without letting it size the frame

    Getting layers to composite means keeping four things identical across
    passes: the spin axis, the fitted geometry, the angle list used for fitting,
    and the camera. Doing that by hand is how sprites end up subtly misaligned,
    so it is resolved here once and pushed into every layer.
    """
    shared = dict(CONFIG)
    shared.update(job.get("shared", {}))
    layers = job["layers"]
    if not layers:
        raise RuntimeError("no layers to render")

    # ---- resolve the geometry each layer contributes ---------------------
    spun, static = set(), set()
    for layer in layers:
        root = bpy.data.objects[layer["target"]]
        excluded = by_hints(layer.get("exclude"))
        objs = [o for o in hierarchy(root)
                if o not in excluded and o.type in RENDERABLE]
        if not objs:
            raise RuntimeError("layer %r renders nothing" % layer.get("name"))
        if layer.get("fit") is False:
            # rendered, but never sizes the frame. An effect is the case that
            # needs this: a muzzle flash is transient and bigger than the gun
            # it comes out of, so letting it into the fit would zoom the camera
            # out and shrink the tank in every other layer - for something that
            # is on screen a tenth of a second. It is also the layer most
            # likely to be retuned, and the fit must not move when it is.
            continue
        (static if layer.get("static") else spun).update(objs)
    if not spun:
        raise RuntimeError("every layer is static - nothing would turn")

    angles = frame_angles(shared)

    # ---- one fitted geometry for the whole job --------------------------
    # Measured here and now, before any layer has rendered and therefore before
    # any phase hook has run. See the note in render_atlas.
    fit_corners = corners_of(spun)
    fit_static_corners = corners_of(static)

    # ---- one spin axis for the whole job --------------------------------
    #
    # More than one object carries the stamp, and that is not a curiosity: a
    # piece separated by hand inherits the parent's custom properties, so
    # `Barrel` carries the turret's ring and `Engine` carries the hull's. They
    # agree, because they are copies - right up until `turret_axis.py` is re-run,
    # which stamps `Turret` and `Hull` and leaves every split-off child holding
    # the old number.
    #
    # This used to be `next(o for o in (spun | static) if ...)` over a *set*,
    # so which one won was hash order. That is the worst possible failure: a
    # stale axis does not distort the sprite, it walks the whole tank round a
    # circle as it turns, and nothing in any number says so.
    #
    # Prefer a holder that is not underneath another holder - a child's copy is
    # derived from its parent's, never the other way round - then by name, so
    # the choice is the same on every run. And say so out loud when they
    # disagree, because at that point there is no way to tell from here which
    # one is current.
    stamped = None
    stamped_spread = 0.0
    key = shared.get("spin_pivot_prop", "ring_axis")
    if key:
        holders = sorted((o for o in (spun | static) if key in o.keys()),
                         key=lambda o: o.name)
        if holders:
            held = set(holders)

            def derived(ob):
                up = ob.parent
                while up is not None:
                    if up in held:
                        return True
                    up = up.parent
                return False

            roots = [o for o in holders if not derived(o)] or holders
            stamped = roots[0]
            values = [tuple(float(v) for v in o[key]) for o in holders]
            # a length mismatch is a disagreement too, not a crash
            stamped_spread = max(
                math.dist(values[0], v) if len(v) == len(values[0])
                else float("inf") for v in values)

    if shared.get("spin_pivot"):
        pivot_xy = tuple(float(v) for v in shared["spin_pivot"])
        pivot_from = "explicit"
    elif stamped is not None:
        # split_turret.py stamps the ring it measured from the free gap; that is
        # the axis the turret actually turns on, so prefer it over any estimate
        pivot_xy = tuple(float(v) for v in stamped[key])
        pivot_from = "%s stamped on %s" % (key, stamped.name)
    elif shared.get("spin_pivot_from"):
        pivot_xy = footprint_centre(by_hints(shared["spin_pivot_from"]))
        pivot_from = "footprint of %s" % ", ".join(shared["spin_pivot_from"])
    else:
        pivot_xy = footprint_centre(spun | static)
        pivot_from = "centre of all layers"

    warnings = []
    if stamped is not None and stamped_spread > 1e-5 and not shared.get("spin_pivot"):
        warnings.append(
            "%s disagrees by %.5f between the objects carrying it, so at least "
            "one is a stale copy left behind by a hand split - %s was used. "
            "Re-run turret_axis.py or clear the property off the split pieces; "
            "a wrong axis walks the whole tank round a circle as it turns and "
            "shows up in no other number"
            % (key, stamped_spread, stamped.name))
    if shared.get("hex_labels") and round(shared["azimuth"]) % 30 != 0:
        warnings.append(
            "azimuth %g is not a multiple of 30, so a hex tile will project "
            "skewed" % shared["azimuth"])

    # facing labels come from the import convention; fill it in so callers do
    # not have to restate it, then cross-check it against the gun's direction
    labels_cfg = shared.get("hex_labels")
    front_check = None
    if labels_cfg:
        labels_cfg = {} if labels_cfg is True else dict(labels_cfg)
        labels_cfg.setdefault("front_dir", shared.get("front_dir", 270.0))
        labels_cfg.setdefault("orientation", "flat")
        front = labels_cfg["front_dir"]
        guess = next((o["front_dir_guess"] for o in (spun | static)
                      if "front_dir_guess" in o.keys()), None)
        if guess is not None:
            off = abs((float(guess) - front + 180.0) % 360.0 - 180.0)
            front_check = {"assumed": front, "barrel_guess": round(float(guess), 1),
                           "off_by": round(off, 1)}
            if off > 60.0:
                warnings.append(
                    "the gun points %.0f deg but the convention says the hull "
                    "faces %.0f deg - the model may have been imported back to "
                    "front, which would flip every side/corner label"
                    % (float(guess), front))

    # ---- render ----------------------------------------------------------
    out = {"layers": {}, "spin_pivot": [round(v, 6) for v in pivot_xy],
           "spin_pivot_from": pivot_from, "angles": angles,
           "front_dir_check": front_check, "warnings": warnings}
    for layer in layers:
        cfg = dict(shared)
        cfg.update({k: v for k, v in layer.items()
                    if k not in ("static", "fit")})
        cfg["spin_pivot"] = list(pivot_xy)
        cfg["fit_objects"] = sorted(spun, key=lambda o: o.name)
        cfg["fit_static_objects"] = sorted(static, key=lambda o: o.name)
        cfg["fit_corners"] = fit_corners
        cfg["fit_static_corners"] = fit_static_corners
        cfg["fit_angles"] = angles
        if layer.get("static"):
            cfg["steps"] = 1
            cfg["columns"] = 1
            cfg["frame_labels"] = ["static tile"]
        elif labels_cfg:
            cfg["frame_labels"] = hex_facing_labels(angles, **labels_cfg)
        extra = dict(cfg.get("meta_extra") or {})
        extra.update({"layer": layer["name"], "static": bool(layer.get("static"))})
        cfg["meta_extra"] = extra

        path, meta = render_atlas(cfg)
        out["layers"][layer["name"]] = {
            "path": path,
            "grid": meta["grid"], "count": meta["count"],
            "tile": meta["tile"],
            "ortho_scale": meta["ortho_scale"],
            "units_per_pixel": meta["units_per_pixel"],
            "anchor_px": meta["anchor_px"],
            "rendered": meta["rendered"],
        }

    # ---- the whole point: prove the layers line up ----------------------
    # Compared in world terms, not in pixels: one distance per pixel, and the
    # anchor at the same fraction of the frame. A layer rendered at a bigger
    # tile satisfies both and composites correctly; comparing ortho_scale and
    # anchor_px would call it a mismatch, which is why the check was rewritten
    # rather than relaxed.
    scales = {round(v["units_per_pixel"], 12) for v in out["layers"].values()}
    anchors = {tuple(round(c / v["tile"][i], 9)
                     for i, c in enumerate(v["anchor_px"]))
               for v in out["layers"].values()}
    out["framing_identical"] = len(scales) == 1 and len(anchors) == 1
    if not out["framing_identical"]:
        warnings.append("layers do NOT share a framing: units_per_pixel=%s "
                        "relative anchors=%s" % (scales, anchors))
    print("[atlas] set done:", out["framing_identical"], warnings)
    return out


# A tank assembled from separate parts: the hull and the turret turn on the
# same axis so the sprites can simply be drawn on top of each other, and the
# ground tile is framed with them but never spun.
#
# An example, not a fixture. The scenes in Scenes/ are hand-split and their
# object names differ - this listed a `world.001` turret root that none of the
# three current scenes has, and it cost a wrong instruction before anyone read
# the scene. Call get_objects_summary and build the layer list from what is
# actually there; the /sprites command says so first thing for this reason.
#
# Note the gun: with the tip separated into `Barrel` and parented to `Turret`,
# the turret layer picks it up through the hierarchy and the hull layer drops
# it through the `Turret` hint, which excludes descendants. Parented anywhere
# else it lands in the hull atlas and turns with the hull.
TANK_JOB = {
    "shared": {
        "output_dir": r"D:\Projects\AgentCoding\BlenderMCP\out",
        "steps": 12, "tile": 256,
        "azimuth": 0.0, "elevation": 30.0,     # multiple of 30 for hex tiles
        "spin_pivot_from": ["Hex"],
        "hex_labels": {"front_dir": 270.0, "orientation": "flat"},
    },
    "layers": [
        {"name": "hull", "target": "world", "exclude": ["Turret"]},
        {"name": "turret", "target": "Turret"},
        {"name": "hex", "target": "Hex", "static": True},
    ],
}


if __name__ == "__main__":
    render_atlas(CONFIG)
