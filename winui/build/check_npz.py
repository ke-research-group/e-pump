"""Load a recording written by `epump simulate --out` with NumPy, the way analysis code will.

Run by CI after the simulator has produced a file. Fails if any array needs pickle, has the
wrong dtype, or if the columns disagree in length.

    python winui/build/check_npz.py <folder-or-file.npz>
"""

import pathlib
import sys

import numpy as np

FLOAT_COLUMNS = ["t_unix", "pressure_psi", "flow_ml_min", "flow_setpoint_ml_min", "pressure_limit_psi"]
FLAG_COLUMNS = ["running", "fault_motor_stall", "fault_over_pressure", "fault_under_pressure", "leak"]


def check(path: pathlib.Path) -> None:
    # allow_pickle=False is the point: every array must be plain numeric or fixed-width text.
    with np.load(path, allow_pickle=False) as npz:
        n = len(npz["t_unix"])
        assert n > 0, "no samples"
        for name in FLOAT_COLUMNS:
            a = npz[name]
            assert a.dtype == np.float64 and a.shape == (n,), (name, a.dtype, a.shape)
        for name in FLAG_COLUMNS:
            a = npz[name]
            assert a.dtype == np.int64 and a.shape == (n,), (name, a.dtype, a.shape)
            assert set(np.unique(a)) <= {0, 1}, name

        assert np.all(np.diff(npz["t_unix"]) > 0), "timestamps must increase"
        assert npz["pressure_cap_psi"].shape == () and float(npz["pressure_cap_psi"]) == 2800.0
        assert npz["event_kind"].dtype.kind == "U" and len(npz["event_kind"]) == len(npz["event_message"])
        assert str(npz["experiment_serial"]).isdigit()

        print(f"{path.name}: {n} samples, {len(npz['event_kind'])} events, "
              f"max {npz['pressure_psi'].max():.0f} psi, serial {npz['experiment_serial']} run {int(npz['run'])}")
        for t, kind, message in zip(npz["event_t_unix"], npz["event_kind"], npz["event_message"]):
            print(f"  {t:.3f}  {kind:<28} {message}")


def main() -> None:
    target = pathlib.Path(sys.argv[1])
    files = sorted(target.glob("*.npz")) if target.is_dir() else [target]
    if not files:
        sys.exit(f"no .npz files in {target}")
    for f in files:
        check(f)


if __name__ == "__main__":
    main()
