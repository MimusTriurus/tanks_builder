using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The ABBEY board, and the audit that says whether an authored map is a map.
///
/// <b>A transcription of the topology, not a scale model, and the numbers force
/// that.</b> A cell of this board is about seven metres across - see
/// <c>WallRig.MetresPerCell</c>, which derives 4.07m from the width of a hull -
/// and the map it is copying is a kilometre square. A hundred and forty two
/// cells to a side is not a board, it is a different problem. So a cell here is
/// about fifty metres and the tank is drawn some nine times oversize against it,
/// which is the same untruth the minimap it was copied from tells.
///
/// What sets the size is therefore how many zones have to stay distinguishable,
/// not metres. Reading the minimap there are seven or eight bands each way -
/// west cliffs, massif, lake, the road, the monastery, the east field, the
/// river - so twenty columns gives each of them two cells.
///
/// <b>Twenty by seventeen is square on the ground and not on the screen.</b> The
/// squash belongs to the camera, so the board that is square where the tanks
/// drive has <c>Rows = 0.866 * Columns</c>: the column step is 1.5R and the row
/// step is sqrt(3)R. On screen it comes out twice as wide as it is tall, which
/// is what an isometric board looks like.
/// </summary>
public sealed partial class BoardMap
{
    /// <summary>
    /// What each cell is.
    ///
    /// <b>The ramp graph is the map, and it is designed rather than copied.</b>
    /// With ramps on the board every change of level goes through one, a ramp
    /// wants exactly one neighbour a level above it, and two adjacent ramps are
    /// refused - so where a rise can be mounted is a small puzzle per approach
    /// rather than a property of the outline. That is not a cost: a hill with two
    /// named ways up is what makes a position matter, and it is what the map it
    /// is copied from is actually made of.
    ///
    /// <b>The monastery is plain, because there is no town kind and inventing one
    /// here would be inventing a mechanism to hold data.</b> Stone walls on those
    /// cells already exist and are generated in code - see <see cref="WallKit"/>
    /// and <c>Wall.tscn</c> - so the village is a later pass over the same cells,
    /// not a seventh letter.
    ///
    /// <b>No roads either.</b> There is no kind for one and none was asked for;
    /// what the road network on the original does - deciding where you may go -
    /// is done here by the rocks and the ramps.
    /// </summary>
    private static readonly string[] AbbeyGround =
    {
        //         1111111111
        // 0123456789012345678 9
        "---...fff...fff..---", // r0   north edge
        "--11..fff...fff##.w-", // r1
        "-#11..fff.#....##.w-", // r2
        "-#11.....f.......ww-", // r3
        "-#1F....#........ww-", // r4
        "-##.........#..11ww-", // r5
        "-###.w........#11ww-", // r6   the lake basin opens
        "-##www.........11ww-", // r7   (4,7) the beach, and it is under water
        "-###ww.......f.11ww-", // r8
        "-###wwf......f.11ww-", // r9
        "-#11wwf.........www-", // r10  the river, widened to its own level
        "-#11ww......#..wwww-", // r11  (16,11) the ford, and it is a wet step now
        "-#11ww.#..vv...wwww-", // r12  (4,12) the lake mouth, under water too
        "-#11.fff.vvv.....ww-", // r13  the dry hollow south of the monastery
        "-#...fff..vv..##..w-", // r14
        "--#..fff.#..ff##..w-", // r15
        "---..............---", // r16  south edge, the home line
    };

    /// <summary>
    /// Which cells carry a ramp.
    ///
    /// <b>Every one of these was chosen off the audit's list of legal sites, not
    /// placed by eye.</b> The guard in <see cref="HexField.SetRelief"/> throws on
    /// the first offender and says nothing about the rest, so hand-placing eight
    /// ramps on a board this size is eight round trips; <see cref="Audit"/>
    /// enumerates every cell that could carry one and which way it would tilt,
    /// and the map picks from that.
    ///
    /// <b>High edges point away from the camera wherever there was a choice.</b>
    /// A face rising at the viewer projects to sin(e) - grade*cos(e) of its own
    /// footprint against sin(e) + grade*cos(e) going away, so a near-facing ramp
    /// is compressed to about half the height of a far-facing one and reads as
    /// the rise simply being a cell bigger. The audit names the ones that face
    /// the camera rather than refusing them - two of them are load-bearing, and a
    /// slope that reads poorly beats a hill nothing can climb.
    /// </summary>
    private static readonly string[] AbbeyRamps =
    {
        //         1111111111
        // 0123456789012345678 9
        "....................", // r0
        "....r...............", // r1   the north road up the west shelf
        "....................", // r2
        "....................", // r3
        "................r...", // r4   the north approach to the east rise
        "....r...............", // r5   the south road up the west shelf
        "....................", // r6
        "....r...............", // r7   down into the lake's north cove
        "....................", // r8
        "....................", // r9
        "..............r.....", // r10  the south approach to the east rise
        "................r...", // r11  the ford, off the mud flat
        "....r...............", // r12  the lake's south beach
        "..........r.........", // r13  into the hollow
        "....r...............", // r14  up the south-west shelf
        "....................", // r15
        "....................", // r16
    };

    /// <summary>Where the three park, in the south. Abreast on one row and at
    /// one height, which is the comparison the class sizes are judged by - see
    /// <c>MovementProfile.Size</c>. The north edge is where an opposing set
    /// would go; nothing here has two sides yet.</summary>
    private static readonly Vector2I[] AbbeyHomes =
        { new(8, 16), new(10, 16), new(12, 16) };

    // --- the audit -----------------------------------------------------------

    /// <summary>
    /// Whether a ramp bridging <paramref name="high"/> has something to stand on
    /// at its low end.
    ///
    /// <b>The check the field does not make.</b> <c>DeriveRamps</c> settles which
    /// edge is high and stops there; <see cref="HexField.Passable"/> then lets a
    /// ramp be entered along its axis only, and the level it reaches at the low
    /// end is the ramp's own. So a ramp whose low axis neighbour is off the
    /// board, on another level, or itself a ramp is enterable from the top and
    /// nowhere else - a slope drawn on the hillside that leads back to the
    /// hillside. Nothing throws and nothing on screen says so.
    /// </summary>
    private bool Foot(HexField probe, Vector2I cell, int high)
    {
        Vector2I foot = HexField.Step(cell, HexField.Reverse(high));
        return probe.InBounds(foot)
               && !Ramps[At(foot)]
               && probe.LevelAt(foot) == probe.LevelAt(cell);
    }

    /// <summary>
    /// Everything that can be wrong with an authored map, in one report.
    ///
    /// <b>It runs on a probe field rather than the live one.</b> None of what it
    /// asks needs the tile - levels, ramp headings, the flood guards and
    /// reachability are all grid arithmetic - so a bare
    /// <see cref="HexField"/> answers them, and the board the session is looking
    /// at is not disturbed to ask about a board it may not even be showing.
    ///
    /// <b>Reachability is the check that catches a broken map, and it is only
    /// possible because the rock is declared.</b> A valley cut off by accident
    /// and a rise meant to be unclimbable are the same picture on screen and the
    /// same arithmetic underneath; <see cref="Rock"/> is what tells them apart.
    /// </summary>
    public IReadOnlyList<string> Audit()
    {
        var lines = new List<string>();
        var probe = new HexField { Columns = Columns, Rows = Rows, Plot = Plot };
        try
        {
            lines.Add($"map {Name}: {Columns} x {Rows} = {Columns * Rows} cells, "
                      + string.Join(", ", Tally().Select(t => $"{t.Count} {t.What}")));

            // Heights first and ramps second, so the ramp derivation is asked
            // about a board that already has its levels - the same order the
            // field itself demands of water.
            probe.SetRelief(Levels);

            // Every cell that could carry a ramp, and which way it would tilt.
            //
            // The first half is DeriveRamps' rule and not a paraphrase of it:
            // exactly one in-bounds neighbour a level above.
            //
            // <b>The second half is the one the field does not check, and a ramp
            // that fails it is a dead end that nothing reports.</b> A ramp is
            // entered and left along its axis only, so besides the high
            // neighbour it needs its <i>low</i> axis neighbour to be on the
            // board, at the ramp's own level, and not itself a ramp - otherwise
            // the only way on to it is down from the top, and the only way off
            // is back up. Found by building one: a ramp on to the east rise had
            // the river's low bank below it, and every number about it was
            // right.
            //
            // <b>Water is not skipped, and it used to be.</b> SetWater refused a
            // ramp under a pond - a slope has no one surface height for water to
            // stand at - so listing a flooded cell as a site offered something
            // that could not be taken. That refusal is gone: the pond's plane
            // crosses the slope instead of needing a height off it, and the
            // depth buffer cuts the water where the two planes meet. So a
            // flooded cell is a site like any other, and what taking it makes is
            // a beach - dry at the head, under water over the back of its run.
            //
            // Left in, the skip did not go quiet: it called every beach on every
            // shipped board BAD, in the words of the guard it outlived.
            var legal = new Dictionary<Vector2I, int>();
            for (int r = 0; r < Rows; r++)
            for (int q = 0; q < Columns; q++)
            {
                var cell = new Vector2I(q, r);
                if (!OnBoard(cell) || Cliffs[At(cell)])
                    continue;
                var up = HexField.EdgeHeadings
                    .Where(h => probe.InBounds(HexField.Step(cell, h))
                                && probe.LevelAt(HexField.Step(cell, h))
                                   == probe.LevelAt(cell) + 1)
                    .ToList();
                // A site whose high edge is a rock is not a site: it would
                // hand away the one thing a rock is.
                if (up.Count == 1 && !IsCliff(HexField.Step(cell, up[0]))
                    && Foot(probe, cell, up[0]))
                    legal[cell] = up[0];
            }

            var declared = new List<Vector2I>();
            for (int r = 0; r < Rows; r++)
            for (int q = 0; q < Columns; q++)
                if (Ramps[r * Columns + q])
                    declared.Add(new Vector2I(q, r));

            lines.Add($"ramps: {declared.Count} declared, "
                      + $"{legal.Count} cells could carry one");

            // A ramp that is not a legal site, and why. Named rather than left
            // to SetRelief, which throws on the first one and says nothing about
            // the others - which is eight round trips on a board this size.
            foreach (Vector2I cell in declared.Where(c => !legal.ContainsKey(c)))
            {
                var up = HexField.EdgeHeadings
                    .Where(h => probe.InBounds(HexField.Step(cell, h))
                                && probe.LevelAt(HexField.Step(cell, h))
                                   == probe.LevelAt(cell) + 1)
                    .ToList();
                if (up.Count != 1)
                {
                    lines.Add($"  BAD ({cell.X},{cell.Y}) on level "
                              + $"{probe.LevelAt(cell)} has {up.Count} "
                              + "neighbours a level above it"
                              + (up.Count == 0
                                  ? ", so there is nothing for it to bridge"
                                  : " - headings " + string.Join(" ", up)
                                    + " - so the map does not decide its high "
                                    + "edge"));
                    continue;
                }
                Vector2I foot = HexField.Step(cell, HexField.Reverse(up[0]));
                lines.Add($"  BAD ({cell.X},{cell.Y}) bridges {up[0]} but its "
                          + $"foot ({foot.X},{foot.Y}) is "
                          + (!probe.InBounds(foot)
                              ? "off the board"
                              : Ramps[At(foot)]
                                  ? "another ramp"
                                  : $"on level {probe.LevelAt(foot)} and this "
                                    + $"ramp is on {probe.LevelAt(cell)}")
                          + " - so it can only be entered from the top and left "
                          + "the same way");
            }

            // A ramp whose high edge points at a rock hands away the one thing a
            // rock is: the rise that cannot be climbed.
            foreach (Vector2I cell in declared.Where(legal.ContainsKey))
            {
                Vector2I high = HexField.Step(cell, legal[cell]);
                if (IsCliff(high))
                    lines.Add($"  BAD ({cell.X},{cell.Y}) climbs on to the rock "
                              + $"at ({high.X},{high.Y})");
            }

            // Near-facing ramps, named and allowed. See AbbeyRamps.
            string near = string.Join(" ", declared.Where(legal.ContainsKey)
                .Where(c => HexField.NearHeading.Contains(legal[c]))
                .Select(c => $"({c.X},{c.Y})@{legal[c]}"));
            if (near.Length > 0)
                lines.Add("  facing the camera, so compressed: " + near);

            // Sea level, printed before the guards run. SetWater throws on
            // this, and a caught throw is one line naming the pairs where the
            // water gets in - not the cells that would end up under it, which is
            // what has to be redrawn.
            IReadOnlyList<Vector2I> flood = Flood();
            lines.Add(flood.Count == 0
                ? "sea level: nothing dry stands at a water surface's own height"
                : $"  BAD sea level: {flood.Count} dry cells stand at a water "
                  + "surface's own height and are under it: "
                  + string.Join(" ", flood.Select(c => $"({c.X},{c.Y})")));

            // The field's own guards, on the real arrays.
            probe.SetRelief(Levels, Ramps);
            if (Water.Any(w => w))
                probe.SetWater(Water);

            // What can be driven to, from every home.
            foreach (Vector2I home in Homes)
            {
                var seen = new HashSet<Vector2I> { home };
                var queue = new Queue<Vector2I>();
                queue.Enqueue(home);
                while (queue.Count > 0)
                {
                    Vector2I at = queue.Dequeue();
                    foreach (int h in HexField.EdgeHeadings)
                    {
                        Vector2I next = HexField.Step(at, h);
                        if (probe.Passable(at, h) && seen.Add(next))
                            queue.Enqueue(next);
                    }
                }

                var stranded = new List<Vector2I>();
                for (int r = 0; r < Rows; r++)
                for (int q = 0; q < Columns; q++)
                {
                    var cell = new Vector2I(q, r);
                    if (OnBoard(cell) && !Cliffs[At(cell)] && !seen.Contains(cell))
                        stranded.Add(cell);
                }

                lines.Add($"from home ({home.X},{home.Y}): {seen.Count} cells "
                          + $"reachable, {stranded.Count} stranded"
                          + (stranded.Count == 0
                              ? ""
                              : ": " + string.Join(" ", stranded.Take(40)
                                  .Select(c => $"({c.X},{c.Y})"))
                                + (stranded.Count > 40 ? " ..." : "")));
            }

            // The legal sites nothing uses, so a stranded region can be fixed by
            // picking one rather than by redrawing the outline.
            string spare = string.Join(" ", legal
                .Where(kv => !Ramps[At(kv.Key)])
                .OrderBy(kv => kv.Key.Y).ThenBy(kv => kv.Key.X)
                .Select(kv => $"({kv.Key.X},{kv.Key.Y})@{kv.Value}"));
            lines.Add("free ramp sites: " + (spare.Length == 0 ? "none" : spare));
        }
        catch (Exception e)
        {
            lines.Add("REFUSED " + e.Message);
        }
        finally
        {
            probe.Free();
        }
        return lines;
    }
}
