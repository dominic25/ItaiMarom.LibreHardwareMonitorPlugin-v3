using ItaiMarom.LibreHardwareMonitorPlugin.Actions;
using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using MacroDeck.Sdk.Actions;
using NUnit.Framework;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// The fan-control actions as the host drives them: describe, list the fans, execute, and report what
/// happened. Driven through <see cref="FakeHardwareSensorSource"/> so nothing here touches real hardware.
/// </summary>
[TestFixture]
public sealed class FanControlActionTests
{
	private static readonly SensorIdentifier Fan = TestFanActions.Control("Fan #1");

	private static readonly SensorIdentifier SecondFan = TestFanActions.Control("Fan #2");

	private static readonly string[] SetFanSpeedParameterNames = [ControlAction.SensorParameterName, "percent"];

	[Test]
	public async Task Setting_a_speed_writes_it_and_succeeds()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var source = new FakeHardwareSensorSource().AddControl(Fan);
		await using var monitor = new HardwareMonitor(source, HardwareMonitorOptions.Default, logger);
		var action = new SetFanSpeedAction(monitor, logger);

		var result = await action.CreateExecutor().ExecuteAsync(
			Context((ControlAction.SensorParameterName, Fan.Id), ("percent", 40d)));

		Assert.That(result.Status, Is.EqualTo(ActionResultStatus.Succeeded));
		Assert.That(source.LastSetSensorId, Is.EqualTo(Fan.Id));
		Assert.That(source.LastSetValue, Is.EqualTo(40f).Within(0.001f));
	}

	[Test]
	public async Task A_slider_value_that_arrived_as_text_still_works()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var source = new FakeHardwareSensorSource().AddControl(Fan);
		await using var monitor = new HardwareMonitor(source, HardwareMonitorOptions.Default, logger);
		var action = new SetFanSpeedAction(monitor, logger);

		// A client that serializes the slider as a string is not a reason to refuse the click.
		var result = await action.CreateExecutor().ExecuteAsync(
			Context((ControlAction.SensorParameterName, Fan.Id), ("percent", "25")));

		Assert.That(result.Status, Is.EqualTo(ActionResultStatus.Succeeded));
		Assert.That(source.LastSetValue, Is.EqualTo(25f).Within(0.001f));
	}

	[Test]
	public async Task Setting_a_speed_without_a_fan_is_an_invalid_parameter()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var source = new FakeHardwareSensorSource().AddControl(Fan);
		await using var monitor = new HardwareMonitor(source, HardwareMonitorOptions.Default, logger);
		var action = new SetFanSpeedAction(monitor, logger);

		var result = await action.CreateExecutor().ExecuteAsync(Context(("percent", 40d)));

		Assert.That(result.Status, Is.EqualTo(ActionResultStatus.Failed));
		Assert.That(result.ErrorCode, Is.EqualTo(ActionErrorCodes.InvalidParameter));
		Assert.That(source.LastSetSensorId, Is.Null);
	}

	[Test]
	public async Task Setting_a_speed_that_is_not_a_number_is_an_invalid_parameter()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var source = new FakeHardwareSensorSource().AddControl(Fan);
		await using var monitor = new HardwareMonitor(source, HardwareMonitorOptions.Default, logger);
		var action = new SetFanSpeedAction(monitor, logger);

		var result = await action.CreateExecutor().ExecuteAsync(
			Context((ControlAction.SensorParameterName, Fan.Id), ("percent", "full")));

		Assert.That(result.Status, Is.EqualTo(ActionResultStatus.Failed));
		Assert.That(result.ErrorCode, Is.EqualTo(ActionErrorCodes.InvalidParameter));
		Assert.That(source.LastSetSensorId, Is.Null);
	}

	[Test]
	public async Task Setting_a_speed_on_a_fan_that_is_gone_is_not_found()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var source = new FakeHardwareSensorSource();
		await using var monitor = new HardwareMonitor(source, HardwareMonitorOptions.Default, logger);
		var action = new SetFanSpeedAction(monitor, logger);

		// The profile still holds the id; the fan it named does not exist on this machine.
		var result = await action.CreateExecutor().ExecuteAsync(
			Context((ControlAction.SensorParameterName, Fan.Id), ("percent", 40d)));

		Assert.That(result.Status, Is.EqualTo(ActionResultStatus.Failed));
		Assert.That(result.ErrorCode, Is.EqualTo(ActionErrorCodes.NotFound));
	}

	[Test]
	public async Task The_action_hands_the_request_to_the_control_which_applies_its_own_range()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var source = new FakeHardwareSensorSource().AddControl(Fan, min: 20f, max: 80f);
		await using var monitor = new HardwareMonitor(source, HardwareMonitorOptions.Default, logger);
		var action = new SetFanSpeedAction(monitor, logger);

		// The slider always offers 0-100; the chip decides what it accepts, so the action passes the request
		// through rather than clamping it a second time. The fake applies the source's rule.
		var result = await action.CreateExecutor().ExecuteAsync(
			Context((ControlAction.SensorParameterName, Fan.Id), ("percent", 5d)));

		Assert.That(result.Status, Is.EqualTo(ActionResultStatus.Succeeded));
		Assert.That(source.LastSetValue, Is.EqualTo(20f).Within(0.001f));
	}

	[Test]
	public async Task Restoring_control_hands_the_fan_back_to_the_hardware()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var source = new FakeHardwareSensorSource().AddControl(Fan);
		await using var monitor = new HardwareMonitor(source, HardwareMonitorOptions.Default, logger);
		var action = new RestoreFanControlAction(monitor, logger);

		var result = await action.CreateExecutor().ExecuteAsync(Context((ControlAction.SensorParameterName, Fan.Id)));

		Assert.That(result.Status, Is.EqualTo(ActionResultStatus.Succeeded));
		Assert.That(source.LastResetSensorId, Is.EqualTo(Fan.Id));
	}

	[Test]
	public async Task The_fan_picker_lists_the_controls_the_hardware_reports()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var fans = new FakeHardwareSensorSource()
			.AddControl(Fan)
			.AddControl(SecondFan);
		await using var monitor = new HardwareMonitor(fans, HardwareMonitorOptions.Default, logger);
		var action = new SetFanSpeedAction(monitor, logger);

		var result = await action.GetDynamicOptionsAsync(OptionsContext(), CancellationToken.None);

		Assert.That(result.Error.IsEmpty, Is.True);
		Assert.That(result.Options.Select(option => option.Value), Is.EqualTo(new[] { Fan.Id, SecondFan.Id }));
		Assert.That(result.Options, Has.All.Matches<ActionParameterOption>(option => !option.Label.IsEmpty));
	}

	[Test]
	public async Task The_fan_picker_can_be_filtered()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var source = new FakeHardwareSensorSource().AddControl(Fan).AddControl(SecondFan);
		await using var monitor = new HardwareMonitor(source, HardwareMonitorOptions.Default, logger);
		var action = new SetFanSpeedAction(monitor, logger);

		var result = await action.GetDynamicOptionsAsync(OptionsContext("fan #2"), CancellationToken.None);

		Assert.That(result.Options.Select(option => option.Value), Is.EqualTo(new[] { SecondFan.Id }));
	}

	[Test]
	public async Task An_empty_control_list_is_explained()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var source = new FakeHardwareSensorSource();
		await using var monitor = new HardwareMonitor(source, HardwareMonitorOptions.Default, logger);
		var action = new SetFanSpeedAction(monitor, logger);

		// The ordinary non-elevated case: the driver never loaded, so there is nothing to offer - and the
		// editor has to say why rather than show an unexplained empty list.
		var result = await action.GetDynamicOptionsAsync(OptionsContext(), CancellationToken.None);

		Assert.That(result.Options, Is.Empty);
		Assert.That(result.Error.IsEmpty, Is.False);
	}

	[Test]
	public async Task Both_actions_describe_the_sensor_parameter_the_executor_reads()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var source = new FakeHardwareSensorSource();
		await using var monitor = new HardwareMonitor(source, HardwareMonitorOptions.Default, logger);

		IActionDefinition[] actions = [new SetFanSpeedAction(monitor, logger), new RestoreFanControlAction(monitor, logger)];

		Assert.That(actions.Select(action => action.Id), Is.EqualTo(TestFanActions.Ids));
		Assert.That(actions, Has.All.Matches<IActionDefinition>(
			action => action.Parameters.Any(
				parameter => parameter.Name == ControlAction.SensorParameterName && parameter.Required)));
		Assert.That(actions[0].Parameters.Select(parameter => parameter.Name), Is.EqualTo(SetFanSpeedParameterNames));
	}

	private static DynamicOptionsContext OptionsContext(string filter = "") => new()
	{
		ParameterName = ControlAction.SensorParameterName,
		Filter = filter,
		CurrentParameters = new Dictionary<string, object?>(),
	};

	private static ActionExecutionContext Context(params (string Name, object Value)[] parameters) => new()
	{
		Parameters = parameters.ToDictionary(parameter => parameter.Name, parameter => parameter.Value),
	};
}
