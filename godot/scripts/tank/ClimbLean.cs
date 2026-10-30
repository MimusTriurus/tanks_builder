using System;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The slope under a tank, sprung - and the two quite different things drawn
/// from it: the body's pitch, and the shape of the footprint printed on it.
///
/// <b>What is sprung is the ground, not the drawing.</b> This used to hold the
/// drawn angle and spring that, which was enough while the body was the only
/// thing a slope moved. It is not: the contact shadow lies <i>in</i> the face,
/// so it is sheared by it - see <see cref="Print"/> - and that shear cannot be
/// recovered from the angle, because the angle is zero on two of the six
/// headings and the plane under those headings is not. Springing both would be
/// two states to keep in agreement, the mistake this project has paid for
/// elsewhere; springing the plane and deriving both leaves one.
///
/// It is also the more physical reading. The lag is the suspension settling onto
/// a face, so it belongs to the face arriving; a hull turning on a slope really
/// does change its pitch as fast as it turns, and with the plane sprung it does.
///
/// <b>Why a rotation and not a shear, for the body.</b> The ground direction of
/// a hex heading is <c>(cos h, -sin h)</c>, so the pitch axis - horizontal and
/// across the travel - is <c>(sin h, cos h)</c>, and its component along the view
/// is <c>cos(h)*cos(e)</c>. That single factor decides the whole of it. On four
/// of the six headings it is 0.75 and the pitch is very nearly an image rotation.
/// On the other two - straight up and down the screen - it is zero, and the
/// pitch is pure foreshortening, which <b>no transform of a flat sprite
/// reaches</b>: the hull's ground extent wants sin(e+t)/sin(e) = 1.39 along a
/// screen direction where the turret's height wants cos(e+t)/cos(e) = 0.83.
/// Affine gives one direction one factor; projective adds a keystone an
/// orthographic camera never produces. What is missing is depth per pixel.
///
/// So this draws the part that is a rigid motion and leaves the rest to
/// <see cref="TankSprite.Rise"/> - which is the answer that came out of trying:
/// a shear past a few pixels reads as the sprite deforming, because nothing on a
/// tank shears, and an anisotropic scale reads as jelly for the same reason. A
/// rotation keeps every length the silhouette had, so the only thing limiting it
/// is whether the pitch is real, and fourteen degrees of it is.
///
/// <b>Sprung, and the spring is the whole of why this is a class.</b> Applied
/// straight, the tilt arrives in one frame at the start of a leg and leaves in
/// one frame at the end - a body that snaps to an angle and snaps back has not
/// climbed anything. Same idiom as <see cref="BodyPitch"/>, stiffer and better
/// damped: omega 12, zeta 0.9, so it arrives over the climb and does not ring
/// afterwards.
/// </summary>
public sealed class ClimbLean
{
    /// <summary>omega squared and 2*zeta*omega, for omega 12 and zeta 0.9. Named
    /// as the two coefficients rather than as the two ratios because that is how
    /// they are used, and the ratios are in the remarks where the reasoning
    /// is.</summary>
    public double Stiffness = 144.0;
    public double Damping = 21.6;

    /// <summary>
    /// The plane under the tank, as its gradient in world ground coordinates:
    /// <c>(dz/dx, dz/dy)</c>, so its length is the steepest rise over run and its
    /// direction points uphill. Zero on level ground, which is what makes all of
    /// this cost nothing on a board without relief.
    ///
    /// A vector rather than a grade and a heading, because this is the sprung
    /// state: two scalars spring to a new plane through every intermediate plane,
    /// while a grade and a heading spring through a swing of the whole hillside.
    /// It also stays continuous where the answer is zero, which a heading is not.
    /// </summary>
    public Vector2 Slope { get; private set; }

    private Vector2 _rate;

    /// <summary>The gradient of a face whose uphill edge is at
    /// <paramref name="heading"/>. World ground coordinates, with +y away from
    /// the camera, matching <see cref="AtlasSet.GroundDirection"/>.</summary>
    public static Vector2 Plane(double grade, double heading)
    {
        double rad = Mathf.DegToRad(heading);
        return new Vector2((float)(grade * Math.Cos(rad)),
                           (float)(grade * Math.Sin(rad)));
    }

    /// <summary>Rise over run along a heading - the plane's gradient projected
    /// onto the travel. Full up the axis, negated coming down it, nothing across
    /// it.</summary>
    public static double Along(Vector2 slope, double heading)
    {
        double rad = Mathf.DegToRad(heading);
        return slope.X * Math.Cos(rad) + slope.Y * Math.Sin(rad);
    }

    /// <summary>
    /// What the plane is worth to something <i>printed on</i> it: the two
    /// coefficients of the screen map a flat footprint lying in the face takes.
    ///
    /// <b>Exact, and that word is the whole reason the shadow gets a map at all
    /// where the body only gets an approximation.</b> A shadow is planar and the
    /// camera is orthographic, so plane-to-screen is linear and an affine
    /// transform is not a model of it - it is it. Screen <c>(x, y)</c> off the
    /// contact patch is ground <c>(x, -y/sin e)</c>, that ground point sits
    /// <c>slope . ground</c> high, and height is worth <c>-cos e</c> up the
    /// screen; collect the terms and the map is <c>y' = y - X*x + Y*y</c> with x
    /// untouched. Nothing is rotated: a footprint on a face is foreshortened, and
    /// turning it instead would stand it off the ground it is a shadow of.
    ///
    /// The <c>1/sin e</c> in the second coefficient is why it is the larger:
    /// screen-vertical is the squashed axis, so a pixel of it is two pixels of
    /// ground to be lifted. It is also what makes the map non-trivial on the
    /// headings where <see cref="Target"/> is zero - straight into the screen, the
    /// whole of the pitch is this stretch, and here, unlike on the body, it is
    /// available.
    /// </summary>
    /// <param name="squash">sin(elevation), which is
    /// <see cref="HexField.Squash"/> - read off the rendered tile, so the camera
    /// angle keeps being measured in one place.</param>
    /// <param name="rise">cos(elevation), which is
    /// <see cref="HexField.RiseFactor"/>.</param>
    public static Vector2 Print(Vector2 slope, double squash, double rise) =>
        squash <= 0.0 ? Vector2.Zero
            : new Vector2((float)(slope.X * rise),
                          (float)(slope.Y * rise / squash));

    /// <summary>
    /// What the slope is worth to the <i>body</i>, as a rotation with no spring
    /// in it.
    ///
    /// Negated because a climb is nose <i>up</i>: the travel direction is where
    /// the nose is, and Godot's positive rotation carries +x down the screen, so
    /// the sign that reads as climbing is the one that reads as diving in the
    /// arithmetic.
    /// </summary>
    /// <param name="grade">Rise over run along the travel - positive climbing.
    /// </param>
    /// <param name="heading">Hull heading in degrees.</param>
    /// <param name="rise">cos(elevation), which is
    /// <see cref="HexField.RiseFactor"/>. Passed rather than assumed, so the one
    /// place the camera angle is measured stays the tile.</param>
    public static double Target(double grade, double heading, double rise) =>
        -Math.Atan(grade) * Math.Cos(Mathf.DegToRad(heading)) * rise;

    /// <summary>The body's drawn rotation for a hull on this heading, off the
    /// sprung plane. A pure function of the state, so a hull turning on a face
    /// re-reads it at once - see the remarks on why the plane is what is
    /// sprung.</summary>
    public double Angle(double heading, double rise) =>
        Target(Along(Slope, heading), heading, rise);

    public void Update(Vector2 slope, double delta)
    {
        _rate += (-(float)Stiffness * (Slope - slope)
                  - (float)Damping * _rate) * (float)delta;
        Slope += _rate * (float)delta;
    }

    /// <summary>Put the body on a plane with no motion left in it. Level by
    /// default, which is a board without relief; with a plane for a tank that is
    /// <i>placed</i> on a slope rather than driven onto one - the spring would
    /// otherwise walk it up from level over a third of a second, so a bench that
    /// opens with a tank on a ramp would open with it flat.</summary>
    public void Reset(Vector2 slope = default)
    {
        Slope = slope;
        _rate = Vector2.Zero;
    }
}
