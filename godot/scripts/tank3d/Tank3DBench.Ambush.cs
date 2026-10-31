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
/// <item><b>Set</b>: the button «Засада» (no key - none is free), then a
/// click: the axis under the pointer (<see cref="AmbushAxisAt"/> - the hex's
/// own, else the nearest of the six; a casemate's hull's); the medium takes a
/// second beside it for its sector, or the same again (or Enter) for one. In
/// the mode the move is not shown nor given, the turret comes round to the
/// axis under the pointer and it is drawn as the line of fire is, red beads
/// out of the muzzle to what cuts it (<see cref="AmbushBeads"/>). A command
/// too: <c>ambush:SIDE[:SIDE2]</c>. The turn is spent with it: set last
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
    /// <summary>Who watches: unit id to its axis, the sector's second (-1 for none)
    /// and the rounds it has left - two with the stabilizer (Д8).</summary>
    private readonly Dictionary<int, (int A, int B, int Shots)> _ambush = new();
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

    /// <summary>The ambush stabilizer (upgrades.md, Д8): the watch goes on past
    /// the first round, and a ram does not lift it.</summary>
    private bool RigHasStabilizer => (KitOf(_unitId) & Kit.AmbushStabilizer) != 0;

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
        _ambush[_unitId] = (axis, second, RigHasStabilizer ? 2 : 1);
        _mt.Fired = _mt.Drove = _mt.Bonus = true;
        _ambushMode = false;
        _ambushPick = null;
        _ramMode = false;
        if (_model.Turreted && _field is not null)
        {
            // two hexes out along the axis, at the hull's height: the turret
            // round to it by the keys' own inputs, then held (Laying)
            _aim = AxisPoint(axis);
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
        foreach (KeyValuePair<int, (int A, int B, int Shots)> w in _ambush)
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
                    // spent - the stabilizer's at its second round (Д8)
                    if (_ambush.TryGetValue(e.Unit, out var left) && left.Shots > 1)
                        _ambush[e.Unit] = (left.A, left.B, left.Shots - 1);
                    else
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
            _smokeMode = _fieryMode = false;
        if (_ambushMode)
            _gearMode = Gear.None;
        if (_ambushMode)
            _ramMode = false;
        _orderNote = !_ambushMode ? "засада выключена"
            : _profile.WatchesSector ? "засада: наведи на ось и щёлкни, затем соседнюю — сектор, или ту же — одна ось"
            : _profile.Turreted ? "засада: наведи на ось и щёлкни"
            : "засада по корпусу: щёлкни";
    }

    /// <summary>With «Засада» on, the axis under the pointer (or
    /// <c>--hover</c>), every frame: the one the click sets and the line
    /// shows; null off the mode or with the pointer on the own hex.</summary>
    private int? _ambushAxis;

    /// <summary>The axis the pointer at <paramref name="screen"/> picks: the
    /// one its hex lies on, else the nearest of the six toward it - any hex
    /// picks one, a rose round the tank. A casemate watches along its hull
    /// (units.md), so its own whatever the pointer.</summary>
    private int? AmbushAxisAt(Vector2 screen)
    {
        if (_field is null || _fate != Fate.Alive)
            return null;
        if (!_profile.Turreted)
            return HullSide();
        Vector2I cell;
        if (_hoverFixed is Vector2I held)
            cell = held;
        else if (PickAt(screen) is var (point, onTank, onOwn))
            cell = onTank && _other is { } t ? OtherCell(t) : onOwn ? CellHere : _field.FlatCellAt(Board(point));
        else
            return null;
        if (cell == CellHere)
            return null;
        (int axis, _) = AxisTo(CellHere, cell);
        return axis >= 0 ? axis : Nearest(CellHere, CellWorld(cell) - CellWorld(CellHere));
    }

    /// <summary>Every frame (<c>AimTick</c>): the axis under the pointer, with «Засада» on.</summary>
    private void AmbushAimTick() =>
        _ambushAxis = _ambushMode && Match ? AmbushAxisAt(GetViewport().GetMousePosition()) : null;

    /// <summary>A point two hexes out along <paramref name="axis"/> at the
    /// hull's height: what the turret is laid on for it.</summary>
    private Vector3 AxisPoint(int axis)
    {
        Vector2I out2 = HexField.Step(HexField.Step(CellHere, axis), axis);
        return Foot(CellWorld(out2)) + Vector3.Up * (_model.Size.Y * _model.PixelsPerUnit * 0.5f);
    }

    /// <summary>The left button with «Засада» on: the axis under it picked
    /// (<see cref="AmbushAxisAt"/>) - for the medium the first waits for its
    /// neighbour or itself again. The move is not to be had in the mode: the
    /// click is always the ambush's.</summary>
    private bool AmbushClick(Vector2 screen)
    {
        if (!_ambushMode)
            return false;
        if (AmbushAxisAt(screen) is not int axis)
            return true;
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
        foreach (KeyValuePair<int, (int A, int B, int Shots)> w in _ambush)
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
            bool thermal = (KitOf(id) & Kit.Thermal) != 0;
            AxisLine(ink, c, w.Value.A, tint, thermal);
            if (w.Value.B >= 0)
                AxisLine(ink, c, w.Value.B, tint, thermal);
            Sight(ink, Over(model, rig) + new Vector2(r * 2.3f, 0.0f), r, tint);
        }
        if (_ambushMode && _ambushPick is int pick)
            AxisLine(ink, CellHere, pick, CardPale, (KitOf(_unitId) & Kit.Thermal) != 0);
    }

    /// <summary>One axis out of <paramref name="cell"/>: hex to hex until the
    /// first thing that cuts it - a wall across an edge, a hull, a wood, the
    /// board's edge - dashed, with a tick on each hex it watches. A thermal
    /// imager's goes through smoke (Д8).</summary>
    private void AxisLine(Control ink, Vector2I cell, int heading, Color tint, bool thermal = false)
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
                || (_field.LevelAt(next) >= level && BoardRules.Screened(_field, next, thermal)))
                break;
            at = next;
        }
        if (end == start)
            return;
        ink.DrawDashedLine(start, end, new Color(0.0f, 0.0f, 0.0f, 0.45f), 4.0f, 10.0f, true, true);
        ink.DrawDashedLine(start, end, new Color(tint, 0.9f), 2.0f, 10.0f, true, true);
    }

    /// <summary>
    /// The axis being picked, as the line of fire is drawn (<c>BeadsTick</c>):
    /// beads in the air from the muzzle down <paramref name="axis"/> to the
    /// first thing that cuts it - a wall across an edge, a hull, a wood or
    /// smoke on its level, a rise, the board's edge (as <see cref="AxisLine"/>
    /// walks it). Its start is the muzzle's tip brought on to the axis, so the
    /// line leaves the barrel once the turret is round to it.
    /// </summary>
    private (Vector3 From, Vector3 End, bool Fades) AmbushRay(int axis)
    {
        Vector3 mid = CellWorld(CellHere);
        Vector3 tip = TipAt(CellHere);
        Vector3 dir = HexWay(CellHere, axis);
        Vector3 from = mid + dir * Mathf.Max(dir.Dot(tip - mid), 0.0f);
        from.Y = tip.Y;
        Vector3 On(Vector3 p) => from + dir * Mathf.Max(dir.Dot(p - from), 0.0f);
        bool thermal = (KitOf(_unitId) & Kit.Thermal) != 0;
        int level = _field!.LevelAt(CellHere);
        Vector2I at = CellHere;
        Vector3 end = from;
        for (int k = 0; k < 16; k++)
        {
            Vector2I next = HexField.Step(at, axis);
            Vector3 rim = 0.5f * (CellWorld(at) + CellWorld(next));
            if (_field.Walled(at, axis))
                return (from, On(rim), false);
            if (next == at || !_field.InBounds(next))
                return (from, On(rim), true);
            if (UnitOn(next) is not null || _field.LevelAt(next) > level
                || (_field.LevelAt(next) >= level && BoardRules.Screened(_field, next, thermal)))
                return (from, On(CellWorld(next)), false);
            end = On(CellWorld(next));
            at = next;
        }
        return (from, end, true);
    }

    /// <summary>
    /// With «Засада» on, the hexes the ambush may watch, as the move's zone is
    /// drawn (<c>Zone</c>) but red: every axis it may be set on now - all six
    /// for a turret, the hull's for a casemate, the first pick and the two
    /// beside it once the medium has one - each from the first hex the gun
    /// may fire at (<see cref="Gunnery.MayTarget"/>: not the one beside) to
    /// the first thing that cuts it, as the core sees it
    /// (<see cref="Battle.Sees"/>): a wall on an edge or the board's edge
    /// before it, a hull, a rise or a wood or smoke on its level the last hex
    /// watched. False with the mode off.
    /// </summary>
    private bool AmbushZone(ImmediateMesh mesh)
    {
        if (!_ambushMode || !Match || _field is null || _tile is null || !_chosen || _fate != Fate.Alive)
            return false;
        _zoneFill.Clear();
        bool thermal = (KitOf(_unitId) & Kit.Thermal) != 0;
        int level = _field.LevelAt(CellHere);
        foreach (int axis in HexField.EdgeHeadings)
        {
            string why = _ambushPick is int first ? (axis == first ? "" : AmbushWhy(first, axis)) : AmbushWhy(axis, -1);
            if (why.Length > 0)
                continue;
            Vector2I at = CellHere;
            for (int k = 1; k <= 16; k++)
            {
                if (_field.Walled(at, axis))
                    break;
                Vector2I next = HexField.Step(at, axis);
                if (next == at || !_field.InBounds(next))
                    break;
                if (Gunnery.MayTarget(_profile.Lobs, k, true) == Gunnery.Distance.Fits)
                    _zoneFill.Add(next);
                if (UnitOn(next) is not null || _field.LevelAt(next) > level
                    || (_field.LevelAt(next) >= level && BoardRules.Screened(_field, next, thermal)))
                    break;
                at = next;
            }
        }
        if (_zoneFill.Count == 0)
            return true;
        Vector2[] corner = Corners();
        mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        var tint = new Color(AimPierce, ZoneTone);
        foreach (Vector2I cell in _zoneFill)
            ZoneCell(mesh, tint, cell, corner);
        ZoneBand(corner, 1);
        var line = new Color(AimPierce, 0.95f);
        for (int i = 0; i < _band.Count; i++)
        {
            mesh.SurfaceSetColor(line);
            mesh.SurfaceAddVertex(_band[i]);
        }
        mesh.SurfaceEnd();
        return true;
    }

    /// <summary>The beads of the axis or axes being picked: the medium's first
    /// pick and the one under the pointer, red where the ambush may be set,
    /// grey where not. True when the mode had the beads.</summary>
    private bool AmbushBeads(MultiMesh mm)
    {
        if (!_ambushMode || _field is null)
            return false;
        int n = 0;
        if (_ambushPick is int pick)
            n = AxisBeads(mm, n, pick, AimPierce);
        if (_ambushAxis is int axis && axis != _ambushPick)
        {
            string why = _ambushPick is int first ? AmbushWhy(first, axis) : AmbushWhy(axis, -1);
            n = AxisBeads(mm, n, axis, why.Length == 0 ? AimPierce : AimOff);
        }
        mm.VisibleInstanceCount = n;
        return true;
    }

    /// <summary>One axis's beads from <paramref name="n"/> on: spaced, sized,
    /// running and waving as the line of fire's; returns the count after.</summary>
    private int AxisBeads(MultiMesh mm, int n, int axis, Color c)
    {
        (Vector3 from, Vector3 end, bool fades) = AmbushRay(axis);
        float total = from.DistanceTo(end);
        float gap = CellStep / BeadsAHex;
        if (total <= gap * 0.5f)
            return n;
        float t = (float)(Time.GetTicksMsec() / 1000.0);
        float run = Mathf.PosMod(t * BeadRun * CellStep, gap);
        float r = Mathf.Clamp(HexScreen() * 0.045f, 2.5f, 6.0f);
        Vector3 basisX = _camera.GlobalBasis.X.Normalized();
        float px = (_camera.UnprojectPosition(end + basisX) - _camera.UnprojectPosition(end)).Length();
        if (px < 1e-4f)
            return n;
        Basis facing = _camera.GlobalBasis.Orthonormalized();
        Vector3 dir = (end - from) / total;
        float start = gap * 0.3f;
        float tail = fades ? CellStep * 2.0f : gap;
        for (float s = start + run; s <= total && n < BeadsMost; s += gap)
        {
            float wave = 0.5f + 0.5f * Mathf.Sin(t * BeadWave - s / gap * 0.9f);
            float edge = Mathf.Clamp((s - start) / gap, 0.0f, 1.0f) * Mathf.Clamp((total - s) / tail + 0.3f, 0.0f, 1.0f);
            float size = r * (0.8f + 0.35f * wave) * (0.4f + 0.6f * edge);
            float outer = (size + 1.6f) / px;
            mm.SetInstanceTransform(n, new Transform3D(facing.Scaled(Vector3.One * outer), from + dir * s));
            mm.SetInstanceColor(n, new Color(c.Lerp(Colors.White, 0.45f * wave), (0.55f + 0.45f * wave) * edge));
            mm.SetInstanceCustomData(n, new Color(size / (size + 1.6f), 0.5f * edge, 0.0f, 0.0f));
            n++;
        }
        return n;
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
