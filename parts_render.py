"""Render the parts-built tank: hull, turret, two track belts.

This is the slim analogue of `tank_pipeline.render()` for the model that arrives
already split into parts, each hanging off its own `*.World` empty and carrying
its own material and texture. The canonical layout, four layer roots directly at
the scene root with no `world` above them:

    Hull.World          Hull.Geometry, Engine.Geometry
    Turret.World        Turret.Geometry, Barrel.Geometry
    Track.Left.World    L.Caterpillar.Geometry, L.Rolls.Geometry
    Track.Right.World   R.Caterpillar.Geometry, R.Rolls.Geometry

`Barrel` and `Engine` are the hand-cut effect pieces and are optional. Note that
the `.Geometry` suffix defeats the stock CONFIG of every effect module: they
resolve `hull`/`turret`/`barrel` by exact name against the bare `Hull`, `Turret`,
`Barrel` of the old single-mesh scenes.

Two things here are not in the old pipeline.

The ring cut
------------
The delivered turret includes its basket - the cylinder that lives *inside* the
hull. On MT_PARTS that is 27% of its vertices, reaching 22 px below the deck.
A 3D render hides it with depth; flat layers cannot, so the turret layer paints
it over the hull deck and the turret reads as standing on a disc rather than
sitting in a ring.

The fix has to be a **horizontal plane**, and that is the whole point. The
obvious move - hold the hull out of the turret layer - is wrong: the turret
turns independently, so a hull silhouette baked into the turret's alpha is right
in exactly one of the twelve hull headings. A horizontal cut is right in all of
them, because it does not depend on either heading.

Nothing is cut from the mesh. A box below the plane is put in the turret layer's
`holdout`, so the cut is a render setting, re-runnable and reversible, and the
plane is measured rather than typed in.

Which holdouts are legal
------------------------
Generalising the above: **a layer may only bake in a holdout for something that
does not turn relative to it.** The hull may be held out of the tracks - they
are bolted to it. The hull may not be held out of the turret. A horizontal plane
may always be held out of anything.

The first draft also held the turret out of the track layers. That is the same
mistake and it happened to be harmless: the turret sits entirely above the belts
and inside the hull's footprint, so at a 30-degree elevation it projects above
them and can never cover them. Harmless is not the same as right, so it is gone.

The spin axis is measured, never read off the empty
---------------------------------------------------
`Turret.World` looks like a declared pivot and is not one: on MT_PARTS it sits
**14.6 px** from the ring the turret actually turns on. Spinning the carousel
about it does not distort anything - it walks the whole tank round a circle of
that radius, which is the failure mode `turret_axis.py` exists to catch and
which no output number reports on its own.

So the axis comes from `turret_axis.measure()`, the same fit the old pipeline
uses, and the empty is only compared against it. Do not be tempted to trust the
empty because it is there: three independent checks put the ring at y=-0.0294
(band fit 0.991 round, cross-band agreement 0.04 mm; a minimax circle on the
basket wall; and the concentricity of the hull's own deck opening, which is
0.63 px consistent about the fitted centre and 9.8 px about the empty).
"""

import json
import os

import numpy as np

CONFIG = {
    "hull": "Hull.World",
    "turret": "Turret.World",
    "tracks": ("Track.Left.World", "Track.Right.World"),
    "hull_mesh": "Hull.Geometry",
    "turret_mesh": "Turret.Geometry",

    "output_dir": None,          # required
    "steps": 12,
    "tile": 256,
    "azimuth": 0.0,
    "elevation": 30.0,
    "front_dir": 270.0,

    # radial bins used to read the deck height; 0.40 covers the widest turret
    "probe_radius": 0.40,
    "probe_bins": 40,
    "cut_box": "_ring_cut",
    "cut_z": None,               # override the measurement, for A/B only

    # the belts run: one phase axis per track layer, `track_cycle` poses them.
    # 0 renders them still, which is the A/B.
    "track_phases": 8,
    # render one extra phase, a slide of one whole link, which must come out
    # identical to phase 0. For checking the seam, not for shipping.
    "seam_probe": False,

    # belt meshes to lay out afresh from one of their own links, so that their
    # cycle closes by construction. Naming one side and not the other puts the
    # two answers next to each other in one render - see track_cycle.rebuild.
    "rebuild": (),

    # belt name -> the loop to lay it on instead of its own, as keyword
    # arguments for `track_cycle.Belt.on_loop`: `length`, `height`, `corner`
    # (the corner radius as a fraction of half the height, so 1.0 is a stadium),
    # `keep` ("ground" holds the bottom run, which is what the tank stands on).
    # A belt named here has to be named in `rebuild` too: the reshape is a loop,
    # and something has to be laid out on it. One side only is the A/B.
    #
    # The word `"preview"` instead of a dict means "the shape the bench is
    # holding", read off the `<belt>.Preview` object that `track_cycle.preview`
    # leaves in the scene. That is the whole point of it: retyping the four
    # numbers here is one shape in two places, and it fails in the quiet
    # direction - drag the preview once more and this keeps the old figure.
    # A missing preview raises; see `track_cycle.shape_from_preview`.
    "reshape": {},

    # the gun tube slides back into the turret on firing: a phase axis on a
    # layer of its own, and the turret gives the tube up to it. 0 leaves the
    # tube in the turret layer exactly as before, which is the A/B.
    # See `barrel_recoil.py` - the travel and the curve live there, and the count
    # follows the travel: about one phase per pixel of stroke.
    "recoil_phases": 5,

    # the ground tile: built at render time and never saved, as everywhere else
    # in the project. None -> skip it.
    "hex": {},
}


def _world(obj):
    me = obj.data
    n = len(me.vertices)
    co = np.empty(n * 3, np.float32)
    me.vertices.foreach_get("co", co)
    mw = np.array(obj.matrix_world)
    return co.reshape(n, 3) @ mw[:3, :3].T + mw[:3, 3]


def ring_axis(cfg=None):
    """Where the turret really turns, and what trusting the empty would cost.

    Delegated to `turret_axis.measure()` so there is one fit in the project and
    not two that can drift apart. Nothing is moved or stamped here - this scene
    is hand-assembled and the render has no business rewriting it.

    But `tank_pipeline.prepare()` *does* stamp it, so from that point on the same
    number exists twice: measured here and stamped there. They come from one fit
    with one config and agree - and "agree" is a claim, so it is checked rather
    than assumed. A silent disagreement would put the flash on the stamped axis
    and spin the carousel about this one, which does not distort anything: it
    walks the whole tank round a circle, the one failure no output number reports.
    """
    import bpy, importlib
    cfg = dict(CONFIG, **(cfg or {}))
    ta = importlib.import_module("turret_axis")
    probe = dict(ta.CONFIG, turret=cfg["turret_mesh"], hull=cfg["hull_mesh"],
                 apply=False, stamp=False)
    _, _, axis, seam_z, rep = ta.measure(probe)
    declared = np.array(bpy.data.objects[cfg["turret"]].location[:2], float)

    warnings = list(rep["warnings"])
    stamped = {}
    for which in ("turret_mesh", "hull_mesh"):
        ob = bpy.data.objects.get(cfg[which])
        if ob is not None and "ring_axis" in ob.keys():
            held = [float(v) for v in ob["ring_axis"]]
            stamped[ob.name] = [round(v, 5) for v in held]
            gap = float(np.hypot(held[0] - axis[0], held[1] - axis[1]))
            if gap > 1e-5:
                warnings.append(
                    "ring_axis stamped on %s is %.5f away from the fit this "
                    "render is spinning about - one of them is stale, and a "
                    "wrong axis walks the tank round a circle of that radius"
                    % (ob.name, gap))
    return {
        "axis": [round(float(v), 5) for v in axis[:2]],
        "stamped": stamped,
        "seam_z": round(float(seam_z), 5),
        "roundness": rep["roundness"],
        "cross_band_spread": rep["cross_band_spread"],
        "declared_empty_xy": [round(float(v), 5) for v in declared],
        # the number that matters: the orbit the tank would walk on the wrong axis
        "wobble_px_if_empty_trusted": rep["wobble_px_before"],
        "axis_warnings": warnings,
        "rotatable": rep.get("rotatable"),
    }


def ring_plane(cfg=None, axis=None):
    """Height of the deck the turret sits in, and how flat that deck is.

    The region to cut is self-defining: it is exactly the radii where the turret
    dips below the hull's top surface.

    Over those radii the cut takes the **highest** hull surface, not the average
    one, and that is not a detail. The hull here has a recessed ring seat at
    0.1686 inside a deck at 0.193; a median lands in the recess and leaves 4 px
    of turret standing inside the deck and painted over it. The rule that makes
    it obvious: turret geometry is hidden by whatever hull stands next to it, so
    the plane has to clear the tallest of that, not the typical.

    The first version got 0.193 anyway - by accident, because it probed about
    the empty rather than the ring and smeared the recess across the annulus.
    A statistic that only works with the axis wrong is not a statistic.

    Note this is the **deck**, not `turret_axis`'s `seam_z`. The seam is the
    bottom of the roundest band - the mating face - and on MT_PARTS it sits
    0.06 below the deck. Cutting there would leave 11 px of basket standing
    above the deck and painted over it, which is the whole bug.
    """
    import bpy
    cfg = dict(CONFIG, **(cfg or {}))
    hull = _world(bpy.data.objects[cfg["hull_mesh"]])
    turret = _world(bpy.data.objects[cfg["turret_mesh"]])
    if axis is None:
        axis = ring_axis(cfg)["axis"]
    axis = np.asarray(axis, float)[:2]

    nb, R = cfg["probe_bins"], cfg["probe_radius"]
    rh = np.hypot(hull[:, 0] - axis[0], hull[:, 1] - axis[1])
    rt = np.hypot(turret[:, 0] - axis[0], turret[:, 1] - axis[1])
    bh = np.clip((rh / R * nb).astype(int), 0, nb - 1)
    bt = np.clip((rt / R * nb).astype(int), 0, nb - 1)

    deck = np.full(nb, -np.inf)
    np.maximum.at(deck, bh, hull[:, 2])
    tlo = np.full(nb, np.inf)
    np.minimum.at(tlo, bt, turret[:, 2])

    sunk = np.isfinite(deck) & np.isfinite(tlo) & (tlo < deck)
    if not sunk.any():
        return {"cut_z": None, "sunk_bins": 0,
                "note": "the turret never reaches below the deck - no cut needed"}

    z = float(deck[sunk].max()) if cfg["cut_z"] is None else float(cfg["cut_z"])
    below = turret[:, 2] < z
    return {
        "cut_z": round(z, 5),
        "cut_from": "measured" if cfg["cut_z"] is None else "override",
        "deck_max": round(float(deck[sunk].max()), 5),
        "deck_median": round(float(np.median(deck[sunk])), 5),
        "deck_spread": round(float(np.percentile(deck[sunk], 90)
                                   - np.percentile(deck[sunk], 10)), 5),
        "sunk_bins": int(sunk.sum()),
        "sunk_radius": [round(float(np.nonzero(sunk)[0].min() * R / nb), 4),
                        round(float((np.nonzero(sunk)[0].max() + 1) * R / nb), 4)],
        "basket_depth": round(z - float(turret[:, 2].min()), 5),
        "turret_verts_below_cut": int(below.sum()),
        "turret_pct_below_cut": round(100.0 * float(below.mean()), 1),
        "probed_about": [round(float(v), 5) for v in axis],
    }


def ground_tile(cfg, axis, drop=()):
    """The hex the tank stands on, built about the measured ring.

    `hex_base.make_hex` wants one root to walk, and this model has four empties
    at the scene root instead - so the pieces of it are used directly rather
    than the whole. Nothing is duplicated: the footprint, the radius and the
    n-gon all come from `hex_base`.

    Two departures from its defaults, both because the tank is placed by hand:
    the tile goes to the tank rather than the tank to the tile, and the centre
    is the ring `turret_axis` measured, not a stamp (this scene carries none)
    and not the footprint. Hull, turret, belts and tile sharing one axis is the
    whole reason `anchor_px` means anything.

    The turret is left out of the fit for the reason the gun barrel always is:
    what must stay inside the cell is the part that drives over it.
    """
    import bpy, importlib, math
    hb = importlib.import_module("hex_base")
    roots = [cfg["hull"]] + list(cfg["tracks"])
    stand = [o for r in roots for o in hb.hierarchy(bpy.data.objects[r])
             if o.type == "MESH" and o.data.vertices and o.name not in drop]
    if not stand:
        raise RuntimeError("nothing to stand on the tile")
    _, _, low_z = hb.footprint(stand)
    radius = hb.radius_about(stand, axis)
    hex_cfg = dict(hb.CONFIG, **(cfg.get("hex") or {}))
    circum = (float(hex_cfg["size"]) if hex_cfg["size"]
              else radius * hex_cfg["padding"] / math.cos(math.radians(30.0)))
    ob = hb.build_hex(hex_cfg["name"], (axis[0], axis[1]), circum, low_z,
                      hex_cfg["orientation"], hex_cfg)
    bpy.context.scene.collection.objects.link(ob)
    return ob, {"name": ob.name, "centre": [round(float(v), 5) for v in axis[:2]],
                "circumradius": round(circum, 5), "ground_z": round(low_z, 5),
                "fit_radius": round(radius, 5), "stood_on": len(stand)}


def _make_cut_box(name, z, reach=4.0):
    import bpy
    me = bpy.data.meshes.new(name)
    verts = [(x, y, zz) for zz in (z - reach, z)
             for x in (-reach, reach) for y in (-reach, reach)]
    faces = [(0, 1, 3, 2), (4, 6, 7, 5), (0, 2, 6, 4),
             (2, 3, 7, 6), (3, 1, 5, 7), (1, 0, 4, 5)]
    me.from_pydata(verts, [], faces)
    me.update()
    ob = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(ob)
    return ob


def _rest_gap(belt):
    """How far the mesh in the scene still is from the pose it arrived in."""
    n = len(belt.me.vertices)
    co = np.empty(n * 3, np.float32)
    belt.me.vertices.foreach_get("co", co)
    return co.reshape(n, 3) - belt.rest


def _belt_layer(name, root, cfg, made, drop):
    """A track layer, with a phase axis if its belt could be found.

    The hook poses only this side's belt. Posing both would be harmless and
    wasteful: the other side is not in this layer's target, so it is not in
    the render at all.

    `drop` names the belts under this root that must not be rendered - the
    original of a rebuilt belt, and any rebuilt belt left in the scene from an
    earlier run that this one is not using. Both would otherwise sit inside the
    same silhouette as the belt that is being drawn.
    """
    import importlib
    layer = {"name": name, "target": root,
             # the hull is welded to the belts, so holding it out is legal
             "holdout": [cfg["hull"]]}
    if drop:
        layer["exclude"] = sorted(drop)
    n = int(cfg["track_phases"] or 0)
    if n > 1 and made:
        tc = importlib.import_module("track_cycle")
        layer["phases"] = n + (1 if cfg["seam_probe"] else 0)
        layer["phase_hook"] = tc.hook(made, denom=n)
        # the belt advances one link over the whole cycle, so this is how far
        # the tank travels per cycle - the number that ties phase to ground
        layer["meta_extra"] = {"track": [b.meta() for b in made]}
    return layer


class Body:
    """The layers that are the tank itself, on the parts layout.

    Split out of `render()` so that `tank_pipeline` can put the effect layers
    beside these and send **one** `render_set` job. That is not tidiness: a
    second job re-resolves the spin axis, the fitted geometry, the angle list and
    the camera, and nothing makes it resolve them the same way. The effect layers
    have to share the tank's framing or they do not line up with it.

    Owns the transient scene state too - the ring-cut box, the ground tile, and
    the belt vertices, which are *written* rather than transformed. `restore()`
    has to run whatever happened, so it is the caller's `finally`.
    """

    def __init__(self, cfg=None):
        import bpy, importlib
        tc = importlib.import_module("track_cycle")
        self.cfg = cfg = dict(CONFIG, **(cfg or {}))
        self.spin = ring_axis(cfg)
        self.ring = ring_plane(cfg, self.spin["axis"])
        self.left, self.right = cfg["tracks"]
        self.hex = None
        self._temps = []

        running = int(cfg["track_phases"] or 0) > 1
        self.belts = tc.belts() if running else []
        self.built = {}
        shapes = dict(cfg.get("reshape") or {})
        for name in cfg["rebuild"] or ():
            src = next((b for b in self.belts if b.ob.name == name), None)
            if src is None:
                raise RuntimeError("%r is not one of the belts %s"
                                   % (name, [b.ob.name for b in self.belts]))
            shape = shapes.pop(name, None)
            if isinstance(shape, str):
                # the shape the bench is holding rather than a copy of it typed
                # in here - `track_cycle` owns the stamp, so it reads it
                shape = tc.shape_from_preview(name)
            self.built[name] = tc.rebuild(
                src, onto=(src.on_loop(**shape) if shape else None))
            src.restore()      # the original stays as it arrived, and unrendered
        if shapes:
            # a reshape with nothing laid out on it is silent otherwise, and it
            # looks exactly like a reshape that did not work
            raise RuntimeError("reshaped %s but did not rebuild them: a loop "
                               "with no belt laid on it renders nothing"
                               % sorted(shapes))

        # animate the rebuilt copy where there is one and the original everywhere
        # else, and keep hold of both so the scene is put back either way
        self.live = [self.built.get(b.ob.name, b) for b in self.belts]
        self.posed = self.belts + list(self.built.values())

        def root_of(ob):
            while ob.parent is not None:
                ob = ob.parent
            return ob

        by_root, drop = {}, {}
        for b in self.live:
            by_root.setdefault(root_of(b.ob).name, []).append(b)
        for b in self.belts:
            name = root_of(b.ob).name
            if b.ob.name in self.built:
                drop.setdefault(name, set()).add(b.ob.name)
            # a rebuilt belt from an earlier run that this one is not drawing
            stale = bpy.data.objects.get(tc.rebuilt_name(b.ob.name))
            if stale is not None and b.ob.name not in self.built:
                drop.setdefault(name, set()).add(stale.name)
        self._by_root, self._drop = by_root, drop

        turret_layer = {"name": "turret", "target": cfg["turret"]}
        if self.ring["cut_z"] is not None:
            box = _make_cut_box(cfg["cut_box"], self.ring["cut_z"])
            self._temps.append(box)
            turret_layer["holdout"] = [cfg["cut_box"]]

        # the gun tube leaves the turret layer for one of its own, so that it can
        # recoil. Both layers keep `Turret.World` as their root: the tube is a
        # child of it and a layer root may not be a child of another layer root.
        self.barrel = None
        self.recoil_skipped = None
        if int(cfg["recoil_phases"] or 0) > 1:
            br = importlib.import_module("barrel_recoil")
            try:
                shorn, self.barrel = br.layers(dict(
                    barrel="Barrel", turret=cfg["turret_mesh"],
                    root=cfg["turret"], phases=int(cfg["recoil_phases"])))
            except (KeyError, RuntimeError) as exc:
                # no Barrel in this scene is a fact about the scene, like a
                # missing Engine, and the tank still renders without it
                self.recoil_skipped = str(exc)
            else:
                turret_layer["exclude"] = list(shorn["exclude"])

        self.layers = [{"name": "hull", "target": cfg["hull"]}, turret_layer,
                       _belt_layer("track_left", self.left, cfg,
                                   by_root.get(self.left, []),
                                   drop.get(self.left)),
                       _belt_layer("track_right", self.right, cfg,
                                   by_root.get(self.right, []),
                                   drop.get(self.right))]
        if self.barrel is not None:
            # right after the turret it came out of, so the composite order in
            # the sheets is the order the game draws
            self.layers.insert(2, self.barrel)
        if cfg["hex"] is not None:
            tile, self.hex = ground_tile(
                cfg, self.spin["axis"], {n for s in drop.values() for n in s})
            self._temps.append(tile)
            # one frame, but framed with the carousel like every other layer
            self.layers.append({"name": "hex", "target": tile.name,
                                "static": True})

    def shared(self):
        """What this layout has to say about the job as a whole."""
        # every layer turns about the measured ring, which is what lets the game
        # stack them at one anchor with the turret aimed elsewhere. Not the
        # empty - see the module docstring.
        return {"spin_pivot": self.spin["axis"]}

    def restore(self):
        import bpy, importlib
        for temp in self._temps:
            data = temp.data
            bpy.data.objects.remove(temp, do_unlink=True)
            bpy.data.meshes.remove(data)
        self._temps = []
        # the belts are posed by writing vertex coordinates, so the .blend has
        # to be put back whatever happened
        for b in self.posed:
            b.restore()
        # the tube is posed by moving the object, and the renderer only restores
        # a layer *root* - the tube is a child, so nothing else will put it back
        if self.barrel is not None:
            importlib.import_module("barrel_recoil").restore()

    def verify(self, res):
        """Say out loud that the belt that was meant to move is in the picture.

        `exclude` matches names by substring, so dropping a belt can silently
        drop its replacement too, and the layer still renders - the road wheels
        are in it as well, so nothing is empty and nothing complains.
        """
        for name, layer in (("track_left", self.left),
                            ("track_right", self.right)):
            want = [b.ob.name for b in self._by_root.get(layer, [])]
            got = res["layers"][name]["rendered"]
            missing = [w for w in want if w not in got]
            if missing:
                raise RuntimeError(
                    "layer %r was meant to animate %s but rendered %s - the "
                    "exclude list matches by substring and may have taken it out"
                    % (name, missing, got))

    def report(self, res):
        import bpy
        cfg = self.cfg
        upp = res["layers"]["hull"]["units_per_pixel"]
        return {
            "spin": self.spin, "ring": self.ring, "hex": self.hex,
            # by its name: the object itself is gone and touching it raises
            "tile_removed": (self.hex is None
                             or self.hex["name"] not in bpy.data.objects),
            "cut_box_removed": cfg["cut_box"] not in bpy.data.objects,
            "units_per_pixel": round(float(upp), 6),
            "track_phases": int(cfg["track_phases"] or 0),
            "seam_probe": bool(cfg["seam_probe"]),
            "rebuilt": {k: v.built for k, v in self.built.items()},
            "belts": [b.report(upp) for b in self.live],
            # the belts are posed by writing coordinates into the scene, so say
            # out loud that the rest pose came back rather than promising it
            "belts_restored": [
                round(float(np.abs(_rest_gap(b)).max()), 9) for b in self.posed],
        }


def render(cfg=None):
    """The tank's own layers and nothing else - the A/B for the belts.

    `tank_pipeline.run()` is the full set. This stays because judging a running
    belt against a still one wants a two-minute render, not a ten-minute one.
    """
    import importlib
    atlas = importlib.import_module("sprite_atlas")
    cfg = dict(CONFIG, **(cfg or {}))
    if not cfg["output_dir"]:
        raise RuntimeError("output_dir is required")

    body = Body(cfg)
    try:
        res = atlas.render_set({
            "shared": dict({
                "output_dir": cfg["output_dir"],
                "steps": cfg["steps"], "tile": cfg["tile"],
                "azimuth": cfg["azimuth"], "elevation": cfg["elevation"],
                "hex_labels": {"front_dir": cfg["front_dir"],
                               "orientation": "flat"},
            }, **body.shared()),
            "layers": body.layers,
        })
    finally:
        body.restore()

    body.verify(res)
    out = dict(body.report(res),
               framing_identical=res["framing_identical"],
               warnings=res["warnings"],
               spin_pivot=[round(float(v), 5) for v in res["spin_pivot"]])

    # Beside the atlases, like tank_pipeline's, so what a set was rendered with
    # is on disk rather than in whoever ran it. The belts especially: which link
    # count and which cut produced these frames is not recoverable from the PNGs.
    with open(os.path.join(cfg["output_dir"], "_run_report.json"), "w") as f:
        json.dump(out, f, indent=1, default=str)
    return out


def check(cfg=None, headings=(2, 3, 4)):
    """Look at the belts, because no return value can see a seam.

    Writes `_check_track.png`: the near belt at 1:1 with the phases across and
    a few headings down, then the ground run at 3x with the frame-to-frame
    difference under it - ordinary steps first and the closing step last.

    Reports `seam_over_ordinary`. A cycle that closes gives 1.0; the closing
    step being the largest step in the sequence is the failure, and it is one
    outlier rather than a deviation from an average, so it is asked about
    directly (the same reasoning as the exhaust and fire cycles).
    """
    import bpy, importlib
    atlas = importlib.import_module("sprite_atlas")
    cfg = dict(CONFIG, **(cfg or {}))
    d = cfg["output_dir"]

    def grid(name, cols, rows, tile=256):
        a = atlas.read_rgba(os.path.join(d, name))[::-1]
        return a.reshape(rows, tile, cols, tile, 4).transpose(0, 2, 1, 3, 4)

    import json

    def load(name):
        return json.load(open(os.path.join(d, "%s_atlas.json" % name)))

    def layer(name):
        m = load(name)
        return grid("%s_atlas.png" % name, m["grid"]["columns"], m["grid"]["rows"],
                    m["tile"][0]), m

    meta = load("track_left")
    ph, ang, tile = meta["phases"], meta["count"], meta["tile"][0]
    hull = layer("hull")[0].reshape(-1, tile, tile, 4)
    turr = layer("turret")[0].reshape(-1, tile, tile, 4)
    TL = grid("track_left_atlas.png", ang, ph, tile)
    TR = grid("track_right_atlas.png", ang, ph, tile)

    def over(dst, src):
        a = src[..., 3:]
        dst[..., :3] = src[..., :3] * a + dst[..., :3] * (1 - a)
        dst[..., 3:] = a + dst[..., 3:] * (1 - a)
        return dst

    def frame(head, phase, ground=0.16):
        o = np.zeros((tile, tile, 4), np.float32)
        o[..., :3] = ground
        o[..., 3] = 1.0
        over(o, hull[head])
        over(o, TL[phase, head])
        over(o, TR[phase, head])
        over(o, turr[head])
        return o

    def changed(x, y):
        cx, cy = x[..., :3] * x[..., 3:], y[..., :3] * y[..., 3:]
        return np.abs(cx - cy).max(-1)

    ordinary = [int((changed(TL[k, c], TL[k + 1, c]) > 8 / 255).sum())
                for k in range(ph - 1) for c in range(ang)]
    seam = [int((changed(TL[ph - 1, c], TL[0, c]) > 8 / 255).sum())
            for c in range(ang)]

    # the near belt, 1:1, phases across and headings down
    al = TL[0, headings[1]][..., 3] > 0.5
    ys, xs = np.nonzero(al)
    x0, x1 = max(0, int(xs.min()) - 6), min(tile, int(xs.max()) + 7)
    y0, y1 = max(0, int(ys.min()) - 6), min(tile, int(ys.max()) + 7)
    w, h, gap = x1 - x0, y1 - y0, 4
    sheet = np.zeros((len(headings) * (h + gap), ph * (w + gap), 4), np.float32)
    sheet[..., 3] = 1.0
    for r, head in enumerate(headings):
        for c in range(ph):
            sheet[r * (h + gap):r * (h + gap) + h,
                  c * (w + gap):c * (w + gap) + w] = frame(head, c)[y0:y1, x0:x1]

    path = os.path.join(d, "_check_track.png")
    img = bpy.data.images.new("_check", sheet.shape[1], sheet.shape[0], alpha=True)
    try:
        img.colorspace_settings.name = "Non-Color"
        img.alpha_mode = "CHANNEL_PACKED"
        img.pixels.foreach_set(sheet[::-1].reshape(-1).astype(np.float32))
        img.file_format = "PNG"
        img.filepath_raw = path
        img.save()
    finally:
        bpy.data.images.remove(img)

    return {
        "sheet": path,
        "phases": ph,
        "ordinary_step_changed_px": [int(np.mean(ordinary)), int(np.max(ordinary))],
        "closing_step_changed_px": [int(np.mean(seam)), int(np.max(seam))],
        "seam_over_ordinary": round(float(np.mean(seam)) / float(np.mean(ordinary)), 2),
        "seam_is_the_largest_step": bool(np.max(seam) > np.max(ordinary)),
    }


if __name__ == "__main__":
    print(json.dumps(render({"output_dir": os.path.join(
        os.path.dirname(__file__), "out", "_parts_cut")}), indent=1, default=str))
