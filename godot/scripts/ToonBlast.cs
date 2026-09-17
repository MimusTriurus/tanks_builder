using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The Breath of the Wild explosion, standing on this board: a cluster of
/// sculpted puffs that pop out of the seat, light up as fire, turn to smoke and
/// are eaten away by a hard alpha cut - the stylised explosion, against the
/// simulated one (<see cref="SheetBlast"/>) and the computed ones
/// (<see cref="ProcRack"/>, <see cref="ProcWave"/>).
///
/// <b>What was imported is the three pictures and one line of shading; the
/// machinery was not, for <see cref="SheetBlast"/>'s reasons.</b> The source
/// (nekotogd, CC0 - see <c>assets/BotW/NOTICE.md</c>) is one Godot 3 shader on a
/// <c>Particles</c> node: three textures of one sculpted puff - its flat toon
/// shade, its lit lumps, its silhouette - and per particle a <c>COLOR</c> the
/// particle system walks over the puff's life, <c>COLOR.r</c> taking it from
/// fire (the lit lumps tinted orange and pushed past white) to smoke (the flat
/// shade as it is) and <c>COLOR.a</c> pulling the alpha under a scissor so the
/// puff is eaten away from its thin places inward, hard-edged, with a scrolling
/// noise warbling the cut. That is the whole look, and it is kept exactly:
/// three samples, one <c>mix</c>, one scissor.
///
/// Everything round it is this project's: the puffs are a <see cref="MultiMesh"/>
/// this class writes every frame from a closed-form model - where each puff is
/// at time <c>t</c> is arithmetic on its index, so the clock can be held,
/// scrubbed and captured twice to the pixel - and the noise is
/// <see cref="Stage3D.EmberNoiseCode"/> on the event's own clock, not
/// <c>TIME</c>. The billboard is the instance basis built in screen px and
/// divided into world on the way out, as the sheet's puffs are. The puffs are
/// written into the instance array far-to-near each frame, so the cluster sorts
/// within itself without writing depth; against the tanks the whole cluster
/// sorts as one thing standing on its seat, which is right for a fireball that
/// stands on the wreck.
///
/// <b>Why it is worth having beside the other two.</b> The sheet's puff is a
/// simulation and reads as real smoke; the computed ones read as glow and
/// soot. This one is a drawing: flat tones, a silhouette with lobes, an edge
/// that is a line. On a board of painted sprites that is a third answer to what
/// an explosion should look like, and the bench exists to put the three on one
/// board at one frame.
/// </summary>
public sealed partial class ToonBlast : Node3D
{
    // ---------------------------------------------------------------- the model

    /// <summary>How many puffs. The source's emitter has about a dozen; a hex
    /// is 248px and the cluster is drawn bigger here.</summary>
    public const int PuffsDefault = 16;
    public int Puffs = PuffsDefault;

    /// <summary>The whole event, in seconds. BotW's explosion is quick: the
    /// fire is a flash, the smoke is gone in a second and a half.</summary>
    public const float LifeDefault = 1.40f;
    public float Life = LifeDefault;

    /// <summary>How far a puff's centre gets from the seat, in tile widths.</summary>
    public const float ReachDefault = 0.55f;
    public float Reach = ReachDefault;

    /// <summary>One puff's width at full size, in tile widths.</summary>
    public const float SizeDefault = 0.42f;
    public float Size = SizeDefault;

    /// <summary>How much a puff grows from its pop to its end, on top of
    /// <see cref="Size"/>.</summary>
    public const float GrowDefault = 1.30f;
    public float Grow = GrowDefault;

    /// <summary>How hard the throw slows: 1 stops exactly at the end of the
    /// puff's life, 0 is constant speed.</summary>
    public const float SlowDefault = 0.95f;
    public float Slow = SlowDefault;

    /// <summary>How much the puffs disagree about leaving, as a share of the
    /// event. Small: BotW's puffs all pop together.</summary>
    public const float StaggerDefault = 0.06f;
    public float Stagger = StaggerDefault;

    /// <summary>How long the white flash lasts at the start of a puff's life,
    /// as a share of it.</summary>
    public const float FlashDefault = 0.08f;
    public float Flash = FlashDefault;

    /// <summary>When a puff is halfway from fire to smoke, as a share of its
    /// life, and over what share the turn happens.</summary>
    public const float BurnDefault = 0.34f;
    public float Burn = BurnDefault;
    public const float BurnSpreadDefault = 0.28f;
    public float BurnSpread = BurnSpreadDefault;

    /// <summary>When the alpha starts down toward the scissor, as a share of a
    /// puff's life. From here to the end the puff is eaten away.</summary>
    public const float ErodeDefault = 0.50f;
    public float Erode = ErodeDefault;

    /// <summary>How high the cluster's middle rises over the event, in tile
    /// widths - smoke drifts up.</summary>
    public const float RiseDefault = 0.22f;
    public float Rise = RiseDefault;

    /// <summary>How much of the throw is sideways rather than up: 1 is a full
    /// circle in the screen plane, 0 is straight up. Puffs thrown downward are
    /// shortened - the ground is there.</summary>
    public const float FlatDefault = 0.85f;
    public float Flat = FlatDefault;

    /// <summary>Where the seat is above the ground, in tile widths.</summary>
    public const float SeatDefault = 0.10f;
    public float Seat = SeatDefault;

    /// <summary>Which cluster: reseeds every hash.</summary>
    public const int SeedDefault = 1;
    public int Seed = SeedDefault;

    // ------------------------------------------------------------- the machinery

    private MultiMeshInstance3D? _cloud;
    private MultiMesh? _many;
    private ShaderMaterial? _ink;
    private float _tile = 248.0f;
    private float _riseFactor = 1.0f;
    private float _clock = -1.0f;
    private Transform3D _seat = Transform3D.Identity;
    private Vector3 _nudge;

    private static Texture2D? _shade;
    private static Texture2D? _lit;
    private static Texture2D? _mask;

    public bool Alive => _clock >= 0.0f;
    public float Age => _clock;

    /// <summary>Hold the clock, for a bench scrubbing through the event.</summary>
    public bool Hold;

    public float Clock
    {
        get => _clock;
        set => _clock = Mathf.Clamp(value, 0.0f, Life);
    }

    /// <summary>Build the cluster. <paramref name="tile"/> is the hex's width
    /// in screen px - every length above is in those - and the two camera terms
    /// are the field's, handed in rather than read.</summary>
    public void Build(float tile, float squash, float rise)
    {
        _tile = tile;
        _riseFactor = Mathf.Max(rise, 0.0001f);
        _ink = new ShaderMaterial { Shader = Puffing, RenderPriority = Stage3D.StandOrder };
        _ink.SetShaderParameter("shade_tex", Art("puff_shade.png", ref _shade));
        _ink.SetShaderParameter("lit_tex", Art("puff_lit.png", ref _lit));
        _ink.SetShaderParameter("mask_tex", Art("puff_mask.png", ref _mask));
        _ink.SetShaderParameter("time", 0.0f);
        _many = new MultiMesh
        {
            Mesh = new QuadMesh { Size = Vector2.One },
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true,
            InstanceCount = Mathf.Max(Puffs, 1),
        };
        _cloud = new MultiMeshInstance3D
        {
            Multimesh = _many,
            MaterialOverride = _ink,
            SortingUseAabbCenter = false,
            Visible = false,
            Position = _nudge = Stage3D.Clear(squash, rise),
        };
        AddChild(_cloud);
        Dress();
    }

    /// <summary>Where the cluster stands, through the transform a tree gets.</summary>
    public void Sit(Vector2 ground, float lift, float squash, float rise)
    {
        _seat = Stage3D.Trunk(ground, lift, 0.0f, squash, rise);
        Transform = _seat;
    }

    public void Fire() => _clock = 0.0f;
    public void Douse() => _clock = -1.0f;

    public void Tick(double delta)
    {
        if (_clock >= 0.0f && !Hold)
        {
            _clock += (float)delta;
            if (_clock > Life)
                _clock = -1.0f;
        }
        bool on = _clock >= 0.0f;
        if (_cloud is not null)
            _cloud.Visible = on;
        if (!on)
            return;
        _ink?.SetShaderParameter("time", _clock);
        Dress();
    }

    /// <summary>A deterministic number in [0, 1) for puff <paramref name="k"/>
    /// and a salt - <see cref="SheetBlast"/>'s arrangement, so two runs agree.</summary>
    private float Hash(int k, int salt)
    {
        unchecked
        {
            uint h = (uint)(k * 73856093) ^ (uint)(salt * 19349663) ^ (uint)(Seed * 83492791);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFFFF) / 16777215.0f;
        }
    }

    /// <summary>When puff <paramref name="k"/> is born, as a share of the event.</summary>
    internal float Birth(int k) => Hash(k, 11) * Mathf.Clamp(Stagger, 0.0f, 0.6f);

    /// <summary>How much of the event puff <paramref name="k"/> gets: what is
    /// left after its birth, less a little so the last puffs do not all end on
    /// the event's last frame.</summary>
    internal float Share(int k) =>
        Mathf.Max((1.0f - Birth(k)) * Mathf.Lerp(0.78f, 1.0f, Hash(k, 53)), 1e-3f);

    private readonly List<(float Depth, Transform3D Where, Color Mine)> _drawn = new();

    /// <summary>
    /// Put every puff where it is now: the whole model, and it is arithmetic on
    /// the index and the clock - the property that lets the clock be held and
    /// scrubbed. The puffs are written far-to-near so the cluster sorts within
    /// itself: the material writes no depth, and a hard-cut puff drawn over a
    /// nearer one would be a visible mistake where a soft one is not.
    /// </summary>
    private void Dress()
    {
        if (_many is null)
            return;
        int count = Mathf.Max(Puffs, 1);
        if (_many.InstanceCount != count)
            _many.InstanceCount = count;
        float t = Mathf.Max(_clock, 0.0f);
        float life = Mathf.Max(Life, 1e-3f);
        float seat = Seat * _tile;
        _drawn.Clear();
        for (int k = 0; k < count; k++)
        {
            float born = Birth(k) * life;
            float mine = Share(k) * life;
            float a = (t - born) / mine;
            if (a <= 0.0f || a >= 1.0f)
            {
                _drawn.Add((0.0f, new Transform3D(Basis.Identity.Scaled(Vector3.Zero), Vector3.Zero),
                            new Color(0.0f, 0.0f, 0.0f, 0.0f)));
                continue;
            }

            // The throw: a direction in the screen plane, sideways by Flat,
            // downward ones shortened because the ground is there.
            float ang = Mathf.Lerp(Mathf.Pi * 0.5f - Mathf.Pi * Mathf.Clamp(Flat, 0.0f, 1.0f),
                                   Mathf.Pi * 0.5f + Mathf.Pi * Mathf.Clamp(Flat, 0.0f, 1.0f),
                                   Hash(k, 23));
            var dir = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
            if (dir.Y < 0.0f)
                dir.Y *= 0.35f;
            float spd = 0.55f + 0.75f * Hash(k, 31);
            float slow = Mathf.Clamp(Slow, 0.0f, 1.0f);
            float run = a * (1.0f - 0.5f * slow * a) / Mathf.Max(1.0f - 0.5f * slow, 1e-3f);
            Vector2 at = dir * Reach * _tile * spd * run;
            float y = seat + at.Y + Rise * _tile * Mathf.SmoothStep(0.0f, 1.0f, a);

            // The pop: full size almost at once, then a slow growth.
            float pop = Mathf.SmoothStep(0.0f, 0.10f, a);
            float wide = Size * _tile * (0.70f + 0.60f * Hash(k, 37))
                         * pop * Mathf.Lerp(1.0f, Mathf.Max(Grow, 0.05f), a);

            // Upright quad in screen px, divided into world on the way out; the
            // puff's own turn is done in the shader, on its UV.
            var basis = new Basis(new Vector3(wide, 0.0f, 0.0f),
                                  new Vector3(0.0f, wide / _riseFactor, 0.0f),
                                  new Vector3(0.0f, 0.0f, 1.0f));
            float depth = Hash(k, 41) * 6.0f;

            // What the source walks over COLOR: fire to smoke, alpha to the
            // scissor, and the white flash that is this project's addition.
            float burn = Mathf.SmoothStep(Burn - BurnSpread * 0.5f, Burn + BurnSpread * 0.5f,
                                          a + 0.10f * (Hash(k, 43) - 0.5f));
            float fade = 1.0f - Mathf.SmoothStep(Mathf.Clamp(Erode, 0.0f, 0.98f), 1.0f, a);
            float flash = 1.0f - Mathf.SmoothStep(0.0f, Mathf.Max(Flash, 1e-3f), a);
            _drawn.Add((depth,
                        new Transform3D(basis, new Vector3(at.X, y / _riseFactor, depth)),
                        new Color(Hash(k, 47), burn, fade, flash)));
        }
        // Far to near: the smaller depth first.
        _drawn.Sort((p, q) => p.Depth.CompareTo(q.Depth));
        for (int i = 0; i < _drawn.Count; i++)
        {
            _many.SetInstanceTransform(i, _drawn[i].Where);
            _many.SetInstanceCustomData(i, _drawn[i].Mine);
        }
    }

    // ---------------------------------------------------------------- the dials

    private readonly Dictionary<string, float> _live = new();

    /// <summary>One number of the puff shader, live: what was written, else the
    /// text's own default.</summary>
    public float Dial(string uniform)
    {
        if (!_live.TryGetValue(uniform, out float now))
        {
            now = ProcBlast.Uniform(Code, uniform);
            _live[uniform] = now;
        }
        return now;
    }

    public void Dial(string uniform, float value)
    {
        _live[uniform] = value;
        _ink?.SetShaderParameter(uniform, value);
    }

    /// <summary>One number of the model, by the name the panel uses; NaN for a
    /// name the model has no field for, which is what the self test looks for.</summary>
    public float Model(string name) => name switch
    {
        "puffs" => Puffs,
        "life" => Life,
        "reach" => Reach,
        "size" => Size,
        "grow" => Grow,
        "slow" => Slow,
        "stagger" => Stagger,
        "flash" => Flash,
        "burn" => Burn,
        "burn_spread" => BurnSpread,
        "erode" => Erode,
        "rise" => Rise,
        "flat" => Flat,
        "seat" => Seat,
        "seed" => Seed,
        _ => float.NaN,
    };

    public void Model(string name, float value)
    {
        switch (name)
        {
            case "puffs": Puffs = Mathf.RoundToInt(value); break;
            case "life": Life = value; break;
            case "reach": Reach = value; break;
            case "size": Size = value; break;
            case "grow": Grow = value; break;
            case "slow": Slow = value; break;
            case "stagger": Stagger = value; break;
            case "flash": Flash = value; break;
            case "burn": Burn = value; break;
            case "burn_spread": BurnSpread = value; break;
            case "erode": Erode = value; break;
            case "rise": Rise = value; break;
            case "flat": Flat = value; break;
            case "seat": Seat = value; break;
            case "seed": Seed = Mathf.RoundToInt(value); break;
            default:
                GD.PushWarning($"toon blast: no model number named '{name}'");
                return;
        }
        Dress();
    }

    // ------------------------------------------------------------------- the art

    /// <summary>One of the three pictures, loaded once for every cluster there
    /// will ever be - <see cref="SheetBlast"/>'s route, for its reasons.</summary>
    private static Texture2D Art(string file, ref Texture2D? kept)
    {
        if (kept is not null)
            return kept;
        string path = ProjectSettings.GlobalizePath("res://assets/BotW/") + file;
        Image? art = Image.LoadFromFile(path);
        if (art is null)
        {
            GD.PushWarning($"toon blast: no {file} under assets/BotW - the puff will be blank");
            art = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
            art.Fill(new Color(0.0f, 0.0f, 0.0f, 0.0f));
        }
        kept = ImageTexture.CreateFromImage(art);
        return kept;
    }

    // ---------------------------------------------------------------- the shader

    /// <summary>
    /// The source's fragment, in this project's terms. Its three samples and one
    /// <c>mix</c> are kept as written; what changed: <c>COLOR</c> becomes the
    /// instance's custom data (turn, burn, fade, flash), <c>TIME</c> becomes the
    /// event's clock, the noise texture the source does not ship becomes
    /// <see cref="Stage3D.EmberNoiseCode"/>, and the billboard matrix the source
    /// builds in the vertex stage is the instance basis instead. The white flash
    /// at the pop is an addition: BotW's explosion opens white before it is
    /// orange, and the source's own scene got that from the particle's
    /// colour ramp rather than from the shader.
    /// </summary>
    private const string PuffShader = @"
shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled;

FLAME_NOISE

// The sculpted puff, three ways: its flat toon shade, its lit lumps, its
// silhouette. All three transparent outside the puff.
uniform sampler2D shade_tex : source_color, filter_linear, hint_default_transparent;
uniform sampler2D lit_tex : source_color, filter_linear, hint_default_transparent;
uniform sampler2D mask_tex : source_color, filter_linear, hint_default_transparent;

// The source's numbers: the fire's colour and how far past white it is pushed,
// how much the noise warbles the cut, and where the cut is.
uniform vec3 fire_colour : source_color = vec3(0.99, 0.31, 0.01);
uniform float fire_strength = 4.3;
uniform float warble = 0.20;
uniform float warble_scale = 2.6;
uniform float warble_speed = 0.9;
uniform float scissor = 0.23;
// This project's: the two tones the flat shade is read into - the source's
// picture is grey 0.29 for the body and pure black in the clefts, which on a
// pale board is half the cluster gone black, so the clefts are a shadow tone
// rather than a hole - how much of the lit lumps shows through the smoke (0 is
// the source's two flat tones), and the flash.
uniform vec3 smoke_body : source_color = vec3(0.56, 0.54, 0.52);
uniform vec3 smoke_shadow : source_color = vec3(0.24, 0.22, 0.22);
uniform float smoke_gain = 1.00;
uniform float smoke_lit = 0.40;
uniform vec3 flash_colour : source_color = vec3(1.0, 0.97, 0.86);
uniform float time = 0.0;

varying vec4 mine;
varying vec2 puff_uv;

void vertex() {
    mine = INSTANCE_CUSTOM;
    // Turned about its middle, so the cluster is not sixteen copies of one
    // picture - the source turns the billboard matrix by INSTANCE_CUSTOM.x.
    float turn = mine.x * 6.2831853;
    vec2 uv = UV - 0.5;
    float s = sin(turn), c = cos(turn);
    puff_uv = clamp(vec2(c * uv.x - s * uv.y, s * uv.x + c * uv.y) + 0.5, 0.0, 1.0);
}

void fragment() {
    vec4 shade = texture(shade_tex, puff_uv);
    vec4 lit = texture(lit_tex, puff_uv);
    vec4 mask = texture(mask_tex, puff_uv);
    // The cut: the silhouette's own alpha pulled down by the puff's fade and
    // warbled by noise on the event's clock, then a hard scissor - the whole
    // of what makes the edge a line.
    float n = ember_fbm(puff_uv * warble_scale + vec2(time * warble_speed, mine.x * 7.0));
    // Inside the silhouette only: a warble that reached outside it drew the
    // quad's corners as squares wherever the noise ran high.
    float a = mask.a * (mask.b * mine.z + (n - 0.5) * 2.0 * warble);
    ALPHA = clamp(a, 0.0, 1.0);
    ALPHA_SCISSOR_THRESHOLD = scissor;
    // Fire to smoke, as the source walks it over COLOR.r.
    vec3 fire = lit.rgb * fire_colour * fire_strength;
    // The flat shade read as a tone: 0 in the clefts, 1 on the body.
    float tone = clamp(shade.r / 0.30, 0.0, 1.0);
    vec3 smoke = mix(smoke_shadow, smoke_body, tone) * smoke_gain
                 * mix(1.0, 0.55 + 0.9 * lit.r, smoke_lit);
    vec3 colour = mix(fire, smoke, mine.y);
    colour = mix(colour, flash_colour, mine.w);
    ALBEDO = colour;
}
";

    internal static readonly string Code =
        PuffShader.Replace("FLAME_NOISE", Stage3D.EmberNoiseCode);

    private static readonly Shader Puffing = new() { Code = Code };
}
