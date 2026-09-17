using System;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The water a hull throws going into the pond off the bank: a bow wave ahead
/// of the nose and two fans of spray off the flanks, every drop thrown once and
/// falling back under gravity, gone the moment it meets the surface again.
///
/// <b>Not a burst, and that is the whole reason this class exists.</b>
/// <see cref="Stage3D.Splash"/> is <see cref="SheetBlast"/> with a white ramp -
/// a round detonating in water, which throws a column up and leaves it hanging
/// for two seconds. A hull entering water does none of that: it displaces, so
/// what comes up comes up low and wide, ahead and to the sides, and it is back
/// on the surface inside a second. Read off the bench as smoke standing on the
/// deck - docs/swim-plan.md, step 10.
///
/// <b>Two looks, one model.</b> <see cref="Style.Calm"/> is the bow wave: low,
/// wide, short, the fans barely over the tracks. <see cref="Style.Cinematic"/>
/// throws the fans a hull high and lets them fall for a second. The same drops,
/// the same arithmetic, different <see cref="Shape"/> - the panel switches
/// between them (<c>ground.splash</c>, <c>bench.splash</c>, <c>--splash</c>).
///
/// <b>Closed form, like <see cref="SheetBlast"/> and for its reason.</b> Where a
/// drop is at time t is written out, not stepped, so a capture at a pinned step
/// is the same picture twice and the clock can be scrubbed. Every length is in
/// board px on the ground (world XZ) and screen px of rise (world Y over
/// <c>rise</c>), scaled by the hull's own size through <see cref="Sit"/>.
/// </summary>
public sealed partial class Plunge : Node3D
{
    public enum Style { Calm, Cinematic }

    /// <summary>Which of the two looks this one is thrown in. Read at
    /// <see cref="Fire"/>, so a switch on the panel takes the next plunge and
    /// leaves one in the air alone.</summary>
    public Style Look = Style.Cinematic;

    /// <summary>How hard the hull hits - <see cref="TankTick.PlungeMight"/>:
    /// mass, seven tenths for a light, one and a fifth for a heavy. Speeds go
    /// by its square root (a heavier hull displaces more, not faster), the
    /// count by the number itself.</summary>
    public float Might = 1.0f;

    /// <summary>The figures of one look. Speeds in px/s, size in px, share the
    /// fraction of the drops that are the bow wave rather than the fans.</summary>
    public readonly record struct Shape(float BowUp, float BowOut, float FanUp,
                                        float FanOut, float Size, int Drops,
                                        float BowShare, float Spread);

    /// <summary>
    /// The two looks. Heights follow from the up speeds through
    /// <see cref="Gravity"/>: apex = v²/2g, so calm fans top out a third of a
    /// hull over the water and cinematic ones a hull and a half. The bow wave is
    /// the lower of the two in both - it is pushed out, not up.
    /// </summary>
    // <b>Out more than up, in both looks, and the camera is why.</b> On this
    // board a screen row is a place on the ground: a streak going straight up
    // reads as a streak going north, over the bank the hull just left. Thrown
    // wide, a fan is a fan - it has somewhere to come down that is not behind
    // the tank. The first cut had FanOut at a third of FanUp and every fan
    // stood on the far bank.
    //
    // <b>And no loose drops at all - the sheet is the whole event.</b> The
    // first cut threw single drops two hulls out to the sides and they read as
    // a cartoon (the user's word); pulled in to the hull's width they were
    // still the one thing in the picture that was not water, and the user had
    // them taken out. What a heavy thing going into water shows is the mass: a
    // white sheet standing up along the flanks and ahead of the nose and
    // falling back. So every "drop" here is a puff of that sheet - big, soft,
    // slow, low - and the two looks differ in how high the sheet stands.
    public static Shape Of(Style style) => style == Style.Calm
        ? new Shape(BowUp: 130.0f, BowOut: 40.0f, FanUp: 60.0f, FanOut: 22.0f,
                    Size: 28.0f, Drops: 110, BowShare: 0.6f, Spread: 0.35f)
        : new Shape(BowUp: 180.0f, BowOut: 55.0f, FanUp: 120.0f, FanOut: 32.0f,
                    Size: 28.0f, Drops: 160, BowShare: 0.5f, Spread: 0.45f);

    /// <summary>Screen px of rise a second squared. Not the world's: the board
    /// is a picture, and what this is tuned against is how long a fan a hull
    /// high takes to come down - about a second at this figure.</summary>
    public const float Gravity = 700.0f;

    /// <summary>The most drops either look wants: the instance array is sized
    /// once and the shape only says how many of them are drawn.</summary>
    public const int Most = 200;

    /// <summary>How high the flanks' sheet of a look gets, in screen px of
    /// rise, for the fastest puff it throws - the check's handle on the two
    /// looks being two.</summary>
    public static float Peak(Style style)
    {
        float up = Of(style).FanUp * 1.3f;
        return up * up / (2.0f * Gravity);
    }

    /// <summary>How long the whole event is, from the first drop to the last
    /// one falling back: the latest birth plus the longest flight.</summary>
    public static float Length(Style style) =>
        LastBirth + 2.0f * Of(style).FanUp * 1.3f / Gravity;

    private const float LastBirth = 0.45f;

    /// <summary>Where a drop is at <paramref name="t"/> seconds after the hull
    /// went in, or null while it is unborn or back in the water. Ground offset
    /// along the hull (x) and across it (y) in board px, height in screen px of
    /// rise, size in px, how opaque it is, and its velocity - ground px/s along
    /// and across, screen px/s up - for the streak it is drawn as. Public and
    /// static so the check can ask the model rather than restate it.</summary>
    public static (Vector2 ground, float height, float size, float alpha,
                   Vector2 run, float climb)? Drop(
        int k, float t, Shape shape, float might, float halfLen, float halfWide,
        int seed)
    {
        if (k >= shape.Drops)
            return null;
        bool bow = Hash(k, 1, seed) < shape.BowShare;
        float side = Hash(k, 2, seed) < 0.5f ? -1.0f : 1.0f;
        // The bow wave is the first thing off the nose; the flanks' sheet
        // arrives as they go in, over the first half second of the fall.
        float born = bow ? 0.02f + 0.16f * Hash(k, 3, seed)
                         : 0.06f + (LastBirth - 0.06f) * Hash(k, 4, seed);
        float age = t - born;
        if (age <= 0.0f)
            return null;
        float speed = Mathf.Sqrt(Mathf.Max(might, 0.05f));
        float up, along, across;
        Vector2 at;
        if (bow)
        {
            // <b>Ahead of the nose and the whole hull wide</b>, and denser than
            // the flanks: on screen the nose is the bottom of the sprite and
            // the water there is already white with the collar, so a thin bow
            // wave vanished into it - the user could not find it. Seated a
            // little further out than the flanks' sheet and given the extra
            // share of the puffs (BowShare), so it stands in front of the
            // tracks rather than under them.
            float u = 2.0f * Hash(k, 6, seed) - 1.0f;
            at = new Vector2(halfLen * (0.9f + 0.4f * Hash(k, 5, seed)),
                             halfWide * 1.1f * u);
            along = shape.BowOut * (0.6f + 0.8f * Hash(k, 7, seed)) * speed;
            across = u * shape.BowOut * 0.5f * speed;
            up = shape.BowUp * (0.7f + 0.6f * Hash(k, 8, seed)) * speed;
        }
        else
        {
            at = new Vector2(halfLen * (-0.5f + 1.3f * Hash(k, 5, seed)),
                             side * halfWide * 1.05f);
            along = shape.FanOut * 0.3f * (Hash(k, 9, seed) - 0.3f) * speed;
            across = side * shape.FanOut * (0.5f + Hash(k, 7, seed)) * speed;
            up = shape.FanUp * (0.6f + 0.7f * Hash(k, 8, seed)) * speed;
        }
        float height = up * age - 0.5f * Gravity * age * age;
        if (height < -1.0f)
            return null;
        // Its own flight, so the fade is spent by the time it lands.
        float flight = Mathf.Max(2.0f * up / Gravity, 0.05f);
        float a = Mathf.Clamp(age / flight, 0.0f, 1.0f);
        float size = shape.Size * (0.7f + 0.6f * Hash(k, 10, seed))
                     * (1.0f + shape.Spread * 2.0f * a) * Mathf.Sqrt(speed);
        float alpha = (bow ? 0.75f : 0.6f) * (1.0f - Mathf.SmoothStep(0.45f, 1.0f, a));
        return (at + new Vector2(along, across) * age, height, size, alpha,
                new Vector2(along, across), up - Gravity * age);
    }

    /// <summary>Whether drop <paramref name="k"/> belongs to the bow wave
    /// rather than to the flanks' sheet - the same coin <see cref="Drop"/>
    /// tosses, exposed rather than restated.
    ///
    /// <b>Because nothing outside can tell the two apart by looking.</b> They
    /// overlap on the ground: the sheet is seated anywhere along
    /// <c>halfLen * (-0.5 .. 0.8)</c> and the wave from <c>0.9</c> out, so a cut
    /// at some x catches the far end of the sheet along with the wave - and a
    /// third of the sheet is thrown backwards, its <c>along</c> term going
    /// negative, which is exactly what a claim about the bow wave going forward
    /// must not be asked about. <see cref="Drop"/> is public and static so the
    /// check can ask the model rather than restate it; this is the half of the
    /// model it could not reach, and restating it was not open to it either -
    /// the hash is private, and a second copy of a hash is a second hash.
    /// </summary>
    public static bool Bow(int k, Shape shape, int seed) =>
        k < shape.Drops && Hash(k, 1, seed) < shape.BowShare;

    /// <summary>Which way the hull is going as it goes in, in screen px on the
    /// ground - the direction the bow wave is thrown. The leg's own travel
    /// while there is one (a hull plunges mid-leg, and the frame before set
    /// it); the sprite's heading for a hull put there, which has none. The
    /// first cut read the heading always, and threw the bow wave at the bank
    /// the hull had just left.</summary>
    public static Vector2 Heading(Vehicle v) =>
        v.Travel.LengthSquared() > 1e-6f ? v.Travel
            : v.Atlas?.GroundDirection(v.Sprite.HullFacing) ?? Vector2.Down;

    private static float Hash(int k, int salt, int seed)
    {
        unchecked
        {
            uint h = (uint)(k * 374761393 + salt * 668265263 + seed * 1274126177);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFFFF) / 16777215.0f;
        }
    }

    // ------------------------------------------------------------- the machinery

    /// <summary>One of the two halves of the spray - see <see cref="Build"/>.</summary>
    private sealed class Half
    {
        public MultiMeshInstance3D? Spray;
        public MultiMesh? Many;
    }

    private readonly Half _near = new();
    private readonly Half _far = new();
    private float _rise = 1.0f;
    private float _squash = 0.5f;
    private float _clock = -1.0f;
    private Shape _shape = Of(Style.Cinematic);
    private Vector3 _seat;
    private Vector3 _along = Vector3.Right;
    private Vector3 _across = Vector3.Back;
    private float _halfLen = 60.0f;
    private float _halfWide = 33.0f;
    private int _seed;

    private static Texture2D? DropArt;

    /// <summary>Build the spray - once, like a burst: a pool of these is kept by
    /// the stage and refilled by <see cref="Fire"/>.</summary>
    ///
    /// <b>Two halves, a rung above the hull and a rung below it.</b> The hull's
    /// card writes no depth (Stage3D.PaintShader), and on the leg a plunge
    /// happens on - a level change off the bank - it is drawn free of the test
    /// as well, so the depth buffer cannot say whether a puff is before the hull
    /// or behind it. The first cut put the whole cloud a rung above the hull,
    /// and the user's screenshot (docs/swim-plan.md step 10) showed the sheet
    /// off the far flank lying over the deck and the turret. So a puff is sorted
    /// by hand: nearer the camera than the hull's middle on the ground it goes
    /// on <see cref="Stage3D.StandOrder"/> + 1 and over the hull, further away
    /// it goes a rung under and the hull covers it - <see cref="Nearer"/>. Two
    /// instances rather than one because a render priority is a material's, and
    /// a material is the instance's.
    public void Build(float squash, float rise)
    {
        _rise = Mathf.Max(rise, 0.0001f);
        Raise(_near, Stage3D.StandOrder + 1, squash, rise);
        Raise(_far, Stage3D.StandOrder - 1, squash, rise);
    }

    private void Raise(Half half, int rung, float squash, float rise)
    {
        var ink = new ShaderMaterial { Shader = Spraying, RenderPriority = rung };
        ink.SetShaderParameter("disc", DropArt ??= Disc());
        half.Many = new MultiMesh
        {
            Mesh = new QuadMesh { Size = Vector2.One },
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true,
            InstanceCount = Most,
        };
        half.Spray = new MultiMeshInstance3D
        {
            Multimesh = half.Many,
            MaterialOverride = ink,
            SortingUseAabbCenter = false,
            Visible = false,
            // Toward the camera by the board's clearance, like every other thing
            // that stands on a cell - see Stage3D.Clear.
            Position = Stage3D.Clear(squash, rise),
        };
        AddChild(half.Spray);
    }

    /// <summary>
    /// Whether a puff at <paramref name="ground"/> - along the hull, across it,
    /// in ground px - stands nearer the camera than the hull's middle, and so
    /// is drawn over the hull rather than under it. The camera looks down the
    /// world's Z, so "nearer" is a bigger Z: the puff's offset projected on Z
    /// through the hull's two ground axes. A hull going toward the camera puts
    /// its bow wave over itself and a hull going away puts it behind, where it
    /// shows only above the silhouette; the flanks' sheet splits by which flank
    /// faces the camera. Public and static so the check can ask.
    /// </summary>
    public static bool Nearer(Vector3 along, Vector3 across, Vector2 ground) =>
        along.Z * ground.X + across.Z * ground.Y > 0.0f;

    /// <summary>
    /// Where the hull went in and which way it was pointing. <paramref name="ground"/>
    /// is its contact point on the board, <paramref name="top"/> the water's
    /// height there, <paramref name="heading"/> the hull's screen-space ground
    /// direction (<see cref="AtlasSet.GroundDirection"/>, squashed), and the two
    /// half-lengths the hull's own, in ground px - so a light's splash is a
    /// light's size before <see cref="Might"/> says anything.
    /// </summary>
    public void Sit(Vector2 ground, float top, Vector2 heading, float halfLen,
                    float halfWide, float squash, float rise)
    {
        _seat = Stage3D.Trunk(ground, top, 0.0f, squash, rise).Origin;
        _squash = Mathf.Max(squash, 0.0001f);
        // A screen direction on the ground back into the world's ground plane:
        // World divides the screen row by the squash - see Stage3D.World.
        var along = new Vector3(heading.X, 0.0f, heading.Y / Mathf.Max(squash, 0.0001f));
        _along = along.LengthSquared() > 1e-6f ? along.Normalized() : Vector3.Right;
        _across = new Vector3(-_along.Z, 0.0f, _along.X);
        _halfLen = Mathf.Max(halfLen, 1.0f);
        _halfWide = Mathf.Max(halfWide, 1.0f);
    }

    public void Fire()
    {
        _shape = Of(Look);
        _seed++;
        _clock = 0.0f;
    }

    public bool Busy => _clock >= 0.0f;

    public void Tick(double delta)
    {
        if (_clock >= 0.0f)
        {
            _clock += (float)delta;
            if (_clock > Length(Look) + 0.2f)
                _clock = -1.0f;
        }
        bool on = _clock >= 0.0f;
        if (_near.Spray is not null)
            _near.Spray.Visible = on;
        if (_far.Spray is not null)
            _far.Spray.Visible = on;
        if (on)
            Dress();
    }

    private static readonly Transform3D Gone =
        new(Basis.Identity.Scaled(Vector3.Zero), Vector3.Zero);

    private void Dress()
    {
        if (_near.Many is null || _far.Many is null)
            return;
        for (int k = 0; k < Most; k++)
        {
            var drop = Drop(k, _clock, _shape, Might, _halfLen, _halfWide, _seed);
            if (drop is null)
            {
                _near.Many.SetInstanceTransform(k, Gone);
                _far.Many.SetInstanceTransform(k, Gone);
                continue;
            }
            (Vector2 ground, float height, float size, float alpha,
             Vector2 run, float climb) = drop.Value;
            Vector3 at = _seat + _along * ground.X + _across * ground.Y
                         + Vector3.Up * (height / _rise);
            // <b>A streak along its own path, not a disc.</b> A drop is drawn
            // long in the direction it is moving on screen and by how fast - the
            // motion blur a fast bright thing has on a frame - and that is the
            // difference between spray and snow. The velocity on the ground is
            // in the world's XZ; on screen the row runs down by the squash and
            // the rise is the height's own px, so the screen vector is what it
            // is below. The quad lies in the world's XY, which is the camera's
            // picture plane up to the foreshortening every billboard here has.
            Vector3 fly = _along * run.X + _across * run.Y;
            var sway = new Vector2(fly.X, climb - fly.Z * _squash);
            float pace = sway.Length();
            float turn = pace > 1e-3f ? Mathf.Atan2(sway.Y, sway.X) : 0.0f;
            // Gently: a puff of the sheet leans the way it moves and no more.
            // A streak three times its width is a comet, and a comet was half
            // of what read as a cartoon.
            float @long = size * (1.0f + Mathf.Min(0.6f, pace / 400.0f));
            float thin = size * 0.85f;
            var basis = new Basis(
                new Vector3(Mathf.Cos(turn), Mathf.Sin(turn), 0.0f) * @long,
                new Vector3(-Mathf.Sin(turn), Mathf.Cos(turn), 0.0f) * thin,
                Vector3.Back);
            // Sorted by hand every frame, not once at birth: a puff drifts, and
            // the one that crosses the hull's middle crosses rungs with it.
            bool near = Nearer(_along, _across, ground);
            MultiMesh mine = near ? _near.Many : _far.Many;
            MultiMesh other = near ? _far.Many : _near.Many;
            mine.SetInstanceTransform(k, new Transform3D(basis, at));
            mine.SetInstanceCustomData(k, new Color(1.0f, 1.0f, 1.0f, alpha));
            other.SetInstanceTransform(k, Gone);
        }
    }

    /// <summary>A soft white disc, drawn once: a drop is a highlight with a
    /// soft edge, and a texture on disk for that would be a texture nobody
    /// could tell from this one.</summary>
    private static Texture2D Disc()
    {
        const int n = 32;
        var image = Image.CreateEmpty(n, n, false, Image.Format.Rgba8);
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float dx = (x + 0.5f) / n * 2.0f - 1.0f, dy = (y + 0.5f) / n * 2.0f - 1.0f;
            float r = Mathf.Sqrt(dx * dx + dy * dy);
            float a = 1.0f - Mathf.SmoothStep(0.35f, 1.0f, r);
            image.SetPixel(x, y, new Color(1.0f, 1.0f, 1.0f, a));
        }
        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>Unshaded, blended, no depth written and none tested. The order
    /// against the hull is the two rungs of <see cref="Build"/>. The test was
    /// tried against the board (2026-09-11, <c>out/swim/22v23-south-140-diff.png</c>):
    /// it changed under five thousand pixels of a frame, all of them at the
    /// pit's corners, where the bank's walls took hard-edged rectangles out of
    /// the puffs standing lowest - and a straight cut through a cloud is the
    /// one thing the user has called a bug twice. Off, a puff over the far bank
    /// reads as height, which it is.</summary>
    private static readonly Shader Spraying = new()
    {
        Code = @"
shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, depth_test_disabled, cull_disabled;
uniform sampler2D disc : filter_linear, repeat_disable;
uniform vec4 tint : source_color = vec4(0.93, 0.97, 1.0, 1.0);
varying vec4 mine;
void vertex() {
    mine = INSTANCE_CUSTOM;
}
void fragment() {
    float a = texture(disc, UV).a * mine.a;
    ALBEDO = tint.rgb;
    ALPHA = a;
}
",
    };
}
