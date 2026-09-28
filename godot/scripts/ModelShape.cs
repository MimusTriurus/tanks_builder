using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// A 3D tank's answers to <see cref="ITankShape"/> - what the built effects
/// read, taken off the model as it stands this frame rather than off an atlas.
///
/// <b>The frame is the stamped ports' own</b> (Blender's: Z up, the front along
/// -Y) about the card's anchor, turned by the facing asked for - the hull's or
/// the turret's, in the sprite bench's bearings - and upright: its Z is world
/// up, so a plume climbs toward the sky and not toward the roof of a hull that
/// stands on a ramp. <see cref="Update"/> re-reads every joint into it, so what
/// the effects are handed is where the exhausts and the muzzle actually are,
/// rocked, tipped and laid; <see cref="Project"/> then undoes exactly the turn
/// <see cref="Update"/> put in, which is why a point comes out on the joint to
/// the pixel whatever the joint was doing.
///
/// <b>Measured, where the atlas carries a stamp.</b> The hull's length and width
/// and the bore's radius are the model's own (<see cref="TankModel.HullLength"/>,
/// <see cref="TankModel.BoreRadius"/>). The port's radius is the one number no
/// mesh states - the sidecar names points on the grilles, not their size - so it
/// is a share of the hull's width (<see cref="PortShare"/>), and the grilles are
/// one port at their middle, as every atlas stamps a pair of them.
///
/// <b>The holdout is live</b> (<see cref="Map"/>): the bench renders the model's
/// heights into a target the size of a card, so the map is this frame's hull
/// and belts rather than a render at the nearest fifteen degrees.
/// </summary>
public sealed class ModelShape : ITankShape
{
    private readonly TankModel _model;
    private readonly float _squash, _rise;

    public ModelShape(TankModel model, float squash, float rise)
    {
        _model = model;
        _squash = squash;
        _rise = rise;
    }

    /// <summary>
    /// The port's radius as a share of the hull's width.
    ///
    /// <b>Read off the five stamped sets, and the medium's is the one taken</b>:
    /// radius over hull width comes out 0.27 (LTP), 0.28 (MTP), 0.25 (HTP_v1),
    /// 0.35 (TDP) and 0.33 (HMP) - the pipeline measures it on the generator's
    /// exhaust mesh (<c>exhaust_point.py</c>), which the game variant does not
    /// carry. The medium is the tank every other figure on the board is read
    /// against (<see cref="MovementProfile.Size"/>).
    /// </summary>
    public const float PortShare = 0.28f;

    /// <summary>How far above the tank's own height the map's byte range runs,
    /// as a share of it: room for a hull rocked or tipped past its rest.</summary>
    public const float HeightRoom = 1.25f;

    /// <summary>The live height map, and its side in board px, centred on the
    /// anchor - the texture may be finer than a texel a px. Null until the
    /// bench has one.</summary>
    public Texture2D? Map;
    public int MapSide;

    /// <summary>The card's anchor, world: where px (0, 0) is.</summary>
    public Vector3 Anchor { get; private set; }

    private readonly List<AtlasSet.Port> _ports = new();
    private AtlasSet.Port _bore;

    /// <summary>Re-read every joint for this frame, about
    /// <paramref name="anchor"/>, at the hull's and the turret's bearings.</summary>
    public void Update(Vector3 anchor, double hullFacing, double turretFacing)
    {
        Anchor = anchor;
        _ports.Clear();
        if (_model.Exhausts.Count > 0)
        {
            Vector3 at = Vector3.Zero, dir = Vector3.Zero;
            foreach (Node3D e in _model.Exhausts)
            {
                at += Local(e.GlobalPosition - anchor, hullFacing);
                dir += Turned(e.GlobalBasis.Y, hullFacing);
            }
            at /= _model.Exhausts.Count;
            _ports.Add(new AtlasSet.Port(at, dir.Normalized(), PortShare * _model.HullWidth));
        }
        Node3D muzzle = _model.Muzzle;
        _bore = new AtlasSet.Port(Local(muzzle.GlobalPosition - anchor, turretFacing),
                                  Turned(muzzle.GlobalBasis.Z, turretFacing),
                                  _model.BoreRadius);
        float ppu = _model.PixelsPerUnit;
        HeightLow = (_model.Tank.GlobalPosition.Y - anchor.Y) / ppu;
        HeightHigh = HeightLow + _model.Size.Y * HeightRoom;
    }

    /// <summary>The engine port's world point - for the card that stands in
    /// front of it.</summary>
    public Vector3 PortWorld
    {
        get
        {
            Vector3 at = Vector3.Zero;
            foreach (Node3D e in _model.Exhausts)
                at += e.GlobalPosition;
            return _model.Exhausts.Count > 0 ? at / _model.Exhausts.Count : _model.Tank.GlobalPosition;
        }
    }

    /// <summary>The world turn a facing stands for: the sprite bench's bearing
    /// is the 3D heading less ninety.</summary>
    private static Basis Yaw(double facing) =>
        new(Vector3.Up, Mathf.DegToRad((float)facing + 90.0f));

    /// <summary>A world direction into the stamped frame at a facing.</summary>
    private static Vector3 Turned(Vector3 world, double facing)
    {
        Vector3 g = Yaw(facing).Inverse() * world;
        return new Vector3(g.X, -g.Z, g.Y).Normalized();
    }

    /// <summary>A world offset, in board px, into the stamped frame in model
    /// units.</summary>
    private Vector3 Local(Vector3 world, double facing)
    {
        Vector3 g = Yaw(facing).Inverse() * world / _model.PixelsPerUnit;
        return new Vector3(g.X, -g.Z, g.Y);
    }

    /// <summary>A world offset as screen px, y down.</summary>
    private Vector2 Drawn(Vector3 d) => new(d.X, d.Z * _squash - d.Y * _rise);

    // --- ITankShape ----------------------------------------------------------

    public double UnitsPerPixel => 1.0 / _model.PixelsPerUnit;

    public double Elevation => Mathf.RadToDeg(Mathf.Asin(_squash));

    public IReadOnlyList<AtlasSet.Port> Ports => _ports;

    public double HullLength => _model.HullLength;

    public bool HasPorts => _ports.Count > 0 && _model.HullLength > 0.0f;

    public bool HasBore => _model.BoreRadius > 0.0f;

    /// <summary>The tube as it stands - laid, recoiling, drooped - whatever the
    /// rung: the model's gun is continuous, and the ladder is the sprite's.</summary>
    public AtlasSet.Port BoreAt(int rung) => _bore;

    public Vector2 Project(Vector3 world, double headingDegrees)
    {
        var gl = new Vector3(world.X, world.Z, -world.Y);
        return Drawn(Yaw(headingDegrees) * gl * _model.PixelsPerUnit);
    }

    /// <summary>The Muzzle node, drawn - the joint itself, so there is no
    /// pivot error to cancel as the atlas's measured mouth has.</summary>
    public Vector2 MuzzleOffset(double facing, int rung) =>
        Drawn(_model.Muzzle.GlobalPosition - Anchor);

    /// <summary>Never: on a card, what is in front of the turret is the depth
    /// buffer's to say.</summary>
    public bool OverTurretAt(string layer, double facing) => false;

    public double HeightLow { get; private set; }

    public double HeightHigh { get; private set; }

    public DepthMap? DepthAt(double facing) =>
        Map is null ? null
            : new DepthMap(Map, new Rect2(0.0f, 0.0f, MapSide, MapSide), Vector2.One * MapSide,
                           Vector2.One * MapSide * 0.5f);

    /// <summary>The same target: its alpha is where the hull and belts are.
    /// </summary>
    public Silhouette? MaskAt(double facing) =>
        Map is null ? null
            : new Silhouette(Map, new Rect2(0.0f, 0.0f, Map.GetWidth(), Map.GetHeight()),
                             new Rect2(-Vector2.One * MapSide * 0.5f, Vector2.One * MapSide));

    public float HullSpanPx => _model.HullLength * _model.PixelsPerUnit;
}
