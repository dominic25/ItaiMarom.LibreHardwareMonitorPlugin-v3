// Rebuilds the two sensor-window mockups from the real page template in
// ClassicWindowPage.cs, so the CSS and markup cannot drift from the plugin.
//
// The tree, counts and readings are example data on purpose: the window is a
// live form, and a store screenshot should show it populated. Everything else -
// stylesheet, row markup, badges, footer - comes from the plugin source.

import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const repo = join(here, "..", "..");
const pagePath = join(
  repo,
  "src",
  "ItaiMarom.LibreHardwareMonitorPlugin",
  "Configuration",
  "ClassicWindowPage.cs",
);

const source = readFileSync(pagePath, "utf8");
const startMarker = 'private const string Template = """';
const start = source.indexOf(startMarker);
if (start < 0) throw new Error("Template not found in ClassicWindowPage.cs");
const bodyStart = source.indexOf("\n", start) + 1;
const end = source.indexOf('\n\t\t""";', bodyStart);
if (end < 0) throw new Error("Template end not found in ClassicWindowPage.cs");

let template = source
  .slice(bodyStart, end)
  .split(/\r?\n/)
  .map((line) => (line.startsWith("\t\t") ? line.slice(2) : line))
  .join("\n");

const css = template.match(/<style>([\s\S]*?)<\/style>/)[1].trim();
const body = template
  .replace(/<script>[\s\S]*?<\/script>\s*/, "")
  .replace(/<style>[\s\S]*?<\/style>\s*/, "");

// --- example data -----------------------------------------------------------

const UNIT_OPTIONS = {
  Temperature: [
    ["", "Default (°C)"],
    ["Celsius", "°C"],
    ["Fahrenheit", "°F"],
  ],
  Data: [
    ["", "Default (GB)"],
    ["Gigabytes", "GB"],
    ["Gibibytes", "GiB"],
  ],
  SmallData: [
    ["", "Default (MB)"],
    ["Megabytes", "MB"],
    ["Mebibytes", "MiB"],
  ],
  Clock: [
    ["", "Default (MHz)"],
    ["Megahertz", "MHz"],
    ["Gigahertz", "GHz"],
  ],
  Throughput: [
    ["", "Default (B/s)"],
    ["BytesPerSecond", "B/s"],
    ["KilobytesPerSecond", "KB/s"],
    ["MegabytesPerSecond", "MB/s"],
  ],
};

function esc(text) {
  return String(text)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;");
}

function unitSelect(unitId, label, kind) {
  const options = UNIT_OPTIONS[kind];
  if (!options) return "";
  return (
    '<span class="unit"><input type="hidden" name="unit_sensor" value="' +
    esc(unitId) +
    '"><select name="unit_value" aria-label="Unit for ' +
    esc(label) +
    '" data-kind="' +
    kind +
    '">' +
    options
      .map(
        ([value, text], index) =>
          `<option value="${value}"${index === 0 ? " selected" : ""}>${text}</option>`,
      )
      .join("") +
    "</select></span>"
  );
}

function sensor({ label, id, kind, value, checked = false, controllable = false, readOnly = false }) {
  const badge = controllable
    ? '<span class="writable" title="This sensor has a software control and can be driven by the built-in Slider widget.">controllable</span>'
    : readOnly
      ? '<span class="readonly" title="This is a control sensor, but the hardware library attached no software control to it, so it can only be watched.">read-only</span>'
      : "";
  return (
    '<li><label><input type="checkbox" name="sensors" value="' +
    esc(id) +
    '"' +
    (checked ? " checked" : "") +
    "><span>" +
    esc(label) +
    "</span>" +
    badge +
    '<span class="value" data-value="' +
    esc(id) +
    '">' +
    esc(value) +
    "</span></label>" +
    unitSelect(id, label, kind) +
    "</li>"
  );
}

function branch({ kind, name, sensors, open, containing }) {
  const ticked = sensors.filter((s) => s.checked).length;
  const control = containing ?? sensors.some((s) => s.controllable);
  return (
    "<details" +
    (open ? " open" : "") +
    "><summary><input type=\"checkbox\"><span class=\"name " +
    kind +
    '">' +
    esc(name) +
    "</span>" +
    (control
      ? '<span class="contains" title="A sensor below can be driven by the built-in Slider widget.">control below</span>'
      : "") +
    '<span class="count">' +
    ticked +
    "/" +
    sensors.length +
    "</span></summary>" +
    (kind === "kind"
      ? "<ul>" + sensors.map((s) => sensor(s)).join("") + "</ul>"
      : sensors.map((s) => s.html ?? "").join("")) +
    "</details>"
  );
}

function hardware(name, kinds) {
  const all = kinds.flatMap((k) => k.sensors);
  const ticked = all.filter((s) => s.checked).length;
  const control = all.some((s) => s.controllable);
  const inner = kinds
    .map((k) => branch({ kind: "kind", name: k.type, sensors: k.sensors, open: k.open, containing: k.sensors.some((s) => s.controllable) }))
    .join("");
  return (
    "<details" +
    (ticked > 0 ? " open" : "") +
    '><summary><input type="checkbox"><span class="name root">' +
    esc(name) +
    "</span>" +
    (control
      ? '<span class="contains" title="A sensor below can be driven by the built-in Slider widget.">control below</span>'
      : "") +
    '<span class="count">' +
    ticked +
    "/" +
    all.length +
    "</span></summary>" +
    inner +
    "</details>"
  );
}

function kind(type, sensors, open = sensors.some((s) => s.checked)) {
  return { type, sensors, open };
}

// Local machine: processor, graphics card, board sensor chip, SSD.
const cpu = hardware("AMD Ryzen 9 5900X", [
  kind("Temperature", [
    { label: "CPU Core #1_Temperature", id: "/amdcpu/0/temperature/0", kind: "Temperature", value: "61.0 °C", checked: true },
    { label: "CPU Core #2_Temperature", id: "/amdcpu/0/temperature/1", kind: "Temperature", value: "58.0 °C" },
    { label: "CPU (Tctl/Tdie)_Temperature", id: "/amdcpu/0/temperature/2", kind: "Temperature", value: "65.2 °C" },
    { label: "CPU CCD1 (Tdie)_Temperature", id: "/amdcpu/0/temperature/3", kind: "Temperature", value: "58.8 °C" },
  ]),
  kind("Load", [
    { label: "CPU Total_Load", id: "/amdcpu/0/load/0", kind: "Load", value: "14.0 %", checked: true },
    { label: "CPU Core Max_Load", id: "/amdcpu/0/load/1", kind: "Load", value: "57.0 %" },
  ]),
  kind("Clock", [
    { label: "CPU Core #1_Clock", id: "/amdcpu/0/clock/0", kind: "Clock", value: "4,400.0 MHz" },
    { label: "CPU Core #2_Clock", id: "/amdcpu/0/clock/1", kind: "Clock", value: "4,400.0 MHz" },
  ], false),
  kind("Power", [{ label: "Package_Power", id: "/amdcpu/0/power/0", kind: "Power", value: "88.4 W" }], false),
  kind("Voltage", [{ label: "CPU Core_Voltage", id: "/amdcpu/0/voltage/0", kind: "Voltage", value: "1.244 V" }], false),
]);

const gpu = hardware("NVIDIA GeForce RTX 4070", [
  kind("Temperature", [
    { label: "GPU Core_Temperature", id: "/gpu-nvidia/0/temperature/0", kind: "Temperature", value: "44.0 °C", checked: true },
    { label: "GPU Hot Spot_Temperature", id: "/gpu-nvidia/0/temperature/1", kind: "Temperature", value: "55.0 °C" },
    { label: "GPU Memory Junction_Temperature", id: "/gpu-nvidia/0/temperature/2", kind: "Temperature", value: "62.0 °C" },
  ]),
  kind("Load", [
    { label: "GPU Core_Load", id: "/gpu-nvidia/0/load/0", kind: "Load", value: "3.0 %", checked: true },
    { label: "GPU Memory_Load", id: "/gpu-nvidia/0/load/1", kind: "Load", value: "7.0 %" },
  ]),
  kind("SmallData", [
    { label: "GPU Memory Free_SmallData", id: "/gpu-nvidia/0/smalldata/0", kind: "SmallData", value: "11,575.0 MB" },
    { label: "GPU Memory Used_SmallData", id: "/gpu-nvidia/0/smalldata/1", kind: "SmallData", value: "4,800.0 MB", checked: true },
    { label: "GPU Memory Total_SmallData", id: "/gpu-nvidia/0/smalldata/2", kind: "SmallData", value: "16,376.0 MB" },
  ]),
  kind("Control", [
    { label: "GPU Fan_Control", id: "/gpu-nvidia/0/control/0", kind: "Control", value: "0.0 %", readOnly: true },
  ], false),
  kind("Fan", [{ label: "GPU Fan_Fan", id: "/gpu-nvidia/0/fan/0", kind: "Fan", value: "0 RPM" }], false),
]);

const board = hardware("Nuvoton NCT6798D", [
  kind("Fan", [
    { label: "Fan #1_Fan", id: "/lpc/nct6798d/0/fan/0", kind: "Fan", value: "1,220 RPM" },
    { label: "Fan #2_Fan", id: "/lpc/nct6798d/0/fan/1", kind: "Fan", value: "980 RPM" },
    { label: "Fan #3_Fan", id: "/lpc/nct6798d/0/fan/2", kind: "Fan", value: "1,100 RPM" },
  ], false),
  kind("Control", [
    { label: "Fan #1_Control", id: "/lpc/nct6798d/0/control/0", kind: "Control", value: "64.0 %", checked: true, controllable: true },
    { label: "Fan #2_Control", id: "/lpc/nct6798d/0/control/1", kind: "Control", value: "50.0 %", controllable: true },
    { label: "Fan #3_Control", id: "/lpc/nct6798d/0/control/2", kind: "Control", value: "45.0 %", controllable: true },
  ]),
  kind("Temperature", [{ label: "System_Temperature", id: "/lpc/nct6798d/0/temperature/0", kind: "Temperature", value: "34.0 °C" }], false),
  kind("Voltage", [
    { label: "+12V_Voltage", id: "/lpc/nct6798d/0/voltage/0", kind: "Voltage", value: "12.096 V" },
    { label: "+5V_Voltage", id: "/lpc/nct6798d/0/voltage/1", kind: "Voltage", value: "5.040 V" },
  ], false),
]);

const ssd = hardware("Samsung SSD 990 PRO 2TB", [
  kind("Temperature", [
    { label: "Temperature_Temperature", id: "/nvme/0/temperature/0", kind: "Temperature", value: "38.0 °C", checked: true },
  ]),
  kind("Data", [
    { label: "Used Space_Data", id: "/nvme/0/data/0", kind: "Data", value: "512.4 GB", checked: true },
    { label: "Free Space_Data", id: "/nvme/0/data/1", kind: "Data", value: "1,419.1 GB" },
  ]),
  kind("Level", [{ label: "Life_Level", id: "/nvme/0/level/0", kind: "Level", value: "99.0 %" }], false),
]);

const tree = board + cpu + gpu + ssd;
const selected = 8;
const total = 34;

function defaultsRow(kind, label, options) {
  const units = options.filter(([value]) => value !== "");
  return (
    '<div class="default-row"><label for="default_' +
    kind +
    '">' +
    label +
    '</label><input type="hidden" name="default_unit_kind" value="' +
    kind +
    '"><select name="default_unit_value" id="default_' +
    kind +
    '" aria-label="Default unit for ' +
    label +
    '" data-default-kind="' +
    kind +
    '">' +
    units
      .map(
        ([value, text], index) =>
          `<option value="${value}"${index === 0 ? " selected" : ""}>${text}</option>`,
      )
      .join("") +
    "</select></div>"
  );
}

const defaults =
  defaultsRow("Temperature", "Temperature", UNIT_OPTIONS.Temperature) +
  defaultsRow("Data", "Data", UNIT_OPTIONS.Data) +
  defaultsRow("SmallData", "Small data", UNIT_OPTIONS.SmallData) +
  defaultsRow("Clock", "Clock", UNIT_OPTIONS.Clock) +
  defaultsRow("Throughput", "Throughput", UNIT_OPTIONS.Throughput);

function windowPage({ tree, counts, remotesOpen, remotes, template, defaultsOpen }) {
  return body
    .replace(
      '<meta charset="utf-8">',
      '<meta charset="utf-8"><style>' +
        css +
        "\nhtml{scrollbar-width:none}::-webkit-scrollbar{display:none}</style>",
    )
    .replace('id="defaults" open', 'id="defaults"' + (defaultsOpen ? " open" : ""))
    .replaceAll("__ACTION__", "#")
    .replaceAll("__TITLE__", "Libre Hardware Monitor")
    .replaceAll("__NOTICE__", "")
    .replaceAll("__TREE__", tree)
    .replaceAll("__VALUES__", "#")
    .replaceAll("__POLLING__", "1000")
    .replaceAll("__POLLING_MIN__", "10")
    .replaceAll("__POLLING_MAX__", "10000")
    .replaceAll("__AUTO_ELEVATE__", " checked")
    .replaceAll("__RESTORE_FANS__", " checked")
    .replaceAll("__ELEVATION_STATUS__", "")
    .replaceAll("__REMOTES_OPEN__", remotesOpen ? " open" : "")
    .replaceAll("__REMOTES__", remotes)
    .replaceAll("__REMOTE_TEMPLATE__", template)
    .replaceAll("__DEFAULTS__", defaults)
    .replace('id="counts"></span>', 'id="counts">' + counts + "</span>");
}

const localPage = windowPage({
  tree,
  counts: selected + " of " + total + " selected",
  remotesOpen: false,
  remotes: "",
  template: "",
  defaultsOpen: false,
});

writeFileSync(join(here, "sensor-window.html"), localPage);

// --- remote close-up --------------------------------------------------------

function remoteSensor(label, id, kind, value, checked) {
  return sensor({ label, id, kind, value, checked });
}

const remoteMachine =
  '<details open data-remote-key="livingroom"><summary><input type="checkbox">' +
  '<span class="name machine">Living room</span><span class="count">3/16</span></summary>' +
  '<details open><summary><input type="checkbox"><span class="name root">AMD Ryzen 7 5800X</span>' +
  '<span class="count">3/8</span></summary>' +
  '<details open><summary><input type="checkbox"><span class="name kind">Temperature</span><span class="count">2/4</span></summary><ul>' +
  remoteSensor("CPU Core #1_Temperature", "/remote/livingroom/amdcpu/0/temperature/0", "Temperature", "62.0 °C", true) +
  remoteSensor("CPU Core #2_Temperature", "/remote/livingroom/amdcpu/0/temperature/1", "Temperature", "59.0 °C", true) +
  remoteSensor("CPU (Tctl/Tdie)_Temperature", "/remote/livingroom/amdcpu/0/temperature/2", "Temperature", "66.4 °C", false) +
  remoteSensor("CPU CCD1 (Tdie)_Temperature", "/remote/livingroom/amdcpu/0/temperature/3", "Temperature", "60.1 °C", false) +
  "</ul></details>" +
  '<details open><summary><input type="checkbox"><span class="name kind">Load</span><span class="count">1/2</span></summary><ul>' +
  remoteSensor("CPU Total_Load", "/remote/livingroom/amdcpu/0/load/0", "Load", "21.0 %", true) +
  remoteSensor("CPU Core Max_Load", "/remote/livingroom/amdcpu/0/load/1", "Load", "68.0 %", false) +
  "</ul></details>" +
  '<details><summary><input type="checkbox"><span class="name kind">Clock</span><span class="count">0/2</span></summary><ul></ul></details>' +
  "</details>" +
  '<details><summary><input type="checkbox"><span class="name root">NVIDIA GeForce RTX 3060</span>' +
  '<span class="count">0/8</span></summary></details>' +
  "</details>";

const remotePage = `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<style>${css}
html, body { height: auto; }
body { padding: 16px 20px 22px; }
.remotes { margin: 0 0 2px; }
.tree { overflow: visible; padding: 8px 0 0; }
</style>
</head>
<body>
<details class="remotes" id="remotes">
  <summary><span>Remote machines</span><span class="count" id="remoteCount">1 configured</span></summary>
</details>
<div class="tree">
${remoteMachine}
</div>
</body>
</html>`;

writeFileSync(join(here, "remote-window.html"), remotePage);

console.log("wrote sensor-window.html and remote-window.html");
