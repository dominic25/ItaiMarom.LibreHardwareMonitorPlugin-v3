using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Actions;

/// <summary>
/// Hands a fan or pump back to the motherboard's own curve. This is the undo for
/// <see cref="SetFanSpeedAction"/>: without it a speed set once would keep applying until the machine
/// restarts, because a software control outlives the plugin that set it.
/// </summary>
internal sealed class RestoreFanControlAction : ControlAction
{
	public RestoreFanControlAction(HardwareMonitor monitor, ILogger logger)
		: base(monitor, logger)
	{
		Parameters = [SensorChoice()];
	}

	/// <inheritdoc />
	public override string Id => "restore-fan-control";

	/// <inheritdoc />
	public override LocalizedText Name => Strings.Actions.RestoreFanControl.Name();

	/// <inheritdoc />
	public override LocalizedText Description => Strings.Actions.RestoreFanControl.Description();

	/// <inheritdoc />
	public override IReadOnlyList<ActionParameter> Parameters { get; }

	/// <inheritdoc />
	protected override async Task<ActionResult> ExecuteCoreAsync(string sensorId, ActionExecutionContext context)
	{
		if (!await Monitor.ResetControlAsync(sensorId, context.CancellationToken).ConfigureAwait(false))
		{
			return ActionResult.Failed(ActionErrorCodes.NotFound, Strings.Actions.SensorGone());
		}

		Logger.Information("Returned {SensorId} to the hardware's own control.", sensorId);
		return ActionResult.Success();
	}
}
