using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// A brick wall standing on one named cell of one named board: the plan, the
/// drawing and the solver, plus the single conversion that carries a direction
/// from the board into the prop's own frame.
///
/// <b>Lifted out of <see cref="WallBench"/> whole rather than written a second
/// time, and that is the only reason it exists</b> - <see cref="TankTick"/>'s
/// argument, arriving at the other end of the board. A second bench that wanted
/// a wall on a hex had two ways to get one: copy <c>Build</c>, <c>Rig</c> and
/// <c>Fell</c>, or share them. The copy is what <see cref="WoodBench"/> refuses
/// in the same words - a bench that reimplements the thing it is judging is a
/// bench measuring itself.
///
/// <b><see cref="Lay"/> is what really had to be shared.</b> Four places have to
/// agree about which way the prop is turned - the shot's direction coming in,
/// the ground triangles going the same way, the sun's run inside
/// <see cref="WallStack"/>, and the heap measured against the cell - and
/// <see cref="WallBench"/> already carries the history of getting the sign
/// wrong: written as <c>+spin</c> it is a shot arriving from the wrong side once
/// the wall is turned, which nothing on a bench that opens at zero would ever
/// have shown. A second copy of that turn in a second bench is the same failure
/// waiting for the first turntable.
///
/// <b>It owns no flags, no keys and no panel.</b> Which recipe, which side, how
/// hard and when are the caller's; what a wall on a cell <i>is</i> is here.
/// </summary>
public sealed partial class WallProp : Node
{
    /// <summary>The board it stands on. Every distance here is a fraction of a
    /// cell of it.</summary>
    public required HexField Field { get; init; }

    /// <summary>The stage that draws that board. Read for three things and all
    /// three are the board's own: where the cell's top face is
    /// (<see cref="Stage3D.Origin"/>), what the ground is
    /// (<see cref="Stage3D.GroundTriangles"/>), and how a flat row becomes a
    /// world point (<see cref="Stage3D.World"/>).</summary>
    public required Stage3D Stage { get; init; }

    /// <summary>Which cell carries it.</summary>
    public required Vector2I Cell { get; init; }

    /// <summary>What the wall is made of. Handed in rather than owned, because
    /// the dials that change it are the caller's - see
    /// <c>WallBench.Panel</c>.</summary>
    public WallKit.Recipe Recipe { get; init; } = new();

    /// <summary>How much of the side it takes, and how far out it stands - one
    /// number for both, see <see cref="WallKit.Fit"/>.</summary>
    public float Coverage { get; set; } = 0.97f;

    /// <summary>Solved parabolas instead of a live solver. The bench's
    /// <c>--solved</c>; a board with a tank on it never asks for it, because the
    /// whole of what a tank does to a wall is contact.</summary>
    public bool Solved { get; set; }

    /// <summary>The atlas the ramming tank is borrowed from, or null when the
    /// board has a real one. See <see cref="WallStack.Tank"/>: the borrowed
    /// sprite exists because the wall bench has no <see cref="Vehicle"/>, and on
    /// a board that has one it would be a second tank.</summary>
    public AtlasSet? Borrow { get; set; }

    /// <summary>Where it sorts. <see cref="WallStack.Order"/>.</summary>
    public int Order { get; init; }

    private WallStack? _wall;
    private WallRig? _rig;
    private WallKit.Plan? _plan;
    private float _scale = 1.0f;

    public WallKit.Plan? Plan => _plan;
    public float Scale => _scale;
    public WallStack? Stack => _wall;
    public WallRig? Rig => _rig;

    /// <summary>Which flat side of the cell it stands on, in board degrees, and
    /// which side a shot therefore arrives from. One dial for both - see
    /// <see cref="WallBench"/>, where the reason is written out: a wall on a
    /// boundary is the boundary being crossed.</summary>
    public float Bearing { get; set; } = 270.0f;

    /// <summary>The turntable, in radians. Only ever a way of looking at it;
    /// nothing about the wall reads it except through <see cref="Lay"/>.
    /// </summary>
    public float Spin { get; set; }

    /// <summary>Which way the prop is turned, all in: the side it stands on plus
    /// the turntable on top of that. <b>Everything that crosses between the
    /// board and the prop goes through this and never through the turntable
    /// alone</b> - the layout is built standing on its own +z side, so putting it
    /// on the board's side <i>b</i> is a turn of <c>b + 90</c>.</summary>
    public float Lay => Spin + Mathf.DegToRad(Bearing + 90.0f);

    private float RadiusPx => Field.Atlas is null
        ? 1.0f : Field.Atlas.HexRect.Size.X * 0.5f;

    /// <summary>Lay a wall, fit it to the cell, scatter its rubble and stand it
    /// up.
    ///
    /// The order is the point: fitting is a multiply over the finished layout,
    /// because nothing in <see cref="WallKit"/> is an absolute length, and the
    /// scatter is third because the rubble is allowed the cell that is left over
    /// once the wall has taken its side of it.</summary>
    public void Build()
    {
        if (Field.Atlas is null)
            return;
        _plan = WallKit.Lay(Recipe);
        _scale = WallKit.Fit(_plan, Coverage);
        WallKit.Scatter(_plan, Recipe, _scale, Coverage);

        _wall?.QueueFree();
        float radius = RadiusPx;
        // The stage's own origin, and never the field's anchor alone: the wall
        // bench parks its grid at zero and the tank bench does not, so a wall
        // built off the bare anchor stands on the right hexagon of one board and
        // beside the right hexagon of the other. The same expression
        // Stage3D.CellTop is written with, which is what the ground triangles
        // come out of.
        Vector2 flat = Stage.Origin + Field.FlatAnchor(Cell) + Field.CentreOffset;
        float lift = Field.LevelAt(Cell) * Field.Lift;
        _wall = new WallStack
        {
            Radius = radius,
            Squash = Field.Squash,
            Rise = Field.RiseFactor,
            Anchor = Stage3D.World(flat, lift, Field.Squash, Field.RiseFactor),
            Order = Order,
        };
        AddChild(_wall);
        _wall.Tank = Borrow;
        _wall.TankFacing = HexField.Reverse(Mathf.RoundToInt(Bearing));
        _wall.Raise(_plan);
        Raise();
        // After Raise, because the setter needs the multimesh to pose the shadow
        // into.
        _wall.Spin = Lay;
    }

    /// <summary>Hand the laid wall to the solver, standing and asleep.
    ///
    /// <b>The ground comes over in the prop's own units, spin and all.</b> The
    /// bodies live in world space with no parent transform over them, so the
    /// board has to be brought into their frame: turned by the lay, shifted to
    /// the anchor, divided by the radius.</summary>
    public void Raise()
    {
        _rig?.QueueFree();
        _rig = null;
        if (Solved || _wall is null || _plan is null || Field.Atlas is null)
            return;
        float radius = RadiusPx;
        Vector3 anchor = _wall.Anchor;
        var back = new Basis(Vector3.Up, -Lay);
        var tris = new List<Vector3>();
        foreach (Vector3 v in Stage.GroundTriangles())
            tris.Add(back * ((v - anchor) / radius));
        _rig = new WallRig();
        AddChild(_rig);
        _rig.Raise(_plan, tris);
        _wall.Rig = _rig;
    }

    /// <summary>Turn the prop, sun and all. A method rather than a bare property
    /// because the shadow has to be redone even when nothing has moved - see
    /// <see cref="WallStack.Spin"/>.</summary>
    public void Turn(float spin)
    {
        Spin = spin;
        if (_wall is not null)
            _wall.Spin = Lay;
    }

    /// <summary>Put it on another side of the cell. Turns the prop <i>and</i>
    /// hands the ground back to the solver, because the triangles it was given
    /// came through the old turn.</summary>
    public void Stand(float bearing)
    {
        Bearing = bearing;
        if (_wall is null)
            return;
        _wall.TankFacing = HexField.Reverse(Mathf.RoundToInt(Bearing));
        _wall.Spin = Lay;
        Raise();
    }

    /// <summary>
    /// A direction on the board, in the prop's own frame.
    ///
    /// <b>Two conversions, and this is the one place either of them is
    /// written.</b> <see cref="Stage3D.World"/> divides out the squash that
    /// <see cref="AtlasSet.GroundDirection"/> put in, so composing them is the
    /// world direction with nothing invented in between; then back through the
    /// lay, because the bodies live in the prop's frame - the same
    /// <c>Basis(Up, -Lay)</c> the ground triangles go through in
    /// <see cref="Raise"/> and the sun's run goes through in
    /// <c>WallStack.LocalRun</c>.
    /// </summary>
    public Vector3 Into(Vector2 flat)
    {
        if (flat.LengthSquared() < 1e-12f)
            return Vector3.Zero;
        return new Basis(Vector3.Up, -Lay) * Stage.World(flat, 0.0f).Normalized();
    }

    /// <summary>Which way a shot from <paramref name="bearing"/> travels, in the
    /// prop's frame. Reversed, because a bearing says where it comes from -
    /// <c>HexField.Lane</c>'s own sentence read backwards.</summary>
    public Vector3 Arriving(float bearing) =>
        Field.Atlas is null ? Vector3.Zero
        : Into(Field.Atlas.GroundDirection(
                   HexField.Reverse(Mathf.RoundToInt(bearing))));

    /// <summary>Fire at it. <paramref name="into"/> is in the prop's frame -
    /// <see cref="Into"/> or <see cref="Arriving"/>.</summary>
    public void Fire(WallRig.Strike shot, Vector3 into, float force,
                     bool beam = true, float from = float.NegativeInfinity) =>
        _rig?.Fire(shot, into, force, beam, from);

    /// <summary>The solved-flights path, for <c>--solved</c>.</summary>
    public void Fell(WallFall.Shot shot) =>
        _wall?.Fell(Recipe.Seed, shot, Coverage);

    /// <summary>
    /// A box somebody else drives, handed over in the board's own terms.
    ///
    /// <b>The board-to-prop conversion lives here and not in the caller</b>,
    /// which is the whole reason this class was pulled out: a tank's contact
    /// point is a flat row plus a lift, and what the solver wants is metres in
    /// the prop's frame. Two statements of that turn are two chances to be a
    /// quarter turn out, and a wall struck through a corner of its own hex is a
    /// picture that looks entirely reasonable.
    /// </summary>
    /// <param name="foot">Where the box touches the ground, in world units -
    /// <see cref="Stage3D.Contact"/>, which is the board's one answer to that and
    /// so cannot disagree with where the tank is drawn.</param>
    /// <param name="flatDir">Which way it is driving, as a board direction.</param>
    /// <param name="size">How big the box is, in metres.</param>
    public void Nose(Vector3 foot, Vector2 flatDir, Vector3 size)
    {
        if (_rig is null || _wall is null || Field.Atlas is null)
            return;
        Vector3 into = Into(flatDir);
        if (into.LengthSquared() < 1e-6f)
            return;
        Vector3 local = new Basis(Vector3.Up, -Lay)
                        * ((foot - _wall.Anchor) / RadiusPx)
                        * WallRig.MetresPerCell;
        // The box's middle, off the contact point: a tank stands on the ground,
        // and everything on this board that is put somewhere is put by where it
        // touches it.
        _rig.Nose(local + Vector3.Up * (size.Y * 0.5f), into, size);
    }

    /// <summary>Stop driving the box and take it off the picture. What the
    /// caller says when the tank has gone somewhere else.</summary>
    public void Halt() => _rig?.Halt();

    /// <summary>
    /// How big a tank is, in metres, for the solver to feel.
    ///
    /// <b>Measured off the atlas rather than written down</b> - the rig's own box
    /// is three by six by two because that bench has no tank to ask. Here there
    /// is one: <see cref="AtlasSet.HullSpan"/> is the hull broadside, which is
    /// its length, and twice <see cref="AtlasSet.TrackArm"/> is the gauge, which
    /// is its width. Both come off the belt and hull layers of the atlas being
    /// drawn, so they are the size of the tank on screen and not a second opinion
    /// about it.
    ///
    /// <b>The class scale comes in with them</b>, which is the whole reason the
    /// bench has three: a heavy rams with a bigger box than a light, and the
    /// difference is the same 0.85/1.00/1.15 the picture shows.
    ///
    /// <b>The height is <see cref="WallRig.TankTall"/> and is a named hole.</b> A
    /// sprite has no height in metres - the height map
    /// (<c>sprite_height.py</c>) carries screen rise, which mixes height with
    /// depth - so this is the one figure here that is declared rather than
    /// measured. It decides how many courses a ram reaches, and nothing on
    /// screen would say if it were wrong.
    /// </summary>
    public static Vector3 Box(Vehicle tank, float radiusPx)
    {
        float metres = WallRig.MetresPerCell / Mathf.Max(radiusPx, 1e-4f);
        float scale = tank.Sprite.BodyScale;
        float length = tank.Atlas.HullSpan * scale * metres;
        float gauge = (float)(tank.Atlas.TrackArm * 2.0) * scale * metres;
        return new Vector3(
            gauge > 0.01f ? gauge : length * 0.5f, WallRig.TankTall, length);
    }

    /// <summary>How close the standing masonry comes to the middle of its own
    /// cell, in metres.
    ///
    /// What a ring leaves for whatever parks inside it, and so the number that
    /// says whether the machine fits the yard it opens in - against the half
    /// diagonal of <see cref="Box"/>, because a hull turns. Printed rather than
    /// left to a comment: it decides whether a tank on this cell spends its life
    /// clipping through brick, and nothing on screen distinguishes that from a
    /// wall drawn a little too close.
    ///
    /// Rubble is not masonry here. The apron lies on the ground inside the cell
    /// and would answer every time; what is being asked is what is standing up.
    /// </summary>
    public float Clearance()
    {
        if (_plan is null)
            return float.PositiveInfinity;
        float near = float.PositiveInfinity;
        foreach (WallKit.Block block in _plan.Blocks)
        {
            if (block.Chip || block.Course < 0)
                continue;
            var to = new Vector3(block.Seat.X, 0.0f, block.Seat.Z);
            float far = to.Length();
            if (far < 1e-4f)
                return 0.0f;
            Vector3 way = to / far;
            // The block's own reach along that ray - exact for a box, which is
            // what these are.
            float over = Mathf.Abs(block.Turn.X.Dot(way)) * block.Half.X
                         + Mathf.Abs(block.Turn.Y.Dot(way)) * block.Half.Y
                         + Mathf.Abs(block.Turn.Z.Dot(way)) * block.Half.Z;
            near = Mathf.Min(near, far - over);
        }
        return near * WallRig.MetresPerCell;
    }

    /// <summary>Whether standing masonry lies across a shot leaving the middle of
    /// this cell along <paramref name="flat"/>.
    ///
    /// <b>The question a cell-sized obstacle cannot answer.</b> What stops a round
    /// is a set of cells - <c>TankTick.Obstacles</c> - and the walk skips the cell
    /// it fires from, which is right for everything else on the board and wrong
    /// for exactly one thing: a wall stands on the <i>rim</i> of its cell, so a
    /// tank inside a ring of it was firing through its own masonry. This is the
    /// finer resolution, and it is one predicate rather than a finer grid.
    ///
    /// <b>Measured off the pieces, never off <see cref="Bearing"/> and
    /// <c>Sides</c>.</b> Those two do say which edges a wall was laid on, but
    /// reading them here means writing the fan's sign convention down a second
    /// time, and a wall with one leaf would then bar shots through the five edges
    /// it does not cover - masonry invented by arithmetic. Walking the blocks
    /// answers the same question with nothing assumed, the way
    /// <see cref="Clearance"/> already does.
    ///
    /// <b>And it asks the rig, so a leaf that has fallen stops barring.</b> A ring
    /// with one side driven through is a ring a tank can shoot out of, which is
    /// the whole of what makes this a rule rather than a wall of glass.
    ///
    /// Rubble is not masonry here, for <see cref="Clearance"/>'s reason: the apron
    /// lies on the ground inside the cell and would answer every heading.</summary>
    public bool Bars(Vector2 flat)
    {
        if (_plan is null || flat.LengthSquared() < 1e-12f)
            return false;
        Vector3 into = Into(flat.Normalized());
        into.Y = 0.0f;
        if (into.LengthSquared() < 1e-12f)
            return false;
        into = into.Normalized();
        // Half a brick across. The pieces of a leaf sit about a brick apart along
        // their edge, so a ray leaving the middle passes within half of one of
        // some piece's centre - and a corridor wider than that starts answering
        // for the leaf on the next edge round.
        float lane = WallKit.BrickLong * 0.5f;
        for (int i = 0; i < _plan.Blocks.Count; i++)
        {
            WallKit.Block block = _plan.Blocks[i];
            if (block.Chip || block.Course < 0)
                continue;
            if (_rig is { } rig && !rig.Held(i))
                continue;
            var to = new Vector3(block.Seat.X, 0.0f, block.Seat.Z);
            float along = to.Dot(into);
            // Behind the shot: the far leaf of a ring is not in the way of a round
            // going the other direction.
            if (along <= 0.0f)
                continue;
            if ((to - into * along).Length() <= lane)
                return true;
        }
        return false;
    }

    /// <summary>Where the pieces have got to, in the cell's own units - through
    /// the lay, because the cell does not turn with the wall.</summary>
    public (float Reach, float Top, int Off, int Awake) Pile()
    {
        if (_rig is not null)
            return _rig.Pile(Lay);
        if (_wall is null)
            return (0.0f, 0.0f, 0, 0);
        (float reach, float top, int aloft, _) = _wall.Pile();
        return (reach, top, aloft, 0);
    }
}
