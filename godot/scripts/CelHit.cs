using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// Rounds arriving on a 3D tank, in the model's own look: <b>where</b> each
/// one lands, the <b>mark</b> it leaves on the plate, and a ricochet's
/// <b>impact</b> - a star of light on the plate, sparks thrown off the way the
/// round glanced and a puff of paint and dust. The sprites' ricochet
/// (<see cref="ProcSpall"/>) is left as it is for the 2D tanks; on the model
/// the 3D bench runs this instead.
///
/// <list type="bullet">
/// <item><b>Where</b> (<see cref="Aim"/>) - anywhere on the tank's plates the
/// round can see from where it comes: a ray from a random point of the
/// tank's cross-section across the round's way, turned off the side's
/// straight line a little and dipping as a round from range does, and the
/// first part of the model it meets. Hull, turret, mantlet, gun and skirts
/// take the round; the belts and wheels stop it and it is thrown again - a
/// mark there would be on a part that runs. Bigger plates, as seen from the
/// round, take more hits, as they would.</item>
/// <item><b>Mark</b> - painted in the cel shader (<see cref="Toon"/>'s
/// <c>mark_*</c>) round the point on the part it hit, in that part's frame, so
/// it turns with the turret and lays with the gun, and stays until
/// <see cref="Reset"/>. A ricochet leaves a gouge - bare steel scraped the way
/// the round went on, the paint burnt round it; a penetration a hole - black,
/// a torn rim of bare steel; HE a round burn with a torn edge, pitted with steel. Fresh,
/// the bare steel glows and cools in about a second. The newest
/// <see cref="Toon.MaxMarks"/> a material carries are kept.</item>
/// <item><b>Impact</b> of a ricochet - a star of light at the plate for four
/// frames; sparks: streaks thrown off round the way the round glanced,
/// falling, going from white to orange; a puff of grey
/// (<see cref="CelCloud"/>) blown off the plate.</item>
/// <item><b>Impact</b> of a penetration (<see cref="Pierce"/>) - a bigger,
/// whiter star for six frames; spall blown back out of the hole round its
/// normal, slower; a dark puff out of it; and then the <b>wound</b>: a thin
/// wisp of smoke trickling out of the hole and rising for
/// <see cref="WispTime"/>, on the part, so it goes with a turning turret.
/// The sprites' entry cloud and glow card (<see cref="ProcKick"/>,
/// <see cref="ProcPierce"/>) are not run on the model.</item>
/// </list>
///
/// All lengths are shares of the hull's length on the board (<see cref="Build"/>).
/// </summary>
public sealed partial class CelHit : Node3D
{
    /// <summary>What a round leaves: the shader's kinds.</summary>
    public enum Kind { Gouge = 0, Hole = 1, Splash = 2 }

    /// <summary>A mark's radius, hull lengths, by kind. A hole's is bigger
    /// than a gouge's: at the gouge's, its black was a pixel or two across and
    /// the penetration read as nothing. HE's is the biggest: at 0.04 it was
    /// five pixels at the turret's foot and gone.</summary>
    public float GougeSize = 0.028f, HoleSize = 0.045f, SplashSize = 0.065f;

    /// <summary>How far off the side's straight line a round may come, deg,
    /// and how steeply it may dip.</summary>
    public float Scatter = 32.0f, DipFrom = 0.04f, DipTo = 0.22f;

    /// <summary>The ricochet's star: how long, s, and its radius, hull lengths.</summary>
    public float StarTime = 4.0f / 60.0f, StarSize = 0.13f;
    /// <summary>The star's light. Off: the ramp stepped the plates round it
    /// straight up to a yellow-green band, a smear bigger than the star.</summary>
    public Color GlowColor = new(1.0f, 0.80f, 0.45f);
    public float GlowEnergy = 0.0f, GlowReach = 0.22f;

    /// <summary>Sparks: how many, their life, s, speed, hull lengths a second,
    /// how wide round the glance they go, deg, and how hard they fall.</summary>
    public int Sparks = 22;
    public float SparkLife = 0.38f, SparkSpeed = 2.8f, SparkCone = 40.0f, SparkFall = 3.5f;

    /// <summary>How long a hole goes on smoking, s, how often a wisp puff
    /// leaves it, s, and how long each lives.</summary>
    public float WispTime = 4.0f, WispEvery = 0.10f, WispLife = 1.3f;

    /// <summary>The puff off the plate: puffs, life, s, reach, hull lengths.</summary>
    public int PuffCount = 9;
    public float PuffLife = 0.8f, PuffReach = 0.16f;

    // ------------------------------------------------------------ the machinery

    private sealed class Mark
    {
        public required MeshInstance3D Owner;
        public required Vector3 At, Way, N;
        public required float R, Born;
        public required Kind Kind;
    }

    private sealed class Part
    {
        public required MeshInstance3D Node;
        public required TriangleMesh Hits;
        public required bool Takes;
        public required HashSet<ShaderMaterial> Wears;
    }

    private readonly List<Part> _parts = new();
    private readonly List<Mark> _marks = new();
    private readonly Dictionary<ShaderMaterial, (Vector4[] At, Vector4[] Dir, Vector4[] Nrm)> _upload = new();
    private IReadOnlyList<ShaderMaterial> _paint = System.Array.Empty<ShaderMaterial>();
    private float _hull = 150.0f;
    private float _now;
    private int _rounds;

    private float _since = -1.0f;
    private Vector3 _at, _n, _glance;
    /// <summary>The impact running is a penetration's.</summary>
    private bool _through;
    /// <summary>The impact running is two hulls meeting - <see cref="Scrape"/>.</summary>
    private bool _scrape;
    private MeshInstance3D? _wispOn;
    private Vector3 _wispAt, _wispN;
    private float _wispSince = -1.0f;
    private int _wisps;
    private CelCloud? _wisp;
    private MeshInstance3D? _star;
    private ShaderMaterial? _starLook;
    private OmniLight3D? _glow;
    private MultiMesh? _sparks;
    private MultiMeshInstance3D? _sparkCloud;
    private CelCloud? _puff;

    public void Build(float hullPx)
    {
        _hull = Mathf.Max(hullPx, 1.0f);
        _starLook = new ShaderMaterial { Shader = StarShader };
        _star = new MeshInstance3D
        {
            Name = "Star", Mesh = new QuadMesh { Size = Vector2.One }, MaterialOverride = _starLook,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false,
        };
        AddChild(_star);
        _glow = new OmniLight3D
        {
            Name = "Glow", LightColor = GlowColor, OmniAttenuation = 0.0f, ShadowEnabled = false,
            LightEnergy = 0.0f, Visible = false,
        };
        AddChild(_glow);
        var sparkLook = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
        };
        _sparks = new MultiMesh
        {
            Mesh = new BoxMesh { Size = Vector3.One },
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            InstanceCount = Sparks,
            VisibleInstanceCount = 0,
        };
        _sparkCloud = new MultiMeshInstance3D
        {
            Name = "Sparks", Multimesh = _sparks, MaterialOverride = sparkLook,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_sparkCloud);
        _puff = new CelCloud(this, "Puff", new Color(0.95f, 0.92f, 0.88f), 0.03f * _hull);
        _wisp = new CelCloud(this, "Wisp", new Color(0.92f, 0.92f, 0.94f), 0.02f * _hull);
    }

    /// <summary>The parts of <paramref name="model"/> a round can meet, and the
    /// cel materials the marks are painted into.</summary>
    public void Targets(TankModel model, IReadOnlyList<ShaderMaterial> paint)
    {
        _parts.Clear();
        _paint = paint;
        var running = new HashSet<Node>();
        foreach (TankModel.Track t in model.Tracks)
            running.Add(t.Node);
        Walk(model.Body, false, running);
    }

    private void Walk(Node node, bool runs, HashSet<Node> running)
    {
        runs |= running.Contains(node);
        if (node is MeshInstance3D mi && mi.Mesh is Mesh mesh)
        {
            var wears = new HashSet<ShaderMaterial>();
            for (int s = 0; s < mesh.GetSurfaceCount(); s++)
                if ((mi.GetSurfaceOverrideMaterial(s) ?? mesh.SurfaceGetMaterial(s)) is ShaderMaterial m)
                    wears.Add(m);
            _parts.Add(new Part { Node = mi, Hits = mesh.GenerateTriangleMesh(), Takes = !runs, Wears = wears });
        }
        foreach (Node child in node.GetChildren())
            Walk(child, runs, running);
    }

    /// <summary>
    /// Where a round travelling about <paramref name="travel"/> (world) lands:
    /// the part, the point and the plate's normal (toward the round), and the
    /// way it came, turned and dipped. Null when nothing of the model is met.
    /// </summary>
    public (MeshInstance3D Part, Vector3 At, Vector3 N, Vector3 Way)? Aim(Vector3 travel)
    {
        int round = ++_rounds;
        Aabb box = default;
        bool any = false;
        foreach (Part p in _parts)
        {
            if (!p.Takes || !p.Node.IsVisibleInTree())
                continue;
            Aabb b = p.Node.GlobalTransform * p.Node.GetAabb();
            box = any ? box.Merge(b) : b;
            any = true;
        }
        if (!any)
            return null;
        Vector3 mid = box.GetCenter();
        float reach = box.Size.Length();
        for (int attempt = 0; attempt < 32; attempt++)
        {
            float h1 = CelPuff.Hash(round * 61 + attempt, 3), h2 = CelPuff.Hash(round * 61 + attempt, 5);
            float h3 = CelPuff.Hash(round * 61 + attempt, 7), h4 = CelPuff.Hash(round * 61 + attempt, 11);
            Vector3 flat = new Vector3(travel.X, 0.0f, travel.Z).Normalized();
            Vector3 way = flat.Rotated(Vector3.Up, Mathf.DegToRad((h1 - 0.5f) * 2.0f * Scatter));
            way = (way + Vector3.Down * Mathf.Lerp(DipFrom, DipTo, h2)).Normalized();
            Vector3 side = way.Cross(Vector3.Up).Normalized();
            Vector3 up = side.Cross(way).Normalized();
            // Across the round's way, anywhere the box can show.
            float u = 0.0f, v = 0.0f;
            for (int c = 0; c < 8; c++)
            {
                Vector3 corner = box.GetEndpoint(c) - mid;
                u = Mathf.Max(u, Mathf.Abs(corner.Dot(side)));
                v = Mathf.Max(v, Mathf.Abs(corner.Dot(up)));
            }
            Vector3 from = mid - way * reach + side * ((h3 - 0.5f) * 2.0f * u) + up * ((h4 - 0.5f) * 2.0f * v);
            Part? best = null;
            float bestD = float.MaxValue;
            Vector3 bestAt = Vector3.Zero, bestN = Vector3.Up;
            foreach (Part p in _parts)
            {
                if (!p.Node.IsVisibleInTree())
                    continue;
                Transform3D inv = p.Node.GlobalTransform.AffineInverse();
                var hit = p.Hits.IntersectRay(inv * from, (inv.Basis * way).Normalized());
                if (hit.Count == 0 || !hit.ContainsKey("position"))
                    continue;
                Vector3 at = p.Node.GlobalTransform * (Vector3)hit["position"];
                float d = (at - from).Dot(way);
                if (d <= 0.0f || d >= bestD)
                    continue;
                bestD = d;
                best = p;
                bestAt = at;
                bestN = (p.Node.GlobalBasis * (Vector3)hit["normal"]).Normalized();
            }
            // The belts and wheels stop a round, and it is thrown again.
            if (best is null || !best.Takes)
                continue;
            if (bestN.Dot(way) > 0.0f)
                bestN = -bestN;
            return (best.Node, bestAt, bestN, way);
        }
        return null;
    }

    /// <summary>A mark of <paramref name="kind"/> on <paramref name="part"/> at
    /// <paramref name="at"/> (world), on a plate facing <paramref name="n"/>,
    /// the round having come along <paramref name="way"/>.</summary>
    public void Leave(MeshInstance3D part, Vector3 at, Vector3 n, Vector3 way, Kind kind)
    {
        // A gouge runs the way the round glanced, along the plate.
        Vector3 on = way - way.Dot(n) * n;
        float glance = Mathf.Clamp(on.Length(), 0.0f, 1.0f);
        if (on.LengthSquared() < 1e-6f)
            on = n.Cross(Vector3.Up);
        if (on.LengthSquared() < 1e-6f)
            on = Vector3.Right;
        Transform3D inv = part.GlobalTransform.AffineInverse();
        float r = _hull * (kind switch { Kind.Gouge => GougeSize, Kind.Hole => HoleSize, _ => SplashSize })
                  * (0.85f + 0.3f * CelPuff.Hash(_rounds, 13));
        _marks.Add(new Mark
        {
            Owner = part,
            At = inv * at,
            // Its length kept: how much the round glanced.
            Way = (inv.Basis * on.Normalized()).Normalized() * glance,
            N = (inv.Basis * n).Normalized(),
            // Kept in world px through the part's scale.
            R = r,
            Born = _now,
            Kind = kind,
        });
        if (_marks.Count > 64)
            _marks.RemoveAt(0);
    }

    /// <summary>A ricochet at <paramref name="at"/> on a plate facing
    /// <paramref name="n"/>, the round going on along <paramref name="glance"/>:
    /// the star, the light, the sparks and the puff.</summary>
    public void Ricochet(Vector3 at, Vector3 n, Vector3 glance)
    {
        _since = 0.0f;
        _through = false;
        _scrape = false;
        _at = at;
        _n = n;
        _glance = glance.Normalized();
    }

    /// <summary>
    /// Metal scraped off where two hulls meet - a ram (<c>Tank3DBench.Ram</c>):
    /// the sparks and the puff of a ricochet at <paramref name="at"/>, thrown
    /// round <paramref name="glance"/> and out of <paramref name="n"/>, and
    /// <b>no star</b>. The star is what the round makes of itself on the plate,
    /// and in a ram there is no round - the sprites' contact fan gave up its
    /// bloom for the same reason (<c>ProcSpall.Blame(Cause.Contact)</c>).
    /// </summary>
    public void Scrape(Vector3 at, Vector3 n, Vector3 glance)
    {
        _rounds++;
        _since = 0.0f;
        _through = false;
        _scrape = true;
        _at = at;
        _n = n.Normalized();
        _glance = glance.Normalized();
    }

    /// <summary>A penetration of <paramref name="part"/> at
    /// <paramref name="at"/> on a plate facing <paramref name="n"/>: the star,
    /// the spall and the dark puff, and the hole left smoking.</summary>
    public void Pierce(MeshInstance3D part, Vector3 at, Vector3 n)
    {
        _since = 0.0f;
        _through = true;
        _scrape = false;
        _at = at;
        _n = n;
        _glance = n;
        Transform3D inv = part.GlobalTransform.AffineInverse();
        _wispOn = part;
        _wispAt = inv * at;
        _wispN = (inv.Basis * n).Normalized();
        _wispSince = 0.0f;
        _wisps++;
    }

    /// <summary>Every mark gone and the impact put out.</summary>
    public void Reset()
    {
        _marks.Clear();
        _since = -1.0f;
        if (_star is not null)
            _star.Visible = false;
        if (_glow is not null)
            _glow.Visible = false;
        if (_sparks is not null)
            _sparks.VisibleInstanceCount = 0;
        _puff?.Hide();
        _wisp?.Hide();
        _wispSince = -1.0f;
        Upload();
    }

    public void Tick(float dt, Basis eye)
    {
        _now += dt;
        Upload();
        Wisp(dt, eye);
        if (_since < 0.0f || _star is null || _glow is null || _sparks is null || _puff is null)
            return;
        float t = _since;
        _since += dt;
        if (t > Mathf.Max(SparkLife * 1.3f, PuffLife * 1.6f))
        {
            _since = -1.0f;
            _star.Visible = false;
            _glow.Visible = false;
            _sparks.VisibleInstanceCount = 0;
            _puff.Hide();
            return;
        }
        Star(t, eye);
        Spray(t);
        Puff(t, eye);
    }

    /// <summary>Every cel material its own marks, in the world as the parts
    /// stand now: the newest <see cref="Toon.MaxMarks"/> of the marks on the
    /// parts that wear it.</summary>
    private void Upload()
    {
        foreach (ShaderMaterial m in _paint)
        {
            if (!_upload.TryGetValue(m, out var arrays))
            {
                arrays = (new Vector4[Toon.MaxMarks], new Vector4[Toon.MaxMarks], new Vector4[Toon.MaxMarks]);
                _upload[m] = arrays;
            }
            int n = 0;
            for (int i = _marks.Count - 1; i >= 0 && n < Toon.MaxMarks; i--)
            {
                Mark k = _marks[i];
                if (!IsInstanceValid(k.Owner) || !Wears(k.Owner, m))
                    continue;
                Transform3D g = k.Owner.GlobalTransform;
                Vector3 at = g * k.At;
                Vector3 way = (g.Basis * k.Way).Normalized() * k.Way.Length();
                Vector3 nrm = (g.Basis * k.N).Normalized();
                arrays.At[n] = new Vector4(at.X, at.Y, at.Z, k.R);
                arrays.Dir[n] = new Vector4(way.X, way.Y, way.Z, (float)k.Kind);
                arrays.Nrm[n] = new Vector4(nrm.X, nrm.Y, nrm.Z, k.Born);
                n++;
            }
            m.SetShaderParameter("mark_count", n);
            if (n == 0)
                continue;
            m.SetShaderParameter("mark_at", arrays.At);
            m.SetShaderParameter("mark_dir", arrays.Dir);
            m.SetShaderParameter("mark_nrm", arrays.Nrm);
            m.SetShaderParameter("mark_now", _now);
        }
    }

    private bool Wears(MeshInstance3D owner, ShaderMaterial m)
    {
        foreach (Part p in _parts)
            if (p.Node == owner)
                return p.Wears.Contains(m);
        return false;
    }

    private void Star(float t, Basis eye)
    {
        float time = _through ? 6.0f / 60.0f : StarTime;
        bool on = t < time && !_scrape;
        _star!.Visible = on;
        _glow!.Visible = on && GlowEnergy > 0.0f;
        if (!on)
            return;
        float size = StarSize * _hull * (_through ? 1.6f : 1.0f);
        Vector3 right = eye.X.Normalized(), up = eye.Y.Normalized(), back = eye.Z.Normalized();
        // Off the plate along its normal, so the plate does not cut it in half;
        // a plate turned from the eye still hides it.
        _star.GlobalTransform = new Transform3D(new Basis(right * (2.0f * size), up * (2.0f * size), back),
                                                _at + _n * (0.05f * _hull));
        _starLook!.SetShaderParameter("seed", CelPuff.Hash(_rounds, 17));
        // A penetration's star holds its white core a frame longer.
        _starLook.SetShaderParameter("frame", _through ? t * 60.0f * 0.6f : t * 60.0f);
        _glow.GlobalPosition = _at + _n * (0.06f * _hull);
        _glow.OmniRange = GlowReach * _hull;
        _glow.LightEnergy = GlowEnergy * (1.0f - t / time);
    }

    private void Spray(float t)
    {
        Vector3 a1 = _glance.Cross(Vector3.Up);
        if (a1.LengthSquared() < 1e-6f)
            a1 = Vector3.Right;
        a1 = a1.Normalized();
        Vector3 a2 = a1.Cross(_glance).Normalized();
        int shown = 0;
        int count = _through ? Sparks * 2 / 3 : Sparks;
        for (int k = 0; k < count; k++)
        {
            float h1 = CelPuff.Hash(_rounds * 97 + k, 19), h2 = CelPuff.Hash(_rounds * 97 + k, 23);
            float h3 = CelPuff.Hash(_rounds * 97 + k, 29);
            float life = SparkLife * (0.5f + 0.7f * h3);
            float a = t / life;
            if (a >= 1.0f)
                continue;
            // Round the glance, and out of the plate - never into it.
            // Spall blows back out of a hole, wide round its normal.
            float spread = Mathf.DegToRad(_through ? 55.0f : SparkCone) * Mathf.Sqrt(h1);
            float ang = h2 * Mathf.Tau;
            Vector3 dir = (_glance * Mathf.Cos(spread)
                           + (a1 * Mathf.Cos(ang) + a2 * Mathf.Sin(ang)) * Mathf.Sin(spread)).Normalized();
            if (dir.Dot(_n) < 0.1f)
                dir = (dir + _n * (0.1f - dir.Dot(_n) + 0.1f)).Normalized();
            float speed = SparkSpeed * _hull * (0.55f + 0.9f * h3) * (_through ? 0.6f : 1.0f);
            Vector3 vel = dir * speed + Vector3.Down * (SparkFall * _hull * t);
            Vector3 at = _at + dir * (speed * t) + Vector3.Down * (0.5f * SparkFall * _hull * t * t);
            // A streak as long as the way it went in a sixtieth, shrinking.
            float len = Mathf.Max(vel.Length() / 60.0f * 1.6f * (1.0f - a), 1.0f);
            float thick = Mathf.Max(1.4f * (1.0f - 0.5f * a), 0.7f);
            Vector3 x = vel.Normalized();
            Vector3 y = x.Cross(Vector3.Up);
            if (y.LengthSquared() < 1e-6f)
                y = Vector3.Right;
            y = y.Normalized();
            Vector3 z = x.Cross(y).Normalized();
            var basis = new Basis(x * len, y * thick, z * thick);
            _sparks!.SetInstanceTransform(shown, new Transform3D(basis, at - x * (0.5f * len)));
            Color hot = new(1.0f, 0.97f, 0.80f), cool = new(1.0f, 0.50f, 0.12f);
            _sparks.SetInstanceColor(shown, hot.Lerp(cool, Mathf.SmoothStep(0.0f, 0.7f, a)));
            shown++;
        }
        _sparks!.VisibleInstanceCount = shown;
    }

    private void Puff(float t, Basis eye)
    {
        _puff!.Clear();
        Vector3 out_ = _through ? _n : (_n * 0.7f + _glance * 0.5f).Normalized();
        float reach = PuffReach * (_through ? 1.4f : 1.0f);
        // A penetration's is dark and spreads wide: gathered close, its big
        // puffs made one black ball.
        float big = _through ? 1.1f : 1.0f;
        float wide = _through ? 2.2f : 1.2f;
        float tone = _through ? 0.34f : 0.48f;
        for (int k = 0; k < PuffCount; k++)
        {
            float h1 = CelPuff.Hash(_rounds * 53 + k, 31), h2 = CelPuff.Hash(_rounds * 53 + k, 37);
            float life = PuffLife * (0.7f + 0.6f * h2);
            float a = t / life;
            if (a >= 1.0f)
                continue;
            Vector3 side = out_.Cross(Vector3.Up);
            if (side.LengthSquared() < 1e-6f)
                side = Vector3.Right;
            side = side.Normalized();
            Vector3 up2 = side.Cross(out_).Normalized();
            float h3 = CelPuff.Hash(_rounds * 53 + k, 39);
            Vector3 at = _at + (out_ + side * ((h1 - 0.5f) * wide) + up2 * ((h3 - 0.5f) * wide * 0.7f)).Normalized()
                         * (reach * _hull * (0.4f + 0.6f * h1) * (1.0f - Mathf.Exp(-t / 0.08f)))
                         + Vector3.Up * (0.05f * _hull * a);
            float r = big * _hull * Mathf.Lerp(0.02f, 0.06f, 1.0f - Mathf.Pow(1.0f - a, 2.0f))
                      * Mathf.SmoothStep(0.0f, 0.5f * StarTime, t);
            _puff.Add(at, r, tone + 0.1f * h2, h1, Mathf.SmoothStep(0.1f, 1.0f, a), a);
        }
        _puff.Draw(eye);
    }

    /// <summary>The wound: puffs leaving the hole every <see cref="WispEvery"/>
    /// for <see cref="WispTime"/>, thinner as it goes, each rising off the
    /// plate and drifting, where the hole is now.</summary>
    private void Wisp(float dt, Basis eye)
    {
        if (_wisp is null)
            return;
        if (_wispSince < 0.0f || _wispOn is null || !IsInstanceValid(_wispOn) || !_wispOn.IsVisibleInTree())
        {
            _wisp.Hide();
            return;
        }
        float t = _wispSince;
        _wispSince += dt;
        if (t > WispTime + WispLife)
        {
            _wispSince = -1.0f;
            _wisp.Hide();
            return;
        }
        Transform3D g = _wispOn.GlobalTransform;
        Vector3 hole = g * _wispAt;
        Vector3 n = (g.Basis * _wispN).Normalized();
        _wisp.Clear();
        int first = Mathf.Max(0, (int)((t - WispLife) / WispEvery));
        int last = (int)(Mathf.Min(t, WispTime) / WispEvery);
        for (int k = first; k <= last; k++)
        {
            float born = k * WispEvery;
            float a = (t - born) / WispLife;
            if (a < 0.0f || a >= 1.0f)
                continue;
            float h1 = CelPuff.Hash(_wisps * 211 + k, 41), h2 = CelPuff.Hash(_wisps * 211 + k, 43);
            // Weaker as the wound goes on: smaller and fewer.
            float left = 1.0f - born / WispTime;
            if (h2 > 0.35f + 0.65f * left)
                continue;
            Vector3 at = hole + n * (0.02f * _hull + 0.06f * _hull * Mathf.Sqrt(a))
                         + Vector3.Up * (0.30f * _hull * a)
                         + new Vector3(0.10f, 0.0f, -0.04f) * (_hull * a)
                         + new Vector3(h1 - 0.5f, 0.0f, h2 - 0.5f) * (0.04f * _hull * a);
            // No smaller than this: under two ink widths the cloud drops a
            // puff, and at 0.01 hull the young wisp was not drawn at all.
            float r = _hull * Mathf.Lerp(0.022f, 0.050f, a) * (0.6f + 0.4f * left)
                      * Mathf.SmoothStep(0.0f, 0.1f, a);
            _wisp.Add(at, r, 0.40f + 0.08f * h1, h1, Mathf.SmoothStep(0.2f, 1.0f, a), a);
        }
        _wisp.Draw(eye);
    }

    /// <summary>
    /// The ricochet's star, by frames since the hit: a white-hot core and six
    /// rays of uneven length (frame 0), the rays out and thinning (1), a
    /// shrinking orange spot (2). The fire's bands, as the gun's flash.
    /// </summary>
    private static readonly Shader StarShader = new()
    {
        Code = @"
shader_type spatial;
render_mode unshaded, cull_disabled, shadows_disabled, fog_disabled;
stencil_mode write, compare_always, 0;
uniform vec3 core : source_color = vec3(1.0, 0.98, 0.86);
uniform vec3 body : source_color = vec3(1.0, 0.82, 0.36);
uniform vec3 edge : source_color = vec3(1.0, 0.50, 0.12);
uniform float frame = 0.0;
uniform float seed = 0.0;
" + Toon.NoiseCode + @"
float hash1(float x) { return fract(sin(x * 12.9898 + 4.1414) * 43758.5453); }
void fragment() {
    vec2 p = (UV - 0.5) * 2.0;
    float d = length(p);
    float ang = atan(p.y, p.x) + seed * 6.28;
    float k = floor(ang / 6.2832 * 6.0 + 0.5);
    float mid = k / 6.0 * 6.2832;
    float off = abs(ang - mid);
    float ray_len = mix(0.55, 1.0, hash1(k + seed * 17.0)) * (frame < 1.0 ? 0.75 : 1.0);
    float ray_w = 0.20 * (1.0 - d / max(ray_len, 1e-3)) * (frame < 1.0 ? 1.0 : 0.6);
    float f = max(1.0 - d / (frame < 2.0 ? 0.32 : 0.20), (ray_w - off * d) * 4.0);
    if (frame >= 2.0) f = 1.0 - d / 0.22;
    if (f < 0.0) discard;
    vec3 c = frame < 2.0 ? (f > 0.45 ? core : (f > 0.18 ? body : edge)) : (f > 0.4 ? body : edge);
    ALBEDO = c;
}
",
    };
}
