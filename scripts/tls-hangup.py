#!/usr/bin/env python3
"""A TLS server that hangs up mid-handshake, for scripts/tls-interop.sh.

Accepts one connection on 127.0.0.1:PORT, reads the ClientHello, then:

  close        closes the connection (FIN)
  reset        resets it (RST)
  half         sends half a record, then closes
  hello-reset  sends a ServerHello (X25519, AES-128-GCM, the client's session
               ID echoed) and resets: the client has handshake keys by then
  bad-record   sends that ServerHello and a record that fails its check, then
               resets, so the client's alert goes into a dead connection

Usage: tls-hangup.py PORT MODE [READY_FILE]
(READY_FILE is created once it listens.)
"""
import os
import socket
import struct
import sys
import time

port, mode = int(sys.argv[1]), sys.argv[2]
listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
listener.bind(("127.0.0.1", port))
listener.listen(1)
if len(sys.argv) > 3:
    open(sys.argv[3], "w").close()
connection, _ = listener.accept()


def read_exactly(n):
    data = b""
    while len(data) < n:
        chunk = connection.recv(n - len(data))
        if not chunk:
            sys.exit(0)
        data += chunk
    return data


def reset():
    # SO_LINGER with a zero timeout: close sends RST instead of FIN.
    connection.setsockopt(socket.SOL_SOCKET, socket.SO_LINGER, struct.pack("ii", 1, 0))
    connection.close()


header = read_exactly(5)
hello = read_exactly(struct.unpack(">H", header[3:5])[0])
session_id = hello[4 + 2 + 32 + 1 : 4 + 2 + 32 + 1 + hello[4 + 2 + 32]]

if mode == "close":
    connection.close()
elif mode == "reset":
    reset()
elif mode == "half":
    connection.sendall(b"\x16\x03\x03\x00\x40" + b"\x02\x00\x00")
    connection.close()
else:
    extensions = (
        b"\x00\x2b\x00\x02\x03\x04"                               # supported_versions: TLS 1.3
        + b"\x00\x33\x00\x24\x00\x1d\x00\x20" + os.urandom(32)    # key_share: X25519
    )
    body = (
        b"\x03\x03" + os.urandom(32)
        + bytes([len(session_id)]) + session_id
        + b"\x13\x01" + b"\x00"
        + struct.pack(">H", len(extensions)) + extensions
    )
    message = b"\x02" + struct.pack(">I", len(body))[1:] + body
    connection.sendall(b"\x16\x03\x03" + struct.pack(">H", len(message)) + message)
    if mode == "bad-record":
        connection.sendall(b"\x17\x03\x03\x00\x20" + os.urandom(32))
        time.sleep(0.2)
    reset()
