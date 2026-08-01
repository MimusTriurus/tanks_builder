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
    public static int Run(HexField field, TankSprite tank)
    {
        int failed = 0;

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

        GD.Print("turret modes");
        tank.TurretLocked = true;
        tank.HullFacing = 270.0;
        tank.TurretFacing = 0.0;
        for (int i = 0; i < 24; i++)
            tank.TurnHull(15.0);
        Check("locked turret holds its world heading through a full hull turn",
            Math.Abs(WrapAngle(tank.TurretFacing - 0.0)) < 1e-6,
            $"turret ended at {tank.TurretFacing:F3}");
        Check("locked hull still came back round",
            Math.Abs(WrapAngle(tank.HullFacing - 270.0)) < 1e-6,
            $"hull ended at {tank.HullFacing:F3}");

        tank.TurretLocked = false;
        tank.HullFacing = 270.0;
        tank.TurretFacing = 0.0;
        double offsetBefore = WrapAngle(tank.TurretFacing - tank.HullFacing);
        for (int i = 0; i < 7; i++)
            tank.TurnHull(23.0);
        double offsetAfter = WrapAngle(tank.TurretFacing - tank.HullFacing);
        Check("free turret keeps a constant offset from the hull",
            Math.Abs(WrapAngle(offsetAfter - offsetBefore)) < 1e-6,
            $"offset {offsetBefore:F3} -> {offsetAfter:F3}");
        Check("free turret actually moved",
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
        // 0.045 is about 3px of roof travel on a 68px hull; past that the
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
        // 68px of lever from the contact point to the roof - the same figure the
        // pitch amplitude check is calibrated against
        const double roofLeverPx = 68.0;
        double tremblePeak = 0.0;
        int pitchCrossings = 0, rollCrossings = 0, axesAgree = 0, trembleSamples = 0;
        double lastPitch = 0.0, lastRoll = 0.0;
        for (int i = 0; i < 60; i++)
        {
            tremble.Advance(0.0, dt);
            tremblePeak = Math.Max(tremblePeak, Math.Abs(tremble.Pitch));
            if (i > 0 && Math.Sign(tremble.Pitch) != Math.Sign(lastPitch)) pitchCrossings++;
            if (i > 0 && Math.Sign(tremble.Roll) != Math.Sign(lastRoll)) rollCrossings++;
            if (i > 0)
            {
                trembleSamples++;
                if (Math.Sign(tremble.Pitch) == Math.Sign(tremble.Roll)) axesAgree++;
            }
            lastPitch = tremble.Pitch;
            lastRoll = tremble.Roll;
        }

        // The contrast with the rumble, and the reason this class exists: the
        // rumble is driven by distance and a stopped tank gets nothing from it.
        Check("the tremble runs with the tank standing still", tremblePeak > 0.0,
            $"peak {tremblePeak:F5} at zero speed");
        // Standing still it is near the floor of what a linear filter can show
        // as motion rather than as a soft edge. Below about a third of a pixel
        // it stops reading as a tremble at all.
        Check("standing still it is a third of a pixel of roof travel",
            tremblePeak * roofLeverPx is > 0.3 and < 0.6,
            $"{tremblePeak * roofLeverPx:F2}px on a {roofLeverPx:F0}px lever");
        // Under 30Hz or it aliases into a slow wobble at 60fps; over a few Hz or
        // it is a sway rather than a tremble. Sign changes are twice the
        // frequency, and this is one second of frames.
        Check("the tremble is a tremble, not a sway or an alias",
            pitchCrossings is > 12 and < 40 && rollCrossings is > 8 and < 40,
            $"{pitchCrossings} pitch and {rollCrossings} roll crossings in 1s");
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
            $"{movingPeak * roofLeverPx:F2}px moving vs {tremblePeak * roofLeverPx:F2}px standing");
        Check("under way it is still under a pixel of roof travel",
            movingPeak * roofLeverPx is > 0.6 and < 1.2,
            $"{movingPeak * roofLeverPx:F2}px on a {roofLeverPx:F0}px lever");
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

        // Same geometry guard the rumble roll has. A tilt about the heading and
        // a tilt reachAcross it project differently, and if both came out near
        // vertical on some heading the tremble would vanish there.
        double worstTrembleAxis = 1.0;
        int worstTrembleHeading = -1;
        foreach (int heading in HexField.EdgeHeadings)
        {
            double reachAcross = Math.Max(
                Math.Abs(tank.Atlas!.GroundDirection(heading).X),
                Math.Abs(tank.Atlas.GroundDirection(heading + 90.0).X));
            if (reachAcross < worstTrembleAxis)
            {
                worstTrembleAxis = reachAcross;
                worstTrembleHeading = heading;
            }
        }
        Check("the tremble has a sideways component at every heading",
            worstTrembleAxis > 0.5, $"worst is {worstTrembleHeading} deg at {worstTrembleAxis:F2}");

        GD.Print("turret scan");
        var scan = new TurretScan();
        scan.Reset();
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
        // The quantisation lesson, made executable. A scan smaller than one
        // frame step draws nothing at all on this atlas: the asked-for few
        // degrees of sway is below what twelve frames can express.
        Check("the scan is at least one frame step wide", scanPeak >= scanStep - 1e-6,
            $"{scanPeak:F1} deg against a {scanStep:F1} deg step");
        Check("it traverses rather than snapping",
            biggestMove <= scan.SlewRate * dt + 1e-9,
            $"biggest step {biggestMove:F3} deg in a frame");
        // Mostly parked. A turret in constant motion reads as a search radar.
        Check("it spends most of its time dwelling",
            movingFrames < 3600 * 0.6, $"{movingFrames} of 3600 frames traversing");

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

        GD.Print("recoil");
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
            Math.Abs(liftPeak) < 0.06, $"peak {Math.Abs(liftPeak):F4}");
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
        tank.Pitch = 0.0;
        tank.Roll = 0.0;
        tank.TremblePitch = 0.008;
        tank.TrembleRoll = 0.008;
        Check("a stabilised turret sits out the engine tremble",
            tank.TiltFor(true) == Vector2.Zero && tank.TiltFor(false) != Vector2.Zero,
            $"turret {tank.TiltFor(true)} vs hull {tank.TiltFor(false)}");
        tank.TremblePitch = 0.0;
        tank.TrembleRoll = 0.0;

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

        Check("an unknown tag still gets a profile",
            MovementProfile.For("nonsense") == MovementProfile.Medium
            && MovementProfile.For("lt") == MovementProfile.Light,
            "fallback or case-insensitive lookup is broken");

        GD.Print(failed == 0 ? "SELFTEST OK" : $"SELFTEST FAILED: {failed}");
        return failed;
    }

    private static double WrapAngle(double degrees) =>
        (degrees % 360.0 + 360.0 + 180.0) % 360.0 - 180.0;
}
