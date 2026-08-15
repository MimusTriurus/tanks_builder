#nullable enable
using System.ComponentModel;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.ReflectorNet.Utils;

namespace TankSpriteTest;

/// <summary>
/// The bench's own MCP tools, opted in by <see cref="Main"/> under <c>--mcp</c>.
///
/// <para>
/// These are not the addon's forty-two: those are editor tools - scene tree, nodes,
/// resources, a shot of the editor viewport - and the editor is not where this bench
/// is judged. Everything here answers a question about the <b>running</b> game, which
/// until now could only be asked by killing the process and starting it again with a
/// different flag. That is the whole point of the runtime side.
/// </para>
///
/// <para>
/// Every handler marshals onto the main thread before it touches anything. A tool call
/// arrives on the connection's thread, and Godot's API is not thread-safe; reading a
/// facing off a sprite mid-frame is the kind of failure that shows up later as one bad
/// pixel in a capture and gets blamed on the atlas.
/// </para>
///
/// <para>
/// Read-only tools say so (<c>ReadOnlyHint</c>), and they mean it: none of them advances
/// a clock. The commanding ones do exactly what their key does and nothing besides -
/// see <c>Main.Mcp.cs</c> for why they go through the key's path rather than their own.
/// </para>
/// </summary>
[AiToolType]
public class McpBench
{
	const string NoBench = "the bench is not running with --mcp";

	[AiTool("bench-state", Title = "Bench / Board State",
		ReadOnlyHint = true, IdempotentHint = true, OpenWorldHint = false)]
	[Description("Report every tank on the board: index, tag (LTP/MTP/HTP), which one is "
		+ "being driven, its cell, hull and turret facing in degrees, speed in px/s, "
		+ "penetrations taken out of the three that kill, and whether it is wrecked, "
		+ "burning, wading or engaging someone. The same figures --trace prints.")]
	public string State(string? nothing = null)
		=> MainThread.Instance.Run(() => Main.Live?.BoardState() ?? NoBench);

	[AiTool("bench-capture", Title = "Bench / Capture Frame",
		ReadOnlyHint = true, OpenWorldHint = false)]
	[Description("Save the current viewport to a PNG at the given absolute path and return "
		+ "that path. The same call --capture makes, so a frame taken this way is "
		+ "comparable with one taken by the flag.")]
	public string Capture(
		[Description("Absolute path to write the PNG to.")] string path)
		=> MainThread.Instance.Run(() => Main.Live?.CaptureTo(path) ?? NoBench);

	[AiTool("bench-select", Title = "Bench / Select Tank", OpenWorldHint = false)]
	[Description("Take control of one of the tanks by index, as the number keys 1-3 do. "
		+ "The other two stay on the board in whatever state they were left.")]
	public string Select(
		[Description("Zero-based tank index, in the order bench-state lists them.")] int index)
		=> MainThread.Instance.Run(() => Main.Live?.SelectTank(index) ?? NoBench);

	[AiTool("bench-drive", Title = "Bench / Drive To Cell", OpenWorldHint = false)]
	[Description("Order the driven tank to a cell, as a left click on empty ground does. "
		+ "The cell is clamped to the board. Another tank's cell is not a destination.")]
	public string Drive(
		[Description("Column of the target cell.")] int col,
		[Description("Row of the target cell.")] int row)
		=> MainThread.Instance.Run(() => Main.Live?.DriveTo(col, row) ?? NoBench);

	[AiTool("bench-fire", Title = "Bench / Fire", OpenWorldHint = false)]
	[Description("Fire the driven tank's gun once, as Z does.")]
	public string Fire(string? nothing = null)
		=> MainThread.Instance.Run(() => Main.Live?.FireGun() ?? NoBench);

	[AiTool("bench-attack", Title = "Bench / Attack Tank", OpenWorldHint = false)]
	[Description("Order the driven tank to attack another by index, as a right click on it "
		+ "does. It lays its gun and opens fire on its own once it has a firing lane, a "
		+ "clear one, a standstill, a laid gun and a loaded round - not before.")]
	public string Attack(
		[Description("Zero-based index of the tank to attack.")] int index)
		=> MainThread.Instance.Run(() => Main.Live?.AttackTank(index) ?? NoBench);

	[AiTool("bench-destroy", Title = "Bench / Destroy Tank",
		DestructiveHint = true, OpenWorldHint = false)]
	[Description("Destroy a tank outright, as a middle click on it does. Reversible with "
		+ "bench-reset.")]
	public string Destroy(
		[Description("Zero-based index of the tank to destroy.")] int index)
		=> MainThread.Instance.Run(() => Main.Live?.DestroyTank(index) ?? NoBench);

	[AiTool("bench-reset", Title = "Bench / Reset Board", OpenWorldHint = false)]
	[Description("Put the board back as R does: tanks repaired, fires out, shells cleared, "
		+ "orders dropped, camera home.")]
	public string Reset(string? nothing = null)
		=> MainThread.Instance.Run(() => Main.Live?.ResetBoard() ?? NoBench);
}
