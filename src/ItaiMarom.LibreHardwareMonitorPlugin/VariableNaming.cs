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

	[GeneratedRegex("_+", RegexOptions.CultureInvariant)]
	private static partial Regex UnderscoreRuns();

	// \z rather than $: '$' also matches before a trailing newline.
	[GeneratedRegex(@"\A[a-z][a-z0-9_]*\z", RegexOptions.CultureInvariant)]
	private static partial Regex ValidName();
}
