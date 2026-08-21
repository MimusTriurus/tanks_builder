using System.IO;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// Where the pipeline's output lives, derived from where this project lives.
///
/// The bench reads its pixels off the filesystem at run time rather than
/// importing them as project resources, and that part is deliberate: re-render
/// the atlases in Blender and the next run picks them up, with no copying and
/// no re-import step. For a harness whose whole job is to look at freshly
/// rendered sprites that is worth more than export portability.
///
/// What was not deliberate is that the path was a literal. Five of them, in
/// four files, all naming a directory this repository used to sit in - so a
/// move renamed every asset root at once and nothing said so. The failure is
/// the quiet kind: <see cref="AtlasSet.Load"/> reports a missing file, `Main`
/// puts one line in the HUD, and `--capture` writes a blank frame instead of
/// refusing, because the vehicles are never built. Of forty-one selftest
/// themes exactly one says anything; the rest skip on `Error.Length > 0`.
///
/// So it is derived instead. `res://` is `GodotTest/`, the output is its
/// sibling, and the one fact the bench needs about its own location is the one
/// Godot already knows. Same trick <see cref="PanelText"/> and
/// <see cref="ClassConfig"/> have always used for their own files.
/// </summary>
internal static class AssetRoot
{
    /// <summary>The repository root: `res://`'s parent, absolute and with
    /// forward slashes, because it goes into paths the rest of the code builds
    /// by interpolation and into the HUD line that names it.</summary>
    internal static readonly string Repo =
        Path.GetFullPath(Path.Combine(
            ProjectSettings.GlobalizePath("res://"), ".."))
            .Replace('\\', '/').TrimEnd('/');

    /// <summary>What <c>sprite_atlas.py</c> wrote, one folder per tank.</summary>
    internal static readonly string Sprites = Repo + "/Sprites";

    /// <summary>Where <c>stage_sounds.sh</c> puts the audio. Not in this
    /// repository - see the script - so this directory is often simply absent,
    /// and everything downstream of it has to be happy about that.</summary>
    internal static readonly string Sounds = Repo + "/Sounds";

    /// <summary>The hand-drawn ground. Optional for the reason the sounds are:
    /// without it the field falls back to the rendered tile and everything else
    /// is unchanged.</summary>
    internal static readonly string Terrains = Repo + "/Images/Terrains";

    /// <summary>Trees, bushes and stones.</summary>
    internal static readonly string Props = Repo + "/Images/Vegetation";

    /// <summary>The pond's drawn surface. Its own folder rather than a file in
    /// Terrains, because TerrainSet reads that folder as kinds of ground and a
    /// kind is picked by hash - which would scatter open water across the
    /// hilltops. Water is a map, not paint; see <see cref="WaterArt"/>.
    /// </summary>
    internal static readonly string Water = Repo + "/Images/Water";

    /// <summary>Scratch output from ad-hoc runs. Gitignored, and it may not
    /// exist: anything writing here creates it or says why not.</summary>
    internal static readonly string Out = Repo + "/out";
}
