#!/usr/bin/env python3
"""Offline smoke for the structured LAD/GRAPH read+write tools (Fake backend, CI-able).

Covers tia_block_read_code / tia_block_write_code end to end:
  * read_code on the seeded LAD block (启保停) renders the boolean expression + logic tree;
  * read_code honors network range / interface filters; SCL sources pass through as text;
  * write_code (standard instruction set: contacts / and-or / coil / set / reset / timers /
    MOVE / compares) imports and round-trips through read_code;
  * re-writing the same name is idempotent (ImportOptions.Override semantics);
  * ReadOnly denies write_code but allows dryRun; malformed specs fail with clear messages;
  * GRAPH write reports the bootstrap requirement until the fixture lands (S3).

Usage:  python brands/tia/mcp/tests/smoke_lad_graph.py <path-to-dll>
"""
import json
import sys

from mcp_client import Client, paths

FAILURES = []


def check(label, ok, detail=""):
    print(f"[{'PASS' if ok else 'FAIL'}] {label}" + (f"  {detail}" if detail else ""))
    if not ok:
        FAILURES.append(f"{label}: {detail}")


def main() -> int:
    dll = sys.argv[1]
    c = Client(dll, backend="fake", mode="ReadWrite", client_name="smoke-lad-graph")
    try:
        tools = c.initialize()
        names = {t["name"] for t in tools}
        check("tools registered", "tia_block_read_code" in names and "tia_block_write_code" in names,
              f"{len(tools)} tools")

        s = paths(c.call("tia_connect", {"mode": "headless"})["path"])
        plc = s["plc"]

        # ---- read: seeded LAD block renders the start-hold-stop expression ----
        lad = c.call("tia_block_read_code", {"path": plc + "/block:FC_MotorLAD"})
        check("lad language", lad.get("language") == "LAD", repr(lad.get("language")))
        nets = lad.get("networks") or []
        check("lad one network", len(nets) == 1, f"{len(nets)}")
        if nets:
            n = nets[0]
            check("lad render", n.get("render") == "(Start_PB OR Motor) AND NOT Stop_PB = ( ) Motor",
                  repr(n.get("render")))
            logic = n.get("logic") or {}
            check("lad logic op", logic.get("op") == "and", repr(logic.get("op")))
            outs = n.get("outputs") or []
            check("lad output coil", len(outs) == 1 and outs[0].get("kind") == "coil"
                  and outs[0].get("operand") == "Motor", repr(outs))
            check("lad interface sections",
                  any(sec.get("name") == "Return" for sec in (lad.get("interface") or [])))

        # ---- read: filters ----
        filt = c.call("tia_block_read_code", {"path": plc + "/block:FC_MotorLAD",
                                              "networkFrom": 0, "networkTo": 0, "includeInterface": False})
        check("network filter", len(filt.get("networks") or []) == 1 and filt.get("interface") is None)

        # ---- read: SCL source passes through as text ----
        scl = c.call("tia_block_read_code", {"path": plc + "/block:FB_Motor"})
        sn = (scl.get("networks") or [{}])[0]
        check("scl passthrough", sn.get("kind") == "scl" and "#Running :=" in (sn.get("text") or ""),
              repr(sn.get("kind")))

        # ---- read: not-found is a structured error, not a crash ----
        nf = c.call("tia_block_read_code", {"path": plc + "/block:NoSuch"})
        check("read not-found", "message" in nf or "_error" in nf, repr(nf)[:100])

        # ---- write: standard-set LAD spec round-trips through read_code ----
        spec = {
            "name": "FB_ConveyorLAD",
            "blockType": "FB",
            "language": "LAD",
            "comment": "smoke: conveyor start-hold-stop + delay stop + analog guard",
            "interface": [
                {"section": "Input", "members": [
                    {"name": "Start_PB", "datatype": "Bool"},
                    {"name": "Stop_PB", "datatype": "Bool"},
                    {"name": "Level", "datatype": "Real"},
                ]},
                {"section": "Output", "members": [
                    {"name": "Motor", "datatype": "Bool"},
                    {"name": "StopLamp", "datatype": "Bool"},
                    {"name": "Alarm", "datatype": "Bool"},
                    {"name": "Scaled", "datatype": "Real"},
                ]},
                {"section": "Static", "members": [
                    {"name": "StopTimer", "datatype": "TOF_TIME"},
                ]},
            ],
            "networks": [
                {"title": "启保停",
                 "rungs": [{"logic": {"op": "and", "args": [
                     {"op": "or", "args": [{"op": "contact", "operand": "Start_PB"},
                                            {"op": "contact", "operand": "Motor"}]},
                     {"op": "contact", "operand": "Stop_PB", "negated": True}]},
                     "output": {"kind": "coil", "operand": "Motor"}}]},
                {"title": "停机延时",
                 "rungs": [{"logic": {"op": "contact", "operand": "Motor", "negated": True},
                            "output": {"kind": "tof", "instance": "StopTimer", "pt": "T#3S",
                                       "q": {"kind": "coil", "operand": "StopLamp"}}}]},
                {"title": "液位报警与标定",
                 "rungs": [
                     {"logic": {"op": "contact", "operand": "Level"},
                      "output": {"kind": "compare", "part": "Ge", "in1": "Level", "in2": "8.0",
                                 "out": {"kind": "set", "operand": "Alarm"}}},
                     {"logic": {"op": "contact", "operand": "Level"},
                      "output": {"kind": "move", "src": "Level", "dst": "Scaled"}},
                 ]},
            ],
        }
        spec_json = json.dumps(spec)
        wr = c.call("tia_block_write_code", {"plcPath": plc, "specJson": spec_json})
        check("write applied", wr.get("status") == "Applied", repr(wr)[:200])

        back = c.call("tia_block_read_code", {"path": plc + "/block:FB_ConveyorLAD", "includeInterface": False})
        bn = back.get("networks") or []
        check("round-trip networks", len(bn) == 3, f"{len(bn)}")
        if len(bn) == 3:
            check("round-trip rung1", bn[0].get("render") == "(Start_PB OR Motor) AND NOT Stop_PB = ( ) Motor",
                  repr(bn[0].get("render")))
            check("round-trip timer", any(b.get("part") == "TOF" for b in (bn[1].get("boxes") or [])),
                  repr(bn[1].get("boxes")))
            outs2 = bn[1].get("outputs") or []
            check("round-trip timer coil", any(o.get("operand") == "StopLamp" for o in outs2), repr(outs2))
            r3 = " ; ".join((bn[2].get("render") or "").split(" ; "))
            check("round-trip set/move", "(S) Alarm" in (bn[2].get("render") or "")
                  and "MOVE" in (bn[2].get("render") or ""), repr(bn[2].get("render")))

        # ---- write: same name again is idempotent (Override) ----
        wr2 = c.call("tia_block_write_code", {"plcPath": plc, "specJson": spec_json})
        check("rewrite idempotent", wr2.get("status") == "Applied", repr(wr2)[:120])

        # ---- write: malformed spec -> clear Failed ----
        bad = c.call("tia_block_write_code", {"plcPath": plc, "specJson": "{ not json"})
        check("malformed spec", bad.get("status") == "Failed" and "spec" in (bad.get("message") or "").lower(),
              repr(bad)[:120])

        # ---- GRAPH: seeded block reads as a structured sequence ----
        g = c.call("tia_block_read_code", {"path": plc + "/block:FB_GraphDemo", "includeInterface": False})
        graph = g.get("graph") or {}
        steps = graph.get("steps") or []
        check("graph seeded steps", len(steps) == 3 and steps[0].get("name") == "Init" and steps[0].get("init") is True,
              repr([(s.get("name"), s.get("init")) for s in steps]))
        if len(steps) == 3:
            check("graph actions", steps[1].get("actions") == [{"qualifier": "N", "operand": "Out2"}],
                  repr(steps[1].get("actions")))
            check("graph supervision", steps[0].get("supervision") == "Cond1", repr(steps[0].get("supervision")))
        trans = graph.get("transitions") or []
        check("graph transitions", len(trans) == 3 and trans[0].get("condition") == "Cond1",
              repr([(t.get("name"), t.get("condition")) for t in trans]))

        # ---- GRAPH write: spec -> import -> read round-trip ----
        gspec = {
            "name": "FB_GraphSmoke", "blockType": "FB", "language": "GRAPH",
            "interface": [
                {"section": "Input", "members": [{"name": "Go", "datatype": "Bool"},
                                                  {"name": "Done", "datatype": "Bool"}]},
                {"section": "Output", "members": [{"name": "Lamp1", "datatype": "Bool"},
                                                   {"name": "Lamp2", "datatype": "Bool"}]},
            ],
            "sequence": [
                {"name": "Init", "actions": [{"qualifier": "N", "operand": "Lamp1"}], "transitionOperand": "Go"},
                {"name": "Work", "actions": [{"qualifier": "S", "operand": "Lamp2"}], "transitionOperand": "Done"},
            ],
        }
        gw = c.call("tia_block_write_code", {"plcPath": plc, "specJson": json.dumps(gspec)})
        check("graph write applied", gw.get("status") == "Applied", repr(gw)[:160])
        gb = c.call("tia_block_read_code", {"path": plc + "/block:FB_GraphSmoke", "includeInterface": False})
        gs = (gb.get("graph") or {}).get("steps") or []
        check("graph round-trip", len(gs) == 2 and gs[0].get("name") == "Init" and
              gs[1].get("actions") == [{"qualifier": "S", "operand": "Lamp2"}],
              repr([(s.get("name"), s.get("actions")) for s in gs]))

        # ---- GRAPH write validation: bad qualifier -> clear Failed ----
        badq = json.dumps({**gspec, "sequence": [{"name": "X", "actions": [{"qualifier": "NOPE", "operand": "Lamp1"}]}]})
        gbad = c.call("tia_block_write_code", {"plcPath": plc, "specJson": badq})
        check("graph bad qualifier", gbad.get("status") == "Failed" and "qualifier" in (gbad.get("message") or ""),
              repr(gbad)[:120])
    finally:
        c.close()

    # ---- ReadOnly: write denied, dryRun allowed ----
    c = Client(dll, backend="fake", mode="ReadOnly", client_name="smoke-lad-graph-ro")
    try:
        c.initialize()
        s = paths(c.call("tia_connect", {"mode": "headless"})["path"])
        denied = c.call("tia_block_write_code", {"plcPath": s["plc"], "specJson": spec_json})
        check("write denied in ReadOnly", denied.get("status") == "Denied", repr(denied)[:120])
        dry = c.call("tia_block_write_code", {"plcPath": s["plc"], "specJson": spec_json, "dryRun": True})
        check("dryRun ok in ReadOnly", isinstance(dry.get("xml"), str) and "<Document>" in dry["xml"],
              repr(dry)[:120])
    finally:
        c.close()

    print("\n" + ("ALL CHECKS PASSED" if not FAILURES else "FAILURES:"))
    for f in FAILURES:
        print("  - " + f)
    return 0 if not FAILURES else 1


if __name__ == "__main__":
    sys.exit(main())
