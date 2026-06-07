#!/usr/bin/env python3
"""GreyHackCLI — interactive headless shell client (Milestone 4).

Opens an independent shell session on your in-game Grey Hack computer over the local
bridge and gives you a real terminal for it. Pure stdlib.

    python cli/ghcli.py

Type commands; output streams back. Ctrl-C or Ctrl-D to quit.

Notes
- The very first time after launching the game, open an in-game terminal once so the bridge
  can learn your username, then connect here.
- Output may contain Grey Hack's TMP rich-text tags (<color=...>, <b>, ...). We strip the
  common ones for readability; pass --raw to see them verbatim.
"""
import argparse
import os
import re
import socket
import sys
import threading

HOST, PORT = "127.0.0.1", 8642

_TAG_RE = re.compile(r"</?(?:color|b|i|u|size|noparse|mark|sub|sup|align)(?:=[^>]*)?>",
                     re.IGNORECASE)


def clean(text: str, raw: bool) -> str:
    return text if raw else _TAG_RE.sub("", text)


def reader_thread(sock: socket.socket, raw: bool, stop: threading.Event) -> None:
    try:
        while not stop.is_set():
            data = sock.recv(4096)
            if not data:
                break
            sys.stdout.write(clean(data.decode("utf-8", errors="replace"), raw))
            sys.stdout.flush()
    except OSError:
        pass
    finally:
        stop.set()
        sys.stdout.write("\n[disconnected]\n")
        sys.stdout.flush()
        # Main thread is blocked on stdin.readline(); exit hard to avoid a daemon-thread
        # stdout-lock error at interpreter shutdown.
        os._exit(0)


def main() -> int:
    ap = argparse.ArgumentParser(description="GreyHackCLI interactive client")
    ap.add_argument("--host", default=HOST)
    ap.add_argument("--port", type=int, default=PORT)
    ap.add_argument("--raw", action="store_true", help="don't strip rich-text tags")
    args = ap.parse_args()

    try:
        sock = socket.create_connection((args.host, args.port), timeout=5)
    except (ConnectionRefusedError, OSError) as e:
        print(f"Could not connect to {args.host}:{args.port} — is Grey Hack running with the "
              f"GreyHackCLI plugin loaded?\n  ({e})")
        return 1
    sock.settimeout(None)

    stop = threading.Event()
    rt = threading.Thread(target=reader_thread, args=(sock, args.raw, stop), daemon=True)
    rt.start()

    try:
        while not stop.is_set():
            try:
                line = sys.stdin.readline()
            except KeyboardInterrupt:
                break
            if line == "":  # EOF (Ctrl-D)
                break
            try:
                sock.sendall(line.encode("utf-8"))
            except OSError:
                break
    finally:
        stop.set()
        try:
            sock.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass
        sock.close()
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except KeyboardInterrupt:
        print("\n[bye]")
