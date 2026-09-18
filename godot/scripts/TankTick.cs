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

    // <b>And it is asked of every cell of the walk, not only the one the gun
    // stands on.</b> It began as the firing cell's exception - everything else
    // solid filled a cell, so the cell under the gun was the one case a cell set
    // could not answer - and that left walls being two things at once: an edge
    // here and a whole cell in Obstacles. The cell won, so a breach was not a way
    // through. See Track.

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

    /// <summary>
    /// Whether every hull on the board carries the wading gear - GDD upgrades.md
    /// "ОПВТ": with it a tank in deep water swims, without it the water stops the
    /// engine and it is drowned (states.md).
    ///
    /// <b>One switch for the board rather than a flag per class</b>, because the
    /// gear is an upgrade a unit buys and the bench has no unit to buy it - what
    /// it has is two pictures to show, and the switch picks which. On by default
    /// so a drive into the pond shows the swim; off shows the drowning
    /// (<c>--no-amphibious</c>). Read in <see cref="Park"/>, where a hull comes
    /// to be on a cell. See docs/swim-plan.md, step 1.
    /// </summary>
    public bool Amphibious = true;

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
    /// <b>Against armour it decides the picture and not the damage, and that
    /// boundary is deliberate.</b> An HE round bursts on the face of the plate it
    /// arrives on, whether or not the plate held - see <see cref="ProcSlam"/> and
    /// the fork in <see cref="Land"/> - so this is what says which of the three
    /// impacts gets drawn. What it does <em>not</em> touch is
    /// <see cref="Gunnery.Penetration"/>, which is a class of gun against a class
    /// of armour and knows nothing of filling: HE and AP go equally deep, kill on
    /// the same third penetration, and are heard by the same impact recording.
    /// Writing HE into the damage table is a change to what the bench <i>does</i>
    /// rather than to what it draws, and it is still a separate piece of work.
    ///
    /// <b>And the default is HE, which means the plainest hit on the bench is now
    /// a burst rather than a bounce.</b> That is the model rather than a
    /// regression - a shell that explodes does not ricochet - but it does mean
    /// the ricochet has to be asked for: <c>--ammo ap</c>, or the row beside this
    /// one. Before this line existed the dial meant nothing here, so a bounce was
    /// what every kind of round drew.
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
    /// <summary>
    /// What can shake the view. One row each in <see cref="Shakes"/>, rather
    /// than one switch over several events.
    ///
    /// <b>Because "is the shake on" was never one question.</b> It started as
    /// the gun's switch, off by default for a measurement's sake - nearly every
    /// number on these benches is a pixel diff taken near a shot, and a shake on
    /// every shot is what the diff would measure. A death then wanted the
    /// opposite answer and got a second switch, with the argument written down:
    /// one event a battle, not one every reload, and the shove it gives the
    /// camera is most of what says a tank went up rather than that a picture of
    /// one did. Two more events have joined since - the mine and the ram - and
    /// each landed on whichever of the two switches it was nearest, which is how
    /// a ram came to be governed by a flag about guns and to be invisible in
    /// every capture taken of it.
    ///
    /// So the switch is per source, and so is the size: what a source is worth
    /// is a fact about that source, and asking it the same question twice in two
    /// places is how the two answers start to differ.
    /// </summary>
    public enum Shook
    {
        /// <summary>The gun going off - the first of these and the reason the
        /// default is off.</summary>
        Gun,
        /// <summary>A charge under the belly.</summary>
        Mine,
        /// <summary>Two hulls meeting.</summary>
        Ram,
        /// <summary>Ammunition going up.</summary>
        Death,
        /// <summary>The turret coming back down on the deck after it.</summary>
        Turret,
    }

    /// <summary>One source's channel: whether it shakes the view at all, the
    /// knock it gives the spring and the ring it leaves after - both as a share
    /// of the class's own <see cref="MovementProfile.ShotShake"/>, which is the
    /// only measure of "how heavy is this tank" this camera has. A ring of
    /// nought is a source that knocks and does not ring, which is every one of
    /// them but the two that are an explosion of some kind.</summary>
    public sealed class Tremor
    {
        public bool On;
        public double Kick;
        public double Ring;
    }

    /// <summary>
    /// The channels themselves, indexed by <see cref="Shook"/>.
    ///
    /// <b>The defaults are two answers, not one, and the split is the measured
    /// one.</b> Off for what happens every reload and would be measured instead
    /// of the effect a capture is aimed at - which is the gun, and only the gun.
    /// On for what happens once and carries the event: a death, the turret coming
    /// down after it, a ram (a decision a player makes two or three times a
    /// battle), and a mine, which a hull drives onto once and never twice. The
    /// A/B that has to straddle one of those turns that row off rather than the
    /// gun's.
    /// </summary>
    public readonly Tremor[] Shakes =
    {
        new() { On = CameraShake.OnByDefault, Kick = 1.0 },
        new() { On = true, Kick = MineShake },
        new() { On = true, Kick = 1.0, Ring = RamRumble },
        new() { On = true, Kick = DeathShake, Ring = DeathRumble },
        new() { On = true, Kick = TurretLandingShake },
    };

    /// <summary>One source's channel by name.</summary>
    public Tremor ShakeOf(Shook what) => Shakes[(int)what];

    /// <summary>Whether anything at all can shake the view - what a scene root
    /// asks before it runs the spring, and the one question that really is about
    /// all of them at once. Put down when nothing can feed it, so a switch
    /// flipped mid-ring does not park the view a few pixels out.</summary>
    public bool Shaking
    {
        get
        {
            foreach (Tremor one in Shakes)
                if (one.On)
                    return true;
            return false;
        }
    }

    /// <summary>The gun's own row, under the name three roots, a flag and a
    /// panel already know it by. <b>The gun's alone now</b>: the mine used to
    /// ride this switch because it was the nearest one, and it has a row of its
    /// own.</summary>
    public bool ShakeOn
    {
        get => ShakeOf(Shook.Gun).On;
        set => ShakeOf(Shook.Gun).On = value;
    }

    /// <summary>
    /// One source shaking the view, or not - <see cref="Shakes"/>.
    ///
    /// <paramref name="along"/> is the direction unnormalised, which is the whole
    /// of what the projection has to say about a knock: a shot into the screen
    /// shakes less than one across it. Straight up is the case that survives it
    /// whole, and the two explosions both use it.
    /// </summary>
    private void Quake(Shook what, Vehicle v, Vector2 along)
    {
        Tremor how = ShakeOf(what);
        if (!how.On || Shake is not { } view)
            return;
        view.Fire(along, v.Profile.ShotShake * how.Kick);
        if (how.Ring > 0.0)
            view.Blast(v.Profile.ShotShake * how.Ring);
    }

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
    /// A hull going into deep water: where it went in (the contact point, in the
    /// board's 2D px), how high the water stands there, and how hard - the
    /// <see cref="Stage3D.Splash"/> arguments, which is what the stage answers
    /// this with. Once per entry, on the frame <see cref="Vehicle.Wading"/> turns
    /// true on a deep cell; a ford raises no plume, because a hull wading in is
    /// a hull driving and the bow wave is already its picture.
    ///
    /// The strength is the class's mass, not its speed: the water's cap has
    /// every class entering at about the same speed, and what the pond answers
    /// is how much hull went in. docs/swim-plan.md, step 3.
    /// </summary>
    public Action<Vehicle, Vector2, float, float>? Plunged;

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

    /// <summary>
    /// A high-explosive round bursting on the face of a plate: what the board
    /// makes of it.
    ///
    /// <b><see cref="Bounced"/>'s twin, and the pair is the ammunition.</b> AP
    /// either gets in or comes off; HE does neither - it stops on the face and
    /// bursts there whether or not the plate held. So this fires on the round
    /// rather than on the damage, which is why it is a separate hook and not a
    /// second case inside the bounce: they are chosen by different questions, and
    /// the one asked first is the shell's own kind.
    ///
    /// Handed the same three things <see cref="Bounced"/> is and out of the same
    /// two measurements - the point of impact in the victim's own frame, the
    /// direction unnormalised, and which side of the hull it is on. The direction
    /// is the difference and it is the whole difference: a mirror there
    /// (<see cref="Vehicle.Graze"/>), the plate's own outward normal here
    /// (<see cref="Vehicle.Blown"/>).
    ///
    /// Answered by whichever root draws a board that can hold the quad, and
    /// unanswered on the flat one for <see cref="Landed"/>'s reason - including
    /// that unanswered draws nothing rather than falling back, so that
    /// <c>--slam off</c> and "no board for it" do not become one state with two
    /// causes.
    /// </summary>
    public Action<Vehicle, Vector2, Vector2, bool>? Blasted;

    /// <summary>
    /// A tank's ammunition going off - the detonation that ends a hull rather
    /// than one that lands on it. See <see cref="ProcRack"/>.
    ///
    /// <b>Raised from <see cref="Kill"/> and nowhere else, which makes it the one
    /// effect on this board that is not on a shell's trigger.</b> Every other
    /// picture here answers a round arriving or leaving; this one answers a state
    /// change, and the state is what it hands over to - see
    /// <see cref="Wreck.RiseSeconds"/>.
    ///
    /// Handed the victim, where its turret ring sits above its feet in screen px,
    /// and how big the detonation is. <b>The deck rather than a plate</b>: nothing
    /// about this event depends on where the last round hit, only on where the
    /// hull's openings are, and <c>anchor_px</c> is the ring by construction. See
    /// <see cref="ProcRack.Aim"/>.
    ///
    /// <b>The size is the tank's own drawn scale, and a measured hull length is
    /// the wrong lever on these assets rather than a better one.</b> That was the
    /// first plan: a hull that fills more of a cell holds more ammunition, which
    /// is a measurement instead of a table. <see cref="AtlasSet.HullSpan"/>'s own
    /// note says why it cannot be - LTP, MTP and HTP come out 188, 183 and 182px
    /// broadside, so all three models are rendered the same physical size and the
    /// heavy is fractionally the <em>smallest</em>. Whatever difference the
    /// classes have on this board is <see cref="MovementProfile.Size"/>'s doing,
    /// so the size a tank is drawn at is not a proxy for how big it is - it is
    /// the only statement of it there is.
    ///
    /// Answered by whichever root draws a board that can hold the quad, and
    /// unanswered on the flat one for <see cref="Landed"/>'s reason.
    /// </summary>
    public Action<Vehicle, Vector2, float>? Detonated;

    /// <summary>
    /// A tank knocked out: the flash at the ring that dropped the turret -
    /// <see cref="Detonated"/>'s small twin, same arguments, same three hooks.
    ///
    /// <b>Because the pose moves and nothing was seen to move it.</b> On the
    /// hit that knocks a tank out the turret drops into its ring and the belts go
    /// slack on the same frame, and until this there was no event on screen for
    /// them to be the aftermath of - a pose that changes by itself reads as the
    /// atlas swapping frames. The blast that did it is small, inside the hull,
    /// and shows at the ring: the same fireball as the death's at a third of
    /// the size (<see cref="KnockOutFlash"/>), so the two events are one family
    /// and their sizes say which was which.
    /// </summary>
    public Action<Vehicle, Vector2, float>? Flashed;

    /// <summary>The knock-out flash as a share of the death's fireball, on top
    /// of the hull's own drawn scale.</summary>
    public const float KnockOutFlash = 0.34f;

    /// <summary>
    /// A mine going off under a hull - <see cref="UpdateMines"/>.
    ///
    /// <b>Handed a point on the board rather than a cell</b>, which is the whole
    /// difference between this and a round put into a hex: a mine lies
    /// somewhere in its cell (<see cref="Mines"/>) and goes off there, under
    /// the tracks, and the tank standing over it hides most of what it throws.
    /// That hiding is the effect and not a loss - it is what says the charge
    /// was under the tank rather than beside it - so the receiver draws an
    /// ordinary ground burst at the point and lets the hull cover it.
    ///
    /// <b>Handed the point and the way the gases leave</b>, on
    /// <see cref="Blasted"/>'s model and with its division of labour: the axis is
    /// measured here, once, and the root turns the point into an offset over the
    /// tank's foot, because that is the frame <c>Stage3D.Slam</c> seats a burst
    /// in. The flash is the board's after all - a mine goes off on the ground,
    /// and a flash drawn on the hull's own plate says a shell hit the tank. What
    /// keeps it clear of the hull is the burst's depth side rather than the layer
    /// order; the whole argument is in <c>Stage3D.Mine</c>.
    ///
    /// Answered by whichever root draws a board that can hold the quad, and
    /// unanswered on the flat one for <see cref="Landed"/>'s reason.
    /// </summary>
    public Action<Vehicle, Vector2, Vector2, float>? Mined;

    /// <summary>
    /// Which way the gases leave a charge that went off under a hull lying
    /// <paramref name="across"/> - the same side of it the camera is on.
    ///
    /// <b>They leave both ways and only one of the two can be seen.</b> A charge
    /// under a track is confined by the belly above it and sprays out from under
    /// both sides; the far side is behind the hull, and drawing it would be
    /// drawing a thing the tank is standing in front of. So the axis is folded
    /// onto the camera side - board y grows toward the camera,
    /// <c>Stage3D.World</c> - and what stands on the board is the half that shows.
    ///
    /// Unnormalised, like every ground direction handed to the board: the length
    /// the projection left is what <c>ProcSlam.Aim</c> reads as how flat the
    /// throw is, so an axis pointing at the camera arrives as the wide low bloom
    /// it ought to be.
    /// </summary>
    public static Vector2 MineAway(Vector2 across) =>
        across.Y < 0.0f ? -across : across;

    /// <remarks>
    /// <b>The length is the projection's and not a number of the mine's own</b>,
    /// and that was tried: <c>ProcSlam.Aim</c> reads it as how flat the throw is,
    /// and a charge confined under a hull has a story for being flat - it vents
    /// wide rather than jetting. Set to a third and to a full one and photographed
    /// at the same might, the two frames are the same picture. Under a hull the
    /// cone's width is hidden by the hull, which is the one part of this the story
    /// forgot, so the dial earned nothing and is not here.
    /// </remarks>

    /// <summary>
    /// The ring's fireball on a mine: none, and the nought is the argument.
    ///
    /// <b>It stood here at a tenth of the death's own, on the reasoning that a
    /// charge under the belly lifts a turret off its seat and that the flash is
    /// the event the dropped turret is the aftermath of.</b> The turret no longer
    /// drops - a mine breaks the running gear and leaves the mount alone, see
    /// <see cref="Wreck.Seated"/> - so the premise is gone and the flash goes with
    /// it: a fireball on a ring that is still holding its turret says a round got
    /// inside, which is the one thing a mine did not do. What carries the event is
    /// the burst on the ground, where the charge was.
    ///
    /// A named nought rather than a dropped argument, because the decision is
    /// worth keeping where the next person will look for it.
    /// </summary>
    public const float MineFlash = 0.0f;

    /// <summary>
    /// How big a mine's burst is, on <see cref="Ordnance"/>'s scale.
    ///
    /// <b>Under one, and both bounds were found on the bench.</b> What the rules
    /// give a mine is one knocked-out tank and nothing next door (GDD field.md,
    /// "мина поражает только танк на своём гексе"), so a burst reaching the next hex would
    /// draw a rule the game does not have. At a full one the flash was right and
    /// the dust was not: <c>ProcSlam</c>'s cloud lives a second and climbs while
    /// it spreads, and a cloud that size rising off a point under the belly came
    /// out as a film laid over the hull - the same complaint the two board-side
    /// attempts earned, arrived at from the other direction. Below this the flash
    /// stops carrying the event and the ring's little ball takes it over, which is
    /// the wrong way round for a charge that went off in the ground.
    /// </summary>
    public const float MineMight = 1.6f;

    /// <summary>How hard the hull is thrown by a mine, on
    /// <see cref="BodyPitch.Jolt"/>'s scale - a little over the turret landing,
    /// because this one comes from underneath.</summary>
    public const double MineJolt = 2.4;

    /// <summary>How hard a hull dropping a level off a bank is caught by the
    /// ground, on <see cref="BodyPitch.Jolt"/>'s scale. Two thirds of a mine's,
    /// and the other way round: a charge lifts the end it goes off under, a
    /// landing drives the nose down.</summary>
    public const double FallJolt = 1.6;

    /// <summary>What the view takes, as a share of this class's own gun shake.
    /// Well under a death: a mine is one charge under one tank.</summary>
    public const float MineShake = 1.3f;

    /// <summary>Which way the hull is thrown by a charge that went off
    /// <paramref name="along"/> pixels from the ring - positive toward the nose,
    /// as <see cref="Mines.Under"/> reports it.
    ///
    /// <b>The end over the blast is the end that comes up</b>, and
    /// <see cref="BodyPitch.Jolt"/> is signed the other way round - positive
    /// drives the nose <em>down</em> - so a mine caught with the nose is a
    /// negative jolt. Its own static because that inversion is the whole of the
    /// "which end" claim and is worth asserting without a board under it.</summary>
    public static double MineJoltFor(double along) =>
        along >= 0.0 ? -MineJolt : MineJolt;

    /// <summary>
    /// Whether a tank that dies draws its ammunition going off.
    ///
    /// <see cref="Slam"/>'s twin and its argument: the detonation stands on the
    /// board and there is no rendered layer for it at all, so off is the only A/B
    /// it can have. <c>--rack off</c> on both roots, and no panel row.
    ///
    /// <b>What off does <em>not</em> take back is the fire coming up</b>, and
    /// that is worth stating because the last two of these flags each needed the
    /// sentence. <see cref="Wreck.RiseSeconds"/> is a correction to a defect that
    /// existed before anything was drawn - a flame at full strength on the frame
    /// of death - and it stands whether or not this picture is drawn. Off is the
    /// A/B for the quad; the ramp is not on trial.
    /// </summary>
    public bool Rack = true;

    /// <summary>
    /// Whether a round that got through lights the hull from inside.
    ///
    /// <see cref="Slam"/>'s twin in shape and not in argument. The other three
    /// flags each swap a built picture for a rendered one, so off means "the
    /// picture this used to have"; there is no rendered twin of this layer at all,
    /// so off means the rendered pair on its own - which is exactly the picture a
    /// penetration had, and the only A/B available.
    ///
    /// <b>It gates the fact rather than the layer</b>, which is what keeps one
    /// answer in one place: with it off a hit is simply not recorded as having got
    /// in, so nothing downstream has to know about a flag. The damage is not
    /// touched - <see cref="Gunnery.Penetration"/> decided that above this and
    /// the plate keeps what it earned.
    /// </summary>
    public bool Pierce = true;

    /// <summary>
    /// Whether a killing blow on <paramref name="face"/> detonates the hull.
    ///
    /// <b>Always, since 2026-09-08: the rules have one death.</b> GDD states.md,
    /// "Уничтожение — это всегда взрыв": however a hull is finished - shot as a
    /// wreck, burnt out, mined - the picture is the same detonation, and it is
    /// the detonation that does the rules' work next door (fire on the six
    /// same-level neighbours, the wood, the walls on the edges - see
    /// <see cref="Playback.Destroy"/>). A quieter fuel death off the rear plate
    /// was this project's own fork, argued in docs/blast.md; it read as a claim
    /// about where things are in a tank, and the rules do not make it. The plate
    /// is still taken, because the fork's other half - the scar the plate keeps -
    /// is measured the same way and <see cref="Wreck.Racked"/> keeps the answer.
    /// </summary>
    internal static bool RackedBy(string? face) => true;

    /// <summary>
    /// Whether an HE round draws the burst on the plate at all.
    ///
    /// <see cref="Bounce"/>'s twin and its argument word for word: the built
    /// burst stands on the board and the rendered pair stands in the tank's
    /// canvas, so off is the only A/B this layer can have. <c>--slam off</c> on
    /// both roots, and no panel row.
    ///
    /// <b>One place it is not <see cref="Bounce"/> word for word, and it is worth
    /// the sentence.</b> Off does not force the rendered pair - it drops the round
    /// back down the damage fork, which is where an HE round went when the
    /// ammunition meant nothing against armour. So an HE round with this off
    /// bounces if it did not get in and draws the rendered pair if it did, which
    /// is exactly the picture the bench had before this layer, and that is what an
    /// A/B has to hand back.
    /// </summary>
    public bool Slam = true;

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
        {
            // An order taking over from a reverse: the backing out of a push is
            // abandoned where it stands, because an order is the tank driving
            // itself again - CancelOrder says the same thing for the other way
            // in, and a belt left reversed would wind backwards under a tank
            // going forwards.
            v.Backing = null;
            v.Dwell = 0.0;
            AdvanceOrder(v, delta);
        }
        else if (v.Backing is not null)
            AdvanceBacking(v, delta);
        else if (v.Pitch.Moving || v.Speed != 0.0 || v.Sprite.Shake != 0.0)
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
        UpdateMines(v);
        UpdateWood(v);
        UpdateWreck(v, delta);
        // After the wreck's clock, because that is what moves a standing hull:
        // a drowning lets it down to the bed frame by frame, and the bob is the
        // other thing that does - see Settle.
        v.Bob.Update(delta);
        Settle(v);
        UpdateBurn(v, delta);
        // Before the scan, which it takes the turret away from, and before the
        // gunnery, which takes the turret away from it - the ring has three
        // possible drivers and they run in order of who yields to whom.
        UpdateTurret(v, delta);
        // The other axis of the same gun, driven on the same frame - see
        // UpdateTube. Beside the ring rather than inside the gunnery because
        // every root runs this and only one of them has an attack loop.
        UpdateTube(v, delta);
        UpdateReload(v, delta);
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
    /// Whether this hull is drowned rather than knocked out - GDD states.md: a
    /// stalled engine in deep water.
    ///
    /// <b>Read off the cell, not off a flag on the hull</b>, for the reason the
    /// grey column off the ports already is: it is the water that does it, and a
    /// flag would be a second copy of a fact the board already holds. Dead is
    /// excluded because a destroyed hull was destroyed by something - it lost
    /// its turret to a round and the water did not put that back.
    /// </summary>
    public bool Drowning(Vehicle vehicle) =>
        DrownedAt(vehicle.Wreck.Disabled, vehicle.Wreck.Dead,
                  Field is not null && Field.IsDeep(vehicle.Cell));

    /// <summary>The same question asked of the bare facts, so a check can be
    /// built on it rather than on a second copy of the predicate - the reason
    /// <see cref="Vehicle.WadingAt"/> is a named static too.</summary>
    public static bool DrownedAt(bool disabled, bool dead, bool deep) =>
        disabled && !dead && deep;

    /// <summary>How long a hull takes to go under, in seconds - from the engine
    /// stopping to the water closing over it.</summary>
    public const double SinkSeconds = 2.4;

    /// <summary>
    /// How far under a drowned hull has got, nought to one: nought on the frame
    /// the engine stops, one once the water is over the last of it.
    ///
    /// <b>Depth is drawn as size, and the hull does not move at all.</b> This
    /// board is orthographic and a screen row is a place on the ground, so a
    /// hull sent down the water column has nowhere on screen to go: rows spent
    /// on depth walk the tank off its own hexagon. That was the mechanism this
    /// replaces - a settle of 13px, read off the board as "the unit is not on
    /// its hex" and trimmed twice before it was dropped. So a drowning hull
    /// stands on its cell's anchor to the pixel like every other unit, and what
    /// says it is going down is that it is drawn smaller while the water closes
    /// over it - see <c>Stage3D.Sunken</c>, and docs/water.md.
    ///
    /// <b>Derived from the wreck's own clock, so there is still no third state
    /// to set or forget.</b> Drowned is a stalled engine in deep water and
    /// nothing else (<see cref="DrownedAt"/>); how far it has got is
    /// <c>Wreck.OutAge</c>, the same age the char, the smoulder and the flare
    /// are shapes of. That age is the drowning's own, because the only way to be
    /// stopped in deep water is to have stopped there: a disabled hull does not
    /// drive in.
    /// </summary>
    public static float SunkAt(bool disabled, bool dead, bool deep,
                               double outAge) =>
        !DrownedAt(disabled, dead, deep) ? 0.0f
            : Mathf.SmoothStep(0.0f, (float)SinkSeconds, (float)outAge);

    /// <summary>The same question asked of a tank on this board - the reason
    /// <see cref="Drowning"/> sits beside <see cref="DrownedAt"/>.</summary>
    public float Sinking(Vehicle vehicle) =>
        SunkAt(vehicle.Wreck.Disabled, vehicle.Wreck.Dead,
               Field is not null && Field.IsDeep(vehicle.Cell),
               vehicle.Wreck.OutAge);

    /// <summary>How deep this hull sits in water when it floats, in screen px:
    /// the class's <see cref="MovementProfile.Draught"/> of its deck's height
    /// (<see cref="AtlasSet.DeckHeightPx"/>; the whole height range on a set
    /// with no height map, where nothing is drawn wet anyway), at the size it
    /// is drawn.</summary>
    public float Draught(Vehicle v) =>
        v.Atlas is null ? 0.0f
            : (float)(v.Profile.Draught * DraughtScale
                      * (v.Atlas.DeckHeightPx > 0.0 ? v.Atlas.DeckHeightPx
                                                    : v.Atlas.HeightSpanPx))
              * v.Sprite.BodyScale;

    /// <summary>A dial over every class's <see cref="MovementProfile.Draught"/>
    /// at once - the bench's way of trying a line before it is written into the
    /// class (<c>--draught</c>, the panel). One so the five keep their ratio;
    /// the ratio is the authored part.</summary>
    public double DraughtScale = 1.0;

    /// <summary>
    /// How far above a cell's face a live hull rides there, in screen px: nought
    /// on any cell but deep water, and there <c>surface - draught - face</c>,
    /// floored at the drawn bed - so nought or less when the pond is shallower
    /// than the hull sits.
    ///
    /// <b>The swimmer stands on its water, not on the bed</b> - GDD states.md,
    /// "на уровне своей воды" - and this is the one place that says by how much.
    /// The old picture parked it on the bed and ruled its waterline to the deck
    /// because measured against the pond it was under entire; floated, the
    /// waterline is a ford's scan again and the deck rule is left to the drowned
    /// (see Stage3D, the shore table). Floored at the <b>drawn</b> bed
    /// (<see cref="Bottom"/>): a pond shallower than the draught is a ford with
    /// a different name, the hull stands on the bottom and the water comes up
    /// it as far as it comes. Measured on the events board at the default size:
    /// the pond stands 39px over its level and a medium's draught wants 48, so
    /// with the bed drawn at the level the water reached seven tenths of the
    /// hull and no higher, whatever the class said. The bed drawn under the
    /// level (<see cref="HexField.DeepBed"/>) is what lets the hull sit as low
    /// as its class says - the reason that knob's default is not nought.
    /// docs/swim-plan.md, step 2.
    /// </summary>
    public float Buoyancy(Vehicle v, Vector2I cell) =>
        Field is null || v.Atlas is null || !Field.IsDeep(cell) ? 0.0f
            : RideAt(Field.WaterTop(cell), Draught(v), Field.TopAt(cell), Bottom(cell));

    /// <summary>The same question asked of bare heights, so the check can be
    /// built on it - <see cref="Vehicle.WadingAt"/>'s reason: a required-member
    /// type cannot be conjured in a test. Surface less draught, over the face,
    /// floored at the drawn bottom (nought or less).</summary>
    public static float RideAt(float waterTop, float draught, float face, float bottom) =>
        Mathf.Max(bottom, waterTop - draught - face);

    /// <summary>Where the drawn bottom of a cell is against its face, in screen
    /// px - nought or less. Nought on any cell but flat deep water, whose bed
    /// may be drawn under its level (<see cref="HexField.BedAt"/>); a ramp into
    /// the pond keeps its face, because a hull on a ramp is on its tracks.</summary>
    public float Bottom(Vector2I cell) =>
        Field is null || !Field.IsDeep(cell) || Field.RampHeading(cell) >= 0 ? 0.0f
            : Mathf.Min(0.0f, Field.BedAt(cell) - Field.TopAt(cell));

    /// <summary>What a hull rides at on a cell now: its <see cref="Buoyancy"/>
    /// while it floats, the drawn <see cref="Bottom"/> once it has drowned, and
    /// the way between the two over <see cref="SinkSeconds"/> - the same clock
    /// <see cref="SunkAt"/> shrinks it by, so the two say one thing.</summary>
    public float Afloat(Vehicle v, Vector2I cell) =>
        Mathf.Lerp(Buoyancy(v, cell), Bottom(cell), Sinking(v));

    /// <summary>The ride with its bob on it - what the hull is actually drawn
    /// at over a cell: the spring's offset, and the swell under a hull that
    /// floats (<see cref="Buoy.Idle"/>) for as long as it floats - a drowned
    /// hull lies on the bed and the swell is not its. Both nought on dry land
    /// by construction: the spring is kicked only going into deep water and
    /// runs down from there, and the swell is added only there.</summary>
    public float Ride(Vehicle v, Vector2I cell) =>
        Afloat(v, cell) + (Field is not null && Field.IsDeep(cell)
                               ? (float)v.Bob.Offset
                                 + (Sinking(v) <= 0.0f ? (float)v.Bob.Idle : 0.0f)
                               : 0.0f);

    /// <summary>How hard a hull of this class hits the water going in, as the
    /// plume's might: a light's splash at seven tenths of a shell's, a heavy's
    /// at one and a fifth. Mass and not speed - see <see cref="Plunged"/>. Nine
    /// tenths was the first cut and the mortar's column stood two hulls tall,
    /// which is a round going off and not a hull going in.</summary>
    public static float PlungeMight(Vehicle v) =>
        0.7f + 0.25f * Math.Max(0, v.Profile.Mass - 1);

    /// <summary>The kick the bob gets on the way in, px/s downward. One number
    /// for every class: the fall is the same height for all of them, and the
    /// water takes what it takes.</summary>
    public const double PlungeKick = -70.0;

    /// <summary>How much of the plunge a hull wading in down the ramp gets -
    /// splash and kick both. Two fifths: enough to say the water was entered,
    /// not enough to say anything fell.</summary>
    public const float RampWash = 0.4f;

    /// <summary>
    /// Move a standing hull to where it rides now, when that has changed under it
    /// - a drowning letting it down, the depth slider moving the water. Nothing
    /// to do on dry land or for a hull mid-leg, whose ride <see cref="Climb"/>
    /// sets every frame. Moves by the difference so the lean, the rumble and the
    /// rest of what sits on the sprite are untouched.
    /// </summary>
    private void Settle(Vehicle v)
    {
        if (v.Moving || Field is null)
            return;
        float shift = Ride(v, v.Cell) - v.Float;
        if (Mathf.Abs(shift) < 1e-3f)
            return;
        v.Float += shift;
        v.Height += shift;
        v.Standing += shift;
        v.Trailing += shift;
        v.Sprite.Position -= new Vector2(0.0f, shift);
        Depth(v);
        v.Sprite.QueueRedraw();
    }

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
        //
        // <b>And the same number for both, on every cell there is.</b> Height is
        // where the hull is in the world and Standing is how high it counts as
        // standing; they part on a climb and nowhere else. Deep water used to
        // part them too - the hull was sunk into the bed by a settle and had to
        // go on counting as standing on the cell, or the neighbouring bed ate
        // it - and it does not any more: a drowning hull is drawn smaller rather
        // than lower, and stays exactly where it stood. See TankTick.SunkAt.
        vehicle.Height = Field.TopAt(vehicle.Cell);
        vehicle.Standing = Field.TopAt(vehicle.Cell);
        // A tank standing on a cell stands at its centre, and there the chord and
        // the surface are the same point - but the clearance is still owed, because
        // marks are laid on the frame a leg ends too. See HexField.MarkAt.
        vehicle.Ground = Field.MarkAt(vehicle.Cell);
        // Not cleared but asked, for the reason OnSlope below is: parking is where
        // a tank comes to rest in a ford, and that is the case this exists for.
        vehicle.Waterline = Field.IsWater(vehicle.Cell)
            ? Field.WaterTop(vehicle.Cell) : float.NegativeInfinity;
        // Without the wading gear, deep water is where the engine stops - GDD
        // states.md "Утоплен": the tank is in the pond and it is drowned, and
        // whether it drove in or was put there does not come into it. Asked here
        // because the cell is what Drowning reads: a hull stopped halfway across
        // the seam would still count as on the bank. No flash and the turret
        // seated - nothing arrived, the water did it (Playback.Sink's reason).
        if (!Amphibious && Field.IsDeep(vehicle.Cell)
            && !vehicle.Wreck.Disabled && !vehicle.Wreck.Dead)
            Disable(vehicle, 0.0f, true);
        // And how far above that face it rides - nought everywhere but afloat in
        // deep water, see Afloat. Into Height and Standing both, so the depth
        // order and the water shader (Waterline - Height is the draught) read the
        // ride, and off the drawn position below by the same number.
        float afloat = Ride(vehicle, vehicle.Cell);
        vehicle.Float = afloat;
        vehicle.Height += afloat;
        vehicle.Standing += afloat;
        // The frame it went in is over by the time it is parked - Climb saw it -
        // or it was put here, and a put is not a plunge. Either way this is the
        // memory for the next leg.
        vehicle.WasWading = vehicle.Waterline > vehicle.Height;
        // A put into any water douses too - GDD field.md, water of any depth -
        // and here rather than in the caller because it is the water that does
        // it. Douse, not Burning = false: somebody put it here to see it.
        if (vehicle.WasWading && vehicle.Burning)
            vehicle.Douse();
        if (Field.IsDeep(vehicle.Cell) && vehicle.Atlas is not null)
            GD.Print($"tick: {vehicle.Tag} on ({vehicle.Cell.X},{vehicle.Cell.Y}) "
                     + $"rides {afloat:F1}px above the bed: water "
                     + $"{Field.WaterTop(vehicle.Cell) - Field.TopAt(vehicle.Cell):F1}px "
                     + $"over it, draught {Draught(vehicle):F1}px of a "
                     + $"{vehicle.Atlas.DeckHeightPx * vehicle.Sprite.BodyScale:F1}px "
                     + $"deck ({vehicle.Atlas.HeightSpanPx * vehicle.Sprite.BodyScale:F1}px "
                     + $"hull), Lift {Field.Lift:F1}px"
                     + (Drowning(vehicle) ? ", drowning" : ""));
        vehicle.Trailing = vehicle.Height;
        vehicle.Travel = Vector2.Zero;
        vehicle.Levelling = false;
        // Not cleared but asked: parking is where a tank comes to rest on a ramp,
        // and that is the case this exists for.
        vehicle.OnSlope = Field.IsRamp(vehicle.Cell);
        // Up by the ride, so the card stands on the water it is counted as
        // standing on - Stage3D.Contact puts the flat row back from Standing.
        vehicle.Sprite.Position = StandOn(vehicle, vehicle.Cell)
                                  - new Vector2(0.0f, afloat);
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
        // <b>A shoved hull's belts do not turn.</b> The phase is read off the
        // distance covered, and the distance a pushed tank covers is not its
        // own: belts wound forward under a hull sliding sideways read as the
        // tank driving itself off the hex it was just knocked out of. Locked,
        // it skids, which is what it is doing. The mark under it is still laid -
        // see TrackMarks.Lay, which takes its place from the hull rather than
        // from the belts.
        // Backing out of a push winds the same belts the other way - see
        // BackOff. Signed rather than a case: the loop and the sound both take
        // this one number, so a tank reversing cannot be heard going forwards.
        if (v.Backing is not null)
            return TrackLoop.Split(-v.Speed * delta, swing, radius);
        if (v.Shoved is not null)
            return (0.0, 0.0);
        return TrackLoop.Split(v.Speed * delta, swing, radius);
    }

    /// <summary>The radius each belt winds about when the hull turns - half the
    /// gauge, on screen.</summary>
    private static double PivotArm(Vehicle v) =>
        (v.Atlas.TrackArm > 0.0
            ? v.Atlas.TrackArm
            : v.Atlas.HullSpan * PivotRadiusFraction) * v.Sprite.BodyScale;

    /// <summary>
    /// Stopped where it stands rather than on a cell - what a ram does at the
    /// moment the hulls meet.
    ///
    /// <b>The order comes off and the drawn position stays.</b> Parking would put
    /// the tank back on the middle of the cell it came from, a full hex from the
    /// hull it just hit, and a ram whose picture is two tanks a cell apart is a
    /// ram that reads as never having happened. So the sprite is left where the
    /// leg had got it: nose against the other tank, which is what a collision
    /// looks like and where the collision was.
    ///
    /// <b>The leg's own bookkeeping is cleared even so, and that is the whole
    /// reason this is not just a cancel.</b> LegDone is what the position is
    /// derived from while a leg runs, so a leg abandoned half way and left at
    /// half would make the next order begin half way along itself - the trap
    /// <see cref="Park"/> names. LegBlend goes with it because the two faces it
    /// mixes are the cell this tank is still standing in and one it never
    /// reached.
    ///
    /// Everything else Park writes - the heights, the waterline, the slope - is
    /// left as the leg had it, exactly as <c>ESC</c> leaves it: they are facts
    /// about where the tank is, and where it is is here.
    /// </summary>
    public void Halt(Vehicle v)
    {
        CancelOrder(v);
        v.LegDone = 0.0f;
        v.LegBlend = 0.0f;
        v.Travel = Vector2.Zero;
        v.Levelling = false;
    }

    /// <summary>
    /// A tank thrown off its hex by a ram - GDD units.md, "Таран", and
    /// docs/ram-plan.md.
    ///
    /// <b>A leg, not a place.</b> The victim could simply be put on the hex
    /// behind it, and that is what every other event on the bench does with a
    /// state; a ram is the one event whose whole content is the movement, and
    /// what the hex it lands on does to it - a mine under the belly, a bank to
    /// fall off, a ramp to run out down - is what a leg already asks on its own
    /// (<see cref="UpdateMines"/>, <see cref="Climb"/>). So the throw is an
    /// order like any other, with two differences: nobody gave it, and the hull
    /// does not turn to face it - see <see cref="Vehicle.Shoved"/>.
    ///
    /// <b>It carries the rammer's speed and brakes itself.</b> The ceiling in
    /// <see cref="AdvanceOrder"/> runs down to nought over what is left of the
    /// path, so a hull shoved at the speed of the hull that hit it comes to rest
    /// on its new cell without a number of its own; capped by the victim's own
    /// <see cref="SpeedCap"/> because the ground under it is the victim's ground.
    /// </summary>
    public void Shove(Vehicle victim, IReadOnlyList<Vector2I> legs, int heading,
                      double speed)
    {
        if (legs.Count == 0)
            return;
        victim.Path = new List<Vector2I>(legs);
        victim.PathStep = 0;
        victim.Shoved = heading;
        victim.LegDone = 0.0f;
        victim.LegBlend = 0.0f;
        // After the path, because the cap is asked of the leg being driven.
        victim.Speed = Math.Min(speed, SpeedCap(victim));
    }

    /// <summary>How long the two hulls stand against each other before the
    /// pusher backs off, in seconds. A beat rather than a pause: hulls that part
    /// on the frame they stop never look as though they had been touching.
    /// </summary>
    public const double RamDwell = 0.22;

    /// <summary>How fast a hull backs out of a push, as a fraction of its own
    /// cruise. Reverse is the slower gear on anything tracked and a third is the
    /// shape of it - measured against the crawl, which was the first thing tried:
    /// <see cref="MovementProfile.CornerFraction"/> is 12%, a pivot's speed, and
    /// at 21px/s the heavy spent longer backing a third of a leg out than it had
    /// spent pushing the whole hex.</summary>
    public const double RamBack = 0.35;

    /// <summary>The hull <paramref name="v"/> has its nose against, or null when
    /// it is pushing nobody.
    ///
    /// Read off the pushed hull rather than kept on both of them, because two
    /// fields pointing at each other are one fact plus a future disagreement -
    /// the reason the mass table is not written twice either. The list is five
    /// long on the biggest board there is.</summary>
    public Vehicle? Pushed(Vehicle v)
    {
        foreach (Vehicle other in Vehicles)
            if (ReferenceEquals(other.Shover, v))
                return other;
        return null;
    }

    /// <summary>
    /// A push in progress, frame by frame: one speed for the two hulls, and the
    /// end of it.
    ///
    /// <b>Nothing is drawn from here (2026-09-18).</b> The pair used to throw
    /// metal off the seam for as long as they were touching - see
    /// <see cref="RamContacts"/>, which now holds the whole of what a ram draws.
    /// A push is a state, and a state that keeps emitting is the one shape this
    /// effect must not take.
    ///
    /// <b>The pusher drives and the pushed one is carried.</b> Both still run
    /// legs of their own - that is what gives the shoved tank its mine, its bank
    /// and its ramp for nothing - but the speed is the pusher's, converted into
    /// the leg's own units: a leg is crossed in its own span of pixels, so a
    /// victim whose leg is the shorter of the two (a step on to a ramp) is given
    /// proportionally less of it and the pair stay exactly as far apart as they
    /// were at the moment they touched. That distance is
    /// <see cref="RamContact"/> of a leg, and holding it is what keeps the two
    /// silhouettes overlapping for the whole hex instead of one leaving the
    /// other behind.
    ///
    /// <b>It ends when the shoved hull arrives, and only its first leg is
    /// pushed.</b> A shove that runs out down a ramp is two legs, and nothing
    /// pushes a tank down a slope: the second one it takes on its own.
    /// </summary>
    public void Pushes(double delta)
    {
        foreach (Vehicle victim in Vehicles)
        {
            if (victim.Shover is not Vehicle pusher)
                continue;
            // <b>Letting go is not the same ending as arriving.</b> The hull
            // behind may have been given somewhere else to be, been stopped, or
            // been knocked out, and the test for all three is whether it is
            // still driving into the tank in front of it. Then the field simply
            // clears: the shoved hull keeps the legs it was thrown along and
            // finishes them on its own engine, and nobody backs out of anything.
            if (pusher.Wreck.Out || !pusher.Moving
                || (pusher.Onto != victim.Cell && pusher.Onto != victim.Onto))
            {
                victim.Shover = null;
                continue;
            }
            // Arriving: the shoved hull is on the hex it was thrown at, or has
            // started the second leg of a slide, which nothing is pushing it
            // down. This is the ending the reverse belongs to.
            if (!victim.Moving || victim.PathStep > 0 || victim.Wreck.Dead)
            {
                victim.Shover = null;
                BackOff(pusher);
                continue;
            }
            double span = LegSpan(pusher);
            victim.Speed = span > 0.001
                ? pusher.Speed * LegSpan(victim) / span
                : pusher.Speed;
        }
    }

    /// <summary>The flat length of the leg a tank is driving, which is the unit
    /// its progress is counted in - see <see cref="AdvanceOrder"/>, where the
    /// frame's budget is spent against exactly this.</summary>
    private double LegSpan(Vehicle v) =>
        !v.Moving
            ? 0.0
            : (Field.FlatAnchor(v.Onto.X, v.Onto.Y)
               - Field.FlatAnchor(v.Cell.X, v.Cell.Y)).Length();

    /// <summary>
    /// The end of a push: the pusher comes off the hull it shoved.
    ///
    /// Three endings, told apart by how far the pusher got rather than by a flag,
    /// because the path answers it either way:
    ///
    /// <list type="bullet">
    /// <item><b>A leg to spare</b> - it never crossed on to the hex it cleared.
    /// The extra cell was only ever somewhere for the nose to be, so it comes off
    /// and the order ends where the player gave it.</item>
    /// <item><b>On that extra leg</b> - the ordinary ending: a third of a leg
    /// past the middle of the victim's hex, which it now reverses out of.</item>
    /// <item><b>Not under way at all</b> - something else has stopped it already,
    /// and there is nothing to undo.</item>
    /// </list>
    /// </summary>
    private void BackOff(Vehicle v)
    {
        if (!v.Moving)
            return;
        if (v.Path.Count - v.PathStep > 1)
        {
            v.Path = v.Path.GetRange(0, v.PathStep + 1);
            return;
        }
        Vector2I ahead = v.Path[v.PathStep];
        float done = v.LegDone;
        CancelOrder(v);
        v.LegDone = done;
        v.Backing = ahead;
        v.Dwell = RamDwell;
    }

    /// <summary>
    /// A hull reversing the last third of a leg, out of the hull it has just
    /// finished pushing - <see cref="Vehicle.Backing"/>.
    ///
    /// <b>The same leg, run backwards.</b> <see cref="Vehicle.LegDone"/> is what
    /// the drawn position is interpolated from, so backing out is that number
    /// running down: the height, the waterline and the lean go on coming off the
    /// same pair of cells they already came from, and the hull ends parked on the
    /// cell it has been standing in all along, with nothing to put back.
    ///
    /// <b>In its own reverse gear</b> - <see cref="RamBack"/>, a fraction of the
    /// class cruise like every other figure that moves a hull. The cornering
    /// crawl was tried first and is what named the number: at 12% of cruise the
    /// heavy took longer to back a third of a leg out than it had taken to push
    /// the whole hex.
    ///
    /// The nose dips as it pulls back, which is the acceleration ratio with its
    /// sign turned over: reversing throws the weight the opposite way from
    /// pulling away, and the spring takes the same channel either way - see
    /// <see cref="UpdatePitch"/>.
    /// </summary>
    private void AdvanceBacking(Vehicle v, double delta)
    {
        if (v.Backing is not Vector2I ahead)
            return;
        // The beat the two hulls spend standing against each other - see
        // Vehicle.Dwell.
        if (v.Dwell > 0.0)
        {
            v.Dwell -= delta;
            v.Speed = 0.0;
            UpdatePitch(v, 0.0, delta);
            UpdateRumble(v, delta);
            v.Sprite.QueueRedraw();
            return;
        }
        double before = v.Speed;
        float span = (Field.FlatAnchor(ahead.X, ahead.Y)
                      - Field.FlatAnchor(v.Cell.X, v.Cell.Y)).Length();
        // Braked by a ceiling rather than by a pedal, which is AdvanceOrder's
        // rule and its reason: what is left of the reverse is LegDone of the
        // leg, and a ceiling that runs down with it finishes the deceleration
        // instead of having it cut off by arrival. Without it the hull came off
        // a third of a hex at 61px/s and stopped in one frame.
        double ceiling = Math.Sqrt(2.0 * v.Profile.Accel
                                   * Math.Max(v.LegDone * span, 0.0));
        v.Speed = Math.Min(Math.Min(v.Profile.TopSpeed * RamBack, ceiling),
                           v.Speed + v.Profile.Accel * delta);
        double ratio = delta > 0.0
            ? Math.Clamp((v.Speed - before) / (v.Profile.Accel * delta), -1.0, 1.0)
            : 0.0;
        float step = span > 0.001f ? (float)(v.Speed * delta) / span : 1.0f;
        v.LegDone -= step;
        if (v.LegDone <= 0.0f)
        {
            v.LegDone = 0.0f;
            v.Backing = null;
            v.Speed = 0.0;
            Park(v, placed: false);
        }
        else
        {
            v.Sprite.Position = StandBetween(v, v.Cell, ahead, v.LegDone);
            Climb(v, ahead);
            Depth(v);
        }
        UpdatePitch(v, -ratio, delta);
        UpdateRumble(v, delta);
        v.Sprite.QueueRedraw();
    }

    /// <summary>
    /// How far into its last leg a tank has to be for the hulls to be touching,
    /// as a fraction of that leg.
    ///
    /// <b>A third, and it was read off the pictures rather than worked out.</b>
    /// Four frames of one approach, captured at a tenth, a quarter, a third and
    /// four ninths of the last leg: at a tenth there is open ground between the
    /// two silhouettes, at a third they just meet, and by four ninths the rammer
    /// is drawn over the hull it is hitting. So a third.
    ///
    /// <b>It was written at a half from arithmetic, and both numbers in that
    /// arithmetic were wrong.</b> The sum was "hulls are 191 to 212px long
    /// against a cell pitch of about 190, so they touch half way" - but the pitch
    /// is 216 flat px (measured off three craters ordered into one column, 108px
    /// apart on screen against a projection that halves the vertical), the hull
    /// length is a broadside figure that no approach is ever seen at, and neither
    /// of them is the thing that decides: what touches is two <em>silhouettes</em>
    /// on a diagonal, guns and turret overhang included. A number about what the
    /// eye sees has to be measured where the eye is.
    /// </summary>
    public const float RamContact = 1.0f / 3.0f;

    /// <summary>
    /// Whether a ram dents the two hulls instead of moving one of them - the
    /// stand's first ram, kept as the other side of an A/B.
    ///
    /// <b>Off, because the rules say a ram does no damage at all</b> - GDD
    /// units.md, "тараном танк только сдвигается, повреждений ни один из двух не
    /// получает". What the stand did until 2026-09-11 was the other model
    /// entirely: a level off <see cref="Gunnery.RamLevel"/> into each hull, three
    /// of them fatal. It is kept rather than deleted for the reason the rendered
    /// flash is kept beside the built one - "the shove reads better" has to stay
    /// a thing anybody can put side by side, not a thing this file remembers.
    /// <c>--ram-dents</c> restores it whole, halt and all.
    /// </summary>
    public bool RamDents;

    /// <summary>
    /// A hull arriving somewhere hard: one coming down off a bank, by its own
    /// drive or on the fall a ram threw it over.
    ///
    /// <b>The ram itself raises nothing here any more (2026-09-18)</b>, and what
    /// that cost is kept written down, because it is what a second attempt would
    /// have to beat. A cloud seated on a hull's own contact patch and blown along
    /// the ram said two things at once: on the standing rung the quad drew
    /// <em>across the armour</em> - a white veil over both tanks, the trap
    /// <c>Stage3D.Mine</c> names word for word - and on the dress rung it was
    /// hidden under the hull that made it and might as well not have fired. Both
    /// are one fact: a hull is a billboard and the ground under its middle is not
    /// visible. Seating it on the seam and throwing it sideways answered that and
    /// spent the volume instead - what came out was quieter than two hulls
    /// meeting wants, and the loudness needed a depth side ProcKick has not got.
    /// So the collision is carried by the metal, which has one, and this hook
    /// keeps the event it was never ambiguous for: a landing.
    ///
    /// The spot is a board point rather than the vehicle's own, because that is
    /// the whole of what this hook had to learn. The vehicle is still handed over
    /// for the two things only it knows: how high the ground is under that point
    /// and how big the hull is.
    /// </summary>
    public Action<Vehicle, Vector2, Vector2>? Bumped;

    /// <summary>How much cloud a hull coming down off a bank throws, against a
    /// gun's round of the reference calibre. Named for the ram because the ram
    /// is what it was measured on - a hull at walking pace moves about as much
    /// ground as a shot's own blast does - and left at that measurement now the
    /// ram raises none: the landing is the same picture and was never drawn at
    /// another size. Here rather than at the root so both roots throw the same
    /// dust.</summary>
    public const float RamKick = 0.5f;

    /// <summary>How much cloud a crown coming down throws, on
    /// <see cref="RamKick"/>'s scale and here for its reason. Much the smallest
    /// of the three, and it was three times this when it was one cloud for a
    /// whole hex thrown from the middle of it: what this is now is one crown
    /// hitting the ground at its own point, and a puff the size of a shot's
    /// reads as the shot rather than as the tree.</summary>
    public const float FellKick = 0.45f;

    /// <summary>
    /// The metal of a ram: scale struck off the plate the two hulls met on.
    ///
    /// <b><see cref="Bounced"/>'s shape to the letter, because it is
    /// <see cref="Bounced"/>'s picture on another trigger</b> - the point of
    /// impact in that hull's own frame, the direction unnormalised, which side of
    /// the hull it is on, and how big. What differs is inside the model and only
    /// there: a ricochet leaves along the mirror of the round that arrived
    /// (<see cref="Vehicle.Graze"/>) and a ram has no round, so the fan opens
    /// along the plate's own normal (<see cref="Vehicle.Blown"/>) and nothing
    /// carries on past it - <see cref="Stage3D.Scrape"/>.
    ///
    ///
    /// <b>Fired twice, once per hull, and both are seated where the hulls
    /// met.</b> The point handed over is a place on the board - the seam, lifted
    /// by a fraction of that hull's own span - rather than a point of the
    /// sprite, and the root sits the fan on it. It used to be seated on each
    /// tank's own foot, with the seam carried alongside as a plate offset, and
    /// measured that offset moves the fan about 25px where the seam is 60 away:
    /// one frame of contact forgave it, a push a hex long did not - the rammer's
    /// sparks came off its own flank, a hull behind the collision, and travelled
    /// with it. What the two fans still differ in is the plate: each hull is
    /// asked for the one that met the other, and the metal leaves along its own
    /// normal. Which side of its hull that is still decides the rung, which is
    /// what sorts the quad - see <see cref="Stage3D.Scrape"/>.
    ///
    /// <b>Before the rules are asked, with the dust and the shake.</b> Two hulls
    /// met; whether anything may then be shoved is about what follows, and a ram
    /// that is refused is still two tanks touching at speed.
    ///
    /// Answered by whichever root draws a board that can hold the quad, and
    /// unanswered draws nothing rather than falling back - <see cref="Bounced"/>'s
    /// reason, word for word.
    /// </summary>
    public Action<Vehicle, Vector2, Vector2, bool, float>? Sparked;

    /// <summary>
    /// How much of a ricochet's fan a ram of the reference class throws, and how
    /// high above the ground the two hulls take it, as a share of a hull's length.
    ///
    /// <b>The height is the only part of the contact point this has to invent.</b>
    /// Where the hulls meet is known exactly - it is the seam, halfway between two
    /// feet - and how far up that seam the metal grinds is not: the plates touch
    /// from the tracks to the top of the glacis. A fifth of a hull is the belt
    /// line, which is low enough to read as two hulls and not two turrets.
    /// </summary>
    public const float RamSpark = 0.75f;
    public const float RamSparkHigh = 0.20f;

    /// <summary>
    /// The fan that hull is worth. <b>The rammer's class for both of them</b>,
    /// because what arrives in a ram is a hull - <see cref="Gunnery.RamLevel"/>'s
    /// own sentence, and the reason the mass table is not read here instead: that
    /// table answers who moves, and a light hull that may not shift a heavy one
    /// still strikes the same scale off it. The medium is one and a class either
    /// side is an eighth of it.
    /// </summary>
    public static float RamSparkFor(MovementProfile hull) =>
        RamSpark * (1.0f + 0.125f * (hull.Mass - 2));

    /// <summary>
    /// What the two hulls leave the contact at, as a share of the speed the
    /// rammer arrived with.
    ///
    /// <b>One number for both of them, and that is what "they do not bounce"
    /// means.</b> Two hulls that stay in contact leave at one speed; conservation
    /// of momentum then makes that speed <c>m1 / (m1 + m2)</c> of what arrived,
    /// and the mass is <see cref="MovementProfile.Mass"/> - the class index, the
    /// only mass this board has and the one <see cref="Gunnery.RamLevel"/>
    /// already reads. Equals both come away at half, a heavy shouldering a light
    /// keeps three quarters, and neither of those is a number anybody chose.
    ///
    /// It says three things at once, which is why it is one expression: how much
    /// the rammer is checked, how fast the shoved hull goes, and - read as the
    /// share each of them changed speed by - how hard each is thrown on its
    /// springs (<see cref="RamJolt"/>).
    /// </summary>
    public static double RamKeep(MovementProfile rammer, MovementProfile victim) =>
        (double)rammer.Mass / Math.Max(rammer.Mass + victim.Mass, 1);

    /// <summary>
    /// How hard two hulls meeting throw each other, on
    /// <see cref="BodyPitch.Jolt"/>'s scale, at the moment both of them change
    /// speed by the whole of it.
    ///
    /// <b>The fall's number, because it is the fall's event</b> - a hull brought
    /// up hard by something that will not move for it. Shared out by
    /// <see cref="RamKeep"/>: each hull is thrown by what it lost or gained, so
    /// equals take four fifths each and a heavy shouldering a light nods at a
    /// quarter while the light is thrown at three.
    /// </summary>
    public const double RamJolt = 1.6;

    /// <summary>The tremble after the knock, as a share of the class's own gun -
    /// <see cref="DeathRumble"/>'s channel, well under it. The kick says the
    /// hulls met; this is the mass ringing afterwards, and a still frame cannot
    /// show it, so unlike everything else about the ram it is a ratio against the
    /// one other event on this channel rather than a measurement.</summary>
    public const double RamRumble = 0.6;

    /// <summary>
    /// The hulls meeting, for every tank that has been told to ram - the whole
    /// of what a ram does.
    ///
    /// <b>Resolved on the leg rather than on arrival, because there is no
    /// arrival.</b> The destination is a cell somebody is standing on: driven to
    /// the end it would put two tanks on one cell, and one cell holding two tanks
    /// is a board where <see cref="Vehicle.At"/> answers with whichever of them
    /// it met first - selection, pathing and gunnery all quietly picking one. So
    /// the ram lands at <see cref="RamContact"/> of the last leg, and by then the
    /// cell is being emptied: the victim is thrown out of it along the same
    /// heading (<see cref="Shove"/>) and the rammer drives the rest of its order
    /// into the hex it has just cleared. That is the rules' sentence -
    /// "таранящий въезжает на гекс цели и замещает её" - and it is also why
    /// nothing halts the rammer any more.
    ///
    /// <b>Whether it may is <see cref="Ramming"/>, and it is asked here rather
    /// than when the order was given.</b> A route is driven over seconds; the
    /// hex behind the target can be taken, vacated or set on fire in that time,
    /// and the only frame whose board matters is this one. A ram that may not
    /// happen is a hull stopping against another hull, which is what two tanks
    /// meeting looks like when nothing moves.
    ///
    /// <b>In the tick rather than in the harness</b>, because the event bench
    /// rams too - one contact rule for the gesture, the event and the tool, and
    /// no root that knows which of them called it.
    /// </summary>
    public void RamContacts(double delta)
    {
        // The pushes already under way, before any new contact - see Pushes.
        Pushes(delta);
        foreach (Vehicle v in Vehicles)
        {
            if (v.Charge is not Vector2I onto)
                continue;
            Vehicle? victim = Vehicle.At(Vehicles, onto);
            // Gone, dead, or arrived without ever touching anybody: the order is
            // spent either way. Cleared on standing still rather than kept, for
            // Main.OrderRam's reason - an intent that outlives its drive is a ram
            // delivered by some later order.
            if (v.Wreck.Out || victim is null || victim == v || victim.Wreck.Dead
                || !v.Moving)
            {
                v.Charge = null;
                continue;
            }
            if (v.Onto != onto || v.LegDone < RamContact)
                continue;
            // The heading is the drive heading, which is a flat side of the hex
            // by construction - the last leg of a route is a step onto a
            // neighbour - so both the armour model and the shove are handed the
            // number they want without a snap, exactly as the six firing lanes
            // hand one over.
            int heading = HexField.HeadingTo(v.Cell, onto);
            Vector2 along = v.Atlas.GroundDirection(heading);
            // The seam: halfway between the two contact patches, which is where
            // the hulls met and where the metal comes off - see Sparked.
            //
            // <b>And no ground off it (2026-09-18).</b> The contact threw the
            // fall's cloud from here as well, across the ram and once each way.
            // Measured, it was quieter than two hulls meeting wants, and louder
            // was not on offer: the volume would have brought back the veil
            // across both hulls, because ProcKick has no depth side to be hidden
            // behind - see Bumped, which carries what those attempts found. What
            // says a collision happened is the metal, which has one.
            Vector2 seam = (v.GroundPoint + victim.GroundPoint) * 0.5f;
            v.Charge = null;
            // The metal, on each hull's own plate rather than on the seam -
            // see Sparked. The rammer's class sizes both fans, and each hull is
            // asked for the plate that met the other one: the plate whose normal
            // looks along the ram, which for the victim is the one looking back
            // up it and is worked out off its own hull heading, not the rammer's.
            string hit = Struck(v, heading);
            string took = Struck(victim, Angles.Mod(heading + 180.0, 360.0));
            float scale = RamSparkFor(v.Profile);
            Scrape(v, seam, hit, scale);
            Scrape(victim, seam, took, scale);
            // What the two hulls themselves feel. Shared out by RamKeep: each is
            // thrown by the speed it changed by, so the light one that is sent
            // flying nods hardest and the heavy that sent it barely does.
            double keep = RamKeep(v.Profile, victim.Profile);
            if (PitchEnabled)
            {
                v.Pitch.Jolt(RamJolt * (1.0 - keep) * Endwise(v, hit));
                victim.Pitch.Jolt(RamJolt * keep * Endwise(victim, took));
            }
            // The knock and the ring after it, on the ram's own channel - see
            // Shook. A collision is not an explosion, so the ring is well under
            // a death's. Given whatever the rules then allow, which is the
            // sparks' rule too: two tanks met, and a refusal is about what
            // follows rather than about the meeting. One shove into the one
            // spring, from the hull that was driven - unnormalised, so a ram
            // into the screen jolts less than one across it, which is the shot's
            // rule on the same camera.
            Quake(Shook.Ram, v, along);
            // And the wood, which is the same event told to the other thing on
            // the board that can answer it - Fire's line and its argument, off
            // the seam rather than off either hull's foot, because the seam is
            // where the board was struck.
            if (Wood is not null)
                Wood.Shock(seam - Origin, v.Profile.ShotShake * Wood.ShotBlast);
            if (RamDents)
            {
                // The stand's first model, kept whole behind the switch - see
                // RamDents. The struck hull first, then the one that struck it:
                // order matters only for the kill, a ram that finishes the victim
                // must not have the rammer's own dent land on a tank the same
                // frame declared a wreck.
                Rammed(victim, Angles.Mod(heading + 180.0, 360.0),
                       Gunnery.RamLevel(v.Profile, victim.Profile));
                Rammed(v, heading, Gunnery.RamLevel(victim.Profile, v.Profile));
                Halt(v);
                continue;
            }
            Ramming.Shove push =
                Ramming.Of(Field, Vehicles, v.Profile, victim, heading);
            if (!push.Allowed)
            {
                // Said out loud, because on the board it is one hull stopping
                // short of another and that picture has half a dozen causes.
                GD.Print($"ram: {v.Tag} does not shove {victim.Tag} - "
                         + Ramming.Because(push.Why));
                Halt(v);
                continue;
            }
            // Both hulls leave at one speed, which is the whole content of
            // RamKeep: the shoved one is started at it and the rammer is checked
            // to it. Before this the rammer drove through the moment of contact
            // without so much as a dip, which is the one thing a collision cannot
            // do - and the engine takes the speed back up on its own over the
            // frames after, so the check is a dip rather than a stop.
            Shove(victim, push.Legs, heading, v.Speed * keep);
            v.Speed *= keep;
            // And from here the two are one thing until the hex has been
            // crossed - see Pushes. The shoved hull's engine goes off
            // (Vehicle.Shover) and the pusher is given one leg more than it was
            // ordered, the hex the victim is being thrown at, so that its nose
            // has ground to be over while it pushes. It never arrives there: the
            // push ends when the victim does, a third of a leg short of that
            // cell's middle, and the third is backed out again - see BackOff.
            victim.Shover = v;
            v.Path = v.Path.GetRange(0, v.PathStep + 1);
            v.Path.Add(push.Legs[0]);
        }
    }

    /// <summary>Which plate of <paramref name="v"/> met the other hull - the
    /// atlas's own question, asked in one place because three things want the
    /// answer: where the sparks come off, which way they spray, and which end of
    /// the hull was thrown.</summary>
    private static string Struck(Vehicle v, double facing) =>
        v.Atlas.FaceFor(facing, v.Sprite.HullFacing);

    /// <summary>
    /// How much of a hit on <paramref name="face"/> is along the hull: +1 square
    /// on the nose, -1 square on the tail, nought on a flank.
    ///
    /// <b>The cosine of the plate's own bearing, so a broadside comes out as
    /// nought without being a case.</b> The end that is struck is the end that
    /// goes down - the opposite of a mine, which lifts the end it went off under
    /// (<see cref="MineJoltFor"/>) - and <see cref="BodyPitch.Jolt"/> is signed
    /// nose-down positive, so the sign falls out with no table of plate names.
    ///
    /// <b>A hull rammed in the flank is given no jolt at all, and that is a gap
    /// named rather than papered over.</b> What a side-on shove does to a hull is
    /// roll it, and this board has no roll: <see cref="BodyPitch"/> is pitch, and
    /// <c>Vehicle.Lean</c> is the ground's slope under the tracks, not an impulse.
    /// Pitching a hull that was hit square in the side would be the one lie the
    /// cosine is here to avoid.
    /// </summary>
    private static double Endwise(Vehicle v, string face) =>
        Math.Cos(Mathf.DegToRad(v.Atlas.HitBearing(face)));

    /// <summary>
    /// One hull's half of a collision: where it touched the other tank and which
    /// way the metal comes off - <see cref="Sparked"/>.
    ///
    /// <b>The seam, not the plate's own centre, and that is the difference between
    /// where a shell lands and where two hulls meet.</b> The hit table records a
    /// plate's centroid, because that is where a mark belongs and a round may
    /// arrive anywhere on the armour; a ram touches at one place only - the line
    /// between the two feet, which <see cref="RamContacts"/> already has for the
    /// dust. Seated on the centroid the fan came off the middle of a flank, half a
    /// hull away from the nose that was doing the ramming.
    ///
    /// The plate is still used, for the two things only it knows: which way the
    /// metal sprays (its outward normal - <see cref="Vehicle.Blown"/>) and which
    /// side of the hull that is (<see cref="Vehicle.Turned"/>). It arrives already
    /// chosen - <see cref="Struck"/> - because the jolt wants the same answer and
    /// asking twice is two answers waiting to differ.
    /// </summary>
    private void Scrape(Vehicle v, Vector2 seam, string face, float might)
    {
        if (Sparked is null)
            return;
        // Up the screen is up on this board, so the height is one subtraction -
        // and it is in board px, scaled by this hull's own size, because the
        // point handed over is a place on the board rather than a place on a
        // sprite. See Sparked, where the seat used to be the hull's own foot.
        Vector2 at = seam - new Vector2(
            0.0f, RamSparkHigh * v.Atlas.HullSpan * v.Sprite.BodyScale);
        Sparked(v, at, v.Blown(face, 0.0f, 0.0f).Out, v.Turned(face), might);
    }

    public void CancelOrder(Vehicle v)
    {
        v.Path = new List<Vector2I>();
        v.PathStep = 0;
        v.Speed = 0.0;
        // The push ends with the path it was: an order that replaces a shove is
        // a tank driving itself again, and so is a shove run out to its end.
        v.Shoved = null;
        // And so does a reverse out of one - see BackOff, which sets these two
        // straight after calling this. R comes through here for every tank on
        // the board, which is the other reason they are cleared in one place.
        v.Backing = null;
        v.Dwell = 0.0;
        // And the gun comes down with the order that raised it - the ring's own
        // rule about the other axis, see SwingForward. R comes through here for
        // every tank on the board, so a reset puts every tube back to rest.
        StowTube(v);
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
        // And deeper still where either end is deep water - a swim, not a
        // wade; the lower of the two by construction, so the ford's figure is
        // untouched everywhere the pond is not. See MovementProfile.SwimSpeed.
        if (Field.IsDeepLeg(v.Cell, next))
            cap = Math.Min(cap, v.Profile.SwimSpeed);
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
        // And a hull in front of the nose, which is the heaviest thing a tank
        // ever pushes - see Pushes. The pair crosses the hex at RamKeep of
        // whichever of the two grounds is the slower: the momentum share is the
        // speed two hulls that do not bounce leave a contact at, and the ground
        // under the one being pushed is ground the pair has to get over.
        if (Pushed(v) is Vehicle under)
            cap = Math.Min(cap, SpeedCap(under)) * RamKeep(v.Profile, under.Profile);
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
        // Pushing another hull, which is neither the board's doing nor this
        // tank's class - see Pushes. Named first for masonry's reason: while it
        // is on it is the cap in force, whatever the ground under either tank.
        if (Pushed(v) is not null)
            return "pushing";
        Vector2I next = v.Path[v.PathStep];
        bool grade = Field.IsGrade(v.Cell, next), wet = Field.IsWet(v.Cell, next);
        // Swimming names the lowest of the water caps, which is the one in
        // force - and a plunge off the bank is a grade and a swim at once.
        if (Field.IsDeepLeg(v.Cell, next))
            return grade ? "swimming off a grade" : "swimming";
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

        // A hull that is being shoved never swings on to the leg: it is going
        // that way because something heavier is making it, and the rules say it
        // keeps the heading it had - see Vehicle.Shoved. Everything below the
        // branch is the same driving, which is the point of putting the
        // difference here rather than in a second mover.
        // <b>A hull with another one's nose in it is not driving.</b> Its speed
        // is the pusher's, written every frame by <see cref="Pushes"/>, and
        // there is no engine here to add to it or take it off. Left to the
        // ordinary branch the shoved hull accelerates up to its own ceiling and
        // walks out from under the tank that is pushing it, which is what the
        // first ram looked like: one shunt and a hull sliding away on its own.
        // See Vehicle.Shover.
        if (v.Shover is not null)
            accelRatio = 0.0;
        else if (v.Shoved is null && Math.Abs(diff) > 0.5)
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
            // <b>A hull that is out coasts: the engine is dead and only the
            // ceiling is left.</b> That ceiling runs down to the arrival speed
            // on its own as the distance goes, so a tank knocked out mid-step
            // rolls the rest of it and comes to rest on the cell rather than
            // stopping dead where it was hit - see Disable, which cuts the order
            // to that one leg.
            v.Speed = v.Wreck.Out
                ? Math.Min(v.Speed, allowed)
                : v.Speed > allowed
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
        // Whether this leg ends by coming down off a dry bank - asked before the
        // cell moves on, because both cells are needed to tell. A wet one is not
        // this: the splash and the bob go off the frame the hull touches the
        // water (see Climb), which is earlier than the arrival and is the event
        // there is to show. Dry, there is nothing until the tracks land.
        bool landed = Field.Drops(v.Cell, next) && !Field.IsWater(next);
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
            if (landed)
            {
                // Nose down, because the hull is stopped by the ground under its
                // front first - the same spring a mine kicks the other way.
                if (PitchEnabled)
                    v.Pitch.Jolt(FallJolt);
                // And the ground it landed on, thrown out from under the tracks
                // to either side - the same cloud a ram throws and seated the same
                // way, because it is the same event: a hull arriving somewhere
                // hard, and ground that has to leave from under it sideways to be
                // seen at all.
                Vector2 flung = v.Atlas.GroundDirection(heading + 90.0);
                Bumped?.Invoke(v, v.GroundPoint, flung);
                Bumped?.Invoke(v, v.GroundPoint, -flung);
            }
            // The throw is over when the legs are: what stands on the far side
            // of it is a tank again, facing wherever the ram left it.
            if (!v.Moving)
                v.Shoved = null;
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
        // The surface between the two cells and nothing subtracted from it:
        // no cell on this board puts a hull below its own face any more, deep
        // water included - see SunkAt.
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
        // And the ride on top of all three - see Afloat. The near cell's until
        // the seam, then toward the far cell's over the second half: off the bank
        // into the pond that is nought rising to the swimming height (the fall,
        // shaped in docs/swim-plan.md step 3), up the ramp out of it the other
        // way. The position was set from the ground before this was called, so
        // it comes up here by the same number.
        float part = Mathf.Clamp((done - 0.5f) * 2.0f, 0.0f, 1.0f);
        float afloat = Mathf.Lerp(Ride(v, v.Cell), Ride(v, next), part);
        // <b>Off the bank into the pond it falls, it does not slide.</b> The
        // surface between a bank cell and a flat deep one is a straight line from
        // the shared edge down to the bed - the chord a ramp would have - so the
        // hull came down it like a ramp it was not on. A drop is quadratic in
        // time: the hull holds the bank's height while its nose goes over, then
        // comes down faster and faster to where it will float. Same start, same
        // end, so nothing about arrival changes; only the shape of the second
        // half. Ramps into the pond are not this - a hull on a ramp is on its
        // tracks - and neither is a leg between two deep cells.
        // <b>And it is any bank, not only the wet one.</b> The shape was written
        // for the pond because the pond was the only step down a tank could take;
        // a rammed hull falls off every bank there is - off the hill on to the
        // plain, into the ravine - and a fall on to dry ground is the same fall
        // with nothing to splash. See HexField.Drops and Ramming.
        bool plunge = Field.Drops(v.Cell, next) && !Field.IsDeep(v.Cell);
        if (plunge && part > 0.0f)
        {
            float edge = Field.EdgeTop(v.Cell, next);
            float fell = Mathf.Lerp(edge, Field.TopAt(next) + Ride(v, next), part * part);
            afloat += fell - (v.Height + afloat);
        }
        v.Float = afloat;
        v.Height += afloat;
        v.Standing += afloat;
        v.Trailing += afloat;
        v.Sprite.Position -= new Vector2(0.0f, afloat);
        // <b>The frame it goes in</b>, told from the frames it is in by the
        // memory on the vehicle. Any water puts a fire out - GDD field.md; deep
        // water is a plunge as well: the water is kicked (Splash) and so is the
        // hull (Buoy). A ford is entered on the tracks and raises no plume.
        bool wading = v.Waterline > v.Height;
        if (wading && !v.WasWading)
        {
            if (v.Burning)
                v.Douse();
            if (Field.IsDeep(under))
            {
                // Down the ramp it wades in and the water takes the hull a
                // track at a time - a wash, not a plume, and a nudge, not a
                // kick. The drop off the bank is the other case, whole.
                float gently = plunge ? 1.0f : RampWash;
                v.Bob.Jolt(PlungeKick * gently);
                Plunged?.Invoke(v, v.GroundPoint, v.Waterline,
                                PlungeMight(v) * gently);
            }
        }
        v.WasWading = wading;
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
        if (!TrembleEnabled || v.Wreck.Out)
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
        if (!ExhaustEnabled || v.Wreck.Out || !v.Atlas.HasExhaust)
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
    ///
    /// <b>And now one pixel of it is an event.</b> Which of the two deaths this
    /// is - see <see cref="RackedBy"/> - and the detonation that draws it, which
    /// hands over to the fire this method has always lit. That handover is the
    /// whole of <see cref="ProcRack"/>; before it, the flame and the column
    /// arrived at full strength on this very frame.
    ///
    /// <paramref name="face"/> is the plate the killing round landed on, or null
    /// where there was no round - see <see cref="RackedBy"/>, which is the only
    /// thing that reads it.
    /// </summary>
    /// <summary>
    /// Knock one tank out - GDD states.md "Подбит", <see cref="Wreck.Disabled"/>.
    ///
    /// <b>Everything here is the engine stopping, and nothing is the tank
    /// breaking.</b> The orders go, the drive goes, the gun comes off its
    /// target and the scan stops - <see cref="Kill"/>'s list up to the point
    /// where the fire is lit and the others stop shooting. They do not stop: a
    /// knocked-out hull is still a target, and the next round of any kind is
    /// what ends it. What the picture adds is in <see cref="UpdateWreck"/> and
    /// <see cref="UpdateBurn"/>: the paint dims a little and the engine deck
    /// smokes, grey, with no flame. The ear gets the engine stopping.
    /// </summary>
    /// <param name="flash">How big the fireball at the ring is, as a share of
    /// the death's own - <see cref="KnockOutFlash"/> for a round, which is what
    /// the default is, and less for a charge that went off under the belly
    /// rather than inside the hull (<see cref="MineFlash"/>). Nought draws
    /// none. A number rather than a bool because the one caller that wanted
    /// something other than the default wanted it <em>smaller</em>, and the
    /// alternative was a second copy of the deck vector at the call site.</param>
    /// <param name="seated">Whether the turret stays on its ring - see
    /// <see cref="Wreck.Seated"/>. A round through the armour drops it; a charge
    /// under a track does not reach it.</param>
    public void Disable(Vehicle v, float flash = KnockOutFlash,
                        bool seated = false)
    {
        if (!v.Wreck.Disable(seated))
            return;
        // <b>A hull that was rolling finishes the step it is on.</b> Stopping it
        // where the blast caught it looks like the brakes worked, which is the
        // one thing a tank with its tracks blown cannot do - and it leaves the
        // wreck straddling a rim, which is worse than a picture: the rules put
        // the tank on a hex (GDD field.md, "Юнит на гексе с миной Подбит"), and
        // Vehicle.Cell does not change until the leg is done. So the order is
        // cut to the leg being driven and the engine is what stops - see
        // AdvanceOrder, where a hull that is out may only slow down. Standing
        // still it stops as it always did, because there is nothing to roll.
        if (v.Moving)
        {
            v.Path = new List<Vector2I> { v.Path[v.PathStep] };
            v.PathStep = 0;
        }
        else
        {
            v.Path.Clear();
            v.PathStep = 0;
            v.Speed = 0.0;
        }
        v.Target = null;
        v.Mark = null;
        v.Charge = null;
        v.Scan.Suspend();
        v.Audio?.Stalled();
        // <b>Nothing is re-placed here, and there used to be a Park for it.</b>
        // A hull that stopped holding itself up was drawn a settle lower, so the
        // frame the engine died was a frame the tank moved on; a drowning is
        // said with size now and the position never changes, so there is nothing
        // to put down - see SunkAt.

        // The picture last, as in Kill: the flash at the ring is what dropped
        // the turret, and it is drawn after every fact about the hull is set.
        if (flash > 0.0f && Rack && v.Atlas is not null)
            Flashed?.Invoke(
                v,
                new Vector2(0.0f, (float)v.Atlas.HeightSpanPx * v.Sprite.BodyScale),
                v.Sprite.BodyScale * flash);
    }

    /// <summary>What a hit does to a hull, by the rules' three states.</summary>
    public enum Fate { None, Disable, Kill }

    /// <summary>
    /// What one hit of <paramref name="level"/> does to a hull in the state
    /// <paramref name="wreck"/> holds - GDD states.md, in one place.
    ///
    /// A knocked-out hull dies to any hit at all, the armour not asked - a
    /// scorch, a ricochet, a burst on the face. A live one is knocked out by a
    /// penetration, <see cref="TankSprite.Penetrating"/>'s threshold, which is the
    /// one the ear and the picture already switch on; anything less scars the
    /// paint and leaves it running. A wreck takes nothing further: it is not a
    /// hull. Static and pure so the rule can be asserted with no board.
    /// </summary>
    public static Fate FateOf(Wreck wreck, int level) =>
        wreck.Dead ? Fate.None
        : wreck.Disabled ? Fate.Kill
        : TankSprite.Penetrating(level) ? Fate.Disable
        : Fate.None;

    /// <summary>Deal the hit's fate - <see cref="Land"/>'s and
    /// <see cref="Rammed"/>'s shared tail.</summary>
    private void Befall(Vehicle victim, string face, int level)
    {
        switch (FateOf(victim.Wreck, level))
        {
            case Fate.Disable:
                Disable(victim);
                break;
            case Fate.Kill:
                Kill(victim, face);
                break;
        }
    }

    public void Kill(Vehicle v, string? face = null)
    {
        if (!v.Wreck.Kill(RackedBy(face)))
            return;
        v.Path.Clear();
        v.PathStep = 0;
        v.Speed = 0.0;
        v.Target = null;
        // And the two orders the mouse gives, for the target's reason: a wreck
        // has no gun to spend a round with and nothing left to drive at. Its own
        // orders only - a mark on the cell a wreck is standing on is still a
        // perfectly good order to somebody else, because a cell is a cell.
        v.Mark = null;
        v.Charge = null;
        v.Scan.Suspend();
        // It burns because it has just been destroyed, not because the key was
        // pressed - and the key can no longer put it out, since a wreck that
        // stops smouldering on a keystroke is a switch pretending to be a state.
        //
        // <b>Unless it is standing in water, where the rules put a fire out the
        // moment it starts</b> - GDD field.md, water of any depth. The neighbours
        // of a blast already catch and go out on this board (Playback.Spread);
        // this is the same sentence about the hull the blast was, and without it
        // a wreck burns for eighteen seconds up to its deck in a pond.
        v.Burning = Field is null || !Field.IsWater(v.Cell);
        // <b>Nothing is re-placed here, and there used to be a Park for it.</b>
        // A hull that stopped holding itself up was drawn a settle lower, so the
        // frame the engine died was a frame the tank moved on; a drowning is
        // said with size now and the position never changes, so there is nothing
        // to put down - see SunkAt.

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
        // <b>And the camera, which this event has been missing since before it
        // had a picture</b> - named as a debt in docs/blast.md, and a line.
        //
        // Straight up the screen, and that is the one direction in this model
        // that is not a bearing: gases leaving a wrecked hull go out of its roof,
        // and vertical is what fully survives this camera's projection - which is
        // exactly what the length of this vector means. See CameraShake.Fire.
        // Up, and the ground under the camera for the half-second after - the
        // part a gun does not have. See CameraShake.Blast.
        if (v.Wreck.Racked)
            Quake(Shook.Death, v, new Vector2(0.0f, -1.0f));
        // <b>The picture last, because everything above it is what a dead tank
        // is and this is only what it looked like.</b> Nothing here may raise a
        // second shove or a second sound: both are sent above, and a detonation
        // that shook the trees again would be two accounts of one death - the
        // double model this file spends its comments refusing.
        if (Rack && v.Wreck.Racked && v.Atlas is not null)
            Detonated?.Invoke(
                v,
                // The deck as drawn, which is the map's own rise times the scale
                // this hull is on screen at - the same product every other
                // measured point on a tank goes through before it reaches the
                // board. See ProcRack.Aim.
                new Vector2(0.0f, (float)v.Atlas.HeightSpanPx
                                  * v.Sprite.BodyScale),
                // <b>And the size is the tank's, not the shell's</b> - see
                // Detonated. The drawn scale rather than MovementProfile.Size,
                // because the two part whenever the size dial is off one and what
                // is being asked is how big this tank is on this board.
                v.Sprite.BodyScale);
    }

    /// <summary>
    /// How much harder a detonation shakes the camera than that tank's own gun.
    ///
    /// <b>Off the class's own <see cref="MovementProfile.ShotShake"/> rather than
    /// a number per class, because the table already says how heavy a tank is in
    /// the only terms this camera has.</b> A heavy's gun moves the view 7px and a
    /// light's 3, so a heavy dying moves it 15 and a light 6.6 - and the ratio
    /// between the two deaths is the ratio the board already draws between the
    /// two guns.
    /// </summary>
    public const double DeathShake = 2.15;

    /// <summary>The tremble after the kick, as a share of the class's gun: a
    /// medium's 4.5px gun trembles the view 7px, a heavy's 7px gun 11.</summary>
    public const double DeathRumble = 1.6;

    /// <summary>The turret landing on the deck: the pitch impulse on the hull
    /// (see <see cref="BodyPitch.Jolt"/>) and the camera's knock as a share of
    /// the class's own gun.</summary>
    public const double TurretLandingJolt = 1.8;
    public const double TurretLandingShake = 0.45;

    /// <summary>
    /// The wreck settling, once a frame.
    ///
    /// Everything it writes is a function of one age - see <see cref="Wreck"/> -
    /// so there is no table here and no second clock. It runs before the burn so
    /// the densities it sets are this frame's rather than last frame's, the same
    /// ordering the audio and the layers already need.
    /// </summary>
    private void UpdateWreck(Vehicle v, double delta)
    {
        // Knocked out or destroyed - both have a clock and a picture on it.
        if (!v.Wreck.Out)
            return;
        v.Wreck.Update(delta);
        TankSprite s = v.Sprite;
        // The pose swaps on the hit and the paint blackens over the second after
        // it: the turret dropping is the event, the char is the aftermath. Set
        // here rather than in Kill so that a reset which clears the wreck clears
        // this too, through the one path that owns the state.
        // <b>On the death frame, with the detonation, and that is the answer to a
        // thing that was tried the other way round.</b> See Wreck: held back until
        // the soot was thickest the cut hid three times as many pixels and read as
        // two events - a blast on an intact tank, and then a broken one. The blast
        // is the tank breaking.
        // <b>Out, not Dead, since 2026-09-08</b>: the drooped turret and the slack
        // belts are the knocked-out tank's picture now - see Wreck.Dim - and the
        // destroyed one goes further: its turret is off (TankSprite.Turretless,
        // which launches it on the frame it turns true).
        // <b>And drowning breaks nothing, so the pose does not come with it.</b>
        // The knocked-out picture is a dropped gun and slack belts because that
        // is what a round through the armour does; Seated already says that a
        // mine does something else. Water does less than either: GDD states.md
        // makes a stalled engine in deep water "Утоплен" rather than "Подбит",
        // and nothing has hit the tank at all - so the running gear is whole,
        // the gun is where it was pointing, and the dimming below is the whole
        // of the picture. Reported as "it catches fire and its tracks come
        // apart, and it is not knocked out".
        //
        // The cell says so rather than a flag on the hull, which is the ruling
        // the grey column off the ports already follows - see Burn, where the
        // same sentence is written for the smoke.
        s.Wrecked = v.Wreck.Out && !Drowning(v);
        // Turreted, because a casemate's mount is welded on: it does not fall
        // off a destroyed vehicle, it chars with the hull. `TankSprite` refuses
        // this write anyway - that is the mechanism - and it is said here as
        // well because this is the line that means "a destroyed tank loses its
        // turret", and it should read as the rule it is rather than as a rule
        // with a silent exception somewhere else.
        s.Turretless = v.Wreck.Dead && v.Profile.Turreted;
        // Which parts of the wreck are in the wreck's pose - Wreck.Seated: a mine
        // breaks the running gear and leaves the mount alone.
        s.TurretSeated = v.Wreck.Seated;
        // The turret coming down on the deck: the hull takes it. Tail dips on
        // the pitch spring (the turret lands astern), the camera takes a smaller
        // knock than the gun gives it, and the armour rings as a bounce does -
        // a lump of steel on steel, not a round going in. See TurretToss.
        if (s.Toss is { } toss && toss.TakeLanding())
        {
            if (PitchEnabled)
                v.Pitch.Jolt(-TurretLandingJolt);
            Quake(Shook.Turret, v, new Vector2(0.0f, 1.0f));
            v.Audio?.Struck(0, v.HitCount + 1);
        }
        s.Char = v.Wreck.Char;
        // The rules' fire burns by the wreck's clock; a knocked-out hull that is
        // not on fire shows the flare of the hit instead - see Wreck.Flare.
        s.FireDensity = (float)(v.Burning ? v.Wreck.Blaze
                                : Drowning(v) ? 0.0 : v.Wreck.Flare);
        // A knocked-out hull that is not on fire smoulders at the wreck's
        // quarter; one that is on fire - the rules' "Горит" is a separate state
        // and can stand on this one - burns at the live column's full.
        s.SmokeDensity = (float)(v.Wreck.Dead || v.Burning ? v.Wreck.Smoke
                                                            : v.Wreck.Smoulder);
        // Once the fire is out the hull leaves - the rule freed the cell on the
        // frame of death, this is the picture catching up. See Wreck.FadeSeconds.
        s.Presence = v.Wreck.Presence;
        // Once the flame is out there is nothing left to advance but the column,
        // and it is still asked for: the smoke is what says where a tank died
        // from the other side of the board.
        s.QueueRedraw();
    }

    /// <summary>How much bigger the steam of a doused fire is than the column
    /// it replaces, at its fullest - <see cref="ProcSmoke.Swell"/>. Measured on
    /// the bench rather than reasoned: at 1.0 the white read as a column that
    /// had thinned, which is a fire dying down and not one put out.</summary>
    public const double SteamSwell = 0.9;

    /// <summary>
    /// A tank that has come over a mine.
    ///
    /// <b>The moment is geometry and the consequence is the rules.</b> GDD
    /// field.md gives the rule in one line - a mine goes off when a tank drives
    /// through its hex or stops on it, the tank is knocked out and the mine is
    /// spent - and says nothing about where in the hex either happens, because
    /// on a paper board there is no "where". On this one there is: the mine lies
    /// at a point (<see cref="Mines.At"/>) and the tank is a rectangle, so the
    /// frame the two meet is the frame the leading end of the hull reached the
    /// charge. Asked as one test - <see cref="Mines.Under"/> - which is why
    /// there is no branch here for driving forward or being shoved backwards:
    /// whichever end got there first is the end that covered it.
    ///
    /// <b>Both cells, because a tank crossing a rim is in neither.</b>
    /// <see cref="Vehicle.Cell"/> does not change until the leg is done, so a
    /// nose already a third of the way into the next hex would be over a mine
    /// the tank is not yet standing on. The cell it is leaving is asked as well,
    /// for the tank that is only now clearing one.
    ///
    /// <b>What the picture is: a burst under the hull, and the hull thrown by
    /// the end that took it.</b> The burst is the board's
    /// (<see cref="Mined"/>); the throw is the tank's and is the one movement
    /// that says the charge was underneath rather than beside - nose up on a
    /// mine caught with the nose, tail up on one caught with the tail, off the
    /// sign <see cref="Mines.Under"/> reports. Then the rules' own consequence,
    /// <see cref="Disable"/>: knocked out, not destroyed.
    ///
    /// <b>A wreck does not set one off.</b> A hull that is already out is not
    /// driving anywhere, and a mine under a tank that has just been knocked out
    /// by something else would go off on the frame it settles.
    /// </summary>
    public void UpdateMines(Vehicle v)
    {
        if (Field is null || v.Wreck.Out)
            return;
        Vector2I ahead = v.Moving ? v.Path[v.PathStep] : v.Cell;
        if (!Mine(v, v.Cell) && ahead != v.Cell)
            Mine(v, ahead);
    }

    /// <summary>The mine on one cell, if there is one and this tank is over
    /// it. True when it went off.</summary>
    private bool Mine(Vehicle v, Vector2I cell)
    {
        if (Field!.CoverAt(cell) != Cover.Minefield
            || Field.CoverStateAt(cell) != CoverState.Intact)
            return false;
        Vector2 at = Origin + Mines.At(Field, cell);
        bool under = Mines.Under(v, at, out double along);
        // <b>The point says where it goes off, not whether.</b> The rules fire a
        // mine both by driving through the hex and by stopping on it - GDD
        // field.md, "срабатывает при проезде и при остановке" - and a hull that
        // has come to rest on the cell with the charge beside its tracks is not
        // a tank that got away with it. Under way the box is still the whole
        // test, which is what keeps T12's "which end of the hull found it"
        // exact; at rest the hex is the test, which is what the rules say.
        // Rammed tanks made this common rather than introducing it: a hull
        // thrown on to a mine arrives broadside, and its box is half as wide
        // that way round.
        if (!under && v.Moving)
            return false;
        // Spent first, so nothing that follows can find it again - the rules'
        // "мина израсходована", and also what takes the marker off the board.
        Field.SetCoverState(cell, CoverState.Cleared);
        // The burst and the hole, both on the ground, because that is where the
        // charge was - Stage3D.Mine, which carries what three earlier pictures
        // got wrong. Across the hull and toward the camera, so the gases leave
        // from under the tracks rather than over the roof.
        Mined?.Invoke(v, at,
                      MineAway(v.Atlas!.GroundDirection(v.Sprite.HullFacing
                                                        + 90.0)),
                      MineMight);
        // Positive drives the nose down (BodyPitch.Jolt), so a charge that went
        // off under the nose is a negative one: the end over the blast is the
        // end that comes up.
        // Only when the charge really was under the hull: MineJoltFor reads which
        // end of it found the charge, and a charge that went off beside the
        // tracks has no end. A roll is what that would want and the body has no
        // roll impulse, so nothing at all beats a nose lifted by the sign of a
        // number that means nothing here.
        if (PitchEnabled && under)
            v.Pitch.Jolt(MineJoltFor(along));
        if (v.Atlas is not null)
            Quake(Shook.Mine, v, new Vector2(0.0f, 1.0f));
        if (Wood is not null)
            Wood.Shock(v.GroundPoint - Origin, Wood.HitBlast);
        // And the rules' consequence: knocked out, never destroyed - GDD
        // field.md, "Юнит на гексе с миной Подбит". <b>With the ring flash</b>,
        // which was off here for one commit on the argument that a fireball on
        // the deck says a round got inside. It does not say that on a mine: a
        // charge under the belly is what lifts a turret off its ring, and the
        // dropped turret is the pose the flash exists to explain. Two flashes,
        // and between them they say where the charge was and what it did - the
        // ring's one smaller than a round's, because what went off was under the
        // tank rather than in it.
        Disable(v, MineFlash, seated: true);
        return true;
    }

    // --- the wood ------------------------------------------------------------

    /// <summary>
    /// The wood under a bulldozer - GDD classes.md "Бульдозер HT", field.md
    /// "деревья", docs/fell-plan.md.
    ///
    /// <b>Beside <see cref="UpdateMines"/> because it is the same question</b>:
    /// what the cell under this hull does about the hull being on it. The rules
    /// put the felling in the turn's own step 6 and this puts it on the drive
    /// in, and those are the same hex in any position a game can reach - a tank
    /// entering a wood is stopped by it, so "drove in" and "ended the turn
    /// there" name one cell. They part only on this bench, where a wood stops
    /// nobody yet (<c>terrain.json</c>, <c>blocks: false</c>).
    ///
    /// <b>Driving spends the hex, and then standing on it goes on felling.</b>
    /// The travel is the test <see cref="Grove.Brush"/> is written on and it is
    /// what keeps a board from losing a grove on its first frame because a heavy
    /// was parked in the trees - so it guards the cover, which is the thing that
    /// cannot be taken back. It does not guard the trees: a tank that drove into
    /// a wood and stopped is standing on the trunks it reached, and they go down
    /// whether it is still rolling or not.
    ///
    /// <b>Called every frame the hull is on the wood, and the cell is written
    /// once.</b> The two halves of this are answers to two different questions.
    /// What the hex is worth is the rules' business and it is settled the moment
    /// a heavy drives in - a cell already <c>Cleared</c> is not written again and
    /// prints nothing. What is still standing on it is the picture's, and that
    /// is settled trunk by trunk as the glacis comes up to each one, which is
    /// why the loop goes on running over a spent cover instead of returning on
    /// it.
    ///
    /// <b>The two cells of the leg, and the hull's own patch inside them.</b>
    /// The patch alone is too generous for a write: six probes a keep-out
    /// radius out reach the cells flanking a boundary the tank is crossing, and
    /// a heavy driving from one hex to the next would flatten the woods to
    /// either side of the gap without ever entering them. The leg says which
    /// two cells a hull may be in; the patch says when it is actually in one.
    ///
    /// <b>Alive or burnt out, the trees go down the same; burning, they do
    /// not.</b> The first is the rules' own "живого или сгоревшего"; the second
    /// is their "горящий гекс землёй не становится" - the fire finishes first,
    /// and the next heavy through flattens what it left.
    /// </summary>
    public void UpdateWood(Vehicle v)
    {
        if (Field is null || Wood is null || !v.Profile.Bulldozes || v.Wreck.Out)
            return;
        foreach (Vector2I cell in Footing.Patch(
                     Field, v.GroundPoint - Origin, Field.Bare(v.Ground),
                     Wood.KeepOut, Wood.Squash))
        {
            if (cell != v.Cell && cell != v.Onto)
                continue;
            if (Field.CoverAt(cell) != Cover.Forest)
                continue;
            CoverState state = Field.CoverStateAt(cell);
            if (state == CoverState.Burning)
                continue;
            // Travel is what may spend a wood, and only that. A hex already
            // spent goes on losing trees to a hull sitting in it, which is the
            // other half of the same picture: the tank drove in, stopped on top
            // of the trunks it had come up to, and they are under it.
            if (state != CoverState.Cleared && v.Speed == 0.0)
                continue;
            // Which way the crowns go over: the way the hull is going, because
            // what puts them down is the glacis. Unnormalised and projected on
            // to the ground like every axis handed out of here.
            Vector2 along = v.Atlas.GroundDirection(v.Sprite.HullFacing);
            // The trees the hull has actually come up to, which is why this runs
            // on a cell already spent: the board's answer was given on the drive
            // in and the wood goes down as the tank gets to it - Grove.Topple.
            int going = Wood.Topple(cell, along, v.GroundPoint - Origin,
                                    Wood.FellReach,
                                    // How hard the glacis throws them, on the
                                    // scale Brush measures a hull's shove by:
                                    // the reference cruise, capped, because the
                                    // fastest thing on the board is already at
                                    // it.
                                    v.Speed / Wood.BrushSpeed);
            if (state != CoverState.Cleared)
            {
                // Spent first, so nothing that follows finds a wood here again -
                // the mine's order, and for its reason. On the drive in and not
                // on the first trunk: what the hex is worth is a question about
                // the hex, and a heavy that entered it has answered it whether
                // or not anything is standing close enough to go over yet.
                Field.SetCoverState(cell, CoverState.Cleared);
                // Printed like the ram's refusal, and for its reason: a board
                // that changed under a tank is an event, and the only other
                // evidence of it is a wood that is not there any more.
                GD.Print($"wood: {v.Tag} fells ({cell.X},{cell.Y}) - "
                         + $"{going} trees, was "
                         + state.ToString().ToLowerInvariant());
            }
            // And no dust from here. What throws the ground up is a crown
            // reaching it, which is a second and a half after the hull drove in
            // and is the wood's own moment to know - Grove.Thud, wired by the
            // roots exactly as this hook was.
        }
    }

    /// <summary>Runs every frame like the exhaust, but takes no speed: see
    /// <see cref="BurnLoop"/>. Both halves are required before anything is
    /// shown, because the flame without its column is the washed-out half of the
    /// effect rather than a cheaper version of it.</summary>
    public void UpdateBurn(Vehicle v, double delta)
    {
        // The fire going out, if one is - here rather than beside the wreck's
        // other clocks, because UpdateWreck returns on the first line for a tank
        // that is not out and a live tank is exactly who this is for. It is also
        // where the two densities are stated for a live tank at all: the wreck
        // writes them for a hull that is out, and a hull that is not has always
        // simply been left at the full both fields open on.
        v.Dousing(delta);
        // The column runs for a fire and for a knocked-out engine deck alike -
        // the flame only for the fire. See TankSprite.Smouldering.
        // Lit by the rules, or by the flare of the hit that knocked it out -
        // the flame layer's own density (Wreck.Flare, set in UpdateWreck) is
        // what puts the second one out.
        bool lit = (v.Burning || (v.Wreck.Flare > 0.0 && !Drowning(v))
                    || v.Ember > 0.0)
                   && v.Atlas.HasBurning;
        // The steam outlives the flame, and while it is up the column has to go
        // on being advanced: the reset below is what ends this effect, and it
        // would otherwise run on the frame the flame went and take the steam
        // with it.
        // And nothing smokes off a deck that is under water - GDD states.md, where
        // a stalled engine in deep water is drowned rather than knocked out. The
        // grey column off the ports is the one part of the knocked-out picture
        // that cannot survive the move, and it is the cell that says so rather
        // than a flag on the hull.
        bool smoulder = !lit && (v.Wreck.Disabled || v.Steam > 0.0)
                        && v.Atlas.HasBurning && !Field.IsDeep(v.Cell);
        if (!lit && !smoulder)
        {
            if (!v.Sprite.Burning && !v.Sprite.Smouldering
                && v.Sprite.FirePhase < 0 && v.Sprite.BurnPhase < 0)
                return;
            v.Burn.Reset();
            v.Sprite.Burning = false;
            v.Sprite.Smouldering = false;
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
        v.Sprite.Burning = lit;
        v.Sprite.Smouldering = smoulder;
        // Grey off a stopped engine, soot off a burning one - the ink follows
        // the state on the frame, so a knocked-out hull that then catches fire
        // goes black with its flame.
        // Grey under the flare too: the flare is the hit, not a fire.
        // White while it steams, and the white is the message: the same cloud
        // off the same ports, pale instead of black, is a fire that was put out
        // rather than one dying down. Asked before the other two, because a
        // doused hull can be a knocked-out one as well and what happened last
        // is what is being shown.
        if (v.Sprite.Column is ProcSmoke column)
        {
            column.Ink = v.Steam > 0.0 ? ProcSmoke.SteamInk
                         : v.Burning ? ProcSmoke.ColumnInk
                         : ProcSmoke.SmoulderInk;
            // And there is more of it while it steams: white alone reads as a
            // thinner column rather than as a cloud, which is what a fire being
            // put out throws. Biggest on the frame the water lands and back to
            // the column's own size as it goes - see ProcSmoke.Swell.
            column.Swell = 1.0f + (float)(v.Steam * SteamSwell);
        }
        // How much flame and how much column: the douse's two clocks while a
        // fire is going out, and the full of both for any other live tank. A
        // hull that is out has had these written by UpdateWreck a moment ago and
        // keeps them - except while it steams, because a fire put out on a wreck
        // is still a fire put out.
        if (v.Steam > 0.0)
        {
            v.Sprite.FireDensity = (float)v.Ember;
            v.Sprite.SmokeDensity = (float)v.Steam;
        }
        else if (!v.Wreck.Out)
        {
            v.Sprite.FireDensity = 1.0f;
            v.Sprite.SmokeDensity = 1.0f;
        }
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
    /// <param name="onto">The cell the round is for, when there is one. It only
    /// ever lowers the line, and that is <see cref="Gunnery.Overtops"/> arriving
    /// here: the floor a shot runs along is the lower of its two ends, so a gun
    /// laid on the plain from a hilltop is stopped by its own plateau unless it
    /// has come out to the brink. Left out, the line runs at the shooter's own
    /// level - which is what a sighting line down an empty heading is, there
    /// being no second end to be lower.</param>
    public (float Run, Vector2I? At, bool Blocked) Reach(Vehicle shooter,
                                                         Vector2 dir,
                                                         Vector2I? onto = null)
    {
        float top = Field.Bare(shooter.Ground);
        var ground = new Vector2(shooter.GroundPoint.X - Origin.X,
                                 shooter.GroundPoint.Y - Origin.Y + top);
        var tanks = new HashSet<Vector2I>();
        // <b>Not a destroyed one</b> - GDD states.md: its hex no longer blocks
        // the line of fire. A knocked-out hull would, and will when it exists
        // (docs/effects-plan.md T1); Dead here is the destroyed state.
        foreach (Vehicle other in Vehicles)
            if (!ReferenceEquals(other, shooter) && !other.Wreck.Dead)
                tanks.Add(other.Cell);
        // And whatever else on the board fills a cell. Unioned rather than asked
        // separately, because Track's whole question is "which cells are not to
        // be crossed" and a shell does not care which kind of thing is in one.
        //
        // <b>Masonry is not in here and must not be</b>: a wall stands on a cell's
        // edges, so putting its cell in this set stops every round that crosses
        // the cell rather than the ones that cross the wall. That is what it used
        // to do, and a breached ring went on stopping shots aimed through the
        // breach. See Barred, which is asked per cell and per direction, and
        // Track, which now asks it all the way down the walk.
        foreach (Vector2I cell in Obstacles)
            tanks.Add(cell);
        // The line the walk is measured against, which is not always the height
        // the walk starts at - see the parameter. Taken off TopAt rather than off
        // the tank standing there, because what a round clears is ground.
        float line = onto is Vector2I mark
            ? Math.Min(top, Field.TopAt(mark)) : top;
        return Track(Field, ground, dir, line + Field.Lift * Clearance, tanks,
                     Barred);
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
    /// <b>Masonry is asked of every cell and of a direction</b>, through
    /// <paramref name="barred"/> - the cell the gun stands on as the round leaves
    /// it, and every cell after as the round arrives at it. Everything else solid
    /// on this board fills a cell and goes in <paramref name="tanks"/>; a wall
    /// stands on a cell's <em>edges</em>, so a set of cells cannot say which of
    /// them it is on.
    ///
    /// <b>It was one bool for the firing cell alone, and that was the whole of the
    /// coarseness.</b> A wall was a cell in <paramref name="tanks"/> as well, so a
    /// round crossing a walled cell stopped there whatever edge it crossed: a ring
    /// with one leaf breached went on stopping rounds aimed through the gap, and
    /// the burst then went off on whichever leaf was still standing nearest the
    /// shot. Reported as firing through a hole and hitting the wall beside it.
    /// Asked per cell and per direction, a breach is a way through.
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
        IReadOnlySet<Vector2I> tanks,
        Func<Vector2I, Vector2, bool>? barred = null)
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
            // The rim of the cell it fired from, if something stands across the
            // way out - the sample before the walk left, by the same reading as
            // every other stop below. Tested here rather than before the loop so
            // the run is the distance to the edge rather than zero: a wall on this
            // cell is met where the cell ends, not at the muzzle.
            if (here == start && barred is not null && barred(start, step))
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
            // Masonry on the side of the cell the round is entering by, which is
            // the side facing back along the shot - the same question the firing
            // cell is asked, read the other way round.
            if (barred is not null && barred(cell, -step))
                return (run - grain, cell, true);
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
    /// Where a round starts when it is not starting at a gun: the point in
    /// board space, how high it stands, and which way it is going as a board
    /// direction.
    ///
    /// <b>The second leg of a round that did not stop</b> - a ricochet leaving
    /// the plate it came off, a destroyer's shell leaving the far side of what
    /// it went through. It is still the same gun's round and still the same
    /// tank's to hold (see <see cref="Vehicle.Rounds"/>): what the leg replaces
    /// is the muzzle, not the shooter. <see cref="Carry"/> is the one caller.
    ///
    /// <c>Along</c> is a board direction rather than the shooter's own, because
    /// the axis was decided on the board - <see cref="Gunnery.Deflect"/> - and
    /// the tank it leaves is not the tank it was fired from.
    /// </summary>
    public readonly record struct Leg(Vector2 From, float Lift, Vector2 Along);

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
                               double fromBearing, int serial, Leg? leg = null)
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
        // The muzzle, unless somebody handed this round a place to start from:
        // a leg is a round that did not stop at its first target and is leaving
        // that target rather than a gun. See Leg and Carry.
        Vector2 launched = leg?.From ?? shooter.Spot(tube);
        // The bore as the target's own layers see it. Carried through both
        // transforms as a pair of points rather than as an angle, so a scaled
        // class and a hull leaning on its springs are handled by the transforms
        // that already express them instead of by arithmetic repeated here.
        // Unspot and not ToLocal, by the same argument: a point handed from one
        // sprite to the other through ToGlobal and ToLocal arrives somewhere else
        // entirely on the staged board, and the bore is the whole of how the
        // scatter is put onto the gun's axis.
        Vector2 eye = victim.Unspot(launched);
        Vector2 bore = victim.Unspot(leg is Leg onward
                                     ? launched + onward.Along * 100.0f
                                     : shooter.Spot(tube + along * 100.0f)) - eye;

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
    /// <param name="level">The outcome, when somebody else has already decided
    /// it: the depth the round will do on arrival, 0 for a bounce. Null asks the
    /// class table, which is what the harness does. The event bench passes it,
    /// because there a shot is an event whose result the rules have settled and
    /// the picture has to show that result rather than argue with it.</param>
    /// <param name="ammo">What the round is, when the caller knows - null asks
    /// the dial, which is what the harness does. Its own parameter for
    /// <paramref name="level"/>'s reason: a ricochet is what a solid shot does,
    /// and an event that says a round bounced on has said which kind it
    /// was.</param>
    /// <param name="goes">What the round does when it arrives, besides landing
    /// - see <see cref="Shell.Onward"/>. It travels on the shell rather than
    /// being asked again on impact, exactly as the level beside it does.</param>
    /// <param name="onwardLevel">The outcome waiting for that second leg, or
    /// null to let the classes decide it - <see cref="Shell.NextLevel"/>.</param>
    /// <param name="leg">Where this round starts, when it is not starting at
    /// this gun's muzzle - <see cref="Leg"/>. The shooter is unchanged: it is
    /// still that gun's round, still held by that tank, and still its class
    /// that decides how deep it gets.</param>
    /// <summary>
    /// Lay this gun at that cell: lift the tube to the rung the board asks for,
    /// and answer with how high over the chord the round it fires will go, in
    /// screen pixels.
    ///
    /// <b>One call, because they are one act.</b> What the tube shows and what
    /// the round does are the same angle read twice - the whole complaint this
    /// was written for was a bomb leaving a level tube - so the bench is not
    /// given two doors to walk through, one of which it can forget.
    ///
    /// <b>The rung takes no time.</b> The ring's traverse is spent in degrees
    /// per second because the swing is drawn; there is nothing rendered between
    /// two rungs, and a tube crawling up an eleven-pose ladder would be eleven
    /// poses of stutter.
    ///
    /// <b>Zero apex comes out by arithmetic and not by a branch</b>: a direct
    /// gun is laid along its own line of sight, so the launch angle and the
    /// chord's angle are the same number and the apex between them is nothing -
    /// see <see cref="Gunnery.ApexPx"/>. Which means this is asked of every shot
    /// alike, and nothing here knows what a mortar is.
    ///
    /// <b>One expression and one caller each side</b>, because the two doors a
    /// round leaves by (<see cref="Shoot"/> at armour, <see cref="Loose"/> at
    /// the field) would otherwise each carry their own copy of it, and a shot
    /// that arced at a tank and not at the ground beside it is one gun drawn as
    /// two.
    ///
    /// <b>The range is asked down a lane, because that is the only way a gun
    /// fires.</b> <see cref="HexField.LaneTo"/> answers -1 for a cell on no lane
    /// of this hex, and then there is no legal shot to size: the fallback is the
    /// longest throw, which is what an unlaid mortar would be standing at.
    ///
    /// <b>The second leg never asks.</b> A round that bounced or went through
    /// (<see cref="Carry"/>) leaves flat whoever fired it, and the rules say so
    /// twice over: a V meets no armour it fails to pass, so a mortar's bomb has
    /// no ricochet to fly - GDD units.md, "рикошет бывает только от выстрелов
    /// LT, MT и HT".
    /// </summary>
    public void Train(Vehicle v, Vector2I? onto)
    {
        SeedTube(v);
        // <b>Moving stows it, and that is the ring's own rule said about the
        // other axis</b> - see SwingForward. A tank crossing the board with its
        // gun held at the angle of a target it has not reached is a tank aiming
        // at nothing; and deciding it here rather than in UpdateTube is what
        // keeps one writer on the want, so the two cannot argue frame by frame.
        // <b>And loading stows it too, which is what makes the return a
        // return.</b> Fire puts the tube back to rest; without this the very
        // next frame of an attack loop would ask for the firing angle again and
        // the gun would come straight back up through its own recoil. A gun is
        // loaded at its loading angle, so the reload is exactly the window the
        // tube is down for - and the frame it runs out, the lay begins. The
        // Trained gate then holds the next round until the tube has arrived, so
        // the cycle is lay, fire, return, lay.
        v.TubeWants = v.Moving || Field is null || v.ReloadLeft > 0.0
            ? RestTube(v) : Wanted(v, onto).Deg;
    }

    /// <summary>
    /// Count the loading down.
    ///
    /// <b>Here rather than in the harness's attack loop, where it lived.</b> A
    /// reload is a fact about a gun and every root runs this tick, while only
    /// one of them has an attack loop - so on every bench a tank that fired
    /// carried its countdown forever. Nothing read it there until the tube
    /// started stowing to load (see <see cref="Train"/>), and then it read as a
    /// gun that never came back up: the event bench's queue waited on a reload
    /// that was not running. The rule the file is written under says where this
    /// goes - a shared module knows no scene.
    /// </summary>
    private static void UpdateReload(Vehicle v, double delta)
    {
        if (v.ReloadLeft > 0.0)
            v.ReloadLeft = Math.Max(0.0, v.ReloadLeft - delta);
    }

    /// <summary>Bring the tube back to where the model left it - what moving
    /// does to it, and what a dropped order does.</summary>
    public void StowTube(Vehicle v)
    {
        SeedTube(v);
        v.TubeWants = RestTube(v);
    }

    /// <summary>
    /// Whether the tube is standing at the angle it was asked for.
    ///
    /// <b>The vertical half of <see cref="Gunnery.Laid"/>, and a gate for the
    /// same reason</b>: a gun that fires while still coming up sends the round
    /// along an angle nobody asked for, and the picture - a tube visibly on its
    /// way - says so at the moment the shot is least deniable. Same tolerance,
    /// because <see cref="UpdateTube"/> lands exactly the way
    /// <see cref="Gunnery.Traverse"/> does and the slack is there to survive the
    /// last partial step rather than to allow a shot that is off.
    /// </summary>
    public bool Trained(Vehicle v)
    {
        SeedTube(v);
        return Math.Abs(v.TubeDeg - v.TubeWants) <= Gunnery.LayTolerance
               && v.TubeDwell <= 0.0 && !v.Barrel.Live;
    }

    /// <summary>Where this tank's tube sits when nothing is asked of it: the
    /// angle the model was built at - see <see cref="AtlasSet.RestDeg"/>. Not
    /// nought, and on the mortar not near it.</summary>
    private static double RestTube(Vehicle v) => v.Atlas?.LayOf(0) ?? 0.0;

    /// <summary>Seed the two angles off the atlas the first time anybody asks.
    /// A tank is built before it is dressed, so neither can be an initialiser.
    /// </summary>
    private static void SeedTube(Vehicle v)
    {
        if (double.IsNaN(v.TubeDeg))
            v.TubeDeg = RestTube(v);
        if (double.IsNaN(v.TubeWants))
            v.TubeWants = RestTube(v);
    }

    /// <summary>
    /// How fast the tube is driven, in degrees per second.
    ///
    /// <b>One figure for all five, because this is the mantlet and not the
    /// ring.</b> <see cref="MovementProfile.TurretRate"/> is per class since a
    /// heavy turret is part of what makes a heavy feel heavy; there is no such
    /// column for elevation, and inventing three numbers to fill one would be
    /// three numbers nobody measured.
    ///
    /// <b>Sized off the movement, not off the pose - which is the correction
    /// this number came to by measurement.</b> The first cut was 60 deg/s, on
    /// the reasoning that the rungs sit three and a half degrees apart and each
    /// pose wants three or four frames to be a pose. Timed on the bench, the
    /// whole lay for a shot one level down at two cells - 4.76 degrees, two
    /// rungs - took 0.07s: four frames, which is a snap with extra steps. What
    /// has to read is the <em>lay</em>, not the rung, and a movement of a few
    /// pixels needs a third of a second before the eye calls it a movement.
    ///
    /// At 20 deg/s that same lay is 0.24s and holds each pose about ten frames;
    /// the steepest the rules allow, one level over one cell, is 0.7s. Slower
    /// than that and the gun becomes what the player waits for - and it is still
    /// three times what a real hydraulic mounting does, the traverse here being
    /// arcade-fast for the same reason.
    /// </summary>
    public static double TubeRate = 20.0;

    /// <summary>
    /// The beat between the gun arriving on its angle and the round leaving, in
    /// seconds.
    ///
    /// <b>The lay is an act and an act has a shape.</b> Without it the round
    /// leaves on the frame the tube stops, so the two movements - the tube
    /// rising and the tube recoiling - run into each other and read as one
    /// twitch; the gun never looks laid, only interrupted. A quarter of a second
    /// is about what the lay itself takes at <see cref="TubeRate"/>, which is
    /// the reason for the figure: the beat is the same length as the movement it
    /// follows, so the pair reads as one deliberate thing with a pause in the
    /// middle rather than as a stall.
    ///
    /// A field rather than a constant, for <see cref="TubeRate"/>'s reason: the
    /// three numbers of this axis are what the event bench exists to judge, and
    /// a rebuild per guess is not judging.
    /// </summary>
    public static double TubeSettle = 0.25;

    /// <summary>
    /// The beat between the tube coming home from its recoil and starting back
    /// down, in seconds.
    ///
    /// <b>The recoil is the floor and this is what sits on top of it.</b>
    /// <see cref="UpdateTube"/> will not move a tube that is out of battery at
    /// all - a gun coming down while it is still coming home is two movements on
    /// one part - so the pause after a shot is already the recoil's own length,
    /// 28 frames, whatever this says. What this buys is the moment after that:
    /// the gun held on target, which is what makes the return a decision rather
    /// than a rebound.
    /// </summary>
    public static double TubeHold = 0.15;

    /// <summary>
    /// Walk the tube towards the angle asked of it and draw the nearest pose.
    ///
    /// <b>It used to take no time, and that was a decision rather than an
    /// oversight</b>: there is nothing rendered between two rungs, so the
    /// argument ran that a tube crawling up an eleven-pose ladder is eleven
    /// poses of stutter. Watched on a board with levels it is the other way
    /// round - the lay is three to six pixels of muzzle, and arriving there
    /// between two frames is a lay nobody can see happen at all. Stepped, the
    /// same three pixels are a movement, which is what the eye reads.
    ///
    /// Lands exactly on the want, for <see cref="Gunnery.Traverse"/>'s reason:
    /// <see cref="Trained"/> is a gate, and a walk that always stopped a hair
    /// short would hold the gun closed forever.
    /// </summary>
    private void UpdateTube(Vehicle v, double delta)
    {
        SeedTube(v);
        // <b>Out of battery, nothing moves.</b> A tube coming down while it is
        // still coming home is two movements on one part, and the recoil is the
        // one the eye is already following. This is also what makes the pause
        // after a shot derived rather than guessed: 28 frames of it exist
        // whatever TubeHold says.
        if (v.Barrel.Live)
            return;
        if (v.TubeDwell > 0.0)
        {
            v.TubeDwell = Math.Max(0.0, v.TubeDwell - delta);
            return;
        }
        double diff = v.TubeWants - v.TubeDeg;
        double budget = TubeRate * delta;
        bool was = Math.Abs(diff) <= Gunnery.LayTolerance;
        v.TubeDeg = Math.Abs(diff) <= budget
            ? v.TubeWants : v.TubeDeg + Math.Sign(diff) * budget;
        // Arrived this frame, which is the only moment the settle can be armed:
        // asked of the want instead, it would re-arm every frame the gun stood
        // laid and the shot would never come.
        if (!was && Math.Abs(v.TubeWants - v.TubeDeg) <= Gunnery.LayTolerance)
            v.TubeDwell = TubeSettle;
        int rung = v.Atlas?.RungFor(v.TubeDeg) ?? 0;
        if (rung == v.Sprite.BarrelRung)
            return;
        v.Sprite.BarrelRung = rung;
        v.Sprite.QueueRedraw();
    }

    /// <summary>
    /// The angle this gun would be laid at to reach that cell, and the two
    /// figures the arc is sized from.
    ///
    /// <b>One question with two answers, and the class picks which.</b> A direct
    /// gun points at what it is shooting - the tube <em>is</em> the line of
    /// sight, and on a level board that is zero, which is why this was never
    /// needed until the board grew levels. A mortar points where the bomb has to
    /// leave, which is nowhere near the target and depends on how far away it
    /// is. See <see cref="Gunnery.LayDeg"/>.
    ///
    /// <b>The range is asked down a lane, because that is the only way a gun
    /// fires.</b> <see cref="HexField.LaneTo"/> answers -1 for a cell on no lane
    /// of this hex, and then there is no legal shot to size: the fallback is the
    /// longest throw, which is what an unlaid mortar would be standing at.
    ///
    /// <b>Past its reach the tube is laid as far as it goes and the bench says
    /// so once.</b> Two levels over one cell is 26.6 degrees - a position the
    /// rules now refuse (<see cref="Gunnery.Reaches"/>) but the mortar still
    /// reaches past its own band - and the alternative to a word is a tube
    /// pointing visibly short of where the round went, with nothing to say why.
    /// The band, not the cap, and it is asymmetric: a rung is a rotation applied
    /// to a tube the model already pointed somewhere, so what the mounting
    /// reaches is that rest plus the ladder.
    /// </summary>
    private (double Deg, int Cells, int Levels) Wanted(Vehicle v, Vector2I? onto)
    {
        int cells = Gunnery.LobReach;
        int levels = 0;
        if (Field is not null && onto is Vector2I at)
        {
            (_, int range) = Field.LaneTo(v.Cell, at);
            if (range > 0)
                cells = range;
            levels = Field.LevelAt(at) - Field.LevelAt(v.Cell);
        }
        double deg = Gunnery.LayDeg(v.Profile, cells, levels,
                                    Field?.StepGrade ?? 0.25);
        if (v.Atlas is not null)
        {
            (double low, double high) = v.Atlas.LayBand;
            if (v.Atlas.ElevRungs > 1
                && (deg > high + 1e-6 || deg < low - 1e-6)
                && _capped.Add(v))
                GD.Print($"lay: {v.Tag} wants {deg:F2}deg at {cells} "
                         + $"cell(s) and the tube reaches {low:F2}..{high:F2}"
                         + " - laid at its stop");
        }
        return (deg, cells, levels);
    }

    /// <summary>
    /// How high over the chord the round about to leave this tube will go, in
    /// screen pixels - what <see cref="Shell.Apex"/> is handed.
    ///
    /// <b>Off the pose that is drawn, not off the angle the rules asked
    /// for.</b> The two were one call while the lay took no time; now that the
    /// tube walks, a round sized off the want would leave a tube that has not
    /// got there yet - the very pair this arithmetic exists to keep together.
    /// So the trigger reads the tube, and the gate (<see cref="Trained"/>) is
    /// what makes the two agree on a shot the rules allowed.
    ///
    /// <b>Past the cap the round keeps the angle the rules gave it and the tube
    /// keeps its stop.</b> Snapping the shot to the mounting there would take a
    /// mortar's bomb and flatten it into a tank shell - the rules' event bent to
    /// fit the art - so the two are allowed to disagree exactly where the bench
    /// has already said out loud that they do.
    ///
    /// <b>The second leg never asks.</b> A round that bounced or went through
    /// (<see cref="Carry"/>) leaves flat whoever fired it, and the rules say so
    /// twice over: a V meets no armour it fails to pass, so a mortar's bomb has
    /// no ricochet to fly - GDD units.md, "рикошет бывает только от выстрелов
    /// LT, MT и HT".
    /// </summary>
    public float Arc(Vehicle shooter, Vector2I? onto)
    {
        if (Field is null)
            return 0.0f;
        (double deg, int cells, int levels) = Wanted(shooter, onto);
        SeedTube(shooter);
        double shown = deg;
        if (shooter.Atlas is not null && shooter.Atlas.ElevRungs > 1)
        {
            (double low, double high) = shooter.Atlas.LayBand;
            if (deg <= high + 1e-6 && deg >= low - 1e-6)
                shown = shooter.Atlas.LayOf(shooter.Sprite.BarrelRung);
        }
        return Gunnery.ApexPx(shooter.Profile, shown, cells, levels,
                              Field.StepGrade, Field.Reach, Field.RiseFactor);
    }

    /// <summary>Tanks that have already had their say about an angle their tube
    /// cannot reach, so the line is printed once rather than sixty times a
    /// second.</summary>
    private readonly HashSet<Vehicle> _capped = new();

    public bool Shoot(Vehicle shooter, Vehicle victim, double fromBearing,
                      int? level = null, Shell.Kind? ammo = null,
                      Shell.Onward goes = Shell.Onward.Stops,
                      int? onwardLevel = null, Leg? leg = null)
    {
        // Bumped before the aim rather than after it, because the aim depends on
        // it: the scatter hash is what puts the second round beside the first, so
        // the serial the round about to leave carries is the count as it is now.
        // It is also why the aiming ray has to predict with one more than this.
        victim.HitCount++;
        if (AimAt(shooter, victim, fromBearing, victim.HitCount, leg)
            is not Aimed shot)
        {
            victim.HitCount--;
            return false;
        }
        // <b>A lobbed round goes at the roof, and the plate half of the solution
        // is dropped rather than carried unused.</b> The aim above solves for a
        // face, an offset along it and a rise up its slope, and all three are
        // statements about armour a bomb arriving from overhead never meets -
        // GDD units.md, "для навесного огня миномёта зона значения не имеет". So
        // the armour half is written blank here exactly as Loose writes it for a
        // round going into the field, and the point is Vehicle.Roof. The
        // solution is still asked for and still able to refuse: an unmeasured
        // hull is one this tick has no idea how to hit, whichever way up.
        float arc = Arc(shooter, victim.Cell);
        // <b>The class, not the apex.</b> Every gun now lays above its own line
        // of sight, so an apex says only that the round is bent; what decides
        // that it comes down on a deck with no plate under it is whose gun it
        // is - see Shell.Overhead, which is where that cost is written down.
        bool over = shooter.Profile.Lobs;
        Send(shooter, new Shell
        {
            Shooter = shooter,
            Target = victim,
            ImpactLocal = over ? victim.Roof() : shot.Impact,
            From = shot.Muzzle,
            FromLift = leg?.Lift ?? shooter.LiftOf(shot.Muzzle),
            BoreMiss = shot.BoreMiss,
            Serial = victim.HitCount,
            // Carried rather than re-read on arrival: what the trigger decided
            // travels with the shell, because the alternative is a round that
            // changes calibre in flight because somebody turned a dial.
            Face = over ? "" : shot.Face,
            // Which way it came from, for the plate it will hit to mirror it
            // about - see Shell.Bearing.
            Bearing = fromBearing,
            Scatter = over ? 0.0f : shot.Scatter,
            Rise = over ? 0.0f : shot.Rise,
            Calibre = Ordnance.At(Calibre),
            Ammo = ammo ?? Ammo,
            Level = level ?? Gunnery.Penetration(shooter.Profile, victim.Profile),
            // And what it does after that, which is nothing at all unless the
            // rules said otherwise - see Carry.
            Goes = goes,
            NextLevel = onwardLevel,
            // And how it gets there, which is the shooter's class and nothing
            // else - see MovementProfile.Lobs. Not a parameter: a mortar has no
            // flat shot to ask for and nobody else has an arc.
            Apex = arc,
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
    ///
    /// <b><paramref name="onto"/> shortens the flight and never lengthens or
    /// bends it.</b> An order to shell a cell says where the round stops; it does
    /// not say which way the gun points, because the six lanes already do and the
    /// order is only ever given down one of them. So the walk still decides the
    /// direction and still decides what the round is allowed to cross - a wall or
    /// a hull short of the cell stops it there, exactly as it stops a shot fired
    /// by hand - and this is the lesser of the two runs. Written as a minimum
    /// rather than as an assignment for that reason: a round told to land four
    /// cells away through a wall two cells away lands on the wall.
    ///
    /// <b>Measured from the muzzle and in the board's own space, which is the
    /// space the answer is spent in.</b> The walk is measured from the contact
    /// point and then spent from the tube - 60 to 70px of difference the ray and
    /// the round both carry, and rightly, because what it decides there is which
    /// cell stops the line. Here it decides where a burst goes off, and 60px on a
    /// 190px cell pitch is most of the way to the next hex: measured from the
    /// contact point, a round sent one cell away landed over the far rim. So this
    /// one is <c>|anchor - muzzle|</c>, and the round lands on the cell's own
    /// anchor exactly - the point a tank standing there would touch the ground
    /// at, which is what "into that hex" has to mean.
    /// </summary>
    private void Loose(Vehicle shooter, Vector2I? onto = null)
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
        // The ordered cell when there is one, and otherwise wherever the walk
        // stopped: both are cells on this gun's own lane, which is what the
        // range has to be measured down - see Lay.
        float arc = Arc(shooter, onto ?? at);
        if (onto is Vector2I sent)
        {
            float far = Sent(Field, Origin, from, sent);
            // <b>A lob is not shortened by what it flies over</b> - GDD
            // classes.md, "снаряд приходит сверху... минуя всё, что стоит между
            // стрелком и гексом". The walk above is a rule about a flat round
            // crossing cells, and the mortar's bomb does not cross them: it goes
            // to the hex it was ordered to and comes down on it. So the run is
            // the order's own, and nothing blocked it.
            run = shooter.Profile.Lobs ? far : Mathf.Min(run, far);
            if (shooter.Profile.Lobs)
            {
                at = sent;
                blocked = false;
            }
        }
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
            Apex = arc,
        });
    }

    /// <summary>How far a round has to run from <paramref name="from"/> to come
    /// down on a cell's own anchor, in the board's space.
    ///
    /// A named static because it is the whole of what an order to shell a cell
    /// means, and because a check cannot conjure a vehicle - the reason
    /// <see cref="Vehicle.WadingAt"/> and <see cref="VehicleAudio.ImpactFor"/>
    /// are ones too. The lifted centre rather than the flat one: what it is
    /// compared against is a drawn point, and a cell up on a crown is drawn
    /// higher.
    ///
    /// <b>The centre and not the anchor, which is the trap the field documents
    /// and this was written into anyway.</b> A cell's anchor is the turret axis
    /// projected to screen and floats some 57px above the ground the hexagon lies
    /// on - <see cref="HexField.CentreOffset"/> - so aiming at it put every burst
    /// a quarter of a cell short along the lane. Measured: the crater from a round
    /// ordered into (4,4) came down at y=529 against the selection ring on that
    /// same cell at y=586, x identical to the pixel.</summary>
    public static float Sent(HexField field, Vector2 origin, Vector2 from,
                             Vector2I onto) =>
        (origin + field.CellCentre(onto) - from).Length();

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
            //
            // <b>Except the one round that never reaches the board</b>: a shot
            // the front or the rear plate spent leaves upward and its path ends
            // in the air, so the burst and the crater that every other loose
            // round earns would be a hole in the sky - see Shell.Skyward.
            if (!round.Skyward)
                Landed?.Invoke(round);
            return;
        }
        Land(round.Target, round.Face, round.Scatter, round.Rise, round.Calibre,
             round.Level, 1, round.Bearing, round.Ammo,
             // And which way it came at the armour, which is the one thing about
             // the arrival the round decides: a bomb comes down on the deck and
             // meets no plate at all - see Shell.Overhead, and Land's own
             // overhead beneath it.
             round.Overhead);
        Carry(round);
    }


    /// <summary>
    /// A round that did not stop where it landed: the second leg, out of the
    /// hull it just hit and down one of the six axes.
    ///
    /// <b>One mechanism for two rules</b> - GDD units.md "Рикошет" and
    /// classes.md "TD — Тяжёлый снаряд" - because from the board's side they
    /// are one event: a round leaves the tank it arrived at and flies on until
    /// something else stops it. What differs is the axis and which plate it
    /// leaves by, and that is four lines apart.
    ///
    /// <b>Raised here rather than by whoever fired the shot</b>, and it is not
    /// a preference. The player of events cannot do this: with the tracer's
    /// trail off a round is freed on the frame it lands
    /// (<see cref="Shell.Expired"/>), so a step waiting for the arrival would
    /// find nothing to ask where the plate was. The shell carries the
    /// instruction instead - <see cref="Shell.Goes"/> - which is this file's
    /// own rule about what travels with a round.
    ///
    /// <b>The two legs are checked against the level the round carried</b>: a
    /// round that got in does not come off a plate, and one that stopped on the
    /// armour did not pass through it. A caller that asks for a bounce on a
    /// penetration is asking for two pictures of one shell, which is the double
    /// model this bench refuses everywhere else.
    ///
    /// <b>Where it leaves from is a plate, and for the pass it is the far
    /// one.</b> A ricochet leaves where its own fan of spall stands
    /// (<see cref="Vehicle.Plated"/>, the same point <see cref="Land"/> handed
    /// <c>Bounced</c>), and a round that went through leaves by the plate
    /// facing the way it is going - <see cref="AtlasSet.FaceFor"/> asked with
    /// the direction of travel instead of the bearing it came from. Half a
    /// hull's length apart, which is most of a tracer's streak, so it is worth
    /// the one call rather than starting both at the hole.
    ///
    /// <b>The walk is the victim's</b>: <see cref="Reach"/> asked of the tank
    /// that was hit already skips that tank, knows the other hulls, the walls
    /// through <see cref="Barred"/> and the ground that stands too high. What
    /// it stops on is either a tank - and then the round arrives on armour with
    /// its outcome, <see cref="Shell.NextLevel"/>, or with the classes' answer
    /// when the rules left it at "с той же огневой мощью" - or anything else,
    /// and then it is a round going into the field exactly as
    /// <see cref="Loose"/> sends one. The run is measured from the hull's
    /// contact point and spent from the plate, which is <see cref="Loose"/>'s
    /// own bias and bounded by the same handful of pixels.
    ///
    /// <b>One continuation and never two.</b> The leg is sent with the default
    /// <see cref="Shell.Onward.Stops"/>: "второго рикошета нет" is the rules'
    /// sentence, and a chain would be this method calling itself with nothing
    /// to stop it but the edge of the board.
    /// </summary>
    private void Carry(Shell round)
    {
        if (round.Goes == Shell.Onward.Stops || round.Target is not Vehicle hit
            || round.Ammo != Shell.Kind.Ap || Field?.Atlas is null)
            return;
        if (round.Goes == Shell.Onward.Bounces ? round.Level > 0 : round.Level <= 0)
            return;
        double hull = hit.Sprite.HullFacing;
        int axis;
        Vector2 leaves;
        if (round.Goes == Shell.Onward.Bounces)
        {
            axis = hit.Deflection(round.Face, round.Bearing);
            leaves = hit.Plated(round.Face, round.Scatter, round.Rise);
        }
        else
        {
            // Straight on: the axis it arrived by, out of the plate that faces
            // that way. The scatter is mirrored with the plate - the far side's
            // tangent runs the other way round the hull - which is a statement
            // about a hole nobody can see rather than a measurement, and is
            // named as one.
            double onward = Angles.Mod(round.Bearing + 180.0, 360.0);
            axis = HexField.EdgeHeadings[Angles.SideFor(onward)];
            leaves = hit.Plated(hit.Atlas.FaceFor(onward, hull),
                                -round.Scatter, round.Rise);
        }
        Vector2 from = hit.Spot(leaves);
        float lift = hit.LiftOf(from);
        // A plate that swallows the round rather than turning it - the front and
        // the rear - and the rules do not stop there: the shot "уходит вверх".
        // So it leaves, straight up out of the plate it hit, and the leg that
        // used to be missing is the one the picture was drawn for. See Skyward.
        if (axis < 0)
        {
            Skyward(round, from, lift);
            return;
        }
        Vector2 dir = Field.Atlas.GroundDirection(axis);
        if (dir.LengthSquared() < 1e-6f)
            return;
        (float run, Vector2I? at, bool blocked) = Reach(hit, dir);
        if (blocked && at is Vector2I cell
            && Vehicle.At(Vehicles, cell) is Vehicle next
            && Shoot(round.Shooter, next, Angles.Mod(axis + 180.0, 360.0),
                     round.NextLevel ?? Spent(round, next),
                     leg: new Leg(from, lift, dir)))
            return;
        Send(round.Shooter, new Shell
        {
            Shooter = round.Shooter,
            Target = null,
            Blocked = blocked ? at : null,
            Ammo = round.Ammo,
            Ground = from + dir.Normalized() * run,
            // Level at both ends, by Loose's argument: the walk that measured
            // the run is a level line, and this is the same shot going on.
            GroundLift = lift,
            From = from,
            FromLift = lift,
            // The armour half means nothing on this one - see Shell.Target.
            ImpactLocal = Vector2.Zero,
            Serial = 0,
            Face = "",
            Scatter = 0.0f,
            Rise = 0.0f,
            BoreMiss = 0.0f,
            // The calibre the round left the gun with, not the dial as it
            // stands now: it is the same shell.
            Calibre = round.Calibre,
            Level = 0,
        });
    }

    /// <summary>
    /// The leg a swallowed round flies: straight up the screen, out of the plate
    /// that spent it, and off the board.
    ///
    /// <b>The rules' own sentence drawn rather than assumed.</b> Only a side
    /// turns a shot on - <see cref="Gunnery.Deflect"/>, whose -1 is the front and
    /// the rear - and what a spent round does there is go up. Until this the -1
    /// was the <em>absence</em> of a second leg, so the tracer stopped dead
    /// inside the hull it hit while <see cref="Vehicle.Spent"/> was already
    /// sending the fan of spall up off the same plate: the picture said the round
    /// left and the round did not.
    ///
    /// <b>Up the screen in board space, which is where the shell's own path
    /// lives.</b> <see cref="Shell.PointAt"/> lerps two board points, and this
    /// board's height is minus y and nothing else - the same convention
    /// <see cref="Vehicle.LiftOf"/> is quoted in. So the far end is the plate
    /// with <see cref="SpentRise"/> cells taken off its y, the lift is the
    /// plate's own at both ends, and there is no apex: a round leaving straight
    /// up is not on an arc, it is on a line, and the tangent
    /// (<see cref="Shell.Heading"/>) therefore points the streak up as well.
    ///
    /// <b>No walk, because there is nothing along it to walk.</b>
    /// <see cref="Reach"/> answers what a round crosses <em>on the board</em>;
    /// this one leaves the board, so no hull, wall or rise can stop it and the
    /// run is a fixed distance rather than a measured one. Nothing is blocked,
    /// nobody is hit, and <see cref="Shell.Skyward"/> is what keeps the end of
    /// the path from being drawn as a landing.
    /// </summary>
    private void Skyward(Shell round, Vector2 from, float lift)
    {
        float tile = Field?.Atlas is null ? 1.0f : Field.Atlas.HexRect.Size.X;
        Send(round.Shooter, new Shell
        {
            Shooter = round.Shooter,
            Target = null,
            Blocked = null,
            // What keeps the far end from becoming a crater in the sky.
            Skyward = true,
            Ammo = round.Ammo,
            Ground = from - new Vector2(0.0f, SpentRise * tile),
            // The plate's own at both ends: the climb is in the board point, and
            // stating it twice would be the round rising and its shadow's datum
            // rising with it.
            GroundLift = lift,
            From = from,
            FromLift = lift,
            // The armour half means nothing on this one - see Shell.Target.
            ImpactLocal = Vector2.Zero,
            Serial = 0,
            Face = "",
            Scatter = 0.0f,
            Rise = 0.0f,
            BoreMiss = 0.0f,
            // The calibre the round left the gun with: it is the same shell.
            Calibre = round.Calibre,
            Level = 0,
        });
    }

    /// <summary>
    /// How far up a spent round climbs before the drawing stops, in cells.
    ///
    /// <b>Quoted in cells and generous, because what it has to clear is the
    /// window rather than a distance.</b> A round going up is a round leaving,
    /// and the only thing the far end decides is where the streak stops being
    /// drawn - so it has to be past the top of the view at the zoom the board is
    /// usually watched at. Four cells is about a thousand pixels, which at
    /// <see cref="Shell.Speed"/> is two thirds of a second of climb: long enough
    /// to read as a round departing, short enough that the smoke it leaves is
    /// gone inside the second and a half a hit already takes.
    /// </summary>
    public const float SpentRise = 4.0f;

    /// <summary>
    /// What a round that flew on does to the next tank, when the rules left the
    /// answer to the classes.
    ///
    /// <b>Two rules and one difference.</b> A bounced round flies on "с той же
    /// огневой мощью" - GDD units.md - so it is judged exactly as the gun's own
    /// shot would be, which is what <c>level: null</c> already means to
    /// <see cref="Shoot"/>. A round that went through has paid for the pass:
    /// <see cref="Gunnery.PassedMight"/>, classes.md's own −1, and it is the
    /// only place in the game where a shell arrives with a might that is not
    /// its gun's.
    ///
    /// Null and not zero for the bounce, because "ask the table" and "it gets
    /// nowhere" are different answers and the second one would quietly forbid
    /// a ricochet from ever holing anything.
    /// </summary>
    private static int? Spent(Shell round, Vehicle next) =>
        round.Goes == Shell.Onward.Passes
            ? Gunnery.Penetration(Gunnery.PassedMight(round.Shooter.Profile),
                                  next.Profile)
            : null;

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
    ///
    /// <paramref name="onto"/> is the cell the round was <em>sent to</em>, for a
    /// gun firing on an order to shell a cell, and null for a gun fired by hand.
    /// It reaches nothing but <see cref="Loose"/>, and there it is a stop rather
    /// than an aim: see that method. Passed rather than read off the vehicle
    /// because who the gun is shooting at is the harness's half of a shot and has
    /// been since the trigger was made one - the same argument
    /// <see cref="Launch"/> is a hook for.
    ///
    /// <paramref name="launch"/> is that answer for this one shot, in place of
    /// the standing <see cref="Launch"/>: a caller that already knows who the
    /// round is for - the event player, whose shot arrives with its outcome
    /// decided - says so here and gets the other five things off the trigger
    /// with it. The first cut of the event bench called <see cref="Shoot"/>
    /// directly and got a round with no flash, no recoil, no dust and no report.
    /// </summary>
    public void Fire(Vehicle v, Vector2I? onto = null,
                     Func<Vehicle, bool>? launch = null)
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
        if (v.Atlas is not null)
            Quake(Shook.Gun, v, v.Atlas.GroundDirection(v.Sprite.TurretFacing));
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
        Func<Vehicle, bool>? who = launch ?? Launch;
        if (who is null || !who(v))
            Loose(v, onto);
        // And the tube comes down, last of all and after the round has left.
        //
        // <b>A gun goes back to its loading angle, and here that is also the
        // only way the lay is ever watched twice.</b> Held at the angle it
        // fired at, a tank with a standing order lays once in its life: the
        // first round walks the tube up and every round after it leaves from a
        // tube that never moved. Stowed at the trigger, each round is a lay,
        // a shot and a return - which is what the ladder was rendered for.
        //
        // <b>After the launch, not before.</b> Arc reads the rung that is drawn
        // to size the round's own flight, and a tube stowed first would send the
        // shell along an angle the picture never showed.
        //
        // What keeps it down is Train, which answers rest for as long as the
        // round is loading; what brings it back up is the same call, the frame
        // the reload runs out.
        StowTube(v);
        // And it is held where it fired for a beat first - see TubeHold. Set
        // after the stow, because the stow is what the beat is delaying.
        v.TubeDwell = TubeHold;
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
                         int? level, int bite, Shell.Kind? ammo = null)
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
        // <b>What the gun is loaded with, unless the caller was carrying its
        // own.</b> A fired shell settled its kind at the trigger and brought it
        // along, for the reason it brought its calibre; a keypress has no round,
        // so it spends the dial. Defaulted rather than required because every
        // caller but one wants the dial, and a required parameter would have them
        // all writing the same field out.
        Land(victim, face, scatter, rise, calibre, level, bite, from,
             ammo ?? Ammo);
    }

    /// <summary>
    /// One hull driven into another: a dent on the plate it struck, the crew
    /// hearing it, and the kill if that was the third one.
    ///
    /// <b>Beside <see cref="TakeHit"/> rather than through it, and the difference
    /// is everything that is not here.</b> A shell arriving brings a burst, a
    /// spall or a slam off the plate, a light inside the hull if it got through,
    /// a shock to the wood and a calibre to remember - all of which are pictures
    /// of a round, and there is no round in a ram. What a ram and a shell do have
    /// in common is the only thing taken: a plate is picked off the bearing, it
    /// takes a level, and three levels past the paint finish the tank. So this is
    /// <see cref="Land"/>'s tail and nothing of its body.
    ///
    /// <b>The mark it leaves is a shell's mark, and that is a borrowing said out
    /// loud rather than a fresh layer.</b> The armour has three rendered scar
    /// levels and no fourth for a dent; drawing nothing at all would make a tank
    /// three rams from dead look exactly like one that had never been touched,
    /// which is the worse of the two lies. See <see cref="Gunnery.RamLevel"/> for
    /// what a ram is worth against what.
    ///
    /// Returns the level of the mark, or -1 for a hull with no armour measured -
    /// <see cref="TakeHit"/>'s answer, for the same reason.
    /// </summary>
    public int Rammed(Vehicle victim, double fromBearing, int level)
    {
        if (!victim.Atlas.HasHit)
            return -1;
        // Snapped to a side, as every bearing anything hands in is: the ram comes
        // out of a neighbouring cell, so it arrives along a flat side by
        // construction - this is the guard, not the conversion.
        double from = HexField.EdgeHeadings[Angles.SideFor(fromBearing)];
        // The same counter a shell bumps, because it is what puts this dent
        // somewhere other than the last one - see ScatterAt. A ram counts into
        // the same tally for the same reason it takes the same plate: what the
        // hull has been through is one number.
        victim.HitCount++;
        string face = victim.Atlas.FaceFor(from, victim.Sprite.HullFacing);
        int got = victim.Sprite.DamageTo(face, ScatterAt(victim.HitCount),
                                         RiseAt(victim.HitCount), level);
        // The impact take, chosen by the level for the reason a shell's is: a ram
        // that only scuffs the paint is heard scuffing it.
        victim.Audio?.Struck(got, victim.HitCount);
        // A dent knocks out only what it penetrates, and finishes anything
        // already out - FateOf, the rules' sentence for a shell and a ram alike.
        Befall(victim, face, got);
        return got;
    }

    /// <summary>A round arriving, with everything about it already settled.
    ///
    /// Split out of <see cref="TakeHit"/> when the shell got a flight: the key
    /// press works out which plate at the moment it lands, and a fired round
    /// worked it out at the trigger and carried it. Both end here, so there is
    /// one place where a tank takes a hit however the hit was arranged.</summary>
    /// <param name="overhead">True for a round that came down on the deck - the
    /// mortar's bomb, GDD classes.md "HM — Навес". <b>It is not a fifth plate;
    /// it is the absence of one.</b> The zone does not apply ("для навесного
    /// огня миномёта зона значения не имеет"), so the round wears no armour,
    /// leaves no scar, cannot bounce, and bursts along a normal that points
    /// straight up rather than out along a face. What it keeps is everything
    /// that is about the hull rather than about the plate: the sound, the shock
    /// to the wood, and the fate. <paramref name="face"/> and
    /// <paramref name="from"/> mean nothing while it is set and are passed
    /// blank, the way <see cref="Loose"/> writes the armour half of a round
    /// going at the ground.</param>
    public void Land(Vehicle victim, string face, float scatter, float rise,
                      float calibre, int? level, int bite, double from,
                      Shell.Kind ammo, bool overhead = false)
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
        // <b>And a round that came down on the roof marks nothing at all.</b>
        // The four plates are the six faces of the hex sorted by the hull's
        // heading (GDD units.md, "Зоны брони"), and a bomb arriving from
        // overhead belongs to none of them - "для навесного огня миномёта зона
        // значения не имеет". So there is no plate to wear, no scar to put on
        // one, and the depth is the matchup's own answer carried in by the round
        // rather than the armour's tally. See Overhead.
        int got = overhead
            ? level ?? bite
            : level is int cap
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
        //
        // <b>And the shell's own kind is asked before the plate is, because a
        // round that bursts does not bounce.</b> HE stops on the face and goes
        // off there whether or not the armour held, so it is not a third case
        // beside "in" and "off" - it is a different question, asked first, and
        // the damage question keeps its own two answers underneath. Read the
        // other way round - a bounce that then checks the filling - an HE round
        // that failed to penetrate would have been either a ricochet or a burst
        // by a rule written in whichever order the two ifs happened to be in.
        //
        // <b>Exactly one picture is drawn whichever way this goes, and the scar
        // is drawn by none of the three.</b> It is written by Damage/DamageTo
        // above this, which every hit reaches alike, so what the plate keeps does
        // not depend on which picture was chosen - a bounce keeps its level-0
        // scorch and an HE hit keeps the mark its own level earned. Worth stating
        // because the ricochet reads as leaving nothing, and what it leaves
        // nothing of is the rendered burst.
        //
        // <b>The overhead round is asked before all of it, and it is the one
        // fork that is about the round's path rather than about its filling.</b>
        // A bomb coming down on the deck cannot bounce - there is no plate to
        // turn it - and the burst it makes is the same fireball with its normal
        // straight up instead of out along a face. So it is one case and not a
        // fourth column in the other two.
        bool burst = !overhead && ammo == Shell.Kind.He && Slam;
        bool bounced = !overhead && !burst && got <= 0 && Bounce;
        if (overhead)
        {
            // Nothing else is drawn on this hull, switch or no switch: the
            // rendered pair below is a burst against a plate placed by where on
            // that plate the round hit, and there is no plate here. A bomb on
            // the deck with the burst switched off is a hull that is simply hit.
            //
            // Up the screen at full length, and both halves of that are the
            // measurement: ProcSlam.Aim reads the normal as a screen vector
            // whose direction is where the gases go and whose length is the
            // share of a ground length that survived the projection. A roof
            // faces the camera squarely on this board, so nothing of it is lost
            // - which is why this is a named vertical and not a GroundDirection
            // of some bearing that would foreshorten.
            if (Slam)
                Blasted?.Invoke(victim, victim.Roof(), new Vector2(0.0f, -1.0f),
                                false);
        }
        else if (burst)
        {
            (Vector2 plate, Vector2 outward) = victim.Blown(face, scatter, rise);
            Blasted?.Invoke(victim, plate, outward, victim.Turned(face));
        }
        else if (bounced)
        {
            (Vector2 plate, Vector2 away) = victim.Graze(face, scatter, rise, from);
            Bounced?.Invoke(victim, plate, away, victim.Turned(face));
        }
        else
        {
            // <b>And whether it got in travels with it, which is the one thing
            // the rendered pair never said.</b> That pair is the outside of a
            // penetration - a puff of earth-tan dust with a warm core - and it is
            // drawn identically whether the round went through or merely failed to
            // bounce. See ProcPierce, which is the inside and is drawn as well as
            // the pair rather than instead of it: one shell, two sides of one
            // plate, one picture each.
            victim.Hit.Strike(face, scatter, rise, calibre,
                              Pierce && got > 0);
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
        // <b>One penetration knocks the tank out, and the next hit of any kind
        // destroys it</b> - GDD states.md, through FateOf; the three-round tally
        // that stood here is gone, see Wreck.Disabled. Judged on this round's
        // level rather than on what the tank has taken, because the rule is
        // about this round: the matchup decides whether it got in, and getting
        // in is the whole of what it takes. <b>With the plate that did it</b>,
        // which is what decides whether the ammunition or the fuel went - see
        // RackedBy. In scope here and nowhere else: the callers that kill by hand
        // have no round and no plate.
        Befall(victim, face, got);
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
            sprite.HitThrough = hit.Through;
        }
        // The frame beside the phase it rounds to - see HitLoop.Elapsed. Written
        // every frame rather than on change, because it changes every frame by
        // definition.
        sprite.HitFrame = hit.Elapsed;
        if (phase != sprite.HitPhase)
        {
            sprite.HitPhase = phase;
            sprite.QueueRedraw();
        }
        hit.Advance();
    }

    // --- the ring -----------------------------------------------------------

    /// <summary>
    /// What is swinging the turret, when neither the gunnery nor a hand is -
    /// the rules' own two movements of the ring, and nothing else.
    ///
    /// GDD units.md gives the turret three sentences and they are all here:
    /// laying it on one of the six lanes is free and happens when the tank
    /// fires or goes into ambush (<see cref="Onto"/>); moving brings it
    /// forward (<see cref="Forward"/>); and between turns it stays where it was
    /// left (<see cref="None"/>) - which is why "nobody is turning it" is a
    /// value here rather than the absence of one. Direction on the board is a
    /// rule in exactly one place, ambush, and a picture everywhere else.
    /// </summary>
    public enum Swing
    {
        /// <summary>Nobody. The gun keeps its bearing, and keeps it across
        /// turns.</summary>
        None,

        /// <summary>On to a fixed bearing: a lane, in the rules.</summary>
        Onto,

        /// <summary>On to the hull's own heading, wherever the hull turns while
        /// it comes - so a gun stowing through a corner arrives forward and not
        /// on the bearing the corner started from.</summary>
        Forward,
    }

    /// <summary>Lay the ring on a bearing and leave it coming: what a shot or an
    /// ambush order does. A casemate is ignored on purpose - its gun is its
    /// hull, and turning the hull is movement with a price in steps, which is
    /// not this. See <see cref="UpdateTurret"/>.</summary>
    public void SwingTo(Vehicle v, double onto)
    {
        if (!v.Profile.Turreted)
            return;
        v.Ring = Swing.Onto;
        v.RingOnto = Angles.Mod(onto, 360.0);
    }

    /// <summary>Stow the gun: bring the ring forward on to the hull. What
    /// moving does to it, and the one call <see cref="UpdateTurret"/> makes on
    /// itself.</summary>
    public void SwingForward(Vehicle v)
    {
        if (!v.Profile.Turreted)
            return;
        v.Ring = Swing.Forward;
    }

    /// <summary>Forget whatever the ring was owed: what anybody who puts the
    /// turret at an angle by hand has to say, or the swing would take the gun
    /// straight back off them. The bench's shooter is the one caller - it is
    /// placed, laid and fired in one frame, and a stow left over from an earlier
    /// drive would walk the gun off the victim while the round was in the air.
    /// </summary>
    public void DropSwing(Vehicle v) => v.Ring = Swing.None;

    /// <summary>Whether the ring is still coming round. What an event waits on
    /// before the next one starts - <c>Playback.TurretTo</c> - and it answers no
    /// at once for a casemate, so a queue behind one does not hang.</summary>
    public bool Swinging(Vehicle v) => v.Ring != Swing.None;

    /// <summary>
    /// The ring, turned by the rules rather than by the gunnery.
    ///
    /// <b>Moving arms it every frame, and that is the rule rather than an
    /// edge.</b> "Сходил - башня смотрит вперёд" is a statement about the end of
    /// the move, and the hull turns all the way through one: a stow armed once
    /// at the first frame would aim at the bearing the tank set off on and
    /// arrive pointing off the corner it drove round. Re-asking every frame
    /// costs nothing - a ring already forward has a zero-degree swing - and is
    /// the only version that survives a stabilised turret, where the hull keeps
    /// opening the gap the whole way.
    ///
    /// <b>It finishes what it started after the tank stops</b>, because the
    /// alternative is a turret parked half way round: the four ways the ring can
    /// be taken away from it are all somebody else asking for the gun, and a
    /// drive ending is not one of them. A short hop with a long swing is the
    /// case that shows the difference.
    ///
    /// <b>Four claims beat it, and the last of the four is the A/B switch.</b> A
    /// target or a mark means the gunnery is laying the gun frame by frame
    /// (<see cref="Aim"/>), and two writers on one angle is a turret that
    /// shudders between them; a wreck holds no gun; a hand on the spin keys or
    /// the mouse is the bench being driven. And a turret asked to hold its world
    /// heading is a gunner holding it there - the stabiliser that
    /// <see cref="TankSprite.TurretHoldsHeading"/> is - so the rule applies when
    /// nobody is, which is the default a tank comes up in and the one the game
    /// plays on.
    /// </summary>
    public void UpdateTurret(Vehicle v, double delta)
    {
        if (v.Target is not null || v.Mark is not null || v.Wreck.Out
            || v.Sprite.TurretHoldsHeading
            || (v == Driven && TurretHeld?.Invoke(v) == true))
        {
            v.Ring = Swing.None;
            return;
        }
        if (v.Moving)
            SwingForward(v);
        if (v.Ring == Swing.None)
            return;
        // The hull's heading read this frame, not the one the swing was armed
        // on: see the summary - a stow is on to the hull, and the hull moves.
        double onto = v.Ring == Swing.Forward ? v.Sprite.HullFacing : v.RingOnto;
        double was = v.Sprite.TurretFacing;
        // The ring's own rate, not Gunnery.LayRate: that one answers "the ring
        // or the whole tank, whichever lays this gun", and nothing but a ring
        // gets here.
        double now = Gunnery.Traverse(was, onto,
                                      Gunnery.TraverseRate(v.Profile) * delta);
        if (now != was)
        {
            v.Sprite.TurretFacing = now;
            v.Sprite.QueueRedraw();
        }
        // Traverse lands exactly on the bearing when it is within reach - the
        // property the fire gate already depends on - so arrival is an equality
        // and not a tolerance.
        if (Math.Abs(Angles.WrapAngle(onto - now)) < 1e-9)
            v.Ring = Swing.None;
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
        // A ring still coming round is the fourth thing that has the turret,
        // and the one that outlives the reason it was armed: a tank that stops
        // half way through stowing is no longer moving, so without this the sway
        // would take the gun over from the stow and leave it pointing anywhere.
        if (!ScanEnabled || v.Moving || v.Target is not null || v.Wreck.Out
            || Swinging(v)
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
