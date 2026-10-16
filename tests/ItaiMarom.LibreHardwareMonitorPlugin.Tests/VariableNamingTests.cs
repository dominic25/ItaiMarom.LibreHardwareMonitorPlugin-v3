using NUnit.Framework;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// The canonical-name and identifier rules. A change here is a change to what every user's existing
/// variable is called, which is why the expectations are spelled out rather than computed.
/// </summary>
[TestFixture]
public sealed class VariableNamingTests
{
	[TestCase("CPU Core #1", "cpu_core_1")]
	[TestCase("CPU Total", "cpu_total")]
	[TestCase("3VSB", "v_3vsb")]
	[TestCase("vars.cpu_total", "cpu_total")]
	[TestCase("VARS.CPU", "cpu")]
	[TestCase("Café", "cafe")]
	[TestCase("  spaced  ", "spaced")]
	[TestCase("°C", "c")]
	[TestCase("", "")]
	[TestCase(null, "")]
	public void Sanitize_produces_a_canonical_name(string? input, string expected) =>
		Assert.That(VariableNaming.Sanitize(input), Is.EqualTo(expected));

	[TestCase("cpu_core_1", true)]
	[TestCase("v_3vsb", true)]
	[TestCase("_cpu", false)]
	[TestCase("1cpu", false)]
	[TestCase("cpu-core", false)]
	[TestCase("CPU", false)]
	[TestCase("", false)]
	[TestCase(null, false)]
	public void IsValid_accepts_only_canonical_names(string? name, bool expected) =>
		Assert.That(VariableNaming.IsValid(name), Is.EqualTo(expected));

	[Test]
	public void A_declared_id_is_lowercase_hyphenated_and_unique_shaped()
	{
		// The shape LibreHardwareMonitor hands out, turned into something the host accepts as a local id.
		Assert.That(
			VariableNaming.ToDeclaredId("/lpc/nct6776f/0/temperature/1"),
			Is.EqualTo("lpc-nct6776f-0-temperature-1"));
	}

	[Test]
	public void A_declared_id_collapses_punctuation_into_single_hyphens()
	{
		Assert.That(VariableNaming.ToDeclaredId("CPU Core #1"), Is.EqualTo("cpu-core-1"));
		Assert.That(VariableNaming.ToDeclaredId("Fan  /  #2"), Is.EqualTo("fan-2"));
	}

	[Test]
	public void A_declared_id_always_starts_with_a_letter()
	{
		Assert.That(VariableNaming.ToDeclaredId("3VSB"), Is.EqualTo("v-3vsb"));
	}

	[Test]
	public void A_declared_id_is_never_empty()
	{
		Assert.That(VariableNaming.ToDeclaredId(null), Is.EqualTo("v"));
		Assert.That(VariableNaming.ToDeclaredId("   "), Is.EqualTo("v"));
		Assert.That(VariableNaming.ToDeclaredId("###"), Is.EqualTo("v"));
	}

	[Test]
	public void A_declared_id_cuts_to_the_length_the_host_accepts()
	{
		var id = VariableNaming.ToDeclaredId("/" + new string('a', 200));

		Assert.That(id, Has.Length.EqualTo(VariableNaming.MaxDeclaredIdLength));
		Assert.That(id, Is.EqualTo(new string('a', VariableNaming.MaxDeclaredIdLength)));
	}

	[Test]
	public void A_declared_id_is_idempotent()
	{
		// Uniqueness is settled by suffixing an already-derived id, so feeding one back in has to be a
		// no-op or the suffix would move every time it was read.
		var once = VariableNaming.ToDeclaredId("/test/cpu/temperature/cpu-core-1");

		Assert.That(VariableNaming.ToDeclaredId(once), Is.EqualTo(once));
	}

	[Test]
	public void A_resource_id_keeps_slashes_so_a_resource_stays_readable()
	{
		Assert.That(
			VariableNaming.ToResourceId("/test/cpu/temperature/cpu-core-1"),
			Is.EqualTo("/test/cpu/temperature/cpu-core-1"));
	}

	[Test]
	public void A_resource_id_replaces_only_what_the_host_reserves()
	{
		Assert.That(VariableNaming.ToResourceId("a b\tc"), Is.EqualTo("a-b-c"));
		Assert.That(VariableNaming.ToResourceId("a::b"), Is.EqualTo("a--b"));
	}

	[Test]
	public void A_resource_id_is_never_empty()
	{
		Assert.That(VariableNaming.ToResourceId(null), Is.EqualTo("resource"));
	}

	[TestCase("/test/cpu/load/cpu-total", true)]
	[TestCase("/lpc/nct6776f/0/temperature/1", true)]
	[TestCase("a::b", false)]
	[TestCase("a b", false)]
	[TestCase("", false)]
	[TestCase(null, false)]
	public void IsValidResourceId_matches_what_the_host_accepts(string? id, bool expected) =>
		Assert.That(VariableNaming.IsValidResourceId(id), Is.EqualTo(expected));

	[Test]
	public void A_resource_id_longer_than_the_host_accepts_is_rejected()
	{
		Assert.That(VariableNaming.IsValidResourceId(new string('a', VariableNaming.MaxResourceIdLength)), Is.True);
		Assert.That(VariableNaming.IsValidResourceId(new string('a', VariableNaming.MaxResourceIdLength + 1)), Is.False);
	}
}
