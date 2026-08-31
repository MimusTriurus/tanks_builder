using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The effects bench's side panel: every number a burst is made of, with a name
/// on it.
///
/// <b>Grouped by kind of burst, and today there is one kind.</b> Every row here
/// is under <c>he.*</c> - the HE round in the ground - and its sub-groups are that
/// burst's own families: the event, the fire, the earth it throws, the collar
/// along the ground, the column over it, how the dust is shaded, the rings and the
/// mark left behind. <c>docs/blast.md</c> lists three more kinds that are this one
/// with other numbers; each arrives as its own top-level prefix (<c>water.*</c>,
/// <c>wall.*</c>), and nothing here has to move for that to happen.
///
/// <b>A view of the shaders' own uniforms, not a second copy of them.</b> A row's
/// opening value is read out of the compiled shader text
/// (<see cref="ProcBlast.Uniform"/>), never written down again here - the
/// arrangement the self-test already uses, and for the same reason: tuning happens
/// in the shader, so a copy would agree with it until the first edit landed in one
/// of them. <c>effects.json</c> carries names, notes and ranges and deliberately
/// carries no defaults at all.
///
/// <b>Built from a table rather than fifty calls.</b> One entry per dial, so a new
/// number is one line and cannot be added without a group, a range and a label -
/// and the self-test can walk the same table and ask the shaders whether every
/// name in it exists.
///
/// <b>The pool is reached through <see cref="Stage3D.Dress"/>.</b> Six bursts live
/// in the stage and they are built on the first shot, so the panel cannot hold a
/// reference to one: it holds the numbers and dresses whichever burst goes off.
/// That is also what makes a dial work before anything has been fired.
/// </summary>
public sealed partial class EffectsBench
{
    /// <summary>Where the panel's names and notes come from. Its own file beside
    /// the harness's <c>panel.json</c> and the tank bench's <c>bench.json</c>, by
    /// the rule those two already live under: the bench's rows are the bench's,
    /// and a shared file would be cross-checked against rows it has never
    /// heard of.</summary>
    private const string PanelFile = "effects.json";

    private ControlPanel? _panel;
    private PanelText _panelText = new();

    /// <summary>
    /// One dial: which shader owns it, what it is called there, and the band a
    /// slider may move it over.
    ///
    /// <c>Step</c> at or above one marks it a count, which is also how the panel
    /// decides to print it without decimals.
    /// </summary>
    /// <param name="Seed">What the row opens on when the shader's own default is
    /// deliberately not the truth. The frame's uniforms are declared at zero so a
    /// run that forgot to write one draws nothing rather than something plausible
    /// - see <see cref="ProcBlast.ReachDefault"/> - so a dial on one of those has
    /// to be told. Null everywhere else, which is nearly everywhere: the shader
    /// text is where a number lives.</param>
    private readonly record struct Dial(string Group, string Id, ProcBlast.Part Part,
                                        string Name, string Label,
                                        double Lo, double Hi, double Step,
                                        double? Seed = null);

    /// <summary>
    /// Every number worth turning while a burst is being judged.
    ///
    /// <b>Not every number there is.</b> The three shaders declare eighty
    /// uniforms between them; the plumbing among those - the clock, the level, the
    /// quad's own measurements, the sun - is written by C# every frame and a slider
    /// on it would be fighting the code. What is left is what an eye asks for.
    ///
    /// The quad's <c>Flank</c> and <c>Tall</c> are deliberately absent too: they
    /// follow the model, and the self-test is what keeps them following it. A
    /// slider that can clip the burst against its own quad is a slider that can
    /// make it look authored small.
    /// </summary>
    private static readonly Dial[] Dials =
    {
        // --- the event as a whole -------------------------------------------
        new("he.event", "he.event.reach", ProcBlast.Part.Frame, "reach",
            "throw apex", 0.10, 0.70, 0.01, ProcBlast.ReachDefault),
        new("he.event", "he.event.spread", ProcBlast.Part.Frame, "spread",
            "throw spread", 0.40, 2.20, 0.05),
        new("he.event", "he.event.root", ProcBlast.Part.Frame, "root",
            "seat above ground", 0.0, 0.12, 0.005),
        new("he.event", "he.event.fade", ProcBlast.Part.Frame, "floor_fade",
            "ground fade band", 0.0, 0.20, 0.005),

        // --- the fire -------------------------------------------------------
        new("he.fire", "he.fire.flash_size", ProcBlast.Part.Fire, "flash_size",
            "flash size", 0.01, 0.20, 0.005),
        new("he.fire", "he.fire.flash_gain", ProcBlast.Part.Fire, "flash_gain",
            "flash brightness", 0.0, 4.0, 0.05),
        new("he.fire", "he.fire.flash_life", ProcBlast.Part.Fire, "flash_life",
            "flash life", 0.01, 0.40, 0.005),
        new("he.fire", "he.fire.balls", ProcBlast.Part.Fire, "balls",
            "fireball bodies", 1, 16, 1),
        new("he.fire", "he.fire.ball_size", ProcBlast.Part.Fire, "ball_size",
            "fireball size", 0.005, 0.16, 0.002),
        new("he.fire", "he.fire.ball_climb", ProcBlast.Part.Fire, "ball_climb",
            "fireball climb", 0.0, 0.40, 0.01),
        new("he.fire", "he.fire.ball_gain", ProcBlast.Part.Fire, "ball_gain",
            "fireball brightness", 0.0, 2.5, 0.05),
        new("he.fire", "he.fire.ball_life", ProcBlast.Part.Fire, "ball_life",
            "fireball life", 0.05, 0.80, 0.01),
        new("he.fire", "he.fire.sparks", ProcBlast.Part.Fire, "sparks",
            "sparks", 0, 24, 1),
        new("he.fire", "he.fire.spark_size", ProcBlast.Part.Fire, "spark_size",
            "spark size", 0.002, 0.030, 0.001),
        new("he.fire", "he.fire.spark_stretch", ProcBlast.Part.Fire, "spark_stretch",
            "spark length", 1.0, 9.0, 0.2),
        new("he.fire", "he.fire.spark_gain", ProcBlast.Part.Fire, "spark_gain",
            "spark brightness", 0.0, 1.5, 0.05),
        new("he.fire", "he.fire.spark_reach", ProcBlast.Part.Fire, "spark_reach",
            "spark reach", 0.5, 2.5, 0.05),

        // --- the earth it throws --------------------------------------------
        new("he.earth", "he.earth.clods", ProcBlast.Part.Dust, "clods",
            "clods", 4, 72, 1),
        new("he.earth", "he.earth.clod_size", ProcBlast.Part.Dust, "clod_size",
            "clod size", 0.005, 0.070, 0.002),
        new("he.earth", "he.earth.clod_stretch", ProcBlast.Part.Dust, "clod_stretch",
            "clod stretch along flight", 0.0, 4.0, 0.05),
        new("he.earth", "he.earth.clod_ink", ProcBlast.Part.Dust, "clod_ink",
            "clod thickness", 0.0, 2.5, 0.05),
        new("he.earth", "he.earth.clod_life", ProcBlast.Part.Dust, "clod_life",
            "clod life", 0.10, 1.60, 0.02),
        new("he.earth", "he.earth.clod_narrow", ProcBlast.Part.Dust, "clod_narrow",
            "cone: tightest lean", 0.0, 1.20, 0.02),
        new("he.earth", "he.earth.clod_wide", ProcBlast.Part.Dust, "clod_wide",
            "cone: widest lean", 0.0, 1.40, 0.02),
        new("he.earth", "he.earth.clod_born", ProcBlast.Part.Dust, "clod_born",
            "cone: seat scatter", 0.0, 0.20, 0.005),

        // --- the collar along the ground ------------------------------------
        new("he.collar", "he.collar.count", ProcBlast.Part.Dust, "collar",
            "puffs", 0, 48, 1),
        new("he.collar", "he.collar.size", ProcBlast.Part.Dust, "collar_size",
            "puff size", 0.005, 0.090, 0.002),
        new("he.collar", "he.collar.flat", ProcBlast.Part.Dust, "collar_flat",
            "how flat they lie", 1.0, 4.0, 0.05),
        new("he.collar", "he.collar.spread", ProcBlast.Part.Dust, "collar_spread",
            "spread along ground", 0.0, 0.70, 0.01),
        new("he.collar", "he.collar.climb", ProcBlast.Part.Dust, "collar_climb",
            "climb", 0.0, 0.40, 0.01),
        new("he.collar", "he.collar.ink", ProcBlast.Part.Dust, "collar_ink",
            "thickness", 0.0, 2.0, 0.05),
        new("he.collar", "he.collar.life", ProcBlast.Part.Dust, "collar_life",
            "life", 0.10, 2.00, 0.05),

        // --- the column over it ---------------------------------------------
        new("he.column", "he.column.count", ProcBlast.Part.Dust, "column",
            "puffs", 0, 40, 1),
        new("he.column", "he.column.size", ProcBlast.Part.Dust, "column_size",
            "puff size", 0.005, 0.120, 0.002),
        new("he.column", "he.column.climb", ProcBlast.Part.Dust, "column_climb",
            "climb", 0.0, 1.00, 0.02),
        new("he.column", "he.column.sway", ProcBlast.Part.Dust, "column_sway",
            "sway", 0.0, 0.40, 0.01),
        new("he.column", "he.column.ink", ProcBlast.Part.Dust, "column_ink",
            "thickness", 0.0, 2.0, 0.05),
        new("he.column", "he.column.life", ProcBlast.Part.Dust, "column_life",
            "life", 0.20, 2.40, 0.05),
        new("he.column", "he.column.born", ProcBlast.Part.Dust, "column_born",
            "starts at", 0.0, 0.60, 0.01),

        // --- how the dust is shaded -----------------------------------------
        new("he.dust", "he.dust.soft", ProcBlast.Part.Dust, "dust_soft",
            "profile softness", 0.5, 5.0, 0.05),
        new("he.dust", "he.dust.clod_soft", ProcBlast.Part.Dust, "clod_soft",
            "clod profile softness", 0.5, 5.0, 0.05),
        new("he.dust", "he.dust.ink", ProcBlast.Part.Dust, "dust_ink",
            "most it may hide", 0.10, 1.0, 0.02),
        new("he.dust", "he.dust.pack", ProcBlast.Part.Dust, "dust_pack",
            "how fast it saturates", 0.20, 6.0, 0.10),
        new("he.dust", "he.dust.toe", ProcBlast.Part.Dust, "dust_toe",
            "toe on the way out", 0.60, 2.50, 0.05),
        new("he.dust", "he.dust.tear", ProcBlast.Part.Dust, "dust_tear",
            "how torn", 0.0, 0.90, 0.02),
        new("he.dust", "he.dust.grain", ProcBlast.Part.Dust, "dust_grain",
            "tear grain", 3.0, 40.0, 0.5),
        new("he.dust", "he.dust.drift", ProcBlast.Part.Dust, "dust_drift",
            "tear drift", 0.0, 0.60, 0.01),
        new("he.dust", "he.dust.steps", ProcBlast.Part.Dust, "dust_steps",
            "shading steps", 0, 8, 1),
        new("he.dust", "he.dust.band", ProcBlast.Part.Dust, "dust_band",
            "how much stepping is taken", 0.0, 1.0, 0.05),
        new("he.dust", "he.dust.seat", ProcBlast.Part.Dust, "dust_seat",
            "light seat", 0.0, 1.20, 0.02),
        new("he.dust", "he.dust.ridge", ProcBlast.Part.Dust, "dust_ridge",
            "sunward ridge", 0.0, 1.50, 0.02),
        new("he.dust", "he.dust.dense", ProcBlast.Part.Dust, "dust_dense",
            "how much thickness darkens", 0.0, 0.80, 0.02),

        // --- the rings on the ground ----------------------------------------
        new("he.ring", "he.ring.count", ProcBlast.Part.Ring, "rings",
            "rings", 0, 6, 1),
        new("he.ring", "he.ring.life", ProcBlast.Part.Ring, "ring_life",
            "life", 0.05, 1.20, 0.02),
        new("he.ring", "he.ring.stagger", ProcBlast.Part.Ring, "ring_stagger",
            "between rings", 0.0, 0.40, 0.01),
        new("he.ring", "he.ring.wide", ProcBlast.Part.Ring, "ring_wide",
            "band width", 0.02, 0.40, 0.005),
        new("he.ring", "he.ring.far", ProcBlast.Part.Ring, "ring_far",
            "how far out", 0.20, 0.95, 0.01),
        new("he.ring", "he.ring.gain", ProcBlast.Part.Ring, "ring_gain",
            "brightness", 0.0, 1.60, 0.02),
        new("he.ring", "he.ring.tear", ProcBlast.Part.Ring, "ring_tear",
            "how torn", 0.0, 1.0, 0.02),
        new("he.ring", "he.ring.disc_life", ProcBlast.Part.Ring, "disc_life",
            "ground flash life", 0.01, 0.40, 0.005),
        new("he.ring", "he.ring.disc_wide", ProcBlast.Part.Ring, "disc_wide",
            "ground flash size", 0.05, 0.95, 0.01),
        new("he.ring", "he.ring.disc_gain", ProcBlast.Part.Ring, "disc_gain",
            "ground flash brightness", 0.0, 2.5, 0.05),
    };

    /// <summary>The table as the self-test reads it: what each dial is called in
    /// which shader, the band it may move over and where it opens. Handed out
    /// rather than duplicated, so the check is of the panel that is built.
    /// </summary>
    internal static IEnumerable<(string Group, string Id, ProcBlast.Part Part,
                                 string Name, double Lo, double Hi, double Opens)>
        DialRows(EffectsBench bench) =>
        Dials.Select(d => (d.Group, d.Id, d.Part, d.Name, d.Lo, d.Hi,
                           (double)bench.Turned(d)));

    /// <summary>Which group the panel opens expanded - <c>--panel &lt;group&gt;</c>,
    /// so a shot of a group's rows does not need a hand on the mouse. The bench's
    /// own rule about captures, applied to the panel.</summary>
    private string? _openGroup;

    /// <summary>What every dial is set to, keyed the way <see cref="ProcBlast"/>
    /// keys them. Seeded from the shaders' own text the first time each is asked
    /// for, so this holds a number only once it has been moved or read.</summary>
    private readonly Dictionary<string, float> _turned = new();

    /// <summary>Whether a burst restarts whenever a dial moves. On, because a
    /// burst is an event: a slider dragged while nothing is in the air changes
    /// nothing anybody can see, and a tuning panel whose effect appears on the
    /// next keypress is a tuning panel nobody believes.</summary>
    private bool _refire = true;

    private float Turned(Dial dial)
    {
        string key = dial.Part + "." + dial.Name;
        if (!_turned.TryGetValue(key, out float now))
        {
            now = dial.Seed is double seeded
                ? (float)seeded
                : ProcBlast.Uniform(SourceOf(dial.Part), dial.Name);
            _turned[key] = now;
        }
        return now;
    }

    /// <summary>The compiled text a dial's default is read from. Mirrors
    /// <c>ProcBlast.Source</c>, which is private to it - and the pair is checked
    /// against each other by the self-test walking this very table.</summary>
    private static string SourceOf(ProcBlast.Part part) => part switch
    {
        ProcBlast.Part.Dust => ProcBlast.DustCode,
        ProcBlast.Part.Fire => ProcBlast.FireCode,
        ProcBlast.Part.Ring => ProcBlast.RingCode,
        _ => ProcBlast.FrameCode,
    };

    private void Turn(Dial dial, double value)
    {
        _turned[dial.Part + "." + dial.Name] = (float)value;
        if (_stage is not null)
            foreach (ProcBlast blast in _stage.Bursting)
                blast.Dial(dial.Part, dial.Name, (float)value);
        if (_refire)
            Burst();
    }

    /// <summary>Push every dial into one burst - the answer to
    /// <see cref="Stage3D.Dress"/>, so a burst built after a dial was moved opens
    /// on the moved value rather than on the shader's.</summary>
    private void Dress(ProcBlast blast)
    {
        foreach (Dial dial in Dials)
            blast.Dial(dial.Part, dial.Name, Turned(dial));
        blast.Life = _life;
        blast.Reach = Turned(Dials[0]);
        blast.RingReach = _ringReach;
        if (_field.Atlas is not null)
            blast.Replane(_field.Atlas.HexRect.Size.X);
        blast.Hold = _hold;
    }

    private float _life = ProcBlast.LifeDefault;
    private float _ringReach = ProcBlast.RingReachDefault;
    private bool _hold;
    private float _scrub;

    private void Panel(CanvasLayer layer)
    {
        _panelText = PanelText.Load(Path.Combine(
            ProjectSettings.GlobalizePath("res://"), PanelFile));
        GD.Print(_panelText.Loaded
            ? "effects: panel json"
            : $"effects: panel built-in ({PanelFile}: {_panelText.Error})");
        _panel = new ControlPanel { Text = _panelText };
        _panel.Prepare();

        // Whichever burst this scene is about, and only that one: two tables in
        // one panel is one table nobody is looking at - see EffectsBench.Sheet.
        if (Sheet)
            SheetPanel();
        else
            ComputedPanel();
        CraterPanel();

        layer.AddChild(_panel);
        _panel.AddHandle();
        // <b>Open, unlike every other bench's.</b> There the board is the subject
        // and the panel is a way to poke it; here the panel is half of what the
        // bench is for - a burst is a hundred numbers and the whole point of this
        // scene is turning them while looking at one. TAB still shuts it.
        _panel.Expand(Sheet ? "sheet" : "he", true);
        // Asked rather than toggled: the panel opens open (ControlPanel._open
        // starts true), so a flip here shut the one bench whose panel is half the
        // point of it.
        if (!_panel.Open)
            _panel.Flip();
        if (_openGroup is not null)
            _panel.Expand(_openGroup, true);
    }

    /// <summary>The computed burst's rows: its five event organs and its eight
    /// groups of uniforms.</summary>
    private void ComputedPanel()
    {
        if (_panel is null)
            return;
        _panel.Heading("he", "HE in the ground");
        _panel.Press("he.fire_now", "set one off", Burst);
        _panel.Toggle("he.refire", "restart on every change",
                      () => _refire, v => _refire = v);
        _panel.Toggle("he.hold", "hold the clock",
                      () => _hold, v =>
                      {
                          _hold = v;
                          foreach (ProcBlast blast in Live())
                              blast.Hold = v;
                      });
        _panel.Slide("he.scrub", "age held at", 0.0, 2.60, 0.02,
                     () => _scrub, v =>
                     {
                         _scrub = (float)v;
                         foreach (ProcBlast blast in Live())
                             blast.Clock = _scrub;
                     }, "s");
        _panel.Slide("he.life", "whole event", 0.40, 4.00, 0.05,
                     () => _life, v =>
                     {
                         _life = (float)v;
                         foreach (ProcBlast blast in Live())
                             blast.Life = _life;
                         if (_refire)
                             Burst();
                     }, "s");
        _panel.Slide("he.ring_reach", "ring plane", 0.15, 0.90, 0.01,
                     () => _ringReach, v =>
                     {
                         _ringReach = (float)v;
                         foreach (ProcBlast blast in Live())
                         {
                             blast.RingReach = _ringReach;
                             if (_field.Atlas is not null)
                                 blast.Replane(_field.Atlas.HexRect.Size.X);
                         }
                         if (_refire)
                             Burst();
                     });
        _panel.Readout("he.state", () =>
        {
            ProcBlast? one = Live().FirstOrDefault(b => b.Alive);
            return one is null
                ? $"nothing in the air, {_pits.Pits.Count} crater(s)"
                : $"{one.Age:F2}s of {one.Life:F2}, {_pits.Pits.Count} crater(s)";
        });

        string group = "";
        foreach (Dial dial in Dials)
        {
            if (dial.Group != group)
            {
                group = dial.Group;
                _panel.Heading(group, group.Split('.').Last());
            }
            Dial it = dial;
            _panel.Slide(it.Id, it.Label, it.Lo, it.Hi, it.Step,
                         () => Turned(it), v => Turn(it, v));
        }
    }

    /// <summary>
    /// What the ground keeps, which is the same group under either burst.
    ///
    /// <b>The crater is the board's rather than the burst's</b> - see
    /// <see cref="Craters"/> on the split - so it is neither effect's group, and
    /// both scenes get it. Its rows keep the <c>he.</c> prefix they were written
    /// with: the ids are what <c>effects.json</c> names them by, and renaming a
    /// row to say it is shared would lose it its label.
    /// </summary>
    private void CraterPanel()
    {
        if (_panel is null)
            return;
        _panel.Heading("he.crater", "the mark it leaves");
        _panel.Slide("he.crater.wide", "width", 0.02, 0.50, 0.01,
                     () => _pits.Wide, v => _pits.Wide = (float)v);
        _panel.Slide("he.crater.ink", "darkness", 0.0, 1.0, 0.02,
                     () => _pits.Ink, v => _pits.Ink = (float)v);
        _panel.Press("he.crater.fill", "fill them in", () => _pits.Fill());
    }

    /// <summary>The bursts that exist, or none before the first shot.</summary>
    private IEnumerable<ProcBlast> Live() =>
        _stage?.Bursting ?? Array.Empty<ProcBlast>();
}
