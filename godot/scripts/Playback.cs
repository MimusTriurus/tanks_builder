using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The stand's vocabulary as methods, and a queue that plays them one at a
/// time: what a rules event becomes on the board.
///
/// <b>Why a queue and not a call.</b> An event in the rules is instantaneous -
/// "the round hit, it bounced, it flew on" is one line - and on the board it is
/// a second and a half of shell in the air, a spall, a second shell. Two events
/// played on the same frame overlap; two played with a gap between them read as
/// two things that happened to happen. The queue is what settles <i>when the
/// next one starts</i>, and that answer - "when the previous one has finished
/// doing what it shows" - is the one thing here that the match scene will need
/// exactly as the bench needs it. Each step says for itself when it is done.
///
/// <b>Knows no scene.</b> Handed the tick, the stage, the field and the fire
/// like <see cref="TankTick"/> is handed its world; driven by whoever owns the
/// frame. The event bench presses its buttons; the match scene, later, will
/// translate <c>Rules.Event</c> into the same calls. See
/// docs/effects-benches.md §5.
///
/// <b>Assembles, never rewrites.</b> A shot is <see cref="TankTick.Shoot"/> with
/// its outcome decided, a fire is <see cref="Wildfire.Light"/>, a burst is
/// <see cref="Stage3D.Land"/>. What a method here owns is which of those to
/// call and how to tell it has finished.
/// </summary>
public sealed class Playback
{
    public required TankTick Tick { get; init; }
    public required HexField Field { get; init; }
    public Stage3D? Stage { get; init; }
    public Wildfire? Fire { get; init; }
    public Vector2 Origin { get; init; }
    /// <summary>The masonry on the board, or null on a board with none - see
    /// <see cref="WallField"/>. The list of props is what the blast and the
    /// round walk; the ram intent is what <see cref="WallRam"/> sets, and the
    /// watcher that reads it is the caller's frame.</summary>
    public WallField? Bricks { get; init; }

    /// <summary>The walls on the board, empty when there are none.</summary>
    private IReadOnlyList<WallProp> Walls =>
        Bricks is null ? Array.Empty<WallProp>() : Bricks.Walls;

    /// <summary>Something the board cannot show yet. The bench draws it as a
    /// label over the tank or the cell; the match scene will log it. Present so
    /// a button exists before its picture does - see docs/effects-benches.md,
    /// where the TODO label is the decision.</summary>
    public Action<string, Vehicle?, Vector2I?>? Todo { get; init; }

    private sealed record Step(string Name, Action Start, Func<double, bool> Done);

    /// <summary>A side index folded into 0..5.</summary>
    private static int Side(int side) => ((side % 6) + 6) % 6;

    private readonly Queue<Step> _queue = new();
    private Step? _current;
    private double _elapsed;

    /// <summary>What is playing now, or empty.</summary>
    public string Now => _current?.Name ?? "";

    /// <summary>Whether anything is playing or waiting to.</summary>
    public bool Busy => _current is not null || _queue.Count > 0;

    /// <summary>How many have been played to their end since the last clear.
    /// What the self test counts.</summary>
    public int Played { get; private set; }

    /// <summary>Move the queue on by one frame. The next step starts on the
    /// frame the previous one reports done - not the frame after - so two
    /// events written back to back have no empty frame between them.</summary>
    public void Advance(double delta)
    {
        if (_current is not null)
        {
            _elapsed += delta;
            if (!_current.Done(_elapsed))
                return;
            Finish();
        }
        // Start the next, and the one after it if that one is done at once:
        // a step with nothing to wait for must not cost a frame, or two events
        // written back to back would open a gap the rules never had.
        while (_queue.Count > 0)
        {
            _current = _queue.Dequeue();
            _elapsed = 0.0;
            _current.Start();
            if (!_current.Done(0.0))
                return;
            Finish();
        }
    }

    /// <summary>Said with its length, so a capture frame can be picked off the
    /// log rather than by trial: the picture is where the event ended.</summary>
    private void Finish()
    {
        GD.Print($"playback: done '{_current!.Name}' after {_elapsed:F2}s");
        Played++;
        _current = null;
    }

    /// <summary>Drop everything, playing and waiting. What a reset does first.
    /// </summary>
    public void Clear()
    {
        _queue.Clear();
        _current = null;
        _elapsed = 0.0;
        Played = 0;
    }

    private void Enqueue(string name, Action start, Func<double, bool> done) =>
        _queue.Enqueue(new Step(name, start, done));

    /// <summary>A step that is done as soon as it has started.</summary>
    private void Now_(string name, Action start) => Enqueue(name, start, _ => true);

    /// <summary>A step that holds the queue for a while after starting, so
    /// the picture it raised is looked at before the next one lands on it.
    /// </summary>
    private void Hold(string name, Action start, double seconds) =>
        Enqueue(name, start, t => t >= seconds);

    // --- shots ---------------------------------------------------------------

    /// <summary>
    /// One round from <paramref name="shooter"/> into <paramref name="victim"/>,
    /// arriving through the face at <paramref name="side"/> (an index into
    /// <see cref="HexField.EdgeHeadings"/>), with its outcome decided:
    /// <paramref name="level"/> 0 is a bounce, 1 a graze, 2 a hole.
    ///
    /// Done when nothing the shooter fired is still in the air. The spall or
    /// the flash that the arrival raises has its own clock and is not waited
    /// for - it is a picture on the board, and the next event may land beside
    /// it as it would in a fight.
    ///
    /// Through <see cref="TankTick.Fire"/> and not <see cref="TankTick.Shoot"/>
    /// alone: the trigger is six things - flash, recoil, shake, wood, dust,
    /// report - and the round is the sixth. The answer to "who is it for" goes
    /// in as the call's own, so the root's <c>Launch</c> stays unanswered.
    /// </summary>
    /// <param name="goes">What the round does when it arrives, besides landing
    /// - <see cref="Shell.Onward"/>. The step's own end covers the second leg
    /// without a word of its own: "nothing this gun fired is still in the air"
    /// is already the condition, and a round that flies on is still in the
    /// air.</param>
    public void Shot(Vehicle shooter, Vehicle victim, int side, int level,
                     Shell.Onward goes = Shell.Onward.Stops)
    {
        int from = HexField.EdgeHeadings[Side(side)];
        StandOff(shooter, victim, from);
        Enqueue($"shot {shooter.Tag} -> {victim.Tag} from {from} level {level}"
                + (goes == Shell.Onward.Stops ? "" : $" and {goes} on"),
                () => Tick.Fire(shooter, null,
                    v => Tick.Shoot(v, victim, Bearing(v, victim), level,
                                    // A round that flies on is a solid shot,
                                    // whatever the dial says: HE stops on the
                                    // face and bursts there - Land's own fork,
                                    // asked of the shell before the plate - so
                                    // an HE round that flew on would be two
                                    // pictures of one shell.
                                    goes == Shell.Onward.Stops
                                        ? null : Shell.Kind.Ap,
                                    goes)),
                _ => shooter.Rounds.All(r => r.Arrived));
    }

    /// <summary>The distance a shooter stands off at, in cells: two, so the
    /// tracer has a length to be seen along.</summary>
    public const int StandOffCells = 2;

    /// <summary>
    /// Put the shooter on the side of the victim the shot is to come from,
    /// facing it, gun laid - so the tracer, the plate the atlas picks and the
    /// spall all agree, because all three are read off the same two positions.
    ///
    /// <b>The face is not told to the armour; it is stood on.</b> The harness
    /// hands the atlas the bearing of the solution that fired, and never a
    /// number chosen elsewhere - <c>Main.RoundFor</c>. The first cut of this
    /// bench did the opposite, and the round came in from the east and holed
    /// the west plate. The shooter is put, not driven: a drive is a second
    /// event with its own end, and the shot is the one being looked at.
    ///
    /// Two cells out along the side's heading; one when two is off the board,
    /// under water or taken; left where it is, with a TODO, when neither is
    /// free - the bearing is then read off where it actually stands.
    /// </summary>
    /// <summary>
    /// How far a mortar stands off at, in cells: three, the shortest range its
    /// rules allow - GDD classes.md, "тяжёлый миномёт бьёт по целям на дистанции
    /// 3, 4 или 5 клеток".
    ///
    /// <b>The only place the mortar's range lives on this bench, and it lives
    /// here because a button has to put the shooter somewhere.</b> Minimum and
    /// maximum range are a rule about what a player may order, and there are no
    /// orders here - see docs/lob-plan.md §4. Three rather than five so the arc
    /// and both tanks fit one frame at board zoom.
    /// </summary>
    public const int LobCells = 3;

    /// <param name="cells">How far out to stand, in cells. The flat shot's two
    /// by default; a mortar's own minimum when it is the one shooting - see
    /// <see cref="LobCells"/>.</param>
    public void StandOff(Vehicle shooter, Vehicle victim, int from,
                         int cells = StandOffCells)
    {
        Now_($"{shooter.Tag} stands off at {from}", () =>
        {
            Vector2I? stand = null;
            for (int reach = cells; reach >= 1 && stand is null; reach--)
            {
                Vector2I cell = victim.Cell;
                for (int i = 0; i < reach; i++)
                    cell = HexField.Step(cell, from);
                if (Standable(cell, shooter))
                    stand = cell;
            }
            if (stand is Vector2I at)
            {
                shooter.Cell = at;
                shooter.Sprite.HullFacing = Angles.Mod(from + 180.0, 360.0);
                Tick.Park(shooter);
            }
            else
                Todo?.Invoke($"no cell to shoot from at {from}", shooter, null);
            // Laid on the victim wherever the shooter ended up: the bore is
            // where the round leaves from, and an unlaid gun is a round that
            // starts beside the tank - TankTick.AimAt.
            double lay = Gunnery.HeadingOf(victim.GroundPoint - shooter.GroundPoint);
            if (shooter.Profile.Turreted)
                shooter.Sprite.TurretFacing = lay;
            else
            {
                shooter.Sprite.HullFacing = lay;
                Tick.Park(shooter);
            }
            shooter.Sprite.QueueRedraw();
        });
    }

    /// <summary>A cell a tank can be put on: on the board, dry or a ford,
    /// unwalled, and nobody else's.</summary>
    private bool Standable(Vector2I cell, Vehicle who)
    {
        if (!Field.InBounds(cell) || Field.IsDeep(cell)
            || Field.CoverAt(cell) == Cover.Walls)
            return false;
        Vehicle? other = Vehicle.At(Tick.Vehicles, cell);
        return other is null || other == who;
    }

    /// <summary>The bearing a shot arrives on, read off the two tanks - the
    /// harness's own arithmetic, <c>Solution.Heading + 180</c>.</summary>
    private static double Bearing(Vehicle shooter, Vehicle victim) =>
        Angles.Mod(Gunnery.HeadingOf(victim.GroundPoint - shooter.GroundPoint)
                   + 180.0, 360.0);

    /// <summary>
    /// A round that bounced and went on - GDD units.md "Рикошет", and
    /// docs/effects-plan.md T7.
    ///
    /// Two steps and no third: a tank stood on the axis the round will leave
    /// by, when the axis has nothing on it, and then the shot itself with
    /// <see cref="Shell.Onward.Bounces"/> on the shell. What happens after the
    /// plate turns it is <c>TankTick.Carry</c>'s, because that is where the
    /// round is when it happens.
    /// </summary>
    public void Ricochet(Vehicle shooter, Vehicle victim, int side)
    {
        StandBehind(shooter, victim, HexField.EdgeHeadings[Side(side)],
                    Shell.Onward.Bounces);
        Shot(shooter, victim, side, 0, Shell.Onward.Bounces);
    }

    /// <summary>A round that went through - the destroyer's, classes.md
    /// "TD — Тяжёлый снаряд", and docs/effects-plan.md T8. The axis is the one
    /// it arrived by, so the tank to catch it stands straight on.</summary>
    public void Through(Vehicle shooter, Vehicle victim, int side)
    {
        StandBehind(shooter, victim, HexField.EdgeHeadings[Side(side)],
                    Shell.Onward.Passes);
        Shot(shooter, victim, side, 2, Shell.Onward.Passes);
    }

    /// <summary>
    /// The mortar's bomb on to a tank - GDD classes.md "HM — Навес", and
    /// docs/effects-plan.md T9.
    ///
    /// <b><see cref="Shot"/> with two words changed, and neither of them is
    /// said here.</b> The arc is the shooter's class (<c>MovementProfile.Lobs</c>)
    /// and the roof is the arc (<c>Shell.Lofted</c>), so this event does not
    /// choose either - it stands a mortar off and pulls the trigger, and what
    /// leaves the tube is a bomb because of what is holding it. A button that
    /// asked for an arc would be a second way to be a mortar.
    ///
    /// <b>The outcome is left to the table rather than stated</b>, which is the
    /// one place this differs from every other shot on the bench. Elsewhere the
    /// event knows the answer and hands it in; here the rules' answer is the
    /// table's own and cannot be anything else - "мощь V больше любой брони,
    /// зона не важна" - so <c>level: null</c>, which already means "ask the
    /// classes", is both the honest reading and the one that cannot drift from
    /// <see cref="Gunnery.Penetration"/>.
    ///
    /// Three cells out and not two: a mortar's shortest range is three, and an
    /// arc wants room - see <see cref="LobCells"/>.
    /// </summary>
    public void Lob(Vehicle shooter, Vehicle victim, int side)
    {
        if (!Lobbing(shooter))
            return;
        int from = HexField.EdgeHeadings[Side(side)];
        StandOff(shooter, victim, from, LobCells);
        Enqueue($"lob {shooter.Tag} -> {victim.Tag} from {from}",
                () => Tick.Fire(shooter, null,
                    v => Tick.Shoot(v, victim, Bearing(v, victim))),
                _ => shooter.Rounds.All(r => r.Arrived));
    }

    /// <summary>
    /// The same bomb on to a hex with no tank on it - GDD classes.md, where the
    /// hex and not the tank is what a mortar is aimed at in the first place.
    ///
    /// <b>Fired down the tube at a cell, which is the door a round with nobody
    /// to hit already goes through</b> - <c>TankTick.Loose</c>, an order to shell
    /// a hex. What the arc changes there is one thing and it is written at the
    /// walk: a lob is not shortened by the wall or the hill it goes over, so the
    /// run is the order's own.
    ///
    /// The shooter is stood off from the cell rather than from a tank, and laid
    /// on the cell's own centre: there is nothing standing there to read a
    /// bearing off.
    /// </summary>
    /// <summary>
    /// Whether this tank has a mortar to lob with, said out loud when it has
    /// not - <c>Playback.WallHull</c>'s refusal for the bulldozer, in the same
    /// words and for the same reason. The button cannot lend a class a gun it
    /// does not have, and a button that quietly did nothing would read as a
    /// button that is broken.
    /// </summary>
    private bool Lobbing(Vehicle shooter)
    {
        if (shooter.Profile.Lobs)
            return true;
        Placeholder($"{shooter.Tag} has no mortar - only the HM lobs; ask for "
                    + "--actor 4", shooter, null);
        return false;
    }

    public void LobAt(Vehicle shooter, Vector2I cell, int side)
    {
        if (!Lobbing(shooter))
            return;
        int from = HexField.EdgeHeadings[Side(side)];
        Now_($"{shooter.Tag} stands off {LobCells} from ({cell.X},{cell.Y})", () =>
        {
            Vector2I? stand = null;
            for (int reach = LobCells; reach >= 1 && stand is null; reach--)
            {
                Vector2I at = cell;
                for (int i = 0; i < reach; i++)
                    at = HexField.Step(at, from);
                if (Standable(at, shooter))
                    stand = at;
            }
            if (stand is Vector2I put)
            {
                shooter.Cell = put;
                Tick.Park(shooter);
            }
            else
                Todo?.Invoke($"no cell to lob from at {from}", shooter, null);
            // Laid on the hex wherever it ended up, and with the hull: a mortar
            // has no ring to turn - see MovementProfile.Turreted - so laying it
            // is turning the tank, and Park has to hear about it.
            shooter.Sprite.HullFacing = Gunnery.HeadingOf(
                Origin + Field.CellCentre(cell) - shooter.GroundPoint);
            Tick.Park(shooter);
            shooter.Sprite.QueueRedraw();
        });
        Enqueue($"lob {shooter.Tag} -> ({cell.X},{cell.Y}) from {from}",
                () => Tick.Fire(shooter, cell),
                _ => shooter.Rounds.All(r => r.Arrived));
    }

    /// <summary>
    /// Put a third tank where the round will go after it leaves
    /// <paramref name="victim"/>, so the second leg arrives at armour instead
    /// of at the edge of the board.
    ///
    /// <b>Stood, not driven - <see cref="StandOff"/>'s argument exactly.</b> A
    /// drive is a second event with its own end, and what is being looked at is
    /// the shot. The axis is read at the step's turn rather than now, because
    /// the shooter standing off may have turned nothing but the queue between
    /// the button and this.
    ///
    /// <b>Only when the axis is empty.</b> The walk that the round itself will
    /// take (<see cref="TankTick.Reach"/>) is asked first: if a wall, a hill or
    /// another tank already stops it, the picture is that, and a hull dropped
    /// two cells out would stand in front of it. On the event board the brick
    /// ring is three cells off one of the light tank's sides, so both pictures
    /// are a face away from each other.
    ///
    /// The catcher is the first tank that is neither of the two in the shot -
    /// named by position rather than by class, because which class catches the
    /// round is not what the button is about.
    /// </summary>
    public void StandBehind(Vehicle shooter, Vehicle victim, int from,
                            Shell.Onward goes)
    {
        Now_($"a tank on {victim.Tag}'s far axis", () =>
        {
            if (Field.Atlas is null)
                return;
            // The mirror for a bounce, straight on for a round that passes
            // through - the two rules' only difference, and the same fork
            // TankTick.Carry makes with the round in its hand.
            int axis = goes == Shell.Onward.Bounces
                ? victim.Deflection(from)
                : HexField.EdgeHeadings[Angles.SideFor(from + 180.0)];
            if (axis < 0)
            {
                // The front or the rear: the round is spent on the plate and
                // there is no second leg to catch. Said out loud, because a
                // button that quietly does nothing reads as a broken one.
                Todo?.Invoke("no bounce off the front or the rear", victim, null);
                return;
            }
            Vector2 dir = Field.Atlas.GroundDirection(axis);
            (float _, Vector2I? _, bool blocked) = Tick.Reach(victim, dir);
            if (blocked)
            {
                GD.Print($"events: {axis} off {victim.Tag} is already stopped "
                         + "by something - nobody stood behind");
                return;
            }
            Vehicle? catcher = Tick.Vehicles.FirstOrDefault(
                v => !ReferenceEquals(v, victim) && !ReferenceEquals(v, shooter)
                     && !v.Wreck.Dead);
            if (catcher is null)
                return;
            for (int reach = StandOffCells; reach >= 1; reach--)
            {
                Vector2I cell = victim.Cell;
                for (int i = 0; i < reach; i++)
                    cell = HexField.Step(cell, axis);
                if (!Standable(cell, catcher))
                    continue;
                catcher.Cell = cell;
                Tick.Park(catcher);
                GD.Print($"events: {catcher.Tag} stands {reach} out on {axis} "
                         + $"to catch what leaves {victim.Tag}");
                return;
            }
            Todo?.Invoke("no cell to catch the round on", victim, null);
        });
    }

    // --- states --------------------------------------------------------------

    /// <summary>
    /// Destroyed - GDD states.md: <b>always the explosion, and the explosion
    /// reaches the six neighbours of its own level.</b> The detonation the
    /// stand already has (<see cref="TankTick.Kill"/>, <c>ProcRack</c>), then
    /// the wave that carries it next door, then what it does there when it
    /// arrives:
    ///
    /// <list type="bullet">
    /// <item><b>The blast wave</b> - <see cref="ProcWave"/> through
    /// <see cref="Stage3D.Wave"/>: a shock ring with a skirt of dust and a
    /// front of flame behind it, run out over the six same-level neighbours
    /// and cut at the edge of any cell the rules do not reach. This is the
    /// picture of the rule: a tank next door catches from something that
    /// visibly came over the ground to it, and a neighbour a level up or down
    /// stays dark. Six ground bursts stood in for it in the first cut and read
    /// as six shots.</item>
    /// <item>when the front reaches the neighbours' centres
    /// (<see cref="ProcWave.ArrivesAt"/> of √3 radii) a live tank there
    /// <b>catches fire</b>, armour or not; in water it flashes and goes out at
    /// once (the rule's own sentence), with the steam puff still a TODO;</item>
    /// <item>a wood on such a cell, and the wood under the wreck itself,
    /// <b>ignites</b> - <see cref="Wildfire.Light"/> refuses where there are no
    /// trees or they are burnt, which is the rule about burnt woods;</item>
    /// <item>every wall on the wreck's edges is <b>struck</b> from the wreck's
    /// side - an HE strike into each wall standing on the cell or on a
    /// same-level neighbour, arriving from the wreck.</item>
    /// </list>
    ///
    /// The chain - a knocked-out or sunk neighbour detonating in turn - waits on
    /// those two states existing (docs/effects-plan.md T1, T5); a destroyed
    /// neighbour does not go off again, its hex is no longer a hull. Held a
    /// second at the end so the next event is not lost in the fire.
    /// </summary>
    public void Destroy(Vehicle victim, string? face = null)
    {
        Now_($"destroy {victim.Tag}", () => Tick.Kill(victim, face));
        Spread(victim);
        Hold("the rack burns down", () => { }, 1.0);
    }

    /// <summary>The same-level neighbours of a cell that are on the board -
    /// the cells the rules' explosion reaches.</summary>
    public IEnumerable<Vector2I> Reached(Vector2I cell)
    {
        int level = Field.LevelAt(cell);
        foreach (int heading in HexField.EdgeHeadings)
        {
            Vector2I next = HexField.Step(cell, heading);
            if (Field.InBounds(next) && Field.LevelAt(next) == level)
                yield return next;
        }
    }

    private void Spread(Vehicle wreck)
    {
        // The wave, on the frame after the rack - read at the step's start
        // rather than now, because the wreck may be stood off or driven between
        // the button and its turn in the queue.
        // Hushed to its ground half while the fireball plays the body of the
        // explosion - ProcWave.Hush, and Stage3D.BallDeaths is the switch.
        Now_("the blast wave", () => Stage?.Wave(wreck.Cell, Stage3D.BallDeaths));

        // What the wave did where its front arrived: one step for all six,
        // held until the flame reaches the neighbours' centres, which for six
        // equidistant cells is one moment. The rules' own list of consequences.
        var wet = new List<Vehicle>();
        float arrives = ProcWave.ArrivesAt(ProcWave.NeighbourAt);
        Enqueue("what the wave did next door", () => { }, t =>
        {
            if (t < arrives)
                return false;
            Vector2I at = wreck.Cell;
            wet.Clear();
            foreach (Vector2I cell in Reached(at))
            {
                if (Vehicle.At(Tick.Vehicles, cell) is Vehicle other
                    && other != wreck && !other.Wreck.Dead)
                {
                    // <b>A knocked-out neighbour goes off itself</b> - the
                    // rules' chain detonation, and its own Destroy deals its
                    // own six neighbours in turn. The wreck that set it off is
                    // dead by now and no longer a hull, so the chain does not
                    // come back.
                    if (other.Wreck.Disabled)
                    {
                        Destroy(other);
                        continue;
                    }
                    other.Burning = true;
                    if (Field.IsWater(cell))
                        wet.Add(other);
                }
                if (Fire?.Light(cell) == true)
                    GD.Print($"events: the rack lit the wood on ({cell.X},{cell.Y})");
            }
            if (Fire?.Light(at) == true)
                GD.Print($"events: the rack lit the wood under it ({at.X},{at.Y})");
            foreach (WallProp wall in Walls)
            {
                if (wall.Cell == at)
                {
                    // A ring round the wreck itself: struck from inside, on
                    // every side at once.
                    // No beam: there is no shot to draw the ray of, and with
                    // nowhere to start it from the rig drew it as a vertical
                    // line down the wreck.
                    foreach (int heading in HexField.EdgeHeadings)
                        wall.Fire(WallRig.Strike.He, wall.Arriving(HexField.Reverse(heading)),
                                  1.0f, false);
                    continue;
                }
                int from = HexField.HeadingTo(wall.Cell, at);
                if (from >= 0 && Field.LevelAt(wall.Cell) == Field.LevelAt(at))
                    wall.Fire(WallRig.Strike.He, wall.Arriving(from), 1.0f, false);
            }
            return true;
        });
        // Said in numbers once the bricks have had a moment - WallShot's report,
        // for its reason: a collapse is judged on the rig's figures.
        Enqueue("walls next door report", () => { }, t =>
        {
            if (t < 0.5)
                return false;
            foreach (WallProp wall in Walls)
                if (wall.Cell == wreck.Cell || Reached(wreck.Cell).Contains(wall.Cell))
                    GD.Print($"events: wall ({wall.Cell.X},{wall.Cell.Y}) by the rack: "
                             + $"struck={wall.Rig?.Struck} loose={wall.Rig?.Loose} "
                             + $"broken={wall.Rig?.Broken} awake={wall.Rig?.Awake}");
            return true;
        });
        // In water it flashes and goes out: the rule's sentence, and the
        // extinguishing has no picture of its own yet - docs/effects-plan.md T3.
        Enqueue("in water it goes out at once", () => { },
                t =>
                {
                    if (t < 0.5)
                        return false;
                    // Doused rather than switched off: in water the rules put
                    // the fire out on the frame it caught, and the steam is
                    // what says so - docs/effects-plan.md T3, which this was
                    // waiting on.
                    foreach (Vehicle tank in wet)
                        tank.Douse();
                    wet.Clear();
                    return true;
                });
    }

    /// <summary>
    /// Alight - GDD states.md "Горит". The flame and the column are on the deck
    /// from the first frame, and the tank goes on driving and shooting.
    ///
    /// Done the moment it starts, like every other state change here: a fire is
    /// a state the rules put a tank in, not a length of time. Two buttons
    /// pressed by hand are seconds apart; only a queue written by --play can
    /// light a tank and put it out on one frame, and that is what two runs are
    /// for - see docs/motion.md, "Потушен".
    /// </summary>
    public void Burn(Vehicle victim) =>
        Now_($"burn {victim.Tag}", () => victim.Burning = true);

    /// <summary>
    /// Put out - <see cref="Vehicle.Douse"/>, and held while it goes: the flame
    /// drops in a third of a second and the steam it leaves hangs about a
    /// second, which is the whole picture and the reason the step waits for it.
    ///
    /// <b>Not <c>Burning = false</c>.</b> That is the spelling a reset uses,
    /// and it takes the fire off the screen between two frames; this is an
    /// event somebody is meant to see happen.
    /// </summary>
    public void Extinguish(Vehicle victim) =>
        Hold($"extinguish {victim.Tag}", victim.Douse, Vehicle.DouseSeconds);

    /// <summary>Knocked out - GDD states.md "Подбит", <see cref="TankTick.Disable"/>:
    /// the engine stops, the paint dims, the deck smokes grey. The hull stays a
    /// target and an obstacle; the next hit destroys it.</summary>
    public void KnockOut(Vehicle victim) =>
        Now_($"knock out {victim.Tag}", () => Tick.Disable(victim));

    public void Placeholder(string what, Vehicle? victim, Vector2I? cell) =>
        Now_(what, () => Todo?.Invoke("TODO " + what, victim, cell));

    /// <summary>
    /// Afloat - GDD states.md, a tank with the wading gear standing in deep water.
    ///
    /// <b>Put rather than driven, and refused off deep water.</b> Every state on
    /// this bench is put (see <see cref="StandOff"/>); what makes this one worth a
    /// method is the refusal, because the state is the cell: a tank swimming on a
    /// ford would be the bench saying the two waters are the same water.
    /// </summary>
    public void Swim(Vehicle tank, Vector2I cell) =>
        Now_($"{tank.Tag} swims on ({cell.X},{cell.Y})", () =>
        {
            if (!Field.IsDeep(cell))
            {
                Todo?.Invoke("no deep water on this cell", tank, cell);
                return;
            }
            tank.Cell = cell;
            // The water puts a fire out on its own - TankTick.Park douses a hull
            // put into any water, with the steam an event is meant to show - so
            // nothing about the fire is said here any more.
            Tick.Park(tank);
            tank.Sprite.QueueRedraw();
        });

    /// <summary>
    /// Drowned - GDD states.md: a tank without the wading gear in deep water, and
    /// any tank knocked out on it. Not moving, not shooting, for the rest of the
    /// battle.
    ///
    /// <b>Two calls and no third state.</b> Put it in the water and stop its
    /// engine, and the picture follows on its own: the hull goes under over the
    /// next couple of seconds because it has stopped holding itself up - see
    /// <see cref="TankTick.SunkAt"/>, which is drawn as size and not as
    /// position - and the grey column off the ports does not come, because the
    /// ports are under. That is the whole difference from <see cref="Swim"/>,
    /// and it is the same difference the rules state.
    ///
    /// <b>No flash and the turret stays seated.</b> Knocking a tank out on land is
    /// a round arriving, and both are that round; nothing arrived here.
    /// </summary>
    public void Sink(Vehicle tank, Vector2I cell)
    {
        Swim(tank, cell);
        Now_($"{tank.Tag} founders", () =>
        {
            if (!Field.IsDeep(tank.Cell))
                return;
            // How far under it is drawn reads the engine, and Disable is what
            // stops it - see TankTick.SunkAt. Here it would be the caller doing
            // the class's job, and a death by gunfire on the same cell would not
            // get it.
            Tick.Disable(tank, 0.0f, true);
            tank.Sprite.QueueRedraw();
        });
    }

    // --- the mine ------------------------------------------------------------

    /// <summary>
    /// Drive one tank to a cell and wait until it gets there - the ordinary
    /// order, given as a step.
    ///
    /// <b>The one event here that is a movement</b>, and it exists because the
    /// mine needs it: what a mine does depends on which end of the hull arrives
    /// first, and a tank stood on the charge has no first end. Everywhere else
    /// on this bench a tank is <em>put</em> rather than driven
    /// (<see cref="StandOff"/>), for the reason given there - a drive is a
    /// second event with its own end - and here the drive is the event.
    ///
    /// Done when the tank has stopped, which covers both ways it can: arriving,
    /// and being stopped by whatever it drove on to.
    /// </summary>
    public void Drive(Vehicle tank, Vector2I onto) =>
        Enqueue($"drive {tank.Tag} to ({onto.X},{onto.Y})",
                () =>
                {
                    // Routed here, because the tick does not route: it drives
                    // Path cell by cell, and a far cell handed to it bare is one
                    // leg straight across the board. Routing is also what makes
                    // a refusal a refusal - out of the pond on to the bank there
                    // is no route (HexField.Passable, the way in only), and the
                    // step says so instead of driving the hull up the wall.
                    var taken = new HashSet<Vector2I>();
                    foreach (Vehicle other in Tick.Vehicles)
                        if (other != tank)
                            taken.Add(other.Cell);
                    List<Vector2I> route = Field.FindPath(tank.Cell, onto, taken);
                    if (route.Count == 0)
                    {
                        Todo?.Invoke($"no route to ({onto.X},{onto.Y})", tank, onto);
                        return;
                    }
                    // Said in the log, because the route is the evidence: out of
                    // a pond the short way is refused and the long way round by
                    // the ramp is what comes back, and a picture of the tank
                    // arriving shows the destination and not the road.
                    GD.Print($"playback: {tank.Tag} drives {route.Count} legs: "
                             + string.Join(" ", route.Select(c => $"({c.X},{c.Y})")));
                    tank.Path = route;
                    tank.PathStep = 0;
                },
                _ => !tank.Moving);

    /// <summary>
    /// A tank driving on to a mine - GDD field.md, "мины", and
    /// docs/effects-plan.md T12.
    ///
    /// <b>Two steps and no picture of its own.</b> The tank is lined up on a
    /// neighbour of the mine's cell facing it, and then driven on to it; every
    /// part of what happens next belongs to <c>TankTick.UpdateMines</c>, which
    /// is where the hull and the charge actually meet. That split is the point:
    /// the event says a tank drove on to a mine, and where under the tank it
    /// went off is a fact about the board rather than about the event.
    ///
    /// The first intact minefield on the board, because the boards this plays
    /// on have one; said out loud when there is none left, since a mine is spent
    /// after it goes off and a second press has nothing to find.
    /// </summary>
    public void Mine(Vehicle tank)
    {
        Vector2I? laid = null;
        for (int r = 0; r < Field.Rows && laid is null; r++)
        for (int q = 0; q < Field.Columns && laid is null; q++)
        {
            var cell = new Vector2I(q, r);
            if (Field.CoverAt(cell) == Cover.Minefield
                && Field.CoverStateAt(cell) == CoverState.Intact)
                laid = cell;
        }
        if (laid is not Vector2I mine)
        {
            Placeholder("no mine left on the board", tank, null);
            return;
        }
        Now_($"{tank.Tag} lines up on the mine ({mine.X},{mine.Y})", () =>
        {
            foreach (int heading in HexField.EdgeHeadings)
            {
                Vector2I from = HexField.Step(mine, heading);
                if (!Standable(from, tank))
                    continue;
                tank.Cell = from;
                // Facing the mine, which is the way it will drive: the hull is
                // turned before the leg starts anyway (TankTick.AdvanceOrder
                // pivots first), and turning it here means the drive is a drive
                // rather than a pivot and then a drive.
                tank.Sprite.HullFacing = HexField.Reverse(heading);
                tank.Sprite.TurretFacing = tank.Sprite.HullFacing;
                Tick.Park(tank);
                return;
            }
            Todo?.Invoke("nowhere to line up on the mine", tank, mine);
        });
        Drive(tank, mine);
    }

    // --- the ram -------------------------------------------------------------

    /// <summary>
    /// One tank ramming another - GDD units.md, "Таран", and docs/ram-plan.md.
    ///
    /// <b>Two steps and no picture of its own</b>, which is <see cref="Mine"/>'s
    /// shape and its reason: the pair is lined up across the face at
    /// <paramref name="side"/>, the intent goes on, and the drive is an ordinary
    /// one leg. Everything after that belongs to <c>TankTick.RamContacts</c>,
    /// which is where the hulls actually meet - whether the shove is allowed,
    /// where the victim goes, and what the hex it lands on does to it. The event
    /// says a tank rammed another; the rest is a fact about the board.
    ///
    /// <b>The victim is turned across the ram on purpose.</b> The rules have it
    /// keeping its hull heading while it is thrown, and a hull already facing the
    /// way it is pushed cannot show that it did not turn. Two sides round is the
    /// most legible angle that is still one of the six.
    ///
    /// <paramref name="cell"/> is where the victim is put, so the shove goes on
    /// to the hex beyond it - which is what the bench's cell and face knobs are
    /// choosing between: plain ground, a bank, the pond, the minefield.
    /// </summary>
    public void Ram(Vehicle tank, Vehicle victim, Vector2I cell, int side)
    {
        int face = HexField.EdgeHeadings[Side(side)];
        Now_($"{tank.Tag} lines up to ram ({cell.X},{cell.Y})", () =>
        {
            Vector2I from = HexField.Step(cell, HexField.Reverse(face));
            if (victim == tank || !Standable(cell, victim))
            {
                Todo?.Invoke("nowhere to line up the ram", tank, cell);
                return;
            }
            // The victim first, and the rammer's hex asked afterwards: on this
            // board the hex a tank is rammed from is often the hex the victim was
            // parked on, and asking both before either has moved refuses the
            // commonest line-up there is.
            victim.Cell = cell;
            victim.Sprite.HullFacing =
                HexField.EdgeHeadings[Side(side + 2)];
            victim.Sprite.TurretFacing = victim.Sprite.HullFacing;
            Tick.Park(victim);
            if (!Standable(from, tank))
            {
                Todo?.Invoke("nowhere to ram from", tank, from);
                return;
            }
            tank.Cell = from;
            tank.Sprite.HullFacing = face;
            tank.Sprite.TurretFacing = face;
            Tick.Park(tank);
        });
        Charge(tank, cell);
    }

    /// <summary>
    /// A tank rammed on to a ramp from above, which runs it out down the slope -
    /// GDD field.md, "вытолкнутый тараном на рампу вдоль её оси сверху
    /// скатывается", and docs/effects-plan.md T11.
    ///
    /// <b>The board is asked for the ramp rather than the panel</b>, which is
    /// <see cref="Mine"/>'s line: a slide needs a ramp, a hex above it to stand
    /// the victim on and a hex at its foot to end on, and three cells that have
    /// to agree are not three knobs. The first ramp that has all three is taken,
    /// and it is said out loud when the board has none.
    /// </summary>
    public void RamRamp(Vehicle tank, Vehicle victim)
    {
        for (int r = 0; r < Field.Rows; r++)
        for (int q = 0; q < Field.Columns; q++)
        {
            var ramp = new Vector2I(q, r);
            int climbs = Field.RampHeading(ramp);
            if (climbs < 0)
                continue;
            // Up the ramp is where the victim stands, and one further up is where
            // the rammer comes from; the throw then goes back down the way the
            // ramp climbs.
            Vector2I above = HexField.Step(ramp, climbs);
            Vector2I behind = HexField.Step(above, climbs);
            Vector2I foot = HexField.Step(ramp, HexField.Reverse(climbs));
            if (!Standable(above, victim) || !Standable(behind, tank)
                || !Standable(foot, victim) || Field.RampHeading(above) >= 0
                // And the rammer has to be able to drive at it: a hex behind the
                // victim that is a level down is a cliff, and a ram delivered off
                // one would be this bench driving a hull up a wall.
                || Field.LevelAt(behind) != Field.LevelAt(above))
                continue;
            Ram(tank, victim, above,
                Array.IndexOf(HexField.EdgeHeadings, HexField.Reverse(climbs)));
            return;
        }
        Placeholder("no ramp on this board to push anybody down", victim, null);
    }

    /// <summary>The ram itself: the intent, then one leg into the hex the victim
    /// is standing on. Routed by hand rather than through <see cref="Drive"/>,
    /// because a route refuses an occupied destination and that hex is occupied
    /// by the whole point of the order.</summary>
    private void Charge(Vehicle tank, Vector2I cell) =>
        Enqueue($"{tank.Tag} rams ({cell.X},{cell.Y})",
                () =>
                {
                    if (HexField.HeadingTo(tank.Cell, cell) < 0)
                    {
                        Todo?.Invoke("the ram is not from a neighbouring hex",
                                     tank, cell);
                        return;
                    }
                    tank.Charge = cell;
                    tank.Path = new List<Vector2I> { cell };
                    tank.PathStep = 0;
                },
                // Both of them: the tank that was hit is still being thrown after
                // the one that hit it has parked, and the picture of a ram is not
                // over until it has stopped.
                _ => !tank.Moving && tank.Charge is null);

    // --- the turret ----------------------------------------------------------

    /// <summary>Lay the turret on one of the six axes - what a shot or an
    /// overwatch order does in the rules. A casemate turns nothing: the hull is
    /// its gun, and the hull is movement.</summary>
    public void TurretTo(Vehicle tank, int side) =>
        Enqueue($"turret {tank.Tag} to {HexField.EdgeHeadings[Side(side)]}",
                () =>
                {
                    if (tank.Profile.Turreted)
                        tank.Sprite.TurretFacing =
                            HexField.EdgeHeadings[Side(side)];
                    tank.Sprite.QueueRedraw();
                },
                _ => true);

    /// <summary>Turret forward - what moving does to it in the rules.</summary>
    public void TurretForward(Vehicle tank) =>
        Now_($"turret {tank.Tag} forward", () =>
        {
            tank.Sprite.TurretFacing = tank.Sprite.HullFacing;
            tank.Sprite.QueueRedraw();
        });

    // --- the field -----------------------------------------------------------

    /// <summary>
    /// A round into a cell: the board's own burst - white on water, splinters in
    /// a wood, earth with a crater everywhere else. <paramref name="might"/> is
    /// the size, on <see cref="Ordnance"/>'s scale.
    ///
    /// <b>Which of the three it is belongs to the board</b>, and this method has
    /// not been told: <see cref="Stage3D.Land"/> reads the cell under the point,
    /// so the one place that decides is the one place that knows.
    ///
    /// <b>What is here rather than there is the fire.</b> GDD field.md lists a
    /// burst among the four things that light a wood - on the hex itself, or on a
    /// neighbour of its own level - and burning is a noun the cell carries, which
    /// puts it on this side of the line. The same six <see cref="Destroy"/> walks
    /// and by the same <see cref="Reached"/>, because a round going off and a tank
    /// going up reach a wood the same way.
    /// </summary>
    public void Blast(Vector2I cell, float might = 1.0f) =>
        Hold($"blast ({cell.X},{cell.Y}) x{might:F2}", () =>
        {
            if (Stage is null)
                return;
            Vector2 spot = Origin + Field.CellCentre(cell);
            Stage.Land(spot, Field.LevelAt(cell) * Field.Lift, might);
            int lit = Fire?.Light(cell) == true ? 1 : 0;
            foreach (Vector2I next in Reached(cell))
                if (Fire?.Light(next) == true)
                    lit++;
            if (lit > 0)
                GD.Print($"events: the round lit the wood on {lit} cell(s)");
        }, 1.0);

    /// <summary>
    /// A heavy driving into a wood, which flattens it - GDD classes.md
    /// "Бульдозер HT", docs/fell-plan.md, F3.
    ///
    /// <b>The drive is the event</b>, exactly as it is for the mine
    /// (<see cref="Mine"/>): everything that happens when the hull reaches the
    /// trees belongs to <c>TankTick.UpdateWood</c>, which is where a tank and the
    /// cell it is entering actually meet. Nothing is put anywhere - the actor
    /// drives from wherever it is parked - because a wood felled by a tank that
    /// was teleported on to it would be a picture of the aftermath.
    ///
    /// <b>Refused to the other four in as many words.</b> A medium in the trees
    /// is a medium in the trees; a button that flattened the wood anyway would
    /// be the bench telling the rules what they say - <see cref="WallHull"/>'s
    /// argument, and the same class table behind it.
    /// </summary>
    public void Fell(Vehicle tank, Vector2I cell)
    {
        if (!tank.Profile.Bulldozes)
        {
            Placeholder($"{tank.Tag} is no bulldozer - only the heavy flattens "
                        + "a wood", tank, cell);
            return;
        }
        if (Field.CoverAt(cell) != Cover.Forest)
        {
            Placeholder("no wood on this cell", tank, cell);
            return;
        }
        if (Field.CoverStateAt(cell) == CoverState.Cleared)
        {
            Placeholder("this wood is already down", tank, cell);
            return;
        }
        // Said before the drive rather than after it, because a burning wood is
        // a refusal of the rules and not of the button: the tank drives in all
        // the same, and what does not happen is the felling - GDD field.md,
        // "горящий гекс землёй не становится".
        if (Field.CoverStateAt(cell) == CoverState.Burning)
            Now_($"wood ({cell.X},{cell.Y}) is alight", () =>
                GD.Print($"events: ({cell.X},{cell.Y}) is burning - it stays a "
                         + "wood until the fire is out"));
        Drive(tank, cell);
        Now_($"wood ({cell.X},{cell.Y}) report", () =>
        {
            IReadOnlyList<PropNode> props =
                Tick.Wood?.Standing ?? Array.Empty<PropNode>();
            int standing = props.Count(
                p => p.Cell == cell && p.Tier.Carries && !p.Going);
            int going = props.Count(p => p.Cell == cell && p.Going);
            int rest = props.Count(p => p.Cell == cell && !p.Tier.Carries);
            GD.Print($"events: wood ({cell.X},{cell.Y}) after {tank.Tag}: "
                     + $"{Field.CoverAt(cell)} {Field.CoverStateAt(cell)}, "
                     + $"{standing} trees standing, {going} going down, "
                     + $"{rest} of the scatter left");
        });
    }

    /// <summary>Set a wood alight. Refused - and said so - on a cell with no
    /// trees, which is <see cref="Wildfire.Light"/>'s own refusal.</summary>
    public void Ignite(Vector2I cell) =>
        Now_($"ignite ({cell.X},{cell.Y})", () =>
        {
            if (Fire is null || !Fire.Light(cell))
                Todo?.Invoke("no wood to light", null, cell);
        });

    /// <summary>
    /// The rules are done with this fire: it burns down and leaves burnt wood.
    ///
    /// <b>Held while it does, and this is the longest hold on the bench.</b> A
    /// wood put out does not stop - the flame dies over the rest of its curve,
    /// the trees hand over to charcoal underneath it, and the smoke drifts after
    /// both. The event is not over until the cell is a stand of burnt trunks,
    /// because the next event in a queue is allowed to assume the board says what
    /// the rules say.
    ///
    /// Refused on a cell that is not holding, so a round tick counted twice is
    /// visible rather than quietly agreed with - see <see cref="Wildfire.Out"/>.
    /// </summary>
    public void Quench(Vector2I cell)
    {
        if (Fire is null)
        {
            Placeholder("no fire on this board", null, cell);
            return;
        }
        Hold($"burn out ({cell.X},{cell.Y})", () =>
        {
            if (!Fire.Out(cell))
                Todo?.Invoke("no fire to put out", null, cell);
        }, Fire.BurnFor * (1.0 - Fire.SwapAt) + Fire.CatchWithin);
    }

    /// <summary>
    /// The field tick at the end of a round: the fire walks one hex and whatever
    /// has had its two rounds goes out.
    ///
    /// <b>One button for both, because the rules are one sentence.</b> The GDD
    /// puts the spread and the two-round life at the same moment, and splitting
    /// them into two presses would let the bench show a board no game can reach.
    /// What each of them does, and in which order, is
    /// <see cref="Wildfire.Round"/>'s - this only says when.
    ///
    /// <b>Held for what it started, not for a fixed beat.</b> A tick that lit
    /// nothing and put nothing out is over at once; one that put a wood out waits
    /// for that wood the way <see cref="Quench"/> does, because the burning down
    /// is the picture the tick exists to show.
    /// </summary>
    public void Round()
    {
        if (Fire is null)
        {
            Placeholder("no fire on this board", null, null);
            return;
        }
        int lit = 0, spent = 0;
        // Enqueued rather than Held, because how long it holds is decided by what
        // it turns out to have done - see the remark above.
        Enqueue("field tick", () =>
        {
            var held = new List<Vector2I>();
            for (int q = 0; q < Field.Columns; q++)
            for (int r = 0; r < Field.Rows; r++)
            {
                var cell = new Vector2I(q, r);
                if (Fire.Waiting(cell))
                    held.Add(cell);
            }
            IReadOnlyList<Vector2I> caught = Fire.Round();
            lit = caught.Count;
            // And whoever was parked in the wood the fire reached is alight - GDD
            // field.md, "a unit catches if the fire spread onto the hex it is
            // standing on". Wildfire returns the cells rather than doing this
            // itself, because it does not know what a tank is.
            foreach (Vector2I cell in caught)
                if (Vehicle.At(Tick.Vehicles, cell) is Vehicle parked)
                    parked.Burning = true;
            spent = held.Count(c => !Fire.Waiting(c));
            if (lit == 0 && spent == 0)
                Todo?.Invoke("nothing burning to tick", null, null);
        }, t => t >= (spent > 0
                      ? Fire.BurnFor * (1.0 - Fire.SwapAt) + Fire.CatchWithin
                      : 0.0));
    }

    /// <summary>
    /// A tank ramming the wall on a cell, through the side at
    /// <paramref name="side"/> - GDD field.md, "таран стены доступен любому
    /// юниту и является действием": the unit standing against the wall drives
    /// through that edge on to the next hex, the wall is destroyed, the tank is
    /// not damaged.
    ///
    /// <b>Three steps and no picture of its own</b>, which is
    /// <see cref="Mine"/>'s shape and for its reason: the tank is lined up on
    /// the cell across the edge facing the wall, the ram intent goes on, and the
    /// drive is an ordinary drive. Everything after that belongs to
    /// <see cref="WallField.Watch"/>, which is where the hull and the masonry
    /// actually meet - the crossing, the section, the record and the edge coming
    /// off the board. The event says a tank rammed a wall; which leaf that is
    /// and what the heap looks like are facts about the board.
    ///
    /// <b>The intent is the whole of what this adds to a drive.</b> A wall an
    /// order did not ask to break does not fall to a hull clipping it
    /// (<see cref="WallField.Sweeps"/>), so an event that only drove would drive
    /// through standing brick and leave it standing. It goes off again when the
    /// drive ends, for the same reason it is not simply left on: the next order
    /// is not this one.
    ///
    /// <b>Ramming from outside, never from within the ring.</b> The rules put
    /// the unit "вплотную к стене" and move it one hex through the edge; a tank
    /// already on the wall's own cell would be driving <i>out</i>, which is the
    /// same geometry and a different sentence - and on this board it is also
    /// where the parked tank happens to stand. So the line-up is unconditional:
    /// the tank is put on the cell across the edge first, whatever it was doing.
    /// </summary>
    public void WallRam(Vehicle tank, Vector2I cell, int side)
    {
        WallProp? wall = Walls.FirstOrDefault(w => w.Cell == cell);
        if (wall is null || Bricks is null)
        {
            Placeholder("no wall on this cell", tank, cell);
            return;
        }
        int heading = HexField.EdgeHeadings[Side(side)];
        Vector2I from = HexField.Step(cell, heading);
        Now_($"{tank.Tag} lines up on the wall ({cell.X},{cell.Y}) at {heading}",
             () =>
             {
                 if (!Standable(from, tank))
                 {
                     Todo?.Invoke($"nowhere to ram the wall from at {heading}",
                                  tank, cell);
                     return;
                 }
                 tank.Cell = from;
                 // Facing the wall, which is the way it will drive - Mine's own
                 // line, and for its reason: turned here, the leg is a drive
                 // rather than a pivot and then a drive.
                 tank.Sprite.HullFacing = HexField.Reverse(heading);
                 tank.Sprite.TurretFacing = tank.Sprite.HullFacing;
                 Tick.Park(tank);
                 Bricks.Ramming = true;
             });
        Drive(tank, cell);
        // And the intent goes off with the order, which is the gate saying what
        // it means: the next drive is not this ram.
        Now_($"the ram of ({cell.X},{cell.Y}) is over",
             () => Bricks.Ramming = false);
        Now_($"wall ({cell.X},{cell.Y}) report", () =>
            GD.Print($"events: wall ({cell.X},{cell.Y}) rammed: "
                     + $"struck={wall.Rig?.Struck} loose={wall.Rig?.Loose} "
                     + $"broken={wall.Rig?.Broken} stuck={wall.Rig?.Stuck} "
                     + $"sides={Convert.ToString(Field.SidesAt(cell), 2).PadLeft(6, '0')}"));
    }

    /// <summary>
    /// A heavy driving through the wall on a cell - GDD classes.md, "Бульдозер
    /// HT": the edge with a wall on it is passable for the heavy at the cost of
    /// an ordinary step, the wall is destroyed by the crossing, and the action
    /// is not spent.
    ///
    /// <b>The same drive as <see cref="WallRam"/> with the intent left off</b>,
    /// and that is the whole of the difference: the gate is asked of the hull
    /// (<see cref="WallField.Sweeping"/>, <see cref="MovementProfile.Bulldozes"/>),
    /// so for this one class a crossing breaks masonry without an order saying
    /// so. Written as "set the ram intent when the actor is heavy" it would have
    /// been the same picture and a different claim - a heavy that rams by hand
    /// rather than a heavy for whom a wall is not an obstacle - and the board
    /// would then still refuse it a route through a walled edge.
    ///
    /// <b>Refused to the other four rather than quietly turned into a ram.</b>
    /// A light nosing at the brick is not a bulldozer, and a button that did it
    /// anyway would be the bench telling the rules what they say.
    /// </summary>
    public void WallHull(Vehicle tank, Vector2I cell, int side)
    {
        WallProp? wall = Walls.FirstOrDefault(w => w.Cell == cell);
        if (wall is null || Bricks is null)
        {
            Placeholder("no wall on this cell", tank, cell);
            return;
        }
        if (!tank.Profile.Bulldozes)
        {
            Placeholder($"{tank.Tag} is no bulldozer - only the heavy drives "
                        + "through masonry", tank, cell);
            return;
        }
        int heading = HexField.EdgeHeadings[Side(side)];
        Vector2I from = HexField.Step(cell, heading);
        Now_($"{tank.Tag} lines up on the wall ({cell.X},{cell.Y}) at {heading}",
             () =>
             {
                 if (!Standable(from, tank))
                 {
                     Todo?.Invoke($"nowhere to drive at the wall from {heading}",
                                  tank, cell);
                     return;
                 }
                 tank.Cell = from;
                 tank.Sprite.HullFacing = HexField.Reverse(heading);
                 tank.Sprite.TurretFacing = tank.Sprite.HullFacing;
                 Tick.Park(tank);
             });
        Drive(tank, cell);
        Now_($"wall ({cell.X},{cell.Y}) report", () =>
            GD.Print($"events: wall ({cell.X},{cell.Y}) bulldozed by {tank.Tag}: "
                     + $"struck={wall.Rig?.Struck} loose={wall.Rig?.Loose} "
                     + $"broken={wall.Rig?.Broken} stuck={wall.Rig?.Stuck} "
                     + $"sides={Convert.ToString(Field.SidesAt(cell), 2).PadLeft(6, '0')}"));
    }

    /// <summary>
    /// An HE round into the wall standing on a cell, from the side at
    /// <paramref name="side"/>: the rig's own strike and the masonry burst on
    /// the leaf. Held while the bricks fall.
    ///
    /// What the tank bench does with a landed shell, without the shell: the
    /// direction is the face's own normal, the force the wall bench's unit.
    /// </summary>
    public void WallShot(Vector2I cell, int side)
    {
        WallProp? wall = Walls.FirstOrDefault(w => w.Cell == cell);
        if (wall is null)
        {
            Placeholder("no wall on this cell", null, cell);
            return;
        }
        Hold($"wall shot ({cell.X},{cell.Y}) from {HexField.EdgeHeadings[Side(side)]}",
             () =>
             {
                 if (Field.Atlas is null || Stage is null)
                     return;
                 int heading = HexField.EdgeHeadings[Side(side)];
                 // A round coming FROM that side flies the opposite way -
                 // WallProp.Arriving says exactly that.
                 wall.Fire(WallRig.Strike.He, wall.Arriving(heading),
                           1.0f, true, float.NegativeInfinity);
                 Vector2I over = HexField.Step(cell, heading);
                 Vector2 mid = (Field.FlatAnchor(cell) + Field.FlatAnchor(over)) * 0.5f
                               - new Vector2(0.0f, Field.TopAt(cell))
                               + Field.CentreOffset;
                 float top = wall.Pile().Top * (Field.Atlas.HexRect.Size.X * 0.5f)
                             * Field.RiseFactor;
                 Vector2 normal = Field.Atlas.GroundDirection(heading);
                 Stage.Slam(Origin + mid, Field.LevelAt(cell) * Field.Lift, normal,
                            new Vector2(0.0f, -top * 0.5f), normal.Y < 0.0f,
                            1.0f, ProcSlam.Surface.Masonry);
             }, 1.5);
        // What the strike did, said once when the hold is over: the collapse is
        // judged on numbers - WallBench's rule - and a capture cannot be diffed
        // against a live solver.
        Now_($"wall ({cell.X},{cell.Y}) report", () =>
            GD.Print($"events: wall ({cell.X},{cell.Y}) struck={wall.Rig?.Struck} "
                     + $"loose={wall.Rig?.Loose} broken={wall.Rig?.Broken} "
                     + $"awake={wall.Rig?.Awake}"));
    }
}
