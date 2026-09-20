using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The concrete capon: a tank shelter standing on every edge of its cell.
///
/// Five sides of concrete on the hexagon's own edges, the sixth left open as
/// the gate, and the side facing the gate carrying a slit for the gun. A roof
/// closes it, so a tank parked inside shows nothing but the barrel through the
/// slit. It is laid as a <see cref="WallKit.Plan"/> and nothing else, so the
/// brick wall's solver, its fade and its section bookkeeping (<c>WallRig</c>)
/// and its drawing (<c>WallStack</c>) take it as they take masonry.
///
/// <b>Every number is a fraction of the cell's circumradius</b>, and every one
/// of them is the Blender model's (<c>pipeline/hex_capon.py</c>), which was
/// measured against all five tank sets: the slit passes every gun at rest, the
/// roof clears the tallest turret by 0.09, and the walls stand on the cell
/// boundary itself with no setback - the tank does not turn inside, so the
/// interior is judged against a hull and not a turning circle.
///
/// <b>The walls are boxes and the roof is six prisms, and that is the one
/// place this prop is not a brick wall.</b> A piece on this board is
/// <c>(Seat, Half, Turn)</c> - see <see cref="WallKit.Block"/> - and a slab on
/// a hexagon's edge is exactly that. A roof over a hexagon is not: it is six
/// congruent triangles about the centre, one over each wall, so that when a
/// wall goes the triangle it carried goes with it. They travel as
/// <see cref="WallKit.Block.Hull"/> - six corners in the piece's own frame -
/// and the box fields stay filled with a bounding box, so everything that
/// reasons about a piece as a box is conservative rather than wrong.
///
/// <b>Corners are lap joints, not mitres.</b> <c>WallKit.Mitre</c> shortens a
/// box behind the kite plane, which on a 0.12-thick slab leaves a wedge of
/// daylight at every corner; a brick wall hides it with two leaves, a single
/// slab cannot. So at every corner one side runs to the vertex and the next
/// stops where the first one's inner face crosses its own, which for a 120
/// degree corner is <c>sqrt(3) * thickness</c> back along its edge - exact,
/// touching, and never overlapping, which for two rigid bodies is the only
/// thing that matters. The two sides flanking the gate both run long, so the
/// gate reads as an opening with square jambs; the slit side runs long at both
/// ends so the slit keeps the width the model gave it.
///
/// Frame: the prop's own, the flat plane's <c>(x, lift, y)</c> with Y up.
/// Side <c>k</c> has its outward normal at <c>60k</c> degrees about Y from +Z;
/// the slit faces +Z, the gate faces -Z, and <c>WallProp.Lay</c> turns that
/// onto the board's bearing the way it turns the wall.
/// </summary>
public static class CaponKit
{
    public sealed class Recipe
    {
        public int Seed = 3;
        /// <summary>Outer hexagon as a fraction of the cell. 1.0 stands the
        /// walls on the cell boundary itself, which is the board's decision.
        /// </summary>
        public float Reach = 1.0f;
        public float WallThick = 0.12f;
        /// <summary>The roof's underside. The tallest turret of the five sets
        /// tops out at 1.06.</summary>
        public float WallHigh = 1.15f;
        public float RoofThick = 0.12f;
        /// <summary>The slit, bottom and top. Muzzles run 0.51 (TDP) to 0.71
        /// (HMP) with barrels up to 0.12 across; every gun passes at rest.
        /// </summary>
        public float SlitLow = 0.40f, SlitHigh = 0.90f;
        /// <summary>Fraction of the slit side left as jamb at each end.</summary>
        public float SlitJamb = 0.13f;
        /// <summary>A beam across the top of the gate, or none. None by
        /// default: the roof's own edge is the lintel, and a beam 0.20 deep
        /// under a 1.15 roof hangs at 0.95, which the medium's turret (1.06)
        /// would strike driving in - and its centre sits on the gate's edge
        /// ray, so the board would read the gate as walled.</summary>
        public float Lintel = 0.0f;
        /// <summary>Pieces along and up a blank side.</summary>
        public int Bays = 2, Courses = 2;
        /// <summary>How far a split wanders from even, fraction of its span.
        /// Concrete cracks where it cracks; a grid reads as tiles.</summary>
        public float Jitter = 0.18f;
        /// <summary>Air between pieces. Two hulls laid touching jitter apart on
        /// the first frame; this is the brick wall's <c>Perpend</c>.</summary>
        public float Gap = 0.004f;
    }

    /// <summary>Which side carries the slit and which is open. Side 0 faces
    /// +Z, the shooter's side in the prop frame; the gate is opposite.</summary>
    public const int SlitSide = 0, GateSide = 3;

    /// <summary>The prop's sides, in order. Written once here because it is
    /// what <see cref="WallKit.Block.Side"/> means for this prop, and the rig
    /// brings a section - wall and the roof triangle over it - down by it.
    /// </summary>
    public const int Sides = 6;

    private static float Roll(int seed, int index, int salt) =>
        WallKit.Roll(seed, index, salt);

    /// <summary>Normal of side <paramref name="k"/> in the prop frame.</summary>
    public static Vector3 Normal(int k)
    {
        float a = Mathf.DegToRad(60.0f * k);
        return new Vector3(Mathf.Sin(a), 0.0f, Mathf.Cos(a));
    }

    private static Basis Turn(int k) => new(Vector3.Up, Mathf.DegToRad(60.0f * k));

    /// <summary>n-1 cut fractions between 0 and 1, evenly spaced then
    /// wandered by the seed.</summary>
    private static float[] Splits(int n, int seed, int salt, float jitter)
    {
        var cuts = new float[n + 1];
        cuts[0] = 0.0f;
        cuts[n] = 1.0f;
        for (int i = 1; i < n; i++)
            cuts[i] = (i + (Roll(seed, i, salt) * 2.0f - 1.0f) * jitter) / n;
        Array.Sort(cuts);
        return cuts;
    }

    public static WallKit.Plan Lay(Recipe r)
    {
        var plan = new WallKit.Plan
        {
            Sides = Sides,
            Courses = r.Courses,
            Leaves = 1,
            Concrete = true,
        };
        float R = r.Reach;                              // circumradius of the box
        float apothem = Mathf.Sqrt(3.0f) * 0.5f * R;    // outer faces sit here
        float t = r.WallThick;
        float H = r.WallHigh;
        // The lap at a corner: where the long side's inner face crosses the
        // short side's, measured back along the short side's edge.
        float lap = Mathf.Sqrt(3.0f) * t;
        float gap = r.Gap;
        int n = 0;

        void Box(int side, float x0, float x1, float y0, float y1, bool window)
        {
            if (x1 - x0 <= gap || y1 - y0 <= gap)
                return;
            Basis turn = Turn(side);
            var half = new Vector3((x1 - x0) * 0.5f - gap * 0.5f,
                                   (y1 - y0) * 0.5f - gap * 0.5f,
                                   t * 0.5f - gap * 0.5f);
            Vector3 local = new((x0 + x1) * 0.5f, (y0 + y1) * 0.5f, apothem - t * 0.5f);
            plan.Blocks.Add(new WallKit.Block
            {
                Seat = turn * local,
                Half = half,
                Turn = turn,
                Tone = 0.35f + 0.4f * Roll(r.Seed, n, 71),
                Moss = 0.0f,
                Course = Mathf.Clamp((int)(y0 / H * r.Courses), 0, r.Courses - 1),
                Chip = false,
                Side = side,
                Window = window,
            });
            n++;
        }

        for (int k = 0; k < Sides; k++)
        {
            // Long at the +X end and cut at the -X end by default. Two
            // exceptions, both so that an opening keeps its full width: the
            // slit side runs long at both ends (its -X neighbour gives way
            // instead), and the gate's +X neighbour runs long into the empty
            // gate. A jamb narrower than the lap would otherwise never be
            // built - measured, the slit's left jamb came out with negative
            // width and was silently dropped.
            bool cutStart = k != (GateSide + 1) % Sides && k != SlitSide;
            bool cutEnd = k == GateSide || (k + 1) % Sides == SlitSide;
            float xs = -R * 0.5f + (cutStart ? lap : 0.0f);
            float xe = R * 0.5f - (cutEnd ? lap : 0.0f);
            if (k == GateSide)
            {
                if (r.Lintel <= 0.0f)
                    continue;
                float[] u = Splits(r.Bays, r.Seed, 10 + k, r.Jitter);
                for (int b = 0; b < r.Bays; b++)
                    Box(k, Mathf.Lerp(xs, xe, u[b]), Mathf.Lerp(xs, xe, u[b + 1]),
                        H - r.Lintel, H, false);
            }
            else if (k == SlitSide)
            {
                float w = R * (1.0f - 2.0f * r.SlitJamb);
                float[] zs = Splits(r.Courses, r.Seed, 30 + k, r.Jitter);
                for (int c = 0; c < r.Courses; c++)
                {
                    Box(k, xs, -w * 0.5f, H * zs[c], H * zs[c + 1], false);
                    Box(k, w * 0.5f, xe, H * zs[c], H * zs[c + 1], false);
                }
                Box(k, -w * 0.5f, w * 0.5f, 0.0f, r.SlitLow, true);
                Box(k, -w * 0.5f, w * 0.5f, r.SlitHigh, H, true);
            }
            else
            {
                float[] u = Splits(r.Bays, r.Seed, 10 + k, r.Jitter);
                float[] zs = Splits(r.Courses, r.Seed, 30 + k, r.Jitter);
                for (int b = 0; b < r.Bays; b++)
                for (int c = 0; c < r.Courses; c++)
                    Box(k, Mathf.Lerp(xs, xe, u[b]), Mathf.Lerp(xs, xe, u[b + 1]),
                        H * zs[c], H * zs[c + 1], false);
            }
        }

        // The roof: one triangle over each side, from the centre to the side's
        // two corners. All six are the same triangle turned, which is what lets
        // the stack draw them off one mesh; the corners are handed over in the
        // piece's own frame about its centroid.
        Vector3[] kite = Kite(R, r.RoofThick, gap);
        float roofMid = H + r.RoofThick * 0.5f;
        Vector3 centroid = new(0.0f, 0.0f, 2.0f * apothem / 3.0f);
        for (int k = 0; k < Sides; k++)
        {
            Basis turn = Turn(k);
            plan.Blocks.Add(new WallKit.Block
            {
                Seat = turn * centroid + Vector3.Up * roofMid,
                // A bounding box about the centroid, conservative: the triangle
                // reaches 2/3 of the apothem one way and 1/3 the other.
                Half = new Vector3(R * 0.5f, r.RoofThick * 0.5f, 2.0f * apothem / 3.0f),
                Turn = turn,
                Tone = 0.45f + 0.3f * Roll(r.Seed, n, 71),
                Moss = 0.0f,
                Course = r.Courses,
                Chip = false,
                Side = k,
                Hull = kite,
            });
            n++;
        }

        plan.Top = H + r.RoofThick;
        plan.Size = new Vector3(2.0f * R, plan.Top, 2.0f * apothem);
        plan.Reach = WallKit.Reach(plan.Blocks);
        return plan;
    }

    /// <summary>The roof triangle over side 0, about its own centroid: the
    /// centre of the cell and the two corners of the +Z edge, extruded by the
    /// roof's thickness, shrunk by the gap. Bottom three then top three.</summary>
    public static Vector3[] Kite(float R, float thick, float gap)
    {
        float apothem = Mathf.Sqrt(3.0f) * 0.5f * R;
        Vector3[] tri =
        {
            new(0.0f, 0.0f, 0.0f),
            new(-R * 0.5f, 0.0f, apothem),
            new(R * 0.5f, 0.0f, apothem),
        };
        Vector3 c = (tri[0] + tri[1] + tri[2]) / 3.0f;
        var pts = new Vector3[6];
        float shrink = 1.0f - gap / (apothem * 2.0f / 3.0f);
        for (int i = 0; i < 3; i++)
        {
            Vector3 p = (tri[i] - c) * shrink;
            pts[i] = p + Vector3.Down * (thick * 0.5f - gap * 0.5f);
            pts[i + 3] = p + Vector3.Up * (thick * 0.5f - gap * 0.5f);
        }
        return pts;
    }

    /// <summary>Every shipped set's gun in units of its own cell radius: the
    /// muzzle's height over the ground and the barrel's radius. Read off
    /// <c>Sprites/&lt;TAG&gt;/_run_report.json</c> by <c>pipeline/hex_capon.py</c>,
    /// which sized the slit against them; kept here so the self-test can hold
    /// the slit to the same five guns the model was.</summary>
    public static readonly (string Tag, float MuzzleZ, float BarrelR)[] Guns =
    {
        ("LTP", 0.694f, 0.063f),
        ("MTP", 0.675f, 0.066f),
        ("HTP", 0.639f, 0.119f),
        ("TDP", 0.507f, 0.067f),
        ("HMP", 0.707f, 0.093f),
    };

    /// <summary>The tallest thing a parked tank raises: the medium's turret
    /// top, in cell radii, off the same measurement.</summary>
    public const float TurretTop = 1.0625f;

    /// <summary>Which guns the slit does not pass at rest, with the clearance
    /// that failed. Empty is the answer wanted.</summary>
    public static List<string> Fouls(Recipe r)
    {
        var out_ = new List<string>();
        foreach ((string tag, float z, float rad) in Guns)
        {
            float below = z - rad - r.SlitLow;
            float above = r.SlitHigh - z - rad;
            if (below < 0.0f || above < 0.0f)
                out_.Add($"{tag} {Mathf.Min(below, above):+0.000;-0.000}");
        }
        return out_;
    }

    /// <summary>The prop's pieces as a sentence, for the bench's readout.</summary>
    public static string Note(WallKit.Plan plan)
    {
        int boxes = 0, kites = 0, windows = 0;
        foreach (WallKit.Block b in plan.Blocks)
        {
            if (b.Hull is not null) kites++; else boxes++;
            if (b.Window) windows++;
        }
        return $"capon: {boxes} slabs, {kites} roof pieces, {windows} slit pieces, "
               + $"top {plan.Top:F2}, reach {plan.Reach:F3}";
    }
}
