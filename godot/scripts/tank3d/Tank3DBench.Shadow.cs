using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The rules core in the shadow of the match (docs/battle-core-plan.md, Д3):
/// what the bench plays, <see cref="Battle"/> plays too, and where the two
/// part the log says so - <c>tank3d: battle: …</c>.
/// <list type="bullet">
/// <item><b>Turn by turn, from the bench.</b> The core's state is taken off
/// the bench (<see cref="ShadowOfBench"/>) when the turn's first action is
/// recorded, so a divergence does not carry into the next turn.</item>
/// <item><b>The actions as the bench gives them</b>: the take (<c>TakeOver</c>),
/// the route with its last side (<c>Drive</c>), the turn on the spot, the
/// round with the hex it was laid on (<c>Launch</c>), the ram, the fire put
/// out. Each is applied to the core as it is given.</item>
/// <item><b>Read twice.</b> As the turn is handed over, the units and the
/// board against the bench (what the turn did); then the core ends its turn
/// (burning out, the heavy's wood, the field's tick) and that is read against
/// the bench as the next turn begins (what the end of the turn did).</item>
/// </list>
/// The bench decides every outcome still; the log is the list of places it
/// leaves the GDD, for Д4 to close. A wood's fire runs by seconds on the bench
/// and by rounds in the core (П-16), so the woods are counted apart.
/// </summary>
public sealed partial class Tank3DBench
{
    private BattleState? _shadow;
    /// <summary>The core's turn ended, waiting for the bench's next to begin.</summary>
    private BattleState? _shadowEnded;
    private readonly List<string> _shadowSaid = new();
    private int _shadowTurns, _shadowDiverged;

    private static Camp CampOf(Side s) => s == Side.Green ? Camp.Green : Camp.Brown;

    /// <summary>The flat side a bench heading (degrees, 0 facing the camera)
    /// points at on <paramref name="cell"/> - the nearest of the six.</summary>
    private int HexSideOf(Vector2I cell, float heading)
    {
        float h = Mathf.DegToRad(heading);
        var way = new Vector3(Mathf.Sin(h), 0.0f, Mathf.Cos(h));
        int best = HexField.EdgeHeadings[0];
        float dot = -2.0f;
        foreach (int side in HexField.EdgeHeadings)
        {
            float d = HexWay(cell, side).Dot(way);
            if (d > dot)
            {
                dot = d;
                best = side;
            }
        }
        return best;
    }

    private UnitFate FateOf(Fate f, Vector2I cell) => f switch
    {
        Fate.Alive => UnitFate.Alive,
        Fate.Destroyed => UnitFate.Destroyed,
        _ => _field!.IsDeep(cell) && _field.RampHeading(cell) < 0 ? UnitFate.Drowned : UnitFate.Knocked,
    };

    private int BurnSinceOf(int id)
    {
        foreach (Blaze b in _blazes)
            if (!b.Out && b.Id == id)
                return b.Since;
        return 0;
    }

    /// <summary>The match as the bench has it now, as the core's state.</summary>
    private BattleState ShadowOfBench()
    {
        var s = new BattleState
        {
            Board = BattleBoard.Of(_field!),
            Turn = CampOf(_mt.Turn), Round = _mt.Round, TurnNo = _mt.TurnNo,
            Drove = _mt.Drove, Fired = _mt.Fired, Bonus = _mt.Bonus, LastStrike = _mt.LastStrike,
            // the control point (Tank3DBench.Point)
            Point = _pointCells, Score = (int[])_mt.Score.Clone(), LastShift = _mt.LastShift,
            Holder = _mt.Holder is Side held ? CampOf(held) : null,
            Won = _mt.Won is Side w ? CampOf(w) : null, Drawn = _mt.Drawn,
            Active = _side == _mt.Turn && _unitId >= 0 ? _unitId : null,
        };
        void Add(int id, string tag, MovementProfile cls, Side side, Vector2I cell, int heading, Fate fate)
        {
            if (id < 0)
                return;
            s.Units.Add(new BattleUnit
            {
                Id = id, Tag = tag, Class = cls, Side = CampOf(side), Cell = cell, Heading = heading,
                Fate = FateOf(fate, cell), Burning = fate == Fate.Alive && Burning(id), BurnSince = BurnSinceOf(id),
                Kit = id < _kits.Length ? _kits[id] : Kit.None,
                Watch = _ambush.TryGetValue(id, out var w) ? w.A : -1,
                Watch2 = _ambush.TryGetValue(id, out var w2) ? w2.B : -1,
                WatchShots = _ambush.TryGetValue(id, out var w3) ? w3.Shots : 0,
            });
        }
        Add(_unitId, _modelTag, _profile, _side, CellHere, HullSide(), _fate);
        foreach (Parked p in _parked)
            Add(p.Id, p.Tag, ClassFor(p.Tag), p.Side, p.Cell, HexSideOf(p.Cell, p.Heading), p.Fate);
        if (_other is { Unit: true } o)
            Add(o.Id, o.Tag, o.Profile, o.Side, OtherCell(o), HexSideOf(OtherCell(o), o.Heading), o.Fate);
        s.Units.Sort((a, b) => a.Id.CompareTo(b.Id));
        for (int r = 0; r < s.Board.Rows; r++)
            for (int q = 0; q < s.Board.Columns; q++)
                if (s.Board.CoverStateAt(new Vector2I(q, r)) == CoverState.Burning)
                    s.Fires[new Vector2I(q, r)] = 2;
        foreach (KeyValuePair<Vector2I, int> smoke in _smokeLeft)
            s.Smoke[smoke.Key] = smoke.Value;
        return s;
    }

    /// <summary>One action of the turn, given on the bench, applied to the core.</summary>
    private void ShadowAct(string what, System.Func<BattleState, string?> act, bool order = true)
    {
        // an order to the unit lifts its ambush (Tank3DBench.Ambush); taking it does not
        if (order)
            AmbushLift(_unitId, what);
        if (!Match || _field is null || _lineup.Count == 0)
            return;
        if (_shadow is null)
        {
            // later than the turn's first quiet frame (ShadowTick): the bench
            // may have marked this very action spent already
            Log.Print($"tank3d: battle: turn {_mt.TurnNo}: {what} before the turn was read - taken off the bench now");
            ShadowBegin();
        }
        int from = _shadow!.Events.Count;
        if (act(_shadow) is { } refused)
            Log.Print($"tank3d: battle: turn {_mt.TurnNo}: the core refuses {what}: {refused}");
        else if (_shadow.Events.Count > from)
            Log.Print($"tank3d: battle: {what}: " + string.Join(", ", _shadow.Events.Skip(from)));
    }

    /// <summary>The turn's state off the bench, and the last turn's end read against it.</summary>
    private void ShadowBegin()
    {
        BattleState now = ShadowOfBench();
        if (_shadowEnded is { } ended)
            ShadowRead("the end of the last turn", ended, now);
        _shadowEnded = null;
        _shadow = now;
    }

    /// <summary>Every frame of a match (<c>MatchTick</c>): the turn is read off
    /// the bench on its first quiet frame - the hand-over done, the burning
    /// out and its chain gone up, nothing of the turn spent yet.</summary>
    private void ShadowTick(bool busy)
    {
        if (_shadow is null && !busy && _pointDue is null && _field is not null && _lineup.Count > 0 && !MatchOver
            && _passIn is null && !_mt.Drove && !_mt.Fired && !_mt.Bonus)
            ShadowBegin();
        // the match over at the hand-over: the last turn's end - the outcome
        // with it - read all the same, once the bench has settled
        else if (MatchOver && _shadowEnded is { } ended && !busy && _pointDue is null)
        {
            ShadowRead("the end of the last turn", ended, ShadowOfBench());
            _shadowEnded = null;
        }
    }

    /// <summary>The turn handed over: the core against the bench, then the
    /// core's own end of the turn, read when the bench's next turn begins.</summary>
    private void ShadowTurnOver()
    {
        if (!Match || _field is null || _lineup.Count == 0)
            return;
        // a turn passed with nothing done is a turn too
        if (_shadow is null)
            ShadowBegin();
        if (_shadow is not { } s)
            return;
        ShadowRead($"turn {s.TurnNo}", s, ShadowOfBench());
        Battle.EndTurn(s);
        _shadowEnded = s;
        _shadow = null;
    }

    /// <summary>The move taken back (Tank3DBench.Undo): the core's turn goes
    /// with it and is taken again off the bench at the next action.</summary>
    private void ShadowDrop() => _shadow = null;

    private void ShadowRead(string when, BattleState core, BattleState bench)
    {
        _shadowSaid.Clear();
        int woods = 0;
        if (core.LastStrike != bench.LastStrike)
            _shadowSaid.Add($"last shot at a tank core round {core.LastStrike} bench {bench.LastStrike}");
        if (core.Point is not null && (core.Score[0] != bench.Score[0] || core.Score[1] != bench.Score[1]
                                       || core.Holder != bench.Holder || core.LastShift != bench.LastShift))
            _shadowSaid.Add($"point core {core.Holder?.ToString() ?? "nobody"} {core.Score[0]}:{core.Score[1]} since {core.LastShift}"
                            + $" bench {bench.Holder?.ToString() ?? "nobody"} {bench.Score[0]}:{bench.Score[1]} since {bench.LastShift}");
        if (core.Drawn != bench.Drawn || core.Won != bench.Won)
            _shadowSaid.Add($"outcome core {(core.Drawn ? "drawn" : core.Won?.ToString() ?? "on")}"
                            + $" bench {(bench.Drawn ? "drawn" : bench.Won?.ToString() ?? "on")}");
        foreach (BattleUnit c in core.Units)
        {
            if (bench.Unit(c.Id) is not { } b)
            {
                _shadowSaid.Add($"#{c.Id} missing on the bench");
                continue;
            }
            if (c.Fate != b.Fate)
                _shadowSaid.Add($"#{c.Id} {c.Tag} fate core {c.Fate} bench {b.Fate}");
            if (c.Standing && c.Cell != b.Cell)
                _shadowSaid.Add($"#{c.Id} {c.Tag} at core {MapAt(c.Cell)} bench {MapAt(b.Cell)}");
            if (c.Fate == UnitFate.Alive && b.Fate == UnitFate.Alive && c.Heading != b.Heading)
                _shadowSaid.Add($"#{c.Id} {c.Tag} facing core {c.Heading} bench {b.Heading}");
            if (c.Burning != b.Burning)
                _shadowSaid.Add($"#{c.Id} {c.Tag} burning core {c.Burning} bench {b.Burning}");
            if (c.Watch != b.Watch || c.Watch2 != b.Watch2)
                _shadowSaid.Add($"#{c.Id} {c.Tag} watching core {c.Watch}/{c.Watch2} bench {b.Watch}/{b.Watch2}");
        }
        for (int r = 0; r < core.Board.Rows; r++)
            for (int q = 0; q < core.Board.Columns; q++)
            {
                var cell = new Vector2I(q, r);
                if (core.Board.Smoked(cell) != bench.Board.Smoked(cell))
                    _shadowSaid.Add($"smoke {MapAt(cell)} core {core.Board.Smoked(cell)} bench {bench.Board.Smoked(cell)}");
                if (core.Board.SidesAt(cell) != bench.Board.SidesAt(cell))
                    _shadowSaid.Add($"walls {MapAt(cell)} core {core.Board.SidesAt(cell)} bench {bench.Board.SidesAt(cell)}");
                CoverState cs = core.Board.CoverStateAt(cell), bs = bench.Board.CoverStateAt(cell);
                if (cs == bs)
                    continue;
                if (core.Board.CoverAt(cell) == Cover.Forest)
                    woods++;
                else
                    _shadowSaid.Add($"{core.Board.CoverAt(cell)} {MapAt(cell)} core {cs} bench {bs}");
            }
        _shadowTurns++;
        var line = new StringBuilder($"tank3d: battle: {when}: ");
        if (_shadowSaid.Count == 0)
            line.Append("agrees");
        else
        {
            _shadowDiverged++;
            line.Append("diverged - ").Append(string.Join("; ", _shadowSaid));
        }
        if (woods > 0)
            line.Append($" (woods differ on {woods} hex(es) - the bench's fire runs by seconds, П-16)");
        Log.Print(line.ToString());
    }
}
