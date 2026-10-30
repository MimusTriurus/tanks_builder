using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The bench as a game: two sides of five on a flat 10 x 10 board, turn about, by the
/// GDD's turn (docs/gdd/match.md, «Ход и раунд», «Структура хода»).
/// <c>res://scenes/Match3D.tscn</c> (its root's <see cref="Match"/>), or
/// <c>--match</c> on <c>Tank3D.tscn</c>; the board is <c>maps/drill10.json</c> (the drill; <c>--map field10</c> for two plain lines)
/// unless <c>--map</c> names another.
///
/// <b>One rig still.</b> The bench's own tank is its fields, and a tank per
/// unit is the bench rewritten (<c>Tank3DBench.Parked</c>). So the unit whose
/// turn it is rides the rig - it drives, turns its turret, fires - and every
/// other unit stands (<see cref="Parked"/>), with its side and its fate. The
/// enemy under the pointer is lifted into the target (<see cref="Other"/>,
/// <see cref="MakeTarget"/>): the line of fire, the plate judged and the
/// round's strike all ask the target, so they ask it. It is put back when
/// another enemy is pointed at or the turn passes (<see cref="DropTarget"/>);
/// its marks go back with it (<c>_parkedHits</c>).
///
/// <b>Sides.</b> Each home names its side, class and facing, or leaves them to
/// the old split: the first half of the homes green, the rest brown, each side
/// in <see cref="Roster"/>'s order - LT, MT, HT, TD, HM (<see cref="LineUpSides"/>). The
/// brown side is the same models with their green paint taken to brown in the cel
/// shader (<c>side_brown</c>, <see cref="Toon"/>), not models of their own.
///
/// <b>A turn</b> (match.md, «Структура хода»): pick one own unit - a click on
/// it, while the one on the rig has done nothing yet; move it once by the
/// steps (<see cref="Steps"/>, the HUD's zone, ghost and rose); fire once.
/// Move first, then fire - except LT, which may fire first
/// (<see cref="MovementProfile.FreeOrder"/>). Once the unit has nothing left
/// to do and its round has landed the turn passes by itself
/// (<see cref="PassDelay"/>); Ctrl+Enter or the button pass it sooner - with
/// nothing done that is the GDD's pass. The next side's first unit able to act
/// is mounted on the rig. A side with none passes.
///
/// <b>Fate by the rules</b> (states.md; units.md, «Броня»): a pierce knocks a
/// unit out - the gun falls, the belts sag, a char on the turret; any round on
/// a knocked one destroys it, armour or not - the fireball, the turret off on
/// to the deck, the hull charred. A destroyed unit blocks neither a move nor a
/// line of fire. The side whose units are all destroyed has lost
/// (match.md, «Условия победы», «Уничтожение»).
///
/// <b>What is not here yet</b>: the placement before the first turn, the
/// draw by four quiet rounds, fire, the explosion's neighbours, the ram as an
/// action (a double click on an enemy does nothing), ambush.
/// </summary>
public sealed partial class Tank3DBench
{
    /// <summary>The bench plays a match - set on <c>Match3D.tscn</c>'s root;
    /// <c>--match</c> does the same on any.</summary>
    [Export] public bool Match { get; set; }

    private enum Side { Green, Brown }

    /// <summary>Each side's five, in its homes' order.</summary>
    private static string[] Roster => Lineup.Roster;

    /// <summary>The match's board when <c>--map</c> named none.</summary>
    private const string MatchMap = "drill10";

    /// <summary>How long the turn waits after its last action before it
    /// passes, s: the round's hit and the blast are seen by the side that
    /// fired it.</summary>
    private const float PassDelay = 2.0f;

    /// <summary>How long the banner of a new turn stays, s.</summary>
    private const float BannerFor = 2.2f;

    /// <summary>The side of the unit on the rig.</summary>
    private Side _side = Side.Green;
    private Side _turn = Side.Green;
    private int _round = 1;
    /// <summary>What the unit on the rig has done this turn: the move, the
    /// attack (the shot) and the bonus act (the ram, putting a fire out) -
    /// match.md «Структура хода», п. 5а and 5б, one of each (U-22).</summary>
    private bool _drove, _fired, _bonus;
    /// <summary>Seconds to the turn passing by itself; null while it waits.</summary>
    private float? _passIn;
    private Side? _won;
    private bool _drawn;
    private string _matchNote = "";
    /// <summary><c>--map</c> was given: the match plays it, not its own board.</summary>
    private bool _mapNamed;

    private Label? _banner;
    private float _bannerLeft;

    private static string SideName(Side s) => s == Side.Green ? "зелёные" : "коричневые";
    private static Side Across(Side s) => s == Side.Green ? Side.Brown : Side.Green;
    private static Color SideInk(Side s) => s == Side.Green ? new Color(0.55f, 0.95f, 0.45f) : new Color(0.85f, 0.62f, 0.38f);

    /// <summary>After the flags: the match's board, every home manned, no
    /// bench target.</summary>
    private void MatchSetup()
    {
        if (!Match)
            return;
        if (!_mapNamed && !_handed)
            _mapName = MatchMap;
        _parkedWanted = true;
        _targetDefault = false;
        _otherWanted = false;
        _modelTag = Roster[0];
        _modelNamed = true;
        _side = Side.Green;
        Log.Print($"tank3d: match on {_mapName}, {Roster.Length} a side");
    }

    // --- the sides -------------------------------------------------------------

    /// <summary>The match's units in the homes' order (<see cref="Lineup.Of"/>);
    /// the first green one rides the rig.</summary>
    private List<Lineup.Unit> _lineup = new();

    /// <summary>The unit on the rig at the start, an index into <see cref="_lineup"/>.</summary>
    private int _rigUnit = -1;

    private static Side SideOf(Lineup.Unit u) => u.Side == Parking.Brown ? Side.Brown : Side.Green;

    /// <summary>
    /// Who stands on which home (<see cref="Lineup.Of"/>, the editor's rule
    /// too), before the rig is mounted - the rig's model and cell come off it.
    /// </summary>
    private void LineUpSides()
    {
        _lineup = new List<Lineup.Unit>();
        _rigUnit = -1;
        if (!Match)
            return;
        _lineup = Lineup.Of(_parkings);
        int green = _lineup.Count(u => u.Side == Parking.Green), brown = _lineup.Count - green;
        Log.Print($"tank3d: match: the board fields {green} green and {brown} brown");
        // a side with no homes has nothing to move, and the match is won before it starts
        if (green == 0 || brown == 0)
            Log.Print($"tank3d: match: one side has no homes - {green} green, {brown} brown");
        if (_lineup.Count == 0)
            return;
        _rigUnit = Math.Max(0, _lineup.FindIndex(u => u.Side == Parking.Green));
        Lineup.Unit rig = _lineup[_rigUnit];
        _modelTag = rig.Model;
        _side = _turn = SideOf(rig);
        if (_startCell is null)
            _home = rig.Cell;
    }

    /// <summary>The bench's <see cref="BuildParked"/> for a match: the rig is
    /// the first green unit, every other unit of the line-up standing on its
    /// home, facing the way the home says.</summary>
    private async Task BuildSides()
    {
        if (_field is null)
            return;
        for (int i = 0; i < _lineup.Count; i++)
        {
            Lineup.Unit u = _lineup[i];
            if (i == _rigUnit)
            {
                Heading = HeadingFor(CellHere, u.Facing);
                continue;
            }
            if (!_field.InBounds(u.Cell))
                continue;
            await TankStage(u.Model, i, _lineup.Count);
            Stand(u.Model, u.Cell, HeadingFor(u.Cell, u.Facing), SideOf(u));
        }
        Log.Print($"tank3d: match: {SideName(_side)} {_modelTag} {MapAt(CellHere)} on the rig; standing "
                 + string.Join(", ", _parked.Select(p => $"{SideName(p.Side)} {p.Tag} {MapAt(p.Cell)}")));
        Banner($"Раунд 1 · ходят {SideName(_turn)}", SideInk(_turn));
    }

    /// <summary>The side's paint on a model: brown or the model's own.</summary>
    private void SidePaint(TankModel model, Side side)
    {
        if (!Match)
            return;
        Lineup.Paint(model, side == Side.Brown ? Parking.Brown : Parking.Green);
    }

    /// <summary>Every unit on the board, the rig's first.</summary>
    private IEnumerable<(string Tag, Side Side, Fate Fate, bool Riding)> Units()
    {
        yield return (_modelTag, _side, _fate, true);
        foreach (Parked p in _parked)
            yield return (p.Tag, p.Side, p.Fate, false);
        if (_other is { Unit: true } o)
            yield return (o.Tag, o.Side, o.Fate, false);
    }

    // --- the turn ----------------------------------------------------------------

    /// <summary>Something of the turn is still under way: a route, a turn on
    /// the spot, the gun laying, a round in the air.</summary>
    private bool MatchBusy =>
        RouteMoving || _turnGoal is not null || _aim is not null || _shotWaiting is not null
        || (_shells?.Rounds.Count ?? 0) > 0 || RamRunning || _ramWaiting is not null || _chain.Count > 0;

    private bool MatchOver => _won is not null || _drawn;

    /// <summary>Why the unit on the rig may not move now, or empty.</summary>
    private string MatchMoveWhy()
    {
        if (!Match)
            return "";
        if (MatchOver)
            return "партия окончена";
        if (_side != _turn)
            return $"ходят {SideName(_turn)}";
        if (_passIn is not null)
            return "ход передаётся";
        if (_drove)
            return $"{_modelTag} уже ходил в этом ходу";
        if ((_fired || _bonus) && !_profile.FreeOrder)
            return "после действия не ездят — действует до хода только LT";
        return "";
    }

    private bool MatchMayMove => MatchMoveWhy().Length == 0;

    /// <summary>The shot of this turn is spent - fired, or the turn going over:
    /// no line of fire nor firing post is drawn for a shot there is not.</summary>
    private bool MatchShotSpent => Match && (_fired || _passIn is not null || MatchOver);

    /// <summary>The bonus act of this turn is spent - the ram or the fire put
    /// out, or the turn going over.</summary>
    private const string BonusWaits = "атака сделана — осталось доп. действие или «Конец хода»";

    private bool MatchBonusSpent => Match && (_bonus || _passIn is not null || MatchOver);

    /// <summary>Whether a bonus act is still to be had once the move is over:
    /// a fire to put out, or a unit beside to ram - the move being spent, the
    /// ram goes from where the hull stands. No allocation, it is read every
    /// frame.</summary>
    private bool BonusLeft()
    {
        if (_bonus)
            return false;
        if (Burning(_side, _modelTag))
            return true;
        for (int i = 0; i < _parked.Count; i++)
            if (_parked[i].Fate != Fate.Destroyed && SideTo(CellHere, _parked[i].Cell) is not null)
                return true;
        return _other is { Unit: true } o && o.Fate != Fate.Destroyed && SideTo(CellHere, OtherCell(o)) is not null;
    }

    /// <summary>Why the unit on the rig may not fire at <paramref name="cell"/>
    /// now, or empty - ahead of the rules' own gate (<see cref="ShotWhy"/>).</summary>
    private string MatchShotWhy(Vector2I cell)
    {
        if (!Match)
            return "";
        if (MatchOver)
            return "партия окончена";
        if (_side != _turn)
            return $"ходят {SideName(_turn)}";
        if (_passIn is not null)
            return "ход передаётся";
        if (_fired)
            return $"{_modelTag} уже стрелял в этом ходу";
        return "";
    }

    /// <summary>A route or a turn on the spot was given: the move of the turn.</summary>
    private void MatchMoved()
    {
        if (!Match || _drove)
            return;
        _drove = true;
        Log.Print($"tank3d: match: {SideName(_side)} {_modelTag} moves");
    }

    /// <summary>The gun fired: the action of the turn.</summary>
    private void MatchFired()
    {
        if (!Match)
            return;
        _fired = true;
        Unlock();
        Log.Print($"tank3d: match: {SideName(_side)} {_modelTag} fires");
    }

    /// <summary>Whether a click on a standing unit takes it: one of the side
    /// whose turn it is, able to act, while the one on the rig has done
    /// nothing yet.</summary>
    private bool MatchTakes(Parked p)
    {
        if (!Match)
            return true;
        string why = MatchOver ? "партия окончена"
            : p.Side != _turn ? $"{p.Tag} — {SideName(p.Side)}, ходят {SideName(_turn)}"
            : p.Fate != Fate.Alive ? $"{p.Tag} {(p.Fate == Fate.Knocked ? "подбит" : "уничтожен")}"
            : _passIn is not null ? "ход передаётся"
            : _side == _turn && (_drove || _fired || _bonus) ? $"в этом ходу уже действует {_modelTag}"
            : MatchBusy ? "дождись конца приказа"
            : "";
        if (why.Length == 0)
            return true;
        _orderNote = why;
        return false;
    }

    /// <summary>Ctrl+Enter and the button: the turn handed over now - with
    /// nothing done, a pass (match.md, «Пас разрешён»).</summary>
    private void EndTurn()
    {
        if (!Match || MatchOver)
            return;
        if (MatchBusy)
        {
            _matchNote = "дождись конца приказа";
            return;
        }
        Log.Print($"tank3d: match: {SideName(_turn)} end the turn"
                 + (_side == _turn && !_drove && !_fired && !_bonus ? " - a pass" : ""));
        PassTurn();
    }

    /// <summary>To the other side, and the side that ended its turn checked for
    /// fires left burning (Tank3DBench.Blast) - after the hand-over, so a unit
    /// that burns out stands, as every other, rather than rides.</summary>
    private void PassTurn()
    {
        Side ending = _turn;
        int turn = _turnNo++;
        HandOver();
        if (!MatchOver)
            BurnOut(ending, turn);
    }

    /// <summary>To the other side: its first unit able to act on the rig. A
    /// side with none passes on.</summary>
    private void HandOver()
    {
        _passIn = null;
        _ramMode = false;
        _matchNote = "";
        CancelOrder();
        DropTarget();
        _drove = _fired = _bonus = false;
        for (int tries = 0; tries < 2; tries++)
        {
            _turn = Across(_turn);
            if (_turn == Side.Green)
                _round++;
            if (MatchCheckEnd())
                return;
            if (FirstReady(_turn) is { } next)
            {
                TakeOver(next);
                Banner($"Раунд {_round} · ходят {SideName(_turn)}", SideInk(_turn));
                Log.Print($"tank3d: match: round {_round}, {SideName(_turn)} to move, {_modelTag} on {MapAt(CellHere)}");
                return;
            }
            if (_side == _turn && _fate == Fate.Alive)
            {
                Banner($"Раунд {_round} · ходят {SideName(_turn)}", SideInk(_turn));
                return;
            }
            Log.Print($"tank3d: match: {SideName(_turn)} have nothing able to act - a pass");
            _matchNote = $"у {SideName(_turn)} ходить некому — пас";
        }
        _drawn = true;
        Banner("Ничья: ходить некому", Colors.White, stay: true);
    }

    /// <summary>Set once a restart has happened in this process: the launch's
    /// <c>--do</c> script is spent, so the new level does not replay it.</summary>
    private static bool _restarted;

    /// <summary>Backspace in a match: the level from scratch - the scene is
    /// reloaded whole, so nothing of the old match survives but the board's name
    /// and the way back to the editor, handed over as Run hands them.</summary>
    private void Restart()
    {
        Log.Print($"tank3d: match: restart on {_mapName}");
        if (_handed)
        {
            Session.Map = _mapName;
            // the editor's board again, not its file, and the parked editor
            // stays parked through the reload (Play-in-Editor)
            Session.Board = _handedBoard;
            Session.Unsaved = _unsaved;
        }
        _keepEditor = true;
        Session.Editing = _editing;
        _restarted = true;
        // Under the loading veil, as Run comes in: the same scene from its
        // file, built in stages - a bare reload sat on the last frame for the
        // whole build (16 s on ABBEY). Loading defers the change itself, so
        // the rest of this frame still runs on a node in the tree.
        Loading.Go(GetTree(), SceneFilePath, $"Restarting {_mapName}…");
    }

    /// <summary>The side's first standing unit able to act, in the roster's order.</summary>
    private Parked? FirstReady(Side side) =>
        _parked.Where(p => p.Side == side && p.Fate == Fate.Alive)
               .OrderBy(p => Array.IndexOf(Roster, p.Tag))
               .FirstOrDefault();

    /// <summary>A side with every unit destroyed has lost.</summary>
    private bool MatchCheckEnd()
    {
        if (MatchOver)
            return true;
        var all = Units().ToList();
        foreach (Side s in new[] { Side.Green, Side.Brown })
        {
            if (!all.Where(u => u.Side == s).All(u => u.Fate == Fate.Destroyed))
                continue;
            _won = Across(s);
            _passIn = null;
            Banner($"Победа: {SideName(_won.Value)} · раунд {_round}", SideInk(_won.Value), stay: true);
            Log.Print($"tank3d: match: {SideName(s)} destroyed to the last - {SideName(_won.Value)} win in round {_round}");
            return true;
        }
        return false;
    }

    /// <summary>Every frame: the standing units' marks, the wrecked target
    /// put back, and the turn passing once its last action is over.</summary>
    private void MatchTick(float dt)
    {
        if (!Match)
            return;
        foreach ((TankModel model, CelHit hits) in _parkedHits)
            if (IsInstanceValid(model) && IsInstanceValid(hits) && !ReferenceEquals(hits, _other?.Hits))
                hits.Tick(dt, _camera.GlobalBasis);
        using (Prof.Measure("match.pyre"))
            PyreTick(dt);
        CharTick();
        using (Prof.Measure("match.blast"))
            BlastTick(dt);
        RamWaitTick();
        if (_banner is not null && _bannerLeft > 0.0f)
        {
            _bannerLeft -= dt;
            _banner.Modulate = new Color(1, 1, 1, Mathf.Clamp(_bannerLeft / 0.5f, 0.0f, 1.0f));
        }
        RollTick();
        bool busy = MatchBusy;
        if (_other is { Unit: true, Fate: Fate.Destroyed } && !busy)
            DropTarget();
        if (MatchOver)
            return;
        // the turn goes by itself once there is nothing left in it: the shot
        // fired, the move done (or barred), and no bonus act to be had - one
        // still open waits for it or for the end of the turn (U-22)
        if (_passIn is null && !busy && _side == _turn && _fired && (_drove || !_profile.FreeOrder) && !BonusLeft())
        {
            _passIn = PassDelay;
            _matchNote = "ход сделан";
        }
        else if (_passIn is null && !busy && _side == _turn && _fired && (_drove || !_profile.FreeOrder))
            _matchNote = BonusWaits;
        // Knocked out on its own move - a mine under it (Tank3DBench.Mines):
        // it can do no more, so the turn goes on, once the hull has rolled to
        // a stop. It waited for a shot that could not come (2026-10-29).
        if (_passIn is null && !busy && _side == _turn && _fate != Fate.Alive && _coastTo is null)
        {
            _passIn = PassDelay;
            _matchNote = $"{_modelTag} подбит";
        }
        if (_passIn is float left && !busy)
        {
            _passIn = left - dt;
            if (_passIn <= 0.0f)
                PassTurn();
        }
    }

    // --- the target --------------------------------------------------------------

    /// <summary>The enemy standing on <paramref name="cell"/>, lifted into the
    /// target - the right button's way to a hex the pointer was not on.</summary>
    private void TargetOn(Vector2I cell)
    {
        if (Match && _parked.Find(p => p.Cell == cell) is { } p)
            MatchAims(p);
    }

    /// <summary>
    /// The pointer on a standing unit: it is made the target, of either side -
    /// friendly fire (GDD units.md: "Выстрелы, рикошеты и тараны не различают
    /// своих и чужих. По своему танку можно выстрелить, свой танк можно
    /// протаранить"). The line of fire, the ram's and the blast's previews
    /// come up on an own unit as on an enemy, and the right button fires at
    /// it or rams it; the left one still takes it (<c>OrderClick</c>). It
    /// used to want Ctrl held (2026-10-28) and wants nothing now
    /// (2026-10-29, the author's note). True when it now is the target.
    /// </summary>
    private bool MatchAims(Parked p)
    {
        if (!Match || p.Fate == Fate.Destroyed)
            return false;
        MakeTarget(p);
        return _other is { } o && ReferenceEquals(o.Model, p.Model);
    }

    /// <summary>The standing unit as the bench's target, on its cell and
    /// heading, its marks with it. Not while a round flies or the gun lays:
    /// both are on the target they began with.</summary>
    private void MakeTarget(Parked p)
    {
        if (_field is null || RamRunning || _aim is not null || (_shells?.Rounds.Count ?? 0) > 0)
            return;
        DropTarget();
        CelHit hits = HitsOf(p);
        _parked.Remove(p);
        _other = WrapOther(p.Model, p.Rig, ClassFor(p.Tag), p.Tag, hits);
        _other.Unit = true;
        _other.Side = p.Side;
        _other.Fate = p.Fate;
        _other.Cell = p.Cell;
        _other.Heading = Mathf.PosMod(p.Heading, 360.0f);
        _otherTag = p.Tag;
        _otherCell = p.Cell;
        _otherHeading = p.Heading;
        _ram = RamPhase.None;
        _ramTouching = false;
    }

    /// <summary>The target back to standing, its marks kept for it.</summary>
    private void DropTarget()
    {
        if (_other is not { Unit: true } o)
            return;
        o.Scuff.QueueFree();
        o.Dust?.QueueFree();
        _parkedHits[o.Model] = o.Hits;
        _parked.Add(new Parked
        {
            Model = o.Model, Rig = o.Rig, Tag = o.Tag, Cell = OtherCell(o), Heading = o.Heading,
            Side = o.Side, Fate = o.Fate, Yaw = o.Model.Yaw, Elevation = o.Model.Elevation,
        });
        _other = null;
        _otherCell = null;
        _ram = RamPhase.None;
    }

    // --- fate --------------------------------------------------------------------

    /// <summary>A round on the target unit (<see cref="Strike"/>,
    /// <see cref="Bursts"/>).</summary>
    private void UnitStruck(Other o, bool pierced)
    {
        if (!Match || !o.Unit)
            return;
        o.Fate = Fall(o.Fate, pierced, o.Model, o.Tag, OtherCell(o), o.Side);
        MatchCheckEnd();
    }

    /// <summary>A ricochet's or a pass's round on a standing unit
    /// (<see cref="StrikeParked"/>).</summary>
    private void UnitStruck(Parked p, bool pierced)
    {
        if (!Match)
            return;
        p.Fate = Fall(p.Fate, pierced, p.Model, p.Tag, p.Cell, p.Side);
        MatchCheckEnd();
    }

    /// <summary>What a round does to a unit's fate, and the picture of it: a
    /// pierce knocks it out, any round on a knocked one destroys it.</summary>
    private Fate Fall(Fate was, bool pierced, TankModel model, string tag, Vector2I cell, Side side)
    {
        if (was == Fate.Destroyed || (was == Fate.Alive && !pierced))
            return was;
        Vector3 at = model.Tank.ToGlobal(model.BlastAt);
        float hull = model.HullLength * model.PixelsPerUnit;
        if (was == Fate.Alive)
        {
            // The rig's knock-out pose, at its rest: the turret sat down and
            // tipped, the gun on its stop, the belts slack.
            model.Cant = 1.0f;
            model.Droop = 1.0f;
            model.Slackness = 1.0f;
            model.Apply();
            // in the pond it does not burn - it goes under (GDD states.md «Утоплен»)
            if (_field is not null && _field.IsDeep(cell))
                Founder(model, cell);
            else
                Kindle(model);
            // knocked out, it burns no more as a unit: the pyre is its fire now
            Quench(side, tag, steam: false);
            Boom(at, (float)TankTick.KnockOutFlash, grounded: false);
            _matchNote = $"{SideName(side)} {tag} подбит";
            Log.Print($"tank3d: match: {SideName(side)} {tag} on {MapAt(cell)} knocked out");
            return Fate.Knocked;
        }
        return Explode(model, tag, cell, side);
    }

    /// <summary>Destroyed - always a blast (GDD states.md, «Уничтожен»), whether
    /// a round found a knocked hull or a fire was left to burn: the picture,
    /// then the six hexes round it (<see cref="Blast"/>).</summary>
    private Fate Explode(TankModel model, string tag, Vector2I cell, Side side)
    {
        Vector3 at = model.Tank.ToGlobal(model.BlastAt);
        float hull = model.HullLength * model.PixelsPerUnit;
        Quench(side, tag, steam: false);
        // The ammunition: the turret off on to the deck where the sidecar lays
        // it, the hull charred over.
        if (model.Turret is not null)
        {
            TankModel.Landing land = model.LandingNear(Mathf.RadToDeg(model.Yaw) + 150.0f);
            model.TurretOverride = new Transform3D(new Basis(land.Rest), land.At);
        }
        Founder(model, cell);
        model.Droop = 1.0f;
        model.Slackness = 1.0f;
        model.Apply();
        if (_pyres.Find(p => p.Model == model) is { } pyre)
            pyre.Charred = true;
        Char(model, at, 1.6f * hull);
        Boom(at, 1.0f, grounded: true);
        _shake.Blast(_profile.ShotShake * 0.6);
        _matchNote = $"{SideName(side)} {tag} уничтожен";
        Log.Print($"tank3d: match: {SideName(side)} {tag} on {MapAt(cell)} destroyed");
        Blast(cell);
        return Fate.Destroyed;
    }

    /// <summary>How long a knocked unit burns at full, s; then the fire falls
    /// over <see cref="CelBurn.FireFall"/>.</summary>
    private const float PyreBurn = 4.0f;
    /// <summary>How much of the paint the char covers at its end - the
    /// shader's own <c>char_cover</c>: the whole tank greys evenly from 0 to
    /// this, rather than a grey patch spreading from the turret.</summary>
    private const float PyreCover = 0.9f;

    /// <summary>A knocked unit's short fire: it burns out, and the char grows
    /// from the turret over the whole tank as it does.</summary>
    private sealed class Pyre
    {
        public TankModel Model = null!;
        public CelBurn Burn = null!;
        public float Clock;
        /// <summary>Destroyed meanwhile: the char is full, the pyre lays no more.</summary>
        public bool Charred;
    }

    private readonly List<Pyre> _pyres = new();
    private readonly List<Vector3> _pyrePorts = new();

    /// <summary>The fire lit on a unit just knocked out.</summary>
    private void Kindle(TankModel model)
    {
        // the burn's own scorch is left out (no Paint): it grows with the
        // heat and goes when the fire does, and this char stays
        var burn = new CelBurn { Name = "Pyre" };
        AddChild(burn);
        burn.Build(model.HullLength * model.PixelsPerUnit);
        _pyres.Add(new Pyre { Model = model, Burn = burn });
        Char(model, model.Tank.ToGlobal(model.BlastAt), 1.6f * model.HullLength * model.PixelsPerUnit, 0.0f);
    }

    /// <summary>Each pyre a frame on: the fire up, then out; the char spread.</summary>
    private void PyreTick(float dt)
    {
        for (int i = _pyres.Count - 1; i >= 0; i--)
        {
            Pyre p = _pyres[i];
            if (!IsInstanceValid(p.Model))
            {
                p.Burn.QueueFree();
                _pyres.RemoveAt(i);
                continue;
            }
            p.Clock += dt;
            TankModel m = p.Model;
            bool lit = p.Clock < PyreBurn;
            p.Burn.Fire = lit ? 1.0f : 0.0f;
            p.Burn.Smoke = lit ? 1.0f : 0.0f;
            _pyrePorts.Clear();
            foreach (Node3D ex in m.Exhausts)
                _pyrePorts.Add(ex.GlobalPosition);
            if (_pyrePorts.Count == 0)
                _pyrePorts.Add(m.Tank.ToGlobal(m.BlastAt));
            p.Burn.TurretAt = m.Turret?.GlobalPosition;
            p.Burn.OverTurret = m.Turret is not null && m.TurretOverride is not null;
            p.Burn.Tick(dt, _pyrePorts, _camera.GlobalBasis);
            if (!p.Charred)
            {
                // the tank greys evenly while it burns and is grey whole by
                // the time the fire is out
                float k = Mathf.Clamp(p.Clock / (PyreBurn + p.Burn.FireFall), 0.0f, 1.0f);
                foreach (ShaderMaterial mat in m.Cel)
                    mat.Param("char_cover", PyreCover * k);
                if (k >= 1.0f)
                    p.Charred = true;
            }
            // gone once the fire is out and the last smoke has risen away
            if (p.Charred && !lit && p.Clock > PyreBurn + p.Burn.FireFall + p.Burn.PuffLife + 1.0f)
            {
                p.Burn.QueueFree();
                _pyres.RemoveAt(i);
            }
        }
    }

    /// <summary>The char of the cel shader's scorch: round
    /// <paramref name="at"/>, <paramref name="reach"/> world px - and kept
    /// on the hull after (<see cref="CharTick"/>).</summary>
    private void Char(TankModel model, Vector3 at, float reach, float cover = PyreCover)
    {
        Charred? c = null;
        foreach (Charred e in _charred)
            if (e.Model == model)
                c = e;
        if (c is null)
            _charred.Add(c = new Charred { Model = model });
        c.At = at;
        c.Spots[0] = c.Spots[1] = c.Spots[2] = c.Spots[3] = at;
        foreach (ShaderMaterial m in model.Cel)
        {
            m.Param("scorch_at", c.Spots);
            m.Param("scorch_n", 1);
            m.Param("scorch_r", reach);
            m.Param("scorch", 1.0f);
            m.Param("ember", 0.0f);
            m.Param("char_cover", cover);
        }
    }

    /// <summary>A charred hull and where its char was laid. The shader's
    /// scorch is a point in the world, so a knocked-out unit shoved by a ram
    /// (GDD states.md: «Подбитый и утопленный танк можно таранить») left its
    /// char standing on the old hex, a black block the hull drove out of
    /// (2026-10-29).</summary>
    private sealed class Charred
    {
        public TankModel Model = null!;
        public Vector3 At;
        public readonly Vector3[] Spots = new Vector3[4];
    }

    private readonly List<Charred> _charred = new();

    /// <summary>Each charred hull's char moved with it, when it moved.</summary>
    private void CharTick()
    {
        for (int i = _charred.Count - 1; i >= 0; i--)
        {
            Charred c = _charred[i];
            if (!IsInstanceValid(c.Model))
            {
                _charred.RemoveAt(i);
                continue;
            }
            Vector3 now = c.Model.Tank.ToGlobal(c.Model.BlastAt);
            if (now.DistanceSquaredTo(c.At) < 0.25f)
                continue;
            c.At = now;
            c.Spots[0] = c.Spots[1] = c.Spots[2] = c.Spots[3] = now;
            foreach (ShaderMaterial m in c.Model.Cel)
                m.Param("scorch_at", c.Spots);
        }
    }

    /// <summary>A model wiped as it changes hands carries no char.</summary>
    private void Uncharred(TankModel model)
    {
        for (int i = _charred.Count - 1; i >= 0; i--)
            if (_charred[i].Model == model)
                _charred.RemoveAt(i);
    }

    // --- on the screen -----------------------------------------------------------

    /// <summary>The banner of the turn and the result, the roll of both sides
    /// and the end of turn's button.</summary>
    private void BuildMatchUi(CanvasLayer layer)
    {
        if (!Match || _noUi)
            return;
        // the bench's panel shut: the roll and the button stand where it opens
        if (_panel is { Open: true })
            _panel.Flip();
        _banner = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            AnchorLeft = 0.5f, AnchorRight = 0.5f,
            OffsetLeft = -420.0f, OffsetRight = 420.0f, OffsetTop = 120.0f,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _banner.AddThemeFontSizeOverride("font_size", 36);
        _banner.AddThemeConstantOverride("outline_size", 10);
        _banner.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f));
        layer.AddChild(_banner);

        // Bottom right (2026-10-30, the author's choice): the turn's buttons
        // along the bottom, the acts by the card (Tank3DBench.Acts) and the
        // end of the turn in the far corner.
        var pass = new Button
        {
            Text = "Конец хода   Ctrl+Enter",
            AnchorLeft = 1.0f, AnchorRight = 1.0f, AnchorTop = 1.0f, AnchorBottom = 1.0f,
            OffsetLeft = -246.0f, OffsetRight = -12.0f, OffsetTop = -56.0f, OffsetBottom = -12.0f,
            FocusMode = Control.FocusModeEnum.None,
        };
        pass.AddThemeFontSizeOverride("font_size", 16);
        pass.Pressed += EndTurn;
        layer.AddChild(pass);
        _passButton = pass;
        BuildActs(layer);
    }

    private Button? _passButton;
    /// <summary><c>--do pass-hover</c>: the pointer held on the end of the turn, for a capture.</summary>
    private bool _passHeld;

    /// <summary>The end of the turn under the pointer: what passing it costs is
    /// shown on the board (<c>Tank3DBench.Badges</c>).</summary>
    private bool PassUnder => _passHeld || (_passButton?.IsHovered() ?? false);

    private string _bannerText = "";
    private Color _bannerInk = Colors.White;

    private void Banner(string text, Color ink, bool stay = false)
    {
        _bannerText = text;
        _bannerInk = ink;
        _bannerLeft = stay ? float.MaxValue : BannerFor;
        if (_banner is null)
            return;
        _banner.Text = text;
        _banner.AddThemeColorOverride("font_color", ink);
        _banner.Modulate = Colors.White;
    }

    /// <summary>The banner's text and colour, once the label is there.</summary>
    private void RollTick()
    {
        if (_banner is not null && _banner.Text != _bannerText)
        {
            _banner.Text = _bannerText;
            _banner.AddThemeColorOverride("font_color", _bannerInk);
        }
    }

    /// <summary>
    /// Both sides on one line of the HUD, unit by unit: ▶ the one on the rig,
    /// ◐ knocked out, ✕ destroyed. A plate of its own on the board's side stood
    /// on the right column's cells - the ghost of a firing post went under it.
    /// </summary>
    private string Roll()
    {
        var all = Units().ToList();
        var text = new System.Text.StringBuilder();
        foreach (Side s in new[] { Side.Green, Side.Brown })
        {
            text.Append(s == Side.Green ? "" : "   ·   ")
                .Append(s == _turn && !MatchOver ? "» " : "").Append(SideName(s)).Append(':');
            foreach (var u in all.Where(u => u.Side == s).OrderBy(u => Array.IndexOf(Roster, u.Tag)))
                text.Append(' ').Append(u.Riding ? "▶" : "").Append(u.Tag[..2])
                    .Append(u.Fate switch { Fate.Alive => "", Fate.Knocked => "◐", _ => "✕" })
                    .Append(u.Fate == Fate.Alive && Burning(u.Side, u.Tag) ? "▲" : "");
        }
        return text.Append("      ▲ горит  ◐ подбит  ✕ уничтожен").ToString();
    }

    /// <summary>What <see cref="MatchHud"/> and <see cref="Roll"/> read, into
    /// the HUD's stamp (<c>HudStamp</c>): the turn, and every unit's fate and
    /// fire - walked here without <see cref="Units"/>' iterator.</summary>
    private void MatchHudStamp(ref HashCode h)
    {
        h.Add(MatchOver);
        h.Add(_drawn);
        h.Add(_won);
        h.Add(_round);
        h.Add(_turn);
        h.Add(_drove);
        h.Add(_fired);
        h.Add(_bonus);
        h.Add(_side);
        // the countdown as it is printed, to a tenth
        h.Add(_passIn is float t ? Mathf.RoundToInt(Mathf.Max(t, 0.0f) * 10.0f) : -1);
        h.Add(_matchNote);
        h.Add(_fate);
        h.Add(Burning(_side, _modelTag));
        foreach (Parked p in _parked)
        {
            h.Add(p.Tag);
            h.Add(p.Side);
            h.Add(p.Fate);
            h.Add(Burning(p.Side, p.Tag));
        }
        if (_other is { Unit: true } o)
        {
            h.Add(o.Tag);
            h.Add(o.Side);
            h.Add(o.Fate);
            h.Add(Burning(o.Side, o.Tag));
        }
    }

    /// <summary>The HUD's lines in a match: where the turn stands and how to
    /// play it.</summary>
    private string MatchHud(string status) =>
        status + "\n"
        + (MatchOver
            ? (_drawn ? "Ничья" : $"Победа: {SideName(_won!.Value)}") + $" · раунд {_round}"
            : $"Раунд {_round} · ходят {SideName(_turn)} · {_modelTag}: ход {(_drove ? "сделан" : "есть")}, "
              + $"атака {(_fired ? "сделана" : "есть")}, доп. действие {(_bonus ? "сделано" : "есть")}"
              + (Burning(_side, _modelTag) ? $" · {_modelTag} горит: J — тушить, иначе сгорит в конце хода" : "")
              + (_passIn is float t ? $" · передача хода через {Mathf.Max(t, 0.0f):F1} с" : ""))
        + (_matchNote.Length > 0 ? $" · {_matchNote}" : "") + "\n"
        + Roll() + "\n"
        + "ЛКМ по своему — взять танк   ЛКМ по клетке — фиксация, ещё раз или Enter — ехать   A/D, колесо — разворот призрака   1, Tab — таран\n"
        + "ПКМ по танку — огонь, по своему тоже (не достать отсюда — ехать и стрелять)   2, J — тушить   Ctrl+Enter — конец хода   Backspace — заново   Esc — отмена   колесо — зум   Home — камера";
}
