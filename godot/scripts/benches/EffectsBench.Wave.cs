using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The blast wave's half of the effects bench: <see cref="ProcWave"/>, set off
/// on the aimed cell, with its dials.
///
/// <b>Judged here before the event bench shows it</b>, the rule every effect on
/// the board has been built under: a wave is a hundred numbers on a shader, and
/// the destroyed event is a queue of six things of which the wave is one. The
/// aimed cell decides the mask - this board's plinth and pond give a wave cells
/// to stop at.
/// </summary>
public sealed partial class EffectsBench
{
    /// <summary>One dial of the wave: a uniform on both planes.</summary>
    private readonly record struct Ripple(string Id, string Name, string Label,
                                          double Lo, double Hi, double Step,
                                          string Unit = "");

    private static readonly Ripple[] Ripples =
    {
        new("wave.reach", "reach", "how far, in radii", 1.5, 3.5, 0.05),
        new("wave.shock_at", "shock_at", "shock reaches the edge in", 0.15, 1.20, 0.01, "s"),
        new("wave.flame_lag", "flame_lag", "flame runs behind by", 0.0, 0.50, 0.01, "s"),
        new("wave.shock_wide", "shock_wide", "shock band", 0.04, 0.50, 0.01),
        new("wave.shock_gain", "shock_gain", "shock light", 0.0, 1.5, 0.05),
        new("wave.flame_wide", "flame_wide", "flame band", 0.10, 1.60, 0.02),
        new("wave.flame_gain", "flame_gain", "flame light", 0.0, 2.0, 0.05),
        new("wave.flame_tear", "flame_tear", "tongues", 0.0, 1.0, 0.05),
        new("wave.glow_gain", "glow_gain", "embers", 0.0, 1.0, 0.02),
        new("wave.dust_wide", "dust_wide", "dust skirt", 0.05, 1.50, 0.05),
        new("wave.dust_gain", "dust_gain", "dust", 0.0, 1.0, 0.02),
        new("wave.soot_gain", "soot_gain", "soot", 0.0, 1.0, 0.02),
        new("wave.soot_linger", "soot_linger", "soot lingers, of life", 0.1, 1.0, 0.05),
        new("wave.shock_warp", "shock_warp", "ground pulled by the shock", 0.0, 0.05, 0.001),
        new("wave.shock_line", "shock_line", "pressure line", 0.01, 0.20, 0.005),
        new("wave.lick_height", "lick_height", "tongues stand, in radii", 0.0, 2.0, 0.05),
        new("wave.lick_gain", "lick_gain", "tongues light", 0.0, 2.5, 0.05),
        new("wave.lick_space", "lick_space", "a tongue every, in radii", 0.15, 1.2, 0.01),
        new("wave.lick_linger", "lick_linger", "tongues burn on, of a run", 0.0, 1.0, 0.05),
        new("wave.spark_gain", "spark_gain", "sparks light", 0.0, 3.0, 0.05),
        new("wave.spark_speed", "spark_speed", "sparks leave at, radii/s", 0.5, 5.0, 0.1),
        new("wave.spark_fall", "spark_fall", "sparks fall at", 0.5, 8.0, 0.1),
        new("wave.spark_size", "spark_size", "spark size", 0.005, 0.06, 0.001),
        new("wave.spark_life", "spark_life", "spark lives", 0.2, 1.5, 0.05, "s"),
        new("wave.smoke_height", "smoke_height", "smoke climbs, in radii", 0.0, 3.0, 0.05),
        new("wave.smoke_gain", "smoke_gain", "smoke", 0.0, 1.0, 0.02),
        new("wave.smoke_back", "smoke_back", "smoke wall, of the flame's radius", 0.3, 1.0, 0.05),
        new("wave.smoke_linger", "smoke_linger", "smoke lingers, of life", 0.2, 1.5, 0.05),
    };

    /// <summary>The wave's dials as the self test reads them: id, uniform, band
    /// and the value it opens on - <c>PuffRows</c>' arrangement.</summary>
    internal static IEnumerable<(string Id, string Name, double Lo, double Hi, double Opens)>
        WaveRows(EffectsBench bench) =>
        Ripples.Select(r => (r.Id, r.Name, r.Lo, r.Hi, bench.Rippled(r)));

    /// <summary>The wave's rows that are not dials on a uniform: the event's
    /// own organs. For the self test's cross-check of <c>effects.json</c>.</summary>
    internal static readonly string[] WaveOwn =
        { "wave.fire_now", "wave.hold", "wave.scrub", "wave.life", "wave.state" };

    private readonly Dictionary<string, float> _rippled = new();
    private bool _surge;
    private float _waveLife = ProcWave.LifeDefault;
    private bool _waveHold;
    private float _waveScrub;

    private IEnumerable<ProcWave> Waving() =>
        _stage?.Waving ?? Array.Empty<ProcWave>();

    /// <summary>Set the wave off on the aimed cell, through the stage's own
    /// pool - <see cref="Burst"/>'s rule.</summary>
    private void Surge()
    {
        if (_stage is null)
            return;
        _stage.Wave(_at);
    }

    /// <summary>Push every wave dial into a new wave - the answer to
    /// <see cref="Stage3D.Surge"/>, <see cref="Attire"/>'s twin.</summary>
    private void Wash(ProcWave wave)
    {
        wave.Life = _waveLife;
        wave.Dial("life", _waveLife);
        wave.Hold = _waveHold;
        foreach ((string name, float value) in _rippled)
            wave.Dial(name, value);
    }

    /// <summary>What a dial shows: what was turned, else the shaders' own
    /// default (<see cref="ProcWave.Declared"/>); NaN when none of the four
    /// declares the name, which is what the self test looks for.</summary>
    private double Rippled(Ripple dial) =>
        _rippled.TryGetValue(dial.Name, out float held)
            ? held : ProcWave.Declared(dial.Name);

    private void RippleTurn(Ripple dial, double value)
    {
        _rippled[dial.Name] = (float)value;
        foreach (ProcWave wave in Waving())
            wave.Dial(dial.Name, (float)value);
        if (_refire)
            Surge();
    }

    private void WavePanel()
    {
        if (_panel is null)
            return;
        _panel.Heading("wave", "the blast wave");
        _panel.Press("wave.fire_now", "set one off  (W)", Surge);
        _panel.Toggle("wave.hold", "hold the clock", () => _waveHold, v =>
        {
            _waveHold = v;
            foreach (ProcWave wave in Waving())
                wave.Hold = v;
        });
        _panel.Slide("wave.scrub", "age held at", 0.0, 2.0, 0.02,
                     () => _waveScrub, v =>
                     {
                         _waveScrub = (float)v;
                         foreach (ProcWave wave in Waving())
                             wave.Clock = _waveScrub;
                     }, "s");
        _panel.Slide("wave.life", "whole event", 0.4, 3.0, 0.05,
                     () => _waveLife, v =>
                     {
                         _waveLife = (float)v;
                         foreach (ProcWave wave in Waving())
                         {
                             wave.Life = _waveLife;
                             wave.Dial("life", _waveLife);
                         }
                     }, "s");
        _panel.Readout("wave.state", () =>
        {
            ProcWave? one = Waving().FirstOrDefault(w => w.Alive);
            if (one is null)
                return "no wave running";
            float shock = ProcWave.ShockAt(one.Age, (float)Rippled(Ripples[0]),
                                           (float)Rippled(Ripples[1]));
            return $"{one.Age:F2}s of {one.Life:F2}, shock at {shock:F2} radii";
        });
        foreach (Ripple dial in Ripples)
        {
            Ripple it = dial;
            _panel.Slide(it.Id, it.Label, it.Lo, it.Hi, it.Step,
                         () => Rippled(it), v => RippleTurn(it, v), it.Unit);
        }
    }
}
