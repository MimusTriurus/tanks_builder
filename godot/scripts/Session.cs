using Godot;

namespace TankSpriteTest;

/// <summary>
/// What one root hands to the next across a scene change: which board to open,
/// and whether the editor is what sent us.
///
/// <b>The project's first scene change, and this is the whole of the coupling it
/// costs.</b> Eight roots have lived beside each other by never mentioning one
/// another - see <c>godot/CLAUDE.md</c>, where "ни один разделяемый модуль не
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
/// <b>The editor's board goes over in memory, not through the folder</b>
/// (Play-in-Editor, stage 1 of docs/editor-ux-plan.md, 2026-10-26). Run hands
/// the bench the board it has - <see cref="Board"/>, built from the draft,
/// saved or not - and the editor itself is not freed but put aside
/// (<see cref="Parked"/>, <see cref="Loading.Park"/>), so Edit gives back the
/// same editor: its undo, its camera, its brush. Before it, Run saved first and
/// the bench read the file: a try cost the file, a compiled board refused to
/// run, and every way back rebuilt the editor without its history.
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

    /// <summary>The board itself, when the editor hands over what it has in
    /// memory: played instead of looking <see cref="Map"/> up.</summary>
    public static BoardMap? Board;

    /// <summary>Whether <see cref="Board"/> is ahead of its file - the bench
    /// says so on its way back.</summary>
    public static bool Unsaved;

    /// <summary>The cell the bench's tank starts on (shift+F5, the map's own
    /// coordinates), or null for the board's first home.</summary>
    public static Vector2I? Start;

    /// <summary>The editor, out of the tree while its board is played, or null.
    /// Whoever frees the bench for good frees this with it.</summary>
    public static Node? Parked;

    /// <summary>The name the next root should open, taking it off the handoff.
    /// Null when nobody handed anything over.</summary>
    public static string? Take()
    {
        string? said = Map;
        Map = null;
        return said;
    }

    /// <summary>The board handed over in memory, taken off the handoff with
    /// its start cell; null when the handoff is a name or nothing.</summary>
    public static BoardMap? TakeBoard(out Vector2I? start, out bool unsaved)
    {
        BoardMap? board = Board;
        start = Start;
        unsaved = Unsaved;
        Board = null;
        Start = null;
        Unsaved = false;
        return board;
    }
}
