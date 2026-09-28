using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The game variant of a tank - <c>Models/&lt;TAG&gt;/tank.glb</c> and its sidecar
/// <c>tank.json</c>, both written by <c>pipeline/repro_kit.py export_game</c> - loaded
/// at run time and posed through the joints the sidecar names.
///
/// <b>Read off the filesystem, not imported,</b> for <see cref="AssetRoot"/>'s
/// reason: <c>Models/</c> sits beside <c>godot/</c>, and a re-export in Blender is
/// picked up by the next run with no import step.
///
/// <b>Everything is in the model's own units under a holder scaled into the
/// board's.</b> The sidecar's numbers (pivots, travel, pitch, paths) are all in
/// Blender scene units and stay that way inside; the one scale is on this node
/// (<see cref="ScaleTo"/>, chosen by the scene against its hex), so one world
/// unit is one screen pixel exactly as on <see cref="Stage3D"/> and the effects
/// sized in pixels fit it unchanged.
///
/// <b>Three facts the sidecar does not state are measured here, off the
/// meshes</b> - <see cref="HullLength"/>, <see cref="HullWidth"/> and
/// <see cref="BoreRadius"/>: what the built effects quote their sizes against,
/// and what the sprite pipeline stamps into an atlas for its tanks.
///
/// Signs are the sidecar's, and they are not guesses - see its <c>frame</c>:
/// +Y up, +Z the tank's front, +X the tank's left.
/// </summary>
public sealed partial class TankModel : Node3D
{
    public sealed class Wheel
    {
        public required Node3D Node;
        public required float Radius;
    }

    /// <summary>One side's running gear: the wheels that turn and the belt laid
    /// as <see cref="Links"/> instances of the one link the glTF carries.</summary>
    public sealed class Track
    {
        public required Node3D Node;
        /// <summary>+1 on the tank's left (+X), -1 on its right: which way a
        /// turn's skid runs this belt.</summary>
        public required float Side;
        public required List<Wheel> Wheels;
        public required MultiMeshInstance3D Links;
        public required int Count;
        public required float Pitch;
        public required Vector3[] Path;
        public required Vector3[] Slack;
        public float[] Along = Array.Empty<float>();
        public float[] SlackAlong = Array.Empty<float>();
    }

    public sealed class Piece
    {
        public required Node3D Node;
        public required Vector3 Size;
    }

    public sealed class Landing
    {
        public required float YawDeg;
        public required Vector3 At;
        public required Quaternion Rest;
    }

    public string Tag = "";
    public float PixelsPerUnit;
    public Node3D Tank = null!, Body = null!, Hull = null!,
                  Mantlet = null!, Barrel = null!, Muzzle = null!;
    /// <summary>
    /// False for a casemate (HMR): the sidecar's <c>turret</c> is null, the gun
    /// hangs on <see cref="Hull"/>, and there is nothing to turn, tip into a
    /// ring or throw onto the deck - <see cref="Turret"/> is null, the wreck's
    /// only pose is the gun's droop, and <c>toss</c> is null too. The absence is
    /// the declaration, as in the canonical scene (<c>pipeline/docs/tank-scene.md</c>,
    /// "Машина без башни"): no flag next to a turret that is there.
    /// </summary>
    public bool Turreted = true;
    public Node3D? Turret;
    public readonly List<Node3D> Exhausts = new();
    public readonly List<Track> Tracks = new();
    public readonly List<Piece> Debris = new();
    public readonly List<Landing> Landings = new();

    public Vector3 BodyRest, TurretRest;
    public float Travel;
    /// <summary>Which way the tube recoils in Mantlet's frame: back along its
    /// bore. -Z for a level gun; a mortar raised at rest recoils down and back
    /// (the sidecar's <c>recoil.back</c>; older sidecars have none and mean -Z).
    /// </summary>
    public Vector3 RecoilBack = new(0.0f, 0.0f, -1.0f);
    public float ElevMin, ElevMax;
    public float DroopDeg, CantDeg, TipPitchDeg;
    public Quaternion Tip = Quaternion.Identity;
    public Vector3 BlastAt;
    public Vector3 Size;

    /// <summary>The hull's longest and shortest horizontal axis, model units:
    /// the <c>Hull</c> mesh's own box - hull, engine deck and a casemate's
    /// fighting compartment, not the belts, the turret or the pieces that fly
    /// off. The pipeline's <c>exhaust_scale</c> measures the same thing, the
    /// larger of the hull's two ground axes.</summary>
    public float HullLength, HullWidth;

    /// <summary>How wide the tube is at its mouth, model units: the widest
    /// the <c>Barrel</c> mesh gets about the bore over its last
    /// <see cref="MouthShare"/> - the atlas measures the same thing as the
    /// barrel layer's width across the bore.</summary>
    public float BoreRadius;

    /// <summary>How much of the tube, from the mouth back, counts as the mouth.
    /// </summary>
    public const float MouthShare = 0.08f;
    public float TossThrow, TossLift, TossSpin, TossFlight, SlideShare, SlideSpin, SlideTime;

    // The pose, in the sidecar's terms. Written, then Apply()'d once a frame.
    public float Yaw;
    public float Elevation;
    public float Recoil;
    public float Pitch;
    public float Roll;
    public float Heave;
    public float Driven;
    /// <summary>How far a turn on the spot has run the belts against each
    /// other: the right belt forward by this, the left one back - positive
    /// turning left.</summary>
    public float Skid;
    public float Slackness;
    public float Droop;
    public float Cant;
    /// <summary>While set, the turret's transform in Body's frame is this and
    /// nothing else - the toss owns it.</summary>
    public Transform3D? TurretOverride;

    public static string ModelDir(string tag) => AssetRoot.Repo + "/Models/" + tag;

    /// <summary>The cel materials <see cref="Toon.Dress"/> put on the model, one
    /// per glTF material; empty when it was loaded with the glTF's own.</summary>
    public List<ShaderMaterial> Cel = new();

    /// <summary>Load <c>Models/&lt;tag&gt;/</c>, scaled so a model unit is
    /// <paramref name="pixelsPerUnit"/> world units - cel shaded and inked
    /// (<see cref="Toon"/>) unless <paramref name="toon"/> is false, which keeps
    /// the glTF's own materials.</summary>
    public static TankModel Load(string tag, float pixelsPerUnit = 1.0f, bool toon = true)
    {
        string dir = ModelDir(tag);
        var doc = new GltfDocument();
        var state = new GltfState();
        Error err = doc.AppendFromFile(dir + "/tank.glb", state);
        if (err != Error.Ok)
            throw new InvalidOperationException($"{dir}/tank.glb: {err}");
        Node scene = doc.GenerateScene(state);
        var model = new TankModel { Tag = tag, PixelsPerUnit = pixelsPerUnit, Name = "Model" };
        // Before Bind: the belt's MultiMesh takes the link's mesh as it stands.
        if (toon)
            model.Cel = Toon.Dress(scene);
        model.Scale = Vector3.One * pixelsPerUnit;
        model.AddChild(scene);
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(dir + "/tank.json"));
        model.Bind(scene, json.RootElement);
        if (toon && model.Turret is not null)
            Toon.MarkTurret(model.Turret, model.Cel);
        return model;
    }

    /// <summary>A node the glTF named, with Godot's renaming undone: a node name
    /// may not hold a dot, so <c>Exhaust.0</c> arrives as <c>Exhaust_0</c>.</summary>
    private static Node3D Find(Node root, string name)
    {
        string safe = name.Replace('.', '_');
        Node? hit = root.FindChild(safe, true, false) ?? root.FindChild(name, true, false);
        return hit as Node3D
            ?? throw new InvalidOperationException($"tank.glb has no node {name}");
    }

    private static Vector3 V3(JsonElement e) =>
        new(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle());

    private static Quaternion Q(JsonElement e) =>
        new(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle(), e[3].GetSingle());

    private static Vector3[] Points(JsonElement e)
    {
        var list = new Vector3[e.GetArrayLength()];
        int i = 0;
        foreach (JsonElement p in e.EnumerateArray())
            list[i++] = V3(p);
        return list;
    }

    private void Bind(Node scene, JsonElement j)
    {
        Tank = Find(scene, "Tank");
        Body = Find(scene, j.GetProperty("body").GetProperty("node").GetString()!);
        Hull = Find(scene, "Hull");
        Turreted = j.TryGetProperty("turret", out JsonElement tur) && tur.ValueKind == JsonValueKind.Object;
        Turret = Turreted ? Find(scene, tur.GetProperty("node").GetString()!) : null;
        JsonElement gun = j.GetProperty("gun");
        Mantlet = Find(scene, gun.GetProperty("node").GetString()!);
        ElevMin = gun.GetProperty("limits_deg")[0].GetSingle();
        ElevMax = gun.GetProperty("limits_deg")[1].GetSingle();
        JsonElement recoil = j.GetProperty("recoil");
        Barrel = Find(scene, recoil.GetProperty("node").GetString()!);
        Travel = recoil.GetProperty("travel").GetSingle();
        if (recoil.TryGetProperty("back", out JsonElement back))
            RecoilBack = V3(back).Normalized();
        Muzzle = Find(scene, j.GetProperty("muzzle").GetProperty("node").GetString()!);
        foreach (JsonElement ex in j.GetProperty("exhaust").EnumerateArray())
            Exhausts.Add(Find(scene, ex.GetProperty("node").GetString()!));
        JsonElement size = j.GetProperty("size");
        Size = new Vector3(size.GetProperty("width").GetSingle(),
                           size.GetProperty("height").GetSingle(),
                           size.GetProperty("length").GetSingle());
        BodyRest = Body.Position;
        TurretRest = Turret?.Position ?? Vector3.Zero;

        JsonElement wreck = j.GetProperty("wreck");
        DroopDeg = wreck.GetProperty("gun").GetProperty("droop_deg").GetSingle();
        // A casemate's wreck has no turret to tip: the gun droops, and only it.
        if (wreck.GetProperty("turret") is { ValueKind: JsonValueKind.Object } tw)
        {
            CantDeg = tw.GetProperty("cant_deg").GetSingle();
            TipPitchDeg = tw.GetProperty("pitch_deg").GetSingle();
            Tip = Q(tw.GetProperty("tip_quat"));
        }

        foreach (JsonProperty side in j.GetProperty("tracks").EnumerateObject())
        {
            JsonElement t = side.Value;
            var node = Find(scene, t.GetProperty("node").GetString()!);
            var wheels = new List<Wheel>();
            foreach (JsonElement w in t.GetProperty("wheels").EnumerateArray())
                wheels.Add(new Wheel { Node = Find(scene, w.GetProperty("node").GetString()!),
                                       Radius = w.GetProperty("r").GetSingle() });
            var link = (MeshInstance3D)Find(scene, t.GetProperty("link").GetString()!);
            link.Visible = false;
            int count = t.GetProperty("links").GetInt32();
            var mm = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh = link.Mesh,
                InstanceCount = count,
            };
            var inst = new MultiMeshInstance3D { Multimesh = mm, Name = "Links" };
            node.AddChild(inst);
            var track = new Track
            {
                Node = node, Side = Mathf.Sign(node.Position.X), Wheels = wheels,
                Links = inst, Count = count,
                Pitch = t.GetProperty("pitch").GetSingle(),
                Path = Points(t.GetProperty("path")),
                Slack = Points(wreck.GetProperty("tracks").GetProperty(side.Name).GetProperty("path")),
            };
            track.Along = Lengths(track.Path);
            track.SlackAlong = Lengths(track.Slack);
            Tracks.Add(track);
        }

        foreach (JsonElement d in j.GetProperty("debris").EnumerateArray())
            Debris.Add(new Piece { Node = Find(scene, d.GetProperty("node").GetString()!),
                                   Size = V3(d.GetProperty("size")) });

        if (j.GetProperty("toss") is { ValueKind: JsonValueKind.Object } toss)
        {
            TossThrow = toss.GetProperty("throw").GetSingle();
            TossLift = toss.GetProperty("lift").GetSingle();
            TossSpin = toss.GetProperty("spin_deg_per_s").GetSingle();
            TossFlight = toss.GetProperty("flight_s").GetSingle();
            JsonElement slide = toss.GetProperty("slide");
            SlideShare = slide.GetProperty("share").GetSingle();
            SlideSpin = slide.GetProperty("spin_deg").GetSingle();
            SlideTime = slide.GetProperty("s").GetSingle();
            foreach (JsonElement l in toss.GetProperty("landing").EnumerateArray())
                Landings.Add(new Landing { YawDeg = l.GetProperty("yaw_deg").GetSingle(),
                                           At = V3(l.GetProperty("at")),
                                           Rest = Q(l.GetProperty("quat")) });
        }
        else
        {
            // Nothing is thrown, but the debris still falls under the gravity a
            // turret's arc would have had: 1.5 heights in a second, as the
            // Blender preview (repro_kit clip_destroyed) lets it fall.
            TossLift = 1.5f * Size.Y;
            TossFlight = 1.0f;
        }
        BlastAt = V3(j.GetProperty("blast").GetProperty("at"));
        Apply();
        Measure();
    }

    /// <summary>A model unit is <paramref name="pixelsPerUnit"/> world units from
    /// here on.</summary>
    public void ScaleTo(float pixelsPerUnit)
    {
        PixelsPerUnit = pixelsPerUnit;
        Scale = Vector3.One * pixelsPerUnit;
    }

    /// <summary>The hull's axes and the bore's radius, off the meshes - at
    /// rest, straight after <see cref="Bind"/>, in the Hull's and the Barrel's
    /// own frames, which carry no scale: that sits on this node.</summary>
    private void Measure()
    {
        if (Hull is MeshInstance3D hull && hull.Mesh is not null)
        {
            Aabb box = hull.Mesh.GetAabb();
            HullLength = Mathf.Max(box.Size.X, box.Size.Z);
            HullWidth = Mathf.Min(box.Size.X, box.Size.Z);
        }
        else
        {
            HullLength = Size.Z;
            HullWidth = Size.X;
        }

        // The bore: the Muzzle node sits on it and points along it, in the
        // Barrel's frame. Every vertex of the tube is some way back from the
        // mouth along that axis and some way out from it.
        Vector3 mouth = Muzzle.Position;
        Vector3 along = Muzzle.Basis.Z.Normalized();
        var reach = new List<(float Back, float Out)>();
        foreach (MeshInstance3D m in MeshesUnder(Barrel))
        {
            // Walked up by hand: the model is not in the tree yet, so there is
            // no global transform to ask for.
            Transform3D to = Transform3D.Identity;
            for (Node3D at = m; at != Barrel && at.GetParent() is Node3D up; at = up)
                to = at.Transform * to;
            for (int s = 0; s < m.Mesh.GetSurfaceCount(); s++)
            {
                var arrays = m.Mesh.SurfaceGetArrays(s);
                foreach (Vector3 v in arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array())
                {
                    Vector3 d = to * v - mouth;
                    float t = d.Dot(along);
                    reach.Add((-t, (d - along * t).Length()));
                }
            }
        }
        float tube = 0.0f;
        foreach (var (back, _) in reach)
            tube = Mathf.Max(tube, back);
        float widest = 0.0f;
        foreach (var (back, out_) in reach)
            if (back <= tube * MouthShare)
                widest = Mathf.Max(widest, out_);
        BoreRadius = widest;
    }

    private static IEnumerable<MeshInstance3D> MeshesUnder(Node3D root)
    {
        if (root is MeshInstance3D self && self.Mesh is not null)
            yield return self;
        foreach (Node n in root.GetChildren())
            if (n is Node3D child)
                foreach (MeshInstance3D m in MeshesUnder(child))
                    yield return m;
    }

    private static float[] Lengths(Vector3[] path)
    {
        var along = new float[path.Length + 1];
        for (int i = 0; i < path.Length; i++)
            along[i + 1] = along[i] + path[i].DistanceTo(path[(i + 1) % path.Length]);
        return along;
    }

    /// <summary>The point <paramref name="s"/> along a closed path, arc length,
    /// wrapped.</summary>
    private static Vector3 At(Vector3[] path, float[] along, float s)
    {
        float total = along[^1];
        s = Mathf.PosMod(s, total);
        int lo = 0, hi = path.Length;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (along[mid] <= s) lo = mid; else hi = mid;
        }
        float span = along[lo + 1] - along[lo];
        float f = span > 1e-9f ? (s - along[lo]) / span : 0.0f;
        return path[lo].Lerp(path[(lo + 1) % path.Length], f);
    }

    /// <summary>Lay one side's links: link k between path(s) and
    /// path(s + pitch), s = k * pitch + distance driven, basis (+X across,
    /// outward normal, path direction) - the sidecar's <c>belt_motion</c>, on
    /// the running path blended toward the wreck's slack one.</summary>
    private void Lay(Track t, float driven)
    {
        float slack = Mathf.Clamp(Slackness, 0.0f, 1.0f);
        for (int k = 0; k < t.Count; k++)
        {
            float s = k * t.Pitch + driven;
            Vector3 a = At(t.Path, t.Along, s), b = At(t.Path, t.Along, s + t.Pitch);
            if (slack > 0.0f)
            {
                // By the same fraction of each loop, so a link keeps its place
                // on the belt as the belt sags.
                float fa = Mathf.PosMod(s, t.Along[^1]) / t.Along[^1];
                float fb = Mathf.PosMod(s + t.Pitch, t.Along[^1]) / t.Along[^1];
                a = a.Lerp(At(t.Slack, t.SlackAlong, fa * t.SlackAlong[^1]), slack);
                b = b.Lerp(At(t.Slack, t.SlackAlong, fb * t.SlackAlong[^1]), slack);
            }
            Vector3 z = (b - a).Normalized();
            // Outward is the path direction turned a quarter toward +Z in the
            // side plane: the loop runs front, down, back along the ground, up
            // and forward over the top - clockwise with +Z to the right.
            Vector3 y = new Vector3(0.0f, z.Z, -z.Y).Normalized();
            Vector3 x = y.Cross(z).Normalized();
            y = z.Cross(x);
            t.Links.Multimesh.SetInstanceTransform(k, new Transform3D(new Basis(x, y, z), (a + b) * 0.5f));
        }
    }

    /// <summary>Put every joint where the pose fields say.</summary>
    public void Apply()
    {
        Body.Position = BodyRest + new Vector3(0.0f, Heave, 0.0f);
        Body.Rotation = new Vector3(Pitch, 0.0f, Roll);
        if (Turret is null)
        {
            // a casemate: the gun is the hull's, nothing turns
        }
        else if (TurretOverride is Transform3D over)
            Turret.Transform = over;
        else
        {
            Basis yaw = new(Vector3.Up, Yaw);
            Quaternion tip = Quaternion.Identity.Slerp(Tip, Mathf.Clamp(Cant, 0.0f, 1.0f));
            Turret.Transform = new Transform3D(yaw * new Basis(tip), TurretRest);
        }
        // Elevation is from the rest pose - a mortar's rest is already raised,
        // in the geometry, not in a node's rotation.
        float elev = Mathf.Clamp(Elevation, Mathf.DegToRad(ElevMin), Mathf.DegToRad(ElevMax));
        Mantlet.Rotation = new Vector3(-elev + Droop * Mathf.DegToRad(DroopDeg), 0.0f, 0.0f);
        Barrel.Position = RecoilBack * (Recoil * Travel);
        foreach (Track t in Tracks)
        {
            float driven = Driven - t.Side * Skid;
            foreach (Wheel w in t.Wheels)
                w.Node.Rotation = new Vector3(driven / w.Radius, 0.0f, 0.0f);
            Lay(t, driven);
        }
    }

    /// <summary>The landing on the deck nearest a yaw, for the toss.</summary>
    public Landing LandingNear(float yawDeg)
    {
        Landing best = Landings[0];
        float gap = float.MaxValue;
        foreach (Landing l in Landings)
        {
            float d = Mathf.Abs(Mathf.PosMod(l.YawDeg - yawDeg + 180.0f, 360.0f) - 180.0f);
            if (d < gap) { gap = d; best = l; }
        }
        return best;
    }
}
