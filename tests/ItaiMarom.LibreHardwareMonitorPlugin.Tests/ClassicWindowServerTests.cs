using System.Net;
using ItaiMarom.LibreHardwareMonitorPlugin.Configuration;
using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using LibreHardwareMonitor.Hardware;
using NUnit.Framework;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// The classic sensor window as its two halves actually meet: the page the plugin serves a browser, and
/// the selection the browser posts back before it is sent on to the host's callback.
/// </summary>
/// <remarks>
/// The window is the only part of the plugin that is reached over the network, so it is tested over the
/// network - a real loopback connection against a real listener - rather than by calling the pieces. What
/// is being checked is that the page a user would see carries the tree the Macro Deck 2 dialog had, and
/// that what a browser posts is what the flow reads back.
/// </remarks>
[TestFixture]
public sealed class ClassicWindowServerTests
{
	private const string RedirectUri = "https://localhost:5000/api/integrations/oauth/callback";
	private const string State = "state-123";

	private static readonly SensorIdentifier CpuCore1 = TestSensor.Create("CPU", "CPU Core #1", SensorType.Temperature);
	private static readonly SensorIdentifier CpuCore2 = TestSensor.Create("CPU", "CPU Core #2", SensorType.Temperature);
	private static readonly SensorIdentifier CpuTotal = TestSensor.Create("CPU", "CPU Total", SensorType.Load);
	private static readonly SensorIdentifier GpuCore = TestSensor.Create("GPU", "GPU Core", SensorType.Temperature);

	private ClassicWindowServer _server = null!;

	[SetUp]
	public void SetUp() => _server = new ClassicWindowServer(new LoggerConfiguration().CreateLogger());

	[TearDown]
	public async Task TearDown() => await _server.DisposeAsync();

	[Test]
	public async Task The_window_serves_the_old_dialog_s_tree_opened_on_the_seeded_sensors()
	{
		var opened = Open([CpuCore1, CpuCore2, CpuTotal, GpuCore], CpuCore1);

		var page = await WindowClient.ReadPageAsync(opened.PageUri);

		// The whole document, not a fragment the browser would have to be coaxed into forgiving.
		Assert.That(page, Does.StartWith("<!DOCTYPE html>"));
		Assert.That(page, Does.EndWith("</html>"));

		// Three levels: the hardware, the reading kind under it, the sensors under that.
		Assert.That(page, Does.Contain("<span class=\"name root\">CPU</span>"));
		Assert.That(page, Does.Contain("<span class=\"name root\">GPU</span>"));
		Assert.That(page, Does.Contain("<span class=\"name kind\">Temperature</span>"));
		Assert.That(page, Does.Contain("<span class=\"name kind\">Load</span>"));

		// One branch per hardware, whatever the number of reading kinds inside it.
		Assert.That(Occurrences(page, "<span class=\"name root\">"), Is.EqualTo(2));

		// Every sensor is a tick box that submits under "sensors", and the seeded one is already ticked.
		Assert.That(page, Does.Contain("name=\"sensors\""));
		Assert.That(page, Does.Contain($"value=\"{CpuCore1.ResourceId}\" checked"));
		Assert.That(page, Does.Contain($"value=\"{CpuCore2.ResourceId}\">"));

		// The leaves are labelled the way the old plugin labelled them - the sensor, then its kind -
		// because that text is the one it also built the variable's name from.
		Assert.That(page, Does.Contain("<span>CPU Core #1_Temperature</span>"));
		Assert.That(page, Does.Contain("<span>CPU Total_Load</span>"));
		Assert.That(page, Does.Contain("<span>GPU Core_Temperature</span>"));

		// The window is its own form target and the interval it opens on is the configured one.
		Assert.That(page, Does.Contain($"action=\"/?s={opened.Key}\""));
		Assert.That(page, Does.Contain(">Polling rate</label>"));
		Assert.That(page, Does.Contain("value=\"1000\""));

		// Nothing is left of the markers the page is written with, so every piece of it was filled in.
		Assert.That(page, Does.Not.Contain("__"));
	}

	[Test]
	public async Task The_tree_keeps_the_order_the_hardware_reported()
	{
		// Reported in an order that is neither the alphabetical one nor the one a stable sort by name
		// would produce, so a tree that sorted would fail every assertion below.
		var opened = Open([GpuCore, CpuCore2, CpuCore1, CpuTotal], CpuCore1);

		var page = await WindowClient.ReadPageAsync(opened.PageUri);

		var gpu = page.IndexOf("name root\">GPU", StringComparison.Ordinal);
		var cpu = page.IndexOf("name root\">CPU", StringComparison.Ordinal);
		var second = page.IndexOf("CPU Core #2_Temperature", StringComparison.Ordinal);
		var first = page.IndexOf("CPU Core #1_Temperature", StringComparison.Ordinal);
		var total = page.IndexOf("CPU Total_Load", StringComparison.Ordinal);

		Assert.That(gpu, Is.GreaterThanOrEqualTo(0), "The GPU's branch is missing.");
		Assert.That(gpu, Is.LessThan(cpu), "The GPU was reported before the CPU.");
		Assert.That(second, Is.LessThan(first), "The cores were reported #2 before #1.");
		Assert.That(cpu, Is.LessThan(total), "The CPU's temperatures were reported before its load.");
	}

	[Test]
	public async Task A_window_serves_the_sensor_names_escaped()
	{
		var awkward = TestSensor.Create("CPU", "Fan <1> & \"2\"", SensorType.Fan);

		var page = await WindowClient.ReadPageAsync(Open([awkward], awkward).PageUri);

		Assert.That(page, Does.Contain("Fan &lt;1&gt; &amp; &quot;2&quot;_Fan"));
		Assert.That(page, Does.Not.Contain("Fan <1>"));
	}

	[Test]
	public async Task Saving_the_window_sends_the_browser_to_the_callback_with_the_key()
	{
		var opened = Open([CpuCore1, CpuCore2], CpuCore1);

		var location = await WindowClient.SaveAsync(opened.PageUri, [CpuCore2.ResourceId], 2500);

		Assert.That(WindowClient.ParameterOf(location, "state"), Is.EqualTo(State));
		Assert.That(WindowClient.ParameterOf(location, "code"), Is.EqualTo(opened.Key));

		Assert.That(_server.TryTakePayload(opened.Key, out var payload), Is.True);
		Assert.That(payload!.ResourceIds, Is.EqualTo(new[] { CpuCore2.ResourceId }));
		Assert.That(payload.PollingMilliseconds, Is.EqualTo(2500));
	}

	[Test]
	public async Task A_selection_is_handed_over_once()
	{
		var opened = Open([CpuCore1], CpuCore1);
		await WindowClient.SaveAsync(opened.PageUri, [CpuCore1.ResourceId], 1000);

		Assert.That(_server.TryTakePayload(opened.Key, out _), Is.True);
		Assert.That(_server.TryTakePayload(opened.Key, out _), Is.False);
	}

	[Test]
	public async Task Cancelling_the_window_goes_back_without_a_selection()
	{
		var opened = Open([CpuCore1], CpuCore1);

		var location = await WindowClient.CancelAsync(opened.PageUri);

		Assert.That(WindowClient.ParameterOf(location, "state"), Is.EqualTo(State));
		Assert.That(WindowClient.ParameterOf(location, "error"), Is.EqualTo("User cancelled"));
		Assert.That(location, Does.Not.Contain("code="));
		Assert.That(_server.TryTakePayload(opened.Key, out _), Is.False);
	}

	[Test]
	public async Task A_key_no_window_is_open_for_is_not_served()
	{
		var opened = Open([CpuCore1], CpuCore1);
		var stranger = new Uri(opened.PageUri, $"?s={new string('0', 32)}");

		Assert.That(await WindowClient.ReadPageStatusAsync(stranger), Is.EqualTo(HttpStatusCode.NotFound));
	}

	[Test]
	public async Task A_key_no_window_is_open_for_is_not_saved_against()
	{
		var opened = Open([CpuCore1], CpuCore1);
		var stranger = new Uri(opened.PageUri, $"?s={new string('0', 32)}");

		Assert.That(
			await WindowClient.PostAsync(stranger, [new KeyValuePair<string, string>("sensors", CpuCore1.ResourceId)]),
			Is.EqualTo(HttpStatusCode.NotFound));

		Assert.That(_server.TryTakePayload(opened.Key, out _), Is.False);
	}

	[Test]
	public void A_key_no_window_ever_used_holds_no_selection() =>
		Assert.That(_server.TryTakePayload(new string('0', 32), out _), Is.False);

	[Test]
	public async Task A_window_says_which_readings_the_driver_could_not_produce()
	{
		var note = "Fan speeds <and> voltages need a driver Windows only lets an administrator open.";

		var page = await WindowClient.ReadPageAsync(OpenWithNotice(note, [CpuCore1], CpuCore1).PageUri);

		Assert.That(page, Does.Contain("<p class=\"notice\">"));
		Assert.That(page, Does.Contain("Fan speeds &lt;and&gt; voltages need a driver"));
		Assert.That(page, Does.Not.Contain("<and>"));
		Assert.That(page, Does.Not.Contain("__"));
	}

	[Test]
	public async Task A_window_with_nothing_to_report_carries_no_notice()
	{
		var page = await WindowClient.ReadPageAsync(Open([CpuCore1], CpuCore1).PageUri);

		Assert.That(page, Does.Not.Contain("class=\"notice\""));
	}

	private ClassicWindowServer.Opened Open(
		IReadOnlyCollection<SensorIdentifier> reported,
		params SensorIdentifier[] selected) =>
		OpenWithNotice(null, reported, selected);

	private ClassicWindowServer.Opened OpenWithNotice(
		string? notice,
		IReadOnlyCollection<SensorIdentifier> reported,
		params SensorIdentifier[] selected) =>
		_server.Open(new ClassicWindowOpening(
			"Libre Hardware Monitor",
			SensorCategory.Build(reported),
			selected.Select(sensor => sensor.ResourceId).ToArray(),
			1000,
			10,
			10000,
			RedirectUri,
			State,
			notice));

	private static int Occurrences(string text, string value)
	{
		var count = 0;
		for (var index = text.IndexOf(value, StringComparison.Ordinal);
			 index >= 0;
			 index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
		{
			count++;
		}

		return count;
	}
}
