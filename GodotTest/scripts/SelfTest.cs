using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// Assertions over the grid arithmetic and the turret modes, run with
/// `godot --path &lt;dir&gt; -- --selftest`.
///
/// It exists because none of this can be checked from a screenshot: picking a
/// cell from a click, walking a path and holding a turret heading through a
/// turn are all behaviour over time, and a still frame shows none of it. The
/// click-to-cell round trip in particular already caught a real bug - the
/// anchor and the hexagon centre are different points, and treating them as one
/// selects the neighbouring cell along every slanted edge.
/// </summary>
public static class SelfTest
{
    /// <summary>
    /// Every check, against the tank that is loaded.
    ///
    /// <paramref name="atlases"/> is every tank that loaded, and it is here for
    /// one reason: the belts only exist on the parts-built model, and the
    /// harness starts on a different one. Asking the current tank alone would
    /// have meant either skipping those checks or making the run order matter.
    /// </summary>
    public static int Run(HexField field, TankSprite tank,
                          IReadOnlyDictionary<string, AtlasSet>? atlases = null,
                          IReadOnlyList<Vehicle>? vehicles = null,
                          int active = 0,
                          SelectionRing? ring = null,
                          SoundSet? common = null,
                          IReadOnlyDictionary<string, SoundSet>? sounds = null,
                          ControlPanel? livePanel = null,
                          Grove? grove = null,
                          Stage3D? stage = null,
                          string? only = null)
    {
        int failed = 0;
        Filter(only);

        // Every check below either wants a flat board or lays its own relief on
        // one, so the run starts from the flat state whatever the session was
        // started with. Without this a --grade run fails four checks that are
        // about the grid rather than about height - and two of them are right to
        // fail on a board with height, which is worse than either answer: a
        // cell's centre really can be hidden by a rise in front of it, so
        // "clicking a cell's centre picks that cell" stops being true and the
        // check has no way to say so.
        bool hadRelief = field.HasRelief;
        field.SetRelief(null);

        void Check(string name, bool ok, string detail = "")
        {
            // A check outside the asked-for themes is not run silently and not
            // reported: it is counted, so the tally at the end can say how much
            // of the run this was. See Filter.
            if (!_on)
            {
                _skipped++;
                return;
            }
            _reported++;
            if (ok)
                GD.Print($"  ok    {name}");
            else
            {
                GD.Print($"  FAIL  {name}  {detail}");
                failed++;
            }
        }

        Theme("grid: click lands on the cell that was drawn");
        int misses = 0;
        var firstMiss = "";
        for (int q = 0; q < field.Columns; q++)
        for (int r = 0; r < field.Rows; r++)
        {
            var cell = new Vector2I(q, r);
            Vector2I hit = field.CellAt(field.CellCentre(cell));
            if (hit != cell)
            {
                misses++;
                if (firstMiss.Length == 0)
                    firstMiss = $"({q},{r}) -> ({hit.X},{hit.Y})";
            }
        }
        Check("cell centre round trip over every cell", misses == 0,
            $"{misses} wrong, first {firstMiss}");

        // A click anywhere inside a cell must pick that cell, not just a click
        // dead centre. Probe at 40% of the way to each of the six neighbours.
        misses = 0;
        firstMiss = "";
        for (int q = 1; q < field.Columns - 1; q++)
        for (int r = 1; r < field.Rows - 1; r++)
        {
            var cell = new Vector2I(q, r);
            Vector2 centre = field.CellCentre(cell);
            foreach (int heading in HexField.EdgeHeadings)
            {
                Vector2 towards = field.CellCentre(HexField.Step(cell, heading));
                Vector2I hit = field.CellAt(centre + (towards - centre) * 0.4f);
                if (hit != cell)
                {
                    misses++;
                    if (firstMiss.Length == 0)
                        firstMiss = $"({q},{r}) at {heading} deg -> ({hit.X},{hit.Y})";
                }
            }
        }
        Check("clicks off-centre stay in the same cell", misses == 0,
            $"{misses} wrong, first {firstMiss}");

        Theme("grid: steps and headings agree");
        misses = 0;
        for (int q = 0; q < field.Columns; q++)
        for (int r = 0; r < field.Rows; r++)
        foreach (int heading in HexField.EdgeHeadings)
        {
            var cell = new Vector2I(q, r);
            if (HexField.HeadingTo(cell, HexField.Step(cell, heading)) != heading)
                misses++;
        }
        Check("HeadingTo inverts Step everywhere", misses == 0, $"{misses} wrong");

        // The chain is what the ram event walks when a diff of nose cells
        // spans more than one rim - see TankBench.Ram - so the claim it
        // stands on is asserted here: both ends are the ends asked for, and
        // every consecutive pair is a Step apart, which is what lets the walk
        // name a flat-side heading for each rim it crossed.
        misses = 0;
        firstMiss = "";
        for (int q = 0; q < field.Columns; q++)
        for (int r = 0; r < field.Rows; r++)
        for (int q2 = 0; q2 < field.Columns; q2++)
        for (int r2 = 0; r2 < field.Rows; r2++)
        {
            var from = new Vector2I(q, r);
            var to = new Vector2I(q2, r2);
            List<Vector2I> chain = HexField.Chain(from, to);
            string why = "";
            if (chain.Count == 0 || chain[0] != from)
                why = "does not start at the start";
            else if (chain[^1] != to)
                why = "does not end at the end";
            else
                for (int i = 1; i < chain.Count; i++)
                    if (HexField.HeadingTo(chain[i - 1], chain[i]) < 0)
                    {
                        why = $"({chain[i - 1].X},{chain[i - 1].Y}) and "
                              + $"({chain[i].X},{chain[i].Y}) are not "
                              + "neighbours";
                        break;
                    }
            if (why.Length > 0)
            {
                misses++;
                if (firstMiss.Length == 0)
                    firstMiss = $"({q},{r})->({q2},{r2}): {why}";
            }
        }
        Check("a chain runs end to end in single steps, between every pair "
              + "on the board", misses == 0, $"{misses} wrong, first {firstMiss}");

        Theme("paths");
        int bad = 0;
        var badDetail = "";
        for (int q = 0; q < field.Columns; q++)
        for (int r = 0; r < field.Rows; r++)
        {
            var from = new Vector2I(0, 0);
            var to = new Vector2I(q, r);
            List<Vector2I> path = field.FindPath(from, to);
            if (from == to)
                continue;
            string why = "";
            if (path.Count == 0)
                why = "no path";
            else if (path[^1] != to)
                why = "does not end at the target";
            else
            {
                Vector2I at = from;
                foreach (Vector2I next in path)
                {
                    if (!field.InBounds(next))
                    {
                        why = $"leaves the board at ({next.X},{next.Y})";
                        break;
                    }
                    if (HexField.HeadingTo(at, next) < 0)
                    {
                        why = $"({at.X},{at.Y}) and ({next.X},{next.Y}) are not neighbours";
                        break;
                    }
                    at = next;
                }
            }
            if (why.Length > 0)
            {
                bad++;
                if (badDetail.Length == 0)
                    badDetail = $"to ({q},{r}): {why}";
            }
        }
        Check("every path is contiguous, in bounds and reaches the target",
            bad == 0, $"{bad} bad, first {badDetail}");

        Terrain(field, vehicles, active, Check);
        Relief(field, grove, Check);
        Ramps(field, stage, Check);
        Water(field, grove, Check);
        Ripple(Check);
        Walls(field, vehicles, Check);
        Burning(field, grove, Check);
        Climbing(field, tank, Check);

        Theme("turret modes");
        // Which one a tank comes up in, off the named constant rather than off
        // whatever the field happens to be initialised to. It decides what every
        // screenshot of a turning tank shows, and the two modes look nothing
        // alike.
        Check("a tank comes up with its turret riding the hull",
            !TankSprite.HoldsHeadingByDefault && !new TankSprite().TurretHoldsHeading,
            "holding its heading is the headline and is one keypress away; "
            + "riding is what a tank does when nobody is laying the gun");

        tank.TurretHoldsHeading = true;
        tank.HullFacing = 270.0;
        tank.TurretFacing = 0.0;
        for (int i = 0; i < 24; i++)
            tank.TurnHull(15.0);
        Check("a holding turret keeps its world heading through a full hull turn",
            Math.Abs(WrapAngle(tank.TurretFacing - 0.0)) < 1e-6,
            $"turret ended at {tank.TurretFacing:F3}");
        Check("the hull under it still came back round",
            Math.Abs(WrapAngle(tank.HullFacing - 270.0)) < 1e-6,
            $"hull ended at {tank.HullFacing:F3}");

        tank.TurretHoldsHeading = false;
        tank.HullFacing = 270.0;
        tank.TurretFacing = 0.0;
        double offsetBefore = WrapAngle(tank.TurretFacing - tank.HullFacing);
        for (int i = 0; i < 7; i++)
            tank.TurnHull(23.0);
        double offsetAfter = WrapAngle(tank.TurretFacing - tank.HullFacing);
        Check("a riding turret keeps a constant offset from the hull",
            Math.Abs(WrapAngle(offsetAfter - offsetBefore)) < 1e-6,
            $"offset {offsetBefore:F3} -> {offsetAfter:F3}");
        Check("a riding turret actually moved",
            Math.Abs(WrapAngle(tank.TurretFacing - 0.0)) > 1.0,
            $"turret ended at {tank.TurretFacing:F3}");

        Theme("body pitch");
        var pitch = new BodyPitch();
        const double dt = 1.0 / 60.0;
        double minAccel = 0.0, maxBrake = 0.0;
        // 0.25s flat out, coast, 0.25s full braking, then let it settle
        for (int i = 0; i < 15; i++) { pitch.Update(1.0, dt); minAccel = Math.Min(minAccel, pitch.Angle); }
        for (int i = 0; i < 30; i++) pitch.Update(0.0, dt);
        double afterCoast = pitch.Angle;
        for (int i = 0; i < 15; i++) { pitch.Update(-1.0, dt); maxBrake = Math.Max(maxBrake, pitch.Angle); }

        // How long the body keeps moving after the last input. This is the
        // assertion that was missing when the tank felt like jelly: the old
        // spring had the direction and the amplitude right and simply rang for
        // over a second.
        int settleFrames = -1;
        for (int i = 0; i < 240 && settleFrames < 0; i++)
        {
            pitch.Update(0.0, dt);
            if (Math.Abs(pitch.Angle) < 0.1 * pitch.Gain)
                settleFrames = i;
        }
        double settle = settleFrames < 0 ? 99.0 : settleFrames * dt;
        for (int i = 0; i < 120; i++) pitch.Update(0.0, dt);

        Check("accelerating lifts the nose", minAccel < -0.5 * pitch.Gain,
            $"peak {minAccel:F4}");
        Check("braking dips the nose", maxBrake > 0.5 * pitch.Gain,
            $"peak {maxBrake:F4}");
        Check("it levels off while coasting", Math.Abs(afterCoast) < 0.3 * pitch.Gain,
            $"still at {afterCoast:F4}");
        Check("it settles level once stopped", Math.Abs(pitch.Angle) < 0.002,
            $"ended at {pitch.Angle:F5}");
        Check("it stops ringing quickly", settle <= 0.35, $"took {settle:F3}s");
        // some overshoot has to survive the extra damping: with none, the body
        // just leans and looks parked on a slope
        Check("the spring still overshoots", Math.Abs(minAccel) > pitch.Gain,
            $"peak {Math.Abs(minAccel):F4} vs gain {pitch.Gain:F4}");
        // 0.045 is about 3px of stern travel on a 68px hull; past that the
        // sprite stops reading as rigid
        Check("amplitude stays under the jelly threshold", Math.Abs(minAccel) < 0.045,
            $"peak {Math.Abs(minAccel):F4}");

        Theme("ground rumble");
        // What a tank drawn at 1:1 shows. The class holds the amplitude now and
        // the draw does the rounding - see TankSprite.SnapHeave - so every claim
        // below about pixels has to be made about the drawn figure, not about the
        // held one.
        static int Shown(BodyRumble r, float scale = 1.0f) =>
            (int)Math.Round(TankSprite.SnapHeave(r.Heave, scale) * scale);
        // The ground is a lattice on the field now, so a test drives a line
        // across it rather than winding an odometer. Neither axis-aligned nor
        // diagonal: on a square lattice those two are the extremes of the
        // crossing rate. The start is off any boundary, so that a coarse step and
        // a fine one are not being compared across a dead zone.
        var course = new Vector2(0.6f, 0.8f);
        var start = new Vector2(7.3f, 11.7f);
        // One cell for every claim about the lattice: which cell the patch is on
        // is the other half of it, and holding it still is what leaves these
        // measuring the square. The claims about the cell are their own, below.
        var oneCell = new Vector2I(4, 4);
        void Roll(BodyRumble r, ref Vector2 at, double speed, double step)
        {
            at += course * (float)(speed * step);
            r.Advance(at, oneCell, speed, step);
        }
        var rumble = new BodyRumble();
        Vector2 rumbleAt = start;
        var seen = new HashSet<int>();
        int changes = 0, previous = 0;
        for (int i = 0; i < 300; i++)
        {
            Roll(rumble, ref rumbleAt, 240.0, dt);
            seen.Add(Shown(rumble));
            if (Shown(rumble) != previous) changes++;
            previous = Shown(rumble);
        }
        Check("the jolt is whole pixels and stays small",
            seen.All(v => v is >= -2 and <= 2), $"saw {string.Join(",", seen.OrderBy(v => v))}");
        // jolts of differing size, not a two-state tick
        Check("it uses more than one step",
            seen.Contains(0) && seen.Any(v => Math.Abs(v) == 1) && seen.Any(v => Math.Abs(v) == 2),
            $"saw {string.Join(",", seen.OrderBy(v => v))}");
        // The upper bound is the real assertion: 300 frames changing 300 times
        // is a buzz, not a rumble, and that is what the first bump spacing did.
        // A jolt needs a couple of still frames around it to register as one.
        // The upper bound is the real assertion. 300 frames changing every two
        // or three is a buzz, not a rumble, and that is exactly what rounding a
        // continuous wave produced. At about 8 bumps a second, five seconds can
        // change at most 40 times.
        Check("it jolts at a plausible rate", changes is > 12 and < 60,
            $"{changes} changes over 5s at 60fps");
        Check("each jolt is held long enough to be seen",
            300.0 / Math.Max(changes, 1) >= 5.0,
            $"a change every {300.0 / Math.Max(changes, 1):F1} frames");

        // The ground belongs to the place, so how the tank got there cannot
        // matter: the same line walked in a seventh of the steps has to end on
        // the same jolt. This is the frame-rate claim the odometer version made,
        // and the stronger one underneath it - that the bump is a function of
        // where the tank is and of nothing else.
        // The end points deliberately avoid the dead zone: within Hysteresis of a
        // boundary the coarse walk and the fine one legitimately hold different
        // patches, which says nothing about what the ground is made of.
        var driftOk = true;
        var mismatch = "";
        foreach (double total in new[] { 47.0, 133.0, 237.0, 512.5 })
        {
            var coarse = new BodyRumble();
            var fine = new BodyRumble();
            Vector2 coarseAt = start, fineAt = start;
            // One speed, two step lengths - not one step and two speeds. The
            // gain is a function of the speed, so walking the same line at two
            // speeds compares two gains and says nothing about the ground: it is
            // how this check first failed after the rewrite.
            for (int i = 0; i < 20; i++)
                Roll(coarse, ref coarseAt, 240.0, total / 20.0 / 240.0);
            for (int i = 0; i < 137; i++)
                Roll(fine, ref fineAt, 240.0, total / 137.0 / 240.0);
            if (Math.Abs(coarse.Heave - fine.Heave) > 1e-12)
            {
                driftOk = false;
                mismatch = $"at {total}px: {coarse.Heave:F6} vs {fine.Heave:F6}";
            }
        }
        Check("the ground is where the tank is, not how it got there", driftOk,
            mismatch);

        // One bump is one decision, and the gain is half of that decision: read
        // live it moves inside the bump, which is what the hold is for. This is
        // the ramp from rest, where it happened on every class.
        //
        // Two things may happen inside one bump and no third: the latched value,
        // and letting go of it once the body has carried it long enough. So what
        // is forbidden is a different value, and coming back after coming level -
        // dropping the offset is not a decision about the ground, and a ramp from
        // rest crosses a patch in more frames than the hold lasts, so this is the
        // run where the release happens.
        var ramp = new BodyRumble { FullSpeed = 155.0 };
        Vector2 rampAt = start;
        double climbing = 0.0;
        long onBump = long.MinValue;
        double heldAt = 0.0;
        bool released = false;
        var slid = "";
        for (int i = 0; i < 240; i++)
        {
            climbing = Math.Min(climbing + 620.0 * dt, 310.0);
            Roll(ramp, ref rampAt, climbing, dt);
            if (ramp.Bump != onBump)
            {
                onBump = ramp.Bump;
                heldAt = ramp.Heave;
                released = false;
            }
            else if (Math.Abs(ramp.Heave) < 1e-12)
                released = true;
            else if (released)
                slid = $"bump {onBump} came back to {ramp.Heave:F4} after letting go";
            else if (Math.Abs(ramp.Heave - heldAt) > 1e-12)
                slid = $"bump {onBump} slid to {ramp.Heave:F4} from {heldAt:F4}";
        }
        Check("one bump is one decision, even while the throttle is opening",
            slid.Length == 0, slid);
        // And the latch must not outlive the motion. A tank that brakes to a
        // halt half way over a bump keeps the latched gain unless something ends
        // the hold, and would sit there jolted until it drove on.
        var halted = new BodyRumble();
        Vector2 haltedAt = start;
        // Driven until it is on a jolt worth showing, rather than for a fixed
        // forty frames. Which patch a frame count lands on is the draw's
        // business, and the pair put two fifths of them level - so the fixed
        // count picked a bump of -0.068 and the check read as failing when what
        // it had actually done was stop on flat ground.
        int haltedFrames = 0;
        while (Math.Abs(halted.Heave) <= 0.5 && haltedFrames < 600)
        {
            Roll(halted, ref haltedAt, 240.0, dt);
            haltedFrames++;
        }
        double rolling = halted.Heave;
        halted.Advance(haltedAt, oneCell, 0.0, dt);
        Check("a tank that stops mid-bump comes level",
            Math.Abs(rolling) > 0.5 && Math.Abs(halted.Heave) < 1e-9,
            $"was {rolling:F3} after {haltedFrames} frames, left at {halted.Heave:F3}");

        // Three tanks on parallel routes must not be jolted in step, and this is
        // what the lattice buys instead of a per-tank seed: they are in different
        // places, so they are on different ground, and nothing has to be told
        // which tank it is. The seed this replaced answered the odometer's
        // problem - equal distances, identical bumps - and is gone with it.
        var groundPer = new List<List<double>>();
        for (int lane = 0; lane < 3; lane++)
        {
            var own = new BodyRumble();
            Vector2 at = start + new Vector2(0.0f, 137.0f * lane);
            var track = new List<double>();
            for (int i = 0; i < 200; i++)
            {
                Roll(own, ref at, 240.0, dt);
                track.Add(own.Heave);
            }
            groundPer.Add(track);
        }
        Check("three tanks on parallel routes do not meet the same ground",
            !groundPer[0].SequenceEqual(groundPer[1])
            && !groundPer[1].SequenceEqual(groundPer[2])
            && !groundPer[0].SequenceEqual(groundPer[2]),
            "two of the three were jolted identically");
        // And the other half, which --capture needs: the same line twice is the
        // same ground twice. A rumble that differed between runs would be
        // measuring itself in every A/B taken near a moving tank.
        var again = new BodyRumble();
        Vector2 againAt = start + new Vector2(0.0f, 137.0f);
        var replay = new List<double>();
        for (int i = 0; i < 200; i++)
        {
            Roll(again, ref againAt, 240.0, dt);
            replay.Add(again.Heave);
        }
        Check("and one tank driving the same line meets it again",
            replay.SequenceEqual(groundPer[1]),
            "the same line gave a different ride the second time");
        // The point of a place-indexed lattice, said directly: two tanks standing
        // on one patch are standing on one patch. Under the odometer they agreed
        // only if they had driven the same distance, which is a coincidence.
        var here = new BodyRumble();
        var there = new BodyRumble();
        Vector2 spot = new Vector2(413.0f, 271.0f);
        // One of them has been driving all over the board first, because that is
        // the half an odometer gets wrong: what the ground gives has to depend on
        // the square and not on how long the tank has been out.
        Vector2 wandering = new Vector2(-190.0f, 640.0f);
        for (int i = 0; i < 300; i++)
            Roll(there, ref wandering, 240.0, dt);
        here.Advance(spot, oneCell, 240.0, dt);
        there.Advance(spot, oneCell, 240.0, dt);
        Check("two tanks on one patch of ground are jolted alike",
            Math.Abs(here.Heave - there.Heave) < 1e-12
            && Math.Abs(here.Roll - there.Roll) < 1e-12
            && Math.Abs(here.Heave) > 1e-9,
            $"{here.Heave:F6} against {there.Heave:F6}");

        // What a square lattice costs: a line into its corner crosses more
        // squares than a line along its side, so the bump rate leans on the
        // heading. sqrt(2) is the worst a straight line can do; the six the board
        // can actually drive are what matters, and if they spread wider than half
        // again the lattice would have to be hexagonal.
        var perThousand = new List<double>();
        foreach (int heading in HexField.EdgeHeadings)
        {
            Vector2 way = tank.Atlas!.GroundDirection(heading).Normalized();
            var along = new BodyRumble();
            Vector2 at = start;
            long bumps = 0, was = long.MinValue;
            for (int i = 0; i < 3000; i++)
            {
                at += way * (float)(240.0 * dt);
                along.Advance(at, oneCell, 240.0, dt);
                if (along.Bump != was) { was = along.Bump; bumps++; }
            }
            perThousand.Add(bumps * 1000.0 / (240.0 * 3000.0 * dt));
        }
        Check("the bump rate does not lean much on the heading",
            perThousand.Max() < perThousand.Min() * 1.5,
            $"{perThousand.Min():F1} to {perThousand.Max():F1} bumps per 1000px "
            + $"({perThousand.Max() / perThousand.Min():F2}x)");

        // The kind of ground is its own multiplier beside the water's, because
        // they are two statements about one patch. Identity first: a board with no
        // art behind a kind rides as it always did.
        var plainRide = new BodyRumble();
        var rough = new BodyRumble { Roughness = 1.3 };
        var soaked = new BodyRumble { Roughness = 1.3, Damping = 0.35 };
        Vector2 stone = new Vector2(913.0f, 77.0f);
        plainRide.Advance(stone, oneCell, 240.0, dt);
        rough.Advance(stone, oneCell, 240.0, dt);
        soaked.Advance(stone, oneCell, 240.0, dt);
        Check("rougher ground gives a bigger jolt off the same patch",
            Math.Abs(rough.Heave - plainRide.Heave * 1.3) < 1e-9
            && Math.Abs(plainRide.Heave) > 1e-9,
            $"{plainRide.Heave:F4} against {rough.Heave:F4}");
        Check("and the ford still damps it on top",
            Math.Abs(soaked.Heave - rough.Heave * 0.35) < 1e-9,
            $"{rough.Heave:F4} against {soaked.Heave:F4}");

        var stopped = new BodyRumble();
        stopped.Advance(Vector2.Zero, oneCell, 0.0, dt);
        Check("it is still when the tank is",
            Math.Abs(stopped.Heave) < 1e-9 && Math.Abs(stopped.Roll) < 1e-9,
            $"heave {stopped.Heave:F5}, roll {stopped.Roll:F5}");

        // Roll must not just track the heave, or it adds nothing the heave has
        // not already said. The pair makes this half cheap - the mean and the
        // half difference of two draws agree in sign exactly half the time by
        // construction - so it is kept as the floor it now is: a roll copied
        // off the heave agrees always, a roll negated from it never. What the
        // pair itself claims is the check under this one.
        var pair = new BodyRumble();
        Vector2 pairAt = start;
        int agree = 0, samples = 0;
        double worstJoint = 0.0;
        for (int i = 0; i < 400; i++)
        {
            Roll(pair, ref pairAt, 240.0, dt);
            if (Math.Abs(pair.Heave) < 1e-9) continue;
            samples++;
            if (Math.Sign(pair.Roll) == Math.Sign(pair.Heave)) agree++;
            worstJoint = Math.Max(worstJoint,
                Math.Abs(pair.Heave) / pair.Amplitude
                + Math.Abs(pair.Roll) / pair.RollAmplitude);
        }
        Check("roll is not derived from the heave",
            samples > 50 && agree > samples * 0.25 && agree < samples * 0.75,
            $"{agree} of {samples} bumps agreed in sign");
        // And yet both come off one pair of track heights, which shows as a
        // bound rather than as a correlation. Heave is their mean and roll their
        // half difference, so the two normalised and added come to the taller of
        // the two tracks exactly - and a track cannot ride up by more than the
        // amplitude. Lifting the whole tank takes both tracks agreeing, so one
        // bump cannot be a full lift and a full tip at once.
        //
        // Both halves again. Two independent draws fill the square instead and
        // put half of every bump outside this diamond, which is what was here
        // before and what the upper bound catches; the lower one is against a
        // rumble that has quietly stopped, since nought passes a bound.
        Check("but both come off one pair of track heights",
            worstJoint <= 1.0 + 1e-9 && worstJoint > 0.9,
            $"worst normalised |heave| + |roll| is {worstJoint:F3} over {samples} bumps");

        // The point of having roll at all. A bump lifts the tank straight up in
        // world space, which is straight up on screen at every heading, so the
        // heave hides inside the motion whenever the tank is travelling up or
        // down the screen. Roll runs across the heading, and comes out
        // horizontal exactly there. Every hex direction needs at least one of
        // the two well off the line of travel, or the rumble is invisible on
        // that heading - which is what driving down a column looked like.
        AtlasSet atlas = tank.Atlas!;
        double worstAxis = 1.0;
        int worstHeading = -1;
        foreach (int heading in HexField.EdgeHeadings)
        {
            Vector2 travel = atlas.GroundDirection(heading).Normalized();
            double acrossHeave = Math.Abs(travel.Cross(new Vector2(0.0f, 1.0f)));
            double acrossRoll = Math.Abs(travel.Cross(
                atlas.GroundDirection(heading + 90.0).Normalized()));
            double best = Math.Max(acrossHeave, acrossRoll);
            if (best < worstAxis)
            {
                worstAxis = best;
                worstHeading = heading;
            }
        }
        Check("every heading has a jolt axis across its travel",
            worstAxis > 0.5,
            $"worst is {worstHeading} deg at {worstAxis:F2}");

        // Counted per bump rather than per frame, and that is not tidying: a
        // bump at 20px/s is held for ninety frames and one at cruise for seven,
        // so a frame count over a fixed number of frames compares thirteen bumps
        // against a hundred and sixty. It read 360 against 387 - a ratio of noise
        // over noise, the same shape of mistake the cycle-seam check made when it
        // divided one median by another.
        var crawl = new BodyRumble();
        var fast = new BodyRumble();
        Vector2 crawlAt = start, fastAt = start;
        int crawlBumps = 0, crawlShown = 0, fastBumps = 0, fastShown = 0;
        long onCrawl = long.MinValue, onFast = long.MinValue;
        for (int i = 0; i < 12000; i++)
        {
            Roll(crawl, ref crawlAt, 20.0, dt);
            if (crawl.Bump != onCrawl)
            {
                onCrawl = crawl.Bump;
                crawlBumps++;
                if (Shown(crawl) != 0) crawlShown++;
            }
            Roll(fast, ref fastAt, 240.0, dt);
            if (fast.Bump != onFast)
            {
                onFast = fast.Bump;
                fastBumps++;
                if (Shown(fast) != 0) fastShown++;
            }
        }
        double crawlShare = (double)crawlShown / Math.Max(crawlBumps, 1);
        double fastShare = (double)fastShown / Math.Max(fastBumps, 1);
        // Both halves, and the left one is the point: written as "fewer than a
        // third" alone this passed on nought, which is what a crawl actually got
        // - the gain could not reach the half pixel a jolt needs. A claim about
        // thinning out that is satisfied by silence is not a claim.
        Check("it thins out at a crawl instead of switching off",
            crawlShare > 0.0 && crawlShare < fastShare * 3 / 4,
            $"{crawlShare:P0} of {crawlBumps} bumps show against {fastShare:P0} of {fastBumps}");
        // And the harness's own crawl, which is the speed this was measured
        // failing at: a tank creeping through a bend does 0.12 of cruise against
        // a FullSpeed of half of it.
        var bend = new BodyRumble { FullSpeed = 240.0 * 0.5 };
        Vector2 bendAt = start;
        int bendJolts = 0;
        for (int i = 0; i < 1200; i++)
        {
            Roll(bend, ref bendAt, 240.0 * 0.12, dt);
            if (Shown(bend) != 0) bendJolts++;
        }
        Check("a tank creeping through a bend still meets the ground",
            bendJolts > 0, $"{bendJolts} of 1200 frames jolted at the corner speed");

        // And a pond bottom is not a field. Per bump and at one speed, so what is
        // being compared is the ground rather than the ford's own speed cap.
        double Showing(double damping, double speed)
        {
            var probe = new BodyRumble { FullSpeed = 120.0, Damping = damping };
            Vector2 at = start;
            long on = long.MinValue;
            int bumps = 0, shown = 0;
            for (int i = 0; i < 12000; i++)
            {
                Roll(probe, ref at, speed, 1.0 / 60.0);
                if (probe.Bump == on) continue;
                on = probe.Bump;
                bumps++;
                if (Math.Abs(TankSprite.SnapHeave(probe.Heave, 1.0f)) > 0.5) shown++;
            }
            return (double)shown / Math.Max(bumps, 1);
        }
        double dry = Showing(1.0, 108.0);
        double wet = Showing(BodyRumble.WetDamping, 108.0);
        Check("the same tank at the same speed rides softer in a ford",
            wet > 0.0 && wet < dry / 2.0, $"{wet:P0} of bumps show wading, {dry:P0} dry");

        // The hold belongs to the body, not to the ground. Held until the next
        // patch, a bump at a crawl stood for 83 frames at a stretch and 270 at
        // the worst - a lean, not a jolt, and the one place the complaint about
        // this effect is real. Both halves again: nought frames would satisfy a
        // bound on its own, and nought is a rumble that has stopped.
        int Longest(double speed, double hold)
        {
            var probe = new BodyRumble { FullSpeed = 120.0, HoldSeconds = hold };
            Vector2 at = start;
            int worst = 0, run = 0;
            for (int i = 0; i < 6000; i++)
            {
                Roll(probe, ref at, speed, dt);
                if (Shown(probe) != 0)
                {
                    run++;
                    worst = Math.Max(worst, run);
                }
                else run = 0;
            }
            return worst;
        }
        // The bound is a number of frames rather than the setting read back.
        // Written as "no longer than HoldSeconds" it is true of any hold at all,
        // including no hold - which is the very thing it is here to forbid, and
        // it passed on it. A third of a second is the claim: it was a second and
        // a half, and the same run uncapped still measures 180 frames.
        int crawlRun = Longest(20.0, new BodyRumble().HoldSeconds);
        Check("a bump does not outstay the suspension",
            crawlRun > 3 && crawlRun < 25,
            $"longest jolt at a crawl is {crawlRun} frames");
        // And it costs the road nothing, which is the whole of why the hold is a
        // time and not a fraction of the patch: at cruise the next bump arrives
        // before the hold runs out, so the ride is the same frame for frame. It
        // has to be the same for the worst case rather than for the average one -
        // the slowest cruise on the heading where a patch takes longest to cross,
        // which is a heavy along a lattice axis at 30px per bump. Not the shared
        // course, because that one is deliberately off the axis.
        var letGo = new BodyRumble { FullSpeed = 175.0 * 0.5 };
        var holdOn = new BodyRumble { FullSpeed = 175.0 * 0.5, HoldSeconds = 1e9 };
        Vector2 letGoAt = start, holdOnAt = start;
        int roadDrift = 0;
        for (int i = 0; i < 3000; i++)
        {
            var axis = new Vector2((float)(175.0 * dt), 0.0f);
            letGoAt += axis;
            holdOnAt += axis;
            letGo.Advance(letGoAt, oneCell, 175.0, dt);
            holdOn.Advance(holdOnAt, oneCell, 175.0, dt);
            if (Shown(letGo) != Shown(holdOn)) roadDrift++;
        }
        Check("and letting go costs the ride at cruise nothing", roadDrift == 0,
            $"{roadDrift} of 3000 frames differ, heavy along a lattice axis");

        // A cell edge is a bump, and the reason is the kind rather than the
        // event. What the ground is made of changes at an edge and the gain is
        // latched per bump, so with the square alone a tank that has driven onto
        // stony ground goes on riding the soft kind until the next square - up to
        // a quarter of a cell late, and the same distance of dry ride past the
        // edge of a ford. Asserted where it is hardest: the position does not
        // move at all, so the square cannot be what decides.
        var acrossEdge = new BodyRumble { FullSpeed = 120.0, Roughness = 0.0 };
        Vector2 kindAt = start;
        for (int i = 0; i < 3; i++)
            Roll(acrossEdge, ref kindAt, 240.0, dt);
        double onSoft = acrossEdge.Heave;
        long softBumps = acrossEdge.Bump;
        acrossEdge.Roughness = 1.0;
        acrossEdge.Advance(kindAt, new Vector2I(oneCell.X + 1, oneCell.Y), 240.0, dt);
        Check("a cell edge is a bump, so the kind of ground takes effect there",
            acrossEdge.Bump == softBumps + 1
            && Math.Abs(onSoft) < 1e-12 && Math.Abs(acrossEdge.Heave) > 1e-9,
            $"{softBumps} bumps and {onSoft:F4} on the soft kind, "
            + $"{acrossEdge.Bump} and {acrossEdge.Heave:F4} across the edge");

        // The whole-pixel claim, made about the buffer the sprite is rasterised
        // into rather than about the sprite's own space. The node carries the
        // class scale, so a whole pixel here is 0.85 of one on a light and 1.15
        // on a heavy - and the zoom multiplies both. Every scale the bench uses
        // against every zoom it reaches.
        var heaveOffGrid = "";
        foreach (float body in new[] { 0.85f, 1.00f, 1.15f })
            foreach (float zoom in new[] { 0.25f, 1.0f, 2.0f, 3.5f, 8.0f })
            {
                float factor = body * zoom;
                var probe = new BodyRumble();
                Vector2 probeAt = start;
                for (int i = 0; i < 200; i++)
                {
                    Roll(probe, ref probeAt, 240.0, dt);
                    double drawn = TankSprite.SnapHeave(probe.Heave, factor) * factor;
                    if (Math.Abs(drawn - Math.Round(drawn)) > 1e-4)
                        heaveOffGrid = $"{body:F2}x at zoom {zoom:F2} draws {drawn:F3}px";
                }
            }
        Check("the jolt lands on whole pixels of what it is drawn into",
            heaveOffGrid.Length == 0, heaveOffGrid);
        // And the answer it replaced fails that, which is the whole of the fix:
        // one pixel decided in the sprite's own space, drawn through a light's
        // scale, is 0.85 of a pixel. Written out rather than described, so the
        // pair is falsifiable in both directions.
        double wasDrawn = Math.Round(new BodyRumble().Amplitude * 0.9) * 0.85f;
        Check("and deciding it before the class scale does not",
            Math.Abs(wasDrawn - Math.Round(wasDrawn)) > 1e-4, $"drew {wasDrawn:F3}px");

        Theme("engine tremble");
        var tremble = new EngineTremble();
        // What one unit of amplitude is worth at the stern - the figure the
        // amplitude checks are calibrated against, and the one the panel quotes
        // under the level slider. It was the roof lever and is the same number: see
        // EngineTremble.SternLeverPx for why keeping it is the point.
        const double sternLeverPx = EngineTremble.SternLeverPx;
        double tremblePeak = 0.0;
        int pitchCrossings = 0, yawCrossings = 0, axesAgree = 0, trembleSamples = 0;
        double lastPitch = 0.0, lastYaw = 0.0;
        for (int i = 0; i < 60; i++)
        {
            tremble.Advance(0.0, dt);
            tremblePeak = Math.Max(tremblePeak, Math.Abs(tremble.Pitch));
            if (i > 0 && Math.Sign(tremble.Pitch) != Math.Sign(lastPitch)) pitchCrossings++;
            if (i > 0 && Math.Sign(tremble.Yaw) != Math.Sign(lastYaw)) yawCrossings++;
            if (i > 0)
            {
                trembleSamples++;
                if (Math.Sign(tremble.Pitch) == Math.Sign(tremble.Yaw)) axesAgree++;
            }
            lastPitch = tremble.Pitch;
            lastYaw = tremble.Yaw;
        }

        // The contrast with the rumble, and the reason this class exists: the
        // rumble is driven by distance and a stopped tank gets nothing from it.
        Check("the tremble runs with the tank standing still", tremblePeak > 0.0,
            $"peak {tremblePeak:F5} at zero speed");
        // Standing still it is near the floor of what a linear filter can show as
        // motion rather than as a soft edge. The band came down with the gains when
        // the lever moved to the stern - see EngineTremble.RestGain: a sixth of a
        // pixel carried coherently by a whole end of the hull is more legible than a
        // third of one tapering off up the body, which is what the old figure was
        // measured on.
        Check("standing still it is a sixth of a pixel of stern travel",
            tremblePeak * sternLeverPx is > 0.12 and < 0.25,
            $"{tremblePeak * sternLeverPx:F2}px on a {sternLeverPx:F0}px lever");
        // Under 30Hz or it aliases into a slow wobble at 60fps; over a few Hz or
        // it is a sway rather than a tremble. Sign changes are twice the
        // frequency, and this is one second of frames.
        Check("the tremble is a tremble, not a sway or an alias",
            pitchCrossings is > 12 and < 40 && yawCrossings is > 8 and < 40,
            $"{pitchCrossings} pitch and {yawCrossings} yaw crossings in 1s");
        // One frequency on both axes is a mechanism cycling; two that never line
        // up is an engine.
        Check("the two axes are not in lockstep",
            axesAgree > trembleSamples * 0.2 && axesAgree < trembleSamples * 0.8,
            $"{axesAgree} of {trembleSamples} frames agreed in sign");

        // Under way it keeps going and speeds up - the engine working harder,
        // not a second effect switching in.
        var underWay = new EngineTremble { TopSpeed = 240.0 };
        double movingPeak = 0.0;
        int movingCrossings = 0;
        double lastMoving = 0.0;
        for (int i = 0; i < 60; i++)
        {
            underWay.Advance(240.0, dt);
            movingPeak = Math.Max(movingPeak, Math.Abs(underWay.Pitch));
            if (i > 0 && Math.Sign(underWay.Pitch) != Math.Sign(lastMoving)) movingCrossings++;
            lastMoving = underWay.Pitch;
        }
        // Both amplitude and rate come up with load, off one ramp. The margin on
        // the amplitude is wide on purpose: at a standstill the tank is the only
        // still thing on screen and a third of a pixel registers, while under
        // way it is sliding past hexes and the same amount disappears into the
        // travel. Equal settings do not look equal.
        Check("it shakes harder under way", movingPeak > 2.0 * tremblePeak,
            $"{movingPeak * sternLeverPx:F2}px moving vs {tremblePeak * sternLeverPx:F2}px standing");
        Check("under way it is still under half a pixel of stern travel",
            movingPeak * sternLeverPx is > 0.3 and < 0.6,
            $"{movingPeak * sternLeverPx:F2}px on a {sternLeverPx:F0}px lever");
        Check("it runs faster under load", movingCrossings > pitchCrossings,
            $"{movingCrossings} crossings at cruise vs {pitchCrossings} at rest");
        // The hard ceiling. Past 30Hz the signal samples into a slow wobble that
        // has nothing to do with it, so the load factor has real headroom and
        // this is where it runs out.
        Check("even at full load it stays well clear of Nyquist",
            underWay.PitchRateAt(underWay.TopSpeed) < 20.0,
            $"{underWay.PitchRateAt(underWay.TopSpeed):F1} Hz against 30 Hz at 60fps");

        // Phase is integrated, not frequency times elapsed time. Under the
        // latter, changing speed moves the whole waveform: after ten seconds of
        // idling the phase would jump by the frequency change times the wait -
        // forty whole cycles here, landing anywhere - which is a visible pop at
        // exactly the moment the tank pulls away.
        //
        // Measured against a twin that never opens the throttle, so what is left
        // is only the legitimate difference: one frame at the higher rate rather
        // than the lower, 0.42 radians of phase, at most 0.42 of the gain apart.
        // A jumped phase would be up to twice the gain apart, well outside.
        var shifting = new EngineTremble { TopSpeed = 240.0 };
        var staying = new EngineTremble { TopSpeed = 240.0 };
        for (int i = 0; i < 600; i++)                              // ten seconds idling
        {
            shifting.Advance(0.0, dt);
            staying.Advance(0.0, dt);
        }
        shifting.Advance(240.0, dt);                               // full throttle
        staying.Advance(0.0, dt);
        // Divided through by the gain, because opening the throttle steps the
        // amplitude too and that step is legitimate - what is being isolated
        // here is the phase. (In play the gain never steps like this: speed
        // ramps at a few px/s per frame, so the amplitude creeps.)
        double divergence = Math.Abs(shifting.Pitch / shifting.GainAt(240.0)
                                     - staying.Pitch / staying.GainAt(0.0));
        Check("a speed change does not jump the phase", divergence < 0.5,
            $"the waveform moved {divergence:F4} of full scale"
            + " in the frame the throttle opened");

        // Which end of the tank moves, which is the whole statement this effect
        // makes and the thing it used to get wrong. Measured off the shear the layer
        // is drawn through - see TankSprite.TrembleTravelPx - at every rendered
        // heading, because the fault was that the answer changed with the heading.
        double wasPitch = tank.TremblePitch, wasYaw = tank.TrembleYaw;
        double trembleHull = tank.HullFacing;
        tank.TremblePitch = 0.006;
        tank.TrembleYaw = 0.004;
        double leastStern = double.MaxValue, mostBow = 0.0, worstRatio = double.MaxValue;
        int worstEnd = -1;
        for (int i = 0; i < tank.Atlas!.Count; i++)
        {
            tank.HullFacing = 360.0 * i / tank.Atlas.Count;
            double stern = tank.TrembleTravelPx(bow: false);
            double bow = tank.TrembleTravelPx(bow: true);
            leastStern = Math.Min(leastStern, stern);
            mostBow = Math.Max(mostBow, bow);
            double ratio = stern / Math.Max(bow, 1e-6);
            if (ratio < worstRatio) { worstRatio = ratio; worstEnd = (int)tank.HullFacing; }
        }
        // The stern moves at every heading and the bow is planted at every heading.
        // Under the old lever - screen height above the contact patch - this fails
        // outright: the end further from the camera got a lever of 107px against 15
        // at the near one, so at half the headings the number below would be the
        // bow's.
        Check("the tremble shakes the stern and not the bow, at every heading",
            leastStern > 0.2 && mostBow < 0.02,
            $"stern moves at least {leastStern:F2}px, bow at most {mostBow:F3}px, "
            + $"worst ratio {worstRatio:F0} at {worstEnd} deg");
        // And it is the same amount at every heading: the amplitude belongs to the
        // engine, not to which way the tank happens to be parked. The pitch is a
        // world vertical, which projects to a screen vertical at any heading, and
        // the yaw is a ground vector, so it is the yaw alone that foreshortens -
        // which is why this is a band rather than an identity.
        tank.TrembleYaw = 0.0;
        double flatStern = double.MaxValue, tallStern = 0.0;
        for (int i = 0; i < tank.Atlas.Count; i++)
        {
            tank.HullFacing = 360.0 * i / tank.Atlas.Count;
            double stern = tank.TrembleTravelPx(bow: false);
            flatStern = Math.Min(flatStern, stern);
            tallStern = Math.Max(tallStern, stern);
        }
        Check("the heave carries the same travel whichever way the tank points",
            tallStern - flatStern < 1e-3,
            $"{flatStern:F4}px to {tallStern:F4}px around the compass");
        // Vertical, not sheared: a world vertical projects to a screen vertical, so
        // the heave has no sideways component to damp. Guards against it being put
        // back through TiltDisplacement, which damps the vertical by four because a
        // rigid body that squashes reads as rubber - a reading a region carried
        // bodily up and down has no way to produce.
        double sidewaysHeave = 0.0;
        for (int i = 0; i < tank.Atlas.Count; i++)
        {
            tank.HullFacing = 360.0 * i / tank.Atlas.Count;
            sidewaysHeave = Math.Max(sidewaysHeave, Math.Abs(tank.TrembleFor(false).X));
        }
        Check("the heave is straight up and down at every heading",
            sidewaysHeave < 1e-6, $"worst sideways component {sidewaysHeave:F6}px");
        // The shear and the formula that built it are two expressions of one thing,
        // and the shear is where a sign, a column or the unsquashing could go wrong
        // without any picture looking obviously broken. Asked against the ends and
        // against the anchor, where the weight is a half.
        tank.TrembleYaw = 0.004;
        double worstAssembly = 0.0;
        for (int i = 0; i < tank.Atlas.Count; i++)
        {
            tank.HullFacing = 360.0 * i / tank.Atlas.Count;
            foreach (Vector2 at in new[]
                     { tank.HullEnd(true), tank.HullEnd(false), Vector2.Zero })
            {
                Vector2 want = tank.TrembleFor(false) * tank.SternWeight(at);
                double had = tank.TremblePitch, hadYaw = tank.TrembleYaw;
                Transform2D with = tank.ShearFor(false);
                tank.TremblePitch = 0.0;
                tank.TrembleYaw = 0.0;
                Vector2 got = with * at - tank.ShearFor(false) * at;
                tank.TremblePitch = had;
                tank.TrembleYaw = hadYaw;
                worstAssembly = Math.Max(worstAssembly, (got - want).Length());
            }
        }
        Check("the shear puts the tremble exactly where the weight says",
            worstAssembly < 1e-3, $"worst disagreement {worstAssembly:F5}px");
        // The anchor is the ring, so the hull under the turret moves half the
        // stern's travel while a stabilised turret moves none - and that difference
        // is the seam. Half of a third of a pixel cannot open it, which is what
        // makes exempting the turret from this affordable at all.
        tank.HullFacing = 270.0;
        Check("the tremble alone cannot part the turret from the hull",
            (tank.TrembleFor(false) * tank.SternWeight(Vector2.Zero)).Length() < 0.5,
            $"{(tank.TrembleFor(false) * tank.SternWeight(Vector2.Zero)).Length():F3}px"
            + " of hull movement under a turret that does not move");
        // And the belts sit it out, which is the argument the whole effect is built
        // on: the engine reaches the hull through the mounts while the tracks are
        // standing on the ground. Asked of the shear a planted layer is drawn
        // through, at every heading, and asked with the hull's own shear beside it -
        // a belt that stopped moving because the tremble stopped would pass a check
        // that only looked at the belt.
        double worstBelt = 0.0, leastHull = double.MaxValue;
        for (int i = 0; i < tank.Atlas.Count; i++)
        {
            tank.HullFacing = 360.0 * i / tank.Atlas.Count;
            Vector2 at = tank.HullEnd(false);
            Vector2 onBelt = tank.ShearFor(false, false, true) * at;
            double had = tank.TremblePitch, hadYaw = tank.TrembleYaw;
            tank.TremblePitch = 0.0;
            tank.TrembleYaw = 0.0;
            Vector2 unshaken = tank.ShearFor(false) * at;
            tank.TremblePitch = had;
            tank.TrembleYaw = hadYaw;
            worstBelt = Math.Max(worstBelt, (onBelt - unshaken).Length());
            leastHull = Math.Min(leastHull, tank.TrembleTravelPx(bow: false));
        }
        Check("the belts stand still while the engine shakes the hull",
            worstBelt < 1e-3 && leastHull > 0.2,
            $"belt moves at most {worstBelt:F5}px where the hull's stern moves at "
            + $"least {leastHull:F2}px");
        tank.TremblePitch = wasPitch;
        tank.TrembleYaw = wasYaw;
        tank.HullFacing = trembleHull;

        // The level knob the panel puts a slider on. It is a multiplier over the
        // tuned pair, so three things have to hold: it scales what comes out, it
        // leaves the standing-to-moving ratio where it was measured (a slider
        // that set an absolute amplitude would flatten that on the first drag),
        // and it does not touch the frequency, which is not its business.
        var loud = new EngineTremble { TopSpeed = 240.0, Level = 2.0 };
        var silent = new EngineTremble { TopSpeed = 240.0, Level = 0.0 };
        double loudPeak = 0.0, silentPeak = 0.0;
        for (int i = 0; i < 60; i++)
        {
            loud.Advance(0.0, dt);
            silent.Advance(0.0, dt);
            loudPeak = Math.Max(loudPeak, Math.Abs(loud.Pitch));
            silentPeak = Math.Max(silentPeak, Math.Abs(silent.Pitch));
        }
        Check("the tremble level scales what comes out",
            Math.Abs(loudPeak - 2.0 * tremblePeak) < 1e-9,
            $"{loudPeak:F5} at level 2 against {tremblePeak:F5} at level 1");
        Check("at level zero the engine stops shaking the hull", silentPeak == 0.0,
            $"peak {silentPeak:F6}");
        Check("the level leaves the standing-to-moving ratio alone",
            Math.Abs(loud.GainAt(240.0) / loud.GainAt(0.0)
                     - tremble.GainAt(240.0) / tremble.GainAt(0.0)) < 1e-12,
            $"{loud.GainAt(240.0) / loud.GainAt(0.0):F4}"
            + $" against {tremble.GainAt(240.0) / tremble.GainAt(0.0):F4}");
        Check("the level does not touch the frequency",
            loud.PitchRateAt(240.0) == tremble.PitchRateAt(240.0),
            $"{loud.PitchRateAt(240.0):F2} Hz against {tremble.PitchRateAt(240.0):F2} Hz");

        Theme("turret scan");
        var scan = new TurretScan();
        scan.Rest(0.0);
        double scanStep = 360.0 / tank.Atlas!.Count;
        var bearings = new HashSet<int>();
        double biggestMove = 0.0, scanPeak = 0.0;
        int movingFrames = 0, offGrid = 0;
        for (int i = 0; i < 3600; i++)          // a minute of idling
        {
            double move = scan.Advance(scanStep, dt);
            biggestMove = Math.Max(biggestMove, Math.Abs(move));
            scanPeak = Math.Max(scanPeak, Math.Abs(scan.Offset));
            if (move != 0.0)
                movingFrames++;
            else
            {
                // at rest it must be parked on a bearing that was rendered,
                // never halfway between two frames
                if (Math.Abs(scan.Offset / scanStep - Math.Round(scan.Offset / scanStep)) > 1e-6)
                    offGrid++;
                bearings.Add((int)Math.Round(scan.Offset));
            }
        }

        Check("the scan stays inside its arc",
            scanPeak <= scan.MaxSteps * scanStep + 1e-6,
            $"reached {scanPeak:F1} deg, limit {scan.MaxSteps * scanStep:F1}");
        Check("the scan visits several bearings", bearings.Count >= 3,
            $"saw {string.Join(",", bearings.OrderBy(v => v))}");
        Check("it rests only on bearings that were rendered", offGrid == 0,
            $"{offGrid} frames parked between frames");
        // The quantisation lesson, made executable. A sway smaller than one
        // frame step draws nothing at all: below the step there is no frame to
        // change to, whether the atlas holds twelve headings or twenty-four.
        Check("the scan is at least one frame step wide", scanPeak >= scanStep - 1e-6,
            $"{scanPeak:F1} deg against a {scanStep:F1} deg step");
        Check("it traverses rather than snapping",
            biggestMove <= scan.PeakRate * dt + 1e-9,
            $"biggest step {biggestMove:F3} deg in a frame, ceiling"
            + $" {scan.PeakRate * dt:F3}");
        // Mostly parked. A turret in constant motion reads as a search radar.
        Check("it spends most of its time dwelling",
            movingFrames < 3600 * 0.6, $"{movingFrames} of 3600 frames traversing");
        // The amplitude that was asked for, stated as itself. The check above
        // says the arc is not smaller than the atlas can draw; this one says it
        // is not bigger than a sway - and the two together pin it to one step,
        // which is the only width that is both.
        Check("the sway reaches one frame step either side and no further",
            scan.MaxSteps == 1 && Math.Abs(scanPeak - scanStep) < 1e-6,
            $"{scanPeak:F1} deg either side of a {scanStep:F1} deg step");
        // And the sway cannot lay the gun down a lane by accident. A gun fires
        // only along a flat side of the hex, so any offset that is a multiple of
        // 60 is a lane the tank was never ordered to fire down. One step of 15
        // clears it; four steps of 15 would not, which is what makes this an
        // assertion rather than a remark.
        int onLane = Enumerable.Range(1, scan.MaxSteps)
            .Count(k => Math.Abs((k * scanStep) % 60.0) < 1e-6);
        Check("no leg of the sway lands on another firing lane", onLane == 0,
            $"{onLane} of {scan.MaxSteps} steps are a multiple of 60 deg");
        // Three gunners, not one gunner three times. Everything here is hashed
        // on the leg so a capture replays, and with nothing else in the hash
        // every tank drew the same legs from the same leg zero at the same
        // moment - the objection the per-vehicle clocks exist for, arriving at
        // the one clock that had been left sharing.
        var sweeps = Enumerable.Range(1, 3).Select(k =>
        {
            var s = new TurretScan { Seed = (ulong)k };
            s.Rest(0.0);
            return s;
        }).ToArray();
        var arcs = sweeps.Select(_ => new List<double>()).ToArray();
        for (int i = 0; i < 1800; i++)
            for (int k = 0; k < sweeps.Length; k++)
            {
                sweeps[k].Advance(scanStep, dt);
                arcs[k].Add(sweeps[k].Offset);
            }
        int abreast = Enumerable.Range(0, arcs[0].Count)
            .Count(i => arcs.All(w => Math.Abs(w[i] - arcs[0][i]) < 1e-9));
        Check("three tanks do not sway in step",
            abreast < arcs[0].Count / 2,
            $"{abreast} of {arcs[0].Count} frames with all three on the same"
            + " bearing");
        // The other half of the same property, and the reason this is a seed and
        // not a clock: --capture and --trace fix the timestep precisely so two
        // runs can be diffed, and a scan that differed between runs would be
        // measuring itself.
        var rerun = new TurretScan { Seed = 2UL };
        rerun.Rest(0.0);
        bool replays = true;
        for (int i = 0; i < 1800; i++)
        {
            rerun.Advance(scanStep, dt);
            if (Math.Abs(rerun.Offset - arcs[1][i]) > 1e-12)
                replays = false;
        }
        Check("but the same tank sways the same way twice", replays,
            "a scan that differs between runs makes every capture diff its own");
        // The traverse rate varies by leg, which is most of what there is to
        // vary: the sway is two snaps and a wait, so when the snap lands is the
        // texture. Never stalled and never backwards, whatever the jitter says.
        double[] rates = Enumerable.Range(0, 40)
            .Select(k => scan.RateOn(k)).ToArray();
        Check("no two legs traverse at quite the same rate",
            rates.Distinct().Count() > 30 && rates.All(r => r > 0.0),
            $"{rates.Distinct().Count()} distinct rates in 40 legs,"
            + $" {rates.Min():F1} to {rates.Max():F1} deg/s");
        // The base is announced, never assumed. Without one the offset would be
        // measured from a bearing nobody chose - the way it was measured from
        // wherever the turret happened to be when the harness started.
        var unbased = new TurretScan();
        double unbasedMove = 0.0;
        for (int i = 0; i < 600; i++)
            unbasedMove += unbased.Advance(scanStep, dt);
        Check("with no base announced the sway does not move the turret",
            unbasedMove == 0.0 && !unbased.Based,
            $"{unbasedMove:F3} deg over 600 frames");
        // The case that made the base worth storing: a tank lays its gun down a
        // lane, fires, and goes back to idling. The sway has to re-centre on the
        // lane rather than carry its old offset into it.
        scan.Suspend();
        Check("anything else driving the turret takes the base away", !scan.Based,
            $"based {scan.Based}");
        scan.Rest(60.0);
        double lanePeak = 0.0;
        for (int i = 0; i < 3600; i++)
        {
            scan.Advance(scanStep, dt);
            lanePeak = Math.Max(lanePeak, Math.Abs(scan.Offset));
        }
        Check("after a fight the sway re-centres on the bearing the gun was left on",
            scan.Base == 60.0 && lanePeak <= scan.MaxSteps * scanStep + 1e-6,
            $"base {scan.Base:F1} deg, reached {lanePeak:F1} deg from it");

        Theme("the shot");
        int shotFrames = FlashSheet.Duration;
        Check("the flash lasts about half a second",
            shotFrames * dt is > 0.3 and < 0.8, $"{shotFrames * dt:F2}s over {shotFrames} frames");
        Check("every sheet frame is on screen at least one frame",
            FlashSheet.Hold.All(v => v >= 1) && FlashSheet.Hold.Length == 16,
            $"{FlashSheet.Hold.Length} frames, min hold {FlashSheet.Hold.Min()}");
        // The bright part is over in about a tenth of a second and the smoke
        // hangs on for the rest. One rate for the whole sheet either drags the
        // flash out into a fire or cuts the smoke off while it is still thick.
        Check("the flash is quicker than the smoke that follows it",
            FlashSheet.Hold.Take(6).Sum() * 3 < FlashSheet.Hold.Skip(6).Sum(),
            $"{FlashSheet.Hold.Take(6).Sum()} frames of flash against"
            + $" {FlashSheet.Hold.Skip(6).Sum()} of smoke");
        Check("the sequence runs through and ends",
            FlashSheet.FrameAt(0) == 0 && FlashSheet.FrameAt(shotFrames - 1) == 15
            && FlashSheet.FrameAt(shotFrames) < 0,
            $"first {FlashSheet.FrameAt(0)}, last {FlashSheet.FrameAt(shotFrames - 1)},"
            + $" past the end {FlashSheet.FrameAt(shotFrames)}");
        var walked = new HashSet<int>();
        for (int i = 0; i < shotFrames; i++) walked.Add(FlashSheet.FrameAt(i));
        Check("no sheet frame is skipped", walked.Count == 16, $"showed {walked.Count} of 16");

        // The muzzle comes off the turret art, so this checks the atlas as much
        // as the code. The test that matters is not how far the tip is - the
        // gun, the aerial and the turret roof are all a similar distance up the
        // screen once the gun points away from the camera - but whether it
        // *swings with the heading*. A muzzle sitting on the aerial stays put
        // while the turret turns, so its bearing from the axis stops agreeing
        // with the gun's, and the dot product below falls away. This is the
        // assertion that caught the aerial winning at 90 degrees.
        double worstAim = 1.0;
        int worstAimFacing = -1, shortest = int.MaxValue, longest = 0;
        foreach (int facing in atlas.RenderedFacings())
        {
            Vector2 offset = atlas.Muzzle(atlas.FrameFor(facing)) - atlas.Anchor;
            int reach = (int)Math.Round(offset.Length());
            shortest = Math.Min(shortest, reach);
            longest = Math.Max(longest, reach);
            double aim = offset.Normalized().Dot(atlas.GroundDirection(facing).Normalized());
            if (aim < worstAim)
            {
                worstAim = aim;
                worstAimFacing = facing;
            }
        }
        Check("the muzzle swings round with the gun at every heading",
            worstAim > 0.80, $"worst is {worstAimFacing} deg at {worstAim:F2}");

        // How far out it lands is not one number, and the spread is the
        // projection rather than an error. For a gun of ground length L sitting
        // h above the axis, the tip shows up at (L, -h*cos e) across the screen,
        // at (0, -L*sin e - h*cos e) pointing away - distance and height adding
        // - and at (0, +L*sin e - h*cos e) pointing at the camera, where they
        // very nearly cancel. So the reach has to fall in that order, and the
        // sixteen pixels at the camera are correct rather than a miss.
        double across = (atlas.Muzzle(atlas.FrameFor(0.0)) - atlas.Anchor).Length();
        double away = (atlas.Muzzle(atlas.FrameFor(90.0)) - atlas.Anchor).Length();
        double toward = (atlas.Muzzle(atlas.FrameFor(270.0)) - atlas.Anchor).Length();
        Check("the muzzle reach falls out in the order the projection dictates",
            across > away && away > toward && across is > 55 and < 120,
            $"across {across:F0}px, away {away:F0}px, at the camera {toward:F0}px");

        // One measurement of where a gun points, three projections of it. The
        // round wants it on the board, the ray wants it in the tank's own
        // picture and the ground shot wants only the direction, and each of them
        // had these two lines written out again above its own transform. The
        // transform is the caller's; this is not.
        if (vehicles is { Count: > 0 })
        {
            Vehicle gunner = vehicles[0];
            AtlasSet own = gunner.Atlas;
            double laid = gunner.Sprite.TurretFacing;
            (Vector2 barrel, Vector2 along) = gunner.Bore(laid);
            Check("a tank measures its own bore the way the flash is placed",
                barrel.DistanceTo(own.Muzzle(own.FrameFor(laid)) - own.Anchor) < 1e-4
                && along == own.GroundDirection(laid),
                "Vehicle.Bore is the one measurement the round, the ray and the "
                + "ground shot share - a second copy of it agrees until somebody "
                + "changes how a gun is measured");
            // The asymmetry, and it is deliberate: under a standing order the
            // round leaves along the lane while the gun may still be traversing
            // onto it, so the barrel is the frame actually being drawn and only the
            // direction follows the bearing it was asked for.
            double lane = Angles.Mod(laid + 60.0, 360.0);
            (Vector2 sameTube, Vector2 other) = gunner.Bore(lane);
            Check("and its barrel is the frame it is drawing while its line is the "
                  + "heading it was asked for",
                sameTube == barrel && other == own.GroundDirection(lane)
                && other != along,
                "a bore that read the heading for both would put the muzzle where "
                + "the gun is not yet pointing");
        }

        // Why GroundDirection is handed to the flash unnormalised. Its length is
        // how much of a horizontal thing survives the projection: all of it
        // across the screen, half of it into the screen. Normalise it and a gun
        // firing away from the camera grows a full-length flash standing over
        // the turret.
        double flashAcross = atlas.GroundDirection(0.0).Length();
        double flashAway = atlas.GroundDirection(90.0).Length();
        Check("the flash foreshortens into the screen",
            Math.Abs(flashAcross - 1.0) < 1e-6
            && Math.Abs(flashAway - Math.Sin(Mathf.DegToRad(atlas.Elevation))) < 1e-6,
            $"x{flashAcross:F2} across, x{flashAway:F2} away");

        Theme("the rendered flash");
        // The rendered flash exists to be compared against the painted sheet,
        // so the first thing to hold is that the comparison is fair: two
        // sources on the same tank at the same moment differing only in
        // artwork. Different tempos would make an A/B measure the tempo.
        Check("both flashes take the same time",
            EffectLayer.Duration == FlashSheet.Duration,
            $"rendered {EffectLayer.Duration} frames, sheet {FlashSheet.Duration}");
        Check("every phase is on screen at least one frame",
            EffectLayer.Hold.All(v => v >= 1) && EffectLayer.Hold.Length == 8,
            $"{EffectLayer.Hold.Length} phases, min hold {EffectLayer.Hold.Min()}");
        Check("the fire is quicker than the smoke that outlives it",
            EffectLayer.Hold.Take(4).Sum() < EffectLayer.Hold.Skip(4).Sum(),
            $"{EffectLayer.Hold.Take(4).Sum()} frames of fire against"
            + $" {EffectLayer.Hold.Skip(4).Sum()} of smoke");
        Check("the sequence runs through and ends",
            EffectLayer.PhaseAt(0) == 0
            && EffectLayer.PhaseAt(EffectLayer.Duration - 1) == 7
            && EffectLayer.PhaseAt(EffectLayer.Duration) < 0,
            $"first {EffectLayer.PhaseAt(0)},"
            + $" last {EffectLayer.PhaseAt(EffectLayer.Duration - 1)},"
            + $" past the end {EffectLayer.PhaseAt(EffectLayer.Duration)}");
        var phases = new HashSet<int>();
        for (int i = 0; i < EffectLayer.Duration; i++)
            phases.Add(EffectLayer.PhaseAt(i));
        Check("no phase is skipped", phases.Count == 8, $"showed {phases.Count} of 8");

        // The effect layers are their own CanvasItems, and a child is redrawn
        // when *it* is dirty, not when its parent is. Gating the redraw on "a
        // shot is live" therefore leaves the last phase on screen for good: the
        // moment the phase goes to -1 the layer stops asking to be redrawn, so
        // it never gets the chance to draw nothing. It showed as smoke that
        // never cleared, because by the last phase the fire has gone and the
        // smoke has not.
        Check("the layer redraws once more after the shot ends",
            EffectLayer.MustRedraw(-1, 7),
            "otherwise the last phase stays on screen for good");
        Check("a live shot keeps redrawing", EffectLayer.MustRedraw(3, 3));
        Check("an idle layer stops redrawing", !EffectLayer.MustRedraw(-1, -1),
            "nothing to show and nothing showing");

        if (!atlas.HasEffects)
        {
            Check($"{atlas.Tag} has rendered effect layers", false,
                "no flash/smoke atlas - split a Barrel in this scene");
        }
        else
        {
            // The invariant that lets a wider tile composite at all. It was
            // never one ortho_scale and one anchor in pixels; it is one world
            // distance per pixel and one anchor at the same place in the frame.
            // A layer drawn at the hull's anchor instead of its own lands 64px
            // out in both directions, which is the whole failure mode here.
            bool aligned = true;
            string worstLayer = "";
            Vector2 tankRelative = atlas.Anchor / (Vector2)atlas.Tile;
            foreach (string layer in AtlasSet.EffectNames)
            {
                Vector2 relative = atlas.AnchorOf(layer) / (Vector2)atlas.TileOf(layer);
                if ((relative - tankRelative).Length() > 1e-4)
                {
                    aligned = false;
                    worstLayer = layer;
                }
            }
            Check("the effect layers share the tank's anchor, in frame fractions",
                aligned,
                aligned ? $"{tankRelative}" : $"{worstLayer} sits elsewhere");
            Check("the effect layers are rendered wider than the tank",
                atlas.TileOf("flash").X > atlas.Tile.X,
                $"flash {atlas.TileOf("flash").X}px against tank {atlas.Tile.X}px");
            Check("smoke and fire are the same size and length",
                atlas.TileOf("smoke") == atlas.TileOf("flash")
                && atlas.PhasesOf("smoke") == atlas.PhasesOf("flash"),
                $"smoke {atlas.TileOf("smoke").X}px x{atlas.PhasesOf("smoke")},"
                + $" fire {atlas.TileOf("flash").X}px x{atlas.PhasesOf("flash")}");
            Check("the harness clock matches the phases that were rendered",
                atlas.EffectPhases == EffectLayer.Hold.Length,
                $"{atlas.EffectPhases} rendered, {EffectLayer.Hold.Length} held");

            // Every phase of every heading has to land on a distinct tile, or
            // the grid is being read the wrong way round - the atlas is one row
            // per phase and one column per heading, and transposing it shows
            // the right picture at heading zero and nonsense everywhere else.
            var tiles = new HashSet<int>();
            foreach (int facing in atlas.RenderedFacings())
                for (int phase = 0; phase < atlas.EffectPhases; phase++)
                    tiles.Add(atlas.EffectFrame("flash", phase, facing));
            Check("every phase and heading maps to its own tile",
                tiles.Count == atlas.EffectPhases * atlas.Count
                && tiles.Max() < atlas.EffectPhases * atlas.Count,
                $"{tiles.Count} distinct of"
                + $" {atlas.EffectPhases * atlas.Count}, highest {tiles.Max()}");
        }

        Theme("trimmed frames");
        {
            AtlasSet packedSet = tank.Atlas!;
            // The fallback first, because it is what keeps every set that was
            // rendered before the trim - Sprites/Obsolete, or a tank not yet
            // re-run - loading and drawing whole tiles.
            Check("a layer with no rectangles reports the whole tile",
                packedSet.SizeOf("no such layer", 0) == (Vector2)packedSet.Tile
                && packedSet.OffsetOf("no such layer", 0) == Vector2.Zero,
                $"{packedSet.SizeOf("no such layer", 0)} at {packedSet.OffsetOf("no such layer", 0)}");

            if (packedSet.Packed)
            {
                long cells = 0, packed = 0;
                int outside = 0, collisions = 0, empty = 0;
                var placed = new HashSet<Vector2>();
                foreach (string layer in packedSet.LoadedLayers)
                {
                    Vector2 tile = packedSet.TileOf(layer);
                    int frames = packedSet.PhasesOf(layer) * packedSet.CountOf(layer);
                    placed.Clear();
                    for (int i = 0; i < frames; i++)
                    {
                        Vector2 size = packedSet.SizeOf(layer, i);
                        Vector2 off = packedSet.OffsetOf(layer, i);
                        cells += (long)tile.X * (long)tile.Y;
                        packed += (long)size.X * (long)size.Y;
                        if (size.X <= 0.0f || size.Y <= 0.0f)
                        {
                            empty++;
                            continue;
                        }
                        // The box has to fit in the tile it was cut from, or the
                        // anchor no longer means what every other measurement in
                        // this atlas means by it.
                        if (off.X < 0.0f || off.Y < 0.0f
                            || off.X + size.X > tile.X || off.Y + size.Y > tile.Y)
                            outside++;
                        // Two frames stored at one place is the packer's silent
                        // failure: the picture is wrong and nothing else says so.
                        if (!placed.Add(packedSet.Region(layer, i).Position))
                            collisions++;
                    }
                }
                Check("every trimmed box fits inside the tile it came from",
                    outside == 0, $"{outside} frames hang outside");
                Check("no two frames are packed at the same place",
                    collisions == 0, $"{collisions} collisions");
                // The blank frames are free now, and they are real: a scar on a
                // plate the hull is standing in front of.
                Check("blank frames are stored as nothing at all", empty > 0,
                    $"{empty} of the set is empty");
                // And the win, measured rather than asserted from the file size.
                Check("the trimmed set is a fraction of the grid it replaces",
                    packed * 4 < cells, $"{packed * 4.0 / 1048576:F0}MB drawn"
                    + $" against {cells * 4.0 / 1048576:F0}MB of cells");
            }
        }

        const double tick = 1.0 / 60.0;

        Theme("the track belts");
        // A loop like the exhaust's, driven by the one thing none of the others
        // are: the ground. Everything here is about that difference.
        var belt = new TrackLoop { Phases = 8, Pitch = 7.0 };
        var beltSeen = new HashSet<int>();
        bool beltInRange = true;
        // A crawl, so the walk is finer than a phase. At a *constant* speed the
        // step can divide the cycle and then the same few frames come round for
        // ever - a link of 7px stepped 1px a frame is 8/7 of a phase and shows
        // seven of the eight, permanently. That is sampling rather than a fault,
        // and it is the same arithmetic the cap below exists for, but it makes
        // "a lap visits every phase" a question about the speed picked here.
        for (int i = 0; i < 600; i++)
        {
            belt.Advance(0.2, tick);
            beltSeen.Add(belt.Frame);
            if (belt.Phase < 0.0 || belt.Phase >= belt.Phases
                || belt.Frame < 0 || belt.Frame >= belt.Phases)
                beltInRange = false;
        }
        Check("the belt stays inside the phases that were rendered", beltInRange,
            $"phase {belt.Phase:F3} frame {belt.Frame} of {belt.Phases}");
        Check("a slow lap visits every phase", beltSeen.Count == belt.Phases,
            $"showed {beltSeen.Count} of {belt.Phases}");
        Check("the belt never runs out, where the shot does",
            EffectLayer.Track(AtlasSet.TrackNames[0]).Loops
            && EffectLayer.PhaseAt(EffectLayer.Duration) < 0,
            "a belt on a moving tank has no last frame");

        // The one that separates it from every other clock here. The exhaust
        // idles, the tremble idles, the fire burns; a stopped tank's tracks are
        // stopped, and a belt that crept while the tank stood would be the most
        // obvious thing on screen.
        var parked = new TrackLoop { Phases = 8, Pitch = 7.0 };
        double parkedStart = parked.Phase;
        for (int i = 0; i < 600; i++)
            parked.Advance(0.0, tick);
        Check("a tank standing still has still tracks",
            Math.Abs(parked.Phase - parkedStart) < 1e-12,
            $"crept {parked.Phase - parkedStart:F6} phases in ten seconds");

        // Distance, not time - and asserted both ways round, because either one
        // alone passes for the wrong reason. The same ground at half the frame
        // rate has to give the same phase; the same time at half the speed must
        // not.
        var quick = new TrackLoop { Phases = 8, Pitch = 7.0 };
        var slow = new TrackLoop { Phases = 8, Pitch = 7.0 };
        for (int i = 0; i < 120; i++)
            quick.Advance(0.5, tick);
        for (int i = 0; i < 60; i++)
            slow.Advance(1.0, tick * 2.0);
        Check("the same ground gives the same phase at any frame rate",
            Math.Abs(quick.Phase - slow.Phase) < 1e-9,
            $"{quick.Phase:F6} against {slow.Phase:F6}");
        var halfSpeed = new TrackLoop { Phases = 8, Pitch = 7.0 };
        for (int i = 0; i < 120; i++)
            halfSpeed.Advance(0.25, tick);
        Check("the same time at half the speed does not",
            Math.Abs(halfSpeed.Phase - quick.Phase) > 0.5,
            $"{halfSpeed.Phase:F3} against {quick.Phase:F3}");

        // One turn of the cycle is one link, which is the whole contract with
        // the renderer: the extra phase it rendered as a check was a slide of
        // exactly one link and came out identical to the first.
        var oneLink = new TrackLoop { Phases = 8, Pitch = 7.0 };
        oneLink.Advance(7.0, 1.0);
        Check("one link of ground is one turn of the cycle",
            Math.Abs(oneLink.Phase) < 1e-9,
            $"came back to {oneLink.Phase:F6} rather than 0");

        var backwards = new TrackLoop { Phases = 8, Pitch = 7.0 };
        backwards.Advance(-1.0, tick);
        Check("backing up runs the belt backwards",
            backwards.Phase > belt.Phases / 2.0,
            $"phase {backwards.Phase:F3} of {backwards.Phases}");

        // The cap and the smear, which are the interesting half and now two
        // thresholds rather than one. Below the lower the belt is in step and
        // its links are countable; between them it is still in step and fading
        // into its own phase average; above the upper it slips, as it always
        // did, but from much further up.
        var capped = new TrackLoop { Phases = 8, Pitch = 7.0 };
        double inStep = capped.BlurSpeed * 0.5 * tick;
        capped.Advance(inStep, tick);
        Check("below the smear speed the belt is in step and crisp",
            Math.Abs(capped.Slip - 1.0) < 1e-9 && capped.Blur == 0.0,
            $"slip {capped.Slip:F4}, blur {capped.Blur:F3}");
        capped.Advance(capped.SyncSpeed * 2.0 * tick, tick);
        Check("far above the cap it slips rather than aliasing",
            capped.Slip < 1.0 && capped.Blur == 1.0,
            $"slip {capped.Slip:F3} at twice {capped.SyncSpeed:F0}px/s,"
            + $" blur {capped.Blur:F2}");
        Check("the smear speed is the lower of the two thresholds",
            capped.BlurSpeed < capped.SyncSpeed,
            $"smears from {capped.BlurSpeed:F0}, slips from"
            + $" {capped.SyncSpeed:F0}px/s");
        // What replaces the old hard cap, and it is the reason the cap could be
        // raised past Nyquist at all: the crisp layer is what could read
        // backwards, and by the alias limit at most half of it is left.
        Check("nothing crisp survives past the alias limit",
            capped.BlurAt(0.5) <= 0.5 + 1e-9 && capped.BlurAt(0.65) >= 1.0 - 1e-9,
            $"blur {capped.BlurAt(0.5):F2} at half a link per frame,"
            + $" {capped.BlurAt(0.65):F2} at the cap");
        Check("the smear only ever comes on as the belt speeds up",
            capped.BlurAt(0.0) == 0.0 && capped.BlurAt(0.2) == 0.0
            && capped.BlurAt(0.45) > 0.0 && capped.BlurAt(0.45) < capped.BlurAt(0.55)
            && capped.BlurAt(10.0) == 1.0,
            "a parked tank is the one time the links are looked at");
        var stepped = new TrackLoop { Phases = 8, Pitch = 7.0 };
        double before = stepped.Phase;
        stepped.Advance(240.0 * tick, tick);
        double moved = stepped.Phase - before;
        Check("a step past the alias limit is covered by the smear",
            moved > 0.0
            && (moved < stepped.Phases / 2.0 || stepped.Blur >= 0.5),
            $"{moved:F3} phases in a frame under {stepped.Blur:F2} of blur,"
            + $" alias at {stepped.Phases / 2.0:F1}");

        // Wiring. The belt is bolted to the hull, so it is indexed and tilted by
        // the hull - the exhaust's mistake, and invisible at heading zero.
        Check("the belts follow the hull, not the turret",
            AtlasSet.TrackNames.All(n => EffectLayer.Track(n).FollowsHull),
            "they are welded to the hull and lean with it");
        Check("a belt is drawn on the shared anchor, never placed",
            AtlasSet.TrackNames.All(n => !EffectLayer.Track(n).Placed),
            "only an arriving shell is placed");
        Check("the belts are kept out of the shot's layer list",
            !AtlasSet.TrackNames.Any(n => AtlasSet.EffectNames.Contains(n)),
            "HasEffects requires every name in it, and would switch the "
            + "rendered flash off on a tank with a barrel but no belts");
        // The shadow is index 0 and is not counted as being on the tank at
        // all - it is the ground, darker, and it is ordered by an absolute z
        // rather than by this list. Everything after it is the tank.
        string[] onTank = TankSprite.LayerOrder
            .SkipWhile(n => n == AtlasSet.ShadowName).ToArray();
        // Every belt there is, running or slack: the wreck's are belts too, and
        // an ordering claim that named only the live pair would have been an
        // ordering claim about half the belts.
        string[] allBelts = AtlasSet.TrackNames
            .Concat(AtlasSet.WreckTrackNames).ToArray();
        Check("the belts composite before everything else on the tank",
            onTank.Take(allBelts.Length).OrderBy(n => n)
                .SequenceEqual(allBelts.OrderBy(n => n)),
            "they are not on the tank, they are the tank: "
            + string.Join(", ", onTank.Take(allBelts.Length + 1)));
        // The belt is lower than the turret but not always behind it: the far
        // one shows over the hull deck legitimately, and only the turret is
        // tall enough to be in its way. Drawn by the parent the turret went
        // down first and the belt painted over it - 1455px of it on HTP.
        int turretAt = Array.IndexOf(onTank, "turret");
        // After *every* belt, which is the substance of it - a belt painting over
        // the turret is the failure, and which belt does not matter. It used to
        // assert the exact index, which was a stronger claim than the reasoning
        // supports and broke the day the wreck's belts landed between them.
        Check("the turret composites after every belt",
            allBelts.All(b => Array.IndexOf(onTank, b) < turretAt),
            $"turret at {turretAt}, {allBelts.Length} belts: "
            + string.Join(", ", onTank.Take(allBelts.Length + 1)));
        // The marks went the other side of the turret when it came out of their
        // holdout: they are paint on the hull, the turret stands over them at
        // every heading, and what used to be cut out of them is now drawn on top
        // of them. Before, this asserted the opposite - correctly, back when the
        // renderer had already removed the overlap.
        Check("and after the marks, which are paint on the hull under it",
            AtlasSet.ScarNames.All(s => Array.IndexOf(onTank, s) < turretAt),
            "a mark on the glacis has to go under the turret that covers it");
        Check("and before anything that happens to the tank",
            turretAt < Array.IndexOf(onTank, AtlasSet.ExhaustName),
            "effects go over the turret, not under it");

        // The turret is not held out of the engine-deck effects any more - it
        // cannot be, they are indexed by the hull and it traverses against the
        // hull - so they are ordered against it instead, per heading. What that
        // machinery has to do is land them on the correct side of it and nowhere
        // else, and land everything else exactly where tree order already had
        // it. Both halves, because a z-index that quietly ignored the flag and
        // one that applied it to every layer look the same from a single case.
        int zTurret = TankSprite.ZFor("turret", false);
        Check("an ordered layer goes under the turret when the atlas says so",
            AtlasSet.OrderedNames.All(n => TankSprite.ZFor(n, false) < zTurret),
            "behind means behind: " + string.Join(", ", AtlasSet.OrderedNames
                .Select(n => $"{n}={TankSprite.ZFor(n, false)}")));
        Check("and over it when it says the other thing",
            AtlasSet.OrderedNames.Where(n => Array.IndexOf(TankSprite.LayerOrder, n)
                                             > Array.IndexOf(TankSprite.LayerOrder, "turret"))
                .All(n => TankSprite.ZFor(n, true) > zTurret),
            "in front means in front");
        Check("the three that move keep their order among themselves",
            TankSprite.ZFor(AtlasSet.ExhaustName, false)
                == TankSprite.ZFor(AtlasSet.FireName, false),
            "tied on z-index they fall back to tree order, which already has "
            + "the column under the flame - a table here would be a second "
            + "copy of that order to keep in step");
        Check("and nothing else is moved by the flag at all",
            TankSprite.LayerOrder.Where(n => Array.IndexOf(AtlasSet.OrderedNames, n) < 0)
                .All(n => TankSprite.ZFor(n, true) == TankSprite.ZFor(n, false)),
            "a set with no flag reports false everywhere, so every layer has to "
            + "keep its tree slot or an older atlas would reshuffle");

        // --- the view jolting when a gun goes off -------------------------
        Theme("camera shake");
        Check("the shake is off by default, and that is a named fact",
            !CameraShake.OnByDefault,
            "it moves the whole frame, and nearly every measurement in this "
            + "bench is a pixel diff - a default in a field initialiser is a "
            + "default nothing can notice being flipped");
        var jolt = new CameraShake();
        double asked = 6.0;
        jolt.Fire(Vector2.Right, asked);
        double peak = 0.0;
        for (int i = 0; i < 120; i++)
        {
            jolt.Update(tick);
            peak = Math.Max(peak, Math.Abs(jolt.Offset.X));
        }
        Check("a kick peaks at about the pixels it was asked for",
            Math.Abs(peak - asked) < asked * 0.05,
            $"asked {asked:F1}px, peaked {peak:F2}px - through the same "
            + "integrator at the same step, which is why it can be asked in "
            + "pixels at all");
        Check("and then it rings down to nothing",
            !jolt.Moving && jolt.Offset.Length() < 0.01f,
            $"left at {jolt.Offset} after two seconds");

        // The one thing that makes it the *gun's* shake rather than a generic
        // rumble: the direction arrives unnormalised, so the projection decides
        // how much of it survives.
        var sideOn = new CameraShake();
        var endOn = new CameraShake();
        sideOn.Fire(atlas.GroundDirection(0.0), 6.0);
        endOn.Fire(atlas.GroundDirection(270.0), 6.0);
        double sideOnPeak = 0.0, endOnPeak = 0.0;
        for (int i = 0; i < 60; i++)
        {
            sideOn.Update(tick);
            endOn.Update(tick);
            sideOnPeak = Math.Max(sideOnPeak, sideOn.Offset.Length());
            endOnPeak = Math.Max(endOnPeak, endOn.Offset.Length());
        }
        Check("a gun fired across the screen jolts further than one fired into it",
            sideOnPeak > endOnPeak * 1.5,
            $"{sideOnPeak:F2}px across against {endOnPeak:F2}px into - "
            + "the ground direction is unnormalised and that is the whole of it");

        // Whole *screen* pixels, so the snap has to happen after the zoom. This
        // is what the capture proved from the other end: shifting a no-shake
        // frame by 4px matched a shaken one with zero residual, so nothing on
        // the board is resampled.
        var zoomed = new CameraShake();
        zoomed.Fire(new Vector2(0.37f, -0.61f), 5.0);
        bool whole = true;
        for (int i = 0; i < 40; i++)
        {
            zoomed.Update(tick);
            foreach (float z in new[] { 0.5f, 1.0f, 2.0f })
            {
                Vector2 px = zoomed.ScreenOffset(z) * z;
                whole &= Math.Abs(px.X - Mathf.Round(px.X)) < 1e-3f
                         && Math.Abs(px.Y - Mathf.Round(px.Y)) < 1e-3f;
            }
        }
        Check("the offset lands on whole screen pixels at every zoom", whole,
            "a fractional camera resamples every sprite on the board at once, "
            + "not one of them");

        var quiet = new CameraShake { Level = 0.0 };
        quiet.Fire(Vector2.Right, 6.0);
        for (int i = 0; i < 20; i++)
            quiet.Update(tick);
        Check("at level zero the view does not move at all",
            quiet.Offset.Length() < 1e-6f, $"{quiet.Offset}");
        var doubled = new CameraShake { Level = 2.0 };
        doubled.Fire(Vector2.Right, 6.0);
        double doubledPeak = 0.0;
        for (int i = 0; i < 60; i++)
        {
            doubled.Update(tick);
            doubledPeak = Math.Max(doubledPeak, Math.Abs(doubled.Offset.X));
        }
        Check("and the level scales it",
            Math.Abs(doubledPeak - 2.0 * peak) < 0.2,
            $"{doubledPeak:F2}px at 2.00x against {peak:F2}px at 1.00x");

        // Three tanks are on the board and any of them may fire, so what a
        // second shot must not do is *replace* the first. Two guns going off
        // together is the unambiguous case and it doubles.
        var together = new CameraShake();
        together.Fire(Vector2.Right, 6.0);
        together.Fire(Vector2.Right, 6.0);
        double togetherPeak = 0.0;
        for (int i = 0; i < 60; i++)
        {
            together.Update(tick);
            togetherPeak = Math.Max(togetherPeak, Math.Abs(together.Offset.X));
        }
        Check("two guns firing together shake twice as hard",
            Math.Abs(togetherPeak - 2.0 * peak) < 0.2,
            $"{togetherPeak:F2}px against {peak:F2}px for one");

        // Out of phase they partly cancel instead, and that is the spring being
        // a spring rather than a fault - an impulse against a velocity already
        // going the other way subtracts. Worth an assertion because the obvious
        // test ("a later shot always shakes more") is false, and asserting it
        // would have made the honest behaviour look broken.
        var lateShot = new CameraShake();
        lateShot.Fire(Vector2.Right, 6.0);
        for (int i = 0; i < 3; i++)
            lateShot.Update(tick);
        var freshSpring = new CameraShake();
        lateShot.Fire(Vector2.Right, 6.0);
        freshSpring.Fire(Vector2.Right, 6.0);
        double lateLeft = 0.0, restartLeft = 0.0;
        for (int i = 0; i < 60; i++)
        {
            lateShot.Update(tick);
            freshSpring.Update(tick);
            lateLeft = Math.Max(lateLeft, Math.Abs(lateShot.Offset.X));
            restartLeft = Math.Max(restartLeft, Math.Abs(freshSpring.Offset.X));
        }
        Check("a second gun mid ring-down adds to the spring, it does not reset it",
            Math.Abs(lateLeft - restartLeft) > 0.1,
            $"{lateLeft:F2}px carrying the first shot against {restartLeft:F2}px "
            + "from a clean spring - equal would mean the first was thrown away");

        Check("a heavier gun shakes the view harder",
            MovementProfile.Heavy.ShotShake > MovementProfile.Medium.ShotShake
            && MovementProfile.Medium.ShotShake > MovementProfile.Light.ShotShake
            && MovementProfile.Heavy.ShotShake
               / MovementProfile.Light.ShotShake > 2.0,
            string.Join(", ", MovementProfile.All
                .Select(p => $"{p.Tag} {p.ShotShake:F1}px")));

        // --- lending one tank's pixels to the others ----------------------
        //
        // The bench's answer to a layer that has landed on one tank and not the
        // others, which is the state it is in right now: MTP has a shadow and
        // 24 headings, LTP and HTP have neither. What it must not do is lend
        // the *class* along with the pixels - the whole value of it is that
        // three identical sprites at 0.85, 1.00 and 1.15 have nothing but the
        // scale to explain any difference between them.
        if (atlases is { Count: > 1 })
        {
            string lender = atlases.Keys.First();
            string borrower = atlases.Keys.First(k => k != lender);
            AtlasSet worn = AtlasSet.Load(
                AssetRoot.Sprites, borrower, lender);
            Check("a borrowed atlas keeps the tag it was asked for",
                worn.Error.Length == 0 && worn.Tag == borrower,
                $"asked for {borrower}, wearing {lender}, tag came back "
                + $"'{worn.Tag}'{(worn.Error.Length > 0 ? ": " + worn.Error : "")}");
            Check("and so still drives on its own class's numbers",
                MovementProfile.For(worn.Tag) == MovementProfile.For(borrower),
                "the pixels are lent, the class is not - otherwise the size "
                + "test compares two tanks that differ in more than size");
            Check("but it really is wearing the other tank's pixels",
                worn.Error.Length > 0
                || (worn.Count == atlases[lender].Count
                    && worn.HullSpan == atlases[lender].HullSpan),
                $"{worn.Count} headings and {worn.HullSpan}px of hull against "
                + $"{atlases[lender].Count} and {atlases[lender].HullSpan}");
        }

        // --- what the belts leave behind ----------------------------------
        //
        // The layer exists for the case a per-cell mark cannot express, so that
        // is what most of this is about: a tank turning on the spot never leaves
        // its cell, and the marks it makes are the whole point.
        Theme("track marks: the ruts the belts leave");
        {
            // The arithmetic first, with no tank in it at all. A belt one arm
            // from the centre covers the drive plus what the turn sweeps it
            // through - see TrackLoop.Split, which is static so this can be said
            // without driving anything into the state.
            (double Left, double Right) rutStill = TrackLoop.Split(0.0, 0.0, 40.0);
            Check("standing still, neither belt moves",
                rutStill.Left == 0.0 && rutStill.Right == 0.0);
            (double Left, double Right) ahead = TrackLoop.Split(10.0, 0.0, 40.0);
            Check("driving straight, both belts cover the same ground",
                Math.Abs(ahead.Left - ahead.Right) < 1e-9 && ahead.Left > 0.0);
            (double Left, double Right) spin = TrackLoop.Split(0.0, 30.0, 40.0);
            Check("a pivot moves both belts, opposite ways and equally",
                spin.Left < 0.0 && spin.Right > 0.0
                && Math.Abs(spin.Left + spin.Right) < 1e-9,
                $"{spin.Left:F2} and {spin.Right:F2} - one number for both belts "
                + "wound them the same way, which is the error this replaced");
            Check("a left turn drives the right belt forward",
                TrackLoop.Split(0.0, 5.0, 40.0).Right > 0.0,
                "headings rise counter-clockwise, so the outside of a left turn "
                + "is the right side");
            Check("a wider gauge sweeps its belts further",
                Math.Abs(TrackLoop.Split(0.0, 30.0, 80.0).Right)
                > Math.Abs(spin.Right));
            (double Left, double Right) back = TrackLoop.Split(-10.0, 0.0, 40.0);
            Check("reversing runs both belts backwards",
                back.Left < 0.0 && back.Right < 0.0);

            Check("the marks sit over the ground and under the ring",
                TrackMarks.MarksZ > HexField.GroundZ
                && TrackMarks.MarksZ < SelectionRing.GroundZ
                && SelectionRing.GroundZ < TankSprite.ShadowZ,
                $"ground {HexField.GroundZ}, ruts {TrackMarks.MarksZ}, ring "
                + $"{SelectionRing.GroundZ}, shadow {TankSprite.ShadowZ} - a rut "
                + "is terrain, and a shadow falls across one");

            if (vehicles is null || vehicles.Count == 0)
                Note("  ..    no vehicles to lay any");
            else
            {
                Vehicle car = vehicles[Math.Clamp(active, 0, vehicles.Count - 1)];
                var marks = new TrackMarks();
                double arm = car.Atlas.TrackArm * car.Sprite.BodyScale;

                // The one this layer is for. A per-cell mark and a mark keyed on
                // where the hull went both draw nothing here, because the hull
                // does not move - the same failure BeltTravel records having
                // made once with the belts themselves.
                car.Sprite.HullFacing = 270.0;
                for (int i = 0; i < 60; i++)
                    marks.Lay(car, TrackLoop.Split(0.0, 3.0, arm), 1.0 / 60.0);
                Check("turning on the spot leaves marks", marks.Count > 0,
                    "the hull does not move, so anything keyed on where it went "
                    + "lays nothing at all");

                IReadOnlyList<Vector2> leftArc = marks.TrailOf(car, 0);
                IReadOnlyList<Vector2> rightArc = marks.TrailOf(car, 1);
                Check("a pivot marks both sides", leftArc.Count > 1 && rightArc.Count > 1,
                    $"{leftArc.Count} and {rightArc.Count}");
                // Concentric: every point of a pivot arc is one arm from the
                // contact patch, whichever way round the hull got there.
                Vector2 rutHub = car.GroundPoint - marks.GlobalPosition;
                double rutWorst = 0.0;
                foreach (Vector2 p in leftArc)
                    rutWorst = Math.Max(rutWorst, Math.Abs(p.DistanceTo(rutHub) - arm));
                Check("the pivot arc stays one arm from the contact patch",
                    rutWorst < arm * 0.5,
                    $"off by {rutWorst:F1}px of a {arm:F1}px arm - the arcs are "
                    + "squashed by the camera, so this is loose on purpose");
                Check("the two sides are a gauge apart",
                    leftArc[^1].DistanceTo(rightArc[^1]) > arm,
                    $"{leftArc[^1].DistanceTo(rightArc[^1]):F1}px against an arm "
                    + $"of {arm:F1}");

                marks.Clear();
                Check("a repair takes the ruts with it", marks.Count == 0);

                // Off distance, not off the clock: the same ground covered in
                // half as many frames has to leave the same trail.
                var rutSlow = new TrackMarks();
                var rutFast = new TrackMarks();
                for (int i = 0; i < 40; i++)
                    rutSlow.Lay(car, (2.0, 2.0), 1.0 / 60.0);
                for (int i = 0; i < 20; i++)
                    rutFast.Lay(car, (4.0, 4.0), 1.0 / 60.0);
                Check("the trail is laid by distance, not by the frame clock",
                    Math.Abs(rutSlow.Count - rutFast.Count) <= 2,
                    $"{rutSlow.Count} against {rutFast.Count}");

                var rutIdle = new TrackMarks();
                for (int i = 0; i < 60; i++)
                    rutIdle.Lay(car, (0.0, 0.0), 1.0 / 60.0);
                Check("a tank that has not moved leaves two points, not a trail",
                    rutIdle.Count == 2, $"{rutIdle.Count}");

                // The rut lies in the ground plane, so it is at its widest when
                // the hull points up the screen and half that across it - the
                // same squash GroundDirection carries.
                var rutAcross = new TrackMarks();
                car.Sprite.HullFacing = 90.0;
                rutAcross.Lay(car, (4.0, 4.0), 1.0 / 60.0);
                double rutUp = rutAcross.WidthOf(car);
                car.Sprite.HullFacing = 0.0;
                rutAcross.Lay(car, (4.0, 4.0), 1.0 / 60.0);
                double rutSide = rutAcross.WidthOf(car);
                Check("a rut is squashed with the ground it lies on",
                    rutUp > rutSide * 1.5,
                    $"{rutUp:F1}px driving up the screen against {rutSide:F1}px across "
                    + "- a constant screen width made a 21px belt 106px wide");

                var rutFull = new TrackMarks();
                car.Sprite.HullFacing = 270.0;
                for (int i = 0; i < TrackMarks.Capacity * 3; i++)
                    rutFull.Lay(car, (TrackMarks.Stitch * 2.0, TrackMarks.Stitch * 2.0),
                             1.0 / 60.0);
                Check("the trail forgets its oldest rather than growing forever",
                    rutFull.TrailOf(car, 0).Count <= TrackMarks.Capacity,
                    $"{rutFull.TrailOf(car, 0).Count} of {TrackMarks.Capacity}");

                var rutLifted = new TrackMarks();
                rutLifted.Lay(car, (8.0, 8.0), 1.0 / 60.0);
                rutLifted.Lift(car);
                int rutBefore = rutLifted.TrailOf(car, 0).Count;
                rutLifted.Lay(car, (8.0, 8.0), 1.0 / 60.0);
                Check("lifting the pen still lays, it just does not join up",
                    rutLifted.TrailOf(car, 0).Count == rutBefore + 1,
                    "a reset or a park moves a tank without driving it, and a "
                    + "single ribbon would draw the jump as a line across the board");

                // A stitch is one link, so the trail is a list of shoe imprints
                // rather than a second sampling at a second spacing.
                var shod = new TrackMarks();
                car.Sprite.HullFacing = 90.0;
                for (int i = 0; i < 200; i++)
                    shod.Lay(car, (1.0, 1.0), 1.0 / 60.0);
                IReadOnlyList<Vector2> shoeWalk = shod.TrailOf(car, 0);
                double shoePitch = car.Atlas.TrackPitch * car.Sprite.BodyScale;
                Check("one stitch is one link of the belt",
                    shoeWalk.Count > 2 && Math.Abs(199.0 / (shoeWalk.Count - 1) - shoePitch)
                                      < shoePitch * 0.05,
                    $"{199.0 / Math.Max(shoeWalk.Count - 1, 1):F2}px between imprints "
                    + $"against a {shoePitch:F2}px link");

                IReadOnlyList<Vector2> shoes = shod.BarsOf(car, 0);
                Vector2 shoeAxis = car.Atlas.GroundDirection(90.0 + 90.0);
                Check("the shoe is laid across the belt, in the ground plane",
                    shoes.Count > 0
                    && Math.Abs(shoes[^1].Cross(shoeAxis)) < 0.01
                    && shoes[^1].Length() > 1.0,
                    $"{shoes[^1]} against {shoeAxis} - a bar is a length lying "
                    + "on the ground, so it foreshortens with the ground");

                // The belt rolls a corner over: a mark is left by 146px of track,
                // not by a dot, so the ribbon cannot carry a kink tighter than
                // the belt's half-length. This is the answer to taking the point
                // off the rear instead of the front - neither end is right alone,
                // because the hull turns about its centre and the two sweep
                // mirrored arcs.
                var rolled = new TrackMarks();
                car.Sprite.HullFacing = 270.0;
                for (int i = 0; i < 30; i++)
                {
                    car.Sprite.HullFacing += 4.0;
                    rolled.Lay(car, TrackLoop.Split(0.0, 4.0, arm), 1.0 / 60.0);
                }
                IReadOnlyList<Vector2> raw = rolled.TrailOf(car, 0);
                IReadOnlyList<Vector2> smooth = rolled.SmoothedOf(car, 0);
                Check("the smoothed path is the same length as the raw one",
                    raw.Count == smooth.Count, $"{raw.Count} and {smooth.Count}");
                double rawSpan = 0.0, smoothSpan = 0.0;
                for (int i = 1; i < raw.Count; i++)
                {
                    rawSpan += raw[i].DistanceTo(raw[i - 1]);
                    smoothSpan += smooth[i].DistanceTo(smooth[i - 1]);
                }
                Check("a turn is rolled over rather than followed to the point",
                    smoothSpan < rawSpan * 0.9,
                    $"{smoothSpan:F1}px of smoothed arc against {rawSpan:F1}px "
                    + "raw - a belt cannot lay a corner tighter than its own "
                    + "half-length");

                var straight = new TrackMarks();
                car.Sprite.HullFacing = 270.0;
                for (int i = 0; i < 200; i++)
                    straight.Lay(car, (1.0, 1.0), 1.0 / 60.0);
                IReadOnlyList<Vector2> plain = straight.TrailOf(car, 0);
                IReadOnlyList<Vector2> plainSmooth = straight.SmoothedOf(car, 0);
                double shoeDrift = 0.0;
                for (int i = 0; i < plain.Count; i++)
                    shoeDrift = Math.Max(shoeDrift, plain[i].DistanceTo(plainSmooth[i]));
                Check("a straight run is left where it was laid", shoeDrift < 0.01,
                    $"moved by {shoeDrift:F3}px - the smoothing is for corners, and a "
                    + "straight line has none");

                // What the 3D board reads, and the two positions are the point of
                // it: the ribbon takes the rolled path and the ladder the raw one.
                // Drawn on the rolled path the ladder collapses towards the middle
                // of its run, because the smoothing window is a belt long - which
                // is a rendering fault that is really a bookkeeping one.
                IReadOnlyList<TrackMarks.Imprint> printed = rolled.ImprintsOf(car, 0);
                bool paired = printed.Count == raw.Count;
                double apart = 0.0;
                for (int i = 0; paired && i < printed.Count; i++)
                {
                    paired &= printed[i].At == raw[i] && printed[i].Rolled == smooth[i];
                    apart = Math.Max(apart, printed[i].At.DistanceTo(printed[i].Rolled));
                }
                Check("an imprint carries both paths, the shoe's and the ribbon's",
                    paired && apart > 1.0,
                    $"{printed.Count} against {raw.Count}, furthest apart {apart:F1}px"
                    + (paired ? "" : " - and one of the two is not what it says"));

                // The height of the ground it was laid on, per imprint. Rebuilt from
                // the cell at draw time this would be wrong on a ramp, where the
                // height varies across the cell; taken off Vehicle.Standing it steps
                // in half levels and a climb comes out as two jumps rather than a
                // slope.
                float wasHigh = car.Height, wasGround = car.Ground;
                var climbed = new TrackMarks();
                car.Sprite.HullFacing = 90.0;
                for (int i = 0; i < 40; i++)
                {
                    car.Ground = i * 1.5f;
                    climbed.Lay(car, (2.0, 2.0), 1.0 / 60.0);
                }
                IReadOnlyList<TrackMarks.Imprint> ramped = climbed.ImprintsOf(car, 0);
                float lowest = float.MaxValue, highest = float.MinValue, jump = 0.0f;
                for (int i = 0; i < ramped.Count; i++)
                {
                    lowest = Math.Min(lowest, ramped[i].Lift);
                    highest = Math.Max(highest, ramped[i].Lift);
                    if (i > 0)
                        jump = Math.Max(jump, Math.Abs(ramped[i].Lift - ramped[i - 1].Lift));
                }
                Check("an imprint remembers how high the ground under it was",
                    ramped.Count > 2 && lowest < 1.0f && highest > 40.0f && jump < 12.0f,
                    $"{lowest:F1}..{highest:F1}px in steps of at most {jump:F1}");

                // The ground's height and not the tank's, which on a ramp are a
                // quarter of a level apart - see Vehicle.Ground. Laid at the
                // sprite's own height the trail sinks under the face it lies on and
                // the face hides it, which is what a ramp did to it.
                var apartHeights = new TrackMarks();
                car.Ground = 40.0f;
                car.Height = 0.0f;
                apartHeights.Lay(car, (2.0, 2.0), 1.0 / 60.0);
                IReadOnlyList<TrackMarks.Imprint> onGround =
                    apartHeights.ImprintsOf(car, 0);
                Check("and it is the ground's height, not the sprite's",
                    onGround.Count > 0 && Math.Abs(onGround[0].Lift - 40.0f) < 0.01f,
                    onGround.Count == 0 ? "nothing laid"
                        : $"laid at {onGround[0].Lift:F1} with ground 40 and sprite 0");
                car.Height = wasHigh;
                car.Ground = wasGround;

                // And the stage puts it back on that ground: the lift comes in
                // apart from the row, so a mark laid a level up is a level up in
                // the world and its screen row is untouched. The one thing a caller
                // of World can get wrong, and the trail is full of callers.
                Vector3 low = Stage3D.World(new Vector2(100.0f, 200.0f), 0.0f,
                                            0.5f, 0.866f);
                Vector3 high = Stage3D.World(new Vector2(100.0f, 200.0f + 64.0f),
                                             64.0f, 0.5f, 0.866f);
                Check("a rut laid a level up sits a level up, not a row back",
                    Math.Abs(high.Y - 64.0f / 0.866f) < 0.01f
                    && Math.Abs(high.Z - (low.Z + 64.0f / 0.5f)) < 0.01f
                    && Math.Abs(high.X - low.X) < 0.01f,
                    $"{low} against {high}");

                Check("and it sorts under the ring and under the tanks",
                    Stage3D.RutOrder < Stage3D.RingOrder && Stage3D.RingOrder < 0,
                    $"ruts {Stage3D.RutOrder}, ring {Stage3D.RingOrder} - the same "
                    + "statement the 2D z indices make, and on the stage it is the "
                    + "only one that can be made: these are transparent, so they "
                    + "cannot settle each other by depth");

                // Ground recovers, so the mark goes - and it goes by age rather
                // than by being pushed out of the buffer, which is what this did
                // first and which is not fading at all: a tank that drives and
                // stops leaves a trail nothing is pushing on.
                var weathered = new TrackMarks();
                car.Sprite.HullFacing = 270.0;
                for (int i = 0; i < 60; i++)
                    weathered.Lay(car, (4.0, 4.0), 1.0 / 60.0);
                IReadOnlyList<double> fresh = weathered.FadesOf(car, 0);
                Check("an older imprint is fainter than a newer one",
                    fresh.Count > 2 && fresh[0] < fresh[^1] && fresh[^1] > 0.98,
                    $"{fresh[0]:F3} at the head against {fresh[^1]:F3} at the tail");

                // Standing still with the belts stopped: nothing is laid, and
                // the clock still runs.
                for (int i = 0; i < (int)(TrackMarks.Life * 60) + 30; i++)
                    weathered.Lay(car, (0.0, 0.0), 1.0 / 60.0);
                Check("a trail older than its life is gone", weathered.Count == 0,
                    $"{weathered.Count} imprints outlived {TrackMarks.Life:F0}s");

                var frozen = new TrackMarks();
                for (int i = 0; i < 60; i++)
                    frozen.Lay(car, (4.0, 4.0), 1.0 / 60.0);
                frozen.Enabled = false;
                for (int i = 0; i < (int)(TrackMarks.Life * 60) + 30; i++)
                    frozen.Lay(car, (0.0, 0.0), 1.0 / 60.0);
                frozen.Enabled = true;
                Check("switching the layer off does not stop the ground healing",
                    frozen.Count == 0,
                    $"{frozen.Count} - the marks came back on unaged, which reads "
                    + "as the toggle having laid them");

                var rutOff = new TrackMarks { Enabled = false };
                for (int i = 0; i < 60; i++)
                    rutOff.Lay(car, (4.0, 4.0), 1.0 / 60.0);
                Check("switched off, nothing is laid at all", rutOff.Count == 0);
            }
        }

        // --- what the cells are made of -----------------------------------
        //
        // Skipped whole when there is no art, the way the sound topic is: the
        // files live outside the repository, and a bench without them is a bench
        // running on the rendered tile, not a broken one.
        if (field.Terrain is null || !field.Terrain.Any)
            Theme("terrain: no art loaded, running on the rendered tile");
        else
        {
            Theme("terrain: the kinds of ground on the board");
            TerrainSet set = field.Terrain;
            Rect2I hex = field.Atlas!.HexRect;
            string wasPaint = field.Paint;

            // The one that decides whether any of the rest means anything: the
            // hexagon's aspect ratio is the camera angle it was drawn at, and
            // the click arithmetic inverts the rendered tile's. Art at another
            // elevation does not look slightly wrong, it makes the hexagon on
            // screen stop being the cell under the mouse.
            Check("the art was drawn at the camera the tanks were rendered at",
                set.CameraAgrees(hex),
                $"plate {set.Plate.Size.X}x{set.Plate.Size.Y} is "
                + $"{set.CameraError(hex) * 100.0f:F1}% off the rendered "
                + $"{hex.Size.X}x{hex.Size.Y}");

            // A whole multiple of the frame, not the frame: art may be painted
            // finer than the template as long as it is the same drawing, and
            // "the same drawing" is exactly "the same factor in both axes" -
            // a different factor per axis is a different hexagon, which is a
            // different camera, which is the check above.
            Check("every kind is painted on the template's frame or a whole "
                  + "multiple of it",
                set.Names.All(n => set.Texture(n) is Texture2D art
                                   && art.GetSize()
                                      == (Vector2)set.Frame * set.Detail(n)),
                $"frame {set.Frame.X}x{set.Frame.Y}, kinds "
                + string.Join(", ", set.Names.Select(
                    n => $"{n} x{set.Detail(n)} {set.Texture(n)!.GetSize()}")));

            // What the ride is taken from, and it is taken from the picture: a
            // table of names would need editing every time somebody paints a hex,
            // and a kind missing from it would ride like nothing in particular.
            // Two halves. The ordering, because that is the whole claim - coarse
            // ground rides coarse - and a spread, because a set whose kinds all
            // ride alike is a feature that does nothing while looking present.
            var byGrain = set.Names.OrderBy(n => set.GrainOf(n)).ToList();
            var byRide = set.Names.OrderBy(n => set.RideOf(n)).ToList();
            Check("the ride a kind gives follows the grain of its art",
                byGrain.SequenceEqual(byRide),
                string.Join(", ", set.Names.Select(
                    n => $"{n} grain {set.GrainOf(n):F1} ride {set.RideOf(n):F2}")));
            Check("and the kinds on this board do not all ride alike",
                set.Names.Count < 2
                || set.RideOf(byRide[^1]) - set.RideOf(byRide[0]) > 0.1f,
                string.Join(", ", set.Names.Select(n => $"{n} {set.RideOf(n):F2}")));
            // Inside the band by construction, and worth saying because the cap is
            // a guard rather than the operating point: art nobody has drawn yet
            // cannot double the ride, and nothing on this board is near it.
            Check("every kind rides inside the band",
                set.Names.All(n => set.RideOf(n) >= TerrainSet.SmoothRide
                                   && set.RideOf(n) <= TerrainSet.RoughRide),
                string.Join(", ", set.Names.Select(n => $"{n} {set.RideOf(n):F2}")));
            // A forest cell is soil with trees on it: the kind has no art, so
            // there is nothing to measure and it rides as normal ground.
            Check("a kind with no art behind it rides at one",
                Math.Abs(set.RideOf(TerrainSet.Forest) - 1.0f) < 1e-6
                && Math.Abs(set.RideOf("no such hex") - 1.0f) < 1e-6,
                $"forest {set.RideOf(TerrainSet.Forest):F2}");

            // Not the silhouette's centre - the template's plate. A kind whose
            // grass grew would otherwise move its whole cell.
            field.Paint = set.Names[0];
            Rect2 drawn = set.RectAt(field.CellCentre(new Vector2I(3, 2)),
                                     set.ScaleTo(hex));
            Vector2 plate = drawn.Position
                + ((Vector2)set.Plate.Position + (Vector2)set.Plate.Size * 0.5f)
                  * set.ScaleTo(hex);
            Check("the plate lands on the cell centre",
                plate.DistanceTo(field.CellCentre(new Vector2I(3, 2))) < 0.01,
                $"{plate} against {field.CellCentre(new Vector2I(3, 2))}");

            // What allowing a finer kind rests on: it is drawn into the same
            // rectangle as the template, so its extra pixels land inside the
            // cell and nothing about the cell moves. A kind drawn at its own
            // scale instead would be four cells wide and read as a broken map.
            Check("a finer kind is drawn down by exactly its detail",
                set.Names.All(n => set.Texture(n)!.GetSize()
                                   * (set.ScaleTo(hex) / set.Detail(n))
                                   == drawn.Size),
                $"{drawn.Size} at scale {set.ScaleTo(hex):F4}");

            // Nearest minification is point sampling, and the board pans: art
            // drawn smaller than it was painted crawls without mipmaps. The one
            // consequence of detail that is not free, and it is off by default
            // for everything else in the project, so it goes quiet if dropped.
            Check("ground painted finer than the template is sampled with "
                  + "mipmaps",
                !set.AnyDetailed
                || field.TextureFilter
                   == CanvasItem.TextureFilterEnum.LinearWithMipmaps,
                $"detailed {set.AnyDetailed}, filter {field.TextureFilter}");

            misses = 0;
            for (int q = 0; q < field.Columns; q++)
            for (int r = 0; r < field.Rows; r++)
                if (field.TypeAt(new Vector2I(q, r)) != set.Names[0])
                    misses++;
            Check("painting one kind puts it on every cell", misses == 0,
                $"{misses} cells kept something else");

            field.Paint = TerrainSet.Mixed;
            var kinds = new HashSet<string>();
            for (int q = 0; q < field.Columns; q++)
            for (int r = 0; r < field.Rows; r++)
                kinds.Add(field.KindAt(new Vector2I(q, r)));
            var wanted = new HashSet<string>(set.Mixable);
            if (field.Trees)
                wanted.Add(TerrainSet.Forest);
            Check("mixed puts every kind a map is made of on the board",
                kinds.SetEquals(wanted),
                $"{string.Join(", ", kinds)} against {string.Join(", ", wanted)}");

            // The template is the frame the others are aligned to, and a board
            // that scattered bare plate among drawn ground would read as ground
            // that failed to draw. Still selectable by name - that is where it
            // is wanted, judging a kind against the plate it was painted over.
            Check("the bare template is not one of them",
                set.Names.Count <= 1 || !kinds.Contains(TerrainSet.Plain),
                $"{TerrainSet.Plain} appeared in a mix of "
                + string.Join(", ", set.Mixable));

            // Hashed off the cell, so this cannot fail by construction - and it
            // is asserted anyway for the reason the shell scatter is: --capture
            // fixes the time step so two runs can be diffed, and a board that
            // reshuffled itself would be measuring itself.
            Check("the mix is the same board twice",
                Enumerable.Range(0, field.Columns * field.Rows).All(i =>
                {
                    var cell = new Vector2I(i % field.Columns, i / field.Columns);
                    return field.TypeAt(cell) == field.TypeAt(cell);
                }));

            field.Paint = "no such ground";
            Check("a kind that did not load falls back to the mix",
                field.TypeAt(new Vector2I(0, 0)) != "no such ground"
                && set.Has(field.TypeAt(new Vector2I(0, 0))),
                $"got {field.TypeAt(new Vector2I(0, 0))}");
            field.Paint = wasPaint;

            // Drawn ground overhangs its cell - grass over the back edge, a tuft
            // off the front - so the paint order is now load-bearing where the
            // flat n-gon made it free. Furthest first, or the cell behind draws
            // its grass over the cell in front.
            IReadOnlyList<Vector2I> order = field.Ordered();
            Check("every cell is painted exactly once",
                order.Count == field.Columns * field.Rows
                && order.Distinct().Count() == order.Count,
                $"{order.Count} of {field.Columns * field.Rows}");
            float last = float.NegativeInfinity;
            misses = 0;
            foreach (Vector2I cell in order)
            {
                float y = field.CellAnchor(cell).Y;
                if (y < last)
                    misses++;
                last = y;
            }
            Check("the ground is painted back to front", misses == 0,
                $"{misses} cells came out of order - a staggered grid has no "
                + "nesting that walks it in screen order");
        }

        // --- what stands on the cells ---------------------------------------
        //
        // Skipped whole when nothing is sown, the way the terrain topic is: the
        // tree art lives outside the repository, and a bench without it is a
        // bench on open ground.
        if (grove is null || grove.Planted == 0)
            Theme("the grove: nothing sown, running on open ground");
        else
        {
            Theme("the grove: what stands on the cells");

            // First, and before anything here re-sows: the board as it stands
            // has to agree with the ground it says it is painted with. Trees
            // are nodes rather than pixels of the plate, so nothing about
            // redrawing the field touches them - a caller that changes the
            // paint and forgets to sow leaves woods on soil and soil where the
            // woods were, and every other check here would pass, because they
            // all plant first.
            var sownOn = new HashSet<Vector2I>(
                grove.Standing.Where(t => t.Tier.Name == PropTier.Trees)
                              .Select(t => t.Cell));
            var wooded = new HashSet<Vector2I>();
            for (int q = 0; q < field.Columns; q++)
            for (int r = 0; r < field.Rows; r++)
                if (grove.IsForest(new Vector2I(q, r)))
                    wooded.Add(new Vector2I(q, r));
            Check("the trees standing there are the ones the ground asks for",
                sownOn.SetEquals(wooded),
                $"{sownOn.Count} cells carry trees, {wooded.Count} are wooded");

            Check("every tree stands on a wooded cell",
                grove.Standing.Where(t => t.Tier.Name == PropTier.Trees)
                              .All(t => grove.IsForest(t.Cell)),
                $"{grove.Standing.Count(t => t.Tier.Name == PropTier.Trees
                                             && !grove.IsForest(t.Cell))} of "
                + $"{grove.Planted} grew on open ground");

            // And the general form of the same statement, which is the one that
            // survives a second tier: whatever a prop is, its cell has to have
            // asked for that tier. Written apart from the wood's because the two
            // fail differently - a bush on a cell whose budget has no bush row is
            // a table that did not reach the sower, and a tree on open ground is
            // the paint and the wood out of step.
            var misplaced = grove.Standing.Where(
                t => !grove.Growth(t.Cell).Any(g => g.Tier == t.Tier)).ToList();
            Check("every prop stands on ground its tier is budgeted for",
                misplaced.Count == 0,
                $"{misplaced.Count} of {grove.Planted}: "
                + string.Join(", ", misplaced.Take(3).Select(
                    t => $"{t.Tier.Name} on {field.KindAt(t.Cell)} {t.Cell}")));

            // The foot decides the cell, so a tree near a border belongs to the
            // hexagon it stands in - and to that one only. Planted per cell off
            // a global lattice, so this is what says the two agree.
            Check("a tree is planted by the cell it stands in",
                grove.Standing.All(t => field.CellAt(t.Ground) == t.Cell),
                $"{grove.Standing.Count(t => field.CellAt(t.Ground) != t.Cell)} "
                + "stand outside the cell that planted them");

            // The rule the whole arrangement rests on, and with no thicket to be
            // exempt there is now nothing it does not cover: every wooded cell
            // has a clearing in it, so every cell on the board is drivable.
            //
            // Asked of the tiers that claim it, which is not a loophole: a tier
            // that opts out is one a tank drives over rather than into, and
            // holding grass to the keep-out would clear a disc of turf out of
            // the middle of every cell. Both halves are asserted, because a
            // check that only asked the claimants would pass a board where every
            // tier had quietly opted out.
            float squash = grove.Squash;
            var intruders = grove.Standing.Where(t =>
            {
                if (t.Tier.Clearing <= 0.0)
                    return false;
                Vector2 centre = field.CellCentre(t.Cell);
                var a = new Vector2(t.Ground.X, t.Ground.Y / squash);
                var b = new Vector2(centre.X, centre.Y / squash);
                return a.DistanceTo(b) < grove.KeepOut * t.Tier.Clearing - 0.5;
            }).ToList();
            Check("nothing that a tank cannot drive over grows where it stands",
                intruders.Count == 0,
                $"{intruders.Count} inside the {grove.KeepOut:F0}px keep-out");

            Check("and the tier that stands in the way is one of them",
                grove.Standing.Any(t => t.Tier.Clearing > 0.0),
                "every tier on the board opted out of the keep-out, which is a "
                + "board with no clearing rule at all rather than a clear one");

            // The foot being inside was never enough - the base is wider than
            // the point it is measured at - and asking which cell the base's
            // horizontal extremes fall in was not enough either: a trunk one
            // pixel above the bottom edge passes that and is drawn standing on
            // the seam. Measured against the boundary itself, in every
            // direction, with the drawn seam's own width to spare.
            double root3 = Math.Sqrt(3.0);
            double radius = field.Atlas!.HexRect.Size.X * 0.5;
            float sq = grove.Squash;
            var straddling = grove.Standing.Where(t =>
            {
                if (!t.Tier.MindsBorder)
                    return false;
                Vector2 c = field.CellCentre(t.Cell);
                double dx = Math.Abs(t.Ground.X - c.X);
                double dy = Math.Abs((t.Ground.Y - c.Y) / sq);
                double edge = Math.Min(root3 * radius * 0.5 - dy,
                                       (root3 * radius - root3 * dx - dy) * 0.5);
                return edge < grove.Props!.RootOf(t.Species) * t.Pixels
                              + grove.Clearance
                              + t.Tier.Inset * root3 * radius * 0.5 - 0.5;
            }).ToList();
            Check("no tree stands on the border of its cell",
                straddling.Count == 0,
                $"{straddling.Count} are within their own base of the edge");

            // --- and a tier that fills its cell fills it ---------------------
            //
            // The complaint this answers looked like a distribution and was two
            // rules: the keep-out, which leaves a band 24 ground px deep out of
            // a 107px radius, and the ceiling's tie-break, which sorted equal
            // candidates by screen row and so kept the topmost - the back edge
            // of every cell, the same edge on every cell.
            //
            // Two numbers, because those are two different pictures and either
            // alone passes the other. A ring has its centroid dead on the cell
            // centre; a line banked against one edge has a perfectly ordinary
            // mean radius.
            double inradius = root3 * radius * 0.5;
            foreach (PropTier tier in grove.Props!.Tiers)
            {
                var mine = grove.Standing.Where(p => p.Tier == tier).ToList();
                if (mine.Count < 20)
                    continue;
                var offsets = mine.Select(p =>
                {
                    Vector2 c = field.CellCentre(p.Cell);
                    return new Vector2(p.Ground.X - c.X,
                                       (p.Ground.Y - c.Y) / squash);
                }).ToList();
                double meanRadius = offsets.Average(o => o.Length()) / inradius;
                double pull = (offsets.Aggregate(Vector2.Zero, (a, b) => a + b)
                               / offsets.Count).Length() / inradius;

                // Measured on the three tiers in hand: a tier spread over its
                // hexagon means 0.61 to 0.63 of the inradius, and one held out
                // of the tank's patch means 0.91 - the band it is allowed is 24
                // ground px deep out of 107, so a ring is all it can be. The
                // threshold between is not tuned, there is nothing in between.
                if (tier.Clearing <= 0.0)
                    Check($"the {tier.Name} tier spreads over its cells rather than ringing them",
                        meanRadius < 0.72,
                        $"mean radius {meanRadius:F2} of the inradius over "
                        + $"{mine.Count} props - a keep-out this tier honours "
                        + "leaves it nowhere but the rim");

                // And the other half, which every tier owes whatever its
                // clearing: a ring has its centroid dead on the cell centre, so
                // the measurement above cannot see a tier banked against one
                // edge - and banked against one edge is what the ceiling used to
                // produce, because it broke ties between equally-preferred spots
                // by screen row and so kept the topmost. Measured both ways on
                // this board: 0.76 to 0.78 of the inradius with that tie-break,
                // 0.07 to 0.14 with the hash that replaced it.
                //
                // Not zero, and it cannot be while the check above about props
                // standing in the cell that planted them fails: CellAt is a
                // screen picker, so a raised cell loses the candidates along its
                // near edge to the cell drawn in front of it, and every tier
                // leans the same few pixels away from the camera for it.
                Check($"and the {tier.Name} tier is not banked against one edge",
                    pull < 0.25,
                    $"centroid {pull:F2} of the inradius off centre over "
                    + $"{mine.Count} props");
            }

            // A wooded cell is wooded. The minimum overrides the edge fade,
            // which is a preference among legal spots, and cannot override the
            // keep-out or the border, which are what make the cell drivable and
            // the tree one cell's tree.
            //
            // Counted per (cell, tier), because the budget is: a cell holding
            // four trees and no bushes reads as full against a total and is
            // short of what its ground asked for.
            Dictionary<(Vector2I, PropTier), int> Census()
            {
                var by = new Dictionary<(Vector2I, PropTier), int>();
                foreach (PropNode p in grove.Standing)
                    by[(p.Cell, p.Tier)] = by.GetValueOrDefault((p.Cell, p.Tier)) + 1;
                return by;
            }

            Dictionary<(Vector2I, PropTier), int> perCell = Census();
            var thin = new List<string>();
            var overfull = new List<string>();
            for (int q = 0; q < field.Columns; q++)
            for (int r = 0; r < field.Rows; r++)
            {
                var cell = new Vector2I(q, r);
                foreach ((PropTier tier, int least, int most) in grove.Growth(cell))
                {
                    int got = perCell.GetValueOrDefault((cell, tier));
                    if (got < least)
                        thin.Add($"{cell} {tier.Name} {got}/{least}");
                    if (most > 0 && got > most)
                        overfull.Add($"{cell} {tier.Name} {got}/{most}");
                }
            }
            Check("every cell carries the floor its ground asks for, tier by tier",
                thin.Count == 0,
                $"{thin.Count} short: " + string.Join(", ", thin.Take(4)));

            Check("and none is over its ceiling",
                overfull.Count == 0,
                $"{overfull.Count} over: " + string.Join(", ", overfull.Take(4)));

            // Both ends move, and both still hold when they are set to the same
            // number - which is the case that catches a floor filled after the
            // ceiling was applied, or a ceiling that trims a cell back under
            // its floor. The wood's two ends specifically: they are the pair
            // that stayed fields, so they are the pair a slider can move.
            int wasLeast = grove.Minimum, wasMost = grove.Maximum;
            grove.Minimum = 6;
            grove.Maximum = 6;
            grove.Plant();
            var counted = Census()
                .Where(e => e.Key.Item2.Name == PropTier.Trees).ToList();
            Check("a floor equal to the ceiling puts exactly that many on a cell",
                counted.All(e => e.Value == 6),
                $"{counted.Count(e => e.Value != 6)} cells missed six");
            grove.Minimum = wasLeast;
            grove.Maximum = wasMost;
            grove.Plant();

            // A prop that never gets planted is one nobody notices is missing -
            // and with the spot choosing among the props that fit it, the way a
            // prop goes missing is by being too wide for every legal spot on
            // the board rather than by being left out of a list. Measured on
            // the four trees in hand: the bases want 11.3 to 18.3px of room
            // against a ring 24 deep at its narrowest, so the largest is at risk.
            //
            // Asked of the tiers the board actually grows. A tier with art and no
            // budget row is a different failure and gets its own check below -
            // folded in here it would read as art that is too wide, which is the
            // thing this one is for.
            var grown = new HashSet<int>(grove.Standing.Select(t => t.Species));
            var budgeted = new HashSet<PropTier>(grove.Standing.Select(t => t.Tier));
            var wanted = Enumerable.Range(0, grove.Props!.Count)
                .Where(k => budgeted.Contains(grove.Props.TierOf(k))).ToList();
            Check("every kind of prop its board grows is somewhere on it",
                grove.Planted < wanted.Count * 4
                || wanted.All(k => grown.Contains(k)),
                $"{grown.Count} of {wanted.Count} appeared in "
                + $"{grove.Planted} props; missing "
                + string.Join(", ", wanted.Where(k => !grown.Contains(k))
                    .Select(k => $"{grove.Props.NameOf(k)} "
                                 + $"(base {grove.Props.RootOf(k) * grove.ScaleOf(k):F1}px)")));

            // The quiet half: art on disk that no kind of ground carries. It
            // loads, it is counted in the note, and it never appears - which
            // from outside is indistinguishable from art that failed to load.
            var idleTiers = grove.Props.Tiers.Where(t => !budgeted.Contains(t)).ToList();
            Check("no tier loaded art that no ground has a budget for",
                idleTiers.Count == 0,
                $"{string.Join(", ", idleTiers.Select(t => t.Name))} loaded and never "
                + "planted - add a row to Grove.Budget or the folder is dead art");

            // Ground space, not screen: the lattice is square on the ground, and
            // measuring the spacing on screen would call every north-south pair
            // too close by the squash.
            //
            // Within a tier and never across one, because that is exactly what
            // the sowing promises: two props off the same jittered lattice
            // cannot meet, and two off different lattices have no relationship
            // for a lattice rule to assert. Asked across, this check would fail
            // on a correct board the moment a second tier existed - and the
            // temptation to loosen the bound until it passed would have thrown
            // away the guarantee it is here for.
            Vector2 Flat(PropNode p) => new(p.Ground.X, p.Ground.Y / squash);
            var tight = new List<string>();
            foreach (PropTier tier in grove.Props.Tiers)
            {
                var mine = grove.Standing.Where(p => p.Tier == tier).ToList();
                double near = double.MaxValue;
                for (int i = 0; i < mine.Count; i++)
                for (int j = i + 1; j < mine.Count; j++)
                    near = Math.Min(near, Flat(mine[i]).DistanceTo(Flat(mine[j])));
                // Half the step, because a cell short of its floor is gone over
                // again at half - so the guarantee is the finer lattice's, and
                // saying otherwise here would be asserting a spacing the sowing
                // does not promise.
                double gap = grove.StepOf(tier) * 0.5 * (1.0 - 2.0 * grove.Jitter);
                if (mine.Count > 1 && near < gap - 0.5)
                    tight.Add($"{tier.Name} {near:F1}px against {gap:F1}px");
            }
            Check("no two props of a tier are closer than its lattice allows",
                tight.Count == 0, string.Join(", ", tight));

            // And the rule that takes over where the lattice stops. Trees are
            // sown first into an empty board, so this asserts nothing about them
            // and everything about what comes after: a bush is refused a spot
            // whose base overlaps a trunk's, and there is nothing else in the
            // sowing that could have kept the two apart.
            var overlapping = new List<string>();
            foreach (PropNode a in grove.Standing)
            foreach (PropNode b in grove.Standing)
            {
                if (a.Tier == b.Tier)
                    continue;
                double reach = grove.Props.RootOf(a.Species) * a.Pixels
                               + grove.Props.RootOf(b.Species) * b.Pixels;
                if (Flat(a).DistanceTo(Flat(b)) < reach - 0.5)
                    overlapping.Add($"{a.Tier.Name} on {b.Tier.Name} at {a.Cell}");
            }
            Check("nothing of one tier stands in the base of another",
                overlapping.Count == 0,
                $"{overlapping.Count / 2} pairs overlap: "
                + string.Join(", ", overlapping.Take(4)));

            // The order the whole arrangement above rests on. Measured off the
            // art rather than declared, so a folder of saplings taller than the
            // trees reorders itself - and a list that had drifted out of height
            // order would put a trunk down on a bush with nothing to notice.
            Check("the tiers are sown tallest first",
                grove.Props.Tiers.Zip(grove.Props.Tiers.Skip(1)).All(p =>
                    grove.Props.Species(p.First).Max(k => grove.Props.RiseOf(k))
                    >= grove.Props.Species(p.Second).Max(k => grove.Props.RiseOf(k))),
                string.Join(" then ", grove.Props.Tiers.Select(t =>
                    $"{t.Name} {grove.Props.Species(t).Max(k => grove.Props.RiseOf(k)):F0}")));

            // The tallest tier is sown into an empty board, so what it plants
            // cannot depend on what comes after it. Asserted by taking the rest
            // of the budget away and re-sowing: every tree has to come back in
            // the same place, which is what says a folder of rocks appearing on
            // disk does not move a single trunk on a board rendered last year.
            var tallBefore = grove.Standing.Where(p => p.Tier == grove.Props.Tiers[0])
                              .Select(p => p.Ground).OrderBy(v => v.Y)
                              .ThenBy(v => v.X).ToList();
            var kept = grove.Budget.ToDictionary(e => e.Key, e => e.Value.ToList());
            foreach (List<Sprouts> rows in grove.Budget.Values)
                rows.RemoveAll(s => !string.Equals(s.Tier, grove.Props.Tiers[0].Name,
                                                   StringComparison.OrdinalIgnoreCase));
            grove.Plant();
            var alone = grove.Standing.Select(p => p.Ground).OrderBy(v => v.Y)
                             .ThenBy(v => v.X).ToList();
            Check("the tallest tier stands where it would with no other tier at all",
                tallBefore.Count == alone.Count
                && tallBefore.Zip(alone).All(p => p.First.DistanceTo(p.Second) < 0.001f),
                $"{tallBefore.Count} with the rest of the budget, {alone.Count} without");
            foreach ((string kind, List<Sprouts> rows) in kept)
                grove.Budget[kind] = rows;
            grove.Plant();

            // --- the specimen cell, and which tiers take part in it ----------
            //
            // Specimen is the wood bench's picture rather than a board's, so it
            // is asserted here and looked at there. Both halves, because each is
            // the other's failure wearing a disguise: a Specimen that quietly
            // dropped the undergrowth reads exactly like a bench cell whose
            // bushes found no spot they fit, and a TreesOnly that did nothing
            // reads exactly like a flag that never arrived.
            //
            // Against what the board budgets rather than against every tier that
            // loaded: a tier no ground carries is a table's decision, and the
            // sower is not the thing to fail for it.
            var offered = new HashSet<PropTier>();
            for (int q = 0; q < field.Columns; q++)
            for (int r = 0; r < field.Rows; r++)
                foreach ((PropTier t, int _, int _) in grove.Growth(new Vector2I(q, r)))
                    offered.Add(t);
            grove.Specimen = true;
            grove.Plant();
            var catalogued = new HashSet<PropTier>(grove.Standing.Select(p => p.Tier));
            grove.TreesOnly = true;
            grove.Plant();
            var wooded2 = new HashSet<PropTier>(grove.Standing.Select(p => p.Tier));
            grove.Specimen = false;
            grove.TreesOnly = false;
            grove.Plant();
            Check("one of each species means one of each tier, unless asked otherwise",
                catalogued.SetEquals(offered)
                && wooded2.Count == 1
                && wooded2.Single().Name == PropTier.Trees,
                $"catalogue stood {catalogued.Count} of the {offered.Count} tiers "
                + $"this board offers, trees-only stood "
                + string.Join(", ", wooded2.Select(t => t.Name)));

            // Same rule as a vehicle's, in the same space - that is what lets a
            // tree and a tank sort against each other at all.
            // Of what still sorts. The dressing gave the sort up rather than got
            // it wrong, and the two checks below it are its own, three down.
            var sorting = grove.Standing.Where(p => !p.Tier.UnderTanks).ToList();
            Check("a tree's depth is its foot",
                sorting.All(t => t.ZIndex == Mathf.RoundToInt(t.Ground.Y)),
                "depth off anything but the foot puts a leaning canopy in front "
                + "of what it grows behind");
            Check("a tree in front of another draws over it",
                sorting.Zip(sorting.Skip(1))
                     .All(p => p.First.ZIndex <= p.Second.ZIndex),
                "the planted list is kept in depth order so a tie resolves back "
                + "to front rather than by which cell was walked first");

            // Not decoration and not an effect: a tree is an object on the
            // ground, so it belongs over the ruts and the ring rather than
            // among them.
            Check("trees stand over the ground marks", grove.Standing.All(
                    t => t.ZIndex > TrackMarks.MarksZ
                         && t.ZIndex > SelectionRing.GroundZ),
                "a trunk under the paint is paint");

            // --- and the dressing under everything that stands ---------------
            //
            // A tier that keeps none of the tank's patch stands in the middle of
            // the cell a tank parks in, where sorting the two by their feet has
            // no honest answer - half the scatter over the hull and half under
            // it, reshuffling as it turns. So the tier leaves the sort and takes
            // a band. Asserted at both edges, because a band with only one is
            // a number: over the marks pressed into the dirt, under everything
            // that stands on it.
            Check("a tier that stands where a tank parks gives up the depth sort",
                grove.Props.Tiers.All(t => t.Clearing > 0.0 || t.UnderTanks),
                string.Join(", ", grove.Props.Tiers
                    .Where(t => t.Clearing <= 0.0 && !t.UnderTanks)
                    .Select(t => t.Name))
                + " keeps none of the keep-out and still sorts by its foot");

            // Banded first, because the band is a state a tank puts a prop in
            // rather than a constant it is planted with - see Grove.Reveal. What
            // is asserted is unchanged: while it is in the band it is under
            // everything that stands, and the check above is what says which
            // tiers are entitled to be there at all.
            var hulls = new HashSet<Vector2I>(grove.Standing.Select(p => p.Cell));
            grove.Reveal(hulls, 1.0 / 60.0);
            Check("the dressing draws under every tree and every tank",
                grove.Standing.Where(p => p.Tier.UnderTanks)
                     .All(p => p.ZIndex == Grove.DressZ)
                && grove.Standing.Where(p => !p.Tier.UnderTanks)
                        .All(p => p.ZIndex > Grove.DressZ)
                && vehicles.All(v => v.Sprite.ZIndex > Grove.DressZ),
                $"band at {Grove.DressZ}, "
                + $"{grove.Standing.Count(p => p.Tier.UnderTanks)} props in it");
            grove.Reveal(new HashSet<Vector2I>(), 10.0);

            Check("and over the marks pressed into the ground",
                Grove.DressZ > TrackMarks.MarksZ
                && Grove.DressZ > SelectionRing.GroundZ
                && Grove.DressZ > TankSprite.ShadowZ,
                $"{Grove.DressZ} against ruts {TrackMarks.MarksZ}, ring "
                + $"{SelectionRing.GroundZ}, shadow {TankSprite.ShadowZ} - a "
                + "stone under the dirt it stands on is dirt");

            // The band only settles the dressing against what stands; two stones
            // still settle against each other, and they do it by falling back to
            // tree order, which Plant leaves in depth order. Nothing sets that
            // twice, so this is the check that it was set once.
            var dressed = grove.Standing.Where(p => p.Tier.UnderTanks).ToList();
            Check("and two of it still settle each other by depth",
                dressed.Zip(dressed.Skip(1)).All(p =>
                    p.First.Depth <= p.Second.Depth
                    && p.First.GetIndex() < p.Second.GetIndex()),
                "sharing a z-index, siblings draw in tree order - so the planted "
                + "list being sorted by depth is the whole of their sort");

            // The same statement on the stage, where the ladder is render
            // priority rather than z-index and the rungs are named apart for it.
            Check("the stage puts the dressing under what stands, and both over the ground",
                Stage3D.DressOrder < Stage3D.StandOrder
                && Stage3D.DressOrder > Stage3D.RingOrder
                && Stage3D.RingOrder > Stage3D.WaterOrder
                && Stage3D.WaterOrder > Stage3D.ShadowOrder
                && Stage3D.ShadowOrder > Stage3D.RutOrder,
                $"stand {Stage3D.StandOrder}, dress {Stage3D.DressOrder}, ring "
                + $"{Stage3D.RingOrder}, water {Stage3D.WaterOrder}, shadow "
                + $"{Stage3D.ShadowOrder}, rut {Stage3D.RutOrder}");

            // --- and each tier by its own rules -----------------------------
            //
            // The flags are copied onto the node at planting - see the sowing -
            // so the first thing to say is that the copy is the tier's. A copy
            // that went stale would leave a rock swaying and every check below
            // it agreeing, because they all read the same copy.
            Check("a prop carries its own tier's rules",
                grove.Standing.All(p => p.Sways == p.Tier.Family.Sways
                                        && p.Fades == p.Tier.Fades),
                $"{grove.Standing.Count(p => p.Sways != p.Tier.Family.Sways || p.Fades != p.Tier.Fades)}"
                + " of the planted disagree with the tier they were planted from");

            // Both halves, and the second is the one that keeps the first
            // honest: a Blow that returned early would leave every prop at zero
            // lean and pass "the rock does not move" outright.
            double wasGale = grove.Wind;
            grove.Wind = 8.0;
            grove.Calm();
            for (int i = 0; i < 30; i++)
                grove.Blow(1.0 / 60.0);
            Check("the wind moves what sways and nothing else",
                grove.Standing.Where(p => !p.Sways).All(p => p.Lean == 0.0f)
                && grove.Standing.Any(p => p.Sways && Math.Abs(p.Lean) > 1e-4f),
                $"{grove.Standing.Count(p => !p.Sways && p.Lean != 0.0f)} still "
                + $"props leaning, {grove.Standing.Count(p => p.Sways && Math.Abs(p.Lean) > 1e-4f)}"
                + $" of {grove.Standing.Count(p => p.Sways)} swaying ones bent");
            grove.Wind = wasGale;
            grove.Calm();
            grove.Blow(1.0 / 60.0);

            // The same shape for the fade, and the same reason for asserting
            // both ends: every cell is occupied here, so a Reveal that did
            // nothing at all would pass the half about the kerbstone.
            var everywhere = new HashSet<Vector2I>(grove.Standing.Select(p => p.Cell));
            for (int i = 0; i < 60; i++)
                grove.Reveal(everywhere, 1.0 / 60.0);
            Check("a tank fades what could have hidden it and nothing else",
                grove.Standing.Where(p => !p.Fades).All(p => p.Modulate.A >= 0.999f)
                && grove.Standing.Where(p => p.Fades)
                        .All(p => p.Modulate.A <= grove.Ghost + 0.01f),
                $"{grove.Standing.Count(p => !p.Fades && p.Modulate.A < 0.999f)} "
                + "that could hide nothing went translucent");
            grove.Reveal(new HashSet<Vector2I>(), 10.0);

            // --- and the other half of what a tank on the cell does -------
            //
            // The dressing band. It was a tier constant, so a bush was under
            // every trunk on every board for good - measured on the wood bench,
            // a bush standing 10.7 ground units nearer the camera than the trunk
            // beside it and drawn under it. The band gave up sorting against a
            // hull, so it is off while there is no hull.
            //
            // Three parts, and the middle one is the whole point: without it a
            // Reveal that simply never banded anything would pass both ends.
            var undergrowth = grove.Standing.Where(p => p.Tier.UnderTanks).ToList();
            var uprights = grove.Standing.Where(p => !p.Tier.UnderTanks).ToList();
            grove.Reveal(new HashSet<Vector2I>(), 1.0 / 60.0);
            bool freeByFeet = undergrowth.All(
                p => !p.Dressed && p.ZIndex == Mathf.RoundToInt(p.Depth));
            grove.Reveal(everywhere, 1.0 / 60.0);
            bool sunkUnderHulls = undergrowth.All(
                p => p.Dressed && p.ZIndex == Grove.DressZ);
            bool treesNeverSink = uprights.All(
                p => !p.Dressed && p.ZIndex == Mathf.RoundToInt(p.Depth));
            grove.Reveal(new HashSet<Vector2I>(), 10.0);
            Check("undergrowth sorts by its feet until a tank stands on its cell",
                undergrowth.Count > 0 && uprights.Count > 0
                && freeByFeet && sunkUnderHulls && treesNeverSink,
                $"{undergrowth.Count} dressing props, {uprights.Count} standing: "
                + $"by feet when clear {freeByFeet}, banded under hulls "
                + $"{sunkUnderHulls}, and the wood never banded {treesNeverSink}");

            // And the stage reads the same flag rather than the tier, because a
            // priority beats depth: a second copy of this decision over there is
            // a board where the two layers disagree about which is in front.
            PropNode probe = undergrowth[0];
            probe.Dressed = false;
            int loose = Stage3D.RungFor(probe);
            probe.Dressed = true;
            int banded = Stage3D.RungFor(probe);
            probe.Dressed = false;
            Check("and the stage takes its rung from that flag, not from the tier",
                Stage3D.DressOrder < Stage3D.StandOrder
                && loose == Stage3D.StandOrder && banded == Stage3D.DressOrder,
                $"unbanded sits at {loose}, banded at {banded} - a rung that did "
                + "not move is a board where the two layers disagree on which "
                + "prop is in front, because a priority beats depth");

            // --- and the same wood, drawn by the stage --------------------
            //
            // The billboards are these trees put where the depth buffer can judge
            // them, so what has to hold is that nothing moved. Asserted against
            // the projection rather than against the expression that implements
            // it - Relief3D's rule, and the reason Trunk takes its two camera
            // terms rather than reading a board.
            const float squashed = 0.5077f, risen = 0.8616f;
            float Row(Vector3 at) => at.Z * squashed - at.Y * risen;
            PropNode planted = grove.Standing[0];
            float storey = field.LevelAt(planted.Cell) * field.Lift;
            Vector3 root = Stage3D.Trunk(planted.Ground, storey, 0.0f,
                                         squashed, risen).Origin;
            Check("a tree on the stage stands on the row it was planted on",
                Math.Abs(Row(root) - planted.Ground.Y) < 0.01f,
                $"row {Row(root):F2} against {planted.Ground.Y:F2} - the drawn "
                + "row carries the lift already, so reading it as a flat one "
                + "stands a tree on a plateau a level too far up the board; and "
                + "the clearance off the face is up-and-back, so it must cost no "
                + "screen row at all");
            // A level up is a level up, not a row further off - the same thing
            // the ruts have to get right, and the same pair to get wrong.
            // Against the tree on the floor rather than against the lift itself,
            // because both carry the clearance off their own face and the
            // difference is what the level is worth.
            Vector3 upstairs = Stage3D.Trunk(planted.Ground, field.Lift, 0.0f,
                                             squashed, risen).Origin;
            Vector3 downstairs = Stage3D.Trunk(planted.Ground, 0.0f, 0.0f,
                                               squashed, risen).Origin;
            Check("and a tree a level up is a level up in the world",
                Math.Abs((upstairs.Y - downstairs.Y) * risen - field.Lift) < 0.01f
                && Math.Abs(Row(upstairs) - Row(downstairs)) < 0.01f,
                $"{(upstairs.Y - downstairs.Y) * risen:F2} of height against a "
                + $"{field.Lift:F2} lift, on the same drawn row");

            // The lean, as the matrix carries it: a point a hundred screen px up
            // drifts by a hundred times the shear, and the foot does not move.
            // The shear is quoted in screen px per screen px, so the rise in the
            // basis is the whole of the conversion - and dropping it would leave
            // a wood that leans by cos(e) of what the wind says while every check
            // on Grove's side still passed.
            Transform3D leaning3 = Stage3D.Trunk(planted.Ground, storey, 0.05f,
                                                 squashed, risen);
            Vector3 head = leaning3 * new Vector3(0.0f, 100.0f / risen, 0.0f);
            Check("the stage leans a tree by what the wind says, about its foot",
                Math.Abs(head.X - leaning3.Origin.X - 5.0f) < 0.01f
                && Math.Abs(Row(head) - Row(leaning3.Origin) + 100.0f) < 0.01f
                && (Stage3D.Trunk(planted.Ground, storey, 0.0f, squashed, risen)
                    * new Vector3(0.0f, 100.0f / risen, 0.0f)).X
                   == leaning3.Origin.X,
                $"{head.X - leaning3.Origin.X:F2}px of drift 100px up at a 0.05 "
                + "shear, and a hundred screen px of height still a hundred");

            // The art keeps the size it is drawn at, which is what makes the
            // stage a different way of drawing the same wood rather than a
            // second opinion about how big a tree is.
            Aabb quad = Stage3D.Stem(planted.Foot * planted.Pixels,
                                     planted.Size * planted.Pixels, risen)
                               .GetAabb();
            Check("and the quad is the size the tree is drawn at",
                Math.Abs(quad.Size.X - planted.Size.X * planted.Pixels) < 0.01f
                && Math.Abs(quad.Size.Y * risen
                            - planted.Size.Y * planted.Pixels) < 0.01f
                && Math.Abs(quad.End.Y * risen
                            - planted.Foot.Y * planted.Pixels) < 0.01f,
                $"{quad.Size.X:F1} wide and {quad.Size.Y * risen:F1} screen px "
                + $"tall against {planted.Size.X * planted.Pixels:F1} by "
                + $"{planted.Size.Y * planted.Pixels:F1}");

            // Over the ground marks on the stage too, where the statement is a
            // render priority rather than a z index: the decals are negative and
            // a tree is at the default, which is also where the tanks are - so a
            // tree and a tank settle each other by their feet and neither is
            // paint.
            Check("and it stands over them on the stage as well",
                Stage3D.RutOrder < 0 && Stage3D.RingOrder < 0,
                $"ruts {Stage3D.RutOrder}, ring {Stage3D.RingOrder} against a "
                + "tree at 0");

            // --- and the same tree laid down the sun ----------------------
            //
            // The shadow is the standing quad through one more map, so what has
            // to hold is what that map claims: it starts where the tree does, it
            // has no height left, and what height it had went down-sun by
            // cot(elevation). Asserted against Sun itself rather than against
            // the numbers SunCast happens to hold, because a cast length that
            // has drifted off the sun is a board lit by two of them.
            Vector3 toSun = Stage3D.Sun;
            float cot = new Vector2(toSun.X, toSun.Z).Length() / toSun.Y;
            Check("the cast a shadow is laid along is the sun's own",
                Math.Abs(Stage3D.SunCast.Length() - cot) < 1e-4f
                && Stage3D.SunCast.X * toSun.X
                   + Stage3D.SunCast.Y * toSun.Z < 0.0f,
                $"cast {Stage3D.SunCast.Length():F4} against cot(elevation) "
                + $"{cot:F4}, running {(Stage3D.SunCast.X * toSun.X + Stage3D.SunCast.Y * toSun.Z < 0.0f ? "away from" : "toward")} "
                + "the sun - and away is the only one of those that is a shadow");

            Transform3D upright = Stage3D.Trunk(planted.Ground, storey, 0.0f,
                                              squashed, risen);
            Transform3D flattened = Stage3D.Cast(planted.Ground, storey, 0.0f,
                                            squashed, risen, Stage3D.SunCast);
            Check("a tree's shadow starts at the tree's own foot",
                flattened.Origin.IsEqualApprox(upright.Origin),
                $"{flattened.Origin} against {upright.Origin} - a shadow that begins "
                + "anywhere else is a shadow of something that is not there");

            // A hundred screen px of crown, and where it lands. Flat is the
            // first half and the whole reason this is a transform: any height
            // left in it would put the mark above the ground it is painted on.
            var aloft = new Vector3(0.0f, 100.0f / risen, 0.0f);
            Vector3 landed = flattened * aloft;
            float run = 100.0f / risen;
            Check("and the crown lands flat on the ground, down the sun",
                Math.Abs(landed.Y - flattened.Origin.Y) < 1e-3f
                && Math.Abs(landed.X - flattened.Origin.X
                            - run * Stage3D.SunCast.X) < 0.01f
                && Math.Abs(landed.Z - flattened.Origin.Z
                            - run * Stage3D.SunCast.Y) < 0.01f,
                $"{landed.Y - flattened.Origin.Y:F3} of height left, and "
                + $"({landed.X - flattened.Origin.X:F1}, {landed.Z - flattened.Origin.Z:F1}) "
                + $"of run against ({run * Stage3D.SunCast.X:F1}, "
                + $"{run * Stage3D.SunCast.Y:F1})");

            // The board's own decision, and the one thing here a reader cannot
            // get back from a still frame: a sign flip in either term is a sun
            // moved to the other side, and the picture it makes is plausible.
            Check("and it falls toward the viewer and to the right",
                landed.X > flattened.Origin.X
                && Row(landed) > Row(flattened.Origin),
                $"{landed.X - flattened.Origin.X:F1}px across and "
                + $"{Row(landed) - Row(flattened.Origin):F1} down the screen - a sun on "
                + "the camera's side throws both the other way, and the shadow "
                + "goes under the crown that cast it");

            // The gust rides along for free, and that it does is the whole
            // argument for folding the flattening into the same basis: a shadow
            // that stayed put while its tree bent is two trees.
            Transform3D gusted = Stage3D.Cast(planted.Ground, storey, 0.05f,
                                            squashed, risen, Stage3D.SunCast);
            Check("and it bends with the tree in a gust",
                Math.Abs((gusted * aloft).X - landed.X - 5.0f) < 0.01f
                && gusted.Origin.IsEqualApprox(flattened.Origin),
                $"{(gusted * aloft).X - landed.X:F2}px of extra drift 100px up at a "
                + "0.05 shear, about a foot that has not moved");

            Check("and a shadow lies over the ruts and under the ring",
                Stage3D.RutOrder < Stage3D.ShadowOrder
                && Stage3D.ShadowOrder < Stage3D.RingOrder,
                $"ruts {Stage3D.RutOrder}, shadow {Stage3D.ShadowOrder}, ring "
                + $"{Stage3D.RingOrder} - a rut drawn over its own shadow is a "
                + "belt mark that glows out of the dark");

            // --- how much of a sprite is height ---------------------------
            //
            // The one number in a tier that no measurement can produce, so what
            // is asserted is that it behaves like the share it claims to be. A
            // value outside (0,1] is not a share: above one throws a shadow
            // longer than the sprite is tall, and zero or below is a prop with
            // no shadow at all, which is what a missing entry looks like.
            Check("every tier throws a share of what is drawn, and only a share",
                PropTier.Known.Values.All(t => t.Stands > 0.0 && t.Stands <= 1.0),
                string.Join(", ", PropTier.Known.Values.Select(
                    t => $"{t.Name} {t.Stands:F2}"))
                + $"; unknown {PropTier.Unknown(PropFamily.Known[PropFamily.Vegetation], "x", 0).Stands:F2}");

            // The whole of it, and it has to be: every board rendered before
            // there was a share was rendered at one, so anything else here is a
            // wood whose shadows moved without anybody asking.
            Check("and a tree throws the whole of it",
                PropTier.Known[PropTier.Trees].Stands == 1.0,
                $"{PropTier.Known[PropTier.Trees].Stands:F2} - the arrangement "
                + "the cast was designed for, and the one every existing board "
                + "was drawn with");

            // The order is the argument: a tree is height, a bush is about as
            // deep as it is tall, a stone is wider than it is high and most of
            // what is drawn above its near edge is its own far edge lying on the
            // same ground. Ordered rather than pinned to three numbers, because
            // the numbers are a judgement and the order is not.
            Check("and the flatter a tier lies, the less of it it throws",
                PropTier.Known[PropTier.Trees].Stands
                    > PropTier.Known[PropTier.Bushes].Stands
                && PropTier.Known[PropTier.Bushes].Stands
                    > PropTier.Known[PropTier.Rocks].Stands,
                $"tree {PropTier.Known[PropTier.Trees].Stands:F2}, bush "
                + $"{PropTier.Known[PropTier.Bushes].Stands:F2}, rock "
                + $"{PropTier.Known[PropTier.Rocks].Stands:F2}");

            // What the share does to the map, and the half of it that must not
            // move: the run shortens by exactly the share and the foot does not
            // budge. A share applied to the origin instead would slide every low
            // prop off its own shadow, which is the fault this was written to
            // remove rather than a new way of having it.
            Transform3D shortened = Stage3D.Cast(planted.Ground, storey, 0.0f,
                                            squashed, risen, Stage3D.SunCast,
                                            0.45f);
            Vector3 nearer = shortened * aloft;
            Check("a smaller share puts the same crown nearer the same foot",
                shortened.Origin.IsEqualApprox(flattened.Origin)
                && Math.Abs((nearer.X - shortened.Origin.X)
                            - 0.45f * (landed.X - flattened.Origin.X)) < 0.01f
                && Math.Abs((nearer.Z - shortened.Origin.Z)
                            - 0.45f * (landed.Z - flattened.Origin.Z)) < 0.01f,
                $"{nearer.X - shortened.Origin.X:F1} across at 0.45 against "
                + $"{landed.X - flattened.Origin.X:F1} at one, about a foot "
                + $"{(shortened.Origin - flattened.Origin).Length():F3} away");

            // And the half of the basis the share must keep its hands off. The
            // lean is a real sideways drift of a point the sprite has already
            // put at that height, so it moves the shadow by the same amount
            // whatever the thing is made of - scale it too and a stone in a gust
            // bends less than the stone it is the shadow of.
            Transform3D shortGust = Stage3D.Cast(planted.Ground, storey, 0.05f,
                                            squashed, risen, Stage3D.SunCast,
                                            0.45f);
            Check("and the wind still bends it by the whole of the lean",
                Math.Abs((shortGust * aloft).X - nearer.X - 5.0f) < 0.01f,
                $"{(shortGust * aloft).X - nearer.X:F2}px of extra drift 100px "
                + "up at a 0.05 shear, the same 5 the full share gets");

            // --- and the ground under the foot ----------------------------
            //
            // A second claim about the same prop, so the first thing to hold is
            // that it is made about the same point. All three transforms come
            // off one expression for exactly this reason; a patch that worked
            // its own origin out is a second answer waiting to disagree.
            float pool = Stage3D.Seating(planted.Root, planted.Rise, planted.Spread);
            Transform3D seated = Stage3D.Footing(planted.Ground, storey,
                                                 squashed, risen, pool);
            Check("the patch under a prop sits on the prop's own foot",
                seated.Origin.IsEqualApprox(upright.Origin),
                $"{seated.Origin} against {upright.Origin}");

            // Flat, and flat is the whole reason this is a transform too: any
            // height left in it puts the patch above the ground it is painted
            // on, and the coin toss that follows is hit_scar's.
            Vector3[] rim =
            {
                seated * new Vector3(-1.0f, -1.0f, 0.0f),
                seated * new Vector3(1.0f, -1.0f, 0.0f),
                seated * new Vector3(1.0f, 1.0f, 0.0f),
                seated * new Vector3(-1.0f, 1.0f, 0.0f),
            };
            Check("and lies in the ground, with no height left in it",
                rim.All(c => Math.Abs(c.Y - seated.Origin.Y) < 1e-3f),
                string.Join(", ", rim.Select(
                    c => $"{c.Y - seated.Origin.Y:F3}")) + " of height at the rim");

            // Round on the ground, which is what makes it an ellipse on the
            // screen without anybody squashing it by hand. Written the other way
            // round it would need this class's own squash quoted twice, and the
            // day the tile is re-rendered one of the two would be stale.
            float wide = rim[1].X - rim[0].X;
            float deep = rim[1].Z - rim[2].Z;
            float tall = Row(rim[1]) - Row(rim[2]);
            Check("and is a disc on the ground, so an ellipse on the screen",
                Math.Abs(wide - deep) < 1e-3f
                && Math.Abs(tall / wide - squashed) < 1e-3f,
                $"{wide:F1} by {deep:F1} on the ground, {wide:F1} by {tall:F1} "
                + $"on the screen - {tall / wide:F4} against a squash of "
                + $"{squashed:F4}");

            // Both terms, and each one asserted by what it is for. The contact
            // is where the thing touches and it is measured; the reach is how
            // far the darkening carries and it comes off the height.
            Check("and it is the measured contact widened by the prop's height",
                Stage3D.Seating(planted.Root, 0.0f, 0.0f) == planted.Root
                && Stage3D.Seating(planted.Root, 100.0f, 0.0f)
                    > Stage3D.Seating(planted.Root, 50.0f, 0.0f)
                && Stage3D.Seating(10.0f, planted.Rise, 0.0f)
                    > Stage3D.Seating(4.0f, planted.Rise, 0.0f),
                $"{planted.Root:F1}px of contact and {planted.Rise:F0}px of "
                + $"height make a {pool:F1}px radius");

            // And the floor under both, which is what a thing lying on the
            // ground gets instead. A max rather than a sum: the two are the same
            // question answered from opposite ends, and a max is also the only
            // form that cannot shrink a patch that already looked right.
            Check("and never smaller than what the prop has on the ground",
                Stage3D.Seating(2.0f, 10.0f, 9.0f) == 9.0f
                && Stage3D.Seating(planted.Root, planted.Rise, 0.0f)
                    == Stage3D.Seating(planted.Root, planted.Rise,
                                       planted.Root * 0.5f)
                && Stage3D.Seating(2.0f, 10.0f, 9.0f)
                    < Stage3D.Seating(2.0f, 10.0f, 9.0f) + 2.0f,
                $"a 2px contact 10px tall gets "
                + $"{Stage3D.Seating(2.0f, 10.0f, 9.0f):F1} against a 9px "
                + "footprint - the boulder case, where the contact band is the "
                + "bottom row of a rounded thing and the height cannot make it up");

            // The measurement behind that floor, on the art actually loaded: a
            // tree declares itself all height, so its band is empty and its
            // spread is the bottom row, which is what leaves every wood where it
            // was. Anything that lies down has a band and fills it.
            Check("and a tree's footprint is nothing, while a stone's is itself",
                grove.Standing.Where(t => t.Tier.Stands >= 1.0)
                     .All(t => t.Spread <= t.Root)
                && grove.Standing.Where(t => t.Tier.Stands < 1.0)
                        .All(t => t.Spread > t.Root),
                string.Join("; ", grove.Standing
                    .GroupBy(t => t.Tier.Name)
                    .Select(g => $"{g.Key} spread {g.Min(t => t.Spread):F1}-"
                                 + $"{g.Max(t => t.Spread):F1} against contact "
                                 + $"{g.Min(t => t.Root):F1}-{g.Max(t => t.Root):F1}")));

            // Two claims, two switches: the cast says where the sun is, the
            // patch says the thing is touching the ground, and the A/B of either
            // is a pair of frames in which the other must not move.
            Check("and the patch and the cast are separate switches",
                new Stage3D().CastShadows && new Stage3D().ContactShadows,
                "both on by default, on the argument that a layer whose whole "
                + "case is the A/B has to be there to be turned off");

            // One ink, not two kept equal - and the check is for the second,
            // because that is what was here and what looked wrong. A patch
            // lighter than the streak leaving the same foot makes the ground
            // brighten as it approaches the thing casting the shadow, which is
            // backwards however soft an occlusion is meant to be. Identity also
            // buys the thing the order does not have to be argued about: both
            // are black, and a + b - ab is symmetric.
            // --- and it paints only the ground the cast has not -----------
            //
            // Two shadows of one object must not compound: this board has one
            // sun and no ambient term, so the second darkening stands for
            // nothing. Measured before the clip, 14% of the shadowed ground came
            // out at 0.68 of the ground colour against a single shadow's 0.45.
            //
            // Asserted on the shader's own text, for FoamEdge's reason: a patch
            // that quietly went back to painting its whole blot would still
            // draw, still be black and still be the right size, and the only
            // thing that changed would be the two lines named here.
            Check("the patch keeps off the ground its own cast already has",
                Stage3D.SeatShader.Contains("cover * ink * clip")
                && Stage3D.SeatShader.Contains("dz / run.y"),
                "the inverse of Cast, and one sample of the art the prop is "
                + "already holding - " + (Stage3D.SeatShader.Contains("dz / run.y")
                    ? "present" : "GONE"));

            // And off what the cast is laying down *this frame*, which during a
            // handover is a mix of the two pictures. Reading one of them leaves the
            // patch standing aside from a streak that is half gone - a bite out of
            // itself taken by a shadow on its way out.
            Check("and off the mix of the pair, not off one of them",
                Stage3D.SeatShader.Contains("mix(cover,")
                && Stage3D.SeatShader.Contains("uniform float swap"),
                "the same handover the cast and the crown are given");

            // The difference and not the maximum, because it is composited over
            // the cast rather than instead of it. Which of the two draws first
            // does not matter, and that is what makes the inversion legal - so
            // the identity it rests on is worth asserting on its own.
            Check("and lays down the difference, so the pair ends at the max",
                Stage3D.SeatShader.Contains(
                    "1.0 - (1.0 - max(cast, blot)) / (1.0 - cast)")
                && Math.Abs((0.45f + 0.30f - 0.45f * 0.30f)
                            - (0.30f + 0.45f - 0.30f * 0.45f)) < 1e-6f,
                "1-(1-a)(1-b) is the same either way round, which is why the "
                + "second layer can be solved for");

            // The half of it that is easy to leave out: with no cast on the
            // board there is nothing to stand aside for, and a patch that went
            // on standing aside would show a bite taken out of it by a shadow
            // nobody can see. Measured: 26 198px of patch on a board with the
            // cast switched off, against 6 639 while it was clipping regardless.
            Check("and stops keeping off when there is no cast to keep off",
                Stage3D.SeatShader.Contains("uniform float clip"),
                "the guard the --no-cast-shadows A/B needs");

            Check("and the patch is the cast's own ink, not a second one",
                Stage3D.SeatInk == Stage3D.ShadowInk
                && Stage3D.SeatInk.R == 0.0f && Stage3D.SeatInk.G == 0.0f
                && Stage3D.SeatInk.B == 0.0f,
                $"patch {Stage3D.SeatInk.A:F2} against cast "
                + $"{Stage3D.ShadowInk.A:F2}, compounding to "
                + $"{1.0f - (1.0f - Stage3D.SeatInk.A) * (1.0f - Stage3D.ShadowInk.A):F2} "
                + "where they overlap - and never to less than the streak "
                + "beside it, which is the fault this replaced");

            // What tells somebody drawing this wood that it is a different wood.
            // The count cannot: a re-roll moves every tree and can leave the
            // total exactly where it was, and billboards built for trees that
            // have been freed keep standing - a freed node still answers where
            // it stood.
            int counter = grove.Sown, standing = grove.Planted;
            grove.Plant();
            grove.Plant();
            Check("the wood counts its own sowings",
                grove.Sown == counter + 2 && grove.Planted == standing,
                $"{counter} to {grove.Sown} over {standing} trees that did not "
                + "change in number");

            // Hashed, not generated: --capture fixes the time step so two runs
            // can be diffed, and a board that reshuffled itself would be
            // measuring itself.
            var sown = grove.Standing.Select(t => (t.Ground, t.Species)).ToList();
            grove.Plant();
            Check("sowing twice gives the same forest",
                grove.Standing.Select(t => (t.Ground, t.Species)).SequenceEqual(sown),
                $"{sown.Count} then {grove.Planted}");

            // The rule that cannot be had, asserted as a rule anyway: with it on
            // nothing crosses a tank on its own cell, and what that costs is the
            // whole front of every clearing.
            bool wasClear = grove.ClearFront;
            grove.ClearFront = true;
            grove.Plant();
            Check("with the clearance rule on, no tree crosses its own cell's tank",
                grove.Standing.All(t =>
                {
                    double d = t.Ground.Y - field.CellCentre(t.Cell).Y;
                    return d <= 0.0 || t.Rise < d - grove.TankBelow;
                }),
                $"tree rise against {grove.TankBelow:F0}px of hang below the "
                + "contact point");
            grove.ClearFront = wasClear;

            grove.Plant();

            // Forest is a kind of ground, so it is chosen where the kinds are
            // chosen. Painting one plate over the board therefore takes the
            // woods off it, and painting forest puts them on every cell - and
            // both have to be true, or "how much forest" has quietly become a
            // second dial arguing with the terrain list.
            string wasPainted = field.Paint;
            field.Paint = TerrainSet.Forest;
            grove.Plant();
            int all = grove.Planted;
            // Every cell the ground can be, which is not quite every cell: a ramp
            // and a flooded cell refuse trees whatever the paint says, and both
            // refusals are the grove's own - see Grove.IsForest. Written as an
            // exception here rather than loosened to "most of them", because "the
            // paint reaches every cell" and "these two kinds of cell are never
            // wooded" are both statements worth keeping.
            Check("painting forest woods every cell the ground allows",
                Enumerable.Range(0, field.Columns * field.Rows)
                    .Select(i => new Vector2I(i % field.Columns, i / field.Columns))
                    .All(c => grove.IsForest(c)
                              || field.IsRamp(c) || field.IsWater(c)) && all > 0,
                $"{all} trees");

            field.Paint = field.Terrain!.Names[0];
            grove.Plant();
            Check("painting a plate takes the trees off", grove.Planted == 0,
                $"{grove.Planted} grew on a board painted {field.Terrain!.Names[0]}");
            field.Paint = wasPainted;

            // The trees stop being ground for exactly as long as a tank is in
            // them. Every tree on the cell rather than the ones overlapping:
            // which trees cross the tank changes with its heading, so a per-tree
            // test would blink as it turned on the spot.
            field.Paint = TerrainSet.Forest;
            grove.Plant();
            Vector2I stood = grove.Standing[0].Cell;
            var busy = new HashSet<Vector2I> { stood };
            for (int i = 0; i < 240; i++)
                grove.Reveal(busy, 1.0 / 60.0);
            // Of the tiers that fade. A rock on the same cell staying solid is
            // the rule working, not the fade missing one - and folded together
            // the two are one average that means neither.
            Check("trees fade for a tank standing in them",
                grove.Standing.Where(t => t.Cell == stood && t.Fades)
                     .All(t => Math.Abs(t.Modulate.A - grove.Ghost) < 0.01f)
                && grove.Standing.Where(t => t.Cell != stood)
                        .All(t => t.Modulate.A > 0.99f),
                $"ghost {grove.Ghost:F2}");
            // The named default, not the live value: a session that dragged the
            // slider would fail the second, and a check on neither would not
            // notice the default going to zero.
            Check("and by default they are still there",
                Grove.GhostByDefault > 0.0f && Grove.GhostByDefault < 1.0f,
                $"{Grove.GhostByDefault:F2} - gone is a tank standing in a "
                + "field, solid is a tank the wood keeps");

            // The dial reaches the trees, and reaches them while they are
            // already faded: it is how the wood is being drawn rather than
            // something a tank carried in with it, so dragging it mid-fade has
            // to retarget rather than wait for the next cell.
            float wasGhost = grove.Ghost;
            grove.Ghost = 0.75f;
            for (int i = 0; i < 240; i++)
                grove.Reveal(busy, 1.0 / 60.0);
            Check("the fade settles wherever the setting says",
                grove.Standing.Where(t => t.Cell == stood && t.Fades)
                     .All(t => Math.Abs(t.Modulate.A - 0.75f) < 0.01f),
                "a fade that ignored the dial would still look like a fade");
            grove.Ghost = wasGhost;
            for (int i = 0; i < 240; i++)
                grove.Reveal(new HashSet<Vector2I>(), 1.0 / 60.0);
            Check("and come back when it drives off",
                grove.Standing.All(t => t.Modulate.A > 0.99f),
                "the fade is a view of the ground, not a change to it");

            // And the patch that decides all of the above is stepped across the
            // ground, never across the screen. A tank standing a level down is
            // drawn a lift lower than its own cell, so a probe walking the
            // keep-out sideways ends up over the bank that is drawn on that row -
            // and the picker names it, honestly, because it is the nearer cell
            // painted there. The wood then opens on a cell nobody is on.
            //
            // Both halves asserted, because a check that only says "the ground
            // answer is right" passes on the screen answer wherever the two
            // happen to agree - which is everywhere on a flat board and on five
            // of these six probes.
            {
                var pit = new Vector2I(6, 5);
                var bank = new Vector2I(5, 5);
                field.SetRelief(BoardMap.Bench.LevelsFor(field.Columns, field.Rows),
                                BoardMap.Bench.RampsFor(field.Columns, field.Rows));
                Vector2 stand = field.CellCentre(pit);
                float drop = field.TopAt(pit);
                var ground = Footing.Patch(field, stand, drop,
                                        grove.KeepOut, grove.Squash).ToHashSet();
                var screen = new HashSet<Vector2I>();
                for (int k = 0; k < 6; k++)
                {
                    double a = Math.PI * k / 3.0;
                    screen.Add(field.CellAt(new Vector2(
                        stand.X + (float)(Math.Cos(a) * grove.KeepOut),
                        stand.Y + (float)(Math.Sin(a) * grove.KeepOut
                                          * grove.Squash))));
                }
                Check("the wood opens for the ground a tank covers, not the "
                      + "pixels it is drawn over",
                    field.LevelAt(pit) < field.LevelAt(bank)
                    && ground.Count == 1 && ground.Contains(pit)
                    && screen.Contains(bank),
                    $"a tank in the ford at {pit.X},{pit.Y} covers "
                    + string.Join(" ", ground.Select(c => $"{c.X},{c.Y}"))
                    + "; read off the screen it covers "
                    + string.Join(" ", screen.Select(c => $"{c.X},{c.Y}")));
                field.SetRelief(null);
            }

            // --- the wind ------------------------------------------------
            //
            // Stepped rather than clocked off the wall, so this runs the same
            // in a check as it does under --capture.
            double wasWind = grove.Wind;
            grove.Wind = 3.0;
            for (int i = 0; i < 40; i++)
                grove.Blow(1.0 / 60.0);
            Check("the wind leans the trees",
                grove.Standing.Any(t => Math.Abs(t.Drift) > 0.2f),
                $"widest lean {grove.Standing.Max(t => Math.Abs(t.Drift)):F2}px");

            // The whole point of hashing a phase per tree. A wood that leans as
            // one body is a wood on a hinge.
            Check("and not all of them the same way",
                grove.Standing.Any(t => t.Lean > 0.0f)
                && grove.Standing.Any(t => t.Lean < 0.0f),
                "every tree leaning the same way at once is one hinge, not wind");

            // Drift is the shear times the height, which is what makes a taller
            // tree lean further - the half of the setting that is derived
            // rather than chosen, and the half that would go quietly if someone
            // made the drift a fixed number of pixels.
            Check("a taller tree leans further, because the lean is a shear",
                grove.Standing.All(t => Math.Abs(t.Drift - t.Lean * t.Rise) < 1e-4),
                "drift is the shear times the height, or it is not a lean");

            // The trunk is planted, and this is the matrix that has to say so:
            // it pivots on the foot, so whatever the crown does the ground
            // contact does not move. Asked of the transform the draw call
            // actually uses - the shear on the wrong axis, or an origin left in
            // it, both slide the tree and both look like wind.
            PropNode bent = grove.Standing.OrderByDescending(t => Math.Abs(t.Lean))
                                       .First();
            Vector2 foot = bent.Bend * Vector2.Zero;
            Vector2 crown = bent.Bend * new Vector2(0.0f, -bent.Rise);
            Check("the foot stays where it was planted",
                foot.Length() < 1e-4f
                && Math.Abs(crown.X - bent.Drift) < 1e-3f
                && Math.Abs(crown.Y + bent.Rise) < 1e-4f,
                $"foot moved to {foot}, crown to {crown} against a "
                + $"{bent.Drift:F2}px drift");

            // The same weather twice, reached two ways: the lean has to be a
            // function of how long the wind has blown, not of how many times it
            // was asked. The trap the tremble and the exhaust both name - a
            // phase advanced per frame rather than per second is a wood that
            // sways at whatever rate the machine happens to run at.
            double wasWeather = grove.Weather;
            grove.Weather = 0.0;
            for (int i = 0; i < 40; i++)
                grove.Blow(1.0 / 60.0);
            var leaning = grove.Standing.Select(t => t.Lean).ToList();
            grove.Weather = 0.0;
            for (int i = 0; i < 20; i++)
                grove.Blow(2.0 / 60.0);
            Check("the same seconds of wind bend it the same way",
                grove.Standing.Select(t => t.Lean).Zip(leaning)
                     .All(p => Math.Abs(p.First - p.Second) < 1e-5f),
                "a phase stepped per frame rather than per second sways at "
                + "whatever rate the machine runs at");
            grove.Weather = wasWeather;

            grove.Wind = 0.0;
            for (int i = 0; i < 40; i++)
                grove.Blow(1.0 / 60.0);
            Check("no wind, no lean at all",
                grove.Standing.All(t => t.Lean == 0.0f),
                $"{grove.Standing.Count(t => t.Lean != 0.0f)} still leaning");

            // --- the blast wave ------------------------------------------
            //
            // Wind held at nothing throughout, so Lean is the flinch and
            // nothing else - except in the last check here, which is about
            // exactly the case where it is both.
            grove.Wind = 0.0;
            grove.Calm();

            float flat = grove.Squash;
            Vector2 Ground(Vector2 screen) => new(screen.X, screen.Y / flat);
            Vector2 seat = Ground(field.CellCentre(grove.Standing[0].Cell));

            // Ten crown pixels at the source, which is twice the shell's own
            // and well clear of the threshold the flinch settles at.
            (int[] Reached, int[] Sign, float[] Peak) Blast(double pixels, int shots)
            {
                grove.Calm();
                for (int s = 0; s < shots; s++)
                    grove.Shock(field.CellCentre(grove.Standing[0].Cell), pixels);
                var reached = new int[grove.Standing.Count];
                var sign = new int[grove.Standing.Count];
                var peak = new float[grove.Standing.Count];
                for (int i = 0; i < reached.Length; i++)
                    reached[i] = -1;
                for (int f = 0; f < 300; f++)
                {
                    grove.Blow(1.0 / 60.0);
                    for (int i = 0; i < grove.Standing.Count; i++)
                    {
                        float now = grove.Standing[i].Flinch;
                        if (reached[i] < 0 && now != 0.0f)
                        {
                            reached[i] = f;
                            sign[i] = Math.Sign(now);
                        }
                        peak[i] = Math.Max(peak[i], Math.Abs(now));
                    }
                }
                return (reached, sign, peak);
            }

            var wave = Blast(10.0, 1);
            Check("a blast shoves the trees around it",
                wave.Peak.Any(p => p > 0.0f),
                $"{wave.Peak.Count(p => p > 0.0f)} of {grove.Planted} moved");

            // The whole of why this needs no per-tree phase the way the wind
            // does: the front travels, so the near trees go first. Asserted
            // against the arrival the speed predicts rather than against each
            // other, because "the far ones lag" is also true of a wave that
            // arrives everywhere at once and merely falls off.
            double perFrame = grove.BlastSpeed / 60.0;
            int first = int.MaxValue, last = -1;
            bool timed = true;
            for (int i = 0; i < grove.Standing.Count; i++)
            {
                // A tier that does not sway never answers a front, and a wave
                // that never reached it is the rule rather than a gap in the
                // wave. Left in, it reports frame -1 and makes the spread the
                // whole run.
                if (!grove.Standing[i].Sways)
                    continue;
                double d = Ground(grove.Standing[i].Ground).DistanceTo(seat);
                if (d >= grove.BlastReach)
                    continue;
                int want = Math.Max(0, (int)Math.Ceiling(d / perFrame) - 1);
                if (Math.Abs(wave.Reached[i] - want) > 1)
                    timed = false;
                first = Math.Min(first, wave.Reached[i]);
                last = Math.Max(last, wave.Reached[i]);
            }
            // The spread rather than "some flinched at once": nothing grows
            // within the keep-out, so the nearest tree there is is already
            // seven frames out and no wave can shake anything sooner.
            Check("and reaches them when a front travelling at its speed would",
                timed && last - first >= 15,
                $"the wood answers from frame {first} to frame {last} - a wave "
                + "that arrived all at once would have those two the same");

            Check("nothing past its reach feels it",
                Enumerable.Range(0, grove.Standing.Count).All(
                    i => Ground(grove.Standing[i].Ground).DistanceTo(seat)
                         < grove.BlastReach || wave.Peak[i] == 0.0f),
                $"reach {grove.BlastReach:F0}px of ground");

            // Away from it, and only the part of "away" that runs across the
            // screen: a tree straight up-board is pushed at the camera, and a
            // shear cannot draw that lean at all. The trees with little
            // sideways in their bearing are left out of the test for the same
            // reason they barely move.
            Check("and every tree is shoved away from it, not toward it",
                Enumerable.Range(0, grove.Standing.Count).All(i =>
                {
                    if (!grove.Standing[i].Sways)
                        return true;
                    Vector2 g = Ground(grove.Standing[i].Ground);
                    double d = g.DistanceTo(seat);
                    if (d >= grove.BlastReach || Math.Abs(g.X - seat.X) < 0.4 * d)
                        return true;
                    return wave.Sign[i] == Math.Sign(g.X - seat.X);
                }),
                "a tree leaning into the blast is the sign of the shove lost");

            // Asked for crown pixels, delivers crown pixels - which is what the
            // unit-impulse integrator is for, and the thing a closed-form
            // normalisation would get wrong by enough to matter.
            int loudest = Enumerable.Range(0, grove.Standing.Count)
                                    .OrderByDescending(i => wave.Peak[i]).First();
            {
                Vector2 g = Ground(grove.Standing[loudest].Ground);
                double d = g.DistanceTo(seat);
                double want = 10.0 * (1.0 - d / grove.BlastReach)
                              * Math.Abs(g.X - seat.X) / d;
                double got = wave.Peak[loudest] * grove.SwayHeight;
                Check("the shove arrives in the crown pixels it was asked for",
                    Math.Abs(got - want) < 0.08 * want,
                    $"{got:F2}px against {want:F2} at {d:F0}px of ground");
            }

            Check("and rings down to nothing",
                grove.Standing.All(t => t.Flinch == 0.0f && t.FlinchRate == 0.0f),
                "a spring left holding a pose bends the board for good");

            // Two guns in one frame is twice one gun. The compounding statement
            // CameraShake.Fire makes, and the reason it is put as two at once
            // rather than one during the other's ring-down: out of phase they
            // partly cancel, so "the second is always bigger" is false.
            var twice = Blast(10.0, 2);
            Check("two blasts at once shove twice as hard",
                Math.Abs(twice.Peak[loudest] - 2.0f * wave.Peak[loudest])
                    < 0.02f * wave.Peak[loudest],
                $"{twice.Peak[loudest]:F4} against {wave.Peak[loudest]:F4}");

            // A multiplier over the three strengths rather than a strength, so
            // that a shot, a hit and a kill stay told apart - the argument the
            // tremble level and the class size both make.
            double wasBlast = grove.Blast;
            grove.Blast = 2.0;
            var harder = Blast(10.0, 1);
            grove.Blast = 0.0;
            var deaf = Blast(10.0, 1);
            grove.Blast = wasBlast;
            Check("the dial scales it, and nothing at zero",
                Math.Abs(harder.Peak[loudest] - 2.0f * wave.Peak[loudest])
                    < 0.02f * wave.Peak[loudest]
                && deaf.Peak.All(p => p == 0.0f) && grove.Waves == 0,
                $"{harder.Peak[loudest]:F4} at 2x, "
                + $"{deaf.Peak.Count(p => p > 0.0f)} moved at 0x");

            // The three events are meant to be told apart by how hard the wood
            // answers, and the order of them is the statement: a tank letting
            // go out-shoves the round that did it, which out-shoves the gun.
            Check("a kill shoves harder than a hit, and a hit than a shot",
                grove.DeathBlast > grove.HitBlast
                && grove.HitBlast > grove.ShotBlast
                    * MovementProfile.For("HTP").ShotShake,
                $"{grove.DeathBlast:F0} / {grove.HitBlast:F0} / "
                + $"{grove.ShotBlast * MovementProfile.For("HTP").ShotShake:F1}px "
                + "for the heaviest gun");

            // Wind and blast add. One writer for Lean, and a flinch that
            // replaced the weather would be showing the wind switching off for
            // a second and a half every time a gun went off.
            grove.Wind = 3.0;
            grove.Calm();
            double markWeather = grove.Weather;
            grove.Shock(field.CellCentre(grove.Standing[0].Cell), 10.0);
            for (int f = 0; f < 30; f++)
                grove.Blow(1.0 / 60.0);
            var mixed = grove.Standing.Select(t => (t.Lean, t.Flinch)).ToList();
            grove.Calm();
            grove.Weather = markWeather;
            for (int f = 0; f < 30; f++)
                grove.Blow(1.0 / 60.0);
            Check("the wind goes on blowing through a blast",
                grove.Standing.Select(t => t.Lean).Zip(mixed)
                     .All(p => Math.Abs(p.First + p.Second.Flinch
                                        - p.Second.Lean) < 1e-5f)
                && mixed.Any(m => m.Flinch != 0.0f),
                "the lean is the weather plus the shove, or one of them is "
                + "overwriting the other");

            // --- a hull through the trees --------------------------------
            //
            // Same spring, different mechanism: a force while the hull is near
            // rather than an impulse once. Driven at the reference cruise, from
            // 40 ground px to the left of a tree, so what it should do is push
            // that tree to the right and hold it there while the hull stays.
            PropNode brushed = grove.Standing[0];
            Vector2 hull = brushed.Ground - new Vector2(40.0f, 0.0f);
            Vector2 step = new((float)(grove.BrushSpeed / 60.0), 0.0f);

            float Pass(int frames, double level, Vector2 by)
            {
                double wasLevel = grove.Brushing;
                grove.Brushing = level;
                grove.Calm();
                float top = 0.0f;
                for (int f = 0; f < frames; f++)
                {
                    grove.Brush(hull, by, 1.0 / 60.0);
                    grove.Blow(1.0 / 60.0);
                    top = Math.Max(top, Math.Abs(brushed.Flinch * brushed.Rise));
                }
                grove.Brushing = wasLevel;
                return top;
            }

            float wasBrushing = (float)grove.Brushing;
            grove.Calm();
            for (int f = 0; f < 20; f++)
            {
                grove.Brush(hull, step, 1.0 / 60.0);
                grove.Blow(1.0 / 60.0);
            }
            Check("a hull going by shoulders the wood aside",
                brushed.Flinch > 0.0f,
                $"{brushed.Flinch * brushed.Rise:F2}px of crown, 40px of ground "
                + "off the hull");

            // Away from it, and the whole ring at once: what a hull does to a
            // wood is part it, so the trees on both sides go outward. Along its
            // travel instead would leave a tank driving up-board pushing every
            // tree at the camera, which a shear cannot draw.
            Check("and parts it, rather than pushing it the way it is going",
                grove.Standing.All(t =>
                {
                    double d = Ground(t.Ground).DistanceTo(Ground(hull));
                    if (d >= grove.BrushReach || t.Flinch == 0.0f)
                        return true;
                    return Math.Sign(t.Flinch)
                           == Math.Sign(t.Ground.X - hull.X);
                }),
                "a tree leaning into the hull is the sign of the shove lost");

            Check("nothing past its reach gives at all",
                grove.Standing.All(
                    t => Ground(t.Ground).DistanceTo(Ground(hull))
                         < grove.BrushReach || t.Flinch == 0.0f),
                $"reach {grove.BrushReach:F0}px of ground");

            // The one that says this is a force and not a kick, and it has to be
            // put as *held* rather than as a bigger peak: the spring settles in
            // a tenth of a second, so six frames of drive already reach 82% of
            // forty frames' worth and a peak comparison says almost nothing. A
            // kick rings down, so the tree would be upright again while the hull
            // was still alongside; a drive holds it over until the hull leaves.
            double rest = wasBrushing * (1.0 - 40.0 / grove.BrushReach)
                          * brushed.Rise / grove.SwayHeight;
            grove.Brushing = wasBrushing;
            grove.Calm();
            for (int f = 0; f < 90; f++)
            {
                grove.Brush(hull, step, 1.0 / 60.0);
                grove.Blow(1.0 / 60.0);
            }
            float held = brushed.Flinch * brushed.Rise;
            for (int f = 0; f < 90; f++)
                grove.Blow(1.0 / 60.0);
            float sprung = brushed.Flinch * brushed.Rise;
            Check("it holds the bend while the hull is there, being a force",
                Math.Abs(held - rest) < 0.05 * rest && Math.Abs(sprung) < 0.02f * held,
                $"{held:F2}px held against a {rest:F2} rest, {sprung:F2} once the "
                + "hull has gone - a kick would be upright already");

            // Which is also what bounds it: repeated kicks pile up without
            // limit, and force over stiffness does not, so a tank that dawdles
            // in the trees does not lay them flat.
            float dawdled = Pass(400, wasBrushing, step);
            Check("and no further than the spring's own rest",
                dawdled < 2.0 * rest,
                $"{dawdled:F2}px after seven seconds of it against a "
                + $"{rest:F2} rest");

            // Travel rather than a speed: a pivot moves no ground point, so it
            // leaves the wood alone without anything here knowing what a pivot
            // is - and a parked tank in a clearing is 83px from the nearest
            // trunk anyway, which is the other half of the same statement.
            Check("a tank that is not moving pushes nothing",
                Pass(60, wasBrushing, Vector2.Zero) == 0.0f,
                "displacement is what says it is happening at all");

            Check("and a faster hull pushes harder",
                Pass(30, wasBrushing, step * 0.25f) < Pass(30, wasBrushing, step),
                "how hard a hull parts a wood is how fast it is going");

            float once = Pass(30, wasBrushing, step);
            float twofold = Pass(30, wasBrushing * 2.0, step);
            Check("the dial scales it, and nothing at zero",
                Math.Abs(twofold - 2.0f * once) < 0.02f * once
                && Pass(30, 0.0, step) == 0.0f,
                $"{twofold:F2}px at twice the setting against {once:F2}");

            grove.Brushing = wasBrushing;
            grove.Calm();

            // The reset has to reach it: the flinch is the one thing in the
            // grove that holds a pose with nothing driving it, and a front
            // still crossing would arrive at a board that had just been put
            // back. The wind is deliberately not reset - it is weather.
            grove.Shock(field.CellCentre(grove.Standing[0].Cell), 10.0);
            grove.Blow(1.0 / 60.0);
            grove.Calm();
            Check("and calming it stops every front and every flinch",
                grove.Waves == 0
                && grove.Standing.All(t => t.Flinch == 0.0f && t.FlinchRate == 0.0f),
                "a wave in flight through a reset lands on a repaired board");

            grove.Wind = wasWind;
            grove.Calm();

            field.Paint = wasPainted;
            bool wasSown = grove.Enabled;
            grove.Enabled = false;
            grove.Plant();
            Check("switched off, nothing grows", grove.Planted == 0,
                $"{grove.Planted} trees on a board with the layer off");
            grove.Enabled = wasSown;
            grove.Plant();
        }

        // --- the ground under the tank ------------------------------------
        //
        // The one layer that is neither the tank nor an effect on it, and every
        // check here is about that difference.
        Theme("the contact shadow");
        var shade = EffectLayer.Shadow(AtlasSet.ShadowName);
        Check("the shadow is ordered under every tank, not with its own",
            shade.ZIndex == TankSprite.ShadowZ && !shade.ZAsRelative
            && TankSprite.ShadowZ < 0,
            $"z {shade.ZIndex}, relative {shade.ZAsRelative} - two heavies in "
            + "neighbouring columns overlap, and a shadow ordered with its own "
            + "tank lands on the neighbour");
        Check("it sits over the ground marks and under the tanks",
            TankSprite.ShadowZ > SelectionRing.GroundZ,
            $"shadow {TankSprite.ShadowZ}, ring {SelectionRing.GroundZ} - a "
            + "shadow falls across paint, not under it");
        Check("it is on the hull's heading, not the turret's",
            shade.FollowsHull,
            "it is the hull's footprint, and the gun is deliberately not in it");
        Check("it is drawn on the shared anchor, never placed",
            !shade.Placed, "only an arriving shell is placed");
        Check("it is not a loop and not one of the shot's layers",
            !shade.Loops && !AtlasSet.EffectNames.Contains(AtlasSet.ShadowName),
            "no clock at all: what it draws is chosen by the heading alone");
        Check("it composites first of everything the tank owns",
            TankSprite.LayerOrder.Length > 0
            && TankSprite.LayerOrder[0] == AtlasSet.ShadowName,
            "it is the ground, and everything else is above the ground");
        // ONE INK ON THE BOARD, and the tank's shadow is the one that can walk
        // away from it: the trees and the relief both read Stage3D.ShadowInk,
        // and this layer once carried no tint at all and so landed at whatever
        // the atlas said. That is a measurement of lost sun, which is a fine
        // thing for an atlas to be and full black on a field - a tree shadow
        // takes 0.449 of the ground and this took 1.000.
        //
        // Asked as an equality against the same field, so it is the *coupling*
        // being asserted and not a number copied here to drift on its own.
        Check("a tank darkens the ground by the board's own ink, as a tree does",
            Mathf.Abs(EffectLayer.DensityOf(EffectLayer.Clocks.Shadow, null)
                      - Stage3D.ShadowInk.A) < 1e-6f,
            $"{EffectLayer.DensityOf(EffectLayer.Clocks.Shadow, null)} against "
            + $"{Stage3D.ShadowInk.A} - two suns of different strengths");

        AtlasSet? shadowed = atlases?.Values.FirstOrDefault(a => a.HasShadow)
                             ?? (atlas.HasShadow ? atlas : null);
        if (shadowed is null)
        {
            Check("some loaded tank stands on a shadow", false,
                "no shadow layer on disk - re-render with tank_pipeline.run()");
        }
        else
        {
            Vector2 anchorFrac = shadowed.Anchor / (Vector2)shadowed.Tile;
            Vector2 mine = shadowed.AnchorOf(AtlasSet.ShadowName)
                           / (Vector2)shadowed.TileOf(AtlasSet.ShadowName);
            Check("the shadow shares the tank's anchor, in frame fractions",
                (mine - anchorFrac).Length() < 1e-4,
                $"{mine} against {anchorFrac}");
            // IT DOES WANT A WIDER FRAME NOW, and this check used to say the
            // opposite. The old one read "the shadow needs no wider frame than
            // the tank", on the grounds that its catcher was a disc of the
            // tile's own inradius and so it could not want room the tank did
            // not already have - a wider frame would have meant it had quietly
            // become a cast shadow. It is one: the board declares a sun, the
            // pass is baked under it, and at 55 degrees the thing reaches past
            // the cell by construction.
            //
            // So what is worth asserting is the other half of that bargain -
            // that the width is being used. A wider frame that the shadow does
            // not fill is memory spent on nothing, and it is exactly what would
            // be left behind if the sun ever went back overhead.
            Vector2I frame = shadowed.TileOf(AtlasSet.ShadowName);
            // FROM THE ANCHOR, not from the corner of the frame, and getting
            // that wrong is this layer's standing trap now that its tile is not
            // the tank's: an offset read raw comes out a plausible number that
            // is mostly half the frame. The same slip was measured and fixed on
            // the render side, where it made the shadow look 128px displaced.
            Vector2 seat = shadowed.AnchorOf(AtlasSet.ShadowName);
            float reach = 0.0f;
            for (int f = 0; f < shadowed.CountOf(AtlasSet.ShadowName); f++)
            {
                Vector2 off = shadowed.OffsetOf(AtlasSet.ShadowName, f) - seat;
                Vector2 size = shadowed.SizeOf(AtlasSet.ShadowName, f);
                reach = Math.Max(reach, Math.Max(off.X + size.X, off.Y + size.Y));
            }
            // What this can honestly claim, and the first draft of it claimed
            // more. "It wants a wider frame and fills it" sounded right and is
            // false: the shadow reaches 120px from the anchor against the 128
            // a 256 frame gives, so it fits the tank's own frame - with eight
            // pixels to spare, which is the margin the wider frame buys rather
            // than a reach that needs it. The old note that justified 512 said
            // 207px, and that number was the whole tank's height; only the hull
            // and the belts cast.
            //
            // So what is asserted is the pair that is true and would break:
            // this layer is given room of its own, and it is not up against the
            // edge of it. The frame costs nothing either way - `atlas_pack`
            // keeps the trimmed rect - so there is no waste to guard against,
            // only clipping.
            float half = frame.X / 2.0f;
            Check("the shadow is cast, so it has room of its own and is inside it",
                frame.X > shadowed.Tile.X && reach < half - 8.0f,
                $"a {frame.X}px frame against the tank's {shadowed.Tile.X}px, "
                + $"with the shadow reaching {reach:F0}px from the anchor - "
                + $"{half}px is where its own frame cuts and "
                + $"{shadowed.Tile.X / 2}px is where the tank's would");
            Check("the shadow was rendered at every heading",
                shadowed.CountOf(AtlasSet.ShadowName) == shadowed.Count,
                $"{shadowed.CountOf(AtlasSet.ShadowName)} against "
                + $"{shadowed.Count} - it turns with the hull");
            Check("the shadow has no phase axis",
                shadowed.PhasesOf(AtlasSet.ShadowName) == 1,
                "a contact shadow is a state of the ground, not an event");
            var shadowTiles = new HashSet<int>();
            foreach (int facing in shadowed.RenderedFacings())
                shadowTiles.Add(
                    shadowed.EffectFrame(AtlasSet.ShadowName, 0, facing));
            Check("every shadow heading maps to its own tile",
                shadowTiles.Count == shadowed.Count,
                $"{shadowTiles.Count} tiles for {shadowed.Count} headings");
        }

        AtlasSet? tracked = atlases?.Values.FirstOrDefault(a => a.HasTracks)
                            ?? (atlas.HasTracks ? atlas : null);
        if (tracked is null)
        {
            Check("some loaded tank has belts that wind", false,
                "no parts-built model on disk - every tank in the harness is one "
                + "now, so this means Sprites/ is empty or stale");
        }
        else
        {
            Vector2 tankAnchor = tracked.Anchor / (Vector2)tracked.Tile;
            foreach (string name in AtlasSet.TrackNames)
            {
                Vector2 beltAnchor = tracked.AnchorOf(name)
                                     / (Vector2)tracked.TileOf(name);
                Check($"{name} shares the tank's anchor, in frame fractions",
                    (beltAnchor - tankAnchor).Length() < 1e-4,
                    $"{beltAnchor} against {tankAnchor}");
                // The belt sits inside the hull's own footprint, so unlike the
                // flash it needs no room of its own. This is what would catch
                // it quietly growing until the frame cost went up for nothing.
                Check($"{name} needs no wider frame than the tank",
                    tracked.TileOf(name) == tracked.Tile,
                    $"{tracked.TileOf(name).X}px against tank {tracked.Tile.X}px");
                // Unlike the hit, which is one heading-less column: a belt is
                // welded to the hull and has to turn with it.
                Check($"{name} was rendered at every heading",
                    tracked.CountOf(name) == tracked.Count,
                    $"{tracked.CountOf(name)} against {tracked.Count}");
            }
            foreach (string name in AtlasSet.TrackNames)
            {
                string smear = AtlasSet.SmearName(name);
                Check($"{name} has a phase average to fade into",
                    tracked.Has(smear),
                    "derived from frames already loaded - no render, and it "
                    + "cannot go stale against the layer it comes from");
                // It stands in for the belt at the same place on screen, so it
                // has to be the same size and hang off the same point. A
                // different anchor would slide the belt sideways as it blurs.
                Check($"{smear} is one frame per heading at the belt's size",
                    tracked.PhasesOf(smear) == 1
                    && tracked.CountOf(smear) == tracked.CountOf(name)
                    && tracked.TileOf(smear) == tracked.TileOf(name)
                    && (tracked.AnchorOf(smear) - tracked.AnchorOf(name)).Length()
                       < 1e-4,
                    $"{tracked.PhasesOf(smear)} phases,"
                    + $" {tracked.CountOf(smear)} headings,"
                    + $" tile {tracked.TileOf(smear).X}px");
                // The '#' is not decoration. by_hints and every exclude in the
                // renderer match layer names by substring, and a smear named
                // "track_left_blur" would be caught by anything reaching for
                // the belt.
                Check($"{smear} is not one of the layers loaded from disk",
                    !AtlasSet.LayerNames.Contains(smear) && smear.Contains('#'),
                    "it is derived, and nothing should go looking for it on disk");
            }
            Check("both belts have the same number of phases",
                tracked.PhasesOf(AtlasSet.TrackNames[0])
                == tracked.PhasesOf(AtlasSet.TrackNames[1]),
                "one render job, one belt design");
            Check("the belts have phases to loop over", tracked.TrackPhases > 1,
                $"{tracked.TrackPhases} phases");
            // What the belts wind about when the tank turns on the spot. A
            // gauge wider than the hull is not a gauge, and one under a few
            // pixels would leave a pivoting tank on frozen tracks.
            Check("the tank's gauge was measured off its own belts",
                tracked.TrackArm > tracked.Tile.X / 16.0
                && tracked.TrackArm * 2.0 < tracked.HullSpan,
                $"half-gauge {tracked.TrackArm:F1}px against a"
                + $" {tracked.HullSpan}px hull");
            // A link the size of the tank would mean the cycle is not a link at
            // all, and a link under a pixel could not be seen to move.
            Check("the link is a plausible fraction of the tank",
                tracked.TrackPitch > 1.0 && tracked.TrackPitch < tracked.Tile.X / 8.0,
                $"{tracked.TrackPitch:F2}px against a {tracked.Tile.X}px frame");

            foreach (string name in AtlasSet.TrackNames)
            {
                var beltTiles = new HashSet<int>();
                foreach (int facing in tracked.RenderedFacings())
                    for (int phase = 0; phase < tracked.TrackPhases; phase++)
                        beltTiles.Add(tracked.EffectFrame(name, phase, facing));
                Check($"every {name} phase and heading maps to its own tile",
                    beltTiles.Count == tracked.TrackPhases * tracked.Count
                    && beltTiles.Max() < tracked.TrackPhases * tracked.Count,
                    $"{beltTiles.Count} distinct of"
                    + $" {tracked.TrackPhases * tracked.Count},"
                    + $" highest {beltTiles.Max()}");
            }
        }

        Theme("the gun tube's recoil");
        // What separates this clock from every other event is that it does not
        // end - it comes back to rest. The tube is part of the tank, so there is
        // no frame without a gun on it, and most of what follows is that one
        // difference asserted from several directions.
        var tube = new RecoilLoop { Phases = 5 };
        Check("the tube idles at rest before anything fires",
            tube.Phase == 0 && !tube.Live, $"phase {tube.Phase}");
        tube.Fire();
        var poses = new HashSet<int>();
        bool everInRange = true, everNegative = false;
        var walk = new List<int>();
        for (int i = 0; i < tube.Duration + 30; i++)
        {
            int phase = tube.Phase;
            walk.Add(phase);
            poses.Add(phase);
            if (phase < 0)
                everNegative = true;
            if (phase >= tube.Phases)
                everInRange = false;
            tube.Advance();
        }
        Check("it never asks for -1, the way the shot and the hit do",
            !everNegative,
            "a -1 here would blink the gun out of existence between rounds");
        Check("it stays inside the poses that were rendered", everInRange,
            $"{poses.Count} distinct of {tube.Phases}");
        Check("every rendered pose is actually shown",
            poses.Count == tube.Phases,
            $"{poses.Count} of {tube.Phases} - an unused pose is a wasted render");
        Check("it is back at rest once the stroke is over",
            walk[tube.Duration] == 0 && walk[^1] == 0,
            $"ends on {walk[^1]}");
        // **The phase index is time, not travel**, and mixing the two is the
        // mistake this pair of checks exists to prevent - it was made here first.
        // `barrel_recoil.curve` is [0, 1.0, 0.75, 0.5, 0.25], so pose 1 is full
        // travel and pose 4 is nearly home: the index counts forward while the
        // stroke counts back. A check written as "the highest index comes first"
        // passes only by accident on a table that eases the wrong way.
        Check("the stroke opens at full travel, which is pose 1",
            walk[0] == 1, $"opens on {walk[0]}");
        Check("full travel is shown once and not returned to",
            walk.IndexOf(1) == 0 && walk.LastIndexOf(1) == walk.IndexOf(2) - 1,
            "the tube does not go back out on the way home");
        // Poses run in time order and then stop: 1,1,1,2,2,...,4,...,0.
        bool inTimeOrder = true;
        for (int i = 0; i + 1 < tube.Duration; i++)
            if (walk[i + 1] < walk[i])
                inTimeOrder = false;
        Check("the poses run in time order all the way home", inTimeOrder,
            string.Join(",", walk.GetRange(0, tube.Duration)));
        int[] holds = RecoilLoop.HoldsFor(tube.Phases);
        Check("there is one hold for every moving pose",
            holds.Length == tube.Phases - 1,
            $"{holds.Length} holds for {tube.Phases - 1} moving poses");
        bool slowing = true;
        for (int i = 1; i < holds.Length - 1; i++)
            if (holds[i] > holds[i + 1])
                slowing = false;
        Check("the tube settles more slowly the closer it gets to rest", slowing,
            $"held {string.Join("/", holds)}");
        Check("the kick is the shortest hold of all",
            holds.Length < 2 || holds[0] < holds[1],
            $"kick {holds[0]} against {holds[1]} on the first return step");
        // The kick has to be over before the flash is, or the frames spent on it
        // are frames spent under a bloom. Measured against the shot's own table
        // rather than a number typed here, because that is the thing covering it.
        Check("the kick is over while the flash is still on the muzzle",
            holds[0] <= EffectLayer.Hold[0] + EffectLayer.Hold[1]
                        + EffectLayer.Hold[2],
            $"kick {holds[0]} frames against the flash's first three phases");
        tube.Fire();
        int restarted = tube.Phase;
        Check("a second round restarts the stroke rather than being ignored",
            restarted == 1, $"phase {restarted}, and full travel is pose 1");
        tube.Reset();
        Check("reset puts it back to rest", tube.Phase == 0 && !tube.Live);
        // A one-pose atlas cannot recoil, and asking for a phase must still be
        // safe: HasRecoil is what gates it, and this is the arithmetic behind it.
        Check("an atlas with nothing but rest reports rest",
            RecoilLoop.PhaseAt(5, 1) == 0 && RecoilLoop.PhaseAt(5, 0) == 0);

        // The heading, which is the one thing here that is the opposite of the
        // belts and the exhaust: those are bolted to the hull, the tube is bolted
        // to the turret. Get it backwards and the gun swings off the mantlet the
        // moment hull and turret disagree.
        EffectLayer tubeLayer = EffectLayer.Barrel(AtlasSet.BarrelName);
        Check("the tube is indexed by the turret's heading, not the hull's",
            !tubeLayer.FollowsHull,
            "it is bolted to the mount, unlike the belts and the plume");
        Check("it is drawn at the shared anchor, not placed",
            !tubeLayer.Placed,
            "only an arriving shell is placed off the anchor");
        Check("its clock is neither a loop nor the shot's", !tubeLayer.Loops
            && tubeLayer.Clock == EffectLayer.Clocks.Recoil);
        Check("the tube is not one of the shot's layers",
            Array.IndexOf(AtlasSet.EffectNames, AtlasSet.BarrelName) < 0,
            "EffectNames requires all of its names, so a tank with a flash but "
            + "no rendered tube would lose the flash");
        int tubeAt = Array.IndexOf(TankSprite.LayerOrder, AtlasSet.BarrelName);
        int mountAt = Array.IndexOf(TankSprite.LayerOrder, "turret");
        Check("the tube composites straight after the turret it slides into",
            tubeAt == mountAt + 1, $"tube at {tubeAt}, turret at {mountAt}");
        foreach (string over in new[]
                 {
                     AtlasSet.ExhaustName, AtlasSet.BurnName, AtlasSet.FireName,
                     "smoke", "flash", AtlasSet.DustName, AtlasSet.BurstName,
                 })
            Check($"{over} goes over the tube, not under it",
                Array.IndexOf(TankSprite.LayerOrder, over) > tubeAt,
                "the tube is part of the tank, so everything that happens to the "
                + "tank happens in front of it");

        AtlasSet? gunned = atlases?.Values.FirstOrDefault(a => a.HasRecoil)
                           ?? (atlas.HasRecoil ? atlas : null);
        if (gunned is null)
        {
            Check("some loaded tank has a tube that recoils", false,
                "no barrel layer on disk - render one with "
                + "tank_pipeline recoil_phases > 1");
        }
        else
        {
            Vector2 tankAnchor = gunned.Anchor / (Vector2)gunned.Tile;
            Vector2 gunAnchor = gunned.AnchorOf(AtlasSet.BarrelName)
                                / (Vector2)gunned.TileOf(AtlasSet.BarrelName);
            Check("the tube shares the tank's anchor, in frame fractions",
                (gunAnchor - tankAnchor).Length() < 1e-4,
                $"{gunAnchor} against {tankAnchor}");
            // It slides along the bore by a few pixels and stays inside the
            // turret's own footprint, so unlike the flash it buys no extra frame.
            Check("the tube needs no wider frame than the tank",
                gunned.TileOf(AtlasSet.BarrelName) == gunned.Tile,
                $"{gunned.TileOf(AtlasSet.BarrelName).X}px against tank"
                + $" {gunned.Tile.X}px");
            // Unlike the hit, which is one heading-less column: the gun turns.
            Check("the tube was rendered at every heading",
                gunned.CountOf(AtlasSet.BarrelName) == gunned.Count,
                $"{gunned.CountOf(AtlasSet.BarrelName)} against {gunned.Count}");
            Check("it has more than one pose to move between",
                gunned.RecoilPhases > 1, $"{gunned.RecoilPhases} poses");
            var gunTiles = new HashSet<int>();
            foreach (int facing in gunned.RenderedFacings())
                for (int phase = 0; phase < gunned.RecoilPhases; phase++)
                    gunTiles.Add(gunned.EffectFrame(AtlasSet.BarrelName, phase,
                                                    facing));
            Check("every tube pose and heading maps to its own tile",
                gunTiles.Count == gunned.RecoilPhases * gunned.Count
                && gunTiles.Max() < gunned.RecoilPhases * gunned.Count,
                $"{gunTiles.Count} distinct of"
                + $" {gunned.RecoilPhases * gunned.Count},"
                + $" highest {gunTiles.Max()}");
            // The muzzle is read off this layer now, and the failure mode of
            // doing that is a heading whose silhouette is too thin to survive
            // erosion: the search finds nothing and leaves the seeded anchor,
            // which fires the flash out of the middle of the tank. Both muzzle
            // checks above went red when the tube left the turret layer; this is
            // the cheap direct statement of what they were catching.
            int atAnchor = 0;
            for (int i = 0; i < gunned.Count; i++)
                if ((gunned.Muzzle(i) - gunned.Anchor).Length() < 1e-3)
                    atAnchor++;
            Check("no heading leaves the muzzle sitting on the anchor",
                atAnchor == 0,
                $"{atAnchor} of {gunned.Count} headings found no solid tube");
        }

        Theme("the engine exhaust");
        // What separates this from every other effect clock is that it has no
        // end, so the assertions are about it going round rather than about it
        // running out.
        var loop = new ExhaustLoop { Phases = 12, TopSpeed = 240.0 };
        var visited = new HashSet<int>();
        bool inRange = true;
        for (int i = 0; i < 600; i++)
        {
            loop.Advance(0.0, tick);
            visited.Add(loop.Frame);
            if (loop.Phase < 0.0 || loop.Phase >= loop.Phases
                || loop.Frame < 0 || loop.Frame >= loop.Phases)
                inRange = false;
        }
        Check("the loop stays inside the phases that were rendered", inRange,
            $"phase {loop.Phase:F3} frame {loop.Frame} of {loop.Phases}");
        Check("a lap visits every phase", visited.Count == loop.Phases,
            $"showed {visited.Count} of {loop.Phases}");
        Check("the loop never runs out, where the shot does",
            visited.All(f => f >= 0) && EffectLayer.PhaseAt(EffectLayer.Duration) < 0,
            "an engine that is running has no last frame");

        // One load ramp drives both, because both come from the engine turning
        // harder. Asserted rather than eyeballed because a plume that does not
        // answer to the throttle is the difference between an effect that says
        // something about the tank and one that just sits there.
        var idle = new ExhaustLoop { Phases = 12, TopSpeed = 240.0 };
        var worked = new ExhaustLoop { Phases = 12, TopSpeed = 240.0 };
        for (int i = 0; i < 60; i++)
        {
            idle.Advance(0.0, tick);
            worked.Advance(240.0, tick);
        }
        Check("working the engine speeds the loop up",
            worked.RateAt(240.0) > idle.RateAt(0.0) * 2.0,
            $"{idle.RateAt(0.0):F1}/s at rest against {worked.RateAt(240.0):F1}/s");
        Check("but the two ends are still the two ends",
            Math.Abs(worked.Response(0.0)) < 1e-9
            && Math.Abs(worked.Response(240.0) - 1.0) < 1e-9,
            "either model has to agree with itself about resting and working");

        // Two states is the delivered model, so the assertion is that there is
        // no third: a plume that reads "part way" is the analogue one, which was
        // measured out and put behind a flag.
        Check("moving is one state, whatever speed it is",
            new[] { 12.0, 60.0, 120.0, 200.0, 240.0 }.All(s =>
                Math.Abs(worked.RateAt(s) - worked.RateAt(240.0)) < 1e-9),
            $"{worked.RateAt(60.0):F2}/s at a quarter of cruise"
            + $" against {worked.RateAt(240.0):F2}/s at cruise");
        Check("and the step is the whole 2.5x",
            Math.Abs(worked.RateAt(240.0) / worked.RateAt(0.0) - 2.5) < 1e-9,
            $"{worked.RateAt(240.0) / worked.RateAt(0.0):F2}x between the states");
        // The creep at a path bend is a real held speed below cruise, and it is
        // the only one - so it decides whether the threshold has a case to
        // answer. A tank grinding round a corner is working.
        Check("a tank creeping round a bend still counts as working",
            worked.Response(240.0 * MovementProfile.Medium.CornerFraction) == 1.0,
            $"the crawl is {MovementProfile.Medium.CornerFraction * 100.0:F0}% of"
            + $" cruise against a threshold of {worked.MoveThreshold * 100.0:F0}%");
        // A real speed and not one derived from the threshold: written as a
        // fraction of MoveThreshold this passes with the threshold at zero,
        // which is the one setting it exists to rule out.
        Check("but arriving does not flick the plume up",
            worked.Response(0.05) == 0.0,
            "a twentieth of a pixel a second is a tank that has stopped,"
            + $" against a threshold of {worked.MoveThreshold * 240.0:F1} px/s");
        // The step is in the rate, never in the phase - the property the whole
        // class is built round. A discontinuous rate is fine; a discontinuous
        // phase is the pop that integrating exists to prevent.
        var stepping = new ExhaustLoop { Phases = 12, TopSpeed = 240.0 };
        for (int i = 0; i < 120; i++)
            stepping.Advance(0.0, tick);
        double resting = stepping.Phase;
        stepping.Advance(240.0, tick);
        Check("and the step lands in the rate, not in the phase",
            Math.Abs(stepping.Phase - resting - stepping.RateAt(240.0) * tick) < 1e-9,
            $"moved {stepping.Phase - resting:F4} phases, one frame at the working"
            + $" rate is {stepping.RateAt(240.0) * tick:F4}");

        // The analogue model, named explicitly, because a check that silently
        // ran against the delivered one would be a check that does not run: the
        // curve and the straight ramp agree at both ends, and the ends are all a
        // two-state clock has.
        var analogue = new ExhaustLoop { Phases = 12, TopSpeed = 240.0, Binary = false };
        Check("the ramp behind the flag is still the curved one",
            analogue.Response(60.0) > 0.4 && analogue.Response(120.0) > 0.6
            && analogue.Response(60.0) < 1.0,
            $"{analogue.Response(60.0) * 100.0:F0}% of the range up at a quarter"
            + $" of cruise, {analogue.Response(120.0) * 100.0:F0}% at half"
            + " - straight would be 25 and 50");
        Check("working the engine thickens the smoke",
            worked.Density > idle.Density + 0.15,
            $"{idle.Density:F2} at rest against {worked.Density:F2}");
        // On the same curve as the rate, and asserted because it was a choice:
        // both halves are one engine answering one throttle, and two shapes
        // would make the plume quick and thin at the very speeds the shape is
        // there to cover.
        var crawling = new ExhaustLoop { Phases = 12, TopSpeed = 240.0 };
        crawling.Advance(60.0, tick);
        Check("and thickens it on the same curve the rate follows",
            Math.Abs(crawling.Density
                     - (crawling.IdleDensity
                        + (crawling.DriveDensity - crawling.IdleDensity)
                        * crawling.Response(60.0))) < 1e-9,
            $"{crawling.Density:F2} at a quarter of cruise, response"
            + $" {crawling.Response(60.0):F2}");

        // The knob is a multiplier over the pair, which is the whole of what
        // keeps the 2.5x between an idling engine and a working one alive: a
        // slider that set the rate would flatten it on the first drag, and the
        // load ramp is the thing the effect says about the tank.
        var turned = new ExhaustLoop { Phases = 12, TopSpeed = 240.0, Level = 2.0 };
        Check("the exhaust level scales both ends of the ramp",
            Math.Abs(turned.RateAt(0.0) - idle.RateAt(0.0) * 2.0) < 1e-9
            && Math.Abs(turned.RateAt(240.0) - worked.RateAt(240.0) * 2.0) < 1e-9,
            $"{turned.RateAt(0.0):F1}/s at rest and {turned.RateAt(240.0):F1}/s worked");
        Check("and leaves the spread between them where it was measured",
            Math.Abs(turned.RateAt(240.0) / turned.RateAt(0.0)
                     - worked.RateAt(240.0) / idle.RateAt(0.0)) < 1e-9,
            $"{turned.RateAt(240.0) / turned.RateAt(0.0):F2}x either way");
        // The other half of the ramp is not its business. Two questions answered
        // by one control make every A/B taken on it a measurement of both.
        var thick = new ExhaustLoop { Phases = 12, TopSpeed = 240.0, Level = 2.5 };
        for (int i = 0; i < 60; i++)
            thick.Advance(240.0, tick);
        Check("but does not touch how thick the smoke is",
            Math.Abs(thick.Density - worked.Density) < 1e-9,
            $"{thick.Density:F2} turned up against {worked.Density:F2}");
        var stalled = new ExhaustLoop { Phases = 12, TopSpeed = 240.0, Level = 0.0 };
        for (int i = 0; i < 600; i++)
            stalled.Advance(240.0, tick);
        Check("at zero the plume holds its pose",
            stalled.Phase == 0.0 && stalled.Frame == 0,
            $"phase {stalled.Phase:F3} after ten seconds at cruise");
        // The ceiling the caption prints, asserted at the value it is printed
        // from: the slider reaches past it on purpose - a knob that cannot show
        // what too much looks like is not much of a knob - but the tuned setting
        // has to be clear of it, or the plume flickers as delivered.
        Check("the tuned level is clear of the flicker floor",
            ExhaustLoop.FramesPerPhase(worked.RateAt(240.0))
            > ExhaustLoop.FloorFramesPerPhase * 1.5,
            $"{ExhaustLoop.FramesPerPhase(worked.RateAt(240.0)):F1} frames a phase"
            + $" against a floor of {ExhaustLoop.FloorFramesPerPhase:F0}");
        Check("and the slider can be dragged past it",
            ExhaustLoop.FramesPerPhase(new ExhaustLoop { TopSpeed = 240.0, Level = 3.0 }
                .RateAt(240.0)) < ExhaustLoop.FloorFramesPerPhase,
            "too much has to be reachable, or the caption warns about nothing");

        // The phase is integrated, not rate times elapsed time. The second form
        // moves the whole loop when the rate moves, so pulling away after a long
        // idle would jump by the rate difference times the whole idle - several
        // laps here, at exactly the moment the tank starts working for it.
        var steady = new ExhaustLoop { Phases = 12, TopSpeed = 240.0 };
        var opens = new ExhaustLoop { Phases = 12, TopSpeed = 240.0 };
        for (int i = 0; i < 600; i++)           // ten seconds sitting there
        {
            steady.Advance(0.0, tick);
            opens.Advance(0.0, tick);
        }
        opens.Advance(240.0, tick);            // one frame on the throttle
        steady.Advance(0.0, tick);
        double drift = Math.Abs(opens.Phase - steady.Phase);
        double legitimate = (opens.RateAt(240.0) - steady.RateAt(0.0)) * tick;
        Check("opening the throttle does not jump the loop",
            drift <= legitimate * 1.05 + 1e-9,
            $"moved {drift:F4} phases, one frame's worth is {legitimate:F4}");

        // The two rendered effects are indexed by different parts, and swapping
        // them is invisible at heading zero and wrong everywhere else - the tank
        // would trail smoke from wherever the gun happened to be pointing.
        Check("the plume follows the hull and the flash follows the turret",
            EffectLayer.Exhaust(AtlasSet.ExhaustName).FollowsHull
            && !EffectLayer.Additive("flash").FollowsHull
            && !EffectLayer.Normal("smoke").FollowsHull,
            "the outlet is bolted to the engine deck, the muzzle to the gun");
        Check("the plume is kept out of the shot's layer list",
            !AtlasSet.EffectNames.Contains(AtlasSet.ExhaustName),
            "HasEffects requires every name in it, and would switch the "
            + "rendered flash off on a tank that has one but no Engine");

        if (!atlas.HasExhaust)
        {
            Check($"{atlas.Tag} has a rendered exhaust layer", false,
                "no exhaust atlas - split an Engine in this scene");
        }
        else
        {
            Vector2 tankAnchor = atlas.Anchor / (Vector2)atlas.Tile;
            Vector2 plumeAnchor = atlas.AnchorOf(AtlasSet.ExhaustName)
                                  / (Vector2)atlas.TileOf(AtlasSet.ExhaustName);
            Check("the plume shares the tank's anchor, in frame fractions",
                (plumeAnchor - tankAnchor).Length() < 1e-4,
                $"{plumeAnchor} against {tankAnchor}");
            // Unlike the flash, which needs a wider frame to hold a long jet.
            // The plume stands over the deck, near the anchor, so it fits the
            // tank's own tile - and the check that says so is what would catch
            // it silently growing until it clipped.
            Check("the plume needs no wider frame than the tank",
                atlas.TileOf(AtlasSet.ExhaustName) == atlas.Tile,
                $"{atlas.TileOf(AtlasSet.ExhaustName).X}px against"
                + $" tank {atlas.Tile.X}px");
            Check("the plume has phases to loop over", atlas.ExhaustPhases > 1,
                $"{atlas.ExhaustPhases} phases");

            var plumeTiles = new HashSet<int>();
            foreach (int facing in atlas.RenderedFacings())
                for (int phase = 0; phase < atlas.ExhaustPhases; phase++)
                    plumeTiles.Add(
                        atlas.EffectFrame(AtlasSet.ExhaustName, phase, facing));
            Check("every plume phase and heading maps to its own tile",
                plumeTiles.Count == atlas.ExhaustPhases * atlas.Count
                && plumeTiles.Max() < atlas.ExhaustPhases * atlas.Count,
                $"{plumeTiles.Count} distinct of"
                + $" {atlas.ExhaustPhases * atlas.Count}, highest {plumeTiles.Max()}");
        }

        Theme("the wreck");
        {
            // Death is an event and a wreck is a state, and the whole of this
            // prototype is that everything is a function of one age. So what is
            // asserted is the shape of that function, not any number in it.
            var hulk = new Wreck();
            Check("a tank opens intact",
                !hulk.Dead && hulk.Char == 0.0, $"dead {hulk.Dead}");
            // On a live tank the flame is at full: being on fire and being dead
            // are different states and only one of them ends, which is what
            // lets key J burn an intact hull without it settling into a wreck.
            Check("a live tank on fire burns at full",
                hulk.Blaze == 1.0 && hulk.Smoke == 1.0,
                $"blaze {hulk.Blaze:F2}, smoke {hulk.Smoke:F2}");

            Check("the killing blow lands once", hulk.Kill() && !hulk.Kill(),
                "a second round restarted the death");
            for (int i = 0; i < 60; i++)
                hulk.Update(Wreck.CharSeconds / 60.0);
            Check("the paint chars and stops charring",
                Math.Abs(hulk.Char - 1.0) < 1e-6, $"char {hulk.Char:F3}");
            // The flame goes out and the column does not. That asymmetry is the
            // effect: a burnt-out hull smoking is what says where one died from
            // across the board, and a wreck that goes quiet is a tank that was
            // never there.
            for (int i = 0; i < 3600; i++)
                hulk.Update(1.0 / 60.0);
            Check("the flame goes out", hulk.Blaze == 0.0, $"{hulk.Blaze:F3}");
            // Against the number and against zero, because comparing it to the
            // constant it came from cannot fail: a floor of nought is a wreck
            // that stops smoking, which is a tank that was never there, and a
            // floor of one is a column that never thinned.
            Check("the column thins to a floor and stays",
                Math.Abs(hulk.Smoke - Wreck.SmokeFloor) < 1e-6
                && hulk.Smoke > 0.2 && hulk.Smoke < 0.9,
                $"{hulk.Smoke:F3} against {Wreck.SmokeFloor:F2}");
            hulk.Reset();
            Check("repair puts it back",
                !hulk.Dead && hulk.Char == 0.0 && hulk.Blaze == 1.0,
                $"dead {hulk.Dead}, char {hulk.Char:F2}");
        }
        {
            // The one thing the char must not touch. An additive layer darkened
            // along with the paint is the opposite of what a wreck needs - the
            // dark hull is precisely what makes the flame on it work - and the
            // failure would look like a fire that has gone dull rather than like
            // a tint reaching too far.
            var armour = new List<string>();
            var lit = new List<string>();
            var planted = new List<string>();
            foreach (string name in TankSprite.LayerOrder)
            {
                EffectLayer made = TankSprite.MakeLayer(name);
                (made.Armour ? armour : lit).Add(name);
                if (made.Planted)
                    planted.Add(name);
                made.QueueFree();
            }
            // Which layers stand on the ground rather than being carried by the
            // hull, and so sit out the engine tremble - see EffectLayer.Planted.
            // The belts and nothing else: it is not a synonym for "part of the
            // tank" (the turret and the gun are carried) nor for Grounded (the
            // shadow is a mark in the ground plane, not a thing resting on it).
            Check("the belts stand on the ground and the rest of the tank does not",
                AtlasSet.TrackNames.All(planted.Contains)
                && AtlasSet.WreckTrackNames.All(planted.Contains)
                && !planted.Contains("turret")
                && !planted.Contains(AtlasSet.BarrelName)
                && !planted.Contains(AtlasSet.ShadowName)
                && !AtlasSet.ScarNames.Any(planted.Contains),
                string.Join(", ", planted));
            Check("the tank's own layers char with the hull",
                AtlasSet.TrackNames.All(armour.Contains)
                && armour.Contains("turret") && armour.Contains(AtlasSet.BarrelName)
                && AtlasSet.ScarNames.All(armour.Contains),
                string.Join(", ", armour));
            Check("nothing that emits light is charred",
                !lit.Any(n => armour.Contains(n))
                && !armour.Contains(AtlasSet.FireName)
                && !armour.Contains(AtlasSet.BurstName)
                && !armour.Contains("flash")
                // and the ground under it is not the tank either
                && !armour.Contains(AtlasSet.ShadowName),
                string.Join(", ", lit));
            Check("the hull carries the char material itself",
                tank.Material is ShaderMaterial,
                tank.Material?.GetType().Name ?? "none");
            Check("an intact tank is not charred at all", tank.Char == 0.0,
                $"{tank.Char:F3}");

            // The knocked-out pose. A pair of layers per part rather than one
            // layer switching atlas, and what has to hold is that exactly one of
            // each pair draws: both showing puts two turrets on the tank, neither
            // showing takes its turret away, and the second is the quieter of the
            // two failures.
            var pairs = new List<(string Live, string Dead)>
            {
                ("turret", AtlasSet.WreckTurretName),
                (AtlasSet.BarrelName, AtlasSet.WreckTurretName),
                (AtlasSet.TrackNames[0], AtlasSet.WreckTrackNames[0]),
                (AtlasSet.TrackNames[1], AtlasSet.WreckTrackNames[1]),
            };
            bool paired = true, exclusive = true, ordered = true;
            foreach ((string alive, string gone) in pairs)
            {
                EffectLayer a = TankSprite.MakeLayer(alive);
                EffectLayer b = TankSprite.MakeLayer(gone);
                paired &= a.StandIn == gone
                          && a.When == EffectLayer.Life.Alive
                          && b.When == EffectLayer.Life.Dead;
                // with the stand-in present, one of the two and never both
                exclusive &= a.LiveNow(false, true) != a.LiveNow(true, true)
                             && a.LiveNow(false, true) != b.LiveNow(false, true)
                             && b.LiveNow(true, true) != a.LiveNow(true, true);
                // and side by side in the order, because which half is showing
                // must not change where the pair composites
                int i = Array.IndexOf(TankSprite.LayerOrder, alive);
                int j = Array.IndexOf(TankSprite.LayerOrder, gone);
                ordered &= i >= 0 && j >= 0 && Math.Abs(i - j) <= pairs.Count;
                a.QueueFree();
                b.QueueFree();
            }
            Check("every part the wreck re-poses is a live/dead pair", paired,
                string.Join(", ", pairs.Select(p => $"{p.Live}->{p.Dead}")));
            Check("exactly one half of each pair ever draws", exclusive,
                "both would show two turrets, neither would show none");
            Check("the wreck's layers composite where the live ones do", ordered,
                string.Join(" ", TankSprite.LayerOrder));
            // And the clause that keeps an older atlas working: a live part only
            // steps aside when there is something to step aside for. Without it a
            // set rendered before the wreck existed loses its turret the moment a
            // tank dies - no turret at all, which is the quietest failure there is.
            EffectLayer solo = TankSprite.MakeLayer("turret");
            Check("a tank with no wreck pose keeps the one it has",
                solo.LiveNow(true, false) && solo.LiveNow(false, false),
                "a set rendered before the wreck layers must not lose its turret");
            solo.QueueFree();
            // The wreck's belts have no phase axis at all, which is the whole
            // difference from the running ones: a dead belt does not wind, so
            // there is no ground to tie it to and nothing for TrackLoop to drive.
            EffectLayer dead0 = TankSprite.MakeLayer(AtlasSet.WreckTrackNames[0]);
            EffectLayer deadT = TankSprite.MakeLayer(AtlasSet.WreckTurretName);
            Check("a wreck's belt does not wind and its turret keeps its own heading",
                dead0.Clock == EffectLayer.Clocks.Body && dead0.FollowsHull
                && !dead0.Loops && deadT.Clock == EffectLayer.Clocks.Body
                && !deadT.FollowsHull && dead0.Armour && deadT.Armour,
                $"belt {dead0.Clock} hull={dead0.FollowsHull},"
                + $" turret {deadT.Clock} hull={deadT.FollowsHull}");
            dead0.QueueFree();
            deadT.QueueFree();
            // The pose is the event and the char is the aftermath, so they are two
            // fields and not one: a flag read off the char would hold the turret
            // level until the hull had started to blacken.
            tank.Wrecked = true;
            bool posedAtOnce = tank.Wrecked && tank.Char == 0.0;
            tank.Wrecked = false;
            Check("the turret drops on the hit and the paint blackens after it",
                posedAtOnce && !tank.Wrecked,
                "the pose must not wait for the char, nor the char for the pose");
            // The middle button pans and destroys, and the gesture is all that
            // separates them. A pan that killed a tank it happened to travel over
            // would be the worst kind of overload: it fires while the hand is
            // busy doing something else.
            Check("a middle tap destroys and a middle drag does not",
                SceneRoot.MiddleTap(new Vector2(400, 300), new Vector2(400, 300))
                && SceneRoot.MiddleTap(new Vector2(400, 300), new Vector2(402, 301))
                && !SceneRoot.MiddleTap(new Vector2(400, 300), new Vector2(420, 300))
                && !SceneRoot.MiddleTap(new Vector2(400, 300), new Vector2(400, 340)),
                "a drag reads as a tap or the other way round");
            // The one that let every tank on the board open already darkened.
            // COLOR arrives holding the texel, so sampling TEXTURE and
            // multiplying it in again squares the sprite - measured exactly as
            // c*c - and burn = 0 stops being a no-op. Asserted as the absence of
            // the sample rather than as a colour, because that is the edit that
            // brings it back, and because the colour itself needs two renders to
            // compare and this runs in one.
            Check("the char works on COLOR and never samples the texture again",
                !TankSprite.CharShaderCode.Contains("texture(")
                && TankSprite.CharShaderCode.Contains("COLOR.rgb"),
                TankSprite.CharShaderCode.Contains("texture(")
                    ? "it samples TEXTURE" : "it does not touch COLOR.rgb");
        }

        Theme("the burning wreck");
        var fire = new BurnLoop { Phases = 12 };
        var fireSeen = new HashSet<int>();
        var smokeSeen = new HashSet<int>();
        bool burnInRange = true;
        for (int i = 0; i < 600; i++)
        {
            fire.Advance(tick);
            fireSeen.Add(fire.FireFrame);
            smokeSeen.Add(fire.SmokeFrame);
            if (fire.FirePhase < 0.0 || fire.FirePhase >= fire.Phases
                || fire.SmokePhase < 0.0 || fire.SmokePhase >= fire.Phases)
                burnInRange = false;
        }
        Check("both burning loops stay inside the phases that were rendered",
            burnInRange,
            $"fire {fire.FirePhase:F3}, smoke {fire.SmokePhase:F3}"
            + $" of {fire.Phases}");
        Check("a lap visits every phase of both",
            fireSeen.Count == fire.Phases && smokeSeen.Count == fire.Phases,
            $"fire {fireSeen.Count}, smoke {smokeSeen.Count} of {fire.Phases}");

        // The two halves run at different rates off the same rendered phases,
        // which is legal because each closes on itself independently. It is
        // asserted because the obvious arrangement - one clock, one phase - is
        // what a later edit would reach for, and it forces a choice between a
        // smouldering flame and a column going up like a jet.
        Check("the flame runs faster than its column",
            fire.FireRate > fire.SmokeRate * 1.5,
            $"{fire.FireRate:F1}/s against {fire.SmokeRate:F1}/s");
        Check("the two halves are not in step",
            Math.Abs(fire.FirePhase - fire.SmokePhase) > 1e-6,
            $"fire {fire.FirePhase:F3}, smoke {fire.SmokePhase:F3}");

        // No load ramp, where the exhaust has one on purpose. An engine under
        // load smokes harder; a fire does not burn harder because the tank is
        // moving, and BurnLoop takes no speed at all - this asserts the shape of
        // the API rather than a value, which is the only way to state it.
        var still = new BurnLoop { Phases = 12 };
        var driven = new BurnLoop { Phases = 12 };
        for (int i = 0; i < 120; i++)
        {
            still.Advance(tick);
            driven.Advance(tick);
        }
        Check("the fire burns the same standing still as at speed",
            Math.Abs(still.FirePhase - driven.FirePhase) < 1e-9
            && Math.Abs(still.SmokePhase - driven.SmokePhase) < 1e-9,
            "speed is not an input to BurnLoop");

        Check("the burning pair follows the hull, like the plume",
            EffectLayer.BurningFire(AtlasSet.FireName).FollowsHull
            && EffectLayer.BurningSmoke(AtlasSet.BurnName).FollowsHull,
            "the ports are stamped on the engine deck, not on the turret");
        Check("the burning pair is kept out of the shot's layer list",
            !AtlasSet.EffectNames.Contains(AtlasSet.FireName)
            && !AtlasSet.EffectNames.Contains(AtlasSet.BurnName),
            "HasEffects requires every name in it, and would switch the "
            + "rendered flash off on a tank that has one but no Engine");
        // Order is the effect: the column exists to put something dark under an
        // additive flame, and reversed it greys out the fire it should back.
        Check("the column is added to the tree before the flame",
            Array.IndexOf(TankSprite.LayerOrder, AtlasSet.BurnName)
            < Array.IndexOf(TankSprite.LayerOrder, AtlasSet.FireName),
            string.Join(" -> ", TankSprite.LayerOrder));

        // Every emitting layer is emitting, on both boards, and that is one
        // statement in two places: the layer adds light without touching alpha,
        // and the stage displays its render target premultiplied. Either half back
        // on straight alpha and the flame dies inside the target - an additive
        // layer raises rgb over a background whose alpha stays at nought, and the
        // quad then scales that rgb by almost nought. Measured on one burning
        // tank against the canvas: 9 levels of red gone, and the flame visibly
        // duller for it.
        //
        // Read off the shaders rather than described, because the failure is a
        // render mode and nothing else about the pass changes with it.
        var emitters = new[]
        {
            TankSprite.MakeLayer(AtlasSet.FireName),
            TankSprite.MakeLayer(AtlasSet.BurstName),
            TankSprite.MakeLayer("flash"),
        };
        Check("light adds to the ground on either board",
            emitters.All(l => ReferenceEquals(l.Material, EffectLayer.Glow))
            && EffectLayer.Glow.Shader.Code.Contains("blend_premul_alpha")
            && Stage3D.PaintShader.Contains("blend_premul_alpha"),
            emitters.Any(l => !ReferenceEquals(l.Material, EffectLayer.Glow))
                ? "an emitting layer is drawing with something else"
                : "one of the two shaders is not premultiplied, so the flame is "
                  + "being multiplied by an alpha it never asked to have");
        Check("and it hides nothing while it does",
            EffectLayer.Glow.Shader.Code.Contains("COLOR.a, 0.0"),
            "an emitting layer that writes alpha takes the ground away by as "
            + "much as it lights it - the dark half beneath it is the burn "
            + "column's job, not the flame's");

        if (!atlas.HasBurning)
        {
            Check($"{atlas.Tag} has both rendered burning layers", false,
                "no fire/burn atlas - split an Engine in this scene");
        }
        else
        {
            Vector2 burnTankAnchor = atlas.Anchor / (Vector2)atlas.Tile;
            foreach (string layer in new[] { AtlasSet.FireName, AtlasSet.BurnName })
            {
                Vector2 anchor = atlas.AnchorOf(layer) / (Vector2)atlas.TileOf(layer);
                Check($"the {layer} layer shares the tank's anchor, in frame fractions",
                    (anchor - burnTankAnchor).Length() < 1e-4,
                    $"{anchor} against {burnTankAnchor}");
            }
            Check("both halves hold the same number of phases",
                atlas.PhasesOf(AtlasSet.FireName)
                == atlas.PhasesOf(AtlasSet.BurnName),
                $"fire {atlas.PhasesOf(AtlasSet.FireName)},"
                + $" burn {atlas.PhasesOf(AtlasSet.BurnName)}");
            // The asymmetry is the point and is why this is asserted rather than
            // left to the renderer: the column is the one effect that stands
            // *above* the tank, so height is its cost and it gets its own frame;
            // the flame sits on the deck and must not quietly acquire one.
            Check("the flame fits the tank's frame and the column is given more",
                atlas.TileOf(AtlasSet.FireName) == atlas.Tile
                && atlas.TileOf(AtlasSet.BurnName).X >= atlas.Tile.X,
                $"burn {atlas.TileOf(AtlasSet.BurnName).X}px,"
                + $" fire {atlas.TileOf(AtlasSet.FireName).X}px,"
                + $" tank {atlas.Tile.X}px");

            var burnTiles = new HashSet<int>();
            foreach (int facing in atlas.RenderedFacings())
                for (int phase = 0; phase < atlas.BurnPhases; phase++)
                    burnTiles.Add(atlas.EffectFrame(AtlasSet.BurnName, phase, facing));
            Check("every column phase and heading maps to its own tile",
                burnTiles.Count == atlas.BurnPhases * atlas.Count
                && burnTiles.Max() < atlas.BurnPhases * atlas.Count,
                $"{burnTiles.Count} distinct of"
                + $" {atlas.BurnPhases * atlas.Count}, highest {burnTiles.Max()}");
        }

        Theme("the column built rather than read");
        // Every one of these is about something that fails without a mark on the
        // screen: a quarter turn of error in the projection still draws a column,
        // a loop that does not close still draws smoke, and an ink read out of
        // the config rather than off the pixels draws a column of the wrong
        // grey that looks like a choice. See ProcSmoke.
        var built = new ProcSmoke();
        Check($"{atlas.Tag} carries a stamped port",
            atlas.HasPorts,
            atlas.Ports.Count == 0
                ? "no ports block - run stamp_ports.py over Sprites/"
                : $"hull length {atlas.HullLength:F3}, units per pixel"
                  + $" {atlas.UnitsPerPixel:F6}");

        if (atlas.HasPorts && atlas.HasBurning)
        {
            // The quarter turn. The projection carries a +90 for the front being
            // -Y, which is an import convention and not a measurement, so it is
            // put against the one thing that already knows where the column goes:
            // the rendered layer's own trimmed frame. A sign or an axis swapped
            // still produces a tidy column, ninety degrees from the vent.
            AtlasSet.Port port = atlas.Ports[0];
            double worstSeat = 0.0;
            int seatHeading = 0;
            foreach (int facing in atlas.RenderedFacings())
            {
                int frame = atlas.EffectFrame(AtlasSet.BurnName, 0, facing);
                Vector2 size = atlas.SizeOf(AtlasSet.BurnName, frame);
                if (size.X <= 0.0f || size.Y <= 0.0f)
                    continue;
                Vector2 lo = atlas.OffsetOf(AtlasSet.BurnName, frame)
                             - atlas.AnchorOf(AtlasSet.BurnName);
                var box = new Rect2(lo, size);
                Vector2 seat = atlas.Project(port.Point, facing);
                double out_ = Math.Max(
                    Math.Max(box.Position.X - seat.X, seat.X - box.End.X),
                    Math.Max(box.Position.Y - seat.Y, seat.Y - box.End.Y));
                if (out_ > worstSeat)
                {
                    worstSeat = out_;
                    seatHeading = facing;
                }
            }
            Check("the stamped port projects inside the rendered column at every"
                  + " heading",
                worstSeat <= 12.0,
                $"worst {worstSeat:F1}px outside its own frame at {seatHeading} deg");

            // World up has to come out straight up on the screen whatever the hull
            // is doing - it is the one direction the carousel cannot turn - and it
            // has to be exactly as long as the height map says a unit of height is.
            // Two statements of one number is how a map and a column come apart.
            double worstLean = 0.0, worstRise = 0.0;
            double wantRise = Math.Cos(Mathf.DegToRad(atlas.Elevation))
                              / atlas.UnitsPerPixel;
            foreach (int facing in atlas.RenderedFacings())
            {
                Vector2 up = atlas.Project(Plumes.Up, facing);
                worstLean = Math.Max(worstLean, Math.Abs(up.X));
                worstRise = Math.Max(worstRise, Math.Abs(-up.Y - wantRise));
            }
            Check("world up projects straight up, at the height map's own scale",
                worstLean < 0.01 && worstRise < 0.01,
                $"leans {worstLean:F3}px, off the map's rise by {worstRise:F3}px"
                + $" of {wantRise:F1}");

            // The path leaves the way the vent points and ends up climbing, and
            // its length is the distance to the tip rather than the distance
            // travelled. Writing the second one instead is a column of the right
            // shape and the wrong height, which nothing else here would notice.
            Vector3[] path = Plumes.Path(port.Dir, 1.0f, built.Buoyancy, built.TurnPower,
                                         -1.0f / built.Slowing);
            Vector3 first = (path[1] - path[0]).Normalized();
            Vector3 last = (path[^1] - path[^2]).Normalized();
            double columnWalked = 0.0;
            for (int i = 1; i < path.Length; i++)
                columnWalked += (path[i] - path[i - 1]).Length();
            Check("the column leaves along the vent and ends up going up",
                first.Dot(port.Dir) > 0.99f && last.Dot(Plumes.Up) > 0.97f,
                $"leaves at {Mathf.RadToDeg(Math.Acos(first.Dot(port.Dir))):F1} deg"
                + $" off the vent, ends {Mathf.RadToDeg(Math.Acos(last.Dot(Plumes.Up))):F1}"
                + " deg off vertical");
            // Two halves: the tip lands exactly on the reach it was given, and
            // the way there is longer than that. Normalised on the arc instead -
            // the obvious other reading - the tip would fall short by the
            // difference, which on this vent is only a percent and a half.
            Check("and its reach is to the tip, not the distance travelled",
                Math.Abs(path[^1].Length() - 1.0) < 1e-3 && columnWalked > 1.002,
                $"tip at {path[^1].Length():F4}, travelled {columnWalked:F4}");

            // The loop closes by construction: age advances by 1/n a lap for every
            // puff at once, so a whole lap is the same picture and one step of the
            // lap columnSlides puff k onto where k+1 was. Neither is a tolerance - both
            // are exact, which is the whole reason there is no phase table.
            ProcSmoke.Puff[] atZero = built.Build(port, atlas.HullLength, 0.0f);
            ProcSmoke.Puff[] atOne = built.Build(port, atlas.HullLength, 1.0f);
            double worstLap = 0.0;
            for (int i = 0; i < atZero.Length; i++)
                worstLap = Math.Max(worstLap,
                    (atZero[i].Centre - atOne[i].Centre).Length());
            Check("a whole lap of the column is the same picture",
                worstLap < 1e-5, $"worst puff moved {worstLap:E2} world units");

            ProcSmoke.Puff[] atStep = built.Build(port, atlas.HullLength,
                                                  1.0f / built.Puffs);
            var wasAges = atZero.Select(q => (double)q.Age).OrderBy(a => a).ToArray();
            var nowAges = atStep.Select(q => (double)q.Age).OrderBy(a => a).ToArray();
            double columnSlid = 0.0;
            for (int i = 0; i + 1 < wasAges.Length; i++)
                columnSlid = Math.Max(columnSlid, Math.Abs(nowAges[i + 1] - wasAges[i]
                                               - 1.0 / built.Puffs));
            Check("and one step of it slides each puff onto the next one's place",
                columnSlid < 1e-5, $"worst {columnSlid:E2} of a life out");

            // Hashed on the puff and never on the phase. Mix the phase in and
            // every puff becomes a different puff every frame - the column boils
            // rather than drifting, and no amount of tuning the shape fixes it.
            // Tone is the one per-puff draw that does not move with age, so it is
            // what says whether the dice were rolled again.
            var wasTone = atZero.Select(q => Math.Round(q.Tone, 6))
                                .OrderBy(t => t).ToArray();
            var nowTone = atStep.Select(q => Math.Round(q.Tone, 6))
                                .OrderBy(t => t).ToArray();
            Check("a puff keeps its own dice as the lap turns",
                wasTone.SequenceEqual(nowTone),
                "the stagger is hashed on the phase as well as the puff, so the "
                + "column boils instead of drifting");

            // Solidity has to reach nothing at both ends of a life, or a puff
            // arrives and leaves with a step. The ends are what onset and fade are
            // for and they are easy to lose to a tidier expression.
            ProcSmoke.Puff youngest = atZero.OrderBy(q => q.Age).First();
            ProcSmoke.Puff oldest = atZero.OrderBy(q => q.Age).Last();
            Check("a puff comes up from nothing and goes back to it",
                youngest.Alpha < built.Alpha * 0.55f
                && oldest.Alpha < built.Alpha * 0.35f,
                $"youngest {youngest.Alpha:F3} at age {youngest.Age:F3},"
                + $" oldest {oldest.Alpha:F3} at age {oldest.Age:F3},"
                + $" against a ceiling of {built.Alpha:F2}");

            // The built column has to come out the size the rendered one is: same
            // model, same numbers, so a difference here is a number read against
            // the wrong thing - which is the mistake the forest's port made with
            // three of them at once.
            Rect2? span = null;
            foreach (ProcSmoke.Puff q in atZero)
            {
                if (q.Alpha <= 0.002f)
                    continue;
                Vector2 at = atlas.Project(q.Centre, 270.0);
                float r = (float)(q.Radius * (1.0 + built.Wobble * 0.5)
                                  / atlas.UnitsPerPixel);
                var one = new Rect2(at - Vector2.One * r, Vector2.One * r * 2.0f);
                span = span is null ? one : span.Value.Merge(one);
            }
            int zero = atlas.EffectFrame(AtlasSet.BurnName, 0, 270.0);
            Vector2 drawn = atlas.SizeOf(AtlasSet.BurnName, zero);
            Check("and it comes out the size the rendered one is",
                span is not null && drawn.X > 0.0f
                && Math.Abs(span.Value.Size.X - drawn.X) < drawn.X * 0.30f
                && Math.Abs(span.Value.Size.Y - drawn.Y) < drawn.Y * 0.35f,
                span is null ? "nothing built"
                    : $"built {span.Value.Size.X:F0}x{span.Value.Size.Y:F0}px,"
                      + $" rendered {drawn.X:F0}x{drawn.Y:F0}px");
        }

        // Exactly one of the pair draws. Both is a tank smoking twice; neither is
        // a switch that took the smoke away rather than remaking it, and that is
        // the half a flag on a set with no port would hit.
        var pairTank = new TankSprite { Atlas = atlas, Burning = true };
        var rendered = TankSprite.MakeLayer(AtlasSet.BurnName);
        rendered.Tank = pairTank;
        pairTank.BurnPhase = 0;
        pairTank.ProceduralSmoke = false;
        bool readWhenOff = rendered.Showing >= 0;
        pairTank.ProceduralSmoke = true;
        bool readWhenOn = rendered.Showing >= 0;
        Check("exactly one of the built column and the read one draws",
            readWhenOff && (!readWhenOn || !atlas.HasPorts),
            $"rendered layer shows {(readWhenOff ? "yes" : "no")} with the flag off,"
            + $" {(readWhenOn ? "yes" : "no")} with it on");
        Check("and a set with no stamped port keeps the rendered one whatever the"
              + " flag says",
            !new TankSprite { ProceduralSmoke = true }.SmokeIsProcedural,
            "a flag honoured on a set that cannot build the column takes the "
            + "smoke away instead of remaking it");

        // Three claims about the shader, each of which fails into a picture that
        // looks deliberate. Read off the code for EffectLayer.Glow's reason: what
        // goes wrong is a render mode or a single term, and nothing else about the
        // pass changes with it.
        string column = ProcSmoke.ColumnCode;
        Check("the column takes light away rather than adding it",
            column.Contains("blend_mix") && !column.Contains("blend_add"),
            "a column that adds light is the one arrangement that washes out the "
            + "flame it exists to hold up");
        Check("a puff is one surface, because that is what the render put down",
            !column.Contains("(1.0 - one) * (1.0 - one)"),
            "the material asks for its transparent back only where that property "
            + "still exists and EEVEE Next dropped it, so doubling here corrects "
            + "the model toward a setting the renderer ignored. Measured on the "
            + "plume, three tanks driving, median levels off the render: single "
            + "14/22/19 against the render's 16/22/20, doubled 24/37/33");
        // Read off what the shaders were compiled with, not off the template,
        // because the block came from Plumes and a marker left unreplaced is
        // exactly the failure this would otherwise miss.
        Check("the height map is sampled without blending",
            ProcSmoke.ColumnShaderCode.Contains("depth_tex : filter_nearest"),
            "blending two heights across a silhouette edge invents a surface "
            + "halfway between the tank and nothing");
        Check("and both built layers cut themselves against the same map",
            ProcSmoke.ColumnShaderCode.Contains(Plumes.DepthCode)
            && ProcFire.BlazeShaderCode.Contains(Plumes.DepthCode),
            "one of the pair is holding itself out by something else, so the "
            + "flame and the column disagree about where the tank is");
        Check("and a puff at the map's own floor and ceiling reads 0 and 1",
            atlas.HeightHigh > atlas.HeightLow
            && Math.Abs(Plumes.LiftOf(atlas, (float)atlas.HeightLow)) < 1e-4
            && Math.Abs(Plumes.LiftOf(atlas, (float)atlas.HeightHigh) - 1.0f) < 1e-4,
            $"map runs {atlas.HeightLow:F3}..{atlas.HeightHigh:F3}");

        Theme("the flame built rather than read");
        // The one that matters most is the first: the forest and the tank now burn
        // by one text. Everything else here is about a term that fails into a
        // picture somebody would take for a choice.
        var blaze = new ProcFire();
        Check("the tree and the tank burn by the same statement of what a lick is",
            Stage3D.BlazingCode.Contains(Stage3D.FlameInk)
            && ProcFire.BlazeShaderCode.Contains(Stage3D.FlameInk),
            "one of the two is carrying its own copy of the ramp, and a copy "
            + "still names every function it defines - so it compiles, draws, and "
            + "parts from the other where nobody puts them side by side");
        Check("and the ramp is in that shared text rather than in either shader",
            Stage3D.FlameInk.Contains("vec4 flame_ramp(")
            && !ProcFire.BlazeShaderCode.Replace(Stage3D.FlameInk, "")
                        .Contains("flame_ramp("),
            "the tank's shader defines a ramp of its own");
        Check("the flame adds light where the column takes it away",
            ProcFire.BlazeShaderCode.Contains("blend_premul_alpha")
            && ProcSmoke.ColumnCode.Contains("blend_mix"),
            "the pair is one effect and the order is the effect: dark under, light "
            + "over. Either half in the other's mode washes out the one it holds up");
        Check("an element is one surface here too, and this one measured worse"
              + " for it",
            !ProcFire.BlazeShaderCode.Contains("flame_over(e, e)"),
            "departure from the rendered flame goes 30/15/26 to 33/21.5/32.5 "
            + "median levels without the doubling, and it is still the right "
            + "answer: the doubling was narrowing the mass gap written down "
            + "beside it, and two wrong terms holding each other up read as one "
            + "correct one");
        Check("and it hands its light over premultiplied, hiding nothing",
            ProcFire.BlazeShaderCode.Contains("fire.rgb * level, 0.0"),
            "the stage shows its target premultiplied, so a straight-alpha emitter "
            + "is scaled by an alpha it never asked to have - EffectLayer.Glow's "
            + "measurement, nine levels of red");

        if (atlas.HasPorts && atlas.HasBurning)
        {
            AtlasSet.Port port = atlas.Ports[0];

            // Positive against the column's negative, and it is the one number that
            // tells a flame from smoke: it crowds the elements at the seat and pulls
            // them apart toward the tips. Zero is an evenly spaced ladder.
            Vector3[] hot = Plumes.Path(port.Dir, 1.0f, blaze.Buoyancy, blaze.TurnPower,
                                        blaze.Gather);
            Vector3[] cool = Plumes.Path(port.Dir, 1.0f, built.Buoyancy,
                                         built.TurnPower, -1.0f / built.Slowing);
            Check("the flame crowds its elements at the seat and the column spreads"
                  + " them",
                Plumes.Along(hot, 0.5f).Length() < 0.5f
                && Plumes.Along(cool, 0.5f).Length() > 0.5f,
                $"at half a life the flame is {Plumes.Along(hot, 0.5f).Length():F3}"
                + $" of the way up and the column {Plumes.Along(cool, 0.5f).Length():F3}");

            // An element is long, so which way it lies is not decoration: got wrong
            // it is a lick lying across its own column. The path's tangent is what
            // it lies along, and at the seat that is the vent.
            Vector3 first = Plumes.Going(hot, 0.0f);
            Vector3 last = Plumes.Going(hot, 1.0f);
            Check("a lick lies along the flow, which leaves down the vent and ends"
                  + " up climbing",
                Math.Abs(first.Length() - 1.0f) < 1e-4f
                && first.Dot(port.Dir) > 0.99f && last.Dot(Plumes.Up) > 0.97f,
                $"leaves {Mathf.RadToDeg(Math.Acos(first.Dot(port.Dir))):F1} deg off"
                + $" the vent, ends {Mathf.RadToDeg(Math.Acos(last.Dot(Plumes.Up))):F1}"
                + " deg off vertical");

            ProcFire.Part[] lapZero = blaze.Build(port, atlas.HullLength, 0.0f);
            // And that the queue is actually laid along it. The claim above is
            // about the path; this one is about what Build did with it, and
            // without it a lick lying across its own column passes.
            double worstLie = 0.0;
            foreach (ProcFire.Part q in lapZero.Skip(blaze.SeatBlobs)
                                               .Take(blaze.Tongues))
                worstLie = Math.Max(worstLie,
                    1.0 - q.Flow.Dot(Plumes.Going(hot, q.Age)));
            Check("and every lick in the queue is laid along it",
                worstLie < 1e-4,
                $"worst lick is {Mathf.RadToDeg(Math.Acos(1.0 - worstLie)):F1} deg"
                + " off the flow at its own age");
            ProcFire.Part[] lapOne = blaze.Build(port, atlas.HullLength, 1.0f);
            double worstLap = 0.0;
            for (int i = 0; i < lapZero.Length; i++)
                worstLap = Math.Max(worstLap,
                    (lapZero[i].Centre - lapOne[i].Centre).Length()
                    + Math.Abs(lapZero[i].Gain - lapOne[i].Gain));
            // The seat is the one part of the effect that never moves, so it is the
            // one part where a jump at the wrap is obvious - which is why its
            // flicker runs a whole number of cycles a lap rather than any wave that
            // happened to look right.
            Check("a whole lap of the flame is the same picture, seat included",
                worstLap < 1e-4, $"worst element moved {worstLap:E2}");

            Check("the seat sits proud of the grille rather than half inside the"
                  + " deck",
                lapZero.Take(blaze.SeatBlobs).All(
                    q => (q.Centre - port.Point).Dot(port.Dir) > 0.0f),
                "half of every seat blob is inside the deck and only shows on the "
                + "headings where the holdout happens not to cut it");

            // The column is the effect that buys height; this one sits on the deck
            // and must not quietly acquire any. Asserted against the rendered
            // flame's own frame, which is the thing that would have to grow.
            int zeroFrame = atlas.EffectFrame(AtlasSet.FireName, 0, 270.0);
            Vector2 drawn = atlas.SizeOf(AtlasSet.FireName, zeroFrame);
            float top = 0.0f, wide = 0.0f;
            foreach (ProcFire.Part q in lapZero)
            {
                if (q.Gain <= 0.002f)
                    continue;
                Vector2 at = atlas.Project(q.Centre, 270.0);
                float r = (float)(q.Radius * q.Stretch / atlas.UnitsPerPixel);
                top = Math.Min(top, at.Y - r);
                wide = Math.Max(wide, Math.Abs(at.X) + r);
            }
            Check("and the built flame stays inside the rendered one's own frame",
                drawn.Y > 0.0f && -top < drawn.Y * 1.25f && wide * 2.0f < drawn.X * 2.2f,
                $"built reaches {-top:F0}px up and {wide:F0}px out, rendered frame"
                + $" {drawn.X:F0}x{drawn.Y:F0}px");
        }

        var firePair = new TankSprite { Atlas = atlas, Burning = true };
        var readFire = TankSprite.MakeLayer(AtlasSet.FireName);
        readFire.Tank = firePair;
        firePair.FirePhase = 0;
        firePair.ProceduralFire = false;
        bool fireWhenOff = readFire.Showing >= 0;
        firePair.ProceduralFire = true;
        bool fireWhenOn = readFire.Showing >= 0;
        Check("exactly one of the built flame and the read one draws",
            fireWhenOff && (!fireWhenOn || !atlas.HasPorts),
            $"rendered layer shows {(fireWhenOff ? "yes" : "no")} with the flag off,"
            + $" {(fireWhenOn ? "yes" : "no")} with it on");
        // Two switches on one effect, and they are meant to be pulled apart: the
        // column is what the flame is composited against, so a built flame over a
        // read column is the picture that says which half moved.
        Check("and the two halves switch apart",
            !new TankSprite { Atlas = atlas, ProceduralFire = true }.SmokeIsProcedural
            && !new TankSprite { Atlas = atlas, ProceduralSmoke = true }.FireIsProcedural,
            "one flag drives both, so neither half can be judged on its own");

        Theme("the burst in the ground");
        // ground_he, and the first of the family docs/blast.md lays out. Three of
        // these are about the same thing from different sides: a burst is made of
        // the forest's own elements, thrown by one statement of a throw, and it
        // has to fit in the quad that runs it per fragment.
        var burst = new ProcBlast();
        Check("the burst is made of the same elements as the forest's fire",
            ProcBlast.DustCode.Contains(Stage3D.FlameInk)
            && ProcBlast.FireCode.Contains(Stage3D.FlameInk),
            "one of the two halves is carrying its own copy of what an element "
            + "is, and a copy still names every function it defines - so it "
            + "compiles, draws, and parts from the other where nobody looks");
        Check("and both halves are thrown by one statement of a throw",
            ProcBlast.DustCode.Contains("vec4 blast_throw(")
            && ProcBlast.FireCode.Contains("vec4 blast_throw(")
            && ProcBlast.DustCode.Contains("blast_throw(a, reach")
            && ProcBlast.FireCode.Contains("blast_throw(a, reach"),
            "the earth and the sparks are thrown by the same explosion, so two "
            + "paths for them is two answers to one question");
        Check("the dust takes light away where the fire adds it",
            ProcBlast.FireCode.Contains("blend_add")
            && !ProcBlast.DustCode.Contains("blend_add"),
            "the pair is one effect and the order is the effect - dark under, "
            + "light over. The additive half over pale ground arrives at almost "
            + "nothing without the dark one behind it");

        // The pixel version of this is a bounding box against the quad, and it is
        // in the note on Flank. This is the same claim asked of the model, which
        // is the half that can be asked without a board: everything thrown has to
        // land inside the rectangle the fragment stage covers, because what
        // reaches the edge is not faded, it is sliced off flat.
        // Off the object rather than out of the shader text, because this is the
        // one number C# owns: the quad is built in C# and the throw is quoted
        // against it, so the shader is told what it is on every material.
        float blastReach = burst.Reach;
        float blastSpread = ProcBlast.Uniform(ProcBlast.DustCode, "spread");
        float blastBorn = ProcBlast.Uniform(ProcBlast.DustCode, "clod_born");
        float blastClod = ProcBlast.Uniform(ProcBlast.DustCode, "clod_size");
        float blastCollarOut = ProcBlast.Uniform(ProcBlast.DustCode, "collar_spread");
        float blastCollarR = ProcBlast.Uniform(ProcBlast.DustCode, "collar_size");
        float blastCollarFlat = ProcBlast.Uniform(ProcBlast.DustCode, "collar_flat");
        float blastClimb = ProcBlast.Uniform(ProcBlast.DustCode, "column_climb");
        float blastSparkRun = ProcBlast.Uniform(ProcBlast.FireCode, "spark_reach");
        float blastLean = ProcBlast.Uniform(ProcBlast.DustCode, "clod_wide");
        float blastColumnR = ProcBlast.Uniform(ProcBlast.DustCode, "column_size");
        // The widest and the tallest anything gets, at the top of each family's
        // own size roll - 2.1 for a clod and 1.45 for the other two, both read off
        // the loops beside them.
        //
        // Two terms of this were wrong the first time and no screenshot caught
        // either, which is the case for asking the model as well as the pixels.
        // The cone's sideways travel is its lean's sine and not its whole throw,
        // so the bound came out a fifth too wide; and the widest element in the
        // burst is not the cone at all but the collar, which lies flat and so
        // reaches out by its own length rather than by its radius.
        float blastWidest = Mathf.Max(
            blastBorn + blastReach * blastSpread * Mathf.Sin(blastLean)
                + blastClod * 2.1f,
            blastCollarOut + blastCollarR * 1.45f * blastCollarFlat);
        float blastTallest = Mathf.Max(
            Mathf.Max(blastReach + blastClod * 2.1f, blastReach * blastSparkRun),
            blastClimb + blastColumnR * 1.45f);
        Check("everything the burst throws lands inside its own quad",
            blastWidest > 0.0f && blastTallest > 0.0f
            && blastWidest < burst.Flank && blastTallest < burst.Tall,
            $"reaches {blastWidest:F3} across and {blastTallest:F3} up against a quad of "
            + $"{burst.Flank:F3} and {burst.Tall:F3} tile widths");
        Check("and the quad is not so big that the fragments are wasted",
            blastWidest > burst.Flank * 0.55f && blastTallest > burst.Tall * 0.55f,
            "every fragment of this quad runs eighty-odd sections, so margin "
            + "nobody draws in is not free the way an oversized atlas cell is");

        // <b>One mass, not a heap of shaded ellipses</b> - the change the burst
        // was asked for by eye and the reason the dust has a pass of its own. Each
        // element hands back thickness and lean; nothing composites an element
        // over another, because doing so gives every one of them its own dark lip
        // and its own lit ridge and the eye then counts them. Asserted on the text
        // because the failure it guards against is a return to flame_over, which
        // compiles, draws, and looks like a pile of potatoes.
        Check("the dust is summed into one mass rather than composited",
            ProcBlast.DustCode.Contains("void dust_part(")
            // Against the ink's own text removed, because the ink is where
            // flame_over is declared - the claim is that the dust never calls it,
            // not that the shared kit stopped offering it. The plume's check has
            // the same shape for the same reason.
            && !ProcBlast.DustCode.Replace(Stage3D.FlameInk, "")
                         .Contains("flame_over("),
            "an element drawn over an element is a thing beside a thing, and "
            + "forty-four of them read as forty-four of them - which is what a "
            + "cloud is not");
        // <b>A cloud's profile, not a sphere's, and the numbers are measured.</b>
        // The CC0 pack's painted puff - the thing whose smoke reads soft - fits
        // (1 - q)^2.57 across all sixty-four of its cells and peaks at 0.35 alpha,
        // reaching 0.95 nowhere in the sprite. A sphere's section is (1 - q)^0.5
        // with infinite slope at its rim, which is an edge no amount of softening
        // elsewhere can undo. This is the check that stops it going back.
        Check("the dust wears a cloud's profile and the fire a sphere's",
            ProcBlast.Uniform(ProcBlast.DustCode, "dust_soft") > 2.0f
            && ProcBlast.DustCode.Contains("flame_reach(")
            && !ProcBlast.DustCode.Replace(Stage3D.FlameInk, "")
                         .Contains("flame_facing(")
            && ProcBlast.FireCode.Contains("flame_blob("),
            $"dust exponent {ProcBlast.Uniform(ProcBlast.DustCode, "dust_soft"):F2} "
            + "against a sphere's 0.5 - and a sphere's rim is where the hard edge "
            + "was");
        Check("and both are built on one statement of where a fragment is",
            Stage3D.FlameInk.Contains("float flame_reach(")
            && Stage3D.FlameInk.Contains("flame_reach(at, centre, half_w, along, flow, lump_seed, down)"),
            "the frame - the ellipse, its flow and its wobble - is shared and the "
            + "profile is not, which is the only split that avoids a second copy "
            + "of the arithmetic");
        Check("the dust is never opaque",
            ProcBlast.Uniform(ProcBlast.DustCode, "dust_ink") < 0.85f,
            "earth in the air is not a wall; the pack's own puff peaks at 0.35 "
            + "alpha and its softness is mostly that");
        Check("and its bands are not taken whole",
            ProcBlast.Uniform(ProcBlast.DustCode, "dust_band") < 1.0f,
            "quantised light on a mass that saturates to one gives slabs, and the "
            + "line between two slabs is a hard edge however soft the alpha under "
            + "it is - which is what it was");
        Check("its silhouette is torn as well as soft",
            ProcBlast.DustCode.Contains("ember_fbm(at * dust_grain")
            && ProcBlast.Uniform(ProcBlast.DustCode, "dust_tear") > 0.1f,
            "soft alone is airbrush: the pack gets its raggedness from a painted "
            + "sprite and this gets it from a field eating the density");

        Check("and it is shaded in steps, which is the pack's own trick",
            ProcBlast.DustCode.Contains("floor(lit * dust_steps)")
            && ProcBlast.Uniform(ProcBlast.DustCode, "dust_steps") >= 2.0f,
            "banding a cloud's light is what makes it read as drawn rather than "
            + "airbrushed - three steps, off the CC0 pack's stylised smoke");

        // <b>The rings lie on their own plane, on the ground.</b> They cannot go on
        // the upright quad: below the contact point and behind the ground are one
        // test under this camera, so the half of a ring that spreads toward the
        // viewer would be sliced off flat. The pack answers this the same way, with
        // cylinder meshes lying flat.
        Check("the ground rings are not drawn on the upright quad",
            !ProcBlast.RingCode.Contains("blast_footing")
            && !ProcBlast.RingCode.Contains("blast_at("),
            "on that quad a ring loses the half of itself that comes toward the "
            + "camera, and what is left reads as an arc rather than a ring");
        Check("and a ring stays inside the plane it is drawn on",
            ProcBlast.Uniform(ProcBlast.RingCode, "ring_far")
                + ProcBlast.Uniform(ProcBlast.RingCode, "ring_wide") < 1.0f,
            $"reaches {ProcBlast.Uniform(ProcBlast.RingCode, "ring_far"):F2} of its "
            + "plane plus its own band, and past one the band is cut by the plane's "
            + "own edge - a straight line across a ring");

        // <b>The event is staggered, which is the other half of what the pack
        // does with an AnimationPlayer.</b> Every family has its own window, and
        // the ratio between the fire's and the dust's is the shape of the thing:
        // read at one window for all of them - which is what this was - a burst is
        // one puff rather than something that happened and then settled.
        float blastFire = Mathf.Max(
            ProcBlast.Uniform(ProcBlast.FireCode, "flash_life"),
            ProcBlast.Uniform(ProcBlast.FireCode, "ball_stagger")
                + ProcBlast.Uniform(ProcBlast.FireCode, "ball_life"));
        float blastRing = ProcBlast.Uniform(ProcBlast.RingCode, "ring_stagger")
                          + ProcBlast.Uniform(ProcBlast.RingCode, "ring_life");
        float blastEarth = ProcBlast.Uniform(ProcBlast.DustCode, "clod_stagger")
                           + ProcBlast.Uniform(ProcBlast.DustCode, "clod_life");
        float blastDust = ProcBlast.Uniform(ProcBlast.DustCode, "column_born")
                          + ProcBlast.Uniform(ProcBlast.DustCode, "column_stagger")
                          + ProcBlast.Uniform(ProcBlast.DustCode, "column_life");
        Check("the fire is a small fraction of the event and the dust is the rest",
            blastFire > 0.0f && blastDust > blastFire * 4.0f,
            $"fire out at {blastFire:F2}s, dust at {blastDust:F2}s - and a burst "
            + "whose families all start and end together is one puff");
        Check("and every one of them is over before the clock is",
            Mathf.Max(Mathf.Max(blastFire, blastRing),
                      Mathf.Max(blastEarth, blastDust)) <= burst.Life,
            $"the longest runs to {Mathf.Max(blastEarth, blastDust):F2}s against a "
            + $"clock of {burst.Life:F2}s, so it is cut off mid-air rather than "
            + "finishing");
        Check("the ring is gone before the earth has landed",
            blastRing < blastEarth,
            $"ring out at {blastRing:F2}s against earth down at {blastEarth:F2}s - "
            + "a shockwave still racing when the clods have settled is a shockwave "
            + "nobody reads as one");

        // <b>The panel is a view of the shaders' uniforms, so the two have to
        // agree about what exists.</b> Every dial names a uniform in a particular
        // shader and opens on that uniform's own default; a typo in either half is
        // a slider that silently writes a name nothing reads, or reads a default
        // of NaN and wipes the layer it governs. The second one has happened: six
        // of these are declared int and were being read as float, which came back
        // NaN, and every loop the panel governed ran zero times.
        var dialBench = new EffectsBench();
        var dialRows = EffectsBench.DialRows(dialBench).ToList();
        var dialLost = dialRows
            .Where(r => float.IsNaN((float)r.Opens))
            .Select(r => r.Name).ToList();
        Check("every dial on the effects panel names a uniform that exists",
            dialRows.Count > 0 && dialLost.Count == 0,
            dialLost.Count == 0
                ? $"{dialRows.Count} dials"
                : $"{dialLost.Count} name nothing: {string.Join(", ", dialLost.Take(6))}");
        var dialOut = dialRows
            .Where(r => r.Opens < r.Lo - 1e-6 || r.Opens > r.Hi + 1e-6)
            .Select(r => $"{r.Name} {r.Opens:F3} outside {r.Lo:F2}..{r.Hi:F2}")
            .ToList();
        Check("and opens inside the band its slider may move over",
            dialOut.Count == 0,
            dialOut.Count == 0 ? "" : string.Join("; ", dialOut.Take(4))
                + " - a slider whose default is outside its range snaps the "
                + "number the moment the panel is built, so the burst that opens "
                + "is not the burst the shader describes");
        var dialText = PanelText.Load(
            ProjectSettings.GlobalizePath("res://") + "effects.json");
        Check("and the panel file knows every one of them",
            dialText.Loaded
            && dialRows.All(r => dialText.RowIds.Contains(r.Id)),
            dialText.Loaded
                ? $"{dialRows.Count(r => !dialText.RowIds.Contains(r.Id))} rows are "
                  + "missing from effects.json, so they open with a bare name and "
                  + "no note"
                : $"effects.json did not load: {dialText.Error}");
        var puffRows = EffectsBench.PuffRows(dialBench).ToList();
        var scarRows = EffectsBench.ScarRows(dialBench).ToList();
        Check("and the file names nothing the panel does not build",
            dialText.Loaded
            && dialText.RowIds.All(id => dialRows.Any(r => r.Id == id)
                                         || puffRows.Any(r => r.Id == id)
                                         || scarRows.Any(r => r.Id == id)
                                         || id.StartsWith("he.crater.")
                                         || !id.Contains('.')
                                         || PanelOwn.Contains(id)),
            "a row in the file that no dial builds is a label nobody will ever "
            + "see, and the next person to look for it will move the dial instead");

        // <b>The imported burst's panel has two halves to disagree with, not
        // one.</b> Its numbers are half uniforms of one shader and half fields of
        // SheetBlast, so a row can miss in two ways: a uniform nothing declares
        // (NaN, which is the failure that once wiped a whole layer) or a model name
        // Model() has no case for (NaN as well, by construction - see the switch).
        // One check catches both, which is why Model returns NaN rather than
        // throwing.
        var puffLost = puffRows
            .Where(r => float.IsNaN((float)r.Opens))
            .Select(r => (r.Shader ? "uniform " : "model ") + r.Name).ToList();
        Check("every dial of the imported burst names a number that exists",
            puffRows.Count > 0 && puffLost.Count == 0,
            puffLost.Count == 0
                ? $"{puffRows.Count} dials, "
                  + $"{puffRows.Count(r => r.Shader)} of them uniforms"
                : $"{puffLost.Count} name nothing: {string.Join(", ", puffLost.Take(6))}");
        var puffOut = puffRows
            .Where(r => r.Opens < r.Lo - 1e-6 || r.Opens > r.Hi + 1e-6)
            .Select(r => $"{r.Name} {r.Opens:F3} outside {r.Lo:F2}..{r.Hi:F2}")
            .ToList();
        Check("and it opens inside the band its slider may move over",
            puffOut.Count == 0,
            puffOut.Count == 0 ? "" : string.Join("; ", puffOut.Take(4)));
        Check("and the panel file knows every one of those too",
            dialText.Loaded && puffRows.All(r => dialText.RowIds.Contains(r.Id)),
            dialText.Loaded
                ? $"{puffRows.Count(r => !dialText.RowIds.Contains(r.Id))} rows of the "
                  + "imported burst are missing from effects.json"
                : $"effects.json did not load: {dialText.Error}");
        // A model number with no dial is a number nobody can turn, which on this
        // bench is the same as a number that does not exist: the whole scene is a
        // panel and a picture. `life` is the exception and is named, because the
        // panel turns it with the event's own slider rather than from the table.
        string[] puffOwn = { "life" };
        var puffMissed = SheetBlast.ModelNames
            .Where(n => !puffRows.Any(r => !r.Shader && r.Name == n)
                        && !puffOwn.Contains(n))
            .ToList();
        Check("and every number the model answers to has a dial on it",
            puffMissed.Count == 0,
            $"no dial for {string.Join(", ", puffMissed)} - on a bench that is a "
            + "panel and a picture, a number with no slider is a number nobody "
            + "will ever move");
        dialBench.Free();

        // <b>The effects board's plinth, which is the whole of why its hexes look
        // like hexes.</b> Stage3D skips a prism's walls when the top face and the
        // floor are the same height, so a board on the datum draws as flat
        // hexagons with no sides - a 3D stage indistinguishable from the flat one.
        // A letter changed in the grid takes that away silently, because a flat
        // board is not an error.
        BoardMap effects = BoardMap.Effects;
        int pitFlat = 0, pitWet = 0, pitWood = 0, pitWalled;
        for (int r = 0; r < effects.Rows; r++)
        for (int q = 0; q < effects.Columns; q++)
        {
            var cell = new Vector2I(q, r);
            int at = effects.At(cell);
            if (effects.Water[at])
            {
                pitWet++;
                continue;
            }
            if (effects.Levels[at] <= 0)
                pitFlat++;
            if (effects.Kinds[at] == TerrainSet.Forest)
                pitWood++;
        }
        pitWalled = effects.Walled().Count;
        Check("the effects board stands a level up, so its hexes have sides",
            pitFlat == 0,
            $"{pitFlat} of its dry cells sit on the datum, and a cell whose top is "
            + "the floor is drawn as a flat hexagon with no side faces at all");
        // And the three things a burst is judged against are on it. Counted rather
        // than assumed: they are three letters in a five by five grid, and a
        // letter is the easiest thing in this project to lose.
        Check("and carries the wood, the pond and the ring of brick",
            pitWood == 1 && pitWet == 1 && pitWalled == 1,
            $"{pitWood} wooded, {pitWet} water, {pitWalled} walled - the burst is judged "
            + "partly on what hides it and what it hides, and those three are the "
            + "only things on this board that can answer");

        // The crater's own numbers, on the same two terms as the bursts': a name
        // the shader does not declare comes back NaN, and a default outside its
        // slider's band is snapped the moment the panel is built, so the crater
        // that opens is not the crater the shader describes.
        var scarLost = scarRows.Where(r => float.IsNaN((float)r.Opens))
                               .Select(r => r.Name).ToList();
        Check("every dial of the crater names a uniform that exists",
            scarRows.Count > 0 && scarLost.Count == 0,
            scarLost.Count == 0
                ? $"{scarRows.Count} dials"
                : $"{scarLost.Count} name nothing: {string.Join(", ", scarLost.Take(6))}");
        var scarOut = scarRows
            .Where(r => r.Opens < r.Lo - 1e-6 || r.Opens > r.Hi + 1e-6)
            .Select(r => $"{r.Name} {r.Opens:F3} outside {r.Lo:F2}..{r.Hi:F2}")
            .ToList();
        Check("and each opens inside its own band",
            scarOut.Count == 0,
            scarOut.Count == 0 ? "" : string.Join("; ", scarOut.Take(4)));
        Check("and the panel file knows the crater's rows as well",
            dialText.Loaded && scarRows.All(r => dialText.RowIds.Contains(r.Id)),
            dialText.Loaded
                ? $"{scarRows.Count(r => !dialText.RowIds.Contains(r.Id))} rows of the "
                  + "crater are missing from effects.json"
                : $"effects.json did not load: {dialText.Error}");
        // <b>The mark is drawn inside its own plane, which is the rule every quad
        // on this board is written under.</b> The plane is square and the crater is
        // round, so its corners are 1.41 of the radius: soil drawn out there would
        // be a mark whose shape is the plane's, and it would be a different shape
        // on every heading. Read off the shader text, because that is where the cut
        // is.
        Check("the crater draws nothing outside the plane it is drawn on",
            PitArt.Code.Contains("if (r > 1.0)", StringComparison.Ordinal)
            && PitArt.Code.Contains("ALPHA = 0.0;", StringComparison.Ordinal),
            "without the cut the corners of the plane show, and a crater with four "
            + "corners is a decal announcing its own quad");
        // And it lightens as well as darkens, which is the whole reason it stopped
        // being a mark in the ash map: that map has one direction.
        Check("and it is drawn over the ground rather than multiplied into it",
            PitArt.Code.Contains("blend_mix", StringComparison.Ordinal),
            "a crater added or multiplied can only go one way from the ground, and "
            + "a crater is a dark hole in the middle of a pale splash");

        // <b>The imported burst is an event on the same terms</b>, and the reason
        // to say it twice is that it keeps a second clock: the sheet's. A cloud
        // resting on frame nought is a puff of smoke that never arrived, and one
        // resting on the last frame is a scorch mark that never leaves.
        var sheeted = new SheetBlast();
        Check("the imported burst is out until it is fired, and out when it is done",
            !sheeted.Alive
            && Blown(sheeted, 0.0).Alive
            && !Blown(sheeted, sheeted.Life + 0.05).Alive,
            "a flipbook that rests on a frame is the one artefact of a flipbook "
            + "that cannot be softened - it stops at whatever opacity that frame "
            + "has");
        // <b>Every puff finishes its sheet inside the event.</b> A puff born at
        // the last of the stagger and living for its whole share has to be done
        // before the clock is, or it vanishes mid-flipbook at full opacity. The
        // trails are the half that makes this a real constraint: they are born up
        // to 0.72 of the way through on purpose.
        float sheetLast = sheeted.LastFinish();
        Check("and every puff of it finishes its sheet before the clock does",
            sheetLast <= 1.0f + 1e-4f,
            $"the last puff runs to {sheetLast:F2} of the event, so it is cut off "
            + "mid-flipbook - which is the one way this effect can end in a hard "
            + "edge");
        // The flame is keyed to the event and not to a puff, and this is the check
        // that says so: at an age past flame_out there is no fire anywhere in the
        // cloud, however new the puff carrying it. Read off the shader's text,
        // because that is where the number lives.
        float sheetOut = ProcBlast.Uniform(SheetBlast.Code, "flame_out");
        Check("and its fire is over long before its smoke is",
            sheetOut > 0.0f && sheetOut < 0.5f * sheeted.Life,
            $"flame out at {sheetOut:F2}s against an event of {sheeted.Life:F2}s - "
            + "a fireball still lit while the cloud is dissolving reads as a puff "
            + "of smoke that is on fire, which is not what a shell does");
        // <b>A spike has to overshoot the mass or it is not a spike.</b> The whole
        // reason the lances exist is that the reference's silhouette is made of
        // tapered fingers standing clear of the head; one thrown at the mass's own
        // speed lands inside the outline and is twenty-six quads nobody can see.
        Check("and the spikes it throws are faster than the mass they leave",
            sheeted.LanceFast > 1.0f && sheeted.Lances > 0,
            $"{sheeted.Lances} spikes at {sheeted.LanceFast:F2}x - at or under one "
            + "they are drawn inside the cloud, which is the whole of what they are "
            + "for spent on nothing");
        // <b>The three acts, asserted on the model rather than on a picture.</b>
        // The complaint that produced them was about the middle one: after the
        // flash the smoke kept being emitted upward, because the climb was a
        // monotone curve reaching its maximum on a puff's last frame. So the rise
        // has to be spent early and the height has to stop being a maximum at the
        // end - neither of which any capture will say out loud, because a plume
        // that is still rising and a plume that has stopped look the same in one
        // frame.
        float sheetRose = sheeted.Climbing(0.30f, SheetBlast.Role.Cloud);
        float sheetTop = sheeted.Climbing(0.55f, SheetBlast.Role.Cloud);
        Check("the imported burst spends its rise in the first third of a puff",
            sheetRose >= 0.90f * sheetTop,
            $"{sheetRose / Mathf.Max(sheetTop, 1e-3f):P0} of the way up at a third of the "
            + "way through - a rise that is still going on at the end is smoke "
            + "being emitted, not earth that was thrown");
        Check("and then settles rather than climbing to its last frame",
            sheeted.Climbing(1.0f, SheetBlast.Role.Cloud) < sheetTop - 0.02f,
            "the highest the dust ever gets is its own last frame, so it vanishes "
            + "at the top of its climb - there is no third act at all");
        // And the coarse half does come back, which is the half of the pair that
        // makes the other half read: spikes falling past a cloud that is hanging
        // is what says one of them is earth and the other is dust.
        float lanceTop = sheeted.Climbing(0.5f, SheetBlast.Role.Lance);
        Check("and the earth it throws falls back while the dust hangs",
            sheeted.Climbing(1.0f, SheetBlast.Role.Lance) < 0.5f * lanceTop
            && sheeted.Climbing(1.0f, SheetBlast.Role.Cloud) > 0.5f * sheetTop,
            $"lance at {sheeted.Climbing(1.0f, SheetBlast.Role.Lance):F2} of its "
            + $"apex {lanceTop:F2}, dust at "
            + $"{sheeted.Climbing(1.0f, SheetBlast.Role.Cloud):F2} - if both do the "
            + "same thing there is one fraction, not two");
        // <b>A bigger burst grows about its seat, and that is the one way this can
        // go wrong silently.</b> The seat is the point the crater is dug at, so a
        // scale applied about anything else slides the burst off its own mark by an
        // amount that grows with the size - the coordinate-space failure this
        // project has already paid for once, where nothing errors and there is
        // simply no burst where the shell went in.
        var sized = new SheetBlast();
        sized.Sit(new Vector2(120.0f, 64.0f), 53.7f, 0.5f, 0.87f);
        Vector3 sizedAt = sized.Transform.Origin;
        sized.Might = 2.5f;
        Check("a burst grows about the very point it is seated on",
            sized.Transform.Origin.IsEqualApprox(sizedAt)
            && Mathf.IsEqualApprox(sized.Transform.Basis.Scale.X, 2.5f)
            && Mathf.IsEqualApprox(sized.Transform.Basis.Scale.Y, 2.5f),
            $"seated at {sizedAt} and at {sized.Transform.Origin} once sized "
            + $"{sized.Transform.Basis.Scale} - a burst that grows about its middle "
            + "walks off the crater it dug");
        var sizedToo = new ProcBlast();
        sizedToo.Sit(new Vector2(120.0f, 64.0f), 53.7f, 0.5f, 0.87f);
        Vector3 sizedTooAt = sizedToo.Transform.Origin;
        sizedToo.Might = 0.4f;
        Check("and the computed one does it the same way",
            sizedToo.Transform.Origin.IsEqualApprox(sizedTooAt)
            && Mathf.IsEqualApprox(sizedToo.Transform.Basis.Scale.X, 0.4f),
            "two bursts sized by two rules is two things to get wrong, and the "
            + "harness sizes both from one list of calibres");
        sized.Free();
        sizedToo.Free();
        sheeted.Free();

        // An event, not a loop - the whole of what separates this clock from the
        // fire's. Out means nothing drawn, rather than resting on a first frame.
        Check("the burst is out until it is fired, and out again when it is done",
            !burst.Alive
            && Fired(burst, 0.0).Alive
            && !Fired(burst, burst.Life + 0.05).Alive,
            "a burst that rests on a frame is a scorch mark that never leaves, "
            + "and one that never ends is a board that fills up with them");

        // The one that broke, and it broke silently: the quad is placed by Trunk
        // and the mark is rasterised by Ground, so the two have to be reading the
        // same point in the same space. Written to take the field's own space
        // instead, the quad got Origin added to a point that already carried it
        // while the mark got it added to neither - so the burst stood 220px off
        // the shot and the crater landed off the edge of the map. No error, no
        // failed check, and no burst where the shell went in.
        Vector3 blastStood = Stage3D.Trunk(new Vector2(120.0f, 64.0f), 53.7f, 0.0f,
                                           0.5f, 0.87f).Origin;
        Vector3 blastDug = Stage3D.Ground(new Vector2(120.0f, 64.0f), 53.7f,
                                          0.5f, 0.87f);
        Check("a burst stands on the very point its crater is dug at",
            blastStood.IsEqualApprox(blastDug + Stage3D.Clear(0.5f, 0.87f)),
            $"quad at {blastStood} against a mark at {blastDug} - the two are one event and "
            + "one point, and the gap between them is silent in every number the "
            + "board prints");

        var pits = new Craters();
        pits.Dig(new Vector2(10.0f, 10.0f), 0.0f);
        pits.Dig(new Vector2(12.0f, 11.0f), 0.0f);
        Check("two rounds on one cell leave two marks",
            pits.Pits.Count == 2,
            "the unit is the point a shell landed on, not the hexagon it landed "
            + "in - marked per cell a crater is a black hexagon, which is the "
            + "failure the ash map's own grain note is about");
        Check("and nonsense digs nothing",
            NoDig(new Vector2(1.0f, 1.0f), 0.0f, 0.0f, Craters.WideDefault)
            && NoDig(new Vector2(1.0f, 1.0f), 0.0f, Craters.InkDefault, 0.0f),
            "a mark with no ink or no width is a splat over the whole map that "
            + "changes nothing, run every frame for the rest of the match");

        for (int k = 0; k < Craters.Capacity + 3; k++)
            pits.Dig(new Vector2(k, 0.0f), 0.0f);
        Check("the board remembers the last few dozen and forgets the oldest",
            pits.Pits.Count == Craters.Capacity
            && pits.Pits[^1].Spot.X == Craters.Capacity + 2,
            $"{pits.Pits.Count} marks of {Craters.Capacity} - the map is rebuilt "
            + "whole every frame, so an unbounded list is every shot of the match "
            + "splatted again forever");
        pits.Fill();
        Check("and a reset fills them in",
            !pits.Any,
            "a board that comes back shelled is a board that was not reset");

        Theme("which burst the board raises");
        // Two implementations of one effect - docs/blast.md - and which of them a
        // landed round raises is a setting on the stage rather than a swap of the
        // call. These are the three things that can go wrong with that: the
        // default, the routing, and the two pools staying separate.
        if (stage is null)
        {
            Note("  no stage on this board - nothing to raise a burst on");
        }
        else
        {
            Check("the board raises the imported burst by default",
                stage.Blast == BlastSource.Sheet,
                $"the stage says {stage.Blast} - the flipbook is what ships, and "
                + "--burst built is how the computed one is asked for");
            // <b>Behavioural, because the routing is the claim.</b> A test that
            // read the field would be a test that the field is the field; what
            // has to be true is that Land puts the round in the pool the field
            // names. Fired on the live stage and put back out afterwards - at
            // startup there is nothing on the board to disturb, and a clock at -1
            // draws nothing.
            BlastSource was = stage.Blast;
            Vector2 nowhere = stage.Origin;
            stage.Blast = BlastSource.Sheet;
            stage.Land(nowhere, 0.0f);
            bool sheetLit = stage.Booming.Any(b => b.Alive)
                            && !stage.Bursting.Any(b => b.Alive);
            foreach (SheetBlast b in stage.Booming)
                b.Douse();
            stage.Blast = BlastSource.Built;
            stage.Land(nowhere, 0.0f);
            bool builtLit = stage.Bursting.Any(b => b.Alive)
                            && !stage.Booming.Any(b => b.Alive);
            foreach (ProcBlast b in stage.Bursting)
                b.Douse();
            stage.Blast = was;
            stage.Pits?.Fill();
            Check("and Land puts the round in the pool the setting names",
                sheetLit && builtLit,
                $"sheet asked for gave {(sheetLit ? "the sheet" : "the wrong pool")}, "
                + $"built gave {(builtLit ? "the computed one" : "the wrong pool")} "
                + "- a setting that does not route is a flag that reads as not "
                + "arriving");
            Check("and the two pools are separate, six each",
                stage.Bursting.Count == Stage3D.Bursts
                && stage.Booming.Count == Stage3D.Bursts
                && !ReferenceEquals(stage.Bursting, stage.Booming),
                $"{stage.Bursting.Count} computed and {stage.Booming.Count} "
                + "imported - one shared pool would make the choice a thing that "
                + "outlives the round that made it");
        }

        Theme("the dust a gun kicks off the ground");
        // ProcKick, and the four frames of a firing tank it was read off. Most of
        // these are the burst's own assertions asked of the second effect built
        // out of the same dust - which is the point of there being one dust.
        var kick = new ProcKick();
        Check("the kick is made of the burst's dust rather than a second dust",
            ProcKick.DustKickCode.Contains(ProcBlast.DustInk)
            && ProcKick.DustKickCode.Contains(Stage3D.FlameInk)
            && ProcKick.GlowCode.Contains(Stage3D.FlameInk),
            "the profile, the tearing, the packing and the light on a cloud of "
            + "earth are one text, or eleven tuned numbers exist twice");
        Check("and the burst reads that same text, not a copy of it",
            ProcBlast.DustCode.Contains(ProcBlast.DustInk)
            && ProcBlast.DustInk.Contains("void dust_part(")
            && ProcBlast.DustInk.Contains("float dust_mass(")
            && ProcBlast.DustInk.Contains("float dust_lit("),
            "the shared block has to be what both of them actually compile");
        Check("and neither of them redefines what the shared text defines",
            !ProcKick.DustKickCode.Replace(ProcBlast.DustInk, "")
                     .Contains("void dust_part(")
            && !ProcBlast.DustCode.Replace(ProcBlast.DustInk, "")
                         .Contains("float dust_mass("),
            "a second definition beside the shared one is the copy this was "
            + "extracted to prevent, wearing the extraction as a disguise");
        // The one that caught a real defect before any screenshot was taken - and
        // the one the burst has for the same reason, in the same words: the quad
        // holds what the model can produce, not what it happens to draw.
        (float kickOut, float kickUp) = ProcKick.Bounds(kick.Reach, Vector2.Zero);
        Check("the quad holds what the model can produce, across",
            kickOut <= kick.Flank,
            $"the model reaches {kickOut:F3} of a tile out against a quad "
            + $"{kick.Flank:F3} wide - anything past the rim is sliced off flat, "
            + "and a cloud missing its outermost lumps reads as a smaller cloud");
        Check("and up",
            kickUp <= kick.Tall,
            $"the model climbs {kickUp:F3} of a tile against a quad {kick.Tall:F3} "
            + "tall - measured at 1.06 by 0.58 the settled dust had a straight "
            + "top edge and a straight right one");
        // And with the muzzle where a real gun puts it, because that is where
        // every element starts - see ProcKick.Aim on the shot that came out as an
        // orange mass lying on the engine deck.
        //
        // <b>Off the tanks that are loaded, not off a plausible number.</b> How
        // far a muzzle stands from the contact point is a fact about three
        // rendered guns and every heading of each, and the worst of them is what
        // the quad has to hold - the heavy's stubby howitzer and the light's long
        // tube are nothing like each other. Taken over all of them, so a fourth
        // tank with a longer gun fails this rather than clipping quietly.
        float kickSnout = 0.0f;
        Vector2 kickSnoutAt = Vector2.Zero;
        foreach (Vehicle v in vehicles)
        {
            float tile = Mathf.Max(v.Atlas.HexRect.Size.X, 1);
            for (int step = 0; step < 24; step++)
            {
                double heading = step * 360.0 / 24.0;
                Vector2 kickTube = v.Atlas.Muzzle(v.Atlas.FrameFor(heading))
                               - v.Atlas.Anchor;
                Vector2 off = (kickTube - v.Atlas.GroundOffset) * v.Sprite.BodyScale
                              / tile;
                var quad = new Vector2(off.X, -off.Y);
                if (quad.Length() > kickSnout)
                {
                    kickSnout = quad.Length();
                    kickSnoutAt = quad;
                }
            }
        }
        (float kickFar, float kickHigh) = ProcKick.Bounds(kick.Reach, kickSnoutAt);
        Check("and it holds them with the snout added, which is where they start",
            kickFar <= kick.Flank && kickHigh <= kick.Tall,
            $"{kickFar:F3} out and {kickHigh:F3} up from the furthest muzzle any "
            + $"loaded tank has ({kickSnoutAt.X:F3}, {kickSnoutAt.Y:F3} of a tile off its "
            + $"contact point), against a quad {kick.Flank:F3} by {kick.Tall:F3}");
        // The event, against the two clocks it is not.
        Check("the cloud outlives the flash by a multiple, not a margin",
            kick.Life > 2.0f * EffectLayer.Hold.Sum() / 60.0f,
            $"{kick.Life:F2}s against the shot sheet's "
            + $"{EffectLayer.Hold.Sum() / 60.0f:F2}s - the reference's last frame "
            + "is settled brown dust, which no 34-frame table can reach");
        Check("and the light is a tenth of it, not half",
            ProcBlast.Uniform(ProcKick.GlowCode, "core_life") < 0.15f * kick.Life,
            "the same elements held four times as long read as something burning "
            + "on the deck rather than as a gun going off");
        // Aim: both halves of the ground direction are used, and the vertical one
        // is clamped - see the class note on why there is no drawing below the
        // contact line.
        kick.Aim(new Vector2(0.30f, 0.80f), Vector2.Zero);
        Check("a shot toward the camera keeps its dust above the ground line",
            kick.Blow.Y >= 0.0f,
            $"blow {kick.Blow} - below the contact point and behind the ground "
            + "are one test under this camera, so dust aimed down is dust sliced "
            + "off flat");
        kick.Aim(new Vector2(0.20f, -0.10f), Vector2.Zero);
        float kickInto = kick.Squat;
        kick.Aim(new Vector2(1.00f, -0.05f), Vector2.Zero);
        Check("and a shot into the screen throws its dust across less of the picture",
            kick.Squat > kickInto,
            $"{kickInto:F2} into the screen against {kick.Squat:F2} across it - the "
            + "same number the camera shake takes off the same trigger");
        Check("and the dust starts at the muzzle rather than at the seat",
            ProcKick.DustKickCode.Contains("dot(at - snout, blow)")
            && ProcKick.DustKickCode.Contains("vec2 p = snout + "),
            "the seat is the contact point because that is where the ground is "
            + "known; a cloud placed from there begins inside the tank");
        Check("and nothing of it is drawn below the contact line",
            ProcKick.DustKickCode.Contains("blast_footing(at)")
            && ProcKick.GlowCode.Contains("blast_footing(at)"),
            "the quad's footing is the one thing standing between the cloud and "
            + "the ground it is seated on");

        Theme("the spall a plate throws when a round bounces off it");
        // ProcSpall. Most of this is the burst's and the kick's own assertions
        // asked of the third effect built out of the same dust - which is the
        // point of there being one dust - plus the two things that are this
        // effect's own: the mirror, and the fork that makes a ricochet a
        // different picture from a penetration.
        var spall = new ProcSpall();
        Check("the ricochet is made of the burst's dust rather than a third dust",
            ProcSpall.DustSpallCode.Contains(ProcBlast.DustInk)
            && ProcSpall.DustSpallCode.Contains(Stage3D.FlameInk)
            && ProcSpall.GlowCode.Contains(Stage3D.FlameInk),
            "the profile, the tearing, the packing and the light on a cloud of "
            + "dust are one text, or eleven tuned numbers exist three times");
        Check("and it does not redefine what the shared text defines",
            !ProcSpall.DustSpallCode.Replace(ProcBlast.DustInk, "")
                      .Contains("void dust_part(")
            && !ProcSpall.DustSpallCode.Replace(ProcBlast.DustInk, "")
                         .Contains("float dust_mass("),
            "a second definition beside the shared one is the copy the shared "
            + "block was extracted to prevent, wearing the extraction as a "
            + "disguise");
        // The quad against the model - the burst's rule, the kick's first defect,
        // and the reason both of them have this check in these words.
        (float spallOut, float spallUp) = ProcSpall.Bounds(spall.Reach,
                                                           Vector2.Zero);
        Check("the quad holds what the model can produce, across",
            spallOut <= spall.Flank,
            $"the model reaches {spallOut:F3} of a tile out against a quad "
            + $"{spall.Flank:F3} wide - anything past the rim is sliced off flat, "
            + "and a fan missing its fastest sparks reads as a smaller fan");
        Check("and up",
            spallUp <= spall.Tall,
            $"the model climbs {spallUp:F3} of a tile against a quad "
            + $"{spall.Tall:F3} tall");
        // <b>And with the impact where a real plate puts it, which on this effect
        // is the larger of the two terms going up.</b> A plate is on the side of a
        // hull, so the point of impact stands most of a tank's height above its
        // own contact point and the fan climbs from there - the kick's snout
        // finding, worse here because armour is higher up than a muzzle is far
        // out. Taken over every plate of every loaded tank at all 24 headings and
        // at the four corners of the scatter, so a fourth tank with a taller hull
        // fails this rather than clipping quietly.
        float spallReach = 0.0f;
        Vector2 spallAt = Vector2.Zero;
        if (vehicles is not null)
            foreach (Vehicle v in vehicles)
            {
                float tile = Mathf.Max(v.Atlas.HexRect.Size.X, 1);
                foreach (string face in v.Atlas.HitFaces)
                    for (int step = 0; step < 24; step++)
                    {
                        double heading = step * 360.0 / 24.0;
                        Vector2 middle = v.Atlas.HitOffset(face, heading);
                        Vector2 tangent = v.Atlas.HitTangent(face, heading);
                        Vector2 slope = v.Atlas.HitSlope(face, heading);
                        for (int corner = 0; corner < 4; corner++)
                        {
                            float along = (corner & 1) == 0
                                ? TankTick.ScatterLimit : -TankTick.ScatterLimit;
                            float rise = (corner & 2) == 0 ? 0.3f : -0.3f;
                            Vector2 hit = middle + tangent * along + slope * rise;
                            Vector2 off = (hit - v.Atlas.GroundOffset)
                                          * v.Sprite.BodyScale / tile;
                            var quad = new Vector2(off.X, -off.Y);
                            if (quad.Length() > spallReach)
                            {
                                spallReach = quad.Length();
                                spallAt = quad;
                            }
                        }
                    }
            }
        (float spallFar, float spallHigh) = ProcSpall.Bounds(spall.Reach, spallAt);
        Check("and it holds them with the impact added, which is where they start",
            spallFar <= spall.Flank && spallHigh <= spall.Tall,
            $"{spallFar:F3} out and {spallHigh:F3} up from the furthest impact any "
            + $"loaded tank can take ({spallAt.X:F3}, {spallAt.Y:F3} of a tile off "
            + $"its contact point), against a quad {spall.Flank:F3} by "
            + $"{spall.Tall:F3}");
        // The mirror, which is what makes this effect worth drawing at all - and
        // the one cell of it anybody can check by hand is the square one.
        if (vehicles is null || vehicles.Count == 0)
        {
            Note("  no tanks on this board - nothing to bounce a round off");
        }
        else
        {
            Vehicle mark = vehicles[0];
            string plate = mark.Atlas.HitFaces[0];
            double outward = mark.Sprite.HullFacing + mark.Atlas.HitBearing(plate);
            Vector2 square = mark.Graze(plate, 0.0f, 0.0f, outward).Away;
            Check("a round arriving square on goes straight back at the shooter",
                square.DistanceTo(mark.Atlas.GroundDirection(outward)) < 1e-4f,
                $"{square} against the plate's own outward "
                + $"{mark.Atlas.GroundDirection(outward)} - the mirror is "
                + "2*outward - from, and this is the cell of it anybody can check");
            Vector2 thrown = mark.Graze(plate, 0.0f, 0.0f, outward + 55.0).Away;
            Check("and a glancing one leaves as far the other side of the normal",
                thrown.DistanceTo(
                    mark.Atlas.GroundDirection(outward - 55.0)) < 1e-4f,
                $"arriving 55 degrees off the normal it left along {thrown} rather "
                + $"than {mark.Atlas.GroundDirection(outward - 55.0)} - a fan that "
                + "does not swing with the shot is a fan that says nothing");
            Check("and the impact point is the plate's own, not a second"
                  + " measurement of it",
                mark.Graze(plate, 0.2f, -0.1f, outward).Plate
                == mark.Plated(plate, 0.2f, -0.1f),
                "the rendered layer reads the same point every frame the dust is "
                + "settling, and two copies of four atlas calls agree until "
                + "somebody changes how a plate is measured");
            // <b>The fork, behaviourally, because the fork is the claim.</b> What
            // has to be true is that a round which bounces draws this and not the
            // rendered pair, and that a round which gets through draws the
            // rendered pair as it always did. Fired on a live tank and repaired
            // afterwards, which is what the R key does anyway.
            // <b>Armour-piercing, every one of them, and that is not decoration
            // on an old check.</b> A bounce is what AP does when it fails to get
            // in; HE bursts on the face whether it got in or not and draws
            // ProcSlam instead - see TankTick.Land's fork, where the round's own
            // kind is asked before the plate's. Passed HE these four would assert
            // the ricochet against the one round that cannot produce one.
            var fork = new TankTick();
            int bounced = 0;
            fork.Bounced = (_, _, _, _) => bounced++;
            mark.Hit.Reset();
            fork.Land(mark, plate, 0.0f, 0.0f, 1.0f, 0, 1, outward, Shell.Kind.Ap);
            bool bounceOnly = bounced == 1 && !mark.Hit.Live;
            mark.Hit.Reset();
            fork.Land(mark, plate, 0.0f, 0.0f, 1.0f, 2, 1, outward, Shell.Kind.Ap);
            bool holeOnly = bounced == 1 && mark.Hit.Live;
            fork.Bounce = false;
            mark.Hit.Reset();
            fork.Land(mark, plate, 0.0f, 0.0f, 1.0f, 0, 1, outward, Shell.Kind.Ap);
            bool offRestores = bounced == 1 && mark.Hit.Live;
            mark.Hit.Reset();
            mark.Sprite.Repair();
            Check("a round that bounces draws the ricochet and not the rendered"
                  + " burst",
                bounceOnly,
                $"the hook fired {bounced} time(s) and the rendered hit was "
                + "started when it should have been left alone - two impacts "
                + "drawn for one shell is the double model, and the rendered pair "
                + "is a shell going in");
            Check("and a round that gets through draws the rendered burst as it"
                  + " always did",
                holeOnly,
                "the fork is Gunnery.Penetration's own zero - the threshold the "
                + "impact sound and the kill tally already switch on");
            Check("and --spall off gives the old picture back",
                offRestores,
                "the built ricochet stands on the board and the rendered pair in "
                + "the tank's canvas, so this flag is the only A/B this layer can "
                + "have");

            // <b>And which rounds those are is the class matchup, on the key as
            // well as in a firefight.</b> A fired shell has a class at both ends
            // so the table has always decided it; a keypress had only one, so the
            // depth came off the calibre's bite and a bounce could not be asked
            // for at all. TankTick.HitBy is the missing end. Checked against
            // Gunnery.Penetration rather than against a written-out table,
            // because the table is already asserted cell by cell where it lives -
            // what is claimed here is that the dial spends *that* answer.
            Check("the manual hit opens with nobody behind it",
                fork.HitBy == TankTick.NoShooter && fork.HitGun is null
                && fork.HitDepth(mark) is null,
                $"HitBy {fork.HitBy} - a default that moved would move every "
                + "capture of the U key, and the bite is what walks one plate "
                + "through all three of its levels");
            bool follows = true, bounces = true;
            for (int by = 0; by < MovementProfile.All.Length; by++)
            {
                fork.HitBy = by;
                follows &= fork.HitDepth(mark)
                           == Gunnery.Penetration(MovementProfile.All[by],
                                                  mark.Profile);
            }
            // The one that says it is a matchup and not a counter: a gun that
            // cannot get in never gets in, however many times it lands. The bite
            // path is the opposite by design, which is why both are kept.
            MovementProfile weak = MovementProfile.Light;
            fork.HitBy = Array.IndexOf(MovementProfile.All, weak);
            fork.Bounce = true;
            if (Gunnery.Penetration(weak, mark.Profile) == 0)
                for (int shot = 0; shot < 4; shot++)
                {
                    mark.Hit.Reset();
                    int was = bounced;
                    fork.TakeHit(mark, fork.HitFrom, 1.0f, null, 1,
                                 Shell.Kind.Ap);
                    bounces &= bounced == was + 1 && !mark.Hit.Live;
                }
            fork.HitBy = TankTick.NoShooter;
            mark.Hit.Reset();
            mark.Sprite.Repair();
            Check("and named a class it spends the matchup's own answer",
                follows,
                "the table is asserted cell by cell where it lives; what this "
                + "says is that the dial reads it rather than deciding again");
            Check("and a gun that cannot get in never gets in, however many"
                  + " times it lands",
                bounces,
                $"a {weak.Tag} gun into a {mark.Profile.Tag} hull is level "
                + $"{Gunnery.Penetration(weak, mark.Profile)}, and four rounds of "
                + "it have to be four ricochets - the bite walks a plate deeper "
                + "on purpose and this must not");
        }
        // The three windows, in order: the flash is out before the fan, the fan
        // before the dust. Held the same length they are a firework.
        float bloomOut = ProcBlast.Uniform(ProcSpall.GlowCode, "bloom_life");
        float fanOut = ProcBlast.Uniform(ProcSpall.GlowCode, "shard_life")
                       + ProcBlast.Uniform(ProcSpall.GlowCode, "shard_stagger");
        float coatOut = ProcBlast.Uniform(ProcSpall.DustSpallCode, "puff_life");
        Check("the flash is out before the fan and the fan before the dust",
            bloomOut < fanOut * 0.5f && fanOut < coatOut,
            $"flash {bloomOut:F3}s, fan {fanOut:F3}s, dust {coatOut:F3}s - one "
            + "thing arriving and its consequences, or three things of one length");
        Check("and the whole of it is over inside a second",
            spall.Life < 1.0f && spall.Life < ProcKick.LifeDefault,
            $"{spall.Life:F2}s against the muzzle dust's "
            + $"{ProcKick.LifeDefault:F2}s - an event whose dust hangs about is "
            + "an event that did something, and this one did not");
        // Aim: both halves of the reflected direction are used, and the vertical
        // one is clamped - the kick's pair of checks, and the same reason under
        // them.
        spall.Aim(new Vector2(0.30f, 0.80f), Vector2.Zero);
        Check("a ricochet toward the camera keeps its spall above the ground line",
            spall.Away.Y >= 0.0f,
            $"away {spall.Away} - below the contact point and behind the ground "
            + "are one test under this camera, so spall aimed down is spall "
            + "sliced off flat");
        spall.Aim(new Vector2(0.20f, -0.10f), Vector2.Zero);
        float spallInto = spall.Squat;
        spall.Aim(new Vector2(1.00f, -0.05f), Vector2.Zero);
        Check("and one into the screen throws its spall across less of the picture",
            spall.Squat > spallInto,
            $"{spallInto:F2} into the screen against {spall.Squat:F2} across it");
        Check("and everything starts at the plate rather than at the seat",
            ProcSpall.GlowCode.Contains("vec2 p = plate + ")
            && ProcSpall.DustSpallCode.Contains("vec2 p = plate + "),
            "the seat is the victim's contact point because that is where the "
            + "ground is known; a fan placed from there begins inside the tank - "
            + "the error ProcKick already paid for once");
        // <b>The one thing the compiler found and no check did.</b> Declared in
        // the glow alone, the dust half compiled to nothing - unknown identifier -
        // and a material whose shader failed draws a black rectangle the size of
        // its quad across the board. What both halves spend belongs in the frame
        // they share, and this is that claim rather than a spelling test: the
        // frame has to declare it and neither shader may declare it again.
        Check("what both halves spend is declared in the frame they share",
            ProcSpall.SpallFrame.Contains("uniform float squat_floor")
            && ProcSpall.DustSpallCode.Contains("squat_floor")
            && ProcSpall.GlowCode.Contains("squat_floor")
            && !ProcSpall.DustSpallCode.Replace(ProcSpall.SpallFrame, "")
                         .Contains("uniform float squat_floor")
            && !ProcSpall.GlowCode.Replace(ProcSpall.SpallFrame, "")
                         .Contains("uniform float squat_floor"),
            "a number one half declares and the other uses is a half that does "
            + "not compile, and a quad whose shader failed is a black rectangle "
            + "over the board");
        Check("and nothing of it is drawn below the contact line",
            ProcSpall.DustSpallCode.Contains("blast_footing(at)")
            && ProcSpall.GlowCode.Contains("blast_footing(at)"),
            "the quad's footing is the one thing standing between the fan and the "
            + "ground it is seated on");
        // The depth side, which cannot be read off a picture: spall off the far
        // plate of a hull has to be behind the hull it came from.
        spall.Sit(Vector2.Zero, 0.0f, 0.5f, 1.0f, behind: true);
        bool sat = spall.Behind;
        spall.Sit(Vector2.Zero, 0.0f, 0.5f, 1.0f);
        Check("a bounce off a plate turned away is put behind the tank",
            sat && !spall.Behind,
            "two quads standing on one contact point sort by nothing, so spall "
            + "thrown off the far side would be drawn over the armour that "
            + "stopped the round");
        if (stage is not null)
        {
            // Behavioural, for the burst theme's reason: what has to be true is
            // that Spall puts the fan in its own pool rather than in one of the
            // other three. Fired on the live stage and put back out afterwards.
            stage.Spall(stage.Origin, 0.0f, Vector2.Right, Vector2.Zero, false);
            bool ownPool = stage.Spalling.Any(x => x.Alive)
                           && !stage.Bursting.Any(x => x.Alive)
                           && !stage.Booming.Any(x => x.Alive)
                           && !stage.Kicking.Any(x => x.Alive)
                           && !stage.Slamming.Any(x => x.Alive);
            foreach (ProcSpall x in stage.Spalling)
                x.Douse();
            Check("and it goes in its own pool, six of them, beside the other"
                  + " three",
                ownPool && stage.Spalling.Count == Stage3D.Bursts
                && !ReferenceEquals(stage.Spalling, stage.Kicking),
                $"{stage.Spalling.Count} of {Stage3D.Bursts} - a shared pool is "
                + "one shell's spall cutting another shell's burst short");
            Check("and a ricochet digs nothing",
                stage.Pits is null or { Any: false },
                "a round that failed to get through a plate did not dig the "
                + "ground either - the two bursts dig because a shell arriving in "
                + "the earth is a hole in it");
        }

        // <b>"high-explosive" spelled out, and it is the filter word rather than a
        // flourish.</b> --selftest matches the heading as a case-insensitive
        // substring, so "HE" selects every theme with the word "the" in it - 1126
        // checks of 1402, which is the filter reporting that it did something
        // while doing nothing.
        Theme("the burst a high-explosive round leaves on the plate it stopped on");
        // <b>The frame that says the shell stopped, and the ricochet above is
        // what it is being told apart from.</b> Both are a thing happening at a
        // measured point on a hull and leaving along a measured direction, both
        // are two quads on the shared dust and the shared ink, and both are fired
        // by the same tick from the same landing. What separates them is one
        // subtraction - a mirror there, the plate's own normal here - and which
        // families are drawn. So most of what follows is a comparison rather than
        // a measurement: the numbers that have to differ, and the one element
        // whose absence is load-bearing.
        var slam = new ProcSlam();
        Check("the burst on armour is made of the burst's own dust, a third time",
            ProcSlam.DustSlamCode.Contains(ProcBlast.DustInk)
            && ProcSlam.DustSlamCode.Contains(Stage3D.FlameInk)
            && ProcSlam.FireCode.Contains(Stage3D.FlameInk),
            "four effects on this board raise dust and one text says what dust "
            + "is - or eleven tuned numbers exist four times");
        Check("and it does not redefine what the shared text defines",
            !ProcSlam.DustSlamCode.Replace(ProcBlast.DustInk, "")
                     .Contains("void dust_part(")
            && !ProcSlam.DustSlamCode.Replace(ProcBlast.DustInk, "")
                        .Contains("float dust_mass("),
            "a second definition beside the shared one is the copy the shared "
            + "block was extracted to prevent, wearing the extraction as a "
            + "disguise");
        // <b>And the impact frame is shared too, which is the reuse this effect
        // was written on.</b> Where the round hit, which way the surface faces,
        // how much of that survived the projection, the floor under the reach and
        // the cone that opens as the reach closes: five facts about a hit on a
        // hull under this camera, none of them about spall in particular.
        Check("and the impact frame is the ricochet's, not a second copy of it",
            ProcSlam.DustSlamCode.Contains(ProcSpall.SpallFrame)
            && ProcSlam.FireCode.Contains(ProcSpall.SpallFrame)
            && !ProcSlam.DustSlamCode.Replace(ProcSpall.SpallFrame, "")
                        .Contains("uniform vec2  away")
            && !ProcSlam.FireCode.Replace(ProcSpall.SpallFrame, "")
                        .Contains("uniform float squat_floor"),
            "away, squat, plate, squat_floor and cone_open are facts about a hit "
            + "on a hull seen from this camera, and the two effects that have one "
            + "should not have two accounts of it");
        // <b>The move that made that possible, asserted where it can be seen to
        // have happened.</b> cone_open was the ricochet's glow's own until this
        // effect wanted it in both halves; declared twice it would be two numbers
        // for one projection, and declared once in the wrong half it is the black
        // rectangle squat_floor already cost - see the check on that above.
        Check("and the cone the projection opens is declared once, in that frame",
            ProcSpall.SpallFrame.Contains("uniform float cone_open")
            && !ProcSpall.GlowCode.Replace(ProcSpall.SpallFrame, "")
                         .Contains("uniform float cone_open")
            && !ProcSlam.FireCode.Replace(ProcSpall.SpallFrame, "")
                        .Contains("uniform float cone_open")
            && !ProcSlam.DustSlamCode.Replace(ProcSpall.SpallFrame, "")
                        .Contains("uniform float cone_open"),
            "seen end-on a cone is a disc, and that is a fact about the camera "
            + "rather than about what is being thrown");
        Check("and all four halves spend the frame's two expressions rather than"
              + " writing them out",
            ProcSpall.SpallFrame.Contains("float spall_run(")
            && ProcSpall.SpallFrame.Contains("float spall_cone(")
            && ProcSpall.GlowCode.Contains("spall_run(reach)")
            && ProcSpall.DustSpallCode.Contains("spall_run(reach)")
            && ProcSlam.FireCode.Contains("spall_run(reach)")
            && ProcSlam.DustSlamCode.Contains("spall_run(reach)")
            && ProcSlam.FireCode.Contains("spall_cone(")
            && ProcSlam.DustSlamCode.Contains("spall_cone("),
            "the reach with its floor and the cone with its opening were written "
            + "out four times between these two effects before they were two "
            + "functions in the text both of them include");
        // The quad against the model - the burst's rule, and the third effect to
        // have this check in these words.
        (float slamOut, float slamUp) = ProcSlam.Bounds(slam.Reach, Vector2.Zero);
        Check("the quad holds what the model can produce, across",
            slamOut <= slam.Flank,
            $"the model reaches {slamOut:F3} of a tile out against a quad "
            + $"{slam.Flank:F3} wide - anything past the rim is sliced off flat, "
            + "and a mass missing its edge reads as a smaller mass");
        Check("and up",
            slamUp <= slam.Tall,
            $"the model climbs {slamUp:F3} of a tile against a quad "
            + $"{slam.Tall:F3} tall");
        // With the impact where a real plate puts it - the ricochet's finding and
        // the same measurement, reused rather than taken again: where the furthest
        // impact any loaded tank can take is is a fact about the tanks, and this
        // effect starts at the same point that one does.
        (float slamFar, float slamHigh) = ProcSlam.Bounds(slam.Reach, spallAt);
        Check("and it holds them with the impact added, which is where they start",
            slamFar <= slam.Flank && slamHigh <= slam.Tall,
            $"{slamFar:F3} out and {slamHigh:F3} up from the furthest impact any "
            + $"loaded tank can take ({spallAt.X:F3}, {spallAt.Y:F3} of a tile off "
            + $"its contact point), against a quad {slam.Flank:F3} by "
            + $"{slam.Tall:F3}");
        // <b>Shorter and wider than the ricochet, which is the silhouette and is
        // therefore worth asserting rather than tuning and forgetting.</b> A
        // volume of gas that air stops at once against fragments leaving at a
        // kilometre a second: the two read apart on shape before they read apart
        // on colour, and a burst that grew to the fan's reach would stop doing so.
        float sootCone = ProcBlast.Uniform(ProcSlam.DustSlamCode, "cloud_cone");
        float fanCone = ProcBlast.Uniform(ProcSpall.GlowCode, "shard_cone");
        Check("the burst goes less far than the ricochet and spreads wider",
            slam.Reach < ProcSpall.ReachDefault && sootCone > fanCone,
            $"reach {slam.Reach:F2} against the fan's {ProcSpall.ReachDefault:F2}, "
            + $"cone {sootCone:F2} against {fanCone:F2}");
        Check("and it lasts longer than the ricochet and nothing like the shell in"
              + " the ground",
            slam.Life > ProcSpall.LifeDefault
            && slam.Life < ProcBlast.LifeDefault,
            $"{slam.Life:F2}s between the bounce's {ProcSpall.LifeDefault:F2} and "
            + $"the burst's {ProcBlast.LifeDefault:F2} - there is no ground under "
            + "this one to feed a column and nothing ballistic to wait for");
        // <b>The absence, and it is as much of the difference as the fireball
        // is.</b> The ricochet's two bolts are the round itself carrying on, and
        // they are most of what makes it read as a bounce. An HE round does not
        // carry on, so a bolt family appearing here would be the one element that
        // says the wrong thing about the event.
        // Asked of the declaration rather than of the word, which is the
        // substring trap sprite_atlas.by_hints paid for once: this shader's own
        // comment says there is no bolt here, and a search for the word finds
        // that sentence.
        Check("nothing here is the round carrying on",
            !ProcSlam.FireCode.Contains("uniform int bolts")
            && ProcSpall.GlowCode.Contains("uniform int bolts"),
            "the deflected shell is the ricochet's, and it is the element an HE "
            + "hit must not have - the fan is fragments that fall");
        float sprayDrop = ProcBlast.Uniform(ProcSlam.FireCode, "spray_drop");
        float shardDrop = ProcBlast.Uniform(ProcSpall.GlowCode, "shard_drop");
        Check("and its fragments fall harder than spall does",
            sprayDrop > shardDrop,
            $"{sprayDrop:F2} against the fan's {shardDrop:F2} - a piece of a shell "
            + "that has already gone off has nowhere to be going");
        // The three windows, in order: the core out before the ball, the ball
        // before the fragments have fallen, and the soot standing after all three.
        float coreOut = ProcBlast.Uniform(ProcSlam.FireCode, "core_life");
        float ballOut = ProcBlast.Uniform(ProcSlam.FireCode, "ball_life")
                        + ProcBlast.Uniform(ProcSlam.FireCode, "ball_stagger");
        float sprayOut = ProcBlast.Uniform(ProcSlam.FireCode, "spray_life")
                         + ProcBlast.Uniform(ProcSlam.FireCode, "spray_stagger");
        float sootOut = ProcBlast.Uniform(ProcSlam.DustSlamCode, "cloud_life");
        Check("the core is out before the ball, and the soot outlasts them all",
            coreOut < ballOut * 0.5f && ballOut < sprayOut && sprayOut < sootOut,
            $"core {coreOut:F3}s, ball {ballOut:F3}s, fragments {sprayOut:F3}s, "
            + $"soot {sootOut:F3}s - a shell arriving and its consequences, or "
            + "four things of one length");
        // <b>The direction, which is the whole of the model.</b> A plate's own
        // outward normal, and it does not depend on where the round came from -
        // the two claims are one check, because the way to say "the arrival
        // bearing dropped out" is that a glancing shot and a square one burst the
        // same way while the ricochet's leave differently.
        if (vehicles is null || vehicles.Count == 0)
        {
            Note("  no tanks on this board - nothing to burst a round against");
        }
        else
        {
            Vehicle mark = vehicles[0];
            string plate = mark.Atlas.HitFaces[0];
            double outward = mark.Sprite.HullFacing + mark.Atlas.HitBearing(plate);
            Vector2 normal = mark.Atlas.GroundDirection(outward);
            Check("an HE round bursts out along the plate's own normal",
                mark.Blown(plate, 0.0f, 0.0f).Out.DistanceTo(normal) < 1e-4f,
                $"{mark.Blown(plate, 0.0f, 0.0f).Out} against the plate's own "
                + $"outward {normal} - the gases go where the metal is not, which "
                + "is one direction and not two bearings");
            Check("and it bursts the same way however the round arrived",
                mark.Graze(plate, 0.0f, 0.0f, outward).Away
                       .DistanceTo(normal) < 1e-4f
                && mark.Graze(plate, 0.0f, 0.0f, outward + 55.0).Away
                       .DistanceTo(normal) > 1e-3f,
                "a shell that stopped has no memory of its own bearing, which is "
                + "exactly what a ricochet is made of - so the square-on bounce "
                + "coincides with this and the glancing one does not");
            Check("and the impact point is the plate's own, not a second"
                  + " measurement of it",
                mark.Blown(plate, 0.2f, -0.1f).Plate
                == mark.Plated(plate, 0.2f, -0.1f),
                "the same one definition the ricochet and the rendered layer both "
                + "read - four atlas calls written a third time agree until "
                + "somebody changes how a plate is measured");
            // <b>The fork, behaviourally, because the fork is the claim - and the
            // claim is that the round's kind is asked before the plate's.</b> An
            // HE round bursts whether or not it got in, an AP one bounces only
            // when it did not, and exactly one picture is drawn in every case.
            var fork = new TankTick();
            int went = 0, off = 0;
            fork.Blasted = (_, _, _, _) => went++;
            fork.Bounced = (_, _, _, _) => off++;
            mark.Hit.Reset();
            fork.Land(mark, plate, 0.0f, 0.0f, 1.0f, 0, 1, outward, Shell.Kind.He);
            bool heldStill = went == 1 && off == 0 && !mark.Hit.Live;
            mark.Hit.Reset();
            fork.Land(mark, plate, 0.0f, 0.0f, 1.0f, 2, 1, outward, Shell.Kind.He);
            bool throughStill = went == 2 && off == 0 && !mark.Hit.Live;
            mark.Hit.Reset();
            fork.Land(mark, plate, 0.0f, 0.0f, 1.0f, 0, 1, outward, Shell.Kind.Ap);
            bool apBounces = went == 2 && off == 1 && !mark.Hit.Live;
            // <b>And what the flag restores is the <em>path</em>, not one
            // picture on it - which is the one place this A/B is not Bounce's
            // word for word, and it took a failed check to say so.</b> Before
            // this effect existed the ammunition meant nothing against armour,
            // so an HE round went down the damage fork like any other: a bounce
            // when it did not get in, the rendered pair when it did. Off has to
            // hand back both of those, and an assertion that it hands back the
            // rendered pair unconditionally is an assertion that the old picture
            // was something it never was.
            fork.Slam = false;
            mark.Hit.Reset();
            fork.Land(mark, plate, 0.0f, 0.0f, 1.0f, 0, 1, outward, Shell.Kind.He);
            bool offBounces = went == 2 && off == 2 && !mark.Hit.Live;
            mark.Hit.Reset();
            fork.Land(mark, plate, 0.0f, 0.0f, 1.0f, 2, 1, outward, Shell.Kind.He);
            bool offRestoresHe = offBounces && went == 2 && mark.Hit.Live;
            fork.Slam = true;
            // And the mark, which is written above the fork and so is reached by
            // all three - the sentence docs/blast.md asks for ("the scar already
            // exists and stays") turning out to need no code at all.
            mark.Sprite.Repair();
            mark.Hit.Reset();
            fork.Land(mark, plate, 0.0f, 0.0f, 1.0f, 1, 1, outward, Shell.Kind.He);
            int heMarks = mark.Sprite.MarksOn(plate).Count;
            mark.Sprite.Repair();
            mark.Hit.Reset();
            fork.Land(mark, plate, 0.0f, 0.0f, 1.0f, 0, 1, outward, Shell.Kind.Ap);
            int apMarks = mark.Sprite.MarksOn(plate).Count;
            mark.Hit.Reset();
            mark.Sprite.Repair();
            Check("an HE round that did not get in bursts on the face of the plate",
                heldStill,
                $"the burst hook fired {went} time(s) and the bounce hook {off} - "
                + "a shell that explodes does not ricochet, and it is the round's "
                + "own kind that says so rather than the damage");
            Check("and one that did get in bursts in exactly the same way",
                throughStill,
                "this is what makes the ammunition the first question rather than "
                + "a third case: HE stops on the face either way, so the depth "
                + "cannot be what chooses the picture");
            Check("while an armour-piercing round still bounces when it fails",
                apBounces,
                "the two hooks answer two events, and the one the plate decides is "
                + "still AP's");
            Check("and --slam off puts an HE round back on the path it was on"
                  + " before this layer existed",
                offRestoresHe,
                "the built burst stands on the board and the rendered pair in the "
                + "tank's canvas, so off is the only A/B this layer can have - but "
                + "what it hands back is the damage fork, which is a bounce and a "
                + "penetration rather than one picture");
            Check("and the plate keeps its mark whichever picture was drawn",
                heMarks == 1 && apMarks == 1,
                $"{heMarks} mark after a burst and {apMarks} after a bounce - the "
                + "scar comes out of DamageTo above the fork, so what the armour "
                + "keeps does not depend on what was drawn over it");
        }
        // Aim: both halves of the normal are used and the vertical one is clamped
        // - the ricochet's pair of checks, and the same reason under them. The
        // clamp matters more here and costs less: a burst on the plate facing the
        // shooter is the common case, and a round mass that climbs is still a
        // round mass.
        slam.Aim(new Vector2(0.30f, 0.80f), Vector2.Zero);
        Check("a burst toward the camera keeps its mass above the ground line",
            slam.Away.Y >= 0.0f,
            $"away {slam.Away} - below the contact point and behind the ground are "
            + "one test under this camera");
        slam.Aim(new Vector2(0.20f, -0.10f), Vector2.Zero);
        float slamInto = slam.Squat;
        slam.Aim(new Vector2(1.00f, -0.05f), Vector2.Zero);
        Check("and one into the screen spreads across less of the picture",
            slam.Squat > slamInto,
            $"{slamInto:F2} into the screen against {slam.Squat:F2} across it");
        Check("and everything starts at the plate rather than at the seat",
            ProcSlam.FireCode.Contains("plate + away * ")
            && ProcSlam.DustSlamCode.Contains("vec2 p = plate + "),
            "the seat is the victim's contact point because that is where the "
            + "ground is known; a mass placed from there begins inside the tank - "
            + "the error ProcKick paid for once and ProcSpall inherited");
        Check("and one family of it deliberately reaches the ground",
            ProcSlam.DustSlamCode.Contains("uniform float spill_seat")
            && ProcSlam.DustSlamCode.Contains("(plate.y - spill_seat)"),
            "everything else here happens up on a plate and could be happening a "
            + "cell away for all the picture says; the spill is what puts the "
            + "burst on this tank, and the ground is the one height known exactly");
        Check("and nothing of it is drawn below the contact line",
            ProcSlam.DustSlamCode.Contains("blast_footing(at)")
            && ProcSlam.FireCode.Contains("blast_footing(at)"),
            "the quad's footing is the one thing standing between the mass and the "
            + "ground it is seated on");
        // <b>Darker than the ricochet's pair, and the dark stop is what the
        // brightness of the whole cloud is.</b> dust_seat minus dust_dense puts a
        // dense element near 0.24, so almost every pixel shows the dark stop -
        // the burst's measured finding, and it means this comparison is the
        // comparison of the two clouds rather than of two swatches.
        Check("the soot of a burst is darker than the coat a bounce knocks loose",
            ProcBlast.Colour(ProcSlam.DustSlamCode, "soot_dark").R
            < ProcBlast.Colour(ProcSpall.DustSpallCode, "coat_dark").R,
            "what comes off a plate under a bounce is paint and road dirt; what a "
            + "shell leaves is the soot of a filling that has just burnt");
        // The depth side, which cannot be read off a picture - the ricochet's
        // check, and the same trap: two quads standing on one contact point sort
        // by nothing.
        slam.Sit(Vector2.Zero, 0.0f, 0.5f, 1.0f, behind: true);
        bool slamSat = slam.Behind;
        slam.Sit(Vector2.Zero, 0.0f, 0.5f, 1.0f);
        Check("a burst on a plate turned away is put behind the tank",
            slamSat && !slam.Behind,
            "a fireball drawn over the armour it went off against is a fireball on "
            + "the wrong side of the hull");
        if (stage is not null)
        {
            // Behavioural, for the burst theme's reason - and here the pool it
            // must not share is the ricochet's, the two being the same trigger at
            // the same point on the same tank.
            stage.Slam(stage.Origin, 0.0f, Vector2.Right, Vector2.Zero, false);
            bool slamPool = stage.Slamming.Any(x => x.Alive)
                            && !stage.Spalling.Any(x => x.Alive)
                            && !stage.Bursting.Any(x => x.Alive)
                            && !stage.Booming.Any(x => x.Alive)
                            && !stage.Kicking.Any(x => x.Alive);
            foreach (ProcSlam x in stage.Slamming)
                x.Douse();
            Check("and it goes in its own pool, six of them, beside the other four",
                slamPool && stage.Slamming.Count == Stage3D.Bursts
                && !ReferenceEquals(stage.Slamming, stage.Spalling),
                $"{stage.Slamming.Count} of {Stage3D.Bursts} - a bounce and a "
                + "burst can land on one hull inside a second of each other, and "
                + "a shared pool is the second restarting the first's clock");
            Check("and a burst on armour digs nothing",
                stage.Pits is null or { Any: false },
                "a shell that went off against a hull did not touch the ground it "
                + "is standing over - the crater under a tank is what ammo_rack "
                + "will be, and that is a different event");

            // <b>And the same pool draws masonry, which is where the two surfaces
            // are told apart behaviourally rather than by reading the text.</b>
            // What has to be true is that naming a surface reaches the materials
            // at all - a property that silently failed to write would leave a
            // brick burst drawing steel, and no number anywhere would say so.
            stage.Slam(stage.Origin, 0.0f, Vector2.Right, Vector2.Zero, false,
                       1.0f, ProcSlam.Surface.Pierced);
            bool named = stage.Slamming.Any(x => x.Alive
                                                 && x.Face == ProcSlam.Surface.Pierced);
            foreach (ProcSlam x in stage.Slamming)
                x.Douse();
            Check("and the surface a round burst against reaches the burst",
                named,
                "armour, brick, or a round that went through brick - three "
                + "pictures out of one text, and the only thing C# owns of the "
                + "three sets is which one this is");
        }
        // <b>Three surfaces, one text, and this is the claim that keeps it one
        // text.</b> Every masonry term is a mix against a uniform that opens at
        // nought, so an armour burst is the arithmetic it was before brick
        // existed - which matters because the armour numbers are the ones that
        // were judged by eye, and a second surface must not have quietly
        // retuned them.
        Check("brick and steel are one shader with the switch at nought",
            ProcBlast.Uniform(ProcSlam.DustSlamCode, "masonry") == 0.0f
            && ProcBlast.Uniform(ProcSlam.FireCode, "masonry") == 0.0f
            && ProcBlast.Uniform(ProcSlam.DustSlamCode, "pierce") == 0.0f
            && ProcBlast.Uniform(ProcSlam.FireCode, "pierce") == 0.0f
            && ProcSlam.DustSlamCode.Contains("mix(1.0, stone_dust, masonry)")
            && ProcSlam.DustSlamCode.Contains("along_face * masonry")
            && ProcSlam.FireCode.Contains("mix(1.0, stone_fire, masonry)"),
            "the armour numbers are the ones judged by eye, and a second surface "
            + "that shifted them by a fraction would be a retune wearing a "
            + "feature's clothes");
        // The colours, and the pair is the point rather than either swatch: the
        // dark stop is what a cloud's brightness is - see the soot check above -
        // so this is the comparison the picture actually makes, HE on steel beside
        // HE on brick.
        //
        // <b>Claimed of the two surfaces of this effect and not of all four dusts
        // on the board, which is where the first version of this check was simply
        // wrong.</b> It asserted lime paler than the ricochet's coat as well; lime
        // is 0.512 and the coat is 0.545, so they are a near tie and the assertion
        // failed. Nothing needed retuning - crushed mortar and steel primer with
        // road dirt in it are genuinely about as pale as each other, and there is
        // no frame anywhere in which those two stand side by side. What has to
        // hold is the pair a picture can put together.
        Check("and lime is much the paler of this effect's two dusts",
            ProcBlast.Colour(ProcSlam.DustSlamCode, "lime_dark").R
            > ProcBlast.Colour(ProcSlam.DustSlamCode, "soot_dark").R + 0.10f,
            $"lime {ProcBlast.Colour(ProcSlam.DustSlamCode, "lime_dark").R:F3} "
            + $"against soot {ProcBlast.Colour(ProcSlam.DustSlamCode, "soot_dark").R:F3}"
            + " - what comes off a wall is the wall, and what comes off a plate is "
            + "the soot of a filling; the two are told apart by colour before "
            + "anything else");
        // <b>The two things about masonry that are not tuning.</b> The fragments
        // are at nought because something else draws them, and a pierced wall has
        // no fire because the round had no filling - a nought rather than a small
        // number, which is the difference between "less of it" and "none".
        Check("a burst on brick draws no fragments, because the rig throws real"
              + " ones",
            ProcBlast.Uniform(ProcSlam.FireCode, "stone_spray") == 0.0f
            && ProcSlam.FireCode.Contains("mix(1.0, stone_spray, masonry)"),
            "WallRig.Burst pushes brick bodies out of the courses; a drawn shard "
            + "beside a simulated one is two accounts of one debris, and the "
            + "simulated one is what knocks the wall down");
        Check("and a round that went through has no fire at all",
            ProcSlam.FireCode.Contains("* (1.0 - pierce)")
            && ProcBlast.Uniform(ProcSlam.DustSlamCode, "bore_dust") < 1.0f,
            "an armour-piercing round has no filling, so the light a wall gives "
            + "it is none - drawn the one way for both, AP came out as a lime "
            + "fireball, which is the one thing it is not");
        // And the geometry the third surface must NOT change: along_face turns the
        // travel, it does not lengthen it, so the quad measured for armour holds
        // for brick without Bounds having to know a surface exists.
        Check("and leaning the mass along a wall turns its travel rather than"
              + " lengthening it",
            ProcSlam.DustSlamCode.Contains("normalize(mix(away, side_dir * hand,"),
            "a mix of two unit directions renormalised is a direction, so the "
            + "quad Bounds measured for a hull is the quad a wall needs - which "
            + "is why Bounds takes a reach and a plate and no surface");

        Theme("the engine plume built rather than read");
        // The plume is ProcSmoke's second configuration, so almost everything
        // about the model is already asserted above and what is left is the pair:
        // that the two really are two, that each reaches the right state, and
        // that neither can quietly become the other.
        ProcSmoke stack = ProcSmoke.Column();
        ProcSmoke plume = ProcSmoke.Plume();
        Check("the plume and the column are one class in two configurations",
            plume.Layer == AtlasSet.ExhaustName
            && stack.Layer == AtlasSet.BurnName
            && plume.GetType() == stack.GetType(),
            "in Blender engine_fire.SMOKE is exhaust_plume with other numbers - "
            + "it imports that file's path outright - and two transcriptions here "
            + "would part at whichever number nobody put side by side");
        Check("and they differ in the numbers that make one a plume and the other"
              + " a column",
            Math.Abs(plume.Rise - stack.Rise) > 0.1f
            && Math.Abs(plume.Slowing - stack.Slowing) > 0.3f
            && Math.Abs(plume.TurnPower - stack.TurnPower) > 0.3f
            && plume.Ink != stack.Ink,
            $"rise {plume.Rise:F2}/{stack.Rise:F2}, slowing "
            + $"{plume.Slowing:F2}/{stack.Slowing:F2}, turn "
            + $"{plume.TurnPower:F2}/{stack.TurnPower:F2}");
        // Both are smoke, so both slow down; the plume slows harder, which is a
        // jet giving its momentum up at once against a fire drawing all the way.
        // Read at half a life it is the plume that is already nearly there.
        Vector3[] hazePath = Plumes.Path(Plumes.Up, 1.0f, plume.Buoyancy,
                                         plume.TurnPower, -1.0f / plume.Slowing);
        Vector3[] stackPath = Plumes.Path(Plumes.Up, 1.0f, stack.Buoyancy,
                                          stack.TurnPower, -1.0f / stack.Slowing);
        Check("the plume is most of the way up by half a life and the column is"
              + " not",
            Plumes.Along(hazePath, 0.5f).Length()
            > Plumes.Along(stackPath, 0.5f).Length() + 0.05f,
            $"plume {Plumes.Along(hazePath, 0.5f).Length():F3}, column "
            + $"{Plumes.Along(stackPath, 0.5f).Length():F3} of the way at half a"
            + " life");
        Check("the plume's ink is the lighter of the two and still darkens the"
              + " board",
            plume.Ink.R > stack.Ink.R + 0.15f && plume.Ink.R < 0.72f,
            $"plume ink {plume.Ink.R:F3}, stack {stack.Ink.R:F3}, pale ground "
            + "0.72 - a haze the same value as what is behind it is not haze");

        if (atlas.HasPorts && atlas.HasExhaust)
        {
            AtlasSet.Port port = atlas.Ports[0];

            // Quoted against the hull and against the port, each where the config
            // quotes it: how far the plume gets is a fact about the tank, how
            // wide it leaves is a fact about the grille. Reading either as a
            // fraction of the other is the mistake the forest's port paid for.
            ProcSmoke.Puff[] near = plume.Build(port, atlas.HullLength, 0.0f);
            ProcSmoke.Puff[] far = plume.Build(port, atlas.HullLength * 2.0, 0.0f);
            float nearTip = 0.0f, farTip = 0.0f;
            foreach (ProcSmoke.Puff q in near)
                nearTip = Math.Max(nearTip, (q.Centre - port.Point).Length());
            foreach (ProcSmoke.Puff q in far)
                farTip = Math.Max(farTip, (q.Centre - port.Point).Length());
            Check("how far the plume reaches is quoted against the hull",
                farTip > nearTip * 1.6f,
                $"doubling the hull took the tip from {nearTip:F3} to {farTip:F3}");

            var wide = new AtlasSet.Port(port.Point, port.Dir, port.Radius * 2.0f);
            ProcSmoke.Puff[] fat = plume.Build(wide, atlas.HullLength, 0.0f);
            float shifted = 0.0f, widened = 0.0f;
            for (int i = 0; i < fat.Length; i++)
            {
                Vector3 nudged = fat[i].Centre - near[i].Centre;
                shifted = Math.Max(shifted, Math.Abs(nudged.Dot(port.Dir)));
                widened = Math.Max(widened, fat[i].Radius - near[i].Radius);
            }
            Check("and how wide it leaves is quoted against the port, which moves"
                  + " puffs across the vent and never along it",
                shifted < 1e-5f && widened > 0.0f,
                $"doubling the port radius moved a puff {shifted:E2} along the "
                + $"vent and grew one by {widened:F4}");

            ProcSmoke.Puff[] lapStart = plume.Build(port, atlas.HullLength, 0.0f);
            ProcSmoke.Puff[] lapEnd = plume.Build(port, atlas.HullLength, 1.0f);
            double worst = 0.0;
            for (int i = 0; i < lapStart.Length; i++)
                worst = Math.Max(worst,
                    (lapStart[i].Centre - lapEnd[i].Centre).Length()
                    + Math.Abs(lapStart[i].Alpha - lapEnd[i].Alpha));
            Check("a whole lap of the plume is the same picture",
                worst < 1e-4, $"worst puff moved {worst:E2}");

            // Solidity has to reach zero at both ends of a life or a puff appears
            // and vanishes with a step, which on a loop is a seam once a lap.
            ProcSmoke.Puff youngest = lapStart.OrderBy(q => q.Age).First();
            ProcSmoke.Puff eldest = lapStart.OrderBy(q => q.Age).Last();
            Check("and its puffs fade up out of nothing and back into it",
                youngest.Alpha < plume.Alpha * 0.6f
                && eldest.Alpha < plume.Alpha * 0.6f,
                $"youngest {youngest.Alpha:F3} at age {youngest.Age:F3}, eldest "
                + $"{eldest.Alpha:F3} at {eldest.Age:F3}, peak {plume.Alpha:F2}");
        }

        var plumePair = new TankSprite { Atlas = atlas, ExhaustPhase = 0 };
        var readPlume = TankSprite.MakeLayer(AtlasSet.ExhaustName);
        readPlume.Tank = plumePair;
        plumePair.ProceduralExhaust = false;
        bool plumeWhenOff = readPlume.Showing >= 0;
        plumePair.ProceduralExhaust = true;
        bool plumeWhenOn = readPlume.Showing >= 0;
        Check("exactly one of the built plume and the read one draws",
            plumeWhenOff && (!plumeWhenOn || !atlas.HasPorts),
            $"rendered layer shows {(plumeWhenOff ? "yes" : "no")} with the flag "
            + $"off, {(plumeWhenOn ? "yes" : "no")} with it on");
        // Three effects, three switches. A tank idles all game and burns once, so
        // this is the one of the three that is on screen almost always - and the
        // whole use of the switches is standing one built half beside read ones.
        Check("and the three switch apart",
            !new TankSprite { Atlas = atlas, ProceduralExhaust = true }
                .SmokeIsProcedural
            && !new TankSprite { Atlas = atlas, ProceduralExhaust = true }
                .FireIsProcedural
            && !new TankSprite { Atlas = atlas, ProceduralSmoke = true }
                .ExhaustIsProcedural,
            "one flag drives more than its own effect, so no half can be judged "
            + "on its own");
        Check("the plume is framed by the hull's heading, not the turret's",
            ProcSmoke.ColumnCode.Contains("origin") // the shader is heading-free
            && !ProcSmoke.ColumnCode.Contains("TurretFacing"),
            "the vent is welded to the engine deck, so a plume indexed by the gun "
            + "drags its smoke wherever the gun is pointing");

        Theme("a shell arriving");
        // An event, like the shot, and unlike everything else that came after
        // it: the phases run out into -1 rather than wrapping.
        var hitSeen = new HashSet<int>();
        bool hitEnds = false;
        for (int i = 0; i < HitLoop.Duration + 10; i++)
        {
            int phase = HitLoop.PhaseAt(i);
            if (phase >= 0)
                hitSeen.Add(phase);
            else if (i >= HitLoop.Duration)
                hitEnds = true;
        }
        Check("a hit runs through every rendered phase and then stops",
            hitSeen.Count == HitLoop.Hold.Length && hitEnds,
            $"{hitSeen.Count} of {HitLoop.Hold.Length} phases,"
            + $" {HitLoop.Duration} frames");
        // The early phases are where the burst is and the late ones are dust
        // settling. A flat tempo either stretches the flash into a bonfire or
        // cuts the dust off in the middle - the same reason the shot's table is
        // uneven, only more so, because the burst is gone by phase four.
        Check("the burst runs faster than the dust that outlives it",
            HitLoop.Hold[0] + HitLoop.Hold[1] < HitLoop.Hold[^1],
            $"first two {HitLoop.Hold[0] + HitLoop.Hold[1]} frames,"
            + $" last {HitLoop.Hold[^1]}");

        var strike = new HitLoop();
        strike.Strike("front", 0.3f, 0.2f, 1.4f);
        for (int i = 0; i < 12; i++)
            strike.Advance();
        int midway = strike.Phase;
        float midwayScale = strike.Scale;
        strike.Strike("rear", -0.2f, -0.1f, 0.7f);
        Check("a second shell restarts the hit rather than being ignored",
            midway > 0 && strike.Phase == 0 && strike.Face == "rear",
            $"was phase {midway}, now {strike.Phase} on {strike.Face}");
        // The calibre belongs to the round, not to the dial. It lived on the
        // tank as a live setting the layer read on every draw, so turning the
        // dial while the dust was in the air resized a shell that had already
        // landed - one round at two calibres. Everything settled when the
        // trigger goes travels with the hit: the plate, the scatter, the size.
        Check("a shell keeps the calibre it went off at for its whole life",
            midwayScale == 1.4f, $"went off at 1.40, was drawing at {midwayScale:F2}");
        Check("the next shell takes the new calibre and only it",
            strike.Scale == 0.7f && Math.Abs(strike.Scatter + 0.2f) < 1e-6
            && Math.Abs(strike.Rise + 0.1f) < 1e-6,
            $"scale {strike.Scale:F2}, scatter {strike.Scatter:F2}"
            + $", rise {strike.Rise:F2}");
        strike.Reset();
        Check("a repaired tank is back to the plain calibre",
            strike.Scale == 1.0f && strike.Face == "" && strike.Rise == 0.0f,
            $"scale {strike.Scale:F2}");
        // One list of calibres, whoever is asking - key, dropdown or flag. The
        // same rule as a bearing snapping to a side of the hex, and it exists
        // because the dropdown cannot show a value that is not in it: the flag
        // took any number, and a 1.4 round was loaded under a control reading
        // 1.0x.
        Check("any calibre asked for resolves to one the gun has",
            Ordnance.For(0.4f) == 0 && Ordnance.For(0.95f) == 1
            && Ordnance.For(1.4f) == 2 && Ordnance.For(9.0f) == 2,
            $"0.4 -> {Ordnance.For(0.4f)}, 0.95 -> {Ordnance.For(0.95f)},"
            + $" 1.4 -> {Ordnance.For(1.4f)}, 9.0 -> {Ordnance.For(9.0f)}");

        Check("the hit pair follows the hull, like the plume",
            EffectLayer.HitBurst(AtlasSet.BurstName).FollowsHull
            && EffectLayer.HitDust(AtlasSet.DustName).FollowsHull,
            "the plates are measured on the hull, not on the turret");
        // A shell landing is the only thing drawn away from the anchor - both
        // halves of the hit and the mark it leaves, all three placed by where
        // the round hit. Everything else on the tank is welded to it.
        Check("only a shell landing is drawn away from the anchor",
            EffectLayer.HitBurst(AtlasSet.BurstName).Placed
            && EffectLayer.HitDust(AtlasSet.DustName).Placed
            && AtlasSet.ScarNames.All(n => EffectLayer.Scar(n).Placed)
            && !EffectLayer.Exhaust(AtlasSet.ExhaustName).Placed
            && !EffectLayer.BurningFire(AtlasSet.FireName).Placed
            && !EffectLayer.Additive("flash").Placed,
            "every other layer draws at -anchor with no offset of any kind");
        Check("the hit does not go round like the loops do",
            !EffectLayer.HitBurst(AtlasSet.BurstName).Loops
            && !EffectLayer.HitDust(AtlasSet.DustName).Loops,
            "a shell lands once");
        Check("the hit pair is kept out of the shot's layer list",
            !AtlasSet.EffectNames.Contains(AtlasSet.BurstName)
            && !AtlasSet.EffectNames.Contains(AtlasSet.DustName),
            "HasEffects requires every name in it");
        Check("the dust is added to the tree before the burst",
            Array.IndexOf(TankSprite.LayerOrder, AtlasSet.DustName)
            < Array.IndexOf(TankSprite.LayerOrder, AtlasSet.BurstName),
            string.Join(" -> ", TankSprite.LayerOrder));

        if (!atlas.HasHit)
        {
            Check($"{atlas.Tag} has the hit layers and a plate table", false,
                "no burst/dust atlas or no `hits` block - re-render this scene");
        }
        else
        {
            Vector2 hitTankAnchor = atlas.Anchor / (Vector2)atlas.Tile;
            foreach (string layer in new[] { AtlasSet.BurstName, AtlasSet.DustName })
            {
                Vector2 anchor = atlas.AnchorOf(layer) / (Vector2)atlas.TileOf(layer);
                Check($"the {layer} layer shares the tank's anchor, in frame fractions",
                    (anchor - hitTankAnchor).Length() < 1e-4,
                    $"{anchor} against {hitTankAnchor}");
            }
            // The one layer with a count of its own, and the reason CountOf
            // exists: indexed against the hull's twelve it would run straight
            // off the end of a ten-frame atlas.
            Check("the hit layers were rendered without headings",
                atlas.CountOf(AtlasSet.BurstName) == 1
                && atlas.CountOf(AtlasSet.DustName) == 1
                && atlas.CountOf("hull") == atlas.Count,
                $"burst {atlas.CountOf(AtlasSet.BurstName)},"
                + $" hull {atlas.CountOf("hull")}");
            var hitTiles = new HashSet<int>();
            bool hitInRange = true;
            foreach (int facing in atlas.RenderedFacings())
                for (int phase = 0; phase < atlas.HitPhases; phase++)
                {
                    int frame = atlas.EffectFrame(AtlasSet.BurstName, phase, facing);
                    hitTiles.Add(frame);
                    if (frame < 0 || frame >= atlas.HitPhases)
                        hitInRange = false;
                }
            Check("every heading resolves to the same column of hit frames",
                hitInRange && hitTiles.Count == atlas.HitPhases,
                $"{hitTiles.Count} distinct of {atlas.HitPhases}");
            // It needs less frame than the tank, not more: the burst is small
            // and is placed rather than anchored, so its tile only has to hold
            // the burst. This is the assertion that catches it quietly growing.
            Check("the hit needs no more frame than the tank",
                atlas.TileOf(AtlasSet.BurstName).X <= atlas.Tile.X
                && atlas.TileOf(AtlasSet.DustName)
                   == atlas.TileOf(AtlasSet.BurstName),
                $"burst {atlas.TileOf(AtlasSet.BurstName).X}px,"
                + $" tank {atlas.Tile.X}px");

            // The plate a shell lands on is geometry, and it has to keep being
            // geometry as the hull turns. A bearing stamped as a world angle
            // would pass at heading zero and be wrong everywhere else - the
            // same failure shape as a transposed frame table.
            bool picksPlate = true, turnsWithHull = true;
            foreach (string face in atlas.HitFaces)
            foreach (int hull in new[] { 0, 90, 180, 270, 45 })
            {
                double outward = hull + atlas.HitBearing(face);
                if (atlas.FaceFor(outward, hull) != face)
                    picksPlate = false;
            }
            string head = atlas.FaceFor(270.0, 270.0);
            if (atlas.FaceFor(270.0, 90.0) == head)
                turnsWithHull = false;
            Check("a shell picks the plate whose armour faces the shooter",
                picksPlate, string.Join(", ", atlas.HitFaces));
            Check("turning the hull round changes which plate takes it",
                turnsWithHull,
                $"nose-on gives {head}, tail-on gives {atlas.FaceFor(270.0, 90.0)}");

            // A shell comes from a neighbouring cell, so it comes across a flat
            // side, so the six directions a tank can drive in are the six it
            // can be shot from. Not a knob: any bearing anything hands in has
            // to resolve to one of those.
            bool snapped = true;
            for (int deg = 0; deg < 360; deg += 7)
            {
                int side = Angles.SideFor(deg);
                int edge = HexField.EdgeHeadings[side];
                double gap = Math.Abs(Mathf.PosMod(edge - deg + 180.0, 360.0) - 180.0);
                if (side < 0 || side >= HexField.EdgeHeadings.Length || gap > 30.0)
                    snapped = false;
            }
            Check("a shot arrives across a flat side of the hex, whatever it was given",
                snapped && HexField.EdgeHeadings.Length == 6,
                string.Join(", ", HexField.EdgeHeadings));

            // Six bearings against four plates cannot put one shell on each, so
            // what is asserted is that a lap still *reaches* all four from every
            // heading - a walk that never turned a plate up would be a walk
            // that cannot be used to test one.
            int worstLap = int.MaxValue, worstAt = -1;
            foreach (int hull in atlas.RenderedFacings())
            {
                var lap = new HashSet<string>();
                foreach (int edge in HexField.EdgeHeadings)
                    lap.Add(atlas.FaceFor(edge, hull));
                if (lap.Count < worstLap)
                {
                    worstLap = lap.Count;
                    worstAt = hull;
                }
            }
            Check("a lap of the six sides reaches every plate, from every heading",
                worstLap == atlas.HitFaces.Count,
                $"worst is {worstLap} of {atlas.HitFaces.Count},"
                + $" at heading {worstAt}");

            // ...and here is the boundary the walk no longer crosses by
            // accident, asked directly: halfway between two plates the answer
            // is one of them, and two degrees either way gives the other.
            bool splits = true;
            foreach (int hull in atlas.RenderedFacings())
            foreach (string face in atlas.HitFaces)
            {
                double normal = hull + atlas.HitBearing(face);
                if (atlas.FaceFor(normal + 44.0, hull)
                    == atlas.FaceFor(normal + 46.0, hull))
                    splits = false;
            }
            Check("either side of the split between two plates picks each of them",
                splits, "the choice between neighbours is where the wrong one hides");

            // Calibre is a scale on the drawn rect, and it has to scale about
            // the impact rather than about the rect's own corner - the tile is
            // 192px against a plate 49px tall, so a corner-anchored scale walks
            // the hit clean off the armour.
            Vector2 hitTile = atlas.TileOf(AtlasSet.BurstName);
            Vector2 hitAnchor = atlas.AnchorOf(AtlasSet.BurstName);
            var impact = new Vector2(37.0f, -18.0f);
            bool scalesAboutImpact = true;
            foreach (float size in new[] { 0.5f, 1.0f, 2.0f })
            {
                Rect2 rect = EffectLayer.Frame(hitAnchor, hitTile, impact, size);
                if ((rect.Position + hitAnchor * size - impact).Length() > 1e-3f
                    || Math.Abs(rect.Size.X / hitTile.X - size) > 1e-4f)
                    scalesAboutImpact = false;
            }
            Check("calibre scales the hit about the point of impact",
                scalesAboutImpact,
                "scaling about the rect corner slides it off the plate");

            // Facing decides front-or-behind, and it must not be the same
            // answer everywhere: a plate that is never behind means the sign is
            // being thrown away, which is exactly how a hit on the far side
            // ends up sitting on the turret roof.
            int behind = 0, infront = 0;
            foreach (string face in atlas.HitFaces)
            foreach (int facing in atlas.RenderedFacings())
            {
                if (atlas.HitFacing(face, facing) <= 0.0)
                    behind++;
                else
                    infront++;
            }
            Check("every plate spends part of the turn behind the tank",
                behind > 0 && infront > 0
                && behind + infront == atlas.HitFaces.Count * atlas.Count,
                $"{behind} behind, {infront} in front");
            // The offset is what places the layer, so a table of zeroes would
            // put every hit on the turret ring and pass every other check here.
            double reach = 0.0;
            foreach (string face in atlas.HitFaces)
            foreach (int facing in atlas.RenderedFacings())
                reach = Math.Max(reach, atlas.HitOffset(face, facing).Length());
            Check("the plates sit away from the anchor, not on it",
                reach > 20.0, $"furthest {reach:F1}px");
        }

        Theme("the mark it leaves");
        // A state, not an event, and every check here is that distinction.
        Check("a scar layer knows its plate from its name",
            EffectLayer.FaceOf("scar_front") == "front"
            && EffectLayer.FaceOf(AtlasSet.BurstName) == ""
            && AtlasSet.ScarNames.All(n => EffectLayer.FaceOf(n) != ""),
            string.Join(", ", AtlasSet.ScarNames));
        Check("the scars follow the hull, like the plates they are on",
            AtlasSet.ScarNames.All(n => EffectLayer.Scar(n).FollowsHull),
            "the plates are measured on the hull, not on the turret");
        // Both halves of a shell landing are placed, and they are placed by
        // different things: the burst is one heading-less render dropped
        // wherever the round hit, the mark was rendered on its own plate at
        // every heading and only slides along it. They still have to agree,
        // which is what the shared scatter below is for.
        Check("the scars do not loop and are not in the shot's layer list",
            AtlasSet.ScarNames.All(n => !EffectLayer.Scar(n).Loops)
            && !AtlasSet.ScarNames.Any(AtlasSet.EffectNames.Contains),
            "nothing advances a level of damage");
        Check("the scars are composited under every other effect",
            AtlasSet.ScarNames.All(n =>
                Array.IndexOf(TankSprite.LayerOrder, n)
                < Array.IndexOf(TankSprite.LayerOrder, AtlasSet.ExhaustName)),
            string.Join(" -> ", TankSprite.LayerOrder));

        if (!atlas.HasScars)
        {
            Check($"{atlas.Tag} has a scar layer for every plate", false,
                "no scar_* atlas - re-render this scene");
        }
        else
        {
            tank.Repair();
            string plate = atlas.HitFaces[0];
            string other = atlas.HitFaces[1];
            bool clean = tank.ScarLevel(plate) < 0;
            int first = tank.Damage(plate, 0.1f, -0.1f);
            bool spared = tank.MarksOn(other).Count == 0;
            for (int i = 1; i < atlas.ScarLevels; i++)
                tank.Damage(plate, 0.1f + 0.15f * i, -0.1f + 0.1f * i);
            var carried = tank.MarksOn(plate).ToList();
            // Rounds do not land on top of each other: three hits leave three
            // marks, each keeping the level it arrived with, so an early scorch
            // is still a scorch after a later round punches through beside it.
            Check("hits accumulate on the plate rather than replacing each other",
                clean && first == 0 && spared
                && carried.Count == atlas.ScarLevels
                && carried.Select(m => m.Level).SequenceEqual(
                       Enumerable.Range(0, atlas.ScarLevels))
                && carried.Select(m => m.Along).Distinct().Count() == carried.Count
                && carried.Select(m => m.Up).Distinct().Count() == carried.Count,
                $"{plate} carries {string.Concat(carried.Select(m => m.Level))},"
                + $" {other} stayed {(spared ? "clean" : "marked")}");
            for (int i = 0; i < 4; i++)
                tank.Damage(plate, 0.2f, 0.1f);
            var full = tank.MarksOn(plate).ToList();
            int worst = tank.ScarLevel(plate);
            tank.Repair();
            // Capped at one per level, which is a budget and also what keeps
            // them looking unalike: the decals are rendered art, so two marks
            // at one level are the same drawing twice.
            Check("a plate carries no more marks than there are levels, and R takes them off",
                full.Count == atlas.ScarLevels
                && full.All(m => m.Level >= 0 && m.Level < atlas.ScarLevels)
                && worst == atlas.ScarLevels - 1
                && tank.MarksOn(plate).Count == 0,
                $"{full.Count} marks after {atlas.ScarLevels + 4} hits,"
                + $" worst {worst}");

            // What the calibre is to the armour. The level used to come from the
            // hit count alone, so every round walked a plate at the same rate
            // and the dial changed nothing but the size of the flash - which is
            // the opposite of what a calibre is.
            int heavyRound = tank.Damage(plate, 0.0f, 0.0f, atlas.ScarLevels);
            tank.Repair();
            int lightFirst = tank.Damage(plate, 0.0f, 0.0f, 1);
            int lightSecond = tank.Damage(plate, 0.15f, 0.1f, 1);
            tank.Repair();
            Check("a heavier round goes deeper into clean armour",
                heavyRound == atlas.ScarLevels - 1 && lightFirst == 0 && lightSecond == 1,
                $"heavy left {heavyRound}, light left {lightFirst} then {lightSecond}");
            // The other half, and the two do not fight: three light rounds reach
            // the same hole one heavy round makes, they just take three goes.
            tank.Damage(plate, 0.0f, 0.0f, 2);
            int onTop = tank.Damage(plate, 0.15f, 0.1f, 1);
            int past = tank.Damage(plate, 0.3f, -0.1f, atlas.ScarLevels);
            int wear = tank.Wear(plate);
            tank.Repair();
            Check("armour only ever gets worse, and stops at the deepest level",
                onTop == 2 && past == atlas.ScarLevels - 1 && wear == atlas.ScarLevels,
                $"gouge then a light round left {onTop}, then a heavy one {past},"
                + $" worked {wear} of {atlas.ScarLevels}");
            // One calibre per level of damage, which is what makes "the dial
            // picks the hole" a rule rather than a coincidence of two lists
            // happening to be three long today.
            Check("the calibres span exactly the levels of damage there are",
                Ordnance.Count == atlas.ScarLevels
                && Ordnance.BiteFor(0) == 1
                && Ordnance.BiteFor(Ordnance.Count - 1) == atlas.ScarLevels,
                $"{Ordnance.Count} calibres against {atlas.ScarLevels} levels,"
                + $" biting {Ordnance.BiteFor(0)}..{Ordnance.BiteFor(Ordnance.Count - 1)}");
            tank.Repair();

            // The other kind of shell: one fired by a tank, whose class sets a
            // ceiling rather than a bite. This is the whole reason DamageTo
            // exists - under the accumulating rule above, a light tank plinking
            // a heavy holes it on the third shot, which is exactly what the
            // matchup forbids.
            int plink = tank.DamageTo(plate, 0.0f, 0.0f, 0);
            int plinkAgain = tank.DamageTo(plate, 0.2f, 0.1f, 0);
            int plinkThird = tank.DamageTo(plate, 0.4f, -0.1f, 0);
            Check("a round with a ceiling never gets past it, however often it lands",
                plink == 0 && plinkAgain == 0 && plinkThird == 0
                && tank.Wear(plate) == 1 && tank.Penetrations == 0,
                $"{plink}/{plinkAgain}/{plinkThird}, worked {tank.Wear(plate)},"
                + $" {tank.Penetrations} penetrations"
                + " - three light rounds must not add up to a hole in a heavy");
            // The counterpart, and it used to assert the opposite - which is why
            // nothing caught the plate quietly recording only its first hit. A
            // ceiling stops the round getting *deeper*; it must not stop it
            // leaving a mark, or a tank under fire from one class stops showing
            // that it is under fire at all.
            Check("but every shell still leaves its own mark in its own place",
                tank.MarksOn(plate).Count == 3
                && tank.MarksOn(plate).Select(m => m.Along).Distinct().Count() == 3
                && tank.MarksOn(plate).Select(m => m.Up).Distinct().Count() == 3,
                $"{tank.MarksOn(plate).Count} marks at "
                + string.Join("/", tank.MarksOn(plate).Select(m => $"{m.Along:F2}"))
                + " - three hits on one plate are three marks");
            // It reaches its level on the first hit, unlike a bite. "A heavy
            // breaches a light" means the first round holes it, not the third.
            tank.Repair();
            Check("a round with a ceiling reaches it on the first hit",
                tank.DamageTo(plate, 0.0f, 0.0f, atlas.ScarLevels - 1)
                    == atlas.ScarLevels - 1,
                "the matchup names the outcome, not a rate of progress");
            // The mark is what this shell did; the wear is what the plate has
            // been through. They part company exactly here, and a light round
            // must not mend a hole.
            int scorchOnBreach = tank.DamageTo(plate, 0.3f, 0.1f, 0);
            Check("a shallow round on breached armour scorches and mends nothing",
                scorchOnBreach == 0 && tank.Wear(plate) == atlas.ScarLevels
                && tank.ScarLevel(plate) == atlas.ScarLevels - 1,
                $"left {scorchOnBreach}, plate worked {tank.Wear(plate)},"
                + $" worst mark {tank.ScarLevel(plate)}");

            // What the tank is carrying towards its end, as against what each
            // plate is carrying. The matchup says whether a round counts, this
            // says when enough of them have.
            tank.Repair();
            var tally = new List<int>();
            for (int i = 0; i < Gunnery.PenetrationsToKill; i++)
            {
                tank.DamageTo(plate, 0.1f * i, 0.0f, 1);
                tally.Add(tank.Penetrations);
            }
            Check("penetrations count up one per round and reach the kill exactly",
                tally.SequenceEqual(
                    Enumerable.Range(1, Gunnery.PenetrationsToKill))
                && tally[^2] < Gunnery.PenetrationsToKill,
                $"tally {string.Join("/", tally)} against"
                + $" {Gunnery.PenetrationsToKill} - the round before last must not"
                + " already be enough");
            // The tank's, not the plate's, and the difference is the whole reason
            // this is not summed off the wear: a plate a heavy has already breached
            // stops climbing while rounds keep arriving, and rounds spread over
            // three plates would count differently from three into one.
            tank.Repair();
            int spread = 0;
            foreach (string face in atlas.HitFaces.Take(3))
            {
                tank.DamageTo(face, 0.0f, 0.0f, 1);
                spread = tank.Penetrations;
            }
            tank.Repair();
            Check("the tally is the tank's, wherever on it the rounds landed",
                spread == Math.Min(3, atlas.HitFaces.Count)
                && tank.Penetrations == 0,
                $"{spread} from three plates, {tank.Penetrations} after repair");
            // The divergence itself, stated rather than implied: past the ceiling
            // the plate is done and the tank is not.
            tank.Repair();
            int deepest = atlas.ScarLevels - 1;
            for (int i = 0; i < 4; i++)
                tank.DamageTo(plate, 0.1f * i, 0.0f, deepest);
            Check("armour stops at its deepest level while the tally keeps climbing",
                tank.Wear(plate) == atlas.ScarLevels && tank.Penetrations == 4,
                $"plate worked {tank.Wear(plate)} of {atlas.ScarLevels},"
                + $" tank at {tank.Penetrations} penetrations - a tally read off the"
                + " wear would have stalled with the plate");
            tank.Repair();
            tank.Repair();

            // Where the round landed, kept as a fraction of the plate rather
            // than as pixels, so it stays on the same spot of armour through a
            // turn. Stored in pixels it would be right at the heading it was
            // taken on and wrong at the other eleven.
            tank.Repair();
            double was = tank.HullFacing;
            tank.HullFacing = 0.0;
            tank.Damage(plate, 0.5f);
            TankSprite.Mark placed = tank.MarksOn(plate)[0];
            Vector2 acrossPlate = tank.MarkOffset(plate, new TankSprite.Mark(0, 0.5f, 0.0f));
            Vector2 upPlate = tank.MarkOffset(plate, new TankSprite.Mark(0, 0.0f, 0.5f));
            Vector2 atZero = tank.MarkOffset(plate, placed);
            tank.HullFacing = 180.0;
            Vector2 atHalf = tank.MarkOffset(plate, placed);
            tank.HullFacing = was;
            // The floor is a "not accidentally zero" guard and nothing more. How
            // many pixels half a plate is worth belongs to the plate and to how
            // foreshortened it is at this heading, not to the rule being asserted:
            // HitFaces[0] is the front, and at heading 0 it is turned away and
            // squeezed, so on MTP the same half scatter that measured tens of
            // pixels on the old single-mesh medium measures 7.5. An 8px floor was
            // reporting that tank's plates rather than this behaviour. What has to
            // hold is that the mark leaves the centroid at all and reverses when
            // the hull turns round.
            Check("a mark slides along its plate and turns with it",
                atZero.Length() > 2.0 && atHalf.Length() > 2.0
                && (atZero - atHalf).Length() > atZero.Length(),
                $"{atZero} at heading 0, {atHalf} at 180");
            // The other half of the same shell. A hole appearing in the middle
            // of a plate the flash went off the end of is what one number
            // shared between them prevents.
            Check("a mark taken dead centre draws on the anchor",
                tank.MarkOffset(plate, new TankSprite.Mark(0, 0.0f, 0.0f)).Length() < 1e-3,
                "so the burst's own zero scatter puts them in the same place");

            // The second axis, and the thing it has to be is *another* direction.
            // With only the tangent every round on a plate landed at the same
            // height, so three hits read as a row of stamps rather than a group;
            // an "up" that happened to be parallel to the tangent would put them
            // straight back on that line while looking like it had been fixed.
            float cross = acrossPlate.X * upPlate.Y - acrossPlate.Y * upPlate.X;
            Check("the scatter's two axes are two directions, not one",
                upPlate.Length() > 1.0
                && Math.Abs(cross) > 0.25f * acrossPlate.Length() * upPlate.Length(),
                $"across {acrossPlate}, up {upPlate}"
                + $" - {Mathf.RadToDeg(Math.Abs(Mathf.Asin(Math.Clamp(cross / Math.Max(acrossPlate.Length() * upPlate.Length(), 1e-6f), -1.0f, 1.0f)))):F0} deg apart");
            // Up the plate's own slope, not up the screen, which is why it can be
            // a fraction at all: a glacis leaning back foreshortens, so the same
            // half-height is fewer screen pixels on it than on a vertical flank,
            // and the shift stays on the metal without anyone clamping it.
            double tallest = 0.0, leastTall = double.MaxValue;
            foreach (string face in atlas.HitFaces)
                for (int i = 0; i < 24; i++)
                {
                    double at = 15.0 * i;
                    tallest = Math.Max(tallest, atlas.HitSlope(face, at).Length());
                    leastTall = Math.Min(leastTall, atlas.HitSlope(face, at).Length());
                    // The plate is wider than it is tall on every tank measured,
                    // which is why the vertical range is the smaller fraction as
                    // well - see Main.RiseAt.
                    if (atlas.HitSlope(face, at).Length()
                        > atlas.HitTangent(face, at).Length())
                        tallest = -1.0;
                }
            Check("a plate is wider than it is tall, at every heading",
                tallest > 0.0 && leastTall > 0.0,
                tallest < 0.0
                    ? "a plate came out taller than it is wide - the +-0.30 against"
                      + " +-0.45 stops being an ellipse and the ruler wants rethinking"
                    : $"half-height {leastTall:F1}..{tallest:F1}px up the slope");
            // The two hashes must not be the same hash twice. 61n and 37n are both
            // invertible mod 100, so with a shared modulus the height would be a
            // function of the offset and every pair would sit on one of a handful
            // of lines - a scatter that looks random and is a lattice.
            var pairs = new HashSet<(float, float)>();
            var heights = new HashSet<float>();
            for (int n = 1; n <= 100; n++)
            {
                pairs.Add((TankTick.ScatterAt(n), TankTick.RiseAt(n)));
                heights.Add(TankTick.RiseAt(n));
            }
            Check("consecutive rounds differ in height as well as in offset",
                heights.Count > 90 && pairs.Count == 100
                && Math.Abs(TankTick.RiseAt(1)) > 0.0f,
                $"{heights.Count} distinct heights and {pairs.Count} distinct"
                + " pairs in 100 rounds - a shared modulus gives fewer of both");

            // The frame a scar draws is chosen by the hull's heading, so the
            // layer has to keep asking to be redrawn for as long as it is
            // showing anything. Gating it on the damage instead - "nothing has
            // been hit since, so nothing has changed" - froze the mark at the
            // heading the shell arrived on and it stayed there through every
            // turn afterwards. It looked exactly like a decal welded to the
            // screen rather than to the tank.
            var scarLayer = EffectLayer.Scar(atlas.ScarLayer(plate));
            scarLayer.Tank = tank;
            int marked = scarLayer.Showing;
            tank.Repair();
            int cleaned = scarLayer.Showing;
            Check("a scar layer asks to be redrawn while it is showing anything",
                marked >= 0 && cleaned < 0
                && EffectLayer.MustRedraw(marked, marked)
                && EffectLayer.MustRedraw(cleaned, marked)
                && !EffectLayer.MustRedraw(cleaned, cleaned),
                "its frame follows the hull, so standing still is not standing"
                + " still");
            scarLayer.Free();
            tank.Repair();

            Vector2 tankAnchor = atlas.Anchor / (Vector2)atlas.Tile;
            bool anchored = true, sameTile = true;
            foreach (string layer in AtlasSet.ScarNames)
            {
                Vector2 a = atlas.AnchorOf(layer) / (Vector2)atlas.TileOf(layer);
                if ((a - tankAnchor).Length() > 1e-4)
                    anchored = false;
                if (atlas.TileOf(layer) != atlas.Tile)
                    sameTile = false;
            }
            Check("every scar shares the tank's anchor, in frame fractions",
                anchored, "the slide along the plate is measured from it");
            // It lies flat on the plate and reaches nowhere, so it has no
            // reason to want a frame of its own - and a scar that grew one
            // would mean the mark had grown past the armour.
            Check("the scars are rendered at the tank's own tile",
                sameTile,
                $"{atlas.TileOf(AtlasSet.ScarNames[0]).X}px"
                + $" against {atlas.Tile.X}px");

            // Rendered *with* headings, which is the whole difference from the
            // hit pair, and indexed by level down and heading across. A
            // transposed grid looks right at heading zero and level zero.
            var scarTiles = new HashSet<int>();
            bool scarInRange = true, turns = true;
            foreach (string layer in AtlasSet.ScarNames)
            {
                if (atlas.CountOf(layer) != atlas.Count)
                    turns = false;
                foreach (int facing in atlas.RenderedFacings())
                    for (int level = 0; level < atlas.ScarLevels; level++)
                    {
                        int frame = atlas.EffectFrame(layer, level, facing);
                        scarTiles.Add(frame);
                        if (frame < 0 || frame >= atlas.ScarLevels * atlas.Count)
                            scarInRange = false;
                    }
                if (scarTiles.Count != atlas.ScarLevels * atlas.Count)
                    scarInRange = false;
                scarTiles.Clear();
            }
            Check("the scars were rendered at every heading, unlike the hit",
                turns && atlas.CountOf(AtlasSet.BurstName) == 1,
                $"{atlas.CountOf(AtlasSet.ScarNames[0])} headings"
                + $" against the burst's {atlas.CountOf(AtlasSet.BurstName)}");
            Check("every level-by-heading pair lands in its own scar tile",
                scarInRange,
                $"{atlas.ScarLevels} levels x {atlas.Count} headings");
        }

        Theme("the settings file");
        {
            // The file decorates rows; it does not declare them. So both
            // directions have to hold, and neither failure says a word on
            // screen: an entry with no row behind it is a description of
            // nothing, and a row with no entry is a control that lost its
            // Russian and quietly kept the English.
            var text = PanelText.Load(null);
            Check("panel.json loads", text.Loaded, text.Error);

            // The real row list, not a stub of one: the ids and the grouping
            // live in Main.BuildPanel, so the panel the harness just built is
            // the only thing that knows them.
            var named = new HashSet<string>();
            int untitled = 0, undescribed = 0, misfiled = 0, duplicated = 0;
            foreach ((string id, string group) in livePanel?.Ids
                                                  ?? new List<(string, string)>())
            {
                if (!named.Add(id))
                    duplicated++;
                if (text.GroupOf(id) != group)
                    misfiled++;
            }
            foreach (string id in text.RowIds)
            {
                if (!named.Contains(id))
                    undescribed++;    // counted below as "spare", see the message
                if (string.IsNullOrWhiteSpace(text.Note(id)))
                    untitled++;
            }
            // Nothing here needs the harness: the panel is stood up with stub
            // delegates, so this is about the file against the row list rather
            // than about any tank.
            Check("every row the panel builds is named in the file",
                named.All(id => text.RowIds.Contains(id)),
                $"{named.Count(id => !text.RowIds.Contains(id))} rows have no entry: "
                + string.Join(", ", named.Where(id => !text.RowIds.Contains(id)).Take(4)));
            Check("every entry in the file is a row the panel builds",
                undescribed == 0,
                $"{undescribed} spare entries: "
                + string.Join(", ", text.RowIds.Where(id => !named.Contains(id)).Take(4)));
            Check("the file groups each row where the panel puts it",
                misfiled == 0, $"{misfiled} rows are filed under another heading");
            Check("no row id is used twice", duplicated == 0,
                $"{duplicated} repeats");
            Check("every row carries a description", untitled == 0,
                $"{untitled} entries have none");
            // The flag table is the other half of the precedence rule, and it is
            // the half that fails silently: a flag naming a row that does not
            // exist protects nothing, so the file overrules the flag and the flag
            // looks like it never arrived.
            Check("every row a start-up flag claims exists",
                Main.FlaggedRows.All(id => named.Contains(id)),
                string.Join(", ", Main.FlaggedRows.Where(id => !named.Contains(id)).Take(4)));

            // Every group opens closed, and a heading shows its own rows and
            // nobody else's. The first half is the reason the headings became
            // buttons at all - fifty rows down one column is a panel nobody
            // reads - and it is the half that would rot quietly, because a
            // group left open still works and just puts the list back.
            var shut = livePanel?.Groups ?? new List<(string, bool)>();
            Check("the settings groups open collapsed",
                shut.Count > 0 && shut.All(g => !g.Open),
                $"{shut.Count(g => g.Open)} of {shut.Count} start open");
            if (shut.Count > 0 && livePanel is not null)
            {
                string first = shut[0].Id;
                livePanel.Expand(first, true);
                var now = livePanel.Groups;
                Check("a heading opens its own group and only its own",
                    now.Count(g => g.Open) == 1 && now.First(g => g.Open).Id == first,
                    $"{now.Count(g => g.Open)} open after expanding {first}");
                livePanel.Expand(first, false);
                Check("and closes it again",
                    livePanel.Groups.All(g => !g.Open), "still open");
            }

            // The second file, and it answers for a different thing: panel.json
            // decorates rows, classes.json fills in the per-class triples. Same
            // division of authority and the same two silent failures - an entry
            // for a tank that does not exist is a number that did nothing, and a
            // class with no entry quietly kept the compiled figure.
            var classes = ClassConfig.Load(null);
            Check("classes.json loads", classes.Loaded, classes.Error);
            Check("every class the harness has is named in the file",
                MovementProfile.All.All(
                    p => classes.Tags.Contains(p.Tag, StringComparer.OrdinalIgnoreCase)),
                string.Join(", ", MovementProfile.All
                    .Where(p => !classes.Tags.Contains(
                        p.Tag, StringComparer.OrdinalIgnoreCase))
                    .Select(p => p.Tag))
                + " run on the compiled figures, which nothing on screen says");
            Check("and every id in the file is a class the harness has",
                !classes.Strays().Any(),
                string.Join(", ", classes.Strays().Take(4))
                + " - a typo here is a setting for a tank that does not exist");
            Check("no class id is used twice in the file",
                classes.Duplicates.Count == 0,
                string.Join(", ", classes.Duplicates)
                + " - the second entry wins silently and the first reads as a "
                + "number that did nothing");
            // What the file is allowed to do and nothing can stop, so it is said
            // out loud instead: three classes given the same number is a spread
            // that has been flattened, and carrying that spread is the entire
            // reason those triples are per class.
            double[] tracers = MovementProfile.All.Select(p => p.TracerCalibre).ToArray();
            double[] trails = MovementProfile.All.Select(p => p.SmokeCalibre).ToArray();
            Check("the file did not flatten either class spread",
                tracers[0] < tracers[2] && trails[0] < trails[2]
                && tracers[2] / tracers[0] > 1.2 && trails[2] / trails[0] > 1.2,
                $"tracer {tracers[0]:F2}/{tracers[2]:F2},"
                + $" trail {trails[0]:F2}/{trails[2]:F2} - a level cannot put back"
                + " a difference the triple no longer has");

            // The third file, and the same division of authority a third time:
            // walls.json fills in the shape of every wall the benches lay - seed,
            // columns, courses, sides, leaves - and does not say which walls
            // exist. The two silent failures are the same pair: a record with no
            // wall behind it is a recipe for a wall that does not exist, and a
            // wall with no record quietly kept the compiled figure.
            var walls = WallConfig.Load(null);
            string[] laid = TankBench.WallNames
                .Concat(new[] { WallBench.WallName }).ToArray();
            Check("walls.json loads", walls.Loaded, walls.Error);
            Check("every wall the benches lay is named in the file",
                laid.All(n => walls.Names.Contains(n, StringComparer.OrdinalIgnoreCase)),
                string.Join(", ", laid.Where(
                    n => !walls.Names.Contains(n, StringComparer.OrdinalIgnoreCase)))
                + " run on the compiled recipes, which nothing on screen says");
            Check("and every id in the file is a wall a bench lays",
                !walls.Strays(laid).Any(),
                string.Join(", ", walls.Strays(laid).Take(4))
                + " - a typo here is a recipe for a wall that does not exist");
            Check("no wall id is used twice in the file",
                walls.Duplicates.Count == 0,
                string.Join(", ", walls.Duplicates)
                + " - the second entry wins silently and the first reads as a "
                + "recipe that did nothing");
            // What the file is allowed to do and nothing can stop, so it is said
            // out loud instead. The four samples exist to move one dial each off
            // the ring's numbers - height, thickness, seed, run - so a sample the
            // file has given the ring's own record is a second ring, and the
            // whole arrangement of four of them has quietly become one wall drawn
            // five times. Compared as written rather than as laid: two records
            // that spell the same five fields are the same wall whatever base
            // they would otherwise have started from.
            var ringRow = walls.Read(TankBench.RingName);
            string[] flat = TankBench.WallNames
                .Where(n => n != TankBench.RingName
                            && walls.Read(n) is { } row && ringRow is { } r
                            && row == r)
                .ToArray();
            Check("the file did not flatten a sample onto the ring",
                flat.Length == 0,
                string.Join(", ", flat)
                + " spell the ring's own record - a sample that differs from the "
                + "ring in nothing is the ring again");
        }

        Theme("the side panel");
        // The panel is a view of the harness's state and never a second copy of
        // it, which is one claim with two halves - and the second half is a
        // feedback loop unless something stops it, because writing a widget's
        // value emits its changed signal.
        // In the tree rather than standing on its own, so the shutdown frees it
        // like any other node. A detached branch of Controls leaves its canvas
        // items behind however carefully it is freed, and the wall of leak
        // warnings that produces at exit would hide a real one later.
        var stub = new ControlPanel();
        tank.GetParent().AddChild(stub);
        bool flag = false;
        double dial = 1.0;
        int pick = 0;
        int writes = 0;
        stub.Toggle("t.flag", "flag", () => flag, on => { flag = on; writes++; });
        // With a note line, because that is a third delegate on the row and can
        // go stale exactly like the other two. It is what the level sliders say
        // an abstract multiplier is worth in pixels, so a note that lags is a
        // number that is wrong about the thing it was added to explain.
        double noted = double.NaN;
        stub.Slide("t.dial", "dial", 0.0, 10.0, 0.5, () => dial, v => { dial = v; writes++; }, "x",
            () => { noted = dial; return "aside"; });
        stub.Choice("t.pick", "pick", new[] { "a", "b", "c" }, () => pick,
            i => { pick = i; writes++; });

        // The harness changes behind the panel's back - a key, a start-up flag,
        // R resetting the lot - and the widgets have to follow without anything
        // telling them to.
        flag = true;
        dial = 6.5;
        pick = 2;
        stub.Sync();
        Check("syncing the panel from the harness writes nothing back",
            writes == 0, $"{writes} setter calls during a sync");
        Check("a slider's note is refreshed along with it", noted == dial,
            $"note read {noted} against {dial}");

        // ...and the other direction still works, or the panel is a display.
        var widgets = new List<Control>();
        void Collect(Node node)
        {
            foreach (Node child in node.GetChildren())
            {
                if (child is CheckBox or HSlider or OptionButton)
                    widgets.Add((Control)child);
                Collect(child);
            }
        }
        Collect(stub);
        // Driven by emitting the signals a click would, rather than by writing
        // the widgets' properties. Godot is inconsistent about which of those
        // emit - a CheckBox's does and an OptionButton's does not - so property
        // writes would be testing the engine's setters instead of this wiring.
        foreach (Control widget in widgets)
        {
            if (widget is CheckBox box)
                box.EmitSignal(BaseButton.SignalName.Toggled, false);
            else if (widget is HSlider bar)
                bar.EmitSignal(Godot.Range.SignalName.ValueChanged, 1.0);
            else if (widget is OptionButton drop)
                drop.EmitSignal(OptionButton.SignalName.ItemSelected, 0);
        }
        Check("moving a control reaches the harness",
            writes == 3 && !flag && Math.Abs(dial - 1.0) < 1e-9 && pick == 0,
            $"{writes} of 3 setters ran, flag {flag}, dial {dial}, pick {pick}");
        // Nothing on it may take keyboard focus: a focused Button eats SPACE
        // and a focused slider eats the arrows, so the panel would break the
        // keys it is a view of. It broke the turret spin first.
        Check("no control on the panel takes the keyboard",
            widgets.All(w => w.FocusMode == Control.FocusModeEnum.None),
            $"{widgets.Count(w => w.FocusMode != Control.FocusModeEnum.None)}"
            + $" of {widgets.Count} would steal a key");
        stub.QueueFree();

        Theme("recoil");
        // Off by default, and this is the assertion that keeps it that way. The
        // spring below still has to be right - it is kept for the A/B - but a
        // bench that shears the hull *and* recoils the tube shows one true
        // movement and one invented one on every shot, and the invented one is
        // the larger. Nothing else in the harness is off for this reason: the
        // burning tank is off because burning is the exception.
        Check("the sprite shear is off, because the tube recoils for real now",
            !Recoil.ShearOnByDefault,
            "one shot, one recoil - and the rendered one is the true one");
        var recoil = new Recoil();
        recoil.Fire(0.0);                       // gun straight ahead
        double liftPeak = 0.0, recoilRollPeak = 0.0;
        for (int i = 0; i < 30; i++)
        {
            recoil.Update(dt);
            liftPeak = Math.Min(liftPeak, recoil.Pitch);
            recoilRollPeak = Math.Max(recoilRollPeak, Math.Abs(recoil.Roll));
        }
        int recoilSettle = -1;
        for (int i = 0; i < 240 && recoilSettle < 0; i++)
        {
            recoil.Update(dt);
            if (!recoil.Moving) recoilSettle = i;
        }
        // Firing pushes the tank backwards at gun height, above the centre of
        // mass, so the couple takes the nose up - the same sign the drive pitch
        // uses for accelerating.
        Check("firing rocks the tank back on its heels", liftPeak < -0.02,
            $"peak {liftPeak:F4}");
        Check("the kick stays inside the rigid-body amplitude",
            Math.Abs(liftPeak) < Recoil.RigidBodyPeak,
            $"peak {Math.Abs(liftPeak):F4} against {Recoil.RigidBodyPeak:F3}");
        Check("a shot down the hull axis does not roll it",
            recoilRollPeak < 1e-9, $"roll reached {recoilRollPeak:F6}");
        Check("the kick rings down quickly",
            recoilSettle >= 0 && recoilSettle * dt < 0.6,
            recoilSettle < 0 ? "never settled" : $"{recoilSettle * dt:F2}s");

        // The turret is not always pointing where the hull is, and the recoil
        // goes back along the gun rather than along the hull. Ninety degrees off
        // and the tank is rocked sideways, not backwards.
        var sideways = new Recoil();
        sideways.Fire(90.0);
        double sidePitch = 0.0, sideRoll = 0.0;
        for (int i = 0; i < 30; i++)
        {
            sideways.Update(dt);
            sidePitch = Math.Max(sidePitch, Math.Abs(sideways.Pitch));
            sideRoll = Math.Max(sideRoll, Math.Abs(sideways.Roll));
        }
        Check("a shot across the hull rolls it instead of pitching it",
            sideRoll > 0.02 && sidePitch < 1e-9,
            $"roll {sideRoll:F4}, pitch {sidePitch:F6}");

        // The level knob, and the one place it differs in kind from the
        // tremble's: it is read when the trigger goes rather than every frame,
        // so what is asserted is the shot, not the state of the spring.
        var soft = new Recoil { Level = 0.5 };
        var dead = new Recoil { Level = 0.0 };
        soft.Fire(0.0);
        dead.Fire(0.0);
        double softPeak = 0.0, deadPeak = 0.0;
        for (int i = 0; i < 30; i++)
        {
            soft.Update(dt);
            dead.Update(dt);
            softPeak = Math.Max(softPeak, Math.Abs(soft.Pitch));
            deadPeak = Math.Max(deadPeak, Math.Abs(dead.Pitch));
        }
        Check("the recoil level scales the kick",
            Math.Abs(softPeak - 0.5 * Math.Abs(liftPeak)) < 1e-9,
            $"{softPeak:F5} at level 0.5 against {Math.Abs(liftPeak):F5} at level 1");
        Check("at level zero the gun does not move the hull",
            deadPeak == 0.0 && !dead.Moving, $"peak {deadPeak:F6}");
        // The number the panel prints beside the slider has to be the number the
        // shot produces. Predicted on paper it would be 0.0945 - two and a half
        // times what a 1/60 step against this spring actually reaches - so the
        // caption would call the tuned setting dangerous and the dangerous one
        // fine.
        Check("the predicted peak is the peak that happens",
            Math.Abs(new Recoil().PeakFor(1.0) - Math.Abs(liftPeak)) < 1e-9,
            $"predicted {new Recoil().PeakFor(1.0):F5}, measured {Math.Abs(liftPeak):F5}");

        Theme("turret stabiliser");
        tank.HullFacing = 270.0;
        tank.Pitch = 0.03;
        tank.Roll = 0.02;
        tank.Shake = -2;

        tank.TurretStabilised = true;
        Vector2 hullTilt = tank.TiltFor(false);
        Vector2 turretTilt = tank.TiltFor(true);
        Check("a stabilised turret takes no tilt", turretTilt == Vector2.Zero,
            $"turret tilt {turretTilt}");
        Check("the hull still tilts", hullTilt != Vector2.Zero, $"hull tilt {hullTilt}");

        // The one that matters for the seam. A bump lifts the whole vehicle and
        // the turret is bolted to the ring; exempting it from heave as well
        // would part the two by a pixel or two at the join on every jolt.
        // Against the SNAPPED jolt and not against Shake itself, which is what
        // this said and what made it fail the day the size dial moved. Heave is
        // Shake put on the pixel grid of what it is drawn into - see
        // TankSprite.SnapHeave, which exists for that - so the two coincide only
        // when Shake happens to land on that grid: -2 does at scale 0.5 and 1.0
        // and does not at 0.8, where it becomes -2.5 and draws as the same -2
        // device pixels. The subject of this check is the stabiliser, and
        // asserting grid alignment as well was asserting something else.
        Check("heave is shared whatever the stabiliser is doing",
            tank.HeaveFor(true) == tank.HeaveFor(false)
            && tank.HeaveFor(true)
               == (float)TankSprite.SnapHeave(tank.Shake, tank.HeaveScale),
            $"{tank.HeaveFor(true)} vs {tank.HeaveFor(false)}, snapped "
            + $"{TankSprite.SnapHeave(tank.Shake, tank.HeaveScale)} at scale "
            + $"{tank.HeaveScale}");

        // And the one part that is not part of the tank takes the bump the way a
        // shadow takes it: along the sun rather than up the screen. Both halves,
        // because either alone passes on a tank that is not being jolted at all -
        // which is every tank, the rumble being off by default.
        Vector2 hullBump = tank.HeaveShift(false, grounded: false);
        Vector2 groundBump = tank.HeaveShift(false, grounded: true);
        Check("a bump lifts the tank straight up",
            Math.Abs(hullBump.X) < 1e-9 && Math.Abs(hullBump.Y) > 0.5,
            $"{hullBump}");
        // Lifted, so the shadow runs away from the tank along the sun - the same
        // direction and the same 0.70 per unit of height that every prop on the
        // board casts by.
        Check("and slides its shadow along the sun instead of lifting that too",
            groundBump.X * Stage3D.SunCast.X > 0.0
            && groundBump.Y * Stage3D.SunCast.Y > 0.0
            && groundBump.Length() > 0.5f && groundBump.Length() < 2.0f,
            $"{groundBump} for a {hullBump.Y}px jolt");

        // The tremble goes the other way: the turret is exempt from it too, so
        // the aerial stops swinging. Affordable only because the seam cost is
        // sub-pixel at the tremble amplitude - checked below - which is the whole
        // difference between this and exempting it from heave.
        //
        // Asked of TrembleFor rather than of TiltFor, because the tremble is no
        // longer summed into the tilts: it levers along the hull instead of up from
        // the ground. See TankSprite.SternWeight. Asked at all rather than dropped,
        // because the exemption is a policy and it has to hold on whichever channel
        // carries it.
        tank.Pitch = 0.0;
        tank.Roll = 0.0;
        tank.TremblePitch = 0.008;
        tank.TrembleYaw = 0.008;
        Check("a stabilised turret sits out the engine tremble",
            tank.TiltFor(true) == Vector2.Zero
            && tank.TrembleFor(true) == Vector2.Zero
            && tank.TrembleFor(false) != Vector2.Zero,
            $"turret {tank.TrembleFor(true)} vs hull {tank.TrembleFor(false)}");
        tank.TremblePitch = 0.0;
        tank.TrembleYaw = 0.0;

        // Recoil goes the other way again. A stabiliser rejects the hull moving
        // under the gun; it cannot reject the gun's own firing impulse, which
        // arrives through the mount as fast as the gun does. Nailing the turret
        // through a shot would look broken on the one event where the turret is
        // what is being watched.
        tank.Pitch = 0.0;
        tank.Roll = 0.0;
        tank.RecoilPitch = -0.045;
        Check("the turret takes the recoil whatever the stabiliser says",
            tank.TiltFor(true) != Vector2.Zero
            && tank.TiltFor(true) == tank.TiltFor(false),
            $"turret {tank.TiltFor(true)} vs hull {tank.TiltFor(false)}");

        // The separate mode: the same kick, given to the turret alone. It is not
        // "the hull skips it" - the pivot moves too, from the contact patch to
        // the ring, because that is what a mount absorbing the recoil does.
        tank.RecoilTurretOnly = true;
        Check("on turret-only recoil the hull is not moved at all",
            tank.TiltFor(false) == Vector2.Zero
            && tank.MountTiltFor(false) == Vector2.Zero,
            $"hull ground {tank.TiltFor(false)}, mount {tank.MountTiltFor(false)}");
        Check("and the turret still is, past the stabiliser",
            tank.MountTiltFor(true) != Vector2.Zero,
            $"turret mount tilt {tank.MountTiltFor(true)} with the stabiliser on");
        // The gate the draw goes through. A stabilised turret standing still has
        // no ground tilt whatever, so asking only TiltFor would have skipped the
        // shear and drawn the kick nowhere.
        Check("the draw gate sees a kick the ground tilt cannot show",
            tank.Sheared(true) && tank.TiltFor(true) == Vector2.Zero,
            $"sheared {tank.Sheared(true)}, ground tilt {tank.TiltFor(true)}");
        // Why the pivot was moved. The ring is where the anchor is, so the
        // displacement there is zero by construction and the seam cannot part -
        // pivoting about the ground would have slid the whole turret across a
        // stationary hull instead. Read off the transforms rather than argued:
        // a shear applied to the anchor is its origin.
        Vector2 ringHull = tank.ShearFor(false) * Vector2.Zero;
        Vector2 ringTurret = tank.ShearFor(true) * Vector2.Zero;
        Check("turret-only recoil cannot part the seam at the ring",
            (ringTurret - ringHull).Length() < 0.01,
            $"{(ringTurret - ringHull).Length():F4}px between hull and turret "
            + "at the anchor");
        // And it does move what is above the ring, which is the whole point of
        // the mode. Measured across the screen rather than at facing 270: firing
        // into the camera, GroundDirection is only sin(elevation) long and the
        // squash takes another three quarters off it, so the same kick is 0.56px
        // there against 4.5 here. That range is the projection, not a fault, and
        // it is the one the driving pitch already has.
        tank.HullFacing = 0.0;
        Vector2 upHull = tank.ShearFor(false) * new Vector2(0.0f, -100.0f);
        Vector2 upTurret = tank.ShearFor(true) * new Vector2(0.0f, -100.0f);
        Check("but it does move the turret above it",
            (upTurret - upHull).Length() > 2.0,
            $"{(upTurret - upHull).Length():F2}px apart 100px above the ring, "
            + "broadside");
        tank.HullFacing = 270.0;
        tank.RecoilTurretOnly = false;
        tank.RecoilPitch = 0.0;
        tank.Pitch = 0.03;
        tank.Roll = 0.02;

        tank.TurretStabilised = false;
        Check("with the stabiliser off the turret rides with the hull",
            tank.TiltFor(true) == tank.TiltFor(false),
            $"{tank.TiltFor(true)} vs {tank.TiltFor(false)}");

        // What the stabiliser costs: hull and turret diverge by tilt times the
        // ring's height above the ground. The ring sits low, so this stays
        // around a pixel and the turret's skirt covers it.
        tank.TurretStabilised = true;
        const double ringHeightPx = 55.0;
        // The default configuration: the pitch on top of the tremble. The worst
        // moment is braking from cruise rather than pulling away, now that the
        // tremble grows with speed - both are near their peak there, where at a
        // standing start the tremble is at its quietest.
        double trembleGain = new EngineTremble().DriveGain;
        double defaultTilt = new Vector2((float)(new BodyPitch().Gain + trembleGain),
            (float)trembleGain).Length();
        Check("stabilising costs about a pixel at the seam",
            defaultTilt * ringHeightPx < 2.5,
            $"{defaultTilt * ringHeightPx:F2}px at a {ringHeightPx:F0}px ring");

        // The rumble is an opt-in extra now rather than the baseline, so it can
        // land on top of everything else. The bound is looser here on purpose:
        // this is every source at once, all in phase, all switched on by hand,
        // and it is reported rather than forbidden. The number above is the one
        // that describes what the harness does out of the box.
        double stackedTilt = new Vector2(
            (float)(new BodyPitch().Gain + trembleGain),
            (float)(new BodyRumble().RollAmplitude + trembleGain)).Length();
        Check("with the rumble stacked on as well the seam still holds",
            stackedTilt * ringHeightPx < 3.5,
            $"{stackedTilt * ringHeightPx:F2}px with pitch, rumble roll and tremble at once");

        // What lets the turret sit out the tremble at all. Exempting it from
        // heave was ruled out because the seam would have parted by a pixel or
        // two on every jolt; the tremble is an order down from that, so the
        // same exemption costs less than half a pixel and cannot show.
        Check("the tremble alone cannot part the seam visibly",
            Math.Sqrt(2.0) * trembleGain * ringHeightPx < 1.0,
            $"{Math.Sqrt(2.0) * trembleGain * ringHeightPx:F2}px at both axes peaking together");

        tank.Pitch = 0.0;
        tank.Roll = 0.0;
        tank.Shake = 0;

        if (vehicles is { Count: > 0 })
        {
            Theme("three tanks on one field");

            // One per class, which is the whole reason there are three: the size
            // difference is a comparison, and two mediums parked side by side
            // would compare nothing.
            Check("every tank has its own class",
                vehicles.Select(v => v.Profile).Distinct().Count() == vehicles.Count,
                string.Join(", ", vehicles.Select(v => $"{v.Tag} {v.Profile.Tag}")));
            Check("and its own atlas, kept for good",
                vehicles.All(v => ReferenceEquals(v.Sprite.Atlas, v.Atlas))
                && vehicles.Select(v => v.Atlas).Distinct().Count() == vehicles.Count,
                "switching selection must not re-dress one sprite - a tank left "
                + "burning has to still be burning when you come back to it");
            // Shared clocks would tremble in lockstep and exhaust on the same
            // frame, which reads as one animation played three times.
            Check("and its own clocks",
                vehicles.Select(v => (object)v.Tremble).Distinct().Count() == vehicles.Count
                && vehicles.Select(v => (object)v.Exhaust).Distinct().Count() == vehicles.Count
                && vehicles.Select(v => (object)v.Track).Distinct().Count() == vehicles.Count,
                "three engines, not one played three times");
            Check("no two tanks are parked on the same cell",
                vehicles.Select(v => v.Cell).Distinct().Count() == vehicles.Count,
                string.Join(", ", vehicles.Select(v => $"{v.Tag} ({v.Cell.X},{v.Cell.Y})")));
            // Same row, so the same screen height: what the eye compares has to be
            // size, and two tanks at different heights differ by perspective too.
            //
            // The line is the tanks whose home is on BoardMap.BenchHomeRow rather than all of
            // them, because one of them is parked on the rosette's hill on purpose -
            // see BoardMap.BenchHomes. A pair is enough to compare sizes against; asking
            // all three would fail on a map that says so out loud. Where the third
            // went is asserted below, because "somebody moved a tank" and "the map
            // changed" look identical from here.
            var line = vehicles.Where(v => v.HomeCell.Y == BoardMap.BenchHomeRow).ToList();
            Check("the ones on the home row stand in one row, so only the size "
                + "differs",
                line.Count >= 2
                && line.Select(v => field.CellAnchor(v.HomeCell).Y)
                       .Distinct().Count() == 1,
                $"{line.Count} on the row: " + string.Join(", ", vehicles.Select(
                    v => $"{v.Tag} y={field.CellAnchor(v.HomeCell).Y:F0}")));
            Check("and the one that is not is parked on the rosette",
                vehicles.Count(v => v.HomeCell.Y != BoardMap.BenchHomeRow) == 0
                || vehicles.Where(v => v.HomeCell.Y != BoardMap.BenchHomeRow)
                           .All(v => v.HomeCell == new Vector2I(11, 3)),
                string.Join(", ", vehicles.Where(v => v.HomeCell.Y != BoardMap.BenchHomeRow)
                    .Select(v => $"{v.Tag} at ({v.HomeCell.X},{v.HomeCell.Y})")));
            // Far enough apart that the silhouettes cannot touch. A heavy is about
            // 212px of hull broadside; anything closer than that would have the
            // spacing arguing with the size.
            double gap = line.Count < 2 ? double.MaxValue
                : Enumerable.Range(1, line.Count - 1).Min(
                    i => (field.CellAnchor(line[i].HomeCell)
                          - field.CellAnchor(line[i - 1].HomeCell)).Length());
            double widest = vehicles.Max(v => v.Atlas.HullSpan * v.Sprite.BodyScale);
            Check("and far enough apart that they cannot overlap",
                gap > widest,
                $"{gap:F0}px between them against {widest:F0}px of hull");

            // The selection. A click on a tank drives that tank; a click on empty
            // ground is an order. Which of the two it is comes off what is standing
            // on the cell, so this is the routing the feature is made of.
            int other = active == 0 ? vehicles.Count - 1 : 0;
            Check("clicking a tank takes control of it",
                Vehicle.SelectionFor(vehicles, active, vehicles[other].Cell) == other,
                $"click on {vehicles[other].Tag}'s cell picked "
                + $"{Vehicle.SelectionFor(vehicles, active, vehicles[other].Cell)}");
            // Otherwise the tank you are driving could not be told to stay where it
            // is, and clicking it would be a selection that does nothing.
            Check("clicking the one you are driving is not a selection",
                Vehicle.SelectionFor(vehicles, active, vehicles[active].Cell) < 0,
                "it has to fall through to a move order");
            Vector2I empty = new(field.Columns - 1, field.Rows - 1);
            Check("clicking empty ground is a move order",
                Vehicle.At(vehicles, empty) is null
                && Vehicle.SelectionFor(vehicles, active, empty) < 0,
                $"({empty.X},{empty.Y}) is not empty");

            // Paths go round the others. Straight through would be the one thing a
            // bench built to show three tanks must not draw.
            HashSet<Vector2I> taken = Vehicle.Occupied(vehicles, vehicles[active]);
            Check("the others are obstacles, not scenery",
                taken.Count == vehicles.Count - 1
                && !taken.Contains(vehicles[active].Cell),
                $"{taken.Count} cells blocked for {vehicles[active].Tag}");
            List<Vector2I> around = field.FindPath(
                vehicles[active].Cell, new Vector2I(field.Columns - 1, 2), taken);
            Check("a path routes round a tank in the way",
                around.Count > 0 && !around.Any(taken.Contains),
                $"{around.Count} steps, {around.Count(taken.Contains)} through a tank");
            Check("and a cell with a tank on it is no destination at all",
                field.FindPath(vehicles[active].Cell, vehicles[other].Cell, taken)
                    .Count == 0,
                "stopping short of where the click went reads as broken pathing");

            // Standing on the ground of its own cell, whatever size it is drawn at.
            // The anchor floats 50 to 59px above the ground, so placing a tank by
            // its anchor puts its tracks off the hex by the scale times that float
            // plus whatever the field's tile disagrees by - 17px on the light,
            // which reads as "not centred vertically" and is not the renderer:
            // tile and footprint agree there to within two pixels.
            foreach (Vehicle v in vehicles)
            {
                Vector2 ground = v.Sprite.Position
                                 + v.Atlas.GroundOffset * v.Sprite.BodyScale;
                // Flat anchor less the tank's own standing height, rather than the
                // lifted anchor: this topic runs on a flat board (SetRelief(null) at
                // the top) while a tank may have been parked on a hill, and asked
                // against the flat cell the check reports one whole Lift of error on
                // a tank that is standing exactly where it should. The invariant is
                // that the contact patch lands on the ground centre of its cell at
                // the height it stands at, and both halves of that are named here.
                Vector2 want = field.Position + field.FlatAnchor(v.Cell)
                               - new Vector2(0.0f, v.Height) + field.CentreOffset;
                Check($"{v.Tag} stands on the centre of its own cell",
                    (ground - want).Length() < 0.5,
                    $"contact patch {ground} against cell centre {want}"
                    + $" at {v.Sprite.BodyScale:F2}x");
                // And the cell that point resolves to is the cell it is on. Two
                // statements, not one: the first says the tank is drawn on its
                // hex, this says anything asking the board about that point gets
                // told the same hex - and that needs the field's own origin
                // taken off, because a ground point is in the tanks' space.
                // Left off, the answer is a cell most of a board away, which is
                // how the pond came to be stirred by tanks nowhere near it while
                // the tank in the water stirred nothing.
                //
                // Asked with the standing height put back, for the reason the
                // want above takes it off: this topic flattens the board while a
                // tank may still be carrying a hill's lift, and a flat CellAt
                // asked at a lifted point answers a row up. Live, the board has
                // its levels and CellAt does that search itself.
                Vector2 patch = ground + new Vector2(0.0f, v.Height)
                                - field.Position;
                Check($"{v.Tag}'s contact patch resolves to the cell it stands on",
                    field.CellAt(patch) == v.Cell,
                    $"{field.CellAt(patch)} against {v.Cell}; without the "
                    + $"origin taken off it reads {field.CellAt(patch + field.Position)}");
            }

            // Depth, not tree order: the tanks move, and whoever was added last
            // would otherwise win every overlap. Taken off the live position, so it
            // is right in the middle of a step and not only on arrival.
            //
            // Through field.Depth with the tank's own height, for the reason the
            // contact patch above uses the flat anchor: the depth key is the flat
            // row plus what the lift is worth along the view, and a tank parked on a
            // hill has both terms. On a flat board this is the row and nothing else,
            // which is what it was written as.
            Check("the depth order comes off the contact patch, not the cell",
                vehicles.All(v => v.Sprite.ZIndex == Mathf.RoundToInt(field.Depth(
                    field.FlatAnchor(v.Cell).Y + field.CentreOffset.Y, v.Height))),
                "parked, the two agree - the live contact patch is the one that "
                + "keeps agreeing while it drives, and at any size");
            // Same row means same depth, whatever the sizes are. Read off the
            // sprite origin instead, three tanks on one row came out with three
            // different depths and the overlap order was decided by class.
            Check("tanks on one row share a depth, whatever their size",
                vehicles.Where(v => v.Cell.Y == vehicles[active].Cell.Y
                                    && v.Cell.X % 2 == vehicles[active].Cell.X % 2)
                    .Select(v => v.Sprite.ZIndex).Distinct().Count() == 1,
                string.Join(", ", vehicles.Select(
                    v => $"{v.Tag} z={v.Sprite.ZIndex} at {v.Sprite.BodyScale:F2}x")));
            Check("the nearer tank draws over the further one",
                vehicles.OrderBy(v => field.CellAnchor(v.Cell).Y)
                    .Select(v => v.Sprite.ZIndex)
                    .SequenceEqual(vehicles.Select(v => v.Sprite.ZIndex).OrderBy(z => z)),
                string.Join(", ", vehicles.Select(
                    v => $"{v.Tag} y={field.CellAnchor(v.Cell).Y:F0} z={v.Sprite.ZIndex}")));

            // One field for three tanks. It cannot follow the selection - a grid
            // that resized when you changed tank would move every tank on it.
            Check("the field's grid does not follow the selection",
                vehicles.Any(v => ReferenceEquals(field.Atlas, v.Atlas)),
                "the tile comes from one tank's atlas and stays there");

            if (ring is not null)
            {
                Theme("the ring on the selected tank");
                ring.Target = vehicles[active];
                ring._Process(0.0);
                Check("the ring marks the tank being driven",
                    ReferenceEquals(ring.Target, vehicles[active])
                    && (ring.Position - vehicles[active].GroundPoint).Length() < 0.5,
                    $"{ring.Position} against {vehicles[active].GroundPoint}");
                // On the ground, not on the sprite's origin - the origin floats a
                // scaled 50-odd px above it, and a ring drawn there would hang in
                // the air by a different amount on each class.
                Check("it sits on the contact patch, not on the sprite's origin",
                    Math.Abs(ring.Position.Y - vehicles[active].Sprite.Position.Y)
                        > 20.0,
                    "the two are not the same point and the ring wants the lower");
                // Under every tank, so its far side passes behind the hull. That is
                // what makes it read as lying on the ground.
                Check("it draws under every tank and over the field",
                    ring.ZIndex < vehicles.Min(v => v.Sprite.ZIndex)
                    && ring.ZIndex > field.ZIndex,
                    $"ring z={ring.ZIndex}, tanks from "
                    + $"{vehicles.Min(v => v.Sprite.ZIndex)}, field {field.ZIndex}");
                // Cell-sized, never tank-sized: it says where the tank stands, and
                // a ring that grew with the class would be a second statement about
                // size arguing with the one the tanks are making.
                Vector2[] outline = SelectionRing.Outline(
                    field.Atlas!.HexRect.Size.X, field.Atlas.HexRect.Size.Y,
                    ring.Inset);
                double ringWidth = outline.Max(p => p.X) - outline.Min(p => p.X);
                double ringHeight = outline.Max(p => p.Y) - outline.Min(p => p.Y);
                Check("it is the size of the cell, not of the tank",
                    Math.Abs(ringWidth - field.Atlas.HexRect.Size.X * ring.Inset) < 0.01
                    && Math.Abs(ringHeight - field.Atlas.HexRect.Size.Y * ring.Inset) < 0.01
                    && ringWidth > vehicles.Max(v => v.Atlas.HullSpan * v.Sprite.BodyScale) * 0.9,
                    $"{ringWidth:F0}x{ringHeight:F0}px against a "
                    + $"{field.Atlas.HexRect.Size.X}x{field.Atlas.HexRect.Size.Y} cell");
                // Closed, and squashed the same way the tile is: it has to be the
                // cell's own shape or it reads as a decoration lying over the grid.
                Check("the outline is a closed hexagon in the tile's own shape",
                    outline.Length == 7 && outline[0] == outline[^1]
                    && Math.Abs(outline[1].X - outline[0].X * 0.5f) < 0.01,
                    "flat-top: vertices at +-w/2 on the axis and +-w/4 at +-h/2");
                // Nothing to mark, nothing drawn. Safe to call straight into: with
                // no target it returns before it would touch the canvas.
                ring.Target = null;
                ring._Draw();
                Check("and it draws nothing at all with no tank selected",
                    !ring.Drawn, "a marker with no target is a marker that lies");
                ring.Target = vehicles[active];
                ring._Process(0.0);
            }
        }

        Theme("movement profiles");
        MovementProfile light = MovementProfile.Light;
        MovementProfile medium = MovementProfile.Medium;
        MovementProfile heavy = MovementProfile.Heavy;

        Check("the classes are ordered light > medium > heavy",
            light.TopSpeed > medium.TopSpeed && medium.TopSpeed > heavy.TopSpeed
            && light.Accel > medium.Accel && medium.Accel > heavy.Accel
            && light.TurnRate > medium.TurnRate && medium.TurnRate > heavy.TurnRate,
            $"{light.TopSpeed}/{medium.TopSpeed}/{heavy.TopSpeed}");

        // Acceleration is what actually reads as class; if the ramps are close
        // together the three tanks feel the same however different the top
        // speeds are.
        Check("the ramps differ enough to be felt",
            heavy.RampTime - light.RampTime > 0.12,
            $"light {light.RampTime:F2}s vs heavy {heavy.RampTime:F2}s");
        Check("no ramp is so brisk it reads as an electric car",
            MovementProfile.All.All(p => p.RampTime >= 0.40),
            string.Join(", ", MovementProfile.All.Select(p => $"{p.Tag} {p.RampTime:F2}s")));
        Check("no ramp is so slow the tank never arrives",
            MovementProfile.All.All(p => p.RampTime <= 1.0),
            string.Join(", ", MovementProfile.All.Select(p => $"{p.Tag} {p.RampTime:F2}s")));

        // The crawl has to be small enough that the sideways slide while the
        // sprite swings round stays unnoticeable, and non-zero so a winding
        // path does not stutter to a halt at every bend.
        Check("the cornering crawl is a crawl, not a stop",
            MovementProfile.All.All(p => p.CornerSpeed > 5.0
                                         && p.CornerSpeed < p.TopSpeed * 0.25),
            string.Join(", ", MovementProfile.All.Select(p => $"{p.Tag} {p.CornerSpeed:F0}")));

        // Size. Authored rather than measured, because the models carry none: all
        // three hulls render within 3% of the same length, so every bit of the
        // difference on screen is these three numbers.
        Check("the classes are drawn light < medium < heavy",
            light.Size < medium.Size && medium.Size < heavy.Size,
            string.Join(", ", MovementProfile.All.Select(p => $"{p.Tag} {p.Size:F2}x")));
        Check("the medium is the size everything else is read against",
            Math.Abs(medium.Size - 1.0) < 1e-9,
            $"medium is {medium.Size:F2}x, so no atlas is drawn as rendered");
        // The spread has to be big enough to see. Two tanks a few percent apart
        // read as the same tank, and the whole point of an authored size is that
        // the class is legible without another tank beside it to compare with.
        Check("the spread between light and heavy is worth having",
            heavy.Size / light.Size > 1.2,
            $"{heavy.Size / light.Size:F2}x from lightest to heaviest");

        // And the tripwire for the day the size is baked into the render instead.
        // Right now no atlas carries a size of its own - the hulls come out within
        // a few percent of each other - so the harness multiplying one in is the
        // whole of it. Re-render the scenes at authored scales and this fails,
        // which is the point: the two would then be multiplying together, and a
        // heavy drawn 1.15x of an already-heavy atlas is 1.32x by accident.
        if (atlases is { Count: > 1 })
        {
            int[] spans = atlases.Values.Select(a => a.HullSpan).ToArray();
            Check("the atlases still carry no size of their own",
                spans.Min() > 0 && (double)spans.Max() / spans.Min() < 1.1,
                string.Join(", ", atlases.Select(kv => $"{kv.Key} {kv.Value.HullSpan}px"))
                + " - if a scale has been baked in, stop scaling here too");
        }

        // How the size reaches the sprite: the node's own scale, so the layers,
        // the children and a shell's offset on its plate all take it about one
        // point. That point is the anchor because the anchor is the node's origin.
        float wasScale = tank.BodyScale;
        tank.BodyScale = 0.85f;
        Check("size is a uniform scale on the tank node",
            Math.Abs(tank.Scale.X - 0.85f) < 1e-6
            && Math.Abs(tank.Scale.Y - 0.85f) < 1e-6,
            $"{tank.Scale} - a tank wider than it is tall is a distorted render");
        Vector2 armLocal = new(40.0f, 0.0f);
        Check("it scales about the anchor, so the axis itself does not move",
            tank.Transform.BasisXform(Vector2.Zero) == Vector2.Zero
            && Math.Abs(tank.Transform.BasisXform(armLocal).X - 34.0f) < 1e-4,
            $"a 40px arm draws at {tank.Transform.BasisXform(armLocal).X:F2}px");
        tank.BodyScale = wasScale;

        // The one thing outside the node that has to follow it. A belt wound at
        // the unscaled link on a tank drawn smaller slips by exactly the factor
        // it was scaled by - which looks like a track bug and is a size bug.
        var wound = new TrackLoop { Phases = 8, Pitch = 10.0, Scale = 0.5 };
        wound.Advance(2.5, 1.0);
        Check("a scaled tank winds its belt by the scaled link",
            Math.Abs(wound.LinkOnScreen - 5.0) < 1e-9
            && Math.Abs(wound.Phase - 4.0) < 1e-9 && wound.Slip == 1.0,
            $"half a 5px link put it at phase {wound.Phase:F2} of 8");
        Check("and the cap moves with the link, not with the atlas",
            Math.Abs(wound.SyncSpeed
                     - 5.0 * wound.MaxPitchesPerFrame * wound.CapFrameRate) < 1e-9,
            $"{wound.SyncSpeed:F0} px/s - a coarser link stays in step further up");

        // The whole reason the cap grew a smear. A limiter that bites on two
        // classes out of three pins both of them to the same tread speed, and
        // measured at cruise the order came out *inverted*: LTP 310px/s of
        // ground showing 135px/s of tread against HTP's 175 on 175. So this
        // asks the question the eye was asking - do the belts run at visibly
        // different speeds, in the same order the tanks do.
        if (atlases is { Count: > 1 })
        {
            var shown = new List<(string Tag, double Px)>();
            foreach ((string tag, AtlasSet set) in atlases)
            {
                if (!set.HasTracks)
                    continue;
                MovementProfile profile = MovementProfile.For(tag);
                var drawn = new TrackLoop
                {
                    Phases = set.TrackPhases,
                    Pitch = set.TrackPitch,
                    Scale = profile.Size,
                };
                // What the tread actually shows: the ground, until the cap.
                shown.Add((tag, Math.Min(profile.TopSpeed, drawn.SyncSpeed)));
            }
            var byTank = shown
                .OrderByDescending(s => MovementProfile.For(s.Tag).TopSpeed)
                .ToArray();
            string read = string.Join(", ", byTank.Select(s =>
                $"{s.Tag} {s.Px:F0}px/s tread on"
                + $" {MovementProfile.For(s.Tag).TopSpeed:F0} of ground"));
            Check("a faster tank shows a faster tread",
                byTank.Length > 1
                && byTank.Zip(byTank.Skip(1)).All(p => p.First.Px > p.Second.Px),
                read + " - the limiter used to invert this");
            // And the reason the pivot radius had to stop being a fraction of
            // the hull. If the gauges were in step with the hulls the fraction
            // would have been fine; they are not, and this is the measurement
            // that says so rather than a memory of it.
            double[] arms = atlases.Values.Where(a => a.HasTracks)
                .Select(a => a.TrackArm).ToArray();
            double[] hulls = atlases.Values.Where(a => a.HasTracks)
                .Select(a => (double)a.HullSpan).ToArray();
            if (arms.Length > 1 && hulls.Min() > 0.0 && arms.Min() > 0.0)
                Check("the gauges spread wider than the hulls they sit under",
                    arms.Max() / arms.Min() - 1.0
                    > (hulls.Max() / hulls.Min() - 1.0) * 2.0,
                    $"gauge {arms.Max() / arms.Min():F2}x,"
                    + $" hull {hulls.Max() / hulls.Min():F2}x"
                    + " - a fraction of the hull cannot say this");
            Check("the tread speeds are far enough apart to read as classes",
                byTank.Length > 1
                && byTank[0].Px / byTank[^1].Px > 1.2,
                byTank.Length > 1
                    ? $"{byTank[0].Px / byTank[^1].Px:F2}x from fastest to slowest"
                    : read);
        }

        Theme("gunnery: the gun fires down a flat side and nowhere else");

        // A lane is a ray of Steps, so the two have to agree by construction -
        // and this is where that gets asserted rather than assumed, because a
        // second definition derived in axial coordinates would be right on the
        // even columns and wrong on the odd ones, which a screenshot cannot tell
        // apart from the pathfinder being wrong.
        int strays = 0;
        var firstStray = "";
        for (int q = 0; q < field.Columns; q++)
        for (int r = 0; r < field.Rows; r++)
        foreach (int heading in HexField.EdgeHeadings)
        {
            var from = new Vector2I(q, r);
            List<Vector2I> lane = field.Lane(from, heading);
            for (int i = 0; i < lane.Count; i++)
            {
                (int back, int range) = field.LaneTo(from, lane[i]);
                if (back == heading && range == i + 1)
                    continue;
                strays++;
                if (firstStray.Length == 0)
                    firstStray = $"({q},{r}) {heading} deg step {i + 1}"
                                 + $" -> {back} deg at {range}";
            }
        }
        Check("every cell down a lane reports that lane and that range",
            strays == 0, $"{strays} wrong, first {firstStray}");

        // The fact that makes the rule visible at all, and the reason the three
        // tanks cannot simply shoot each other where they are parked. If this
        // ever starts passing by accident - the home cells moved onto a common
        // ray - the feature stops demonstrating itself the first time it is
        // tried, which is worth being told about.
        Check("two cells two columns apart on the same row share no lane",
            field.LaneTo(new Vector2I(2, 2), new Vector2I(4, 2)).Heading < 0
            && field.LaneTo(new Vector2I(4, 2), new Vector2I(2, 2)).Heading < 0,
            "a gun restricted to six lanes has to have somewhere it cannot shoot");

        Check("a neighbour is one cell down the lane that steps onto it",
            HexField.EdgeHeadings.All(h =>
            {
                var mid = new Vector2I(4, 3);
                Vector2I next = HexField.Step(mid, h);
                (int lane, int range) = field.LaneTo(mid, next);
                return lane == h && range == 1
                       && HexField.HeadingTo(mid, next) == h;
            }),
            "the movement heading and the firing lane are meant to be one list");

        // Reciprocity. Not decoration: the shell leaves along the heading and
        // arrives along its reverse, and the armour model is handed that reverse
        // directly rather than a bearing snapped back to a side. If the two ends
        // of a lane disagreed, every plate would be chosen off a bearing that is
        // 60 degrees out on half the shots.
        int oneWay = 0;
        for (int q = 0; q < field.Columns; q++)
        for (int r = 0; r < field.Rows; r++)
        foreach (int heading in HexField.EdgeHeadings)
        foreach (Vector2I far in field.Lane(new Vector2I(q, r), heading))
        {
            (int back, _) = field.LaneTo(far, new Vector2I(q, r));
            if (back != (heading + 180) % 360)
                oneWay++;
        }
        Check("a lane read from the far end is the same lane reversed",
            oneWay == 0,
            $"{oneWay} pairs disagree - the plate a shell lands on comes off this");

        Check("the reverse of a lane is one of the six the armour knows",
            HexField.EdgeHeadings.All(h =>
                HexField.EdgeHeadings[Angles.SideFor((h + 180) % 360)] == (h + 180) % 360),
            "the firing heading turned round has to be a hex side exactly, "
            + "or the snap in TakeHit moves the shell to another plate");

        // The traverse has to land *on* the heading. Short by any amount and the
        // fire gate never closes, while the picture shows a turret pointing
        // straight at the target - a tank that aims and will not shoot.
        Check("the traverse lands exactly on the heading it is given",
            Gunnery.Traverse(270.0, 30.0, 5.0) != 30.0
            && Gunnery.Traverse(28.0, 30.0, 5.0) == 30.0
            && Gunnery.Laid(Gunnery.Traverse(28.0, 30.0, 5.0), 30),
            $"a 2 degree gap with 5 to spend left it at "
            + $"{Gunnery.Traverse(28.0, 30.0, 5.0):F3}");
        Check("and it goes the short way round",
            Math.Abs(WrapAngle(Gunnery.Traverse(350.0, 30.0, 10.0) - 0.0)) < 1e-9
            && Math.Abs(WrapAngle(Gunnery.Traverse(30.0, 350.0, 10.0) - 20.0)) < 1e-9,
            "350 to 30 with 10 degrees of budget should reach 0, not 340");

        Check("laying is judged tightly enough to mean pointing down the lane",
            Gunnery.Laid(30.0, 30) && !Gunnery.Laid(35.0, 30)
            && Gunnery.LayTolerance < 1.0,
            $"tolerance {Gunnery.LayTolerance} - loose enough and the tank "
            + "shoots along a lane it is not pointing down");

        // Where a shell lands against where the gun is pointing, on the real
        // geometry rather than on a made-up plate: the shooter stands one or two
        // cells down a lane, its muzzle is the one the flash comes out of, and
        // the plate is whichever one that lane arrives on. In the target's own
        // space the shooter's anchor is minus the gap between the two cell
        // centres, both anchors sitting the same height above their cells.
        //
        // Asserted rather than eyeballed because the failure is a few degrees of
        // kink between the tube and the tracer at close range and nothing at all
        // further out - visible in exactly the case a screenshot is least likely
        // to be taken of.
        {
            AtlasSet gun = tank.Atlas!;
            double worst = 0.0;
            var worstAt = "";
            int clamped = 0, tried = 0, worse = 0;
            double solvedTotal = 0.0, hashedTotal = 0.0;
            double keptHull = tank.HullFacing;
            foreach (int lane in HexField.EdgeHeadings)
            {
                Vector2 step = field.CellCentre(HexField.Step(new Vector2I(4, 3), lane))
                               - field.CellCentre(new Vector2I(4, 3));
                Vector2 aim = gun.GroundDirection(lane);
                Vector2 nose = gun.Muzzle(gun.FrameFor(lane)) - gun.Anchor;
                for (int range = 1; range <= 2; range++)
                for (int f = 0; f < 24; f++)
                {
                    double hull = 15.0 * f;
                    // The shell leaves along the lane and arrives along its
                    // reverse, which is what picks the plate.
                    string face = gun.FaceFor((lane + 180) % 360, hull);
                    Vector2 eye = nose - step * range;
                    Vector2 centroid = gun.HitOffset(face, hull);
                    Vector2 tangent = gun.HitTangent(face, hull);
                    Vector2 slope = gun.HitSlope(face, hull);
                    for (int n = 1; n <= 5; n++)
                    {
                        float rise = TankTick.RiseAt(n);
                        float bare = Gunnery.ScatterOntoBore(
                            eye, aim, centroid, tangent, slope, rise,
                            TankTick.ScatterLimit, TankTick.ScatterAt(n));
                        // What the trigger actually places, dispersion and all,
                        // rather than the three steps reassembled here.
                        float aimedAt = TankTick.AimedScatter(
                            eye, aim, centroid, tangent, slope, rise, n);
                        float off = Gunnery.BoreMiss(
                            eye, aim, centroid + tangent * aimedAt + slope * rise);
                        float aimOff = Gunnery.BoreMiss(
                            eye, aim, centroid + tangent * bare + slope * rise);
                        // What the hash on its own used to put on the armour, on
                        // the same geometry and the same round. The A/B is the
                        // claim; a solve that is merely different is not a fix.
                        float hashed = Gunnery.BoreMiss(
                            eye, aim,
                            centroid + tangent * TankTick.ScatterAt(n) + slope * rise);
                        tried++;
                        solvedTotal += off;
                        hashedTotal += hashed;
                        if (aimOff > hashed + 0.01f)
                            worse++;
                        if (Math.Abs(bare) >= TankTick.ScatterLimit - 1e-4f)
                            clamped++;
                        else if (aimOff > worst)
                        {
                            worst = aimOff;
                            worstAt = $"{face} at hull {hull:F0}, lane {lane},"
                                      + $" range {range}";
                        }
                    }
                }
            }
            tank.HullFacing = keptHull;            // Where the plate does reach the axis, it reaches it exactly. This is
            // the algebra, and it is asserted apart from the geometry so that a
            // sign slip cannot hide inside a clamp that was going to bite anyway.
            Check("a fired round lands on the gun's own axis where it can",
                tried > 1000 && clamped < tried && worst < 1.0,
                worstAt.Length == 0 ? $"{tried} shots, {clamped} clamped"
                                    : $"{worst:F2}px off at {worstAt}");
            // And where it cannot, it still gets nearer than the hash did. The
            // clamp bites on most geometries - the plates do not tile the hull,
            // so a gun laid on the enemy's ring is not pointing at the middle of
            // the plate it is about to hit - and that is why the claim worth
            // making is the comparison rather than a threshold. A solve that is
            // merely different from the hash would pass a threshold and fix
            // nothing.
            Check("and never aims further off it than the hash used to",
                worse == 0,
                $"{worse} of {tried} geometries aim worse than a hash that knows"
                + " nothing - the clamped solve is the nearest point on the plate,"
                + " so anything here is a sign slip");
            // What is actually placed, dispersion included. The residual is not
            // meant to be zero and cannot be: the gun is drawn level while the
            // plate it hits sits 20 to 60px lower, and the plate's own centre is
            // off the tank's axis on oblique headings. What went away is the part
            // that varied for no reason.
            Check("and what it places is nearer the axis than the hash was",
                solvedTotal < 0.8 * hashedTotal,
                $"mean off-axis {solvedTotal / Math.Max(tried, 1):F1}px against"
                + $" the hash's {hashedTotal / Math.Max(tried, 1):F1}px"
                + $" ({clamped} of {tried} clamped onto the near corner)");
        }

        Check("no target is not heading zero",
            !Gunnery.None.OnLane && !Gunnery.None.Clear,
            "default(Shot) would say 0 degrees, which is a real direction");

        // The focus trim holds back the ambience and not the battle. Measured on
        // a bare instance rather than on a vehicle's, because what is being
        // asserted is the rule and not whichever tank happens to be selected.
        var noSounds = new SoundSet();
        var voice = new VehicleAudio
        {
            Own = noSounds, Common = noSounds, Focused = false, MasterDb = 0.0f,
        };
        Check("the focus sets a tank's engine back but not its gun",
            Math.Abs(voice.AppliedDb - voice.UnfocusedDb) < 1e-6
            && Math.Abs(voice.AppliedEventDb) < 1e-6,
            $"loop {voice.AppliedDb:F1}dB, event {voice.AppliedEventDb:F1}dB - "
            + "trimmed events mean you only hear hits on your own tank, and in "
            + "an engagement you are the shooter");
        voice.Focused = true;
        Check("and the driven tank is trimmed nowhere",
            Math.Abs(voice.AppliedDb) < 1e-6
            && Math.Abs(voice.AppliedEventDb) < 1e-6,
            $"loop {voice.AppliedDb:F1}dB, event {voice.AppliedEventDb:F1}dB");
        voice.QueueFree();

        // The wobble is what stands in for a second take where the second take
        // could not be used. Two things are asked of it, and the second is the
        // one with history: it must not itself alternate, or the pattern that was
        // just removed comes back in a different channel.
        var wobble = new List<float>();
        for (int shell = 1; shell <= 12; shell++)
            wobble.Add(VehicleAudio.PitchOf(shell));
        Check("the impact's pitch varies and stays near unity",
            wobble.Distinct().Count() >= 4
            && wobble.All(p => p > 0.9f && p < 1.1f),
            $"{wobble.Distinct().Count()} values, "
            + $"{wobble.Min():F2}..{wobble.Max():F2}");
        Check("and it does not fall into a two-shot pattern",
            Enumerable.Range(0, wobble.Count - 2)
                .Any(i => wobble[i] != wobble[i + 2]),
            "every other shell alike is the thing this replaced");

        // The ring turns when the turret moves against the *hull*, and the two
        // readings part company in both directions - which is why this is
        // asserted rather than left to the obvious one. A turret riding round
        // with an unlocked hull moves in the world and is not being driven at
        // all; a stabilised turret holding a world heading through a hull turn
        // is being driven the whole time and does not move on screen at all.
        // Taking the rate off TurretFacing alone gets each of those backwards.
        double wasHull = tank.HullFacing;
        double wasTurret = tank.TurretFacing;
        bool wasLocked = tank.TurretHoldsHeading;
        double Offset() => WrapAngle(tank.TurretFacing - tank.HullFacing);

        tank.HullFacing = 0.0;
        tank.TurretFacing = 0.0;
        tank.TurretHoldsHeading = true;
        double wasOffset = Offset();
        tank.TurnHull(30.0);
        Check("a hull turn under a held gun drives the ring",
            Math.Abs(WrapAngle(Offset() - wasOffset) - -30.0) < 1e-6,
            $"the ring moved {WrapAngle(Offset() - wasOffset):F1} deg - a stabilised "
            + "turret is being traversed even though nothing moves on screen");

        tank.HullFacing = 0.0;
        tank.TurretFacing = 0.0;
        tank.TurretHoldsHeading = false;
        before = Offset();
        tank.TurnHull(30.0);
        Check("and a turret riding round with the hull does not",
            Math.Abs(WrapAngle(Offset() - before)) < 1e-6,
            $"the ring moved {WrapAngle(Offset() - before):F1} deg - the turret "
            + "went round the world without its motor doing anything");
        tank.HullFacing = wasHull;
        tank.TurretFacing = wasTurret;
        tank.TurretHoldsHeading = wasLocked;

        // Switched off, the ring's angle still has to be read and consumed every
        // frame. Left unread, switching the motor on mid-traverse would see the
        // whole swing since anybody last looked as one frame of it and blip.
        if (vehicles is { Count: > 0 } && vehicles[active].Audio is { } voiceOf)
        {
            Vehicle v = vehicles[active];
            bool wasMotor = voiceOf.TurretMotor;
            voiceOf.TurretMotor = false;
            v.Sprite.TurretFacing = (v.Sprite.HullFacing + 40.0) % 360.0;
            voiceOf.Update(v, 1.0 / 60.0);
            Check("the ring's angle is read even when the motor is switched off",
                Math.Abs(WrapAngle(v.LastTurretOffset - 40.0)) < 1e-6,
                $"offset left at {v.LastTurretOffset:F1} - switching the motor on "
                + "would then hear every degree since anybody last looked");
            voiceOf.TurretMotor = wasMotor;
        }

        // Every loop has to meet itself end to start - there is no crossfade.
        // The traverse motor is built rather than recorded and needed this: the
        // first attempt joined with a step forty times a typical one, which is a
        // click on a sound that plays continuously.
        if (sounds is not null)
        {
            var loops = new List<(string Where, string Name, double Ratio)>();
            foreach (KeyValuePair<string, SoundSet> set in sounds)
            foreach (string name in SoundSet.Looped)
            {
                double ratio = set.Value.SeamRatio(name);
                if (ratio >= 0.0)
                    loops.Add(($"{set.Key}/{name}", name, ratio));
            }
            if (loops.Count > 0)
                Check("every looping sample meets itself at the join",
                    loops.All(l => l.Ratio < 4.0),
                    string.Join(", ", loops.Where(l => l.Ratio >= 4.0)
                        .Select(l => $"{l.Where} {l.Ratio:F1}x"))
                    + " - a step much over a typical one is a click every lap");
        }

        // Two takes of one event exist so two shells do not sound identical -
        // they are meant to differ in character. Differing in *level* as well is
        // how one of them goes missing: the ricochet pair was 4.2dB apart, and
        // against a report starting on the same frame the quiet one simply was
        // not there. Reported as the ricochet firing every other shot, which is
        // what it was.
        //
        // Skipped when nothing loaded, because Sounds/ is gitignored and a fresh
        // checkout runs silent - which is a state the bench has to reach cleanly
        // rather than fail in.
        if (common is { Any: true })
        {
            foreach (string takes in new[] { "armour_ricochet", "armour_hit" })
            {
                double a = common.Rms($"{takes}1");
                double b = common.Rms($"{takes}2");
                if (a < 0.0 || b < 0.0)
                    continue;
                double gap = Math.Abs(20.0 * Math.Log10(a / b));
                Check($"the two takes of {takes} are the same loudness", gap < 1.0,
                    $"{gap:F1}dB apart ({a:F3} against {b:F3}) - re-run "
                    + "stage_sounds.sh, which levels them");
            }
        }

        // The matchup table, cell by cell rather than by re-deriving the rule -
        // this is the one place a wrong answer is a game rule quietly changed
        // rather than a bug, so it is written out the way it was asked for.
        MovementProfile lt = MovementProfile.Light;
        MovementProfile mt = MovementProfile.Medium;
        MovementProfile ht = MovementProfile.Heavy;
        Check("a light gun scorches a medium and a heavy and no more",
            Gunnery.Penetration(lt, mt) == 0 && Gunnery.Penetration(lt, ht) == 0,
            $"{Gunnery.Penetration(lt, mt)} / {Gunnery.Penetration(lt, ht)}");
        Check("a medium gouges a light and scorches a heavy",
            Gunnery.Penetration(mt, lt) == 1 && Gunnery.Penetration(mt, ht) == 0,
            $"{Gunnery.Penetration(mt, lt)} / {Gunnery.Penetration(mt, ht)}");
        Check("a heavy breaches both of the others",
            Gunnery.Penetration(ht, lt) == 2 && Gunnery.Penetration(ht, mt) == 2,
            $"{Gunnery.Penetration(ht, lt)} / {Gunnery.Penetration(ht, mt)}"
            + " - a difference of ranks would gouge the medium, not breach it");
        Check("a class has no special answer to its own armour",
            MovementProfile.All.All(p => Gunnery.Penetration(p, p) == 0),
            "the one cell of the table nobody specified, and the bench has one "
            + "tank of each class so it cannot be shown either");
        Check("the ranks are ordered light to heavy and distinct",
            lt.Rank < mt.Rank && mt.Rank < ht.Rank,
            $"{lt.Rank} / {mt.Rank} / {ht.Rank} - the table is a comparison");

        // The pair of classes all the way through to the sound, which is where
        // it broke: the matchup table was right, the level it produced was right,
        // and the branch turning a level into a bounce or a penetration was still
        // reading the older model's thresholds. Asserted end to end precisely
        // because both ends were correct on their own.
        Check("a gun that does not out-class the armour is heard bouncing off it",
            VehicleAudio.ImpactFor(Gunnery.Penetration(lt, mt)) == "armour_ricochet"
            && VehicleAudio.ImpactFor(Gunnery.Penetration(lt, ht)) == "armour_ricochet"
            && VehicleAudio.ImpactFor(Gunnery.Penetration(mt, ht)) == "armour_ricochet",
            "a bounce is the only thing level 0 can mean");
        Check("and one that does is heard going through",
            VehicleAudio.ImpactFor(Gunnery.Penetration(mt, lt)) == "armour_hit"
            && VehicleAudio.ImpactFor(Gunnery.Penetration(ht, lt)) == "armour_hit"
            && VehicleAudio.ImpactFor(Gunnery.Penetration(ht, mt)) == "armour_hit",
            $"a medium into a light is level {Gunnery.Penetration(mt, lt)} and "
            + $"sounds like {VehicleAudio.ImpactFor(Gunnery.Penetration(mt, lt))}"
            + " - a gouge is still a round that got in");

        if (vehicles is { Count: > 1 })
        {
            Vehicle shooter = vehicles[active];
            Vehicle foe = vehicles[active == 0 ? 1 : 0];
            Vector2I wasShooter = shooter.Cell;
            Vector2I wasFoe = foe.Cell;
            Vehicle? wasTarget = shooter.Target;

            // Put them on a lane rather than trusting where they are parked -
            // which the check above has just established they are not.
            shooter.Cell = new Vector2I(4, 3);
            foe.Cell = HexField.Step(HexField.Step(shooter.Cell, 90), 90);
            Shot clear = Gunnery.Solve(field, vehicles, shooter, foe);
            Check("a target two cells up a lane is a clear shot",
                clear.Clear && clear.Heading == 90 && clear.Range == 2,
                $"{clear}");

            // The blocker is the third tank standing between them. This is the
            // only way a solution goes away without either of the two moving,
            // which is why the reason is reported separately from "no lane".
            Vehicle? third = vehicles.FirstOrDefault(
                v => v != shooter && v != foe);
            Vector2I wasThird = third?.Cell ?? Vector2I.Zero;
            if (third is not null)
            {
                third.Cell = HexField.Step(shooter.Cell, 90);
                Shot barred = Gunnery.Solve(field, vehicles, shooter, foe);
                Check("a tank in the lane stops the shell",
                    barred.OnLane && !barred.Clear
                    && barred.BlockedAt == third.Cell,
                    $"{barred} - blocked and no lane are different answers");
                third.Cell = wasThird;
            }

            // Off the lane by one cell. The turret still tracks, which is the
            // half of the rule that is about the picture: a tank that parked its
            // gun where it happened to be would read as not having noticed.
            foe.Cell = HexField.Step(HexField.Step(shooter.Cell, 90), 30);
            Check("one cell off the lane is no shot at all",
                !Gunnery.Solve(field, vehicles, shooter, foe).OnLane,
                "a gun that fires down six lanes has to miss most of the board");

            Check("a tank cannot be given itself as a target",
                !Gunnery.Solve(field, vehicles, shooter, shooter).OnLane,
                "right-clicking your own tank is a ceasefire, not a duel");

            // Per vehicle, which is the whole of the scene: three tanks in three
            // states side by side, not one duel shown three times.
            Check("the target rides on the vehicle, not on the harness",
                vehicles.All(v => v.Target is null || v.Target != v),
                "a shared target would make selecting a tank retarget everyone");

            // Reload is a countdown rather than a timestamp, so it is the same
            // number of frames in two runs - the property --capture is fixed at
            // 1/60 for.
            // Ordered light-fastest, and every one of them longer than the shot
            // itself - 34 frames of flash and smoke, so a reload under 0.57s
            // would start the next round on top of the last one's picture. The
            // gun *report* is the other floor and it is per class (1.2s on the
            // light, 3.4s on the heavy, measured off the staged samples); it is
            // not asserted here because nothing in this code knows a sample's
            // length, and a single figure applied to all three would be the
            // heavy's answer given to the light.
            Check("the classes reload at different rates, none inside its own shot",
                vehicles.Select(v => v.Profile.ReloadTime).Distinct().Count()
                    == vehicles.Count
                && MovementProfile.Light.ReloadTime < MovementProfile.Medium.ReloadTime
                && MovementProfile.Medium.ReloadTime < MovementProfile.Heavy.ReloadTime
                && vehicles.All(v => v.Profile.ReloadTime > 34.0 / 60.0),
                string.Join(", ", vehicles.Select(
                    v => $"{v.Tag} {v.Profile.ReloadTime:F1}s")));
            Check("and traverse at different rates, heavy slowest",
                vehicles.Select(v => v.Profile.TurretRate).Distinct().Count()
                    == vehicles.Count
                && MovementProfile.Heavy.TurretRate < MovementProfile.Medium.TurretRate
                && MovementProfile.Medium.TurretRate < MovementProfile.Light.TurretRate,
                string.Join(", ", vehicles.Select(
                    v => $"{v.Tag} {v.Profile.TurretRate:F0} deg/s")));
            // The number the complaint was actually about. Nobody can judge
            // 175 deg/s against 70 without something beside it, and the thing
            // beside it is the tank's own hull: while the turret was 2.7-2.9x
            // slower than the hull it sat on, spinning the tank was the quicker
            // way to point the gun, which is backwards and is what read as
            // sluggish. Half the hull rate is a floor rather than the tuned
            // ratio, so it catches a regression without pinning the value.
            Check("and no turret is dramatically slower than its own hull",
                MovementProfile.All.All(p => p.TurretRate >= 0.5 * p.TurnRate),
                string.Join(", ", MovementProfile.All.Select(
                    p => $"{p.Tag} {p.TurretRate / p.TurnRate:F2} of hull")));
            // A multiplier over the triple, not a rate - the same claim the
            // tremble level and the size level carry, and the same failure if it
            // is ever rewritten as a rate: the 2.0x class spread goes flat on the
            // control's first drag.
            double keepLevel = Gunnery.TraverseLevel;
            Gunnery.TraverseLevel = 2.0;
            bool scales = MovementProfile.All.All(p =>
                Math.Abs(Gunnery.TraverseRate(p) - 2.0 * p.TurretRate) < 1e-9);
            double spreadAt2 = MovementProfile.Light.TurretRate
                               / MovementProfile.Heavy.TurretRate;
            Gunnery.TraverseLevel = 1.0;
            double spreadAt1 = MovementProfile.Light.TurretRate
                               / MovementProfile.Heavy.TurretRate;
            Check("the traverse level scales every class and keeps their spread",
                scales && Math.Abs(spreadAt2 - spreadAt1) < 1e-9,
                $"x2 scales all three: {scales}, spread {spreadAt1:F2} either way");
            // Turning the gun up must not make the motor sound pinned at its
            // stop: the effort is normalised against the rate the gun is *driven*
            // at, which is the one thing the two readers of that rate have to
            // agree on. Asserted through the named helper, which is why it is one.
            Gunnery.TraverseLevel = 2.0;
            double effortFast = VehicleAudio.TraverseEffort(
                MovementProfile.Medium.TurretRate, MovementProfile.Medium);
            Gunnery.TraverseLevel = 1.0;
            double effortTuned = VehicleAudio.TraverseEffort(
                MovementProfile.Medium.TurretRate, MovementProfile.Medium);
            Check("the traverse motor is normalised against the driven rate",
                Math.Abs(effortTuned - 1.0) < 1e-9
                && Math.Abs(effortFast - 0.5) < 1e-9,
                $"effort at the tuned rate {effortTuned:F2}, at x2 {effortFast:F2}");
            // At zero the gun does not move at all, which is what a level has to
            // do to be a level: the same claim the tremble and the shake carry,
            // and the one that fails if the multiplier is ever floored somewhere.
            Gunnery.TraverseLevel = 0.0;
            double stuck = Gunnery.Traverse(
                270.0, 30.0, Gunnery.TraverseRate(MovementProfile.Medium) * (1.0 / 60.0));
            Gunnery.TraverseLevel = keepLevel;
            Check("at zero the traverse level leaves the gun where it is",
                Math.Abs(WrapAngle(stuck - 270.0)) < 1e-9,
                $"asked to swing to 30, stayed at {stuck:F1}");

            // The round in the air. The property being asserted is the one the
            // whole thing was written for: the shell must not land in the frame
            // it left. Three passes at audio levels could only make the impact a
            // fairer fight against a report starting on the same frame; time is
            // what removes the masking, and time is what a faster round would
            // silently take back.
            Vector2 aim = foe.Sprite.ToLocal(
                foe.Sprite.GlobalPosition + new Vector2(0.0f, -10.0f));
            var round = new Shell
            {
                Shooter = shooter, Target = foe, ImpactLocal = aim,
                From = foe.Spot(aim) - new Vector2(Shell.PointBlank, 0.0f),
                FromLift = 0.0f,
                Serial = 1, Face = "front", Scatter = 0.0f, BoreMiss = 0.0f, Rise = 0.0f, Calibre = 1.0f,
                Level = 1,
            };
            int frames = 0;
            float was2 = -1.0f;
            bool climbed = true;
            while (!round.Arrived && frames < 600)
            {
                round.Advance(1.0 / 60.0);
                frames++;
                if (round.Fraction < was2)
                    climbed = false;
                was2 = round.Fraction;
            }
            // The floor under the speed, and the reason that slider prints frames
            // rather than px/s: past about 1660px/s the impact climbs back inside
            // the attack transient of the gun report, which is what the flight
            // was written to get it out of.
            Check("a round at point blank still takes several frames to arrive",
                round.Arrived && frames >= 4,
                $"{frames} frames over {Shell.PointBlank}px at {Shell.Speed}px/s"
                + " - landing in the frame it was fired is what this replaced");
            Check("and the speed level lengthens the flight it shortens",
                SpeedFrames(shooter, foe, aim, 0.5) > frames
                && SpeedFrames(shooter, foe, aim, 2.0) < frames,
                $"{SpeedFrames(shooter, foe, aim, 0.5)} frames at half speed and"
                + $" {SpeedFrames(shooter, foe, aim, 2.0)} at double, against"
                + $" {frames} tuned");
            Check("and it crosses rather than jumping",
                climbed && round.Fraction >= 1.0f,
                $"fraction ended at {round.Fraction:F2}");
            // The change that made the trail read as smoke rather than as an
            // effect being switched off. It is also the one most likely to be
            // undone by accident, because freeing the node the moment it lands is
            // the obvious thing to write.
            // Both ways round, because the trail is off by default now and the
            // claim is about the trail: a round laying smoke has to outlive its
            // own arrival so the line can hang - freeing the node the moment it
            // lands is the obvious thing to write and reads as the effect being
            // switched off - while a round laying none has nothing to wait for
            // and has to go at once, or it lingers drawing nothing and the trace
            // reports smoke that is not there.
            bool wasSmoking = Shell.SmokeOn;
            Shell.SmokeOn = true;
            Check("a round laying smoke outlives its own arrival so it can hang",
                round.Arrived && !round.Expired,
                $"expired {round.Expired} the frame it landed - and note Arrived "
                + "stays true from here on, which is why the strike has to be "
                + "guarded rather than driven off it");
            for (int i = 0; i < 200 && !round.Expired; i++)
                round.Advance(1.0 / 60.0);
            Check("and it does go away once the smoke has",
                round.Expired,
                $"still drawing after {Shell.SmokeSeconds}s of trail life");
            Shell.SmokeOn = false;
            Check("and one laying none goes the frame it lands",
                round.Expired,
                "a round with no trail behind it has nothing to wait for");
            Shell.SmokeOn = wasSmoking;
            // Frozen at impact. While it flies, following the target is the one
            // liberty taken; afterwards the smoke belongs to the air, and a
            // target driving off would swing the whole line round behind it.
            {
                Vector2 restedAt = round.To;
                Vector2 wasAt = foe.Sprite.Position;
                foe.Sprite.Position = wasAt + new Vector2(120.0f, 40.0f);
                Vector2 stillAt = round.To;
                foe.Sprite.Position = wasAt;
                Check("the smoke it left stops following the tank it hit",
                    restedAt.DistanceTo(stillAt) < 0.01f,
                    $"the trail moved {restedAt.DistanceTo(stillAt):F0}px when the "
                    + "target drove 126px - smoke does not ride the armour");
            }
            Check("what the trigger settled travels with the round",
                round.Face == "front" && round.Level == 1
                && Math.Abs(round.Calibre - 1.0f) < 1e-6,
                "a shell that re-read the dials would change calibre in flight");
            // A round with nobody to hit. The gun fires down a flat side of the
            // hex and hits what it is laid on; laid on nobody it still goes off,
            // and what leaves the tube has to leave, cross and land the same way
            // - see Main.Shoot. Before this the hand-fired shot was the one shot
            // on the bench with no shell in it, which is what a key that flashes
            // and does nothing else looks like from outside.
            {
                Vector2 out0 = shooter.Spot(Vector2.Zero);
                var loose = new Shell
                {
                    Shooter = shooter, Target = null,
                    From = out0, FromLift = 3.0f,
                    Ground = out0 + new Vector2(600.0f, 0.0f), GroundLift = 3.0f,
                    ImpactLocal = Vector2.Zero, Serial = 0, Face = "",
                    Scatter = 0.0f, BoreMiss = 0.0f, Rise = 0.0f, Calibre = 1.0f,
                    Level = 0,
                };
                loose.Position = loose.From;
                Check("a round with nobody to hit is aimed at the ground it was given",
                    loose.To.DistanceTo(out0 + new Vector2(600.0f, 0.0f)) < 0.01f
                    && Math.Abs(loose.ToLift - 3.0f) < 1e-6,
                    $"headed for {loose.To} rather than the point it was handed - "
                    + "a target-less round reading the anchor would fly at its own "
                    + "shooter");
                int loft = 0;
                float climbedTo = -1.0f;
                bool rose = true;
                while (!loose.Arrived && loft < 600)
                {
                    loose.Advance(1.0 / 60.0);
                    loft++;
                    if (loose.Fraction < climbedTo)
                        rose = false;
                    climbedTo = loose.Fraction;
                }
                Check("and it crosses and lands like any other",
                    loose.Arrived && loft >= 4 && rose,
                    $"{loft} frames over 600px, rising {rose} - a shot that only "
                    + "flashes is what this replaced");
                // The difference in kind between the two ends. The liberty a
                // round takes with a target exists because a tank drives away;
                // the ground does not, so there is nothing here to follow and
                // nothing to freeze.
                Vector2 landedAt = loose.To;
                Vector2 stood = shooter.Sprite.Position;
                shooter.Sprite.Position = stood + new Vector2(150.0f, 60.0f);
                Check("and the ground it went into does not walk off with a tank",
                    loose.To.DistanceTo(landedAt) < 0.01f,
                    $"the landing moved {loose.To.DistanceTo(landedAt):F0}px when "
                    + "the shooter drove - the board is not a target");
                shooter.Sprite.Position = stood;
            }
            // Whose round it is. The list was the harness's, so a bench with a
            // tank on it had a gun that only flashed - the whole flight lived in
            // the scene root, and the one-tank bench could not have it without a
            // second copy of it. Now the tank carries what it fired and the tick
            // flies it, which is the same argument TankTick itself is under.
            {
                var gun = new TankTick
                {
                    Field = field,
                    Origin = new Vector2(220, 200),
                    Vehicles = vehicles!,
                };
                foreach (Vehicle v in vehicles!)
                    v.Rounds.Clear();
                int wasShot = shooter.ShotFrame;
                double wasLoad = shooter.ReloadLeft;

                gun.Fire(shooter);
                Check("firing puts the round on the tank that fired it",
                    shooter.Rounds.Count == 1 && foe.Rounds.Count == 0,
                    $"{shooter.Rounds.Count} on the shooter and"
                    + $" {foe.Rounds.Count} on the other tank - a round belongs to"
                    + " the gun, not to the board it crosses");
                Check("and it is aimed at the ground when nobody answers for it",
                    shooter.Rounds.Count == 1 && shooter.Rounds[0].Target is null,
                    "a tick with no Launch hook is a bench: nobody to hit, so the"
                    + " shot goes into the field");

                // The seam. Who a round is for is about two tanks and stays with
                // the caller; that the round exists at all is about one and is
                // here. A hook that says "I have launched it" must not then get a
                // second round loosed underneath it.
                gun.ClearRounds();
                Vehicle? told = null;
                gun.Launch = v => { told = v; return true; };
                gun.Fire(shooter);
                Check("and a caller that launches its own is not given a second",
                    ReferenceEquals(told, shooter)
                    && shooter.Rounds.Count == 0,
                    $"asked about {(told is null ? "nobody" : told.Tag)} and"
                    + $" left {shooter.Rounds.Count} rounds up - answering the hook"
                    + " is what suppresses the ground shot");
                gun.Launch = null;

                // Flown by the tick rather than by the root, and swept when they
                // are done with. Both halves: a round that is never advanced is a
                // shot that hangs at the muzzle, and one that is never dropped is
                // a leak the trace reports as a shot in progress for ever.
                gun.ClearRounds();
                gun.Fire(shooter);
                float wasAt = shooter.Rounds.Count == 1
                    ? shooter.Rounds[0].Fraction : -1.0f;
                for (int i = 0; i < 600 && shooter.Rounds.Count > 0; i++)
                    gun.Fly(1.0 / 60.0);
                Check("the tick flies what the tank is carrying, and sweeps it up",
                    wasAt >= 0.0f && shooter.Rounds.Count == 0,
                    $"started at {wasAt:F2} and left {shooter.Rounds.Count} up");

                // The switch reaching what is already drawn, which is the whole
                // of why it is not just a flag read at launch.
                gun.ClearRounds();
                gun.TracerVisible = true;
                gun.Fire(shooter);
                bool lit = shooter.Rounds.Count == 1 && shooter.Rounds[0].Visible;
                gun.TracerVisible = false;
                gun.ShowRounds();
                Check("the tracer switch reaches the rounds already up",
                    lit && shooter.Rounds.Count == 1
                    && !shooter.Rounds[0].Visible,
                    "a round that kept its tracer because it was launched a"
                    + " moment ago reads as the switch not working");
                gun.TracerVisible = Shell.TracerOnByDefault;

                gun.ClearRounds();
                Check("and clearing takes every tank's rounds off the board",
                    shooter.Rounds.Count == 0 && foe.Rounds.Count == 0,
                    "the reset runs before the repair, so a shell left in the air"
                    + " would land on armour that has just been mended");
                shooter.ShotFrame = wasShot;
                shooter.ReloadLeft = wasLoad;
            }
            // Above every tank: depth is taken from the contact patch, so the
            // deepest a tank can sit is the height of the board.
            Check("the tracer draws over any tank on the board",
                Shell.Layer > field.Rows * (int)field.CellAnchor(0, 1).Y + 1000,
                $"{Shell.Layer} against a board {field.Rows} rows deep - a tracer "
                + "vanishing behind a hull reads as a dropped frame");
            // The tracer is a drawing and only a drawing. Both of these are the
            // kind of assertion that quietly stops being true: a default is easy
            // to flip in an initialiser nobody reads, and "hide it" is one small
            // step from "skip it".
            // The trail's one structural claim. A puff hung at a constant offset
            // behind the head travels with it and reads as a comet; smoke stands
            // where it was made. Both look identical in a screenshot, which is
            // exactly the split these tests exist across.
            {
                var trailer = new Shell
                {
                    Shooter = shooter, Target = foe, ImpactLocal = aim,
                    From = foe.Spot(aim) - new Vector2(400.0f, 0.0f), FromLift = 0.0f,
                    Serial = 3, Face = "front", Scatter = 0.0f, BoreMiss = 0.0f,
                    Rise = 0.0f, Calibre = 1.0f, Level = 1,
                };
                trailer.Position = trailer.From;
                for (int i = 0; i < 4; i++) trailer.Advance(1.0 / 60.0);
                Vector2 wasPuff = trailer.Position + trailer.PuffLocal(1);
                int born = trailer.Puffs;
                for (int i = 0; i < 4; i++) trailer.Advance(1.0 / 60.0);
                Vector2 nowPuff = trailer.Position + trailer.PuffLocal(1);
                Check("the smoke stands still while the round goes past it",
                    wasPuff.DistanceTo(nowPuff) < 1.5f && trailer.Puffs > born,
                    $"puff 1 moved {wasPuff.DistanceTo(nowPuff):F1}px in four"
                    + $" frames while the trail grew {born} -> {trailer.Puffs}"
                    + " - a trail that follows the head is a comet");
                // The one that was wrong and looked like a class difference. A
                // puff has to be born wider than the gap to its neighbour, or
                // the fresh end of the trail is beads - and the fresh end is the
                // part being looked at. It held on the medium by a fraction of a
                // pixel and failed on the light, whose puff had shrunk while the
                // spacing had not; both scale together now, so the claim is one
                // ratio rather than three cases.
                Check("a puff is born wider than the gap to the next one",
                    2.0f * Shell.SmokeSeed > Shell.SmokeStep,
                    $"{2.0f * Shell.SmokeSeed:F1}px across against a"
                    + $" {Shell.SmokeStep:F1}px step - a dotted trail is not a"
                    + " thinner trail, and no calibre can mend the ratio because"
                    + " the calibre multiplies both");
                // The line has to be continuous at the speed it is flown, and
                // the streak is what carries that now: it was 14px against a
                // 25px step and the trail closed the gap, which made the smoke
                // load-bearing for something the streak should say itself - so
                // the trail could not be taken off the default without the
                // round going dotted. Asserted on the streak rather than on the
                // trail, because that is the claim; a trail is welcome to help
                // and is no longer needed to.
                // The level is a multiplier over the tuned length, the rule every
                // other level here follows, and it has to be able to show both
                // ends: a streak long enough to hold the line, and one short
                // enough to go dotted - a slider that cannot show "too little"
                // is a slider nobody can judge the tuned value against.
                {
                    double keep = Shell.StreakLevel;
                    Shell.StreakLevel = 2.0;
                    float twice = Shell.StreakSize;
                    Shell.StreakLevel = 0.3;
                    float least = Shell.StreakSize;
                    Shell.StreakLevel = keep;
                    Check("the streak length level multiplies the tuned length",
                        Math.Abs(twice - 2.0f * Shell.Streak) < 1e-4f
                        && Math.Abs(Shell.StreakSize - Shell.Streak) < 1e-4f,
                        $"{Shell.Streak:F0}px tuned, {twice:F0}px at 2x - a"
                        + " control setting the length rather than a multiplier"
                        + " would part company with the reference proportion");
                    Check("and the bottom of its range can go dotted",
                        least < Shell.Speed / 60.0f
                        && Shell.Streak >= Shell.Speed / 60.0f,
                        $"{least:F0}px at the bottom against"
                        + $" {Shell.Speed / 60.0f:F0}px of path a frame, tuned"
                        + $" {Shell.Streak:F0}px - the tuned value has to be"
                        + " clear of the threshold and the threshold has to be"
                        + " reachable by dragging");
                }
                // Width on its own dial, and the claim is that the two dials
                // are the proportion: the size level multiplies both at once, so
                // without this one the tracer could be made bigger and smaller
                // and never slimmer. Each level has to move its own term and
                // leave the other alone, or every A/B taken on either measures
                // both - the objection that keeps the trail's thickness off the
                // tracer's dial.
                {
                    double keepW = Shell.WidthLevel;
                    double keepL = Shell.StreakLevel;
                    Shell.WidthLevel = 2.0;
                    float wideBody = Shell.BodySize, wideLen = Shell.StreakSize;
                    Shell.WidthLevel = keepW;
                    Shell.StreakLevel = 2.0;
                    float longBody = Shell.BodySize, longLen = Shell.StreakSize;
                    Shell.StreakLevel = keepL;
                    Check("the width level moves the width and nothing else",
                        Math.Abs(wideBody - 2.0f * Shell.Body) < 1e-4f
                        && Math.Abs(wideLen - Shell.Streak) < 1e-4f,
                        $"body {Shell.Body:F1} -> {wideBody:F1} at 2x while the"
                        + $" length stayed {wideLen:F0} - one dial over both can"
                        + " make the round bigger but never slimmer");
                    Check("and the length level moves the length and nothing else",
                        Math.Abs(longLen - 2.0f * Shell.Streak) < 1e-4f
                        && Math.Abs(longBody - Shell.Body) < 1e-4f,
                        $"length {Shell.Streak:F0} -> {longLen:F0} at 2x while"
                        + $" the body stayed {longBody:F1}");
                    // The hem stays a pixel outside whatever the body does,
                    // which is what stops it reappearing as a spike off the
                    // tail: written as a share of the body it would grow with it.
                    Check("and the hem does not grow with the body",
                        Shell.Edge < 0.5f * Shell.Body,
                        $"{Shell.Edge:F1}px outside a {Shell.Body:F1}px body,"
                        + " unscaled by either level");
                }
                Check("the streak covers the ground the head crosses in a frame",
                    Shell.Streak >= Shell.Speed / 60.0f,
                    $"a {Shell.Streak:F0}px streak against"
                    + $" {Shell.Speed / 60.0f:F0}px of path a frame - a round"
                    + " that outruns its own streak draws a dotted line, and no"
                    + " calibre mends it because the calibre multiplies both");
                // And it stays a streak rather than becoming a beam: the taper
                // is only readable while the thing is much longer than it is
                // wide, which is the proportion the reference is drawn at.
                Check("and it is drawn long against its own width",
                    Shell.Streak / (2.0f * Shell.Body) > 6.0f,
                    $"{Shell.Streak:F0}px long against"
                    + $" {2.0f * Shell.Body:F1}px of lit body"
                    + $" = {Shell.Streak / (2.0f * Shell.Body):F1}:1");
                // Both ends taper to nothing, which is what closes the outline -
                // a lance whose profile does not reach zero is a bar with mitred
                // corners, and at these sizes the two look the same in a still.
                Check("the lance tapers to nothing at both ends",
                    Shell.Waist(0.0f) < 1e-6f && Shell.Waist(1.0f) < 1e-6f
                    && Shell.Waist(0.25f) > 0.5f,
                    $"waist {Shell.Waist(0.0f):F3} at the head,"
                    + $" {Shell.Waist(0.25f):F3} a quarter back,"
                    + $" {Shell.Waist(1.0f):F3} at the tail");
                // Three layers across, hem outermost, and each strictly inside
                // the one before it. Written as fractions of the hem for the
                // reason the trail's spacing is: one shape at three widths.
                // Three layers across, and the middle one is the picture: a
                // white core inside a warm body inside a hem that is only there
                // to carry contrast against a pale tile - the selection ring's
                // answer, and the reason this is not additive.
                Check("the core sits well inside the lit body",
                    Shell.CoreOf < 0.7f && Shell.CoreOf > 0.25f,
                    $"core {Shell.CoreOf:F2} of the body - too wide and the"
                    + " streak is a white bar, too narrow and it is an amber one");
                // The hem is an outset, not a wider copy: written as a fraction
                // it closed later than the body and drew a black spike out of
                // the tail, which is a defect nothing else in the picture
                // explains.
                Check("and the hem is a pixel outside the body, not a share of it",
                    Shell.Edge > 0.4f && Shell.Edge < 0.5f * Shell.Body,
                    $"{Shell.Edge:F1}px outside a {Shell.Body:F1}px body - held"
                    + " outside all the way along, the hem closes where the body"
                    + " closes and the tail has no dark spike");
            }
            // Sized by the gun, not by the hit dial. One control moving two
            // things makes every A/B taken on it measure both.
            {
                double keep = Shell.TracerLevel;
                double[] atOne = MovementProfile.All
                    .Select(p => p.TracerCalibre).ToArray();
                Shell.TracerLevel = 2.0;
                bool scaled = MovementProfile.All
                    .Select((p, i) => Math.Abs(p.TracerCalibre * Shell.TracerLevel
                                               - 2.0 * atOne[i]) < 1e-9)
                    .All(x => x);
                Shell.TracerLevel = keep;
                Check("a heavier gun draws a bigger tracer, and the level keeps "
                      + "the spread",
                    atOne[0] < atOne[1] && atOne[1] < atOne[2]
                    && atOne[2] / atOne[0] > 1.5 && scaled,
                    $"{atOne[0]:F2} / {atOne[1]:F2} / {atOne[2]:F2} - a control "
                    + "setting the size rather than a multiplier flattens the "
                    + "class spread on its first drag");
            }
            // The trail is sized apart from the streak, which is one claim with
            // two halves: it has its own triple, and it has its own level. Both
            // halves fail the same silent way - one number for both looks like two
            // dials that happen to agree, and every A/B taken on either moves the
            // other effect too.
            {
                double keepTracer = Shell.TracerLevel;
                double keepSmoke = Shell.SmokeLevel;
                double[] trails = MovementProfile.All
                    .Select(p => p.SmokeCalibre).ToArray();
                double[] tracers = MovementProfile.All
                    .Select(p => p.TracerCalibre).ToArray();
                Check("a heavier gun lays a thicker trail, and wider apart than "
                      + "its streak",
                    trails[0] < trails[1] && trails[1] < trails[2]
                    && trails[2] / trails[0] > tracers[2] / tracers[0],
                    $"{trails[0]:F2} / {trails[1]:F2} / {trails[2]:F2}, a"
                    + $" {trails[2] / trails[0]:F2}x spread against the streak's"
                    + $" {tracers[2] / tracers[0]:F2}x - a soft line three to"
                    + " eighteen pixels across has room a two-pixel head has not,"
                    + " and one number for both could only suit one of them");
                Shell.TracerLevel = 2.0;
                Shell.SmokeLevel = 1.0;
                float streakOnly = round.TracerSize, trailThen = round.SmokeSize;
                Shell.TracerLevel = 1.0;
                Shell.SmokeLevel = 2.0;
                float streakThen = round.TracerSize, trailOnly = round.SmokeSize;
                Shell.TracerLevel = keepTracer;
                Shell.SmokeLevel = keepSmoke;
                Check("and the two levels move one effect each",
                    Math.Abs(streakOnly - 2.0f * streakThen) < 1e-5f
                    && Math.Abs(trailOnly - 2.0f * trailThen) < 1e-5f,
                    $"streak {streakThen:F2} -> {streakOnly:F2} on its own dial,"
                    + $" trail {trailThen:F2} -> {trailOnly:F2} on the other -"
                    + " a dial that moved both would make every A/B measure two"
                    + " effects");
            }
            // The tracer draws and the trail does not, and both halves are named
            // constants precisely so a change to either is a change somebody made
            // on purpose. The tracer was off while it was one stroke of debug
            // line, and is on now that it is a layer with a calibre to be judged;
            // the trail was on while it carried the line's continuity at speed,
            // and is off now that the streak is long enough to carry it - see
            // Shell.Streak. A trail nothing rests on, and absent from the picture
            // being matched, is decoration.
            Check("the tracer draws unless asked not to, and the trail does not",
                Shell.TracerOnByDefault && !Shell.SmokeOnByDefault
                && VehicleAudio.TurretSoundOnByDefault,
                $"tracer {Shell.TracerOnByDefault}, trail {Shell.SmokeOnByDefault},"
                + $" turret motor {VehicleAudio.TurretSoundOnByDefault}");
            // The other half of this one is not a constant and cannot be: that no
            // [mcp] line is printed by the very run this check is inside. If the
            // connection had opened, it would have opened before SelfTest.Run was
            // called - StartMcp is the last thing _Ready does, and --selftest quits
            // from _Ready before reaching it.
            Check("the bench serves its tools unless asked not to",
                Main.McpOnByDefault,
                "named for the reason the three above are. On is safe only because "
                + "--capture, --trace and --selftest refuse the connection at "
                + "StartMcp whatever the flag said: a run that answered a tool call "
                + "between frames is not the run the other half of an A/B came from");
            // The aiming ray - see AimRay. What it draws is a prediction, so
            // the claims are about agreement rather than about pixels: a ray that
            // points somewhere plausible on every frame and is wrong about the one
            // shot being watched is exactly what this is written to prevent.
            {
                var trace = new AimRay();
                Check("the aiming ray stays off unless asked", !AimRay.OnByDefault,
                    "a named constant precisely so a change to it is a change "
                    + "somebody made on purpose. On by default would put a debug "
                    + "line into every pixel diff this bench takes - which is the "
                    + "argument the tracer itself was off under while it was one "
                    + "stroke of debug drawing");
                // It ends ON the target's armour, so it has to be drawn over the
                // tank it ends on: a ray that stopped at the hull would mark the
                // air in front of the plate instead of the plate.
                Check("the ray draws over the tank it lands on",
                    AimRay.Layer > Shell.Layer
                    && Shell.Layer > field.Rows * (int)field.CellAnchor(0, 1).Y,
                    $"{AimRay.Layer} against the tracer's {Shell.Layer}");
                Check("nothing is laid out until something is aimed",
                    trace.Count == 0 && !trace.Drawn,
                    "an overlay that comes up with a ray in it is a ray belonging "
                    + "to no gun");
                trace.Aim(new Vector2(10.0f, 10.0f), new Vector2(90.0f, 40.0f),
                          AimRay.Tip.Hole);
                trace.Aim(new Vector2(11.0f, 11.0f), new Vector2(91.0f, 41.0f),
                          AimRay.Tip.Stop);
                bool one = trace.Count == 2 && trace.Marked == 1
                           && trace.At(0).From == new Vector2(10.0f, 10.0f)
                           && trace.At(0).To == new Vector2(90.0f, 40.0f)
                           && trace.At(0).End == AimRay.Tip.Hole
                           && trace.At(1).End == AimRay.Tip.Stop;
                trace.Clear();
                Check("an aim goes in as given and is dropped every frame",
                    one && trace.Count == 0,
                    "the ray is only true for the frame it was solved on - the "
                    + "turret is still traversing and the target still driving. "
                    + "Whether it claims a hit travels with it: a line that ran "
                    + "out and a line that ends in a hole are the two different "
                    + "things this overlay says");
                // The dashes are the whole of what makes it read as a measurement
                // rather than as a second tracer, and the shortest shot on this
                // board is the case that would quietly turn them into one stroke.
                // The billboard and its inverse have to be each other's, and this
                // reads the mesh Stage3D.Body actually built rather than
                // restating its arithmetic: every vertex's own UV is fed back
                // through OnBody and the vertex is required back. It is the one
                // claim the staged ray rests on - staged, the muzzle and the
                // plate are pixels in a 512px viewport, and this is what turns
                // them into a point on the screen.
                {
                    float squash = 0.5f, rise = 0.866f;
                    var line = new Vector3(0.6f, 0.8f, 30.0f);
                    float worst = 0.0f;
                    int mapped = 0, skipped = 0;
                    foreach (Vector2 lead in new[] { Vector2.Zero,
                                                     new Vector2(12.0f, 61.0f),
                                                     new Vector2(-8.0f, 44.0f) })
                    {
                        float seam = (Stage3D.PaintSize * 0.5f + lead.Y)
                                     / Stage3D.PaintSize;
                        ArrayMesh mesh = Stage3D.Body(lead, squash, rise, 0.0f,
                                                      line);
                        var arrays = mesh.SurfaceGetArrays(0);
                        Vector3[] verts = arrays[(int)Mesh.ArrayType.Vertex]
                            .AsVector3Array();
                        Vector2[] uvs = arrays[(int)Mesh.ArrayType.TexUV]
                            .AsVector2Array();
                        for (int i = 0; i < verts.Length; i++)
                        {
                            // The hinge row is the one place the surface is
                            // honestly double-valued - the standing half ends and
                            // the lying half begins in the same pixel - so a
                            // vertex sitting exactly on it has two right answers
                            // and is not evidence either way.
                            if (Math.Abs(uvs[i].Y - seam) < 1e-4f)
                            {
                                skipped++;
                                continue;
                            }
                            Vector3 back = Stage3D.OnBody(
                                uvs[i] * Stage3D.PaintSize, lead, squash, rise,
                                0.0f, line);
                            worst = Math.Max(worst, back.DistanceTo(verts[i]));
                            mapped++;
                        }
                    }
                    // Twelve is the floor rather than a round number: each cut
                    // gives twelve vertices and half of them sit on the hinge by
                    // construction, so six per cut is all there is to check and a
                    // threshold above that fails on a correct inverse - which is
                    // what it did.
                    Check("a pixel of a tank maps back onto the billboard it is "
                          + "drawn on", mapped >= 12 && worst < 0.01f,
                        $"{mapped} vertices ({skipped} on the hinge), worst "
                        + $"{worst:F4} units out - the staged ray is this inverse "
                        + "and nothing else, so a wrong one puts the mark in the "
                        + "corner of the window");
                    // And the climb slide is a branch, so it has to be shown to
                    // branch: the same pixel with and without a drop differs by
                    // the slide or by nothing, never by anything else, and some
                    // pixel has to take it or the tail half is untested.
                    var slide = new Vector3(0.0f, -18.0f / rise, -18.0f / squash);
                    int bentOn = 0, stood = 0, odd = 0;
                    for (int px = 8; px < Stage3D.PaintSize; px += 37)
                    for (int py = 8; py < Stage3D.PaintSize; py += 37)
                    {
                        var at = new Vector2(px, py);
                        Vector3 flat = Stage3D.OnBody(at, Vector2.Zero, squash,
                                                      rise, 0.0f, line);
                        Vector3 bent = Stage3D.OnBody(at, Vector2.Zero, squash,
                                                      rise, -18.0f, line);
                        if (bent.DistanceTo(flat) < 1e-3f) stood++;
                        else if (bent.DistanceTo(flat + slide) < 1e-3f) bentOn++;
                        else odd++;
                    }
                    Check("and a pixel on the trailing side of a climb is slid, "
                          + "the rest untouched", bentOn > 0 && stood > 0 && odd == 0,
                        $"{bentOn} slid, {stood} standing, {odd} neither - a third "
                        + "answer is the seam being read differently from the way "
                        + "the mesh was split along it");
                }
                Check("even the shortest shot gets several dashes",
                    Shell.PointBlank / AimRay.Dash > 5.0f,
                    $"{Shell.PointBlank:F0}px point blank over {AimRay.Dash:F0}px"
                    + " of dash and gap - a solid line out of a muzzle is a tracer");
                // The line follows the selected tank whatever its turret is
                // doing, so a line claiming nothing is most of what is ever on
                // screen, and it may not compete with the one case that claims
                // something.
                Check("a ray that claims nothing draws dimmer than one that does",
                    AimRay.Idle > 0.2f && AimRay.Idle < 1.0f,
                    $"{AimRay.Idle:F2} of the ink - at full a mark the selected "
                    + "tank carries permanently competes with a gun actually laid "
                    + "on somebody, and at nothing the sweep is lost");
                // The length. It is counted in hexes walked over the ground
                // rather than in pixels, which is the whole of the fix: a pixel
                // length is a length on a board that zooms, so the same ray
                // covered a different number of hexes at every zoom.
                var vacant = new HashSet<Vector2I>();
                Vector2 home = field.CellCentre(new Vector2I(2, 2));
                Vector2 east = new Vector2(1.0f, 0.0f);
                float roof = field.Lift * (TankTick.Clearance + 2.0f);
                (float far, Vector2I? edge, bool hit) =
                    TankTick.Track(field, home, east, roof, vacant);
                Check("a ray with nothing in the way runs to the last hex there is",
                    far > field.Atlas!.HexRect.Size.X * 2.0f && edge is null
                    && !hit,
                    $"{far:F0}px to the board's edge over a tile of "
                    + $"{field.Atlas.HexRect.Size.X:F0}px - and no cap on it, "
                    + "because a cap in cells would be a made-up number and a "
                    + "cap in pixels is the thing being removed");
                // Ground blocks it when it stands higher than the line does, and
                // a level line has one height everywhere - so the same walk
                // under a low roof stops short of the same vacant ground.
                (float under, Vector2I? wall, bool pinned) =
                    TankTick.Track(field, home, east, -1.0f, vacant);
                Check("and ground standing above the line stops it",
                    pinned && wall is not null && under < far,
                    $"{under:F0}px to {wall} against {far:F0}px vacant - the test "
                    + "is one comparison rather than a profile because the line "
                    + "is level, which is what a tank gun is");
                // A tank is the other thing that stops a shell, and the walk has
                // to name the cell it pinned in: that name is what decides
                // whether the end gets a ring, so a walk that pinned without
                // saying where cannot be marked.
                Vector2I holder = field.FlatCellAt(
                    home + east * field.Atlas.HexRect.Size.X * 1.5f);
                (float upto, Vector2I? met, bool blocked) = TankTick.Track(
                    field, home, east, roof, new HashSet<Vector2I> { holder });
                Check("and so does a tank, which is named so the ring can be "
                      + "gated on it", blocked && met == holder && upto < far,
                    $"halted at {met} after {upto:F0}px, vacant run {far:F0}px - "
                    + "the hole is drawn only where the line got as far as the "
                    + "tank it belongs to, so what halted it has to be known");
                // And the ring is the half that can lie, so where it is allowed
                // to appear is asserted on every lane rather than on one: the
                // shell leaves along a flat side of the hex and along nothing in
                // between.
                int laidOn = 0, laidOff = 0;
                foreach (int lane in HexField.EdgeHeadings)
                {
                    if (Gunnery.Laid(lane, lane))
                        laidOn++;
                    if (!Gunnery.Laid(lane + 15.0, lane)
                        && !Gunnery.Laid(lane - 15.0, lane))
                        laidOff++;
                }
                Check("and a turret between lanes is laid on none of them",
                    laidOn == 6 && laidOff == 6,
                    $"{laidOn} of six laid down their own lane, {laidOff} of six "
                    + "refusing half an atlas step off it - the atlas steps 15 "
                    + "degrees and the lanes are 60 apart, so snapping to the "
                    + "nearest would ring a hole for a shot that needs a "
                    + "traverse first, with the ring off the end of its own "
                    + "line");
                trace.QueueFree();
            }
            var unseen = new Shell
            {
                Shooter = shooter, Target = foe, ImpactLocal = aim,
                From = foe.Spot(aim) - new Vector2(Shell.PointBlank, 0.0f),
                FromLift = 0.0f,
                Serial = 2, Face = "front", Scatter = 0.0f, BoreMiss = 0.0f, Rise = 0.0f, Calibre = 1.0f,
                Level = 1,
            };
            unseen.Visible = false;
            int hidden = 0;
            while (!unseen.Arrived && hidden < 600)
            {
                unseen.Advance(1.0 / 60.0);
                hidden++;
            }
            Check("a round with its tracer hidden still flies and still lands",
                unseen.Arrived && hidden == frames,
                $"{hidden} frames hidden against {frames} drawn - the flight is "
                + "what separates the report from the impact and is not optional");
            unseen.QueueFree();

            round.QueueFree();

            shooter.Cell = wasShooter;
            foe.Cell = wasFoe;
            shooter.Target = wasTarget;
        }

        Check("an unknown tag still gets a profile",
            MovementProfile.For("nonsense") == MovementProfile.Medium
            && MovementProfile.For("ltp") == MovementProfile.Light,
            "fallback or case-insensitive lookup is broken");

        if (hadRelief)
            field.SetRelief(BoardMap.Bench.LevelsFor(field.Columns, field.Rows),
                            BoardMap.Bench.RampsFor(field.Columns, field.Rows));

        // The themes, whenever the filter asked for something no theme is called -
        // including the literal "list", which is a filter that matches nothing on
        // purpose. A filter that matches nothing otherwise reports "OK, 0 checks",
        // which is the one result that must never read as a pass.
        if (_only.Length > 0 && _reported == 0)
        {
            GD.Print("themes:");
            foreach (string name in _themes)
                GD.Print("  " + name);
            if (_listing)
            {
                GD.Print($"SELFTEST LISTED {_themes.Count} themes, "
                         + $"{_skipped} checks");
                return 0;
            }
            GD.Print($"SELFTEST FILTER \"{string.Join(",", _only)}\" matched no "
                     + "theme");
            return 1;
        }
        // Never a bare "OK" on a filtered run: a green line that says nothing
        // about how much of the run it covers is exactly how a filter turns into
        // a false pass. Hence the rule that the whole run goes before a commit.
        string scope = _only.Length == 0
            ? $"{_reported} checks"
            : $"\"{string.Join(",", _only)}\" only - {_reported} of "
              + $"{_reported + _skipped} checks, {_skipped} skipped by the filter";
        GD.Print(failed == 0 ? $"SELFTEST OK: {scope}"
                             : $"SELFTEST FAILED: {failed} of {scope}");
        return failed;
    }

    /// <summary>
    /// Which themes get reported.
    ///
    /// <b>It filters the report and not the work, and that is the whole of what
    /// it promises.</b> The run is one linear pass with variables shared across
    /// its themes, so skipping a theme's work means either restructuring it or
    /// scoping those variables out from under the rest - and the clock does not
    /// ask for it: the whole run is about twelve seconds, most of which is the
    /// engine starting. What a filter buys is the answer to "I changed the wall,
    /// show me the wall", which is a question about reading output.
    ///
    /// <b>Matched as a case-insensitive substring of the theme's own heading</b>,
    /// so the words already printed are the words to filter by and there is no
    /// second list of keys to keep in step: <c>wall</c> finds "the wall a tank
    /// drives into", <c>relief</c> finds all seven of its parts. Commas separate
    /// several.
    ///
    /// <b>A filter that matches nothing fails and prints the themes.</b> Reporting
    /// zero checks and calling it OK is the one outcome that could send somebody
    /// to a commit on the strength of a typo.
    /// </summary>
    private static void Filter(string? only)
    {
        _themes.Clear();
        _reported = 0;
        _skipped = 0;
        _listing = only is "list" or "?";
        _only = _listing || string.IsNullOrWhiteSpace(only)
            ? Array.Empty<string>()
            : only!.Split(',', StringSplitOptions.RemoveEmptyEntries
                               | StringSplitOptions.TrimEntries);
        // The listing wants every theme named and no check reported, which is a
        // filter nothing matches - written as one rather than as a second mode.
        if (_listing)
            _only = new[] { " " };
        _on = _only.Length == 0;
    }

    /// <summary>Start a theme: print its heading and decide whether its checks are
    /// reported. Every heading in the run goes through here, which is what makes
    /// the printed list the list a filter can name.</summary>
    private static void Theme(string name)
    {
        _themes.Add(name);
        _on = _only.Length == 0
              || _only.Any(key => name.Contains(key,
                                                StringComparison.OrdinalIgnoreCase));
        if (_on)
            GD.Print(name);
    }

    /// <summary>An aside inside a theme - "no art loaded", "skipped". Silent when
    /// the theme is not being reported, or a filtered run prints the notes of
    /// every theme it is not showing.</summary>
    private static void Note(string line)
    {
        if (_on)
            GD.Print(line);
    }

    private static string[] _only = Array.Empty<string>();
    private static bool _on = true;
    private static bool _listing;
    private static int _reported;
    private static int _skipped;
    private static readonly List<string> _themes = new();

    /// <summary>Frames a point-blank round takes at a given speed level. The
    /// level is restored, because a check that leaves a global moved is a check
    /// that decides what the next one measures.</summary>
    private static int SpeedFrames(Vehicle shooter, Vehicle foe, Vector2 aim,
                                   double level)
    {
        double keep = Shell.SpeedLevel;
        Shell.SpeedLevel = level;
        var round = new Shell
        {
            Shooter = shooter, Target = foe, ImpactLocal = aim,
            From = foe.Spot(aim) - new Vector2(Shell.PointBlank, 0.0f),
            FromLift = 0.0f,
            Serial = 9, Face = "front", Scatter = 0.0f, BoreMiss = 0.0f,
            Rise = 0.0f, Calibre = 1.0f, Level = 1,
        };
        int n = 0;
        while (!round.Arrived && n < 600)
        {
            round.Advance(1.0 / 60.0);
            n++;
        }
        Shell.SpeedLevel = keep;
        return n;
    }

    /// <summary>How much of two shader sources is the same line. Lines rather
    /// than a substring, because the two brick variants differ by a render mode
    /// and one statement: asking whether either contains the other would be false
    /// for both while they are still one text.</summary>
    private static double Same(string a, string b)
    {
        var one = new HashSet<string>(a.Split('\n'));
        string[] two = b.Split('\n');
        int shared = 0;
        foreach (string line in two)
            if (one.Contains(line))
                shared++;
        return two.Length == 0 ? 0.0 : (double)shared / two.Length;
    }

    private static double WrapAngle(double degrees) =>
        (degrees % 360.0 + 360.0 + 180.0) % 360.0 - 180.0;

    /// <summary>
    /// What a slope does to the drawn body: the lean, the rise, the footprint
    /// printed on the face, and the things none of them is allowed to touch.
    ///
    /// The shadow's half is asserted against the projection worked out here from
    /// the squash and the rise, not against the expression that implements it -
    /// because for a flat patch under an orthographic camera there <i>is</i> a
    /// right answer, and the point of the whole exercise is that this layer gets
    /// it while the body gets a stand-in.
    /// </summary>
    private static void Climbing(HexField field, TankSprite tank,
                                 Action<string, bool, string> Check)
    {
        Theme("climb: the body is turned by the slope and its shadow is "
                 + "flattened by it");

        float rise = field.RiseFactor;
        float squash = field.Squash;
        var lean = new ClimbLean();
        // The face a tank drives straight up: its axis is the heading every check
        // below reads the body at, so the grade along the travel is the whole of
        // its steepness and the old numbers still mean what they meant.
        Vector2 face = ClimbLean.Plane(0.25, 330.0);

        // Level ground first, because it is what every board without relief is
        // and what every measurement the bench already takes is taken on.
        for (int i = 0; i < 240; i++)
            lean.Update(Vector2.Zero, 1.0 / 60.0);
        Check("on the level it never leaves level",
            Math.Abs(lean.Angle(330.0, rise)) < 1e-9 && lean.Slope == Vector2.Zero,
            $"{Mathf.RadToDeg((float)lean.Angle(330.0, rise)):F4} deg on flat "
            + $"ground, plane {lean.Slope}");

        // The projection and the plane it is taken from, which is the claim that
        // lets one sprung state serve the body and its shadow both: reading the
        // plane along the travel has to give back the grade the plane was built
        // from, or the pitch is being drawn off a different hill than the shadow.
        Check("a plane read along its own axis is the grade it was made from",
            Math.Abs(ClimbLean.Along(face, 330.0) - 0.25) < 1e-6
            && Math.Abs(ClimbLean.Along(face, 330.0 + 90.0)) < 1e-6,
            $"{ClimbLean.Along(face, 330.0):F4} up the axis, "
            + $"{ClimbLean.Along(face, 330.0 + 90.0):F4} across it");

        // Nose up climbing, nose down descending. In Godot's screen frame a
        // positive rotation carries +x down, so the sign that reads as climbing
        // is the negative one - the reason ClimbLean.Target has a minus in it,
        // and the kind of thing that is invisible until somebody looks.
        double up = ClimbLean.Target(0.25, 330.0, rise);
        double down = ClimbLean.Target(-0.25, 330.0, rise);
        Check("a climb lifts the nose and a descent drops it",
            up < -0.02 && down > 0.02 && Math.Abs(up + down) < 1e-9,
            $"climb {Mathf.RadToDeg((float)up):F1} deg, descent "
            + $"{Mathf.RadToDeg((float)down):F1} deg");

        // The documented zero, asserted so it is not repaired. Straight up and
        // down the screen the pitch axis has no component along the view at all,
        // so an image rotation reaches none of the pitch - the whole of it is
        // foreshortening, which no transform of a flat sprite can produce.
        double across = Math.Max(Math.Abs(ClimbLean.Target(0.25, 90.0, rise)),
                                 Math.Abs(ClimbLean.Target(0.25, 270.0, rise)));
        double slant = Math.Abs(ClimbLean.Target(0.25, 30.0, rise));
        Check("driving into the screen draws no rotation, because none of it is",
            across < 1e-9 && slant > 0.1,
            $"{Mathf.RadToDeg((float)across):F3} deg into the screen against "
            + $"{Mathf.RadToDeg((float)slant):F1} across it");

        // It has to arrive over the climb rather than in one frame, and it has to
        // stop when it gets there. Same pair BodyPitch is held to, and the same
        // reason: a body that snaps to an angle has not climbed anything, and one
        // that rings afterwards is on a spring nobody wanted.
        lean.Reset();
        int frames = 0;
        double target = ClimbLean.Target(0.25, 330.0, rise);
        while (frames < 240
               && Math.Abs(lean.Angle(330.0, rise) - target) > Math.Abs(target) * 0.05)
        {
            lean.Update(face, 1.0 / 60.0);
            frames++;
        }
        Check("it arrives over a fair part of a second, not in one frame",
            frames is > 6 and < 60, $"took {frames} frames");
        // Measured after it has had time to settle, not from the moment it is
        // within five percent: taken from there the tail of the approach is the
        // biggest number in the window and the check would be reading the arrival
        // it just finished asserting.
        for (int i = 0; i < 240; i++)
            lean.Update(face, 1.0 / 60.0);
        double worst = 0.0;
        for (int i = 0; i < 240; i++)
        {
            lean.Update(face, 1.0 / 60.0);
            worst = Math.Max(worst, Math.Abs(lean.Angle(330.0, rise) - target));
        }
        Check("and settles there instead of ringing",
            worst < Math.Abs(target) * 0.02,
            $"still {Mathf.RadToDeg((float)worst):F3} deg off after four seconds");

        if (tank.Atlas is null)
            return;

        // The rise is a similarity or it is jelly. Asserted on the matrix rather
        // than on the field it is built from, because what the eye reads is the
        // matrix: equal column lengths and a right angle between them is exactly
        // "every length in the silhouette scaled by the same amount".
        double wasClimb = tank.Climb;
        float wasRise = tank.Rise;
        Vector2 wasSlope = tank.Slope;
        (double Pitch, double Roll, double Trem, double TremRoll,
         double Rec, double RecRoll) was =
            (tank.Pitch, tank.Roll, tank.TremblePitch, tank.TrembleYaw,
             tank.RecoilPitch, tank.RecoilRoll);
        try
        {
            // All six, not the two that are obvious. The tremble is on by
            // default, so a tank asked about its matrix mid-session answers with
            // a shear of half a percent in it - enough to fail "the rise squares
            // nothing" on a rise that is perfectly square, which is the check
            // measuring the wrong thing rather than the code being wrong.
            tank.Pitch = tank.Roll = tank.TremblePitch = tank.TrembleYaw =
                tank.RecoilPitch = tank.RecoilRoll = 0.0;
            tank.Climb = 0.0;
            tank.Slope = Vector2.Zero;
            tank.Rise = 1.0f + TankSprite.RisePerLevel;
            Transform2D risen = tank.ShearFor(false);
            float lx = risen.X.Length(), ly = risen.Y.Length();
            Check("the rise scales both axes alike and squares neither",
                Math.Abs(lx - ly) < 1e-4f && Math.Abs(risen.X.Dot(risen.Y)) < 1e-4f
                && Math.Abs(lx - (1.0f + TankSprite.RisePerLevel)) < 1e-4f,
                $"x {lx:F4}, y {ly:F4}, dot {risen.X.Dot(risen.Y):F5}");

            // One class apart is 0.15 and two levels of climb is 0.12. They must
            // not cross, or height starts saying what the class size is for.
            Check("two levels of climb stay inside one class of size",
                2.0f * TankSprite.RisePerLevel < 0.15f,
                $"{2.0f * TankSprite.RisePerLevel:F2} against 0.15 between classes");

            // The rise reaches the shadow, and this is the check that says so.
            // Left out, the shadow is six percent small per level and the hull
            // overhangs the ground it stands on - which is the same failure the
            // grounded flag exists to prevent, arriving from the other side.
            tank.Climb = 0.0;
            tank.Slope = Vector2.Zero;
            tank.Rise = 1.0f + TankSprite.RisePerLevel;
            Transform2D lifted = tank.ShearFor(false, true);
            Check("the rise reaches the ground the tank stands on too",
                Math.Abs(lifted.X.Length() - tank.Rise) < 1e-4f
                && Math.Abs(lifted.Y.Length() - tank.Rise) < 1e-4f
                && Math.Abs(lifted.X.Dot(lifted.Y)) < 1e-4f,
                $"x {lifted.X.Length():F4}, y {lifted.Y.Length():F4}");

            // The contact shadow takes the bumps and is not turned by the slope -
            // it is foreshortened by it. Both halves are asserted, because the
            // flag is easy to lose in a refactor and easy to over-read into "no
            // slope at all", which is what it used to mean.
            tank.Rise = 1.0f;
            tank.Climb = ClimbLean.Target(0.25, 330.0, rise);
            tank.Slope = ClimbLean.Print(face, squash, rise);
            Transform2D body = tank.ShearFor(false);
            Transform2D ground = tank.ShearFor(false, true);
            Check("a printed layer keeps every column it had, and a body does not",
                Math.Abs(ground.Y.X) < 1e-6f && Math.Abs(ground.X.X - 1.0f) < 1e-6f
                && Math.Abs(body.Y.X) > 1e-3f,
                $"printed sideways {ground.Y.X:F5}, body {body.Y.X:F5}");
            Check("and it is moved by the slope at all",
                ground != Transform2D.Identity, $"printed {ground}");

            // Exact, not approximate, and asserted against the projection rather
            // than against the expression that implements it: a ground offset is
            // put where the tilted plane puts it, worked out here from the squash,
            // the rise and the height alone. This is what catches a sign, and the
            // 1/sin(e) that is the whole difference between a plane and a guess.
            Vector2 foot = tank.Atlas.GroundOffset;
            double worstPlane = 0.0;
            foreach (Vector2 u in new[] { new Vector2(60.0f, 0.0f),
                                          new Vector2(0.0f, 55.0f),
                                          new Vector2(-40.0f, -30.0f),
                                          new Vector2(25.0f, -48.0f) })
            {
                var flat = new Vector2(u.X, -u.Y * squash);
                Vector2 want = foot + flat
                    - new Vector2(0.0f, rise * (face.X * u.X + face.Y * u.Y));
                worstPlane = Math.Max(worstPlane,
                                      (ground * (foot + flat) - want).Length());
            }
            Check("a footprint lands where the tilted plane puts it, exactly",
                worstPlane < 1e-3, $"off by {worstPlane:F4}px at worst");

            // The two readings of one plane have to agree, or the shadow slides
            // out from under the tank it belongs to. Along the travel the body's
            // rotation lifts the nose by sin(atan(g)*cos(e)) per pixel and the
            // shadow's shear by g*cos(e): the same number to three percent, which
            // is why one of them moving alone is what shows.
            tank.Climb = ClimbLean.Target(0.25, 0.0, rise);
            tank.Slope = ClimbLean.Print(ClimbLean.Plane(0.25, 0.0), squash, rise);
            var nose = new Vector2(60.0f, 0.0f);
            float byBody = (tank.ShearFor(false) * (foot + nose) - foot - nose).Y;
            float byShade = (tank.ShearFor(false, true) * (foot + nose)
                             - foot - nose).Y;
            Check("the shadow's nose rises with the hull's, not on its own",
                byBody < -10.0f && Math.Abs(byBody - byShade) < Math.Abs(byBody) * 0.05f,
                $"hull {byBody:F2}px, shadow {byShade:F2}px");

            // The documented zero of the body, and the finding that goes with it:
            // straight into the screen the rotation reaches none of the pitch, and
            // the footprint - which is planar, so affine is exact for it - takes
            // the whole of it as a stretch along the view.
            tank.Rise = 1.0f;
            tank.Climb = ClimbLean.Target(0.25, 90.0, rise);
            tank.Slope = ClimbLean.Print(ClimbLean.Plane(0.25, 90.0), squash, rise);
            Transform2D intoBody = tank.ShearFor(false);
            Transform2D intoShade = tank.ShearFor(false, true);
            // Nil rather than exactly identity: cos(90 deg) in float leaves a
            // rotation of 1e-17 in the body's matrix, and asking for the identity
            // asks the float library a question about the camera angle.
            Check("driving into the screen the shadow is stretched where the body "
                  + "cannot be",
                Math.Abs(intoBody.Y.X) < 1e-6f && Math.Abs(intoBody.X.Y) < 1e-6f
                && intoShade.Y.Y > 1.4f && Math.Abs(intoShade.X.Y) < 1e-6f,
                $"body turn {intoBody.X.Y:E2}, shadow stretch {intoShade.Y.Y:F3}");

            tank.Rise = 1.0f + TankSprite.RisePerLevel;
            tank.Climb = ClimbLean.Target(0.25, 330.0, rise);
            tank.Slope = ClimbLean.Print(face, squash, rise);

            // And that it is the shadow claiming it, off LayerOrder rather than
            // by naming the one layer: the matrix above honours the flag whether
            // or not anything ever passes it, so on its own it would go on
            // passing with the shadow leaning at fourteen degrees. Both
            // directions, because a second grounded layer is a part of the tank
            // that stopped climbing with the rest of it.
            var printed = new List<string>();
            foreach (string name in TankSprite.LayerOrder)
                if (TankSprite.MakeLayer(name).Grounded)
                    printed.Add(name);
            Check("and the shadow is the only layer that is one",
                printed.Count == 1 && printed[0] == AtlasSet.ShadowName,
                printed.Count == 0
                    ? "nothing is printed on the ground, so the shadow leans"
                    : "printed: " + string.Join(", ", printed));
        }
        finally
        {
            tank.Climb = wasClimb;
            tank.Rise = wasRise;
            tank.Slope = wasSlope;
            (tank.Pitch, tank.Roll, tank.TremblePitch, tank.TrembleYaw,
             tank.RecoilPitch, tank.RecoilRoll) = was;
        }
    }

    /// <summary>
    /// Trees on a board with height, sorted against the projection rather than
    /// against the expression that sorts them.
    ///
    /// The claim is the grove's own: a tree sorts by the same depth a tank
    /// standing where it stands would. While the board was flat that was the
    /// foot's screen row and the two agreed by accident; with height they agree
    /// only because both ask the field. Off the drawn row a wood on a rise sorts
    /// behind the entire plain.
    /// </summary>

    /// <summary>
    /// The wood on fire: what a cell does when it is lit, what it hands to its
    /// neighbours, and what one tree standing on it looks like.
    ///
    /// <b>Most of it is asked of <see cref="Wildfire"/> with no art on disk and no
    /// board under it</b>, which is why that class takes a predicate rather than a
    /// grove - the same shape <see cref="Swell"/> is in, and for the same gain: the
    /// interesting half is when the front moves, and none of it needs a tree.
    /// </summary>
    private static void Burning(HexField field, Grove? grove,
                                Action<string, bool, string> Check)
    {
        Theme("the wood on fire");

        // A board where the middle column is wooded and nothing else is, so what
        // spread does and does not reach is a statement rather than an accident.
        var wooded = new HashSet<Vector2I>();
        for (int r = 0; r < field.Rows; r++)
            wooded.Add(new Vector2I(4, r));
        var fire = new Wildfire { Field = field, Wooded = wooded.Contains };

        var lit = new Vector2I(4, 3);
        var bare = new Vector2I(6, 3);
        Check("a cell with nothing on it cannot be set alight",
            !fire.Light(bare) && !fire.LitAt(bare),
            "a bare hex quietly blackening says the fire spread where it did not");
        Check("a wooded cell can", fire.Light(lit) && fire.LitAt(lit), "");
        Check("and lighting it twice is not a restart",
            !fire.Light(lit), "the second click would put its clock back");

        // One long frame, to show the front walks rather than crossing the board.
        // A frame this long is not a thing the bench runs, which is the point: what
        // the fire does must not depend on the frame rate.
        fire.Tick(60.0);
        int reached = 0;
        for (int r = 0; r < field.Rows; r++)
            if (fire.LitAt(new Vector2I(4, r)))
                reached++;
        Check("one frame hands the fire on by one cell, however long the frame is",
            reached == 3, $"{reached} cells of the strip are alight after one tick");
        Check("and only onto wood",
            !fire.LitAt(new Vector2I(3, 3)) && !fire.LitAt(new Vector2I(5, 3)),
            "scrubless ground either side took it");

        // The edge is the pair's, not the source's: a delay hashed off one end
        // lights all six neighbours of a cell in the same frame.
        var a = new Vector2I(4, 5);
        var b = HexField.Step(a, HexField.EdgeHeadings[0]);
        Check("an edge takes the same time to cross either way",
            Mathf.Abs(fire.Delay(a, b) - fire.Delay(b, a)) < 0.0001f,
            $"{fire.Delay(a, b):F2}s against {fire.Delay(b, a):F2}s");
        var seen = new HashSet<float>();
        foreach (int heading in HexField.EdgeHeadings)
            seen.Add(Mathf.Round(fire.Delay(a, HexField.Step(a, heading)) * 100.0f));
        Check("and its six edges do not all take the same time",
            seen.Count >= 4,
            $"{seen.Count} distinct delays of six - a cell that hands the fire to "
            + "every neighbour at once draws a hexagon on the board");

        // What one tree does, over the whole of a burn. Read off a fresh fire so
        // the ages are exact.
        var clock = new Wildfire { Field = field, Wooded = wooded.Contains };
        clock.Light(lit);
        Wildfire.Coat green = clock.Of(lit, 0.5);
        Check("a tree that has not caught yet is untouched",
            green.Untouched, "the stagger is what keeps a cell from lighting at once");
        // A fifth of the way in, read off a tree that caught with its cell - the
        // stagger is asked about separately, two lines down.
        clock.Tick(clock.BurnFor * 0.2f);
        Wildfire.Coat young = clock.Of(lit, 0.0);
        Check("one that has just caught is alight and not yet charred",
            young.Flame > 0.5f && young.Char < 0.6f && !young.Burnt,
            $"flame {young.Flame:F2} char {young.Char:F2}");
        Check("two trees on one cell are not at the same point in it",
            Mathf.Abs(clock.Of(lit, 0.0).Char - clock.Of(lit, 0.9).Char) > 0.05f,
            "a cell lighting in step is one animation played eight times");

        clock.Tick(clock.BurnFor * 0.6f);       // four fifths of the way in
        Wildfire.Coat old = clock.Of(lit, 0.0);
        Check("the char deepens over the burn",
            old.Char > young.Char && !old.Burnt,
            $"{young.Char:F2} to {old.Char:F2}");
        Check("and the flame is dying down by the end of it",
            old.Flame < young.Flame && old.Flame > 0.0f,
            $"flame {young.Flame:F2} to {old.Flame:F2}");

        clock.Tick(clock.BurnFor * 0.4f);
        Wildfire.Coat done = clock.Of(lit, 0.0);
        Check("when it has finished burning there is no flame on it",
            done.Burnt && done.Flame <= 0.0f, $"flame {done.Flame:F2}");
        Check("the char stays on at the swap, because that picture is still up",
            done.Char >= 1.0f,
            "it is the outgoing picture the char belongs to, and it fades out "
            + "over the handover rather than being cut");
        Check("the smoke outlives the flame",
            done.Smoke > 0.0f, "a fire that stops smoking as it dies is a switch");
        Check("the ash does not lift",
            clock.AshAt(lit) >= 1.0f, "a mark that faded is a fire nobody can find");

        clock.Tick(clock.SmokeFor * 1.2f);
        Check("and the smoke does go, in the end",
            clock.Of(lit, 0.0).Smoke <= 0.0f, "the column would stand for good");
        Check("the ash is still there when it has",
            clock.AshAt(lit) >= 1.0f, "");

        // The swap is a window and not a frame, which is the whole of what the
        // crossfade is - and it opens inside the burn, which is the whole of what
        // the tree turning to charcoal while it is still alight is. Its own clock,
        // so the ages are exact.
        var handing = new Wildfire { Field = field, Wooded = wooded.Contains };
        handing.Light(lit);
        handing.Tick(handing.BurnFor * handing.SwapAt);
        Wildfire.Coat began = handing.Of(lit, 0.0);
        Check("the picture has not moved on the frame the handover opens",
            !began.Burnt && began.Swap <= 0.02f,
            $"swap {began.Swap:F2} - a silhouette that changes between two frames "
            + "reads as a tree replaced rather than one burnt down");
        Check("and the outgoing picture is fully charred before it starts to go",
            began.Char >= 1.0f,
            $"char {began.Char:F2} - a tree still half green, cross-faded into "
            + "charcoal, is two trees on screen at once rather than one turning");
        handing.Tick(handing.SwapFor * 0.5f);
        Wildfire.Coat half = handing.Of(lit, 0.0);
        Check("halfway through the window it is part way between the two",
            half.Swap > 0.3f && half.Swap < 0.7f, $"swap {half.Swap:F2}");
        handing.Tick(handing.SwapFor);
        Wildfire.Coat swapped = handing.Of(lit, 0.0);
        Check("and it ends on the burnt picture exactly",
            swapped.Swap >= 1.0f,
            "short of one it is a crown that never quite goes");
        // The whole point of moving it: the charcoal stands in its own fire, rather
        // than arriving after the fire has gone out. Asserted on the flame and not
        // on the clock, because a window that opens early and a burn that ends early
        // are the same two numbers read the other way round.
        Check("and the tree is standing in its charcoal while it is still burning",
            !swapped.Burnt && swapped.Flame > 0.5f,
            $"flame {swapped.Flame:F2} at a finished swap - handed over at the end "
            + "of the burn, the fire only ever plays on the green tree: it burns "
            + "without changing and then changes without burning");
        handing.Tick(handing.BurnFor);
        Check("and it is still the burnt picture once the fire is out",
            handing.Of(lit, 0.0) is { Burnt: true, Swap: >= 1.0f },
            "the state arrives at the end, the picture arrived during");

        // Shutting the window is the swap this replaced, to the frame: the A/B
        // the crossfade is judged by, and --hard-swap is that switch. Walked at
        // the fixed step rather than jumped, because what has to be true is that
        // no frame ever catches it in between.
        var cut = new Wildfire
        {
            Field = field, Wooded = wooded.Contains, SwapFor = 0.0f,
        };
        cut.Light(lit);
        bool between = false;
        bool alight = false;
        for (int f = 0; f < 900; f++)
        {
            cut.Tick(1.0 / 60.0);
            Wildfire.Coat shut = cut.Of(lit, 0.0);
            between |= shut.Swap > 0.0f && shut.Swap < 1.0f;
            alight |= shut.Swap >= 1.0f && !shut.Burnt;
        }
        Check("shutting the window is the one-frame swap it replaced",
            !between,
            "a hard swap caught halfway is a third picture rather than the one "
            + "the crossfade is measured against");
        Check("and it still swaps inside the burn, window or no window",
            alight,
            "the A/B has to differ by the crossfade and nothing else, so the hard "
            + "swap keeps the moment and drops only the fade");

        clock.Douse();
        Check("dousing puts the wood back green",
            !clock.LitAt(lit) && clock.AshAt(lit) <= 0.0f && clock.Scorched == 0,
            "a board left burning comes back alight before anybody lit it");

        // The paint, and this is the half that needs the art. A kind with no burnt
        // picture is the one the char has to stay on: it has nothing to swap to.
        var stump = new PropNode { Coat = new Wildfire.Coat(0.4f, 1.0f, 1.0f, false) };
        Check("a tree part way through the burn is charring and still itself",
            !stump.Charred && Mathf.Abs(stump.Scorch - 0.4f) < 0.0001f,
            $"scorch {stump.Scorch:F2}");
        stump.Coat = new Wildfire.Coat(0.0f, 0.0f, 0.5f, true);
        Check("one that has burnt out with no burnt art keeps every bit of its char",
            !stump.Charred && stump.Scorch >= 1.0f,
            "dropping it left bright green scrub standing in a burnt-out cell");
        Check("and never swaps, because it has nothing to swap to",
            stump.Swap <= 0.0f,
            "a fade to nothing is worse than no fade");

        // The flame's own shape, asked of the shader's text for FoamEdge's reason:
        // this is what the driver compiles, and all three of these are mistakes that
        // were made once and cost a render each to find.
        //
        // The *compiled* text, not the template. Since the element moved into
        // Stage3D.FlameInk - shared with the tank's own flame - the template holds
        // a marker where the ramp used to be, and every one of these read nothing
        // and said so.
        string flame = Stage3D.BlazingCode;
        Check("the flame's elements composite over each other, they do not sum",
            flame.Contains("flame_over(") && !flame.Contains("fire +="),
            "added instead, the dark lip that keeps the licks apart brightens what "
            + "is behind it - additive light cannot darken anything - and twenty-two "
            + "elements melt into one bar; measured, a fifth of the flame pinned "
            + "red at 255 and it read as lava");
        Check("and their falloff is on the facing, not on the radius",
            flame.Contains("pow(facing, rim_falloff)"),
            "engine_fire's alpha goes as Layer Weight's Facing to that power, and "
            + "facing is sqrt(1 - r^2): writing pow(1 - r^2, ...) instead is the "
            + "exponent twice over, so every element comes out half as soft");
        Check("the three widths are ratios of the rise, so one knob is the size",
            flame.Contains("rise * seat_ratio")
            && flame.Contains("rise * sway_ratio")
            && flame.Contains("rise * ember_sway_ratio"),
            "written as absolutes they have to be moved by hand with the rise, and "
            + "a fire scaled in one direction stops being the same fire");

        // No stop in the ramp is near white. engine_fire's rule, and it is the one
        // an editing hand breaks: red saturating is not the failure, green and blue
        // coming up with it is, and a yellow stop anywhere near the mass guarantees
        // it. Read off the stops themselves so a hand-picked colour cannot slip in.
        int ramp = flame.IndexOf("vec4 flame_ramp", System.StringComparison.Ordinal);
        int after = ramp < 0 ? -1 : flame.IndexOf("\nfloat flame_life", ramp,
                                                 System.StringComparison.Ordinal);
        var pale = new System.Collections.Generic.List<string>();
        var sooty = new System.Collections.Generic.List<string>();
        int stops = 0;
        for (int at = ramp; ramp >= 0 && after > ramp;)
        {
            at = flame.IndexOf("vec3(", at + 1, System.StringComparison.Ordinal);
            if (at < 0 || at > after)
                break;
            int shut = flame.IndexOf(')', at);
            string[] bits = flame.Substring(at + 5, shut - at - 5).Split(',');
            if (bits.Length != 3)
                continue;
            stops++;
            float[] c = new float[3];
            for (int j = 0; j < 3; j++)
                c[j] = float.Parse(bits[j].Trim(),
                                   System.Globalization.CultureInfo.InvariantCulture);
            if (c[1] > 0.55f || c[2] > 0.30f || c[1] >= c[0])
                pale.Add($"({c[0]:F2},{c[1]:F2},{c[2]:F2})");
            // And the other way, which is the one a copying hand breaks: these stops
            // are the tank sprite's rendered hue, not engine_fire's linear config,
            // and the config read here is a sixth of the sprite's green. The
            // near-white guard above passes either way - the config is *redder*, not
            // paler - so nothing else would say a word.
            if (c[1] / System.Math.Max(c[0], 1e-6f) < 0.28f
                || c[2] / System.Math.Max(c[0], 1e-6f) < 0.02f)
                sooty.Add($"({c[0]:F2},{c[1]:F2},{c[2]:F2})");
        }

        // The quad has to hold the fire, and the bounds that size it live in C#
        // while the fire's own numbers live in the shader. Two copies of one bound
        // is exactly how a frame starts clipping quietly, so the shader is read back
        // and the bounds are asserted against what it says.
        (float lift, float flank, float seated, float fade) = Stage3D.PyreBounds;
        float seatR = Uniform(flame, "seat_ratio");
        float swayR = Uniform(flame, "sway_ratio");
        float emberR = Uniform(flame, "ember_sway_ratio");
        float reach = Uniform(flame, "ember_reach");
        float spark = Uniform(flame, "ember_size");
        float draw = Uniform(flame, "stretch");
        float spare = Uniform(flame, "vary");
        float born = Uniform(flame, "born_scatter");
        float sparkR = spark * seatR * 1.5f;
        float wantsUp = reach + (sparkR * 1.6f);
        float wantsOut = Mathf.Max(emberR + sparkR,
                                   (seatR * born) + swayR + (seatR * (1.0f + spare)));
        Check("the flame's quad holds the fire the shader draws in it",
            lift >= wantsUp && flank >= wantsOut,
            $"quad {lift:F2}/{flank:F2} against {wantsUp:F2} up, {wantsOut:F2} out "
            + "- the embers drift furthest on both axes, and a quad measured "
            + "against the tree instead printed its own rectangle as soon as a "
            + "taller flame was asked for");
        Check("and it holds the lift as well as the fire standing on it",
            Mathf.Abs(Stage3D.PyreQuad(
                          new PropNode { Size = Vector2.One, Foot = Vector2.One },
                          1.0f).Size.Y - (lift + seated)) < 0.001f,
            $"a quad {lift:F2} tall carrying a fire lifted {seated:F2} is a fire with "
            + "its embers out of the frame - the lift buys the cut off the bottom "
            + "and pays for it at the top");
        // The fade band lives above the contact point, and the seat above the band.
        // Below that point nothing draws at all - the depth a fragment loses is
        // y/sin(e), so "under the ground" and "behind the ground" are one test - so a
        // band spent under the line is a band that was never asked, which is exactly
        // what the flame did while it was reaching 0.40 of a rise down and fading
        // over three quarters of it.
        Check("the flame fades out above the ground line, not below it",
            fade > 0.0f && seated > 0.0f && fade < seated
            && flame.Contains("smoothstep(0.0, max(floor_fade")
            && flame.Contains("* tall - root"),
            $"fade {fade:F2} against a lift of {seated:F2} - a straight cut across a "
            + "fire is the one thing that says rectangle out loud, and no room under "
            + "the foot can fix it because those fragments are behind the cell the "
            + "tree stands on");
        Check("and the quad keeps no room under the foot, because none can be drawn",
            Mathf.Abs(Stage3D.PyreQuad(
                          new PropNode { Size = Vector2.One, Foot = Vector2.One },
                          1.0f).Foot.Y
                      - Stage3D.PyreQuad(
                          new PropNode { Size = Vector2.One, Foot = Vector2.One },
                          1.0f).Size.Y) < 0.001f,
            "rows below the contact point are fill spent on fragments the ground "
            + "buries, and a fade written into them reads as one that works");
        Check("and it is centred on the trunk, not on the art's own foot",
            Mathf.Abs(Stage3D.PyreQuad(
                new PropNode { Size = new Vector2(400.0f, 1000.0f),
                               Foot = new Vector2(90.0f, 1000.0f) },
                Stage3D.FlameRiseDefault).Foot.X * 2.0f
                - Stage3D.PyreQuad(
                    new PropNode { Size = new Vector2(400.0f, 1000.0f),
                                   Foot = new Vector2(90.0f, 1000.0f) },
                    Stage3D.FlameRiseDefault).Size.X) < 0.001f,
            "the shader reads across from the middle of the quad, so a quad built "
            + "around a foot that is not its middle stands the whole flame beside "
            + "its own tree");

        // The shadow hands over along with the crown it is the shadow of, and the
        // thing that used to stop it was two draws rather than the blending: a
        // stencil laid down twice at complementary strength compounds instead of
        // averaging. So the claim is that there is one draw and one alpha.
        string cast = Stage3D.CastShader;
        Check("the shadow hands over from one picture to the other, not on a frame",
            cast.Contains("mix(a, lift(burnt")
            && cast.Contains("uniform float swap"),
            "it showed one picture or the other and changed on the middle frame of "
            + "the handover - which, now that the handover happens under the fire, "
            + "is the one frame the eye is already watching");
        Check("and mixes the coverage in one draw, because two would compound",
            cast.Split("ALPHA").Length == 2
            && Math.Abs((0.5f + 0.5f - 0.5f * 0.5f) - 0.75f) < 1e-6f
            && Math.Abs(Mathf.Lerp(0.5f, 0.5f, 0.5f) - 0.5f) < 1e-6f,
            "two half-covered stencils composite to 0.75 and mix to 0.50 - the "
            + "first is a shadow that darkens as it changes, which is what "
            + "ground_shadow refuses to draw a hull and a turret separately for");
        // One text for the crown and for the mask of the crown. Asserted on both
        // compiled sources for FoamEdge's reason: a copy that drifted would still
        // compile, still draw, and read as a burnt trunk throwing a leafy shadow.
        Check("and lifts its pictures off the quad with the crown's own code",
            Stage3D.CastShader.Replace("LIFT_FN", Stage3D.PictureLift)
                  .Contains(Stage3D.PictureLift)
            && Stage3D.BarkShader.Replace("LIFT_FN", Stage3D.PictureLift)
                      .Contains(Stage3D.PictureLift)
            && Stage3D.PictureLift.Contains("vec4 lift("),
            "a mask that disagrees with the silhouette it is a mask of is two "
            + "answers to one question");
        Check("and puts the crown's own gamma on the burnt alpha",
            cast.Contains("burnt_map, firm")
            && Stage3D.BarkShader.Contains("burnt_map, firm")
            && Stage3D.SeatShader.Contains("uniform float firm"),
            $"Firm is {Stage3D.Firm:F2} on art whose strokes are thinner than a "
            + "screen pixel, so a shadow without it is thinner than the crown "
            + "throwing it");

        // The section across an element: a shift on the same ramp, and a shoulder
        // that is brighter and not only yellower.
        Check("the section across an element is a shift on that same ramp",
            flame.Contains("core_hue * across"),
            "a second set of colours is a second place the flame's colour is "
            + "written, and the one that is not checked for white");
        Check("and its shoulder is brighter, not only yellower",
            flame.Contains("shell_gain") && Uniform(flame, "shell_gain") > 1.0f
            && Uniform(flame, "rim_reach") < 0.5f,
            "hue alone does not read: both the lip and the alpha fall off from the "
            + "middle, so a yellower shoulder still comes out darker and the eye "
            + "sees one dimming body - measured, and the lip has to be pulled in "
            + "close to the rim to leave room for the shoulder");

        Check("and every stop carries the tank sprite's hue, not engine_fire's config",
            stops >= 4 && sooty.Count == 0,
            stops < 4 ? $"only {stops} stops found, so this checked nothing"
                : $"{string.Join(" ", sooty)} - engine_fire's numbers are linear and "
                  + "this shader's ALBEDO lands in an sRGB framebuffer, where the add "
                  + "happens; read there they are a sixth of the sprite's green and "
                  + "none of its blue, and the forest burns blood-red where the tank "
                  + "burns amber");

        Check("and no stop in its ramp is anywhere near white",
            stops >= 4 && pale.Count == 0,
            stops < 4 ? $"only {stops} stops found, so this checked nothing"
                : $"{string.Join(" ", pale)} - the first pass at the tank's fire "
                  + "opened on (1.00, 0.86, 0.48) and the whole flame came out the "
                  + "colour of a lightbulb");

        if (grove?.Props is not null && grove.Props.Any)
        {
            int paired = -1;
            for (int k = 0; k < grove.Props.Count; k++)
                if (grove.Props.HasBurnt(k))
                {
                    paired = k;
                    break;
                }
            if (paired < 0)
            {
                Note("        (no burnt art in this run)");
            }
            else
            {
                var tree = new PropNode
                {
                    Art = grove.Props.ArtOf(paired, false),
                    Foot = grove.Props.FootOf(paired, false),
                    Size = grove.Props.SizeOf(paired),
                    RiseImage = grove.Props.RiseOf(paired),
                    RootImage = grove.Props.RootOf(paired),
                    BurntArt = grove.Props.ArtOf(paired, false, true),
                    BurntFoot = grove.Props.FootOf(paired, false, true),
                    BurntSize = grove.Props.SizeOf(paired, true),
                    Pixels = 1.0f,
                };
                Check("a kind with burnt art is drawn in its living one until it is not",
                    tree.ShownArt == tree.Art && !tree.Charred, "");
                tree.Coat = new Wildfire.Coat(0.0f, 0.0f, 0.5f, true);
                Check("and swaps every part of the picture at once, not just the art",
                    tree.Charred && tree.ShownArt == tree.BurntArt
                    && tree.ShownFoot == tree.BurntFoot
                    && tree.ShownSize == tree.BurntSize,
                    "a foot left behind is a tree that steps sideways as it burns");
                Check("with the char left on, because the picture it belongs to "
                      + "is still fading out",
                    tree.Scorch >= 1.0f, $"scorch {tree.Scorch:F2}");
                tree.Coat = new Wildfire.Coat(1.0f, 0.0f, 0.5f, true, 0.5f);
                Check("halfway through the handover both pictures are on it",
                    tree.Swapping && Mathf.Abs(tree.Swap - 0.5f) < 0.0001f,
                    $"swap {tree.Swap:F2}");

                // The quad both of them are drawn on. Asserted rather than
                // eyeballed because either failure is a crown cut off square:
                // too small and the outgoing one is clipped, mismapped and it
                // is stretched.
                (Vector2 foot, Vector2 size, Godot.Vector4 live,
                 Godot.Vector4 dead) = Stage3D.Union(tree);
                const float slack = 0.001f;
                bool holds = true;
                foreach ((Vector2 f, Vector2 z) in new[]
                         { (tree.Foot, tree.Size), (tree.BurntFoot, tree.BurntSize) })
                    holds &= foot.X >= f.X - slack
                             && size.X - foot.X >= z.X - f.X - slack
                             && foot.Y >= f.Y - slack
                             && foot.Y - size.Y <= f.Y - z.Y + slack;
                Check("the quad a burning tree stands on holds both its pictures",
                    holds,
                    $"{size.X:F0}x{size.Y:F0} against {tree.Size.X:F0}x"
                    + $"{tree.Size.Y:F0} and {tree.BurntSize.X:F0}x"
                    + $"{tree.BurntSize.Y:F0} - neither rectangle contains the "
                    + "other, so it has to be the union of the pair");
                bool sited = true;
                foreach ((Vector2 f, Vector2 z, Godot.Vector4 m) in new[]
                         { (tree.Foot, tree.Size, live),
                           (tree.BurntFoot, tree.BurntSize, dead) })
                {
                    float u0 = (foot.X - f.X) / size.X, v0 = (foot.Y - f.Y) / size.Y;
                    float su = z.X / size.X, sv = z.Y / size.Y;
                    sited &= Mathf.Abs(m.X + u0 * m.Z) < 0.0005f
                             && Mathf.Abs(m.X + (u0 + su) * m.Z - 1.0f) < 0.0005f
                             && Mathf.Abs(m.Y + v0 * m.W) < 0.0005f
                             && Mathf.Abs(m.Y + (v0 + sv) * m.W - 1.0f) < 0.0005f;
                }
                Check("and puts each of them back exactly where it belongs in it",
                    sited,
                    "a map off by a fraction is a picture stretched across the "
                    + "margin the union added");
                var lone = new PropNode
                {
                    Art = tree.Art, Foot = tree.Foot, Size = tree.Size,
                    Pixels = 1.0f,
                };
                (Vector2 alone, Vector2 span, Godot.Vector4 only, _) =
                    Stage3D.Union(lone);
                Check("a kind with nothing to swap to keeps the quad it always had",
                    alone == lone.Foot && span == lone.Size
                    && only == new Godot.Vector4(0.0f, 0.0f, 1.0f, 1.0f),
                    "the union of one rectangle with itself is the identity, and "
                    + "every board rendered before any of this depends on it");
                tree.Coat = new Wildfire.Coat(0.0f, 0.0f, 0.5f, true);
                // The one thing the pair must not do. Measured against the living
                // art's own numbers, which is all the placement was decided on.
                (float rise, float root) = grove.Props.BurntSpanOf(paired);
                Check("the burnt art is no taller than the tree it replaces",
                    rise <= grove.Props.RiseOf(paired) + 1.0f,
                    $"{rise:F0}px against {grove.Props.RiseOf(paired):F0}");
                Check("what the state cannot touch is what the spot was chosen on",
                    tree.Rise == grove.Props.RiseOf(paired)
                    && tree.Root == grove.Props.RootOf(paired),
                    "a prop already standing cannot be given room it was not sown "
                    + "with - and a burnt trunk with ash round it measures a base "
                    + "twice the wood's");
                // Named rather than asserted: the base is the artist's, and a wide
                // one is a note about the art and not a fault in the code.
                if (root > grove.Props.RootOf(paired) * 1.35f)
                    Note($"        (note: {grove.Props.NameOf(paired)}'s burnt "
                             + $"base is {root:F0}px against the wood's "
                             + $"{grove.Props.RootOf(paired):F0} - it is drawn wider "
                             + "than the tree it replaces)");
            }
        }

        // A rock does not burn, and the flag says so rather than the arithmetic.
        Check("a rock does not burn and a tree carries the fire",
            PropTier.Known[PropTier.Trees].Family.Burns
            && PropTier.Known[PropTier.Trees].Carries
            && !PropTier.Known[PropTier.Rocks].Family.Burns
            && !PropTier.Known[PropTier.Rocks].Carries,
            "a boulder charring is the one thing on the board announcing that all "
            + "of this is a tint on a sprite");
        Check("scrub burns where the front is and is not a way across a hexagon",
            PropTier.Known[PropTier.Bushes].Family.Burns
            && !PropTier.Known[PropTier.Bushes].Carries,
            "made one, the fire walks the scatter on every cell of a mixed board");

        PropFamily vegetation = PropFamily.Known[PropFamily.Vegetation];
        PropFamily solid = PropFamily.Known[PropFamily.Solid];

        // --- and which of the two levels answers which question -------------
        //
        // Swaying and burning are the family's, and the point of asserting it
        // this way round rather than tier by tier is that it holds for tiers
        // nobody has written a row for: the two flags no longer have a value of
        // their own to get wrong. A hand that puts them back on PropTier will
        // find the rows still say the right thing and this line still failing.
        Check("what sways and what burns is the family's answer, for every tier",
            PropTier.Known.Values.All(
                t => t.Name.StartsWith(t.Family.Name + "/", StringComparison.Ordinal))
            && vegetation.Sways && vegetation.Burns
            && !solid.Sways && !solid.Burns,
            $"vegetation sways {vegetation.Sways} burns {vegetation.Burns}, "
            + $"solid sways {solid.Sways} burns {solid.Burns}; "
            + string.Join(", ", PropTier.Known.Values.Select(
                t => $"{t.Name} under {t.Family.Name}")));

        // And carrying is not, which is the whole evidence there are two levels
        // rather than one: a tree passes the fire across a hexagon and a bush
        // does not, and both are vegetation. Were this derivable from the
        // family, the tier would be a folder and nothing more.
        Check("and carrying the front is not - two vegetation tiers disagree on it",
            PropTier.Known[PropTier.Trees].Family == PropTier.Known[PropTier.Bushes].Family
            && PropTier.Known[PropTier.Trees].Carries
            && !PropTier.Known[PropTier.Bushes].Carries,
            "if the family could answer this one too, there would be no reason "
            + "for a tier to exist");

        // The refusal, and it is the opposite of what an unknown tier gets: a
        // tier without a row is missing numbers and loads, a family without one
        // is missing behaviour and there is nothing to guess it from. Written
        // as both halves, because a For() that returned some cautious default
        // would pass the first on its own.
        Check("a folder that is no known family is refused rather than guessed at",
            PropFamily.For("rubbish") is null
            && PropFamily.For(PropFamily.Solid) is not null,
            "two bits are one line to write, which is cheaper than any wrong "
            + "guess about them");

        // And an undeclared tier under a known family arrives with the right
        // answer to both for free. This is the failure the restructure was for:
        // the old Unknown() guessed them, and its cautious middle - both true -
        // is exactly backwards for anything solid.
        Check("an undescribed folder under Solid neither sways nor burns",
            !PropTier.Unknown(solid, PropFamily.Solid + "/debris", 9).Family.Sways
            && !PropTier.Unknown(solid, PropFamily.Solid + "/debris", 9).Family.Burns
            && !PropTier.Unknown(solid, PropFamily.Solid + "/debris", 9).Declared
            && !PropTier.Unknown(solid, PropFamily.Solid + "/debris", 9).Carries,
            "and it does not get to carry a fire either, which is the safe "
            + "direction for a tier nobody has described");

        // --- what a fire leaves on something that is not fuel ---------------
        //
        // Both halves, because each on its own is a picture nobody wants. No soot
        // at all is a bright boulder standing in permanently blackened ash, which
        // is the same announcement a charring boulder would be from the other
        // end; as much soot as charcoal is the charring boulder itself.
        Check("a fire marks what it cannot burn, and marks it less deeply",
            vegetation.Soot >= 0.999f
            && solid.Soot > 0.05f && solid.Soot < vegetation.Soot,
            $"vegetation {vegetation.Soot:F2}, solid {solid.Soot:F2} - at zero a "
            + "stone comes out of a burnt cell cleaner than the ground it stands "
            + "on, and at one it comes out charcoal");

        // What "only the soot of it" means, asked of a coat with something in
        // every field: the mark stays and the rest goes. The smoke is the one to
        // watch - the dark column is what a fire pours out, and a boulder
        // emitting one is a boulder alight, which is the reading the whole flag
        // exists to rule out.
        var burning = new Wildfire.Coat(0.7f, 0.9f, 0.6f, false, 0.3f, 0.4f);
        Wildfire.Coat marked = burning.Sooted();
        Check("a non-fuel coat is the mark and nothing else",
            Mathf.Abs(marked.Char - burning.Char) < 0.0001f
            && marked.Flame <= 0.0f && marked.Smoke <= 0.0f
            && !marked.Burnt && marked.Swap <= 0.0f && marked.Spent <= 0.0f,
            $"char {marked.Char:F2} flame {marked.Flame:F2} smoke "
            + $"{marked.Smoke:F2} burnt {marked.Burnt} swap {marked.Swap:F2} "
            + $"spent {marked.Spent:F2}");

        // And the mark on the sprite, which is where the family's number gets
        // used. Both bounds come off the shaders that own them rather than being
        // repeated here, because a mirrored bound is the thing that goes stale:
        // the mark has to leave a surface darker than the burnt ground it stands
        // on - brighter, and the object is the lightest thing in a scorched cell,
        // which is what 0.55 got wrong - and lighter than the charcoal beside it,
        // or marked and charred have stopped being two answers.
        float coal = Uniform(Stage3D.BarkShader, "coal");
        // And the ground has to be using it, not just declaring it: a literal
        // put back into the mix leaves this readable and stale, which is the
        // exact failure reading it was supposed to avoid. Twice - once to
        // declare and at least once to use.
        int usesKeep = Stage3D.SoilShader.Split("ash_keep").Length - 1;
        float ashKeep = Uniform(Stage3D.SoilShader, "ash_keep");
        // Both at the end of it, because the bound is on how black the thing
        // ends up: marked above is a coat part way through, and a stone judged
        // half sooted is a stone judged against a fire that has not finished.
        var settled = new Wildfire.Coat(1.0f, 0.0f, 0.5f, false, 0.0f, 0.0f);
        var stone = new PropNode { Soot = solid.Soot, Coat = settled };
        var trunk = new PropNode { Soot = vegetation.Soot, Coat = settled };
        float left = 1.0f - (1.0f - coal) * stone.Scorch;
        Check("so a stone comes out darker than the ash and lighter than charcoal",
            stone.Scorch > 0.0f && stone.Scorch < trunk.Scorch
            && left < ashKeep && left > coal && usesKeep >= 2,
            $"scorch {stone.Scorch:F2} against the wood's {trunk.Scorch:F2}, "
            + $"which leaves it at {left:F3} of its own luminance - the burnt "
            + $"ground keeps {ashKeep:F2} of its and full char keeps {coal:F2}"
            + (usesKeep >= 2 ? "" : " - and the soil shader names ash_keep "
                                    + $"{usesKeep} time(s), so that bound is "
                                    + "readable and no longer in force"));

        // --- and what a fire leaves of the fuel itself ----------------------
        //
        // The sibling of the carrying check above, and the same evidence for the
        // same conclusion: a trunk leaves a snag and a bush leaves nothing, and
        // both are vegetation. Two tiers of one family disagreeing is the whole
        // reason a tier exists.
        Check("scrub is taken away and a trunk is left standing",
            PropTier.Known[PropTier.Trees].Family
                == PropTier.Known[PropTier.Bushes].Family
            && !PropTier.Known[PropTier.Trees].Consumed
            && PropTier.Known[PropTier.Bushes].Consumed,
            "dry brush in a wood fire is fuel and goes; a trunk chars and stands");

        // Declared and not read off the art, which is the mistake it would be
        // easiest to make: today the bushes have no burnt picture, so "nothing to
        // hand over to" and "is consumed" name the same folders by accident. Both
        // directions, because either alone passes on a version that derived it.
        var drawn = new PropNode
        {
            Consumed = true, BurntArt = new PlaceholderTexture2D(),
            Coat = new Wildfire.Coat(1.0f, 0.5f, 0.5f, false, 0.5f, 0.5f),
        };
        var plain = new PropNode
        {
            Consumed = false,
            Coat = new Wildfire.Coat(1.0f, 0.5f, 0.5f, false, 0.5f, 0.5f),
        };
        Check("being taken away is declared, not read off whether the art exists",
            drawn.Spent > 0.0f && plain.Spent <= 0.0f,
            $"a kind with a burnt picture still goes if its tier says so "
            + $"({drawn.Spent:F2}), and one without still stands if it does not "
            + $"({plain.Spent:F2}) - let the picture answer it and the day "
            + "bush_1_burnt.png lands, every bush on every board stops "
            + "disappearing and nothing says why");

        // How much of it is on screen: the reveal times the burn. Three readers
        // take this, and a reader that took one factor fails visibly either way -
        // the reveal alone leaves a bush's shadow lying on the ground after the
        // bush has gone, and the burn alone puts a solid bush back under a tank.
        var ghosted = new PropNode
        {
            Consumed = true, Modulate = new Color(1.0f, 1.0f, 1.0f, 0.35f),
            Coat = new Wildfire.Coat(1.0f, 0.5f, 0.5f, false, 0.5f, 0.5f),
        };
        Check("what is on screen is the reveal times what the fire has left",
            Mathf.Abs(ghosted.Shown - 0.35f * 0.5f) < 0.0001f
            && ghosted.Shown < ghosted.Modulate.A
            && ghosted.Shown < 1.0f - ghosted.Spent,
            $"shown {ghosted.Shown:F3} against a reveal of "
            + $"{ghosted.Modulate.A:F2} and {1.0f - ghosted.Spent:F2} left of it");

        // The timing, over a real burn, and it is two statements rather than one
        // window: the fuel running out and the fire going out are one event, so
        // there must be no frame with a flame on a prop that has gone and none
        // with the prop still there after its flame has.
        var clip = new Wildfire { Field = field, Wooded = _ => true };
        clip.Light(Vector2I.Zero);
        var scrub = new PropNode { Consumed = true };
        var snag = new PropNode();
        bool hovering = false, lingering = false, early = true;
        bool wentOut = false, scrubGone = false, snagStands = false;
        for (int f = 0; f < 900; f++)
        {
            clip.Tick(1.0 / 60.0);
            Wildfire.Coat coat = clip.Of(Vector2I.Zero, 0.0);
            scrub.Coat = coat;
            snag.Coat = coat;
            if (scrub.Spent >= 1.0f && coat.Flame > 0.0f)
                hovering = true;
            if (coat.Burnt && scrub.Spent < 1.0f)
                lingering = true;
            if (coat.Char > 0.0f && coat.Char < 1.0f && scrub.Spent > 0.0f)
                early = false;
            if (coat.Burnt)
            {
                wentOut = true;
                scrubGone = scrub.Shown <= 0.0f;
                snagStands = snag.Shown >= 0.999f;
            }
        }
        Check("scrub is gone exactly when its own flame is, and no sooner",
            wentOut && !hovering && !lingering && early
            && scrubGone && snagStands,
            $"burnt out {wentOut}, flame over nothing {hovering}, left over "
            + $"after the fire {lingering}, gone before the char finished "
            + $"{!early}, scrub {scrub.Shown:F2} snag {snag.Shown:F2} - a prop "
            + "that vanished early leaves its own flame burning over bare "
            + "ground, because the flame quad hangs on the prop's transform");

        // And all of it end to end on a sown board, which needs a board that has
        // been sown - so it lives in Charring below, called from the one place in
        // this file that already owns the planting. Everything above was asked of
        // a coat held in a hand, and that is why it can be asked here: Burning
        // runs before the grove's own section, and a re-plant this early turns
        // three of that section's checks into checks over an empty list.

        // And the two quads over a burning tree: the smoke has to be the roomier
        // of the two, because it stands over the crown the flame licks through.
        Check("the smoke is given more room than the flame",
            Stage3D.PlumeReach > Stage3D.PyreReach,
            $"{Stage3D.PlumeReach:F2} against {Stage3D.PyreReach:F2}");
    }

    /// <summary>One uniform's default out of a shader's own text. Read back rather
    /// than mirrored in C#, because a mirrored bound is the thing that goes stale.
    /// </summary>
    private static float Uniform(string src, string name)
    {
        int at = src.IndexOf("uniform float " + name + " = ",
                             System.StringComparison.Ordinal);
        if (at < 0)
            return float.NaN;
        at += 14 + name.Length + 3;
        int shut = src.IndexOf(';', at);
        return shut < 0 ? float.NaN
            : float.Parse(src.Substring(at, shut - at).Trim(),
                          System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// What a fire leaves of a board that has actually been sown: the fuel gone
    /// or charred, the stone only marked, and the counts saying so.
    ///
    /// <b>Here because everything else about the burn can be asked of a coat held
    /// in a hand, and this cannot.</b> The tier, the family,
    /// <see cref="Grove.Smoulder"/> and <see cref="Grove.Ablaze"/> are four
    /// separate pieces of wiring, and each of them is a place where the right
    /// answer can be computed and handed to nobody.
    ///
    /// <b>Called from <see cref="Woods"/> rather than from <see cref="Burning"/>,
    /// and the reason is an ordering trap worth knowing.</b> Whoever re-plants the
    /// shared grove replaces the sowing the startup made, and three checks in the
    /// grove's own section read that sowing without planting anything themselves.
    /// Woods runs after them and already re-plants; Burning runs before them, so
    /// the same code there turned three real failures into checks over an empty
    /// list - which is the quietest way a suite can go green.
    /// </summary>
    private static void Charring(HexField field, Grove grove,
                                 Action<string, bool, string> Check)
    {
        Wildfire? wasFire = grove.Fire;
        try
        {
            var whole = new Wildfire { Field = field, Wooded = _ => true };
            for (int q = 0; q < field.Columns; q++)
            for (int r = 0; r < field.Rows; r++)
                whole.Light(new Vector2I(q, r));
            grove.Fire = whole;
            // Long enough that every prop has caught and burnt out, stagger and
            // all: the mark is finished and so is the fuel.
            whole.Tick(whole.BurnFor + whole.CatchWithin + 1.0);
            grove.Smoulder();
            var fuel = grove.Standing.Where(t => t.Tier.Family.Burns).ToList();
            var rock = grove.Standing.Where(t => !t.Tier.Family.Burns).ToList();
            (int _, int ash, int soot) = grove.Ablaze();
            bool eaten = fuel.Where(t => t.Consumed).All(t => t.Shown <= 0.0f);
            bool stands = fuel.Where(t => !t.Consumed)
                              .All(t => t.Shown >= 0.999f && t.Scorch >= 0.999f);
            bool marks = rock.All(t => t.Shown >= 0.999f && t.Scorch > 0.0f
                                       && t.Scorch < 0.999f);
            Check("on a burnt-out board the fuel is gone or charred and the stone "
                  + "is only marked",
                fuel.Count > 0 && rock.Count > 0 && eaten && stands && marks
                && ash == fuel.Count && soot == rock.Count,
                $"{fuel.Count} fuel, {rock.Count} stone: consumed gone {eaten}, "
                + $"husks standing {stands}, stone marked {marks}, counted {ash} "
                + $"burnt and {soot} sooted - a stone counted as burnt is a "
                + "boulder that went up, and one counted as nothing leaves the "
                + "readout unable to say the fire was ever here");
        }
        finally
        {
            grove.Fire = wasFire;
            grove.Smoulder();
        }
    }

    private static void Woods(HexField field, Grove? grove, double elevation,
                              Action<string, bool, string> Check)
    {
        if (grove is null)
        {
            Check("trees sort by where they stand, not by where they are drawn",
                true, "");
            Note("        (no grove in this run)");
            return;
        }

        // Forced, because the default paint has no woods on it at all and a
        // check over an empty list passes by having nothing to say.
        string wasPaint = field.Paint;
        try
        {
            field.Paint = TerrainSet.Forest;
            grove.Plant();

            // What a fire leaves of a sown board, here rather than beside the rest
            // of the fire's checks because it needs a wood on the ground and this
            // method is the one that already forces one.
            Charring(field, grove, Check);

            // Only what still sorts by its foot. The dressing tiers were taken out
            // of the sort on purpose - see PropTier.UnderTanks - so a projection
            // rule asked of them is a rule asked of the wrong thing.
            var trees = grove.Standing.Where(p => !p.Tier.UnderTanks).ToList();
            int levels = 0;
            foreach (PropNode tree in trees)
                if (field.LevelAt(tree.Cell) != 0)
                    levels++;
            if (trees.Count == 0)
            {
                Check("trees sort by where they stand, not by where they are drawn",
                    true, "");
                Note("        (no tree art on disk, so nothing was sown)");
                return;
            }
            Check("the wood reaches ground at more than one level", levels > 0,
                $"{trees.Count} trees and all of them on the datum - it proves nothing");

            double cos = Math.Cos(elevation), sin = Math.Sin(elevation);
            float squash = Math.Max(field.Squash, 0.01f);
            float rise = Math.Max(field.RiseFactor, 0.01f);
            double Along(PropNode tree)
            {
                float lift = field.LevelAt(tree.Cell) * field.Lift;
                return (tree.Ground.Y + lift) / squash * cos + lift / rise * sin;
            }

            int wrong = 0;
            var first = "";
            for (int i = 0; i < trees.Count; i++)
            for (int j = i + 1; j < trees.Count; j++)
            {
                double a = Along(trees[i]), b = Along(trees[j]);
                if (Math.Abs(a - b) < 1.0)
                    continue;   // a pixel apart is a tie, and ties are rounded
                if ((a > b) == (trees[i].ZIndex > trees[j].ZIndex)
                    || trees[i].ZIndex == trees[j].ZIndex)
                    continue;
                wrong++;
                if (first.Length == 0)
                    first = $"({trees[i].Cell.X},{trees[i].Cell.Y}) z {trees[i].ZIndex}"
                            + $" against ({trees[j].Cell.X},{trees[j].Cell.Y}) z "
                            + $"{trees[j].ZIndex}";
            }
            Check($"all {trees.Count} of them sort the way the projection does",
                wrong == 0, $"{wrong} pairs out of order, first {first}");
        }
        finally
        {
            field.Paint = wasPaint;
            grove.Plant();
        }
    }

    /// <summary>
    /// Height on the board: the picking, the occlusion rule and the promise that
    /// a flat board is untouched.
    ///
    /// Run against a relief laid on the live field and taken off again, rather
    /// than behind --relief. The flag decides what a session looks like; whether
    /// the arithmetic is right is not a thing to find out only when somebody
    /// passes it.
    /// </summary>
    /// <summary>
    /// The ramps: the cells that make a level change driveable, and the rules
    /// that make a cliff not.
    ///
    /// <b>What this topic is here to hold is that the surface is continuous.</b>
    /// Everything the ramp buys follows from it - the halved step per leg, the
    /// honest lean, the chance of retiring the depth-test escape a levelling tank
    /// currently gets - and it is the one property that cannot be seen on a
    /// screenshot, because a seam of a fraction of a pixel looks like a seam of
    /// none.
    /// </summary>
    private static void Ramps(HexField field, Stage3D? stage,
                              Action<string, bool, string> Check)
    {
        Theme("ramps: the level changes where the map says it may");
        try
        {
            field.SetRelief(BoardMap.Bench.LevelsFor(field.Columns, field.Rows),
                            BoardMap.Bench.RampsFor(field.Columns, field.Rows));
            Check("the board carries ramps at all", field.HasRamps,
                "no ramp survived the derivation, so nothing below is being tested");

            // The grade IS the ramp's tangent, because a ramp rises one level over
            // one Reach of run. Two places read it that way - the lift and the
            // lean - so it is asserted rather than left as a coincidence of two
            // definitions.
            float world = field.RiseFactor <= 0.0f ? 0.0f
                : field.Lift / field.RiseFactor;
            float tan = field.Reach <= 0.0f ? 0.0f : world / field.Reach;
            Check("one level over one step is the grade, which is the ramp's slope",
                Math.Abs(tan - (float)field.StepGrade) < 1e-4f,
                $"a ramp rises {tan:F4} per run against a grade of "
                + $"{field.StepGrade:F4}");

            // The top of a ramp is one plane, so opposite corners average to the
            // centre - and the centre is TopAt, which is what a parked tank
            // stands on. A top that is not planar is a tile no formula describes.
            int bent = 0, offCentre = 0;
            var firstBent = "";
            for (int q = 0; q < field.Columns; q++)
            for (int r = 0; r < field.Rows; r++)
            {
                var cell = new Vector2I(q, r);
                float[] top = field.TopCorners(cell);
                for (int i = 0; i < 3; i++)
                {
                    float across = top[i] + top[i + 3];
                    if (Math.Abs(across - 2.0f * field.TopAt(cell)) > 0.01f)
                    {
                        bent++;
                        if (firstBent.Length == 0)
                            firstBent = $"({q},{r}) corners {i} and {i + 3} average "
                                        + $"{across * 0.5f:F2} against a centre of "
                                        + $"{field.TopAt(cell):F2}";
                    }
                }
                if (field.IsRamp(cell))
                {
                    int high = HexField.EdgeIndex(field.RampHeading(cell));
                    int low = HexField.EdgeIndex(
                        HexField.Reverse(field.RampHeading(cell)));
                    float want = (field.LevelAt(cell) + 1) * field.Lift;
                    float floor = field.LevelAt(cell) * field.Lift;
                    if (Math.Abs(top[high] - want) > 0.01f
                        || Math.Abs(top[(high + 1) % 6] - want) > 0.01f
                        || Math.Abs(top[low] - floor) > 0.01f
                        || Math.Abs(top[(low + 1) % 6] - floor) > 0.01f)
                        offCentre++;
                }
            }
            Check("a top face is one plane, so its corners straddle its centre",
                bent == 0, $"{bent} do not, first {firstBent}");
            Check("and a ramp's high edge is a level above its low one",
                offCentre == 0, $"{offCentre} ramps do not reach their own levels");

            // The property everything else rests on. The shared edge of any two
            // cells a tank may drive between has to be at one height, or the tank
            // steps.
            int split = 0;
            var firstSplit = "";
            for (int q = 0; q < field.Columns; q++)
            for (int r = 0; r < field.Rows; r++)
            {
                var cell = new Vector2I(q, r);
                float[] here = field.TopCorners(cell);
                foreach (int heading in HexField.EdgeHeadings)
                {
                    Vector2I next = HexField.Step(cell, heading);
                    if (!field.InBounds(next) || !field.Passable(cell, heading))
                        continue;
                    float[] there = field.TopCorners(next);
                    int a = HexField.EdgeIndex(heading);
                    int b = HexField.EdgeIndex(HexField.Reverse(heading));
                    // Opposite winding, so this cell's first corner of the edge is
                    // the neighbour's second.
                    float d1 = Math.Abs(here[a] - there[(b + 1) % 6]);
                    float d2 = Math.Abs(here[(a + 1) % 6] - there[b]);
                    if (Math.Max(d1, d2) > 0.01f)
                    {
                        split++;
                        if (firstSplit.Length == 0)
                            firstSplit = $"({q},{r}) to ({next.X},{next.Y}) opens "
                                         + $"{Math.Max(d1, d2):F2}px";
                    }
                }
            }
            Check("the surface is continuous across every step a tank may take",
                split == 0, $"{split} steps open a seam, first {firstSplit}");

            // And the height of that shared edge is one number whichever cell is
            // asked, which is what lets anything lying in the ground be carried
            // across a boundary without a step - see HexField.SurfaceBetween.
            int edgeSplit = 0, dipped = 0, sunk = 0, over = 0;
            var firstEdge = "";
            float worstDip = 0.0f;
            for (int q = 0; q < field.Columns; q++)
            for (int r = 0; r < field.Rows; r++)
            {
                var cell = new Vector2I(q, r);
                foreach (int heading in HexField.EdgeHeadings)
                {
                    Vector2I next = HexField.Step(cell, heading);
                    if (!field.InBounds(next) || !field.Passable(cell, heading))
                        continue;
                    float mine = field.EdgeTop(cell, next);
                    float theirs = field.EdgeTop(next, cell);
                    if (Math.Abs(mine - theirs) > 0.01f)
                    {
                        edgeSplit++;
                        if (firstEdge.Length == 0)
                            firstEdge = $"({q},{r}) says {mine:F2}, ({next.X},"
                                        + $"{next.Y}) says {theirs:F2}";
                    }
                    // The surface meets the edge at the halfway point and the two
                    // cell centres at the ends: that is the claim, and it is what
                    // the chord cannot make.
                    if (Math.Abs(field.SurfaceBetween(cell, next, 0.5f) - mine) > 0.01f
                        || Math.Abs(field.SurfaceBetween(cell, next, 0.0f)
                                    - field.TopAt(cell)) > 0.01f
                        || Math.Abs(field.SurfaceBetween(cell, next, 1.0f)
                                    - field.TopAt(next)) > 0.01f)
                        dipped++;
                    // How far the chord departs from the ground, both ways. Under
                    // the face driving at a high edge - which is what hid the belt
                    // marks - and over it leaving towards a low one, so the chord
                    // is no use to anything lying in the ground in either
                    // direction.
                    for (int step = 0; step <= 10; step++)
                    {
                        float f = step / 10.0f;
                        float gap = field.SurfaceBetween(cell, next, f)
                                    - field.HeightBetween(cell, next, f);
                        if (Math.Abs(gap) > field.Lift * 0.25f + 0.01f)
                            sunk++;
                        worstDip = Math.Max(worstDip, Math.Abs(gap));
                        if (gap < -0.01f)
                            over++;
                    }
                }
            }
            Check("both cells agree how high the edge between them is",
                edgeSplit == 0, $"{edgeSplit} disagree, first {firstEdge}");
            // And the tank is driven on that ground rather than on the chord of it,
            // which is what the belt marks caught it doing. The claim is asked of
            // the board's own faces - TopAtPoint reads the corner planes where
            // SurfaceBetween reads edge means - so this is two descriptions agreeing
            // and not one restating itself.
            //
            // The failure it exists for is not the vertical error but what that
            // error is on a diagonal leg: a screen row is a place on the ground, so
            // a quarter level of it slides the boundary crossing along the shared
            // edge - measured on (9,2)->(10,3), 21.6px, 52% of the way to the
            // corner. Off the chord the tank drove into the hex through the wrong
            // part of its face. The discriminating count is reported because on a
            // flat leg the chord is the ground, so a board of flat tops would pass
            // this either way.
            int offGround = 0, discriminates = 0;
            float worstOff = 0.0f;
            var firstOff = "";
            for (int q = 0; q < field.Columns; q++)
            for (int r = 0; r < field.Rows; r++)
            {
                var cell = new Vector2I(q, r);
                foreach (int heading in HexField.EdgeHeadings)
                {
                    Vector2I next = HexField.Step(cell, heading);
                    if (!field.InBounds(next) || !field.Passable(cell, heading))
                        continue;
                    for (int step = 0; step <= 8; step++)
                    {
                        float f = step / 8.0f;
                        // Where the drive puts the contact point, and the flat point
                        // it is over - the horizontal part carries no lift at all,
                        // so it is the same both ways.
                        Vector2 at = Footing.GroundBetween(field, cell, next, f);
                        Vector2 flat = field.FlatAnchor(cell.X, cell.Y).Lerp(
                                           field.FlatAnchor(next.X, next.Y), f)
                                       + field.CentreOffset;
                        float laid = flat.Y - at.Y;
                        float want = field.TopAtPoint(flat);
                        float off = Math.Abs(laid - want);
                        if (off > 0.01f)
                        {
                            offGround++;
                            if (firstOff.Length == 0)
                                firstOff = $"({q},{r}) to ({next.X},{next.Y}) at "
                                           + $"{f:F2} stands {laid:F2} on ground "
                                           + $"{want:F2}";
                        }
                        worstOff = Math.Max(worstOff, off);
                        // Would the chord have been caught here? Only where the
                        // ground is tilted, which is the whole of why this was
                        // invisible on a flat board.
                        if (Math.Abs(field.HeightBetween(cell, next, f) - want) > 1.0f)
                            discriminates++;
                    }
                }
            }
            Check("a tank is driven on the ground, not on the chord of it",
                offGround == 0 && discriminates > 0,
                offGround > 0
                    ? $"{offGround} places off by up to {worstOff:F2}px, first "
                      + firstOff
                    : $"clean on every leg, and it would have caught the chord in "
                      + $"{discriminates} places");
            Check("the ground under a step runs centre to edge to centre",
                dipped == 0, $"{dipped} legs do not pass through their own edge");
            // The clearance a flat mark is given on tilted ground, and the two
            // things about it worth holding. It has to beat the residual the
            // chord leaves behind - a quarter level of error times the face's own
            // screen gradient - or a mark laid against it is eaten anyway.
            float residual = field.Reach <= 0.0f || field.Squash <= 0.0f ? 0.0f
                : field.Lift * field.Lift
                  / (4.0f * field.Reach * field.Squash);
            Check("a mark on tilted ground is cleared by more than the chord can "
                  + "cost it",
                field.Lift * HexField.MarkClear > residual && residual > 0.0f,
                $"{field.Lift * HexField.MarkClear:F2}px of clearance against a "
                + $"{residual:F2}px residual");

            // And a park has to owe the same clearance as a leg, because a cell is
            // parked on at the end of every one of them and marks are laid on that
            // frame too. Asked as an identity rather than by driving: the two
            // expressions are what came apart.
            int parkGap = 0;
            for (int q = 0; q < field.Columns; q++)
            for (int r = 0; r < field.Rows; r++)
            {
                var cell = new Vector2I(q, r);
                if (Math.Abs(field.MarkAt(cell)
                             - field.MarkBetween(cell, cell, 0.0f)) > 0.01f)
                    parkGap++;
            }
            Check("standing on a cell owes the same clearance as crossing it",
                parkGap == 0,
                $"{parkGap} cells clear a park differently from a leg - the bars at "
                + "every boundary go when they do");

            Check("and the chord a sprite is drawn on parts from it by a quarter "
                  + "level, both ways",
                sunk == 0 && over > 0
                && Math.Abs(worstDip - field.Lift * 0.25f) < 0.02f,
                $"worst {worstDip:F2}px against a quarter level of "
                + $"{field.Lift * 0.25f:F2}, {sunk} beyond it, {over} samples with "
                + "the chord above - under the face it is hidden by it, over the "
                + "face it is in front of what should hide it, so nothing lying in "
                + "the ground can be laid at it");

            // A leg onto a ramp climbs half a level, which is the whole reason the
            // lean has a ramp case: read off the leg it would be half the slope
            // the tank is drawn sitting on.
            int halves = 0, wrongHalf = 0;
            for (int q = 0; q < field.Columns; q++)
            for (int r = 0; r < field.Rows; r++)
            {
                var cell = new Vector2I(q, r);
                foreach (int heading in HexField.EdgeHeadings)
                {
                    Vector2I next = HexField.Step(cell, heading);
                    if (!field.InBounds(next) || !field.Passable(cell, heading))
                        continue;
                    if (field.IsRamp(cell) == field.IsRamp(next))
                        continue;
                    halves++;
                    if (Math.Abs(Math.Abs(field.TopAt(next) - field.TopAt(cell))
                                 - field.Lift * 0.5f) > 0.01f)
                        wrongHalf++;
                }
            }
            Check("a leg on or off a ramp climbs exactly half a level",
                halves > 0 && wrongHalf == 0,
                halves == 0 ? "no leg touches a ramp at all"
                            : $"{wrongHalf} of {halves} climb something else");

            // What the speed cap is asked - see HexField.IsGrade. Every leg on the
            // board, against the heights themselves, because the cap has to be on
            // for the whole of a climb and off for the whole of a level run: a leg
            // it disagrees with is a tank that crawls across ground it is not
            // climbing or takes a bank at cruise.
            int graded = 0, level = 0, wrongGrade = 0, oneWay = 0, sameLevel = 0;
            for (int q = 0; q < field.Columns; q++)
            for (int r = 0; r < field.Rows; r++)
            {
                var cell = new Vector2I(q, r);
                foreach (int heading in HexField.EdgeHeadings)
                {
                    Vector2I next = HexField.Step(cell, heading);
                    if (!field.InBounds(next) || !field.Passable(cell, heading))
                        continue;
                    bool climbs = Math.Abs(field.TopAt(next) - field.TopAt(cell))
                                  > 0.001f;
                    if (field.IsGrade(cell, next) != climbs)
                        wrongGrade++;
                    // Symmetric, because a descent is capped too and the slip to
                    // guard against is a rule that only sees the climb.
                    if (field.IsGrade(cell, next) != field.IsGrade(next, cell))
                        oneWay++;
                    if (!climbs)
                        level++;
                    else
                    {
                        graded++;
                        // Onto a ramp off the flat: half a lift of climb with no
                        // change of level at all. The case that says the rule reads
                        // height rather than level - ask LevelAt and the first half
                        // of every climb on this board is uncapped.
                        if (field.LevelAt(next) == field.LevelAt(cell))
                            sameLevel++;
                    }
                }
            }
            Check("a leg is a grade exactly when it changes height",
                wrongGrade == 0 && graded > 0 && level > 0,
                $"{wrongGrade} disagree; {graded} graded and {level} level legs");
            Check("and it is one going down as well as up",
                oneWay == 0, $"{oneWay} legs are a grade one way only");
            // The broadside case - one height, tilted ground, not a grade - has no
            // witness on this board and cannot have one: two adjacent ramps are
            // refused by Passable, and a ramp and a flat cell are never at the same
            // height, so the only such leg is impassable by rule. Asserted the way
            // it is available instead: onto a ramp is a grade, and the level says
            // otherwise.
            Check("and a leg onto a ramp is a grade though it changes no level",
                sameLevel > 0,
                "no climb happens within one level, so reading LevelAt instead of "
                + "TopAt would pass here and leave half of every climb uncapped");

            // And what it costs. A fraction of each class's own cruise rather than a
            // speed, so the spread the table was tuned for survives; above the
            // cornering crawl by a wide margin, or the two would fight over the same
            // leg.
            Check("a grade costs each class two thirds of its own cruise",
                MovementProfile.All.All(p =>
                    Math.Abs(p.GradeSpeed - p.TopSpeed * 2.0 / 3.0) < 0.01
                    && p.GradeSpeed > p.CornerSpeed * 3.0
                    && p.GradeSpeed < p.TopSpeed),
                string.Join(", ", MovementProfile.All.Select(
                    p => $"{p.Tag} {p.GradeSpeed:F0} of {p.TopSpeed:F0} px/s, "
                         + $"crawl {p.CornerSpeed:F0}")));

            // Why the cap is slowed into rather than clamped to. Not a preference:
            // the step down is bigger than one frame of braking, so a bare Min would
            // be a stop dead in a frame and would report a frame of full brake to
            // the pitch. If this ever stops being true the branch can go.
            Check("the cap is further below cruise than one frame of braking",
                MovementProfile.All.All(p =>
                    p.TopSpeed - p.GradeSpeed > p.Accel / 60.0 * 2.0),
                string.Join(", ", MovementProfile.All.Select(
                    p => $"{p.Tag} drops {p.TopSpeed - p.GradeSpeed:F0} against "
                         + $"{p.Accel / 60.0:F1} px/s a frame")));

            // The rule, stated against the routes that have to obey it.
            int cliff = 0;
            var firstCliff = "";
            for (int q = 0; q < field.Columns; q++)
            for (int r = 0; r < field.Rows; r++)
            for (int c = 0; c < field.Columns; c++)
            for (int d = 0; d < field.Rows; d++)
            {
                var from = new Vector2I(q, r);
                Vector2I at = from;
                foreach (Vector2I next in field.FindPath(from, new Vector2I(c, d)))
                {
                    if (field.LevelAt(next) != field.LevelAt(at))
                    {
                        int heading = HexField.HeadingTo(at, next);
                        bool onRamp =
                            (field.IsRamp(at) && heading == field.RampHeading(at))
                            || (field.IsRamp(next)
                                && HexField.Reverse(heading)
                                   == field.RampHeading(next));
                        if (!onRamp)
                        {
                            cliff++;
                            if (firstCliff.Length == 0)
                                firstCliff = $"({at.X},{at.Y}) to "
                                             + $"({next.X},{next.Y})";
                        }
                    }
                    at = next;
                }
            }
            Check("no route changes level anywhere but up a ramp", cliff == 0,
                $"{cliff} steps do, first {firstCliff}");

            // And the map has to actually be walkable, or the rule above is held
            // by a board nobody can drive on. The homes are (2,2), (4,2), (6,2).
            var home = new Vector2I(2, 2);
            Check("the plateau is reachable from a home cell",
                field.FindPath(home, new Vector2I(3, 3)).Count > 0,
                "no route up onto (3,3), so its ramp does not connect");
            Check("and so is the far end of the pit",
                field.FindPath(home, new Vector2I(6, 5)).Count > 0,
                "no route down into (6,5), so the pit's mouth does not open");

            // A cliff is refused, and that is what the ramps are for. (3,3) is
            // plateau and (3,1) is the flat two cells above it; the step from
            // (3,1)'s neighbour straight onto the plateau is the one a tank used
            // to be able to take.
            Check("a cliff is not driveable where no ramp bridges it",
                !field.Passable(new Vector2I(4, 3), 210)
                && HexField.Step(new Vector2I(4, 3), 210) == new Vector2I(3, 3),
                "the flat beside the plateau still steps straight up onto it");

            // Onto a ramp across a side edge: the face runs from one level to the
            // other there, so there is no surface to cross. (2,4) and the ramp at
            // (3,4) are both level 0, so only the axis rule can refuse this one -
            // which is what makes it the case worth asserting.
            Check("and a ramp is not entered across a side edge",
                !field.Passable(new Vector2I(2, 4), 330)
                && HexField.Step(new Vector2I(2, 4), 330) == new Vector2I(3, 4)
                && field.LevelAt(new Vector2I(2, 4))
                   == field.LevelAt(new Vector2I(3, 4)),
                "the ramp at (3,4) is entered off its own axis");

            // --- the rosette: a hill with a ramp on each of its six faces ------
            //
            // The reason it is on the board at all. A ramp's top is a corner
            // pattern rotated to its high edge, so a board whose ramps all rise
            // the same way renders one of six rotations and says nothing about the
            // other five. These four assertions are what make the patch a test
            // rather than scenery.
            var hill = new Vector2I(11, 3);
            Check("the rosette is a hill and nothing else is that high beside it",
                field.LevelAt(hill) == 1 && !field.IsRamp(hill),
                $"(11,3) is level {field.LevelAt(hill)}"
                + (field.IsRamp(hill) ? " and marked as a ramp" : ""));

            // Every rotation, and it is the set that matters rather than the count:
            // six ramps that happened to share a heading would count six.
            var faces = new HashSet<int>();
            for (int q = 0; q < field.Columns; q++)
            for (int r = 0; r < field.Rows; r++)
            {
                int h = field.RampHeading(new Vector2I(q, r));
                if (h >= 0)
                    faces.Add(h);
            }
            Check("every one of the six flat faces is somebody's high edge",
                HexField.EdgeHeadings.All(faces.Contains),
                "the board only renders the rotations "
                + string.Join(", ", faces.OrderBy(h => h))
                + ", so the rest of the corner pattern is untested");

            // Each face of the hill carries its own ramp, tilted at the hill, and
            // each is entered from the flat one step further out along the same
            // axis. Both halves are asserted together because a ramp that leans
            // the right way and cannot be driven onto is not an approach.
            int wrongWay = 0, noWayIn = 0, notReached = 0;
            var firstBadFace = "";
            foreach (int heading in HexField.EdgeHeadings)
            {
                Vector2I ramp = HexField.Step(hill, heading);
                // From the hill, the ramp lies at `heading`; from the ramp the hill
                // lies back the other way, and that is the ramp's high edge.
                int high = HexField.Reverse(heading);
                Vector2I gate = HexField.Step(ramp, heading);
                if (field.RampHeading(ramp) != high)
                {
                    wrongWay++;
                    if (firstBadFace.Length == 0)
                        firstBadFace = $"the ramp at {ramp} tilts "
                                       + $"{field.RampHeading(ramp)} rather than {high}";
                }
                else if (!field.Passable(gate, high)
                         || HexField.Step(gate, high) != ramp)
                {
                    noWayIn++;
                    if (firstBadFace.Length == 0)
                        firstBadFace = $"the ramp at {ramp} cannot be entered from "
                                       + $"{gate}";
                }
                else if (!field.Passable(ramp, high))
                {
                    notReached++;
                    if (firstBadFace.Length == 0)
                        firstBadFace = $"the ramp at {ramp} does not reach the hill";
                }
            }
            Check("each of the six is tilted at the hill, entered from the flat, "
                + "and climbs it",
                wrongWay + noWayIn + notReached == 0,
                $"{wrongWay} lean wrong, {noWayIn} have no way in, "
                + $"{notReached} do not arrive: {firstBadFace}");

            // And the hill is reachable from a home cell, which is the whole patch
            // answering at once - the derivation, the six approaches, and the
            // search that has to find one of them.
            List<Vector2I> up = field.FindPath(new Vector2I(2, 2), hill);
            Check("and a tank can drive from its home cell onto the rosette",
                up.Count > 0 && up[^1] == hill
                && field.IsRamp(up[up.Count - 2]),
                up.Count == 0 ? "no route reaches (11,3) at all"
                    : $"the route ends at {up[^1]} arriving from "
                      + $"{up[up.Count - 2]}");

            // The ring cells are all adjacent to each other and none of them may be
            // driven to another, so the way from one face to the next is out and
            // round. Asserted because it is what makes each of the six its own
            // test, and because a rule that quietly stopped refusing would turn the
            // ring into one lap.
            int ringToRing = 0;
            foreach (int heading in HexField.EdgeHeadings)
            {
                Vector2I ramp = HexField.Step(hill, heading);
                foreach (int out2 in HexField.EdgeHeadings)
                    if (field.IsRamp(HexField.Step(ramp, out2))
                        && field.Passable(ramp, out2))
                        ringToRing++;
            }
            Check("but no ramp of the ring may be driven onto its neighbour",
                ringToRing == 0,
                $"{ringToRing} steps go straight from one ramp to another");

            // --- the shadow the ground itself throws --------------------------
            //
            // The ground shader marches a height map toward the sun, so the one
            // thing to establish about that map is that it is the ground.
            // Everything else it could be is a board slightly other than the one
            // being drawn, which shades perfectly convincingly and is wrong at
            // every fragment.
            //
            // Baked here rather than read off whatever the stage happened to
            // have: a --selftest run quits before the stage's first frame, so
            // the map asked for without this is the map of a board that was
            // never built - and "there is no map" is a pass, which makes it a
            // check that cannot fail. Survey is the same call Build makes, with
            // the same argument.
            if (stage is not null)
            {
                stage.Survey();
                int wrong = 0;
                float worst = 0.0f;
                for (int q = 0; q < field.Columns; q++)
                for (int r = 0; r < field.Rows; r++)
                {
                    var cell = new Vector2I(q, r);
                    // The cell's own middle, where a flat top and a ramp's
                    // tilted one both agree with TopAt exactly - away from the
                    // corners, so the texel grid has nothing to argue about.
                    Vector2 onGround = stage.Origin + field.FlatAnchor(cell)
                                       + field.CentreOffset;
                    Vector3 mid = Stage3D.World(onGround,
                                                field.LevelAt(cell) * field.Lift,
                                                field.Squash, field.RiseFactor);
                    float? said = stage.MapHeight(mid.X, mid.Z);
                    if (said is null)
                        continue;
                    float off = Math.Abs(said.Value
                                         - field.TopAt(cell) / field.RiseFactor);
                    worst = Math.Max(worst, off);
                    if (off > 1.5f)
                        wrong++;
                }
                Check("the height map the ground marches is the ground",
                    stage.ShadowReach > 0.0f && wrong == 0,
                    $"{wrong} cells disagree with their own top, worst "
                    + $"{worst:F2} world units, over a reach of "
                    + $"{stage.ShadowReach:F1}");

                // A fixed number of samples spread over exactly this, so a reach
                // short of what the relief can throw is a shadow that stops in
                // the middle of itself - and one that stops short reads as a
                // shorter shadow rather than as a broken one, which is why no
                // picture would report it.
                (int low, int high) = field.LevelRange;
                float relief = (high - low) * field.Lift / field.RiseFactor;
                Check("and it reaches as far as the tallest step can throw",
                    stage.ShadowReach
                        >= relief * Stage3D.SunCast.Length() - 0.01f,
                    $"reach {stage.ShadowReach:F1} against {relief:F1} of relief "
                    + $"at cot {Stage3D.SunCast.Length():F3}");

                // And a board with nothing to cast bakes nothing at all, which
                // is what keeps 24 samples off every fragment of the flat one.
                field.SetRelief(null);
                stage.Survey();
                Check("and a flat board bakes no map and marches nothing",
                    stage.ShadowReach == 0.0f && stage.MapHeight(0.0f, 0.0f) is null,
                    "there is a height map on a board with no relief in it");
            }

            // The fallback, which is what keeps every relief assertion written
            // before ramps existed describing the board it was written for.
            field.SetRelief(BoardMap.Bench.LevelsFor(field.Columns, field.Rows));
            Check("without ramps a board paths by MaxClimb, as it always did",
                !field.HasRamps && field.Passable(new Vector2I(4, 3), 150),
                "a ramp-free board stopped letting a tank climb one level");

            // And the derivation refuses what the map does not decide. (8,4) sits
            // in the pit with two neighbours a level above it, so which edge is
            // its high one is not answerable - proved to fail rather than assumed
            // to, because a guard that cannot fail is not a guard.
            var bad = new bool[field.Columns * field.Rows];
            bad[4 * field.Columns + 8] = true;
            bool refused = false;
            try
            {
                field.SetRelief(BoardMap.Bench.LevelsFor(field.Columns, field.Rows), bad);
            }
            catch (InvalidOperationException)
            {
                refused = true;
            }
            Check("an undecidable ramp is refused rather than guessed",
                refused, "a cell with two higher neighbours was given a direction");
        }
        finally
        {
            field.SetRelief(null);
        }
    }

    /// <summary>
    /// The water: cells a tank drives into rather than round.
    ///
    /// <b>What this topic holds is that the water is a surface and not a
    /// colour.</b> Everything asked for follows from that one thing - the tank
    /// goes under it, the pond has to be flat, a slope cannot carry it, and the
    /// cost of being in it is a speed rather than a refusal - and the half that a
    /// screenshot cannot settle is exactly the half that says so: a pond drawn at
    /// one height per cell looks identical to a pond drawn at one height, until
    /// somebody puts two levels next to each other.
    /// </summary>
    /// <summary>
    /// The pond's own surface, and every word of it asserted with no board, no art
    /// and no frame drawn - which is the whole reason the field is stepped on the
    /// CPU rather than in a ping-pong of viewports. See Ripples.
    /// </summary>
    /// <summary>
    /// A tank meeting a wall: the board it happens on, the lane the round takes
    /// to get there, and the one turn that carries a direction from the board
    /// into the prop's own frame.
    ///
    /// <b>Every word of it without a frame drawn</b>, which is what decides what
    /// is here and what is not. Grid arithmetic, the walk a shell takes and the
    /// board-to-prop turn are all answerable on a probe field; how a wall
    /// actually comes apart is a live solver stepping physics, and
    /// <c>--selftest</c> quits before the first frame of a scene. So the three
    /// strikes answering differently is measured by running the bench and reading
    /// the numbers it prints - named here rather than half-asserted, because a
    /// check that cannot run is worse than one that is missing.
    /// </summary>
    private static void Walls(HexField field, IReadOnlyList<Vehicle>? vehicles,
                              Action<string, bool, string> Check)
    {
        Theme("the wall a tank drives into");

        BoardMap map = BoardMap.WallMap;
        IReadOnlyList<Vector2I> walled = map.Walled();
        // The ring is the first home's own cell: the board opens with the tank
        // inside the walls, which is what makes the ram six answers rather than a
        // run-up. Both halves, because each is a different board: a home that is
        // not walled is a tank standing in the open, and a walled cell that is not
        // a home is a yard with nothing in it.
        Vector2I home = map.Homes[0];
        Check("the tank starts inside a wall, and the samples stand elsewhere",
            walled.Count > 1 && walled.Contains(home),
            $"{walled.Count} walls, home ({home.X},{home.Y}) "
            + (walled.Contains(home) ? "is walled" : "is not"));
        if (walled.Count == 0)
            return;
        // And that ring runs every side of it. A ring with a gap leaves the
        // machine standing in an opening nobody chose, and no readout names which
        // side it was - see TankBench.RingSides.
        Check("and the wall it starts in runs all six sides of the cell",
            TankBench.RingSides == 6, $"{TankBench.RingSides} sides");
        Vector2I wall = home;

        // --- the board -------------------------------------------------------
        //
        // With ramps on the board Passable reduces to "the two cells stand at the
        // same level", so a wall's cell standing above its neighbours cannot be
        // driven on to at all - and the ram is the whole subject. Nothing on
        // screen says so: the tank simply refuses the order and the picture is a
        // tank parked beside a wall.
        var steps = new List<string>();
        foreach (Vector2I at in walled)
        foreach (int heading in HexField.EdgeHeadings)
        {
            Vector2I next = HexField.Step(at, heading);
            // Off the board is a failure for the ring and not for a sample: the
            // ring is rammed out of on any of the six, so every one of them has to
            // be somewhere a tank can be, whereas a sample is only ever met down
            // the one lane that leads to it - and each of them stands on the rim
            // of the hexagon, where three of the six are off it by construction.
            if (!map.OnBoard(next))
            {
                if (at == wall)
                    steps.Add($"the ring's {heading} is off the board");
                continue;
            }
            if (map.Levels[map.At(next)] != map.Levels[map.At(at)])
                steps.Add($"({at.X},{at.Y}) {heading} stands at "
                          + $"{map.Levels[map.At(next)]}");
        }
        Check("every walled cell is level with the neighbours it has, and the "
              + "ring has all six",
            steps.Count == 0, string.Join(", ", steps));

        // Which side the wall stands on is a dial, so each of the six has to
        // carry a run-up and a lane to shoot down. Three cells because the third
        // is where the braking curve becomes visible - two is the minimum that
        // works at all.
        int LaneRun(int heading)
        {
            int run = 0;
            Vector2I at = wall;
            while (true)
            {
                Vector2I next = HexField.Step(at, heading);
                if (next == at || !map.OnBoard(next))
                    return run;
                at = next;
                run++;
            }
        }
        var lanes = new List<string>();
        foreach (int heading in HexField.EdgeHeadings)
            if (LaneRun(heading) < 3)
                lanes.Add($"{heading} runs {LaneRun(heading)}");
        Check("and every one of its six lanes is three cells long",
            lanes.Count == 0, string.Join(", ", lanes));

        // A sample the tank cannot fire at is a board where the shot has to be
        // set up before it can be taken. Every sample stands at the end of a lane
        // out of the ring on purpose - so driving out of it and turning is not
        // part of taking the shot - and the one found here is what the rest of
        // this topic fires at.
        Vector2I mark = home;
        int laneHeading = -1;
        var offLane = new List<string>();
        foreach (Vector2I sample in walled)
        {
            if (sample == home)
                continue;
            int found = -1;
            foreach (int heading in HexField.EdgeHeadings)
            {
                Vector2I at = home;
                for (int k = 0; k < map.Columns + map.Rows; k++)
                {
                    Vector2I next = HexField.Step(at, heading);
                    if (next == at || !map.OnBoard(next))
                        break;
                    at = next;
                    if (at == sample)
                        found = heading;
                }
            }
            if (found < 0)
                offLane.Add($"({sample.X},{sample.Y})");
            else if (laneHeading < 0)
            {
                laneHeading = found;
                mark = sample;
            }
        }
        Check("every sample stands down a lane out of the ring",
            laneHeading >= 0 && offLane.Count == 0,
            offLane.Count > 0 ? string.Join(" ", offLane) + " are on no lane"
                              : "no sample is on one");
        wall = mark;

        // --- the ram tour ----------------------------------------------------
        //
        // How the masonry answers a ram is measured by driving and printing
        // numbers - a live solver steps the physics and this leaves before the
        // first frame of a scene. What is assertable here is the plan: which
        // walls, in which order, along which heading. Every one of those fails
        // silently - a leg the tank refuses is a tank parked in a yard, and a
        // wall left off the list is a wall nobody drove at.
        List<TankBench.Leg> tour = TankBench.TourLegs(home, TankBench.RingSides);
        List<TankBench.Leg> ringLegs =
            tour.Where(l => l.Wall == home).ToList();
        Check("the ram tour drives out of the ring along every one of its "
              + "sides, each of them once",
            ringLegs.Count == TankBench.RingSides
            && ringLegs.Select(l => l.Side).Distinct().Count()
               == TankBench.RingSides,
            $"{ringLegs.Count} legs on the ring, "
            + $"{ringLegs.Select(l => l.Side).Distinct().Count()} distinct");

        // Every sample too, and exactly once. A walled cell no leg names is a
        // wall that stands through the whole tour and is never reported on,
        // which is the one failure a per-leaf report can have: it looks like a
        // wall the ram did not reach. And a sample whose bearing is not a flat
        // side of the hex is dropped by TourLegs rather than rounded to the
        // nearest - so this is the check that says the dropping never happens.
        List<Vector2I> samples = walled.Where(c => c != home).ToList();
        var missed = samples.Where(c => tour.Count(l => l.Wall == c) != 1)
                            .Select(c => $"({c.X},{c.Y})").ToList();
        Check("and every other wall on the board is on it exactly once",
            missed.Count == 0 && tour.Count == TankBench.RingSides
                                              + samples.Count,
            missed.Count > 0 ? string.Join(" ", missed) + " named "
                               + $"{missed.Count} times"
                             : $"{tour.Count} legs for {samples.Count} samples");

        // The ring before any sample, and not because it reads better: a run-up
        // to a sample is a ram the whole way - see TankBench.RamAt - so it
        // sweeps whatever it drives past, and what it drives past on the way out
        // of the yard is the ring. Taken the other way round, every sample leg
        // would be measuring two collapses at once.
        Check("and it takes the ring before any sample, because a run-up sweeps "
              + "what it drives past",
            samples.Count == 0
            || tour.FindLastIndex(l => l.Wall == home)
               < tour.FindIndex(l => l.Wall != home),
            $"ring last at {tour.FindLastIndex(l => l.Wall == home)}, "
            + $"first sample at {tour.FindIndex(l => l.Wall != home)}");

        // And each leg has a cell to come at its wall from. A run-up cell off
        // the board is an order the tank simply refuses, and nothing on screen
        // says so - the leg reports "no wall" or nothing at all, and the tour
        // walks on.
        var noRun = tour.Where(
            l => !map.OnBoard(HexField.Step(l.Wall,
                                            HexField.EdgeHeadings[l.Side])))
            .Select(l => $"({l.Wall.X},{l.Wall.Y})@"
                         + HexField.EdgeHeadings[l.Side]).ToList();
        Check("and every leg has a cell to come at its wall from",
            noRun.Count == 0, string.Join(" ", noRun));

        // What the tour cannot promise, said as arithmetic rather than left to
        // the run: driving out of a yard the hull does not fit in clips the
        // mitred corner of the leaf next door, and on the three-sided sample
        // that graze runs to 11 pieces of 78 - past the share. Asked per piece,
        // that is a leaf coming down that the tank never entered; the ram names
        // its section instead, which is why the measured tour reports no
        // unnamed section at all.
        Check("and a corner graze can cross the share, which is why the ram "
              + "names its section rather than counting pieces",
            WallRig.Breached(11, 78) && !WallRig.Breached(11, 96),
            $"11 of 78 {WallRig.Breached(11, 78)}, "
            + $"11 of 96 {WallRig.Breached(11, 96)}");

        // --- the lane a round takes ------------------------------------------
        //
        // Both halves, and the second is the point: a set nothing reads passes
        // the first on its own, and a shell that goes through a wall is exactly
        // what that looks like.
        if (field.Atlas is null)
        {
            Note("  skip  no atlas loaded, so the board has no distance");
        }
        else if (laneHeading >= 0)
        {
            var probe = new HexField
            {
                Atlas = field.Atlas,
                Columns = map.Columns, Rows = map.Rows, Plot = map.Plot,
            };
            try
            {
                probe.SetRelief(map.Levels, map.Ramps);
                Vector2 from = probe.FlatAnchor(home) + probe.CentreOffset;
                // Towards the sample, and not reversed: laneHeading is the way out
                // of the ring, which is the way the round goes. It used to be the
                // heading from the wall back to the home, which is the same lane
                // read the other way - and reading it the old way now walks the
                // round off the far edge of the board, where it stops at nothing
                // and the check passes for the wrong reason.
                Vector2 dir = field.Atlas.GroundDirection(laneHeading);
                float top = probe.LevelAt(home) * probe.Lift;
                var none = new HashSet<Vector2I>();
                var solid = new HashSet<Vector2I> { wall };
                (float openRun, Vector2I? openAt, bool openStop) =
                    TankTick.Track(probe, from, dir, top, none);
                (float shutRun, Vector2I? shutAt, bool shutStop) =
                    TankTick.Track(probe, from, dir, top, solid);
                Check("a line down the lane runs past the wall's cell when "
                      + "nothing stands there",
                    !openStop && openAt is null,
                    $"stopped at {openAt} after {openRun:F0}px");
                Check("and stops at it when the wall does",
                    shutStop && shutAt == wall,
                    $"stopped at {shutAt} after {shutRun:F0}px");
                Check("and stops short of the cell that stopped it, which is why "
                      + "a round carries what blocked it",
                    shutStop
                    && probe.FlatCellAt(from + dir.Normalized() * shutRun) != wall,
                    "the landing point is inside the blocking cell, so asking it "
                    + "which cell it is in would have answered");
                // And the cell it fires from, which is the one cell a set of
                // cells cannot speak for: a wall stands on the rim of its own
                // hex, so a tank inside a ring of it was firing straight through
                // its own masonry. Both halves, because the default is what every
                // other board on this project answers and the other is the whole
                // feature.
                (float rimRun, Vector2I? rimAt, bool rimStop) =
                    TankTick.Track(probe, from, dir, top, none, rim: true);
                Check("a line crosses the cell it fires from when nothing on it "
                      + "bars the way out",
                    !openStop && openAt is null,
                    $"stopped at {openAt} after {openRun:F0}px with no rim");
                Check("and stops on that cell when something does - the one hex a "
                      + "set of cells cannot answer for, because a wall stands on "
                      + "its rim rather than in it",
                    rimStop && rimAt == home && rimRun > 0.0f,
                    $"stopped at {rimAt} after {rimRun:F0}px");

                // The wiring, which is the half that rots: Track is handed a set
                // and Reach is what builds one. A tank is wanted for that, and a
                // run may have none.
                if (vehicles is { Count: > 0 })
                {
                    Vehicle shooter = vehicles[0];
                    Vector2 was = shooter.Sprite.Position;
                    float stood = shooter.Standing, ground = shooter.Ground;
                    var tick = new TankTick
                    {
                        Field = probe,
                        Origin = Vector2.Zero,
                        Vehicles = new[] { shooter },
                        Obstacles = solid,
                    };
                    shooter.Sprite.Position =
                        from - shooter.Atlas.GroundOffset * shooter.Sprite.BodyScale;
                    shooter.Standing = top;
                    shooter.Ground = top;
                    (_, Vector2I? at, bool blocked) = tick.Reach(shooter, dir);
                    tick.Obstacles = none;
                    (_, Vector2I? bare, bool _) = tick.Reach(shooter, dir);
                    shooter.Sprite.Position = was;
                    shooter.Standing = stood;
                    shooter.Ground = ground;
                    Check("and Reach hands that set to the walk, so the aiming "
                          + "ray and the round stop at the same wall",
                        blocked && at == wall && bare is null,
                        $"with the wall {at}, without it {bare}");
                }
            }
            finally
            {
                probe.Free();
            }
        }

        // --- the one turn between the board and the prop ---------------------
        //
        // The layout is built standing on its own +z side, so however the wall is
        // stood on the board, a shot from the side it stands on has to arrive
        // straight down the prop's own axis. Written as +Lay it is a shot from
        // the wrong side the moment the wall leaves its opening bearing - the
        // failure WallBench carries the history of, and the one a second copy of
        // that turn would bring back.
        if (field.Atlas is not null)
        {
            var stage = new Stage3D { Field = field, Origin = Vector2.Zero };
            var prop = new WallProp
            {
                Field = field, Stage = stage, Cell = Vector2I.Zero,
            };
            try
            {
                var axis = new Vector3(0.0f, 0.0f, -1.0f);
                // <b>Not to the bit, and the slack is named.</b> The two
                // conversions the turn is composed of read the camera off two
                // different places - AtlasSet.GroundDirection squashes by
                // sin(the atlas Elevation) and Stage3D.World divides by the
                // field own squash - so a round arrives about a third of a
                // degree off the axis. That is the board as it stands rather
                // than anything this turn does, and a quarter turn is 1.41.
                const float slack = 0.02f;
                float spin = Mathf.DegToRad(37.0f);
                var turned = new Basis(Vector3.Up, -spin) * axis;
                float worstSide = 0.0f, worstSpin = 0.0f;
                foreach (int bearing in HexField.EdgeHeadings)
                {
                    prop.Spin = 0.0f;
                    prop.Bearing = bearing;
                    worstSide = Mathf.Max(worstSide,
                                          prop.Arriving(bearing).DistanceTo(axis));
                    // And the turntable moves the arrival by exactly the
                    // turntable: it turns the prop under a fixed board, so the
                    // face the round meets turns with it and by nothing else. A
                    // sign wrong here is a shot from the far side once the wall
                    // is turned, which a bench opening at zero never shows.
                    prop.Spin = spin;
                    worstSpin = Mathf.Max(
                        worstSpin, prop.Arriving(bearing).DistanceTo(turned));
                }
                Check("a shot from the side the wall stands on arrives down the "
                      + "prop's own axis, from all six",
                    worstSide < slack, $"worst {worstSide:F4} off");
                Check("and the turntable moves that arrival by exactly the "
                      + "turntable",
                    worstSpin < slack, $"worst {worstSpin:F4} off");
            }
            finally
            {
                prop.Free();
                stage.Free();
            }
        }

        // --- one tank, one box -----------------------------------------------
        //
        // The rig's own box is a stand-in for a tank on a board that has none. A
        // board with one that also let the rig make its own would draw two tanks
        // in one place, both solid - and the ram would be felt twice.
        var recipe = new WallKit.Recipe();
        WallKit.Plan plan = WallKit.Lay(recipe);
        WallKit.Fit(plan, 0.97f);
        var rig = new WallRig();
        try
        {
            rig.Raise(plan, Array.Empty<Vector3>());
            // A wall nobody is driving at has no ram box, and the overlay is
            // told so rather than left to draw one at the origin - which is
            // where a six-metre box would sit on a board measured in pixels.
            Check("a rig with no tank has no box to draw",
                rig.Hull() is null, "a frame came back for a box that is not there");
            // Standing masonry does not clear itself, which is the whole of which
            // pieces go: what the shot let go of. A wall left standing round an AP
            // hole has to be there however long anybody looks at it, and the
            // failure is silent in the worst direction - a wall that quietly
            // sweeps itself away with nothing having hit it.
            bool whole = rig.Crumbled == 0;
            for (int i = 0; i < rig.Count; i++)
                whole &= rig.Left(i) >= 1.0f;
            Check("a wall nothing has hit is whole, and stays whole: only what a "
                  + "shot let go of clears itself",
                whole, $"{rig.Crumbled} of {rig.Count} pieces gone unhit");
            // --- the driven ram, arrived as an event --------------------------
            //
            // See WallRig.Rammed: the board says a hull's front face crossed a
            // wall-bearing rim, and the rig answers once. What is assertable
            // without a frame is the naming: a crossing with standing masonry
            // in front enters a section and strikes the wall, one anywhere
            // else enters nothing - and a single-sided wall has exactly one
            // rim that answers.
            var hull = new Vector3(2.5f, WallRig.TankTall, 6.0f);
            int met = 0, face = -1;
            for (int d = 0; d < 12; d++)
            {
                float a = Mathf.DegToRad(d * 30.0f);
                int hit = rig.Rammed(
                    new Vector3(0.0f, WallRig.TankTall * 0.5f, 0.0f),
                    new Vector3(Mathf.Cos(a), 0.0f, Mathf.Sin(a)), 0.0f, hull);
                if (hit < 0)
                    continue;
                met++;
                face = hit;
            }
            Check("a hull crossing the wall's plane enters the one section a "
                  + "single-sided wall has, from the one direction that meets "
                  + "masonry",
                met == 1 && face == 0 && rig.Struck && rig.Loose > 0,
                $"{met} of 12 directions entered, face {face}, "
                + $"struck {rig.Struck}, {rig.Loose} let go");
            // A crossing over a rim the last ram emptied is a drive, not a
            // strike: the section is asked of standing bricks, and none stand.
            int loose = rig.Loose;
            bool again = false;
            for (int d = 0; d < 12; d++)
            {
                float a = Mathf.DegToRad(d * 30.0f);
                again |= rig.Rammed(
                    new Vector3(0.0f, WallRig.TankTall * 0.5f, 0.0f),
                    new Vector3(Mathf.Cos(a), 0.0f, Mathf.Sin(a)), 0.0f,
                    hull) >= 0;
            }
            Check("and a second crossing over the emptied rim enters nothing "
                  + "and lets nothing more go",
                !again && rig.Loose == loose,
                $"entered {again}, {rig.Loose} let go against {loose}");
            // The bench's own ram is still a body, and it is live from the
            // start: that one is a shot rather than a tank, so there is no
            // nose to advance.
            rig.Fire(WallRig.Strike.Ram, Vector3.Forward);
            Check("the bench's own ram box has a frame, and its shape is on "
                  + "the moment it is made",
                rig.Ram() is not null && rig.Hull() is { Live: true },
                $"{(rig.Hull() is null ? "no frame" : "not live")}");
        }
        finally
        {
            rig.Free();
        }

        // --- driving back in over an emptied rim -----------------------------
        //
        // The single-sided wall above cannot ask this: once its one leaf is
        // down there is no masonry left to misname. On a ring there is - the
        // opposite leaf, two apothems out - and Meets scans as far as it can
        // see, so a hull re-entering over the rim its own ram had emptied was
        // answered with that leaf and Rammed felled it from across the cell.
        // The strike belongs to the plane that was crossed: an emptied plane
        // is a drive, and the far leaf answers at its own rim when the hull
        // actually gets there.
        var yard = new WallRig();
        try
        {
            WallKit.Plan walls = WallKit.Lay(new WallKit.Recipe { Sides = 6 });
            WallKit.Fit(walls, 0.97f);
            yard.Raise(walls, Array.Empty<Vector3>());
            var hull = new Vector3(2.5f, WallRig.TankTall, 6.0f);
            var outward = new Vector3(0.0f, 0.0f, 1.0f);
            int left = yard.Rammed(
                new Vector3(0.0f, WallRig.TankTall * 0.5f, 0.0f),
                outward, 0.0f, hull);
            int loose = yard.Loose;
            Check("a hull ramming out of a ring enters the leaf it crossed",
                left >= 0 && loose > 0,
                $"face {left}, {loose} let go");
            // Back in over the same rim: the nose just inside the plane it
            // emptied, the rest of the cell - and the far leaf - ahead of it.
            int back = yard.Rammed(
                new Vector3(0.0f, WallRig.TankTall * 0.5f,
                            WallRig.Apothem - 0.1f + hull.Z * 0.5f),
                -outward, 0.0f, hull);
            Check("and driving back in over the rim it emptied is a drive, "
                  + "not a strike on the leaf across the cell",
                back < 0 && yard.Loose == loose,
                $"face {back}, {yard.Loose} let go against {loose}");
            // The far leaf still answers, at its own rim: the crossing where
            // the hull actually reaches it.
            int far = yard.Rammed(
                new Vector3(0.0f, WallRig.TankTall * 0.5f,
                            -WallRig.Apothem - 0.1f + hull.Z * 0.5f),
                -outward, 0.0f, hull);
            Check("while the leaf across the cell still falls to the crossing "
                  + "at its own rim",
                far >= 0 && far != left && yard.Loose > loose,
                $"face {far} against {left}, {yard.Loose} let go against {loose}");
        }
        finally
        {
            yard.Free();
        }

        // --- the driven box ---------------------------------------------------
        //
        // The ram as a body again, bounded by intent: mounted, a crossing only
        // names the section and the release follows the box's own forward
        // progress - a pivot and a stand-still release nothing. What is
        // assertable without stepping the solver is exactly that split, plus
        // the birth rule: a box born in rubble excepts what it stood in.
        var driven = new WallRig();
        try
        {
            WallKit.Plan lone = WallKit.Lay(new WallKit.Recipe());
            WallKit.Fit(lone, 0.97f);
            driven.Raise(lone, Array.Empty<Vector3>());
            var hull = new Vector3(2.5f, WallRig.TankTall, 6.0f);
            int named = -1;
            var way = Vector3.Zero;
            for (int d = 0; d < 12 && named < 0; d++)
            {
                float a = Mathf.DegToRad(d * 30.0f);
                var dir = new Vector3(Mathf.Cos(a), 0.0f, Mathf.Sin(a));
                driven.Mount(Vector3.Zero, dir, hull);
                if (driven.Rammed(new Vector3(0.0f, WallRig.TankTall * 0.5f,
                                              0.0f), dir, 0.0f, hull) >= 0)
                {
                    named = 1;
                    way = dir;
                }
            }
            Check("with the box mounted, a crossing names the section and "
                  + "releases nothing",
                named >= 0 && driven.Mounted && driven.Loose == 0
                && !driven.Struck,
                $"named {named}, mounted {driven.Mounted}, "
                + $"{driven.Loose} let go, struck {driven.Struck}");
            // A pivot is a pose with no forward progress, and it releases
            // nothing however far the nose swings.
            driven.Drive(Vector3.Zero,
                         new Basis(Vector3.Up, Mathf.DegToRad(90.0f)) * way,
                         0.0f);
            driven.Drive(Vector3.Zero, way, 0.0f);
            Check("a pivot in place swings the box and releases nothing",
                driven.Loose == 0 && !driven.Struck,
                $"{driven.Loose} let go, struck {driven.Struck}");
            for (int s = 1; s <= 40; s++)
                driven.Drive(way * (WallRig.Apothem * 1.2f * s / 40.0f), way,
                             0.0f);
            Check("and the box's own advance releases the named section, "
                  + "starting the clock on the first piece",
                driven.Loose > 0 && driven.Struck,
                $"{driven.Loose} let go, struck {driven.Struck}");
            int loose = driven.Loose;
            driven.Dismount();
            Check("dismounted, the rig carries no box and keeps what fell",
                !driven.Mounted && driven.Hull() is null
                && driven.Loose == loose,
                $"mounted {driven.Mounted}, "
                + $"{driven.Loose} let go against {loose}");
            // Born again in the rubble it just made: everything the box
            // overlaps at birth is excepted, so the solver is never asked to
            // separate a hull from the heap it starts in.
            driven.Mount(way * WallRig.Apothem, way, hull);
            int spared = 0;
            if (driven.Hull() is not null)
                foreach (Node child in driven.GetChildren())
                    if (child is AnimatableBody3D box)
                        spared = box.GetCollisionExceptions().Count;
            Check("a box born in rubble excepts what it stood in",
                spared > 0, $"{spared} exceptions on a box in a fallen wall");
            // And the ghosts heal. An exception is overlap insurance, and
            // overlap ends: once the hull has driven clear of a spared
            // piece, the shepherd removes the pair and the body owns the
            // brick again - which is what lets a returning hull push the
            // heap its own ram once ghosted through.
            for (int s = 1; s <= 30; s++)
                driven.Drive(
                    way * (WallRig.Apothem * (1.0f + 1.5f * s / 30.0f)),
                    way, 0.0f);
            int healed = 0;
            foreach (Node child in driven.GetChildren())
                if (child is AnimatableBody3D box)
                    healed = box.GetCollisionExceptions().Count;
            Check("and the ghosts heal behind the hull: what the box was "
                  + "born inside collides again once it has driven clear",
                healed < spared,
                $"{spared} exceptions at birth, {healed} after driving clear");
            // The naming grant: without one (and without a crossing) the box
            // is a pose however far it drives; granted the crossing, it names
            // the leaf for itself at the face - which is what lets the prow
            // meet a leaf standing 0.4m before the rim the cell-flip naming
            // arrived too late for.
            var granted = new WallRig();
            try
            {
                WallKit.Plan second = WallKit.Lay(new WallKit.Recipe());
                WallKit.Fit(second, 0.97f);
                granted.Raise(second, Array.Empty<Vector3>());
                granted.Mount(Vector3.Zero, way, hull);
                for (int s = 1; s <= 40; s++)
                    granted.Drive(way * (WallRig.Apothem * 1.2f * s / 40.0f),
                                  way, 0.0f);
                Check("without a grant or a crossing, the box names nothing "
                      + "however far it drives",
                    granted.Loose == 0 && !granted.Struck,
                    $"{granted.Loose} let go, struck {granted.Struck}");
                granted.Dismount();
                granted.Mount(Vector3.Zero, way, hull);
                for (int s = 1; s <= 40; s++)
                    granted.Drive(way * (WallRig.Apothem * 1.2f * s / 40.0f),
                                  way, 0.0f, way * 100.0f);
                Check("granted the crossing, it names the leaf at its face "
                      + "and ploughs it for itself",
                    granted.Loose > 0 && granted.Struck,
                    $"{granted.Loose} let go, struck {granted.Struck}");
                // The box outlives the order now, so the order's end has to
                // take the naming with it: an armed box on a plain drive
                // would keep releasing and finish a wall the ram only
                // entered.
                int ploughed = granted.Loose;
                granted.Disarm();
                for (int s = 1; s <= 20; s++)
                    granted.Drive(
                        way * (WallRig.Apothem * (1.2f + 0.6f * s / 20.0f)),
                        way, 0.0f);
                Check("and disarmed, the box keeps its body but forgets its "
                      + "section: further advance releases nothing more",
                    granted.Loose == ploughed && granted.Mounted,
                    $"{granted.Loose} let go against {ploughed}, "
                    + $"mounted {granted.Mounted}");
            }
            finally
            {
                granted.Free();
            }
        }
        finally
        {
            driven.Free();
        }

        // --- the collider overlay --------------------------------------------
        //
        // Two claims, and both of them are about the picture being readable
        // rather than about the picture itself.
        Check("the colliders are off by default: nearly every number on these "
              + "benches is a pixel difference between two captures, and a frame "
              + "drawn over one would be measuring itself",
            !WallHulls.ShownByDefault, "on by default");
        // Four states, one overlay. Two of them painted alike is an overlay that
        // draws the shapes and answers none of the questions they were drawn for
        // - which side of the wall has been let go of, and whether the box the
        // tank drives is in the simulation yet.
        Color[] inks =
        {
            WallHulls.Standing, WallHulls.Loose, WallHulls.Ram, WallHulls.Idle,
        };
        float nearest = 9.0f;
        for (int i = 0; i < inks.Length; i++)
            for (int k = i + 1; k < inks.Length; k++)
                nearest = Mathf.Min(nearest,
                    new Vector3(inks[i].R - inks[k].R,
                                inks[i].G - inks[k].G,
                                inks[i].B - inks[k].B).Length());
        Check("and its four states are four colours: standing masonry, masonry "
              + "let go of, the ram box, and the ram box the solver cannot see "
              + "yet",
            nearest > 0.3f, $"the closest pair is {nearest:F2} apart");

        // --- the rubble clears itself ----------------------------------------
        //
        // The schedule rather than the picture, because the picture is what this
        // cannot have: the solver steps physics and --selftest exits before the
        // first frame of a scene, so how a heap goes is measured by running one.
        // What is assertable is the order of the two numbers and the text both
        // shaders are written in.
        Check("the rubble is looked at before it leaves: it lies still first, and "
              + "the going is the longer half",
            WallRig.Linger > 0.0f && WallRig.Crumble > WallRig.Linger,
            $"lies {WallRig.Linger:F1}s, goes over {WallRig.Crumble:F1}s");
        Check("and it clears by default, which is the one state the effect is "
              + "looked at in",
            WallRig.CrumblesByDefault, "off by default");
        // Asserted on the compiled sources for FoamEdge's reason: a copy that
        // drifted would still compile and still draw, and it would read as a
        // fading brick being a slightly different brick from the one beside it.
        string intact = WallStack.MortarCode;
        string going = WallStack.GhostCode;
        Check("a whole brick and a going one are one text with two settings, "
              + "never two shaders",
            intact.Contains("void fragment") && going.Contains("void fragment")
            && Same(intact, going) >= 0.9,
            $"they share {Same(intact, going) * 100.0:F0}% of their lines");
        // The pair the fade rests on. A material that writes ALPHA is a material
        // in the transparent pass, where multimesh instances are not sorted - so
        // only rubble goes there, and Backwards is what orders it instead of the
        // buffer. What depth each of them takes is asserted further down, where
        // the reason for it is.
        Check("what is standing stays opaque and what is rubble blends",
            !intact.Contains("ALPHA") && !intact.Contains("depth_draw")
            && going.Contains("ALPHA = left"),
            $"whole: alpha {intact.Contains("ALPHA")}, depth "
            + $"{intact.Contains("depth_draw")}; going: alpha "
            + $"{going.Contains("ALPHA = left")}");
        // And the shadow spends the same figure as ink rather than as coverage,
        // because a stencil union cannot composite alpha: the first fragment at a
        // pixel wins, so an eaten shadow stays full black in patches under a brick
        // that has almost gone.
        Check("and its shadow thins with it rather than being eaten away",
            WallStack.ShadeCode.Contains("ALPHA = ink * left"),
            "the shadow does not read how much is left");

        // --- what a ram lets go of -------------------------------------------
        //
        // A tank knocks a brick out by driving into it, and the driven ram is
        // an event now - see WallRig.Rammed, asserted above on a raised wall.
        // What is still arithmetic here is the pair of windows the event and
        // the shove count share.
        Check("the window stops short of the tip by half a brick, so masonry "
              + "the nose has only come to rest against is not driven through",
            WallRig.Through(0.6f) < 0.0f
            && Mathf.IsEqualApprox(WallRig.Through(0.6f), -0.3f),
            $"the near end at {WallRig.Through(0.6f):F2}m on a 0.60m brick");

        // --- and it ends on the piece rather than on the worst piece ---------
        //
        // The window is tested on a centre, so it only means "let go when the
        // nose gets to it" if it reaches forward by the piece's own half-extent.
        // NoseThrough is the worst any piece can present whatever way it was
        // laid, which makes it right as a bound and wrong as a distance.
        Check("a box's reach along its own axes is its half extents, which is "
              + "what turns a window tested on a centre into one on a face",
            Mathf.IsEqualApprox(
                WallRig.Face(Basis.Identity, new Vector3(0.30f, 0.05f, 0.15f),
                             Vector3.Right), 0.30f)
            && Mathf.IsEqualApprox(
                WallRig.Face(Basis.Identity, new Vector3(0.30f, 0.05f, 0.15f),
                             Vector3.Back), 0.15f),
            $"{WallRig.Face(Basis.Identity, new Vector3(0.30f, 0.05f, 0.15f), Vector3.Right):F2}m "
            + $"along and {WallRig.Face(Basis.Identity, new Vector3(0.30f, 0.05f, 0.15f), Vector3.Back):F2}m across");
        Check("and it turns with the piece, so a header presents its length "
              + "where a stretcher presents its depth",
            Mathf.IsEqualApprox(
                WallRig.Face(new Basis(Vector3.Up, Mathf.Pi * 0.5f),
                             new Vector3(0.30f, 0.05f, 0.15f), Vector3.Right),
                0.15f),
            $"{WallRig.Face(new Basis(Vector3.Up, Mathf.Pi * 0.5f), new Vector3(0.30f, 0.05f, 0.15f), Vector3.Right):F2}m "
            + "turned a quarter about Y");
        Check("a stretcher is let go of nearer the tip than the bound, and a "
              + "header exactly at it - the bound being the worst of the two",
            WallRig.Face(Basis.Identity, new Vector3(0.30f, 0.05f, 0.15f),
                         Vector3.Back) < -WallRig.Through(0.6f)
            && Mathf.IsEqualApprox(
                WallRig.Face(Basis.Identity, new Vector3(0.30f, 0.05f, 0.15f),
                             Vector3.Right), -WallRig.Through(0.6f)),
            $"0.15m and 0.30m against a bound of {-WallRig.Through(0.6f):F2}m");

        // --- and the momentum it hands over instead of touching --------------
        //
        // The ram was the only one of the three strikes handing the masonry
        // nothing at all: a standing brick is a static body, and the driven
        // hull is not in the solver at all. These are what the shove that
        // replaces the touch may and may not be.
        Check("a reversing hull rams nothing and a parked one nothing at all, "
              + "because the band is measured along the heading it faces now",
            WallRig.Shunted(0.0f) == 0.0f && WallRig.Shunted(-2.0f) == 0.0f
            && WallRig.Shunted(1.6f) > 0.0f,
            $"{WallRig.Shunted(-2.0f):F2} m/s backing off, "
            + $"{WallRig.Shunted(1.6f):F2} driving at 1.60");
        Check("and a piece never leaves faster than the box that let go of it, "
              + "which is what makes it a ram and not a break shot",
            WallRig.Shunted(1.6f) <= 1.6f + 0.001f
            && WallRig.Shunted(3.2f) > WallRig.Shunted(1.6f),
            $"{WallRig.Shunted(1.6f):F2} m/s off a 1.60 m/s box, "
            + $"{WallRig.Shunted(3.2f):F2} off a 3.20");

        // --- and which way, which is what breaks a stack rather than moving it
        //
        // Handed the same speed every piece keeps its neighbours' velocity, so
        // nothing shears and the leaf travels as a block: measured on the ring,
        // two leaves came back with more seated masonry at a uniform shove than
        // with no shove at all. The hull drives through what it can and parts
        // the rest, so a piece at the flank leaves across the lane.
        Check("a piece on the drive axis is pushed straight down it, there "
              + "being no side for a wedge to part it towards",
            WallRig.Wedge(Vector3.Back, Vector3.Zero, 1.94f)
                   .IsEqualApprox(Vector3.Back),
            $"{WallRig.Wedge(Vector3.Back, Vector3.Zero, 1.94f)}");
        Check("one out at the flank is thrown across the lane as well, and "
              + "keeps its forward push rather than paying for the other with it",
            Mathf.IsEqualApprox(
                WallRig.Wedge(Vector3.Back, Vector3.Right * 1.94f, 1.94f)
                       .Dot(Vector3.Back), 1.0f)
            && Mathf.IsEqualApprox(
                   WallRig.Wedge(Vector3.Back, Vector3.Right * 1.94f, 1.94f)
                          .Dot(Vector3.Right), WallRig.RamSplay)
            && WallRig.Wedge(Vector3.Back, Vector3.Right * 1.94f, 1.94f)
                      .Length() <= 2.0f,
            $"{WallRig.Wedge(Vector3.Back, Vector3.Right * 1.94f, 1.94f)}");
        Check("and a course standing high over the axis is not thrown by its "
              + "own height, because what the hull parts it parts sideways",
            WallRig.Wedge(Vector3.Back, Vector3.Up * 2.0f, 1.94f)
                   .IsEqualApprox(Vector3.Back),
            $"{WallRig.Wedge(Vector3.Back, Vector3.Up * 2.0f, 1.94f)}");
        Check("the ram hands its momentum over by default",
            WallRig.ShuntsByDefault, $"{WallRig.ShuntsByDefault}");

        // --- and what it is pushing against while it does ---------------------
        //
        // The going, not the sweep: how heavy masonry is to drive through. The
        // two windows are the whole of it that can be asserted here - the count
        // itself is a live solver, and a picture of a tank slowing down is a
        // picture, which --selftest does not have.
        Check("the band a nose presses against begins where the band it has "
              + "driven through ends, so no piece is both let go of and resisting",
            WallRig.Through(0.6f) < 0.0f && WallRig.Shove(0.6f) > 0.0f,
            $"driven through up to {WallRig.Through(0.6f):F2}m, pressing to "
            + $"{WallRig.Shove(0.6f):F2}m");
        Check("and it reaches a good way in front of the tip, because what a tank "
              + "shoves once the leaf gives is the leaf in bits, moving ahead of "
              + "it",
            WallRig.Shove(0.6f) >= 0.6f,
            $"{WallRig.Shove(0.6f):F2}m ahead on a 0.60m brick");
        // Where it sits among the other two caps, and that it is one figure
        // rather than three. Below the ford at every class because a wall does
        // not move until it is broken; the same for all three because the wall
        // under all three is one wall, and because the spread of three caps was
        // the whole of the scatter - see MovementProfile.WallSpeed.
        MovementProfile mid = MovementProfile.Medium;
        Check("masonry is the heaviest going on the board, at every class",
            MovementProfile.WallSpeed < MovementProfile.Light.WaterSpeed
            && MovementProfile.WallSpeed < MovementProfile.Heavy.WaterSpeed,
            $"wall {MovementProfile.WallSpeed:F0} px/s against fords of "
            + $"{MovementProfile.Light.WaterSpeed:F0} and "
            + $"{MovementProfile.Heavy.WaterSpeed:F0}");
        Check("and it is one figure and not three, so what a ram flings is the "
              + "wall's doing rather than the class's",
            MovementProfile.All.All(
                p => Math.Abs(MovementProfile.WallSpeed
                              - MovementProfile.WallSpeed) < 0.001)
            && MovementProfile.Light.TopSpeed > MovementProfile.Heavy.TopSpeed,
            $"{MovementProfile.WallSpeed:F0} px/s under cruises of "
            + $"{MovementProfile.Light.TopSpeed:F0} and "
            + $"{MovementProfile.Heavy.TopSpeed:F0}");
        // The cap itself, through the tick that applies it. A board with no walls
        // answers null and gets the cap it had - that half matters as much as the
        // other, because a hook that fires on every board is a tank that crawls
        // everywhere.
        if (vehicles is { Count: > 0 } drivers && field.InBounds(new Vector2I(1, 1)))
        {
            Vehicle one = drivers[0];
            List<Vector2I> was = one.Path;
            int step = one.PathStep;
            Vector2I stood = one.Cell;
            one.Cell = new Vector2I(1, 1);
            one.Path = new List<Vector2I> { new Vector2I(1, 1) };
            one.PathStep = 0;
            var walk = new TankTick { Field = field };
            double open = walk.SpeedCap(one);
            string openWhy = walk.SpeedCapWhy(one);
            walk.Shoving = _ => true;
            double dug = walk.SpeedCap(one);
            string dugWhy = walk.SpeedCapWhy(one);
            walk.Shoving = _ => false;
            double clear = walk.SpeedCap(one);
            one.Path = was;
            one.PathStep = step;
            one.Cell = stood;
            Check("a tank with its nose in masonry may only go at the wall's "
                  + "figure, and gets its cruise back the moment it is through",
                Math.Abs(dug - MovementProfile.WallSpeed) < 0.01
                && Math.Abs(clear - open) < 0.01 && dug < open - 1.0,
                $"{open:F0} px/s open, {dug:F0} shoving, {clear:F0} out the far "
                + "side");
            Check("and the readout names the cap in force, so a tank in a wall is "
                  + "not read as an order gone stale",
                dugWhy == "shoving" && openWhy != "shoving",
                $"shoving reads \"{dugWhy}\", open ground reads \"{openWhy}\"");
            // And how fast the ceiling may be closed on. Harder than the brakes,
            // because a wall is hit rather than eased onto - the one exception to
            // the rule the ramp exists for - and still not a stop in one frame,
            // which is the objection that put the ramp there.
            double bite = one.Profile.Accel * MovementProfile.WallBrake / 60.0;
            Check("masonry takes speed off harder than the engine's own brakes, "
                  + "because it is hit rather than eased onto",
                MovementProfile.WallBrake > 1.0,
                $"x{MovementProfile.WallBrake:F1} of a {one.Profile.Accel:F0} "
                + "px/s/s brake");
            Check("and still not a dead stop in one frame, which is what the ramp "
                  + "it goes through is there to stop",
                bite < one.Profile.TopSpeed * 0.25,
                $"{bite:F0} px/s a frame off a {one.Profile.TopSpeed:F0} px/s "
                + "cruise");
        }

        // --- and whether a crossing is a ram at all ---------------------------
        //
        // What breaks masonry is the order. The board stands the tank inside a
        // ring it does not fit in, so a rule that broke masonry on any contact
        // read as a hull demolishing a yard by driving out of it. Both halves
        // are asserted, because either alone passes on the failure the other
        // one is: a gate that never fires is a wall no tank can break, and one
        // that always fires is what this replaces.
        Check("an ordinary order does not break masonry - a ram is an intent, "
              + "and driving past a wall is not one",
            !TankBench.Sweeps(true, false, false),
            $"moving under an ordinary order: {TankBench.Sweeps(true, false, false)}");
        Check("and one that asked to ram does, which is the half that makes the "
              + "gate a gate rather than a wall nothing can break",
            TankBench.Sweeps(true, true, false),
            $"moving under a ram: {TankBench.Sweeps(true, true, false)}");
        Check("and a standstill breaks nothing whatever was asked for, because a "
              + "hull turning on the spot inside a ring is not a ram either",
            !TankBench.Sweeps(false, true, false)
            && !TankBench.Sweeps(false, false, true),
            $"parked under a ram: {TankBench.Sweeps(false, true, false)}, "
            + $"parked with --drive-rams: {TankBench.Sweeps(false, false, true)}");
        Check("and --drive-rams puts the old answer back, where any order took "
              + "the leaf it drove through",
            TankBench.Sweeps(true, false, true),
            $"moving under an ordinary order: {TankBench.Sweeps(true, false, true)}");

        // --- what the tank pushes with ---------------------------------------
        if (vehicles is { Count: > 0 } garage && field.Atlas is not null)
        {
            float radius = field.Atlas.HexRect.Size.X * 0.5f;
            Vector3 box = WallProp.Box(garage[0], radius);
            // Judged at unit scale, not as the dial left it: the box rightly
            // follows the size level, so a panel.json that opens the bench
            // small halves every side - a legitimate setting that must not
            // read as a mismeasured tank. Caught live: a 0.5 opening level
            // put LTP at 0.90m of box and failed an absolute band.
            float unit = Mathf.Max(garage[0].Sprite.BodyScale, 1e-4f);
            Check("the ram's box is measured off the atlas: longer than it is "
                  + "wide, and a sane number of metres at unit scale",
                box.Z > box.X && box.Z / unit > 2.0f && box.Z / unit < 15.0f
                && box.X / unit > 0.5f
                && box.Y / unit > 1.0f && box.Y / unit < 4.5f,
                $"{box.X:F2} x {box.Y:F2} x {box.Z:F2} m at {unit:F3}x "
                + $"({garage[0].Tag}, radius {radius:F1}px, "
                + $"span {garage[0].Atlas.HullSpan}px)");
            // The height too, where the set carries a height map: the map's
            // range is the hull's own world span, so the box stops being
            // TankTall metres of declaration the moment there is something to
            // measure - and stays exactly that on a set with nothing to ask.
            Check("and its height is the height map's answer where there is "
                  + "one, not the declared constant",
                garage[0].Atlas.HullTallPx > 0.0
                    ? Mathf.Abs(box.Y - (float)garage[0].Atlas.HullTallPx
                                * garage[0].Sprite.BodyScale
                                * WallRig.MetresPerCell / radius) < 0.01f
                    : box.Y == WallRig.TankTall,
                $"{box.Y:F2}m of box against {garage[0].Atlas.HullTallPx:F1}px "
                + "of map");
            // The prow's slope is the sprite's own where it could be read:
            // a run of nought builds the declared third-of-the-hull wedge,
            // anything measured has to be a fraction a glacis could be.
            float bow = WallProp.Bow(garage[0], radius);
            Check("and the glacis run is measured off the same map, a "
                  + "believable fraction of the hull or nothing at all",
                bow == 0.0f || (bow > box.Z * 0.05f && bow < box.Z * 0.7f),
                $"{bow:F2}m of bow on a {box.Z:F2}m hull");
            // The crown is measured off the turret sprite's broadsides, so it
            // exists with or without a height map - but it only means anything
            // against a hull height, so it is judged as the rig judges it:
            // above the deck, and not a tower.
            float crown = WallProp.Crown(garage[0], radius);
            Check("and the turret's crown stands above the deck and below a "
                  + "tower, so the box grows a roof and not a mast",
                crown > box.Y && crown < box.Y * 2.5f,
                $"{crown:F2}m of crown over {box.Y:F2}m of hull");
            // The class scale comes in with all four numbers, which is both
            // why the bench has three classes and the guarantee that resizing
            // a tank never reshapes it: one multiplier, measured proportions.
            float was = garage[0].Sprite.BodyScale;
            garage[0].Sprite.BodyScale = was * 2.0f;
            Vector3 twice = WallProp.Box(garage[0], radius);
            float twiceBow = WallProp.Bow(garage[0], radius);
            garage[0].Sprite.BodyScale = was;
            Check("and the class scale comes in with it - every side and the "
                  + "bow together - so a resized tank is the same shape pushed "
                  + "larger, never a different one",
                Mathf.Abs(twice.Z - box.Z * 2.0f) < 0.01f
                && Mathf.Abs(twice.X - box.X * 2.0f) < 0.01f
                // A declared height is a constant, and a constant does not
                // scale - only what was measured has proportions to keep.
                && (garage[0].Atlas.HullTallPx > 0.0
                    ? Mathf.Abs(twice.Y - box.Y * 2.0f) < 0.01f
                    : twice.Y == box.Y)
                && Mathf.Abs(twiceBow - bow * 2.0f) < 0.01f,
                $"{box.X:F2} x {box.Y:F2} x {box.Z:F2} + {bow:F2} doubled is "
                + $"{twice.X:F2} x {twice.Y:F2} x {twice.Z:F2} + {twiceBow:F2}");
        }

        // --- the bench panel's file -------------------------------------------
        //
        // What is assertable from the harness: the file loads, and it does not
        // quietly become a second owner of the wall's opening shape. The
        // two-way row check cannot run here - the bench's rows are delegates
        // onto a live bench, and a second declaration of the list just for
        // checking would be the failure the check catches - so it runs in
        // TankBench.Vet at every bench start-up, loudly.
        PanelText benchText = PanelText.Load(System.IO.Path.Combine(
            ProjectSettings.GlobalizePath("res://"), TankBench.PanelFile));
        Check("the bench panel has a file of its own, so what it opens on is "
              + "edited rather than recompiled",
            benchText.Loaded, benchText.Error);
        Check("and the wall's opening shape stays walls.json's alone: the "
              + "panel file carries no default for sides, courses or leaves",
            new[] { "wall.sides", "wall.courses", "wall.leaves" }
                .All(id => benchText.Default(id) is null),
            "a second owner of the wall's shape, quietly outranking walls.json");

        // --- a breach takes the section --------------------------------------
        //
        // Which shots do, and it is the one exception AP already carries against
        // the cascade: a round through the middle takes what stands on its line
        // and leaves the rest standing round the gap.
        Check("a ram and a burst break into masonry, so each brings the section "
              + "it broke into down whole; a round through it does not",
            WallRig.Breaching(WallRig.Strike.Ram)
            && WallRig.Breaching(WallRig.Strike.He)
            && !WallRig.Breaching(WallRig.Strike.Ap),
            $"ram {WallRig.Breaching(WallRig.Strike.Ram)}, "
            + $"he {WallRig.Breaching(WallRig.Strike.He)}, "
            + $"ap {WallRig.Breaching(WallRig.Strike.Ap)}");
        // A share of the section and never a count, because a section is however
        // long the side it stands on is cut into. Both walls this bench actually
        // builds, at their own sizes.
        Check("and how much of a section has to go is a share of it, so the same "
              + "rule fits a ring's leaf and a single-sided wall",
            WallRig.Breached(12, 89) && !WallRig.Breached(10, 89)
            && WallRig.Breached(8, 59) && !WallRig.Breached(6, 59),
            $"of 89: 10 {WallRig.Breached(10, 89)}, 12 "
            + $"{WallRig.Breached(12, 89)}; of 59: 6 {WallRig.Breached(6, 59)}, "
            + $"8 {WallRig.Breached(8, 59)}");
        // The one that pays for itself: a hull leaving the ring clips a brick or
        // two off the mitred corner of the leaf next to the one it drives
        // through, and a rule that levelled a section on first contact would
        // bring that one down as well.
        //
        // It only covers the smallest graze, and that is worth saying. Measured
        // on the ring, a corner clip runs to 54 pieces of 96 - four times the
        // share - so what keeps a grazed leaf standing is the ram naming its
        // section, below. This protects the first brick or two and nothing past
        // them.
        Check("so a graze is not a breach: two bricks off the corner of a leaf "
              + "leave it standing",
            !WallRig.Breached(1, 89) && !WallRig.Breached(2, 89),
            $"two of 89 breaches: {WallRig.Breached(2, 89)}");
        Check("and a section the profile has cut down to a few blocks still "
              + "answers, while one that has lost nothing never does",
            WallRig.Breached(1, 4) && !WallRig.Breached(0, 89)
            && !WallRig.Breached(1, 0),
            $"one of four {WallRig.Breached(1, 4)}, "
            + $"none of 89 {WallRig.Breached(0, 89)}, "
            + $"one of none {WallRig.Breached(1, 0)}");

        // And the reason a burst has to name the section it brings down rather
        // than let the share decide it - see WallRig.Breach.
        //
        // A burst has a radius and the radius grows with the dial, so a heavy
        // shell reaches into the leaves either side of the one it went off
        // against. Measured on the ring, as a fraction of each leaf's 96 pieces:
        // the struck leaf takes 0.28 at force 0.25 and 0.82 at force 1, while a
        // neighbour takes 0.08 at 1.5, 0.36 at 3 and 0.81 at 10. Both ends below
        // are true of the same threshold, so no threshold orders them - which is
        // the whole argument, and it fails the moment someone tries to fix a
        // spreading breach by raising the share instead.
        Check("and the share cannot tell the leaf that was hit from the "
              + "neighbour the blast reached, which is why the section is named",
            WallRig.Breached(27, 96) && WallRig.Breached(78, 96)
            && 27.0f / 96.0f < 78.0f / 96.0f,
            $"struck leaf at a quarter charge {WallRig.Breached(27, 96)}, "
            + $"neighbour at ten {WallRig.Breached(78, 96)}");

        // And the same argument for the ram, which used to ask per piece.
        //
        // A hull's lane cannot widen with the dial, so "a brick it reached is a
        // brick it drove through" is true - but a brick off a mitred corner is a
        // brick it drove through belonging to a leaf it did not. Measured on the
        // ring with the breach off, so that the hull and the cascade are all
        // that is acting: a leaf driven through came down 70-72 of 72 and 74-75
        // of 75, while a leaf merely grazed lost 8 to 54 of 96. Both ends are
        // over the share, so no threshold orders them either - and the low end
        // is not free to raise, because the wall bench's own ram at half force
        // needs the share to fire at all (measured: the wall lies down at top
        // 0.173 with the breach and stands at 0.526 without).
        //
        // So the ram names its section too - see WallRig.Reached, which asks
        // Breach once about the leaf the lane is going into rather than once per
        // piece. Measured on three legs of one command: two breaches on one run
        // and three on another before, one on every run after.
        Check("and the share cannot tell the leaf a hull drove through from the "
              + "one it grazed on the way out, which is why the ram names its "
              + "section as well",
            WallRig.Breached(70, 72) && WallRig.Breached(12, 96)
            && 12.0f / 96.0f < 70.0f / 72.0f,
            $"driven through {WallRig.Breached(70, 72)}, "
            + $"grazed {WallRig.Breached(12, 96)}");

        // So the field is confined, not just the verdict - see WallRig.Reaches.
        //
        // Naming the section a burst brings down stopped the neighbours coming
        // down whole and left the blast still reaching them: at force 10 it
        // thaws and throws 78 of each neighbour's 96 pieces, which is a leaf
        // destroyed by any reading but the rule's.
        Check("a strike acts on the section it struck, on the rubble at its "
              + "feet, and on no other section - blast field, hull band and "
              + "cascade cut by the one predicate",
            WallRig.Reaches(2, 2) && WallRig.Reaches(2, -1)
            && !WallRig.Reaches(2, 1) && !WallRig.Reaches(2, 3),
            $"own {WallRig.Reaches(2, 2)}, rubble {WallRig.Reaches(2, -1)}, "
            + $"next door {WallRig.Reaches(2, 3)}");
        // And its own docstring's claim, which the line that distributes the
        // blast did not keep: a round put through a gap in the masonry went off
        // at the muzzle and threw whatever stood within a radius of the gun.
        // Worse than untidy - the burst centre came out at an infinity with a
        // zero in it, so every distance was NaN, every test against it false,
        // and the whole prop was thawed and pushed to nowhere. Measured on the
        // wall bench turned 60 degrees: "reach NaN, top NaN", and a wall that
        // vanishes.
        Check("and a burst that meets no face touches nothing at all",
            !WallRig.Reaches(-1, 0) && !WallRig.Reaches(-1, -1),
            $"no face still reaches masonry {WallRig.Reaches(-1, 0)}, "
            + $"rubble {WallRig.Reaches(-1, -1)}");
        WallRig.Segmented = false;
        bool wide = WallRig.Reaches(2, 3) && WallRig.Reaches(-1, 0);
        WallRig.Segmented = WallRig.SegmentedByDefault;
        Check("and --wide-blast puts back all three fields that crossed "
              + "sections, which is the A/B a segment is judged by",
            wide && WallRig.SegmentedByDefault && WallRig.Segmented,
            $"wide blast still confined: {!wide}");

        // --- and every piece knows which section it is in ----------------------
        //
        // Written where the fold is decided and nowhere else - see Block.Side.
        // The failure this catches is silent and total: with the side left
        // unwritten every piece reads as section nought, so the first brick a
        // shot reaches brings down a leaf on the far side of the cell.
        WallKit.Plan fan = WallKit.Lay(new WallKit.Recipe { Sides = 6 });
        WallKit.Fit(fan, 0.97f);
        WallKit.Scatter(fan, new WallKit.Recipe { Sides = 6 }, 1.0f, 0.97f);
        var carried = new HashSet<int>();
        bool sane = true, apron = false;
        foreach (WallKit.Block b in fan.Blocks)
        {
            if (b.Course < 0)
            {
                apron = true;
                continue;
            }
            sane &= b.Side >= 0 && b.Side < 6;
            carried.Add(b.Side);
        }
        Check("every piece of masonry stands on a side of the cell, and a chain "
              + "folded round all six carries pieces on every one of them",
            sane && carried.Count == 6,
            $"{carried.Count} of 6 sides carry masonry, all in range {sane}");
        Check("and the wall that runs along one side puts all of it there",
            plan.Blocks.TrueForAll(b => b.Course < 0 || b.Side == 0),
            "a single-sided wall has masonry off side nought");
        // Rubble is not masonry, for the fourth time: the apron is dropped after
        // the fold, lies on the ground inside the cell and is no part of a
        // section - so it cannot be brought down with one, and it is what
        // WallStack draws under a hull rather than over it.
        Check("the apron is dropped after the fold, so it is rubble on the "
              + "ground rather than a piece of any section",
            apron, "the fan has no apron at all, so nothing was asserted");

        // --- what rubble does about a hull -------------------------------------
        //
        // Standing masonry is a solid on the board and takes the depth buffer,
        // which is what lets it hide a tank behind it. Only the apron gives
        // that up now. A let-go piece used to give it up too, back when the
        // box swallowed what it overran and the heap lived inside the hull -
        // drawn honestly it painted across the hull up to the turret ring.
        // The shepherd ended the burial, and the trade flipped with it: the
        // heap stands before the prow, and dressing it down made the tank
        // visibly drive over the wall it was ploughing.
        Check("the apron is rubble the moment it is laid; a whole piece is "
              + "masonry, standing or shoved - the heap before the prow hides "
              + "the hull that pushes it",
            WallStack.Rubble(-1) && !WallStack.Rubble(3),
            $"apron {WallStack.Rubble(-1)}, course {WallStack.Rubble(3)}");
        WallStack.Dressed = false;
        bool asSolid = !WallStack.Rubble(-1);
        WallStack.Dressed = WallStack.DressedByDefault;
        Check("and --solid-rubble puts back the wall whose every piece was a "
              + "solid, which is the A/B the change is judged by",
            asSolid && WallStack.DressedByDefault && WallStack.Dressed,
            $"solid-rubble leaves the apron rubble: {!asSolid}");
        Check("standing masonry writes depth and rubble does not, which is how "
              + "a wall hides a tank behind it while a heap at its feet does not",
            !WallStack.MortarCode.Contains("depth_draw")
            && WallStack.GhostCode.Contains("depth_draw_never")
            && WallStack.SolidCode.Contains("depth_draw_always"),
            "the two bricks disagree about depth the wrong way round");
        // Both are the same text with one render mode changed, for FoamEdge's
        // reason: the seam, the grain, the chip and the tone are one statement,
        // and a second copy of them drifts exactly where nobody compares it - the
        // colour of a fading brick against the one beside it.
        Check("and the two are the same brick with one render mode between them, "
              + "never two copies of it",
            WallStack.GhostCode.Replace("depth_draw_never", "depth_draw_always")
            == WallStack.SolidCode,
            "the rubble brick and the solid one have drifted apart");
        // Rubble is on the board's dressing rung and a tank on the standing one,
        // which is what settles the pair when neither writes depth. A tie is
        // settled by distance, and the pile in front of a tank is nearer than the
        // tank.
        Check("rubble sorts under what stands on the board, and a cast shadow "
              + "under both",
            Stage3D.ShadowOrder < Stage3D.DressOrder
            && Stage3D.DressOrder < Stage3D.StandOrder,
            $"shadow {Stage3D.ShadowOrder}, dressing {Stage3D.DressOrder}, "
            + $"standing {Stage3D.StandOrder}");

        // --- the engine the numbers were tuned against -----------------------
        //
        // A physics engine that is not the one they were tuned against is
        // invisible: the collapse still happens, it is just a different collapse,
        // and every jolt_ setting in project.godot is quietly doing nothing.
        string engine = ProjectSettings.GetSetting(
            "physics/3d/physics_engine", "DEFAULT").AsString();
        double slop = ProjectSettings.GetSetting(
            "physics/jolt_physics_3d/simulation/penetration_slop", 0.02).AsDouble();
        Check("the physics engine is the one the collapse was tuned against",
            engine.Contains("Jolt"), $"it is {engine}");
        Check("and its penetration slop is the tuned one, which decides how much "
              + "of the wall comes down",
            slop <= 0.005, $"slop {slop:F4}, tuned at 0.002");
    }

    private static void Ripple(Action<string, bool, string> Check)
    {
        Theme("ripples: water that carries on after the tank has gone");
        // A tidy squash, so the two grains come out 7 and 14 and a probe can be
        // put where the arithmetic says rather than near it. The board's own is
        // 0.5075 and nothing below depends on which it is.
        const float squash = 0.5f;
        float dx = Ripples.Grain;
        float dz = Ripples.Grain / squash;
        Rect2 Span(int w, int d) =>
            new Rect2(Vector2.Zero, new Vector2(w * dx, d * dz));
        Ripples Pond(int w, int d, Func<Vector2, bool>? wet = null)
        {
            var pond = new Ripples();
            pond.Fit(Span(w, d), squash, 1L, wet ?? (_ => true));
            return pond;
        }
        void Steps(Ripples pond, int frames)
        {
            for (int i = 0; i < frames; i++)
                pond.Tick(Ripples.Step);
        }

        Ripples calm = Pond(24, 12);
        Steps(calm, 120);
        Check("a pond nobody has touched stays flat",
              calm.Peak == 0.0f,
              $"two seconds of stepping raised {calm.Peak:F4} units out of "
              + "nothing, so the step is making water rather than moving it");

        // How the whole field is laid: square on screen, not on the water. The one
        // number here that is about the camera, and it is worth an assertion
        // because getting it the other way up costs twice the texels for nothing
        // the eye can resolve.
        Check("its grid is square on screen rather than on the water",
              Mathf.Abs(calm.Cell.Y * squash - calm.Cell.X) < 1e-3f,
              $"a texel is {calm.Cell.X:F2} by {calm.Cell.Y:F2} world units, which "
              + $"at a squash of {squash} is {calm.Cell.X:F2} by "
              + $"{calm.Cell.Y * squash:F2} on screen");

        Ripples still = Pond(24, 12);
        still.Note(still.Middle(12, 6), Vector2.Right, 0.0f);
        Steps(still, 30);
        Check("and a hull that is parked does not push it",
              still.Peak == 0.0f,
              $"a tank standing still put {still.Peak:F4} units of wave in the "
              + "water, so the share of the ford's ceiling is not being read");

        // Everything below shares one board big enough that a probe past the wave
        // front is still inside the field - otherwise "nothing has got there yet"
        // and "there is no field there" are the same answer, and the check that
        // the front is bounded passes on a box too small to hold it.
        Ripples ring = Pond(200, 100);
        Vector2 hub = ring.Middle(100, 50);
        ring.Note(hub, Vector2.Right, 1.0f);
        ring.Tick(Ripples.Step);
        float bow = ring.Height(hub + new Vector2(Ripples.Reach, 0.0f));
        float stern = ring.Height(hub - new Vector2(Ripples.Reach, 0.0f));
        Check("a hull under way piles water ahead of itself and hollows it behind",
              bow > 0.0f && stern < 0.0f,
              $"the water stands {bow:F4} at the bow and {stern:F4} at the stern, "
              + "so what is being put in is not a dipole - and a bow wave is a "
              + "dipole from a moving source or it is a ring");

        Steps(ring, 59);
        float lobe = Ripples.Reach + Ripples.Spread;
        float near = ring.Height(hub + new Vector2(lobe + 90.0f, 0.0f));
        Check("and the answer travels outward from it",
              Mathf.Abs(near) > 1e-5f,
              "a second on and the water 90 units past the far edge of the push "
              + $"has moved by {near:F6}, so nothing is propagating - which is the "
              + "one thing a field is for");

        float front = Ripples.Speed * 1.0f + lobe + 4.0f * ring.Cell.X;
        float ahead = ring.Height(hub + new Vector2(front, 0.0f));
        Check("and no faster than the speed it was given",
              Mathf.Abs(ahead) < 1e-6f,
              $"{front:F0} units out - past a second of travel at "
              + $"{Ripples.Speed:F0} plus the push's own reach - the water has "
              + $"already moved {ahead:F6}");

        // Judged on the height and not on the total, and that is a measurement
        // rather than a preference: the total is a sum over the field, and a ring
        // spreading over more of it holds a larger sum at a smaller height.
        //
        // <b>And judged over five half-lives rather than three, because a small
        // pond concentrates what it reflects.</b> Damping alone puts three at an
        // eighth and five at a thirtieth; measured on this one they came out at
        // 30% and 6%, and the difference is the walls - a ring that has come back
        // off four of them is standing on itself. So the threshold is set where
        // the reflections are paid for, and it still asserts the thing that
        // matters, which is that the water is not a memory that keeps.
        Ripples dies = Pond(60, 30);
        dies.Note(dies.Middle(30, 15), Vector2.Right, 1.0f);
        Steps(dies, 30);
        float loud = dies.Peak;
        float total = dies.Energy;
        Steps(dies, Mathf.RoundToInt(Ripples.Fade * 5.0f / Ripples.Step));
        Check("and dies away when nothing is pushing it",
              loud > 0.0f && dies.Peak < loud * 0.1f && dies.Energy < total,
              $"five half-lives on it stands {dies.Peak:F4} of {loud:F4}, so a "
              + "pond somebody crossed a minute ago is still moving");

        // Land is a wall, which is the whole of the boundary: a dry texel is left
        // out of the neighbour sum, so there is no slope across the bank and the
        // wave turns round instead of draining into it.
        Ripples bank = Pond(120, 60, p => p.X < 60.0f * dx);
        bank.Note(new Vector2(55.0f * dx, bank.Middle(0, 30).Y), Vector2.Right,
                  1.0f);
        Steps(bank, 90);
        int leaked = 0;
        float wetPeak = 0.0f;
        for (int j = 0; j < bank.Tall; j++)
        for (int i = 0; i < bank.Wide; i++)
        {
            float h = bank.Height(bank.Middle(i, j));
            if (bank.Wet(i, j))
                wetPeak = Mathf.Max(wetPeak, Mathf.Abs(h));
            else if (h != 0.0f)
                leaked++;
        }
        Check("land is a wall the water does not cross",
              leaked == 0 && wetPeak > 0.0f,
              $"{leaked} dry texels are carrying water, so the bank is a hole "
              + "rather than a boundary");

        // And it comes back off it. Asserted against the same push in open water,
        // because a bounded pond holds what an open one lets past - which is what
        // reflection is, said as a number.
        Ripples open = Pond(120, 60);
        open.Note(new Vector2(55.0f * dx, open.Middle(0, 30).Y), Vector2.Right,
                  1.0f);
        Steps(open, 90);
        float held = 0.0f;
        for (int j = 0; j < bank.Tall; j++)
        for (int i = 0; i < bank.Wide; i++)
            if (bank.Wet(i, j))
                held += Mathf.Abs(bank.Height(bank.Middle(i, j)));
        float loose = 0.0f;
        for (int j = 0; j < open.Tall; j++)
        for (int i = 0; i < 60; i++)
            loose += Mathf.Abs(open.Height(open.Middle(i, j)));
        Check("and it comes back off that wall rather than through it",
              held > loose * 1.05f,
              $"the walled half holds {held:F2} against {loose:F2} loose in the "
              + "same half of an open pond, so nothing is being turned back");

        // A rate and not an impulse, which is the same statement as being
        // independent of the frame rate: what a hull does to water in a second is
        // the same whether the second came in one frame or in sixty.
        Ripples lump = Pond(60, 30);
        Ripples split = Pond(60, 30);
        Vector2 spot = lump.Middle(30, 15);
        lump.Note(spot, Vector2.Right, 1.0f);
        lump.Tick(Ripples.Step * 3.0);
        for (int i = 0; i < 3; i++)
        {
            split.Note(spot, Vector2.Right, 1.0f);
            split.Tick(Ripples.Step);
        }
        Check("one long frame is the frames it is made of",
              lump.Steps == 3 && Mathf.Abs(lump.Peak - split.Peak) < 1e-6f,
              $"three steps in one frame gave {lump.Peak:F6} and three frames of "
              + $"one gave {split.Peak:F6}, so the push is an impulse per frame "
              + "and the water depends on the frame rate");

        Ripples late = Pond(20, 10);
        late.Note(late.Middle(10, 5), Vector2.Right, 1.0f);
        late.Tick(60.0);
        Check("and a frame a minute long does not advance it by a minute",
              late.Steps == Ripples.MaxSteps,
              $"{late.Steps} steps for one frame of sixty seconds, so a hitch "
              + "hands the pond an hour of weather");

        // Bitwise, because a simulation two captures of cannot be compared is a
        // simulation this bench cannot judge - the reason --capture pins the step
        // in the first place.
        Ripples twinA = Pond(40, 20);
        Ripples twinB = Pond(40, 20);
        for (int i = 0; i < 45; i++)
        {
            twinA.Note(twinA.Middle(10 + i / 4, 10), Vector2.Right, 1.0f);
            twinB.Note(twinB.Middle(10 + i / 4, 10), Vector2.Right, 1.0f);
            twinA.Tick(Ripples.Step);
            twinB.Tick(Ripples.Step);
        }
        Check("two runs of it agree to the bit",
              twinA.Peak == twinB.Peak && twinA.Energy == twinB.Energy,
              $"peaks {twinA.Peak:F9} and {twinB.Peak:F9}, energies "
              + $"{twinA.Energy:F6} and {twinB.Energy:F6}");

        // Two tanks add up, which is interference and is half of what a field is
        // for - and it is a property of the step rather than of the wiring, so it
        // is asserted on the step.
        Ripples solo1 = Pond(120, 60);
        Ripples solo2 = Pond(120, 60);
        Ripples both = Pond(120, 60);
        Vector2 one = both.Middle(40, 30);
        Vector2 two = both.Middle(80, 30);
        for (int i = 0; i < 40; i++)
        {
            solo1.Note(one, Vector2.Right, 1.0f);
            solo2.Note(two, Vector2.Left, 1.0f);
            both.Note(one, Vector2.Right, 1.0f);
            both.Note(two, Vector2.Left, 1.0f);
            solo1.Tick(Ripples.Step);
            solo2.Tick(Ripples.Step);
            both.Tick(Ripples.Step);
        }
        Vector2 mid = both.Middle(60, 30);
        float sum = solo1.Height(mid) + solo2.Height(mid);
        float mix = both.Height(mid);
        Check("two tanks pushing one pond add up where they meet",
              Mathf.Abs(mix - sum) < 1e-4f * Mathf.Max(1.0f, Mathf.Abs(sum)),
              $"between them the water stands {mix:F5} against {sum:F5} for the "
              + "two pushes taken apart, so they are not sharing one surface");

        // The dial moves the forcing and only the forcing: the field is linear, so
        // twice the shove is exactly twice the wave and not a differently shaped
        // one. That is the whole argument for it being the only dial there is.
        Ripples soft = Pond(120, 60);
        Ripples hard = Pond(120, 60);
        hard.Level = 2.0f;
        for (int i = 0; i < 40; i++)
        {
            soft.Note(one, Vector2.Right, 1.0f);
            hard.Note(one, Vector2.Right, 1.0f);
            soft.Tick(Ripples.Step);
            hard.Tick(Ripples.Step);
        }
        float soloH = soft.Height(mid);
        float hardH = hard.Height(mid);
        Check("the dial scales the wave and does not reshape it",
              Mathf.Abs(soloH) > 1e-6f
              && Mathf.Abs(hardH - 2.0f * soloH) < 1e-3f * Mathf.Abs(soloH),
              $"at twice the shove the same point stands {hardH:F6} against "
              + $"{soloH:F6}, which is not twice - so the level is doing something "
              + "besides scaling the push");

        // The same pond keeps its field when the board is rebuilt, and a different
        // one does not. Both halves, because this is the failure it was written
        // for: the ripples a crossing had made vanished on a frame near the end of
        // the order and stayed gone, which reads as a field that forgets.
        Ripples kept = Pond(60, 30);
        kept.Note(kept.Middle(30, 15), Vector2.Right, 1.0f);
        Steps(kept, 20);
        float before = kept.Peak;
        kept.Fit(Span(60, 30), squash, 1L, _ => true);
        Check("the same pond keeps its ripples when the board is rebuilt",
              before > 0.0f && kept.Peak == before,
              $"a rebuild took the field from {before:F4} to {kept.Peak:F4}, so "
              + "anything that rebuilds a mesh empties the pond");
        kept.Fit(Span(60, 30), squash, 2L, _ => true);
        Check("and a pond that is not the same one does not",
              kept.Peak == 0.0f,
              $"a different set of flooded cells kept {kept.Peak:F4} units of the "
              + "old field, so the wet mask under it may be stale");

        Ripples huge = new Ripples();
        huge.Fit(new Rect2(Vector2.Zero, new Vector2(20000.0f, 40000.0f)), squash,
                 1L, _ => true);
        Check("a pond too big for the field coarsens rather than being cropped",
              huge.Wide * huge.Tall <= Ripples.MaxTexels
              && huge.Box.Size.X >= 20000.0f && huge.Box.Size.Y >= 40000.0f,
              $"{huge.Wide}x{huge.Tall} texels over {huge.Box.Size} - either past "
              + $"the {Ripples.MaxTexels} cap or short of the water it was asked "
              + "to cover");

        Check("and the step stays inside its own stability bound",
              huge.Coupling <= Ripples.Bound + 1e-6f
              && calm.Coupling <= Ripples.Bound + 1e-6f,
              $"a coupling of {huge.Coupling:F3} against a bound of "
              + $"{Ripples.Bound:F3}: past a half the field does not wobble, it "
              + "doubles every step");

        Ripples dry = new Ripples();
        dry.Drain();
        Check("a board with no water has no field at all",
              dry.Wide == 0 && dry.Height(Vector2.Zero) == 0.0f
              && dry.Sheet is null,
              "a drained field still answers, so a board without a pond is paying "
              + "for one");

        // And what reads it. Three ways of drawing and nothing that decides, which
        // is the whole of how a displacement about the datum is allowed to exist:
        // the sprite's own cut, the pond's wall, the ring and the wading flag all
        // go on reading WaterTop.
        Check("the surface bends its normals by the field",
              Stage3D.DeepShader.Contains("wslope"),
              "nothing in the deep shader differences the field, so a ripple has "
              + "no slope and the one thing the eye reads off water is missing");
        Check("and runs it up the beach on the projection's own term",
              Stage3D.DeepShader.Contains("+ wsh * squash"),
              "the lap does not carry the field, or carries it through a gain - "
              + "and a gain there is a second opinion about the camera");
        Check("and whitens only the crests that stand over the cut",
              Stage3D.DeepShader.Contains("wash_crest")
              && Stage3D.DeepShader.Contains("float crest ="),
              "the foam does not read the field, so the bow wave is whatever the "
              + "band was painting");
        Check("but the sprite's own waterline does not read it at all",
              !Stage3D.PaintShader.Contains("wash"),
              "the half of the line that lies on the armour is being cut by the "
              + "simulation, which puts a displacement in front of every reader "
              + "that decides something");
        Check("and a board without a field costs nothing",
              Stage3D.DeepShader.Contains("hint_default_black")
              && Stage3D.DeepShader.Contains("* wash_on"),
              "an unset sampler has to read zero and the whole thing has to be "
              + "gated, or --no-ripples is not the picture as it was");
    }

    private static void Water(HexField field, Grove? grove,
                              Action<string, bool, string> Check)
    {
        Theme("water: a cell to drive into, not round");
        // What the session came up with, because the grove was sown against it and
        // the topic that judges the wood asks the board as it stands. Run() clears
        // the relief for the same reason and puts it back at the end; this is that
        // line for the water, and without it the pond's own cells come back
        // forest-eligible with no trees on them - which reports as the grove
        // disagreeing with the ground, a mile from anything to do with water.
        bool hadWater = field.HasWater;
        try
        {
            field.SetRelief(BoardMap.Bench.LevelsFor(field.Columns, field.Rows),
                            BoardMap.Bench.RampsFor(field.Columns, field.Rows));
            field.SetWater(BoardMap.Bench.WaterFor(field.Columns, field.Rows));
            Check("the board carries water at all", field.HasWater,
                "no cell flooded, so nothing below is being tested");

            // It stands over its bottom rather than replacing it: a tank in a ford
            // is standing on the cell's own top face, and the water is what is
            // added. Nought here is the whole feature quietly reduced to a tinted
            // hexagon.
            int flat = 0, sunk = 0;
            var pond = new List<Vector2I>();
            for (int q = 0; q < field.Columns; q++)
            for (int r = 0; r < field.Rows; r++)
            {
                var cell = new Vector2I(q, r);
                if (!field.IsWater(cell))
                    continue;
                pond.Add(cell);
                if (field.WaterTop(cell) - field.TopAt(cell) < 1.0f)
                    sunk++;
                foreach (int heading in HexField.EdgeHeadings)
                {
                    Vector2I next = HexField.Step(cell, heading);
                    if (field.IsWater(next)
                        && Math.Abs(field.WaterTop(next) - field.WaterTop(cell))
                           > 0.01f)
                        flat++;
                }
            }
            Check("water stands over its bottom rather than on it", sunk == 0,
                $"{sunk} cells are flooded to less than a pixel");
            Check("and one body of it is one surface", flat == 0,
                $"{flat} neighbouring pairs are at two heights");

            // How much of a tank goes under, which is the only measure of the
            // depth worth having - the fraction of a level says nothing about
            // whether it reads. Both ends are wrong in their own way, which is why
            // there are two of them.
            float deep = field.WaterRise;
            float tall = field.Atlas?.DrawnHeight ?? 0.0f;
            double part = tall > 0.0f ? deep / tall : 0.0;
            Check("a ford is deep enough to see and shallow enough to drive",
                part >= Main.ShallowAt && part <= Main.SunkAt,
                $"{deep:F0}px is {part:P0} of a tank's {tall:F0}px - wanted "
                + $"{Main.ShallowAt:P0} to {Main.SunkAt:P0}");

            // Water is not a wall, which was the whole request - and it is asked
            // as "flooding changes nothing" rather than "every step across the
            // pond is legal".
            //
            // <b>The second form was the old one and it stopped being true the
            // day a beach could be flooded.</b> A flooded ramp is still a ramp
            // and is still entered along its own axis, so eight steps sideways
            // onto the pit's mouth are refused - by the ramp rule, exactly as
            // they were while the mouth was dry, and the old check simply had no
            // ramp inside the pond to trip over. What would be a regression is a
            // Passable that learned about water at all, and that is what this
            // says: the answer is the same with the pond in and with it out.
            var steps = new List<(Vector2I Cell, int Heading)>();
            foreach (Vector2I cell in pond)
            foreach (int heading in HexField.EdgeHeadings)
                steps.Add((cell, heading));
            var wetted = steps.Select(s => field.Passable(s.Cell, s.Heading))
                              .ToArray();
            bool[] hold = BoardMap.Bench.WaterFor(field.Columns, field.Rows);
            field.SetWater(null);
            var drained = steps.Select(s => field.Passable(s.Cell, s.Heading))
                               .ToArray();
            field.SetWater(hold);
            int swung = wetted.Where((yes, i) => yes != drained[i]).Count();
            Check("water is not a wall - flooding a cell changes no step onto it",
                swung == 0,
                $"{swung} of {steps.Count} steps round the pond answered "
                + "differently with the water in");
            Check("and the pond is reachable from the tanks' own end of the board",
                field.FindPath(new Vector2I(4, 2), pond[0]).Count > 0,
                $"no route from (4,2) to ({pond[0].X},{pond[0].Y})");

            // Both ways, which is the slip the grade's own note names: wading out
            // onto a bank is the harder half and capping only the way in is what
            // gets forgotten.
            Vector2I wet = pond[0];
            Vector2I dry = HexField.Step(wet, 90);
            foreach (int heading in HexField.EdgeHeadings)
            {
                Vector2I next = HexField.Step(wet, heading);
                if (field.InBounds(next) && !field.IsWater(next))
                    dry = next;
            }
            Check("entering the water costs and so does leaving it",
                field.IsWet(dry, wet) && field.IsWet(wet, dry),
                "one direction of the shoreline is free");
            Check("and dry ground either side of it costs nothing",
                !field.IsWet(new Vector2I(2, 2), new Vector2I(3, 2)),
                "a dry leg reports as wet");

            // The lower of the two caps and never their product: the beach at the
            // mouth of the pit is a leg that is both, and a product would invent a
            // third terrain there.
            MovementProfile medium = MovementProfile.Medium;
            Check("wading is slower than climbing, so the two are different ground",
                MovementProfile.WaterFraction < MovementProfile.GradeFraction
                && MovementProfile.WaterFraction > medium.CornerFraction,
                $"water {MovementProfile.WaterFraction:F2}, grade "
                + $"{MovementProfile.GradeFraction:F2}, crawl "
                + $"{medium.CornerFraction:F2}");
            Check("and a wet grade is the slower of the two, not the two multiplied",
                Math.Abs(Math.Min(medium.GradeSpeed, medium.WaterSpeed)
                         - medium.WaterSpeed) < 0.01
                && medium.GradeSpeed * MovementProfile.WaterFraction
                   < medium.WaterSpeed - 1.0,
                $"min is {Math.Min(medium.GradeSpeed, medium.WaterSpeed):F0}, "
                + $"product would be "
                + $"{medium.GradeSpeed * MovementProfile.WaterFraction:F0}");

            // Where it sits in the ladder of things lying on the board. Over the
            // marks because they are terrain and it lies on them, under the tanks
            // because a surface has one sort key and a tank standing in front of
            // the pond would lose to it - and under the selection ring, which is
            // the one thing here that is not ground at all. These are drawn by
            // priority and not by depth, so under the water is not dimmed by it
            // but painted out by it: measured, a tank parked in the ford kept 0 px
            // of its own ring.
            Check("the water sorts over the ground marks, under the ring and under every tank",
                Stage3D.RutOrder < Stage3D.WaterOrder
                && Stage3D.WaterOrder < Stage3D.RingOrder
                && Stage3D.RingOrder < 0,
                $"ruts {Stage3D.RutOrder}, water {Stage3D.WaterOrder}, ring "
                + $"{Stage3D.RingOrder}");

            // The cut is the shader's, and it has to be: a horizontal surface
            // crossing an upright billboard has half of itself in front of the
            // card, and one sort key cannot say so. Asserted on the shader source
            // rather than on a rendered pixel, the way the premultiplied blend
            // already is.
            Check("and the submerged half is tinted by the sprite's own shader",
                Stage3D.PaintShader.Contains("uniform sampler2D shore")
                && Stage3D.PaintShader.Contains("shore_cut.x / scale"),
                "the paint shader has no waterline in it, so the pond can only "
                + "tint the whole tank or none of it");

            // The height map, and what it is for. Asked of every tank rather
            // than of the field's own, so both halves are exercised by whatever
            // is on disk: a set that carries a map is judged on it, and a set
            // rendered before sprite_height.py existed is judged on falling
            // back to the groundline instead of losing its waterline.
            var mapped = new List<string>();
            var bare = new List<string>();
            string tallTale = "", underTale = "", deepTale = "", prowTale = "",
                   crownTale = "";
            int inked = 0, unanswered = 0;
            foreach (string tag in MovementProfile.Tags)
            {
                AtlasSet set = AtlasSet.Load(
                    AssetRoot.Sprites, tag, tag);
                if (set.Error.Length > 0)
                    continue;
                if (!set.HasHeights)
                {
                    bare.Add(tag);
                    if (set.Waterline(20.0f) is not null)
                        deepTale = $"{tag} builds a waterline with no map";
                    continue;
                }
                mapped.Add(tag);
                // A tank is not as tall as it is drawn: the drawn height is
                // height*cos(e) + depth*sin(e), and this number is only the
                // first term. A map claiming otherwise has been read with the
                // wrong range and would put the waterline off the top.
                if (set.HeightSpanPx <= 0.0 || set.HeightSpanPx >= set.Tile.Y)
                    tallTale = $"{tag} says one tank is {set.HeightSpanPx:0.0}px "
                               + $"of rise in a {set.Tile.Y}px tile";
                // The two numbers the ram's wedge is built from. The true
                // height has to stand above the drawn rise - the rise is that
                // height shortened by cos(elevation) - and a bow is either
                // unmeasurable (nought, the wedge keeps its declared slope) or
                // a fraction of the hull a glacis could actually be.
                if (set.HullTallPx <= set.HeightSpanPx
                    || set.HullTallPx >= set.Tile.Y)
                    prowTale = $"{tag} stands {set.HullTallPx:0.0}px tall "
                               + $"against {set.HeightSpanPx:0.0}px of rise";
                else if (set.HullBowPx > 0.0
                         && (set.HullBowPx < set.HullSpan * 0.05
                             || set.HullBowPx > set.HullSpan * 0.7))
                    prowTale = $"{tag} runs a {set.HullBowPx:0}px glacis on a "
                               + $"{set.HullSpan}px hull";
                else
                    Note($"prow: {tag} stands {set.HullTallPx:0.0}px, glacis "
                         + $"runs {set.HullBowPx:0}px of {set.HullSpan}px");
                // The crown the box grows over the wedge: a turret roof is
                // above the deck and not a tower - a reading outside that is a
                // misread sprite, and the box must keep its deck height. Atlas
                // pixels against atlas pixels, so no dial can move the band.
                if (set.TurretTallPx <= set.HullTallPx
                    || set.TurretTallPx > set.HullTallPx * 2.5)
                    crownTale = $"{tag} puts the turret roof at "
                                + $"{set.TurretTallPx:0.0}px over a "
                                + $"{set.HullTallPx:0.0}px hull";
                else
                    Note($"crown: {tag} turret roof at {set.TurretTallPx:0.0}px "
                         + $"over a {set.HullTallPx:0.0}px hull");
                set.Waterline(20.0f);
                int above = 0, moved = 0, rose = 0, fell = 0;
                var shallow = new int[set.Count * set.Tile.X];
                for (int f = 0; f < set.Count; f++)
                for (int x = 0; x < set.Tile.X; x++)
                {
                    int line = set.WaterlineAt(f, x);
                    shallow[f * set.Tile.X + x] = line;
                    int ground = set.GroundlineAt(f, x);
                    // Water stands *on* the tank, so its line is at or above
                    // the row where that column meets the ground - never below
                    // it, which would be a tank standing in a hole in the pond.
                    if (line >= 0 && ground >= 0 && line > ground)
                        above++;
                }
                set.Waterline(40.0f);
                for (int f = 0; f < set.Count; f++)
                for (int x = 0; x < set.Tile.X; x++)
                {
                    int now = set.WaterlineAt(f, x);
                    int had = shallow[f * set.Tile.X + x];
                    if (now == had)
                        continue;
                    moved++;
                    if (had < 0 || now < 0)
                        continue;
                    if (now < had)
                        rose++;
                    else
                        fell++;
                }
                if (above > 0)
                    underTale = $"{tag}: {above} columns put the water below "
                                + "the tank's own feet";
                if (moved == 0 || fell > 0)
                    deepTale = $"{tag}: deeper water moved {moved} columns, "
                               + $"{rose} up and {fell} down";
                // And what shallow water does to it, which is the case the flat
                // fallback was wrong about: a column whose own surface stands
                // above the plane has no answer, and in an inch of water that is
                // most of the hull. Counted against the groundline, which is what
                // says a column has a silhouette at all.
                set.Waterline(4.0f);
                for (int f = 0; f < set.Count; f++)
                for (int x = 0; x < set.Tile.X; x++)
                {
                    if (set.GroundlineAt(f, x) < 0)
                        continue;
                    inked++;
                    if (set.WaterlineAt(f, x) < 0)
                        unanswered++;
                }
            }
            Check("some tank carries a height map, and some may not",
                mapped.Count + bare.Count > 0,
                "no set loaded at all, so neither path was exercised");
            Check("and where there is one it is a tank's height, not a tile's",
                tallTale.Length == 0, tallTale);
            Check("and the wedge's two measures come out believable: the true "
                  + "height above the drawn rise, the glacis a fraction of the "
                  + "hull or honestly nought",
                prowTale.Length == 0, prowTale);
            Check("and the crown's measure too: the turret roof above the deck "
                  + "and no tower",
                crownTale.Length == 0, crownTale);
            Check("and the water it puts on a tank never stands below its feet",
                underTale.Length == 0, underTale);
            Check("and deeper water climbs the tank, in every column that moves",
                deepTale.Length == 0, deepTale);
            Check("and a set without one still has a groundline to fall back on",
                bare.All(t => AtlasSet.Load(
                    AssetRoot.Sprites, t, t)
                    .Groundline is not null),
                $"{string.Join(", ", bare)} would lose the waterline entirely");

            // <b>A column the map cannot answer is dry, and it used to be cut
            // flat.</b> The two tables fail for two reasons - see the shader's own
            // note on shore_flat - and the flat cut only leaves the groundline's
            // failure dry by accident of where the gun is drawn. On the height
            // map it drew a line at the cell centre's row straight across a hull
            // in an inch of water, which is the one row cut the per-column table
            // exists to abolish.
            Check("and a column the height map cannot answer is left dry, not cut flat",
                Stage3D.PaintShader.Contains("shore_flat < 0.0"),
                "the sprite falls back to a flat cut for a column the waterline "
                + "has no answer for, which tints and foams a column with no "
                + "water in it");
            // The other half, and it is the half that says the rule matters: the
            // unanswered column is not a rare one. Asserted as existing rather
            // than as a fraction, because the fraction is a fact about the art -
            // but it is printed, because it is the finding.
            Check("and shallow water leaves such columns, which is why that is not rare",
                inked == 0 || unanswered > 0,
                $"in 4px of water every one of {inked} inked columns has an "
                + "answer, so either the map or the scan has stopped meaning "
                + "what it says");
            if (inked > 0)
                Note($"waterline: 4px of water answers "
                         + $"{inked - unanswered} of {inked} inked columns "
                         + $"({100.0 * unanswered / inked:0}% dry)");

            // The table the waterline is cut against, and the three things about
            // it that decide whether it can be trusted: that it exists, that it
            // is the tank's own bottom rather than the frame's, and that it stays
            // inside the hull so the gun cannot be dipped in it.
            AtlasSet? art = field.Atlas;
            int spread = 0, aboveAnchor = 0, widest = 0;
            var atHeading = "";
            if (art?.Groundline is not null)
                for (int f = 0; f < art.Count; f++)
                {
                    int low = int.MaxValue, high = int.MinValue, wide = 0;
                    for (int x = 0; x < art.Tile.X; x++)
                    {
                        int row = art.GroundlineAt(f, x);
                        if (row < 0)
                            continue;
                        wide++;
                        low = Math.Min(low, row);
                        high = Math.Max(high, row);
                        if (row <= art.Anchor.Y)
                            aboveAnchor++;
                    }
                    widest = Math.Max(widest, wide);
                    if (wide > 0 && high - low > spread)
                    {
                        spread = high - low;
                        atHeading = $"frame {f}";
                    }
                }
            Check("the atlas carries a groundline to cut the waterline against",
                art?.Groundline is not null,
                "with none the cut falls back to one row for the whole sprite, "
                + "which is what put the tail dry in the pond the nose was under");
            Check("and it is the tank's own bottom, under the anchor everywhere",
                aboveAnchor == 0,
                $"{aboveAnchor} columns claim ground above the turret axis");
            Check("and it stays inside the hull, so the gun cannot be dipped",
                widest > 0 && widest <= art!.HullSpan + 8,
                $"{widest} columns carry ground against a {art?.HullSpan ?? 0}px "
                + "hull - a table that reaches the muzzle would wet it");
            Check("and it varies by more than a flat cut can express",
                spread >= 8,
                $"the worst heading spreads {spread}px ({atHeading}); under this "
                + "the per-column cut is a flat cut with extra machinery");

            int wooded = pond.Count(c => grove?.IsForest(c) ?? false);
            Check("nothing grows in the water", wooded == 0,
                $"{wooded} flooded cells are wooded");

            // The guard that is left has to be shown to bite, and the one that
            // went has to be shown to have gone: a refusal quietly still in place
            // reads exactly like a beach nobody thought to flood.
            bool twoLevels = false;
            var mask = BoardMap.Bench.WaterFor(field.Columns, field.Rows);
            Vector2I ramp = default;
            for (int q = 0; q < field.Columns && ramp == default; q++)
            for (int r = 0; r < field.Rows; r++)
                if (field.IsRamp(new Vector2I(q, r)))
                {
                    ramp = new Vector2I(q, r);
                    break;
                }
            var bad = (bool[])mask.Clone();
            bad[ramp.Y * field.Columns + ramp.X] = true;
            bool tookRamp = true;
            try { field.SetWater(bad); }
            catch (InvalidOperationException) { tookRamp = false; }
            Check("a ramp can be flooded - the water is flat and the slope crosses it",
                tookRamp,
                $"the pond was refused the ramp at ({ramp.X},{ramp.Y}), which is "
                + "the one cell that has to be half wet");
            // And one body is still one surface across the pair, which is what
            // measuring the top off the cell's floor buys. The ramp's own foot is
            // the flat cell to compare against, and it exists for every legal ramp
            // by the dead-end rule. Off TopAt instead the two come out half a
            // level apart - a waterfall along the edge a beach shares with its
            // own pond, which is the failure this replaced a refusal with.
            Vector2I sole = HexField.Step(ramp,
                HexField.Reverse(field.RampHeading(ramp)));
            float wetSlope = field.WaterTop(ramp), wetFoot = field.WaterTop(sole);
            Check("and its surface is the pond's own, not half a level over it",
                Mathf.Abs(wetSlope - wetFoot) < 0.01f,
                $"({ramp.X},{ramp.Y}) stands its water at {wetSlope:F2} and its "
                + $"foot ({sole.X},{sole.Y}) at {wetFoot:F2}, "
                + $"{Mathf.Abs(wetSlope - wetFoot):F2}px apart");
            // Put the board back before the rest of the topic reads it: the
            // refusal used to leave the field untouched by throwing, and now the
            // call succeeds.
            field.SetWater(mask);

            // A cell of the pit and the cell above it on the flat: adjacent, two
            // levels, which is a waterfall and not a pond.
            bad = (bool[])mask.Clone();
            Vector2I over = HexField.Step(pond[0], 90);
            for (int i = 0; i < 6 && field.LevelAt(over) == field.LevelAt(pond[0]);
                 i++)
                over = HexField.Step(pond[0], HexField.EdgeHeadings[i]);
            bad[over.Y * field.Columns + over.X] = true;
            try { field.SetWater(bad); }
            catch (InvalidOperationException) { twoLevels = true; }
            Check("and a pond on two levels is refused rather than made a fall",
                twoLevels,
                $"({over.X},{over.Y}) on {field.LevelAt(over)} was allowed beside "
                + $"({pond[0].X},{pond[0].Y}) on {field.LevelAt(pond[0])}");

            // The drawn surface, loaded here off the real folder against the real
            // tile, because what is being asserted is the loader's judgement of
            // the shipped file and a hand-built stand-in would assert nothing.
            // Skipped whole when there is no strip on disk - the same courtesy the
            // sounds get, and for the same reason: a bench with no art still shows
            // everything it claims to.
            WaterArt surf = art is null
                ? WaterArt.Load(AssetRoot.Water, default)
                : WaterArt.Load(AssetRoot.Water, art.HexRect);
            if (!surf.Any)
            {
                Note("  skip  water art: " + surf.Note);
            }
            else
            {
                // Two frames is the least that can be a loop. One is a picture,
                // and the flat colour is already a picture for less machinery -
                // so a one-frame strip is not a cheap surface, it is a surface
                // that quietly stopped being the thing it was added for.
                Check("the drawn water is a loop rather than a still",
                    surf.Frames > 1,
                    $"{surf.Frames} frame(s) in {surf.Frame.X}x{surf.Frame.Y}");

                // The camera check, restated as the thing that actually breaks: a
                // frame has to be the hexagon's own shape or the corners of the
                // mesh do not land on the corners of the picture. The sheet as
                // delivered is drawn at 36.4 degrees against the board's 30, so
                // this is the guard that squash exists to satisfy.
                float want = art!.HexRect.Size.Y / (float)art.HexRect.Size.X;
                float got = surf.Frame.Y / (float)surf.Frame.X;
                Check("and a frame is the tile's own hexagon, not the artist's",
                    Math.Abs(got - want) / want <= 0.02f,
                    $"the art is {got:F4} against the tile's {want:F4} - "
                    + $"{Math.Abs(got - want) / want:P1} out, see water_sheet.py");

                // A pond is one body of water, so it shows one moment of itself.
                // The per-cell phase this replaces came from the rule the clocks
                // obey - three tanks tremble on their own - and that rule is about
                // separate objects: desynchronised, two cells show different
                // moments of the same swell either side of a shared edge, so the
                // water draws every cell boundary instead of hiding it.
                // The phase is shared and the *state* is not, and the shader is
                // where those two part company. UV2 carries which cell this is,
                // which is the state's business; a phase offset per cell would be
                // the swell disagreeing with itself across every shared edge.
                Check("and the whole pond shows one moment of the same swell",
                    Stage3D.SwellShader.Contains("fract(phase) * run")
                    && !Stage3D.SwellShader.Contains("phase + UV2"),
                    "the surface takes a phase per cell, so its cells disagree "
                    + "across every edge they share");

                // The step is taken in the shader and not by rewriting the mesh:
                // Build is deliberately gated on the board changing at all, so a
                // surface that advanced its frames through its UVs would rebuild
                // the whole board sixty times a second.
                Check("and the frame steps in the shader rather than in the mesh",
                    Stage3D.SwellShader.Contains("b0 * frames + f0"),
                    "the surface shader does not step frames, so the loop can "
                    + "only move by the board being rebuilt");

                // The ladder, and which way up it is. Getting it backwards would
                // leave a tank ploughing a flat calm through breaking water,
                // which no number reports and which the loader would load happily.
                Check("and its bands climb from calm to breaking, in that order",
                    surf.Loop >= 2 && surf.Loop * WaterArt.Bands == surf.Frames
                    && surf.MeanOfBand(0).Luminance < surf.MeanOfBand(1).Luminance
                    && surf.MeanOfBand(1).Luminance < surf.MeanOfBand(2).Luminance,
                    $"{surf.Frames} frames in {WaterArt.Bands} bands, means "
                    + string.Join("/", Enumerable.Range(0, WaterArt.Bands)
                        .Select(b => surf.MeanOfBand(b).Luminance.ToString("F3"))));

                // Blending is a model, not a smoothing knob, and this is what
                // makes it one: at zero the mix term vanishes and what is drawn is
                // the floor frame and nothing else - the stepped loop exactly. A
                // blend written any other way would leave --hard-swell showing a
                // third thing that was never the pond.
                Check("and turning the blend off leaves exactly the stepped loop",
                    Stage3D.SwellShader.Contains("fract(t) * blend")
                    && Stage3D.SwellShader.Contains("floor(t)"),
                    "the surface does not fold its blend into the step, so off is "
                    + "not the loop it is meant to be judged against");

                // The tuned speed has to be on the right side of the number the
                // caption warns about, and the slider has to be able to reach the
                // wrong side - a dial that cannot show what too much looks like is
                // a poor dial, and one whose default is already past it is worse.
                double held = Stage3D.SwellPeriodDefault / surf.Frames * 60.0;
                Check("and one frame of it is held for less than a slideshow",
                    held <= Main.StepsAt || Stage3D.SwellBlendDefault,
                    $"a frame stands for {held:F0} screen frames against "
                    + $"{Main.StepsAt:F0}, and the blend that answers that is off");
                // said so: a frame is exactly the plate, so the keyed rim lies on
                // the frame's own edge and two cells meeting lay two fades on top
                // of each other. Five cells drew as three pieces with dry ground
                // between them. Measured where the mesh samples, after the bleed.
                Check("and it is opaque along the edge a neighbour is on",
                    surf.EdgeAlpha >= 0.99f,
                    $"the drawn boundary bottoms out at alpha {surf.EdgeAlpha:F2} "
                    + $"with {Stage3D.SwellBleed:F0}px of bleed - every shared "
                    + "edge is a strip of dry ground");

                // The flat fallback and the art have to be the same water. This
                // is what makes --flat-water an A/B about the texture: a fallback
                // in its own colour would make the toggle a colour swap wearing a
                // texture swap's name, and every measurement taken across it would
                // be measuring both.
                float off = Math.Max(Math.Abs(surf.Mean.R - Stage3D.Pond.R),
                    Math.Max(Math.Abs(surf.Mean.G - Stage3D.Pond.G),
                             Math.Abs(surf.Mean.B - Stage3D.Pond.B)));
                Check("and the flat colour under it is that surface's own average",
                    off <= 0.01f,
                    $"the fallback is {Stage3D.Pond.ToHtml(false)} against a "
                    + $"measured {surf.Mean.ToHtml(false)}, {off:F3} apart - "
                    + "--flat-water would be swapping the colour too");
            }

            // What the water does about the tanks. Built here on the real field,
            // and driven by cells and a flag rather than by a Vehicle - which is
            // the whole reason Swell.Note takes those: a tank cannot be built in
            // a check, and the interesting part is not about tanks anyway.
            var sea = new Swell { Field = field };
            Check("a pond nobody has driven into is calm all over",
                sea.Peak == 0.0f && pond.All(c => sea.StateAt(c) == 0.0f),
                $"peak {sea.Peak:F2} before anybody moved");

            // Driving through a cell stirs that cell. Its neighbour is the other
            // half of the claim: a chop that spread would be the pond reacting as
            // one body, which is what the shared phase says and what the state
            // deliberately does not.
            for (int i = 0; i < 30; i++)
            {
                sea.Note(0, pond[0], true);
                sea.Tick(1.0 / 60.0);
            }
            Check("and a tank driving through one stirs that cell, not its neighbour",
                sea.StateAt(pond[0]) > 0.9f * Swell.ChurnBand
                && sea.StateAt(pond[1]) == 0.0f,
                $"({pond[0].X},{pond[0].Y}) is {sea.StateAt(pond[0]):F2} and "
                + $"({pond[1].X},{pond[1].Y}) is {sea.StateAt(pond[1]):F2}");

            // Crossing into a cell breaks it, and higher than driving through it:
            // if the two landed on the same band there would be no splash, only a
            // chop that arrived sooner.
            float churned = sea.StateAt(pond[0]);
            sea.Note(0, pond[1], true);
            sea.Tick(1.0 / 60.0);
            Check("and crossing into one breaks it, above the chop it was at",
                sea.StateAt(pond[1]) > churned
                && sea.StateAt(pond[1]) >= Swell.SplashBand - 0.05f,
                $"the crossing reached {sea.StateAt(pond[1]):F2} against a chop of "
                + $"{churned:F2} and a break at {Swell.SplashBand:F0}");

            // Both ends, because leaving throws as much water as entering and a
            // shoreline is crossed by exactly one of them being dry.
            Check("and the cell it left breaks too",
                sea.StateAt(pond[0]) >= Swell.SplashBand - 0.05f,
                $"the cell behind is {sea.StateAt(pond[0]):F2}");

            // Exactly the band, not almost it. The splash used to be spent in the
            // same pass that read it, so the top of the ladder measured 1.96 of 2
            // and the four frames a crossing is drawn with were never once shown
            // without a quarter of the chop mixed into them.
            Check("and a crossing is worth the whole of the breaking band",
                Mathf.IsEqualApprox(sea.StateAt(pond[1]), Swell.SplashBand),
                $"{sea.StateAt(pond[1]):F4} of {Swell.SplashBand:F0}, so the "
                + "band is only ever reached through its neighbour");

            // The two ends are told apart, and on the loop rather than on the
            // band: dropping the left cell a band would say chop, which is a tank
            // driving through, not a tank climbing out.
            Check("and the cell left behind runs the milder half of that band",
                Mathf.IsEqualApprox(sea.SpanAt(pond[0]), Swell.LeavingSpan)
                && Mathf.IsEqualApprox(sea.SpanAt(pond[1]), 1.0f),
                $"left {sea.SpanAt(pond[0]):F2}, entered {sea.SpanAt(pond[1]):F2}"
                + " - the same span at both ends is one splash drawn twice");

            // And the surface honours it. The band picks which four frames; this
            // picks how many of the four, so a shader that stepped the whole loop
            // regardless would leave the two ends identical on screen while the
            // numbers said otherwise.
            // Asked as "the run comes out of the span", not as "both words are in
            // the file": a shader that reads the span and then steps the whole
            // band anyway contains every string a looser check would look for.
            // Written the loose way first, and a deliberately broken shader
            // passed it.
            Check("and the surface steps only as much of the loop as it is given",
                Stage3D.SwellShader.Contains("floor(frames * clamp(span[cell]")
                && Stage3D.SwellShader.Contains("fract(phase) * run"),
                "the surface runs the whole band whatever it is handed, so "
                + "leaving a cell looks exactly like entering it");

            // The foam is a detail on the band and never a second opinion about
            // it, and this is the arithmetic that makes that true rather than
            // intended: three octaves cannot sum past FoamCeiling, and a cell at
            // state nought faces FoamCut + 0.5. Add an octave or lift the cut
            // without coming here and calm water starts growing foam - which is
            // the pond saying a tank was somewhere no tank has been.
            Check("calm water cannot reach the foam's cutoff at all",
                Stage3D.FoamCeiling < Stage3D.FoamCut,
                $"noise tops out at {Stage3D.FoamCeiling:F3} against a cut of "
                + $"{Stage3D.FoamCut:F3} on still water");

            // And it is shaped by where the tank was, not only by which cell it
            // was in. A state is one number for a whole hexagon, so a mask made
            // from it alone is hexagonal however the noise is tuned - measured
            // on the picture: the raft filled its cell corner to corner while
            // the neighbour stayed clean, which is the water drawing the grid it
            // is on the board to hide. Two goes at the cutoff did not touch it,
            // because the cutoff was never what made it that shape.
            Check("and the foam is shaped by where the tank was, not by the cell",
                Stage3D.SwellShader.Contains("distance(world.xz, mark[cell])")
                && Stage3D.SwellShader.Contains("level *="),
                "the foam is cut by the cell's state alone, so it is hexagonal "
                + "whatever else is done to it");

            // And its reach dies inside the tile. Past that a raft runs to the
            // cell edge again and the mark has bought nothing.
            Check("and a raft dies out before the cell it sits in ends",
                Stage3D.FoamReach is > 0.0f and < 2.0f,
                $"reach {Stage3D.FoamReach:F2} of the tile's own circumradius");

            // And the state is what cuts it, so the foam can only ever agree with
            // the frame the strip already chose.
            Check("and what foam there is is cut by the same state the band is",
                Stage3D.SwellShader.Contains("state[cell] / max(bands - 1.0")
                && Stage3D.SwellShader.Contains("clumps(fuv) + level"),
                "the foam is drawn off something other than the cell's state, so "
                + "it is a second opinion about what the water is doing");

            // Every term multiplied by the level, which is the whole of what
            // makes off give the picture back. One line missing it and --no-foam
            // still tints the pond - and a claim of byte-identity nobody can see
            // failing is worse than no claim.
            int paid = 0;
            foreach (string line in Stage3D.SwellShader.Split('\n'))
                if (line.Contains("foam_rim") && line.Contains("* foam")
                    || line.Contains("foam_ink") && line.Contains("* foam")
                    || line.Contains("foam_lift") && line.Contains("* foam"))
                    paid++;
            Check("and turning it off takes every term of it away", paid == 3,
                $"{paid} of the 3 places foam is applied are multiplied by the "
                + "level, so nought is not the pond as it was");

            // In world space. Per-hex UVs would stamp the same raft into every
            // cell and lay its edges along the grid - the water drawing the cell
            // boundary instead of hiding it, which is the failure the shared
            // phase exists to avoid and would arrive by another door.
            Check("and the foam is laid in world space, not per cell",
                Stage3D.SwellShader.Contains("world.xz * foam_scale")
                && !Stage3D.SwellShader.Contains("UV * foam_scale"),
                "foam is laid out in the cell's own UVs, so every hex wears the "
                + "same raft and the grid shows through the water");

            // Which pond a plain run actually draws. Said first because every
            // assertion about the strip below is describing something nobody
            // sees once this is on, and a check whose answer goes nowhere is
            // indistinguishable from a check that is not there.
            Check("a default run draws the computed surface, not the strip",
                Stage3D.DeepDefault,
                "the strip is what comes up, so the computed surface is a mode "
                + "nobody is looking at");

            // The two facts it stands on, both measured before a line of it was
            // written: the depth texture works in Compatibility and already
            // holds the pit floor, and the tanks draw after the water so the
            // screen texture it bends has no tank in it.
            Check("and it reads the depth of the bottom and the screen behind it",
                Stage3D.DeepShader.Contains("hint_depth_texture")
                && Stage3D.DeepShader.Contains("hint_screen_texture"),
                "the computed surface samples neither, so it is a flat colour "
                + "with a noise on it");

            // Opaque, and that is the whole difference in kind from the strip.
            // The strip had to be see-through because showing the bottom was the
            // only way it could show it; this samples the bottom itself.
            Check("and it owns every pixel it covers, rather than blending over one",
                Stage3D.DeepShader.Contains("ALPHA = 1.0;"),
                "the computed surface is translucent, which is the strip's "
                + "bargain with more arithmetic on top");

            // Where it stops, and that it stops by hand. The depth test clips this
            // pass on the intersection of two planes, which is a straight segment -
            // the shoreline the ramps wore, read as one because it is one - and no
            // dial can move it. The same comparison made against zbuf gives the
            // same answer within a pixel of the line (measured: 724 columns
            // touched, median 2px, longest run 4) and can be offset.
            Check("and it clips itself against the bottom rather than by the depth test",
                Stage3D.DeepShader.Contains("depth_test_disabled")
                && Stage3D.DeepShader.Contains("if (bed + lap <= 0.0)")
                && Stage3D.DeepShader.Contains("discard;"),
                "the computed surface is clipped by the hardware, so its shore is "
                + "the straight segment two planes cross on");

            // And how far it is allowed to move it. Along the shore the bed does
            // not change; across it it changes at the ramp's own gradient, so a
            // field steeper than that crosses zero more than once and the water
            // comes apart into islands standing on dry sand. The crumple's two
            // octaves put that bound at about 0.05 of the feature size.
            //
            // <b>A band and not a ceiling, because both ends were measured.</b>
            // Held under the bound the line came back as one bow per ramp and, on
            // the flatter ramps, as the segment it replaced; at twice it drew hard
            // teeth. The film buys the middle - a fold arrives as a tongue instead
            // of a tooth - so a number outside this range is one of the two
            // pictures that were rejected rather than a taste.
            //
            // Asked of the board in hand, because the grade is a dial and the
            // answer is not one number: 1.05 here, 1.48 on the tank bench, which
            // keeps HexField's own 0.25 and is where the picture was judged.
            float lapGrade = 2.6f * Stage3D.LapWave.Y * Stage3D.LapWave.X;
            float bedGrade = (float)field.StepGrade * field.Squash;
            float lapOver = lapGrade / Mathf.Max(bedGrade, 1e-6f);
            Check("and the lapping is past the bound of a hard cut, inside the film's",
                lapOver > 1.0f && lapOver < 2.0f,
                $"the lap's gradient is {lapOver:0.00} of the ground's - "
                + (lapOver <= 1.0f
                    ? "under one it reads as the straight segment again"
                    : "over two it comes apart into teeth"));

            // Its own clock, for the reason the foam has one.
            string deepCode = string.Join('\n',
                Stage3D.DeepShader.Split('\n').Select(l => l.Split("//")[0]));
            Check("and it runs on the bench's clock too",
                !deepCode.Contains("TIME") && deepCode.Contains("uniform float time"),
                "the computed surface reads the wall clock, so no two captures "
                + "of the pond agree");

            // And its foam obeys the same two things the strip's does. Two
            // surfaces that disagree about when water foams would be two answers
            // to one question, and only one of them is ever on screen.
            Check("and its foam is cut by the same state and the same mark",
                Stage3D.DeepShader.Contains("state[cell] / max(bands - 1.0")
                && Stage3D.DeepShader.Contains("distance(world.xz, mark[cell])"),
                "the computed surface foams on its own terms, so the two ponds "
                + "disagree about what a tank does to water");

            // And it runs on the clock the bench hands it. TIME is the wall
            // clock, and --capture pins the step to 1/60 precisely so two runs
            // can be compared - read it in here and every capture of the pond
            // comes out different, so any A/B taken near the water measures the
            // hour it was taken at rather than the thing it was aimed at. Caught
            // that way round: the first cut of this used TIME, and two captures
            // on identical flags came back with different hashes.
            // Asked of the code and not of the file: the paragraph above this in
            // the shader has to name TIME to say why it is not used, and a plain
            // search on the source calls that a failure. Comments stripped first.
            string swellCode = string.Join('\n',
                Stage3D.SwellShader.Split('\n')
                    .Select(l => l.Split("//")[0]));
            Check("and the foam runs on the bench's clock, not the wall's",
                !swellCode.Contains("TIME")
                && swellCode.Contains("foam_time"),
                "the surface reads TIME, so no two captures of the water agree "
                + "and every A/B near it measures when it was taken");

            // Denser than water but not a lid on it: a ford shows its bottom, and
            // that is what makes it a ford rather than a hole.
            Check("and foam hides the bottom without sealing it over",
                Stage3D.FoamLift is > 0.0f and < 1.0f,
                $"lift {Stage3D.FoamLift:F2} - at one the pond goes opaque where "
                + "it foams and the ford stops being one");

            // The shoreline. Asked of the board rather than of the view, and
            // that is the whole of the change: the old edge was "how soon does
            // the eye hit the bottom", which is real foam under a wall standing
            // behind the water and nothing at all where the bank is on the near
            // side. That is why the ramp and the near shore had none.
            var hex = new Vector3[6];
            for (int k = 0; k < 6; k++)
            {
                float turn = Mathf.Pi / 3.0f * k;
                hex[k] = new Vector3(Mathf.Cos(turn), 0.0f, Mathf.Sin(turn));
            }
            // The hexagon those edges belong to, worked out from the corners the
            // mesh was built from rather than from an angle: corner order is
            // whoever built it business and the camera squashes it besides.
            (Vector2[] outward, float[] stand, float inradius) = Stage3D.Rim(hex);
            bool sane = true;
            for (int k = 0; k < 6; k++)
            {
                // Asked the way the shader asks it, and that matters: a normal
                // and an offset that are each reasonable on their own but do not
                // belong to the same line put the band anywhere. So the edge's
                // own midpoint has to come out at nought distance from it, and
                // the middle of the cell at the inradius.
                Vector3 span3 = hex[k] + hex[(k + 1) % 6];
                var mid = new Vector2(span3.X, span3.Z) * 0.5f;
                sane &= Mathf.Abs(outward[k].Length() - 1.0f) < 1e-4f
                        && stand[k] > 0.0f
                        && Mathf.Abs(stand[k] - inradius) < 1e-3f
                        && Mathf.Abs(stand[k] - mid.Dot(outward[k])) < 1e-3f;
            }
            Check("a cell's edges are found from its corners, not from an angle",
                sane && Mathf.Abs(inradius - Mathf.Sqrt(3.0f) * 0.5f) < 1e-3f,
                $"inradius {inradius:F3} against 0.866 - a normal that is not "
                + "unit, does not point out, or does not match its fellows is a "
                + "shore band of a different width on every edge, and a "
                + "circumradius here is the corner direction mistaken for the "
                + "edge one");

            // And the same hexagon wound the other way gives the same answer.
            // Which way the perpendicular of a segment points depends on which
            // way round the segment was handed over, so without the orientation
            // step every normal here would point inward and every shore band
            // would be measured from outside the cell - a failure this board's
            // own winding cannot show, because it happens to be the one that
            // comes out right.
            var back = new Vector3[6];
            for (int k = 0; k < 6; k++)
                back[k] = hex[5 - k];
            (Vector2[] mirror, float[] far, float span) = Stage3D.Rim(back);
            bool same = Mathf.Abs(span - inradius) < 1e-4f;
            for (int k = 0; k < 6; k++)
                same &= far[k] > 0.0f && Mathf.Abs(far[k] - span) < 1e-3f
                        && mirror[k].Length() > 0.9f;
            Check("and a hexagon wound the other way gives the same edges",
                same, $"inradius {span:F3} against {inradius:F3} - the normals "
                      + "follow the winding, so a board that numbers its corners "
                      + "the other way measures its shores from outside");

            // The board's own pond, and this is the measurement the whole change
            // rests on. Carried on the vertices, a shore had to be marked on the
            // corners so the two triangles sharing one agreed - and a corner
            // belongs to two edges, so one dry edge wet the ends of both its
            // neighbours. Said per edge it does not.
            //
            // <b>Strictly more corners than edges, and it used to be all of
            // them.</b> That was true of a five-cell pond where no cell had more
            // than two water neighbours; flooding the mouth put a cell inside the
            // pond, whose corners the spread does not reach, and the check failed
            // on a pond that shows the difference perfectly well. The statement
            // was never "every corner" - it is that the corner rule over-marks.
            int edged = 0, cornered = 0;
            foreach (Vector2I pool in field.WaterCells)
            {
                bool[] shore = Stage3D.Shoreline(field, pool, hex, 1.0f, 1.0f);
                var ends = new bool[6];
                for (int k = 0; k < 6; k++)
                    if (shore[k])
                    {
                        edged++;
                        ends[k] = true;
                        ends[(k + 1) % 6] = true;
                    }
                foreach (bool e in ends)
                    if (e)
                        cornered++;
            }
            int all = field.WaterCells.Count * 6;
            Check("and the board's pond is shore at every corner but not at "
                  + "every edge",
                field.WaterCells.Count > 0 && cornered > edged && edged < all,
                $"{edged} edges and {cornered} corners of {all} - if these agree "
                + "the pond has stopped being the case that showed the "
                + "difference, and this check has stopped saying anything");

            // On the board's own field, because a HexField built bare has no
            // tile size and every neighbour then sits at the same anchor - all
            // six headings match one edge and the answer is one. The relief
            // comes off first so the guards will take a made-up mask; both go
            // back below.
            var wasWater = BoardMap.Bench.WaterFor(field.Columns, field.Rows);
            field.SetWater(null);
            field.SetRelief(null, null);
            var middle = new Vector2I(2, 2);

            int lit = 0;
            foreach (bool b in Stage3D.Shoreline(field, middle, hex, 1.0f, 1.0f))
                if (b)
                    lit++;
            Check("a cell with dry ground all round it is shore on every edge",
                lit == 6, $"{lit} of 6 - six headings that do not resolve to six "
                          + "different edges is the matching being wrong");

            var flood = new bool[field.Columns * field.Rows];
            for (int i = 0; i < flood.Length; i++)
                flood[i] = true;
            field.SetWater(flood);
            lit = 0;
            foreach (bool b in Stage3D.Shoreline(field, middle, hex, 1.0f, 1.0f))
                if (b)
                    lit++;
            Check("and one in open water is shore on none of them", lit == 0,
                $"{lit} edges marked in the middle of a pond");

            // One dry neighbour is one edge and nothing else. Counted rather
            // than named, because which edge is which is the hexagon builder's
            // business - the matching goes by direction for exactly that reason,
            // and a test that named them would assert the very assumption the
            // code refuses to make. Two here is the old answer, which is what
            // put foam along the edges either side of the one that was dry.
            flood[HexField.Step(middle, HexField.EdgeHeadings[0]).Y * field.Columns
                  + HexField.Step(middle, HexField.EdgeHeadings[0]).X] = false;
            field.SetWater(flood);
            lit = 0;
            foreach (bool b in Stage3D.Shoreline(field, middle, hex, 1.0f, 1.0f))
                if (b)
                    lit++;
            Check("and a single dry neighbour lights exactly one edge",
                lit == 1, $"{lit} edges for one dry neighbour");

            field.SetRelief(BoardMap.Bench.LevelsFor(field.Columns, field.Rows),
                            BoardMap.Bench.RampsFor(field.Columns, field.Rows));
            field.SetWater(wasWater);

            // The flank under the free edges, and the two things it has to be
            // told apart from. Every edge that earns one is a shore - the water
            // stops there - but not every shore earns one, and the far bank is
            // why: there the ground stands a whole level over the surface and it
            // is the bank's own wall the water meets, so a flank would be a sheet
            // of water drawn in the sand's plane and fighting it for the depth
            // test. Strictly fewer, and the day the two agree the far bank has
            // started being drawn.
            int shored = 0, freed = 0;
            bool inside = true;
            foreach (Vector2I pool in field.WaterCells)
            {
                bool[] shore = Stage3D.Shoreline(field, pool, hex, 1.0f, 1.0f);
                bool[] open = Stage3D.Freeboard(field, pool, hex, 1.0f, 1.0f);
                Vector2I[] beside = Stage3D.Beside(field, pool, hex, 1.0f, 1.0f);
                for (int k = 0; k < 6; k++)
                {
                    if (shore[k])
                        shored++;
                    if (!open[k])
                        continue;
                    freed++;
                    inside &= shore[k];
                }
            }
            Check("every edge the pond wants a flank on is a shore of it",
                freed > 0 && inside,
                $"{freed} free edges, and one of them is not a shore - the flank "
                + "and the foam would then be drawn along different edges of the "
                + "same water");
            Check("and not every shore wants one, because the far bank holds the "
                  + "water in",
                freed < shored,
                $"{freed} of {shored} shore edges - all of them means a flank is "
                + "being stood in the far bank's own plane, where it has nothing "
                + "to be the side of");
            // And the one that decides whether the test is about the surface or
            // about the cell: a dry ramp at the pond's own level. A flank asked
            // for on "the neighbour is a level down" misses it entirely, and the
            // beach keeps the floating sheet the flank exists to close.
            //
            // <b>Built here rather than read off the board, because the board no
            // longer has one.</b> The pit's mouth was that cell and it is under
            // water now - which is the point of letting a ramp be flooded at all
            // - so the witness has to be a board that cannot be authored away.
            // The same reason BoardMap.Flood's pair is built rather than found.
            //
            // A trench: the floor at -1 with the water on it, a dry ramp beside
            // it also at -1 rising to the rim at 0, and the ramp's five other
            // neighbours taken down to -1 as well, because SetRelief wants a ramp
            // to have exactly one neighbour a level above it.
            var bed = new int[field.Columns * field.Rows];
            var tilt = new bool[field.Columns * field.Rows];
            var trench = new bool[field.Columns * field.Rows];
            var wetRamp = new Vector2I(5, 3);
            Vector2I wetFloor = HexField.Step(wetRamp, 270);
            int Ix(Vector2I c) => c.Y * field.Columns + c.X;
            bed[Ix(wetRamp)] = -1;
            tilt[Ix(wetRamp)] = true;
            foreach (int h in HexField.EdgeHeadings)
            {
                Vector2I side = HexField.Step(wetRamp, h);
                if (h != 90 && field.InBounds(side))
                    bed[Ix(side)] = -1;
            }
            trench[Ix(wetFloor)] = true;
            field.SetRelief(bed, tilt);
            field.SetWater(trench);
            bool[] free = Stage3D.Freeboard(field, wetFloor, hex, 1.0f, 1.0f);
            Vector2I[] round = Stage3D.Beside(field, wetFloor, hex, 1.0f, 1.0f);
            bool onLevel = field.IsRamp(wetRamp)
                           && field.LevelAt(wetRamp) == field.LevelAt(wetFloor);
            bool lipped = false;
            for (int k = 0; k < 6; k++)
                lipped |= free[k] && round[k] == wetRamp;
            Check("and a dry ramp at the pond's own level earns one too",
                onLevel && lipped,
                onLevel
                    ? "the flank is asked for on \"the neighbour is a level "
                      + "down\" rather than on \"its ground is under the "
                      + "surface\", so a beach keeps its floating sheet"
                    : $"the trench did not build: ({wetRamp.X},{wetRamp.Y}) is "
                      + $"{(field.IsRamp(wetRamp) ? "level " + field.LevelAt(wetRamp)
                                                  : "not a ramp")}");
            field.SetRelief(BoardMap.Bench.LevelsFor(field.Columns, field.Rows),
                            BoardMap.Bench.RampsFor(field.Columns, field.Rows));
            field.SetWater(wasWater);
            // Three of six face the eye, which is the ground prisms' arithmetic
            // and the reason theirs need no list. Here the cull is needed rather
            // than saved: a flank on a far edge is behind the surface's own
            // hexagon, but it is opaque and writes depth, so the surface would
            // read it as the bottom, find the water paper-thin, and lay foam
            // along the whole of that edge.
            var toEye = new Vector3(0.0f, field.Squash, field.RiseFactor);
            int seen = 0;
            foreach (Vector2 out_ in outward)
                if (new Vector3(out_.X, 0.0f, out_.Y).Dot(toEye) > 0.0f)
                    seen++;
            Check("and half a cell's flanks face the eye and half stand behind "
                  + "its own surface",
                seen == 3,
                $"{seen} of 6 edges face the camera - the cull is either taking "
                + "everything, which puts a false bottom under the far shore, or "
                + "nothing, which leaves the near one open");

            // And the surface reads it. The band comes off the mesh, not off the
            // depth of the water in front of the eye.
            Check("and the surface takes its shore off the board, not the view",
                Stage3D.DeepShader.Contains("rim_d[k] - dot(off, rim_n[k])"),
                "the shoreline is not being measured against the cell's own "
                + "edges, so it is either back on the view ray - there on the "
                + "far bank and missing on the near one - or back on the "
                + "corners, where one dry edge wets the two beside it");
            Check("and it says so per edge, so a corner cannot wet its "
                  + "neighbours",
                !Stage3D.DeepShader.Contains("beach, UV2.y"),
                "the band is still interpolated off the vertices");

            // Where the ground is under a *point* of it, which is what anything
            // laid across a whole cell has to ask before it can pick a height at
            // all. A ramp's face tilts a level across itself, so the tank's own
            // height is not the ground under the far half of a cell-sized shape:
            // a flat hexagon laid at it sank into the face uphill and the face hid
            // that half - measured, 922 px of selection ring in a 218x53 box where
            // a flat board draws 1482 in 218x96. The ring takes the highest of
            // these, which is the one height nothing under it can be in front of.
            Vector2I slope = new(-1, -1);
            for (int q = 0; q < field.Columns && slope.X < 0; q++)
            for (int r = 0; r < field.Rows && slope.X < 0; r++)
                if (field.IsRamp(new Vector2I(q, r)))
                    slope = new Vector2I(q, r);
            if (slope.X >= 0 && field.Atlas is not null)
            {
                Vector2 mid = field.FlatAnchor(slope) + field.CentreOffset;
                float[] top = field.TopCorners(slope);
                // The ring's own corners, off the shape it is actually cut to -
                // a check that reached for a radius of its own would stop being
                // about the ring the moment the ring moved.
                Vector2[] hoop = SelectionRing.Outline(
                    field.Atlas.HexRect.Size.X, field.Atlas.HexRect.Size.Y,
                    Stage3D.Ring);
                float low = float.PositiveInfinity, high = float.NegativeInfinity;
                foreach (Vector2 point in hoop)
                {
                    float at = field.TopAtPoint(mid + point);
                    low = Math.Min(low, at);
                    high = Math.Max(high, at);
                }
                Check("a ramp answers a shape laid across it with a spread of "
                      + "heights, not one",
                    high - low > field.Lift * 0.5f,
                    $"the ring spans {high - low:F1}px of height across a face "
                    + $"that rises {field.Lift:F1} - no spread here and the "
                    + "highest of them is the tank's own height, which is what "
                    + "buried the uphill half");
                // And what it reads there is the face's own plane, sampled along
                // each corner ray rather than at the corner itself: a corner is
                // shared by three cells and a ramp's high corner stands a level
                // over the flat one touching it, so the point is genuinely two
                // heights and which one comes back is the rounding's business.
                // The band never asks there - it is inset to 0.86 - and neither
                // does this.
                //
                // The two corner tables start at opposite corners, the ring's
                // outline opening at (-w/2, 0) where TopCorners opens at
                // (+w/2, 0), so the index is rotated by three rather than shared.
                float worst = 0.0f;
                Vector2[] ledge = SelectionRing.Outline(
                    field.Atlas.HexRect.Size.X, field.Atlas.HexRect.Size.Y, 0.9f);
                for (int k = 0; k < 6; k++)
                {
                    float want = field.TopAt(slope)
                        + 0.9f * (top[(k + 3) % 6] - field.TopAt(slope));
                    worst = Math.Max(worst,
                        Math.Abs(field.TopAtPoint(mid + ledge[k]) - want));
                }
                Check("and the height it reads there is that corner's own",
                    worst < 0.01f && Math.Abs(field.TopAtPoint(mid)
                                              - field.TopAt(slope)) < 0.01f,
                    $"worst corner off by {worst:F3}px, centre off by "
                    + $"{Math.Abs(field.TopAtPoint(mid) - field.TopAt(slope)):F3}");
                // Asked of a named cell it keeps that cell's plane going, which
                // is what a shape a cell wide needs: the ring overhangs its own
                // cell the moment the tank is between two, and a corner that
                // took the ground it happens to be over would drop a level onto
                // the flat beside a ramp and stop being part of a hexagon.
                Vector2I beside = HexField.Step(slope, HexField.EdgeHeadings[0]);
                if (field.InBounds(beside) && !field.IsRamp(beside))
                {
                    Vector2 apart = field.FlatAnchor(beside) + field.CentreOffset;
                    Check("and a named cell's face carries on past its own edge",
                        Math.Abs(field.TopOn(slope, apart)
                                 - field.TopAtPoint(apart)) > 1.0f
                        && Math.Abs(field.TopAtPoint(apart)
                                    - field.TopAt(beside)) < 0.01f,
                        "the plane of one cell read over the next gives the next "
                        + "cell's own height, so naming the cell buys nothing and "
                        + "the ring is back to following whatever it is over");
                    // Which is also why the band has to be clamped up to the
                    // ground on its low side: carried on, the face passes under
                    // the ground out there, and ground over the mark is ground
                    // that swallows it. See Stage3D.Lie, where that clamp is
                    // refused to cells standing over the tank's own - those are
                    // in front of the mark and are meant to cover it.
                    Vector2I foot = HexField.Step(
                        slope, HexField.Reverse(field.RampHeading(slope)));
                    if (field.InBounds(foot))
                    {
                        Vector2 down = field.FlatAnchor(foot) + field.CentreOffset;
                        Check("and past the low edge it passes under the ground "
                              + "out there",
                            field.TopOn(slope, down) < field.TopAtPoint(down)
                                                       - 1.0f,
                            $"{field.TopOn(slope, down):F1} against ground at "
                            + $"{field.TopAtPoint(down):F1} - nothing to clamp "
                            + "means the clamp is untested, not unneeded");
                    }
                }
                // Continuous into the next cell, which is what lets a corner
                // stray off this face and still land on the ground: the two
                // planes agree along the edge they share, and the ring at a
                // cell boundary is answered by whichever cell claims the point.
                Vector2I above = HexField.Step(slope, field.RampHeading(slope));
                if (field.InBounds(above))
                {
                    Vector2 seam = (mid + field.FlatAnchor(above)
                                    + field.CentreOffset) * 0.5f;
                    Check("and it is the same height read from either side of a "
                          + "shared edge",
                        Math.Abs(field.TopAtPoint(seam)
                                 - field.EdgeTop(slope, above)) < 0.01f,
                        $"{field.TopAtPoint(seam):F2} at the seam against "
                        + $"{field.EdgeTop(slope, above):F2} - a step here is a "
                        + "step in anything laid across it");
                }
                // And the cell being climbed onto is never in front of the mark,
                // however much it stands over the one being left. A ramp is half
                // a level up by TopAt, so the plain "higher ground covers it"
                // test called it a wall and took the uphill half of the ring with
                // it - measured on the climb up column 3, 1084 px of ring in a
                // 218x69 box where a whole one draws 1482 in 218x96.
                //
                // Both halves are asserted, and the first is the one that keeps
                // this honest: without it the exemption could be excusing a cell
                // that was never going to occlude anything.
                Vector2I under = HexField.Step(
                    slope, HexField.Reverse(field.RampHeading(slope)));
                if (field.InBounds(under))
                {
                    // Early in the step onto it, which is where it hurts: the
                    // seat is still nearly the flat ground behind.
                    float toe = Mathf.Lerp(field.TopAt(under),
                                           field.TopAt(slope), 0.1f);
                    Check("and a ramp climbed onto is not a wall in front of the "
                          + "mark",
                        field.TopAt(slope) > toe + 0.01f
                        && !Stage3D.Covers(slope, under, slope,
                                           field.TopAt(slope), toe),
                        $"a ramp at {field.TopAt(slope):F1} over a seat of "
                        + $"{toe:F1} - either it never stood over the tank, so "
                        + "this proves nothing, or it still covers the half of "
                        + "the ring lying on it");
                    // And the exemption is exactly two cells wide: anything to
                    // the side keeps the occlusion the rule is for.
                    Vector2I flank = HexField.Step(slope,
                        HexField.EdgeHeadings[2]);
                    Check("and it stays a wall when it is not being driven onto",
                        Stage3D.Covers(flank, under, slope,
                                       field.TopAt(slope), toe),
                        "a cell the tank is on neither side of is exempt too, so "
                        + "nothing on the board can cover the ring any more");
                }
                // A flat cell has nothing to interpolate and must say so, or
                // every mark on the board starts wobbling for no reason.
                Vector2I plain = new(0, 0);
                Vector2 plainAt = field.FlatAnchor(plain) + field.CentreOffset;
                Check("and a flat cell is one height all over",
                    !field.IsRamp(plain)
                    && Math.Abs(field.TopAtPoint(plainAt + hoop[0])
                                - field.TopAtPoint(plainAt + hoop[3])) < 0.01f,
                    "opposite corners of a level cell disagree about where the "
                    + "ground is");
            }

            // And in a ford the ground is the bed, water or no water. A wading
            // tank stands on the bottom and the mark says which cell it stands
            // on, so it lies there and the pond is drawn under it - which is the
            // whole of why the ladder above puts the ring over the water: under
            // the surface the band was not dimmed but painted out, measured at
            // 0 px in the ford against 1482 on the same cell with --no-water.
            if (field.WaterCells.Count > 0 && field.Atlas is not null)
            {
                Vector2I pool = field.WaterCells[0];
                Vector2 pondAt = field.FlatAnchor(pool) + field.CentreOffset;
                Check("a mark laid in a ford lies on the bed, not on the surface",
                    Math.Abs(field.TopAtPoint(pondAt) - field.TopAt(pool)) < 0.01f
                    && field.TopAtPoint(pondAt) < field.WaterTop(pool)
                    && Stage3D.WaterOrder < Stage3D.RingOrder,
                    $"{field.TopAtPoint(pondAt):F1} against a bed at "
                    + $"{field.TopAt(pool):F1} and a surface at "
                    + $"{field.WaterTop(pool):F1} - and it is only visible down "
                    + "there because the water sorts under it");
                // And it is a decal, not an overlay: a hex standing over the one
                // the tank is on is in front of the mark and covers it. What it
                // must not be covered by is ground it merely dips under at the
                // edges, which is Stage3D.Lie's clamp and not the depth test's
                // business - stood off the buffer altogether, the band came out
                // painted up the walls of its own pit.
                Check("and the ring is a decal, so higher ground covers it",
                    Stage3D.RingTakesDepth,
                    "the mark ignores the depth buffer, so it is drawn over every "
                    + "hex standing above the one the tank is on");
                // And the nudge that carries it clear of the water's own side is
                // capped by whatever ground stands over that bed, so it cannot
                // carry it clear of the beach as well. Asked of the pond cell
                // beside the pit's mouth, which is the one that showed it: half a
                // level of ramp is plenty to be in front of, and the same tank on
                // dry ground - with no nudge to spend - had that half cut.
                var ring = new Vector3[6];
                for (int k = 0; k < 6; k++)
                {
                    float turn = Mathf.Pi / 3.0f * k;
                    ring[k] = new Vector3(Mathf.Cos(turn), 0.0f, Mathf.Sin(turn));
                }
                Vector2I mouth = new(7, 4);
                Vector2I beside = new(8, 5);
                if (field.InBounds(mouth) && field.InBounds(beside)
                    && field.IsWater(beside) && !field.IsWater(mouth))
                {
                    float[] stands = Stage3D.Overlook(
                        field, beside, ring, 1.0f, 1.0f);
                    Vector2I[] next = Stage3D.Beside(
                        field, beside, ring, 1.0f, 1.0f);
                    float lip = 0.0f, pool2 = -1.0f;
                    for (int k = 0; k < 6; k++)
                    {
                        if (next[k] == mouth) lip = stands[k];
                        if (field.IsWater(next[k]) && next[k] != beside)
                            pool2 = Math.Max(pool2, stands[k]);
                    }
                    Check("and the water is not allowed to carry the mark in "
                          + "front of the beach",
                        lip > 0.0f && pool2 == 0.0f,
                        $"the mouth stands {lip:F1}px over this bed and the "
                        + $"deepest water neighbour {pool2:F1} - the first is what "
                        + "caps the nudge, and the second must cap nothing or "
                        + "the pond stops clearing its own side");
                }
            }

            // Every foam edge on the board is one field, and the two shaders that
            // draw them get it as the same text. Asked of the *formatted* source
            // and not of the constants, because that is what the driver compiles:
            // a shader that stopped taking the shared module would still name
            // crumple and shred in its own copy, and a copy is the whole failure.
            string litWater = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                Stage3D.DeepShader, 8, 0.9f, 0.5f, 0.5f, 8, 64, 256,
                Stage3D.FoamEdge);
            string litPaint = string.Format(Stage3D.PaintShader, "",
                                            Stage3D.FoamEdge);
            Check("and the water's edge is one field, shared as one text",
                litWater.Contains(Stage3D.FoamEdge)
                && litPaint.Contains(Stage3D.FoamEdge),
                "the surface and the sprite carry their own copies of the field "
                + "that shapes a foam edge, so the half of the waterline on the "
                + "armour and the half on the water beside it drift apart at the "
                + "one place they are seen touching");
            // Displacement and tearing are two jobs and both halves do both.
            // Tearing alone leaves a band where it was and only punches holes in
            // it, which is what drew hexagons along the shore; displacing alone
            // gives an even line that reads as piping.
            Check("and every edge is both pushed and torn by it",
                litWater.Contains("crumple(") && litWater.Contains("shred(")
                && litPaint.Contains("crumple(") && litPaint.Contains("shred("),
                "one of the two edges is only dimmed or only moved, so the shore "
                + "and the collar are not the same material");
            // And the push is read on the line rather than at the fragment. At
            // the fragment it varies across the band as well as along it, so the
            // band's width becomes a function of the field and folds into hard
            // teeth once the gradient reaches one - measured at 0.9 on the hull's
            // own band before this was moved.
            Check("and it is read on the line, so no dial can fold the band",
                Stage3D.PaintShader.Contains("vec2(tile.x, line)")
                && Stage3D.DeepShader.Contains("vec2(px.x, line)"),
                "a foam band is displaced by the field sampled under the "
                + "fragment, so it pinches where the gradient is steep and comes "
                + "back as a row of teeth rather than a shore");

            // The waterline round a hull is the tank's own outline, and it is
            // the same table the sprite's own waterline is cut by - which is
            // what makes them one measurement rather than two that agree today.
            Check("and the waterline round a hull is the tank's own outline",
                Stage3D.DeepShader.Contains("hull_line[lo]")
                && Stage3D.DeepShader.Contains("int lo = who * ")
                && Stage3D.PaintShader.Contains("uniform sampler2D shore"),
                "the collar is a shape drawn round the tank rather than the "
                + "shape of it, so it stands off the nose and cuts the guards");
            // And it stands where the water stands, not where the tank touches
            // the bottom. The shape is the outline's and the height is the
            // waterline's, and taking both from the table drew the two halves of
            // one line a whole depth apart - measured at 29 board px of water,
            // which is most of what is drawn of a tank's running gear.
            Check("and it stands at the waterline, not at the tank's feet",
                Stage3D.DeepShader.Contains("(hem ? 0.0 : hull_dip[who])"),
                "the collar on the water is drawn where the tank meets the "
                + "bottom, so it and the line on the armour are one water "
                + "deep apart");
            // And the half of that line that lies on the tank is drawn by the
            // tank, because those pixels belong to the sprite and to nothing
            // else: the pond is behind it, and a surface drawn before an opaque
            // one cannot show through it. This is the same waterline the tint is
            // cut at - one expression, so they cannot part.
            string paint = string.Join('\n',
                Stage3D.PaintShader.Split('\n').Select(l => l.Split("//")[0]));
            Check("and the foam at the hull is drawn by the hull, at the same "
                  + "line the tint is cut at",
                paint.Contains("float line = bottom;")
                && paint.Contains("tile.y > line")
                && paint.Contains("tile.y - (line"),
                "the sprite either draws no foam at its waterline or draws it "
                + "at a line of its own, which is a second opinion about where "
                + "the water is");
            Check("and it goes out with the pond's own foam",
                paint.Contains("foam_lap.a"),
                "a tank keeps a white collar in water that has stopped foaming");
            Check("and it runs on the bench's clock, like every other foam",
                !paint.Contains("TIME") && paint.Contains("lap_time"),
                "the line at the hull reads the wall clock, so no two captures "
                + "of a wading tank agree");
            // Narrower than the one on the water, and the reason is what it is
            // drawn on: a wide band here is not foam, it is a repaint.
            Check("and the line on the paint is narrower than the one on the "
                  + "water",
                Stage3D.LapBand < Stage3D.HullBand,
                $"{Stage3D.LapBand:F1}px on the hull against "
                + $"{Stage3D.HullBand:F1}px on the water");

            // Off the atlas rather than re-derived: a set that could not be
            // measured has no line to draw and must draw none.
            Check("and the line it follows is the one the atlas measured",
                field.Atlas is null || field.Atlas.Groundline is null
                || field.Atlas.GroundlineAt(0, field.Atlas.Tile.X / 2) > field.Atlas.Anchor.Y,
                "the groundline at the middle of the tank is not below its "
                + "anchor, so what the collar would follow is not the ground");

            // The bow. What the water does in front of a driving hull, and it
            // is the collar's own line dilated along the way the tank is going -
            // so the two cannot disagree about where the tank is, and the crest
            // needs no test for which end is the bow.
            Check("the crest ahead of a hull is the tank's own outline, pushed",
                Stage3D.DeepShader.Contains("shoreline(i, back, true)")
                && Stage3D.DeepShader.Contains(
                    "px - normalize(hull_run[i]) * half_swell"),
                "the bow wave is a shape drawn in front of the tank rather than "
                + "the shape of it pushed along, which is the objection an "
                + "ellipse round the hull already lost to");
            // And it is dilated off the *bottom* of that silhouette while the
            // collar is drawn at the waterline. Two halves of one scan, and the
            // reason is where the drawn water starts: the waterline runs up the
            // armour, so a crest dilated off it lands under the sprite - measured
            // at 6 changed pixels in the whole pond, which is nothing.
            Check("and off the bottom of it, where the drawn water starts",
                Stage3D.DeepShader.Contains("shoreline(i, px, false)")
                && Stage3D.DeepShader.Contains("shoreline(i, back, true)")
                && Stage3D.DeepShader.Contains("hem ? hull_hem[lo]"),
                "the crest and the collar read the same row of the same table, "
                + "so one of them is drawn where it cannot be seen");
            // Offset and spread are one number, halved: equal is the single
            // setting that fills the crest from the hull outward. Less opens a
            // gap of clean water beside the tank, more draws the inner half
            // under the sprite.
            Check("and it stands off by exactly what it spreads by",
                Stage3D.DeepShader.Contains("normalize(hull_run[i]) * half_swell")
                && Stage3D.DeepShader.Contains("smoothstep(0.0, half_swell,"),
                "the crest is offset by one number and spread by another, so it "
                + "either leaves clean water at the hull or wastes its inner "
                + "half behind the sprite");
            // The projection belongs to the reach and not to the opacity. Both
            // is the same reason counted twice, and it costs the heading that
            // shows a bow best: driving into the camera GroundDirection is only
            // sin(elevation) long, so the crest came out half as far and half as
            // white at once.
            var across = new Vector2(1.0f, 0.0f);
            var intoEye = new Vector2(0.0f, 0.5f);
            Check("and a bow driven across the screen reaches further than one "
                  + "driven into it",
                Stage3D.BowStandoff(across) > Stage3D.BowStandoff(intoEye) * 1.5f,
                $"across {Stage3D.BowStandoff(across):F1}px against "
                + $"{Stage3D.BowStandoff(intoEye):F1}px into the camera, so the "
                + "crest does not foreshorten with the ground it stands on");
            Check("and it reaches further the harder the tank is pushing, and "
                  + "not at all parked",
                Stage3D.BowStandoff(across * 0.5f)
                    < Stage3D.BowStandoff(across) - 1.0f
                && Stage3D.BowStandoff(Vector2.Zero) == 0.0f,
                $"half push stands off {Stage3D.BowStandoff(across * 0.5f):F1}px "
                + $"against {Stage3D.BowStandoff(across):F1}px at full");
            Check("but how white it is comes off the push alone",
                Stage3D.DeepShader.Contains("float push = hull_push[i] * bow_on;")
                && Stage3D.DeepShader.Contains("prow = max(prow, push"),
                "the crest's opacity carries the projection as well as its "
                + "reach, so a tank driving into the camera - the heading that "
                + "shows a bow best - all but stops making one");
            // Its own switch beside the wake's, because the two are different
            // statements: one is what the water does behind a tank and one is
            // what it does in front, and an A/B of either wants the other to
            // hold still.
            Check("and the bow and the wake are switched apart",
                Stage3D.DeepShader.Contains("bow_on")
                && Stage3D.DeepShader.Contains("wake_on")
                && !Stage3D.DeepShader.Contains("prow * wake_on"),
                "one switch takes both, so neither can be judged against the "
                + "picture without the other");

            // The wake. Its own object because a swell answers "what is this
            // hexagon doing" and a trail is a line across hexagons - two
            // statements about one event at two resolutions, and neither is
            // recoverable from the other.
            var trail = new Wake();
            // A pond is a level down, so every stamp in this block is laid on
            // ground below the datum - which is the case the drawn row and the
            // earth row disagree in, and the only case that shows anything.
            float pondBed = -field.Lift;
            Check("a fresh wake stamp is wider than the gap to the next one",
                2.0f * Wake.Seed > Wake.Step,
                $"a stamp is {Wake.Seed:F0} across and they are {Wake.Step:F0} "
                + "apart, so the trail is a row of dots and no opacity fixes it");

            // Distance and not time. A stamp every so many seconds is dense
            // behind a crawling tank and dotted behind a fast one - the ruts and
            // the belt phase are laid off distance for the same reason.
            for (int i = 0; i < 30; i++)
                trail.Note(0, new Vector2(100.0f, 100.0f), pondBed, true, true);
            Check("and standing still in the water lays exactly one",
                trail.Count == 1, $"{trail.Count} stamps without moving an inch");

            var was = trail.At[0];
            for (int i = 1; i <= 6; i++)
                trail.Note(0, new Vector2(100.0f + i * Wake.Step * 1.1f, 100.0f),
                           pondBed, true, true);
            Check("and driving on lays one for every step of ground covered",
                trail.Count == 7, $"{trail.Count} stamps over six steps");

            // The comet test. A stamp hung at a fixed offset behind the hull
            // travels with it, and on a still frame the two are the same
            // picture - which is why this is asserted rather than looked at.
            Check("and the ones already laid stay where they were laid",
                trail.At[0] == was, $"the first stamp moved to {trail.At[0]}");

            // Out of the water lays nothing, and coming back does not draw a
            // line across the dry ground in between.
            // Asked at close quarters, because at long range it is not a test:
            // a tank that leaves the water and comes back far away lays a stamp
            // either way, on distance alone. The version of this that walked out
            // to (900,900) and back passed with the forgetting taken out - it
            // was asserting the distance rule twice and the thing it named not
            // at all.
            Vector2 edge = trail.At[trail.Count - 1];
            int ashore = trail.Count;
            trail.Note(0, edge + new Vector2(Wake.Step * 0.3f, 0.0f), pondBed, true,
                       false);
            trail.Note(0, edge + new Vector2(Wake.Step * 0.6f, 0.0f), pondBed, true,
                       true);
            Check("and coming back to the water starts the trail again at once",
                ashore == 7 && trail.Count == 8,
                $"{trail.Count} stamps against {ashore} before - the tank was "
                + "remembered while it was on dry land, so the first stretch "
                + "back in the water leaves no mark");

            // And they go. A trail that stuck would be a pond wearing the marks
            // of every crossing of the session.
            for (int i = 0; i < 60 * 6; i++)
                trail.Tick(1.0 / 60.0);
            Check("and a wake fades off the water", trail.Count == 0,
                $"{trail.Count} stamps six seconds after the last one was laid");

            // One threshold for both, because a tank that stirs the water it
            // stands in and leaves no trail through it is two answers to one
            // question.
            // The blot's own half of the same pairing. Sized and cleared with the
            // mark, because a lift left standing beside a cleared mark is a blot
            // at the origin sitting a level off the ground.
            Check("and the swell keeps a lift beside every mark",
                sea.Rest.Length == sea.Mark.Length,
                $"{sea.Rest.Length} lifts against {sea.Mark.Length} marks");

            Check("and what counts as driving is the same for the swell and the wake",
                Mathf.IsEqualApprox(Wake.DriveAbove, Swell.StirAbove),
                $"wake {Wake.DriveAbove:F2} against swell {Swell.StirAbove:F2}");

            // Combined by max in the shader. Stamps overlap along the path by
            // construction - that is what makes the line continuous - so summing
            // them puts a bright bead at every spacing, which reads as the
            // trail being made of dots after all.
            Check("and the surface combines its stamps by max, never by sum",
                Stage3D.DeepShader.Contains("trail = max(trail,"),
                "overlapping stamps add up, so the trail beads at its own spacing");

            // A lift beside every stamp, because the point is a drawn row. Two
            // lists rather than one, so this is the same pairing risk the swell's
            // mark carries and the same answer: same order, same length, dropped
            // together.
            Check("and every wake stamp carries the lift of the ground it was laid on",
                trail.Lift.Count == trail.At.Count
                && (trail.Count == 0 || Mathf.IsEqualApprox(trail.Lift[0], pondBed)),
                $"{trail.Lift.Count} lifts against {trail.At.Count} stamps");

            // <b>And the whole of what that lift buys.</b> Three spellings, three
            // rows, and only one of them is the tank's own. Asserted rather than
            // looked at, because the screen row of the stamp's own centre comes
            // out the same however it is spelled - World draws a point at flat
            // minus lift, and every spelling differs by exactly its lift in both
            // terms. What moves is <b>where the surface meets it</b>: the pond is
            // a horizontal plane, so the fragment whose world z matches the stamp
            // is drawn a lift away from the one that should have.
            //
            // Which is why nothing said so, twice over. The trail was not blank,
            // not clipped and not the wrong shape - it was a level below the tank
            // that laid it, then a ford's depth above it, and every number about
            // it stayed sane both times.
            float mark = 400.0f;
            float flatten = field.Squash;
            float lifted = field.RiseFactor;
            float surface = pondBed + field.WaterRise;
            // The row the pond fragment that matches a stamp is drawn on: its own
            // world z through the camera, less the height the surface stands at.
            //
            // Through the stage's own Ground and its own World rather than either
            // written out here: a check that spells the conversion itself asserts
            // the arithmetic and not the wiring, and the wiring is what was wrong.
            float Row(Vector3 at) => at.Z * flatten - surface;
            var spot = new Vector2(0.0f, mark);
            float onWater = Row(Stage3D.Ground(spot, surface, flatten, lifted));
            float onBed = Row(Stage3D.Ground(spot, pondBed, flatten, lifted));
            float onNothing = Row(Stage3D.World(spot, 0.0f, flatten, lifted));
            Check("and a wake laid in a ford is drawn on the tank's own row",
                Mathf.IsEqualApprox(onWater, mark),
                $"row {onWater:F2} against the tank's {mark:F2}");
            // The second mistake, and the reason it is not a rounding: the depth
            // of a ford and half a track gauge project to about the same number of
            // rows, so a blot that misses by the one lands on the far track's own
            // ground line and reads as clinging to it.
            Check("and carrying the bed's lift instead would raise it by the water's depth",
                Mathf.IsEqualApprox(onWater - onBed, field.WaterRise)
                && field.WaterRise > 0.0f,
                $"{onWater - onBed:F2} px apart against {field.WaterRise:F2} of "
                + "water, so this is not the mistake it is named for");
            Check("and taking a lift of zero would drop it a whole level below that",
                Mathf.IsEqualApprox(onNothing - onBed, -pondBed)
                && !Mathf.IsZeroApprox(pondBed),
                $"{onNothing - onBed:F2} px apart against a lift of {-pondBed:F2} "
                + "- the two spellings differ by something other than the ground's "
                + "own height, so this is not the mistake it is named for");

            // And it settles. A state that stuck would be a pond that had been
            // driven through once and stayed broken for the rest of the session.
            for (int i = 0; i < 60 * 5; i++)
                sea.Tick(1.0 / 60.0);
            Check("and it all settles back to calm once nobody is in it",
                sea.Peak == 0.0f, $"peak {sea.Peak:F2} five seconds later");

            // One ordering, and the mesh writes an ordinal off it into every
            // vertex. Two lists that agree until somebody floods a cell is the
            // failure this is here to make impossible.
            bool ordered = true;
            for (int i = 0; i < field.WaterCells.Count; i++)
                ordered &= field.WaterIndex(field.WaterCells[i]) == i;
            Check("and the surface and the swell index its cells the same way",
                ordered && field.WaterIndex(HexField.Step(pond[0], 90)) is var away
                        && (field.IsWater(HexField.Step(pond[0], 90)) || away < 0),
                "the field's list and its index disagree, so the mesh would hand "
                + "a cell somebody else's state");

            // The one number the belts read, and the whole of what stops a rut
            // being laid in a pond. Two halves of one field rather than a flag
            // beside a height, because a pair is a pair that can disagree.
            // And it is asked against the ground rather than against nothing,
            // which is the half a flooded ramp needed: over the front of a beach
            // the cell is water and the tank is not in it.
            float wetTop = field.WaterTop(pond[0]);
            Check("a tank is wading exactly when the water stands over its own ground",
                !Vehicle.WadingAt(float.NegativeInfinity, 0.0f)
                && Vehicle.WadingAt(wetTop, field.TopAt(pond[0]))
                && Vehicle.WadingAt(wetTop, wetTop - 1.0f)
                && !Vehicle.WadingAt(wetTop, wetTop + 1.0f),
                "the belts and the stage read this one field for two questions, "
                + "and the belts read it to keep a rut out of the water");
        }
        finally
        {
            field.SetWater(null);
            field.SetRelief(null);
            if (hadWater)
                field.SetWater(BoardMap.Bench.WaterFor(field.Columns, field.Rows));
        }
    }

    /// <summary>
    /// The two slots a hex has - what its floor is made of and what stands on
    /// it - and the rules the two carry.
    ///
    /// A method of its own with the vehicles handed in, because the last of it
    /// is a shot down a lane: what a cover does to a round is half of what the
    /// layer is for, and it cannot be asked without two tanks. Everything
    /// before that runs on boards invented here, for the reason the map checks
    /// do - none of it needs a tile, a prop or a scene to exist.
    /// </summary>
    private static void Terrain(HexField field,
                                IReadOnlyList<Vehicle>? vehicles, int active,
                                Action<string, bool, string> Check)
    {
        // --- the two slots a hex has --------------------------------------
        //
        // Run on probe boards for the reason the maps below are: none of this
        // needs a tile, a prop or a tank to exist. What a cell is made of and
        // what stands on it are a table and two grids, so the assertions are
        // arithmetic and the board they are made against is invented here.
        Theme("terrain: the two slots a hex has");

        TerrainRules.Ready();
        Check("terrain.json is read, and everything it names exists",
            TerrainRules.Loaded && TerrainRules.Error.Length == 0,
            TerrainRules.Loaded ? TerrainRules.Error
                : "running on the compiled figures - " + TerrainRules.Error);

        // Both directions, WallConfig's rule: an entry with nothing behind it is
        // a figure for a floor that does not exist, and a floor with no entry is
        // one that quietly kept the compiled number. The file is the numbers and
        // the enums are the vocabulary, so the two have to cover each other.
        var unspoken = new List<string>();
        foreach (Foundation ground in TerrainRules.Grounds)
            if (!TerrainRules.Knows(ground))
                unspoken.Add(ground.ToString());
        foreach (Cover kind in TerrainRules.Covers)
            if (!TerrainRules.Knows(kind))
                unspoken.Add(kind.ToString());
        Check("every floor and every cover has a rule behind it",
            unspoken.Count == 0, string.Join(" ", unspoken));

        // Intact is the one state every cover has to admit - see
        // TerrainRules.Apply, which refuses a file that takes it away. A cover
        // that cannot stand intact is broken the moment it is laid.
        var brokenBorn = TerrainRules.Covers
            .Where(c => c != Cover.None
                        && !TerrainRules.Allows(c, CoverState.Intact))
            .Select(c => c.ToString()).ToList();
        Check("every cover can stand intact",
            brokenBorn.Count == 0, string.Join(" ", brokenBorn));

        // The table saying no, which is the half that matters: a state list that
        // admitted everything would be a list that says nothing. Both of these
        // are nonsense in the same way and the place they are refused is the
        // table rather than a switch in whatever asks.
        Check("a wall cannot burn and a wood cannot be breached",
            !TerrainRules.Allows(Cover.Walls, CoverState.Burning)
            && !TerrainRules.Allows(Cover.Forest, CoverState.Breached),
            "the states a cover may be in are declared per cover");

        // Spent is not gone: the cover is still what it was and on the map it
        // still reads as woods, and what it stops is nothing. Both halves,
        // because a burning wood standing is the one that is easy to get wrong -
        // it is still trees, and on fire.
        Check("a burning wood still stands and a burnt one does not",
            TerrainRules.Standing(Cover.Forest, CoverState.Burning)
            && !TerrainRules.Standing(Cover.Forest, CoverState.Burnt),
            "burning is a state of a cover that is there");

        var slots = new HexField { Columns = 8, Rows = 6 };
        var floors = new Foundation[8 * 6];
        floors[3 * 8 + 3] = Foundation.Sand;
        floors[3 * 8 + 4] = Foundation.Deep;
        floors[3 * 8 + 5] = Foundation.Rock;
        slots.SetGround(floors);

        Check("a board reads back the floors it was given",
            slots.FoundationAt(new Vector2I(3, 3)) == Foundation.Sand
            && slots.FoundationAt(new Vector2I(4, 3)) == Foundation.Deep
            && slots.FoundationAt(new Vector2I(5, 3)) == Foundation.Rock
            && slots.FoundationAt(new Vector2I(0, 0)) == Foundation.Solid,
            $"{slots.FoundationAt(new Vector2I(3, 3))} / "
            + $"{slots.FoundationAt(new Vector2I(4, 3))} / "
            + $"{slots.FoundationAt(new Vector2I(5, 3))}");

        // The floor is a refusal in the same place the board's edge is, which is
        // what lets the pathing keep one statement of what a step may cross.
        //
        // Asked of every neighbour rather than of a heading picked by eye: two
        // cells on one row are not neighbours on a staggered grid, and a check
        // written as if they were would pass on the even columns and mean
        // nothing on the odd ones.
        bool Reaches(Vector2I onto) =>
            HexField.EdgeHeadings.Any(h =>
                slots.Passable(HexField.Step(onto, h), HexField.Reverse(h)));
        Check("nothing drives into deep water or on to rock",
            !Reaches(new Vector2I(4, 3)) && !Reaches(new Vector2I(5, 3))
            && Reaches(new Vector2I(1, 3)),
            "deep water and rock are floors, not heights");

        // Sand is the only figure in the file that is not neutral, and no map
        // authors sand - so this is the whole proof that the ceiling works, and
        // the reason it can be made without moving a number on a real board.
        Check("a slow floor caps the drive and a plain one does not",
            TerrainRules.Speed(slots.FaceAt(new Vector2I(3, 3))) < 1.0f
            && Math.Abs(TerrainRules.Speed(slots.FaceAt(new Vector2I(0, 0)))
                        - 1.0f) < 1e-4f,
            $"sand {TerrainRules.Speed(slots.FaceAt(new Vector2I(3, 3))):F2}, "
            + $"solid {TerrainRules.Speed(slots.FaceAt(new Vector2I(0, 0))):F2}");

        // Water is read off the floor and authored beside it, and that seam is
        // the one thing about this model that is unfinished - so it is asserted
        // rather than left to be discovered: whatever a board's ground grid
        // says, a flooded cell comes back as water.
        var wet = new bool[8 * 6];
        wet[3 * 8 + 1] = true;
        slots.SetWater(wet);
        Check("a flooded cell reads as water whatever the map made it of",
            slots.FoundationAt(new Vector2I(1, 3)) == Foundation.Shallow
            && slots.FaceAt(new Vector2I(1, 3)).Wet
            && !slots.FaceAt(new Vector2I(0, 3)).Wet,
            $"{slots.FoundationAt(new Vector2I(1, 3))}");
        slots.SetWater(null);

        // And off the board, which is the other value that is not a material.
        // Plot has said which cells are on the board since the maps arrived; up
        // to here nothing in the pathing had ever asked it.
        var yard = new Vector2I(2, 2);
        var yardOut = HexField.Step(yard, HexField.EdgeHeadings[0]);
        var fenced = new HexField
        {
            Columns = 6, Rows = 6,
            Plot = new HashSet<Vector2I> { yard, yardOut },
        };
        Check("a cell off the plot is a floor nothing drives on to",
            fenced.FoundationAt(HexField.Step(yard, HexField.EdgeHeadings[3]))
                == Foundation.Void
            && fenced.FoundationAt(yard) == Foundation.Solid
            && fenced.Passable(yard, HexField.EdgeHeadings[0])
            && !fenced.Passable(yard, HexField.EdgeHeadings[3]),
            "the board's edge is a floor like any other");

        Theme("terrain: cover in the way");

        // Back to plain ground first: the floors above left deep water and a
        // rock on this board, and a route refused by the floor would look
        // exactly like a route refused by a wall - which is the one thing the
        // checks below are for telling apart.
        slots.SetGround(null);

        var over = new Cover[8 * 6];
        over[3 * 8 + 4] = Cover.Walls;
        over[3 * 8 + 2] = Cover.Forest;
        slots.SetCover(over);

        Check("a board reads back what was put on it",
            slots.CoverAt(new Vector2I(4, 3)) == Cover.Walls
            && slots.CoverAt(new Vector2I(2, 3)) == Cover.Forest
            && slots.CoverAt(new Vector2I(0, 0)) == Cover.None,
            $"{slots.CoverAt(new Vector2I(4, 3))} / "
            + $"{slots.CoverAt(new Vector2I(2, 3))}");

        // The state grid refuses off the table rather than repairing - the shape
        // SetWater and DeriveRamps already have. A caller that gets false has
        // asked for something that is not a state of the world, and the one
        // thing that must not happen is that it quietly becomes one. Asked of
        // the wood, because masonry has no state of its own to set - see below.
        Check("a cover takes a state it may be in and refuses one it may not",
            slots.SetCoverState(new Vector2I(2, 3), CoverState.Burning)
            && !slots.SetCoverState(new Vector2I(2, 3), CoverState.Breached)
            && slots.CoverStateAt(new Vector2I(2, 3)) == CoverState.Burning,
            $"{slots.CoverStateAt(new Vector2I(2, 3))}");
        slots.SetCoverState(new Vector2I(2, 3), CoverState.Intact);

        // Masonry comes up sealed - a map letter cannot say which edges, and a
        // walled cell that barred nothing until a bench had run would be a board
        // that reads differently depending on the scene.
        Check("a walled cell comes up with all six of its sides standing",
            slots.SidesAt(new Vector2I(4, 3)) == HexField.AllSides
            && slots.SidesAt(new Vector2I(2, 3)) == 0
            && HexField.EdgeHeadings.All(
                h => slots.SideStands(new Vector2I(4, 3), h)),
            $"{slots.SidesAt(new Vector2I(4, 3)):x}");

        // The state of a walled cell is a reading off its sides and is refused
        // as a thing to write - the one cover whose state is derived, because
        // masonry is the one cover that stands on edges.
        Check("and its state is read off them rather than written",
            !slots.SetCoverState(new Vector2I(4, 3), CoverState.Breached)
            && slots.CoverStateAt(new Vector2I(4, 3)) == CoverState.Intact,
            "a wall's state is its six sides");

        // What the whole layer is for, now per edge: a wall stops a round
        // crossing the side it stands on and does nothing to the other five.
        // This is the resolution the cell could not carry - a ring with one leaf
        // down is a ring a tank shoots out of on that side and no other.
        int gate = HexField.EdgeHeadings[2];
        Check("a leaf brought down opens its own side and no other",
            slots.Breach(new Vector2I(4, 3), gate)
            && !slots.SideStands(new Vector2I(4, 3), gate)
            && HexField.EdgeHeadings.Count(
                h => slots.SideStands(new Vector2I(4, 3), h)) == 5
            && slots.CoverStateAt(new Vector2I(4, 3)) == CoverState.Intact,
            $"{slots.SidesAt(new Vector2I(4, 3)):x}");

        Check("and the same leaf cannot come down twice",
            !slots.Breach(new Vector2I(4, 3), gate),
            "a side that is already gone is not a breach");

        // The last of them, which is when the cover is spent - and it is the
        // reading, not a flag somebody set.
        foreach (int h in HexField.EdgeHeadings)
            slots.Breach(new Vector2I(4, 3), h);
        Check("a ring with every leaf down reads as breached",
            slots.CoverStateAt(new Vector2I(4, 3)) == CoverState.Breached
            && slots.SidesAt(new Vector2I(4, 3)) == 0,
            $"{slots.CoverStateAt(new Vector2I(4, 3))}");
        slots.SetSides(new Vector2I(4, 3), HexField.AllSides);

        // The edge belongs to both cells and neither: two rings side by side
        // each carry masonry on the side between them, so crossing is refused by
        // either and it does not matter which.
        var mine = new Vector2I(4, 3);
        int door = HexField.EdgeHeadings[0];
        Vector2I yon = HexField.Step(mine, door);
        slots.SetSides(mine, HexField.AllSides & ~(1 << HexField.EdgeIndex(door)));
        Check("a wall on either side of an edge closes it",
            !slots.Blocked(mine, door)
            && slots.SidesAt(yon) == 0
            && slots.Blocked(HexField.Step(mine, HexField.EdgeHeadings[1]),
                             HexField.Reverse(HexField.EdgeHeadings[1])),
            "the edge is asked of both cells");
        slots.SetSides(mine, HexField.AllSides);

        // The seam that keeps a ram legal. Passable is about the ground, so it
        // says yes into a walled cell; the refusal is an edge the route is asked
        // to mind - and the wall bench, whose whole subject is driving into
        // masonry, does not ask.
        Check("a wall is not a wall to Passable, and is one to a route minding it",
            slots.Passable(new Vector2I(3, 3), 90)
            && slots.Blocked(new Vector2I(4, 3), 90),
            "Passable answers for the ground");

        // A wall down the whole of column four, which on a staggered grid really
        // does cut the board in two: every neighbour of a cell in column three
        // is in column four. So the route that exists without the barricades and
        // does not exist with them is the one statement worth making, and it
        // does not depend on counting steps across an offset grid.
        var fence = new Cover[8 * 6];
        for (int r = 0; r < 6; r++)
            fence[r * 8 + 4] = Cover.Walls;
        slots.SetCover(fence);
        var far = new Vector2I(6, 3);
        var near = new Vector2I(2, 3);
        List<Vector2I> through = slots.FindPath(near, far);
        List<Vector2I> round = slots.FindPath(near, far, null, masonry: true);
        Check("a route minding the masonry goes round the wall, not through",
            through.Count > 0 && through.Any(c => c.X == 4) && round.Count == 0,
            $"through {through.Count} cells, round {round.Count}");

        // Breach one cell of it and the same order walks - through that cell and
        // no other, the rest of the course still standing. Not decoration: it is
        // the half that says the state is read when the route is asked for
        // rather than baked in when the board was laid.
        slots.SetSides(new Vector2I(4, 3), 0);
        List<Vector2I> gap = slots.FindPath(near, far, null, masonry: true);
        Check("and a breach in it lets the route through",
            gap.Count > 0 && gap.Contains(new Vector2I(4, 3)),
            $"{gap.Count} cells");

        if (vehicles is { Count: > 1 })
        {
            // The lane is walked rather than assumed: two cells on one row are
            // deliberately not on a lane on this grid - the check above this
            // topic asserts exactly that - so a shooter and a target picked by
            // eye would be testing nothing.
            var stand = new Vector2I(2, 4);
            List<Vector2I> shot = slots.Lane(stand, 90, 3);
            var wall = new Cover[8 * 6];
            if (shot.Count == 3)
                wall[shot[1].Y * 8 + shot[1].X] = Cover.Walls;
            slots.SetCover(wall);

            Vehicle gunner = vehicles[active];
            Vehicle mark = vehicles[active == 0 ? 1 : 0];
            Vector2I heldGunner = gunner.Cell, heldMark = mark.Cell;
            gunner.Cell = stand;
            mark.Cell = shot.Count == 3 ? shot[2] : stand;
            Shot walled = Gunnery.Solve(slots, vehicles, gunner, mark);
            Check("a wall in the lane stops the shell, and says it was the board",
                shot.Count == 3 && walled.OnLane && !walled.Clear
                && walled.BlockedAt == shot[1] && walled.ByCover,
                $"{walled} - a tank in the way and a wall in the way are "
                + "different orders to give");

            if (shot.Count == 3)
                slots.SetSides(shot[1], 0);
            Shot opened = Gunnery.Solve(slots, vehicles, gunner, mark);
            Check("and the shot is clear through the breach",
                opened.Clear && opened.Range == 3, $"{opened}");

            // The rim of the shooter's own cell, which is the case the cell was
            // never able to answer: a tank inside a ring is firing through its
            // own masonry, and the one wall on the board it is actually inside
            // would otherwise be the one wall it cannot hit.
            var ringed = new Cover[8 * 6];
            ringed[stand.Y * 8 + stand.X] = Cover.Walls;
            slots.SetCover(ringed);
            Shot inside = Gunnery.Solve(slots, vehicles, gunner, mark);
            Check("a tank inside a ring is stopped by its own rim",
                inside.OnLane && !inside.Clear && inside.ByCover
                && inside.BlockedAt == shot[0],
                $"{inside}");

            slots.Breach(stand, 90);
            Shot out90 = Gunnery.Solve(slots, vehicles, gunner, mark);
            Check("and shoots out of the leaf it knocked down",
                out90.Clear, $"{out90}");

            gunner.Cell = heldGunner;
            mark.Cell = heldMark;
        }

        // The wood's own state, written by the thing that burns it. The point of
        // the arrangement rather than a detail of it: everything Wildfire knows
        // is a function of one age - char, flame, smoke, the handover - and none
        // of that is a thing a rule can be written against. Two nouns are, and
        // this is where they arrive on the board.
        //
        // On a board of its own with every cell wooded, because what is being
        // asserted is the clock reaching the cell and not which cells the grove
        // happened to sow.
        var burning = new HexField { Columns = 4, Rows = 4 };
        var woods = new Cover[4 * 4];
        Array.Fill(woods, Cover.Forest);
        burning.SetCover(woods);
        var fire = new Wildfire { Field = burning, Wooded = _ => true };
        var lit = new Vector2I(1, 1);

        Check("an unlit wood is a wood that is not burning",
            burning.CoverStateAt(lit) == CoverState.Intact,
            $"{burning.CoverStateAt(lit)}");

        fire.Light(lit);
        fire.Tick(0.1);
        Check("a wood that catches says so on the board",
            burning.CoverStateAt(lit) == CoverState.Burning
            && burning.FaceAt(lit).Standing,
            $"{burning.CoverStateAt(lit)} - burning is a state of a cover that "
            + "is still there");

        // Past the last tree on the cell, which is the one staggered by the
        // whole of CatchWithin - the boundary the alight count is already
        // written against, asked here rather than a second judgement about when
        // a wood is finished.
        fire.Tick(fire.BurnFor + fire.CatchWithin + 0.1);
        Check("and that it is finished when the last tree on it is",
            burning.CoverStateAt(lit) == CoverState.Burnt
            && !burning.FaceAt(lit).Standing,
            $"{burning.CoverStateAt(lit)} - a burnt wood is a hole in the line");

        // The fire spreads, so its neighbours are burning by now and the board
        // has to have been told about them too - the half that a check on one
        // lit cell cannot see.
        //
        // One more frame first, and it is the spread own rule rather than a
        // settling time: a cell caught during a tick is lit after the walk that
        // caught it, so what it is doing is said on the next one. Without this
        // the count is one and the assertion would be about nothing.
        fire.Tick(0.1);
        int caught = 0;
        for (int q = 0; q < 4; q++)
        for (int r = 0; r < 4; r++)
            if (burning.CoverStateAt(new Vector2I(q, r)) != CoverState.Intact)
                caught++;
        Check("every cell the fire reached is one the board knows about",
            caught > 1 && caught == fire.Scorched,
            $"{caught} cells marked against {fire.Scorched} alight or burnt");

        // The board the harness actually opens on, where the woods are not
        // authored at all - the terrain hash scatters them - so the cover grid
        // is null and there is nothing to write a state into. A cell with trees
        // drawn on it that cannot be told it is on fire is the one answer too
        // many this layer exists to stop, so the grid is made on demand.
        //
        // Asked of the live field rather than of an invented one because the
        // hash is what has to be asked: a probe board with no terrain loaded has
        // no woods on it to find.
        Vector2I? hashed = null;
        for (int q = 0; q < field.Columns && hashed is null; q++)
        for (int r = 0; r < field.Rows && hashed is null; r++)
            if (field.CoverAt(new Vector2I(q, r)) == Cover.Forest)
                hashed = new Vector2I(q, r);
        if (hashed is Vector2I wood)
        {
            bool took = field.SetCoverState(wood, CoverState.Burning);
            bool read = field.CoverStateAt(wood) == CoverState.Burning;
            field.SetCoverState(wood, CoverState.Intact);
            Check("a wood nobody authored can still be told it is burning",
                took && read,
                $"({wood.X},{wood.Y}) took {took}, read back {read}");
        }

        // And putting the wood back puts the board back, for Swell.Settle's
        // reason: a state left standing comes back as a board that was burning
        // before anybody lit it.
        fire.Douse();
        int still = 0;
        for (int q = 0; q < 4; q++)
        for (int r = 0; r < 4; r++)
            if (burning.CoverStateAt(new Vector2I(q, r)) != CoverState.Intact)
                still++;
        Check("dousing it puts every cell back",
            still == 0, $"{still} cells still burnt");

        // The two representations of the same board, held against each other.
        // This is the migration's own check and it is meant to be temporary: the
        // walls, the cliffs and the wooded kinds are still authored as their own
        // grids, and the slots are decoded from the same characters. The day one
        // of them is edited alone, this is what says so.
        var mismatched = new List<string>();
        foreach (string name in BoardMap.Names)
        {
            BoardMap board;
            try
            {
                board = BoardMap.ByName(name);
            }
            catch (Exception)
            {
                continue;               // judged, and reported, below
            }
            for (int r = 0; r < board.Rows && mismatched.Count < 6; r++)
            for (int q = 0; q < board.Columns && mismatched.Count < 6; q++)
            {
                var cell = new Vector2I(q, r);
                if (!board.OnBoard(cell))
                    continue;
                int at = board.At(cell);
                if (board.Walls[at] != (board.Over[at] == Cover.Walls))
                    mismatched.Add($"{name} ({q},{r}) wall");
                if (board.Cliffs[at] != (board.Ground[at] == Foundation.Rock))
                    mismatched.Add($"{name} ({q},{r}) rock");
                if ((board.Kinds[at] == TerrainSet.Forest)
                    != (board.Over[at] == Cover.Forest))
                    mismatched.Add($"{name} ({q},{r}) wood");
            }
        }
        Check("every authored board says the same thing twice about its cells",
            mismatched.Count == 0, string.Join("; ", mismatched));

    }

    private static void Relief(HexField field, Grove? grove,
                               Action<string, bool, string> Check)
    {
        Theme("relief: a flat board is the board it was");

        // The claim the whole slice rests on. Depth is only ever compared, so it
        // could be scaled by anything - but the trees sort at round(footY) and
        // the ruts and rings sit at fixed rungs below the board, so a key that
        // scaled the row would put every tank behind every tree, on a board with
        // no relief on it at all.
        float worst = 0.0f;
        for (float row = -200.0f; row <= 900.0f; row += 13.0f)
            worst = Math.Max(worst, Math.Abs(field.Depth(row, 0.0f) - row));
        Check("with nothing raised, depth is the foot row itself", worst < 0.001f,
            $"off by {worst:F4} - the trees sort against this number");

        int drift = 0;
        for (int q = 0; q < field.Columns; q++)
        for (int r = 0; r < field.Rows; r++)
            if (field.CellAnchor(q, r) != field.FlatAnchor(q, r))
                drift++;
        Check("and no cell is lifted", !field.HasRelief && drift == 0,
            $"{drift} cells moved");
        Check("and nothing is drawn over a tank",
            field.Occluders(300.0f, 0.0f).Count == 0,
            "a flat board promoted ground over a tank");

        // Ramp-free on purpose: everything in this topic is the 2D board's
        // occlusion rule, which is written in whole levels and cannot answer for a
        // surface half a level up - see Main.Ramped. The ramps have their own
        // topic and their own board.
        field.SetRelief(BoardMap.Bench.LevelsFor(field.Columns, field.Rows));
        try
        {
            Theme("relief: a click lands on the face that was drawn");

            float lift = field.Lift;
            Check("one level is a visible number of pixels", lift > 4.0f,
                $"{lift:F1}px - below this the board has height nobody can see");

            // The oracle, and it is deliberately not the pixel-to-hex code the
            // picker uses: a point is on a cell's face when it is inside that
            // cell's drawn hexagon, and the answer is the frontmost of those,
            // because frontmost is what was drawn last. Written as half-planes on
            // the ground-space offset, so the only thing shared with the picker
            // is the squash.
            float squash = Math.Max(field.Squash, 0.01f);
            float radius = field.Atlas is null ? 0.0f
                : field.Atlas.HexRect.Size.X * 0.5f;
            float apothem = Mathf.Sqrt(3.0f) * radius * 0.5f;

            // How far inside its own face a probe has to be before it counts. A
            // point on a boundary is legitimately on two cells, and a check that
            // demands an answer there is measuring float rounding.
            const float Margin = 3.0f;

            float Slack(Vector2I cell, Vector2 point)
            {
                Vector2 to = point - field.CellCentre(cell);
                float gx = Math.Abs(to.X), gy = Math.Abs(to.Y / squash);
                return Math.Min(apothem - gy, Mathf.Sqrt(3.0f) * (radius - gx) - gy);
            }

            Vector2I Frontmost(Vector2 point)
            {
                var best = new Vector2I(-1, -1);
                float front = float.NegativeInfinity;
                for (int q = 0; q < field.Columns; q++)
                for (int r = 0; r < field.Rows; r++)
                {
                    var cell = new Vector2I(q, r);
                    if (Slack(cell, point) < 0.0f)
                        continue;
                    float row = field.FlatAnchor(cell).Y;
                    if (row <= front)
                        continue;
                    front = row;
                    best = cell;
                }
                return best;
            }

            int probes = 0, wrong = 0, retired = 0;
            var firstWrong = "";
            for (int q = 0; q < field.Columns; q++)
            for (int r = 0; r < field.Rows; r++)
            {
                var cell = new Vector2I(q, r);
                Vector2 centre = field.CellCentre(cell);
                for (int i = 0; i < 6; i++)
                for (int step = 0; step <= 4; step++)
                {
                    double angle = Math.PI / 3.0 * i;
                    var out_ = new Vector2((float)Math.Cos(angle) * radius,
                                           (float)Math.Sin(angle) * apothem * squash
                                           * (2.0f / Mathf.Sqrt(3.0f)));
                    Vector2 point = centre + out_ * (step * 0.22f);
                    Vector2I want = Frontmost(point);
                    if (want.X < 0)
                        continue;
                    // Retired near any boundary, this cell's or another's: a
                    // probe a hair inside one face and a hair outside the face in
                    // front of it is a tie, and a tie has no right answer.
                    bool edgy = false;
                    for (int c = 0; c < field.Columns && !edgy; c++)
                    for (int d = 0; d < field.Rows && !edgy; d++)
                        edgy = Math.Abs(Slack(new Vector2I(c, d), point)) < Margin;
                    if (edgy)
                    {
                        retired++;
                        continue;
                    }
                    probes++;
                    Vector2I got = field.CellAt(point);
                    if (got == want)
                        continue;
                    wrong++;
                    if (firstWrong.Length == 0)
                        firstWrong = $"({point.X:F0},{point.Y:F0}) wanted "
                                     + $"({want.X},{want.Y}) level {field.LevelAt(want)}"
                                     + $" got ({got.X},{got.Y}) level {field.LevelAt(got)}";
                }
            }
            Check($"every one of {probes} probes picks the face it is on",
                wrong == 0 && probes > 1000,
                wrong > 0 ? $"{wrong} wrong, first {firstWrong}"
                          : $"only {probes} probes, {retired} retired");

            Theme("relief: the depth key is the camera it claims to be");

            // Against the projection written out from the angle, rather than
            // against itself. Depth is compared and never read, so what has to
            // hold is the ordering; the far edge and the flat-term scaling are
            // both free to move under that, and this catches a swapped factor or
            // a sign, which is what would make raising a thing push it away.
            double elevation = Math.Asin(Math.Clamp(field.Squash, 0.01f, 0.99f));
            double cos = Math.Cos(elevation), sin = Math.Sin(elevation);
            double True(float row, float liftPx) =>
                row / Math.Max(field.Squash, 0.01f) * cos
                + liftPx / Math.Max(field.RiseFactor, 0.01f) * sin;
            int disagree = 0;
            var pairs = new List<(float Row, float Lift)>();
            for (float row = 0.0f; row <= 600.0f; row += 47.0f)
            for (int level = -1; level <= 2; level++)
                pairs.Add((row, level * lift));
            foreach ((float rowA, float liftA) in pairs)
            foreach ((float rowB, float liftB) in pairs)
            {
                int mine = field.Depth(rowA, liftA).CompareTo(field.Depth(rowB, liftB));
                int real = True(rowA, liftA).CompareTo(True(rowB, liftB));
                if (mine != real && Math.Abs(True(rowA, liftA) - True(rowB, liftB)) > 0.01)
                    disagree++;
            }
            Check("it orders every pair the way the projection does", disagree == 0,
                $"{disagree} pairs out of order - raising a thing must bring it nearer");

            Theme("relief: ground hides what stands below it and nothing else");

            // The two reported pictures, asked as themselves rather than by
            // repeating the rule. Both are needed: a test that only says no
            // passes by returning nothing at all.
            int flatOver = 0, stepMissed = 0, steps = 0, behind = 0, rises = 0;
            // Split by how far in front, because that is what a broken key
            // reports. A rise is either half a row ahead - the two diagonal
            // neighbours - or a whole one, and a depth key off by less than a
            // row loses the diagonals and keeps the rest. Counted apart so the
            // failure says which, rather than "8 were left under the tank" on a
            // board where the ones that worked were the ones nobody parks next
            // to.
            int nearSteps = 0, nearMissed = 0;
            var firstFlat = "";
            var firstBehind = "";
            for (int q = 0; q < field.Columns; q++)
            for (int r = 0; r < field.Rows; r++)
            {
                var stand = new Vector2I(q, r);
                // The row the harness hands in - where the tank's tracks are,
                // not where its turret axis projects. Built here rather than
                // taken from a vehicle because this walks the whole board, and
                // built by the field's own accessor because the first version
                // wrote it out and wrote it out in the other space: the checks
                // below then certified a rule the bench was not using.
                float row = field.GroundRow(stand);
                float standing = field.LevelAt(stand) * lift;
                var hiders = new HashSet<Vector2I>(field.Occluders(row, standing));
                foreach (int heading in HexField.EdgeHeadings)
                {
                    Vector2I next = HexField.Step(stand, heading);
                    if (!field.InBounds(next))
                        continue;
                    int step = field.LevelAt(next) - field.LevelAt(stand);
                    if (field.FlatAnchor(next).Y <= field.FlatAnchor(stand).Y)
                    {
                        // Behind it. A rise back there is higher and is still not
                        // allowed over the tank, and this is the half a rule
                        // written on height alone gets wrong: the hill's wall
                        // comes down across the turret of the tank in front of
                        // it.
                        if (step <= 0)
                            continue;
                        rises++;
                        if (hiders.Contains(next))
                        {
                            behind++;
                            if (firstBehind.Length == 0)
                                firstBehind = $"({next.X},{next.Y}) over a tank in "
                                              + $"front of it on ({q},{r})";
                        }
                        continue;
                    }
                    if (step < 0 && hiders.Contains(next))
                    {
                        flatOver++;
                        if (firstFlat.Length == 0)
                            firstFlat = $"({next.X},{next.Y}) over a tank on "
                                        + $"({q},{r}), {step} levels above it";
                    }
                    if (step <= 0)
                        continue;
                    steps++;
                    bool near = field.FlatAnchor(next).Y - field.FlatAnchor(stand).Y
                                < field.Atlas!.HexRect.Size.Y * 0.75f;
                    if (near)
                        nearSteps++;
                    if (hiders.Contains(next))
                        continue;
                    stepMissed++;
                    if (near)
                        nearMissed++;
                }
            }
            Check("ground below a tank never covers it", flatOver == 0,
                $"{flatOver} did, first {firstFlat} - a tank on a rise is not "
                + "hidden by the pit in front of it");

            Check($"but each of the {steps} rises in front of one does, "
                  + $"{nearSteps} of them only half a row",
                stepMissed == 0 && steps > 0 && nearSteps > 0,
                steps == 0 || nearSteps == 0
                    ? "the map has no rise in front of any cell - it proves nothing"
                    : $"{stepMissed} were left under the tank, {nearMissed} of them "
                      + "half a row in front - a depth key short by less than a row "
                      + "keeps the ones straight ahead and loses exactly these");
            // Level ground, which had a clause here once and no longer does. The
            // claim is worth making as a claim rather than left as an absence,
            // because what it forbids is not a picture anyone would call wrong
            // on a still frame - it is a whole cell going down over everything
            // the tank is drawn against, the near half of its selection ring
            // included, and a coverage that grows smoothly and then vanishes in
            // one frame halfway along every leg.
            //
            // Walked mid-step as well as parked, because that is where the
            // clause failed: parked at either end it was harmless, and the
            // moment it stopped applying was the moment it was covering the
            // most.
            int levelOver = 0, legs = 0;
            var firstLevel = "";
            for (int q = 0; q < field.Columns; q++)
            for (int r = 0; r < field.Rows; r++)
            {
                var from = new Vector2I(q, r);
                foreach (int heading in HexField.EdgeHeadings)
                {
                    Vector2I onto = HexField.Step(from, heading);
                    if (!field.InBounds(onto)
                        || field.LevelAt(onto) != field.LevelAt(from)
                        || field.FlatAnchor(onto).Y <= field.FlatAnchor(from).Y)
                        continue;
                    legs++;
                    float standing = field.LevelAt(from) * lift;
                    float a = field.GroundRow(from), b = field.GroundRow(onto);
                    for (float t = 0.0f; t <= 1.0f; t += 0.1f)
                    foreach (Vector2I cell in field.Occluders(Mathf.Lerp(a, b, t), standing))
                    {
                        if (field.LevelAt(cell) * lift > standing + 0.5f)
                            continue;
                        levelOver++;
                        if (firstLevel.Length == 0)
                            firstLevel = $"({cell.X},{cell.Y}) at {t:F1} of the leg "
                                         + $"({q},{r}) to ({onto.X},{onto.Y})";
                    }
                }
            }
            Check($"and nothing level with it ever does, over {legs} legs walked",
                levelOver == 0 && legs > 0,
                legs == 0 ? "no level neighbour in front anywhere - it proves nothing"
                          : $"{levelOver} did, first {firstLevel} - that repaint takes "
                            + "the selection ring with it and lets go mid-step");

            // Climbing, which is where the two heights parted company. A tank
            // covers a hex when it has come up to that hex's level - so while it
            // is on the way up, every cell at the level it is climbing to and in
            // front of it is still over it, and only arriving lets it out. Asked
            // both ways round: the second half is what keeps it from being passed
            // by a rule that never lets a tank out at all.
            int early = 0, climbs = 0, stuck = 0;
            var firstEarly = "";
            for (int q = 0; q < field.Columns; q++)
            for (int r = 0; r < field.Rows; r++)
            {
                var from = new Vector2I(q, r);
                foreach (int heading in HexField.EdgeHeadings)
                {
                    Vector2I onto = HexField.Step(from, heading);
                    if (!field.InBounds(onto)
                        || field.LevelAt(onto) <= field.LevelAt(from))
                        continue;
                    climbs++;
                    float a = field.GroundRow(from), b = field.GroundRow(onto);
                    for (float t = 0.0f; t <= 1.0f; t += 0.1f)
                    {
                        float row = Mathf.Lerp(a, b, t);
                        float up = field.HeightBetween(from, onto, t);
                        var hides = field.Occluders(row, up);
                        bool arrived = t > 0.999f;
                        for (int c = 0; c < field.Columns; c++)
                        for (int d = 0; d < field.Rows; d++)
                        {
                            var cell = new Vector2I(c, d);
                            if (field.LevelAt(cell) != field.LevelAt(onto)
                                || field.DepthOf(cell) <= field.Depth(row, up))
                                continue;
                            if (hides.Contains(cell) != arrived)
                                continue;
                            if (arrived)
                                stuck++;
                            else if (early++ == 0)
                                firstEarly = $"({c},{d}) at {t:F1} of the climb "
                                             + $"({q},{r}) to ({onto.X},{onto.Y})";
                        }
                    }
                }
            }
            Check($"a tank part way up a climb is still under the level it is "
                  + $"climbing to, over {climbs} climbs",
                early == 0 && stuck == 0 && climbs > 0,
                climbs == 0 ? "nothing on this map climbs - it proves nothing"
                : early > 0 ? $"{early} let it out early, first {firstEarly} - a tank "
                              + "called up at the first pixel is drawn over hexes it "
                              + "has not reached"
                            : $"{stuck} still over it once it arrived - it never gets "
                              + "up at all");

            // The stage's version of the same statement, asked of the pair the
            // stage actually reads. One depth per tank could not make it:
            // promoted whole to the crown, the sprite stood level with every
            // hex at that level from the first pixel, and (3,3) stopped
            // covering the tank the moment (3,2)->(4,3) began. So mid-climb
            // the nose must hold the crown - the cell being entered must not
            // cut the running gear - while the tail is still below it, which
            // is what lets the ground it has not climbed go back over it; a
            // climb's split must close exactly where the leg parks, or
            // arrival pops the tail's depth by whatever is left of it;
            // descending behind the rim the same split runs the other way
            // up - the nose sinking on the drawn ramp, the tail on the crown
            // for the whole leg, drained by the seam rather than closed by
            // the number (see StepHeights); and a level leg, or a descent in
            // plain sight, must not split at all, because those two ends
            // have nothing to disagree about.
            int stepPairs = 0, noseOff = 0, tailUp = 0, openEnd = 0,
                flatSplit = 0, buriedHull = 0;
            var firstPair = "";
            for (int q = 0; q < field.Columns; q++)
            for (int r = 0; r < field.Rows; r++)
            {
                var from = new Vector2I(q, r);
                foreach (int heading in HexField.EdgeHeadings)
                {
                    Vector2I onto = HexField.Step(from, heading);
                    if (!field.InBounds(onto))
                        continue;
                    stepPairs++;
                    (float nose, float tail) =
                        Footing.StepHeights(field, from, onto, 0.5f, 0.4f, 20.0f);
                    (float noseEnd, float tailEnd) =
                        Footing.StepHeights(field, from, onto, 1.0f, 0.4f, 20.0f);
                    // Surface heights rather than levels, for StepHeights'
                    // reason: a leg onto a ramp climbs half a lift, and asked in
                    // levels it is a flat leg that had better not split.
                    if (field.TopAt(onto) > field.TopAt(from))
                    {
                        if (Mathf.Abs(nose - field.TopAt(onto))
                            > 0.01f && noseOff++ == 0)
                            firstPair = $"nose {nose:F1} off the crown climbing "
                                        + $"({q},{r}) to ({onto.X},{onto.Y})";
                        if (tail >= nose - 0.01f && tailUp++ == 0)
                            firstPair = $"tail {tail:F1} up beside nose {nose:F1} "
                                        + $"mid-climb ({q},{r}) to ({onto.X},{onto.Y})";
                        // The gear and no more: the fully honest tail hid half
                        // the tank behind a wall the camera cannot see, and the
                        // cap is what keeps the hull of a mounting tank whole.
                        if (tail < nose - 20.01f && buriedHull++ == 0)
                            firstPair = $"tail {tail:F1} sunk {nose - tail:F1} "
                                        + $"under a cover of 20, ({q},{r}) to "
                                        + $"({onto.X},{onto.Y})";
                        if (Mathf.Abs(tailEnd - noseEnd) > 0.01f
                            && openEnd++ == 0)
                            firstPair = $"a split of {noseEnd - tailEnd:F1} "
                                        + $"still open at the far anchor of "
                                        + $"({q},{r}) to ({onto.X},{onto.Y})";
                    }
                    else if (field.TopAt(onto) < field.TopAt(from)
                             && field.GroundRow(onto) < field.GroundRow(from))
                    {
                        // Behind the rim the split is the climb's, the other
                        // way up: the nose sinks on the ramp the sprite is
                        // drawn on - anything else and what shows past the
                        // rim sinks at a different rate than it is drawn
                        // sinking - while the tail holds the crown for the
                        // whole leg. Not until some moment: a fragment over
                        // the plateau's top is visible at exactly the crown
                        // and swallowed whole a hair under it, so a tail
                        // that descends by number dumps the whole crown
                        // side in one frame, wherever the number puts it.
                        // The seam drains that side instead, which is why
                        // this pair, alone, is allowed to stay open at the
                        // far anchor.
                        if ((Mathf.Abs(nose - field.HeightBetween(from, onto,
                                 0.5f)) > 0.01f
                             || Mathf.Abs(noseEnd - field.TopAt(onto))
                                 > 0.01f) && noseOff++ == 0)
                            firstPair = $"nose {nose:F1}/{noseEnd:F1} off the "
                                        + $"drawn ramp descending ({q},{r}) "
                                        + $"to ({onto.X},{onto.Y})";
                        float crown = field.TopAt(from);
                        if ((Mathf.Abs(tail - crown) > 0.01f
                             || Mathf.Abs(tailEnd - crown) > 0.01f)
                            && tailUp++ == 0)
                            firstPair = $"tail {tail:F1}/{tailEnd:F1} off the "
                                        + $"crown {crown:F1} descending "
                                        + $"({q},{r}) to ({onto.X},{onto.Y})";
                    }
                    else
                    {
                        if (Mathf.Abs(tail - nose) > 0.01f && flatSplit++ == 0)
                            firstPair = $"a split of {nose - tail:F1} on a leg "
                                        + $"that is level or in plain sight, "
                                        + $"({q},{r}) to ({onto.X},{onto.Y})";
                        if (Mathf.Abs(tailEnd - noseEnd) > 0.01f
                            && openEnd++ == 0)
                            firstPair = $"a split of {noseEnd - tailEnd:F1} "
                                        + $"still open at the far anchor of "
                                        + $"({q},{r}) to ({onto.X},{onto.Y})";
                    }
                }
            }
            Check($"mid-climb the tail hangs below the nose by the gear and no "
                  + $"more, and mid-descent behind the rim it still holds the "
                  + $"crown, over {stepPairs} legs",
                stepPairs > 0 && noseOff == 0 && tailUp == 0 && openEnd == 0
                && flatSplit == 0 && buriedHull == 0,
                stepPairs == 0 ? "no legs walked - it proves nothing" : firstPair);

            // And the line the split runs along. The hex being mounted must be
            // wholly on the promoted side - any corner of it across the line
            // sits down on the tank riding up, which is how the vertical seam
            // failed - and the covering neighbour wholly on the other, or the
            // cover it owes the tail is cut short. The shared edge is the one
            // line that can say both, and this asks it of every drawn corner.
            int seams = 0, spilt = 0;
            var firstSpill = "";
            Vector2 hexHalf = (Vector2)field.Atlas!.HexRect.Size * 0.5f;
            var rim = new Vector2[]
            {
                new(hexHalf.X, 0.0f), new(hexHalf.X * 0.5f, hexHalf.Y),
                new(-hexHalf.X * 0.5f, hexHalf.Y), new(-hexHalf.X, 0.0f),
                new(-hexHalf.X * 0.5f, -hexHalf.Y), new(hexHalf.X * 0.5f, -hexHalf.Y),
            };
            for (int q = 0; q < field.Columns; q++)
            for (int r = 0; r < field.Rows; r++)
            {
                var from = new Vector2I(q, r);
                foreach (int heading in HexField.EdgeHeadings)
                {
                    Vector2I onto = HexField.Step(from, heading);
                    if (!field.InBounds(onto)
                        || field.LevelAt(onto) <= field.LevelAt(from))
                        continue;
                    if (Footing.SeamFlank(field, from, onto) is not Vector2I flank
                        || field.LevelAt(flank) != field.LevelAt(onto))
                        continue;
                    seams++;
                    Vector3 line = Footing.SeamLine(field, Vector2.Zero, from, onto);
                    foreach (Vector2 c in rim)
                    {
                        Vector2 pOn = field.CellAnchor(onto) + field.CentreOffset + c;
                        Vector2 pFl = field.CellAnchor(flank) + field.CentreOffset + c;
                        bool bad =
                            line.X * pOn.X + line.Y * pOn.Y - line.Z < -0.5f
                            || line.X * pFl.X + line.Y * pFl.Y - line.Z > 0.5f;
                        if (bad && spilt++ == 0)
                            firstSpill = $"({from.X},{from.Y}) to ({onto.X},{onto.Y})"
                                         + $" beside ({flank.X},{flank.Y})";
                    }
                }
            }
            Check($"the seam keeps the mounted hex whole on one side and the "
                  + $"covering one whole on the other, over {seams} climbs",
                seams > 0 && spilt == 0,
                seams == 0 ? "no flanked climb on this map - it proves nothing"
                           : $"{spilt} corners spilt over, first {firstSpill}");

            // The descent's seam is the leg's own edge in the flat world, and
            // it must part the two cells cleanly there: any corner of the
            // plateau being left across the line gets the sinking nose's
            // depth while it is still drawn over the top, and the top face
            // bites it whole. This is the same screen-vs-flat mistake the
            // climb's seam made on its first run - the flat-perpendicular
            // normal is (dx*s, dy/s), not the screen bisector - asserted on
            // the seam that reads it the other way round.
            int legsDown = 0, spiltDown = 0;
            var firstDown = "";
            for (int q = 0; q < field.Columns; q++)
            for (int r = 0; r < field.Rows; r++)
            {
                var from = new Vector2I(q, r);
                foreach (int heading in HexField.EdgeHeadings)
                {
                    Vector2I onto = HexField.Step(from, heading);
                    if (!field.InBounds(onto)
                        || field.LevelAt(onto) >= field.LevelAt(from)
                        || field.GroundRow(onto) >= field.GroundRow(from))
                        continue;
                    legsDown++;
                    Vector3 line = Footing.SeamEdge(field, Vector2.Zero, from, onto);
                    foreach (Vector2 c in rim)
                    {
                        Vector2 pFrom = field.FlatAnchor(from)
                                        + field.CentreOffset + c;
                        Vector2 pOnto = field.FlatAnchor(onto)
                                        + field.CentreOffset + c;
                        bool bad =
                            line.X * pFrom.X + line.Y * pFrom.Y - line.Z > 0.5f
                            || line.X * pOnto.X + line.Y * pOnto.Y - line.Z
                               < -0.5f;
                        if (bad && spiltDown++ == 0)
                            firstDown = $"({from.X},{from.Y}) down to "
                                        + $"({onto.X},{onto.Y})";
                    }
                }
            }
            Check($"and descending behind the rim, the seam parts the plateau "
                  + $"from the floor cleanly, over {legsDown} descents",
                legsDown > 0 && spiltDown == 0,
                legsDown == 0 ? "no descent behind a rim on this map - it "
                                + "proves nothing"
                              : $"{spiltDown} corners spilt over, first {firstDown}");

            Check($"and none of the {rises} rises behind one does",
                behind == 0 && rises > 0,
                rises == 0 ? "the map has no rise behind any cell - it proves nothing"
                           : $"{behind} did, first {firstBehind} - this paints a wall "
                             + "across the turret");

            Theme("relief: every hex is a column standing on one floor");

            // Two claims, and between them they are the whole of what a column
            // has to satisfy - which is the point of columns: the rule per pair
            // of cells that stood here needed four cases and got three of them
            // wrong in turn.
            //
            // First, no seam. Where the ground in front of a cell is lower, the
            // two plates part company, and what closes the gap is the higher
            // cell's own side rather than a face fitted between the pair. Asked
            // as the worst margin over the board, because a yes says nothing
            // about how close it came.
            float worstSeam = float.NegativeInfinity;
            var firstSeam = "";
            float half = field.Atlas!.HexRect.Size.Y * 0.5f;
            int drops = 0;
            for (int q = 0; q < field.Columns; q++)
            for (int r = 0; r < field.Rows; r++)
            {
                var here = new Vector2I(q, r);
                foreach (int heading in HexField.EdgeHeadings)
                {
                    Vector2I front = HexField.Step(here, heading);
                    if (!field.InBounds(front)
                        || field.FlatAnchor(front).Y <= field.FlatAnchor(here).Y
                        || field.LevelAt(front) >= field.LevelAt(here))
                        continue;
                    drops++;
                    // Bottom of this cell's side against the top of that plate.
                    float past = field.CellCentre(here).Y + half + field.SkirtDrop(here);
                    float lip = field.CellCentre(front).Y - half;
                    if (lip - past <= worstSeam)
                        continue;
                    worstSeam = lip - past;
                    firstSeam = $"({q},{r}) to ({front.X},{front.Y})";
                }
            }
            Check($"no cell parts company with the lower ground in front of it, "
                  + $"over {drops} drops",
                worstSeam <= 0.01f && drops > 0,
                drops == 0 ? "no cell has lower ground in front - it proves nothing"
                           : $"{worstSeam:F1}px of nothing at {firstSeam} - the board "
                             + "shows through between two hexes");

            // Second, the order. A column is in front of another exactly when its
            // ground is, and the failure this guards is a sort key that carries
            // the lift again: it agrees with this one nearly everywhere, so it
            // reads as working and comes apart on the few cells where a rise and
            // the flat ground beside it are within the lift of each other.
            var order = new Dictionary<Vector2I, int>();
            for (int i = 0; i < field.Ordered().Count; i++)
                order[field.Ordered()[i]] = i;
            int late = 0, neighbours = 0;
            var firstLate = "";
            for (int q = 0; q < field.Columns; q++)
            for (int r = 0; r < field.Rows; r++)
            {
                var here = new Vector2I(q, r);
                foreach (int heading in HexField.EdgeHeadings)
                {
                    Vector2I front = HexField.Step(here, heading);
                    if (!field.InBounds(front)
                        || field.FlatAnchor(front).Y <= field.FlatAnchor(here).Y)
                        continue;
                    neighbours++;
                    if (order[front] > order[here])
                        continue;
                    late++;
                    if (firstLate.Length == 0)
                        firstLate = $"({front.X},{front.Y}) before ({q},{r})";
                }
            }
            Check($"and each of the {neighbours} nearer neighbours is painted after the "
                  + "one behind it",
                late == 0 && neighbours > 0,
                neighbours == 0 ? "no neighbour is in front of another - it proves nothing"
                           : $"{late} the wrong way round, first {firstLate}");

            // Third, the repaint. The field paints the whole column and lets the
            // cells in front cover its lower half; ReliefCap paints one cell over
            // one tank with nothing painted after it, so the same column carried
            // whole lands on the cell in front and undoes it. What it may carry
            // is the part standing above the ground the tank is on - a cliff is
            // not visible below the field in front of it - and that has to be
            // both less than the whole side and more than nothing: nothing means
            // the plate is put back without its face and the tank shows through
            // the cliff under it.
            int faces = 0, buried = 0, deep = 0, shown = 0, bites = 0;
            var firstBad = "";
            for (int q = 0; q < field.Columns; q++)
            for (int r = 0; r < field.Rows; r++)
            {
                var stand = new Vector2I(q, r);
                float standing = field.LevelAt(stand) * field.Lift;
                foreach (Vector2I cell in field.Occluders(field.GroundRow(stand), standing))
                foreach (int heading in HexField.NearHeading)
                {
                    faces++;
                    float side = field.VisibleSide(cell, heading, standing);
                    Vector2I front = HexField.Step(cell, heading);
                    // Held up by the ground across that edge, or by the tank's own
                    // plane: either way there is nothing of the cliff above it to
                    // show, and anything carried lands on a cell that is level with
                    // this one and covers all of it.
                    bool hidden = field.InBounds(front)
                                  && field.LevelAt(front) >= field.LevelAt(cell);
                    if (side > 0.01f && (hidden || standing >= field.LevelAt(cell) * field.Lift))
                    {
                        buried++;
                        if (firstBad.Length == 0)
                            firstBad = $"({cell.X},{cell.Y}) toward {heading} over a tank "
                                       + $"on ({q},{r}) carries {side:F0}px onto ground "
                                       + "level with it";
                    }
                    else if (side > field.SkirtDrop(cell) + 0.01f)
                    {
                        deep++;
                        if (firstBad.Length == 0)
                            firstBad = $"({cell.X},{cell.Y}) carries {side:F0}px of a "
                                       + $"{field.SkirtDrop(cell):F0}px side";
                    }
                    if (side > 0.01f)
                        shown++;
                    if (side < field.SkirtDrop(cell) - 0.01f)
                        bites++;
                }
            }
            Check($"a cell repainted over a tank carries only the side standing "
                  + $"above both, over {faces} faces",
                faces > 0 && buried == 0 && deep == 0 && bites > 0 && shown > 0,
                faces == 0 ? "nothing stands over a tank - it proves nothing"
                : shown == 0 ? "no face is ever shown - a repainted plate with no "
                               + "cliff under it lets the tank through"
                : bites == 0 ? "the whole side is always standing proud - this board "
                               + "cannot tell the clip from no clip"
                             : firstBad);

            // Fourth, where the repaint may land. Admitting a cell is all or
            // nothing, so a cell admitted goes down over the ruts and the board
            // wherever it happens to be - and a tank in a pit is below every
            // level cell at once, so the far side of the board qualified. Asked
            // as a subset, plus that the window bites: a window nothing meets is
            // a clause that has stopped being applied.
            int loose = 0, stands = 0, cut = 0;
            var firstLoose = "";
            var span = new Vector2(256.0f, 256.0f);
            for (int q = 0; q < field.Columns; q++)
            for (int r = 0; r < field.Rows; r++)
            {
                var stand = new Vector2I(q, r);
                float standing = field.LevelAt(stand) * lift;
                float row = field.GroundRow(stand);
                var box = new Rect2(field.CellCentre(stand) - span * 0.5f, span);
                var all = field.Occluders(row, standing);
                var near = field.Occluders(row, standing, box);
                stands++;
                cut += all.Count - near.Count;
                foreach (Vector2I cell in near)
                {
                    if (box.Intersects(field.Column(cell)))
                        continue;
                    loose++;
                    if (firstLoose.Length == 0)
                        firstLoose = $"({cell.X},{cell.Y}) over a tank on ({q},{r})";
                }
            }
            Check($"and it is only repainted where it could touch that tank, "
                  + $"over {stands} stands",
                loose == 0 && cut > 0 && stands > 0,
                loose > 0 ? $"{loose} repainted out of reach, first {firstLoose}"
                          : "no cell anywhere is out of reach - this board cannot "
                            + "tell the window from no window");

            // And the test the belt marks are put back by, since a cell repainted
            // from the terrain alone comes back bare. Asked about the drawn top
            // face and nothing else: a mark lies where it lies, and which cell is
            // repainting is already decided by the time this is asked.
            //
            // Not "and on no other" - on a board with height that is false and
            // has to be, because a lifted hexagon overlaps the drawn face of the
            // cell behind it. Six centres on this map do. What is asked instead
            // is the pair that would break the repaint: a face that does not hold
            // its own middle, and one that reaches past its own corner.
            int missed = 0, spilled = 0;
            float reach = field.Atlas!.HexRect.Size.X * 0.5f + 1.0f;
            for (int q = 0; q < field.Columns; q++)
            for (int r = 0; r < field.Rows; r++)
            {
                var cell = new Vector2I(q, r);
                Vector2 centre = field.CellCentre(cell);
                if (!field.OnCell(centre, cell))
                    missed++;
                if (field.OnCell(centre + new Vector2(reach, 0.0f), cell)
                    || field.OnCell(centre + new Vector2(0.0f, reach), cell))
                    spilled++;
            }
            Check("a mark sits on the drawn face of the cell it was laid on",
                missed == 0 && spilled == 0,
                missed > 0 ? $"{missed} faces do not hold their own middle"
                           : $"{spilled} reach past their own corner");

            Theme("relief: a wood on a rise sorts where it stands");
            Woods(field, grove, elevation, Check);

            Theme("relief: a route walks the board it is on");
            int steep = 0;
            var firstSteep = "";
            for (int q = 0; q < field.Columns; q++)
            for (int r = 0; r < field.Rows; r++)
            {
                var from = new Vector2I(q, r);
                for (int c = 0; c < field.Columns; c++)
                for (int d = 0; d < field.Rows; d++)
                {
                    var path = field.FindPath(from, new Vector2I(c, d));
                    Vector2I at = from;
                    foreach (Vector2I next in path)
                    {
                        if (Math.Abs(field.LevelAt(next) - field.LevelAt(at))
                            > field.MaxClimb)
                        {
                            steep++;
                            if (firstSteep.Length == 0)
                                firstSteep = $"({at.X},{at.Y}) to ({next.X},{next.Y})";
                        }
                        at = next;
                    }
                }
            }
            Check("no route takes a step it cannot climb", steep == 0,
                $"{steep} did, first {firstSteep}");
        }
        finally
        {
            field.SetRelief(null);
        }

        // Taking it off again has to leave the board where it was: the panel row
        // turns it off as readily as on.
        Theme("relief: and taking it off puts the board back");
        int moved = 0;
        for (int q = 0; q < field.Columns; q++)
        for (int r = 0; r < field.Rows; r++)
            if (field.CellAnchor(q, r) != field.FlatAnchor(q, r))
                moved++;
        Check("nothing is left lifted", !field.HasRelief && moved == 0,
            $"{moved} cells still lifted");

        // And the claim that says why the draw order behind it can be cached at
        // all. Height moves a cell's depth key by lift*tan(e)^2, and cells sit at
        // least half a row apart down the board: while the one is smaller than
        // the other, <b>relief cannot reorder the cells among themselves</b> -
        // all it reorders is cells against the things standing on them.
        //
        // Worth asserting rather than assuming, because it is a threshold and not
        // a fact: it holds at 1:4 with room to spare and stops holding somewhere
        // past 1:2.4, and the failure is the quietest kind - the board is still
        // drawn, in the sequence the hill wanted, and what it costs is one cell
        // overhanging the wrong neighbour.
        float gap = float.MaxValue;
        for (int q = 0; q < field.Columns; q++)
        for (int r = 0; r < field.Rows; r++)
        for (int c = 0; c < field.Columns; c++)
        for (int d = 0; d < field.Rows; d++)
        {
            float apart = Math.Abs(field.FlatAnchor(q, r).Y - field.FlatAnchor(c, d).Y);
            if (apart > 0.01f)
                gap = Math.Min(gap, apart);
        }
        // Ramp-free on purpose: everything in this topic is the 2D board's
        // occlusion rule, which is written in whole levels and cannot answer for a
        // surface half a level up - see Main.Ramped. The ramps have their own
        // topic and their own board.
        field.SetRelief(BoardMap.Bench.LevelsFor(field.Columns, field.Rows));
        (int low, int high) = field.LevelRange;
        float tan = field.Squash / Math.Max(field.RiseFactor, 0.01f);
        float shift = (high - low) * field.Lift * tan * tan;
        field.SetRelief(null);
        Check("and no cell ever changes places with another over it",
            shift < gap, $"height moves a cell {shift:F1}px of depth against "
                         + $"{gap:F1}px between the closest two");

        // --- the map rules -------------------------------------------------
        //
        // The rules a board is judged by live in MapRules and nowhere else, and
        // this is what pays for that: on boards built to be broken, the
        // validator and the field's own guards have to name the same cell.
        //
        // <b>Why the theme below still writes its statements out by hand.</b> It
        // judges the shipped maps, and a check that reads the implementation it
        // is judging asserts only that the code agrees with itself - the same
        // argument the bench board's second copy is kept for. So the shipped
        // boards keep their hand-written statements, and what this adds is the
        // parity between the two ways of asking.
        Theme("map rules: the validator and the guards agree");
        {
            // Three by three, flat, with one cell a level up and a ramp under
            // it: the smallest board on which a ramp is legal at all.
            MapRules.Draft Board(int[] levels, bool[] ramps, bool[] water) => new()
            {
                Columns = 3, Rows = 3, Levels = levels, Ramps = ramps,
                Water = water,
            };
            int[] Flat() => new int[9];
            bool[] None() => new bool[9];

            int[] one = Flat();
            one[0 * 3 + 1] = 1;            // (1,0) a level up
            bool[] ramp = None();
            ramp[1 * 3 + 1] = true;        // (1,1) bridges it at heading 90

            var legal = Board(one, ramp, None());
            var faults = MapRules.RampFaults(legal);
            Check("a ramp with one neighbour above it and a foot under it is legal",
                faults.Count == 0,
                string.Join("; ", faults.Select(f => f.Text)));
            Check("and the site list offers exactly that cell at that heading",
                MapRules.Sites(legal).TryGetValue(new Vector2I(1, 1), out int up)
                && up == 90,
                string.Join(" ", MapRules.Sites(legal)
                    .Select(kv => $"({kv.Key.X},{kv.Key.Y})@{kv.Value}")));

            // Two neighbours above it: which edge is high is not decided by the
            // map, and this is the one ramp rule the field itself throws on.
            int[] two = Flat();
            two[0 * 3 + 1] = 1;
            two[1 * 3 + 2] = 1;            // (2,1), which is (1,1) stepped at 30
            var broken = Board(two, ramp, None());
            List<MapRules.Fault> bridge = MapRules.RampFaults(broken)
                .Where(f => f.Rule == "ramp.bridge").ToList();
            Check("two neighbours above a ramp is a fault the validator names",
                bridge.Count == 1 && bridge[0].Cell == new Vector2I(1, 1),
                string.Join("; ", bridge.Select(f => f.ToString())));

            var probe = new HexField { Columns = 3, Rows = 3 };
            try
            {
                string threw = "";
                try
                {
                    probe.SetRelief(two, ramp);
                }
                catch (Exception e)
                {
                    threw = e.Message;
                }
                Check("and the field's guard throws the validator's own words",
                    bridge.Count == 1 && threw == bridge[0].Text,
                    $"guard said \"{threw}\"");

                // And the legal board builds, so the parity is not two ways of
                // always refusing.
                string quiet = "";
                try
                {
                    probe.SetRelief(one, ramp);
                }
                catch (Exception e)
                {
                    quiet = e.Message;
                }
                Check("the legal board builds, so neither is refusing everything",
                    quiet.Length == 0 && probe.RampHeading(new Vector2I(1, 1)) == 90,
                    quiet.Length > 0
                        ? quiet
                        : $"heading {probe.RampHeading(new Vector2I(1, 1))}");

                // One body of water is one surface: two wet cells on two levels.
                int[] stepped = Flat();
                stepped[1 * 3 + 0] = -1;   // (0,1), which is (0,0) stepped at 270
                bool[] wet = None();
                wet[0] = true;
                wet[1 * 3 + 0] = true;
                var falls = Board(stepped, None(), wet);
                List<MapRules.Fault> steps = MapRules.StepFaults(falls);
                Check("water on two levels is a waterfall the validator names",
                    steps.Count > 0 && steps[0].Cell == new Vector2I(0, 0),
                    string.Join("; ", steps.Select(f => f.Brief)));

                string refused = "";
                try
                {
                    probe.SetRelief(stepped);
                    probe.SetWater(wet);
                }
                catch (Exception e)
                {
                    refused = e.Message;
                }
                Check("and the field's guard refuses it naming the same pair",
                    steps.Count > 0 && refused.Contains(steps[0].Brief),
                    refused.Length == 0 ? "the guard allowed it" : refused);
            }
            finally
            {
                probe.Free();
            }

            // A home is judged by where it is, and all four ways of being
            // nowhere a tank can park are one rule.
            var parked = Board(one, ramp, None());
            parked.Homes = new[] { new Vector2I(1, 1) };
            Check("a home on a ramp is not somewhere a tank can park",
                MapRules.HomeFaults(parked).Any(f => f.Rule == "home.park"),
                "the validator allowed a home on a ramp");
            parked.Homes = new[] { new Vector2I(0, 2) };
            Check("and on flat ground it is",
                MapRules.HomeFaults(parked).Count == 0,
                string.Join("; ", MapRules.HomeFaults(parked).Select(f => f.Text)));
        }

        // --- the map file --------------------------------------------------
        //
        // A board written to disk and read back has to be the board that was
        // written, cell for cell. That is the whole of what a file format owes,
        // and it is the check the format was built around: BoardMap.Plinth
        // exists because this found a board coming back with its plain ground
        // re-encoded as hills.
        Theme("the map file: written and read back is the board that was");
        {
            foreach (string name in BoardMap.Compiled)
            {
                BoardMap was = BoardMap.ByName(name);
                BoardMap now;
                try
                {
                    now = MapFile.Parse(MapFile.Write(was), name);
                }
                catch (Exception e)
                {
                    Check($"{name}: survives the round trip", false, e.Message);
                    continue;
                }

                string drifted = "";
                if (was.Columns != now.Columns || was.Rows != now.Rows)
                    drifted = $"{now.Columns}x{now.Rows}, was "
                            + $"{was.Columns}x{was.Rows}";
                for (int at = 0; at < was.Levels.Length && drifted.Length == 0; at++)
                {
                    int q = at % was.Columns, r = at / was.Columns;
                    string what =
                        was.Levels[at] != now.Levels[at] ? "level"
                        : was.Ramps[at] != now.Ramps[at] ? "ramp"
                        : was.Water[at] != now.Water[at] ? "water"
                        : was.Cliffs[at] != now.Cliffs[at] ? "rock"
                        : was.Walls[at] != now.Walls[at] ? "wall"
                        : was.Ground[at] != now.Ground[at] ? "floor"
                        : was.Over[at] != now.Over[at] ? "cover"
                        : was.OnBoard(new Vector2I(q, r))
                          != now.OnBoard(new Vector2I(q, r)) ? "plot"
                        : "";
                    if (what.Length > 0)
                        drifted = $"({q},{r}) came back with a different {what}";
                }
                if (drifted.Length == 0 && !was.Homes.SequenceEqual(now.Homes))
                    drifted = "the homes drifted";
                if (drifted.Length == 0 && was.Paint != now.Paint)
                    drifted = $"paint {now.Paint}, was {was.Paint}";
                if (drifted.Length == 0 && was.Height != now.Height)
                    drifted = "height flag flipped";
                Check($"{name}: written and read back, cell for cell",
                    drifted.Length == 0, drifted);

                // Kinds are judged apart from the rest, because the bench is
                // the one board written as three grids and three grids say
                // nothing about what a cell is made of - see BoardMap, which
                // says so. The alphabet does say: '1' IS a rise. So the bench
                // comes back painted, and what is asserted is that the paint it
                // gains is exactly the paint its letters imply and nothing else.
                var painted = new List<string>();
                for (int at = 0; at < was.Kinds.Length; at++)
                    if (was.Kinds[at] != now.Kinds[at])
                        painted.Add($"({at % was.Columns},{at / was.Columns}) "
                                    + $"{was.Kinds[at] ?? "-"} -> "
                                    + $"{now.Kinds[at] ?? "-"}");
                if (name == "bench")
                {
                    bool implied = true;
                    for (int at = 0; at < was.Kinds.Length; at++)
                        if (was.Kinds[at] != now.Kinds[at])
                            implied &= was.Kinds[at] is null
                                       && now.Kinds[at] == TerrainSet.Rise
                                       && was.Levels[at] == 1;
                    Check("bench: the three-grid board gains exactly the paint "
                          + "its letters imply",
                        painted.Count > 0 && implied,
                        painted.Count == 0
                            ? "nothing was painted, so the alphabet stopped "
                              + "naming a rise"
                            : string.Join(" ", painted));
                }
                else
                {
                    Check($"{name}: and its kinds with it",
                        painted.Count == 0, string.Join(" ", painted));
                }
            }

            // And once through an actual file, because everything above is a
            // string in memory and what a format is for is the disk. Written to
            // the scratch folder rather than to maps/, which is judged.
            string scratch = System.IO.Path.Combine(AssetRoot.Out, "maps-selftest");
            try
            {
                string path = MapFile.Save(BoardMap.Abbey,
                    System.IO.Path.Combine(scratch, "abbey.json"));
                BoardMap back = MapFile.Read(path, "abbey");
                Check("and once through a real file on disk",
                    back.Columns == BoardMap.Abbey.Columns
                    && back.Levels.SequenceEqual(BoardMap.Abbey.Levels),
                    $"{path} came back {back.Columns}x{back.Rows}");
            }
            catch (Exception e)
            {
                Check("and once through a real file on disk", false, e.Message);
            }
            finally
            {
                try
                {
                    if (System.IO.Directory.Exists(scratch))
                        System.IO.Directory.Delete(scratch, true);
                }
                catch (Exception)
                {
                    // Leaving a scratch folder behind is not a failed check.
                }
            }

            // What the reader refuses, and every one of these reads as something
            // else when it is let through: a ragged row becomes a column of
            // plain nobody drew, a stale size field becomes a board of the wrong
            // shape, a map answering to two names becomes a board judged as one
            // and shown as the other.
            string good = MapFile.Write(BoardMap.Test);
            string Refusal(string text, string name)
            {
                try
                {
                    MapFile.Parse(text, name);
                    return "";
                }
                catch (Exception e)
                {
                    return e.Message;
                }
            }

            Check("the good file parses, so the refusals are not two ways of "
                  + "always refusing",
                Refusal(good, "test").Length == 0, Refusal(good, "test"));
            Check("a ragged row is refused rather than clipped",
                Refusal(good.Replace("\"" + MapFile.Ground(BoardMap.Test)[0] + "\"",
                                     "\"" + MapFile.Ground(BoardMap.Test)[0][1..] + "\""),
                        "test").Length > 0,
                "a short row was accepted");
            Check("a size that stopped agreeing with the drawing is refused",
                Refusal(good.Replace($"[{BoardMap.Test.Columns}, {BoardMap.Test.Rows}]",
                                     $"[{BoardMap.Test.Columns + 1}, {BoardMap.Test.Rows}]"),
                        "test").Length > 0,
                "a stale size was accepted");
            Check("a file calling itself something else is refused",
                Refusal(good, "elsewhere").Length > 0,
                "a map answered to a name it does not carry");
            Check("a format this reader does not speak is refused",
                Refusal(good.Replace("\"format\": 1", "\"format\": 2"), "test")
                    .Length > 0,
                "a later format was read as far as it happened to parse");
            Check("a board with no homes is refused",
                Refusal(good.Replace(
                    "\"homes\": [" + string.Join(", ",
                        BoardMap.Test.Homes.Select(h => $"[{h.X}, {h.Y}]")) + "]",
                    "\"homes\": []"), "test").Length > 0,
                "a board nothing can park on was accepted");

            // The clash rule, asserted on the list rather than by writing a file
            // into maps/ - which would be a check that leaves a second reference
            // behind if it dies halfway.
            // The other end of the same rule, and the end that actually
            // happens: a board opened from the compiled ABBEY keeps its name, so
            // the ordinary way to get two references under one name is to press
            // save. Refused before anything is written - by the time ByName
            // refuses, the file is on disk and every scene is broken.
            string saved = "";
            try
            {
                MapFile.Save(BoardMap.Abbey);
            }
            catch (Exception e)
            {
                saved = e.Message;
            }
            Check("and saving one under that name is refused before it is written",
                saved.Length > 0 && !MapFile.Has("abbey"),
                saved.Length == 0
                    ? "a compiled board was written into maps/"
                    : saved);

            Check("a map file may not answer to a compiled board's name",
                MapFile.Clashes(BoardMap.Compiled).Count == 0,
                "these shadow a compiled map: "
                + string.Join(" ", MapFile.Clashes(BoardMap.Compiled)));
            Check("and the compiled five are all still there",
                BoardMap.Compiled.All(n => BoardMap.Names.Contains(n)),
                string.Join(" ", BoardMap.Names));
        }

        // --- the alphabet and the brushes ----------------------------------
        //
        // The letters are decoded in two places - BoardMap.FromGround, which is
        // the reader of record, and MapFile.Alphabet, which the writer and the
        // editor need - so the first thing asserted is that the two say the same
        // thing about every letter there is. Everything after it is the brushes,
        // and none of it needs a scene: a board being drawn is two grids.
        Theme("the map alphabet: both readers of a letter say the same thing");
        {
            foreach (MapFile.Glyph glyph in MapFile.Alphabet)
            {
                // One letter on a board of one cell. Small enough that no guard
                // has a neighbour to object about, which is what lets every
                // letter be asked about on its own.
                BoardMap one;
                try
                {
                    one = BoardMap.FromGround(
                        $"glyph{glyph.Letter}", new[] { glyph.Letter.ToString() },
                        new[] { "." },
                        new Parking[] { new Vector2I(0, 0) },
                        TerrainSet.Mixed, true);
                }
                catch (Exception e)
                {
                    Check($"'{glyph.Letter}' reads as a board of one cell", false,
                        e.Message);
                    continue;
                }

                var cell = new Vector2I(0, 0);
                bool on = glyph.Letter != BoardMap.Off;
                string said =
                    one.OnBoard(cell) != on ? "on the board"
                    : !on ? ""
                    : one.Ground[0] != glyph.Floor ? $"floor {one.Ground[0]}"
                    : one.Over[0] != glyph.Over ? $"cover {one.Over[0]}"
                    : one.Levels[0] != glyph.Level ? $"level {one.Levels[0]}"
                    : one.Water[0] != glyph.Water ? $"water {one.Water[0]}"
                    : one.Cliffs[0] != glyph.Cliff ? $"rock {one.Cliffs[0]}"
                    : one.Walls[0] != glyph.Wall ? $"wall {one.Walls[0]}"
                    : glyph.Wood && one.Kinds[0] != TerrainSet.Forest
                        ? $"kind {one.Kinds[0] ?? "-"}"
                        : "";
                Check($"'{glyph.Letter}': the table and BoardMap agree",
                    said.Length == 0,
                    said.Length == 0 ? "" : $"the map says {said}");

                // And back the other way, so the table is a bijection rather
                // than two lists that happen to overlap.
                if (on)
                    Check($"'{glyph.Letter}': and the axes come back to it",
                        MapFile.Letter(glyph.Floor, glyph.Over, glyph.Level,
                                       glyph.Water, glyph.Cliff)
                        == glyph.Letter,
                        "came back as "
                        + (MapFile.Letter(glyph.Floor, glyph.Over, glyph.Level,
                                          glyph.Water, glyph.Cliff)
                           ?? '?').ToString());
            }

            Check("no letter appears twice in the alphabet",
                MapFile.Alphabet.Select(g => g.Letter).Distinct().Count()
                == MapFile.Alphabet.Count,
                new string(MapFile.Alphabet.Select(g => g.Letter).ToArray()));
        }

        Theme("the map brushes: a board being drawn is two grids");
        {
            // Opened, written out, and it is the board that was opened.
            var edit = MapEdit.Of(BoardMap.Abbey);
            Check("a map opened for editing writes back its own picture",
                edit.Ground().SequenceEqual(MapFile.Ground(BoardMap.Abbey))
                && edit.Ramps().SequenceEqual(MapFile.Ramps(BoardMap.Abbey)),
                "the grids came back different");
            BoardMap rebuilt = edit.Build();
            Check("and builds the board it was opened from",
                rebuilt.Levels.SequenceEqual(BoardMap.Abbey.Levels)
                && rebuilt.Water.SequenceEqual(BoardMap.Abbey.Water)
                && rebuilt.Ramps.SequenceEqual(BoardMap.Abbey.Ramps),
                "the arrays moved");

            // A fresh board, which is where the brushes are judged: nothing on
            // it is load-bearing, so a check that breaks it says what it meant.
            // On the shipped default rather than a size picked here, so what the
            // brushes are judged on is the board the tool actually opens with.
            var draw = MapEdit.Blank("scratch", MapEdit.DefaultColumns,
                                     MapEdit.DefaultRows);
            Check("a new board is open ground with one home on it",
                draw.Ground().All(row => row.All(c => c == BoardMap.Plain))
                && draw.Homes.Count == 1
                && draw.Columns == 10 && draw.Rows == 10,
                $"{draw.Columns}x{draw.Rows}: " + string.Join("/", draw.Ground()));
            Check("and the dialog's suggested proportion is square on the ground",
                MapEdit.Square(20) == 17,
                $"20 columns suggested {MapEdit.Square(20)} rows");

            // One stroke, many cells, one undo.
            var middle = new Vector2I(4, 4);
            draw.Begin();
            IReadOnlyList<Vector2I> patch = draw.Ring(middle, 1);
            Check("a brush of radius one covers a cell and its six neighbours",
                patch.Count == 7, $"{patch.Count} cells");
            IReadOnlyList<string> refused = draw.Sweep(patch,
                c => (draw.Over(c, Cover.Forest, out string why), why));
            Check("and paints all of them",
                refused.Count == 0 && patch.All(
                    c => draw.LetterAt(c) == BoardMap.Wood),
                string.Join("; ", refused));
            Check("one stroke is one entry on the stack",
                draw.Depth == 1, $"{draw.Depth} entries");
            draw.Undo();
            Check("and one undo takes the whole stroke back",
                patch.All(c => draw.LetterAt(c) == BoardMap.Plain)
                && draw.Depth == 0,
                string.Join(" ", patch.Where(
                    c => draw.LetterAt(c) != BoardMap.Plain)
                    .Select(c => $"({c.X},{c.Y})")));
            draw.Redo();
            Check("redo puts it back",
                patch.All(c => draw.LetterAt(c) == BoardMap.Wood),
                "the stroke did not come back");
            draw.Undo();

            // The fill fills what is drawn.
            Check("the fill takes every cell of one letter",
                draw.Region(middle).Count == draw.Columns * draw.Rows,
                $"{draw.Region(middle).Count} of {draw.Columns * draw.Rows}");

            // What the alphabet cannot carry, and every one of these is a cell
            // that could not be saved if the brush let it through.
            draw.Begin();
            draw.Level(middle, 1, out _);
            Check("sand does not lie on a rise, and the brush says so",
                !draw.Floor(middle, Foundation.Sand, out string sandWhy),
                "sand was painted on to level " + draw.GlyphAt(middle).Level);
            Note("    " + sandWhy);
            Check("a floor made of water has no letter",
                !draw.Floor(middle, Foundation.Shallow, out string wetWhy),
                "shallow was painted");
            Note("    " + wetWhy);
            Check("level two is the rock, and it is declared rather than raised "
                  + "into",
                !draw.Level(middle, 2, out string highWhy),
                "a cell was raised to a rock");
            Note("    " + highWhy);
            draw.Undo();

            // The rock is the whole cell.
            draw.Begin();
            Check("a rock is painted on to open ground",
                draw.Floor(middle, Foundation.Rock, out string rockWhy)
                && draw.LetterAt(middle) == BoardMap.Rock, rockWhy);
            Check("nothing stands on a rock",
                !draw.Over(middle, Cover.Forest, out _),
                "a wood was planted on a rock");
            Check("and no ramp climbs off one",
                !draw.Ramp(middle, true, out _), "a rock took a ramp");
            draw.Undo();

            // Water carries its own level, and that is the letter rather than a
            // side effect.
            draw.Begin();
            Check("water stands in low ground, because that is what the letter "
                  + "says",
                draw.Water(middle, true, out _)
                && draw.GlyphAt(middle).Level == -1
                && draw.LetterAt(middle) == BoardMap.Wet,
                $"the cell came out {draw.LetterAt(middle)} at level "
                + $"{draw.GlyphAt(middle).Level}");
            Check("and raising it out of the water is refused",
                !draw.Level(middle, 0, out _),
                "water was lifted to the datum");
            draw.Undo();

            // The eraser means a different cell on each tab.
            draw.Begin();
            draw.Over(middle, Cover.Trench, out _);
            draw.Erase(middle, MapEdit.Tab.Cover, out _);
            Check("the eraser on the cover tab takes the cover off",
                draw.LetterAt(middle) == BoardMap.Plain,
                $"left {draw.LetterAt(middle)}");
            draw.Level(middle, 1, out _);
            draw.Ramp(middle, true, out _);
            draw.Erase(middle, MapEdit.Tab.Level, out _);
            Check("and on the level tab it puts the cell back on the datum",
                draw.GlyphAt(middle).Level == 0 && !draw.IsRamp(middle),
                $"level {draw.GlyphAt(middle).Level}, ramp "
                + $"{draw.IsRamp(middle)}");
            draw.Undo();

            // The last home is not droppable, and a home's cell is not
            // removable - both are the same rule from two ends.
            Check("the last home cannot be dropped",
                !draw.DropHome(0, out _), "a board was left with no homes");
            Check("and a home's cell cannot be taken off the board",
                !draw.Plot(draw.Homes[0].Cell, false, out _),
                "a home was left standing off the board");

            // The draft the rules are asked about is built from the letters, so
            // it cannot disagree with what is drawn.
            draw.Begin();
            draw.Level(new Vector2I(4, 3), 1, out _);
            draw.Ramp(middle, true, out _);
            MapRules.Draft drafted = draw.Draft();
            Check("a new board stands on a plinth, so a flat one has sides",
                draw.Plinth == 1,
                $"plinth {draw.Plinth} - a board flat on the datum is hexagons "
                + "of ground and nothing else");
            Check("the draft the rules see is the picture that is drawn",
                // Against the plinth, because the letters are written relative
                // to it and the draft carries the board's own heights - the two
                // differ by exactly the plinth, and this check is where that
                // first showed.
                drafted.LevelAt(new Vector2I(4, 3)) == 1 + draw.Plinth
                && drafted.IsRamp(middle)
                && drafted.LevelAt(middle) == draw.Plinth,
                $"level {drafted.LevelAt(new Vector2I(4, 3))} against plinth "
                + $"{draw.Plinth}, ramp {drafted.IsRamp(middle)}");
            Check("and a ramp with one neighbour above it draws no fault",
                draw.Faults().Count(f => f.Fatal) == 0,
                string.Join("; ", draw.Faults().Where(f => f.Fatal)
                    .Select(f => f.Text)));

            // An illegal draft is a list rather than a refusal - which is the
            // whole reason MapRules returns one - and the board is still there
            // to go on drawing.
            draw.Level(new Vector2I(4, 5), 1, out _);
            Check("a ramp between two rises is a fault, not a refused brush",
                draw.Faults().Any(f => f.Rule == "ramp.bridge"
                                       && f.Cell == middle),
                string.Join("; ", draw.Faults().Select(f => f.Text)));
            Check("and the letters are still what they were painted",
                draw.IsRamp(middle)
                && draw.GlyphAt(new Vector2I(4, 5)).Level == 1,
                "the draft rolled a brush back");
            draw.Undo();
        }

        Theme("the masonry: six edges, one wall, and a file that says so");
        {
            // The mask and the run invert each other, over every wall there is.
            // Sixty-one of them: six bearings by five counts, plus the ring.
            var wrong = new List<string>();
            foreach (int sides in new[] { 1, 2, 3, 4, 5, 6 })
            foreach (int bearing in sides % 2 == 1
                         ? Masonry.Headings
                         : new[] { 0, 60, 120, 180, 240, 300 })
            {
                int mask = Masonry.Fan(bearing, sides);
                int count = Enumerable.Range(0, 6)
                    .Count(i => (mask & (1 << i)) != 0);
                (int Bearing, int Sides)? back = Masonry.Run(mask);
                if (count != sides)
                    wrong.Add($"{bearing}/{sides} covers {count}");
                else if (back is not (int said, int many)
                         || many != sides
                         || (sides < 6 && said != bearing))
                    wrong.Add($"{bearing}/{sides} came back as {back}");
            }
            Check("a fan of sides and the mask it covers invert each other",
                wrong.Count == 0, string.Join("; ", wrong));

            // Two runs, which is the one mask that is not a wall. 330 and 210
            // with 270 taken out of the middle of them.
            int split = (1 << Masonry.Bit(330)) | (1 << Masonry.Bit(210));
            Check("edges in two runs are not one wall",
                Masonry.Run(split) is null, $"{split:b} came back a wall");
            Check("and neither is a cell with nothing standing",
                Masonry.Run(0) is null, "an empty mask claimed to be a wall");

            // The bit order is the geometry's, and this is what makes a run of
            // bits a run of edges - see Masonry.Headings, which is deliberately
            // not HexField.EdgeHeadings.
            Check("the mask's bits run round the cell in order",
                Enumerable.Range(0, 6).All(
                    i => Masonry.Bit(Masonry.Headings[i]) == i),
                string.Join(" ", Masonry.Headings.Select(Masonry.Bit)));

            // --- the brush -----------------------------------------------
            var draw = MapEdit.Blank("masonry", 6, 5);
            var cell = new Vector2I(2, 2);
            Check("a cell with no wall on it carries no masonry",
                draw.MasonryAt(cell) is null, "it had an entry");
            draw.Over(cell, Cover.Walls, out string why);
            Check("painting masonry lays a closed ring", why.Length == 0
                && draw.MasonryAt(cell) is { Edges: Masonry.Ring },
                why.Length > 0 ? why : "the ring was not laid");

            Check("and taking three sides off leaves one run",
                draw.Edge(cell, 30, false, out _)
                && draw.Edge(cell, 90, false, out _)
                && draw.Edge(cell, 150, false, out _)
                && draw.MasonryAt(cell)!.Run() is (270, 3),
                draw.MasonryAt(cell)!.Say());

            // The last side is refused, and the reason is not tidiness: masonry
            // standing on nothing is CoverState.Breached, which is what happens
            // to a wall in a game rather than what a map declares.
            foreach (int heading in new[] { 330, 210 })
                draw.Edge(cell, heading, false, out _);
            Check("the last side of a wall cannot be taken off",
                !draw.Edge(cell, 270, false, out string last)
                && last.Contains("breach"),
                last);

            Check("a dial out of range is refused by name",
                !draw.Shape(cell, Masonry.Dial.Leaves, 99, out string tall)
                && tall.Contains("leaves"), tall);
            Check("and one in range is taken and can be taken back",
                draw.Shape(cell, Masonry.Dial.Leaves, 3, out _)
                && draw.MasonryAt(cell)!.Leaves == 3
                && draw.Shape(cell, Masonry.Dial.Leaves, null, out _)
                && draw.MasonryAt(cell)!.Leaves is null,
                "the dial did not answer");

            // The letter is what says a cell carries a wall, so the entry
            // follows the letter both ways - see MapEdit.Sync.
            draw.Shape(cell, Masonry.Dial.Seed, 7, out _);
            draw.Over(cell, Cover.Forest, out _);
            Check("a cell that stops being a wall loses its masonry",
                draw.MasonryAt(cell) is null, "the entry outlived the letter");
            draw.Over(cell, Cover.Walls, out _);
            Check("and one painted again comes back a plain ring",
                draw.MasonryAt(cell) is { Edges: Masonry.Ring, Seed: null },
                "a remembered shape came back");

            // One stroke, one undo - the checkbox's end of MapEdit.Begin's rule.
            draw.Begin();
            draw.Edge(cell, 30, false, out _);
            draw.Undo();
            Check("an undo brings a wall's own edges back",
                draw.MasonryAt(cell)!.Edges == Masonry.Ring,
                draw.MasonryAt(cell)!.Say());

            // --- the join to the builder ---------------------------------
            //
            // Silence takes WallKit's own figures, which is what makes a tick in
            // the panel start where the untouched wall already was. The floor of
            // the range is what it used to start from, and a wall "configured"
            // by ticking all four came out one course of one leaf.
            var plain = new Masonry();
            var stock = new WallKit.Recipe();
            (WallKit.Recipe Recipe, int Bearing)? laid = plain.Laying();
            Check("a wall that said nothing lays WallKit's own recipe",
                laid is not null
                && laid.Value.Recipe.Seed == stock.Seed
                && laid.Value.Recipe.Columns == stock.Columns
                && laid.Value.Recipe.Courses == stock.Courses
                && laid.Value.Recipe.Leaves == stock.Leaves
                && laid.Value.Recipe.Sides == 6,
                laid is null ? "it laid nothing" : plain.Say());
            Check("and the panel's tick starts from those same figures",
                Masonry.Dials.All(
                    d => Masonry.Silence(d) == d switch
                    {
                        Masonry.Dial.Seed => stock.Seed,
                        Masonry.Dial.Columns => stock.Columns,
                        Masonry.Dial.Courses => stock.Courses,
                        _ => stock.Leaves,
                    }),
                string.Join(" ", Masonry.Dials.Select(Masonry.Silence)));

            var told = new Masonry
            {
                Edges = Masonry.Fan(270, 3), Seed = 42, Courses = 5,
            };
            (WallKit.Recipe Recipe, int Bearing)? one = told.Laying();
            Check("a wall that spoke lays what it said and no more",
                one is (WallKit.Recipe spoke, 270)
                && spoke.Seed == 42 && spoke.Courses == 5 && spoke.Sides == 3
                && spoke.Columns == stock.Columns
                && spoke.Leaves == stock.Leaves,
                told.Say());

            Check("and two runs lay nothing at all",
                new Masonry { Edges = split }.Laying() is null,
                "two walls on one cell were handed to the builder as one");

            // --- the file ------------------------------------------------
            draw.Edge(cell, 30, false, out _);
            draw.Edge(cell, 90, false, out _);
            draw.Shape(cell, Masonry.Dial.Seed, 42, out _);
            draw.Shape(cell, Masonry.Dial.Courses, 5, out _);
            BoardMap built = draw.Build();
            BoardMap read = MapFile.Parse(MapFile.Write(built), built.Name);
            Masonry? there = read.MasonryAt(cell);
            Check("a wall written to a file and read back is the wall that was",
                there is not null && there.Edges == built.MasonryAt(cell)!.Edges
                && there.Seed == 42 && there.Courses == 5
                && there.Columns is null && there.Leaves is null,
                there?.Say() ?? "nothing came back");

            // A ring that said nothing is not written at all, because the letter
            // already says it - and it still reads back as a ring.
            var ring = MapEdit.Blank("bare", 5, 4);
            ring.Over(new Vector2I(1, 1), Cover.Walls, out _);
            string text = MapFile.Write(ring.Build());
            Check("a wall that said nothing puts no line in the file",
                !text.Contains("walls"), "the block was written anyway");
            Check("and still comes back a closed ring",
                MapFile.Parse(text, "bare")
                    .MasonryAt(new Vector2I(1, 1))!.Edges == Masonry.Ring,
                "the ring did not survive the round trip");

            // Masonry on open ground is the map disagreeing with itself, and it
            // reads as a wall shape that quietly did nothing.
            string stray = text.TrimEnd().TrimEnd('}').TrimEnd()
                           + ",\n  \"walls\": [ { \"at\": [3, 3] } ]\n}\n";
            string strayed = "";
            try
            {
                MapFile.Parse(stray, "bare");
            }
            catch (Exception e)
            {
                strayed = e.Message;
            }
            Check("masonry on a cell with no wall is refused",
                strayed.Contains("carrying no wall"), strayed);

            // And the audit says which walls are two walls.
            var two = MapEdit.Blank("two", 5, 4);
            var far = new Vector2I(2, 2);
            two.Over(far, Cover.Walls, out _);
            foreach (int heading in new[] { 270, 150, 30 })
                two.Edge(far, heading, false, out _);
            Check("a wall drawn as two runs is a fault and not a refusal",
                two.Faults().Any(f => f.Rule == "wall.run" && f.Cell == far
                                      && !f.Fatal),
                string.Join("; ", two.Faults().Select(f => f.Rule)));
        }

        Theme("the parkings: where the tanks stand and who the board asked for");
        {
            // --- the pairing, without a board ----------------------------
            string[] loaded = MovementProfile.Tags;
            var plain = new Parking[]
            {
                new Vector2I(1, 1), new Vector2I(2, 1), new Vector2I(3, 1),
            };
            Check("a board that names no class pairs in order, as it always did",
                Parking.Pair(plain, loaded).SequenceEqual(new[] { 0, 1, 2 }),
                string.Join(" ", Parking.Pair(plain, loaded)));

            // The last parking asks for the first tank, so it moves and the
            // other two close up behind it.
            var named = new Parking[]
            {
                new Vector2I(1, 1), new Vector2I(2, 1),
                new Parking(new Vector2I(3, 1), loaded[0]),
            };
            Check("and a parking that names a class takes that tank",
                Parking.Pair(named, loaded).SequenceEqual(new[] { 2, 0, 1 }),
                string.Join(" ", Parking.Pair(named, loaded)));

            // The failure this replaced: Homes[Math.Min(i, Count - 1)] put every
            // extra tank on the last parking, and three tanks inside each other
            // is one tank on screen.
            var one = new Parking[] { new Vector2I(6, 6) };
            Check("a tank with no parking is -1 rather than stacked on the last",
                Parking.Pair(one, loaded).SequenceEqual(new[] { 0, -1, -1 }),
                string.Join(" ", Parking.Pair(one, loaded)));

            // --- what a board may declare --------------------------------
            foreach ((string tag, string wrong, string what) in new[]
                     {
                         ("NOPE", "class NOPE", "a class no build has"),
                         (loaded[0], "two homes ask", "one class twice"),
                     })
            {
                var homes = new Parking[]
                {
                    new Parking(new Vector2I(1, 1), loaded[0]),
                    new Parking(new Vector2I(2, 1), tag),
                };
                string said = "";
                try
                {
                    BoardMap.FromGround("crew", new[] { "...", "..." },
                        new[] { "...", "..." }, homes, TerrainSet.Mixed, true);
                }
                catch (Exception e)
                {
                    said = e.Message;
                }
                Check($"a board asking for {what} is refused",
                    said.Contains(wrong), said.Length == 0 ? "it was taken" : said);
            }

            // --- the file ------------------------------------------------
            var draw = MapEdit.Blank("parked", 6, 5);
            draw.Home(new Vector2I(1, 1), 1, out _);
            Check("a fresh home names nobody",
                draw.Homes[1].Class is null, draw.Homes[1].ToString());

            Check("a class this build has not got is refused by name",
                !draw.Crew(1, "NOPE", out string nope)
                && nope.Contains("not a class"), nope);
            string twice = "";
            Check("and one another home already asked for, by which home",
                draw.Crew(0, loaded[0], out _)
                && !draw.Crew(1, loaded[0], out twice)
                && twice.Contains("home 1"), twice);
            Check("moving a home keeps the class it asked for",
                draw.Home(new Vector2I(2, 2), 0, out _)
                && draw.Homes[0] == new Parking(new Vector2I(2, 2), loaded[0]),
                draw.Homes[0].ToString());

            string text = MapFile.Write(draw.Build());
            BoardMap back = MapFile.Parse(text, "parked");
            Check("a class written to a file and read back is the class that was",
                back.Parked[0] == draw.Homes[0]
                && back.Parked[1] == draw.Homes[1],
                string.Join(" ", back.Parked));
            Check("the home that named nobody is still written as a bare pair",
                text.Contains($"[{draw.Homes[1].Cell.X}, {draw.Homes[1].Cell.Y}]"),
                text.Split("\"homes\"")[1].Split("\n")[0]);

            // Every shipped board writes the shape it always wrote, so no file
            // on disk and no compiled map changes because this field exists.
            var rewritten = new List<string>();
            foreach (string name in BoardMap.Compiled)
            {
                BoardMap map = BoardMap.ByName(name);
                if (map.Parked.Any(p => p.Class is not null))
                    rewritten.Add(name);
            }
            Check("no shipped board asks for a class, so none of them moved",
                rewritten.Count == 0, string.Join(" ", rewritten));
        }

        // --- the authored boards ------------------------------------------
        //
        // Run here because a map is data and nothing on the board it describes
        // has to exist to judge it - levels, ramp headings, the flood guards and
        // reachability are all grid arithmetic on a probe field. So the map that
        // no scene in this session opened is still checked, which is the point:
        // --map-report is the authoring tool and is run by hand, and an edit made
        // three months from now will be judged by this instead.
        Theme("the authored boards");
        foreach (string name in BoardMap.Names)
        {
            BoardMap map;
            try
            {
                map = BoardMap.ByName(name);
            }
            catch (Exception e)
            {
                Check($"{name} loads at all", false, e.Message);
                continue;
            }

            var probe = new HexField
            {
                Columns = map.Columns, Rows = map.Rows, Plot = map.Plot,
            };
            try
            {
                // The field's own guards, in the order it demands: heights, then
                // the ramps derived off them, then the flood judged against both.
                string refused = "";
                try
                {
                    probe.SetRelief(map.Levels, map.Ramps);
                    if (map.Water.Any(w => w))
                        probe.SetWater(map.Water);
                }
                catch (Exception e)
                {
                    refused = e.Message;
                }
                Check($"{name}: every level change goes through a legal ramp",
                    refused.Length == 0, refused);
                if (refused.Length > 0)
                    continue;

                // The check the field does not make. A ramp is entered along its
                // axis only and the level it reaches at the low end is its own,
                // so a ramp whose low axis neighbour is off the board, on another
                // level or another ramp can only be entered from the top and left
                // the same way - a slope on the hillside leading back to it.
                // Nothing throws and nothing on screen says so.
                var stumps = new List<string>();
                var onRock = new List<string>();
                for (int r = 0; r < map.Rows; r++)
                for (int q = 0; q < map.Columns; q++)
                {
                    var cell = new Vector2I(q, r);
                    int up = probe.RampHeading(cell);
                    if (up < 0)
                        continue;
                    Vector2I foot = HexField.Step(cell, HexField.Reverse(up));
                    if (!probe.InBounds(foot) || probe.RampHeading(foot) >= 0
                        || probe.LevelAt(foot) != probe.LevelAt(cell))
                        stumps.Add($"({q},{r})");
                    if (map.IsCliff(HexField.Step(cell, up)))
                        onRock.Add($"({q},{r})");
                }
                Check($"{name}: no ramp is a dead end at its own foot",
                    stumps.Count == 0, string.Join(" ", stumps));
                Check($"{name}: no ramp hands away a rock",
                    onRock.Count == 0, string.Join(" ", onRock));

                // Reachability, and it is only a question because the rock is
                // declared: a valley cut off by accident and a rise meant to be
                // unclimbable are the same arithmetic, and the letter is what
                // tells them apart. Both halves are asserted - a map where
                // everything is reachable has no rocks in it, and one where the
                // rocks are climbable has no cliffs.
                foreach (Vector2I home in map.Homes)
                {
                    // Not "on the datum", which was the first draft and was
                    // wrong about a board it had no business judging: the bench
                    // parks its light on the rosette's hill on purpose, a level
                    // up, because a hill with six approaches is worth having a
                    // tank on. What a home has to be is somewhere with one
                    // surface height to stand at and a way off it.
                    Check($"{name}: home ({home.X},{home.Y}) is somewhere a tank "
                          + "can park",
                        map.OnBoard(home) && !map.IsCliff(home)
                        && probe.RampHeading(home) < 0
                        && !map.Water[map.At(home)],
                        (map.OnBoard(home) ? "" : "off the board")
                        + (map.IsCliff(home) ? "on a rock" : "")
                        + (probe.RampHeading(home) >= 0 ? "on a ramp" : "")
                        + (map.OnBoard(home) && map.Water[map.At(home)]
                            ? "in the water" : ""));

                    var seen = new HashSet<Vector2I> { home };
                    var queue = new Queue<Vector2I>();
                    queue.Enqueue(home);
                    while (queue.Count > 0)
                    {
                        Vector2I at = queue.Dequeue();
                        foreach (int h in HexField.EdgeHeadings)
                            if (probe.Passable(at, h)
                                && seen.Add(HexField.Step(at, h)))
                                queue.Enqueue(HexField.Step(at, h));
                    }

                    var stranded = new List<string>();
                    var climbed = new List<string>();
                    for (int r = 0; r < map.Rows; r++)
                    for (int q = 0; q < map.Columns; q++)
                    {
                        var cell = new Vector2I(q, r);
                        if (!map.OnBoard(cell))
                            continue;
                        if (map.IsCliff(cell) && seen.Contains(cell))
                            climbed.Add($"({q},{r})");
                        else if (!map.IsCliff(cell) && !seen.Contains(cell))
                            stranded.Add($"({q},{r})");
                    }
                    Check($"{name}: everything but the rocks can be reached from "
                          + $"({home.X},{home.Y})",
                        stranded.Count == 0,
                        $"{stranded.Count} stranded: "
                        + string.Join(" ", stranded.Take(20)));
                    Check($"{name}: and no rock can be, from ({home.X},{home.Y})",
                        climbed.Count == 0,
                        $"{climbed.Count} climbable: "
                        + string.Join(" ", climbed.Take(20)));
                }

                // Water finds its level, so a flat cell at a surface's own
                // height beside it is under it. SetWater refuses this, which
                // means the check above already caught it - what this adds is
                // the closure: how much would be under water, not where it gets
                // in. A board naming one wrong cell in a wide basin is refused
                // for one pair and has to be redrawn for twenty.
                IReadOnlyList<Vector2I> flood = map.Flood();
                Check($"{name}: nothing dry stands at a water surface's own "
                      + "height",
                    flood.Count == 0,
                    $"{flood.Count} under water: "
                    + string.Join(" ", flood.Take(20)
                        .Select(c => $"({c.X},{c.Y})")));

                // And the exemption is load-bearing rather than decorative: a
                // shore is a ramp that runs into the water, so every board with
                // water on it has one, and without the exemption every one of
                // them is refused. Named because it reads as an omission -
                // "water beside dry land" is exactly what a beach is.
                var shores = new List<string>();
                for (int r = 0; r < map.Rows; r++)
                for (int q = 0; q < map.Columns; q++)
                {
                    var cell = new Vector2I(q, r);
                    if (!map.OnBoard(cell) || probe.RampHeading(cell) < 0)
                        continue;
                    foreach (int h in HexField.EdgeHeadings)
                    {
                        Vector2I next = HexField.Step(cell, h);
                        if (probe.InBounds(next) && map.Water[map.At(next)]
                            && probe.LevelAt(next) == probe.LevelAt(cell))
                            shores.Add($"({q},{r})");
                    }
                }
                if (map.Water.Any(w => w))
                    Check($"{name}: its shore is a ramp running into the water",
                        shores.Count > 0,
                        "no ramp touches the water at its own level, so the "
                        + "board has no beach and the exemption is untested");

                // A board that authored its kinds and comes up on a named plate
                // has its kinds shadowed by it - see BoardMap.Paint, which exists
                // because that happened and cost every tree on the map. Silent:
                // the board draws, the plate is a real plate, and the woods are
                // simply not there.
                bool authored = map.Kinds.Any(k => k is not null);
                Check($"{name}: {(authored ? "authored kinds come up on the mix"
                                           : "a board with no kinds names a plate")}",
                    authored
                        ? map.Paint == TerrainSet.Mixed
                        : map.Paint != TerrainSet.Mixed,
                    $"paint is {map.Paint}");
            }
            finally
            {
                probe.Free();
            }
        }

        // Sea level, both halves, on a board built for it: a column of three
        // cells, plain on top and a level below it one water cell and one dry
        // cell between them. The two runs differ by a single bool, so neither
        // can pass for the wrong reason - flat, the middle cell is a hole in a
        // lake and floods; as a ramp it is a beach and dams, and the cell above
        // it stays dry because it is a level up.
        //
        // Built rather than read off the shipped boards, because both of them
        // obey the rule and a guard nothing ever trips is a guard whose state
        // nobody knows.
        for (int pass = 0; pass < 2; pass++)
        {
            bool beach = pass == 0;
            IReadOnlyList<Vector2I> under = BoardMap.Flood(
                1, 3, new[] { -1, -1, 0 }, new[] { false, beach, false },
                new[] { true, false, false }, null);
            Check(beach
                    ? "a ramp running into the water is a shore, not a hole"
                    : "and flat, the same cell is a hole in a lake",
                beach
                    ? under.Count == 0
                    : under.Count == 1 && under[0] == new Vector2I(0, 1),
                beach
                    ? $"{under.Count} cells flooded through a beach"
                    : $"{under.Count} flooded, wanted just the dry middle");
        }

        // The bench board is the one nothing may change, and this is a golden:
        // the three grids written out a second time, here, and decoded here.
        //
        // A second copy on purpose, which is the opposite of what this file does
        // everywhere else, and the reason is that the old form could not fail.
        // It compared BoardMap.Bench against Main's three decoders - and those
        // decoders read the very grids BoardMap.Bench was built from, so editing
        // a character moved both sides together and the check passed. What it
        // actually asserted was that two decoders of one grid agree; the decoder
        // is now one, and that assertion has nothing left to make. Held against
        // a copy, a stray character in either grid names the cell it moved.
        BoardMap bench = BoardMap.Bench;
        string[] wasRelief =
        {
            "..............",
            "..............",
            "...1.1........",
            "...1.1.....1..",
            "......vvv.....",
            "......vvv.....",
        };
        string[] wasRamps =
        {
            "..............",
            "..............",
            "...........r..",
            "..........r.r.",
            "...r.r.r..rrr.",
            "..............",
        };
        string[] wasWater =
        {
            "..............",
            "..............",
            "..............",
            "..............",
            "......www.....",
            "......www.....",
        };
        Vector2I[] wasHomes = { new(11, 3), new(4, 2), new(6, 2) };
        string wrongCell = "";
        for (int r = 0; r < bench.Rows && wrongCell.Length == 0; r++)
        for (int q = 0; q < bench.Columns && wrongCell.Length == 0; q++)
        {
            int at = r * bench.Columns + q;
            int level = wasRelief[r][q] switch { '1' => 1, '2' => 2, 'v' => -1, _ => 0 };
            if (bench.Levels[at] != level)
                wrongCell = $"({q},{r}) stands at {bench.Levels[at]}, was {level}";
            else if (bench.Ramps[at] != (wasRamps[r][q] == 'r'))
                wrongCell = $"({q},{r}) is {(bench.Ramps[at] ? "" : "not ")}a ramp now";
            else if (bench.Water[at] != (wasWater[r][q] == 'w'))
                wrongCell = $"({q},{r}) is {(bench.Water[at] ? "" : "not ")}wet now";
        }
        Check("the bench board is the board it always was",
            wrongCell.Length == 0
            && bench.Columns == 14 && bench.Rows == 6
            && bench.Plot is null
            && bench.Kinds.All(k => k is null)
            && bench.Homes.SequenceEqual(wasHomes),
            wrongCell.Length > 0 ? wrongCell
                : $"{bench.Columns}x{bench.Rows}, homes "
                  + string.Join(" ", bench.Homes));

        // The island on the test board, and it is the one thing about that board
        // a single character undoes without a word. Dry one cell of its ring and
        // it is a peninsula: nothing throws, the flood guard is happy, everything
        // is still reachable, and the only thing that changed is that the picture
        // is no longer of an island. TankBench's "to the island" asks the map, so
        // a ring with a hole in it drives the tank somewhere else instead.
        //
        // Both halves, because BoardMap.Ringed is deliberately the weaker
        // predicate - it says no shore touches the cell, and counts an off-board
        // neighbour as water so that open water at the board's edge still
        // answers. Surrounded is the stronger thing, and it is what makes an
        // island one.
        BoardMap test = BoardMap.Test;
        Vector2I? isle = test.Ringed(false);
        var bare = new List<string>();
        if (isle is Vector2I stood)
            foreach (int h in HexField.EdgeHeadings)
            {
                Vector2I next = HexField.Step(stood, h);
                if (!test.OnBoard(next) || !test.Water[test.At(next)])
                    bare.Add($"({next.X},{next.Y})");
            }
        Check("the test board holds an island, with water on all six sides of it",
            isle is not null && bare.Count == 0,
            isle is null
                ? "no dry cell on it has water all round"
                : $"({isle.Value.X},{isle.Value.Y}) is not surrounded: "
                  + string.Join(" ", bare));

        // <b>What is deliberately not checked beside it: that the way on to the
        // island is a flooded beach a level up.</b> It reads like the
        // load-bearing half, and every part of it is already held elsewhere -
        // that there is a ramp in the ring at all by the reachability check
        // above, that the ramp stands under water by the ring being water, and
        // that the island stands over the pond by Flood, which puts a dry cell
        // at a surface's own height beneath it. Written out, no mutation makes it
        // fail alone: dry the landing and the ring check goes first, sink the
        // island and Flood does, raise it two levels and DeriveRamps finds the
        // beach nothing to bridge. A check that cannot fail by itself is a second
        // copy of three answers.
    }

    /// <summary>The effects panel's rows that are not dials: buttons, toggles and
    /// the readout, which the file carries names for just the same.</summary>
    private static readonly string[] PanelOwn =
    {
        "he.fire_now", "he.refire", "he.hold", "he.scrub", "he.life",
        "he.ring_reach", "he.state", "he.crater.fill",
        "sheet.fire_now", "sheet.pair", "sheet.refire", "sheet.hold",
        "sheet.scrub", "sheet.life", "sheet.state",
        "he.might", "sheet.might",
    };

    /// <summary>A sheet burst fired and run to this age, for a check that wants to
    /// ask it what it is doing - <see cref="Fired"/> for the computed one, and the
    /// same reason: the clock is private and a burst is only ever asked about a
    /// moment of itself.</summary>
    private static SheetBlast Blown(SheetBlast blast, double age)
    {
        blast.Fire();
        blast.Tick(age);
        return blast;
    }

    /// <summary>Whether a dig with these numbers leaves nothing behind, on a
    /// board of its own so the caller's marks are not disturbed.</summary>
    private static bool NoDig(Vector2 spot, float lift, float much, float wide)
    {
        var board = new Craters();
        board.Dig(spot, lift, much, wide);
        return !board.Any;
    }

    /// <summary>A burst set off and run on for so long, so the clock can be
    /// asserted without a frame going past. Ticked in one step rather than sixty:
    /// the clock is a sum, so the two are the same number.</summary>
    private static ProcBlast Fired(ProcBlast burst, double after)
    {
        burst.Fire();
        burst.Tick(after);
        return burst;
    }
}
