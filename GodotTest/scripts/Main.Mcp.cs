using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using com.IvanMurzak.Godot.MCP;
using com.IvanMurzak.Godot.MCP.Connection;
using com.IvanMurzak.Godot.MCP.Runtime;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The bench's side of Godot-MCP's runtime mode: a small public façade over the
/// private controls an agent would otherwise have to reach by restarting the
/// process with a flag.
///
/// <para>
/// It is a partial of <see cref="Main"/> rather than a set of accessors on the
/// harness, because every method here is one the keyboard already calls. That is
/// the whole design constraint: an MCP tool must go through the very path the key
/// goes through, or the bench acquires a second way to fire a shot and the two
/// drift - the same argument that makes each panel row a pair of delegates onto
/// the field the key writes rather than a copy of it.
/// </para>
///
/// <para>
/// <b>Off unless asked for (<c>--mcp</c>).</b> The connection opens a socket and
/// services calls between frames, and almost every measurement on this bench is a
/// pixel difference between two captures. A run that answers a tool call mid-frame
/// is not the run the other capture was taken from, so <c>--capture</c>,
/// <c>--trace</c> and <c>--selftest</c> must be able to state that nothing was
/// listening. The addon's own default is the same and for the same reason: zero
/// tools, no auto-connect.
/// </para>
/// </summary>
public sealed partial class Main
{
	/// <summary>Set by <c>_Ready</c> under <c>--mcp</c>; the tools reach the live bench through it.</summary>
	public static Main? Live;

	/// <summary>
	/// Whether the runtime connection opens on an ordinary run - <c>--no-mcp</c> shuts
	/// it. On.
	///
	/// <para>
	/// It came up off, and that default was about the evidence runs rather than about
	/// the connection: almost every measurement here is a pixel difference between two
	/// captures, and a run that serviced a tool call between frames is not the run the
	/// other capture came from. But that is a statement about <c>--capture</c>,
	/// <c>--trace</c> and <c>--selftest</c>, and those refuse the connection outright
	/// (see <see cref="StartMcp"/>) - so charging every ordinary session a flag for a
	/// guarantee three modes already enforce themselves bought nothing. A tool set you
	/// have to remember to switch on is a tool set nobody reaches for, which is the
	/// same argument that turned the tracer on.
	/// </para>
	///
	/// <para>
	/// Named here rather than left in the initialiser for the reason
	/// <see cref="Recoil.ShearOnByDefault"/> is: a default nobody can point at is a
	/// default that quietly changes.
	/// </para>
	/// </summary>
	public const bool McpOnByDefault = true;

	private bool _mcpEnabled = McpOnByDefault;

	private GodotMcpRuntimeHandle? _mcp;

	/// <summary>
	/// Open the runtime connection unless this run is one that must not have one.
	/// Called at the very end of <c>_Ready</c>, after the board exists: a tool that
	/// arrives before the tanks do would answer about a board that is not there yet,
	/// and the first thing an agent does is ask for the state.
	///
	/// <para>
	/// <b>The three evidence modes refuse it here, and refuse it whatever the flag
	/// said.</b> A capture is proof, and proof has to be able to state that nothing
	/// was listening - a connection services calls between frames, so a run that
	/// answered one is not the run the other half of the A/B came from. Enforced at
	/// the point of connecting rather than by leaving the default off, because that
	/// put the guarantee in the hands of whoever remembered to omit a flag: the mode
	/// that needs it is the mode that knows it needs it. <c>--mcp</c> does not
	/// override this; there is nothing it could usefully mean here.
	/// </para>
	///
	/// <para>
	/// The tool set is named rather than swept off the assembly. <c>WithToolsFromAssembly</c>
	/// would also pick up the addon's own editor families - they compile into this same
	/// assembly whenever <c>TOOLS</c> is defined, which it is for every run out of the
	/// editor - and a game process advertising <c>editor-scene-open</c> is a game process
	/// lying about what it can do.
	/// </para>
	///
	/// <para>
	/// Mode and host are set in code as well as in <c>.env</c>. The file is the
	/// convenience; this is the guarantee - the in-code layer is the highest of the
	/// three, so a build of this bench cannot dial the cloud because a file went
	/// missing.
	/// </para>
	/// </summary>
	private void StartMcp()
	{
		if (_capturePath is not null || _traceFrames > 0 || _selfTest)
			return;
		if (!_mcpEnabled)
			return;

		Live = this;
		_mcp = GodotMcpRuntime.Initialize(builder =>
		{
			builder.WithConfig(config =>
			{
				config.ConnectionMode = GodotMcpConnectionMode.Custom;
				config.CustomHost = McpHost;
			});
			builder.WithTools(typeof(McpBench));
		}).Build();

		// Fire and forget: Connect is a handshake with a server that may not be up,
		// and a bench that refuses to open because nothing was listening is worse
		// than one that opens with no tools. What it did is said out loud instead -
		// and that line matters more now than it did behind a flag, because "no
		// server" is the ordinary state of an ordinary session and has to read as a
		// note rather than as a fault.
		//
		// BOUNDED, and that is what makes the line true. Left to itself Connect
		// keeps trying rather than failing, so with nothing listening it printed
		// NOTHING - measured, forty seconds of silence - and a default-on feature
		// that says nothing at all is one you cannot tell from a broken one. On
		// loopback a server either answers at once or is not there, so five seconds
		// is the whole question with room to spare.
		//
		// Faulted and cancelled as well as false: a refused socket is the common
		// case, and an unobserved exception on a task nobody awaits is a line in the
		// log that looks like the bench breaking.
		var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5.0));
		_ = _mcp.Connect(deadline.Token).ContinueWith(t =>
		{
			deadline.Dispose();
			GD.Print(t.Status == TaskStatus.RanToCompletion && t.Result
				? $"[mcp] bench tools live on {McpHost}"
				: $"[mcp] no server on {McpHost} - tools off this session"
				  + " (start one to use them; --no-mcp to stop trying)");
		});
	}

	/// <summary>
	/// The self-hosted server both this and the editor addon dial. One number, and it
	/// is the one <c>.env</c> pins - see the file for why it is pinned rather than
	/// derived, and for the two URLs that come off it.
	/// </summary>
	private const string McpHost = "http://localhost:8427";

	/// <summary>
	/// One line per tank, in the order the number keys address them, and the same
	/// figures <c>--trace</c> prints - tag, cell, both facings, speed, and what has
	/// happened to it. Deliberately the trace's numbers rather than a second set:
	/// a tool that reported its own idea of the board would be a second opinion to
	/// keep in agreement, and this project has paid for that twice.
	/// </summary>
	public string BoardState()
	{
		if (_vehicles.Count == 0)
			return "no tanks loaded";

		return string.Join("\n", _vehicles.Select((v, i) =>
		{
			TankSprite s = v.Sprite;
			return string.Format(CultureInfo.InvariantCulture,
				"{0}{1} {2,-3} cell {3},{4}  hull {5,6:F1}  turret {6,6:F1}"
				+ "  speed {7,6:F1}  pen {8}/{9}{10}{11}{12}{13}",
				i == _active ? "*" : " ", i, v.Tag,
				v.Cell.X, v.Cell.Y,
				s.HullFacing, s.TurretFacing, v.Speed,
				s.Penetrations, Gunnery.PenetrationsToKill,
				s.Wrecked ? "  wrecked" : "",
				v.Burning ? "  burning" : "",
				v.Wading ? "  wading" : "",
				v.Target is not null ? $"  -> {v.Target.Tag}" : "");
		}));
	}

	/// <summary>
	/// Save the viewport to <paramref name="path"/>. The same call <c>--capture</c>
	/// makes, so a frame taken through a tool and a frame taken by the flag are the
	/// same picture - which is what makes it worth comparing them.
	/// </summary>
	public string CaptureTo(string path)
	{
		Capture(path);
		return path;
	}

	/// <summary>Take control of tank <paramref name="index"/> - what the number keys do.</summary>
	public string SelectTank(int index)
	{
		if (index < 0 || index >= _vehicles.Count)
			return $"no tank {index}; there are {_vehicles.Count}";
		Select(index);
		return $"driving {Active.Tag}";
	}

	/// <summary>Order the driven tank to a cell - what a left click on empty ground does.</summary>
	public string DriveTo(int col, int row)
	{
		if (_vehicles.Count == 0)
			return "no tanks loaded";
		Vector2I cell = _field.ClampCell(new Vector2I(col, row));
		OrderMoveTo(cell);
		return $"{Active.Tag} ordered to {cell.X},{cell.Y}";
	}

	/// <summary>Fire the driven tank's gun - <c>Z</c>.</summary>
	public string FireGun()
	{
		if (_vehicles.Count == 0)
			return "no tanks loaded";
		Fire();
		return $"{Active.Tag} fired";
	}

	/// <summary>
	/// Order the driven tank to attack another - a right click on a tank. Nothing
	/// is fired here: the tank lays its gun and opens up only once all five gates
	/// agree, and which one is refusing is what <c>--trace</c>'s <c>aim</c> field
	/// reports.
	/// </summary>
	public string AttackTank(int index)
	{
		if (_vehicles.Count == 0)
			return "no tanks loaded";
		if (index < 0 || index >= _vehicles.Count)
			return $"no tank {index}; there are {_vehicles.Count}";
		if (_vehicles[index] == Active)
			return "a tank is not its own target";
		Engage(Active, _vehicles[index]);
		return $"{Active.Tag} engaging {_vehicles[index].Tag}";
	}

	/// <summary>Destroy a tank outright - the middle-click on it.</summary>
	public string DestroyTank(int index)
	{
		if (index < 0 || index >= _vehicles.Count)
			return $"no tank {index}; there are {_vehicles.Count}";
		Kill(_vehicles[index]);
		return $"{_vehicles[index].Tag} destroyed";
	}

	/// <summary>Put the board back - <c>R</c>.</summary>
	public string ResetBoard()
	{
		ResetAll();
		return "reset";
	}
}
