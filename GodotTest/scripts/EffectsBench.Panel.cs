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
/// <b>The pool is reached through <see cref="Stage3D.Attire"/>.</b> Six bursts
/// live in the stage and they are built on the first shot, so the panel cannot
/// hold a reference to one: it holds the numbers and dresses whichever burst goes
/// off. That is also what makes a dial work before anything has been fired.
///
/// <b>This file used to be the computed burst's own table - sixty-two dials in
/// eight groups - and that is what came off with <c>BlastSource.Built</c>.</b> What
/// is left of it is the shell every panel here needs and the crater's twelve,
/// because a crater is the board's and outlives whichever burst dug it.
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

    /// <summary>Which group the panel opens expanded - <c>--panel &lt;group&gt;</c>,
    /// so a shot of a group's rows does not need a hand on the mouse. The bench's
    /// own rule about captures, applied to the panel.</summary>
    private string? _openGroup;

    /// <summary>Whether a burst restarts whenever a dial moves. On, because a
    /// burst is an event: a slider dragged while nothing is in the air changes
    /// nothing anybody can see, and a tuning panel whose effect appears on the
    /// next keypress is a tuning panel nobody believes.</summary>
    private bool _refire = true;

    /// <summary>How big a burst this bench sets off, as a multiple of the tuned
    /// one - the dial <see cref="SheetBlast.Might"/> exists for. Shared by both
    /// benches because it is a property of the shot rather than of either
    /// effect.</summary>
    private float _might = 1.0f;

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

        // The burst, then the mark it leaves: one table each, and the second is
        // the board's rather than the burst's - see CraterPanel.
        SheetPanel();
        CraterPanel();

        layer.AddChild(_panel);
        _panel.AddHandle();
        // <b>Open, unlike every other bench's.</b> There the board is the subject
        // and the panel is a way to poke it; here the panel is half of what the
        // bench is for - a burst is a hundred numbers and the whole point of this
        // scene is turning them while looking at one. TAB still shuts it.
        _panel.Expand("sheet", true);
        // Asked rather than toggled: the panel opens open (ControlPanel._open
        // starts true), so a flip here shut the one bench whose panel is half the
        // point of it.
        if (!_panel.Open)
            _panel.Flip();
        if (_openGroup is not null)
            _panel.Expand(_openGroup, true);
    }

    /// <summary>
    /// The crater's own numbers: how the mark is drawn rather than how big it is.
    ///
    /// A second table beside the burst's, and flat rather than keyed by a part,
    /// because a crater is one shader. Read out of its text like the others, so the
    /// panel is a view of the numbers and not a second copy of them.
    /// </summary>
    private static readonly (string Id, string Name, string Label,
                             double Lo, double Hi, double Step)[] Scars =
    {
        ("he.crater.bowl", "bowl", "how much of it is the hole", 0.15, 0.85, 0.01),
        ("he.crater.deep", "deep", "how dark the bottom is", 0.0, 1.0, 0.02),
        ("he.crater.wall", "wall", "light on the far inner wall", 0.0, 1.50, 0.02),
        ("he.crater.lip", "lip", "the lit rim above the ground", 0.0, 1.0, 0.02),
        ("he.crater.rag", "rag", "how ragged the outline is", 0.0, 0.70, 0.02),
        ("he.crater.fingers", "fingers", "fingers of thrown soil", 1.0, 20.0, 0.5),
        ("he.crater.streaks", "streaks", "streaks converging on the hole", 4.0, 60.0, 1.0),
        ("he.crater.apron", "apron", "how much soil is thrown out", 0.0, 1.20, 0.02),
        ("he.crater.spatter", "spatter", "how far the spatter carries", 0.0, 1.20, 0.02),
        ("he.crater.grit", "grit", "gravel in the soil", 0.0, 1.0, 0.02),
        ("he.crater.grain", "grain", "how big that grain is on screen", 0.8, 12.0, 0.1),
        ("he.crater.soft", "soft", "how softly the layers meet", 0.0, 1.0, 0.02),
    };

    /// <summary>The crater table as the self-test reads it, the burst table's
    /// arrangement.</summary>
    internal static IEnumerable<(string Id, string Name, double Lo, double Hi,
                                 double Opens)>
        ScarRows(EffectsBench bench) =>
        Scars.Select(r => (r.Id, r.Name, r.Lo, r.Hi, (double)bench.Scarred(r.Name)));

    private readonly Dictionary<string, float> _scarred = new();

    private float Scarred(string name)
    {
        if (!_scarred.TryGetValue(name, out float now))
        {
            now = ProcBlast.Uniform(PitArt.Code, name);
            _scarred[name] = now;
        }
        return now;
    }

    /// <summary>Push every crater number into one crater's art - the answer to
    /// <see cref="Stage3D.Scar"/>, <see cref="Attire"/>'s twin.</summary>
    private void Scar(PitArt art)
    {
        foreach ((string _, string name, string _, double _, double _, double _) in Scars)
            art.Dial(name, Scarred(name));
    }

    /// <summary>
    /// What the ground keeps, which is the same group under either burst.
    ///
    /// <b>The crater is the board's rather than the burst's</b> - see
    /// <see cref="Craters"/> on the split - so it is its own group. Its rows keep
    /// the <c>he.</c> prefix they were written with, from when there were two
    /// bursts and this was the group they shared: the ids are what
    /// <c>effects.json</c> names them by, and renaming a row would lose it its
    /// label.
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
        foreach (var scar in Scars)
        {
            var it = scar;
            _panel.Slide(it.Id, it.Label, it.Lo, it.Hi, it.Step,
                         () => Scarred(it.Name),
                         v =>
                         {
                             _scarred[it.Name] = (float)v;
                             foreach (PitArt art in _stage?.Etched
                                                    ?? Array.Empty<PitArt>())
                                 art.Dial(it.Name, (float)v);
                         });
        }
    }
}
