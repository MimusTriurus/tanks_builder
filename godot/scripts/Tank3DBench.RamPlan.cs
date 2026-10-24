using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The ram in a match, shown before it is ordered (2026-10-24): two ghosts -
/// the own tank on the target's hex, where the ram leaves it, and the target
/// on the hex it is shoved to, as it will stand - and an arrow of the shove
/// between them. A ram the rules refuse is shown red, the reason in the HUD.
///
/// <b>When.</b> The pointer on an enemy standing next to the own tank: then
/// the ram is the action of the right button rather than the shot. With the
/// ram on (<c>Tab</c>, <see cref="_ramMode"/>) an enemy further off is rammed
/// after the move: the way to a cell beside it and the hull turned on to it
/// are drawn as the firing post's are (<c>Tank3DBench.Post</c>) - a soft lock
/// of the fixed cell, its ribbon and its ghost - and the right button drives
/// there, then rams.
///
/// <b>The rules</b> (GDD units.md «Таран», C-06): an action instead of the
/// shot; only forward, hull square on to the target; the target thrown one hex
/// along the ram, keeping its heading; refused by the mass table, a unit, the
/// edge, higher ground or masonry behind it (<see cref="Ramming.Of"/>). Which
/// side to ram from: one the shove is allowed along first, then a way with no
/// risk, then the fewest steps with the turn.
///
/// Not yet: ramming one's own (the pointer on an own unit takes it), what the
/// hex behind does to the shoved - a mine, deep water - in the preview.
/// </summary>
public sealed partial class Tank3DBench
{
    /// <summary>The ram ability on (Tab): a target off the hull is rammed
    /// after the move.</summary>
    private bool _ramMode;

    /// <summary>The fixed cell is the ram's approach, not the player's.</summary>
    private bool _ramLock;

    private sealed record RamPlan(Vector2I Target, Vector2I From, int Side, int Way, int Steps,
                                  Ramming.Shove Push, string Why)
    {
        public bool Allowed => Why.Length == 0;
    }

    private RamPlan? _ramPlan;
    /// <summary>The plan found for <see cref="_ramKey"/>, kept while nothing it was found from moves.</summary>
    private RamPlan? _ramFound;
    private string _ramKey = "";
    /// <summary>A ram ordered: done once the hull is on its approach and turned.</summary>
    private RamPlan? _ramWaiting;
    private bool _ramFiredBefore;

    private sealed class RamGhost
    {
        public Node3D Rig = null!;
        public TankModel Model = null!;
        public string Tag = "";
        public ShaderMaterial Fill = null!, Ink = null!;
    }

    private RamGhost? _ramMe, _ramThem;

    private static readonly Color RamRefused = new(0.95f, 0.30f, 0.26f);

    /// <summary>One play of the ram in the preview, s, and where in it we are.</summary>
    private const float RamLoop = 1.8f;
    private float _ramPlay;

    /// <summary>The shove over a loop, 0..1: a beat at the start, the drive
    /// eased in and out, a longer beat where it ends.</summary>
    private static float RamShove(float t) => Mathf.SmoothStep(0.0f, 1.0f, Mathf.Clamp((t - 0.15f) / 0.4f, 0.0f, 1.0f));

    /// <summary>A refused ram over a loop: up to the target, a third of a hex,
    /// and back - the hull stopped against the hull.</summary>
    private static float RamBump(float t) => 0.35f * Mathf.Sin(Mathf.Pi * Mathf.Clamp((t - 0.15f) / 0.5f, 0.0f, 1.0f));

    /// <summary>The ram's preview is up: the right button rams.</summary>
    private bool RamShown => _ramPlan is not null;

    /// <summary>Every frame from <see cref="AimTick"/>, before the shot: true
    /// while the ram is what the pointer offers - the shot is not judged then.</summary>
    private bool RamTick()
    {
        RamPlan? plan = _ramWaiting ?? AskRam();
        _ramPlan = _ramWaiting is null ? plan : null;
        if (plan is null)
        {
            if (_ramLock)
            {
                _ramLock = false;
                ReleasePost();
            }
            HideRamGhosts();
            return false;
        }
        if (_ramWaiting is null)
            RamLock(plan);
        ShowRam(plan);
        _judgedNote = plan.Allowed
            ? $"таран {plan.Target} → {plan.Push.Legs[^1]}"
              + (_ramWaiting is not null ? "" : plan.From != CellHere ? $" с {plan.From}: {plan.Steps} из {_reach?.Budget}" : plan.Steps > 0 ? $": разворот {plan.Steps}" : "")
              + (_ramWaiting is null ? "; ПКМ — таранить" : "")
            : $"таран нельзя: {plan.Why}";
        return true;
    }

    /// <summary>The ram the pointer offers now, or null.</summary>
    private RamPlan? AskRam()
    {
        if (!Match || !_rules || !_chosen || _fate != Fate.Alive || RamRunning || MatchShotSpent || MatchBusy
            || !_overTarget || _other is not { Unit: true } o || o.Fate == Fate.Destroyed || _field is null)
            return null;
        Vector2I target = OtherCell(o);
        bool beside = SideTo(CellHere, target) is not null;
        if (!beside && !_ramMode)
            return null;
        string key = $"{_reachKey}|{target}|{o.Heading:F0}|{o.Fate}|{CellHere}|{HullSide()}|{_drove}|{_modelTag}|{_ramMode}";
        if (key != _ramKey)
        {
            _ramKey = key;
            _ramFound = FindRam(o, target);
            GD.Print(_ramFound is { } found
                ? $"tank3d: ram {o.Tag} {target}: from {found.From} along {found.Side}, {found.Steps} steps"
                  + (found.Allowed ? $", shoved to {found.Push.Legs[^1]}" : $" - refused: {found.Why}")
                : $"tank3d: ram {o.Tag} {target}: no way to it");
        }
        return _ramFound;
    }

    /// <summary>The side of <paramref name="from"/> that <paramref name="to"/>
    /// is on, or null when they are not neighbours.</summary>
    private static int? SideTo(Vector2I from, Vector2I to)
    {
        foreach (int side in HexField.EdgeHeadings)
            if (HexField.Step(from, side) == to)
                return side;
        return null;
    }

    /// <summary>The best way to ram <paramref name="o"/>: from where the tank
    /// stands, or - the ram on - from a cell of the move beside it.</summary>
    private RamPlan? FindRam(Other o, Vector2I target)
    {
        RamPlan? best = null;
        (int, int, int, int) bestRank = (int.MaxValue, 0, 0, 0);
        void Offer(Vector2I from, int side, int way, int steps, bool risky)
        {
            Ramming.Shove push = Ramming.Of(_field!, c => ParkedCells.Contains(c), _profile, o.Profile, target, side);
            var rank = (push.Allowed ? 0 : 1, risky ? 1 : 0, steps, way);
            if (best is not null && rank.CompareTo(bestRank) >= 0)
                return;
            bestRank = rank;
            best = new RamPlan(target, from, side, way, steps, push, push.Allowed ? "" : RamWhy(push.Why, o));
        }
        // from here: square on already, or a turn on the spot if the move is still to be had
        if (SideTo(CellHere, target) is int here)
        {
            int turns = Steps.Turning(HullSide(), here);
            if (turns == 0 || (MatchMayMove && _reach is not null && turns <= _reach.Budget))
                Offer(CellHere, here, -1, turns, false);
        }
        // after the move: a cell beside the target the way reaches, and the
        // hull turned on to it out of the same steps
        if (_ramMode && _reach is { } reach && CourseShown && (_lockCell is null || _postLock))
            foreach (int side in HexField.EdgeHeadings)
            {
                Vector2I from = HexField.Step(target, (side + 180) % 360);
                if (!reach.Reaches(from))
                    continue;
                List<Steps.Route> ways = reach.RoutesTo(from);
                for (int w = 0; w < ways.Count; w++)
                    if (reach.TurnTo(ways[w], side) is int t)
                        Offer(from, side, w, ways[w].Spent + t, ways[w].Risk != Steps.Risk.None);
            }
        if (best is null && SideTo(CellHere, target) is int faced)
            return new RamPlan(target, CellHere, faced, -1, 0, new Ramming.Shove(Array.Empty<Vector2I>(), Ramming.Bar.None),
                               "развернуться к цели лбом нечем — ход сделан");
        return best;
    }

    /// <summary>A refusal in the player's words.</summary>
    private static string RamWhy(Ramming.Bar why, Other o) => why switch
    {
        Ramming.Bar.Mass => $"{o.Tag} тяжелее",
        Ramming.Bar.Edge => "за целью край карты",
        Ramming.Bar.Taken => "за целью стоит юнит",
        Ramming.Bar.Uphill => "за целью возвышенность",
        Ramming.Bar.Cliff => "за целью обрыв глубже уровня",
        Ramming.Bar.Rock => "за целью скала",
        Ramming.Bar.Masonry => "стена на гексе цели или за ней",
        Ramming.Bar.Crosswise => "поперёк рампы",
        Ramming.Bar.Landing => "рампе за целью некуда спустить",
        _ => "нельзя",
    };

    /// <summary>The approach fixed as the firing post's is - the ribbon and
    /// the ghost on it come with the lock.</summary>
    private void RamLock(RamPlan plan)
    {
        if (plan.From == CellHere)
        {
            if (_ramLock)
            {
                _ramLock = false;
                ReleasePost();
            }
            return;
        }
        if (_ramLock && _lockCell == plan.From && _lockFace == plan.Side)
            return;
        _lockCell = plan.From;
        _hoverCell = plan.From;
        _ways = _reach!.RoutesTo(plan.From);
        _wayIx = plan.Way;
        _lockFace = plan.Side;
        _postLock = true;
        _ramLock = true;
    }

    // --- the order ---------------------------------------------------------------

    /// <summary>The right button on the target while the ram is shown: true
    /// when it was the ram's to answer.</summary>
    private bool RamOrder(Vector2I cell)
    {
        if (_ramPlan is not { } plan || plan.Target != cell)
            return false;
        if (!plan.Allowed)
        {
            _orderNote = $"таран нельзя: {plan.Why}";
            return true;
        }
        if (plan.From != CellHere)
            Confirm();
        else if (plan.Steps > 0)
        {
            MatchMoved();
            _turnGoal = HeadingFor(CellHere, plan.Side);
        }
        _ramFiredBefore = _fired;
        _ramWaiting = plan;
        _ramPlan = null;
        _ramLock = false;
        _ramMode = false;
        // the action of the turn, instead of the shot; the rammer takes the
        // target's hex, so the move is spent with it
        _fired = _drove = true;
        _orderNote = $"таран {cell}";
        GD.Print($"tank3d: match: {SideName(_side)} {_modelTag} rams {cell} from {plan.From} along {plan.Side}");
        return true;
    }

    /// <summary>The ram waiting for the hull: driven into the target once the
    /// approach and the turn are done.</summary>
    private void RamWaitTick()
    {
        if (_ramWaiting is not { } plan || RouteMoving || _turnGoal is not null || RamRunning)
            return;
        _ramWaiting = null;
        HideRamGhosts();
        if (_field is null || _other is not { } o || OtherCell(o) != plan.Target || o.Fate == Fate.Destroyed
            || CellHere != plan.From)
        {
            _orderNote = "таран сорвался";
            GD.Print($"tank3d: match: the ram on {plan.Target} fell through");
            return;
        }
        Drive(new List<Vector2I> { CellHere, plan.Target }, ram: true, $"таран {plan.Target}");
    }

    private void RamCancel()
    {
        // called off before the hull went in: the action was not taken
        if (_ramWaiting is not null)
            _fired = _ramFiredBefore;
        _ramWaiting = null;
        _ramPlan = null;
        HideRamGhosts();
    }

    /// <summary>Tab in a match: the ram on or off.</summary>
    private void RamToggle()
    {
        if (!_ramMode && MatchShotSpent)
        {
            _orderNote = "действие этого хода уже сделано";
            return;
        }
        _ramMode = !_ramMode;
        _ramKey = "";
        _orderNote = _ramMode ? "таран: наведи на вражеский танк" : "таран выключен";
    }

    // --- the picture -------------------------------------------------------------

    private RamGhost? RamGhostOf(ref RamGhost? slot, string tag, string name)
    {
        if (slot is not null && slot.Tag == tag)
            return slot;
        slot?.Rig.QueueFree();
        slot = null;
        TankModel model;
        try
        {
            model = TankModel.Copy(tag, Bare) ?? TankModel.Load(tag, toon: true, bare: Bare);
        }
        catch (Exception e)
        {
            GD.Print($"tank3d: ram ghost of {tag}: {e.Message}");
            return null;
        }
        var ink = new ShaderMaterial { Shader = GhostInkShader, RenderPriority = Stage3D.RingOrder + 2 };
        var fill = new ShaderMaterial { Shader = GhostFillShader, RenderPriority = Stage3D.RingOrder + 1, NextPass = ink };
        slot = new RamGhost { Rig = DressGhost(model, tag, fill, name), Model = model, Tag = tag, Fill = fill, Ink = ink };
        return slot;
    }

    private void Pose(RamGhost g, Vector3 at, float heading, float yaw, Color tint, bool firm)
    {
        float clear = GhostClear(at);
        g.Rig.Position = at;
        g.Rig.Rotation = new Vector3(0.0f, Mathf.DegToRad(heading), 0.0f);
        g.Model.Yaw = yaw;
        g.Model.Elevation = 0.0f;
        g.Model.Apply();
        g.Fill.SetShaderParameter("tint", tint);
        g.Fill.SetShaderParameter("alpha", (firm ? GhostFirm : GhostFaint) * clear);
        g.Ink.SetShaderParameter("ink", tint.Darkened(0.7f));
        g.Ink.SetShaderParameter("alpha", clear);
        g.Rig.Visible = clear > 0.01f;
    }

    private void ShowRam(RamPlan plan)
    {
        if (_other is not { } o || _field is null)
        {
            HideRamGhosts();
            return;
        }
        // The ram played over and over: the own ghost drives from its cell on
        // to the target's and the target's is shoved on to the next, both at
        // once, as the hulls go coupled. Refused, the own ghost runs up to the
        // target and stops short against it; the target's stands red on the hex
        // it cannot have.
        _ramPlay = (_ramPlay + (float)GetProcessDeltaTime()) % RamLoop;
        float k = plan.Allowed ? RamShove(_ramPlay / RamLoop) : RamBump(_ramPlay / RamLoop);
        Color mine = plan.Allowed ? SelectionRing.Friendly : RamRefused;
        if (RamGhostOf(ref _ramMe, _modelTag, "RamMe") is { } me)
            Pose(me, CellMiddle(plan.From).Lerp(CellMiddle(plan.Target), k), HeadingFor(plan.Target, plan.Side), 0.0f, mine, true);
        Vector2I onto = plan.Allowed ? plan.Push.Legs[^1] : HexField.Step(plan.Target, plan.Side);
        Vector3 them0 = plan.Allowed ? CellMiddle(plan.Target) : CellMiddle(onto);
        if (_field.InBounds(onto) && RamGhostOf(ref _ramThem, o.Tag, "RamThem") is { } them)
            Pose(them, them0.Lerp(CellMiddle(onto), plan.Allowed ? k : 0.0f), o.Heading, o.Model.Yaw,
                 plan.Allowed ? SideInk(o.Side) : RamRefused, plan.Allowed);
        else if (_ramThem is not null)
            _ramThem.Rig.Visible = false;
    }

    private void HideRamGhosts()
    {
        if (_ramMe is not null)
            _ramMe.Rig.Visible = false;
        if (_ramThem is not null)
            _ramThem.Rig.Visible = false;
    }

    /// <summary>A refused ram: a cross on the hex the target cannot be thrown
    /// to, on the aim's ink. An allowed one needs nothing over the ghosts -
    /// their play is the arrow.</summary>
    private void DrawRam(Control ink)
    {
        RamPlan? plan = _ramWaiting ?? _ramPlan;
        if (plan is null || plan.Allowed || _field is null)
            return;
        Vector2 at = Screen(HexField.Step(plan.Target, plan.Side));
        float hex = HexScreen();
        float x = hex * 0.18f, w = Mathf.Clamp(hex * 0.05f, 2.5f, 6.0f);
        ink.DrawLine(at - new Vector2(x, x), at + new Vector2(x, x), new Color(0, 0, 0, 0.55f), w + 3.0f, true);
        ink.DrawLine(at - new Vector2(x, -x), at + new Vector2(x, -x), new Color(0, 0, 0, 0.55f), w + 3.0f, true);
        ink.DrawLine(at - new Vector2(x, x), at + new Vector2(x, x), RamRefused, w, true);
        ink.DrawLine(at - new Vector2(x, -x), at + new Vector2(x, -x), RamRefused, w, true);
    }
}
