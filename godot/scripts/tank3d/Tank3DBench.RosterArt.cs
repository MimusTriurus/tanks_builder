using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The models' pictures (docs/hud3d-plan.md, «Ростер», 2026-10-30): each
/// unit's own model, rendered once in a viewport of its own - the same glTF,
/// cel shading and ink as on the board, in its side's paint, turned
/// three-quarters to the camera at the board's pitch. The squad's screens draw
/// them (<c>Tank3DBench.Quick</c>: the old build's cards, the hangar's stand-ins);
/// the match's roster drew them too until 2026-11-03 and shows the class's octagon
/// now, as the hangar's tabs do (<c>Tank3DBench.Roster</c>).
/// <list type="bullet">
/// <item><b>Kept on disk</b> (<c>user://roster_art</c>, <see cref="RosterCachePath"/>):
/// rendered again only when the model's files, the camera's pitch or the
/// render's own settings change.</item>
/// <item><b>Once per model and side</b>, a frame each, then the viewport
/// goes: what the icon draws is a plain texture. A tank on fire or knocked
/// out is not re-rendered - its state is the token in the icon's corner.</item>
/// <item><b>Framed one by one</b> to fill the icon: the class is told by its
/// shape and the label under it, not by its size.</item>
/// <item>Until a picture is in, and for a model that fails to load, the icon
/// draws the class's silhouette (<c>RosterTank</c>).</item>
/// </list>
/// </summary>
public sealed partial class Tank3DBench
{
    /// <summary>The render's side, px: twice the icon, drawn down to it.</summary>
    private const int RosterArtPx = 128;
    /// <summary>The model's turn towards the camera, degrees: front and left flank.</summary>
    private const float RosterArtYaw = 35.0f;

    /// <summary>The hangar's view, high, in the model's body heights swept round
    /// (<see cref="RosterScene"/> with <c>even</c>): a tenth under it and 0.15 over it -
    /// the guns swung round reach 0.09 past the body either way (TD, the longest).</summary>
    private const float RosterEven = 1.25f;

    private readonly Dictionary<(string Tag, Side Side), Texture2D> _rosterArt = new();

    /// <summary>An even live view's turntable axis on the ground, as shares of the
    /// view's size (<see cref="RosterScene"/>); its box carries it too (QuickStage).</summary>
    private static readonly StringName RosterAxisMeta = "q_axis";

    /// <summary>One model's picture in a side's paint: off the disk, else
    /// rendered and kept, and into <see cref="_rosterArt"/>; null if the model
    /// does not load. The quick battle's build screen asks it too
    /// (<c>Tank3DBench.Quick</c>).</summary>
    private async System.Threading.Tasks.Task<(Texture2D Art, bool Cached)?> RosterArtOf(string tag, Side side)
    {
        if (_rosterArt.TryGetValue((tag, side), out Texture2D? had))
            return (had, true);
        string path = RosterCachePath(tag, side);
        (Texture2D, bool)? art = null;
        if (path.Length > 0 && FileAccess.FileExists(path) && Image.LoadFromFile(path) is { } kept && !kept.IsEmpty())
            art = (ImageTexture.CreateFromImage(kept), true);
        else if (await RosterRender(tag, side) is { } made)
        {
            art = (ImageTexture.CreateFromImage(made), false);
            if (path.Length > 0)
                RosterCacheWrite(path, tag, side, made);
        }
        if (art is { } done)
            _rosterArt[(tag, side)] = done.Item1;
        return art;
    }

    /// <summary>Bumped by hand when the picture changes for a reason the
    /// model's files do not show - the framing, the light, the cel look.</summary>
    private const int RosterArtVersion = 1;
    private const string RosterCacheDir = "user://roster_art";

    /// <summary>
    /// Where a render is kept on disk: one file per model and side, its name
    /// stamped with what made it - the model's <c>tank.glb</c> and
    /// <c>tank.json</c> as last written, the camera's pitch, the turn, the
    /// size, cel or PBR and <see cref="RosterArtVersion"/>. A model the
    /// pipeline exports again gets a new name and is rendered afresh. Empty
    /// when the model's files are not there.
    /// </summary>
    private string RosterCachePath(string tag, Side side)
    {
        string dir = TankModel.ModelDir(tag);
        ulong glb = FileAccess.GetModifiedTime(dir + "/tank.glb"), json = FileAccess.GetModifiedTime(dir + "/tank.json");
        if (glb == 0)
            return "";
        // GD.Hash, not System.HashCode: that one is seeded afresh in every
        // process, and a cache keyed by it was never found again
        string stamp = $"{glb}|{json}|{Mathf.RoundToInt(Mathf.RadToDeg(Mathf.Asin(Squash)) * 100.0f)}"
                       + $"|{RosterArtYaw}|{RosterArtPx}|{_pbr}|{RosterArtVersion}";
        return $"{RosterCacheDir}/{tag}_{side}_{GD.Hash(stamp):x8}.png";
    }

    /// <summary>The render on disk, and the same model and side's older ones gone.</summary>
    private static void RosterCacheWrite(string path, string tag, Side side, Image image)
    {
        DirAccess.MakeDirRecursiveAbsolute(RosterCacheDir);
        string prefix = $"{tag}_{side}_";
        string name = path[(RosterCacheDir.Length + 1)..];
        foreach (string old in DirAccess.GetFilesAt(RosterCacheDir))
            if (old.StartsWith(prefix, StringComparison.Ordinal) && old != name)
                DirAccess.RemoveAbsolute($"{RosterCacheDir}/{old}");
        Error err = image.SavePng(path);
        if (err != Error.Ok)
            Log.Print($"tank3d: roster: {path}: {err}");
    }

    private async System.Threading.Tasks.Task<Image?> RosterRender(string tag, Side side)
    {
        if (RosterScene(tag, side, new Vector2I(RosterArtPx, RosterArtPx), this, live: false) is not var (view, _))
            return null;

        // drawn on the next frame; read back once it has been
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Image? image = view.GetTexture()?.GetImage();
        view.QueueFree();
        if (image is null || image.IsEmpty())
            return null;
        // cut to what was drawn: the box frames the model loosely (antennae,
        // the gun's reach), the icon wants the tank edge to edge
        Rect2I used = image.GetUsedRect();
        if (used.Size.X > 0 && used.Size.Y > 0)
            image = image.GetRegion(used.Grow(1).Intersection(new Rect2I(Vector2I.Zero, image.GetSize())));
        return image;
    }

    /// <summary>
    /// One model alone in a viewport of its own under <paramref name="parent"/>
    /// (it must be in the tree: the framing reads the placed meshes): the
    /// board's glTF, cel shading and paint, a sun without shadows, a clear
    /// background, the board's pitch. Still (<paramref name="live"/> false) it
    /// is turned three-quarters and framed tight on that view, for a picture
    /// read back once. Live - the quick battle's build card - it is drawn every
    /// frame and framed loose enough for the rig to turn it any way round; the
    /// rig is the model's turntable. With <paramref name="even"/> (live, the
    /// hangar's podium) the view is <see cref="RosterEven"/> times the body's height,
    /// its foot on one line: every class the same size. Null when the model does not load.
    /// </summary>
    private (SubViewport View, Node3D Rig)? RosterScene(string tag, Side side, Vector2I size, Node parent, bool live, bool even = false)
    {
        TankModel model;
        try
        {
            model = TankModel.Load(tag, toon: !_pbr);
        }
        catch (Exception e)
        {
            Log.Print($"tank3d: roster: {tag}: {e.Message}");
            return null;
        }
        var view = new SubViewport
        {
            Name = $"RosterArt_{tag}",
            Size = size,
            OwnWorld3D = true,
            TransparentBg = true,
            Msaa3D = Viewport.Msaa.Msaa4X,
            RenderTargetUpdateMode = live ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Once,
        };
        parent.AddChild(view);
        var world = BoardDress.World();
        world.Environment.BackgroundMode = Godot.Environment.BGMode.ClearColor;
        view.AddChild(world);
        DirectionalLight3D sun = BoardDress.Sun();
        sun.ShadowEnabled = false;
        view.AddChild(sun);

        model.ScaleTo(PixelsFor(model, ClassFor(tag)));
        SidePaint(model, side);
        var rig = new Node3D { Name = "Rig", RotationDegrees = new Vector3(0.0f, RosterArtYaw, 0.0f) };
        view.AddChild(rig);
        rig.AddChild(model);
        model.Apply();

        // the board's pitch, framed on the model's box as the camera sees it
        var camera = new Camera3D
        {
            Projection = Camera3D.ProjectionType.Orthogonal,
            KeepAspect = Camera3D.KeepAspectEnum.Height,
            RotationDegrees = new Vector3(-Mathf.RadToDeg(Mathf.Asin(Squash)), 0.0f, 0.0f),
            Current = true,
        };
        view.AddChild(camera);
        Aabb box = RosterBox(model);
        Basis b = camera.Basis;
        float reach = box.Size.Length();
        camera.Near = 1.0f;
        camera.Far = reach * 4.0f;
        if (live)
        {
            // the model's own box, unturned, swept round the rig's axis a
            // twelfth of a turn at a time: what the camera sees at its widest
            rig.RotationDegrees = Vector3.Zero;
            Aabb own = RosterBox(model);
            Aabb body = RosterBox(model, model.Barrel);
            rig.RotationDegrees = new Vector3(0.0f, RosterArtYaw, 0.0f);
            (float Low, float High, float Wide) Sweep(Aabb a)
            {
                float low = float.MaxValue, high = float.MinValue, wide = 0.0f;
                for (int k = 0; k < 12; k++)
                {
                    var turn = new Basis(Vector3.Up, Mathf.Tau * k / 12.0f);
                    for (int i = 0; i < 8; i++)
                    {
                        Vector3 p = turn * (a.Position + new Vector3((i & 1) != 0 ? a.Size.X : 0.0f,
                                                                     (i & 2) != 0 ? a.Size.Y : 0.0f,
                                                                     (i & 4) != 0 ? a.Size.Z : 0.0f));
                        float y = p.Dot(b.Y);
                        low = Mathf.Min(low, y);
                        high = Mathf.Max(high, y);
                        wide = Mathf.Max(wide, Mathf.Abs(p.Dot(b.X)));
                    }
                }
                return (low, high, wide);
            }
            var (lowY, highY, wide) = Sweep(own);
            if (even)
            {
                // every class the same size by its body - hull and turret, the gun
                // left out (a long gun made its tank small) - its tracks on one line
                // near the foot: a class changed neither jumps in size nor in height
                // (author 2026-11-03); the view keeps room above for the gun swung away
                var (bodyLow, bodyHigh, _) = Sweep(body);
                float s = (bodyHigh - bodyLow) * RosterEven;
                camera.Size = s;
                // the foot a tenth of the body under its tracks: a gun swung at the camera dips that far
                camera.Position = b.Y * (bodyLow - (bodyHigh - bodyLow) * 0.1f + s * 0.5f) + b.Z * reach * 2.0f;
                // where the turntable's axis meets the ground, as shares of the view: the
                // podium's light is centred there (author 2026-11-04). Orthogonal, the
                // height kept: a camera unit is size.Y / s pixels either way
                Vector3 foot = new Vector3(0.0f, own.Position.Y, 0.0f) - camera.Position;
                float unit = size.Y / s;
                view.SetMeta(RosterAxisMeta, new Vector2((size.X * 0.5f + foot.Dot(b.X) * unit) / size.X,
                                                         (size.Y * 0.5f - foot.Dot(b.Y) * unit) / size.Y));
                return (view, rig);
            }
            camera.Size = Mathf.Max(highY - lowY, 2.0f * wide * size.Y / Mathf.Max(size.X, 1)) * 1.04f;
            camera.Position = b.Y * ((lowY + highY) * 0.5f) + b.Z * reach * 2.0f;
            return (view, rig);
        }
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        for (int i = 0; i < 8; i++)
        {
            Vector3 p = box.Position + new Vector3((i & 1) != 0 ? box.Size.X : 0.0f,
                                                   (i & 2) != 0 ? box.Size.Y : 0.0f,
                                                   (i & 4) != 0 ? box.Size.Z : 0.0f);
            float x = p.Dot(b.X), y = p.Dot(b.Y);
            minX = Mathf.Min(minX, x);
            maxX = Mathf.Max(maxX, x);
            minY = Mathf.Min(minY, y);
            maxY = Mathf.Max(maxY, y);
        }
        camera.Size = Mathf.Max(maxX - minX, maxY - minY) * 1.08f;
        Vector3 mid = box.GetCenter();
        // the box's middle on the camera's axis, the camera well back along it
        Vector3 off = b.X * ((minX + maxX) * 0.5f - mid.Dot(b.X)) + b.Y * ((minY + maxY) * 0.5f - mid.Dot(b.Y));
        camera.Position = mid + off + b.Z * reach * 2.0f;
        return (view, rig);
    }

    /// <summary>The model's box in the viewport's world: every mesh's, as placed;
    /// with <paramref name="skip"/>, every mesh but those under it (the barrel).</summary>
    private static Aabb RosterBox(Node3D root, Node? skip = null)
    {
        Aabb? box = null;
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            Node n = stack.Pop();
            if (n == skip)
                continue;
            if (n is VisualInstance3D v and (MeshInstance3D or MultiMeshInstance3D) && v.IsVisibleInTree())
            {
                Aabb a = v.GlobalTransform * v.GetAabb();
                box = box is { } b ? b.Merge(a) : a;
            }
            foreach (Node c in n.GetChildren())
                stack.Push(c);
        }
        return box ?? new Aabb(Vector3.Zero, Vector3.One);
    }
}
