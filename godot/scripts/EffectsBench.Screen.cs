using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The smoke screen's half of the effects bench: <see cref="ProcScreen"/>, laid on
/// the aimed cell, with its dials.
///
/// <b>The one group here with buttons on both ends of a life.</b> Every other
/// effect on this bench is set off and then watched: one press, one event, and the
/// picture is a function of seconds. A screen is laid, held for as long as the
/// rules hold it, and then taken away one of two ways - so the group has three
/// presses (lay, lift, blow through) and the two clearing lengths behind them, and
/// that shape is the finding rather than the convenience. Judging it needs the hold
/// to be a hold: a screen that thinned out while nobody pressed anything would be
/// the bug F4 exists to avoid, and here it is visible as a slider that does nothing.
///
/// <b>Two tables in one, like the imported burst's.</b> Some of a screen's numbers
/// are uniforms of <see cref="SheetBlast.Puffing"/> - the sheet is shared, so those
/// are the same names the burst turns - and the rest are fields of the model, which
/// is where the placement and the whole life live. A row can therefore miss in two
/// ways, and both come back NaN; the self test looks for exactly that.
/// </summary>
public sealed partial class EffectsBench
{
    /// <summary>One dial of the screen: a uniform of the puff shader, or a number of
    /// <see cref="ProcScreen"/>'s own model.</summary>
    private readonly record struct Veil(string Id, string Name, string Label,
                                        double Lo, double Hi, double Step,
                                        bool Shader, string Unit = "");

    private static readonly Veil[] Veils =
    {
        // The model: where the puffs go, and how the playhead is held.
        new("screen.puffs", "puffs", "how many puffs", 4, 48, 1, false),
        new("screen.spread", "spread", "scattered out to, in radii", 0.1, 1.2, 0.02, false),
        new("screen.heart", "heart", "the cloud's heart sits at", 0.1, 1.2, 0.02, false),
        new("screen.deep", "deep", "and they scatter about it by", 0.0, 0.8, 0.02, false),
        new("screen.size", "size", "one puff is, in radii", 0.15, 1.4, 0.02, false),
        new("screen.grow", "grow", "and grows out of the grenade by", 1.0, 4.0, 0.05, false),
        new("screen.churn", "churn", "a puff lives", 0.6, 12.0, 0.1, false, "s"),
        new("screen.bustle", "bustle", "turning over, while it fills", 0.2, 6.0, 0.05, false, "x"),
        new("screen.calm", "calm", "and once it just hangs", 0.0, 2.0, 0.02, false, "x"),
        new("screen.settle", "settle", "slowing down over", 0.1, 8.0, 0.1, false, "s"),
        new("screen.roll", "roll", "and rolls, in radii", 0.0, 0.8, 0.02, false),
        new("screen.blink", "blink", "easing in and out over", 0.02, 0.45, 0.01, false),
        new("screen.rise", "rise", "fills in", 0.1, 3.0, 0.05, false, "s"),
        new("screen.fade", "fade", "clears in, the round up", 0.3, 6.0, 0.05, false, "s"),
        new("screen.gust", "gust", "clears in, a hull through", 0.2, 3.0, 0.05, false, "s"),
        new("screen.drift", "drift", "leans, in radii", 0.0, 0.8, 0.01, false),
        new("screen.bearing", "bearing", "and leans toward", 0.0, 359.0, 5.0, false),
        // The sheet: the same uniforms the imported burst turns, on this cloud.
        new("screen.ink", "puff_ink", "how much of the sheet is drawn", 0.1, 1.0, 0.02, true),
        new("screen.ease", "sheet_ease", "how the playhead runs", 0.2, 2.0, 0.02, true),
        new("screen.blend", "frame_blend", "crossfading the sheet's cells", 0.0, 1.0, 0.05, true),
        new("screen.flash", "flame_gain", "the grenade's flash", 0.0, 5.0, 0.05, true),
        new("screen.flash_out", "flame_out", "and it is out by", 0.0, 1.2, 0.02, true, "s"),
        new("screen.pale", "pale", "how much paler the top is", 0.0, 1.0, 0.02, true),
        new("screen.lit_seat", "lit_seat", "light before the sun", 0.0, 1.0, 0.02, true),
        new("screen.lit_gain", "lit_gain", "and what the sun is worth", 0.0, 1.5, 0.02, true),
    };

    /// <summary>The screen's dials as the self test reads them: id, name, whether it
    /// is a uniform, band, and the value it opens on - <c>PuffRows</c>'
    /// arrangement.</summary>
    internal static IEnumerable<(string Id, string Name, bool Shader, double Lo,
                                 double Hi, double Opens)>
        ScreenRows(EffectsBench bench) =>
        Veils.Select(v => (v.Id, v.Name, v.Shader, v.Lo, v.Hi, bench.Veiled(v)));

    /// <summary>The screen's rows that are not dials at all: the three presses, the
    /// hold and the readout. For the self test's cross-check of
    /// <c>effects.json</c>.</summary>
    internal static readonly string[] ScreenOwn =
    {
        "screen.lay", "screen.lift", "screen.blow", "screen.hold", "screen.state",
    };

    private readonly Dictionary<string, float> _veiledInk = new();
    private readonly Dictionary<string, float> _veiledModel = new();
    private bool _veilNow;
    private bool _veilHold;

    /// <summary>An unbuilt screen, only so the panel can read the model's defaults
    /// off it before any grenade has gone off - the stage's pool has no entries until
    /// the first one does, and a panel that opened on zeroes would be a panel lying
    /// about the effect.</summary>
    private readonly ProcScreen _screenModel = new();

    /// <summary>Seconds after <c>--screen</c> at which the screen is taken off again
    /// - <c>--screen-off</c>, and <c>--screen-gust</c> for which of the two ends it
    /// is. Null when nobody asked, which is the interactive case.
    ///
    /// <b>Its own flag rather than a frame of <c>--capture-at</c>.</b> Every other
    /// effect on this bench ends by itself, so naming a frame is naming an age; a
    /// screen ends because something happened to it, and a capture of the clearing
    /// has to say what.</summary>
    private float? _veilOffAt;
    private bool _veilGustOff;
    private float _veilClock;

    /// <summary>Count down to that second event, if one was asked for. Only the
    /// flag's path goes through here - a press does it at once.</summary>
    private void ScreenTick(double delta)
    {
        if (_veilOffAt is not float off || _stage is null)
            return;
        _veilClock += (float)delta;
        if (_veilClock < off)
            return;
        _veilOffAt = null;
        LiftScreen(_veilGustOff);
    }

    private IEnumerable<ProcScreen> Screening() =>
        _stage?.Screening ?? Array.Empty<ProcScreen>();

    /// <summary>Lay a screen on the aimed cell, through the stage's own pool -
    /// <c>Burst</c>'s rule.</summary>
    private void LayScreen() => _stage?.Screen(_at);

    /// <summary>The rules take it away. Both ends here rather than only the ordinary
    /// one: the fast clearing is a different picture, not the same one hurried, and a
    /// bench that could only show the slow one would be judging half the
    /// effect.</summary>
    private void LiftScreen(bool gust) => _stage?.Unscreen(_at, gust);

    /// <summary>Push every screen dial into a new screen - the answer to
    /// <see cref="Stage3D.Veil"/>, <c>Attire</c>'s twin.</summary>
    private void Dress(ProcScreen veil)
    {
        veil.Hold = _veilHold;
        foreach ((string name, float value) in _veiledModel)
            veil.Model(name, value);
        foreach ((string name, float value) in _veiledInk)
            veil.Dial(name, value);
    }

    /// <summary>What a dial shows: what was turned, else the shader's or the model's
    /// own default; NaN when neither has the name, which is what the self test looks
    /// for.</summary>
    private double Veiled(Veil dial)
    {
        var held = dial.Shader ? _veiledInk : _veiledModel;
        if (held.TryGetValue(dial.Name, out float now))
            return now;
        return dial.Shader ? ProcScreen.Declared(dial.Name)
                           : _screenModel.Model(dial.Name);
    }

    private void VeilTurn(Veil dial, double value)
    {
        if (dial.Shader)
        {
            _veiledInk[dial.Name] = (float)value;
            foreach (ProcScreen veil in Screening())
                veil.Dial(dial.Name, (float)value);
        }
        else
        {
            _veiledModel[dial.Name] = (float)value;
            _screenModel.Model(dial.Name, (float)value);
            foreach (ProcScreen veil in Screening())
                veil.Model(dial.Name, (float)value);
        }
    }

    private void ScreenPanel()
    {
        if (_panel is null)
            return;
        _panel.Heading("screen", "the smoke screen");
        _panel.Press("screen.lay", "lay one  (S)", LayScreen);
        _panel.Press("screen.lift", "the round is up  (D)", () => LiftScreen(false));
        _panel.Press("screen.blow", "a hull goes through  (shift D)",
                     () => LiftScreen(true));
        _panel.Toggle("screen.hold", "hold the life", () => _veilHold, v =>
        {
            _veilHold = v;
            foreach (ProcScreen veil in Screening())
                veil.Hold = v;
        });
        _panel.Readout("screen.state", () =>
        {
            var up = Screening().Where(s => s.Alive).ToList();
            return up.Count == 0 ? "no screen standing"
                                 : string.Join("\n", up.Select(s => s.Note));
        });
        foreach (Veil dial in Veils)
        {
            Veil it = dial;
            _panel.Slide(it.Id, it.Label, it.Lo, it.Hi, it.Step,
                         () => Veiled(it), v => VeilTurn(it, v), it.Unit);
        }
    }
}
