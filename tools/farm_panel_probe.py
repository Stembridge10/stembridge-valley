#!/usr/bin/env python3
"""Talk to a Junimo Hollow server's farm panel the way the launcher does (test tool).

usage: farm_panel_probe.py HOST:PORT CODE OP [json-extra]
Prints the server's reply.
"""
import json, socket, struct, sys, random


def ask(address, code, op, extra=None, timeout=3.0):
    host, port = address.rsplit(":", 1)
    q = {"v": 1, "id": random.randint(1, 2**40), "code": code, "op": op, **(extra or {})}
    body = json.dumps(q).encode()
    packet = bytes([0, 0, 0]) + struct.pack("<H", len(body) * 8) + body
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.settimeout(timeout)
    for _ in range(3):
        s.sendto(packet, (host, int(port)))
        try:
            while True:
                data, _ = s.recvfrom(2048)
                if data[0] != 0:
                    continue
                n = struct.unpack("<H", data[3:5])[0] // 8
                reply = json.loads(data[5:5 + n])
                if reply.get("id") == q["id"]:
                    return reply
        except socket.timeout:
            continue
    return {"ok": False, "error": "no answer"}


if __name__ == "__main__":
    extra = json.loads(sys.argv[4]) if len(sys.argv) > 4 else None
    print(json.dumps(ask(sys.argv[1], sys.argv[2], sys.argv[3], extra), indent=1))
