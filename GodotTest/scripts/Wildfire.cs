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
    /// <b>Inside the flame's plateau, and that is what picks the number.</b> The
    /// flame is at full strength from 0.18 of the burn to 0.55 of it, so a window
    /// opening at 0.45 runs under the fire and closes just as it starts to drop -
    /// and then the charcoal stands in its own flame for the rest of the burn,
    /// which is the whole of what was asked for. Later than 0.55 and the handover
    /// happens as the fire dies, which is the frames it exists to hide behind.
    ///
    /// <b>The char has to reach one here and not at the end</b>, because it
    /// belongs to the outgoing picture: a living tree still half green, cross-faded
    /// into charcoal, is two different trees on screen at once rather than one
    /// turning.
    /// </summary>
    public float SwapAt = 0.45f;

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
    /// <b>It starts where the swap starts and ends where the burn does</b>, so
    /// it needs no window of its own: fuel running out and a fire going out are
    /// one event, and the flame is at zero on the same frame this reaches one.
    /// Roughly five seconds of the nine at the settings above, which is what
    /// makes it a thing being consumed rather than an object deleted.</param>
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
        return true;
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
            _age[i] = age = Mathf.Min(age + step, over + 1.0f);
            if (age < BurnFor + CatchWithin)
                alight++;
            if (!Enabled)
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
        if (t <= 0.0f)
            return Green;
        float burn = Mathf.Max(BurnFor, 1e-4f);
        bool burnt = t >= burn;
        // Where the picture starts handing over - see SwapAt. Everything about the
        // char and the swap is measured from here, and only Burnt is measured from
        // the end of the burn.
        float handing = burn * Mathf.Clamp(SwapAt, 0.02f, 1.0f);
        // The paint blackens up to the handover, and it is the living art it
        // blackens - so it holds at one afterwards rather than coming off: that
        // picture is still on screen, fading out, and it has to stay charcoal
        // for the whole of it. Up to the handover and not over the whole burn,
        // because a picture that is still half green when it starts to fade puts
        // two different trees on screen at once.
        float charred = Mathf.Min(1.0f, t / handing);
        float flame = burnt
            ? 0.0f
            : Mathf.Min(1.0f, t / (0.18f * burn))        // up in a fifth of it
              * Mathf.Clamp(1.0f - Mathf.Max(0.0f, t / burn - 0.55f) / 0.45f,
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
        // rather than hands over - see Coat.Spent. From the same anchor as the
        // swap, so the instant a trunk begins to turn to charcoal is the instant
        // the scrub beside it begins to go; to the end of the burn rather than
        // over a window, so it reaches nothing on the frame the flame does.
        float spent = t < handing ? 0.0f
            : Mathf.Clamp((t - handing) / Mathf.Max(burn - handing, 1e-4f),
                          0.0f, 1.0f);
        return new Coat(charred, flame, smoke, burnt, swap, spent);
    }

    /// <summary>Put the wood back. Called by the reset and when the fire is
    /// switched off, for <see cref="Swell.Settle"/>'s reason: a state left
    /// standing comes back as a board that was burning before anybody lit it.
    /// </summary>
    public void Douse()
    {
        Array.Fill(_age, -1.0f);
        Alight = 0;
        Scorched = 0;
    }

    public string Note() =>
        Scorched == 0
            ? "no fire"
            : $"{Scorched} cells burnt, {Alight} still alight, "
              + $"step {StepFor:F1}s burn {BurnFor:F1}s";

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
        Array.Fill(_age, -1.0f);
        Alight = 0;
        Scorched = 0;
    }
}
