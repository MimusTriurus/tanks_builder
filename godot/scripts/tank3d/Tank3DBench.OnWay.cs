using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The shot on the move on the bench (GDD upgrades.md «Выстрел на ходу», LT,
/// «Рейд» 3). The rule is the core's (<see cref="Battle.OnTheWay"/>, then
/// <see cref="Battle.Move"/>): said before the move, fired as the hull comes on
/// to its hex, after what that hex and the ambushes did to it.
/// <list type="bullet">
/// <item><b>Given</b> by the button «На ходу» in the attack's plate, then three
/// clicks: the left button on the hex to fire from, the right on the target,
/// the left on where the move ends - or <c>onway:Q,R:AQ,AR</c> before a
/// <c>go</c>. The way is the cheapest one to the end through the firing hex
/// (<see cref="Steps.Reach.RoutesTo"/>); a dearer one through it is not looked
/// for.</item>
/// <item><b>Checked</b> by the core on a copy of the bench before the hull
/// moves: the hex on the way, the target on an axis from it, the lane open.</item>
/// <item><b>Driven</b> as two legs: up to the hex, the stop and the shot as any
/// shot given on the move (<c>ShotOnArrival</c>), then the rest of the way once
/// the round is down (<see cref="OnWayTick"/>). The core plays the whole move
/// with its shot at once (<c>Drive</c>, <c>ShadowMove</c>), so the round on
/// the bench is not played to it again (<see cref="OnWayLaunch"/>).</item>
/// </list>
/// </summary>
public sealed partial class Tank3DBench
{
    /// <summary>The panel's «На ходу» on: the clicks pick the hex, the target, the end.</summary>
    private bool _onWayMode;
    private Button? _actOnWay;
    /// <summary>The hex picked to fire from, and the target picked; null till picked.</summary>
    private Vector2I? _onWayAt, _onWayAim;
    /// <summary>The move under way stopped on its firing hex: the rest of its
    /// cells, its end's side and whether astern; null with none to drive.</summary>
    private List<Vector2I>? _onWayRest;
    private int? _onWayFace;
    private bool _onWayBack;
    /// <summary>The round about to leave is the move's: the core has fired it already.</summary>
    private bool _onWayFiring;

    private bool RigFiresOnMove => (KitOf(_unitId) & Kit.FireOnMove) != 0;

    /// <summary>«На ходу» on or off: on only with the move and the shot both to be had.</summary>
    private void OnWayToggle()
    {
        if (!_onWayMode && OnWayWhy() is { Length: > 0 } no)
        {
            _orderNote = no;
            return;
        }
        _onWayMode = !_onWayMode;
        _onWayAt = _onWayAim = null;
        if (_onWayMode)
            _ambushMode = _fieryMode = _smokeShotMode = false;
        _orderNote = _onWayMode ? L.T("onway.mode_on") : L.T("onway.mode_off");
    }

    /// <summary>Why the shot on the move may not be said now, or empty.</summary>
    private string OnWayWhy()
    {
        if (!RigFiresOnMove)
            return L.T("onway.none", _modelTag);
        if (MatchMoveWhy() is { Length: > 0 } move)
            return move;
        return MatchShotSpent ? L.T("onway.spent") : "";
    }

    /// <summary>The left button with «На ходу» on: the firing hex first, the
    /// move's end once the target is picked. True when the click was the mode's.</summary>
    private bool OnWayClick(Vector2I cell, int? face)
    {
        if (!_onWayMode || _field is null)
            return false;
        if (_onWayAt is not Vector2I at)
        {
            if (cell == CellHere)
            {
                _orderNote = L.T("onway.own_hex");
                return true;
            }
            _onWayAt = cell;
            _orderNote = L.T("onway.hex", MapAt(cell));
            Log.Print($"tank3d: on the way: fire from {MapAt(cell)}");
            return true;
        }
        if (_onWayAim is not Vector2I aim)
        {
            _orderNote = L.T("onway.aim_first");
            return true;
        }
        OnWayDrive(at, aim, cell, face);
        return true;
    }

    /// <summary>The right button with «На ходу» on: the target, once the hex
    /// is picked. True when the click was the mode's.</summary>
    private bool OnWayAimClick(Vector2I cell)
    {
        if (!_onWayMode)
            return false;
        if (_onWayAt is not Vector2I at)
        {
            _orderNote = L.T("onway.hex_first");
            return true;
        }
        if (AxisTo(at, cell).Axis < 0)
        {
            _orderNote = L.T("onway.off_axis", MapAt(cell), MapAt(at));
            return true;
        }
        _onWayAim = cell;
        _orderNote = L.T("onway.aim", MapAt(cell), MapAt(at));
        Log.Print($"tank3d: on the way: fire from {MapAt(at)} at {MapAt(cell)}");
        return true;
    }

    /// <summary>
    /// The move to <paramref name="cell"/> through <paramref name="at"/>, the
    /// round at <paramref name="aim"/> on the way: the cheapest way through
    /// the hex, the core's leave on a copy, then the core told and the first
    /// leg driven.
    /// </summary>
    private void OnWayDrive(Vector2I at, Vector2I aim, Vector2I cell, int? face)
    {
        if (OnWayWhy() is { Length: > 0 } no)
        {
            _orderNote = no;
            return;
        }
        Vector2I here = CellHere;
        Steps.Reach reach = Steps.Plan(_field!, new Steps.Pose(here, HullSide()), RigMover(Barred()));
        Steps.Route? route = null;
        List<Vector2I>? cells = null;
        foreach (Steps.Route way in reach.RoutesTo(cell, 32))
        {
            List<Vector2I> through = way.Cells();
            if (through.Contains(at))
            {
                route = way;
                cells = through;
                break;
            }
        }
        if (route is null || cells is null)
        {
            _orderNote = L.T("onway.not_on_way", MapAt(cell), MapAt(at));
            Log.Print($"tank3d: on the way: no cheapest way {MapAt(here)} -> {MapAt(cell)} goes through {MapAt(at)}");
            return;
        }
        int turn = face is int f ? reach.TurnTo(route, f) ?? 0 : 0;
        int? endFace = turn > 0 ? face : null;
        int side = endFace ?? route.End.Heading;
        bool back = route.Back;
        if (Match && _lineup.Count > 0)
        {
            BattleState st = ShadowOfBench();
            if ((Battle.OnTheWay(st, at, aim) ?? Battle.Move(st, cells, side, back)) is { } refused)
            {
                _orderNote = L.T("onway.refused", MapAt(at), MapAt(aim));
                Log.Print($"tank3d: on the way: the core refuses {MapAt(at)} at {MapAt(aim)}: {refused}");
                return;
            }
        }
        ShadowAct($"on the way {MapAt(at)} at {MapAt(aim)}", s => Battle.OnTheWay(s, at, aim));
        _onWayMode = false;
        _onWayAt = _onWayAim = null;
        _fieryMode = _smokeShotMode = _attackMode = false;
        _turnGoal = null;
        _routeBack = back;
        _routeFace = endFace;
        _routeGoal = (cell, side);
        Drive(cells, ram: false, L.T("onway.go", MapAt(cell), MapAt(at), MapAt(aim)));
        // the first leg ends on the firing hex; the rest waits for the round
        int k = cells.IndexOf(at);
        if (k + 1 < cells.Count)
        {
            _route = cells.GetRange(0, k + 1);
            _onWayRest = cells.GetRange(k + 1, cells.Count - k - 1);
            _onWayFace = _routeFace;
            _onWayBack = back;
            _routeFace = null;
        }
        _shotWaiting = aim;
        _onWayFiring = true;
        Log.Print($"tank3d: on the way: {MapAt(here)} -> {MapAt(cell)} through {MapAt(at)}, firing at {MapAt(aim)}: {MapAt(cells, " ")}");
    }

    /// <summary>The round leaves: true when it is the move's, already the core's.</summary>
    private bool OnWayLaunch()
    {
        bool mine = _onWayFiring;
        _onWayFiring = false;
        return mine;
    }

    /// <summary>Every frame of a match: the rest of the way driven once the
    /// hull has stopped on the firing hex and its round is down - or the shot
    /// was refused there; dropped if the hull is out.</summary>
    private void OnWayTick()
    {
        if (_onWayRest is null)
            return;
        if (_fate != Fate.Alive || MatchOver)
        {
            OnWayDrop();
            return;
        }
        if (RouteMoving || _turnGoal is not null || _shotWaiting is not null || _aim is not null
            || (_shells?.Rounds.Count ?? 0) > 0)
            return;
        _route = _onWayRest;
        _routeStep = 0;
        _routeFrom = CellHere;
        _routeFace = _onWayFace;
        _routeBack = _onWayBack;
        _onWayRest = null;
        _onWayFace = null;
        _onWayFiring = false;
        Log.Print($"tank3d: on the way: on to {MapAt(_route[^1])}");
    }

    /// <summary>Everything of the shot on the move let go.</summary>
    private void OnWayDrop()
    {
        _onWayMode = _onWayFiring = false;
        _onWayAt = _onWayAim = null;
        _onWayRest = null;
        _onWayFace = null;
    }
}
