using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// What the rules ask of a board, and nothing they draw with
/// (docs/battle-core-plan.md, Д2). <see cref="Steps"/>, <see cref="Gunnery"/>,
/// <see cref="Ramming"/> and <see cref="Mines"/> take this, so the same rule
/// runs on the bench's <see cref="HexField"/> and on <see cref="BattleBoard"/>,
/// the board as plain arrays the rules core and the AI copy and search.
///
/// <b>Two halves.</b> The cell's own layers are read off the board
/// (<see cref="LevelAt"/>, <see cref="RampHeading"/>, <see cref="FoundationAt"/>,
/// <see cref="CoverAt"/>, <see cref="CoverStateAt"/>, <see cref="SidesAt"/>);
/// everything worked out of them - the step, the drop, the wall across an
/// edge, the screen, the lane - is <see cref="BoardRules"/>'s, once, for both
/// boards. A board that answered those itself would be a second answer.
/// </summary>
public interface IHexBoard
{
    int Columns { get; }
    int Rows { get; }
    /// <summary>The board has levels at all; without them every cell is level 0
    /// and a step never climbs.</summary>
    bool Leveled { get; }
    /// <summary>The levels are joined by ramps; without them a step may climb
    /// <see cref="MaxClimb"/> levels (the old boards, <c>--no-ramps</c>).</summary>
    bool Ramped { get; }
    /// <summary>Levels joined by slopes (<c>--relief</c>, BOT-11): a step down by
    /// up to <see cref="MaxClimb"/> is a drive, not a fall.</summary>
    bool Slopes { get; }
    int MaxClimb { get; }

    bool InBounds(Vector2I cell);
    int LevelAt(Vector2I cell);
    /// <summary>The world heading of a ramp's high edge, or -1 where the top is flat.</summary>
    int RampHeading(Vector2I cell);
    Foundation FoundationAt(Vector2I cell);
    Cover CoverAt(Vector2I cell);
    CoverState CoverStateAt(Vector2I cell);
    /// <summary>The cell's own standing walls, a mask over
    /// <see cref="HexField.EdgeIndex"/>; 0 where its cover is not walls.</summary>
    int SidesAt(Vector2I cell);

    bool IsRamp(Vector2I cell);
    bool IsWater(Vector2I cell);
    bool IsDeep(Vector2I cell);
    bool Passable(Vector2I cell, int heading);
    bool Drops(Vector2I from, Vector2I onto);
    bool Walled(Vector2I from, int heading);
    bool Blocked(Vector2I from, int heading);
    bool Screened(Vector2I cell);
    List<Vector2I> Lane(Vector2I cell, int heading, int limit = int.MaxValue);
    (int Heading, int Range) LaneTo(Vector2I from, Vector2I to);
}

/// <summary>
/// The board's rules worked out of a cell's layers - one copy for every
/// <see cref="IHexBoard"/>. Moved here out of <see cref="HexField"/> as they
/// were (Д2); the reasons are on the methods there that call these.
/// </summary>
public static class BoardRules
{
    public static bool IsRamp(IHexBoard b, Vector2I cell) => b.RampHeading(cell) >= 0;

    public static bool IsWater(IHexBoard b, Vector2I cell) =>
        b.FoundationAt(cell) is Foundation.Shallow or Foundation.Deep;

    public static bool IsDeep(IHexBoard b, Vector2I cell) => b.FoundationAt(cell) == Foundation.Deep;

    /// <summary><see cref="HexField.Passable"/>.</summary>
    public static bool Passable(IHexBoard b, Vector2I cell, int heading)
    {
        Vector2I next = HexField.Step(cell, heading);
        if (!b.InBounds(cell) || !b.InBounds(next))
            return false;
        if (!TerrainRules.Drivable(b.FoundationAt(next)))
            return false;
        if (!b.Leveled)
            return true;
        if (!b.Ramped)
            return System.Math.Abs(b.LevelAt(next) - b.LevelAt(cell)) <= b.MaxClimb;

        int here = b.RampHeading(cell), there = b.RampHeading(next);
        int back = HexField.Reverse(heading);
        if (here >= 0)
        {
            if (heading != here && heading != HexField.Reverse(here))
                return false;
            if (there >= 0)
                return false;
            return b.LevelAt(next) == b.LevelAt(cell) + (heading == here ? 1 : 0);
        }
        if (there >= 0)
        {
            if (back != there && back != HexField.Reverse(there))
                return false;
            return b.LevelAt(cell) == b.LevelAt(next) + (back == there ? 1 : 0);
        }
        return b.LevelAt(next) == b.LevelAt(cell)
               || (TerrainRules.Plunges(b.FoundationAt(next))
                   && b.LevelAt(next) == b.LevelAt(cell) - 1);
    }

    /// <summary><see cref="HexField.Drops"/>.</summary>
    public static bool Drops(IHexBoard b, Vector2I from, Vector2I onto) =>
        b.Slopes
            ? b.LevelAt(onto) < b.LevelAt(from) - b.MaxClimb
              || (TerrainRules.Plunges(b.FoundationAt(onto)) && b.LevelAt(onto) < b.LevelAt(from))
            : b.RampHeading(from) < 0 && b.RampHeading(onto) < 0
              && b.LevelAt(onto) < b.LevelAt(from);

    public static bool SideStands(IHexBoard b, Vector2I cell, int heading) =>
        (b.SidesAt(cell) & (1 << HexField.EdgeIndex(heading))) != 0;

    /// <summary><see cref="HexField.Walled"/>: either cell's wall on the shared edge.</summary>
    public static bool Walled(IHexBoard b, Vector2I from, int heading) =>
        SideStands(b, from, heading)
        || SideStands(b, HexField.Step(from, heading), HexField.Reverse(heading));

    /// <summary><see cref="HexField.Blocked"/>.</summary>
    public static bool Blocked(IHexBoard b, Vector2I from, int heading) =>
        TerrainRules.Of(Cover.Walls).Blocks && Walled(b, from, heading);

    /// <summary><see cref="HexField.Screened"/>: what stands on the hex stops a
    /// round crossing it - a wood. Not masonry: a wall stands on an edge and
    /// stops what crosses that edge (<see cref="Blocked"/>), not the hex
    /// (GDD field.md, «кирпичные стены»; П-26, Д4). <c>terrain.json</c> has
    /// the walls screen for the 2D board's capon, which asks the table.</summary>
    public static bool Screened(IHexBoard b, Vector2I cell) =>
        b.CoverAt(cell) != Cover.Walls && TerrainRules.Screens(b.CoverAt(cell), b.CoverStateAt(cell));

    /// <summary><see cref="HexField.Lane"/>.</summary>
    public static List<Vector2I> Lane(IHexBoard b, Vector2I cell, int heading, int limit = int.MaxValue)
    {
        var lane = new List<Vector2I>();
        Vector2I at = cell;
        while (lane.Count < limit)
        {
            Vector2I next = HexField.Step(at, heading);
            if (next == at || !b.InBounds(next))
                break;
            lane.Add(next);
            at = next;
        }
        return lane;
    }

    /// <summary><see cref="HexField.LaneTo"/>.</summary>
    public static (int Heading, int Range) LaneTo(IHexBoard b, Vector2I from, Vector2I to)
    {
        if (from == to)
            return (-1, 0);
        foreach (int heading in HexField.EdgeHeadings)
        {
            Vector2I at = from;
            for (int range = 1; ; range++)
            {
                Vector2I next = HexField.Step(at, heading);
                if (next == at || !b.InBounds(next))
                    break;
                if (next == to)
                    return (heading, range);
                at = next;
            }
        }
        return (-1, 0);
    }
}
