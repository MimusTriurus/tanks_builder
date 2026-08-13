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

    /// <summary>How far a belt travels between stitches when the atlas does not
    /// say. It normally does: a stitch is <b>one link</b>, so the spacing is
    /// <see cref="AtlasSet.TrackPitch"/> - the same number the belts themselves
    /// wind by, which makes a point of the trail and an imprint of a track shoe
    /// the same thing rather than two things at two spacings.</summary>
    public const double Stitch = 7.0;

    /// <summary>One link on the ground, as drawn, or <see cref="Stitch"/> when
    /// there is no belt metadata to ask.</summary>
    private static double StitchFor(Vehicle v) =>
        v.Atlas.TrackPitch > 0.0
            ? v.Atlas.TrackPitch * v.Sprite.BodyScale
            : Stitch;

    /// <summary>Points kept per belt. At <see cref="Stitch"/> that is 6000px of
    /// travel, some 25 rows of the board. The cost of forgetting late is a
    /// longer polyline every frame; the cost of forgetting early is a tank that
    /// out-drives its own trail in one order.</summary>
    public const int Capacity = 1500;

    /// <summary>
    /// How long an imprint lasts, in seconds, from laid to gone.
    ///
    /// Time, not a place in the buffer. Fading by position in the ring - which
    /// is what this did first - is not fading at all: a tank that drives ten
    /// cells and stops leaves a trail that never goes, because nothing is
    /// pushing the old points out, and the same drive fades differently
    /// depending on how full the ring happens to be. Ground recovering is a
    /// thing that happens to old marks, so age is what it has to be keyed on.
    ///
    /// Twenty seconds is longer than any manoeuvre on this board and shorter
    /// than a session: the trail outlives the move that made it and does not
    /// accumulate into a scribble. At cruise it is about 690 imprints per belt,
    /// which also makes <see cref="Capacity"/> what it should have been all
    /// along - a backstop rather than the mechanism.
    /// </summary>
    public const double Life = 20.0;

    /// <summary>Darkening at full strength. Plain alpha over the ground rather
    /// than anything additive - see the pale-ground lesson the effect layers all
    /// carry - and nowhere near black: a rut is soil turned over, and a black
    /// one reads as painted rails.</summary>
    public static readonly Color Ink = new(0.20f, 0.16f, 0.11f, 0.16f);

    /// <summary>The grouser bars, darker than the ground they sit in. A track
    /// does not leave a smooth stripe: it leaves a shoe every pitch, and the
    /// ladder is what reads as a track rather than as a drag mark.
    ///
    /// One inversion worth naming, because the belts themselves carry the
    /// opposite lesson: a moving tread aliases past half a link per frame and
    /// has to be smeared out. An imprint does not move, so the ladder is honest
    /// at any speed - the ground is where the sampling problem is not.</summary>
    public static readonly Color BarInk = new(0.14f, 0.11f, 0.07f, 0.26f);

    /// <summary>How thick one bar is drawn. Two, because a shoe is a shoe: it does
    /// not grow with the tank, and a fatter bar closes the gaps that make it a
    /// ladder.
    ///
    /// Screen pixels here and ground units to the stage, which reads the same
    /// number - see <see cref="Stage3D.Shoes"/>. A length is a length; what
    /// differs is that on the ground the camera thins it across the screen and on
    /// the canvas it cannot.</summary>
    public const float BarThickness = 2.0f;

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

        /// <summary>When each imprint was laid, on this trail's own clock.
        /// Stamps rather than ages, so a frame does not have to write every
        /// point it did not touch - the clock moves instead.</summary>
        public readonly List<double> Stamps = new();

        /// <summary>Seconds since this trail started. Advanced from the delta the
        /// harness hands down, never from _Process: --capture fixes that delta so
        /// two runs land on the same state, and a clock of its own would put the
        /// real frame time back into a picture that is meant to be evidence.
        /// </summary>
        public double Clock;

        /// <summary>Half the shoe, across the belt, in the ground plane. Stored
        /// rather than rebuilt from a heading at draw time because it is what
        /// the belt was actually lying along when the imprint was made, and the
        /// hull has turned since.</summary>
        public readonly List<Vector2> Bars = new();

        /// <summary>How high the ground under this imprint was, in screen px off
        /// the datum. Stored for the bars' reason, and the reason is sharper
        /// here: the point is in <i>drawn</i> screen space, so on a board with
        /// height the row it was laid at already has the lift taken out of it,
        /// and nothing left in the trail could put it back. Rebuilt from the
        /// cell at draw time it would also be wrong on a ramp, where the height
        /// varies across the cell and only the tank knew where on it it stood.
        /// </summary>
        public readonly List<float> Lifts = new();

        /// <summary>How wide the belt was lying, in ground px - the same number
        /// <see cref="Widths"/> is the screen projection of. Both are kept
        /// because the two views of the board want different ones: a canvas item
        /// needs the width as drawn, and geometry lying on the ground wants the
        /// width on the ground and lets the camera squash it.</summary>
        public readonly List<float> Grounds = new();

        public double Travelled;
        public bool Down;
        public double LastWidth;

        /// <summary>Where the belt was at the end of the last frame, so imprints
        /// can be placed along this frame's move rather than all at its end.
        /// </summary>
        public Vector2 LastAt;

        /// <summary>Half the smoothing window, in points - see
        /// <see cref="Smoothed"/>. Off the belt's length and the link pitch, so
        /// it is however many shoes are on the ground at once.</summary>
        public int Window = 1;

        public void Add(Vector2 at, float width, float ground, float lift,
                        Vector2 bar, bool broken)
        {
            Points.Add(at);
            Widths.Add(width);
            Grounds.Add(ground);
            Lifts.Add(lift);
            Bars.Add(bar);
            Breaks.Add(broken);
            Stamps.Add(Clock);
            if (Points.Count > Capacity)
                Drop(Points.Count - Capacity);
        }

        /// <summary>Move the clock on and let go of anything the ground has
        /// recovered. Oldest first and stamps only ever increase, so the expired
        /// are always a prefix - no scan of the whole trail.</summary>
        public void Age(double delta)
        {
            Clock += delta;
            int gone = 0;
            while (gone < Stamps.Count && Clock - Stamps[gone] >= Life)
                gone++;
            if (gone > 0)
                Drop(gone);
        }

        private void Drop(int n)
        {
            Points.RemoveRange(0, n);
            Widths.RemoveRange(0, n);
            Grounds.RemoveRange(0, n);
            Lifts.RemoveRange(0, n);
            Bars.RemoveRange(0, n);
            Breaks.RemoveRange(0, n);
            Stamps.RemoveRange(0, n);
            // The trail now starts wherever the survivor does, and a run that
            // has lost its head is a run: the first point has to say so or the
            // ribbon draws a line back to nothing.
            if (Points.Count > 0)
                Breaks[0] = true;
        }

        public double AgeOf(int i) => Clock - Stamps[i];

        public void Clear()
        {
            Points.Clear();
            Widths.Clear();
            Grounds.Clear();
            Lifts.Clear();
            Bars.Clear();
            Breaks.Clear();
            Stamps.Clear();
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

    /// <summary>The shoe half-vectors that go with <see cref="TrailOf"/>, and
    /// the same path with the belt rolled over it. Both for the self-test: what
    /// the ladder is laid across and how much a corner is rounded are the two
    /// claims here, and neither can be read off a still frame.</summary>
    public IReadOnlyList<Vector2> BarsOf(Vehicle v, int side) =>
        _trails.TryGetValue(v, out Trail[]? pair)
            ? pair[side].Bars : Array.Empty<Vector2>();

    /// <summary>How faded each imprint is, newest last - 1 fresh, 0 gone. For
    /// the self-test: that old marks go is the claim, and a still frame two
    /// seconds into a run cannot show it.</summary>
    public IReadOnlyList<double> FadesOf(Vehicle v, int side)
    {
        if (!_trails.TryGetValue(v, out Trail[]? pair))
            return Array.Empty<double>();
        Trail trail = pair[side];
        var fades = new double[trail.Points.Count];
        for (int i = 0; i < fades.Length; i++)
            fades[i] = Fade(trail, i);
        return fades;
    }

    /// <summary>
    /// One imprint as anything drawing the trail somewhere else needs it: where
    /// the shoe went, how high the ground under it was, what the shoe was laid
    /// across, how wide the belt was lying on the ground, how faded it is and
    /// whether it starts a run.
    ///
    /// One shape rather than a sixth parallel accessor. The lists are how the
    /// trail is stored - a ring buffer wants columns, not rows - but a reader
    /// that has to fetch five of them and trust their indices line up is a reader
    /// that can be given four of the five, and this project has already paid for
    /// a check whose answer went nowhere.
    ///
    /// <b>Two positions, and using one for both is a bug that looks like a
    /// rendering fault.</b> <paramref name="Rolled"/> is the ribbon's path, with
    /// the belt rolled over the corners; <paramref name="At"/> is where the shoe
    /// actually bit, and the ladder is drawn on that - see
    /// <see cref="DrawShoes"/> for why it is not smoothed. Drawn on the rolled
    /// path the ladder collapses towards the middle of its run, because the
    /// smoothing window is as long as the belt: measured, one 12-point run came
    /// out as a 20px blob instead of 70px of track.
    ///
    /// Where the rolled path goes is <see cref="Smoothed"/>'s answer and belongs
    /// to the trail; a second smoothing somewhere else would be a second opinion
    /// about how far a belt rolls a corner over.
    /// </summary>
    public readonly record struct Imprint(Vector2 At, Vector2 Rolled, float Lift,
                                          Vector2 Bar, float Ground, float Fade,
                                          bool Break);

    /// <summary>One belt's trail as imprints, oldest first. Empty for a tank that
    /// has not laid anything, which is also the answer while the layer is switched
    /// off - the trail ages but nothing new goes down.</summary>
    public IReadOnlyList<Imprint> ImprintsOf(Vehicle v, int side)
    {
        if (!_trails.TryGetValue(v, out Trail[]? pair))
            return Array.Empty<Imprint>();
        Trail trail = pair[side];
        Vector2[] path = Smoothed(trail);
        var marks = new Imprint[path.Length];
        for (int i = 0; i < path.Length; i++)
            marks[i] = new Imprint(trail.Points[i], path[i], trail.Lifts[i],
                                   trail.Bars[i], trail.Grounds[i],
                                   (float)Fade(trail, i), trail.Breaks[i]);
        return marks;
    }

    public IReadOnlyList<Vector2> SmoothedOf(Vehicle v, int side) =>
        _trails.TryGetValue(v, out Trail[]? pair)
            ? Smoothed(pair[side]) : Array.Empty<Vector2>();

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
        // Aged before the early-out: the ground recovers whether or not the
        // layer is switched on, so toggling it off must not freeze the trail in
        // the state it was in.
        pair[0].Age(delta);
        pair[1].Age(delta);
        if (!Enabled || v.Atlas.HasTracks != true)
        {
            pair[0].Down = false;
            pair[1].Down = false;
            return;
        }

        double arm = v.Atlas.TrackArm * v.Sprite.BodyScale;
        Vector2 across = v.Atlas.GroundDirection(v.Sprite.HullFacing + 90.0);
        Vector2 centre = v.GroundPoint - GlobalPosition;
        double belt = v.Atlas.TrackWidth * v.Sprite.BodyScale;
        double ground = belt * Scrub(v, travel, delta);
        float width = ScreenWidth(v, ground);
        // The height of the ground, not of the tank: see Vehicle.Ground. Neither of
        // the tank's own two heights will do. Standing is the depth promotion and is
        // deliberately stepped - on a climb it is the crown from the first pixel -
        // so a trail laid at it crosses a ramp in two jumps of a third of a level
        // instead of a slope. Height is where the sprite is drawn, which is a chord
        // between cell centres, and a chord runs a quarter of a level *below* a
        // ramp's face at the boundary: the face then hides the rut, which is what
        // it did on the upper half of every ramp and the far half of every crown.
        //
        // One height for both belts. On a slope the two are a gauge apart in height
        // and this says they are not - the same one-height-per-tank the whole
        // sprite is drawn at, so a rut that disagreed with it would be a rut
        // disagreeing with the tank standing on it.
        float lift = v.Ground;
        // Half a shoe, across the belt and in the ground plane. Off the same
        // unnormalised GroundDirection the gauge is: a bar is a length lying on
        // the ground, so it foreshortens with the ground.
        Vector2 bar = across * (float)(belt * 0.5);
        double stitch = StitchFor(v);
        // However many shoes are on the ground at once - that is the length of
        // belt a corner has to be rolled over by, so it is the window the ribbon
        // is smoothed through.
        int window = Math.Max(1, (int)Math.Round(
            v.Atlas.TrackLength * v.Sprite.BodyScale * 0.5 / Math.Max(stitch, 1.0)));

        pair[0].LastWidth = width;
        pair[1].LastWidth = width;

        for (int side = 0; side < 2; side++)
        {
            Trail trail = pair[side];
            trail.Window = window;
            Vector2 at = centre + across * (float)(side == 0 ? arm : -arm);
            double moved = Math.Abs(side == 0 ? travel.Left : travel.Right);
            trail.Travelled += moved;
            if (!trail.Down)
            {
                // The pen coming down lays one straight away. Without it the
                // trail begins a shoe late, which on a pivot is most of it.
                trail.Add(at, width, (float)ground, lift, bar, true);
                trail.Travelled = 0.0;
                trail.Down = true;
            }
            // The remainder is carried rather than dropped, and the imprints are
            // placed along this frame's move rather than all at its end. Zeroing
            // it instead quantised the spacing to whatever a frame happened to
            // cover - 8.3px between shoes on a 7.1px link - which is the frame
            // clock getting into a number that is supposed to be the belt's.
            while (trail.Travelled >= stitch)
            {
                trail.Travelled -= stitch;
                double back = moved > 1e-9 ? trail.Travelled / moved : 0.0;
                trail.Add(at.Lerp(trail.LastAt, (float)Math.Clamp(back, 0.0, 1.0)),
                          width, (float)ground, lift, bar, false);
            }
            trail.LastAt = at;
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
        {
            DrawTrail(this, trail, null);
            DrawShoes(this, trail, null);
        }
    }

    /// <summary>
    /// The imprints themselves: one bar across the belt per link, all in one
    /// call.
    ///
    /// Drawn over the ribbon rather than instead of it. The ribbon is the soil
    /// pressed flat, which is continuous; the ladder is what the shoes bit into,
    /// which is not. Bars alone read as a dotted line at this pitch, and the
    /// ribbon alone reads as a drag mark.
    ///
    /// Off the stored bar rather than off the hull's heading now, because the
    /// hull has turned since - and on a pivot it has turned a long way, which is
    /// exactly where the ladder earns its keep: the bars fan out radially and
    /// fill the ring the belts churned, where the ribbon can only draw its
    /// centre line.
    /// </summary>
    private void DrawShoes(CanvasItem into, Trail trail, Func<Vector2, bool>? keep)
    {
        int n = 0;
        var take = new List<int>(trail.Points.Count);
        for (int i = 0; i < trail.Points.Count; i++)
            if (keep is null || keep(trail.Points[i]))
                take.Add(i);
        n = take.Count;
        if (n == 0)
            return;
        var ends = new Vector2[n * 2];
        // One colour per bar, not per end: DrawMultilineColors takes a segment
        // list, so the colour array is half the length of the point array.
        var ink = new Color[n];
        for (int i = 0; i < n; i++)
        {
            int at = take[i];
            ends[i * 2] = trail.Points[at] - trail.Bars[at];
            ends[i * 2 + 1] = trail.Points[at] + trail.Bars[at];
            Color colour = BarInk;
            colour.A *= (float)Fade(trail, at);
            ink[i] = colour;
        }
        into.DrawMultilineColors(ends, ink, BarThickness);
    }

    /// <summary>How much of the ink an imprint still has, by how long ago it
    /// was laid. Linear over <see cref="Life"/>: ground does not hold a mark at
    /// full strength and then let go of it, and a hold followed by a drop is
    /// what a table of held frames is for - this is weather, not animation.
    /// </summary>
    private static double Fade(Trail trail, int i) =>
        Math.Clamp(1.0 - trail.AgeOf(i) / Life, 0.0, 1.0);

    /// <summary>
    /// The trail's points with the belt rolled over them.
    ///
    /// A belt is 146px long and a mark is left by all of it, so the ribbon
    /// cannot carry a corner tighter than the belt's half-length: the belt rolls
    /// over it. Convolving the centre line with the belt is that, to first
    /// order, and it is one moving average - which is also the honest answer to
    /// "take the point off the rear instead of the front". Neither end is right
    /// on its own; the hull turns about its centre, so front and rear sweep
    /// mirrored arcs and moving the sample point only moves the arc.
    ///
    /// It does mean a pivot's two neat crescents collapse towards a churned
    /// patch, and that is the truthful picture - a tank spun on the spot leaves
    /// a scuffed circle, not two clean arcs. The crescents are still there in
    /// the ladder, which is not smoothed.
    ///
    /// Prefix sums, so this is one pass however wide the window is, and it does
    /// not reach across a break: two runs are two trails that happen to share a
    /// list.
    /// </summary>
    private static Vector2[] Smoothed(Trail trail)
    {
        int n = trail.Points.Count;
        var outp = new Vector2[n];
        var sum = new Vector2[n + 1];
        for (int i = 0; i < n; i++)
            sum[i + 1] = sum[i] + trail.Points[i];
        // Where each run starts and ends, so the average never straddles a jump.
        int runStart = 0;
        for (int i = 0; i < n; i++)
        {
            if (trail.Breaks[i])
                runStart = i;
            int runEnd = i;
            while (runEnd + 1 < n && !trail.Breaks[runEnd + 1])
                runEnd++;
            int lo = Math.Max(runStart, i - trail.Window);
            int hi = Math.Min(runEnd, i + trail.Window);
            outp[i] = (sum[hi + 1] - sum[lo]) / (hi - lo + 1);
        }
        return outp;
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
    /// it, a 30px ribbon grew an arrowhead half a cell long. Smoothing takes
    /// most of those out; this catches what is left.</summary>
    private const float TurnCut = 25.0f;

    /// <summary>The soil pressed flat: one ribbon down the middle of the
    /// imprints, over the belt-smoothed path.</summary>
    private void DrawTrail(CanvasItem into, Trail trail, Func<Vector2, bool>? keep)
    {
        int n = trail.Points.Count;
        if (n < 2)
            return;
        Vector2[] path = Smoothed(trail);
        var run = new List<Vector2>();
        var ink = new List<Color>();
        float width = 0.0f;

        for (int i = 0; i < n; i++)
        {
            float want = trail.Widths[i];
            bool kinked = run.Count >= 2
                          && Turn(run[^2], run[^1], path[i]) > TurnCut;
            // A point that is not wanted cuts the run, exactly as a lifted pen
            // does. So a ribbon repainted for one cell stops at that cell's edge
            // rather than reaching a stitch onto the neighbour, where it would be
            // ground drawn over a tank that is standing in front of it.
            if (keep is not null && !keep(path[i]))
            {
                Flush(into, run, ink, width);
                run.Clear();
                ink.Clear();
                continue;
            }
            bool cut = trail.Breaks[i]
                       || kinked
                       || (run.Count > 0
                           && Math.Abs(want - width) > width * WidthDrift);
            if (cut && run.Count > 0)
            {
                Flush(into, run, ink, width);
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
            run.Add(path[i]);
            Color colour = Ink;
            colour.A *= (float)Fade(trail, i);
            ink.Add(colour);
        }
        Flush(into, run, ink, width);
    }

    /// <summary>
    /// The marks that lie on one of <paramref name="cells"/>, painted into
    /// somebody else's node.
    ///
    /// <b>Because a repainted cell is repainted bare.</b> The cap puts the ground
    /// that stands in front of a tank back over it - see
    /// <see cref="HexField.Occluders"/> - and a cell painted from the terrain
    /// alone comes back without the belt marks that were on it, so a tank
    /// dropping into a pit wiped its own fresh ruts off the cell it had just left.
    /// The marks are a ribbon across cells rather than a thing each cell owns,
    /// which is why they are not simply part of PaintCell.
    ///
    /// Cut per stitch, so a ribbon stops at the edge of the cell it is being
    /// repainted for. That leaves at most one stitch of gap at the seam, and the
    /// seam is where the drawn hexagon's own border line is.
    /// </summary>
    public void PaintOn(CanvasItem into, HexField field, Vector2 shift,
                        IReadOnlyList<Vector2I> cells)
    {
        if (!Enabled || cells.Count == 0)
            return;
        bool Keep(Vector2 at)
        {
            foreach (Vector2I cell in cells)
                if (field.OnCell(at + shift, cell))
                    return true;
            return false;
        }
        foreach (Trail[] pair in _trails.Values)
        foreach (Trail trail in pair)
        {
            DrawTrail(into, trail, Keep);
            DrawShoes(into, trail, Keep);
        }
    }

    private void Flush(CanvasItem into, List<Vector2> run, List<Color> ink, float width)
    {
        // A run of one is a tank that jumped and has not moved since: nothing to
        // draw, and DrawPolylineColors will not take it.
        if (run.Count >= 2)
            into.DrawPolylineColors(run.ToArray(), ink.ToArray(), width, true);
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
