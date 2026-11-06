using System;
using System.Runtime.InteropServices;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The tide's rise drawn (<see cref="TideFront"/>; docs/tank3d.md, «Прилив»):
/// for the rise's few seconds the ground is a mesh off the old board that
/// carries the new one's height, normal and paint (<see cref="ReliefMesh"/>),
/// and the vertex shader lets each point down as the water reaches it. The
/// water's sheet is the new board's from the start, so its depth clip draws
/// the shoreline crawling in with the foam it already has; the ground ahead
/// darkens, and a crest of foam rides the front.
///
/// <b>Swapped back at the end with nothing to see.</b> The points the rise
/// changes end under the sea's murk, so the old board's rock, soil looks and
/// levels the morph mesh still wears there are never seen; the new board's
/// own mesh takes over when the clock runs out.
/// </summary>
public sealed partial class Stage3D
{
    /// <summary>The new board's own mesh, put back by <see cref="TideEnd"/>.</summary>
    private Mesh? _tideAfter;
    private ImageTexture? _tideArrive;

    /// <summary>The rise begins: the board built off the new relief (already
    /// set as <see cref="Relief"/>), then the sinking mesh in its place. False
    /// when it cannot be shown (no relief built, another raster): the board is
    /// simply the new one.</summary>
    public bool TideBegin(TideFront front)
    {
        Standing();
        if (Relief is null || !ReferenceEquals(Relief, front.Next) || _tops.Mesh is null
            || _tops.MaterialOverride is not ShaderMaterial ground)
            return false;
        Relief f = front.Next;
        _tideAfter = _tops.Mesh;
        _tops.Mesh = ReliefMesh(front.Old, f);
        byte[] bytes = MemoryMarshal.AsBytes(front.Arrive.AsSpan()).ToArray();
        _tideArrive = ImageTexture.CreateFromImage(Image.CreateFromData(f.Nx, f.Ny, false, Image.Format.Rf, bytes));
        // texel centres on the points, as the sun's height map (BakeRelief)
        float x0 = f.X0 + Origin.X - f.Cell * 0.5f, z0 = f.Z0 + Origin.Y / Squash - f.Cell * 0.5f;
        var map = new Vector4(x0, z0, 1.0f / (f.Nx * f.Cell), 1.0f / (f.Ny * f.Cell));
        foreach (ShaderMaterial m in TideInks(ground))
        {
            m.Param("tide_arrive", _tideArrive);
            m.Param("tide_map", map);
            m.Param("tide_fall", TideFront.Fall);
            m.Param("tide_end", TideFront.Spread + TideFront.Fall);
            m.Param("tide_t", 0.0f);
            m.Param("tide_on", 1.0f);
        }
        return true;
    }

    /// <summary>The rise's clock, s - a frame's tick.</summary>
    public void TideAt(float t)
    {
        if (_tideAfter is null)
            return;
        if (_tops.MaterialOverride is ShaderMaterial ground)
            ground.Param("tide_t", t);
        _deepInk?.Param("tide_t", t);
    }

    /// <summary>The rise played: the new board's own mesh back.</summary>
    public void TideEnd()
    {
        if (_tideAfter is null)
            return;
        _tops.Mesh = _tideAfter;
        _tideAfter = null;
        _tideArrive = null;
        if (_tops.MaterialOverride is ShaderMaterial ground)
            ground.Param("tide_on", 0.0f);
        _deepInk?.Param("tide_on", 0.0f);
    }

    private ShaderMaterial[] TideInks(ShaderMaterial ground) =>
        _deepInk is null ? new[] { ground } : new[] { ground, _deepInk };
}
