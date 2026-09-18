using Godot;

namespace TankSpriteTest;

public sealed partial class BoardMap
{
    /// <summary>
    /// The event bench's board: every kind of cell the rules know, within one
    /// screen, each beside plain ground, and five tanks a shot apart.
    ///
    /// <b>One board for three scenes</b>, because an event on the field lights
    /// a tank and an event on a tank lights the field, and two boards would show
    /// neither seam. What the scenes differ in is where the camera opens and
    /// which group of the panel is unfolded - see <see cref="EventBench"/>.
    ///
    /// <b>Laid out so every guard passes on its own terms.</b> The rise at the
    /// top left is climbed by a ramp on <c>(1,2)</c>, whose only higher neighbour
    /// is <c>(1,1)</c>. The hollow at the bottom is a flower of six cells so its
    /// ramp on <c>(7,5)</c> has one higher neighbour, <c>(7,4)</c>, and five low
    /// ones. The deep pond has a ramp on <c>(11,5)</c> climbing north on to
    /// <c>(11,4)</c>: it is the pond's own water (<c>d</c> under the <c>r</c>, so
    /// the body stays one surface), flooded over the back of its run like a
    /// ford's beach, and its one higher neighbour is the bank - the two cells
    /// east of it are off the board, which the ramp guard does not count.
    /// A tank drops into the pond off any bank cell ((10,4) on to (10,5) is
    /// the bench's own jump) and gets out by this ramp alone - see
    /// docs/swim-plan.md. The ford at <c>(3,6)</c>–<c>(3,7)</c> has no beach -
    /// the bench puts tanks into it by event, not by driving - so this board, like
    /// <see cref="Effects"/>, is <b>deliberately not in <see cref="Names"/></b>
    /// and is not judged by the driving rules. What it is still held to is the
    /// structural half: unknown letters, ramps off the board, water that cannot
    /// bank.
    ///
    /// <b>A shelf over a pit in the top right corner, because two levels is the
    /// steepest shot the rules allow and nothing else on this board could ask
    /// for it.</b> The shelf <c>(8,0)</c>-<c>(9,0)</c> stands a level over plain
    /// ground and the pit <c>(10,1)</c>-<c>(11,1)</c> a level under it, so a gun
    /// on <c>(9,0)</c> firing down 330 drops two levels over two cells:
    /// <c>atan(step_grade)</c> exactly, which is the ladder's top rung,
    /// <see cref="Gunnery.Reaches"/>'s own limit (two levels want two cells) and
    /// the steepest the tube was ever rendered at. A cell nearer is a position
    /// the rules refuse; a cell further is the shallower shot the ravine already
    /// shows. <see cref="Playback.Edge"/> finds it the way it finds the ravine -
    /// by shape and not by name - and 330 is the heading it looks along first,
    /// which is why the drop is cut on that diagonal rather than down a column:
    /// straight at the camera the tube foreshortens to nothing and this is the
    /// one board feature whose whole subject is the tube's angle.
    ///
    /// <b>Both cells of the pit are in it and not beside it.</b> The fire rule
    /// takes the floor of a shot to be the lower of its two ends and lets
    /// nothing strictly between stand above that floor, so a shelf with one low
    /// cell under it and plain ground past it would be a shot blocked by the
    /// ground it was fired over - see <see cref="Gunnery.Solve"/>. The shelf is
    /// two cells for the other half of the same rule: <c>(8,0)</c> is the hex
    /// back from the brink, where the shooter's own plateau blocks it.
    ///
    /// <b>Neither end carries a ramp, and neither needs one.</b> The pit is
    /// walled by the board's edge on two sides and by plain ground on the rest,
    /// which leaves no cell with the single higher neighbour a ramp wants; the
    /// tanks are <em>put</em> on these four cells by the event and never driven
    /// to them, which is <see cref="Playback.Tier"/>'s own contract. It is a
    /// shooting gallery, not a road.
    ///
    /// <b>A mine and a ring of brick two cells from a parking on purpose.</b>
    /// These are the props the events act on, and a tank that has to drive
    /// three cells to reach them is three cells of animation in front of every
    /// screenshot.
    ///
    /// The sand at <c>(1,5)</c>–<c>(2,5)</c> stands in for mud until mud has a
    /// letter; there is no road yet at all - see docs/effects-plan.md.
    /// </summary>
    private static readonly string[] EventsGround =
    {
        // 0123456789AB
        ".....ff.11..", // r0  a wood at the top middle; the shelf at (8,0),(9,0)
        ".11..f...Wvv", // r1  a rise top left; brick at (9,1); the pit at (10,1),(11,1)
        "............", // r2  ramp on (1,2) climbs north on to (1,1)
        "....m.......", // r3  a mine between the two parkings on this row
        "............", // r4  (7,4) is the ravine's rim: the ramp's high edge
        ".ss...vvv.dd", // r5  sand; the ravine's top row, ramp on (7,5); deep pond
        "......vvv.dd", // r6
        "...ww.......", // r7  a ford
    };

    private static readonly string[] EventsRamps =
    {
        "............",
        "............",
        ".r..........",
        "............",
        "............",
        ".......r...r", // r5  the ravine's ramp; the pond's ramp on (11,5), north on to (11,4)
        "............",
        "............",
    };

    /// <summary>Five parkings, one per class, because two of the events are a
    /// class's own: the tank destroyer's round goes through its first target,
    /// the mortar's comes down from above, and a board without them has two
    /// buttons with nothing to press them on. The medium is the one driven by
    /// default, the light what it shoots at, the heavy the neighbour of the
    /// brick. Named rather than left to load order, because which tank stands
    /// where is what a screenshot is of. The tank destroyer sits a row below
    /// the sand rather than on it, because <c>(4,4)</c> is where a medium
    /// stands off to shoot the light from the south-west - see
    /// <see cref="Playback.StandOff"/> - and a parking next to that cell put
    /// two hulls in one picture.
    /// </summary>
    private static readonly Parking[] EventsHomes =
    {
        new(new Vector2I(2, 3), "MTP"),
        new(new Vector2I(6, 3), "LTP"),
        new(new Vector2I(9, 2), "HTP"),
        new(new Vector2I(4, 6), "TDP"),
        new(new Vector2I(10, 3), "HMP"),
    };

    private static BoardMap? _events;

    /// <summary>The mix, for <see cref="Effects"/>'s reason: a named plate would
    /// sit over the wood's kind and plant nothing.</summary>
    public static BoardMap Events => _events ??= FromGround(
        "events", EventsGround, EventsRamps, EventsHomes, TerrainSet.Mixed, true,
        plinth: 1);
}
