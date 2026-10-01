using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The round in the air (<see cref="CelShell"/>): where the shot goes, what
/// it meets, and what that does - the sprites' <see cref="Shell"/> and the
/// half of <see cref="TankTick"/> that lands it, on the model.
///
/// <list type="bullet">
/// <item><b>Every round leaves along the bore and falls</b> - a throw from
/// the muzzle at the class's speed under its fall (<see cref="Ballistics"/>),
/// walked in equal steps of time until it meets the target's plates
/// (<see cref="CelHit.Cast"/>), the ground or the board's edge, and flown
/// at that pace: slowest at the top. <b>The tube is the aim</b>: where it
/// points, the round goes - laid by hand (Q/E, R/F) or by
/// <see cref="Laying"/> on to a hex picked with the right button, which the
/// rules must allow (<see cref="AimCell"/>: on one of the six axes, the
/// mortar's 3 to 5 out, a gun's tank not next to it).</item>
/// <item><b>The mortar too</b> (<see cref="MovementProfile.Lobs"/>): its tube
/// - 12.4 to 40.5 degrees on HMR, its rest 26.46 and 14 either way - is laid
/// by the range (<see cref="LobLay"/>), and the charge (<see cref="Charge"/>)
/// puts the bomb on the hex. Its
/// curve was a parabola over the chord, the top off the sprites' lob table
/// (<see cref="Gunnery.LobDeg"/>, 40 to 45 degrees, which this tube cannot
/// reach): the bomb left the muzzle steeper than the tube pointed and came
/// down where the bore did not say. Over the board it swells and has the
/// board's shadow (<see cref="ShellShade"/>); whatever it meets, it bursts on
/// (<see cref="Bursts"/>) - there is no plate for a round from above.</item>
/// <item><b>On the target the rules decide</b>: the face it came in
/// through, the plate (<see cref="Gunnery.PlateFor"/>), the depth the gun
/// gets into it (<see cref="Gunnery.Penetration"/>). In - a hole, the
/// model's pierce; held on a flank - the ricochet, and the round <b>flies
/// on</b> off the plate (<see cref="Gunnery.Ricochets"/>); held on the front
/// or the rear - spent where it hit, a gouge and sparks. The bomb bursts on
/// the roof (<see cref="CelBlast"/>). Every hit rocks the target on its
/// springs. Into the ground - the board's burst and crater; into deep water
/// - its splash and a ring on the ripples.</item>
/// </list>
/// </summary>
public sealed partial class Tank3DBench
{
    private CelShell? _shells;
    private readonly List<ShellShade> _shades = new();

    /// <summary><c>--no-tracer</c> and <c>--tracer-smoke</c>: the sprites'
    /// two switches, the same defaults.</summary>
    private bool _tracerOn = Shell.TracerOnByDefault, _tracerSmoke = Shell.SmokeOnByDefault;

    /// <summary>
    /// How fast a direct gun's round falls, world units a second squared.
    /// Not the sprites' arc - theirs is solved to land on a plate the rules
    /// chose; here nothing chose one, and the fall is what brings a round
    /// fired over empty ground back down. At this a gun laid level puts its
    /// round in the ground some seven cells out (6.7 for LTR, 7.5 for MTR on
    /// the events board), a round at two cells drops four px - nothing - and
    /// every degree of elevation (R) carries it further.
    /// </summary>
    private const float ShellFall = 160.0f;

    /// <summary>
    /// The mortar's bomb: how fast it leaves, world units a second, and how
    /// hard it falls. The fall is the bomb's; the speed is only Space's - a
    /// bomb laid on a hex (<see cref="Laying"/>) goes with the charge for it
    /// (<see cref="Charge"/>). One speed for the whole of the rules' reach
    /// (3 to 5 cells) wants a tube of 12 to 40 degrees, which is HMR's to the
    /// degree - and its three cells fell at the bottom stop with the ground
    /// level and past it with the ground lower: the reach and the tube cannot
    /// both be met by one speed. A mortar's charges are how a real one does it.
    /// </summary>
    private const float LobSpeed = 720.0f, LobFall = 590.0f;

    /// <summary>The class's round: its speed out of the muzzle and its fall.</summary>
    private (float Speed, float Fall) Ballistics =>
        _profile.Lobs ? (LobSpeed, LobFall) : (Shell.Speed, ShellFall);

    /// <summary>How long a round is followed before it is let go off the
    /// board unseen, s, and how long a ricochet flies on.</summary>
    private const float ShellLongest = 2.5f, GlanceLongest = 0.8f;

    /// <summary>A round's column against a hull's plunge, the splash's might.</summary>
    private const float ShellSplashMight = 0.8f;

    /// <summary>How far a step of the throw goes, world units: short enough
    /// that a turret roof is not stepped over.</summary>
    private const float ShellStep = 6.0f;

    /// <summary>A ricochet's speed against the round's: it leaves the plate
    /// having given it most of itself.</summary>
    private const float GlanceSpeed = 0.55f;

    private void ShellsBuild()
    {
        if (_shells is not null)
            return;
        _shells = new CelShell { Name = "Shells" };
        AddChild(_shells);
        _shells.Build();
    }

    /// <summary>The round of the shot just fired.</summary>
    private void Launch()
    {
        ShellsBuild();
        Vector3 muzzle = _model.Muzzle.GlobalPosition;
        Vector3 bore = _model.Muzzle.GlobalBasis.Z.Normalized();
        var (speed, fall) = Ballistics;
        // A bomb laid on a hex goes with the charge that puts it there.
        if (_profile.Lobs && _chargeFor is Vector3 to && Charge(muzzle, bore, to, fall) is float v)
            speed = v;
        _chargeFor = null;
        _aimRound = Throw(muzzle, bore * speed, fall, ShellLongest, true, _profile.Lobs);
    }

    /// <summary>Where the bomb being fired was laid on, for its charge.</summary>
    private Vector3? _chargeFor;

    /// <summary>
    /// The mortar's charge: the speed out of the muzzle that brings a bomb
    /// leaving along <paramref name="bore"/> down on <paramref name="to"/> -
    /// <c>v² = g·D² / (2·cos²θ·(D·tanθ − h))</c>; none when the bore cannot
    /// carry it there at any speed (pointing at or under the line to it).
    /// </summary>
    private static float? Charge(Vector3 from, Vector3 bore, Vector3 to, float g)
    {
        Vector3 d = to - from;
        float run = new Vector2(d.X, d.Z).Length();
        float flat = new Vector2(bore.X, bore.Z).Length();
        if (run < 1e-3f || flat < 1e-4f)
            return null;
        float tan = bore.Y / flat, cos2 = flat * flat;
        float over = run * tan - d.Y;
        if (over <= 1e-3f)
            return null;
        return Mathf.Sqrt(g * run * run / (2.0f * cos2 * over));
    }

    /// <summary>
    /// The angle the mortar's tube is laid at for a target <paramref name="cells"/>
    /// out: the rules' nearest (<see cref="Gunnery.LobShortest"/>) at the
    /// tube's bottom, the furthest (<see cref="Gunnery.LobReach"/>) at its top,
    /// <see cref="LobSpare"/> in from each stop, evenly between - so the tube
    /// says the range, and the charge (<see cref="Charge"/>) puts the bomb on
    /// the hex whatever the ground does there.
    /// </summary>
    private float LobLay(float cells, float pitch)
    {
        float lo = pitch - _model.Elevation + Mathf.DegToRad(_model.ElevMin + LobSpare);
        float hi = pitch - _model.Elevation + Mathf.DegToRad(_model.ElevMax - LobSpare);
        float k = Mathf.Clamp((cells - Gunnery.LobShortest) / (float)(Gunnery.LobReach - Gunnery.LobShortest), 0.0f, 1.0f);
        return Mathf.Lerp(lo, hi, k);
    }

    /// <summary>How far in from each of the tube's stops the mortar lays,
    /// degrees.</summary>
    private const float LobSpare = 1.0f;

    private float TracerCal => (float)(_profile.TracerCalibre * Shell.TracerLevel);
    private float SmokeCal => (float)(_profile.SmokeCalibre * Shell.SmokeLevel);

    /// <summary>The way between two neighbouring cells' middles, world units.</summary>
    private float CellStep
    {
        get
        {
            if (_field is null)
                return HexWidth * 0.75f;
            Vector2I c = CellHere;
            return (CellWorld(HexField.Step(c, HexField.EdgeHeadings[1])) - CellWorld(c)).Length();
        }
    }

    /// <summary>What a falling round meets the ground at, world Y: the face,
    /// or in deep water the water's top.</summary>
    private float ShellGround(Vector3 w, out bool wet)
    {
        float y = Foot(w).Y;
        wet = false;
        if (_field is null)
            return y;
        Vector2I cell = _field.FlatCellAt(Board(w));
        if (_field.InBounds(cell) && _field.IsDeep(cell))
        {
            wet = true;
            y = Mathf.Max(y, _field.WaterTop(cell) / RiseFactor);
        }
        return y;
    }

    private bool OnBoard(Vector3 w)
    {
        if (_field is null)
            return true;
        return _field.InBounds(_field.FlatCellAt(Board(w)));
    }

    /// <summary>
    /// A round thrown from <paramref name="from"/> at <paramref name="v"/>
    /// under <paramref name="fall"/>: stepped in equal time until it meets the
    /// target (<paramref name="strikes"/>), the ground, the board's edge or
    /// <paramref name="longest"/> s, then flown at that pace. A
    /// <paramref name="lob"/> bursts on whatever it meets and is drawn over
    /// the board, with its shadow.
    /// </summary>
    private CelShell.Round Throw(Vector3 from, Vector3 v, float fall, float longest, bool strikes, bool lob)
    {
        var path = new List<Vector3> { from };
        var g = new Vector3(0.0f, -fall, 0.0f);
        float h = ShellStep / Mathf.Max(v.Length(), 1.0f);
        Vector3 p = from;
        System.Action? landed = null;
        for (float t = 0.0f; t < longest; t += h)
        {
            Vector3 next = p + v * h + 0.5f * g * h * h;
            v += g * h;
            Vector3 seg = next - p;
            if (strikes && _other is { } o && o.Hits.Cast(p, seg) is { } hit
                && (hit.At - p).Length() <= seg.Length() + 0.5f)
            {
                path.Add(hit.At);
                Vector3 way = seg.Normalized();
                landed = lob ? () => Bursts(o, hit.Part, hit.At, hit.N)
                             : () => Strike(o, hit.Part, hit.At, hit.N, way);
                break;
            }
            float ground = ShellGround(next, out bool wet);
            if (next.Y <= ground)
            {
                // Where the step went under, by the share of it above.
                float above = p.Y - ShellGround(p, out _);
                float k = above / Mathf.Max(above + (ground - next.Y), 1e-4f);
                Vector3 at = p.Lerp(next, Mathf.Clamp(k, 0.0f, 1.0f));
                at.Y = ShellGround(at, out wet);
                path.Add(at);
                bool spent = !strikes;
                landed = () => Lands(at, wet, spent);
                break;
            }
            path.Add(next);
            p = next;
            if (!OnBoard(p))
                break;
        }
        return _shells!.Fly(path, v.Length(), TracerCal, SmokeCal, lob, landed, h);
    }

    /// <summary>
    /// A direct round on the target's plate. The face is where it came in
    /// from against the target's nose - front and rear a sixth of the way
    /// round each, the flanks the rest (the GDD's side is four of the six hex
    /// faces); the rest is the rules'.
    /// </summary>
    private void Strike(Other o, MeshInstance3D part, Vector3 at, Vector3 n, Vector3 way)
    {
        // Taken off the board, or swapped for another, while the round flew.
        if (!ReferenceEquals(o, _other) || !IsInstanceValid(part))
            return;
        Vector3 ahead = o.Ahead, left = new(ahead.Z, 0.0f, -ahead.X);
        Vector3 from = -new Vector3(way.X, 0.0f, way.Z);
        float bearing = Mathf.RadToDeg(Mathf.Atan2(from.Dot(left), from.Dot(ahead)));
        string face = Mathf.Abs(bearing) <= 30.0f ? "front"
                    : Mathf.Abs(bearing) >= 150.0f ? "rear"
                    : bearing > 0.0f ? "left" : "right";
        Gunnery.Plate plate = Gunnery.PlateFor(face, 0);
        int level = Gunnery.Penetration(_profile, o.Profile, plate);
        Vector3 glance = way - 2.0f * way.Dot(n) * n;
        float k;
        string what;
        if (level > 0)
        {
            o.Hits.Leave(part, at, n, way, CelHit.Kind.Hole);
            o.Hits.Pierce(part, at, n);
            k = 3.2f;
            what = "pierced";
        }
        else
        {
            o.Hits.Leave(part, at, n, way, CelHit.Kind.Gouge, Gunnery.Ricochets(plate) ? 1.0f : 0.8f);
            o.Hits.Ricochet(at, n, glance);
            k = 2.8f;
            what = "held";
            if (Gunnery.Ricochets(plate))
            {
                // Off the flank and on: what is left of it, along the glance.
                Throw(at + glance * 2.0f, glance.Normalized() * Shell.Speed * GlanceSpeed, ShellFall,
                      GlanceLongest, false, false);
                what = "ricochet";
            }
        }
        o.Pitch.Kick(k * ahead.Dot(way));
        o.Roll.Kick(-k * left.Dot(way));
        RockedOther(o, k / 3.2f);
        GD.Print($"tank3d: round on {o.Tag} {face} ({plate}), level {level}: {what}");
    }

    /// <summary>The target struck in the water: its rings (<see cref="Rocked"/>).</summary>
    private void RockedOther(Other o, float might)
    {
        Vector3 ahead = o.Ahead, left = new(ahead.Z, 0.0f, -ahead.X);
        Rocked(o.Rig.Position + ahead * o.Foot.Along + left * o.Foot.Across, ahead, o.Foot.HalfLen, o.Foot.HalfWide, might);
    }

    /// <summary>The mortar's bomb on the target: no plate to say no
    /// (<see cref="Shell.Overhead"/>) - it bursts where it meets the hull,
    /// and splashes it.</summary>
    private void Bursts(Other o, MeshInstance3D part, Vector3 at, Vector3 n)
    {
        if (!ReferenceEquals(o, _other) || !IsInstanceValid(part))
            return;
        o.Hits.Leave(part, at, n, Vector3.Down, CelHit.Kind.Splash);
        if (_celBlast is not null)
        {
            _celBlast.Burst(at, n, Foot(at));
            WoodBlast(Foot(at), 0.12f, 150.0f);
        }
        else
            FxGround(Foot(at));
        // Half a landing's sink: the bomb shoves the roof down on the springs.
        o.Heave.Kick(-0.5f * LandSink * o.Model.HullLength * Mathf.Sqrt(SinkSpring.K));
        RockedOther(o, 1.0f);
        o.Pitch.Kick(1.5f * (2.0f * CelPuff.Hash(_shells!.Rounds.Count, 5) - 1.0f));
        o.Roll.Kick(1.5f * (2.0f * CelPuff.Hash(_shells.Rounds.Count, 7) - 1.0f));
        _shake.Blast(_profile.ShotShake * 0.6);
        GD.Print($"tank3d: bomb on {o.Tag} at {o.Rig.ToLocal(at).Y:F0} up the hull");
    }

    /// <summary>A round into the ground: the board's burst and crater, or in
    /// deep water its splash and a ring; a spent ricochet only goes in.</summary>
    private void Lands(Vector3 at, bool wet, bool spent)
    {
        GD.Print($"tank3d: round into the {(wet ? "water" : "ground")} at "
                 + $"{_field?.FlatCellAt(Board(at)).ToString() ?? "-"}, {at.DistanceTo(_rig.Position) / Mathf.Max(CellStep, 1.0f):F1} cells out"
                 + (spent ? ", spent" : ""));
        if (spent)
            return;
        if (wet && _stage is not null && _field is not null)
        {
            Vector2I cell = _field.FlatCellAt(Board(at));
            float top = _field.WaterTop(cell);
            // The model's own column (CelSplash.Spout); the board's sheet
            // under --fx2d.
            if (_shellSplash is not null)
                _shellSplash.Spout(new Vector3(at.X, top / RiseFactor, at.Z), ShellSplashMight);
            else
                _stage.Splash(Board(at) - new Vector2(0.0f, top), top);
            _ripples.Strike(new Vector2(at.X, at.Z), Stage3D.Waves);
            _shake.Blast(_profile.ShotShake * 0.4);
            return;
        }
        FxGround(at);
    }

    private void ShellsTick(float dt)
    {
        if (_shells is null)
            return;
        _shells.Tracer = _tracerOn;
        _shells.Smoke = _tracerSmoke;
        _shells.Tick(dt, _camera.GlobalBasis);
        // The shadow under a bomb over the board, the board's own.
        int used = 0;
        foreach (CelShell.Round r in _shells.Rounds)
        {
            if (!r.Lofted || r.Arrived)
                continue;
            if (used >= _shades.Count)
            {
                var made = new ShellShade { Name = $"ShellShade{used}" };
                AddChild(made);
                made.Build(Squash, RiseFactor);
                _shades.Add(made);
            }
            Vector3 foot = Foot(r.Head);
            _shades[used++].Show(Spot(foot), LiftAt(foot), r.High, Squash, RiseFactor);
        }
        for (int i = used; i < _shades.Count; i++)
            _shades[i].Hide();
        // The mark stays until the round fired at it is down; a refusal's
        // for a moment, its note on the HUD until the next pick.
        if (_markRefused > 0.0f)
        {
            _markRefused -= dt;
            if (_markRefused <= 0.0f)
                _aimMark?.Hide();
        }
        else if (_aim is null && (_aimRound is null || _aimRound.Arrived))
        {
            if (_aimRound is not null)
                _aimNote = "";
            _aimRound = null;
            _aimMark?.Hide();
        }
    }

    // ------------------------------------------------------------ the laying

    /// <summary>The point picked with the right button, world, until the gun
    /// fired at it; the round fired, until it is down; the mark on the ground.</summary>
    private Vector3? _aim;
    private CelShell.Round? _aimRound;
    private MeshInstance3D? _aimMark;
    private float _markRefused;
    private string _aimNote = "";

    /// <summary>How near laid is laid: the bore within this of the bearing and
    /// of the angle the throw wants, degrees.</summary>
    private const float LaidYaw = 0.3f, LaidElev = 0.15f;

    /// <summary>
    /// The angle above the horizon a round leaving <paramref name="from"/> at
    /// <paramref name="v"/> under <paramref name="g"/> must leave at to come
    /// down on <paramref name="to"/>, radians - the lower of the two, the one
    /// a gun lays at (the other is the howitzer's over 45); none when it does
    /// not reach.
    /// </summary>
    private static float? LayFor(Vector3 from, Vector3 to, float v, float g)
    {
        Vector3 d = to - from;
        float run = new Vector2(d.X, d.Z).Length();
        if (run < 1e-3f)
            return null;
        float v2 = v * v;
        float under = v2 * v2 - g * (g * run * run + 2.0f * d.Y * v2);
        if (under < 0.0f)
            return null;
        return Mathf.Atan((v2 - Mathf.Sqrt(under)) / (g * run));
    }

    /// <summary>The right button: the round's mark where the cursor points -
    /// on the target's plates if it is over them, else on the ground - and
    /// the gun goes to it.</summary>
    private void AimAt(Vector2 screen)
    {
        if (_fate != Fate.Alive)
            return;
        Vector3 from = _camera.ProjectRayOrigin(screen);
        Vector3 way = _camera.ProjectRayNormal(screen).Normalized();
        Vector3? at = null;
        float best = float.MaxValue;
        bool onTank = false;
        if (_other?.Hits.Cast(from, way) is { } hit)
        {
            at = hit.At;
            best = (hit.At - from).Length();
            onTank = true;
        }
        // The ground: walked down the ray to where it goes under, then halved.
        const float step = 4.0f;
        float prev = 0.0f;
        for (float s = step; s < 2.0f * Back + 2000.0f && s < best; s += step)
        {
            Vector3 p = from + way * s;
            if (p.Y > ShellGround(p, out _))
            {
                prev = s;
                continue;
            }
            float lo = prev, hi = s;
            for (int i = 0; i < 12; i++)
            {
                float mid = 0.5f * (lo + hi);
                Vector3 q = from + way * mid;
                if (q.Y > ShellGround(q, out _)) lo = mid; else hi = mid;
            }
            Vector3 g = from + way * hi;
            if (OnBoard(g))
            {
                at = new Vector3(g.X, ShellGround(g, out _), g.Z);
                onTank = false;
            }
            break;
        }
        if (at is not Vector3 point)
            return;
        if (_field is null)
        {
            // No board, no cells: the point itself.
            Aim(point, point, "");
            return;
        }
        Vector2I cell = onTank && _other is { } t ? OtherCell(t) : _field.FlatCellAt(Board(point));
        AimCell(cell);
    }

    /// <summary>
    /// The rules' target (GDD units.md, «Цель выстрела»; classes.md, «HM —
    /// Навес»): a hex on one of the six axes out of the shooter's own, the
    /// mortar's three to five out (<see cref="Gunnery.LobShortest"/>,
    /// <see cref="Gunnery.LobReach"/>), a gun's anything but a tank next to it
    /// - «между стреляющим юнитом и целью-танком должен быть хотя бы один
    /// пустой гекс» (<see cref="DirectNearest"/>). Off the rules nothing is
    /// fired and the HUD says why. On them the round is wanted at the hex's
    /// middle on the ground - the mortar's bomb comes down on the hex, a gun's
    /// round goes along the axis - or, with a tank on it, at the tank: the
    /// bomb on its roof, the round at its middle.
    /// </summary>
    private void AimCell(Vector2I cell)
    {
        if (_field is null || _fate != Fate.Alive || !_field.InBounds(cell))
            return;
        Vector2I own = CellHere;
        var (axis, cells) = AxisTo(own, cell);
        Other? tank = _other is { } o && OtherCell(o) == cell ? o : null;
        string why = "";
        if (cells <= 0)
            why = "по своему гексу не стреляют";
        else if (axis < 0)
            why = "гекс не на оси огня";
        else if (_profile.Lobs && (cells < Gunnery.LobShortest || cells > Gunnery.LobReach))
            why = $"миномёт бьёт на {Gunnery.LobShortest}–{Gunnery.LobReach} клетки, до цели {cells}";
        else if (!_profile.Lobs && tank is not null && cells < DirectNearest)
            why = "по танку вплотную нельзя: нужен пустой гекс между";
        Vector3 mid = CellWorld(cell);
        Vector3 ground = new(mid.X, ShellGround(mid, out _), mid.Z);
        if (why.Length > 0)
        {
            _aim = null;
            _aimNote = why;
            MarkAt(ground, refused: true);
            GD.Print($"tank3d: no shot at {cell}: {why}");
            return;
        }
        Vector3 want = ground;
        if (tank is not null)
        {
            float tall = tank.Model.Size.Y * tank.Model.PixelsPerUnit;
            if (_profile.Lobs && tank.Hits.Cast(tank.Rig.Position + Vector3.Up * (2.0f * tall + HexWidth), Vector3.Down) is { } roof)
                want = roof.At;
            else
                want = tank.Rig.Position + tank.Up * (0.45f * tall);
        }
        Aim(want, ground, $"{cell}, axis {axis}, {cells} cells");
    }

    /// <summary>A gun's nearest tank target, cells: one empty hex between.</summary>
    private const int DirectNearest = 2;

    /// <summary>Which of the six axes out of <paramref name="from"/>
    /// <paramref name="to"/> lies on, and how many cells along it; -1 and the
    /// cells walked when it lies on none.</summary>
    private (int Axis, int Cells) AxisTo(Vector2I from, Vector2I to)
    {
        if (from == to)
            return (-1, 0);
        foreach (int heading in HexField.EdgeHeadings)
        {
            Vector2I c = from;
            for (int k = 1; k <= 32; k++)
            {
                c = HexField.Step(c, heading);
                if (c == to)
                    return (heading, k);
                if (_field is not null && !_field.InBounds(c))
                    break;
            }
        }
        return (-1, 1);
    }

    /// <summary>The cell the target stands on - where it is, not the cell it
    /// was put on, which a push leaves behind.</summary>
    private Vector2I OtherCell(Other o) => _field?.FlatCellAt(Board(o.Rig.Position)) ?? o.Cell;

    private void Aim(Vector3 want, Vector3 mark, string what)
    {
        _aim = want;
        _aimRound = null;
        _aimNote = "";
        MarkAt(mark);
        GD.Print($"tank3d: aim at {what} {want}, {want.DistanceTo(_rig.Position) / Mathf.Max(CellStep, 1.0f):F1} cells");
    }

    /// <summary>The gun on to <see cref="_aim"/>, by the same inputs the keys
    /// give - the turret (or a casemate's hull) round to its bearing, the
    /// tube to the angle the class's throw wants - and fired once laid. Keys
    /// pressed take it back.</summary>
    private void Laying(float dt, ref float yawIn, ref float elevIn, ref float turnIn)
    {
        if (_aim is not Vector3 at)
            return;
        if (_fate != Fate.Alive || yawIn != 0.0f || elevIn != 0.0f)
        {
            _aim = null;
            _aimNote = "";
            return;
        }
        Vector3 muzzle = _model.Muzzle.GlobalPosition;
        Vector3 bore = _model.Muzzle.GlobalBasis.Z.Normalized();
        Vector3 pivot = _model.Turreted && _model.Turret is not null ? _model.Turret.GlobalPosition : _rig.GlobalPosition;
        Vector3 want = at - pivot, now = bore;
        want.Y = 0.0f;
        now.Y = 0.0f;
        float yaw = want.LengthSquared() > 1e-4f && now.LengthSquared() > 1e-6f
            ? Mathf.Atan2(now.Z * want.X - now.X * want.Z, now.X * want.X + now.Z * want.Z) : 0.0f;
        if (dt > 0.0f)
        {
            if (_model.Turreted)
                yawIn = Mathf.Clamp(yaw / (Mathf.DegToRad(SlewDeg) * dt), -1.0f, 1.0f);
            else
                turnIn = Mathf.Clamp(Mathf.RadToDeg(yaw) / (TurnRate * 0.5f * dt), -1.0f, 1.0f);
        }
        var (speed, fall) = Ballistics;
        float pitch = Mathf.Asin(Mathf.Clamp(bore.Y, -1.0f, 1.0f));
        // The mortar lays by the range and fires the charge for it; a gun has
        // the one charge and lays for the throw.
        float? lay = _profile.Lobs
            ? LobLay(new Vector2(at.X - _rig.Position.X, at.Z - _rig.Position.Z).Length() / Mathf.Max(CellStep, 1.0f), pitch)
            : LayFor(muzzle, at, speed, fall);
        // Out of reach: as far as it throws, which is 45 degrees.
        float elev = (lay ?? Mathf.Pi * 0.25f) - pitch;
        if (dt > 0.0f)
            elevIn = Mathf.Clamp(elev / (Mathf.DegToRad(ElevDeg) * dt), -1.0f, 1.0f);
        bool stopped = (elev > 0.0f && _model.Elevation >= Mathf.DegToRad(_model.ElevMax) - 1e-4f)
                    || (elev < 0.0f && _model.Elevation <= Mathf.DegToRad(_model.ElevMin) + 1e-4f);
        _aimNote = lay is null ? "цель вне досягаемости" : stopped ? "ствол на упоре: не дотянется" : "";
        bool laid = Mathf.Abs(Mathf.RadToDeg(yaw)) < LaidYaw
                    && (Mathf.Abs(Mathf.RadToDeg(elev)) < LaidElev || stopped);
        if (!laid)
            return;
        yawIn = elevIn = turnIn = 0.0f;
        _aim = null;
        if (_profile.Lobs)
            _chargeFor = at;
        string charge = _profile.Lobs && Charge(muzzle, bore, at, fall) is float v ? $", charge {v:F0}/s" : "";
        GD.Print($"tank3d: laid at {Mathf.RadToDeg(pitch):F1} deg (wants {(lay is float l ? Mathf.RadToDeg(l).ToString("F1") : "beyond reach")}){charge}"
                 + (_aimNote.Length > 0 ? $": {_aimNote}" : ""));
        Shoot();
    }

    /// <summary>The mark: a ring on the ground where the round is wanted - in
    /// the hex's middle, as wide as most of it; crossed out, and gone in a
    /// moment, where the rules say no.</summary>
    private void MarkAt(Vector3 at, bool refused = false)
    {
        if (_aimMark is null)
        {
            _aimMark = new MeshInstance3D
            {
                Name = "AimMark",
                Mesh = new PlaneMesh { Size = Vector2.One },
                MaterialOverride = new ShaderMaterial { Shader = MarkShader },
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            AddChild(_aimMark);
        }
        float wide = 0.32f * HexWidth;
        ((ShaderMaterial)_aimMark.MaterialOverride).SetShaderParameter("refused", refused);
        _markRefused = refused ? 0.6f : 0.0f;
        _aimMark.GlobalTransform = new Transform3D(Basis.Identity.Scaled(new Vector3(wide, 1.0f, wide)),
                                                   new Vector3(at.X, Foot(at).Y + 1.0f, at.Z));
        _aimMark.Visible = true;
    }

    /// <summary>The mark in the model's look: a red ring and a dot, inked.</summary>
    private static readonly Shader MarkShader = new()
    {
        Code = @"
shader_type spatial;
render_mode unshaded, cull_disabled, shadows_disabled, depth_draw_never;
uniform bool refused = false;
void fragment() {
    vec2 q = (UV - 0.5) * 2.0;
    float r = length(q);
    vec3 ink = vec3(0.08, 0.03, 0.02), red = vec3(0.92, 0.18, 0.10);
    if (refused) red = vec3(0.55, 0.52, 0.50);
    float a = 0.0;
    vec3 c = red;
    if (r > 0.60 && r < 0.92) { a = 1.0; c = (r < 0.66 || r > 0.86) ? ink : red; }
    else if (!refused && r < 0.16) { a = 1.0; c = r > 0.10 ? ink : red; }
    // No: a cross through the ring.
    float bar = min(abs(q.x - q.y), abs(q.x + q.y)) * 0.7071;
    if (refused && r < 0.92 && bar < 0.09) { a = 1.0; c = bar > 0.05 ? ink : red; }
    if (a < 0.5) discard;
    ALBEDO = c;
}
",
    };

    private void ShellsReset()
    {
        _aim = null;
        _aimRound = null;
        _aimMark?.Hide();
        _shells?.Reset();
        foreach (ShellShade s in _shades)
            s.Hide();
    }
}
