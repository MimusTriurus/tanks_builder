using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The blast of a destroyed unit and the fire it lights (2026-10-24), GDD
/// states.md «Уничтожен» and «Горит».
///
/// <list type="bullet">
/// <item><b>The blast</b> (<see cref="Blast"/>) reaches the six hexes round
/// the wreck on its own level and does not ask whose they are: a unit there
/// still fighting <b>catches fire</b>, armour or not; a knocked one <b>goes up
/// itself</b> - a chain, each link a beat after the last
/// (<see cref="ChainBeat"/>) so the eye can follow it round.</item>
/// <item><b>Burning</b> (<see cref="Blaze"/>) belongs to the unit - its side
/// and class - not to a model: the model on the rig and the one standing are
/// loaded afresh at every hand-over, and the fire goes with the unit. It burns
/// until the end of its owner's next turn; if it is burning then, it is
/// destroyed - with a blast, as one finished off (<see cref="BurnOut"/>).
/// A unit set alight in its own side's turn has the next one to put it out.
/// </item>
/// <item><b>Putting it out</b> (<c>J</c> in a match, <see cref="Douse"/>) is
/// the turn's action, instead of the shot - after the move, LT whenever.</item>
/// </list>
///
/// Not yet: the blast setting a wood alight and taking the walls on the
/// wreck's edges down, a unit in water going out at once, a burning unit
/// setting the wood it drives through alight.
/// </summary>
public sealed partial class Tank3DBench
{
    /// <summary>The wait between one link of a chain and the next, s.</summary>
    private const float ChainBeat = 0.7f;

    private sealed class Blaze
    {
        public Side Side;
        public string Tag = "";
        public CelBurn Burn = null!;
        /// <summary>The turn it caught fire in (<see cref="_turnNo"/>).</summary>
        public int Since;
        /// <summary>Put out: the fire falls and the steam rises, then it goes.</summary>
        public bool Out;
        public float OutFor;
    }

    private readonly List<Blaze> _blazes = new();
    /// <summary>Blasts to come, a beat apart: a knocked hull the last blast
    /// reached, or (<c>Burnt</c>) a burning unit whose turn is over.</summary>
    private readonly List<(Vector2I Cell, float In, bool Burnt)> _chain = new();
    private readonly List<Vector3> _blazePorts = new();
    /// <summary>Turns played, the first nought: a fire's clock.</summary>
    private int _turnNo;

    /// <summary>The blasts' own pictures beside the bench's one: a chain blows
    /// while the last link's fire ball is still up.</summary>
    private readonly List<(CelDeath Death, float Age)> _blastBalls = new();
    private float _deathAge = 99.0f;

    // Loops, not Exists/Find with a lambda: both are asked every frame, per
    // unit, and a lambda over side and tag is a closure each call (BOT-3).
    private bool Burning(Side side, string tag)
    {
        foreach (Blaze b in _blazes)
            if (!b.Out && b.Side == side && b.Tag == tag)
                return true;
        return false;
    }

    /// <summary>The model the unit wears now: on the rig, standing or the target.</summary>
    private TankModel? ModelOf(Side side, string tag)
    {
        if (_side == side && _modelTag == tag)
            return _model;
        if (_other is { Unit: true } o && o.Side == side && o.Tag == tag)
            return o.Model;
        foreach (Parked p in _parked)
            if (p.Side == side && p.Tag == tag)
                return p.Model;
        return null;
    }

    // --- the blast ---------------------------------------------------------------

    /// <summary>The six hexes round <paramref name="at"/> on its level.</summary>
    private void Blast(Vector2I at)
    {
        if (!Match || _field is null)
            return;
        int level = _field.LevelAt(at);
        foreach (int side in HexField.EdgeHeadings)
        {
            Vector2I n = HexField.Step(at, side);
            if (!_field.InBounds(n) || _field.LevelAt(n) != level)
                continue;
            if (CellHere == n && _fate == Fate.Alive)
                Ignite(_side, _modelTag, n);
            else if (_other is { Unit: true } o && OtherCell(o) == n)
                Struck(o.Side, o.Tag, o.Fate, n);
            else if (_parked.Find(p => p.Cell == n && p.Fate != Fate.Destroyed) is { } p)
                Struck(p.Side, p.Tag, p.Fate, n);
        }
    }

    private void Struck(Side side, string tag, Fate fate, Vector2I cell)
    {
        if (fate == Fate.Alive)
            Ignite(side, tag, cell);
        else if (fate == Fate.Knocked && !_chain.Exists(c => c.Cell == cell))
        {
            _chain.Add((cell, ChainBeat, false));
            Log.Print($"tank3d: match: the blast reaches {SideName(side)} {tag} on {cell}, knocked - it goes up next");
        }
    }

    /// <summary><c>--do ignite=Q,R</c>: the unit fighting on that hex set
    /// alight as a blast would - for the stress run (BOT-6), not a move of
    /// the game.</summary>
    private void IgniteAt(Vector2I cell)
    {
        if (!Match)
            return;
        if (CellHere == cell && _fate == Fate.Alive)
            Ignite(_side, _modelTag, cell);
        else if (_other is { Unit: true, Fate: Fate.Alive } o && OtherCell(o) == cell)
            Ignite(o.Side, o.Tag, cell);
        else if (_parked.Find(p => p.Cell == cell && p.Fate == Fate.Alive) is { } p)
            Ignite(p.Side, p.Tag, cell);
        else
            Log.Print($"tank3d: --do ignite={cell.X},{cell.Y}: no unit fighting there");
    }

    /// <summary>A unit set alight.</summary>
    private void Ignite(Side side, string tag, Vector2I cell)
    {
        // a hull in water catches and goes out at once (GDD states.md «Уничтожен»)
        if (_field is not null && _field.IsWater(cell))
            return;
        if (Burning(side, tag) || ModelOf(side, tag) is not { } model)
            return;
        var burn = new CelBurn { Name = $"Blaze_{side}_{tag}" };
        AddChild(burn);
        burn.Build(model.HullLength * model.PixelsPerUnit);
        _blazes.Add(new Blaze { Side = side, Tag = tag, Burn = burn, Since = _turnNo });
        _matchNote = $"{SideName(side)} {tag} горит";
        Log.Print($"tank3d: match: {SideName(side)} {tag} on {cell} catches fire (turn {_turnNo})");
    }

    /// <summary>A unit's fire gone: put out (the steam), or the unit knocked
    /// out or destroyed under it (at once).</summary>
    private void Quench(Side side, string tag, bool steam)
    {
        foreach (Blaze b in _blazes.Where(b => !b.Out && b.Side == side && b.Tag == tag).ToList())
        {
            if (steam)
            {
                b.Burn.Douse();
                b.Out = true;
                continue;
            }
            b.Burn.QueueFree();
            _blazes.Remove(b);
        }
    }

    /// <summary>The fire ball of a blast: the bench's own picture when it is
    /// free, a spare one while it is still busy with the last.</summary>
    private void Boom(Vector3 at, float might, bool grounded)
    {
        if (_celDeath is null)
            return;
        if (_deathAge > 2.5f)
        {
            _celDeath.Blow(at, Foot(at), might, grounded);
            _deathAge = 0.0f;
            return;
        }
        var death = new CelDeath { Name = "Boom" };
        AddChild(death);
        death.Build(_model.HullLength * _model.PixelsPerUnit, HexWidth);
        death.Ground = w => Foot(w).Y;
        death.Blow(at, Foot(at), might, grounded);
        _blastBalls.Add((death, 0.0f));
    }

    // --- the turn's end -------------------------------------------------------------

    /// <summary>The owner's turn over (п. 6): every unit of <paramref name="side"/>
    /// still burning from before the turn began is destroyed.</summary>
    private void BurnOut(Side side, int turn)
    {
        float beat = 0.0f;
        foreach (Blaze b in _blazes.Where(b => !b.Out && b.Side == side && b.Since < turn).ToList())
        {
            Vector2I? at = _other is { Unit: true } o && o.Side == side && o.Tag == b.Tag ? OtherCell(o)
                : _parked.Find(p => p.Side == side && p.Tag == b.Tag)?.Cell;
            if (at is not Vector2I cell)
            {
                // still on the rig: nobody of the other side was there to take it
                Log.Print($"tank3d: match: {SideName(side)} {b.Tag} burns on the rig - left alight");
                continue;
            }
            // one after another, as the chain goes: in one frame they were one blast
            _chain.Add((cell, beat, true));
            beat += ChainBeat;
        }
    }

    /// <summary>The burning unit on <paramref name="cell"/> destroyed, if it is
    /// still there and still alight.</summary>
    private void BurnsOut(Vector2I cell)
    {
        if (_other is { Unit: true, Fate: Fate.Alive } o && OtherCell(o) == cell && Burning(o.Side, o.Tag))
            o.Fate = Explode(o.Model, o.Tag, cell, o.Side);
        else if (_parked.Find(p => p.Cell == cell && p.Fate == Fate.Alive) is { } p && Burning(p.Side, p.Tag))
            p.Fate = Explode(p.Model, p.Tag, cell, p.Side);
        else
            return;
        Log.Print($"tank3d: match: the unit on {cell} burnt out at the end of its side's turn");
        MatchCheckEnd();
    }

    /// <summary>J in a match: the fire on the unit on the rig put out - the
    /// turn's action.</summary>
    private void Douse()
    {
        if (!Match)
            return;
        string why = !Burning(_side, _modelTag) ? $"{_modelTag} не горит"
            : _side != _turn ? $"ходят {SideName(_turn)}"
            : _fired ? "действие этого хода уже сделано"
            : _passIn is not null ? "ход передаётся"
            : MatchBusy ? "дождись конца приказа"
            : "";
        if (why.Length > 0)
        {
            _orderNote = why;
            return;
        }
        Quench(_side, _modelTag, steam: true);
        _fired = true;
        _ramMode = false;
        Unlock();
        _orderNote = $"{_modelTag} потушен";
        Log.Print($"tank3d: match: {SideName(_side)} {_modelTag} puts its fire out");
    }

    // --- the frame -------------------------------------------------------------------

    private void BlastTick(float dt)
    {
        _deathAge += dt;
        for (int i = _blastBalls.Count - 1; i >= 0; i--)
        {
            (CelDeath death, float age) = _blastBalls[i];
            age += dt;
            death.Tick(dt, _camera.GlobalBasis);
            if (age > 6.0f)
            {
                death.QueueFree();
                _blastBalls.RemoveAt(i);
            }
            else
                _blastBalls[i] = (death, age);
        }
        // the chain's next links
        for (int i = _chain.Count - 1; i >= 0; i--)
        {
            (Vector2I cell, float left, bool burnt) = _chain[i];
            left -= dt;
            if (left > 0.0f)
            {
                _chain[i] = (cell, left, burnt);
                continue;
            }
            _chain.RemoveAt(i);
            if (burnt)
            {
                BurnsOut(cell);
                continue;
            }
            if (_other is { Unit: true, Fate: Fate.Knocked } o && OtherCell(o) == cell)
                UnitStruck(o, true);
            else if (_parked.Find(p => p.Cell == cell && p.Fate == Fate.Knocked) is { } p)
                UnitStruck(p, true);
        }
        // the fires, on whichever model the unit wears now
        for (int i = _blazes.Count - 1; i >= 0; i--)
        {
            Blaze b = _blazes[i];
            TankModel? m = ModelOf(b.Side, b.Tag);
            if (m is null || !IsInstanceValid(m))
                continue;
            if (b.Out)
            {
                b.OutFor += dt;
                if (b.OutFor > b.Burn.DouseTime + b.Burn.PuffLife + 1.0f)
                {
                    b.Burn.QueueFree();
                    _blazes.RemoveAt(i);
                    continue;
                }
            }
            b.Burn.Fire = b.Out ? 0.0f : 1.0f;
            b.Burn.Smoke = b.Out ? 0.0f : 1.0f;
            _blazePorts.Clear();
            foreach (Node3D ex in m.Exhausts)
                _blazePorts.Add(ex.GlobalPosition);
            if (_blazePorts.Count == 0)
                _blazePorts.Add(m.Tank.ToGlobal(m.BlastAt));
            b.Burn.TurretAt = m.Turret?.GlobalPosition;
            b.Burn.OverTurret = false;
            b.Burn.Tick(dt, _blazePorts, _camera.GlobalBasis);
        }
    }
}
