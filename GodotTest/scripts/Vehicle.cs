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

    /// <summary>The cell it is driving onto, or the one it is standing in while
    /// parked. Named here because two things now mix the two cells' faces by
    /// <see cref="LegBlend"/> - the body's lean and the mark under it - and the
    /// pair has to be the same pair, or the ring lies on one slope while the tank
    /// stands on another.</summary>
    public Vector2I Onto => Moving ? Path[PathStep] : Cell;

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
    /// How high to lay something flat under it, which is a third number and not a
    /// spare copy of the first two.
    ///
    /// <see cref="Height"/> is where the sprite is drawn and
    /// <see cref="Standing"/> is what may hide it; both are chords between cell
    /// centres, which is right for a card standing on the ground and wrong for
    /// anything lying in it. A ramp rises a whole level across itself, so the
    /// chord runs a quarter of a level below the face at the boundary - see
    /// <see cref="HexField.SurfaceBetween"/>. The belt marks lie in the ground, so
    /// they take this one; sunk under the face, the face hid them for the upper
    /// half of every ramp.
    ///
    /// It is the ground <i>cleared</i> where the ground is tilted, not the ground
    /// itself - see <see cref="HexField.MarkClear"/>, which is why the last of it
    /// cannot be chased while the sprite rides the chord. Equal to the surface on
    /// a step with no ramp at either end, so a flat board never pays for it.
    /// </summary>
    public float Ground;

    /// <summary>
    /// Where the surface of the water this tank is in stands, in screen px off the
    /// datum, or negative infinity on dry land.
    ///
    /// <b>One number rather than a flag and a height, because the flag is the
    /// height.</b> Two readers want it and they want opposite halves: the belt
    /// marks want "is it wet" and the stage wants "where is the line", and a pair
    /// of fields is a pair that can disagree the first time one of them is set
    /// somewhere the other is not.
    ///
    /// Pushed by the harness like <see cref="Standing"/>, <see cref="Ground"/> and
    /// <see cref="OnSlope"/>: it is a fact about the board under this tank, and the
    /// board is not something a vehicle holds.
    /// </summary>
    public float Waterline = float.NegativeInfinity;

    /// <summary>Whether this tank is standing in water at all.</summary>
    public bool Wading => WadingAt(Waterline, Height);

    /// <summary>
    /// The same question asked of bare heights, so the check can be built on it -
    /// a required-member type cannot be conjured in a test, and a check that
    /// re-wrote the predicate would keep passing after this stopped meaning it.
    /// The reason <see cref="VehicleAudio.ImpactFor"/> is a named static too.
    ///
    /// <b>Against the ground, and it used to be against nothing</b> - a waterline
    /// at all was enough. That is the same statement on a flat cell, where the
    /// surface stands over the floor by construction, and it is wrong on the one
    /// cell that is half wet: a flooded ramp carries water over the back
    /// <c>WaterDepth</c> of its run and none over the front, so a tank up the
    /// beach laid no rut and wore a foam collar while standing on dry sand. The
    /// dry case still falls out without a branch, because negative infinity is
    /// under every ground there is.
    /// </summary>
    public static bool WadingAt(float waterline, float ground) =>
        waterline > ground;

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
    /// rim. See Footing.StepHeights for the numbers, Stage3D.Body for what is
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

    /// <summary>
    /// True while the face under the tank is tilted, or the one it is driving onto
    /// is - which is to say, while it is on a slope.
    ///
    /// <b>Kept apart from <see cref="Levelling"/> because a ramp changes height
    /// without changing level.</b> Levelling asks whether the two ends of the leg
    /// are on different levels, and a tank driving from the flat onto a ramp is on
    /// one level at both ends while climbing half of one - so it answers no, and it
    /// is right to: there is no wall in that leg, which is the thing it was written
    /// about.
    ///
    /// The stage draws a tank over the ground whenever either is true. A slope is
    /// where a flat billboard and the geometry disagree the most - the sprite stands
    /// vertically at one point of a face that is rising through it - and a tank
    /// parked on a ramp behind something higher lost its lower half to honest depth.
    /// A flat leg at one level keeps the test, so a tank driving behind a ridge
    /// still goes behind it. Cleared by parking onto flat ground, like
    /// <see cref="Travel"/>.</summary>
    public bool OnSlope;

    /// <summary>
    /// How much of the hull stands on the far cell of the current leg: nought while
    /// it is all on the near one, one when it has all crossed. Zero while parked.
    ///
    /// <b>It exists because the slope under a tank is not a property of the leg.</b>
    /// Asked as "which cell am I driving onto", the answer arrives the instant the
    /// order is given - a tank a whole cell away from a ramp began pitching on the
    /// spot, which is what it looked like. The face is under the hull or it is not,
    /// so the grade is mixed between the two cells by this, and the pitch begins
    /// when the nose reaches the seam and is complete when the tail has passed it.
    ///
    /// Not a duration and not a curve: the interval is the hull's own length in the
    /// leg's units, so it is shorter for a light tank and longer on a short leg, and
    /// nothing has to be tuned. The spring in <see cref="ClimbLean"/> still rounds
    /// the two corners off.</summary>
    public float LegBlend;

    /// <summary>
    /// How far along the current leg the tank is, nought at the near cell and one
    /// at the far one. Zero while parked.
    ///
    /// <b>Driven rather than read back off the position, and that is the whole of
    /// why it exists.</b> Every other progress here is derived - the belts read
    /// their travel off the heading, the height used to be read off the drawn
    /// position - because a stored copy is a copy to keep in step. This one cannot
    /// be: the drawn path is no longer a straight line between the two anchors but
    /// the ground's own surface, which bends at the shared edge, so "how far along"
    /// is not recoverable from a screen position without inverting that bend. The
    /// leg is the one place where the progress is the cause and the position the
    /// effect.
    ///
    /// In the leg's <b>flat</b> length, so it is the ground the tank covers rather
    /// than the pixels its picture crosses. The two are the same number on every
    /// leg with no ramp at either end, which is every leg of a board without them.
    /// </summary>
    public float LegDone;

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
    /// carries <see cref="Standing"/>. Climbing it is <c>Footing.SeamLine</c>,
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

    /// <summary>
    /// Where a point of this tank's picture sits on the board, from where it
    /// sits in the sprite.
    ///
    /// <b>Not <c>Sprite.ToGlobal</c>, and that is the whole of why this exists.</b>
    /// On the 3D stage every tank's sprite is reparented into a render target of
    /// its own (see <c>Stage3D.Take</c>), so ToGlobal answers in the coordinates
    /// of that target - a different space per tank. Two such answers cannot be
    /// subtracted, which is exactly what a shell did with its muzzle and its
    /// impact: the round flew off the board, drawn in a corner of the world
    /// nobody was looking at, while every number about it stayed sane.
    ///
    /// The sprite's own Position is in board space on both boards - the stage
    /// leaves it alone and compensates with its holder - so the offset applied
    /// by hand is the one expression that means the same thing either way. On the
    /// 2D board it agrees with ToGlobal to the bit, the sprites being direct
    /// children of the harness.
    /// </summary>
    public Vector2 Spot(Vector2 local) =>
        Sprite.Position + local * Sprite.BodyScale;

    /// <summary>The reverse of <see cref="Spot"/>: a point of the board as this
    /// tank's sprite sees it.</summary>
    public Vector2 Unspot(Vector2 spot) =>
        (spot - Sprite.Position) / Mathf.Max(Sprite.BodyScale, 0.0001f);

    /// <summary>
    /// How high above its own contact row a point of this tank's picture stands,
    /// in screen pixels - the lift <c>Stage3D.World</c> asks for separately.
    ///
    /// A drawn row mixes height with depth and a flat picture cannot split them,
    /// which is why props declare a fraction instead. A tank's billboard has no
    /// such trouble: the standing half of the quad is parameterised over height
    /// exactly, so a pixel that many rows above the contact is that much height
    /// and nothing else. Carrying the tank's own <see cref="Standing"/> on top,
    /// because a lift is measured from the datum rather than from the cell.
    /// </summary>
    public float LiftOf(Vector2 spot) =>
        Standing + (GroundPoint.Y - spot.Y);

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
    /// Where this tank's muzzle sits and which way its bore runs, in the
    /// sprite's own frame.
    ///
    /// <b>One measurement with three projections, and it was written out three
    /// times.</b> The round wants it in board space (<see cref="Spot"/>), the
    /// aiming ray wants it in the tank's own picture, and the ground shot wants
    /// only the direction - so each of them had the same two lines at the top and
    /// its own transform underneath. The transform is genuinely the caller's; the
    /// measurement is not, and three copies of it agree until somebody changes
    /// how a gun is measured.
    ///
    /// <paramref name="heading"/> is which way, and it is asked for rather than
    /// read off the turret because the two part company for one real case: under
    /// a standing order the round leaves along the <b>lane</b>, and the gun may
    /// still be traversing onto it. The tube is always the frame the gun is
    /// actually showing, which is what makes it foreshorten by itself and come
    /// out of the barrel on every heading.
    ///
    /// <c>Along</c> is unnormalised on purpose - it is
    /// <see cref="AtlasSet.GroundDirection"/>, whose length is the share of a
    /// ground length that survived the projection. A caller wanting a far point
    /// scales it; a caller wanting only a direction normalises it.
    /// </summary>
    public (Vector2 Tube, Vector2 Along) Bore(double heading) =>
        (Atlas.Muzzle(Atlas.FrameFor(Sprite.TurretFacing)) - Atlas.Anchor,
         Atlas.GroundDirection(heading));

    /// <summary>
    /// Where a round struck this hull, and which way it went after failing to get
    /// in: the point of impact in the sprite's own frame, and the reflection of
    /// the shot about the plate's outward normal.
    ///
    /// <b>One measurement with two projections, and <see cref="Bore"/>'s
    /// argument entire.</b> The caller wants the point in board space
    /// (<see cref="Spot"/>) and its height off the ground
    /// (<see cref="LiftOf"/>), and both roots would otherwise assemble the same
    /// four terms out of four atlas calls apiece - four copies of a measurement
    /// that agree until somebody changes how a plate is measured. What is
    /// genuinely the caller's is the transform; this is not.
    ///
    /// <b>The mirror is two bearings and one subtraction, and both bearings are
    /// already here.</b> <see cref="AtlasSet.HitBearing"/> is where the plate
    /// looks as an offset from the hull's heading - the number
    /// <see cref="AtlasSet.FaceFor"/> picked the plate with -
    /// and <paramref name="from"/> is where the shooter stands, in the same
    /// bearing space, because that is what <c>FaceFor</c> compared it against.
    /// Reflecting a direction of travel about the plate's tangent gives
    /// <c>2 * outward - from</c>; a round arriving square on
    /// (<c>from == outward</c>) goes straight back at the shooter, which is the
    /// one case worth checking by hand and the one the check checks.
    ///
    /// <b>Specular, and knowingly.</b> A real ricochet leaves nearer the plate's
    /// tangent than a mirror does, because the round digs in and slides before it
    /// departs. That bias is well inside the fan's own half-angle - see
    /// <see cref="ProcSpall"/> - so stating it here would be a second statement
    /// of a spread the effect already has.
    ///
    /// <c>Away</c> is unnormalised for <see cref="Bore"/>'s reason: it is
    /// <see cref="AtlasSet.GroundDirection"/>, whose length is the share of a
    /// ground length that survived the projection.
    /// </summary>
    public (Vector2 Plate, Vector2 Away) Graze(string face, float scatter,
                                               float rise, double from)
    {
        double outward = Sprite.HullFacing + Atlas.HitBearing(face);
        return (Plated(face, scatter, rise),
                Atlas.GroundDirection(Angles.Mod(2.0 * outward - from, 360.0)));
    }

    /// <summary>
    /// Where a round burst on this hull, and which way the plate it burst on
    /// faces: the point of impact in the sprite's own frame, and the plate's own
    /// outward normal.
    ///
    /// <b><see cref="Graze"/> with the subtraction taken off it, and that is the
    /// whole difference between the two events.</b> A ricochet leaves along the
    /// mirror of the incoming round, so it needs both bearings and one of them is
    /// where the shooter stood. HE does not leave: it stops on the face and its
    /// gases go where the metal is not, which is out along the normal and nowhere
    /// else - so <paramref name="face"/> is the only thing this asks about, and
    /// where the round came from does not enter the model at all. See
    /// <see cref="ProcSlam"/>, which is that sentence as a picture.
    ///
    /// Its own method rather than a flag on <see cref="Graze"/> for the reason
    /// <see cref="Plated"/> is its own: the two callers want different things and
    /// share the measurement, not the arithmetic. What they share is
    /// <c>Plated</c>, and it is one call in both.
    ///
    /// <c>Out</c> is unnormalised for <see cref="Bore"/>'s reason: it is
    /// <see cref="AtlasSet.GroundDirection"/>, whose length is the share of a
    /// ground length that survived the projection.
    /// </summary>
    public (Vector2 Plate, Vector2 Out) Blown(string face, float scatter,
                                              float rise) =>
        (Plated(face, scatter, rise),
         Atlas.GroundDirection(Angles.Mod(Sprite.HullFacing
                                          + Atlas.HitBearing(face), 360.0)));

    /// <summary>
    /// Where a shell struck a plate, in the sprite's own frame: the plate's
    /// centroid plus that shell's own offset on the plate's own two axes.
    ///
    /// <b>Its own method because it is read from two places at two rates.</b> The
    /// rendered hit layer wants it every frame the dust is settling - the hull can
    /// turn while it does, and the mark has to stay on the metal - while
    /// <see cref="Graze"/> wants it once, at the moment of impact, along with the
    /// mirror. Written out at both, the four atlas calls would be two copies that
    /// agree until somebody changes how a plate is measured.
    ///
    /// <b>Fractions of the plate's two half-extents, not pixels</b>, which is
    /// <see cref="TankSprite.MarkOffset"/>'s reason: both screen axes turn with
    /// the hull, so a fraction of each stays on the armour at all twenty-four
    /// headings where a pixel offset is right at one of them.
    /// <paramref name="scatter"/> and <paramref name="rise"/> are the very pair
    /// the mark and the burst already share - one shell, one place it landed.
    /// </summary>
    public Vector2 Plated(string face, float scatter, float rise)
    {
        double hull = Sprite.HullFacing;
        return Atlas.HitOffset(face, hull)
               + Atlas.HitTangent(face, hull) * scatter
               + Atlas.HitSlope(face, hull) * rise;
    }

    /// <summary>Whether a hit on <paramref name="face"/> is on a plate turned
    /// away from the camera, and so belongs behind the tank rather than over it.
    /// The sign <see cref="AtlasSet.HitFacing"/> exists for, read in the one place
    /// so the rendered layer and the built one cannot disagree about which side of
    /// a hull a hit is on - see <see cref="TankSprite.HitBehind"/>.</summary>
    public bool Turned(string face) =>
        Atlas.HitFacing(face, Sprite.HullFacing) <= 0.0;

    /// <summary>
    /// The rounds this gun has in the air, and the smoke they are still laying.
    ///
    /// <b>On the tank because a round belongs to the gun that fired it</b>, not
    /// to the board it crosses. It carries its shooter already - the class
    /// decides how deep it gets - and everything that happens to it afterwards is
    /// one tank's business: it leaves, it crosses, it lands. Which tank it lands
    /// on, when it lands on one, is the harness's business and stays there; see
    /// <see cref="TankTick.Launch"/> for the seam.
    ///
    /// <b>Which is also why it is not a list on the scene root.</b> It was, and a
    /// bench with a tank on it therefore had a gun that only flashed - the whole
    /// flight lived in the harness, so the one-tank bench could not have it
    /// without a second copy. Same argument as <see cref="TankTick"/> itself.
    ///
    /// The nodes are not children of this tank. A shell must not ride the hull
    /// that fired it, and on the staged board the sprite is reparented into a
    /// render target of its own - parenting a tracer there draws it inside the
    /// picture rather than on the board. <see cref="TankTick.Deck"/> is where
    /// they go.
    /// </summary>
    public readonly List<Shell> Rounds = new();

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

    /// <summary>
    /// Which tank a click on a cell means, or -1 for "no tank - this is a move
    /// order".
    ///
    /// A cell rather than the sprite's pixels: a tank occupies a cell, that is the
    /// unit the game is played in, and the two readings differ only where a
    /// silhouette overhangs its own hex - where a pixel test would let you select
    /// a tank by clicking the ground beside it.
    ///
    /// The tank already being driven does not count, so clicking the one you are
    /// driving is an order to stay put rather than a selection that does nothing.
    /// Here rather than on the harness: it is a question about a list of tanks
    /// and a cell, and says nothing about the board they stand on.
    ///
    /// Static and pure so the routing can be asserted: "a click on an occupied
    /// cell selects instead of ordering" is the whole feature.
    /// </summary>
    public static int SelectionFor(IReadOnlyList<Vehicle> vehicles, int active,
                                  Vector2I cell)
    {
        for (int i = 0; i < vehicles.Count; i++)
            if (i != active && vehicles[i].Cell == cell)
                return i;
        return -1;
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
