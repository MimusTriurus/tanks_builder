namespace TankSpriteTest;

/// <summary>
/// What one root hands to the next across a scene change: which board to open,
/// and whether the editor is what sent us.
///
/// <b>The project's first scene change, and this is the whole of the coupling it
/// costs.</b> Eight roots have lived beside each other by never mentioning one
/// another - see <c>GodotTest/CLAUDE.md</c>, where "ни один разделяемый модуль не
/// знает, какая сцена его запустила" is the rule and is checked by grep. Run and
/// Edit break that between exactly two of them, on purpose and in one place: the
/// alternative was letting a tank into the editor's own scene, which is a second
/// harness inside the tool the harness is judged with.
///
/// <b>Static because a scene change keeps nothing else.</b> The tree is replaced
/// whole; command-line args survive it (<c>OS.GetCmdlineUserArgs</c> still
/// answers), which is why <see cref="Map"/> has to <i>win</i> over <c>--map</c> -
/// a flag from the original launch is stale by the time a handoff exists.
///
/// <b>Consumed by whoever opens it.</b> The reader takes the name and sets this
/// back to null, so a root coming up on its own never finds somebody else's
/// board waiting. A run under <c>--capture</c>, <c>--selftest</c> or
/// <c>--map-report</c> never sets any of it, so none of them change behaviour.
///
/// <b>The board goes through the folder rather than through here.</b> Run saves
/// first - so what is played is what is on disk, and a name is enough to say
/// which. That is also the one thing it costs: Run on a compiled board refuses,
/// because <see cref="MapFile.Save"/> refuses to shadow one, and the author has
/// to save it under another name first. The refusal is the save's own words.
/// </summary>
public static class Session
{
    /// <summary>The board the next root should open, or null for whatever its
    /// own flags say.</summary>
    public static string? Map;

    /// <summary>Whether the editor sent the harness here, so that the harness
    /// offers the way back. Not <c>Map is not null</c>: Edit hands a name over
    /// too, and it is going the other way.</summary>
    public static bool Editing;

    /// <summary>The name the next root should open, taking it off the handoff.
    /// Null when nobody handed anything over.</summary>
    public static string? Take()
    {
        string? said = Map;
        Map = null;
        return said;
    }
}
