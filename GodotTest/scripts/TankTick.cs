using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// One tank's frame: driving it along its order, standing it on the cell it
/// reaches, and winding every clock hung off it.
///
/// <b>Lifted out of <see cref="Main"/> whole rather than written a second
/// time, and that is the only reason it exists.</b> A bench that wanted a tank
/// on a hex - <see cref="TankBench"/> - had two ways to get one: copy this
/// sequence, or share it. The copy is what <see cref="WoodBench"/>'s docstring
/// refuses in the same words: a bench that reimplements the thing it is judging
/// is a bench measuring itself, and the two copies agree only until the first
/// edit lands in one of them. So the harness and the bench call the same object
/// and the ordering below is the only ordering there is.
///
/// <b>What it is not is a second scene.</b> It owns no nodes, builds no board
/// and reads no flags. The board, the switches and the tank list are handed in
/// as fields, which is the same arrangement <see cref="Grove"/> and
/// <see cref="Stage3D"/> already use: the caller says what the world is, and
/// this says what a frame does to a tank standing in it.
///
/// <b>The world is pushed every frame; the switches live here.</b> Which board,
/// which tanks and which of them is driven are the caller's and change under it,
/// so a root reaching for this and saying what the world is are one act. The
/// switches are not that: each is set once by a key, a flag or a panel row and
/// read until the hand moves again, so they are declared here and written in
/// place. They were mirrored on every root and copied across in <see cref="Run"/>'s caller
/// before, which cost twenty-four declarations for twelve facts and carried the
/// one risk that arrangement has - that a new switch is added to the mirror and
/// not to the push.
///
/// <b>Gunnery is the harness's and is not here.</b> It is about two tanks -
/// which lane, whose armour, whose shell in the air - and this is about one,
/// so a bench with a single tank would carry it and never run it. It still has
/// to run <i>where</i> it ran, between the scan and the shot, or a round fired
/// this frame would be drawn with last frame's flash: hence <see cref="Aim"/>,
/// a hook rather than a call. The harness fills it in; the bench leaves it
/// null.
/// </summary>
public sealed class TankTick
{
    // --- the board it ticks on ----------------------------------------------

    /// <summary>The grid every distance here is measured on.</summary>
    public HexField Field = null!;

    /// <summary>Where that grid sits on the canvas. The tanks are drawn in the
    /// same space, so most of what follows is a point plus this.</summary>
    public Vector2 Origin;

    /// <summary>What the ground is made of, for the ride - see
    /// <see cref="TerrainSet.RideOf"/>. Null is a board with no plates loaded,
    /// which rides as though every cell were the smooth one.</summary>
    public TerrainSet? Terrain;

    /// <summary>The ruts. Null on a bench that does not lay them.</summary>
    public TrackMarks? Marks;

    /// <summary>The wood, which answers a shot and a hit by flinching. Null on
    /// a board with no props.</summary>
    public Grove? Wood;

    /// <summary>
    /// Cells that stop a line across the board besides the tanks - today the
    /// props that are solid, which is the walls.
    ///
    /// <b>The world's, so it is pushed with the rest of it</b>, and a set rather
    /// than a list of props because that is the only thing <see cref="Track"/>
    /// asks: which cells are not to be crossed. Which keeps the walk static and
    /// assertable without a scene - the reason it was shaped that way in the
    /// first place.
    ///
    /// <b>The aiming ray gets it for nothing</b>, because the ray walks the same
    /// <see cref="Reach"/>: a sighting line that runs through a wall the round
    /// stops at would be the one number this project keeps refusing to hold in
    /// two places.
    /// </summary>
    public IReadOnlySet<Vector2I> Obstacles = new HashSet<Vector2I>();

    /// <summary>Whether what stands on a cell stops a round <b>leaving</b> it in a
    /// given direction - the one question <see cref="Obstacles"/> cannot be asked,
    /// because it is a set of cells and this is about the rim of one.
    ///
    /// <b>A hook rather than a second set, and unanswered by default.</b> Every
    /// other solid thing on this board occupies a cell, so the walk skipping the
    /// cell it fires from is right: a tank does not shoot itself and the ground
    /// under a gun does not block it. A wall is the exception - it stands on the
    /// <i>edges</i> of its cell - and a board that has none answers null and gets
    /// exactly the walk it had.
    ///
    /// The direction is passed because the answer depends on it: a wall covering
    /// one edge bars one way out and no other, and a ring that has had a side
    /// driven through bars five.</summary>
    public Func<Vector2I, Vector2, bool>? Barred;

    /// <summary>Whether this tank is pressing against masonry right now, and so
    /// may only go at <see cref="MovementProfile.WallSpeed"/> -
    /// see <see cref="SpeedCap"/>.
    ///
    /// <b>A hook because a wall is a prop and the other two caps are terrain.</b>
    /// Grade and water are asked of <see cref="Field"/>, which knows every cell of
    /// itself; what stands on a cell it does not know at all, and a board with no
    /// walls answers null and gets exactly the cap it had.
    ///
    /// <b>A predicate rather than a number, exactly as <c>HexField.IsGrade</c> and
    /// <c>IsWet</c> are.</b> How heavy the going is belongs beside the other two
    /// fractions, where it can be read against them; all that is asked here is
    /// whether it is in force. What answers it is a count of pieces the solver has
    /// the nose against - <c>WallRig.Shoving</c> - so the drag ends when the
    /// shoving does rather than when a timer says so.</summary>
    public Func<Vehicle, bool>? Shoving;

    /// <summary>The view's spring. A gun going off shoves it - see
    /// <see cref="Fire"/> - and a bench without one simply does not shake.
    /// </summary>
    public CameraShake? Shake;

    /// <summary>Every tank on the board. Read for two things only: clearing a
    /// dead tank out of everyone's sights, and nothing else - so a bench with
    /// one tank hands in a list of one rather than nothing.</summary>
    public IReadOnlyList<Vehicle> Vehicles = Array.Empty<Vehicle>();

    /// <summary>The one being driven, or null. It decides two things and both
    /// are about the board rather than the tank: whose route is highlighted,
    /// and whose scan the hand-driven turret suspends.</summary>
    public Vehicle? Driven;

    /// <summary>What a whole screen pixel is worth to a sprite - the camera's
    /// zoom. Pushed rather than read, because a sprite has no business reading
    /// the camera and the jolt has to be whole against the screen.</summary>
    public float ViewZoom = 1.0f;

    /// <summary>Whether the stage owns the ground. The rumble hands it to the
    /// sprite, which snaps its heave in a different space in each mode.
    /// </summary>
    public bool Staged;

    /// <summary>
    /// Where a round's node is hung, or null on a board that does not want the
    /// tracer drawn.
    ///
    /// <b>The board and never the tank.</b> A shell must not ride the hull that
    /// fired it, and on the staged board the sprite is reparented into a render
    /// target of its own - a tracer parented there is drawn inside the tank's
    /// picture instead of on the field, which is a bug this project has already
    /// paid for once. See <see cref="Vehicle.Rounds"/>.
    /// </summary>
    public Node2D? Deck;

    /// <summary>
    /// Who the round leaving this gun is for, when the caller knows - true if it
    /// launched one, false to let the shot go at the ground.
    ///
    /// <b>A hook for the same reason <see cref="Aim"/> is one.</b> Which tank a
    /// round is for is about two tanks: whose lane, whose armour, whether
    /// somebody is in the way. That is the harness's and stays there. What the
    /// round then does - leave, cross, land - is about one tank and is here, so a
    /// bench with a single tank gets a whole shot out of <see cref="Fire"/>
    /// rather than a flash.
    ///
    /// Null is the bench: nobody to hit, so every shot goes into the field.
    /// </summary>
    public Func<Vehicle, bool>? Launch;

    // --- the switches ------------------------------------------------------

    /// <summary>
    /// Which of a tank's effects are running. One copy, here, and the key, the
    /// flag and the panel row of whichever root is up write straight onto it.
    ///
    /// <b>Each root used to keep a private mirror of all twelve and copy them
    /// across in Bind.</b> Twenty-four declarations and twenty-four assignments
    /// for twelve facts, and the arrangement's own docstring named what it cost:
    /// "pushing costs the risk that one is forgotten". There is nothing left to
    /// forget - a switch nobody pushes is a switch that was never anywhere else.
    ///
    /// <b>Bind still pushes the world</b>, because the world is genuinely the
    /// caller's: which board, which tanks, which of them is driven. That is the
    /// half where reaching for the tick and saying what the world is have to be
    /// one act. A dial is not that - it is set once by a hand and read until the
    /// hand moves again.
    ///
    /// The defaults are the effects' own, named where the effect names them.
    /// Where a root disagrees it says so at its own <c>TankTick</c> - the harness
    /// opens with the pitch off, because there it is a thing to switch on and
    /// look at rather than the subject.
    /// </summary>
    public bool PitchEnabled = true;

    /// <summary>Which calibre is loaded, as an index into
    /// <see cref="Ordnance"/>.
    ///
    /// A dial like the twelve above it, and here rather than on a root because
    /// both roots had one: it is what the gun is loaded with, and the shot is
    /// what this object now owns end to end.</summary>
    public int Calibre = 1;

    /// <summary>
    /// What the gun is loaded with: a shell that bursts, or one that goes
    /// through.
    ///
    /// A dial like the calibre beside it, and here for the same reason - it is
    /// what the gun holds, and the shot is what this object owns end to end. It
    /// travels with the round at the trigger (<see cref="Shell.Ammo"/>): turning
    /// the dial while a round is in the air must not change what arrives.
    ///
    /// <b>Against armour it means nothing yet, and that is named rather than
    /// done.</b> <see cref="Gunnery.Penetration"/> is a class of gun against a
    /// class of armour; writing HE and AP into it is a change to what the bench
    /// <i>does</i> rather than to what it draws, and it is a separate piece of
    /// work. What it means today is what a wall makes of it.
    /// </summary>
    public Shell.Kind Ammo = Shell.Kind.He;

    /// <summary>Which side of the hex the next hand-dealt hit comes from, as an
    /// index into <see cref="HexField.EdgeHeadings"/>.
    ///
    /// A dial of the shot like the calibre beside it, and here for the same
    /// reason: both roots kept one. It says which side the <em>next</em> manual
    /// hit arrives on - a shell from another tank must not write it, or the
    /// control becomes a report of what happened instead of a control.
    ///
    /// Opens on the last side so the U key steps to the first.</summary>
    public int HitSide = HexField.EdgeHeadings.Length - 1;

    /// <summary>The heading <see cref="HitSide"/> names.</summary>
    public double HitFrom => HexField.EdgeHeadings[HitSide];

    /// <summary>
    /// Which class of gun the next hand-dealt hit comes out of, as an index into
    /// <see cref="MovementProfile.Tags"/>, or <see cref="NoShooter"/> for a round
    /// with nobody behind it.
    ///
    /// <b>The dial that makes a keypress mean the same thing as a shot.</b>
    /// <see cref="Gunnery.Penetration"/> is a class of gun against a class of
    /// armour, and a fired round has both ends of that - so a bounce or a
    /// penetration follows the matchup table by construction, and has since the
    /// table was written. A keypress had only one end: no shooter, so no
    /// matchup, so the depth came from <see cref="Calibre"/>'s bite instead.
    /// Which means the manual hit could not show a ricochet at all without the
    /// calibre being dropped to its smallest, and then only on a clean plate -
    /// an accident of <see cref="Ordnance.BiteFor"/> rather than a control.
    ///
    /// <b>Both paths are kept, and they answer different questions.</b> The bite
    /// walks one plate through scorch, gouge and breach over three presses,
    /// which is what shows that the scar's phase axis is damage rather than
    /// three renders of one drawing - see <see cref="TankSprite.Damage"/>. This
    /// one reproduces one cell of the matchup: a light gun into this hull, over
    /// and over, stays a ricochet however many times it lands.
    ///
    /// <b>Opens at <see cref="NoShooter"/></b>, because that is what the U key
    /// has always done and a default that moved would move every capture of it.
    /// A dial here rather than on a root for <see cref="Calibre"/>'s reason: it
    /// already has two readers.
    /// </summary>
    public int HitBy = NoShooter;

    /// <summary>No gun behind the next manual hit - the bite decides how deep it
    /// gets. Named rather than left as a bare -1, because it is a state with a
    /// meaning rather than a missing value: it is the one round on this bench
    /// that nobody fired.</summary>
    public const int NoShooter = -1;

    /// <summary>The class <see cref="HitBy"/> names, or null with no shooter.
    /// </summary>
    public MovementProfile? HitGun =>
        HitBy >= 0 && HitBy < MovementProfile.All.Length
            ? MovementProfile.All[HitBy] : null;

    /// <summary>
    /// How deep the next manual hit may get into <paramref name="victim"/>, or
    /// null for a round with no ceiling.
    ///
    /// <b>One method because it is read in two places and they must not
    /// disagree</b>: <see cref="TakeHit"/> spends it, and both roots' panels
    /// report it so the dial can say what it will do before it does it. A
    /// readout that re-derived the matchup would agree with the shot until
    /// somebody changed the table.
    /// </summary>
    public int? HitDepth(Vehicle victim) =>
        HitGun is MovementProfile gun
            ? Gunnery.Penetration(gun, victim.Profile) : null;

    /// <summary>Whether a round in the air is drawn. The flight is not on a
    /// switch - it is what puts time between the report and the impact - so this
    /// is the drawing alone, and it reaches the rounds already up: see
    /// <see cref="ShowRounds"/>.</summary>
    public bool TracerVisible = Shell.TracerOnByDefault;

    public bool RumbleEnabled;
    public bool TracksEnabled = true;
    public bool TrembleEnabled = true;
    public double TrembleLevel = 1.0;
    public bool ExhaustEnabled = true;
    public double ExhaustLevel = 1.0;
    public bool ExhaustRamp = !ExhaustLoop.BinaryByDefault;
    public bool ScanEnabled;
    public bool RecoilTube = true;
    public bool RecoilShear = Recoil.ShearOnByDefault;

    /// <summary>Whether the smoke column is built rather than read off the
    /// atlas - see <see cref="ProcSmoke"/>. On the tick and not on a tank
    /// because it is a question about the effect, and answered on one tank
    /// while two others burn the other way it destroys the comparison it exists
    /// for. Off by default, and named there rather than here: the rendered
    /// column is what every shipped set draws, and the A/B is what makes
    /// "built reads better" an assertion instead of a memory.</summary>
    /// <summary>Whether the engine's plume is built rather than read - see
    /// <see cref="ProcSmoke.Plume"/>. Its own switch beside the column's and the
    /// flame's, because the three are separate effects and the whole use of the
    /// switches is putting one built half beside two read ones.</summary>
    public bool ProceduralExhaust = ProcSmoke.OnByDefault;

    public bool ProceduralSmoke = ProcSmoke.OnByDefault;

    /// <summary>The flame built rather than read - see <see cref="ProcFire"/>.
    /// Its own switch for the column's reason: the two halves are judged
    /// apart.</summary>
    public bool ProceduralFire = ProcFire.OnByDefault;
    public bool ShakeOn = CameraShake.OnByDefault;

    // --- what it cannot answer for itself -----------------------------------

    /// <summary>Whether this tank's turret is being driven by hand right now -
    /// the spin key, or the mouse. It suspends the scan, and it is asked rather
    /// than held because both of those are the harness's own controls and a
    /// bench may have neither. Null answers no.</summary>
    public Func<Vehicle, bool>? TurretHeld;

    /// <summary>Gunnery, run between the scan and the shot because that is
    /// where it ran. See the class summary: it belongs to a board with two
    /// tanks on it, and a bench with one leaves it null.</summary>
    public Action<Vehicle, double>? Aim;

    /// <summary>
    /// What a round with no tank in front of it hit, once it gets there.
    ///
    /// The third hook, on <see cref="Aim"/>'s and <see cref="Launch"/>'s model
    /// and for their reason: what a shell does to a prop is a statement about the
    /// board, and what a shell <i>is</i> is a statement about one tank. Null
    /// leaves the shot going into the field exactly as it did - see
    /// <see cref="Strike"/>.
    ///
    /// The round carries what it hit (<see cref="Shell.Blocked"/>) rather than
    /// being asked where it landed, because the landing point is deliberately
    /// short of the cell's edge - see <see cref="Track"/>.
    /// </summary>
    public Action<Shell>? Landed;

    /// <summary>
    /// What the board makes of the gun going off on it: the dust the muzzle
    /// blast blows off the ground - <see cref="ProcKick"/>.
    ///
    /// <b><see cref="Landed"/>'s twin at the other end of the shot, and a
    /// separate hook rather than a second thing done inside it.</b> A round
    /// arriving and a round leaving are two events on two triggers, and only one
    /// of them happens for certain: a gun laid on nobody still kicks its own
    /// dust, and a round that hits a tank never reaches Landed at all.
    ///
    /// Answered by whichever root draws a board that can hold a cloud, and
    /// unanswered on the flat one for <see cref="Landed"/>'s reason - the effect
    /// is a quad in the 3D world and the legacy board has nowhere to put it.
    ///
    /// The direction is the gun's own ground direction, unnormalised, so the
    /// receiver gets both which way and how much of it survived the projection -
    /// the same number the camera shake takes off this trigger.
    /// </summary>
    public Action<Vehicle, Vector2>? Kicked;

    /// <summary>
    /// A round that struck armour and did not get through - <see cref="ProcSpall"/>.
    ///
    /// <b>Fired instead of the rendered burst rather than beside it</b>, which is
    /// the whole reason it exists: see <see cref="Land"/>. A ricochet and a
    /// penetration were one picture, and everything else on this bench already
    /// told them apart.
    ///
    /// Handed the measurement rather than the plate it happened on, on
    /// <see cref="Kicked"/>'s model: the point of impact in the victim's own
    /// frame, the reflected direction unnormalised, and which side of the hull it
    /// is on. All three come out of <see cref="Vehicle.Graze"/> and
    /// <see cref="Vehicle.Turned"/> in one place, so the two roots that answer
    /// this are one line each and neither of them re-measures a plate.
    ///
    /// Answered by whichever root draws a board that can hold the quad, and
    /// unanswered on the flat one for <see cref="Landed"/>'s reason. Unanswered,
    /// a bounce draws nothing at all - which is deliberate rather than a
    /// fallback: the alternative is a hook whose absence quietly restores the
    /// picture it was written to replace, and then <c>--spall off</c> and "no
    /// board for it" become the same state with two causes.
    /// </summary>
    public Action<Vehicle, Vector2, Vector2, bool>? Bounced;

    /// <summary>
    /// Whether a round that fails to penetrate draws the ricochet at all.
    ///
    /// <b>The A/B, and the only one this layer can have.</b> The built ricochet
    /// stands on the board and the rendered burst stands in the tank's canvas, so
    /// unlike the muzzle flash's three sources they cannot be swapped in one
    /// frame and looked at side by side; off, a bounce draws the rendered pair it
    /// always did. <c>--spall off</c> on both roots, and no panel row - see
    /// <c>Stage3D.Blast</c> on why a flag with nothing to overrule stays out of
    /// <c>FlagRows</c>.
    ///
    /// Here rather than on a root, for the reason <see cref="Calibre"/> and
    /// <see cref="HitSide"/> are here: a dial written in two places agrees until
    /// the first edit lands in one of them, and this one already has two readers.
    /// </summary>
    public bool Bounce = true;

    // --- the frame -----------------------------------------------------------

    /// <summary>
    /// One tank, one frame, in the order the harness ran these in.
    ///
    /// <b>The order is the contract.</b> The lean is read after the drive
    /// because it reads the position the drive left; the belts are wound from
    /// the travel the same drive reported and the ruts are laid from that same
    /// pair, so that a mark and a track phase cannot disagree about how far the
    /// ground went past; the shot is advanced after the gunnery so a round
    /// fired this frame shows its first flash on this frame rather than the
    /// next.
    /// </summary>
    public void Run(Vehicle v, double delta)
    {
        if (v.Moving)
            AdvanceOrder(v, delta);
        else if (v.Pitch.Angle != 0.0 || v.Speed != 0.0 || v.Sprite.Shake != 0.0)
        {
            // Standing still is not the same as having always been standing:
            // the pitch spring is still ringing down and the last jolt is still
            // on screen, and both have to be run to nothing rather than left.
            v.Speed = 0.0;
            UpdatePitch(v, 0.0, delta);
            UpdateRumble(v, delta);
            v.Sprite.QueueRedraw();
        }

        UpdateLean(v, delta);

        (double Left, double Right) belts = BeltTravel(v, delta);
        UpdateTracks(v, belts, delta);
        Marks?.Lay(v, belts, delta);
        UpdateTremble(v, delta);
        UpdateExhaust(v, delta);
        UpdateWreck(v, delta);
        UpdateBurn(v, delta);
        UpdateScan(v, delta);
        Aim?.Invoke(v, delta);
        UpdateShot(v, delta);
        UpdateHit(v, delta);
        v.Audio?.Update(v, delta);
    }

    /// <summary>Put a tank on its cell.
    ///
    /// The z index comes off the cell's screen height, which is the whole of the
    /// overlap rule: a tank further down the screen is nearer the camera and draws
    /// over one behind it. Tree order cannot say that - it is fixed at build time
    /// and the tanks move - and without it the tank that happens to have been
    /// created last wins every overlap, which reads as one driving through
    /// another.</summary>
    /// <summary>
    /// Where a tank's sprite has to sit for the tank to be standing on a cell.
    ///
    /// On its <em>contact patch</em>, not on its anchor, and that distinction is
    /// worth the method. Every layer of one tank shares the anchor, so drawing a
    /// tank at its cell's anchor is what keeps hull, turret and belts together -
    /// but the anchor is the turret axis lifted to the mid-height of the fitted
    /// bounds, so it floats 50 to 59 px above the ground. Two things then move the
    /// tank off the ground the field is drawing, and both were introduced here
    /// rather than by the renderer, which centres tile and footprint to within
    /// two pixels:
    ///
    /// - the size scales about the anchor, so a tank drawn at 0.85 has that 51px
    ///   of float shortened to 43 and stands 8px high in its hex, and a 1.15 one
    ///   sinks 8px into it;
    /// - the field draws one tile under all three, and the tiles do not agree on
    ///   where the ground is: 49.7px below the anchor on the light's, 59.0 on the
    ///   medium's, which is another 9px.
    ///
    /// They happened to cancel on the heavy and to add on the light, so the tank
    /// that looked wrong was the one where the two errors agreed - which is why
    /// this reads as "sometimes off centre" rather than as an offset.
    ///
    /// Asking instead for the ground point to land on the cell's ground centre
    /// answers both at once, at any size and with any tile under it.
    /// </summary>
    public Vector2 StandOn(Vehicle vehicle, Vector2I cell) =>
        Origin + Field.CellAnchor(cell) + Field.CentreOffset
        - vehicle.Atlas.GroundOffset * vehicle.Sprite.BodyScale;

    /// <summary>
    /// The same place part way from one cell to the next: the contact point on the
    /// <b>ground</b> between them.
    ///
    /// <b>It replaces driving along the straight line between the two anchors, and
    /// the belt marks are what proved that line wrong.</b> The straight line is the
    /// chord of <see cref="HexField.HeightBetween"/>, and a ramp's surface is not a
    /// chord - it runs centre to shared edge to centre, a quarter of a level above
    /// the chord at the boundary (see <see cref="HexField.SurfaceBetween"/>, which
    /// already carried this for anything lying in the ground). The chord was kept
    /// because for a billboard it looked like a fine approximation: the error is
    /// vertical, and a card drawn 16px low still stands on its hex.
    ///
    /// <b>It is not vertical once the leg is diagonal, and that is the measurement
    /// that retired it.</b> A screen row is a place on the ground, so 16.19px of
    /// row is 32px of ground along the view. On a leg straight up or down the screen
    /// that slides the tank along its own path and shows as nothing; on a diagonal
    /// leg the shared edge is a slanted line, so the same error slides the crossing
    /// <i>along that edge</i> - measured on (9,2)->(10,3) of this board, 21.6px off
    /// the edge's midpoint, 52% of the way to its corner. The tank drove into the
    /// hex through the wrong part of its face, and the ruts, laid at the contact
    /// point, drew it.
    ///
    /// The two ends are untouched: <see cref="HexField.SurfaceBetween"/> is
    /// <c>TopAt</c> at nought and at one, so this is <see cref="StandOn"/> there and
    /// arriving still parks on the same pixel it always did.
    /// </summary>
    public Vector2 StandBetween(Vehicle vehicle, Vector2I from, Vector2I onto,
                                 float done) =>
        Origin + Footing.GroundBetween(Field, from, onto, done)
        - vehicle.Atlas.GroundOffset * vehicle.Sprite.BodyScale;

    /// <param name="placed">Whether the tank is being <i>put</i> here rather than
    /// having driven here. It decides one thing - whether the body is sat on the
    /// face it stands on or left to the spring - and getting it wrong is loud: a
    /// cell is parked on at the end of <b>every</b> leg, so sitting the body on
    /// arrival snapped a tank cresting a ramp flat in one frame, and the whole
    /// point of <see cref="ClimbLean"/> is that it does not do that. Placing is
    /// start-up, a reset, and the key that drops a tank on a cell; those have no
    /// motion to preserve and want the lean already right on the first frame.
    /// </param>
    public void Park(Vehicle vehicle, bool placed = true)
    {
        vehicle.Cell = Field.ClampCell(vehicle.Cell);
        Field.Position = Origin;
        // The surface, not the floor: the two are half a level apart on a ramp,
        // and this has to agree with the drawn position - HexField.CellAnchor is
        // lifted off the same number, and Depth subtracts this one back out of
        // that one to get the flat row. Written as the level's lift, the depth
        // term and the drawn row disagreed by half a lift on every ramp cell.
        vehicle.Height = Field.TopAt(vehicle.Cell);
        vehicle.Standing = vehicle.Height;
        // A tank standing on a cell stands at its centre, and there the chord and
        // the surface are the same point - but the clearance is still owed, because
        // marks are laid on the frame a leg ends too. See HexField.MarkAt.
        vehicle.Ground = Field.MarkAt(vehicle.Cell);
        // Not cleared but asked, for the reason OnSlope below is: parking is where
        // a tank comes to rest in a ford, and that is the case this exists for.
        vehicle.Waterline = Field.IsWater(vehicle.Cell)
            ? Field.WaterTop(vehicle.Cell) : float.NegativeInfinity;
        vehicle.Trailing = vehicle.Height;
        vehicle.Travel = Vector2.Zero;
        vehicle.Levelling = false;
        // Not cleared but asked: parking is where a tank comes to rest on a ramp,
        // and that is the case this exists for.
        vehicle.OnSlope = Field.IsRamp(vehicle.Cell);
        vehicle.Sprite.Position = StandOn(vehicle, vehicle.Cell);
        // Cleared before the lean is asked: the leg that just ended left this at
        // one, and on the cell arrived at the two faces being mixed are the same
        // one anyway - but the next leg's first frame must not begin already
        // crossed.
        vehicle.LegBlend = 0.0f;
        // And the leg's own progress, for the same reason and one more: it is what
        // the position is now derived from, so a leg that began with the previous
        // one's progress would put the tank part way along itself on its first
        // frame - see StandBetween.
        vehicle.LegDone = 0.0f;
        // Sat on the face it is standing on rather than sprung up to it, but only
        // for a tank being put here - see the parameter. Asked after the position,
        // because SurfaceGrade reads the heading off the sprite.
        if (placed)
        {
            vehicle.Lean.Reset(SurfaceSlope(vehicle));
            vehicle.Sprite.Climb =
                vehicle.Lean.Angle(vehicle.Sprite.HullFacing, Field.RiseFactor);
            vehicle.Sprite.Slope = ClimbLean.Print(vehicle.Lean.Slope,
                                                   Field.Squash,
                                                   Field.RiseFactor);
        }
        // Moved rather than driven, so the ribbon must break here or the jump is
        // drawn as a line across the board - and only then, which is the same
        // question the parameter above already answers. A cell is parked on at the
        // end of *every* leg, so lifting the pen unconditionally broke the ribbon
        // at every cell boundary of an ordinary drive: with the belt-smoothing
        // window as long as a run, each cell's worth of trail collapsed towards its
        // own middle and a four-cell route was drawn as four blobs. Measured on the
        // 3D board, where the marks are geometry and the collapse is plain: 1081
        // rut pixels in two patches 52px apart against 3476 over the whole 186px
        // of the route. The old peak was the darker of the two (67 against 34)
        // because a collapsed run draws its every segment on top of itself.
        if (placed)
            Marks?.Lift(vehicle);
        Depth(vehicle);
        // Belt travel is read back off the heading rather than reported by
        // whatever turned it, so the baseline has to be laid down wherever the
        // heading is set from outside. Left stale, the first frame after a reset
        // sees the whole of the difference as one frame's swing and the belts
        // jump.
        vehicle.LastHullFacing = vehicle.Sprite.HullFacing;
        // Same baseline, same reason, one line down: the ring's rate is read back
        // off the angle, so a reset that moved the turret and left this stale
        // would report the whole move as one frame's traverse and blip the motor.
        vehicle.LastTurretOffset =
            Angles.WrapAngle(vehicle.Sprite.TurretFacing - vehicle.Sprite.HullFacing);
        // And the third baseline, for the third thing read back rather than
        // announced. Left stale, a tank put back on its home cell would count
        // the whole jump as one frame's travel and shoulder the wood aside on
        // the frame after a reset.
        vehicle.LastGroundPoint = vehicle.GroundPoint - Origin;
        vehicle.Sprite.QueueRedraw();
    }

    /// <summary>The overlap order, off where the tank actually is rather than off
    /// the cell it last reached. Taken from the live position because a tank spends
    /// most of a move between two cells: stamped on arrival only, one crossing the
    /// row of another would hold the wrong order for the whole step, which is
    /// exactly the frame you would be looking at.
    ///
    /// From the contact patch, not from the sprite's origin, for the same reason
    /// <see cref="StandOn"/> exists: what decides which tank is nearer the camera
    /// is where each one stands, and the origin sits a scaled float above that. Off
    /// the origin, three tanks on one row got three different depths - the light by
    /// 16 - and the order between them was decided by their sizes.</summary>
    public void Depth(Vehicle vehicle)
    {
        // The row it would stand on with the board flattened. The position
        // already carries the lift - CellAnchor is lifted - so putting it back is
        // how the two terms of the depth get separated again, and there is only
        // ever one place the height is subtracted.
        float row = vehicle.GroundPoint.Y - Origin.Y + vehicle.Height;
        vehicle.Sprite.ZIndex = Mathf.RoundToInt(Field.Depth(row, vehicle.Height));
        if (vehicle.Cap is null)
            return;
        vehicle.Cap.FlatRow = row;
        vehicle.Cap.Standing = vehicle.Height;
        vehicle.Cap.Box = TankBox(vehicle);
        vehicle.Cap.QueueRedraw();
    }

    /// <summary>What a tank occupies, in the field's local space, for the one
    /// question that needs it: which cells could hide it. The frame its layers
    /// are drawn from, at the class's size, about the anchor - so it is wider than
    /// the tank and narrower than the gap between two columns of the grid, which
    /// is the property that matters. Exact would mean the union of eight layers,
    /// re-measured every frame, to decide whether to repaint a cell that is beside
    /// the tank rather than on it.</summary>
    public Rect2 TankBox(Vehicle vehicle)
    {
        Vector2 size = (Vector2)vehicle.Atlas.Tile * vehicle.Sprite.BodyScale;
        Vector2 at = vehicle.Sprite.Position - Origin;
        return new Rect2(at - size * 0.5f, size);
    }

    /// <summary>
    /// Roughly half the track gauge, as a fraction of the hull's broadside span.
    ///
    /// The fallback now, and only for a tank with no belt layers to measure -
    /// see <see cref="AtlasSet.TrackArm"/>. It was the answer, and it was wrong
    /// by 12-32%: <see cref="AtlasSet.HullSpan"/> is the widest frame, which is
    /// the hull's *length*, and the three hulls are one length to within 3%
    /// while their gauges range over a fifth. Kept because a tank without belts
    /// still turns, and named rather than buried so it can be argued with.
    /// </summary>
    private const double PivotRadiusFraction = 0.25;

    /// <summary>
    /// Ground the belts covered this frame: what the tank drove, plus what it
    /// swung.
    ///
    /// The second half was missing and it showed. A tank pivoting from a standing
    /// start has <c>Speed</c> of zero - the cornering crawl is a floor, not a
    /// target - and so did the belts, so the hull swung round on tracks that were
    /// not moving. Turning on the spot is the most track-heavy thing a tank does
    /// and it was the one case with no belt motion at all.
    ///
    /// One number rather than a separate path for the sound, which is the whole
    /// reason it is fixed here: <see cref="TrackLoop"/> takes the travel, the
    /// sprite takes its phase from that and <see cref="VehicleAudio"/> takes its
    /// rate from the same clock, so the belts cannot be heard turning while they
    /// are seen standing still.
    ///
    /// Signed, and per side, which is the debt this used to carry openly. A real
    /// pivot runs the two belts in opposite directions; one unsigned number for
    /// both wound them the same way, so one of the two was always climbing its
    /// loop backwards. The fix is arithmetic rather than a case: a belt at
    /// <c>arm</c> from the centre covers <c>drive + omega*arm</c>, and the two
    /// sides differ only in the sign of <c>arm</c>.
    ///
    /// It was the ruts that forced it. A spinning belt only suggests a direction
    /// and the eye forgives it; a mark left on the ground records one, and two
    /// arcs curling the same way where they should be counter-rotating is the
    /// first thing anyone would see.
    ///
    /// The radius is <see cref="AtlasSet.TrackArm"/>, measured off those two
    /// layers, and only falls back to a fraction of the hull when there are no
    /// belts to measure. The fraction was wrong by 12-32%, worst on the heavy,
    /// and wrong in a way it could not have been right: the three hulls are one
    /// length to within 3% and the three gauges are not.
    /// </summary>
    private (double Left, double Right) BeltTravel(Vehicle v, double delta)
    {
        double swing = Angles.WrapAngle(v.Sprite.HullFacing - v.LastHullFacing);
        v.LastHullFacing = v.Sprite.HullFacing;
        double radius = PivotArm(v);
        return TrackLoop.Split(v.Speed * delta, swing, radius);
    }

    /// <summary>The radius each belt winds about when the hull turns - half the
    /// gauge, on screen.</summary>
    private static double PivotArm(Vehicle v) =>
        (v.Atlas.TrackArm > 0.0
            ? v.Atlas.TrackArm
            : v.Atlas.HullSpan * PivotRadiusFraction) * v.Sprite.BodyScale;

    public void CancelOrder(Vehicle v)
    {
        v.Path = new List<Vector2I>();
        v.PathStep = 0;
        v.Speed = 0.0;
        if (v != Driven)
            return;
        Field.Highlight = Array.Empty<Vector2I>();
        Field.QueueRedraw();
    }

    /// <summary>Distance left in the current straight run, and the speed to be
    /// doing by the end of it.
    ///
    /// The run stops at the first bend on purpose: the tank has to slow there
    /// to swing round, so looking past the bend would start the braking too
    /// late. What it slows *to* depends on what the bend is: the crawl at a
    /// corner, a full stop only at the destination.</summary>
    private (double Distance, double EndSpeed) RemainingRun(Vehicle v)
    {
        double total = (StandOn(v, v.Path[v.PathStep]) - v.Sprite.Position).Length();
        int heading = HexField.HeadingTo(v.Cell, v.Path[v.PathStep]);
        int i = v.PathStep;
        for (; i + 1 < v.Path.Count; i++)
        {
            if (HexField.HeadingTo(v.Path[i], v.Path[i + 1]) != heading)
                break;
            total += (Field.CellAnchor(v.Path[i + 1])
                      - Field.CellAnchor(v.Path[i])).Length();
        }
        bool endOfPath = i + 1 >= v.Path.Count;
        return (total, endOfPath ? 0.0 : v.Profile.CornerSpeed);
    }

    /// <summary>The fastest this tank may cruise at where it is now: its class
    /// figure on the level, two thirds of it going up or down a step, 45% of it
    /// through water - see <see cref="MovementProfile.GradeFraction"/> and
    /// <see cref="MovementProfile.WaterFraction"/>.
    ///
    /// A method rather than the expression inlined at the one place it drives the
    /// speed, because the trace and the panel have to be able to say which of the
    /// two is in force. A tank crawling up a bank and a tank whose order has gone
    /// stale are the same picture, and the cap is the only number that tells them
    /// apart.
    ///
    /// <b>The lower of the two where both apply, and never their product.</b> A
    /// leg is a slope or it is not and it is wet or it is not; multiplying would
    /// invent a third terrain nothing on the board is made of, and the one place
    /// the two meet on this map - the beach at the mouth of the pit - is exactly
    /// where that invention would show.
    ///
    /// <b>And a third that is not terrain: masonry the tank is shoving</b>, at
    /// <see cref="MovementProfile.WallSpeed"/> - see <see cref="Shoving"/>.
    /// It arrives through a hook because a wall stands on a cell rather than being
    /// one, and it takes the same lower-of-them treatment for the same reason.
    /// The ramp in <see cref="AdvanceOrder"/> is what turns it into an effect: a
    /// ceiling that drops when the nose meets the leaf and comes back up when the
    /// leaf is behind is a tank slowing into the wall at its own retardation and
    /// pulling away again at its own acceleration, which is what pushing through
    /// something looks like.</summary>
    public double SpeedCap(Vehicle v)
    {
        double cap = v.Profile.TopSpeed;
        if (!v.Moving)
            return cap;
        Vector2I next = v.Path[v.PathStep];
        if (Field.IsGrade(v.Cell, next))
            cap = Math.Min(cap, v.Profile.GradeSpeed);
        if (Field.IsWet(v.Cell, next))
            cap = Math.Min(cap, v.Profile.WaterSpeed);
        // And what the cell itself is worth - the floor and what stands on it,
        // out of terrain.json. A fraction of the class's own top speed rather
        // than a figure, because the terrain is the same terrain for every
        // class and only the tank differs. One everywhere on today's boards, so
        // this line changes no measurement that was taken before it.
        float ground = TerrainRules.Speed(Field.FaceAt(next));
        if (ground < 1.0f)
            cap = Math.Min(cap, v.Profile.TopSpeed * ground);
        // And masonry, which is neither of those and is not the board's at all -
        // see Shoving. Lowest of the three by construction, so a tank in a wall on
        // a wet bank goes at the wall's figure; the same "the lower of them, never
        // their product" that already holds for the other two.
        if (Shoving is not null && Shoving(v))
            cap = Math.Min(cap, MovementProfile.WallSpeed);
        return cap;
    }

    /// <summary>What is holding this tank back, in one word - or in nothing at all
    /// where the answer is its own class.
    ///
    /// <b>Beside <see cref="SpeedCap"/> rather than inside the two places that
    /// print it</b>, because a cap and the reason for it are one answer: a trace
    /// reading "160" with the panel reading "on a grade" while the tank is in the
    /// water is two statements about one number, and the second one is the one
    /// somebody acts on.</summary>
    public string SpeedCapWhy(Vehicle v)
    {
        if (!v.Moving)
            return "";
        // Masonry first, because the word names the cap actually in force and
        // the wall's is the lowest of the three by construction - see
        // MovementProfile.WallSpeed. A tank shoving a leaf on a wet bank is
        // going at the wall's figure whatever else is true of the leg.
        if (Shoving is not null && Shoving(v))
            return "shoving";
        Vector2I next = v.Path[v.PathStep];
        bool grade = Field.IsGrade(v.Cell, next), wet = Field.IsWet(v.Cell, next);
        if (grade || wet)
            return grade && wet ? "wading a grade" : wet ? "wading" : "grade";
        // Last, because it is the loosest ceiling of the four and the word has
        // to name the cap actually in force - the same ordering the masonry has
        // at the top of this method.
        return TerrainRules.Why(Field.FaceAt(next));
    }

    private void AdvanceOrder(Vehicle v, double delta)
    {
        Vector2I next = v.Path[v.PathStep];
        int heading = HexField.HeadingTo(v.Cell, next);
        if (heading < 0)
        {
            CancelOrder(v);     // path went stale - do not drive off the grid
            return;
        }

        double diff = Angles.WrapAngle(heading - v.Sprite.HullFacing);
        double accelRatio;

        if (Math.Abs(diff) > 0.5)
        {
            // Slow to the cornering crawl and swing round while still creeping.
            // The crawl is a floor, never a target to speed up to, so a standing
            // start still pivots in place - which is what a tank does - while a
            // bend taken at speed stays continuous.
            double crawl = v.Profile.CornerSpeed;
            accelRatio = v.Speed > crawl ? -1.0 : 0.0;
            if (v.Speed > crawl)
                v.Speed = Math.Max(crawl, v.Speed - v.Profile.Accel * delta);
            double budget = v.Profile.TurnRate * delta;
            v.Sprite.TurnHull(Math.Abs(diff) <= budget
                ? diff
                : Math.Sign(diff) * budget);
        }
        else
        {
            (double remaining, double endSpeed) = RemainingRun(v);
            // A ceiling rather than a brake pedal, and the difference is the
            // whole of why a tank used to arrive still doing a third of its
            // cruise and then stop dead in one frame.
            //
            // The old form asked "is the distance left below the braking
            // distance" once a frame and then applied full retardation. The test
            // is only as fine as the step: at cruise the tank covers 4px between
            // asks, so braking began up to 4px late, and 4px of missed braking is
            // sqrt(2 * 420 * 4) = 58px/s still on the clock at the goal. Measured
            // at 72. Nothing was wrong with the arithmetic - the trigger was late,
            // and a trigger can always be late.
            //
            // This is the same curve read the other way round: the fastest this
            // tank may be going and still be able to stop in what is left. It has
            // no moment of engagement to miss, and as `remaining` runs out the
            // ceiling runs down to `endSpeed` on its own, so the deceleration
            // finishes instead of being cut off by arrival.
            double ceiling = Math.Sqrt(endSpeed * endSpeed
                                       + 2.0 * v.Profile.Accel * Math.Max(remaining, 0.0));
            // And no faster than a step allows, whichever way the step goes - see
            // SpeedCap.
            double allowed = Math.Min(SpeedCap(v), ceiling);
            double before = v.Speed;
            // How fast that ceiling may be closed on. The engine's own
            // retardation for everything the board is made of - see below - and
            // harder for the one thing it is not: masonry does not slow a tank
            // down, it is hit. See MovementProfile.WallBrake.
            double down = v.Profile.Accel
                          * (Shoving is not null && Shoving(v)
                              ? MovementProfile.WallBrake : 1.0);
            // Slowed into rather than snapped to, and the two are told apart here
            // because only one of them can be. The braking ceiling descends at the
            // tank's own retardation by construction, so clamping to it was already
            // gradual; the grade's cap appears the frame a leg onto a bank begins and
            // is 80px/s below cruise, which as a bare Min would be a stop dead in one
            // frame - eleven times what the engine can do - and would report itself
            // to the pitch as a single frame of full brake where the tank has a
            // smooth dip.
            v.Speed = v.Speed > allowed
                ? Math.Max(allowed, v.Speed - down * delta)
                : Math.Min(v.Speed + v.Profile.Accel * delta, allowed);
            // What the body actually felt, rather than a flag saying which branch
            // was taken. The pitch is driven by acceleration, and under the
            // ceiling the retardation varies instead of being all or nothing -
            // reporting -1 throughout would give the nose a constant dip where
            // the tank has a smooth one.
            accelRatio = delta > 0.0
                ? Math.Clamp((v.Speed - before) / (v.Profile.Accel * delta), -1.0, 1.0)
                : 0.0;
        }

        UpdatePitch(v, accelRatio, delta);
        UpdateRumble(v, delta);

        // The same StandOn the parking uses, or the tank would drive to where its
        // anchor belongs and stop the height of its float short of the cell.
        Vector2 goal = StandOn(v, next);
        Vector2 to = goal - v.Sprite.Position;
        var budgetPx = (float)(v.Speed * delta);
        // Along the leg rather than toward the goal, because the drawn path is the
        // ground's surface and bends at the shared edge - see StandBetween. The
        // budget is spent on the leg's flat length, which is the ground the tank
        // covers; on a leg with no ramp at either end that is the straight line the
        // march used to walk, so nothing off a ramp changes by a pixel.
        float legSpan = (Field.FlatAnchor(next.X, next.Y)
                         - Field.FlatAnchor(v.Cell.X, v.Cell.Y)).Length();
        float legStep = legSpan > 0.001f ? budgetPx / legSpan : 1.0f;
        if (v.LegDone + legStep >= 1.0f
            || (budgetPx <= 0.0f && to.Length() < 0.5f))
        {
            v.Cell = next;
            v.PathStep++;
            // Park rather than a bare Position: arriving in a cell is also where
            // the depth order changes, and a tank that drove past another without
            // its z index following would pass through it. Driven, not placed, so
            // the lean is left to its spring - see Park.
            Park(v, placed: false);
            if (!v.Moving && v == Driven)
            {
                Field.Highlight = Array.Empty<Vector2I>();
                Field.QueueRedraw();
            }
        }
        else if (budgetPx > 0.0f)
        {
            v.LegDone += legStep;
            v.Sprite.Position = StandBetween(v, v.Cell, next, v.LegDone);
            Climb(v, next);
            Depth(v);
        }
        v.Sprite.QueueRedraw();
    }

    /// <summary>
    /// The height, part way from one cell to the next.
    ///
    /// <see cref="Vehicle.Height"/> is taken from how far along the leg the tank
    /// is rather than integrated frame by frame, for the reason the belts read
    /// their travel back off the heading: a height stepped forward alongside the
    /// driving would have to be remembered by everything else that moves a tank -
    /// the W/S keys, a reset, an order cancelled mid-step. It is exact at both ends
    /// of the leg whatever happened in between.
    ///
    /// <b>It is the surface and no longer a chord of it, and it has to be the same
    /// expression <see cref="StandBetween"/> drew with.</b> Height means the lift
    /// baked into the drawn position - <see cref="Depth"/> subtracts it back out to
    /// recover the flat row, and the waterline takes its depth off it - so a height
    /// that disagreed with the position would not be an approximation, it would be
    /// a wrong answer about which row the tank is standing on. That the two used to
    /// be the same chord is why nothing noticed.
    ///
    /// <b>One height, and there were two.</b> The occlusion rule used to be asked
    /// against the higher of the two ends, so that a tank halfway up a wall was
    /// called up already - which reads well for the cell it is climbing onto and
    /// is wrong about every other cell at that level. Climbing (3,2) to (4,3) the
    /// tank was promoted to the crown at the first pixel of the leg and spent the
    /// whole climb drawn over (3,3), a cell it had not reached and was not level
    /// with. The rule is that a tank covers a hex when it has come up to that
    /// hex's level, so the height that answers it is the height it is drawn at,
    /// and the two only ever differed while that answer was wrong.
    /// </summary>
    private void Climb(Vehicle v, Vector2I next)
    {
        Vector2 from = StandOn(v, v.Cell), goal = StandOn(v, next);
        float span = (goal - from).Length();
        // The leg's own progress rather than a distance read back off the drawn
        // position: the drawn path bends at the shared edge now, so the position no
        // longer says how far along it is - see Vehicle.LegDone.
        float done = Mathf.Clamp(v.LegDone, 0.0f, 1.0f);
        // Which cell's water it is in, if any - the one its contact point is over,
        // which changes at the shared edge halfway along the leg. A body of water
        // is one flat surface, so there is nothing to interpolate: a tank is in it
        // or it is not, and the moment it becomes so is the shoreline the board
        // draws.
        //
        // Ahead of the relief gate, because a flooded board need not be a raised
        // one - and because a waterline left over from last frame is a tank still
        // wading a hundred pixels up the beach.
        Vector2I under = done >= 0.5f ? next : v.Cell;
        v.Waterline = Field.IsWater(under) ? Field.WaterTop(under)
                                            : float.NegativeInfinity;
        if (!Field.HasRelief)
            return;
        v.Height = Field.SurfaceBetween(v.Cell, next, done);
        // And the same ground with the flat mark's clearance on it - see
        // Vehicle.Ground. The two used to be a quarter of a level apart because this
        // one was the surface and that one a chord of it; now they differ by the
        // clearance alone, which is what HexField.MarkClear says it is for.
        v.Ground = Field.MarkBetween(v.Cell, next, done);
        // The heights are the step's, not the frame's - see StepHeights, which
        // carries the whole of why the two ends of one hull are two numbers.
        // The gait is how far the contact point leads the trailing end, as a
        // fraction of this leg: half a hull, in the leg's own units.
        float gait = span <= 0.001f ? 0.0f
            : v.Atlas.HullSpan * v.Sprite.BodyScale * 0.5f / span;
        (v.Standing, v.Trailing) = Footing.StepHeights(Field, v.Cell, next, done, gait,
            Footing.GearCover * v.Sprite.BodyScale);
        v.Travel = span <= 0.001f ? Vector2.Zero : (goal - from) / span;
        // How much of the hull has crossed onto the far cell's face - see
        // Vehicle.LegBlend. Nought until the nose reaches the seam at 0.5 - gait,
        // one when the tail has passed it at 0.5 + gait, so the span of the change
        // is the hull's own length in the leg's units and needs no number of its
        // own. The same gait as the two ends of the hull above, for the same
        // reason: it is where the hull is.
        v.LegBlend = gait <= 0.001f ? (done >= 0.5f ? 1.0f : 0.0f)
            : Mathf.Clamp((done - (0.5f - gait)) / (2.0f * gait), 0.0f, 1.0f);
        // The climb's split fades driving straight up or down the screen: its
        // seam is the flanker's edge, and there the mounted hex and the ground
        // being passed share the sprite's columns, so the only right split is
        // none - the crown for the whole sprite, the old behaviour. The
        // descent's seam is the leg's own rim and never degenerates, which is
        // why the fade lives here, next to the knowledge of which leg this is,
        // and not in the stage.
        v.Levelling = Field.LevelAt(next) != Field.LevelAt(v.Cell);
        v.OnSlope = Field.IsRamp(v.Cell) || Field.IsRamp(next);
        bool down = Field.LevelAt(next) < Field.LevelAt(v.Cell);
        if (!down)
        {
            v.Trailing = v.Standing
                + (v.Trailing - v.Standing) * Mathf.Abs(v.Travel.X);
            v.SeamLine = Footing.SeamLine(Field, Origin, v.Cell, next);
            return;
        }
        // A descent's seam is not pinned to the board at all - it is a wipe.
        // The board's own lines were both tried and each dumped the crown
        // side in a lump: a fragment over the plateau's top is visible at
        // exactly the crown and swallowed whole a hair under it, so whatever
        // single moment flips a region, that whole region blinks out on that
        // frame - measured on (4,3)->(4,2) three ways (six hundred to eight
        // hundred pixels in a frame or two, wherever the moment was put).
        // The only smooth transition available is spatial: a line parallel
        // to the leg's shared edge sweeping down the sprite over the second
        // half of the leg, from above everything (the whole tank still holds
        // the crown, as it did while on the top) to below everything (the
        // whole tank on the drawn ramp, which at the far anchor is exactly
        // the parked picture - so parking pops nothing). Fragments flip one
        // row at a time, and each flip is a few rows a frame.
        // Bottom first, which is why the normal is turned around: the wipe's
        // crown side (visible, still up on the top) must be the sprite's
        // upper part and the sinking nose side its lower, or the mid-hull is
        // cut at the rim while the tracks are still drawn on the plateau
        // below it - a tank torn in two, measured and seen. Swept over the
        // whole leg, because the drawn ramp sinks the sprite from the first
        // pixel and half a leg at cruise is a handful of frames.
        Vector3 axis = Footing.SeamEdge(Field, Origin, v.Cell, next);
        float reach = v.Atlas.HullSpan * v.Sprite.BodyScale;
        // The range is the rows where the two depths actually differ - from
        // just under the shadow's far edge to the top of the parked cut.
        // Wider spends sweep on rows where nothing shows and concentrates
        // the visible part of the wipe into fewer frames.
        float slide = Mathf.Lerp(0.25f * reach, -0.35f * reach, done);
        v.SeamLine = new Vector3(-axis.X, -axis.Y,
            -(axis.X * v.GroundPoint.X + axis.Y * v.GroundPoint.Y) + slide);
    }

    /// <summary>
    /// The plane under a tank, as a gradient in world ground coordinates, for
    /// <see cref="ClimbLean"/>.
    ///
    /// <b>A plane and not a grade, because two things are drawn from it and only
    /// one of them is the pitch.</b> The body takes the component along its
    /// heading; the contact shadow lies in the face and takes the whole of it,
    /// including on the headings where the component is nought. A grade along the
    /// travel cannot be turned back into a plane - divide by the cosine and it
    /// blows up in exactly the case that matters, a tank standing broadside on a
    /// face, where the travel grade is zero and the face is not.
    ///
    /// <b>Read off the face the tank is standing on, not off the ends of its
    /// leg</b>, and that is what lets a tank stopped on a ramp stay leaning. A
    /// slope is a property of the ground; asked as "how much higher is the cell I
    /// am going to", the question has no answer once the tank has arrived, so the
    /// spring levelled it out - a tank standing on a visible slope, drawn flat.
    ///
    /// A ramp's top is <see cref="HexField.StepGrade"/> steep and its gradient
    /// points along its axis, so what the hull feels is that steepness projected
    /// onto its heading: full up the axis, negated coming down it, and nothing
    /// across it. The projection also removes the sign that used to be taken from
    /// which end was higher - one statement where there were two.
    ///
    /// <b>Across the slope it says level, and that is the class rather than the
    /// map.</b> A tank standing broadside on a ramp really is rolled, and
    /// <see cref="ClimbLean"/> is one rotation about the pitch axis - the roll
    /// would want the other one, and the two do not compose into a rigid motion of
    /// a flat sprite any more than the foreshortening does. Named because it is
    /// visible: park across a face and the body stands square on a slanted tile.
    /// Its shadow, which can express the whole plane, is not square on it.
    ///
    /// Steps between two flat levels keep their old answer, the difference in
    /// levels laid along the travel: there is no face there, only a wall, and a
    /// tank crossing it is climbing nothing - it is being carried up. Given a
    /// direction anyway, so the shadow follows the body it belongs to rather than
    /// staying flat under a hull the same fiction has tipped.
    /// </summary>
    public Vector2 SurfaceSlope(Vehicle v)
    {
        if (!Field.HasRelief)
            return Vector2.Zero;
        Vector2I next = v.Onto;
        // Mixed between the two cells by how much of the hull has crossed, not
        // taken from whichever of them is a ramp - see Vehicle.LegBlend. Driving
        // onto a face this rises from nothing as the nose reaches it; cresting
        // onto the flat top it falls away the same way, which is the same
        // statement read backwards. Mixed as planes rather than as grades, which
        // is the same number for the body - a lerp of the projections is the
        // projection of the lerp - and the only one of the two the shadow can use.
        if (Field.IsRamp(v.Cell) || Field.IsRamp(next))
            return FaceSlope(v.Cell).Lerp(FaceSlope(next), v.LegBlend);
        int heading = HexField.HeadingTo(v.Cell, next);
        if (!v.Moving || heading < 0)
            return Vector2.Zero;
        return ClimbLean.Plane(
            (Field.LevelAt(next) - Field.LevelAt(v.Cell)) * Field.StepGrade,
            heading);
    }

    /// <summary>The gradient of one cell's own top: a ramp's steepness pointed up
    /// its axis, and nothing at all for a cell whose top is flat. Asked of the
    /// cell and not of the tank, because a face is a property of the ground - the
    /// projection onto whoever is standing on it happens later, in
    /// <see cref="ClimbLean.Along"/>.</summary>
    private Vector2 FaceSlope(Vector2I cell)
    {
        int face = Field.RampHeading(cell);
        return face < 0 ? Vector2.Zero
                        : ClimbLean.Plane(Field.StepGrade, face);
    }

    /// <summary>What the sprung plane is worth along a hull's heading, for the
    /// trace: the number the pitch is drawn from.</summary>
    public double SurfaceGrade(Vehicle v) =>
        ClimbLean.Along(SurfaceSlope(v), v.Sprite.HullFacing);

    private void UpdateLean(Vehicle v, double delta)
    {
        v.Lean.Update(SurfaceSlope(v), delta);
        v.Sprite.Climb = v.Lean.Angle(v.Sprite.HullFacing, Field.RiseFactor);
        // The same plane as the line above, in the terms a layer printed on the
        // ground needs it in - see TankSprite.Slope. One spring, two readings, so
        // the shadow cannot lag the hull standing on it.
        v.Sprite.Slope = ClimbLean.Print(v.Lean.Slope, Field.Squash,
                                         Field.RiseFactor);
        v.Sprite.Rise = Field.Lift <= 0.0f
            ? 1.0f
            : 1.0f + TankSprite.RisePerLevel * (v.Height / Field.Lift);
        if (v.Sprite.Climbing)
            v.Sprite.QueueRedraw();
    }

    public void UpdatePitch(Vehicle v, double accelRatio, double delta)
    {
        if (!PitchEnabled)
        {
            v.Pitch.Reset();
            v.Sprite.Pitch = 0.0;
            return;
        }
        v.Pitch.Update(accelRatio, delta);
        v.Sprite.Pitch = v.Pitch.Angle;
    }

    public void UpdateRumble(Vehicle v, double delta)
    {
        // What the jolt has to be whole against, and it is pushed rather than
        // pulled because the sprite has no business reading the camera. Every
        // frame, for the reason the tremble level is: a tank built after a zoom
        // would otherwise sit on the old factor.
        v.Sprite.ViewZoom = ViewZoom;
        v.Sprite.Painted = Staged;
        if (!RumbleEnabled)
        {
            v.Rumble.Reset();
            v.Sprite.Shake = 0.0;
            v.Sprite.Roll = 0.0;
            return;
        }
        // What the ground is doing to the ride. A pond bottom is not a field -
        // see BodyRumble.WetDamping, which also says what the ford's full ride
        // was doing to the waterline.
        v.Rumble.Damping = v.Wading ? BodyRumble.WetDamping : 1.0;
        // Where it is standing rather than how far it has come, and in flat space
        // rather than drawn space - the lift goes back on, the way every other
        // caller that asks the board about a point puts it back. See
        // BodyRumble.Advance and HexField.Bare.
        var ground = new Vector2(v.GroundPoint.X - Origin.X,
                                 v.GroundPoint.Y - Origin.Y + Field.Bare(v.Ground));
        // And what that ground is made of, asked of the field rather than worked
        // out here: HexField.KindAt is the one answer to which kind a cell is, and
        // a second one would disagree with the picture on the mixed board. Clamped
        // (CellUnder, not FlatCellAt) because a tank is always standing somewhere.
        Vector2I patchCell = Field.CellUnder(ground);
        v.Rumble.Roughness = Terrain is null ? 1.0
            : Terrain.RideOf(Field.KindAt(patchCell));
        // The cell goes in with the point, and it is the same cell the kind was
        // just read off - the patch is the square and the cell, so the kind takes
        // effect at the edge where it changes rather than at the next square. The
        // delta goes in for the hold, not for the bump: which bump is the place,
        // how long the body carries it is time - see BodyRumble.HoldSeconds.
        v.Rumble.Advance(ground, patchCell, v.Speed, delta);
        v.Sprite.Shake = v.Rumble.Heave;
        v.Sprite.Roll = v.Rumble.Roll;
    }



    /// <summary>
    /// Winds the belts on by the ground that went past.
    ///
    /// Keyed on distance like the rumble, and unlike it that is not a reading of
    /// the terrain but the belt's definition: it is the part in contact with the
    /// ground. So this one is driven from the same <c>_speed * delta</c> and
    /// goes still the moment the tank does, which is correct - a stationary
    /// tank's tracks are stationary, whatever the engine is doing.
    /// </summary>
    public void UpdateTracks(Vehicle v, (double Left, double Right) travel,
                              double delta)
    {
        if (!TracksEnabled || v.Atlas.HasTracks != true)
        {
            if (!v.Sprite.TracksRunning)
                return;
            v.TrackLeft.Reset();
            v.TrackRight.Reset();
            v.Sprite.TrackPhaseLeft = -1;
            v.Sprite.TrackPhaseRight = -1;
            v.Sprite.TrackBlurLeft = 0.0;
            v.Sprite.TrackBlurRight = 0.0;
            v.Sprite.QueueRedraw();
            return;
        }
        v.TrackLeft.Advance(travel.Left, delta);
        v.TrackRight.Advance(travel.Right, delta);
        v.Sprite.TrackPhaseLeft = v.TrackLeft.Frame;
        v.Sprite.TrackPhaseRight = v.TrackRight.Frame;
        v.Sprite.TrackBlurLeft = v.TrackLeft.Blur;
        v.Sprite.TrackBlurRight = v.TrackRight.Blur;
    }

    /// <summary>Runs every frame, moving or not - that is the whole point of it.
    /// The rumble is keyed on distance and goes silent the instant the tank
    /// stops; the engine does not. Speed only moves the frequency, so there is
    /// no threshold anywhere for the effect to switch on or off at.</summary>
    public void UpdateTremble(Vehicle v, double delta)
    {
        // A dead engine does not tremble. Of every switch this state throws,
        // this is the one that carries most: it is on by default precisely
        // because a tank with nothing moving on it reads as wrong, and that is
        // exactly what a wreck is supposed to read as.
        if (!TrembleEnabled || v.Wreck.Dead)
        {
            if (v.Sprite.TremblePitch == 0.0 && v.Sprite.TrembleYaw == 0.0)
                return;
            v.Tremble.Reset();
            v.Sprite.TremblePitch = 0.0;
            v.Sprite.TrembleYaw = 0.0;
            v.Sprite.QueueRedraw();
            return;
        }
        // Here rather than at the drag, so every tank carries the same level and a
        // tank built after it was set cannot be left on the tuned figure - see
        // TrembleLevel.
        v.Tremble.Level = TrembleLevel;
        v.Tremble.Advance(v.Speed, delta);
        v.Sprite.TremblePitch = v.Tremble.Pitch;
        v.Sprite.TrembleYaw = v.Tremble.Yaw;
        v.Sprite.QueueRedraw();
    }

    /// <summary>Runs every frame, moving or not, like the tremble and for the
    /// same reason: it is the engine, not the ground. Speed picks one of two
    /// states, or walks the ramp under --exhaust-ramp.</summary>
    public void UpdateExhaust(Vehicle v, double delta)
    {
        // Both settings before the early return, not after it. The panel's
        // caption reads the rate off this instance, so a clock left on last
        // frame's model while the exhaust is switched off would freeze under the
        // very control describing it - and switching the model back on with the
        // plume off, then on, would show the stale one for a frame.
        v.Exhaust.Level = ExhaustLevel;
        v.Exhaust.Binary = !ExhaustRamp;
        // A wreck's engine is not idling, and this is the plainest of the
        // several ways the bench says so.
        if (!ExhaustEnabled || v.Wreck.Dead || !v.Atlas.HasExhaust)
        {
            if (v.Sprite.ExhaustPhase < 0)
                return;
            v.Exhaust.Reset();
            v.Sprite.ExhaustPhase = -1;
            v.Sprite.ProceduralExhaust = ProceduralExhaust;
            v.Sprite.QueueRedraw();
            return;
        }
        v.Exhaust.Advance(v.Speed, delta);
        v.Sprite.ExhaustPhase = v.Exhaust.Frame;
        v.Sprite.ExhaustDensity = (float)v.Exhaust.Density;
        v.Sprite.ProceduralExhaust = ProceduralExhaust;
        // The continuous lap beside the frame it rounds to, for the built plume -
        // see TankSprite.ExhaustCycle. Set here rather than worked out there so
        // the loop stays the one thing that knows how long a lap is.
        v.Sprite.ExhaustCycle =
            (float)(v.Exhaust.Phase / Math.Max(v.Exhaust.Phases, 1));
    }

    /// <summary>
    /// Kill one tank.
    ///
    /// Three rounds past the paint do it, and nothing else - see
    /// <see cref="Land"/> and <see cref="Gunnery.PenetrationsToKill"/>. That is
    /// still an assumption and worth naming as one: the bench has no hit points,
    /// and inventing some would be a second damage model beside the one the class
    /// matchup already is. Counting penetrations leaves the matchup table deciding
    /// *who* can kill whom, which is what it was written to say - a light gun that
    /// can only ever scorch a heavy cannot destroy one however long it keeps at it
    /// - and leaves the count deciding *when*.
    ///
    /// The first version had the deepest scar level be death, and the two
    /// questions were one: a heavy killed a medium with its first shell and a
    /// medium killed a light never, so every cell of the table was one shot or
    /// nothing. Three rounds is what put the middle back.
    ///
    /// Everything here is the tank ceasing to be a machine. Most of the read is
    /// in this list rather than in any pixel: on this field a live tank trembles,
    /// smokes, scans with its turret and rocks when it moves.
    /// </summary>
    public void Kill(Vehicle v)
    {
        if (!v.Wreck.Kill())
            return;
        v.Path.Clear();
        v.PathStep = 0;
        v.Speed = 0.0;
        v.Target = null;
        v.Scan.Suspend();
        // It burns because it has just been destroyed, not because the key was
        // pressed - and the key can no longer put it out, since a wreck that
        // stops smouldering on a keystroke is a switch pretending to be a state.
        v.Burning = true;
        // Nobody goes on shooting at a wreck. Otherwise the engagement runs for
        // ever against a target that cannot answer, which reads as a gunnery
        // fault rather than as a fight that is over.
        foreach (Vehicle other in Vehicles)
            if (other.Target == v)
                other.Target = null;
        // Counted off the shells this hull took, like the impact takes are: the
        // three recordings are variety, and the same one every time reads as one
        // event rather than as three tanks dying.
        v.Audio?.Destroyed(1 + v.HitCount % 3);
        // The biggest of the three shoves, and it goes out from the hull that
        // just let go. In Kill rather than in Land so it happens once: the round
        // that did it has already sent its own, and a wreck taking further fire
        // does not blow up again.
        if (Wood is not null)
            Wood.Shock(v.GroundPoint - Origin, Wood.DeathBlast);
    }

    /// <summary>
    /// The wreck settling, once a frame.
    ///
    /// Everything it writes is a function of one age - see <see cref="Wreck"/> -
    /// so there is no table here and no second clock. It runs before the burn so
    /// the densities it sets are this frame's rather than last frame's, the same
    /// ordering the audio and the layers already need.
    /// </summary>
    private static void UpdateWreck(Vehicle v, double delta)
    {
        if (!v.Wreck.Dead)
            return;
        v.Wreck.Update(delta);
        TankSprite s = v.Sprite;
        // The pose swaps on the hit and the paint blackens over the second after
        // it: the turret dropping is the event, the char is the aftermath. Set
        // here rather than in Kill so that a reset which clears the wreck clears
        // this too, through the one path that owns the state.
        s.Wrecked = true;
        s.Char = v.Wreck.Char;
        s.FireDensity = (float)v.Wreck.Blaze;
        s.SmokeDensity = (float)v.Wreck.Smoke;
        // Once the flame is out there is nothing left to advance but the column,
        // and it is still asked for: the smoke is what says where a tank died
        // from the other side of the board.
        s.QueueRedraw();
    }

    /// <summary>Runs every frame like the exhaust, but takes no speed: see
    /// <see cref="BurnLoop"/>. Both halves are required before anything is
    /// shown, because the flame without its column is the washed-out half of the
    /// effect rather than a cheaper version of it.</summary>
    public void UpdateBurn(Vehicle v, double delta)
    {
        if (!v.Burning || !v.Atlas.HasBurning)
        {
            if (!v.Sprite.Burning && v.Sprite.FirePhase < 0 && v.Sprite.BurnPhase < 0)
                return;
            v.Burn.Reset();
            v.Sprite.Burning = false;
            v.Sprite.FirePhase = -1;
            v.Sprite.BurnPhase = -1;
            v.Sprite.FireCycle = 0.0f;
            v.Sprite.SmokeCycle = 0.0f;
            v.Sprite.ProceduralSmoke = ProceduralSmoke;
            v.Sprite.ProceduralFire = ProceduralFire;
            v.Sprite.QueueRedraw();
            return;
        }
        v.Burn.Advance(delta);
        v.Sprite.Burning = true;
        v.Sprite.ProceduralSmoke = ProceduralSmoke;
        v.Sprite.ProceduralFire = ProceduralFire;
        v.Sprite.FirePhase = v.Burn.FireFrame;
        v.Sprite.BurnPhase = v.Burn.SmokeFrame;
        // The same two positions unrounded, for whatever draws itself rather
        // than picking a rendered frame - see TankSprite.SmokeCycle. Set beside
        // the frames rather than derived at the reader, so the two can never be
        // a frame apart.
        v.Sprite.FireCycle = (float)(v.Burn.FirePhase / Math.Max(v.Burn.Phases, 1));
        v.Sprite.SmokeCycle = (float)(v.Burn.SmokePhase / Math.Max(v.Burn.Phases, 1));
    }


    // --- the round -----------------------------------------------------------

    /// <summary>
    /// How far above its own ground the gun's line runs, in levels.
    ///
    /// What it is for is the one test a level line needs: ground blocks the line
    /// when it stands higher than the line does. Half a level is about where a
    /// gun sits over its own deck, and the figure hardly matters because the
    /// board's tops are whole levels apart - anything strictly between 0 and 1
    /// gives the same answer for every flat cell. It decides exactly one case,
    /// the ramp, whose top is half a level up: at 0.5 a ramp on your own level
    /// does not block and one a level up does, which is what the picture shows.
    /// </summary>
    public const float Clearance = 0.5f;

    /// <summary>
    /// How far this tank's level line gets across the board, which cell stopped
    /// it, and whether anything did.
    ///
    /// <b>One walk for the ray and for the round</b>, which is the argument the
    /// aim point is already under: the line drawn out of a gun and the flight of
    /// what leaves it answer one question, so two copies of it would agree
    /// wherever anybody put them side by side and differ on the shot being
    /// watched.
    ///
    /// Asked of the board in flat space, the way every other caller that asks it
    /// about a point does - the lift goes back on. See <see cref="HexField.Bare"/>
    /// and <c>Main.Patch</c>.
    /// </summary>
    public (float Run, Vector2I? At, bool Blocked) Reach(Vehicle shooter,
                                                         Vector2 dir)
    {
        float top = Field.Bare(shooter.Ground);
        var ground = new Vector2(shooter.GroundPoint.X - Origin.X,
                                 shooter.GroundPoint.Y - Origin.Y + top);
        var tanks = new HashSet<Vector2I>();
        foreach (Vehicle other in Vehicles)
            if (!ReferenceEquals(other, shooter))
                tanks.Add(other.Cell);
        // And whatever else on the board is solid. Unioned rather than asked
        // separately, because Track's whole question is "which cells are not to
        // be crossed" and a shell does not care which kind of thing is in one.
        foreach (Vector2I cell in Obstacles)
            tanks.Add(cell);
        // And whether the cell it fires from stops it on the way out - see Barred.
        // Asked of the walk's own starting cell rather than of Vehicle.Cell: those
        // are the same hex whenever the tank is parked, and the walk is what the
        // answer is for.
        bool rim = Barred is not null
                   && Barred(Field.FlatCellAt(ground), dir);
        return Track(Field, ground, dir, top + Field.Lift * Clearance, tanks,
                     rim);
    }

    /// <summary>
    /// How far a level line out of a tank gets across the board before something
    /// stops it, which cell stopped it, and whether anything did.
    ///
    /// <b>Measured in cells walked rather than in pixels run</b>, which is the
    /// whole of why it exists: a length in pixels is a length on a board that
    /// zooms, so the same ray covered a different number of hexes at every zoom.
    /// Walked in flat space and asked of the field, so the answer is a fact about
    /// the ground rather than about the picture - the rule <c>Main.Patch</c> is
    /// written under, and the fourth place this board has charged for the
    /// difference.
    ///
    /// <b>What stands on the cell it fires from is asked separately</b>, through
    /// <paramref name="rim"/>, and defaults to no. Everything solid on this board
    /// fills a cell, so skipping the one under the gun is the rule rather than the
    /// exception; a wall stands on that cell's edges instead, and is the only
    /// thing that has ever needed the other answer. See <see cref="Barred"/>.
    ///
    /// <b>Ground blocks the line when it stands higher than the line does</b>, and
    /// a level line has one height everywhere - <paramref name="top"/> - so that
    /// is one comparison rather than a profile. A cell holding a tank blocks it
    /// too, wreck or not: what stops a shell is a tank being there.
    ///
    /// No cap on the range, and none is wanted. The line runs as far as there are
    /// hexes that way, which is what a sighting line does and what was asked for;
    /// a cap in cells would be a made-up number, and a cap in pixels is the thing
    /// being removed. <c>limit</c> is only the walk's own guard - a board's worth
    /// of travel, so a direction that never leaves the grid cannot spin here.
    ///
    /// Static and given a set of cells rather than the vehicles, so all of it can
    /// be asserted without a scene: the same reason <c>Main.Patch</c> and
    /// <see cref="Gunnery.Solve"/> are shaped this way.
    /// </summary>
    internal static (float Run, Vector2I? At, bool Blocked) Track(
        HexField field, Vector2 from, Vector2 dir, float top,
        IReadOnlySet<Vector2I> tanks, bool rim = false)
    {
        if (field.Atlas is null || dir.LengthSquared() < 1e-9f)
            return (0.0f, null, false);
        Vector2 step = dir.Normalized();
        Vector2 tile = field.Atlas.HexRect.Size;
        // An eighth of a tile: finer than half the shortest way across a cell, so
        // nothing standing in the line is stepped over, and coarse enough that a
        // board's worth of walk is a hundred samples.
        float grain = Mathf.Max(tile.X * 0.125f, 1.0f);
        float limit = field.Columns * tile.X + field.Rows * tile.Y;
        Vector2I here = field.FlatCellAt(from);
        Vector2I start = here;
        for (float run = grain; run <= limit; run += grain)
        {
            Vector2I cell = field.FlatCellAt(from + step * run);
            if (cell == here)
                continue;
            // The rim of the cell it fired from, if something stands on it - the
            // sample before the walk left, by the same reading as every other stop
            // below. Tested here rather than before the loop so the run is the
            // distance to the edge rather than zero: a wall on this cell is met
            // where the cell ends, not at the muzzle.
            if (rim && here == start)
                return (run - grain, start, true);
            here = cell;
            // Stopped at the near side of whatever stopped it - the sample before
            // the one that found it. The line is drawn from the muzzle, which
            // already stands a little way along this direction from the cell
            // centre the walk starts at, so what is drawn reaches a little into the
            // cell that stopped it rather than halting at its edge. That bias is
            // forward, bounded by the muzzle's own offset, and it is the readable
            // direction to be out by: a line that stops short of a hill by a third
            // of a cell reads as stopping at nothing.
            if (!field.InBounds(cell))
                return (run - grain, null, false);
            if (tanks.Contains(cell) || field.TopAt(cell) > top)
                return (run - grain, cell, true);
        }
        return (limit, null, false);
    }


    /// <summary>Where one gun would hole one tank, and what the round would be
    /// aimed through to get there. Everything the shot settles at the trigger
    /// except the calibre and the penetration level, which are dials rather than
    /// geometry.
    ///
    /// <c>Muzzle</c> is in board space, not in the shooter's own picture: it is
    /// what the round is launched from. The ray needs the other space and takes
    /// its own projection of <see cref="Vehicle.Bore"/>.</summary>
    public readonly record struct Aimed(Vector2 Muzzle, Vector2 Impact,
                                        string Face, float Scatter, float Rise,
                                        float BoreMiss);

    /// <summary>
    /// Solve one shot without firing it.
    ///
    /// <b>The one place a shooter, a target and a bearing turn into a hole in the
    /// armour</b>, and it is a method because there are two callers: the shot,
    /// and the aiming ray that draws where the shot would put its round. A second
    /// copy of this arithmetic would be a ray that looks plausible on every frame
    /// and is wrong about the one shot somebody is watching - the failure this
    /// project keeps naming, one number held in two places.
    ///
    /// <b>Here rather than on the harness because it decides nothing.</b> Who is
    /// engaging whom, whose lane it is and whether it is clear are statements
    /// about two tanks on a board and stay with the root that owns the board.
    /// Given a shooter, a victim and a bearing, where the hole goes is geometry -
    /// the same kind of thing as <see cref="TakeHit"/> and <see cref="Land"/>,
    /// which have always been here.
    ///
    /// <paramref name="serial"/> is the round's own count against that victim,
    /// because the scatter is hashed off it: that is what puts consecutive rounds
    /// beside each other instead of through the same hole, so predicting the next
    /// one means predicting with the count it will carry rather than the count
    /// standing now.
    ///
    /// Null when the target's atlas carries no plate table - nothing can be aimed
    /// at a tank whose armour was never measured, and <see cref="Shoot"/> refuses
    /// that case rather than aiming at the anchor.
    /// </summary>
    public static Aimed? AimAt(Vehicle shooter, Vehicle victim,
                               double fromBearing, int serial)
    {
        AtlasSet atlas = victim.Atlas;
        if (!atlas.HasHit)
            return null;
        double from = HexField.EdgeHeadings[Angles.SideFor(fromBearing)];
        double hull = victim.Sprite.HullFacing;
        string face = atlas.FaceFor(from, hull);
        Vector2 centroid = atlas.HitOffset(face, hull);
        Vector2 tangent = atlas.HitTangent(face, hull);
        Vector2 slope = atlas.HitSlope(face, hull);

        // Board space and not the shooter's own picture, because the round is a
        // thing on the board and the bore below is a difference of two tanks'
        // points. See Vehicle.Spot - on the staged board a sprite's own
        // coordinates are its render target's, so two of them cannot be
        // subtracted.
        (Vector2 tube, Vector2 along) = shooter.Bore(shooter.Sprite.TurretFacing);
        Vector2 launched = shooter.Spot(tube);
        // The bore as the target's own layers see it. Carried through both
        // transforms as a pair of points rather than as an angle, so a scaled
        // class and a hull leaning on its springs are handled by the transforms
        // that already express them instead of by arithmetic repeated here.
        // Unspot and not ToLocal, by the same argument: a point handed from one
        // sprite to the other through ToGlobal and ToLocal arrives somewhere else
        // entirely on the staged board, and the bore is the whole of how the
        // scatter is put onto the gun's axis.
        Vector2 eye = victim.Unspot(launched);
        Vector2 bore = victim.Unspot(shooter.Spot(tube + along * 100.0f)) - eye;

        // The vertical fraction stays on its hash and the tangential one follows
        // it onto the gun's axis - see Gunnery.ScatterOntoBore. Only on this
        // path: a round asked for by the U key has no shooter and so no bore, and
        // it keeps both hashes.
        //
        // The hash is not gone, it is demoted to what it should always have been:
        // dispersion about an aim point, a tenth of a plate rather than the whole
        // of it. Without it the solve is one number per geometry, so a gun
        // shooting twice from the same place would put its second round exactly
        // through the first - and where the solve is clamped that is a column of
        // holes down one edge, which is the row-of-stamps failure again in the
        // other axis.
        float rise = RiseAt(serial);
        float scatter = AimedScatter(eye, bore, centroid, tangent, slope,
                                     rise, serial);
        Vector2 impact = centroid + tangent * scatter + slope * rise;
        return new Aimed(launched, impact, face, scatter, rise,
                         Gunnery.BoreMiss(eye, bore, impact));
    }

    /// <summary>
    /// One tank's round at another: solve it, and put it in the air.
    ///
    /// False when there is nothing to aim at - a target whose armour was never
    /// measured - so the caller can let the shot go at the ground instead. That
    /// is <see cref="Launch"/>'s contract read from the other end.
    /// </summary>
    public bool Shoot(Vehicle shooter, Vehicle victim, double fromBearing)
    {
        // Bumped before the aim rather than after it, because the aim depends on
        // it: the scatter hash is what puts the second round beside the first, so
        // the serial the round about to leave carries is the count as it is now.
        // It is also why the aiming ray has to predict with one more than this.
        victim.HitCount++;
        if (AimAt(shooter, victim, fromBearing, victim.HitCount) is not Aimed shot)
        {
            victim.HitCount--;
            return false;
        }
        Send(shooter, new Shell
        {
            Shooter = shooter,
            Target = victim,
            ImpactLocal = shot.Impact,
            From = shot.Muzzle,
            FromLift = shooter.LiftOf(shot.Muzzle),
            BoreMiss = shot.BoreMiss,
            Serial = victim.HitCount,
            // Carried rather than re-read on arrival: what the trigger decided
            // travels with the shell, because the alternative is a round that
            // changes calibre in flight because somebody turned a dial.
            Face = shot.Face,
            // Which way it came from, for the plate it will hit to mirror it
            // about - see Shell.Bearing.
            Bearing = fromBearing,
            Scatter = shot.Scatter,
            Rise = shot.Rise,
            Calibre = Ordnance.At(Calibre),
            Ammo = Ammo,
            Level = Gunnery.Penetration(shooter.Profile, victim.Profile),
        });
        return true;
    }

    /// <summary>
    /// Put a finished round in the air on the tank that fired it.
    ///
    /// The one door into <see cref="Vehicle.Rounds"/>, so the two things a round
    /// needs before anybody sees it - a list to be advanced from and a parent to
    /// be drawn under - happen together. The harness's aimed round comes through
    /// here as well, which is what keeps the ground shot and the armour shot one
    /// kind of object.
    /// </summary>
    public void Send(Vehicle shooter, Shell round)
    {
        // Invisible is still in the air: the flight is what separates the report
        // from the impact, and only the drawing is on a switch.
        round.Visible = TracerVisible;
        shooter.Rounds.Add(round);
        Deck?.AddChild(round);
    }

    /// <summary>
    /// A round with nobody to hit: down the tube, onto the field where the walk
    /// stopped.
    ///
    /// <b>Not a lesser shot.</b> A gun fires down a flat side of the hex and hits
    /// what it is laid on; laid on nobody it still goes off, and what leaves has
    /// to leave, cross and land. Before this the hand-fired shot was the one shot
    /// on the board with no round in it, which reads as a key that half works.
    ///
    /// Down the tube rather than down a lane, and that is allowed exactly because
    /// there is nobody: the six lanes are a rule about <em>armour</em> - the
    /// arrival bearing has to be a flat side of the hex or
    /// <c>AtlasSet.FaceFor</c> is handed a number it cannot use - and a round
    /// going at the ground never asks for a plate.
    /// </summary>
    private void Loose(Vehicle shooter)
    {
        if (Field?.Atlas is null)
            return;
        (Vector2 tube, Vector2 along) = shooter.Bore(shooter.Sprite.TurretFacing);
        if (along.LengthSquared() < 1e-6f)
            return;
        Vector2 dir = along.Normalized();
        // Board space, by Vehicle.Spot's argument - the round is a thing on the
        // board, and on the staged board a sprite's own coordinates are its render
        // target's. The run comes back in flat pixels, which the board is measured
        // in too, so the direction carries it across unchanged.
        Vector2 from = shooter.Spot(tube);
        (float run, Vector2I? at, bool blocked) = Reach(shooter, dir);
        float lift = shooter.LiftOf(from);
        Send(shooter, new Shell
        {
            Shooter = shooter,
            Target = null,
            // What stopped it, taken from the walk rather than from where it
            // landed - see Shell.Blocked.
            Blocked = blocked ? at : null,
            Ammo = Ammo,
            Ground = from + dir * run,
            // The same height at both ends, because a tank gun is level and that
            // is the one assumption the walk itself is written under - see Track,
            // which blocks the line with a single comparison for exactly this
            // reason.
            GroundLift = lift,
            From = from,
            FromLift = lift,
            // The armour half, written blank rather than left to default: there is
            // no plate in this shot at all. See Shell.Target.
            ImpactLocal = Vector2.Zero,
            Serial = 0,
            Face = "",
            Scatter = 0.0f,
            Rise = 0.0f,
            BoreMiss = 0.0f,
            Calibre = Ordnance.At(Calibre),
            Level = 0,
        });
    }

    /// <summary>
    /// Every round in the air, one frame on.
    ///
    /// <b>A pass of its own after every tank has moved</b>, not a step inside
    /// <see cref="Run"/>: a round aimed at a tank has to see where that tank got
    /// to this frame rather than last, and folded into the per-tank sequence the
    /// first gun's shell would fly against the second gun's stale position. Same
    /// ordering argument the belts and the audio already need.
    ///
    /// Landing and being finished with stopped being the same frame when the smoke
    /// was allowed to outlive the round, so the strike is guarded and the node
    /// goes on the trail rather than on the hit.
    /// </summary>
    public void Fly(double delta)
    {
        foreach (Vehicle v in Vehicles)
            for (int i = v.Rounds.Count - 1; i >= 0; i--)
            {
                Shell round = v.Rounds[i];
                round.Advance(delta);
                if (round.Arrived && !round.Struck)
                {
                    round.Struck = true;
                    Strike(round);
                }
                if (!round.Expired)
                    continue;
                v.Rounds.RemoveAt(i);
                round.QueueFree();
            }
    }

    /// <summary>
    /// A round that has flown its path.
    ///
    /// Nothing is decided here: what the trigger settled travels with the shell,
    /// because the alternative is a round that changes calibre in flight because
    /// somebody turned a dial.
    ///
    /// A round with no target lands and that is all of it - no plate, no burst,
    /// no sound. It is not a hit that failed to register; it is a shell going
    /// into the field, which is what the board has to say back when the gun was
    /// laid on nobody. See <see cref="Shell.Target"/>.
    /// </summary>
    private void Strike(Shell round)
    {
        if (round.Target is null)
        {
            // Whatever the board makes of a round that went into it. Unanswered,
            // the shot goes into the field exactly as it did - see Landed.
            Landed?.Invoke(round);
            return;
        }
        Land(round.Target, round.Face, round.Scatter, round.Rise, round.Calibre,
             round.Level, 1, round.Bearing);
    }

    /// <summary>Take every round off the board - the reset, and it runs before
    /// the repair: a shell left in the air would land on armour that has just
    /// been mended.</summary>
    public void ClearRounds()
    {
        foreach (Vehicle v in Vehicles)
        {
            foreach (Shell round in v.Rounds)
                round.QueueFree();
            v.Rounds.Clear();
        }
    }

    /// <summary>
    /// The tracer switch reaching the rounds already up.
    ///
    /// Reaching into what is flying is the point rather than an extra: switching
    /// a drawing off has to take effect on what is being drawn, and a round that
    /// kept its tracer because it was launched a moment ago would read as the
    /// switch not working.
    /// </summary>
    public void ShowRounds()
    {
        foreach (Vehicle v in Vehicles)
            foreach (Shell round in v.Rounds)
                round.Visible = TracerVisible;
    }

    /// <summary>
    /// One round out of one tank's gun.
    ///
    /// Takes the vehicle rather than working on the driven one, because a tank
    /// left engaging a target goes on firing after you have selected somebody
    /// else - which is the whole of the attack scene.
    ///
    /// <b>Six things off one trigger now, and the fifth is the shell.</b> It was
    /// four, and the round was wired to the standing order alone - so the one
    /// shot a person fires by hand was the one with nothing in it. Who the round
    /// is for is asked of <see cref="Launch"/>, which is the harness's answer;
    /// unanswered, the shot goes at the ground.
    ///
    /// <b>The sixth is the ground, and it is the only one that belongs to neither
    /// the tank nor the round.</b> A muzzle blast blows dust off the board it is
    /// standing over - see <see cref="Kicked"/> - which is a thing on the board
    /// with its own clock, three times the length of the flash's, and so it is
    /// the board's to raise rather than the sprite's to draw.
    /// </summary>
    public void Fire(Vehicle v)
    {
        v.ShotFrame = 0;
        v.ReloadLeft = v.Profile.ReloadTime;
        // The sprite shear only if it is asked for: the tube's own recoil is the
        // real article now, and the two together show one true movement and one
        // invented one.
        if (RecoilShear)
            v.Recoil.Fire(Angles.WrapAngle(v.Sprite.TurretFacing - v.Sprite.HullFacing));
        // The tube goes back on the same trigger and on its own clock: the two
        // events last different lengths of time, so one counter would give one of
        // them the wrong tempo. See RecoilLoop.
        v.Barrel.Fire();
        // Fourth thing off the one trigger, and the only one that is not about
        // the tank at all: the view. Along the gun's own ground direction and
        // unnormalised, so a shot into the screen jolts less than one across it.
        // Any tank's shot, not just the driven one - a gun going off beside you
        // is the same event as your own, and the bench has all three on screen.
        if (ShakeOn && v.Atlas is not null)
            Shake?.Fire(v.Atlas.GroundDirection(v.Sprite.TurretFacing),
                        v.Profile.ShotShake);
        // And the wood, which is the same event told to the other thing on the
        // board that can answer it. Off the tank's contact point rather than the
        // muzzle: the muzzle stands 60-70px out along the gun, and nothing grows
        // within 83 of a cell's centre, so the source and the tube's end are on
        // the same side of every tree there is - it would move the number and
        // not the picture. Any tank's gun, not just the driven one, for the
        // reason the camera shake gives just above.
        if (Wood is not null)
            Wood.Shock(v.GroundPoint - Origin,
                         v.Profile.ShotShake * Wood.ShotBlast);
        // And the ground the gun is standing over, which is the sixth thing off
        // the one trigger and the only one that is neither the tank nor the round:
        // a muzzle blast blows dust off the board. Along the gun's own ground
        // direction and unnormalised, for the shake's reason twice over - the
        // dust goes where the gun points, and a shot into the screen throws it
        // across less of the picture. Any tank's gun, not just the driven one.
        if (v.Atlas is not null)
            Kicked?.Invoke(v, v.Atlas.GroundDirection(v.Sprite.TurretFacing));
        // Third thing off one trigger, and a third clock, for the same reason:
        // the gun's report is 1.2s on the light and 3.4s on the heavy, and neither
        // is the 34 frames the flash runs or the 28 the tube takes to come home.
        v.Audio?.Fire();
        // And the round, last because it is the only one of the five that leaves
        // the tank. Who it is for is the caller's to know - see Launch.
        if (Launch is null || !Launch(v))
            Loose(v);
    }

    /// <summary>The shot runs on screen frames, not seconds. The sheet is a
    /// hand-timed sequence of held frames, so counting frames is what it is
    /// timed in; under --capture the clock is fixed at 1/60 anyway, which is
    /// what makes a shot land on the same sheet frame in two runs.</summary>
    private void UpdateShot(Vehicle v, double delta)
    {
        if (RecoilShear)
        {
            v.Recoil.Update(delta);
            v.Sprite.RecoilPitch = v.Recoil.Pitch;
            v.Sprite.RecoilRoll = v.Recoil.Roll;
        }
        else if (v.Recoil.Moving || v.Sprite.RecoilPitch != 0.0
                 || v.Sprite.RecoilRoll != 0.0)
        {
            // Switched off part way through a ring-down, which has to put the
            // body back rather than leave it leaning: the spring is the only
            // effect here that holds a pose with nothing driving it.
            v.Recoil.Reset();
            v.Sprite.RecoilPitch = 0.0;
            v.Sprite.RecoilRoll = 0.0;
            v.Sprite.QueueRedraw();
        }

        // Both clocks run, whichever source is showing. They are separate
        // tables - sixteen sheet frames against eight rendered phases - but
        // they add up to the same 34 frames, so flipping V mid-shot swaps the
        // picture without moving the moment, which is what makes an A/B
        // comparison one.
        int frame = v.ShotFrame < 0 ? -1 : FlashSheet.FrameAt(v.ShotFrame);
        int phase = v.ShotFrame < 0 ? -1 : EffectLayer.PhaseAt(v.ShotFrame);
        if (v.ShotFrame >= 0)
        {
            v.ShotFrame++;
            if (frame < 0 && phase < 0)
                v.ShotFrame = -1;
        }
        // The gun tube, on its own clock and its own count of frames. Switched
        // off it holds the rest pose rather than disappearing - the gun is still
        // a gun, it just stops moving, which is the A/B against every tank
        // rendered before this layer existed.
        int tube = 0;
        if (v.Atlas.HasRecoil && RecoilTube)
        {
            v.Barrel.Advance();
            tube = v.Barrel.Phase;
        }
        else
        {
            v.Barrel.Reset();
        }

        if (frame != v.Sprite.FlashFrame || phase != v.Sprite.ShotPhase
            || tube != v.Sprite.RecoilPhase || v.Recoil.Moving)
        {
            v.Sprite.FlashFrame = frame;
            v.Sprite.ShotPhase = phase;
            v.Sprite.RecoilPhase = tube;
            v.Sprite.QueueRedraw();
        }
    }

    /// <summary>
    /// One shell into one tank, from a world bearing.
    ///
    /// The victim is a parameter for the reason the shooter is: a tank is shot
    /// at by another tank now, and the one being driven is as likely to be the
    /// gunner as the target. Everything else is unchanged - which is the point,
    /// because the armour model is exactly as good at answering a shell that
    /// came from a neighbouring tank as one that came from a keypress.
    ///
    /// <paramref name="level"/> is the ceiling the round cannot get past, or null
    /// for a round with no ceiling, which digs by <paramref name="bite"/>
    /// instead. Exactly one of the two is in force, and which one is the whole
    /// difference between a shell fired by a tank and a shell asked for by a key
    /// - see <see cref="TankSprite.DamageTo"/>.
    /// </summary>
    public void TakeHit(Vehicle victim, double fromBearing, float calibre,
                         int? level, int bite)
    {
        TankSprite sprite = victim.Sprite;
        AtlasSet atlas = victim.Atlas;
        if (!atlas.HasHit)
            return;
        // <b>The dial fills in the end a keypress does not have.</b> A caller
        // that already knows the level - one tank shooting another - keeps it;
        // one that passed null gets whatever HitBy says, which is either the
        // matchup against this hull or still null. See HitBy on why a manual hit
        // could not show a ricochet before this line existed.
        level ??= HitDepth(victim);
        // Snapped, not taken as given: a shell arrives from a neighbouring
        // cell, so every angle anything hands in resolves to one of the six
        // sides rather than to itself.
        double from = HexField.EdgeHeadings[Angles.SideFor(fromBearing)];
        // Deterministic under --capture and --trace, which fix the time step so
        // two runs can be diffed; a random scatter would put the two hits in
        // different places and the diff would measure that instead.
        victim.HitCount++;
        // How far along the plate this one landed, as a fraction of its
        // half-width. It is passed to both halves - the burst that shows the
        // shell arriving and the mark it leaves - because it is one shell, and
        // a hole appearing in the middle of a plate the flash went off the end
        // of is the whole reason this is a shared number rather than two.
        //
        // +-0.45 rather than the burst's old +-0.6: the mark has to stay on the
        // armour, and it is about 17px wide against half-widths of 38 to 61.
        //
        // Two axes, because one made every round on a plate land at the same
        // height - see RiseAt for why the vertical range is the tighter of the two.
        float scatter = ScatterAt(victim.HitCount);
        float rise = RiseAt(victim.HitCount);
        string face = atlas.FaceFor(from, sprite.HullFacing);
        Land(victim, face, scatter, rise, calibre, level, bite, from);
    }

    /// <summary>A round arriving, with everything about it already settled.
    ///
    /// Split out of <see cref="TakeHit"/> when the shell got a flight: the key
    /// press works out which plate at the moment it lands, and a fired round
    /// worked it out at the trigger and carried it. Both end here, so there is
    /// one place where a tank takes a hit however the hit was arranged.</summary>
    public void Land(Vehicle victim, string face, float scatter, float rise,
                      float calibre, int? level, int bite, double from)
    {
        TankSprite sprite = victim.Sprite;
        // How deep it goes is the calibre's business - see TankSprite.Damage.
        // A light round still walks a plate scorch -> gouge -> breach over three
        // hits, which is what shows that the phase axis is damage and not three
        // renders of one drawing; a heavy one gets there in one.
        //
        // <b>Asked before anything is drawn, which it was not.</b> The rendered
        // burst used to be started first and the depth worked out after, because
        // nothing drawn depended on the depth; a round that bounces now draws
        // something else entirely, so the one question whose answer decides which
        // picture this is has to be asked first. Nothing else moved: the mark and
        // the loop touch different state.
        int got = level is int cap
            ? sprite.DamageTo(face, scatter, rise, cap)
            : sprite.Damage(face, scatter, rise, bite);
        // <b>A round that did not get in draws the ricochet instead of the
        // rendered burst, not as well as it.</b> Two impacts drawn for one shell
        // is the double model this project spends its docstrings refusing, and
        // the two would not even agree: the rendered pair is a puff of earth-tan
        // dust with a warm core, which is a shell going in. The whole point of
        // this layer is that it looks like a shell not going in.
        //
        // The threshold is not a new one. Zero is exactly "the gun did not
        // out-class the armour" - Gunnery.Penetration's own answer, already what
        // the impact sound switches on and already what the kill tally counts -
        // so the picture, the ear and the tally agree by construction rather than
        // by three comparisons kept in step.
        //
        // The calibre goes the same way as the scatter and for the same reason:
        // both are settled the moment the round leaves, so both travel with it
        // rather than being looked up again while it is on screen.
        bool bounced = got <= 0 && Bounce;
        if (bounced)
        {
            (Vector2 plate, Vector2 away) = victim.Graze(face, scatter, rise, from);
            Bounced?.Invoke(victim, plate, away, victim.Turned(face));
        }
        else
        {
            victim.Hit.Strike(face, scatter, rise, calibre);
        }
        // The sound is chosen by the same level the mark is, so a round that
        // bounces is heard bouncing and one that goes through is heard going
        // through. Not the calibre directly: a light round on armour already
        // twice hit does go through, and the ear should be told the same thing
        // the plate is.
        //
        // On the tank that was hit rather than on the one that fired: the ricochet
        // rings off this armour, and its player is positioned there.
        //
        // The shell count picks which of the two takes plays - see Struck. It is
        // the victim's, so it counts shells into this hull however many guns are
        // pointed at it.
        victim.Audio?.Struck(got, victim.HitCount);
        // The wood answers the burst, from the hull it went off against. Every
        // round, not only the ones that go through: a shell breaking up on
        // armour is the louder of the two events, and a wood that only flinched
        // at penetrations would be reading the damage table off the trees.
        if (Wood is not null)
            Wood.Shock(victim.GroundPoint - Origin, Wood.HitBlast);
        // Three rounds past the paint kill, and the matchup decides which rounds
        // those are. The two halves are deliberately apart - see
        // Gunnery.PenetrationsToKill. The tally is counted inside Damage/DamageTo,
        // so this asks the tank what it has taken rather than judging the shell
        // that just landed: a plate a heavy has already breached goes no deeper,
        // and a kill read off this round's level would stall there.
        if (sprite.Penetrations >= Gunnery.PenetrationsToKill)
            Kill(victim);
    }

    /// <summary>Where the nth shell lands along its plate, as a fraction of the
    /// plate's half-width. Deterministic on purpose: --capture and --trace fix
    /// the time step so two runs can be diffed, and a random scatter would be
    /// measuring itself.</summary>
    internal static float ScatterAt(int n) => (((n * 37) % 100) / 100.0f - 0.5f) * 0.9f;

    /// <summary>
    /// How far along its plate a hit may sit, as a fraction of the half-width.
    ///
    /// It bounds <see cref="ScatterAt"/> above, which is why they are one number:
    /// the hash was written to reach exactly this far, and a fired round now has
    /// its offset *solved* rather than hashed - see
    /// <see cref="Gunnery.ScatterOntoBore"/> - so the limit had to become a thing
    /// with a name instead of a 0.9 buried in a hash. Both paths land on the same
    /// armour and the mark is the same size on it.
    /// </summary>
    internal const float ScatterLimit = 0.45f;

    /// <summary>
    /// How far a fired round disperses about where the gun was actually aimed,
    /// as a fraction of the plate's half-width.
    ///
    /// A quarter of <see cref="ScatterLimit"/>, and the difference between the
    /// two numbers is the whole change: the old hash reached the full 0.45
    /// because it *was* the placement, and 0.45 of a 28 to 53px half-width runs
    /// almost exactly across the shot, so it was up to 24px of visible daylight
    /// between the tube and its own hole. A group of rounds fired at one tank
    /// from one place is a group, not a plate-wide spread.
    /// </summary>
    internal const float ScatterSpread = 0.12f;

    /// <summary>
    /// Where the nth round from this gun lands along that plate: aimed, then
    /// dispersed, then kept on the armour.
    ///
    /// One function rather than the three steps written out at the trigger,
    /// because the self-test has to be able to ask what actually happens. A check
    /// that re-assembled the same three steps for itself would keep passing after
    /// the trigger stopped doing one of them - the shape of failure this project
    /// names as a check whose answer goes nowhere.
    /// </summary>
    internal static float AimedScatter(Vector2 muzzle, Vector2 bore,
                                       Vector2 centroid, Vector2 tangent,
                                       Vector2 slope, float rise, int n) =>
        Math.Clamp(
            Gunnery.ScatterOntoBore(muzzle, bore, centroid, tangent, slope, rise,
                                    ScatterLimit, ScatterAt(n))
            + ScatterSpread * (ScatterAt(n) / ScatterLimit),
            -ScatterLimit, ScatterLimit);

    /// <summary>
    /// And where up the plate's slope, as a fraction of its half-height.
    ///
    /// **+-0.30 against the tangent's +-0.45, and the asymmetry is the plate's,
    /// not a preference.** A plate is two to three times wider than it is tall
    /// (tangent 28-53px against slope 7-22px across the three tanks), so even an
    /// equal fraction would already draw an ellipse - which is right, a group of
    /// rounds on a wide short plate *is* wider than it is tall. What makes the
    /// vertical fraction the smaller one as well is that the mark is nearly as
    /// tall as the plate is: <c>scar_fit</c> runs 0.62 to 1.30, so vertical
    /// headroom is 5-8% of a half-height on the rear plates and negative on HTP's.
    /// The mark exceeding its plate is not the same as leaving the tank - it slides
    /// onto the deck above or the lower hull - but it is the scarce direction, and
    /// this is the number to pull back if <c>scar_off_armour</c> ever complains.
    ///
    /// A different modulus, not just a different multiplier, and that is not
    /// fussiness: 61n and 37n are both invertible mod 100, so 61n = 53*(37n) and
    /// the vertical would be a *function* of the horizontal - every pair sitting
    /// on one of a handful of lines. 97 is prime and coprime to 100, so the pair
    /// does not repeat for 9700 rounds.
    /// </summary>
    internal static float RiseAt(int n) => (((n * 61) % 97) / 97.0f - 0.5f) * 0.6f;

    private void UpdateHit(Vehicle v, double delta)
    {
        TankSprite sprite = v.Sprite;
        HitLoop hit = v.Hit;
        int phase = hit.Phase;
        if (phase >= 0)
        {
            // Read every frame, not once at the strike: the hull can turn while
            // the dust is still settling, and the hit has to stay on the plate
            // it landed on rather than sliding round with the heading.
            sprite.HitOffset = v.Plated(hit.Face, hit.Scatter, hit.Rise);
            sprite.HitBehind = v.Turned(hit.Face);
            // The shell's calibre, not the dial's. Pushed from here alongside
            // the other two things the layer needs and cannot work out for
            // itself, so what is drawn is what was fired.
            sprite.HitScale = hit.Scale;
        }
        if (phase != sprite.HitPhase)
        {
            sprite.HitPhase = phase;
            sprite.QueueRedraw();
        }
        hit.Advance();
    }

    /// <summary>Suspended while under way, and while anything else is driving
    /// the turret. A locked turret holding its world heading through a
    /// manoeuvre is the feature this harness exists to show; a scan quietly
    /// walking it off that heading would look exactly like the lock failing.</summary>
    public void UpdateScan(Vehicle v, double delta)
    {
        // The spin and the mouse only ever drive the tank being controlled, so
        // they only suspend that one's scan: a tank standing off to the side keeps
        // looking around while you spin the turret of the one you are driving.
        // A tank with a target is laying its gun, and the scan is what it does
        // with nothing to look at - so the target suspends it on that tank only,
        // exactly as the spin and the mouse suspend it on the driven one.
        if (!ScanEnabled || v.Moving || v.Target is not null || v.Wreck.Dead
            || (v == Driven && TurretHeld?.Invoke(v) == true))
        {
            v.Scan.Suspend();
            return;
        }
        double step = 360.0 / v.Atlas.Count;
        if (!v.Scan.Based)
        {
            // The sway is an offset, so it needs a bearing to be an offset from,
            // and the honest one is where the turret has just been left - a tank
            // that has finished laying its gun down a lane sways about that lane.
            // Snapped to a rendered bearing, which costs nothing on screen: the
            // sprite is drawn at the nearest rendered heading regardless, so this
            // moves the number and not the picture.
            double onto = Angles.Mod(Math.Round(v.Sprite.TurretFacing / step) * step, 360.0);
            v.Scan.Rest(onto);
            if (onto != v.Sprite.TurretFacing)
            {
                v.Sprite.TurretFacing = onto;
                v.Sprite.QueueRedraw();
            }
        }
        double move = v.Scan.Advance(step, delta);
        if (move == 0.0)
            return;
        v.Sprite.TurretFacing = Angles.Mod(v.Sprite.TurretFacing + move, 360.0);
        v.Sprite.QueueRedraw();
    }
}
