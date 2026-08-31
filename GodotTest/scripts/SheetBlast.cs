using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The reference project's explosion, standing on this board.
///
/// <b>What was imported is the art and the shading model; the machinery was
/// not, because none of it survives the move.</b> The pack (Duarte David, MIT -
/// see <c>assets/ExplosionVFX/NOTICE.md</c>) is a <c>GPUParticles3D</c> pair
/// under a perspective camera with a directional light and a
/// <c>WorldEnvironment</c> doing glow and ACES, and its soft edge reads the
/// depth buffer through <c>hint_depth_texture</c>. This board is orthographic,
/// one world unit to one screen pixel, has no lights and no environment at all,
/// and runs on <c>gl_compatibility</c>, where there is no depth texture to
/// read. Four of the pack's five moving parts are answers to questions this
/// board does not ask.
///
/// The fifth is the one worth having, and it is the reason this exists:
/// <b>sixty-four frames of a simulated smoke puff, with a signed normal map on
/// every frame.</b> That is curl and self-shadowing that no noise field
/// computes - <see cref="ProcBlast"/> spends thirteen uniforms on the shape of
/// its dust and still reads as a smudge, because a sum of ellipses is a sum of
/// ellipses. So the flipbook is kept whole, and everything round it is rewritten
/// in this project's own terms:
///
/// - <b>Its particles become a <see cref="MultiMesh"/> this class writes every
///   frame.</b> A GPU particle cannot be put on a frame, and every measurement
///   on this bench is a pixel diff of two captures at a pinned step - the
///   blocker that stopped the other CC0 pack cold. Twenty-odd puffs is a model
///   small enough to keep on the CPU, so the model is on the CPU, deterministic
///   by index, and the shader is left with the section across one puff. The
///   division <see cref="ProcSmoke"/> and <see cref="ProcBlast"/> already run
///   under.
/// - <b>Its depth fade becomes an analytic fade against the ground.</b> The pack
///   needs the depth buffer because it does not know what its puffs will
///   intersect; here the only thing under a burst is the cell it went off on, and
///   the height above it is known in the vertex stage. Cheaper, works in
///   Compatibility, and identical to the eye.
/// - <b>Its lighting becomes the board's own sun.</b> The pack hands a normal to
///   a lit material and lets a <c>DirectionalLight3D</c> do the lambert. There
///   are no lights here, so the lambert is one dot product in the fragment
///   against <see cref="Stage3D.Sun"/> - the same vector the terrain's shadows,
///   the water's glint and the dust cone all read, so a puff is lit from where
///   everything else on the board is lit from.
/// - <b>Its glow does not become anything, and that is the one visible
///   loss.</b> <c>emission_energy</c> of 5 is a number for a tonemapped pipeline
///   with bloom: it means "clip to white and bleed". Written straight into
///   ALBEDO with no environment it means "white disc", so the default here is
///   1.7 and the flames are colour rather than light. Named in
///   <c>docs/blast.md</c> as the next thing to try, because it wants a
///   <c>WorldEnvironment</c> and that is a decision about the whole board rather
///   than about one effect.
///
/// The pack's three ramps are kept exactly as its <c>.tres</c> has them (see
/// <see cref="Ramp"/>), except the smoke's, which was the default black-to-white
/// - grey smoke on a brown board. It is the dust cone's own earth instead, so
/// the two bursts throw up the same ground.
/// </summary>
public sealed partial class SheetBlast : Node3D
{
    // ---------------------------------------------------------------- the model

    /// <summary>
    /// How many puffs are in the cloud proper.
    ///
    /// The pack's first emitter has twenty at <c>explosiveness 0.91</c>, which is
    /// twenty out at once; this is that, plus a couple, because a hex is 248px
    /// and the cloud is drawn bigger here than the pack's is on screen.
    /// </summary>
    public const int PuffsDefault = 22;

    public int Puffs = PuffsDefault;

    /// <summary>
    /// How many puffs come afterwards, spread over the whole life.
    ///
    /// The pack's second emitter: five, no explosiveness, same lifetime - so they
    /// keep arriving while the first twenty are dying. <b>This is what stops a
    /// flipbook cloud reading as one object fading out</b>, which is what twenty
    /// puffs on their own do: they are all on the same frame of the same sheet at
    /// the same time, so the whole cloud thins in step.
    /// </summary>
    public const int TrailsDefault = 6;

    public int Trails = TrailsDefault;

    /// <summary>The whole event, in seconds. The pack's 2.13 shortened: at 2.13
    /// the tail is a grey smear standing on the board long after the shot reads
    /// as over, and a burst that outlasts the shell's own dust cone
    /// (<see cref="ProcBlast.LifeDefault"/>) would be the slower of the two
    /// bursts.</summary>
    public const float LifeDefault = 1.55f;

    public float Life = LifeDefault;

    /// <summary>How far the cloud's own puffs get from the seat, in tile widths.
    /// The one size of the thing, <see cref="ProcBlast.Reach"/>'s role.</summary>
    public const float ReachDefault = 0.21f;

    public float Reach = ReachDefault;

    /// <summary>How wide one puff is drawn at birth, in tile widths. Note that
    /// the sheet grows the puff on its own - the flipbook is a puff expanding -
    /// so this is the size of the first frame, not of the biggest one.</summary>
    public const float SizeDefault = 0.155f;

    public float Size = SizeDefault;

    /// <summary>How much bigger a puff's quad ends than it starts, on top of what
    /// the sheet does. A little, and not none: the sheet's own growth stops at
    /// frame twenty and its last forty frames are a fixed-size cloud
    /// dissolving.</summary>
    public const float GrowDefault = 1.45f;

    public float Grow = GrowDefault;

    /// <summary>How far the middle of the cloud rises over its life, in tile
    /// widths. The pack has <c>gravity 0</c> and no climb at all - it is an
    /// explosion in the air, seen head-on. A burst in the ground is seen from
    /// above and has to leave the ground.</summary>
    public const float ClimbDefault = 0.27f;

    public float Climb = ClimbDefault;

    /// <summary>Where the middle of the cloud starts, in tile widths above the
    /// contact point. Not zero: half of every puff would be below the ground line
    /// and this camera buries that half - see <see cref="Stage3D.Stem"/>.</summary>
    public const float SeatDefault = 0.105f;

    public float Seat = SeatDefault;

    /// <summary>How much of a puff's throw is sideways rather than up. One is a
    /// disc, zero is a column. The pack throws into a full sphere, of which the
    /// bottom half is under the ground here.</summary>
    public const float FlatDefault = 0.68f;

    public float Flat = FlatDefault;

    /// <summary>How much a puff slows down over its own life: the pack's
    /// <c>linear_accel -1</c> against velocities of 0.5 to 1, which is a puff
    /// that has spent most of its travel by the time it is half dead. Nought is
    /// constant speed, one comes to a stop exactly at the end.</summary>
    public const float SlowDefault = 0.62f;

    public float Slow = SlowDefault;

    /// <summary>How much the cloud's puffs disagree about when to leave, as a
    /// share of the life. The pack's <c>explosiveness 0.91</c> is 0.09 of this;
    /// a little more, because a perfectly simultaneous cloud has one silhouette
    /// and reads as a single sprite.</summary>
    public const float StaggerDefault = 0.12f;

    public float Stagger = StaggerDefault;

    /// <summary>How much of its own life one puff gets, as a share of the whole
    /// event. Under one so a puff born late still finishes its sheet inside the
    /// event - a puff cut off mid-flipbook vanishes at full opacity, which is the
    /// one artefact of a flipbook that cannot be softened.</summary>
    public const float PuffLifeDefault = 0.78f;

    public float PuffLife = PuffLifeDefault;

    /// <summary>
    /// How far into the sheet a puff is born, as a share of it.
    ///
    /// <b>Nought is the honest reading of the flipbook and the wrong first
    /// frame.</b> Cell zero of the sheet is empty and cell one is a dot: born at
    /// the start, twenty-two puffs spend the first tenth of a second being
    /// nothing at all, so the burst opens on a bare cell and grows into itself -
    /// which is a puff of smoke starting, not a shell going off. Started a few
    /// frames in, the cloud arrives already formed, which is what a detonation
    /// does.
    ///
    /// <b>It shifts the sheet and not the age.</b> The flame is keyed to the
    /// puff's own age, so a phase that moved both would put the fire out before
    /// the smoke had arrived - which is why the two travel in separate channels of
    /// the instance data.
    /// </summary>
    public const float StartDefault = 0.11f;

    public float Start = StartDefault;

    /// <summary>Which cloud this is. The model is a hash of the index and this,
    /// so the same seed is the same burst to the pixel and a different one is a
    /// different cloud with the same numbers.</summary>
    public int Seed = 1;

    private float _clock = -1.0f;

    public bool Alive => _clock >= 0.0f;

    public float Age => _clock;

    /// <summary>Whether the clock stands still - <see cref="ProcBlast.Hold"/>'s
    /// reason, word for word, and the pack agrees: it spends a uniform
    /// (<c>still_frame</c>) on freezing every particle on one frame of the sheet.
    /// That uniform is here too, and this is the other half of it - the frame the
    /// model is on rather than the frame the sheet is on.</summary>
    public bool Hold;

    public float Clock
    {
        get => _clock;
        set => _clock = Mathf.Clamp(value, 0.0f, Life);
    }

    // ------------------------------------------------------------- the machinery

    private MultiMeshInstance3D? _cloud;
    private MultiMesh? _many;
    private ShaderMaterial? _ink;
    private float _tile = 248.0f;
    private float _rise = 1.0f;

    private static Texture2D? _sheet;
    private static Texture2D? _bulge;
    private static Texture2D? _dent;

    /// <summary>
    /// Build the cloud. <paramref name="tile"/> is the hex's width in screen px -
    /// every length above is in those - and the two camera terms are the field's,
    /// handed in rather than read, for <see cref="ProcBlast.Build"/>'s reason.
    /// </summary>
    public void Build(float tile, float squash, float rise)
    {
        _tile = tile;
        _rise = Mathf.Max(rise, 0.0001f);

        _ink = new ShaderMaterial { Shader = Puffing, RenderPriority = Stage3D.StandOrder };
        _ink.SetShaderParameter("sheet", Art("puff_sheet.png", ref _sheet));
        _ink.SetShaderParameter("bulge", Art("puff_bulge.png", ref _bulge));
        _ink.SetShaderParameter("dent", Art("puff_dent.png", ref _dent));
        _ink.SetShaderParameter("smoke_ramp", Ramp(SmokeStops, 256));
        _ink.SetShaderParameter("flame_ramp", Ramp(FlameStops, 256));
        _ink.SetShaderParameter("flame_fade", Ramp(FadeStops, 64));
        _ink.SetShaderParameter("sun", Stage3D.Sun);
        _ink.SetShaderParameter("seat_y", 0.0f);

        _many = new MultiMesh
        {
            Mesh = new QuadMesh { Size = Vector2.One },
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true,
            InstanceCount = Puffs + Trails,
        };
        _cloud = new MultiMeshInstance3D
        {
            Multimesh = _many,
            MaterialOverride = _ink,
            SortingUseAabbCenter = false,
            Visible = false,
            // Toward the camera by the board's own clearance, like every other
            // thing that stands on a cell - two coplanar surfaces are hit_scar's
            // coin toss.
            Position = Stage3D.Clear(squash, rise),
        };
        AddChild(_cloud);
        Dress();
    }

    /// <summary>Where the burst stands, through the transform a tree gets - see
    /// <see cref="ProcBlast.Sit"/>. The seat's own world height goes to the
    /// shader as well, because the ground fade is measured from it.</summary>
    public void Sit(Vector2 ground, float lift, float squash, float rise)
    {
        Transform = Stage3D.Trunk(ground, lift, 0.0f, squash, rise);
        _ink?.SetShaderParameter("seat_y", Transform.Origin.Y);
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
        // The event's own clock, which the flame is keyed to - see flame_out.
        _ink?.SetShaderParameter("time", _clock);
        Dress();
    }

    /// <summary>
    /// Put every puff where it is now.
    ///
    /// <b>The whole model, and it is deliberately arithmetic.</b> One puff is a
    /// direction, a speed that decays, a birth and a size; where it is at time t
    /// is that in closed form, which is the property that lets the clock be held
    /// and scrubbed. A simulation stepped by delta could not be scrubbed
    /// backwards and could not be captured twice - see <see cref="Hold"/>.
    /// </summary>
    private void Dress()
    {
        if (_many is null)
            return;
        int want = Mathf.Max(Puffs, 0) + Mathf.Max(Trails, 0);
        if (_many.InstanceCount != want)
            _many.InstanceCount = want;

        float t = Mathf.Max(_clock, 0.0f);
        float life = Mathf.Max(Life, 1e-3f);
        for (int k = 0; k < want; k++)
        {
            bool trail = k >= Mathf.Max(Puffs, 0);
            float born = Birth(k, trail) * life;
            float mine = Share(born / life, trail) * life;
            float a = (t - born) / mine;
            if (a <= 0.0f || a >= 1.0f)
            {
                // Nothing drawn rather than nothing sent: a degenerate quad
                // rasterises no pixels, and a hole in the instance array would
                // have to be closed by renumbering every puff every frame.
                _many.SetInstanceTransform(k, new Transform3D(
                    Basis.Identity.Scaled(Vector3.Zero), Vector3.Zero));
                _many.SetInstanceCustomData(k, new Color(0.0f, 0.0f, 0.0f, 0.0f));
                continue;
            }

            float turn = Hash(k, 23) * Mathf.Tau;
            float up = Mathf.Lerp(1.0f, 0.15f, Flat) * (0.35f + 0.65f * Hash(k, 29));
            // Speed that decays: the pack's negative linear accel, integrated.
            // Slow=1 comes to a stop exactly at the end of the puff's own life.
            float run = a * (1.0f - 0.5f * Slow * a) / Mathf.Max(1.0f - 0.5f * Slow, 1e-3f);
            float far = Reach * _tile * (trail ? 0.55f : 1.0f)
                        * (0.45f + 0.85f * Hash(k, 31)) * run;
            float x = Mathf.Cos(turn) * far;
            float y = Seat * _tile
                      + Mathf.Sin(turn) * far * up * 0.55f
                      + Climb * _tile * (trail ? 1.35f : 1.0f) * run;
            float wide = Size * _tile * (trail ? 1.25f : 1.0f)
                         * (0.72f + 0.56f * Hash(k, 37))
                         * Mathf.Lerp(1.0f, Mathf.Max(Grow, 0.05f), a);

            // Screen px into world: x is px, height is px over rise, and z is the
            // puff's own place in the stack so the order the cloud composites in
            // is a property of the model rather than of the draw order.
            var basis = Basis.Identity.Scaled(new Vector3(wide, wide / _rise, 1.0f));
            _many.SetInstanceTransform(k, new Transform3D(
                basis, new Vector3(x, y / _rise, Hash(k, 41) * 4.0f)));
            // The turn as a share of a full one (custom data is safest kept
            // inside the unit range), the puff's own age, where in the sheet that
            // age puts it, and how much of the puff is drawn at all. <b>The last
            // two are not the same number</b> - see Start.
            float phase = Mathf.Clamp(Start, 0.0f, 0.95f);
            _many.SetInstanceCustomData(k, new Color(
                Hash(k, 43), a, phase + (1.0f - phase) * a,
                trail ? 0.62f : 1.0f));
        }
    }

    /// <summary>How late in the event the last trail puff may be born, as a share
    /// of it. The pack's second emitter has no explosiveness at all, so its five
    /// puffs are spread over the whole lifetime; this stops short of the end
    /// because a puff still has to play its sheet - see <see cref="Share"/>.
    /// </summary>
    public const float TrailSpread = 0.72f;

    /// <summary>
    /// When one puff is born, as a share of the event.
    ///
    /// The cloud's are scattered by hash across the stagger - the pack's
    /// explosiveness - and the trails are spaced evenly across
    /// <see cref="TrailSpread"/>, because six of them arriving at random would
    /// leave gaps in exactly the stretch they exist to fill.
    /// </summary>
    internal float Birth(int k, bool trail) =>
        trail
            ? (k - Mathf.Max(Puffs, 0) + 0.5f) / Mathf.Max(Trails, 1) * TrailSpread
            : Hash(k, 11) * Stagger;

    /// <summary>
    /// How much of the event one puff gets, as a share of it.
    ///
    /// <b>Never more than is left, and that is the whole of this function.</b> A
    /// flipbook cut off mid-sheet does not fade - it disappears at whatever opacity
    /// that frame happens to have, which is the one hard edge this effect can show
    /// and the one thing no amount of tuning softens. A trail born at 0.72 of the
    /// event and given the full 0.78 of it ran to 1.50 and vanished on frame
    /// thirty-two, in full view, every single burst: found by the check that asks
    /// this question rather than by looking, because a puff popping out of a
    /// dissolving cloud reads as part of the dissolve.
    ///
    /// So a late puff plays its sheet faster instead. That is the right trade of
    /// the two available - the other is to stop the trails arriving late, which is
    /// the entire reason they exist.
    /// </summary>
    internal float Share(float born, bool trail) =>
        Mathf.Max(Mathf.Min(PuffLife, 1.0f - born), 1e-3f);

    /// <summary>How far through the event the last puff to finish finishes, as a
    /// share of it. One or less is the invariant; the self-test asserts it here
    /// rather than recomputing the model, so the two cannot drift.</summary>
    internal float LastFinish()
    {
        float last = 0.0f;
        int cloud = Mathf.Max(Puffs, 0);
        for (int k = 0; k < cloud + Mathf.Max(Trails, 0); k++)
        {
            bool trail = k >= cloud;
            float born = Birth(k, trail);
            last = Mathf.Max(last, born + Share(born, trail));
        }
        return last;
    }

    /// <summary>One number of the puff shader, live - <see cref="ProcBlast.Dial"/>
    /// exactly, and read out of the shader's own text for the same reason: the
    /// uniforms are where the numbers live.</summary>
    public float Dial(string uniform)
    {
        if (!_live.TryGetValue(uniform, out float now))
        {
            now = ProcBlast.Uniform(PuffCode, uniform);
            _live[uniform] = now;
        }
        return now;
    }

    public void Dial(string uniform, float value)
    {
        _live[uniform] = value;
        bool counted = PuffCode.Contains("uniform int " + uniform + " = ",
                                         StringComparison.Ordinal);
        Variant sent = counted ? Mathf.RoundToInt(value) : value;
        _ink?.SetShaderParameter(uniform, sent);
    }

    private readonly Dictionary<string, float> _live = new();

    /// <summary>
    /// One number of the model, by name.
    ///
    /// <b>A switch rather than reflection</b>, because the panel's table is
    /// checked against this: a name that is not here comes back NaN, and the
    /// self-test says which one. Three of them are counts and come back as their
    /// integer selves.
    /// </summary>
    public float Model(string name) => name switch
    {
        "puffs" => Puffs,
        "trails" => Trails,
        "life" => Life,
        "reach" => Reach,
        "size" => Size,
        "grow" => Grow,
        "climb" => Climb,
        "seat" => Seat,
        "flat" => Flat,
        "slow" => Slow,
        "stagger" => Stagger,
        "puff_life" => PuffLife,
        "start" => Start,
        "seed" => Seed,
        _ => float.NaN,
    };

    public void Model(string name, float value)
    {
        switch (name)
        {
            case "puffs": Puffs = Mathf.RoundToInt(value); break;
            case "trails": Trails = Mathf.RoundToInt(value); break;
            case "life": Life = value; break;
            case "reach": Reach = value; break;
            case "size": Size = value; break;
            case "grow": Grow = value; break;
            case "climb": Climb = value; break;
            case "seat": Seat = value; break;
            case "flat": Flat = value; break;
            case "slow": Slow = value; break;
            case "stagger": Stagger = value; break;
            case "puff_life": PuffLife = value; break;
            case "start": Start = value; break;
            case "seed": Seed = Mathf.RoundToInt(value); break;
            default:
                GD.PushWarning($"sheet blast: no model number called {name}");
                return;
        }
        Dress();
    }

    /// <summary>Every name <see cref="Model(string)"/> answers, so the panel and
    /// the check can both be built off one list rather than off two copies of
    /// one.</summary>
    internal static readonly string[] ModelNames =
    {
        "puffs", "trails", "life", "reach", "size", "grow", "climb", "seat",
        "flat", "slow", "stagger", "puff_life", "start", "seed",
    };

    /// <summary>
    /// One index's own random number.
    ///
    /// <b>Deterministic and cheap, and it has to be both.</b> Every claim about
    /// this bench is a pixel diff of two captures, so a cloud built off
    /// <see cref="GD.Randf"/> could not be captured twice; and it is called some
    /// two hundred times a frame, so it cannot allocate. The integer hash the
    /// ember noise uses, in C#.
    /// </summary>
    private float Hash(int k, int salt)
    {
        unchecked
        {
            uint h = (uint)(k * 374761393 + salt * 668265263 + Seed * 1274126177);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFFFF) / 16777215.0f;
        }
    }

    /// <summary>
    /// One of the pack's sheets, loaded once for every burst there will ever be.
    ///
    /// <b>Off the filesystem rather than as a project resource</b> -
    /// <see cref="AssetRoot"/>'s route, for its stated reason: what is loaded by
    /// path is what is on disk, so a re-exported sheet is picked up by the next
    /// run with no re-import step in between.
    ///
    /// <b>It does not avoid the importer, and it was written here as though it
    /// did.</b> The sheets sit under <c>res://</c>, so the editor imports them
    /// anyway - the three <c>.import</c> files are in that folder, committed - and
    /// what this route actually buys is that nothing here reads the imported copy.
    /// Named rather than quietly fixed, because the alternative is to move 7.6MB
    /// of art out of the project to save three small text files, and that trade is
    /// the wrong way round.
    ///
    /// Static, because they are 16MB each as RGBA8 and six bursts sharing one
    /// board must not be six copies of 48MB.
    /// </summary>
    private static Texture2D Art(string file, ref Texture2D? kept)
    {
        if (kept is not null)
            return kept;
        string path = ProjectSettings.GlobalizePath("res://assets/ExplosionVFX/") + file;
        Image? art = Image.LoadFromFile(path);
        if (art is null)
        {
            // Said out loud and answered with a texture, not with a null: a null
            // sampler is white, which is a screen-filling white square - a failure
            // that looks like a bug in the effect rather than a missing file.
            GD.PushWarning($"sheet blast: no {file} under assets/ExplosionVFX - "
                           + "the puff will be blank");
            art = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
            art.Fill(new Color(0.0f, 0.0f, 0.0f, 0.0f));
        }
        kept = ImageTexture.CreateFromImage(art);
        return kept;
    }

    /// <summary>
    /// A gradient as a strip of pixels.
    ///
    /// The pack keeps these as <c>GradientTexture1D</c> sub-resources of its
    /// material; here they are built from the same stops in code, because this
    /// project has no <c>.tres</c> files and a colour is not something
    /// <see cref="ControlPanel"/> has an organ for anyway. The stops are copied
    /// from <c>ExplosionMaterial.tres</c> unchanged except the smoke's - see the
    /// remarks on <see cref="SmokeStops"/>.
    /// </summary>
    private static ImageTexture Ramp((float At, Color Ink)[] stops, int wide)
    {
        Image strip = Image.CreateEmpty(wide, 1, false, Image.Format.Rgba8);
        for (int x = 0; x < wide; x++)
        {
            float u = (x + 0.5f) / wide;
            Color ink = stops[0].Ink;
            for (int s = 0; s + 1 < stops.Length; s++)
            {
                if (u < stops[s].At)
                    break;
                ink = u >= stops[s + 1].At
                    ? stops[s + 1].Ink
                    : stops[s].Ink.Lerp(stops[s + 1].Ink,
                        (u - stops[s].At)
                        / Mathf.Max(stops[s + 1].At - stops[s].At, 1e-5f));
            }
            strip.SetPixel(x, 0, ink);
        }
        return ImageTexture.CreateFromImage(strip);
    }

    /// <summary>Brightness of the sheet to the colour of the smoke. <b>The one
    /// ramp not copied from the pack:</b> its own is the default black-to-white,
    /// which is grey smoke, and grey smoke on this board is the wrong earth. These
    /// are the dust cone's own two dirt colours, so the two bursts throw up the
    /// same ground.</summary>
    private static readonly (float, Color)[] SmokeStops =
    {
        (0.00f, new Color(0.168f, 0.130f, 0.098f)),
        (0.50f, new Color(0.470f, 0.385f, 0.290f)),
        (1.00f, new Color(0.930f, 0.820f, 0.630f)),
    };

    /// <summary>Brightness plus the age shift to the colour of the flame, copied
    /// stop for stop from the pack: nothing, then white, then a warm yellow, then
    /// red, then out. Read left to right it is the life of a fireball, and the
    /// age shift is what walks along it.</summary>
    private static readonly (float, Color)[] FlameStops =
    {
        (0.0169f, new Color(0.0f, 0.0f, 0.0f, 0.0f)),
        (0.380f, new Color(0.965f, 0.965f, 0.965f)),
        (0.461f, new Color(1.000f, 0.800f, 0.502f)),
        (0.550f, new Color(1.000f, 0.000f, 0.000f)),
        (0.602f, new Color(0.0f, 0.0f, 0.0f)),
    };

    /// <summary>A puff's own age to how far its flame lookup has walked. The
    /// pack's, unchanged: nearly all of the walk is spent in the first third of a
    /// puff's life, which is why its flames go out long before its smoke
    /// does.</summary>
    private static readonly (float, Color)[] FadeStops =
    {
        (0.000f, new Color(0.0f, 0.0f, 0.0f)),
        (0.043f, new Color(0.439f, 0.439f, 0.439f)),
        (0.320f, new Color(1.0f, 1.0f, 1.0f)),
    };

    private static readonly Shader Puffing = new() { Code = PuffCode };

    /// <summary>The shader's text, for the panel and the check to read the
    /// numbers out of - <see cref="ProcBlast.FrameCode"/>'s arrangement.</summary>
    internal static string Code => PuffCode;

    /// <summary>
    /// The pack's fragment stage, ported.
    ///
    /// Line for line it is the same idea: the sheet's brightness is a coordinate
    /// into three ramps - albedo, flame colour, flame survival - and the signed
    /// normal lights it. What changed is listed on the class; what did not is the
    /// part worth keeping.
    /// </summary>
    private const string PuffCode = @"
shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled;

// The flipbook and its two normal halves. Nearest would show the cell grid, so
// linear, and no mipmaps: the cell is 256px drawn at about 50, and a mipmap
// chain built across cell boundaries bleeds the neighbouring frame in.
uniform sampler2D sheet : source_color, filter_linear, hint_default_transparent;
uniform sampler2D bulge : filter_linear, hint_default_black;
uniform sampler2D dent : filter_linear, hint_default_black;

uniform sampler2D smoke_ramp : source_color, filter_linear;
uniform sampler2D flame_ramp : source_color, filter_linear;
uniform sampler2D flame_fade : filter_linear;

// The sheet's layout. Eight by eight, and a uniform rather than a constant
// because the next sheet will not be.
uniform int across = 8;
uniform int down = 8;

// Every puff frozen on one frame of the sheet, or -1 for the animation. The
// pack's own debugging aid, kept because it is the only way to look at what one
// frame of the flipbook is doing.
uniform float still = -1.0;

// How much of the sheet's alpha is drawn.
uniform float puff_ink = 0.92;

// How bright the flames are. The pack says 5 and means it - it has ACES and glow
// behind it, so 5 there is 'bleed'. Here it would be 'clip', so this is 1.7.
uniform float flame_gain = 2.30;
// How far a puff's age walks the flame lookup. The pack's 0.5 - it warns that
// high values look wrong, and they do: past about 0.8 the whole cloud starts red.
uniform float flame_shift = 0.42;
// When the flame is out, in seconds <b>of the event</b>. The pack has no such
// number and does not need one: its ramp walks to red and then to black, and with
// energy 5 and bloom behind it the black end reads as a fire going out. With
// neither, the walk alone leaves a red clot sitting on the ground - red on brown,
// at the seat, for the rest of the event - so the flame is faded out as well as
// walked along.
//
// <b>The event's clock and not the puff's, which is the correction that mattered
// most.</b> Keyed to a puff's own age it is the pack's own arrangement and is
// exactly right for the pack, whose particles are all born at once - the two
// clocks are the same number there. Here six puffs arrive late on purpose, and a
// puff born a second in was reading its own age as young and lighting up: a
// white fireball opening at the bottom of a dying grey cloud. Fire happens when
// the shell goes off, not whenever a puff is new.
uniform float flame_out = 0.26;
uniform float time = 0.0;

// The lambert: how much light a puff has before the sun reaches it, and how much
// the sun is worth. Seated well off zero because a puff's dark side is smoke in
// daylight, not smoke at night.
uniform float lit_seat = 0.58;
uniform float lit_gain = 0.55;
uniform vec3 sun = vec3(0.0, 1.0, 0.0);

// How much the sheet's own turn varies per puff. Zero draws every puff of the
// cloud in the same orientation, which is the failure a flipbook is most likely
// to show: twenty copies of one picture.
uniform float spin = 1.0;

// The ground fade, in world units: where the seat is, how far below it a puff is
// gone, and the band it fades over. The pack reads the depth buffer for this;
// here the ground is known - see the class.
uniform float seat_y = 0.0;
uniform float root = 4.0;
uniform float floor_fade = 26.0;

varying float high;
varying vec4 mine;

void vertex() {
    mine = INSTANCE_CUSTOM;

    // Which cell of the sheet this puff is on, from its own age.
    float frames = float(across * down);
    float f = floor(mine.z * frames);
    if (still > -0.01) {
        f = floor(still);
    }
    f = clamp(f, 0.0, frames - 1.0);

    // Turned about its middle before it is mapped into the cell, so the cloud is
    // not twenty copies of one picture. Clamped rather than wrapped: a turn pulls
    // the corners in from outside the cell, and outside the cell is the next
    // frame of the animation.
    float turn = mine.x * spin * 6.2831853;
    vec2 uv = UV - 0.5;
    float s = sin(turn), c = cos(turn);
    uv = clamp(vec2(c * uv.x - s * uv.y, s * uv.x + c * uv.y) + 0.5, 0.0, 1.0);
    UV = uv / vec2(float(across), float(down))
         + vec2(mod(f, float(across)) / float(across),
                floor(f / float(across)) / float(down));

    // How far above the ground this corner is, in world units. The seat's own
    // world height comes in as a uniform because the node's transform carries
    // the cell's lift and this needs the difference.
    high = (MODEL_MATRIX * vec4(VERTEX, 1.0)).y - seat_y;
}

void fragment() {
    vec4 puff = texture(sheet, UV);
    float bright = max(max(puff.r, puff.g), puff.b);

    // The signed normal: two unsigned halves subtracted. The epsilon is not
    // decoration - the sheet is transparent over most of its area and normalize
    // of a zero vector is a NaN, which comes out as a black square.
    vec3 face = normalize(texture(bulge, UV).rgb - texture(dent, UV).rgb
                          + vec3(0.0, 0.0, 1e-4));
    float lit = lit_seat + lit_gain * clamp(dot(face, normalize(sun)), 0.0, 1.0);

    vec3 smoke = texture(smoke_ramp, vec2(bright, 0.5)).rgb * lit;

    // The flame: the same brightness, walked along its ramp by the puff's age.
    float gone = texture(flame_fade, vec2(mine.y, 0.5)).r;
    vec3 flame = texture(flame_ramp, vec2(bright + flame_shift * gone, 0.5)).rgb
                 * flame_gain
                 * (1.0 - smoothstep(0.0, max(flame_out, 1e-3), time));

    // Added into the albedo rather than into EMISSION: unshaded, and with no
    // environment on this board, EMISSION would be the same number with an extra
    // step in it - see the class on what is lost with the glow.
    ALBEDO = smoke + flame;
    ALPHA = clamp(puff.a * puff_ink * mine.w, 0.0, 1.0)
            * smoothstep(0.0, max(floor_fade, 1e-4), high + root);
}
";
}
