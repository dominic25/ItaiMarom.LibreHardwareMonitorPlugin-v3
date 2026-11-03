using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using NUnit.Framework;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// Turning what a user types into the one address a LibreHardwareMonitor Remote Web Server answers on.
/// A user should not have to know that the endpoint is called <c>data.json</c>.
/// </summary>
[TestFixture]
public sealed class RemoteSourceAddressTests
{
	[TestCase("living-room-pc:8085", "http://living-room-pc:8085/data.json")]
	[TestCase("http://living-room-pc:8085", "http://living-room-pc:8085/data.json")]
	[TestCase("http://living-room-pc:8085/", "http://living-room-pc:8085/data.json")]
	[TestCase("http://living-room-pc:8085/data.json", "http://living-room-pc:8085/data.json")]
	[TestCase("https://living-room-pc:8085", "https://living-room-pc:8085/data.json")]
	[TestCase("http://living-room-pc:8085/lhm/", "http://living-room-pc:8085/lhm/data.json")]
	[TestCase("http://living-room-pc:8085/lhm", "http://living-room-pc:8085/lhm/data.json")]
	public void An_address_is_reduced_to_the_data_endpoint(string typed, string expected) =>
		Assert.That(RemoteSourceAddress.Normalize(typed), Is.EqualTo(expected));

	[TestCase("ftp://living-room-pc:8085")]
	[TestCase("file:///C:/sensors.json")]
	[TestCase("")]
	[TestCase("   ")]
	[TestCase("http://")]
	public void Anything_that_is_not_an_http_address_is_refused(string typed) =>
		Assert.That(RemoteSourceAddress.TryParse(typed, out _), Is.False);

	[Test]
	public void A_fragment_does_not_survive_normalization()
	{
		Assert.That(
			RemoteSourceAddress.Normalize("http://host:8085/data.json#top"),
			Is.EqualTo("http://host:8085/data.json"));
	}
}
