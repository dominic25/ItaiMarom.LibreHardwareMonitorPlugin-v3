using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using NUnit.Framework;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// The browser's half of the classic sensor window, driven without a browser: it reads the page the
/// plugin serves and posts back what a user would have ticked.
/// </summary>
/// <remarks>
/// Redirects are never followed. The redirect the window ends on is the host's OAuth callback, which is
/// the whole point of it - it is what resumes the flow - so it is asserted here rather than chased.
/// </remarks>
internal static class WindowClient
{
	private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false });

	/// <summary>The page a window serves, as the browser would render it.</summary>
	public static async Task<string> ReadPageAsync(Uri windowUri)
	{
		using var response = await Client.GetAsync(windowUri).ConfigureAwait(false);

		Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"GET {windowUri} failed.");

		return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
	}

	/// <summary>What a window holds answers with, or what it answers a stranger with.</summary>
	public static async Task<HttpStatusCode> ReadPageStatusAsync(Uri windowUri)
	{
		using var response = await Client.GetAsync(windowUri).ConfigureAwait(false);
		return response.StatusCode;
	}

	/// <summary>
	/// The readings the window is showing, as the page asks for them while it is open: one entry per
	/// sensor, keyed by the id the page names it by, and null where the sensor has no reading.
	/// </summary>
	public static async Task<IReadOnlyDictionary<string, string?>> ReadValuesAsync(Uri valuesUri)
	{
		using var response = await Client.GetAsync(valuesUri).ConfigureAwait(false);

		Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"GET {valuesUri} failed.");
		Assert.That(
			response.Content.Headers.ContentType?.MediaType,
			Is.EqualTo("application/json"),
			$"GET {valuesUri} did not answer with readings.");

		var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

		return JsonSerializer.Deserialize<Dictionary<string, string?>>(json)
			?? new Dictionary<string, string?>(StringComparer.Ordinal);
	}

	/// <summary>
	/// Ticks what the user ticked and presses Save, and gives back the address the browser was sent to:
	/// the host's callback, carrying the state and the key the flow reads its selection back under.
	/// </summary>
	public static async Task<string> SaveAsync(
		Uri windowUri,
		IEnumerable<string> resourceIds,
		double? polling = null,
		bool autoElevate = false)
	{
		var fields = resourceIds
			.Select(resourceId => new KeyValuePair<string, string>("sensors", resourceId))
			.Append(new KeyValuePair<string, string>("save", "1"))
			.ToList();

		if (polling is { } milliseconds)
		{
			fields.Add(new KeyValuePair<string, string>(
				"polling",
				milliseconds.ToString("R", CultureInfo.InvariantCulture)));
		}

		if (autoElevate)
		{
			// A checkbox is submitted only while it is ticked, so this is exactly what a browser sends
			// for the startup elevation box.
			fields.Add(new KeyValuePair<string, string>("auto_elevate", "1"));
		}

		return await PostAsync(windowUri, fields).ConfigureAwait(false);
	}

	/// <summary>Presses Cancel, which is the other way out of the window.</summary>
	public static Task<string> CancelAsync(Uri windowUri) =>
		PostAsync(windowUri, [new KeyValuePair<string, string>("cancel", "1")]);

	/// <summary>Posts a window a form without asserting anything about where it goes.</summary>
	public static async Task<HttpStatusCode> PostAsync(Uri windowUri, IEnumerable<KeyValuePair<string, string>> fields)
	{
		using var response = await Client
			.PostAsync(windowUri, new FormUrlEncodedContent(fields))
			.ConfigureAwait(false);

		return response.StatusCode;
	}

	/// <summary>Reads the key out of the address the callback was sent to, which is what a flow resumes on.</summary>
	public static string CodeOf(string callbackUri) => ParameterOf(callbackUri, "code");

	/// <summary>Reads a parameter out of the address the callback was sent to.</summary>
	public static string ParameterOf(string callbackUri, string name)
	{
		var value = new Uri(callbackUri).Query
			.Split('&', StringSplitOptions.RemoveEmptyEntries)
			.Select(pair => pair.Split('=', 2))
			.Where(parts => parts.Length == 2 && parts[0].TrimStart('?') == name)
			.Select(parts => Uri.UnescapeDataString(parts[1]))
			.FirstOrDefault();

		Assert.That(value, Is.Not.Null, $"{callbackUri} carried no {name}.");

		return value!;
	}

	private static async Task<string> PostAsync(Uri windowUri, IReadOnlyList<KeyValuePair<string, string>> fields)
	{
		using var response = await Client
			.PostAsync(windowUri, new FormUrlEncodedContent(fields))
			.ConfigureAwait(false);

		Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Found), $"POST {windowUri} was not redirected.");
		Assert.That(response.Headers.Location, Is.Not.Null, $"POST {windowUri} carried no Location.");

		return response.Headers.Location!.ToString();
	}
}
