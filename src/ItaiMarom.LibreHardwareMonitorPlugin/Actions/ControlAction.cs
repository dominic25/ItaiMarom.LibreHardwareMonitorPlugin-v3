using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Actions;

/// <summary>
/// What the plugin's fan-control actions share: one parameter that picks a controllable sensor, an
/// option list built from the hardware on demand, and an executor that turns a refusal into a result.
/// </summary>
/// <remarks>
/// <para>
/// Constructing an action must not touch hardware. Macro Deck reads
/// <see cref="LibreHardwareMonitorIntegration.Actions"/> before it initializes the integration and
/// expects a complete, side-effect-free list, so the control list is fetched only when the action editor
/// asks for options.
/// </para>
/// <para>
/// The monitor is the only writer: a control goes through the same semaphore the polling loop holds, so
/// a write can never catch the hardware library in the middle of an update.
/// </para>
/// </remarks>
internal abstract class ControlAction : IActionDefinition, IDynamicOptionsActionDefinition
{
	/// <summary>The name every fan-control action gives its sensor parameter.</summary>
	internal const string SensorParameterName = "sensor";

	protected ControlAction(HardwareMonitor monitor, ILogger logger)
	{
		Monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
		Logger = (logger ?? throw new ArgumentNullException(nameof(logger))).ForContext(GetType());
	}

	protected HardwareMonitor Monitor { get; }

	protected ILogger Logger { get; }

	/// <inheritdoc />
	public abstract string Id { get; }

	/// <inheritdoc />
	public abstract LocalizedText Name { get; }

	/// <inheritdoc />
	public abstract LocalizedText Description { get; }

	/// <inheritdoc />
	public abstract IReadOnlyList<ActionParameter> Parameters { get; }

	/// <inheritdoc />
	public MacroDeckPlatform Platforms => MacroDeckPlatform.All;

	/// <inheritdoc />
	public string? Role => null;

	/// <inheritdoc />
	public IActionExecutor CreateExecutor() => new Executor(this);

	/// <inheritdoc />
	/// <remarks>
	/// An empty list is explained rather than left blank: a process that cannot open the hardware library's
	/// driver cannot see a control at all, and "no fans" is then a fact about the process, not about the
	/// machine.
	/// </remarks>
	public async Task<DynamicOptionsResult> GetDynamicOptionsAsync(
		DynamicOptionsContext context,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);

		if (!string.Equals(context.ParameterName, SensorParameterName, StringComparison.Ordinal))
		{
			return new DynamicOptionsResult { Options = [] };
		}

		IReadOnlyList<SensorIdentifier> controls;
		try
		{
			controls = await Monitor.EnumerateControlsAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			Logger.Warning(exception, "The controllable sensors could not be enumerated.");
			return new DynamicOptionsResult { Options = [], Error = Strings.Actions.ControlsUnavailable() };
		}

		var options = controls
			.Where(sensor => sensor.Matches(context.Filter))
			.Select(sensor => new ActionParameterOption
			{
				Value = sensor.Id,
				Label = LocalizedText.FromLiteral(Label(sensor)),
			})
			.ToArray();

		return options.Length == 0 && controls.Count == 0
			? new DynamicOptionsResult { Options = [], Error = NoControlsError() }
			: new DynamicOptionsResult { Options = options };
	}

	/// <summary>The action-specific half of a run, with the sensor name already read off the parameters.</summary>
	protected abstract Task<ActionResult> ExecuteCoreAsync(string sensorId, ActionExecutionContext context);

	/// <summary>The parameter both actions open with: the fan or pump to drive.</summary>
	protected static ActionParameter SensorChoice() =>
		ActionParameter.DynamicChoice(
				SensorParameterName,
				label: Strings.Actions.Sensor.Label(),
				description: Strings.Actions.Sensor.Description(),
				required: true)
			.AsSearchable();

	/// <summary>What a user sees in the picker: the sensor, then the device it sits on.</summary>
	private static string Label(SensorIdentifier sensor) =>
		$"{sensor.DisplayName} - {sensor.Hardware.Path}";

	/// <summary>
	/// Why the picker is empty. The monitor already worked out whether this process can open the hardware
	/// library's driver, so the explanation follows that answer instead of blaming elevation on a machine
	/// where the driver is simply not installed.
	/// </summary>
	private LocalizedText NoControlsError() =>
		Monitor.AccessNote is null
			? Strings.Actions.NoControls()
			: Strings.Actions.NoControlsBlocked();

	/// <summary>
	/// The executor both actions hand out. It has no state of its own: the definition is the lifetime,
	/// because the host may create an executor per run.
	/// </summary>
	private sealed class Executor(ControlAction action) : IActionExecutor
	{
		public async Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			ArgumentNullException.ThrowIfNull(context);

			if (context.Parameters.GetValueOrDefault(SensorParameterName) is not string sensorId
				|| string.IsNullOrWhiteSpace(sensorId))
			{
				return ActionResult.Failed(ActionErrorCodes.InvalidParameter, Strings.Actions.NoSensorSelected());
			}

			try
			{
				return await action.ExecuteCoreAsync(sensorId, context).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
			{
				// The flow was aborted. Let the engine record the run it actually had.
				throw;
			}
			catch (Exception exception)
			{
				action.Logger.Error(exception, "Changing the control of {SensorId} failed.", sensorId);
				return ActionResult.Failed(ActionErrorCodes.ProviderError, Strings.Actions.ControlFailed());
			}
		}
	}
}
