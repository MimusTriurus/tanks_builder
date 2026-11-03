using System;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The game's one tip (author 2026-11-03, step 3 of the theme): the hangar's and
/// the lobby's pads, the match's act buttons and corner buttons, the roster's icons
/// and the placement's buttons all show theirs through this, so a tip waits as long,
/// fades in alike, wears one surface and rim, and says what it tells of the same
/// way everywhere:
/// <list type="bullet">
/// <item><b>When</b> - after the pointer rests <see cref="Wait"/>; for
/// <see cref="Warm"/> after one goes, the next comes at once; fades in over
/// <see cref="Fade"/>. <see cref="Hold"/> - a script's - shows at once.</item>
/// <item><b>Where</b> - beside what it tells of (<see cref="Prefer.Beside"/>: right,
/// else left, else under or over) or over it (<see cref="Prefer.Above"/>), inside the
/// screen; a beak on the side toward it, level with its middle.</item>
/// <item><b>Which</b> - what it tells of ringed in <see cref="Ui.TipRim"/>, the tip's
/// rim colour, as long as it is up: by the thing itself when it can draw its own
/// shape (a <c>QuickPad</c>'s <c>Mark</c>), else a square ring drawn here.</item>
/// </list>
/// A unit on the board is not an interface element: its card stands off the hull on a
/// leader (<c>Tank3DBench.Peek</c>), in the same surface and with the same timing.
/// </summary>
public sealed partial class UiTip : Control
{
    /// <summary>How long the pointer rests before the first tip, s (the common 0.4-0.5).</summary>
    public const float Wait = 0.45f;

    /// <summary>After a tip goes, how long the next comes at once, ms.</summary>
    public const ulong Warm = 400;

    /// <summary>The fade in, s.</summary>
    public const float Fade = 0.12f;

    /// <summary>The meta that stands a tip beside this ancestor's box rather than its
    /// pad's (a machine's card, the hangar's tree): the panel the player is about to
    /// click stays open.</summary>
    public static readonly StringName Beside = "ui_tip_beside";

    public enum Prefer { Beside, Above }

    private enum Edge { None, Left, Right, Top, Bottom }

    /// <summary>A script's tips: shown at once, no fade (<c>--quick-do tip:</c>,
    /// <c>--do tip=</c>).</summary>
    public bool Hold;

    private PanelContainer? _box;
    private object? _for;
    private Func<Rect2>? _target, _near;
    private Func<Control>? _build;
    private Prefer _prefer;
    private Action<bool>? _mark;
    private bool _ring;
    private int _seq;
    private ulong _warmTill;
    private float _up;
    private Edge _beak;
    private float _beakAt;

    public UiTip()
    {
        Name = "Tip";
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        ZIndex = 10;
        SetProcess(false);
    }

    /// <summary>A tip is up (not only waiting).</summary>
    public bool Showing => _box is not null;

    /// <summary>What the tip shown or waiting tells of.</summary>
    public object? For => _for;

    /// <summary>The tip of <paramref name="key"/>: <paramref name="target"/> its rect on
    /// the screen, <paramref name="build"/> its body, built when it shows (and again on
    /// <see cref="Renew"/>). <paramref name="near"/> - the rect to stand by, when not the
    /// target's; <paramref name="mark"/> - the target rings itself (true on, false off),
    /// else <paramref name="ring"/> draws a square ring round it here.</summary>
    public async void Show(object key, Func<Rect2> target, Func<Control> build, Prefer prefer = Prefer.Beside,
                           Func<Rect2>? near = null, Action<bool>? mark = null, bool ring = true)
    {
        if (ReferenceEquals(key, _for) || Equals(key, _for))
            return;
        Hide(null);
        int seq = ++_seq;
        (_for, _target, _build, _prefer, _near, _mark, _ring) = (key, target, build, prefer, near, mark, ring);
        if (!Hold && Time.GetTicksMsec() > _warmTill)
        {
            await ToSignal(GetTree().CreateTimer(Wait), SceneTreeTimer.SignalName.Timeout);
            if (seq != _seq || !IsInsideTree())
                return;
        }
        var box = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, Modulate = new Color(1, 1, 1, 0) };
        box.AddThemeStyleboxOverride("panel", Ui.TipStyle);
        box.Draw += () =>
        {
            Ui.DrawRim(box, box.Size, Ui.TipRim, 1.0f);
            DrawBeak(box);
        };
        box.AddChild(build());
        AddChild(box);
        _box = box;
        // a wrapped label first measures itself at no width - a word a line, the
        // screen's height - and settles only once laid out; a container never shrinks
        // by itself, so the tip is cut back to its content and placed anew each time
        box.MinimumSizeChanged += () => Callable.From(() => Place(box)).CallDeferred();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (seq != _seq || !IsInstanceValid(box))
            return;
        Place(box);
        _up = Hold ? 1.0f : 0.0f;
        box.Modulate = new Color(1, 1, 1, _up);
        _mark?.Invoke(true);
        SetProcess(true);
        QueueRedraw();
    }

    /// <summary>The tip gone - <paramref name="key"/>'s only, or whichever with null.</summary>
    public void Hide(object? key)
    {
        if (key is not null && !ReferenceEquals(key, _for) && !Equals(key, _for))
            return;
        _seq++;
        if (_box is not null)
        {
            _warmTill = Math.Max(_warmTill, Time.GetTicksMsec() + Warm);
            _mark?.Invoke(false);
            _box.QueueFree();
            _box = null;
        }
        _for = null;
        _target = _near = null;
        _build = null;
        _mark = null;
        SetProcess(false);
        QueueRedraw();
    }

    /// <summary>The body built anew: what it says changed (a slot spent under the pointer).</summary>
    public void Renew()
    {
        if (_box is null || _build is null)
            return;
        foreach (Node n in _box.GetChildren())
            n.QueueFree();
        _box.AddChild(_build());
    }

    /// <summary>The body under the tip, to redraw a drawn one (the roster's card says
    /// the turn as it goes).</summary>
    public Control? Body => _box is { } b && b.GetChildCount() > 0 ? b.GetChild<Control>(0) : null;

    public override void _Process(double delta)
    {
        if (_box is null)
            return;
        if (_up < 1.0f)
        {
            _up = Mathf.Min(1.0f, _up + (float)delta / Fade);
            _box.Modulate = new Color(1, 1, 1, _up);
        }
        // the ring follows its target
        if (_mark is null && _ring)
            QueueRedraw();
    }

    public override void _Draw()
    {
        if (_box is null || _mark is not null || !_ring || _target is null)
            return;
        Rect2 r = _target().Grow(4.0f);
        r.Position -= GetGlobalRect().Position;
        Ui.DrawRim(this, r, new Color(Ui.TipRim, _up), 2.0f);
    }

    /// <summary>The tip cut to its content and put by its target, inside the screen, and
    /// its beak set toward the target's middle.</summary>
    private void Place(PanelContainer box)
    {
        if (!IsInstanceValid(box) || _box != box || _target is null)
            return;
        box.ResetSize();
        Rect2 own = _target(), r = _near?.Invoke() ?? own;
        Vector2 ts = box.Size, vs = GetViewportRect().Size, origin = GetGlobalRect().Position;
        float x, y;
        if (_prefer == Prefer.Above)
        {
            x = Mathf.Clamp(own.GetCenter().X - ts.X * 0.5f, 8, vs.X - ts.X - 8);
            y = r.Position.Y - ts.Y - 12;
            _beak = Edge.Bottom;
            if (y < 8)
            {
                y = r.End.Y + 12;
                _beak = Edge.Top;
            }
        }
        else
        {
            x = r.End.X + 10;
            y = r.Position.Y;
            _beak = Edge.Left;
            if (x + ts.X > vs.X - 8)
            {
                x = r.Position.X - ts.X - 10;
                _beak = Edge.Right;
            }
            if (x < 8)
            {
                x = Mathf.Clamp(r.GetCenter().X - ts.X * 0.5f, 8, vs.X - ts.X - 8);
                y = r.End.Y + 8;
                _beak = Edge.Top;
                if (y + ts.Y > vs.Y - 8)
                {
                    y = r.Position.Y - ts.Y - 8;
                    _beak = Edge.Bottom;
                }
            }
        }
        box.Position = new Vector2(x, Mathf.Clamp(y, 8, Mathf.Max(8, vs.Y - ts.Y - 8))) - origin;
        // the beak at the target's own middle (not the card's), clear of the tip's corners
        Vector2 mid = own.GetCenter() - origin;
        bool across = _beak is Edge.Left or Edge.Right;
        _beakAt = across ? Mathf.Clamp(mid.Y - box.Position.Y, 12, ts.Y - 12)
                         : Mathf.Clamp(mid.X - box.Position.X, 12, ts.X - 12);
        box.QueueRedraw();
    }

    /// <summary>The beak: a short triangle out of the tip's side toward its target, filled
    /// as the tip and rimmed as it, its base cutting the rim so the two read as one outline.</summary>
    private void DrawBeak(Control box)
    {
        if (_beak == Edge.None)
            return;
        const float half = 7, reach = 8;
        float t = Ui.RimLayout(box, 1), a = _beakAt;
        Vector2 s = box.Size;
        (Vector2 b0, Vector2 apex, Vector2 b1, Vector2 inward) = _beak switch
        {
            Edge.Left => (new Vector2(0, a - half), new Vector2(-reach, a), new Vector2(0, a + half), new Vector2(t, 0)),
            Edge.Right => (new Vector2(s.X, a - half), new Vector2(s.X + reach, a), new Vector2(s.X, a + half), new Vector2(-t, 0)),
            Edge.Top => (new Vector2(a - half, 0), new Vector2(a, -reach), new Vector2(a + half, 0), new Vector2(0, t)),
            _ => (new Vector2(a - half, s.Y), new Vector2(a, s.Y + reach), new Vector2(a + half, s.Y), new Vector2(0, -t)),
        };
        box.DrawColoredPolygon(new[] { b0 + inward, apex, b1 + inward }, Ui.TipBg);
        box.DrawPolyline(new[] { b0, apex, b1 }, Ui.TipRim, t, true);
    }

    /// <summary>A tip's body: the kicker - over the head, in capitals, after a mark in
    /// its ink when given (an act's group) - the head with a key at its right, the text,
    /// a table, a closing line, a row of kind and cost, and the state (amber, or as
    /// given). Sizes by the screens' scale (<see cref="Ui.Size"/>).</summary>
    public static Control Box(string kicker, string head, string text, (string Kind, string Cost)? row = null,
                              string? state = null, (string, string)[]? table = null, string? coda = null,
                              string? key = null, Color? kickerInk = null, Control? kickerMark = null, Color? stateInk = null,
                              bool large = false)
    {
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(300, 0) };
        col.AddThemeConstantOverride("separation", 5);
        if (kicker.Length > 0)
        {
            var kick = new HBoxContainer();
            kick.AddThemeConstantOverride("separation", 6);
            if (kickerMark is not null)
            {
                kickerMark.SizeFlagsVertical = SizeFlags.ShrinkCenter;
                kick.AddChild(kickerMark);
            }
            kick.AddChild(Ui.Text(kicker.ToUpperInvariant(), 11, kickerInk ?? Ui.TipMuted, bold: true, large: large));
            col.AddChild(kick);
        }
        var top = new HBoxContainer();
        var h = Ui.Text(head, 17, Ui.TipInk, bold: true, large: large);
        h.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        top.AddChild(h);
        if (key is { Length: > 0 })
        {
            var k = Ui.Text(key, 12, Ui.TipMuted, large: large);
            k.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            top.AddChild(k);
        }
        col.AddChild(top);
        if (text.Length > 0)
            col.AddChild(Ui.Text(text, 13, Ui.TipInk, wrap: true, large: large));
        if (table is not null)
        {
            var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            grid.AddThemeConstantOverride("h_separation", 10);
            foreach ((string k, string v) in table)
            {
                var kl = Ui.Text(k, 12, Ui.TipMuted, large: large);
                kl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                grid.AddChild(kl);
                var vl = Ui.Text(v, 12, Ui.TipInk, bold: true, large: large);
                vl.HorizontalAlignment = HorizontalAlignment.Right;
                grid.AddChild(vl);
            }
            col.AddChild(grid);
        }
        if (coda is not null)
            col.AddChild(Ui.Text(coda, 13, Ui.TipInk, wrap: true, large: large));
        if (row is var (kind, cost))
        {
            col.AddChild(new ColorRect { Color = new Color(1, 1, 1, 0.12f), CustomMinimumSize = new Vector2(0, 1) });
            var r = new HBoxContainer();
            var kl = Ui.Text(kind, 12, Ui.TipMuted, large: large);
            kl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            r.AddChild(kl);
            r.AddChild(Ui.Text(cost, 12, Ui.TipInk, bold: true, large: large));
            col.AddChild(r);
        }
        if (state is { Length: > 0 })
            col.AddChild(Ui.Text(state, 12, stateInk ?? Ui.TipState, wrap: true, large: large));
        return col;
    }
}
