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
/// <item><b>The rose</b> - six discs toward the six sides: 0 on the side the
/// way arrives facing, +1 a side and so on, grey where the budget runs out or
/// the cell ends the move (<see cref="Steps.Reach.TurnTo"/>). A click on a
/// disc, A/D, or the wheel over the fixed cell turns the ghost; the order
/// turns the hull there on arrival, out of the same steps.</item>
/// </list>
///
/// <c>--do lock=Q,R</c>, <c>face=S</c> (a side, 30..330), <c>confirm</c>,
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
            // cel dressed: the ink's normal rides on the surfaces (Toon.Dress)
            model = TankModel.Load(tag, toon: true);
        }
        catch (Exception e)
        {
            GD.Print($"tank3d: ghost of {tag}: {e.Message} - the arrowhead stands in");
            return;
        }
        _ghostFill ??= new ShaderMaterial { Shader = GhostFillShader, RenderPriority = Stage3D.RingOrder + 1 };
        _ghostInk ??= new ShaderMaterial { Shader = GhostInkShader, RenderPriority = Stage3D.RingOrder + 2 };
        _ghostFill.SetShaderParameter("tint", SelectionRing.Friendly);
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

    /// <summary>The side the ghost faces: the fixed one, else where the way
    /// arrives.</summary>
    private int GhostFace(Steps.Route way) => _lockCell == way.End.Cell && _lockFace is int f ? f : way.End.Heading;

    /// <summary>Every frame from <see cref="CourseTick"/>: the ghost on the
    /// way's end, or put away.</summary>
    private void GhostTick(Steps.Route? way)
    {
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

    private void GhostAt(Vector2I cell, int side, bool firm)
    {
        GhostFor(_modelTag);
        if (_ghostRig is null || _ghostFill is null || _ghostInk is null)
            return;
        _ghostRig.Position = CellMiddle(cell);
        _ghostRig.Rotation = new Vector3(0.0f, Mathf.DegToRad(HeadingFor(cell, side)), 0.0f);
        // the turret ahead, until a target under the pointer turns it (Tank3DBench.Aim)
        _ghost!.Yaw = 0.0f;
        _ghost.Elevation = 0.0f;
        _ghost.Apply();
        float clear = GhostClear(_ghostRig.Position);
        _ghostFill.SetShaderParameter("alpha", (firm ? GhostFirm : GhostFaint) * clear);
        _ghostInk.SetShaderParameter("alpha", (firm ? 1.0f : 0.7f) * clear);
        _ghostRig.Visible = clear > 0.01f;
    }

    /// <summary>How much of a ghost at <paramref name="at"/> to draw, 0..1:
    /// all of it most of a cell off every real hull, none on
    /// one. Drawn whole, a ghost met by its tank as it arrived, or played
    /// into a standing one by the ram's preview, was a hull through a hull.
    /// </summary>
    private float GhostClear(Vector3 at)
    {
        float near = Flat(at - _rig.Position).Length();
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
        GD.Print($"tank3d: fixed {cell}: {(_ways.Count > 0 ? _ways[_wayIx % _ways.Count].ToString() : "no way")}");
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

    /// <summary>The rose's discs on the screen, side by side.</summary>
    private List<(int Side, Vector2 At)> Rose(Vector2I cell)
    {
        var discs = new List<(int, Vector2)>();
        Vector2 mid = Screen(cell);
        foreach (int side in HexField.EdgeHeadings)
        {
            Vector2 way = _camera.UnprojectPosition(OnTop(cell, CellFlat(cell)
                + (CellFlat(HexField.Step(cell, side)) - CellFlat(cell)) * 0.5f)) - mid;
            discs.Add((side, mid + way * 1.25f));
        }
        return discs;
    }

    /// <summary>The disc of the rose under <paramref name="screen"/>, if any.</summary>
    private int? RoseAt(Vector2 screen)
    {
        // a firing post has no rose: its side is the one it fires from (Tank3DBench.Post)
        if (_lockCell is not Vector2I cell || _ways.Count == 0 || _postLock)
            return null;
        float r = RoseDisc();
        foreach ((int side, Vector2 at) in Rose(cell))
            if (at.DistanceTo(screen) <= r * 1.3f)
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

    private void DrawRose(Control ink, Font font, int size)
    {
        if (_lockCell is not Vector2I cell || _reach is null || _ways.Count == 0 || _postLock)
            return;
        Steps.Route way = _ways[_wayIx % _ways.Count];
        float r = RoseDisc();
        foreach ((int side, Vector2 at) in Rose(cell))
        {
            int? cost = _reach.TurnTo(way, side);
            bool on = side == GhostFace(way);
            Color fill = on ? CourseInk : cost is null ? new Color(0.62f, 0.62f, 0.60f, 0.85f) : new Color(0.92f, 0.95f, 0.98f);
            Color letters = on ? Colors.White : cost is null ? new Color(0.3f, 0.3f, 0.3f) : new Color(0.05f, 0.27f, 0.49f);
            ink.DrawCircle(at, r, fill);
            ink.DrawArc(at, r, 0, Mathf.Tau, 24, cost is null ? new Color(0.45f, 0.45f, 0.43f) : CourseInk, 1.5f, true);
            string text = cost is null ? "–" : cost == 0 ? "0" : $"+{cost}";
            Vector2 w = font.GetStringSize(text, HorizontalAlignment.Left, -1, size);
            ink.DrawString(font, at + new Vector2(-w.X * 0.5f, size * 0.36f), text, HorizontalAlignment.Left, -1, size, letters);
        }
    }
}
