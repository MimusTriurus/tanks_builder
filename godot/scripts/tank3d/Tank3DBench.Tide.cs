using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The tide (GameModes.md, «Пат — прилив», 2026-11-06): a stalemate is no
/// draw - at the field's tick after <see cref="MatchRules.QuietRoundsDraw"/>
/// quiet rounds the sea rises a ring. The rules core's
/// <c>Battle.Tide</c> on the bench's own board:
/// <list type="bullet">
/// <item>the rings worked out once at the match's start
/// (<see cref="Battle.Rings"/>, the core's: steps to the sea, and the
/// vessels' spill through the low cells joined to it) - on a flooded board
/// they would come out wrong;</item>
/// <item>a rise floods the map's own arrays the field holds (floor deep,
/// level the sea's, no cover, no ramp) and sets them on the field again,
/// then builds the relief afresh as the editor does (<c>MapEditor.Relief</c>)
/// and hands it to the stage - a moment's halt under the banner;</item>
/// <item>what stood on the drowned cells goes: the trees hidden, the grass
/// taken up, a fire out; a hull able to fight without the snorkel drowned,
/// a knocked-out one down to the bed, one with the snorkel afloat;</item>
/// <item>the next ring marked on the board, the rounds to the rise on the HUD,
/// and the core's shadow told (<c>ShadowOfBench</c>) for the AI.</item>
/// </list>
/// Off without the relief's sea (<c>--ramps</c>): there the stalemate is the
/// old draw.
/// </summary>
public sealed partial class Tank3DBench
{
    /// <summary>The tide plays: a match on the relief, the sea round it.</summary>
    private bool _tideOn;
    /// <summary>The moated map whose arrays the field holds - flooded in place.</summary>
    private BoardMap? _tideMap;
    /// <summary>Each cell's rise (<see cref="Battle.Rings"/>), the sea's level.</summary>
    private int[]? _tideRings;
    private int _tideSea;
    /// <summary>The cells the next rise floods: the board's mark.</summary>
    private readonly List<Vector2I> _tideNext = new();
    private MeshInstance3D? _tideMark;
    /// <summary>The next ring's mark: the sea's blue.</summary>
    private static readonly Color TideInk = new(0.30f, 0.62f, 0.95f);

    /// <summary>After the board: the rings, once.</summary>
    private void TideSetup(BoardMap map)
    {
        _tideOn = false;
        if (!Match || !_reliefOn || _field?.Sea is null)
            return;
        _tideMap = map;
        _tideRings = Battle.Rings(BattleBoard.Of(_field), out _tideSea);
        _tideOn = true;
        TideMarkCells();
        Log.Print($"tank3d: match: the tide - a rise every {MatchRules.QuietRoundsDraw} quiet rounds, "
                  + $"the sea at level {_tideSea}, {_tideNext.Count} cells in the first ring");
    }

    /// <summary>The cell's rise: the sea nought.</summary>
    private int TideRing(Vector2I cell) =>
        _tideRings is null || !_field!.InBounds(cell) ? 0 : _tideRings[cell.Y * _field.Columns + cell.X];

    /// <summary>The cells dry now that the next rise floods.</summary>
    private void TideMarkCells()
    {
        _tideNext.Clear();
        if (_field is null)
            return;
        for (int r = 0; r < _field.Rows; r++)
            for (int q = 0; q < _field.Columns; q++)
            {
                var c = new Vector2I(q, r);
                if (_field.InBounds(c) && !_field.IsDeep(c) && TideRing(c) == _mt.TideLevel + 1)
                    _tideNext.Add(c);
            }
    }

    /// <summary>The field's tick (<c>FieldTick</c>, before the round's count
    /// goes on): the rise, once this round makes the clock's count.</summary>
    private void TideTick()
    {
        if (!_tideOn || MatchOver || _mt.TideQuiet + 1 < MatchRules.QuietRoundsDraw)
            return;
        TideRise();
    }

    /// <summary>The sea a ring up: the cells, the relief, what stood on them.</summary>
    private void TideRise()
    {
        BoardMap map = _tideMap!;
        int level = _mt.TideLevel + 1;
        var flooded = new List<Vector2I>();
        bool land = false;
        for (int r = 0; r < _field!.Rows; r++)
            for (int q = 0; q < _field.Columns; q++)
            {
                var c = new Vector2I(q, r);
                if (!_field.InBounds(c) || _field.IsDeep(c))
                    continue;
                if (TideRing(c) > level)
                    land = true;
                else
                    flooded.Add(c);
            }
        // the field's arrays are the map's (BuildBoard): flooded in place and
        // set again, the states the match gave the cover kept
        int n = _field.Columns * _field.Rows;
        var states = new CoverState[n];
        for (int i = 0; i < n; i++)
            states[i] = _field.CoverStateAt(new Vector2I(i % _field.Columns, i / _field.Columns));
        int[] levels = (int[])map.Levels.Clone();
        Foundation[] ground = (Foundation[])map.Ground.Clone();
        bool[] water = (bool[])map.Water.Clone();
        Cover[] over = (Cover[])map.Over.Clone();
        string?[] kinds = (string?[])map.Kinds.Clone();
        foreach (Vector2I c in flooded)
        {
            int i = c.Y * _field.Columns + c.X;
            map.Levels[i] = _tideSea;
            map.Ground[i] = Foundation.Deep;
            map.Water[i] = true;
            map.Over[i] = Cover.None;
            map.Kinds[i] = null;
            states[i] = CoverState.Intact;
        }
        try
        {
            _field.Surface = null;
            _field.SetKinds(map.Kinds);
            _field.SetGround(map.Ground);
            _field.SetCover(map.Over, states);
            _field.SetRelief(map.Levels, null);
            _field.SetWater(map.Water);
            foreach (Vector2I c in flooded)
                _field.SetSides(c, 0);
        }
        catch (InvalidOperationException e)
        {
            // the water's guard (HexField.Flat): the sea against a pond above it -
            // the board put back, the rise not played
            Array.Copy(levels, map.Levels, n);
            Array.Copy(ground, map.Ground, n);
            Array.Copy(water, map.Water, n);
            Array.Copy(over, map.Over, n);
            Array.Copy(kinds, map.Kinds, n);
            _field.SetKinds(map.Kinds);
            _field.SetGround(map.Ground);
            _field.SetCover(map.Over, states);
            _field.SetRelief(map.Levels, null);
            _field.SetWater(map.Water);
            _field.Surface = _relief is { } back ? back.HeightAt : null;
            Log.Print($"tank3d: match: the tide refused by the water's guard - {e.Message}");
            return;
        }
        _mt.TideLevel = level;
        _mt.LastTide = _mt.Round;
        ulong t0 = Time.GetTicksMsec();
        TideRelief();
        TideClear(flooded);
        Log.Print($"tank3d: match: the tide rises to ring {level} at the end of round {_mt.Round}, frame {_frame} - "
                  + $"{flooded.Count} cells under the sea, the board built again in {Time.GetTicksMsec() - t0} ms");
        TideDrown(flooded);
        TideMarkCells();
        // the reach shown is planned again on the new board (Tank3DBench.Course)
        _reachHere = new Vector2I(int.MinValue, int.MinValue);
        Banner(L.T("match.tide_banner", level), TideInk);
        _matchNote = L.N("match.tide_note", flooded.Count);
        if (MatchCheckEnd())
            return;
        if (!land)
        {
            _mt.Drawn = true;
            _passIn = null;
            _commands.Clear();
            Banner(L.T("match.tide_draw"), Colors.White, stay: true);
            Log.Print($"tank3d: match: the sea took the whole board - drawn in round {_mt.Round}");
        }
    }

    /// <summary>The relief built again off the flooded field, the editor's way
    /// (<c>MapEditor.Relief</c>), and the water's skins with it.</summary>
    private void TideRelief()
    {
        Relief? relief = Relief.Build(_field!, rocks: _tideRocks, berms: _tideBerms);
        if (relief is null)
            return;
        _relief = relief;
        _field!.Surface = relief.HeightAt;
        if (_stage is not null)
            _stage.Relief = relief;
        if (_celRipples is not null)
        {
            _celRipples.QueueFree();
            _celRipples = new CelRipples { Name = "CelRipples" };
            AddChild(_celRipples);
            _celRipples.Build(_field, Squash, RiseFactor, HexWidth * 0.5f);
        }
    }

    /// <summary>What the relief was built with at the start, for the rises.</summary>
    private RockStyles? _tideRocks;
    private HashSet<Vector2I>? _tideBerms;

    /// <summary>The trees, the grass and the fire off the drowned cells.</summary>
    private void TideClear(List<Vector2I> flooded)
    {
        var under = new HashSet<Vector2I>(flooded);
        // the trees go whole - model and fire - and off the wood's books, so
        // the fire's tick never asks after them again (WoodFire)
        for (int i = _trees.Count - 1; i >= 0; i--)
        {
            Planted t = _trees[i];
            if (!under.Contains(t.Cell))
                continue;
            t.Holder.QueueFree();
            t.Fire.QueueFree();
            _trees.RemoveAt(i);
        }
        foreach (Vector2I c in flooded)
        {
            _woodedCells.Remove(c);
            _wildfire?.Out(c);
            if (_lawnOf.Remove(c, out int meadow))
                _grass?.TakeUp(meadow);
            _meadowOf.Remove(c);
            _singed.Remove(c);
            _frontOf.Remove(c);
            _entry.Remove(c);
            _razed.Remove(c);
        }
    }

    /// <summary>The hulls on the drowned cells: able to fight without the
    /// snorkel drowned, knocked out to the bed, with the snorkel afloat - a
    /// fire out either way (GameModes.md, «Пат — прилив»).</summary>
    private void TideDrown(List<Vector2I> flooded)
    {
        var under = new HashSet<Vector2I>(flooded);
        if (under.Contains(CellHere) && _unitId >= 0)
        {
            if (Burning(_unitId))
                Quench(_unitId, steam: true);
            if (_fate == Fate.Alive && !Snorkel(_unitId))
            {
                Drown();
                Log.Print($"tank3d: match: {SideName(_side)} {_modelTag}#{_unitId} on {MapAt(CellHere)} - the tide drowns it");
            }
        }
        if (_other is { Unit: true } o && under.Contains(OtherCell(o)))
        {
            if (Burning(o.Id))
                Quench(o.Id, steam: true);
            if (o.Fate == Fate.Alive && !Snorkel(o.Id))
            {
                o.Fate = Fate.Knocked;
                Log.Print($"tank3d: match: {SideName(o.Side)} {o.Tag}#{o.Id} on {MapAt(OtherCell(o))} - the tide drowns it");
            }
        }
        foreach (Parked p in _parked)
        {
            if (!under.Contains(p.Cell) || p.Fate == Fate.Destroyed)
                continue;
            if (Burning(p.Id))
                Quench(p.Id, steam: true);
            if (p.Fate == Fate.Alive && Snorkel(p.Id))
                continue;
            if (p.Fate == Fate.Alive)
            {
                p.Fate = Fate.Knocked;
                Log.Print($"tank3d: match: {SideName(p.Side)} {p.Tag}#{p.Id} on {MapAt(p.Cell)} - the tide drowns it");
            }
            Founder(p.Model, p.Cell);
        }
    }

    /// <summary>Every frame of a match: the next ring's cells marked.</summary>
    private void TideDraw()
    {
        if (!_tideOn || _field is null || _tile is null || _noUi)
            return;
        _tideMark ??= CellMarker("TideNext");
        // brighter as the rise nears
        float near = MatchOver ? 0.0f : TideNear();
        CellMarks(_tideMark, MatchOver ? Array.Empty<Vector2I>() : _tideNext,
                  new Color(TideInk, 0.06f + 0.22f * near), new Color(TideInk, 0.35f + 0.6f * near));
    }

    /// <summary>How near the rise is: 0 a full clock away, 1 at this round's end.</summary>
    private float TideNear() =>
        Mathf.Clamp((_mt.TideQuiet + 1) / (float)MatchRules.QuietRoundsDraw, 0.0f, 1.0f);

    /// <summary>The HUD's word on the tide: the rounds to the rise.</summary>
    private string TideHud() =>
        _tideOn ? L.N("match.tide_in", Mathf.Max(1, MatchRules.QuietRoundsDraw - _mt.TideQuiet)) : "";
}
