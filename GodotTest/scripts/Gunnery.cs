using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// What a shooter can and cannot do to a target from where it stands.
///
/// Three fields rather than one bool, because "cannot shoot" has three
/// different answers and the player has to be told which: not on a lane means
/// drive somewhere else, blocked means the tank in the way has to move or be
/// shot first, and clear means the only thing left is the traverse and the
/// reload. A single flag would make all three read as the feature not working.
/// </summary>
public readonly struct Shot
{
    /// <summary>The flat-side heading the shell would leave along, or -1 when
    /// the target stands on no lane from here.</summary>
    public int Heading { get; init; }

    /// <summary>Cells down the lane, 1 for a neighbour, 0 when there is no
    /// lane.</summary>
    public int Range { get; init; }

    /// <summary>The cell of the tank standing in the way, if one is.</summary>
    public Vector2I? BlockedAt { get; init; }

    public bool OnLane => Heading >= 0;

    public bool Clear => OnLane && BlockedAt is null;

    public override string ToString() =>
        !OnLane ? "no lane"
        : BlockedAt is Vector2I b ? $"{Heading} deg blocked at ({b.X},{b.Y})"
        : $"{Heading} deg at {Range}";
}

/// <summary>
/// The firing rules, in one place and with no scene in them.
///
/// Static and pure for the reason <see cref="Vehicle.SelectionFor"/> is: the whole
/// of this feature is a handful of decisions - whether there is a line, whether
/// something is in it, whether the gun is laid, whether the round is loaded -
/// and every one of them is invisible in a screenshot. Asserted instead.
/// </summary>
public static class Gunnery
{
    /// <summary>No solution at all. A named value rather than
    /// <c>default(Shot)</c>, which would say heading 0 - a real direction, and
    /// the one pointing up and right.</summary>
    public static readonly Shot None = new() { Heading = -1, Range = 0 };

    /// <summary>
    /// How near the lane the gun has to be laid before it will fire, in degrees.
    ///
    /// Tight, because <see cref="Traverse"/> lands exactly on the heading rather
    /// than approaching it: the tolerance is here to survive the last partial
    /// step and the wrap arithmetic, not to allow a shot that is off. Loose
    /// enough to fire while still swinging and the tank shoots along a lane it
    /// is not pointing down, which is the one thing the rule forbids.
    /// </summary>
    public const double LayTolerance = 0.5;

    /// <summary>
    /// Where the shooter's shell would go if it fired at this target now.
    ///
    /// The blocking test walks the lane short of the target: a tank standing in
    /// between stops the shell, and the target standing on the last cell
    /// obviously does not stop it being the target. Cheap enough to redo every
    /// frame, and it has to be - both tanks are usually moving, and a solution
    /// worked out when the order was given would be about where everyone was
    /// standing then.
    /// </summary>
    public static Shot Solve(HexField field, IReadOnlyList<Vehicle> vehicles,
                             Vehicle shooter, Vehicle target)
    {
        if (ReferenceEquals(shooter, target))
            return None;
        (int heading, int range) = field.LaneTo(shooter.Cell, target.Cell);
        if (heading < 0)
            return None;
        List<Vector2I> lane = field.Lane(shooter.Cell, heading, range);
        Vector2I? blocked = null;
        for (int i = 0; i + 1 < lane.Count; i++)
            if (Vehicle.At(vehicles, lane[i]) is not null)
            {
                blocked = lane[i];
                break;
            }
        return new Shot { Heading = heading, Range = range, BlockedAt = blocked };
    }

    /// <summary>
    /// How deep a round from one class gets into another class's armour, as a
    /// damage level - 0 scorch, 1 gouge, 2 breach.
    ///
    /// <code>
    ///          vs LTP   vs MTP   vs HTP
    ///   LTP      -        0        0
    ///   MTP      1        -        0
    ///   HTP      2        2        -
    /// </code>
    ///
    /// **A gun that out-classes the armour does its own calibre's worth; anything
    /// else scorches the paint.** That single sentence is the whole table, which
    /// is why it is written as a comparison rather than as nine numbers - nine
    /// numbers would go stale the day a fourth class arrives, and it is a table
    /// with no rule in it that lets a light tank quietly start holing heavies.
    ///
    /// Note what it is *not*: a difference. Rank minus rank would make a heavy
    /// gouge a medium and breach only a light, and the medium is the tank the
    /// heavy is most obviously meant to overmatch.
    ///
    /// Equal classes come out at 0, which is the one cell of the table that was
    /// not specified and so is an assumption: a tank does not have a special
    /// answer to its own armour, and 0 is what every other unfavourable matchup
    /// gives. It is also the cell the bench cannot show, there being one tank of
    /// each class.
    ///
    /// **This is a level, not a bite, and that is the point.** The armour model
    /// otherwise accumulates - three light rounds reach the breach one heavy
    /// round does - and under that rule a light tank plinking a heavy would hole
    /// it on the third shot, which is exactly what the class matchup exists to
    /// forbid. See <see cref="TankSprite.DamageTo"/>: the shell reaches this
    /// level on the first hit and never goes past it, and the plate keeps the
    /// worst it has taken from anybody.
    /// </summary>
    public static int Penetration(MovementProfile gun, MovementProfile armour) =>
        gun.Rank > armour.Rank ? gun.Rank : 0;

    /// <summary>
    /// How many rounds have to get past the paint before the tank is finished.
    ///
    /// **The matchup says whether, three rounds say when, and splitting the two
    /// is the whole of this number.** It used to say both, and it collapsed: the
    /// kill hung on the deepest scar level, so a heavy killed a medium with its
    /// first shell and a medium killed a light never - one shot or nothing, with
    /// no cell of the table in between. Now the table answers the question it was
    /// written for and only that one. A light kills nobody, a medium kills a
    /// light, a heavy kills a light and a medium, and each of them takes three
    /// goes about it.
    ///
    /// A penetration is a round that came out of <see cref="Penetration"/> above
    /// zero, and that threshold is not a new one: zero is exactly "the gun did not
    /// out-class the armour", which is already what the impact sound switches on -
    /// see <see cref="VehicleAudio.ImpactFor"/>. So a ricochet is not quietly
    /// counted towards a kill, and the ear and the tally agree by construction
    /// rather than by two comparisons kept in step.
    ///
    /// Three, and named rather than left in the comparison: it decides how long
    /// every firefight on this bench lasts.
    /// </summary>
    public const int PenetrationsToKill = 3;

    /// <summary>
    /// How fast the turret is driven, against the tuned per-class triple.
    ///
    /// **A multiplier over <see cref="MovementProfile.TurretRate"/> rather than a
    /// rate**, for the reason the tremble level and the size level are: the three
    /// figures are spread apart on purpose - 240 / 175 / 120, a 2.0x spread that
    /// is most of what makes a heavy turret feel like one - and a control that
    /// set the rate directly would flatten that spread on its first drag.
    ///
    /// One knob for all three tanks, because it is a question about the
    /// mechanism rather than about a tank, and answering it on one while the
    /// other two lay their guns at some other rate would destroy the comparison
    /// the control exists for.
    ///
    /// A mutable static here rather than a field on the harness, which is where
    /// every other level lives, and the reason is <see cref="TraverseRate"/>
    /// below: the rate has two readers that share no object.
    /// </summary>
    public static double TraverseLevel { get; set; } = 1.0;

    /// <summary>The rate this class's turret is actually driven at, deg/s.
    ///
    /// One definition because there are two readers and they are not
    /// interchangeable: <see cref="Main.UpdateAttack"/> spends it as a budget,
    /// and <see cref="VehicleAudio.TraverseEffort"/> divides by it to ask how
    /// hard the motor is working. Scale the first and not the second and every
    /// traverse sounds like a motor pinned at its stop - the same shape of
    /// mistake as a threshold left behind when the quantity under it changed.
    /// </summary>
    public static double TraverseRate(MovementProfile profile) =>
        profile.TurretRate * TraverseLevel;

    /// <summary>
    /// The turret swung towards a heading by at most <paramref name="budget"/>
    /// degrees, landing exactly on it when it is within reach.
    ///
    /// Landing exactly matters more than it looks: the fire gate asks whether
    /// the gun is laid, and a traverse that always stops a fraction short would
    /// close that gate forever while the picture showed a turret pointing
    /// straight at the target. The same shape as the hull's turn in
    /// <see cref="Main"/>, and separate from it because a turret and a hull do
    /// not turn at the same rate on any tank.
    /// </summary>
    public static double Traverse(double facing, double onto, double budget)
    {
        double diff = WrapAngle(onto - facing);
        if (Math.Abs(diff) <= budget)
            return Mod(onto, 360.0);
        return Mod(facing + Math.Sign(diff) * budget, 360.0);
    }

    /// <summary>Whether the gun is pointing down a heading closely enough to
    /// fire along it.</summary>
    public static bool Laid(double facing, int heading) =>
        Math.Abs(WrapAngle(heading - facing)) <= LayTolerance;

    /// <summary>
    /// How far off the gun's own axis a hit sits, in pixels: the perpendicular
    /// distance from the drawn bore line to where the hole is drawn.
    ///
    /// **The whole of the complaint, as one number.** A round used to be placed
    /// by a hash - see <see cref="Main.ScatterAt"/> - which knew nothing about
    /// where the gun was pointing, so the hole landed anywhere along the plate
    /// while the barrel pointed down the lane. The tangential half of that
    /// scatter runs almost exactly across the shot, because the plate a shell
    /// lands on is the one turned towards the shooter, so every pixel of it was
    /// a pixel of miss: +-0.45 of a 28 to 53px half-width is up to 24px.
    ///
    /// Worst at point blank and invisible far away, which is the prediction to
    /// check by eye: 24px across an 83px flight is 16 degrees of visible kink
    /// between the tube and the tracer, and the same 24px at four cells is 2.
    /// </summary>
    public static float BoreMiss(Vector2 muzzle, Vector2 bore, Vector2 impact)
    {
        if (bore.LengthSquared() < 1e-9f)
            return 0.0f;
        Vector2 n = new Vector2(-bore.Y, bore.X).Normalized();
        return Math.Abs(n.Dot(impact - muzzle));
    }

    /// <summary>
    /// Where along its plate a round has to land to sit on the gun's own axis,
    /// as a fraction of the plate's half-width.
    ///
    /// Everything is in one screen space and there is no depth in it, which is
    /// not a shortcut but the shape of the problem: the sprite has no depth to
    /// give, and what the eye is comparing is a drawn tube against a drawn hole.
    /// So the requirement is purely a screen one - the impact must sit on the
    /// ray from the muzzle along the bore - and that is **one** equation:
    ///
    /// <code>
    ///   n . (C + s*T + r*S - M) = 0      n = perpendicular to the bore
    /// </code>
    ///
    /// One equation, two unknowns, and the spare degree of freedom is the point.
    /// The vertical fraction stays free and keeps coming off its own hash, so
    /// consecutive rounds still land in different places; the tangential one is
    /// solved to follow it onto the axis. **A gun that aims at a point and
    /// disperses about it**, rather than one that aims at nothing and lands
    /// anywhere on the plate.
    ///
    /// **The clamp is the common case, not the failure, and that was measured
    /// rather than assumed.** On MTP at one cell the solve wants 1.5 to 2.5
    /// half-widths on most geometries, and the reason is the armour model rather
    /// than the scatter: the four plates do not tile the hull, they are four
    /// patches each with its own centroid, and on an oblique heading the centre
    /// of the rear plate genuinely sits up to 54px to one side of the tank's own
    /// axis against a perpendicular half-width of 18 to 30. A gun laid on the
    /// enemy's turret ring is not pointing at the middle of that plate and never
    /// was.
    ///
    /// So the clamp says something true: a round sent at an oblique tank arrives
    /// on **the near corner** of the plate facing it. That reads better than the
    /// hash ever did, and it reads as geometry - shoot from the left and the
    /// holes are on the left of the plate - where a hash reads as nothing.
    ///
    /// Choosing a different plate was measured and is not the lever: taking
    /// whichever plate still facing the shooter comes nearest the bore gives
    /// 23.8px against the 25.6px <see cref="FaceFor"/> already picks, so the
    /// neighbouring plates are no better placed. What is left is 21.3px of
    /// screen-vertical against 9.5px sideways, and the vertical half is the gun
    /// being drawn level over a plate that sits lower - a real gun would depress
    /// and this sprite has no frame for it. That reads as a shot angled slightly
    /// down, which is what it is.
    ///
    /// <paramref name="fallback"/> covers a tangent lying along the shot, where
    /// sliding along the plate cannot steer at all. It should not arise - the
    /// plate is chosen for facing the shooter, and an affine projection cannot
    /// make two independent ground vectors parallel - so it is a guard rather
    /// than a case, and it hands back the hash so the round still scatters.
    /// </summary>
    public static float ScatterOntoBore(Vector2 muzzle, Vector2 bore,
                                        Vector2 centroid, Vector2 tangent,
                                        Vector2 slope, float rise,
                                        float limit, float fallback)
    {
        Vector2 n = new(-bore.Y, bore.X);
        float across = n.Dot(tangent);
        if (Math.Abs(across) < 1e-3f * n.Length() * tangent.Length())
            return Math.Clamp(fallback, -limit, limit);
        float want = (n.Dot(muzzle - centroid) - rise * n.Dot(slope)) / across;
        return Math.Clamp(want, -limit, limit);
    }

    /// <summary>
    /// The world heading a screen offset points along.
    ///
    /// Screen y grows downward and the isometric view squashes the ground plane
    /// by sin(elevation), so both are undone before an angle is read off. The
    /// same conversion the mouse aim does, here rather than there because the
    /// turret now wants it for a target that is not under the cursor.
    /// </summary>
    public static double HeadingOf(Vector2 screenOffset)
    {
        double worldY = -screenOffset.Y / Math.Sin(Mathf.DegToRad(30.0));
        return Mod(Mathf.RadToDeg(Math.Atan2(worldY, screenOffset.X)), 360.0);
    }

    private static double Mod(double a, double n) => (a % n + n) % n;

    private static double WrapAngle(double degrees) => Mod(degrees + 180.0, 360.0) - 180.0;
}
