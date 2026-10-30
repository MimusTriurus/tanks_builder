using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// Which cells of the wood are alight, how long each has been, and how far the
/// fire has walked.
///
/// <b>Per cell, and the fire is the cell's rather than the tree's.</b> A wood
/// does not burn one trunk at a time - it burns in patches, and the patch is the
/// unit the board already has. It is also the unit the spread is stated in: a
/// hexagon has six neighbours, so "it gets to the next hex" is one rule instead
/// of a radius over four hundred trunks.
///
/// <b>What is per tree is when each one catches, and that is a stagger and not a
/// state.</b> Trees on one cell lighting in the same frame read as one animation
/// played eight times - the rule every clock in this bench is written to, from
/// three tanks trembling to two hundred trees swaying. So the cell carries the
/// clock and each tree reads it a little late, by a hash of where it stands. One
/// number, no per-tree storage, and reproducible: <c>--capture</c> fixes the step
/// precisely so two runs can be compared, and a fire seeded from a random would
/// measure itself.
///
/// <b>It does not know what a tree is.</b> Whether a cell has anything to burn is
/// a predicate handed in, for <see cref="Swell"/>'s reason: what burns is a cell
/// with something standing on it, so taking a <see cref="Grove"/> would be this
/// class learning about props in order to ignore all but one bit of them - and
/// the whole of the interesting part, when the front moves, can then be asserted
/// with no art on disk and no board under it.
///
/// <b>Three clocks and not one, because the three things a fire does end at
/// different times.</b> The flame goes out first, the smoke drifts on after it,
/// and the ash never leaves. Folded into one they would have to end together,
/// which is a fire that is deleted rather than one that burns out.
/// </summary>
public sealed class Wildfire
{
    public required HexField Field;

    /// <summary>Whether the wood can catch at all. Off is the board as it was
    /// before this - see <c>--no-tree-fire</c>.</summary>
    public bool Enabled = true;

    /// <summary>Whether a burning cell hands the fire to its neighbours on
    /// its own clock. Off for a board whose fire is counted in rounds by the
    /// rules - the event bench - where a cell is lit by <see cref="Light"/> and
    /// the spread is somebody else's decision. <see cref="Enabled"/> stays on
    /// there, because <see cref="Light"/> refuses on a disabled fire.</summary>
    public bool Spreads = true;

    /// <summary>
    /// Whether a lit cell waits to be put out instead of burning down on its own
    /// clock.
    ///
    /// <b>The same split the smoke screen is built on, and for the same reason:
    /// the rules own how long, the picture owns how it looks.</b> By the GDD a
    /// wood burns two rounds - not nine seconds - and a round is however long the
    /// players take over it. A fire that went out on <see cref="BurnFor"/> would
    /// be a board that decided the rule, and a fire whose flame was pinned to a
    /// round counter would be a picture that jumps.
    ///
    /// <b>So the clock is not stopped, it is held.</b> Everything up to
    /// <see cref="Ripe"/> plays exactly as it does off this switch - the cell
    /// catches, the trees stagger in, the flame comes up, the crowns burn away to
    /// charcoal and the scrub goes with them - and then the age waits there until
    /// <see cref="Out"/>, after which the rest of the same curve runs: the flame
    /// falling off a tree the fire has already been through. Nothing is frozen
    /// while it waits: <c>Coat.Flame</c> is an intensity and the flicker is
    /// <c>Stage3D</c>'s own clock, so a held fire burns.
    ///
    /// Off is the wood bench and the harness, where a fire lit by hand has
    /// nobody to put it out.
    /// </summary>
    public bool Ruled = false;

    /// <summary>How many field ticks a wood stays alight before <see cref="Round"/>
    /// puts it out. Two, from the GDD; here rather than at the caller because the
    /// cell already carries Burning and Burnt, and a second owner of when the fire
    /// ends is two answers to one question.</summary>
    public int Lasts = 2;

    /// <summary>Whether a wall or anything else stands between two cells, so the
    /// fire does not cross. Handed in for <see cref="Wooded"/>'s reason - this
    /// class does not know what a wall is - and asked only by <see cref="Round"/>,
    /// because the second-by-second spread is the harness's own board where
    /// nothing bars anything.</summary>
    public Func<Vector2I, Vector2I, bool>? Barred;

    /// <summary>Whether this cell has anything to burn. Handed in rather than
    /// worked out - see the class remarks. Nothing burns without it, which is the
    /// right default: a board whose wood has not been sown yet cannot be lit.
    /// </summary>
    public Func<Vector2I, bool>? Wooded;

    /// <summary>
    /// How long a tree stands in flame before it is a burnt trunk, in seconds.
    ///
    /// The one number here that is a judgement rather than a consequence, and it
    /// is judged against the spread: a cell that burns out before the fire has
    /// reached its neighbour shows a wood that is never alight in two places at
    /// once, which is a wood that never looks like it is on fire. At 9s against a
    /// 3.2s step, a front is burning across three cells' worth of depth.
    /// </summary>
    public float BurnFor = 9.0f;

    /// <summary>How long after that the smoke keeps drifting. A fire that stops
    /// smoking the instant its flame dies reads as a switch, and the smoke is what
    /// says where the fire has been while the ash is still being looked for.
    /// </summary>
    public float SmokeFor = 7.0f;

    /// <summary>
    /// How long the living picture takes to hand over to the burnt one, in
    /// seconds.
    ///
    /// <b>The swap is a window, and this is the window.</b> A tree whose
    /// silhouette changes between two frames reads as a tree being replaced
    /// rather than one burning down - the tank's wreck answers the same
    /// complaint the same way, charring over a second and a half rather than at
    /// the hit.
    ///
    /// <b>Judged against the flame, and that is why it can be short.</b> The
    /// handover sits inside the flame's own plateau - see <see cref="SwapAt"/> -
    /// so it happens under the brightest thing on the tree. Same argument as the
    /// muzzle flash covering the recoil: the frames that would show the change are
    /// the frames something else is standing in front of.
    /// </summary>
    public float SwapFor = 1.2f;

    /// <summary>
    /// How far into the burn the handover starts, as a share of it.
    ///
    /// <b>The tree turns to charcoal while it is still burning, not when it has
    /// finished.</b> Handing over at the end made the burnt picture the thing that
    /// arrived after the fire went out, so the fire was only ever on the green
    /// tree - a tree that burns without changing and then changes without burning.
    /// The state is what happens at the end; the picture is what happens during.
    ///
    /// <b>The whole window sits inside the flame's plateau, and it is the
    /// shutting of it that picks the number.</b> The flame is at full strength
    /// from <see cref="FlareIn"/> of the burn to <see cref="FadeFrom"/> of it, so
    /// a window opening at 0.40 shuts at 0.53 - just inside - and the charcoal
    /// then stands in its own full fire for the rest of the burn.
    ///
    /// <b>Judged on where it shuts rather than where it opens</b>, because that
    /// frame is the one a ruled fire waits on - see <see cref="Hold"/>. Anything
    /// past <c>FadeFrom - SwapFor/BurnFor</c>, which is 0.417 here, leaves the
    /// rules holding a wood whose fire has already begun to go out, and a round
    /// can be as long as the players take over it.
    ///
    /// <b>The char has to reach one here and not at the end</b>, because it
    /// belongs to the outgoing picture: a living tree still half green, cross-faded
    /// into charcoal, is two different trees on screen at once rather than one
    /// turning.
    /// </summary>
    public float SwapAt = 0.40f;

    /// <summary>
    /// How much of the burn the flame takes to come up, and how far into it it
    /// stands at full before it starts to go, as shares of it.
    ///
    /// <b>Named rather than written into the curve, because they are what every
    /// other window here is judged against:</b> the handover shuts inside the
    /// plateau (<see cref="SwapAt"/>), the fuel that is eaten is eaten inside it
    /// (<see cref="Coat.Spent"/>), and a ruled fire waits on it
    /// (<see cref="Hold"/>). Three readers taking the same edge off three
    /// literals is how three numbers drift apart.
    /// </summary>
    public const float FlareIn = 0.18f;

    /// <summary>The far end of that plateau - see <see cref="FlareIn"/>.</summary>
    public const float FadeFrom = 0.55f;

    /// <summary>How much later than its cell a tree may catch, in seconds. Read
    /// through a hash of where the tree stands, so it is the same tree every run
    /// and a different one from its neighbour. A third of the burn: long enough
    /// that a cell is visibly catching rather than lit, short enough that the
    /// last trunk on a cell is not still green when the first is charcoal.
    /// </summary>
    public float CatchWithin = 3.0f;

    /// <summary>
    /// How long the fire takes to reach the next hex, in seconds, before jitter.
    ///
    /// <b>Jittered per pair and not per cell</b>, because a cell that hands the
    /// fire on to all six neighbours at once puts a hexagon on the board - the
    /// same failure the pond's foam had when its mask came off the cell, and the
    /// same fix: the shape is broken up where it is drawn from, not tidied
    /// afterwards.
    /// </summary>
    public float StepFor = 3.2f;

    /// <summary>How wide the jitter on that is, as a share of it either way. Half
    /// means the slowest edge takes three times the fastest, which is enough for
    /// the front to arrive as a line rather than a ring.</summary>
    public float StepJitter = 0.5f;

    /// <summary>How long the ground takes to blacken, in seconds. The ash lands
    /// while the tree is still burning - it is what is falling off it - so this
    /// is under <see cref="BurnFor"/> and not after it.</summary>
    public float AshIn = 6.0f;

    /// <summary>Salts, so no two decisions about one cell share a hash. Same
    /// discipline as <see cref="PropTier.Salt"/> one level up: the stagger and
    /// the step must be independent, or the tree nearest the next hex is always
    /// the last to catch.</summary>
    private const int CatchSalt = 511_003;
    private const int StepSalt = 522_007;

    /// <summary>Seconds since each cell was lit, or -1 for a cell that never
    /// was. One entry per cell of the board, because a fire is not confined to
    /// the cells that happen to be wooded now - the wood can be re-sown.
    /// </summary>
    private float[] _age = Array.Empty<float>();

    /// <summary>Whether the rules have let each cell finish. Meaningless off
    /// <see cref="Ruled"/>, and allocated anyway: one bool a cell against a branch
    /// in the frame loop.</summary>
    private bool[] _freed = Array.Empty<bool>();

    /// <summary>How many field ticks each cell has been alight for, or -1. Counted
    /// here rather than by whoever calls <see cref="Round"/> so that the fire has
    /// one owner - see <see cref="Lasts"/>.</summary>
    private int[] _since = Array.Empty<int>();

    private int _wide, _tall;

    /// <summary>How many cells have ever been lit, and how many are still in
    /// flame. Reported rather than set - a front that has stopped moving and a
    /// fire that was never lit are the same still picture.</summary>
    public int Scorched { get; private set; }

    public int Alight { get; private set; }

    /// <summary>What one tree looks like: how far its paint has charred, how much
    /// flame is on it, how much smoke is over it, and whether it has finished.
    ///
    /// <b>Char and Burnt are two answers and not one.</b> The paint blackens over
    /// the whole burn and the silhouette changes at the end of it - the tank's
    /// <c>Wrecked</c> and <c>Char</c> split exactly, and for the tank's reason: a
    /// state derived from the other has geometry lagging colour, which reads as
    /// the two belonging to different objects.
    ///
    /// <b>Char is for the living art only</b>, which is the picture that fades
    /// out - see <see cref="PropNode.Scorch"/>. It reaches one at the swap and
    /// stays there, because the outgoing picture is fully charred for the whole
    /// of the handover.
    ///
    /// <b>Burnt and Swap are two answers as well, and the second is the one that
    /// takes time.</b> Burnt is the state the tree has entered, and it still
    /// arrives on one frame; Swap says how far the picture has got there. Split
    /// so that nothing measured off the state has to wait for the picture: the
    /// ash, the counts and the stiffened sway all read Burnt, and only whoever
    /// draws reads Swap. Defaulted to one, so a Coat written out by hand means
    /// the state it names and means it fully.
    /// </summary>
    /// <param name="Spent">How far through being used up the fuel is - the same
    /// kind of number as <paramref name="Swap"/> and defaulted the same way for
    /// the same reason, since <paramref name="Burnt"/> means the handover is
    /// over whichever of the two a tier does.
    ///
    /// <b>Computed for every cell whether anything on it is consumed or
    /// not</b>, because this class does not know what a tree is - see the class
    /// remarks. Which tiers use it is <see cref="PropTier.Consumed"/>'s
    /// business, and a tier that leaves a husk reads
    /// <paramref name="Swap"/> instead.
    ///
    /// <b>It is eaten between the flame coming up and the handover shutting</b>,
    /// so it belongs to the catching and not to the dying - the same move as the
    /// handover itself, and the same reason. Scrub has no crown to hand over to
    /// charcoal; being eaten is the whole of what the fire does to it, and a cell
    /// the rules are holding has to be a cell the fire has already got through.
    /// Roughly three seconds of the nine at the settings above, which is what
    /// makes it a thing being consumed rather than an object deleted.
    ///
    /// <b>So the flame outlives its fuel, and that is the picture rather than an
    /// oversight.</b> The quad hangs on the prop's transform and is not faded by
    /// what is left of it (<c>Stage3D.Kindle</c> against <c>PropNode.Shown</c>),
    /// so the fire goes on standing where the scrub was, over the ash it made -
    /// which is what a burning cell with nothing left on it looks like. It was
    /// the other way round while the fuel ran out with the flame: one event, and
    /// no frame of fire over bare ground.</param>
    public readonly record struct Coat(float Char, float Flame, float Smoke,
                                      bool Burnt, float Swap = 1.0f,
                                      float Spent = 1.0f)
    {
        public bool Untouched => Char <= 0.0f && Flame <= 0.0f && Smoke <= 0.0f
                                && !Burnt;

        /// <summary>
        /// Only the mark the fire leaves - what something that is not fuel gets.
        ///
        /// <b>Here rather than at the caller, so there is one copy of the
        /// curve.</b> A stone soots on the same schedule the paint beside it
        /// chars on, so the alternative was a second entry point computing the
        /// same <see cref="Char"/> again - which is the shape of every number in
        /// this project that has drifted. The caller says which coat it wants
        /// and this says what "only the soot of it" means.
        ///
        /// <b>The smoke goes with the flame and not with the soot.</b> The dark
        /// column is what the fire pours out, and a boulder emitting one is a
        /// boulder on fire - the flag exists to rule out exactly that reading.
        /// So: the mark, and nothing else on it.
        /// </summary>
        public Coat Sooted() => new(Char, 0.0f, 0.0f, false, 0.0f, 0.0f);
    }

    public static readonly Coat Green =
        new(0.0f, 0.0f, 0.0f, false, 0.0f, 0.0f);

    /// <summary>Light one cell, if there is anything on it to burn. Refuses
    /// rather than lighting the ground: a cell with no trees has nothing to show
    /// for a fire, and an empty hex quietly blackening is a board that says the
    /// fire spread where it did not.</summary>
    public bool Light(Vector2I cell)
    {
        Size();
        if (!Enabled || !Field.InBounds(cell))
            return false;
        if (Wooded is not null && !Wooded(cell))
            return false;
        int i = At(cell);
        if (i < 0 || _age[i] >= 0.0f)
            return false;
        _age[i] = 0.0f;
        _freed[i] = false;
        _since[i] = 0;
        return true;
    }

    /// <summary>Where the picture starts handing over, in seconds into one
    /// tree's own burn - <see cref="SwapAt"/> as a time. A property because the
    /// curve, the hold and the length of the burning down all measure from it.
    /// </summary>
    public float Handing =>
        Mathf.Max(BurnFor, 1e-4f) * Mathf.Clamp(SwapAt, 0.02f, 1.0f);

    /// <summary>
    /// Where one tree is done being changed by its fire, in seconds into its own
    /// burn: the handover has shut, whatever the fire eats is eaten, and the
    /// flame is still at full.
    ///
    /// <b>This is what a ruled fire holds, and it is the far side of the handover
    /// rather than its doorstep.</b> A wood the rules have not finished with
    /// shows charcoal standing in full flame - the fire has got through the crown
    /// and is still burning - so everything the fire does to the picture belongs
    /// to the catching, and the only thing left for the rules to spend is the
    /// flame itself. Held one frame earlier, as it was, the crown never burns
    /// while the wood is alight: every visible change is packed into the frames
    /// after the rules let go, so a wood catching fire shows a green tree with
    /// flames on it and putting it out is what burns it down.
    ///
    /// <b>No dial of its own</b>, for the reason there was never one: a number
    /// here would drift off the picture it exists to hold. Clamped into the burn,
    /// so a window set longer than the fire cannot put the hold past the end of
    /// it.
    /// </summary>
    public float Hold => Mathf.Min(Handing + Mathf.Max(SwapFor, 0.0f),
                                   Mathf.Max(BurnFor, 1e-4f));

    /// <summary>
    /// The age a ruled fire waits at: <see cref="Hold"/> for the tree that reads
    /// the clock latest.
    ///
    /// <b>The stagger is in it, and that is why this is a property and not the
    /// product.</b> Trees read the cell's age late, by up to
    /// <see cref="CatchWithin"/>; the cell is not ripe until the last of them has
    /// caught and come through.
    /// </summary>
    public float Ripe => CatchWithin + Hold;

    /// <summary>
    /// How much faster the fire runs once the rules have let it go.
    ///
    /// <b>The dying is the only part of the burn the rules pay for, so it is the
    /// only part with a pace of its own.</b> Everything up to <see cref="Hold"/>
    /// is a cell catching fire, and that plays at the picture's own speed whoever
    /// is counting rounds. What is left after it is a flame going out over a tree
    /// nothing further will happen to - the handover is done, the fuel is gone,
    /// the ash is down - and at the burn's own rate that is seven seconds of a
    /// board whose event is already decided, all of which the queue waits through
    /// (<see cref="Dying"/>).
    ///
    /// <b>The stagger is inside it and is halved along with the rest</b>, which
    /// is the point rather than a side effect: trees going out one after another
    /// are still eight animations instead of one, and how far apart they are
    /// belongs to the event they are spread across, not to a constant. The same
    /// hash still decides the order, so the first to catch is still the first to
    /// go.
    ///
    /// One is a released fire that dies exactly as an unruled one does, which is
    /// the A/B this number is judged by.
    /// </summary>
    public float OutPace = 2.0f;

    /// <summary>How long a released fire takes to burn down, stagger and all -
    /// the flame falling to nothing from a tree already handed over, at
    /// <see cref="OutPace"/>. Here rather than at the caller for
    /// <see cref="Lasts"/>'s reason: the event bench holds its queue open for
    /// exactly this long, and a second copy of the sum would go stale the first
    /// time the curve moved.</summary>
    public float Dying => (Mathf.Max(BurnFor, 1e-4f) - Hold + CatchWithin)
                          / Mathf.Max(OutPace, 0.01f);

    /// <summary>Whether this cell is a ruled fire sitting at <see cref="Ripe"/>,
    /// waiting to be told. Reported for the panel and the self-test - a fire that
    /// is holding and a fire that is dying look the same to everything else,
    /// because that is the point of holding at the plateau.</summary>
    public bool Waiting(Vector2I cell)
    {
        int i = At(cell);
        return Ruled && i >= 0 && i < _age.Length && !_freed[i]
               && _age[i] >= Ripe - 1e-4f;
    }

    /// <summary>How many field ticks this cell has been alight for, or -1.</summary>
    public int RoundsAt(Vector2I cell)
    {
        int i = At(cell);
        return i < 0 || i >= _since.Length ? -1 : _since[i];
    }

    /// <summary>
    /// The rules are finished with this cell: let its clock run on and burn down.
    ///
    /// <b>Refuses on a cell that is not held</b>, and says so, rather than
    /// resetting anything: told twice, the second telling would be a fire being
    /// put out that has been out for a round, and quietly agreeing is how a
    /// double-counted round tick stays invisible.
    /// </summary>
    public bool Out(Vector2I cell)
    {
        int i = At(cell);
        if (i < 0 || i >= _age.Length || _age[i] < 0.0f || _freed[i])
            return false;
        _freed[i] = true;
        return true;
    }

    /// <summary>
    /// One tick of the field: what has had its rounds goes out, what is left hands
    /// the fire on.
    ///
    /// <b>Spread first and out after, because a wood that burns through a round
    /// spreads for that round.</b> Both happen on this one tick, and the GDD says
    /// nothing about their order; done the other way, a fire lit two rounds ago
    /// would spend its last round doing nothing, and the front would advance on
    /// every round but the ones where it mattered.
    ///
    /// <b>And a cell lit by this tick does neither in it</b> - the walked-set
    /// discipline <see cref="Tick"/> already keeps, for the same reason: otherwise
    /// one tick crosses the whole wood, and a cell would burn out having never
    /// been alight for a round.
    ///
    /// Returns what caught, so the caller can say who is standing in it - by the
    /// GDD a unit on a cell the fire reached is alight, and that is a decision
    /// about tanks, which this class does not have.
    /// </summary>
    public IReadOnlyList<Vector2I> Round()
    {
        Size();
        var caught = new List<Vector2I>();
        if (_age.Length == 0 || !Enabled)
            return caught;
        // What is alight at the start of the tick, taken before anything is lit or
        // put out by it.
        var burning = new List<Vector2I>();
        for (int q = 0; q < _wide; q++)
        for (int r = 0; r < _tall; r++)
        {
            int i = r * _wide + q;
            if (_age[i] >= 0.0f && !_freed[i])
                burning.Add(new Vector2I(q, r));
        }
        foreach (Vector2I cell in burning)
        foreach (int heading in HexField.EdgeHeadings)
        {
            Vector2I next = HexField.Step(cell, heading);
            if (next == cell || !Field.InBounds(next))
                continue;
            if (Barred is not null && Barred(cell, next))
                continue;
            // Light refuses on a cell with no wood and on one that has already
            // burnt - which is the GDD's "burnt wood does not catch again", and it
            // is that refusal rather than a rule written twice here.
            if (LitAt(next) || !Light(next))
                continue;
            caught.Add(next);
        }
        foreach (Vector2I cell in burning)
        {
            int i = At(cell);
            if (++_since[i] >= Lasts)
                Out(cell);
        }
        return caught;
    }

    /// <summary>Move every fire on by one frame, and hand it to the neighbours
    /// whose turn has come.
    ///
    /// The spread is walked over what was already burning at the start of the
    /// frame, so a cell lit this frame does not pass the fire on in the same one -
    /// otherwise a long enough frame crosses the whole board in a single step, and
    /// what the front does would depend on the frame rate.</summary>
    public void Tick(double delta)
    {
        Size();
        if (_age.Length == 0)
            return;
        float step = (float)delta;
        int alight = 0, scorched = 0;
        var caught = new List<Vector2I>();
        for (int q = 0; q < _wide; q++)
        for (int r = 0; r < _tall; r++)
        {
            int i = r * _wide + q;
            float age = _age[i];
            if (age < 0.0f)
                continue;
            scorched++;
            // Capped so a board left burning for an hour does not lose its
            // precision; everything read off it has finished long before.
            float over = BurnFor + CatchWithin + SmokeFor;
            // Held at the plateau while the rules have not finished with it, and
            // clamped rather than skipped: a long frame that overshot Ripe would
            // leave the cell a little further into its burn than its neighbour,
            // and two woods lit on the same tick would go out on different frames.
            _age[i] = age = Ruled && !_freed[i]
                ? Mathf.Min(age + step, Ripe)
                : Mathf.Min(age + step, over + 1.0f);
            // Where the last tree on the cell stops burning. Two sums rather
            // than one because a held fire spends its tail at OutPace: read off
            // the burn alone, a ruled cell was called Burnt while its trees were
            // still visibly alight.
            float ends = Ruled ? Ripe + Dying : BurnFor + CatchWithin;
            bool flaming = age < ends;
            if (flaming)
                alight++;
            // What the board is told, off the boundary the count above already
            // is: a cell is alight while anything standing on it is in flame,
            // and the last tree on it is the one staggered by the whole of
            // CatchWithin. Two readings of one number rather than a judgement of
            // its own - see Told.
            Told(new Vector2I(q, r),
                 flaming ? CoverState.Burning : CoverState.Burnt);
            if (!Enabled || !Spreads)
                continue;
            var cell = new Vector2I(q, r);
            foreach (int heading in HexField.EdgeHeadings)
            {
                Vector2I next = HexField.Step(cell, heading);
                if (next == cell || !Field.InBounds(next))
                    continue;
                int j = At(next);
                if (j < 0 || _age[j] >= 0.0f)
                    continue;
                if (age >= Delay(cell, next))
                    caught.Add(next);
            }
        }
        foreach (Vector2I cell in caught)
            Light(cell);
        Alight = alight;
        Scorched = scorched;
    }

    /// <summary>How long this edge takes to carry the fire. Off both cells, so
    /// the pair agrees whichever end asks - a delay hashed off the source alone
    /// would light every neighbour of a cell in the same frame.</summary>
    public float Delay(Vector2I from, Vector2I to)
    {
        // Order-free, so the same edge is the same delay from either side.
        int a = Math.Min(Key(from), Key(to)), b = Math.Max(Key(from), Key(to));
        double roll = Grove.Hash01(a, b, StepSalt);
        return StepFor * (float)(1.0 - StepJitter + 2.0 * StepJitter * roll);
    }

    /// <summary>Seconds since this cell was lit, or -1.</summary>
    public float AgeAt(Vector2I cell)
    {
        int i = At(cell);
        return i < 0 || _age.Length == 0 ? -1.0f : _age[i];
    }

    public bool LitAt(Vector2I cell) => AgeAt(cell) >= 0.0f;

    /// <summary>How black the ground under this cell is, 0 to 1. Stays at one -
    /// ash does not lift, and a fire whose mark faded would be a fire nobody can
    /// find afterwards.</summary>
    public float AshAt(Vector2I cell)
    {
        float age = AgeAt(cell);
        return age < 0.0f ? 0.0f
                          : Mathf.Clamp(age / Mathf.Max(AshIn, 1e-4f), 0.0f, 1.0f);
    }

    /// <summary>
    /// What one tree on one cell looks like right now, given how late it catches.
    ///
    /// <b>The flame comes up fast and goes out slowly</b>, which is the same
    /// asymmetry the ruts fade on and the chop rises with: what is set alight is
    /// alight at once, and a fire dies down. Written as a curve on the age rather
    /// than as a clock of its own, because everything here is a function of that
    /// one age - the property the tank's plume has and the reason its cycle closes
    /// by construction.
    /// </summary>
    public Coat Of(Vector2I cell, double stagger)
    {
        float age = AgeAt(cell);
        if (age < 0.0f)
            return Green;
        float t = age - (float)stagger * CatchWithin;
        float burn = Mathf.Max(BurnFor, 1e-4f);
        // Where the picture starts handing over and where the fire is done with
        // it - see SwapAt and Hold. Everything the fire changes about a tree is
        // measured between these two, and only Burnt is measured from the end of
        // the burn.
        float handing = Handing;
        float hold = Hold;
        if (Ruled)
        {
            // The hold, and it is a tree's own and not the cell's - see Ripe. The
            // cell's age stops at Ripe, which is where the LAST tree comes out of
            // the handover; clamped there and no further, the first tree would be
            // a whole CatchWithin past it and half burnt down already.
            //
            // And the stagger comes back on the way out, spent on the release
            // rather than lost: held long enough, every tree on the cell is at the
            // same point in its burn, which is true - they have all been alight the
            // same length of time - and a cell that then goes out in one frame is
            // the one animation played eight times this class exists to avoid. The
            // same hash both ways, so the first to catch is the first to go.
            //
            // At OutPace, which is why the release is scaled and the stagger is
            // not: the delay is in the curve's own seconds, so dividing it out is
            // what spreads the going-out across the shorter event rather than
            // leaving three seconds of waiting inside three and a half.
            int held = At(cell);
            float since = held >= 0 && held < _freed.Length && !_freed[held]
                ? 0.0f
                : Mathf.Max(0.0f, age - Ripe);
            t = Mathf.Min(t, hold)
                + Mathf.Max(0.0f, since * Mathf.Max(OutPace, 0.01f)
                                  - (float)stagger * CatchWithin);
        }
        if (t <= 0.0f)
            return Green;
        bool burnt = t >= burn;
        // The paint blackens up to the handover, and it is the living art it
        // blackens - so it holds at one afterwards rather than coming off: that
        // picture is still on screen, fading out, and it has to stay charcoal
        // for the whole of it. Up to the handover and not over the whole burn,
        // because a picture that is still half green when it starts to fade puts
        // two different trees on screen at once.
        float charred = Mathf.Min(1.0f, t / handing);
        float flame = burnt
            ? 0.0f
            : Mathf.Min(1.0f, t / (FlareIn * burn))      // up in a fifth of it
              * Mathf.Clamp(1.0f - Mathf.Max(0.0f, t / burn - FadeFrom)
                                   / Mathf.Max(1.0f - FadeFrom, 1e-4f),
                            0.0f, 1.0f);                 // and down over the rest
        float smoke = burnt
            ? Mathf.Clamp(1.0f - (t - burn) / Mathf.Max(SmokeFor, 1e-4f),
                          0.0f, 1.0f)
            : Mathf.Min(1.0f, t / (0.25f * burn));
        // How far the burnt picture has taken over. Starts inside the burn and
        // not at the end of it: the char is the transition up to the handover,
        // this is the transition through it, and both are over while the tree is
        // still in flame.
        // No window means no window, said rather than divided by an epsilon: at
        // a tiny SwapFor the age lands inside it about as often as not, so the
        // hard swap would keep catching a frame or two halfway - which is the
        // one picture it exists to rule out.
        float swap = t < handing ? 0.0f
                     : SwapFor <= 0.0f ? 1.0f
                     : Mathf.Clamp((t - handing) / SwapFor, 0.0f, 1.0f);
        // And how much of the fuel is left, for whatever the fire takes away
        // rather than hands over - see Coat.Spent. Between the flame coming up
        // and the handover shutting: it is the flame that eats the scrub, so it
        // does not start before there is one, and it is gone by the frame the
        // trunks beside it have finished turning - which is the frame a ruled
        // fire holds on.
        float eating = Mathf.Max(hold - FlareIn * burn, 1e-4f);
        float spent = Mathf.Clamp((t - FlareIn * burn) / eating, 0.0f, 1.0f);
        return new Coat(charred, flame, smoke, burnt, swap, spent);
    }

    /// <summary>Put the wood back. Called by the reset and when the fire is
    /// switched off, for <see cref="Swell.Settle"/>'s reason: a state left
    /// standing comes back as a board that was burning before anybody lit it.
    /// </summary>
    public void Douse()
    {
        // The board back with the wood, and before the ages go: a cell that has
        // been forgotten here cannot be found to put right afterwards.
        for (int q = 0; q < _wide; q++)
        for (int r = 0; r < _tall; r++)
        {
            int i = r * _wide + q;
            if (i < _age.Length && _age[i] >= 0.0f)
                Told(new Vector2I(q, r), CoverState.Intact);
        }
        Array.Fill(_age, -1.0f);
        Array.Fill(_freed, false);
        Array.Fill(_since, -1);
        Alight = 0;
        Scorched = 0;
    }

    /// <summary>
    /// Tell the board what the wood on a cell is doing, if it is not what the
    /// board already thinks.
    ///
    /// <b>The state is the board's and the clock is this class's</b>, which is
    /// the whole of the arrangement: everything here is a function of one age -
    /// the char, the flame, the smoke, the handover - and none of that is a
    /// thing a rule can be written against. Burning and Burnt are, and they are
    /// the two the cell carries.
    ///
    /// <b>Only on a change.</b> Written every frame it would be a redraw per
    /// burning cell per frame for a value that moves twice in the life of a
    /// fire; asked first, a wood that has been alight for six seconds costs a
    /// comparison.
    /// </summary>
    private void Told(Vector2I cell, CoverState state)
    {
        // <b>And never over a wood that has been driven flat.</b> Cleared is
        // spent, the way Burnt is, and a fire whose cell is gone has nothing
        // left to say about it - said here because this class keeps its own
        // clock and would otherwise write Burnt back on the next frame and go on
        // doing it for ever, with the bulldozer writing Cleared in between. Two
        // owners of one slot, arguing at sixty frames a second: measured on the
        // first heavy to flatten a burnt-out wood, GDD classes.md "живого или
        // сгоревшего".
        if (Field.CoverStateAt(cell) != state && !Field.Felled(cell))
            Field.SetCoverState(cell, state);
    }

    public string Note()
    {
        if (Scorched == 0)
            return "no fire";
        if (!Ruled)
            return $"{Scorched} cells burnt, {Alight} still alight, "
                   + $"step {StepFor:F1}s burn {BurnFor:F1}s";
        int held = 0;
        for (int q = 0; q < _wide; q++)
        for (int r = 0; r < _tall; r++)
            if (Waiting(new Vector2I(q, r)))
                held++;
        return $"{Scorched} cells burnt, {Alight} still alight, "
               + $"{held} holding for the rules, {Lasts} rounds each";
    }

    private int Key(Vector2I cell) => cell.Y * 1_000 + cell.X;

    private int At(Vector2I cell)
    {
        if (cell.X < 0 || cell.Y < 0 || cell.X >= _wide || cell.Y >= _tall)
            return -1;
        return cell.Y * _wide + cell.X;
    }

    private void Size()
    {
        if (_wide == Field.Columns && _tall == Field.Rows
            && _age.Length == _wide * _tall)
            return;
        _wide = Field.Columns;
        _tall = Field.Rows;
        _age = new float[_wide * _tall];
        _freed = new bool[_wide * _tall];
        _since = new int[_wide * _tall];
        Array.Fill(_age, -1.0f);
        Array.Fill(_since, -1);
        Alight = 0;
        Scorched = 0;
    }
}
