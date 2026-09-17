using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// Where a rammed tank goes, and when it may not be rammed at all - GDD
/// units.md, "Таран", and field.md for what the cell behind it is made of.
///
/// <b>A ram moves and does not hurt.</b> The rules are one sentence about it:
/// "тараном танк только сдвигается, повреждений ни один из двух не получает".
/// The rammer takes the victim's hex and the victim is thrown one hex along the
/// ram, keeping its hull heading. What this class answers is the other half -
/// whether that hex will have it.
///
/// <b>Beside the pathing rather than inside it, because a shove is not a
/// drive.</b> <see cref="HexField.Passable"/> is the question "may this tank
/// drive there", and its answer for a step down off a bank is no - except into
/// deep water, which is entered as a fall. A tank that is pushed falls off
/// every bank there is: on to the plain from a hill, into the ravine, into the
/// pond. Teaching <c>Passable</c> that would open those drops to ordinary
/// orders as well, which is precisely the rule the ramps exist to state. So the
/// shove reads the board itself and leaves the pathing alone.
///
/// <b>The mass table is <see cref="Gunnery.RamLevel"/> read as a yes.</b> The
/// GDD's "кто кого таранит" grid and the stand's damage table are one table -
/// "не меньше массы" is <c>hull.Mass &gt;= other.Mass</c> - so a second
/// expression here would be that table written twice, and two copies of a table
/// are one table plus a future disagreement.
/// </summary>
public static class Ramming
{
    /// <summary>Why a ram cannot happen, or <see cref="None"/> when it can.
    /// A word rather than a bare false: the player who is told "no" has to be
    /// told which no it is, and the bench prints it - see
    /// <see cref="Because"/>.</summary>
    public enum Bar
    {
        None,
        /// <summary>The rammer is lighter than its target - classes.md.</summary>
        Mass,
        /// <summary>The hex behind the target is off the board.</summary>
        Edge,
        /// <summary>Somebody is standing on it. A destroyed hull is not
        /// somebody - see <see cref="Vehicle.At"/>.</summary>
        Taken,
        /// <summary>It is higher than the target's own hex. Down is a fall and
        /// is allowed; up wants a ramp, and a ram is not a ramp.</summary>
        Uphill,
        /// <summary>It is more than one level down. The rules describe a fall of
        /// one - hill to plain, plain to ravine or water - and nothing on any
        /// board falls further; refusing it is cheaper than drawing it.</summary>
        Cliff,
        /// <summary>Its floor takes no tank at all: rock.</summary>
        Rock,
        /// <summary>A wall stands on the target's hex or on the one it would be
        /// thrown to - any edge of either, not only the one in the way. HT is
        /// the exception and breaks them instead, classes.md "Бульдозер
        /// HT".</summary>
        Masonry,
        /// <summary>A ramp met across its axis, either under the target or
        /// under the hex behind it. There is no surface to be thrown on to.
        /// </summary>
        Crosswise,
        /// <summary>The target would slide down a ramp on to a hex that is off
        /// the board or occupied - field.md, and then the whole ram is
        /// refused rather than stopped half way.</summary>
        Landing,
    }

    /// <summary>
    /// The shove: the hexes the victim is to be moved through, in order, and
    /// why it may not be.
    ///
    /// One hex normally, two when the first is a ramp taken from above - the
    /// slide of T11, which is not a second event but the same throw continuing
    /// down the slope it landed on.
    /// </summary>
    public readonly record struct Shove(IReadOnlyList<Vector2I> Legs, Bar Why)
    {
        public bool Allowed => Why == Bar.None;
    }

    /// <summary>The refusal in one word, for the log and the bench.</summary>
    public static string Because(Bar why) => why switch
    {
        Bar.None => "",
        Bar.Mass => "the rammer is the lighter hull",
        Bar.Edge => "the hex behind it is off the board",
        Bar.Taken => "the hex behind it is occupied",
        Bar.Uphill => "the hex behind it is higher ground",
        Bar.Cliff => "the hex behind it is more than one level down",
        Bar.Rock => "the hex behind it is rock",
        Bar.Masonry => "masonry stands on one of the two hexes",
        Bar.Crosswise => "a ramp is met across its axis",
        Bar.Landing => "the ramp below has nowhere to put it",
        _ => "no",
    };

    /// <summary>
    /// Whether this hull may shove that one at all - the mass table, and the
    /// whole of what the two classes decide.
    /// </summary>
    public static bool Outweighs(MovementProfile rammer, MovementProfile victim) =>
        Gunnery.RamLevel(rammer, victim) == 1;

    /// <summary>
    /// Where <paramref name="victim"/> goes when it is rammed along
    /// <paramref name="heading"/>, or the reason it cannot be.
    ///
    /// The questions are asked in the rules' own order, and the order shows: a
    /// light tank charging a heavy is refused by the mass table before anything
    /// is asked about the ground, because that is the sentence the player needs
    /// to hear.
    /// </summary>
    public static Shove Of(HexField field, IReadOnlyList<Vehicle> vehicles,
                           MovementProfile rammer, Vehicle victim, int heading) =>
        Of(field, cell => Vehicle.At(vehicles, cell) is not null, rammer,
           victim.Profile, victim.Cell, heading);

    /// <summary>
    /// The same question with the board's own words for it: what is occupied
    /// comes in as a predicate and the victim as its class and its cell.
    ///
    /// <b>Written this way round so it can be asserted at all.</b> A tank on this
    /// bench is a required-member type with an atlas and a sprite behind it, and
    /// the self test has no atlases - the same reason
    /// <see cref="TankTick.RideAt"/> and <see cref="Vehicle.WadingAt"/> exist
    /// beside the methods that call them. A rule nobody can put a board in front
    /// of is a rule that gets read rather than checked.
    /// </summary>
    public static Shove Of(HexField field, Func<Vector2I, bool> occupied,
                           MovementProfile rammer, MovementProfile victim,
                           Vector2I from, int heading)
    {
        var none = new List<Vector2I>();
        if (!Outweighs(rammer, victim))
            return new Shove(none, Bar.Mass);

        Vector2I onto = HexField.Step(from, heading);
        Bar bar = Takes(field, occupied, onto, from);
        if (bar != Bar.None)
            return new Shove(none, bar);

        // Masonry on either hex, and it is the two hexes rather than the edge
        // between them: "любая стена на гексе цели или на гексе выталкивания
        // запрещает таран, не только на пути удара". A bulldozer is not stopped
        // by brick at all - it takes the walls down with the hull - so the
        // question is not asked of it.
        if (!rammer.Bulldozes
            && (field.SidesAt(from) != 0 || field.SidesAt(onto) != 0))
            return new Shove(none, Bar.Masonry);

        // A ramp is a surface with one axis, and a hull thrown across it meets a
        // face rather than a slope - the same refusal Passable states for a
        // drive, said again here because a shove does not go through Passable.
        int under = field.RampHeading(from), there = field.RampHeading(onto);
        if (under >= 0 && heading != under && heading != HexField.Reverse(under))
            return new Shove(none, Bar.Crosswise);
        if (there < 0)
            return new Shove(new List<Vector2I> { onto }, Bar.None);

        int back = HexField.Reverse(heading);
        if (back != there && back != HexField.Reverse(there))
            return new Shove(none, Bar.Crosswise);
        // From the low side the ramp is simply the hex it lands on: the rules
        // let a thrown tank come to rest on a ramp, and only on a ramp does a
        // tank stand where it would never have parked itself (field.md - "на
        // рампе оказывается только подбитый миной или отброшенный тараном").
        if (back != there)
            return new Shove(new List<Vector2I> { onto }, Bar.None);
        // From above it does not come to rest: it runs the slope out on to the
        // hex at its foot - T11. If that hex will not have it the ram is refused
        // whole, rather than leaving a tank parked half way down a slope nothing
        // else on this board ever stops on.
        Vector2I foot = HexField.Step(onto, heading);
        if (Takes(field, occupied, foot, onto) != Bar.None)
            return new Shove(none, Bar.Landing);
        return new Shove(new List<Vector2I> { onto, foot }, Bar.None);
    }

    /// <summary>Whether a hex will take a hull thrown on to it from
    /// <paramref name="from"/> - the board's half of the question, without the
    /// classes or the masonry. Shared by the hex behind the target and by the
    /// foot of the ramp it may slide down, because the rules ask the same three
    /// things of both.</summary>
    private static Bar Takes(HexField field, Func<Vector2I, bool> occupied,
                             Vector2I cell, Vector2I from)
    {
        if (!field.InBounds(cell))
            return Bar.Edge;
        if (!TerrainRules.Drivable(field.FoundationAt(cell)))
            return Bar.Rock;
        int drop = field.LevelAt(from) - field.LevelAt(cell);
        if (drop < 0)
            return Bar.Uphill;
        if (drop > 1)
            return Bar.Cliff;
        if (occupied(cell))
            return Bar.Taken;
        return Bar.None;
    }
}
