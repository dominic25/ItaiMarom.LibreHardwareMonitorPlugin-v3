using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.Events;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Events;

/// <summary>
/// The events this plugin offers to Macro Deck's automation editor.
/// </summary>
/// <remarks>
/// They are the things a sensor plugin can know that a variable change cannot express: a threshold held
/// for a while, a value that clears with hysteresis, a sensor that stopped reporting at all, and a remote
/// machine that crossed between online and offline. Every definition is built without touching hardware,
/// because the host describes a plugin before it initializes it.
/// </remarks>
internal static class HardwareEvents
{
	/// <summary>
	/// Empty on purpose: the host then shows the integration's own name, which keeps the store plugin and
	/// the test plugin apart in the event picker.
	/// </summary>
	internal const string ProviderName = "";

	internal const string ThresholdId = "sensor-threshold";
	internal const string StoppedId = "sensor-stopped";
	internal const string RemoteOfflineId = "remote-offline";
	internal const string RemoteOnlineId = "remote-online";
	internal const string ElevationChangedId = "elevation-changed";

	internal const string SensorParameter = "sensor";
	internal const string MachineParameter = "machine";
	internal const string DirectionParameter = "direction";
	internal const string ThresholdParameter = "threshold";
	internal const string DwellParameter = "dwell";
	internal const string HysteresisParameter = "hysteresis";
	internal const string GraceParameter = "grace";

	internal const string DirectionAbove = "above";
	internal const string DirectionBelow = "below";

	/// <summary>
	/// The payload names, which are also the variables the automation can read afterwards. They are
	/// deliberately different from the configuration names where the values differ: the host compares a
	/// configuration parameter with a payload parameter of the same name, and the configuration stores a
	/// sensor's resource id while the payload carries the readable name.
	/// </summary>
	internal const string PayloadSensorName = "sensorName";
	internal const string PayloadSensorId = "sensorId";
	internal const string PayloadMachineName = "machineName";
	internal const string PayloadValue = "value";
	internal const string PayloadUnit = "unit";
	internal const string PayloadSeconds = "seconds";
	internal const string PayloadUrl = "url";
	internal const string PayloadError = "error";
	internal const string PayloadElevated = "elevated";

	internal static IReadOnlyList<EventDefinition> Definitions { get; } =
	[
		Threshold(),
		Stopped(),
		RemoteOffline(),
		RemoteOnline(),
		ElevationChanged(),
	];

	private static EventDefinition Threshold() => new()
	{
		Id = ThresholdId,
		Name = Strings.Events.Threshold.Name(),
		Description = Strings.Events.Threshold.Description(),
		Category = Strings.Events.Category(),
		DeliveryKind = EventDeliveryKind.Push,
		ConfigurationParameters =
		[
			SensorChoice(),
			ActionParameter.Choice(
				DirectionParameter,
				[
					new ActionParameterOption
					{
						Value = DirectionAbove,
						Label = Strings.Events.Threshold.Direction.Above(),
					},
					new ActionParameterOption
					{
						Value = DirectionBelow,
						Label = Strings.Events.Threshold.Direction.Below(),
					},
				],
				label: Strings.Events.Threshold.Direction.Label(),
				description: Strings.Events.Threshold.Direction.Description(),
				defaultValue: DirectionAbove,
				required: true),
			ActionParameter.Number(
				ThresholdParameter,
				label: Strings.Events.Threshold.Limit.Label(),
				description: Strings.Events.Threshold.Limit.Description(),
				step: 1,
				defaultValue: 80,
				required: true),
			ActionParameter.Number(
				DwellParameter,
				label: Strings.Events.Threshold.Dwell.Label(),
				description: Strings.Events.Threshold.Dwell.Description(),
				min: 0,
				max: 3600,
				step: 1,
				defaultValue: 10),
			ActionParameter.Number(
				HysteresisParameter,
				label: Strings.Events.Threshold.Hysteresis.Label(),
				description: Strings.Events.Threshold.Hysteresis.Description(),
				min: 0,
				step: 1,
				defaultValue: 2),
		],
		PayloadParameters =
		[
			ActionParameter.Text(PayloadSensorName, Strings.Events.Payload.Sensor.Label(), Strings.Events.Payload.Sensor.Description()),
			ActionParameter.Text(PayloadSensorId, Strings.Events.Payload.SensorId.Label(), Strings.Events.Payload.SensorId.Description()),
			ActionParameter.Number(PayloadValue, Strings.Events.Payload.Value.Label(), Strings.Events.Payload.Value.Description()),
			ActionParameter.Number(ThresholdParameter, Strings.Events.Payload.Threshold.Label(), Strings.Events.Payload.Threshold.Description()),
			ActionParameter.Text(PayloadUnit, Strings.Events.Payload.Unit.Label(), Strings.Events.Payload.Unit.Description()),
			ActionParameter.Text(PayloadMachineName, Strings.Events.Payload.Machine.Label(), Strings.Events.Payload.Machine.Description()),
		],
	};

	private static EventDefinition Stopped() => new()
	{
		Id = StoppedId,
		Name = Strings.Events.Stopped.Name(),
		Description = Strings.Events.Stopped.Description(),
		Category = Strings.Events.Category(),
		DeliveryKind = EventDeliveryKind.Push,
		ConfigurationParameters =
		[
			SensorChoice(),
			ActionParameter.Number(
				GraceParameter,
				label: Strings.Events.Stopped.Grace.Label(),
				description: Strings.Events.Stopped.Grace.Description(),
				min: 0,
				max: 86400,
				step: 1,
				defaultValue: 30),
		],
		PayloadParameters =
		[
			ActionParameter.Text(PayloadSensorName, Strings.Events.Payload.Sensor.Label(), Strings.Events.Payload.Sensor.Description()),
			ActionParameter.Text(PayloadSensorId, Strings.Events.Payload.SensorId.Label(), Strings.Events.Payload.SensorId.Description()),
			ActionParameter.Number(PayloadSeconds, Strings.Events.Payload.Seconds.Label(), Strings.Events.Payload.Seconds.Description()),
			ActionParameter.Text(PayloadMachineName, Strings.Events.Payload.Machine.Label(), Strings.Events.Payload.Machine.Description()),
		],
	};

	private static EventDefinition RemoteOffline() => new()
	{
		Id = RemoteOfflineId,
		Name = Strings.Events.RemoteOffline.Name(),
		Description = Strings.Events.RemoteOffline.Description(),
		Category = Strings.Events.Category(),
		DeliveryKind = EventDeliveryKind.Push,
		ConfigurationParameters = [MachineChoice()],
		PayloadParameters =
		[
			ActionParameter.Text(PayloadMachineName, Strings.Events.Payload.Machine.Label(), Strings.Events.Payload.Machine.Description()),
			ActionParameter.Text(PayloadUrl, Strings.Events.Payload.Url.Label(), Strings.Events.Payload.Url.Description()),
			ActionParameter.Text(PayloadError, Strings.Events.Payload.Error.Label(), Strings.Events.Payload.Error.Description()),
		],
	};

	private static EventDefinition RemoteOnline() => new()
	{
		Id = RemoteOnlineId,
		Name = Strings.Events.RemoteOnline.Name(),
		Description = Strings.Events.RemoteOnline.Description(),
		Category = Strings.Events.Category(),
		DeliveryKind = EventDeliveryKind.Push,
		ConfigurationParameters = [MachineChoice()],
		PayloadParameters =
		[
			ActionParameter.Text(PayloadMachineName, Strings.Events.Payload.Machine.Label(), Strings.Events.Payload.Machine.Description()),
			ActionParameter.Text(PayloadUrl, Strings.Events.Payload.Url.Label(), Strings.Events.Payload.Url.Description()),
		],
	};

	private static EventDefinition ElevationChanged() => new()
	{
		Id = ElevationChangedId,
		Name = Strings.Events.Elevation.Name(),
		Description = Strings.Events.Elevation.Description(),
		Category = Strings.Events.Category(),
		DeliveryKind = EventDeliveryKind.Push,
		ConfigurationParameters = [],
		PayloadParameters =
		[
			ActionParameter.Toggle(
				PayloadElevated,
				Strings.Events.Payload.Elevated.Label(),
				Strings.Events.Payload.Elevated.Description()),
		],
	};

	private static ActionParameter SensorChoice() =>
		ActionParameter.DynamicChoice(
				SensorParameter,
				label: Strings.Events.Sensor.Label(),
				description: Strings.Events.Sensor.Description(),
				optionsSourceId: "sensors",
				placeholder: Strings.Events.Sensor.Placeholder(),
				required: true)
			.AsSearchable();

	private static ActionParameter MachineChoice() =>
		ActionParameter.DynamicChoice(
				MachineParameter,
				label: Strings.Events.Machine.Label(),
				description: Strings.Events.Machine.Description(),
				optionsSourceId: "machines",
				placeholder: Strings.Events.Machine.Placeholder(),
				required: true)
			.AsSearchable();
}
