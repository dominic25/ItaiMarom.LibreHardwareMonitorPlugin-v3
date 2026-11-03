namespace ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

/// <summary>
/// Turns what a user types into the one address a LibreHardwareMonitor remote source answers on.
/// </summary>
/// <remarks>
/// A user should not have to know the endpoint's name. <c>living-room-pc:8085</c>,
/// <c>http://living-room-pc:8085</c> and <c>http://living-room-pc:8085/data.json</c> all name the same
/// machine, so all three are accepted and reduced to the last one. Only <c>http</c> and <c>https</c> are
/// accepted, because the plugin has no business opening a file or a socket by another name.
/// </remarks>
public static class RemoteSourceAddress
{
	/// <summary>The endpoint LibreHardwareMonitor's Remote Web Server serves its tree on.</summary>
	public const string DataPath = "/data.json";

	/// <summary>
	/// Parses one address, filling in the scheme and the endpoint when the user left them out. Returns
	/// false for anything that cannot be an absolute HTTP address.
	/// </summary>
	public static bool TryParse(string? text, out Uri address)
	{
		address = null!;

		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}

		var candidate = text.Trim();
		if (!candidate.Contains("://", StringComparison.Ordinal))
		{
			candidate = "http://" + candidate;
		}

		if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed)
			|| (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
			|| string.IsNullOrWhiteSpace(parsed.Host))
		{
			return false;
		}

		var path = parsed.AbsolutePath;
		if (string.IsNullOrEmpty(path) || path == "/")
		{
			path = DataPath;
		}
		else if (path.EndsWith('/'))
		{
			path += DataPath.TrimStart('/');
		}
		else if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
		{
			path += DataPath;
		}

		address = new UriBuilder(parsed)
		{
			Path = path,
			Fragment = string.Empty,
		}.Uri;

		return true;
	}

	/// <summary>
	/// The address in the form it is stored and compared in, or null when it is not an address at all.
	/// </summary>
	public static string? Normalize(string? text) =>
		TryParse(text, out var address) ? address.AbsoluteUri : null;
}
