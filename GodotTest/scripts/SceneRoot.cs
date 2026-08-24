using System;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// What every runnable root in this project has in common: the five flags that
/// are about the run rather than about what is being run, and the screenshot
/// they exist for.
///
/// <b>Written once because it was written four times.</b> The harness and the
/// three benches each carried their own <c>_capturePath</c>, <c>_captureAt</c>,
/// <c>_zoomAt</c>, <c>_noUi</c>, <c>_showUi</c>, their own parse of the five
/// flags, and their own <c>Capture</c> - four copies of a method that differed
/// only in the word it printed. Four copies agree until the first edit lands in
/// one of them, and the edit that lands here is the one that matters: almost
/// every measurement in this project is a pixel difference between two shots, so
/// a root whose capture drifted from the others would quietly be measuring a
/// different thing.
///
/// <b>What is deliberately not here is the frame counter.</b> Each root counts
/// its own frames and decides for itself when the shot is due - the harness
/// fires on <c>++frames &gt; CaptureAt</c>, the benches on <c>frame &gt;=
/// CaptureAt</c>, and the wall prints a drift line on the exact frame first.
/// Folding those into one rule would move every baseline in the repository by a
/// frame, which is a picture change wearing a refactor's clothes.
///
/// <b>The relief stands are not here either, and that is named rather than
/// forgotten.</b> <see cref="ReliefBench"/> and <see cref="Relief3D"/> shoot with
/// <c>--shot</c> and <c>--shot-at</c>, which are the same two flags under other
/// names. Bringing them in means renaming a documented invocation, so it is a
/// change to make on purpose rather than in passing.
/// </summary>
public abstract partial class SceneRoot : Node2D
{
	/// <param name="captureAt">The frame this root shoots on when
	/// <c>--capture-at</c> is not given. A constructor argument rather than a
	/// field default so a new root has to say what its own is - the harness wants
	/// four, a bench that drives a tank wants forty.</param>
	protected SceneRoot(int captureAt) => CaptureAt = captureAt;

	/// <summary>Where the screenshot goes, or null for an interactive run.</summary>
	protected string? CapturePath;

	/// <summary>Which frame it is taken on.</summary>
	protected int CaptureAt;

	/// <summary>Whether anybody said when. The harness has a flag that wants a
	/// capture frame of its own - an order takes ninety frames to be worth looking
	/// at - and writing that unconditionally made --capture-at work or not by
	/// which side of --drive it stood on.</summary>
	protected bool CaptureAtAsked;

	/// <summary>The zoom <c>--zoom</c> asked for, or null for the root's own.
	/// Nullable rather than pre-filled, because "nobody said" and "somebody said
	/// this happens to be the default" are different: a bench that fits the view
	/// to its board has to know which it is.</summary>
	protected float? ZoomAt;

	/// <summary>Whether the interface is drawn. <c>--ui</c> wins over
	/// <c>--no-ui</c>: it is the more specific request, and the pair only ever
	/// arrives together by accident.</summary>
	protected bool NoUi;

	protected bool ShowUi;

	/// <summary>
	/// Consume one of the five if it is next, and say whether it did.
	///
	/// A method the caller folds into its own loop rather than a parser that owns
	/// the arguments: every root's remaining flags are its own, and a base that
	/// swallowed the array would have to hand back what it did not understand,
	/// which is the same loop with an extra copy of the arguments in it.
	/// </summary>
	protected bool ReadCommonFlag(string[] args, ref int i)
	{
		string arg = args[i];
		if (arg == "--capture" && i + 1 < args.Length)
		{
			CapturePath = args[++i];
			return true;
		}
		if (arg.StartsWith("--capture="))
		{
			CapturePath = arg["--capture=".Length..];
			return true;
		}
		if (arg == "--capture-at" && i + 1 < args.Length
			&& int.TryParse(args[i + 1], out int at))
		{
			CaptureAt = at;
			CaptureAtAsked = true;
			i++;
			return true;
		}
		// Invariant culture, and it is not pedantry: this machine's locale takes a
		// comma, so a bare TryParse returns false on "1.4", the default stands,
		// and two shots come back byte for byte identical - which reads as the
		// flag doing nothing rather than as the flag not parsing.
		if (arg == "--zoom" && i + 1 < args.Length
			&& float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float,
							  System.Globalization.CultureInfo.InvariantCulture,
							  out float zoom))
		{
			ZoomAt = Mathf.Clamp(zoom, 0.25f, 8.0f);
			i++;
			return true;
		}
		if (arg == "--no-ui")
		{
			NoUi = true;
			return true;
		}
		if (arg == "--ui")
		{
			ShowUi = true;
			return true;
		}
		return false;
	}

	/// <summary>
	/// The rule every root applies once its own flags are read: a shot is
	/// evidence, so it is taken on a pinned clock and without the interface.
	///
	/// <paramref name="proof"/> rather than reading <see cref="CapturePath"/>
	/// here, because the harness has a second thing that counts as proof -
	/// <c>--trace</c> - and a base that guessed would be guessing about a flag it
	/// does not own.
	/// </summary>
	protected void SettleForProof(bool proof)
	{
		if (!proof)
			return;
		FrameClock.FixedStep = 1.0 / 60.0;
		NoUi = !ShowUi;
	}

	/// <summary>The screenshot itself. The directory is made rather than assumed:
	/// a capture into a path that does not exist yet is the ordinary case for a
	/// scratch run, and Godot's own failure for it reads like a render error.
	/// </summary>
	protected void Capture(string path)
	{
		Image image = GetViewport().GetTexture().GetImage();
		DirAccess.MakeDirRecursiveAbsolute(path.GetBaseDir());
		Error err = image.SavePng(path);
		if (err != Error.Ok)
			GD.PushError($"{Name}: could not write {path}: {err}");
		else
			GD.Print($"{Name}: wrote {path}");
	}

	/// <summary>Whether a middle-button press and release were one place, so the
	/// gesture was a tap rather than a pan. Screen pixels, not world ones: the
	/// same movement of the hand is eight world pixels at zoom 8, and the
	/// question is about the hand.</summary>
	internal static bool MiddleTap(Vector2 from, Vector2 to)
		=> from.DistanceTo(to) <= MiddleSlack;

	/// <summary>How far the middle button may travel and still be a tap.</summary>
	internal const float MiddleSlack = 4.0f;
}
