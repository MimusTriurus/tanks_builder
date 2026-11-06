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
        Relief? before = _relief;
        TideRelief();
        ulong t1 = Time.GetTicksMsec();
        TideShow(before);
        Log.Print($"tank3d: match: the tide rises to ring {level} at the end of round {_mt.Round}, frame {_frame} - "
                  + $"{flooded.Count} cells under the sea, the board built again in {t1 - t0} ms, "
                  + (_tideFront is { } front
                      ? $"the front over {front.Order.Length} points ({TideFront.Stats}) in {Time.GetTicksMsec() - t1} ms"
                      : "shown at once"));
        TideClear(flooded);
        TideDrown(flooded);
        TideMarkCells();
        // the reach shown is planned again on the new board (Tank3DBench.Course)
        _reachHere = new Vector2I(int.MinValue, int.MinValue);
        _tideBanner = L.T("match.tide_banner", level);
        Banner(_tideBanner, TideInk);
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
        // each cell as the water reaches it: the play is held till the rise
        // is over (MatchBusy), so the wood's books may wait for it
        foreach (Vector2I c in flooded)
            TideAt(c, 0.0f, () => TideClearCell(c));
    }

    /// <summary>One drowned cell's trees, grass and fire.</summary>
    private void TideClearCell(Vector2I c)
    {
        // the trees go whole - model and fire - and off the wood's books, so
        // the fire's tick never asks after them again (WoodFire); the model
        // keels over and goes down
        for (int i = _trees.Count - 1; i >= 0; i--)
        {
            Planted t = _trees[i];
            if (t.Cell != c)
                continue;
            t.Fire.QueueFree();
            _trees.RemoveAt(i);
            TideFell(t.Holder);
        }
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

    /// <summary>The hulls on the drowned cells: able to fight without the
    /// snorkel drowned, knocked out to the bed, with the snorkel afloat - a
    /// fire out either way (GameModes.md, «Пат — прилив»).</summary>
    private void TideDrown(List<Vector2I> flooded)
    {
        var under = new HashSet<Vector2I>(flooded);
        if (under.Contains(CellHere) && _unitId >= 0)
        {
            if (Burning(_unitId))
                TideAt(CellHere, 0.0f, () => Quench(_unitId, steam: true));
            if (_fate == Fate.Alive && !Snorkel(_unitId))
            {
                Drown();
                Log.Print($"tank3d: match: {SideName(_side)} {_modelTag}#{_unitId} on {MapAt(CellHere)} - the tide drowns it");
            }
        }
        if (_other is { Unit: true } o && under.Contains(OtherCell(o)))
        {
            if (Burning(o.Id))
            {
                int id = o.Id;
                TideAt(OtherCell(o), 0.0f, () => Quench(id, steam: true));
            }
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
            {
                int id = p.Id;
                TideAt(p.Cell, 0.0f, () => Quench(id, steam: true));
            }
            if (p.Fate == Fate.Alive && Snorkel(p.Id))
                continue;
            if (p.Fate == Fate.Alive)
            {
                p.Fate = Fate.Knocked;
                Log.Print($"tank3d: match: {SideName(p.Side)} {p.Tag}#{p.Id} on {MapAt(p.Cell)} - the tide drowns it");
            }
            Parked q = p;
            TideAt(p.Cell, 0.0f, () => TideFounder(q.Model, q.Cell));
        }
    }

    // --- the rise seen (TideFront, Stage3D.TideBegin) ---------------------------

    /// <summary>The rise playing: the ground going down point by point; null
    /// between rises. The match is held while it plays (<c>MatchBusy</c>).</summary>
    private TideFront? _tideFront;
    private float _tideClock;
    /// <summary>What waits for the water: a cell's trees and grass, a fire to
    /// put out, a hull to let down - each at its cell's arrival.</summary>
    private readonly List<(float Due, Action Do)> _tideDue = new();
    /// <summary>The spray thrown along the front, a few in turn.</summary>
    private CelSplash[]? _tideSprays;
    private int _tideSpray, _tideCursor;
    private float _tideSprayLast;
    /// <summary>The rise's banner, held over the round's while the water comes
    /// in; the round's put back after.</summary>
    private string _tideBanner = "";
    private string? _tideAfterText;
    private Color _tideAfterInk;

    /// <summary>A rise forced at this frame (<c>--tide-at</c>), -1 none; and
    /// again this many frames on, 0 once.</summary>
    private int _tideAt = -1, _tideEvery;

    private const int TideSprays = 10;
    /// <summary>Spray at most this often, s, and from about this share of the
    /// front's points.</summary>
    private const float TideSprayGap = 0.08f, TideSprayShare = 0.12f;

    /// <summary>The rise shown over a few seconds rather than in one frame:
    /// the front between the boards, the sinking mesh up.</summary>
    private void TideShow(Relief? before)
    {
        _tideFront = null;
        if (before is null || _relief is null || _stage is null || _field is null || _noUi)
            return;
        ulong t0 = Time.GetTicksMsec();
        TideFront? front = TideFront.Of(before, _relief, _field.Lift);
        ulong t1 = Time.GetTicksMsec();
        if (front is null || front.Order.Length == 0 || !_stage.TideBegin(front))
            return;
        Log.Print($"tank3d: match: the tide's front {t1 - t0} ms, the stage and the sinking mesh {Time.GetTicksMsec() - t1} ms");
        _tideFront = front;
        _tideClock = 0.0f;
        _tideCursor = 0;
        _tideSprayLast = -1.0f;
        _tideAfterText = null;
        front.Tick(0.0f);
        // the hulls and the effects stand on the ground as it goes down
        _field.Surface = front.HeightAt;
        if (_tideSprays is null && !_fx2d)
        {
            _tideSprays = new CelSplash[TideSprays];
            for (int i = 0; i < TideSprays; i++)
            {
                _tideSprays[i] = new CelSplash { Name = $"TideSpray{i}" };
                AddChild(_tideSprays[i]);
                _tideSprays[i].Build(Squash, RiseFactor);
            }
        }
    }

    /// <summary>Something for when the water reaches <paramref name="cell"/>'s
    /// middle, <paramref name="after"/> s later; at once with no rise playing.</summary>
    private void TideAt(Vector2I cell, float after, Action act)
    {
        if (_tideFront is not { } front || _field is null)
        {
            act();
            return;
        }
        float due = front.ArriveAt(_field.FlatAnchor(cell) + _field.CentreOffset);
        _tideDue.Add((Mathf.Min(due, TideFront.Spread) + after, act));
    }

    /// <summary>A tree the water takes: it keels over and goes down
    /// shrinking, then is gone.</summary>
    private void TideFell(Node3D holder)
    {
        if (!IsInstanceValid(holder))
            return;
        float drop = HexWidth * 0.5f;
        float lean = Mathf.DegToRad(30.0f + 25.0f * GD.Randf());
        float turn = GD.Randf() * Mathf.Tau;
        Tween t = CreateTween().SetParallel();
        t.TweenProperty(holder, "position:y", holder.Position.Y - drop, TideFront.Fall * 1.6f)
         .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
        t.TweenProperty(holder, "rotation", holder.Rotation + new Vector3(Mathf.Cos(turn), 0.0f, Mathf.Sin(turn)) * lean,
                        TideFront.Fall * 1.2f)
         .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
        t.TweenProperty(holder, "scale", holder.Scale * 0.05f, TideFront.Fall * 1.6f)
         .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
        t.Chain().TweenCallback(Callable.From(holder.QueueFree));
    }

    /// <summary>A knocked-out hull the water takes goes down to the new bed
    /// with the ground - <see cref="Founder"/>'s, aimed at the board after the
    /// rise rather than at the ground halfway down.</summary>
    private void TideFounder(TankModel model, Vector2I cell)
    {
        if (_field is null || _relief is null || !_field.IsDeep(cell) || model.GetParent() is not Node3D rig
            || _other is { } o && ReferenceEquals(o.Model, model))
            return;
        float bed = _relief.HeightAt(_field.FlatAnchor(cell) + _field.CentreOffset) / RiseFactor;
        CreateTween().TweenProperty(rig, "position:y", bed, TideFront.Fall + (float)TankTick.SinkSeconds)
                     .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
    }

    /// <summary>A frame of a match: the rise's clock, what is due, the spray;
    /// at the end the board's own mesh and ground back.</summary>
    private void TideAnim(float dt)
    {
        // a rise asked for by the launch (--tide-at), between turns' work
        if (_tideOn && _tideAt >= 0 && _frame >= _tideAt && _tideFront is null && !MatchOver && !MatchBusy)
        {
            Log.Print($"tank3d: match: --tide-at {_tideAt} - the sea up a ring now");
            _tideAt = _tideEvery > 0 ? _frame + _tideEvery : -1;
            TideRise();
        }
        if (_tideSprays is not null)
            for (int i = 0; i < _tideSprays.Length; i++)
                if (_tideSprays[i].Busy)
                    _tideSprays[i].Tick(dt, _camera.GlobalBasis);
        if (_tideFront is not { } front)
            return;
        _tideClock += dt;
        front.Tick(_tideClock);
        _stage?.TideAt(_tideClock);
        for (int i = _tideDue.Count - 1; i >= 0; i--)
        {
            if (_tideDue[i].Due > _tideClock)
                continue;
            Action act = _tideDue[i].Do;
            _tideDue.RemoveAt(i);
            act();
        }
        TideSpray(front);
        // the rise's banner over the round's the hand-over put up
        if (!MatchOver && _bannerText != _tideBanner)
        {
            _tideAfterText = _bannerText;
            _tideAfterInk = _bannerInk;
            Banner(_tideBanner, TideInk);
        }
        if (_tideClock >= front.End)
            TideStop();
    }

    /// <summary>The rise over: all that waited done, the new board's own mesh
    /// and ground, the round's banner back.</summary>
    private void TideStop()
    {
        _tideFront = null;
        _stage?.TideEnd();
        if (_field is not null && _relief is not null)
            _field.Surface = _relief.HeightAt;
        for (int i = 0; i < _tideDue.Count; i++)
            _tideDue[i].Do();
        _tideDue.Clear();
        if (_tideAfterText is { } text && !MatchOver)
            Banner(text, _tideAfterInk);
        _tideAfterText = null;
        Log.Print($"tank3d: match: the tide's rise played out in {_tideClock:0.00} s, frame {_frame}");
    }

    /// <summary>Spray where the water has just come: the front's points in
    /// order of arrival, now and then one thrown up the way the front runs,
    /// with a ring on the pond.</summary>
    private void TideSpray(TideFront front)
    {
        Relief f = front.Next;
        while (_tideCursor < front.Order.Length && front.Arrive[front.Order[_tideCursor]] <= _tideClock)
        {
            int p = front.Order[_tideCursor++];
            if (_tideSprays is null || _tideClock - _tideSprayLast < TideSprayGap
                || Hashed(p) > TideSprayShare || float.IsNaN(f.Surface[p]))
                continue;
            _tideSprayLast = _tideClock;
            float x = f.X0 + (p % f.Nx) * f.Cell, z = f.Z0 + (p / f.Nx) * f.Cell;
            float top = f.Surface[p] / RiseFactor;
            Vector2 way = front.Way(p);
            float half = HexWidth * (0.08f + 0.06f * Hashed(p + 7));
            _tideSprays[_tideSpray].Fire(new Vector3(x, top, z), new Vector3(way.X, 0.0f, way.Y),
                                         half, half * 0.8f, 0.18f + 0.15f * Hashed(p + 3));
            _tideSpray = (_tideSpray + 1) % _tideSprays.Length;
            _ripples.Strike(new Vector2(x, z), Stage3D.Waves * 0.35f);
        }
    }

    private static float Hashed(int p)
    {
        uint h = (uint)p * 2246822519u;
        h ^= h >> 13;
        h *= 3266489917u;
        h ^= h >> 16;
        return (h & 0xFFFF) / 65535.0f;
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
