using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The masonry standing on a board, as one thing: which walls are on it, what a
/// round and a hull do to them, and what the board is then told about its own
/// edges.
///
/// <b>Lifted out of <see cref="TankBench"/> whole rather than written a second
/// time</b> - <see cref="WallProp"/>'s own argument, one level up. <c>WallProp</c>
/// is a wall on a cell; this is every wall on a board, and what it adds is the
/// three things a single prop cannot answer: which prop a round stopped at, which
/// rim a hull crossed this frame, and which of a cell's six edges still stand.
/// A second scene that wanted masonry with a tank in it had two ways to get it -
/// copy the ram watcher and the record, or share them - and the copy is what
/// <c>WoodBench</c> refuses in the same words.
///
/// <b>The ram watcher is the part that had to be shared.</b> Its traps are not
/// in the geometry but in the bookkeeping: a pivot must fire nothing while it
/// turns, and the nose's diff can span two rims when it does turn, so the chain
/// owes an answer for every rim it names. A second copy of that in a second
/// scene is the same bug waiting for the first turn - see <see cref="Watch"/>,
/// where both are argued.
///
/// <b>It knows no scene.</b> Handed a field, a stage and a tick like
/// <see cref="Playback"/> is; which walls stand on the board, which dials move
/// them and which button rams are the caller's. What a board's masonry <i>is</i>
/// is here.
/// </summary>
public sealed class WallField
{
    /// <summary>The board the walls stand on. Every distance is a fraction of a
    /// cell of it.</summary>
    public required HexField Field { get; init; }

    /// <summary>The stage that draws it - the depth the bricks sort against and
    /// the contact point a hull is measured from. Null on a 2D board, which is a
    /// board with no walls drawn on it.</summary>
    public Stage3D? Stage { get; init; }

    /// <summary>Where the board's own origin is in screen space.</summary>
    public Vector2 Origin { get; init; }

    /// <summary>The tick whose world this is. Read for two things and both are
    /// the round's: which tank is driven (<see cref="TankTick.Driven"/>) and what
    /// calibre the burst is drawn at.</summary>
    public required TankTick Tick { get; init; }

    /// <summary>Order the driven tank to a cell - the caller's pathing, because
    /// a route is the board's business and not the masonry's. Null on a board
    /// that drives nothing; then <see cref="RamAt"/> does nothing, which is the
    /// honest answer for a board with no driver.</summary>
    public Func<Vector2I, bool>? Order { get; init; }

    /// <summary>Whether the order under way asked to ram - see
    /// <see cref="Sweeps"/>. The caller's, because what an order meant is the
    /// caller's to say.</summary>
    public bool Ramming;

    /// <summary>Whether any drive rams - <c>--drive-rams</c>, the old answer
    /// kept so the two can be looked at side by side.</summary>
    public bool DriveRams;

    /// <summary>A multiplier over the round's own calibre when it reaches the
    /// rig - the bench's force dial. One, and then the round is what it is.
    /// </summary>
    public float Force = 1.0f;

    /// <summary>Whether the rig draws its own tracer for a round that reached
    /// it. Off, because a gun's shell has a tracer already.</summary>
    public bool Beam;

    /// <summary>The driven tank, or null when nothing is being driven.</summary>
    private Vehicle? Driven => Tick.Driven;

    private readonly List<WallProp> _walls = new();

    /// <summary>The walls on the board, in the order they were stood.</summary>
    public IReadOnlyList<WallProp> Walls => _walls;

    /// <summary>Take a wall that has already been built - the caller stands it,
    /// because which cells, which recipe and which side differ per board, and
    /// tell the board about its edges at once.</summary>
    public void Add(WallProp prop)
    {
        _walls.Add(prop);
        Restate(prop, laying: true);
    }

    /// <summary>Where a wall is in that list, or -1 - what a report that names
    /// walls by index needs.</summary>
    public int IndexOf(WallProp prop) => _walls.IndexOf(prop);

    /// <summary>Whether the drive under way is only the run-up to a ram, so that
    /// a caller with its own queue of legs does not claim the standstill this one
    /// is waiting for - see <see cref="_lineUp"/>.</summary>
    public bool Lining => _lineUp is not null;

    /// <summary>Every wall has been laid again - what a dial that replaces the
    /// bodies leaves behind. The record is written whole, because masonry that
    /// has been rebuilt is masonry that stands: <see cref="HexField.Breach"/>
    /// only ever takes an edge off, and a board told about a re-lay one edge at a
    /// time would keep every gap the last collapse made.</summary>
    public void Laid()
    {
        foreach (WallProp prop in _walls)
            Restate(prop, laying: true);
    }

    /// <summary>Whether a wall already stands on a cell.</summary>
    public bool Stands(Vector2I cell)
    {
        foreach (WallProp prop in _walls)
            if (prop.Cell == cell)
                return true;
        return false;
    }

    /// <summary>Drop every wall - the caller is about to free the props. The box
    /// is forgotten with them, which is what keeps <see cref="Rebox"/> from
    /// dismounting a rig that no longer exists.</summary>
    public void Clear()
    {
        _walls.Clear();
        Forget();
    }

    /// <summary>Forget what the last collapse left behind, keeping the walls:
    /// what a re-lay needs. The box first, for <see cref="Clear"/>'s reason.
    /// </summary>
    public void Forget()
    {
        _boxed = null;
        _noseCell = null;
        _lineUp = null;
        Named = null;
        RamSpeed = -1.0;
        _stated.Clear();
    }

    /// <summary>How fast the tank was going, in metres a second, when the wall
    /// first let go of a piece - negative until it has.
    ///
    /// <b>Reported, because it is neither of the two numbers anybody would
    /// guess.</b> The medium's cruise is 240px/s, which at 30.5px to the metre is
    /// 7.9 m/s against the <see cref="WallRig.RamSpeed"/> of 3.9 the shot was
    /// tuned at - but a tank braking to stop on the wall's cell meets the masonry
    /// an apothem short of where it is aiming to stand, so what it actually
    /// arrives at is neither. Without this figure "the wall went off like a break
    /// shot" cannot be told from "the wall is tuned wrong".</summary>
    public double RamSpeed = -1.0;

    /// <summary>
    /// Write which of a cell's six edges still carry masonry on to the board -
    /// <see cref="HexField.SetSides"/> when the wall is being laid, and
    /// <see cref="HexField.Breach"/> for every edge that has gone since.
    ///
    /// <b>Measured off the bricks, never off the bearing and the side count.</b>
    /// <see cref="WallProp.Bars"/> already refuses to derive it, and for the
    /// reason that matters twice as much here: what the board records has to
    /// fall with the leaf, and a mask worked out from the recipe would still say
    /// six sides stand on a ring a tank has driven through.
    ///
    /// The direction of an edge is taken off the field, so this asks the same
    /// question the round does - <see cref="Barring"/>, with the shot's direction
    /// swapped for the six the grid has.
    ///
    /// <b>Walked in <see cref="Masonry.Headings"/> order, which is the mask's
    /// order and is not <c>HexField.EdgeHeadings</c>.</b> <c>Masonry</c> names
    /// the difference and calls it "a bug waiting to be written"; it was written
    /// here, and it was written invisibly. The two run opposite ways
    /// (<c>EdgeIndex</c> counts from 330), so a mask built off the ascending
    /// array and read back through <c>SideStands</c> is every edge mirrored -
    /// and on a board whose walls are a ring, a sealed cell and an empty one,
    /// every mask that had ever been looked at was symmetric. What found it was
    /// <see cref="HexField.Breach"/>: the first caller that writes one edge
    /// rather than all six, measured on the ram tour as a leaf rammed at 30
    /// taking the edge at 330 off the board's record.
    ///
    /// <b>Laying writes and falling breaches, and they are not the same
    /// sentence.</b> A cell the map marked comes up sealed on all six
    /// (<c>HexField.SetSides</c>'s own remark), and a wall covering a run of
    /// three has to write the three; after that the only thing that may happen to
    /// the record is an edge going, so it goes through <c>Breach</c> one at a
    /// time. Written as a mask both times it was the same arithmetic on this
    /// board and the wrong shape on the next one: a mask is what one prop can
    /// see, so a second wall laid on the same cell would have its edges rubbed
    /// out by the first one's re-measure - and nothing anywhere would say an
    /// edge had come down, which is the event the rules are waiting for.
    /// </summary>
    /// <param name="laying">Whether the wall is being stood up now. The mask is
    /// written whole then and never after.</param>
    public void Restate(WallProp prop, bool laying = false)
    {
        int mask = 0;
        Vector2 middle = Field.FlatAnchor(prop.Cell);
        for (int bit = 0; bit < Masonry.Headings.Length; bit++)
        {
            Vector2I next = HexField.Step(prop.Cell, Masonry.Headings[bit]);
            // The mask is what stops a drive, so it is asked for a hull: the
            // capon's slit stops a tank and not a round - see WallProp.Bars.
            if (prop.Bars(Field.FlatAnchor(next) - middle, crossing: true))
                mask |= 1 << bit;
        }
        _stated[prop.Cell] = (prop.Rig?.Loose ?? 0, prop.Rig?.Broken ?? 0);
        if (laying)
        {
            Field.SetSides(prop.Cell, mask);
            return;
        }
        int was = Field.SidesAt(prop.Cell);
        for (int bit = 0; bit < Masonry.Headings.Length; bit++)
        {
            if ((was & (1 << bit)) == 0 || (mask & (1 << bit)) != 0)
                continue;
            // Asked of the board rather than assumed: Breach answers whether that
            // side was standing until now, and an edge two walls share may have
            // been taken off by the other one already.
            if (Field.Breach(prop.Cell, Masonry.Headings[bit]))
                Down?.Invoke(prop.Cell, Masonry.Headings[bit]);
        }
    }

    /// <summary>An edge of a cell has lost its masonry: the rules' own
    /// <c>WallDestroyed(cell, face)</c>, raised once per edge and at the moment
    /// the board stops calling it walled.
    ///
    /// <b>Raised by the falling and not by the strike</b>, for
    /// <see cref="Restated"/>'s reason: a section let go of is not yet a section
    /// gone, and what the rules are waiting for is the edge, not the impulse. So
    /// a ram that enters a leaf and a shell that breaches one arrive here by the
    /// same door, and so does a leaf brought down by a neighbour's blast.
    ///
    /// Null on a board that has nothing to do with it - the sound, the print and
    /// the tally are the caller's.</summary>
    public Action<Vector2I, int>? Down;

    /// <summary>What each wall had let go of and broken when the board was last
    /// told about it. Two ints rather than a re-measure every frame: walking the
    /// bricks six times per wall is what <see cref="Restate"/> costs, and a wall
    /// that nothing has hit since the last frame has nothing to say.</summary>
    private readonly Dictionary<Vector2I, (int Loose, int Broken)> _stated =
        new();

    /// <summary>Tell the board about any wall that has lost masonry since the
    /// last frame. Here rather than inside the ram and the round, because both
    /// of them happen to a rig and settle over the frames after - a leaf let go
    /// of falls, and it is the falling that takes it off the edge.</summary>
    public void Restated()
    {
        foreach (WallProp prop in _walls)
        {
            var now = (prop.Rig?.Loose ?? 0, prop.Rig?.Broken ?? 0);
            if (_stated.TryGetValue(prop.Cell, out var was) && was == now)
                continue;
            Restate(prop);
        }
    }

    /// <summary>Whether the wall on a cell stands across a round leaving it -
    /// <see cref="TankTick.Barred"/> answered by whichever prop is on that cell.
    ///
    /// This is what lets a tank in the ring shoot its way out: the walk skips the
    /// cell it fires from, so without it the one wall on this board a tank is
    /// actually inside was the one wall it could not hit.</summary>
    public bool Barring(Vector2I cell, Vector2 dir)
    {
        foreach (WallProp prop in _walls)
            if (prop.Cell == cell)
                return prop.Bars(dir);
        return false;
    }

    /// <summary>Whether the tank has its nose in masonry - the answer to
    /// <see cref="TankTick.Shoving"/>, asked of the bricks
    /// (<see cref="WallProp.Against"/>).
    ///
    /// <b>Under a ram and never under an ordinary order</b> - the same gate the
    /// event has (<see cref="Sweeps"/>): a wall an order did not ask to break
    /// does not slow the hull that clips through it, which is what the old
    /// driven box also answered by only being pushed in under a ram.
    ///
    /// Asked of every wall the tank is beside, and of the driven tank only: the
    /// other two in the garage are parked somewhere else.</summary>
    public bool Pressing(Vehicle v)
    {
        if (Driven is null || v != Driven || Stage is null
            || Field.Atlas is null || !Sweeping(v))
            return false;
        Vector2I here = Field.CellAt(v.GroundPoint - Origin);
        Vector3 box = WallProp.Box(v, Field.Atlas.HexRect.Size.X * 0.5f);
        Vector3 foot = Stage.Contact(v);
        Vector2 way = v.Atlas.GroundDirection(v.Sprite.HullFacing);
        foreach (WallProp prop in _walls)
            if (Beside(here, prop.Cell) && prop.Against(foot, way, box) > 0)
                return true;
        return false;
    }

    /// <summary>
    /// A round that went into the field: raise a burst where it landed - in the
    /// ground, or against the masonry that stopped it.
    ///
    /// <b>One of the two, and it used to be both.</b> The old arrangement raised
    /// the earth cone and then let the walls have the shell as well, on the
    /// argument that masonry taking a round does not stop earth being thrown -
    /// which was true of the physics and became false of the picture the day the
    /// wall had one. What it drew was a shell stopping in the air at a wall's own
    /// boundary and a crater opening in the ground just short of it: the reported
    /// complaint, and correctly reported. A round that stopped on a wall did not
    /// arrive in the earth, and now the earth is not what it draws.
    ///
    /// The physics is untouched and still unconditional - <see cref="Struck"/>
    /// pushes the courses either way, because that half was never the thing that
    /// looked wrong.
    ///
    /// The harness answers the same hook with the ground alone - see
    /// <c>Main.Splashed</c> - because it has no walls for a round to stop on.
    /// </summary>
    public void Landing(Shell round)
    {
        // Asked before anything is drawn, which is the fork on armour's own
        // ordering and for its reason: the one question whose answer decides which
        // picture this is has to be asked first.
        WallProp? hit = _walls.Count > 0 ? Standing(round) : null;
        if (hit is null)
            // At the calibre the gun is loaded with - the harness's reason, and
            // the same dial: TankTick.Calibre is one field both roots read.
            Stage?.Land(round.Ground, round.GroundLift,
                         Ordnance.At(Tick.Calibre));
        else if (round.Overhead && hit.Concrete)
            Roofed(round, hit);
        else
            Breached(round, hit);
        if (_walls.Count > 0)
            Struck(round);
    }

    /// <summary>The wall a round stopped on, or null for one that went into the
    /// ground. <see cref="Struck"/>'s own first two lines, split out because the
    /// answer is now wanted before anything is drawn as well as when the courses
    /// are pushed - and one walk of the list is the only way the picture and the
    /// physics cannot disagree about which wall it was.</summary>
    public WallProp? Standing(Shell round)
    {
        // What stopped it, or - for a round nothing stopped, the mortar's bomb
        // coming down on the cell it was sent to - where it came down. A gun's
        // round at a walled cell is always blocked at that cell's edge, so for
        // it the two are one answer; the bomb is sent over the edge and lands
        // inside, which is the whole of what the capon is broken by.
        Vector2I cell = round.Blocked ?? Field.CellAt(round.Ground - Origin);
        foreach (WallProp prop in _walls)
            if (prop.Cell == cell)
                return prop;
        return null;
    }

    /// <summary>
    /// A round that burst against masonry: the lime dust and the short flash, on
    /// the face it hit.
    ///
    /// <b>The same effect the armour burst is, with its surface named</b> - see
    /// <see cref="ProcSlam.Face"/>, where the four differences are argued. What is
    /// this method's own is the geometry, and all three terms of it come off the
    /// wall rather than off the shell.
    ///
    /// <b>Which side of the cell the round crossed, and it is not
    /// <see cref="WallProp.Bearing"/>.</b> That was the first answer and it was
    /// wrong in a way one board could not show: <c>Bearing</c> is the
    /// <em>middle</em> of a run of sides (see <see cref="Masonry.Run"/>), and a
    /// ring - every side standing - reports 270 by convention because any bearing
    /// closes the same ring. So the burst was seated on the 270 boundary whichever
    /// leaf the round hit. On the ring bench 270 is the near wall and it looked
    /// right; on a map whose wall stands anywhere else it put the burst on bare
    /// ground across the cell, which is what was reported.
    ///
    /// The side is the one the round <em>walked through</em>, so it comes off the
    /// walk: <see cref="Shell.Ground"/> is one sample short of the blocking cell,
    /// so the cell it lies in is the cell before - and the heading from the wall's
    /// cell to that one is the side. When the two are the same cell the shooter is
    /// standing inside the wall, which this board does on purpose, and then the
    /// round is <em>leaving</em>: the side is its own flight snapped to one of the
    /// six, exactly as <c>TankTick.Barred</c> asks it.
    ///
    /// <b>The direction is that side's own plane, not the reverse of the
    /// flight</b> - exact for a plane, where the reverse flight is exact only
    /// head-on. The sign is which face of it was hit: a flight running with the
    /// side's outward direction went out through it and struck the inner face, and
    /// one running against it came from outside.
    ///
    /// <b>The seat is on the wall, and the round's own landing point is not
    /// it.</b> That was the first thing tried, because it is the pair
    /// <c>Stage3D.Land</c> already takes and its lift is known - and it put the
    /// burst inside the ring, over the tank. A wall stands on the <em>boundary</em>
    /// of its cell, so a tank in a ring is firing at its own leaf: the cell that
    /// blocked the shot is the cell the shooter is standing on, and the landing
    /// point is a few pixels past the muzzle. So the point comes off the wall
    /// instead - the middle of the boundary it stands on, which is halfway between
    /// its own cell's centre and the centre of the cell across that side.
    ///
    /// <b>Off the flat layout rather than off <c>CellCentre</c>, because the cell
    /// across may not be on the board at all.</b> A wall on a rim cell has a
    /// neighbour that does not exist, and <c>CellCentre</c> asks the terrain how
    /// high it is; <c>FlatAnchor</c> is pure layout arithmetic and answers for any
    /// pair of coordinates. The height subtracted is then the wall's own cell's,
    /// which is always a cell there is an answer for. On this board the ring sits
    /// in the middle and the difference never shows, which is exactly why it is
    /// written down.
    ///
    /// <b>What is not modelled is where <em>along</em> the wall it burst</b> - the
    /// crossing point of the flight and the boundary would give it, and the guard
    /// arithmetic that keeps such a solve on the masonry costs more lines than a
    /// hex side is wide: the side is a cell across, the burst is a third of a cell,
    /// so the middle of it is on the wall in every case this board can produce.
    ///
    /// <b>And the height is the wall's, halved.</b> A round with no target flies
    /// level at the ground (<see cref="Shell.ToLift"/>), so the shell itself has
    /// no impact height to give and the wall is the only thing that does:
    /// <c>Pile().Top</c> is the highest brick in world units, and a world height
    /// is a screen lift divided by the field's rise, so multiplying it back is the
    /// wall's own top in the pixels this effect measures in. Half of it, because a
    /// round crossing a cell arrives at the middle of the courses rather than at
    /// the coping.
    /// </summary>
    /// <summary>
    /// The mortar's bomb coming down on a concrete box: the heavy earth burst,
    /// seated on the roof.
    ///
    /// <b>Not <see cref="Breached"/>, whose every term is a wall's.</b> That
    /// method asks which side the round crossed and seats a masonry flash on
    /// the middle of that leaf, halfway up - right for a gun's shell stopped at
    /// a boundary, and wrong three ways for a bomb that crossed no side: it
    /// picked a leaf the round never touched, put the flash at mid-wall on a
    /// shell that arrived from above, and drew the short lime puff of a round
    /// on brick for the one charge on this board that levels a box. Measured
    /// on CaponTest as the burst sitting on the left wall and barely showing.
    ///
    /// <b>The seat is the round's own landing point, which for a lob is
    /// honest.</b> A bomb is sent to a cell and comes down on its anchor -
    /// see <c>TankTick</c>'s lob, where the run is the order's own - so
    /// <see cref="Shell.Ground"/> is the middle of the box, and the height is
    /// the ground's plus the box: <c>Pile().Top</c> is the roof while the box
    /// stands, in the same share-of-a-radius units Breached converts.
    ///
    /// <b><see cref="Stage3D.Boom"/> rather than <c>Land</c></b>, because Land
    /// asks what is under the point - water, wood - and under this point is a
    /// roof. The might is the calibre's, doubled: the flash for a shell that
    /// takes the whole box down has to read as the loudest thing on the board,
    /// and the masonry slam's half-again was already argued for a wall that
    /// merely loses a section.
    /// </summary>
    private void Roofed(Shell round, WallProp prop)
    {
        if (Field.Atlas is null || Stage is null)
            return;
        float top = prop.Pile().Top * (Field.Atlas.HexRect.Size.X * 0.5f)
                    * Field.RiseFactor;
        // <b>Raised as a pair, because the stage's lift is not a height over
        // the point - it is the rise already folded into the point.</b>
        // Stage3D.Trunk unfolds it (foot + lift along y, then lift up), so a
        // bigger lift on the same foot is a point further back on the ground
        // and higher by the same amount - which on this camera is the ground
        // again, a hair up-screen. Measured as the column standing on the
        // floor of the box with the roof still on. Taking the height off the
        // foot and adding it to the lift leaves the ground point where it was
        // and raises the seat by the roof.
        Stage.Boom(round.Ground - new Vector2(0.0f, top), round.GroundLift + top,
                   Ordnance.At(Tick.Calibre) * 2.0f, dig: false);
    }

    private void Breached(Shell round, WallProp prop)
    {
        if (Field.Atlas is null || Stage is null)
            return;
        Vector2 flight = round.To - round.From;
        int edge = Crossed(prop, flight, round.Shooter.Cell);
        Vector2 normal = Field.Atlas.GroundDirection(edge);
        // Which face of that side was hit. Zero-length is a guard rather than a
        // case - a round that landed where it started blocked on nothing - and it
        // keeps the outward face.
        if (flight.LengthSquared() > 1.0f && flight.Dot(normal) > 0.0f)
            normal = -normal;
        Vector2I over = HexField.Step(prop.Cell, edge);
        Vector2 mid = (Field.FlatAnchor(prop.Cell) + Field.FlatAnchor(over))
                      * 0.5f
                      - new Vector2(0.0f, Field.TopAt(prop.Cell))
                      + Field.CentreOffset;
        // <b>Pile measures in fractions of a hex radius, not in world
        // pixels, and taking it for pixels made the wall 0.43px tall.</b> Its
        // Reach is what Coverage is compared against, and Coverage is a share of
        // the cell - so Top is that same share, and a share becomes a screen lift
        // through the cell's own half-width and the field's rise. Measured, not
        // reasoned: the trace said Top 0.5, which as pixels is nothing and as
        // half a hex radius is a wall 53px tall.
        float top = prop.Pile().Top * (Field.Atlas.HexRect.Size.X * 0.5f)
                    * Field.RiseFactor;
        Stage.Slam(Stage.Origin + mid,
                    Field.LevelAt(prop.Cell) * Field.Lift, normal,
                    // Screen y grows downward, so up the wall is negative - the
                    // flip ProcSlam.Aim undoes on the way in.
                    new Vector2(0.0f, -top * 0.5f),
                    // <b>Which face of the wall was hit, which is the normal's
                    // own question and not the wall's position.</b> Asked as "is
                    // this leaf further up the screen than the tank" it came out
                    // backwards for the case this board is built around: a tank
                    // inside a ring firing north hits the north leaf's INNER face,
                    // which looks back at the camera, and the burst belongs in
                    // front of the bricks. The leaf is up-screen of the tank all
                    // the same, so that test drew the whole event behind the wall
                    // it went off against - reported, and visible as a pale mass
                    // hanging past the rubble.
                    //
                    // The normal already knows: it was flipped above to point out
                    // of the face the round arrived on, so a normal running down
                    // the screen faces the camera and one running up faces away.
                    // Screen y grows downward, which is the same sentence the rest
                    // of this file is written under.
                    behind: normal.Y < 0.0f,
                    // <b>Half again the calibre, because a wall gives a burst
                    // far more to throw than a plate does.</b> Reported as the
                    // flash being too small for a shell that demolishes masonry,
                    // and the size is the honest half of that answer: the charge
                    // is the charge, but what ends up in the air is the surface,
                    // and a cell-wide wall of brick is not a facet of armour.
                    //
                    // Might rather than a bigger reach inside the shader, and that
                    // is not a preference: reach is what Bounds measures the quad
                    // from, so a shader-side multiplier would grow the picture
                    // past the quad built for it - the one failure the quad check
                    // exists to catch. Might is a scale on the transform, so the
                    // quad comes with it.
                    Ordnance.At(Tick.Calibre) * 1.45f,
                    // <b>And which of the two masonry events it is, off the round
                    // the wall is already being broken by.</b> WallRig has taken
                    // the same distinction since the day it could be shot at -
                    // Strike.He pushes a field over a section, Strike.Ap makes a
                    // hole - so the picture reading it from the same field is the
                    // picture and the physics agreeing by construction. Drawn the
                    // one way for both, an AP round came out as a lime fireball,
                    // which is the one thing it is not.
                    round.Ammo == Shell.Kind.Ap
                        ? ProcSlam.Surface.Pierced : ProcSlam.Surface.Masonry);
    }

    /// <summary>
    /// A round that went into the board rather than into a tank, offered to the
    /// wall it stopped at.
    ///
    /// <b>The direction is the round's own flight, not the bearing</b>, so the
    /// line that was drawn and the line the damage landed on cannot point
    /// different ways - which is the whole claim <see cref="WallRig.Beam"/> is
    /// written under. A level gun has the same lift at both ends, so the
    /// difference of the two drawn rows is the ground direction with nothing
    /// mixed in.
    ///
    /// <b>The force is what the round already carries.</b>
    /// <see cref="Ordnance"/> is an ordered three and the bench's dial is a
    /// multiplier over it; a second table of wall forces beside the calibres
    /// would be a second thing to hold in step.
    ///
    /// <b>And the rig draws no line of its own</b> - see <c>Beam</c>.
    /// </summary>
    public void Struck(Shell round)
    {
        // The same walk Landing already did, through the one method that does it -
        // see Standing. Written out twice, the picture and the physics could
        // answer two different walls on a board where two share a cell.
        if (Standing(round) is WallProp prop)
        {
            Vector2 flight = round.To - round.From;
            if (flight.LengthSquared() < 1.0f)
                return;
            // Where the round began, when it began on this wall's own cell: a
            // tank in a ring fires from the middle of it, which in the prop's
            // frame is nought along any heading. Left at "outside" otherwise, and
            // that is exact rather than approximate - a round from another cell
            // starts further back than any piece of this wall, which is what
            // negative infinity says. See WallRig.Fire's own from.
            // Asked of the tank, not of Shell.From and not of CellAt: the muzzle
            // is a drawn point some forty pixels above the ground, and a drawn
            // row is not a ground row on a board that stands on a plinth - the
            // fourth place this board has charged for that difference, and
            // measured here as the shot going on answering the wrong leaf. The
            // bench has the one tank that fired, and the cell it is on is a fact
            // it already holds.
            bool inside = round.Shooter.Cell == prop.Cell;
            prop.Fire(round.Ammo switch
                      {
                          Shell.Kind.Ap => WallRig.Strike.Ap,
                          Shell.Kind.Cp => WallRig.Strike.Cp,
                          _ => WallRig.Strike.He,
                      },
                      prop.Into(flight.Normalized()),
                      (float)(round.Calibre * Force), Beam,
                      inside ? 0.0f : float.NegativeInfinity);
        }
    }

    /// <summary>
    /// Which of the six sides of a wall's cell a round crossed, as a heading.
    ///
    /// Its own method because the answer has two cases and neither is the wall's
    /// declared bearing - see <see cref="Breached"/>, where the whole of that is
    /// argued.
    ///
    /// <b>The two cases are told apart by where the shooter stands, and the first
    /// attempt asked the landing point instead - which is the second reason a
    /// burst came out behind the wall.</b> <c>Track</c> says so in its own
    /// docstring: the line is drawn from the muzzle, which already stands a little
    /// way along the shot from the cell centre the walk starts at, so what is
    /// drawn <em>reaches a little into the cell that stopped it</em>. So
    /// <c>CellAt(round.Ground)</c> is sometimes the wall's own cell for a round
    /// fired from outside it - and read as "the shooter is inside", which is what
    /// that test meant, it took the branch for a round LEAVING the cell and
    /// answered with the side it would leave by. The far one. The burst then sat
    /// on the boundary beyond the masonry, which is exactly what was reported: the
    /// round breaks the wall and goes off behind it.
    ///
    /// Asked of the tank instead, which is a fact the bench holds rather than a
    /// point it has to round to a cell - and the same test <see cref="Struck"/>
    /// already spends on the same question, so the picture and the solver cannot
    /// disagree about which side of the wall the shot came from.
    ///
    /// <b>And the side itself is the flight snapped to one of the six rather than
    /// a heading between two cells.</b> A round travels down a hex lane, so the
    /// snap is exact; taken between the wall's cell and the shooter's it would
    /// need them to be neighbours, which a shot from two cells down the lane is
    /// not. Reversed for a shooter outside, because the side a round enters by is
    /// the one facing back along its flight.
    ///
    /// <b>But the side the round came in by is not always a side with masonry on
    /// it, and then the snap alone puts the burst on an empty boundary of the
    /// right cell.</b> A wall covers a run of edges rather than all six - the
    /// samples on this board cover three - so a round arriving from the open side
    /// crosses bare ground, enters the cell and meets the leaf further round. And
    /// a shell is stopped by the <em>cell</em> (see <c>_blocking</c> on why that is
    /// still coarse), so it stops whichever side it came in by.
    ///
    /// So the bricks are asked, and asked with the same measurement the board is
    /// told about the wall with: of the six sides, take those
    /// <see cref="WallProp.Bars"/> answers for and keep the one nearest the way
    /// the round was going. Six calls, and <see cref="Restate"/> already makes
    /// them every time a wall loses a piece. When none bars - which the coarse
    /// obstacle rule can produce, a ring stopping a round aimed through its own
    /// breach - the snap stands, and that is the honest answer for a cell that
    /// stopped a shell without having anything on the side it came in by.
    /// </summary>
    private int Crossed(WallProp prop, Vector2 flight, Vector2I from)
    {
        Vector2 look = from == prop.Cell ? flight : -flight;
        int snapped = HexField.EdgeHeadings[
            Angles.SideFor(Gunnery.HeadingOf(look))];
        if (look.LengthSquared() < 1.0f)
            return snapped;
        Vector2 want = look.Normalized();
        Vector2 middle = Field.FlatAnchor(prop.Cell);
        int best = -1;
        float nearest = -2.0f;
        foreach (int heading in HexField.EdgeHeadings)
        {
            Vector2 to = Field.FlatAnchor(HexField.Step(prop.Cell, heading))
                         - middle;
            if (to.LengthSquared() < 1.0f || !prop.Bars(to))
                continue;
            float score = to.Normalized().Dot(want);
            if (score > nearest)
            {
                nearest = score;
                best = heading;
            }
        }
        return best >= 0 ? best : snapped;
    }

    /// <summary>Whether a crossing breaks masonry: only under an order that
    /// asked to ram.
    ///
    /// <b>A ram is an intent, and driving past a wall is not one.</b> The board
    /// stands the tank inside a ring it does not fit in - 2.76m of clear yard
    /// against half-hulls of 2.62 / 3.00 / 3.43m - so a rule that broke
    /// masonry on any contact read as a hull demolishing a yard by driving out
    /// of it, which was exactly the complaint.
    ///
    /// <b>What is given up is the sentence this was refused with once:</b> a
    /// frozen piece is a static body, so a tank driving at standing masonry it
    /// did not ask to ram passes through it. A hull clipping a wall is visible;
    /// a wall falling down for free was visible and also wrong.
    ///
    /// <paramref name="moving"/> is kept in the gate even though a crossing
    /// implies movement, because the gate is also what cuts the shove count
    /// (<see cref="Pressing"/>) and a parked hull presses on nothing.
    ///
    /// <paramref name="always"/> - <c>--drive-rams</c> - puts the old answer
    /// back, so the two can be looked at side by side.</summary>
    public static bool Sweeps(bool moving, bool ramming, bool always) =>
        moving && (ramming || always);

    /// <summary>The same gate asked of a hull, which is where the one class that
    /// does not need an order comes in: for the heavy a walled edge is a step,
    /// the crossing breaks the wall and no action is spent - GDD classes.md,
    /// "Бульдозер HT", and <see cref="MovementProfile.Bulldozes"/>.
    ///
    /// <b>Read as intent rather than as a second gate</b>, because that is what
    /// the rule says: a heavy driving through masonry has asked to break it by
    /// driving. So the yard argument above holds for the other four and stops
    /// holding for this one, and it stops holding on purpose - a heavy leaving
    /// its own ring takes the leaf it crosses with it, which is the rule and not
    /// a wall falling for free.</summary>
    public bool Sweeping(Vehicle tank) =>
        Sweeps(tank.Moving, Ramming || tank.Profile.Bulldozes, DriveRams);

    /// <summary>The driven tank's nose cell as of the last look, so a crossing
    /// is a difference rather than a state - null before the first frame.
    ///
    /// <b>Frozen through a pivot, and that is the pivot rule's whole
    /// residence.</b> The nose sweeps an arc a half-hull wide while the contact
    /// point stands still, so tracked raw it would bank rim crossings that no
    /// ram made; frozen, the first driven frame diffs against where the nose
    /// stood before the pivot and a swept rim still fires once, as a
    /// crossing. The debt this runs up is that the diff is not always one
    /// step, which is what <see cref="HexField.Chain"/> pays out.</summary>
    private Vector2I? _noseCell;

    /// <summary>The last ram event: which wall's cell, and the section the rig
    /// said the hull entered. What the tour latches - see
    /// <c>TankBench.Tour</c>.</summary>
    public (Vector2I Cell, int Face)? Named;

    /// <summary>
    /// The driven ram: fire an event at the wall whose plane the hull's nose
    /// has crossed this frame, if an order asked to ram.
    ///
    /// <b>An event, mirroring the shot, and that is the whole revision.</b> The
    /// shot's path - <see cref="TankTick.Landed"/> into
    /// <see cref="WallProp.Fire"/> - has never needed a degenerate case tamed;
    /// the swept kinematic box needed five (see <see cref="WallRig.Rammed"/>).
    /// A ram is now the same shape: the nose crosses a wall-bearing rim, the
    /// rig answers once, and how fast the tank arrives, where it stops and how
    /// far it turns stay the board's business.
    ///
    /// <b>The nose, not the contact point.</b> A wall's outer plane stands on
    /// the rim, so the front face reaching the rim is the front face reaching
    /// the masonry; measured from the contact point instead, the hull would
    /// clip through half its length of standing wall before anything gave.
    ///
    /// <b>A pivot fires nothing while it turns</b>: <see cref="_noseCell"/>
    /// freezes below walking pace, and the first driven frame diffs against
    /// where the nose stood before the turn. That diff can span more than one
    /// rim - a pivot wider than a step parks the nose two cells from where it
    /// was - so it is walked as a chain of adjacent cells and every rim in it
    /// is answered once. It used to be dropped instead, whenever
    /// <c>HeadingTo</c> said the pair were not neighbours, with the latch
    /// already moved: the wall never heard about the crossing and the tank
    /// drove on through standing masonry.
    /// </summary>
    private void Ram()
    {
        if (Stage is null || _walls.Count == 0 || Field.Atlas is null
            || Driven is not Vehicle tank)
            return;
        Vector2 way = tank.Atlas.GroundDirection(tank.Sprite.HullFacing);
        float half = (float)(tank.Atlas.HullSpan * 0.5 * tank.Sprite.BodyScale);
        Vector2I nose = Field.CellAt(tank.GroundPoint + way * half - Origin);
        // The box rides always, order or none: mounted on the nearest wall
        // the hull stands on, beside, or will drive through, posed to the
        // hull every frame. It used to live only while a ram order did, and
        // the seam showed: the moment the order ended, the hull went back to
        // ghosting through the heap its own ram had made. The body is not
        // what breaks masonry - standing pieces are static and the kinematic
        // box passes through them - so a permanent box shoves what is already
        // loose and touches nothing that stands.
        Rebox(Boxable(tank), tank);
        bool ramming = Sweeping(tank);
        // What the intent still bounds is the naming: the gate exists only
        // under a ram order, and a face named by the last ram is forgotten
        // with it - an armed box on a plain drive would keep releasing and
        // breaching, finishing a wall the ram only entered.
        if (!ramming)
            _boxed?.Disarm();
        _boxed?.Drive(Stage.Contact(tank), way, (float)tank.Speed,
                      ramming ? Gate(tank, way, half) : null);
        // No intent - follow silently, so a later ram diffs against the
        // present rather than against wherever the last one ended.
        if (!ramming)
        {
            _noseCell = nose;
            return;
        }
        // A pivot is not a ram - see _noseCell.
        if (tank.Speed < 1.0)
        {
            _noseCell ??= nose;
            return;
        }
        Vector2I was = _noseCell ?? nose;
        _noseCell = nose;
        if (was == nose)
            return;
        Vector3 box = WallProp.Box(tank, Field.Atlas.HexRect.Size.X * 0.5f);
        Vector3 foot = Stage.Contact(tank);
        // Every rim between where the nose stood and where it is, each
        // answered once. One pair at driving speed - but held through a pivot
        // wider than a step the diff spans two rims, and the first version
        // returned on HeadingTo's -1 with _noseCell already moved: the
        // crossing was consumed unanswered and the tank drove on through
        // standing masonry. A chain owes an answer for every rim it names,
        // and each heading is a flat side by construction.
        List<Vector2I> chain = HexField.Chain(was, nose);
        for (int leg = 1; leg < chain.Count; leg++)
        {
            int heading = HexField.HeadingTo(chain[leg - 1], chain[leg]);
            if (heading < 0)
                continue;
            Vector2 dir = tank.Atlas.GroundDirection(heading);
            foreach (WallProp prop in _walls)
            {
                // A wall stands on the rim of its own cell, so the crossed rim
                // belongs to one of the two cells it parts. Which section - if
                // any - is the rig's answer, off the bricks.
                if (prop.Cell != chain[leg - 1] && prop.Cell != chain[leg])
                    continue;
                int face = prop.Rammed(foot, dir, (float)tank.Speed, box);
                if (face < 0)
                    continue;
                Named = (prop.Cell, face);
                // How fast it was going the moment the masonry gave, and only
                // the first such moment: a speed read afterwards is the speed
                // of a tank already slowed by what it broke.
                if (RamSpeed < 0.0)
                    RamSpeed = tank.Speed * WallRig.MetresPerCell
                                / (Field.Atlas.HexRect.Size.X * 0.5);
            }
        }
    }

    /// <summary>The prop whose rig carries the driven box, or null when no
    /// wall is anywhere near the hull. One box, one wall: the box lives on
    /// the target rig's collision bit, so every other wall is invisible to
    /// it. The box itself is permanent company now - what a ram order still
    /// owns is the naming, not the body (see <see cref="Ram"/>).</summary>
    private WallProp? _boxed;

    /// <summary>The wall the hull is about: the first prop whose cell the
    /// tank stands on, will drive through, or stands beside. Asked every
    /// frame rather than latched, so a charge past two walls hands the box
    /// from one to the next as the first is passed - and a wall behind the
    /// tank drops out by itself, because a driven path only shortens.
    ///
    /// <b>The neighbours are in the walk because heaps spill.</b> A felled
    /// section throws its pieces a cell out, so a hull parked beside the
    /// wall's own cell is parked in that wall's rubble - and a box mounted
    /// only on the cell itself left exactly that hull ghosting through the
    /// heap. Own cell first, then the path: during a charge the wall being
    /// driven at must win over one merely stood beside.</summary>
    private WallProp? Boxable(Vehicle tank)
    {
        foreach (WallProp prop in _walls)
            if (prop.Cell == tank.Cell)
                return prop;
        for (int i = tank.PathStep; i < tank.Path.Count; i++)
            foreach (WallProp prop in _walls)
                if (prop.Cell == tank.Path[i])
                    return prop;
        foreach (WallProp prop in _walls)
            foreach (int heading in HexField.EdgeHeadings)
                if (Field.Neighbour(tank.Cell, heading) == prop.Cell)
                    return prop;
        return null;
    }

    /// <summary>The point on the rim the current order still has to cross with
    /// the boxed wall's masonry on it - the naming gate of
    /// <see cref="WallRig.Drive"/>, null when there is no such rim. Walked off
    /// the remaining path: every consecutive pair touching the wall's cell is
    /// a rim the order crosses, and the farthest one still ahead of the nose
    /// is how far the hull is entitled to name masonry for itself. A world
    /// point rather than a distance, because the distance was first measured
    /// in flat screen coordinates where the isometric squash halves vertical
    /// lengths: a vertical ram's grant came out half its true size, the
    /// naming arrived late, and the leaf fell flat off the swallowed branch
    /// while diagonal rams ploughed correctly. A wall the order parks short
    /// of never enters this walk, which is what keeps a hull braking to a
    /// stop in its own ring from naming the leaf its nose merely clears by
    /// less than a corner.</summary>
    private Vector3? Gate(Vehicle tank, Vector2 way, float half)
    {
        if (_boxed is null || Stage is null || Field.Atlas is null)
            return null;
        Vector2 nose = tank.GroundPoint + way * half - Origin;
        Vector3? gate = null;
        float best = float.NegativeInfinity;
        Vector2I prev = tank.Cell;
        for (int i = tank.PathStep; i < tank.Path.Count; i++)
        {
            Vector2I next = tank.Path[i];
            if (prev != _boxed.Cell && next != _boxed.Cell)
            {
                prev = next;
                continue;
            }
            // Only while the hull actually faces this crossing: measured off
            // the pose alone, a hull swinging through its turn at the start
            // of a return leg pointed its nose into the mitre of the leaf
            // NEXT to the breach for a few frames, named it, and BreachShare
            // took the whole neighbour - a section felled by a pivot, the
            // exact sentence the nose-cell freeze exists to forbid. Thirty
            // degrees of the crossing's own direction is a hull that is
            // driving at the rim, not past it; both vectors carry the same
            // squash, so the angle test is consistent with itself - and both
            // are normalized, because GroundDirection is not: a vertical
            // heading comes back squash long (0.5), so an unnormalized dot
            // could never reach 0.87 and the gate stayed shut on every
            // vertical ram - the leaf fell flat off the late-named branch
            // while diagonals ploughed.
            Vector2 dir = Field.FlatAnchor(next) - Field.FlatAnchor(prev);
            if (dir.LengthSquared() >= 1e-6f && way.LengthSquared() >= 1e-6f
                && dir.Normalized().Dot(way.Normalized()) >= 0.87f)
            {
                Vector2 mid = (Field.FlatAnchor(prev)
                               + Field.FlatAnchor(next)) * 0.5f
                              + Field.CentreOffset;
                float d = (mid - nose).Dot(way);
                if (d > best)
                {
                    best = d;
                    // The same expression WallProp.Build stands its wall
                    // with, so the gate cannot disagree with where the rim
                    // is drawn.
                    gate = Stage3D.World(
                        Stage.Origin + mid,
                        Field.LevelAt(_boxed.Cell) * Field.Lift,
                        Field.Squash, Field.RiseFactor);
                }
            }
            prev = next;
        }
        return gate;
    }

    /// <summary>Move the driven box to <paramref name="prop"/>'s rig - a
    /// no-op when it is already there. Mounting takes the tank's pose as it
    /// stands, so the birth exceptions (see <see cref="WallRig.Mount"/>) cover
    /// exactly what the hull is standing in at this moment.</summary>
    private void Rebox(WallProp? prop, Vehicle tank)
    {
        if (_boxed == prop)
            return;
        _boxed?.Dismount();
        _boxed = prop;
        if (prop is null || Stage is null || Field.Atlas is null)
            return;
        float radius = Field.Atlas.HexRect.Size.X * 0.5f;
        prop.Mount(Stage.Contact(tank),
                   tank.Atlas.GroundDirection(tank.Sprite.HullFacing),
                   WallProp.Box(tank, radius), WallProp.Bow(tank, radius),
                   WallProp.Crown(tank, radius));
    }

    /// <summary>The same drive, at a named wall on a named side - what
    /// a bench's ram button does to the ring, and what a tour does to each
    /// wall on the board in turn.
    ///
    /// <b>One copy, because the two legs are the awkward part.</b> A caller that
    /// wrote its own run-up would be a second answer to "and arrive along this
    /// heading", and the two agree until the day one of them is edited.</summary>
    public void RamAt(WallProp wall, int side)
    {
        if (Driven is not Vehicle tank)
            return;
        side = Mathf.PosMod(side, HexField.EdgeHeadings.Length);
        // Out through the named side when the tank is already in the ring, which
        // is where the board opens. One leg, not two: the run-up exists because
        // the pathing cannot say "and arrive along this heading", and from inside
        // the cell the first step is the heading. What it rams is its own wall,
        // which is what a machine in a walled yard does to get out.
        if (tank.Cell == wall.Cell)
        {
            Order?.Invoke(HexField.Step(wall.Cell,
                                        HexField.EdgeHeadings[side]));
            Ramming = true;
            _lineUp = null;
            return;
        }
        Vector2I from = HexField.Step(wall.Cell, HexField.EdgeHeadings[side]);
        // Line up first when it is not already on the lane: a path that comes at
        // the wall round a corner is a ram along whatever heading the search
        // happened to leave on, which is not the side the dial names.
        Order?.Invoke(tank.Cell == from ? wall.Cell : from);
        // Both legs, because both are this order: the run-up is only there
        // because the pathing cannot say "and arrive along this heading", and a
        // tank that lined up and then stopped ramming would drive through the
        // leaf it came for.
        Ramming = true;
        _lineUp = tank.Cell != from ? (wall, side) : null;
    }

    /// <summary>Which wall and which side the drive under way is only the
    /// run-up for, so that reaching its end orders the ram itself, or null when
    /// the drive is not a run-up. Two legs rather than one, for the reason
    /// above; held rather than pathed in one go because the board's pathing has
    /// no way to say "and arrive along this heading".
    ///
    /// <b>The wall is carried rather than assumed to be the ring</b>: a tour
    /// rams every wall on the board in turn, and a second leg that always came
    /// in at the ring would walk the tank home in the middle of ramming a
    /// sample.</summary>
    private (WallProp Wall, int Side)? _lineUp;

    /// <summary>Whether two cells are the same one or neighbours. Through
    /// <see cref="HexField.Step"/>, which is the only definition of what is next
    /// to what - a second one written in axial arithmetic is wrong on the odd
    /// columns only.</summary>
    private static bool Beside(Vector2I a, Vector2I b)
    {
        if (a == b)
            return true;
        foreach (int heading in HexField.EdgeHeadings)
            if (HexField.Step(a, heading) == b)
                return true;
        return false;
    }
    /// <summary>One frame: the box follows the hull, a crossing is answered, and
    /// what fell is put on to the board. The order is the point and it is the
    /// bench's own - the box the wall feels is where the tank got to this frame,
    /// a leaf is taken off its edge by falling rather than by being let go of,
    /// and the second leg of a ram is ordered once the run-up has arrived.
    /// </summary>
    public void Watch()
    {
        Ram();
        Restated();
        if (Driven is not Vehicle tank || tank.Moving)
            return;
        if (_lineUp is (WallProp wall, int side)
            && tank.Cell == HexField.Step(wall.Cell,
                                          HexField.EdgeHeadings[side]))
        {
            _lineUp = null;
            Order?.Invoke(wall.Cell);
            Ramming = true;
        }
    }
}
