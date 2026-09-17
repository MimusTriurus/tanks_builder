"""Which side of the turret an effect is on, heading by heading.

A layer indexed by the *hull's* heading cannot have the turret held out of it.
The turret turns against the hull, so a turret cut into the layer's alpha is cut
at one relative bearing out of twenty-four, and the atlas has no axis to vary it
along: 288 fire frames are 12 phases by 24 hull headings and nothing else.
Traverse the gun and the bite stays where the turret used to be - a hole in the
flame that nothing on screen accounts for - while flame lies over the turret
where it now is. It is not a tuning problem, it is the frame index.

So the turret is rendered whole and the effect is *ordered* against it, which the
harness can do per heading because the answer is a property of the heading: the
turret sits on its ring in the middle of the hull, and turning it changes its
silhouette but not its depth. What this module produces is that one bit per
heading, plus the margin it was decided by.

**Rays rather than pixels, and rather than centroids.** Two other tests were
tried and are worth naming, because both look right:

- *Centroid depth* - compare the effect's centre with the turret's along the view
  axis. Wrong by one heading out of twenty-four on HTP, and wrong in the way that
  matters: what decides the order is the part that *overlaps*, and at the
  crossover that part is nowhere near either centre.
- *Occluded fraction* - what share of the effect the turret hides. Never rose
  above 0.46 even where every overlapping pixel was behind the turret, because
  most of a flame column is above the turret and overlaps nothing. A statistic
  over the whole effect cannot answer a question about the overlap.

Casting both ways answers it directly: a vertex whose ray to the camera hits the
turret is behind it, a vertex whose ray away from the camera hits it is in front
of it, and a vertex that hits neither does not overlap and does not vote. Checked
against a pixel measurement - two renders of the fire layer, with the turret held
out and without, differenced against the turret's silhouette on all 24 headings -
and it agreed on every one.

**The margin is reported because the crossover is real.** At the two headings
where the answer flips, some of the effect is genuinely on the wrong side of the
turret; ordering is one decision for the whole layer and cannot express that. On
HTP those headings carry 93px and 88px of the losing side. That is the price of
the scheme, it is bounded, and it is two headings out of twenty-four - against a
baked holdout, which is wrong by an unbounded amount the moment the turret moves.
"""

import math

import bpy
import numpy as np
from mathutils import Matrix, Vector


def _world_verts(ob):
    n = len(ob.data.vertices)
    if n == 0:
        return np.zeros((0, 3))
    co = np.empty(n * 3, dtype=np.float64)
    ob.data.vertices.foreach_get("co", co)
    m = np.array(ob.matrix_world)
    return co.reshape(n, 3) @ m[:3, :3].T + m[:3, 3]


def over_turret(effect, turret, pivot, to_cam, angles, step=3):
    """`(flags, margins)` - is `effect` in front of `turret` at each heading.

    `to_cam` is the unit vector from the pivot towards the camera, and the tank
    is spun rather than the camera, so a heading is applied here by turning the
    view the other way. That keeps the mesh untouched: this runs while the scene
    is at rest, before the job, and must leave it that way.

    `step` samples the effect's vertices. These are puff clouds of a few thousand
    vertices and the answer is a majority over the overlapping ones, so a third
    of them is plenty and the cost is what makes this affordable at all.
    """
    fx = bpy.data.objects[effect] if isinstance(effect, str) else effect
    tur = bpy.data.objects[turret] if isinstance(turret, str) else turret
    world = _world_verts(fx)
    inv = tur.matrix_world.inverted()
    flags, margins = [], []
    for ang in angles:
        d = (Matrix.Rotation(-math.radians(ang), 4, "Z") @ Vector(to_cam)).normalized()
        local = (inv.to_3x3() @ d).normalized()
        behind = ahead = 0
        for k in range(0, len(world), step):
            o = inv @ Vector(world[k])
            if tur.ray_cast(o, local, distance=50.0)[0]:
                behind += 1
            elif tur.ray_cast(o, -local, distance=50.0)[0]:
                ahead += 1
        flags.append(bool(ahead > behind))
        # what the decision won by, as a share of the overlapping votes. Zero
        # means nothing overlapped and the flag is arbitrary - which is fine,
        # and is exactly what a reader needs to know before believing it.
        total = ahead + behind
        margins.append(round(abs(ahead - behind) / total, 3) if total else None)
    return flags, margins
