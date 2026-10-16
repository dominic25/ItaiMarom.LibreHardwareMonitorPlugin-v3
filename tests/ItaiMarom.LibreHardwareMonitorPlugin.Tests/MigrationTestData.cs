using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using ItaiMarom.LibreHardwareMonitorPlugin.Migration;
using MacroDeck.Plugin.Testing.Fakes;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// Puts a sensor selection into a fake host configuration the way the Macro Deck 2 migration does, so a
/// test can drive the plugin down the path production takes rather than reaching past it into the
/// selection the integration owns.
/// </summary>
/// <remarks>
/// It goes through <see cref="MigratedSensorSelection"/>'s own serialization rather than a hand-written
/// document, which is the point: a change to the stored shape that a read-back could not follow fails
/// here instead of shipping.
/// </remarks>
internal static class MigrationTestData
{
	/// <summary>Records <paramref name="sensors"/> as the sensors a migration left behind.</summary>
	/// <returns>The id of the configuration entry the selection was written to.</returns>
	public static Guid SeedMigratedSensors(FakeIntegrationConfig config, params SensorIdentifier[] sensors)
	{
		var values = new MigratedSensorSelection(sensors, null).ToValues();

		var entry = config.AddEntry(MacroDeck2Migration.ConfigurationTitle);
		config.SeedString(
			entry,
			MigratedSensorSelection.SensorsKey,
			values[MigratedSensorSelection.SensorsKey].GetString()!);

		return entry;
	}
}
