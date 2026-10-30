using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The roster's pictures (docs/hud3d-plan.md, «Ростер», 2026-10-30): each
/// unit's own model, rendered once at the match's start in a viewport of its
/// own - the same glTF, cel shading and ink as on the board, in its side's
/// paint, turned three-quarters to the camera at the board's pitch.
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

    private readonly Dictionary<(string Tag, Side Side), Texture2D> _rosterArt = new();

    /// <summary>Every model of the line-up in each side's paint, one after
    /// another; started with the roster.</summary>
    private async void RosterArt()
    {
        var wanted = new List<(string, Side)>();
        foreach (Lineup.Unit u in _lineup)
        {
            var key = (u.Model.ToUpperInvariant(), SideOf(u));
            if (!wanted.Contains(key))
                wanted.Add(key);
        }
        int cached = 0, rendered = 0;
        foreach (var (tag, side) in wanted)
        {
            if (!IsInsideTree())
                return;
            string path = RosterCachePath(tag, side);
            Texture2D? art = null;
            if (path.Length > 0 && FileAccess.FileExists(path) && Image.LoadFromFile(path) is { } kept && !kept.IsEmpty())
            {
                art = ImageTexture.CreateFromImage(kept);
                cached++;
            }
            else if (await RosterRender(tag, side) is { } made)
            {
                art = ImageTexture.CreateFromImage(made);
                rendered++;
                if (path.Length > 0)
                    RosterCacheWrite(path, tag, side, made);
            }
            if (art is not null)
                _rosterArt[(tag, side)] = art;
        }
        Log.Print($"tank3d: roster: {_rosterArt.Count} of {wanted.Count} units - {cached} from the cache, {rendered} rendered");
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
            Size = new Vector2I(RosterArtPx, RosterArtPx),
            OwnWorld3D = true,
            TransparentBg = true,
            Msaa3D = Viewport.Msaa.Msaa4X,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Once,
        };
        AddChild(view);
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
        float reach = box.Size.Length();
        camera.Size = Mathf.Max(maxX - minX, maxY - minY) * 1.08f;
        camera.Near = 1.0f;
        camera.Far = reach * 4.0f;
        Vector3 mid = box.GetCenter();
        // the box's middle on the camera's axis, the camera well back along it
        Vector3 across = b.X * ((minX + maxX) * 0.5f - mid.Dot(b.X)) + b.Y * ((minY + maxY) * 0.5f - mid.Dot(b.Y));
        camera.Position = mid + across + b.Z * reach * 2.0f;

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

    /// <summary>The model's box in the viewport's world: every mesh's, as placed.</summary>
    private static Aabb RosterBox(Node3D root)
    {
        Aabb? box = null;
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            Node n = stack.Pop();
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
