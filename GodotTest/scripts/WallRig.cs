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

    /// <summary>HE: how hard, and how far it reaches, in metres. The reach is
    /// what makes it a breach instead of a demolition - past it the wall is only
    /// losing its support.</summary>
    public const float HeImpulse = 2200.0f;
    public const float HeReach = 1.2f;

    /// <summary>AP: one line, and only what stands on it. Large, because a piece
    /// that is merely nudged reads as the wall settling rather than as a round
    /// going through it.</summary>
    public const float ApImpulse = 2000.0f;

    /// <summary>How wide the hole is, in metres, measured from the line the
    /// round travels down.
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
    /// <b>It is a calibre, not a dial.</b> The force multiplier moves the
    /// impulse and never this: a heavier round goes through harder, and the
    /// reason only the burst has a radius is written where that radius is.
    /// Measured against the piece it is knocking out - 0.87 x 0.44 x 0.40m - so
    /// half a metre is one block up and one down, and the courses either side
    /// stay where they are.</summary>
    public const float ApBore = 0.55f;
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
        Strike.He => $"{HeImpulse * force:F0} Ns at the burst over "
                     + $"{HeReach * Mathf.Pow(Mathf.Max(force, 0.0f), 1.0f / 3.0f):F1} m",
        _ => $"{ApImpulse * force:F0} Ns down the line",
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
        _tank = null;
        _clock = -1.0f;
        _beam = null;
    }

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
    public void Fire(Strike shot, Vector3 into, float force = 1.0f)
    {
        // Flattened and normalised here rather than trusted: the shot runs along
        // the ground whatever the caller handed over, and a degenerate direction
        // is a round with nowhere to go, not a round straight down.
        into = new Vector3(into.X, 0.0f, into.Z);
        if (_bodies.Count == 0 || into.LengthSquared() < 1e-6f)
            return;
        into = into.Normalized();
        _shot = shot;
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
    }

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
        _tankLane = wide * 0.5f + WallKit.BrickLong * MetresPerCell;
        _tankTip = nose + into * (deep * 0.5f);
        _tankInto = into;
        _tankGone = 0.0f;
        // Nothing at all when the tank never sets off: force nought is no ram,
        // and a wall let go of falls down by itself.
        if (speed > 0.0f)
            Reached(0.0f);
    }

    /// <summary>Let go of everything the tank has driven into by now.
    ///
    /// <paramref name="lead"/> is how far ahead of the nose to look, and it has
    /// to be at least a step's worth of travel: a frozen piece is a static body,
    /// which a kinematic one drives straight through, so a brick still frozen
    /// when the nose arrives is a brick the tank passes through rather than
    /// hits. At the top of the dial a step is 0.65m, which is most of a piece.
    /// </summary>
    private void Reached(float lead)
    {
        float front = _tankGone + lead;
        for (int i = 0; i < _bodies.Count; i++)
        {
            if (_live.Contains(i))
                continue;
            Vector3 arm = _bodies[i].Transform.Origin - _tankTip;
            float along = arm.Dot(_tankInto);
            // Up to where the tank has actually got, and not a brick further.
            if (along < 0.0f || along > front)
                continue;
            if ((arm - _tankInto * along).Length() > _tankLane)
                continue;
            Thaw(i);
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
        float front = float.MaxValue;
        foreach (RigidBody3D b in _bodies)
            front = Mathf.Min(front, b.Transform.Origin.Dot(into));
        Vector3 at = into * (front - HeReach * HeStandoff) + Vector3.Up * mid;
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
        float blast = Mathf.Max(HeReach * grow, 0.0001f);
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
            _bodies[i].ApplyImpulse(dir * HeImpulse * _force * fall);
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
            if (b.Transform.Origin.Y > crest)
            {
                crest = b.Transform.Origin.Y;
                lane = b.Transform.Origin.Dot(side);
            }
        // Gun height - see ApAim - and dropped far enough under that crest to
        // leave a course over the hole, so a wall too low for a gun is hit near
        // its top rather than over it.
        float lintel = ApBore + WallKit.BrickTall * MetresPerCell;
        mid = crest > lintel ? Mathf.Min(ApAim, crest - lintel) : ApAim;
        Vector3 from = -into * reach + side * lane + Vector3.Up * mid;
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
            if (off > ApBore + span * ApBite)
                continue;
            // Only what the round takes, and nothing that touches it - see
            // Thaw: the frozen ring round the gap is what keeps it a hole.
            Thaw(i, spreads: false);
            // Punched in the middle, dropped at the rim. One over one plus the
            // square, the burst's falloff over its own width, for the burst's
            // reason: an edge that is a step is a patch of pieces all leaving at
            // once, which reads as a small charge rather than as a hole.
            float t = off / Mathf.Max(ApBore + span * ApBite, 1e-4f);
            float fall = 1.0f - ApSpall * t * t;
            // <b>Straight down the shot, and the cone was built and measured
            // worse.</b> Real spall goes out in a cone, and leaning the push away
            // from the axis drives every piece into the frozen side of its own
            // hole: measured, the furthest went 1.35m against 4.88m straight. A
            // hole is only as wide as the pieces that left it.
            _bodies[i].ApplyImpulse(into * ApImpulse * _force * fall);
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
        if (_clock < 0.0f)
            return;
        float step = (float)delta;
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
        // box reaches it however fast the box is going.
        Reached(_tankRun.Length() * step * 1.5f);
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
