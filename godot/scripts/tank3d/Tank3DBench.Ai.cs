using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The AI at the wheel of a side (docs/ai-plan.md, «Как ИИ ходит на стенде»,
/// И1). On its side's turn the bench reads the match off itself
/// (<see cref="ShadowOfBench"/>), the AI chooses on that copy off the main
/// thread (<see cref="AiBrain.Choose"/>) while «ИИ думает» stands in the
/// match's line, and the play's orders are then given one at a time through
/// the same gate as the player's and <c>--commands</c>' (<see cref="Execute"/>):
/// each once the bench has been quiet for a few frames, the take followed by
/// a beat for the camera to arrive. An order refused - the bench and the core
/// disagree - drops the rest and hands the turn over. Meanwhile the player
/// has the camera only: no clicks on the board, no order keys, no act panel.
/// <list type="bullet">
/// <item>A quick battle: the AI drives the side that is not the player's.</item>
/// <item><c>--ai SIDE[:LEVEL]</c> on any match: <c>green</c>, <c>brown</c>,
/// <c>both</c> or <c>off</c>; LEVEL <c>recruit</c>, <c>fighter</c> (default),
/// <c>veteran</c>. <c>--ai-seed N</c>: the AI's dice, a random seed without
/// it (logged, so a match can be played again).</item>
/// </list>
/// </summary>
public sealed partial class Tank3DBench
{
    /// <summary><c>--ai</c>'s sides, null for the match's own rule (the quick
    /// battle's other side, nobody elsewhere).</summary>
    private string? _aiSides;
    /// <summary><c>--ai SIDE:LEVEL</c>'s level, null when not said.</summary>
    private AiLevel? _aiLevelSaid;

    /// <summary>The level the AI plays at: the flag's, else the quick
    /// battle's choice (<see cref="QuickSetup.Level"/>), else the fighter.</summary>
    private AiLevel _aiLevel => _aiLevelSaid ?? _quick?.Level ?? AiLevel.Fighter;
    private Random? _aiRng;
    private int _aiSeed = -1;
    private Task<AiChoice>? _aiThink;
    /// <summary>The play's orders still to give, in <c>--commands</c>' words.</summary>
    private readonly Queue<string> _aiQueue = new();
    /// <summary>The turn the queue was chosen for; -1 for none.</summary>
    private int _aiTurnNo = -1;
    private int _aiQuiet;
    /// <summary>Seconds to hold before the next order: the camera's beat after a take.</summary>
    private float _aiHold;

    /// <summary>Quiet frames in a row before the AI's next order.</summary>
    private const int AiQuietFrames = 10;
    /// <summary>The beat after the take: the camera on the unit, the player reads who moves.</summary>
    private const float AiTakeHold = 0.6f;

    /// <summary>Reads <c>--ai</c> and <c>--ai-seed</c>; true for an argument it took.</summary>
    private bool AiArg(string a, string[] args, ref int i)
    {
        bool more = i + 1 < args.Length;
        if (a == "--ai" && more)
        {
            string[] parts = args[++i].ToLowerInvariant().Split(':');
            _aiSides = parts[0];
            if (parts.Length > 1)
            {
                if (AiBrain.LevelOf(parts[1]) is AiLevel level)
                    _aiLevelSaid = level;
                else
                    GD.PrintErr($"tank3d: --ai: no level {parts[1]} - recruit, fighter, veteran");
            }
            return true;
        }
        if (a == "--ai-seed" && more)
        {
            _aiSeed = int.TryParse(args[++i], out int seed) ? seed : -1;
            return true;
        }
        return false;
    }

    /// <summary>The AI drives <paramref name="side"/>.</summary>
    private bool AiPlays(Side side) => _aiSides switch
    {
        null => _quick is { } q && side != q.Player,
        "both" => true,
        "green" => side == Side.Green,
        "brown" => side == Side.Brown,
        _ => false,
    };

    /// <summary>The turn is the AI's: the player has the camera only.</summary>
    private bool AiTurn => Match && !Deploying && !MatchOver && AiPlays(_mt.Turn);

    /// <summary>The AI may begin: its turn, and the squads' reveal read and
    /// shut by the player («К бою», Tank3DBench.Quick).</summary>
    private bool AiMayGo => AiTurn && _qReveal is null;

    /// <summary>The AI's dice, seeded once a match (<c>--ai-seed</c>, else the
    /// clock - logged, so the match plays again).</summary>
    private Random AiDice()
    {
        if (_aiRng is null)
        {
            int seed = _aiSeed >= 0 ? _aiSeed : (int)(Time.GetTicksUsec() % int.MaxValue);
            _aiRng = new Random(seed);
            Log.Print($"tank3d: ai: seed {seed} (--ai-seed {seed} plays it again), level {_aiLevel}");
        }
        return _aiRng;
    }

    /// <summary>
    /// The AI places <paramref name="side"/> in its zone, blind to the
    /// other's (<see cref="AiDeploy"/>; docs/ai-plan.md, И4): a quick battle's
    /// AI before the player places, a side the AI plays when its placement
    /// comes. The tanks are set as a «place» sets them.
    /// </summary>
    private void AiDeploySide(Side side)
    {
        if (_field is null)
            return;
        BattleState s = ShadowOfBench();
        List<AiDeploy.Spot> spots = AiDeploy.Place(s, CampOf(side), DeployZone(side), DeployZone(Across(side)), AiDice());
        foreach (AiDeploy.Spot p in spots)
            DeploySet(p.Unit, p.Cell, p.Heading);
        Log.Print($"tank3d: ai: {SideName(side)} placed - "
                  + string.Join(", ", spots.ConvertAll(p => $"#{p.Unit} {MapAt(p.Cell)} facing {p.Heading}")));
    }

    /// <summary>Every frame of a match, after the turn's own pacing: think,
    /// then give the play's orders one by one.</summary>
    private void AiTick(bool busy, float dt)
    {
        if (!AiTurn)
        {
            if (_aiQueue.Count > 0)
                _aiQueue.Clear();
            return;
        }
        if (!AiMayGo)
            return;
        if (_aiThink is { } think)
        {
            if (!think.IsCompleted)
            {
                _matchNote = L.T("ai.thinking");
                return;
            }
            _aiThink = null;
            _matchNote = "";
            if (think.IsFaulted)
            {
                GD.PrintErr($"tank3d: ai: threw - {think.Exception?.GetBaseException()}");
                _aiTurnNo = _mt.TurnNo;
                return;
            }
            AiChoice c = think.Result;
            string said = c.Play.Say(_moat);
            Log.Print(FormattableString.Invariant(
                $"tank3d: ai: {SideName(_mt.Turn)} {_aiLevel}, round {_mt.Round}: {said} - score {c.Play.Score:F2}, {c.Plays} plays, {c.Replies} replies, {c.Ms:F0} ms"));
            foreach (string one in said.Split(';', StringSplitOptions.RemoveEmptyEntries))
                _aiQueue.Enqueue(one);
            _aiTurnNo = _mt.TurnNo;
            return;
        }
        if (_aiHold > 0.0f)
        {
            _aiHold -= dt;
            return;
        }
        bool quiet = !busy && _passIn is null && _turnGoal is null && !RouteMoving && _pointDue is null && _commands.Count == 0;
        _aiQuiet = quiet ? _aiQuiet + 1 : 0;
        if (_aiQuiet < AiQuietFrames)
            return;
        _aiQuiet = 0;
        // a new turn of the AI's: choose its play off the main thread
        if (_aiTurnNo != _mt.TurnNo)
        {
            _aiQueue.Clear();
            BattleState now = ShadowOfBench();
            AiLevel level = _aiLevel;
            Random rng = AiDice();
            _aiThink = Task.Run(() => AiBrain.Choose(now, level, rng));
            _matchNote = L.T("ai.thinking");
            return;
        }
        // the play given and the turn still the AI's: it did not go by itself
        string next = _aiQueue.Count > 0 ? _aiQueue.Dequeue() : "pass";
        if (ParseCommand(next) is not { } command)
        {
            GD.PrintErr($"tank3d: ai: {next} - not a command");
            _aiQueue.Clear();
            return;
        }
        if (!Execute(command))
        {
            // the bench refused what the core allowed: the rest is void
            GD.PrintErr($"tank3d: ai: {next} refused on the bench - the turn handed over");
            _aiQueue.Clear();
            Execute(new Command(CommandKind.Pass));
            return;
        }
        if (command.Kind == CommandKind.Take)
            _aiHold = AiTakeHold;
    }
}
