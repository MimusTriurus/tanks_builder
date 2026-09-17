using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The event bench's panel: the common rows and one button per event, each
/// button a call on <see cref="Playback"/> and nothing else.
///
/// <b>A button is registered as well as built</b>, under the same id the panel
/// row has, so <c>--play id,id</c> presses the same delegate a click does and a
/// screenshot "after event X" is one command. The registry is the list the
/// self test walks too.
///
/// Row ids and their groups are the plan's - docs/effects-benches.md §4 - and
/// <c>events.json</c> carries their captions; a button whose picture is not
/// built yet calls <see cref="Playback.Placeholder"/>, which puts a TODO over
/// the tank or the cell rather than doing nothing.
/// </summary>
public sealed partial class EventBench
{
    /// <summary>Every button by id, in the order built.</summary>
    private readonly List<(string Id, Action Go)> _buttons = new();

    private void Button(string id, string text, Action go)
    {
        _buttons.Add((id, go));
        _panel!.Press(id, text, go);
    }

    private void Buttons(string id, string left, Action goLeft,
                         string right, Action goRight)
    {
        _buttons.Add((id + ".left", goLeft));
        _buttons.Add((id + ".right", goRight));
        _panel!.PressPair(id, left, goLeft, right, goRight);
    }

    /// <summary>Press a button by id, as <c>--play</c> does. False when there
    /// is no such button; the left and right halves of a pair answer to
    /// <c>id.left</c> and <c>id.right</c>, and a bare pair id presses the left.
    /// </summary>
    public bool Press(string id)
    {
        foreach ((string had, Action go) in _buttons)
            if (had == id || had == id + ".left")
            {
                go();
                return true;
            }
        return false;
    }

    /// <summary>The ids that can be pressed, for the self test and for the
    /// message an unknown <c>--play</c> gets.</summary>
    public IReadOnlyList<string> ButtonIds => _buttons.Select(b => b.Id).ToList();

    private void Panel(CanvasLayer layer)
    {
        _text = PanelText.Load(Path.Combine(
            ProjectSettings.GlobalizePath("res://"), PanelFile));
        GD.Print(_text.Loaded ? "events: panel json"
                 : $"events: panel built-in ({PanelFile}: {_text.Error})");
        _panel = new ControlPanel { Text = _text };
        _panel.Prepare();

        // --- the common rows ------------------------------------------------
        _panel.Heading("bench", "bench");
        List<string> names = _vehicles.Select((v, i) => $"{i}  {v.Tag}").ToList();
        _panel.Choice("bench.actor", "actor", names, () => _actor,
                      i => _actor = Math.Clamp(i, 0, _vehicles.Count - 1));
        _panel.Choice("bench.target", "target", names, () => _target,
                      i => _target = Math.Clamp(i, 0, _vehicles.Count - 1));
        _panel.Readout("bench.cell", () =>
            $"cell ({_cell.X},{_cell.Y}) level {_field.LevelAt(_cell)}"
            + (_field.IsRamp(_cell) ? ", a ramp" : "")
            + (_field.IsDeep(_cell) ? ", deep water"
               : _field.IsWater(_cell) ? ", a ford" : "")
            + (_field.CoverAt(_cell) != Cover.None
                ? $", {_field.CoverAt(_cell).ToString().ToLowerInvariant()}" : "")
            + "   - middle click picks");
        _panel.Radio("bench.face", () => "shot comes from: shooter stands there",
                     HexField.EdgeHeadings.Select(h => $"{h}").ToList(),
                     () => _face, i => _face = i);
        _panel.Slide("bench.heading", "target hull heading", 0.0, 345.0, 15.0,
                     () => _heading, v => { _heading = v; Face(); }, "°");
        _panel.Slide("bench.speed", "playback speed", 0.25, 4.0, 0.25,
                     () => _speed, v => _speed = v, "x");
        _panel.Readout("bench.state", Note);
        _panel.Readout("bench.playing", () =>
            _play is null ? "" : _play.Busy ? $"playing: {_play.Now}" : "idle");
        // Whether the pond is swum or drowned in - see TankTick.Amphibious.
        _panel.Toggle("bench.amphibious", "wading gear on every class  (--no-amphibious)",
                      () => Tick.Amphibious, v => Tick.Amphibious = v);
        // How deep every class sits when it swims, over its own figure - see
        // TankTick.DraughtScale. Read back as what the target's line is in
        // pixels of its hull, which is what the eye is judging.
        _panel.Slide("bench.draught", "draught  (--draught)", 0.25, 1.5, 0.05,
                     () => Tick.DraughtScale, v => Tick.DraughtScale = v, "x",
                     () => Target.Atlas is null ? ""
                         : $"{Tick.Draught(Target):F0}px of a "
                           + $"{Target.Atlas.DeckHeightPx * Target.Sprite.BodyScale:F0}px deck");
        // The pond itself, beside the draught, because the three are one
        // picture: how much water a hull swims in and how far down a drowned
        // one goes. The harness has the same two rows as ground.deep_depth and
        // ground.deep_bed. Live: Settle follows the ride frame by frame and the
        // stage rebuilds off its signature, which carries both numbers. The
        // second readout says what the drowning has to put under - the
        // target's roof against the water over the bed - which is the one
        // comparison the bed's number is for (Stage3D.Shrunk).
        _panel.Slide("bench.deep_depth", "deep water depth  (--deep-depth)",
                     0.5, 1.0, 0.05,
                     () => _field.DeepDepth,
                     v => { _field.DeepDepth = v; _field.QueueRedraw(); },
                     "of a level",
                     () => $"{_field.DeepRise:F0}px over the level, "
                           + $"{_field.Lift - _field.DeepRise:F0}px under the bank");
        _panel.Slide("bench.deep_bed", "how deep the pond is drawn  (--deep-bed)",
                     0.0, 2.0, 0.05,
                     () => _field.DeepBed,
                     v => { _field.DeepBed = v; _field.QueueRedraw(); },
                     "of a level under -1",
                     () => $"{_field.DeepRise + _field.Lift * (float)_field.DeepBed:F0}px "
                           + "of water over the bed"
                           + (Target.Atlas is null ? ""
                              : $", the target's roof {Target.Atlas.RoofRisePx * Target.Sprite.BodyScale:F0}px"));
        // And what a hull throws going in off the bank - see Plunge.Style. Read
        // at the plunge, so switching it takes the next drive in.
        _panel.Choice("bench.splash", "splash off the bank  (--splash)", Main.SplashNames,
                      () => (int)_splash, i => _splash = (Plunge.Style)i);
        Button("bench.reset", "reset  (R)", Reset);

        // --- the tank -------------------------------------------------------
        // The actor does - shoots, rams, turns its turret, drives on to the
        // mine; the target is done to - shot, pushed, knocked out, set alight.
        // Read the way the words read, because the first thing a person at the
        // panel did with the other arrangement was ask why the shot flew
        // backwards.
        _panel.Heading("tank", "tank");
        Buttons("tank.hit", "pierce", () => _play?.Shot(Actor, Target, _face, 2),
                "bounce", () => _play?.Shot(Actor, Target, _face, 0));
        Buttons("tank.hit.on", "ricochet on",
                () => _play?.Ricochet(Actor, Target, _face),
                "TD through", () => _play?.Through(Actor, Target, _face));
        // The actor shoots, as everywhere else on this panel - and the other
        // four are refused in words, the way the bulldozer's button refuses
        // anything but the heavy. The arc is the shooter's class and this row
        // does not override it: there is no "shoot this one over" anywhere.
        // Right half is the same bomb with nothing standing on the hex, which is
        // what a mortar is aimed at in the first place.
        Buttons("tank.hit.top", "HM from above",
                () => _play?.Lob(Actor, Target, _face),
                "HM into the hex", () => _play?.LobAt(Actor, _cell, _face));
        Buttons("tank.state.out", "knocked out", () => _play?.KnockOut(Target),
                "destroyed", () => _play?.Destroy(Target));
        Buttons("tank.state.fire", "catches fire", () => _play?.Burn(Target),
                "extinguished", () => _play?.Extinguish(Target));
        // Driven, not put: the swim is an entry now - off the bank into the
        // pond, splash and all (docs/swim-plan.md). The put is kept as its own
        // button because a still picture of a floating hull wants no drive in
        // front of it.
        Buttons("tank.state.water", "drives in", () => _play?.Drive(Target, _cell),
                "sunk", () => _play?.Sink(Target, _cell));
        Button("tank.state.afloat", "put afloat", () => _play?.Swim(Target, _cell));
        // The actor rams the target standing on bench.cell, along bench.face:
        // which hex the target is thrown on to is what the two knobs choose, and
        // plain ground, a bank, the pond and the minefield are all one event.
        Buttons("tank.ram", "ram: push",
                () => _play?.Ram(Actor, Target, _cell, _face),
                "ramp slide", () => _play?.RamRamp(Actor, Target));
        Buttons("tank.mine", "mine blows",
                () => _play?.Mine(Actor),
                "plough clears",
                () => _play?.Placeholder("plough clears the mine", Actor, null));
        Buttons("tank.turret", "turret to axis", () => _play?.TurretTo(Actor, _face),
                "turret forward", () => _play?.TurretForward(Actor));

        // --- the field ------------------------------------------------------
        // Everything here happens on bench.cell.
        _panel.Heading("field", "field");
        Buttons("field.forest", "wood ignites", () => _play?.Ignite(_cell),
                "burns out", () => _play?.Quench(_cell));
        Button("field.forest.he", "HE into the wood",
               () => _play?.Blast(_cell, (float)_might));
        Button("field.forest.fell", "HT fells wood",
               () => _play?.Fell(Actor, _cell));
        Buttons("field.smoke", "smoke laid",
                () => _play?.Placeholder("smoke on the cell", null, _cell),
                "smoke clears",
                () => _play?.Placeholder("smoke dissipates", null, _cell));
        Buttons("field.mine", "mine laid",
                () => _play?.Placeholder("mine marker", null, _cell),
                "mine removed",
                () => _play?.Placeholder("mine marker off", null, _cell));
        Buttons("field.wall", "wall: shot", () => _play?.WallShot(_cell, _face),
                "wall: rammed", () => _play?.WallRam(Actor, _cell, _face));
        Button("field.wall.hull", "HT drives through wall",
               () => _play?.WallHull(Actor, _cell, _face));
        _panel.Slide("field.blast.might", "burst size", 0.25, 3.0, 0.25,
                     () => _might, v => _might = v, "x");
        Button("field.blast", "round into cell",
               () => _play?.Blast(_cell, (float)_might));
        Button("field.round", "field tick", () => _play?.Round());

        _panel.OpenDefaults(_flagged);
        Vet();
        _panel.Expand("bench", true);
        _panel.Expand(Set is "tank" or "field" ? Set : "bench", true);
        if (!NoUi)
        {
            layer.AddChild(_panel);
            _panel.AddHandle();
        }
        else
        {
            _panel.QueueFree();
            _panel = null;
        }
    }

    /// <summary>Check <see cref="PanelFile"/> against the rows this panel
    /// built, in both directions - the tank bench's Vet.</summary>
    private void Vet()
    {
        if (!_text.Loaded || _panel is null)
            return;
        var built = _panel.Ids;
        var haveRows = built.Select(pair => pair.Id).ToHashSet();
        var fileRows = _text.RowIds.ToHashSet();
        foreach ((string id, string group) in built)
        {
            if (!fileRows.Contains(id))
                GD.PushWarning($"events: {PanelFile} has no entry for row {id}");
            else if (_text.GroupOf(id) != group)
                GD.PushWarning($"events: {PanelFile} puts {id} in group "
                               + $"'{_text.GroupOf(id)}', the panel in '{group}'");
        }
        foreach (string id in fileRows)
            if (!haveRows.Contains(id))
                GD.PushWarning($"events: {PanelFile} names a row this panel "
                               + $"does not build: {id}");
    }
}
