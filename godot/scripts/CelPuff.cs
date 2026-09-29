using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The burning column's smoke puffs (<see cref="CelBurn"/>): the puff's
/// shaders, drawn as a <see cref="MultiMesh"/> of spheres whose instance
/// custom data carries each puff's own numbers - r its seed, g its age, b its
/// grey, a how far it is eaten. The exhaust (<see cref="CelExhaust"/>) takes
/// only the hash: it draws its puffs as one cloud, not as spheres.
/// </summary>
public static class CelPuff
{
    /// <summary>A multimesh of <paramref name="count"/> puffs with the body and
    /// its ink, the ink's width the model's (<see cref="Toon.InkWidth"/>).</summary>
    public static (MultiMesh Puffs, MultiMeshInstance3D Cloud, ShaderMaterial Body) Cloud(string name, int count)
    {
        var ink = new ShaderMaterial { Shader = InkShader };
        ink.SetShaderParameter("width", Toon.InkWidth);
        ink.SetShaderParameter("min_px", Toon.InkMinPx);
        var body = new ShaderMaterial { Shader = BodyShader, NextPass = ink };
        var puffs = new MultiMesh
        {
            Mesh = new SphereMesh { Radius = 0.5f, Height = 1.0f, RadialSegments = 24, Rings = 12 },
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true,
            InstanceCount = count,
        };
        var cloud = new MultiMeshInstance3D
        {
            Name = name, Multimesh = puffs, MaterialOverride = body,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        return (puffs, cloud, body);
    }

    /// <summary>Into the instance array far to near. Everything here is cut
    /// hard and writes depth, so the order only saves overdraw.</summary>
    public static void Write(MultiMesh into, List<(float Depth, Transform3D Where, Color Mine)> order)
    {
        order.Sort((p, q) => p.Depth.CompareTo(q.Depth));
        for (int i = 0; i < order.Count; i++)
        {
            into.SetInstanceTransform(i, order[i].Where);
            into.SetInstanceCustomData(i, order[i].Mine);
        }
    }

    public static float Hash(int k, int salt)
    {
        unchecked
        {
            uint h = (uint)(k * 73856093) ^ (uint)(salt * 19349663) ^ 0x9E3779B9u;
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFFFF) / 16777215.0f;
        }
    }

    /// <summary>
    /// A puff: a unit sphere made lumpy along its normal, on the model's ramp.
    /// It is eaten by a hard cut on a noise plus its rim (edges go first), the
    /// cut rising with <c>INSTANCE_CUSTOM.a</c>. Near a fire its foot is lit by
    /// the fire's light through the same ramp, which is all the glow it needs: a glow of
    /// its own on dark smoke read as brown. Both faces drawn: through a hole
    /// the cut makes, the far side of the puff is what should show, and with
    /// only front faces it was the ink's shell - a dark blot in the smoke.
    /// </summary>
    public static readonly Shader BodyShader = new()
    {
        Code = @"
shader_type spatial;
render_mode cull_disabled, specular_disabled, ambient_light_disabled, shadows_disabled;
stencil_mode write, compare_always, 0;
uniform float lump = 0.22;
uniform vec3 tint : source_color = vec3(0.96, 0.95, 1.0);
varying vec3 obj;
varying vec4 mine;
" + Toon.NoiseCode + Toon.RampCode + @"
void vertex() {
    mine = INSTANCE_CUSTOM;
    obj = VERTEX;
    float n = noise3(VERTEX * 2.6 + mine.r * 41.0);
    VERTEX += NORMAL * (n - 0.5) * lump;
}
void fragment() {
    float n = noise3(obj * 5.0 + vec3(mine.r * 13.0, -mine.g * 2.0, 0.0));
    float rim = clamp(NORMAL.z, 0.0, 1.0);
    if (n * 0.7 + rim * 0.3 < mine.a) discard;
    vec3 c = vec3(mine.b) * tint;
    ALBEDO = c;
    EMISSION = c * shade;
}
",
    };

    /// <summary>The puff's ink: <see cref="Toon.InkShader"/>'s shell on the
    /// same lumps and cut away with the puff.</summary>
    public static readonly Shader InkShader = new()
    {
        Code = @"
shader_type spatial;
render_mode unshaded, cull_front, skip_vertex_transform, shadows_disabled, fog_disabled;
stencil_mode write, compare_always, 0;
uniform float lump = 0.22;
uniform float width = 1.0;
uniform float min_px = 1.0;
uniform float dark = 0.4;
varying vec3 obj;
varying vec4 mine;
" + Toon.NoiseCode + @"
void vertex() {
    mine = INSTANCE_CUSTOM;
    obj = VERTEX;
    float n = noise3(VERTEX * 2.6 + mine.r * 41.0);
    vec3 v = VERTEX + NORMAL * (n - 0.5) * lump;
    VERTEX = (MODELVIEW_MATRIX * vec4(v, 1.0)).xyz;
    vec3 nv = normalize(mat3(MODELVIEW_MATRIX) * NORMAL);
    float px = 2.0 / (PROJECTION_MATRIX[1][1] * VIEWPORT_SIZE.y);
    float w = max(width, min_px * px);
    VERTEX.xy += nv.xy * w;
    VERTEX.z -= w;
    NORMAL = nv;
}
void fragment() {
    float n = noise3(obj * 5.0 + vec3(mine.r * 13.0, -mine.g * 2.0, 0.0));
    if (n * 0.7 < mine.a) discard;
    ALBEDO = vec3(mine.b) * dark;
}
",
    };
}
