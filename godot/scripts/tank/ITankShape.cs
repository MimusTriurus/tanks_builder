using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The tank as the built effects see it - <see cref="ProcFire"/>, both
/// <see cref="ProcSmoke"/>s, <see cref="ProcFume"/>, <see cref="ProcFlash"/> and
/// <see cref="ProcPierce"/>: where it breathes and where it fires from, in its
/// own world units; how those units reach the screen; and what of the tank
/// stands in front of an element at a pixel.
///
/// <b>Two answers, one per kind of tank.</b> A sprite tank answers from its atlas
/// (<see cref="AtlasSet"/>): stamped ports and bore, the muzzle measured per
/// heading, the rendered height map and the hull's own frame for a mask. A 3D
/// tank answers from its model (<see cref="ModelShape"/>): the sidecar's joints
/// posed this frame, and a height map rendered live. The effects cannot tell the
/// two apart, and that is the point - the model of a flame or a plume is one
/// text, whichever tank it burns on.
///
/// <b>Every world point is in the frame the ports were always stamped in</b>:
/// Blender's, Z up, the tank's front along -Y, and <see cref="Project"/> is the
/// one place it turns into pixels off the tank's anchor.
/// </summary>
public interface ITankShape
{
    /// <summary>World units per screen pixel of the board at zoom 1.</summary>
    double UnitsPerPixel { get; }

    /// <summary>The camera's elevation, degrees.</summary>
    double Elevation { get; }

    /// <summary>Where the engine breathes - see <see cref="AtlasSet.Port"/>.</summary>
    IReadOnlyList<AtlasSet.Port> Ports { get; }

    /// <summary>The hull's longest world axis, which every length of the smoke
    /// and the flame is quoted against.</summary>
    double HullLength { get; }

    bool HasPorts { get; }

    bool HasBore { get; }

    /// <summary>The bore as the tube stands on a rung of the ladder.</summary>
    AtlasSet.Port BoreAt(int rung);

    /// <summary>A world point or vector as px off the anchor, for a part of the
    /// tank facing <paramref name="headingDegrees"/> - linear in its vector, so a
    /// difference of projections is the projection of the difference.</summary>
    Vector2 Project(Vector3 world, double headingDegrees);

    /// <summary>Where the muzzle is drawn, in px off the anchor, for the turret
    /// facing <paramref name="facing"/> and the tube on <paramref name="rung"/>.
    /// </summary>
    Vector2 MuzzleOffset(double facing, int rung);

    /// <summary>Whether a layer of effect draws over the turret at this hull
    /// heading - the canvas's draw order, which a tank in a depth buffer does
    /// not need.</summary>
    bool OverTurretAt(string layer, double facing);

    /// <summary>The world heights the height map's byte range spans.</summary>
    double HeightLow { get; }

    double HeightHigh { get; }

    /// <summary>The height map at this hull heading, or null where there is
    /// none - see <see cref="Plumes.SetDepth"/>.</summary>
    DepthMap? DepthAt(double facing);

    /// <summary>The hull's silhouette at this hull heading, for a layer that
    /// masks light by it - see <see cref="ProcPierce"/>. Null where there is
    /// none.</summary>
    Silhouette? MaskAt(double facing);

    /// <summary>The hull's length on screen, px - what the light through a hole
    /// is sized against.</summary>
    float HullSpanPx { get; }
}

/// <summary>
/// A height map as the built layers read it (<see cref="Plumes.DepthCode"/>): one
/// byte of height per pixel, 0 where there is no tank, 1..255 over
/// <see cref="ITankShape.HeightLow"/>..<see cref="ITankShape.HeightHigh"/>.
/// <paramref name="Region"/> is where this heading's frame sits in the sheet and
/// <paramref name="Sheet"/> how big the sheet is, both in the map's own pixels,
/// which are the board's; a point <c>at</c> px off the anchor reads the frame at
/// <c>at + Shift</c>. The texture may hold more texels than that - a map
/// rendered finer than the board it is read on.
/// </summary>
public readonly record struct DepthMap(Texture2D Texture, Rect2 Region, Vector2 Sheet, Vector2 Shift);

/// <summary>
/// A picture whose alpha is the hull: <paramref name="Region"/> of
/// <paramref name="Texture"/>, drawn at <paramref name="Quad"/> px off the anchor.
/// </summary>
public readonly record struct Silhouette(Texture2D Texture, Rect2 Region, Rect2 Quad);
