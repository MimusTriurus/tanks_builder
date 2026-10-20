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

    private static readonly Color AimPierce = new(0.36f, 0.84f, 0.38f);
    private static readonly Color AimGlance = new(0.97f, 0.74f, 0.24f);
    private static readonly Color AimHeld = new(0.90f, 0.36f, 0.30f);
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
                                 string Face, Gunnery.Plate Plate, int Level, string Why, bool Lob)
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
            _judgedNote = $"цель {o.Tag} {_judged.Cell}{(from != CellHere ? $" из {from}" : "")}: {_judged.Says}";
            if (_lockCell is Vector2I after && _profile.FreeOrder && !driving)
            {
                Judged then = Judge(after, o);
                _judgedNote += $"; после хода из {after}: {then.Says}";
            }
            if (_aimMesh?.Mesh is ImmediateMesh plate)
                PlateEdge(plate, _judged);
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
        }
        else
            level = LevelOn(o, end - muzzle, _profile.Might, out face, out plate);
        return new Judged(from, cell, muzzle, end, axis, face, plate, level, why, _profile.Lobs);
    }

    /// <summary>Where the gun is when it fires from <paramref name="from"/>:
    /// the muzzle where the tank stands; the ghost's where the ghost stands;
    /// else a turret's height over the cell.</summary>
    private Vector3 MuzzleAt(Vector2I from)
    {
        if (from == CellHere)
            return _model.Muzzle.GlobalPosition;
        if (_ghost is not null && _ghostRig is { Visible: true } rig && _field!.FlatCellAt(Board(rig.Position)) == from)
            return _ghost.Muzzle.GlobalPosition;
        return CellMiddle(from) + Vector3.Up * (0.7f * _model.Size.Y * _model.PixelsPerUnit);
    }

    /// <summary>The edge of the target's hex the round comes in through, in
    /// the outcome's colour; the mortar's - the whole hex, faint.</summary>
    private void PlateEdge(ImmediateMesh mesh, Judged shot)
    {
        if (!shot.Fires || _tile is null)
            return;
        Vector2I cell = shot.Cell;
        Vector2[] corner = Corners();
        Vector2 mid = CellFlat(cell);
        mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
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
        mesh.SurfaceEnd();
    }

    private void DrawAim()
    {
        if (_aimInk is not { } ink || _judged is not { } shot)
            return;
        Font font = ThemeDB.FallbackFont;
        int size = Mathf.RoundToInt(Mathf.Clamp(HexScreen() * 0.16f, 9.0f, 16.0f) * 1.15f);
        Color under = new(0.0f, 0.0f, 0.0f, 0.55f);
        Vector2 a = _camera.UnprojectPosition(shot.Muzzle), b = _camera.UnprojectPosition(shot.End);
        var line = new List<Vector2>();
        if (shot.Lob)
        {
            // the bomb's arc, a third of the throw high over the middle
            float rise = shot.Muzzle.DistanceTo(shot.End) * 0.35f;
            for (int k = 0; k <= 24; k++)
            {
                float t = k / 24.0f;
                line.Add(_camera.UnprojectPosition(shot.Muzzle.Lerp(shot.End, t) + Vector3.Up * (4.0f * rise * t * (1.0f - t))));
            }
        }
        else
        {
            line.Add(a);
            line.Add(b);
        }
        bool offAxis = shot.Axis < 0;
        if (offAxis)
            for (int k = 0; k + 1 < line.Count; k++)
                ink.DrawDashedLine(line[k], line[k + 1], shot.Ink, 2.0f, 8.0f, true, true);
        else
        {
            ink.DrawPolyline(line.ToArray(), under, 6.0f, true);
            ink.DrawPolyline(line.ToArray(), shot.Ink, 2.5f, true);
        }
        Vector2 tip = line[^1], d = (line[^1] - line[^2]).Normalized(), n = d.Orthogonal();
        float r = size * 0.55f;
        if (shot.Fires)
        {
            Vector2[] head = { tip, tip - d * r * 1.6f + n * r * 0.8f, tip - d * r * 1.6f - n * r * 0.8f };
            ink.DrawColoredPolygon(head, shot.Ink);
            ink.DrawPolyline(new[] { head[0], head[1], head[2], head[0] }, under, 1.5f, true);
        }
        else if (!offAxis)
        {
            // the lane shut: a cross where the round would stop
            ink.DrawLine(tip - new Vector2(r, r), tip + new Vector2(r, r), AimHeld, 3.0f, true);
            ink.DrawLine(tip - new Vector2(r, -r), tip + new Vector2(r, -r), AimHeld, 3.0f, true);
        }
        Vector2 over = _camera.UnprojectPosition(_other!.Rig.Position + _other.Up * (_other.Model.Size.Y * _other.Model.PixelsPerUnit * 1.2f));
        // kept under the HUD's three lines
        over.Y = Mathf.Max(over.Y, 100.0f + size * 2.0f);
        Color letters = shot.Fires ? new Color(0.06f, 0.10f, 0.06f) : new Color(0.15f, 0.15f, 0.15f);
        Pill(ink, font, over + new Vector2(0, -size), shot.Says, shot.Ink, letters, size);
        int semi = _judgedNote.IndexOf("; после хода", System.StringComparison.Ordinal);
        if (semi >= 0)
            Pill(ink, font, over + new Vector2(0, size * 0.8f), _judgedNote[(semi + 2)..], new Color(0.92f, 0.95f, 0.98f),
                 new Color(0.05f, 0.27f, 0.49f), size);
    }
}
