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

    /// <summary>
    /// Where the match stands by the rules, apart from the board and the units
    /// (docs/battle-core-plan.md, Д1): whose turn, the round, what the unit on
    /// the rig has spent, the result. One object, so the rules core (Д3) takes
    /// it whole into its state - the bench's pacing (<see cref="_passIn"/>,
    /// the banner, the notes) stays out of it.
    /// </summary>
    private sealed class MatchTurn
    {
        public Side Turn = Side.Green;
        /// <summary>Player 1, who moves first in every round (<see cref="BattleState.First"/>).</summary>
        public Side First = Side.Green;
        public int Round = 1;
        /// <summary>Turns played, the first nought: a fire's clock (Tank3DBench.Blast).</summary>
        public int TurnNo;
        /// <summary>What the unit on the rig has done this turn: the move, the
        /// attack (the shot) and the bonus act (the ram, putting a fire out) -
        /// match.md «Структура хода», п. 5а and 5б, one of each (U-22).</summary>
        public bool Drove, Fired, Bonus;
        /// <summary>The forced engine said this turn (<see cref="BattleState.Overdrive"/>).</summary>
        public bool Overdrive;
        /// <summary>The round of the last shot at a tank, nought for none -
        /// the stalemate's clock (Tank3DBench.Rules, Д6).</summary>
        public int LastStrike;
        /// <summary>Each side's points left on the control point, green then
        /// brown (Tank3DBench.Point, Д9); read only with a point.</summary>
        public int[] Score = new int[2];
        /// <summary>Who held the point at the last field tick; null for nobody.</summary>
        public Side? Holder;
        /// <summary>The round the point last changed hands in, nought for never.</summary>
        public int LastShift;
        public Side? Won;
        public bool Drawn;

        /// <summary>Whole rounds since the last shot at a tank or the point
        /// changing hands (<see cref="BattleState.QuietRounds"/>).</summary>
        public int QuietRounds => Mathf.Max(0, Round - 1 - Mathf.Max(LastStrike, LastShift));
    }

    private readonly MatchTurn _mt = new();
    /// <summary>Seconds to the turn passing by itself; null while it waits.</summary>
    private float? _passIn;
    /// <summary>When the turn last went by itself (<see cref="_passIn"/> run
    /// out), ms of <c>Time.GetTicksMsec</c>; 0 for never.</summary>
    private ulong _passedByItself;
    /// <summary>A hand's pass this soon after the turn went by itself was meant
    /// for the turn that went, ms - the new turn's banner (<see cref="BannerFor"/>).</summary>
    private const ulong PassGrace = 2200;
    private string _matchNote = "";
    /// <summary><c>--map</c> was given: the match plays it, not its own board.</summary>
    private bool _mapNamed;

    private Label? _banner;
    private float _bannerLeft;

    private static string SideName(Side s) => s == Side.Green ? L.T("match.side_green") : L.T("match.side_brown");
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
        // Player 1 (match.md, «Расстановка и первый ход»): a toss in the quick battle.
        // A run by script - commands, a capture, a recipe, a measure - is not
        // a game and keeps green first, so it plays the same each time;
        // --first says either side for one.
        bool scripted = _commands.Count > 0 || _capturePath is not null || _sequenceDir is not null
                        || _script.Count > 0 || _perfDir is not null;
        _tossed = _firstSaid is null && !scripted;
        // the OS's seed: the engine's GD.Randi came up brown four runs of four
        _mt.First = _firstSaid ?? (_tossed && Random.Shared.Next(2) == 1 ? Side.Brown : Side.Green);
        _side = _mt.Turn = _mt.First;
        Log.Print($"tank3d: match on {_mapName}, {Roster.Length} a side; stalemate - {MatchRules.Said}; "
                 + $"{SideName(_mt.First)} move first - {(_firstSaid is not null ? "--first" : _tossed ? "the toss" : "a script keeps green")}");
    }

    /// <summary><c>--first</c>: player 1 said; null tosses.</summary>
    private Side? _firstSaid;

    /// <summary>Player 1 came by the toss: the first banner says so.</summary>
    private bool _tossed;

    /// <summary>The first banner's word on the toss.</summary>
    private string TossSaid => _tossed ? L.T("match.toss", SideName(_mt.First)) : "";

    // --- the sides -------------------------------------------------------------

    /// <summary>The match's units in the homes' order (<see cref="Lineup.Of"/>);
    /// the first green one rides the rig.</summary>
    private List<Lineup.Unit> _lineup = new();

    /// <summary>The unit on the rig at the start, an index into <see cref="_lineup"/>.</summary>
    private int _rigUnit = -1;

    /// <summary>
    /// The unit on the rig now - its place in <see cref="_lineup"/>, which is
    /// its name for the whole match (docs/battle-core-plan.md, Д1). The side
    /// and the class were the name before, and two of one class on a side
    /// shared a fire. -1 off a match. Standing units and the target carry
    /// theirs (<c>Parked.Id</c>, <c>Other.Id</c>); a hand-over swaps them.
    /// </summary>
    private int _unitId = -1;

    /// <summary>Each unit's kit, by its id: its home's (<see cref="Parking.Kit"/>), else <see cref="Kits.Starter"/>.</summary>
    private Kit[] _kits = Array.Empty<Kit>();

    /// <summary>Whether unit <paramref name="id"/> has the wading gear - off
    /// a match, the bench's own switch for every tank (<c>--no-amphibious</c>).</summary>
    private bool Snorkel(int id) =>
        !Match ? _amphibious : id >= 0 && id < _kits.Length && (_kits[id] & Kit.Snorkel) != 0;

    /// <summary>The unit on the rig has the wading gear.</summary>
    private bool RigSnorkel => Snorkel(_unitId);

    /// <summary>Unit <paramref name="id"/>'s kit; none off a match (Д8: the
    /// trawl and the smoke grenade are read off it).</summary>
    private Kit KitOf(int id) => Match && id >= 0 && id < _kits.Length ? _kits[id] : Kit.None;

    /// <summary>How the unit on the rig moves: its class, its wading gear and
    /// what else its kit says of driving (upgrades.md, Д8: the trawl, wide
    /// tracks, the reverse gear, the double turn - <see cref="Steps.Kitted"/>).</summary>
    private Steps.Mover RigMover(IReadOnlySet<Vector2I>? barred = null) =>
        Steps.Kitted(Steps.Mover.Of(_profile, RigSnorkel, barred), KitOf(_unitId));

    private static Side SideOf(Lineup.Unit u) => u.Side == Parking.Brown ? Side.Brown : Side.Green;

    /// <summary>
    /// Who stands on which home (<see cref="Lineup.Of"/>, the editor's rule
    /// too), before the rig is mounted - the rig's model and cell come off it.
    /// </summary>
    private void LineUpSides()
    {
        _lineup = new List<Lineup.Unit>();
        _rigUnit = _unitId = -1;
        _kits = Array.Empty<Kit>();
        if (!Match)
            return;
        // a quick battle: the two squads on the homes, each machine its kit (Tank3DBench.Quick)
        QuickHomes();
        _lineup = Lineup.Of(_parkings);
        // the home's kit, else the starting squad's stub by the class (Р-4, Д8)
        _kits = _lineup.Select(u => _parkings[u.Slot].Kit ?? Kits.Starter(ClassFor(u.Model))).ToArray();
        Log.Print("tank3d: match: kits "
                 + string.Join(", ", _lineup.Select((u, i) => $"{SideName(SideOf(u))} {u.Model}#{i} {Kits.Names(_kits[i])}")));
        int green = _lineup.Count(u => u.Side == Parking.Green), brown = _lineup.Count - green;
        Log.Print($"tank3d: match: the board fields {green} green and {brown} brown");
        // a side with no homes has nothing to move, and the match is won before it starts
        if (green == 0 || brown == 0)
            Log.Print($"tank3d: match: one side has no homes - {green} green, {brown} brown");
        if (_lineup.Count == 0)
            return;
        // player 1's first unit rides the rig
        string first = _mt.First == Side.Brown ? Parking.Brown : Parking.Green;
        _rigUnit = Math.Max(0, _lineup.FindIndex(u => u.Side == first));
        _unitId = _rigUnit;
        Lineup.Unit rig = _lineup[_rigUnit];
        _modelTag = rig.Model;
        _side = _mt.Turn = SideOf(rig);
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
            Stand(u.Model, u.Cell, HeadingFor(u.Cell, u.Facing), SideOf(u), id: i);
        }
        Log.Print($"tank3d: match: {SideName(_side)} {_modelTag} {MapAt(CellHere)} on the rig; standing "
                 + string.Join(", ", _parked.Select(p => $"{SideName(p.Side)} {p.Tag} {MapAt(p.Cell)}")));
        // the placement before the first turn, on a map with a zone (Tank3DBench.Deploy);
        // its «В бой» says the round, and the first side's banner the toss
        DeployStart();
        if (!Deploying)
            Banner(L.T("match.round1_banner", TossSaid, SideName(_mt.Turn)), SideInk(_mt.Turn));
    }

    /// <summary>The side's paint on a model (<see cref="Livery"/>): in a quick battle
    /// the squad's, the hangar's choice, and the AI's own; else the green side's the
    /// model's own and the brown side's brown, as they were.</summary>
    private void SidePaint(TankModel model, Side side)
    {
        if (!Match)
            return;
        Lineup.Paint(model, Parking.Green);
        Livery.Apply(model, PaintOf(side));
    }

    /// <summary>The paint <paramref name="side"/>'s machines wear (<see cref="SidePaint"/>).</summary>
    private string PaintOf(Side side)
    {
        QuickSetup? q = _quick ?? (Quick ? _qs : null);
        if (q is null)
            return side == Side.Brown ? Livery.Brown : Livery.Own;
        if (side == q.Player)
            return q.Paint;
        // the paint is all that tells the sides apart (the roof's mark was taken off,
        // author 2026-11-04): the AI never in the player's colour
        if (Livery.ColourOf(q.AiPaint) != Livery.ColourOf(q.Paint))
            return q.AiPaint;
        return Livery.ColourOf(q.Paint) == Livery.Brown ? Livery.Own : Livery.Brown;
    }

    /// <summary>Every unit on the board, the rig's first.</summary>
    private IEnumerable<(int Id, string Tag, Side Side, Fate Fate, bool Riding)> Units()
    {
        yield return (_unitId, _modelTag, _side, _fate, true);
        foreach (Parked p in _parked)
            yield return (p.Id, p.Tag, p.Side, p.Fate, false);
        if (_other is { Unit: true } o)
            yield return (o.Id, o.Tag, o.Side, o.Fate, false);
    }

    // --- the turn ----------------------------------------------------------------

    /// <summary>Something of the turn is still under way: a route, a turn on
    /// the spot, the gun laying, a round in the air.</summary>
    private bool MatchBusy =>
        RouteMoving || _turnGoal is not null || _aim is not null || _shotWaiting is not null
        || (_shells?.Rounds.Count ?? 0) > 0 || RamRunning || _ramWaiting is not null || _chain.Count > 0
        // the hull rolling on after a mine, a wall being rammed (Д5: a command
        // given then went in under them)
        || _coastTo is not null || _wallRam
        // a hull on the winch's cable (Tank3DBench.Gear)
        || _pull is not null
        // an ambush round waiting for its hull (Tank3DBench.Ambush)
        || _ambushDue.Count > 0
        // the rest of a move stopped for its shot (Tank3DBench.OnWay)
        || _onWayRest is not null;

    private bool MatchOver => _mt.Won is not null || _mt.Drawn;

    /// <summary>Why the unit on the rig may not move now, or empty.</summary>
    private string MatchMoveWhy()
    {
        if (!Match)
            return "";
        if (MatchOver)
            return L.T("match.over");
        if (Deploying)
            return L.T("match.deploying");
        if (_side != _mt.Turn)
            return L.T("match.side_to_move", SideName(_mt.Turn));
        if (_passIn is not null)
            return L.T("match.passing");
        if (_mt.Drove)
            return L.T("match.already_moved", _modelTag);
        if (RigPinned)
            return L.T("match.pinned_no_move", _modelTag);
        // the forced engine is said before the move (Battle.Overdrive)
        if ((_mt.Fired || (_mt.Bonus && !_mt.Overdrive)) && !_profile.FreeOrder)
            return L.T("match.no_move_after_act");
        return "";
    }

    private bool MatchMayMove => MatchMoveWhy().Length == 0;

    /// <summary>The shot of this turn is spent - fired, or the turn going over:
    /// no line of fire nor firing post is drawn for a shot there is not.</summary>
    private bool MatchShotSpent => Match && (_mt.Fired || _passIn is not null || MatchOver || Deploying);

    /// <summary>The bonus act of this turn is spent - the ram or the fire put
    /// out, or the turn going over.</summary>
    private static string BonusWaits => L.T("match.bonus_waits");

    private bool MatchBonusSpent => Match && (_mt.Bonus || RigOwes || _passIn is not null || MatchOver || Deploying);

    /// <summary>Units rammed by a ram blade (upgrades.md, «Таранный отвал»):
    /// no move in their next activation (<see cref="BattleUnit.Pinned"/>).</summary>
    private readonly HashSet<int> _pinned = new();

    /// <summary>The unit on the rig is pinned: its move is not to be had.</summary>
    private bool RigPinned => Match && _pinned.Contains(_unitId);

    /// <summary>Whether a bonus act is still to be had once the move is over:
    /// a fire to put out, a unit beside to ram - the move being spent, the
    /// ram goes from where the hull stands - or the double turn (Battle.TurnDone).
    /// None while it is owed to the extinguisher. No allocation, it is read
    /// every frame.</summary>
    private bool BonusLeft()
    {
        if (_mt.Bonus || RigOwes)
            return false;
        if (Burning(_unitId))
            return true;
        if (RigHas(Kit.DoubleTurn) && !(_field?.IsRamp(CellHere) ?? false))
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
            return L.T("match.over");
        if (Deploying)
            return L.T("match.deploying");
        if (_side != _mt.Turn)
            return L.T("match.side_to_move", SideName(_mt.Turn));
        if (_passIn is not null)
            return L.T("match.passing");
        if (_mt.Fired)
            return L.T("match.already_fired", _modelTag);
        return "";
    }

    /// <summary>A route or a turn on the spot was given: the move of the turn.</summary>
    private void MatchMoved()
    {
        if (!Match || _mt.Drove)
            return;
        _mt.Drove = true;
        // the unit as it set out, for the move taken back (Tank3DBench.Undo)
        UndoSnap();
        Log.Print($"tank3d: match: {SideName(_side)} {_modelTag} moves");
    }

    /// <summary>The gun fired: the action of the turn.</summary>
    private void MatchFired()
    {
        if (!Match)
            return;
        _mt.Fired = true;
        Unlock();
        Log.Print($"tank3d: match: {SideName(_side)} {_modelTag} fires");
    }

    /// <summary>Whether a click on a standing unit takes it: one of the side
    /// whose turn it is, able to act, while the one on the rig has done
    /// nothing yet.</summary>
    private bool MatchTakes(Parked p)
    {
        string why = TakeWhy(p);
        if (why.Length == 0)
            return true;
        _orderNote = why;
        return false;
    }

    /// <summary>Why a click would not take the standing unit, or empty.</summary>
    private string TakeWhy(Parked p) =>
        Takeable(p) ? ""
            : MatchOver ? L.T("match.over")
            : p.Side != _mt.Turn ? L.T("match.take_other_side", p.Tag, SideName(p.Side), SideName(_mt.Turn))
            : p.Fate != Fate.Alive ? L.T(p.Fate == Fate.Knocked ? "match.unit_knocked" : "match.unit_destroyed", p.Tag)
            : _passIn is not null ? L.T("match.passing")
            : _side == _mt.Turn && (_mt.Drove || _mt.Fired || _mt.Bonus) ? L.T("match.other_acting", _modelTag)
            : L.T("match.wait_order");

    /// <summary>The same without the words - the roster asks it every frame
    /// for its dimmed icons, and the words allocate.</summary>
    private bool Takeable(Parked p) =>
        !Match
        || (!MatchOver && p.Side == _mt.Turn && p.Fate == Fate.Alive && _passIn is null
            && !(_side == _mt.Turn && (_mt.Drove || _mt.Fired || _mt.Bonus)) && !MatchBusy);

    /// <summary>Ctrl+Enter and the button: the turn handed over now - with
    /// nothing done, a pass (match.md, «Пас разрешён»).</summary>
    private void EndTurn()
    {
        if (!Match || MatchOver)
            return;
        // in placement the button is «В бой» (Tank3DBench.Deploy)
        if (Deploying)
        {
            Execute(new Command(CommandKind.Ready));
            return;
        }
        if (MatchBusy)
        {
            _matchNote = L.T("match.wait_order");
            return;
        }
        Log.Print($"tank3d: match: {SideName(_mt.Turn)} end the turn"
                 + (_side == _mt.Turn && !_mt.Drove && !_mt.Fired && !_mt.Bonus ? " - a pass" : ""));
        PassTurn();
    }

    /// <summary>Ctrl+Enter and the button: <see cref="EndTurn"/>, unless the
    /// turn has just gone by itself and the new side has done nothing yet -
    /// the press was meant for the turn that went. An ambush spends the whole
    /// turn, it went on its own while the player reached for the button, and
    /// the press passed the other side's turn back (2026-10-31). A command's
    /// pass (<c>--commands</c>) is not a hand and is not held.</summary>
    private void PassByHand()
    {
        if (!Deploying && !MatchOver && _passedByItself > 0
            && Time.GetTicksMsec() - _passedByItself < PassGrace
            && !(_side == _mt.Turn && (_mt.Drove || _mt.Fired || _mt.Bonus)))
        {
            Log.Print($"tank3d: match: a pass {Time.GetTicksMsec() - _passedByItself} ms after the turn went by itself - held");
            _matchNote = L.T("match.already_passed");
            return;
        }
        Execute(new Command(CommandKind.Pass));
    }

    /// <summary>To the other side, and the side that ended its turn checked for
    /// fires left burning (Tank3DBench.Blast) - after the hand-over, so a unit
    /// that burns out stands, as every other, rather than rides.</summary>
    private void PassTurn()
    {
        // the rules core read against the bench (Tank3DBench.Shadow)
        ShadowTurnOver();
        // the heavy's wood (Tank3DBench.Rules)
        HeavyClears();
        Side ending = _mt.Turn;
        int turn = _mt.TurnNo++;
        // the unit the side moved or acted with, and whether its bonus act is
        // spent, for the auto-extinguisher's price (upgrades.md, 2026-11-02)
        int activated = _side == ending && (_mt.Drove || _mt.Fired || _mt.Bonus) ? _unitId : -1;
        bool bonusSpent = _mt.Bonus;
        // the activation the ram blade pinned it for is over (Battle.EndTurn)
        if (activated >= 0 && _pinned.Remove(activated))
            Log.Print($"tank3d: match: #{activated} went this activation without its move - the ram blade lets it go");
        HandOver();
        if (!MatchOver)
            BurnOut(ending, turn, activated, bonusSpent);
    }

    /// <summary>To the other side: its first unit able to act on the rig. A
    /// side with none passes on.</summary>
    private void HandOver()
    {
        _passIn = null;
        _ramMode = false;
        _smokeMode = false;
        _fieryMode = _fiery = false;
        _smokeShotMode = false;
        OnWayDrop();
        _gearMode = Gear.None;
        _ambushMode = _attackMode = false;
        _ambushPick = null;
        _matchNote = "";
        CancelOrder();
        DropTarget();
        _mt.Drove = _mt.Fired = _mt.Bonus = _mt.Overdrive = false;
        _moveSnap = null;
        for (int tries = 0; tries < 2; tries++)
        {
            _mt.Turn = Across(_mt.Turn);
            // the round over once player 2 has moved (match.md, «Ход и раунд»)
            if (_mt.Turn == _mt.First)
            {
                FieldTick();
                _mt.Round++;
            }
            if (MatchCheckEnd())
                return;
            if (FirstReady(_mt.Turn) is { } next)
            {
                TakeOver(next);
                // the view round to the side to move (Tank3DBench.Deploy)
                ViewToSide(_mt.Turn);
                Banner(L.T("match.round_banner", _mt.Round, SideName(_mt.Turn)), SideInk(_mt.Turn));
                Log.Print($"tank3d: match: round {_mt.Round}, {SideName(_mt.Turn)} to move, {_modelTag} on {MapAt(CellHere)}");
                return;
            }
            if (_side == _mt.Turn && _fate == Fate.Alive)
            {
                ViewToSide(_mt.Turn);
                Banner(L.T("match.round_banner", _mt.Round, SideName(_mt.Turn)), SideInk(_mt.Turn));
                return;
            }
            Log.Print($"tank3d: match: {SideName(_mt.Turn)} have nothing able to act - a pass");
            _matchNote = L.T("match.nobody_pass", SideName(_mt.Turn));
        }
        _mt.Drawn = true;
        Banner(L.T("match.draw_nobody"), Colors.White, stay: true);
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
            _mt.Won = Across(s);
            _passIn = null;
            Banner(L.T("match.victory_round", SideName(_mt.Won.Value), _mt.Round), SideInk(_mt.Won.Value), stay: true);
            Log.Print($"tank3d: match: {SideName(s)} destroyed to the last - {SideName(_mt.Won.Value)} win in round {_mt.Round}");
            return true;
        }
        // out of points on the control point (Tank3DBench.Point)
        return PointOver();
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
        OnWayTick();
        if (_banner is not null && _bannerLeft > 0.0f)
        {
            _bannerLeft -= dt;
            _banner.Modulate = new Color(1, 1, 1, Mathf.Clamp(_bannerLeft / 0.5f, 0.0f, 1.0f));
        }
        RollTick();
        // the view's turn from placement into battle (Tank3DBench.Deploy)
        CameraTurnTick(dt);
        // the units let go, their turrets home (Tank3DBench.Parked)
        ParkedTurretsTick(dt);
        bool busy = MatchBusy;
        // the placement before the first turn (Tank3DBench.Deploy): nothing of a turn yet
        if (Deploying)
        {
            DeployTick(busy);
            return;
        }
        // the control point's tick, owed from the hand-over (Tank3DBench.Point)
        PointDueTick(busy);
        // the stalemate, once the hand-over's burning out is done (Tank3DBench.Rules)
        StalemateTick(busy);
        // the rules core takes the turn off the bench (Tank3DBench.Shadow)
        ShadowTick(busy);
        // the smoke on the board, drawn (Tank3DBench.Smoke)
        SmokeTick(dt);
        // the control point on the board (Tank3DBench.Point)
        PointDraw();
        // the winch's pull (Tank3DBench.Gear)
        PullTick(dt);
        // the watchers out of the fight out of ambush (Tank3DBench.Ambush)
        AmbushTick();
        // the ambush rounds waiting for their hull to come on to the axis (Tank3DBench.Ambush)
        AmbushDueTick();
        // what a hex does to a hull coming on to it (Tank3DBench.Rules)
        HexEffectsTick();
        // --commands, one at a time (Tank3DBench.Commands)
        CommandsTick(busy);
        if (_other is { Unit: true, Fate: Fate.Destroyed } && !busy)
            DropTarget();
        if (MatchOver)
            return;
        // the turn goes by itself once there is nothing left in it: the shot
        // fired, the move done (or barred), and no bonus act to be had - one
        // still open waits for it or for the end of the turn (U-22)
        if (_passIn is null && !busy && _side == _mt.Turn && _mt.Fired && (_mt.Drove || RigPinned || !_profile.FreeOrder) && !BonusLeft())
        {
            _passIn = PassDelay;
            _matchNote = L.T("match.turn_done");
        }
        else if (_passIn is null && !busy && _side == _mt.Turn && _mt.Fired && (_mt.Drove || !_profile.FreeOrder))
            _matchNote = BonusWaits;
        // Knocked out on its own move - a mine under it (Tank3DBench.Mines):
        // it can do no more, so the turn goes on, once the hull has rolled to
        // a stop. It waited for a shot that could not come (2026-10-29).
        if (_passIn is null && !busy && _side == _mt.Turn && _fate != Fate.Alive && _coastTo is null)
        {
            _passIn = PassDelay;
            _matchNote = L.T("match.unit_knocked", _modelTag);
        }
        if (_passIn is float left && !busy)
        {
            _passIn = left - dt;
            if (_passIn <= 0.0f)
            {
                PassTurn();
                _passedByItself = Time.GetTicksMsec();
            }
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
    /// used to want Ctrl held (2026-10-28), then nothing (2026-10-29); now
    /// an own unit is a target only with «Атака» or the ram on - by default
    /// ours are not fired on (the author's note, 2026-10-31; Tank3DBench.Acts).
    /// True when it now is the target.
    /// </summary>
    private bool MatchAims(Parked p)
    {
        // none in the placement: there is nothing to fire at, and the hidden side is not to be found by the pointer
        if (!Match || Deploying || p.Fate == Fate.Destroyed || p.Side == _side && !_attackMode && !_ramMode)
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
        _other.Id = p.Id;
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
            Side = o.Side, Fate = o.Fate, Yaw = o.Model.Yaw, Elevation = o.Model.Elevation, Id = o.Id,
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
        o.Fate = Fall(o.Fate, pierced, o.Model, o.Id, o.Tag, OtherCell(o), o.Side);
        MatchCheckEnd();
    }

    /// <summary>A ricochet's or a pass's round on a standing unit
    /// (<see cref="StrikeParked"/>).</summary>
    private void UnitStruck(Parked p, bool pierced)
    {
        if (!Match)
            return;
        p.Fate = Fall(p.Fate, pierced, p.Model, p.Id, p.Tag, p.Cell, p.Side);
        MatchCheckEnd();
    }

    /// <summary>What a round does to a unit's fate, and the picture of it: a
    /// pierce knocks it out, any round on a knocked one destroys it.</summary>
    private Fate Fall(Fate was, bool pierced, TankModel model, int id, string tag, Vector2I cell, Side side)
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
            Quench(id, steam: false);
            Boom(at, (float)TankTick.KnockOutFlash, grounded: false);
            _matchNote = L.T("match.side_unit_knocked", SideName(side), tag);
            Log.Print($"tank3d: match: {SideName(side)} {tag} on {MapAt(cell)} knocked out");
            return Fate.Knocked;
        }
        return Explode(model, id, tag, cell, side);
    }

    /// <summary>Destroyed - always a blast (GDD states.md, «Уничтожен»), whether
    /// a round found a knocked hull or a fire was left to burn: the picture,
    /// then the six hexes round it (<see cref="Blast"/>).</summary>
    private Fate Explode(TankModel model, int id, string tag, Vector2I cell, Side side)
    {
        Vector3 at = model.Tank.ToGlobal(model.BlastAt);
        float hull = model.HullLength * model.PixelsPerUnit;
        Quench(id, steam: false);
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
        _matchNote = L.T("match.side_unit_destroyed", SideName(side), tag);
        Log.Print($"tank3d: match: {SideName(side)} {tag} on {MapAt(cell)} destroyed");
        Blast(cell);
        // ...and the wood under it and round it catches, the walls on its hex
        // come down (GDD states.md, «Уничтожен»; П-13, Д4) - the own tank's
        // blast did it (FxDestroyed), a unit's in a match did not. Afloat there
        // is no blast wave.
        bool wet = _field is not null && _field.IsDeep(cell);
        WoodIgnite(Foot(at), grounded: !wet);
        if (!wet)
            WallsBlown(Foot(at), "the blast", TankBlast);
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
        _banner.AddThemeFontSizeOverride("font_size", Ui.Display);
        _banner.AddThemeConstantOverride("outline_size", 10);
        _banner.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f));
        layer.AddChild(_banner);

        // Bottom right (2026-10-30, the author's choice): the turn's buttons
        // along the bottom, the acts by the card (Tank3DBench.Acts) and the
        // end of the turn in the far corner.
        // An icon and no text (the author's note, 2026-11-01): the name, the
        // key and what it does on its card under the pointer (Tank3DBench.ActTips).
        var pass = new Button
        {
            AnchorLeft = 1.0f, AnchorRight = 1.0f, AnchorTop = 1.0f, AnchorBottom = 1.0f,
            OffsetLeft = -12.0f - PassSide, OffsetRight = -12.0f, OffsetTop = -12.0f - PassSide, OffsetBottom = -12.0f,
            FocusMode = Control.FocusModeEnum.None,
        };
        pass.Pressed += PassByHand;
        TurnButton(pass, "endturn", PassIconPx, "Enter");
        layer.AddChild(pass);
        _passButton = pass;
        BuildUndo(layer);
        BuildActs(layer);
        BuildCursors();
        BuildRoster(layer);
        // the placement's panel, where the acts stand in the battle (Tank3DBench.Deploy)
        BuildDeployPanel(layer);
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
                .Append(s == _mt.Turn && !MatchOver ? "» " : "").Append(SideName(s)).Append(':');
            foreach (var u in all.Where(u => u.Side == s).OrderBy(u => Array.IndexOf(Roster, u.Tag)))
                text.Append(' ').Append(u.Riding ? "▶" : "").Append(u.Tag[..2])
                    .Append(u.Fate switch { Fate.Alive => "", Fate.Knocked => "◐", _ => "✕" })
                    .Append(u.Fate == Fate.Alive && Burning(u.Id) ? "▲" : "");
        }
        return text.Append(L.T("match.roll_legend")).ToString();
    }

    /// <summary>What <see cref="MatchHud"/> and <see cref="Roll"/> read, into
    /// the HUD's stamp (<c>HudStamp</c>): the turn, and every unit's fate and
    /// fire - walked here without <see cref="Units"/>' iterator.</summary>
    private void MatchHudStamp(ref HashCode h)
    {
        h.Add(MatchOver);
        h.Add(_mt.Drawn);
        h.Add(_mt.Won);
        h.Add(_mt.Round);
        h.Add(_mt.LastStrike);
        h.Add(_mt.LastShift);
        h.Add(_mt.Score[0]);
        h.Add(_mt.Score[1]);
        h.Add(PointHolder());
        h.Add(_ambush.Count);
        h.Add(_ambush.TryGetValue(_unitId, out var watching) ? watching.Shots : 0);
        h.Add(_mt.Turn);
        h.Add(_mt.Drove);
        h.Add(_deploy);
        h.Add(_orderNote);
        h.Add(_mt.Fired);
        h.Add(_mt.Bonus);
        h.Add(_side);
        // the countdown as it is printed, to a tenth
        h.Add(_passIn is float t ? Mathf.RoundToInt(Mathf.Max(t, 0.0f) * 10.0f) : -1);
        h.Add(_matchNote);
        h.Add(_hints);
        h.Add(_fate);
        h.Add(_unitId);
        h.Add(Burning(_unitId));
        foreach (Parked p in _parked)
        {
            h.Add(p.Id);
            h.Add(p.Tag);
            h.Add(p.Side);
            h.Add(p.Fate);
            h.Add(Burning(p.Id));
        }
        if (_other is { Unit: true } o)
        {
            h.Add(o.Id);
            h.Add(o.Tag);
            h.Add(o.Side);
            h.Add(o.Fate);
            h.Add(Burning(o.Id));
        }
    }

    /// <summary>F1 in a match: both sides' line and the controls under the
    /// turn's line. Off by default - the roster down the left edge stands
    /// where they were (Tank3DBench.Roster).</summary>
    private bool _hints;

    /// <summary>The HUD's lines in a match: where the turn stands, and with F1
    /// how to play it.</summary>
    private string MatchHud(string status) =>
        // the placement before the first turn (Tank3DBench.Deploy)
        Deploying ? status + "\n" + DeployHud() + PointHud() :
        status + "\n"
        + (MatchOver
            ? (_mt.Drawn ? L.T("match.draw") : L.T("match.victory", SideName(_mt.Won!.Value))) + L.T("match.round_tail", _mt.Round) + PointHud()
            : L.T("match.round", _mt.Round)
              // the stalemate's count, once there is one (Tank3DBench.Rules)
              + (_mt.QuietRounds > 0 ? L.T("match.quiet", _mt.QuietRounds, MatchRules.QuietRoundsDraw) : "")
              // the control point and the points (Tank3DBench.Point)
              + PointHud()
              + L.T("match.turn_state", SideName(_mt.Turn), _modelTag,
                    L.T(_mt.Drove ? "match.state_move_done" : "match.state_left"),
                    L.T(_mt.Fired ? "match.state_attack_done" : "match.state_left"),
                    L.T(_mt.Bonus ? "match.state_bonus_done" : "match.state_left"))
              // its upgrades (Д8): the passive ones show nowhere else
              + (KitOf(_unitId) != Kit.None ? L.T("match.upgrades", Kits.Names(KitOf(_unitId))) : "")
              + (Burning(_unitId) ? L.T("match.burning", _modelTag) : "")
              // its ambush (Tank3DBench.Ambush): any order lifts it
              + (_ambush.TryGetValue(_unitId, out var watch) ? L.T("match.ambush", _modelTag, watch.Shots > 1 ? L.T("match.ambush_shots", watch.Shots) : "") : "")
              + (_passIn is float t ? L.T("match.pass_in", Mathf.Max(t, 0.0f)) : ""))
        + (_matchNote.Length > 0 ? $" · {_matchNote}" : "")
        + (!_hints ? L.T("match.hints_off")
            : "\n" + Roll() + "\n"
              + L.T("match.hints"));
}
