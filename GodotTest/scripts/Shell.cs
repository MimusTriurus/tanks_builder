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

    /// <summary>How long the bright streak behind the head is, per unit of
    /// calibre.</summary>
    public const float Streak = 14.0f;

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

    /// <summary>Whether the round lays smoke behind it. Named rather than left
    /// in a field initialiser, the rule <see cref="Recoil.ShearOnByDefault"/>
    /// set: this one carries the tracer's continuity at speed, so turning it off
    /// is a comparison somebody should have to ask for.</summary>
    public const bool SmokeOnByDefault = true;

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

    public required Vehicle Target { get; init; }

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

    /// <summary>Where it left, in global space, fixed at the shot. The muzzle
    /// moves with the turret and the turret keeps tracking, so a launch point
    /// read live would drag the whole tracer sideways behind a traversing
    /// gun.</summary>
    public required Vector2 From { get; init; }

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

    /// <summary>
    /// Where it is going now, in global space - and where it went, once it has.
    ///
    /// Frozen on arrival, and it has to be: the trail is drawn along this
    /// direction, so a target that drives away afterwards would swing the whole
    /// line of smoke round behind it. While the round is flying, following the
    /// target is the deliberate liberty; once it has hit, the smoke belongs to
    /// the air rather than to the tank.
    /// </summary>
    public Vector2 To => Arrived ? _landed : Target.Sprite.ToGlobal(ImpactLocal);

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
        float total = (to - From).Length();
        _flown += (float)(Speed * delta);
        if (total <= 0.001f || _flown >= total)
        {
            Position = to;
            _landed = to;
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

        // Backing first, one line, so the streak reads over the amber route tint
        // as well as over the pale tile and its own smoke.
        // Nothing bright is drawn once it has landed - what is left is the
        // smoke. The round is still here only because the smoke is.
        if (Arrived)
            return;

        float len = Streak * cal;
        DrawLine(-along * (len + 1.0f), along * 1.5f,
                 new Color(0.10f, 0.05f, 0.0f, 0.45f), 2.6f * cal);

        // The streak, brightening into the head. Two segments rather than a
        // gradient because a gradient needs a polygon and this is two pixels
        // wide.
        //
        // **Both segments have to out-cover their own backing.** The dim one ran
        // at 0.35 over a 0.55 black and composited to dark brown, so the tracer
        // read as a stick with a lit tip rather than as a streak - the backing is
        // there to carry contrast against the ground, not to show through.
        DrawLine(-along * len, -along * (len * 0.4f),
                 new Color(1.0f, 0.58f, 0.22f, 0.60f), 1.7f * cal);
        DrawLine(-along * (len * 0.4f), Vector2.Zero,
                 new Color(1.0f, 0.80f, 0.42f, 0.92f), 2.2f * cal);
        // **The core is small and near white, and the mass around it is smoke.**
        // A big amber ball is a fireball travelling sideways; a hot point with a
        // line of smoke behind it is a round. The bright part is a pixel and a
        // half on the medium, which is as much of it as there should be.
        DrawCircle(Vector2.Zero, 1.5f * cal, new Color(1.0f, 0.97f, 0.90f));
    }
}
