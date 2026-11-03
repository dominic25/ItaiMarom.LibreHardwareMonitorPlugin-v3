using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using LibreHardwareMonitor.Hardware;
using NUnit.Framework;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// Reading the tree LibreHardwareMonitor's Remote Web Server serves. The document has grouping nodes
/// (the root, the machine, and each reading-kind branch) mixed with hardware nodes and sensor leaves, so
/// the walk has to recognise what a node is from the fields it carries rather than from its depth.
/// </summary>
[TestFixture]
public sealed class RemoteSensorParserTests
{
	private static readonly RemoteSourceDefinition Source =
		new("Living Room", "http://living-room-pc:8085/data.json", "living_room");

	private static readonly string[] SettledRepeatedIds =
	[
		"/remote/living_room/gpu/load/3",
		"/remote/living_room/gpu/load/3-2",
	];

	private const string Document = """
		{
		  "Text": "Sensor",
		  "Children": [
		    {
		      "Text": "DESKTOP-ABC",
		      "HardwareId": "/",
		      "Children": [
		        {
		          "Text": "AMD Ryzen 7 5800X",
		          "HardwareId": "/amdcpu/0",
		          "Children": [
		            {
		              "Text": "Temperatures",
		              "Children": [
		                {
		                  "Text": "CPU Core #1",
		                  "SensorId": "/amdcpu/0/temperature/0",
		                  "Type": "Temperature",
		                  "RawValue": 51.0,
		                  "Value": "51.0 °C"
		                }
		              ]
		            }
		          ]
		        },
		        {
		          "Text": "Gigabyte X570",
		          "HardwareId": "/motherboard/0",
		          "Children": [
		            {
		              "Text": "Nuvoton NCT6798D",
		              "HardwareId": "/lpc/nct6798d/0",
		              "Children": [
		                {
		                  "Text": "Fan #1",
		                  "SensorId": "/lpc/nct6798d/0/fan/0",
		                  "Type": "Fan",
		                  "RawValue": 812.0,
		                  "Value": "812 RPM"
		                }
		              ]
		            }
		          ]
		        }
		      ]
		    }
		  ]
		}
		""";

	[Test]
	public void A_remote_sensor_is_namespaced_under_its_source_key()
	{
		var sensors = RemoteSensorParser.Parse(Document, Source);

		var cpu = sensors.Single(sensor => sensor.Sensor.SensorName == "CPU Core #1");

		Assert.That(cpu.Sensor.Id, Is.EqualTo("/remote/living_room/amdcpu/0/temperature/0"));
		Assert.That(cpu.Sensor.Hardware.Id, Is.EqualTo("/remote/living_room/amdcpu/0"));
		Assert.That(cpu.Sensor.Hardware.Source, Is.EqualTo("living_room"));
		Assert.That(cpu.Sensor.VariableName, Is.EqualTo("living_room_cpu_core_1_temperature"));
		Assert.That(cpu.Value, Is.EqualTo(51f));
	}

	[Test]
	public void A_remote_root_hardware_carries_the_source_label()
	{
		var sensors = RemoteSensorParser.Parse(Document, Source);
		var cpu = sensors.Single(sensor => sensor.Sensor.SensorName == "CPU Core #1");

		Assert.That(cpu.Sensor.Hardware.Name, Is.EqualTo("Living Room · AMD Ryzen 7 5800X"));
		Assert.That(cpu.Sensor.RootHardwareName, Is.EqualTo("Living Room · AMD Ryzen 7 5800X"));
	}

	[Test]
	public void A_sub_hardware_keeps_its_own_name_under_the_qualified_root()
	{
		var sensors = RemoteSensorParser.Parse(Document, Source);
		var fan = sensors.Single(sensor => sensor.Sensor.SensorName == "Fan #1");

		Assert.That(fan.Sensor.Hardware.Name, Is.EqualTo("Nuvoton NCT6798D"));
		Assert.That(fan.Sensor.Hardware.Path, Is.EqualTo("Living Room · Gigabyte X570/Nuvoton NCT6798D"));
		Assert.That(fan.Sensor.RootHardwareName, Is.EqualTo("Living Room · Gigabyte X570"));
	}

	[Test]
	public void The_document_root_and_the_machine_node_are_not_hardware()
	{
		var sensors = RemoteSensorParser.Parse(Document, Source);

		// The machine node carries "/" as its hardware id. Treating it as a device would make every
		// sensor's branch a child of a device the user never sees in LibreHardwareMonitor.
		Assert.That(sensors, Has.Count.EqualTo(2));
		Assert.That(sensors.Select(sensor => sensor.Sensor.Hardware.RootName).Distinct().Count(), Is.EqualTo(2));
	}

	[Test]
	public void A_raw_value_is_preferred_over_the_formatted_one()
	{
		var sensors = RemoteSensorParser.Parse(
			"""
			{"Children":[{"HardwareId":"/cpu","Text":"CPU","Children":[{"SensorId":"/cpu/load/0","Type":"Load","Text":"CPU Total","RawValue":42.5,"Value":"43 %"}]}]}
			""",
			Source);

		Assert.That(sensors.Single().Value, Is.EqualTo(42.5f));
	}

	[Test]
	public void A_raw_value_the_server_formats_as_text_is_read()
	{
		var sensors = RemoteSensorParser.Parse(
			"""
			{"Children":[{"HardwareId":"/cpu","Text":"CPU","Children":[{"SensorId":"/cpu/voltage/0","Type":"Voltage","Text":"CPU Core","RawValue":"0.696 V","Value":"0.696 V"}]}]}
			""",
			Source);

		Assert.That(sensors.Single().Value, Is.EqualTo(0.696f));
	}

	[Test]
	public void A_formatted_value_uses_the_servers_decimal_separator()
	{
		var sensors = RemoteSensorParser.Parse(
			"""
			{"Children":[{"HardwareId":"/cpu","Text":"CPU","Children":[{"SensorId":"/cpu/temperature/0","Type":"Temperature","Text":"CPU","RawValue":"51,5 °C","Value":"51,5 °C"}]}]}
			""",
			Source);

		Assert.That(sensors.Single().Value, Is.EqualTo(51.5f));
	}

	[Test]
	public void A_document_without_a_raw_value_falls_back_to_the_formatted_number()
	{
		var sensors = RemoteSensorParser.Parse(
			"""
			{"Children":[{"HardwareId":"/cpu","Text":"CPU","Children":[{"SensorId":"/cpu/temperature/0","Type":"Temperature","Text":"CPU","Value":"51.0 °C"}]}]}
			""",
			Source);

		Assert.That(sensors.Single().Value, Is.EqualTo(51f));
	}

	[Test]
	public void A_non_finite_reading_is_no_reading()
	{
		var sensors = RemoteSensorParser.Parse(
			"""
			{"Children":[{"HardwareId":"/cpu","Text":"CPU","Children":[
			  {"SensorId":"/cpu/temperature/0","Type":"Temperature","Text":"A","RawValue":"NaN"},
			  {"SensorId":"/cpu/temperature/1","Type":"Temperature","Text":"B","RawValue":"Infinity"}
			]}]}
			""",
			Source);

		Assert.That(sensors.Select(sensor => sensor.Value), Is.EqualTo(new float?[] { null, null }));
	}

	[Test]
	public void A_formatted_non_finite_reading_is_no_reading()
	{
		var sensors = RemoteSensorParser.Parse(
			"""
			{"Children":[{"HardwareId":"/net","Text":"Network","Children":[
			  {"SensorId":"/net/load/0","Type":"Load","Text":"Utilization","RawValue":"NaN %","Value":"NaN %"},
			  {"SensorId":"/net/load/1","Type":"Load","Text":"Missing","RawValue":"N/A","Value":""}
			]}]}
			""",
			Source);

		Assert.That(sensors.Select(sensor => sensor.Value), Is.EqualTo(new float?[] { null, null }));
	}

	[Test]
	public void A_sensor_with_an_unknown_type_is_left_out()
	{
		var sensors = RemoteSensorParser.Parse(
			"""
			{"Children":[{"HardwareId":"/cpu","Text":"CPU","Children":[
			  {"SensorId":"/cpu/unknown/0","Type":"NotAReading","Text":"Mystery","RawValue":1}
			]}]}
			""",
			Source);

		Assert.That(sensors, Is.Empty);
	}

	[Test]
	public void A_remote_id_the_server_repeats_is_settled_with_a_suffix()
	{
		var sensors = RemoteSensorParser.Parse(
			"""
			{"Children":[{"HardwareId":"/gpu","Text":"GPU","Children":[
			  {"SensorId":"/gpu/load/3","Type":"Load","Text":"GPU Bus","RawValue":1},
			  {"SensorId":"/gpu/load/3","Type":"Load","Text":"GPU Memory","RawValue":2}
			]}]}
			""",
			Source);

		Assert.That(sensors.Select(sensor => sensor.Sensor.Id), Is.EqualTo(SettledRepeatedIds));
	}

	[Test]
	public void A_sensor_without_a_hardware_above_it_has_nowhere_to_hang()
	{
		var sensors = RemoteSensorParser.Parse(
			"""
			{"Children":[{"Text":"Computer","Children":[{"SensorId":"/orphan/0","Type":"Load","Text":"Orphan","RawValue":1}]}]}
			""",
			Source);

		Assert.That(sensors, Is.Empty);
	}
}
