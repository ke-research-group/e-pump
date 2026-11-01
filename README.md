# e-pump — ReaXus CP Class pump control

A C# / WinUI 3 application that runs a **Teledyne ISCO (Teledyne LABS) ReaXus CP Class**
dual-piston pump at **constant flow** with a hard **2800 psi** ceiling, and records pressure and
flow to NumPy `.npz` files. It is meant to run beside the
[NI6451 acquisition application](https://github.com/ke-research-group/NI6451) and follows its
layout, build setup and file naming.

The pump in the lab is the 12 mL/min model: 0.001–12.000 mL/min in 0.001 steps, rated to
18,000 psi.

## Safety model

The CP Class has no constant-flow mode. It always adjusts its flow, up to the flow set point
(`FI`), to reach a pressure set point (`PS`). This application uses it for constant flow like this:

| Layer | What it does |
| --- | --- |
| `PS` pinned at 2800 psi (front panel **PRESS**, LED solid) | The pump's constant-pressure target is never below the cap, so it delivers exactly `FI` and does not start throttling before the limit. Never changed by the operator. |
| Pump upper limit `UP` (front panel **HI PR**) | The operator's limit (≤ 2800, default 2700): the pressure at which the pump stops itself and faults. This is the value the window's "Limit · HI PR" changes. Read back after every write. |
| Software over-pressure stop | Every poll (2 Hz): if the pressure is at or above the limit while running, `ST` is sent at once. |
| Software flow-deviation stop | If the reported flow stays more than 5 % below the set point for 3 polls, the pump is stopped: it was throttling, so the flow was no longer constant. Suspended for 10 s after Run or a flow change. |
| Keypad guard | If the pump's `UP` is raised from the front panel, it is put back on the next poll. The keypad can also be locked (`KD`) while connected. |
| Run gating | Run is only sent after `UP`, `PS` and `FI` have been written **and read back**. A pump with an active fault is not started. |
| Window and link | The window will not close or disconnect while the pump runs. Losing contact for 3 poll cycles raises an alarm: the pump keeps running, and the software checks cannot act until it answers again. |

All of these numbers live in [`PumpConfig.cs`](winui/src/EPump.Core/PumpConfig.cs).

## Connecting the pump

The pump has RS-232 (RJ12), micro-USB and Ethernet on the back. All three carry the same
ASCII protocol (9600 baud, 8N1). This application uses a serial port: either the micro-USB
port, or RS-232 through an adapter.

1. On the Windows PC, install the Teledyne SSI USB driver from
   <https://www.teledynessi.com/driver-downloads>.
2. Plug in the micro-USB cable. Device Manager should show a new **COM port**. If it shows
   something else, the driver is not a virtual COM port and this application cannot use it as
   is; report what it shows.
3. Check the link without changing anything on the pump:

   ```
   epump ports
   epump ping COM5
   ```

   `ping` only sends query commands. It prints the firmware, max flow and pressure, pressure
   units, current pressure, flow, limits, set point, faults and leak sensor.
4. Start `EPump.exe`, pick the port, and press **Connect**.

Connecting sets the pump's `PS` to 2800 psi and its `UP` to the limit in the window. The
**Simulator** port runs everything against a built-in simulated pump, with no hardware.

## Using it

- **Connect / Disconnect**: polling, the live readout and the safety checks run for as long
  as the pump is connected. There is no separate Monitor button.
- **Run & record / STOP PUMP**: every run is recorded, so Run needs an output folder.
  - **Run & record** starts the recording first, then the pump, so the file holds the whole
    start-up. If the pump does not start, the recording is discarded and no run number is used.
  - **STOP PUMP** stops the pump and then saves the `.npz`. It is always enabled, even while
    another command is in flight.
  - If the pump stops by itself, through a safety stop, a fault or its own RUN/STOP key, the
    recording runs on for 5 s to capture the pressure decay, then saves.
  - **Apply to pump** changes the flow or the limit, including while running.
- **Fault geometry**: choose 1D (5 cm or 10 cm) or 2D (50 cm, fixed), as in NI6451. The
  **SHEAR STRESS** tile converts the pump pressure into shear stress on the fault with NI6451's
  formula: pressure × shear-piston area (12.67 × 10⁻³ m²; ×3 for 2D) ÷ (thickness × 0.5 m).
- The constant-flow card shows the HI PR and PRESS values the pump reports, so they can be
  compared with its front panel.
- **Output naming** is read from NI6451's `%LOCALAPPDATA%\Ni6451\naming.json` and never
  written to it:
  - If NI6451 has recorded a run today, the same serial and run are used, assuming NI6451's
    Record was pressed first.
  - Otherwise the app uses the numbers NI6451 will use for its next run.

  Both fields stay editable. The pump file is
  `T{serial}-pump-run{RN}-{yyyyMMdd_HHmmss}.npz`, named after the local start time of the
  recording. It sits next to NI6451's `T{serial}-raw-run{RN}-….npz`.
- An interrupted recording, after a crash or a power cut, stays in a hidden
  `.epump-spool-…` folder. Choosing that output folder again offers **Recover now**. The CLI
  can do the same: `epump recover <folder> --apply`.

## Recording format

The archive is uncompressed, with no object arrays. Load it with `np.load(path, allow_pickle=False)`.

| Array | dtype | Shape | Meaning |
| --- | --- | --- | --- |
| `t_unix` | `<f8` | `(N,)` | Wall-clock time of each poll, Unix seconds (UTC). Use it to align with NI6451 data. |
| `pressure_psi` | `<f8` | `(N,)` | Outlet pressure |
| `shear_stress_mpa` | `<f8` | `(N,)` | Shear stress on the fault, from `pressure_psi` and the recording's fault geometry |
| `flow_ml_min` | `<f8` | `(N,)` | Flow reported by the pump |
| `flow_setpoint_ml_min` | `<f8` | `(N,)` | `FI` |
| `pressure_limit_psi` | `<f8` | `(N,)` | `UP`, as the pump reports it |
| `running`, `fault_motor_stall`, `fault_over_pressure`, `fault_under_pressure`, `leak` | `<i8` | `(N,)` | 0/1 flags |
| `event_t_unix`, `event_kind`, `event_message` | `<f8`, `<U…`, `<U…` | `(k,)` | Run, stop, settings changes, safety stops, faults, link loss |
| `start_time_unix`, `max_flow_ml_min`, `max_pressure_psi`, `pressure_cap_psi`, `pressure_set_point_psi`, `fault_thickness_m` | `<f8` | `()` | `fault_thickness_m` is the effective thickness (0.5 for 2D) |
| `run`, `poll_interval_ms` | `<i8` | `()` | |
| `experiment_serial`, `port`, `pump_identity`, `pressure_units_native`, `app_version`, `fault_type` | `<U…` | `()` | `fault_type` is `1D` or `2D` |

## Building

WinUI 3 cannot be built on macOS. [`.github/workflows/build-winui.yml`](.github/workflows/build-winui.yml)
builds on `windows-latest` on every push to `main` that touches `winui/`. Each run:

1. Runs `epump selftest`.
2. Records a simulated run and loads it with NumPy, using
   [`check_npz.py`](winui/build/check_npz.py).
3. Publishes these artifacts:
   - `EPump-win-x64-aot`: the recommended build
   - `EPump-win-x64`: the standard self-contained build
   - `epump-cli-win-x64`: the command line tool as a single exe
   - `sample-recording`

Pushing a `v*` tag also attaches zips to a GitHub Release. Unpack the whole folder and run
`EPump.exe`. If no window appears, look at `%LOCALAPPDATA%\EPump\startup.log`.

On Windows: `pwsh winui/build/build-win-x64.ps1`.

`winui/global.json` pins the .NET 8 SDK. The Windows App SDK `1.6.241114003` / Win2D
`1.3.2` pair is the same as in NI6451, for the same reasons; see that repository's
`winui/README.md`.

The platform-neutral parts build and test on a Mac with the .NET 8 SDK:

```bash
cd winui && dotnet run --project src/EPump.Tools/EPump.Tools.csproj -- selftest
```

## Project layout

| Project | TFM | Purpose |
| --- | --- | --- |
| `winui/src/EPump.Core` | `net8.0` | Protocol, serial transport, simulator, session and safety rules, `.npz` container, spool and recovery, naming, settings |
| `winui/src/EPump.App` | `net8.0-windows10.0.19041.0` | The WinUI 3 application |
| `winui/src/EPump.Tools` | `net8.0` | `epump` CLI: `ports`, `ping`, `dump`, `recover`, `simulate`, `selftest` |

## Protocol reference

- *SSI Pump Communication: RS232, USB & Ethernet*, Teledyne SSI document 903068 rev F (2020):
  the command set, the 100 ms minimum command spacing, retries, and the `OK…/` and `Er/`
  replies.
- *ReaXus CP Class operator's manual*, Teledyne LABS: the keypad, the setup menu, the rear
  terminal block, and the CP set point and PID.

## Not yet verified on the real pump

- That the micro-USB driver presents a virtual COM port.
- Whether the pump's own CP controller reduces flow somewhat before the pressure reaches `PS`.
  If it does, the flow-deviation stop triggers before the limit does. That still stops the
  pump, but the 5 % tolerance may need tuning.
- The format of `CC` pressure replies when the pump is set to bar or MPa. Readings are parsed
  as decimal numbers in those units and converted. Keep the pump on psi to be safe.
