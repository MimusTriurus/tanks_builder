"""
The whole tank, end to end.

Measures the muzzle and the exhaust ports, builds the ground tile, the flash,
its smoke, the engine plume and the burning-engine flame, renders every layer as
one job, and composites check images with the numbers that decide whether it
worked.

    import tank_pipeline
    tank_pipeline.run()                       # everything, into out/
    tank_pipeline.run({"output_dir": r"...\\Sprites\\MT"})

Two steps here are not automatic and cannot sensibly be, and both are the same
step: separating a piece of the model by hand so the measurement has something
unambiguous to measure.

    Barrel  -> the gun tip, parented to Turret.  Gives the muzzle flash.
    Engine  -> the outlet, parented to Hull.     Gives the exhaust plume,
                                                 and the fire when it burns.

`Engine` pays for two layers off one measurement: `exhaust_point` stamps the
ports on the hull, and both `exhaust_plume` and `engine_fire` read that stamp.
The outlet is where the engine is and the engine is what catches fire, so a
second measurement of the same hole would only be a second thing to keep in
step.

Neither is required. Whichever is missing is skipped with a note, so a scene can
be brought along one piece at a time - which is what actually happens, since the
split is a minute of hand work per tank. See MUZZLE_FLASH.md for the gun.

Why one entry point rather than several
---------------------------------------
The same reason `render_set` exists, and it is the reason the exhaust lives in
this file rather than in one of its own. Layers composite only if they share a
spin axis, a fitted geometry, a fit angle list and a camera; a second render job
resolves all four again and there is nothing to stop it resolving them
differently. So there is one job, and everything that must line up is decided
once. `check` then measures the result rather than trusting it, because every
number below was wrong at least once.
"""

import importlib.util
import json
import math
import os

import bpy
import numpy as np

REPO = r"D:\Projects\AgentCoding\BlenderMCP"

CONFIG = {
    "repo": REPO,
    "output_dir": os.path.join(REPO, "out"),

    # scene objects. Read the scene before trusting these: the three scenes in
    # Scenes/ are hand-split and have differed before.
    "root": "world",
    "hull": "Hull",
    "turret": "Turret",
    "barrel": "Barrel",
    "exhaust": "Engine",        # a name *prefix*: two outlets are two objects
    "flash": "Flash",
    "smoke": "Smoke",
    "plume": "Plume",
    "fire": "Fire",
    "burn": "Burn",
    "hex": "Hex",

    # --- the render ---------------------------------------------------------
    "steps": 12,
    "tile": 256,
    # the flash layers get a wider frame at the same units_per_pixel: the
    # muzzle already sits 68.8px out from the spin axis, so a 256 tile leaves
    # the flash 59px and a flash worth looking at is longer than that.
    # The plume needs none of that - it stands over the deck, near the anchor.
    "effect_tile_scale": 1.5,
    "plume_tile_scale": 1.0,
    # The flame needs none of that room either, which is worth stating because
    # it is the effect that looks like it should. It stands *above* the tank
    # rather than beside it, so the budget it spends is the half-tile over the
    # anchor rather than the one out to the muzzle - and that half-tile is the
    # smaller of the two, 0.4645 of the frame against 0.5354 below. Measured on
    # MT: the port sits 28px above the anchor, the flame is 50px of screen and
    # its embers 66, so the tallest thing in the layer lands ~94px up against
    # the 119px a plain tile leaves; sideways, the worst heading puts the port
    # 67px out and the embers ~103px, against 128.
    # Raise this if `rise` goes up - `fire_edge_alpha` will say so first.
    "fire_tile_scale": 1.0,
    # The smoke column is the one layer that really does need a bigger frame,
    # and by a lot.
    #
    # The room a tile leaves above the anchor is `0.4645 * tile`, not
    # `tile/2 - 9`: the anchor is a fixed *fraction* of the frame, so the
    # shortfall grows with the tile. Getting that wrong is what put the first
    # attempt at 2.25 and clipped 28px off the top of the column.
    #
    # Measured, by projecting the mesh through the same camera basis at all
    # twelve angles rather than estimating: the column reaches 289px above the
    # anchor and 190px to the side. 2.5 leaves 297, and rendering it put 0.008
    # of alpha on the border - under the 0.02 the check trips at, so it passed,
    # and still the top of the column touching its frame. Eight pixels of
    # measured margin turned out to be none. 2.75 leaves 327 and renders clean.
    #
    # This is what a tall effect costs: 704px tiles, 144 of them, so an 8448px
    # atlas - about 285 MB once a game has decoded it. That is the price of a
    # hull and a half, and the lever on it is `rise` in engine_fire.SMOKE.
    "burn_tile_scale": 2.75,
    "phases": 8,
    # More phases than the flash, for a reason that is about looping rather
    # than about length. A flash is watched once for a tenth of a second; the
    # plume runs for the whole game, and a conveyor stepping four pixels at a
    # time is visible in a way the same step inside an explosion is not.
    "exhaust_phases": 12,
    # The flame's lap, for the plume's reason - it burns for as long as it is on
    # screen, so the loop is what gets looked at rather than the shape of any
    # one phase. It is played faster than the plume in the game; that is a rate,
    # not a frame count, and it does not belong here.
    #
    # The smoke column takes the same count, because it is the same event: one
    # tank, on fire, one clock. Two counts would only be worth having if the
    # game wanted to run them at different rates, and it can do that with one
    # count anyway - the layers carry separate frame indices.
    "fire_phases": 12,
    "azimuth": 0.0,
    "elevation": 30.0,
    "front_dir": 270.0,

    # --- the check ----------------------------------------------------------
    # frame indices to composite: at camera, across right, away, across left
    "check_headings": [0, 3, 6, 9],
    "check_phases": [0, 1, 2, 4],
    # how far the flash may sit off the tank's symmetry line, in pixels, on the
    # two headings where that line is vertical on screen. This is the assertion
    # that would have caught the muzzle sitting on the rim of the bore instead
    # of on its axis - see MUZZLE_FLASH.md.
    "centre_tolerance_px": 2.0,
    # How much bigger than the worst ordinary one the loop's closing step may
    # be. The plume's phases are a conveyor, so every step should be about the
    # same size; a seam shows up as one outlier and nothing else does.
    "loop_tolerance": 1.35,
    # Least the plume may change the picture by, in levels of 255, measured
    # against the tile's own colour. Below this it is present in the alpha and
    # absent from the screen.
    "exhaust_contrast_min": 28.0,
    # Least the flame may stay red by, in levels of 255: how much more red than
    # green-and-blue the picture becomes once the layer is added over the ground.
    # Brightness is not the thing that fails here - a fire that clips to white
    # is brighter than one that does not, and worse. See engine_fire's docstring
    # on the ramp.
    "fire_redness_min": 30.0,
}

# Also the order they are composited in, which for the burning pair is not a
# preference: the smoke goes down with normal alpha and the fire is added on
# top of it. See engine_fire.SMOKE.
LAYER_ORDER = ("hex", "hull", "turret", "exhaust", "burn", "fire",
               "smoke", "flash")


def _load(cfg, name):
    spec = importlib.util.spec_from_file_location(
        name, os.path.join(cfg["repo"], "%s.py" % name))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def _find(scene, name):
    """An object by name, tolerating the leading space P > Selection leaves."""
    ob = scene.objects.get(name)
    if ob is not None:
        return ob
    return next((o for o in scene.objects if o.name.strip() == name.strip()), None)


def parts(cfg=None):
    """Which hand-split pieces this scene has, and so which layers it gets."""
    cfg = dict(CONFIG, **(cfg or {}))
    scene = bpy.context.scene
    exhaust = _load(cfg, "exhaust_point").ports_of(scene, {"prefix": cfg["exhaust"]})
    return {"flash": _find(scene, cfg["barrel"]) is not None,
            "exhaust": bool(exhaust),
            "exhaust_objects": [o.name for o in exhaust]}


# ---------------------------------------------------------------------------
# stages
# ---------------------------------------------------------------------------

def prepare(cfg=None):
    """Measure what is there, build the tile and the effects. Idempotent."""
    cfg = dict(CONFIG, **(cfg or {}))
    scene = bpy.context.scene
    have = parts(cfg)

    if not have["flash"] and not have["exhaust"]:
        raise RuntimeError(
            "this scene has neither %r nor an object starting with %r, so "
            "there is no effect to render. Separate one by hand first: select "
            "it, Ctrl+L to complete the panels it cuts through, P > Selection, "
            "rename it, then Ctrl+P > Object (Keep Transform) onto %s for the "
            "gun or %s for the exhaust. See MUZZLE_FLASH.md."
            % (cfg["barrel"], cfg["exhaust"], cfg["turret"], cfg["hull"]))

    # the transient objects are rebuilt every run, so a re-run never leaves
    # Hex.001 behind to be rendered instead of Hex
    for name in (cfg["hex"], cfg["flash"], cfg["smoke"], cfg["plume"],
                 cfg["fire"], cfg["burn"]):
        ob = scene.objects.get(name)
        if ob is not None:
            data = ob.data
            bpy.data.objects.remove(ob)
            bpy.data.meshes.remove(data)

    out = {"have": have, "muzzle": None, "exhaust": None}

    if have["flash"]:
        out["muzzle"] = _load(cfg, "muzzle_point").set_muzzle(
            {"turret": cfg["turret"], "barrel": cfg["barrel"],
             "stamp_on": [cfg["turret"], cfg["barrel"]]})
    if have["exhaust"]:
        out["exhaust"] = _load(cfg, "exhaust_point").set_exhaust(
            {"prefix": cfg["exhaust"], "hull": cfg["hull"],
             "turret": cfg["turret"], "stamp_on": [cfg["hull"]]})

    hex_mod = _load(cfg, "hex_base")
    out["hex"] = hex_mod.make_hex(dict(
        hex_mod.CONFIG,
        target=cfg["root"], name=cfg["hex"],
        # the tank in these scenes is already placed and must not be moved;
        # ground_z None then stands the tile under its tracks
        move_target=False, center_on_origin=False, ground_z=None))

    if have["flash"]:
        flash_mod = _load(cfg, "muzzle_flash")
        flash_mod.build({"turret": cfg["turret"], "name": cfg["flash"]})
        flash_mod.build_smoke({"turret": cfg["turret"], "name": cfg["smoke"]})
    if have["exhaust"]:
        _load(cfg, "exhaust_plume").build({"hull": cfg["hull"],
                                           "name": cfg["plume"]})
        fire_mod = _load(cfg, "engine_fire")
        fire_mod.build({"hull": cfg["hull"], "name": cfg["fire"]})
        fire_mod.build_smoke({"hull": cfg["hull"], "name": cfg["burn"]})
    return out


def render(cfg=None):
    """Render every layer this scene has as one framed set."""
    cfg = dict(CONFIG, **(cfg or {}))
    atlas = _load(cfg, "sprite_atlas")
    have = parts(cfg)

    layers = [
        # the turret hint excludes descendants, which is what keeps the barrel
        # out of the hull layer. The plume and the flame sit at scene root and
        # so are inside `world` too - they are named here for the same reason.
        {"name": "hull", "target": cfg["root"],
         "exclude": [cfg["turret"], cfg["plume"], cfg["fire"], cfg["burn"]]},
        {"name": "turret", "target": cfg["turret"]},
        {"name": "hex", "target": cfg["hex"], "static": True},
    ]

    if have["flash"]:
        flash_mod = _load(cfg, "muzzle_flash")
        layers.extend([
            {"name": "flash", "target": cfg["flash"],
             # never in the fit, or a transient effect resizes every layer
             "fit": False,
             "tile_scale": cfg["effect_tile_scale"],
             "phases": cfg["phases"],
             "phase_hook": flash_mod.phase_hook({"turret": cfg["turret"],
                                                 "name": cfg["flash"]}),
             # the tank blocks without being drawn: fired away from the camera
             # the flash is behind the turret, and a flat layer has no depth
             "holdout": [cfg["hull"], cfg["turret"]]},
            # smoke is its own layer because it occludes where the flash emits.
            # One layer cannot be both: fire adds light to what is behind it,
            # smoke takes it away, and the game blends them differently.
            {"name": "smoke", "target": cfg["smoke"], "fit": False,
             "tile_scale": cfg["effect_tile_scale"],
             "phases": cfg["phases"],
             "phase_hook": flash_mod.smoke_phase_hook(
                 {"turret": cfg["turret"], "name": cfg["smoke"]}),
             "holdout": [cfg["hull"], cfg["turret"]]},
        ])

    if have["exhaust"]:
        plume_mod = _load(cfg, "exhaust_plume")
        layers.append(
            # Holdout on both, and the turret is the interesting half. Baking a
            # rotating part into a static occlusion sounds wrong and is nearly
            # right here: what gets cut is the turret's *body*, whose silhouette
            # from a given heading barely changes as it turns. The case it
            # cannot express is the gun swung back over the engine deck, where
            # the plume should pass behind the barrel and will not.
            {"name": "exhaust", "target": cfg["plume"], "fit": False,
             "tile_scale": cfg["plume_tile_scale"],
             "phases": cfg["exhaust_phases"],
             "phase_hook": plume_mod.phase_hook({"hull": cfg["hull"],
                                                 "name": cfg["plume"]}),
             "holdout": [cfg["hull"], cfg["turret"]]})

        fire_mod = _load(cfg, "engine_fire")
        layers.append(
            # Same ports as the plume, same holdout argument, same plain tile.
            # What it needs room for is height rather than reach, and the tile
            # already has more of that than the muzzle left over - see
            # fire_tile_scale.
            {"name": "fire", "target": cfg["fire"], "fit": False,
             "tile_scale": cfg["fire_tile_scale"],
             "phases": cfg["fire_phases"],
             "phase_hook": fire_mod.phase_hook({"hull": cfg["hull"],
                                                "name": cfg["fire"]}),
             "holdout": [cfg["hull"], cfg["turret"]]})
        layers.append(
            # The other half of the fire. Its own frame, because a hull and a
            # half of column does not fit in the tank's tile - see
            # burn_tile_scale. Same holdout, same phase count, drawn *under* the
            # flame.
            {"name": "burn", "target": cfg["burn"], "fit": False,
             "tile_scale": cfg["burn_tile_scale"],
             "phases": cfg["fire_phases"],
             "phase_hook": fire_mod.smoke_phase_hook({"hull": cfg["hull"],
                                                      "name": cfg["burn"]}),
             "holdout": [cfg["hull"], cfg["turret"]]})

    return atlas.render_set({
        "shared": {
            "output_dir": cfg["output_dir"],
            "steps": cfg["steps"], "tile": cfg["tile"],
            "azimuth": cfg["azimuth"], "elevation": cfg["elevation"],
            "hex_labels": {"front_dir": cfg["front_dir"], "orientation": "flat"},
        },
        "layers": layers,
    })


# ---------------------------------------------------------------------------
# check
# ---------------------------------------------------------------------------

def _atlases(cfg):
    atlas = _load(cfg, "sprite_atlas")
    out = {}
    for name in LAYER_ORDER:
        base = os.path.join(cfg["output_dir"], "%s_atlas" % name)
        if not os.path.exists(base + ".json"):
            continue
        with open(base + ".json", encoding="utf-8") as fh:
            meta = json.load(fh)
        out[name] = (atlas.read_rgba(base + ".png"), meta)
    return out


def _tile_of(layers, name, index):
    pix, meta = layers[name]
    size = meta["tile"][0]
    cols, rows = meta["grid"]["columns"], meta["grid"]["rows"]
    col, row = index % cols, index // cols
    # image data is bottom-up; grid row 0 is the top one
    y0 = (rows - row - 1) * size
    return pix[y0:y0 + size, col * size:(col + 1) * size]


def _over(dst, src):
    a = src[:, :, 3:4]
    out = np.empty_like(dst)
    out[:, :, :3] = src[:, :, :3] * a + dst[:, :, :3] * (1.0 - a)
    out[:, :, 3:4] = a + dst[:, :, 3:4] * (1.0 - a)
    return out


def _add(dst, src):
    out = dst.copy()
    out[:, :, :3] = np.clip(dst[:, :, :3] + src[:, :, :3] * src[:, :, 3:4],
                            0.0, 1.0)
    return out


def _centroid_x(tile_rgba, floor=0.05):
    a = tile_rgba[:, :, 3]
    mask = a > floor
    if not mask.any():
        return None
    xs = np.nonzero(mask)[1]
    weights = a[mask]
    return float((xs * weights).sum() / weights.sum())


def _edge_alpha(layers, name):
    _, meta = layers[name]
    worst = 0.0
    for frame in meta["frames"]:
        t = _tile_of(layers, name, frame["index"])[:, :, 3]
        worst = max(worst, float(max(t[0].max(), t[-1].max(),
                                     t[:, 0].max(), t[:, -1].max())))
    return worst


def _ground_colour(layers):
    """The tile's own colour - what the game's field looks like."""
    tile = _tile_of(layers, "hex", 0)
    solid = tile[:, :, 3] > 0.9
    if not solid.any():
        return np.array([0.16, 0.16, 0.16], dtype=np.float32)
    return np.median(tile[solid][:, :3], axis=0).astype(np.float32)


def _sheet(cfg, layers, name, rows, cols, draw, background, used):
    """A grid of composites, saved as a PNG.

    Sized to the widest of the layers it actually draws - `used` - and every
    layer placed by its *anchor* rather than by its frame corner. That is the
    composite the game performs, and it is the only one that stays right when
    the tiles differ, which they do: the flash is rendered wider than the tank
    and the smoke column wider again.

    `used` is not a micro-optimisation. Sizing to the widest layer in the whole
    set meant one 576px effect grew every sheet to 576px cells, so the shot
    sheet - which does not draw it - became four times the area it needs and
    unreadable at a glance.

    The background is an argument because the two sheets want opposite ones, and
    picking one for both hid a real fault. A dark ground is right for the flash:
    it emits, and it is composited additively. It is wrong for the plume, which
    occludes - grey smoke on a 0.16 grey is a strong signal and the same smoke on
    the game's pale field is nearly nothing. The plume was tuned on the dark one,
    looked convincing, and moved the harness picture by 20 levels out of 255.
    """
    drawn = [layers[n][1] for n in used if n in layers]
    size = max(m["tile"][0] for m in drawn)
    at = np.array([max(m["anchor_px"][i] for m in drawn) for i in range(2)])
    out = np.zeros((len(rows) * size, len(cols) * size, 4), dtype=np.float32)
    out[:, :, :3] = background
    out[:, :, 3] = 1.0

    def place(buf, layer, index, blend):
        src = _tile_of(layers, layer, index)
        anchor = np.array(layers[layer][1]["anchor_px"])
        # tile pixels are top-down, the arrays are bottom-up: x shifts as it
        # reads, y shifts the other way
        dx = int(round(at[0] - anchor[0]))
        dy = int(round((size - at[1]) - (src.shape[0] - anchor[1])))
        x0, y0 = max(dx, 0), max(dy, 0)
        sx, sy = max(-dx, 0), max(-dy, 0)
        w = min(src.shape[1] - sx, size - x0)
        h = min(src.shape[0] - sy, size - y0)
        if w <= 0 or h <= 0:
            return buf
        patch = buf[y0:y0 + h, x0:x0 + w]
        buf[y0:y0 + h, x0:x0 + w] = blend(patch, src[sy:sy + h, sx:sx + w])
        return buf

    for ri, row in enumerate(rows):
        for ci, col in enumerate(cols):
            cell = out[(len(rows) - ri - 1) * size:(len(rows) - ri) * size,
                       ci * size:(ci + 1) * size]
            cell[:] = draw(cell.copy(), place, row, col)

    path = os.path.join(cfg["output_dir"], "%s.png" % name)
    img = bpy.data.images.new(name, len(cols) * size, len(rows) * size,
                              alpha=True, float_buffer=False)
    try:
        img.colorspace_settings.name = "Non-Color"
        img.alpha_mode = "CHANNEL_PACKED"
        img.pixels.foreach_set(out.reshape(-1))
        img.file_format = "PNG"
        img.filepath_raw = path
        img.save()
    finally:
        bpy.data.images.remove(img)
    return path


def _contrast(layers, name, headings, ground):
    """How much an occluding layer moves the picture, in levels of 255.

    Against the tile's own colour, because that is the ground it will be seen
    on. Size says nothing about this: the exhaust plume measured a healthy
    47x31 px while shifting the harness picture by 20 levels, which on screen is
    a smudge you have to be told is there.
    """
    _, meta = layers[name]
    worst = 0.0
    for heading in headings:
        for p in range(meta["phases"]):
            t = _tile_of(layers, name, p * meta["count"] + heading)
            a = t[:, :, 3:4]
            over = t[:, :, :3] * a + ground[None, None, :] * (1.0 - a)
            worst = max(worst, float(np.abs(over - ground).max()))
    return worst * 255.0


def _loop(layers, name, heading, tolerance):
    """How evenly the phases step, and whether the wrap is one of the steps.

    The plume's loop closes by construction - a puff's age is `frac(...)`, so
    phase N is phase 0 - but that is an argument, and the house rule is to
    measure the picture. A seam shows up as exactly one outlier: the closing
    step is much larger than every other, either because the smoke jumps back
    to the port or because it appears out of nothing.
    """
    _, meta = layers[name]
    phases = meta["phases"]
    centres, mass = [], []
    for p in range(phases):
        a = _tile_of(layers, name, p * meta["count"] + heading)[:, :, 3]
        total = float(a.sum())
        mass.append(total)
        if total <= 1e-6:
            centres.append(None)
            continue
        ys, xs = np.nonzero(a > 0.0)
        w = a[ys, xs]
        centres.append((float((xs * w).sum() / total),
                        float((ys * w).sum() / total)))

    steps, mass_steps = [], []
    for p in range(phases):
        q = (p + 1) % phases
        if centres[p] is None or centres[q] is None:
            steps.append(float("inf"))
        else:
            steps.append(math.hypot(centres[q][0] - centres[p][0],
                                    centres[q][1] - centres[p][1]))
        mass_steps.append(abs(mass[q] - mass[p]))

    # Against the *worst ordinary step*, not against a typical one.
    #
    # The first version compared the closing step to the median of the others
    # and cried seam at a wrap that was in fact six times smaller than typical.
    # The mass criterion did it: a loop in steady state barely changes mass from
    # phase to phase, so the median change is a noise floor and every ratio
    # taken against it is noise over noise. What a seam actually looks like is
    # an *outlier* - the closing step far bigger than any other - and asking
    # that directly needs no tolerance on the noise at all.
    worst = max(steps[:-1]) if phases > 1 else 0.0
    worst_mass = max(mass_steps[:-1]) if phases > 1 else 0.0
    report = {
        "phases": phases,
        "empty_phases": [p for p, c in enumerate(centres) if c is None],
        "step_px": [round(s, 3) for s in steps],
        "typical_step_px": round(float(np.median(steps[:-1])), 3),
        "worst_step_px": round(worst, 3),
        "wrap_step_px": round(steps[-1], 3),
        "wrap_mass_ratio": round(mass_steps[-1] / max(worst_mass, 1e-6), 3),
    }
    # a whole pixel of slack either way, or a plume that barely moves between
    # phases fails on rounding noise rather than on a seam
    report["seamless"] = bool(
        steps[-1] <= max(tolerance * worst, 1.0)
        and mass_steps[-1] <= max(tolerance * worst_mass, 0.01 * max(mass)))
    return report


def check(cfg=None):
    """Composite the layers with no offsets, and measure what the picture shows.

    The composite is the check. A wrong axis, a tile at the wrong height, a
    flash off the bore or a plume that pops at the seam are obvious here and
    invisible in a return value - the first three of those happened.
    """
    cfg = dict(CONFIG, **(cfg or {}))
    layers = _atlases(cfg)
    meta = layers["hull"][1]
    report = {"problems": [], "layers": sorted(layers)}

    headings = cfg["check_headings"]

    # --- the shot: phase across, heading down --------------------------------
    if "flash" in layers:
        def draw_shot(buf, place, heading, phase):
            frame = phase * meta["count"] + heading
            for name, index in (("hex", 0), ("hull", heading),
                                ("turret", heading), ("smoke", frame)):
                buf = place(buf, name, index, _over)
            if "exhaust" in layers:
                buf = place(buf, "exhaust", heading, _over)
            # additive, because that is how the game draws it. Composited with
            # plain alpha the flash comes out visibly paler and the picture
            # understates it. The smoke above is the opposite - it occludes, so
            # it goes on with normal alpha and *under* the fire.
            return place(buf, "flash", frame, _add)

        report["composite"] = _sheet(
            cfg, layers, "_check_shot", headings, cfg["check_phases"],
            draw_shot, 0.16,
            ["hex", "hull", "turret", "exhaust", "smoke", "flash"])

    # --- the plume: one full lap across --------------------------------------
    if "exhaust" in layers:
        plume_meta = layers["exhaust"][1]

        def draw_plume(buf, place, heading, phase):
            for name, index in (("hex", 0), ("hull", heading),
                                ("turret", heading)):
                buf = place(buf, name, index, _over)
            return place(buf, "exhaust",
                         phase * plume_meta["count"] + heading, _over)

        report["exhaust_composite"] = _sheet(
            cfg, layers, "_check_exhaust", headings,
            list(range(plume_meta["phases"])), draw_plume,
            _ground_colour(layers), ["hex", "hull", "turret", "exhaust"])

    # --- the burning tank: one full lap across -------------------------------
    if "fire" in layers:
        fire_meta = layers["fire"][1]

        def draw_fire(buf, place, heading, phase):
            for name, index in (("hex", 0), ("hull", heading),
                                ("turret", heading)):
                buf = place(buf, name, index, _over)
            # Smoke down first with normal alpha, then the flame added on top of
            # it. That order is the effect, not a preference: the flame is added,
            # so what is behind it decides how much of it arrives, and the smoke
            # is there to put something dark there. Reversed, the smoke would
            # grey out the fire it is meant to back.
            if "burn" in layers:
                buf = place(buf, "burn",
                            phase * layers["burn"][1]["count"] + heading, _over)
            return place(buf, "fire", phase * fire_meta["count"] + heading, _add)

        report["fire_composite"] = _sheet(
            cfg, layers, "_check_fire", headings,
            list(range(fire_meta["phases"])), draw_fire,
            _ground_colour(layers), ["hex", "hull", "turret", "burn", "fire"])

    # --- nothing may touch the tile edge, on any frame of any tank layer -----
    edges = {}
    for name in ("hull", "turret", "hex"):
        worst = _edge_alpha(layers, name)
        edges[name] = round(worst, 4)
        if worst > 0.0:
            report["problems"].append(
                "%s touches the tile edge (alpha %.3f) - the frame is too "
                "tight and the sprite is being clipped" % (name, worst))
    report["max_edge_alpha"] = edges

    # The effects get the same test, but it means something different and so is
    # reported separately. A tank layer touching the edge is a framing bug. An
    # effect touching it is the tile budget running out: the muzzle sits a fixed
    # distance from the spin axis, the frame has half a tile, and what is left
    # over is all the flash may have. That distance differs per tank, so an
    # effect sized in radii can fit on one gun and be cut on another - this is
    # the one thing tuning on a bench cannot tell you.
    if "flash" in layers:
        worst = _edge_alpha(layers, "flash")
        report["flash_edge_alpha"] = round(worst, 4)
        if worst > 0.02:
            report["problems"].append(
                "the flash reaches the tile edge (alpha %.3f) and is being cut "
                "off - shorten it in muzzle_flash.CONFIG, or raise "
                "effect_tile_scale" % worst)
    if "exhaust" in layers:
        worst = _edge_alpha(layers, "exhaust")
        report["exhaust_edge_alpha"] = round(worst, 4)
        if worst > 0.02:
            report["problems"].append(
                "the plume reaches the tile edge (alpha %.3f) and is being cut "
                "off - lower rise in exhaust_plume.CONFIG, or raise "
                "plume_tile_scale" % worst)
    if "fire" in layers:
        worst = _edge_alpha(layers, "fire")
        report["fire_edge_alpha"] = round(worst, 4)
        if worst > 0.02:
            report["problems"].append(
                "the flame reaches the tile edge (alpha %.3f) and is being cut "
                "off - the embers are the tallest thing in it, so lower "
                "ember_reach or rise in engine_fire.CONFIG, or raise "
                "fire_tile_scale" % worst)
    if "burn" in layers:
        worst = _edge_alpha(layers, "burn")
        report["burn_edge_alpha"] = round(worst, 4)
        if worst > 0.02:
            report["problems"].append(
                "the smoke column reaches the tile edge (alpha %.3f) and is "
                "being cut off - lower rise in engine_fire.SMOKE, or raise "
                "burn_tile_scale" % worst)

    # --- the tank stands on the tile, rather than the tile cutting through ---
    hull_a = _tile_of(layers, "hull", 0)[:, :, 3] > 0.5
    hex_a = _tile_of(layers, "hex", 0)[:, :, 3] > 0.5
    hull_rows = np.nonzero(hull_a)[0]
    hex_rows = np.nonzero(hex_a)[0]
    stands = bool(hex_rows.min() <= hull_rows.min() <= hex_rows.max())
    report["hull_rows"] = [int(hull_rows.min()), int(hull_rows.max())]
    report["hex_rows"] = [int(hex_rows.min()), int(hex_rows.max())]
    report["stands_on_tile"] = stands
    if not stands:
        report["problems"].append(
            "the hull's lowest pixel is outside the tile's span, so the tank "
            "is not standing on the tile - check hex_base ground_z")

    # --- the flash sits on the bore ------------------------------------------
    if "flash" in layers:
        # The flash sits on the tank's symmetry line, at the two headings where
        # that line is vertical on screen. This is the cheap independent test
        # for a muzzle point off the bore axis: it needs no camera maths, only
        # the fact that a tank pointing at or away from the camera is symmetric.
        offsets = {}
        for frame in meta["frames"]:
            facing = None
            for part in (frame.get("label") or "").split(" "):
                if part.isdigit():
                    facing = int(part)
                    break
            if facing is None or facing % 180 != int(cfg["front_dir"]) % 180:
                continue
            flash = _tile_of(layers, "flash", 1 * meta["count"] + frame["index"])
            cx = _centroid_x(flash)
            if cx is None:
                continue
            # against the flash layer's own anchor - it may be a wider tile
            anchor = layers["flash"][1]["anchor_px"]
            offsets[facing] = round(cx - anchor[0], 2)
            # Only the heading that fires at the camera is asserted on. The
            # opposite one is measured too, but the flash there is behind the
            # turret and clipped by the holdout, and the tank is not symmetric
            # about that cut - the aerial stands off to one side and shaves the
            # flash unevenly. It read 1.85px off on MT with the muzzle dead on
            # the bore, which is noise, not error.
            if facing != int(cfg["front_dir"]) % 360:
                continue
            if abs(cx - anchor[0]) > cfg["centre_tolerance_px"]:
                report["problems"].append(
                    "at facing %d the flash sits %.1f px off the tank's centre "
                    "line - the muzzle point is not on the bore axis"
                    % (facing, cx - anchor[0]))
        report["flash_offcentre_px"] = offsets

    # --- the plume loops ------------------------------------------------------
    if "exhaust" in layers:
        loops = {h: _loop(layers, "exhaust", h, cfg["loop_tolerance"])
                 for h in headings}
        report["exhaust_loop"] = loops
        for heading, loop in loops.items():
            if loop["empty_phases"]:
                report["problems"].append(
                    "at heading index %d the plume is empty on phases %s - "
                    "nothing is drawn for part of the lap"
                    % (heading, loop["empty_phases"]))
            elif not loop["seamless"]:
                report["problems"].append(
                    "at heading index %d the plume's loop is not seamless: the "
                    "closing step is %.1f px against a worst ordinary %.1f "
                    "(mass %.1fx) - it will pop once a lap"
                    % (heading, loop["wrap_step_px"], loop["worst_step_px"],
                       loop["wrap_mass_ratio"]))

        # How big it actually is. Fractions of a hull do not say, and this is
        # the number the effect lives or dies by: an idling engine is haze over
        # the deck, and the same config one notch up is a factory chimney.
        pm = layers["exhaust"][1]
        rows, cols = [], []
        for p in range(pm["phases"]):
            a = _tile_of(layers, "exhaust", p * pm["count"] + headings[0])[:, :, 3]
            ys, xs = np.nonzero(a > 0.02)
            if len(ys):
                rows.extend([int(ys.min()), int(ys.max())])
                cols.extend([int(xs.min()), int(xs.max())])
        report["exhaust_span_px"] = (
            [max(cols) - min(cols) + 1, max(rows) - min(rows) + 1]
            if rows else [0, 0])

        # Grey smoke is a strong signal on the check sheet's dark grey and a
        # weak one on the game's pale field, and only the second is the real
        # question. See `_contrast`.
        ground = _ground_colour(layers)
        worst = _contrast(layers, "exhaust", headings, ground) / 255.0
        report["ground_colour"] = [round(float(v), 3) for v in ground]
        report["exhaust_contrast"] = round(worst * 255.0, 1)
        if worst * 255.0 < cfg["exhaust_contrast_min"]:
            report["problems"].append(
                "the plume only moves the picture by %.0f levels of 255 against "
                "the ground it stands on - it is there in the alpha and not on "
                "the screen. Raise alpha or opacity, or lower rim_falloff, "
                "which costs more density than it looks like it should"
                % (worst * 255.0))
        hull_cols = np.nonzero(hull_a.any(axis=0))[0]
        hull_lines = np.nonzero(hull_a.any(axis=1))[0]
        report["hull_span_px"] = [int(hull_cols.max() - hull_cols.min() + 1),
                                  int(hull_lines.max() - hull_lines.min() + 1)]

    # --- the smoke column loops, and is actually dark ------------------------
    if "burn" in layers:
        loops = {h: _loop(layers, "burn", h, cfg["loop_tolerance"])
                 for h in headings}
        report["burn_loop"] = loops
        for heading, loop in loops.items():
            if loop["empty_phases"]:
                report["problems"].append(
                    "at heading index %d the smoke column is empty on phases "
                    "%s" % (heading, loop["empty_phases"]))
            elif not loop["seamless"]:
                report["problems"].append(
                    "at heading index %d the smoke column's loop is not "
                    "seamless: the closing step is %.1f px against a worst "
                    "ordinary %.1f (mass %.1fx) - it will pop once a lap"
                    % (heading, loop["wrap_step_px"], loop["worst_step_px"],
                       loop["wrap_mass_ratio"]))

        bm = layers["burn"][1]
        rows, cols = [], []
        for p in range(bm["phases"]):
            a = _tile_of(layers, "burn", p * bm["count"] + headings[0])[:, :, 3]
            ys, xs = np.nonzero(a > 0.02)
            if len(ys):
                rows.extend([int(ys.min()), int(ys.max())])
                cols.extend([int(xs.min()), int(xs.max())])
        report["burn_span_px"] = (
            [max(cols) - min(cols) + 1, max(rows) - min(rows) + 1]
            if rows else [0, 0])

        # Same measurement as the plume's and the same threshold, because it is
        # the same question: an occluding layer that does not move the picture
        # is not there. It should pass by a mile - this one is nearly black on a
        # pale field - and if it does not, the fire has lost its backdrop and
        # the whole reason for the layer with it.
        worst = _contrast(layers, "burn", headings, _ground_colour(layers))
        report["burn_contrast"] = round(worst, 1)
        if worst < cfg["exhaust_contrast_min"]:
            report["problems"].append(
                "the smoke column only moves the picture by %.0f levels of 255 "
                "against the ground it stands on - raise alpha or opacity in "
                "engine_fire.SMOKE, or darken its colour" % worst)

    # --- the fire loops, and stays the colour it was tuned to ----------------
    if "fire" in layers:
        loops = {h: _loop(layers, "fire", h, cfg["loop_tolerance"])
                 for h in headings}
        report["fire_loop"] = loops
        for heading, loop in loops.items():
            if loop["empty_phases"]:
                report["problems"].append(
                    "at heading index %d the flame is empty on phases %s - the "
                    "tank stops burning for part of the lap"
                    % (heading, loop["empty_phases"]))
            elif not loop["seamless"]:
                report["problems"].append(
                    "at heading index %d the flame's loop is not seamless: the "
                    "closing step is %.1f px against a worst ordinary %.1f "
                    "(mass %.1fx) - it will pop once a lap"
                    % (heading, loop["wrap_step_px"], loop["worst_step_px"],
                       loop["wrap_mass_ratio"]))

        fm = layers["fire"][1]
        rows, cols = [], []
        for p in range(fm["phases"]):
            a = _tile_of(layers, "fire", p * fm["count"] + headings[0])[:, :, 3]
            ys, xs = np.nonzero(a > 0.02)
            if len(ys):
                rows.extend([int(ys.min()), int(ys.max())])
                cols.extend([int(xs.min()), int(xs.max())])
        report["fire_span_px"] = (
            [max(cols) - min(cols) + 1, max(rows) - min(rows) + 1]
            if rows else [0, 0])

        # How red the picture actually goes, once the layer has been *added* to
        # the ground. This is the fire's version of the plume's contrast check
        # and it asks a different question on purpose: the plume can be present
        # in the alpha and absent on screen, while the fire's failure is to
        # arrive too hard and clip to white, which is brighter and worse. So the
        # statistic is hue, and it is the median over the body of the flame
        # rather than a maximum - one red pixel at the rim proves nothing.
        #
        # The pale ground is the worst case, and it stays the worst case now
        # that the smoke column exists: the column only ever puts something
        # darker behind the flame, and addition over dark clips later, not
        # sooner. What is measured here is the part that sticks out past it.
        ground = _ground_colour(layers)
        base = float(ground[0] - 0.5 * (ground[1] + ground[2]))
        reds, white, seen = [], 0, 0
        for heading in headings:
            for p in range(fm["phases"]):
                t = _tile_of(layers, "fire", p * fm["count"] + heading)
                a = t[:, :, 3]
                mask = a > 0.25
                if not mask.any():
                    continue
                over = np.clip(t[:, :, :3] * a[:, :, None]
                               + ground[None, None, :], 0.0, 1.0)[mask]
                reds.append(over[:, 0] - 0.5 * (over[:, 1] + over[:, 2]))
                white += int((over.min(axis=1) > 0.99).sum())
                seen += int(mask.sum())
        if seen:
            redness = float(np.median(np.concatenate(reds))) - base
            report["fire_redness"] = round(redness * 255.0, 1)
            report["fire_white_fraction"] = round(white / float(seen), 3)
            if redness * 255.0 < cfg["fire_redness_min"]:
                report["problems"].append(
                    "the flame is only %.0f levels of 255 redder than the "
                    "ground it burns on - additive compositing has taken the "
                    "colour. Lower brightness or alpha in engine_fire.CONFIG, "
                    "or move the ramp's stops away from white; raising them "
                    "makes this worse, not better" % (redness * 255.0))
            if white / float(seen) > 0.25:
                report["problems"].append(
                    "%.0f%% of the flame clips to white - it is not brighter, "
                    "it is colourless. The lever is brightness, not the ramp"
                    % (100.0 * white / float(seen)))

    report["step_deg"] = 360.0 / meta["count"]
    report["anchor_px"] = meta["anchor_px"]
    report["tiles"] = {name: m["tile"][0] for name, (_, m) in layers.items()}
    report["units_per_pixel"] = meta["units_per_pixel"]
    return report


def run(cfg=None):
    """prepare, render, check - and say plainly whether it is good."""
    cfg = dict(CONFIG, **(cfg or {}))
    prepared = prepare(cfg)
    rendered = render(cfg)
    checked = check(cfg)

    problems = list(rendered["warnings"]) + list(checked["problems"])
    if not rendered["framing_identical"]:
        problems.append("layers do not share a framing")
    for stage in ("muzzle", "exhaust"):
        if prepared[stage]:
            problems.extend(prepared[stage]["warnings"])

    report = {
        "have": prepared["have"],
        "ground_z": prepared["hex"]["ground_z"],
        "stands_on_tile": checked["stands_on_tile"],
        "framing_identical": rendered["framing_identical"],
        "spin_pivot": rendered["spin_pivot"],
        "spin_pivot_from": rendered["spin_pivot_from"],
        "anchor_px": checked["anchor_px"],
        "units_per_pixel": checked["units_per_pixel"],
        "max_edge_alpha": checked["max_edge_alpha"],
        "tiles": checked["tiles"],
        "problems": problems,
    }
    if prepared["muzzle"]:
        report.update({
            "muzzle_point": prepared["muzzle"]["muzzle_point"],
            "muzzle_shape": prepared["muzzle"]["muzzle_shape"],
            "bearing_disagreement": prepared["muzzle"]["bearing_disagreement"],
            "flash_edge_alpha": checked["flash_edge_alpha"],
            "flash_offcentre_px": checked["flash_offcentre_px"],
            "composite": checked["composite"],
        })
    if prepared["exhaust"]:
        report.update({
            "exhaust_ports": prepared["exhaust"]["ports"],
            "exhaust_edge_alpha": checked["exhaust_edge_alpha"],
            "exhaust_span_px": checked["exhaust_span_px"],
            "hull_span_px": checked["hull_span_px"],
            "exhaust_contrast": checked["exhaust_contrast"],
            "ground_colour": checked["ground_colour"],
            "exhaust_loop": {h: v["seamless"]
                             for h, v in checked["exhaust_loop"].items()},
            "exhaust_composite": checked["exhaust_composite"],
            "fire_edge_alpha": checked["fire_edge_alpha"],
            "fire_span_px": checked["fire_span_px"],
            "fire_redness": checked.get("fire_redness"),
            "fire_white_fraction": checked.get("fire_white_fraction"),
            "fire_loop": {h: v["seamless"]
                          for h, v in checked["fire_loop"].items()},
            "fire_composite": checked["fire_composite"],
            "burn_edge_alpha": checked["burn_edge_alpha"],
            "burn_span_px": checked["burn_span_px"],
            "burn_contrast": checked["burn_contrast"],
            "burn_loop": {h: v["seamless"]
                          for h, v in checked["burn_loop"].items()},
        })

    print("[tank] %s  %d problems" % (cfg["output_dir"], len(problems)))
    for line in problems:
        print("[tank]   ! %s" % line)
    for key in ("composite", "exhaust_composite", "fire_composite"):
        if report.get(key):
            print("[tank] now open %s and look at it - the numbers above have "
                  "all been green while the picture was wrong" % report[key])
    return report


if __name__ == "__main__":
    from pprint import pprint

    pprint(run(CONFIG))
