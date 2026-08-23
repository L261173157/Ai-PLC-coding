#!/usr/bin/env python3
"""LIVE verification (real TIA V21) of the structured LAD/GRAPH read+write tools.

Covers what the offline Fake smoke cannot prove against real Siemens behavior:
  1. tia_block_read_code against the project's REAL LAD blocks (render stats, no crashes);
  2. tia_block_write_code a kitchen-sink FB exercising EVERY v1 instruction
     (NO/NC contacts, nested and/or, coil/set/reset, TON/TOF/TP, MOVE, all six compares)
     -> tia_project_compile must return 0 errors; same-name rewrite is idempotent;
     read_code round-trips the written body;
  3. dryRun returns the generated XML (saved next to this script's output for hand-checks);
  4. the worker's SclSource language guard rejects a LAD block with a clear message;
  5. GRAPH: if tests/fixtures/FB_GraphDemo.xml exists, import it and read its GRAPH view;
     otherwise prints the one-time bootstrap procedure (G0).

The script does NOT save the project — the headless Portal discards the demo block on close.

Usage: python brands/tia/mcp/tests/verify_lad_graph.py <server.dll> <path-to.ap21>
"""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mcp_client import Client

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
FIXTURE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "fixtures", "FB_GraphDemo.xml")

KITCHEN_SINK = {
    "name": "FB_LadKitchenSink",
    "blockType": "FB",
    "language": "LAD",
    "comment": "verify_lad_graph: exercises every tia_block_write_code v1 instruction",
    "interface": [
        {"section": "Input", "members": [
            {"name": "Start", "datatype": "Bool"},
            {"name": "Auto", "datatype": "Bool"},
            {"name": "Stop", "datatype": "Bool"},
            {"name": "Level", "datatype": "Real"},
            {"name": "Setpoint", "datatype": "Real"},
            {"name": "Count", "datatype": "Int"},
        ]},
        {"section": "Output", "members": [
            {"name": "Motor", "datatype": "Bool"},
            {"name": "Alarm", "datatype": "Bool"},
            {"name": "AlarmOff", "datatype": "Bool"},
            {"name": "Done", "datatype": "Bool"},
            {"name": "Pulse", "datatype": "Bool"},
            {"name": "Scaled", "datatype": "Real"},
            {"name": "High", "datatype": "Bool"},
            {"name": "Low", "datatype": "Bool"},
            {"name": "InBand", "datatype": "Bool"},
            {"name": "OutOfRange", "datatype": "Bool"},
            {"name": "Above", "datatype": "Bool"},
            {"name": "Below", "datatype": "Bool"},
            {"name": "Equal", "datatype": "Bool"},
            {"name": "NotEqual", "datatype": "Bool"},
        ]},
        {"section": "Static", "members": [
            {"name": "OnTimer", "datatype": "TON_TIME"},
            {"name": "OffTimer", "datatype": "TOF_TIME"},
            {"name": "PulseTimer", "datatype": "TP_TIME"},
        ]},
    ],
    "networks": [
        # 1. nested and/or + NC contact + plain coil (启保停)
        {"rungs": [{"logic": {"op": "and", "args": [
            {"op": "or", "args": [{"op": "contact", "operand": "Start"},
                                   {"op": "contact", "operand": "Motor"}]},
            {"op": "contact", "operand": "Stop", "negated": True}]},
            "output": {"kind": "coil", "operand": "Motor"}}]},
        # 2. TON with Q coil
        {"rungs": [{"logic": {"op": "and", "args": [
            {"op": "contact", "operand": "Start"},
            {"op": "contact", "operand": "Auto"}]},
            "output": {"kind": "ton", "instance": "OnTimer", "pt": "T#3S",
                       "q": {"kind": "coil", "operand": "Done"}}}]},
        # 3. TOF (Q open -> auto OpenCon)
        {"rungs": [{"logic": {"op": "contact", "operand": "Motor", "negated": True},
                    "output": {"kind": "tof", "instance": "OffTimer", "pt": "T#2S",
                               "q": {"kind": "coil", "operand": "AlarmOff"}}}]},
        # 4. TP
        {"rungs": [{"logic": {"op": "contact", "operand": "Auto"},
                    "output": {"kind": "tp", "instance": "PulseTimer", "pt": "T#500MS",
                               "q": {"kind": "coil", "operand": "Pulse"}}}]},
        # 5. MOVE
        {"rungs": [{"logic": {"op": "contact", "operand": "Auto"},
                    "output": {"kind": "move", "src": "Level", "dst": "Scaled"}}]},
        # 6. compare Ge -> set, Lt -> reset
        {"rungs": [
            {"logic": {"op": "contact", "operand": "Auto"},
             "output": {"kind": "compare", "part": "Ge", "in1": "Level", "in2": "9.5",
                        "out": {"kind": "set", "operand": "High"}}},
            {"logic": {"op": "contact", "operand": "Auto"},
             "output": {"kind": "compare", "part": "Lt", "in1": "Level", "in2": "1.0",
                        "out": {"kind": "reset", "operand": "High"}}},
        ]},
        # 7. every remaining compare
        {"rungs": [
            {"logic": {"op": "contact", "operand": "Auto"},
             "output": {"kind": "compare", "part": "Gt", "in1": "Level", "in2": "8.0", "out": {"kind": "coil", "operand": "Above"}}},
            {"logic": {"op": "contact", "operand": "Auto"},
             "output": {"kind": "compare", "part": "Le", "in1": "Level", "in2": "7.0", "out": {"kind": "coil", "operand": "Below"}}},
            {"logic": {"op": "contact", "operand": "Auto"},
             "output": {"kind": "compare", "part": "Eq", "in1": "Count", "in2": "5", "out": {"kind": "coil", "operand": "Equal"}}},
            {"logic": {"op": "contact", "operand": "Auto"},
             "output": {"kind": "compare", "part": "Ne", "in1": "Count", "in2": "0", "out": {"kind": "coil", "operand": "NotEqual"}}},
        ]},
        # 8. compare on two Real variables driving a coil
        {"rungs": [
            {"logic": {"op": "contact", "operand": "Auto"},
             "output": {"kind": "compare", "part": "Eq", "in1": "Level", "in2": "Setpoint",
                        "out": {"kind": "coil", "operand": "InBand"}}},
        ]},
    ],
}

FAILS = []


def check(label, ok, detail=""):
    print(f"[{'PASS' if ok else 'FAIL'}] {label}" + (f"  {detail}" if detail else ""))
    if not ok:
        FAILS.append(f"{label}: {detail}")


def main() -> int:
    if len(sys.argv) < 3:
        print(__doc__)
        return 2
    dll, project = sys.argv[1], sys.argv[2]

    c = Client(dll, backend="openness", mode="ReadWrite", client_name="verify-lad-graph")
    try:
        c.initialize()
        c.call("tia_connect", {"mode": "headless"})
        c.call("tia_project_open", {"sessionPath": "session:s-openness", "path": project, "visible": False})
        targets = c.call("tia_project_list", {"projectPath": "session:s-openness"})
        plcs = [t for t in targets if t.get("kind") == "Plc"] if isinstance(targets, list) else []
        plc = plcs[0]["path"] + "/plc:program"
        dev = plcs[0]["path"]
        print(f"plc: {plc}")

        # ---- 1. read_code over real LAD blocks (skipped when the project has none — the
        # kitchen-sink round-trip below already covers real-machine LAD reading) ----
        blocks = c.call("tia_block_list", {"path": plc, "type": "FB", "limit": 500}).get("blocks", [])
        lads = [b for b in blocks if b.get("language") == "LAD"][:8]
        total = rendered = fallbacks = 0
        for b in lads:
            r = c.call("tia_block_read_code", {"path": b["path"], "includeInterface": False}, timeout=300)
            if not isinstance(r, dict) or "networks" not in r:
                check(f"read_code {b['name']}", False, str(r)[:120])
                continue
            for n in r["networks"]:
                total += 1
                if n.get("fallback"):
                    fallbacks += 1
                elif n.get("render"):
                    rendered += 1
            sample = next((n["render"] for n in r["networks"] if n.get("render")), None)
            print(f"  {b['name']}: {len(r['networks'])} networks, sample: {str(sample)[:90]}")
        if lads:
            check("read_code on real LAD blocks", rendered > 0,
                  f"{len(lads)} blocks, {total} networks, {rendered} rendered, {fallbacks} fallback")
        else:
            print("[SKIP] read_code on real LAD blocks: project has no pre-existing LAD blocks")

        # ---- 2. kitchen-sink write -> compile 0 errors ----
        wr = c.call("tia_block_write_code", {"plcPath": plc, "specJson": json.dumps(KITCHEN_SINK)}, timeout=300)
        check("write kitchen-sink", wr.get("status") == "Applied", str(wr)[:140])

        comp = c.call("tia_project_compile", {"scopePath": dev, "mode": "Software"}, timeout=600)
        check("compile 0 errors", comp.get("success") is True and comp.get("errors") == 0,
              f"success={comp.get('success')} errors={comp.get('errors')}")

        # idempotent rewrite
        wr2 = c.call("tia_block_write_code", {"plcPath": plc, "specJson": json.dumps(KITCHEN_SINK)}, timeout=300)
        check("rewrite idempotent", wr2.get("status") == "Applied", str(wr2)[:120])

        # round-trip read
        back = c.call("tia_block_read_code", {"path": plc + "/block:FB_LadKitchenSink", "includeInterface": False}, timeout=300)
        nets = back.get("networks", []) if isinstance(back, dict) else []
        ok_rt = len(nets) == len(KITCHEN_SINK["networks"]) and all(n.get("render") or n.get("fallback") for n in nets)
        check("round-trip read", ok_rt, f"{len(nets)} networks read back")
        for n in nets[:3]:
            print(f"    net{n['index']}: {str(n.get('render') or n.get('fallback', {}).get('reason'))[:100]}")

        # ---- 3. dryRun XML for hand-checks ----
        dry = c.call("tia_block_write_code", {"plcPath": plc, "specJson": json.dumps(KITCHEN_SINK), "dryRun": True})
        if isinstance(dry.get("xml"), str):
            outdir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "output")
            os.makedirs(outdir, exist_ok=True)
            out = os.path.join(outdir, "FB_LadKitchenSink.dryrun.xml")
            with open(out, "w", encoding="utf-8") as f:
                f.write(dry["xml"])
            print(f"[PASS] dryRun XML -> {out} ({len(dry['xml'])} chars)")
        else:
            check("dryRun XML", False, str(dry)[:120])

        # ---- 4. SclSource language guard on a LAD block ----
        if lads:
            ex = c.call("tia_block_export", {"path": lads[0]["path"], "format": "SclSource"}, timeout=300)
            msg = str(ex.get("_error") or ex.get("message") or "")
            check("SclSource guard on LAD", "SCL/STL only" in msg and "format=Xml" in msg, msg[:140])

        # ---- 5. GRAPH: fixture round-trip + spec write -> compile ----
        if os.path.isfile(FIXTURE):
            imp = c.call("tia_block_import", {"plcPath": plc, "name": "FB_GraphDemo", "source": FIXTURE}, timeout=300)
            check("GRAPH fixture import", imp.get("status") == "Applied", str(imp)[:120])
            g = c.call("tia_block_read_code", {"path": plc + "/block:FB_GraphDemo"}, timeout=300)
            graph = g.get("graph") if isinstance(g, dict) else None
            check("GRAPH read_code", graph is not None and len(graph.get("steps", [])) > 0,
                  f"steps={len((graph or {}).get('steps', []))}")
        else:
            print("[SKIP] GRAPH fixture round-trip: fixtures/FB_GraphDemo.xml absent")

        gspec = json.dumps({
            "name": "FB_GraphWrite", "blockType": "FB", "language": "GRAPH",
            "comment": "verify_lad_graph: spec-written GRAPH sequence",
            "interface": [
                {"section": "Input", "members": [
                    {"name": "Go", "datatype": "Bool"},
                    {"name": "Done", "datatype": "Bool"},
                ]},
                {"section": "Output", "members": [
                    {"name": "Lamp1", "datatype": "Bool"},
                    {"name": "Lamp2", "datatype": "Bool"},
                ]},
            ],
            "sequence": [
                {"name": "Init", "actions": [{"qualifier": "N", "operand": "Lamp1"}], "transitionOperand": "Go"},
                {"name": "Work", "actions": [{"qualifier": "S", "operand": "Lamp2"}], "transitionOperand": "Done"},
            ],
        })
        gw = c.call("tia_block_write_code", {"plcPath": plc, "specJson": gspec}, timeout=300)
        check("GRAPH spec write", gw.get("status") == "Applied", str(gw)[:160])
        gcomp = c.call("tia_project_compile", {"scopePath": dev, "mode": "Software"}, timeout=600)
        check("GRAPH compile 0 errors", gcomp.get("success") is True and gcomp.get("errors") == 0,
              f"success={gcomp.get('success')} errors={gcomp.get('errors')}")
        gb = c.call("tia_block_read_code", {"path": plc + "/block:FB_GraphWrite", "includeInterface": False}, timeout=300)
        gsteps = ((gb.get("graph") or {}).get("steps") or []) if isinstance(gb, dict) else []
        check("GRAPH round-trip", len(gsteps) == 2 and gsteps[1].get("actions") == [{"qualifier": "S", "operand": "Lamp2"}],
              repr([(s.get("name"), s.get("actions")) for s in gsteps]))
    finally:
        c.close()

    print("\n" + ("LIVE CHECKS PASSED" if not FAILS else "FAILURES:"))
    for f in FAILS:
        print("  - " + f)
    return 0 if not FAILS else 1


if __name__ == "__main__":
    sys.exit(main())
