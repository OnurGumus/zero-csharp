# Runs a C64 program in VICE without the debug cartridge (which ends the
# emulator the moment a program ends), so that the program returns to BASIC,
# and types A=42:POKE 2,A for BASIC to run then: a variable, so BASIC needs
# memory of its own. BASIC that came back healthy runs it; one left broken
# answers ?OUT OF MEMORY ERROR, or never reads it.
# The check reads address 2 through VICE's remote monitor.
# Usage: python3 basic_after.py EMULATOR PROGRAM.prg [VICE OPTIONS...]
# (the options: where the ROMs are, as c64/run.sh gives them on Linux)
import re, socket, subprocess, sys, time

emulator, program, extra = sys.argv[1], sys.argv[2], sys.argv[3:]
limit = 120

# A port nobody uses, so that a VICE still closing down doesn't answer.
with socket.socket() as probe:
    probe.bind(("127.0.0.1", 0))
    port = probe.getsockname()[1]
vice = subprocess.Popen(
    [emulator, "-default", "+saveres", "+confirmonexit", "-warp", "+sound", "-minimized",
     "-remotemonitor", "-remotemonitoraddress", f"127.0.0.1:{port}", *extra,
     "-autostartprgmode", "1", "-autostart", program, "-keybuf", "a=42:poke 2,a\\n"],
    stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
try:
    start = time.time()
    sock = None
    while sock is None and time.time() - start < 30 and vice.poll() is None:
        try:
            sock = socket.create_connection(("127.0.0.1", port), timeout=5)
        except OSError:
            time.sleep(0.5)
    if sock is None:
        said = vice.stderr.read().decode(errors="replace")[-400:] if vice.poll() is not None else "(still starting)"
        print("no VICE monitor; VICE said:", said)
        sys.exit(1)

    def ask(command):
        sock.sendall((command + "\n").encode())
        data = b""
        sock.settimeout(5)
        while not re.search(rb"\(C:\$[0-9a-f]{4}\) $", data):
            data += sock.recv(65536)
        return data.decode(errors="replace")

    while time.time() - start < limit:
        reply = ask("m 0002 0002")
        sock.sendall(b"x\n")
        if re.search(r">C:0002\s+2a\b", reply):
            print("ok")
            sys.exit(0)
        time.sleep(1)
    print("BASIC didn't run A=42:POKE 2,A after the program ended")
    sys.exit(1)
finally:
    vice.terminate()
    try:
        vice.wait(timeout=10)
    except subprocess.TimeoutExpired:
        vice.kill()
