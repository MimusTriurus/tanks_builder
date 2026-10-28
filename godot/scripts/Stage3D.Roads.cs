using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The roads on the relief (format 3, docs/map-format3-plan.md Ф6): the six
/// of tools/render/roads.py - dirt, gravel, cobble, corduroy, a forest track,
/// asphalt - drawn along the axes <see cref="Road.Axes"/> lays through the
/// hex sides.
///
/// <b>A map of the distance to the road's axis, not a mesh.</b> Every texel
/// near a road holds how far it lies from the nearest axis, how far along that
/// axis, and the road's type, in world XZ under the board; the ground's shader
/// reads it and paints the road over everything the ground is - grass, sand,
/// a swamp beside a log road. The distance is unsigned: at a fork a signed one
/// flips across the seam between the two axes, and its false zero drew a
/// centre line where there was none (the renders' catch).
///
/// <b>Fine, because the road's lines are.</b> A texel is a fortieth of a
/// circumradius: an asphalt's centre dash is a few hundredths of one wide and
/// drawn where the distance is under that, which a 1/12 raster would round off.
/// </summary>
public sealed partial class Stage3D
{
    /// <summary>A texel of the road map, in circumradii.</summary>
    private const float RoadTexel = 1.0f / 40.0f;

    /// <summary>How far from its axis a road's map reaches, in circumradii:
    /// past its half width and its torn edge.</summary>
    private const float RoadReach = 0.7f;

    private ImageTexture? _roadMap;

    /// <summary>The map's distance, world units, and its frame - kept for
    /// <see cref="RoadNear"/>.</summary>
    private float[]? _roadDist;
    private Vector4 _roadFrame;
    private int _roadWide, _roadTall;

    /// <summary>
    /// The road map for the relief just built, handed to its shader - or the
    /// roads switched off on a board with none. Each axis's segments are laid
    /// over their own box of texels, nearest axis kept.
    /// </summary>
    private void Roads(Relief f) => RedrawRoads();

    /// <summary>
    /// The road map drawn again from <see cref="HexField.Roads"/>, without
    /// the relief - the editor's road tool lays its road being drawn this way,
    /// a frame at a time (MapEditor.Roads). Roads from
    /// <paramref name="ghostFrom"/> on are its ghost: drawn as roads, see-through
    /// and cooler, so the author sees the road it will be and that it is not
    /// laid yet.
    /// </summary>
    public void RedrawRoads(int ghostFrom = int.MaxValue)
    {
        _roadDist = null;
        if (Field.Atlas is null || _tops.MaterialOverride is not ShaderMaterial ink)
            return;
        float r = Field.Atlas.HexRect.Size.X * 0.5f;
        List<Road.Axis> axes = Road.Axes(Field.Roads, Middle, 0.05f * r);
        if (axes.Count == 0)
        {
            ink.Param("road_on", 0.0f);
            return;
        }
        float reach = RoadReach * r, texel = RoadTexel * r;
        float loX = float.MaxValue, loZ = float.MaxValue, hiX = float.MinValue, hiZ = float.MinValue;
        foreach (Road.Axis a in axes)
            foreach (Vector2 p in a.Points)
            {
                loX = Mathf.Min(loX, p.X - reach); hiX = Mathf.Max(hiX, p.X + reach);
                loZ = Mathf.Min(loZ, p.Y - reach); hiZ = Mathf.Max(hiZ, p.Y + reach);
            }
        int wide = Mathf.Clamp(Mathf.CeilToInt((hiX - loX) / texel) + 1, 2, 4096);
        int tall = Mathf.Clamp(Mathf.CeilToInt((hiZ - loZ) / texel) + 1, 2, 4096);
        float stepX = (hiX - loX) / (wide - 1), stepZ = (hiZ - loZ) / (tall - 1);
        var dist = new float[wide * tall];
        var along = new float[wide * tall];
        var type = new float[wide * tall];
        System.Array.Fill(dist, float.MaxValue);
        foreach (Road.Axis a in axes)
            for (int i = 0; i + 1 < a.Points.Length; i++)
            {
                Vector2 p0 = a.Points[i], p1 = a.Points[i + 1];
                Vector2 seg = p1 - p0;
                float len2 = Mathf.Max(seg.LengthSquared(), 1e-6f);
                int x0 = Mathf.Max(0, (int)((Mathf.Min(p0.X, p1.X) - reach - loX) / stepX));
                int x1 = Mathf.Min(wide - 1, (int)((Mathf.Max(p0.X, p1.X) + reach - loX) / stepX) + 1);
                int z0 = Mathf.Max(0, (int)((Mathf.Min(p0.Y, p1.Y) - reach - loZ) / stepZ));
                int z1 = Mathf.Min(tall - 1, (int)((Mathf.Max(p0.Y, p1.Y) + reach - loZ) / stepZ) + 1);
                for (int z = z0; z <= z1; z++)
                    for (int x = x0; x <= x1; x++)
                    {
                        var q = new Vector2(loX + x * stepX, loZ + z * stepZ);
                        float t = Mathf.Clamp((q - p0).Dot(seg) / len2, 0.0f, 1.0f);
                        float d = q.DistanceTo(p0 + seg * t);
                        int k = z * wide + x;
                        if (d >= dist[k])
                            continue;
                        dist[k] = d;
                        along[k] = a.Along[i] + t * Mathf.Sqrt(len2);
                        type[k] = (float)a.Type + (a.Index >= ghostFrom ? 8.0f : 0.0f);
                    }
            }
        Image img = Image.CreateEmpty(wide, tall, false, Image.Format.Rgbaf);
        for (int z = 0; z < tall; z++)
            for (int x = 0; x < wide; x++)
            {
                int k = z * wide + x;
                bool near = dist[k] <= reach;
                img.SetPixel(x, z, near
                    ? new Color(dist[k] / r, along[k] / r, type[k], 1.0f)
                    : new Color(9.0f, 0.0f, 0.0f, 0.0f));
            }
        if (_roadMap is null || _roadMap.GetWidth() != wide || _roadMap.GetHeight() != tall)
            _roadMap = ImageTexture.CreateFromImage(img);
        else
            _roadMap.Update(img);
        _roadDist = dist;
        _roadWide = wide;
        _roadTall = tall;
        _roadFrame = new Vector4(loX, loZ, 1.0f / (hiX - loX), 1.0f / (hiZ - loZ));
        ink.Param("road_tex", _roadMap);
        ink.Param("road_map", _roadFrame);
        ink.Param("road_on", 1.0f);
    }

    /// <summary>A cell's middle in world XZ, as the ground's shader reads it.</summary>
    private Vector2 Middle(Vector2I cell)
    {
        Vector2 flat = Field.FlatAnchor(cell) + Field.CentreOffset;
        return new Vector2(flat.X + Origin.X, (flat.Y + Origin.Y) / Squash);
    }

    /// <summary>How far a world XZ point lies from the nearest road's axis,
    /// world units; MaxValue with no road near.</summary>
    private float RoadNear(Vector2 xz)
    {
        if (_roadDist is null)
            return float.MaxValue;
        int x = Mathf.RoundToInt((xz.X - _roadFrame.X) * _roadFrame.Z * (_roadWide - 1));
        int z = Mathf.RoundToInt((xz.Y - _roadFrame.Y) * _roadFrame.W * (_roadTall - 1));
        return x < 0 || z < 0 || x >= _roadWide || z >= _roadTall
            ? float.MaxValue : _roadDist[z * _roadWide + x];
    }

    private const string RoadPaint = @"
// The roads (Stage3D.Roads): the distance to the nearest axis, the length
// along it and the type, in circumradii, from a map under the board.
uniform sampler2D road_tex : filter_linear, hint_default_black;
uniform vec4 road_map = vec4(0.0, 0.0, 1.0, 1.0);
uniform float road_on = 0.0;

vec3 road_at(vec3 soil, float grain) {
    if (road_on < 0.5)
        return soil;
    vec2 uv = (ground.xz - road_map.xy) * road_map.zw;
    if (uv.x < 0.0 || uv.y < 0.0 || uv.x > 1.0 || uv.y > 1.0)
        return soil;
    vec4 rd = texture(road_tex, uv);
    if (rd.a < 0.5)
        return soil;
    float d = rd.r, s = rd.g;
    // 8 and over: the editor's road being drawn, its ghost (RedrawRoads)
    bool ghost = rd.b > 7.5;
    int ty = int(rd.b + 0.5) - (ghost ? 8 : 0);
    vec2 q = ground.xz / hex_r;
    // a side wide, the forest track narrower; a dirt road's edge torn, a
    // paved one's clean
    float half_w = ty == 4 ? 0.3 : 0.5;
    bool rough = ty == 0 || ty == 4 || ty == 3;
    float edge = half_w + (rough ? 0.05 : 0.01) * (rock_noise(q / 0.12) * 2.0 - 1.0);
    float on = 1.0 - smoothstep(edge - 0.015, edge, d);
    if (on <= 0.0)
        return soil;
    vec3 c;
    if (ty == 0) {
        // dirt: earth, two ruts, a strip of grass between them
        c = vec3(0.54, 0.42, 0.28) * (0.75 + 0.5 * grain);
        float rut = 1.0 - smoothstep(0.03, 0.06, abs(d - 0.22));
        c *= 1.0 - 0.25 * rut;
        float mid = (1.0 - smoothstep(0.05, 0.09, d)) * step(0.4, rock_noise(q / 0.08));
        c = mix(c, grass_ink * (0.8 + 0.4 * grain), 0.7 * mid);
    } else if (ty == 1) {
        // gravel: pale grit, darker shoulders
        c = vec3(0.68, 0.64, 0.56) * (0.82 + 0.3 * rock_noise(q / 0.012));
        c *= 1.0 - 0.15 * smoothstep(0.38, 0.48, d);
    } else if (ty == 2) {
        // cobble: setts with dark joints, a kerb at the edge
        vec2 w = rock_cells(q / 0.06);
        c = vec3(0.56, 0.54, 0.51) * (0.78 + 0.35 * rock_hash(floor(q / 0.06)));
        c *= 1.0 - 0.55 * (1.0 - smoothstep(0.0, 0.07, w.y - w.x));
        c = mix(c, vec3(0.70, 0.68, 0.64), smoothstep(0.43, 0.46, d));
    } else if (ty == 3) {
        // corduroy: logs across the way, dark gaps between
        float t = fract(s / 0.1 + 0.15 * rock_noise(vec2(s / 0.4, 0.0)));
        float roll = sin(t * 3.1416);
        c = vec3(0.47, 0.34, 0.21) * (0.65 + 0.45 * roll) * (0.85 + 0.3 * grain);
        c *= 1.0 - 0.6 * (1.0 - smoothstep(0.0, 0.12, t));
    } else if (ty == 4) {
        // the forest track: dark earth strewn with needles
        c = vec3(0.42, 0.32, 0.21) * (0.75 + 0.5 * grain);
        c = mix(c, vec3(0.55, 0.40, 0.22), 0.5 * step(0.7, rock_noise(q / 0.015)));
    } else {
        // asphalt: dark, white edge lines, a dashed centre
        c = vec3(0.27, 0.27, 0.28) * (0.9 + 0.2 * rock_noise(q / 0.03));
        float line = (1.0 - smoothstep(0.012, 0.02, abs(d - 0.43)));
        float dash = (1.0 - smoothstep(0.015, 0.025, d)) * step(0.5, fract(s / 0.3));
        c = mix(c, vec3(0.92, 0.90, 0.84), max(line, dash));
    }
    if (ghost) {
        // the road being drawn: itself, cooler, and a bright rim at its
        // edge so its width reads over any ground
        float rim = smoothstep(edge - 0.06, edge - 0.02, d);
        c = mix(mix(c, vec3(0.70, 0.88, 1.0), 0.3), vec3(0.92, 0.97, 1.0), rim);
        return mix(soil, c, on * 0.85);
    }
    return mix(soil, c, on);
}
";
}
