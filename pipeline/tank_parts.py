"""Where the parts of a tank are, whichever shape the scene arrived in.

Two layouts exist in this project and both have to keep working.

**single** - the old scenes (HT, MT, LT). One `world` empty, `Hull` and `Turret`
under it, hand-cut pieces (`Barrel`, `Engine`) parented to those. The model came
as one mesh and was split by hand.

**parts** - the canon for everything from MT_PARTS on. Four layer roots straight
at the scene root, no `world` above them, and the meshes carry a `.Geometry`
suffix::

    Hull.World          Hull.Geometry, Engine.Geometry
    Turret.World        Turret.Geometry, Barrel.Geometry
    Track.Left.World    L.Caterpillar.Geometry, L.Rolls.Geometry
    Track.Right.World   R.Caterpillar.Geometry, R.Rolls.Geometry

**A casemate is the parts layout with the turret root absent**, and that absence
is the whole declaration - there is no flag anywhere::

    Hull.World          Hull.Geometry, Engine.Geometry,
                        Casemate.Geometry, Barrel.Geometry
    Track.Left.World    ...
    Track.Right.World   ...

The gun still needs a layer of its own to recoil in, so `Barrel.Geometry` stays
a separate mesh - but it hangs off the hull, because on a casemate everything
turns with the hull and nothing turns against it. The box itself is *in* the hull
layer, which is what stops the two facts that used to be wrong about these
vehicles: the fitted ring (`ring_roundness` 0.70 on a box, and a fitted radius
larger than the box it was fitted to) and the hole in the deck the generator
leaves under a mount, which shows the moment the mount layer is not drawn.

Why the absence and not a property: the same reason `layout` is read from the
scene rather than configured. A flag saying "no turret" on a scene that has a
`Turret.World` would be two answers to one question, and the stale one wins
silently. Here the vehicle's shape *is* the answer - see docs/tank-scene.md.

Two different questions get asked about a part, and every module that broke on
the parts layout broke by conflating them:

`mesh()` - **the geometry to measure.** Rays, vertex clouds, bore axes, plate
fits. On a parts scene that is `Hull.Geometry`, never the empty above it.

`root()` - **the object a layer renders,** whose `matrix_world` the renderer
writes and restores, and which parenting is checked against. On a parts scene
that is `Hull.World`. On a single scene the hull layer targets `world` and so
covers the turret too, which is why the hull layer there carries an `exclude`
and here does not.

Substring hints are a third thing again and need no help from this module:
`sprite_atlas.by_hints` matches by substring and pulls in descendants, so the
bare `"Hull"` already names hull-plus-engine on both layouts and `"Turret"`
already names turret-plus-barrel. Holdouts and excludes are therefore left
spelled bare, and only measurement targets are resolved here.

Resolution order, and why it is this and not a fuzzy match:

1. the exact name - so a caller who knows what it wants gets it, including the
   empties (`root()` relies on this)
2. exact `name + ".Geometry"` - the canon, which makes `"Hull"` work unchanged
   on both layouts and is why no module's `CONFIG` had to be rewritten
3. a *unique* mesh whose name starts with `name + "."`
4. a *unique* mesh with `name` somewhere in it

Ambiguity raises and names the candidates rather than picking one. That is the
whole point: `sprite_atlas` once chose an axis holder by hash order, and a wrong
axis does not distort the sprite - it walks the tank round a circle, and no
output number reports it. A resolver that guesses would be the same failure in a
new place.
"""

import bpy

CANON_SUFFIX = ".Geometry"
ROOT_SUFFIX = ".World"

# The layer roots of the parts layout, and the meshes each is expected to hold.
# A name here is a fact about the canon, not a preference - see docs/tank-scene.md.
PARTS_ROOTS = {
    "hull": "Hull.World",
    "turret": "Turret.World",
    "track_left": "Track.Left.World",
    "track_right": "Track.Right.World",
}
# The three that make a scene the parts layout. The turret root is *optional* and
# its absence is how a casemate says so - see the module docstring.
#
# Three rather than four, and the reason the test needs more than one root at all
# is unchanged: a single stray `*.World` empty in an old hand-split scene would
# otherwise flip the whole pipeline over. No single-mesh scene has a track root,
# let alone two, so the guard still holds with the turret taken out of it.
PARTS_REQUIRED = ("hull", "track_left", "track_right")

# The single-mesh layout. `hull` is `world` and not `Hull` because that is what
# the hull layer targets there: the whole tank, with the turret excluded.
SINGLE_ROOTS = {
    "hull": "world",
    "turret": "Turret",
}


def _scene(scene=None):
    return scene if scene is not None else bpy.context.scene


def hierarchy(ob):
    """`ob` and everything under it."""
    out = [ob]
    for child in ob.children:
        out.extend(hierarchy(child))
    return out


def inside(ob, root):
    """Is `ob` `root` or below it? Cheap, and walks up rather than down."""
    if root is None:
        return False
    up = ob
    while up is not None:
        if up is root:
            return True
        up = up.parent
    return False


def layout(scene=None):
    """Which of the two shapes this scene is, decided by what is in it.

    Read from the scene rather than configured, so a scene cannot be driven with
    the wrong layout by a stale flag. The parts test is the four roots, not one
    of them: a single `*.World` empty in an old scene would otherwise flip the
    whole pipeline over.
    """
    scene = _scene(scene)
    names = {o.name for o in scene.objects}
    if all(PARTS_ROOTS[part] in names for part in PARTS_REQUIRED):
        return "parts"
    if "world" in names:
        return "single"
    raise RuntimeError(
        "this scene is neither layout: no `world` root, and not all of %s. "
        "See the canonical structure in docs/tank-scene.md."
        % sorted(PARTS_ROOTS[p] for p in PARTS_REQUIRED))


def turreted(scene=None, kind=None):
    """Does this vehicle have a ring, as a fact about the scene?

    The turret layer root, present or not. Read here so that the twenty-odd
    places downstream that used to assume a turret ask one question of one
    module instead of each inventing its own test - and so that the test is not
    "is there a mesh called Turret", which a casemate box left under that name
    would pass. It is the *root* that carries the meaning: a layer root is a
    thing that turns against the hull.
    """
    scene = _scene(scene)
    kind = kind or layout(scene)
    if kind != "parts":
        return SINGLE_ROOTS["turret"] in {o.name for o in scene.objects}
    return PARTS_ROOTS["turret"] in {o.name for o in scene.objects}


def roots(scene=None, kind=None):
    """The layer-root names for this scene's layout, and only the ones it has.

    The turret drops out on a casemate rather than being reported as a name that
    resolves to nothing: a caller that asks for a root it cannot have should get
    a `KeyError` at the point of asking, not a `None` three stages downstream.
    """
    scene = _scene(scene)
    kind = kind or layout(scene)
    table = dict(PARTS_ROOTS if kind == "parts" else SINGLE_ROOTS)
    if not turreted(scene, kind):
        table.pop("turret", None)
    return table


def mesh(name, scene=None, required=True):
    """The mesh a part name means. See the module docstring for the order."""
    scene = _scene(scene)
    if name is None:
        # A role this vehicle does not have - the turret of a casemate. Callers
        # that already pass `required=False` and test the answer need no case of
        # their own; one that asks for it outright gets told what it asked.
        if required:
            raise RuntimeError(
                "asked for a part that is None - this vehicle has no such part "
                "(a casemate has no turret). The caller should be asking "
                "tank_parts.turreted, not resolving a name.")
        return None
    wanted = name.strip()

    # 1. the exact name, tolerating the leading space P > Selection leaves behind
    ob = scene.objects.get(name) or scene.objects.get(wanted)
    if ob is None:
        ob = next((o for o in scene.objects if o.name.strip() == wanted), None)
    if ob is not None:
        return ob

    # 2. the canon
    ob = scene.objects.get(wanted + CANON_SUFFIX)
    if ob is not None:
        return ob

    meshes = [o for o in scene.objects if o.type == "MESH"]
    # 3. one mesh below this part's name, then 4. one mesh mentioning it
    for candidates in ([o for o in meshes
                        if o.name.strip().startswith(wanted + ".")],
                       [o for o in meshes if wanted.lower() in o.name.lower()]):
        if len(candidates) == 1:
            return candidates[0]
        if len(candidates) > 1:
            raise RuntimeError(
                "%r matches %d meshes - %s. Name the one you mean; a resolver "
                "that guessed here would be the stale-axis bug in a new place."
                % (name, len(candidates),
                   ", ".join(sorted(o.name for o in candidates))))

    if required:
        raise RuntimeError(
            "no object for part %r. Tried the exact name, %r, and a unique mesh "
            "under or mentioning it. This scene holds %s."
            % (name, wanted + CANON_SUFFIX,
               ", ".join(sorted(o.name for o in scene.objects)[:20])))
    return None


def root(part, scene=None, kind=None, required=True):
    """The object the layer for `part` renders.

    `part` is a role - "hull", "turret", "track_left", "track_right" - not an
    object name. Anything else is taken as an object name and resolved as one,
    so a caller with a concrete name in hand is never blocked.
    """
    scene = _scene(scene)
    table = roots(scene, kind)
    name = table.get(part, part)
    ob = scene.objects.get(name)
    if ob is not None:
        return ob
    return mesh(name, scene, required=required)


def ports(prefix, scene=None):
    """Every mesh whose name starts with `prefix`, in name order.

    One object per outlet: two exhausts are `Engine` and `Engine.001`. The order
    is the numbering, so it has to be stable, and `.Geometry` needs no special
    case here - a prefix match already covers it.
    """
    scene = _scene(scene)
    needle = prefix.strip().lower()
    found = [o for o in scene.objects
             if o.type == "MESH" and o.name.strip().lower().startswith(needle)]
    return sorted(found, key=lambda o: o.name)


def describe(cfg=None, scene=None):
    """Everything an orchestrator needs to drive either layout.

    `cfg` may name the parts (`hull`, `turret`, `barrel`, `exhaust`); whatever is
    missing falls back to the bare canonical names, which resolve on both
    layouts. Returned names are concrete and exact, so the modules downstream
    keep their plain `scene.objects[...]` lookups.
    """
    scene = _scene(scene)
    cfg = cfg or {}
    kind = layout(scene)
    hull = cfg.get("hull", "Hull")
    turret = cfg.get("turret", "Turret")
    barrel = cfg.get("barrel", "Barrel")
    casemate = cfg.get("casemate", "Casemate")

    ring = turreted(scene, kind)
    hull_mesh = mesh(hull, scene)
    # None on a casemate, and *not* resolved by hint even if a box happens to be
    # named for a turret: `turreted` asked about the root, and this has to agree
    # with it or the two halves of one answer part company.
    turret_mesh = mesh(turret, scene) if ring else None
    barrel_mesh = mesh(barrel, scene, required=False)
    out = {
        "layout": kind,
        "turret": ring,
        # measurement targets
        "hull_mesh": hull_mesh.name,
        "turret_mesh": turret_mesh.name if turret_mesh is not None else None,
        "barrel_mesh": barrel_mesh.name if barrel_mesh is not None else None,
        # **The mount, which is not the same question as the ring.** What the
        # tube slides into when it recoils, drops out of when the tank dies, and
        # is measured against for how far it may travel - a turret on one
        # vehicle, a welded box on another. `barrel_recoil` and `wreck_pose`
        # want this one; only the carousel and the traverse want the ring.
        "mount_mesh": (turret_mesh.name if turret_mesh is not None
                       else (lambda m: m.name if m is not None else None)(
                           mesh(casemate, scene, required=False))),
        # layer roots
        "hull_root": root("hull", scene, kind).name,
        "turret_root": root("turret", scene, kind).name if ring else None,
        # substring hints, which work on both layouts untouched
        "hull_hint": hull,
        "turret_hint": turret if ring else None,
        "ports": [o.name for o in ports(cfg.get("exhaust", "Engine"), scene)],
    }
    if kind == "parts":
        out["track_roots"] = [PARTS_ROOTS["track_left"],
                              PARTS_ROOTS["track_right"]]
    return out
