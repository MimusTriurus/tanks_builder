using System;

namespace TankSpriteTest;

/// <summary>
/// The rounds a gun has: how big each is drawn, and how deep it goes.
///
/// <b>One list, and that is the whole of why this is a type rather than three
/// fields on the harness.</b> <see cref="TankBench"/> already reached into
/// <see cref="Main"/> for <c>CalibreAt</c> and <c>BiteFor</c> so that its
/// "heavy" would be the harness's heavy; a bench that wrote its own would be a
/// bench firing a different shell under the same name.
/// </summary>
public static class Ordnance
{
	/// <summary>Calibres, as a multiplier on the rendered hit - key Y.
	///
	/// The layer is a sprite, so this is a scale on the drawn rect and costs
	/// nothing; what it cannot do is change the shape of the burst, only its
	/// size. Kept modest for that reason - past about half again the rendered
	/// size the dust starts to read as soft rather than as bigger, and the
	/// answer there is another render, not a bigger number.
	/// </summary>
	private static readonly float[] Calibres = { 0.7f, 1.0f, 1.4f };

	public static int Count => Calibres.Length;

	/// <summary>The nth calibre itself.</summary>
	public static float At(int n) => Calibres[Math.Clamp(n, 0, Calibres.Length - 1)];

	/// <summary>How many levels of armour the nth round goes through in one hit.
	///
	/// The position in the calibre list rather than a table beside it: the
	/// calibres are ordered, there are as many of them as there are levels of
	/// damage, and "the smallest round does one level" is the whole rule. A
	/// second list would be a second thing to keep in step with the first. The
	/// self test asserts the two lengths still line up - adding a fourth round or
	/// a fourth decal without the other is the way this stops meaning anything.
	/// </summary>
	public static int BiteFor(int calibre) => calibre + 1;

	/// <summary>Which of the three a scale asked for on the command line means.
	///
	/// Snapped rather than taken as given, for the reason a bearing is snapped to
	/// a side of the hex: a gun has the calibres it has, and there is one list of
	/// them rather than a keyboard list and a flag list. An arbitrary number was
	/// accepted here before, and the cost was a dropdown reading 1.0x with a 1.4
	/// round loaded - the control lying about what it would fire.</summary>
	public static int For(float scale)
	{
		int best = 0;
		for (int i = 1; i < Calibres.Length; i++)
			if (Math.Abs(Calibres[i] - scale) < Math.Abs(Calibres[best] - scale))
				best = i;
		return best;
	}
}
