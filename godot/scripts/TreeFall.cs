using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// One tree model going over (docs/props.md, "Tree3D: падение от тарана"):
/// the sprite's pendulum (<c>Grove.Timber</c>) with the model's own crown spring
/// and lie from <c>tree.json</c> (<c>model.fall</c>), posed by
/// <see cref="Toon.FallCode"/>; the root plate tearing out with its pit and
/// a few crumbs off its rim. Shared by <c>Tree3DBench</c> (a ram, RMB, the
/// wood) and <c>Tank3DBench</c> (the heavy's bulldozer); which hull pushed it,
/// and whether it may go - a burning tree does not - is the scene's.
/// </summary>
public sealed class TreeFall
{
    /// <summary>What <c>tree.json</c> says about the model going over
    /// (<c>model.fall</c>, pipeline/docs/trees.md "Падение модели"), metres.</summary>
    public sealed class Data
    {
        public float Hinge, Y0, H, Mat, Lift;
        /// <summary>The root plate's radius and its top, m (<c>fall.plate</c>;
        /// 0 for a sidecar from before it).</summary>
        public float Plate, PlateTop;
        public float[] Azimuth = Array.Empty<float>(), Rest = Array.Empty<float>();
        public float[] LiveTouch = Array.Empty<float>(), LiveTouchM = Array.Empty<float>();
        public float[] BurntTouch = Array.Empty<float>(), BurntTouchM = Array.Empty<float>();
        public double Gravity, Shove, LiftAngle, Bounce, Settle, Lag, Whip, Stiffness, Damping;

        public static Data Read(JsonElement f)
        {
            JsonElement lie = f.GetProperty("lie"), t = f.GetProperty("timber");
            float[] A(string k) => System.Linq.Enumerable.ToArray(
                System.Linq.Enumerable.Select(lie.GetProperty(k).EnumerateArray(), x => x.GetSingle()));
            return new Data
            {
                Hinge = f.GetProperty("hinge_m").GetSingle(),
                Y0 = f.GetProperty("bend").GetProperty("y0_m").GetSingle(),
                H = f.GetProperty("bend").GetProperty("H_m").GetSingle(),
                Mat = f.GetProperty("crush").GetProperty("mat_m").GetSingle(),
                Lift = f.GetProperty("crush").GetProperty("lift_m").GetSingle(),
                Plate = f.TryGetProperty("plate", out JsonElement pl) ? pl.GetProperty("radius_m").GetSingle() : 0.0f,
                PlateTop = f.TryGetProperty("plate", out JsonElement pt) ? pt.GetProperty("top_m").GetSingle() : 0.0f,
                Azimuth = A("azimuth_deg"), Rest = A("rest_deg"),
                LiveTouch = A("live_touch_deg"), LiveTouchM = A("live_touch_m"),
                BurntTouch = A("burnt_touch_deg"), BurntTouchM = A("burnt_touch_m"),
                Gravity = t.GetProperty("gravity").GetDouble(), Shove = t.GetProperty("shove").GetDouble(),
                LiftAngle = t.GetProperty("lift").GetDouble(), Bounce = t.GetProperty("bounce").GetDouble(),
                Settle = t.GetProperty("settle").GetDouble(), Lag = t.GetProperty("lag").GetDouble(),
                Whip = t.GetProperty("whip").GetDouble(), Stiffness = t.GetProperty("stiffness").GetDouble(),
                Damping = t.GetProperty("damping").GetDouble(),
            };
        }

        /// <summary>One of the 24 directions' numbers at any azimuth, between
        /// its two neighbours.</summary>
        public float At(float[] of, float azimuth)
        {
            int n = Azimuth.Length;
            float step = 360.0f / n;
            float x = Mathf.PosMod(azimuth, 360.0f) / step;
            int i = (int)Mathf.Floor(x) % n;
            return Mathf.Lerp(of[i], of[(i + 1) % n], x - Mathf.Floor(x));
        }
    }

    public required Data Of;
    public required Node3D Holder;
    /// <summary>Every material the pose goes into: cel, ink and the crown's mask.</summary>
    public required List<ShaderMaterial> Burn;
    /// <summary>Where the crumbs and the pit go.</summary>
    public required Node Parent;
    public required float Ppm, Height, Width;

    public bool Going, Lying, Touched;
    /// <summary>The way it goes over: in the model (the shader's and the
    /// sidecar's) and on the ground; the two differ by the model's turn.</summary>
    public Vector2 Dir, WorldDir;
    public double Angle, Spin, Flinch, FlinchRate, Rest, Touch, TouchM;
    private readonly List<Clod> _clods = new();
    private MeshInstance3D? _pit;
    private ShaderMaterial? _pitLook;

    /// <summary>How far over, nought standing to one lying.</summary>
    public float Down => Going ? (float)Math.Min(1.0, Angle / Math.Max(Rest, 1e-6)) : 0.0f;

    /// <summary>A model's fall, off its sidecar (<c>model.fall</c>), and its
    /// bend handed to the wind (the same band of the trunk bends).</summary>
    public static TreeFall For(TreeModel.Loaded model, Node3D holder, List<ShaderMaterial> burn, Node parent)
    {
        var fall = new TreeFall
        {
            Of = Data.Read(model.Json.GetProperty("model").GetProperty("fall")),
            Holder = holder, Burn = burn, Parent = parent,
            Ppm = model.Ppm, Height = model.Height, Width = model.Width,
        };
        foreach (ShaderMaterial m in burn)
        {
            m.SetShaderParameter("wind_y0", fall.Of.Y0);
            m.SetShaderParameter("wind_h", fall.Of.H);
        }
        return fall;
    }

    /// <summary>
    /// Over toward <paramref name="way"/> (the ground's x, z): the pendulum's
    /// start, and the lie and touch its direction has in <c>tree.json</c> -
    /// a <paramref name="burnt"/> tree touches as its bare limbs do. A tree
    /// already going is left to it. <paramref name="log"/> heads the line.
    /// </summary>
    public bool Start(Vector2 way, float shove, bool burnt, string log)
    {
        if (Going)
            return false;
        Data f = Of;
        WorldDir = way.Normalized();
        // the model may stand turned: the shader and the sidecar are in its frame
        Vector3 own = Holder.GlobalBasis.Orthonormalized().Inverse() * new Vector3(WorldDir.X, 0.0f, WorldDir.Y);
        var dir = new Vector2(own.X, own.Z).Normalized();
        // tree.json's azimuth: from +X toward -Z
        float az = Mathf.RadToDeg(Mathf.Atan2(-dir.Y, dir.X));
        Dir = dir;
        Rest = Mathf.DegToRad(Mathf.Min(f.At(f.Rest, az), 88.0f));
        Touch = Mathf.DegToRad(f.At(burnt ? f.BurntTouch : f.LiveTouch, az));
        TouchM = f.At(burnt ? f.BurntTouchM : f.LiveTouchM, az);
        Going = true;
        Lying = Touched = false;
        Angle = f.LiftAngle;
        Spin = f.Shove * (0.6 + 0.4 * Mathf.Clamp(shove, 0.0f, 1.0f));
        Flinch = FlinchRate = 0.0;
        foreach (ShaderMaterial m in Burn)
        {
            m.SetShaderParameter("fall_on", true);
            m.SetShaderParameter("fall_dir", dir);
            m.SetShaderParameter("fall_hinge", f.Hinge);
            m.SetShaderParameter("fall_y0", f.Y0);
            m.SetShaderParameter("fall_h", f.H);
            m.SetShaderParameter("fall_mat", f.Mat);
            m.SetShaderParameter("fall_lift", f.Lift);
        }
        Tear();
        GD.Print($"{log}: {Holder.Name} over to {az:F0} deg, lies at {Mathf.RadToDeg((float)Rest):F0}, "
                 + $"touches at {Mathf.RadToDeg((float)Touch):F0}");
        return true;
    }

    /// <summary>
    /// A frame of the fall: <c>tree_gen.timber</c>, which is
    /// <c>Grove.Timber</c> with the model's own crown spring - the trunk a
    /// pendulum on its root plate, <c>a'' = g sin(a)</c>, stopped at its lie
    /// and bounced; the crown trailing while it comes over, thrown on past it
    /// when it stops, and ringing down. In steps of 1/120 s, the pipeline's, so
    /// the board and the sheets play one fall. True on the frame the crown
    /// first touches the ground - the dust's.
    /// </summary>
    public bool Step(float dt)
    {
        if (!Going)
            return false;
        Data f = Of;
        const double step = 1.0 / 120.0;
        for (double left = dt; left > 1e-9; left -= step)
        {
            double h = Math.Min(step, left);
            double drag = 0.0;
            if (!Lying || Spin != 0.0)
            {
                double pull = f.Gravity * Math.Sin(Angle);
                Spin += pull * h;
                Angle += Spin * h;
                if (Angle >= Rest)
                {
                    double hit = Spin;
                    Angle = Rest;
                    Lying = true;
                    FlinchRate += f.Whip * hit;
                    Spin = Math.Abs(hit) < f.Settle ? 0.0 : -hit * f.Bounce;
                }
                else if (Angle < 0.0)
                {
                    Angle = 0.0;
                    Spin = 0.0;
                }
                drag = -f.Lag * pull;
            }
            FlinchRate += (-f.Stiffness * Flinch - f.Damping * FlinchRate + f.Stiffness * drag) * h;
            Flinch += FlinchRate * h;
        }
        bool touched = false;
        if (!Touched && Angle >= Touch)
            touched = Touched = true;
        Throw(dt);
        float down = Down;
        foreach (ShaderMaterial m in Burn)
        {
            m.SetShaderParameter("fall_trunk", (float)Angle);
            m.SetShaderParameter("fall_crown", (float)Flinch);
            m.SetShaderParameter("fall_down", down);
        }
        return touched;
    }

    /// <summary>Standing again: the crumbs and the pit gone, the pose off.</summary>
    public void Reset()
    {
        Going = Lying = Touched = false;
        foreach (Clod c in _clods)
            c.Node.QueueFree();
        _clods.Clear();
        _pit?.QueueFree();
        _pit = null;
        Angle = Spin = Flinch = FlinchRate = 0.0;
        foreach (ShaderMaterial m in Burn)
            m.SetShaderParameter("fall_on", false);
    }

    /// <summary><see cref="Toon.FallCode"/>'s pose of a model point, for what
    /// sits on the tree and is not its mesh - the fire's ports.</summary>
    public Vector3 Pose(Vector3 v)
    {
        if (!Going)
            return v;
        Data f = Of;
        float w = Mathf.Clamp((v.Y - f.Y0) / Mathf.Max(f.H - f.Y0, 1e-3f), 0.0f, 1.0f);
        w *= w;
        var d = new Vector3(Dir.X, 0.0f, Dir.Y);
        Vector3 k = Vector3.Up.Cross(d).Normalized();
        Vector3 piv = d * f.Hinge;
        return piv + (v - piv).Rotated(k, (float)(Angle + Flinch * w));
    }

    // --- the root plate's crumbs and pit -----------------------------------

    /// <summary>A crumb of earth off the root plate: it rides the plate's rim
    /// up (<see cref="Seat"/>, in the model) until the trunk is
    /// <see cref="Launch"/> over, drops off it with the rim's own speed, and
    /// lies where it lands.</summary>
    private sealed class Clod
    {
        public MeshInstance3D Node = null!;
        public Vector3 Seat, At, Prev, Vel, Axis;
        public float Launch, Rate, Size, Turned;
        public Basis Shape;
        public bool Flying, Down;
        public int Bounces;
    }

    /// <summary>
    /// Crumbs a tree drops and the pull that brings them down (board px/s²).
    ///
    /// <b>A plate crumbles, it does not burst.</b> The first go threw 16-18
    /// clods of up to 6 px off the collar at 140-240 px/s: a fountain of earth
    /// several metres out, where a tree pushed over lifts its plate and loses
    /// a few crumbs off the torn rim, onto the ground at its foot. Now there
    /// are 7, of 1-2 px, carried up on the rim of the plate as it comes out
    /// and let go between 8 and 40 degrees with its speed - they land within a
    /// metre or so of it.
    /// </summary>
    private const int Clods = 7;
    private const float ClodGravity = 520.0f;
    private static readonly Color Soil = new(0.42f, 0.32f, 0.22f);
    private static Mesh? _clodMesh;

    /// <summary>One lump for every clod: a sphere of five sides and three
    /// rings - faceted, so the cel steps break it into a lump and not a ball
    /// - on the cel look, and not inked: a crumb of 1-2 px is smaller than
    /// its own line (Toon.InkWidth), and inked it was a dark hook.</summary>
    private static Mesh ClodMesh()
    {
        if (_clodMesh is not null)
            return _clodMesh;
        var m = new MeshInstance3D
        {
            Mesh = new SphereMesh
            {
                Radius = 1.0f, Height = 2.0f, RadialSegments = 5, Rings = 3,
                Material = new StandardMaterial3D { AlbedoColor = Soil },
            },
        };
        var holder = new Node3D();
        holder.AddChild(m);
        foreach (ShaderMaterial cel in Toon.Dress(holder))
            cel.NextPass = null;
        _clodMesh = m.Mesh;
        holder.Free();
        return _clodMesh;
    }

    /// <summary>
    /// The root plate tears out: the hinge is on the side the tree goes over,
    /// so the collar's back comes up out of the ground, and what it throws is
    /// the soil there - clods off the back and the sides, up and away from
    /// the fall, launched over the first 25 degrees as the plate lifts; and
    /// under it a torn pit, growing as it goes over. Same for a burnt tree:
    /// the roots are what tear, and they did not burn.
    /// </summary>
    private void Tear()
    {
        Vector3 foot = Holder.GlobalPosition;
        var fall = new Vector3(WorldDir.X, 0.0f, WorldDir.Y);
        var back = new Vector3(-Dir.X, 0.0f, -Dir.Y);   // in the model
        float plate = Of.Plate > 0.0f ? Of.Plate : 1.4f;
        Mesh lump = ClodMesh();
        int salt = Holder.Name.ToString().GetHashCode();
        for (int k = 0; k < Clods; k++)
        {
            float h1 = CelPuff.Hash(k, salt), h2 = CelPuff.Hash(k, salt + 1), h3 = CelPuff.Hash(k, salt + 2);
            float h4 = CelPuff.Hash(k, salt + 3), h5 = CelPuff.Hash(k, salt + 4);
            // on the plate's back rim, the part that comes up, and its underside
            Vector3 seat = back.Rotated(Vector3.Up, Mathf.DegToRad((h1 * 2.0f - 1.0f) * 80.0f))
                           * (plate * (0.6f + 0.35f * h3)) + Vector3.Down * (0.05f + 0.25f * h4);
            float size = (0.05f + 0.06f * h2) * Ppm;
            var c = new Clod
            {
                Seat = seat,
                Axis = new Vector3(h1 - 0.5f, 0.6f, h3 - 0.5f).Normalized(),
                Rate = 4.0f + 6.0f * h2,
                Launch = Mathf.DegToRad(8.0f + 32.0f * h5),
                Size = size,
                Shape = Basis.Identity.Scaled(new Vector3(1.0f + 0.4f * h4, 0.7f + 0.3f * h5, 1.0f + 0.3f * h1)),
                Node = new MeshInstance3D { Mesh = lump, Visible = false },
            };
            Parent.AddChild(c.Node);
            _clods.Add(c);
        }
        _pitLook = new ShaderMaterial { Shader = PitShader };
        _pitLook.SetShaderParameter("seed", (salt & 1023) * 0.37f);
        _pit = new MeshInstance3D
        {
            Name = "Pit",
            Mesh = new PlaneMesh { Size = new Vector2(2.0f, 2.0f) },
            MaterialOverride = _pitLook,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            // where the plate came out of: the plate's round, behind the hinge
            // (cut); a hair over the ground so it is not in the ground's own plane
            Position = foot + Vector3.Up * 0.4f,
            Scale = Vector3.One * (plate * Ppm),
            Rotation = new Vector3(0.0f, Mathf.Atan2(fall.X, fall.Z), 0.0f),
        };
        _pitLook.SetShaderParameter("cut", Of.Hinge / plate);
        Parent.AddChild(_pit);
    }

    private void Throw(float dt)
    {
        _pitLook?.SetShaderParameter("grow", Mathf.SmoothStep(0.02f, 0.30f, (float)Angle));
        float ground = Holder.GlobalPosition.Y;
        foreach (Clod c in _clods)
        {
            if (!c.Flying && !c.Down)
            {
                // riding the rim up, hidden in it
                c.Prev = c.At;
                c.At = Holder.ToGlobal(Pose(c.Seat));
                if (Angle < c.Launch || c.At.Y < ground + c.Size)
                    continue;
                c.Flying = true;
                c.Node.Visible = true;
                // the rim's own speed, and a little of its own
                c.Vel = (c.At - c.Prev) / Mathf.Max(dt, 1e-4f)
                        + new Vector3(c.Axis.X, 0.3f, c.Axis.Z) * 12.0f;
            }
            if (c.Flying)
            {
                c.Vel += Vector3.Down * (ClodGravity * dt);
                c.At += c.Vel * dt;
                c.Turned += c.Rate * dt;
                float floor = ground + c.Size * 0.6f;
                if (c.At.Y <= floor && c.Vel.Y < 0.0f)
                {
                    c.At.Y = floor;
                    // one small hop, then it lies there
                    if (c.Bounces++ == 0 && c.Vel.Y < -90.0f)
                    {
                        c.Vel = new Vector3(c.Vel.X * 0.35f, -c.Vel.Y * 0.22f, c.Vel.Z * 0.35f);
                        c.Rate *= 0.4f;
                    }
                    else
                    {
                        c.Flying = false;
                        c.Down = true;
                    }
                }
            }
            c.Node.GlobalTransform = new Transform3D(
                new Basis(c.Axis, c.Turned) * c.Shape.Scaled(Vector3.One * c.Size), c.At);
        }
    }

    /// <summary>The torn pit: a disc on the ground with a ragged edge, the
    /// torn soil's rim and a darker hole, laid over the ground by
    /// multiplying it, grown by <c>grow</c> as the plate lifts.</summary>
    private static readonly Shader PitShader = new()
    {
        Code = @"
shader_type spatial;
render_mode unshaded, blend_mul, depth_draw_never, cull_disabled, shadows_disabled, fog_disabled;
uniform float grow = 0.0;
uniform float seed = 0.0;
uniform float cut = 0.3;
uniform vec3 hole : source_color = vec3(0.40, 0.31, 0.24);
uniform vec3 rim : source_color = vec3(0.68, 0.57, 0.46);
" + Toon.NoiseCode + @"
varying vec2 p;
void vertex() {
    p = VERTEX.xz;
}
void fragment() {
    // behind the hinge only (p.y is along the fall): the plate's front is
    // still in the ground, turned further under
    if (p.y > cut) discard;
    float a = atan(p.y, p.x);
    float edge = 0.80 + 0.20 * noise3(vec3(cos(a) * 2.2, sin(a) * 2.2, seed))
               + 0.10 * (noise3(vec3(cos(a) * 7.0, sin(a) * 7.0, seed + 5.0)) - 0.5);
    float r = length(p) / max(grow, 1e-3);
    if (r > edge) discard;
    ALBEDO = r < edge * 0.6 ? hole : rim;
}",
    };
}

/// <summary>
/// The ground going up where a felled crown comes down, once (the sprite's
/// rule, <c>Grove.Thud</c>): a line of puffs from where it first touches
/// (<c>touch_m</c>) out to its top, born near to far as the crown sweeps down
/// it - one cloud for every fall on the board (<see cref="CelCloud"/>).
/// </summary>
public sealed class FallDust
{
    private readonly CelCloud _cloud;
    private readonly List<(Vector3 At, Vector3 Along, Vector3 Across, float Reach, float Wide, float Tone, float Age)> _dust = new();
    private const float DustLife = 1.3f;
    private const int DustPuffs = 26;

    public FallDust(Node parent) =>
        _cloud = new CelCloud(parent, "Dust", new Color(0.80f, 0.68f, 0.50f), 4.0f);

    /// <summary>A crown down on the ground: dust along it, or ash for a
    /// <paramref name="burnt"/> one.</summary>
    public void Add(TreeFall t, bool burnt)
    {
        var along = new Vector3(t.WorldDir.X, 0.0f, t.WorldDir.Y);
        Vector3 at = t.Holder.GlobalPosition + along * ((float)t.TouchM * t.Ppm);
        _dust.Add((at, along, Vector3.Up.Cross(along).Normalized(),
                   Mathf.Max(t.Height - (float)t.TouchM, 1.0f) * t.Ppm, t.Width * t.Ppm,
                   burnt ? 0.36f : 0.66f, 0.0f));   // a burnt crown throws up ash
    }

    public void Clear()
    {
        _dust.Clear();
        _cloud.Hide();
    }

    /// <summary>A frame: <paramref name="foot"/> the ground under a point.</summary>
    public void Settle(float dt, Basis eye, Func<Vector3, Vector3> foot)
    {
        _cloud.Clear();
        for (int i = _dust.Count - 1; i >= 0; i--)
        {
            var d = _dust[i];
            d.Age += dt;
            _dust[i] = d;
            if (d.Age > DustLife + 0.5f)
            {
                _dust.RemoveAt(i);
                continue;
            }
            for (int k = 0; k < DustPuffs; k++)
            {
                float age = d.Age - 0.35f * CelPuff.Hash(k, 47);   // near to far
                if (age <= 0.0f || age >= DustLife)
                    continue;
                float a = age / DustLife;
                float h1 = CelPuff.Hash(k, 41), h2 = CelPuff.Hash(k, 43);
                // Many small puffs over the patch the crown came down on, out
                // to its sides and a little up, eaten early. Laid along it at
                // their own height they read as flat pale stones; a dozen big
                // ones flowed into one shape (CelCloud's union) and read as a
                // pale boulder in front of the crown.
                float h3 = CelPuff.Hash(k, 47);
                Vector3 at = d.At + d.Along * (d.Reach * (0.15f + 0.85f * h3))
                             + d.Across * ((h1 - 0.5f) * d.Wide * (0.9f + 0.6f * a));
                float r = d.Wide * (0.045f + 0.075f * Mathf.Sqrt(a)) * (0.7f + 0.6f * h2);
                float up = r * 0.8f + d.Wide * 0.14f * Mathf.Sqrt(a);
                _cloud.Add(foot(at) + Vector3.Up * up, r, d.Tone, h2 * 13.0f,
                           Mathf.SmoothStep(0.10f, 0.85f, a), a);
            }
        }
        if (_dust.Count == 0)
            _cloud.Hide();
        else
            _cloud.Draw(eye);
    }
}
