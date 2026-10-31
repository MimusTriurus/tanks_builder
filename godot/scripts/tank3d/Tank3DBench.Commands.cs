using System;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The turn's orders as commands (docs/battle-core-plan.md, Д5): one record a
/// mouse, a key, a <c>--do cmd=…</c> line and - next - the AI give, one gate
/// that says why not (<see cref="CommandWhy"/>), one executor
/// (<see cref="Execute"/>) over the bench's own orders. None of them wants
/// the pointer: the ram finds its own way to the target, and the wall's ram is
/// the match's bonus act it was not.
///
/// <list type="bullet">
/// <item><b>Take</b> - unit by its id; <b>Go</b> - to a hex, turned to a side
/// at the end if the budget has it; <b>Turn</b> - on the spot; <b>Fire</b> - at
/// a hex; <b>Burn</b> - the incendiary round at one (Tank3DBench.Incendiary);
/// <b>Ram</b> - the unit on a hex, from where it stands or from a hex of
/// the move beside it; <b>RamWall</b> - through the wall ahead; <b>Ambush</b> -
/// on an axis, the medium's on two beside each other (Tank3DBench.Ambush); <b>Smoke</b> - the grenade on a
/// hex beside (Tank3DBench.Smoke); <b>Douse</b>;
/// <b>Pass</b> - the end of the turn; <b>Undo</b> - the move taken back.</item>
/// <item>Each lands on the bench where the mouse's lands - <c>OrderTo</c>,
/// <c>SpinTo</c>, <c>ShotOrder</c>, <c>RamOrder</c>, <c>WallRamGo</c>,
/// <c>Douse</c>, <c>EndTurn</c>, <c>UndoMove</c> - so the rules core in the
/// shadow sees a command as it sees a click (Tank3DBench.Shadow).</item>
/// <item>The keys go through here (J, Ctrl+Enter, Ctrl+Z, 1-5 the roster);
/// the board's clicks still call the orders themselves - the same orders.</item>
/// </list>
/// </summary>
public sealed partial class Tank3DBench
{
    private enum CommandKind { Take, Go, Turn, Fire, Burn, Ram, RamWall, Ambush, Smoke, Douse, Pass, Undo }

    /// <summary>A command: what, and its hex (bench cells), side (a world
    /// heading of <see cref="HexField.EdgeHeadings"/>, -1 for none) or unit;
    /// <paramref name="Side2"/> the medium's second axis.</summary>
    private readonly record struct Command(CommandKind Kind, Vector2I Cell = default, int Side = -1, int Unit = -1, int Side2 = -1)
    {
        public override string ToString() => Kind switch
        {
            CommandKind.Take => $"take #{Unit}",
            CommandKind.Go => $"go ({Cell.X},{Cell.Y}){(Side >= 0 ? $" facing {Side}" : "")}",
            CommandKind.Turn or CommandKind.RamWall => $"{Kind.ToString().ToLowerInvariant()} {Side}",
            CommandKind.Fire or CommandKind.Burn or CommandKind.Ram or CommandKind.Smoke => $"{Kind.ToString().ToLowerInvariant()} ({Cell.X},{Cell.Y})",
            CommandKind.Ambush => $"ambush {Side}{(Side2 >= 0 ? $"+{Side2}" : "")}",
            _ => Kind.ToString().ToLowerInvariant(),
        };
    }

    /// <summary>Why <paramref name="c"/> may not be given now, or empty - the
    /// bench's gates, gathered: whose turn, what is spent, what is busy.</summary>
    private string CommandWhy(Command c)
    {
        if (!Match)
            return "не партия";
        if (MatchOver)
            return "партия окончена";
        if (c.Kind is not (CommandKind.Pass or CommandKind.Undo or CommandKind.Take) && MatchBusy)
            return "дождись конца приказа";
        switch (c.Kind)
        {
            case CommandKind.Take:
                return UnitStanding(c.Unit) is { } p ? TakeWhy(p)
                    : c.Unit == _unitId ? "" : $"юнита #{c.Unit} нет на доске";
            case CommandKind.Go:
            case CommandKind.Turn:
                return _fate != Fate.Alive ? $"{_modelTag} не на ходу" : MatchMoveWhy();
            case CommandKind.Fire:
            case CommandKind.Burn:
                if (_fate != Fate.Alive)
                    return $"{_modelTag} не на ходу";
                if (c.Kind == CommandKind.Burn && !RigBurns)
                    return $"у {_modelTag} нет зажигательных";
                return ShotWhy(CellHere, c.Cell, out _, out _, out _);
            case CommandKind.Ram:
                if (_fate != Fate.Alive)
                    return $"{_modelTag} не на ходу";
                if (MatchBonusSpent)
                    return "доп. действие этого хода уже сделано";
                return UnitStanding(c.Cell) is null && !(_other is { Unit: true } o && OtherCell(o) == c.Cell)
                    ? $"на {MapAt(c.Cell)} некого таранить" : "";
            case CommandKind.RamWall:
                if (_fate != Fate.Alive)
                    return $"{_modelTag} не на ходу";
                if (_side != _mt.Turn)
                    return $"ходят {SideName(_mt.Turn)}";
                if (MatchBonusSpent)
                    return "доп. действие этого хода уже сделано";
                if (_field is null || !_field.Walled(CellHere, c.Side))
                    return "на этой грани нет стены";
                return HullSide() != c.Side ? "встань носом к стене" : "";
            case CommandKind.Ambush:
                return AmbushWhy(c.Side, c.Side2);
            case CommandKind.Smoke:
                return SmokeWhy(c.Cell);
            case CommandKind.Douse:
                return !Burning(_unitId) ? $"{_modelTag} не горит"
                    : _side != _mt.Turn ? $"ходят {SideName(_mt.Turn)}"
                    : _mt.Bonus ? "доп. действие этого хода уже сделано" : "";
            case CommandKind.Undo:
                return UndoWhy();
            default:
                return "";
        }
    }

    /// <summary>Give <paramref name="c"/>: true when it was taken; the reason
    /// in the order's note when not.</summary>
    private bool Execute(Command c)
    {
        string why = CommandWhy(c);
        if (why.Length > 0)
        {
            _orderNote = why;
            Log.Print($"tank3d: command {c}: refused - {why}");
            return false;
        }
        switch (c.Kind)
        {
            case CommandKind.Take:
                if (c.Unit == _unitId)
                    GlideTo(_rig.Position);
                else
                {
                    if (_other is { Unit: true } o && o.Id == c.Unit)
                        DropTarget();
                    if (UnitStanding(c.Unit) is { } p)
                        TakeOver(p);
                }
                break;
            case CommandKind.Go:
                SetRules(true, " (command)");
                OrderTo(c.Cell, face: c.Side >= 0 ? c.Side : null);
                break;
            case CommandKind.Turn:
                if (!TurnTo(c.Side))
                    return Failed(c);
                break;
            case CommandKind.Fire:
            case CommandKind.Burn:
                // the round is chosen as the button chooses it, and leaves with the shot
                _fieryMode = c.Kind == CommandKind.Burn;
                ShotOrder(c.Cell);
                break;
            case CommandKind.Ram:
                if (!RamAt(c.Cell))
                    return Failed(c);
                break;
            case CommandKind.RamWall:
                if (!WallRamGo(false))
                {
                    _orderNote = "стену так не протаранить";
                    return Failed(c);
                }
                // the match's bonus act (GDD field.md, «кирпичные стены»; Д5):
                // the shot of the turn stays
                _mt.Bonus = true;
                int side = c.Side;
                ShadowAct($"ram the wall {side}", s => Battle.RamWall(s, side));
                break;
            case CommandKind.Ambush:
                AmbushSet(c.Side, c.Side2);
                break;
            case CommandKind.Smoke:
                SmokeLay(c.Cell);
                break;
            case CommandKind.Douse:
                Douse();
                break;
            case CommandKind.Pass:
                EndTurn();
                break;
            case CommandKind.Undo:
                UndoMove();
                break;
        }
        Log.Print($"tank3d: command {c}");
        return true;
    }

    private bool Failed(Command c)
    {
        Log.Print($"tank3d: command {c}: not given - {_orderNote}");
        return false;
    }

    /// <summary>The standing unit of that id, or null.</summary>
    private Parked? UnitStanding(int id)
    {
        foreach (Parked p in _parked)
            if (p.Id == id)
                return p;
        return null;
    }

    private Parked? UnitStanding(Vector2I cell)
    {
        foreach (Parked p in _parked)
            if (p.Cell == cell && p.Fate != Fate.Destroyed)
                return p;
        return null;
    }

    /// <summary>The hull round to <paramref name="side"/> on the spot, out of
    /// the move's steps (<see cref="Steps.Turning"/> against the budget the
    /// hex leaves) - <c>SpinTo</c> without the rose.</summary>
    private bool TurnTo(int side)
    {
        if (_field is null || Array.IndexOf(HexField.EdgeHeadings, side) < 0)
            return false;
        if (Steps.Turning(HullSide(), side) == 0)
            return true;
        Steps.Mover mover = RigMover();
        int cost = Steps.TurnCost(HullSide(), side, mover);
        int budget = Steps.BudgetAt(_field, CellHere, mover);
        if (cost > budget)
        {
            _orderNote = "на этот разворот шагов нет";
            return false;
        }
        CancelOrder();
        MatchMoved();
        _route = null;
        _turnGoal = HeadingFor(CellHere, side);
        ShadowAct($"turn to {side}", s => Battle.Turn(s, side));
        return true;
    }

    /// <summary>The ram of the unit on <paramref name="cell"/> without the
    /// pointer: it made the target, the way to it found as the preview finds
    /// it (<see cref="FindRam"/>), and ordered.</summary>
    private bool RamAt(Vector2I cell)
    {
        if (!(_other is { Unit: true } t && OtherCell(t) == cell) && UnitStanding(cell) is { } p)
            MakeTarget(p);
        if (_other is not { Unit: true } o || OtherCell(o) != cell)
        {
            _orderNote = "цель не взять";
            return false;
        }
        // the ram on, as Tab has it: the approach may be a hex of the move
        bool wasOn = _ramMode;
        _ramMode = true;
        RamPlan? found = FindRam(o, cell);
        _ramMode = wasOn;
        if (found is not { } plan)
        {
            _orderNote = $"к {MapAt(cell)} для тарана пути нет";
            return false;
        }
        if (!plan.Allowed)
        {
            _orderNote = $"таран нельзя: {plan.Why}";
            return false;
        }
        RamLock(plan);
        _ramPlan = plan;
        return RamOrder(cell);
    }

    /// <summary><c>--do cmd=KIND[:Q,R][:SIDE]</c>, map cells: <c>take:ID</c>,
    /// <c>go:Q,R[:SIDE]</c>, <c>turn:SIDE</c>, <c>fire:Q,R</c>, <c>burn:Q,R</c>, <c>ram:Q,R</c>,
    /// <c>ramwall:SIDE</c>, <c>ambush:SIDE[:SIDE2]</c>, <c>smoke:Q,R</c>, <c>douse</c>, <c>pass</c>, <c>undo</c>.</summary>
    private Command? ParseCommand(string text)
    {
        string[] parts = text.Split(':');
        Vector2I Cell(string s)
        {
            string[] qr = s.Split(',');
            return MapCell(int.Parse(qr[0]), int.Parse(qr[1]));
        }
        try
        {
            return parts[0] switch
            {
                "take" => new Command(CommandKind.Take, Unit: int.Parse(parts[1])),
                "go" => new Command(CommandKind.Go, Cell(parts[1]), parts.Length > 2 ? int.Parse(parts[2]) : -1),
                "turn" => new Command(CommandKind.Turn, Side: int.Parse(parts[1])),
                "fire" => new Command(CommandKind.Fire, Cell(parts[1])),
                "burn" => new Command(CommandKind.Burn, Cell(parts[1])),
                "ram" => new Command(CommandKind.Ram, Cell(parts[1])),
                "ramwall" => new Command(CommandKind.RamWall, Side: int.Parse(parts[1])),
                "smoke" => new Command(CommandKind.Smoke, Cell(parts[1])),
                "ambush" => new Command(CommandKind.Ambush, Side: int.Parse(parts[1]),
                                        Side2: parts.Length > 2 ? int.Parse(parts[2]) : -1),
                "douse" => new Command(CommandKind.Douse),
                "pass" => new Command(CommandKind.Pass),
                "undo" => new Command(CommandKind.Undo),
                _ => null,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary><c>--commands</c>: what is left of the match's commands.</summary>
    private readonly System.Collections.Generic.Queue<string> _commands = new();
    private int _commandsQuiet;

    /// <summary>Every frame of a match: the next command once the bench is
    /// quiet - nothing under way, the turn not being handed over - for a few
    /// frames running, so what a shot or a ram sets off has settled. A pass
    /// waits for the next turn's first quiet frame the same way.</summary>
    private void CommandsTick(bool busy)
    {
        if (_commands.Count == 0)
            return;
        bool quiet = !busy && _passIn is null && _turnGoal is null && !RouteMoving && _built;
        _commandsQuiet = quiet ? _commandsQuiet + 1 : 0;
        if (_commandsQuiet < 10)
            return;
        _commandsQuiet = 0;
        string next = _commands.Dequeue();
        if (ParseCommand(next) is { } c)
            Execute(c);
        else
            Log.Print($"tank3d: --commands {next}: not a command");
        if (_commands.Count == 0)
            Log.Print("tank3d: --commands: all given");
    }

    /// <summary>The wall's ram is to be had: the bonus act open and a wall
    /// across the edge the hull faces - the button's reading, every frame.</summary>
    private bool WallAhead =>
        Match && _field is not null && _fate == Fate.Alive && _side == _mt.Turn && !MatchBonusSpent
        && _field.Walled(CellHere, HullSide());
}
