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
    /// at it.</summary>
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
    private AnimatableBody3D? _tank;
    private Vector3 _tankSize;
    private Vector3 _tankRun;
    private float _tankLeft;
    private Vector3 _tankTip;
    private Vector3 _tankInto;
    private float _tankLane;
    private float _tankGone;
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
        // Nothing when the board is driving it: the stand-in exists because that
        // bench has no tank, and a board that has one draws its own. Two would be
        // two tanks in one place, which is the guard in <see cref="Nose"/> said
        // again in the picture.
        if (_tank is null || _driven)
            return null;
        Transform3D t = _tank.Transform;
        return (new Transform3D(t.Basis, t.Origin / MetresPerCell),
                _tankSize / MetresPerCell);
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

        var floor = new StaticBody3D { Name = "Ground" };
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
        }
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
        _tank = null;
        _driven = false;
        _nosePlaced = false;
        _noseGone = 0.0f;
        _noseAim = Vector3.Zero;
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
        // Before the clock, so a refused ram leaves the wall standing rather than
        // struck-but-untouched - see Ram, which is where the reason is written.
        if (shot == Strike.Ram && _driven)
        {
            GD.PushWarning("wall: a ram was fired while a tank is driving the "
                           + "nose - one box at a time");
            return;
        }
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
        // One tank at a time, and the guard is not tidiness: the box below is a
        // stand-in for a tank on a board that has none, so a rig already being
        // driven by a real one would be shown two - the rig's own driving through
        // the wall while the board's drives beside it, both solid. Refused rather
        // than replaced, because which of the two the caller meant is not
        // recoverable here. Turned away in Fire, before the clock is started.
        if (_driven)
            return;
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
            Transform = new Transform3D(
                Basis.LookingAt(into, Vector3.Up), nose),
        };
        _tank.AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(wide, tall, deep) },
        });
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
            Reached(_tankTip, _tankInto, _tankLane, 0.0f, 0.0f);
    }

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
                         float front)
    {
        for (int i = 0; i < _bodies.Count; i++)
        {
            if (_live.Contains(i))
                continue;
            Vector3 arm = _bodies[i].Transform.Origin - tip;
            float along = arm.Dot(into);
            // Up to where the tank has actually got, and not a brick further.
            if (along < -back || along > front)
                continue;
            if ((arm - into * along).Length() > lane)
                continue;
            Thaw(i);
        }
    }

    // --- a tank somebody else drives ----------------------------------------

    /// <summary>Whether a box is being driven from outside. The other half of
    /// <see cref="Ramming"/>: that one is the rig's own box on its way in, this
    /// one is a board's tank that the rig only feels.</summary>
    public bool Driven => _driven;

    private bool _driven;
    private Vector3 _noseAt;
    private Vector3 _noseInto;
    private Vector3 _noseSize;
    private bool _nosePlaced;

    /// <summary>How far behind its nose a driven box has swept, in metres: how
    /// far it has advanced, capped at its own length.
    ///
    /// The cap, and <see cref="Advance"/> is what it caps. Both are named
    /// functions rather than expressions inside <see cref="Drive"/>, because
    /// together they are the whole of the rule that a tank knocks a brick out by
    /// driving into it - and the only part of a ram that can be asserted at all:
    /// the rest is a live solver, and <c>--selftest</c> quits before the first
    /// frame of a scene.</summary>
    public static float Swept(float travel, float length) =>
        Mathf.Clamp(travel, 0.0f, Mathf.Max(length, 0.0f));

    /// <summary>How far the nose has advanced along the heading it is pointing
    /// now: what it had, plus this step's <paramref name="ahead"/> - and nothing
    /// at all of what it had if the heading has turned, <paramref name="held"/>
    /// being the cosine between the old heading and the new.
    ///
    /// <b>Advance, not travel, and that is the whole of it.</b> A band behind the
    /// nose stands for ground the front face has already crossed, and it says
    /// that about <i>one</i> heading: turn, and the volume behind the new nose
    /// was never crossed by the new nose, so what was banked belongs to a
    /// direction the box no longer faces. Kept across the turn it means a hull
    /// pivoting in a yard it does not fit in lets go of everything it overlaps -
    /// which is the bug this replaces, arriving a second time by a longer road: a
    /// trip out and back banks the full length, and from then on every frame,
    /// parked or pivoting, sweeps the whole footprint.
    ///
    /// <b>Backwards sweeps nothing</b>, by the same sentence. The box is aimed by
    /// the hull's facing, so in reverse the leading face is the tail and the band
    /// is behind the wrong end; the safe way to be wrong is frozen, because a
    /// brick left frozen is a brick the hull clips and a brick let go is a wall
    /// that fell for free.</summary>
    public static float Advance(float had, float ahead, float held) =>
        (held < NoseHeld ? 0.0f : Mathf.Max(had, 0.0f))
        + Mathf.Max(ahead, 0.0f);

    /// <summary>How straight a heading has to stay, as a cosine, for the band
    /// behind the nose to carry over from one step to the next.
    ///
    /// Measured off the board rather than picked: the slowest hull turns at
    /// 140 deg/s, which is 2.3 deg in a 60Hz step and a cosine of 0.9992 - well
    /// under this - while a leg driven straight holds its facing to the float,
    /// cosine 1.0. So a turn always resets and a straight run never does, with
    /// three decimal places of room on either side.</summary>
    public const float NoseHeld = 0.9999f;

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
    /// Named for the same reason as <see cref="Swept"/> and
    /// <see cref="Advance"/>: the sign is the whole of the rule, and a live
    /// solver is the one thing <c>--selftest</c> cannot run.</summary>
    public static float Through(float brick) => -brick * NoseThrough;

    /// <summary>How far the driven box has advanced along its current heading, in
    /// metres. The reach behind the nose in <see cref="Drive"/>, reset with the
    /// box and reset by a turn: a tank that drove off, came back and swung round
    /// sweeps from where it is pointing now, not from everywhere it has ever
    /// been.</summary>
    private float _noseGone;

    /// <summary>The heading <see cref="_noseGone"/> was banked along, so that
    /// <see cref="Advance"/> can be told whether the box still faces it. Read
    /// only while <see cref="_nosePlaced"/>, which is why nothing has to seed
    /// it.</summary>
    private Vector3 _noseAim;

    /// <summary>
    /// Where the tank is and how big, in metres, in the prop's own frame -
    /// pushed every frame by whoever is driving it.
    ///
    /// <b>The rig feels it and does not move it.</b> <see cref="Ram"/> owns its
    /// box because there is no tank on that board; here there is one, and how
    /// fast it goes, when it stops and where it turns are the tank's own
    /// business - decided by an order, a speed ceiling and a ground slope that
    /// this class knows nothing about. What is left for the rig is the two things
    /// only it can do: put a kinematic body where the tank is, and let go of what
    /// that body has reached.
    ///
    /// <b>It does not start the clock.</b> A tank parked a cell away is not a
    /// strike, and a wall reported as struck from the first frame would have its
    /// settle report fire before anything had been touched. The clock starts on
    /// the first piece actually let go - see <see cref="Drive"/> - which is the
    /// honest moment: the wall is struck when the tank reaches it.
    /// </summary>
    public void Nose(Vector3 at, Vector3 into, Vector3 size)
    {
        if (_tank is not null && !_driven)
        {
            // The mirror of Ram's guard, and the same reason.
            GD.PushWarning("wall: a tank tried to drive the nose while the rig "
                           + "has a ram of its own - one box at a time");
            return;
        }
        into = new Vector3(into.X, 0.0f, into.Z);
        if (_bodies.Count == 0 || into.LengthSquared() < 1e-6f)
            return;
        _driven = true;
        _noseAt = at;
        _noseInto = into.Normalized();
        if (_tank is null || _noseSize != size)
        {
            _noseSize = size;
            _tank?.QueueFree();
            _tank = new AnimatableBody3D
            {
                SyncToPhysics = true,
                Transform = new Transform3D(
                    Basis.LookingAt(_noseInto, Vector3.Up), at),
            };
            _tank.AddChild(new CollisionShape3D
            {
                Shape = new BoxShape3D { Size = size },
            });
            AddChild(_tank);
            _tankSize = size;
            _nosePlaced = false;
            _noseGone = 0.0f;
            _noseAim = Vector3.Zero;
        }
    }

    /// <summary>Take the driven box away again - the tank has gone somewhere
    /// else. What it has already let go of stays let go: a brick knocked out by a
    /// tank that then drove off is still a brick that was knocked out.</summary>
    public void Halt()
    {
        if (!_driven)
            return;
        _driven = false;
        _nosePlaced = false;
        _noseGone = 0.0f;
        _noseAim = Vector3.Zero;
        _tank?.QueueFree();
        _tank = null;
    }

    /// <summary>One physics step of a box the board is driving: put it where it
    /// has got to, and let go of what it has <b>driven into</b> since it arrived.
    ///
    /// <b>The band is anchored to advance, not to the box and not to travel.</b>
    /// A tank knocks a brick out by driving into it; standing in one, turning in
    /// one and coming to rest with the nose over one are not the same act, and a
    /// board can put a tank somewhere it does not fit. This one does: the ring
    /// stands on the tank's own cell, its nearest standing brick 2.76m off the
    /// middle against hull corners of 2.88 to 3.67m, so the box overlaps masonry
    /// at every heading and sweeps all six leaves as the hull turns on the spot.
    /// Written as the whole footprint, that turn let go of 452 to 468 pieces out
    /// of 536 - and the bench's own readout named it, because the speed at the
    /// moment the masonry first gave was <b>0.0 m/s</b>: the wall was not rammed,
    /// it was swept.
    ///
    /// So the reach behind the nose is <see cref="Advance"/>, capped by
    /// <see cref="Swept"/> at the box's own length - which is exactly the ground
    /// the front face has crossed along the heading it faces now. Nothing at a
    /// standstill, the full footprint once the tank has driven its own length
    /// down one lane, and continuous in between, so no brick the nose has passed
    /// is left frozen for the hull to slide through: <see cref="Thaw"/> is
    /// idempotent and the band grows as fast as the tip does.
    ///
    /// <b>Travel alone was not enough, and the second report is why.</b> A plain
    /// odometer is only ever zero once - on the frame the board is built - so it
    /// fixed the opening and nothing after it. Two apothems is 7.05m against a
    /// 6.0m box, so <i>every</i> approach from outside banks the full length
    /// before the tank parks; and the box is not taken down in between, because
    /// <c>Beside</c> is the cell and its six, and a struck wall keeps the box in
    /// whether the tank moves or not. A trip out and back therefore left the band
    /// saturated, and every frame after it - parked, pivoting, or merely aimed at
    /// a leaf from the next hex - swept the whole footprint again. Resetting on a
    /// turn is what makes the band a statement about one lane.
    ///
    /// The flank term this replaces read "a piece level with the tank's flank has
    /// been driven into just as surely as one at its nose", and that is true of a
    /// tank arriving from outside - whose flank only ever passes bricks its nose
    /// went through first, which are let go already. What it was really covering
    /// was the sideways cases: a hull spawned inside masonry, and one turning in
    /// place. Neither is a ram.
    ///
    /// <b>What is left over is named rather than fixed:</b> parked in the middle
    /// the hull's nose stands 3.0m out against 2.76m of clear yard, so the last
    /// quarter-metre of an approach really does push the tip into the far leaf's
    /// inner face. That is a brick or two, not a leaf, and the lever for it is
    /// the one already named - the tank does not fit in the ring.</summary>
    private void Drive(float step)
    {
        if (_tank is null)
            return;
        Vector3 was = _nosePlaced ? _tank.GlobalPosition : _noseAt;
        Vector3 aim = _nosePlaced ? _noseAim : _noseInto;
        _tank.GlobalTransform = new Transform3D(
            Basis.LookingAt(_noseInto, Vector3.Up), _noseAt);
        _nosePlaced = true;
        // Along the heading, never the plain length of the step: a hull pivoting
        // about its contact point covers ground without advancing a metre, and
        // the band is a statement about ground the front face has crossed.
        float ahead = (_noseAt - was).Dot(_noseInto);
        _noseGone = Advance(_noseGone, ahead, aim.Dot(_noseInto));
        _noseAim = _noseInto;
        Vector3 tip = _noseAt + _noseInto * (_noseSize.Z * 0.5f);
        int had = _live.Count;
        // Nothing forward of the nose at all, and less than nothing: a piece has
        // to be NoseThrough of a brick behind the tip before the tank counts as
        // having driven through it. The forward term this replaces was there
        // against tunnelling, and the band makes it unnecessary - it grows by the
        // same step the tip takes, so a piece skipped by one long frame is caught
        // by the next rather than missed.
        Reached(tip, _noseInto, _noseSize.X * 0.5f + _brick,
                Swept(_noseGone, _noseSize.Z), Through(_brick));
        // Struck when the tank first reaches a brick, and never before: see
        // Nose. The shot is named here rather than at the trigger because with a
        // driven nose there is no trigger - the tank arriving is the whole event.
        if (_live.Count > had && _clock < 0.0f)
        {
            _shot = Strike.Ram;
            _force = 1.0f;
            _clock = 0.0f;
        }
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
        // is still the middle of the plate and not a corner of it.
        // Nearest along the shot, and never nearer than the round started: a
        // ring is masonry on both sides of the gun, so the smallest projection of
        // all is the leaf at the shooter's back. Measured on it - a round aimed at
        // one leaf took the opposite one out, which reads as the wall answering
        // the wrong side rather than as the burst being placed from infinity.
        float front = float.MaxValue;
        for (int i = 0; i < _bodies.Count; i++)
        {
            // Rubble is not masonry, Clearance's own sentence arriving where it
            // decides something: the apron lies on the ground inside the cell, so
            // a round fired from the middle of a ring has a fallen brick at its
            // feet - measured, the nearest piece ahead was 0.13m away and the
            // charge went off beside the tank.
            if (_plan is { } plan && i < plan.Blocks.Count
                && (plan.Blocks[i].Chip || plan.Blocks[i].Course < 0))
                continue;
            Vector3 arm = _bodies[i].Transform.Origin;
            float d = arm.Dot(into);
            if (d < _from)
                continue;
            // And within a brick of the line, because the face is what the round
            // would actually meet. Measured on the ring: a leaf running alongside
            // the shot has pieces at every projection, so the smallest of them was
            // a brick 0.13m along - beside the tank rather than in front of it -
            // and the charge went off there. A plate met head on is unmoved by
            // this: its near face spans the line, so the nearest piece on the line
            // is the nearest piece.
            if ((arm - into * d).Length() > _brick)
                continue;
            front = Mathf.Min(front, d);
        }
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
            float span = Reach(_bodies[i], across / Mathf.Max(off, 1e-4f));
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
    private float Reach(RigidBody3D b, Vector3 n)
    {
        Vector3 half = _plan.Blocks[_bodies.IndexOf(b)].Half * MetresPerCell;
        Basis m = b.Transform.Basis;
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
        RigidBody3D b = _bodies[i];
        b.Freeze = false;
        b.Sleeping = false;
        b.ContinuousCd = true;
        b.ContactMonitor = true;
        b.MaxContactsReported = 6;
        if (!spreads)
            return;
        b.BodyEntered += hit =>
        {
            if (b.LinearVelocity.LengthSquared() < ThawSpeed * ThawSpeed)
                return;
            for (int k = 0; k < _bodies.Count; k++)
                if (ReferenceEquals(_bodies[k], hit))
                {
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

    public override void _PhysicsProcess(double delta)
    {
        float step = (float)delta;
        // Before the clock gate, because a driven nose is what starts the clock:
        // a tank standing off is not a strike, and the wall it has not reached
        // yet is a wall that has not been hit. See Nose.
        if (_driven)
            Drive(step);
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
        if (_tank is null || _driven)
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
                _tankGone + _tankRun.Length() * step * 1.5f);
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
