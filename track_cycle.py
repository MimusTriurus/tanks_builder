"""Make the track belt run: slide it along its own loop, one link per cycle.

The belt is a closed band of geometry. Running it means moving every vertex
*along* that band by the same arc length, so the links ride round the idlers
instead of translating rigidly off the end of the hull.

Why not scroll the texture
--------------------------
The obvious answer, and it is not available here. The belt's UV layout is an
atlas of thousands of tiny islands - within a single degree of arc the UVs
cover 0.92 of the range - so there is no direction in UV space that corresponds
to "along the track". Sliding U by anything at all shuffles unrelated patches
of the texture. The relief is real geometry on this model, which is what makes
the geometric answer both possible and the better-looking one.

Why not move whole links
------------------------
Because there are no whole links to move. The mesh is the same soup the rest of
the project deals with - 28,351 disconnected shells with a median of 5 vertices,
one per panel - so connected components find panels, not links. Transport has to
be per-vertex, and a link therefore bends slightly as it rounds an idler. At a
7-pixel pitch that is invisible; the alternative is clustering shells into links
by position, which is a lot of machinery to buy a wrongness nobody can see.

The cycle does not close on MT_PARTS, and that is the asset, not the transport
-----------------------------------------------------------------------------
Sliding by one whole link pitch has to leave the belt looking exactly as it was,
or the last phase pops back to the first. That is a property of the model, and
on this one it is false. Asked by rendering - shift the belt, diff the frames,
average over all twelve headings - the mismatch just grows with the shift and
flattens out:

    1/8 link  458 px      2 links  914        5 links   966
    1/4 link  720         3 links  945        9 links   965
    1 link    827         4 links  963        half loop 1012

A track made of repeated links would show a deep well at one link and at every
multiple of one. This is a decorrelation curve with no well anywhere: the belt
is a one-off irregular band whose relief happens to average about 45 bumps, not
45 copies of a link. Sweeping the shift finely (0.90 to 1.10 of a pitch) and
sweeping the link count as an integer (41 to 49) both give a 2% wobble on a flat
floor - there is no shift that closes it, so there is no parametrisation, warp
or phase count that can be talked into one.

What it costs is one number: the closing step is **1.8x an ordinary phase step**
(827 against 458). Not a snap - a hitch, of about the size of two steps instead
of one. Two ways out if it reads badly in motion, and they are opposites:
fewer phases makes the hitch vanish into the step (at 4 phases it is 827 against
720, i.e. 1.15x) at the cost of a 2.2 px stride; or the belt gets rebuilt as one
link repeated, which closes by construction and is a change to the asset.

`report()` carries `tread_repeat` for exactly this: how well the tread profile
lies on itself after one pitch. Near 1.0 means a real track and a cycle that
will close; MT_PARTS is far from it. Look at that before believing any loop.

The pitch is measured off the tread, not off the vertices
---------------------------------------------------------
Two signals disagree here and only one of them is the thing anyone sees. The
density of *vertices* around the loop peaks cleanly at 47, and it is wrong: the
mesh carries edge loops the tread face does not. The relief of the tread on the
straight runs says 45.8, and the rendered belt, cross-correlated frame to frame,
says 45.2. Believing the vertices made the belt slip 9% against the ground -
eight phases moved it 8.07 px while its pattern repeated every 8.79 px - which
is a wrong walking speed, and it is invisible in every number the renderer
prints.

Measured on the straight runs, where the parameter is just x and no projection
is involved. Round the loop the same question is smeared, and that smear is the
parametrisation and not the belt: assigning arc length by *bearing* from the
loop centre shears the band wherever the radial direction is oblique to it,
which is everywhere along the straights. So the projection here is a proper
nearest-point one, and the pitch still comes from the straight runs.

The belt runs backwards under the tank
--------------------------------------
For a hull moving forward, the bottom run is what is standing still against the
ground, so *relative to the hull* it travels rearward. Getting this backwards
gives a tank that looks like it is being dragged. The sign is derived rather
than typed: the tangent at the lowest node of the loop, pushed through the
object's world matrix, is compared against world rear (+Y). That survives a
belt whose local axes point the other way, which is exactly what the two sides
of a tank tend to do.

Nothing is left modified
------------------------
`set_phase` writes vertex coordinates straight into the mesh and `restore` puts
the rest pose back, so the .blend is untouched once a render finishes. Phase 0
is *also* transported rather than short-cut to the rest pose: the round trip
through the polyline is not bit-exact, and a phase 0 that skipped it would be
the one frame in the cycle with a different shape.
"""

import numpy as np

CONFIG = {
    "belts": ("L.Caterpillar.Geometry", "R.Caterpillar.Geometry"),

    # 8 phases over an 8.8 px pitch is a ~1.1 px step. More buys nothing: past
    # one step per pixel the extra frames repeat. Fewer is a real option on a
    # belt that does not close - see the docstring - because it shrinks the
    # closing hitch relative to an ordinary step.
    "phases": 8,

    # bearings used to trace the loop's mid-band, and the boxcar that takes the
    # tread's own ripple back out of it before it becomes the path
    "angular_bins": 1440,
    "smooth_bins": 41,

    # the path is resampled uniform in arc length: the transport is a slide by
    # distance, so the nodes have to be spaced by distance
    "path_nodes": 1024,
    "search_nodes": 48,          # +/- nodes of the bearing guess, ~2 pitches

    # the straight runs, where the pitch is read: the middle `run_frac` of the
    # length, and the outer `face_frac` of the height, which is the tread face
    "run_frac": 0.60,
    "face_frac": 0.55,
    "run_bins": 900,
    "run_smooth": 61,

    # link counts worth believing on a tank track; the peak is picked inside it
    "link_range": (25, 80),
}


def _coords(me):
    n = len(me.vertices)
    co = np.empty(n * 3, np.float32)
    me.vertices.foreach_get("co", co)
    return co.reshape(n, 3).astype(np.float64)


class Belt:
    """One belt, the loop it lies on, and where each vertex sits along it.

    Everything is done in the object's own local space. The belts carry a
    compound non-uniform scale (0.905, 0.661, 0.856 on MT_PARTS, from two
    levels multiplied together), and an affine map carries the one-pitch slide
    symmetry with it: if the links are evenly spaced in the space they were
    authored in, they stay evenly spaced after the squash. Measuring in world
    space would mix the squash into the pitch for no gain.
    """

    def __init__(self, ob, cfg=None):
        self.cfg = dict(CONFIG, **(cfg or {}))
        self.ob = ob
        self.me = ob.data
        self.rest = _coords(self.me)
        lo, hi = self.rest.min(0), self.rest.max(0)
        # the loop lives in local xz; local y is across the belt and is carried
        # through the transport untouched
        self.centre = 0.5 * (lo + hi)
        self.plane = self.rest[:, [0, 2]] - self.centre[[0, 2]]
        self._trace()
        self._project()
        self._pitch()
        self._sense()

    # -- the loop ----------------------------------------------------------
    def _trace(self):
        cfg = self.cfg
        th = np.arctan2(self.plane[:, 1], self.plane[:, 0])
        r = np.hypot(self.plane[:, 0], self.plane[:, 1])
        nb = cfg["angular_bins"]
        b = np.clip(((th + np.pi) / (2 * np.pi) * nb).astype(int), 0, nb - 1)
        rmax = np.full(nb, -np.inf)
        np.maximum.at(rmax, b, r)
        rmin = np.full(nb, np.inf)
        np.minimum.at(rmin, b, r)
        empty = int((~np.isfinite(rmax)).sum())
        if empty:
            raise RuntimeError(
                "the loop is not star-shaped about its own centre: %d of %d "
                "bearings are empty, so a polar trace cannot find it" % (empty, nb))
        self.band = (rmax - rmin)

        rc = 0.5 * (rmax + rmin)
        k = int(cfg["smooth_bins"]) | 1
        pad = k // 2
        rc = np.convolve(np.concatenate([rc[-pad:], rc, rc[:pad]]),
                         np.ones(k) / k, "same")[pad:-pad]
        tc = (np.arange(nb) + 0.5) / nb * 2 * np.pi - np.pi
        c = np.stack([rc * np.cos(tc), rc * np.sin(tc)], 1)

        seg = np.linalg.norm(np.roll(c, -1, 0) - c, axis=1)
        s = np.concatenate([[0.0], np.cumsum(seg)])
        self.perimeter = float(s[-1])
        m = int(cfg["path_nodes"])
        want = np.arange(m) / m * self.perimeter
        loop = np.vstack([c, c[:1]])
        self.node = np.stack([np.interp(want, s, loop[:, 0]),
                              np.interp(want, s, loop[:, 1])], 1)
        self.ds = self.perimeter / m
        t = np.roll(self.node, -1, 0) - np.roll(self.node, 1, 0)
        t /= np.linalg.norm(t, axis=1)[:, None]
        self.tan = t
        self.nor = np.stack([-t[:, 1], t[:, 0]], 1)

    # -- where every vertex sits on it -------------------------------------
    def _project(self):
        node, nor = self.node, self.nor
        m = len(node)
        ang = np.arctan2(node[:, 1], node[:, 0])
        if not np.all(np.diff(ang) > 0):
            raise RuntimeError("the traced path doubles back on itself in "
                               "bearing, so it cannot be inverted")
        th = np.arctan2(self.plane[:, 1], self.plane[:, 0])
        guess = np.clip(np.interp(th, ang, np.arange(m)).astype(int), 0, m - 1)

        w = int(self.cfg["search_nodes"])
        best = np.full(len(self.plane), np.inf)
        s_at = np.zeros(len(self.plane))
        d_at = np.zeros(len(self.plane))
        for off in range(-w, w + 1):
            j = (guess + off) % m
            a = node[j]
            ab = node[(j + 1) % m] - a
            ap = self.plane - a
            t = np.clip((ap * ab).sum(1) / (ab * ab).sum(1), 0.0, 1.0)
            foot = a + ab * t[:, None]
            gap = self.plane - foot
            d2 = (gap * gap).sum(1)
            hit = d2 < best
            best[hit] = d2[hit]
            s_at[hit] = (j[hit] + t[hit]) * self.ds
            d_at[hit] = (gap[hit] * nor[j[hit]]).sum(1)
        self.s = s_at % self.perimeter
        self.d = d_at
        self.foot_gap = float(np.sqrt(best).max())

    # -- how far apart the links are ---------------------------------------
    def _run_profile(self, sign):
        """The tread face along one straight run, high-passed.

        `sign` picks the ground run (-1) or the top run (+1). Only the outer
        slice of the height is taken, so this is the face with the grousers on
        it and not the sides, the guides or the wheels showing through.
        """
        cfg = self.cfg
        lo, hi = self.rest.min(0), self.rest.max(0)
        half = 0.5 * (hi[0] - lo[0]) * cfg["run_frac"]
        face = 0.5 * (hi[2] - lo[2]) * cfg["face_frac"]
        m = (np.abs(self.rest[:, 0] - self.centre[0]) < half) \
            & (sign * (self.rest[:, 2] - self.centre[2]) > face)
        if m.sum() < 1000:
            return None, 0.0
        x = self.rest[m, 0]
        z = sign * (self.rest[m, 2] - self.centre[2])
        nb = int(cfg["run_bins"])
        b = np.clip(((x - x.min()) / np.ptp(x) * nb).astype(int), 0, nb - 1)
        prof = np.full(nb, -np.inf)
        np.maximum.at(prof, b, z)
        i = np.arange(nb)
        ok = np.isfinite(prof)
        prof = np.interp(i, i[ok], prof[ok])
        k = int(cfg["run_smooth"]) | 1
        pad = k
        ext = np.concatenate([prof[-pad:], prof, prof[:pad]])
        return prof - np.convolve(ext, np.ones(k) / k, "same")[pad:-pad], float(np.ptp(x))

    def _pitch(self):
        """The link pitch, off the tread and cross-checked against the vertices.

        The two disagree - 45.8 from the tread against 47 from the vertex
        density - and the tread is the one that shows. See the module docstring;
        the difference is a belt that walks 9% slower than the ground.
        """
        ns = 4096
        b = np.clip((self.s / self.perimeter * ns).astype(int), 0, ns - 1)
        dens = np.bincount(b, minlength=ns).astype(float)
        f = np.abs(np.fft.rfft(dens - dens.mean()))
        ks = np.arange(len(f))
        lo, hi = self.cfg["link_range"]
        w = (ks >= lo) & (ks <= hi)
        self.density_links = int(ks[w][np.argmax(f[w])])

        found, self.tread_repeat = [], 0.0
        for sign in (-1, 1):
            prof, span = self._run_profile(sign)
            if prof is None:
                continue
            n = len(prof)
            g = np.abs(np.fft.rfft(prof * np.hanning(n)))
            kk = np.arange(len(g))
            # periods between a fiftieth and a fifth of the run: anything
            # outside that is the run's own shape or the mesh's tessellation
            m = (kk >= 5) & (kk <= 30)
            j = int(kk[m][np.argmax(g[m])])
            # the top run answers with its second harmonic, so step down to the
            # fundamental whenever half the winner is nearly as strong
            if j % 2 == 0 and j // 2 >= 5 and g[j // 2] > 0.5 * g[j]:
                j //= 2
            y0, y1, y2 = g[j - 1], g[j], g[j + 1]
            ref = j + 0.5 * (y0 - y2) / (y0 - 2 * y1 + y2)
            found.append((span / ref, prof, span, ref))

        if not found:
            self.links = self.density_links
            self.pitch = self.perimeter / self.links
            self.pitch_from = "vertex density - no straight run to read"
            return

        # the ground run carries more of the belt and is the one on show
        pitch_x, prof, span, ref = found[0]
        self.links = max(1, int(round(self.perimeter / pitch_x)))
        self.pitch = self.perimeter / self.links
        self.pitch_from = "tread relief on the straight run"
        self.raw_links = round(self.perimeter / pitch_x, 2)
        self.run_agreement = ([round(self.perimeter / q[0], 2) for q in found])

        # does the tread actually repeat at that pitch? Near 1.0 is a real
        # track and a cycle that will close; low means an irregular band, and
        # no phase count will hide the seam. This is the number to read first.
        lag = len(prof) / ref                      # samples in one pitch
        x = prof - prof.mean()
        f2 = np.fft.rfft(x)
        kk = np.arange(len(f2))
        y = np.fft.irfft(f2 * np.exp(-2j * np.pi * kk * lag / len(x)), len(x))
        self.tread_repeat = float((x * y).sum() / (x * x).sum())

    # -- which way it has to run -------------------------------------------
    def _sense(self):
        bottom = int(np.argmin(self.node[:, 1]))
        t = self.tan[bottom]                      # local (x, z) at the ground
        mw = np.array(self.ob.matrix_world)[:3, :3]
        world = mw @ np.array([t[0], 0.0, t[1]])
        # the ground run travels rearward - world +Y - when the tank goes forward
        self.forward = 1.0 if world[1] > 0 else -1.0
        self.rear_speed = float(abs(world[1]) / max(np.linalg.norm(world), 1e-12))

    # -- posing ------------------------------------------------------------
    def transport(self, s, d, y, shift=0.0):
        """Put points given in belt coordinates back into the object's space.

        The one place that turns (arc length, offset from the path, across the
        belt) into xyz. Both the animation and the rebuild go through it, which
        is what makes a rebuilt belt close exactly: it is laid out by the same
        map that later slides it.
        """
        m = len(self.node)
        u = ((s + shift) % self.perimeter) / self.ds
        j = u.astype(int) % m
        t = (u - np.floor(u))[:, None]
        a, b = self.node[j], self.node[(j + 1) % m]
        na, nb = self.nor[j], self.nor[(j + 1) % m]
        n = na + (nb - na) * t
        n /= np.linalg.norm(n, axis=1)[:, None]
        xz = a + (b - a) * t + np.asarray(d)[:, None] * n
        out = np.empty((len(u), 3))
        out[:, 0] = xz[:, 0] + self.centre[0]
        out[:, 1] = y
        out[:, 2] = xz[:, 1] + self.centre[2]
        return out

    def coords_at(self, shift):
        return self.transport(self.s, self.d, self.rest[:, 1], shift)

    def set_phase(self, phase, phases):
        """Pose the belt for one phase of the cycle.

        Phase `phases` would be phase 0 again by construction, which is the
        whole point of measuring the pitch.
        """
        self.write(self.coords_at(self.forward * self.pitch * phase / phases))

    def write(self, co):
        self.me.vertices.foreach_set("co", co.astype(np.float32).ravel())
        self.me.update()

    def restore(self):
        self.write(self.rest)

    # -- what it cost ------------------------------------------------------
    def world_pitch(self):
        """One link, in world units - how far the tank moves per cycle.

        Given to the game in world units and not in pixels on purpose. Pixels
        are what the camera fit *produces*, so a pitch in pixels stamped before
        the render would be a second guess at a number the job is about to
        decide. The atlas already carries `units_per_pixel`; dividing is the
        game's business.
        """
        mw = np.array(self.ob.matrix_world)[:3, :3]
        # the pitch runs along the belt; on the ground run that is local x
        return self.pitch * float(np.linalg.norm(mw @ np.array([1.0, 0.0, 0.0])))

    def meta(self):
        """What a game needs to run this belt against the ground."""
        return {"links": int(self.links), "pitch": round(self.world_pitch(), 6),
                "closes_by_construction": getattr(self, "built_closed", False),
                "mesh": self.ob.name}

    def report(self, upp=None):
        mw = np.array(self.ob.matrix_world)[:3, :3]
        scale = float(np.linalg.norm(mw @ np.array([1.0, 0.0, 0.0])))
        rt = np.linalg.norm(self.coords_at(0.0) - self.rest, axis=1)
        rep = {
            "name": self.ob.name,
            "verts": int(len(self.rest)),
            "perimeter_local": round(self.perimeter, 5),
            "links": self.links,
            "links_raw": getattr(self, "raw_links", None),
            "links_by_run": getattr(self, "run_agreement", None),
            "links_by_vertex_density": self.density_links,
            "pitch_from": self.pitch_from,
            "pitch_local": round(self.pitch, 5),
            "pitch_world": round(self.pitch * scale, 5),
            # 1.0 is a track of repeated links, whose cycle closes. Low is an
            # irregular band, whose cycle cannot be made to. None means the
            # belt was laid out closed and the question does not arise.
            "tread_repeat": (None if self.tread_repeat is None
                             else round(self.tread_repeat, 3)),
            "closes_by_construction": getattr(self, "built_closed", False),
            "band_thickness": [round(float(self.band.min()), 4),
                               round(float(np.median(self.band)), 4),
                               round(float(self.band.max()), 4)],
            "offset_range": [round(float(self.d.min()), 4),
                             round(float(self.d.max()), 4)],
            "worst_foot_gap": round(self.foot_gap, 5),
            "rest_roundtrip_max": round(float(rt.max()), 6),
            "rest_roundtrip_p99": round(float(np.percentile(rt, 99)), 6),
            "runs_rearward_along_local_x": self.forward,
            "ground_run_alignment": round(self.rear_speed, 4),
        }
        if upp:
            rep["pitch_px"] = round(self.pitch * scale / upp, 2)
            rep["rest_roundtrip_px"] = round(float(rt.max()) * scale / upp, 3)
        return rep


    # -- adopting a belt whose coordinates are known, not measured ----------
    @classmethod
    def laid_out(cls, ob, path, s, d, pitch, links):
        """A belt built *by* `path`, animated by the same numbers that built it.

        Measuring the rebuilt mesh afresh would trace its own centreline and
        find its own pitch, and those would differ from the ones it was laid
        out with by a hair. A hair is enough: closure is exact only when the
        slide and the layout use the same map. So the coordinates are handed
        over rather than recovered.
        """
        b = cls.__new__(cls)
        b.cfg, b.ob, b.me = path.cfg, ob, ob.data
        b.rest = _coords(ob.data)
        b.centre, b.band = path.centre, path.band
        b.node, b.tan, b.nor = path.node, path.tan, path.nor
        b.ds, b.perimeter = path.ds, path.perimeter
        b.s, b.d, b.pitch, b.links = s, d, pitch, links
        b.forward, b.rear_speed = path.forward, path.rear_speed
        b.foot_gap = 0.0
        b.density_links = path.density_links
        # `tread_repeat` asks how well an irregular band lies on itself, and it
        # is not the question here: this belt closes because it was laid out
        # that way, not because a measurement came out well. Copying the
        # source's figure across would have reported 0.44 for a belt whose
        # closing frame is byte-identical to its first.
        b.tread_repeat = None
        b.built_closed = True
        b.pitch_from = "laid out at perimeter/%d by construction" % links
        return b


def belts(cfg=None):
    """Build a Belt for every belt named in the config that is in the scene."""
    import bpy
    cfg = dict(CONFIG, **(cfg or {}))
    out = []
    for name in cfg["belts"]:
        ob = bpy.data.objects.get(name)
        if ob is not None and ob.type == "MESH":
            out.append(Belt(ob, cfg))
    return out


def measure(cfg=None, upp=None):
    """Measure without rendering, and put everything back."""
    made = belts(cfg)
    try:
        return {"belts": [b.report(upp) for b in made]}
    finally:
        for b in made:
            b.restore()


REBUILD = {
    # The rebuilt belt must not have the original's name as a prefix, and this
    # is not tidiness. `sprite_atlas.by_hints` matches names by *substring*, so
    # a layer told to exclude `L.Caterpillar.Geometry` also excluded
    # `L.Caterpillar.Geometry.Rebuilt` - the replacement vanished with the
    # thing it replaced and the layer quietly rendered the road wheels alone.
    # It read as "the rebuild does not animate", because the wheels do not.
    "drop": ".Geometry",
    "suffix": ".Rebuilt",
    # polygons are the unit of the cut, not shells. A shell is a panel and 263
    # of them straddle more than a whole link - 20% of the vertices - so
    # sorting shells into links by their centroid would replicate those panels
    # on top of themselves. Polygons here are triangles spanning a median of
    # 0.033 of a link, so the cut runs along polygon edges and lands well
    # inside a pixel. Thirteen of the 905,152 are wider than a link, and they
    # are the price.
    "cut_bins": 200,
    "cut_smooth": 9,
}


def rebuilt_name(name, cfg=None):
    """What `rebuild` will call the belt it lays out from `name`."""
    cfg = dict(REBUILD, **(cfg or {}))
    base = name[:-len(cfg["drop"])] if name.endswith(cfg["drop"]) else name
    return base + cfg["suffix"]


def rebuild(path, cfg=None):
    """Lay a new belt out as one link of the old one, repeated round the loop.

    This is the fix for a cycle that will not close, and it closes by
    construction rather than by tuning. Copy k is the base link put at
    `k * pitch`; sliding everything by one pitch puts copy k exactly where copy
    k+1 was, and copy `links-1` onto copy 0 because the perimeter is `links`
    pitches by definition. The set of poses is unchanged, so the picture is
    unchanged, so the last phase *is* the first. No measurement is involved in
    that claim - which is the point, because measuring is what failed.

    What it costs is that the belt becomes regular. Which is what a track is:
    the original is an irregular band whose relief merely averages 46 bumps
    (`tread_repeat` 0.44), and no amount of parametrisation could make a slide
    reproduce it.

    Where the cut goes is measured, not chosen. Every polygon is folded into a
    single pitch and the boundary is put at the emptiest phase - the natural
    gap between links, where the fewest polygons straddle it. On MT_PARTS that
    is 0.855 of the mean density, a shallow minimum, because the links are not
    crisply separated in the first place.

    Nothing is destroyed: the result is a new object beside the original, and
    the original is left alone for the render to exclude or to keep as an A/B.
    """
    import bpy
    cfg = dict(REBUILD, **(cfg or {}))
    src = path.me
    npoly, nloop = len(src.polygons), len(src.loops)
    lstart = np.empty(npoly, np.int32)
    src.polygons.foreach_get("loop_start", lstart)
    ltotal = np.empty(npoly, np.int32)
    src.polygons.foreach_get("loop_total", ltotal)
    lvi = np.empty(nloop, np.int32)
    src.loops.foreach_get("vertex_index", lvi)

    # where each polygon sits on the loop, as a circular mean of its corners
    ang = path.s / path.perimeter * 2 * np.pi
    own = np.repeat(np.arange(npoly), ltotal)
    cx = np.bincount(own, weights=np.cos(ang[lvi]), minlength=npoly)
    cy = np.bincount(own, weights=np.sin(ang[lvi]), minlength=npoly)
    poly_s = (np.arctan2(cy, cx) % (2 * np.pi)) / (2 * np.pi) * path.perimeter

    nb = int(cfg["cut_bins"])
    fold = (poly_s % path.pitch) / path.pitch
    h = np.histogram(fold, bins=nb, range=(0.0, 1.0))[0].astype(float)
    k = int(cfg["cut_smooth"]) | 1
    pad = k // 2
    hs = np.convolve(np.concatenate([h[-pad:], h, h[:pad]]),
                     np.ones(k) / k, "same")[pad:-pad]
    cut = (int(np.argmin(hs)) + 0.5) / nb * path.pitch
    keep = np.floor(((poly_s - cut) % path.perimeter) / path.pitch).astype(int) == 0
    if not keep.any():
        raise RuntimeError("no polygon landed in the base link")

    # the base link's loops, in polygon order, with its vertices from zero
    base_loops = np.concatenate([np.arange(lstart[i], lstart[i] + ltotal[i])
                                 for i in np.nonzero(keep)[0]])
    base_vi = lvi[base_loops]
    used, remap = np.unique(base_vi, return_inverse=True)
    nv, np_ = len(used), int(keep.sum())
    n = int(path.links)

    s0, d0 = path.s[used], path.d[used]
    y0 = path.rest[used, 1]
    co = np.concatenate([path.transport(s0, d0, y0, i * path.pitch)
                         for i in range(n)])

    tot = ltotal[keep]
    vi = (remap[None, :] + (np.arange(n) * nv)[:, None]).ravel()
    starts = np.concatenate([[0], np.cumsum(np.tile(tot, n))[:-1]])

    dst = bpy.data.meshes.new(rebuilt_name(src.name, cfg))
    dst.vertices.add(nv * n)
    dst.loops.add(len(vi))
    dst.polygons.add(np_ * n)
    dst.vertices.foreach_set("co", co.ravel().astype(np.float32))
    dst.loops.foreach_set("vertex_index", vi.astype(np.int32))
    dst.polygons.foreach_set("loop_start", starts.astype(np.int32))
    dst.polygons.foreach_set("loop_total", np.tile(tot, n).astype(np.int32))
    dst.update(calc_edges=True)

    for mat in src.materials:
        dst.materials.append(mat)
    if len(src.materials) > 1:
        mi = np.empty(npoly, np.int32)
        src.polygons.foreach_get("material_index", mi)
        dst.polygons.foreach_set("material_index", np.tile(mi[keep], n).astype(np.int32))
    for layer in src.uv_layers:
        uv = np.empty(nloop * 2, np.float32)
        layer.data.foreach_get("uv", uv)
        dst.uv_layers.new(name=layer.name).data.foreach_set(
            "uv", np.tile(uv.reshape(-1, 2)[base_loops].ravel(), n))
    smooth = np.empty(npoly, bool)
    src.polygons.foreach_get("use_smooth", smooth)
    dst.polygons.foreach_set("use_smooth", np.tile(smooth[keep], n))
    dst.update()

    name = rebuilt_name(path.ob.name, cfg)
    old = bpy.data.objects.get(name)
    if old is not None:
        data = old.data
        bpy.data.objects.remove(old, do_unlink=True)
        if data.users == 0:
            bpy.data.meshes.remove(data)
    ob = bpy.data.objects.new(name, dst)
    for coll in path.ob.users_collection:
        coll.objects.link(ob)
    ob.parent = path.ob.parent
    ob.matrix_parent_inverse = path.ob.matrix_parent_inverse.copy()
    ob.matrix_world = path.ob.matrix_world.copy()

    s = (np.tile(s0, n) + np.repeat(np.arange(n) * path.pitch, nv)) % path.perimeter
    made = Belt.laid_out(ob, path, s, np.tile(d0, n), path.pitch, n)
    made.built = {
        "from": path.ob.name,
        "links": n,
        "cut_at": round(float(cut), 5),
        "cut_density_vs_mean": round(float(hs.min() / hs.mean()), 3),
        "base_link": {"verts": nv, "polys": np_},
        "verts": nv * n, "polys": np_ * n,
        "source": {"verts": len(path.rest), "polys": npoly},
        "polys_per_link_in_source": [int(x) for x in (
            np.bincount(np.floor(((poly_s - cut) % path.perimeter)
                                 / path.pitch).astype(int), minlength=n).min(),
            np.bincount(np.floor(((poly_s - cut) % path.perimeter)
                                 / path.pitch).astype(int), minlength=n).max())],
    }
    return made


def hook(made, denom=None):
    """A phase hook that poses these belts.

    `denom` overrides the phase count the shift is divided by. That is what
    tests the seam: render `phases + 1` frames against a denominator of
    `phases`, and the extra frame is a slide of one whole pitch, which has to
    come out identical to phase 0. Comparing the two PNGs is the only honest
    answer to "does the cycle close" - the spectrum has an opinion and the
    picture has the facts.
    """
    def run(phase, phases):
        n = denom or phases
        for b in made:
            b.set_phase(phase, n)
    return run
