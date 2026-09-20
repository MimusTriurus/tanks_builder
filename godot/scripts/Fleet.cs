using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// One tank stood up from its atlas: the sprite, the vehicle and every clock
/// that has to be told how many phases its layer was rendered with.
///
/// <b>Written once because it was written twice and about to be written a third
/// time.</b> <see cref="Main"/> and <see cref="TankBench"/> each carried the
/// same fourteen lines - build a <see cref="TankSprite"/> off the class, build a
/// <see cref="Vehicle"/> round it, read the track, exhaust, burn and recoil
/// phase counts off the atlas, hand the class's cruise to the exhaust, the
/// tremble and the rumble, seed the turret scan - and the event bench wanted
/// them a third time. Two copies agree until the first edit lands in one of
/// them, and the edit that matters here is exactly the kind that lands in one:
/// a new clock on the vehicle that one root would seed and the other would run
/// on its default.
///
/// <b>What is not here is what the roots disagree about on purpose.</b> The
/// harness gives each tank a <see cref="ReliefCap"/> and a
/// <see cref="VehicleAudio"/>, sets the flash source and the recoil switch off
/// its flags; the bench does none of that. Those stay with their roots, applied
/// to the vehicle this returns - a shared builder that took every root's
/// options would be a shared builder with a switch per root in it.
/// </summary>
public static class Fleet
{
    /// <summary>
    /// Stand a tank up. The sprite is added to <paramref name="deck"/> here, so
    /// the caller gets a vehicle whose sprite is already in the tree - a vehicle
    /// whose sprite is not is a vehicle nothing can park.
    /// </summary>
    /// <param name="deck">Whose child the sprite becomes: the root, always - a
    /// sprite parented to anything else is a sprite whose position means
    /// something else.</param>
    /// <param name="tag">Which set of pixels, and through <see cref="MovementProfile.For"/>
    /// which class - see <see cref="Main.SharedSpriteDir"/> on why the two can
    /// differ.</param>
    /// <param name="home">Where it stands to begin with; <see cref="Vehicle.Cell"/>
    /// starts there too.</param>
    /// <param name="seed">What stops the turrets swaying in step - see
    /// <see cref="TurretScan.Seed"/>. Never zero: a seed left unset should be
    /// distinguishable from the first tank's.</param>
    public static Vehicle Crew(Node deck, string tag, AtlasSet atlas,
                               FlashSheet? flash, Vector2I home, ulong seed)
    {
        MovementProfile profile = MovementProfile.For(tag);
        var sprite = new TankSprite
        {
            Atlas = atlas,
            Flash = flash,
            // Whether the turret layer turns on a ring of its own. Off the class
            // rather than off the atlas, because the atlas cannot say: a casemate
            // is rendered as twenty-four frames of a turret like everything else.
            // See MovementProfile.Turreted.
            Turreted = profile.Turreted,
        };
        deck.AddChild(sprite);
        var vehicle = new Vehicle
        {
            Tag = tag,
            Atlas = atlas,
            Sprite = sprite,
            Profile = profile,
            HomeCell = home,
        };
        vehicle.Cell = home;
        // Where in its swell this hull starts, off the same seed that keeps the
        // turrets out of step - see Buoy.Idle: five hulls put on a pond together
        // must not rise and fall as one.
        vehicle.Bob.Phase = seed % 1000UL / 1000.0 * Mathf.Tau;
        // Phase counts come off each layer and are never assumed: the renderer's
        // count is a config value, and a clock that wraps anywhere but at the
        // seam pops there.
        foreach (TrackLoop belt in new[] { vehicle.TrackLeft, vehicle.TrackRight })
        {
            belt.Phases = atlas.TrackPhases;
            belt.Pitch = atlas.TrackPitch;
        }
        vehicle.Exhaust.Phases = atlas.ExhaustPhases;
        vehicle.Burn.Phases = atlas.BurnPhases;
        // The tube's recoil poses, read off the layer like every other count:
        // the renderer picks it from the travel, so a table here would drop the
        // last pose the day the travel changes. `RecoilPhases` and not the
        // layer's own phase count - the layer also carries the laying angles
        // now, and this clock has no business walking those.
        vehicle.Barrel.Phases = atlas.RecoilPhases;
        // Per class, so a heavy at its own cruise is as worked as a light at its
        // own instead of idling along because it happens to be slower.
        vehicle.Exhaust.TopSpeed = profile.TopSpeed;
        vehicle.Tremble.TopSpeed = profile.TopSpeed;
        // And the rumble reaches full strength at half cruise, whatever cruise is
        // for this class.
        vehicle.Rumble.FullSpeed = profile.TopSpeed * 0.5;
        vehicle.Scan.Seed = seed == 0UL ? 1UL : seed;
        return vehicle;
    }

    /// <summary>
    /// Make a tank good again: every clock reset, every mark repaired, the
    /// wreck stood back up, the fire out, the orders dropped, both facings
    /// back to the rendered default.
    ///
    /// <b>Moved here out of <see cref="TankBench"/>'s reset, where it was
    /// sixty lines that the event bench wanted verbatim.</b> The order inside
    /// is load-bearing in two places and both are commented where they stand:
    /// the wreck before the fire, because a wreck is what keeps relighting it;
    /// the ceasefire beside the repair, because a tank left engaging would put
    /// holes back into armour just fixed. Where it stands afterwards is the
    /// caller's: this does not move it.
    /// </summary>
    /// <summary>
    /// The class's size onto the tank, and onto the one thing outside it that
    /// has to follow.
    ///
    /// <see cref="MovementProfile.Size"/> times <paramref name="level"/> - the
    /// panel's "size level", a multiplier over the authored row and never a size
    /// of its own (docs/ui.md, "Размер класса"). The belt's link is a length of
    /// ground, so a tank drawn at 0.85 has a 0.85 link and must wind that much
    /// sooner or the tread slips by exactly the factor the tank was scaled by -
    /// the one coupling <see cref="TankSprite.BodyScale"/> cannot take care of
    /// by scaling the node. Speeds are not touched: they are screen px/s per
    /// class, and how large a tank is drawn is not how fast it goes.
    ///
    /// The contact patch is held still. The scale pivots on the anchor, which
    /// floats above the ground, so resizing alone would lift or sink the tank -
    /// see <see cref="TankTick.StandOn"/>. Corrected by the change rather than
    /// re-parked, so dragging the dial while a tank is under way does not snap
    /// it back to the cell it last reached.
    ///
    /// Here rather than in each root, because the third root to stand tanks up
    /// forgot it - the event bench opened with five classes drawn at the one
    /// size the generator rendered them, 169 to 189 px of hull, and the light
    /// read as large as the heavy. <see cref="Crew"/> does not call it: a level
    /// is the root's dial, and a root that had none would still have to know
    /// the number. It is a separate call so the root can say the level.
    /// </summary>
    public static void Resize(Vehicle vehicle, double level)
    {
        float was = vehicle.Sprite.BodyScale;
        var now = (float)(vehicle.Profile.Size * level);
        if (now == was)
            return;
        vehicle.Sprite.BodyScale = now;
        vehicle.TrackLeft.Scale = now;
        vehicle.TrackRight.Scale = now;
        vehicle.Sprite.Position -= vehicle.Atlas.GroundOffset * (now - was);
    }

    /// <summary>
    /// The cells the tanks are standing on this frame, for
    /// <see cref="Grove.Reveal"/>: the two cells of the leg, and no others -
    /// where each hull is and where it is going. The second term is what opens
    /// the wood for the crossing rather than after it, and it counts from the
    /// moment the order starts, not from when the hull arrives.
    ///
    /// <b>The leg, and not the contact patch, and the difference is a wood
    /// beside the road that blinks.</b> This stepped <see cref="Footing.Patch"/>
    /// - six probes a keep-out radius round the contact point. Parked, that
    /// circle stays inside its own cell with 19px to spare; mid-leg its centre
    /// sits on the boundary and the rim falls into the cells the tank is merely
    /// passing between. Measured on the harness, the leg (7,0) to (8,0): the
    /// probe at sixty degrees reads (8,1) from frame 212 to 237 and drops it at
    /// 238, so the wood on (8,1) - a cell no tank enters on that drive - ghosts
    /// from 1.00 to 0.68 and comes back, then does it again on the next leg,
    /// claimed from the other side. Two pulses per pass, 14 603 px of it on one
    /// frame, and the dressing band flips with them, instantly and both ways.
    ///
    /// <b><see cref="Razing"/> already had this fix and said why.</b> So does
    /// <c>TankTick.UpdateWood</c>, which keeps the same patch and intersects it
    /// with the same pair: "a cell the patch grazes but the tick never fells
    /// would be a wood that stopped ghosting and then went on standing." The
    /// ghost was the one caller that had not been told.
    ///
    /// <b>And a hull-shaped test cannot replace the cell here - measured.</b>
    /// A box or a band round the contact point was tried, in the ground axes
    /// and then in the screen's. Parked on (10,1) with a wood on it, this
    /// board's own props sit 40 to 96 px from the contact point and the
    /// nearest prop on a neighbouring cell sits at 92: the two populations
    /// overlap, so no distance from the hull separates them. What it cost when
    /// tried was both ends at once - bushes on the tank's own cell drawn over
    /// the hull at full strength, and the whole cell in front of it ghosting.
    /// The cell is the only thing on this board that answers "the ground this
    /// tank is on" exactly, which is why it stays the unit and only the set it
    /// is built from changed.
    ///
    /// Here rather than in each root because the event bench opened without it
    /// and its bushes drew over the hulls parked on them (a prop sorts by its
    /// foot until Reveal bands it under the tank on its cell - see
    /// <see cref="Grove.Reveal"/>).
    /// </summary>
    public static HashSet<Vector2I> Standing(IEnumerable<Vehicle> vehicles)
    {
        var cells = new HashSet<Vector2I>();
        foreach (Vehicle vehicle in vehicles)
        {
            cells.Add(vehicle.Cell);
            cells.Add(vehicle.Onto);
        }
        return cells;
    }

    /// <summary>
    /// The cells whose wood is coming down: a bulldozer is standing on them or
    /// driving on to them.
    ///
    /// <b>Told apart from <see cref="Standing"/> because the reveal answers two
    /// different questions with it.</b> A hull on a cell is a reason to see
    /// through what stands there - and no reason at all when what stands there
    /// is about to be lying down. A wood ghosted on the way in and then handed
    /// back at full strength to fall is a fade that runs backwards, which is
    /// what it looked like; see <see cref="Grove.Reveal"/>.
    ///
    /// <b>The same predicate the tick fells by</b> - mass, not tag, and not a
    /// wreck - so the wood that does not ghost is exactly the wood that goes
    /// over. Asked from the moment of the order, like the cell a tank is driving
    /// on to, because that is when the ghost would otherwise start.
    /// </summary>
    /// <summary>Where the hulls are, as footprints on the flat board: what a
    /// felled trunk asks to know whether a tank is standing on its root. The
    /// box and not the cell, because a hull is a third of a hex and the cell
    /// says nothing about which end of it a root is at - see
    /// <see cref="Grove.Reveal"/>. Every tank, not only the bulldozers: a hull
    /// parked on a log somebody else knocked down is on top of it just the
    /// same.</summary>
    public static List<Footing.Tread> Treading(IEnumerable<Vehicle> vehicles,
                                               Vector2 origin)
    {
        var treads = new List<Footing.Tread>();
        foreach (Vehicle vehicle in vehicles)
            if (Footing.Tread.Of(vehicle, origin) is { } tread)
                treads.Add(tread);
        return treads;
    }

    public static HashSet<Vector2I> Razing(IEnumerable<Vehicle> vehicles)
    {
        var cells = new HashSet<Vector2I>();
        foreach (Vehicle vehicle in vehicles)
        {
            if (!vehicle.Profile.Bulldozes || vehicle.Wreck.Out)
                continue;
            // The two cells the tick fells on and no others - where it is and
            // where it is going (TankTick.UpdateWood keeps the contact patch to
            // the same pair). A cell the patch grazes but the tick never fells
            // would be a wood that stopped ghosting and then went on standing.
            cells.Add(vehicle.Cell);
            cells.Add(vehicle.Onto);
        }
        return cells;
    }

    /// <summary>The hulls shoulder the wood aside - <see cref="Grove.Brush"/>
    /// for every tank, off the ground it covered since last frame. Every tank
    /// rather than the driven one: a tank left under orders goes on driving
    /// after somebody else is selected. Writes <see cref="Vehicle.LastGroundPoint"/>.
    /// </summary>
    public static void Shoulder(IEnumerable<Vehicle> vehicles, Grove grove,
                                Vector2 origin, double delta)
    {
        foreach (Vehicle vehicle in vehicles)
        {
            Vector2 now = vehicle.GroundPoint - origin;
            grove.Brush(now, now - vehicle.LastGroundPoint, delta);
            vehicle.LastGroundPoint = now;
        }
    }

    public static void Restore(Vehicle vehicle, TankTick tick)
    {
        tick.CancelOrder(vehicle);
        TankSprite s = vehicle.Sprite;
        s.HullFacing = 270.0;
        s.TurretFacing = 270.0;
        vehicle.Pitch.Reset();
        s.Pitch = 0.0;
        vehicle.Rumble.Reset();
        s.Shake = 0.0;
        s.Roll = 0.0;
        vehicle.Tremble.Reset();
        s.TremblePitch = 0.0;
        s.TrembleYaw = 0.0;
        vehicle.Exhaust.Reset();
        s.ExhaustPhase = -1;
        vehicle.TrackLeft.Reset();
        vehicle.TrackRight.Reset();
        s.TrackPhaseLeft = -1;
        s.TrackPhaseRight = -1;
        s.TrackBlurLeft = 0.0;
        s.TrackBlurRight = 0.0;
        // Before the fire is put out, because a wreck is what keeps
        // relighting it - see TankTick.UpdateWreck.
        vehicle.Wreck.Reset();
        s.Wrecked = false;
        s.TurretSeated = false;
        s.Char = 0.0;
        s.FireDensity = 1.0f;
        s.SmokeDensity = 1.0f;
        vehicle.Burning = false;
        vehicle.Burn.Reset();
        s.Burning = false;
        s.FirePhase = -1;
        s.BurnPhase = -1;
        // A ceasefire is part of it for the repair's reason: a tank left
        // engaging would start putting holes back into armour just fixed.
        vehicle.Target = null;
        vehicle.Solution = Gunnery.None;
        vehicle.ReloadLeft = 0.0;
        vehicle.Hit.Reset();
        vehicle.HitCount = 0;
        s.HitPhase = -1;
        s.Repair();
        vehicle.Scan.Reset();
        vehicle.Recoil.Reset();
        vehicle.Barrel.Reset();
        s.RecoilPhase = 0;
        vehicle.Tremble.Level = 1.0;
        vehicle.Recoil.Level = 1.0;
        vehicle.ShotFrame = -1;
        s.FlashFrame = -1;
        s.ShotPhase = -1;
        s.RecoilPitch = 0.0;
        s.RecoilRoll = 0.0;
    }
}
