using System;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// A round in the air between the gun that fired it and the armour it is going
/// to hit.
///
/// It exists for two reasons and the second one is the reason it was written.
///
/// The obvious one: a shot with nothing between the muzzle flash and the burst
/// is two events that happen to be near each other, and the eye has no way to
/// tell which gun a hit came from when three tanks are firing. A tracer says it
/// in the one channel that costs nothing.
///
/// The one that forced it: without a flight there is no *time* between the two,
/// and the round landed in the very frame it left. Every audio problem this
/// bench has had in the last stretch came back to a short metallic impact
/// starting inside the attack transient of a 1.2 to 3.4 second gun report, and
/// three passes at levels and takes could only ever make that a fairer fight.
/// Separating them in time is what actually removes the masking. It happens to
/// be what the picture wanted as well - a burst appearing four cells away the
/// instant the gun fires is wrong before anything is audible.
///
/// A straight line on screen is not an approximation here: an affine projection
/// takes a straight line in the world to a straight line on screen, so a flat
/// trajectory drawn flat is exact. Tank guns at these ranges shoot flat; there
/// is deliberately no arc.
/// </summary>
public sealed partial class Shell : Node2D
{
    /// <summary>
    /// Screen pixels per second.
    ///
    /// One figure for every class, and the restraint is deliberate: a shell is a
    /// shell, and what differs between a light gun and a heavy one is what
    /// happens when it arrives, which the class matchup already says. A second
    /// per-class table would be another thing to keep in step for a difference
    /// nobody can see over a fifth of a second.
    ///
    /// Not physical, and could not be. A real AP round crosses these ranges in
    /// about two milliseconds, which is one frame - it would put the impact back
    /// inside the report and leave nothing on screen at all.
    ///
    /// Measured off the trace rather than reasoned about, and the short end is
    /// shorter than it looks: a round at one cell flies 83px, not the 149px
    /// between the two anchors, because the gun already reaches out towards the
    /// target and the plate it lands on is the near one. That is 0.09s, about six
    /// frames. Each further cell adds 149px along a slanted lane or 107px up a
    /// column, so four cells is 0.59s.
    ///
    /// **The floor is the ear, and it is a hard one.** At 1500 a point-blank
    /// round crosses its 83px in four frames; at 1800 it is three, and the check
    /// that says "a round at point blank still takes several frames" starts
    /// failing - which is the impact climbing back inside the attack transient
    /// of a 1.2 to 3.4 second gun report, the thing this class was written to
    /// stop. So the slider may go past it and the frame count is what the caption
    /// reports, because past that point the round is fast and the shot is one
    /// noise again.
    ///
    /// It used to be 900, chosen so the head moved less far per frame than the
    /// streak was long and consecutive frames overlapped into one line. **The
    /// smoke took that job over.** At 1500 the head moves 25px against a 22px
    /// streak, so the streak alone would read dotted - and does not, because the
    /// trail behind it is continuous by construction: it is seeded along the
    /// path in world space rather than dragged behind the head.
    /// </summary>
    public const float BaseSpeed = 1500.0f;

    /// <summary>
    /// How fast rounds fly, against <see cref="BaseSpeed"/>.
    ///
    /// A multiplier rather than a rate, the rule the tremble, size and traverse
    /// levels already follow - though here there is no per-class spread to
    /// flatten, because a shell is a shell and what differs between a light gun
    /// and a heavy one is what happens on arrival.
    /// </summary>
    public static double SpeedLevel { get; set; } = 1.0;

    /// <summary>Screen pixels per second, as flown.</summary>
    public static float Speed => (float)(BaseSpeed * SpeedLevel);

    /// <summary>
    /// How long the streak is, per unit of calibre.
    ///
    /// <b>Long enough to carry its own continuity, and that is the number's
    /// whole job.</b> At 1500px/s the head moves 25px a frame; a streak shorter
    /// than that leaves a gap between consecutive frames, and the round reads as
    /// a dotted line rather than as one travelling bar of light. It was 14px and
    /// the trail behind it closed that gap - which worked, and made the smoke
    /// load-bearing for something the streak should say itself.
    ///
    /// So the reference picture and the arithmetic want the same thing: about
    /// nine long to one wide (see <see cref="Hem"/>), which at this calibre is
    /// 52px against a 25px step. The one thing to watch when moving it is that
    /// pairing - a faster round needs a longer streak, and the check says so.
    ///
    /// Clipped to the distance actually flown, because a smear cannot be longer
    /// than the path that made it: at point blank the round crosses about 30px,
    /// and a full-length streak there would draw a solid bar from muzzle to
    /// plate - a beam, not a shell.
    /// </summary>
    public const float Streak = 52.0f;

    /// <summary>
    /// How long the streak is drawn, against <see cref="Streak"/>.
    ///
    /// A multiplier rather than a length, the rule every other level here
    /// follows - though the reason is not a class spread this time, there being
    /// none: it is that the length is paired with the speed. The streak has to
    /// cover the ground the head crosses in a frame or the line goes dotted, so
    /// the two numbers are read together, and the caption says which side of
    /// that the slider is currently on.
    ///
    /// Read live rather than settled at the trigger, with the tracer's size and
    /// for its reason: this decides how the round is drawn, not what it does.
    /// </summary>
    public static double StreakLevel { get; set; } = 1.0;

    /// <summary>How long the streak is, per unit of calibre, as drawn.</summary>
    public static float StreakSize => (float)(Streak * StreakLevel);

    /// <summary>
    /// How wide the streak is drawn, against <see cref="Body"/>.
    ///
    /// <b>Apart from the length, because together they are the proportion.</b>
    /// The size level multiplies both at once - it is the whole round, scaled -
    /// so on its own it can make the tracer bigger and smaller and never
    /// slimmer. What tells a streak from a domino is how long it is against how
    /// wide, and a proportion needs its two terms on two dials.
    ///
    /// The hem is left out of it: it is a pixel outside the body whatever the
    /// body is doing, which is what keeps it from reappearing as a dark spike
    /// off the tail - see <see cref="Edge"/>.
    /// </summary>
    public static double WidthLevel { get; set; } = 1.0;

    /// <summary>Half the width of the lit body, per unit of calibre, as
    /// drawn.</summary>
    public static float BodySize => (float)(Body * WidthLevel);

    /// <summary>
    /// How big the tracer is drawn, against the per-class calibre in
    /// <see cref="MovementProfile.TracerCalibre"/>.
    ///
    /// Read live rather than settled at the trigger, and that is the opposite of
    /// the rule the plate, the scatter and the hit calibre follow - because those
    /// decide what the round *does* and this decides only how it is drawn. The
    /// closer precedent is the tracer's own visibility, which reaches rounds
    /// already in the air for the same reason: a switch that skipped the round
    /// launched a moment ago reads as a switch that does not work.
    /// </summary>
    public static double TracerLevel { get; set; } = 1.0;

    /// <summary>This round's tracer size: its gun's calibre times the level.
    /// Named apart from <see cref="Calibre"/>, which is the hit dial and sizes
    /// the burst - two things called calibre, and only one of them is the gun.
    /// </summary>
    public float TracerSize =>
        (float)(Shooter.Profile.TracerCalibre * TracerLevel);

    /// <summary>
    /// How thick the trail is drawn, against the per-class calibre in
    /// <see cref="MovementProfile.SmokeCalibre"/>.
    ///
    /// **A level of its own, because the trail and the streak are judged against
    /// different things.** The tracer is two pixels of bright on grass and is
    /// judged on being findable; the trail is a soft line that crosses armour and
    /// is judged on reading as smoke rather than as a road. One dial over both
    /// meant every A/B taken on it moved two effects at once - the same objection
    /// that keeps the hit calibre out of the tracer.
    /// </summary>
    public static double SmokeLevel { get; set; } = 1.0;

    /// <summary>This round's trail thickness: its gun's smoke calibre times the
    /// level.</summary>
    public float SmokeSize =>
        (float)(Shooter.Profile.SmokeCalibre * SmokeLevel);

    /// <summary>
    /// Whether the streak is drawn at all - --tracer, or the panel. On.
    ///
    /// <b>It was off, and the reason it was has expired.</b> A debug line that
    /// crept into an A/B would be measuring itself, so while the tracer was one
    /// stroke of plain code that was the right default. It is now a drawn layer
    /// with a calibre, a trail and two levels of its own - something to be judged
    /// rather than something to keep out of the way - and a feature you have to
    /// switch on to see is a feature nobody looks at. The comparison is still one
    /// click away, which is all it ever needed to be.
    ///
    /// Named here rather than left in an initialiser, for the reason
    /// <see cref="Recoil.ShearOnByDefault"/> is: a default nobody can point at is
    /// a default that quietly changes. Beside <see cref="SmokeOnByDefault"/>
    /// rather than on the harness, because what a round looks like is the round's.
    /// </summary>
    public const bool TracerOnByDefault = true;

    /// <summary>
    /// Whether the round lays smoke behind it.
    ///
    /// <b>Off, and it used to be on.</b> The reference picture has no trail at
    /// all - a bar of light and nothing behind it - and the streak is now long
    /// enough to hold the line together on its own, which is the job the smoke
    /// had been doing (see <see cref="Streak"/>). A trail that is no longer
    /// load-bearing and is not in the picture being matched is decoration.
    ///
    /// Kept rather than deleted, and switched by a flag, for the bought muzzle
    /// sheet's reason: "the streak reads better without it" stays an assertion
    /// until the two can be put side by side. Named rather than left in a field
    /// initialiser, the rule <see cref="Recoil.ShearOnByDefault"/> set.
    /// </summary>
    public const bool SmokeOnByDefault = false;

    public static bool SmokeOn { get; set; } = SmokeOnByDefault;

    /// <summary>
    /// Pixels of path between one puff of trail and the next, on the medium.
    /// Scaled with the smoke calibre by <see cref="Step"/>.
    ///
    /// **A puff at birth has to be wider than the gap to the next one**, which is
    /// `2 * SmokeSeed > SmokeStep` and is asserted rather than left to be looked
    /// at: a trail whose fresh end is beads is a dotted line, and it fails at the
    /// head, which is the part of it anybody is looking at. It was 4.0 against a
    /// 3.0px puff and got away with it on the medium because alpha blending closed
    /// the last pixel - so the failure showed up only on the class whose puff had
    /// shrunk.
    /// </summary>
    internal const float SmokeStep = 2.5f;

    /// <summary>
    /// Pixels of path between puffs for this round.
    ///
    /// **Scaled with the calibre, and the light tank is why.** It was constant, on
    /// the argument that a heavier gun should lay a thicker trail rather than a
    /// denser one - and the argument was right about the *ratio* and wrong to hold
    /// the spacing still, because the puff shrank with the calibre and the spacing
    /// did not. Measured on LTP: puffs 2.1px across sitting 4px apart, which is not
    /// a thinner line but a **dotted** one, and it stayed dotted for the whole
    /// flight (a 0.70 puff needs 0.19s of growth to close a 4px gap, and the round
    /// arrives long before that).
    ///
    /// Scaling both keeps the density *relative to the width* identical on all
    /// three classes, which is what that argument actually wanted: one trail at
    /// three sizes, differing in how wide it is and in nothing else.
    /// </summary>
    public float Step => SmokeStep * Math.Max(SmokeSize, 0.05f);

    /// <summary>
    /// Seconds a puff lasts, and with it how long the line hangs after the round
    /// has gone.
    ///
    /// **A value rather than a multiplier, and that is not a lapse of the rule.**
    /// Every other level here - tremble, size, traverse, tracer size - multiplies
    /// a per-class triple, and is a multiplier precisely so the spread between the
    /// classes survives being adjusted. There is no triple under this one: smoke
    /// hangs for as long as smoke hangs, whichever gun made it. So the number is
    /// the thing itself, and the caption turns it into the length of trail it buys
    /// at the current speed, which is what is actually being judged.
    /// </summary>
    public static float SmokeSeconds { get; set; } = TunedSmokeSeconds;

    /// <summary>The tuned figure, named so the panel and the reset have one place
    /// to come back to.</summary>
    public const float TunedSmokeSeconds = 1.1f;

    /// <summary>Radius a puff is born at, per unit of smoke calibre. Internal
    /// because the panel's caption turns it into the width the trail actually
    /// draws, and a caption that repeated the number would be a second copy of
    /// it.</summary>
    internal const float SmokeSeed = 1.5f;

    /// <summary>How fast a puff spreads, px of radius per second. Low, because
    /// what is wanted is a **line** that softens rather than a cloud that
    /// blooms: at the tuned life a puff ends around eleven pixels across, and
    /// the near half of the trail stays two or three.</summary>
    internal const float SmokeGrow = 7.0f;

    private const float SmokeAlpha = 0.55f;

    /// <summary>How many puffs the round has laid so far - the head has passed
    /// the seed point of every one of them.</summary>
    public int Puffs => (int)(Flown / Step) + 1;

    /// <summary>
    /// Where puff <paramref name="k"/> sits, relative to the round's own
    /// position now.
    ///
    /// Public so the one claim the trail rests on can be asserted instead of
    /// described: **it stands still while the round goes past it.** A puff at a
    /// constant offset behind the head is a comet, and the difference between the
    /// two is invisible in a screenshot and obvious in motion, which is precisely
    /// the split this harness's self-tests exist across.
    /// </summary>
    public Vector2 PuffLocal(int k)
    {
        Vector2 along = To - From;
        float total = along.Length();
        if (total < 1e-3f)
            return Vector2.Zero;
        along /= total;
        Vector2 across = new(-along.Y, along.X);
        float at = (k + 0.5f) * Step;
        float wob = ((k * 41 + Serial * 17) % 23) / 23.0f - 0.5f;
        float t = PuffAge(k) / SmokeSeconds;
        return -along * (Flown - at)
               + across * (wob * (1.2f + 5.0f * t) * SmokeSize);
    }

    /// <summary>Seconds since the head went past puff <paramref name="k"/>.
    /// </summary>
    public float PuffAge(int k) =>
        (Flown - (k + 0.5f) * Step) / Math.Max(Speed, 1.0f) + _dead;

    /// <summary>Distance covered, never past the target.</summary>
    private float Flown => Math.Min(_flown, (To - From).Length());

    /// <summary>The gun that fired it. Kept because the impact is the shooter's
    /// business - which class it is decides how deep the round gets - and
    /// because a tracer with no origin cannot be checked against one.</summary>
    public required Vehicle Shooter { get; init; }

    /// <summary>
    /// The tank it is going to hit, or null for a round going at the ground.
    ///
    /// <b>Null is a shot, not an absent one.</b> A gun fires down a flat side of
    /// the hex and hits what it is laid on; laid on nobody it still goes off, and
    /// the round still leaves, flies and lands - it just lands on the field. That
    /// case has no armour in it at all, so the whole armour half below
    /// (<see cref="ImpactLocal"/>, <see cref="Face"/>, <see cref="Scatter"/>,
    /// <see cref="Rise"/>, <see cref="Level"/>, <see cref="BoreMiss"/>,
    /// <see cref="Serial"/>) means nothing and is written blank by the caller
    /// rather than quietly defaulted - see <see cref="Ground"/>.
    /// </summary>
    public required Vehicle? Target { get; init; }

    /// <summary>
    /// Where a round with no target is going, in board space, and how high that
    /// point stands.
    ///
    /// A board point rather than something read off a tank each frame, and that
    /// is the difference in kind between the two ends: the liberty
    /// <see cref="ImpactLocal"/> takes exists because a tank drives away, and the
    /// ground does not. So this is fixed at the shot and nothing has to be frozen
    /// on arrival.
    ///
    /// Ignored while <see cref="Target"/> is set. Left at the origin by the
    /// armour path on purpose: a field that means nothing should read as nothing
    /// rather than as a plausible point.
    /// </summary>
    public Vector2 Ground { get; init; }

    /// <inheritdoc cref="Ground"/>
    public float GroundLift { get; init; }

    /// <summary>
    /// Where on the target it is going, in the target sprite's own space.
    ///
    /// Stored against the tank rather than as a point on the ground, so the round
    /// follows a target that is still moving. A shell does not steer, and this is
    /// the one liberty taken - but the alternative is worse than a liberty: the
    /// tracer would land where the tank no longer is while the burst goes off
    /// where it is, and two halves of one event in two places reads as a bug
    /// rather than as ballistics. Over a fifth of a second at 240px/s the
    /// difference is under fifty pixels anyway.
    /// </summary>
    public required Vector2 ImpactLocal { get; init; }

    /// <summary>Where it left, in board space, fixed at the shot. The muzzle
    /// moves with the turret and the turret keeps tracking, so a launch point
    /// read live would drag the whole tracer sideways behind a traversing
    /// gun.</summary>
    public required Vector2 From { get; init; }

    /// <summary>
    /// How high the muzzle stood above the datum when it fired, in screen
    /// pixels - see <see cref="Vehicle.LiftOf"/>.
    ///
    /// Carried rather than measured on demand for the reason <see cref="From"/>
    /// is: the shooter may drive off after the shot, and a lift re-read from a
    /// tank that has since climbed would walk the tail of the tracer up the
    /// screen. Only the 3D stage reads it - a flat board has no use for a height
    /// told apart from a row - and it is a plain number rather than a world point
    /// so the model stays two-dimensional.
    /// </summary>
    public required float FromLift { get; init; }

    /// <summary>Which round this is, for the spark scatter. Deterministic for
    /// the reason the hit scatter is: --capture and --trace fix the time step so
    /// two runs can be diffed, and a random one would be measuring itself.
    /// </summary>
    public required int Serial { get; init; }

    /// <summary>
    /// Which plate, where on it, at what calibre, and how deep it may get.
    ///
    /// Settled at the trigger and carried, not looked up on arrival. The rule is
    /// already the bench's - the scatter and the calibre travel with a round
    /// rather than being read off a dial while it is on screen - and a flight
    /// makes it matter rather than making it new: a fifth of a second is long
    /// enough for the hull to turn, for the calibre dial to be moved, and for
    /// another shell to land on the same plate first.
    /// </summary>
    public required string Face { get; init; }

    /// <summary>
    /// Where the shooter stood, as a bearing, and the reason it is carried is
    /// <see cref="Face"/>'s.
    ///
    /// <b>It is what the plate was chosen with</b> - see
    /// <see cref="AtlasSet.FaceFor"/> - and what a ricochet is mirrored about
    /// once it arrives (<see cref="Vehicle.Graze"/>). Six values and no more: a
    /// round comes in across a flat side of a hex, so the bearing is one of
    /// <see cref="HexField.EdgeHeadings"/>.
    ///
    /// <b>Not derivable from where the round is.</b> Its own launch point is
    /// here already, so the travel direction across the screen could be
    /// subtracted out - but reflecting about a plate needs a <em>bearing</em>,
    /// and a screen vector is a bearing that has been through the camera's
    /// squash. Inverting that is a second statement of the projection, which is
    /// the way two layers come apart.
    ///
    /// Not required, and the default is honest: the round that goes into the
    /// field has no plate and no bearing to mirror about, exactly as it has no
    /// <see cref="Face"/>.
    /// </summary>
    public double Bearing { get; init; }

    public required float Scatter { get; init; }

    /// <summary>
    /// How far the hole ended up from the gun's own axis, in pixels.
    ///
    /// Zero is the normal answer, because the offset along the plate is solved
    /// to put it there - see <see cref="Gunnery.ScatterOntoBore"/>. It is carried
    /// and printed because the one way that solve can fail is the clamp keeping
    /// the mark on the armour, and a clamped round is a round whose tube points a
    /// little past its own hole. **The picture cannot say which happened**: a few
    /// pixels of kink at close range and a plate too small to reach across look
    /// the same, so the number goes in the trace beside the flight.
    /// </summary>
    public required float BoreMiss { get; init; }

    /// <summary>Where on the plate up its slope, the second half of the scatter.
    /// </summary>
    public required float Rise { get; init; }

    public required float Calibre { get; init; }

    /// <summary>The ceiling this gun's class gets into that armour's class - see
    /// <see cref="Gunnery.Penetration"/>. Fixed when it left, so a round is not
    /// re-judged against a target that has since been selected or repaired.
    /// </summary>
    public required int Level { get; init; }

    /// <summary>
    /// What the round is: a shell that bursts on the face, or one that goes
    /// through.
    ///
    /// <b>Two, because that is how many ways masonry can be broken by something
    /// arriving down a line</b> - see <see cref="WallRig.Strike"/>, whose third
    /// is a tank and is therefore not a round at all. Against armour it means
    /// nothing yet, which <see cref="TankTick.Ammo"/> says out loud.
    /// </summary>
    public enum Kind { He, Ap }

    /// <summary>Which of the two this one is. Settled at the trigger and carried,
    /// like the calibre beside it: a dial turned while the round is in the air
    /// must not change what arrives.</summary>
    public Kind Ammo { get; init; } = Kind.He;

    /// <summary>
    /// Which cell stopped it, or null for a round that ran out of board.
    ///
    /// <b>Carried rather than worked out from where it landed, and that is not
    /// tidiness.</b> <see cref="TankTick.Track"/> stops the walk at the near side
    /// of whatever blocked it - one sample short, up to an eighth of a tile - on
    /// purpose, so the landing point is deliberately not inside the cell that
    /// stopped it. Asking the point which cell it is in therefore answers the
    /// cell before, every time.
    ///
    /// Meaningless while <see cref="Target"/> is set: a round going at armour was
    /// stopped by a tank, and which tank is <see cref="Target"/> itself.
    /// </summary>
    public Vector2I? Blocked { get; init; }

    /// <summary>True once it has reached the armour. The owner reads this and
    /// applies the hit - the shell does not reach into the harness to do damage,
    /// because then the order of two things that both have to happen exactly
    /// once would live in a draw node.</summary>
    public bool Arrived { get; private set; }

    /// <summary>Set by the owner once it has applied the strike. The round
    /// outlives its own arrival now - the smoke hangs after the shot is over -
    /// so "has landed" and "has been dealt with" stopped being the same fact,
    /// and the damage has to happen exactly once across the frames in
    /// between.</summary>
    public bool Struck { get; set; }

    /// <summary>
    /// True once the trail has faded and there is nothing left to draw.
    ///
    /// **This is what the owner frees on, not <see cref="Arrived"/>.** A trail
    /// that vanishes the instant the round lands is the thing this drawing was
    /// changed to stop: smoke that disappears on impact reads as the effect being
    /// switched off rather than as smoke.
    /// </summary>
    /// <remarks>With the trail switched off there is nothing to wait for, and
    /// the round has to go at once: otherwise it lingers a second drawing
    /// nothing, and the trace reports smoke that is not there - which is the
    /// same picture as a trail that failed to draw.</remarks>
    public bool Expired => Arrived && (!SmokeOn || _dead > SmokeSeconds);

    private float _flown;

    /// <summary>Seconds since it landed.</summary>
    private float _dead;

    /// <summary>Where the path ended, frozen at the moment of arrival.</summary>
    private Vector2 _landed;

    /// <summary>And how high, frozen with it and for the same reason.</summary>
    private float _landedLift;

    /// <summary>
    /// Where it is going now, in global space - and where it went, once it has.
    ///
    /// Frozen on arrival, and it has to be: the trail is drawn along this
    /// direction, so a target that drives away afterwards would swing the whole
    /// line of smoke round behind it. While the round is flying, following the
    /// target is the deliberate liberty; once it has hit, the smoke belongs to
    /// the air rather than to the tank.
    /// </summary>
    public Vector2 To =>
        Arrived ? _landed : Target is null ? Ground : Target.Spot(ImpactLocal);

    /// <summary>How high the armour it is going for stands, in screen pixels.
    /// Frozen on arrival with <see cref="To"/>, which it is the other half
    /// of.</summary>
    public float ToLift =>
        Arrived ? _landedLift
        : Target is null ? GroundLift : Target.LiftOf(To);

    /// <summary>Above every tank. A tracer disappearing behind a hull it is
    /// passing reads as a dropped frame, and it is a few pixels of bright on a
    /// sprite nobody is judging at that moment - unlike the ground marks, which
    /// go under the tanks precisely so they look like ground.</summary>
    public const int Layer = 4000;

    /// <summary>The shortest shot this bench can produce, in pixels: two adjacent
    /// tanks, measured off the trace. Not 149px, which is the gap between the
    /// anchors - the gun reaches out towards the target and the plate it lands on
    /// is the near one. Named so the flight at point blank can be asserted, since
    /// that is the case a faster round would quietly take back to nothing.
    /// </summary>
    public const float PointBlank = 83.0f;

    public override void _Ready()
    {
        ZIndex = Layer;
        Position = From;
    }

    /// <summary>
    /// Move it on by one frame.
    ///
    /// The distance is re-read every frame because the target may be moving, so
    /// the fraction along the path is recomputed rather than integrated. It costs
    /// a little exactness in speed while the target moves - the path is being
    /// stretched under the round - and buys the tracer and the burst agreeing
    /// about where the armour is, which is the thing anybody would notice.
    /// </summary>
    public void Advance(double delta)
    {
        if (Arrived)
        {
            // Landed: it stops being a round and becomes the smoke it left. It
            // still has to ask to be redrawn, because the trail is fading.
            _dead += (float)delta;
            QueueRedraw();
            return;
        }
        Vector2 to = To;
        float lift = ToLift;
        float total = (to - From).Length();
        _flown += (float)(Speed * delta);
        if (total <= 0.001f || _flown >= total)
        {
            Position = to;
            _landed = to;
            // Read before Arrived is set, because ToLift answers off the frozen
            // pair the moment it is - which would freeze it to itself.
            _landedLift = lift;
            Arrived = true;
        }
        else
        {
            Position = From.Lerp(to, _flown / total);
        }
        QueueRedraw();
    }

    /// <summary>How far along it is, 0 at the muzzle and 1 on the armour. For the
    /// trace, and for asserting that it actually crosses rather than jumping.
    /// </summary>
    public float Fraction
    {
        get
        {
            float total = (To - From).Length();
            return total <= 0.001f ? 1.0f : Math.Clamp(_flown / total, 0.0f, 1.0f);
        }
    }

    /// <summary>
    /// A tracer and the smoke it leaves, and nothing else.
    ///
    /// Ordinary alpha with a dark backing under a hot core, not additive. Every
    /// additive layer in this project has had the same trouble - the ground is a
    /// pale tile around 0.72 and additive light lands on it as a slightly warmer
    /// tile - and the answer the selection ring already found is that a small
    /// bright mark carries its own contrast instead of borrowing the ground's.
    /// A tracer is a few pixels across; it cannot afford to be tinted ground.
    ///
    /// **The trail is seeded along the path, not dragged behind the head**, and
    /// that is the whole of it. Puffs hung at fixed offsets behind the round move
    /// with it, which reads as a comet; smoke stands still and disperses. So a
    /// puff belongs to a distance down the path, comes into being when the head
    /// passes it, and ages from there - which also makes the trail continuous at
    /// any speed, and is why the round could be allowed to fly faster than its
    /// own streak is long.
    ///
    /// The smoke is a mid grey rather than white, for the reason the selection
    /// ring is not: it spends its life over a pale tile, where white is nothing,
    /// and crosses armour, where black would be nothing. Carrying its own
    /// contrast costs it a little realism and buys it being visible at all.
    ///
    /// The sparks are gone with the sheet of things that were decoration: a halo
    /// of dots round the tip is a firework, and the one thing a tracer has to
    /// read as is fast.
    /// </summary>
    public override void _Draw()
    {
        Vector2 to = To;
        Vector2 along = to - From;
        float total = along.Length();
        if (total < 1e-3f)
            return;
        along /= total;
        float cal = TracerSize;
        // Two calibres, not one, and this is the whole of that split on the
        // drawing side: the streak takes the gun's, the trail takes the trail's.
        float puff = SmokeSize;

        if (SmokeOn)
            for (int k = Puffs - 1; k >= 0; k--)
            {
                // Walking k downwards walks backwards along the path, so once
                // one puff is too old every puff behind it is too. The wobble
                // inside PuffLocal comes off the index and the round's serial,
                // so two runs of --capture agree - the rule the hit scatter set.
                float age = PuffAge(k);
                if (age < 0.0f)
                    continue;
                if (age > SmokeSeconds)
                    break;
                float fade = (float)Math.Pow(1.0f - age / SmokeSeconds, 0.7);
                DrawCircle(PuffLocal(k), (SmokeSeed + SmokeGrow * age) * puff,
                           // Linear rather than squared, and that was the
                           // difference between a trail and a smudge: a puff
                           // grows as it ages, so a squared fade takes the
                           // opacity away exactly as the area arrives and the
                           // wide half of the trail is never seen.
                           new Color(0.40f, 0.38f, 0.36f, SmokeAlpha * fade));
            }

        // Nothing bright is drawn once it has landed - what is left is the
        // smoke. The round is still here only because the smoke is.
        if (Arrived)
            return;

        // **A tapered lance, three shells thick across rather than three
        // segments long, and both halves of that are what the reference
        // picture is.** The old streak graded along its length - dim tail into a
        // bright head - which reads as a stick with a lit tip; a round travelling
        // fast reads as one even bar of light, hot all the way, and what varies
        // is across it: a near-white core inside a warm rim inside a dark hem.
        //
        // The hem is what makes that legal here. The reference is drawn over
        // near-black cobbles, where a white core additive over the ground is the
        // whole effect; this board's ground is a pale tile around 0.72, where the
        // same thing is a slightly warmer tile. So the outermost layer carries
        // the contrast the ground will not lend - the selection ring's answer,
        // and the reason this is ordinary alpha rather than additive.
        //
        // Both ends taper, which needs a polygon rather than a line. A line's
        // ends are square, and a square-ended bar of light is a domino: the
        // tapered head is most of what says which way it is going.
        float len = Mathf.Min(StreakSize * cal, Flown);
        Vector2 across = new(-along.Y, along.X);
        // Three nested lances off one profile, hem outwards. One profile because
        // the layers are one shape at three widths - written apart, they part
        // company at the head, where the taper is the whole of the reading.
        DrawPolygon(Lance(along, across, len, (BodySize + Edge) * cal),
                    Fill(new Color(0.07f, 0.04f, 0.02f, 0.55f)));
        DrawPolygon(Lance(along, across, len, BodySize * cal),
                    Fill(new Color(1.0f, 0.46f, 0.11f, 1.0f)));
        DrawPolygon(Lance(along, across, len, BodySize * cal * CoreOf),
                    Fill(new Color(1.0f, 0.95f, 0.87f, 1.0f)));
    }

    /// <summary>Half the width of the lit body, per unit of calibre. Against
    /// <see cref="Streak"/> this is the slenderness the reference is drawn at -
    /// about nine long to one wide - and it is the one number to move if the
    /// round starts reading as a dash rather than as a tracer.</summary>
    internal const float Body = 3.0f;

    /// <summary>
    /// How far the dark hem stands outside the body, per unit of calibre.
    ///
    /// <b>An outset rather than a fraction, and the tail is why.</b> Written as
    /// a wider copy of the same lance, the hem tapers to nothing later than the
    /// body does - so the last several pixels of the streak were hem and nothing
    /// else, and the round drew a black spike out of its own tail. Held a pixel
    /// outside the body all the way along, there is no such stretch: the hem
    /// closes where the body closes.
    ///
    /// It is a pixel because that is all it has to be. Its job is to carry
    /// contrast against a pale tile - the selection ring's answer - not to be
    /// seen.
    /// </summary>
    internal const float Edge = 0.9f;

    /// <summary>The near-white core, as a fraction of the body. A fraction
    /// rather than a width of its own, so the streak stays one shape at any
    /// calibre - the rule the trail's spacing had to learn.</summary>
    internal const float CoreOf = 0.62f;

    /// <summary>
    /// How much of each end is taper: a short cone at the nose, a longer one at
    /// the tail, full width in between.
    ///
    /// <b>A trapezium rather than a curve, and that is what makes it read as one
    /// bar of light.</b> The first shape here closed as a power of the distance
    /// left, which is the right curve for a smear that thins out - and it meant
    /// the streak was at full width nowhere and under half width over most of
    /// its length, so the near-white core measured a single pixel across and the
    /// whole thing read as an amber needle. The reference is even along its
    /// middle and pointed only at the ends; a plateau says that directly.
    /// </summary>
    private const float Nose = 0.08f;

    private const float Tail = 0.22f;

    /// <summary>How many steps down each side of the lance. Twelve because the
    /// taper is a curve and the head is a couple of pixels: fewer shows as a
    /// facet on the one part of the shape anybody reads.</summary>
    private const int Steps = 12;

    /// <summary>
    /// The outline of one layer: the head at the origin, the tail
    /// <paramref name="len"/> back along the path, tapered at both ends.
    ///
    /// Public shape, private colour - the profile is asserted rather than
    /// described, because "it tapers" and "it is a rectangle with mitred
    /// corners" look the same in a still frame at these sizes.
    /// </summary>
    internal static Vector2[] Lance(Vector2 along, Vector2 across, float len,
                                   float half)
    {
        var pts = new Vector2[2 * Steps];
        for (int i = 0; i < Steps; i++)
        {
            float s = i / (float)(Steps - 1);
            float w = half * Waist(s);
            Vector2 at = -along * (len * s);
            pts[i] = at + across * w;
            pts[2 * Steps - 1 - i] = at - across * w;
        }
        return pts;
    }

    /// <summary>How wide the lance is a fraction <paramref name="s"/> back from
    /// its head, as a fraction of the widest. Zero at both ends by construction,
    /// which is what "tapered" has to mean for the outline to close.</summary>
    internal static float Waist(float s) =>
        Mathf.Min(Mathf.Min(1.0f, s / Nose),
                  Mathf.Min(1.0f, Mathf.Max(1.0f - s, 0.0f) / Tail));

    private static Color[] Fill(Color ink)
    {
        var flat = new Color[2 * Steps];
        for (int i = 0; i < flat.Length; i++)
            flat[i] = ink;
        return flat;
    }
}
