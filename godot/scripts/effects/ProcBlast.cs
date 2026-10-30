using System;
using System.Globalization;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The text every burst on this board is made of, and the two ways a number is
/// read back out of it.
///
/// <b>This was a burst.</b> A high-explosive round going off in the ground,
/// computed: a flash and fireball at the seat, a cone of earth thrown out of it,
/// dust that outlived both, rings spreading on the ground - three quads and
/// sixty-two tuned uniforms, its shape solved per fragment. It stood beside
/// <see cref="SheetBlast"/>, which plays sixty-four rendered frames of a
/// simulated cloud, so the two could be judged on one board at one frame; and it
/// lost, on earth and then on water. What a fragment summing eighty ellipses
/// cannot buy at any price is curl and self-shadowing, and that is most of what
/// makes a cloud read as one.
///
/// <b>What is left is the part four other effects were already made of</b>, which
/// is why this file did not go with the picture:
///
/// - <see cref="DustInk"/> - what a cloud of earth IS: its profile, its tearing,
///   its packing, its stepped light. Compiled into <c>ProcKick</c>,
///   <c>ProcSlam</c>, <c>ProcSpall</c> and <c>ProcRack</c>, so the dust a gun
///   blows off the ground and the soot off a detonating rack are one text rather
///   than eleven tuned numbers written four times.
/// - <see cref="FrameCode"/> - what each of them is told about the quad it is
///   drawn on, where the event sits in it, and the throw and the footing every
///   element is placed by. <see cref="SheetBlast"/> takes this too.
/// - <see cref="RingCode"/> and <see cref="Ringing"/> - the rings that spread on
///   the ground, on their own flat plane. <c>ProcRack</c> takes them outright.
/// - <see cref="Quad"/>, <see cref="Uniform"/> and <see cref="Colour"/> - how big
///   a quad is for a given reach, and the two readers that let a check ask a
///   shader what it actually compiled with instead of trusting a C# copy of it.
///
/// <b>Named for the effect it used to be, and deliberately.</b> Every mass on this
/// board says <c>ProcBlast.DustInk</c> in its own source, and a rename would be
/// two hundred edits across the code and the docs to say the same thing. What the
/// name means is written here instead.
///
/// <b>Everything in the text is quoted in tile widths</b>, because an event on a
/// cell has a cell for its unit - a board drawn at another zoom moves every effect
/// with it and needs no number touched. And time arrives as a uniform rather than
/// <c>TIME</c>, because two runs of the same flags have to be the same picture:
/// see <see cref="FrameClock.FixedStep"/>.
/// </summary>
public static class ProcBlast
{

    /// <summary>
    /// The quad the burst is drawn on, in screen pixels: from <c>-Foot</c> across
    /// <c>Size</c>, which is what <see cref="Stage3D.Stem"/> takes.
    ///
    /// Its bottom edge is the contact point exactly, so the seat sits on the
    /// ground line and <c>foot_v</c> is 1. Given as a static function of the tile
    /// rather than read off a built node, so how big a burst is can be asserted
    /// without a board under it - <see cref="Stage3D.PyreQuad"/>'s arrangement.
    /// </summary>
    public static (Vector2 Foot, Vector2 Size) Quad(float tile, float flank, float tall)
    {
        float half = flank * tile, up = tall * tile;
        return (new Vector2(half, up), new Vector2(half * 2.0f, up));
    }

    /// <summary>
    /// Build the pair of quads. <paramref name="tile"/> is the hex's own width in
    /// screen pixels - every length above is in those - and the two camera terms
    /// are the field's, handed in rather than read so this needs no board.
    /// </summary>
    /// <summary>What both halves of the burst are told about the quad they are
    /// drawn on, and where the burst sits in it. Shared text rather than a copy
    /// per shader, for <see cref="Stage3D.FlameInk"/>'s reason at a smaller
    /// scale: a dust cone and the fire in the middle of it, seated at two
    /// different heights, are two effects that happen to be in the same
    /// place.</summary>
    internal static string FrameCode => BlastFrame;

    private const string BlastFrame = @"
// <b>The four numbers C# owns are declared at zero, and that is on purpose.</b>
// Each of them is written by ProcBlast.Ink every time a material is made - the
// quad's own size has to be known on both sides of the boundary, because C# is
// what builds the mesh - so a default here is a second declaration of the same
// fact, and the second one wins on any run that forgets to write it. Zero cannot
// be mistaken for a burst: nothing is thrown, nothing is drawn, and the omission
// is loud. Read the other way round, the shader's default silently drew a burst
// of a different size from the quad that was built for it, and it took the check
// on the model to notice.
//
// The quad in tile widths, and where the contact point sits down it.
uniform float foot_v = 0.0;
uniform float tall = 0.0;
uniform float wideq = 0.0;
// The seat above the contact point, and the band the burst fades out over below
// it. There is no drawing under the contact point at all - see the C# note.
uniform float root = 0.03;
uniform float floor_fade = 0.055;
// How high the burst throws, in tile widths: the one size in the whole thing, and
// every other length here is a ratio to it. ProcBlast.Reach is where it lives -
// see above on why this is zero.
uniform float reach = 0.0;
// Seconds since the round went off, and whether anything is drawn at all.
uniform float time = 0.0;
uniform float level = 0.0;
uniform vec3 sun = vec3(-0.40, 0.82, -0.41);

// How wide the burst throws, as a multiple of how high it throws. Here rather
// than with the earth because the sparks are thrown by the same explosion: two
// numbers for one throw would part at whichever of them nobody touched.
uniform float spread = 1.15;

// <b>Where something thrown out of this burst is, and which way it is going.</b>
// Stated as an apex rather than as a launch speed and a gravity: written the
// second way - which is how this started - the two numbers fight, the fall takes
// most of the throw back, and the cone comes out a low heap of earth sitting in a
// hole however far the speed is pushed. Here the parabola is stated by where it
// is going: up by <c>run * fast</c> at the middle of the element's life, back on
// the ground exactly at the end of it, and out sideways all the while.
//
// <c>a</c> is the element's own age in 0..1, <c>lean</c> its launch angle off
// straight up and <c>side</c> which way it went. Returns the offset in xy and the
// direction of travel in zw, so the element can be stretched along its own
// velocity - a clod at the top of its arc lying vertically is the single thing
// that makes a cone read as thrown earth rather than as a fan.
vec4 blast_throw(float a, float run, float fast, float lean, float side) {
    float up = run * 4.0 * fast * a * (1.0 - a);
    float out_at = sin(lean) * side * run * spread * fast * a;
    vec2 vel = vec2(sin(lean) * side * run * spread * fast,
                    run * 4.0 * fast * (1.0 - 2.0 * a));
    vec2 flow = length(vel) < 1e-4 ? vec2(0.0, 1.0) : normalize(vel);
    return vec4(out_at, up, flow.x, flow.y);
}

// The fragment in tile widths from the seat: x across from the middle, y up.
// Takes the UV rather than reading it: a built-in is a local of the function it
// is declared in, and reaching for UV inside a helper fails to compile.
vec2 blast_at(vec2 uv) {
    return vec2((uv.x - 0.5) * wideq, (foot_v - uv.y) * tall - root);
}

// Out before the ground line rather than at it - the band is above the line
// because there is no below.
float blast_footing(vec2 at) {
    return smoothstep(0.0, max(floor_fade, 1e-4), at.y + root);
}
";

    /// <summary>
    /// One of a shader's own uniform defaults, read out of its text.
    ///
    /// <b>Read rather than kept a second time in C#, and that is the point.</b>
    /// The tuning numbers belong in the shader, because tuning them is what a
    /// sweep does; but how big the burst is has to be checkable against how big
    /// its quad is, and a C# copy of <c>reach</c> would agree with the shader's
    /// until the first sweep landed in one of them. So the check reads the
    /// numbers the shader actually compiles with.
    ///
    /// Returns <paramref name="ifMissing"/> when the name is not there, so a
    /// renamed uniform fails the check that uses it rather than throwing inside
    /// the test run.
    /// </summary>
    internal static float Uniform(string code, string name, float ifMissing = float.NaN)
    {
        // <b>Counts as well as sizes.</b> Six of these are declared int - how many
        // clods, balls, sparks, puffs, rings - and read as floats only, they came
        // back NaN, which a panel then wrote into the shader and every loop it
        // governed ran zero times. The layer simply vanished, with no error
        // anywhere: exactly the shape of failure a missing name should not have.
        string kind = code.Contains("uniform int " + name + " = ",
                                    StringComparison.Ordinal)
            ? "uniform int " : "uniform float ";
        int at = code.IndexOf(kind + name + " = ", StringComparison.Ordinal);
        if (at < 0)
            return ifMissing;
        at += kind.Length + name.Length + " = ".Length;
        int end = code.IndexOf(';', at);
        return end < 0
               || !float.TryParse(code[at..end], NumberStyles.Float,
                                  CultureInfo.InvariantCulture, out float value)
            ? ifMissing : value;
    }

    /// <summary>
    /// One of a shader's own colour stops, read out of its text the way
    /// <see cref="Uniform"/> reads a number, and for its reason.
    ///
    /// <b>Written because the four dust effects on this board are now told apart
    /// partly by their colour, and that is a claim about a pair rather than about
    /// a swatch.</b> <c>dust_seat</c> minus <c>dust_dense</c> puts a dense element
    /// near 0.24, so almost every pixel of a cloud shows its <em>dark</em> stop -
    /// which makes the dark stop the brightness of the whole cloud, and makes "the
    /// soot of a burst is darker than the coat a bounce knocks loose" a statement
    /// that can be checked rather than looked at.
    ///
    /// Returns black for a name that is not there, so a renamed stop fails the
    /// check that uses it rather than throwing inside the test run - the number
    /// reader's arrangement, with the one difference that a colour has no
    /// meaningful NaN.
    /// </summary>
    internal static Color Colour(string code, string name)
    {
        int at = code.IndexOf("uniform vec3 " + name + " = vec3(",
                              StringComparison.Ordinal);
        if (at < 0)
            return Colors.Black;
        at = code.IndexOf('(', at) + 1;
        int end = code.IndexOf(')', at);
        if (end < 0)
            return Colors.Black;
        string[] parts = code[at..end].Split(',');
        float Channel(int n) =>
            n < parts.Length
            && float.TryParse(parts[n], NumberStyles.Float,
                              CultureInfo.InvariantCulture, out float v)
                ? v : 0.0f;
        // One channel is what every reader wants and three is what a stop has, so
        // the whole colour comes back and the caller takes what it is comparing.
        return new Color(Channel(0), Channel(1), Channel(2));
    }

    /// <summary>
    /// The rings a burst pushes out along the ground: a bright disc under it at
    /// the instant of the shot, and two rings racing out of that and fading.
    ///
    /// <b>Its own plane and its own space.</b> Drawn in the plane's UV rather than
    /// in the quad's tile widths, so a ring is a circle here and the board's own
    /// projection squashes it into the ellipse the eye expects. Written on the
    /// upright quad instead it would have needed the camera's squash as a uniform
    /// and would still have lost its near half to the ground line.
    ///
    /// <b>Torn, not drawn with a compass.</b> A perfect annulus reads as a decal;
    /// the same noise the flame and the dust are broken up with, sampled round the
    /// ring, gives it a rim that varies - and the ring is where a burst is most
    /// likely to look like a sprite.
    ///
    /// Additive and premultiplied, for the fire's reason: this is light on the
    /// ground.
    /// </summary>
    private const string RingShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, blend_add, depth_draw_never;

FLAME_NOISE

FLAME_INK

uniform float time = 0.0;
uniform float level = 0.0;

// The rings: how many, how long each lasts, and how far apart they set off.
uniform int rings = 2;
uniform float ring_life = 0.34;
uniform float ring_stagger = 0.09;
// How wide the band is and how much of the plane a ring is allowed to reach -
// short of 1 so the band's outer half is never cut by the plane's own edge.
uniform float ring_wide = 0.115;
uniform float ring_far = 0.88;
// Dimmer than it wants to be, and that is the note worth keeping: read at the
// gain a ring looks right at on its own, two of them on pale ground came out as
// lava - a bright orange annulus is the single most sprite-like thing a burst can
// draw. It is light on the ground, so it has to be light the ground merely
// catches.
uniform float ring_gain = 0.42;
uniform float ring_tear = 0.78;

// The disc under the burst at the instant of the shot: what makes the ground look
// lit rather than merely marked.
uniform float disc_life = 0.11;
uniform float disc_wide = 0.42;
uniform float disc_gain = 0.85;

void fragment() {
    if (level <= 0.0) {
        ALBEDO = vec3(0.0);
        ALPHA = 1.0;
    } else {
        vec2 d = (UV - 0.5) * 2.0;
        float r = length(d);
        vec3 lit = vec3(0.0);

        // The disc, first and briefest.
        float da = time / max(disc_life, 1e-4);
        if (da < 1.0) {
            float edge = disc_wide * (0.55 + 0.75 * da);
            float body = 1.0 - smoothstep(edge * 0.35, edge, r);
            lit += flame_ramp(0.04 + 0.30 * da).rgb * body
                   * (disc_gain * pow(max(1.0 - da, 0.0), 1.60));
        }

        // The rings.
        float turn = atan(d.y, d.x);
        for (int k = 0; k < rings; k++) {
            float fk = float(k);
            float born = ring_stagger * fk;
            float a = (time - born) / max(ring_life, 1e-3);
            if (a > 0.0 && a < 1.0) {
                float far = ring_far * pow(a, 0.55);
                float wide = ring_wide * (0.55 + 0.9 * a);
                float band = 1.0 - smoothstep(0.0, wide, abs(r - far));
                // Torn round its own circumference, and the noise is walked along
                // the ring rather than across the plane so the tear travels with
                // it instead of sitting still on the ground.
                float torn = 1.0 - ring_tear
                             + ring_tear * ember_fbm(vec2(turn * 2.4 + fk * 7.7,
                                                          far * 3.1));
                lit += flame_ramp(0.10 + 0.62 * a).rgb * band * torn
                       * (ring_gain * pow(max(1.0 - a, 0.0), 1.90));
            }
        }

        ALBEDO = lit * level;
        ALPHA = 1.0;
    }
}
";

    /// <summary>The dust as it is compiled - the shared noise, the shared ink and
    /// the shared frame, put together in one place so a check can ask whether the
    /// burst and the forest's fire are one text.</summary>
    /// <summary>
    /// What a cloud of earth is made of - the profile, the tearing, the packing
    /// and the light on the finished mass - as one text, shared with
    /// <see cref="ProcKick"/>.
    ///
    /// <b><see cref="Stage3D.FlameInk"/>'s move at a smaller scale, and for its
    /// reason.</b> A shell's earth and a gun's blown dust are the same dust; what
    /// differs is the colour and which elements there are, and those stay with
    /// each shader. Eleven tuned numbers written out twice would agree until the
    /// first sweep landed in one of them.
    ///
    /// Substituted after <see cref="BlastFrame"/> and after the ink, because it
    /// reads <c>time</c> and <c>sun</c> from the first and <c>flame_reach</c> from
    /// the second.
    /// </summary>
    internal const string DustInk = @"
// <b>What a cloud of earth is made of, shared by everything on this board that
// raises one.</b> <see cref=""Stage3D.FlameInk""/> at a smaller scale and for
// exactly its reason: the dust a shell throws up and the dust a gun blows off the
// ground are the same dust - the same profile, the same tearing, the same
// packing, the same light - and eleven tuned numbers written twice part company
// at whichever of them nobody put side by side.
//
// What is *not* in here is what differs: the colour, and which elements there
// are. A burst's earth is dark and warm and thrown up out of a seat; a muzzle's
// is pale at first and blown along the bore. Those are the two shaders' own.

// <b>How soft an element is, as the exponent of its own profile</b>, and this is
// the number the whole look turns on.
//
// The flame's section is a sphere's - <c>sqrt(1 - q)</c>, an exponent of 0.5 - and
// that is exactly right for a lick, which is a body: bright in the middle, thin
// and dark at a rim the eye can find. It is exactly wrong for dust, because a
// sphere's profile has *infinite slope* at that rim: alpha climbs off zero inside
// a pixel or two, and no amount of softening anywhere else can undo an edge that
// steep. That was the hard edge.
//
// <b>2.55 is measured, not chosen.</b> The CC0 pack's painted puff - the thing
// whose smoke reads soft - fits <c>(1 - q)^2.57</c> across all sixty-four of its
// cells, and its alpha peaks at 0.35 and reaches 0.95 nowhere in the sprite at
// all: every pixel of it is in transition. Five times the flame's exponent, and
// that ratio is the difference between a stone and a cloud.
//
// A body of earth small enough to read as a body - a clod - keeps a crisper one,
// and that is the shader's own number rather than this block's.
uniform float dust_soft = 2.55;
uniform float dust_seat = 0.52;
uniform float dust_ridge = 0.78;
uniform float dust_dense = 0.28;
// How fast piled-up elements stop adding to the mass. The whole reason the
// silhouette is one shape - see the note on fragment().
uniform float dust_pack = 3.00;
// How many steps the light is quantised to, and 0 for none. Taken from the CC0
// pack's stylised smoke, whose own value is three: banding a cloud's shading
// reads as a drawn cloud rather than as an airbrushed one, and it is the cheapest
// thing in this file.
uniform float dust_steps = 3.0;
// How much of the stepping is actually taken. <b>Not all of it, and that is the
// second half of the hard-edge finding.</b> The pack quantises the light on smoke
// that is never denser than 0.35 alpha, so its bands read as shading; taken whole
// on a mass that saturates to one they read as slabs, and the line between two
// slabs is a hard edge however soft the alpha under it is. Half-taken, the banding
// is visible as shading and the boundary is not a line.
uniform float dust_band = 0.55;
// The most of the ground a cloud may hide. Earth in the air is not a wall, and a
// mass that saturates to one is: at 1.0 the burst read as a hole in the board.
uniform float dust_ink = 0.72;
// <b>How the silhouette is torn.</b> Soft is not the same as ragged: with the
// profile alone the cloud is a smooth blob with a gentle edge, which reads as
// airbrush. The pack gets its raggedness from a painted sprite; here the summed
// density is eaten by a noise field, which is what the tree's own smoke does with
// its <c>torn</c> term. Drifting, because a static field is a stencil the cloud
// slides under.
uniform float dust_tear = 0.38;
uniform float dust_grain = 13.0;
uniform float dust_drift = 0.10;
// A toe on the way out, so thin dust fades over a longer distance than a dense
// core does. The pack spends a whole ramp on this idea - soft_blend_ramp, which
// widens the depth fade for its wispiest pixels - and on a board with no depth
// fade to widen it lands here instead.
uniform float dust_toe = 1.20;

// <b>What an element contributes, rather than what it looks like.</b> This is the
// change that took the burst from a heap of ellipses to a cloud: an element hands
// back how much earth it puts in the way and which way its own surface leans, and
// nothing else. It has no colour, no rim and no light of its own, because those
// are what made forty-four of them read as forty-four things.
//
// <c>w</c> is its coverage, <c>lean</c> its outward direction weighted by that
// coverage - summed over the mass it points where the cloud thins, which is the
// only normal a cloud has.
void dust_part(vec2 at, vec2 centre, float half_w, float along, vec2 flow,
               float lump_seed, float gain, float soft, float age,
               inout float dens, inout vec2 lean, inout float years) {
    float down;
    // The shared frame, and this element's own profile on it - see flame_reach.
    float q = flame_reach(at, centre, half_w, along, flow, lump_seed, down);
    if (q >= 1.0) {
        return;
    }
    float w = pow(1.0 - q, soft) * gain;
    dens += w;
    lean += normalize(at - centre + vec2(1e-5, 1e-5)) * w;
    years += w * age;
}

// <b>Saturating, so a pile-up stops reading as a pile.</b> Overlaps add thickness
// and thickness runs out of effect, which is what a volume does - and it is what
// keeps twenty elements in one place from being twenty times as opaque as one.
//
// Torn first, so the noise eats the density rather than the picture: a field
// applied to the finished alpha is a stencil lying on the screen, while one
// applied to the thickness is dust that is genuinely thinner in places.
float dust_mass(vec2 at, float dens) {
    float torn = 1.0 - dust_tear
                 + dust_tear * ember_fbm(at * dust_grain
                                         + vec2(0.0, -time * dust_drift
                                                      * dust_grain));
    float mass = 1.0 - exp(-dens * torn * dust_pack);
    // And a toe: thin dust leaves over a longer distance than thick dust.
    return pow(mass, dust_toe);
}

// Where on its own ramp the finished cloud sits: one surface lit once, off the
// sum of the elements' leans rather than per element - see dust_part.
//
// The ridge lives at the edge, where the mass is thin, and the body darkens as it
// thickens.
//
// <b>Seated high enough that three steps land on three bands.</b> The quantiser
// floors, so a light term that sits at 0.16 - which is what a seat of 0.46 minus
// a dense body gives - floors to nothing and the whole cloud comes out at its
// darkest stop. Measured: near-black. Seated at 0.52 the three bands land where
// they are wanted: the dense core away from the sun floors to the darkest stop,
// the body lands mid and the thin sunward edge tops out. Seated at 0.62 nothing
// reached the bottom band at all and the cloud was the same value as the ground
// it stood on - a pale smudge, which on this board is the other way to be
// invisible.
float dust_lit(float mass, vec2 lean) {
    // The cloud's own normal, out of the sum: where the mass thins.
    vec2 face = length(lean) < 1e-5 ? vec2(0.0, 1.0) : normalize(lean);
    float side = dot(face, normalize(sun.xy));
    float lit = clamp(dust_seat + dust_ridge * side * (1.0 - mass)
                      - dust_dense * mass, 0.0, 1.0);
    if (dust_steps > 1.5) {
        // The pack's own formula, over-ranging by design: it brightens the top
        // band, which is what stops three steps reading as three greys.
        float banded = clamp(floor(lit * dust_steps) / (dust_steps - 1.0),
                             0.0, 1.0);
        lit = mix(lit, banded, dust_band);
    }
    return lit;
}
";

    internal static readonly string RingCode =
        RingShader.Replace("FLAME_NOISE", Stage3D.EmberNoiseCode)
                  .Replace("FLAME_INK", Stage3D.FlameInk);

    // <b>Shared with ProcRack rather than compiled twice</b>, which is what
    // sharing the text is for: two Shader objects off one string are two compiled
    // programs saying the same thing, and the uniforms that differ between a
    // crater's rings and a rack's are per-material anyway.
    internal static readonly Shader Ringing = new() { Code = RingCode };
}
