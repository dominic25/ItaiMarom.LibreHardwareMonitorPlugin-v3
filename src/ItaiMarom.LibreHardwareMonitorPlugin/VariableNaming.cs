using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ItaiMarom.LibreHardwareMonitorPlugin;

/// <summary>
/// The rules for canonical Macro Deck variable names: lowercase ASCII letters, digits and underscores,
/// starting with a letter.
/// </summary>
/// <remarks>
/// <para>
/// This mirrors the SDK's own <c>MacroDeck.Sdk.Variables.VariableNames</c>, which the SDK version this
/// plugin is built against does not ship yet. It is not a reinterpretation: the same input has to give
/// the same name here and there, or a variable would be renamed the day the SDK catches up. When that
/// release lands, this type is deleted and its call sites point at the SDK's.
/// </para>
/// <para>
/// The canonical form is deliberately <em>not</em> what <see cref="Hardware.SensorIdentifier.VariableName"/>
/// returns for most sensors: Macro Deck 2 produced names like <c>cpu_core__1_temperature</c> that are
/// already legal, and wrecking them to collapse the double underscore would break layouts that already
/// reference them.
/// </para>
/// </remarks>
public static partial class VariableNaming
{
	private const string PublicPrefix = "vars.";

	/// <summary>
	/// Turns any text into a canonical name: a leading <c>vars.</c> is dropped, accents are removed,
	/// letters are lowercased, every other character becomes an underscore, runs of underscores collapse,
	/// leading and trailing ones are trimmed, and a name starting with a digit gets a <c>v_</c> prefix. The
	/// result is empty when nothing usable is left.
	/// </summary>
	public static string Sanitize(string? input)
	{
		if (string.IsNullOrWhiteSpace(input))
		{
			return string.Empty;
		}

		var trimmed = input.Trim();
		if (trimmed.StartsWith(PublicPrefix, StringComparison.OrdinalIgnoreCase))
		{
			trimmed = trimmed[PublicPrefix.Length..];
		}

		// Decompose so an accented letter becomes its base letter plus a combining mark, then drop the
		// marks: "°C" would otherwise leave a name no one can type.
		var folded = trimmed.Normalize(NormalizationForm.FormD);
		var builder = new StringBuilder(folded.Length);
		foreach (var ch in folded)
		{
			if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
			{
				continue;
			}

			if (ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')
			{
				builder.Append(ch);
			}
			else if (ch is >= 'A' and <= 'Z')
			{
				builder.Append(char.ToLowerInvariant(ch));
			}
			else
			{
				builder.Append('_');
			}
		}

		var collapsed = UnderscoreRuns().Replace(builder.ToString(), "_").Trim('_');

		if (collapsed.Length > 0 && char.IsDigit(collapsed[0]))
		{
			collapsed = "v_" + collapsed;
		}

		return collapsed;
	}

	/// <summary>Whether <paramref name="name"/> is already a canonical variable name.</summary>
	public static bool IsValid(string? name) =>
		!string.IsNullOrEmpty(name) && ValidName().IsMatch(name);

	/// <summary>Longest declared local id the host accepts.</summary>
	public const int MaxDeclaredIdLength = 64;

	/// <summary>Longest resource local id the host accepts.</summary>
	public const int MaxResourceIdLength = 256;

	/// <summary>
	/// Turns arbitrary text - a LibreHardwareMonitor identifier like <c>/lpc/nct6776f/0/temperature/1</c>,
	/// or a sensor name - into a declared local id: lowercase, digits and single hyphens between them,
	/// starting with a letter, at most <see cref="MaxDeclaredIdLength"/> characters.
	/// </summary>
	/// <remarks>
	/// This is the id half of a variable, not its name, so it only has to be unique and stable; the
	/// readable, backwards-compatible spelling lives on
	/// <see cref="Hardware.SensorIdentifier.VariableName"/>. It is fed the hardware library's own sensor
	/// identifier, which is the one piece of a sensor that stays the same when the user swaps a device of
	/// the same kind and which distinguishes two of the same model.
	/// </remarks>
	public static string ToDeclaredId(string? source)
	{
		if (string.IsNullOrWhiteSpace(source))
		{
			return "v";
		}

		var builder = new StringBuilder(source.Length);
		var pendingSeparator = false;
		foreach (var character in source)
		{
			var isLetter = character is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
			if (!isLetter && character is not (>= '0' and <= '9'))
			{
				// Everything else - '/', ' ', '#', '-' - is a boundary between two segments.
				pendingSeparator = true;
				continue;
			}

			if (pendingSeparator && builder.Length > 0)
			{
				builder.Append('-');
			}

			pendingSeparator = false;
			builder.Append(char.ToLowerInvariant(character));
		}

		var id = builder.ToString();
		if (id.Length == 0)
		{
			return "v";
		}

		// A declared id has to start with a letter.
		if (char.IsDigit(id[0]))
		{
			id = "v-" + id;
		}

		return id.Length > MaxDeclaredIdLength ? Trim(id, MaxDeclaredIdLength) : id;
	}

	/// <summary>
	/// Turns any text into a legal catalog resource id: the same text with whitespace, control characters
	/// and the reserved <c>::</c> separator replaced. Unlike a declared id this may contain anything else,
	/// including '/', so a LibreHardwareMonitor identifier passes through readable.
	/// </summary>
	public static string ToResourceId(string? source)
	{
		if (string.IsNullOrWhiteSpace(source))
		{
			return "resource";
		}

		var builder = new StringBuilder(source.Length);
		foreach (var character in source)
		{
			builder.Append(char.IsWhiteSpace(character) || char.IsControl(character) || character == ':'
				? '-'
				: character);
		}

		var id = builder.ToString();
		return id.Length > MaxResourceIdLength ? id[..MaxResourceIdLength] : id;
	}

	/// <summary>Whether <paramref name="id"/> is a legal resource id for this plugin's catalog.</summary>
	public static bool IsValidResourceId(string? id) =>
		!string.IsNullOrEmpty(id)
		&& id.Length <= MaxResourceIdLength
		&& !id.Contains("::", StringComparison.Ordinal)
		&& !id.Any(character => char.IsWhiteSpace(character) || char.IsControl(character));

	/// <summary>Cuts to <paramref name="max"/> characters without leaving a trailing separator.</summary>
	private static string Trim(string value, int max)
	{
		var trimmed = value[..max].TrimEnd('-');
		return trimmed.Length == 0 ? "v" : trimmed;
	}

	[GeneratedRegex("_+", RegexOptions.CultureInvariant)]
	private static partial Regex UnderscoreRuns();

	// \z rather than $: '$' also matches before a trailing newline.
	[GeneratedRegex(@"\A[a-z][a-z0-9_]*\z", RegexOptions.CultureInvariant)]
	private static partial Regex ValidName();
}
