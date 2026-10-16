using System.Diagnostics;
using ItaiMarom.LibreHardwareMonitorPlugin;
using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using ItaiMarom.LibreHardwareMonitorPlugin.Variables;
using LibreHardwareMonitor.Hardware;
using MacroDeck.Plugin.Protocol.Capabilities.Variables;
using MacroDeck.Plugin.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// The variable surface as the host sees it: the same <c>describe</c>/<c>get</c>/<c>discover</c>/
/// <c>resolve</c>/<c>subscribe</c> operations the WebSocket carries, driven through the in-process
/// harness. Nothing here touches real hardware.
/// </summary>
[TestFixture]
public sealed class VariableProviderProtocolTests
{
	private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

	[Test]
	public async Task The_host_describes_and_reads_a_declared_variable()
	{
		var source = new FakeHardwareSensorSource().Add("CPU", "CPU Total", SensorType.Load, 42f);
		var cpu = source.Sensors[0];
		var selection = Select(cpu);

		await using var harness = CreateHarness(source, selection);
		await harness.InitializeIntegrationsAsync();

		var described = (await harness.Variables.DescribeAsync()).DataAs<VariableCatalogPayload>();

		Assert.That(described, Is.Not.Null);
		Assert.That(described!.SupportsCatalog, Is.True);
		Assert.That(described.SupportsSearch, Is.True);

		// The plugin reads the hardware on its own schedule, so the host parks the values and never pushes:
		// a button is redrawn on the host's refresh interval, not on a SMART query finishing.
		Assert.That(described.SupportsPush, Is.False);
		Assert.That(described.CatalogEntryCount, Is.Null);

		// The old name is what the layout references; there is nothing to declare until a configuration has
		// produced a selection, so the host is told to ask again after one changes.
		Assert.That(described.VariablesDependOnConfiguration, Is.True);
		Assert.That(
			described.Variables.Select(variable => variable.Name),
			Is.EqualTo(new List<string> { "cpu_total_load" }));

		// The identifier a layout stores is the declared id, not the name - and the name the old plugin
		// produced has no derivable id, which is the one detail that makes this work at all.
		await WaitUntilAsync(async () =>
		{
			var reading = (await harness.Variables.GetAsync(cpu.VariableId)).DataAs<VariableReadingDto>();
			return reading?.Value?.Number == 42d;
		});
	}

	[Test]
	public async Task The_host_can_browse_the_catalog_and_bind_what_it_finds()
	{
		var source = new FakeHardwareSensorSource()
			.Add("CPU", "CPU Total", SensorType.Load, 42f);
		var boardFan = TestSensor.Create("Motherboard", "Fan #1", SensorType.Fan, subHardwareName: "ITE IT8688E");
		source.Add(boardFan, 1200f);
		var selection = Select(source.Sensors[0]);

		await using var harness = CreateHarness(source, selection);
		await harness.InitializeIntegrationsAsync();

		var roots = (await harness.Variables.DiscoverAsync()).DataAs<VariableCatalogPageResult>();

		Assert.That(roots, Is.Not.Null);
		Assert.That(
			roots!.Items.Select(item => item.Name),
			Is.EqualTo(new List<string> { "cpu", "motherboard" }));
		Assert.That(roots.Items, Has.All.Matches<VariableDefinitionDto>(item => item.IsContainer));

		var motherboard = roots.Items.Single(item => item.Name == "motherboard");
		var children = (await harness.Variables.DiscoverAsync(parentId: motherboard.Id)).DataAs<VariableCatalogPageResult>();

		Assert.That(children, Is.Not.Null);
		var fan = children!.Items.Single();

		// A catalog entry is reached by its library identifier: the fan is on a super-I/O chip the old
		// plugin never listed, and its own name would be ambiguous with the motherboard's own fan header.
		Assert.That(fan.Id, Is.EqualTo(boardFan.ResourceId));
		Assert.That(fan.ParentId, Is.EqualTo(motherboard.Id));
		Assert.That(fan.Unit, Is.EqualTo("RPM"));
		Assert.That(fan.IsBindable, Is.True);

		var resolved = (await harness.Variables.ResolveAsync(fan.Id)).DataAs<VariableResolveResult>();

		Assert.That(resolved?.Definition, Is.Not.Null);
		Assert.That(resolved!.Definition!.Id, Is.EqualTo(fan.Id));

		// Binding is what makes a resource read: subscribe replaces the working set and the plugin tells the
		// polling loop to pick the fan up.
		Assert.That((await harness.Variables.SubscribeAsync([fan.Id])).Succeeded, Is.True);

		await WaitUntilAsync(async () =>
		{
			var reading = (await harness.Variables.GetAsync(fan.Id)).DataAs<VariableReadingDto>();
			return reading?.Value?.Number == 1200d;
		});
	}

	[Test]
	public async Task A_resource_that_is_not_bound_reads_as_unavailable()
	{
		var source = new FakeHardwareSensorSource()
			.Add("CPU", "CPU Total", SensorType.Load, 42f)
			.Add("GPU", "GPU Core", SensorType.Temperature, 60f);
		var gpu = source.Sensors[1];
		var selection = Select(source.Sensors[0]);

		await using var harness = CreateHarness(source, selection);
		await harness.InitializeIntegrationsAsync();

		// The catalog was never walked, so the plugin has never heard of this resource. An eager variable
		// read from a profile that was saved on another machine must answer "not available" rather than
		// making the host wait on hardware enumeration.
		var outcome = await harness.Variables.GetAsync(gpu.ResourceId);

		Assert.That(outcome.Succeeded, Is.True);
		Assert.That(outcome.DataAs<VariableReadingDto>()?.Value?.Number, Is.Null);
	}

	[Test]
	public async Task An_id_that_is_not_a_resource_id_resolves_to_nothing()
	{
		var selection = new SensorSelection();

		await using var harness = CreateHarness(new FakeHardwareSensorSource(), selection);
		await harness.InitializeIntegrationsAsync();

		// A plugin with nothing selected declares nothing, and the host still has to be able to ask it
		// whether it owns an id - and to be told "no" rather than "yes, but there is no value".
		var outcome = await harness.Variables.ResolveAsync("a::b");

		Assert.That(outcome.Succeeded, Is.True);
		Assert.That(outcome.DataAs<VariableResolveResult>()?.Definition, Is.Null);
	}

	private static SensorSelection Select(params SensorIdentifier[] sensors)
	{
		var selection = new SensorSelection();
		selection.Replace(sensors);
		return selection;
	}

	private static PluginTestHarness CreateHarness(FakeHardwareSensorSource source, SensorSelection selection) =>
		PluginTestHarness.Create(builder => builder
			.ConfigureServices((_, services) =>
			{
				services.AddSingleton<IHardwareSensorSource>(source);
				services.AddSingleton(selection);
			})
			.RegisterIntegration<LibreHardwareMonitorIntegration>());

	private static async Task WaitUntilAsync(Func<Task<bool>> condition)
	{
		var deadline = Stopwatch.StartNew();
		while (deadline.Elapsed < Patience)
		{
			if (await condition())
			{
				return;
			}

			await Task.Delay(20);
		}

		Assert.Fail($"The condition was still false after {Patience}.");
	}
}
