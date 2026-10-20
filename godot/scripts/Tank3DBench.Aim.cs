using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// Which tank is which, and what a shot would do, before it is fired
/// (docs/hud3d-plan.md, stage 4).
///
/// <list type="bullet">
/// <item><b>The outline</b> - a rim of colour round the model, outside its
/// ink (<see cref="Toon.Halo"/>): wide in the own side's colour on the
/// selected tank, thin on a tank under the pointer - the own one unselected, a
/// standing one to take over, or the target in the hostile red. The ring on
/// the ground stays (<c>Tank3DBench.Select</c>); the violet band on the
/// order's cell gives way to the ghost, which now stands there for the whole
/// drive.</item>
/// <item><b>The line of fire</b> - with the pointer on the target: from the
/// muzzle along the axis to the hull, the mortar's as an arc; cut short with a
/// cross where the lane is shut, dashed and grey off the axis.</item>
/// <item><b>The plate</b> - the edge of the target's hex the round comes in
/// through, in the outcome's colour, and a label over the target: лоб, борт,
/// корма or крыша, and пробьёт, рикошет or не пробьёт - off the same
/// <see cref="Gunnery"/> sums the round in flight asks
/// (<c>Tank3DBench.Shell</c>, <c>LevelOn</c>).</item>
/// <item><b>From where.</b> The shot is judged from where it will be fired:
/// the cell the drive ends on while driving, the fixed cell once one is fixed
/// - every class drives first. LT alone may shoot first (classes.md,
/// «Свободный порядок LT»): it is judged from where it stands, and the label
/// adds what the shot would be after the move.</item>
/// </list>
///
/// The right button follows the same reading: with a cell fixed it orders the
/// move and the shot on arrival - LT's fires now and keeps the cell fixed.
/// <c>--hover Q,R</c> on the target's cell holds the pointer on it for a
/// capture; <c>--no-ui</c> shows none of this.
/// </summary>
public sealed partial class Tank3DBench
{
    /// <summary>The rim's width past the ink, screen px: selected, under the
    /// pointer.</summary>
    private const float HaloChosen = 3.0f, HaloHover = 1.5f;

    // red where it gets in: the target under fire, and not the green of the tanks
    private static readonly Color AimPierce = new(0.93f, 0.24f, 0.20f);
    private static readonly Color AimGlance = new(0.97f, 0.74f, 0.24f);
    private static readonly Color AimHeld = new(0.62f, 0.68f, 0.76f);
    private static readonly Color AimOff = new(0.66f, 0.66f, 0.63f);

    private readonly Dictionary<TankModel, Toon.Rim?> _halos = new();
    private bool _overOwn, _overTarget;
    private Parked? _overParked;

    private MeshInstance3D? _aimMesh;
    private Control? _aimInk;
    private Judged? _judged;
    private string _judgedNote = "";

    /// <summary>A shot judged before it is fired: from where, through which
    /// plate, to what end - or why not.</summary>
    private sealed record Judged(Vector2I From, Vector2I Cell, Vector3 Muzzle, Vector3 End, int Axis,
                                 string Face, Gunnery.Plate Plate, int Level, string Why, bool Lob,
                                 int Cells, Vector2I? Stop, Vector3 Tip)
    {
        public bool Fires => Why.Length == 0;
        public bool Pierces => Fires && Level > 0;
        public bool Glances => Fires && Level == 0 && Gunnery.Ricochets(Plate);

        public Color Ink => !Fires ? AimOff : Pierces ? AimPierce : Glances ? AimGlance : AimHeld;

        public string Says
        {
            get
            {
                if (!Fires)
                    return Why;
                string face = Face switch
                {
                    "front" => "лоб",
                    "rear" => "корма",
                    "roof" => "крыша",
                    _ => "борт",
                };
                string what = Pierces ? $"пробьёт ({Level})" : Glances ? "рикошет" : "не пробьёт";
                return $"{face} · {what}";
            }
        }
    }

    private void BuildAim(CanvasLayer layer)
    {
        if (_noUi)
            return;
        _aimMesh = new MeshInstance3D
        {
            Name = "AimPlate",
            Mesh = new ImmediateMesh(),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                RenderPriority = Stage3D.RingOrder + 1,
            },
        };
        AddChild(_aimMesh);
        _aimInk = new Control { Name = "AimInk", MouseFilter = Control.MouseFilterEnum.Ignore };
        _aimInk.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _aimInk.Draw += DrawAim;
        layer.AddChild(_aimInk);
    }

    /// <summary>Every frame, after <see cref="CourseTick"/>: what is under the
    /// pointer, the outlines, the shot judged.</summary>
    private void AimTick()
    {
        if (_noUi)
            return;
        UnderTick();
        HaloTick();
        _judged = null;
        _judgedNote = "";
        if (_aimMesh?.Mesh is ImmediateMesh mesh)
            mesh.ClearSurfaces();
        if (_rules && _chosen && _fate == Fate.Alive && _overTarget && _other is { } o && _field is not null && !RamRunning)
        {
            bool driving = RouteMoving && !_routeRams && _route is { Count: > 0 };
            Vector2I from = driving ? _route![^1]
                : _lockCell is Vector2I fixedAt && !_profile.FreeOrder ? fixedAt : CellHere;
            _judged = Judge(from, o);
            GhostTrack(_judged, o);
            _judgedNote = $"цель {o.Tag} {_judged.Cell}{(from != CellHere ? $" из {from}" : "")}: {_judged.Says}";
            if (_lockCell is Vector2I after && _profile.FreeOrder && !driving)
            {
                Judged then = Judge(after, o);
                _judgedNote += $"; после хода из {after}: {then.Says}";
            }
            if (_aimMesh?.Mesh is ImmediateMesh plate)
                AimGround(plate, _judged);
        }
        _aimInk?.QueueRedraw();
    }

    // --- the outlines -------------------------------------------------------

    /// <summary>The own tank, the target or a standing one under the pointer -
    /// or under <c>--hover</c>.</summary>
    private void UnderTick()
    {
        _overOwn = _overTarget = false;
        _overParked = null;
        if (_hoverFixed is Vector2I held)
        {
            _overTarget = _other is { } t && OtherCell(t) == held;
            _overOwn = held == CellHere;
            _overParked = _parked.Find(p => p.Cell == held);
            return;
        }
        Vector2 mouse = GetViewport().GetMousePosition();
        if (ParkedAt(mouse) is { } standing)
        {
            _overParked = standing;
            return;
        }
        if (PickAt(mouse) is not var (point, onTank, onOwn))
            return;
        _overOwn = onOwn;
        _overTarget = onTank
            || (!onOwn && _other is { } o && _field is not null && _field.FlatCellAt(Board(point)) == OtherCell(o));
    }

    private void HaloTick()
    {
        foreach (TankModel gone in _halos.Keys.Where(m => !IsInstanceValid(m)).ToList())
            _halos.Remove(gone);
        bool alive = _fate == Fate.Alive;
        SetHalo(_model, !alive ? 0.0f : _chosen ? HaloChosen : _overOwn ? HaloHover : 0.0f, SelectionRing.Friendly);
        if (_other is { } o)
            SetHalo(o.Model, _overTarget ? HaloHover : 0.0f, SelectionRing.Hostile);
        foreach (Parked p in _parked)
            SetHalo(p.Model, ReferenceEquals(p, _overParked) ? HaloHover : 0.0f, SelectionRing.Friendly);
    }

    /// <summary>The rim on <paramref name="model"/>, hung on first need.</summary>
    private void SetHalo(TankModel model, float px, Color tint)
    {
        if (!_halos.TryGetValue(model, out Toon.Rim? halo))
        {
            if (px <= 0.0f)
                return;
            _halos[model] = halo = Toon.Halo(model);
        }
        if (halo is null)
            return;
        halo.Set(px, tint);
    }

    // --- the shot -------------------------------------------------------------

    /// <summary>A shot from <paramref name="from"/> at the target: the
    /// right button's gate (<see cref="ShotWhy"/>), a wall on the axis, then
    /// the plate the round meets and what it does there.</summary>
    private Judged Judge(Vector2I from, Other o)
    {
        Vector2I cell = OtherCell(o);
        string why = ShotWhy(from, cell, out int axis, out int cells, out Vector2I? stop);
        Vector3 muzzle = MuzzleAt(from);
        float tall = o.Model.Size.Y * o.Model.PixelsPerUnit;
        Vector3 end = o.Rig.Position + o.Up * (0.45f * tall);
        if (why.Length == 0 && !_profile.Lobs && WallOnAxis(from, axis, cells, passOne: ShootsThrough) is { } wall)
        {
            why = $"снаряд уйдёт в стену {wall.Cell}";
            end = wall.At;
            stop = wall.Cell;
        }
        else if (stop is Vector2I shut)
            end = CellWorld(shut) + Vector3.Up * (0.3f * tall);
        string face = "roof";
        Gunnery.Plate plate = Gunnery.Plate.Roof;
        int level;
        if (_profile.Lobs)
        {
            // the bomb comes down on the roof (classes.md, «HM — Навес»)
            end = o.Rig.Position + o.Up * tall;
            level = Gunnery.Penetration(_profile.Might, o.Profile, plate);
            // on the roof itself, where the bomb comes down (AimCell's cast)
            if (why.Length == 0 && o.Hits.Cast(o.Rig.Position + Vector3.Up * (2.0f * tall + HexWidth), Vector3.Down) is { } roof)
                end = roof.At;
        }
        else
        {
            level = LevelOn(o, end - muzzle, _profile.Might, out face, out plate);
            // the plate itself, where the round from the muzzle meets it
            Vector3 tip = TipAt(from);
            if (why.Length == 0 && o.Hits.Cast(tip, (end - tip).Normalized()) is { } hit)
                end = hit.At;
        }
        return new Judged(from, cell, muzzle, end, axis, face, plate, level, why, _profile.Lobs, cells, stop, TipAt(from));
    }

    /// <summary>Where the line of fire starts from <paramref name="from"/>: over
    /// the hull's middle at the muzzle's height - the tank's where it stands,
    /// the ghost's where the ghost stands, else a turret's height over the
    /// cell. Not the muzzle: a line grown out of the barrel read as part of
    /// the model, and a casemate's barrel points where the hull does.</summary>
    private Vector3 MuzzleAt(Vector2I from)
    {
        if (from == CellHere)
            return OverMiddle(_model, _rig);
        if (GhostOn(from) is { } ghost)
            return OverMiddle(ghost, _ghostRig!);
        return CellMiddle(from) + Vector3.Up * (0.7f * _model.Size.Y * _model.PixelsPerUnit);
    }

    /// <summary>The ghost, if it stands on <paramref name="cell"/>.</summary>
    /// <summary>The muzzle's tip where the shot is fired from: the beads on the
    /// ground start under it.</summary>
    private Vector3 TipAt(Vector2I from) =>
        from == CellHere ? _model.Muzzle.GlobalPosition
        : GhostOn(from) is { } ghost ? ghost.Muzzle.GlobalPosition
        : CellMiddle(from);

    private TankModel? GhostOn(Vector2I cell) =>
        _ghost is not null && _ghostRig is { Visible: true } rig && _field!.FlatCellAt(Board(rig.Position)) == cell
            ? _ghost : null;

    private static Vector3 OverMiddle(TankModel model, Node3D rig) =>
        new(rig.GlobalPosition.X, model.Muzzle.GlobalPosition.Y, rig.GlobalPosition.Z);

    /// <summary>How far the bore is turned off <paramref name="target"/>, rad,
    /// about the turret (or the hull) - <c>Laying</c>'s reading, signed so
    /// that adding it to <see cref="TankModel.Yaw"/> lays the gun.</summary>
    private static float BoreOff(TankModel model, Node3D rig, Vector3 target)
    {
        Vector3 bore = model.Muzzle.GlobalBasis.Z;
        Vector3 pivot = model.Turreted && model.Turret is not null ? model.Turret.GlobalPosition : rig.GlobalPosition;
        Vector3 want = target - pivot, now = bore;
        want.Y = 0.0f;
        now.Y = 0.0f;
        return want.LengthSquared() > 1e-4f && now.LengthSquared() > 1e-6f
            ? Mathf.Atan2(now.Z * want.X - now.X * want.Z, now.X * want.X + now.Z * want.Z) : 0.0f;
    }

    /// <summary>The turret onto the target under the pointer, the rules' way
    /// (at the class's traverse, <c>SlewDeg</c>): the HUD's line of fire then
    /// leaves along the barrel. Only standing, with no shot being laid - the
    /// right button's laying has the turret then - and only when the shot is
    /// judged from here; from a fixed cell it is the ghost's turret that
    /// turns. A casemate's hull does not turn for a look: that is a step.</summary>
    private float TrackIn(float dt)
    {
        if (_judged is not { } shot || _other is not { } o || _aim is not null || RouteMoving
            || !_model.Turreted || _fate != Fate.Alive || dt <= 0.0f || shot.From != CellHere)
            return 0.0f;
        float yaw = BoreOff(_model, _rig, Centre(o));
        return Mathf.Clamp(yaw / (Mathf.DegToRad(SlewDeg) * dt), -1.0f, 1.0f);
    }

    /// <summary>The ghost's turret straight onto the target when the shot is
    /// judged from the ghost's cell - a picture, so at once.</summary>
    private void GhostTrack(Judged shot, Other o)
    {
        if (GhostOn(shot.From) is not { Turreted: true } ghost)
            return;
        ghost.Yaw += BoreOff(ghost, _ghostRig!, Centre(o));
        ghost.Apply();
    }

    private static Vector3 Centre(Other o) =>
        o.Rig.Position + o.Up * (0.45f * o.Model.Size.Y * o.Model.PixelsPerUnit);

    // --- the target on the board --------------------------------------------

    /// <summary><c>--no-target</c>: no target unless asked for.</summary>
    private bool _targetDefault = true;

    /// <summary>The target the bench starts with (HUD stage 4): along the own
    /// hull three hexes out - one of the six axes, two empty hexes between -
    /// broadside on; else the next side round, or four or two out. Dry, on
    /// the board, off the standing tanks, the mines and the woods (a tank
    /// under the crowns is hard to see to aim at).</summary>
    private void StandTarget()
    {
        if (_field is null || _other is not null)
            return;
        Vector2I here = CellHere;
        var sides = new List<int>(HexField.EdgeHeadings);
        int hull = HullSide();
        sides.Sort((a, b) => Turn(hull, a).CompareTo(Turn(hull, b)));
        HashSet<Vector2I> taken = Barred();
        // a hex the gun reaches first: behind a rise it would greet the bench
        // with a refusal; any dry one after that
        foreach (bool clear in new[] { true, false })
        foreach (int cells in new[] { 3, 4, 2 })
            foreach (int side in sides)
            {
                Vector2I c = here;
                for (int k = 0; k < cells; k++)
                    c = HexField.Step(c, side);
                if (!_field.InBounds(c) || _field.IsWater(c) || taken.Contains(c)
                    || _field.CoverAt(c) is Cover.Minefield or Cover.Forest
                    || (clear && ShotWhy(here, c, out _, out _, out _).Length > 0))
                    continue;
                PlaceOther(c, HeadingFor(c, side) + 90.0f);
                GD.Print($"tank3d: target {_otherTag} stands on {c}, {cells} out along {side}");
                return;
            }
    }

    private static int Turn(int from, int to) => Mathf.Min(Mathf.PosMod(to - from, 360), Mathf.PosMod(from - to, 360));

    // --- the line of fire and the plate -------------------------------------

    /// <summary>
    /// The line of fire (HUD stage 4): beads in the air from the muzzle's tip
    /// to where the round meets the hull - cast on the target's plates - or
    /// to where the lane is shut; they run toward the target with a soft wave
    /// of light, in the outcome's colour: red where the round gets in, amber
    /// off a flank, steel where it holds, grey where it stops short. Tried
    /// before it and turned down: a solid arrow (read as part of the model),
    /// a dash over the board, beads on the ground along the axis (the shot
    /// did not read as a flight). Off with <c>--no-fire-axis</c> or the panel:
    /// then only the reticle, the plate and the label.
    /// </summary>
    private bool _fireAxis = true;

    /// <summary>Beads a hex, how many hexes a second they run, how fast the
    /// light goes along them.</summary>
    private const float BeadsAHex = 6.0f, BeadRun = 0.55f, BeadWave = 5.0f;

    private void AimGround(ImmediateMesh mesh, Judged shot)
    {
        if (_tile is null || _field is null)
            return;
        bool shut = shot.Stop is not null;
        if (!shut && !shot.Fires)
            return;
        Vector2[] corner = Corners();
        mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        if (shut)
            ShutHex(mesh, shot, corner);
        if (shot.Fires)
        {
            Vector2I cell = shot.Cell;
            Vector2 mid = CellFlat(cell);
            if (shot.Lob)
            {
                var tint = new Color(shot.Ink, 0.35f);
                for (int i = 0; i < 6; i++)
                    Tri(mesh, tint, cell, mid, mid + corner[i] * 0.95f, mid + corner[(i + 1) % 6] * 0.95f);
            }
            else
            {
                int e = EdgeOf(cell, HexField.Reverse(shot.Axis), corner);
                Vector2 a = corner[e], b = corner[(e + 1) % 6];
                Quad(mesh, SelectionRing.Under, cell, mid + a * 1.03f, mid + b * 1.03f, mid + b * 0.76f, mid + a * 0.76f);
                Quad(mesh, shot.Ink, cell, mid + a * 0.99f, mid + b * 0.99f, mid + b * 0.80f, mid + a * 0.80f);
            }
        }
        mesh.SurfaceEnd();
    }

    /// <summary>The hex that shuts the lane, tinted on the ground; the beads
    /// themselves are in the air (<see cref="DrawBeads"/>).</summary>
    private void ShutHex(ImmediateMesh mesh, Judged shot, Vector2[] corner)
    {
        if (shot.Stop is not Vector2I shut)
            return;
        Vector2 mid = CellFlat(shut);
        var tint = new Color(AimOff, 0.30f);
        for (int i = 0; i < 6; i++)
            Tri(mesh, tint, shut, mid, mid + corner[i] * 0.95f, mid + corner[(i + 1) % 6] * 0.95f);
    }

    /// <summary>
    /// The round's flight as beads in the air: from the muzzle's tip to where
    /// it meets the hull (<see cref="Judged.End"/>, cast on the target's
    /// plates) - the mortar's along its arc - or to where the lane is shut.
    /// They run toward the target with a soft wave of light, coming in at the
    /// muzzle and going at the hull, in the outcome's colour. Spaced in the
    /// world, sized on the screen, so a long shot has more beads and not
    /// bigger ones.
    /// </summary>
    private void DrawBeads(Control ink, Judged shot, float hex)
    {
        var path = new List<Vector3>();
        int steps = shot.Lob ? 32 : 1;
        float rise = shot.Lob ? shot.Tip.DistanceTo(shot.End) * 0.35f : 0.0f;
        for (int k = 0; k <= steps; k++)
        {
            float u = k / (float)steps;
            path.Add(shot.Tip.Lerp(shot.End, u) + Vector3.Up * (4.0f * rise * u * (1.0f - u)));
        }
        float total = 0.0f;
        for (int k = 0; k + 1 < path.Count; k++)
            total += path[k].DistanceTo(path[k + 1]);
        float gap = CellStep / BeadsAHex;
        if (total <= gap * 0.5f)
            return;
        float t = (float)(Time.GetTicksMsec() / 1000.0);
        float run = Mathf.PosMod(t * BeadRun * CellStep, gap);
        float r = Mathf.Clamp(hex * 0.045f, 2.5f, 6.0f);
        Color c = shot.Fires ? shot.Ink : AimOff;
        // the first bead a little out of the bore, the last on the plate
        float start = gap * 0.3f;
        for (float s = start + run; s <= total; s += gap)
        {
            float wave = 0.5f + 0.5f * Mathf.Sin(t * BeadWave - s / gap * 0.9f);
            float edge = Mathf.Clamp((s - start) / gap, 0.0f, 1.0f) * Mathf.Clamp((total - s) / gap + 0.3f, 0.0f, 1.0f);
            Vector2 p = _camera.UnprojectPosition(Along3(path, s));
            float size = r * (0.8f + 0.35f * wave) * (0.4f + 0.6f * edge);
            ink.DrawCircle(p, size + 1.6f, new Color(0.0f, 0.0f, 0.0f, 0.5f * edge));
            ink.DrawCircle(p, size, new Color(c.Lerp(Colors.White, 0.45f * wave), (0.55f + 0.45f * wave) * edge));
        }
    }

    private static Vector3 Along3(List<Vector3> path, float s)
    {
        for (int k = 0; k + 1 < path.Count; k++)
        {
            float len = path[k].DistanceTo(path[k + 1]);
            if (s <= len && len > 0.0f)
                return path[k].Lerp(path[k + 1], s / len);
            s -= len;
        }
        return path[^1];
    }

    private void Disc(ImmediateMesh mesh, Vector2I cell, Vector2 at, float r, Color ink)
    {
        const int sides = 10;
        for (int i = 0; i < sides; i++)
        {
            float u = Mathf.Tau * i / sides, v = Mathf.Tau * (i + 1) / sides;
            Tri(mesh, ink, cell, at, at + new Vector2(Mathf.Cos(u), Mathf.Sin(u)) * r,
                at + new Vector2(Mathf.Cos(v), Mathf.Sin(v)) * r);
        }
    }

    // --- the screen: the reticle, the cross, the label ----------------------

    /// <summary>Over the board: a reticle round the target in the outcome's
    /// colour, or a cross where the lane is shut, and the label.</summary>
    private void DrawAim()
    {
        if (_aimInk is not { } ink || _judged is not { } shot)
            return;
        Font font = ThemeDB.FallbackFont;
        float hex = HexScreen();
        int size = Mathf.RoundToInt(Mathf.Clamp(hex * 0.16f, 9.0f, 16.0f) * 1.15f);
        Vector2 end = _camera.UnprojectPosition(shot.End);
        if (_fireAxis && shot.Axis >= 0)
            DrawBeads(ink, shot, hex);
        float r = size * 0.55f;
        if (shot.Fires)
            Reticle(ink, end, Mathf.Max(size * 1.2f, hex * 0.32f), shot.Ink);
        else if (shot.Axis >= 0)
        {
            // the lane shut: a cross where the round would stop
            var dark = new Color(0.0f, 0.0f, 0.0f, 0.55f);
            foreach ((Color c, float w) in new[] { (dark, 5.5f), (new Color(0.92f, 0.92f, 0.9f), 3.0f) })
            {
                ink.DrawLine(end - new Vector2(r, r), end + new Vector2(r, r), c, w, true);
                ink.DrawLine(end - new Vector2(r, -r), end + new Vector2(r, -r), c, w, true);
            }
        }
        Vector2 over = _camera.UnprojectPosition(_other!.Rig.Position + _other.Up * (_other.Model.Size.Y * _other.Model.PixelsPerUnit * 1.2f));
        // kept under the HUD's three lines
        over.Y = Mathf.Max(over.Y, 100.0f + size * 2.0f);
        Color letters = shot.Pierces ? Colors.White : new Color(0.12f, 0.12f, 0.12f);
        Pill(ink, font, over + new Vector2(0, -size), shot.Says, shot.Ink, letters, size);
        int semi = _judgedNote.IndexOf("; после хода", System.StringComparison.Ordinal);
        if (semi >= 0)
            Pill(ink, font, over + new Vector2(0, size * 0.8f), _judgedNote[(semi + 2)..], new Color(0.92f, 0.95f, 0.98f),
                 new Color(0.05f, 0.27f, 0.49f), size);
    }

    /// <summary>A ring with four ticks pointing in, in the outcome's colour on
    /// a dark one: where the round goes in.</summary>
    private static void Reticle(Control ink, Vector2 at, float r, Color c)
    {
        var dark = new Color(0.0f, 0.0f, 0.0f, 0.5f);
        foreach ((Color col, float w) in new[] { (dark, 5.0f), (c, 2.5f) })
        {
            ink.DrawArc(at, r, 0.0f, Mathf.Tau, 40, col, w, true);
            for (int i = 0; i < 4; i++)
            {
                Vector2 d = Vector2.Right.Rotated(Mathf.Pi * 0.5f * i + Mathf.Pi * 0.25f);
                ink.DrawLine(at + d * (r - r * 0.35f), at + d * (r + r * 0.25f), col, w, true);
            }
        }
    }
}
