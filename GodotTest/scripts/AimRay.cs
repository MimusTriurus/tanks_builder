using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The aiming ray: a dashed line out of the gun to the exact spot on the other
/// tank its next round would hole.
///
/// <b>It answers a question about a shot that has not happened yet, which is what
/// separates it from everything else drawn here.</b> The tracer, the trail and
/// the burst all report a round already on its way; none of them can be looked at
/// before the trigger, and by the time they can there is a flash on one end and a
/// burst on the other. What the gunnery model actually decides at the trigger -
/// which plate, and where on that plate - is invisible until it is too late to
/// check it.
///
/// <b>The point is taken from the firing code, not worked out again here.</b>
/// That is the whole of the design. <see cref="Main.Aim"/> is the one place that
/// turns a shooter, a target and a bearing into a hole in the armour, and both
/// the launch and this line go through it - so a ray that pointed somewhere the
/// round does not go would be a bug in the gun rather than a lie in the overlay.
/// Two copies of that arithmetic is exactly how a mark and the thing it marks
/// drift apart, and it would drift in the quietest direction there is: the ray
/// would look plausible on every frame and be wrong about the one shot somebody
/// was watching.
///
/// <b>It says two things, and only one of them can be wrong.</b> The line is
/// where the gun is pointing, which is true whenever there is a gun; the ring
/// on the end is where the round would land, which is only true when there is a
/// round and somewhere for it to go. So the line is drawn for the tank being
/// driven whatever its turret is doing, and the ring appears only where a hole
/// was actually solved. Marking the end regardless would be the one lie
/// available here: a hole claimed for a shot that cannot be fired.
///
/// <b>Hence three tips rather than two, because a line can end for three
/// different reasons</b> - see <see cref="Tip"/>. A hole gets the ring at full
/// ink; a line stopped by something in the way gets a bar across it at
/// <see cref="Idle"/>, since it is reporting an obstacle and claiming nothing;
/// a line that simply ran out of board ends. Folding the last two together
/// would make "there is a hill here" and "there is nothing more that way" the
/// same picture, and they are opposite pieces of news.
///
/// <b>Which also answers "why is nothing showing".</b> It used to need an
/// attack order with a clear lane, so selecting a tank and switching the overlay
/// on drew nothing at all until a duel had been set up - and the thing worth
/// looking at is the gun, which is being driven by hand long before anybody is
/// ordered to fire at anybody. A tank with an order still gets its ray whether
/// it is the selected one or not, because a tank left engaging goes on firing
/// after you have selected somebody else.
///
/// <b>Dashed, and that is not decoration.</b> A solid bright line out of a muzzle
/// is what a tracer is, and the bench already has one; the eye would read a
/// second one as a round in flight. Dashes read as a measurement laid over the
/// picture rather than as something happening in it.
///
/// <b>Above every tank, because it ends on one.</b> The far end of the ray sits
/// on the target's armour, which is where the round goes - a line that stopped at
/// the hull it points into would mark the air in front of the tank instead of the
/// plate. Same reason <see cref="Shell.Layer"/> is where it is, and the opposite
/// of the ground marks, which go under the tanks precisely so they read as
/// ground. On the staged board that means it is drawn over the hills as well,
/// which is the same statement: a measurement laid over the picture rather than
/// something standing in it.
///
/// <b>One drawing on both boards, and the ends are converted rather than the
/// drawing duplicated.</b> Both are pixels of a tank's own picture, and what a
/// pixel means depends on who draws the board: flat, the sprite hangs in the
/// harness's canvas; staged, it lives in a SubViewport of its own and is shown on
/// a quad, so the same <c>ToGlobal</c> answers in that viewport's 512px space.
/// The harness asks the stage where its own pixel went -
/// <see cref="Stage3D.Screen"/>, which is the billboard's mesh read backwards -
/// and hands the answer in. Two drawings, one per board, would have been two
/// chances for the ray to stop agreeing with itself about a line that is supposed
/// to be exact.
///
/// <b>Off by default, and that is the tracer's own argument rather than
/// caution.</b> A debug line that crept into an A/B would be measuring itself,
/// and almost every measurement here is a pixel diff of two frames. The tracer
/// was off for exactly this reason while it was one stroke of debug drawing, and
/// came on once it became a layer to be judged; this one stays what the tracer
/// stopped being. Which is also why it has a flag: a capture is evidence, and
/// putting the overlay into one deliberately must not need a hand on the mouse.
///
/// <b>Magenta, over its own dark backing.</b> The ground already carries the
/// route's amber and yellow-green, the selection ring's cyan and the target
/// ring's red; magenta is what is left, and being left over is what makes it read
/// as an overlay rather than as part of the board. The backing is the lesson the
/// ring and the tracer both learned the hard way: a colour tuned on the pale tile
/// disappears on the amber one, and this line crosses the pale tile, the amber
/// route and dark green armour within its own length - so it carries its own
/// contrast instead of borrowing the ground's.
/// </summary>
public sealed partial class AimRay : Node2D
{
    /// <summary>Whether the ray draws. Named rather than left in an initialiser,
    /// the rule <see cref="Recoil.ShearOnByDefault"/> set: a default nobody can
    /// point at is a default that quietly changes - and this one is load-bearing,
    /// because on by default would put a debug line into every pixel diff this
    /// bench takes.</summary>
    public const bool OnByDefault = false;

    /// <summary>
    /// Static for the reason <see cref="Shell.SmokeOn"/> is: the flags are parsed
    /// before any node exists, so a switch that lived on the instance would have
    /// to be pushed once the scene was built - the trap named beside
    /// <c>_flashSource</c> in <see cref="Main"/>, where flags that wrote straight
    /// through a node that was not there yet hung the capture instead of failing
    /// it.
    /// </summary>
    public static bool On { get; set; } = OnByDefault;

    /// <summary>
    /// Why a ray ends where it does, which is the whole of what its far end is
    /// allowed to say.
    ///
    /// <c>Hole</c> is the only one that claims a shot, and it is only ever
    /// handed over for a point that came out of the firing code. <c>Stop</c> is
    /// the sighting line running into a tank or into ground standing above it -
    /// news about the board, not about a round. <c>Open</c> is the line reaching
    /// the last hex there is in that direction.
    /// </summary>
    public enum Tip
    {
        Open,
        Stop,
        Hole,
    }

    /// <summary>One gun's aim: out of the muzzle, onto the plate. Both ends in
    /// global space, because both come off sprite transforms that already carry
    /// the class scale and whatever the hull is leaning on its springs.
    ///
    /// <c>End</c> is why it stops there - see <see cref="Tip"/>. It is asked for
    /// rather than defaulted because a ray that claimed a hole it was never
    /// given is the only way any of this goes wrong.</summary>
    public readonly record struct Ray(Vector2 From, Vector2 To, Tip End);

    private readonly List<Ray> _rays = new();

    /// <summary>How many guns are being drawn a ray for. For the trace, where it
    /// sits beside whether anything was actually put on screen: an overlay
    /// switched on with nothing to aim at and one that failed to draw are the
    /// same empty picture, and they are repaired in different places.</summary>
    public int Count => _rays.Count;

    /// <summary>How many of those rays claim a hole rather than merely running
    /// out. Beside the count because they are the two things this draws and one
    /// number cannot tell them apart: a gun sweeping past nothing and a gun laid
    /// on somebody are both "one ray", and only one of them is the thing the
    /// overlay was built to show.</summary>
    public int Marked
    {
        get
        {
            int marked = 0;
            foreach (Ray ray in _rays)
                if (ray.End == Tip.Hole)
                    marked++;
            return marked;
        }
    }

    /// <summary>How many were stopped by something in the way rather than by
    /// running out of board. Counted apart from <see cref="Marked"/> because the
    /// three tips are three different pieces of news and one count cannot carry
    /// them: under --capture there is no panel to look at, and "stopped at a
    /// hill" and "nothing more that way" are the same length of line.</summary>
    public int Stopped
    {
        get
        {
            int stopped = 0;
            foreach (Ray ray in _rays)
                if (ray.End == Tip.Stop)
                    stopped++;
            return stopped;
        }
    }

    /// <summary>Whether the last draw put anything on screen. Named for the same
    /// reason, and it is what asks for the one redraw after the switch goes off.
    /// </summary>
    public bool Drawn => _drawn;

    private bool _drawn;

    /// <summary>Drop last frame's rays. Called every frame before the aims are
    /// handed over, because an aim is only true for the frame it was solved on -
    /// the turret is still traversing and the target is still driving.</summary>
    public void Clear() => _rays.Clear();

    /// <summary>Hand over one gun's aim, as the harness solved it.</summary>
    public void Aim(Vector2 from, Vector2 to, Tip end) =>
        _rays.Add(new Ray(from, to, end));

    /// <summary>One ray, so it can be asserted against the round that follows it
    /// rather than read off the screen.</summary>
    public Ray At(int i) => _rays[i];

    /// <summary>One step above the tracer, which is itself above every tank.
    /// </summary>
    public const int Layer = Shell.Layer + 1;

    public override void _Ready() => ZIndex = Layer;

    public override void _Process(double _)
    {
        // Asks to be redrawn while there is anything to draw, and once more after
        // there is not: a node that stops asking the frame it stops wanting to
        // draw leaves its last picture on screen for good. Written as wanted
        // against drawn rather than as "nothing changed", the shape
        // EffectLayer.MustRedraw already has - and here "nothing changed" would be
        // wrong every frame anyway, since the gun is traversing.
        if ((On && _rays.Count > 0) || _drawn)
            QueueRedraw();
    }

    /// <summary>How long a dash and its gap are together. Long enough that the
    /// line reads as dashes rather than as a dotted one at this zoom, short enough
    /// that the shortest shot on this board - a neighbouring cell, about eighty
    /// pixels - still gets several of them.</summary>
    public const float Dash = 9.0f;


    /// <summary>
    /// How much of the ink a ray that claims nothing gets.
    ///
    /// Dimmer on purpose, and it is the same argument as the faint lanes: the
    /// selected tank carries this line permanently, so at full strength it would
    /// compete with the case that is worth seeing - a gun actually laid on
    /// somebody. Not so dim that the sweep is lost, which is the other half of
    /// what the overlay is for.
    /// </summary>
    public const float Idle = 0.55f;

    /// <summary>
    /// The ray itself: red, asked for as red.
    ///
    /// Worth naming what it costs, because the shade was picked once already to
    /// avoid it: the ground carries the route's amber, the selection ring's cyan
    /// and - closest of all - the target ring's red and the faint red lanes. So
    /// this is a harder red than either of those, full in green's absence rather
    /// than the ring's softer salmon, and it is the only dashed thing on the
    /// board. The dark backing does the rest of the work; without it a red line
    /// on dark green armour is a line nobody finds.
    /// </summary>
    public static readonly Color Ink = new(1.0f, 0.13f, 0.10f);

    /// <summary>The dark line under it, so it reads over the pale tile, over the
    /// amber route and over armour alike.</summary>
    public static readonly Color Under = new(0.12f, 0.0f, 0.10f, 0.65f);

    public override void _Draw()
    {
        _drawn = false;
        if (!On)
            return;
        foreach (Ray ray in _rays)
        {
            // Converted rather than assumed: the harness's root happens to sit at
            // identity, so global and local are the same numbers today, and a
            // drawing that reads them as the same numbers is a drawing that breaks
            // the day anything is put under a transform.
            Vector2 from = ToLocal(ray.From);
            Vector2 to = ToLocal(ray.To);
            // Written as the positive condition rather than as "shorter than a
            // pixel", because a comparison is not a test for a number that is
            // not one: NaN is neither less nor greater, so the short form lets
            // it through - and what it reaches is Godot's dashed line, which
            // resizes an array by the dash count and dies on a negative one.
            // Guarded at the source as well (Stage3D.Screen); this is the line
            // that must not be able to crash the bench either way.
            if (!(from.DistanceTo(to) >= 1.0f))
                continue;
            bool hole = ray.End == Tip.Hole;
            Color ink = hole ? Ink : new Color(Ink, Ink.A * Idle);
            Color under = hole ? Under : new Color(Under, Under.A * Idle);
            // Both dashed, at the same period, so the backing sits under the
            // dashes instead of filling the gaps between them - a solid backing
            // would put a continuous dark line on the board and undo the whole
            // reason the ray is dashed.
            DrawDashedLine(from, to, under, 3.2f, Dash);
            DrawDashedLine(from, to, ink, 1.6f, Dash);
            // The far end is the answer, so it is marked rather than merely
            // reached: a ring for the hole with a cross through it, which reads as
            // a point being pointed at instead of as a line that stopped. Only
            // where there is an answer - a line that merely ran out ends, and
            // ending is what it has to look like.
            if (hole)
            {
                DrawArc(to, 4.5f, 0.0f, Mathf.Tau, 20, Under, 3.0f);
                DrawArc(to, 4.5f, 0.0f, Mathf.Tau, 20, Ink, 1.6f);
                DrawLine(to - new Vector2(6.5f, 0.0f),
                         to + new Vector2(6.5f, 0.0f), Ink, 1.4f);
                DrawLine(to - new Vector2(0.0f, 6.5f),
                         to + new Vector2(0.0f, 6.5f), Ink, 1.4f);
            }
            else if (ray.End == Tip.Stop)
            {
                // A bar across the line rather than a ring on it: the line was
                // stopped by something, which is the opposite piece of news from
                // a hole, and across is what stopping looks like. Square to the
                // ray rather than to the screen, because what it marks is where
                // this ray ends and not a point on the board.
                Vector2 across = (to - from).Normalized().Orthogonal() * 5.5f;
                DrawLine(to - across, to + across, under, 3.4f);
                DrawLine(to - across, to + across, ink, 1.8f);
            }
            // And the near end, because "out of this gun" is half of what the line
            // says and three tanks can be firing.
            DrawCircle(from, 3.2f, under);
            DrawCircle(from, 2.0f, ink);
            _drawn = true;
        }
    }
}
