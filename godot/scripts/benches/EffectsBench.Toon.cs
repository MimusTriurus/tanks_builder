using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The stylised explosion's half of the effects bench: <see cref="ToonBlast"/>,
/// set off on the aimed cell, with its dials - half of them the model's fields,
/// half the puff shader's uniforms, the imported burst's arrangement.
///
/// <b>Judged here before anything on the board plays it</b>, the rule every
/// effect has been built under: this one is the third answer to what an
/// explosion looks like, and the bench is where the three stand on one board.
/// </summary>
public sealed partial class EffectsBench
{
    /// <summary>One dial of the toon burst: which half owns the number.</summary>
    private readonly record struct Cel(string Id, bool Shader, string Name, string Label,
                                       double Lo, double Hi, double Step, string Unit = "");

    private static readonly Cel[] Cels =
    {
        // --- the cluster: the model ------------------------------------------
        new("toon.puffs", false, "puffs", "puffs in the cluster", 1, 40, 1),
        new("toon.life", false, "life", "whole event", 0.4, 3.0, 0.05, "s"),
        new("toon.reach", false, "reach", "how far they get, in tiles", 0.05, 1.5, 0.01),
        new("toon.size", false, "size", "one puff, in tiles", 0.05, 1.0, 0.01),
        new("toon.grow", false, "grow", "growth after the pop", 0.5, 3.0, 0.05),
        new("toon.slow", false, "slow", "how much they slow down", 0.0, 1.0, 0.02),
        new("toon.stagger", false, "stagger", "disagreement about leaving", 0.0, 0.6, 0.01),
        new("toon.flash", false, "flash", "white flash, of a life", 0.0, 0.5, 0.01),
        new("toon.burn", false, "burn", "fire to smoke at, of a life", 0.0, 1.0, 0.01),
        new("toon.burn_spread", false, "burn_spread", "over how much of it", 0.02, 1.0, 0.01),
        new("toon.erode", false, "erode", "eaten away from, of a life", 0.0, 0.98, 0.01),
        new("toon.rise", false, "rise", "how far the cluster rises, in tiles", 0.0, 1.0, 0.01),
        new("toon.flat", false, "flat", "disc against column", 0.0, 1.0, 0.02),
        new("toon.seat", false, "seat", "seat above the ground, in tiles", 0.0, 0.5, 0.01),
        new("toon.seed", false, "seed", "which cluster", 1, 64, 1),
        // --- the look: the shader --------------------------------------------
        new("toon.fire_strength", true, "fire_strength", "fire pushed past white", 0.5, 8.0, 0.1),
        new("toon.warble", true, "warble", "the cut warbles by", 0.0, 0.6, 0.01),
        new("toon.warble_scale", true, "warble_scale", "warble, how coarse", 0.5, 8.0, 0.1),
        new("toon.warble_speed", true, "warble_speed", "warble, how fast", 0.0, 4.0, 0.05),
        new("toon.scissor", true, "scissor", "the cut", 0.02, 0.9, 0.01),
        new("toon.smoke_gain", true, "smoke_gain", "smoke, how light", 0.2, 3.0, 0.05),
        new("toon.smoke_lit", true, "smoke_lit", "lit lumps through the smoke", 0.0, 1.0, 0.02),
    };

    /// <summary>The toon table as the self test reads it.</summary>
    internal static IEnumerable<(string Id, bool Shader, string Name, double Lo, double Hi, double Opens)>
        ToonRows(EffectsBench bench) =>
        Cels.Select(c => (c.Id, c.Shader, c.Name, c.Lo, c.Hi, (double)bench.Celled(c)));

    /// <summary>The toon rows that are not dials: the event's own organs.</summary>
    internal static readonly string[] ToonOwn =
        { "toon.fire_now", "toon.hold", "toon.scrub", "toon.state" };

    private readonly Dictionary<string, float> _celled = new();
    private bool _toon;
    private bool _toonHold;
    private float _toonScrub;

    /// <summary>A cluster nobody fires, asked what its model opens on -
    /// <see cref="Fresh"/>'s arrangement.</summary>
    private static readonly ToonBlast FreshToon = new();

    private IEnumerable<ToonBlast> Tooning() =>
        _stage?.Tooning ?? Array.Empty<ToonBlast>();

    /// <summary>Set the cluster off on the aimed cell, through the stage's pool.</summary>
    private void Toon()
    {
        if (_stage is null)
            return;
        Vector2 spot = _stage.Origin + _field.CellCentre(_at);
        float lift = _field.LevelAt(_at) * _field.Lift;
        _stage.Toon(spot, lift);
    }

    private float Celled(Cel dial)
    {
        string key = (dial.Shader ? "ink." : "model.") + dial.Name;
        if (!_celled.TryGetValue(key, out float now))
        {
            now = dial.Shader ? ProcBlast.Uniform(ToonBlast.Code, dial.Name)
                              : FreshToon.Model(dial.Name);
            _celled[key] = now;
        }
        return now;
    }

    private void CelTurn(Cel dial, double value)
    {
        _celled[(dial.Shader ? "ink." : "model.") + dial.Name] = (float)value;
        foreach (ToonBlast blast in Tooning())
        {
            if (dial.Shader)
                blast.Dial(dial.Name, (float)value);
            else
                blast.Model(dial.Name, (float)value);
        }
        if (_refire)
            Toon();
    }

    /// <summary>Push every toon dial into a cluster on its way out - the answer
    /// to <see cref="Stage3D.Style"/>.</summary>
    private void Styling(ToonBlast blast)
    {
        blast.Hold = _toonHold;
        foreach (Cel dial in Cels)
        {
            if (dial.Shader)
                blast.Dial(dial.Name, Celled(dial));
            else
                blast.Model(dial.Name, Celled(dial));
        }
    }

    private void ToonPanel()
    {
        if (_panel is null)
            return;
        _panel.Heading("toon", "the drawn explosion");
        _panel.Press("toon.fire_now", "set one off  (T)", Toon);
        _panel.Toggle("toon.hold", "hold the clock", () => _toonHold, v =>
        {
            _toonHold = v;
            foreach (ToonBlast blast in Tooning())
                blast.Hold = v;
        });
        _panel.Slide("toon.scrub", "age held at", 0.0, 3.0, 0.02,
                     () => _toonScrub, v =>
                     {
                         _toonScrub = (float)v;
                         foreach (ToonBlast blast in Tooning())
                             blast.Clock = _toonScrub;
                     }, "s");
        _panel.Readout("toon.state", () =>
        {
            ToonBlast? one = Tooning().FirstOrDefault(b => b.Alive);
            return one is null ? "no cluster running"
                : $"{one.Age:F2}s of {one.Life:F2}";
        });
        foreach (Cel dial in Cels)
        {
            Cel it = dial;
            _panel.Slide(it.Id, it.Label, it.Lo, it.Hi, it.Step,
                         () => Celled(it), v => CelTurn(it, v), it.Unit);
        }
    }
}
