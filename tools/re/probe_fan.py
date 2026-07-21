#!/usr/bin/env python3
"""
probe_fan.py — capture the fan's side of the WiFi conversation.

The command protocol is fully reversed, but the *file upload* is a stateful,
chunked protocol whose flow control (the device's "configuration" reply and its
per-chunk acknowledgements) exists only on the wire — the vendor binary sends and
waits, it doesn't hardcode the replies. This script captures those replies so the
upload can be finished correctly instead of guessed.

SAFE: it connects, sends the known 24-byte handshake, and reads whatever the device
sends back. It does NOT send file data, so it cannot leave an upload half-open, and
it issues no destructive command.

Run it on the machine joined to the fan's AP (e.g. the Pi):

    python3 tools/re/probe_fan.py

Copy the entire output back to the assistant.
"""
import socket

HOST, PORT = "192.168.4.1", 20320
# header magic + trailer magic, exactly what FanProtocol.Handshake() sends.
HANDSHAKE = ("C0EEB7C9BAA3" + "C0EEBDF9E5B7").encode("ascii")


def dump(label, data):
    if not data:
        print(f"{label}: <nothing>")
        return
    print(f"{label}: {len(data)} bytes")
    print(f"  hex   : {data.hex(' ')}")
    ascii_ = "".join(chr(b) if 32 <= b < 127 else "." for b in data)
    print(f"  ascii : {ascii_}")


def main():
    print(f"connecting to {HOST}:{PORT} …")
    with socket.create_connection((HOST, PORT), timeout=5) as s:
        s.settimeout(4)
        print(f"connected. sending handshake ({len(HANDSHAKE)} bytes)")
        s.sendall(HANDSHAKE)

        # Read a few rounds — the device may reply in more than one packet.
        for i in range(4):
            try:
                data = s.recv(4096)
            except socket.timeout:
                print(f"round {i}: (timeout — no more data)")
                break
            if not data:
                print(f"round {i}: connection closed by device")
                break
            dump(f"round {i} reply", data)


if __name__ == "__main__":
    try:
        main()
    except Exception as e:
        print(f"error: {e}")
