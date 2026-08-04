using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// One tank on the field: its sprite, its atlas, its class, where it is, and
/// every clock that runs on it.
///
/// The harness drove a single tank and switched which atlas that tank wore. That
/// answered "does this sprite set hold together" and could not answer "is the
/// light one smaller than the heavy one", because the comparison needs both on
/// screen at once - a viewer cannot judge 0.85x against 1.15x from memory, which
/// is the same argument the acceleration figures are chosen on.
///
/// So the per-tank state moved here and Main keeps a list of these. What stayed
/// in Main is what belongs to the harness rather than to a vehicle: which
/// effects are switched on, which calibre is loaded, the camera, the panel. The
/// division is worth stating because it decides how every key behaves - a global
/// toggle reaches all three tanks, and anything a tank owns reaches only the one
/// being driven.
///
/// Every clock is its own instance, and that is the point of them being here.
/// Shared, three tanks would tremble in lockstep and exhaust on the same frame,
/// which reads as one animation played three times rather than as three engines.
/// </summary>
public sealed class Vehicle
{
    public required string Tag { get; init; }
    public required AtlasSet Atlas { get; init; }
    public required TankSprite Sprite { get; init; }
    public required MovementProfile Profile { get; init; }

    /// <summary>Where it was parked, so a reset can put it back. The three start
    /// spread along one row: same screen height, two columns apart, which is what
    /// makes the size difference readable at a glance instead of a measurement.
    /// </summary>
    public required Vector2I HomeCell { get; init; }

    /// <summary>The cell it is standing in - the last one reached, while under
    /// way.</summary>
    public Vector2I Cell;

    public List<Vector2I> Path = new();
    public int PathStep;
    public double Speed;

    public bool Moving => PathStep < Path.Count;

    public readonly BodyPitch Pitch = new();
    public readonly BodyRumble Rumble = new();
    public readonly EngineTremble Tremble = new();
    public readonly TurretScan Scan = new();
    public readonly TrackLoop Track = new();
    public readonly ExhaustLoop Exhaust = new();
    public readonly BurnLoop Burn = new();
    public readonly HitLoop Hit = new();
    public readonly Recoil Recoil = new();

    /// <summary>The gun tube's own clock. Named for the part rather than the
    /// event so it does not read as a second <see cref="Recoil"/> - that one is
    /// the hull rocking under the shot, this one is the tube sliding in its
    /// mount, and they run off the same trigger at different rates.</summary>
    public readonly RecoilLoop Barrel = new();

    /// <summary>
    /// Where this tank touches the ground, in its parent's space.
    ///
    /// The sprite's own origin is the turret axis lifted to the mid-height of the
    /// fitted bounds, so it floats a scaled 50-odd pixels above the ground. Three
    /// separate things need the ground point instead - where to stand the tank,
    /// which tank is nearer the camera, and where to draw the selection ring - and
    /// this is the one definition of it, because three copies of that expression is
    /// how two of them come to disagree.
    /// </summary>
    public Vector2 GroundPoint =>
        Sprite.Position + Atlas.GroundOffset * Sprite.BodyScale;

    /// <summary>Whether this one is on fire. Per tank rather than per harness:
    /// key J burns the tank being driven, and a bench where all three catch at
    /// once could not show a burning tank next to an intact one.</summary>
    public bool Burning;

    /// <summary>Shells this tank has taken. Drives the deterministic scatter, so
    /// it is per tank for the same reason the marks are: they are a record of
    /// what happened to this hull.</summary>
    public int HitCount;

    /// <summary>Screen frames since its gun went off, or -1 between shots.</summary>
    public int ShotFrame = -1;

    /// <summary>
    /// Which tank is standing on a cell, or null for an empty one.
    ///
    /// Static and over the list rather than a lookup kept beside it: three
    /// vehicles is a scan of three, and a dictionary from cell to tank would be a
    /// second copy of where everyone is - wrong for exactly as long as it takes
    /// somebody to move without updating it.
    /// </summary>
    public static Vehicle? At(IReadOnlyList<Vehicle> vehicles, Vector2I cell)
    {
        foreach (Vehicle vehicle in vehicles)
            if (vehicle.Cell == cell)
                return vehicle;
        return null;
    }

    /// <summary>Cells taken by everyone except <paramref name="mover"/> - what a
    /// path has to go round. A tank driving straight through another tank on a
    /// bench built to compare tanks is not a thing to leave for later.</summary>
    public static HashSet<Vector2I> Occupied(IReadOnlyList<Vehicle> vehicles,
                                            Vehicle mover)
    {
        var taken = new HashSet<Vector2I>();
        foreach (Vehicle vehicle in vehicles)
            if (vehicle != mover)
                taken.Add(vehicle.Cell);
        return taken;
    }
}
