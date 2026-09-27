using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The bench's effects on the 3D tank - the same classes, raised with the
/// arguments <see cref="TankBench"/> and <see cref="Stage3D"/> raise them with.
///
/// <b>Two kinds, two hosts.</b>
/// <list type="bullet">
/// <item><b>Node3D effects</b> (<see cref="ProcKick"/>, <see cref="ProcSpall"/>,
/// <see cref="ProcSlam"/>, <see cref="ProcBall"/>, <see cref="SheetBlast"/>) live
/// in this scene's world, which is the space they were written for. Each is
/// pooled, built once, and ticked here because none of them ticks itself.</item>
/// <item><b>Node2D effects</b> (<see cref="ProcFire"/>, both
/// <see cref="ProcSmoke"/>s, <see cref="ProcFume"/>, <see cref="ProcFlash"/>,
/// <see cref="ProcPierce"/>, the hit burst and dust layers) are children of a
/// <see cref="TankSprite"/> and read everything off it. So there is one: the
/// sprite set the model copies, in a render target, with its own pictures made
/// invisible - the hull by the material, the rest by their flags - and its
/// clocks driven from the 3D tank. The model <i>is</i> that sprite's geometry,
/// so ports, bore and depth cut all land on it.</item>
/// </list>
///
/// <b>Where a card stands is the one new rule.</b> An effect card is upright
/// and depth-tested against an opaque model, so a card seated at the tank's
/// centre would be hidden by its near half - a spray off a front plate turned to
/// the camera would go behind that plate. Every card is therefore slid along the
/// view ray, which moves nothing on screen, until its plane is just in front of
/// what makes the effect: the muzzle, the engine port, the struck plate. The
/// model's own depth then hides exactly what stands in front of that.
/// </summary>
public sealed partial class Tank3DBench
{
    // --- 3D effects -------------------------------------------------------

    private readonly List<ProcKick> _kicks = new();
    private readonly List<ProcKick> _drifts = new();
    private readonly List<ProcSpall> _spalls = new();
    private readonly List<ProcSlam> _slams = new();
    private readonly List<ProcBall> _balls = new();
    private readonly List<SheetBlast> _booms = new();
    private readonly List<PitArt> _pits = new();
    private int _nextKick, _nextDrift, _nextSpall, _nextSlam, _nextBall, _nextBoom, _nextPit;
    private const int Pool = 6;
    private const int DriftPool = 48;

    /// <summary>Calibre of the rounds this bench throws - the bench's default,
    /// <see cref="TankTick.Calibre"/> 1.</summary>
    private static float Might => Ordnance.At(1);

    /// <summary>World units in front of its source a card stands.</summary>
    private const float Margin = 4.0f;

    // --- the sprite that carries the 2D effects ----------------------------

    private sealed class Card
    {
        public required SubViewport Paint;
        public required Node2D Holder;
        public required MeshInstance3D Quad;
    }

    /// <summary>Render-target side in board px, and how many target pixels to
    /// a board px - two, so the card is not magnified soft at the default zoom.
    /// </summary>
    private const int CardSize = 512;
    private const int CardZoom = 2;

    private TankSprite? _sprite;
    private Card? _rear, _front, _hit, _glow;
    private readonly List<CanvasItem> _painted = new();

    private readonly ExhaustLoop _exhaust = new();
    private readonly BurnLoop _burn = new();
    private readonly Wreck _wreck = new();
    private readonly HitLoop _hitLoop = new();
    private readonly CameraShake _shake = new();
    private int _shotFrame = -1;
    private bool _burning;

    /// <summary>The struck point in the hull's own frame, so the burst follows
    /// the hull as it rocks, and whether that plate faces away.</summary>
    private Vector3 _hitLocal;
    private bool _hitBehind;

    /// <summary>The sprite's anchor (its <c>spin_pivot</c>) and first engine
    /// port, in the model's <c>Tank</c> frame.</summary>
    private Vector3 _anchorTank, _portTank;
    private TriangleMesh? _hullHits;
    private MovementProfile _profile = MovementProfile.Light;

    private readonly List<(StandardMaterial3D Mat, Color Albedo)> _paint = new();

    private static readonly Shader CardShader = new()
    {
        Code = @"
shader_type spatial;
render_mode unshaded, blend_premul_alpha, depth_draw_never, cull_disabled;
uniform sampler2D picture : source_color, filter_linear;
void fragment() {
    vec4 c = texture(picture, UV);
    ALBEDO = c.rgb;
    ALPHA = c.a;
}",
    };

    /// <summary>
    /// The same, adding light and nothing else - for <see cref="ProcPierce"/>,
    /// which redraws the hull frame additively to use its alpha as a mask. Over
    /// the sprite's own hull that alpha was already 1; over an empty target it
    /// is the whole silhouette, and a premultiplied card draws it black.
    /// </summary>
    private static readonly Shader GlowShader = new()
    {
        Code = @"
shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled;
uniform sampler2D picture : source_color, filter_linear;
void fragment() {
    ALBEDO = texture(picture, UV).rgb;
}",
    };

    /// <summary>What the sprite's own pictures are drawn with instead of its
    /// char shader: nothing. The hull goes through the sprite's material and so
    /// do the scars and the tossed turret, so this one line hides all three and
    /// leaves every effect, each on a material of its own, untouched.</summary>
    private static readonly Shader Nothing = new()
    {
        Code = "shader_type canvas_item;\nvoid fragment() { COLOR = vec4(0.0); }",
    };

    // --- setup ------------------------------------------------------------

    private void BuildEffects()
    {
        foreach (MovementProfile p in new[] { MovementProfile.Light, MovementProfile.Medium,
                                              MovementProfile.Heavy, MovementProfile.Destroyer,
                                              MovementProfile.Mortar })
            if (p.Tag == _spriteTag)
                _profile = p;
        if (_profile.Turreted != _model.Turreted)
            GD.Print($"tank3d: {_modelTag} is {(_model.Turreted ? "turreted" : "a casemate")} but "
                     + $"moves as the {_profile.Tag} class, which is {(_profile.Turreted ? "turreted" : "a casemate")}"
                     + " - the class comes from --sprites");
        foreach (MeshInstance3D mesh in Meshes(_model))
            for (int s = 0; s < mesh.Mesh.GetSurfaceCount(); s++)
                if (mesh.Mesh.SurfaceGetMaterial(s) is StandardMaterial3D m
                    && !_paint.Exists(x => x.Mat == m))
                    _paint.Add((m, m.AlbedoColor));
        if (_model.Hull is MeshInstance3D hull)
            _hullHits = hull.Mesh.GenerateTriangleMesh();

        if (_atlas is null)
        {
            _note = "no sprite set - no effects";
            return;
        }
        ReadFrame();
        _exhaust.Phases = _atlas.ExhaustPhases;
        _exhaust.TopSpeed = _profile.TopSpeed;
        _burn.Phases = _atlas.BurnPhases;

        _rear = MakeCard("Rear");
        _front = MakeCard("Front");
        _hit = MakeCard("Hit");
        _glow = MakeCard("Glow", GlowShader);
        _sprite = new TankSprite
        {
            Atlas = _atlas,
            Name = "Effects",
            ShowTurret = false,
            ShowTracks = false,
            ShowShadow = false,
            ProceduralExhaust = true,
            ProceduralSmoke = true,
            ProceduralFire = true,
            Source = FlashSource.Built,
        };
        _rear.Holder.AddChild(_sprite);
        _sprite.Material = new ShaderMaterial { Shader = Nothing };
        // Out of the sprite and into the card that stands where each one is
        // made. Each still reads the sprite through its Tank field and draws in
        // its own local frame, so a holder at the sprite's place draws it the same.
        foreach (Node child in _sprite.GetChildren())
        {
            string? layer = child is EffectLayer e ? e.Layer : null;
            Card? to = child switch
            {
                ProcFume or ProcFlash => _front,
                ProcPierce => _glow,
                EffectLayer when layer is "smoke" or "flash" => _front,
                EffectLayer when layer == AtlasSet.DustName || layer == AtlasSet.BurstName => _hit,
                _ => null,
            };
            if (to is null)
                continue;
            _sprite.RemoveChild(child);
            to.Holder.AddChild(child);
        }
        _painted.Add(_sprite);
        foreach (Card c in new[] { _rear, _front, _hit, _glow })
            foreach (Node n in c.Holder.GetChildren())
                if (n is CanvasItem item && item != _sprite)
                    _painted.Add(item);
        foreach (Node n in _sprite.GetChildren())
            if (n is CanvasItem item)
                _painted.Add(item);
    }

    private static IEnumerable<MeshInstance3D> Meshes(Node root)
    {
        foreach (Node n in root.GetChildren())
        {
            if (n is MeshInstance3D m && m.Mesh is not null)
                yield return m;
            foreach (MeshInstance3D d in Meshes(n))
                yield return d;
        }
    }

    /// <summary>
    /// Where the sprite's anchor and ports are on the model. Both are in the
    /// Blender frame of the tank the sprites were rendered from (Z up, -Y the
    /// front); the model is the same geometry exported as glTF with its ground
    /// at the origin, so a point is <c>(x, z - ground, -y)</c>. The ground is
    /// read off the sprite itself: the hex under the tank is drawn at the anchor's
    /// foot, <c>GroundOffset</c> below it at the render's elevation.
    /// </summary>
    private void ReadFrame()
    {
        Vector3 pivot = Vector3.Zero;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(
                $"{AssetRoot.Sprites}/{_spriteTag}/hull_atlas.json"));
            JsonElement p = doc.RootElement.GetProperty("spin_pivot");
            pivot = new Vector3(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle());
        }
        catch (Exception e)
        {
            GD.Print($"tank3d: spin_pivot: {e.Message}");
        }
        float upp = (float)_atlas!.UnitsPerPixel;
        float height = _atlas.GroundOffset.Y * upp / Mathf.Cos(Mathf.DegToRad((float)_atlas.Elevation));
        float ground = pivot.Z - height;
        _anchorTank = Canon(pivot, ground);
        _portTank = _atlas.Ports.Count > 0 ? Canon(_atlas.Ports[0].Point, ground)
                                           : _model.Tank.ToLocal(_model.Exhausts[0].GlobalPosition);
        GD.Print($"tank3d: sprite anchor {_anchorTank} (ground z {ground:F4}), port {_portTank}");

        Vector3 Canon(Vector3 b, float g) => new(b.X, b.Z - g, -b.Y);
    }

    private Card MakeCard(string name, Shader? shader = null)
    {
        var paint = new SubViewport
        {
            Name = name,
            Size = new Vector2I(CardSize * CardZoom, CardSize * CardZoom),
            TransparentBg = true,
            Disable3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            RenderTargetClearMode = SubViewport.ClearMode.Always,
        };
        AddChild(paint);
        var holder = new Node2D
        {
            Position = Vector2.One * CardSize * CardZoom * 0.5f,
            Scale = Vector2.One * CardZoom,
        };
        paint.AddChild(holder);
        var mat = new ShaderMaterial { Shader = shader ?? CardShader, RenderPriority = Stage3D.StandOrder };
        mat.SetShaderParameter("picture", paint.GetTexture());
        // Upright, facing +Z like every card on the stage; a target pixel
        // (u, w) lands at (u - half, (half - w) / rise) so it is drawn exactly
        // one board px from its neighbour on screen.
        var quad = new MeshInstance3D
        {
            Name = name + "Card",
            Mesh = new QuadMesh { Size = new Vector2(CardSize, CardSize / RiseFactor) },
            MaterialOverride = mat,
            SortingUseAabbCenter = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(quad);
        return new Card { Paint = paint, Holder = holder, Quad = quad };
    }

    // --- the board's terms --------------------------------------------------

    /// <summary>A ground point (Y = 0) as the harness would pass it: the flat
    /// board's row is world depth times the squash.</summary>
    private Vector2 Board(Vector3 w) => new(w.X, w.Z * Squash);

    /// <summary>A world offset as screen px, y down - what the board calls a
    /// snout or a plate.</summary>
    private Vector2 Drawn(Vector3 d) => new(d.X, d.Z * Squash - d.Y * RiseFactor);

    /// <summary>The atlas heading of a world direction on the ground: 0 screen
    /// right, 90 up the screen (-Z).</summary>
    private static float HeadingOf(Vector3 d) => Mathf.RadToDeg(Mathf.Atan2(-d.Z, d.X));

    /// <summary><see cref="AtlasSet.GroundDirection"/> of a world direction -
    /// unnormalised, as every effect that takes one expects.</summary>
    private Vector2 Along(Vector3 d) => _atlas!.GroundDirection(HeadingOf(d));

    private static readonly Vector3 Up = Vector3.Up;

    /// <summary>Toward the camera along its view - the one direction a card can
    /// move without moving on screen.</summary>
    private Vector3 View => new(0.0f, Squash, RiseFactor);

    /// <summary>Slide a node along the view ray until it stands at depth
    /// <paramref name="z"/>.</summary>
    private void StandAt(Node3D node, float z)
    {
        float t = (z - node.Position.Z) / RiseFactor;
        node.Position += View * t;
    }

    private static T Next<T>(List<T> pool, ref int next, int size, Func<T> make)
    {
        while (pool.Count < size)
            pool.Add(make());
        T item = pool[next % size];
        next = (next + 1) % size;
        return item;
    }

    // --- events -------------------------------------------------------------

    private void FxShot()
    {
        if (_atlas is null)
            return;
        _shotFrame = 0;
        Vector3 muzzle = _model.Muzzle.GlobalPosition;
        Vector3 foot = new(muzzle.X, 0.0f, muzzle.Z);
        Vector3 bore = _model.Muzzle.GlobalBasis.Z;
        ProcKick kick = Next(_kicks, ref _nextKick, Pool, () =>
        {
            var made = new ProcKick();
            AddChild(made);
            made.Build(HexWidth, Squash, RiseFactor);
            return made;
        });
        // Stage3D.Raise, minus the bench's panel hook.
        kick.Might = 1.0f;
        kick.Might *= Might;
        kick.Order = Stage3D.StandOrder;
        kick.Dress(ProcKick.Cloud.Muzzle);
        // Seated under the muzzle rather than at the tank's middle - see the
        // class note: the middle is behind the near half of an opaque hull.
        kick.Sit(Board(foot), 0.0f, Squash, RiseFactor);
        kick.Aim(Along(new Vector3(bore.X, 0.0f, bore.Z)), Drawn(muzzle - foot));
        kick.Fire();
    }

    /// <summary>
    /// Where a round travelling <paramref name="travel"/> (hull frame) meets the
    /// hull, and the plate's normal there - by a ray at the hull mesh, because
    /// the plates are sloped and a box would put a front-plate hit in the air.
    /// </summary>
    private (Vector3 At, Vector3 Normal) Strike(Vector3 travel)
    {
        var hull = _model.Hull;
        Aabb box = hull is MeshInstance3D mi ? mi.GetAabb() : new Aabb(-Vector3.One * 0.4f, Vector3.One * 0.8f);
        Vector3 aim = box.GetCenter() + new Vector3(0.0f, box.Size.Y * 0.08f, 0.0f);
        Vector3 from = aim - travel * 2.0f;
        if (_hullHits is not null)
        {
            var hit = _hullHits.IntersectRay(from, travel);
            if (hit.Count > 0 && hit.ContainsKey("position"))
            {
                Vector3 n = ((Vector3)hit["normal"]).Normalized();
                if (n.Dot(travel) > 0.0f)
                    n = -n;
                return ((Vector3)hit["position"], n);
            }
        }
        Vector3 half = box.Size * 0.5f;
        Vector3 at = aim - travel * new Vector3(half.X, 0, half.Z).Dot(travel.Abs());
        return (at, -travel);
    }

    private void FxHit(int side, Vector3 travel, bool pierce)
    {
        if (_atlas is null)
            return;
        // A little off the plate's normal, so a glancing round has somewhere to
        // glance to, and a little downward, as a round arriving from range does.
        float skew = side % 2 == 0 ? 25.0f : -25.0f;
        Vector3 u = travel.Rotated(Vector3.Up, Mathf.DegToRad(skew));
        u = (u + new Vector3(0.0f, -0.12f, 0.0f)).Normalized();
        (Vector3 local, Vector3 normalLocal) = Strike(u);
        Node3D hull = _model.Hull;
        Vector3 at = hull.ToGlobal(local);
        Vector3 n = (hull.GlobalBasis * normalLocal).Normalized();
        Vector3 uw = (hull.GlobalBasis * u).Normalized();
        bool behind = n.Z <= 0.0f;
        Vector3 foot = new(at.X, 0.0f, at.Z);
        if (pierce)
        {
            _hitLocal = local;
            _hitBehind = behind;
            _hitLoop.Strike(Sides[side], 0.0f, 0.0f, 1.0f, true);
            return;
        }
        Vector3 r = uw - 2.0f * uw.Dot(n) * n;
        ProcSpall spall = Next(_spalls, ref _nextSpall, Pool, () =>
        {
            var made = new ProcSpall();
            AddChild(made);
            made.Build(HexWidth, Squash, RiseFactor);
            return made;
        });
        spall.Might = 1.0f;
        spall.Blame(ProcSpall.Cause.Round);
        spall.Might *= Might;
        spall.Order = Stage3D.StandOrder;
        spall.Sit(Board(foot), 0.0f, Squash, RiseFactor, behind);
        Vector3 flat = new(r.X, 0.0f, r.Z);
        spall.Aim(flat.LengthSquared() < 1e-4f ? Vehicle.Spent : Along(flat), Drawn(at - foot));
        spall.Fire();
    }

    /// <summary>An HE round bursting on a plate - <see cref="ProcSlam"/> on
    /// armour, from the side named.</summary>
    private void FxHe(int side, Vector3 travel)
    {
        if (_atlas is null)
            return;
        (Vector3 local, Vector3 normalLocal) = Strike(travel);
        Node3D hull = _model.Hull;
        Vector3 at = hull.ToGlobal(local);
        Vector3 n = (hull.GlobalBasis * normalLocal).Normalized();
        Vector3 foot = new(at.X, 0.0f, at.Z);
        ProcSlam slam = Next(_slams, ref _nextSlam, Pool, () =>
        {
            var made = new ProcSlam();
            AddChild(made);
            made.Build(HexWidth, Squash, RiseFactor);
            return made;
        });
        slam.Might = 1.0f;
        slam.Might *= Might;
        slam.Face = ProcSlam.Surface.Armour;
        slam.Order = Stage3D.StandOrder;
        slam.Lit = Stage3D.StandOrder;
        slam.Sit(Board(foot), 0.0f, Squash, RiseFactor, n.Z <= 0.0f);
        slam.Aim(Along(new Vector3(n.X, 0.0f, n.Z)), Drawn(at - foot));
        slam.Fire();
    }

    /// <summary>A round landing in the ground beside the tank: the board's
    /// <see cref="Stage3D.Boom"/>, burst and crater.</summary>
    private void FxGround()
    {
        if (_atlas is null)
            return;
        Vector3 spot = _rig.Position + new Vector3(0.55f, 0.0f, 0.45f) * HexWidth;
        SheetBlast blast = Next(_booms, ref _nextBoom, Pool, () =>
        {
            var made = new SheetBlast();
            AddChild(made);
            made.Build(HexWidth, Squash, RiseFactor);
            return made;
        });
        blast.Might = 1.0f;
        blast.Might *= Might;
        blast.Sit(Board(spot), 0.0f, Squash, RiseFactor);
        blast.Fire();
        var pits = new Craters();
        PitArt pit = Next(_pits, ref _nextPit, Pool, () =>
        {
            var made = new PitArt();
            AddChild(made);
            made.Build(Squash, RiseFactor);
            return made;
        });
        Vector2 at = Board(spot);
        pit.Show(at, 0.0f, pits.Wide * Might * HexWidth, pits.Ink,
                 Mathf.Abs(at.X * 0.37f + at.Y * 0.71f) % 64.0f, Squash, RiseFactor);
        _shake.Blast(_profile.ShotShake * 0.6);
    }

    /// <summary>A fireball on the tank - the death, or (ungrounded and small)
    /// the knock-out flash: <see cref="Stage3D.Detonate"/> and
    /// <see cref="Stage3D.Flash"/>.</summary>
    private void Fireball(float might, bool grounded)
    {
        Vector3 foot = _rig.Position;
        ProcBall ball = Next(_balls, ref _nextBall, Pool, () =>
        {
            var made = new ProcBall();
            AddChild(made);
            made.Build(HexWidth, Squash, RiseFactor);
            return made;
        });
        ball.Might = 1.0f;
        ball.Grounded = grounded;
        ball.Might *= might;
        ball.Sit(Board(foot), 0.0f, Squash, RiseFactor);
        ball.Aim(new Vector2(0.0f, (float)_atlas!.HeightSpanPx) / Mathf.Max(HexWidth, 1.0f));
        ball.Fire();
        // In front of the whole tank, as the board draws it over the sprite.
        StandAt(ball, _rig.Position.Z + _model.Size.Length() * 0.5f * _model.PixelsPerUnit);
    }

    private void FxKnocked()
    {
        if (_atlas is null)
            return;
        _hitLocal = Strike(new Vector3(0, 0, -1)).At;
        _hitBehind = (_model.Hull.GlobalBasis * Vector3.Back).Z <= 0.0f;
        _wreck.Disable();
        Fireball(TankTick.KnockOutFlash, grounded: false);
    }

    private void FxDestroyed(Vector3 blast)
    {
        if (_atlas is null)
            return;
        if (!_wreck.Out)
            _wreck.Disable();
        _wreck.Kill(racked: true);
        _burning = true;
        Fireball(1.0f, grounded: true);
        // TankTick.Quake(Death): the class's own gun shake, harder.
        _shake.Fire(new Vector2(0.0f, -1.0f), _profile.ShotShake * 2.15);
        _shake.Blast(_profile.ShotShake * 1.6);
    }

    /// <summary>The turret hit the deck: the board's turret quake.</summary>
    private void FxLanded() => _shake.Fire(new Vector2(0.0f, 1.0f), _profile.ShotShake * 0.45);

    private void FxBurn(bool on) => _burning = on;

    private void FxReset()
    {
        _wreck.Reset();
        _burn.Reset();
        _exhaust.Reset();
        _hitLoop.Reset();
        _shake.Reset();
        _burning = false;
        _shotFrame = -1;
        foreach (var (mat, albedo) in _paint)
            mat.AlbedoColor = albedo;
        foreach (ProcKick k in _kicks) k.Douse();
        foreach (ProcKick k in _drifts) k.Douse();
        foreach (ProcSpall s in _spalls) s.Douse();
        foreach (ProcSlam s in _slams) s.Douse();
        foreach (ProcBall b in _balls) b.Douse();
        foreach (PitArt p in _pits) p.Hide();
    }

    // --- the frame ----------------------------------------------------------

    private float _dustSpent;
    private int _dustSide, _dustLaid;

    private void FxProcess(float dt, float speed, float accel)
    {
        _shake.Update(dt);
        foreach (ProcKick k in _kicks) k.Tick(dt);
        foreach (ProcKick k in _drifts) k.Tick(dt);
        foreach (ProcSpall sp in _spalls) sp.Tick(dt);
        foreach (ProcSlam sl in _slams) sl.Tick(dt);
        foreach (ProcBall b in _balls) b.Tick(dt);
        foreach (SheetBlast b in _booms) b.Tick(dt);
        if (_sprite is null || _atlas is null)
            return;

        TankSprite s = _sprite;
        double hull = TankSprite.Mod(_heading - 90.0, 360.0);
        s.HullFacing = hull;
        s.TurretFacing = TankSprite.Mod(hull + Mathf.RadToDeg(_model.Yaw), 360.0);
        s.BarrelRung = _atlas.RungFor(Mathf.RadToDeg(_model.Elevation));

        // TankTick.UpdateExhaust
        if (_wreck.Out)
        {
            if (s.ExhaustPhase >= 0)
            {
                _exhaust.Reset();
                s.ExhaustPhase = -1;
            }
        }
        else
        {
            _exhaust.Advance(Mathf.Abs(speed), dt);
            s.ExhaustPhase = _exhaust.Frame;
            s.ExhaustDensity = (float)_exhaust.Density;
            s.ExhaustCycle = (float)(_exhaust.Phase / Math.Max(_exhaust.Phases, 1));
        }

        // TankTick.UpdateWreck, then UpdateBurn
        if (_wreck.Out)
        {
            _wreck.Update(dt);
            s.FireDensity = (float)(_burning ? _wreck.Blaze : _wreck.Flare);
            s.SmokeDensity = (float)(_wreck.Dead || _burning ? _wreck.Smoke : _wreck.Smoulder);
            Char((float)_wreck.Char);
        }
        bool lit = (_burning || _wreck.Flare > 0.0) && _atlas.HasBurning;
        bool smoulder = !lit && _wreck.Disabled && _atlas.HasBurning;
        if (!lit && !smoulder)
        {
            if (s.Burning || s.Smouldering || s.FirePhase >= 0 || s.BurnPhase >= 0)
            {
                _burn.Reset();
                s.Burning = false;
                s.Smouldering = false;
                s.FirePhase = -1;
                s.BurnPhase = -1;
                s.FireCycle = 0.0f;
                s.SmokeCycle = 0.0f;
            }
        }
        else
        {
            _burn.Advance(dt);
            s.Burning = lit;
            s.Smouldering = smoulder;
            if (s.Column is ProcSmoke column)
                column.Ink = _burning ? ProcSmoke.ColumnInk : ProcSmoke.SmoulderInk;
            if (!_wreck.Out)
            {
                s.FireDensity = 1.0f;
                s.SmokeDensity = 1.0f;
            }
            s.FirePhase = _burn.FireFrame;
            s.BurnPhase = _burn.SmokeFrame;
            s.FireCycle = (float)(_burn.FirePhase / Math.Max(_burn.Phases, 1));
            s.SmokeCycle = (float)(_burn.SmokePhase / Math.Max(_burn.Phases, 1));
        }

        // TankTick.UpdateShot - frames, as the board counts them.
        int frame = _shotFrame < 0 ? -1 : FlashSheet.FrameAt(_shotFrame);
        int phase = _shotFrame < 0 ? -1 : EffectLayer.PhaseAt(_shotFrame);
        if (_shotFrame >= 0)
        {
            _shotFrame++;
            if (frame < 0 && phase < 0)
                _shotFrame = -1;
        }
        s.FlashFrame = frame;
        s.ShotPhase = phase;

        // The anchor, and every card slid to its source.
        Vector3 anchor = _model.Tank.ToGlobal(_anchorTank);
        Vector3 muzzle = _model.Muzzle.GlobalPosition;
        Vector3 struck = _model.Hull.ToGlobal(_hitLocal);

        // TankTick.UpdateHit, with the plate point the model's rather than the
        // atlas's table.
        int hitPhase = _hitLoop.Phase;
        if (hitPhase >= 0)
        {
            s.HitOffset = Drawn(struck - anchor);
            s.HitBehind = _hitBehind;
            s.HitScale = _hitLoop.Scale;
            s.HitThrough = _hitLoop.Through;
        }
        s.HitFrame = _hitLoop.Elapsed;
        s.HitPhase = hitPhase;
        _hitLoop.Advance();

        foreach (Card c in new[] { _rear!, _front!, _hit!, _glow! })
            c.Quad.Position = anchor;
        // The fire is the engine deck's, and a turret thrown onto that deck lies
        // over the port and overhangs the stern: stood behind it, the wreck
        // would burn out of sight. In front of it instead - the board draws the
        // tossed turret under the fire too.
        float port = _model.Tank.ToGlobal(_portTank).Z;
        if (_model.TurretOverride is not null && _model.Turret is MeshInstance3D turret)
            port = Mathf.Max(port, Nearest(turret));
        StandAt(_rear!.Quad, port + Margin);
        StandAt(_front!.Quad, muzzle.Z + Margin);
        StandAt(_hit!.Quad, struck.Z + Margin);
        // The leak is light round the turret ring, drawn on the sprite under
        // the turret: at the ring's own depth the turret's near half covers it
        // as the sprite's turret layer did. A casemate has no ring: the
        // fighting compartment it would leak from is the sidecar's blast point.
        Vector3 ring = _model.Turret?.GlobalPosition ?? _model.Tank.ToGlobal(_model.BlastAt);
        StandAt(_glow!.Quad, ring.Z + Margin);
        // The flash and the fume put the muzzle where the atlas measured it,
        // per 15-degree frame; the model's turret turns smoothly, so their card
        // is shifted by the difference - on screen, which is all it is.
        Vector2 measured = _atlas.Muzzle(s.TurretFacing, s.BarrelRung) - _atlas.Anchor;
        Vector2 miss = Drawn(muzzle - anchor) - measured;
        _front.Holder.Position = (Vector2.One * CardSize * 0.5f + miss) * CardZoom;

        foreach (CanvasItem item in _painted)
            item.QueueRedraw();

        Dust(dt, speed);
    }

    /// <summary>The largest world Z of a mesh's box - its nearest point to the
    /// camera, as depth goes here.</summary>
    private static float Nearest(MeshInstance3D mesh)
    {
        Aabb box = mesh.GetAabb();
        float z = float.MinValue;
        for (int i = 0; i < 8; i++)
            z = Mathf.Max(z, mesh.ToGlobal(box.GetEndpoint(i)).Z);
        return z;
    }

    /// <summary>The wreck's char on the model: the albedo dimmed toward soot
    /// by the fraction the board's char shader burns the sprite.</summary>
    private void Char(float amount)
    {
        var soot = new Color(0.16f, 0.14f, 0.12f);
        foreach (var (mat, albedo) in _paint)
            mat.AlbedoColor = albedo.Lerp(soot * albedo, Mathf.Clamp(amount, 0.0f, 1.0f) * 0.85f);
    }

    /// <summary><see cref="TrackDust.Lay"/> on the model's belts: a puff every
    /// <see cref="TrackDust.Step"/> px of travel, belts in turn, a share of the
    /// track's length astern, blown astern.</summary>
    private void Dust(float dt, float speed)
    {
        if (_wreck.Out || dt <= 0.0f)
            return;
        float moved = Mathf.Abs(speed) * dt;
        if (moved / dt < TrackDust.DriveAbove)
        {
            _dustSpent = 0.0f;
            return;
        }
        _dustSpent += moved;
        if (_dustSpent < TrackDust.Step)
            return;
        _dustSpent = Mathf.Min(_dustSpent - (float)TrackDust.Step, (float)TrackDust.Step);
        int side = _dustSide;
        _dustSide ^= 1;
        int laid = _dustLaid++;
        Vector3 forward = _rig.GlobalBasis.Z;
        Vector3 left = _rig.GlobalBasis.X;
        Vector3 astern = speed >= 0.0f ? -forward : forward;
        float arm = _model.Tracks.Count > 0
            ? Mathf.Abs(_model.Tracks[0].Node.Position.X) * _model.PixelsPerUnit : 40.0f;
        float length = _model.Size.Z * _model.PixelsPerUnit;
        float wander = (2.0f * Plumes.Hash01(laid, 21391) - 1.0f) * 12.0f;
        Vector3 at = _rig.Position + left * ((side == 0 ? arm : -arm) + wander)
                     + astern * length * (float)TrackDust.Trail;
        float share = Mathf.Clamp(Mathf.Abs(speed) / (float)_profile.TopSpeed, 0.0f, 1.0f);
        float might = TrackDust.Might * share * (float)_profile.Size
                      * (1.0f + TrackDust.Ripple * (2.0f * Plumes.Hash01(laid, 60167) - 1.0f));
        ProcKick kick = Next(_drifts, ref _nextDrift, DriftPool, () =>
        {
            // Stage3D.Drift's ring: lying, and shaped before Build.
            var made = new ProcKick { Lying = true, Reach = TrackDust.Carry, Swell = TrackDust.Born };
            made.Root = TrackDust.Bed;
            made.Tall = TrackDust.Bed * 2.0f;
            AddChild(made);
            made.Build(HexWidth, Squash, RiseFactor);
            made.Hasten(TrackDust.Hang);
            made.Settle(TrackDust.Creep);
            made.Dial(ProcKick.Part.Dust, "dust_ink", made.Dial(ProcKick.Part.Dust, "dust_ink") * TrackDust.Ink);
            return made;
        });
        kick.Might = 1.0f;
        kick.Might *= might;
        kick.Order = Stage3D.DressOrder;
        kick.Dress(ProcKick.Cloud.Ground);
        kick.Sit(Board(at), 0.0f, Squash, RiseFactor);
        kick.Aim(Along(astern), Vector2.Zero);
        kick.Fire();
    }

    /// <summary>The camera's shake, in world units at this zoom.</summary>
    private Vector2 ShakeOffset() => _shake.ScreenOffset(_zoom);
}
