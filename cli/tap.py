#!/usr/bin/env python3
"""GreyHackCLI — Milestone 3 read-only tap viewer.

Connects to the in-game bridge and prints every chunk of terminal output the game
produces (each tagged with its window PID). Pure stdlib; no deps.

    python cli/tap.py

Then type things in any in-game terminal — you should see the output mirrored here.
Quit with Ctrl-C. (Output may contain TMP rich-text tags like <color=...>; that's
expected for now — the full client will strip them.)
"""
import socket
import sys

HOST, PORT = "127.0.0.1", 8642


def main() -> int:
    try:
        sock = socket.create_connection((HOST, PORT), timeout=5)
    except (ConnectionRefusedError, OSError) as e:
        print(f"Could not connect to {HOST}:{PORT} — is Grey Hack running with the "
              f"GreyHackCLI plugin loaded?\n  ({e})")
        return 1

    print(f"Connected to GreyHackCLI tap at {HOST}:{PORT}. Ctrl-C to quit.\n")
    with sock:
        sock.settimeout(None)
        while True:
            data = sock.recv(4096)
            if not data:
                print("\n[connection closed by game]")
                return 0
            sys.stdout.write(data.decode("utf-8", errors="replace"))
            sys.stdout.flush()


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except KeyboardInterrupt:
        print("\n[bye]")
