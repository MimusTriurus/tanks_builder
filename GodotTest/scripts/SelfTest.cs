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
                          IReadOnlyDictionary<string, SoundSet>? sounds = null)
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
        // The lever from the contact point to the roof - the same figure the
        // pitch amplitude check is calibrated against, and the one the panel
        // quotes under the level slider.
        const double roofLeverPx = EngineTremble.RoofLeverPx;
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
            biggestMove <= scan.SlewRate * dt + 1e-9,
            $"biggest step {biggestMove:F3} deg in a frame");
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
        Check("the belts composite before everything else on the tank",
            onTank.Take(AtlasSet.TrackNames.Length)
                .SequenceEqual(AtlasSet.TrackNames),
            "they are not on the tank, they are the tank: "
            + string.Join(", ", onTank.Take(3)));
        // The belt is lower than the turret but not always behind it: the far
        // one shows over the hull deck legitimately, and only the turret is
        // tall enough to be in its way. Drawn by the parent the turret went
        // down first and the belt painted over it - 1455px of it on HTP.
        int turretAt = Array.IndexOf(onTank, "turret");
        Check("the turret composites after both belts",
            turretAt == AtlasSet.TrackNames.Length,
            $"turret at {turretAt}, belts end at {AtlasSet.TrackNames.Length}: "
            + string.Join(", ", onTank.Take(4)));
        Check("and before anything that happens to the tank",
            turretAt < Array.IndexOf(onTank, AtlasSet.ScarNames[0]),
            "damage and effects go over the turret, not under it");

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

            Check("every kind is painted on the template's frame",
                set.Names.All(n => set.Texture(n) is Texture2D art
                                   && art.GetSize() == (Vector2)set.Frame),
                $"frame {set.Frame.X}x{set.Frame.Y}");

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
                kinds.Add(field.TypeAt(new Vector2I(q, r)));
            Check("mixed puts every loaded kind on the board",
                kinds.Count == set.Names.Count,
                $"{kinds.Count} of {set.Names.Count} appeared");

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
            worked.RateAt(240.0) > idle.RateAt(0.0) * 1.3,
            $"{idle.RateAt(0.0):F1}/s at rest against {worked.RateAt(240.0):F1}/s");
        Check("working the engine thickens the smoke",
            worked.Density > idle.Density + 0.15,
            $"{idle.Density:F2} at rest against {worked.Density:F2}");

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
        strike.Strike("front", 0.3f, 1.4f);
        for (int i = 0; i < 12; i++)
            strike.Advance();
        int midway = strike.Phase;
        float midwayScale = strike.Scale;
        strike.Strike("rear", -0.2f, 0.7f);
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
            strike.Scale == 0.7f && Math.Abs(strike.Scatter + 0.2f) < 1e-6,
            $"scale {strike.Scale:F2}, scatter {strike.Scatter:F2}");
        strike.Reset();
        Check("a repaired tank is back to the plain calibre",
            strike.Scale == 1.0f && strike.Face == "", $"scale {strike.Scale:F2}");
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
            int first = tank.Damage(plate, 0.1f);
            bool spared = tank.MarksOn(other).Count == 0;
            for (int i = 1; i < atlas.ScarLevels; i++)
                tank.Damage(plate, 0.1f + 0.15f * i);
            var carried = tank.MarksOn(plate).ToList();
            // Rounds do not land on top of each other: three hits leave three
            // marks, each keeping the level it arrived with, so an early scorch
            // is still a scorch after a later round punches through beside it.
            Check("hits accumulate on the plate rather than replacing each other",
                clean && first == 0 && spared
                && carried.Count == atlas.ScarLevels
                && carried.Select(m => m.Level).SequenceEqual(
                       Enumerable.Range(0, atlas.ScarLevels))
                && carried.Select(m => m.Along).Distinct().Count() == carried.Count,
                $"{plate} carries {string.Concat(carried.Select(m => m.Level))},"
                + $" {other} stayed {(spared ? "clean" : "marked")}");
            for (int i = 0; i < 4; i++)
                tank.Damage(plate, 0.2f);
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
            int heavyRound = tank.Damage(plate, 0.0f, atlas.ScarLevels);
            tank.Repair();
            int lightFirst = tank.Damage(plate, 0.0f, 1);
            int lightSecond = tank.Damage(plate, 0.15f, 1);
            tank.Repair();
            Check("a heavier round goes deeper into clean armour",
                heavyRound == atlas.ScarLevels - 1 && lightFirst == 0 && lightSecond == 1,
                $"heavy left {heavyRound}, light left {lightFirst} then {lightSecond}");
            // The other half, and the two do not fight: three light rounds reach
            // the same hole one heavy round makes, they just take three goes.
            tank.Damage(plate, 0.0f, 2);
            int onTop = tank.Damage(plate, 0.15f, 1);
            int past = tank.Damage(plate, 0.3f, atlas.ScarLevels);
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
            int plink = tank.DamageTo(plate, 0.0f, 0);
            int plinkAgain = tank.DamageTo(plate, 0.2f, 0);
            int plinkThird = tank.DamageTo(plate, 0.4f, 0);
            Check("a round with a ceiling never gets past it, however often it lands",
                plink == 0 && plinkAgain == 0 && plinkThird == 0
                && tank.Wear(plate) == 1,
                $"{plink}/{plinkAgain}/{plinkThird}, worked {tank.Wear(plate)}"
                + " - three light rounds must not add up to a hole in a heavy");
            // The counterpart, and it used to assert the opposite - which is why
            // nothing caught the plate quietly recording only its first hit. A
            // ceiling stops the round getting *deeper*; it must not stop it
            // leaving a mark, or a tank under fire from one class stops showing
            // that it is under fire at all.
            Check("but every shell still leaves its own mark in its own place",
                tank.MarksOn(plate).Count == 3
                && tank.MarksOn(plate).Select(m => m.Along).Distinct().Count() == 3,
                $"{tank.MarksOn(plate).Count} marks at "
                + string.Join("/", tank.MarksOn(plate).Select(m => $"{m.Along:F2}"))
                + " - three hits on one plate are three marks");
            // It reaches its level on the first hit, unlike a bite. "A heavy
            // breaches a light" means the first round holes it, not the third.
            tank.Repair();
            Check("a round with a ceiling reaches it on the first hit",
                tank.DamageTo(plate, 0.0f, atlas.ScarLevels - 1)
                    == atlas.ScarLevels - 1,
                "the matchup names the outcome, not a rate of progress");
            // The mark is what this shell did; the wear is what the plate has
            // been through. They part company exactly here, and a light round
            // must not mend a hole.
            int scorchOnBreach = tank.DamageTo(plate, 0.3f, 0);
            Check("a shallow round on breached armour scorches and mends nothing",
                scorchOnBreach == 0 && tank.Wear(plate) == atlas.ScarLevels
                && tank.ScarLevel(plate) == atlas.ScarLevels - 1,
                $"left {scorchOnBreach}, plate worked {tank.Wear(plate)},"
                + $" worst mark {tank.ScarLevel(plate)}");
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
                tank.MarkOffset(plate, new TankSprite.Mark(0, 0.0f)).Length() < 1e-3,
                "so the burst's own zero scatter puts them in the same place");

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

        GD.Print("the side panel");
        // The panel is a view of the harness's state and never a second copy of
        // it, which is one claim with two halves - and the second half is a
        // feedback loop unless something stops it, because writing a widget's
        // value emits its changed signal.
        // In the tree rather than standing on its own, so the shutdown frees it
        // like any other node. A detached branch of Controls leaves its canvas
        // items behind however carefully it is freed, and the wall of leak
        // warnings that produces at exit would hide a real one later.
        var panel = new ControlPanel();
        tank.GetParent().AddChild(panel);
        bool flag = false;
        double dial = 1.0;
        int pick = 0;
        int writes = 0;
        panel.Toggle("flag", () => flag, on => { flag = on; writes++; });
        // With a note line, because that is a third delegate on the row and can
        // go stale exactly like the other two. It is what the level sliders say
        // an abstract multiplier is worth in pixels, so a note that lags is a
        // number that is wrong about the thing it was added to explain.
        double noted = double.NaN;
        panel.Slide("dial", 0.0, 10.0, 0.5, () => dial, v => { dial = v; writes++; }, "x",
            () => { noted = dial; return "aside"; });
        panel.Choice("pick", new[] { "a", "b", "c" }, () => pick,
            i => { pick = i; writes++; });

        // The harness changes behind the panel's back - a key, a start-up flag,
        // R resetting the lot - and the widgets have to follow without anything
        // telling them to.
        flag = true;
        dial = 6.5;
        pick = 2;
        panel.Sync();
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
        Collect(panel);
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
        panel.QueueFree();

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
            Check("they stand in one row, so only the size differs",
                vehicles.Select(v => field.CellAnchor(v.HomeCell).Y).Distinct().Count() == 1,
                string.Join(", ", vehicles.Select(
                    v => $"{v.Tag} y={field.CellAnchor(v.HomeCell).Y:F0}")));
            // Far enough apart that the silhouettes cannot touch. A heavy is about
            // 212px of hull broadside; anything closer than that would have the
            // spacing arguing with the size.
            double gap = vehicles.Count < 2 ? double.MaxValue
                : Enumerable.Range(1, vehicles.Count - 1).Min(
                    i => (field.CellAnchor(vehicles[i].HomeCell)
                          - field.CellAnchor(vehicles[i - 1].HomeCell)).Length());
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
                Vector2 want = field.Position + field.CellAnchor(v.Cell)
                               + field.CentreOffset;
                Check($"{v.Tag} stands on the centre of its own cell",
                    (ground - want).Length() < 0.5,
                    $"contact patch {ground} against cell centre {want}"
                    + $" at {v.Sprite.BodyScale:F2}x");
            }

            // Depth, not tree order: the tanks move, and whoever was added last
            // would otherwise win every overlap. Taken off the live position, so it
            // is right in the middle of a step and not only on arrival.
            Check("the depth order comes off the contact patch, not the cell",
                vehicles.All(v => v.Sprite.ZIndex == Mathf.RoundToInt(
                    field.CellAnchor(v.Cell).Y + field.CentreOffset.Y)),
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
        bool wasLocked = tank.TurretLocked;
        double Offset() => WrapAngle(tank.TurretFacing - tank.HullFacing);

        tank.HullFacing = 0.0;
        tank.TurretFacing = 0.0;
        tank.TurretLocked = true;
        double wasOffset = Offset();
        tank.TurnHull(30.0);
        Check("a hull turn under a held gun drives the ring",
            Math.Abs(WrapAngle(Offset() - wasOffset) - -30.0) < 1e-6,
            $"the ring moved {WrapAngle(Offset() - wasOffset):F1} deg - a stabilised "
            + "turret is being traversed even though nothing moves on screen");

        tank.HullFacing = 0.0;
        tank.TurretFacing = 0.0;
        tank.TurretLocked = false;
        before = Offset();
        tank.TurnHull(30.0);
        Check("and a turret riding round with the hull does not",
            Math.Abs(WrapAngle(Offset() - before)) < 1e-6,
            $"the ring moved {WrapAngle(Offset() - before):F1} deg - the turret "
            + "went round the world without its motor doing anything");
        tank.HullFacing = wasHull;
        tank.TurretFacing = wasTurret;
        tank.TurretLocked = wasLocked;

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
                Serial = 1, Face = "front", Scatter = 0.0f, Calibre = 1.0f,
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
            Check("a round at point blank still takes several frames to arrive",
                round.Arrived && frames >= 4,
                $"{frames} frames over {Shell.PointBlank}px at {Shell.Speed}px/s"
                + " - landing in the frame it was fired is what this replaced");
            Check("and it crosses rather than jumping",
                climbed && round.Fraction >= 1.0f,
                $"fraction ended at {round.Fraction:F2}");
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
            Check("the tracer is off until asked for, and the motor with it",
                !Main.TracerOnByDefault && !Main.TurretSoundOnByDefault,
                "both are named constants precisely so a change to them is a "
                + "change somebody made on purpose");
            var unseen = new Shell
            {
                Shooter = shooter, Target = foe, ImpactLocal = aim,
                From = foe.Sprite.ToGlobal(aim)
                       - new Vector2(Shell.PointBlank, 0.0f),
                Serial = 2, Face = "front", Scatter = 0.0f, Calibre = 1.0f,
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

        GD.Print(failed == 0 ? "SELFTEST OK" : $"SELFTEST FAILED: {failed}");
        return failed;
    }

    private static double WrapAngle(double degrees) =>
        (degrees % 360.0 + 360.0 + 180.0) % 360.0 - 180.0;
}
