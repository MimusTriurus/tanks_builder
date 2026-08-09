using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// A stand for hexes at different heights, and nothing else on it.
///
/// Separate from the bench for <c>flash_bench.sheet()</c>'s reason: the thing
/// being judged here is a few polygons, and judging them inside the harness
/// means loading three atlases and waiting on a board that has tanks, sound,
/// a panel and eighteen layers on it. Here a hill is drawn from the tile's own
/// measurements in a scene that comes up in a second.
///
/// Run it with the scene as the positional argument, leaving project.godot
/// alone:
///
///     Godot_v4.5.2-stable_mono_win64.exe --path GodotTest res://Relief.tscn
///
/// <b>Schematic on purpose.</b> Every face here is a flat polygon, so what is
/// on screen is the geometry and only the geometry - the moment a hill is
/// painted, "does the step read" stops being a question about the step. T lays
/// the real ground over the top faces for the other half of that comparison.
///
/// What it is here to settle, in the order the questions bite:
///
/// <list type="number">
/// <item><b>The lift is measured, not chosen.</b> The tile carries the camera -
/// a flat-top hexagon is (sqrt(3)/2)*sin(e) as tall as it is wide - so the
/// ground squash comes off the plate and the vertical follows from it as
/// sqrt(1 - squash^2). Same idiom as <see cref="Grove.Squash"/>, and for the
/// same reason: a second copy of the elevation is a second thing to keep in
/// agreement.</item>
/// <item><b>The order stays on the ground row.</b> A raised cell is drawn
/// higher up the screen and is not one step further away, so sorting by drawn
/// Y walks a hill backwards behind the board. Everything here sorts on
/// <see cref="FlatCentre"/> and lifts only at the moment of drawing.</item>
/// <item><b>The click has to be undone per level.</b> One screen point belongs
/// to a low cell and to the raised cell behind it at once, and the flat inverse
/// answers with the low one - which reads as broken cell picking rather than as
/// height.</item>
/// <item><b>The tank cannot be tilted, and this is where you see by how
/// much.</b> The shear is capped at <see cref="LeanCap"/>, and the readout
/// prints what the true grade asked for beside what was drawn.</item>
/// </list>
/// </summary>
public sealed partial class ReliefBench : Node2D
{
    private const string TerrainsRoot = "D:/Projects/AgentCoding/BlenderMCP/Images/Terrains";
    private const string SpritesRoot = "D:/Projects/AgentCoding/BlenderMCP/Sprites";

    /// <summary>
    /// The tank standing on the relief. The light one, and the class matters
    /// here rather than being a default: it is the smallest of the three, so it
    /// is the one a step has the best chance of dwarfing, and it is the one
    /// whose gun does not overhang its hull - which makes its tile the widest
    /// for its hull and its silhouette the least forgiving against a bank.
    /// </summary>
    private const string TankTag = "LTP";

    /// <summary>The tile if no template loads. Not a guess - it is what the
    /// plate of the template measures and what the rendered hex layer of every
    /// atlas measures, so a run with no art on disk still has the right
    /// board.</summary>
    private static readonly Vector2 FallbackTile = new(248.0f, 109.0f);

    /// <summary>
    /// Step height as a fraction of the distance to a neighbour, so it is the
    /// <i>grade</i> that is set and the pixels follow.
    ///
    /// 1:4 is 14 degrees, which is a slope a tank climbs and a viewer reads.
    /// Written this way round because the pixels are not portable and the grade
    /// is: another tile size moves every number here and leaves the hill
    /// looking the same.
    /// </summary>
    public double StepGrade = 0.25;

    /// <summary>
    /// How far the body may be sheared, and the whole of what the sprite format
    /// allows.
    ///
    /// <see cref="BodyPitch"/> puts 0.030 at about 2px of roof travel on its
    /// 68px arm and records the ceiling in the same breath: past roughly 3px the
    /// eye stops seeing a heavy thing on its suspension and starts seeing the
    /// sprite deform. 0.055 is that ceiling. A real 1:4 slope wants 0.25, which
    /// is 17px of roof travel - <b>near six times what a shear can draw</b>, and
    /// the reason the hill has to be read off the ground rather than off the
    /// tank.
    /// </summary>
    public double LeanCap = 0.055;

    /// <summary>Screen px the marker climbs per second, so the crossing lasts
    /// long enough to be looked at.</summary>
    public double Speed = 190.0;

    /// <summary>
    /// The grade of the bank between two levels, which is a different number
    /// from the grade of the board and has to be.
    ///
    /// A step at the board's own 1:4 is a run of 214.8 ground px - the whole
    /// distance between two centres - so a bank cut at that grade leaves no flat
    /// ground at all and the tank is tilted permanently, which is the one pose
    /// the shear cannot hold. Cut steeper and the middle of the cell stays
    /// level: the tank parks flat and is only tilted while crossing, which is
    /// exactly as long as a shear can get away with.
    ///
    /// Only read in <see cref="Sides.Bank"/>.
    /// </summary>
    public double BankGrade = 0.5;

    /// <summary>
    /// What a cell wears between its own level and the next.
    ///
    /// Three, because the two that are not the bank are not degenerate versions
    /// of it - they are different pictures with different costs, and the only
    /// way to choose is to put them side by side.
    /// </summary>
    public enum Sides
    {
        /// <summary>Sloped shoulders. The only one a tank can be seen to climb,
        /// and the only one where a cell is not a hexagon: each edge is pulled
        /// in by what is across it, so no two cells on a boundary are the same
        /// shape. That unevenness is the price, and it is what the eye picks up
        /// first on a board that is mostly boundary.</summary>
        Bank,

        /// <summary>Vertical walls. Every cell is the same hexagon at a
        /// different height, which is as even as a board can be, and the
        /// transition is a wall the tank walks up.</summary>
        Cliff,

        /// <summary>
        /// Nothing at all - the hexagon lifted and left.
        ///
        /// It cannot simply be that, and the reason is worth having in one
        /// place: a hex tiling has no ground under a raised cell, so lifting one
        /// opens a hole in its own footprint the shape of a crescent. So the
        /// footprint is laid down first in shadow and the face put on top of it,
        /// which reads as a slab standing proud of the field. Uniform like the
        /// cliff, and cheaper: no side geometry anywhere on the board.
        /// </summary>
        None,
    }

    public Sides Skirt = Sides.Cliff;

    /// <summary>How much of a cell's apothem the bank may eat from each side.
    /// Under a half, so there is always a flat middle to stand on - a cell whose
    /// shoulders meet in the centre is a ramp, and a ramp is a different kind of
    /// cell with a different answer.</summary>
    public double FlatKeep = 0.42;

    /// <summary>
    /// Levels a single step may gain or lose. One, so a cliff is a cliff and the
    /// route goes round it.
    ///
    /// Gameplay rather than drawing, and it is here for one reason: without it
    /// the search walked a marker from the flooded cell straight onto the crown,
    /// three levels in one hex. Nothing about that picture is worth looking at -
    /// the body flies up the face, and the grade it reports is 0.75, which is not
    /// a number any shear is ever going to be judged against.
    /// </summary>
    public static int MaxClimb = 1;

    /// <summary>
    /// The board, one character per cell, row per line.
    ///
    /// Written out rather than hashed, unlike <see cref="TerrainSet.MixedAt"/>:
    /// a hashed board is right for a bench that wants variety and wrong for one
    /// that wants a particular shape to look at. This one has a crowned hill, a
    /// dry ditch beside flooded ones, and a shelf that puts a two-step wall in
    /// view of a one-step wall so the facets can be compared.
    /// </summary>
    private static readonly string[] Relief =
    {
        "...11..",
        "..121..",
        "wwv.1..",
        ".www...",
        "..ww...",
    };

    private const char Water = 'w';

    private TerrainSet? _terrain;

    /// <summary>The tank's atlases, or null when the sprite set is not on disk -
    /// a working state, like <see cref="TerrainSet"/> being empty, and then the
    /// marker is the crate it was before.</summary>
    private AtlasSet? _tank;

    /// <summary>Which way it is pointing, a rendered hex heading. Taken from the
    /// leg being driven rather than turned toward it: a tank that swings round
    /// on the spot is a second animation, and what this stand is about is what
    /// happens under it.</summary>
    private double _facing = 270.0;

    private bool _crate;
    private bool _sheet;
    private Label? _hud;

    /// <summary>
    /// How the body is tilted on a slope, and the three are not degrees of one
    /// thing - they are different transforms with different failures.
    /// </summary>
    public enum Tilts
    {
        /// <summary>Level, whatever the ground does. What Age of Empires and
        /// StarCraft do, and the only one with no failure mode at all.</summary>
        None,

        /// <summary>Sheared about the contact patch, as <see cref="BodyPitch"/>
        /// shears for acceleration. Draws only the horizontal share of the push
        /// and is capped where the sprite starts to read as deforming.</summary>
        Shear,

        /// <summary>
        /// Turned in the screen plane about the contact patch.
        ///
        /// Not a hack and not a worse shear: a pitch about an axis lying along
        /// the view <b>is</b> an image rotation, and on the four hex headings
        /// that run across the screen the pitch axis is 87% along the view. What
        /// it cannot do is the other two headings, where the axis lies in the
        /// image plane and the pitch is a change of camera elevation instead.
        /// </summary>
        Turn,
    }

    public Tilts Tilt = Tilts.Turn;

    /// <summary>
    /// How fast the body takes up the slope it is on. omega 12, zeta 0.9: about
    /// a quarter second to come up, and no overshoot.
    ///
    /// <b>Deliberately better damped than <see cref="BodyPitch"/></b>, whose 0.65
    /// is there because overshoot is what reads as weight. That is right for an
    /// impulse - a start, a stop, a shot - and wrong here: a slope is a level to
    /// be held, and a body that overshoots on reaching it bobs on the ramp.
    /// </summary>
    public double TiltStiffness = 144.0;
    public double TiltDamping = 21.6;
    private bool _art;
    private bool _check;
    private bool _flatPick;
    private bool _posts = true;

    private Vector2I _cell = new(0, 2);
    private Vector2I _goal = new(-1, -1);
    private string? _shotPath;
    private int _shotAfter = 3;
    private int _frames;
    private readonly List<Vector2I> _route = new();
    private int _leg;

    /// <summary>Where the marker stands with the height taken out. <b>The walk
    /// happens here and only here</b>, and the lift is put back on at the moment
    /// of drawing - which is the same split the cells are drawn under, and the
    /// reason the marker keeps its place in the order while it is crossing.
    ///
    /// Walked in the drawn space first, and the picture caught it: the depth was
    /// taken as the drawn Y with the level of the cell the marker had last
    /// <i>reached</i> added back, so half way up a plateau the correction was a
    /// level out of date and the marker sank behind the cliff it was climbing.
    /// </summary>
    private Vector2 _ground;

    /// <summary>How far off the ground it is, in screen px. Interpolated across
    /// a leg rather than stepped at the boundary: a tank that gains its 46px on
    /// arrival teleports up the cliff on one frame.</summary>
    private float _height;

    /// <summary>
    /// The height the marker is judged at for occlusion, which is not always the
    /// height it is drawn at.
    ///
    /// Across a step the two part company, and the reason is that with vertical
    /// cliffs the pose in between is not on any surface at all: half way up, the
    /// body is at 20 of the step's 46 px, which is inside the rock. The ground
    /// then covers it correctly - a raised face hides exactly what is below its
    /// own rim - and the picture is still wrong, because it is answering a
    /// question about a tank that is nowhere.
    ///
    /// So a body between two levels is judged on the <b>higher</b> of them: it is
    /// climbing the face, and a thing on a face is in front of it, not behind.
    /// Nothing else changes - parked, the two are the same number, so every case
    /// already settled stays settled.
    /// </summary>
    private float _standing;

    /// <summary>
    /// The tilt as drawn, chasing what the slope asks for.
    ///
    /// <b>Both outputs are sprung, and the angle is sprung rather than the
    /// grade.</b> The grade would be the obvious state to smooth and it snaps
    /// anyway: the drawn angle is the grade times <c>cos(heading)</c>, and the
    /// heading jumps sixty degrees at a corner, so a perfectly smooth grade
    /// still arrives on screen as a step. Springing what is actually drawn
    /// makes a corner a move of the target like any other.
    ///
    /// Both, not the one the mode is on, so switching modes mid-slope does not
    /// hand the spring a value in the other one's units.
    /// </summary>
    private double _shear, _shearRate, _turn, _turnRate;

    private double _wanted;

    /// <summary>Where the board's flat origin sits in the viewport.</summary>
    private static readonly Vector2 Origin = new(150.0f, 190.0f);

    // --- measurements ------------------------------------------------------

    /// <summary>The tile, from the template's plate. The plate is the hexagon
    /// the art was painted on and the rendered hex layer is the same rectangle,
    /// so this is the one measurement the whole stand rests on.</summary>
    private Vector2 Tile =>
        _terrain is { Any: true } t && t.Plate.Size.X > 0
            ? t.Plate.Size : FallbackTile;

    /// <summary>Ground-plane squash - sin(elevation), read off the tile.</summary>
    private float Squash
    {
        get
        {
            Vector2 tile = Tile;
            return tile.X <= 0.0f ? 0.5f
                : 2.0f * tile.Y / (Mathf.Sqrt(3.0f) * tile.X);
        }
    }

    /// <summary>Screen px a ground px of <i>height</i> is worth - cos(elevation),
    /// and the only new number height needs. Follows from the squash rather than
    /// being measured separately, because the two are one angle.</summary>
    private float RiseFactor => Mathf.Sqrt(Mathf.Max(0.0f, 1.0f - Squash * Squash));

    /// <summary>Distance to any of the six neighbours, in ground px. It is
    /// sqrt(3)*R for a flat-top hexagon, and equally twice the inradius - the
    /// same number in all six directions, which is what lets one grade serve
    /// every edge.</summary>
    private float Reach => Mathf.Sqrt(3.0f) * Tile.X * 0.5f;

    /// <summary>One level, in ground px of height.</summary>
    private float StepGround => (float)(StepGrade * Reach);

    /// <summary>One level, in screen px.</summary>
    private float Lift => StepGround * RiseFactor;

    // --- the board ---------------------------------------------------------

    private static int Columns => Relief[0].Length;
    private static int Rows => Relief.Length;

    private static bool InBounds(Vector2I cell) =>
        cell.X >= 0 && cell.X < Columns && cell.Y >= 0 && cell.Y < Rows;

    private static char Mark(Vector2I cell) =>
        InBounds(cell) ? Relief[cell.Y][cell.X] : '.';

    /// <summary>How high a cell stands, in levels. Off the board is the plain,
    /// so a wall at the edge of the board is drawn against the same datum the
    /// rest of it is.</summary>
    private static int LevelAt(Vector2I cell) => Mark(cell) switch
    {
        '1' => 1,
        '2' => 2,
        'v' => -1,
        Water => -1,
        _ => 0,
    };

    private static bool IsWater(Vector2I cell) => Mark(cell) == Water;

    // --- layout ------------------------------------------------------------

    /// <summary>Centre of a cell's hexagon with the height taken out - the
    /// ground row. <b>This is the sorting space</b>, and the one thing the lift
    /// must never reach.</summary>
    private Vector2 FlatCentre(Vector2I cell)
    {
        Vector2 tile = Tile;
        float stagger = cell.X % 2 != 0 ? tile.Y * 0.5f : 0.0f;
        return Origin + new Vector2(cell.X * tile.X * 0.75f,
                                    cell.Y * tile.Y + stagger);
    }

    /// <summary>Where the cell is actually drawn.</summary>
    private Vector2 Centre(Vector2I cell) =>
        FlatCentre(cell) - new Vector2(0.0f, LevelAt(cell) * Lift);

    /// <summary>The flat inverse: which cell a point would be in if the board
    /// were level. Undoes the squash before the standard flat-top conversion and
    /// rounds in cube coordinates, because rounding column and row apart picks
    /// the wrong cell along every slanted edge.</summary>
    private Vector2I FlatCellAt(Vector2 point)
    {
        Vector2 tile = Tile;
        Vector2 p = point - Origin;
        float size = tile.X * 0.5f;
        float y = p.Y * (Mathf.Sqrt(3.0f) * tile.X) / (2.0f * tile.Y);
        float q = 2.0f / 3.0f * p.X / size;
        float r = (-1.0f / 3.0f * p.X + Mathf.Sqrt(3.0f) / 3.0f * y) / size;
        return CubeRound(q, r);
    }

    /// <summary>
    /// Which cell a point is on, height and all.
    ///
    /// One screen point sits on a low cell and on the raised cell behind it at
    /// the same time, so the flat inverse alone answers with whichever the
    /// arithmetic reaches - which is the low one, every time, and reads as the
    /// picking being broken rather than as the board having height.
    ///
    /// Asked per level instead: drop the point by each level the board carries
    /// and keep the answers that agree about their own level. Of those, the
    /// frontmost wins, because frontmost is the one that was drawn last and is
    /// therefore the one being looked at. A board carries a handful of levels,
    /// so this is exact rather than approximate, and deterministic, which the
    /// hashed alternative would not be.
    /// </summary>
    private Vector2I CellAt(Vector2 point)
    {
        // What the picking is without the height in it, kept as a flag rather
        // than as a story: --check has to be able to fail, and the only way to
        // show that is to put the older answer back and watch it.
        if (_flatPick)
            return FlatCellAt(point);

        var best = new Vector2I(-1, -1);
        float front = float.NegativeInfinity;
        for (int level = -1; level <= 2; level++)
        {
            Vector2I cell = FlatCellAt(point + new Vector2(0.0f, level * Lift));
            if (!InBounds(cell) || LevelAt(cell) != level)
                continue;
            float row = FlatCentre(cell).Y;
            if (row <= front)
                continue;
            front = row;
            best = cell;
        }
        // Falls back to the flat answer for a click that landed on a wall rather
        // than on a top face. A wall belongs to the cell above it, but resolving
        // it that way would need the walls kept as polygons; the cell in front
        // is close enough for a stand and never off the board.
        return best.X >= 0 ? best : FlatCellAt(point);
    }

    private static Vector2I CubeRound(float q, float r)
    {
        float s = -q - r;
        int rq = Mathf.RoundToInt(q);
        int rr = Mathf.RoundToInt(r);
        int rs = Mathf.RoundToInt(s);
        float dq = Mathf.Abs(rq - q);
        float dr = Mathf.Abs(rr - r);
        float ds = Mathf.Abs(rs - s);
        if (dq > dr && dq > ds)
            rq = -rr - rs;
        else if (dr > ds)
            rr = -rq - rs;
        return new Vector2I(rq, rr + (rq - (rq & 1)) / 2);
    }

    // --- hexagon geometry --------------------------------------------------

    /// <summary>
    /// The hexagon's corners in tile units, right first and going clockwise on
    /// screen.
    ///
    /// No trigonometry, for <see cref="SelectionRing"/>'s reason: a flat-top
    /// hexagon of circumradius R has corners at (±R, 0) and (±R/2, ±sqrt(3)R/2),
    /// and the isometry squashes the ground vertically - so in units of the
    /// drawn tile those are (±w/2, 0) and (±w/4, ±h/2), with neither the camera
    /// angle nor a sine anywhere in it.
    /// </summary>
    private Vector2[] Corners()
    {
        Vector2 t = Tile;
        float x2 = t.X * 0.5f, x4 = t.X * 0.25f, y2 = t.Y * 0.5f;
        return new[]
        {
            new Vector2( x2, 0.0f),   // 0 right
            new Vector2( x4,  y2),    // 1 lower right
            new Vector2(-x4,  y2),    // 2 lower left
            new Vector2(-x2, 0.0f),   // 3 left
            new Vector2(-x4, -y2),    // 4 upper left
            new Vector2( x4, -y2),    // 5 upper right
        };
    }

    /// <summary>The heading of the neighbour across edge i, where edge i runs
    /// from corner i to corner i+1.</summary>
    private static readonly int[] EdgeHeading = { 330, 270, 210, 150, 90, 30 };

    /// <summary>The same corners with the squash taken out - the hexagon as it
    /// lies on the ground, where a bank's run has to be measured. Offsetting an
    /// edge in screen px would make the shoulder of a north-facing bank twice
    /// the shoulder of an eastern one, on ground that is the same slope.</summary>
    private Vector2[] GroundCorners()
    {
        float r = Tile.X * 0.5f;
        float a = Mathf.Sqrt(3.0f) * r * 0.5f;
        return new[]
        {
            new Vector2( r, 0.0f), new Vector2( r * 0.5f,  a),
            new Vector2(-r * 0.5f,  a), new Vector2(-r, 0.0f),
            new Vector2(-r * 0.5f, -a), new Vector2( r * 0.5f, -a),
        };
    }

    /// <summary>
    /// How near the camera something is - the number the painter's algorithm
    /// actually wants, and not the ground row once the board has height in it.
    ///
    /// At elevation e a point's distance along the view is
    /// <c>y_ground*cos(e) + z*sin(e)</c>: <b>raising something brings it nearer</b>,
    /// because the camera is above. The ground row is only the first term, which
    /// is why it was right for as long as z was zero everywhere.
    ///
    /// Both arguments are screen px - the row as the field draws it, the lift as
    /// this cell or this tank carries it - so nothing outside has to hold ground
    /// units.
    /// </summary>
    private float Depth(float row, float lift) =>
        row / Squash * RiseFactor + lift / RiseFactor * Squash;

    /// <summary>
    /// A cell's depth, taken at its <b>far</b> edge rather than its centre.
    ///
    /// A face is not a point and cannot be sorted as one: the near half of a low
    /// cell really is in front of a tank up on the terrace behind it, and the far
    /// half really is behind it. The two halves want opposite answers and a
    /// single key can only give one, so it should give the one whose failure is
    /// survivable - a tank drawn over a strip of ground it should be behind is a
    /// pixel or two, and a tank swallowed by the ground in front of it is the
    /// picture that started this.
    ///
    /// The far edge is exactly that choice: a cell goes down before the tank
    /// unless the whole of it is nearer. Among cells it changes nothing - every
    /// key moves by the same apothem - so this is only ever read at the moment a
    /// cell is weighed against something standing on the board.
    /// </summary>
    private float DepthOf(Vector2I cell) =>
        Depth(FlatCentre(cell).Y - Tile.Y * 0.5f, LevelAt(cell) * Lift);

    /// <summary>
    /// Whether a cell's surface stands above where the marker is standing - the
    /// second half of the occlusion test, and the half that was missing.
    ///
    /// <b>Flat ground in front of a thing cannot hide it, at any distance.</b>
    /// The ray from a point back to the camera only ever rises, so ground below
    /// that point is never on it. Only ground standing <i>higher</i> can be, and
    /// on a board where nothing is raised that is nothing at all - which is
    /// exactly why the harness gets away with one field node under every tank.
    ///
    /// Depth alone said otherwise and the picture showed it: for the first half
    /// of every step the cell being driven into is nearer than the tank, so it
    /// went down last and cut the hull off at the tracks. Nearer was true; it was
    /// just never the question on its own.
    /// </summary>
    private bool Above(Vector2I cell) => LevelAt(cell) * Lift > _standing + 0.5f;

    /// <summary>Ground point to screen, at a given level. The one place the lift
    /// is put back on, so nothing else in the drawing has to remember it.</summary>
    private Vector2 Screen(Vector2I cell, Vector2 ground, float level) =>
        FlatCentre(cell) + new Vector2(ground.X, ground.Y * Squash)
        - new Vector2(0.0f, level * Lift);

    /// <summary>
    /// How far a cell pulls each of its edges in, so a bank has somewhere to
    /// lie. Half the bank's run, because the cell across gives the other half -
    /// and that symmetry is the whole reason one rule covers all four cases of
    /// higher-or-lower and near-or-far: <b>the higher cell of a pair draws the
    /// bank, and both of them make room for it</b>. Nobody over-draws, nobody
    /// has to know who is drawn first.
    ///
    /// Capped, and the cap is the finding. A one-level step at the board's own
    /// grade wants a run of 214.8 ground px, which is the entire distance
    /// between two centres: at 1:4 there is no plateau anywhere on the board,
    /// only slope. So the bank is given its own steeper grade and clipped to
    /// leave a flat middle, and the readout prints what the bank actually came
    /// out at rather than what was asked for.
    /// </summary>
    /// <summary>The outward normal of edge i, in ground space. For a regular
    /// hexagon centred on nothing the edge midpoint <i>is</i> the normal
    /// direction, so there is no perpendicular to take.</summary>
    private static Vector2 Outward(Vector2[] ground, int i) =>
        ((ground[i] + ground[(i + 1) % 6]) * 0.5f).Normalized();

    /// <summary>Where edges <paramref name="i"/> and <paramref name="j"/> meet
    /// once each has been pulled in by its own inset - the corner between them.
    ///
    /// Taken as a line crossing rather than by moving the corner itself, because
    /// two adjacent edges pull in by different amounts wherever a terrace ends,
    /// and a moved corner then belongs to neither line: the polygon opens along
    /// the shallower of the two.</summary>
    private static Vector2 EdgeMeet(Vector2[] ground, float[] inset, int i, int j)
    {
        Vector2 pi = ground[i] - Outward(ground, i) * inset[i];
        Vector2 di = ground[(i + 1) % 6] - ground[i];
        Vector2 pj = ground[j] - Outward(ground, j) * inset[j];
        Vector2 dj = ground[(j + 1) % 6] - ground[j];
        float denom = di.Cross(dj);
        if (Mathf.Abs(denom) < 0.0001f)
            return pj;
        return pi + di * ((pj - pi).Cross(dj) / denom);
    }

    /// <summary>The cell's top face in its own ground frame: each edge pulled in
    /// by its own inset, corners taken as the crossings.</summary>
    private Vector2[] FlatFace(Vector2I cell)
    {
        Vector2[] hex = GroundCorners();
        var inset = new float[6];
        int level = LevelAt(cell);
        for (int i = 0; i < 6; i++)
            inset[i] = InsetFor(level - LevelAt(HexField.Step(cell, EdgeHeading[i])));
        var flat = new Vector2[6];
        for (int i = 0; i < 6; i++)
            flat[i] = EdgeMeet(hex, inset, (i + 5) % 6, i);
        return flat;
    }

    /// <summary>
    /// Where the cell across edge <paramref name="i"/> has put its own edge, in
    /// this cell's ground frame - the two points a bank has to land on.
    ///
    /// Asked of the neighbour rather than got by pushing this cell's edge
    /// outward, and the picture is what made the difference plain: a translated
    /// edge lands on the neighbour's face only where the two cells pulled in by
    /// the same amounts on the adjacent edges, and they do not wherever a
    /// terrace ends. The banks came out as a star of parallelograms shooting
    /// past their neighbours - every one of them a correct polygon aimed at a
    /// point that was not there.
    /// </summary>
    private (Vector2 A, Vector2 B) OppositeEdge(Vector2I cell, int i)
    {
        Vector2I over = HexField.Step(cell, EdgeHeading[i]);
        Vector2 shift = FlatCentre(over) - FlatCentre(cell);
        var ground = new Vector2(shift.X, shift.Y / Squash);
        Vector2[] face = FlatFace(over);
        int j = (i + 3) % 6;
        // The shared edge runs the other way round from over there, so its
        // corner j is this cell's corner i+1 and its j+1 is this cell's i.
        return (face[j] + ground, face[(j + 1) % 6] + ground);
    }

    private float InsetFor(int drop)
    {
        if (drop == 0 || Skirt != Sides.Bank || BankGrade <= 0.0)
            return 0.0f;
        float apothem = Mathf.Sqrt(3.0f) * Tile.X * 0.25f;
        float run = (float)(Math.Abs(drop) * StepGround / BankGrade) * 0.5f;
        return Mathf.Min(run, apothem * (float)FlatKeep);
    }

    // --- ink ---------------------------------------------------------------

    private static readonly Color Soil = new(0.60f, 0.57f, 0.45f);
    private static readonly Color Pond = new(0.22f, 0.40f, 0.50f);
    private static readonly Color Edge = new(0.0f, 0.0f, 0.0f, 0.22f);
    private static readonly Color Post = new(1.0f, 1.0f, 1.0f, 0.30f);

    /// <summary>The top of a cell. Higher reads lighter, which is the one hint
    /// the top faces carry - without it a plateau and the plain are the same
    /// paint and only the walls say which is which, and the walls are not in
    /// view from every side.</summary>
    private static Color FaceOf(Vector2I cell) =>
        IsWater(cell) ? Pond : SoilAt(LevelAt(cell));

    private static Color SoilAt(int level)
    {
        float step = 1.0f + 0.11f * level;
        return new Color(Soil.R * step, Soil.G * step, Soil.B * step);
    }

    /// <summary>A wall, shaded by which way it faces.
    ///
    /// The two that face the camera - the near edge of a hill and the far bank
    /// of a pit - catch more than the four flanking them, which is all it takes
    /// for the block to read as a solid rather than as a hexagon with a skirt.
    ///
    /// Earth whatever the top is, and that is not a detail: the bank of a pond
    /// is soil, and a wall taken from the pond's own blue gave the river vertical
    /// blue sides and made it a slab of glass rather than water in a cut.</summary>
    private static Color WallOf(Vector2I cell, int heading)
    {
        Color face = SoilAt(LevelAt(cell));
        float k = heading is 270 or 90 ? 0.62f : 0.46f;
        return new Color(face.R * k, face.G * k, face.B * k);
    }

    // --- life --------------------------------------------------------------

    /// <summary>The same three flags the harness has, and for the same reason:
    /// a picture is the evidence here, and taking it twice at two grades must
    /// not need a hand on the keyboard.</summary>
    private void ReadFlags()
    {
        string[] args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--shot" && i + 1 < args.Length)
                _shotPath = args[++i];
            else if (args[i] == "--shot-at" && i + 1 < args.Length
                     && int.TryParse(args[i + 1], out int at))
                _shotAfter = at;
            else if (args[i] == "--bank" && i + 1 < args.Length
                     && double.TryParse(args[i + 1],
                                        System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture,
                                        out double bank))
                BankGrade = bank;
            else if (args[i] == "--grade" && i + 1 < args.Length
                     && double.TryParse(args[i + 1],
                                        System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture,
                                        out double grade))
                StepGrade = grade;
            else if (args[i] == "--stand" && i + 1 < args.Length)
            {
                string[] parts = args[i + 1].Split(',');
                if (parts.Length == 2 && int.TryParse(parts[0], out int q)
                    && int.TryParse(parts[1], out int r))
                    _cell = new Vector2I(q, r);
            }
            else if (args[i] == "--drive" && i + 1 < args.Length)
            {
                string[] parts = args[i + 1].Split(',');
                if (parts.Length == 2 && int.TryParse(parts[0], out int q)
                    && int.TryParse(parts[1], out int r))
                    _goal = new Vector2I(q, r);
            }
            else if (args[i] == "--art")
                _art = true;
            else if (args[i] == "--flat-pick")
                _flatPick = true;
            else if (args[i] == "--skirt" && i + 1 < args.Length)
                Skirt = args[++i] switch
                {
                    "bank" => Sides.Bank,
                    "none" => Sides.None,
                    _ => Sides.Cliff,
                };
            else if (args[i] == "--crate")
                _crate = true;
            else if (args[i] == "--sheet")
                _sheet = true;
            else if (args[i] == "--tilt" && i + 1 < args.Length)
                Tilt = args[++i] switch
                {
                    "none" => Tilts.None,
                    "turn" => Tilts.Turn,
                    _ => Tilts.Shear,
                };
            else if (args[i] == "--check")
                _check = true;
        }
    }

    public override void _Ready()
    {
        ReadFlags();
        _terrain = TerrainSet.Load(TerrainsRoot);
        GD.Print($"relief: {_terrain.Note}");
        try
        {
            _tank = AtlasSet.Load(SpritesRoot, TankTag);
            GD.Print($"relief: {TankTag} tile {_tank.HexRect.Size.X}"
                     + $"x{_tank.HexRect.Size.Y}, {_tank.Count} headings, "
                     + $"drawn at {TankScale:F3}");
        }
        catch (Exception e)
        {
            GD.Print($"relief: no {TankTag} sprites ({e.Message}) - drawing a crate");
            _tank = null;
        }
        GD.Print($"relief: tile {Tile.X}x{Tile.Y}, squash {Squash:F4}, "
                 + $"rise {RiseFactor:F4}, reach {Reach:F1} ground px, "
                 + $"step {StepGround:F1} ground = {Lift:F1} screen px, "
                 + $"grade {Mathf.RadToDeg(Mathf.Atan((float)StepGrade)):F1} deg");

        _hud = new Label
        {
            Position = new Vector2(14.0f, 8.0f),
            ZIndex = 10,
        };
        _hud.AddThemeColorOverride("font_color", new Color(0.92f, 0.94f, 0.98f));
        _hud.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.8f));
        _hud.AddThemeConstantOverride("outline_size", 4);
        AddChild(_hud);

        if (_check)
        {
            Check();
            GetTree().Quit();
            return;
        }

        _cell = InBounds(_cell) ? _cell : new Vector2I(0, 2);
        _ground = FlatCentre(_cell);
        _height = LevelAt(_cell) * Lift;
        _standing = _height;
        if (InBounds(_goal))
            Order(_goal);
    }

    /// <summary>Whether a point lands on a cell's drawn top face. The hexagon is
    /// unsquashed back to a regular one and asked the three-inequality way,
    /// which has no trigonometry in it and no second copy of the camera.</summary>
    private float OnFace(Vector2I cell, Vector2 point)
    {
        Vector2 p = point - Centre(cell);
        float radius = Tile.X * 0.5f;
        float root = Mathf.Sqrt(3.0f);
        float x = Mathf.Abs(p.X);
        float y = Mathf.Abs(p.Y * (root * radius) / Tile.Y);
        return Mathf.Min(root * radius * 0.5f - y, root * radius - (root * x + y));
    }

    /// <summary>
    /// The inverse, asked over the whole board, because its failure is silent:
    /// a level-blind answer is a legal cell every time, just not the one under
    /// the cursor, and on a still frame that is invisible.
    ///
    /// <b>Asked against an oracle rather than against ownership.</b> The first
    /// version probed each cell near its own corners and demanded its own name
    /// back, and it failed nineteen times out of two hundred - on the code being
    /// right. A raised cell overhangs its low neighbours on screen, so a low
    /// cell does not own the ground over its own corner, and that is the whole
    /// point of there being height. What is actually true is one sentence and it
    /// does not mention ownership: <b>the cell picked is the frontmost one whose
    /// drawn face covers the point</b> - frontmost being last drawn, and last
    /// drawn being what you are looking at. Points covered by no face at all are
    /// the walls, which belong to nobody here and are skipped.
    /// </summary>
    private void Check()
    {
        var cells = new List<Vector2I>();
        for (int q = 0; q < Columns; q++)
        for (int r = 0; r < Rows; r++)
            cells.Add(new Vector2I(q, r));

        int bad = 0, tried = 0, onWalls = 0, shown = 0;
        Vector2 span = FlatCentre(new Vector2I(Columns - 1, Rows - 1));
        for (float x = Origin.X - Tile.X; x < span.X + Tile.X; x += 5.0f)
        for (float y = Origin.Y - Tile.Y * 2.0f; y < span.Y + Tile.Y; y += 5.0f)
        {
            var probe = new Vector2(x, y);
            var want = new Vector2I(-1, -1);
            float front = float.NegativeInfinity;
            bool onEdge = false;
            foreach (Vector2I cell in cells)
            {
                float inside = OnFace(cell, probe);
                // A point sitting on a face's own boundary is on two faces at
                // once and has no right answer, so it is not evidence either
                // way. Retired rather than resolved: the first attempt shrank
                // the oracle's hexagon instead, which retired the ties and then
                // hid the *front* cell wherever two faces genuinely overlap -
                // and they do overlap, because a sunken cell is drawn down over
                // the one in front of it.
                if (Mathf.Abs(inside) < 3.0f)
                    onEdge = true;
                if (inside <= 0.0f)
                    continue;
                float row = FlatCentre(cell).Y;
                if (row <= front)
                    continue;
                front = row;
                want = cell;
            }
            if (onEdge || want.X < 0)
            {
                onWalls++;
                continue;
            }
            tried++;
            Vector2I got = CellAt(probe);
            if (got == want)
                continue;
            bad++;
            if (shown++ < 8)
                GD.Print($"  {probe.X:F0},{probe.Y:F0} is on {want.X},{want.Y} "
                         + $"(level {LevelAt(want)}) but picked {got.X},{got.Y} "
                         + $"(level {LevelAt(got)})");
        }
        GD.Print(bad == 0
            ? $"picking: {tried} probes on a face all home, {onWalls} on walls or off the board"
            : $"picking: {bad} of {tried} probes went astray");

        // Ground that is not above the tank must never be drawn over it, and the
        // flat board is the case that matters: parked on any cell, with every
        // neighbour at its own level, nothing may come after it. This is the
        // check that was not here when the cell being driven into cut the hull
        // off at the tracks.
        int over = 0, poses = 0;
        float wasHeight = _height, wasStanding = _standing;
        foreach (Vector2I stand in cells)
        foreach (int heading in new[] { -1 }.Concat(HexField.EdgeHeadings))
        {
            // Parked on the cell, then part way onto each neighbour in turn -
            // because the failure this catches lives entirely in the crossing,
            // where the drawn height and the height it is judged at part
            // company.
            Vector2I onto = heading < 0 ? stand : HexField.Step(stand, heading);
            if (!InBounds(onto))
                continue;
            int floor = Math.Max(LevelAt(stand), LevelAt(onto));
            for (float t = 0.0f; t <= 1.0f; t += 0.25f)
            {
                if (heading < 0 && t > 0.0f)
                    break;
                poses++;
                _height = Mathf.Lerp(LevelAt(stand) * Lift, LevelAt(onto) * Lift, t);
                _standing = floor * Lift;
                float row = Mathf.Lerp(FlatCentre(stand).Y, FlatCentre(onto).Y, t);
                float depth = Depth(row, _height);
                foreach (Vector2I cell in cells)
                    if (DepthOf(cell) > depth && Above(cell) && LevelAt(cell) <= floor)
                        over++;
            }
        }
        _height = wasHeight;
        _standing = wasStanding;
        GD.Print(over == 0
            ? $"ground: {poses} poses, nothing at or below the tank draws over it"
            : $"ground: {over} cells at or below the tank would draw over it");

        var probeCell = new Vector2I(2, 1);
        Vector2[] g6 = GroundCorners();
        var ins = new float[6];
        for (int i = 0; i < 6; i++)
            ins[i] = InsetFor(LevelAt(probeCell)
                              - LevelAt(HexField.Step(probeCell, EdgeHeading[i])));
        GD.Print($"cell {probeCell.X},{probeCell.Y} level {LevelAt(probeCell)} "
                 + $"insets {string.Join(", ", Array.ConvertAll(ins, v => v.ToString("F1")))}");
        for (int i = 0; i < 6; i++)
        {
            Vector2 c = EdgeMeet(g6, ins, (i + 5) % 6, i);
            GD.Print($"  corner {i}: hex {g6[i].X:F0},{g6[i].Y:F0}"
                     + $" -> flat {c.X:F0},{c.Y:F0} (|{c.Length():F0}| of {g6[i].Length():F0})");
        }

        // And the shear, against the slope it is standing in for, on every step
        // the board can produce.
        for (int step = 1; step <= 2; step++)
        {
            double grade = step * StepGround / Reach;
            double drawn = Math.Min(grade, LeanCap) * 68.0;
            GD.Print($"lean: {step} level(s) is grade {grade:F3} = "
                     + $"{grade * 68.0:F1}px of roof, shear draws {drawn:F1}px "
                     + $"({grade * 68.0 / drawn:F1}x short)");
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { Pressed: true } click)
        {
            if (click.ButtonIndex == MouseButton.Left)
                Order(CellAt(GetGlobalMousePosition()));
            return;
        }
        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
            return;
        switch (key.Keycode)
        {
            case Key.T: _art = !_art; break;
            case Key.G: _posts = !_posts; break;
            // The cliff and the bank, one key apart. Zero is not "no bank" as a
            // setting - it is what the board looked like before there were
            // banks, which is the thing this is being judged against.
            case Key.B: Skirt = (Sides)(((int)Skirt + 1) % 3); break;
            // The crate is not a fallback here, it is the control: a silhouette
            // whose footprint is exactly known, against a tank whose is not.
            case Key.M: _crate = !_crate; break;
            // The tilt had a flag and no key, which meant the one mode that has
            // to be judged in motion could only be set before the run started.
            case Key.N: Tilt = (Tilts)(((int)Tilt + 1) % 3); break;
            case Key.Bracketleft: StepGrade = Math.Max(0.0, StepGrade - 0.05); Restand(); break;
            case Key.Bracketright: StepGrade = Math.Min(0.60, StepGrade + 0.05); Restand(); break;
            case Key.R: Restand(); break;
            case Key.Escape: GetTree().Quit(); break;
        }
        QueueRedraw();
    }

    /// <summary>Put the marker back on its cell without moving it off it. The
    /// step dial changes where every cell is drawn, so a marker left at its old
    /// screen point would be standing beside its own hex.</summary>
    private void Restand()
    {
        _route.Clear();
        _leg = 0;
        _shear = _shearRate = _turn = _turnRate = 0.0;
        _wanted = 0.0;
        _ground = FlatCentre(_cell);
        _height = LevelAt(_cell) * Lift;
        _standing = _height;
    }

    /// <summary>Walk to a cell, one hex at a time, so every leg has a grade of
    /// its own. A straight run across the board would have one average grade and
    /// would climb nothing in particular.</summary>
    private void Order(Vector2I goal)
    {
        if (!InBounds(goal) || goal == _cell)
            return;
        List<Vector2I> path = Route(_cell, goal);
        if (path.Count == 0)
            return;
        _route.Clear();
        _route.AddRange(path);
        _leg = 0;
    }

    /// <summary>Breadth-first over <see cref="HexField.Step"/> - the project's
    /// one definition of what is next to what, taken as it is rather than
    /// rewritten in axial arithmetic, which would be wrong down the odd columns
    /// and right in a screenshot.</summary>
    private static List<Vector2I> Route(Vector2I from, Vector2I to)
    {
        var path = new List<Vector2I>();
        var cameFrom = new Dictionary<Vector2I, Vector2I> { [from] = from };
        var queue = new Queue<Vector2I>();
        queue.Enqueue(from);
        bool found = false;
        while (queue.Count > 0 && !found)
        {
            Vector2I cell = queue.Dequeue();
            foreach (int heading in HexField.EdgeHeadings)
            {
                Vector2I next = HexField.Step(cell, heading);
                if (!InBounds(next) || cameFrom.ContainsKey(next)
                    || Math.Abs(LevelAt(next) - LevelAt(cell)) > MaxClimb)
                    continue;
                cameFrom[next] = cell;
                if (next == to)
                {
                    found = true;
                    break;
                }
                queue.Enqueue(next);
            }
        }
        if (!found)
            return path;
        for (Vector2I at = to; at != from; at = cameFrom[at])
            path.Add(at);
        path.Reverse();
        return path;
    }

    public override void _Process(double delta)
    {
        // Fixed while a shot is being taken, and capped otherwise, and the
        // reason is the spring rather than the walk.
        //
        // Explicit Euler on a spring is unstable past dt = 2/omega, which at
        // omega 12 is 0.167s - and the first frame of a window is longer than
        // that. Measured: the tilt came out at 26.1 degrees against a target of
        // 10.5 and spent the rest of the run decaying toward it, which reads as
        // a tank that lurches on setting off and never quite settles. The
        // harness fixes its step under --capture for the neighbouring reason -
        // two runs have to be diffable - and both wants are the same fix.
        delta = _shotPath is not null ? 1.0 / 60.0 : Math.Min(delta, 1.0 / 30.0);
        Walk(delta);
        // After the walk, because the walk is what sets the target, and every
        // frame rather than only while moving - a tilt that stopped being
        // integrated the moment the route ended would freeze at whatever angle
        // the last leg left it on.
        Settle(delta);
        QueueRedraw();
        Report();

        // A few frames in, the way the harness does it: the viewport holds the
        // last frame finished, so asking on the first one saves a blank.
        if (_shotPath is null || ++_frames <= _shotAfter)
            return;
        Image image = GetViewport().GetTexture().GetImage();
        Error err = image.SavePng(_shotPath);
        GD.Print(err == Error.Ok ? $"shot: {_shotPath}"
                                 : $"shot to {_shotPath} failed: {err}");
        _shotPath = null;
        GetTree().Quit();
    }

    private void Walk(double delta)
    {
        if (_leg >= _route.Count)
        {
            _wanted = 0.0;
            // Target only. The spring keeps running in Settle, so the tilt
            // eases back to level instead of dropping on the last frame.

            _height = LevelAt(_cell) * Lift;
            _standing = _height;
            return;
        }

        Vector2I next = _route[_leg];
        Vector2 from = FlatCentre(_cell), goal = FlatCentre(next);

        // Snapped to the leg, because a hex step is already one of the rendered
        // headings - there is no rounding to do and nothing to turn through.
        int heading = HexField.HeadingTo(_cell, next);
        if (heading >= 0)
            _facing = heading;

        // The grade of this leg, and the whole of what the climb is. Levels
        // times the step over the distance to a neighbour - both in ground px,
        // so the ratio is the real slope and not a screen one.
        _wanted = (LevelAt(next) - LevelAt(_cell)) * StepGround / Reach;

        Vector2 away = goal - _ground;
        float step = (float)(Speed * delta);
        if (away.Length() <= step)
        {
            _ground = goal;
            _cell = next;
            _leg++;
            _height = LevelAt(_cell) * Lift;
            _standing = _height;
            // The lean is left on the leg just finished until the next one
            // starts, rather than dropped here: a body that flattens for one
            // frame at every boundary flickers all the way up the hill.
        }
        else
        {
            _ground += away.Normalized() * step;
            float leg = (goal - from).Length();
            float done = leg <= 0.0f ? 1.0f
                : Mathf.Clamp((_ground - from).Length() / leg, 0.0f, 1.0f);
            _height = Mathf.Lerp(LevelAt(_cell) * Lift, LevelAt(next) * Lift, done);
            _standing = Math.Max(LevelAt(_cell), LevelAt(next)) * Lift;
        }

    }

    /// <summary>
    /// The body taking up the slope, and letting go of it.
    ///
    /// A grade is a level, not an event, so the target is held for as long as
    /// the leg lasts and the spring simply sits on it. What the spring buys is
    /// the two ends: the tilt comes up over about a quarter second at the start
    /// and goes back down over the same at the finish, instead of appearing and
    /// vanishing on the frame the leg changes.
    ///
    /// Run for both outputs whichever is being drawn - see
    /// <see cref="_shear"/> - and with the same constants, because it is one
    /// question either way: how quickly the hull follows the ground.
    /// </summary>
    private void Settle(double delta)
    {
        if (delta <= 0.0)
            return;
        (double shear, double turn) = TargetTilt(_wanted, _facing);
        _shearRate += (-TiltStiffness * (_shear - shear)
                       - TiltDamping * _shearRate) * delta;
        _shear += _shearRate * delta;
        _turnRate += (-TiltStiffness * (_turn - turn)
                      - TiltDamping * _turnRate) * delta;
        _turn += _turnRate * delta;
    }

    private void Report()
    {
        if (_hud is null)
            return;
        if (_sheet)
        {
            double across = Math.Cos(Mathf.DegToRad(330.0)) * RiseFactor;
            _hud.Text =
                $"grade 1:{1.0 / StepGrade:F0} = "
                + $"{Mathf.RadToDeg(Mathf.Atan((float)StepGrade)):F1} deg per level\n"
                + "columns:  heading 330 down / flat / up   |   "
                + "heading 270 down / flat / up\n"
                + $"pitch axis along the view: {Math.Abs(across) * 100.0:F0}% on 330, "
                + "0% on 270 - which is why the turn does nothing on the right half\n"
                + $"shear capped at {LeanCap:F3} = {LeanCap * 68.0:F1}px of roof; "
                + $"turn is {Mathf.RadToDeg((float)(Math.Atan(StepGrade) * across)):F1} deg "
                + "and rigid - no length on the silhouette changes";
            return;
        }
        double drawn = Math.Abs(_shear) * 68.0;
        double honest = Math.Abs(_wanted) * 68.0;
        _hud.Text =
            $"tile {Tile.X:F0}x{Tile.Y:F0}   squash {Squash:F4}   "
            + $"rise sqrt(1-squash^2) {RiseFactor:F4}\n"
            + $"neighbour {Reach:F1} ground px   grade 1:{1.0 / Math.Max(0.001, StepGrade):F1}"
            + $" = {Mathf.RadToDeg(Mathf.Atan((float)StepGrade)):F1} deg   "
            + $"step {StepGround:F1} ground = {Lift:F1} screen px\n"
            + $"cell {_cell.X},{_cell.Y}   level {LevelAt(_cell)}"
            + (IsWater(_cell) ? " (water)" : "")
            + "\n" + Tilted(drawn, honest) + "\n"
            + Banks() + "\n"
            + "left click drive   [ ] step   B sides   N tilt   M tank/crate   "
            + "T ground art   G posts   R reset   ESC quit";
    }

    /// <summary>
    /// What the body is doing about the slope, in the terms of whichever
    /// transform is switched on.
    ///
    /// One line per mode rather than one line for all three, because they do not
    /// share a number. Read in shear's terms while the turn was on, it printed
    /// "0.0px drawn, no share across the screen" - true of the shear, which was
    /// not running, and a flat lie about the picture, which was tilted ten
    /// degrees. A readout that describes a mode that is off is worse than none.
    /// </summary>
    private string Tilted(double drawn, double honest)
    {
        double along = Math.Cos(Mathf.DegToRad(_facing)) * RiseFactor;
        switch (Tilt)
        {
            case Tilts.None:
                return $"tilt: level - slope is {honest:F1}px of roof and none of "
                       + "it is drawn, which is what Age of Empires does";
            case Tilts.Turn:
                // Drawn beside asked, because the whole of the spring is the gap
                // between them: equal means it has arrived, and a still frame
                // cannot tell settled from never-moved.
                double want = Mathf.RadToDeg((float)(Math.Atan(_wanted) * along));
                double now = Mathf.RadToDeg((float)_turn);
                return $"tilt: turn {Math.Abs(now):F1} deg drawn of "
                       + $"{Math.Abs(want):F1} asked (rigid)   "
                       + $"axis {Math.Abs(along) * 100.0:F0}% along the view on "
                       + $"heading {_facing:F0}"
                       + (Math.Abs(along) < 0.01
                          ? " - into the screen, so an image turn reaches none of it"
                          : "");
            default:
                return $"tilt: shear {drawn:F1}px of roof, slope wanted {honest:F1}px"
                       + (drawn < 0.05 && honest > 0.05
                          ? "  [no share across the screen]"
                          : honest > drawn + 0.05 ? $"  [capped {honest / drawn:F1}x]"
                                                  : "");
        }
    }

    /// <summary>What the bank actually came out at, which is not what was asked
    /// for whenever the cap bites - and at this board's grade it always bites.
    /// Printed rather than left in the dial, because the dial says 1:2 while the
    /// ground says 1:1.7 and only one of the two is on screen.</summary>
    private string Banks()
    {
        if (Skirt == Sides.None)
            return "sides: none - lifted slabs on their own shadow";
        if (Skirt == Sides.Cliff)
            return "sides: vertical cliffs - every cell the same hexagon";
        float apothem = Mathf.Sqrt(3.0f) * Tile.X * 0.25f;
        float inset = InsetFor(1);
        double got = StepGround / (2.0 * inset);
        double flatLeft = (apothem - inset) / apothem;
        return $"sides: bank asked 1:{1.0 / BankGrade:F1}, cut 1:{1.0 / got:F1} "
               + $"= {Mathf.RadToDeg(Mathf.Atan((float)got)):F0} deg over "
               + $"{2.0 * inset:F0} ground px"
               + (got > BankGrade + 0.005 ? "  [capped]" : "")
               + $"   flat middle {flatLeft * 100.0:F0}% of the apothem";
    }

    // --- drawing -----------------------------------------------------------

    /// <summary>
    /// The tank at every tilt against every slope the board can produce, on one
    /// page and off the board.
    ///
    /// On the board the question cannot be answered: the body is 110px in a
    /// 1600px frame, it is moving, and the ground behind it is different in
    /// every cell. "Does the sprite deform" is a question about the silhouette
    /// and wants the silhouette large, still, repeated and against nothing.
    ///
    /// Two headings, because the finding is that they are not the same case: 330
    /// runs across the screen where the pitch axis is 87% along the view and the
    /// pitch is nearly an image rotation, and 270 runs into it where the axis
    /// lies in the image plane and there is nothing an image transform can do.
    /// </summary>
    private void DrawSheet()
    {
        if (_tank is null)
            return;
        (string Name, Tilts Mode)[] rows =
        {
            ("level", Tilts.None), ("shear", Tilts.Shear), ("turn", Tilts.Turn),
        };
        (int Heading, double Grade, string Name)[] columns =
        {
            (330, -0.25, "330 down"), (330, 0.0, "330 flat"), (330, 0.25, "330 up"),
            (270, -0.25, "270 down"), (270, 0.0, "270 flat"), (270, 0.25, "270 up"),
        };
        var cell = new Vector2(248.0f, 235.0f);
        var start = new Vector2(160.0f, 260.0f);
        Font font = ThemeDB.FallbackFont;
        var ink = new Color(0.92f, 0.94f, 0.98f);
        Tilts was = Tilt;

        // Column headings go in the readout rather than over the page: a label
        // above a tank lands inside the tank above it, and the sprite is 190px
        // of the 235 a row has.
        for (int r = 0; r < rows.Length; r++)
        {
            DrawString(font, new Vector2(30.0f, start.Y + r * cell.Y - 10.0f),
                       rows[r].Name, HorizontalAlignment.Left, -1, 17, ink);
            for (int c = 0; c < columns.Length; c++)
            {
                Tilt = rows[r].Mode;
                (float lean, float turn) = TiltOf(columns[c].Grade, columns[c].Heading);
                DrawTankAt(start + new Vector2(c * cell.X, r * cell.Y),
                           columns[c].Heading, lean, turn);
            }
        }
        Tilt = was;
    }

    public override void _Draw()
    {
        if (_sheet)
        {
            DrawSheet();
            return;
        }

        // Back to front by ground row, with the marker slotted in at its own
        // row rather than drawn after everything. Sorting on the drawn Y instead
        // would walk the hill backwards: a raised cell moves up the screen and
        // is not one step further away.
        var order = new List<Vector2I>(Columns * Rows);
        for (int q = 0; q < Columns; q++)
        for (int r = 0; r < Rows; r++)
            order.Add(new Vector2I(q, r));
        order.Sort((a, b) => DepthOf(a).CompareTo(DepthOf(b)));

        // The marker's own depth, from where it stands and how far off the
        // ground it is. Both are already tracked apart, which is what makes this
        // one line rather than a correction.
        float depth = Depth(_ground.Y, _height);
        bool placed = false;

        foreach (Vector2I cell in order)
        {
            if (!placed && DepthOf(cell) > depth && Above(cell))
            {
                DrawMarker();
                placed = true;
            }
            DrawCell(cell);
        }
        if (!placed)
            DrawMarker();
    }

    private void DrawCell(Vector2I cell)
    {
        Vector2 top = Centre(cell);
        int level = LevelAt(cell);

        var drop = new int[6];
        for (int i = 0; i < 6; i++)
            drop[i] = level - LevelAt(HexField.Step(cell, EdgeHeading[i]));

        // Each edge line pushed inward by its own inset, corners taken as the
        // crossings. Offsetting the corners instead would open the polygon
        // wherever two adjacent edges pull in by different amounts, which is
        // every corner of a terrace that ends.
        Vector2[] flat = FlatFace(cell);

        var face = new Vector2[6];
        for (int i = 0; i < 6; i++)
            face[i] = Screen(cell, flat[i], level);

        // With no sides at all the cell still has to fill its own footprint, or
        // it lifts off a hole the shape of a crescent. Its own hexagon, put down
        // at the datum in shadow, is the cheapest thing that does - and it is
        // the ground the slab is standing on, which is what it looks like.
        if (Skirt == Sides.None && level != 0)
        {
            var under = new Vector2[6];
            for (int i = 0; i < 6; i++)
                under[i] = Screen(cell, flat[i], 0.0f);
            Color soil = SoilAt(level);
            DrawColoredPolygon(under,
                new Color(soil.R * 0.42f, soil.G * 0.42f, soil.B * 0.42f));
        }

        // Which of the two goes down first is decided by the mode, and it is not
        // a preference either way.
        //
        // With vertical walls the cell keeps its full hexagon, so the three
        // walls facing away are covered by its own face and have to be under it
        // - drawn after, they paint pale slabs over the grass, which is exactly
        // what the picture showed.
        //
        // With banks the cell has pulled its edges in, so the ground art - a
        // rectangle painted on the full hexagon - hangs out over its own bank,
        // and only the bank drawn afterwards takes it back.
        bool wallsUnder = Skirt != Sides.Bank;
        if (wallsUnder)
            DrawSides(cell, flat, drop, level);

        if (_art && !IsWater(cell) && _terrain is { Any: true } t
            && t.Texture(t.Has(TerrainSet.Default) ? TerrainSet.Default
                                                   : TerrainSet.Plain) is { } ground)
        {
            var frame = new Rect2I(Vector2I.Zero, (Vector2I)Tile);
            DrawTextureRect(ground, t.RectAt(top, t.ScaleTo(frame)), false,
                            new Color(1.0f, 1.0f, 1.0f).Lerp(FaceOf(cell), 0.35f));
        }
        else
        {
            DrawColoredPolygon(face, FaceOf(cell));
            if (IsWater(cell))
                Ripples(top, Corners());
        }

        if (!wallsUnder)
            DrawSides(cell, flat, drop, level);

        var outline = new Vector2[7];
        for (int i = 0; i < 6; i++)
            outline[i] = face[i];
        outline[6] = outline[0];
        DrawPolyline(outline, Edge, 1.0f);

        // The post says how far off the ground the cell is standing, which is
        // the one thing a still frame of a top face cannot say.
        if (_posts && level != 0)
            DrawLine(FlatCentre(cell), top, Post, 1.0f);
    }

    /// <summary>
    /// The faces between a cell and whatever is across each of its edges.
    ///
    /// One rule for all four cases: the higher cell of a pair draws the side
    /// between them, and both have already made room for it. That covers the
    /// near side of a hill, the far bank of a pit, a terrace with a shelf in
    /// front and one with a shelf behind, without anybody knowing who is drawn
    /// first - which is what the two hand-written cases it replaces both got
    /// wrong once each.
    /// </summary>
    private void DrawSides(Vector2I cell, Vector2[] flat, int[] drop, int level)
    {
        if (Skirt == Sides.None)
            return;
        for (int i = 0; i < 6; i++)
        {
            if (drop[i] <= 0)
                continue;
            (Vector2 c, Vector2 d) = OppositeEdge(cell, i);
            float under = level - drop[i];
            DrawColoredPolygon(new[]
            {
                Screen(cell, flat[i], level), Screen(cell, flat[(i + 1) % 6], level),
                Screen(cell, c, under), Screen(cell, d, under),
            }, WallOf(cell, EdgeHeading[i]));
        }
    }

    /// <summary>Two light strokes across the pond. Enough for water to stop
    /// reading as a blue hex, and deliberately not more: the moment it is
    /// painted, "does the level read" stops being a question about the
    /// level.</summary>
    private void Ripples(Vector2 top, Vector2[] corner)
    {
        var ink = new Color(1.0f, 1.0f, 1.0f, 0.20f);
        float w = Tile.X, h = Tile.Y;
        DrawLine(top + new Vector2(-w * 0.22f, -h * 0.10f),
                 top + new Vector2(w * 0.06f, -h * 0.10f), ink, 2.0f);
        DrawLine(top + new Vector2(-w * 0.04f, h * 0.12f),
                 top + new Vector2(w * 0.24f, h * 0.12f), ink, 2.0f);
    }

    /// <summary>
    /// A tank's worth of box, standing on its contact patch.
    ///
    /// Schematic to the point of being a crate, and that is the point: what is
    /// being judged is whether it stands on the cell, whether it is covered by
    /// what is in front of it, and how much of the slope the shear can show. A
    /// sprite here would answer none of those and would raise the question of
    /// whether its own anchor was right.
    ///
    /// 68px tall because that is <see cref="BodyPitch"/>'s arm, so the roof
    /// travel printed in the readout is the number that class is tuned against
    /// rather than a second one to compare it with.
    /// </summary>
    /// <summary>What the tank's own tile has to be multiplied by to sit on this
    /// board's. One on the three sets as they stand - every hex layer is 248 px
    /// across - but taken rather than assumed, because it is the same question
    /// the ground art answers with <see cref="TerrainSet.ScaleTo"/> and a tank
    /// standing on a board of another size is the one thing this stand must not
    /// quietly get wrong.</summary>
    private float TankScale =>
        _tank is null || _tank.HexRect.Size.X <= 0
            ? 1.0f : Tile.X / _tank.HexRect.Size.X;

    /// <summary>
    /// The tank, in the order <see cref="TankSprite.LayerOrder"/> puts it in -
    /// contact shadow, hull, both belts, turret, gun.
    ///
    /// Six draws rather than a <see cref="TankSprite"/>: that class carries
    /// every clock the harness has, and what is being judged here is where the
    /// thing stands, not what it does. The layers all draw against one anchor,
    /// which is exactly the property this is testing - a tank that came apart on
    /// a slope would come apart here.
    /// </summary>
    /// <summary>
    /// The two numbers a slope is worth on screen, for a body travelling along
    /// <paramref name="heading"/> at <paramref name="grade"/>.
    ///
    /// The ground direction of a hex heading is <c>(cos h, -sin h)</c>, so the
    /// pitch axis - horizontal and across the travel - is <c>(sin h, cos h)</c>,
    /// and its component along the view is <c>cos(h)*cos(e)</c>. That single
    /// factor decides everything: at <c>h = 90</c> or <c>270</c> it is zero and
    /// the pitch is pure foreshortening, which no transform of a flat sprite
    /// reaches; on the other four headings it is 0.75, and the pitch is very
    /// nearly an image rotation.
    ///
    /// The shear is capped and the turn is not, and that asymmetry is the point.
    /// A shear past a few pixels reads as the sprite deforming because nothing
    /// on a tank shears. A turn is a rigid motion - the silhouette keeps every
    /// length it had - so the only thing limiting it is whether the pitch is
    /// real, and 14 degrees of it is.
    /// </summary>
    /// <summary>What the slope asks for, both ways at once and before any
    /// smoothing - the spring's target, and what the sheet draws directly
    /// because a sheet has no time in it.</summary>
    private (double Shear, double Turn) TargetTilt(double grade, double heading)
    {
        double along = Math.Cos(Mathf.DegToRad(heading));
        return (Math.Clamp(grade, -LeanCap, LeanCap) * -Math.Sign(along),
                -Math.Atan(grade) * along * RiseFactor);
    }

    private (float Shear, float Turn) TiltOf(double grade, double heading)
    {
        double along = Math.Cos(Mathf.DegToRad(heading));
        switch (Tilt)
        {
            case Tilts.Shear:
                return ((float)(Math.Clamp(grade, -LeanCap, LeanCap)
                                * -Math.Sign(along)), 0.0f);
            case Tilts.Turn:
                // Negated because a climb is nose *up*: the travel direction is
                // where the nose is, and Godot's positive rotation carries +x
                // down the screen, so the sign that reads as climbing is the
                // one that reads as diving in the maths.
                return (0.0f, (float)(-Math.Atan(grade) * along * RiseFactor));
            default:
                return (0.0f, 0.0f);
        }
    }

    private void DrawTank(Vector2 foot) =>
        DrawTankAt(foot, _facing,
                   Tilt == Tilts.Shear ? (float)_shear : 0.0f,
                   Tilt == Tilts.Turn ? (float)_turn : 0.0f);

    private void DrawTankAt(Vector2 foot, double facing, float lean, float turn)
    {
        AtlasSet atlas = _tank!;
        float scale = TankScale;
        Vector2 origin = foot - atlas.GroundOffset * scale;

        // The shadow lies on the ground, so it is drawn before the shear and
        // never sheared: a contact shadow that leans with the hull has come off
        // the ground it is meant to be printed on.
        if (atlas.HasShadow)
            DrawTankLayer(atlas, AtlasSet.ShadowName, origin, scale, facing);

        bool tilted = lean != 0.0f || turn != 0.0f;
        if (tilted)
        {
            // Both pivot on the contact patch, which is the pivot every tilt in
            // the project uses: the tank turns or leans, the ground it stands on
            // does not move.
            var basis = new Transform2D(
                new Vector2(Mathf.Cos(turn), Mathf.Sin(turn)),
                new Vector2(-lean * Mathf.Cos(turn) - Mathf.Sin(turn),
                            -lean * Mathf.Sin(turn) + Mathf.Cos(turn)),
                Vector2.Zero);
            basis.Origin = foot - basis.BasisXform(foot);
            DrawSetTransformMatrix(basis);
        }

        DrawTankLayer(atlas, "hull", origin, scale, facing);
        foreach (string belt in AtlasSet.TrackNames)
            if (atlas.Has(belt))
                DrawTankLayer(atlas, belt, origin, scale, facing);
        DrawTankLayer(atlas, "turret", origin, scale, facing);
        if (atlas.Has(AtlasSet.BarrelName))
            DrawTankLayer(atlas, AtlasSet.BarrelName, origin, scale, facing);

        if (tilted)
            DrawSetTransformMatrix(Transform2D.Identity);
    }

    /// <summary>One layer, the way <c>TankSprite.DrawLayer</c> draws it: the
    /// frame's own trimmed box moved by what came off the tile's top-left, so a
    /// packed set and an unpacked one land in the same place.</summary>
    private void DrawTankLayer(AtlasSet atlas, string layer, Vector2 origin,
                               float scale, double facing)
    {
        int index = atlas.FrameFor(facing);
        Vector2 size = atlas.SizeOf(layer, index);
        if (size.X <= 0.0f || size.Y <= 0.0f)
            return;
        DrawTextureRectRegion(atlas.Texture(layer),
            new Rect2(origin + (-atlas.Anchor + atlas.OffsetOf(layer, index)) * scale,
                      size * scale),
            atlas.Region(layer, index));
    }

    private void DrawMarker()
    {
        const float half = 48.0f, tall = 68.0f;
        float lean = Tilt == Tilts.Shear ? (float)_shear : 0.0f;
        Vector2 foot = _ground - new Vector2(0.0f, _height);

        if (_tank is not null && !_crate)
        {
            DrawTank(foot);
            return;
        }

        // The contact patch, squashed the way the ground is - so it lies on the
        // hexagon rather than standing in it.
        var patch = new Vector2[16];
        for (int i = 0; i < 16; i++)
        {
            float a = Mathf.Tau * i / 16.0f;
            patch[i] = foot + new Vector2(Mathf.Cos(a) * 54.0f,
                                          Mathf.Sin(a) * 54.0f * Squash);
        }
        DrawColoredPolygon(patch, new Color(0.0f, 0.0f, 0.0f, 0.28f));

        // Sheared about the foot: a point at height t moves sideways by lean*t,
        // which is a Transform2D in the real thing and four corners here.
        var body = new[]
        {
            foot + new Vector2(-half, 0.0f),
            foot + new Vector2( half, 0.0f),
            foot + new Vector2( half + lean * tall, -tall),
            foot + new Vector2(-half + lean * tall, -tall),
        };
        DrawColoredPolygon(body, new Color(0.40f, 0.44f, 0.34f));
        DrawPolyline(new[] { body[0], body[1], body[2], body[3], body[0] },
                     new Color(0.0f, 0.0f, 0.0f, 0.45f), 1.5f);

        Vector2 roof = foot + new Vector2(lean * tall, -tall);
        DrawCircle(roof + new Vector2(0.0f, -10.0f), 16.0f,
                   new Color(0.50f, 0.54f, 0.42f));
    }
}
