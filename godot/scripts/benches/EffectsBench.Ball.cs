using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The fireball's half of the effects bench: <see cref="ProcBall"/>, set off
/// on the aimed cell, with its dials - the frame's reach, the fire's and the
/// dust's uniforms, the plate burst's arrangement.
///
/// <b>Judged here before the board plays it</b>: this is the fourth picture of
/// a destroyed tank's explosion, and the bench is where the four stand on one
/// board - <c>Space</c>, <c>W</c>, <c>T</c>, <c>B</c>.
/// </summary>
public sealed partial class EffectsBench
{
    /// <summary>One dial of the ball: which shader owns the number.</summary>
    private readonly record struct Coal(string Id, ProcBall.Part Part, string Name,
                                        string Label, double Lo, double Hi,
                                        double Step, string Unit = "");

    private static readonly Coal[] Coals =
    {
        // --- the fire -----------------------------------------------------------
        new("ball.flash_size", ProcBall.Part.Fire, "flash_size", "flash, in tiles", 0.05, 0.8, 0.01),
        new("ball.flash_gain", ProcBall.Part.Fire, "flash_gain", "flash pushed past white", 0.5, 8.0, 0.1),
        new("ball.flash_life", ProcBall.Part.Fire, "flash_life", "flash lasts", 0.02, 0.4, 0.005, "s"),
        new("ball.balls", ProcBall.Part.Fire, "balls", "bodies in the ball", 1, 40, 1),
        new("ball.ball_life", ProcBall.Part.Fire, "ball_life", "a body burns for", 0.2, 2.0, 0.05, "s"),
        new("ball.ball_lead", ProcBall.Part.Fire, "ball_lead", "how far out, of the reach", 0.1, 1.5, 0.02),
        new("ball.ball_climb", ProcBall.Part.Fire, "ball_climb", "and how far up on top", 0.0, 1.0, 0.02),
        new("ball.ball_size", ProcBall.Part.Fire, "ball_size", "one body, in tiles", 0.05, 0.6, 0.01),
        new("ball.ball_grow", ProcBall.Part.Fire, "ball_grow", "growth over a life", 0.0, 3.0, 0.05),
        new("ball.ball_gain", ProcBall.Part.Fire, "ball_gain", "fire light", 0.0, 3.0, 0.05),
        new("ball.under", ProcBall.Part.Frame, "under", "downward throws kept", 0.0, 1.0, 0.02),
        new("ball.embers", ProcBall.Part.Fire, "embers", "embers thrown", 0, 60, 1),
        new("ball.ember_speed", ProcBall.Part.Fire, "ember_speed", "embers leave at, of the reach", 0.2, 3.0, 0.05),
        new("ball.ember_fall", ProcBall.Part.Fire, "ember_fall", "embers fall by", 0.0, 3.0, 0.05),
        new("ball.ember_size", ProcBall.Part.Fire, "ember_size", "ember size", 0.003, 0.05, 0.001),
        new("ball.ember_gain", ProcBall.Part.Fire, "ember_gain", "embers light", 0.0, 3.0, 0.05),
        // --- the dust -----------------------------------------------------------
        new("ball.soot", ProcBall.Part.Dust, "soot", "elements in the head", 1, 60, 1),
        new("ball.soot_life", ProcBall.Part.Dust, "soot_life", "the head lasts", 0.5, 4.0, 0.05, "s"),
        new("ball.soot_lead", ProcBall.Part.Dust, "soot_lead", "head out, of the reach", 0.1, 1.5, 0.02),
        new("ball.soot_climb", ProcBall.Part.Dust, "soot_climb", "head up, of the reach", 0.0, 1.5, 0.02),
        new("ball.soot_size", ProcBall.Part.Dust, "soot_size", "one element, in tiles", 0.05, 0.5, 0.01),
        new("ball.soot_grow", ProcBall.Part.Dust, "soot_grow", "head growth over a life", 0.0, 3.0, 0.05),
        new("ball.soot_ink", ProcBall.Part.Dust, "soot_ink", "head, how thick", 0.0, 2.0, 0.05),
        new("ball.skirt", ProcBall.Part.Dust, "skirt", "elements in the skirt", 0, 40, 1),
        new("ball.skirt_run", ProcBall.Part.Dust, "skirt_run", "skirt out, of the reach", 0.1, 2.0, 0.05),
        new("ball.skirt_size", ProcBall.Part.Dust, "skirt_size", "one skirt element, in tiles", 0.02, 0.4, 0.01),
        new("ball.skirt_ink", ProcBall.Part.Dust, "skirt_ink", "skirt, how thick", 0.0, 2.0, 0.05),
        // --- the dust on the ground -----------------------------------------------
        new("ball.puffs", ProcBall.Part.Ground, "puffs", "elements on the ground", 0, 80, 1),
        new("ball.puff_life", ProcBall.Part.Ground, "puff_life", "ground dust lasts", 0.5, 4.0, 0.05, "s"),
        new("ball.puff_run", ProcBall.Part.Ground, "puff_run", "ground dust out, of the reach", 0.1, 1.5, 0.02),
        new("ball.puff_size", ProcBall.Part.Ground, "puff_size", "one ground element, in tiles", 0.05, 0.5, 0.01),
        new("ball.puff_grow", ProcBall.Part.Ground, "puff_grow", "ground dust growth", 0.0, 3.0, 0.05),
        new("ball.puff_ink", ProcBall.Part.Ground, "puff_ink", "ground dust, how thick", 0.0, 2.0, 0.05),
    };

    /// <summary>The ball's dials as the self test reads them.</summary>
    internal static IEnumerable<(string Id, string Name, double Lo, double Hi, double Opens)>
        BallRows(EffectsBench bench) =>
        Coals.Select(c => (c.Id, c.Name, c.Lo, c.Hi, bench.Coaled(c)));

    /// <summary>The ball's rows that are not dials on a uniform.</summary>
    internal static readonly string[] BallOwn =
        { "ball.fire_now", "ball.hold", "ball.scrub", "ball.life", "ball.reach",
          "ball.might", "ball.state" };

    private readonly Dictionary<string, float> _coaled = new();
    private bool _ball;
    private bool _ballHold;
    private float _ballScrub;
    private float _ballLife = ProcBall.LifeDefault;
    private float _ballReach = ProcBall.ReachDefault;
    private float _ballMight = 1.0f;

    private IEnumerable<ProcBall> Balling() =>
        _stage?.Balling ?? Array.Empty<ProcBall>();

    /// <summary>Set the ball off on the aimed cell, through the stage's pool.
    /// No hull to measure here, so the heart stays where the shader puts it.</summary>
    private void Ball()
    {
        if (_stage is null)
            return;
        Vector2 spot = _stage.Origin + _field.CellCentre(_at);
        float lift = _field.LevelAt(_at) * _field.Lift;
        _stage.Ball(spot, lift, ProcBall.HeartDefault, _ballMight);
    }

    /// <summary>What a dial shows: what was turned, else the shaders' own default;
    /// NaN when none of the three declares the name.</summary>
    private double Coaled(Coal dial) =>
        _coaled.TryGetValue(dial.Name, out float held)
            ? held : ProcBall.Declared(dial.Name);

    private void CoalTurn(Coal dial, double value)
    {
        _coaled[dial.Name] = (float)value;
        foreach (ProcBall ball in Balling())
            ball.Dial(dial.Part, dial.Name, (float)value);
        if (_refire)
            Ball();
    }

    /// <summary>Push every ball dial into one on its way out - the answer to
    /// <see cref="Stage3D.Fuse"/>.</summary>
    private void Fusing(ProcBall ball)
    {
        ball.Hold = _ballHold;
        ball.Life = _ballLife;
        ball.Reach = _ballReach;
        ball.Dial(ProcBall.Part.Frame, "reach", _ballReach);
        foreach (Coal dial in Coals)
            ball.Dial(dial.Part, dial.Name, (float)Coaled(dial));
    }

    private void BallPanel()
    {
        if (_panel is null)
            return;
        _panel.Heading("ball", "the fireball");
        _panel.Press("ball.fire_now", "set one off  (B)", Ball);
        _panel.Toggle("ball.hold", "hold the clock", () => _ballHold, v =>
        {
            _ballHold = v;
            foreach (ProcBall ball in Balling())
                ball.Hold = v;
        });
        _panel.Slide("ball.scrub", "age held at", 0.0, 4.0, 0.02,
                     () => _ballScrub, v =>
                     {
                         _ballScrub = (float)v;
                         foreach (ProcBall ball in Balling())
                             ball.Clock = _ballScrub;
                     }, "s");
        _panel.Slide("ball.life", "whole event", 0.5, 5.0, 0.05,
                     () => _ballLife, v =>
                     {
                         _ballLife = (float)v;
                         foreach (ProcBall ball in Balling())
                             ball.Life = _ballLife;
                     }, "s");
        _panel.Slide("ball.reach", "how far, in tiles", 0.3, 2.5, 0.02,
                     () => _ballReach, v =>
                     {
                         _ballReach = (float)v;
                         foreach (ProcBall ball in Balling())
                         {
                             ball.Reach = _ballReach;
                             ball.Dial(ProcBall.Part.Frame, "reach", _ballReach);
                         }
                         if (_refire)
                             Ball();
                     });
        _panel.Slide("ball.might", "how big, of the tuned one", 0.25, 3.0, 0.05,
                     () => _ballMight, v =>
                     {
                         _ballMight = (float)v;
                         if (_refire)
                             Ball();
                     });
        _panel.Readout("ball.state", () =>
        {
            ProcBall? one = Balling().FirstOrDefault(b => b.Alive);
            return one is null ? "no ball running"
                : $"{one.Age:F2}s of {one.Life:F2}";
        });
        foreach (Coal dial in Coals)
        {
            Coal it = dial;
            _panel.Slide(it.Id, it.Label, it.Lo, it.Hi, it.Step,
                         () => Coaled(it), v => CoalTurn(it, v), it.Unit);
        }
    }
}
