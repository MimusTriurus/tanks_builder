using System;
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

    /// <summary>
    /// Hull heading as of the end of last frame, so this frame can tell how far
    /// it swung.
    ///
    /// Derived rather than reported: a pivot happens from an order taking a
    /// corner, from a standing start, and from the A/D keys, and every future
    /// source would have to remember to announce itself. Reading the heading back
    /// catches all of them, including the keys, which are handled in _Input and
    /// would otherwise be invisible to the belts entirely.
    /// </summary>
    public double LastHullFacing;

    /// <summary>
    /// Where the turret sat relative to the hull at the end of last frame, so
    /// this frame can tell how far the ring turned.
    ///
    /// **Relative, and that is the whole of it.** The traverse motor turns when
    /// the turret moves against the hull, not when it moves against the world.
    /// The two part company in both directions and the bench can show both: a
    /// turret riding round with an unlocked hull is not being driven at all,
    /// and a stabilised turret holding a world heading through a hull turn is
    /// being driven the whole time. Taking the rate off TurretFacing alone gets
    /// each of those exactly backwards.
    ///
    /// Read back off the angle rather than reported by whatever turned it, for
    /// the reason <see cref="LastHullFacing"/> is: the keys, the scan, the mouse
    /// and now the gunnery all move it, and every future one would have to
    /// remember to announce itself.
    /// </summary>
    public double LastTurretOffset;

    /// <summary>Where it touched the ground at the end of last frame, field-local,
    /// so this frame can tell how far and which way it travelled.
    ///
    /// Read back for <see cref="LastHullFacing"/>'s reason, and the list of ways
    /// a tank moves is longer than the list of ways it turns: an order, the W/S
    /// keys, a reset dropping it on its home cell. The wood is shouldered aside
    /// by the hull passing, so what it needs is the passing and not whatever
    /// arranged it.</summary>
    public Vector2 LastGroundPoint;

    public bool Moving => PathStep < Path.Count;

    /// <summary>How far off the datum it is drawn, in screen px. Carried in the
    /// position already - <see cref="HexField.CellAnchor"/> is lifted, so driving
    /// between two cells at different levels raises the sprite for free - and
    /// kept here as well because the depth needs the two apart: which row it
    /// stands on and how high it is standing are different terms of the same
    /// sum, and the position is their total.
    ///
    /// <b>It is also how high it counts as standing for what may hide it</b>, and
    /// there was a second number for that until this one answered it: the higher
    /// of the two cells it is between, which called a tank up on the crown at the
    /// first pixel of the climb. See Main.Climb.</summary>
    public float Height;

    /// <summary>
    /// How high it counts as standing - equal to <see cref="Height"/> whenever it
    /// is standing still, and the crown of the step while it is on the wall
    /// between two levels. See Main.Climb for when it leaves that crown.
    ///
    /// <b>The second number is back, and the 3D stage is what needed it.</b>
    /// Height alone answered this while the answer was a z index: being a level
    /// early only admitted the tank over the crown early, which is what a
    /// climbing tank should look like. On the stage the number is a position in
    /// the world, so the same interpolation puts the tank <b>inside the hill</b>
    /// for the first frames of a step - measured, and it takes the running gear
    /// off exactly as the flat billboard used to.
    ///
    /// Costs nothing on screen. <c>World(row + L, L)</c> draws at the same pixel
    /// for every L, so this moves the tank up and toward the camera by amounts
    /// that cancel, and buys depth alone.
    /// </summary>
    public float Standing;

    /// <summary>
    /// How high the trailing end of the hull counts as standing - equal to
    /// <see cref="Standing"/> whenever the two ends have nothing to disagree
    /// about, the height read off the leg behind the contact point while the
    /// tank climbs, and the crown it is leaving while it descends behind a
    /// rim. See Main.StepHeights for the numbers, Stage3D.Body for what is
    /// done with them.
    ///
    /// <b>It exists because one depth per tank cannot answer a climb.</b>
    /// Standing promotes the sprite to the crown so the cell being climbed does
    /// not cut the running gear - and applied to the whole billboard it paid
    /// with every other cell at that level: promoted at the first pixel, the
    /// tank spent the whole of (3,2)->(4,3) drawn over (3,3), a lateral hex it
    /// had not come up to and was never level with until the end. While the hull
    /// is on a wall its nose and tail are at two different heights, and this is
    /// the tail's; the stage lays the difference along the travel axis, so the
    /// low end of the sprite goes back behind the ground it has not climbed yet
    /// while the nose keeps the crown it is entitled to.
    /// </summary>
    public float Trailing;

    /// <summary>True while the current leg takes the tank from one level to
    /// another - the legs where the stage draws it over everything, because
    /// every honest depth tried for a billboard crossing a wall bought some
    /// stutter (see Stage3D.Place). A flat leg keeps honest depth: a tank
    /// passing behind a ridge on its own level stays behind it. Cleared by
    /// parking, like <see cref="Travel"/>.</summary>
    public bool Levelling;

    /// <summary>The screen direction of the current leg, zero while parked.
    /// What lets the stage lay <see cref="Trailing"/> along the axis the hull
    /// actually spans rather than across the whole billboard - and what fades
    /// a climb's split out on legs up and down the screen, where the two ends
    /// share their screen columns and a split would be wrong for both. A
    /// descent's split does not fade: its seam is a wipe down the sprite and
    /// never degenerates (see Main.Climb).</summary>
    public Vector2 Travel;

    /// <summary>The screen-space line the stage splits the sprite's depth
    /// along, as <c>(a, b, c)</c> with <c>a*x + b*y &gt;= c</c> the side that
    /// carries <see cref="Standing"/>. Climbing it is <c>Main.SeamLine</c>,
    /// the shared edge of the hex being mounted and the raised neighbour
    /// nearest the camera; descending behind a rim it is the wipe
    /// <c>Main.Climb</c> sweeps down the sprite. Meaningless while parked,
    /// and unread: the split is zero there.</summary>
    public Vector3 SeamLine;

    /// <summary>The ground that stands in front of this tank and above it,
    /// repainted over it. Null until the harness builds one.</summary>
    public ReliefCap? Cap;

    /// <summary>What the slope is doing to the drawn body. Its own instance for
    /// the reason every clock here is: three tanks on three grades leaning as one
    /// is one animation played three times.</summary>
    public readonly ClimbLean Lean = new();

    public readonly BodyPitch Pitch = new();
    public readonly BodyRumble Rumble = new();
    public readonly EngineTremble Tremble = new();
    public readonly TurretScan Scan = new();
    /// <summary>A clock per belt, because the two belts do not run together.
    ///
    /// One clock for both was the arrangement until the ruts, and it could not
    /// be right about the one thing a tank does that is all track: a pivot runs
    /// them in opposite directions, and a single unsigned phase wound them the
    /// same way, so one of the two was always going backwards up its own loop.
    /// The atlas was ready for this from the start - track_left and track_right
    /// are separate layers - and it was the marks on the ground that made the
    /// error impossible to keep, because a rut records the direction where a
    /// spinning belt only suggests it.</summary>
    public readonly TrackLoop TrackLeft = new();

    public readonly TrackLoop TrackRight = new();

    /// <summary>The belt the panel, the trace and the engine note speak for:
    /// whichever is winding faster. Not an average - during a pivot the two are
    /// equal and opposite, and an average of those is zero, which would report
    /// still belts on the most track-heavy thing a tank does.</summary>
    public TrackLoop Track =>
        Math.Abs(TrackLeft.PhaseRate) >= Math.Abs(TrackRight.PhaseRate)
            ? TrackLeft : TrackRight;
    public readonly ExhaustLoop Exhaust = new();
    public readonly BurnLoop Burn = new();
    public readonly HitLoop Hit = new();
    public readonly Recoil Recoil = new();

    /// <summary>The gun tube's own clock. Named for the part rather than the
    /// event so it does not read as a second <see cref="Recoil"/> - that one is
    /// the hull rocking under the shot, this one is the tube sliding in its
    /// mount, and they run off the same trigger at different rates.</summary>
    public readonly RecoilLoop Barrel = new();

    /// <summary>This tank's players. Null when nothing loaded, which is a state
    /// the bench has to reach cleanly: Sounds/ is not in the repository, so a
    /// fresh checkout runs silent until stage_sounds.sh has been run.
    ///
    /// A node rather than a clock, so it is not readonly like the rest - it is
    /// built after the vehicle and parented into the tree.</summary>
    public VehicleAudio? Audio;

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

    /// <summary>Whether it has been killed, and how long ago. Per tank for the
    /// reason the fire is, and more so: a wreck beside an intact tank is the
    /// whole of what this shows.</summary>
    public readonly Wreck Wreck = new();

    /// <summary>Whether this one is on fire. Per tank rather than per harness:
    /// key J burns the tank being driven, and a bench where all three catch at
    /// once could not show a burning tank next to an intact one.</summary>
    public bool Burning;

    /// <summary>
    /// The tank this one is shooting at, or null for nobody.
    ///
    /// Per vehicle rather than on the harness, for the reason every clock here
    /// is: an order given to a tank belongs to that tank. Select another one and
    /// this one keeps engaging, which is the whole of the scene - a bench where
    /// only the tank under the cursor could be fighting would be one duel shown
    /// three times.
    /// </summary>
    public Vehicle? Target;

    /// <summary>The solution against <see cref="Target"/> as of this frame.
    ///
    /// Kept rather than re-derived by everything that wants it - the fire gate,
    /// the lanes drawn on the ground, the panel, the trace. Solved once per
    /// frame because both tanks move; the alternative is four answers to one
    /// question and no reason for them to agree.</summary>
    public Shot Solution = Gunnery.None;

    /// <summary>Seconds until the gun will go off again. Counted down rather
    /// than a timestamp compared against a clock, because the harness fixes the
    /// time step under --capture and --trace and a countdown is the same number
    /// of frames in two runs.</summary>
    public double ReloadLeft;

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
