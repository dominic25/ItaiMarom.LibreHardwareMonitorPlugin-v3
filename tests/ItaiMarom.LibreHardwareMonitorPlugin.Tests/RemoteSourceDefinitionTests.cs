using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using NUnit.Framework;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// The identity of a remote machine: the label a user reads and the key a sensor identity and a
/// variable name are built from.
/// </summary>
[TestFixture]
public sealed class RemoteSourceDefinitionTests
{
	[Test]
	public void A_named_source_keeps_the_name_and_derives_a_legal_key()
	{
		Assert.That(
			RemoteSourceDefinition.TryCreate(
				name: "Living Room",
				url: "living-room-pc:8085",
				key: null,
				usedKeys: null,
				usedUrls: null,
				out var definition),
			Is.True);

		Assert.That(definition!.Name, Is.EqualTo("Living Room"));
		Assert.That(definition.Url, Is.EqualTo("http://living-room-pc:8085/data.json"));
		Assert.That(definition.Key, Is.EqualTo("living_room"));
	}

	[Test]
	public void An_unnamed_source_is_labelled_with_its_authority()
	{
		Assert.That(
			RemoteSourceDefinition.TryCreate(
				name: "  ",
				url: "http://office-pc:8085",
				key: null,
				usedKeys: null,
				usedUrls: null,
				out var definition),
			Is.True);

		Assert.That(definition!.Name, Is.EqualTo("office-pc:8085"));
	}

	[Test]
	public void A_rename_keeps_the_key_so_bound_variables_do_not_move()
	{
		Assert.That(
			RemoteSourceDefinition.TryCreate(
				name: "Living Room",
				url: "living-room-pc:8085",
				key: "living_room",
				usedKeys: null,
				usedUrls: null,
				out var definition),
			Is.True);

		Assert.That(definition!.Key, Is.EqualTo("living_room"));
	}

	[Test]
	public void Two_rows_for_one_address_are_one_source()
	{
		var usedKeys = new HashSet<string>(StringComparer.Ordinal);
		var usedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		Assert.That(
			RemoteSourceDefinition.TryCreate(
				"First", "living-room-pc:8085", null, usedKeys, usedUrls, out var first),
			Is.True);
		Assert.That(
			RemoteSourceDefinition.TryCreate(
				"Second", "http://living-room-pc:8085/data.json", null, usedKeys, usedUrls, out _),
			Is.False);

		Assert.That(first, Is.Not.Null);
	}

	[Test]
	public void Two_sources_with_the_same_label_still_get_distinct_keys()
	{
		var usedKeys = new HashSet<string>(StringComparer.Ordinal);
		var usedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		RemoteSourceDefinition.TryCreate("Living Room", "first-pc:8085", null, usedKeys, usedUrls, out var first);
		RemoteSourceDefinition.TryCreate("Living Room", "second-pc:8085", null, usedKeys, usedUrls, out var second);

		Assert.That(first!.Key, Is.EqualTo("living_room"));
		Assert.That(second!.Key, Is.EqualTo("living_room_2"));
	}

	[Test]
	public void An_address_that_is_not_http_is_refused()
	{
		Assert.That(
			RemoteSourceDefinition.TryCreate("Bad", "ftp://host", null, null, null, out var definition),
			Is.False);
		Assert.That(definition, Is.Null);
	}

	[Test]
	public void A_watched_hardware_name_is_matched_by_the_qualified_label()
	{
		var source = new RemoteSourceDefinition("Living Room", "http://host:8085/data.json", "living_room");

		Assert.That(source.MatchesHardwareFilter("Living Room"), Is.True);
		Assert.That(source.MatchesHardwareFilter(source.Qualify("Gigabyte X570")), Is.True);
		Assert.That(source.MatchesHardwareFilter("Living Room PC"), Is.False);
		Assert.That(source.MatchesHardwareFilter("Gigabyte X570"), Is.False);
	}

	[Test]
	public void A_hardware_name_is_qualified_with_the_label()
	{
		var source = new RemoteSourceDefinition("Living Room", "http://host:8085/data.json", "living_room");

		Assert.That(source.Qualify("Gigabyte X570"), Is.EqualTo("Living Room · Gigabyte X570"));
		Assert.That(source.Qualify(" "), Is.EqualTo("Living Room"));
	}
}
