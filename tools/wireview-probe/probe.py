#!/usr/bin/env python3
"""Read-only probe of a Thermal Grizzly WireView Pro II (USB CDC-ACM, STM32 0483:5740).

    sudo python3 tools/wireview-probe/probe.py [/dev/ttyACM0] [samples]

Sends only reads: the welcome string (RTS raised), vendor data (0x01), build info (0x0D) and sensor values (0x04).
Never config, calibration, flash, screen, reset or bootloader commands. Protocol facts (command numbers, frame
layout) were learned from the community wireview-linux project for interoperability; this code is OpenHW's own.
"""
import fcntl, os, select, struct, sys, termios, time

PORT = sys.argv[1] if len(sys.argv) > 1 else '/dev/ttyACM0'
SAMPLES = int(sys.argv[2]) if len(sys.argv) > 2 else 3
TIOCMBIS, TIOCMBIC, TIOCM_RTS = 0x5416, 0x5417, 0x004

fd = os.open(PORT, os.O_RDWR | os.O_NOCTTY | os.O_NONBLOCK)
attrs = termios.tcgetattr(fd)
attrs[0] = 0                                   # iflag: raw
attrs[1] = 0                                   # oflag
attrs[2] = termios.CS8 | termios.CREAD | termios.CLOCAL
attrs[3] = 0                                   # lflag
attrs[4] = attrs[5] = termios.B115200
termios.tcsetattr(fd, termios.TCSANOW, attrs)


def read_exact(n, timeout=1.0):
    buf, end = b'', time.monotonic() + timeout
    while len(buf) < n and time.monotonic() < end:
        r, _, _ = select.select([fd], [], [], max(0, end - time.monotonic()))
        if r:
            buf += os.read(fd, n - len(buf))
    return buf


def flush():
    termios.tcflush(fd, termios.TCIFLUSH)


def rts(on):
    fcntl.ioctl(fd, TIOCMBIS if on else TIOCMBIC, struct.pack('I', TIOCM_RTS))


def cmd(code, size):
    flush()
    os.write(fd, bytes([code]))
    return read_exact(size)


# Welcome: raise RTS, read up to a NUL.
flush(); rts(True); time.sleep(0.01)
welcome, end = b'', time.monotonic() + 1.0
while time.monotonic() < end and not welcome.endswith(b'\0') and len(welcome) < 128:
    r, _, _ = select.select([fd], [], [], 0.1)
    if r:
        welcome += os.read(fd, 1)
time.sleep(0.01); rts(False)
print('welcome  :', welcome.rstrip(b'\0').decode('ascii', 'replace') or '(none)')

vd = cmd(0x01, 3)
print('vendor   :', vd.hex(' ') if vd else '(none)', '(Pro II = ef 05, Noctua = ef 06; 3rd byte = firmware)')
build = cmd(0x0D, 3 + 32 + 32 + 1)
if len(build) == 68:
    name = build[3:35].split(b'\0')[0].decode('ascii', 'replace')
    info = build[35:67].split(b'\0')[0].decode('ascii', 'replace')
    print('build    :', name, '|', info)

CAP = {0: 600, 1: 450, 2: 300, 3: 150}
for n in range(SAMPLES):
    f = cmd(0x04, 100)
    if len(f) != 100:
        print('sample %d: short read (%d bytes)' % (n, len(f))); continue
    if f[10] > 100 or f[11] != 0 or f[95] != 0:
        print('sample %d: frame out of step, skipped' % n); continue
    ts = struct.unpack_from('<4h', f, 0)
    vdd, duty = struct.unpack_from('<HB', f, 8)
    pins = [struct.unpack_from('<hxxII', f, 12 + 12 * i) for i in range(6)]
    total_mw, total_ma, avg_mv, cap, fault_status, fault_log = struct.unpack_from('<IIHBxHH', f, 84)
    print('sample %d: temps in %.1f out %.1f ts3 %.1f ts4 %.1f °C · vdd %d mV · fan %d %%' % (n, *(t / 10 for t in ts), vdd, duty))
    for i, (mv, ma, mw) in enumerate(pins):
        print('          pin %d: %6.3f V  %6.3f A  %7.2f W' % (i + 1, mv / 1000, ma / 1000, mw / 1000))
    print('          total %.2f W  %.3f A  avg %.3f V · PSU cap %s W · faults status 0x%04x log 0x%04x' % (
        total_mw / 1000, total_ma / 1000, avg_mv / 1000, CAP.get(cap, '?'), fault_status, fault_log))
    time.sleep(0.5)
os.close(fd)
