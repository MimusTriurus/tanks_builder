using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The kit's bonus acts aimed at a hex (docs/battle-core-plan.md, Д8; GDD
/// upgrades.md): the mine layer, the repair kit, the winch. The rules are the
/// core's - asked of the bench as it stands (<see cref="ShadowOfBench"/>) - and
/// what the core says happened is what the bench plays (Р-1, as with the
/// ambush):
/// <list type="bullet">
/// <item><b>Мина</b> - a mine on a hex beside: the cell a minefield, the model
/// laid (<see cref="BoardDress.LayMine"/>), live at once.</item>
/// <item><b>Ремонт</b> - an own knocked-out hull beside fights again: the pose
/// up, its pyre out, the char off.</item>
/// <item><b>Лебёдка</b> - the hull 2 out along an axis slid on to the hex
/// between (<see cref="PullTick"/>), then what the core said that hex did to
/// it - a mine, fire, water, smoke - and the ambushes that answered it.</item>
/// <item><b>Поворот</b> - the double turn (upgrades.md, 2026-11-02): a click
/// on the hex beside, left or right of the nose, and the hull comes 60° round
/// to it with no step (<see cref="Battle.Pivot"/>); the move is over with it.
/// Its command is <c>pivot:SIDE</c>, as the core's order.</item>
/// </list>
/// A button each in the bonus act's group, for a unit with that kit, then a
/// click on the hex; or <c>mine:Q,R</c>, <c>repair:Q,R</c>, <c>winch:Q,R</c>.
/// While one is on - or «Дым» - the hexes it may be done on are drawn in
/// place of the move's zone (<see cref="PickZone"/>).
/// Not yet: the cable itself is not drawn.
/// </summary>
public sealed partial class Tank3DBench
{
    private enum Gear { None, Mine, Repair, Winch, Pivot }

    /// <summary>The panel's mode: a click on a hex is this act.</summary>
    private Gear _gearMode;
    private Button? _actMine, _actRepair, _actWinch, _actPivot;

    /// <summary>Seconds the winch takes to pull a hull a hex.</summary>
    private const float WinchTime = 0.9f;

    /// <summary>A hull on the winch's cable, and the core's account of what
    /// its new hex does to it, played when it gets there.</summary>
    private sealed class Pull
    {
        public required Parked Hull;
        public required Vector3 From, To;
        public required BattleState State;
        public required int Next;
        public float T;
    }

    private Pull? _pull;

    private static Kit KitFor(Gear g) => g switch
    {
        Gear.Mine => Kit.MineLayer,
        Gear.Repair => Kit.RepairKit,
        Gear.Pivot => Kit.DoubleTurn,
        _ => Kit.Winch,
    };

    private static string GearName(Gear g) => g switch
    {
        Gear.Mine => L.T("gear.kit_mine"),
        Gear.Repair => L.T("gear.kit_repair"),
        Gear.Pivot => L.T("gear.kit_pivot"),
        _ => L.T("gear.kit_winch"),
    };

    private bool RigHas(Kit k) => (KitOf(_unitId) & k) != 0;

    /// <summary>A bonus act on a hex being picked: «Дым» or a kit's.</summary>
    private bool PickMode => _smokeMode || _gearMode != Gear.None;

    /// <summary>The hexes the act being picked would be done on, as last
    /// found, and what they were found for: the mode, the cell, the reach's
    /// search (the board's tanks) - asking the rules of each hex builds the
    /// core's state, so not every frame.</summary>
    private readonly List<Vector2I> _pickCells = new();
    private (bool Smoke, Gear Gear, Vector2I Here, int Reach) _pickKey;
    private bool _pickFound;

    /// <summary>
    /// With «Дым», «Мина», «Ремонт» or «Лебёдка» on (the author's note,
    /// 2026-11-01): the move's zone gone (<c>CourseShown</c>) and, drawn as it
    /// is but amber, the hexes the act may be done on now - each asked of the
    /// same rules a click is (<see cref="SmokeWhy"/>, <see cref="GearWhy"/>):
    /// the six beside, or for the winch the six <see cref="Battle.WinchReach"/>
    /// out along the axes. None to be had - nothing drawn, and a click says why.
    /// </summary>
    private void PickZone(ImmediateMesh mesh)
    {
        if (!PickMode || !Match || _field is null || _tile is null || !_chosen || _fate != Fate.Alive)
        {
            _pickFound = false;
            return;
        }
        var key = (_smokeMode, _gearMode, CellHere, _reachVersion);
        if (!_pickFound || key != _pickKey)
        {
            _pickKey = key;
            _pickFound = true;
            PickCells();
        }
        _zoneFill.Clear();
        _zoneFill.AddRange(_pickCells);
        // on the relief as the move's zone is: off the hexes' edges
        if (_relief is not null)
        {
            ReliefZoneShow(Ui.Extra);
            return;
        }
        if (_pickCells.Count == 0)
            return;
        Vector2[] corner = Corners();
        mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        var tint = new Color(Ui.Extra, ZoneTone);
        foreach (Vector2I cell in _zoneFill)
            ZoneCell(mesh, tint, cell, corner);
        ZoneBand(corner, 1);
        var line = new Color(Ui.Extra, 0.95f);
        for (int i = 0; i < _band.Count; i++)
        {
            mesh.SurfaceSetColor(line);
            mesh.SurfaceAddVertex(_band[i]);
        }
        mesh.SurfaceEnd();
    }

    private void PickCells()
    {
        _pickCells.Clear();
        int reach = !_smokeMode && _gearMode == Gear.Winch ? Battle.WinchReach : 1;
        foreach (int axis in HexField.EdgeHeadings)
        {
            // the hex `reach` out along the axis, if the board has it
            Vector2I at = CellHere;
            int k = 0;
            for (; k < reach; k++)
            {
                Vector2I next = HexField.Step(at, axis);
                if (next == at || !_field!.InBounds(next))
                    break;
                at = next;
            }
            if (k < reach)
                continue;
            if ((_smokeMode ? SmokeWhy(at) : GearWhy(_gearMode, at)).Length == 0)
                _pickCells.Add(at);
        }
    }

    private static string? GearAct(Gear g, BattleState s, Vector2I cell) => g switch
    {
        Gear.Mine => Battle.LayMine(s, cell),
        Gear.Repair => Battle.Repair(s, cell),
        // the side the hex beside is on, from where the unit stands in the core
        Gear.Pivot => s.Active is int id && s.Unit(id) is { } u
            ? Battle.Pivot(s, HexField.HeadingTo(u.Cell, cell) is >= 0 and int side ? side : -1)
            : "no unit taken",
        _ => Battle.Winch(s, cell),
    };

    /// <summary>Why the unit on the rig may not do <paramref name="g"/> on
    /// <paramref name="cell"/> now, or empty: the turn's gates in the bench's
    /// words, then the core's own on the bench as it stands.</summary>
    private string GearWhy(Gear g, Vector2I cell)
    {
        if (_field is null || _lineup.Count == 0)
            return L.T("gear.why_no_match");
        if (_fate != Fate.Alive)
            return L.T("gear.why_out", _modelTag);
        if (_side != _mt.Turn)
            return L.T("gear.why_turn", SideName(_mt.Turn));
        if (!RigHas(KitFor(g)))
            return L.T("gear.why_no_kit", _modelTag, GearName(g));
        if (MatchBonusSpent)
            return BonusGoneSay("gear.why_bonus_spent");
        return GearAct(g, ShadowOfBench(), cell) is { } no ? Words(no) : "";
    }

    /// <summary>The core's refusal in the player's words.</summary>
    private static string Words(string why) => why switch
    {
        "only on a hex beside" => L.T("gear.why_beside"),
        "a tank stands there" => L.T("gear.why_tank_there"),
        "not on water" => L.T("gear.why_water"),
        "a mine there already" => L.T("gear.why_mine_there"),
        "only a hull beside" => L.T("gear.why_hull_beside"),
        "nobody there" => L.T("gear.why_nobody"),
        "not one of ours" => L.T("gear.why_not_ours"),
        "a drowned hull is not raised" => L.T("gear.why_drowned"),
        "not knocked out" => L.T("gear.why_not_knocked"),
        "heavier than its own hull" => L.T("gear.why_heavier"),
        "only on its own level" => L.T("gear.why_level"),
        "the hex between is taken" => L.T("gear.why_between"),
        "a wall across the way" => L.T("gear.why_wall"),
        "the double turn is sixty degrees" or "-1 is not a side" => L.T("gear.why_sixty"),
        "not on a ramp" => L.T("gear.why_ramp"),
        _ when why.StartsWith("the winch pulls", System.StringComparison.Ordinal)
            => L.T("gear.why_winch_reach", Battle.WinchReach),
        _ => why,
    };

    /// <summary>The act given (the command's executor): the core asked, the
    /// shadow told, the bonus act spent, and what happened played.</summary>
    private void GearDo(Gear g, Vector2I cell)
    {
        if (g == Gear.Pivot)
        {
            PivotTo(HexField.HeadingTo(CellHere, cell));
            return;
        }
        BattleState st = ShadowOfBench();
        int was = st.Events.Count;
        if (GearAct(g, st, cell) is { } no)
        {
            _orderNote = Words(no);
            return;
        }
        ShadowAct($"{g.ToString().ToLowerInvariant()} {MapAt(cell)}", s => GearAct(g, s, cell));
        _mt.Bonus = true;
        _gearMode = Gear.None;
        _orderNote = "";
        for (int i = was; i < st.Events.Count; i++)
        {
            BattleEvent e = st.Events[i];
            switch (e.Kind)
            {
                case BattleEventKind.AmbushLifted:
                    AmbushLift(e.Unit, e.Note);
                    break;
                case BattleEventKind.MineLaid:
                    MineDrop(e.Cell);
                    break;
                case BattleEventKind.Repaired:
                    Raise(e.Unit, e.Cell);
                    break;
                case BattleEventKind.Winched:
                    PullStart(e.Unit, e.Cell, st, i + 1);
                    return;
            }
        }
    }

    /// <summary>A mine laid on <paramref name="cell"/>: the board's cover and
    /// the model - a spent one there laid again.</summary>
    private void MineDrop(Vector2I cell)
    {
        _field!.LayMinefield(cell);
        Laid? old = _mines.Find(m => m.Cell == cell);
        if (old is not null)
        {
            old.Spent = old.Asleep = false;
            old.Node.Visible = true;
        }
        else if (_tile is not null && BoardDress.Mine(out string? note) is { } art)
        {
            Node3D node = BoardDress.LayMine(this, art, _field, cell, _tile.HexRect.Size, _pbr, Squash, RiseFactor,
                                             out Vector3 at);
            _mines.Add(new Laid { Cell = cell, Node = node, At = at });
        }
        else
            Log.Print($"tank3d: no mine model - the mine on {MapAt(cell)} is laid bare");
        _matchNote = L.T("gear.mine_laid");
        Log.Print($"tank3d: match: {SideName(_side)} {_modelTag}#{_unitId} lays a mine on {MapAt(cell)}");
    }

    /// <summary>The knocked-out unit <paramref name="id"/> repaired: «Активен»,
    /// its knock-out pose, pyre and char gone.</summary>
    private void Raise(int id, Vector2I cell)
    {
        if (_other is { Unit: true } o && o.Id == id)
            DropTarget();
        if (UnitStanding(id) is not { } p)
            return;
        p.Fate = Fate.Alive;
        TankModel m = p.Model;
        m.Cant = 0.0f;
        m.Droop = 0.0f;
        m.Slackness = 0.0f;
        m.Apply();
        for (int i = _pyres.Count - 1; i >= 0; i--)
            if (_pyres[i].Model == m)
            {
                _pyres[i].Burn.QueueFree();
                _pyres.RemoveAt(i);
            }
        foreach (ShaderMaterial mat in m.Cel)
        {
            mat.Param("char_cover", 0.0f);
            mat.Param("scorch", 0.0f);
        }
        Uncharred(m);
        _matchNote = L.T("gear.back_in_fight", p.Tag);
        Log.Print($"tank3d: match: {SideName(p.Side)} {p.Tag}#{id} on {MapAt(cell)} repaired - it fights from its side's next turn");
    }

    /// <summary>The winch takes unit <paramref name="id"/>: on to
    /// <paramref name="mid"/> for the rules now, slid there on the screen.</summary>
    private void PullStart(int id, Vector2I mid, BattleState st, int next)
    {
        if (_other is { Unit: true } o && o.Id == id)
            DropTarget();
        if (UnitStanding(id) is not { } p)
            return;
        Log.Print($"tank3d: match: {SideName(_side)} {_modelTag}#{_unitId}'s winch pulls {p.Tag}#{id} {MapAt(p.Cell)} -> {MapAt(mid)}");
        _pull = new Pull { Hull = p, From = p.Rig.Position, To = Foot(CellWorld(mid)), State = st, Next = next };
        p.Cell = mid;
        _matchNote = L.T("gear.winch_pulls", p.Tag);
    }

    /// <summary>Every frame of a match: the hull on the cable a step nearer,
    /// and on arrival what its hex did to it. Allocates nothing.</summary>
    private void PullTick(float dt)
    {
        if (_pull is not { } pull)
            return;
        if (!IsInstanceValid(pull.Hull.Rig))
        {
            _pull = null;
            return;
        }
        pull.T += dt / WinchTime;
        float k = Mathf.SmoothStep(0.0f, 1.0f, Mathf.Min(pull.T, 1.0f));
        pull.Hull.Rig.Position = Foot(pull.From.Lerp(pull.To, k));
        if (pull.T < 1.0f)
            return;
        _pull = null;
        PullLanded(pull);
    }

    /// <summary>The pulled hull on its hex: the core's events after the pull,
    /// played on it - the ambushes' rounds by <see cref="AmbushPlay"/>.</summary>
    private void PullLanded(Pull pull)
    {
        Parked p = pull.Hull;
        BattleState st = pull.State;
        for (int i = pull.Next; i < st.Events.Count; i++)
        {
            BattleEvent e = st.Events[i];
            switch (e.Kind)
            {
                case BattleEventKind.SmokeGone:
                    SmokeGone(e.Cell, "the winch dragged a hull on to it");
                    break;
                case BattleEventKind.MineBlown or BattleEventKind.MineCleared:
                    if (_mines.Find(m => m.Cell == e.Cell && !m.Spent) is { } mine)
                        Blow(mine);
                    else
                        _field?.SetCoverState(e.Cell, CoverState.Cleared);
                    break;
                case BattleEventKind.Knocked or BattleEventKind.Drowned when e.Unit == p.Id:
                    if (p.Fate == Fate.Alive)
                        UnitStruck(p, pierced: true);
                    else if (_field is not null && _field.IsDeep(p.Cell))
                        Founder(p.Model, p.Cell);
                    break;
                case BattleEventKind.Ignited when e.Unit == p.Id:
                    Ignite(p.Id, p.Side, p.Tag, p.Cell);
                    break;
                case BattleEventKind.Doused when e.Unit == p.Id:
                    Quench(p.Id, steam: true);
                    break;
                case BattleEventKind.WoodAlight:
                    _wildfire?.Light(e.Cell);
                    break;
                case BattleEventKind.AmbushFired:
                    AmbushPlay(st, i);
                    return;
            }
        }
    }

    // --- the panel ---------------------------------------------------------------------

    /// <summary>A kit act's mode on or off: on, a click on a hex is it.</summary>
    private void GearToggle(Gear g)
    {
        if (_gearMode != g && MatchBonusSpent)
        {
            _orderNote = BonusGoneSay("gear.why_bonus_spent");
            return;
        }
        _gearMode = _gearMode == g ? Gear.None : g;
        if (_gearMode != Gear.None)
        {
            _ramMode = false;
            _smokeMode = false;
            _ambushMode = _attackMode = false;
        }
        _orderNote = _gearMode switch
        {
            Gear.Mine => L.T("gear.mode_mine"),
            Gear.Repair => L.T("gear.mode_repair"),
            Gear.Winch => L.T("gear.mode_winch", Battle.WinchReach),
            Gear.Pivot => L.T("gear.mode_pivot"),
            _ => L.T("gear.mode_off"),
        };
    }

    /// <summary>The left button with a kit act on: the act on the hex under it.</summary>
    private bool GearClick(Vector2 screen)
    {
        if (_gearMode == Gear.None)
            return false;
        if (PickAt(screen) is not var (point, onTank, onOwn) || _field is null)
            return true;
        Vector2I cell = onTank && _other is { } t ? OtherCell(t) : onOwn ? CellHere : _field.FlatCellAt(Board(point));
        // the double turn's command is the side, as the core's order
        if (_gearMode == Gear.Pivot)
        {
            int side = HexField.HeadingTo(CellHere, cell);
            if (side < 0)
                _orderNote = L.T("gear.why_sixty");
            else
                Execute(new Command(CommandKind.Pivot, Side: side));
            return true;
        }
        Execute(new Command(CommandOf(_gearMode), cell));
        return true;
    }

    private static CommandKind CommandOf(Gear g) => g switch
    {
        Gear.Mine => CommandKind.Mine,
        Gear.Repair => CommandKind.Repair,
        Gear.Pivot => CommandKind.Pivot,
        _ => CommandKind.Winch,
    };

    private static Gear GearOf(CommandKind k) => k switch
    {
        CommandKind.Mine => Gear.Mine,
        CommandKind.Repair => Gear.Repair,
        CommandKind.Pivot => Gear.Pivot,
        _ => Gear.Winch,
    };

    /// <summary>Why the forced engine may not be said now, or empty: the turn's
    /// gates, then the core's own (<see cref="Battle.Overdrive"/>).</summary>
    private string OverdriveWhy()
    {
        if (_field is null || _lineup.Count == 0)
            return L.T("gear.why_no_match");
        if (_fate != Fate.Alive)
            return L.T("gear.why_out", _modelTag);
        if (_side != _mt.Turn)
            return L.T("gear.why_turn", SideName(_mt.Turn));
        if (!RigHas(Kit.Overdrive))
            return L.T("gear.why_no_kit", _modelTag, L.T("gear.kit_overdrive"));
        if (MatchBonusSpent)
            return BonusGoneSay("gear.why_bonus_spent");
        if (_mt.Drove)
            return L.T("gear.why_overdrive_moved");
        if (MatchBusy)
            return L.T("blast.busy");
        return Battle.Overdrive(ShadowOfBench()) is { } no
            ? no == "pinned by a ram blade" ? L.T("match.pinned_no_move", _modelTag) : no
            : "";
    }

    /// <summary>The forced engine said (the command's executor): the bonus act
    /// spent, the move still to make - its ambushes timed by the core, which
    /// fires them only where it stops (<see cref="ShadowMove"/>).</summary>
    private void OverdriveGo()
    {
        if (OverdriveWhy() is { Length: > 0 } no)
        {
            _orderNote = no;
            return;
        }
        ShadowAct("overdrive", s => Battle.Overdrive(s));
        _mt.Bonus = true;
        _mt.Overdrive = true;
        _gearMode = Gear.None;
        _ramMode = _smokeMode = false;
        _orderNote = L.T("gear.overdrive_on");
        Log.Print($"tank3d: match: {SideName(_side)} {_modelTag}#{_unitId} says the forced engine - ambushes fire only where it stops");
    }

    /// <summary>Why the double turn to <paramref name="side"/> may not be had
    /// now, or empty: the gates of a kit act, then the core's own.</summary>
    private string PivotWhy(int side) =>
        side < 0 ? L.T("gear.why_sixty")
        : GearWhy(Gear.Pivot, HexField.Step(CellHere, side)) is { Length: > 0 } no ? no
        : MatchBusy ? L.T("blast.busy")
        : "";

    /// <summary>The double turn given (the command's executor): the core asked,
    /// the hull brought round as a turn on the spot is (<c>_turnGoal</c>) and
    /// the enemy's ambushes timed by the core (<see cref="ShadowMove"/>); the
    /// bonus act and the move spent.</summary>
    private void PivotTo(int side)
    {
        if (PivotWhy(side) is { Length: > 0 } no)
        {
            _orderNote = no;
            return;
        }
        _gearMode = Gear.None;
        _mt.Bonus = true;
        _mt.Drove = true;
        _route = null;
        _turnGoal = HeadingFor(CellHere, side);
        ShadowMove($"pivot to {side}", s => Battle.Pivot(s, side));
        _orderNote = "";
        Log.Print($"tank3d: match: {SideName(_side)} {_modelTag}#{_unitId} double turn on {MapAt(CellHere)} to {side}, no step");
    }
}
