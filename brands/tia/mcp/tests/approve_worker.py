#!/usr/bin/env python3
"""Re-approve the freshly built Openness worker via the VISIBLE dialog.

Every worker rebuild changes the exe fingerprint, and TIA's Openness access control then asks for
approval again (HKLM\\...\\Openness\\AllowList is keyed by SHA256). For headless spawns that dialog
is INVISIBLE -> the connect wedges until the RPC watchdog kills the worker. This probe spawns an
INTERACTIVE Portal so the dialog shows on the desktop: click 允许/Yes (tick "always allow" if
offered) once, and this build is registered — headless connects work again.

    python brands/tia/mcp/tests/approve_worker.py [server.dll]

Expect: a TIA Portal window opens; answer the Openness authorization prompt with Allow.
"""
import sys

from mcp_client import Client


def main() -> int:
    dll = sys.argv[1] if len(sys.argv) > 1 else \
        "D:/linxin/Learn/app/SiemensPLC/tia-plugins/plc-siemens/server/TiaMcp.Server.dll"

    c = Client(dll, backend="openness", mode="ReadWrite", client_name="approve-worker")
    c.initialize()
    try:
        # Above the server-side Connect watchdog (300s): we WANT the server's structured error
        # if the prompt goes unanswered, not a client-side cutoff.
        s = c.call("tia_connect", {"mode": "interactive"}, timeout=400)
        print("tia_connect(interactive) ->", s, flush=True)
        if "sessionId" in s:
            print("\nOK — worker approved (this build is now in the Openness allowlist)")
            return 0
        return 1
    finally:
        c.close()


if __name__ == "__main__":
    sys.exit(main())
