using Godot;

namespace TankSpriteTest;

/// <summary>
/// The shadow a round in the air throws on the ground under it.
///
/// <b>It exists for one round in the game and for one reason: on this camera,
/// up the screen and away down the board are the same movement.</b> A bomb
/// climbing over a wood and a shell running along the ground away from the
/// viewer both slide up the picture, and nothing in a flat projection separates
/// them. The streak growing with the height says it once (see
/// <see cref="Shell.Loom"/>); a shadow that stays on the ground while the round
/// leaves it says it the way every other height on this board is said - by
/// having two things where a flat round has one.
///
/// <b>A plane lying on the board and not a canvas ellipse, and that is the whole
/// of why this is a node at all.</b> The tracer is a canvas item over the entire
/// 3D world by construction (<see cref="Shell.Layer"/>), which is right for the
/// round - a streak of light disappearing behind a hull reads as a dropped frame
/// - and wrong for its shadow, which has to go <em>under</em> the hull it is
/// passing over. Anything that must lose a sort against a tank has to be in the
/// world with the tank.
///
/// <b>Built as a crater with the rim taken off</b>: <see cref="PitArt"/>'s
/// arrangement of plane, rung and sorting offset, down to the reason the rung is
/// <c>StandOrder</c> rather than one of the ground rungs - a decal below it is
/// not drawn at all, which is written out where it was found. What differs is
/// that this one moves every frame and is gone the moment the round lands, so it
/// is shown and hidden rather than dug.
/// </summary>
public sealed partial class ShellShade : Node3D
{
    /// <inheritdoc cref="PitArt.Rung"/>
    public const int Rung = Stage3D.StandOrder;

    /// <inheritdoc cref="PitArt.Deep"/>
    public const float Deep = PitArt.Deep;

    /// <summary>
    /// How wide the patch is at the ground, in screen px, before the height
    /// widens it.
    ///
    /// A shell is a few pixels across and its shadow would be too, which is a
    /// shadow nobody sees. What is being drawn is not the round's silhouette but
    /// where it is - the same liberty the contact patch under a tank takes, and
    /// for the same reason: on this board a shadow is a mark that says "here",
    /// and a true one says nothing at this size.
    /// </summary>
    public const float Wide = 26.0f;

    /// <summary>
    /// How much wider the patch gets at the top of the arc, against
    /// <see cref="Wide"/>.
    ///
    /// <b>Wider and fainter together, which is the pair that reads as
    /// height.</b> Either alone is ambiguous - a patch that only grows reads as a
    /// bigger round, one that only fades reads as a shadow going out - and the
    /// two together are what a rising thing does to the light it blocks.
    /// </summary>
    public const float Spread = 2.0f;

    /// <summary>How much of the ink survives at the top of the arc. The ground's
    /// own shadow density (<see cref="Stage3D.ShadowInk"/>) is what it starts
    /// from, so a round on the deck throws the same patch a tank's foot
    /// does.</summary>
    public const float Fades = 0.55f;

    /// <summary>The soft edge, as a share of the patch's radius: the outer part
    /// is a run out to nothing. A hard disc under a shell reads as a plate -
    /// <c>Stage3D.SeatCore</c>'s finding, borrowed rather than re-derived.
    /// </summary>
    private const float Core = 0.30f;

    private MeshInstance3D? _patch;
    private ShaderMaterial? _ink;
    private PlaneMesh? _plane;

    /// <summary>Build the plane. The two camera terms are the field's, handed in
    /// rather than read - <see cref="PitArt.Build"/>'s reason.</summary>
    public void Build(float squash, float rise)
    {
        _ink = new ShaderMaterial { Shader = Blot, RenderPriority = Rung };
        _ink.SetShaderParameter("core", Core);
        _plane = new PlaneMesh { Size = Vector2.One };
        _patch = new MeshInstance3D
        {
            Mesh = _plane,
            SortingUseAabbCenter = false,
            SortingOffset = -Deep,
            MaterialOverride = _ink,
            Visible = false,
            Position = Stage3D.Clear(squash, rise),
        };
        AddChild(_patch);
    }

    /// <summary>
    /// Put the patch under a round.
    ///
    /// <paramref name="spot"/> is the board point below the round and
    /// <paramref name="lift"/> the ground's height there - <see cref="PitArt.Show"/>'s
    /// pair, and <see cref="Stage3D.Ground"/> takes it. <paramref name="high"/> is
    /// how far up the round is as a share of its own apex, 0 on the ground and 1
    /// at the top, which is the only thing the size and the ink are read off.
    /// </summary>
    public void Show(Vector2 spot, float lift, float high, float squash,
                     float rise)
    {
        if (_patch is null || _plane is null || _ink is null)
            return;
        float wide = WideAt(high);
        _plane.Size = new Vector2(2.0f * wide, 2.0f * wide);
        Position = Stage3D.Ground(spot, lift, squash, rise);
        _ink.SetShaderParameter("ink", InkAt(high));
        _patch.Visible = true;
    }

    /// <summary>How wide the patch is at a given share of the apex, in screen
    /// px - the radius, so the plane is twice it. A named function rather than
    /// two lines inside <see cref="Show"/>, so the pair the height is read as can
    /// be asserted without a board to stand a round over.</summary>
    public static float WideAt(float high) =>
        Wide * (1.0f + (Spread - 1.0f) * high);

    /// <summary>How dark it is at that height - <see cref="WideAt"/>'s twin, and
    /// the other half of the pair.</summary>
    public static float InkAt(float high) =>
        Stage3D.ShadowInk.A * (1.0f - (1.0f - Fades) * high);

    public void Hide()
    {
        if (_patch is not null)
            _patch.Visible = false;
    }

    /// <summary>Whether it is being drawn - for the check, which cannot read a
    /// frame.</summary>
    public bool Showing => _patch?.Visible == true;

    /// <summary>How wide the plane is standing, in screen px - the same.</summary>
    public float Span => _plane?.Size.X ?? 0.0f;

    /// <summary>How much ink it is standing at - the same again.</summary>
    public float Ink =>
        (float)(_ink?.GetShaderParameter("ink").AsSingle() ?? 0.0f);

    private static readonly Shader Blot = new() { Code = BlotCode };

    private const string BlotCode = @"
shader_type spatial;
render_mode unshaded, cull_disabled, blend_mix, depth_draw_never;

// How dark the middle is. Set from Stage3D.ShadowInk, thinned by how high the
// round has got - one density for every shadow on this board.
uniform float ink = 0.45;

// How much of the radius is the flat middle. The rest runs out to nothing.
uniform float core = 0.30;

void fragment() {
    vec2 d = (UV - 0.5) * 2.0;
    float r = length(d);
    // Outside its own plane, and said so rather than left to the falloff: the
    // corners of a square plane are 1.41 of its radius - PitArt's own line.
    float a = r > 1.0 ? 0.0 : ink * (1.0 - smoothstep(core, 1.0, r));
    ALBEDO = vec3(0.0);
    ALPHA = a;
}
";
}
