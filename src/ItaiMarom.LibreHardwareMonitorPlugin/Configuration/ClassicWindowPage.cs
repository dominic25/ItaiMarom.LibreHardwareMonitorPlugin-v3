using System.Globalization;
using System.Net;
using System.Text;
using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using LibreHardwareMonitor.Hardware;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Configuration;

/// <summary>
/// Draws the Macro Deck 2 dialog: a tick box per sensor under its reading kind and its hardware, with
/// the polling rate underneath.
/// </summary>
/// <remarks>
/// <para>
/// The tree is the old dialog's, node for node: the hardware first, named the way the hardware library
/// names it, then the reading kind, then the sensor - labelled the way the old plugin labelled it, with
/// its kind after it, because that text is also the variable it ends up naming. The branches keep the
/// order the hardware library reported them in, and the sensors are handed over already ticked, so a
/// reopened window shows the configuration the plugin is running on.
/// </para>
/// <para>
/// The page is rendered here rather than built by the browser, which is what makes it work with no
/// scripting at all: every sensor arrives as a real, already-ticked <c>input</c>, with the reading it is
/// reporting at that moment beside it, and the form posts it back as it stands. Scripting only adds what
/// a tree of tick boxes cannot be without it - a branch that ticks everything under it, a count, a filter
/// for a machine with a few hundred sensors, and the refreshes that keep those readings live.
/// </para>
/// <para>
/// The markup is a constant in this file rather than a page beside it. A page is a file the plugin
/// package would have to either carry or explain away, and it is neither an asset the host uses nor
/// something to ship a second copy of: compiled in, there is no path to resolve, nothing to go missing
/// from an installation, and no duplicate to keep in step with the renderer below.
/// </para>
/// </remarks>
internal static class ClassicWindowPage
{
	private const string ActionMarker = "__ACTION__";
	private const string TitleMarker = "__TITLE__";
	private const string NoticeMarker = "__NOTICE__";
	private const string TreeMarker = "__TREE__";
	private const string ValuesMarker = "__VALUES__";
	private const string PollingMarker = "__POLLING__";
	private const string PollingMinimumMarker = "__POLLING_MIN__";
	private const string PollingMaximumMarker = "__POLLING_MAX__";

	/// <summary>What a window with no readings to show has: every cell renders as "no reading".</summary>
	private static readonly IReadOnlyDictionary<string, string?> EmptyValues =
		new Dictionary<string, string?>(StringComparer.Ordinal);

	/// <summary>
	/// The whole page but for what changes per window, which is everything a marker stands in for: the
	/// address the form posts to, the title, the tree and the slider's three numbers.
	/// </summary>
	/// <remarks>
	/// It is served byte for byte as written, so the indentation here is the page's own and the blank
	/// lines are part of it.
	/// </remarks>
	private const string Template = """
		<!DOCTYPE html>
		<html lang="en">
		<head>
		<meta charset="utf-8">
		<meta name="viewport" content="width=device-width, initial-scale=1">
		<title>__TITLE__</title>
		<style>
		:root {
			color-scheme: dark;
			--bg: #16181d;
			--panel: #1d2026;
			--panel-2: #23262e;
			--line: #32363f;
			--text: #e6e8ee;
			--dim: #9aa1ad;
			--accent: #4c8dff;
			--accent-soft: #24375c;
			--missing: #6f7783;
		}
		* { box-sizing: border-box; }
		body {
			margin: 0;
			background: var(--bg);
			color: var(--text);
			font: 14px/1.45 "Segoe UI", system-ui, sans-serif;
		}
		form { display: flex; flex-direction: column; height: 100vh; }
		header { padding: 18px 22px 12px; border-bottom: 1px solid var(--line); }
		h1 { margin: 0 0 6px; font-size: 17px; font-weight: 600; }
		header p { margin: 0; color: var(--dim); max-width: 78ch; }
		p.notice {
			margin: 10px 0 0; padding: 9px 12px; max-width: 78ch;
			border-left: 3px solid #d9a13b; border-radius: 0 6px 6px 0;
			background: #2a2417; color: #f0dcae;
		}
		.toolbar {
			display: flex; align-items: center; gap: 12px;
			padding: 12px 22px; background: var(--panel); border-bottom: 1px solid var(--line);
		}
		#filter {
			flex: 1; padding: 7px 10px; border: 1px solid var(--line); border-radius: 6px;
			background: var(--bg); color: var(--text); font: inherit;
		}
		#filter:focus { outline: 2px solid var(--accent-soft); border-color: var(--accent); }
		#counts, #updated { color: var(--dim); white-space: nowrap; font-variant-numeric: tabular-nums; }
		.tree { flex: 1; overflow: auto; padding: 10px 12px 18px; }
		details { border-radius: 6px; }
		summary {
			display: flex; align-items: center; gap: 9px;
			padding: 7px 10px; cursor: pointer; user-select: none;
			border-radius: 6px; font-weight: 600;
		}
		summary:hover, details[open] > summary { background: var(--panel); }
		summary::-webkit-details-marker { display: none; }
		summary .name { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
		summary .count { color: var(--dim); font-weight: 400; font-variant-numeric: tabular-nums; }
		details details { margin-left: 20px; border-left: 1px solid var(--line); }
		summary .kind { font-weight: 500; color: var(--dim); }
		ul { list-style: none; margin: 0; padding: 0 0 4px 34px; }
		li { margin: 1px 0; }
		li label { display: flex; align-items: center; gap: 9px; padding: 5px 10px; border-radius: 5px; cursor: pointer; }
		li label:hover { background: var(--panel); }
		li.hidden, .hidden { display: none; }
		input[type="checkbox"] { accent-color: var(--accent); width: 15px; height: 15px; cursor: pointer; flex: none; }
		input[type="checkbox"]:indeterminate { opacity: .8; }
		li label span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
		li label span:first-of-type { flex: 1; min-width: 0; }
		li label .value { flex: none; color: var(--dim); font-variant-numeric: tabular-nums; }
		li label .value.none { color: var(--missing); }
		li label .writable, summary .contains, li label .readonly {
			flex: none; padding: 1px 7px; border-radius: 999px; font-size: 11px; font-weight: 600;
			letter-spacing: .02em;
		}
		li label .writable { border: 1px solid var(--accent); color: #a9c6ff; }
		summary .contains { border: 1px dashed var(--accent); color: var(--accent); }
		li label .readonly { border: 1px solid var(--line); color: var(--missing); font-weight: 500; }
		footer {
			display: flex; align-items: center; gap: 20px; flex-wrap: wrap;
			padding: 14px 22px; background: var(--panel); border-top: 1px solid var(--line);
		}
		.polling { display: flex; align-items: center; gap: 12px; flex: 1; min-width: 320px; }
		.polling label { color: var(--dim); white-space: nowrap; }
		input[type="range"] { flex: 1; min-width: 120px; accent-color: var(--accent); }
		input[type="number"] {
			width: 92px; padding: 6px 8px; border: 1px solid var(--line); border-radius: 6px;
			background: var(--bg); color: var(--text); font: inherit; text-align: right;
		}
		.actions { display: flex; gap: 10px; }
		button {
			padding: 8px 18px; border-radius: 6px; font: inherit; font-weight: 600; cursor: pointer;
			border: 1px solid var(--line); background: var(--panel-2); color: var(--text);
		}
		button:hover { background: var(--line); }
		button.primary { background: var(--accent); border-color: var(--accent); color: #fff; }
		button.primary:hover { filter: brightness(1.1); }
		.empty { padding: 24px; color: var(--dim); }
		</style>
		</head>
		<body>
		<form method="post" action="__ACTION__" autocomplete="off">
			<header>
				<h1>__TITLE__</h1>
				<p>Tick the sensors to publish as Macro Deck variables. Either way, every sensor shows what it is reporting right now, so a reading that never moves can be left alone. Anything you leave out stays reachable from the variable catalog, and the variable names the Macro Deck 2 plugin used are kept, so an upgraded button keeps working. A sensor marked <strong>controllable</strong> can then be driven by Macro Deck's Slider widget; a branch marked <strong>control below</strong> is where one of those sensors is.</p>
		__NOTICE__
			</header>

			<div class="toolbar">
				<input type="search" id="filter" placeholder="Filter sensors&hellip;" aria-label="Filter sensors">
				<span id="updated"></span>
				<span id="counts"></span>
			</div>

			<div class="tree">
		__TREE__
			</div>

			<footer>
				<div class="polling">
					<label for="polling" title="How often the sensors are read, in milliseconds.">Polling rate</label>
					<input type="range" id="pollingRange" min="__POLLING_MIN__" max="__POLLING_MAX__" step="1">
					<input type="number" id="polling" name="polling" min="__POLLING_MIN__" max="__POLLING_MAX__" step="1" value="__POLLING__">
				</div>
				<div class="actions">
					<button type="submit" name="cancel" value="1">Cancel</button>
					<button type="submit" name="save" value="1" class="primary">Save</button>
				</div>
			</footer>
		</form>

		<script>
		(function () {
			"use strict";

			var form = document.forms[0];
			var tree = document.querySelector(".tree");
			var boxes = [].slice.call(tree.querySelectorAll('input[name="sensors"]'));
			var counts = document.getElementById("counts");
			var filter = document.getElementById("filter");
			var polling = document.getElementById("polling");
			var pollingRange = document.getElementById("pollingRange");

			// The group tick boxes are not submitted - they only drive the sensors under them, so they start
			// indeterminate wherever the sensors disagree and never carry a value of their own.
			var groups = [].slice.call(tree.querySelectorAll("details")).map(function (details) {
				return {
					details: details,
					box: details.querySelector("summary input"),
					sensors: [].slice.call(details.querySelectorAll('input[name="sensors"]')),
				};
			});

			function syncGroup(group) {
				var on = 0;
				for (var i = 0; i < group.sensors.length; i++) if (group.sensors[i].checked) on++;
				if (group.box) {
					group.box.checked = on > 0 && on === group.sensors.length;
					group.box.indeterminate = on > 0 && on < group.sensors.length;
				}
				var count = group.details.querySelector("summary .count");
				if (count) count.textContent = on + "/" + group.sensors.length;
			}

			// The tree has a branch per hardware and a branch per reading kind inside it, so the branch counts
			// overlap. The number of the whole window is counted off the sensors alone.
			function syncAll() {
				var on = 0;
				for (var i = 0; i < groups.length; i++) syncGroup(groups[i]);
				for (var i = 0; i < boxes.length; i++) if (boxes[i].checked) on++;
				counts.textContent = on + " of " + boxes.length + " selected";
			}

			for (var g = 0; g < groups.length; g++) {
				(function (group) {
					if (group.box) {
						group.box.addEventListener("click", function (event) { event.stopPropagation(); });
						group.box.addEventListener("change", function () {
							for (var i = 0; i < group.sensors.length; i++) {
								group.sensors[i].checked = group.box.checked;
							}
							syncAll();
						});
					}
					for (var i = 0; i < group.sensors.length; i++) {
						group.sensors[i].addEventListener("change", syncAll);
					}
				})(groups[g]);
			}

			// Filtering: sensors that do not match are hidden, and a branch is opened while it has a match and
			// closed again once the box is emptied, so the tree collapses back to the configured sensors.
			filter.addEventListener("input", function () {
				var needle = filter.value.trim().toLowerCase();
				for (var i = 0; i < boxes.length; i++) {
					var name = boxes[i].nextElementSibling;
					var match = !needle || name.textContent.toLowerCase().indexOf(needle) !== -1;
					boxes[i].closest("li").classList.toggle("hidden", !match);
					if (needle) boxes[i].closest("details").open = true;
				}
				for (var d = 0; d < groups.length; d++) {
					if (needle) { groups[d].details.open = true; }
					else { groups[d].details.open = groups[d].sensors.some(function (box) { return box.checked; }); }
				}
			});

			function applyPolling(value) {
				polling.value = value;
				pollingRange.value = value;
			}
			pollingRange.addEventListener("input", function () { applyPolling(pollingRange.value); });
			polling.addEventListener("input", function () { applyPolling(polling.value); });
			applyPolling(polling.value);

			syncAll();

			// The readings are asked for once a second and written into the cell of the sensor they belong
			// to. Only those cells are touched, so nothing the user has ticked, opened or typed moves under
			// them, and a cell is never matched by position: it carries the sensor's resource id.
			var cells = [].slice.call(tree.querySelectorAll("[data-value]"));
			var updated = document.getElementById("updated");
			var valuesUrl = "__VALUES__";

			function refreshValues() {
				if (document.hidden) return;

				fetch(valuesUrl, { cache: "no-store" })
					.then(function (response) { return response.ok ? response.json() : null; })
					.then(function (values) {
						if (!values) return;

						for (var i = 0; i < cells.length; i++) {
							var cell = cells[i];
							var value = values[cell.getAttribute("data-value")];
							var missing = value === null || value === undefined || value === "";
							cell.textContent = missing ? "\u2014" : value;
							cell.classList.toggle("none", missing);
						}

						if (updated) updated.textContent = "values updated " + new Date().toLocaleTimeString();
					})
					.catch(function () {
						// Reading stopped - the plugin is closing, or this page is on its way out. The next
						// tick asks again, and what is on screen is left as it was rather than blanked.
						if (updated) updated.textContent = "live values unavailable";
					});
			}

			// Everything the tree needs to be a working form is already done. The refresh is started last,
			// and only where there is a fetch to make, so a page that cannot refresh is still a page whose
			// ticks and counts work - with the readings it was drawn with.
			if (typeof window.fetch === "function") {
				refreshValues();
				window.setInterval(refreshValues, 1000);
			}
		})();
		</script>
		</body>
		</html>
		""";

	/// <summary>The whole page for one window.</summary>
	public static string Render(ClassicWindowOpening opening, string key)
	{
		ArgumentNullException.ThrowIfNull(opening);

		return Template
			.Replace(ActionMarker, $"/?s={WebUtility.UrlEncode(key)}", StringComparison.Ordinal)
			.Replace(TitleMarker, WebUtility.HtmlEncode(opening.Title), StringComparison.Ordinal)
			.Replace(NoticeMarker, RenderNotice(opening.Notice), StringComparison.Ordinal)
			.Replace(TreeMarker, RenderTree(opening), StringComparison.Ordinal)
			.Replace(ValuesMarker, $"/values?s={WebUtility.UrlEncode(key)}", StringComparison.Ordinal)
			.Replace(PollingMinimumMarker, Milliseconds(opening.MinimumPollingMilliseconds), StringComparison.Ordinal)
			.Replace(PollingMaximumMarker, Milliseconds(opening.MaximumPollingMilliseconds), StringComparison.Ordinal)
			.Replace(PollingMarker, Milliseconds(opening.PollingMilliseconds), StringComparison.Ordinal);
	}

	/// <summary>
	/// The one thing that is wrong with the machine rather than with the selection, put under the heading:
	/// readings the hardware library's kernel driver would have produced and this process cannot reach.
	/// The sentence says why they are missing and what to do about it; the lead says that something is,
	/// so the line reads as a warning rather than as small print. Nothing at all when there is nothing to
	/// say, which is the usual case.
	/// </summary>
	private static string RenderNotice(string? notice) =>
		string.IsNullOrWhiteSpace(notice)
			? string.Empty
			: $"\t\t\t<p class=\"notice\"><strong>Some readings are missing.</strong> {WebUtility.HtmlEncode(notice)}</p>";

	/// <summary>
	/// The tree: one branch per hardware, one branch per reading kind inside it, and a tick box per
	/// sensor. Branches that hold a configured sensor start open, so a reopened window shows what the
	/// plugin is running on rather than a wall of collapsed names.
	/// </summary>
	private static string RenderTree(ClassicWindowOpening opening)
	{
		var categories = opening.Categories;
		if (categories.Count == 0)
		{
			return "\t\t<p class=\"empty\">No sensors were reported by this machine.</p>";
		}

		var selected = opening.SelectedResourceIds.ToHashSet(StringComparer.Ordinal);
		var values = opening.Values ?? EmptyValues;
		var builder = new StringBuilder();

		foreach (var root in categories.GroupBy(category => category.RootName, StringComparer.Ordinal))
		{
			var rootSensors = root.SelectMany(category => category.Sensors).ToArray();
			var rootTicked = rootSensors.Count(sensor => selected.Contains(sensor.ResourceId));

			AppendOpen(
				builder,
				"root",
				root.Key,
				rootTicked,
				rootSensors.Length,
				rootSensors.Any(sensor => sensor.Controllable));

			foreach (var category in root)
			{
				var kindTicked = category.Sensors.Count(sensor => selected.Contains(sensor.ResourceId));

				AppendOpen(
					builder,
					"kind",
					category.TypeName,
					kindTicked,
					category.Sensors.Count,
					category.Sensors.Any(sensor => sensor.Controllable));

				builder.Append("\t\t\t<ul>\n");
				foreach (var sensor in category.Sensors)
				{
					AppendSensor(builder, sensor, selected.Contains(sensor.ResourceId), values);
				}

				builder.Append("\t\t\t</ul>\n\t\t</details>\n");
			}

			builder.Append("\t</details>\n");
		}

		return builder.ToString();
	}

	/// <summary>
	/// A branch's own row: a tick box that is not submitted, its name, whether a control hides below it,
	/// and how much of it is on.
	/// </summary>
	/// <remarks>
	/// The marker on a branch is deliberately not the one a sensor carries. A branch cannot be driven; it
	/// only says that one of the sensors under it can be, so a collapsed hardware or reading kind still
	/// tells the user where to look without claiming the branch itself is writable.
	/// </remarks>
	private static void AppendOpen(
		StringBuilder builder,
		string kind,
		string name,
		int ticked,
		int count,
		bool containsControl)
	{
		builder.Append(ticked > 0 ? "\t<details open>\n" : "\t<details>\n")
			.Append("\t\t<summary><input type=\"checkbox\">")
			.Append("<span class=\"name ")
			.Append(kind)
			.Append("\">")
			.Append(WebUtility.HtmlEncode(name))
			.Append("</span>");

		if (containsControl)
		{
			builder.Append(
				"<span class=\"contains\" title=\"A sensor below can be driven by the built-in Slider widget.\">control below</span>");
		}

		builder.Append("<span class=\"count\">")
			.Append(ticked.ToString(CultureInfo.InvariantCulture))
			.Append('/')
			.Append(count.ToString(CultureInfo.InvariantCulture))
			.Append("</span></summary>\n");
	}

	/// <summary>
	/// One sensor, labelled the way the old dialog labelled it: its name with the reading kind after it,
	/// which is the text the old plugin also built its variable names from. The tick box carries the
	/// identity, so this is what comes back from the window.
	/// </summary>
	/// <remarks>
	/// A sensor the hardware library gave a software control carries a badge. Without one a user cannot
	/// tell it apart from the fan's RPM sensor beside it, and would bind a Slider to a reading that only
	/// ever moves. A <see cref="SensorType.Control"/> sensor with no control behind it says so instead:
	/// the library creates those too - for a graphics card's fan duty, or a chip it can read but not
	/// drive - and an unexplained missing badge would read as a bug.
	/// </remarks>
	private static void AppendSensor(
		StringBuilder builder,
		SensorIdentifier sensor,
		bool ticked,
		IReadOnlyDictionary<string, string?> values)
	{
		builder.Append("\t\t\t<li><label><input type=\"checkbox\" name=\"sensors\" value=\"")
			.Append(WebUtility.HtmlEncode(sensor.ResourceId))
			.Append('"')
			.Append(ticked ? " checked" : string.Empty)
			.Append("><span>")
			.Append(WebUtility.HtmlEncode(sensor.LegacyToken))
			.Append("</span>");

		AppendControlBadge(builder, sensor);

		builder.Append(ValueCell(sensor, values))
			.Append("</label></li>\n");
	}

	/// <summary>
	/// The badge that explains what a sensor can be used for, if anything: driven by the Slider, or - for
	/// a control sensor the library gave no control - why it cannot be.
	/// </summary>
	private static void AppendControlBadge(StringBuilder builder, SensorIdentifier sensor)
	{
		if (sensor.Controllable)
		{
			builder.Append(
				"<span class=\"writable\" title=\"This sensor has a software control and can be driven by the built-in Slider widget.\">controllable</span>");
		}
		else if (sensor.SensorType == SensorType.Control)
		{
			builder.Append(
				"<span class=\"readonly\" title=\"This is a control sensor, but the hardware library attached no software control to it, so it can only be watched.\">read-only</span>");
		}
	}

	/// <summary>
	/// The reading the sensor is reporting, in a cell the page keeps refreshing. The cell carries the
	/// sensor's resource id rather than a position, so a reading cannot land beside the wrong sensor.
	/// </summary>
	private static string ValueCell(SensorIdentifier sensor, IReadOnlyDictionary<string, string?> values)
	{
		var value = values.GetValueOrDefault(sensor.ResourceId);
		var missing = string.IsNullOrEmpty(value);

		return new StringBuilder()
			.Append("<span class=\"value")
			.Append(missing ? " none" : string.Empty)
			.Append("\" data-value=\"")
			.Append(WebUtility.HtmlEncode(sensor.ResourceId))
			.Append("\">")
			.Append(missing ? "&mdash;" : WebUtility.HtmlEncode(value))
			.Append("</span>")
			.ToString();
	}

	private static string Milliseconds(double value) =>
		Math.Round(value, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);
}
