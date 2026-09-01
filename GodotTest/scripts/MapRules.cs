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

        /// <summary>The floor. Read for <see cref="Foundation.Shallow"/> and
        /// <see cref="Foundation.Deep"/> only, so that a map naming its water on
        /// the floor gets the same guards as one handing in a flood mask - which
        /// is exactly what <see cref="HexField.IsWater"/> says about the two
        /// authors of one answer.</summary>
        public Foundation[] Ground = Array.Empty<Foundation>();

        /// <summary>Which rises are declared unclimbable.</summary>
        public bool[] Cliffs = Array.Empty<bool>();

        public IReadOnlySet<Vector2I>? Plot;
        public IReadOnlyList<Vector2I> Homes = Array.Empty<Vector2I>();

        /// <summary>What the walled cells carry, for <see cref="WallFaults"/>.
        /// Empty on a board that authored no shapes, which is a board of closed
        /// rings and has nothing for that rule to say.</summary>
        public IReadOnlyDictionary<Vector2I, Masonry> Walling =
            new Dictionary<Vector2I, Masonry>();

        public int At(Vector2I cell) => cell.Y * Columns + cell.X;

        public bool On(Vector2I cell) =>
            cell.X >= 0 && cell.X < Columns && cell.Y >= 0 && cell.Y < Rows
            && (Plot is null || Plot.Contains(cell));

        /// <summary>The datum off the board, which is what
        /// <see cref="HexField.LevelAt"/> answers too.</summary>
        public int LevelAt(Vector2I cell) =>
            !On(cell) || At(cell) >= Levels.Length ? 0 : Levels[At(cell)];

        public bool IsRamp(Vector2I cell) =>
            On(cell) && At(cell) < Ramps.Length && Ramps[At(cell)];

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
            Homes = map.Homes,
            Walling = map.Walling,
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
        /// <c>reach.rock</c>.</summary>
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
                if (!draft.IsWater(next) || draft.LevelAt(next) == draft.LevelAt(cell))
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
        foreach (Vector2I cell in Flood(draft))
            faults.Add(new Fault(cell, "water.level",
                $"({cell.X},{cell.Y}) stands at a water surface's own height "
                + "beside it, so it is under it"));
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
        faults.AddRange(WaterFaults(draft));
        faults.AddRange(HomeFaults(draft));
        faults.AddRange(WallFaults(draft));
        return faults;
    }

    /// <summary>The faults that refuse a board, as against the ones that are
    /// merely named.</summary>
    public static IEnumerable<Fault> Fatal(IEnumerable<Fault> faults) =>
        faults.Where(f => f.Fatal);
}
