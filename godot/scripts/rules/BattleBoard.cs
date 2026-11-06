using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The board as plain arrays - what the rules core and the AI copy, change and
/// search (docs/battle-core-plan.md, Д2). No node, no drawing, nothing of the
/// engine but its value types: it may be cloned by the thousand and read off
/// the main thread, which <see cref="HexField"/>, a <c>Node2D</c>, may not.
///
/// <b>Taken off a board, not rebuilt from the map.</b> <see cref="Of"/> reads
/// every cell's layers through <see cref="IHexBoard"/> from the board the bench
/// plays on, so the two agree by construction - the plot, the flood mask, the
/// wood drawn from the terrain kinds, a wall knocked down mid-match are all in
/// the answers it copies. Building it from <see cref="BoardMap"/> a second way
/// would be the second answer this class exists not to be.
///
/// <b>The rules are not here.</b> Everything worked out of the layers is
/// <see cref="BoardRules"/>'s, the same code <see cref="HexField"/> calls.
///
/// <b>What changes in a match is settable</b>: the cover's state (a wood alight,
/// burnt, cleared; a minefield spent) and the walls' sides. Levels, ramps and
/// floors do not change in a battle.
/// </summary>
public sealed class BattleBoard : IHexBoard
{
    public int Columns { get; }
    public int Rows { get; }
    public bool Leveled { get; }
    public bool Ramped { get; }
    public bool Slopes { get; }
    public int MaxClimb { get; }

    private readonly bool[] _on;
    private readonly int[] _level;
    private readonly int[] _ramp;
    private readonly Foundation[] _floor;
    private readonly Cover[] _cover;
    private readonly CoverState[] _state;
    private readonly byte[] _sides;
    private readonly bool[] _smoke;

    private BattleBoard(int columns, int rows, bool leveled, bool ramped, bool slopes, int maxClimb)
    {
        Columns = columns;
        Rows = rows;
        Leveled = leveled;
        Ramped = ramped;
        Slopes = slopes;
        MaxClimb = maxClimb;
        int n = columns * rows;
        _on = new bool[n];
        _level = new int[n];
        _ramp = new int[n];
        _floor = new Foundation[n];
        _cover = new Cover[n];
        _state = new CoverState[n];
        _sides = new byte[n];
        _smoke = new bool[n];
    }

    /// <summary>Every cell of <paramref name="board"/>, as it answers now.</summary>
    public static BattleBoard Of(IHexBoard board)
    {
        var b = new BattleBoard(board.Columns, board.Rows, board.Leveled, board.Ramped,
                                board.Slopes, board.MaxClimb);
        for (int r = 0; r < b.Rows; r++)
            for (int q = 0; q < b.Columns; q++)
            {
                var cell = new Vector2I(q, r);
                int i = r * b.Columns + q;
                b._on[i] = board.InBounds(cell);
                b._level[i] = board.LevelAt(cell);
                b._ramp[i] = board.RampHeading(cell);
                b._floor[i] = board.FoundationAt(cell);
                b._cover[i] = board.CoverAt(cell);
                b._state[i] = board.CoverStateAt(cell);
                b._sides[i] = (byte)board.SidesAt(cell);
                b._smoke[i] = board.Smoked(cell);
            }
        return b;
    }

    public BattleBoard Clone()
    {
        var b = new BattleBoard(Columns, Rows, Leveled, Ramped, Slopes, MaxClimb);
        Array.Copy(_on, b._on, _on.Length);
        Array.Copy(_level, b._level, _level.Length);
        Array.Copy(_ramp, b._ramp, _ramp.Length);
        Array.Copy(_floor, b._floor, _floor.Length);
        Array.Copy(_cover, b._cover, _cover.Length);
        Array.Copy(_state, b._state, _state.Length);
        Array.Copy(_sides, b._sides, _sides.Length);
        Array.Copy(_smoke, b._smoke, _smoke.Length);
        return b;
    }

    private int Index(Vector2I cell) => cell.Y * Columns + cell.X;

    private bool Inside(Vector2I cell) =>
        cell.X >= 0 && cell.X < Columns && cell.Y >= 0 && cell.Y < Rows;

    public bool InBounds(Vector2I cell) => Inside(cell) && _on[Index(cell)];

    public int LevelAt(Vector2I cell) => InBounds(cell) ? _level[Index(cell)] : 0;

    public int RampHeading(Vector2I cell) => InBounds(cell) ? _ramp[Index(cell)] : -1;

    public Foundation FoundationAt(Vector2I cell) => InBounds(cell) ? _floor[Index(cell)] : Foundation.Void;

    public Cover CoverAt(Vector2I cell) => InBounds(cell) ? _cover[Index(cell)] : Cover.None;

    /// <summary>As <see cref="HexField.CoverStateAt"/>: masonry has no state of
    /// its own, it reads off its sides - so a wall knocked down here
    /// (<see cref="SetSides"/>) is breached without a second write.</summary>
    public CoverState CoverStateAt(Vector2I cell)
    {
        if (!InBounds(cell))
            return CoverState.Intact;
        int i = Index(cell);
        if (_cover[i] == Cover.Walls)
            return _sides[i] == 0 ? CoverState.Breached : CoverState.Intact;
        return _cover[i] == Cover.None ? CoverState.Intact : _state[i];
    }

    public int SidesAt(Vector2I cell) =>
        InBounds(cell) && _cover[Index(cell)] == Cover.Walls ? _sides[Index(cell)] : 0;

    /// <summary>The cover on a cell changes state: a wood catches, burns out or
    /// is driven flat; a minefield is spent.</summary>
    public void SetCoverState(Vector2I cell, CoverState state)
    {
        if (InBounds(cell) && _cover[Index(cell)] != Cover.None)
            _state[Index(cell)] = state;
    }

    /// <summary><see cref="HexField.LayMinefield"/>: a bare cell, or a spent
    /// minefield, is a live one.</summary>
    public bool LayMinefield(Vector2I cell)
    {
        if (!InBounds(cell) || _cover[Index(cell)] is not (Cover.None or Cover.Minefield))
            return false;
        _cover[Index(cell)] = Cover.Minefield;
        _state[Index(cell)] = CoverState.Intact;
        return true;
    }

    /// <summary>The cell's own standing walls, as a mask (a leaf down is a bit gone).</summary>
    public void SetSides(Vector2I cell, int mask)
    {
        if (InBounds(cell))
            _sides[Index(cell)] = (byte)mask;
    }

    /// <summary>The tide's (trial, <see cref="BattleState.Tide"/>): the cell
    /// under the sea - deep water at <paramref name="level"/>, its wood,
    /// walls and mines gone with it.</summary>
    public void Flood(Vector2I cell, int level)
    {
        if (!InBounds(cell))
            return;
        int i = Index(cell);
        _floor[i] = Foundation.Deep;
        _level[i] = level;
        _ramp[i] = -1;
        _cover[i] = Cover.None;
        _state[i] = CoverState.Intact;
        _sides[i] = 0;
    }

    public bool Smoked(Vector2I cell) => InBounds(cell) && _smoke[Index(cell)];

    /// <summary>Smoke laid on the hex, or gone from it.</summary>
    public void SetSmoke(Vector2I cell, bool on)
    {
        if (InBounds(cell))
            _smoke[Index(cell)] = on;
    }

    public bool IsRamp(Vector2I cell) => BoardRules.IsRamp(this, cell);
    public bool IsWater(Vector2I cell) => BoardRules.IsWater(this, cell);
    public bool IsDeep(Vector2I cell) => BoardRules.IsDeep(this, cell);
    public bool Passable(Vector2I cell, int heading) => BoardRules.Passable(this, cell, heading);
    public bool Drops(Vector2I from, Vector2I onto) => BoardRules.Drops(this, from, onto);
    public bool Walled(Vector2I from, int heading) => BoardRules.Walled(this, from, heading);
    public bool Blocked(Vector2I from, int heading) => BoardRules.Blocked(this, from, heading);
    public bool Screened(Vector2I cell) => BoardRules.Screened(this, cell);
    public List<Vector2I> Lane(Vector2I cell, int heading, int limit = int.MaxValue) =>
        BoardRules.Lane(this, cell, heading, limit);
    public (int Heading, int Range) LaneTo(Vector2I from, Vector2I to) => BoardRules.LaneTo(this, from, to);
}
