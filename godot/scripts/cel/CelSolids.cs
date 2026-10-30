using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// What a <see cref="CelCloud"/>'s puffs may not stand through: boxes round
/// the hull, the turret and the belts of every tank on the stage, refreshed
/// each frame (<see cref="Clear"/>, <see cref="Box"/>/<see cref="Take"/>),
/// and a puff that has gone into one thinned away as the cloud is drawn
/// (<see cref="Thin"/>). The puffs live in the world and know nothing of the
/// tank: a tank backing up ran over its own exhaust and drove on with the
/// cloud standing through its turret (the user showed it).
///
/// <b>Thinned, not shoved.</b> Moved out of the box by the nearest face, the
/// puffs a turret backed into went out over its roof and stood there as a
/// chimney; made dearer over the roof, they went out of the side facing the
/// eye and lay over the plate - smoke before the armour, the same fault seen
/// from the other side. And a box is fatter than a round turret: shoved off
/// its corners, the smoke hung in the air a hand off the steel. A puff the
/// armour meets goes instead, as the hull breaks the gas up: its radius by
/// how deep its middle is in the box - whole while it stands
/// <see cref="Whole"/> of a radius clear of it, nought <see cref="Gone"/> of a
/// radius inside. The box's distance is smooth, so it thins as the tank
/// comes on, no faster, and the part of it still out is hidden behind the
/// plate by the depth test as before. Stateless: a puff the tank draws off
/// again grows back on its own path.
/// </summary>
public sealed class CelSolids
{
    /// <summary>How far clear of a box a puff's middle is still whole, and
    /// how deep in it is gone, radii.</summary>
    public float Whole = 0.25f, Gone = 0.5f;

    private readonly List<(Vector3 At, Basis Axes, Vector3 Half)> _boxes = new();

    public void Clear() => _boxes.Clear();

    /// <summary>A box: <paramref name="local"/> in <paramref name="frame"/>'s
    /// own space, the frame's scale taken into the box.</summary>
    public void Box(Transform3D frame, Aabb local)
    {
        if (local.Size.X <= 0.0f || local.Size.Y <= 0.0f || local.Size.Z <= 0.0f)
            return;
        Basis b = frame.Basis;
        Vector3 scale = b.Scale;
        Vector3 half = local.Size * 0.5f * scale;
        _boxes.Add((frame * local.GetCenter(), b.Orthonormalized(), half));
    }

    /// <summary>The model's hull, turret (without the gun, which is its own
    /// node) and belts.</summary>
    public void Take(TankModel model)
    {
        if (model.Hull is MeshInstance3D hull && hull.Mesh is not null)
            Box(hull.GlobalTransform, hull.Mesh.GetAabb());
        if (model.Turret is MeshInstance3D turret && turret.Mesh is not null)
            Box(turret.GlobalTransform, turret.Mesh.GetAabb());
        foreach (TankModel.Track t in model.Tracks)
        {
            if (t.Path.Length == 0)
                continue;
            var box = new Aabb(t.Path[0], Vector3.Zero);
            foreach (Vector3 p in t.Path)
                box = box.Expand(p);
            // The run is a line in the belt's plane; the link has a width the
            // path does not carry - the wheels' own, a share of the run.
            float wide = Mathf.Max(box.Size.Y * 0.35f, 1e-3f);
            box.Position -= new Vector3(wide * 0.5f, 0.0f, 0.0f);
            box.Size += new Vector3(wide, 0.0f, 0.0f);
            Box(t.Node.GlobalTransform, box);
        }
    }

    /// <summary>The share of its radius a puff of radius <paramref name="r"/>
    /// at <paramref name="at"/> keeps, 0..1.</summary>
    public float Thin(Vector3 at, float r)
    {
        float keep = 1.0f;
        foreach (var (mid, axes, half) in _boxes)
        {
            Vector3 d = at - mid;
            Vector3 q = new Vector3(Mathf.Abs(d.Dot(axes.X)), Mathf.Abs(d.Dot(axes.Y)),
                                    Mathf.Abs(d.Dot(axes.Z))) - half;
            // The box's own distance: outside, to its nearest point; inside,
            // minus the depth under its nearest face.
            float sd = new Vector3(Mathf.Max(q.X, 0.0f), Mathf.Max(q.Y, 0.0f), Mathf.Max(q.Z, 0.0f)).Length()
                       + Mathf.Min(Mathf.Max(q.X, Mathf.Max(q.Y, q.Z)), 0.0f);
            keep = Mathf.Min(keep, Mathf.SmoothStep(-Gone * r, Whole * r, sd));
        }
        return keep;
    }
}
