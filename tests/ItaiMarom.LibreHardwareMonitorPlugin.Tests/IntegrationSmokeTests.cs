using NUnit.Framework;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

[TestFixture]
public sealed class IntegrationSmokeTests
{
	[Test]
	public void The_integration_exposes_no_actions()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		var integration = new LibreHardwareMonitorIntegration(logger);

		// The Macro Deck 2 plugin had no actions either: it only ever produced variables. Declaring the
		// same shape here means upgrading cannot silently lose or invent a button action.
		Assert.That(integration.Actions, Is.Empty);
	}
}
