using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The ruts the belts leave on the ground.
///
/// The unit here is not a cell. "The hex a tank drove over gets a rutted look"
/// is the obvious model and it cannot say the one thing that was asked of this:
/// <b>a tank turning on the spot never leaves its cell</b>, so a per-cell mark
/// draws either nothing or the whole hex. The unit is the point where each belt
/// touches the ground, and what it leaves behind is the ribbon it drags.
///
/// From that everything else falls out with no case of its own:
///
/// <list type="bullet">
/// <item>driving straight lays two parallel ribbons,</item>
/// <item>a corner lays two arcs of different radius,</item>
/// <item>a pivot leaves the hull's contact point where it was and sweeps both
/// belt points round it - <b>two concentric arcs</b>, counter-rotating, which is
/// the signature of a pivot and is why this exists.</item>
/// </list>
///
/// Three things are load-bearing and each is a mistake that was available:
///
/// <b>Laid off each belt's own distance, not off time and not off the hull's
/// displacement.</b> By time, the trail thickens at a crawl. By hull
/// displacement, a pivot lays nothing at all - the hull does not move - which is
/// exactly the failure <see cref="Main.BeltTravel"/> already records having made
/// once with the belts themselves.
///
/// <b>The belt offset comes off <see cref="AtlasSet.GroundDirection"/>
/// unnormalised.</b> It carries the isometric squash, so a gauge pointing into
/// the screen shrinks to half. Normalise it and a tank driving away from the
/// camera runs on a track gauge wider than its own hull.
///
/// <b>Runs, not one long list of points.</b> Reset, parking, a class switch:
/// the tank jumps, and a single polyline joins the two ends with a straight line
/// across the board. A pen that lifts is one field and a break in the trail.
///
/// Bounded rather than baked. A render target would cost only the new stitches
/// and could not fade or be cleared per tank; a ring buffer of points costs a
/// handful of polylines a frame, forgets the oldest by itself, clears with the
/// repair and stays deterministic under --capture, which is what a capture that
/// is evidence needs. The bake is the upgrade for the day the board outgrows
/// this - at the numbers below one belt remembers about 25 rows of travel.
/// </summary>
public sealed partial class TrackMarks : Node2D
{
    /// <summary>Under everything and over the ground.
    ///
    /// There was no free rung: the field sat at -100 and the selection ring at
    /// -99, so both moved down rather than this squeezing between them. Ruts are
    /// terrain, so they go under the ring, which is a marker; the contact shadow
    /// stays above them, because a shadow falls across a rut. Leaning on child
    /// order instead would work and is the arrangement this project already
    /// refuses - overlap is decided by a number, not by who was added first.
    /// </summary>
    public const int MarksZ = -101;

    /// <summary>How far a belt travels between stitches, in screen pixels. Short
    /// enough that a pivot arc reads as a curve rather than a fan of chords -
    /// the tightest arc here has a radius of about 40px, and 4px of arc on that
    /// is under six degrees.</summary>
    public const double Stitch = 4.0;

    /// <summary>Points kept per belt. At <see cref="Stitch"/> that is 6000px of
    /// travel, some 25 rows of the board. The cost of forgetting late is a
    /// longer polyline every frame; the cost of forgetting early is a tank that
    /// out-drives its own trail in one order.</summary>
    public const int Capacity = 1500;

    /// <summary>How much of the tail is spent fading out. A trail that ends in a
    /// clean cut reads as a rendering fault; one that ends in nothing reads as
    /// ground recovering.</summary>
    private const double FadeTail = 0.25;

    /// <summary>Darkening at full strength. Plain alpha over the ground rather
    /// than anything additive - see the pale-ground lesson the effect layers all
    /// carry - and nowhere near black: a rut is soil turned over, and a black
    /// one reads as painted rails.</summary>
    private static readonly Color Ink = new(0.20f, 0.16f, 0.11f, 0.5f);

    /// <summary>How much wider than the belt a scrubbed mark gets, at most. The
    /// scrub itself is derived rather than dialled - see
    /// <see cref="Scrub"/>.</summary>
    private const double ScrubWidth = 1.5;

    public IReadOnlyList<Vehicle> Vehicles = Array.Empty<Vehicle>();
    public bool Enabled = true;

    /// <summary>The width one tank's last stitch went down at, in screen
    /// pixels. In the trace because it is a derived number with two factors in
    /// it - the camera squash and the scrub - and a rut that comes out the wrong
    /// width gives no clue which of them did it.
    ///
    /// Per tank, not one number: this is laid for every vehicle every frame, and
    /// a single field reports whoever came last in the loop rather than whoever
    /// the trace is about. It read 25.3px off a parked heavy while the medium
    /// being traced was laying 11.</summary>
    public double WidthOf(Vehicle v) =>
        _trails.TryGetValue(v, out Trail[]? pair) ? pair[0].LastWidth : 0.0;

    /// <summary>One belt's trail: a ring of points with a break flag, so a jump
    /// splits the ribbon instead of drawing a line across the board.</summary>
    private sealed class Trail
    {
        public readonly List<Vector2> Points = new();
        public readonly List<float> Widths = new();
        public readonly List<bool> Breaks = new();
        public double Travelled;
        public bool Down;
        public double LastWidth;

        public void Add(Vector2 at, float width, bool broken)
        {
            Points.Add(at);
            Widths.Add(width);
            Breaks.Add(broken);
            if (Points.Count > Capacity)
            {
                Points.RemoveAt(0);
                Widths.RemoveAt(0);
                Breaks.RemoveAt(0);
            }
        }

        public void Clear()
        {
            Points.Clear();
            Widths.Clear();
            Breaks.Clear();
            Travelled = 0.0;
            Down = false;
        }
    }

    private readonly Dictionary<Vehicle, Trail[]> _trails = new();

    public override void _Ready() => ZIndex = MarksZ;

    /// <summary>Total points held, for the panel and the trace. A layer that
    /// laid nothing and a layer that is switched off are the same empty picture,
    /// so the count is reported rather than inferred.</summary>
    public int Count
    {
        get
        {
            int n = 0;
            foreach (Trail[] pair in _trails.Values)
                n += pair[0].Points.Count + pair[1].Points.Count;
            return n;
        }
    }

    /// <summary>One belt's trail, oldest first - side 0 is the left. For the
    /// self-test: where the ribbons went is the whole claim, and it cannot be
    /// read off a still frame.</summary>
    public IReadOnlyList<Vector2> TrailOf(Vehicle v, int side) =>
        _trails.TryGetValue(v, out Trail[]? pair)
            ? pair[side].Points : Array.Empty<Vector2>();

    public void Clear()
    {
        foreach (Trail[] pair in _trails.Values)
        {
            pair[0].Clear();
            pair[1].Clear();
        }
        QueueRedraw();
    }

    /// <summary>Lift the pen on one tank: the next stitch starts a new run
    /// rather than joining across wherever it went. Called by anything that
    /// moves a tank without driving it there.</summary>
    public void Lift(Vehicle v)
    {
        if (_trails.TryGetValue(v, out Trail[]? pair))
        {
            pair[0].Down = false;
            pair[1].Down = false;
        }
    }

    /// <summary>
    /// Advance both belts of one tank by the ground they covered.
    ///
    /// Takes the travel rather than working it out, so this and the belts
    /// themselves cannot disagree about how far a track went - the same reason
    /// <see cref="Main.BeltTravel"/> is one number feeding the sprite, the sound
    /// and now this.
    /// </summary>
    public void Lay(Vehicle v, (double Left, double Right) travel, double delta)
    {
        if (!_trails.TryGetValue(v, out Trail[]? pair))
        {
            pair = new[] { new Trail(), new Trail() };
            _trails[v] = pair;
        }
        if (!Enabled || v.Atlas.HasTracks != true)
        {
            pair[0].Down = false;
            pair[1].Down = false;
            return;
        }

        double arm = v.Atlas.TrackArm * v.Sprite.BodyScale;
        Vector2 across = v.Atlas.GroundDirection(v.Sprite.HullFacing + 90.0);
        Vector2 centre = v.GroundPoint - GlobalPosition;
        float width = ScreenWidth(v,
            v.Atlas.TrackWidth * v.Sprite.BodyScale * Scrub(v, travel, delta));
        pair[0].LastWidth = width;
        pair[1].LastWidth = width;

        for (int side = 0; side < 2; side++)
        {
            Trail trail = pair[side];
            Vector2 at = centre + across * (float)(side == 0 ? arm : -arm);
            double moved = Math.Abs(side == 0 ? travel.Left : travel.Right);
            trail.Travelled += moved;
            if (trail.Down && trail.Travelled < Stitch)
                continue;
            // A stitch is dropped when the belt has covered one, or when the pen
            // has just come down. The second half is what puts a first point
            // under a tank that has only just started to move; without it the
            // ribbon begins one stitch late, which on a pivot is most of it.
            trail.Add(at, width, !trail.Down);
            trail.Travelled = 0.0;
            trail.Down = true;
        }
        QueueRedraw();
    }

    /// <summary>
    /// How wide a rut of <paramref name="ground"/> pixels shows on screen when
    /// the hull is pointing where it is.
    ///
    /// A rut is not a line on the screen, it is a strip lying in the ground
    /// plane, and the plane is squashed. So its width goes with the heading: a
    /// tank driving up the screen shows its belts at full width, one driving
    /// across shows them at sin(elevation) - a factor of two, and the same
    /// factor of two <see cref="AtlasSet.GroundDirection"/> exists to carry.
    ///
    /// Both edges of the strip sit at <c>+-GroundDirection(heading+90)</c> from
    /// the centre line, so this is the part of that offset which is not already
    /// along the strip. Getting it wrong is not subtle: at a constant screen
    /// width the ruts came out 106px across a 21px belt, because a diagonal line
    /// is cut wide by a horizontal ruler.
    /// </summary>
    private static float ScreenWidth(Vehicle v, double ground)
    {
        Vector2 along = v.Atlas.GroundDirection(v.Sprite.HullFacing);
        Vector2 across = v.Atlas.GroundDirection(v.Sprite.HullFacing + 90.0)
                         * (float)ground;
        if (along.LengthSquared() <= 1e-9f)
            return (float)Math.Max(2.0, ground);
        Vector2 unit = along.Normalized();
        return (float)Math.Max(2.0, (across - unit * across.Dot(unit)).Length());
    }

    /// <summary>
    /// How hard the belts are grinding sideways, as a multiplier on the rut's
    /// width, 1 for a clean roll.
    ///
    /// A straight belt of length L rolling along a path of radius R cannot lie
    /// along that path: it scuffs across it, and how much is L/R. So this is
    /// derived, not a taste knob - which matters because the interesting case is
    /// the extreme one. On a pivot R is the arm, about 40px, against a belt
    /// nearer 145px, so the ground under the belt is not rolled over, it is
    /// churned. It saturates almost at once, and that is honest: a tank is
    /// either rolling or it is digging, and there is very little in between.
    /// </summary>
    private static double Scrub(Vehicle v, (double Left, double Right) travel,
                                double delta)
    {
        double arm = v.Atlas.TrackArm * v.Sprite.BodyScale;
        if (arm <= 0.0 || delta <= 0.0)
            return 1.0;
        // The belts' difference is the turn, their mean is the drive: the same
        // decomposition BeltTravel does forwards, read backwards.
        double turn = Math.Abs(travel.Left - travel.Right) / (2.0 * arm);
        double drive = Math.Abs(travel.Left + travel.Right) / 2.0;
        if (turn <= 1e-9)
            return 1.0;                                   // straight: rolling
        double belt = v.Atlas.HullSpan * v.Sprite.BodyScale * 0.8;
        double radius = drive / turn;                     // px of path per radian
        double scrub = radius <= 1e-6
            ? 1.0                                         // pivot: all scrub
            : Math.Min(1.0, belt / (2.0 * radius));
        return 1.0 + (ScrubWidth - 1.0) * scrub;
    }

    public override void _Draw()
    {
        if (!Enabled)
            return;
        foreach (Trail[] pair in _trails.Values)
        foreach (Trail trail in pair)
            DrawTrail(trail);
    }

    /// <summary>How far the width may drift inside one run before it is cut.
    /// A polyline carries one width, and the width is a function of heading, so
    /// a trail that turns has to be drawn in pieces. 12% is under a pixel on
    /// these belts, and the pieces share their end point so they butt rather
    /// than gap.</summary>
    private const float WidthDrift = 0.12f;

    /// <summary>How far a run may turn between two stitches before it is cut.
    ///
    /// A wide polyline mitres its joints, and a mitre on a sharp corner shoots
    /// out to a point: where a pivot's arc met the straight run that followed
    /// it, a 30px ribbon grew an arrowhead half a cell long. Cutting there
    /// leaves two ribbons meeting at a blunt end, which is what a track does.
    /// Six degrees is one stitch of the tightest arc here, so a pivot itself
    /// never trips it.</summary>
    private const float TurnCut = 25.0f;

    private void DrawTrail(Trail trail)
    {
        int n = trail.Points.Count;
        if (n < 2)
            return;
        var run = new List<Vector2>();
        var ink = new List<Color>();
        float width = 0.0f;

        for (int i = 0; i < n; i++)
        {
            float want = trail.Widths[i];
            bool kinked = run.Count >= 2
                          && Turn(run[^2], run[^1], trail.Points[i]) > TurnCut;
            bool cut = trail.Breaks[i]
                       || kinked
                       || (run.Count > 0
                           && Math.Abs(want - width) > width * WidthDrift);
            if (cut && run.Count > 0)
            {
                Flush(run, ink, width);
                // The next piece starts on the last point of this one, so a
                // width change is a step in the ribbon and not a hole in it. A
                // real break - the pen lifted - starts empty.
                Vector2 seam = run[^1];
                Color seamInk = ink[^1];
                run.Clear();
                ink.Clear();
                if (!trail.Breaks[i])
                {
                    run.Add(seam);
                    ink.Add(seamInk);
                }
                // Here, not under "the run is empty": the seam point has already
                // gone in, so the run is not empty and the new width would never
                // be picked up. That left every piece drawn at the width of the
                // first one - a straight run of 11px ribbon coming out at the
                // 30px the opening pivot had scrubbed.
                width = want;
            }
            else if (run.Count == 0)
                width = want;
            run.Add(trail.Points[i]);
            // Age measured against the whole trail rather than against this run,
            // so a tank that stops and starts does not get a fresh dark stub
            // beside a faded ribbon of the same age.
            double age = (n - 1 - i) / (double)Math.Max(n - 1, 1);
            double fade = age <= 1.0 - FadeTail
                ? 1.0
                : 1.0 - (age - (1.0 - FadeTail)) / FadeTail;
            var colour = Ink;
            colour.A *= (float)Math.Clamp(fade, 0.0, 1.0);
            ink.Add(colour);
        }
        Flush(run, ink, width);
    }

    private void Flush(List<Vector2> run, List<Color> ink, float width)
    {
        // A run of one is a tank that jumped and has not moved since: nothing to
        // draw, and DrawPolylineColors will not take it.
        if (run.Count >= 2)
            DrawPolylineColors(run.ToArray(), ink.ToArray(), width, true);
    }

    /// <summary>Degrees between two consecutive steps of a run.</summary>
    private static float Turn(Vector2 a, Vector2 b, Vector2 c)
    {
        Vector2 first = b - a, second = c - b;
        if (first.LengthSquared() <= 1e-9f || second.LengthSquared() <= 1e-9f)
            return 0.0f;
        return Mathf.RadToDeg(Math.Abs(first.AngleTo(second)));
    }
}
