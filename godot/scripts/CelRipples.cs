using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The ripples on the water in the model's look: the board's own wave field
/// (<see cref="Ripples"/>) read back and drawn as flat bands - a pale crest
/// where the water stands up, a darker teal trough where it is pulled down,
/// hard-edged - on a skin over the water cells.
///
/// <b>Why a second reader of a field the stage already reads.</b> The stage
/// takes the field for its surface's normals, glints and foam, which is how a
/// painted pond moves; on this board that bends a few glints and nothing
/// else - with the field and without it, side by side, the frames were hard to
/// tell apart. Cel water says a wave by its shape: a ring of light running out,
/// reflecting off the bank, crossing another. The field is the physics
/// (<see cref="Ripples.Strike"/>, <see cref="Ripples.Note"/>, the banks as
/// walls) and this is only how it is drawn.
///
/// <b>The skin is the water cells' own hexagons at their surface</b>, lifted by
/// the stage's clearance: under a bank it runs under the ground and the ground
/// hides it; over a flooded ramp's dry head the same.
/// </summary>
public sealed partial class CelRipples : Node3D
{
    /// <summary>Displacement, world units, over which the crest's band shows and
    /// under which the trough's; and where the crest's band ends, so a tall
    /// crest is a ring with water inside it rather than a white disc.</summary>
    public float Crest = 0.6f, CrestTop = 4.5f, Trough = -1.6f;

    private MeshInstance3D _skin = null!;
    private ShaderMaterial _look = null!;

    /// <summary>Lay the skin: a hexagon of <paramref name="radius"/> (world, to
    /// a corner) at each water cell's surface.</summary>
    public void Build(HexField field, float squash, float rise, float radius)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        Vector3 lift = Stage3D.Clear(squash, rise);
        int cells = 0;
        for (int q = 0; q < field.Columns; q++)
            for (int r = 0; r < field.Rows; r++)
            {
                var cell = new Vector2I(q, r);
                if (!field.IsWater(cell))
                    continue;
                Vector2 flat = field.FlatAnchor(cell) + field.CentreOffset;
                var mid = new Vector3(flat.X, field.WaterTop(cell) / rise, flat.Y / squash) + lift;
                // A little over the cell, so two cells' skins meet without a seam.
                float reach = radius * 1.02f;
                for (int k = 0; k < 6; k++)
                {
                    float a0 = Mathf.DegToRad(60.0f * k), a1 = Mathf.DegToRad(60.0f * (k + 1));
                    st.AddVertex(mid);
                    st.AddVertex(mid + new Vector3(Mathf.Cos(a0), 0.0f, Mathf.Sin(a0)) * reach);
                    st.AddVertex(mid + new Vector3(Mathf.Cos(a1), 0.0f, Mathf.Sin(a1)) * reach);
                }
                cells++;
            }
        _look = new ShaderMaterial { Shader = RipplesShader, RenderPriority = Stage3D.DressOrder };
        _look.SetShaderParameter("light", CelSplash.Crest);
        _look.SetShaderParameter("dark", new Color(0.05f, 0.20f, 0.20f));
        _skin = new MeshInstance3D
        {
            Name = "Ripples",
            Mesh = cells > 0 ? st.Commit() : null,
            MaterialOverride = _look,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            // After the pond, which is transparent and drawn late (CelRuts);
            // under the wake and the splash's ring, which are nearer the eye.
            SortingOffset = -90000.0f,
            Visible = false,
        };
        AddChild(_skin);
    }

    /// <summary>The field as it stands this frame.</summary>
    public void Show(Ripples field)
    {
        if (_skin is null)
            return;
        bool on = field.Enabled && field.Wide > 0 && field.Sheet is not null && _skin.Mesh is not null;
        _skin.Visible = on;
        if (!on)
            return;
        _look.SetShaderParameter("wash", field.Sheet);
        _look.SetShaderParameter("wash_box", new Vector4(field.Box.Position.X, field.Box.Position.Y,
                                                         field.Box.Size.X, field.Box.Size.Y));
        _look.SetShaderParameter("crest", Crest);
        _look.SetShaderParameter("crest_top", CrestTop);
        _look.SetShaderParameter("trough", Trough);
    }

    /// <summary>
    /// The bands: the field looked up where the fragment is on the water, a
    /// crest band pale, a trough dark, both cut hard with the derivative for
    /// their edge. The crest's band is hollow past <c>crest_top</c>: the tall
    /// middle of a fresh hole's rebound is water, its rim the light.
    /// </summary>
    private static readonly Shader RipplesShader = new()
    {
        Code = @"
shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled;
uniform sampler2D wash : filter_linear, repeat_disable;
uniform vec4 wash_box = vec4(0.0, 0.0, 1.0, 1.0);
uniform float crest = 0.9;
uniform float crest_top = 4.0;
uniform float trough = -1.2;
uniform float line_px = 1.3;
uniform float slope_from = 0.05;
uniform float slope_full = 0.12;
uniform vec3 light : source_color = vec3(0.97, 1.0, 1.0);
uniform vec3 dark : source_color = vec3(0.05, 0.2, 0.2);
varying vec3 wp;
void vertex() {
    wp = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}
float cut(float x) {
    return clamp(x / max(fwidth(x), 1e-4) + 0.5, 0.0, 1.0);
}
void fragment() {
    vec2 uv = (wp.xz - wash_box.xy) / wash_box.zw;
    if (uv.x < 0.0 || uv.y < 0.0 || uv.x > 1.0 || uv.y > 1.0) discard;
    float h = texture(wash, uv).r;
    // A crest is a line where the water crosses the crest height - a ring of
    // light running out - and filled only where it stands tallest; a trough a
    // faint dark. Filled bands at the first cut laid pale sheets over half the
    // pond after the splash.
    float d = abs(h - crest) / max(fwidth(h), 1e-4);
    float line = 1.0 - smoothstep(line_px - 0.5, line_px + 0.5, d);
    // Only on a front: where the water is steep. The flat top of a small bump
    // just over the crest height drew a closed loop of its own, and a pond
    // full of those read as contour lines on a map.
    float slope = fwidth(h) / max(fwidth(wp.x) + fwidth(wp.z), 1e-4);
    line *= smoothstep(slope_from, slope_full, slope);
    float top = cut(h - crest_top);
    float lo = cut(trough - h);
    float hi = max(line, top * 0.6);
    if (hi + lo < 0.01) discard;
    ALBEDO = mix(dark, light, step(0.01, hi));
    ALPHA = hi * 0.8 + lo * (1.0 - hi) * 0.22;
}
",
    };
}
