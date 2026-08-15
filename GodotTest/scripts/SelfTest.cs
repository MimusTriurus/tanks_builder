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
                          Grove? grove = null)
    {
        int failed = 0;

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
            if (ok)
                GD.Print($"  ok    {name}");
            else
            {
                GD.Print($"  FAIL  {name}  {detail}");
                failed++;
            }
        }

        GD.Print("grid: click lands on the cell that was drawn");
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

        GD.Print("grid: steps and headings agree");
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

        GD.Print("paths");
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

        Relief(field, grove, Check);
        Ramps(field, Check);
        Water(field, grove, Check);
        Climbing(field, tank, Check);

        GD.Print("turret modes");
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

        GD.Print("body pitch");
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

        GD.Print("ground rumble");
        var rumble = new BodyRumble();
        var seen = new HashSet<int>();
        int changes = 0, previous = 0;
        for (int i = 0; i < 300; i++)
        {
            rumble.Advance(240.0 * dt, 240.0);
            seen.Add(rumble.Offset);
            if (rumble.Offset != previous) changes++;
            previous = rumble.Offset;
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

        // Phase must come from distance. The same ground covered in a seventh
        // of the steps has to give the same offset, or the rumble is really a
        // clock and every speed rattles alike.
        // The distances deliberately avoid whole multiples of BumpSpacing: on a
        // boundary the two accumulations straddle it by one float ulp and land
        // on different bumps, which says nothing about whether distance drives
        // the phase. An earlier version used exactly 240px - eight bumps on the
        // nose - and passed only while those two bumps happened to agree.
        var driftOk = true;
        var mismatch = "";
        foreach (double total in new[] { 47.0, 133.0, 237.0, 512.5 })
        {
            var coarse = new BodyRumble();
            var fine = new BodyRumble();
            for (int i = 0; i < 20; i++) coarse.Advance(total / 20.0, 240.0);
            for (int i = 0; i < 137; i++) fine.Advance(total / 137.0, 240.0);
            if (coarse.Offset != fine.Offset)
            {
                driftOk = false;
                mismatch = $"at {total}px: {coarse.Offset} vs {fine.Offset}";
            }
        }
        Check("phase follows distance, not the frame clock", driftOk, mismatch);

        var stopped = new BodyRumble();
        stopped.Advance(0.0, 0.0);
        Check("it is still when the tank is",
            stopped.Offset == 0 && Math.Abs(stopped.Roll) < 1e-9,
            $"offset {stopped.Offset}, roll {stopped.Roll:F5}");

        // Roll must not just track the heave, or it adds nothing the heave has
        // not already said.
        var pair = new BodyRumble();
        int agree = 0, samples = 0;
        for (int i = 0; i < 400; i++)
        {
            pair.Advance(240.0 * dt, 240.0);
            if (pair.Offset == 0) continue;
            samples++;
            if (Math.Sign(pair.Roll) == Math.Sign(pair.Offset)) agree++;
        }
        Check("roll is drawn independently of the heave",
            samples > 50 && agree > samples * 0.25 && agree < samples * 0.75,
            $"{agree} of {samples} bumps agreed in sign");

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

        var crawl = new BodyRumble();
        int crawlNonZero = 0, fastNonZero = 0;
        var fast = new BodyRumble();
        for (int i = 0; i < 600; i++)
        {
            crawl.Advance(20.0 * dt, 20.0);
            if (crawl.Offset != 0) crawlNonZero++;
            fast.Advance(240.0 * dt, 240.0);
            if (fast.Offset != 0) fastNonZero++;
        }
        Check("it thins out at a crawl instead of switching off",
            crawlNonZero < fastNonZero / 3, $"{crawlNonZero} vs {fastNonZero} frames jolted");

        GD.Print("engine tremble");
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

        GD.Print("turret scan");
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

        GD.Print("the shot");
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

        GD.Print("the rendered flash");
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

        GD.Print("trimmed frames");
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

        GD.Print("the track belts");
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
        GD.Print("camera shake");
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
                "D:/Projects/AgentCoding/BlenderMCP/Sprites", borrower, lender);
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
        GD.Print("track marks: the ruts the belts leave");
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
                GD.Print("  ..    no vehicles to lay any");
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
            GD.Print("terrain: no art loaded, running on the rendered tile");
        else
        {
            GD.Print("terrain: the kinds of ground on the board");
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
            GD.Print("the grove: nothing sown, running on open ground");
        else
        {
            GD.Print("the grove: what stands on the cells");

            // First, and before anything here re-sows: the board as it stands
            // has to agree with the ground it says it is painted with. Trees
            // are nodes rather than pixels of the plate, so nothing about
            // redrawing the field touches them - a caller that changes the
            // paint and forgets to sow leaves woods on soil and soil where the
            // woods were, and every other check here would pass, because they
            // all plant first.
            var sownOn = new HashSet<Vector2I>(grove.Trees.Select(t => t.Cell));
            var wooded = new HashSet<Vector2I>();
            for (int q = 0; q < field.Columns; q++)
            for (int r = 0; r < field.Rows; r++)
                if (grove.IsForest(new Vector2I(q, r)))
                    wooded.Add(new Vector2I(q, r));
            Check("the trees standing there are the ones the ground asks for",
                sownOn.SetEquals(wooded),
                $"{sownOn.Count} cells carry trees, {wooded.Count} are wooded");

            Check("every tree stands on a wooded cell",
                grove.Trees.All(t => grove.IsForest(t.Cell)),
                $"{grove.Trees.Count(t => !grove.IsForest(t.Cell))} of "
                + $"{grove.Planted} grew on open ground");

            // The foot decides the cell, so a tree near a border belongs to the
            // hexagon it stands in - and to that one only. Planted per cell off
            // a global lattice, so this is what says the two agree.
            Check("a tree is planted by the cell it stands in",
                grove.Trees.All(t => field.CellAt(t.Ground) == t.Cell),
                $"{grove.Trees.Count(t => field.CellAt(t.Ground) != t.Cell)} "
                + "stand outside the cell that planted them");

            // The rule the whole arrangement rests on, and with no thicket to be
            // exempt there is now nothing it does not cover: every wooded cell
            // has a clearing in it, so every cell on the board is drivable.
            float squash = grove.Squash;
            var intruders = grove.Trees.Where(t =>
            {
                Vector2 centre = field.CellCentre(t.Cell);
                var a = new Vector2(t.Ground.X, t.Ground.Y / squash);
                var b = new Vector2(centre.X, centre.Y / squash);
                return a.DistanceTo(b) < grove.KeepOut - 0.5;
            }).ToList();
            Check("nothing grows where a tank stands",
                intruders.Count == 0,
                $"{intruders.Count} inside the {grove.KeepOut:F0}px keep-out");

            // The foot being inside was never enough - the base is wider than
            // the point it is measured at - and asking which cell the base's
            // horizontal extremes fall in was not enough either: a trunk one
            // pixel above the bottom edge passes that and is drawn standing on
            // the seam. Measured against the boundary itself, in every
            // direction, with the drawn seam's own width to spare.
            double root3 = Math.Sqrt(3.0);
            double radius = field.Atlas!.HexRect.Size.X * 0.5;
            float sq = grove.Squash;
            var straddling = grove.Trees.Where(t =>
            {
                Vector2 c = field.CellCentre(t.Cell);
                double dx = Math.Abs(t.Ground.X - c.X);
                double dy = Math.Abs((t.Ground.Y - c.Y) / sq);
                double edge = Math.Min(root3 * radius * 0.5 - dy,
                                       (root3 * radius - root3 * dx - dy) * 0.5);
                return edge < grove.Props!.RootOf(t.Species) * t.Pixels
                              + grove.Clearance - 0.5;
            }).ToList();
            Check("no tree stands on the border of its cell",
                straddling.Count == 0,
                $"{straddling.Count} are within their own base of the edge");

            // A wooded cell is wooded. The minimum overrides the edge fade,
            // which is a preference among legal spots, and cannot override the
            // keep-out or the border, which are what make the cell drivable and
            // the tree one cell's tree.
            var perCell = new Dictionary<Vector2I, int>();
            foreach (PropNode tree in grove.Trees)
                perCell[tree.Cell] = perCell.GetValueOrDefault(tree.Cell) + 1;
            var thin = new List<Vector2I>();
            for (int q = 0; q < field.Columns; q++)
            for (int r = 0; r < field.Rows; r++)
            {
                var cell = new Vector2I(q, r);
                if (grove.IsForest(cell)
                    && perCell.GetValueOrDefault(cell) < grove.Minimum)
                    thin.Add(cell);
            }
            Check($"every wooded cell carries at least {grove.Minimum} trees",
                thin.Count == 0,
                $"{thin.Count} cells came out thinner than that");

            Check($"and at most {grove.Maximum}",
                grove.Maximum <= 0
                || perCell.Values.All(n => n <= grove.Maximum),
                $"the fullest carries "
                + $"{(perCell.Count == 0 ? 0 : perCell.Values.Max())}");

            // Both ends move, and both still hold when they are set to the same
            // number - which is the case that catches a floor filled after the
            // ceiling was applied, or a ceiling that trims a cell back under
            // its floor.
            int wasLeast = grove.Minimum, wasMost = grove.Maximum;
            grove.Minimum = 6;
            grove.Maximum = 6;
            grove.Plant();
            var counted = new Dictionary<Vector2I, int>();
            foreach (PropNode tree in grove.Trees)
                counted[tree.Cell] = counted.GetValueOrDefault(tree.Cell) + 1;
            Check("a floor equal to the ceiling puts exactly that many on a cell",
                counted.Values.All(n => n == 6),
                $"{counted.Values.Count(n => n != 6)} cells missed six");
            grove.Minimum = wasLeast;
            grove.Maximum = wasMost;
            grove.Plant();

            // A prop that never gets planted is one nobody notices is missing -
            // and with the spot choosing among the trees that fit it, the way a
            // prop goes missing is by being too wide for every legal spot on
            // the board rather than by being left out of a list. Measured on
            // the four in hand: the bases want 11.3 to 18.3px of room against a
            // ring 24 deep at its narrowest, so the largest is the one at risk.
            var grown = new HashSet<int>(grove.Trees.Select(t => t.Species));
            Check("every kind of tree is somewhere on the board",
                grove.Planted < grove.Props!.Count * 4
                || grown.Count == grove.Props.Count,
                $"{grown.Count} of {grove.Props.Count} appeared in "
                + $"{grove.Planted} trees; missing "
                + string.Join(", ", Enumerable.Range(0, grove.Props.Count)
                    .Where(k => !grown.Contains(k))
                    .Select(k => $"{grove.Props.NameOf(k)} "
                                 + $"(base {grove.Props.RootOf(k) * grove.DrawScale:F1}px)")));

            // Ground space, not screen: the lattice is square on the ground, and
            // measuring the spacing on screen would call every north-south pair
            // too close by the squash.
            double closest = double.MaxValue;
            for (int i = 0; i < grove.Trees.Count; i++)
            for (int j = i + 1; j < grove.Trees.Count; j++)
            {
                var a = new Vector2(grove.Trees[i].Ground.X,
                                    grove.Trees[i].Ground.Y / squash);
                var b = new Vector2(grove.Trees[j].Ground.X,
                                    grove.Trees[j].Ground.Y / squash);
                closest = Math.Min(closest, a.DistanceTo(b));
            }
            // The lattice guarantees it whatever the cells decide, which is the
            // point of sowing globally: two neighbouring hexagons cannot plant
            // two trees a pixel apart across their shared edge. Half the step,
            // because a cell short of its floor is gone over again at half - so
            // the guarantee is the finer lattice's, and saying otherwise here
            // would be asserting a spacing the sowing does not promise.
            double floorGap = grove.Spacing * 0.5 * (1.0 - 2.0 * grove.Jitter);
            Check("no two trees are closer than the lattice allows",
                closest >= floorGap - 0.5,
                $"closest {closest:F1}px against {floorGap:F1}px");

            // Same rule as a vehicle's, in the same space - that is what lets a
            // tree and a tank sort against each other at all.
            Check("a tree's depth is its foot",
                grove.Trees.All(t => t.ZIndex == Mathf.RoundToInt(t.Ground.Y)),
                "depth off anything but the foot puts a leaning canopy in front "
                + "of what it grows behind");
            Check("a tree in front of another draws over it",
                grove.Trees.Zip(grove.Trees.Skip(1))
                     .All(p => p.First.ZIndex <= p.Second.ZIndex),
                "the planted list is kept in depth order so a tie resolves back "
                + "to front rather than by which cell was walked first");

            // Not decoration and not an effect: a tree is an object on the
            // ground, so it belongs over the ruts and the ring rather than
            // among them.
            Check("trees stand over the ground marks", grove.Trees.All(
                    t => t.ZIndex > TrackMarks.MarksZ
                         && t.ZIndex > SelectionRing.GroundZ),
                "a trunk under the paint is paint");

            // --- and the same wood, drawn by the stage --------------------
            //
            // The billboards are these trees put where the depth buffer can judge
            // them, so what has to hold is that nothing moved. Asserted against
            // the projection rather than against the expression that implements
            // it - Relief3D's rule, and the reason Trunk takes its two camera
            // terms rather than reading a board.
            const float squashed = 0.5077f, risen = 0.8616f;
            float Row(Vector3 at) => at.Z * squashed - at.Y * risen;
            PropNode planted = grove.Trees[0];
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
            var sown = grove.Trees.Select(t => (t.Ground, t.Species)).ToList();
            grove.Plant();
            Check("sowing twice gives the same forest",
                grove.Trees.Select(t => (t.Ground, t.Species)).SequenceEqual(sown),
                $"{sown.Count} then {grove.Planted}");

            // The rule that cannot be had, asserted as a rule anyway: with it on
            // nothing crosses a tank on its own cell, and what that costs is the
            // whole front of every clearing.
            bool wasClear = grove.ClearFront;
            grove.ClearFront = true;
            grove.Plant();
            Check("with the clearance rule on, no tree crosses its own cell's tank",
                grove.Trees.All(t =>
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
            Vector2I stood = grove.Trees[0].Cell;
            var busy = new HashSet<Vector2I> { stood };
            for (int i = 0; i < 240; i++)
                grove.Reveal(busy, 1.0 / 60.0);
            Check("trees fade for a tank standing in them",
                grove.Trees.Where(t => t.Cell == stood)
                     .All(t => Math.Abs(t.Modulate.A - grove.Ghost) < 0.01f)
                && grove.Trees.Where(t => t.Cell != stood)
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
                grove.Trees.Where(t => t.Cell == stood)
                     .All(t => Math.Abs(t.Modulate.A - 0.75f) < 0.01f),
                "a fade that ignored the dial would still look like a fade");
            grove.Ghost = wasGhost;
            for (int i = 0; i < 240; i++)
                grove.Reveal(new HashSet<Vector2I>(), 1.0 / 60.0);
            Check("and come back when it drives off",
                grove.Trees.All(t => t.Modulate.A > 0.99f),
                "the fade is a view of the ground, not a change to it");

            // --- the wind ------------------------------------------------
            //
            // Stepped rather than clocked off the wall, so this runs the same
            // in a check as it does under --capture.
            double wasWind = grove.Wind;
            grove.Wind = 3.0;
            for (int i = 0; i < 40; i++)
                grove.Blow(1.0 / 60.0);
            Check("the wind leans the trees",
                grove.Trees.Any(t => Math.Abs(t.Drift) > 0.2f),
                $"widest lean {grove.Trees.Max(t => Math.Abs(t.Drift)):F2}px");

            // The whole point of hashing a phase per tree. A wood that leans as
            // one body is a wood on a hinge.
            Check("and not all of them the same way",
                grove.Trees.Any(t => t.Lean > 0.0f)
                && grove.Trees.Any(t => t.Lean < 0.0f),
                "every tree leaning the same way at once is one hinge, not wind");

            // Drift is the shear times the height, which is what makes a taller
            // tree lean further - the half of the setting that is derived
            // rather than chosen, and the half that would go quietly if someone
            // made the drift a fixed number of pixels.
            Check("a taller tree leans further, because the lean is a shear",
                grove.Trees.All(t => Math.Abs(t.Drift - t.Lean * t.Rise) < 1e-4),
                "drift is the shear times the height, or it is not a lean");

            // The trunk is planted, and this is the matrix that has to say so:
            // it pivots on the foot, so whatever the crown does the ground
            // contact does not move. Asked of the transform the draw call
            // actually uses - the shear on the wrong axis, or an origin left in
            // it, both slide the tree and both look like wind.
            PropNode bent = grove.Trees.OrderByDescending(t => Math.Abs(t.Lean))
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
            var leaning = grove.Trees.Select(t => t.Lean).ToList();
            grove.Weather = 0.0;
            for (int i = 0; i < 20; i++)
                grove.Blow(2.0 / 60.0);
            Check("the same seconds of wind bend it the same way",
                grove.Trees.Select(t => t.Lean).Zip(leaning)
                     .All(p => Math.Abs(p.First - p.Second) < 1e-5f),
                "a phase stepped per frame rather than per second sways at "
                + "whatever rate the machine runs at");
            grove.Weather = wasWeather;

            grove.Wind = 0.0;
            for (int i = 0; i < 40; i++)
                grove.Blow(1.0 / 60.0);
            Check("no wind, no lean at all",
                grove.Trees.All(t => t.Lean == 0.0f),
                $"{grove.Trees.Count(t => t.Lean != 0.0f)} still leaning");

            // --- the blast wave ------------------------------------------
            //
            // Wind held at nothing throughout, so Lean is the flinch and
            // nothing else - except in the last check here, which is about
            // exactly the case where it is both.
            grove.Wind = 0.0;
            grove.Calm();

            float flat = grove.Squash;
            Vector2 Ground(Vector2 screen) => new(screen.X, screen.Y / flat);
            Vector2 seat = Ground(field.CellCentre(grove.Trees[0].Cell));

            // Ten crown pixels at the source, which is twice the shell's own
            // and well clear of the threshold the flinch settles at.
            (int[] Reached, int[] Sign, float[] Peak) Blast(double pixels, int shots)
            {
                grove.Calm();
                for (int s = 0; s < shots; s++)
                    grove.Shock(field.CellCentre(grove.Trees[0].Cell), pixels);
                var reached = new int[grove.Trees.Count];
                var sign = new int[grove.Trees.Count];
                var peak = new float[grove.Trees.Count];
                for (int i = 0; i < reached.Length; i++)
                    reached[i] = -1;
                for (int f = 0; f < 300; f++)
                {
                    grove.Blow(1.0 / 60.0);
                    for (int i = 0; i < grove.Trees.Count; i++)
                    {
                        float now = grove.Trees[i].Flinch;
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
            for (int i = 0; i < grove.Trees.Count; i++)
            {
                double d = Ground(grove.Trees[i].Ground).DistanceTo(seat);
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
                Enumerable.Range(0, grove.Trees.Count).All(
                    i => Ground(grove.Trees[i].Ground).DistanceTo(seat)
                         < grove.BlastReach || wave.Peak[i] == 0.0f),
                $"reach {grove.BlastReach:F0}px of ground");

            // Away from it, and only the part of "away" that runs across the
            // screen: a tree straight up-board is pushed at the camera, and a
            // shear cannot draw that lean at all. The trees with little
            // sideways in their bearing are left out of the test for the same
            // reason they barely move.
            Check("and every tree is shoved away from it, not toward it",
                Enumerable.Range(0, grove.Trees.Count).All(i =>
                {
                    Vector2 g = Ground(grove.Trees[i].Ground);
                    double d = g.DistanceTo(seat);
                    if (d >= grove.BlastReach || Math.Abs(g.X - seat.X) < 0.4 * d)
                        return true;
                    return wave.Sign[i] == Math.Sign(g.X - seat.X);
                }),
                "a tree leaning into the blast is the sign of the shove lost");

            // Asked for crown pixels, delivers crown pixels - which is what the
            // unit-impulse integrator is for, and the thing a closed-form
            // normalisation would get wrong by enough to matter.
            int loudest = Enumerable.Range(0, grove.Trees.Count)
                                    .OrderByDescending(i => wave.Peak[i]).First();
            {
                Vector2 g = Ground(grove.Trees[loudest].Ground);
                double d = g.DistanceTo(seat);
                double want = 10.0 * (1.0 - d / grove.BlastReach)
                              * Math.Abs(g.X - seat.X) / d;
                double got = wave.Peak[loudest] * grove.SwayHeight;
                Check("the shove arrives in the crown pixels it was asked for",
                    Math.Abs(got - want) < 0.08 * want,
                    $"{got:F2}px against {want:F2} at {d:F0}px of ground");
            }

            Check("and rings down to nothing",
                grove.Trees.All(t => t.Flinch == 0.0f && t.FlinchRate == 0.0f),
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
            grove.Shock(field.CellCentre(grove.Trees[0].Cell), 10.0);
            for (int f = 0; f < 30; f++)
                grove.Blow(1.0 / 60.0);
            var mixed = grove.Trees.Select(t => (t.Lean, t.Flinch)).ToList();
            grove.Calm();
            grove.Weather = markWeather;
            for (int f = 0; f < 30; f++)
                grove.Blow(1.0 / 60.0);
            Check("the wind goes on blowing through a blast",
                grove.Trees.Select(t => t.Lean).Zip(mixed)
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
            PropNode brushed = grove.Trees[0];
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
                grove.Trees.All(t =>
                {
                    double d = Ground(t.Ground).DistanceTo(Ground(hull));
                    if (d >= grove.BrushReach || t.Flinch == 0.0f)
                        return true;
                    return Math.Sign(t.Flinch)
                           == Math.Sign(t.Ground.X - hull.X);
                }),
                "a tree leaning into the hull is the sign of the shove lost");

            Check("nothing past its reach gives at all",
                grove.Trees.All(
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
            grove.Shock(field.CellCentre(grove.Trees[0].Cell), 10.0);
            grove.Blow(1.0 / 60.0);
            grove.Calm();
            Check("and calming it stops every front and every flinch",
                grove.Waves == 0
                && grove.Trees.All(t => t.Flinch == 0.0f && t.FlinchRate == 0.0f),
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
        GD.Print("the contact shadow");
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
            // Its catcher is a disc of the tile's own inradius, so it cannot
            // want room the tank does not already have. A wider frame here
            // would mean it had quietly become a cast shadow.
            Check("the shadow needs no wider frame than the tank",
                shadowed.TileOf(AtlasSet.ShadowName) == shadowed.Tile,
                $"{shadowed.TileOf(AtlasSet.ShadowName).X}px against tank "
                + $"{shadowed.Tile.X}px");
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

        GD.Print("the gun tube's recoil");
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

        GD.Print("the engine exhaust");
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

        GD.Print("the wreck");
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
                Main.MiddleTap(new Vector2(400, 300), new Vector2(400, 300))
                && Main.MiddleTap(new Vector2(400, 300), new Vector2(402, 301))
                && !Main.MiddleTap(new Vector2(400, 300), new Vector2(420, 300))
                && !Main.MiddleTap(new Vector2(400, 300), new Vector2(400, 340)),
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

        GD.Print("the burning wreck");
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

        GD.Print("a shell arriving");
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
            Main.CalibreFor(0.4f) == 0 && Main.CalibreFor(0.95f) == 1
            && Main.CalibreFor(1.4f) == 2 && Main.CalibreFor(9.0f) == 2,
            $"0.4 -> {Main.CalibreFor(0.4f)}, 0.95 -> {Main.CalibreFor(0.95f)},"
            + $" 1.4 -> {Main.CalibreFor(1.4f)}, 9.0 -> {Main.CalibreFor(9.0f)}");

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
                int side = Main.SideFor(deg);
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

        GD.Print("the mark it leaves");
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
                Main.CalibreCount == atlas.ScarLevels
                && Main.BiteFor(0) == 1
                && Main.BiteFor(Main.CalibreCount - 1) == atlas.ScarLevels,
                $"{Main.CalibreCount} calibres against {atlas.ScarLevels} levels,"
                + $" biting {Main.BiteFor(0)}..{Main.BiteFor(Main.CalibreCount - 1)}");
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
                pairs.Add((Main.ScatterAt(n), Main.RiseAt(n)));
                heights.Add(Main.RiseAt(n));
            }
            Check("consecutive rounds differ in height as well as in offset",
                heights.Count > 90 && pairs.Count == 100
                && Math.Abs(Main.RiseAt(1)) > 0.0f,
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

        GD.Print("the settings file");
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
        }

        GD.Print("the side panel");
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

        GD.Print("recoil");
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

        GD.Print("turret stabiliser");
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
        Check("heave is shared whatever the stabiliser is doing",
            tank.HeaveFor(true) == tank.HeaveFor(false) && tank.HeaveFor(true) == tank.Shake,
            $"{tank.HeaveFor(true)} vs {tank.HeaveFor(false)}");

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
            GD.Print("three tanks on one field");

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
            // The line is the tanks whose home is on Main.HomeRow rather than all of
            // them, because one of them is parked on the rosette's hill on purpose -
            // see Main.HomeCells. A pair is enough to compare sizes against; asking
            // all three would fail on a map that says so out loud. Where the third
            // went is asserted below, because "somebody moved a tank" and "the map
            // changed" look identical from here.
            var line = vehicles.Where(v => v.HomeCell.Y == Main.HomeRow).ToList();
            Check("the ones on the home row stand in one row, so only the size "
                + "differs",
                line.Count >= 2
                && line.Select(v => field.CellAnchor(v.HomeCell).Y)
                       .Distinct().Count() == 1,
                $"{line.Count} on the row: " + string.Join(", ", vehicles.Select(
                    v => $"{v.Tag} y={field.CellAnchor(v.HomeCell).Y:F0}")));
            Check("and the one that is not is parked on the rosette",
                vehicles.Count(v => v.HomeCell.Y != Main.HomeRow) == 0
                || vehicles.Where(v => v.HomeCell.Y != Main.HomeRow)
                           .All(v => v.HomeCell == new Vector2I(11, 3)),
                string.Join(", ", vehicles.Where(v => v.HomeCell.Y != Main.HomeRow)
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
                Main.SelectionFor(vehicles, active, vehicles[other].Cell) == other,
                $"click on {vehicles[other].Tag}'s cell picked "
                + $"{Main.SelectionFor(vehicles, active, vehicles[other].Cell)}");
            // Otherwise the tank you are driving could not be told to stay where it
            // is, and clicking it would be a selection that does nothing.
            Check("clicking the one you are driving is not a selection",
                Main.SelectionFor(vehicles, active, vehicles[active].Cell) < 0,
                "it has to fall through to a move order");
            Vector2I empty = new(field.Columns - 1, field.Rows - 1);
            Check("clicking empty ground is a move order",
                Vehicle.At(vehicles, empty) is null
                && Main.SelectionFor(vehicles, active, empty) < 0,
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
                GD.Print("the ring on the selected tank");
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

        GD.Print("movement profiles");
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

        GD.Print("gunnery: the gun fires down a flat side and nowhere else");

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
                HexField.EdgeHeadings[Main.SideFor((h + 180) % 360)] == (h + 180) % 360),
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
                        float rise = Main.RiseAt(n);
                        float bare = Gunnery.ScatterOntoBore(
                            eye, aim, centroid, tangent, slope, rise,
                            Main.ScatterLimit, Main.ScatterAt(n));
                        // What the trigger actually places, dispersion and all,
                        // rather than the three steps reassembled here.
                        float aimedAt = Main.AimedScatter(
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
                            centroid + tangent * Main.ScatterAt(n) + slope * rise);
                        tried++;
                        solvedTotal += off;
                        hashedTotal += hashed;
                        if (aimOff > hashed + 0.01f)
                            worse++;
                        if (Math.Abs(bare) >= Main.ScatterLimit - 1e-4f)
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
                From = foe.Sprite.ToGlobal(aim)
                       - new Vector2(Shell.PointBlank, 0.0f),
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
            Check("the round outlives its own arrival so its smoke can hang",
                round.Arrived && !round.Expired,
                $"expired {round.Expired} the frame it landed - and note Arrived "
                + "stays true from here on, which is why the strike has to be "
                + "guarded rather than driven off it");
            for (int i = 0; i < 200 && !round.Expired; i++)
                round.Advance(1.0 / 60.0);
            Check("and it does go away once the smoke has",
                round.Expired,
                $"still drawing after {Shell.SmokeSeconds}s of trail life");
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
                    From = foe.Sprite.ToGlobal(aim) - new Vector2(400.0f, 0.0f),
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
                Check("and the trail is continuous at the speed it is flown",
                    Shell.Speed / 60.0f > Shell.Streak
                    && Shell.Speed / 60.0f < 40.0f,
                    $"{Shell.Speed / 60.0f:F0}px a frame against a"
                    + $" {Shell.Streak:F0}px streak - the streak stopped covering"
                    + " the gap when the round was sped up, and the trail is what"
                    + " took that job over");
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
            Check("the tracer and its smoke draw unless asked not to",
                Main.TracerOnByDefault && Shell.SmokeOnByDefault
                && Main.TurretSoundOnByDefault,
                "all three are named constants precisely so a change to them is "
                + "a change somebody made on purpose - the tracer was off while "
                + "it was one stroke of debug line, and is on now that it is a "
                + "layer with a calibre and a trail to be judged");
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
            var unseen = new Shell
            {
                Shooter = shooter, Target = foe, ImpactLocal = aim,
                From = foe.Sprite.ToGlobal(aim)
                       - new Vector2(Shell.PointBlank, 0.0f),
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
            field.SetRelief(Main.ReliefMap(field.Columns, field.Rows),
                            Main.RampMask(field.Columns, field.Rows));

        GD.Print(failed == 0 ? "SELFTEST OK" : $"SELFTEST FAILED: {failed}");
        return failed;
    }

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
            From = foe.Sprite.ToGlobal(aim) - new Vector2(Shell.PointBlank, 0.0f),
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
        GD.Print("climb: the body is turned by the slope and its shadow is "
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
    private static void Woods(HexField field, Grove? grove, double elevation,
                              Action<string, bool, string> Check)
    {
        if (grove is null)
        {
            Check("trees sort by where they stand, not by where they are drawn",
                true, "");
            GD.Print("        (no grove in this run)");
            return;
        }

        // Forced, because the default paint has no woods on it at all and a
        // check over an empty list passes by having nothing to say.
        string wasPaint = field.Paint;
        try
        {
            field.Paint = TerrainSet.Forest;
            grove.Plant();
            var trees = grove.Trees;
            int levels = 0;
            foreach (PropNode tree in trees)
                if (field.LevelAt(tree.Cell) != 0)
                    levels++;
            if (trees.Count == 0)
            {
                Check("trees sort by where they stand, not by where they are drawn",
                    true, "");
                GD.Print("        (no tree art on disk, so nothing was sown)");
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
    private static void Ramps(HexField field, Action<string, bool, string> Check)
    {
        GD.Print("ramps: the level changes where the map says it may");
        try
        {
            field.SetRelief(Main.ReliefMap(field.Columns, field.Rows),
                            Main.RampMask(field.Columns, field.Rows));
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

            // The fallback, which is what keeps every relief assertion written
            // before ramps existed describing the board it was written for.
            field.SetRelief(Main.ReliefMap(field.Columns, field.Rows));
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
                field.SetRelief(Main.ReliefMap(field.Columns, field.Rows), bad);
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
    private static void Water(HexField field, Grove? grove,
                              Action<string, bool, string> Check)
    {
        GD.Print("water: a cell to drive into, not round");
        // What the session came up with, because the grove was sown against it and
        // the topic that judges the wood asks the board as it stands. Run() clears
        // the relief for the same reason and puts it back at the end; this is that
        // line for the water, and without it the pond's own cells come back
        // forest-eligible with no trees on them - which reports as the grove
        // disagreeing with the ground, a mile from anything to do with water.
        bool hadWater = field.HasWater;
        try
        {
            field.SetRelief(Main.ReliefMap(field.Columns, field.Rows),
                            Main.RampMask(field.Columns, field.Rows));
            field.SetWater(Main.WaterMask(field.Columns, field.Rows));
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

            // Water is not a wall, which was the whole request. The pond is
            // entered from the beach at the pit's mouth, so this is asked of the
            // board rather than of the rule: a Passable that learned about water
            // would show up here as a step that used to be legal and is not.
            int blocked = 0;
            foreach (Vector2I cell in pond)
            foreach (int heading in HexField.EdgeHeadings)
            {
                Vector2I next = HexField.Step(cell, heading);
                if (field.InBounds(next) && field.IsWater(next)
                    && !field.Passable(cell, heading))
                    blocked++;
            }
            Check("water is not a wall - a pond can be driven across",
                blocked == 0,
                $"{blocked} steps between flooded cells are refused");
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
            // marks because they are on the bottom, under the tanks because a
            // surface has one sort key and a tank standing in front of the pond
            // would lose to it.
            Check("the water sorts over the ground marks and under every tank",
                Stage3D.RutOrder < Stage3D.RingOrder
                && Stage3D.RingOrder < Stage3D.WaterOrder
                && Stage3D.WaterOrder < 0,
                $"ruts {Stage3D.RutOrder}, ring {Stage3D.RingOrder}, water "
                + $"{Stage3D.WaterOrder}");

            // The cut is the shader's, and it has to be: a horizontal surface
            // crossing an upright billboard has half of itself in front of the
            // card, and one sort key cannot say so. Asserted on the shader source
            // rather than on a rendered pixel, the way the premultiplied blend
            // already is.
            Check("and the submerged half is tinted by the sprite's own shader",
                Stage3D.PaintShader.Contains("uniform sampler2D shore")
                && Stage3D.PaintShader.Contains("bottom - shore_cut.x"),
                "the paint shader has no waterline in it, so the pond can only "
                + "tint the whole tank or none of it");

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

            // Both guards, and both have to be shown to bite: a guard that cannot
            // refuse is a comment.
            bool onRamp = false, twoLevels = false;
            var mask = Main.WaterMask(field.Columns, field.Rows);
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
            try { field.SetWater(bad); }
            catch (InvalidOperationException) { onRamp = true; }
            Check("water on a ramp is refused rather than wedged along it", onRamp,
                $"a pond was allowed onto the ramp at ({ramp.X},{ramp.Y}), which "
                + "has no one surface height");

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
                ? WaterArt.Load(Main.WaterRoot, default)
                : WaterArt.Load(Main.WaterRoot, art.HexRect);
            if (!surf.Any)
            {
                GD.Print("  skip  water art: " + surf.Note);
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

                // The one thing a single mesh cannot say for itself. Every clock
                // in this bench is per tank for this reason; the pond is drawn as
                // one surface, so the phase has to be carried per cell or five
                // hexagons breathe together and read as one animation played five
                // times.
                var phases = pond.Select(Stage3D.SwellAt).ToList();
                Check("and each flooded cell starts its loop somewhere else",
                    phases.Distinct().Count() == phases.Count,
                    $"{phases.Count - phases.Distinct().Count()} of "
                    + $"{phases.Count} cells share a phase with another");

                // The step is taken in the shader, off UV2, and not by rewriting
                // the mesh: Build is deliberately gated on the board changing at
                // all, so a surface that advanced its frames through its UVs would
                // rebuild the whole board sixty times a second.
                Check("and the frame steps in the shader rather than in the mesh",
                    Stage3D.SwellShader.Contains("UV2.x")
                    && Stage3D.SwellShader.Contains("(u + step) / frames"),
                    "the surface shader does not step frames, so the loop can "
                    + "only move by the board being rebuilt");

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

            // The one number the belts read, and the whole of what stops a rut
            // being laid in a pond. Two halves of one field rather than a flag
            // beside a height, because a pair is a pair that can disagree.
            Check("a tank is wading exactly when it has a waterline",
                !Vehicle.WadingAt(float.NegativeInfinity)
                && Vehicle.WadingAt(field.WaterTop(pond[0]))
                && Vehicle.WadingAt(0.0f),
                "the belts and the stage read this one field for two questions, "
                + "and the belts read it to keep a rut out of the water");
        }
        finally
        {
            field.SetWater(null);
            field.SetRelief(null);
            if (hadWater)
                field.SetWater(Main.WaterMask(field.Columns, field.Rows));
        }
    }

    private static void Relief(HexField field, Grove? grove,
                               Action<string, bool, string> Check)
    {
        GD.Print("relief: a flat board is the board it was");

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
        field.SetRelief(Main.ReliefMap(field.Columns, field.Rows));
        try
        {
            GD.Print("relief: a click lands on the face that was drawn");

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

            GD.Print("relief: the depth key is the camera it claims to be");

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

            GD.Print("relief: ground hides what stands below it and nothing else");

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
                        Main.StepHeights(field, from, onto, 0.5f, 0.4f, 20.0f);
                    (float noseEnd, float tailEnd) =
                        Main.StepHeights(field, from, onto, 1.0f, 0.4f, 20.0f);
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
                    if (Main.SeamFlank(field, from, onto) is not Vector2I flank
                        || field.LevelAt(flank) != field.LevelAt(onto))
                        continue;
                    seams++;
                    Vector3 line = Main.SeamLine(field, Vector2.Zero, from, onto);
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
                    Vector3 line = Main.SeamEdge(field, Vector2.Zero, from, onto);
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

            GD.Print("relief: every hex is a column standing on one floor");

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

            GD.Print("relief: a wood on a rise sorts where it stands");
            Woods(field, grove, elevation, Check);

            GD.Print("relief: a route walks the board it is on");
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
        GD.Print("relief: and taking it off puts the board back");
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
        field.SetRelief(Main.ReliefMap(field.Columns, field.Rows));
        (int low, int high) = field.LevelRange;
        float tan = field.Squash / Math.Max(field.RiseFactor, 0.01f);
        float shift = (high - low) * field.Lift * tan * tan;
        field.SetRelief(null);
        Check("and no cell ever changes places with another over it",
            shift < gap, $"height moves a cell {shift:F1}px of depth against "
                         + $"{gap:F1}px between the closest two");
    }
}
