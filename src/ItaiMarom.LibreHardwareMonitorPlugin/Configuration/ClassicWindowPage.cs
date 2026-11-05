using System.Globalization;
using System.Net;
using System.Text;
using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using LibreHardwareMonitor.Hardware;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Configuration;

/// <summary>
/// Draws the Macro Deck 2 dialog: a tick box per sensor under its reading kind and its hardware, with
/// the polling rate and the startup elevation option underneath.
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
	private const string AutoElevateMarker = "__AUTO_ELEVATE__";
	private const string RemotesMarker = "__REMOTES__";
	private const string RemoteTemplateMarker = "__REMOTE_TEMPLATE__";
	private const string RemotesOpenMarker = "__REMOTES_OPEN__";
	private const string DefaultsMarker = "__DEFAULTS__";

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
		.remotes {
			margin: 10px 22px 0; border: 1px solid var(--line); border-radius: 8px;
			background: var(--panel);
		}
		.remotes > summary { padding: 9px 12px; font-weight: 600; }
		.remotes > summary .count { color: var(--dim); font-weight: 400; margin-left: auto; }
		.remote-body { padding: 2px 12px 12px; }
		.remote-body .hint { margin: 0 0 10px; color: var(--dim); max-width: 92ch; }
		.remote-body code {
			padding: 1px 5px; border-radius: 4px; background: var(--bg);
			font: 12px/1.4 Consolas, monospace;
		}
		.remote-row {
			display: grid; grid-template-columns: minmax(140px, 1fr) minmax(220px, 2fr) minmax(140px, 1fr) minmax(140px, 1fr) auto auto;
			gap: 8px; margin-bottom: 8px;
		}
		.remote-row input {
			padding: 7px 9px; border: 1px solid var(--line); border-radius: 6px;
			background: var(--bg); color: var(--text); font: inherit; min-width: 0;
		}
		.remote-row input:focus { outline: 2px solid var(--accent-soft); border-color: var(--accent); }
		.remote-row button.test,
		.remote-row button.remove { padding: 7px 12px; }
		.remote-row button.test { background: var(--accent-soft); border-color: var(--accent); color: var(--accent); }
		.remote-row button.test:hover { background: var(--accent); color: #fff; }
		.remote-row button.test:disabled { opacity: 0.5; cursor: not-allowed; }
		.remote-row .test-result { padding: 7px 12px; font-size: 12px; border-radius: 6px; min-width: 120px; }
		.remote-row .test-result.success { background: #1a3a2a; color: #8fd9a8; border: 1px solid #2d7d4a; }
		.remote-row .test-result.error { background: #3a1a1a; color: #f9a8a8; border: 1px solid #7d2d2d; }
		#addRemote { margin-top: 2px; }
		.defaults {
			margin: 10px 22px 0; border: 1px solid var(--line); border-radius: 8px;
			background: var(--panel);
		}
		.defaults > summary { padding: 9px 12px; font-weight: 600; }
		.default-body { padding: 2px 12px 12px; }
		.default-body .hint { margin: 0 0 10px; color: var(--dim); max-width: 92ch; }
		.default-rows {
			display: grid; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
			gap: 8px 18px;
		}
		/* The label and its selector stay together; spreading them across the column would put the
		   dropdown nearer the next kind's label than its own. */
		.default-row { display: flex; align-items: center; gap: 8px; justify-content: flex-start; }
		.default-row label { color: var(--dim); }
		.default-row select {
			padding: 5px 7px; border: 1px solid var(--line); border-radius: 5px;
			background: var(--bg); color: var(--text); font: inherit;
		}
		.default-row select:focus { outline: 2px solid var(--accent-soft); border-color: var(--accent); }
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
		li { display: flex; align-items: center; gap: 8px; margin: 1px 0; }
		li label { display: flex; align-items: center; gap: 9px; padding: 5px 10px; border-radius: 5px; cursor: pointer; flex: 1; min-width: 0; }
		.unit { flex: none; }
		.unit select {
			padding: 4px 6px; border: 1px solid var(--line); border-radius: 5px;
			background: var(--bg); color: var(--text); font: inherit;
		}
		.unit select:focus { outline: 2px solid var(--accent-soft); border-color: var(--accent); }
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
		.startup { color: var(--dim); }
		.startup label { display: flex; align-items: center; gap: 8px; cursor: pointer; white-space: nowrap; }
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
				<span id="counts"></span>
			</div>

			<details class="remotes" id="remotes"__REMOTES_OPEN__>
				<summary><span>Remote machines</span><span class="count" id="remoteCount"></span></summary>
				<div class="remote-body">
					<p class="hint">Read sensors from another PC that runs LibreHardwareMonitor with its <strong>Remote Web Server</strong> enabled. Enter <code>host:8085</code> or the full <code>http://host:8085/data.json</code> address. Remote sensors are read-only. A machine added here appears in the tree the next time this window is opened.</p>
					<div id="remoteRows">__REMOTES__</div>
					<button type="button" id="addRemote">Add remote machine</button>
					<template id="remoteTemplate">__REMOTE_TEMPLATE__</template>
				</div>
			</details>

			<details class="defaults" id="defaults" open>
				<summary><span>Default units</span></summary>
				<div class="default-body">
					<p class="hint">The unit a sensor of each kind is shown in when it has no choice of its own. Every sensor still set to <em>Default</em> follows these - including the ones the catalog offers - and a sensor's own selector overrides them.</p>
					<div class="default-rows" id="defaultRows">__DEFAULTS__</div>
				</div>
			</details>

			<div class="tree">
		__TREE__
			</div>

			<footer>
				<div class="polling">
					<label for="polling" title="How often the sensors are read, in milliseconds.">Polling rate</label>
					<input type="range" id="pollingRange" min="__POLLING_MIN__" max="__POLLING_MAX__" step="1">
					<input type="number" id="polling" name="polling" min="__POLLING_MIN__" max="__POLLING_MAX__" step="1" value="__POLLING__">
				</div>
				<div class="startup">
					<label title="Ask Windows for administrator rights each time Macro Deck starts, so the readings that need the driver are there without pressing Enable elevated readings first. Windows still shows its prompt every time, and this is only used when the driver is installed and Macro Deck is not already running as administrator.">
						<input type="checkbox" name="auto_elevate" value="1"__AUTO_ELEVATE__>
						Start elevated readings at startup
					</label>
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

			// A sensor selector that is left on "Default" follows the defaults editor above it. Changing a
			// default only rewrites the label of those selectors; a sensor with an explicit choice keeps it,
			// which is what makes the default a default rather than an override.
			var defaultSelects = [].slice.call(document.querySelectorAll("select[data-default-kind]"));

			function syncDefaultLabels(select) {
				var kind = select.getAttribute("data-default-kind");
				var symbol = select.options[select.selectedIndex].textContent;
				var units = tree.querySelectorAll('select[data-kind="' + kind + '"]');

				for (var i = 0; i < units.length; i++) {
					if (units[i].value === "" && units[i].options.length > 0) {
						units[i].options[0].textContent = "Default (" + symbol + ")";
					}
				}
			}

			for (var d = 0; d < defaultSelects.length; d++) {
				defaultSelects[d].addEventListener("change", function () {
					syncDefaultLabels(this);
					if (typeof window.fetch === "function") refreshValues();
				});
			}

			// Remote machines are rows of three fields each, so what the form posts is three parallel
			// lists. A row added here starts as a blank line; the plugin drops one it cannot turn into an
			// address, and the tree it would add to is only rebuilt the next time the window opens.
			var remotes = document.getElementById("remotes");
			var remoteRows = document.getElementById("remoteRows");
			var remoteTemplate = document.getElementById("remoteTemplate");
			var remoteCount = document.getElementById("remoteCount");

			function syncRemotes() {
				var rows = remoteRows.querySelectorAll(".remote-row").length;
				if (remoteCount) remoteCount.textContent = rows === 0 ? "none configured" : rows + " configured";
			}

			remoteRows.addEventListener("click", function (event) {
				var button = event.target.closest("button.remove");
				if (!button) return;
				button.closest(".remote-row").remove();
				syncRemotes();
			});

			document.getElementById("addRemote").addEventListener("click", function () {
				if (!remoteTemplate || !remoteTemplate.content) return;
				var row = remoteTemplate.content.firstElementChild.cloneNode(true);
				remoteRows.appendChild(row);
				remotes.open = true;
				syncRemotes();
				var url = row.querySelector('input[name="remote_url"]');
				if (url) url.focus();
			});

			syncRemotes();

			// Test connection button for remote machines
			remoteRows.addEventListener("click", function (event) {
				var button = event.target.closest("button.test");
				if (!button) return;

				var row = button.closest(".remote-row");
				var urlInput = row.querySelector('input[name="remote_url"]');
				var userInput = row.querySelector('input[name="remote_username"]');
				var passInput = row.querySelector('input[name="remote_password"]');
				var keyInput = row.querySelector('input[name="remote_key"]');
				var nameInput = row.querySelector('input[name="remote_name"]');
				var resultSpan = row.querySelector(".test-result");

				var url = urlInput?.value?.trim();
				if (!url) {
					showTestResult(resultSpan, "Enter a URL first", false);
					return;
				}

				// Generate a key from the URL if the key input is empty (for new rows not yet saved).
				// A saved row keeps its key so a rename cannot move the variables already bound to it.
				// "test" is reserved for internal dummy sources and is never used as a real key.
				var key = keyInput?.value?.trim();
				if (!key || key === "test") {
					key = generateKeyFromUrl(url);
					if (keyInput) keyInput.value = key;
				}

				button.disabled = true;
				button.textContent = "Testing…";
				showTestResult(resultSpan, "Connecting…", null);

				var sessionKey = window.location.search.slice(1).split("s=")[1]?.split("&")[0] || "";
				var testUrl = "/test-remote?s=" + encodeURIComponent(sessionKey);
				var body = new URLSearchParams();
				body.append("url", url);
				body.append("name", nameInput?.value || "");
				if (userInput?.value) body.append("username", userInput.value);
				if (passInput?.value) body.append("password", passInput.value);
				body.append("key", key);

				fetch(testUrl, { method: "POST", body: body, cache: "no-store" })
					.then(function (response) { return response.json(); })
					.then(function (data) {
						if (data.ok && data.sensors && data.sensors.length > 0) {
							// The server answers with the definition a save would build, so the branch
							// added here is laid out exactly like the one a reopened window shows.
							addRemoteSensorsToTree(
								data.key || keyInput?.value || "remote",
								data.name || nameInput?.value || url,
								data.sensors
							);
							showTestResult(resultSpan, "Connected - " + data.sensorCount + " sensors added", true);
						} else {
							showTestResult(resultSpan, data.message || (data.ok ? "Connected" : "Failed"), data.ok);
						}
					})
					.catch(function (err) {
						showTestResult(resultSpan, "Error: " + err.message, false);
					})
					.finally(function () {
						button.disabled = false;
						button.textContent = "Test";
					});
			});

			function showTestResult(span, message, ok) {
				if (!span) return;
				span.textContent = message;
				span.className = "test-result" + (ok === true ? " success" : ok === false ? " error" : "");
			}

			// Add remote sensors to the sensor tree
			function addRemoteSensorsToTree(remoteKey, remoteName, sensors) {
				// Check if this remote already has a branch in the tree
				var existingBranch = tree.querySelector('[data-remote-key="' + remoteKey + '"]');
				if (existingBranch) {
					// Remove existing sensors for this remote
					existingBranch.remove();
				}

				// Group sensors by hardware path, then by type
				var byHardware = {};
				for (var i = 0; i < sensors.length; i++) {
					var s = sensors[i];
					var hwPath = s.hardwarePath || s.hardware;
					if (!byHardware[hwPath]) {
						byHardware[hwPath] = { name: s.hardware, path: hwPath, kinds: {} };
					}
					if (!byHardware[hwPath].kinds[s.type]) {
						byHardware[hwPath].kinds[s.type] = [];
					}
					byHardware[hwPath].kinds[s.type].push(s);
				}

				// Build the HTML for the remote machine branch
				var html = '<details data-remote-key="' + remoteKey + '" open>' +
					'<summary><input type="checkbox"><span class="name machine">' + escapeHtml(remoteName) + '</span>' +
					'<span class="count">0/' + sensors.length + '</span></summary>';

				for (var hwPath in byHardware) {
					if (!byHardware.hasOwnProperty(hwPath)) continue;
					var hw = byHardware[hwPath];
					var hwSensorCount = 0;
					for (var kind in hw.kinds) hwSensorCount += hw.kinds[kind].length;

					html += '<details><summary><input type="checkbox"><span class="name ' + 'root">' + escapeHtml(hw.name) + '</span>';
					if (hwSensorCount > 0) html += '<span class="cont' + 'ains" title="A sensor below can be driven by the built-in Slider widget.">control below</span>';
					html += '<span class="count">0/' + hwSensorCount + '</span></summary>';

					for (var kind in hw.kinds) {
						if (!hw.kinds.hasOwnProperty(kind)) continue;
						var kindSensors = hw.kinds[kind];
						html += '<details><summary><input type="checkbox"><span class="name kind">' + escapeHtml(kind) + '</span>';
						html += '<span class="count">0/' + kindSensors.length + '</span></summary>';
						html += '<ul>';

						for (var j = 0; j < kindSensors.length; j++) {
							var s = kindSensors[j];
							var valueDisplay = s.value ? escapeHtml(s.value) : "&mdash;";
							var valueClass = s.value ? "value" : "value none";
							var controllableBadge = s.controllable ?
								'<span class="writ' + 'able" title="This sensor has a software control and can be driven by the built-in Slider widget.">controllable</span>' : '';
							html += '<li><label><input type="checkbox" name="sensors" value="' + escapeHtml(s.resourceId) + '">' +
								'<span>' + escapeHtml(s.name + '_' + s.type) + '</span>' +
								controllableBadge +
								'<span class="' + valueClass + '" data-value="' + escapeHtml(s.resourceId) + '">' + valueDisplay + '</span>' +
								'</label></li>';
						}

						html += '</ul></details>';
					}

					html += '</details>';
				}

				html += '</details>';

				// Insert before the closing </div> of .tree
				tree.insertAdjacentHTML('beforeend', html);

				// Re-initialize groups and boxes for the new elements
				initializeTreeElements();
			}

			function escapeHtml(text) {
				var div = document.createElement('div');
				div.textContent = text;
				return div.innerHTML;
			}

			// Generate a key from a URL, matching the server's RemoteSourceDefinition.NormalizeKey logic
			function generateKeyFromUrl(url) {
				// Remove protocol
				var clean = url.replace(/^https?:\/\//i, '');
				// Remove path/data.json
				clean = clean.replace(/\/data\.json$/i, '').replace(/\/$/, '');
				// Replace non-alphanumeric with underscore, lowercase
				var key = clean.replace(/[^A-Za-z0-9]/g, '_').toLowerCase();
				// Trim to 32 chars, trim trailing underscores
				if (key.length > 32) key = key.substring(0, 32).replace(/_+$/, '');
				// "test" is reserved for internal test dummy sources; never allow it as a real remote key
				if (key === "test") key = "remote";
				return key || 'remote';
			}

			// Re-initialize tree elements (groups, boxes, event listeners) after adding remote sensors
			function initializeTreeElements() {
				boxes = [].slice.call(tree.querySelectorAll('input[name="sensors"]'));
				groups = [].slice.call(tree.querySelectorAll("details")).map(function (details) {
					return {
						details: details,
						box: details.querySelector("summary input"),
						sensors: [].slice.call(details.querySelectorAll('input[name="sensors"]')),
					};
				});

				// Re-attach event listeners for new group checkboxes
				for (var g = 0; g < groups.length; g++) {
					var group = groups[g];
					if (group.box && !group.box.dataset.listenerAttached) {
						group.box.dataset.listenerAttached = "true";
						group.box.addEventListener("click", function (event) { event.stopPropagation(); });
						group.box.addEventListener("change", function () {
							for (var i = 0; i < group.sensors.length; i++) {
								group.sensors[i].checked = group.box.checked;
							}
							syncAll();
						});
					}
					for (var i = 0; i < group.sensors.length; i++) {
						if (!group.sensors[i].dataset.listenerAttached) {
							group.sensors[i].dataset.listenerAttached = "true";
							group.sensors[i].addEventListener("change", syncAll);
						}
					}
				}

				syncAll();
			}

			// The readings are asked for once a second and written into the cell of the sensor they belong
			// to. Only those cells are touched, so nothing the user has ticked, opened or typed moves under
			// them, and a cell is never matched by position: it carries the sensor's resource id.
			var cells = [].slice.call(tree.querySelectorAll("[data-value]"));
			console.log("[LHM] Found", cells.length, "value cells");
			var valuesUrl = "__VALUES__";

			// The readings are asked for in the defaults the editor is showing, not the ones the plugin
			// saved, so choosing a default updates the value column immediately instead of after Save.
			function defaultsQuery() {
				var query = "";
				for (var i = 0; i < defaultSelects.length; i++) {
					query += "&default_unit_kind=" + encodeURIComponent(defaultSelects[i].getAttribute("data-default-kind"));
					query += "&default_unit_value=" + encodeURIComponent(defaultSelects[i].value);
				}
				console.log("[LHM] defaultsQuery:", query);
				return query;
			}

			// Per-sensor unit selectors also affect the value column. When a user changes one from an
			// explicit unit back to Default, the live refresh should reflect the default immediately.
			function unitsQuery() {
				var query = "";
				var unitSelects = tree.querySelectorAll('select[name="unit_value"]');
				console.log("[LHM] Found", unitSelects.length, "unit selectors");
				for (var i = 0; i < unitSelects.length; i++) {
					var select = unitSelects[i];
					var sensorId = select.previousElementSibling?.value; // hidden unit_sensor input
					console.log("[LHM] Selector", i, "sensorId:", sensorId, "value:", select.value);
					if (sensorId) {
						query += "&unit_sensor=" + encodeURIComponent(sensorId);
						query += "&unit_value=" + encodeURIComponent(select.value);
					}
				}
				return query;
			}

			function refreshValues() {
				if (document.hidden) return;

				var url = valuesUrl + defaultsQuery() + unitsQuery();
				console.log("[LHM] Fetching:", url);
				fetch(url, { cache: "no-store" })
					.then(function (response) {
						console.log("[LHM] Response:", response.status, response.ok);
						return response.ok ? response.json() : null;
					})
					.then(function (values) {
						console.log("[LHM] Values:", values);
						if (!values) return;

						for (var i = 0; i < cells.length; i++) {
							var cell = cells[i];
							var value = values[cell.getAttribute("data-value")];
							var missing = value === null || value === undefined || value === "";
							cell.textContent = missing ? "\u2014" : value;
							cell.classList.toggle("none", missing);
						}
					})
					.catch(function (err) {
						console.log("[LHM] Fetch error:", err);
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
			.Replace(PollingMarker, Milliseconds(opening.PollingMilliseconds), StringComparison.Ordinal)
			.Replace(AutoElevateMarker, opening.AutoElevate ? " checked" : string.Empty, StringComparison.Ordinal)
			.Replace(RemotesOpenMarker, opening.RemoteSources is { Count: > 0 } ? " open" : string.Empty, StringComparison.Ordinal)
			.Replace(RemotesMarker, RenderRemotes(opening), StringComparison.Ordinal)
			.Replace(RemoteTemplateMarker, RenderRemoteRow(null), StringComparison.Ordinal)
			.Replace(DefaultsMarker, RenderDefaults(opening), StringComparison.Ordinal);
	}

	/// <summary>
	/// The defaults editor: one row per kind that offers a unit choice. The kind travels in a hidden field
	/// beside its selector, so the two lists the form posts stay aligned whatever the order of the rows.
	/// </summary>
	private static string RenderDefaults(ClassicWindowOpening opening)
	{
		var builder = new StringBuilder();

		foreach (var sensorType in SensorUnits.SupportedKinds)
		{
			var chosen = SensorUnits.DefaultFor(sensorType, opening.UnitDefaults);
			var label = SensorUnits.Label(sensorType);

			builder.Append("<div class=\"default-row\"><label for=\"default_")
				.Append(sensorType)
				.Append("\">")
				.Append(WebUtility.HtmlEncode(label))
				.Append("</label><input type=\"hidden\" name=\"default_unit_kind\" value=\"")
				.Append(sensorType)
				.Append("\"><select name=\"default_unit_value\" id=\"default_")
				.Append(sensorType)
				.Append("\" aria-label=\"Default unit for ")
				.Append(WebUtility.HtmlEncode(label))
				.Append("\" data-default-kind=\"")
				.Append(sensorType)
				.Append("\">");

			foreach (var option in SensorUnits.OptionsFor(sensorType))
			{
				builder.Append("<option value=\"")
					.Append(option.ToString())
					.Append('"')
					.Append(option == chosen ? " selected" : string.Empty)
					.Append('>')
					.Append(WebUtility.HtmlEncode(SensorUnits.Symbol(option)))
					.Append("</option>");
			}

			builder.Append("</select></div>");
		}

		return builder.ToString();
	}

	/// <summary>
	/// The remote machines the window is configured with, one row each. The key travels in a hidden
	/// field: it is what variable names and sensor identities were built from, and a rename must not
	/// move either.
	/// </summary>
	private static string RenderRemotes(ClassicWindowOpening opening)
	{
		var remotes = opening.RemoteSources;
		if (remotes is not { Count: > 0 })
		{
			return string.Empty;
		}

		var builder = new StringBuilder();
		foreach (var remote in remotes)
		{
			builder.Append(RenderRemoteRow(remote));
		}

		return builder.ToString();
	}

	/// <summary>One row of the remote editor, and the blank row the page clones when one is added.</summary>
	private static string RenderRemoteRow(RemoteSourceDefinition? remote)
	{
		return new StringBuilder()
			.Append("<div class=\"remote-row\"><input type=\"text\" name=\"remote_name\" placeholder=\"Name (optional)\" aria-label=\"Remote machine name\" value=\"")
			.Append(WebUtility.HtmlEncode(remote?.Name ?? string.Empty))
			.Append("\"><input type=\"text\" name=\"remote_url\" placeholder=\"host:8085 or http://host:8085/data.json\" aria-label=\"Remote machine address\" value=\"")
			.Append(WebUtility.HtmlEncode(remote?.Url ?? string.Empty))
			.Append("\"><input type=\"text\" name=\"remote_username\" placeholder=\"User (optional)\" aria-label=\"Remote machine username\" value=\"")
			.Append(WebUtility.HtmlEncode(remote?.UserName ?? string.Empty))
			.Append("\"><input type=\"password\" name=\"remote_password\" placeholder=\"Password (optional)\" aria-label=\"Remote machine password\" autocomplete=\"current-password\" value=\"")
			.Append(WebUtility.HtmlEncode(remote?.Password ?? string.Empty))
			.Append("\"><input type=\"hidden\" name=\"remote_key\" value=\"")
			.Append(WebUtility.HtmlEncode(remote?.Key ?? string.Empty))
			.Append("\"><button type=\"button\" class=\"test\" title=\"Test connection to this remote machine\">Test</button><button type=\"button\" class=\"remove\" title=\"Remove this remote machine\">Remove</button><span class=\"test-result\" aria-live=\"polite\"></span></div>\n")
			.ToString();
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
		var units = opening.DisplayUnits;
		var defaults = opening.UnitDefaults;
		var builder = new StringBuilder();

		// This machine keeps the old dialog's shape: a root, its reading kinds, its sensors.
		foreach (var root in categories
			.Where(category => category.Source is null)
			.GroupBy(category => category.RootName, StringComparer.Ordinal))
		{
			AppendHardwareRoot(builder, root.Key, root, selected, values, units, defaults);
		}

		// Each remote machine is one top-level branch, and its hardware sits underneath it rather than
		// beside this machine's. The root the library names is still there - it is what a user opens to
		// reach a processor's temperatures - but the machine is the container that keeps two PCs apart.
		foreach (var machine in categories
			.Where(category => category.Source is not null)
			.GroupBy(category => category.Source!, StringComparer.Ordinal))
		{
			var machineSensors = machine.SelectMany(category => category.Sensors).ToArray();
			var machineTicked = machineSensors.Count(sensor => selected.Contains(sensor.ResourceId));

			AppendOpen(
				builder,
				"machine",
				machine.First().MachineName ?? machine.Key,
				machineTicked,
				machineSensors.Length,
				machineSensors.Any(sensor => sensor.Controllable),
				machine.Key);

			foreach (var root in machine.GroupBy(category => category.RootName, StringComparer.Ordinal))
			{
				AppendHardwareRoot(builder, root.Key, root, selected, values, units, defaults);
			}

			builder.Append("\t</details>\n");
		}

		return builder.ToString();
	}

	/// <summary>
	/// One piece of hardware and the reading kinds under it. It is the same shape for this machine and for
	/// a remote one; only what encloses it differs.
	/// </summary>
	private static void AppendHardwareRoot(
		StringBuilder builder,
		string name,
		IEnumerable<SensorCategory> categories,
		HashSet<string> selected,
		IReadOnlyDictionary<string, string?> values,
		IReadOnlyDictionary<string, SensorUnit>? units,
		IReadOnlyDictionary<SensorType, SensorUnit>? defaults)
	{
		var root = categories.ToArray();
		var rootSensors = root.SelectMany(category => category.Sensors).ToArray();
		var rootTicked = rootSensors.Count(sensor => selected.Contains(sensor.ResourceId));

		AppendOpen(
			builder,
			"root",
			name,
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
				var unit = units is not null && units.TryGetValue(sensor.ResourceId, out var chosen)
					? chosen
					: sensor.DisplayUnit;

				AppendSensor(builder, sensor, selected.Contains(sensor.ResourceId), values, unit, defaults);
			}

			builder.Append("\t\t\t</ul>\n\t\t</details>\n");
		}

		builder.Append("\t</details>\n");
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
		bool containsControl,
		string? remoteKey = null)
	{
		builder.Append(ticked > 0 ? "\t<details open" : "\t<details")
			.Append(remoteKey is null ? ">" : $" data-remote-key=\"{WebUtility.HtmlEncode(remoteKey)}\">")
			.Append('\n')
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
		IReadOnlyDictionary<string, string?> values,
		SensorUnit? unit,
		IReadOnlyDictionary<SensorType, SensorUnit>? defaults)
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
			.Append("</label>");

		AppendUnitSelector(builder, sensor, unit, defaults);

		builder.Append("</li>\n");
	}

	/// <summary>
	/// The unit selector for a kind that offers more than one. The sensor's resource id travels in the
	/// hidden field next to it, so the two lists the form posts stay aligned even where a kind - a fan
	/// speed, a voltage - has no selector at all.
	/// </summary>
	/// <remarks>
	/// An empty value means "follow the default for this kind", which is what a sensor that never made a
	/// choice posts. That is deliberately different from posting the default's own unit: if Save stored
	/// the default as an explicit choice, changing the default later would leave the sensor behind.
	/// </remarks>
	private static void AppendUnitSelector(
		StringBuilder builder,
		SensorIdentifier sensor,
		SensorUnit? unit,
		IReadOnlyDictionary<SensorType, SensorUnit>? defaults)
	{
		var options = SensorUnits.OptionsFor(sensor.SensorType);
		if (options.Count == 0)
		{
			return;
		}

		var defaultUnit = SensorUnits.DefaultFor(sensor.SensorType, defaults);
		var chosen = unit is { } candidate && SensorUnits.IsValidFor(sensor.SensorType, candidate)
			? candidate
			: (SensorUnit?)null;

		builder.Append("<span class=\"unit\"><input type=\"hidden\" name=\"unit_sensor\" value=\"")
			.Append(WebUtility.HtmlEncode(sensor.ResourceId))
			.Append("\"><select name=\"unit_value\" aria-label=\"Unit for ")
			.Append(WebUtility.HtmlEncode(sensor.LegacyToken))
			.Append("\" data-kind=\"")
			.Append(sensor.SensorType)
			.Append("\"><option value=\"\"")
			.Append(chosen is null ? " selected" : string.Empty)
			.Append(">Default (")
			.Append(WebUtility.HtmlEncode(SensorUnits.Symbol(defaultUnit)))
			.Append(")</option>");

		foreach (var option in options)
		{
			builder.Append("<option value=\"")
				.Append(option.ToString())
				.Append('"')
				.Append(option == chosen ? " selected" : string.Empty)
				.Append('>')
				.Append(WebUtility.HtmlEncode(SensorUnits.Symbol(option)))
				.Append("</option>");
		}

		builder.Append("</select></span>");
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



