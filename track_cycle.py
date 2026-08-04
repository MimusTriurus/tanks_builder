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


def _resample(c, m):
    """A closed polyline as `m` nodes spaced evenly by arc length, with frames.

    The transport is a slide by *distance*, so the nodes have to be spaced by
    distance and not by whatever parameter drew them. Shared by the traced loop
    and by `rounded_loop`, so that a synthetic path is the same kind of object as
    a measured one and `transport` cannot tell them apart.
    """
    seg = np.linalg.norm(np.roll(c, -1, 0) - c, axis=1)
    s = np.concatenate([[0.0], np.cumsum(seg)])
    perimeter = float(s[-1])
    want = np.arange(m) / m * perimeter
    loop = np.vstack([c, c[:1]])
    node = np.stack([np.interp(want, s, loop[:, 0]),
                     np.interp(want, s, loop[:, 1])], 1)
    t = np.roll(node, -1, 0) - np.roll(node, 1, 0)
    t /= np.linalg.norm(t, axis=1)[:, None]
    return perimeter, node, perimeter / m, t, np.stack([-t[:, 1], t[:, 0]], 1)


def rounded_loop(length, height, corner=1.0, samples=8192, unsquash=None):
    """A rounded rectangle, as a dense closed polyline about its own centre.

    `corner` is the corner radius as a fraction of *half the height*, so 1.0 is
    a stadium - semicircular ends - and the round part follows the height on its
    own, with no second number to keep in step with it. 0 would be a sharp
    rectangle, which no belt can wear: `Belt.on_loop` checks the radius against
    the band's own half-thickness, because a band thicker than the arc it turns
    has its inner surface folded through the arc's centre.

    Ordered by increasing bearing about the centre - anticlockwise from the rear,
    down first - which is the order `_trace` produces. That is not cosmetic: `d`
    was measured against a normal built from that order, and its sign has to keep
    meaning the same side of the band.

    `unsquash` is a pair of axis scales to divide the result by, which is how a
    shape built in the proportions the render sees is handed back in the mesh's
    own space. The arcs then come out as ellipses here in order to be circles
    there - see `Belt.on_loop`'s `in_world`.

    Returns the polyline and the analytic perimeter *in the space it was built
    in*, which is not the local perimeter once `unsquash` has had its say. The
    polyline's own measured perimeter comes out a hair under the analytic one,
    chords cutting the arcs, and the two are reported side by side rather than
    asserted equal.
    """
    a, b = 0.5 * float(length), 0.5 * float(height)
    r = min(max(float(corner), 0.0) * b, a, b)
    ax, bz = a - r, b - r                     # centres of the corner arcs
    perim = 4.0 * (ax + bz) + 2.0 * np.pi * r
    n = max(int(samples), 64)

    def line(p0, p1):
        ln = float(np.linalg.norm(np.subtract(p1, p0)))
        if ln <= 1e-12:                       # a stadium has no straight ends
            return np.zeros((0, 2))
        k = max(2, int(round(n * ln / perim)))
        t = np.linspace(0.0, 1.0, k, endpoint=False)[:, None]
        return np.asarray(p0, float) + (np.asarray(p1, float) - p0) * t

    def arc(cx, cz, a0, a1):
        if r <= 1e-12:                        # a sharp rectangle has no arcs
            return np.zeros((0, 2))
        k = max(2, int(round(n * abs(a1 - a0) * r / perim)))
        th = np.linspace(a0, a1, k, endpoint=False)
        return np.stack([cx + r * np.cos(th), cz + r * np.sin(th)], 1)

    pieces = [
        line((-a, 0.0), (-a, -bz)),
        arc(-ax, -bz, np.pi, 1.5 * np.pi),
        line((-ax, -b), (ax, -b)),
        arc(ax, -bz, 1.5 * np.pi, 2.0 * np.pi),
        line((a, -bz), (a, bz)),
        arc(ax, bz, 0.0, 0.5 * np.pi),
        line((ax, b), (-ax, b)),
        arc(-ax, bz, 0.5 * np.pi, np.pi),
        line((-a, bz), (-a, 0.0)),
    ]
    c = np.vstack([p for p in pieces if len(p)])
    if unsquash is not None:
        c = c / np.asarray(unsquash, float)
    # the walk starts on the -x axis, where `atan2` answers +pi and not -pi, so
    # the first point alone sits at the far end of the bearing range and
    # `_project`'s "strictly increasing in bearing" reads false on a loop that is
    # perfectly star-shaped. Rolling the seam to the end makes the invariant read
    # what it is. It moves s=0 by one dense sample - a twentieth of a pixel - and
    # s=0 stays where the trace puts it, at the -x end of the loop, mid-band,
    # which is what keeps a reshape from rotating the material round the belt.
    return np.roll(c, -1, 0), float(perim)


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

        self.perimeter, self.node, self.ds, self.tan, self.nor = \
            _resample(c, int(cfg["path_nodes"]))

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
            # present only on a belt whose loop was replaced rather than traced
            "shaped": getattr(self, "shaped", None),
        }
        if upp:
            rep["pitch_px"] = round(self.pitch * scale / upp, 2)
            rep["rest_roundtrip_px"] = round(float(rt.max()) * scale / upp, 3)
        return rep


    # -- the same material, carried by a different loop ---------------------
    def on_loop(self, length=None, height=None, corner=1.0, keep="ground",
                in_world=False, nodes=None, samples=8192):
        """This belt's own material, carried by a rounded-rectangle loop.

        The shape of a belt lives in exactly one place - `self.node` - because
        `transport` is the only route from belt coordinates back to xyz. So
        reshaping it is replacing that loop and nothing else: every vertex keeps
        its arc length along the band, its offset from the surface and its
        position across it, which leaves the tread relief, the cross-section, the
        material and the UVs untouched by construction rather than by care.

        `length` and `height` are the loop's own local extents and default to
        what it has now. They are the *path's* extents, not the mesh bbox, which
        is half a band's thickness wider on every side - about 0.02 here. The
        corner radius is a fraction of half the height, so the round ends follow
        the height on their own.

        `keep` decides what stays put when the height changes: "ground" holds the
        bottom run where it is, which is what the tank stands on and what the
        tile check measures, and "centre" holds the middle instead. Nothing holds
        the road wheels, which are a separate mesh this knows nothing about.

        `in_world` is what makes the round ends round *in the frame*, and it
        exists because they are not otherwise. The belt carries a non-uniform
        scale - measured 0.944 along the loop against 0.665 up on LT_PARTS, a
        ratio of 0.704 - so a circle in the mesh's own space arrives as an
        ellipse flattened by 30%: a radius of 0.175 renders 26.3 px along the
        belt and 18.5 px up. That is a property of every belt in the project, the
        scale sitting on the root and on the mesh long before any reshape.

        With it on, the rectangle is built in the proportions the render sees and
        handed back divided by those two scales, so the arc is an ellipse here in
        order to be a circle there. `length` and `height` are then world units
        too, which is the other half of the argument: world units divide by
        `units_per_pixel` into pixels, and the mesh's own units convert to
        nothing at all.

        The two scales are read off `matrix_world`, so this is only right while
        the belt is parented the way the scene says. An unparented belt reports
        its own local matrix instead and the squash silently reads as 1.

        Hand the result to `rebuild(src, onto=...)`. It carries this belt's mesh
        and measurements rather than remeasuring anything, for the same reason
        `laid_out` is handed its coordinates: closure is exact only when the
        layout and the slide use the same map.

        It is poseable, and worth posing to see the new silhouette without
        rebuilding 679k vertices first - but its cycle is the *source's* cycle,
        stretched along with it, so it closes no better than the source did.
        Judge the shape on it and the seam on what `rebuild` returns.
        """
        cur = self.node
        mw = np.array(self.ob.matrix_world)[:3, :3]
        # the loop lives in local xz, so these are the images of those two axes
        scale = np.array([float(np.linalg.norm(mw[:, 0])),
                          float(np.linalg.norm(mw[:, 2]))]) \
            if in_world else np.array([1.0, 1.0])
        if length is None:
            length = float(np.ptp(cur[:, 0])) * scale[0]
        if height is None:
            height = float(np.ptp(cur[:, 1])) * scale[1]
        c, analytic = rounded_loop(length, height, corner, samples,
                                   unsquash=(scale if in_world else None))

        # a band thicker than the arc it turns folds through the arc's centre, so
        # the radius is checked against the offsets actually in the mesh. What has
        # to be checked is the *local* curvature: with `in_world` the arc is an
        # ellipse here, and an ellipse turns tightest at the end of its major
        # axis. Written as one expression rather than two branches because a
        # circle is the case where the two semi-axes are equal.
        r = min(max(float(corner), 0.0) * 0.5 * height, 0.5 * length, 0.5 * height)
        lo, hi = sorted((r / scale[0], r / scale[1]))
        tight = (lo * lo / hi) if hi > 0 else 0.0
        thick = float(np.percentile(np.abs(self.d), 99.9))
        if tight <= thick:
            raise RuntimeError(
                "the loop turns in %.4f at its tightest and cannot carry a band "
                "%.4f thick: the inner surface would fold through the centre of "
                "the arc. Raise `corner`, or the height it is a fraction of."
                % (tight, thick))

        # where it sits is taken off the two polylines rather than from `height`,
        # which would assume the built loop is centred - true of a circle, and
        # something to re-derive for every shape that is not
        if keep == "ground":
            dz = float(cur[:, 1].min() - c[:, 1].min())
        elif keep == "centre":
            dz = float(0.5 * (cur[:, 1].min() + cur[:, 1].max())
                       - 0.5 * (c[:, 1].min() + c[:, 1].max()))
        else:
            raise RuntimeError("keep is 'ground' or 'centre', not %r" % (keep,))
        dx = float(0.5 * (cur[:, 0].min() + cur[:, 0].max())
                   - 0.5 * (c[:, 0].min() + c[:, 0].max()))
        c = c + np.array([dx, dz])

        b = self.__class__.__new__(self.__class__)
        b.cfg, b.ob, b.me, b.rest = self.cfg, self.ob, self.me, self.rest
        b.centre, b.band, b.plane = self.centre, self.band, self.plane
        b.density_links, b.tread_repeat = self.density_links, self.tread_repeat
        b.foot_gap, b.built_closed = self.foot_gap, False
        b.perimeter, b.node, b.ds, b.tan, b.nor = \
            _resample(c, int(nodes or self.cfg["path_nodes"]))
        # the material keeps its *fraction* of the way round, which is what makes
        # this a reshape and not a slide. The link count is what follows the new
        # perimeter, at the pitch the tread already has - a track shoe is a
        # physical part, so a longer belt carries more of them and not longer
        # ones. Keeping the pitch is also what keeps the game's number, and hence
        # the bench's slip, comparable across a reshape.
        b.s = self.s * (b.perimeter / self.perimeter)
        b.d = self.d
        b.links = max(1, int(round(b.perimeter / self.pitch)))
        b.pitch = b.perimeter / b.links
        b.pitch_from = "rounded loop at perimeter/%d, pitch kept from %s" % (
            b.links, self.pitch_from)
        b._sense()
        b.shaped = {
            # `space` says what units `length`, `height` and `corner_radius` are
            # in, and there is no reading the rest of this without it
            "space": "world" if in_world else "mesh",
            "length": round(float(length), 5), "height": round(float(height), 5),
            "corner_frac": round(float(corner), 4),
            "corner_radius": round(float(r), 5),
            "axis_scale_along_up": ([round(float(v), 5) for v in scale]
                                    if in_world else None),
            "keep": keep,
            "was": {"length_mesh": round(float(np.ptp(cur[:, 0])), 5),
                    "height_mesh": round(float(np.ptp(cur[:, 1])), 5),
                    "perimeter": round(self.perimeter, 5),
                    "links": int(self.links),
                    "pitch": round(self.pitch, 5)},
            "length_mesh": round(float(np.ptp(b.node[:, 0])), 5),
            "height_mesh": round(float(np.ptp(b.node[:, 1])), 5),
            "perimeter": round(b.perimeter, 5),
            "perimeter_analytic_in_that_space": round(analytic, 5),
            "links": b.links, "pitch": round(b.pitch, 5),
            "tightest_local_radius": round(float(tight), 5),
            "band_half_thickness": round(thick, 5),
        }
        return b

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
        b.shaped = getattr(path, "shaped", None)
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


def rebuild(path, cfg=None, onto=None):
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

    `onto` lays the links on a *different* loop from the one they were cut off -
    see `Belt.on_loop`. The cut still reads the source's own arc length and its
    own pitch, so the base link is one true shoe of the delivered belt, and only
    the layout moves; the count follows the new perimeter at that pitch. Closure
    survives untouched, and it is worth seeing why: it needs the copies to sit at
    multiples of `perimeter / n` and nothing else. Whether the shoe is exactly
    that long never entered the argument, so a shoe that over- or under-runs its
    slot by the rounding - here at most a pitch in `n`, well under a tenth of a
    pixel - closes exactly the same.

    Left as None it is the source's own loop, and every number below reduces to
    what it was: `n` is `links` because the pitch is defined as perimeter over
    links, so a scene that does not reshape renders byte for byte as before.
    """
    import bpy
    cfg = dict(REBUILD, **(cfg or {}))
    onto = onto if onto is not None else path
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
    # as many shoes as the loop being laid on has room for, at the pitch the
    # tread already has. `onto is path` makes this `links` exactly.
    n = max(1, int(round(onto.perimeter / path.pitch)))
    step = onto.perimeter / n

    s0, d0 = path.s[used], path.d[used]
    y0 = path.rest[used, 1]
    co = np.concatenate([onto.transport(s0, d0, y0, i * step)
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

    s = (np.tile(s0, n) + np.repeat(np.arange(n) * step, nv)) % onto.perimeter
    made = Belt.laid_out(ob, onto, s, np.tile(d0, n), step, n)
    made.built = {
        "from": path.ob.name,
        "onto": (None if onto is path else getattr(onto, "shaped", "a given loop")),
        "links": n,
        "cut_at": round(float(cut), 5),
        "cut_density_vs_mean": round(float(hs.min() / hs.mean()), 3),
        "base_link": {"verts": nv, "polys": np_},
        "verts": nv * n, "polys": np_ * n,
        "source": {"verts": len(path.rest), "polys": npoly},
        "polys_per_link_in_source": [int(x) for x in (
            # the source's own links, so `path.links` and not `n`: with a
            # reshape those differ, and padding to the larger would report an
            # empty link that does not exist
            np.bincount(np.floor(((poly_s - cut) % path.perimeter)
                                 / path.pitch).astype(int),
                        minlength=int(path.links)).min(),
            np.bincount(np.floor(((poly_s - cut) % path.perimeter)
                                 / path.pitch).astype(int),
                        minlength=int(path.links)).max())],
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


PREVIEW = {
    # Set these and run this file - Alt+P in Blender's Text Editor - and the new
    # shape is in the viewport at once, with no render at all.
    #
    # It builds a `<belt>.Preview` object beside each belt and hides the belt
    # itself with the eye, rather than writing into the belt. That is not
    # tidiness, it is what makes iterating possible at all. Measured: a preview
    # written into the source and then previewed again drifts 0.81px, because `d`
    # is re-projected onto a different curve every pass and the band smears - the
    # link count came out 47 and then 46, and the band thickened 18%. Starting
    # from the untouched source each time is exact instead.
    #
    # The preview objects sit at the scene root, parented to nothing, so no
    # render layer can see them: the track layers target `Track.*.World` and pull
    # in descendants, and these are deliberately not descendants.
    #
    # The .blend does come up modified. `{"restore": True}` removes the previews
    # and unhides the belts, and unlike a rest pose it survives a reload, because
    # what it undoes is objects in the scene rather than a copy held in memory.
    "belts": CONFIG["belts"],
    "length": None,          # None is the belt as delivered, in the space below
    "height": None,
    "corner": None,          # radius as a fraction of half the height; None is 1.0,
                             # or whatever the preview being rescaled already had
    "in_world": True,        # round in the frame, and the two sizes in world units
    "keep": "ground",
    "restore": False,
    "suffix": ".Preview",

    # Take the two sizes off the preview object's own scale instead of typing
    # them: run `preview()`, grab the `.Preview` object in the viewport, `S` `Y`
    # for the length and `S` `Z` for the height - the tank's long axis is world Y
    # and both map exactly onto the loop's own axes through the root's 90 degrees
    # - or type them into the sidebar. Then run again with this on.
    #
    # While you are dragging, what you see is a *proposal* and not a belt: the
    # scale is stretching the tread and the band along with the loop. This turns
    # it into the real thing - the shoes back to their true size, the ends back to
    # true arcs at the radius the new height asks for - and resets the scale, so
    # the next drag starts from 1 again.
    #
    # Scaling the belt itself would be the same proposal with no way back, which
    # is why it is the preview that carries the handle.
    "take_scale": False,
    # the last render's `units_per_pixel`, so the readout can be in pixels. None
    # prints world units, because a pixel size is a thing the render decides and
    # this is not the place to guess at it.
    "units_per_pixel": None,
}

# stamped on the preview object, not on the belt: [length, height, corner,
# in_world] is what this preview was asked for, and the object scale it was built
# with is the baseline a later drag is measured against. Both die with the
# preview, which is the point - nothing about a shape being tried out belongs on
# the asset.
SHAPE = "_loop_shape"
BASE = "_loop_base_scale"


def preview(cfg=None):
    """Show a shape in the viewport without rendering anything.

    The whole point is the length of the loop between changing a number and
    seeing it: a render is two minutes and a shape is a judgement. So the belt's
    own material is transported onto the new loop into a copy beside it, where
    the viewport shows it at any zoom with its texture on, and the road wheels
    stay put next to it so the fit is visible too.

    What this cannot answer is the seam. It poses the *source's* material, so its
    cycle is the source's cycle; closure comes from `rebuild`, and the picture
    that shows it is the one `parts_render.check` writes.

    `corner_px_along_up` is the readout worth watching: the corner radius along
    the belt and up, in pixels, and with `in_world` the two are equal. That
    equality is the flag working - in the mesh's own space they stand 26.3 to
    18.5, which is the same circle flattened by 30%.
    """
    import bpy
    cfg = dict(PREVIEW, **(cfg or {}))
    upp = cfg["units_per_pixel"]
    rows = []
    for name in cfg["belts"]:
        src = bpy.data.objects.get(name)
        if src is None or src.type != "MESH":
            continue
        old = bpy.data.objects.get(name + cfg["suffix"])

        length, height = cfg["length"], cfg["height"]
        corner, in_world, took = cfg["corner"], cfg["in_world"], None
        if cfg["take_scale"] and not cfg["restore"]:
            if old is None or SHAPE not in old:
                raise RuntimeError(
                    "there is no scale to take: run preview(), scale %r in the "
                    "viewport, then run this" % (name + cfg["suffix"],))
            was_l, was_h, was_c, was_w = (float(v) for v in old[SHAPE])
            base = [float(v) for v in old[BASE]]
            mult = [old.scale[i] / base[i] for i in range(3)]
            length, height = was_l * mult[0], was_h * mult[2]
            if corner is None:
                corner = was_c
            # the units of the sizes just taken are the ones they were stamped
            # in, so this is not the caller's to choose here
            in_world = bool(was_w)
            took = {"along_up": [round(mult[0], 4), round(mult[2], 4)],
                    # the band's width across the belt is not a loop dimension:
                    # the loop is a curve, and how wide the tread sits on it is
                    # the mesh's business
                    "across_ignored": round(mult[1], 4),
                    "in_world_from_the_preview": in_world}

        # any earlier preview goes now and always: the source carries the
        # delivered shape and every preview has to start from it
        if old is not None:
            data = old.data
            bpy.data.objects.remove(old, do_unlink=True)
            if data.users == 0:
                bpy.data.meshes.remove(data)
        src.hide_set(not cfg["restore"])
        if cfg["restore"]:
            rows.append({"belt": name, "preview_removed": old is not None,
                         "unhidden": True})
            continue

        b = Belt(src, cfg)
        onto = b.on_loop(length=length, height=height,
                         corner=1.0 if corner is None else corner,
                         keep=cfg["keep"], in_world=in_world)
        me = src.data.copy()
        me.name = name + cfg["suffix"]
        me.vertices.foreach_set(
            "co", onto.coords_at(0.0).astype(np.float32).ravel())
        me.update()
        ob = bpy.data.objects.new(name + cfg["suffix"], me)
        for coll in src.users_collection:
            coll.objects.link(ob)
        # no parent on purpose - see the note in PREVIEW. The placement comes
        # across as a world matrix instead, which is the same trick `rebuild`
        # uses in the other direction.
        ob.matrix_world = src.matrix_world.copy()
        # what this preview asked for, and the scale it starts from, so that a
        # drag can be read as a multiple of it. `matrix_world` above leaves the
        # object's scale at the belt's own non-unit one, so 1.0 is the wrong
        # baseline and the baseline has to be recorded rather than assumed.
        ob[SHAPE] = [float(onto.shaped["length"]), float(onto.shaped["height"]),
                     float(1.0 if corner is None else corner),
                     1.0 if in_world else 0.0]
        ob[BASE] = [float(v) for v in ob.scale]

        row = dict(onto.shaped, belt=name, preview=ob.name)
        if took is not None:
            row["took_scale"] = took
        if upp:
            mw = np.array(src.matrix_world)[:3, :3]
            sa = float(np.linalg.norm(mw[:, 0]))
            su = float(np.linalg.norm(mw[:, 2]))
            r = onto.shaped["corner_radius"]
            row["corner_px_along_up"] = (
                [round(r / upp, 2)] * 2 if onto.shaped["space"] == "world"
                else [round(r * sa / upp, 2), round(r * su / upp, 2)])
            row["extent_px"] = [round(onto.shaped["length_mesh"] * sa / upp, 1),
                                round(onto.shaped["height_mesh"] * su / upp, 1)]
            row["pitch_px"] = round(onto.pitch * sa / upp, 2)
        rows.append(row)
    return {"restored" if cfg["restore"] else "previewed": rows,
            "the_blend_is_now_modified": True,
            "put_it_back_with": "track_cycle.preview({'restore': True})"}


if __name__ == "__main__":
    import json
    print(json.dumps(preview(), indent=1, default=str))
