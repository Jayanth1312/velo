#!/usr/bin/env python3
"""bench_report.py — parse `cargo run -p app --release -- bench` output,
compare against a saved baseline, and flag regressions.

Stdlib only (Python 3.10+). This is the one place Python earns its keep in
the Velo repo: ad-hoc text parsing + stats over benchmark output, not a
runtime dependency of the app itself.

Usage:
    cargo run -p app --release -- bench > /tmp/bench.txt
    python3 scripts/bench_report.py --input /tmp/bench.txt --save-baseline
    python3 scripts/bench_report.py --input /tmp/bench.txt
    python3 scripts/bench_report.py --input /tmp/bench.txt --markdown
    python3 scripts/bench_report.py            # runs cargo bench itself
    python3 scripts/bench_report.py --self-test
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
BASELINE_PATH = REPO_ROOT / "scripts" / "bench_baseline.json"

# metric key -> (unit, "low"|"high" is better)
METRICS: dict[str, tuple[str, str]] = {
    "parse_frame_ms": ("ms", "low"),
    "parse_frame_mib_s": ("MiB/s", "high"),
    "parse_frame_lines_s": ("lines/s", "high"),
    "advance_ms": ("ms", "low"),
    "advance_mib_s": ("MiB/s", "high"),
    "frame_ms": ("ms", "low"),
    "frame_ms_per_frame": ("ms/frame", "low"),
    "typing_ms_per_frame": ("ms/frame", "low"),
    "flood_fps": ("FPS", "high"),
    "flood_ms_per_frame": ("ms/frame", "low"),
    "idle_fps": ("FPS", "high"),
    "idle_ms_per_frame": ("ms/frame", "low"),
}
METRIC_ORDER = list(METRICS)

REGRESSION_THRESHOLD_PCT = 10.0


def parse_bench_output(text: str) -> dict[str, float]:
    """Extract metric name -> value from bench.rs's println! output.

    Matches the exact format printed by crates/app/src/bench.rs (parse_throughput
    and render_fps). See that file for the format strings this mirrors.
    """
    metrics: dict[str, float] = {}
    for line in text.splitlines():
        m = re.match(
            r"^\s*([\d.]+) ms\s*->\s*([\d.]+) MiB/s,\s*([\d.]+) lines/s\s*$", line
        )
        if m:
            metrics["parse_frame_ms"] = float(m.group(1))
            metrics["parse_frame_mib_s"] = float(m.group(2))
            metrics["parse_frame_lines_s"] = float(m.group(3))
            continue

        m = re.match(
            r"^\s*advance:\s*([\d.]+) ms \(([\d.]+) MiB/s\)\s*"
            r"frame:\s*([\d.]+) ms \(([\d.]+) ms/frame\)\s*$",
            line,
        )
        if m:
            metrics["advance_ms"] = float(m.group(1))
            metrics["advance_mib_s"] = float(m.group(2))
            metrics["frame_ms"] = float(m.group(3))
            metrics["frame_ms_per_frame"] = float(m.group(4))
            continue

        m = re.match(
            r"^\[typing\] \d+ single-char frames: ([\d.]+) ms/frame\s*$", line
        )
        if m:
            metrics["typing_ms_per_frame"] = float(m.group(1))
            continue

        m = re.match(
            r"^\[render\] flood \S+ \(\S+\): ([\d.]+) FPS, ([\d.]+) ms/frame\s*$",
            line,
        )
        if m:
            metrics["flood_fps"] = float(m.group(1))
            metrics["flood_ms_per_frame"] = float(m.group(2))
            continue

        m = re.match(
            r"^\[render\] idle \(incremental\): ([\d.]+) FPS, ([\d.]+) ms/frame\s*$",
            line,
        )
        if m:
            metrics["idle_fps"] = float(m.group(1))
            metrics["idle_ms_per_frame"] = float(m.group(2))
            continue

    return metrics


def fmt_num(v: float | None) -> str:
    if v is None:
        return "-"
    if v >= 100:
        return f"{v:.0f}"
    if v >= 10:
        return f"{v:.1f}"
    return f"{v:.3f}"


def fmt_value(v: float | None, unit: str) -> str:
    if v is None:
        return "-"
    return f"{fmt_num(v)} {unit}"


def compare(
    baseline: dict[str, float], current: dict[str, float]
) -> tuple[list[tuple[str, float | None, float | None, float | None, str, bool]], bool]:
    """Return (rows, any_regression). Each row is
    (metric, baseline_val, current_val, delta_pct, unit, regressed)."""
    rows = []
    any_regression = False
    keys = sorted(
        set(baseline) | set(current),
        key=lambda k: METRIC_ORDER.index(k) if k in METRIC_ORDER else len(METRIC_ORDER),
    )
    for k in keys:
        b = baseline.get(k)
        c = current.get(k)
        unit, better = METRICS.get(k, ("", "low"))
        delta_pct: float | None = None
        regressed = False
        if b is not None and c is not None:
            delta_pct = 0.0 if b == 0 else (c - b) / b * 100.0
            if better == "low":
                regressed = delta_pct > REGRESSION_THRESHOLD_PCT
            else:
                regressed = delta_pct < -REGRESSION_THRESHOLD_PCT
        if regressed:
            any_regression = True
        rows.append((k, b, c, delta_pct, unit, regressed))
    return rows, any_regression


def print_table(rows) -> None:
    headers = ("Metric", "Baseline", "Current", "Delta")
    table_rows = []
    for metric, b, c, delta_pct, unit, regressed in rows:
        b_str = fmt_value(b, unit)
        c_str = fmt_value(c, unit)
        if delta_pct is None:
            d_str = "-"
        else:
            d_str = f"{delta_pct:+.1f}%"
        if regressed:
            d_str += "  REGRESSION"
        table_rows.append((metric, b_str, c_str, d_str))

    widths = [len(h) for h in headers]
    for row in table_rows:
        for i, cell in enumerate(row):
            widths[i] = max(widths[i], len(cell))

    def line(cells):
        return "  ".join(cell.ljust(widths[i]) for i, cell in enumerate(cells))

    print(line(headers))
    print(line(["-" * w for w in widths]))
    for row in table_rows:
        print(line(row))


def print_markdown(rows) -> None:
    print("| Metric | Baseline | Current | Delta |")
    print("|---|---|---|---|")
    for metric, b, c, delta_pct, unit, regressed in rows:
        b_str = fmt_value(b, unit)
        c_str = fmt_value(c, unit)
        d_str = "-" if delta_pct is None else f"{delta_pct:+.1f}%"
        if regressed:
            d_str = f"**{d_str} (regression)**"
        print(f"| {metric} | {b_str} | {c_str} | {d_str} |")


def load_baseline() -> dict[str, float]:
    if BASELINE_PATH.exists():
        return json.loads(BASELINE_PATH.read_text())
    return {}


def save_baseline(metrics: dict[str, float]) -> None:
    BASELINE_PATH.write_text(json.dumps(metrics, indent=2, sort_keys=True) + "\n")


def run_cargo_bench() -> str:
    proc = subprocess.run(
        ["cargo", "run", "-p", "app", "--release", "--", "bench"],
        cwd=REPO_ROOT,
        capture_output=True,
        text=True,
    )
    if proc.returncode != 0:
        print(proc.stdout)
        print(proc.stderr, file=sys.stderr)
        raise SystemExit(f"cargo bench failed with exit code {proc.returncode}")
    return proc.stdout


# ---------------------------------------------------------------------------
# Self-test: embedded sample of the real bench.rs output format, with known
# values, so this script's parser can be verified without a Rust build.
# ---------------------------------------------------------------------------

SAMPLE_OUTPUT = """\
== velo bench ==

[verify] render pixel check: PASS

[parse+frame] 100000 lines, 5.8 MiB, 1449 frames
  235 ms  ->  24.6 MiB/s,  425531 lines/s
  advance: 86 ms (67.6 MiB/s)   frame: 148 ms (0.100 ms/frame)

[typing] 10000 single-char frames: 0.022 ms/frame

[render] adapter: llvmpipe (Cpu, Vulkan)
[render] flood 120x40 (1440x960px): 57 FPS, 17.5 ms/frame
[render] idle (incremental): 60 FPS, 16.7 ms/frame

Note: keypress-to-pixel, `cat` throughput, and on-screen present
FPS are end-to-end Windows/ConPTY numbers and must be measured on a
Windows host (see PROGRESS.md).
"""

EXPECTED_SAMPLE_METRICS = {
    "parse_frame_ms": 235.0,
    "parse_frame_mib_s": 24.6,
    "parse_frame_lines_s": 425531.0,
    "advance_ms": 86.0,
    "advance_mib_s": 67.6,
    "frame_ms": 148.0,
    "frame_ms_per_frame": 0.100,
    "typing_ms_per_frame": 0.022,
    "flood_fps": 57.0,
    "flood_ms_per_frame": 17.5,
    "idle_fps": 60.0,
    "idle_ms_per_frame": 16.7,
}


def self_test() -> None:
    # 1. Parsing extracts exactly the expected metrics with exact values.
    metrics = parse_bench_output(SAMPLE_OUTPUT)
    assert metrics == EXPECTED_SAMPLE_METRICS, (
        f"parse mismatch:\n got={metrics}\n want={EXPECTED_SAMPLE_METRICS}"
    )

    # 2. Every parsed metric is a known metric (units/direction defined).
    for k in metrics:
        assert k in METRICS, f"metric {k} has no unit/direction entry in METRICS"

    # 3. compare(): identical baseline/current -> zero delta, no regression.
    rows, any_regression = compare(metrics, metrics)
    assert not any_regression, "identical baseline/current flagged a regression"
    assert all(delta == 0.0 for _, _, _, delta, _, _ in rows)

    # 4. compare(): a >10% slower "low is better" metric is flagged.
    worse = dict(metrics)
    worse["typing_ms_per_frame"] = metrics["typing_ms_per_frame"] * 1.5  # +50%
    rows, any_regression = compare(metrics, worse)
    assert any_regression, "50% slower typing_ms_per_frame should regress"
    typing_row = next(r for r in rows if r[0] == "typing_ms_per_frame")
    assert typing_row[5] is True

    # 5. compare(): a <=10% slower metric is NOT flagged.
    slightly_worse = dict(metrics)
    slightly_worse["typing_ms_per_frame"] = metrics["typing_ms_per_frame"] * 1.05
    _, any_regression = compare(metrics, slightly_worse)
    assert not any_regression, "5% slowdown should be within tolerance"

    # 6. compare(): a >10% drop in a "high is better" metric (FPS) regresses.
    worse_fps = dict(metrics)
    worse_fps["flood_fps"] = metrics["flood_fps"] * 0.8  # -20%
    _, any_regression = compare(metrics, worse_fps)
    assert any_regression, "20% FPS drop should regress"

    # 7. compare(): a >10% *increase* in FPS must NOT be flagged as regression.
    better_fps = dict(metrics)
    better_fps["flood_fps"] = metrics["flood_fps"] * 1.3
    _, any_regression = compare(metrics, better_fps)
    assert not any_regression, "FPS improvement must not be flagged"

    # 8. fmt_value / fmt_num sanity.
    assert fmt_value(24.6, "MiB/s") == "24.6 MiB/s"
    assert fmt_value(None, "ms") == "-"

    print("self-test: all assertions passed")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument(
        "--input", metavar="FILE", help="saved bench output file (skip running cargo)"
    )
    parser.add_argument(
        "--save-baseline",
        action="store_true",
        help="write parsed metrics to scripts/bench_baseline.json",
    )
    parser.add_argument(
        "--markdown", action="store_true", help="emit a BENCHMARKS.md-style table"
    )
    parser.add_argument(
        "--self-test",
        action="store_true",
        help="run embedded self-test and exit (no cargo/file needed)",
    )
    args = parser.parse_args()

    if args.self_test:
        self_test()
        return 0

    if args.input:
        text = Path(args.input).read_text()
    else:
        text = run_cargo_bench()

    metrics = parse_bench_output(text)
    if not metrics:
        print("error: no known metrics found in bench output", file=sys.stderr)
        return 1

    if args.save_baseline:
        save_baseline(metrics)
        print(f"Saved baseline to {BASELINE_PATH} ({len(metrics)} metrics)")
        return 0

    baseline = load_baseline()
    rows, any_regression = compare(baseline, metrics)

    if args.markdown:
        print_markdown(rows)
    else:
        print_table(rows)
        if not baseline:
            print("\n(no baseline found — run with --save-baseline to create one)")

    if any_regression:
        print(
            f"\nREGRESSION: one or more metrics moved >{REGRESSION_THRESHOLD_PCT:.0f}% "
            "in the wrong direction vs baseline",
            file=sys.stderr,
        )
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
