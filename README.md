# DeskMon

A small always-visible desktop widget showing CPU / GPU / VRAM / RAM usage and
temperatures. Sits behind your normal windows, above the wallpaper — like a
stripped-down Task Manager glued to the desktop.

```
┌────────────────────────────────┐
│ CPU  ▁▂▃▅▃▂▁▁    10%      n/a  │
│ GPU  ▁▁▁▂▁▁▁▁    27%      45°  │
│ VRM  ▂▂▂▂▂▂▂▂        3.5/20.0G │
│ RAM  ▄▄▄▄▅▄▄▄       15.5/31.9G │
└────────────────────────────────┘
```

## Before you change anything

Aimed at whoever picks this up next, human or AI. Three rules, each of which looks
like an obvious improvement and is actually a regression. The reasoning is in the
sections below.

1. **Do not "update" to LibreHardwareMonitorLib 0.9.4 or older.** Those versions embed
   **WinRing0** (CVE-2020-14979) — Microsoft Defender quarantines it, Vanguard and Easy
   Anti-Cheat block it. 0.9.5+ dropped it. This is the entire design premise and it is
   invisible from the code alone. Pinning to 0.9.6 is deliberate, not staleness.

2. **Do not add `requireAdministrator` to `app.manifest`.** It does not work. Measured on
   AMD hardware: running elevated, Ryzen Tctl/Tdie still reads exactly `0`. Elevation
   buys nothing and costs the no-UAC property that makes logon autostart a plain
   shortcut instead of a scheduled task.

3. **Do not drop `WS_EX_NOACTIVATE` or `SWP_NOACTIVATE`.** They stop a click or drag on
   the widget from taking foreground, which is what keeps it from knocking a
   borderless-fullscreen game out of fullscreen.

Two smaller invariants worth keeping:

- **A sensor reading of exactly `0` is missing data, not a measurement.** Tctl and
  package power both report `0` when there is no ring-0 path, and Afterburner publishes
  `0` for sources it lists but cannot read. Rendering that as `0°` looks completely
  plausible and is completely wrong. `HardwareMonitor.Positive()` is the rule.
- **Shared-memory readers must release the handle when the section goes stale.** A
  Windows named section lives as long as any handle to it, so holding a dead one pins
  the name and a restarting publisher asking for a larger section gets handed the stale
  smaller one. See `NamedSection.Detach()`.

**Platform:** Windows only. `net8.0-windows`, WinForms, Win32 interop. It will not build
on macOS or Linux — that is not a fixable configuration issue. Needs the .NET SDK (8 or
newer) and internet access for the first NuGet restore.

## No kernel driver, no admin, no anti-cheat risk

This is the important design decision, and it was made from measurement on this
machine rather than assumption.

Most hardware monitors (HWiNFO, FanControl, OpenRGB, older LibreHardwareMonitor,
MSI Afterburner) reach ring 0 through **WinRing0** — driver version 1.2.0.5, signed
by a certificate that expired in 2008. It is:

- **CVE-2020-14979** — arbitrary MSR and I/O port read/write from user mode, a local
  privilege-escalation primitive
- **quarantined by Microsoft Defender** as `VulnerableDriver:WinNT/Winring0` /
  `HackTool:Win32/Winring0`
- **blocked by Riot Vanguard**, and **not whitelisted by Easy Anti-Cheat** —
  LibreHardwareMonitor has a standing EAC false-positive issue because of it

DeskMon uses **LibreHardwareMonitorLib 0.9.6**, the first line that dropped WinRing0
entirely in favour of PawnIO. It ships **no kernel driver** and runs **unelevated**.

What that costs, measured on this box: **nothing except Ryzen CPU temperature.**

| Reading | Without any driver |
|---|---|
| GPU core / hot spot / memory temp | works (user-mode `atiadlxx.dll`) |
| GPU load, VRAM used/total, GPU power | works |
| CPU load, per-core load | works (performance counters) |
| RAM used / total / load | works |
| **CPU temperature (Tctl/Tdie)** | needs a ring-0 source — see below |

Elevation alone does **not** help: verified by running elevated, where Tctl still
reads exactly `0`. That zero is why the code treats a non-positive CPU temperature as
missing — otherwise the widget would confidently display "0°".

## CPU temperature without installing anything

**Is any of this required? No.** These supply exactly one number — the CPU
temperature. GPU temp, GPU hot spot, GPU load, VRAM, CPU load and RAM all work with
nothing running and nothing installed. With none of them present the CPU cell reads
`n/a` and everything else is unaffected. DeskMon does not start, install or depend on
any of them — it reads whichever happens to be there.

DeskMon tries three, in order. Each is a **read-only** attachment to a shared-memory
section the other process already publishes:

| Source | Section | Notes |
|---|---|---|
| MSI Afterburner | `MAHMSharedMemory` | Most likely to already be running on a gaming PC |
| HWiNFO | `Global\HWiNFO_SENS_SM2` | Needs *Shared Memory Support* enabled; that switch is a Pro feature in current builds |
| Core Temp | `CoreTempMappingObject` | Smallest free option if you have neither of the above |

All three already load their own kernel driver, so borrowing their published value adds
**no new driver and no new attack surface**. Install none of them and nothing breaks.

If **MSI Afterburner** is running, DeskMon reads CPU temperature straight out of its
shared memory. Afterburner already loads its own ring-0 driver, so this borrows a
number that is already being read — **no new driver, no elevation, no new exposure.**
Start Afterburner and the `n/a` becomes a live reading; close it and it reverts.

The mechanism, all verified against the live section on this machine:

- Named section `MAHMSharedMemory`, opened read-only from an unelevated process
- 32-byte header (`dwSignature` `'MAHM'`, `dwHeaderSize`, `dwNumEntries`,
  `dwEntrySize`, 32-bit `time_t`), then `dwNumEntries` × 1324-byte entries
- Each entry: 5 × `char[260]` ANSI strings, then `float data` at +1300
- DeskMon reads the aggregate `"CPU temperature"` entry — on Ryzen the per-core
  `CPU1..CPU16 temperature` entries are just Tdie repeated
- Entries are located **by name**, not index: the order depends on the user's
  Afterburner monitoring config, and the cached index is re-validated each poll
- Liveness comes from the header timestamp, which is genuine **UTC** unix time
  (confirmed on a UTC−4 machine). Readings older than 15 s are discarded, so a
  stale section left behind by a closed Afterburner never shows as a real value
- `FLT_MAX` sentinels are rejected — Afterburner parks its framerate entries there
  when no game is running

Precedence is: direct sensor read → Afterburner → HWiNFO → Core Temp → `n/a`. If you
later install a ring-0 source of DeskMon's own, that wins and none of the external
tools is queried. Power and clock are filled in independently of temperature, since
each reading is toggled separately in these tools' own settings.

### Paths that were investigated and rejected

| Path | Why not |
|---|---|
| `MSAcpi_ThermalZoneTemperature` (WMI) | Returns "Not supported" on this board; no ACPI thermal zones exist at all |
| `Win32_TemperatureProbe` | Returns nothing — almost never implemented on consumer desktops |
| WinRing0 | CVE-2020-14979, Defender-quarantined, Vanguard/EAC-blocked. This is the one to avoid |
| AMD Ryzen Master SDK | `Platform.dll` / `Device.dll` export **no temperature getter**; the only entry point is a mangled C++ `IDeviceManager` vtable, `Platform.dll` is an overclocking-control surface (`SetCurrentVoltage`, `SetOverclockFreqAllCores`), and it needs admin. Wrong risk profile for a monitoring widget |
| AIDA64 shared memory | Same pattern as the three supported ones, but paid software — low value to add |

### The other option: PawnIO

Right-click → **Enable CPU temperature…** also links to
[PawnIO](https://github.com/namazso/PawnIO.Setup/releases) — a signed,
Microsoft-attested driver written specifically to replace WinRing0. DeskMon detects and
uses it automatically once installed.

Be aware this is the less attractive option here, and honestly so: PawnIO's
compatibility with Memory Integrity (which is **on** on this machine) and its standing
with anti-cheat are **not publicly documented**, and installing it is still a kernel
driver install. The Afterburner route needs no install at all.

## Build

Framework-dependent — ~5 MB, needs the .NET 8 Desktop runtime on the target machine:

```bash
dotnet publish DeskMon.csproj -c Release -r win-x64 --self-contained false -o publish
```

Self-contained — a single 142 MB exe that runs on any Windows 10/11 x64 box with
nothing installed. This is what gets shipped:

```bash
dotnet publish DeskMon.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=false -p:IncludeNativeLibrariesForSelfExtract=true -o ship
```

Compression is deliberately **off**. `EnableCompressionInSingleFile=true` shrinks the
file to 65 MB but decompresses the whole bundle into memory at startup, taking private
memory from 18 MB to 52 MB. Uncompressed, .NET memory-maps the bundle instead and RAM
matches the framework-dependent build exactly. Disk is the cheaper thing to spend.

## Run at logon

```bash
powershell -ExecutionPolicy Bypass -File .\install-startup.ps1
```

Creates a shortcut in your Startup folder. No UAC prompt, no scheduled task, no
elevation. Remove it with:

```bash
powershell -ExecutionPolicy Bypass -File .\install-startup.ps1 -Uninstall
```

## Using it

- **Left-drag** anywhere to move it. Snaps to screen edges.
- **Drag the bottom-right corner** to resize — scales text, rows and graphs together,
  60 %–300 %.
- **Right-click** (or right-click the tray icon) for the menu.
- **Double-click the tray icon** cycles placement: on-desktop → always-on-top → normal.

| Menu item | What it does |
|---|---|
| Move to monitor | Jumps to a display, numbered as in Settings → Display |
| Corner | Which corner of that display to home to |
| Placement | On desktop (behind windows) / Normal / Always on top |
| Refresh rate | 0.5 s … 5 s (default 1.5 s) |
| Size | 85 % / 100 % / 125 % / 160 % presets, plus the current value |
| Opacity | 40 % … 100 % (default 88 %) |
| Show VRAM row | Toggles the VRAM row |
| GPU hot spot instead of core | Swaps GPU temp to junction temp (higher thresholds) |
| Lock position | Stops accidental dragging, hides the resize grip |
| Enable CPU temperature… | Only shown when no source is supplying a temperature |
| Copy sensor list… | Dumps every detected sensor to a text file and opens it |

Settings live in `%APPDATA%\DeskMon\config.json`, written on change and on exit.
Out-of-range values in that file are clamped on load.

## Defaults

Display **2** (the portrait 1080×1920), top-right corner, pinned to the desktop,
1.5 s refresh, 88 % opacity, 100 % scale.

## Overhead

Measured over a 60 s window at the default 1.5 s refresh:

| Metric | Value |
|---|---|
| CPU | **0.041 %** of total (0.65 % of one core) |
| Private memory | ~18 MB |
| Working set | ~65 MB |
| Handles | stable, no growth |

- Only CPU, GPU and memory sensors are opened. Motherboard, storage, network and PSU
  polling are all disabled — those are the expensive ones.
- Sensor polling is one background thread at `BelowNormal`; the process is `BelowNormal`.
- Workstation GC, non-concurrent — no background GC thread.
- `WS_EX_TOOLWINDOW`: no taskbar button, no alt-tab entry, exempt from Show Desktop.
- `WS_EX_NOACTIVATE`: clicking or dragging the widget never takes foreground, so it
  cannot knock a borderless-fullscreen game out of fullscreen.

## Troubleshooting

**CPU temp shows `n/a`** — no ring-0 source is available. Easiest fix: start MSI
Afterburner. `--` instead of `n/a` means the sensor was not found at all.

**Which source is the CPU temp coming from?** Hover the tray icon — it shows
`temp: MSI Afterburner` (or HWiNFO / Core Temp). *Copy sensor list…* lists all three
and which ones responded.

**Widget is hidden** — in *On desktop* mode it sits behind every window by design.
Right-click the tray icon → Placement → Always on top to pull it forward.

**Check what was detected** — right-click → *Copy sensor list…*. The header reports the
LHM version, whether the process is elevated, and whether PawnIO is present.

## Project layout

| File | What lives there |
|---|---|
| `Program.cs` | Entry point: single-instance mutex, `BelowNormal` priority, message loop |
| `OverlayForm.cs` | The widget — painting, sparklines, drag/resize, tray icon, context menu, z-order |
| `HardwareMonitor.cs` | Background sensor thread, `Snapshot`, LibreHardwareMonitor sensor picking |
| `CpuTempChain.cs` | Tries each external CPU-temperature source in order |
| `NamedSection.cs` | Shared-memory attach/detach plumbing, plus the `ICpuTempSource` interface |
| `AfterburnerSensors.cs` | MSI Afterburner `MAHMSharedMemory` reader |
| `HwInfoSensors.cs` | HWiNFO `HWiNFO_SENS_SM2` reader |
| `CoreTempSensors.cs` | Core Temp `CoreTempMappingObject` reader |
| `Native.cs` | Win32 P/Invoke and constants |
| `Config.cs` | JSON settings in `%APPDATA%\DeskMon`, clamped on load |
| `app.manifest` | DPI awareness and `asInvoker` — see rule 2 above |

Adding a fourth temperature source means implementing `ICpuTempSource` and adding it to
the array in `CpuTempChain`. Nothing else needs to change.

**Note on test coverage:** the Afterburner reader is verified against the live section on
real hardware. The HWiNFO and Core Temp readers are verified only against synthetic
sections built to the documented layouts — neither tool was installed on the development
machine. If one of them misbehaves in the wild, that is the first place to look.

## License and credits

DeskMon is MIT licensed — see [LICENSE](LICENSE). Use it, fork it, ship it.

It depends on [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor),
licensed under the **Mozilla Public License 2.0**. The self-contained build bundles that
library, so if you redistribute the executable, MPL-2.0 asks that recipients can obtain
that library's source — the link above satisfies it. MPL-2.0 is file-level copyleft and
places no obligation on DeskMon's own source.

The shared-memory formats read by the optional CPU-temperature sources belong to their
respective tools (MSI Afterburner, HWiNFO, Core Temp). DeskMon attaches to them
read-only and bundles no part of them.
