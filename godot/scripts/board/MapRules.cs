using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// What can be wrong with a board, as a list rather than as a throw.
///
/// <b>This exists because the rules were written three times.</b>
/// <see cref="HexField.SetRelief"/> and <see cref="HexField.SetWater"/> throw on
/// the first offender, <see cref="BoardMap.Audit"/> enumerates them for
/// <c>--map-report</c>, and the self test's authored-boards theme asserts them a
/// third time. Three copies of one rule agree until somebody edits one, and this
/// project has already paid for that once: the audit outlived a guard it was
/// paraphrasing and started calling every beach on every shipped board BAD, in
/// the words of the rule that had been removed.
///
/// <b>A list rather than a throw, because the map editor draws boards that are
/// not maps yet.</b> A board halfway through a brush stroke is illegal almost
/// always - a raised cell beside a ramp, a pond with one corner still dry - and
/// a validator that stops at the first fault cannot paint the rest. The guards
/// keep throwing, and what they throw is the first entry of this list, so the
/// declared board is refused exactly as it was.
///
/// <b>The self test keeps its own hand-written copy, and that is deliberate.</b>
/// It is the same argument the bench board's second copy is kept for: a check
/// that reads the implementation it is judging asserts only that the code agrees
/// with itself. So the shipped maps are judged by statements written out
/// separately, and what this type owes them is a parity check - on a board built
/// to be broken, the validator and the field's guards name the same cell.
///
/// <b>Passability is not re-implemented here.</b> Reachability is the one rule
/// that needs the floor, the flood, the cliffs and the ramp axis together, and
/// <see cref="HexField.Passable"/> is where all four already meet. So
/// <see cref="Reach"/> takes a probe field and asks it, rather than growing a
/// second answer to the most load-bearing question on the board.
/// </summary>
public static class MapRules
{
    /// <summary>
    /// A board as the rules see it: the loose arrays and nothing else.
    ///
    /// <b>Not a <see cref="BoardMap"/>.</b> The editor holds grids it has not
    /// managed to build a map out of yet - that is the whole point of it - and a
    /// validator that could only be handed a constructed map could not be asked
    /// about the one being drawn.
    /// </summary>
    public sealed class Draft
    {
        public string Name = "draft";
        public int Columns;
        public int Rows;

        /// <summary>Height per cell. Never null: a board with no relief is a
        /// board of zeroes, and the rules read the same on it.</summary>
        public int[] Levels = Array.Empty<int>();

        /// <summary>Which cells are marked as carrying a ramp - the map's own
        /// flag, before <see cref="HexField.DeriveRamps"/> works out which edge
        /// is the high one.</summary>
        public bool[] Ramps = Array.Empty<bool>();

        /// <summary>The flood grid.</summary>
        public bool[] Water = Array.Empty<bool>();

        /// <summary>The levels are joined by slopes, not by ramps - the relief
        /// (<see cref="HexField.Slopes"/>, BOT-11). Set, the ramp rules have
        /// nothing to say and <see cref="DropFaults"/> names the steps no hull
        /// drives. The editor sets it always; a built map's draft does not.
        /// </summary>
        public bool Slopes;

        /// <summary>The floor. Read for <see cref="Foundation.Shallow"/> and
        /// <see cref="Foundation.Deep"/> only, so that a map naming its water on
        /// the floor gets the same guards as one handing in a flood mask - which
        /// is exactly what <see cref="HexField.IsWater"/> says about the two
        /// authors of one answer.</summary>
        public Foundation[] Ground = Array.Empty<Foundation>();

        /// <summary>Which rises are declared unclimbable.</summary>
        public bool[] Cliffs = Array.Empty<bool>();

        public IReadOnlySet<Vector2I>? Plot;

        /// <summary>The sea round a moated board (<see cref="BoardMap.Sea"/>):
        /// <see cref="StepFaults"/> does not ask it against the land's water.</summary>
        public IReadOnlySet<Vector2I>? Sea;

        public IReadOnlyList<Vector2I> Homes = Array.Empty<Vector2I>();

        /// <summary>The homes with their sides, for <see cref="ArenaFaults"/>;
        /// empty where only the cells were handed in.</summary>
        public IReadOnlyList<Parking> Parked = Array.Empty<Parking>();

        /// <summary>The zones and the point (<see cref="TankSpriteTest.Arena"/>).</summary>
        public Arena Arena = Arena.Empty;

        /// <summary>What the walled cells carry, for <see cref="WallFaults"/>.
        /// Empty on a board that authored no shapes, which is a board of closed
        /// rings and has nothing for that rule to say.</summary>
        public IReadOnlyDictionary<Vector2I, Masonry> Walling =
            new Dictionary<Vector2I, Masonry>();

        /// <summary>Every cell that carries masonry - a bare letter too, which
        /// <see cref="Walling"/> leaves out - for <see cref="WallReliefFaults"/>.</summary>
        public IReadOnlyCollection<Vector2I> Walled = Array.Empty<Vector2I>();

        public int At(Vector2I cell) => cell.Y * Columns + cell.X;

        public bool On(Vector2I cell) =>
            cell.X >= 0 && cell.X < Columns && cell.Y >= 0 && cell.Y < Rows
            && (Plot is null || Plot.Contains(cell));

        /// <summary>The datum off the board, which is what
        /// <see cref="HexField.LevelAt"/> answers too.</summary>
        public int LevelAt(Vector2I cell) =>
            !On(cell) || At(cell) >= Levels.Length ? 0 : Levels[At(cell)];

        /// <summary>Whether the water on a cell is deep - filled to the brim,
        /// the surface a level over the bed. <see cref="HexField.IsDeep"/>'s
        /// answer: the floor says so and nothing else does.</summary>
        public bool IsDeep(Vector2I cell) =>
            On(cell) && At(cell) < Ground.Length
            && Ground[At(cell)] == Foundation.Deep;

        /// <summary>Never on the relief: the letters may still carry ramps,
        /// and nothing builds them there.</summary>
        public bool IsRamp(Vector2I cell) =>
            !Slopes && On(cell) && At(cell) < Ramps.Length && Ramps[At(cell)];

        public bool IsCliff(Vector2I cell) =>
            On(cell) && At(cell) < Cliffs.Length && Cliffs[At(cell)];

        /// <summary><see cref="HexField.IsWater"/>'s answer: the flood grid or a
        /// floor that is made of water.</summary>
        public bool IsWater(Vector2I cell)
        {
            if (!On(cell))
                return false;
            int at = At(cell);
            return (at < Water.Length && Water[at])
                   || (at < Ground.Length
                       && (Ground[at] == Foundation.Shallow
                           || Ground[at] == Foundation.Deep));
        }

        /// <summary>Every cell of the board in reading order.</summary>
        public IEnumerable<Vector2I> Cells()
        {
            for (int r = 0; r < Rows; r++)
            for (int q = 0; q < Columns; q++)
            {
                var cell = new Vector2I(q, r);
                if (On(cell))
                    yield return cell;
            }
        }

        /// <summary>The draft a built map is, so the audit and the self test can
        /// ask about a board that has already been through the guards.</summary>
        public static Draft Of(BoardMap map) => new()
        {
            Name = map.Name,
            Columns = map.Columns,
            Rows = map.Rows,
            Levels = map.Levels,
            Ramps = map.Ramps,
            Water = map.Water,
            Ground = map.Ground,
            Cliffs = map.Cliffs,
            Plot = map.Plot,
            Sea = map.Sea,
            Homes = map.Homes,
            Parked = map.Parked,
            Arena = map.Arena,
            Walling = map.Walling,
            Walled = map.Walled(),
        };
    }

    /// <summary>
    /// One thing wrong with a board.
    ///
    /// <b><see cref="Rule"/> is a key and <see cref="Text"/> is a sentence.</b>
    /// The editor colours by the key and the report prints the sentence; a
    /// consumer that had to match on prose would break the first time a message
    /// was reworded.
    /// </summary>
    public readonly struct Fault
    {
        public Fault(Vector2I cell, string rule, string text, bool fatal = true,
                     string? brief = null)
        {
            Cell = cell;
            Rule = rule;
            Text = text;
            Fatal = fatal;
            Brief = brief ?? text;
        }

        public Vector2I Cell { get; }

        /// <summary>One of <c>ramp.bridge</c>, <c>ramp.foot</c>,
        /// <c>ramp.rock</c>, <c>ramp.near</c>, <c>water.step</c>,
        /// <c>water.level</c>, <c>home.park</c>, <c>reach.stranded</c>,
        /// <c>reach.rock</c>, <c>wall.run</c>, and the match's ground
        /// (<see cref="ArenaFaults"/>): <c>zone.both</c>, <c>zone.home</c>,
        /// <c>zone.park</c>, <c>zone.point</c>, <c>point.off</c>,
        /// <c>point.dead</c>.</summary>
        public string Rule { get; }

        public string Text { get; }

        /// <summary><c>false</c> for a fault the board is allowed to carry - a
        /// ramp facing the camera is compressed on screen and two of them on
        /// ABBEY are load-bearing. The audit names those rather than refusing
        /// them, and the editor paints them a different colour.</summary>
        public bool Fatal { get; }

        /// <summary>The fault without its sentence around it, for a guard that
        /// lists every offender under one heading of its own. Defaults to
        /// <see cref="Text"/>, so only a rule that has something shorter to say
        /// carries both.</summary>
        public string Brief { get; }

        public override string ToString() => $"({Cell.X},{Cell.Y}) {Text}";
    }

    // --- ramps ---------------------------------------------------------------

    /// <summary>The headings in which a cell has a neighbour exactly one level
    /// above it. <see cref="HexField.DeriveRamps"/>' rule, and the only copy of
    /// it.</summary>
    public static List<int> Above(Draft draft, Vector2I cell) =>
        HexField.EdgeHeadings
            .Where(h => draft.On(HexField.Step(cell, h))
                        && draft.LevelAt(HexField.Step(cell, h))
                           == draft.LevelAt(cell) + 1)
            .ToList();

    /// <summary>
    /// The headings of the neighbours exactly one level BELOW a cell.
    ///
    /// <b>Here only to say where the ramp should have gone.</b> A ramp is never
    /// authored on the high cell - it sits on the low one and climbs out - and
    /// "descending into a hollow does not work" is what that reads as from the
    /// top. Nothing judges a board by this; <see cref="RampFaults"/> puts the
    /// cells it names into the refusal.
    /// </summary>
    public static List<int> Below(Draft draft, Vector2I cell) =>
        HexField.EdgeHeadings
            .Where(h => draft.On(HexField.Step(cell, h))
                        && draft.LevelAt(HexField.Step(cell, h))
                           == draft.LevelAt(cell) - 1)
            .ToList();

    /// <summary>
    /// Whether a ramp bridging <paramref name="high"/> has something to stand on
    /// at its low end.
    ///
    /// <b>The check the field does not make.</b> A ramp is entered and left along
    /// its axis only and the level it reaches at the low end is its own, so a
    /// ramp whose low axis neighbour is off the board, on another level or itself
    /// a ramp can only be entered from the top and left the same way - a slope
    /// drawn on the hillside leading back to the hillside. Nothing throws and
    /// nothing on screen says so.
    /// </summary>
    public static bool Footed(Draft draft, Vector2I cell, int high)
    {
        Vector2I foot = HexField.Step(cell, HexField.Reverse(high));
        return draft.On(foot) && !draft.IsRamp(foot)
               && draft.LevelAt(foot) == draft.LevelAt(cell);
    }

    /// <summary>
    /// Every cell that could carry a ramp, and which way it would tilt.
    ///
    /// This is what makes authoring a board a pick off a list rather than a
    /// round trip per ramp: the guard throws on the first offender and says
    /// nothing about the rest, which is nine goes on a board the size of ABBEY.
    /// </summary>
    public static Dictionary<Vector2I, int> Sites(Draft draft)
    {
        var legal = new Dictionary<Vector2I, int>();
        if (draft.Slopes)
            return legal;
        foreach (Vector2I cell in draft.Cells())
        {
            if (draft.IsCliff(cell))
                continue;
            List<int> up = Above(draft, cell);
            // A site whose high edge is a rock is not a site: taking it would
            // hand away the one thing a rock is.
            if (up.Count == 1 && !draft.IsCliff(HexField.Step(cell, up[0]))
                && Footed(draft, cell, up[0]))
                legal[cell] = up[0];
        }
        return legal;
    }

    /// <summary>
    /// What is wrong with the ramps, in reading order.
    ///
    /// The first entry is what <see cref="HexField.SetRelief"/> throws, word for
    /// word, so the declared board is refused exactly as it was.
    /// </summary>
    public static List<Fault> RampFaults(Draft draft)
    {
        var faults = new List<Fault>();
        // on the relief the ramps the letters carry are not built at all
        if (draft.Slopes)
            return faults;
        for (int r = 0; r < draft.Rows; r++)
        for (int q = 0; q < draft.Columns; q++)
        {
            var cell = new Vector2I(q, r);
            if (draft.At(cell) >= draft.Ramps.Length || !draft.Ramps[draft.At(cell)])
                continue;

            // A ramp off the board is the map disagreeing with itself, and it
            // reads as a ramp that simply does not work rather than as one that
            // was refused.
            if (!draft.On(cell))
            {
                faults.Add(new Fault(cell, "ramp.bridge",
                    $"the ramp at ({q},{r}) is off the board"));
                continue;
            }
            if (draft.IsCliff(cell))
            {
                faults.Add(new Fault(cell, "ramp.rock",
                    $"the ramp at ({q},{r}) is on a rock, which is the one rise "
                    + "that is not meant to be driven"));
                continue;
            }

            List<int> up = Above(draft, cell);
            if (up.Count != 1)
            {
                // What to do about it, not only what is wrong - because both
                // shapes of this fault have one likely cause and it is not the
                // one the message used to suggest. See Below.
                List<int> down = Below(draft, cell);
                string fix = up.Count == 0
                    ? down.Count > 0
                        ? " - a ramp belongs to the LOW cell of a step and "
                          + "climbs out of it, so it goes on ("
                          + string.Join(") or (", down.Select(h =>
                              {
                                  Vector2I low = HexField.Step(cell, h);
                                  return $"{low.X},{low.Y}";
                              }))
                          + ") and rises back to here"
                        : " - nothing beside it is a step at all"
                    : " - a ramp is one plane tilted about one edge, so the low "
                      + "ground has to meet the high on a single edge: widen it, "
                      + "or put the ramp where only one of its edges is the rim. "
                      + "A one-cell hollow has six and can never carry one";
                faults.Add(new Fault(cell, "ramp.bridge",
                    $"the ramp at ({q},{r}) on level {draft.LevelAt(cell)} has "
                    + (up.Count == 0
                        ? "no neighbour a level above it, so there is nothing "
                          + "for it to bridge"
                        : $"{up.Count} neighbours a level above it - headings "
                          + string.Join(", ", up)
                          + " - so which edge is its high one is not decided by "
                          + "the map")
                    + fix));
                continue;
            }

            Vector2I high = HexField.Step(cell, up[0]);
            if (draft.IsCliff(high))
                faults.Add(new Fault(cell, "ramp.rock",
                    $"the ramp at ({q},{r}) climbs on to the rock at "
                    + $"({high.X},{high.Y}), which hands away the one thing a "
                    + "rock is"));

            if (!Footed(draft, cell, up[0]))
            {
                Vector2I foot = HexField.Step(cell, HexField.Reverse(up[0]));
                faults.Add(new Fault(cell, "ramp.foot",
                    $"the ramp at ({q},{r}) bridges {up[0]} but its foot "
                    + $"({foot.X},{foot.Y}) is "
                    + (!draft.On(foot)
                        ? "off the board"
                        : draft.IsRamp(foot)
                            ? "another ramp"
                            : $"on level {draft.LevelAt(foot)} and this ramp is "
                              + $"on {draft.LevelAt(cell)}")
                    + " - so it can only be entered from the top and left the "
                    + "same way"));
            }

            // Named and allowed. A face rising at the viewer projects to about
            // half the footprint of one going away, so a near-facing ramp reads
            // as the rise simply being a cell bigger - and a slope that reads
            // poorly still beats a hill nothing can climb.
            if (HexField.NearHeading.Contains(up[0]))
                faults.Add(new Fault(cell, "ramp.near",
                    $"the ramp at ({q},{r}) faces the camera at {up[0]}, so it "
                    + "is compressed on screen", false));
        }
        return faults;
    }

    /// <summary>
    /// The steps on the relief that no hull drives: a neighbour more than
    /// <see cref="HexField.MaxClimb"/> levels down, neither of the two a rock.
    ///
    /// <b>Named, never fatal</b>, like <c>ramp.near</c>: a bluff over a hollow
    /// is a thing an author may want, and a tank still goes down it - thrown off
    /// by a ram (<see cref="HexField.Drops"/>). What the author gets is being
    /// told that the edge is a wall from below and a fall from above, which on
    /// the slopes of the relief does not show. Once per pair, on the high cell.
    /// </summary>
    public static List<Fault> DropFaults(Draft draft, int climb = 1)
    {
        var faults = new List<Fault>();
        if (!draft.Slopes)
            return faults;
        foreach (Vector2I cell in draft.Cells())
        {
            if (draft.IsCliff(cell))
                continue;
            foreach (int h in HexField.EdgeHeadings)
            {
                Vector2I low = HexField.Step(cell, h);
                if (!draft.On(low) || draft.IsCliff(low)
                    || draft.LevelAt(cell) - draft.LevelAt(low) <= climb)
                    continue;
                faults.Add(new Fault(cell, "slope.drop",
                    $"({cell.X},{cell.Y}) stands more than a level over "
                    + $"({low.X},{low.Y}): no tank drives that edge, up or down",
                    false,
                    $"({cell.X},{cell.Y}) over ({low.X},{low.Y})"));
            }
        }
        return faults;
    }

    // --- water ---------------------------------------------------------------

    /// <summary>
    /// One body of water is one surface.
    ///
    /// Two water cells side by side on two levels is a waterfall, which is a
    /// different thing entirely and is not what the board draws. Asked of
    /// neighbours rather than of the whole board, because two unconnected ponds
    /// at two levels are perfectly reasonable.
    /// </summary>
    public static List<Fault> StepFaults(Draft draft)
    {
        var faults = new List<Fault>();
        foreach (Vector2I cell in draft.Cells())
        {
            if (!draft.IsWater(cell))
                continue;
            foreach (int heading in HexField.EdgeHeadings)
            {
                Vector2I next = HexField.Step(cell, heading);
                if (!draft.IsWater(next))
                    continue;
                if (!Shores(draft, cell, next))
                    continue;
                // A ford beside deep water on one level is no step: the body
                // takes the deep water's surface and the ford's bed rises under
                // it (Brimmed), so the one surface is one by construction.
                if (draft.LevelAt(next) == draft.LevelAt(cell))
                    continue;
                faults.Add(new Fault(cell, "water.step",
                    $"({cell.X},{cell.Y}) on {draft.LevelAt(cell)} is water "
                    + $"beside ({next.X},{next.Y}) on {draft.LevelAt(next)}, "
                    + "and one body of water is one surface", true,
                    $"({cell.X},{cell.Y}) on {draft.LevelAt(cell)} beside "
                    + $"({next.X},{next.Y}) on {draft.LevelAt(next)}"));
            }
        }
        return faults;
    }

    /// <summary>Whether two water cells side by side are asked as one body:
    /// the sea round a moated board meets a river with a step
    /// (<see cref="BoardMap.Moated"/>), so a sea cell and a land one never
    /// are.</summary>
    private static bool Shores(Draft draft, Vector2I cell, Vector2I next) =>
        draft.Sea is not { } sea || sea.Contains(cell) == sea.Contains(next);

    /// <summary>
    /// Which water cells stand at the deep water's surface: every cell of a body
    /// that has deep water in it.
    ///
    /// <b>The surface belongs to the body and the depth to the bed</b>
    /// (2026-10-26, docs/editor3d-plan.md, «Модель воды»). A body is the water
    /// cells joined across edges on one level - the cells
    /// <see cref="StepFaults"/> asks as one surface - and one with any deep cell
    /// in it is filled to the deep water's height, its fords a shelf under
    /// it (<see cref="HexField.BedAt"/>). A body with no deep cell keeps the
    /// ford's own surface, so every pond and river drawn before this reads as it
    /// did.
    /// </summary>
    public static bool[] Brimmed(Draft draft)
    {
        var brim = new bool[draft.Columns * draft.Rows];
        var queue = new Queue<Vector2I>();
        foreach (Vector2I cell in draft.Cells())
            if (draft.IsDeep(cell) && draft.IsWater(cell))
            {
                brim[draft.At(cell)] = true;
                queue.Enqueue(cell);
            }
        while (queue.Count > 0)
        {
            Vector2I at = queue.Dequeue();
            foreach (int h in HexField.EdgeHeadings)
            {
                Vector2I next = HexField.Step(at, h);
                if (!draft.IsWater(next) || brim[draft.At(next)]
                    || draft.LevelAt(next) != draft.LevelAt(at)
                    || !Shores(draft, at, next))
                    continue;
                brim[draft.At(next)] = true;
                queue.Enqueue(next);
            }
        }
        return brim;
    }

    /// <summary>
    /// The dry cells that stand at a water surface's own height beside it, and
    /// are therefore under it.
    ///
    /// <b>Water finds its level, and a dry hole in a lake is a wall of water
    /// with nothing holding it up.</b> The same sentence as the waterfall guard
    /// from the other end: that one forbids two water cells at two levels, this
    /// one forbids one level being water and dry land at once.
    ///
    /// <b>A ramp stops it, and that is the whole of the exemption.</b> It has no
    /// one surface height, so it holds nothing and dams instead - which is what a
    /// shore driven down into a lake is. Evidence the exemption was not carved to
    /// fit the map it was written for: the bench board, authored long before any
    /// of this, passes with exactly one exception and that exception is its ford.
    ///
    /// <b>The closure, not the way in.</b> The guard names the pairs where the
    /// water gets in; the water then keeps going, so the list of cells to redraw
    /// is longer than the list of cells that are wrong.
    /// </summary>
    public static List<Vector2I> Flood(Draft draft)
    {
        var wet = new HashSet<Vector2I>();
        var queue = new Queue<Vector2I>();
        foreach (Vector2I cell in draft.Cells())
        {
            if (!draft.IsWater(cell))
                continue;
            wet.Add(cell);
            queue.Enqueue(cell);
        }

        var found = new List<Vector2I>();
        while (queue.Count > 0)
        {
            Vector2I at = queue.Dequeue();
            foreach (int h in HexField.EdgeHeadings)
            {
                Vector2I next = HexField.Step(at, h);
                if (!draft.On(next) || wet.Contains(next) || draft.IsRamp(next)
                    || draft.LevelAt(next) != draft.LevelAt(at))
                    continue;
                wet.Add(next);
                found.Add(next);
                queue.Enqueue(next);
            }
        }
        found.Sort((a, b) => a.Y != b.Y ? a.Y - b.Y : a.X - b.X);
        return found;
    }

    /// <summary>The waterfall guard and the sea level rule, in that order.
    /// </summary>
    public static List<Fault> WaterFaults(Draft draft)
    {
        List<Fault> faults = StepFaults(draft);
        // Named and allowed on the relief: its ground is one smooth surface, and
        // a dry cell at the water's level beside it is drawn as the shore
        // shelving under it (Relief.Connect keeps it dry past the bank) - a
        // board the bench plays, where the prisms drew a wall of water. The
        // old ramp out of a pond is exactly this cell once its ramp is gone.
        foreach (Vector2I cell in Flood(draft))
            faults.Add(new Fault(cell, "water.level",
                $"({cell.X},{cell.Y}) stands at a water surface's own height "
                + "beside it, so it is under it", !draft.Slopes));
        return faults;
    }

    // --- homes and reach -----------------------------------------------------

    /// <summary>
    /// Whether each home is somewhere a tank can park.
    ///
    /// <b>Not "on the datum"</b>, which was the first draft of this and was
    /// wrong about a board it had no business judging: the bench parks its light
    /// tank on the rosette's hill on purpose, a level up. What a home has to be
    /// is somewhere with one surface height to stand at and a way off it.
    /// </summary>
    public static List<Fault> HomeFaults(Draft draft)
    {
        var faults = new List<Fault>();
        foreach (Vector2I home in draft.Homes)
        {
            string why =
                !draft.On(home) ? "off the board"
                : draft.IsCliff(home) ? "on a rock"
                : draft.IsRamp(home) ? "on a ramp"
                : draft.IsWater(home) ? "in the water"
                : "";
            if (why.Length > 0)
                faults.Add(new Fault(home, "home.park",
                    $"the home at ({home.X},{home.Y}) is {why}, which is not "
                    + "somewhere a tank can park"));
        }
        return faults;
    }

    /// <summary>
    /// Everything a tank can drive to from a cell, asked of a field.
    ///
    /// The probe carries the relief, the ramps and the flood; what it answers is
    /// <see cref="HexField.Passable"/>, which is not re-implemented here for the
    /// reason the type's note gives.
    /// </summary>
    public static HashSet<Vector2I> Seen(HexField probe, Vector2I from)
    {
        var seen = new HashSet<Vector2I> { from };
        var queue = new Queue<Vector2I>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            Vector2I at = queue.Dequeue();
            foreach (int h in HexField.EdgeHeadings)
                if (probe.Passable(at, h) && seen.Add(HexField.Step(at, h)))
                    queue.Enqueue(HexField.Step(at, h));
        }
        return seen;
    }

    /// <summary>
    /// What is cut off, and what should have been.
    ///
    /// <b>Both halves, and it is only a question because the rock is
    /// declared.</b> A valley cut off by accident and a rise meant to be
    /// unclimbable are the same picture and the same arithmetic; the declared
    /// cliff is what tells them apart. A map where everything is reachable has no
    /// rocks in it, and one where the rocks can be climbed has no cliffs.
    /// </summary>
    public static List<Fault> ReachFaults(Draft draft, HexField probe)
    {
        var faults = new List<Fault>();
        foreach (Vector2I home in draft.Homes)
        {
            if (!draft.On(home))
                continue;
            HashSet<Vector2I> seen = Seen(probe, home);
            foreach (Vector2I cell in draft.Cells())
            {
                if (draft.IsCliff(cell))
                {
                    if (seen.Contains(cell))
                        faults.Add(new Fault(cell, "reach.rock",
                            $"({cell.X},{cell.Y}) is a declared rock and can be "
                            + $"driven to from the home at ({home.X},{home.Y})"));
                }
                else if (draft.IsDeep(cell))
                {
                    // Deep water is dropped into off any bank cell and left by a
                    // ramp alone (HexField.Passable), so reaching it is no fault -
                    // and neither is not reaching it: a pond no home can get to is
                    // a pond, not a stranded valley. A pond with no ramp out is
                    // not a fault either; the rules allow exactly that trap, see
                    // GDD contradictions.md C-03.
                }
                else if (!seen.Contains(cell))
                {
                    faults.Add(new Fault(cell, "reach.stranded",
                        $"({cell.X},{cell.Y}) cannot be reached from the home "
                        + $"at ({home.X},{home.Y})"));
                }
            }
        }
        return faults;
    }

    // --- masonry -------------------------------------------------------------

    /// <summary>
    /// Whether each walled cell is one wall.
    ///
    /// <b>Never fatal, and that is the difference between this and every other
    /// rule here.</b> The rest name a board that cannot be built - a ramp on to
    /// nothing, water falling downhill - and this names one that builds
    /// perfectly and lays two walls where the author drew one. A cell whose
    /// edges are two runs is a shape <see cref="WallKit"/> cannot fold into a
    /// chain, so whoever stands the props gets to decide what to do about it;
    /// what the author gets is being told, on the stroke, that the far edge just
    /// clicked is not joined to the near one.
    ///
    /// <b>An empty mask is not in here</b>, because it cannot be reached:
    /// <see cref="MapEdit.Edge"/> refuses the last side and
    /// <see cref="MapFile"/> refuses an empty list, both naming the same reason
    /// - masonry standing on nothing is a breach rather than a wall.
    /// </summary>
    public static List<Fault> WallFaults(Draft draft)
    {
        var faults = new List<Fault>();
        foreach ((Vector2I cell, Masonry wall) in draft.Walling
                     .OrderBy(kv => kv.Key.Y).ThenBy(kv => kv.Key.X))
        {
            if (!draft.On(cell) || wall.Run() is not null)
                continue;
            faults.Add(new Fault(cell, "wall.run",
                $"the masonry at ({cell.X},{cell.Y}) stands on "
                + string.Join(", ", wall.Standing())
                + ", which is two runs - a wall is one folded chain of bricks, "
                + "so this is two walls on one cell", false,
                $"({cell.X},{cell.Y}) is two runs of wall"));
        }
        return faults;
    }

    /// <summary>
    /// <c>wall.relief</c>, fatal: masonry stands on its level or above the
    /// ground round it, never under it. A walled cell is a pad flat at its
    /// level (<see cref="Relief.Pad"/>); a higher land neighbour brings its
    /// slope down on to the wall's back, and two walled cells side by side on
    /// different levels leave the slope nowhere to go - a sheer step. A
    /// fortified height stays: lower neighbours are what a pad's shoulder is
    /// for. Water and rock are not ground the rule asks about: a pond's bank
    /// and a crag's foot are their own. The user's call, 2026-11-02 (BOT-12,
    /// option B), which replaces the step allowed as a cliff (В-3).
    /// </summary>
    public static List<Fault> WallReliefFaults(Draft draft)
    {
        var faults = new List<Fault>();
        foreach (Vector2I cell in draft.Walled.OrderBy(c => c.Y).ThenBy(c => c.X))
        {
            if (!draft.On(cell))
                continue;
            var over = new List<string>();
            foreach (int h in HexField.EdgeHeadings)
            {
                Vector2I next = HexField.Step(cell, h);
                if (!draft.On(next) || draft.IsWater(next) || draft.IsCliff(next))
                    continue;
                if (draft.LevelAt(next) > draft.LevelAt(cell))
                    over.Add($"({next.X},{next.Y})");
            }
            if (over.Count == 0)
                continue;
            faults.Add(new Fault(cell, "wall.relief",
                $"the masonry at ({cell.X},{cell.Y}) stands under higher ground at "
                + string.Join(", ", over) + " - a wall stands on its level or "
                + "above the ground round it, never under a slope or a step",
                true, $"({cell.X},{cell.Y}) masonry under a slope"));
        }
        return faults;
    }

    // --- the whole board -----------------------------------------------------

    /// <summary>
    /// Everything the rules can say about a draft without a field: the ramps,
    /// the water and the homes.
    ///
    /// <b>Reachability is not in here</b>, because it needs a probe carrying the
    /// relief that only builds once the ramps are legal - so asking it of a board
    /// with a broken ramp is asking about a board that could not be built. The
    /// editor runs this on every stroke and <see cref="ReachFaults"/> only when
    /// this comes back clean, which is also the order the audit prints in.
    /// </summary>
    public static List<Fault> Faults(Draft draft)
    {
        var faults = new List<Fault>();
        faults.AddRange(RampFaults(draft));
        faults.AddRange(DropFaults(draft));
        faults.AddRange(WaterFaults(draft));
        faults.AddRange(HomeFaults(draft));
        faults.AddRange(WallFaults(draft));
        faults.AddRange(WallReliefFaults(draft));
        faults.AddRange(ArenaFaults(draft));
        return faults;
    }

    // --- the match's ground ----------------------------------------------------

    /// <summary>Why a tank could not be set down on a cell, or empty: the
    /// homes' rule (<see cref="HomeFaults"/>).</summary>
    private static string Unparkable(Draft draft, Vector2I cell) =>
        !draft.On(cell) ? "off the board"
        : draft.IsCliff(cell) ? "on a rock"
        : draft.IsRamp(cell) ? "on a ramp"
        : draft.IsWater(cell) ? "in the water"
        : "";

    /// <summary>
    /// The deployment zones and the control point (GDD match.md, Д9):
    /// <list type="bullet">
    /// <item><c>zone.both</c> - a cell in both sides' zones: whose is it?</item>
    /// <item><c>zone.home</c> - a home outside its side's zone, when the side
    /// has one: the row the match starts in (match.md, «в ряд») stands in the
    /// zone.</item>
    /// <item><c>zone.park</c>, named, not refused - a zone cell no tank can be
    /// set down on: the placement skips it, and a broad brush over a pond is
    /// not a broken board.</item>
    /// <item><c>point.off</c> - a hex of the point off the board;
    /// <c>point.dead</c> - a point with no hex a tank can stand on: nobody
    /// ever holds it, and both sides only bleed.</item>
    /// <item><c>zone.point</c>, named - a zone on the point: that side holds it
    /// from the first tick.</item>
    /// </list>
    /// </summary>
    public static List<Fault> ArenaFaults(Draft draft)
    {
        var faults = new List<Fault>();
        Arena arena = draft.Arena;
        if (arena.Plain)
            return faults;
        foreach (Vector2I c in arena.Zone(Parking.Green))
            if (arena.Zone(Parking.Brown).Contains(c))
                faults.Add(new Fault(c, "zone.both",
                    $"({c.X},{c.Y}) is in both zones, and a cell deploys one side",
                    brief: $"({c.X},{c.Y}) in both zones"));
        foreach (string side in Parking.Sides)
            foreach (Vector2I c in arena.Zone(side))
            {
                string why = Unparkable(draft, c);
                if (why.Length > 0)
                    faults.Add(new Fault(c, "zone.park",
                        $"({c.X},{c.Y}) of the {side} zone is {why}: no tank will be set down there",
                        false, $"({c.X},{c.Y}) of the {side} zone is {why}"));
                if (arena.Point is { } p && p.Holds(c))
                    faults.Add(new Fault(c, "zone.point",
                        $"({c.X},{c.Y}) of the {side} zone is on the control point: {side} hold it from the first round",
                        false, $"({c.X},{c.Y}) of the {side} zone on the point"));
            }
        string[] sides = Parking.SidesOf(draft.Parked);
        for (int i = 0; i < draft.Parked.Count; i++)
        {
            Vector2I home = draft.Parked[i].Cell;
            IReadOnlyList<Vector2I> zone = arena.Zone(sides[i]);
            if (zone.Count > 0 && !zone.Contains(home))
                faults.Add(new Fault(home, "zone.home",
                    $"the {sides[i]} home at ({home.X},{home.Y}) is outside the {sides[i]} zone, "
                    + "and the match starts with a side's row in its zone",
                    brief: $"{sides[i]} home ({home.X},{home.Y}) outside its zone"));
        }
        if (arena.Point is { } point)
        {
            bool any = false;
            foreach (Vector2I c in point.Cells())
            {
                if (!draft.On(c))
                    faults.Add(new Fault(c, "point.off",
                        $"({c.X},{c.Y}) of the control point is off the board",
                        brief: $"point ({c.X},{c.Y}) off the board"));
                else
                    any |= Unparkable(draft, c).Length == 0;
            }
            if (!any)
                faults.Add(new Fault(point.Cell, "point.dead",
                    $"no hex of the control point at ({point.Cell.X},{point.Cell.Y}) takes a tank, "
                    + "so nobody ever holds it and both sides only lose points",
                    brief: $"point ({point.Cell.X},{point.Cell.Y}) holds no tank"));
        }
        return faults;
    }

    /// <summary>The faults that refuse a board, as against the ones that are
    /// merely named.</summary>
    public static IEnumerable<Fault> Fatal(IEnumerable<Fault> faults) =>
        faults.Where(f => f.Fatal);
}
