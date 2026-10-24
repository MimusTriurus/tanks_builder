using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The 2D harness's way of driving and fighting, on the model: orders by the
/// mouse on cells, a route through the board, and the gun by the rules. The
/// other way - WASD, Q/E, the tank driven like a car - stays, behind the
/// panel's switch and <c>--free-drive</c>.
///
/// <list type="bullet">
/// <item><b>The left button on a cell is an order to go there</b>: a route of
/// cells (<see cref="HexField.FindPath"/>, breadth first over the six sides -
/// the board's passing rule, its masonry, the bulldozer's licence, the target's
/// cell barred), driven as <c>TankTick.AdvanceOrder</c> drives it: off a bend
/// at the cornering crawl (<see cref="MovementProfile.CornerSpeed"/>) while the
/// hull swings on to the next leg, then up to the cap and down again under the
/// braking ceiling <c>√(end² + 2·a·run)</c> to the next bend - the crawl there,
/// nought at the end. The cap is the lowest, never the product, of the class's
/// cruise, a grade's, a ford's, a swim's and the ground's
/// (<see cref="RouteCap"/>, <c>TankTick.SpeedCap</c>).</item>
/// <item><b>A double click on the target is the ram</b>: the route into its
/// cell, the last leg at full speed, and the hulls' contact
/// (<see cref="RamMeets"/>) does the rest, as the T key's ram.</item>
/// <item><b>The right button is a shot</b>, as it always was here
/// (<see cref="AimCell"/>), with the 2D gate in front of it: the lane walked
/// by <see cref="Gunnery.Solve(HexField, Vector2I, Vector2I, bool, System.Func{Vector2I, bool}, bool)"/>
/// - the wood or smoke, the rise, the hull in the way; and a shot given while
/// the tank drives waits for it to stop, then is laid from where it stopped.</item>
/// <item><b>The turret by the rules</b> (GDD units.md, «Башня»): on the move it
/// comes round to the front, on a shot it goes on to the axis; Q/E are the
/// free mode's.</item>
/// <item><b>W/S</b> a cell along or against the hull, <b>A/D</b> sixty degrees
/// on the spot, <b>ESC</b> the order cancelled.</item>
/// </list>
/// </summary>
public sealed partial class Tank3DBench
{
    /// <summary>Orders by the mouse on cells (true) or the tank driven by the
    /// keys (false, <c>--free-drive</c>).</summary>
    private bool _rules = true;

    /// <summary>The cells the order goes through, its start not among them,
    /// and the next one's index; null with no order.</summary>
    private List<Vector2I>? _route;
    private int _routeStep;
    /// <summary>The cell the leg being driven started from.</summary>
    private Vector2I _routeFrom;
    /// <summary>The order ends in the target: the last leg is not braked for.</summary>
    private bool _routeRams;
    /// <summary>The order is the step back: the hull keeps its heading and the
    /// tank drives astern (Steps, «назад на 1»).</summary>
    private bool _routeBack;
    /// <summary>A shot given on the move: laid and fired from where it stops.</summary>
    private Vector2I? _shotWaiting;
    /// <summary>A turn on the spot ordered by A/D: the heading it ends at.</summary>
    private float? _turnGoal;
    private string _orderNote = "";

    private bool RouteMoving => _route is not null;

    /// <summary>The panel's switch: on to the cells, or back to the keys. Either
    /// way the order in hand is dropped.</summary>
    private void SetRules(bool on, string why = "")
    {
        if (_rules == on)
            return;
        _rules = on;
        CancelOrder();
        GD.Print($"tank3d: movement {(on ? "by cells (2D)" : "free (WASD)")}{why}");
    }

    private void CancelOrder()
    {
        _route = null;
        _routeRams = false;
        _routeBack = false;
        _routeFace = null;
        _routeGoal = null;
        _shotWaiting = null;
        _turnGoal = null;
        _orderNote = "";
        Unlock();
    }

    /// <summary>An order to go to <paramref name="cell"/> - or, with
    /// <paramref name="ram"/>, into the target standing on it.</summary>
    private void OrderTo(Vector2I cell, bool ram = false, int? face = null)
    {
        if (_field is null || _fate != Fate.Alive || RamRunning)
            return;
        // a match's turn: one move, by the side whose turn it is (Tank3DBench.Match)
        if (MatchMoveWhy() is { Length: > 0 } no)
        {
            _orderNote = no;
            return;
        }
        if (Match && ram)
        {
            _orderNote = "таран в партии ещё не сделан";
            return;
        }
        Vector2I here = CellHere;
        // the target and the standing tanks (Tank3DBench.Parked)
        HashSet<Vector2I> barred = Barred(ram);
        _turnGoal = null;
        _routeBack = false;
        _routeGoal = null;
        if (ram)
        {
            // The ram is an action (GDD classes.md, «Таран»), not a move: its run
            // keeps the board's plain way into the target's cell.
            List<Vector2I> path = _field.FindPath(here, cell, barred, masonry: true, bulldozer: _profile.Bulldozes);
            if (path.Count == 0)
            {
                _orderNote = $"к {cell} пути нет";
                GD.Print($"tank3d: no route {here} -> {cell}");
                return;
            }
            Drive(path, ram: true, $"таран {OtherCell(_other!)}");
            GD.Print($"tank3d: route {here} -> {cell} (ram): {string.Join(" ", path)}");
            return;
        }

        // A move by the steps (Steps, docs/hud3d-plan.md): the class's budget,
        // what each cell costs, where the move has to end.
        Steps.Reach reach = Steps.Plan(_field, new Steps.Pose(here, HullSide()),
                                       Steps.Mover.Of(_profile, _amphibious, barred));
        // The way shown on the board, if it is to this cell (Tab's pick among
        // equal ways, Tank3DBench.Course) - else the straightest.
        Steps.Route? shown = ShownWay(cell);
        if (shown is not null && shown.Legs[0].At != reach.Start)
            shown = null;
        if ((shown ?? reach.RouteTo(cell)) is not { } route)
        {
            bool anyWay = _field.FindPath(here, cell, barred, masonry: true, bulldozer: _profile.Bulldozes).Count > 0;
            _orderNote = cell == here ? "" : anyWay ? $"до {cell} не хватает шагов ({reach.Budget})" : $"к {cell} пути нет";
            GD.Print($"tank3d: no move {here} -> {cell}: {(anyWay ? $"over the budget of {reach.Budget}" : "no way")}");
            return;
        }
        _routeBack = route.Back;
        // The rose's side, turned to on arrival out of the same budget
        // (Tank3DBench.Ghost) - only if the budget has it.
        int turn = face is int f ? reach.TurnTo(route, f) ?? 0 : 0;
        _routeFace = turn > 0 ? face : null;
        _routeGoal = (cell, _routeFace ?? route.End.Heading);
        string risk = route.Risk == Steps.Risk.None ? "" : $", {route.Risk}";
        Drive(route.Cells(), ram: false,
              $"к {cell}: {route.Spent + turn} из {reach.Budget} шагов{(route.Back ? ", назад" : "")}"
              + $"{(turn > 0 ? $", разворот {turn}" : "")}{risk}");
        GD.Print($"tank3d: move {here} -> {cell}: {route}{(turn > 0 ? $" then t{face} (+{turn})" : "")} of {reach.Budget}{risk}"
                 + $" ({reach.RoutesTo(cell).Count} way(s) of that cost)");
    }

    private void Drive(List<Vector2I> path, bool ram, string note)
    {
        _route = path;
        _routeStep = 0;
        _routeFrom = CellHere;
        _routeRams = ram;
        _orderNote = note;
        if (!ram)
            MatchMoved();
    }

    /// <summary>The flat side the hull points at - the nearest of the six to
    /// its heading (OrderStep's reading).</summary>
    private int HullSide()
    {
        Vector2I here = CellHere;
        float h = Mathf.DegToRad(_heading);
        var way = new Vector3(Mathf.Sin(h), 0.0f, Mathf.Cos(h));
        int best = HexField.EdgeHeadings[0];
        float dot = -2.0f;
        foreach (int side in HexField.EdgeHeadings)
        {
            float d = HexWay(here, side).Dot(way);
            if (d > dot)
            {
                dot = d;
                best = side;
            }
        }
        return best;
    }

    /// <summary>W/S: one cell along the hull or against it - the nearest of the
    /// six sides to where the hull points.</summary>
    private void OrderStep(bool back)
    {
        if (_field is null)
            return;
        Vector2I here = CellHere;
        int best = -1;
        float dot = -2.0f;
        float h = Mathf.DegToRad(_heading + (back ? 180.0f : 0.0f));
        var way = new Vector3(Mathf.Sin(h), 0.0f, Mathf.Cos(h));
        foreach (int side in HexField.EdgeHeadings)
        {
            float d = HexWay(here, side).Dot(way);
            if (d > dot)
            {
                dot = d;
                best = side;
            }
        }
        if (best >= 0)
            OrderTo(HexField.Step(here, best));
    }

    /// <summary>A/D: sixty degrees on the spot, to the next of the six sides.</summary>
    private void OrderTurn(bool left)
    {
        if (_fate != Fate.Alive)
            return;
        // in a match a turn on the spot is the turn's move (Tank3DBench.Match)
        if (MatchMoveWhy() is { Length: > 0 } no)
        {
            _orderNote = no;
            return;
        }
        MatchMoved();
        _route = null;
        _routeGoal = null;
        float from = _turnGoal ?? Mathf.Round(_heading / 60.0f) * 60.0f;
        _turnGoal = from + (left ? 60.0f : -60.0f);
    }

    /// <summary>The fastest the tank may go on the leg from
    /// <paramref name="from"/> to <paramref name="to"/>: <c>TankTick.SpeedCap</c>
    /// - the lowest, never the product.</summary>
    private float RouteCap(Vector2I from, Vector2I to)
    {
        float cap = MaxSpeed;
        if (_field is null)
            return cap;
        if (_field.IsGrade(from, to))
            cap = Mathf.Min(cap, (float)_profile.GradeSpeed);
        if (_field.IsWet(from, to))
            cap = Mathf.Min(cap, (float)_profile.WaterSpeed);
        if (_field.IsDeepLeg(from, to) || Swimming)
            cap = Mathf.Min(cap, (float)_profile.SwimSpeed);
        float ground = TerrainRules.Speed(_field.FaceAt(to));
        if (ground < 1.0f)
            cap = Mathf.Min(cap, MaxSpeed * ground);
        return cap;
    }

    /// <summary>The straight run left to the first bend and what the tank must
    /// be doing there: the crawl at a bend, nought at the end - or, ramming,
    /// its cruise into the hull.</summary>
    private (float Run, float End) RouteRun(Vector3 at)
    {
        List<Vector2I> path = _route!;
        Vector2I next = path[_routeStep];
        float run = Flat(CellWorld(next) - at).Length();
        int heading = HexField.HeadingTo(_routeFrom, next);
        int i = _routeStep;
        for (; i + 1 < path.Count; i++)
        {
            if (HexField.HeadingTo(path[i], path[i + 1]) != heading)
                break;
            run += Flat(CellWorld(path[i + 1]) - CellWorld(path[i])).Length();
        }
        bool last = i + 1 >= path.Count;
        float end = !last ? (float)_profile.CornerSpeed : _routeRams ? MaxSpeed : 0.0f;
        return (run, end);
    }

    private static Vector3 Flat(Vector3 v) => new(v.X, 0.0f, v.Z);

    /// <summary>
    /// One frame of the route: the hull swung on to the leg at the crawl, or
    /// driven along it under the cap and the braking ceiling. Out: the speed it
    /// was making for, its acceleration as a share of the class's, the turn in
    /// degrees a second and the way covered in model units - what the frame
    /// does with any drive.
    /// </summary>
    private void RouteDrive(float dt, out float target, out float a, out float turn, out float step)
    {
        // Astern the leg is driven as ahead with the hull pointing the other way:
        // the speed is a magnitude inside and signed outside, as the keys' S
        // signs it.
        bool back = _routeBack;
        if (back)
            _speed = -_speed;
        RouteLeg(dt, back, out target, out a, out turn, out step);
        if (back)
        {
            _speed = -_speed;
            target = -target;
            step = -step;
            a = -a;
        }
    }

    private void RouteLeg(float dt, bool back, out float target, out float a, out float turn, out float step)
    {
        target = a = turn = step = 0.0f;
        if (_route is null || _field is null)
            return;
        Vector2I next = _route[_routeStep];
        Vector3 pos = _rig.Position;
        Vector3 goal = CellWorld(next);
        Vector3 to = Flat(goal - pos);
        float left = to.Length();
        float legHeading = HeadingOf((back ? -1.0f : 1.0f) * Flat(goal - CellWorld(_routeFrom)));
        float diff = (float)Angles.WrapAngle(legHeading - _heading);
        float before = _speed;
        if (Mathf.Abs(diff) > 0.5f && left > 1.0f)
        {
            // Slow to the crawl and swing round while still creeping; from a
            // stand that is a turn on the spot (TankTick.AdvanceOrder).
            float crawl = (float)_profile.CornerSpeed;
            if (_speed > crawl)
                _speed = Mathf.Max(crawl, _speed - Accel * dt);
            float budget = TurnRate * dt;
            float d = Mathf.Abs(diff) <= budget ? diff : Mathf.Sign(diff) * budget;
            Heading += d;
            turn = dt > 0.0f ? d / dt : 0.0f;
            a = _speed < before ? -1.0f : 0.0f;
        }
        else
        {
            // On the leg: the heading the way to the cell's middle, the speed
            // under the ceiling it can still stop (or crawl) from.
            if (left > 1.0f)
                Heading = HeadingOf(back ? -to : to);
            (float run, float end) = RouteRun(pos);
            float ceiling = Mathf.Sqrt(end * end + 2.0f * Accel * Mathf.Max(run, 0.0f));
            float allowed = Mathf.Min(Mathf.Min(RouteCap(_routeFrom, next), ceiling),
                                      MaxSpeed * MireCap);
            _speed = _speed > allowed
                ? Mathf.Max(allowed, _speed - Accel * dt)
                : Mathf.Min(_speed + Accel * dt, allowed);
            a = dt > 0.0f ? Mathf.Clamp((_speed - before) / (Accel * dt), -1.0f, 1.0f) : 0.0f;
        }
        target = _speed;

        float move = Mathf.Min(_speed * dt, left);
        Vector3 way = left > 1e-3f ? to / left : Vector3.Zero;
        Vector3 nextPos = pos + way * move;
        // Another hull in the way: up to it - and the ram, if that is the order
        // and the tank is making speed (Tank3DBench.Ram).
        if (RamMeets(ref nextPos))
        {
            _route = null;
            if (!RamRunning)
            {
                _speed = 0.0f;
                _orderNote = _routeRams ? "таран: не разогнался" : "путь занят";
            }
            else
                _orderNote = "";
            _routeRams = false;
            step = Flat(nextPos - pos).Length() / _model.PixelsPerUnit;
            _rig.Position = nextPos;
            return;
        }
        if (!CanDrive(pos, nextPos, way))
        {
            // The board said no after all - a wall the route did not know, a
            // capon's gate: stop where it is.
            _speed = 0.0f;
            _route = null;
            _orderNote = "дальше не проехать";
            return;
        }
        _rig.Position = nextPos;
        step = move / _model.PixelsPerUnit;
        if (left - move <= 0.5f)
        {
            _routeFrom = next;
            _routeStep++;
            if (_routeStep >= _route.Count)
            {
                _route = null;
                _routeRams = false;
                _routeBack = false;
                _speed = 0.0f;
                _orderNote = "";
                GD.Print($"tank3d: arrived at {next}, heading {_heading:F0}, off the middle {left - move:F1} px");
                // the rose's turn, on the spot
                if (_routeFace is int face)
                {
                    _turnGoal = HeadingFor(next, face);
                    _routeFace = null;
                }
                ShotOnArrival();
            }
        }
    }

    /// <summary>A/D's turn on the spot, as a steer the frame applies: -1..1.</summary>
    private float TurnGoalIn(float dt)
    {
        if (_turnGoal is not float goal)
            return 0.0f;
        float diff = (float)Angles.WrapAngle(goal - _heading);
        if (Mathf.Abs(diff) < 0.2f)
        {
            Heading = goal;
            _turnGoal = null;
            return 0.0f;
        }
        return dt > 0.0f ? Mathf.Clamp(diff / (TurnRate * 0.5f * dt), -1.0f, 1.0f) : 0.0f;
    }

    /// <summary>The turret by the rules: on the move it comes round to the
    /// front at the class's traverse (GDD units.md: «при движении танка башня
    /// выравнивается вперёд»).</summary>
    private void TurretByRules(float dt)
    {
        if (!_model.Turreted || _fate != Fate.Alive || !RouteMoving)
            return;
        float rate = Mathf.DegToRad((float)_profile.TurretRate) * dt;
        float yaw = Mathf.Wrap(_model.Yaw, -Mathf.Pi, Mathf.Pi);
        _model.Yaw = Mathf.Abs(yaw) <= rate ? 0.0f : yaw - Mathf.Sign(yaw) * rate;
    }

    /// <summary>The right button under the rules: on the move the shot waits
    /// for the stop; standing, it is laid now.</summary>
    private void ShotOrder(Vector2I cell)
    {
        // With a cell fixed the shot is fired from it: the move is ordered and
        // the shot waits for the stop. LT fires first and keeps the cell
        // (classes.md, «Свободный порядок LT»; Tank3DBench.Aim).
        // A firing post the bench fixed is LT's too: it cannot fire from here
        // (Tank3DBench.Post).
        if (_lockCell is not null && (!_profile.FreeOrder || _postLock))
            Confirm();
        if (RouteMoving)
        {
            _shotWaiting = cell;
            _aimNote = $"выстрел по {cell} — после остановки";
            return;
        }
        AimCell(cell);
    }

    private void ShotOnArrival()
    {
        if (_shotWaiting is not Vector2I cell)
            return;
        _shotWaiting = null;
        AimCell(cell);
    }

    /// <summary>The 2D gate on a direct shot (<c>Gunnery.Solve</c>): why the
    /// lane to <paramref name="cell"/> is shut, or empty. The walls are left to
    /// the round: on this board it hits them.</summary>
    private string LaneShut(Vector2I own, Vector2I cell, out Vector2I? stop)
    {
        stop = null;
        if (_field is null)
            return "";
        // In a match every unit still on the board stands in the lane, a
        // destroyed one does not (Tank3DBench.Match).
        Shot shot = Gunnery.Solve(_field, own, cell, _profile.Lobs,
                                  c => (_other is { } o && o.Fate != Fate.Destroyed && OtherCell(o) == c)
                                       || (Match && ParkedCells.Contains(c)), masonry: false);
        if (shot.Clear || shot.BlockedAt is not Vector2I at)
            return "";
        stop = at;
        return shot.Stop switch
        {
            Barrier.Tank => $"на линии огня танк {at}",
            Barrier.Ground => $"линию огня закрывает рельеф {at}",
            _ => $"линию огня закрывает {(_field.CoverAt(at) == Cover.Forest ? "лес" : "дым")} {at}",
        };
    }

    /// <summary>The left button under the rules, as the 2D harness reads it:
    /// the own tank - selected, or, selected already, its order cancelled;
    /// with nothing selected nothing else is an order; the target - a double
    /// click rams it; anywhere else - go.</summary>
    private void OrderClick(Vector2 screen, bool twice)
    {
        // A standing tank under the pointer is taken rather than driven at
        // (Tank3DBench.Parked).
        if (ParkedAt(screen) is { } standing)
        {
            // in a match, only one of the side whose turn it is (Tank3DBench.Match)
            if (MatchTakes(standing))
                TakeOver(standing);
            return;
        }
        // a disc of the rose round the fixed cell (Tank3DBench.Ghost)
        if (RoseAt(screen) is int side)
        {
            FaceTo(side);
            return;
        }
        if (PickAt(screen) is not var (point, onTank, onOwn) || _field is null)
            return;
        Vector2I cell = onTank && _other is { } t ? OtherCell(t)
            : onOwn ? CellHere : _field.FlatCellAt(Board(point));
        if (cell == CellHere)
        {
            if (!_chosen)
                Choose(true);
            else
                CancelOrder();
            return;
        }
        if (!_chosen)
        {
            _orderNote = "танк не выбран — щёлкни по нему";
            return;
        }
        if (_other is { } o && cell == OtherCell(o))
        {
            if (twice && !Match)
                OrderTo(cell, ram: true);
            return;
        }
        // A cell in reach takes two clicks: the first fixes it and shows the
        // ghost and the rose, the second on it orders the move
        // (Tank3DBench.Ghost). Out of reach the order says why not.
        if (CourseShown && _reach is { } reach && reach.Reaches(cell))
        {
            if (_lockCell == cell)
                Confirm();
            else
                Lock(cell);
            return;
        }
        OrderTo(cell);
    }
}
