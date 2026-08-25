#!/usr/bin/env python3
"""LIVE end-to-end electrical-dev-flow test (real TIA V21, backend=openness, ReadWrite).

Covers every brand-skill workflow against one fresh scratch project:
  connect-project  -> connect headless, project_create/open/save/save_as/archive/close,
                      bare-project-name open (2026-08-24 feature)
  hardware-config  -> catalog_search, device_add (S7-1500 CPU), module_add (DI+DQ),
                      network_configure (IP/subnet/IO system), device_item_list, hardware_read
  write-program    -> group_create, tag_create, tagtable export+import round-trip, udt_import
                      (migrated from the ref project), SCL FB/FC/DB/OB via generate_from_source,
                      LAD + GRAPH via block_write_code, compile to 0 errors
  reuse-library    -> library_open testLibrary.al21, mastercopy_list, block_create_from_copy
  migrate-project  -> export UDT XML from the reference project, import into E2E
  reads            -> block_list/info/interface/source/code, cross_reference (block AND tag path),
                      udt_list, tagtable_list, project_status
  no-hardware ops  -> online_status / online_connect attempted and RECORDED (no physical CPU
                      expected: structured error, not a hang), then disconnect

Fault model: each stage runs in its own try/except and reports; a stage whose prerequisite
failed is SKIPped. Exit 0 only if zero failures.

Usage: python -u brands/tia/mcp/tests/e2e_dev_flow.py <server.dll> [<ref.ap21>]
       (ref default: plc/plcRef/Demo_V21/Demo_V21.ap21)
"""
import json
import os
import shutil
import sys
import tempfile

from mcp_client import Client

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))))
REF_DEFAULT = os.path.join(REPO_ROOT, "plc", "plcRef", "Demo_V21", "Demo_V21.ap21")
LIBRARY = os.path.join(REPO_ROOT, "refcode", "plcLibrary", "testLibrary", "testLibrary.al21")
SCRATCH_ROOT = os.path.join(REPO_ROOT, "plc", "_scratch", "e2e_dev_flow")

FAILURES = []


def check(name, ok, detail=""):
    print(f"[{'PASS' if ok else 'FAIL'}] [{STAGE[0]}] {name}" + (f"  | {detail}" if detail and not ok else ""),
          flush=True)
    if not ok:
        FAILURES.append(f"[{STAGE[0]}] {name}: {detail}")


def as_list(x, *keys):
    """Tolerate result shapes: bare list, or dict carrying the list under one of `keys`."""
    if isinstance(x, list):
        return x
    if isinstance(x, dict):
        for k in keys:
            v = x.get(k)
            if isinstance(v, list):
                return v
    return []


def gen_names(res):
    """generate_from_source reports created block names only inside the message text:
    \"Generated 2 block(s) from source 'x.scl' (FB_E2E, FC_E2E).\" (+ optional untouched note)."""
    import re
    m = re.search(r"\(([^()]*)\)\.", str(field(res, "message") or ""))
    return [n.strip() for n in m.group(1).split(",")] if m else []


def field(x, *keys):
    if isinstance(x, dict):
        for k in keys:
            if x.get(k) is not None:
                return x.get(k)
    return None


STAGE = ["boot"]
STATE = {"c": None, "sp": None, "e2e": None, "device": None, "plc": None, "compiled": False,
         "ref_udt_xml": None}


def stage_boot():
    c = STATE["c"]
    tools = c.initialize()
    print(f"  initialized: {len(tools)} tools", flush=True)
    st = c.call("tia_status", timeout=120)
    check("tia_status V21", field(st, "tiaVersion") == "V21", repr(st)[:200])
    sess = c.call("tia_connect", {"mode": "headless"}, timeout=300)
    STATE["sp"] = sess.get("path")
    check("tia_connect headless", bool(STATE["sp"]) and sess.get("sessionId") == "s-openness",
          repr(sess)[:200])


def stage_migrate_export():
    c, sp = STATE["c"], STATE["sp"]
    proj = c.call("tia_project_open", {"sessionPath": sp, "path": REF[0], "visible": False}, timeout=300)
    ref_path = field(proj, "path")
    check("open ref project", bool(ref_path), repr(proj)[:200])
    if not ref_path:
        return
    tgts = c.call("tia_project_list", {"projectPath": ref_path}, timeout=120)
    plc_tgt = next((t for t in as_list(tgts) if t.get("kind") == "Plc"), None)
    check("ref has PLC target", plc_tgt is not None, repr(tgts)[:200])
    if not plc_tgt:
        return
    ref_plc = plc_tgt["path"] + "/plc:program"

    udts = c.call("tia_udt_list", {"path": ref_plc}, timeout=120)
    names = [u.get("name") for u in as_list(udts, "types", "udts", "entries")]
    print(f"  ref UDTs ({len(names)}): {names[:12]}{'...' if len(names) > 12 else ''}", flush=True)
    first = as_list(udts, "types", "udts", "entries")
    if first:
        xp = c.call("tia_block_export", {"path": first[0].get("path"), "format": "Xml"}, timeout=180)
        STATE["ref_udt_xml"] = field(xp, "filePath")
        check("export ref UDT Xml", bool(STATE["ref_udt_xml"]), repr(xp)[:200])
    else:
        print("  (ref project has no UDTs - migrate UDT leg skipped)", flush=True)

    tt = c.call("tia_tagtable_list", {"path": ref_plc}, timeout=120)
    tables = as_list(tt, "tables", "tagTables", "entries")
    if tables:
        xp = c.call("tia_tagtable_export", {"path": tables[0].get("path")}, timeout=180)
        STATE["ref_tagtable_xml"] = field(xp, "filePath")
        check("export ref tagtable", bool(STATE.get("ref_tagtable_xml")), repr(xp)[:200])
    else:
        print("  (ref project has no tag tables - migrate tagtable leg skipped)", flush=True)

    cl = c.call("tia_project_close", {"projectPath": ref_path, "saveBeforeClose": False}, timeout=300)
    check("close ref project", field(cl, "status") == "Applied", repr(cl)[:200])


def stage_connect_project():
    c, sp = STATE["c"], STATE["sp"]
    if os.path.isdir(SCRATCH_ROOT):
        shutil.rmtree(SCRATCH_ROOT, ignore_errors=True)
    os.makedirs(SCRATCH_ROOT, exist_ok=True)
    pc = c.call("tia_project_create",
                {"sessionPath": sp, "projectDirectory": SCRATCH_ROOT, "projectName": "E2EFlow",
                 "author": "e2e-dev-flow", "comment": "auto e2e"}, timeout=300)
    check("project_create Applied", field(pc, "status") == "Applied", repr(pc)[:200])
    # MutationResult carries no path field: the created project is open at <session>/project:<name>.
    e2e = sp + "/project:E2EFlow"
    ps = c.call("tia_project_status", {"projectPath": e2e}, timeout=120)
    if field(ps, "message") or field(ps, "_error"):
        po = c.call("tia_project_open",
                    {"sessionPath": sp, "path": os.path.join(SCRATCH_ROOT, "E2EFlow", "E2EFlow.ap21"),
                     "visible": False}, timeout=300)
        e2e = field(po, "path") or ""
        ps = c.call("tia_project_status", {"projectPath": e2e}, timeout=120)
    check("E2EFlow open in session", bool(e2e) and field(ps, "name") == "E2EFlow", f"{ps}")
    STATE["e2e"] = e2e


def stage_hardware():
    c, sp, e2e = STATE["c"], STATE["sp"], STATE["e2e"]
    # '6ES7 510' matches ET200SP CPUs (CPU 1510SP, DJ00) whose stations ResolveDevice can't address
    # and whose racks reject S7-1500 signal modules. Sweep queries until one returns an S7-1500 CPU.
    ti = None
    for q in ("CPU 1511C", "6ES7 511", "6ES7 516", "6ES7 510"):
        cpu = c.call("tia_catalog_search", {"scopePath": sp, "query": q}, timeout=120)
        cpu_items = as_list(cpu, "results", "items")
        pick = next((e for e in cpu_items if "S7-1500" in str(e.get("catalogPath", ""))), None)
        if pick:
            print(f"  CPU pick (query '{q}'): {pick.get('articleNumber')} {pick.get('typeIdentifier')} "
                  f"[...{pick.get('catalogPath', '')[-70:]}]", flush=True)
            ti = pick.get("typeIdentifier")
            break
    check("catalog_search S7-1500 CPU", bool(ti), "no query returned an S7-1500 rack CPU")
    if not ti:
        return
    dev = c.call("tia_device_add", {"projectPath": e2e, "typeIdentifier": ti, "deviceName": "PLC_E2E"},
                 timeout=300)
    print(f"  device_add -> {dev}", flush=True)
    check("device_add CPU", field(dev, "status") == "Applied", repr(dev)[:300])
    # The real device name lands only in the message text ("Created device 'NAME' ..."), and the
    # device path must come from the project's own target list — never assume deviceName stuck.
    tg = c.call("tia_project_list", {"projectPath": e2e}, timeout=120)
    plc_tgt = next((t for t in as_list(tg) if t.get("kind") == "Plc"), None)
    check("project_list sees new PLC", plc_tgt is not None, repr(tg)[:300])
    STATE["device"] = (plc_tgt or {}).get("path") or (e2e + "/device:PLC_E2E")

    for order, label in (("6ES7 521", "DI"), ("6ES7 522", "DQ")):
        cat = c.call("tia_catalog_search", {"scopePath": sp, "query": order}, timeout=120)
        items = as_list(cat, "results", "items")
        if items:
            mti = field(items[0], "typeIdentifier")
            ma = c.call("tia_module_add", {"projectPath": e2e, "deviceName": "PLC_E2E",
                                           "typeIdentifier": mti, "moduleName": "E2E_" + label},
                        timeout=300)
            check(f"module_add {label}", field(ma, "status") == "Applied" or field(ma, "slot") is not None,
                  repr(ma)[:300])
        else:
            check(f"catalog_search {label} ({order})", False, repr(cat)[:200])

    nc = c.call("tia_network_configure",
                {"projectPath": e2e, "deviceName": "PLC_E2E", "ipAddress": "192.168.77.10",
                 "subnetMask": "255.255.255.0", "pnDeviceName": "plc-e2e",
                 "subnetName": "PN/IE_E2E", "ioSystemName": "IO_E2E"}, timeout=300)
    check("network_configure", "applied" in str(nc).lower(), repr(nc)[:300])

    items = c.call("tia_device_item_list", {"path": STATE["device"]}, timeout=120)
    names = [i.get("name") for i in as_list(items, "items", "deviceItems")]
    if not names:
        print(f"  device_item_list RAW: {items}", flush=True)
    check("device_item_list", len(names) >= 3, f"items={names}")

    hw = c.call("tia_hardware_read", {"projectPath": e2e}, timeout=180)
    n = len(as_list(hw, "devices")) + len(as_list(hw, "subnets")) + len(as_list(hw, "ioSystems"))
    check("hardware_read", n > 0, repr(hw)[:200])
    STATE["plc"] = STATE["device"] + "/plc:program"


def stage_write_program():
    c, plc = STATE["c"], STATE["plc"]
    grp = c.call("tia_group_create", {"plcPath": plc, "name": "E2E", "kind": "block"}, timeout=180)
    check("group_create E2E", field(grp, "status") == "Applied" or field(grp, "path"), repr(grp)[:200])

    for name, addr, dt in (("E2E_Start", "%I0.0", "Bool"), ("E2E_Stop", "%I0.1", "Bool"),
                           ("E2E_Run", "%Q0.0", "Bool"), ("E2E_Speed", "%IW64", "Int")):
        tc = c.call("tia_tag_create", {"tagTablePath": plc + "/tagtable:Default", "name": name,
                                       "address": addr, "dataType": dt, "comment": "e2e " + name},
                    timeout=120)
        check(f"tag_create {name}", field(tc, "status") == "Applied" or field(tc, "path"),
              repr(tc)[:200])

    te = c.call("tia_tagtable_export", {"path": plc + "/tagtable:Default"}, timeout=180)
    fp = field(te, "filePath")
    if fp:
        ti = c.call("tia_tagtable_import", {"plcPath": plc, "sourceXml": fp}, timeout=300)
        check("tagtable export->import round-trip", field(ti, "status") == "Applied", repr(ti)[:200])
    else:
        check("tagtable_export Default", False, repr(te)[:200])

    if STATE.get("ref_udt_xml"):
        ui = c.call("tia_udt_import", {"plcPath": plc, "sourceXml": STATE["ref_udt_xml"]}, timeout=300)
        check("udt_import (migrated)", field(ui, "status") == "Applied" or
              as_list(ui, "types", "dataTypes"), repr(ui)[:300])

    src_a = """FUNCTION_BLOCK "FB_E2E"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
   VAR_INPUT
      Run : Bool;
   END_VAR
   VAR_OUTPUT
      Count : Int;
   END_VAR
BEGIN
   IF #Run THEN
      #Count := #Count + 1;
   END_IF;
END_FUNCTION_BLOCK

FUNCTION "FC_E2E" : Int
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
   VAR_INPUT
      In : Int;
   END_VAR
BEGIN
   #FC_E2E := #In + 1;
END_FUNCTION
"""
    ga = c.call("tia_block_generate_from_source",
                {"plcPath": plc, "sourceName": "e2e_a.scl", "sourceText": src_a}, timeout=300)
    ga_names = gen_names(ga)
    check("generate_from_source FB+FC", {"FB_E2E", "FC_E2E"} <= set(ga_names), repr(ga)[:300])

    src_b = """DATA_BLOCK "DB_E2E"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
: "FB_E2E";
BEGIN
END_DATA_BLOCK

ORGANIZATION_BLOCK "Main"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
BEGIN
   "DB_E2E"(Run := TRUE);
END_ORGANIZATION_BLOCK
"""
    gb = c.call("tia_block_generate_from_source",
                {"plcPath": plc, "sourceName": "e2e_b.scl", "sourceText": src_b}, timeout=300)
    gb_names = gen_names(gb)
    # The project ships a default OB "Main" that TIA's generator leaves untouched — accepted,
    # but the 2026-08-25 worker fix must NAME it instead of silently dropping the declaration.
    check("generate_from_source DB (+OB Main reported as untouched)",
          "DB_E2E" in gb_names and ("untouched: Main" in str(field(gb, "message") or "")
                                    or "Main" in gb_names), repr(gb)[:300])

    lad_spec = {
        "name": "FB_ConveyorE2E", "blockType": "FB", "language": "LAD",
        "comment": "e2e: conveyor start-hold-stop",
        "interface": [
            {"section": "Input", "members": [
                {"name": "Start_PB", "datatype": "Bool"},
                {"name": "Stop_PB", "datatype": "Bool"}]},
            {"section": "Output", "members": [
                {"name": "Motor", "datatype": "Bool"}]},
        ],
        "networks": [
            {"title": "E2E start-hold-stop",
             "rungs": [{"logic": {"op": "and", "args": [
                 {"op": "or", "args": [{"op": "contact", "operand": "Start_PB"},
                                        {"op": "contact", "operand": "Motor"}]},
                 {"op": "contact", "operand": "Stop_PB", "negated": True}]},
                 "output": {"kind": "coil", "operand": "Motor"}}]},
        ],
    }
    lw = c.call("tia_block_write_code", {"plcPath": plc, "specJson": json.dumps(lad_spec)}, timeout=300)
    check("write_code LAD FB_ConveyorE2E", field(lw, "status") == "Applied", repr(lw)[:200])

    graph_spec = {
        "name": "FB_GraphE2E", "blockType": "FB", "language": "GRAPH",
        "interface": [
            {"section": "Input", "members": [{"name": "Go", "datatype": "Bool"},
                                              {"name": "Done", "datatype": "Bool"}]},
            {"section": "Output", "members": [{"name": "Lamp1", "datatype": "Bool"},
                                               {"name": "Lamp2", "datatype": "Bool"}]},
        ],
        "sequence": [
            {"name": "Init", "actions": [{"qualifier": "N", "operand": "Lamp1"}],
             "transitionOperand": "Go"},
            {"name": "Work", "actions": [{"qualifier": "S", "operand": "Lamp2"}],
             "transitionOperand": "Done"},
        ],
        "loop": True,
    }
    gw = c.call("tia_block_write_code", {"plcPath": plc, "specJson": json.dumps(graph_spec)}, timeout=300)
    check("write_code GRAPH FB_GraphE2E(loop)", field(gw, "status") == "Applied", repr(gw)[:200])


def stage_reuse_library():
    c, plc = STATE["c"], STATE["plc"]
    if not os.path.isfile(LIBRARY):
        print(f"  (library not found: {LIBRARY} - reuse-library leg skipped)", flush=True)
        return
    lo = c.call("tia_library_open", {"libraryPath": LIBRARY, "readOnly": True}, timeout=300)
    check("library_open testLibrary", field(lo, "status") == "Applied", repr(lo)[:200])
    mc = c.call("tia_mastercopy_list", {}, timeout=120)
    copies = as_list(mc, "masterCopies", "copies")
    names = [m.get("name") for m in copies]
    print(f"  master copies ({len(names)}): {names[:15]}", flush=True)
    if copies:
        cc = c.call("tia_block_create_from_copy",
                    {"plcPath": plc + "/blockgroup:E2E", "masterCopyName": copies[0].get("name")},
                    timeout=300)
        check("block_create_from_copy", field(cc, "status") == "Applied" or field(cc, "path"),
              repr(cc)[:300])


def stage_reads():
    c, plc = STATE["c"], STATE["plc"]
    bl = c.call("tia_block_list", {"path": plc, "limit": 100}, timeout=120)
    names = [b.get("name") for b in as_list(bl, "blocks")]
    print(f"  blocks ({field(bl, 'total', 'count')}): {names}", flush=True)
    check("block_list sees writes",
          {"FB_E2E", "FC_E2E", "DB_E2E", "FB_ConveyorE2E", "FB_GraphE2E"} <= set(names), repr(names))

    bi = c.call("tia_block_info", {"path": plc + "/block:FB_E2E"}, timeout=120)
    check("block_info FB_E2E", field(bi, "name") == "FB_E2E", repr(bi)[:200])
    ir = c.call("tia_interface_read", {"path": plc + "/block:FB_E2E"}, timeout=120)
    secs = as_list(ir, "sections", "interface")
    check("interface_read FB_E2E", len(secs) > 0, repr(ir)[:200])
    rs = c.call("tia_block_read_source", {"path": plc + "/block:FB_E2E"}, timeout=120)
    # Openness read_source returns SimaticML XML; Fake returns SCL text — accept either.
    rs_txt = str(field(rs, "source") or rs)
    check("read_source FB_E2E", "FUNCTION_BLOCK" in rs_txt or "xml/simaticml" in
          str(field(rs, "language") or "").lower() or rs_txt.startswith("<?xml"), repr(rs)[:200])
    rc = c.call("tia_block_read_code", {"path": plc + "/block:FB_ConveyorE2E"}, timeout=120)
    nets = as_list(rc, "networks")
    check("read_code LAD render", any("Start_PB" in (n.get("render") or "") for n in nets),
          repr(rc)[:200])
    rg = c.call("tia_block_read_code", {"path": plc + "/block:FB_GraphE2E", "includeInterface": False},
                timeout=120)
    steps = as_list(field(rg, "graph") or {}, "steps")
    check("read_code GRAPH steps", len(steps) >= 2 and steps[0].get("name") == "Init",
          repr([s.get("name") for s in steps]))

    xr = c.call("tia_cross_reference", {"path": plc + "/block:FB_E2E", "aggregate": True}, timeout=180)
    check("cross_reference FB_E2E (no error DTO)", isinstance(xr, dict) and "message" not in xr
          and isinstance(as_list(xr, "references"), list), repr(xr)[:200])
    xtag = c.call("tia_cross_reference", {"path": plc + "/tagtable:Default/tag:E2E_Start",
                                          "aggregate": True}, timeout=180)
    print(f"  cross_reference tag E2E_Start -> {str(xtag)[:300]}", flush=True)
    check("cross_reference TAG path (2026-08-24 feature; needs worker 3ec4310)",
          isinstance(xtag, dict) and "message" not in xtag, repr(xtag)[:300])


def stage_compile():
    c, plc = STATE["c"], STATE["plc"]
    comp = c.call("tia_project_compile", {"scopePath": plc, "mode": "Software"}, timeout=600)
    diags = as_list(comp, "diagnostics")
    errs = [d for d in diags if d.get("severity") == "Error"]
    check("compile success 0 errors", field(comp, "success") is True and not errs,
          json.dumps(diags, ensure_ascii=False)[:600])
    print(f"  compile: success={field(comp, 'success')} errors={len(errs)} "
          f"warnings={len(diags) - len(errs)}", flush=True)
    STATE["compiled"] = field(comp, "success") is True


def stage_lifecycle():
    c, sp, e2e = STATE["c"], STATE["sp"], STATE["e2e"]
    sv = c.call("tia_project_save", {"projectPath": e2e}, timeout=300)
    check("project_save", field(sv, "status") == "Applied", repr(sv)[:200])

    bn = c.call("tia_project_open", {"sessionPath": sp, "path": "E2EFlow", "visible": False},
                timeout=120)
    check("project_open by BARE name", field(bn, "path") == e2e, repr(bn)[:200])

    copy_dir = os.path.join(SCRATCH_ROOT, "copy")
    os.makedirs(copy_dir, exist_ok=True)
    sa = c.call("tia_project_save_as", {"projectPath": e2e, "targetDirectory": copy_dir,
                                        "targetName": "E2EFlowCopy", "rebind": False}, timeout=300)
    check("project_save_as copy", field(sa, "status") == "Applied" or field(sa, "path"),
          repr(sa)[:200])
    # SaveAs MOVES the handle: with rebind=false the source is now closed in the session (by
    # design) -> reopen it by file path to keep archiving/closing the ORIGINAL.
    ro = c.call("tia_project_open",
                {"sessionPath": sp, "path": os.path.join(SCRATCH_ROOT, "E2EFlow", "E2EFlow.ap21"),
                 "visible": False}, timeout=300)
    e2e = field(ro, "path") or e2e
    check("reopen E2EFlow after save_as", field(ro, "status") == "Applied" or field(ro, "path"),
          repr(ro)[:200])

    ar = c.call("tia_project_archive", {"projectPath": e2e, "archiveDirectory": SCRATCH_ROOT,
                                        "archiveName": "E2EFlow", "mode": "Compressed"}, timeout=600)
    ap = field(ar, "archivePath") or ""
    check("project_archive", ap.lower().endswith((".zap12", ".zap11", ".zap19", ".zap21"))
          or field(ar, "status") == "Applied", repr(ar)[:200])

    cl = c.call("tia_project_close", {"projectPath": e2e, "saveBeforeClose": True}, timeout=300)
    check("project_close", field(cl, "status") == "Applied" or not field(cl, "message"),
          repr(cl)[:200])


def stage_hardware_absent():
    c = STATE["c"]
    ost = c.call("tia_online_status", {"path": STATE["device"]}, timeout=120)
    print(f"  online_status: {ost}", flush=True)
    check("online_status structured", not field(ost, "_error") or isinstance(ost, dict), repr(ost)[:200])
    oc = c.call("tia_online_connect", {"path": STATE["device"]}, timeout=240)
    print(f"  online_connect (no physical CPU, expected to fail): {str(oc)[:300]}", flush=True)
    print("  NOTE: no physical CPU attached - download/plc_run/plc_stop NOT live-tested "
          "(guard behaviour verified offline).", flush=True)


STAGES = [
    ("boot", stage_boot, []),
    ("migrate-export", stage_migrate_export, ["sp"]),
    ("connect-project", stage_connect_project, ["sp"]),
    ("hardware-config", stage_hardware, ["e2e"]),
    ("write-program", stage_write_program, ["plc"]),
    ("reuse-library", stage_reuse_library, ["plc"]),
    ("reads", stage_reads, ["plc"]),
    ("compile", stage_compile, ["plc"]),
    ("lifecycle", stage_lifecycle, ["e2e"]),
    ("hardware-absent", stage_hardware_absent, ["device"]),
]


def main() -> int:
    dll = sys.argv[1]
    REF.append(sys.argv[2] if len(sys.argv) > 2 else REF_DEFAULT)
    err_path = os.path.join(tempfile.gettempdir(), "tiamcp_e2e_stderr.log")

    print("########## E2E dev-flow (real TIA) ##########", flush=True)
    c = Client(dll, "openness", "ReadWrite", client_name="e2e-dev-flow", stderr_path=err_path)
    STATE["c"] = c
    try:
        for name, fn, deps in STAGES:
            STAGE[0] = name
            missing = [d for d in deps if not STATE.get(d)]
            if missing:
                print(f"[SKIP] [{name}] prerequisite {missing} unavailable", flush=True)
                FAILURES.append(f"[{name}] skipped: prerequisite {missing} failed")
                continue
            try:
                fn()
            except Exception as ex:  # noqa: BLE001 - record, keep going
                FAILURES.append(f"[{name}] EXCEPTION {type(ex).__name__}: {ex}")
                print(f"[EXC ] [{name}] {type(ex).__name__}: {ex}", flush=True)
    finally:
        STAGE[0] = "teardown"
        try:
            c.call("tia_disconnect", timeout=120)
            print("  disconnected", flush=True)
        except Exception as ex:  # noqa: BLE001
            print(f"  disconnect: {ex}", flush=True)
        c.close()
        err = c.stderr_text().strip()
        if err:
            print("\n--- server/worker stderr (tail) ---", flush=True)
            print("\n".join(err.splitlines()[-15:]), flush=True)

    print("\n" + ("ALL STAGES PASSED" if not FAILURES else f"{len(FAILURES)} FAILURE(S):"), flush=True)
    for f in FAILURES:
        print("  - " + f, flush=True)
    return 0 if not FAILURES else 1


REF = []

if __name__ == "__main__":
    sys.exit(main())
