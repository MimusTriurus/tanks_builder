using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The dust the belts raise while a tank is driving.
///
/// <b>The same cloud a falling crown throws</b> - <see cref="ProcKick"/> wearing
/// <see cref="ProcKick.Cloud.Ground"/>, through <see cref="Stage3D.Drift"/> -
/// because a belt grinding the ground and a trunk landing on it are one event at
/// two sizes: earth in the air, nothing burning in it, and none of the haze that
/// makes a shot read as smoke. What belongs to this class is the cadence, and
/// that is the whole of it: where the puffs go, how often, and how big.
///
/// <b>Off the belts' own travel, not off time and not off the hull's
/// displacement.</b> <see cref="TrackMarks"/>' argument word for word, and the
/// same two failures: by time the trail thickens at a crawl, and by the hull's
/// displacement a tank pivoting on the spot raises nothing at all - though a
/// pivot is the dustiest thing a tank does, because the belts are then churning
/// the ground rather than rolling over it. That churn is
/// <see cref="TrackMarks.Scrub"/>, borrowed rather than restated: one statement
/// of how hard a belt is grinding, read by the rut it widens and by the dust it
/// throws.
///
/// <b>Seated on the belts' contact points, taking it in turns.</b> Under the
/// middle of the hull dust cannot be seen at all and over the hull it is a veil
/// - <c>TankTick.Bumped</c>'s finding, measured on a landing and true here for
/// the same reason. Both belts on one frame would be two clouds one gauge apart,
/// which at this size is one cloud drawn twice; alternating rows cost half as
/// much and read as the same two ribbons.
///
/// <b>A puff belongs to the place it was made.</b> <see cref="Wake"/>'s rule and
/// its reason: anything hung at a fixed offset behind a hull travels with it and
/// reads as a comet. The cloud is born where the belt was and ages from there,
/// which is also what makes the trail even at any speed - nothing about it
/// depends on how fast the thing that made it was going.
///
/// <b>And a trail of events is only continuous if they overlap.</b> The first
/// cadence laid one puff every 52px and it came out in beats - a tank throwing
/// dust in flashes rather than driving in a cloud. Two things were wrong and
/// both are stated where they live: the gap that matters is two steps, not one,
/// because the rows take it in turns (<see cref="Step"/>); and a cloud handed a
/// short life is a cloud cut off at full density rather than a short cloud
/// (<see cref="ProcKick.Hasten"/>). Measured frame by frame against the same
/// frame under <c>--no-dust</c>, the mass used to climb for four frames and
/// fall off a cliff on the fifth, once per puff laid; it now climbs and levels
/// off.
///
/// <b>A tank driving straight away from the camera hides its own dust, and
/// that is geometry rather than a gap.</b> The trail is behind the hull, the
/// hull is a billboard, and dust may not be drawn over armour - see
/// <see cref="Stage3D.Drift"/>. What shows on that heading is the part that has
/// risen past the roofline and the width that reaches past the tracks; measured
/// against the same frame with no dust, 4.4k changed pixels there against 26k
/// for the same tank on a diagonal. Keeping the cloud low was tried, on the
/// reasoning that belt dust hugs the ground and the ground is the one place a
/// hull cannot hide it: it halves the figure instead - 2.0k - because on that
/// heading the ground behind a tank <em>is</em> the hull. So the model's own
/// rise stays, and the answer to "where is the dust" when a tank drives at the
/// horizon is that it is behind the tank.
///
/// <b>What the floor is worth is handed over, not asked for.</b> The multiplier
/// comes in as a number (<see cref="GroundRule.Dust"/>, read by the tick off the
/// cell the tank stands on): water raises none, sand raises more. Same division
/// <see cref="Wake"/> keeps - which camera is looking, and which cell is
/// underneath, is not this class's business - and it is what lets the cadence be
/// asserted without a board.
/// </summary>
public sealed class TrackDust
{
    /// <summary>Whether any dust is raised at all. Off leaves the board as it
    /// was before this - see <c>--no-dust</c>, which is the A/B it is judged
    /// by.</summary>
    public bool Enabled = true;

    /// <summary>
    /// How far the belts travel between puffs, in ground px, taken as the mean
    /// of the two.
    ///
    /// <b>Not the shoe.</b> A rut is stitched once per link
    /// (<see cref="TrackMarks.Stitch"/>, about 7px) because an imprint is what
    /// one shoe leaves; a cloud is not, and a cloud every 7px would be one
    /// continuous fog with the pool recycling under it a dozen times a second.
    ///
    /// <b>And under HALF the width of a puff, because the rows take it in
    /// turns.</b> <see cref="Wake"/> states the contract: a fresh stamp has to
    /// be wider than the gap to the next one or the trail is a dotted line. It
    /// was stated here against the wrong gap twice. 90 was a row of separate
    /// clouds with board between them; 52 looked right on paper - a puff was
    /// some 87px across - and still came out in beats, because
    /// <see cref="Lay"/> alternates the belts, so what one ribbon actually gets
    /// is a puff every <em>two</em> steps. The gap that matters is 2 x this,
    /// and it has to be under a puff, not under two. So this figure is tied to
    /// <see cref="Might"/> and moves with it: the puff was halved and the step
    /// went with it, or the beads would have come back.
    ///
    /// <b>A cloud is an event, so continuity is overlap and nothing else.</b>
    /// <see cref="ProcKick"/> was written for a shot: it is born, it grows, it
    /// settles. Laid end to end those envelopes read as a string of flashes,
    /// however finely they are spaced; laid on top of one another they read as
    /// one mass being fed. At this a medium at speed lays one every fifteenth
    /// of a second and carries nine at once.
    /// </summary>
    public const double Step = 6.0;

    /// <summary>
    /// How long a puff lasts, in seconds - <see cref="ProcKick.Life"/> for the
    /// pool the board keeps for these.
    ///
    /// <b>Shorter than the shot's 1.6s, and it is not a second dress.</b> What
    /// <see cref="ProcKick.Dress"/> writes is what the cloud is made of; how
    /// long one lasts is a field beside it, and the muzzle's figure is set by
    /// the veil a gun leaves hanging - which the ground dress has already turned
    /// down to a quarter.
    ///
    /// <b>What sets this one is the pool against the hull.</b> Every tenth of a
    /// second is another two live clouds per driving tank (<see cref="Step"/>),
    /// out of a ring the whole board shares - so the figure wants to be small.
    /// What pushes it back up is that a good part of a puff's life is spent
    /// under a hull that has not driven off it yet (<see cref="Trail"/>): at
    /// 0.6s the trail behind a tank was the last third of each cloud and read as
    /// a smear, and this is what buys it a life in the open. A tank at speed
    /// holds seventeen and <see cref="Stage3D.Drifts"/> covers two of them
    /// driving at once, which is what this board does; past that the ring wraps
    /// on to a cloud that is still up, and a cloud cut short is the one pop that
    /// cannot be mistaken for dust.
    /// </summary>
    public const float Hang = 0.90f;

    /// <summary>
    /// How far down the throw the mass gets, in tile widths -
    /// <see cref="ProcKick.Reach"/> for the same pool.
    ///
    /// <b>Under half the gun's, and this is the number the first picture was
    /// wrong about.</b> A muzzle blast drives its dust two thirds of a cell
    /// downrange, and on a board where most headings put the throw across the
    /// screen that reads as a hundred-pixel smear lying on the ground behind
    /// the tank - a stain rather than a cloud. Ground coming off a belt is not
    /// driven anywhere: it is lifted and dropped, so it stays over the patch it
    /// came off and what it does with the time is rise.
    /// </summary>
    public const float Carry = 0.55f;

    /// <summary>
    /// How far astern of the contact point a puff is seated, as a share of a
    /// belt's length.
    ///
    /// <b>Not nought, and that was the first picture.</b> Seated on the contact
    /// patch itself the cloud stands under the hull, where - <c>TankTick.Bumped</c>'s
    /// finding - there is nothing to see: the tank is drawn over it, and a tank
    /// driving away from the camera throws its dust toward the camera, which
    /// <see cref="ProcKick.Aim"/> clamps at the horizon rather than translating.
    /// So the seat carries what the throw cannot, and it is honest about it: the
    /// belt <em>was</em> there, and dust is what is left behind.
    ///
    /// <b>And it is the back of the belt, not a length behind the tank.</b>
    /// Half a belt is 72px of ground and this is 51 - inside the contact patch,
    /// at the rear roadwheel, which is where the ground actually leaves a track.
    /// Past the belt the trail starts behind the hull with clear ground between
    /// it and the tracks, and reads as dust that belongs to something else;
    /// nought puts every puff under the middle of the hull, where the billboard
    /// hides it for most of its life.
    ///
    /// <b>What makes it visible is that the tank drives off it.</b> A puff does
    /// not move (<see cref="Wake"/>'s rule); the hull does. Born at the back of
    /// the belt it is hidden for the fifth of a second it takes the tank to
    /// clear it, and it spends the rest of <see cref="Hang"/> in the open behind
    /// it - which is the one reading of "dust from under the tracks" that a
    /// board drawing its tanks as billboards can give.
    /// </summary>
    public const double Trail = 0.35;

    /// <summary>Below this the belts are twitching rather than driving, in px/s.
    /// <see cref="Swell.StirAbove"/>, the same threshold the water uses, because
    /// a tank that stirs the pond it stands in and raises no dust on dry land is
    /// two answers to one question.</summary>
    public const double DriveAbove = Swell.StirAbove;

    /// <summary>
    /// How big a puff is at full pace on ordinary ground, on
    /// <c>TankTick.RamKick</c>'s scale.
    ///
    /// <b>Bigger than a falling crown's, and it is not a bigger cloud.</b> The
    /// obvious figure was a third of <c>TankTick.FellKick</c> - a belt is
    /// friction where a crown is an arrival - and against the board it was
    /// nothing at all: at 0.35 the frame with the dust in it and the frame
    /// without it differ by one level on three thousand pixels, which is a layer
    /// that costs a pool and says nothing. What this scales is a model whose
    /// throw has already been cut to <see cref="Carry"/>, a little over a third
    /// of the gun's, so the mass lands on about the patch of ground a crown's
    /// does and is denser on it - which is the difference between earth dropped
    /// on and earth ground up.
    ///
    /// <b>It went up once and then came back down by half, and both moves are
    /// measurements rather than tastes.</b> Up, because a puff used to be cut
    /// off at full density (<see cref="ProcKick.Hasten"/>) and every one of them
    /// spent its whole short life in the dense young phase; played properly it
    /// spends half of it settling, so the same number drew a third of the ink -
    /// 390k against 300k over the same ten frames - and 1.25 was what put it
    /// back. Down, because at 1.25 the cloud was simply too big for a board
    /// where a cell is 248px: it stood two thirds of a hex tall behind a hull
    /// 110px wide. Half of that is this, and two other numbers had to move with
    /// it - <see cref="Step"/>, which is tied to the width of a puff, and
    /// <see cref="Ink"/>, without which halving the size takes nine tenths of
    /// the mass rather than three quarters of it.
    /// </summary>
    public const float Might = 0.62f;

    /// <summary>
    /// How much denser than the model a puff is drawn, on
    /// <c>ProcBlast.DustInk</c>'s own <c>dust_ink</c>.
    ///
    /// <b>Because scaling a cloud down does not just make it smaller.</b> The
    /// mass in this model is elements overlapping: at half the size each
    /// element is half as wide, they overlap over a quarter of the area, and
    /// the shared light term shades what is left as thin dust rather than as a
    /// cloud. Measured on the same ten frames, halving <see cref="Might"/> took
    /// the ink from 390k to 41k - a tenth, not a quarter - and the first four
    /// frames of the trail came out with under thirty pixels in them. So a
    /// smaller puff has to be a denser one, and this is that and nothing else:
    /// the footprint is the size, the alpha is this.
    ///
    /// <b>It was 3.0, and 3.0 was a stain.</b> Measured on one diagonal frame
    /// against the same frame with no dust in it, that put the mean change at 56
    /// levels a pixel and the densest at 316 of 765 - a mass with no ground
    /// showing through it, which is a mark on the board rather than dust over
    /// it. At this the same frame reads 44 and 245.
    /// </summary>
    public const float Ink = 1.7f;

    /// <summary>
    /// How far in front of its own belt a puff reaches, in tile widths - the
    /// <c>root</c> the shader is given for this ring.
    ///
    /// <b>It exists because the cloud lies on the board now.</b> Standing, there
    /// was nothing to say: below the contact line and behind the ground are the
    /// same test, so a puff began at its rut and everything it had was on the
    /// far side of it. Lying, the near side is ordinary ground the camera can
    /// see, and a cloud that starts at the rut instead of covering it is the
    /// same complaint one surface along.
    ///
    /// <b>Half the sheet, and the sheet is twice this deep.</b> A standing quad
    /// is all on one side of its seat because there is nothing on the other; a
    /// lying one has ordinary ground both ways, and a sheet that kept the
    /// standing shape clips its own near half - which is where the throw goes,
    /// because the mass leaves astern and astern is toward the camera for
    /// anything driving away from it. So the seat sits in the middle:
    /// <c>Stage3D.Raise</c> gives the quad a <c>Tall</c> of twice this.
    /// </summary>
    public const float Bed = 0.85f;

    /// <summary>
    /// How much of the model's climb a lying puff keeps - see
    /// <see cref="ProcKick.Settle"/>.
    ///
    /// <b>A quarter, because lying down the climb is not a climb.</b> The quad's
    /// up is height when it stands and distance away from the camera when it
    /// lies, so all of it is dust crawling backwards over the ground for no
    /// reason anything in the world is giving. None of it reads as a stain, and
    /// a stain is what the whole surface was warned about; a quarter is enough
    /// spread to keep the mass soft at its far edge. Measured on the diagonal,
    /// centre of the trail against centre of the ruts: 13px of offset at nothing
    /// and 16px at this.
    /// </summary>
    public const float Creep = 1.0f;

    /// <summary>
    /// What share of its size a puff is born at - see <see cref="ProcKick.Swell"/>,
    /// which carries why a trail needs this and a shot does not.
    ///
    /// <b>A third, because a trail is a wedge.</b> Tight and low where it leaves
    /// the track, wide and high where it has had a second to spread - that is the
    /// shape, and the model's own growth does not give it: measured along the
    /// diagonal the trail was 123 to 131 px wide from end to end, flat.
    /// </summary>
    public const float Born = 0.35f;

    /// <summary>
    /// How much one puff differs in size from the one before it, as a share
    /// either side of <see cref="Might"/>.
    ///
    /// <b>Because a trail of identical clouds is a stamped pattern, not
    /// dust.</b> Overlap is what makes the trail continuous (see
    /// <see cref="Step"/>), and overlapping copies of one shape keep the beat
    /// they were laid on - the eye finds the period even when no single cloud
    /// stands out. A little size either way breaks it without touching the
    /// cadence.
    ///
    /// <b>Off a hash of the count, not off a die.</b> <see cref="Plumes.Hash01"/>,
    /// for the reason every random-looking number on this board goes through it:
    /// two runs of the same flags have to come out the same picture, or a
    /// capture stops being evidence.
    /// </summary>
    public const float Ripple = 0.18f;

    /// <summary>
    /// How far across its own belt a puff wanders from the belt's middle, as a
    /// share of half the belt's width.
    ///
    /// <b>Not about where the dust comes from - about the trail reading as a
    /// stamp.</b> Two straight lines of identical puffs give the eye a period to
    /// find; spread across the belt they do not. It was written for a sharper
    /// fault than that - a standing puff ends in a flat line at its seat, and
    /// seated on two lines those flat ends stacked into staircases with treads
    /// fifty pixels long - and that fault went out with the standing quad (see
    /// <see cref="ProcKick.Lying"/>). What it does now is the smaller thing, and
    /// the smaller thing is still worth a line of code.
    ///
    /// <b>One, and one is the belt.</b> The patch a belt stands on is
    /// <c>TrackWidth</c> across, so a puff anywhere on it is a puff on the
    /// belt; wider than that and the dust is beside the tank, which is the
    /// fault <see cref="Flare"/> was set back to one over.
    ///
    /// <b>Off the count's hash, like <see cref="Ripple"/></b>, and for the same
    /// reason - see <see cref="Plumes.Hash01"/>. A second seed, because one
    /// hash driving both would tie a puff's size to its place on the belt.
    /// </summary>
    public const double Scatter = 1.0;

    /// <summary>
    /// How far outboard of its own belt a puff is seated, as a multiple of the
    /// gauge.
    ///
    /// <b>One, and it is one because 1.6 was tried and is wrong.</b> The two
    /// rows are a gauge apart - 63px on this board - inside a trail as long as
    /// <see cref="Hang"/> times the speed, nearer 190px, so they merge into one
    /// band; pushing them outboard parts the band and puts dust clear of the
    /// hull on both sides. It also puts it <em>beside</em> the tank rather than
    /// under its tracks, which is what the picture then showed and what this
    /// effect is not: a tank throws ground from under its belts, and a puff that
    /// starts a gauge and a half off the centreline started somewhere no part of
    /// the tank touches. The band merging is the honest answer - a tank driving
    /// diagonally does leave one trail, because its tracks are a gauge apart and
    /// its trail is three times that long.
    /// </summary>
    public const double Flare = 1.0;

    /// <summary>How far out from the hull's centreline the mass is thrown, as a
    /// share of the throw astern. Ground leaving from under a belt goes mostly
    /// backwards and a little sideways; all backwards is a jet, and much more
    /// sideways is a hull ploughing rather than driving.</summary>
    /// <summary>How far out from the hull's centreline the mass is thrown, as a
    /// share of the throw astern.
    ///
    /// <b>Nothing, and it was 0.35.</b> Ground leaving from under a belt does go
    /// a little sideways, and 0.35 is a fair guess at how much - but sideways on
    /// this board is the one direction that takes the dust off the rut the belt
    /// just wrote, and the rut is the only statement on the table about where
    /// the dust belongs. Measured on a tank driving straight away from the
    /// camera, dust masked against ruts: mean distance from a dust pixel to the
    /// nearest rut 20.3px at 0.35 against 15.8px at nothing, and what is left is
    /// the width of a puff rather than an offset. The note under this
    /// one is where the same argument runs out.</summary>
    public const double Spread = 0.0;

    // ---- a finding, kept where the next attempt would start -------------
    // Settling a cloud that STANDS is worth nothing, and it was tried twice
    // before the surface was. At 0.65 of the climb the trail moved two pixels,
    // at 0.40 three, and 0.40 cost the tank driving straight away from the
    // camera three quarters of what it showed. The mass was not up the screen
    // for having climbed: half of every puff was below its own contact line and
    // not drawn at all, so what was left had its centre half a cloud up however
    // little it rose. Lying down there is no such half, and the same dial does
    // what it says - see Creep.

    /// <summary>Where the puffs go: the tank that raised one, the point on the
    /// board it stands on, which way the mass leaves as
    /// <see cref="AtlasSet.GroundDirection"/> gives it (unnormalised, so the
    /// projection is carried), and how big it is. An action rather than a stage
    /// for <see cref="Wake"/>'s reason - this class knows nothing about who
    /// draws what it raises.
    ///
    /// <b>Which belt it came off is not among them, and that was a fifth
    /// argument for one commit.</b> The row facing the camera was drawn over the
    /// hull so that a diagonal would show both rows; what it showed was dust on
    /// the armour. Dust passes in front of a tank in life and reads as a fault
    /// on a board whose tanks are billboards, so the rung is the same for both
    /// rows again - see <see cref="Stage3D.Drift"/>.</summary>
    public Action<Vehicle, Vector2, Vector2, float>? Puff;

    private sealed class Cart
    {
        /// <summary>Belt travel since the last puff, in ground px.</summary>
        public double Spent;

        /// <summary>Which belt the next puff comes off, 0 left.</summary>
        public int Side;

        /// <summary>How many this tank has laid. The key the size is shaken
        /// by - see <see cref="Ripple"/> - and it is the count rather than the
        /// place, so that a tank driving the same leg twice shakes them the
        /// same way.</summary>
        public int Laid;

        /// <summary>Where the contact point was last frame - only a shoved hull
        /// reads it, and only because its belts are locked.</summary>
        public Vector2 LastAt;

        /// <summary>Whether the point above means anything yet: the rut's own
        /// pen and its reason, that a tank which jumps must not measure the
        /// jump.</summary>
        public bool Down;
    }

    private readonly Dictionary<Vehicle, Cart> _carts = new();

    /// <summary>
    /// Advance one tank by the ground its belts covered.
    ///
    /// Takes the travel rather than working it out, <see cref="TrackMarks.Lay"/>'s
    /// reason: the belt phase, the rut and the dust are three readings of one
    /// number, and a fourth measurement of how far a track went could disagree
    /// with the other three.
    /// </summary>
    /// <param name="ground">What the floor under the tank is worth, 1 for
    /// ordinary ground and 0 for a floor that raises nothing.</param>
    public void Lay(Vehicle v, (double Left, double Right) travel, double delta,
                    float ground)
    {
        if (!_carts.TryGetValue(v, out Cart? cart))
        {
            cart = new Cart();
            _carts[v] = cart;
        }
        // The pen goes up rather than the cadence slowing - the rut's gate and
        // its wording.
        //
        // Wading is asked as well as the floor, and it is not the same
        // question twice. The floor handed in is the cell the hull has
        // arrived at, and a tank driving into a ford is up to its belts in it
        // for most of a leg before that cell changes hands; the waterline knows
        // first. What a ford throws is spray, and the wake and the ripples draw
        // that - see TrackMarks.Lay, which lifts its pen on the same pair.
        if (!Enabled || Puff is null || ground <= 0.0f || delta <= 0.0
            || v.Atlas.HasTracks != true || v.Wading || v.Wreck.Out)
        {
            cart.Down = false;
            cart.Spent = 0.0;
            return;
        }
        // A shoved hull measures the ground it covered, not the belt it did not
        // turn - TrackMarks.Lay's finding, and the case matters more here than
        // there: a hull thrown a whole hex sideways is the one that should be
        // raising most.
        double moved = v.Shoved is null
            ? (Math.Abs(travel.Left) + Math.Abs(travel.Right)) * 0.5
            : cart.Down ? v.GroundPoint.DistanceTo(cart.LastAt) : 0.0;
        cart.LastAt = v.GroundPoint;
        cart.Down = true;
        double pace = moved / delta;
        if (pace < DriveAbove)
        {
            // Forgotten rather than carried: a tank that idles for a minute and
            // then moves an inch must not open with a puff it saved up.
            cart.Spent = 0.0;
            return;
        }
        cart.Spent += moved;
        if (cart.Spent < Step)
            return;
        // One puff a frame, and the remainder carried rather than dropped. Two
        // clouds born on one frame stand at one point and are one cloud drawn
        // twice; zeroing the remainder instead quantises the spacing to whatever
        // a frame happened to cover, which is the frame clock getting into a
        // number that belongs to the belt.
        cart.Spent = Math.Min(cart.Spent - Step, Step);
        int side = cart.Side;
        cart.Side ^= 1;
        int laid = cart.Laid++;

        double arm = v.Atlas.TrackArm * v.Sprite.BodyScale;
        // The gauge is the hull's and comes off GroundDirection unnormalised, so
        // it carries the isometric squash: normalised, a tank driving away from
        // the camera would raise its dust outside its own hull. The rut's
        // measurement, and the same one.
        Vector2 across = v.Atlas.GroundDirection(v.Sprite.HullFacing + 90.0);
        // And a wander across the belt's own width, because the contact
        // patch is a patch - see Scatter, which is about the flat end every
        // puff has rather than about where the dust comes from.
        double wander = (2.0 * Plumes.Hash01(laid, ScatterSeed) - 1.0)
                        * v.Atlas.TrackWidth * v.Sprite.BodyScale
                        * 0.5 * Scatter;
        Vector2 gauge = across
                        * (float)((side == 0 ? arm : -arm) * Flare + wander);

        // Astern of the course, and outward. The course is the push for a hull
        // that did not choose it - see Vehicle.Shoved - and it is reversed when
        // the belts are winding backwards, because what leaves from under a belt
        // leaves the way the belt is pushing it.
        double course = v.Shoved ?? v.Sprite.HullFacing;
        double blown = course + (travel.Left + travel.Right >= 0.0 ? 180.0 : 0.0);
        Vector2 astern = v.Atlas.GroundDirection(blown);
        // A ground length times a ground direction, both unnormalised, so the
        // offset foreshortens with the heading exactly as the gauge beside it
        // does - see Trail for why there is one at all.
        Vector2 at = v.GroundPoint + gauge
                     + astern * (float)(v.Atlas.TrackLength * v.Sprite.BodyScale
                                        * Trail);
        Vector2 away = astern + across * (float)(side == 0 ? Spread : -Spread);
        Puff(v, at, away,
             MightAt(v, travel, delta, pace, ground)
             * (1.0f + Ripple * (2.0f * Plumes.Hash01(laid, RippleSeed) - 1.0f)));
    }

    /// <summary>
    /// How big the puff standing on that point is.
    ///
    /// Four multipliers on one figure, and every one of them is something
    /// already measured rather than a taste: how fast the belts are going as a
    /// share of what this class can do, how hard they are grinding
    /// (<see cref="TrackMarks.Scrub"/>, 1 rolling and 1.5 on a pivot), how big
    /// the hull is (<see cref="MovementProfile.Size"/>) and what the floor is
    /// worth. The pace is the belts' and not <see cref="Vehicle.Speed"/>, which
    /// is the hull's: on a pivot the hull's speed is nought while the belts are
    /// at their busiest, and taking it from there would silence the one case
    /// this effect exists to show.
    /// </summary>
    public static float MightAt(Vehicle v, (double Left, double Right) travel,
                                double delta, double pace, float ground)
    {
        double top = Math.Max(v.Profile.TopSpeed, 1.0);
        double share = Math.Clamp(pace / top, 0.0, 1.0);
        return (float)(Might * share * TrackMarks.Scrub(v, travel, delta)
                       * v.Profile.Size * ground);
    }

    /// <summary>Forget where everybody was. The clouds themselves are the
    /// board's and are put out by whoever holds them - see
    /// <see cref="Stage3D.Quench"/>; what this holds is a remainder and a side,
    /// and a reset that left them would start the next drive half a step early
    /// on the wrong belt.</summary>
    public void Clear() => _carts.Clear();

    /// <summary>The second key the shake is drawn with - a number of its own so
    /// that two things keyed on a count do not shake in step.</summary>
    private const int RippleSeed = 60167;
    private const int ScatterSeed = 21391;
}
