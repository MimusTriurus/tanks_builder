using System.Collections.Generic;
using System.Text;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The blast of a knocked-out target, shown before the shot (2026-10-28), GDD
/// states.md «Уничтожен»: any round on a knocked or drowned hull blows it up,
/// and the blast reaches the six hexes round it on its level, whoever's -
/// a unit still fighting catches fire, a knocked one goes up next, a chain.
///
/// <b>Not a ghost</b>: nobody moves, and a copy of a hull over the hull is
/// nothing. Instead, on the board and on the units themselves:
/// <list type="bullet">
/// <item>the hexes the blast reaches, on the ground (<see cref="BlastGround"/>);
/// a neighbour on another level is not lit - the rule seen;</item>
/// <item>on each unit it reaches, its outline and a sign over it
/// (<see cref="DrawBlast"/>): orange and a flame - it catches fire; red and
/// a burst - it goes up in the chain, and its own hexes are lit in turn;</item>
/// <item>the HUD: whom it sets alight, the own side's called out, and the
/// chain.</item>
/// </list>
/// Played over and over as the ram's preview is (<see cref="RamLoop"/>): the
/// target's burst, its hexes flaring, the signs coming up, each link of the
/// chain a <see cref="ChainBeat"/> after the last - the real chain's beat.
///
/// <b>The same answer as the blast</b>: the hexes by <see cref="BlastTouches"/>,
/// the units by <see cref="UnitOn"/>, and fire refused in water as
/// <see cref="Ignite"/> refuses it. Shown only when the round reaches the
/// target (<see cref="Judged.Fires"/>) - a lane shut short of it blows
/// nothing up.
///
/// Not yet: a ricochet or a pass on to a knocked hull past the target, the
/// wood the blast would set alight.
/// </summary>
public sealed partial class Tank3DBench
{
    private enum BlastHit { Fire, Chain, Wet, Alight }

    private readonly record struct BlastMark(Vector2I Cell, int Wave, BlastHit Hit, Side Side, string Tag, TankModel Model);

    /// <summary>The units the shown blast reaches, in the order it reaches them.</summary>
    private readonly List<BlastMark> _blastMarks = new();

    /// <summary>The hexes it reaches, with the wave that reaches them; and the
    /// hexes that go up, the wave each goes up in (the target nought).</summary>
    private readonly List<(Vector2I Cell, int Wave)> _blastZone = new(), _blastBlows = new();

    private Vector2I? _blastAt;
    private int _blastSig;
    private string _blastNote = "";
    private float _blastPlay;

    /// <summary>When in the loop the round lands, s.</summary>
    private const float BlastStart = 0.25f;

    /// <summary>The loop's rest after the last link, s.</summary>
    private const float BlastRest = 1.4f;

    /// <summary>How long a sign, an outline or a hex takes to come up, and
    /// everything to go down at the loop's end, s. Popped up and cut at the
    /// end, the loop read as a blinking alarm (the user's note, 2026-10-28).</summary>
    private const float BlastRise = 0.3f, BlastFall = 0.5f;

    /// <summary>How far up a mark set off at <paramref name="when"/> is now,
    /// 0..1: eased in after it, and with the rest eased out at the loop's end.</summary>
    private float BlastUp(float when) =>
        Mathf.SmoothStep(0.0f, 1.0f, (_blastPlay - when) / BlastRise) * BlastOut;

    /// <summary>The loop's fade at its end, 1..0.</summary>
    private float BlastOut => 1.0f - Mathf.SmoothStep(BlastLoop - BlastFall, BlastLoop, _blastPlay);

    private static readonly Color BlastFire = new(1.0f, 0.58f, 0.12f);
    private static readonly Color BlastChain = new(0.95f, 0.22f, 0.16f);

    /// <summary>From <see cref="AimTick"/>, the shot judged: the blast shown
    /// when the round would reach a knocked-out unit, else put away.</summary>
    private void BlastPlanTick(Judged? shot, Other? o)
    {
        if (!Match || shot is not { Fires: true } || o is not { Unit: true, Fate: Fate.Knocked } || _field is null)
        {
            BlastPlanClear();
            return;
        }
        Vector2I at = OtherCell(o);
        BlastReach(at);
        int sig = BlastSig(at);
        if (_blastAt != at || sig != _blastSig)
        {
            _blastAt = at;
            _blastSig = sig;
            _blastPlay = 0.0f;
            _blastNote = BlastSays();
            Log.Print($"tank3d: blast preview on {MapAt(at)}: {_blastNote}");
        }
        _blastPlay = (_blastPlay + (float)GetProcessDeltaTime()) % BlastLoop;
        // the outlines over the hover's: HaloTick has set them this frame
        for (int i = 0; i < _blastMarks.Count; i++)
        {
            BlastMark m = _blastMarks[i];
            if (m.Hit is not (BlastHit.Fire or BlastHit.Chain))
                continue;
            SetHalo(m.Model, Mathf.Lerp(HaloHover, HaloChosen, BlastUp(WaveAt(m.Wave))),
                    m.Hit == BlastHit.Fire ? BlastFire : BlastChain);
        }
    }

    private void BlastPlanClear()
    {
        _blastAt = null;
        _blastMarks.Clear();
        _blastZone.Clear();
        _blastBlows.Clear();
        _blastNote = "";
    }

    /// <summary>When the blast of wave <paramref name="wave"/> goes off in
    /// the loop, s.</summary>
    private static float WaveAt(int wave) => BlastStart + ChainBeat * wave;

    private float BlastLoop
    {
        get
        {
            int last = 0;
            for (int i = 0; i < _blastBlows.Count; i++)
                last = Mathf.Max(last, _blastBlows[i].Wave);
            return WaveAt(last) + BlastRest;
        }
    }

    /// <summary>The blast from <paramref name="at"/> and the chain it starts:
    /// who it reaches and what it does to them, wave by wave.</summary>
    private void BlastReach(Vector2I at)
    {
        _blastMarks.Clear();
        _blastZone.Clear();
        _blastBlows.Clear();
        _blastBlows.Add((at, 0));
        for (int b = 0; b < _blastBlows.Count; b++)
        {
            (Vector2I from, int wave) = _blastBlows[b];
            foreach (int side in HexField.EdgeHeadings)
            {
                Vector2I n = HexField.Step(from, side);
                if (!BlastTouches(from, n))
                    continue;
                if (!InZone(n))
                    _blastZone.Add((n, wave));
                // gone up already, the target among them: nothing left to reach
                if (Blows(n) || Marked(n) || UnitOn(n) is not { } u)
                    continue;
                if (u.Fate == Fate.Knocked)
                {
                    _blastMarks.Add(new BlastMark(n, wave, BlastHit.Chain, u.Side, u.Tag, u.Model));
                    _blastBlows.Add((n, wave + 1));
                }
                else if (u.Fate == Fate.Alive)
                {
                    // as Ignite has it: no fire in water, none twice
                    BlastHit hit = _field!.IsWater(n) ? BlastHit.Wet
                        : Burning(u.Side, u.Tag) ? BlastHit.Alight : BlastHit.Fire;
                    _blastMarks.Add(new BlastMark(n, wave, hit, u.Side, u.Tag, u.Model));
                }
            }
        }
    }

    private bool InZone(Vector2I cell)
    {
        for (int i = 0; i < _blastZone.Count; i++)
            if (_blastZone[i].Cell == cell)
                return true;
        return false;
    }

    private bool Blows(Vector2I cell)
    {
        for (int i = 0; i < _blastBlows.Count; i++)
            if (_blastBlows[i].Cell == cell)
                return true;
        return false;
    }

    private bool Marked(Vector2I cell)
    {
        for (int i = 0; i < _blastMarks.Count; i++)
            if (_blastMarks[i].Cell == cell)
                return true;
        return false;
    }

    /// <summary>What the blast found, as a number: the words are made again
    /// only when it changes.</summary>
    private int BlastSig(Vector2I at)
    {
        int sig = at.X * 7919 + at.Y;
        for (int i = 0; i < _blastMarks.Count; i++)
        {
            BlastMark m = _blastMarks[i];
            sig = sig * 31 + m.Cell.X * 131 + m.Cell.Y * 17 + (int)m.Hit * 5 + m.Wave;
        }
        return sig;
    }

    /// <summary>The HUD's words: whom it sets alight, the chain, and who is
    /// spared and why.</summary>
    private string BlastSays()
    {
        var says = new StringBuilder("взорвётся");
        void Name(BlastHit hit, string head)
        {
            bool any = false;
            foreach (BlastMark m in _blastMarks)
            {
                if (m.Hit != hit)
                    continue;
                says.Append(any ? ", " : head).Append(m.Tag);
                if (m.Side == _side)
                    says.Append(" (свой!)");
                any = true;
            }
        }
        Name(BlastHit.Fire, "; подожжёт ");
        Name(BlastHit.Chain, "; цепь: ");
        Name(BlastHit.Wet, "; в воде не загорится: ");
        Name(BlastHit.Alight, "; уже горит: ");
        if (_blastMarks.Count == 0)
            says.Append("; рядом никого");
        return says.ToString();
    }

    // --- the picture -------------------------------------------------------------

    /// <summary>The reached hexes on the ground: always there, faint, and
    /// each flaring when its wave goes off.</summary>
    private void BlastGround(ImmediateMesh mesh)
    {
        if (_blastAt is null || _blastZone.Count == 0 || _tile is null)
            return;
        Vector2[] corner = Corners();
        mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        for (int i = 0; i < _blastZone.Count; i++)
        {
            (Vector2I cell, int wave) = _blastZone[i];
            // eased up to its flare, then slowly down to a glow, and out with the loop
            float when = WaveAt(wave);
            float glow = 0.4f + 0.6f * Mathf.Exp(-1.5f * Mathf.Max(0.0f, _blastPlay - when - BlastRise));
            var tint = new Color(BlastFire, 0.16f + 0.30f * BlastUp(when) * glow);
            Vector2 mid = CellFlat(cell);
            for (int k = 0; k < 6; k++)
                Tri(mesh, tint, cell, mid, mid + corner[k] * 0.92f, mid + corner[(k + 1) % 6] * 0.92f);
        }
        mesh.SurfaceEnd();
    }

    /// <summary>Over the board: a burst over each hull that goes up, as it
    /// goes; a flame over each one set alight, as the blast reaches it.</summary>
    private void DrawBlast(Control ink)
    {
        if (_blastAt is null)
            return;
        float hex = HexScreen();
        float r = Mathf.Max(10.0f, hex * 0.16f);
        if (_other is { } o)
            Pop(ink, Over(o.Model, o.Rig), WaveAt(0), r * 1.2f, BlastChain, true);
        for (int i = 0; i < _blastMarks.Count; i++)
        {
            BlastMark m = _blastMarks[i];
            if (m.Hit is not (BlastHit.Fire or BlastHit.Chain) || m.Model.GetParent() is not Node3D rig)
                continue;
            bool chain = m.Hit == BlastHit.Chain;
            // a link goes up a beat after the blast that reached it
            Pop(ink, Over(m.Model, rig), WaveAt(chain ? m.Wave + 1 : m.Wave), r, chain ? BlastChain : BlastFire, chain);
        }
    }

    private Vector2 Over(TankModel model, Node3D rig) =>
        _camera.UnprojectPosition(rig.GlobalPosition + Vector3.Up * (model.Size.Y * model.PixelsPerUnit * 1.15f));

    /// <summary>A sign that comes up at <paramref name="when"/> in the loop -
    /// fading in as it grows from three quarters of its size - and stays until
    /// the loop fades out.</summary>
    private void Pop(Control ink, Vector2 at, float when, float r, Color c, bool burst)
    {
        float up = BlastUp(when);
        if (up <= 0.01f)
            return;
        float s = Mathf.Lerp(0.75f, 1.0f, up);
        if (burst)
            Burst(ink, at, r * s, c, up);
        else
            Flame(ink, at, r * s, c, up);
    }

    private readonly Vector2[] _flame = new Vector2[13];

    /// <summary>A flame: a drop with its point up, in <paramref name="c"/> on a
    /// dark one, a pale core inside.</summary>
    private void Flame(Control ink, Vector2 at, float r, Color c, float a = 1.0f)
    {
        FlameShape(at, r * 1.15f);
        ink.DrawColoredPolygon(_flame, new Color(0.0f, 0.0f, 0.0f, 0.5f * a));
        FlameShape(at, r);
        ink.DrawColoredPolygon(_flame, new Color(c, c.A * a));
        FlameShape(at + new Vector2(0.0f, r * 0.25f), r * 0.45f);
        ink.DrawColoredPolygon(_flame, new Color(1.0f, 0.93f, 0.6f, a));
    }

    private void FlameShape(Vector2 at, float r)
    {
        // a circle below, drawn to a point above: the drop
        int n = _flame.Length - 1;
        _flame[0] = at + new Vector2(0.0f, -r * 1.35f);
        for (int i = 1; i <= n; i++)
        {
            float a = Mathf.Lerp(-0.2f * Mathf.Pi, 1.2f * Mathf.Pi, (i - 1) / (float)(n - 1));
            _flame[i] = at + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * (r * 0.7f) + new Vector2(0.0f, r * 0.15f);
        }
    }
}
