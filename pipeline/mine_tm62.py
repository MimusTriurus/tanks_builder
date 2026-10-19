"""The TM-62M anti-tank mine, low-poly for the 3D bench's cel look.

Real dimensions, metres, Blender Z up, the mine's bottom centre at the origin:
a steel drum 0.32 across and about 0.09 tall with a rolled seam round its
waist and a chamfered lip; a shallow top plate; in its middle the threaded
collar and the MVCh-62 fuze with its pressure cap, 0.128 overall. The numbers
are the mine's own (TM-62M: 320 mm, 128 mm with the fuze), so the bench is the
one that decides how much bigger than life it stands on a hex
(Tank3DBench.Mines, MineWide) - here it stays true.

Few segments and no texture: Toon draws it in flat tones with the tanks' ink,
and a 32-gon reads round at any zoom the board has. Two materials - the olive
body and the dark fuze - because those are the two things a player has to see:
a mine, and which way is up.

build() makes it in its own scene, export_game() writes
assets/Models/Mine/{tm62.glb, tm62.json} and assets/Scenes/mine_tm62.blend
with the mine alone (Scenes/ is out of git: the script is the source),
then takes the scene down again - the file that was open stays as it was.
"""

import json
import math
import os

import bmesh
import bpy

SEGMENTS = 32

CONFIG = {
    "name": "TM62",
    "radius": 0.160,         # the drum: 320 mm across
    "body_top": 0.088,       # the drum's height to the top plate
    "chamfer": 0.008,        # the rolled lip at the top and the bottom
    "seam_z": 0.046,         # the waist seam's middle
    "seam_h": 0.008,
    "seam_out": 0.004,       # how far the seam stands proud
    "plate_r": 0.142,        # the top plate inside the lip
    "plate_top": 0.094,
    "ring_r": 0.108,         # the stiffening ring pressed into the plate
    "ring_w": 0.006,
    "ring_h": 0.003,
    "collar_r": 0.050,       # the fuze well's threaded collar
    "collar_top": 0.104,
    "fuze_r": 0.040,         # MVCh-62
    "fuze_top": 0.120,
    "cap_r": 0.030,          # the pressure cap
    "cap_top": 0.128,
    "body": "#56603a",       # olive drab, a shade lighter than the tanks' green
    "fuze": "#33332f",
}

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
OUT = os.path.join(REPO, "assets", "Models", "Mine")


def _material(name, hexcolor, rough=0.7):
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    h = hexcolor.lstrip("#")
    srgb = [int(h[i:i + 2], 16) / 255.0 for i in (0, 2, 4)]
    lin = [c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4 for c in srgb]
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get("Principled BSDF")
    bsdf.inputs["Base Color"].default_value = (*lin, 1.0)
    bsdf.inputs["Roughness"].default_value = rough
    bsdf.inputs["Metallic"].default_value = 0.0
    mat.diffuse_color = (*lin, 1.0)
    return mat


def _lathe(bm, profile, mat_index):
    """A solid of revolution from a (r, z) profile, bottom to top, closed at
    both ends by fans when the end radius is nought or by a cap face."""
    rings = []
    for r, z in profile:
        if r <= 1e-6:
            rings.append([bm.verts.new((0.0, 0.0, z))])
            continue
        rings.append([bm.verts.new((r * math.cos(2 * math.pi * i / SEGMENTS),
                                    r * math.sin(2 * math.pi * i / SEGMENTS), z))
                      for i in range(SEGMENTS)])
    faces = []
    for a, b in zip(rings, rings[1:]):
        for i in range(SEGMENTS):
            j = (i + 1) % SEGMENTS
            if len(a) == 1:
                faces.append(bm.faces.new((a[0], b[j], b[i])))
            elif len(b) == 1:
                faces.append(bm.faces.new((a[i], a[j], b[0])))
            else:
                faces.append(bm.faces.new((a[i], a[j], b[j], b[i])))
    if len(rings[0]) > 1:
        faces.append(bm.faces.new(list(reversed(rings[0]))))
    if len(rings[-1]) > 1:
        faces.append(bm.faces.new(rings[-1]))
    for f in faces:
        f.material_index = mat_index
        f.smooth = False
    return faces


def _part(scene, name, profile, mat):
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    _lathe(bm, profile, 0)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bm.to_mesh(me)
    bm.free()
    me.materials.append(mat)
    ob = bpy.data.objects.new(name, me)
    scene.collection.objects.link(ob)
    return ob


def build(cfg=CONFIG):
    """The mine in a scene of its own, named after it. Returns the scene."""
    name = cfg["name"]
    old = bpy.data.scenes.get(name)
    if old is not None:
        _drop(old)
    scene = bpy.data.scenes.new(name)
    body = _material("TM62_Body", cfg["body"])
    fuze = _material("TM62_Fuze", cfg["fuze"], rough=0.5)

    R, c = cfg["radius"], cfg["chamfer"]
    top = cfg["body_top"]
    sz, sh, so = cfg["seam_z"], cfg["seam_h"], cfg["seam_out"]
    # The drum, its seam and the plate as one turned body: chamfered foot,
    # the wall, the seam standing proud, the wall, the rolled lip, the dip
    # inside the lip, the plate with its pressed ring, up to the collar.
    rr, rw, rh = cfg["ring_r"], cfg["ring_w"], cfg["ring_h"]
    pr, pt = cfg["plate_r"], cfg["plate_top"]
    _part(scene, name + "_Body", [
        (0.0, 0.0),
        (R - c, 0.0),
        (R, c),
        (R, sz - sh * 0.5),
        (R + so, sz - sh * 0.5 + so),
        (R + so, sz + sh * 0.5 - so),
        (R, sz + sh * 0.5),
        (R, top - c),
        (R - c * 0.5, top),
        (pr + 0.004, top),
        (pr, pt - 0.002),
        (rr + rw, pt),
        (rr + rw * 0.5, pt + rh),
        (rr - rw * 0.5, pt + rh),
        (rr - rw, pt),
        (cfg["collar_r"] + 0.004, pt),
        (0.0, pt),
    ], body)
    # The fuze: the collar, the fuze body, the pressure cap with a soft edge.
    cr, ct = cfg["collar_r"], cfg["collar_top"]
    fr, ft = cfg["fuze_r"], cfg["fuze_top"]
    kr, kt = cfg["cap_r"], cfg["cap_top"]
    _part(scene, name + "_Fuze", [
        (0.0, pt - 0.004),
        (cr, pt - 0.004),
        (cr, ct),
        (fr, ct),
        (fr, ft - 0.003),
        (fr - 0.003, ft),
        (kr + 0.002, ft),
        (kr, ft + 0.002),
        (kr, kt - 0.002),
        (kr - 0.003, kt),
        (0.0, kt),
    ], fuze)
    return scene


def _drop(scene):
    for ob in list(scene.collection.all_objects):
        me = ob.data
        bpy.data.objects.remove(ob)
        if me is not None and me.users == 0:
            bpy.data.meshes.remove(me)
    bpy.data.scenes.remove(scene)


def export_game(out_dir=OUT, cfg=CONFIG, keep=False):
    """Write tm62.glb (glTF / Godot: +Y up, metres, bottom centre at the
    origin), tm62.json and assets/Scenes/mine_tm62.blend with the mine alone. The scene is taken
    down afterwards unless keep is set, and the window goes back to the scene
    it showed."""
    os.makedirs(out_dir, exist_ok=True)
    win = bpy.context.window
    was = win.scene
    scene = build(cfg)
    try:
        win.scene = scene
        glb = os.path.join(out_dir, "tm62.glb")
        bpy.ops.export_scene.gltf(filepath=glb, export_format="GLB", use_selection=False, use_active_scene=True,
                                  export_apply=True, export_yup=True, export_materials="EXPORT",
                                  export_animations=False, export_cameras=False, export_lights=False)
        side = {
            "source": "pipeline/mine_tm62.py export_game",
            "frame": "glTF / Godot: +Y up, metres, the mine's bottom centre at the origin",
            "units": "m",
            "size": {"diameter": round(2 * (cfg["radius"] + cfg["seam_out"]), 4),
                     "body": round(2 * cfg["radius"], 4), "height": cfg["cap_top"]},
            "nodes": {"body": cfg["name"] + "_Body", "fuze": cfg["name"] + "_Fuze"},
        }
        with open(os.path.join(out_dir, "tm62.json"), "w", encoding="utf-8") as f:
            json.dump(side, f, indent=1)
        blend = os.path.join(REPO, "assets", "Scenes", "mine_tm62.blend")
        os.makedirs(os.path.dirname(blend), exist_ok=True)
        bpy.data.libraries.write(blend, {scene}, fake_user=True)
        return {"glb": glb, "bytes": os.path.getsize(glb), "blend": blend,
                "verts": sum(len(o.data.vertices) for o in scene.objects)}
    finally:
        win.scene = was
        if not keep:
            _drop(scene)
