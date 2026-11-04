namespace ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

/// <summary>
/// One other machine the plugin reads sensors from: what the user calls it, the address its
/// LibreHardwareMonitor Remote Web Server answers on, and the stable key its sensors are namespaced by.
/// </summary>
/// <remarks>
/// <para>
/// The key is not derived again on every start. A user can rename a remote machine without anything
/// already bound to its sensors changing, which is why the settings window carries the key in a hidden
/// field and the stored document keeps it.
/// </para>
/// <para>
/// The label is what the settings tree and the catalog show. The key is what variable names and sensor
/// identities are built from, because a label is free text and an identity has to be stable and legal.
/// </para>
/// <para>
/// Optional HTTP Basic Auth credentials for the remote server. When provided, every request includes
/// an <c>Authorization: Basic</c> header. The password is stored in the plugin configuration and is
/// transmitted only to the configured remote address over HTTPS (or HTTP on a trusted LAN).
/// </para>
/// </remarks>
public sealed record RemoteSourceDefinition(string Name, string Url, string Key, string? UserName = null, string? Password = null)
{
	/// <summary>What separates a remote label from the hardware name it qualifies.</summary>
	public const string NameSeparator = " · ";

	/// <summary>Enough to tell two machines apart without turning an identity into a paragraph.</summary>
	private const int MaximumKeyLength = 32;

	/// <summary>
	/// Builds a definition from what a user typed or what the configuration held. The address is
	/// normalised, the label is filled in from the address when it is blank, and the key is kept when it
	/// is usable so a rename cannot move a variable.
	/// </summary>
	/// <param name="usedKeys">Keys already taken, and the set this one is added to.</param>
	/// <param name="usedUrls">Normalised addresses already taken, and the set this one is added to.</param>
	public static bool TryCreate(
		string? name,
		string? url,
		string? key,
		ISet<string>? usedKeys,
		ISet<string>? usedUrls,
		out RemoteSourceDefinition? definition,
		string? userName = null,
		string? password = null)
	{
		definition = null;

		if (!RemoteSourceAddress.TryParse(url, out var address))
		{
			return false;
		}

		var normalizedUrl = address.AbsoluteUri;
		if (usedUrls is not null && !usedUrls.Add(normalizedUrl))
		{
			// Two rows naming the same machine are one source. The first is the one that stays, so a
			// duplicate row cannot make two sets of identically named variables.
			return false;
		}

		var label = string.IsNullOrWhiteSpace(name) ? address.Authority : name.Trim();
		var stem = NormalizeKey(key);
		if (stem.Length == 0)
		{
			stem = NormalizeKey(label);
		}

		if (stem.Length == 0)
		{
			stem = "remote";
		}

		definition = new RemoteSourceDefinition(label, normalizedUrl, UniqueKey(stem, usedKeys), userName, password);
		return true;
	}

	/// <summary>Whether a watched hardware name belongs to this source.</summary>
	public bool MatchesHardwareFilter(string hardwareName) =>
		string.Equals(hardwareName, Name, StringComparison.Ordinal)
		|| hardwareName.StartsWith(Name + NameSeparator, StringComparison.Ordinal);

	/// <summary>The label and hardware name as the sensor tree and the catalog show them.</summary>
	public string Qualify(string hardwareName) =>
		string.IsNullOrWhiteSpace(hardwareName) ? Name : Name + NameSeparator + hardwareName;

	/// <inheritdoc />
	public override string ToString() => $"{Name} ({Url})";

	private static string NormalizeKey(string? text)
	{
		var key = VariableNaming.Sanitize(text);
		key = key.Length > MaximumKeyLength ? key[..MaximumKeyLength].TrimEnd('_') : key;
		// "test" is reserved for internal test dummy sources; never allow it as a real remote key
		return string.Equals(key, "test", StringComparison.OrdinalIgnoreCase) ? "remote" : key;
	}

	private static string UniqueKey(string stem, ISet<string>? used)
	{
		if (used is null || used.Add(stem))
		{
			return stem;
		}

		for (var suffix = 2; ; suffix++)
		{
			var tail = "_" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
			var room = MaximumKeyLength - tail.Length;
			var head = stem.Length > room ? stem[..room].TrimEnd('_') : stem;
			var candidate = (head.Length > 0 ? head : "remote") + tail;

			if (used.Add(candidate))
			{
				return candidate;
			}
		}
	}
}
