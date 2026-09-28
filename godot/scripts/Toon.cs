using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// Cel shading and an inked outline for a model the glTF loader made: every
/// <see cref="StandardMaterial3D"/> becomes a <see cref="ShaderMaterial"/> on
/// <see cref="CelShader"/> with <see cref="InkShader"/> as its next pass.
///
/// <b>The outline is an inverted hull, pushed out along a smoothed normal.</b>
/// The game variant's edges are hard - two-segment bevels, split normals - and a
/// shell pushed along split normals tears open at every such edge. So each
/// surface is rebuilt with <c>CUSTOM0</c> holding the normal averaged over all
/// the vertices on one spot (xyz) and how much ink the piece takes (w); the
/// cel pass keeps the mesh's own normals.
///
/// <b>The ink is board pixels, not screen pixels</b>: the sprites' ink is drawn
/// into the atlas and grows with the zoom, and a 3D tank beside them should
/// read the same - with <see cref="InkMinPx"/> screen pixels as the floor, so
/// zoomed out the line does not break up.
///
/// <b>Small pieces take no ink</b> (<see cref="InkFrom"/>, <see cref="InkFull"/>):
/// a rivet, a bolt or a handle with a shell of its own is a black dot, and a
/// hull full of rivets reads as dirt. The painted rim the bake gives a rivet
/// (<c>K.rivets</c>, the <c>Ink</c> rim) is what draws it.
/// </summary>
public static class Toon
{
    /// <summary>How wide the outline is, board px (world units).</summary>
    public const float InkWidth = 1.1f;
    /// <summary>The outline is never thinner than this many screen px.</summary>
    public const float InkMinPx = 1.0f;
    /// <summary>How dark the ink is against the paint under it: the sprites'
    /// line is the paint's own colour, darkened, not black.</summary>
    public const float InkDark = 0.28f;
    /// <summary>A piece - vertices joined by triangles or by sharing a spot -
    /// whose box diagonal is under this share of the model's length takes no
    /// ink; from <see cref="InkFull"/> up it takes all of it.</summary>
    public const float InkFrom = 0.035f, InkFull = 0.07f;

    /// <summary>Faces meeting at more than this, degrees, meet at a crease for
    /// the light (<see cref="Facet"/>); under it they shade as one surface.</summary>
    public const float CreaseDeg = 35.0f;

    /// <summary>
    /// The light as three tones, in the paint's own colour: <c>shade</c> is
    /// what a face turned from the sun keeps (the scene's ambient is off here,
    /// this stands for it, occlusion and all), the middle tone adds
    /// <c>mid</c> of the sun above <c>edge_dark</c> of its cosine, full sun
    /// comes in above <c>edge_lit</c>; <c>soft</c> is how wide each step is.
    /// Shadow is folded into the same cosine, so a cast shadow steps down to
    /// the shade tone as a turned-away face does.
    /// </summary>
    public static readonly Shader CelShader = new()
    {
        Code = @"
shader_type spatial;
render_mode cull_back, specular_disabled, ambient_light_disabled;
uniform vec4 albedo : source_color = vec4(1.0);
uniform sampler2D albedo_tex : source_color, hint_default_white, filter_linear_mipmap_anisotropic;
uniform sampler2D orm_tex : hint_default_white, filter_linear_mipmap;
uniform bool has_orm = false;
uniform vec3 shade = vec3(0.36, 0.38, 0.44);
uniform float ao_light = 0.5;
uniform float edge_dark = 0.12;
uniform float edge_lit = 0.7;
uniform float mid = 0.5;
uniform float soft = 0.035;
uniform float sun = 1.0;
void fragment() {
    vec4 c = texture(albedo_tex, UV) * albedo;
    float ao = has_orm ? texture(orm_tex, UV).r : 1.0;
    ALBEDO = c.rgb;
    EMISSION = c.rgb * shade * ao;
    AO = ao;
    AO_LIGHT_AFFECT = ao_light;
}
void light() {
    float t = clamp(dot(NORMAL, LIGHT), 0.0, 1.0) * ATTENUATION;
    float v = mid * smoothstep(edge_dark - soft, edge_dark + soft, t);
    v = mix(v, 1.0, smoothstep(edge_lit - soft, edge_lit + soft, t));
    DIFFUSE_LIGHT += v * sun * LIGHT_COLOR / PI;
}
",
    };

    /// <summary>
    /// The shell: back faces only, every vertex moved out in view space along
    /// the smoothed normal and back from the eye by the same, so where it is
    /// not the silhouette it stays under the surface it wraps.
    /// </summary>
    public static readonly Shader InkShader = new()
    {
        Code = @"
shader_type spatial;
render_mode unshaded, cull_front, skip_vertex_transform, shadows_disabled, fog_disabled;
uniform vec4 albedo : source_color = vec4(1.0);
uniform sampler2D albedo_tex : source_color, hint_default_white, filter_linear_mipmap;
uniform float width = 1.0;
uniform float min_px = 1.0;
uniform float dark = 0.3;
void vertex() {
    VERTEX = (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
    vec3 n = normalize(mat3(MODELVIEW_MATRIX) * CUSTOM0.xyz);
    // One screen px in view units, for an orthographic eye.
    float px = 2.0 / (PROJECTION_MATRIX[1][1] * VIEWPORT_SIZE.y);
    float w = max(width, min_px * px) * CUSTOM0.w;
    VERTEX.xy += n.xy * w;
    VERTEX.z -= w;
    NORMAL = n;
}
void fragment() {
    ALBEDO = texture(albedo_tex, UV).rgb * albedo.rgb * dark;
}
",
    };

    /// <summary>Dress every mesh under <paramref name="scene"/>: its surfaces
    /// rebuilt with the ink's normal, its materials swapped for cel ones, one
    /// per source material. Returns the cel materials.</summary>
    public static List<ShaderMaterial> Dress(Node scene)
    {
        var made = new Dictionary<Material, ShaderMaterial>();
        var meshes = new List<MeshInstance3D>();
        Collect(scene, meshes);
        float length = 0.0f;
        foreach (MeshInstance3D m in meshes)
            length = Mathf.Max(length, m.Mesh.GetAabb().Size.Length());
        foreach (MeshInstance3D m in meshes)
            m.Mesh = Rebuild(m.Mesh, length, made);
        return new List<ShaderMaterial>(made.Values);
    }

    /// <summary>Set the paint of a cel material and of its ink together.</summary>
    public static void Paint(ShaderMaterial cel, Color albedo)
    {
        cel.SetShaderParameter("albedo", albedo);
        (cel.NextPass as ShaderMaterial)?.SetShaderParameter("albedo", albedo);
    }

    public static Color PaintOf(ShaderMaterial cel) => cel.GetShaderParameter("albedo").AsColor();

    private static void Collect(Node node, List<MeshInstance3D> into)
    {
        if (node is MeshInstance3D m && m.Mesh is not null)
            into.Add(m);
        foreach (Node child in node.GetChildren())
            Collect(child, into);
    }

    private static ArrayMesh Rebuild(Mesh src, float length, Dictionary<Material, ShaderMaterial> made)
    {
        var dst = new ArrayMesh();
        const Mesh.ArrayFormat custom = (Mesh.ArrayFormat)((long)Mesh.ArrayCustomFormat.RgbaFloat
                                                           << (int)Mesh.ArrayFormat.FormatCustom0Shift);
        for (int s = 0; s < src.GetSurfaceCount(); s++)
        {
            Godot.Collections.Array arrays = Facet(src.SurfaceGetArrays(s));
            arrays[(int)Mesh.ArrayType.Custom0] = InkNormals(arrays, length);
            dst.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, custom);
            Material? mat = src.SurfaceGetMaterial(s);
            if (mat is not null)
                dst.SurfaceSetMaterial(s, CelFor(mat, made));
        }
        return dst;
    }

    private static ShaderMaterial CelFor(Material mat, Dictionary<Material, ShaderMaterial> made)
    {
        if (made.TryGetValue(mat, out ShaderMaterial? cel))
            return cel;
        var std = mat as BaseMaterial3D;
        Color albedo = std?.AlbedoColor ?? Colors.White;
        Texture2D? tex = std?.AlbedoTexture;
        Texture2D? orm = std?.AOTexture;
        var ink = new ShaderMaterial { Shader = InkShader };
        ink.SetShaderParameter("albedo", albedo);
        ink.SetShaderParameter("width", InkWidth);
        ink.SetShaderParameter("min_px", InkMinPx);
        ink.SetShaderParameter("dark", InkDark);
        cel = new ShaderMaterial { Shader = CelShader, NextPass = ink };
        cel.SetShaderParameter("albedo", albedo);
        if (tex is not null)
        {
            cel.SetShaderParameter("albedo_tex", tex);
            ink.SetShaderParameter("albedo_tex", tex);
        }
        if (orm is not null && std!.AOEnabled)
        {
            cel.SetShaderParameter("orm_tex", orm);
            cel.SetShaderParameter("has_orm", true);
        }
        made[mat] = cel;
        return cel;
    }

    /// <summary>
    /// The light's normals, made again from the faces: each corner takes the
    /// faces round its spot that turn less than <see cref="CreaseDeg"/> from
    /// its own, weighted by their area, and a vertex whose corners come out
    /// different is split.
    ///
    /// <b>The glTF's normals are smooth across the bevels</b>: over the whole
    /// of MTR's skirt panels they lean 8-20 deg off the face toward the
    /// two-segment bevels round them, so a flat plate shades as a slope drawn
    /// across its triangles. The cel ramp's step turns that into stripes where
    /// the plate stands near a threshold - diagonal blots across the skirts,
    /// with or without shadow and occlusion. Made again, a plate is one
    /// normal and steps as one, and a loft's turret or a tube stays round.
    ///
    /// <b>By area, not by angle</b>: a two-segment bevel on a square edge
    /// turns its first face 22.5 deg off the plate, inside any crease angle
    /// that keeps a loft round, and weighted by its corner angle a strip a few
    /// mm wide bent a plate's corner as much as the plate held it. By area the
    /// strip is what it is beside the plate.
    /// </summary>
    private static Godot.Collections.Array Facet(Godot.Collections.Array arrays)
    {
        Vector3[] pos = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        if (arrays[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil)
            return arrays;
        int[] idx = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        int faces = idx.Length / 3;
        var fn = new Vector3[faces];
        var area = new float[faces];
        for (int f = 0; f < faces; f++)
        {
            Vector3 a = pos[idx[f * 3]], b = pos[idx[f * 3 + 1]], c = pos[idx[f * 3 + 2]];
            // Godot's front faces wind clockwise.
            Vector3 n = (c - a).Cross(b - a);
            area[f] = n.Length();
            fn[f] = area[f] > 1e-10f ? n / area[f] : Vector3.Zero;
        }
        var bySpot = new Dictionary<(long, long, long), List<int>>();
        var cornerSpot = new List<int>[idx.Length];
        for (int k = 0; k < idx.Length; k++)
        {
            Vector3 p = pos[idx[k]] * 1.0e4f;
            var key = ((long)Mathf.Round(p.X), (long)Mathf.Round(p.Y), (long)Mathf.Round(p.Z));
            if (!bySpot.TryGetValue(key, out List<int>? list))
                bySpot[key] = list = new List<int>();
            list.Add(k);
            cornerSpot[k] = list;
        }
        float limit = Mathf.Cos(Mathf.DegToRad(CreaseDeg));
        var from = new List<int>();
        var normals = new List<Vector3>();
        var made = new Dictionary<(int, long, long, long), int>();
        var index = new int[idx.Length];
        for (int k = 0; k < idx.Length; k++)
        {
            Vector3 own = fn[k / 3];
            Vector3 sum = Vector3.Zero;
            foreach (int o in cornerSpot[k])
                if (fn[o / 3].Dot(own) >= limit)
                    sum += fn[o / 3] * area[o / 3];
            Vector3 n = sum.LengthSquared() > 1e-20f ? sum.Normalized()
                : own != Vector3.Zero ? own : Vector3.Up;
            var key = (idx[k], (long)Mathf.Round(n.X * 1e4f), (long)Mathf.Round(n.Y * 1e4f), (long)Mathf.Round(n.Z * 1e4f));
            if (!made.TryGetValue(key, out int v))
            {
                v = from.Count;
                made[key] = v;
                from.Add(idx[k]);
                normals.Add(n);
            }
            index[k] = v;
        }

        var result = new Godot.Collections.Array();
        result.Resize((int)Mesh.ArrayType.Max);
        int old = pos.Length;
        for (int a = 0; a < (int)Mesh.ArrayType.Max; a++)
        {
            Variant src = arrays[a];
            if (src.VariantType == Variant.Type.Nil || a == (int)Mesh.ArrayType.Index)
                continue;
            result[a] = src.VariantType switch
            {
                Variant.Type.PackedVector3Array => Pick(src.AsVector3Array(), from, 1),
                Variant.Type.PackedVector2Array => Pick(src.AsVector2Array(), from, 1),
                Variant.Type.PackedColorArray => Pick(src.AsColorArray(), from, 1),
                Variant.Type.PackedFloat32Array => Pick(src.AsFloat32Array(), from, src.AsFloat32Array().Length / old),
                Variant.Type.PackedInt32Array => Pick(src.AsInt32Array(), from, src.AsInt32Array().Length / old),
                Variant.Type.PackedByteArray => Pick(src.AsByteArray(), from, src.AsByteArray().Length / old),
                _ => src,
            };
        }
        result[(int)Mesh.ArrayType.Normal] = normals.ToArray();
        result[(int)Mesh.ArrayType.Index] = index;
        return result;
    }

    private static T[] Pick<T>(T[] src, List<int> from, int per)
    {
        var dst = new T[from.Count * per];
        for (int i = 0; i < from.Count; i++)
            System.Array.Copy(src, from[i] * per, dst, i * per, per);
        return dst;
    }

    /// <summary>
    /// Per vertex: the normal averaged over every vertex on its spot, and the
    /// ink its piece takes - a piece being what triangles and shared spots join,
    /// its size the diagonal of its box against the model's length.
    /// </summary>
    private static float[] InkNormals(Godot.Collections.Array arrays, float length)
    {
        Vector3[] pos = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        Vector3[] nor = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
        int[] idx = arrays[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil
            ? System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Range(0, pos.Length))
            : arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        int n = pos.Length;
        var parent = new int[n];
        for (int i = 0; i < n; i++)
            parent[i] = i;
        int Root(int i)
        {
            while (parent[i] != i)
                i = parent[i] = parent[parent[i]];
            return i;
        }
        void Join(int a, int b)
        {
            a = Root(a);
            b = Root(b);
            if (a != b)
                parent[a] = b;
        }

        // One spot: positions within a tenth of a millimetre of a model a metre long.
        var spot = new Dictionary<(long, long, long), int>();
        var spotOf = new int[n];
        var sums = new List<Vector3>();
        for (int i = 0; i < n; i++)
        {
            Vector3 p = pos[i] * 1.0e4f;
            var key = ((long)Mathf.Round(p.X), (long)Mathf.Round(p.Y), (long)Mathf.Round(p.Z));
            if (!spot.TryGetValue(key, out int k))
            {
                k = sums.Count;
                spot[key] = k;
                sums.Add(Vector3.Zero);
                spotOf[i] = k;
            }
            else
            {
                spotOf[i] = k;
            }
            sums[k] += i < nor.Length ? nor[i] : Vector3.Zero;
        }
        var first = new int[sums.Count];
        System.Array.Fill(first, -1);
        for (int i = 0; i < n; i++)
        {
            int k = spotOf[i];
            if (first[k] < 0)
                first[k] = i;
            else
                Join(i, first[k]);
        }
        for (int t = 0; t + 2 < idx.Length; t += 3)
        {
            Join(idx[t], idx[t + 1]);
            Join(idx[t], idx[t + 2]);
        }

        var lo = new Dictionary<int, Vector3>();
        var hi = new Dictionary<int, Vector3>();
        for (int i = 0; i < n; i++)
        {
            int r = Root(i);
            if (!lo.TryGetValue(r, out Vector3 a))
            {
                lo[r] = hi[r] = pos[i];
                continue;
            }
            lo[r] = a.Min(pos[i]);
            hi[r] = hi[r].Max(pos[i]);
        }

        var custom = new float[n * 4];
        for (int i = 0; i < n; i++)
        {
            Vector3 s = sums[spotOf[i]];
            s = s.LengthSquared() > 1e-12f ? s.Normalized() : (i < nor.Length ? nor[i] : Vector3.Up);
            int r = Root(i);
            float size = (hi[r] - lo[r]).Length() / Mathf.Max(length, 1e-6f);
            float ink = Mathf.SmoothStep(InkFrom, InkFull, size);
            custom[i * 4 + 0] = s.X;
            custom[i * 4 + 1] = s.Y;
            custom[i * 4 + 2] = s.Z;
            custom[i * 4 + 3] = ink;
        }
        return custom;
    }
}
