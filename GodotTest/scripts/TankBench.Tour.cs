using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>The ram tour: drive at every wall on the board in turn, and say
/// what each ram did to the masonry it was not aimed at.
///
/// <b>The question this answers is per-leaf, and no single number can.</b> A
/// ring is six sections on one cell, and the whole of what the ram was taught
/// last is that it takes the section it drove into and no other - see
/// <c>WallRig.Reached</c>. <c>Loose</c> counts pieces, <c>Broken</c> counts
/// sections; neither says <em>which</em>, so a run that fells the leaf it aimed
/// at and a run that fells that leaf and the two beside it read as a heavier
/// hull rather than as a rule reaching where it was not meant to. This drives
/// all six and prints the difference each ram made to each leaf.
///
/// <b>A run and printed numbers, not a <c>--selftest</c> check</b> - the
/// bench's standing rule, and load-bearing here: a live solver steps the
/// physics and <c>--selftest</c> leaves before the first frame of a scene, so
/// how the masonry answers can only be measured by driving. What <em>is</em>
/// assertable without a frame is the plan - which walls, in which order, along
/// which heading - and that is <see cref="TankBench.TourLegs"/>, a pure
/// function the wall theme judges.
///
/// <b>The ring first and the samples after</b>, because that is the order the
/// board is built for: the tank opens inside the ring, so each of its six legs
/// is one drive out and one drive home, and by the time the samples come round
/// the ring is rubble - which is what makes the sample legs measurable at all.
/// A run-up to a sample is a ram the whole way (see <c>TankBench.RamAt</c>), so
/// it sweeps whatever it passes; passing a ring with nothing left standing
/// costs nothing.
///
/// <b>What it cannot promise is that nothing else moves at all.</b> The hull
/// does not fit in the yard - 2.76m clear against a 3.18m hull corner - so
/// driving out clips a brick or two off the mitred corner of the leaf next
/// door, and that is measured rather than hidden: the verdict names the worst
/// spill onto a leaf nobody rammed. What it does promise is the thing the rule
/// is about - that no leaf but the one named comes down.</summary>
public sealed partial class TankBench
{
    /// <summary>One ram: which wall, and the flat side of its cell the hull
    /// drives through. The heading is the side, not the leaf - which leaf that
    /// turns out to be is <c>WallKit.Fan</c> answering with geometry, and having
    /// the tour name the heading while the rig names the leaf is what puts the
    /// two under test against each other.</summary>
    public readonly record struct Leg(Vector2I Wall, int Side);

    /// <summary>The order a tour goes in: every side of the ring, then each
    /// sample wall along the lane it faces.
    ///
    /// <b>Pure, and public, so the plan can be judged without a frame.</b> Which
    /// cells carry a wall, how many sides the ring has and which lane each
    /// sample faces are all settled before anything is built, and every one of
    /// them fails silently: a sample whose run-up cell is off the board is a leg
    /// the tank simply refuses, and the picture is a tank parked in a yard.
    ///
    /// A sample whose bearing is not a flat side of the hex is left out rather
    /// than rounded to the nearest. Rounding would put the hull into the masonry
    /// at an angle and call it a ram, and the difference between a ram and a
    /// graze is measured at a tenth of the pieces - see
    /// <see cref="Samples"/>.</summary>
    public static List<Leg> TourLegs(Vector2I ring, int sides)
    {
        var legs = new List<Leg>();
        for (int k = 0; k < Math.Min(sides, HexField.EdgeHeadings.Length); k++)
            legs.Add(new Leg(ring, k));
        foreach ((Vector2I cell, float bearing, string _, WallKit.Recipe _)
                 in Samples)
        {
            int side = Array.IndexOf(HexField.EdgeHeadings,
                                     Mathf.RoundToInt(bearing));
            if (side >= 0)
                legs.Add(new Leg(cell, side));
        }
        return legs;
    }

    /// <summary>Whether <c>--ram-tour</c> was asked for.</summary>
    private bool _tour;

    /// <summary>The plan, built once the board is up. Null until then, because
    /// which cells carry a wall is the map answering, and the map is read in
    /// <c>_Ready</c>.</summary>
    private List<Leg>? _tourLegs;

    /// <summary>Which leg is under way, or <c>-1</c> before the first.</summary>
    private int _tourAt = -1;

    /// <summary>Whether a ram is out, so that the next standstill is the end of
    /// one rather than an arrival at home.</summary>
    private bool _tourRamming;

    /// <summary>How long the tank has been standing still, in seconds. The gate
    /// on reading the numbers: a pile is still being added to while anything in
    /// it is moving, so a tally read on the frame the hull stopped misses every
    /// piece the cascade takes afterwards - and the cascade is exactly what a
    /// per-leaf question is about.</summary>
    private double _tourRest;

    /// <summary>How long to wait after the tank stops before reading.
    ///
    /// <b>Two numbers, because a jammed heap has no end.</b> Nothing moving is
    /// the only honest end a live collapse has - the bench's own rule - but the
    /// solver can free a jammed pile with an enormous impulse and never come to
    /// rest at all, and a tour that waits for that prints nothing at all. The
    /// cap turns "it never settled" into a named moment instead of silence, and
    /// the report says which of the two it was.</summary>
    private const double TourPause = 0.5;

    /// <summary>How long to wait for a pile that will not settle. See
    /// <see cref="TourPause"/>.</summary>
    private const double TourGiveUp = 8.0;

    /// <summary>Which section the hull named on its way into the wall this leg,
    /// latched. <c>WallRig.Entered</c> is cleared the moment the tank stops
    /// sweeping that prop, so by the time the pile has settled and the numbers
    /// are worth reading the answer is gone - it has to be caught while the ram
    /// is happening.</summary>
    private int _tourFace = -1;

    /// <summary>What every section of every wall had lost when this leg began,
    /// so the report is a difference rather than a running total. Indexed the
    /// way <see cref="_walls"/> is.</summary>
    private readonly List<int[]> _tourWas = new();

    /// <summary>The same for sections that had already come down.</summary>
    private readonly List<int> _tourBroke = new();

    /// <summary>Which leaf each ram named, as <c>(cell)/leaf</c>. Kept because
    /// the per-leg lines are ten lines and the question is one question.
    /// </summary>
    private readonly List<string> _tourNamed = new();

    /// <summary>The worst spill onto a leaf nobody rammed, and where it was.
    /// Reported rather than hidden: the hull does not fit in the yard, so a
    /// mitred corner losing a brick or two is the price of the board and not a
    /// rule reaching too far - but a spill that grows into a leaf is the second
    /// of those wearing the clothes of the first.</summary>
    private int _tourSpill;

    private string _tourSpillAt = "none";

    /// <summary>Sections that came down other than the one the ram named. The
    /// number the whole tour is for.</summary>
    private int _tourUnnamed;

    /// <summary>How many legs were read while the pile was still moving.
    /// </summary>
    private int _tourStuck;

    /// <summary>Set the tour going and step it. Called after the tank has been
    /// driven and the wall has felt it, so a leg that ends this frame is judged
    /// on this frame's masonry.</summary>
    private void Tour(double delta)
    {
        if (!_tour || _walls.Count == 0 || _garage.Count == 0 || Wall is null)
            return;
        if (_tourLegs is null)
        {
            _tourLegs = TourLegs(Wall.Cell, _brick.Sides);
            GD.Print($"tank bench: ram tour over {_tourLegs.Count} wall(s) - "
                     + string.Join(" ", _tourLegs.Select(
                         l => $"({l.Wall.X},{l.Wall.Y})@"
                              + HexField.EdgeHeadings[l.Side])));
        }
        // Caught while the ram is happening - see _tourFace.
        if (_tourAt >= 0 && _tourAt < _tourLegs.Count
            && Prop(_tourLegs[_tourAt].Wall)?.Rig is { Entered: >= 0 } rig)
            _tourFace = rig.Entered;

        // Still under way. The run-up counts as moving too: a two-leg ram stands
        // still for a frame between its legs, and a standstill read there is the
        // ram being judged before it has happened.
        if (Tank.Moving || _lineUp is not null)
        {
            _tourRest = 0.0;
            return;
        }
        _tourRest += delta;
        if (_tourRest < TourPause)
            return;
        bool still = _walls.All(w => w.Rig is null or { Awake: 0 });
        if (!still && _tourRest < TourGiveUp)
            return;

        if (_tourRamming)
        {
            TourSay(still);
            _tourRamming = false;
            _tourRest = 0.0;
            // Home before the next leg, because that is the drive that was asked
            // for and because RamAt's one-leg case wants the tank on the ring's
            // own cell. An ordinary order and never a ram: the way back must not
            // sweep anything, or every leg after the first would be measuring
            // two drives.
            if (Tank.Cell != Wall.Cell && OrderTo(Wall.Cell))
                return;
        }
        if (++_tourAt >= _tourLegs.Count)
        {
            TourEnd();
            return;
        }
        TourGo(_tourLegs[_tourAt]);
    }

    /// <summary>The wall standing on a cell, or null if none does.</summary>
    private WallProp? Prop(Vector2I cell) =>
        _walls.FirstOrDefault(w => w.Cell == cell);

    /// <summary>Take the tallies down and order the ram.</summary>
    private void TourGo(Leg leg)
    {
        WallProp? wall = Prop(leg.Wall);
        _tourWas.Clear();
        _tourBroke.Clear();
        foreach (WallProp prop in _walls)
        {
            var was = new int[HexField.EdgeHeadings.Length];
            for (int s = 0; s < was.Length; s++)
                was[s] = prop.Rig?.Section(s).Gone ?? 0;
            _tourWas.Add(was);
            _tourBroke.Add(prop.Rig?.Broken ?? 0);
        }
        _tourFace = -1;
        _tourRest = 0.0;
        if (wall is null)
        {
            GD.Print($"tank bench: ram {_tourAt + 1}/{_tourLegs!.Count} - no "
                     + $"wall on ({leg.Wall.X},{leg.Wall.Y}), skipped");
            return;
        }
        _tourRamming = true;
        RamAt(wall, leg.Side);
    }

    /// <summary>What this ram did, leaf by leaf.</summary>
    private void TourSay(bool still)
    {
        Leg leg = _tourLegs![_tourAt];
        WallProp? wall = Prop(leg.Wall);
        int at = wall is null ? -1 : _walls.IndexOf(wall);
        if (wall?.Rig is not { } rig || at < 0)
            return;

        // What the leaf it named lost, and what every other leaf of the same
        // wall lost beside it. Both, because either alone is the wrong picture:
        // a leaf that came down is what a ram is for, and a leaf that lost two
        // bricks to a mitred corner is the price - see the class note.
        var spill = new List<string>();
        int took = 0, laid = 0;
        for (int s = 0; s < HexField.EdgeHeadings.Length; s++)
        {
            (int gone, int stood) = rig.Section(s);
            if (stood == 0)
                continue;
            int moved = gone - _tourWas[at][s];
            if (s == _tourFace)
            {
                took = moved;
                laid = stood;
                continue;
            }
            if (moved <= 0)
                continue;
            spill.Add($"{s}:{moved}/{stood}");
            if (moved > _tourSpill)
            {
                _tourSpill = moved;
                _tourSpillAt = $"({leg.Wall.X},{leg.Wall.Y}) leaf {s}, "
                               + $"{moved} of {stood}";
            }
        }
        // Other walls it went past on the way. A run-up is a ram the whole way,
        // so a sample leg drives out through the ring - named rather than left
        // out, because masonry falling somewhere the report does not mention is
        // exactly the failure this tour exists to catch.
        var past = new List<string>();
        for (int w = 0; w < _walls.Count; w++)
        {
            if (w == at || _walls[w].Rig is not { } other)
                continue;
            int moved = 0;
            for (int s = 0; s < HexField.EdgeHeadings.Length; s++)
                moved += other.Section(s).Gone - _tourWas[w][s];
            if (moved > 0)
                past.Add($"({_walls[w].Cell.X},{_walls[w].Cell.Y}) {moved}");
        }

        // One section down is the ram doing its job; anything past that is the
        // rule reaching where it was not meant to, and a ram that named nothing
        // and still felled something is the same failure with no name on it.
        int broke = rig.Broken - _tourBroke[at];
        _tourUnnamed += _tourFace >= 0 && broke > 0 ? broke - 1 : broke;
        _tourNamed.Add($"({leg.Wall.X},{leg.Wall.Y})/"
                       + (_tourFace >= 0 ? _tourFace.ToString() : "-"));
        if (!still)
            _tourStuck++;

        (float reach, float top, int off, _) = wall.Pile();
        GD.Print($"tank bench: ram {_tourAt + 1}/{_tourLegs.Count} "
                 + $"({leg.Wall.X},{leg.Wall.Y}) along "
                 + $"{HexField.EdgeHeadings[leg.Side]} - "
                 + (_tourFace >= 0
                        ? $"entered leaf {_tourFace}, took {took} of {laid}"
                        : "entered nothing")
                 + "; spill "
                 + (spill.Count > 0 ? string.Join(" ", spill) : "none")
                 + "; past "
                 + (past.Count > 0 ? string.Join(" ", past) : "none")
                 + $"; {broke} section(s) down"
                 + $"; reach {reach:F2}, top {top:F2}, {off} off the cell"
                 + (still ? "" : "; STILL SETTLING"));
    }

    /// <summary>The one line the ten are for.</summary>
    private void TourEnd()
    {
        string ring = $"({Wall!.Cell.X},{Wall.Cell.Y})";
        List<string> leaves = _tourNamed.Where(n => !n.EndsWith("/-")).ToList();
        int named = leaves.Where(n => n.StartsWith(ring + "/"))
                          .Distinct().Count();
        GD.Print($"tank bench: ram tour done - {_tourNamed.Count} ram(s), "
                 + $"{leaves.Count} named a leaf, {named} of {_brick.Sides} "
                 + "ring leaves named; "
                 + $"{_tourUnnamed} section(s) came down unnamed; "
                 + $"worst spill onto a leaf nobody rammed: {_tourSpillAt}"
                 + (_tourStuck > 0 ? $"; {_tourStuck} never settled" : ""));
        if (CapturePath is not null)
            Capture(CapturePath);
        GetTree().Quit();
    }
}
