#!/usr/bin/env python3
"""
Condense a raw WebHID capture dump (from the hidLog instrumentation) into a
compact, readable summary grouped by command prefix.

Usage:
    python analyze.py captures/aula_hid_dump_1.json
    python analyze.py captures/aula_hid_dump_1.json --raw   # dump full sequence instead of grouping
"""
import json
import sys
from collections import defaultdict, OrderedDict


def load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def hexbytes(s):
    if not s:
        return []
    return [int(x, 16) for x in s.split()]


def summarize(entries):
    """
    Groups sendReport/inputreport frames by (cmd_byte, subcmd_byte) = data[0:2].
    data[2] is (so far) always 0 — data[3] is the per-item index (key #, param #),
    data[4:] is the value payload for that index. Collapses repetitive per-key
    sweeps into an index->value table so a 64-key readout is one compact block.
    """
    events = [e for e in entries if e.get("type") in ("sendReport", "inputreport")]

    groups = OrderedDict()  # key: (dir, b0,b1) -> list of full byte arrays
    for e in events:
        b = hexbytes(e.get("data", ""))
        if len(b) < 2:
            continue
        direction = "TX" if e["type"] == "sendReport" else "RX"
        key = (direction, b[0], b[1])
        groups.setdefault(key, []).append(b)

    lines = []
    for (direction, b0, b1), samples in groups.items():
        n = len(samples)
        # value payload after the index byte, trimmed of trailing zeros, up to 12 bytes
        table = OrderedDict()
        no_idx_tails = set()
        for s in samples:
            if len(s) <= 3:
                no_idx_tails.add(tuple(s[2:8]))
                continue
            idx = s[3]
            payload = s[4:16]
            while payload and payload[-1] == 0:
                payload = payload[:-1]
            table[idx] = payload  # last write wins if seen twice

        if table:
            idxs = sorted(table)
            values = [table[i] for i in idxs]
            # if every value is identical, collapse further
            uniq_vals = {tuple(v) for v in values}
            idx_desc = f"0x{idxs[0]:02x}..0x{idxs[-1]:02x} (n={len(idxs)})"
            if len(uniq_vals) == 1:
                v = " ".join(f"{x:02x}" for x in values[0])
                lines.append(f"{direction} {b0:02x} {b1:02x}  x{n:<4} idx={idx_desc}  ALL SAME -> [{v}]")
            else:
                pairs = ", ".join(
                    f"{i:02x}:[{' '.join(f'{x:02x}' for x in table[i])}]" for i in idxs
                )
                lines.append(f"{direction} {b0:02x} {b1:02x}  x{n:<4} idx={idx_desc}\n      {pairs}")
        if no_idx_tails:
            tail_sample = ", ".join(
                "[" + " ".join(f"{x:02x}" for x in t) + "]" for t in list(no_idx_tails)[:6]
            )
            lines.append(f"{direction} {b0:02x} {b1:02x}  x{len(no_idx_tails):<4} (no idx byte) tails: {tail_sample}")
    return "\n".join(lines)


def raw_dump(entries, limit=None):
    lines = []
    for e in entries:
        t = e.get("type")
        if t in ("sendReport", "inputreport"):
            d = e.get("data", "")
            lines.append(f"{'TX' if t=='sendReport' else 'RX'} r{e.get('reportId')}: {d}")
        elif t in ("requestDevice", "requestDevice:result", "open", "open:done"):
            lines.append(f"-- {t}: {json.dumps({k:v for k,v in e.items() if k not in ('t',)})[:200]}")
    if limit:
        lines = lines[:limit]
    return "\n".join(lines)


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    path = sys.argv[1]
    data = load(path)
    print(f"# {path} — {len(data)} log entries\n")
    if "--raw" in sys.argv:
        limit = None
        for a in sys.argv[2:]:
            if a.isdigit():
                limit = int(a)
        print(raw_dump(data, limit))
    else:
        print(summarize(data))
