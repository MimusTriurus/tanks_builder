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

    /// <summary>The cell of whatever stands in the way - a tank, or cover that
    /// stops a round.</summary>
    public Vector2I? BlockedAt { get; init; }

    /// <summary>Whether what blocks it is the board rather than a tank. Two
    /// answers rather than one for <see cref="Shot"/>'s own reason: a tank in
    /// the way moves or is shot first, and a wall in the way is a different
    /// order entirely - drive round it, or breach it.</summary>
    public bool ByCover { get; init; }

    public bool OnLane => Heading >= 0;

    public bool Clear => OnLane && BlockedAt is null;

    public override string ToString() =>
        !OnLane ? "no lane"
        : BlockedAt is Vector2I b
            ? $"{Heading} deg blocked at ({b.X},{b.Y})"
              + (ByCover ? " by cover" : "")
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
                             Vehicle shooter, Vehicle target) =>
        ReferenceEquals(shooter, target)
            ? None : Solve(field, vehicles, shooter, target.Cell);

    /// <summary>
    /// The same solution against a <em>cell</em> rather than against a tank.
    ///
    /// <b>The one the right button asks for, and the tank overload is now written
    /// on top of it.</b> Nothing in the walk ever wanted the target itself: it
    /// wanted the cell to reach, and everything about the shell - the lane, the
    /// range, who is standing in the way, the wall on an edge - is a fact about
    /// two cells. A shell put into empty ground is the same shot as one put into
    /// a tank standing on that ground, which is the whole reason the order is
    /// given by cell.
    ///
    /// The shooter's own cell answers <see cref="None"/> for the reason a tank
    /// cannot be its own target: a gun laid on the hull carrying it is not a
    /// shot, and both buttons read a click on yourself as "stop".
    /// </summary>
    public static Shot Solve(HexField field, IReadOnlyList<Vehicle> vehicles,
                             Vehicle shooter, Vector2I onto)
    {
        if (onto == shooter.Cell)
            return None;
        (int heading, int range) = field.LaneTo(shooter.Cell, onto);
        if (heading < 0)
            return None;
        List<Vector2I> lane = field.Lane(shooter.Cell, heading, range);
        Vector2I? blocked = null;
        bool byCover = false;
        // Cover beside the tanks and in the same walk, because from the round's
        // point of view they are one question: something is standing in the way
        // and the shell stops there. Which of the two it was is carried out
        // separately - see Shot.ByCover - because the two are different orders
        // to give.
        //
        // <b>Masonry is asked of the edge, and the first edge is the shooter's
        // own rim.</b> A wall stands on the boundary of its cell, so a tank
        // inside a ring is firing through its own leaf - the one wall on the
        // board it is actually inside would otherwise be the one wall it could
        // not hit. The same sentence WallProp.Bars is written under, said here
        // about a board that may have no props on it at all.
        Vector2I at = shooter.Cell;
        for (int i = 0; i < lane.Count && blocked is null; i++)
        {
            if (field.Blocked(at, heading))
            {
                blocked = lane[i];
                byCover = true;
                break;
            }
            at = lane[i];
            // The target's own cell is the last of the lane and is skipped, as
            // it always was: what stands on the cell being shot at does not stop
            // the shell aimed at it. The edge on to it is not skipped - that
            // wall is between them.
            if (i + 1 >= lane.Count)
                break;
            bool tank = Vehicle.At(vehicles, lane[i]) is not null;
            if (!tank && !field.Screened(lane[i]))
                continue;
            blocked = lane[i];
            byCover = !tank;
        }
        return new Shot
        {
            Heading = heading, Range = range,
            BlockedAt = blocked, ByCover = byCover,
        };
    }

    /// <summary>
    /// How deep a round from one class gets into another class's armour, as a
    /// damage level - 0 scorch, 1 gouge, 2 breach.
    ///
    /// <code>
    ///          vs LTP   vs MTP   vs HTP   vs TDP   vs HMP
    ///   LTP       0        0        0        0        0
    ///   MTP       1        0        0        0        1
    ///   HTP       2        2        0        2        2
    ///   TDP       2        2        2        2        2
    ///   HMP       2        2        2        2        2
    /// </code>
    ///
    /// **A gun whose might out-classes the plate does its own worth; anything
    /// else scorches the paint.** That single sentence is the whole table, which
    /// is why it is written as a comparison rather than as twenty-five numbers -
    /// numbers would go stale the day a sixth class arrives, and a table with no
    /// rule in it is one where a light tank can quietly start holing heavies.
    ///
    /// Note what it is *not*: a difference. Might minus mass would make a heavy
    /// gouge a medium and breach only a light, and the medium is the tank the
    /// heavy is most obviously meant to overmatch.
    ///
    /// **The two ordinals are what let this table have five rows.** It used to
    /// compare one field against itself - see <see cref="MovementProfile.Might"/>
    /// - which was exactly right while the gun and the hull said the same thing
    /// about a class, and stops being right at the destroyer: a IV on a medium's
    /// hull. So the gun's side is <see cref="MovementProfile.Might"/> and the
    /// plate's is <see cref="MovementProfile.Mass"/>, which is the GDD's front
    /// armour column under its own name.
    ///
    /// **Equal classes come out at 0 for three of the five and 2 for the other
    /// two, and that asymmetry is the GDD's, not an oversight.** A medium has no
    /// special answer to its own armour because a II cannot beat a II. A
    /// destroyer does have one, because its IV beats every plate in the game
    /// including its own, and the mortar's V likewise - which is also why a
    /// ricochet exists only off LT, MT and HT rounds.
    ///
    /// **Capped at <see cref="DeepestLevel"/>, and the cap is the difference
    /// between an ordinal and a level.** Might runs to five and the scar layer is
    /// rendered with three depths, so a IV and a V both arrive at the deepest
    /// picture there is rather than at an index nothing has drawn. Left uncapped
    /// the trace printed a plate as "1/4" and <c>DamageTo</c> silently clamped it
    /// one call later, which is the same number decided in two places.
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
        Penetration(gun.Might, armour);

    /// <summary>
    /// The same table asked with a might rather than with a gun.
    ///
    /// <b>Because one round in the game arrives with a might that is not its
    /// gun's</b>: the destroyer's shell, which loses one going through its
    /// first target - see <see cref="PassedMight"/>. Everything the rule is
    /// about is a might against a mass, so this is the rule and the pair above
    /// is the common way of asking it.
    /// </summary>
    public static int Penetration(int might, MovementProfile armour) =>
        might > armour.Mass ? Math.Min(might - 1, DeepestLevel) : 0;

    /// <summary>
    /// What a round has left after it has gone through something - GDD
    /// classes.md, "Тяжёлый снаряд TD": <b>после прохода огневая мощь снаряда
    /// −1 (IV → III)</b>, and it meets the second thing on the axis with that.
    ///
    /// <b>The rules' one subtraction, written once.</b> It is the whole price
    /// of the pass and the only place a round's might differs from the gun's,
    /// so it is a named method rather than a <c>- 1</c> inside the flight -
    /// see <c>TankTick.Carry</c>, its one caller.
    ///
    /// Floored at zero: a might of I that somehow passed through something has
    /// nothing left, and a negative ordinal would read as a table index rather
    /// than as a spent shell.
    /// </summary>
    public static int PassedMight(MovementProfile gun) =>
        Math.Max(gun.Might - 1, 0);

    /// <summary>
    /// The deepest damage level the scar layer has a drawing for, counting from
    /// zero - so three phases, 0 scorch, 1 gouge, 2 breach.
    ///
    /// <b>A constant here rather than <c>Atlas.ScarLevels - 1</c> because
    /// <see cref="Penetration"/> is pure and has no tank in front of it</b>: it
    /// answers a question about two classes, and which set of pixels the answer
    /// will be drawn with is not part of it. The self-test is what holds the two
    /// together - every atlas on disk is checked to carry exactly this many scar
    /// phases, so a re-render that changed the count fails there rather than
    /// showing the wrong plate.
    /// </summary>
    public const int DeepestLevel = 2;

    /// <summary>
    /// How deep a ram gets into the hull it is driven into, as a damage level -
    /// what <see cref="Penetration"/> answers for a shell.
    ///
    /// <b>One level, and it is the same level whoever rams whoever.</b> A ram has
    /// no calibre to spend: what arrives is a hull, and the two hulls in the
    /// collision are the same two objects whichever way round the order was
    /// given. So the only question is who takes it, and that is
    /// <see cref="RamLevel"/>'s whole content read twice - once each way.
    ///
    /// <code>
    ///          rams LTP  rams MTP  rams HTP  rams TDP  rams HMP
    ///   LTP        1         0         0         0         1
    ///   MTP        1         1         0         1         1
    ///   HTP        1         1         1         1         1
    ///   TDP        1         1         0         1         1
    ///   HMP        1         0         0         0         1
    /// </code>
    ///
    /// <b>Mass and nothing else, which is why a IV in the hull changes none of
    /// this.</b> The destroyer's row is the medium's row and the mortar's is the
    /// light's, because what arrives in a ram is a hull - see
    /// <see cref="MovementProfile.Mass"/>, the field this reads. The GDD says the
    /// same thing the other way round: the ram did not manage to separate MT from
    /// TD, and could have.
    ///
    /// <b>The heavier hull wins the exchange and equals hurt each other</b>, which
    /// is <see cref="Penetration"/>'s shape with one deliberate difference: this
    /// one is <c>&gt;=</c> where the gun's table is <c>&gt;</c>. A gun that does
    /// not out-class the armour scorches the paint, because a shell either gets
    /// through or it does not; two hulls of one class meeting at speed dent each
    /// other, because there is no plate in a ram that is not also a ram. So a
    /// medium ramming a medium is the one exchange both sides lose, and it is
    /// meant to be - it is the reason a ram is a decision rather than a free hit.
    ///
    /// Level 1 rather than the rammer's mass, and that is the difference from a
    /// shell that matters most: a ram cannot breach. It gouges, and a gouge that
    /// gets past the paint knocks the hull out exactly as a round would - see
    /// <see cref="TankTick.FateOf"/> - so a ram is worth one round of whatever
    /// gun could hurt that hull at all.
    /// </summary>
    public static int RamLevel(MovementProfile hull, MovementProfile other) =>
        hull.Mass >= other.Mass ? 1 : 0;

    // <b>PenetrationsToKill is gone (2026-09-08)</b>, and the argument it carried
    // - the matchup says whether, a count says when - is now the rules' own two
    // states: a penetration knocks a hull out, the next hit of any kind destroys
    // it. See Wreck.Disabled and TankTick.FateOf. The matchup still answers the
    // question it was written for: a light gun that cannot get past a heavy's
    // paint cannot knock one out, and so cannot kill one.

    /// <summary>
    /// Which of the six axes a round leaves along when a plate turns it away,
    /// or -1 for a plate that swallows it - GDD units.md, "Рикошет".
    ///
    /// <b>The mirror is <see cref="Vehicle.Graze"/>'s own expression, snapped to
    /// the six.</b> That is the whole method, and it is the point of having one:
    /// the fan of spall and the round that flies on are one event seen twice, so
    /// a second derivation of "which way did it go" would be a picture and a
    /// flight that agree on every board except the one being watched - this
    /// project's own named failure, one number held in two places. The fan keeps
    /// the continuous mirror, because a fan has a half-angle; the flight takes it
    /// snapped, because a round arriving at armour has to arrive across a flat
    /// side or <see cref="AtlasSet.FaceFor"/> is handed a bearing it cannot use.
    ///
    /// <b>Snapped even when everything is already on an axis</b>, and that is not
    /// slack: the plates are measured off the render rather than assumed, so a
    /// side comes back at 89.299 and -89.667 on MTP instead of at ±90. The mirror
    /// of two axis bearings about a measured plate therefore misses its axis by a
    /// degree and a half - a rounding away from the rules' answer, and nowhere
    /// near the next axis.
    ///
    /// <b>Only a side plate turns a round on</b>, which is the rules' own
    /// sentence: a non-penetrating hit on the front or the rear is spent, it
    /// "уходит вверх" and hurts nobody. So the plate is asked here rather than
    /// by the caller, and -1 is the convention <see cref="HexField.HeadingTo"/>
    /// already uses for "there is no such heading".
    ///
    /// <paramref name="from"/> is where the round came from - the bearing the
    /// shooter stands on, which is what <see cref="AtlasSet.FaceFor"/> compared
    /// the plates against - and <paramref name="outward"/> is where the plate
    /// looks, the hull's heading plus <see cref="AtlasSet.HitBearing"/>. What
    /// comes back is a direction of travel and not a bearing back: a round
    /// arriving square on (<c>from == outward</c>) leaves along <c>outward</c>,
    /// straight back at the gun.
    ///
    /// The rules' table, for a hull looking N, is what the self test asserts:
    ///
    /// <code>
    ///   came from   NE   SE   NW   SW
    ///   left along  SE   NE   SW   NW
    /// </code>
    ///
    /// Two faces of one side swap places, which is the mirror written as a table.
    /// <b>The prose in units.md said the opposite of its own table until
    /// 2026-09-08</b> - "составляющая «в сторону борта» сохраняется" - and the table is
    /// the half that is right: a mirror reverses the component across the plate
    /// and keeps the one along it. The sentence was turned over in the commit
    /// that added this method.
    /// </summary>
    public static int Deflect(string face, double from, double outward) =>
        face is "left" or "right"
            ? HexField.EdgeHeadings[Angles.SideFor(2.0 * outward - from)]
            : -1;

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
    /// The rate whatever holds this gun is actually swung at, deg/s - the ring on
    /// a turreted class, the hull on a casemate.
    ///
    /// <b>One definition because laying a gun has two mechanisms now and three
    /// callers that must not care which.</b> The attack loop spends it as a
    /// budget, and the panel divides 180 by it to say how long a rear target
    /// costs; both asked <see cref="TraverseRate"/> directly, which on a
    /// destroyer is nought and made the panel print a swing time of six weeks.
    ///
    /// <b>The level does not scale the hull half, and that is deliberate.</b>
    /// <see cref="TraverseLevel"/> is a control over one mechanism - the ring -
    /// so a class with no ring has nothing for it to be a multiplier of; the hull
    /// rate is a movement figure and belongs to the movement table. A dial that
    /// quietly retuned how fast two of the five tanks drive would be measuring
    /// something else.
    /// </summary>
    public static double LayRate(MovementProfile profile) =>
        profile.Turreted ? TraverseRate(profile) : profile.TurnRate;

    // --- laying the tube, the other axis ------------------------------------

    /// <summary>
    /// The angle from a gun to what it is shooting at, in degrees above the
    /// ground: <c>atan(grade·levels / cells)</c>.
    ///
    /// <b>The board's own number, and the same sentence that generated the
    /// atlas's ladder</b> - <c>pipeline/barrel_recoil.ladder()</c>, which builds
    /// the rendered elevations out of exactly this over every legal (cells,
    /// levels) pair. Written here in the same form on purpose: two expressions
    /// of one geometry agree until somebody changes the grade.
    ///
    /// <b>No projection enters it.</b> A level stands <c>step_grade</c> of a
    /// cell's reach in <em>ground</em> units, so the ratio is unitless and the
    /// camera has nothing to say about it - which is why this can be compared
    /// with a table the renderer wrote without either side knowing the other's
    /// scale. Squash and rise come in later, once, when the height is drawn -
    /// see <see cref="ApexPx"/>.
    /// </summary>
    public static double SightDeg(int cells, int levels, double grade) =>
        cells <= 0 ? 0.0
        : Mathf.RadToDeg(Math.Atan2(grade * levels, cells));

    /// <summary>
    /// How far a mortar can throw, in cells - GDD classes.md, "тяжёлый миномёт
    /// бьёт по целям на дистанции 3, 4 или 5 клеток".
    ///
    /// Here rather than in the movement table because it is not a movement
    /// figure, and because this is the one thing that reads it: the laying table
    /// below has an entry per cell of that band and nothing outside it.
    /// </summary>
    public const int LobReach = 5;

    /// <summary>The shortest throw, and the steepest - see <see cref="LobTable"/>.
    /// </summary>
    public const int LobShortest = 3;

    /// <summary>
    /// What a mortar's tube stands at for each cell of its range band, in
    /// degrees: three cells, four, five.
    ///
    /// <b>The charge varies and the height does not, which is what a mortar crew
    /// actually does</b> - and it is also the only version of this that stays on
    /// the board. Solved the other way, from one muzzle velocity calibrated so
    /// that five cells is the flattest throw a mortar makes (45 degrees), the
    /// range equation hands back 63.4 degrees at four cells and 71.6 at three -
    /// and 71.6 over three cells is an apex of two and a bit cells of world
    /// height. Measured on the event bench: the bomb left the top of the board
    /// and spent its middle third in the grey margin. Physically right, and
    /// unusable.
    ///
    /// So the angles are authored across a band a gunner would recognise and the
    /// charge is what gives way - which is the real mechanism anyway, mortars
    /// being laid within a fixed angle band and loaded with the increment the
    /// range wants.
    ///
    /// <b>And the band is quoted against the board's height, not against a real
    /// mortar's.</b> A level is a <em>quarter</em> of a cell's reach
    /// (<c>HexField.StepGrade</c>), so this board's vertical scale is four times
    /// finer than its ground scale - and an angle that is right for a world
    /// where a hill is as tall as it is wide overshoots everything here. The
    /// first cut was 60/52/45 and measured out at <b>5.2 levels of apex</b> on a
    /// board whose tallest tree is 2.8 and whose tank is 2.2: twice the height
    /// of the tallest thing the bomb could be flying over, which reads as a
    /// firework rather than as a shot. At 45/42/40 the apex is 3.0 to 4.2 levels
    /// - over the trees and no further.
    ///
    /// <b>Two floors, and neither of them is taste.</b> Below the shooter's own
    /// drawn height the round stops counting as being over anything at all -
    /// that is <c>Shell.Lofted</c>'s own threshold, 2.2 levels, and under it the
    /// bomb loses its ground shadow. Below the tallest prop, 2.8 levels, it
    /// stops <em>looking</em> like it clears what a mortar exists to clear. So
    /// the room left under this band is about half a level, and it is worth
    /// knowing that rather than rediscovering it.
    ///
    /// <b>Flat as it goes is a share of the flight, not a height.</b> Held at one
    /// height the far throw would come out flatter than the near one in the only
    /// place anyone judges it - on screen - so the band is compressed instead
    /// and the apex is allowed to grow with the range: the drawn arc is then
    /// about 0.23 to 0.28 of its own chord at every range, which is one weapon
    /// throwing one bomb rather than three.
    ///
    /// <b>Nearer is steeper, which is the half of it anybody watching can
    /// see</b> and the reason the table is ordered this way rather than being one
    /// angle.
    ///
    /// <b>The camera has a say and it is not compensated for.</b> The same throw
    /// draws twice as tall fired away from the camera as across it - height
    /// keeps <c>cos 30</c> of itself while the ground keeps <c>sin 30</c> - so
    /// these ratios are the middle of a spread, not a constant. Sizing the apex
    /// off the drawn chord instead would even that out and would make the bomb
    /// rise higher in the world for pointing away from the camera, which is
    /// exactly the kind of thing the shadow underneath it would then disagree
    /// with.
    /// </summary>
    public static readonly double[] LobTable = { 45.0, 42.0, 40.0 };

    /// <summary>
    /// The angle a mortar's tube stands at to drop a bomb
    /// <paramref name="cells"/> away, in degrees.
    ///
    /// Clamped to the band rather than extrapolated: outside three to five there
    /// is no legal shot to lay for, and a mortar asked for one is answered with
    /// the nearest end of its own table - see <see cref="LobTable"/>.
    /// </summary>
    public static double LobDeg(int cells) =>
        LobTable[Math.Clamp(cells, LobShortest, LobReach) - LobShortest];

    /// <summary>
    /// What this gun's tube is laid at, in degrees above the ground.
    ///
    /// <b>One question with two answers, and the class picks which.</b> A direct
    /// gun points at what it is shooting - the tube <em>is</em> the line of
    /// sight, and on a level board that is zero, which is why this was never
    /// needed until the board grew levels. A mortar points where the bomb has to
    /// leave, which is nowhere near the target and depends on how far away it is.
    /// Both are "where the tube is", so both are this.
    /// </summary>
    public static double LayDeg(MovementProfile gun, int cells, int levels,
                                double grade) =>
        gun.Lobs
            ? LobDeg(cells)
            : SightDeg(cells, levels, grade) + SuperDeg(cells);

    /// <summary>
    /// The flattest lift that still reads on this board, in degrees - what a
    /// direct gun stands at for its longest shot.
    ///
    /// <b>Authored, and the one number here that is.</b> A real tank gun at
    /// three to five hexes needs a fraction of a degree of superelevation; drawn
    /// at that, the tube is level and the round is a straight line, which is the
    /// picture this was written to replace. So the calibration is chosen for the
    /// eye and then the equation is honest about everything else - which is the
    /// same bargain the mortar's table makes, in the other direction.
    ///
    /// <b>4.764 rather than a round number, because it is a rung.</b> The tube
    /// is only ever drawn at the elevations the renderer rendered, so a
    /// calibration that lands between two of them is a calibration that gets
    /// snapped away - see <c>AtlasSet.RungFor</c>. This is the second rung up
    /// the ladder, and its neighbour below (3.576) is where the middle of the
    /// band lands, so the band reads as three states: level, a little, a little
    /// more.
    /// </summary>
    public const double SuperAtReach = 4.764;

    /// <summary>
    /// How far a direct gun would throw if it were laid at 45 degrees, in cells
    /// - the one number that calibrates its muzzle velocity.
    ///
    /// Derived from <see cref="SuperAtReach"/> rather than written beside it, so
    /// that moving the readable lift moves the whole curve with it instead of
    /// leaving two numbers to be kept in step. Comes out around thirty cells,
    /// which is the arithmetic saying what everyone knows: a tank gun's range is
    /// far past anything this board holds, and that is exactly why it shoots
    /// flat across it.
    /// </summary>
    public static double DirectReach =>
        LobReach / Math.Sin(Mathf.DegToRad(2.0 * SuperAtReach));

    /// <summary>
    /// How far above the line of sight a direct gun is laid to reach
    /// <paramref name="cells"/>, in degrees.
    ///
    /// <b>The same range equation as the mortar's and the other root of it.</b>
    /// A thrown round of fixed speed covers <c>R = v²·sin(2θ)/g</c>, and one
    /// range is two angles: the low one, which is a gun shooting at what it can
    /// see, and the high one, which is a mortar dropping a bomb behind a hill.
    /// <see cref="LobDeg"/> takes the high root and this takes the low one, and
    /// <em>that</em> is the whole difference between "стреляет" and "стреляет
    /// навесом" - GDD classes.md, "HM — Навес".
    ///
    /// <b>Which is also why the two go opposite ways with range.</b> On the low
    /// root a farther target wants the tube higher; on the high root it wants it
    /// lower, towards 45 degrees. Both are "nearer is different", and they are
    /// different in opposite directions, which is the thing that reads wrong
    /// until you know there are two roots.
    ///
    /// Nought at the muzzle end of the board by rounding rather than by a rule:
    /// at one cell the answer is under a degree, and the nearest thing the tube
    /// was rendered in is level.
    /// </summary>
    public static double SuperDeg(int cells) =>
        cells <= 0 ? 0.0
        : 0.5 * Mathf.RadToDeg(
            Math.Asin(Math.Clamp(cells / DirectReach, 0.0, 1.0)));

    /// <summary>
    /// How high over the chord a round from this gun goes at the top, in screen
    /// pixels - what <see cref="Shell.Apex"/> is handed.
    ///
    /// <b>The tube decides the arc and not the other way round</b>, which is the
    /// whole of why this exists. A round leaving at <paramref name="lay"/> over a
    /// chord that itself rises at the sight angle stands
    /// <c>D·(tan lay − tan sight)/4</c> over that chord at the top - ordinary
    /// ballistics, and zero exactly when the two angles are the same, which is
    /// what a direct gun is. So a flat shot needs no special case: it gets one by
    /// arithmetic.
    ///
    /// <b>World first, projected once.</b> The range is
    /// <c>cells · HexField.Reach</c> in ground px and the height comes out in the
    /// same units, so the only camera term is the last multiply -
    /// <c>HexField.RiseFactor</c>, screen px per ground px of height. Worked the
    /// other way - a fraction of the drawn chord - the same gun would throw a
    /// different arc up a column than across the screen, because the board
    /// squashes one and not the other.
    /// </summary>
    /// <param name="lay">The angle the tube is standing at, in degrees - which
    /// is not always the angle that was asked for: the ladder has eleven poses
    /// and nothing between them, so the caller snaps first and hands over what
    /// will be drawn. See <c>TankTick.Lay</c>.</param>
    public static float ApexPx(MovementProfile gun, double lay, int cells,
                               int levels, double grade, float reach, float rise)
    {
        if (cells <= 0)
            return 0.0f;
        double over = Math.Tan(Mathf.DegToRad(lay))
                      - Math.Tan(Mathf.DegToRad(SightDeg(cells, levels, grade)));
        return over <= 0.0
            ? 0.0f : (float)(cells * reach * over * 0.25) * rise;
    }

    /// <summary>The same, for a caller with no tube to snap against - the check,
    /// and anything asking what a class would throw rather than what this tank
    /// is about to.</summary>
    public static float ApexPx(MovementProfile gun, int cells, int levels,
                               double grade, float reach, float rise) =>
        ApexPx(gun, LayDeg(gun, cells, levels, grade), cells, levels, grade,
               reach, rise);

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
