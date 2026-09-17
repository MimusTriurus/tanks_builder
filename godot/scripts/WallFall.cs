using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The wall coming down: one solved flight per piece, and no solver.
///
/// <b>Where a brick lands is chosen first and how it flies there is worked out
/// second.</b> That inversion is the whole design, and it is what a rigid-body
/// simulation cannot do. The Blender prop knocks the wall over and then fences
/// the cell to keep the rubble on it - measured, without the fence eleven of
/// thirty-eight bricks finish on a neighbouring tile and the worst is 56% of a
/// cell past the edge. Here the destination is drawn inside the hexagon, so
/// staying on the cell is not enforced, it is a property of the construction.
///
/// <b>And it buys the two things the bench actually needs.</b> A capture is
/// evidence and two runs of the same flags have to be diffable - the rule
/// <see cref="FrameClock.FixedStep"/> exists for - and a live solver would be the first
/// thing on this board that is not reproducible. It also costs nothing: forty
/// parabolas against a physics tick the project does not otherwise have.
///
/// <b>What is given up is brick-on-brick collision in flight.</b> Pieces pass
/// through each other on the way down. At a cell 248px wide over three quarters
/// of a second that is close to invisible, and it is a real loss rather than a
/// free one, so it is said here. What is not given up is the shape of the pile:
/// the simulated one comes to rest 23 bricks in the first layer, 15 in the
/// second and none above - measured - which is a two-deep scatter, and that is
/// what <see cref="Layers"/> reproduces on purpose rather than by luck.
/// </summary>
public static class WallFall
{
    /// <summary>How the charge went off. Two, because they answer different
    /// questions and the Blender build measured the difference: a ram from one
    /// side leaves the near third of the cell bare (1 / 26 / 32 pieces across
    /// three strips), a charge under the footing does not (16 / 25 / 18).
    /// A wall is a line, so a line pushed one way lands as a line whatever the
    /// energy - which is why this is two distributions and not one energy
    /// knob.</summary>
    public enum Shot { Mine, Ram }

    /// <summary>One piece's whole trip, solved up front.</summary>
    public struct Flight
    {
        public float Wait;        // seconds before it lets go
        public float Span;        // seconds in the air
        public Vector3 From, To;
        public float Arc;         // how high above the straight line it goes
        public Basis Rest;        // how it lies when it gets there
        public Vector3 Spin;      // axis, scaled by turns
        public float Settle;      // seconds of the last bit of roll
    }

    /// <summary>Roughly how many bricks share a landing spot. 0.62 puts 23 of 38
    /// in the first layer and 15 in the second, which is the simulated pile to
    /// the piece.</summary>
    public const float Layers = 0.62f;

    /// <summary>Solve every piece's flight. Deterministic in the seed, and in
    /// nothing else - no wall clock, no frame count.</summary>
    public static List<Flight> Solve(WallKit.Plan plan, int seed, Shot shot,
                                     float coverage = 0.92f)
    {
        var flights = new List<Flight>(plan.Blocks.Count);

        // Order the pieces by how far they are from where the charge went, so the
        // collapse runs outward from it instead of everything letting go at once.
        Vector3 blast = shot == Shot.Mine
            ? new Vector3(0.0f, 0.0f, 0.0f)
            : new Vector3(0.0f, plan.Top * 0.45f, -plan.Size.Z);
        var order = new List<(float D, int I)>(plan.Blocks.Count);
        for (int i = 0; i < plan.Blocks.Count; i++)
            order.Add(((plan.Blocks[i].Seat - blast).Length(), i));
        order.Sort((a, b) => a.D.CompareTo(b.D));

        // The landing spots, drawn inside the hexagon once and shared out. Fewer
        // spots than pieces is what makes the pile two deep - see Layers.
        //
        // **Inset by a brick, because a spot is a centre and the cell has to hold
        // the whole piece.** Drawn to the edge, a brick lying with its length
        // across the rim hangs half of itself over it - which is the same lesson
        // the layout already learned twice, that a rotated brick is wider than a
        // brick. It costs no spread: the centres come in but the bodies stick
        // back out, so what the rubble covers is unchanged and only what it
        // overhangs goes away.
        int spots = Mathf.Max(1, Mathf.RoundToInt(plan.Blocks.Count * Layers));
        var ground = Spots(seed, spots, coverage - Inset(plan));
        var used = new int[spots];

        for (int k = 0; k < order.Count; k++)
        {
            int i = order[k].I;
            WallKit.Block b = plan.Blocks[i];

            // A chip is already on the ground and mostly stays there; it gets a
            // nudge rather than a flight, or the apron leaps into the air.
            if (b.Chip)
            {
                flights.Add(Nudge(seed, i, b));
                continue;
            }

            int spot = k % spots;
            int layer = used[spot]++;
            Vector2 at = ground[spot];
            float thick = b.Half.Y * 2.0f;
            var to = new Vector3(at.X, thick * (layer + 0.5f), at.Y);

            if (shot == Shot.Ram)
            {
                // Downwind, and the far half of the cell only. This is the
                // distribution the simulated ram produced, not a weaker version
                // of the mine.
                to.Z = Mathf.Lerp(to.Z, Mathf.Abs(to.Z), 0.85f);
            }

            float run = (to - b.Seat).Length();
            float span = Mathf.Clamp(0.42f + run * 0.55f, 0.42f, 1.05f);
            flights.Add(new Flight
            {
                // Near the charge goes first. Not a wave with a hard front: the
                // spread on the delay is what stops the courses moving as a slab.
                Wait = order[k].D * 0.55f
                       + WallKit.Roll(seed, i, 71) * 0.10f,
                Span = span,
                From = b.Seat,
                To = to,
                // Higher for the pieces that travel, and a floor so the near ones
                // still leave the ground rather than sliding.
                Arc = b.Seat.Y * 0.35f + run * 0.30f
                      + WallKit.Roll(seed, i, 72) * 0.06f,
                Rest = Lying(seed, i, layer),
                Spin = Tumble(seed, i, run),
                Settle = 0.18f + WallKit.Roll(seed, i, 73) * 0.12f,
            });
        }

        // Solve() walked the pieces in blast order; hand them back in the plan's
        // order so a caller can index one against the other.
        var byIndex = new Flight[plan.Blocks.Count];
        for (int k = 0; k < order.Count; k++)
            byIndex[order[k].I] = flights[k];
        return new List<Flight>(byIndex);
    }

    /// <summary>How far in from the rim a landing spot has to be drawn so the
    /// piece standing on it still fits.
    ///
    /// The hexagon holds a point at <c>x + z/sqrt(3) &lt;= r</c> and
    /// <c>|z| &lt;= sqrt(3)/2 r</c>, so a body reaching <c>e</c> either way needs
    /// <c>e + e/sqrt(3)</c> off the first and <c>e / (sqrt(3)/2)</c> off the
    /// second; the larger of the two is the inset, and on a brick it is the
    /// first. Worst case over the pieces rather than per piece, so that two
    /// bricks sharing a spot still stack on each other instead of drifting apart
    /// by the difference between them.</summary>
    private static float Inset(WallKit.Plan plan)
    {
        float e = 0.0f;
        foreach (WallKit.Block b in plan.Blocks)
            // Any yaw, plus whatever the lean tips out of the vertical: the flat
            // diagonal is what a turned brick reaches, and Half.Y is the most the
            // tilt in Lying can add to it.
            e = Mathf.Max(e, Mathf.Sqrt(b.Half.X * b.Half.X + b.Half.Z * b.Half.Z)
                             + b.Half.Y);
        return Mathf.Max(e + e / Mathf.Sqrt(3.0f), e / (Mathf.Sqrt(3.0f) * 0.5f));
    }

    private static Flight Nudge(int seed, int i, in WallKit.Block b)
    {
        float shove = WallKit.BrickLong * 0.35f;
        var to = b.Seat + new Vector3(
            (WallKit.Roll(seed, i, 81) - 0.5f) * shove, 0.0f,
            (WallKit.Roll(seed, i, 82) - 0.5f) * shove);
        return new Flight
        {
            Wait = 0.05f + WallKit.Roll(seed, i, 83) * 0.25f,
            Span = 0.30f,
            From = b.Seat,
            To = to,
            Arc = WallKit.BrickTall * 0.4f,
            Rest = b.Turn,
            Spin = new Vector3(0.0f, WallKit.Roll(seed, i, 84) - 0.5f, 0.0f),
            Settle = 0.1f,
        };
    }

    /// <summary>How a brick lies once it is down: flat, turned any way, and
    /// leaning a little more the higher up the pile it is.</summary>
    private static Basis Lying(int seed, int i, int layer)
    {
        float yaw = WallKit.Roll(seed, i, 91) * Mathf.Tau;
        // A brick on top of others is not level. On the flat it very nearly is,
        // and a scatter of perfectly flat bricks reads as tiles.
        float tilt = Mathf.DegToRad((WallKit.Roll(seed, i, 92) - 0.5f)
                                    * (layer == 0 ? 14.0f : 34.0f));
        float roll = Mathf.DegToRad((WallKit.Roll(seed, i, 93) - 0.5f)
                                    * (layer == 0 ? 10.0f : 26.0f));
        return new Basis(Vector3.Up, yaw)
               * new Basis(Vector3.Right, tilt)
               * new Basis(Vector3.Back, roll);
    }

    private static Vector3 Tumble(int seed, int i, float run)
    {
        var axis = new Vector3(WallKit.Roll(seed, i, 101) - 0.5f,
                               WallKit.Roll(seed, i, 102) - 0.5f,
                               WallKit.Roll(seed, i, 103) - 0.5f);
        if (axis.LengthSquared() < 1e-6f)
            axis = Vector3.Right;
        // Turns, not radians per second: the piece has to arrive at Rest, so what
        // matters is how many whole turns it makes getting there.
        float turns = 0.4f + run * 1.6f + WallKit.Roll(seed, i, 104) * 0.6f;
        return axis.Normalized() * turns;
    }

    /// <summary>Landing spots inside a flat-top hexagon of the given radius.
    ///
    /// A jittered grid filtered by the hexagon's own two constraints, not a disc:
    /// the vertex axis holds 15% more than the inradius does, and on this board
    /// that is where the wall lies.</summary>
    private static List<Vector2> Spots(int seed, int count, float radius)
    {
        var outp = new List<Vector2>(count);
        // Enough of a grid that the filter and the shuffle have something to
        // choose from; the hexagon keeps about 3/4 of a square that bounds it.
        int side = Mathf.Max(2, Mathf.CeilToInt(Mathf.Sqrt(count / 0.7f)) + 1);
        var pool = new List<Vector2>();
        for (int gy = 0; gy < side; gy++)
        for (int gx = 0; gx < side; gx++)
        {
            int id = gy * side + gx;
            float u = (gx + 0.5f + (WallKit.Roll(seed, id, 111) - 0.5f) * 0.8f) / side;
            float v = (gy + 0.5f + (WallKit.Roll(seed, id, 112) - 0.5f) * 0.8f) / side;
            var p = new Vector2((u * 2.0f - 1.0f) * radius,
                                (v * 2.0f - 1.0f) * radius * (Mathf.Sqrt(3.0f) * 0.5f));
            if (Inside(p, radius))
                pool.Add(p);
        }
        if (pool.Count == 0)
            pool.Add(Vector2.Zero);

        // Deterministic shuffle, so the spots are handed out in an order that has
        // nothing to do with the grid - otherwise the collapse fills the cell in
        // reading order and the last bricks all land in one corner.
        for (int i = pool.Count - 1; i > 0; i--)
        {
            int j = (int)(WallKit.Roll(seed, i, 113) * (i + 1));
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }
        for (int i = 0; i < count; i++)
            outp.Add(pool[i % pool.Count]);
        return outp;
    }

    private static bool Inside(Vector2 p, float radius)
    {
        float x = Mathf.Abs(p.X), y = Mathf.Abs(p.Y);
        return y <= Mathf.Sqrt(3.0f) * 0.5f * radius
               && x + y / Mathf.Sqrt(3.0f) <= radius;
    }

    /// <summary>Where a piece is at time <paramref name="t"/>, seconds after the
    /// charge.</summary>
    public static Transform3D At(in Flight f, in WallKit.Block b, float t)
    {
        float s = f.Span > 1e-4f ? (t - f.Wait) / f.Span : 1.0f;
        if (s <= 0.0f)
            return b.Frame;

        if (s >= 1.0f)
        {
            // Down. The last of the roll is eased out over Settle rather than
            // stopped on the frame it lands, which is the difference between a
            // brick coming to rest and a brick being switched off.
            float k = f.Settle > 1e-4f
                ? Mathf.Clamp((t - f.Wait - f.Span) / f.Settle, 0.0f, 1.0f)
                : 1.0f;
            Basis land = Spun(b.Turn, f.Spin, 1.0f);
            return new Transform3D(land.Slerp(f.Rest, Ease(k)), f.To);
        }

        // A parabola through both ends, which is what makes the destination
        // exact. Solving a launch velocity and integrating would put the landing
        // wherever the numbers took it, and the whole point is that it does not.
        Vector3 p = f.From.Lerp(f.To, s);
        p.Y += f.Arc * 4.0f * s * (1.0f - s);
        return new Transform3D(Spun(b.Turn, f.Spin, s), p);
    }

    private static Basis Spun(Basis start, Vector3 spin, float s)
    {
        float turns = spin.Length();
        if (turns < 1e-5f)
            return start;
        return new Basis(spin / turns, turns * Mathf.Tau * s) * start;
    }

    private static float Ease(float k) => k * k * (3.0f - 2.0f * k);

    /// <summary>When the last piece has finished moving. What the bench loops
    /// on, and what a caller bakes to.</summary>
    public static float Length(IReadOnlyList<Flight> flights)
    {
        float last = 0.0f;
        foreach (Flight f in flights)
            last = Mathf.Max(last, f.Wait + f.Span + f.Settle);
        return last;
    }
}
