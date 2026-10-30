using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// A shader parameter set or read by its name as written,
/// <c>mat.Param("char_cover", k)</c>, through one <see cref="StringName"/> per
/// name for the life of the process.
///
/// <b>Why not <c>SetShaderParameter("char_cover", k)</c>:</b> the string goes
/// to a new <see cref="StringName"/> on every call - an object with a
/// finalizer, some 80-100 bytes - and the bench made several hundred such
/// calls a frame (the cel effects, the stage's water, the grass's fire). It was
/// most of the 50 KB a frame the main thread allocated and the GC came back
/// for every 13 MB, 10-25 ms a time (BOT-3, docs/perf.md). The lookup here is
/// by the string's own hash and allocates nothing. Code that runs every frame
/// uses this; a name built at run time (<c>$"mark_{i}"</c>) should be built
/// once too.
/// </summary>
public static class ShaderParams
{
    private static readonly Dictionary<string, StringName> _names = new();

    /// <summary>The one <see cref="StringName"/> for <paramref name="name"/>.</summary>
    public static StringName Name(string name)
    {
        if (!_names.TryGetValue(name, out StringName? n))
            _names[name] = n = new StringName(name);
        return n;
    }

    public static void Param(this ShaderMaterial m, string name, Variant value) =>
        m.SetShaderParameter(Name(name), value);

    public static Variant Param(this ShaderMaterial m, string name) =>
        m.GetShaderParameter(Name(name));
}
