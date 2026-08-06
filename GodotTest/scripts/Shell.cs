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
    /// Nine hundred keeps the tracer reading as fast - the head moves 15px a
    /// frame against a 24px trail, so consecutive frames still overlap into one
    /// streak - at the cost of the shortest shot separating gun from impact by
    /// only six frames. Point blank is the hardest case for the ear and there is
    /// not much room in it either way: the tanks are touching.
    /// </summary>
    public const float Speed = 900.0f;

    /// <summary>How long the streak behind the head is, in pixels. About a
    /// frame and a half of travel, so the tracer reads as one object moving
    /// rather than as a dotted line.</summary>
    public const float Trail = 24.0f;

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

    public required float Calibre { get; init; }

    /// <summary>The ceiling this gun's class gets into that armour's class - see
    /// <see cref="Gunnery.Penetration"/>. Fixed when it left, so a round is not
    /// re-judged against a target that has since been selected or repaired.
    /// </summary>
    public required int Level { get; init; }

    /// <summary>True once it has reached the armour. The owner reads this, applies
    /// the hit and frees the node - the shell does not reach into the harness to
    /// do damage, because then the order of two things that both have to happen
    /// exactly once would live in a draw node.</summary>
    public bool Arrived { get; private set; }

    private float _flown;

    /// <summary>Where it is going now, in global space.</summary>
    public Vector2 To => Target.Sprite.ToGlobal(ImpactLocal);

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
        Vector2 to = To;
        float total = (to - From).Length();
        _flown += (float)(Speed * delta);
        if (total <= 0.001f || _flown >= total)
        {
            Position = to;
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
    /// Head, streak and a few sparks trailing off it.
    ///
    /// Ordinary alpha with a dark backing under a hot core, not additive. Every
    /// additive layer in this project has had the same trouble - the ground is a
    /// pale tile around 0.72 and additive light lands on it as a slightly warmer
    /// tile - and the answer the selection ring already found is that a small
    /// bright mark carries its own contrast instead of borrowing the ground's.
    /// A tracer is about six pixels across; it cannot afford to be tinted ground.
    /// </summary>
    public override void _Draw()
    {
        Vector2 to = To;
        Vector2 along = (to - From);
        if (along.LengthSquared() < 1e-6f)
            return;
        along = along.Normalized();
        Vector2 across = new(-along.Y, along.X);

        // Backing first, one line, so the streak reads over the amber route tint
        // as well as over the pale tile.
        DrawLine(-along * (Trail + 2.0f), along * 2.0f,
                 new Color(0.10f, 0.05f, 0.0f, 0.55f), 4.0f);

        // The streak, brightening into the head. Three segments rather than a
        // gradient because a gradient needs a polygon and this is two pixels
        // wide.
        DrawLine(-along * Trail, -along * (Trail * 0.55f),
                 new Color(1.0f, 0.55f, 0.15f, 0.25f), 2.0f);
        DrawLine(-along * (Trail * 0.55f), -along * (Trail * 0.22f),
                 new Color(1.0f, 0.68f, 0.25f, 0.55f), 2.0f);
        DrawLine(-along * (Trail * 0.22f), Vector2.Zero,
                 new Color(1.0f, 0.85f, 0.45f, 0.9f), 2.5f);
        DrawCircle(Vector2.Zero, 2.6f, new Color(1.0f, 0.93f, 0.70f));

        // Sparks shed behind, off the round's own serial so two runs agree. They
        // sit on the streak rather than around the head: a halo of dots round the
        // tip is a firework, and the one thing a tracer has to read as is fast.
        for (int i = 0; i < 3; i++)
        {
            float k = ((Serial * 13 + i * 29) % 17) / 17.0f;
            float back = Trail * (0.35f + 0.6f * k);
            float side = ((i % 2 == 0) ? 1.0f : -1.0f) * (0.8f + 1.4f * k);
            DrawCircle(-along * back + across * side, 1.1f,
                       new Color(1.0f, 0.62f, 0.22f, 0.5f));
        }
    }
}
