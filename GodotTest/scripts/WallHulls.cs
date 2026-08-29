using System;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The solver's own boxes, drawn as wire frames over the picture: one colour for
/// the masonry still standing, one for what has been let go of, one for the ram
/// box the tank drives.
///
/// <b>It exists because the ram box is invisible and is the thing under
/// discussion.</b> Every other actor on this board draws itself - the bricks are
/// a multimesh, the tank is its own sprite - but the box that decides what the
/// wall loses has no picture at all, so "the tank does not fit in the ring" and
/// "the tank clips the masonry" were arguments about a shape nobody had ever
/// seen. A frame costs twelve lines and settles them by looking.
///
/// <b>Frames, never solids.</b> The point is to read the box <i>against</i> what
/// it overlaps, so it must not hide it; and it is drawn with the depth test off
/// for the same reason - a box buried in the ground or standing inside a brick
/// is exactly the case worth seeing, and a depth-tested overlay shows nothing
/// there.
///
/// <b>Drawn where the bricks are drawn, and in their units.</b> A child of
/// <see cref="WallStack"/>, so the prop's spin, radius and anchor come for free
/// and this class never repeats them - the same reason
/// <see cref="WallRig.Ram"/> is solved in the rig and drawn in the stack.
///
/// <b>What it does not draw is the ground.</b> The floor is the board's, not the
/// wall's, and it is a mesh rather than a box; a wire cage over the whole cell
/// would bury the two shapes this is for.
/// </summary>
public sealed partial class WallHulls : MeshInstance3D
{
    /// <summary>Off, and it is a debug overlay's default: nearly every number on
    /// these benches is a pixel difference between two captures, so a frame
    /// drawn over the picture would be measuring itself. Named rather than left
    /// in the field's initialiser for <c>Recoil.ShearOnByDefault</c>'s
    /// reason.</summary>
    public const bool ShownByDefault = false;

    /// <summary>Whether the colliders are drawn at all - <c>--hulls</c> on
    /// either bench, and a toggle on either panel. Static for
    /// <see cref="WallRig.Crumbles"/>'s reason: it is a property of the view and
    /// not of any one wall, and a board carrying five props would otherwise
    /// answer it five times.</summary>
    public static bool Shown = ShownByDefault;

    /// <summary>Masonry the solver is still holding still - a frozen body, which
    /// is a static body and is what a kinematic box drives straight
    /// through.</summary>
    public static readonly Color Standing = new(0.25f, 1.00f, 0.35f);

    /// <summary>Masonry that has been let go of. Told apart from the above
    /// because the two behave in opposite ways against the ram box, and because
    /// a leaf that reports itself gone and is still in the picture is the whole
    /// question - see <see cref="WallRig.Stuck"/>.</summary>
    public static readonly Color Loose = new(1.00f, 0.55f, 0.10f);

    /// <summary>The box the tank drives.</summary>
    public static readonly Color Ram = new(0.25f, 0.90f, 1.00f);

    /// <summary>The same box while its shape is disabled - out of the simulation
    /// until the nose has advanced, see <see cref="WallRig.Feels"/>. Drawn, and
    /// drawn differently: a box painted as though it collides while the solver
    /// cannot see it is precisely the misleading picture this class is for.
    /// </summary>
    public static readonly Color Idle = new(0.20f, 0.40f, 0.50f);

    /// <summary>Redraw the frames for one rig.
    ///
    /// Rebuilt whole every frame rather than posed: an
    /// <see cref="ImmediateMesh"/> has no instances to move, and at a few
    /// hundred boxes the whole surface is cheaper than the bookkeeping that
    /// would avoid it. It costs nothing at all while <see cref="Shown"/> is
    /// false, which is the case that has to stay free.</summary>
    public void Draw(WallRig? rig, WallKit.Plan? plan)
    {
        Visible = Shown;
        if (_mesh is null)
        {
            _mesh = new ImmediateMesh();
            Mesh = _mesh;
            _ink = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
                // See the class note: a box inside a brick is the case worth
                // seeing.
                NoDepthTest = true,
                // And over the sprites as well, which the depth test alone does
                // not buy: a billboard is a transparent quad with a priority,
                // and priority beats depth, so an opaque overlay is drawn in the
                // pass before it and the tank hides its own box - which is the
                // one shape this exists to show. Alpha to join that pass, and
                // the top of it to win the sort.
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                RenderPriority = 127,
            };
        }
        _mesh.ClearSurfaces();
        if (!Shown || rig is null || plan is null)
            return;
        _mesh.SurfaceBegin(Mesh.PrimitiveType.Lines, _ink);
        for (int i = 0; i < plan.Blocks.Count && i < rig.Count; i++)
            Box(rig.At(i), plan.Blocks[i].Half,
                rig.Held(i) ? Standing : Loose);
        if (rig.Hull() is { } hull)
        {
            // The true shape, never the bounding box: the driven ram is a
            // prow, and a frame drawn as its box is exactly the drift this
            // overlay exists to prevent - it read as "the collider is still
            // a slab" the day the wedge went in.
            if (hull.Prow is { } prow)
                Wedge(hull.At, prow, hull.Live ? Ram : Idle);
            else
                Box(hull.At, hull.Size * 0.5f, hull.Live ? Ram : Idle);
        }
        _mesh.SurfaceEnd();
    }

    /// <summary>The twelve edges of the driven prow - the same eight corners
    /// the solver's convex hull is built from, handed over in the rig's own
    /// metres and drawn in the cell's units like everything else here.</summary>
    private void Wedge(in Transform3D at, Vector3[] prow, Color ink)
    {
        // Corner order is Mount's: rear face 0-3, front bottom edge 4-5,
        // front top edge 6-7.
        ReadOnlySpan<(int A, int B)> edge = stackalloc (int, int)[]
        {
            (0, 1), (1, 3), (3, 2), (2, 0),
            (0, 4), (1, 5), (4, 5),
            (2, 6), (3, 7), (6, 7),
            (4, 6), (5, 7),
        };
        foreach ((int a, int b) in edge)
        {
            _mesh!.SurfaceSetColor(ink);
            _mesh.SurfaceAddVertex(at * (prow[a] / WallRig.MetresPerCell));
            _mesh.SurfaceSetColor(ink);
            _mesh.SurfaceAddVertex(at * (prow[b] / WallRig.MetresPerCell));
        }
    }

    /// <summary>The twelve edges of one oriented box.</summary>
    private void Box(in Transform3D at, Vector3 half, Color ink)
    {
        Span<Vector3> corner = stackalloc Vector3[8];
        for (int k = 0; k < 8; k++)
            corner[k] = at * new Vector3(
                (k & 1) == 0 ? -half.X : half.X,
                (k & 2) == 0 ? -half.Y : half.Y,
                (k & 4) == 0 ? -half.Z : half.Z);
        // Every pair of corners one bit apart, which is every edge and each of
        // them once.
        for (int k = 0; k < 8; k++)
            for (int bit = 1; bit <= 4; bit <<= 1)
                if ((k & bit) == 0)
                {
                    _mesh!.SurfaceSetColor(ink);
                    _mesh.SurfaceAddVertex(corner[k]);
                    _mesh.SurfaceSetColor(ink);
                    _mesh.SurfaceAddVertex(corner[k | bit]);
                }
    }

    private ImmediateMesh? _mesh;
    private StandardMaterial3D? _ink;
}
