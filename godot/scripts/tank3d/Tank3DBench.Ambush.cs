using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The ambush on the bench (docs/battle-core-plan.md, Д7; GDD units.md,
/// «Режим засады»; classes.md, «MT — Секторная засада»). The rules are the
/// core's (<see cref="Battle.Ambush"/>, <see cref="Battle.Ambushes"/>): the
/// bench keeps who watches which axis, asks the core whether a hull coming on
/// to a hex is fired on, and plays what the core says - Р-1, the core decides.
/// <list type="bullet">
/// <item><b>Set</b>: the button «Засада» (no key - none is free), then a hex
/// on the axis; the medium takes a second hex on the axis beside it for its
/// sector, or the same axis again (or Enter) for one. A command too:
/// <c>ambush:SIDE[:SIDE2]</c>. The turn is spent with it: set last
/// (units.md); the turret is laid along the axis and stays so.</item>
/// <item><b>Lifted</b> by any order to the unit (<c>ShadowAct</c>), a ram into
/// it, its knocking out or destruction (<see cref="AmbushTick"/>).</item>
/// <item><b>Fired</b> as a hull - the own tank driving, the target shoved by a
/// ram - comes on to a hex (<c>HexEffectsTick</c>): the core's state off the
/// bench, its <see cref="Battle.Ambushes"/> run, and the round it fired flown
/// from the watcher's muzzle down the core's legs. A hull it knocks out is
/// stopped at once and rolls to the hex's middle, as a mine's; the fates land
/// with the round.</item>
/// <item><b>Drawn</b>: the axis as far as it sees now, dashed in the side's
/// ink, and a sight over the watcher (<see cref="DrawAmbushes"/>).</item>
/// </list>
/// </summary>
public sealed partial class Tank3DBench
{
    /// <summary>Who watches: unit id to its axis and the sector's second (-1 for none).</summary>
    private readonly Dictionary<int, (int A, int B)> _ambush = new();
    /// <summary>Ambush rounds fired in the match: a move one was fired on is
    /// not taken back (Tank3DBench.Undo).</summary>
    private int _ambushShots;
    /// <summary>The gun laid by <c>Laying</c> and held, not fired: the ambush's lay.</summary>
    private bool _aimHold;
    /// <summary>The panel's «Засада» on: a click on a hex picks the axis.</summary>
    private bool _ambushMode;
    /// <summary>The medium's first axis, waiting for a second or the confirm.</summary>
    private int? _ambushPick;
    private Button? _actAmbush;

    private const string AmbushText = "Засада";

    /// <summary>Why the unit on the rig may not watch <paramref name="axis"/>
    /// (and <paramref name="second"/>) now, or empty.</summary>
    private string AmbushWhy(int axis, int second)
    {
        if (_fate != Fate.Alive)
            return $"{_modelTag} не на ходу";
        if (_side != _mt.Turn)
            return $"ходят {SideName(_mt.Turn)}";
        if (MatchShotSpent)
            return "атака этого хода уже сделана";
        if (System.Array.IndexOf(HexField.EdgeHeadings, axis) < 0)
            return "это не ось";
        if (second >= 0)
        {
            if (!_profile.WatchesSector)
                return "две оси караулит только MT";
            int apart = ((second - axis) % 360 + 360) % 360;
            if (apart != 60 && apart != 300)
                return "оси сектора должны быть соседними";
        }
        if (!_profile.Turreted && axis != HullSide())
            return "каземат караулит по корпусу: сперва развернись";
        return "";
    }

    /// <summary>The ambush set (the command's executor): the core told, the
    /// turn spent, the turret laid along the axis.</summary>
    private void AmbushSet(int axis, int second)
    {
        ShadowAct($"ambush {axis}{(second >= 0 ? $"+{second}" : "")}", s => Battle.Ambush(s, axis, second));
        _ambush[_unitId] = (axis, second);
        _mt.Fired = _mt.Drove = _mt.Bonus = true;
        _ambushMode = false;
        _ambushPick = null;
        _ramMode = false;
        if (_model.Turreted && _field is not null)
        {
            // two hexes out along the axis, at the hull's height: the turret
            // round to it by the keys' own inputs, then held (Laying)
            Vector2I out2 = HexField.Step(HexField.Step(CellHere, axis), axis);
            _aim = Foot(CellWorld(out2)) + Vector3.Up * (_model.Size.Y * _model.PixelsPerUnit * 0.5f);
            _aimRound = null;
            _aimHold = true;
        }
        _matchNote = second >= 0 ? "засада на две оси" : "засада";
        Log.Print($"tank3d: match: {SideName(_side)} {_modelTag}#{_unitId} in ambush on {MapAt(CellHere)} along {axis}"
                  + (second >= 0 ? $" and {second}" : ""));
    }

    /// <summary>Unit <paramref name="id"/>'s ambush off, saying why.</summary>
    private void AmbushLift(int id, string why)
    {
        if (id >= 0 && _ambush.Remove(id))
            Log.Print($"tank3d: match: #{id} out of ambush - {why}");
    }

    /// <summary>Every frame of a match: a watcher knocked out or destroyed
    /// watches no more (the core's <c>Lift</c>). Allocates nothing.</summary>
    private void AmbushTick()
    {
        if (_ambush.Count == 0)
            return;
        for (int i = 0; i < _parked.Count; i++)
            if (_parked[i].Fate != Fate.Alive)
                _ambush.Remove(_parked[i].Id);
        if (_other is { Unit: true } o && o.Fate != Fate.Alive)
            _ambush.Remove(o.Id);
        if (_fate != Fate.Alive)
            _ambush.Remove(_unitId);
    }

    private Side? SideOfUnit(int id) =>
        id == _unitId ? _side
        : _other is { Unit: true } o && o.Id == id ? o.Side
        : UnitStanding(id)?.Side;

    /// <summary>
    /// A hull - the own tank (<paramref name="rig"/>) or the target - has come
    /// on to <paramref name="cell"/> from <paramref name="from"/>: the
    /// other side's ambushes asked through the core, on the bench as it stands,
    /// the hull turned the way the core's move has it. A hex with a live mine
    /// is the mine's: it knocks the hull out first and the ambush answers the
    /// hull as it came in (<c>Battle.Entered</c>).
    /// </summary>
    private void AmbushWatch(int id, Vector2I cell, Vector2I from, bool rig)
    {
        if (_ambush.Count == 0 || _field is null || _lineup.Count == 0 || MatchOver)
            return;
        if (SideOfUnit(id) is not Side side)
            return;
        bool any = false;
        foreach (KeyValuePair<int, (int A, int B)> w in _ambush)
            any |= SideOfUnit(w.Key) is Side ws && ws != side;
        if (!any)
            return;
        if (_field.CoverAt(cell) == Cover.Minefield && _field.CoverStateAt(cell) == CoverState.Intact)
            return;
        BattleState st = ShadowOfBench();
        if (st.Unit(id) is not { } mover)
            return;
        mover.Cell = cell;
        int way = HexField.HeadingTo(from, cell);
        if (rig && way >= 0 && mover.Heading != HexField.Reverse(way))
            mover.Heading = way;
        int was = st.Events.Count;
        UnitFate before = mover.Fate;
        Battle.Ambushes(st, mover);
        if (st.Events.Count == was)
            return;
        Log.Print($"tank3d: battle: {mover.Tag}#{id} on {MapAt(cell)}: " + string.Join(", ", st.Events.GetRange(was, st.Events.Count - was)));
        // a hull it knocks out stops here, now, and rolls to the middle - as
        // a mine's does (Tank3DBench.Mines); the fate lands with the round
        if (rig && mover.Fate != before && _fate == Fate.Alive)
        {
            CancelOrder();
            _coastTo = CellWorld(cell);
            _speed = (_speed < 0.0f ? -1.0f : 1.0f) * Mathf.Max(Mathf.Abs(_speed), 0.2f * MaxSpeed);
            _driveScripted = 0.0f;
        }
        AmbushPlay(st, was);
    }

    /// <summary>The core's ambush rounds from event <paramref name="from"/>
    /// on: each flown from its watcher's muzzle along the legs the core gave it
    /// - the hull it took, a glance on, a pass through - and on landing what
    /// they did: holes, glances, fates.</summary>
    private void AmbushPlay(BattleState st, int from)
    {
        List<Vector3>? path = null;
        var struck = new List<(int Unit, bool Pierced, string Plate)>();
        void Fly()
        {
            if (path is null)
                return;
            var hits = new List<(int Unit, bool Pierced, string Plate)>(struck);
            ShellsBuild();
            if (path.Count < 2)
                path.Add(path[0] + Vector3.Up);
            _shells!.Fly(path, Shell.Speed, TracerCal, SmokeCal, false, () => AmbushLanded(hits));
            path = null;
            struck.Clear();
        }
        for (int i = from; i < st.Events.Count; i++)
        {
            BattleEvent e = st.Events[i];
            switch (e.Kind)
            {
                case BattleEventKind.AmbushFired:
                    Fly();
                    _ambush.Remove(e.Unit);
                    _ambushShots++;
                    path = new List<Vector3> { MuzzleOf(e.Unit) };
                    break;
                case BattleEventKind.Hit:
                case BattleEventKind.Glanced:
                    path?.Add(HullPoint(e.Unit, e.Cell));
                    struck.Add((e.Unit, e.Kind == BattleEventKind.Hit, e.Note));
                    break;
                case BattleEventKind.Spent:
                    path?.Add(Foot(CellWorld(e.Cell)) + Vector3.Up * (CellStep * 0.15f));
                    break;
            }
        }
        Fly();
    }

    /// <summary>The ambush round arrived: each hull it met, by what the core
    /// said of it - a pierce is a fate, a glance only marks.</summary>
    private void AmbushLanded(List<(int Unit, bool Pierced, string Plate)> hits)
    {
        foreach ((int unit, bool pierced, string plate) in hits)
        {
            TankShot();
            if (unit == _unitId)
            {
                // the rig's own hull: its marks by the face, and «Подбит» - a
                // mine's fate without the throw (Tank3DBench.Mines)
                int face = plate switch { "Side" => 1, "Rear" => 2, _ => 0 };
                Hit(face, pierced);
                if (pierced && _fate == Fate.Alive)
                {
                    _fate = Fate.Knocked;
                    _sinceFate = 0.0f;
                    Quench(_unitId, steam: false);
                    FxKnocked();
                    _note = "knocked out from an ambush";
                    MatchCheckEnd();
                }
                else if (pierced && _fate == Fate.Knocked)
                {
                    _fate = Explode(_model, _unitId, _modelTag, CellHere, _side);
                    MatchCheckEnd();
                }
            }
            else if (_other is { Unit: true } o && o.Id == unit)
                UnitStruck(o, pierced);
            else if (UnitStanding(unit) is { } p)
                UnitStruck(p, pierced);
        }
    }

    private Vector3 MuzzleOf(int id)
    {
        TankModel? m = ModelOf(id);
        return m is not null && IsInstanceValid(m.Muzzle) ? m.Muzzle.GlobalPosition : _rig.GlobalPosition;
    }

    /// <summary>Where a round meets unit <paramref name="id"/>: half up its hull,
    /// where it stands - the rig's own as it drives.</summary>
    private Vector3 HullPoint(int id, Vector2I cell)
    {
        TankModel? m = ModelOf(id);
        Node3D? rig = id == _unitId ? _rig
            : _other is { Unit: true } o && o.Id == id ? o.Rig
            : UnitStanding(id)?.Rig;
        Vector3 at = rig?.GlobalPosition ?? Foot(CellWorld(cell));
        return at + Vector3.Up * ((m?.Size.Y ?? 1.0f) * (m?.PixelsPerUnit ?? 1.0f) * 0.45f);
    }

    // --- the panel ---------------------------------------------------------------------

    /// <summary>«Засада» on or off: on, a click on a hex picks the axis.</summary>
    private void AmbushToggle()
    {
        if (!_ambushMode && AmbushWhy(HullSide(), -1) is { Length: > 0 } why && MatchShotSpent)
        {
            _orderNote = why;
            return;
        }
        _ambushMode = !_ambushMode;
        _ambushPick = null;
        if (_ambushMode)
            _ramMode = false;
        _orderNote = !_ambushMode ? "засада выключена"
            : _profile.WatchesSector ? "засада: щёлкни по гексу оси, затем по соседней — сектор, или ещё раз — одна ось"
            : _profile.Turreted ? "засада: щёлкни по гексу на оси"
            : "засада по корпусу: щёлкни по гексу впереди";
    }

    /// <summary>The left button with «Засада» on: the hex's axis picked - for
    /// the medium the first waits for its neighbour or itself again. True when
    /// the click was the ambush's.</summary>
    private bool AmbushClick(Vector2 screen)
    {
        if (!_ambushMode)
            return false;
        if (PickAt(screen) is not var (point, onTank, onOwn) || _field is null)
            return true;
        Vector2I cell = onTank && _other is { } t ? OtherCell(t) : onOwn ? CellHere : _field.FlatCellAt(Board(point));
        (int axis, _) = AxisTo(CellHere, cell);
        if (axis < 0)
        {
            _orderNote = "засада: гекс не на оси";
            return true;
        }
        if (_profile.WatchesSector && _ambushPick is not int first)
        {
            if (AmbushWhy(axis, -1) is { Length: > 0 } no)
            {
                _orderNote = no;
                return true;
            }
            _ambushPick = axis;
            _orderNote = "засада: соседняя ось — сектор, эта же или Enter — одна ось";
            return true;
        }
        int a = _ambushPick ?? axis;
        int b = _ambushPick is int f && f != axis ? axis : -1;
        Execute(new Command(CommandKind.Ambush, Side: a, Side2: b));
        return true;
    }

    /// <summary>Enter with the medium's first axis picked: one axis.</summary>
    private bool AmbushConfirm()
    {
        if (!_ambushMode || _ambushPick is not int a)
            return false;
        Execute(new Command(CommandKind.Ambush, Side: a));
        return true;
    }

    // --- the picture ---------------------------------------------------------------------

    /// <summary>Over the board: each watcher's axes, dashed in its side's ink as
    /// far as they see now, and a sight over it; the medium's first pick too.
    /// Allocates nothing.</summary>
    private void DrawAmbushes(Control ink, float r)
    {
        foreach (KeyValuePair<int, (int A, int B)> w in _ambush)
        {
            int id = w.Key;
            TankModel? model = ModelOf(id);
            Node3D? rig = id == _unitId ? _rig
                : _other is { Unit: true } o && o.Id == id ? o.Rig
                : UnitStanding(id)?.Rig;
            Vector2I? cell = id == _unitId ? CellHere
                : _other is { Unit: true } ot && ot.Id == id ? OtherCell(ot)
                : UnitStanding(id)?.Cell;
            if (model is null || rig is null || cell is not Vector2I c || SideOfUnit(id) is not Side side)
                continue;
            Color tint = SideInk(side);
            AxisLine(ink, c, w.Value.A, tint);
            if (w.Value.B >= 0)
                AxisLine(ink, c, w.Value.B, tint);
            Sight(ink, Over(model, rig) + new Vector2(r * 2.3f, 0.0f), r, tint);
        }
        if (_ambushMode && _ambushPick is int pick)
            AxisLine(ink, CellHere, pick, CardPale);
    }

    /// <summary>One axis out of <paramref name="cell"/>: hex to hex until the
    /// first thing that cuts it - a wall across an edge, a hull, a wood, the
    /// board's edge - dashed, with a tick on each hex it watches.</summary>
    private void AxisLine(Control ink, Vector2I cell, int heading, Color tint)
    {
        Vector2 start = _camera.UnprojectPosition(Foot(CellWorld(cell)));
        Vector2 end = start;
        Vector2I at = cell;
        int level = _field!.LevelAt(cell);
        for (int k = 0; k < 16; k++)
        {
            if (_field.Walled(at, heading))
                break;
            Vector2I next = HexField.Step(at, heading);
            if (next == at || !_field.InBounds(next))
                break;
            Vector2 p = _camera.UnprojectPosition(Foot(CellWorld(next)));
            end = p;
            ink.DrawCircle(p, 2.5f, new Color(tint, 0.85f));
            if (UnitOn(next) is not null || _field.LevelAt(next) > level
                || (_field.LevelAt(next) >= level && _field.Screened(next)))
                break;
            at = next;
        }
        if (end == start)
            return;
        ink.DrawDashedLine(start, end, new Color(0.0f, 0.0f, 0.0f, 0.45f), 4.0f, 10.0f, true, true);
        ink.DrawDashedLine(start, end, new Color(tint, 0.9f), 2.0f, 10.0f, true, true);
    }

    /// <summary>The watcher's sign: a sight on a dark token.</summary>
    private static void Sight(Control ink, Vector2 at, float r, Color tint)
    {
        ink.DrawCircle(at, r + 1.5f, new Color(0.0f, 0.0f, 0.0f, 0.45f));
        ink.DrawCircle(at, r, BadgeToken);
        ink.DrawArc(at, r, 0.0f, Mathf.Tau, 32, tint, 1.5f, true);
        float s = r * 0.55f;
        ink.DrawArc(at, s * 0.7f, 0.0f, Mathf.Tau, 24, tint, 1.5f, true);
        ink.DrawLine(at - new Vector2(s, 0.0f), at - new Vector2(s * 0.35f, 0.0f), tint, 1.5f, true);
        ink.DrawLine(at + new Vector2(s * 0.35f, 0.0f), at + new Vector2(s, 0.0f), tint, 1.5f, true);
        ink.DrawLine(at - new Vector2(0.0f, s), at - new Vector2(0.0f, s * 0.35f), tint, 1.5f, true);
        ink.DrawLine(at + new Vector2(0.0f, s * 0.35f), at + new Vector2(0.0f, s), tint, 1.5f, true);
    }
}
