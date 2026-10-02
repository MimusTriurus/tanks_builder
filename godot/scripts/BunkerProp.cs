using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The hex bunker on the 3D board (docs/tank3d.md, "Бункер"): the model
/// <c>pipeline/bunker_break.py export_game</c> writes to
/// <c>assets/Models/Bunker/</c>, standing where the capon's
/// <see cref="WallProp"/> stands and broken the way the pipeline's collapse
/// breaks it. The prop draws and falls; the board's rules stay the capon's -
/// the gate, the slit, the sides the board holds, the bomb on the roof - and
/// its <see cref="WallProp"/> still runs them, unseen.
///
/// <list type="bullet">
/// <item><b>Standing it is the model as built</b> (<c>Intact_*</c>) on its
/// pad (<c>Foundation</c>); the chunks (<c>Debris_*</c>) are in the file at
/// rest and hidden. Broken, the two swap: the chunks at rest look like the
/// bunker - their bevels are the bunker's seams only - so the swap does not
/// show under the bomb's burst.</item>
/// <item><b>It falls in, not out</b>: a wall that stands on the cell's edge
/// and falls outward can only land on the next cell. The roof heaves and
/// drops in, each wall folds inward about its inner foot as one rigid motion,
/// the bedded course stays, and an unseen fence on the cell's edges keeps
/// whatever rolls off the heap on the bunker's own hex. The numbers are the
/// sidecar's, Blender's, carried into the solver's metres
/// (<see cref="WallRig.MetresPerCell"/>): lengths by it, times and speeds by
/// its root - the same fall at the same gravity on a bigger bunker.</item>
/// <item><b>A tank in it is in the way</b>: a kinematic hull and turret from
/// the model's own meshes (not its gun, not its aerial), following the tank,
/// so the roof comes down on the turret and what lies on the deck goes when
/// the tank drives off.</item>
/// <item><b>Then it goes</b>: each chunk, still for <see cref="WallRig.Linger"/>
/// seconds, is eaten away over <see cref="WallRig.Crumble"/> - the cel pass's
/// burn (<see cref="Toon.BurnCode"/>, <c>burn_eat</c>) with a pale dust rim
/// and no embers - and the pad is what stays: four millimetres proud of the
/// ground, it stops nothing.</item>
/// </list>
/// </summary>
public sealed partial class BunkerProp : Node3D
{
    /// <summary>The collision bit the bunker's bodies live on: the walls'
    /// rigs count up from 0 (<see cref="WallRig.Channel"/>).</summary>
    public const int Bit = 30;

    /// <summary>Each wall's own bit while it folds, faces 0-5 from here.</summary>
    public const int FaceBit = 24;

    private const uint Faces = 0x3fu << FaceBit;
    private static uint Group => 1u << Bit;

    /// <summary>How long after the last wall is let go the walls meet each
    /// other again, seconds.
    ///
    /// <b>A ring of rigid walls cannot fold in.</b> Its inner chord is shorter
    /// than its outer one, so a wall turning in about its foot drives its end
    /// into its neighbour's side of the corner - measured: every wall stood,
    /// touching both neighbours, while only the roof fell in. Concrete crushes
    /// at the corners; rigid chunks lock as an arch. So for the fold each wall
    /// meets the ground, the fence, the roof, the bedded course and the tank,
    /// but not the other walls, and the corners go through each other under
    /// the bomb's smoke; then they are one heap again.</summary>
    private const float MergeAfter = 1.5f;

    public static string GlbPath => AssetRoot.Bunker + "/bunker.glb";
    public static string JsonPath => AssetRoot.Bunker + "/bunker.json";
    public static bool OnDisk => File.Exists(GlbPath) && File.Exists(JsonPath);

    /// <summary>The sidecar's sizes, for the capon's recipe to match the
    /// model: roof underside and top, the slit.</summary>
    public readonly record struct Sizes(float RoofUnder, float Height, float SlitLow, float SlitHigh);

    public static Sizes? ReadSizes()
    {
        if (!OnDisk)
            return null;
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(JsonPath));
        JsonElement s = doc.RootElement.GetProperty("size");
        JsonElement slit = s.GetProperty("slit");
        return new Sizes(s.GetProperty("roof_under").GetSingle(), s.GetProperty("height").GetSingle(),
                         slit[0].GetSingle(), slit[1].GetSingle());
    }

    /// <summary>A tank in the box: its frame in the world and its points in
    /// that frame's turn, board px - the hull and the turret apart.</summary>
    public sealed class Occupant
    {
        public required Node3D Frame;
        public required List<Vector3[]> Parts;
    }

    private sealed class Chunk
    {
        public required string Node;
        public required string Kind;
        public int Face = -1;
        public Vector3 At;
        public Vector3[] Hull = Array.Empty<Vector3>();
        public float Volume;
        public Node3D? View;
        public Transform3D Rest;
        public RigidBody3D? Body;
        public readonly List<ShaderMaterial> Burn = new();
        /// <summary>Each cel copy and the ink pass it carries.</summary>
        public readonly List<(ShaderMaterial Cel, ShaderMaterial Ink)> Ink = new();
        public float Release;
        public Vector3 V, W;
        public bool Free;
        public float Still;
        public float Going = -1.0f;
        public bool Gone;
    }

    public required float Radius { get; init; }
    public required Vector3 Anchor { get; init; }
    public required float Lay { get; init; }
    public required Vector2I Cell { get; init; }
    public bool Pbr { get; init; }

    private Node3D _view = null!;
    private Node3D? _scene;
    private readonly List<Node3D> _intact = new();
    private readonly List<Chunk> _chunks = new();
    private readonly List<(Vector3 Normal, Vector3 Foot, bool Gate)> _faces = new();
    private JsonElement _collapse;
    private float _fenceReach = 1.005f, _fenceHigh = 2.4f, _fenceThick = 0.25f, _fenceOver = 1.12f;
    private readonly List<Node> _static = new();
    private readonly List<(AnimatableBody3D Body, Node3D Frame)> _tanks = new();
    private float _t = -1.0f;
    private float _merge = -1.0f;
    private string _problem = "";

    private static float M => WallRig.MetresPerCell;

    /// <summary>The second wave of walls waits this much longer, seconds of
    /// the sidecar's clock: the first wave is down before its neighbours go.</summary>
    private const float WaveGap = 0.12f;

    /// <summary>The bodies' hulls, as a share of the drawn ones.</summary>
    private const float HullShrink = 0.985f;

    /// <summary>A chunk's own friction (the ground's is the sidecar's).</summary>
    private const float ChunkFriction = 0.6f;
    /// <summary>Blender's fall at g = 9.81 R/s^2 is this one at 9.8 m/s^2 with
    /// R = <see cref="M"/> metres when times stretch by this and speeds in
    /// m/s are Blender's R/s times it.</summary>
    private static float K => Mathf.Sqrt(M);

    public bool Built => _scene is not null;
    public bool Broken => _t >= 0.0f;
    public int Pieces => _chunks.Count;
    public int Gone { get; private set; }
    /// <summary>What went wrong reading the files, or empty.</summary>
    public string Problem => _problem;

    /// <summary>Read the files, dress the model, stand it on the cell.</summary>
    public bool Build()
    {
        var doc = new GltfDocument();
        var state = new GltfState();
        Godot.Error err = doc.AppendFromFile(GlbPath, state);
        if (err != Godot.Error.Ok)
        {
            _problem = $"{GlbPath}: {err}";
            return false;
        }
        _scene = (Node3D)doc.GenerateScene(state);
        if (!Pbr)
            Toon.Dress(_scene);
        _view = new Node3D
        {
            Name = "View",
            Transform = new Transform3D(new Basis(Vector3.Up, Lay) * Basis.FromScale(Vector3.One * Radius), Anchor),
        };
        AddChild(_view);
        _view.AddChild(_scene);

        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(JsonPath));
        JsonElement root = json.RootElement;
        _collapse = root.GetProperty("collapse").Clone();
        JsonElement fence = root.GetProperty("fence");
        _fenceReach = fence.GetProperty("reach").GetSingle();
        _fenceHigh = fence.GetProperty("height").GetSingle();
        _fenceThick = fence.GetProperty("thickness").GetSingle();
        _fenceOver = fence.GetProperty("overlap").GetSingle();
        foreach (JsonElement f in root.GetProperty("faces").EnumerateArray())
            _faces.Add((Vec(f.GetProperty("normal")), Vec(f.GetProperty("inner_foot")), f.GetProperty("gate").GetBoolean()));

        foreach (Node n in _scene.GetChildren())
            if (n is Node3D n3 && n3.Name.ToString().StartsWith("Intact_", StringComparison.Ordinal))
                _intact.Add(n3);
        foreach (JsonElement c in root.GetProperty("chunks").EnumerateArray())
        {
            var hull = new List<Vector3>();
            foreach (JsonElement p in c.GetProperty("hull").EnumerateArray())
                hull.Add(Vec(p));
            var chunk = new Chunk
            {
                Node = c.GetProperty("node").GetString()!,
                Kind = c.GetProperty("kind").GetString()!,
                Face = c.GetProperty("face").ValueKind == JsonValueKind.Number ? c.GetProperty("face").GetInt32() : -1,
                At = Vec(c.GetProperty("at")),
                Hull = hull.ToArray(),
                Volume = c.GetProperty("volume").GetSingle(),
            };
            chunk.View = _scene.FindChild(chunk.Node, recursive: true, owned: false) as Node3D;
            if (chunk.View is null)
            {
                _problem = $"{GlbPath}: no node {chunk.Node}";
                continue;
            }
            chunk.Rest = chunk.View.Transform;
            chunk.View.Visible = false;
            if (!Pbr)
                Fadeable(chunk);
            _chunks.Add(chunk);
        }
        return _problem.Length == 0;
    }

    private static Vector3 Vec(JsonElement a) => new(a[0].GetSingle(), a[1].GetSingle(), a[2].GetSingle());

    private float C(string key) => _collapse.GetProperty(key).GetSingle();

    /// <summary>Its own copy of every cel material on the chunk, set to be
    /// eaten (the burn's role 1 with <c>burn_eat</c>, <c>UV2.x</c> nought
    /// over the whole chunk): <c>burn</c> -1 whole, 0 gone.</summary>
    private static void Fadeable(Chunk chunk)
    {
        foreach (MeshInstance3D m in Meshes(chunk.View!))
        {
            if (m.Mesh is null)
                continue;
            for (int s = 0; s < m.Mesh.GetSurfaceCount(); s++)
            {
                if (m.Mesh.SurfaceGetMaterial(s) is not ShaderMaterial cel)
                    continue;
                var copy = (ShaderMaterial)cel.Duplicate(true);
                if (copy.NextPass is ShaderMaterial ink)
                    chunk.Ink.Add((copy, ink));
                foreach (ShaderMaterial pass in copy.NextPass is ShaderMaterial i2
                             ? new[] { copy, i2 } : new[] { copy })
                {
                    pass.SetShaderParameter("burn_role", 1);
                    pass.SetShaderParameter("burn_eat", true);
                    pass.SetShaderParameter("burn_window", 1.0f);
                    pass.SetShaderParameter("burn", -1.0f);
                    // holes a third of a chunk across, not freckles: at 7 px
                    // the going chunk read as camouflage, black and white
                    pass.SetShaderParameter("burn_grain", 18.0f);
                    // crumbling, not burning: a narrow pale rim, no embers
                    pass.SetShaderParameter("char_tone", new Color(0.70f, 0.68f, 0.64f));
                    pass.SetShaderParameter("char_cover", 0.35f);
                    pass.SetShaderParameter("ember_tone", new Color(0.0f, 0.0f, 0.0f));
                    chunk.Burn.Add(pass);
                }
                m.SetSurfaceOverrideMaterial(s, copy);
            }
        }
    }

    private static IEnumerable<MeshInstance3D> Meshes(Node node)
    {
        if (node is MeshInstance3D m)
            yield return m;
        foreach (Node c in node.GetChildren())
            foreach (MeshInstance3D x in Meshes(c))
                yield return x;
    }

    /// <summary>A world point in the prop's frame, metres.</summary>
    private Vector3 Local(Vector3 world) => new Basis(Vector3.Up, -Lay) * ((world - Anchor) / Radius) * M;

    /// <summary>
    /// The bomb has come down on the roof going <paramref name="way"/> (a world
    /// direction): the chunks in for the bunker, the bodies raised, the pushes
    /// keyed - the hit slab and the hatch now, the roof after the fuse, the
    /// walls after it in two waves, the bedded course last.
    /// </summary>
    public void Break(Vector3 way, IReadOnlyList<Occupant> occupants)
    {
        if (!Built || Broken)
            return;
        _t = 0.0f;
        Gone = 0;
        foreach (Node3D n in _intact)
            n.Visible = false;
        uint bit = Group, all = Group | Faces;
        var rough = new PhysicsMaterial { Friction = C("friction"), Bounce = C("restitution") };
        // chunk on chunk slides more than chunk on ground (Jolt takes the
        // geometric mean of the two): concrete faces broken apart grind past
        var slick = new PhysicsMaterial { Friction = ChunkFriction, Bounce = C("restitution") };

        // floor: a box whose top is the cell's ground
        var floor = new StaticBody3D { Name = "Floor", CollisionLayer = bit, CollisionMask = all, PhysicsMaterialOverride = rough };
        floor.AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(40.0f, 2.0f, 40.0f) * M },
            Position = new Vector3(0.0f, -1.0f * M, 0.0f),
        });
        AddChild(floor);
        _static.Add(floor);
        // the fence on the cell's six edges (brick_wall.hex_fence)
        float inner = Mathf.Sqrt(3.0f) * 0.5f * _fenceReach;
        foreach ((Vector3 n, Vector3 _, bool _) in _faces)
        {
            var fence = new StaticBody3D { Name = "Fence", CollisionLayer = bit, CollisionMask = all, PhysicsMaterialOverride = rough };
            Vector3 along = Vector3.Up.Cross(n);
            fence.Transform = new Transform3D(new Basis(along, Vector3.Up, n),
                                              (n * (inner + _fenceThick * 0.5f) + Vector3.Up * (_fenceHigh * 0.5f - 0.05f)) * M);
            fence.AddChild(new CollisionShape3D
            {
                Shape = new BoxShape3D { Size = new Vector3(_fenceOver, _fenceHigh, _fenceThick) * M },
            });
            AddChild(fence);
            _static.Add(fence);
        }
        // a tank in the box: kinematic, following it
        foreach (Occupant o in occupants)
        {
            var body = new AnimatableBody3D { CollisionLayer = bit, CollisionMask = all, SyncToPhysics = false };
            foreach (Vector3[] part in o.Parts)
            {
                if (part.Length < 4)
                    continue;
                var pts = new Vector3[part.Length];
                for (int i = 0; i < part.Length; i++)
                    pts[i] = part[i] / Radius * M;
                body.AddChild(new CollisionShape3D { Shape = new ConvexPolygonShape3D { Points = pts } });
            }
            AddChild(body);
            _static.Add(body);
            _tanks.Add((body, o.Frame));
        }
        FollowTanks();

        // the chunks as bodies, frozen where they stand
        float density = C("density");
        foreach (Chunk c in _chunks)
        {
            c.View!.Transform = c.Rest;
            c.View.Visible = true;
            // a hair inside the drawn hull: laid face to face, neighbours
            // touch on every side and the solver holds them there
            var pts = new Vector3[c.Hull.Length];
            for (int i = 0; i < pts.Length; i++)
                pts[i] = c.Hull[i] * HullShrink * M;
            bool wall = c.Kind == "wall" && c.Face >= 0;
            uint own = wall ? 1u << (FaceBit + c.Face) : bit;
            var body = new RigidBody3D
            {
                CollisionLayer = own, CollisionMask = wall ? bit | own : all,
                Mass = Mathf.Max(c.Volume * M * M * M * density, 1.0f),
                PhysicsMaterialOverride = slick,
                LinearDamp = _collapse.GetProperty("damping")[0].GetSingle(),
                AngularDamp = _collapse.GetProperty("damping")[1].GetSingle(),
                ContinuousCd = true,
                Freeze = true,
                FreezeMode = RigidBody3D.FreezeModeEnum.Kinematic,
                Transform = new Transform3D(c.Rest.Basis.Orthonormalized(), c.At * M),
            };
            body.AddChild(new CollisionShape3D { Shape = new ConvexPolygonShape3D { Points = pts } });
            body.Name = c.Node;
            AddChild(body);
            c.Body = body;
            c.Free = false;
            c.Still = 0.0f;
            c.Going = -1.0f;
            c.Gone = false;
            SetBurn(c, -1.0f);
        }
        Pushes(new Basis(Vector3.Up, -Lay) * way.Normalized());
        _merge = MergeAfter;
        foreach (Chunk c in _chunks)
            if (c.Kind == "wall")
                _merge = Mathf.Max(_merge, c.Release * K + MergeAfter);
        GD.Print($"bunker {Cell}: broken - {_chunks.Count} chunks, {occupants.Count} tank(s) inside");
    }

    /// <summary>When each chunk goes and how: the pipeline's collapse
    /// (<c>bunker_break.STYLES["collapse"]</c>), Blender's numbers kept here
    /// in Blender's units and carried over on release.</summary>
    private void Pushes(Vector3 shot)
    {
        int fps = _collapse.GetProperty("fps").GetInt32();
        float fuse = _collapse.GetProperty("fuse_frames").GetInt32() / (float)fps;
        JsonElement delays = _collapse.GetProperty("wall_delays_frames");
        float d0 = delays[0].GetInt32() / (float)fps, d1 = delays[1].GetInt32() / (float)fps;
        float jitter = C("jitter");
        var rng = new RandomNumberGenerator { Seed = (ulong)(Cell.X * 7919 + Cell.Y * 104729 + 3) };
        Vector3 Jit() => new(rng.RandfRange(-1, 1), rng.RandfRange(-1, 1), rng.RandfRange(-1, 1));

        var wall = new Dictionary<int, (Vector3 V, Vector3 W, Vector3 Pivot, float When)>();
        var ring = new Dictionary<int, (Vector3 V, Vector3 W)>();
        for (int k = 0; k < _faces.Count; k++)
        {
            (Vector3 n, Vector3 foot, bool gate) = _faces[k];
            if (!gate)
            {
                float f = 1.0f + jitter * rng.RandfRange(-1, 1);
                // in at the foot, and the top folding in about the inner foot
                Vector3 v = -n * C("wall_in") * f + Vector3.Up * 0.05f;
                Vector3 w = -Vector3.Up.Cross(n) * C("topple_in") * f;
                // Two waves by the face's parity: neighbours round the ring
                // always differ in it. Counted down the list instead, faces 0
                // and 5 - neighbours - folded together, met at their corner
                // and locked the ring as an arch: nothing fell.
                wall[k] = (v, w, foot, fuse + (k % 2 == 0 ? d0 : d1 + WaveGap));
            }
            ring[k] = (Vector3.Up * C("ring_heave") * rng.RandfRange(0.7f, 1.3f) - n * C("ring_in"),
                       Jit() * C("ring_spin"));
        }
        Vector3 side = new Vector3(shot.X, 0.0f, shot.Z);
        side = side.LengthSquared() > 1e-8f ? side.Normalized() : Vector3.Zero;
        foreach (Chunk c in _chunks)
        {
            switch (c.Kind)
            {
                case "core":
                    // down through the roof, leaning the way the bomb came: the
                    // step the board hands over is near level, and taken as it
                    // is it threw the slab out sideways at 5 m/s
                    c.V = (Vector3.Down * 0.9f + side * 0.35f).Normalized() * C("core_speed") + Jit() * 0.3f;
                    c.W = Jit() * 2.0f;
                    c.Release = 0.0f;
                    break;
                case "hatch":
                    c.V = -side * C("hatch_side") + Vector3.Up * C("hatch_up") + Jit() * 0.1f;
                    c.W = Jit() * 9.0f;
                    c.Release = 0.0f;
                    break;
                case "ring":
                    (c.V, c.W) = ring[c.Face];
                    c.Release = fuse;
                    break;
                case "plinth":
                    // the bedded course stays: a kerb the heap lies in
                    c.V = Jit() * 0.02f;
                    c.W = Vector3.Zero;
                    c.Release = fuse + Mathf.Max(d0, d1);
                    break;
                default:
                    (Vector3 v, Vector3 w, Vector3 pivot, float when) = wall[c.Face];
                    c.V = v + w.Cross(c.At - pivot) + Jit() * 0.06f;
                    c.W = w;
                    c.Release = when;
                    break;
            }
        }
    }

    private void FollowTanks()
    {
        foreach ((AnimatableBody3D body, Node3D frame) in _tanks)
        {
            if (!IsInstanceValid(frame))
                continue;
            Transform3D t = frame.GlobalTransform;
            body.Transform = new Transform3D(new Basis(Vector3.Up, -Lay) * t.Basis.Orthonormalized(), Local(t.Origin));
        }
    }

    /// <summary>How far into its going the chunk is, -1 whole to 0 gone.
    /// The outline goes as the eating starts (WallStack's rule: the line leaves
    /// before the mass). The ink is an inside-out shell and draws its back
    /// faces, so through a hole in the paint its far wall showed as a black
    /// spot - eaten by the noise at its own place, not the hole's - and thinned
    /// to nothing it still did.</summary>
    private static void SetBurn(Chunk c, float burn)
    {
        foreach (ShaderMaterial m in c.Burn)
            m.SetShaderParameter("burn", burn);
        bool inked = burn <= -1.0f;
        foreach ((ShaderMaterial cel, ShaderMaterial ink) in c.Ink)
            cel.NextPass = inked ? ink : null;
    }

    /// <summary>A frame of the fall: what is due let go, the views on the
    /// bodies, and the clean-up's clocks.</summary>
    public void Tick(float dt)
    {
        if (!Broken)
            return;
        _t += dt;
        FollowTanks();
        foreach (Chunk c in _chunks)
        {
            if (c.Body is null || c.Gone)
                continue;
            if (!c.Free && _t >= c.Release * K)
            {
                c.Free = true;
                c.Body.Freeze = false;
                c.Body.LinearVelocity = c.V * K;
                c.Body.AngularVelocity = c.W / K;
            }

            Transform3D b = c.Body.Transform;
            c.View!.Transform = new Transform3D(b.Basis, b.Origin / M);
            if (!c.Free || !WallRig.Crumbles)
                continue;
            if (c.Going < 0.0f)
            {
                // time spent still, never reset (WallRig.Left): a chunk on a
                // tank never sleeps, so the speed is asked, not the flag
                if (c.Body.LinearVelocity.Length() < WallRig.Still && c.Body.AngularVelocity.Length() < 0.2f)
                    c.Still += dt;
                if (c.Still >= WallRig.Linger)
                    c.Going = 0.0f;
                continue;
            }
            c.Going += dt / WallRig.Crumble;
            if (Pbr)
                c.View.Scale = Vector3.One * Mathf.Max(1.0f - c.Going, 0.01f);
            else
                SetBurn(c, Mathf.Min(c.Going, 1.0f) - 1.0f);
            if (c.Going >= 1.0f)
            {
                c.Gone = true;
                c.View.Visible = false;
                // the body stays, still: what lay on it does not fall for it
                c.Body.Freeze = true;
                Gone++;
            }
        }
        if (_merge > 0.0f && _t >= _merge)
        {
            // one heap again: every wall meets every other
            foreach (Chunk c in _chunks)
                if (c.Body is not null)
                    c.Body.CollisionMask = Group | Faces;
            _merge = -1.0f;
        }
        if (_t >= SettleAt && _t - dt < SettleAt)
            Report();
        if (Gone == _chunks.Count && _chunks.Count > 0 && _static.Count > 0)
        {
            Clear();
            GD.Print($"bunker {Cell}: gone, the pad stays");
        }
    }

    /// <summary>When the heap is measured, seconds after the bomb.</summary>
    private const float SettleAt = 5.0f;

    /// <summary>The heap, once: what moved, how far down it came, and whether
    /// it kept to its hex - the farthest chunk's middle against the cell's
    /// sides (inradius 0.866 R).</summary>
    private void Report()
    {
        int moved = 0, off = 0;
        float drop = 0.0f, worst = -1.0f, top = 0.0f;
        foreach (Chunk c in _chunks)
        {
            if (c.Body is null)
                continue;
            Vector3 p = c.Body.Position / M;
            if ((p - c.At).Length() * M > 0.3f)
                moved++;
            drop += c.At.Y - p.Y;
            top = Mathf.Max(top, p.Y);
            float out_ = -1.0f;
            foreach ((Vector3 n, Vector3 _, bool _) in _faces)
                out_ = Mathf.Max(out_, n.Dot(new Vector3(p.X, 0.0f, p.Z)) - Mathf.Sqrt(3.0f) * 0.5f);
            worst = Mathf.Max(worst, out_);
            if (out_ > 0.0f)
                off++;
        }
        GD.Print($"bunker {Cell}: at {SettleAt:F0} s {moved} of {_chunks.Count} chunks moved, "
                 + $"down {drop / Mathf.Max(_chunks.Count, 1):F2} R on average, heap top {top:F2} R, "
                 + $"{off} off the hex (worst {worst:+0.00;-0.00} R past its side)");
    }

    private void Clear()
    {
        foreach (Node n in _static)
            n.QueueFree();
        _static.Clear();
        _tanks.Clear();
        foreach (Chunk c in _chunks)
        {
            c.Body?.QueueFree();
            c.Body = null;
        }
    }

    /// <summary>Stood up again - <c>Backspace</c>.</summary>
    public void Reset()
    {
        Clear();
        _t = -1.0f;
        Gone = 0;
        foreach (Node3D n in _intact)
            n.Visible = true;
        foreach (Chunk c in _chunks)
        {
            if (c.View is null)
                continue;
            c.View.Transform = c.Rest;
            c.View.Visible = false;
            c.Free = false;
            c.Gone = false;
            c.Going = -1.0f;
            c.Still = 0.0f;
            SetBurn(c, -1.0f);
        }
    }

    /// <summary>
    /// A tank's points for <see cref="Break"/>: its meshes in its model root's
    /// turn, board px, hull and turret apart, without the gun (it is in the
    /// slit, and a hull round it would hold the lintel up) and cut at the
    /// turret's roof (the aerial would stand in the roof as a spike) -
    /// voxelled to a few hundred points each.
    /// </summary>
    public static Occupant Occupy(TankModel model)
    {
        Node3D frame = model.Tank;
        Transform3D inv = new Transform3D(frame.GlobalTransform.Basis.Orthonormalized(), frame.GlobalTransform.Origin).AffineInverse();
        var gun = new HashSet<Node>();
        Mark(model.Barrel, gun);
        var turret = new HashSet<Node>();
        if (model.Turret is not null)
            Mark(model.Turret, turret);
        var hull = new List<Vector3>();
        var top = new List<Vector3>();
        foreach (MeshInstance3D m in Meshes(frame))
        {
            if (gun.Contains(m) || m.Mesh is null)
                continue;
            Transform3D to = inv * m.GlobalTransform;
            List<Vector3> into = turret.Contains(m) ? top : hull;
            for (int s = 0; s < m.Mesh.GetSurfaceCount(); s++)
                foreach (Vector3 v in m.Mesh.SurfaceGetArrays(s)[(int)Mesh.ArrayType.Vertex].AsVector3Array())
                    into.Add(to * v);
        }
        // the roof: the highest slab of points wider than an aerial
        var all = new List<Vector3>(hull);
        all.AddRange(top);
        float cap = Cap(all, model.HullLength * model.PixelsPerUnit);
        return new Occupant { Frame = frame, Parts = new List<Vector3[]> { Voxel(hull, cap), Voxel(top, cap) } };
    }

    private static void Mark(Node n, HashSet<Node> into)
    {
        into.Add(n);
        foreach (Node c in n.GetChildren())
            Mark(c, into);
    }

    /// <summary>The turret's roof under its aerial: going down in steps of a
    /// hundredth of the hull, the first one whose points are wider than a
    /// tenth of it.</summary>
    private static float Cap(List<Vector3> pts, float hull)
    {
        if (pts.Count == 0)
            return 0.0f;
        float hi = float.MinValue, lo = float.MaxValue;
        foreach (Vector3 p in pts)
        {
            hi = Mathf.Max(hi, p.Y);
            lo = Mathf.Min(lo, p.Y);
        }
        float step = hull * 0.01f;
        for (float h = hi; h > lo; h -= step * 0.5f)
        {
            float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
            int n = 0;
            foreach (Vector3 p in pts)
                if (p.Y <= h && p.Y > h - step)
                {
                    x0 = Mathf.Min(x0, p.X); x1 = Mathf.Max(x1, p.X);
                    z0 = Mathf.Min(z0, p.Z); z1 = Mathf.Max(z1, p.Z);
                    n++;
                }
            if (n >= 3 && Mathf.Max(x1 - x0, z1 - z0) > hull * 0.08f)
                return h;
        }
        return hi;
    }

    private static Vector3[] Voxel(List<Vector3> pts, float cap)
    {
        var seen = new Dictionary<Vector3I, Vector3>();
        // a hull needs its extremes, not its rivets: a few hundred points
        float cell = 5.0f;
        foreach (Vector3 p in pts)
        {
            if (p.Y > cap + 0.5f)
                continue;
            var key = new Vector3I(Mathf.FloorToInt(p.X / cell), Mathf.FloorToInt(p.Y / cell), Mathf.FloorToInt(p.Z / cell));
            seen.TryAdd(key, p);
        }
        return new List<Vector3>(seen.Values).ToArray();
    }
}
