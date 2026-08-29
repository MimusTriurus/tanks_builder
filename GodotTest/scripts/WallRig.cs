using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The wall knocked down by an actual solver: one rigid body per piece, and the
/// three things that break a wall told apart by where the force arrives.
///
/// <b>Why this replaces <see cref="WallFall"/> rather than joining it.</b> The
/// solved flights pick a destination for every piece first, so a shot whose whole
/// point is that it moves <i>two</i> bricks and leaves the rest standing cannot be
/// written down in them at all - an AP round through the middle is not a weaker
/// collapse, it is a different question. Same for a ram: what makes it fall four
/// ways is the courses either side of the breach losing the neighbour they lean
/// on, which is contact and nothing else. The parabolas stay under
/// <c>--solved</c>, because "honest is better" is a claim until the two are side
/// by side.
///
/// <b>What is given up is the byte-identical capture, and it is named here.</b>
/// Almost every number in this bench is a pixel diff of two runs, and a live
/// solver is the first thing on this board that does not repeat by construction.
/// Godot's own <c>--fixed-fps</c> pins the whole loop, physics included, which is
/// the nearest thing to that guarantee without baking - and whether it actually
/// held is measured, not assumed.
///
/// <b>Metres inside, cell radii outside.</b> The prop is laid out in fractions of
/// a cell, where a course piece is 0.099 tall - and the solver's penetration slop
/// is 0.02 of whatever unit it is given, which taken as cell radii is a fifth of
/// a course. So the bodies are built at <see cref="MetresPerCell"/> times the
/// plan and the transforms are divided back on the way to the multimesh. Gravity
/// is then the real one, a block falls in the time a block that size takes, and
/// every tolerance in the solver means what its documentation says it means.
/// </summary>
public sealed partial class WallRig : Node3D
{
    /// <summary>What hit it. Three, because the force arrives in three different
    /// shapes and that is the whole difference between them: a ram is a moving
    /// body against the face, HE is a field around a point, AP is a line
    /// through.</summary>
    public enum Strike { Ram, He, Ap }

    /// <summary>How many metres a cell radius is.
    ///
    /// <b>The board never declared this and a collapse cannot be written without
    /// it.</b> Derived rather than picked: the medium's hull measures 183px
    /// broadside (<c>AtlasSet.HullSpan</c>) and a medium tank's hull is about 6m,
    /// so the board runs at 30.5px to the metre; the tile plate is 248px across,
    /// so its radius is 124px, and that is 4.07m.
    ///
    /// It follows that these are not bricks: a course piece comes out 0.87m long
    /// and about 280kg. That is what the board's own scale makes of a wall a tank
    /// drives into, and it is better said than quietly corrected - shrink the
    /// pieces to real bricks and the wall is a garden edge on a hex four metres
    /// across.</summary>
    public const float MetresPerCell = 4.07f;

    /// <summary>Fired clay, near enough. Only its ratio to the impulses matters,
    /// but it is written as a density because that is the half of the ratio that
    /// has a right answer.</summary>
    public const float Density = 1900.0f;

    /// <summary>How fast a tank arrives, in metres a second, and how far past
    /// the face it gets before it stops.
    ///
    /// Half the medium's cruise: 240px/s is 7.9m/s at 30.5px to the metre, and a
    /// tank does not hit a wall at cruise - it noses into it. Measured at cruise
    /// and driving clean through, the whole wall was shoved a median of 9.8m,
    /// which is not a collapse but a break shot.
    ///
    /// And it stops rather than passing through, which is the other half of the
    /// same finding: what makes the wall fall four ways is the tank taking the
    /// middle out and standing there while the rest comes down round it. Driven
    /// on out the far side it takes the wall with it.</summary>
    public const float RamSpeed = 3.9f;

    /// <summary>How much of the box's own speed a piece carries away when the
    /// box lets go of it, as a fraction.
    ///
    /// <b>The ram is the only one of the three strikes that handed the masonry
    /// no momentum at all, and this is it.</b> A shell says what it does with an
    /// impulse - see <see cref="HeSpeed"/> and <see cref="ApSpeed"/> - and the
    /// ram said it with contact, which is exactly the thing it does not have: a
    /// standing brick is a static body and a hull is not in the solver at all,
    /// so nothing pushed it.
    ///
    /// <b>One, because that is the plain inelastic answer</b>: a forty-tonne
    /// hull against a ninety-kilo brick loses nothing worth counting, so the
    /// brick leaves at the speed of the thing that hit it. Above one it would be
    /// leaving faster than the tank, which is a break shot and not a ram; the
    /// levers below it are this and <see cref="ThawSpeed"/> together, since a
    /// piece shoved slower than the cascade threshold does not take its
    /// neighbours with it.</summary>
    public const float RamCarry = 1.0f;

    /// <summary>How far to the side of the heading a piece is thrown as well as
    /// forward, as a fraction of the shove, out at the edge of the band.
    ///
    /// <b>A uniform shove does not break a stack, it moves one.</b> Measured on
    /// the ring: handed the same speed every piece keeps its neighbours'
    /// velocity, so nothing shears, the leaf travels as a block and lands as
    /// one - two leaves came back with more seated masonry at 0.5 and 1.0 than
    /// with no shove at all (20 and 22 of 72 against 15), and only a shove half
    /// again the tank's own speed was violent enough to break it. That is a
    /// break shot bought with energy, and the wedge buys it with direction
    /// instead: the hull drives through what it can and pushes the rest aside,
    /// so a piece out at the flank leaves across the lane while one on the axis
    /// leaves along it, and the two come apart.
    ///
    /// Taken from the piece's own offset across the lane, which is what makes it
    /// a wedge rather than a scatter: deterministic, nothing hashed, and nought
    /// on the axis where a hull really does push straight.</summary>
    public const float RamSplay = 1.0f;

    /// <summary>Whether the ram hands its momentum over at all. On, and named
    /// here rather than left in the field's initialiser for
    /// <c>Recoil.ShearOnByDefault</c>'s reason.</summary>
    public const bool ShuntsByDefault = true;

    /// <summary>Whether a piece the box has been excepted from is given the
    /// speed of the box instead - <c>--no-shunt</c> on either bench, which puts
    /// back the ram that let masonry go and never pushed it. Static for
    /// <see cref="Breaches"/>'s reason.</summary>
    public static bool Shunts = ShuntsByDefault;

    /// <summary>How far past the cell's near edge the tank carries on, per
    /// unit of force, in metres.
    ///
    /// <b>Small on purpose, and this is the number that decides whether it falls
    /// four ways.</b> Driven on through, the tank bulldozes: measured, the whole
    /// wall shifted a median of 2.85m and every piece finished on the far side -
    /// a heap behind the tank, not a collapse around it. Stopped just inside the
    /// near face, the near leaf comes back over the bonnet, the far leaf goes on
    /// forward, and the ends fall sideways into the gap between them, because
    /// what held them was each other.
    ///
    /// It is measured from the <i>cell</i> now rather than from the first brick
    /// it meets - see <see cref="Ram"/>. The two land within a metre of each
    /// other on this wall, and only one of them is a promise about where the
    /// tank ends up.</summary>
    public const float RamBite = 1.2f;

    /// <summary>Half the distance between two neighbouring cell centres, in
    /// metres - the flat-top cell's apothem, and the whole of the ram's
    /// geometry. One of them is how far a tank travels to put its contact point
    /// over this hexagon; two of them is the middle of it, which is as far as it
    /// is allowed to go.</summary>
    public static readonly float Apothem = MetresPerCell * Mathf.Sqrt(3.0f) * 0.5f;

    /// <summary>How fast each shot throws a piece it catches, in metres a
    /// second, and how far the burst reaches, in metres. The reach is what makes
    /// HE a breach instead of a demolition - past it the wall is only losing its
    /// support.
    ///
    /// <b>Speeds, and they were newton-seconds - 2200 and 2000.</b> Impulse is
    /// the more physical of the two: a blast delivers momentum to the face it can
    /// see and a shell delivers its own, so neither cares what the piece weighs.
    /// It is also the one that breaks the moment the wall changes size. Cutting
    /// the wall to the cell's side took every piece to 0.69 of its length, which
    /// is 0.33 of its mass, so the same newton-seconds became three times the
    /// speed: measured on one seed and one shot, the HE heap went from reach 1.10
    /// with a single piece off the cell to reach 5.93 with thirty-six, and AP from
    /// 1.72 and three to 8.22 and twelve. Quoted as a speed the shot is scale-free
    /// by construction - the cell it has to stay on did not shrink, only the
    /// bricks did, and how far a piece thrown at v goes in cells is v squared over
    /// g times the cell, which has no brick in it. The two numbers are the old
    /// ones divided by the mass they were tuned against, so the shot they describe
    /// is the shot that was tuned.</summary>
    public const float HeSpeed = 7.6f;
    public const float ApSpeed = 6.9f;

    /// <summary>How wide a hole each shot makes, <b>in course bricks</b> - the
    /// burst's radius and the round's bore.
    ///
    /// <b>Bricks, because a hole is read against the wall it is in.</b> These
    /// were 1.2 m and 0.55 m, and the second was already argued from the piece it
    /// knocks out - half a metre being one block up and one down against a block
    /// 0.87 m long. Cutting the wall to the cell's side took the block to 0.60 m
    /// and left both numbers where they were, so the same two shots became
    /// different shots: measured, HE moved 57 of 59 pieces where it had moved 38
    /// of 58, and AP 16 where it had moved 9 - a demolition and a gap instead of a
    /// breach and a hole. Quoted in bricks they say what they are for, and the
    /// bench keeps three shots that answer differently.
    ///
    /// The brick they are measured against is <b>measured off the wall</b> rather
    /// than read from <see cref="WallKit"/>: the plan has been cut to the cell by
    /// the time it gets here, so the nominal is not the built size and reading it
    /// would be reading the very number the fit exists to replace.</summary>
    public const float HeReach = 1.38f;
    public const float ApBore = 0.63f;

    /// <summary>The course brick as it was actually built, in metres - its length
    /// and its height. Largest rather than mean, because the size jitter only ever
    /// cuts a brick short, so the largest is the nominal.</summary>
    private float _brick = WallKit.BrickLong * MetresPerCell;
    private float _course = WallKit.BrickTall * MetresPerCell;

    /// <summary>Why the bore is not a ray.
    ///
    /// <b>The hole an AP round leaves is wider than the round, and that is the
    /// whole of why this is not a ray.</b> Two rays hit two pieces - one leaf
    /// each - and two pieces leaving a wall of eighty-seven centimetre blocks is
    /// not a hole, it is two blocks missing; what a shot through masonry
    /// actually does is spall a patch out round the entry. So everything whose
    /// centre lies within the bore of the line goes, and the impulse falls off
    /// across it, so the middle of the patch is thrown and its rim is only
    /// dropped.
    ///
    /// <b>It is a calibre, not a dial.</b> The force multiplier moves the speed
    /// and never the bore: a heavier round goes through harder, and the reason
    /// only the burst has a radius is written where that radius is. Measured
    /// against the piece it knocks out, which is why it is quoted in bricks - one
    /// block up and one down, and the courses either side stay where they
    /// are.</summary>
    public const float ApBite = 0.5f;
    public const float ApSpall = 0.2f;

    /// <summary>How tall the ramming tank is, in metres, and how high up it
    /// carries its gun.
    ///
    /// <b>The gun height is where AP is aimed, and that is a board number rather
    /// than a measurement of the wall.</b> Half of the prop's greatest height is
    /// a line that leaves the low end of a stepped ruin entirely; the median
    /// piece is down among the bottom courses, behind the rubble already lying
    /// against the foot - measured, both of those put the hole somewhere it
    /// could not be seen. What decides how high a hole in a wall is is what shot
    /// at it.
    ///
    /// <b>And it stands on the ground rather than being buried to the waist,
    /// which it was.</b> A box placed at the contact point the board hands over
    /// and centred on its own origin runs from a metre underground to a metre
    /// up - half of it below the floor, against a wall that stands 2.20m - and
    /// nothing said so, because a ram that reaches half a wall still brings a
    /// wall down; it only leaves more of it lying where it was laid. Measured
    /// on the ring: the heap went from <c>reach 5.08</c> to <c>1.58</c> for the
    /// same count let go. The rig's own ram lifts its box by half its height,
    /// and the driven ram's window is centred on the courses the same
    /// way.</summary>
    public const float TankTall = 2.0f;
    public const float ApAim = TankTall * 0.75f;

    /// <summary>How fast a piece has to be going before it takes its neighbour
    /// with it, in metres a second.
    ///
    /// <b>Unbounded, the contact hook brings the whole wall down every time.</b>
    /// Measured: an AP round told to move two pieces moved 38 of 58 and left a
    /// heap, because a thawed piece settles into the solver's penetration slop,
    /// touches its neighbour at a few centimetres a second, thaws it, and the
    /// wall unfreezes itself from the inside out. A brush is not a blow, and the
    /// threshold is what says so.</summary>
    public const float ThawSpeed = 2.0f;

    private readonly List<RigidBody3D> _bodies = new();
    private readonly HashSet<int> _live = new();
    private readonly HashSet<int> _gone = new();
    private readonly List<float> _lain = new();
    /// <summary>Masonry per side as it was laid, and how much of each has been
    /// let go by anything at all. The count is kept in <see cref="Thaw"/> so that
    /// a piece knocked out by a falling neighbour counts against its section the
    /// same as one the shot reached - a leaf does not care what took its bricks.
    /// What the cascade does <i>not</i> do is set the collapse off: that is asked
    /// only where a strike lands, so a leaf coming down cannot walk round the
    /// ring bringing its neighbours with it.</summary>
    private readonly int[] _stood = new int[6];
    private readonly int[] _taken = new int[6];
    private readonly HashSet<int> _breached = new();

    /// <summary>Frozen masonry whose footing may just have gone - queued by
    /// <see cref="Thaw"/>, judged by <see cref="Undermined"/>. A standing piece
    /// is held by the freeze flag and not by anything under it, so what a
    /// collapse leaves overhanging the mitre hangs on air for ever unless
    /// somebody asks the question; this is where the question waits to be
    /// asked.</summary>
    private readonly HashSet<int> _perch = new();

    /// <summary>Pieces that have crumbled away and been taken out of the
    /// solver. Kept so the clearing is done once: the body stays in
    /// <see cref="_bodies"/> for every caller that indexes against the plan,
    /// it just no longer collides with anything.</summary>
    private readonly HashSet<int> _cleared = new();

    private AnimatableBody3D? _tank;
    private Vector3 _tankSize;

    /// <summary>The ram box's shape - see <see cref="Hull"/>.</summary>
    private CollisionShape3D? _tankHull;
    private Vector3 _tankRun;
    private float _tankLeft;
    private Vector3 _tankTip;
    private Vector3 _tankInto;
    private float _tankLane;

    /// <summary>Which section the rig's own ram named on the way in - see
    /// <see cref="Meets"/>. Latched when the charge is laid rather than read back
    /// off the pieces it took, for <see cref="Breach"/>'s reason: a strike brings
    /// down the section it went into, and a tally is not a name.</summary>
    private int _tankFace = -1;
    private float _tankGone;

    /// <summary>Whether the box is synced from a live tank rather than run by
    /// the rig - see <see cref="Mount"/>. The two never coexist: the rig's own
    /// ram is a shot on a board with no tank, the driven box is a tank on a
    /// board with no shot.</summary>
    private bool _tankDriven;

    /// <summary>Where the driven box's contact point was last frame, so a
    /// frame's forward progress is a difference rather than a speed handed in -
    /// a reversing hull rams nothing, and a pivot advances nothing.</summary>
    private Vector3 _tankAt;

    /// <summary>Pieces the driven box holds a collision exception with - what
    /// it was born inside, and what was released already inside it. Kept so
    /// each pair is excepted once; cleared with the box.</summary>
    private readonly HashSet<int> _sparedByBox = new();

    /// <summary>How many pieces the safety sweep swallowed mid-push - pushed
    /// hard enough to bury their centre inside the box, then excepted. The
    /// wedge exists to make this rare, and the count is what says whether it
    /// is: a ram that swallows dozens is a prow shaped wrong, and that is a
    /// number rather than an impression. Reported at <see cref="Dismount"/>,
    /// reset at <see cref="Mount"/>. Birth exceptions do not count - what the
    /// box started inside was never pushed at all.</summary>
    private int _swallowed;

    /// <summary>How far back the wedge's slope runs from the leading edge, in
    /// metres - set with the shape in <see cref="Mount"/>, read by the sweep
    /// that has to know where the shape's inside actually is.</summary>
    private float _tankBow;

    /// <summary>The wedge's own corners, in the box's local metres - what the
    /// solver actually holds, kept so <see cref="Hull"/> can hand the overlay
    /// the true shape. A frame drawn as the bounding box while the solver
    /// holds a prow is exactly the misleading picture the overlay exists to
    /// prevent. Null while the box is the rig's own plain one.</summary>
    private Vector3[]? _tankProw;
    private WallKit.Plan _plan = null!;
    private float _clock = -1.0f;
    private (Vector3 From, Vector3 To)? _beam;
    private Strike _shot = Strike.Ram;
    private float _force = 1.0f;

    /// <summary>Seconds since the strike, negative while the wall stands.
    /// </summary>
    public float Clock => _clock;
    public bool Struck => _clock >= 0.0f;
    public Strike Shot => _shot;

    /// <summary>What one setting of the force dial buys, in the unit the chosen
    /// shot is actually measured in. It goes under the slider, because a
    /// multiplier on its own answers neither of the questions it is dragged to
    /// answer.</summary>
    public static string Costs(Strike shot, float force) => shot switch
    {
        // The depth is quoted clamped, because it is clamped: a dial that
        // says twelve metres while the tank parks at three and a half is a dial
        // that has stopped describing the shot.
        Strike.Ram => force <= 0.0f ? "no ram - the tank stays put"
            : $"{RamSpeed * force:F1} m/s, "
              + $"{Mathf.Min(RamBite * force, Apothem):F1} m of {Apothem:F1} "
              + "onto the cell",
        // The reach is quoted because it moves: a heavier charge reaches
        // further by the cube root of itself, and the panel's job is to say what
        // the number costs rather than what it is called.
        Strike.He => $"{HeSpeed * force:F1} m/s at the burst over "
                     + $"{HeReach * Mathf.Pow(Mathf.Max(force, 0.0f), 1.0f / 3.0f):F1}"
                     + " bricks",
        _ => $"{ApSpeed * force:F1} m/s down a {ApBore * 2.0f:F1} brick bore",
    };
    public int Count => _bodies.Count;

    /// <summary>Where the tank is and how big, in the cell's own units, or null
    /// when nothing is ramming.
    ///
    /// <b>Solved here and drawn there.</b> The first version put a mesh on the
    /// body, which is six metres of box at the world origin of a board whose
    /// units are pixels - a six pixel speck in the corner of the picture, while
    /// the thing it was meant to show was a wall falling over for no visible
    /// reason. The bodies live in the prop's frame; only the prop knows how to
    /// put that on screen.</summary>
    public (Transform3D At, Vector3 Size)? Ram()
    {
        if (_tank is null)
            return null;
        Transform3D t = _tank.Transform;
        return (new Transform3D(t.Basis, t.Origin / MetresPerCell),
                _tankSize / MetresPerCell);
    }

    /// <summary>The ram box as the solver holds it: where it is in the cell's
    /// own units, how big it is, and whether its shape is switched on. Null
    /// when there is no box - which on a board that drives its own tank is
    /// always, because a driven ram is an event and not a body (see
    /// <see cref="Rammed"/>).
    ///
    /// <b>The shape's own offset comes with it</b>, so a frame drawn off the
    /// body alone cannot drift from where the solver puts the shape.</summary>
    public (Transform3D At, Vector3 Size, bool Live, Vector3[]? Prow)? Hull()
    {
        if (_tank is null || _tankHull is null)
            return null;
        Transform3D t = _tank.Transform * _tankHull.Transform;
        return (new Transform3D(t.Basis, t.Origin / MetresPerCell),
                _tankSize / MetresPerCell,
                !_tankHull.Disabled,
                _tankProw);
    }

    /// <summary>How long the tracer hangs about, and how much of that it spends
    /// at full before it starts to go, in seconds.
    ///
    /// <b>A round that is drawn for one frame is not drawn.</b> The flight
    /// itself is instantaneous here - the impulse lands on the frame the button
    /// is pressed - so this is a tracer rather than a projectile, and what it
    /// has to survive is the eye rather than the physics. Held first and faded
    /// after, for the reason the harness's shell smoke fades slower than
    /// linear: the line is thin, and something thin that dims evenly reads as
    /// having been switched off rather than having gone past.</summary>
    public const float BeamLife = 0.55f;
    public const float BeamHold = 0.12f;

    /// <summary>Where the round went, in the cell's own units, and how much of
    /// it is left to draw - or null when there is nothing in the air.
    ///
    /// <b>The line that is drawn is the line that was fired.</b> AP's is
    /// literally the segment handed to <c>IntersectRay</c>, and HE's ends at the
    /// burst point the impulses are measured from, so a picture that disagrees
    /// with where the damage landed is a picture that cannot happen. The ram has
    /// none: the tank <i>is</i> what arrives, and a beam in front of it would be
    /// a second answer to which way it came.
    ///
    /// Solved here and drawn there, the same split as <see cref="Ram"/>.
    /// </summary>
    public (Vector3 From, Vector3 To, float Fade)? Beam()
    {
        if (_beam is not { } line || _clock < 0.0f || _clock > BeamLife)
            return null;
        float fade = _clock <= BeamHold ? 1.0f
            : 1.0f - (_clock - BeamHold) / (BeamLife - BeamHold);
        return (line.From / MetresPerCell, line.To / MetresPerCell, fade);
    }

    /// <summary>How many pieces are still moving. What the bench waits on, and
    /// the only honest end to a live collapse: there is no baked length to run
    /// to.</summary>
    public int Awake
    {
        get
        {
            int n = 0;
            foreach (RigidBody3D b in _bodies)
                if (!b.Freeze
                    && (b.LinearVelocity.LengthSquared() > Still * Still
                        || b.AngularVelocity.LengthSquared() > Still * Still))
                    n++;
            return n;
        }
    }

    /// <summary>Under this it is not moving, in metres a second.
    ///
    /// <b>Asked of the velocity rather than of the sleep flag, because the flag
    /// answers a different question.</b> A piece resting on the tank never
    /// sleeps - a kinematic body keeps its contacts live - so with the rubble
    /// lying against the bonnet the count sat at 22 fifteen seconds after
    /// everything had visibly stopped, and the settle report never fired.
    /// </summary>
    public const float Still = 0.05f;

    /// <summary>How many pieces the shot has let go of. The other half of
    /// <see cref="Awake"/>, and the one that says what kind of shot it was: two
    /// for a round through the middle, most of the wall for a tank.</summary>
    public int Loose => _live.Count;

    /// <summary>How many pieces went over the edge of the board. Counted rather
    /// than hidden: rubble on a neighbour is what was asked for, rubble in the
    /// void is the board being too small for the shot.</summary>
    public int Fell => _gone.Count;

    /// <summary>How near its own seat a piece has to be to count as still
    /// standing, in bricks - see <see cref="Stuck"/>.</summary>
    public const float Seated = 0.25f;

    /// <summary>How upright it has to have stayed, as the cosine between
    /// the block's own up axis then and now - see <see cref="Stuck"/>. A
    /// brick that dropped straight down onto the rubble under it kept its
    /// seat to within a brick and is not a course; one still laid is.</summary>
    public const float Upright = 0.985f;

    /// <summary>How many of the pieces let go of are still laid where they
    /// were put - within a quarter of a brick of their seat and still upright.
    ///
    /// <b>Let go of and fallen down are two different facts with one count, and
    /// this is the second one.</b> <see cref="Loose"/> says the rig released a
    /// piece; whether anything then moved it is the solver's business, and dry
    /// masonry with no bed joint is a stack of boxes that stands perfectly well
    /// once it has been unfrozen. So a leaf can report <c>96/96</c> in
    /// <see cref="Leaves"/> and still be standing in the picture, which is
    /// exactly the shape of "the ram only took the outer part of the wall".
    ///
    /// <b>Measured, and the two cases are far apart.</b> A four-leaf sample met
    /// from outside keeps <b>19 of the 111</b> it let go of, and the picture is a
    /// tumbled heap; the leaf of the ring the tank drives out of keeps <b>36 of
    /// 98</b>, at the wall's own full height, because the hull's front face
    /// begins level with that masonry and can never cross it. That is the ring's
    /// standing debt - the tank does not fit in it - arriving where it can be
    /// seen.
    ///
    /// <b>Seated and upright, not seated alone.</b> A brick that dropped
    /// straight down onto the rubble under it keeps its seat to within a brick
    /// and is not a course; asked by distance alone the four-leaf sample - a
    /// visible tumble with nothing laid left in it - came back with 38 of its
    /// 111 standing. Turned by more than <see cref="Upright"/> it is rubble
    /// whatever it is sitting on: 38 becomes 19.
    ///
    /// <b>And masonry only</b> - <c>Clearance</c>'s sentence in a sixth place.
    /// The apron was laid on the ground and was never standing, so a shot that
    /// let go of it without moving it has not left a wall behind.
    ///
    /// It still counts a little high, because a bottom course that settled in
    /// place is honestly still a course - so it is a floor under what is
    /// standing rather than a measure of it.</summary>
    public int Stuck
    {
        get
        {
            if (_plan is not { } plan)
                return 0;
            float near = _brick * Seated;
            int held = 0;
            foreach (int k in _live)
            {
                if (k >= plan.Blocks.Count || plan.Blocks[k].Course < 0)
                    continue;
                Transform3D laid = plan.Blocks[k].Frame;
                Transform3D now = _bodies[k].Transform;
                if ((now.Origin - laid.Origin * MetresPerCell).Length() >= near)
                    continue;
                if (now.Basis.Y.Dot(laid.Basis.Y) < Upright)
                    continue;
                held++;
            }
            return held;
        }
    }

    /// <summary>How many sections have come down whole - see <see cref="Breach"/>.
    ///
    /// <b>Printed because the piece count cannot say it.</b> A round that levels
    /// the leaf it was aimed at and one that levels that leaf and both its
    /// neighbours are 99 pieces against 296, and the difference between those two
    /// numbers reads as a bigger charge rather than as a rule reaching where it
    /// was not meant to. Sections is the unit the ask was made in.</summary>
    public int Broken => _breached.Count;

    /// <summary>What each section has lost, out of what it stood with:
    /// <c>side:gone/laid</c> per leaf that carried masonry.
    ///
    /// <b>The piece total cannot say which leaf.</b> A leaf driven through and a
    /// leaf caught by the corner of a band are the same arithmetic on
    /// <see cref="Loose"/> and entirely different pictures, and on a ring that
    /// is the only question a ram is ever about. <see cref="Broken"/> answers it
    /// one bit at a time and only past <see cref="BreachShare"/>; this is the
    /// reading underneath it.
    ///
    /// Read off the two tallies <see cref="Breach"/> already judges rather than
    /// counted again, so a printed line and a fallen leaf cannot disagree.
    /// </summary>
    public string Leaves()
    {
        var said = new List<string>();
        for (int s = 0; s < _stood.Length; s++)
            if (_stood[s] > 0)
                said.Add($"{s}:{_taken[s]}/{_stood[s]}");
        return said.Count > 0 ? string.Join(" ", said) : "no sections";
    }

    /// <summary>What one section has lost, out of what it stood with.
    ///
    /// <b>The reading <see cref="Leaves"/> formats, handed out as numbers.</b>
    /// A tour that rams every leaf in turn has to say what each ram did to the
    /// leaves it was not aimed at, and that is a difference between two moments
    /// rather than a line of text - so the caller needs the tallies, not the
    /// sentence. Read off the same two arrays <see cref="Breach"/> judges, for
    /// the reason <see cref="Leaves"/> is: a printed number and a fallen leaf
    /// cannot disagree.
    ///
    /// A side nobody laid masonry on answers <c>(0, 0)</c> rather than throwing:
    /// how many sides a wall has is the recipe's, and a caller walking all six
    /// of a one-sided wall is asking a fair question.</summary>
    public (int Gone, int Laid) Section(int side) =>
        side >= 0 && side < _stood.Length
            ? (_taken[side], _stood[side])
            : (0, 0);

    /// <summary>How long a piece that has been let go lies still before it starts
    /// to go, and how long it takes to go, in seconds.
    ///
    /// <b>Two numbers rather than one, because the rubble has to be looked at
    /// before it leaves.</b> The whole point of the three shots is what the heap
    /// ends up like - <c>reach</c>, <c>top</c>, how much of the cell it covers -
    /// and a piece that starts vanishing on the frame it lands is a heap nobody
    /// ever sees. The lie-still comes first and the going is slower than it, so
    /// what reads on screen is masonry being cleared and not masonry being
    /// deleted.</summary>
    public const float Linger = 2.0f;

    /// <summary>How long the going takes. See <see cref="Linger"/>.</summary>
    public const float Crumble = 4.0f;

    /// <summary>Whether fallen pieces go at all. On, and named here rather than
    /// left in the field's initialiser for <c>Recoil.ShearOnByDefault</c>'s
    /// reason: a default nobody can point at is a default nobody notices being
    /// changed.</summary>
    public const bool CrumblesByDefault = true;

    /// <summary>Whether fallen pieces go at all - <c>--no-crumble</c> on either
    /// bench. Static because it is a question about the effect and not about one
    /// wall, the same way <see cref="Gunnery.TraverseLevel"/> is not held on a
    /// machine.</summary>
    public static bool Crumbles = CrumblesByDefault;

    /// <summary>How much of one section a strike has to take before the rest of
    /// it comes down with it: an eighth.
    ///
    /// <b>A share of the section and never a count, because a section is however
    /// long the cell's side is cut into.</b> The same eighth is 11 pieces of a
    /// ring's leaf and 8 of a single-sided wall, and both are the same statement
    /// about masonry that has lost its footing.
    ///
    /// <b>And it is what keeps a graze from being a breach.</b> A hull leaving
    /// the ring clips one or two bricks off the mitred corner of the leaf next to
    /// the one it drives through - measured, two of 89 - and a rule that levelled
    /// a section on first contact would bring that one down too. Two per cent is
    /// a long way under an eighth; the leaf actually driven through loses most of
    /// itself in the same second.</summary>
    public const float BreachShare = 0.125f;

    /// <summary>Whether a section that has lost <paramref name="gone"/> of its
    /// <paramref name="pieces"/> is breached - see <see cref="BreachShare"/>.
    ///
    /// Rounded up, which is what makes a section too small to have an eighth
    /// still answer: the ceiling of any share of one piece or more is one, so a
    /// stub the profile has cut down to three blocks comes down when it loses
    /// one. Written with a floor under it as well at first, and that floor was
    /// dead the moment it was typed.</summary>
    public static bool Breached(int gone, int pieces) =>
        pieces > 0 && gone >= Mathf.CeilToInt(pieces * BreachShare);

    /// <summary>Which shots bring a whole section down, and it is every one but
    /// AP.
    ///
    /// <b>The same exception, said a second time.</b> AP already opts out of the
    /// cascade (<see cref="Thaw"/>'s <c>spreads</c>), and for the same reason: a
    /// round through the middle takes what stands on its line and leaves the rest
    /// standing round the gap, so "the wall did not notice" is the whole shot. A
    /// ram and a burst are the opposite claim - they break <i>into</i> masonry,
    /// and what a breach does is bring the section down.</summary>
    public static bool Breaching(Strike shot) => shot != Strike.Ap;

    /// <summary>Whether a section comes down whole at all. On, and named here
    /// rather than left in the field's initialiser for
    /// <c>Recoil.ShearOnByDefault</c>'s reason.</summary>
    public const bool BreachesByDefault = true;

    /// <summary>Whether a breach takes the whole section - <c>--no-breach</c> on
    /// either bench, which puts back the wall that only ever lost what the strike
    /// itself reached. Static for <see cref="Crumbles"/>'s reason.</summary>
    public static bool Breaches = BreachesByDefault;

    /// <summary>Whether a strike is confined to the section it struck. On, and
    /// named here rather than left in the field's initialiser for
    /// <c>Recoil.ShearOnByDefault</c>'s reason.</summary>
    public const bool SegmentedByDefault = true;

    /// <summary>Whether a strike acts on one section and no other -
    /// <c>--wide-blast</c> on either bench puts back the fields that crossed
    /// them. Named for the blast because that was the only one of them it cut
    /// when it was written, and kept rather than renamed so every command
    /// written down with it still works - the name is the narrower of the two.
    ///
    /// <b>The section is the unit the board already counts in.</b> A wall stands
    /// on a cell edge, <c>Recipe.Sides</c> counts edges, <c>WallProp.Bars</c>
    /// asks per edge and <see cref="Breach"/> brings one down; a blast that
    /// reaches into the two edges either side leaves "which section did that
    /// shell take" with no answer, and on a ring one round flattens half the
    /// cell.
    ///
    /// <b>Confining the collapse was not enough, and the reason is worth
    /// keeping.</b> Naming the section a burst <i>breaches</i> stopped the
    /// neighbours coming down whole, and left the blast itself still reaching
    /// them: measured on the ring, force 10 thaws and throws <b>78 of each
    /// neighbour's 96 pieces</b>, which is a leaf destroyed by any reading but
    /// the rule's. The field has to be confined, not just the verdict.
    ///
    /// <b>Three fields, and the blast was only the first of them.</b> A strike
    /// reaches masonry three ways - the blast field in <see cref="Burst"/>, the
    /// hull's band in <see cref="Reached"/>, and the cascade in
    /// <see cref="Thaw"/> - and confining one leaves the other two crossing.
    /// Measured by the ram tour, which drives every leaf of a ring in turn: with
    /// only the blast cut, a leg spilled up to <b>10 pieces of a neighbour's
    /// 96</b> off the mitred corner; cutting the band took the worst to 6 and
    /// left seven legs of ten clean; cutting the cascade as well took every leg
    /// to <b>none</b>. The three are one rule and go together.
    ///
    /// A falling piece of the apron waking masonry falls out of this too, and
    /// that is the right way round: the apron is rubble that has lain on the
    /// ground since it was laid, so a hull ploughing it into a standing leaf is
    /// not that leaf being struck - the same sentence <see cref="Reaches"/>,
    /// <c>WallProp.Bars</c> and <see cref="Against"/> each say about their own
    /// question.
    ///
    /// Costs nothing on a wall that is one section: every piece is the face's,
    /// so <c>Wall.tscn</c> answers to the digit either way - which is the
    /// regression this is checked by, and it does, on all five of its shots.
    /// </summary>
    public static bool Segmented = SegmentedByDefault;

    /// <summary>Whether a burst that went off against section
    /// <paramref name="face"/> acts on a piece standing in section
    /// <paramref name="side"/>.
    ///
    /// <b>Rubble is reached whatever the face</b> (<paramref name="side"/> below
    /// zero): the apron lies on the ground inside this cell and has been down
    /// since it was laid, so a shell kicking it about cannot read as a
    /// neighbouring wall breaking - which is the whole of what is confined here.
    ///
    /// <b>A burst with no face reaches nothing, which is <see cref="Burst"/>
    /// saying out loud what it already claimed.</b> "It goes off where it was
    /// fired, which touches nothing - the honest answer" is written above the
    /// line that places it, and was not true of the line that distributes it: a
    /// round put through a gap in the masonry went off at the muzzle and threw
    /// whatever stood within a radius of the gun, which on a ring is the masonry
    /// at the shooter's back.</summary>
    public static bool Reaches(int face, int side) =>
        !Segmented || (face >= 0 && (side < 0 || side == face));

    /// <summary>
    /// How much of piece <paramref name="i"/> is still there: one whole, zero
    /// gone.
    ///
    /// <b>A standing piece is always whole, and that is the whole of which pieces
    /// go.</b> What has not been let go of is masonry, and masonry does not clear
    /// itself; the shot decides what falls and this only decides what happens to
    /// it afterwards. So a wall left standing round an AP hole stays exactly as it
    /// was drawn, however long anybody looks at it.
    ///
    /// <b>Measured off time spent lying still, which is what makes it
    /// monotone.</b> A piece nudged again by the one landing on it has been lying
    /// still for however long it had - a brick cannot un-crumble - so the clock
    /// only ever adds. Written as "reset it when it moves" instead, a heap still
    /// shuffling into place keeps putting its pieces back together.
    /// </summary>
    public float Left(int i)
    {
        if (!Crumbles || i < 0 || i >= _lain.Count || !_live.Contains(i))
            return 1.0f;
        float going = _lain[i] - Linger;
        return going <= 0.0f
            ? 1.0f : Mathf.Clamp(1.0f - going / Crumble, 0.0f, 1.0f);
    }

    /// <summary>How many pieces have gone completely. Reported because it is the
    /// one thing about this that a still picture cannot tell from a shot that
    /// never moved them: an empty cell is either cleared rubble or a wall that
    /// was never hit.</summary>
    public int Crumbled
    {
        get
        {
            int n = 0;
            for (int i = 0; i < _bodies.Count; i++)
                if (Left(i) <= 0.0f)
                    n++;
            return n;
        }
    }

    /// <summary>Still driving. The settle report has to wait for it: a wall that
    /// has not been hit yet is quiet, and quiet is what that report is watching
    /// for - measured, it fired at 0.60s while the tank was still on its way.
    /// </summary>
    public bool Ramming => _tank is not null && _tankLeft > 0.0f;

    /// <summary>Which collision bit everything this rig raises lives on - the
    /// floor, the masonry and the bench's own ram box alike.
    ///
    /// <b>Every rig is centred on the world origin, and that is why this
    /// exists.</b> The bodies live in prop-local units with no parent transform
    /// over them (see <c>WallProp.Raise</c>), so every wall on a board stands
    /// in the same place in the one shared <c>World3D</c> - five rigs, one
    /// origin, all overlapping. Frozen masonry hid it (static against static
    /// never collides), but rubble is live: ramming one wall threw its pieces
    /// through another wall's heap in phantom space, and on screen the other
    /// wall's rubble scattered by itself - measured, a charge into the far
    /// sample smeared the ring's settled heap across its whole cell. The same
    /// overlap is what blew the solver up on late tour legs (reach 409-434
    /// with no kinematic body left in the world): by the eighth ram, pieces
    /// were being released straight into seven other walls' rubble.
    ///
    /// Nothing real is given up: in origin-centred frames the geometry between
    /// two walls was never on the board, so cross-rig contact was always
    /// phantom, never physics. Masked with the same bit it carries as layer,
    /// so a rig collides with itself and nobody else; past 32 walls the bits
    /// wrap, which is a board nobody has drawn.</summary>
    public int Channel { get; set; }

    /// <summary>The layer-and-mask bit <see cref="Channel"/> names.</summary>
    private uint Bit => 1u << (Channel & 31);

    /// <summary>Stand the wall up as bodies, on ground handed in as triangles in
    /// cell units.
    ///
    /// <b>Everything starts asleep, because dry masonry does not stand.</b>
    /// Measured on the Blender build: woken and left alone, the wall settles a
    /// median of 16mm and its worst brick travels 0.74m inside forty frames.
    /// Asleep it stands, and only what the shot reaches wakes - which is also the
    /// picture that was asked for, since two of the three shots are supposed to
    /// leave most of the wall up.
    ///
    /// The ground is passed in rather than built here, and from the same corners
    /// the renderer draws: a second description of the floor would disagree
    /// exactly where it is hardest to check, and the failure is rubble resting on
    /// nothing.</summary>
    public void Raise(WallKit.Plan plan, IReadOnlyList<Vector3> groundTris)
    {
        Clear();
        _plan = plan;
        // The brick the shots are measured against, off the wall that got built.
        float nose = 0.0f, tall = 0.0f;
        foreach (WallKit.Block b in plan.Blocks)
            if (b.Course >= 0)
            {
                nose = Mathf.Max(nose, b.Half.X * 2.0f);
                tall = Mathf.Max(tall, b.Half.Y * 2.0f);
            }
        _brick = (nose > 0.0f ? nose : WallKit.BrickLong) * MetresPerCell;
        _course = (tall > 0.0f ? tall : WallKit.BrickTall) * MetresPerCell;

        var floor = new StaticBody3D
        {
            Name = "Ground",
            CollisionLayer = Bit,
            CollisionMask = Bit,
        };
        var tris = new Vector3[groundTris.Count];
        for (int i = 0; i < groundTris.Count; i++)
            tris[i] = groundTris[i] * MetresPerCell;
        floor.AddChild(new CollisionShape3D
        {
            Shape = new ConcavePolygonShape3D { Data = tris },
        });
        floor.PhysicsMaterialOverride =
            new PhysicsMaterial { Friction = 0.9f, Bounce = 0.0f };
        AddChild(floor);

        foreach (WallKit.Block b in plan.Blocks)
        {
            Vector3 size = b.Half * 2.0f * MetresPerCell;
            var body = new RigidBody3D
            {
                Mass = size.X * size.Y * size.Z * Density,
                // Frozen static, not asleep. Asleep does not hold: measured, all
                // 58 were awake on the third frame and the stack had already
                // settled a median of 14mm - which is the solver's penetration
                // slop, not masonry, and it reads as a wall that sags the moment
                // you look at it. Frozen, the wall stands exactly as it was laid
                // and costs nothing; what the shot reaches is thawed, and what a
                // thawed piece touches thaws in turn, which is how the hole
                // spreads without anybody writing down how far it should.
                Freeze = true,
                FreezeMode = RigidBody3D.FreezeModeEnum.Static,
                CanSleep = true,
                CollisionLayer = Bit,
                CollisionMask = Bit,
                Transform = new Transform3D(b.Turn, b.Seat * MetresPerCell),
                PhysicsMaterialOverride =
                    new PhysicsMaterial { Friction = 0.8f, Bounce = 0.0f },
            };
            body.AddChild(new CollisionShape3D
            {
                Shape = new BoxShape3D { Size = size },
            });
            AddChild(body);
            _bodies.Add(body);
            _lain.Add(0.0f);
            int s = b.Course >= 0 ? b.Side : -1;
            if (s >= 0 && s < _stood.Length)
                _stood[s]++;
        }
    }

    /// <summary>Which section piece <paramref name="i"/> belongs to, or -1 for
    /// the apron. <b>Rubble is not masonry</b> - <c>Clearance</c>'s sentence
    /// arriving in a fourth place: what is already lying on the ground is not
    /// part of a section and cannot be brought down with one.</summary>
    private int SideOf(int i) =>
        _plan is { } plan && i >= 0 && i < plan.Blocks.Count
        && plan.Blocks[i].Course >= 0
            ? plan.Blocks[i].Side : -1;

    /// <summary>Bring the whole of one section down.
    ///
    /// Once only, and the guard is taken before anything is let go: every piece
    /// this releases counts against its own section in <see cref="Thaw"/>, which
    /// would ask to breach the section again on the first one.</summary>
    private void Collapse(int side)
    {
        if (side < 0 || side >= _stood.Length || !_breached.Add(side))
            return;
        for (int i = 0; i < _bodies.Count; i++)
            if (SideOf(i) == side)
                Thaw(i);
    }

    /// <summary>Bring down section <paramref name="side"/>, if a strike has now
    /// taken enough of it. Asked where a strike lands and never in
    /// <see cref="Thaw"/>: see <see cref="_stood"/>.
    ///
    /// <b>Named, never inferred from what fell, and the ring is why.</b> A burst
    /// has a radius and the radius grows with the dial - the one thing about the
    /// three strikes that <see cref="Burst"/>'s own docstring already says - so a
    /// heavy shell honestly reaches into the leaves either side of the one it
    /// went off against. Asked per piece, that made every leaf the blast touched
    /// bring itself down: measured on the ring, one round took <b>three sections
    /// of six</b> from force 1.5 up.
    ///
    /// <b>The share cannot be what spares them, and this was measured before it
    /// was designed around.</b> Reach of the burst into each leaf, as a fraction
    /// of its 96 pieces: the struck leaf takes 0.28 at force 0.25 and 0.82 at
    /// force 1, while a neighbour takes 0.08 at 1.5, 0.36 at 3 and <b>0.81</b> at
    /// 10. The two ranges overlap end to end, so no threshold orders them - one
    /// that lets a lightly hit leaf fall lets a neighbour fall too, and one high
    /// enough to spare a neighbour at full charge stops the struck leaf falling
    /// at the half force the whole rule exists for.</summary>
    private void Breach(int side, Strike shot)
    {
        if (!Breaches || !Breaching(shot))
            return;
        if (side >= 0 && side < _stood.Length
            && Breached(_taken[side], _stood[side]))
            Collapse(side);
    }

    public void Clear()
    {
        foreach (Node child in GetChildren())
        {
            RemoveChild(child);
            child.QueueFree();
        }
        _bodies.Clear();
        _live.Clear();
        _gone.Clear();
        _lain.Clear();
        _breached.Clear();
        _perch.Clear();
        _cleared.Clear();
        System.Array.Clear(_stood);
        System.Array.Clear(_taken);
        _tank = null;
        _tankHull = null;
        _tankDriven = false;
        _sparedByBox.Clear();
        _tankProw = null;
        _tankFace = -1;
        _clock = -1.0f;
        _beam = null;
    }

    /// <summary>Whether piece <paramref name="i"/> is still where it was laid -
    /// frozen, and never let go by a shot or a ram.
    ///
    /// Asked rather than worked out from <see cref="At"/>: a piece knocked out of
    /// a wall may come to rest a hand's width from its seat, so "has it moved" is
    /// a threshold, while "was it let go" is a fact the rig already holds.</summary>
    public bool Held(int i) => i >= 0 && i < _bodies.Count && !_live.Contains(i);

    /// <summary>Where piece <paramref name="i"/> is, back in the cell's own
    /// units. The basis comes straight over: the bodies carry no scale, so
    /// dividing the origin is the whole conversion.</summary>
    public Transform3D At(int i) =>
        new(_bodies[i].Transform.Basis, _bodies[i].Transform.Origin / MetresPerCell);

    /// <summary>Fire. <paramref name="into"/> is the way the round travels, a
    /// unit vector in the prop's own frame, and <paramref name="force"/> is a
    /// multiplier over whichever of the three constants this shot uses.
    ///
    /// <b>A direction, not a bearing, because a heading is the board's word and
    /// this class is not the board.</b> The first version took degrees and made
    /// its own vector out of them - <c>(-sin, 0, cos)</c> - which is the board's
    /// convention turned a quarter turn: <see cref="AtlasSet.GroundDirection"/>
    /// puts heading h at <c>(cos h, -sin h * squash)</c> and
    /// <see cref="Stage3D.World"/> divides that squash straight back out, so the
    /// world direction is <c>(cos h, 0, -sin h)</c> and nothing else. Ninety
    /// degrees out, every one of the six flat-side bearings pointed at a
    /// <i>vertex</i>: the tank drove in over a corner of its own hex and the
    /// rounds arrived across one. Now the caller resolves it, which is where it
    /// is answerable - the caller is the one that has a board.
    ///
    /// <b>A level over the three, never a number of its own.</b> The three are
    /// not comparable - one is a speed in metres a second and two are impulses
    /// in newton seconds - and the spread between them is what makes a ram a
    /// ram; a control that set the value directly would flatten them the moment
    /// it was dragged. The same shape as every other level in this project, and
    /// for the same reason.
    ///
    /// Read here rather than held, so a shot carries the force it was fired
    /// with: turning the dial while the rubble is still moving must not reach
    /// back into the round already in the air.</summary>
    /// <param name="beam">Whether the rig draws its own tracer for this shot.
    /// False for a round that was already drawn on its way in - a gun's shell has
    /// a tracer of its own, and a second line over it is a second answer to which
    /// way the round came. See <see cref="Beam"/>, whose whole claim is that the
    /// line drawn is the line fired.</param>
    /// <param name="from">Where the round started, as a coordinate along
    /// <paramref name="into"/> in the prop's own metres, or negative infinity for
    /// "from outside" - which is what every shot at a wall was until a tank stood
    /// inside one.
    ///
    /// <b>One coordinate rather than a point, because one is all that is
    /// answerable.</b> <see cref="Burst"/> takes the face as the nearest piece
    /// along the shot and <see cref="Pierce"/> runs its channel from behind the
    /// masonry; both read "the round comes from far away up this line", which is
    /// true of every shot fired at a wall from another cell and false of one fired
    /// from the middle of a ring. Measured on that ring: a round aimed at one leaf
    /// blew the leaf <i>behind</i> the tank, because the piece with the smallest
    /// projection is the one at the shooter's back. Across and up are left where
    /// they were - the same restraint <c>Burst</c> already states about its own
    /// face - so this says where the round began and not where on the plate it
    /// struck, which is still the debt named in the bench's notes.</param>
    public void Fire(Strike shot, Vector3 into, float force = 1.0f,
                     bool beam = true, float from = float.NegativeInfinity)
    {
        // Flattened and normalised here rather than trusted: the shot runs along
        // the ground whatever the caller handed over, and a degenerate direction
        // is a round with nowhere to go, not a round straight down.
        into = new Vector3(into.X, 0.0f, into.Z);
        if (_bodies.Count == 0 || into.LengthSquared() < 1e-6f)
            return;
        into = into.Normalized();
        _shot = shot;
        _from = from;
        _force = Mathf.Max(0.0f, force);
        _clock = 0.0f;
        _beam = null;
        float mid = _plan.Top * 0.5f * MetresPerCell;
        float reach = new Vector2(_plan.Size.X, _plan.Size.Z).Length() * MetresPerCell;

        switch (shot)
        {
            case Strike.Ram: Ram(into); break;
            case Strike.He: Burst(into, mid, reach); break;
            case Strike.Ap: Pierce(into, mid, reach); break;
        }
        if (!beam)
            _beam = null;
    }

    /// <summary>Where the round in flight started, along its own direction, in the
    /// prop's metres - see the <c>from</c> parameter of <see cref="Fire"/>.
    /// Negative infinity means "outside", which is every shot but one.</summary>
    private float _from = float.NegativeInfinity;

    /// <summary>A tank drives into it.
    ///
    /// <b>A moving body, not an impulse, and that is what makes it fall four
    /// ways.</b> An impulse along the heading is a wall pushed as one piece, and
    /// a wall pushed one way lands as a line - measured on the Blender build,
    /// 1/26/32 pieces across three strips of the cell. What spreads it is the
    /// courses either side of the breach losing the neighbour they lean on, and
    /// that needs something that keeps coming rather than one kick.
    ///
    /// Drawn as well as felt: a wall falling over with nothing visible touching
    /// it is a bench whose main picture cannot be judged.</summary>
    private void Ram(Vector3 into)
    {
        const float wide = 3.0f, deep = 6.0f;
        float tall = TankTall;
        // <b>It starts on the neighbouring cell and ends standing on this one,
        // and both ends of that are board numbers rather than measurements of
        // the wall.</b> A flat-top cell's neighbour is two apothems away along a
        // flat side, and the shared edge is one apothem in - so a tank that has
        // travelled an apothem has its contact point over this hexagon, which is
        // what the board means by standing on a cell (Main.StandOn puts a parked
        // tank's contact point at the middle of its cell's ground, and the
        // harness's self-test holds it there). Anything further is the bite.
        //
        // <b>Guaranteed, and that is the point of measuring it off the cell.</b>
        // The old travel was "the nearest brick in the lane, plus the bite",
        // which is a distance to the masonry: a low force parked the tank short
        // of the shared edge, so the picture was a tank ramming from the cell
        // next door and never taking this one. Where the bricks happen to be
        // cannot decide whether a tank has taken a hex.
        //
        // The measured-off-the-box lesson stands behind both: the first version
        // projected the plan's half extents onto the shot and called that the
        // wall's thickness, which for a long wall hit at an angle is the
        // distance to a *corner* of the box - at 210 degrees, 2.39m against a
        // wall 1.0m thick, and the tank stopped in mid air having thawed
        // nothing.
        float apothem = Apothem;
        Vector3 nose = -into * (apothem * 2.0f) + Vector3.Up * (tall * 0.5f);
        _tank = new AnimatableBody3D
        {
            SyncToPhysics = true,
            CollisionLayer = Bit,
            CollisionMask = Bit,
            Transform = new Transform3D(
                Basis.LookingAt(into, Vector3.Up), nose),
        };
        _tankHull = new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(wide, tall, deep) },
        };
        _tank.AddChild(_tankHull);
        AddChild(_tank);
        _tankSize = new Vector3(wide, tall, deep);

        // Force moves the bite, not just the speed: told to go slower over the
        // same distance the tank ends in the same place, so the wall does too -
        // measured, 0.35x and 1.0x came back identical to the digit. A harder ram
        // goes in deeper, and how deep it goes is what this shot is about.
        //
        // Force nought is no ram at all, as it is for the other two: the tank
        // never sets off and nothing is let go. That is also the proof that the
        // collapse is the tank's doing rather than the wall's own - see below.
        float speed = RamSpeed * _force;
        // <b>And it stops on the cell, however hard it is pushed.</b> Two
        // apothems is the far edge, so a contact point taken past the middle of
        // this hexagon is a tank standing on the next one - which is the promise
        // above broken from the other end. Above about three the dial stops
        // buying depth and buys speed, and the tank parks dead centre on what it
        // has just knocked down.
        float travel = speed > 0.0f
            ? Mathf.Min(apothem + RamBite * _force, apothem * 2.0f) : 0.0f;
        _tankRun = into * speed;
        _tankLeft = speed > 0.0f ? travel / speed : 0.0f;

        // Only what the tank sweeps through, and only once it gets there.
        //
        // <b>Thawing everything was not a ram at all.</b> A ruined wall let go of
        // all at once does not stand - the Blender build measured that, and it is
        // why these bodies are frozen in the first place - so what followed was
        // the wall falling over by itself while a tank drove past. Proved by
        // taking the tank away: at force 0 it never moves, and the settle came
        // back identical to the digit at 0, 0.35, 1.0 and 2.0. What spreads the
        // rest is the contact hook, the same mechanism the other two shots use.
        //
        // <b>And letting the whole lane go on the frame the button is pressed is
        // the same failure with a longer fuse.</b> The tank now starts a cell
        // away, so it is three quarters of a second on the road; released at
        // nought, the courses in front of it come down before it arrives, and the
        // picture is a wall collapsing at a tank that is still driving. So the
        // thaw follows the tank - <see cref="Reached"/> - which is the same rule
        // stated properly: a brick is let go when the tank gets to it.
        _tankLane = wide * 0.5f + _brick;
        _tankTip = nose + into * (deep * 0.5f);
        _tankInto = into;
        _tankGone = 0.0f;
        // Nothing at all when the tank never sets off: force nought is no ram,
        // and a wall let go of falls down by itself.
        if (speed > 0.0f)
        {
            // Which section the charge is going into, named on the way in -
            // see Meets. From the box middle rather than its tip, because a
            // leaf a hull opens inside is already behind the tip when the run
            // begins - named where it decides which leaf comes down.
            _tankFace = Meets(nose, into, 0.0f).Face;
            Reached(_tankTip, _tankInto, _tankLane, 0.0f, 0.0f, _tankFace);
        }
    }

    /// <summary>How fast a piece leaves when a box going
    /// <paramref name="speed"/> metres a second lets go of it, in metres a
    /// second - <see cref="RamCarry"/> of the box.
    ///
    /// <b>Never backwards.</b> The band is measured along the heading the box
    /// faces now, and a tank in reverse crosses that ground with a negative
    /// advance; handed on unclamped it would fling the masonry towards the
    /// tank. A reversing hull rams nothing, and nought is what says so.
    ///
    /// Named and static because the live solver is the one thing
    /// <c>--selftest</c> cannot run, and this is the half that is
    /// arithmetic.</summary>
    public static float Shunted(float speed) =>
        Mathf.Max(0.0f, speed) * RamCarry;

    /// <summary>Which way a piece leaves when the box lets go of it: down the
    /// heading, splayed across the lane by how far off the axis it stood - see
    /// <see cref="RamSplay"/>. <paramref name="off"/> is its offset from the
    /// axis, <paramref name="lane"/> the half-width of the band.
    ///
    /// <b>The offset is flattened first.</b> The band is a cylinder about the
    /// drive line, so a piece's offset from it carries the height it stands at,
    /// and a top course thrown by its own height is a wall lifted rather than
    /// pushed. What the hull wedges aside, it wedges sideways.
    ///
    /// Not normalised: length is the shove, and a piece at the flank is given
    /// the forward push it would have had plus the sideways one, rather than
    /// having the forward push taken off it to pay for the other.
    ///
    /// Named and static for <see cref="Shunted"/>'s reason.</summary>
    public static Vector3 Wedge(Vector3 into, Vector3 off, float lane) =>
        lane > 0.0f
            ? into + new Vector3(off.X, 0.0f, off.Z) * (RamSplay / lane)
            : into;

    /// <summary>
    /// Let go of every piece the box has swept, in a band along
    /// <paramref name="into"/> reaching from <paramref name="back"/> behind
    /// <paramref name="tip"/> to <paramref name="front"/> ahead of it.
    ///
    /// <b>The nose and its direction are arguments rather than fields, because a
    /// real tank turns on the way in.</b> The rig's own box is fired down one
    /// heading and never leaves it, so it could be asked about the tip it started
    /// from; a tank driving an order steers, and a band measured off where it set
    /// out is a band that stops covering the tank about a third of the way
    /// through a corner.
    ///
    /// <paramref name="front"/> has to be at least a step's worth of travel: a
    /// frozen piece is a static body, which a kinematic one drives straight
    /// through, so a brick still frozen when the nose arrives is a brick the tank
    /// passes through rather than hits. At the top of the dial a step is 0.65m,
    /// which is most of a piece.
    ///
    /// Going back over ground already swept costs nothing - a piece is only ever
    /// let go once (<see cref="Thaw"/> is idempotent through
    /// <see cref="_live"/>), which is what lets the driven nose ask about the
    /// band it is standing in rather than about everywhere it has been.
    /// </summary>
    private void Reached(Vector3 tip, Vector3 into, float lane, float back,
                         float front, int face)
    {
        for (int i = 0; i < _bodies.Count; i++)
        {
            if (_live.Contains(i))
                continue;
            // One section, and no other - see Reaches. The same confinement the
            // blast already has, arriving at the ram: the hull's lane is its own
            // width and cannot widen with a dial, so every piece standing in the
            // band is a piece it drove through - but driving across the mitred
            // corner of the leaf next door is going through a corner and not
            // through the leaf, and a corner let go of is a corner that falls.
            if (!Reaches(face, SideOf(i)))
                continue;
            Vector3 arm = _bodies[i].Transform.Origin - tip;
            float along = arm.Dot(into);
            // Up to where the tank has actually got, and not a brick further -
            // measured on the piece rather than bounded by the worst one, see
            // Face. Taken as a maximum, so a caller that hands in a lead of its
            // own - the rig's own box does - keeps it.
            float edge = front;
            if (_plan is { } near && i < near.Blocks.Count)
                edge = Mathf.Max(front, -Face(_bodies[i].Transform.Basis,
                                              near.Blocks[i].Half * MetresPerCell,
                                              into));
            if (along < -back || along > edge)
                continue;
            if ((arm - into * along).Length() > lane)
                continue;
            Thaw(i);
        }
        // A tank driving through masonry is a breach, and a breach takes the
        // section - see Breaching.
        //
        // <b>The section it drove into, and not the section of every brick it
        // reached.</b> What it lets go of is per piece and stays that way: a
        // hull's lane is the hull's own width and cannot widen with the dial, so
        // a brick it reached is a brick it drove through, and driving across a
        // mitred corner takes brick off two leaves because it went through two.
        // But going through a corner of a leaf is not going through the leaf, and
        // asking per piece could not tell the two apart - measured on the ring,
        // a graze took 8 to 54 pieces of 96 where BreachShare is 12, so a leaf
        // the tank never entered came down on one pass in three. See Rammed,
        // which also has the numbers for why a share cannot separate them.
        //
        // Asked every frame rather than once at the end, so the section still
        // goes on the frame the tally crosses the share - what the per-piece call
        // did. The tally it reads counts the cascade too, but the question is
        // only ever asked here.
        Breach(face, Strike.Ram);
    }

    /// <summary>How many pieces of the wall proper stand in the band from
    /// <paramref name="back"/> behind <paramref name="tip"/> to
    /// <paramref name="front"/> ahead of it - the mirror of
    /// <see cref="Reached"/>, counting instead of letting go.
    ///
    /// Standing pieces of the wall proper: what has been let go of does not
    /// count and neither does the apron - see <see cref="Against"/>.
    /// </summary>
    private int Pressing(Vector3 tip, Vector3 into, float lane, float back,
                         float front)
    {
        int on = 0;
        for (int i = 0; i < _bodies.Count; i++)
        {
            if (_live.Contains(i))
                continue;
            if (_plan is { } plan && i < plan.Blocks.Count
                && (plan.Blocks[i].Chip || plan.Blocks[i].Course < 0))
                continue;
            Vector3 arm = _bodies[i].Transform.Origin - tip;
            float along = arm.Dot(into);
            if (along < -back || along > front)
                continue;
            if ((arm - into * along).Length() > lane)
                continue;
            on++;
        }
        return on;
    }

    /// <summary>How far past a brick the nose has to be before that brick counts
    /// as driven through, as a fraction of a brick's own length.
    ///
    /// <b>Through it, not up to it, and this is the half the band could not say.</b>
    /// The window used to run from the band's far end up to the tip, so a brick
    /// the nose had merely poked into came out - and a tank driving back into the
    /// ring parks with its nose 3.0m out against 2.76m of clear yard, poking a
    /// quarter of a metre into the leaf it stopped at. That is the innermost
    /// course of that leaf, the course everything above it stands on, so the leaf
    /// in front of a tank that had come to rest fell down: measured, an out-and-
    /// back trip let go of 161 pieces where the one leaf it drove through is 89.
    ///
    /// Half a brick rather than a brick, because it is the tip's clearance past
    /// the piece's <i>centre</i>, and half the long side is the worst a piece can
    /// present to a nose whatever way it was laid. It errs frozen, which is the
    /// side to err on: a brick left frozen is one the hull clips, a brick let go
    /// is a wall that fell for free.</summary>
    public const float NoseThrough = 0.5f;

    /// <summary>The near end of the window <see cref="Reached"/> is given,
    /// in metres: <see cref="NoseThrough"/> of a brick <i>behind</i> the tip,
    /// so the number is negative and a piece level with the tip is outside it.
    ///
    /// Named because the sign is the whole of the rule, and a live solver is
    /// the one thing <c>--selftest</c> cannot run.</summary>
    public static float Through(float brick) => -brick * NoseThrough;

    /// <summary>How far an oriented box reaches along <paramref name="axis"/>
    /// from its own centre, in whatever units its <paramref name="half"/>
    /// extents are given in - the support function of a box, and the same three
    /// lines <c>WallKit</c> uses to keep the courses apart.
    ///
    /// <b>The window is tested on a centre, and this is what turns it into a
    /// face.</b> <see cref="NoseThrough"/> is the worst any piece can present
    /// whatever way it was laid, which is what makes it right as a bound and
    /// wrong as a distance: a stretcher lying along the wall presents its depth,
    /// about a third of that, so the nose had swallowed it whole before letting
    /// go. Measured head on, the pieces come out at the tip instead of a brick
    /// behind it.
    ///
    /// <b>It cannot let go of more than the bound did</b>, which is why it is
    /// safe on every board already measured: a piece's own extent is never more
    /// than half the longest brick, so the window only ever ends nearer the tip
    /// than it used to, never further back. The out-and-back case
    /// <see cref="NoseThrough"/> is written against keeps its margin - the far
    /// leaf's innermost course sits 0.09m behind a parked MTP tip against an
    /// extent of about 0.15m.
    ///
    /// Named and static because a live solver is the one thing
    /// <c>--selftest</c> cannot run, and this half of the rule is
    /// arithmetic.</summary>
    public static float Face(in Basis turn, Vector3 half, Vector3 axis) =>
        Mathf.Abs(turn.X.Dot(axis)) * half.X
        + Mathf.Abs(turn.Y.Dot(axis)) * half.Y
        + Mathf.Abs(turn.Z.Dot(axis)) * half.Z;

    /// <summary>How far ahead of the nose masonry counts as being shoved, as a
    /// fraction of a brick's length.
    ///
    /// <b>Two bricks rather than nothing, and it is the leaf's own thickness
    /// that is too short.</b> A leaf is under a metre through, which a hull at
    /// cruise crosses in a sixth of a second - at 420px/s/s that is a tenth off
    /// the speed, a dip nobody sees. The window has to open before the tip
    /// touches the masonry so the ceiling has somewhere to bite, and two bricks
    /// ahead with half a one behind is 1.5m of it. Because the tank slows inside
    /// the band it also stays in it longer than the arithmetic on its entry
    /// speed suggests.
    ///
    /// Two rather than more, because further ahead is braking rather than
    /// shoving: a tank that starts crawling a couple of metres short of a wall
    /// reads as having seen it coming.</summary>
    public const float NoseShove = 2.0f;

    /// <summary>The far end of the window <see cref="Against"/> is counted over,
    /// in metres, with <see cref="Through"/> as its near end.
    ///
    /// <b>The two windows meet and never overlap</b>, and that is the whole of
    /// why both are named: a piece is either driven through or being pressed
    /// against, and one that was both would be resisting a tank that has already
    /// let go of it.</summary>
    public static float Shove(float brick) => brick * NoseShove;

    // --- a tank somebody else drives ----------------------------------------

    /// <summary>Whether a driven box is standing in this rig's world.</summary>
    public bool Mounted => _tankDriven && _tank is not null;

    /// <summary>Stand a kinematic box in this rig's world for a tank somebody
    /// else drives - the ram brought back as a body, for the reason the event
    /// alone could not answer: one impulse at the crossing is a wall that
    /// crumbles while the hull ghosts through the fall, and what made the
    /// bench's own ram (<see cref="Ram"/>) read as a ram was never the release,
    /// it was the box shoving what the release let go.
    ///
    /// <b>The box lives whenever a wall is near, and only on one rig - what
    /// intent bounds is the naming, not the body.</b> It used to live only
    /// while a ram order did, and the seam showed: the order over, the hull
    /// went back to ghosting through its own ram's heap. The old swept box
    /// died not of living always but of living always <i>armed</i> and being
    /// reborn in heaps unexcepted - a pivot swept six leaves, a box reborn in
    /// the last leg's heap measured <c>reach 696</c>; this one is disarmed by
    /// default (<see cref="Disarm"/> - the caller calls it when the ram order
    /// ends) and excepts what it is born inside. <see cref="Channel"/> bounds
    /// its world: the box carries this rig's bit, so every other wall's
    /// masonry and rubble are not merely ignored but invisible to it.
    ///
    /// <b>What it was born inside, it must not throw.</b> A tank standing in
    /// its own rubble and ordered on is exactly the reborn-box case, and the
    /// solver answers overlap only with a separating impulse. Every live piece
    /// overlapping the box at birth - found by arithmetic over the bodies, a
    /// brick's own margin wide, no space query - becomes a collision exception
    /// for the box's whole life. The price is named: the hull does not shove
    /// the rubble it started in, which is the price the rig's own ram already
    /// paid for the same reason.
    ///
    /// <b>Frozen masonry needs no guard at all</b>: the box is kinematic and
    /// standing pieces are static, and the two pass through each other - a
    /// brick is only ever pushed after <see cref="Drive"/> has let it go, two
    /// bricks ahead of the nose, so it is dynamic before the box arrives.
    /// </summary>
    /// <param name="at">The hull's contact point, in the prop's own metres -
    /// the box centre is lifted by half its own height here.</param>
    /// <param name="into">Which way the hull faces, in the prop's frame.</param>
    /// <param name="size">The hull's box, in metres - <c>WallProp.Box</c>.</param>
    /// <param name="bow">How long the glacis runs, nose to deck, in metres -
    /// <c>WallProp.Bow</c>, measured off the tank's own height map. Nought or
    /// absent keeps the declared slope of a third of the hull, which is what
    /// every box was before the map could be asked.</param>
    public void Mount(Vector3 at, Vector3 into, Vector3 size, float bow = 0.0f)
    {
        Dismount();
        into = new Vector3(into.X, 0.0f, into.Z);
        if (_bodies.Count == 0 || into.LengthSquared() < 1e-6f)
            return;
        into = into.Normalized();
        Vector3 centre = at + Vector3.Up * (size.Y * 0.5f);
        _tank = new AnimatableBody3D
        {
            SyncToPhysics = true,
            CollisionLayer = Bit,
            CollisionMask = Bit,
            Transform = new Transform3D(
                Basis.LookingAt(into, Vector3.Up), centre),
        };
        // A prow, not a wall: the front is a wedge - the bottom edge leads at
        // half width, the top is set back by the bow - so a brick met by the
        // advancing face is pushed up-forward and aside along the slope
        // instead of being pinched between a vertical plate and the ground.
        // Measured flat-faced, the hull toppled the wall onto itself and
        // swallowed what fell in; the wedge is what turns "run down" into
        // "ploughed through". The bow is the tank's own where the atlas
        // could measure one, and a declared third of the hull where it could
        // not - capped short of the box's own length either way, because a
        // ramp the length of the hull is not a prow, it is a different shape
        // with no measurement behind it. One convex hull, because two shapes
        // on one body are two chances to pinch in the seam. Forward is -Z
        // under LookingAt; Ram()/Hull() keep reporting the bounding box, so
        // the overlay draws the frame the wedge lives in.
        float hx = size.X * 0.5f, hy = size.Y * 0.5f, hz = size.Z * 0.5f;
        bow = bow > 0.01f ? Mathf.Min(bow, size.Z * 0.7f) : size.Z * 0.35f;
        _tankBow = bow;
        _tankProw = new[]
        {
            new Vector3(-hx, -hy, hz), new Vector3(hx, -hy, hz),
            new Vector3(-hx, hy, hz), new Vector3(hx, hy, hz),
            new Vector3(-hx * 0.5f, -hy, -hz),
            new Vector3(hx * 0.5f, -hy, -hz),
            new Vector3(-hx, hy, -hz + bow),
            new Vector3(hx, hy, -hz + bow),
        };
        _tankHull = new CollisionShape3D
        {
            Shape = new ConvexPolygonShape3D { Points = _tankProw },
        };
        _tank.AddChild(_tankHull);
        _sparedByBox.Clear();
        Basis frame = _tank.Transform.Basis.Inverse();
        foreach (int i in _live)
        {
            if (_bodies[i].Freeze)
                continue;
            Vector3 arm = frame * (_bodies[i].Transform.Origin - centre);
            if (Mathf.Abs(arm.X) < size.X * 0.5f + _brick
                && Mathf.Abs(arm.Y) < size.Y * 0.5f + _brick
                && Mathf.Abs(arm.Z) < size.Z * 0.5f + _brick)
            {
                _tank.AddCollisionExceptionWith(_bodies[i]);
                _sparedByBox.Add(i);
            }
        }
        AddChild(_tank);
        _tankSize = size;
        _tankDriven = true;
        _tankRun = Vector3.Zero;
        _tankLeft = 0.0f;
        _tankGone = 0.0f;
        _tankInto = into;
        _tankLane = size.X * 0.5f + _brick;
        _tankTip = centre + into * (size.Z * 0.5f);
        _tankAt = at;
        _tankFace = -1;
        _swallowed = 0;
    }

    /// <summary>Put the driven box where the tank has got to this frame, and
    /// let go of what its nose has earned.
    ///
    /// <b>The release follows forward progress, never the pose.</b> The frame's
    /// advance is the contact point's displacement along the heading, taken as
    /// a difference rather than a speed handed in - so a pivot (displacement
    /// nought) and a reversing hull (displacement negative) release nothing,
    /// which is the old sweep's first death answered by arithmetic.
    ///
    /// <b>What stands ahead of the tip is bulldozed; what the naming arrived
    /// too late for is shoved the event's own way.</b> The section is named
    /// when the sprite's nose crosses the rim, and a hull leaving its own ring
    /// crosses that plane with the leaf's thickness already behind the box tip
    /// - measured, the naming arrived with the tip 0.11m past the rim and the
    /// nearest centres 0.33m behind it. A piece released inside the box is a
    /// pair the solver can only answer with a separating impulse (measured:
    /// reach 16.4, 30 pieces off the board - the reborn-box explosion in
    /// miniature), and a piece released inside and excepted just stands there
    /// (measured: 66 of 72 stood, a wall driven through and still up). So the
    /// split is by where the piece stands when it is let go: at or behind the
    /// tip - excepted from the box and handed <see cref="Wedge"/> of
    /// <see cref="Shunted"/>, exactly what the event gave every piece; ahead
    /// of the tip - let go clean and left to the advancing body, which is the
    /// whole reason the box exists.
    ///
    /// <b>Nothing is released before a crossing has named the section</b> -
    /// <see cref="Rammed"/> latches the face while the box is mounted and
    /// releases nothing itself; until then this call is only a pose. The clock
    /// starts on the first piece actually let go, the event's own rule.</summary>
    /// <param name="speed">How fast the hull is going, metres a second - what
    /// the swallowed pieces leave at, the event's own parameter.</param>
    /// <param name="gate">The point on the rim the order is still to cross,
    /// in the prop's own metres - null forbids self-naming. Handed over as a
    /// point rather than a distance because the distance was first computed
    /// in flat screen coordinates, where the isometric squash halves every
    /// vertical length: a vertical ram's grant came out half its true value,
    /// self-naming arrived late, and the leaf fell flat off the swallowed
    /// branch while a diagonal ram ploughed correctly. A point crosses the
    /// board-prop seam through the same conversion as everything else, and
    /// the scalar is taken here, in true metres.
    ///
    /// <b>The caller's knowledge of the order, handed over as a place.</b>
    /// The crossing event names off the sprite nose's cell, which flips a
    /// step late: leaving a ring, the leaf's inner face stands 0.4m before
    /// the rim, so by the time the cell flipped the box had already passed
    /// through the frozen leaf and the whole of it went down the swallowed
    /// branch - a wall that crumples vertically instead of being pushed, on
    /// the commonest gesture the bench has. Naming by tip proximity fixes
    /// that and, unguarded, re-breaks what the crossing rule protects: a hull
    /// braking to park in its ring stops with its nose inside the far leaf's
    /// clearance (2.76m against a 3.18m corner), and proximity alone would
    /// release the lane's worth of it - past BreachShare, the whole section,
    /// the felled-from-across-the-cell bug over again. The grant is the
    /// resolution: the bench measures how far ahead the order still has a
    /// rim of this wall to cross, and masonry beyond that is a wall the
    /// order never crosses - parked short of it, the gate falls behind the
    /// tip and this call is a pose again.</param>
    public void Drive(Vector3 at, Vector3 into, float speed,
                      Vector3? gate = null)
    {
        if (!_tankDriven || _tank is null)
            return;
        into = new Vector3(into.X, 0.0f, into.Z);
        into = into.LengthSquared() < 1e-6f ? _tankInto : into.Normalized();
        Vector3 centre = at + Vector3.Up * (_tankSize.Y * 0.5f);
        _tank.Transform = new Transform3D(
            Basis.LookingAt(into, Vector3.Up), centre);
        float ahead = (at - _tankAt).Dot(into);
        _tankAt = at;
        _tankInto = into;
        _tankTip = centre + into * (_tankSize.Z * 0.5f);
        Shepherd(centre, speed);
        if (ahead <= 0.0f)
            return;
        // Released just in time, not two bricks early. NoseShove's lead was
        // sized for the rig's own box at 3.9 m/s; at driving speeds it bought
        // the wall half a second to topple under gravity before the face
        // arrived, so the hull met a falling curtain instead of standing
        // masonry - "the tank knocks the wall over and drives through it, it
        // never pushes it". Half a brick plus the frame's own advance is
        // still a couple of frames of dynamics before contact, and the face
        // now meets the wall standing - which is what pushing is.
        float front = ahead * 1.5f + _brick * 0.5f;
        // Name for ourselves what stands at the face and short of the gate -
        // see the gate parameter for why both bounds are load-bearing. Half a
        // brick of slack past the gate's plane, for the leaf standing just
        // inside its own rim.
        if (_tankFace < 0)
        {
            if (gate is not { } rim)
                return;
            float grant = (new Vector3(rim.X, 0.0f, rim.Z) - _tankTip)
                          .Dot(into) + _brick * 0.5f;
            (float met, int face) = Meets(
                _tankTip - into * (2.0f * _brick), into, 0.0f);
            float d = met - 2.0f * _brick;
            if (face < 0 || d > front || d > grant)
                return;
            _tankFace = face;
        }
        int had = _live.Count;
        for (int i = 0; i < _bodies.Count; i++)
        {
            if (_live.Contains(i) || !Reaches(_tankFace, SideOf(i)))
                continue;
            Vector3 arm = _bodies[i].Transform.Origin - _tankTip;
            float along = arm.Dot(into);
            if (along < -2.0f * _brick || along > front)
                continue;
            Vector3 off = arm - into * along;
            if (off.Length() > _tankLane)
                continue;
            Thaw(i);
            if (along > 0.0f)
                continue;
            _tank.AddCollisionExceptionWith(_bodies[i]);
            _sparedByBox.Add(i);
            if (Shunts && speed > 0.0f)
                _bodies[i].ApplyImpulse(
                    Wedge(into, off, _tankLane)
                    * (Shunted(speed) * _bodies[i].Mass));
        }
        Breach(_tankFace, Strike.Ram);
        if (_live.Count > had && _clock < 0.0f)
        {
            _shot = Strike.Ram;
            _force = 1.0f;
            _clock = 0.0f;
            _beam = null;
        }
    }

    /// <summary>The driven box's per-frame care of what is already loose
    /// around it - run on every pose, named or not, advancing or pivoting,
    /// because a faceless box wading its own heap is still a kinematic body
    /// in a pile (measured unshepherded: reach 18, 7 off the board, on a
    /// return over the rim its own ram had emptied).
    ///
    /// <b>The belt over the braces</b>: a piece buried inside the wedge would
    /// be the solver-overlap pair again, so it is excepted the moment that
    /// happens. Inside the WEDGE, not the bounding box - the air over the
    /// slope is exactly where a ploughed brick is supposed to be, and a
    /// bounds test swallowed 58 pieces of one exit ram, most of them
    /// section-collapse masonry standing beside the hull. A quarter brick of
    /// burial past the slope, so a piece being honestly ridden up the prow
    /// keeps being pushed.
    ///
    /// <b>And a muzzle brake on the solver's answer</b>: a piece pinched
    /// between the advancing wedge and something that will not move is
    /// separated with whatever impulse it takes, which reads as a brick
    /// teleporting. Anything near the box is capped at twice the hull's
    /// speed (floored at 8 m/s so a crawling hull still lets bricks tumble):
    /// the honest push is at most the hull's speed and is untouched, only
    /// the shout is muted.</summary>
    private void Shepherd(Vector3 centre, float speed)
    {
        if (_tank is null)
            return;
        Basis hold = _tank.Transform.Basis.Inverse();
        float bx = _tankSize.X * 0.5f, by = _tankSize.Y * 0.5f,
              bz = _tankSize.Z * 0.5f;
        float cap = Mathf.Max(speed * 2.0f, 8.0f);
        foreach (int i in _live)
        {
            if (_bodies[i].Freeze)
                continue;
            Vector3 arm = hold * (_bodies[i].Transform.Origin - centre);
            if (Mathf.Abs(arm.X) >= bx + _brick
                || Mathf.Abs(arm.Y) >= by + _brick
                || Mathf.Abs(arm.Z) >= bz + _brick)
                continue;
            Vector3 rush = _bodies[i].LinearVelocity;
            float pace = rush.Length();
            if (pace > cap)
                _bodies[i].LinearVelocity = rush * (cap / pace);
            if (_sparedByBox.Contains(i))
                continue;
            float prow = -bz + Mathf.Clamp(
                (arm.Y + by) * (_tankBow / _tankSize.Y), 0.0f, _tankBow);
            if (Mathf.Abs(arm.X) < bx && Mathf.Abs(arm.Y) < by && arm.Z < bz
                && arm.Z - prow > _brick * 0.25f)
            {
                _tank.AddCollisionExceptionWith(_bodies[i]);
                _sparedByBox.Add(i);
                _swallowed++;
            }
        }
    }

    /// <summary>Forget the section the box was ploughing, keeping the box.
    ///
    /// The box outliving the order is what made this necessary: while it was
    /// mounted per-ram, <see cref="Dismount"/> cleared the face with the body,
    /// but a box that stays with the hull carries the last ram's naming into
    /// the next plain drive - and <see cref="Drive"/> releases and breaches
    /// for as long as a face is named, so an armed box on an ordinary order
    /// would finish the wall the ram only entered. The caller says the intent
    /// ended; the body stays and keeps shoving what is already loose, which
    /// is the whole point of it staying.</summary>
    public void Disarm() => _tankFace = -1;

    /// <summary>Take the driven box out of the world - the order ended, was
    /// cancelled, or moved on to another wall. The heap it was holding lies
    /// down at once, which is the parked-box failure answered by absence.
    /// Leaves the rig's own scripted ram alone: that box is not driven and not
    /// this method's to free.</summary>
    public void Dismount()
    {
        if (!_tankDriven)
            return;
        if (_swallowed > 0)
            GD.Print($"wall rig: the ram box swallowed {_swallowed} piece(s) "
                     + "mid-push - see WallRig._swallowed");
        _tankDriven = false;
        _tankFace = -1;
        _sparedByBox.Clear();
        _tankProw = null;
        _tank?.QueueFree();
        _tank = null;
        _tankHull = null;
    }

    /// <summary>A hull crossed the wall's plane: the driven ram, arrived as an
    /// event.
    ///
    /// <b>An event and not a body, and that is the whole revision.</b> The
    /// driven ram used to be a kinematic box swept continuously along the
    /// tank's path, and nearly everything about it existed to tame the sweep's
    /// degenerate cases - a pivot is not a ram, a hull spawned inside masonry,
    /// a poke against a leaf against a drive through it, a box reborn inside
    /// the last ram's heap. A shot already arrives as one call
    /// (<c>TankTick.Landed</c> into <see cref="Fire"/>) and that path has never
    /// needed taming, so the ram arrives the same way: the board says a hull's
    /// front face crossed a wall-bearing rim, and the rig answers once.
    ///
    /// <b>What it does is what the band did, said once:</b> every standing
    /// piece of the section the nose met, within the hull's own lane, is let
    /// go and handed the hull's momentum - <see cref="Shunted"/> of the speed
    /// it crossed at, parted by <see cref="Wedge"/> - and <see cref="Breach"/>
    /// takes the section. The quantum of a driven ram is the section, which it
    /// already was through <see cref="BreachShare"/>; what is gone is the
    /// per-frame release, and with it the pivot, the spawn and the rebirth
    /// cases whole.
    ///
    /// <b>The section is asked of the bricks, never mapped from the
    /// heading:</b> <c>WallKit.Fan</c> decides which leaf a side is, and
    /// putting Fan and the bricks against each other is the tour's whole
    /// subject. See <see cref="Meets"/>.
    ///
    /// <b>No body, so nothing to pinch.</b> A kinematic box overlapping a
    /// dynamic brick is a pair the solver can only answer with a separating
    /// impulse - measured at <c>reach 696</c> cells when a box reborn for a
    /// second leg stood in the first leg's heap - and a box that does not
    /// exist overlaps nothing. What is given up is named: the hull does not
    /// shove old rubble along (under <see cref="Shunts"/> it already did not),
    /// and nothing holds a heap off the machine (once the order ended, nothing
    /// did).</summary>
    /// <param name="at">The hull's centre at the crossing, in the prop's own
    /// metres - the contact point lifted by half the box, so the lane is
    /// centred on the courses.</param>
    /// <param name="into">Which way it crossed, in the prop's frame.</param>
    /// <param name="speed">How fast it was going, metres a second - what the
    /// pieces leave at. See <see cref="RamCarry"/>.</param>
    /// <param name="size">The hull's box, in metres - <c>WallProp.Box</c>, so
    /// a heavy rams a wider lane than a light.</param>
    /// <returns>The section the nose met, or -1 for a crossing with no
    /// standing masonry at the plane it crossed - which is not a strike and
    /// starts no clock. At the plane, not merely ahead: masonry counts within
    /// two bricks of the nose and no further.</returns>
    public int Rammed(Vector3 at, Vector3 into, float speed, Vector3 size)
    {
        into = new Vector3(into.X, 0.0f, into.Z);
        if (_bodies.Count == 0 || into.LengthSquared() < 1e-6f)
            return -1;
        into = into.Normalized();
        // The nose, because the event is the front face crossing the plane.
        Vector3 nose = at + into * (size.Z * 0.5f);
        // Named from two bricks behind the nose: a hull leaving its own ring
        // crosses the plane with the leaf's thickness already behind the tip,
        // while one arriving from outside has it all ahead.
        //
        // And no further than two bricks ahead of it, because the event is
        // the nose crossing THIS plane. Meets scans as far as it can see, and
        // rubble is not masonry to it - so a hull driving back in over the
        // rim its own ram had emptied was answered with the opposite leaf,
        // two apothems out (7.0m against the half-brick a leaf stands from
        // the rim it is laid on), and Rammed felled it from across the cell.
        // Masonry the hull has not reached is a later crossing's business, at
        // that leaf's own rim - and a hull that parks short of it never rams
        // it at all, which is the promise in the line above. Two bricks
        // mirrors the reach behind, and NoseShove's reason fits it unchanged:
        // further ahead is driving towards, not driving through.
        (float front, int face) = Meets(nose - into * (2.0f * _brick), into, 0.0f);
        // With a driven box mounted, the crossing only names: the release is
        // the box's, piece by piece as the nose earns them (see Drive), and
        // the shove is the body's own push rather than an impulse - which is
        // the whole difference between a wall that crumbles at a tank and a
        // wall that is bulldozed by one. Breach still asks, so a section the
        // cascade already bled past its share falls on the crossing, the
        // event's own rule.
        //
        // And when the plane it crossed holds only rubble, the answer is the
        // face the box already carries: the box names at its own tip and can
        // beat the cell-quantized crossing by most of a leaf, so by the time
        // the nose's cell flipped the masonry it broke was rubble Meets
        // rightly skips - measured on the tour, four diagonal ring legs came
        // back "entered nothing" over a leaf the box had just ploughed, and
        // the sections fell as spill. A crossing with no face anywhere - box
        // included - stays a drive.
        if (_tankDriven && _tank is not null)
        {
            if (face >= 0 && front <= 4.0f * _brick)
                _tankFace = face;
            if (_tankFace < 0)
                return -1;
            Breach(_tankFace, Strike.Ram);
            return _tankFace;
        }
        if (face < 0 || front > 4.0f * _brick)
            return -1;
        float lane = size.X * 0.5f + _brick;
        int had = _live.Count;
        for (int i = 0; i < _bodies.Count; i++)
        {
            if (_live.Contains(i))
                continue;
            // One section, and no other - see Reaches: the same confinement
            // the band had, and the rubble at the hull's feet comes with it.
            if (!Reaches(face, SideOf(i)))
                continue;
            Vector3 arm = _bodies[i].Transform.Origin - nose;
            float along = arm.Dot(into);
            // The leaf's own thickness behind the nose and the drive ahead of
            // it: a hull parks no further than the far rim, and the section's
            // masonry all stands within a leaf of its plane.
            if (along < -2.0f * _brick || along > Apothem * 2.0f)
                continue;
            if ((arm - into * along).Length() > lane)
                continue;
            Thaw(i);
            // Straight down the heading and at the centre of mass - the AP
            // round's answer for the AP round's reason: a cone was built for
            // that one and measured worse. --no-shunt keeps the ram that let
            // go and pushed nothing, which is what the driven ram was before
            // it handed its momentum over.
            if (Shunts)
                _bodies[i].ApplyImpulse(
                    Wedge(into, arm - into * along, lane)
                    * (Shunted(speed) * _bodies[i].Mass));
        }
        // A hull through masonry is a breach, and a breach takes the section.
        Breach(face, Strike.Ram);
        // Struck when a brick was actually let go, and never before: a
        // crossing over a rim the last ram already emptied is a drive, not a
        // strike.
        if (_live.Count > had && _clock < 0.0f)
        {
            _shot = Strike.Ram;
            _force = 1.0f;
            _clock = 0.0f;
            _beam = null;
        }
        return face;
    }

    /// <summary>How many standing pieces a hull at <paramref name="at"/> is
    /// pressing against - the count under <c>TankTick.Shoving</c>'s speed cap,
    /// asked of the bricks.
    ///
    /// The same window the driven band used - <see cref="Through"/> of a brick
    /// behind the nose to <see cref="Shove"/> ahead of it, the hull's own lane
    /// wide - so the going costs what it cost. And it ends by itself: a wall
    /// resists until it is broken, <see cref="Rammed"/> breaks it, and the
    /// count drains as the section goes.</summary>
    public int Against(Vector3 at, Vector3 into, Vector3 size)
    {
        into = new Vector3(into.X, 0.0f, into.Z);
        if (_bodies.Count == 0 || into.LengthSquared() < 1e-6f)
            return 0;
        into = into.Normalized();
        Vector3 tip = at + into * (size.Z * 0.5f);
        return Pressing(tip, into, size.X * 0.5f + _brick,
                        -Through(_brick), Shove(_brick));
    }

    /// <summary>How far off the plate a shell goes off, as a fraction of
    /// <see cref="HeReach"/>.
    ///
    /// <b>Measured off the frontmost piece, never off the plan's box, and this
    /// is the ram's lesson arriving a second time.</b> The burst used to sit at
    /// <c>reach * 0.22</c> along the shot, where <c>reach</c> is the diagonal of
    /// the bounding box - a distance to nothing in particular. Measured, that
    /// put the nearest piece anywhere between <b>0.20m and 1.60m</b> away
    /// depending only on which of the six sides was fired from: the masonry's
    /// centroid sits 0.41m off the box centre along z (the box carries the
    /// bricks lying in front of the wall), and end on it is worse still, because
    /// there the diagonal is mostly the wall's <i>length</i> and the burst lands
    /// inside the masonry.
    ///
    /// The collapse tracked it exactly - 24 pieces of 58 moved from 270 against
    /// 46 from 90 at the same force - so a shot that read as "HE does nothing
    /// from this side" was a burst hanging almost a metre and a half off a wall
    /// whose falloff is one over distance squared. Half a reach off the
    /// frontmost centre is about 0.4m clear of the plate, which is a shell going
    /// off on contact rather than a bomb in the air.</summary>
    public const float HeStandoff = 0.5f;

    /// <summary>What a strike coming from <paramref name="from"/> along
    /// <paramref name="into"/> meets: how far ahead the frontmost standing centre
    /// is, and which section it belongs to. <c>(MaxValue, -1)</c> when there is
    /// nothing in front of it at all.
    ///
    /// <b>The face the strike meets is the section it brings down</b>, and two
    /// measurements of the same face are two answers to one question - which is
    /// why this is one function with two callers rather than a scan inside each
    /// of them. See <see cref="Breach"/>.
    ///
    /// Never nearer than <paramref name="back"/>: a ring is masonry on both sides
    /// of the gun, so the smallest projection of all is the leaf at the shooter's
    /// back. Measured on it - a round aimed at one leaf took the opposite one
    /// out, which reads as the wall answering the wrong side rather than as the
    /// strike being placed from infinity.
    ///
    /// <b>Rubble is not masonry</b>, <c>Clearance</c>'s own sentence arriving
    /// where it decides something: the apron lies on the ground inside the cell,
    /// so a round fired from the middle of a ring has a fallen brick at its feet -
    /// measured, the nearest piece ahead was 0.13m away and the charge went off
    /// beside the tank.
    ///
    /// <b>And within a brick of the line</b>, because the face is what the strike
    /// would actually meet. Measured on the ring: a leaf running alongside the
    /// shot has pieces at every projection, so the smallest of them was a brick
    /// 0.13m along - beside the tank rather than in front of it - and the charge
    /// went off there. A plate met head on is unmoved by this: its near face spans
    /// the line, so the nearest piece on the line is the nearest piece.</summary>
    private (float Front, int Face) Meets(Vector3 from, Vector3 into, float back)
    {
        float front = float.MaxValue;
        int face = -1;
        for (int i = 0; i < _bodies.Count; i++)
        {
            if (_plan is { } plan && i < plan.Blocks.Count
                && (plan.Blocks[i].Chip || plan.Blocks[i].Course < 0))
                continue;
            // And masonry that has become rubble is rubble too, which is the
            // same sentence one step on: a leaf the tank drove through leaves
            // brick lying in front of it, and a fallen brick names a section the
            // strike is not going into. Measured on the ring - a third leg named
            // a leaf it never entered in one run of three, which then came down
            // whole because the tally from two earlier passes was already over
            // the share. Only ever adds to what is skipped, so a strike on a wall
            // nobody has touched is unmoved.
            if (_live.Contains(i))
                continue;
            Vector3 arm = _bodies[i].Transform.Origin - from;
            float d = arm.Dot(into);
            if (d < back)
                continue;
            if ((arm - into * d).Length() > _brick)
                continue;
            if (d >= front)
                continue;
            front = d;
            face = SideOf(i);
        }
        return (front, face);
    }

    /// <summary>HE against the face: everything within reach of the burst,
    /// thrown away from it and on through.
    ///
    /// <b>Away from the shooter is what "inward" means on a wall.</b> The blend
    /// is the point - pure radial is a sphere of debris that goes backwards as
    /// much as forwards, pure heading is a piston that moves the wall in one
    /// slab. Falls off as one over distance squared so the breach has an edge
    /// instead of the whole wall lifting.</summary>
    private void Burst(Vector3 into, float mid, float reach)
    {
        // The face, taken off the pieces: the frontmost centre along the shot,
        // then a standoff back towards the shooter. Only the along-shot
        // coordinate is measured - across and up stay where they were, so this
        // is still the middle of the plate and not a corner of it. See Meets for
        // the three rules the scan follows and what each of them cost.
        (float front, int face) = Meets(Vector3.Zero, into, _from);
        // Nothing ahead of it: a round fired out of a gap in the masonry. It goes
        // off where it was fired, which touches nothing - the honest answer.
        if (front == float.MaxValue)
            front = _from;
        Vector3 at = into * (front - HeReach * _brick * HeStandoff)
                     + Vector3.Up * mid;
        _beam = (-into * reach + Vector3.Up * mid, at);
        // A bigger charge reaches further, by the cube root of itself.
        //
        // <b>The only one of the three whose size grows with the dial, and the
        // reason is that only this one has a radius.</b> A tank's lane is the
        // width of the tank and an AP round is a line; neither widens because
        // the slider was dragged. A burst does, and Hopkinson says by the cube
        // root of the charge - which is what force is here, since it multiplies
        // the impulse and impulse goes with charge.
        //
        // Left out, the dial did almost nothing: measured, force 3 against force
        // 1 moved 12 pieces against 10 from 30 degrees, 11 against 9 from 210,
        // and 5 against 5 from 330. Everything past a fixed 2.4m stayed frozen,
        // so a heavier shell only threw the same few pieces harder - and pieces
        // thrown away from the wall touch nothing on the way out, so the cascade
        // had nothing to spread through either.
        float grow = Mathf.Pow(Mathf.Max(_force, 0.0f), 1.0f / 3.0f);
        float blast = Mathf.Max(HeReach * _brick * grow, 0.0001f);
        for (int i = 0; i < _bodies.Count; i++)
        {
            // One section, and no other - see Reaches.
            if (!Reaches(face, SideOf(i)))
                continue;
            Vector3 arm = _bodies[i].Transform.Origin - at;
            float d = arm.Length();
            // Thawed wider than it is pushed, and that gap is the shot. Given an
            // impulse but no room, the near pieces only shove against frozen
            // masonry: measured, 17 pieces moved and the worst went 13cm, which
            // is a wall being leaned on rather than breached.
            if (d > blast * 2.0f)
                continue;
            Thaw(i);
            if (d > blast * 1.5f)
                continue;
            Vector3 dir = (arm.Normalized() * 0.55f + into * 0.45f).Normalized();
            float fall = 1.0f / (1.0f + (d / blast) * (d / blast));
            _bodies[i].ApplyImpulse(
                dir * (HeSpeed * _force * fall * _bodies[i].Mass));
        }
        // The face, and only the face. Asked after the loop rather than inside
        // it so the tally is complete: a section is breached on what the whole
        // burst took from it, not on what the first piece of it took.
        Breach(face, Strike.He);
    }

    /// <summary>AP straight through: a line, and only what is standing on it.
    ///
    /// <b>Nothing else is touched, and that is the whole shot.</b> What the rest
    /// of the wall does about the hole is the solver's business - a course that
    /// still has both neighbours stays up, one that does not comes down on its
    /// own. That distinction is the reason this could never have been a
    /// distribution.</summary>
    private void Pierce(Vector3 into, float mid, float reach)
    {
        // <b>Aimed at the wall, not at the middle of the cell.</b> The prop is
        // a stepped ruin standing off centre, so a line through the cell's middle
        // meets its low end: measured, the round took the top two courses there
        // and the picture was a bite out of the skyline rather than a hole
        // through masonry. A round is aimed where the wall is, and where it is is
        // where it is tallest - that is the one column with courses left above
        // the hole.
        Vector3 side = into.Cross(Vector3.Up).Normalized();
        float lane = 0.0f, crest = 0.0f;
        foreach (RigidBody3D b in _bodies)
            // Ahead of where the round started, for Burst's reason: masonry at
            // the shooter's back is not what the round is aimed at, and a ring is
            // masonry on both sides of the gun.
            if (b.Transform.Origin.Dot(into) >= _from
                && b.Transform.Origin.Y > crest)
            {
                crest = b.Transform.Origin.Y;
                lane = b.Transform.Origin.Dot(side);
            }
        // Gun height - see ApAim - and dropped far enough under that crest to
        // leave a course over the hole, so a wall too low for a gun is hit near
        // its top rather than over it.
        float lintel = ApBore * _brick + _course;
        mid = crest > lintel ? Mathf.Min(ApAim, crest - lintel) : ApAim;
        Vector3 from = -into * reach + side * lane + Vector3.Up * mid;
        // Brought forward to where the round started, when that is known: the
        // channel is everything ahead of its own beginning, and begun from
        // infinity it drills the masonry behind the gun as well as the masonry in
        // front of it. Burst's lesson in the other half of the same shot.
        if (!float.IsNegativeInfinity(_from))
            from += into * (_from - from.Dot(into));
        _beam = (from, into * reach + Vector3.Up * mid);
        // Everything standing in the bore, both leaves, and nothing behind the
        // wall - which is nothing, because the line runs out of masonry and the
        // rest of the board is not on it. No ray cast at all any more: a ray
        // answers "what is the first thing on this line", and the question here
        // is "what is within half a metre of it", which the pieces answer
        // themselves. It also kept the old failure available - a ray that missed
        // between two courses fired a round that did nothing.
        for (int i = 0; i < _bodies.Count; i++)
        {
            Vector3 arm = _bodies[i].Transform.Origin - from;
            float along = arm.Dot(into);
            if (along < 0.0f)
                continue;
            Vector3 across = arm - into * along;
            float off = across.Length();
            float span = Reach(i, across / Mathf.Max(off, 1e-4f));
            // <b>What the channel clips, not what it contains.</b> Asking whether
            // a piece's *centre* is in the bore leaves standing every piece the
            // round went through the edge of - and one of those is the header,
            // which by its whole purpose lies across both leaves. Measured: the
            // pieces on the line went and a frozen header stayed behind them, so
            // the tunnel had a brick in it and the wall read solid. The box's own
            // reach along the offset is the honest test, and it is exact for a
            // box: the sum of its half extents projected on that direction.
            float bore = ApBore * _brick;
            if (off > bore + span * ApBite)
                continue;
            // Only what the round takes, and nothing that touches it - see
            // Thaw: the frozen ring round the gap is what keeps it a hole.
            Thaw(i, spreads: false);
            // Punched in the middle, dropped at the rim. One over one plus the
            // square, the burst's falloff over its own width, for the burst's
            // reason: an edge that is a step is a patch of pieces all leaving at
            // once, which reads as a small charge rather than as a hole.
            float t = off / Mathf.Max(bore + span * ApBite, 1e-4f);
            float fall = 1.0f - ApSpall * t * t;
            // <b>Straight down the shot, and the cone was built and measured
            // worse.</b> Real spall goes out in a cone, and leaning the push away
            // from the axis drives every piece into the frozen side of its own
            // hole: measured, the furthest went 1.35m against 4.88m straight. A
            // hole is only as wide as the pieces that left it.
            _bodies[i].ApplyImpulse(
                into * (ApSpeed * _force * fall * _bodies[i].Mass));
        }
    }

    /// <summary>How far below the ground a piece has to get before it counts as
    /// gone, in metres. One cell radius: the board is seven hexagons and nothing
    /// catches what leaves them, so a piece over the edge falls for ever - 260m
    /// and still going, measured, which keeps the run awake and puts a number in
    /// the drift report that is about the void rather than about the wall.
    /// </summary>
    public const float Lost = -MetresPerCell;


    /// <summary>Let a piece go.
    ///
    /// <b>The contact hook is what makes a hole spread on its own.</b> An AP
    /// round is told to move two pieces and nothing else; what happens to the
    /// courses over the hole is the solver's business, and it can only be its
    /// business if the pieces those two brush on the way out come loose too. So
    /// a thawed piece reports its contacts and thaws whatever it hits. Frozen
    /// bodies report nothing, which is exactly right - a wall nobody has touched
    /// costs no contacts at all.
    ///
    /// An impulse on a frozen body is silently nothing, and that failure looks
    /// precisely like an impulse that was too small.</summary>
    /// <summary>How far a piece reaches from its own centre in the direction
    /// <paramref name="n"/> - the support radius of its box, which is the sum of
    /// its half extents projected onto that direction. Exact for a box, and the
    /// only thing needed to ask whether the round's channel clips it.</summary>
    private float Reach(int i, Vector3 n)
    {
        Vector3 half = _plan.Blocks[i].Half * MetresPerCell;
        Basis m = _bodies[i].Transform.Basis;
        return Mathf.Abs(m.Column0.Dot(n)) * half.X
               + Mathf.Abs(m.Column1.Dot(n)) * half.Y
               + Mathf.Abs(m.Column2.Dot(n)) * half.Z;
    }

    /// <summary>Let piece <paramref name="i"/> go.
    ///
    /// <paramref name="spreads"/> is whether what it touches goes with it, and
    /// the one shot that says no is AP. <b>A hole is what is left when the wall
    /// does not notice.</b> The cascade is how a breach widens - the courses
    /// either side of it lose what they leaned on - and a round through the
    /// middle is the opposite claim: it takes the pieces on its line and leaves
    /// the rest standing round the gap. Let it spread and the pieces above sag
    /// into the void on the frame it opens, which closes it: measured, the hole
    /// read as a dent because the wall settled into it.</summary>
    private void Thaw(int i, bool spreads = true)
    {
        if (!_live.Add(i))
            return;
        int side = SideOf(i);
        if (side >= 0 && side < _taken.Length)
            _taken[side]++;
        RigidBody3D b = _bodies[i];
        b.Freeze = false;
        b.Sleeping = false;
        b.ContinuousCd = true;
        b.ContactMonitor = true;
        b.MaxContactsReported = 6;
        if (!spreads)
            return;
        // What stood on this piece now stands on one support fewer, and the
        // question of whether it stands on any at all waits for Undermined.
        // Behind the spreads gate with the cascade, and for the cascade's
        // reason read backwards: an AP hole is a hole because the wall does
        // not notice, so what spans it keeps spanning it.
        Perch(i);
        b.BodyEntered += hit =>
        {
            if (b.LinearVelocity.LengthSquared() < ThawSpeed * ThawSpeed)
                return;
            for (int k = 0; k < _bodies.Count; k++)
                if (ReferenceEquals(_bodies[k], hit))
                {
                    // Its own section, and no other - see Reaches, which is the
                    // same predicate the band and the blast field are cut by.
                    // The cascade is how a breach widens: the courses either
                    // side of the hole lose what they leaned on, and that is an
                    // argument about one leaf. Two leaves meet at a mitre and
                    // nowhere else, so a brick falling out of one knocking the
                    // corner off the next is the breach crossing a boundary the
                    // wall does not have.
                    if (Reaches(side, SideOf(k)))
                        Thaw(k);
                    return;
                }
        };
    }

    private void ThawAll()
    {
        for (int i = 0; i < _bodies.Count; i++)
            Thaw(i);
    }

    /// <summary>Whether piece <paramref name="i"/> bears piece
    /// <paramref name="k"/>: stands under it within a bed joint of height and
    /// overlaps it in plan by more than a corner kiss. Asked of frozen pieces,
    /// which are exactly where the plan laid them, so the tolerances are the
    /// layout's own - a third of a course of slack for the size jitter, and
    /// two centimetres of required overlap so that neighbours in one course,
    /// which touch end to end, never read as each other's footing.</summary>
    private bool Bears(int i, int k)
    {
        Vector3 under = _bodies[i].Transform.Origin;
        Vector3 over = _bodies[k].Transform.Origin;
        float bed = over.Y - Reach(k, Vector3.Up)
                    - (under.Y + Reach(i, Vector3.Up));
        if (Mathf.Abs(bed) > _course * 0.35f)
            return false;
        var flat = new Vector3(over.X - under.X, 0.0f, over.Z - under.Z);
        float gap = flat.Length();
        if (gap < 1e-4f)
            return true;
        Vector3 n = flat / gap;
        return gap < Reach(i, n) + Reach(k, n) - 0.02f;
    }

    /// <summary>Whether frozen piece <paramref name="j"/> still has anything
    /// under it: the floor, or another frozen piece bearing it. Rubble and
    /// chips can bear - the test is geometry, not rank - but in practice the
    /// apron lies a course below any masonry bed and never answers.</summary>
    private bool Footed(int j)
    {
        if (_plan.Blocks[j].Course <= 0)
            return true;
        // Near the floor is footed however the courses were counted: a ruin's
        // column can start above course nought.
        if (_bodies[j].Transform.Origin.Y - Reach(j, Vector3.Up)
            < _course * 0.5f)
            return true;
        for (int i = 0; i < _bodies.Count; i++)
            if (i != j && !_live.Contains(i) && Bears(i, j))
                return true;
        return false;
    }

    /// <summary>Queue for <see cref="Undermined"/> every frozen piece of
    /// masonry that was standing on piece <paramref name="i"/>.</summary>
    private void Perch(int i)
    {
        if (_plan is not { } plan)
            return;
        for (int k = 0; k < _bodies.Count; k++)
            if (k != i && SideOf(k) >= 0 && !plan.Blocks[k].Chip
                && !_live.Contains(k) && !_perch.Contains(k) && Bears(i, k))
                _perch.Add(k);
    }

    /// <summary>Let go what has been left standing on air.
    ///
    /// <b>A frozen piece is held by the flag, not by anything under it</b>, and
    /// every field that could knock it down is confined to its own section by
    /// <see cref="Reaches"/> - so nothing anywhere could release a frozen brick
    /// whose whole footing fell with somebody else's section. This is the rule
    /// that says masonry stands on masonry, said where it can act.
    ///
    /// <b>Measured, it almost never fires, and that is the bond doing its
    /// work</b>: rammed on every bearing and burst at force 3, every frozen
    /// piece the collapses queued here (84-130 a strike) still overlapped a
    /// standing piece of its own leaf, mitre included, and not one was let go.
    /// So this is insurance against the seed and the strike nobody ran yet,
    /// bought cheap - the queue only fills when something thaws - rather than
    /// the repair of a failure the bench shows daily.
    ///
    /// <b>It crosses the section boundary on purpose, and that is not the
    /// strike escaping its confinement.</b> Reaches cuts what the strike does;
    /// a piece with nothing under it falls by gravity, whoever took its
    /// footing - the same distinction the cascade's docstring already draws
    /// between what a shot reaches and what the solver does about it.
    ///
    /// Judged when the support went rather than swept every frame: each thaw
    /// queues what stood on the thawed piece (<see cref="Perch"/>), and this
    /// drains the queue, thawing whatever no longer answers
    /// <see cref="Footed"/> - which queues what stood on <i>that</i>, so an
    /// undermined stack peels from the bottom up in one drain.</summary>
    private void Undermined()
    {
        while (_perch.Count > 0)
        {
            int j = -1;
            foreach (int k in _perch)
            {
                j = k;
                break;
            }
            _perch.Remove(j);
            if (!_live.Contains(j) && !Footed(j))
                Thaw(j);
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        float step = (float)delta;
        // Before the clock guard, not after: the queue can only fill when
        // something thawed, but whether that also started the clock is the
        // strike's business and no concern of gravity's.
        Undermined();
        if (_clock < 0.0f)
            return;
        _clock += step;
        // Stop what has left the board. Frozen where it fell rather than deleted,
        // so the number of pieces never changes under a caller that indexes them
        // against the plan.
        foreach (int i in _live)
            if (!_bodies[i].Freeze && _bodies[i].Transform.Origin.Y < Lost)
            {
                _bodies[i].Freeze = true;
                _gone.Add(i);
            }
        // How long each fallen piece has been lying still, which is what decides
        // when it starts to go - see Left. Only ever added to, and asked of the
        // velocity rather than of the sleep flag for Still's reason: a piece
        // resting against the tank never sleeps.
        foreach (int i in _live)
        {
            RigidBody3D b = _bodies[i];
            if (b.Freeze
                || (b.LinearVelocity.LengthSquared() <= Still * Still
                    && b.AngularVelocity.LengthSquared() <= Still * Still))
                _lain[i] += step;
        }
        // A piece that has crumbled away leaves the solver, not just the
        // picture. The renderer stops drawing at Left <= 0 and nothing used to
        // stop colliding, so a brick whose bed faded first hovered on an
        // invisible body until its own clock caught up. What lay on it is
        // woken by hand: a body sleeping on a collider that vanishes is not
        // guaranteed a contact event to wake to.
        foreach (int i in _live)
        {
            if (Left(i) > 0.0f || !_cleared.Add(i))
                continue;
            RigidBody3D b = _bodies[i];
            foreach (int k in _live)
                if (k != i && !_bodies[k].Freeze
                    && (_bodies[k].Transform.Origin - b.Transform.Origin)
                       .LengthSquared() < _brick * _brick * 4.0f)
                    _bodies[k].Sleeping = false;
            b.Freeze = true;
            b.CollisionLayer = 0;
            b.CollisionMask = 0;
        }
        if (_tank is null)
            return;
        if (_tankLeft <= 0.0f)
            // Stops where it stopped. A tank that rammed a wall is parked in the
            // rubble it made, and the rest of the wall coming down around it is
            // the picture this shot is about.
            return;
        _tankLeft -= step;
        _tank.GlobalPosition += _tankRun * step;
        _tankGone += _tankRun.Length() * step;
        // A step and a half of lead, so the piece is a dynamic body before the
        // box reaches it however fast the box is going. Measured off the tip it
        // set out from, which for a box driven down one heading is the same band
        // as one measured off where it has got to.
        Reached(_tankTip, _tankInto, _tankLane, 0.0f,
                _tankGone + _tankRun.Length() * step * 1.5f, _tankFace);
    }

    /// <summary>How far the pieces have moved from where they were laid, in
    /// metres: the worst, the median, and how many shifted more than a
    /// centimetre. The question a picture cannot answer - a wall that has sagged
    /// two centimetres a course and one that is coming down look alike at this
    /// zoom.</summary>
    public (float Worst, float Median, int Moved) Drift()
    {
        var d = new List<float>(_bodies.Count);
        for (int i = 0; i < _bodies.Count; i++)
            d.Add((_bodies[i].Transform.Origin
                   - _plan.Blocks[i].Seat * MetresPerCell).Length());
        d.Sort();
        int moved = 0;
        foreach (float v in d)
            if (v > 0.01f)
                moved++;
        return (d.Count == 0 ? 0.0f : d[^1],
                d.Count == 0 ? 0.0f : d[d.Count / 2], moved);
    }

    /// <summary>Where the pieces have got to, in the cell's own units: the same
    /// hexagon factor the standing prop is fitted by, plus how high the heap is
    /// and how much of it left the cell.
    ///
    /// <b>Reported, no longer a guard.</b> Rubble is allowed onto the neighbours
    /// now, so this says where it went rather than whether it stayed - and the
    /// count that used to be zero by construction is the one worth printing.
    /// </summary>
    public (float Reach, float Top, int Off, int Awake) Pile(float spin)
    {
        float reach = 0.0f, top = 0.0f;
        int off = 0;
        var turn = new Basis(Vector3.Up, spin);
        for (int i = 0; i < _bodies.Count; i++)
        {
            Transform3D f = At(i);
            top = Mathf.Max(top, f.Origin.Y);
            WallKit.Block b = _plan.Blocks[i];
            float worst = 0.0f;
            for (int c = 0; c < 8; c++)
            {
                var corner = new Vector3(b.Half.X * ((c & 1) == 0 ? -1 : 1),
                                         b.Half.Y * ((c & 2) == 0 ? -1 : 1),
                                         b.Half.Z * ((c & 4) == 0 ? -1 : 1));
                Vector3 w = turn * (f * corner);
                float x = Mathf.Abs(w.X), z = Mathf.Abs(w.Z);
                worst = Mathf.Max(worst, Mathf.Max(z / (Mathf.Sqrt(3.0f) * 0.5f),
                                                   x + z / Mathf.Sqrt(3.0f)));
            }
            reach = Mathf.Max(reach, worst);
            if (worst > 1.0f)
                off++;
        }
        return (reach, top, off, Awake);
    }
}
