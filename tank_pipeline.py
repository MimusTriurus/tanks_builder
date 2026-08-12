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
import mathutils
import numpy as np

REPO = r"D:\Projects\AgentCoding\BlenderMCP"

CONFIG = {
    "repo": REPO,
    "output_dir": os.path.join(REPO, "out"),

    # Scene objects, as bare part names. They resolve on either layout - to
    # themselves on the old single-mesh scenes and to `Hull.Geometry` and friends
    # on a parts-built one - so this block does not change per tank. `tank_parts`
    # owns that rule and the reasons for it; `layout` below is what differs.
    "hull": "Hull",
    "turret": "Turret",
    "barrel": "Barrel",
    "exhaust": "Engine",        # a name *prefix*: two outlets are two objects
    # None -> read it off the scene, which is the only honest source: a flag
    # could be stale and would then drive the wrong shape of tank.
    "layout": None,
    # the single-mesh layout's one root. Unused on a parts scene, which has four.
    "root": "world",

    # --- the parts layout only ----------------------------------------------
    # The belts. Phases pose them along their own loop; 0 renders them still.
    # `rebuild` names the belts to lay out afresh from one of their own links so
    # that the cycle closes by construction - on the delivered asset it does not,
    # and `track_cycle.report`'s `tread_repeat` is the number that says so.
    "track_phases": 8,
    "rebuild": ("L.Caterpillar.Geometry", "R.Caterpillar.Geometry"),
    # belt name -> a rounded-rectangle loop to lay it on instead of its own; see
    # `track_cycle.Belt.on_loop`. Empty is the delivered shape, and the shape is
    # the asset's business, so nothing here is a default anyone should inherit.
    "reshape": {},
    # the gun tube slides back into the turret when it fires, on a layer of its
    # own. The turret gives the tube up to it, so 0 is the A/B: the tube stays
    # in the turret layer and nothing else changes. See `barrel_recoil.py`.
    "recoil_phases": 5,
    # the knocked-out pose - a canted turret, a dropped gun, slack belts - as
    # three extra layers of one pose each. None is the A/B: the live set and
    # nothing else. The paint is *not* burnt here; the harness darkens it in a
    # shader, which costs no frames and is reversible. See `wreck_pose.py`.
    "wreck": {},
    "flash": "Flash",
    "smoke": "Smoke",
    "plume": "Plume",
    "fire": "Fire",
    "burn": "Burn",
    "burst": "Burst",
    "dust": "Dust",
    "scar": "Scar",
    "hex": "Hex",

    # --- the render ---------------------------------------------------------
    # Frames over the full 360, and the one number that decides how smooth the
    # tank looks turning: the atlas quantises heading, so this is the step, and
    # 24 is 15 deg against 30. Must stay a multiple of 6 so the six hex
    # directions land on rendered frames.
    #
    # It doubles the whole set, and that is not avoidable by doubling only the
    # hull and the turret: the belts, the tube, the scars and the effects are
    # welded to one or the other, and a layer stepping 30 under a hull stepping
    # 15 slides against it - worst on the scars, which are hard-edged decals on
    # armour at 60px of radius, where half a step is 8px of slide.
    #
    # Measured on MTP: 387MB of decoded atlas at 12, 774 at 24.
    "steps": 24,
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
    # The smoke column is the only layer that needs a frame of its own, and the
    # reason is height rather than reach - it is the one effect that stands
    # *above* the tank instead of beside it.
    #
    # The room a tile leaves above the anchor is `0.4645 * tile`, not
    # `tile/2 - 9`: the anchor sits at a fixed *fraction* of the frame, so the
    # shortfall grows with the tile. Getting that wrong once clipped 28px off
    # the top of an earlier, much taller version.
    #
    # Measured by projecting the mesh through the same camera basis at all
    # twelve angles rather than estimated: the column reaches 131px above the
    # anchor and 124px to the side. A plain tile leaves 119 and 128, so it does
    # not fit - and it does not *nearly* fit either, which is the useful part:
    # at 256 the column could only reach 112, three pixels above the flame,
    # with seven of frame margin. Eight pixels of measured margin has already
    # turned out to be none once. 1.25 leaves 149 and 160.
    #
    # Height is the whole cost of this layer: 320px tiles, 144 of them, a
    # 3840px atlas, about 59 MB decoded. At a hull and a half it wanted 704px
    # tiles and 285 MB. The lever is `rise` in engine_fire.SMOKE.
    #
    # 1.5 rather than 1.25 because of what sets the *pixel* scale, which is not
    # the tank's size: the tile circumscribes the turning circle, so a tank whose
    # gun does not overhang its hull fills more of its own frame. LT_PARTS'
    # barrel stops 0.04 short of the nose, so its hull renders 136px wide where
    # MT_PARTS' renders 112 - and every effect sized in hull fractions comes out
    # 21% bigger in pixels for it. At 1.25 the column wanted 342px of the 320 it
    # had and was cut with 0.180 of alpha at the edge, plainly visible on
    # `_check_fire.png`. Measured the same way as the note above: 157px above the
    # anchor and 129 to the side, against the 176 and 192 that 1.5 leaves.
    "burn_tile_scale": 1.5,
    # The hit pair is the one layer that wants *less* frame than the tank. It
    # is not welded to the model, so it renders once with no headings at all and
    # the game puts it where the hit was - which means its frame only has to
    # hold the burst itself, 56 x 46 px on MT inside 192.
    "burst_tile_scale": 0.75,
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
    # An event, like the shot, so the count is about how much of it there is to
    # watch rather than about a loop closing. Ten: four of fire and six of the
    # dust outliving it.
    "hit_phases": 10,
    "azimuth": 0.0,
    "elevation": 30.0,
    "front_dir": 270.0,

    # --- the check ----------------------------------------------------------
    # Quarters of the carousel to composite: at camera, across right, away,
    # across left. Fractions rather than frame indices, because indices are
    # relative to `steps` and silently became eighths of a turn the day it went
    # from 12 to 24 - four sheets that all still looked plausible.
    "check_headings": [0.0, 0.25, 0.5, 0.75],
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
    # Least the contact shadow may darken the tile by, in levels of 255. The
    # same measure as the plume's and a lower bar on purpose: the plume has to
    # be seen against the field, and this only has to seat the tank, so it is
    # right for it to be the quietest layer in the set. Below this the tank is
    # back to reading as a decal, which is the whole complaint it answers.
    "shadow_contrast_min": 12.0,
    # Least the flame may stay red by, in levels of 255: how much more red than
    # green-and-blue the picture becomes once the layer is added over the ground.
    # Brightness is not the thing that fails here - a fire that clips to white
    # is brighter than one that does not, and worse. See engine_fire's docstring
    # on the ramp.
    "fire_redness_min": 30.0,
    # The same pair of measures for the hit, and for the same reasons: the dust
    # must actually move the picture, the burst must stay a colour. The dust is
    # held to more than the plume because it is a small solid puff rather than a
    # thin column, so a low number here means it is not covering the burst -
    # which is what makes the burst go pale.
    "dust_contrast_min": 40.0,
    "burst_redness_min": 30.0,
    # How far the hit may land from the plate the table says it is on, as a
    # fraction of that plate's own half-extent. This is the assertion that
    # catches a transposed table: at heading 0 a transposed one still looks
    # plausible, and at every other heading it puts the hit off the tank.
    "hit_on_plate": 1.0,
    # The mark left behind, on its own tile - it lies on the armour and needs
    # no more frame than the tank does.
    "scar_tile_scale": 1.0,
    # Least a level of damage may be worse than the one before, as a ratio of
    # how much of the picture each changes. What this is for is catching three
    # renders of one drawing, and those score exactly 1.00; the bar is low
    # because how far above 1 a real step lands depends on the plate. On the
    # front glacis - the darkest armour on the tank - a scuff that bares metal
    # is already most of the change a hole makes, and the step is 1.16; on the
    # rear plate the same three levels step 1.80 and 1.71.
    "scar_step_ratio": 1.15,
    # Most of a mark that may lap off the tank's silhouette. Not zero, and the
    # reason is geometric: the mark is centred on the plate's measured
    # centroid, and a plate whose centroid sits within a mark's radius of the
    # hull's lower edge - MT's rear plate does - laps over it by a pixel or two
    # at oblique headings. That is twelve pixels. The version that was a
    # quarter off the tank was a mark too big for its plate, and this catches
    # that with room to spare.
    "scar_off_armour_max": 0.06,
    # Most of a plate's half-extent the worst mark may take. Over 1 it hangs
    # off the armour it is supposed to be on.
    "scar_fit_max": 0.85,
    # Least even the lightest mark may move the armour by, in levels of 255.
    # Measured against the paint, not against the ground - see the check. Not
    # much higher than this is available: the paint is 44 to 68 levels off
    # black, so soot alone tops out around forty however black it is, and
    # anything above that is metal being bared.
    "scar_contrast_min": 45.0,
}

# Also the order they are composited in, which for the burning pair is not a
# preference: the smoke goes down with normal alpha and the fire is added on
# top of it. See engine_fire.SMOKE. The hit pair is the same shape of rule and
# goes last, being the most recent thing to have happened.
#
# The scars go directly over the armour and under everything else, because they
# are *on* the tank rather than in front of it: a plume drifting across a holed
# plate passes over the hole.
SCAR_FACES = ("front", "rear", "left", "right")
SCAR_LAYERS = tuple("scar_" + f for f in SCAR_FACES)
TRACK_LAYERS = ("track_left", "track_right")
# The knocked-out pose. Only the layers whose *geometry* changes: the wreck's
# hull, shadow and scars are the live ones to the pixel, because a dead tank's
# hull, its zenith footprint and its armour plates have not moved. The turret
# stays a layer of its own for the reason in wreck_pose.py - a tank dies with its
# gun at whatever bearing it had, and one baked sprite would snap it.
WRECK_LAYERS = ("wreck_turret", "wreck_track_left", "wreck_track_right")
# Layers the turret is *not* held out of, because it cannot be: they are indexed
# by the hull's heading and the turret turns against the hull, so a turret baked
# into their alpha is baked at one relative bearing out of twenty-four. Traverse
# the gun and the bite stays where the turret used to be - a hole in the flame
# with nothing on screen to account for it, and flame lying over the turret
# where it now is. No amount of rendering fixes that; the frame index has no
# axis for it. So the turret is drawn whole and these are ordered against it,
# per heading, by `draw_order`. See `OVER_TURRET` there for what it costs.
ORDERED_LAYERS = ("exhaust", "fire", "burn") + SCAR_LAYERS
# The belts go straight after the hull and before everything that happens *to*
# the tank, because they are not an effect on it - they are the part of it that
# the hull layer cut away with a holdout. Absent on a single-mesh scene, and
# `_atlases` skips whatever has no file, so one order serves both layouts.
# The gun tube sits with the turret and for the same reason as the belts: it is
# part of the tank that a layer cut away, not an effect on it. Straight after the
# turret it came out of, and still ahead of the scars, which are *on* the armour.
# The wreck sits next to the live layers it stands in for, and never beside them:
# nothing composites a canted turret over a seated one. It is in this list only so
# that `_atlases` loads it, and every sheet names the layers it draws.
# The scars moved ahead of the turret with the holdout change: they are paint on
# the hull and the turret stands over them at every heading (measured - all four
# plates, all twenty-four), so what used to be cut out of them is now simply
# drawn on top of them.
LAYER_ORDER = (("hex", "shadow", "hull") + TRACK_LAYERS + SCAR_LAYERS
               + ("turret", "barrel") + WRECK_LAYERS
               + ("exhaust", "burn", "fire", "smoke", "flash", "dust", "burst"))


def _load(cfg, name):
    spec = importlib.util.spec_from_file_location(
        name, os.path.join(cfg["repo"], "%s.py" % name))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def parts(cfg=None):
    """Which pieces this scene has, and so which layers it gets.

    Also which shape the scene is, because that decides what the tank's own
    layers are: two on a single-mesh scene, four on a parts-built one. Read from
    the scene, never configured - see `tank_parts.layout`.
    """
    cfg = dict(CONFIG, **(cfg or {}))
    scene = bpy.context.scene
    tp = _load(cfg, "tank_parts")
    kind = cfg["layout"] or tp.layout(scene)
    named = tp.describe(cfg, scene)
    exhaust = tp.ports(cfg["exhaust"], scene)
    # The hit needs no hand split at all - `hit_point` fires rays at the hull
    # and finds the plates - so every scene with a hull gets it. That is why the
    # guard below is about the two effects that *do* need a split.
    return {"layout": kind,
            "flash": named["barrel_mesh"] is not None,
            "exhaust": bool(exhaust),
            "hit": tp.mesh(cfg["hull"], scene, required=False) is not None,
            "exhaust_objects": [o.name for o in exhaust],
            "named": named}


def _names(cfg, have):
    """The concrete object names every stage below hands to the modules.

    Two kinds, and conflating them is what broke on the parts layout. A **mesh**
    is what gets measured - rays, vertex clouds, bore axes. A **root** is what a
    layer renders and what parenting is judged against; on a single-mesh scene a
    part's root and its mesh are the same object, which is why nothing noticed
    until there was a scene where they are not.

    Holdouts and excludes are a third thing and appear here untouched:
    `sprite_atlas.by_hints` matches by substring and pulls in descendants, so the
    bare "Hull" already means hull-plus-engine on both layouts.
    """
    named = have["named"]
    return {
        "hull_mesh": named["hull_mesh"],
        "turret_mesh": named["turret_mesh"],
        "barrel_mesh": named["barrel_mesh"],
        "hull_root": named["hull_root"],
        "turret_root": named["turret_root"],
        "hull_hint": cfg["hull"],
        "turret_hint": cfg["turret"],
    }


# ---------------------------------------------------------------------------
# stages
# ---------------------------------------------------------------------------

def prepare(cfg=None):
    """Measure what is there, build the tile and the effects. Idempotent."""
    cfg = dict(CONFIG, **(cfg or {}))
    scene = bpy.context.scene
    have = parts(cfg)

    if not have["hit"]:
        raise RuntimeError("no object named %r - there is no tank here"
                           % cfg["hull"])
    if not have["flash"] and not have["exhaust"]:
        print("[tank] no %r and nothing starting with %r: this scene gets the "
              "hit layers only. Separate a piece by hand for the rest - select "
              "it, Ctrl+L to complete the panels it cuts through, P > "
              "Selection, rename it, then Ctrl+P > Object (Keep Transform) "
              "onto %s for the gun or %s for the exhaust. See MUZZLE_FLASH.md."
              % (cfg["barrel"], cfg["exhaust"], cfg["turret"], cfg["hull"]))

    # the transient objects are rebuilt every run, so a re-run never leaves
    # Hex.001 behind to be rendered instead of Hex
    for name in (cfg["hex"], cfg["flash"], cfg["smoke"], cfg["plume"],
                 cfg["fire"], cfg["burn"], cfg["burst"], cfg["dust"],
                 cfg["scar"]):
        ob = scene.objects.get(name)
        if ob is not None:
            data = ob.data
            bpy.data.objects.remove(ob)
            bpy.data.meshes.remove(data)

    name = _names(cfg, have)
    hull, turret = name["hull_mesh"], name["turret_mesh"]
    out = {"have": have, "names": name, "layout": have["layout"],
           "axis": None, "muzzle": None, "exhaust": None, "hits": None}

    # The ring axis has to exist before anything below it: the flash checks its
    # bearing against it and the burst is built on it. On the old scenes it is
    # already stamped, by hand, possibly tuned - so measure only when there is
    # nothing there at all, and say which it was. Silently re-stamping would
    # overwrite a hand-set axis, and a wrong axis is the one error that shows up
    # in no output number.
    axis_mod = _load(cfg, "turret_axis")
    held = [n for n in (hull, turret) if "ring_axis" in scene.objects[n].keys()]
    if held:
        out["axis"] = {"from": "already stamped on %s" % ", ".join(held),
                       "ring_axis": [float(v) for v in
                                     scene.objects[held[0]]["ring_axis"]]}
    else:
        # stamp but do not apply: these scenes are placed by hand and moving a
        # turret's origin is not this stage's business
        report = axis_mod.set_axis(dict(axis_mod.CONFIG, turret=turret,
                                        hull=hull, apply=False, stamp=True))
        out["axis"] = {"from": "measured here", "ring_axis": report.get("axis"),
                       "roundness": report.get("roundness"),
                       "wobble_px": report.get("wobble_px_before"),
                       "warnings": report.get("warnings")}

    out["hits"] = _load(cfg, "hit_point").set_hits(
        {"hull": hull, "turret": turret, "hull_root": name["hull_root"],
         "turret_root": name["turret_root"],
         "front_dir": cfg["front_dir"], "azimuth": cfg["azimuth"],
         "elevation": cfg["elevation"], "camera_elevation": cfg["elevation"],
         "stamp_on": [hull]})
    if have["flash"]:
        out["muzzle"] = _load(cfg, "muzzle_point").set_muzzle(
            {"turret": turret, "barrel": name["barrel_mesh"],
             "turret_root": name["turret_root"],
             "stamp_on": [turret, name["barrel_mesh"]]})
    if have["exhaust"]:
        out["exhaust"] = _load(cfg, "exhaust_point").set_exhaust(
            {"prefix": cfg["exhaust"], "hull": hull, "turret": turret,
             "hull_root": name["hull_root"],
             "turret_root": name["turret_root"], "stamp_on": [hull]})

    # The tile. On a parts-built scene it is `parts_render`'s, because
    # `hex_base.make_hex` walks one root and that scene has four - and it is
    # built inside the render there, so there is nothing to do here.
    if have["layout"] == "parts":
        out["hex"] = {"from": "parts_render.ground_tile, at render time"}
    else:
        hex_mod = _load(cfg, "hex_base")
        out["hex"] = hex_mod.make_hex(dict(
            hex_mod.CONFIG,
            target=cfg["root"], name=cfg["hex"],
            # the tank in these scenes is already placed and must not be moved;
            # ground_z None then stands the tile under its tracks
            move_target=False, center_on_origin=False, ground_z=None))

    if have["flash"]:
        flash_mod = _load(cfg, "muzzle_flash")
        flash_mod.build({"turret": turret, "name": cfg["flash"]})
        flash_mod.build_smoke({"turret": turret, "name": cfg["smoke"]})
    if have["exhaust"]:
        _load(cfg, "exhaust_plume").build({"hull": hull, "name": cfg["plume"]})
        fire_mod = _load(cfg, "engine_fire")
        fire_mod.build({"hull": hull, "name": cfg["fire"]})
        fire_mod.build_smoke({"hull": hull, "name": cfg["burn"]})

    burst_mod = _load(cfg, "hit_burst")
    seat = {"hull": hull, "turret": turret, "hull_root": name["hull_root"],
            "turret_root": name["turret_root"],
            "azimuth": cfg["azimuth"], "elevation": cfg["elevation"]}
    burst_mod.build(dict(seat, name=cfg["burst"]))
    burst_mod.build_dust(dict(seat, name=cfg["dust"]))

    scar_mod = _load(cfg, "hit_scar")
    # `hull_root` for the seat probe, and it is the same argument the hit rays
    # make: on a parts scene the plate is assembled out of the hull mesh *and*
    # its siblings, so a probe down one mesh reads the plate as clear where a
    # sibling stands in front of it. See hit_scar.seat.
    scar_seat = {"hull": hull, "hull_root": name["hull_root"],
                 "name": cfg["scar"]}
    scar_mod.build(scar_seat)
    out["scar"] = {"faces": scar_mod.faces({"hull": hull}),
                   "levels": len(scar_mod.LEVELS),
                   "fit": scar_mod.fits({"hull": hull}),
                   # what the decal had to climb to clear its plate. Reported
                   # because a seat that grew is a plate that is not flat, and
                   # nothing else in the output would say so.
                   "seats": scar_mod.seats(scar_seat)}
    return out


def _body_layers(cfg, have):
    """The layers that are the tank itself, and how to put the scene back.

    Returns `(layers, body)`, where `body` is `parts_render.Body` on a parts
    scene and None on a single-mesh one. It owns transient scene state - the
    ring-cut box, the tile, and belt vertices that are *written* - so the caller
    must call `body.restore()` in a `finally`.

    The single-mesh layout keeps its `exclude` list and the parts layout has
    none, and that difference is the layouts in one line: there the hull layer is
    `world`, meaning the whole tank, so everything that is not hull has to be
    named and taken out; here it is `Hull.World`, which contains the hull and
    nothing else. Every effect object sits at the scene root either way, which is
    why they are excluded there and silent here.
    """
    if have["layout"] == "parts":
        pr = _load(cfg, "parts_render")
        body = pr.Body(dict(pr.CONFIG,
                            output_dir=cfg["output_dir"], steps=cfg["steps"],
                            tile=cfg["tile"], azimuth=cfg["azimuth"],
                            elevation=cfg["elevation"],
                            front_dir=cfg["front_dir"],
                            track_phases=cfg["track_phases"],
                            rebuild=cfg["rebuild"],
                            reshape=cfg["reshape"],
                            recoil_phases=cfg["recoil_phases"],
                            wreck=cfg["wreck"]))
        return list(body.layers), body

    return [
        # the turret hint excludes descendants, which is what keeps the barrel
        # out of the hull layer. The plume and the flame sit at scene root and
        # so are inside `world` too - they are named here for the same reason.
        {"name": "hull", "target": cfg["root"],
         "exclude": [cfg["turret"], cfg["plume"], cfg["fire"], cfg["burn"],
                     cfg["burst"], cfg["dust"], cfg["scar"]]},
        {"name": "turret", "target": cfg["turret"]},
        {"name": "hex", "target": cfg["hex"], "static": True},
    ], None


def render(cfg=None):
    """Render every layer this scene has as one framed set."""
    cfg = dict(CONFIG, **(cfg or {}))
    atlas = _load(cfg, "sprite_atlas")
    have = parts(cfg)
    named = _names(cfg, have)
    hull, turret = named["hull_mesh"], named["turret_mesh"]
    layers, body = _body_layers(cfg, have)

    # Holdouts stay spelled bare. `by_hints` matches by substring and pulls in
    # descendants, so "Hull" is hull-plus-engine and "Turret" is turret-plus-gun
    # on both layouts, while the phase hooks below want the one mesh that carries
    # the stamps. Two roles, two spellings - see `_names`.
    blockers = [cfg["hull"], cfg["turret"]]
    # And the same list without the turret, for the layers that are ordered
    # against it instead. The hull stays in both: it does not turn relative to
    # anything indexed by its own heading, so holding it out is exact at every
    # frame - which is the property the turret lost the moment it could traverse.
    #
    # The muzzle flash and its smoke keep the full list, and that is not an
    # oversight either: they are indexed by the *turret's* heading, so the turret
    # is the part that is exact for them and the hull is the one baked at one
    # relative bearing. Same disease, other organ, and it wants its own
    # measurement rather than this one applied by symmetry.
    hull_only = [cfg["hull"]]

    if have["flash"]:
        flash_mod = _load(cfg, "muzzle_flash")
        layers.extend([
            {"name": "flash", "target": cfg["flash"],
             # never in the fit, or a transient effect resizes every layer
             "fit": False,
             "tile_scale": cfg["effect_tile_scale"],
             "phases": cfg["phases"],
             "phase_hook": flash_mod.phase_hook({"turret": turret,
                                                 "name": cfg["flash"]}),
             # the tank blocks without being drawn: fired away from the camera
             # the flash is behind the turret, and a flat layer has no depth
             "holdout": blockers},
            # smoke is its own layer because it occludes where the flash emits.
            # One layer cannot be both: fire adds light to what is behind it,
            # smoke takes it away, and the game blends them differently.
            {"name": "smoke", "target": cfg["smoke"], "fit": False,
             "tile_scale": cfg["effect_tile_scale"],
             "phases": cfg["phases"],
             "phase_hook": flash_mod.smoke_phase_hook(
                 {"turret": turret, "name": cfg["smoke"]}),
             "holdout": blockers},
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
             "phase_hook": plume_mod.phase_hook({"hull": hull,
                                                 "name": cfg["plume"]}),
             "holdout": hull_only})

        fire_mod = _load(cfg, "engine_fire")
        layers.append(
            # Same ports as the plume, same holdout argument, same plain tile.
            # What it needs room for is height rather than reach, and the tile
            # already has more of that than the muzzle left over - see
            # fire_tile_scale.
            {"name": "fire", "target": cfg["fire"], "fit": False,
             "tile_scale": cfg["fire_tile_scale"],
             "phases": cfg["fire_phases"],
             "phase_hook": fire_mod.phase_hook({"hull": hull,
                                                "name": cfg["fire"]}),
             "holdout": hull_only})
        layers.append(
            # The other half of the fire. Same holdout, same phase count, drawn
            # *under* the flame. Its frame is its own config rather than the
            # tank's tile because height is what this layer costs - see
            # burn_tile_scale.
            {"name": "burn", "target": cfg["burn"], "fit": False,
             "tile_scale": cfg["burn_tile_scale"],
             "phases": cfg["fire_phases"],
             "phase_hook": fire_mod.smoke_phase_hook({"hull": hull,
                                                      "name": cfg["burn"]}),
             "holdout": hull_only})

    burst_mod = _load(cfg, "hit_burst")
    seat = {"hull": hull, "turret": turret, "hull_root": named["hull_root"],
            "turret_root": named["turret_root"],
            "azimuth": cfg["azimuth"], "elevation": cfg["elevation"]}
    for layer_name, target, hook in (
            ("dust", cfg["dust"],
             burst_mod.dust_phase_hook(dict(seat, name=cfg["dust"]))),
            ("burst", cfg["burst"],
             burst_mod.phase_hook(dict(seat, name=cfg["burst"])))):
        layers.append(
            # `static`, so one frame rather than twelve: a hit is not welded to
            # the tank and a ball of light looks the same from every azimuth.
            # Still fitted over the whole carousel, so it keeps the job's
            # units_per_pixel and is the right size.
            #
            # And deliberately *no holdout*. Every other effect bakes the tank's
            # occlusion into its alpha, which it can do because it is rendered
            # at the heading it will be drawn at. This one is rendered once and
            # used at all twelve, so a holdout would burn heading zero's
            # silhouette into every hit. The harness occludes instead, by
            # drawing the layer behind the tank when the plate faces away -
            # which the stamped `facing` says, and which agreed with the hull on
            # all sixteen heading-face pairs tested.
            {"name": layer_name, "target": target, "static": True, "fit": False,
             "tile_scale": cfg["burst_tile_scale"],
             "phases": cfg["hit_phases"], "phase_hook": hook})

    scar_mod = _load(cfg, "hit_scar")
    # Measured here, with the scene at rest, and handed to the hooks as numbers.
    # A hook runs inside the job, where the holdout objects are still spun to the
    # previous phase's last angle, so a hook that probed the scene itself would
    # measure the plate against a tank standing at another heading - see
    # `hit_scar.set_state`.
    scar_cfg = {"hull": hull, "hull_root": named["hull_root"],
                "name": cfg["scar"]}
    scar_seats = {f: v["seat"] for f, v in scar_mod.seats(scar_cfg).items()
                  if isinstance(v, dict)}
    for face in scar_mod.faces({"hull": hull}):
        layers.append(
            # The opposite of the pair above in every way that matters, and the
            # comparison is the point. A hole is flat, stuck to one plate and
            # turns with the hull, so it is rendered at every heading like the
            # armour it is on - and because it is, the tank can hold it out.
            # That does the whole occlusion job here: when the plate faces away
            # the hull stands in front of the decal and the frame comes out
            # empty by itself, with no front-or-behind rule in the game at all.
            #
            # `phases` is damage, not time. There is no cycle to close and no
            # hold table to run; the check asserts an ordering instead.
            {"name": "scar_" + face, "target": cfg["scar"], "fit": False,
             "tile_scale": cfg["scar_tile_scale"],
             "phases": len(scar_mod.LEVELS),
             "phase_hook": scar_mod.phase_hook(face, dict(scar_cfg,
                                                          seats=scar_seats)),
             "holdout": hull_only})

    # ---- which side of the turret each ordered layer is on -------------------
    #
    # Measured here, on the resting scene, and stamped into the layer's own
    # metadata for the harness to order by. Before the job for the plain reason
    # that it has nothing to do with the camera fit - unlike the plate table,
    # which is in pixels and so has to wait for it - and because the phase hooks
    # have not run yet, so every effect is standing in the pose it was built in.
    #
    # One flag per layer rather than one for the group: fire and exhaust agreed
    # on all 24 headings, and the burning column did not, being twice as tall it
    # overlaps the turret on two more headings at each end. A shared flag would
    # be wrong on four frames of seventy-two.
    order = _load(cfg, "draw_order")
    cam = atlas.camera_matrix(mathutils.Vector((0.0, 0.0, 0.0)), cfg["azimuth"],
                              cfg["elevation"], 10.0)
    to_cam = cam.translation.normalized()
    angles = atlas.frame_angles({"steps": cfg["steps"], "start_angle": 0.0,
                                 "direction": "ccw"})
    for layer in layers:
        if layer["name"] not in ORDERED_LAYERS:
            continue
        flags, margins = order.over_turret(layer["target"], turret,
                                           None, to_cam, angles)
        extra = dict(layer.get("meta_extra") or {})
        extra.update({"over_turret": flags, "over_turret_margin": margins})
        layer["meta_extra"] = extra

    # The wreck goes last in the *job*, which is not the same statement as the
    # one `Body` already makes. It poses the turret's children, and nothing puts
    # them back until `body.restore()` in the `finally` below - the renderer
    # writes and restores a layer *root*, and the pose is deliberately not on
    # one, because the root is what the carousel turns. `Body` puts these last
    # in its own list, which is exactly enough for the live turret layer it was
    # worried about and blind to every layer appended here.
    #
    # Measured, because the failure is silent and reads as an effect bug: with
    # the wreck in the middle, the flash, its smoke, the exhaust, the fire, its
    # column, the burst, the dust and all four scars rendered against a turret
    # canted 24 deg and dropped into its ring. The turret is their holdout, so
    # what the wrong occluder failed to cut is what shows through the *upright*
    # turret the game draws. One heading of one tank: 10 px of flame over the
    # turret rendered alone, 444 px rendered in this job.
    #
    # Stable, so the three keep their order among themselves, and they still
    # follow the live turret and barrel layers - which is what `Body` wanted.
    layers.sort(key=lambda layer: layer["name"].startswith("wreck_"))

    shared = {
        "output_dir": cfg["output_dir"],
        "steps": cfg["steps"], "tile": cfg["tile"],
        "azimuth": cfg["azimuth"], "elevation": cfg["elevation"],
        "hex_labels": {"front_dir": cfg["front_dir"], "orientation": "flat"},
    }
    if body is not None:
        shared.update(body.shared())

    try:
        res = atlas.render_set({"shared": shared, "layers": layers})
    finally:
        # the belts are posed by writing vertex coordinates and the tile and the
        # ring-cut box are transient objects, so the scene goes back whatever
        # happened
        if body is not None:
            body.restore()

    if body is not None:
        body.verify(res)
        res["body"] = body.report(res)
    res["layout"] = have["layout"]
    return res


def stamp_table(cfg=None):
    """Write the plates, projected, into the hit layers' metadata.

    After the render rather than through `meta_extra`, and that is forced: the
    table is in pixels, and `units_per_pixel` is a *result* of fitting the
    camera to the carousel. Anything computed before the job would be a second
    guess at a number the job is about to decide.

    It goes into the hit layers' own JSON rather than the hull's, because it is
    those layers that get placed by it, and offsets are measured from the burst
    layer's build origin rather than from any frame's anchor. Every value here
    is regenerated by the render that produced it, so the harness cannot read a
    table describing a tank that has since moved.
    """
    cfg = dict(CONFIG, **(cfg or {}))
    hp = _load(cfg, "hit_point")
    burst_mod = _load(cfg, "hit_burst")
    named = _names(cfg, parts(cfg))
    hull = named["hull_mesh"]

    hull_path = os.path.join(cfg["output_dir"], "hull_atlas.json")
    with open(hull_path, encoding="utf-8") as fh:
        hull_meta = json.load(fh)
    angles = [f["angle"] for f in hull_meta["frames"][:hull_meta["count"]]]

    origin = burst_mod.origin_of(
        {"hull": hull, "turret": named["turret_mesh"],
         "hull_root": named["hull_root"], "turret_root": named["turret_root"],
         "name": cfg["burst"]})
    written = []
    for name in ("burst", "dust"):
        path = os.path.join(cfg["output_dir"], "%s_atlas.json" % name)
        if not os.path.exists(path):
            continue
        with open(path, encoding="utf-8") as fh:
            meta = json.load(fh)
        meta["hits"] = {
            "origin": [round(float(v), 6) for v in origin],
            "faces": hp.project_plates(
                origin, meta["units_per_pixel"], angles,
                {"hull": hull, "azimuth": cfg["azimuth"],
                 "elevation": cfg["elevation"]}),
        }
        with open(path, "w", encoding="utf-8") as fh:
            json.dump(meta, fh, indent=2)
        written.append(os.path.basename(path))
    return {"stamped": written,
            "faces": sorted(hp.read_plates(
                bpy.context.scene.objects[hull])[0])}


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


def _body(layers, heading, phase=0):
    """The tank itself at `heading`, in composite order.

    Present so that the belts appear on every check sheet without each draw
    function having to know whether this scene has them. A tank drawn without
    its tracks looks like it is hovering, and the sheets are what gets looked
    at - a layer missing from them is a layer nobody checks.

    The belts take phase 0 unless asked otherwise: their cycle is independent of
    whatever the sheet is stepping through, so pinning them keeps the sheet
    about its own subject.

    The gun tube is the same case as the belts and arrived for the same reason:
    it left the turret layer so that it could recoil, so every sheet that draws a
    turret has to draw the tube back onto it or the tank has no gun. At rest,
    because a sheet about smoke has nothing to say about recoil.
    """
    out = [("hull", heading)]
    for name in TRACK_LAYERS:
        if name in layers:
            out.append((name, phase * layers[name][1]["count"] + heading))
    out.append(("turret", heading))
    if "barrel" in layers:
        out.append(("barrel", heading))          # phase 0 - the tube at rest
    return out


def _ground(layers, heading):
    """The tile and what the tank puts on it, in composite order.

    Its own function rather than a `("hex", 0)` in front of every sheet because
    the shadow is the second thing on the ground and there will not be a third
    without this being the place it goes. It is *not* part of `_body`: the tank
    is what turns and what a hit has to land on, and the shadow is neither - it
    is the tile, darker.
    """
    out = [("hex", 0)]
    if "shadow" in layers:
        out.append(("shadow", heading))
    return out


def _body_used(*names):
    """Layer names for `_sheet`'s `used`, with the belts always allowed in."""
    return (["hex", "shadow", "hull"] + list(TRACK_LAYERS)
            + ["turret", "barrel"] + list(names))


def _tank_alpha(layers, heading, phase=0):
    """Alpha of the whole tank at `heading` - every layer that *is* the tank.

    Two tests ask "is this pixel on the tank": whether a hit lands on armour and
    whether a scar hangs off it. On a parts scene the hull layer has a hole in it
    where the tracks were held out, so asking hull-or-turret would call the
    track band empty and fail a hit low on a side plate that is perfectly on the
    armour. The belts are part of the tank, not an effect on it.

    The gun tube joins them for the same reason: once it is its own layer, the
    turret's alpha stops at the mantlet, and a mask that ended there would call
    the tube empty space.
    """
    out = _tile_of(layers, "hull", heading)[:, :, 3].copy()
    for name in ("turret", "barrel") + TRACK_LAYERS:
        if name not in layers:
            continue
        index = heading
        if layers[name][1].get("phases", 1) > 1:
            index = phase * layers[name][1]["count"] + heading
        out = np.maximum(out, _tile_of(layers, name, index)[:, :, 3])
    return out


def _tile_of(layers, name, index):
    """One frame, pasted back into a tile-sized buffer.

    The single place that knows how an atlas is laid out, which is why trimming
    the frames cost the check sheets one function and left all ten callers
    alone. A packed frame is fetched by its own rectangle and put back at the
    offset it was trimmed from; an atlas without rectangles is still a grid.

    Bottom-up, because that is what `read_rgba` hands back and what every caller
    here already composites in. `atlas_pack` works top-down, so the flip happens
    on both sides of it and nowhere else.
    """
    pix, meta = layers[name]
    size = meta["tile"][0]
    frame = meta["frames"][index]
    if "rect" in frame:
        tile = np.zeros((size, size, 4), dtype=pix.dtype)
        rect = frame["rect"]
        if rect is not None:
            x, y, w, h = rect
            ox, oy = frame["off"]
            top = pix[::-1]
            tile[oy:oy + h, ox:ox + w] = top[y:y + h, x:x + w]
        return tile[::-1]
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


def _span_px(layers, name, floor=0.02):
    """Widest and tallest the layer ever gets, over every frame."""
    _, meta = layers[name]
    w = h = 0
    for frame in meta["frames"]:
        a = _tile_of(layers, name, frame["index"])[:, :, 3]
        ys, xs = np.nonzero(a > floor)
        if not len(xs):
            continue
        w = max(w, int(xs.max() - xs.min() + 1))
        h = max(h, int(ys.max() - ys.min() + 1))
    return [w, h]


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

    def place(buf, layer, index, blend, offset=(0.0, 0.0)):
        src = _tile_of(layers, layer, index)
        anchor = np.array(layers[layer][1]["anchor_px"])
        # tile pixels are top-down, the arrays are bottom-up: x shifts as it
        # reads, y shifts the other way. `offset` is in screen pixels - the hit
        # layers are the only ones that use it, and they are drawn where the
        # plate is rather than at the anchor
        dx = int(round(at[0] - anchor[0] + offset[0]))
        dy = int(round((size - at[1]) - (src.shape[0] - anchor[1]) - offset[1]))
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

    headings = [int(round(f * meta["count"])) % meta["count"]
                for f in cfg["check_headings"]]

    # --- the shot: phase across, heading down --------------------------------
    if "flash" in layers:
        def draw_shot(buf, place, heading, phase):
            frame = phase * meta["count"] + heading
            for name, index in (_ground(layers, heading) + _body(layers, heading)
                                + [("smoke", frame)]):
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
            draw_shot, 0.16, _body_used("exhaust", "smoke", "flash"))

    # --- the plume: one full lap across --------------------------------------
    if "exhaust" in layers:
        plume_meta = layers["exhaust"][1]

        def draw_plume(buf, place, heading, phase):
            for name, index in _ground(layers, heading) + _body(layers, heading):
                buf = place(buf, name, index, _over)
            return place(buf, "exhaust",
                         phase * plume_meta["count"] + heading, _over)

        report["exhaust_composite"] = _sheet(
            cfg, layers, "_check_exhaust", headings,
            list(range(plume_meta["phases"])), draw_plume,
            _ground_colour(layers), _body_used("exhaust"))

    # --- the burning tank: one full lap across -------------------------------
    if "fire" in layers:
        fire_meta = layers["fire"][1]

        def draw_fire(buf, place, heading, phase):
            for name, index in _ground(layers, heading) + _body(layers, heading):
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
            _ground_colour(layers), _body_used("burn", "fire"))

    # --- the hit: face across, heading down ----------------------------------
    if "burst" in layers:
        burst_meta = layers["burst"][1]
        table = (burst_meta.get("hits") or {}).get("faces") or {}
        faces = [f for f in ("front", "rear", "left", "right") if f in table]
        hit_phase = min(1, burst_meta["phases"] - 1)

        def draw_hit(buf, place, heading, face):
            row = table[face]["frames"][heading]

            def hit(b):
                # dust down with normal alpha, burst added over it - the same
                # order and the same reason as the burning pair
                b = place(b, "dust", hit_phase, _over, row["offset"])
                return place(b, "burst", hit_phase, _add, row["offset"])

            # A plate turned away from the camera is behind the tank, and the
            # harness draws it there. Compositing it on top instead would put a
            # hit on the far side over the turret roof, which is the picture
            # this sheet exists to rule out rather than to produce.
            for name, index in _ground(layers, heading):
                buf = place(buf, name, index, _over)
            if row["facing"] <= 0.0:
                buf = hit(buf)
            for name, index in _body(layers, heading):
                buf = place(buf, name, index, _over)
            return buf if row["facing"] <= 0.0 else hit(buf)

        if faces:
            report["hit_composite"] = _sheet(
                cfg, layers, "_check_hit", headings, faces, draw_hit,
                _ground_colour(layers), _body_used("dust", "burst"))

        # Non-circular version of "the hit lands on its plate". The table says
        # where the plate is, so asking the table again proves nothing; ask the
        # *tank* instead. Every face the camera can see has to put its hit
        # inside the tank's own silhouette, at every heading. A transposed table
        # looks right at heading zero and puts the hit off the tank everywhere
        # else, which is exactly the failure that survives a spot check.
        anchor, tile = meta["anchor_px"], meta["tile"][0]
        off_tank = []
        for face in faces:
            for h in range(meta["count"]):
                row = table[face]["frames"][h]
                if row["facing"] <= 0.25:
                    continue
                x = int(round(anchor[0] + row["offset"][0]))
                y = int(round(anchor[1] + row["offset"][1]))
                if not (0 <= x < tile and 0 <= y < tile):
                    off_tank.append([face, h, "off frame"])
                    continue
                seen = float(_tank_alpha(layers, h)[tile - 1 - y, x])
                if seen < 0.5:
                    off_tank.append([face, h, round(seen, 3)])
        report["hit_off_tank"] = off_tank
        if off_tank:
            report["problems"].append(
                "%d of the hits the camera can see land off the tank (%s) - "
                "the plate table and the atlas disagree about where the tank is"
                % (len(off_tank), ", ".join("%s@%d" % (f, h)
                                            for f, h, _ in off_tank[:6])))

        # The same pair of colour measures as the fire, for the same reasons.
        # The dust one is not decoration: the burst is additive, so how dark the
        # dust is under it decides how much colour survives, and a burst that
        # looks pale has twice now turned out to be a dust cloud with holes.
        ground = _ground_colour(layers)
        ground_red = float(ground[0] - 0.5 * (ground[1] + ground[2]))
        redness, white, contrast = [], 0.0, 0.0
        for p in range(burst_meta["phases"]):
            b = _tile_of(layers, "burst", p)
            field = np.zeros_like(b)
            field[:, :, :3] = ground
            field[:, :, 3] = 1.0
            under = _over(field, _tile_of(layers, "dust", p)) \
                if "dust" in layers else field
            lit = _add(under, b)
            body = b[:, :, 3] > 0.25
            if body.any():
                rgb = lit[:, :, :3][body]
                redness.append(float(
                    np.median(rgb[:, 0] - 0.5 * (rgb[:, 1] + rgb[:, 2]))
                    - ground_red))
                white = max(white, float(np.mean(rgb.min(axis=1) > 0.97)))
            contrast = max(contrast,
                           float(np.abs(under[:, :, :3] - ground).max() * 255.0))
        report["burst_redness"] = round(255.0 * max(redness or [0.0]), 1)
        report["burst_white_fraction"] = round(white, 4)
        report["dust_contrast"] = round(contrast, 1)
        report["burst_span_px"] = _span_px(layers, "burst")
        report["dust_span_px"] = _span_px(layers, "dust")
        for name in ("burst", "dust"):
            worst = _edge_alpha(layers, name)
            report["%s_edge_alpha" % name] = round(worst, 4)
            if worst > 0.02:
                report["problems"].append(
                    "the hit's %s touches its tile edge (alpha %.3f) - raise "
                    "burst_tile_scale or shrink it in hit_burst" % (name, worst))
        if report["burst_redness"] < cfg["burst_redness_min"]:
            report["problems"].append(
                "the burst is only %.0f levels of 255 redder than the ground - "
                "on a pale field an additive layer with nothing dark under it "
                "is that field, faintly tinted. The lever is hit_burst.DUST, "
                "not the ramp" % report["burst_redness"])
        if report["dust_contrast"] < cfg["dust_contrast_min"]:
            report["problems"].append(
                "the hit's dust moves the picture by only %.0f levels of 255 - "
                "it is in the alpha and not on the screen, and the burst that "
                "sits on it will read pale" % report["dust_contrast"])
        if white > 0.25:
            report["problems"].append(
                "%.0f%% of the burst clips to white - it is not brighter, it is "
                "colourless" % (100.0 * white))

    # --- the mark left behind: on the armour, and only where the armour is ---
    scar_layers = [n for n in SCAR_LAYERS if n in layers]
    if scar_layers:
        levels = layers[scar_layers[0]][1]["phases"]
        tile = meta["tile"][0]

        # How much of each plate's mark the camera gets at each heading, and so
        # which heading shows it best. Everything below is measured there, since
        # a plate half turned away is a legitimate two pixels of mark and would
        # fail any threshold worth setting.
        cover = {}
        for name in scar_layers:
            m = layers[name][1]
            cover[name] = [
                int((_tile_of(layers, name,
                              (levels - 1) * m["count"] + h)[:, :, 3] > 0.25).sum())
                for h in range(m["count"])]
        best = {n: int(np.argmax(c)) for n, c in cover.items()}
        report["scar_best_heading"] = best
        report["scar_cover_px"] = {n: max(c) for n, c in cover.items()}

        # The assertion the holdout exists for. A plate turned away from the
        # camera has the hull standing in front of it, so its frame has to come
        # out *empty* - that is what lets the game draw this layer flat with no
        # front-or-behind rule of its own. A mark visible from every heading
        # means the holdout did not take, and it would show as a hole in the
        # near side of the tank that is really on the far side.
        seen = {n: sum(1 for c in cover[n] if c > 20) for n in scar_layers}
        report["scar_headings_seen"] = seen
        for name, n in seen.items():
            if n == 0:
                report["problems"].append(
                    "%s never shows - the holdout is eating all of it, so `lift` "
                    "is too small for this plate" % name)
            elif n >= layers[name][1]["count"]:
                report["problems"].append(
                    "%s shows from every heading - the tank is not holding it "
                    "out, so a hole in the far side is being drawn on the near "
                    "one" % name)

        # And the other half of the same question: what *is* drawn has to be on
        # the armour. Anti-aliasing at the silhouette costs a pixel or two, so
        # this is a fraction rather than a count.
        off = {}
        for name in scar_layers:
            m = layers[name][1]
            worst = 0.0
            for h in range(m["count"]):
                a = _tile_of(layers, name,
                             (levels - 1) * m["count"] + h)[:, :, 3] > 0.5
                # Headings where the plate is nearly edge-on are skipped, and
                # that is not the threshold going soft. There the mark is a
                # sliver a few pixels wide standing a pixel and a half off the
                # armour, so a pixel either way is a quarter of it - the
                # measure stops meaning "hangs off the tank" and starts meaning
                # "is small". A quarter of its best showing is where it still
                # means the first thing.
                if a.sum() < 20 or cover[name][h] < 0.25 * max(cover[name]):
                    continue
                tank = _tank_alpha(layers, h) > 0.25
                worst = max(worst, float((a & ~tank).sum()) / float(a.sum()))
            off[name] = round(worst, 4)
        report["scar_off_armour"] = off
        for name, frac in off.items():
            if frac > cfg["scar_off_armour_max"]:
                report["problems"].append(
                    "%.0f%% of %s hangs off the tank - the plate was measured "
                    "wider than it is, or the mark is bigger than the plate"
                    % (100.0 * frac, name))

        # How much each level moves the picture - the plume's contrast measure
        # with the backdrop swapped, and the swap is the whole lesson of this
        # layer. Every other effect here is judged over the game's pale field,
        # because that is what it is seen against and a low-alpha layer there is
        # that field faintly tinted. A decal is seen against dark green paint,
        # so the rule inverts: what carries a mark on armour is the *light* in
        # it. A black hole on a dark hull moves the picture by 40 levels and the
        # bare metal torn back around it by 130.
        #
        # This is the number that says "the scorch is invisible" without
        # anybody's eyes. It was, at first: it measured perfectly well against
        # the ground and read as nothing at all on the tank.
        contrast, weight = {}, {}
        for name in scar_layers:
            m = layers[name][1]
            h = best[name]
            armour = _tile_of(layers, "hull", h).copy()
            armour = _over(armour, _tile_of(layers, "turret", h))
            per, mass = [], []
            for p in range(levels):
                s = _tile_of(layers, name, p * m["count"] + h)
                lit = _over(armour, s)
                moved = np.abs(lit[:, :, :3] - armour[:, :, :3]).max(axis=2)
                body = s[:, :, 3] > 0.25
                per.append(round(float(moved[body].max() * 255.0)
                                 if body.any() else 0.0, 1))
                mass.append(int(moved.sum() * 255.0))
            contrast[name] = per
            weight[name] = mass
        report["scar_contrast"] = contrast
        report["scar_weight"] = weight
        for name, per in contrast.items():
            if min(per) < cfg["scar_contrast_min"]:
                report["problems"].append(
                    "the lightest %s only moves the armour by %.0f levels of "
                    "255 - on dark paint the readable part of a mark is the "
                    "metal it bares, not the soot" % (name, min(per)))

        # Three renders of one drawing would pass everything above. The axis is
        # damage, so each level has to be plainly heavier than the last -
        # measured as how much of the picture it changes in total, which is the
        # `scar_weight` above.
        #
        # Neither half of that on its own works, and both were tried. Area
        # says a long scrape is worse than a round hole, because it covers more
        # plate. Peak contrast says the opposite the moment the light level
        # opens with bared metal - a scuff and a gouge both bare metal, so
        # their brightest pixels are within a few levels of each other. What
        # separates them is that the hole changes more of the picture, more.
        steps = {}
        for name in scar_layers:
            m = layers[name][1]
            steps[name] = [
                int((_tile_of(layers, name,
                              p * m["count"] + best[name])[:, :, 3] > 0.25).sum())
                for p in range(levels)]
        report["scar_level_px"] = steps
        for name, series in weight.items():
            gaps = [b / max(a, 1) for a, b in zip(series, series[1:])]
            if gaps and min(gaps) < cfg["scar_step_ratio"]:
                report["problems"].append(
                    "%s barely worsens between levels (x%s) - the phase axis is "
                    "damage, and three renders of one drawing is not it"
                    % (name, ", ".join("%.2f" % g for g in gaps)))

        for name in scar_layers:
            worst = _edge_alpha(layers, name)
            report.setdefault("scar_edge_alpha", {})[name] = round(worst, 4)
            if worst > 0.02:
                report["problems"].append(
                    "%s touches its tile edge (alpha %.3f)" % (name, worst))

        try:
            fit = _load(cfg, "hit_scar").fits(
                {"hull": _names(cfg, parts(cfg))["hull_mesh"]})
            report["scar_fit"] = fit
            over = [f for f, v in fit.items() if v > cfg["scar_fit_max"]]
            if over:
                report["problems"].append(
                    "the mark takes more than %.0f%% of the %s plate - it is a "
                    "smaller shell or a plate measured too tightly, and neither "
                    "is worth guessing at"
                    % (100.0 * cfg["scar_fit_max"], ", ".join(over)))
        except Exception as exc:                      # scene without the stamps
            report["scar_fit"] = str(exc)

        # heading down, plate across, at the worst damage: this is the sheet
        # that shows the mark turning with the hull and going out behind it.
        def draw_scar(buf, place, heading, name):
            m = layers[name][1]
            for layer, index in _ground(layers, heading) + _body(layers, heading):
                buf = place(buf, layer, index, _over)
            return place(buf, name, (levels - 1) * m["count"] + heading, _over)

        report["scar_composite"] = _sheet(
            cfg, layers, "_check_scar", list(range(meta["count"])), scar_layers,
            draw_scar, _ground_colour(layers), _body_used(*scar_layers))

        # plate down, damage across, each on the heading that shows it best
        def draw_level(buf, place, name, level):
            m = layers[name][1]
            h = best[name]
            for layer, index in _ground(layers, h) + _body(layers, h):
                buf = place(buf, layer, index, _over)
            return place(buf, name, level * m["count"] + h, _over)

        report["scar_levels"] = _sheet(
            cfg, layers, "_check_damage", scar_layers, list(range(levels)),
            draw_level, _ground_colour(layers), _body_used(*scar_layers))

    # --- nothing may touch the tile edge, on any frame of any tank layer -----
    edges = {}
    # the gun tube is named here for the reason it is named in `_body()`: it
    # left the turret layer, so the turret's clean edge stopped saying anything
    # about the tube, and the one layer that moves outward on its own axis was
    # the one nobody asked
    # the shadow is in this list rather than with the effects because it cannot
    # be a budget question: its catcher is a disc of the tile's own inradius, so
    # it is clipped to ground the tile covers, and a shadow at the frame edge
    # means the tile is wrong rather than that the shadow is too big
    # the wreck is in this list and not with the effects for the same reason the
    # shadow is: it is the tank, in another pose, so an edge it touches is a
    # framing bug and not a budget. It is also the pose most likely to reach
    # further than the live one - a canted turret stands higher than a seated
    # one - so it is exactly the layer this test exists for.
    for name in (("hull", "turret", "barrel", "hex", "shadow")
                 + TRACK_LAYERS + WRECK_LAYERS):
        if name not in layers:
            continue
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

    # --- the shadow is dark enough to see, and it is under the tank ----------
    #
    # Two numbers because there are two ways to fail and they look nothing
    # alike. A shadow present in the alpha and absent from the screen is the
    # trap `exhaust_contrast` was written for and the same measure catches it.
    # A shadow that is dark but in the wrong place is the trap the *whole
    # layered pipeline* is about: it is drawn on the shared anchor, so if it
    # were rendered against a different fit it would sit beside the tank rather
    # than under it, and every heading would look plausible on its own.
    if "shadow" in layers:
        ground = _ground_colour(layers)
        report["shadow_contrast"] = round(
            _contrast(layers, "shadow", headings, ground), 1)
        if report["shadow_contrast"] < cfg["shadow_contrast_min"]:
            report["problems"].append(
                "the shadow moves the tile by only %.1f levels of 255 - it is "
                "in the alpha and not on the screen. Lower `reach` or widen "
                "`softness` in ground_shadow.CONFIG"
                % report["shadow_contrast"])
        adrift = []
        for heading in headings:
            shade = _tile_of(layers, "shadow", heading)[:, :, 3] > 0.15
            tank = _tank_alpha(layers, heading) > 0.5
            if not shade.any():
                adrift.append([heading, 0.0])
                continue
            # the tank's own column span, not its silhouette: the shadow is on
            # the ground and the tank is above it, so they overlap in x and
            # need not in y
            cols = np.nonzero(tank.any(axis=0))[0]
            on = float(shade[:, cols.min():cols.max() + 1].sum()) / shade.sum()
            if on < 0.9:
                adrift.append([heading, round(on, 3)])
        report["shadow_under_tank"] = not adrift
        if adrift:
            report["problems"].append(
                "the shadow falls outside the tank's own span at %s - it is "
                "not fitted with the tank" % adrift)

    # --- the tank stands on the tile, rather than the tile cutting through ---
    # On a parts scene the lowest thing on the tank is a belt, not the hull -
    # the hull layer's bottom edge is where the tracks were held out of it - so
    # the test takes whichever of them the scene has.
    hull_a = _tile_of(layers, "hull", 0)[:, :, 3] > 0.5
    for name in TRACK_LAYERS:
        if name in layers:
            hull_a |= _tile_of(layers, name, 0)[:, :, 3] > 0.5
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

    # --- the wreck: live tank against knocked-out, every heading -------------
    #
    # Two rows of the same headings, because the whole question is a comparison:
    # a wreck has to be unmistakable beside the tank it used to be, and neither
    # row on its own answers that. The turret's cant is fixed to the turret, so
    # the void under it opens towards the camera on some headings and away on
    # others - the sheet is where that is judged, and it is the reason the sheet
    # runs every heading rather than the usual four.
    if "wreck_turret" in layers:
        def draw_wreck(buf, place, heading, dead):
            for layer, index in _ground(layers, heading):
                buf = place(buf, layer, index, _over)
            buf = place(buf, "hull", heading, _over)
            # phase 0 of a running belt is index `heading`, and a wreck belt has
            # one phase, so both are the same index either way
            for running, gone in ((TRACK_LAYERS[0], "wreck_track_left"),
                                  (TRACK_LAYERS[1], "wreck_track_right")):
                name = gone if (dead and gone in layers) else running
                if name in layers:
                    buf = place(buf, name, heading, _over)
            if dead:
                return place(buf, "wreck_turret", heading, _over)
            buf = place(buf, "turret", heading, _over)
            if "barrel" in layers:
                buf = place(buf, "barrel", heading, _over)
            return buf

        report["wreck_composite"] = _sheet(
            cfg, layers, "_check_wreck", list(range(layers["hull"][1]["count"])),
            [0, 1], draw_wreck, _ground_colour(layers),
            _body_used(*WRECK_LAYERS))

        # A wreck that is not different is not a wreck. Measured against the live
        # tank on the same heading and in the same composite, because "the pose
        # was applied" is exactly the claim a hook can make while doing nothing:
        # `pose_turret` reads the ring off a stamp, and a missing stamp used to
        # mean the layer rendered a seated turret and said so in no number.
        live = _tile_of(layers, "turret", 0)[:, :, 3]
        dead = _tile_of(layers, "wreck_turret", 0)[:, :, 3]
        moved = int(np.count_nonzero((live > 0.5) != (dead > 0.5)))
        report["wreck_turret_moved_px"] = moved
        if moved < 200:
            report["problems"].append(
                "the wreck's turret differs from the live one by %d px - the "
                "pose did not take, or the cant is too small to see. It clears "
                "the skirt at a few degrees and still hides under it until "
                "about 20 - see wreck_pose.py" % moved)
        # And the void: with the turret lifted, the hull's own unlit interior
        # shows through the ring. It is the strongest mark of a wreck here, and it
        # exists only because there is no deck under the turret.
        #
        # **Measured as dark hull rather than as uncovered turret**, and that
        # correction matters: counting pixels the turret used to cover and no
        # longer does mostly counts the silhouette *moving*, which a cant does
        # whether or not it opens anything. That number read 1676 on HT_PARTS at a
        # cant which opens no void at all - it passed a threshold of 60 while the
        # picture showed a tank with a tilted turret. So the test asks for the
        # three things that make it a void: the turret has come off it, the hull
        # is behind it, and what is behind is far darker than the hull's own paint.
        best, per = 0, []
        for h in range(layers["hull"][1]["count"]):
            hull = _tile_of(layers, "hull", h)
            was = _tile_of(layers, "turret", h)[:, :, 3] > 0.5
            now = _tile_of(layers, "wreck_turret", h)[:, :, 3] > 0.5
            skin = hull[:, :, 3] > 0.5
            lum = hull[:, :, :3] @ np.array([0.299, 0.587, 0.114])
            if not skin.any():
                per.append(0)
                continue
            paint = float(np.median(lum[skin]))
            seen = int(np.count_nonzero(was & ~now & skin
                                        & (lum < 0.55 * paint)))
            per.append(seen)
            best = max(best, seen)
        report["wreck_void_px"] = best
        report["wreck_void_by_heading"] = per
        if best < 60:
            report["problems"].append(
                "the lifted turret opens no void - %d px of dark hull interior at "
                "the best heading. That is what reads as a burnt-out fighting "
                "compartment, and without it the wreck is a tank with a tilted "
                "turret. The lever is wreck_pose.CONFIG['rim_clear_frac']" % best)
        for name in ("wreck_track_left", "wreck_track_right"):
            if name not in layers:
                continue
            base = TRACK_LAYERS[0] if name.endswith("left") else TRACK_LAYERS[1]
            a = _tile_of(layers, base, 0)[:, :, 3]
            b = _tile_of(layers, name, 0)[:, :, 3]
            slack = int(np.count_nonzero((a > 0.5) != (b > 0.5)))
            report.setdefault("wreck_track_moved_px", {})[name] = slack
            if slack < 80:
                report["problems"].append(
                    "%s is within %d px of the running belt - the top run is "
                    "under the sponson and sagging it is invisible, so the "
                    "slack has to go outboard at the front" % (name, slack))

    # --- the gun tube: the stroke across, headings down ----------------------
    if "barrel" in layers:
        bm = layers["barrel"][1]
        nph = int(bm.get("phases") or 1)

        def draw_recoil(buf, place, heading, phase):
            for layer, index in _ground(layers, heading) + _body(layers, heading):
                if layer == "barrel":
                    continue          # drawn at this sheet's phase, not at rest
                buf = place(buf, layer, index, _over)
            return place(buf, "barrel", phase * bm["count"] + heading, _over)

        report["recoil_composite"] = _sheet(
            cfg, layers, "_check_recoil", headings, list(range(nph)),
            draw_recoil, _ground_colour(layers), _body_used())

        # How far the muzzle actually withdraws, per phase and per heading. The
        # travel runs along the bore, which leaves the ground plane, so this
        # varies about four-fold with heading - the same projection that gives
        # the muzzle its 71/60/16 px overhang, and not a fault.
        #
        # Two phases that render the same pixels are one render done twice, so
        # the sheet is not the only thing that has to be looked at here.
        ax, ay = bm["anchor_px"]
        reach = []
        for p in range(nph):
            row = []
            for h in range(bm["count"]):
                # `_tile_of` hands back bottom-up rows and `anchor_px` counts
                # from the top-left, so the tile is flipped before the distance
                # is taken. Measuring in the two conventions at once pins the
                # far point to the silhouette's bottom edge and reports a stroke
                # of exactly zero - which reads as a hook that never fired.
                a = _tile_of(layers, "barrel", p * bm["count"] + h)[::-1, :, 3]
                ys, xs = np.nonzero(a > 0.5)
                row.append(None if len(xs) == 0 else
                           float(np.hypot(xs - ax, ys - ay).max()))
            reach.append(row)
        stroke = [[None if (reach[0][h] is None or reach[p][h] is None)
                   else round(reach[0][h] - reach[p][h], 2)
                   for h in range(bm["count"])] for p in range(nph)]
        report["recoil_stroke_px"] = stroke
        best = max(s for s in stroke[1] if s is not None)
        report["recoil_peak_px"] = round(best, 2)
        if best < 2.0:
            report["problems"].append(
                "the gun withdraws %.2f px at best, which reads as nothing - "
                "raise `barrel_recoil.CONFIG['travel']`" % best)
        # every phase past the kick must be on its way home, and none may reach
        # further out than rest: a transposed table looks right at phase 0
        col = [stroke[p][3 % bm["count"]] for p in range(nph)]
        if any(s is not None and s < -0.75 for row in stroke for s in row):
            report["problems"].append(
                "the tube reaches further than rest on some phase - that is "
                "not a recoil, and the phase table is the place to look")
        elif nph > 2 and not all(col[i] >= col[i + 1] - 0.35
                                 for i in range(1, nph - 1)):
            report["problems"].append(
                "the stroke does not come home monotonically: %s" % col)
        flat = [p for p in range(2, nph)
                if col[p] is not None and col[p - 1] is not None
                and abs(col[p] - col[p - 1]) < 0.35]
        if flat:
            report["problems"].append(
                "phases %s render the same pixels as the phase before them - "
                "space the travel evenly or render fewer phases" % flat)

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
    # between the two: the plate table is in pixels and needs the camera the
    # render just fitted, and `check` reads it back out of the layer metadata
    stamped = stamp_table(cfg)
    checked = check(cfg)

    problems = list(rendered["warnings"]) + list(checked["problems"])
    if not rendered["framing_identical"]:
        problems.append("layers do not share a framing")
    # the body measures the ring itself and compares it against the stamp; a
    # disagreement there is the invisible failure and has to surface here
    problems.extend((rendered.get("body") or {})
                    .get("spin", {}).get("axis_warnings", []))
    for stage in ("muzzle", "exhaust", "hits"):
        if prepared[stage]:
            problems.extend(prepared[stage]["warnings"])

    # the tile is `hex_base`'s on a single-mesh scene and `parts_render`'s on a
    # parts one, where it is built inside the render - so its numbers come back
    # with the body rather than from `prepare`
    body = rendered.get("body") or {}
    tile_meta = body.get("hex") or prepared["hex"]

    report = {
        "have": prepared["have"],
        "layout": prepared["layout"],
        "axis": prepared["axis"],
        "ground_z": tile_meta.get("ground_z"),
        "stands_on_tile": checked["stands_on_tile"],
        "framing_identical": rendered["framing_identical"],
        "spin_pivot": rendered["spin_pivot"],
        "spin_pivot_from": rendered["spin_pivot_from"],
        "anchor_px": checked["anchor_px"],
        "units_per_pixel": checked["units_per_pixel"],
        "max_edge_alpha": checked["max_edge_alpha"],
        "shadow": body.get("shadow"),
        "shadow_contrast": checked.get("shadow_contrast"),
        "shadow_under_tank": checked.get("shadow_under_tank"),
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
    # The gun tube, and asked of `checked` rather than of `prepared`: the tube
    # recoils when the scene has a barrel *and* phases were asked for, which is
    # not the question `prepared["muzzle"]` answers - `recoil_phases` 0 leaves it
    # in the turret layer and leaves these keys absent.
    #
    # This list is enumerated, so a key added to `check` and not added here is
    # computed, dropped, and never seen. It happened to exactly these three: the
    # sheet was written, the stroke was measured, and the report carried none of
    # it - which reads as a check that was never written rather than one whose
    # answer went nowhere.
    if checked.get("recoil_stroke_px"):
        report.update({
            "recoil_stroke_px": checked["recoil_stroke_px"],
            "recoil_peak_px": checked["recoil_peak_px"],
            "recoil_composite": checked["recoil_composite"],
        })
    if checked.get("wreck_turret_moved_px") is not None:
        report.update({
            "wreck_turret_moved_px": checked["wreck_turret_moved_px"],
            "wreck_void_px": checked["wreck_void_px"],
            "wreck_void_by_heading": checked["wreck_void_by_heading"],
            "wreck_track_moved_px": checked.get("wreck_track_moved_px"),
            "wreck_composite": checked["wreck_composite"],
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
    if prepared["hits"]:
        report.update({
            # `visible` belongs here beside `faceon` because the two disagree,
            # and the disagreement is the whole reason it is measured: HT's rear
            # plate had the *best* faceon of the four (0.999) while two thirds of
            # the mark on it was under the engine deck. Enumerated by name like
            # everything else in this report, which is how the gun tube's stroke
            # once got computed and dropped - so a key added to a plate has to be
            # added here too or it goes nowhere.
            "plates": [{k: p.get(k) for k in ("face", "elevation", "faceon",
                                              "visible", "visible_at",
                                              "share", "extent")}
                       for p in prepared["hits"]["plates"]],
            "hit_table": stamped["stamped"],
            "burst_edge_alpha": checked.get("burst_edge_alpha"),
            "dust_edge_alpha": checked.get("dust_edge_alpha"),
            "burst_span_px": checked.get("burst_span_px"),
            "dust_span_px": checked.get("dust_span_px"),
            "burst_redness": checked.get("burst_redness"),
            "burst_white_fraction": checked.get("burst_white_fraction"),
            "dust_contrast": checked.get("dust_contrast"),
            "hit_off_tank": checked.get("hit_off_tank"),
            "hit_composite": checked.get("hit_composite"),
            "scar_fit": checked.get("scar_fit"),
            "scar_headings_seen": checked.get("scar_headings_seen"),
            "scar_off_armour": checked.get("scar_off_armour"),
            "scar_level_px": checked.get("scar_level_px"),
            "scar_contrast": checked.get("scar_contrast"),
            "scar_weight": checked.get("scar_weight"),
            "scar_cover_px": checked.get("scar_cover_px"),
            "scar_edge_alpha": checked.get("scar_edge_alpha"),
            "scar_composite": checked.get("scar_composite"),
            "scar_levels": checked.get("scar_levels"),
        })
    if body:
        # the belts are posed by writing vertex coordinates, so whether the rest
        # pose came back is a result to report, not a promise to make
        report.update({
            "track_phases": body["track_phases"],
            "belts": body["belts"],
            "belts_restored": body["belts_restored"],
            "rebuilt": body["rebuilt"],
            "ring_cut_z": body["ring"].get("cut_z"),
            "tile_removed": body["tile_removed"],
            "cut_box_removed": body["cut_box_removed"],
            # the pose the wreck layers were rendered in, and the lift over the
            # skirt that says whether its void could show at all
            "wreck": body.get("wreck"),
        })

    # Beside the atlases, the same as `parts_render.render()` writes and for the
    # same reason: which axis, which ports, which plates and which belt produced
    # these frames is not recoverable from the PNGs, and leaving it in whoever
    # ran it means it is gone by the next session.
    with open(os.path.join(cfg["output_dir"], "_run_report.json"), "w",
              encoding="utf-8") as fh:
        json.dump(report, fh, indent=1, default=str)

    print("[tank] %s  %d problems" % (cfg["output_dir"], len(problems)))
    for line in problems:
        print("[tank]   ! %s" % line)
    for key in ("composite", "exhaust_composite", "fire_composite",
                "hit_composite", "scar_composite", "scar_levels"):
        if report.get(key):
            print("[tank] now open %s and look at it - the numbers above have "
                  "all been green while the picture was wrong" % report[key])
    return report


if __name__ == "__main__":
    from pprint import pprint

    pprint(run(CONFIG))
