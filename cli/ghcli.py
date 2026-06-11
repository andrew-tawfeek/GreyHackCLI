#!/usr/bin/env python3
"""GreyHackCLI — interactive headless shell client.

Opens an independent shell session on your in-game Grey Hack computer via the local bridge
and gives you a real terminal for it: line editing, persistent history, and Ctrl-C to cancel
a running command.

    python cli/ghcli.py

Keys:
  Up/Down        history (also fuzzy auto-suggest from history)
  Ctrl-C         at a prompt: clear the line; while a command runs: cancel it
  Ctrl-D         quit

Notes:
- After launching the game, open an in-game terminal once so the bridge learns your username.
- Output may carry Grey Hack TMP rich-text tags (<color=...> etc.); we strip the common ones.
  Pass --raw to see them verbatim.

Protocol (bridge -> client): length-framed messages "<TYPE> <LEN>\\n<payload>", where TYPE is
  O output, P prompt, A input request, W password request.
Client -> bridge: UTF-8 command lines terminated by '\\n'; a lone 0x03 byte cancels (Ctrl-C).
"""
import argparse
import os
import queue
import re
import socket
import sys
import threading

from prompt_toolkit import PromptSession
from prompt_toolkit.auto_suggest import AutoSuggestFromHistory
from prompt_toolkit.history import FileHistory
from prompt_toolkit.patch_stdout import patch_stdout

HOST, PORT = "127.0.0.1", 8642
HISTORY_FILE = os.path.expanduser("~/.greyhackcli_history")
_TAG_RE = re.compile(r"</?(?:color|b|i|u|size|noparse|mark|sub|sup|align)(?:=[^>]*)?>", re.IGNORECASE)


def clean(text: str, raw: bool) -> str:
    return text if raw else _TAG_RE.sub("", text)


class FrameReader:
    """Incrementally parses '<TYPE> <LEN>\\n<payload>' frames off a byte stream."""

    def __init__(self):
        self.buf = bytearray()
        self._need = None     # payload bytes still expected, or None if reading a header
        self._type = None

    def feed(self, data: bytes):
        self.buf += data

    def next_frame(self):
        """Return (type, payload_str) if a full frame is buffered, else None."""
        if self._need is None:
            nl = self.buf.find(b"\n")
            if nl < 0:
                return None
            parts = bytes(self.buf[:nl]).decode("ascii", "replace").split()
            del self.buf[:nl + 1]
            if len(parts) >= 2 and parts[1].isdigit():
                self._type = parts[0]
                self._need = int(parts[1])
            else:
                return self.next_frame()  # malformed header; skip and continue
        if len(self.buf) < self._need:
            return None
        payload = bytes(self.buf[:self._need]).decode("utf-8", "replace")
        del self.buf[:self._need]
        ftype, self._type, self._need = self._type, None, None
        return ftype, payload


def reader_thread(sock, prompt_q, raw, stop):
    fr = FrameReader()
    try:
        while not stop.is_set():
            frame = fr.next_frame()
            if frame is None:
                data = sock.recv(4096)
                if not data:
                    break
                fr.feed(data)
                continue
            ftype, payload = frame
            if ftype == "O":
                sys.stdout.write(clean(payload, raw))
                sys.stdout.flush()
            elif ftype == "P":
                prompt_q.put(("prompt", payload, False))
            elif ftype == "A":
                prompt_q.put(("ask", payload, False))
            elif ftype == "W":
                prompt_q.put(("ask", payload, True))
    except OSError:
        pass
    finally:
        stop.set()
        prompt_q.put(None)  # unblock the main loop


def wait_for_prompt(prompt_q, sock, stop):
    """Block until the server is ready for input. Ctrl-C here cancels the running command."""
    while not stop.is_set():
        try:
            return prompt_q.get(timeout=0.2)
        except queue.Empty:
            continue
        except KeyboardInterrupt:
            try:
                sock.sendall(b"\x03")
            except OSError:
                return None
    return None


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

    prompt_q = queue.Queue()
    stop = threading.Event()
    threading.Thread(target=reader_thread, args=(sock, prompt_q, args.raw, stop), daemon=True).start()

    session = PromptSession(history=FileHistory(HISTORY_FILE),
                            auto_suggest=AutoSuggestFromHistory())

    pending = None
    with patch_stdout():
        while True:
            if pending is None:
                pending = wait_for_prompt(prompt_q, sock, stop)
                if pending is None:
                    break
            _kind, text, is_pw = pending
            try:
                line = session.prompt(text, is_password=is_pw)
            except KeyboardInterrupt:
                continue           # clear the current line, re-show the same prompt
            except EOFError:
                break              # Ctrl-D
            pending = None
            try:
                sock.sendall((line + "\n").encode("utf-8"))
            except OSError:
                break

    stop.set()
    try:
        sock.shutdown(socket.SHUT_RDWR)
    except OSError:
        pass
    sock.close()
    print("[disconnected]")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except KeyboardInterrupt:
        pass
