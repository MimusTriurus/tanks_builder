using System;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The turret blown off a destroyed tank: up out of the ring on the frame of
/// the detonation, over and down beside the hull, where it lies, chars with the
/// hull and leaves the board with it.
///
/// <b>Why a flight and not a cut.</b> The destroyed tank's picture is "no
/// turret" (<see cref="TankSprite.Turretless"/>), and a turret that is simply
/// not there on the next frame reads as the atlas losing a layer. A turret seen
/// leaving is the event the missing turret is the aftermath of - the same split
/// the pose and the char stand on either side of in <see cref="Wreck"/>. The
/// ball of the blast (<c>ProcBall</c>) covers the first tenth of a second, so
/// what is judged is the turret coming out of the fire, not the frame it left on.
///
/// <b>No new pixels.</b> The piece in the air is the wreck's own drooped turret
/// layer, gun included - a turret with its gun hanging is what a torn-off one
/// looks like - or the live turret on a set without one. Spun by changing its
/// heading frame, which is the one rotation the atlas has, so it turns about
/// its own axis as it flies; the tumble a real one does about a horizontal axis
/// has no frames to draw it with and is not faked with a squash, which reads as
/// the sprite being scaled rather than the turret turning.
///
/// <b>It lands on the hull, not beside it - on the engine deck, ring down,
/// hanging a little over the stern.</b> Asked for after both alternatives
/// were tried and read wrong. Thrown clear of the hull the turret has to lie
/// on the ground, and the ground here is a hex the picture must not leave:
/// half a hull plus half a turret is about half a hex on these atlases, so a
/// turret on the ground is on the neighbour's cell fore and aft and pressed
/// against the belt abeam, and either way the question it raises is "why is
/// it there". A turret back on its own hull raises none: the deck is where a
/// turret is, and a turret lying on the deck off its ring, turned, with the
/// ring showing empty beside it, is a turret that was lifted and dropped.
/// <b>The ring stays over the hull</b> - the throw is under a third of the
/// hull's length, so the turret's footprint is on the deck with at most a
/// few pixels past the stern; a ring half over the edge would be a turret that
/// should have tipped off and did not. The lift is a hull and a half, no more:
/// high enough to clear the smoke while it is up, low enough to still read as
/// a thing thrown and not a thing fired.
///
/// Drawn with the tank's material, so <see cref="TankSprite.Char"/> blackens
/// it at the hull's rate, and under the tank's modulate, so
/// <see cref="TankSprite.Presence"/> takes it off the board with the hull.
/// </summary>
public sealed partial class TurretToss : Node2D
{
    public TankSprite Tank = null!;

    /// <summary>How long the turret is in the air. It lands and lies: a second
    /// hop of a tenth of the arc was built here and taken out - on the deck it
    /// read as a ball, not as several tons of steel, which stop where they hit.</summary>
    public const double FlySeconds = 1.0;

    /// <summary>
    /// The landing: the turret does not stop where it touches, it slides.
    ///
    /// <b>A thing that stops dead on the frame it lands is glued to the deck</b>
    /// - asked for after the bounce came out: the arc read, the stop did not,
    /// there was no weight in it. Weight is momentum that has to be got rid of,
    /// and on a flat deck it goes into a skid: the last share of the travel and
    /// the last degrees of the spin happen on the deck, decelerating, over the
    /// third of a second after touchdown. The hull answers on the same frame -
    /// <c>TankTick.UpdateWreck</c> jolts its pitch spring, shakes the camera and
    /// rings the armour - which is the other half of an impact: the thing hit
    /// moves too.
    /// </summary>
    public const double SlideSeconds = 0.35;
    public const double SlideShare = 0.18;
    public const double SlideSpin = 70.0;

    /// <summary>How high it goes, in drawn hull heights, and how far back along
    /// the hull it lands, in hull lengths (<see cref="AtlasSet.TrackLength"/>,
    /// the belt laid across the screen) - see the class note for both bounds.
    /// At 0.30 the ring's far edge is on the stern or a few pixels past it.</summary>
    public const double Lift = 1.5;
    public const double Throw = 0.30;

    /// <summary>How far past the stern the ring may hang, in hull lengths, and
    /// still be read as resting on the deck. Asserted, not used: the check holds
    /// the throw and the turret's width against it.</summary>
    public const double Overhang = 0.12;

    /// <summary>How far it turns on the way, degrees: one and two-thirds turns,
    /// so the frame it lands on is not the one it left on.</summary>
    public const double Spin = 600.0;

    /// <summary>Which way it is thrown: astern, along the hull - the deck is
    /// behind the ring on every one of these hulls, and the deck is where it has
    /// to land. The gun's own bearing does not enter: a turret leaves a ring
    /// the way the ring is set in the hull, not the way the gun was laid.</summary>
    public static double ThrowBearing(double hull) => hull + 180.0;

    /// <summary>How far back it lands, in atlas pixels along the ground: a share
    /// of the belt's length, which is the hull's, and a share of the hex when a
    /// set has no belts to measure.</summary>
    public static double ThrowPx(AtlasSet atlas) =>
        Throw * (atlas.TrackLength > 0.0 ? atlas.TrackLength : 0.6 * atlas.HexRect.Size.X);

    /// <summary>How much bigger it draws at the top of the arc - nearer the
    /// camera, a little; a mark of height in a projection that has no other.</summary>
    public const double Swell = 0.15;

    /// <summary>Seconds since launch, or -1 while there is nothing to draw.</summary>
    public double Age { get; private set; } = -1.0;

    /// <summary>The heading the turret left on, degrees, and the hull's under it.</summary>
    public double Heading { get; private set; }
    public double Hull { get; private set; }

    public bool Launched => Age >= 0.0;
    public bool Flying => Launched && Age < FlySeconds;
    public bool Landed => Launched && Age >= FlySeconds;
    public bool Sliding => Landed && Age < FlySeconds + SlideSeconds;

    private bool _landing;

    /// <summary>True once, on the frame the turret touched the deck - what the
    /// tick reads to make the hull answer the impact. Polled rather than raised,
    /// because the tick already visits every wreck every frame and a callback
    /// would be a second path to the same place.</summary>
    public bool TakeLanding()
    {
        bool was = _landing;
        _landing = false;
        return was;
    }

    public override void _Ready()
    {
        TextureFilter = TextureFilterEnum.Linear;
        // The hull's char shader, so the thrown turret blackens with the hull it
        // came off - see TankSprite.Char.
        UseParentMaterial = true;
    }

    public void Launch(double hull, double heading)
    {
        Age = 0.0;
        Hull = hull;
        Heading = heading;
        ZIndex = Aloft;
        QueueRedraw();
    }

    public void Reset()
    {
        Age = -1.0;
        _landing = false;
        QueueRedraw();
    }

    /// <summary>The z a turret in the air draws at: over every layer of its own
    /// tank, fire and smoke included.</summary>
    public static int Aloft => 2 * TankSprite.LayerOrder.Length + 4;

    /// <summary>The z a turret on the deck draws at: under everything that comes
    /// out of the ring - the deck effects that sort behind the live turret on
    /// some headings sit one below its slot, and this is one below them - and
    /// over the hull, the belts and the scars (the scars tie, and the toss is
    /// the later child). The fire is what it is for: it comes out of the empty
    /// ring, and a turret lying astern of the ring is behind that fire on the
    /// headings that face the camera, which are the ones that are looked at.
    /// On the others the flame draws over a turret that is nearer than it; the
    /// flame is additive and the turret is black, and that reads as the turret
    /// in the fire, which is not wrong either.</summary>
    public static int Grounded => TankSprite.ZFor("turret", true) - 2;

    public override void _Process(double delta)
    {
        if (!Launched)
            return;
        bool wasFlying = Flying;
        Age += delta;
        if (wasFlying && !Flying)
        {
            ZIndex = Grounded;
            _landing = true;
        }
        // While moving - in the air and in the skid - every frame; once still,
        // only when something else (char, heave) asks the tank to redraw.
        if (Age < FlySeconds + SlideSeconds)
            QueueRedraw();
    }

    /// <summary>
    /// Where the turret is at <paramref name="age"/> seconds: the ground point
    /// it is over, in the tank's pixels from the ring, and its height above that
    /// point, in pixels up the screen. Pure, so the checks can ask it.
    /// </summary>
    public static (Vector2 ground, double height, double heading, double swell)
        At(AtlasSet atlas, double hull, double heading, double age)
    {
        double t = Math.Clamp(age / FlySeconds, 0.0, 1.0);
        // The skid: nought until touchdown, then out to one with the speed
        // running down to nothing - the derivative of 1-(1-u)^2 is 2(1-u).
        double u = Math.Clamp((age - FlySeconds) / SlideSeconds, 0.0, 1.0);
        double skid = 1.0 - (1.0 - u) * (1.0 - u);
        // Along the ground: linear in the air, the way a thrown thing travels
        // sideways, then the last share of the way on the deck, decelerating.
        Vector2 dir = atlas.GroundDirection(ThrowBearing(hull));
        double along = ThrowPx(atlas) * ((1.0 - SlideShare) * t + SlideShare * skid);
        Vector2 ground = dir * (float)along;
        // Up and down: one parabola, and nought from the frame it lands.
        double lift = Lift * atlas.DrawnHeight;
        double height = 4.0 * t * (1.0 - t) * lift;
        // Round: most of it in the air, the last degrees in the skid.
        double turned = heading + (Spin - SlideSpin) * t + SlideSpin * skid;
        double swell = 1.0 + Swell * Math.Sin(Math.PI * t);
        return (ground, height, turned, swell);
    }

    /// <summary>Which layer flies: the wreck's drooped turret when the set has
    /// one, else the live turret.</summary>
    public static string LayerFor(AtlasSet atlas) =>
        atlas.Has(AtlasSet.WreckTurretName) ? AtlasSet.WreckTurretName : "turret";

    public override void _Draw()
    {
        if (!Launched || Tank.Atlas is not AtlasSet atlas)
            return;
        string layer = LayerFor(atlas);
        if (!atlas.Has(layer))
            return;
        var (ground, height, heading, swell) = At(atlas, Hull, Heading, Age);
        int index = atlas.FrameFor(heading);
        Vector2 size = atlas.SizeOf(layer, index);
        if (size.X <= 0.0f || size.Y <= 0.0f)
            return;
        // The ring is the tank's origin; the piece is drawn about the ring it
        // left, moved by where it has got to, and swollen about that point.
        var place = new Transform2D(0.0f, new Vector2((float)swell, (float)swell), 0.0f,
                                    ground + new Vector2(0.0f, (float)(Tank.Heave - height)));
        DrawSetTransformMatrix(place);
        DrawTextureRectRegion(atlas.Texture(layer),
            new Rect2(-atlas.Anchor + atlas.OffsetOf(layer, index), size),
            atlas.Region(layer, index));
        DrawSetTransformMatrix(Transform2D.Identity);
    }
}
