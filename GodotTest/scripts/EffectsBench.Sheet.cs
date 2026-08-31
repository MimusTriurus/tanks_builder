using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The imported burst's half of the bench: its own scene, its own key, its own
/// group of dials.
///
/// <b>Why it is a second scene rather than a second setting of the first.</b>
/// <c>Effects.tscn</c> judges <see cref="ProcBlast"/>, which computes its shape;
/// <c>Boom.tscn</c> judges <see cref="SheetBlast"/>, which plays sixty-four
/// rendered frames of one. The two are made of different things and are tuned by
/// different numbers - thirteen model fields and nine uniforms here against
/// sixty-two uniforms there - so one panel showing both would be one panel
/// showing whichever half is not being looked at. Everything under the two is
/// shared: the same board, the same aim, the same crater, the same stage,
/// the same <see cref="Stage3D.Boom"/> call the harness will make.
///
/// <b>Both pools live in the stage at once and that is on purpose</b> - see
/// <see cref="Stage3D.Boom"/>. The comparison worth having is the two bursts on
/// one board at one frame, and a flag that swapped one for the other would make
/// that a comparison against a memory.
/// </summary>
public sealed partial class EffectsBench
{
    /// <summary>Which burst this scene is about: the flipbook one when set, the
    /// computed one otherwise. An export so <c>Boom.tscn</c> is the same script
    /// with one field turned on rather than a second copy of this bench, and a
    /// flag as well (<c>--sheet</c>) so a capture of either can be taken off
    /// either scene.</summary>
    [Export] public bool Sheet;

    /// <summary>
    /// One dial of the imported burst: which half of it owns the number.
    ///
    /// <b>Two halves rather than one, because this effect genuinely has two.</b>
    /// The model is in C# - where the puffs are, when they are born, how fast they
    /// slow down - and the look is in the shader, and a dial has to know which,
    /// because writing a model number into a material is silent and writing a
    /// uniform into the model is a warning. <see cref="ProcBlast"/> needed no such
    /// distinction: every one of its sixty-two numbers is a uniform.
    /// </summary>
    private readonly record struct Puff(string Group, string Id, bool Shader,
                                        string Name, string Label,
                                        double Lo, double Hi, double Step);

    /// <summary>
    /// Every number the flipbook burst is made of.
    ///
    /// Twenty-two of them against the computed burst's sixty-two, and the ratio is
    /// the whole argument for the import: the shape does not need describing,
    /// because it was simulated once and rendered into sixty-four frames. What is
    /// left to say is how many puffs there are, where they go, and how they are
    /// lit.
    /// </summary>
    private static readonly Puff[] Puffs =
    {
        // --- the cloud: how many, where they go -----------------------------
        new("sheet.cloud", "sheet.cloud.puffs", false, "puffs",
            "puffs in the cloud", 1, 48, 1),
        new("sheet.cloud", "sheet.cloud.trails", false, "trails",
            "puffs arriving after", 0, 24, 1),
        new("sheet.cloud", "sheet.cloud.reach", false, "reach",
            "how far they get", 0.02, 0.80, 0.01),
        new("sheet.cloud", "sheet.cloud.size", false, "size",
            "one puff, at birth", 0.02, 0.50, 0.005),
        new("sheet.cloud", "sheet.cloud.grow", false, "grow",
            "growth on top of the sheet", 0.50, 3.00, 0.05),
        new("sheet.cloud", "sheet.cloud.climb", false, "climb",
            "how far the cloud rises", 0.0, 0.80, 0.01),
        new("sheet.cloud", "sheet.cloud.seat", false, "seat",
            "seat above the ground", 0.0, 0.40, 0.005),
        new("sheet.cloud", "sheet.cloud.flat", false, "flat",
            "disc against column", 0.0, 1.0, 0.02),
        new("sheet.cloud", "sheet.cloud.slow", false, "slow",
            "how much they slow down", 0.0, 1.0, 0.02),
        new("sheet.cloud", "sheet.cloud.stagger", false, "stagger",
            "disagreement about leaving", 0.0, 0.60, 0.01),
        new("sheet.cloud", "sheet.cloud.puff_life", false, "puff_life",
            "one puff's share of the event", 0.20, 1.0, 0.02),
        new("sheet.cloud", "sheet.cloud.start", false, "start",
            "born this far into the sheet", 0.0, 0.60, 0.01),
        new("sheet.cloud", "sheet.cloud.seed", false, "seed",
            "which cloud", 1, 64, 1),

        // --- the spikes, which are what makes it thrown earth ---------------
        new("sheet.throw", "sheet.throw.lances", false, "lances",
            "spikes thrown out of it", 0, 64, 1),
        new("sheet.throw", "sheet.throw.fast", false, "lance_fast",
            "how much faster a spike is", 1.0, 4.0, 0.05),
        new("sheet.throw", "sheet.throw.thin", false, "lance_thin",
            "how much thinner a spike is", 0.10, 1.0, 0.02),
        new("sheet.throw", "sheet.throw.stretch", false, "stretch",
            "drawn out along its flight", 1.0, 6.0, 0.05),
        new("sheet.throw", "sheet.throw.rising", false, "rising",
            "how fast the rise is spent", 0.01, 0.60, 0.005),
        new("sheet.throw", "sheet.throw.settle", false, "settle",
            "how far the dust sags back", 0.0, 0.60, 0.01),
        new("sheet.throw", "sheet.throw.fall", false, "fall",
            "how far the earth falls back", 0.05, 1.40, 0.02),

        // --- the sheet: how one puff is drawn -------------------------------
        new("sheet.look", "sheet.look.ink", true, "puff_ink",
            "most one puff may hide", 0.10, 1.0, 0.02),
        new("sheet.look", "sheet.look.spin", true, "spin",
            "how much the frames are turned", 0.0, 1.0, 0.02),
        new("sheet.look", "sheet.look.still", true, "still",
            "every puff held on frame", -1, 63, 1),
        new("sheet.look", "sheet.look.root", true, "root",
            "how far below the seat it survives", 0.0, 30.0, 0.5),
        new("sheet.look", "sheet.look.fade", true, "floor_fade",
            "ground fade band", 1.0, 90.0, 1.0),

        // --- the flame and the light ----------------------------------------
        new("sheet.fire", "sheet.fire.gain", true, "flame_gain",
            "flame brightness", 0.0, 5.0, 0.05),
        new("sheet.fire", "sheet.fire.shift", true, "flame_shift",
            "how fast the flame walks its ramp", 0.0, 1.0, 0.02),
        new("sheet.fire", "sheet.fire.out", true, "flame_out",
            "flame out by", 0.02, 1.0, 0.02),
        new("sheet.fire", "sheet.fire.low", true, "flame_low",
            "fire survives this far up", 4.0, 200.0, 2.0),
        new("sheet.fire", "sheet.fire.pale", true, "pale",
            "how much paler the head is", 0.0, 1.0, 0.02),
        new("sheet.fire", "sheet.fire.pale_high", true, "pale_high",
            "over how much height", 20.0, 400.0, 5.0),
        new("sheet.fire", "sheet.fire.lit_seat", true, "lit_seat",
            "light before the sun", 0.0, 1.0, 0.02),
        new("sheet.fire", "sheet.fire.lit_gain", true, "lit_gain",
            "what the sun is worth", 0.0, 1.5, 0.02),
    };

    /// <summary>The sheet table as the self-test reads it - <see cref="DialRows"/>
    /// for the other burst, handed out for its reason.</summary>
    internal static IEnumerable<(string Group, string Id, bool Shader, string Name,
                                 double Lo, double Hi, double Opens)>
        PuffRows(EffectsBench bench) =>
        Puffs.Select(p => (p.Group, p.Id, p.Shader, p.Name, p.Lo, p.Hi,
                           (double)bench.Puffed(p)));

    private readonly Dictionary<string, float> _puffed = new();

    /// <summary>
    /// What a sheet dial is set to.
    ///
    /// Read once from whichever half owns it and kept after that, exactly as the
    /// other burst's dials are - and the shader half is read out of the shader's
    /// own text, so the panel is a view of the numbers rather than a second copy
    /// of them. The model half is read off a burst that is built on demand, for
    /// the same reason the other panel needs <see cref="Stage3D.Dress"/>: the pool
    /// does not exist until the first shot and a dial must work before it.
    /// </summary>
    private float Puffed(Puff dial)
    {
        string key = (dial.Shader ? "ink." : "model.") + dial.Name;
        if (!_puffed.TryGetValue(key, out float now))
        {
            now = dial.Shader
                ? ProcBlast.Uniform(SheetBlast.Code, dial.Name)
                : Fresh.Model(dial.Name);
            _puffed[key] = now;
        }
        return now;
    }

    /// <summary>
    /// A burst nobody fires, kept only to be asked what its model numbers open on.
    ///
    /// <b>The model's defaults live on the class as fields, and a field cannot be
    /// read out of a text the way a uniform can.</b> So one is built - never added
    /// to the tree, never ticked, never drawn - and asked. Cheaper than a second
    /// copy of thirteen defaults, and it cannot fall out of step with them.
    /// </summary>
    private static readonly SheetBlast Fresh = new();

    private void PuffTurn(Puff dial, double value)
    {
        _puffed[(dial.Shader ? "ink." : "model.") + dial.Name] = (float)value;
        foreach (SheetBlast blast in Booming())
        {
            if (dial.Shader)
                blast.Dial(dial.Name, (float)value);
            else
                blast.Model(dial.Name, (float)value);
        }
        if (_refire)
            Burst();
    }

    /// <summary>Push every sheet dial into one burst - the answer to
    /// <see cref="Stage3D.Attire"/>, <see cref="Dress"/>'s twin.</summary>
    private void Attire(SheetBlast blast)
    {
        foreach (Puff dial in Puffs)
        {
            if (dial.Shader)
                blast.Dial(dial.Name, Puffed(dial));
            else
                blast.Model(dial.Name, Puffed(dial));
        }
        blast.Life = _puffLife;
        blast.Might = _might;
        blast.Hold = _hold;
    }

    private float _puffLife = SheetBlast.LifeDefault;

    /// <summary>Whether the computed burst goes off under the imported one, so the
    /// two can be seen composed rather than compared - see
    /// <see cref="Burst"/>. Off, because what this scene judges is the import.
    /// </summary>
    private bool _pair;

    /// <summary>The sheet bursts that exist, or none before the first shot.</summary>
    private IEnumerable<SheetBlast> Booming() =>
        _stage?.Booming ?? Array.Empty<SheetBlast>();

    /// <summary>
    /// The imported burst's side of the panel.
    ///
    /// Same five organs at the top as the computed burst's - fire, refire, hold,
    /// scrub, readout - because those are properties of an event rather than of
    /// one effect, and then its own three groups. The crater group is the board's
    /// and is built by the caller for both.
    /// </summary>
    private void SheetPanel()
    {
        if (_panel is null)
            return;
        _panel.Heading("sheet", "the imported burst");
        _panel.Press("sheet.fire_now", "set one off", Burst);
        _panel.Toggle("sheet.pair", "with the computed burst under it",
                      () => _pair, v =>
                      {
                          _pair = v;
                          if (_refire)
                              Burst();
                      });
        _panel.Toggle("sheet.refire", "restart on every change",
                      () => _refire, v => _refire = v);
        _panel.Toggle("sheet.hold", "hold the clock",
                      () => _hold, v =>
                      {
                          _hold = v;
                          foreach (SheetBlast blast in Booming())
                              blast.Hold = v;
                      });
        _panel.Slide("sheet.scrub", "age held at", 0.0, 2.60, 0.02,
                     () => _scrub, v =>
                     {
                         _scrub = (float)v;
                         foreach (SheetBlast blast in Booming())
                             blast.Clock = _scrub;
                     }, "s");
        _panel.Slide("sheet.might", "how big this burst is", 0.25, 3.00, 0.05,
                     () => _might, v =>
                     {
                         _might = (float)v;
                         foreach (SheetBlast blast in Booming())
                             blast.Might = _might;
                         if (_refire)
                             Burst();
                     }, "x");
        _panel.Slide("sheet.life", "whole event", 0.30, 4.00, 0.05,
                     () => _puffLife, v =>
                     {
                         _puffLife = (float)v;
                         foreach (SheetBlast blast in Booming())
                             blast.Life = _puffLife;
                         if (_refire)
                             Burst();
                     }, "s");
        _panel.Readout("sheet.state", () =>
        {
            SheetBlast? one = Booming().FirstOrDefault(b => b.Alive);
            return one is null
                ? $"nothing in the air, {_pits.Pits.Count} crater(s)"
                : $"{one.Age:F2}s of {one.Life:F2}, frame "
                  + $"{Mathf.FloorToInt(64.0f * Mathf.Clamp(one.Age / Mathf.Max(one.Life, 1e-3f), 0.0f, 0.999f))}"
                  + $", {_pits.Pits.Count} crater(s)";
        });

        string group = "";
        foreach (Puff dial in Puffs)
        {
            if (dial.Group != group)
            {
                group = dial.Group;
                _panel.Heading(group, group.Split('.').Last());
            }
            Puff it = dial;
            _panel.Slide(it.Id, it.Label, it.Lo, it.Hi, it.Step,
                         () => Puffed(it), v => PuffTurn(it, v));
        }
    }
}
