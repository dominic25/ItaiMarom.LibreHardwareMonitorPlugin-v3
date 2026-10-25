using System.Globalization;
using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Actions;

/// <summary>
/// Sets a fan or pump to a percentage of its range. The value the slider offers is a percentage; the
/// control itself may report a different range, so the source clamps to that before writing.
/// </summary>
internal sealed class SetFanSpeedAction : ControlAction
{
	private const string PercentParameterName = "percent";

	public SetFanSpeedAction(HardwareMonitor monitor, ILogger logger)
		: base(monitor, logger)
	{
		Parameters =
		[
			SensorChoice(),
			ActionParameter.Slider(
				PercentParameterName,
				0,
				100,
				label: Strings.Actions.SetFanSpeed.Percent.Label(),
				description: Strings.Actions.SetFanSpeed.Percent.Description(),
				step: 1,
				defaultValue: 50),
		];
	}

	/// <inheritdoc />
	public override string Id => "set-fan-speed";

	/// <inheritdoc />
	public override LocalizedText Name => Strings.Actions.SetFanSpeed.Name();

	/// <inheritdoc />
	public override LocalizedText Description => Strings.Actions.SetFanSpeed.Description();

	/// <inheritdoc />
	public override IReadOnlyList<ActionParameter> Parameters { get; }

	/// <inheritdoc />
	protected override async Task<ActionResult> ExecuteCoreAsync(string sensorId, ActionExecutionContext context)
	{
		if (!TryReadPercent(context.Parameters, out var percent))
		{
			return ActionResult.Failed(
				ActionErrorCodes.InvalidParameter,
				Strings.Actions.SetFanSpeed.InvalidPercent());
		}

		if (!await Monitor.SetControlAsync(sensorId, percent, context.CancellationToken).ConfigureAwait(false))
		{
			return ActionResult.Failed(ActionErrorCodes.NotFound, Strings.Actions.SensorGone());
		}

		Logger.Information("Set {SensorId} to {Percent}%.", sensorId, percent);
		return ActionResult.Success();
	}

	/// <summary>
	/// Reads the slider's value however the host serialized it - a number, a string, or nothing at all -
	/// and refuses anything that is not a finite number.
	/// </summary>
	private static bool TryReadPercent(IReadOnlyDictionary<string, object> parameters, out double percent)
	{
		percent = 0;

		if (parameters.GetValueOrDefault(PercentParameterName) is not { } raw)
		{
			return false;
		}

		try
		{
			percent = raw switch
			{
				double number => number,
				float number => number,
				int number => number,
				long number => number,
				decimal number => (double)number,
				_ => Convert.ToDouble(raw.ToString(), CultureInfo.InvariantCulture),
			};
		}
		catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
		{
			return false;
		}

		return double.IsFinite(percent);
	}
}
