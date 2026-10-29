using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// Where the move leaves the tank, before it is ordered (docs/hud3d-plan.md,
/// stage 3): the tank's own model standing on the cell the way ends on, as a
/// ghost turned the way it will point, and a rose of the six sides round it
/// with what each turn costs.
///
/// <list type="bullet">
/// <item><b>The ghost</b> - a copy of the selected tank's model (the parked
/// tanks' <see cref="TankModel.Load"/>), every surface a translucent fill in the
/// own side's colour under a dashed ink. The fill writes depth, so the ink's
/// far side does not show through it; both draw after the board, over the
/// grass. Faint while the pointer is on a cell, firmer once the cell is
/// fixed.</item>
/// <item><b>Two clicks.</b> The first on a cell in reach fixes it and opens
/// the rose; the second on it, or Enter, gives the order - which cannot be
/// taken back (GDD match.md). A click on another cell in reach fixes that one
/// instead; ESC lets the cell go, then as before.</item>
/// <item><b>The rose</b> - five discs toward the sides but the one the way
/// arrives facing (that turn is none): +1 a side and so on, none where the
/// budget runs out or the cell ends the move (<see cref="Steps.Reach.TurnTo"/>).
/// The pointer on a disc turns the ghost to it, a click on it is the order
/// with that turn at the end (2026-10-29); A/D or the wheel over the fixed
/// cell turn the ghost, and the second click or Enter orders it. The hull
/// turns there on arrival, out of the same steps.</item>
/// </list>
///
/// <c>--do lock=Q,R</c>, <c>face=S</c> (a side, 30..330), <c>rose-hover=S</c>, <c>confirm</c>,
/// <c>unlock</c> do from the command line what the clicks do.
/// </summary>
public sealed partial class Tank3DBench
{
    private const float GhostFaint = 0.22f, GhostFirm = 0.42f;

    private Node3D? _ghostRig;
    private TankModel? _ghost;
    private string _ghostTag = "";
    private ShaderMaterial? _ghostFill, _ghostInk;

    /// <summary>The cell fixed by the first click, and the side the hull is to
    /// face there.</summary>
    private Vector2I? _lockCell;
    private int? _lockFace;
    /// <summary>The order's turn on arrival: a side, set by the rose.</summary>
    private int? _routeFace;
    /// <summary>Where the move ordered ends and the side the hull will face
    /// there: the ghost stands on it for the whole drive (stage 4, in place of
    /// the order's band).</summary>
    private (Vector2I Cell, int Side)? _routeGoal;

    private static readonly Shader GhostFillShader = new()
    {
        Code = @"
shader_type spatial;
render_mode unshaded, cull_back, blend_mix, depth_draw_always, shadows_disabled, fog_disabled;
uniform vec4 tint : source_color = vec4(0.3, 0.85, 1.0, 1.0);
uniform float alpha = 0.3;
void fragment() {
    // a little form, off the view normal, so the ghost is a body and not a stain
    float face = 0.45 + 0.55 * clamp(NORMAL.z, 0.0, 1.0);
    ALBEDO = tint.rgb * face;
    ALPHA = alpha;
}
",
    };

    private static readonly Shader GhostInkShader = new()
    {
        Code = @"
shader_type spatial;
render_mode unshaded, cull_front, blend_mix, skip_vertex_transform, shadows_disabled, fog_disabled;
uniform vec4 ink : source_color = vec4(0.05, 0.25, 0.4, 1.0);
uniform float width = 1.2;
uniform float min_px = 2.2;
uniform float dash = 6.0;
uniform float alpha = 0.9;
void vertex() {
    VERTEX = (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
    vec3 n = normalize(mat3(MODELVIEW_MATRIX) * CUSTOM0.xyz);
    float px = 2.0 / abs(PROJECTION_MATRIX[1][1] * VIEWPORT_SIZE.y);
    float w = max(width, min_px * px) * CUSTOM0.w;
    VERTEX.xy += n.xy * w;
    VERTEX.z -= w;
}
void fragment() {
    if (mod(FRAGCOORD.x + FRAGCOORD.y, dash * 2.0) < dash) {
        discard;
    }
    ALBEDO = ink.rgb;
    ALPHA = alpha;
}
",
    };

    /// <summary>The ghost of the model now driven: built on first need and
    /// again when the model changes (the dropdown, a tank taken over).</summary>
    private void GhostFor(string tag)
    {
        if (_ghostTag == tag && _ghost is not null)
            return;
        _ghostRig?.QueueFree();
        _ghostRig = null;
        _ghost = null;
        _ghostTag = tag;
        TankModel model;
        try
        {
            // cel dressed: the ink's normal rides on the surfaces (Toon.Dress);
            // a copy of the scene a load kept, when there is one - the glTF
            // read again was the hand-over's other third (docs/perf.md, BOT-2)
            model = TankModel.Copy(tag, Bare) ?? TankModel.Load(tag, toon: true, bare: Bare);
        }
        catch (Exception e)
        {
            Log.Print($"tank3d: ghost of {tag}: {e.Message} - the arrowhead stands in");
            return;
        }
        _ghostFill ??= new ShaderMaterial { Shader = GhostFillShader, RenderPriority = Stage3D.RingOrder + 1 };
        _ghostInk ??= new ShaderMaterial { Shader = GhostInkShader, RenderPriority = Stage3D.RingOrder + 2 };
        _ghostFill.Param("tint", SelectionRing.Friendly);
        _ghostFill.NextPass = _ghostInk;
        _ghostRig = DressGhost(model, tag, _ghostFill, "Ghost");
        _ghost = model;
    }

    /// <summary>A loaded model as a ghost: sized for its class, every surface
    /// in <paramref name="fill"/> (its ink the next pass), no shadow, on a rig
    /// of its own that starts hidden.</summary>
    private Node3D DressGhost(TankModel model, string tag, ShaderMaterial fill, string name)
    {
        model.ScaleTo(PixelsFor(model, ClassFor(tag)));
        var meshes = new List<MeshInstance3D>();
        Meshes(model, meshes);
        foreach (MeshInstance3D m in meshes)
        {
            m.MaterialOverride = fill;
            m.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        }
        // the belts' links are a MultiMesh: the same dress
        foreach (Node n in model.FindChildren("*", "MultiMeshInstance3D", true, false))
            if (n is MultiMeshInstance3D mm)
            {
                mm.MaterialOverride = fill;
                mm.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            }
        var rig = new Node3D { Name = name, Visible = false };
        AddChild(rig);
        rig.AddChild(model);
        model.Apply();
        return rig;
    }

    private static void Meshes(Node node, List<MeshInstance3D> into)
    {
        if (node is MeshInstance3D m)
            into.Add(m);
        foreach (Node child in node.GetChildren())
            Meshes(child, into);
    }

    /// <summary>The bench's heading for a flat side, from a cell.</summary>
    private float HeadingFor(Vector2I cell, int side) => HeadingOf(HexWay(cell, side));

    /// <summary>The side the ghost faces: the rose's disc under the pointer,
    /// the fixed one, else where the way arrives.</summary>
    private int GhostFace(Steps.Route way) =>
        _lockCell != way.End.Cell ? way.End.Heading
        : _roseHover is int h ? h
        : _lockFace is int f ? f : way.End.Heading;

    /// <summary>The disc of the fixed cell's rose the pointer is on, the
    /// budget having it - the ghost turns to it before the click
    /// (2026-10-29, the author's note); set each frame in <c>CourseTick</c>.</summary>
    private int? _roseHover;
    /// <summary><c>--do rose-hover=S</c>: a disc held under the pointer.</summary>
    private int? _roseHeld;

    private void RoseHoverTick()
    {
        _roseHover = null;
        if (_lockCell is null || _ways.Count == 0 || _postLock || _reach is null)
            return;
        int? side = _roseHeld ?? RoseAt(GetViewport().GetMousePosition());
        if (side is int s && _reach.TurnTo(_ways[_wayIx % _ways.Count], s) is not null)
            _roseHover = s;
    }

    /// <summary>Every frame from <see cref="CourseTick"/>: the ghost on the
    /// way's end, or put away.</summary>
    private void GhostTick(Steps.Route? way)
    {
        _ghostSea += (float)GetProcessDeltaTime();
        _ghostSign = null;
        _ghostBurst = null;
        // the way's risk is the ghost's on its end - a mine or fire on the way
        _ghostRiskCell = way?.End.Cell;
        _ghostRisk = way?.Risk ?? Steps.Risk.None;
        // the turn on the spot: the ghost on the own cell, turned to the disc
        // under the pointer, over the own hull by design (Tank3DBench.Spin)
        if (SpinShown() is int spin && spin != HullSide())
        {
            _ghostRiskCell = null;
            GhostAt(CellHere, spin, true, overOwn: true);
            return;
        }
        // the plan sees no mines (Steps.Mover.MinesUnseen): the ghost goes up
        // only with the pointer on the mine itself, not on a cell past it
        if (way is not null && _field is { } f && f.CoverAt(way.End.Cell) == Cover.Minefield
            && f.CoverStateAt(way.End.Cell) != CoverState.Cleared)
            _ghostRisk |= Steps.Risk.Mine;
        if (way is not null)
        {
            GhostAt(way.End.Cell, GhostFace(way), _lockCell == way.End.Cell);
            return;
        }
        // An order in hand: the ghost waits where it ends until the hull is
        // there and turned.
        bool driving = _rules && _fate == Fate.Alive && (RouteMoving || _turnGoal is not null);
        if (!driving)
            _routeGoal = null;
        // ...and fades as the hull comes onto it, not to sit inside it (GhostClear)
        if (_routeGoal is var (goal, side) && !_routeRams)
        {
            GhostAt(goal, side, true);
            return;
        }
        if (_ghostRig is not null)
            _ghostRig.Visible = false;
    }

    private void GhostAt(Vector2I cell, int side, bool firm, bool overOwn = false)
    {
        GhostFor(_modelTag);
        if (_ghostRig is null || _ghostFill is null || _ghostInk is null)
            return;
        Vector3 at = CellMiddle(cell);
        float roll = 0.0f, pitch = 0.0f, fade = 1.0f, knocked = 0.0f;
        Color tint = SelectionRing.Friendly;
        if (_ghostSeaCell != cell)
        {
            _ghostSeaCell = cell;
            _ghostSea = 0.0f;
        }
        // What the way does to the hull there, one picture and one sign: deep
        // water first - it ends the way, and puts a fire out - then a mine, then fire.
        Steps.Risk risk = _ghostRiskCell == cell ? _ghostRisk : Steps.Risk.None;
        if (GhostAfloat(cell, ref at, ref roll, ref tint, ref fade) is var (_, line))
            _ghostSign = (_amphibious ? Badge.Afloat : Badge.Drowned, line);
        else if (risk.HasFlag(Steps.Risk.Mine))
        {
            knocked = GhostMined(at, ref at, ref pitch, ref tint, ref fade);
            _ghostSign = (Badge.Knocked, CellMiddle(cell));
        }
        else if (risk.HasFlag(Steps.Risk.Fire))
            _ghostSign = (Badge.Burning, at);
        _ghostRig.Position = at;
        _ghostRig.Rotation = new Vector3(0.0f, Mathf.DegToRad(HeadingFor(cell, side)), 0.0f);
        _ghostRig.RotateObjectLocal(Vector3.Forward, roll);
        _ghostRig.RotateObjectLocal(Vector3.Right, pitch);
        // the turret ahead, until a target under the pointer turns it (Tank3DBench.Aim)
        _ghost!.Yaw = 0.0f;
        _ghost.Elevation = 0.0f;
        // the knock-out pose, as Fall gives the real hull (Tank3DBench.Match)
        _ghost.Cant = knocked;
        _ghost.Droop = knocked;
        _ghost.Slackness = knocked;
        _ghost.Apply();
        float clear = GhostClear(_ghostRig.Position, overOwn);
        _ghostClearNow = clear;
        _ghostFill.Param("tint", tint);
        _ghostFill.Param("alpha", (firm ? GhostFirm : GhostFaint) * clear * fade);
        _ghostInk.Param("alpha", (firm ? 1.0f : 0.7f) * clear * fade);
        _ghostRig.Visible = clear > 0.01f;
    }

    /// <summary>The ghost's clock on its cell: the swell, the sinking loop,
    /// the mine's loop - from nought each new cell.</summary>
    private float _ghostSea;
    private Vector2I? _ghostSeaCell;
    /// <summary>The way's risk, and the cell it ends on (<see cref="GhostTick"/>).</summary>
    private Steps.Risk _ghostRisk;
    private Vector2I? _ghostRiskCell;
    /// <summary>The sign over the ghost and where it stands, world - over the
    /// hull at rest or the water line, not over the hull as it moves
    /// (<c>Tank3DBench.Badges</c>, <c>DrawGhostSign</c>).</summary>
    private (Badge Kind, Vector3 Anchor)? _ghostSign;
    /// <summary>The mine going off under the ghost: where, and how much of the
    /// flash is left, 0..1.</summary>
    private (Vector3 At, float A)? _ghostBurst;
    private float _ghostClearNow;

    /// <summary>The ghost's mine loop, s: drives on, the mine, knocked out, held.</summary>
    private const float GhostMineLoop = 2.6f;
    /// <summary>What a knocked-out ghost dims into: the wreck's soot.</summary>
    private static readonly Color GhostWreck = new(0.42f, 0.38f, 0.34f);

    /// <summary>
    /// The move's ghost on a mine (GDD field.md «мины»: it goes off under the
    /// hull, the hull is knocked out, the way ends there): standing whole, a
    /// flash under it (<see cref="_ghostBurst"/>), the hull thrown up and its
    /// nose with it, and down into the knock-out pose - turret sat over, gun
    /// on its stop, belts slack - dimming to soot, where it stays the rest of
    /// the loop. Faded in and out at the loop's ends, not popped back whole.
    /// Returns how far into the pose it is, 0..1.
    /// </summary>
    private float GhostMined(Vector3 rest, ref Vector3 at, ref float pitch, ref Color tint, ref float fade)
    {
        float t = (_ghostSea % GhostMineLoop) / GhostMineLoop;
        float tall = _ghost!.Size.Y * _ghost.PixelsPerUnit;
        float s = Mathf.Clamp((t - 0.28f) / 0.16f, 0.0f, 1.0f);
        float hop = Mathf.Sin(Mathf.Pi * s);
        at.Y = rest.Y + 0.14f * tall * hop;
        pitch = 0.14f * hop;
        if (t >= 0.28f && t < 0.52f)
            _ghostBurst = (rest + Vector3.Up * (0.1f * tall), 1.0f - (t - 0.28f) / 0.24f);
        float knocked = Mathf.SmoothStep(0.0f, 1.0f, Mathf.Clamp((t - 0.32f) / 0.18f, 0.0f, 1.0f));
        tint = tint.Lerp(GhostWreck, 0.85f * knocked);
        fade = Mathf.Min(Mathf.SmoothStep(0.0f, 1.0f, t / 0.1f), Mathf.SmoothStep(0.0f, 1.0f, (1.0f - t) / 0.08f));
        return knocked;
    }

    /// <summary>The swell's size and pace, read off a hull's (never ridden).</summary>
    private static readonly Buoy GhostSwell = new();

    /// <summary>The ghost's sinking loop, s: on the water, down, held under.</summary>
    private const float GhostSinkLoop = 2.6f;

    /// <summary>
    /// The move's ghost on deep water, as the hull will be there (GDD
    /// states.md «Плывёт», «Утоплен»; the real hull - <c>Tank3DBench.Water</c>):
    /// with the wading gear on its swimming line, riding a swell of its own
    /// - a heave and a little roll; without it, down from the line to most of
    /// its deck under, dimming into the water's blue, as the ram's preview
    /// sinks its target (<see cref="RamPlungeY"/>) - and again, faded in at the
    /// top of each loop rather than popped up. Null on dry land.
    /// </summary>
    private (Vector2I, Vector3)? GhostAfloat(Vector2I cell, ref Vector3 at, ref float roll, ref Color tint, ref float fade)
    {
        if (_ghost is null || SwimY(cell, _ghost, _profile) is not float line)
            return null;
        Vector3 surface = new(at.X, line, at.Z);
        if (_amphibious)
        {
            // the real hull's swell (Buoy.Idle), a little larger: a ghost is
            // faint, and a heave of a pixel or two read as standing still
            float s = Mathf.Tau * _ghostSea / (float)GhostSwell.SwellPeriod;
            at.Y = line + 2.0f * (float)GhostSwell.SwellAmplitude * Mathf.Sin(s) / RiseFactor;
            roll = 0.035f * Mathf.Sin(s * 0.5f + 1.0f);
            return (cell, surface);
        }
        float t = (_ghostSea % GhostSinkLoop) / GhostSinkLoop;
        float under = Mathf.Max(BedPx(cell) / RiseFactor, line - RamUnder * _decks[_ghost] / RiseFactor);
        float down = Mathf.SmoothStep(0.0f, 1.0f, Mathf.Clamp((t - 0.2f) / 0.45f, 0.0f, 1.0f));
        at.Y = Mathf.Lerp(line, under, down);
        tint = tint.Lerp(RamDrowned, down);
        // dimmed as it goes under; in from nothing at the top of the loop
        fade = (1.0f - 0.3f * down) * Mathf.SmoothStep(0.0f, 1.0f, t / 0.12f);
        return (cell, surface);
    }

    /// <summary>How much of a ghost at <paramref name="at"/> to draw, 0..1:
    /// all of it most of a cell off every real hull, none on
    /// one. Drawn whole, a ghost met by its tank as it arrived, or played
    /// into a standing one by the ram's preview, was a hull through a hull.
    /// </summary>
    private float GhostClear(Vector3 at, bool overOwn = false)
    {
        // a casemate's ghost laid over its own hull (Tank3DBench.Aim,
        // CasemateGhost) stands there by design: faded by that hull, it went,
        // and the line of fire, taken off it, went with it
        float near = overOwn ? float.MaxValue : Flat(at - _rig.Position).Length();
        if (_other is { } o)
            near = Mathf.Min(near, Flat(at - o.Rig.Position).Length());
        foreach (Parked p in _parked)
            near = Mathf.Min(near, Flat(at - p.Rig.Position).Length());
        return Mathf.SmoothStep(0.25f, 0.85f, near / CellStep);
    }

    // --- the two clicks -----------------------------------------------------

    /// <summary>The first click: fix the cell, its way the one shown, the
    /// hull to face where it arrives.</summary>
    private void Lock(Vector2I cell)
    {
        if (_reach is null || !_reach.Reaches(cell))
            return;
        bool same = _hoverCell == cell;
        _lockCell = cell;
        _hoverCell = cell;
        if (!same)
        {
            _wayIx = 0;
            _ways = _reach.RoutesTo(cell);
        }
        _lockFace = _ways.Count > 0 ? _ways[_wayIx % _ways.Count].End.Heading : null;
        Log.Print($"tank3d: fixed {MapAt(cell)}: {(_ways.Count > 0 ? MapAt(_ways[_wayIx % _ways.Count]) : "no way")}");
    }

    private void Unlock()
    {
        _lockCell = null;
        _lockFace = null;
        _postLock = false;
    }

    /// <summary>The second click, or Enter: the order on the fixed cell, the
    /// way and the side shown.</summary>
    private void Confirm()
    {
        if (_lockCell is not Vector2I cell)
            return;
        int? face = _lockFace;
        OrderTo(cell, face: face);
        Unlock();
    }

    /// <summary>The rose's turn: to <paramref name="side"/> if the budget has
    /// it.</summary>
    private bool FaceTo(int side)
    {
        if (_lockCell is null || _reach is null || _ways.Count == 0)
            return false;
        Steps.Route way = _ways[_wayIx % _ways.Count];
        if (_reach.TurnTo(way, side) is null)
        {
            _orderNote = way.Final ? "здесь путь кончается — разворота нет" : "на этот разворот шагов нет";
            return false;
        }
        _lockFace = side;
        _orderNote = "";
        return true;
    }

    /// <summary>A/D, the wheel: the ghost a side round, past the sides the
    /// budget does not have.</summary>
    private void FaceStep(bool left)
    {
        if (_lockCell is null || _lockFace is not int from)
            return;
        for (int k = 1; k < 6; k++)
            if (FaceTo(((from + (left ? 60 : -60) * k) % 360 + 360) % 360))
                return;
    }

    /// <summary>The side the way arrives facing: its disc is not drawn - the
    /// ghost faces it already, a turn to it is none (the author's note,
    /// 2026-10-29).</summary>
    private int RoseHome() => _ways[_wayIx % _ways.Count].End.Heading;

    /// <summary>The disc of the rose under <paramref name="screen"/>, if any.</summary>
    private int? RoseAt(Vector2 screen)
    {
        // a firing post has no rose: its side is the one it fires from (Tank3DBench.Post)
        if (_lockCell is not Vector2I cell || _ways.Count == 0 || _postLock || _reach is null)
            return null;
        float r = RoseDisc();
        Vector2 mid = Screen(cell);
        Steps.Route way = _ways[_wayIx % _ways.Count];
        foreach (int side in HexField.EdgeHeadings)
            if (side != way.End.Heading && SpinDisc(cell, mid, side).DistanceTo(screen) <= r * 1.3f
                && _reach.TurnTo(way, side) is not null)
                return side;
        return null;
    }

    private float RoseDisc() => Mathf.Clamp(HexScreen() * 0.16f, 9.0f, 16.0f);

    /// <summary>Half a hex across, in screen px at the tank's cell.</summary>
    private float HexScreen()
    {
        Vector2I at = _reach?.Start.Cell ?? CellHere;
        return (Screen(at) - _camera.UnprojectPosition(
            OnTop(at, CellFlat(at) + new Vector2(_tile!.HexRect.Size.X * 0.5f, 0)))).Length();
    }

    /// <summary>The wheel over the fixed cell turns the ghost; elsewhere it
    /// zooms.</summary>
    private bool WheelTurns(Vector2 screen, bool up)
    {
        if (_lockCell is not Vector2I cell || _postLock || Screen(cell).DistanceTo(screen) > HexScreen() * 1.4f)
            return false;
        FaceStep(up);
        return true;
    }

    /// <summary>How much of the fixed cell's rose is drawn, 0..1, and what it
    /// was drawn for - kept while it fades out after the cell is let go or
    /// ordered (Tank3DBench.Spin, <c>RoseFadeTime</c>).</summary>
    private float _roseFade;
    private Vector2I _roseCell;
    private Steps.Route? _roseWay;
    private int _roseFace;

    /// <summary>Every frame from <c>CourseTick</c>, before it may return:
    /// the rose in while a cell is fixed, out after.</summary>
    private void RoseFadeTick()
    {
        bool on = _lockCell is Vector2I cell && _ways.Count > 0 && !_postLock && _reach is not null && CourseShown;
        if (on)
        {
            _roseCell = _lockCell!.Value;
            _roseWay = _ways[_wayIx % _ways.Count];
            _roseFace = GhostFace(_roseWay);
        }
        _roseFade = Mathf.MoveToward(_roseFade, on ? 1.0f : 0.0f, (float)GetProcessDeltaTime() / RoseFadeTime);
    }

    private void DrawRose(Control ink, Font font, int size)
    {
        if (_roseFade <= 0.0f || _roseWay is not { } way || _reach is null)
            return;
        float r = RoseDisc();
        Vector2 mid = Screen(_roseCell);
        foreach (int side in HexField.EdgeHeadings)
        {
            // a side the steps left after the drive do not reach has no disc
            // either (the author's note, 2026-10-29)
            if (side == way.End.Heading || _reach.TurnTo(way, side) is not int cost)
                continue;
            RoseDraw(ink, font, size, SpinDisc(_roseCell, mid, side), r, _roseFade, side == _roseFace, false,
                     SpinCosts[Mathf.Clamp(cost, 0, 3)]);
        }
    }
}
