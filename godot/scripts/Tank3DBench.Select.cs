using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// Which tank the orders are for. The 2D board's bands round the rim of a cell
/// (<see cref="SelectionRing"/>, <c>Stage3D.Band</c>) - cyan on the own tank,
/// red on the target, violet on the order's cell - are no longer drawn since
/// HUD stage 4: the outline on the model (<c>Tank3DBench.Aim</c>) and the
/// ghost (<c>Tank3DBench.Ghost</c>) say it. The drawing below stays.
///
/// <b>The ground and not the model</b>, for <see cref="SelectionRing"/>'s
/// reason: an outline round the hull is drawn in the channel the bench judges,
/// and a mark on the cell speaks the grid's language - its far side passes
/// behind the hull, which is what makes it read as lying on the ground.
///
/// <b>The same shape, colours and widths as the relief board's</b> -
/// <see cref="Stage3D.Ring"/>, <c>Line</c>, <c>Fringe</c>, nested inward by
/// <c>Nest</c> where two marks share a cell, dark-ink-dark so the mark carries
/// its own contrast - and the same sort (<see cref="Stage3D.RingOrder"/>, over
/// the water, under the tanks). Sized from the cell, never from the tank.
///
/// <b>A tank's mark follows its middle</b>, not the cell it last reached: a
/// tank spends most of an order between two cells, and a mark left behind
/// reads as the selection lagging. It lies in the plane of the cell that
/// middle is over (<see cref="HexField.TopOn"/>).
///
/// <b>Selected by clicking it</b> (<see cref="OrderClick"/>): with nothing
/// selected the left button selects and does nothing else, W/S/A/D and the
/// shot wait; ESC with no order in hand lets go. <c>--no-ui</c> draws no marks.
/// </summary>
public sealed partial class Tank3DBench
{
    /// <summary>How much wider than the relief board's the band is: the grass
    /// tufts stand in it here, and at the relief board's width they broke it
    /// into dashes.</summary>
    private const float MarkWide = 1.5f;

    /// <summary>How far toward the eye the band is drawn, a share of the hex:
    /// the tufts' height. The camera is orthographic, so this moves no pixel
    /// and buys depth alone (<c>Stage3D.Clear</c>'s trick) - enough to stand
    /// in front of the grass, not of the hull standing on the far side.</summary>
    private const float MarkNudge = 0.08f;

    /// <summary>The own tank is selected: its orders are taken.</summary>
    private bool _chosen = true;

    private MeshInstance3D? _marks;

    private void Choose(bool on)
    {
        if (_chosen == on)
            return;
        _chosen = on;
        if (!on)
            CancelOrder();
        _orderNote = "";
        Log.Print($"tank3d: own tank {(on ? "selected" : "let go")}");
    }

    private void BuildMarks()
    {
        if (_noUi)
            return;
        _marks = new MeshInstance3D
        {
            Name = "Marks",
            Mesh = new ImmediateMesh(),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                RenderPriority = Stage3D.RingOrder,
            },
        };
        AddChild(_marks);
    }

    /// <summary>What is marked: nothing any more. The own tank's band, the
    /// order's cell's and the target's went with HUD stage 4 - the outline on
    /// the model says which tank is selected and which is aimed at, the ghost
    /// where the order goes (Tank3DBench.Aim, Tank3DBench.Ghost). The bands
    /// stay drawable for whatever needs a mark on a cell next.</summary>
    private List<(Vector3 At, Color Ink)> Marked() => new();

    /// <summary>Every frame: the bands rebuilt where the tanks are now.</summary>
    private void MarksTick()
    {
        if (_marks?.Mesh is not ImmediateMesh mesh || _field is null || _tile is null)
            return;
        mesh.ClearSurfaces();
        List<(Vector3 At, Color Ink)> marks = Marked();
        if (marks.Count == 0)
            return;
        mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        var cells = new List<Vector2I>();
        foreach ((Vector3 at, Color ink) in marks)
        {
            Vector2I cell = _field.FlatCellAt(Board(at));
            int over = cells.FindAll(c => c == cell).Count;
            cells.Add(cell);
            Band(mesh, at, cell, ink, Stage3D.Ring - Stage3D.Nest * over);
        }
        mesh.SurfaceEnd();
    }

    /// <summary>One band: dark, ink, dark, six quads each, round
    /// <paramref name="at"/> at <paramref name="ring"/> of the cell, every
    /// vertex on the plane of <paramref name="cell"/>.</summary>
    private void Band(ImmediateMesh mesh, Vector3 at, Vector2I cell, Color ink, float ring)
    {
        // The flat-top hexagon in board units, off the drawn tile with no
        // trigonometry (SelectionRing.Outline): (±w/2, 0), (±w/4, ±h/2).
        Vector2 size = _tile!.HexRect.Size;
        float w = size.X * 0.5f, h = size.Y * 0.5f;
        var corner = new[]
        {
            new Vector2(-w, 0.0f), new Vector2(-w * 0.5f, -h), new Vector2(w * 0.5f, -h),
            new Vector2(w, 0.0f), new Vector2(w * 0.5f, h), new Vector2(-w * 0.5f, h),
        };
        Vector2 middle = Board(at);
        Vector3 lie = Stage3D.Clear(Squash, RiseFactor) + _camera.GlobalBasis.Z * MarkNudge * HexWidth;
        float line = Stage3D.Line * MarkWide, fringe = Stage3D.Fringe * MarkWide;
        foreach ((float from, float to, Color paint) in new[]
                 {
                     (ring - fringe, ring - line, SelectionRing.Under),
                     (ring - line, ring + line, ink),
                     (ring + line, ring + fringe, SelectionRing.Under),
                 })
            for (int i = 0; i < 6; i++)
            {
                Vector2 a = corner[i], b = corner[(i + 1) % 6];
                foreach (Vector2 v in new[] { a * to, b * to, b * from, a * to, b * from, a * from })
                {
                    Vector2 on = middle + v;
                    float top = _field!.TopOn(cell, on);
                    mesh.SurfaceSetColor(paint);
                    mesh.SurfaceAddVertex(new Vector3(on.X, top / RiseFactor, on.Y / Squash) + lie);
                }
            }
    }
}
