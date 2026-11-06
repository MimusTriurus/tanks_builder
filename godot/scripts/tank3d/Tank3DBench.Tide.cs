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
        _tideMarkStale = true;
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
        // F9's warning was of this rise
        _tideWarnForced = false;
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

    /// <summary>F9 in a match, in two presses (the author's note, 2026-11-06):
    /// the first puts the warning up - the next ring and the hulls on it
    /// blinking, as the last round before a rise shows them; the second, with
    /// the warning up (F9's or the clock's own), the sea up a ring now, as
    /// <c>--tide-at</c> does. Nothing while a rise plays, a turn's work is on
    /// or the sides are placing.</summary>
    private void TideForce()
    {
        if (!_tideOn)
        {
            Log.Print("tank3d: match: F9 - no tide on this board (no sea round it, or --ramps)");
            return;
        }
        // not over the placement either: the sides' rows stand at the sea
        if (_tideFront is not null || MatchOver || MatchBusy || Deploying)
            return;
        if (!TideWarnShown)
        {
            _tideWarnForced = true;
            Log.Print($"tank3d: match: F9 - the warning up, {_tideNext.Count} cells in ring {_mt.TideLevel + 1}; F9 again floods them");
            return;
        }
        Log.Print($"tank3d: match: F9 - the sea up a ring now, round {_mt.Round}");
        TideRise();
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

    // --- the warning: the next ring blinking on the ground ---------------------

    /// <summary>The rounds before a rise the next ring shows: this round's end
    /// and the one before (the author's note, 2026-11-06: marked from the
    /// match's start it was noise, and the rimmed hexes stood off the relief).</summary>
    public const int TideWarnRounds = 2;

    /// <summary>The blink's full beat, s - the round before, then the last round's.</summary>
    private const float TideBeatFar = 1.8f, TideBeatNear = 1.2f;
    /// <summary>The fill's alpha at the top of a beat - the round before, the
    /// last round. Plain blue, not the sea's skin (tried 2026-11-06 and put
    /// back on the author's note: a soft blink reads better).</summary>
    private const float TideGlowFar = 0.32f, TideGlowNear = 0.5f;

    /// <summary>The mark's mesh is of the ring and board it was built on: built
    /// again when the next ring is worked out (after a rise, the relief new).</summary>
    private bool _tideMarkStale = true;
    private ShaderMaterial? _tideMarkMat;
    private float _tideBlink;

    /// <summary>The rounds to the next rise, this one counted: 1 - at this round's end.</summary>
    private int TideRoundsLeft => Mathf.Max(1, MatchRules.QuietRoundsDraw - _mt.TideQuiet);

    /// <summary>The warning put up by F9 ahead of its time (<see cref="TideForce"/>):
    /// it stays, as the last round's, until the rise it warns of.</summary>
    private bool _tideWarnForced;

    /// <summary>The next ring is blinking now: the rounds before the rise, or F9's.</summary>
    private bool TideWarnShown =>
        _tideOn && !MatchOver && _tideFront is null && !Deploying && _tideNext.Count > 0
        && (_tideWarnForced || TideRoundsLeft <= TideWarnRounds);

    /// <summary>Every frame of a match: the next ring's cells, a soft blink of
    /// the ground itself - no rim - and only <see cref="TideWarnRounds"/>
    /// rounds before the rise; faster and brighter in the last one.</summary>
    private void TideDraw()
    {
        if (!_tideOn || _field is null || _relief is null || _noUi)
            return;
        int left = TideRoundsLeft;
        bool show = TideWarnShown;
        if (!show)
        {
            if (_tideMark is not null)
                _tideMark.Visible = false;
            _tideBlink = 0.0f;
            _tideGlowNow = 0.0f;
            TideGhostsHide();
            return;
        }
        if (_tideMark is null)
        {
            _tideMarkMat = new ShaderMaterial { Shader = new Shader { Code = TideMarkShader }, RenderPriority = Stage3D.RingOrder - 1 };
            _tideMark = new MeshInstance3D
            {
                Name = "TideNext",
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = _tideMarkMat,
            };
            AddChild(_tideMark);
        }
        if (_tideMarkStale)
        {
            _tideMarkStale = false;
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            Vector2[] corner = Corners();
            foreach (Vector2I cell in _tideNext)
                ReliefCell(st, cell, corner, TideInk);
            _tideMark.Mesh = st.Commit();
            _tideMarkMat!.Param("lift", Lie.Length());
        }
        _tideMark.Visible = true;
        bool last = left <= 1 || _tideWarnForced;
        // from nothing up and back to nothing: the cells come and go
        _tideBlink += (float)GetProcessDeltaTime() / (last ? TideBeatNear : TideBeatFar);
        _tideBlink -= Mathf.Floor(_tideBlink);
        float glow = 0.5f - 0.5f * Mathf.Cos(Mathf.Tau * _tideBlink);
        _tideGlowNow = glow;
        _tideMarkMat!.Param("glow", glow * (last ? TideGlowNear : TideGlowFar));
        _tideClockAll += (float)GetProcessDeltaTime();
        TideGhostsTick(glow, last);
    }

    /// <summary>The warning's own clock, s: the afloat ghosts' swell.</summary>
    private float _tideClockAll;

    /// <summary>The blink now, 0..1: the hulls' signs over the ring beat with it (Tank3DBench.Badges).</summary>
    private float _tideGlowNow;

    /// <summary>The mark's shader: the ground's own triangles under the
    /// cells (<c>ReliefCell</c>), lifted toward the eye as the move's zone is,
    /// in their colour at <c>glow</c>.</summary>
    private const string TideMarkShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, blend_mix;

uniform float lift = 0.0;
uniform float glow = 0.0;

void vertex() {
    vec4 v = MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
    v.z += lift;
    POSITION = PROJECTION_MATRIX * v;
}

void fragment() {
    ALBEDO = COLOR.rgb;
    ALPHA = glow;
}
";

    // --- the warning's ghosts: the hulls the rise takes --------------------------

    /// <summary>A unit's ghost for the warning: a copy of its model, kept for
    /// the match and stood over it while it is on the next ring.</summary>
    private sealed class TideGhost
    {
        public required Node3D Rig;
        public required TankModel Model;
        public required bool Afloat;
        public bool Used;
    }

    private readonly Dictionary<int, TideGhost> _tideGhosts = new();
    private ShaderMaterial? _tideSinkFill, _tideFloatFill;
    /// <summary>How far a ghost goes down, of its hull's height: one without
    /// the snorkel most of its deck under, one with it to its swimming line.</summary>
    private const float TideSinkDeep = 0.75f, TideSinkFloat = 0.25f;

    /// <summary>The ghosts of the units able to fight on the next ring, in
    /// the blink's beat (<paramref name="glow"/>, 0..1): without the snorkel
    /// going down into the ground, dimming to the water's blue - drowned at
    /// the rise; with it down to its line and riding a swell - afloat
    /// (GameModes.md, «Пат — прилив»). Knocked-out hulls have none.</summary>
    private void TideGhostsTick(float glow, bool last)
    {
        foreach (TideGhost g in _tideGhosts.Values)
            g.Used = false;
        float down = Mathf.SmoothStep(0.0f, 1.0f, Mathf.Clamp((_tideBlink - 0.1f) / 0.5f, 0.0f, 1.0f));
        if (_fate == Fate.Alive && _unitId >= 0 && TideComes(CellHere))
            TideGhostOver(_unitId, _modelTag, _rig, _model, down);
        if (_other is { Unit: true } o && o.Fate == Fate.Alive && TideComes(OtherCell(o)))
            TideGhostOver(o.Id, o.Tag, o.Rig, o.Model, down);
        for (int i = 0; i < _parked.Count; i++)
        {
            Parked p = _parked[i];
            if (p.Fate == Fate.Alive && TideComes(p.Cell))
                TideGhostOver(p.Id, p.Tag, p.Rig, p.Model, down);
        }
        foreach (TideGhost g in _tideGhosts.Values)
            if (!g.Used)
                g.Rig.Visible = false;
        float a = glow * (last ? 1.0f : 0.75f);
        _tideSinkFill?.Param("tint", SelectionRing.Friendly.Lerp(RamDrowned, down));
        _tideSinkFill?.Param("alpha", TideGhostAlpha * a * (1.0f - 0.3f * down));
        _tideFloatFill?.Param("alpha", TideGhostAlpha * a);
    }

    /// <summary>The cell is on the ring the next rise takes.</summary>
    private bool TideComes(Vector2I cell)
    {
        for (int i = 0; i < _tideNext.Count; i++)
            if (_tideNext[i] == cell)
                return true;
        return false;
    }

    /// <summary>The ghost of unit <paramref name="id"/> stood on its rig,
    /// <paramref name="down"/> of the way down; built the first time.</summary>
    private void TideGhostOver(int id, string tag, Node3D rig, TankModel real, float down)
    {
        bool afloat = Snorkel(id);
        if (!_tideGhosts.TryGetValue(id, out TideGhost? g) || g.Afloat != afloat)
        {
            if (g is not null)
                g.Rig.QueueFree();
            g = TideGhostMake(tag, afloat);
            if (g is null)
            {
                _tideGhosts.Remove(id);
                return;
            }
            _tideGhosts[id] = g;
        }
        g.Used = true;
        float tall = g.Model.Size.Y * g.Model.PixelsPerUnit / RiseFactor;
        Transform3D t = rig.Transform;
        if (afloat)
        {
            float s = Mathf.Tau * _tideClockAll / (float)GhostSwell.SwellPeriod;
            t.Origin += Vector3.Down * (TideSinkFloat * tall * down)
                        + Vector3.Up * (2.0f * (float)GhostSwell.SwellAmplitude * Mathf.Sin(s) / RiseFactor);
        }
        else
            // a little under at the top of the beat, not on the real hull's
            // own faces: a ghost there fights the hull for the pixels
            t.Origin += Vector3.Down * (tall * (0.06f + TideSinkDeep * down));
        g.Rig.Transform = t;
        g.Model.Position = real.Position;
        g.Model.Yaw = real.Yaw;
        g.Model.Elevation = real.Elevation;
        g.Model.Apply();
        g.Rig.Visible = true;
    }

    /// <summary>A ghost's copy of <paramref name="tag"/>'s model, dressed
    /// in the sinking or the floating fill.</summary>
    private TideGhost? TideGhostMake(string tag, bool afloat)
    {
        TankModel model;
        try
        {
            model = TankModel.Copy(tag, Bare) ?? TankModel.Load(tag, toon: true, bare: Bare);
        }
        catch (Exception e)
        {
            Log.Print($"tank3d: match: the tide's ghost of {tag}: {e.Message}");
            return null;
        }
        ShaderMaterial fill;
        if (afloat)
        {
            _tideFloatFill ??= new ShaderMaterial { Shader = TideGhostShader(), RenderPriority = Stage3D.RingOrder + 1 };
            _tideFloatFill.Param("tint", SelectionRing.Friendly);
            fill = _tideFloatFill;
        }
        else
        {
            _tideSinkFill ??= new ShaderMaterial { Shader = TideGhostShader(), RenderPriority = Stage3D.RingOrder + 1 };
            fill = _tideSinkFill;
        }
        Node3D rig = DressGhost(model, tag, fill, afloat ? "TideGhostAfloat" : "TideGhostSink");
        return new TideGhost { Rig = rig, Model = model, Afloat = afloat };
    }

    private void TideGhostsHide()
    {
        foreach (TideGhost g in _tideGhosts.Values)
            g.Rig.Visible = false;
    }

    private static Shader? _tideGhostFill;

    /// <summary>The move ghost's fill, drawn over everything: the warning's
    /// ghost stands in its own hull and goes down into the ground, and
    /// depth-tested the hull and the ground hid all of it. No dashed ink: with
    /// no depth its inflated back faces hatched the whole hull.</summary>
    private static Shader TideGhostShader() =>
        _tideGhostFill ??= new Shader
        {
            Code = GhostFillShader.Code.Replace("depth_draw_always", "depth_draw_never, depth_test_disabled"),
        };

    /// <summary>The warning ghost's fill at the top of the beat.</summary>
    private const float TideGhostAlpha = 0.5f;

    /// <summary>The HUD's word on the tide: the rounds to the rise.</summary>
    private string TideHud() =>
        _tideOn ? L.N("match.tide_in", Mathf.Max(1, MatchRules.QuietRoundsDraw - _mt.TideQuiet)) : "";
}
