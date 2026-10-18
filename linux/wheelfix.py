#!/usr/bin/env python3
# WheelFix for Linux - the service, started by systemd (wheelfix.service) as
# root. It takes every mouse with a wheel for itself (EVIOCGRAB), so that
# nothing else receives its events, and passes them on through a virtual copy
# of the mouse made with uinput, filtering the vertical wheel with
# WheelFilter on the way. It works below the desktop, so with any Wayland
# compositor, X11 or the console. Settings: /etc/wheelfix.ini, reloaded when
# it changes. Messages go to the system journal: journalctl -u wheelfix
# Python standard library only.

import errno
import fcntl
import os
import queue
import select
import struct
import sys
import threading
import time

# The other WheelFix modules sit next to this file; Python does not look
# there by itself when started with -I or PYTHONSAFEPATH.
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from wheelfilter import (SETTINGS_PATH, Settings, SettingsError, WheelFilter,
                         format_decision)

TICK_S = 0.05
SETTINGS_CHECK_S = 1.0
# How often /dev/input is looked at for mice plugged in later.
SCAN_S = 2.0
# Marks the virtual copies, so WheelFix never takes them for real mice.
VIRTUAL_PHYS = "wheelfix"

# linux/input-event-codes.h
EV_SYN, EV_KEY, EV_REL, EV_MSC = 0x00, 0x01, 0x02, 0x04
SYN_REPORT, SYN_DROPPED = 0, 3
REL_X, REL_WHEEL, REL_WHEEL_HI_RES = 0x00, 0x08, 0x0b
KEY_MAX, REL_MAX, MSC_MAX, INPUT_PROP_MAX = 0x2ff, 0x0f, 0x07, 0x1f
CLOCK_MONOTONIC = 1


# ioctl request numbers, as built by the _IOC macros of asm-generic/ioctl.h
# (the layout used on x86 and ARM).
def _ioc(direction, kind, number, size):
    return direction << 30 | size << 16 | ord(kind) << 8 | number


_NONE, _WRITE, _READ = 0, 1, 2
_POINTER_SIZE = struct.calcsize("P")


def EVIOCGBIT(event_type, length):
    return _ioc(_READ, "E", 0x20 + event_type, length)


def EVIOCGNAME(length):
    return _ioc(_READ, "E", 0x06, length)


def EVIOCGPHYS(length):
    return _ioc(_READ, "E", 0x07, length)


def EVIOCGPROP(length):
    return _ioc(_READ, "E", 0x09, length)


def EVIOCGKEY(length):
    return _ioc(_READ, "E", 0x18, length)


EVIOCGID = _ioc(_READ, "E", 0x02, 8)
EVIOCGRAB = _ioc(_WRITE, "E", 0x90, 4)
EVIOCSCLOCKID = _ioc(_WRITE, "E", 0xa0, 4)
UI_DEV_CREATE = _ioc(_NONE, "U", 1, 0)
UI_DEV_SETUP = _ioc(_WRITE, "U", 3, 92)
UI_SET_EVBIT = _ioc(_WRITE, "U", 100, 4)
UI_SET_KEYBIT = _ioc(_WRITE, "U", 101, 4)
UI_SET_RELBIT = _ioc(_WRITE, "U", 102, 4)
UI_SET_MSCBIT = _ioc(_WRITE, "U", 104, 4)
UI_SET_PHYS = _ioc(_WRITE, "U", 108, _POINTER_SIZE)
UI_SET_PROPBIT = _ioc(_WRITE, "U", 110, 4)

# struct input_event: a timeval, then type, code and value.
EVENT = struct.Struct("llHHi")


class Log:
    """Writes to standard output, which systemd sends to the journal, from a
    separate thread, so a slow journal never delays the mouse. If the journal
    falls far behind, lines are dropped."""

    def __init__(self):
        self.lines = queue.Queue(maxsize=10000)
        threading.Thread(target=self._run, daemon=True).start()

    def write(self, line):
        try:
            self.lines.put_nowait(line)
        except queue.Full:
            pass

    def note(self, text):
        self.write("# " + text)

    def _run(self):
        while True:
            sys.stdout.write(self.lines.get() + "\n")
            if self.lines.empty():
                sys.stdout.flush()


def split_wheel(frame):
    """A frame is the group of events the mouse reports at once. A wheel step
    usually comes as REL_WHEEL (1 per step) and REL_WHEEL_HI_RES (120 per
    step) in the same frame, so both are blocked or passed together.
    Returns the step in wheel units (120 per step, 0 if none) and the frame
    without the vertical wheel events."""
    values = {}
    rest = []
    for event in frame:
        if event[0] == EV_REL and event[1] in (REL_WHEEL, REL_WHEEL_HI_RES):
            values[event[1]] = event[2]
        else:
            rest.append(event)
    return values.get(REL_WHEEL_HI_RES, values.get(REL_WHEEL, 0) * 120), rest


class WheelStream:
    """The events of one mouse, passed on with the vertical wheel filtered.

    emit(events) sends one frame, a list of (type, code, value), to the
    virtual mouse; read_keys() returns the buttons the real mouse holds down.
    No device access happens here, so this class can be tested on its own.
    """

    def __init__(self, settings, emit, read_keys, log, hi_res):
        self.emit = emit
        self.read_keys = read_keys
        self.log = log
        # The mouse reports REL_WHEEL_HI_RES (120 per step) besides REL_WHEEL.
        self.hi_res = hi_res
        self.frame = []
        self.dropped = False
        self.keys_down = set()
        self.set_settings(settings)

    def set_settings(self, settings):
        self.filter = WheelFilter(settings)
        self.debug = settings.debug

    def feed(self, sec, usec, event_type, code, value):
        """Takes one event read from the mouse."""
        if event_type != EV_SYN:
            if not self.dropped:
                self.frame.append((event_type, code, value))
            return
        if code == SYN_DROPPED:
            # The kernel lost events: discard everything up to the next
            # report, then make sure no button is left pressed by mistake.
            self.dropped = True
            self.frame = []
        elif code == SYN_REPORT:
            frame, self.frame = self.frame, []
            if self.dropped:
                self.dropped = False
                self._resync_keys()
            else:
                self._end_frame(frame, sec * 1000 + usec // 1000)

    def tick(self, now_ms):
        """A pending change can end without any wheel event: give back the
        held steps."""
        decision = self.filter.tick(now_ms)
        if decision is None:
            return
        if self.debug:
            self.log.write(format_decision(now_ms, 0, decision))
        if decision.replay_delta:
            self._send(self._wheel_frame(decision.replay_delta))

    def _end_frame(self, frame, now_ms):
        delta, rest = split_wheel(frame)
        replay = 0
        if delta:
            decision = self.filter.process(delta, now_ms)
            if self.debug:
                self.log.write(format_decision(now_ms, delta, decision))
            if not decision.pass_original:
                frame = rest
            replay = decision.replay_delta
        self._send(frame)
        if replay:
            self._send(self._wheel_frame(replay))

    def _wheel_frame(self, delta):
        frame = [(EV_REL, REL_WHEEL, delta // 120)]
        if self.hi_res:
            frame.append((EV_REL, REL_WHEEL_HI_RES, delta))
        return frame

    def _send(self, frame):
        if not frame:
            return
        for event_type, code, value in frame:
            if event_type == EV_KEY:
                if value == 1:
                    self.keys_down.add(code)
                elif value == 0:
                    self.keys_down.discard(code)
        self.emit(frame)

    def _resync_keys(self):
        pressed = set(self.read_keys())
        changes = [(EV_KEY, code, 1 if code in pressed else 0)
                   for code in sorted(self.keys_down ^ pressed)]
        self.keys_down = pressed
        if changes:
            self.emit(changes)
        self.log.note("The kernel dropped some mouse events; buttons resynchronised.")


# ---- Devices ----

def explain(error):
    """An error from Mouse.open() in words."""
    if error.filename == "/dev/uinput":
        return ("the virtual mouse cannot be created: the uinput kernel module "
                "is not available (sudo modprobe uinput)")
    if error.errno == errno.EBUSY:
        return "another program has already taken this mouse"
    return error.strerror or str(error)


def _bitmask(fd, request, maximum):
    """Codes set in a bitmask returned by an EVIOCG* request."""
    buffer = bytearray(maximum // 8 + 1)
    fcntl.ioctl(fd, request, buffer)
    return [code for code in range(maximum + 1) if buffer[code // 8] >> (code % 8) & 1]


def _capabilities(fd, event_type, maximum):
    return _bitmask(fd, EVIOCGBIT(event_type, maximum // 8 + 1), maximum)


def _string(fd, request_for_length):
    buffer = bytearray(256)
    try:
        fcntl.ioctl(fd, request_for_length(len(buffer)), buffer)
    except OSError:
        return ""
    return buffer.split(b"\0", 1)[0].decode("utf-8", "replace")


class Mouse:
    """A real mouse taken by WheelFix and its virtual copy."""

    # make_stream(emit, read_keys, hi_res) returns the object that receives
    # the events of the real mouse: a WheelStream in the service.
    def __init__(self, path, fd, name, make_stream):
        self.path = path
        self.fd = fd
        self.name = name
        hi_res = REL_WHEEL_HI_RES in _capabilities(fd, EV_REL, REL_MAX)
        # /dev/uinput is opened first: if it is missing, the real mouse is
        # never taken, so not a single event of it can be lost.
        self.virtual = os.open("/dev/uinput", os.O_WRONLY)
        try:
            # From now on only WheelFix receives the events of the real
            # mouse; they wait in the kernel until the copy exists. If the
            # rest fails, open() closes the device, which ends the grab.
            fcntl.ioctl(fd, EVIOCGRAB, 1)
            # Timestamps on the clock of time.monotonic(), used for tick().
            fcntl.ioctl(fd, EVIOCSCLOCKID, struct.pack("i", CLOCK_MONOTONIC))
            self._create_virtual_copy()
        except OSError:
            os.close(self.virtual)
            raise
        self.stream = make_stream(self._emit, self._read_keys, hi_res)

    @classmethod
    def open(cls, path, make_stream):
        """Takes the device at path if it is a mouse with a wheel. Returns
        None otherwise."""
        fd = os.open(path, os.O_RDONLY | os.O_NONBLOCK)
        try:
            relative = _capabilities(fd, EV_REL, REL_MAX)
            if (REL_X not in relative or REL_WHEEL not in relative or
                    _string(fd, EVIOCGPHYS) == VIRTUAL_PHYS):
                os.close(fd)
                return None
            return cls(path, fd, _string(fd, EVIOCGNAME), make_stream)
        except OSError:
            os.close(fd)
            raise

    def _create_virtual_copy(self):
        """Makes self.virtual a device with the same buttons, axes, name and
        IDs as the real mouse, so the desktop applies the same settings."""
        virtual = self.virtual
        for event_type, maximum, request in ((EV_KEY, KEY_MAX, UI_SET_KEYBIT),
                                             (EV_REL, REL_MAX, UI_SET_RELBIT),
                                             (EV_MSC, MSC_MAX, UI_SET_MSCBIT)):
            codes = _capabilities(self.fd, event_type, maximum)
            if codes:
                fcntl.ioctl(virtual, UI_SET_EVBIT, event_type)
                for code in codes:
                    fcntl.ioctl(virtual, request, code)
        for prop in _bitmask(self.fd, EVIOCGPROP(INPUT_PROP_MAX // 8 + 1), INPUT_PROP_MAX):
            fcntl.ioctl(virtual, UI_SET_PROPBIT, prop)
        fcntl.ioctl(virtual, UI_SET_PHYS, VIRTUAL_PHYS.encode() + b"\0")
        bustype, vendor, product, version = struct.unpack(
            "HHHH", fcntl.ioctl(self.fd, EVIOCGID, bytes(8)))
        fcntl.ioctl(virtual, UI_DEV_SETUP, struct.pack(
            "HHHH80sI", bustype, vendor, product, version,
            self.name.encode("utf-8")[:79], 0))
        fcntl.ioctl(virtual, UI_DEV_CREATE)

    def read(self):
        """Passes on the events waiting on the real mouse. Raises OSError if
        the mouse was unplugged."""
        try:
            data = os.read(self.fd, EVENT.size * 64)
        except BlockingIOError:
            return
        if not data:
            raise OSError("end of device")
        for offset in range(0, len(data) - EVENT.size + 1, EVENT.size):
            self.stream.feed(*EVENT.unpack_from(data, offset))

    def _emit(self, frame):
        events = frame + [(EV_SYN, SYN_REPORT, 0)]
        os.write(self.virtual, b"".join(EVENT.pack(0, 0, *event) for event in events))

    def _read_keys(self):
        return _bitmask(self.fd, EVIOCGKEY(KEY_MAX // 8 + 1), KEY_MAX)

    def close(self):
        # Closing the real mouse ends the grab; closing the copy removes it.
        os.close(self.virtual)
        os.close(self.fd)


# ---- Settings ----

class SettingsFile:
    """/etc/wheelfix.ini, read again when its modification time changes. A
    file with an error keeps the previous settings; a missing file means the
    defaults."""

    def __init__(self, path, log):
        self.path = path
        self.log = log
        self.stamp = None
        self.current = Settings()

    def check(self):
        """Returns True when the settings changed."""
        try:
            stamp = os.stat(self.path).st_mtime_ns
        except FileNotFoundError:
            stamp = "missing"
        except OSError:
            return False
        if stamp == self.stamp:
            return False
        self.stamp = stamp

        if stamp == "missing":
            self.current = Settings()
            self.log.note(f"{self.path} not found: using the default settings.")
            return True
        try:
            self.current = Settings.load(self.path)
        except OSError:
            # Perhaps being saved right now: try again at the next check.
            self.stamp = None
            return False
        except SettingsError as error:
            self.log.note(f"{error} The previous settings are still in use.")
            return False
        self.log.note(f"Settings: {self.current}")
        return True


# ---- Main loop ----

class Service:
    def __init__(self):
        self.log = Log()
        self.settings = SettingsFile(SETTINGS_PATH, self.log)
        self.mice = {}
        # Devices that are not mice with a wheel, by (path, inode): a node
        # reused by a new device gets a new inode.
        self.ignored = set()
        # The last error reported for each mouse that could not be taken.
        self.errors = {}

    def run(self):
        self.log.note("WheelFix started.")
        next_settings_check = next_scan = 0.0
        while True:
            now = time.monotonic()
            if now >= next_settings_check:
                next_settings_check = now + SETTINGS_CHECK_S
                if self.settings.check():
                    for mouse in self.mice.values():
                        mouse.stream.set_settings(self.settings.current)
            if now >= next_scan:
                next_scan = now + SCAN_S
                self.scan()

            ready, _, _ = select.select(list(self.mice), [], [], TICK_S)
            for fd in ready:
                mouse = self.mice[fd]
                try:
                    mouse.read()
                except OSError:
                    self.remove(mouse, "unplugged")
            now_ms = int(time.monotonic() * 1000)
            for mouse in list(self.mice.values()):
                try:
                    mouse.stream.tick(now_ms)
                except OSError:
                    self.remove(mouse, "lost")

    def scan(self):
        taken = {mouse.path for mouse in self.mice.values()}
        try:
            names = sorted(os.listdir("/dev/input"))
        except OSError:
            return
        for name in names:
            path = "/dev/input/" + name
            if not name.startswith("event") or path in taken:
                continue
            try:
                key = (path, os.stat(path).st_ino)
            except OSError:
                continue
            if key in self.ignored:
                continue
            try:
                mouse = Mouse.open(path, self._make_stream)
            except OSError as error:
                # Tried again at every scan, since the cause may go away (the
                # uinput module loaded, the other program closed), but
                # reported only once.
                message = f"Cannot filter {path}: {explain(error)}."
                if self.errors.get(key) != message:
                    self.errors[key] = message
                    self.log.note(message)
                continue
            self.errors.pop(key, None)
            if mouse is None:
                self.ignored.add(key)
                continue
            self.mice[mouse.fd] = mouse
            self.log.note(f"Filtering {mouse.name} ({path}).")

    def _make_stream(self, emit, read_keys, hi_res):
        return WheelStream(self.settings.current, emit, read_keys, self.log, hi_res)

    def remove(self, mouse, why):
        del self.mice[mouse.fd]
        try:
            mouse.close()
        except OSError:
            pass
        self.log.note(f"Stopped filtering {mouse.name} ({mouse.path}): {why}.")


def main():
    if os.geteuid() != 0:
        sys.stderr.write("WheelFix must run as root; it is started by systemd: "
                         "sudo systemctl start wheelfix\n")
        return 1
    Service().run()
    return 0


if __name__ == "__main__":
    sys.exit(main())
