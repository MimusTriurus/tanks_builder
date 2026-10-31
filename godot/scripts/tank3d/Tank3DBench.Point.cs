using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The control point in a match (GDD match.md, «Удержание точки»;
/// docs/battle-core-plan.md, Д9): the map marks one hex or a hex with its
/// ring and the points each side starts with. On the field's tick the side
/// with more active units on it holds it; the side that does not loses a
/// point, both when nobody does. A side out of points has lost; the holding
/// changing hands puts the stalemate's clock back, as a shot at a tank does.
/// The rule is the core's (<see cref="Battle.Holding"/>); the bench counts
/// its own units and the shadow reads the two against each other.
/// </summary>
public sealed partial class Tank3DBench
{
    /// <summary>The zones and the point the board's map marks.</summary>
    private Arena _arena = Arena.Empty;

    /// <summary>The point's hexes, or null for a match to the last tank.</summary>
    private Vector2I[]? _pointCells;

    private MeshInstance3D? _pointMark;

    private static readonly Color PointInk = new(1.0f, 0.86f, 0.35f);

    /// <summary>The line-up done: the point's hexes and both sides' points.</summary>
    private void PointSetup()
    {
        _pointCells = null;
        if (!Match || _arena.Point is not { } p)
            return;
        var cells = new List<Vector2I>();
        foreach (Vector2I c in p.Cells())
            if (_field is null || _field.InBounds(c))
                cells.Add(c);
        _pointCells = cells.ToArray();
        _mt.Score[0] = _mt.Score[1] = p.Score;
        Log.Print($"tank3d: match: the control point {p} - {_pointCells.Length} hexes");
    }

    /// <summary>Whether <paramref name="cell"/> is a hex of the point.</summary>
    private bool OnPoint(Vector2I cell) => _pointCells is not null && System.Array.IndexOf(_pointCells, cell) >= 0;

    /// <summary>Who holds the point now: the active units on its hexes.</summary>
    private Side? PointHolder()
    {
        if (_pointCells is null)
            return null;
        int green = 0, brown = 0;
        void Count(Side side, Vector2I cell, Fate fate)
        {
            if (fate != Fate.Alive || !OnPoint(cell))
                return;
            if (side == Side.Green)
                green++;
            else
                brown++;
        }
        if (_unitId >= 0)
            Count(_side, CellHere, _fate);
        foreach (Parked p in _parked)
            Count(p.Side, p.Cell, p.Fate);
        if (_other is { Unit: true } o)
            Count(o.Side, OtherCell(o), o.Fate);
        return Battle.Holding(green, brown) is Camp c ? (c == Camp.Green ? Side.Green : Side.Brown) : null;
    }

    /// <summary>The round whose tick is owed, once the hand-over has settled;
    /// null for none.</summary>
    private int? _pointDue;

    /// <summary>The field's tick (<c>FieldTick</c>): the point's count is owed
    /// for this round. Not taken here: the core burns the ending side's units
    /// out, their blasts with them, before the tick (<c>Battle.EndTurn</c>),
    /// and the bench plays that after the hand-over, a blast at a time
    /// (<c>BurnOut</c>) - a unit burning out on the point would count for its
    /// side here and not in the core.</summary>
    private void PointTick()
    {
        if (_pointCells is not null)
            _pointDue = _mt.Round;
    }

    /// <summary>Every frame of a match: the owed tick taken once the
    /// hand-over's burning out is done - the point held or not, the points
    /// taken, a side out of them has lost.</summary>
    private void PointDueTick(bool busy)
    {
        if (_pointDue is not int round || busy || MatchOver)
            return;
        _pointDue = null;
        Side? held = PointHolder();
        if (held != _mt.Holder)
        {
            Log.Print($"tank3d: match: the point {(held is Side h ? $"to {SideName(h)}" : "held by nobody")} in round {round}");
            _mt.Holder = held;
            _mt.LastShift = round;
        }
        if (held != Side.Green)
            _mt.Score[0]--;
        if (held != Side.Brown)
            _mt.Score[1]--;
        Log.Print($"tank3d: match: points after round {round} - green {_mt.Score[0]}, brown {_mt.Score[1]}");
        MatchCheckEnd();
    }

    /// <summary>A side out of points has lost; both on one tick, a draw (the
    /// core's <c>Battle.Over</c>).</summary>
    private bool PointOver()
    {
        if (_pointCells is null)
            return false;
        bool green = _mt.Score[0] <= 0, brown = _mt.Score[1] <= 0;
        if (!green && !brown)
            return false;
        _passIn = null;
        _commands.Clear();
        if (green && brown)
        {
            _mt.Drawn = true;
            Banner($"Ничья: очки кончились у обеих сторон · раунд {_mt.Round}", Colors.White, stay: true);
            Log.Print($"tank3d: match: both sides out of points - drawn in round {_mt.Round}");
            return true;
        }
        _mt.Won = green ? Side.Brown : Side.Green;
        Banner($"Победа по очкам: {SideName(_mt.Won.Value)} · раунд {_mt.Round}", SideInk(_mt.Won.Value), stay: true);
        Log.Print($"tank3d: match: {SideName(Across(_mt.Won.Value))} out of points - {SideName(_mt.Won.Value)} win in round {_mt.Round}");
        return true;
    }

    /// <summary>The HUD's word on the point: who holds it now and the points.</summary>
    private string PointHud()
    {
        if (_pointCells is null)
            return "";
        Side? now = PointHolder();
        return $" · точка: {(now is Side h ? $"держат {SideName(h)}" : "никто не держит")}"
               + $" · очки: зелёные {_mt.Score[0]}, коричневые {_mt.Score[1]}";
    }

    /// <summary>Every frame of a match: the point's hexes outlined in gold,
    /// filled in the colour of who holds it now.</summary>
    private void PointDraw()
    {
        if (_pointCells is null || _field is null || _tile is null || _noUi)
            return;
        _pointMark ??= CellMarker("Point");
        Side? held = PointHolder();
        CellMarks(_pointMark, _pointCells, new Color(held is Side s ? SideInk(s) : PointInk, 0.22f),
                  new Color(PointInk, 0.95f));
    }

    /// <summary>A mesh of marks on the board's hexes, under the marks and over
    /// the water - the point's, the deployment zone's.</summary>
    private MeshInstance3D CellMarker(string name)
    {
        var marker = new MeshInstance3D
        {
            Name = name,
            Mesh = new ImmediateMesh(),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                RenderPriority = Stage3D.RingOrder - 1,
            },
        };
        AddChild(marker);
        return marker;
    }

    /// <summary>Each of <paramref name="cells"/> filled and rimmed, drawn
    /// afresh: the marks are nudged toward the eye, which turns.</summary>
    private void CellMarks(MeshInstance3D marker, IReadOnlyList<Vector2I> cells, Color fill, Color rim)
    {
        var mesh = (ImmediateMesh)marker.Mesh;
        mesh.ClearSurfaces();
        if (cells.Count == 0)
            return;
        mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        Vector2[] corner = Corners();
        for (int k = 0; k < cells.Count; k++)
        {
            Vector2I cell = cells[k];
            Vector2 mid = CellFlat(cell);
            for (int i = 0; i < 6; i++)
            {
                Vector2 a = mid + corner[i], b = mid + corner[(i + 1) % 6];
                Vector2 ai = mid + corner[i] * 0.84f, bi = mid + corner[(i + 1) % 6] * 0.84f;
                Tri(mesh, fill, cell, mid, ai, bi);
                Quad(mesh, rim, cell, a, b, bi, ai);
            }
        }
        mesh.SurfaceEnd();
    }
}
