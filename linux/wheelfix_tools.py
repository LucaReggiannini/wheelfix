#!/usr/bin/env python3
# WheelFix for Linux - wheelfix-tools: calibrate, debug, replay and restore,
# as on Windows. calibrate, debug and restore need administrator rights and
# ask for them with sudo; replay does not. Files they save are given to the
# user who ran sudo. Python standard library only.

import os
import select
import signal
import subprocess
import sys
import threading
import time

# The other WheelFix modules sit next to this file; Python does not look
# there by itself when started with -I or PYTHONSAFEPATH.
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from wheelfilter import (LOG_HEADER, SETTINGS_PATH, Settings, SettingsError,
                         WheelFilter, direction_name, format_decision, sign)
from wheelfix import Mouse, WheelStream, explain, split_wheel

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_SETTINGS = os.path.join(HERE, "wheelfix.ini")

CALIBRATION_SECONDS = 30
COUNTDOWN_SECONDS = 3
MIN_CALIBRATION_EVENTS = 30
TICK_MS = 50

# calibrate tries these from the most responsive to the strictest: hybrid
# confirms a regular change of direction at once, grouped always waits
# confirm-ms. Columns: response, confirm-ms, groups.
CANDIDATES = [
    ("hybrid", 300, 2), ("hybrid", 450, 2), ("hybrid", 450, 3), ("hybrid", 600, 3),
    ("hybrid", 800, 4), ("grouped", 450, 3), ("grouped", 600, 4), ("grouped", 800, 4),
    ("grouped", 1200, 5),
]

HELP = """WheelFix tools

wheelfix-tools calibrate [down|up|both]
    Records your wheel while you turn it in one direction at a time, finds
    the settings that let the fewest wrong steps through and offers to save
    them. Nothing scrolls while recording. Default: both.

wheelfix-tools debug [FILE]
    Shows every wheel event and the decision of WheelFix as it happens.
    With FILE, also saves them, to use later with replay.

wheelfix-tools replay FILE [--option value ...] [--verbose]
    Runs a recording (from calibrate or debug, also made on Windows) through
    the filter and shows what WheelFix would deliver with the settings in
    /etc/wheelfix.ini. Options change them for this run only, for example
    --response grouped --confirm-ms 600. --verbose shows every decision.

wheelfix-tools restore
    Writes the default /etc/wheelfix.ini. The current one is kept as
    /etc/wheelfix.ini.bak.

calibrate, debug and restore ask for administrator rights (sudo)."""


class UsageError(Exception):
    pass


def main(args):
    command = args[0].lower() if args else "help"
    commands = {"calibrate": run_calibrate, "debug": run_debug,
                "replay": run_replay, "restore": run_restore}
    try:
        if command in ("help", "--help", "-h"):
            print(HELP)
            return 0
        if command not in commands:
            raise UsageError(f'unknown command "{args[0]}". Run wheelfix-tools for help.')
        return commands[command](args)
    except (UsageError, SettingsError, OSError, RuntimeError) as error:
        print(f"Error: {error}", file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        print()
        return 1


# ---- restore ----

def run_restore(args):
    if len(args) > 1:
        raise UsageError("restore takes no options.")
    as_root(args)
    kept = os.path.exists(SETTINGS_PATH)
    if kept:
        os.replace(SETTINGS_PATH, SETTINGS_PATH + ".bak")
    with open(DEFAULT_SETTINGS, encoding="utf-8") as source:
        write_settings(source.read().splitlines())
    print(f"{SETTINGS_PATH} now has the default settings. WheelFix applies them within a second.")
    if kept:
        print(f"The previous file is kept as {SETTINGS_PATH}.bak.")
    return 0


# ---- replay ----

def run_replay(args):
    if len(args) < 2 or args[1].startswith("--"):
        raise UsageError("replay needs a recording, for example: "
                         "wheelfix-tools replay calibration_20261018_101500_down.csv")
    settings = load_settings()
    verbose = False
    options = args[2:]
    while options:
        option = options.pop(0).lower()
        if option == "--verbose":
            verbose = True
            continue
        if not option.startswith("--") or not options:
            raise UsageError("options are written as --name value, for example --confirm-ms 600.")
        try:
            settings.set(option[2:], options.pop(0))
        except SettingsError as error:
            raise UsageError(str(error)) from None

    samples = load_recording(args[1])
    log = [] if verbose else None
    result = replay(settings, samples, log)

    if verbose:
        origin = [None]
        for line in log:
            print(show(line, origin))
        print()
    span = (samples[-1][0] - samples[0][0]) / 1000
    print(f"{os.path.basename(args[1])}: {len(samples)} events in {span:.1f} s")
    print(f"Settings: {settings}")
    print()
    print(f"{'Steps':<24}{'DOWN':>8}{'UP':>8}")
    print(f"{'Recorded':<24}{steps(result.recorded[0]):>8}{steps(result.recorded[1]):>8}")
    print(f"{'Delivered by WheelFix':<24}{steps(result.delivered[0]):>8}{steps(result.delivered[1]):>8}")
    print()
    print("Decisions: " + result.decision_counts())
    return 0


class ReplayResult:
    REASONS = ("ACCEPT", "ACCEPT_IDLE", "BLOCK", "HOLD", "CONFIRM_FAST",
               "CONFIRM_GROUPS", "CANCEL", "TIMEOUT")

    def __init__(self):
        # Wheel units per direction: [0] DOWN, [1] UP. 120 is one step.
        self.recorded = [0, 0]
        self.delivered = [0, 0]
        self.decisions = {}

    def decision_counts(self):
        return ", ".join(f"{reason}={self.decisions[reason]}"
                         for reason in self.REASONS if reason in self.decisions)


def replay(settings, samples, log=None):
    """Runs a recording through WheelFilter as the service does, including
    its 50 ms tick, which ends pending changes when the wheel goes quiet.
    log, if a list, receives one journal line per decision, with times
    counted from the first event."""
    wheel = WheelFilter(settings)
    result = ReplayResult()
    origin = samples[0][0]
    next_tick = origin + TICK_MS

    def apply(decision, time_ms, delta):
        if decision is None:
            return
        add(result.delivered, decision.replay_delta)
        result.decisions[decision.reason] = result.decisions.get(decision.reason, 0) + 1
        if log is not None:
            log.append(format_decision(time_ms - origin, delta, decision))

    for time_ms, delta in samples:
        while next_tick < time_ms:
            apply(wheel.tick(next_tick), next_tick, 0)
            next_tick += TICK_MS
        add(result.recorded, delta)
        decision = wheel.process(delta, time_ms)
        if decision.pass_original:
            add(result.delivered, delta)
        apply(decision, time_ms, delta)

    # A change still pending at the end finishes as it would live.
    end = samples[-1][0] + settings.idle_ms + TICK_MS
    while next_tick <= end:
        apply(wheel.tick(next_tick), next_tick, 0)
        next_tick += TICK_MS
    return result


def add(units, delta):
    if delta < 0:
        units[0] -= delta
    else:
        units[1] += delta


def load_recording(path):
    """Reads a recording: the CSV files of calibrate (also those made on
    Windows, old ones included, with Italian headers and su/giu) and the files
    saved by debug. Only the first three columns are used: time_ms,
    direction, delta. Notes (#), headers and lines without a wheel event are
    skipped. Returns a list of (time_ms, delta)."""
    name = os.path.basename(path)
    with open(path, encoding="utf-8-sig") as file:
        lines = file.read().splitlines()
    samples = []
    previous = None
    for number, line in enumerate(lines, 1):
        line = line.strip()
        if not line or line[0] == "#" or line[0].isalpha():
            continue
        fields = line.split(",")
        try:
            time_ms, value = int(fields[0]), int(fields[2])
            if not -32768 <= value <= 32767:
                raise ValueError
        except (ValueError, IndexError):
            raise RuntimeError(f"{name}, line {number}: expected time_ms,direction,delta.") from None
        if value == 0:
            continue
        if previous is not None and time_ms < previous:
            raise RuntimeError(f"{name}, line {number}: time_ms goes back. "
                               "Replay one recording at a time.")
        previous = time_ms
        direction = {"up": 1, "su": 1, "down": -1, "giu": -1}.get(fields[1].strip().lower(), 0)
        samples.append((time_ms, (direction or sign(value)) * abs(value)))
    if not samples:
        raise RuntimeError(f"{name} contains no wheel events.")
    return samples


def show(line, origin):
    """A journal line as text, with times counted from the first line.
    origin is a one-item list holding that first time."""
    fields = line.split(",")
    try:
        time_ms = int(fields[0])
    except ValueError:
        return line
    if len(fields) < 10:
        return line
    if origin[0] is None:
        origin[0] = time_ms
    return (f"[{time_ms - origin[0]:>8}ms] {fields[1].upper():<4} {fields[2]:>5}  {fields[3]:<14} "
            f"accepted={fields[4].upper():<4} lead={fields[5]:>2} groups={fields[6]} "
            f"fast-run={fields[7]} age={fields[8]}ms replay={fields[9]}")


# ---- debug ----

def run_debug(args):
    if len(args) > 2:
        raise UsageError("debug takes at most one file name.")
    as_root(args)
    save_path = args[1] if len(args) == 2 else None
    exit_on_signals()

    turned_on = False
    if not load_settings().debug:
        answer = input(f"Logging is off (debug = false in {SETTINGS_PATH}). Turn it on now? [y/N] ")
        if not yes(answer):
            return 0
        save_options(("debug", "true"))
        turned_on = True

    save = None
    journal = None
    try:
        if save_path:
            save = open(save_path, "w", encoding="utf-8")
            give_to_user(save_path)
            save.write(LOG_HEADER + "\n")
            print(f"Saving to {os.path.abspath(save_path)}.")
        show_service_state()
        print("Showing wheel events as they happen. Press Enter or Ctrl+C to stop.")
        print()
        journal = subprocess.Popen(
            ["journalctl", "--follow", "--lines=0", "--output=cat", "--no-pager", "--unit=wheelfix"],
            stdout=subprocess.PIPE, universal_newlines=True)
        threading.Thread(target=follow, args=(journal, save), daemon=True).start()
        input()
    except EOFError:
        pass
    finally:
        if journal is not None:
            journal.terminate()
        if save is not None:
            save.close()
        if turned_on:
            save_options(("debug", "false"))
            print("Logging turned off again.")
    return 0


def show_service_state():
    """Says whether the service runs and shows its latest messages, which
    tell which mice it filters or why it cannot."""
    if systemctl("is-active", "--quiet", "wheelfix") != 0:
        print("WheelFix is not running. Start it with: sudo systemctl start wheelfix")
    output = subprocess.run(
        ["journalctl", "--unit=wheelfix", "--boot", "--output=cat", "--no-pager", "--lines=1000"],
        stdout=subprocess.PIPE, universal_newlines=True).stdout
    notes = [line for line in output.splitlines() if line.startswith("#")][-5:]
    if notes:
        print("Latest messages from WheelFix:")
        for note in notes:
            print("  " + note)
    print()


def follow(journal, save):
    origin = [None]
    for line in journal.stdout:
        line = line.rstrip("\n")
        if not line:
            continue
        if save is not None:
            save.write(line + "\n")
            save.flush()
        print(line if line.startswith("#") else show(line, origin), flush=True)


# ---- calibrate ----

def run_calibrate(args):
    if len(args) > 2:
        raise UsageError("calibrate takes one option: down, up or both.")
    which = args[1].lower() if len(args) == 2 else "both"
    if which not in ("down", "up", "both"):
        raise UsageError("calibrate takes down, up or both.")
    as_root(args)
    current = load_settings()
    # Ctrl+C, a closed terminal or a stop request still run the cleanup below.
    exit_on_signals()

    print("Calibration records your wheel while you turn it in one direction at a time.")
    service_was_running = systemctl("is-active", "--quiet", "wheelfix") == 0
    if service_was_running:
        print("WheelFix is stopped meanwhile and started again at the end.")
        systemctl("stop", "wheelfix")
    try:
        recorder = Recorder()
        try:
            recordings = []
            if which != "up":
                recordings.append(record(recorder, -1))
            if which != "down":
                recordings.append(record(recorder, 1))
        finally:
            recorder.close()

        print()
        stamp = time.strftime("%Y%m%d_%H%M%S")
        for direction, samples in recordings:
            print("Recording saved: " + save_recording(direction, samples, stamp))
        print()
        suggest(current, recordings)
    finally:
        if service_was_running:
            systemctl("start", "wheelfix")
            print("WheelFix started again.")
    return 0


def record(recorder, direction):
    name = direction_name(direction).upper()
    print()
    print(f"Get ready to turn the wheel {name} only, as you usually do. ", end="", flush=True)
    for count in range(COUNTDOWN_SECONDS, 0, -1):
        print(f"{count}... ", end="", flush=True)
        time.sleep(1)
    print()
    print(f"START: turn the wheel {name} now. Nothing scrolls while recording.")
    end = recorder.begin(CALIBRATION_SECONDS)
    while True:
        left = end - time.monotonic()
        if left <= 0:
            break
        print(f"\r{int(left + 0.999):>3} s left, {recorder.count()} events recorded ", end="", flush=True)
        time.sleep(min(0.25, left))
    samples = recorder.end()
    print()
    print("STOP.")
    return direction, samples


def save_recording(direction, samples, stamp):
    path = os.path.abspath(f"calibration_{stamp}_{direction_name(direction)}.csv")
    with open(path, "w", encoding="utf-8") as file:
        file.write("time_ms,direction,delta\n")
        for time_ms, delta in samples:
            file.write(f"{time_ms},{direction_name(sign(delta))},{delta}\n")
    give_to_user(path)
    return path


class Recorder:
    """Takes the mice as the service does, so the pointer and the buttons
    keep working, and passes their events on from a thread of its own, so the
    terminal can never delay the mouse. During a phase the wheel steps are
    recorded instead of passed on, so nothing scrolls; the phase ends by
    itself at its deadline."""

    def __init__(self):
        self.lock = threading.Lock()
        self.samples = None
        self.start_ms = self.end_ms = 0
        self.mice = []
        for name in sorted(os.listdir("/dev/input")):
            if not name.startswith("event"):
                continue
            try:
                mouse = Mouse.open("/dev/input/" + name, self._make_stream)
            except OSError as error:
                for taken in self.mice:
                    taken.close()
                raise RuntimeError(f"cannot take /dev/input/{name}: {explain(error)}.") from None
            if mouse is not None:
                self.mice.append(mouse)
        if not self.mice:
            raise RuntimeError("no mouse with a wheel was found.")
        self.stopping = False
        self.thread = threading.Thread(target=self._run, daemon=True)
        self.thread.start()

    def _make_stream(self, emit, read_keys, hi_res):
        return RecordingStream(self, emit, read_keys, hi_res)

    def begin(self, seconds):
        """Starts a phase. Returns its end, on the clock of time.monotonic()."""
        with self.lock:
            self.samples = []
            self.start_ms = int(time.monotonic() * 1000)
            self.end_ms = self.start_ms + seconds * 1000
            return self.end_ms / 1000

    def record(self, now_ms, delta):
        """Called by the streams. Returns True if the step was recorded."""
        with self.lock:
            if self.samples is None or not self.start_ms <= now_ms < self.end_ms:
                return False
            self.samples.append((now_ms - self.start_ms, delta))
            return True

    def count(self):
        with self.lock:
            return len(self.samples or [])

    def end(self):
        with self.lock:
            samples, self.samples = self.samples, None
        return sorted(samples)

    def close(self):
        self.stopping = True
        self.thread.join(2)
        for mouse in self.mice:
            mouse.close()

    def _run(self):
        while not self.stopping:
            ready, _, _ = select.select([mouse.fd for mouse in self.mice], [], [], 0.05)
            for mouse in list(self.mice):
                if mouse.fd in ready:
                    try:
                        mouse.read()
                    except OSError:
                        # Unplugged: release it.
                        self.mice.remove(mouse)
                        mouse.close()


class _Quiet:
    def write(self, line):
        pass

    def note(self, text):
        pass


class RecordingStream(WheelStream):
    """Passes everything on, except the wheel steps taken by the recorder."""

    def __init__(self, recorder, emit, read_keys, hi_res):
        super().__init__(Settings(), emit, read_keys, _Quiet(), hi_res)
        self.recorder = recorder

    def _end_frame(self, frame, now_ms):
        delta, rest = split_wheel(frame)
        if delta and self.recorder.record(now_ms, delta):
            frame = rest
        self._send(frame)

    def tick(self, now_ms):
        pass


def suggest(current, recordings):
    """Replays the recordings with your settings and with each candidate, and
    suggests the best candidate (see better()) if it does better than your
    settings. Among equally good candidates the most responsive, that is the
    first in the list, is kept."""
    events = 0
    wrong_recorded = 0
    for direction, samples in recordings:
        total = sum(abs(delta) for _, delta in samples)
        wrong = sum(abs(delta) for _, delta in samples if sign(delta) != direction)
        print(f"{direction_name(direction).upper()}: {steps(total)} steps recorded, "
              f"{steps(wrong)} of them in the wrong direction.")
        events += len(samples)
        wrong_recorded += wrong
    print()
    if events < MIN_CALIBRATION_EVENTS:
        print("Too few steps to judge. Run calibrate again and keep turning the wheel until STOP.")
        return
    if wrong_recorded == 0:
        print("Your wheel sent no steps in the wrong direction: there is nothing to tune.")
        print("If it misfires only now and then, run calibrate again and turn it faster.")
        return

    current_score = evaluate(current, recordings)
    tried = []
    for response, confirm_ms, groups in CANDIDATES:
        settings = copy_settings(current)
        settings.response, settings.confirm_ms, settings.groups = response, confirm_ms, groups
        tried.append((settings, evaluate(settings, recordings)))
    best, best_score = tried[0]
    for settings, score in tried[1:]:
        if better(score, best_score):
            best, best_score = settings, score
    improves = better(best_score, current_score)

    # Nothing to compare: say it in one sentence instead of a table.
    if all(score == current_score for _, score in tried):
        print(f"Every setting tried, yours included, lets {steps(current_score[0])} of the "
              f"{steps(wrong_recorded)} wrong steps through and loses {steps(current_score[1])} steps.")
        print("This recording cannot tell them apart. Nothing to change.")
        return

    print(f'For each setting: "wrong" is how many of the {steps(wrong_recorded)} wrong steps would still reach')
    print('your programs (the page jumps back); "lost" is how many steps in the direction')
    print("you were turning would be held back and never arrive.")
    print()
    print(f"  {'response':<9}{'confirm-ms':>11}{'groups':>8}{'wrong':>8}{'lost':>7}")
    if not any(same_filter(settings, current) for settings, _ in tried):
        print_score(current, current_score, "  <- your settings")
    for settings, score in tried:
        note = ""
        if same_filter(settings, current):
            note = "  <- your settings"
        elif improves and settings is best:
            note = "  <- suggested"
        print_score(settings, score, note)
    print()

    if not improves:
        print("No setting tried does better than yours. Nothing to change.")
        return
    print(f"Suggested: response = {best.response}, confirm-ms = {best.confirm_ms}, groups = {best.groups}.")
    print(f"Wrong steps that get through: {steps(best_score[0])} (now {steps(current_score[0])}). "
          f"Steps lost: {steps(best_score[1])} (now {steps(current_score[1])}).")
    if not yes(input(f"Save these settings to {SETTINGS_PATH}? [y/N] ")):
        print("Nothing was changed.")
        return
    save_options(("response", best.response), ("confirm-ms", str(best.confirm_ms)),
                 ("groups", str(best.groups)))
    print(f"Saved. You can change them at any time: sudoedit {SETTINGS_PATH}")


def evaluate(settings, recordings):
    """(wrong, lost) in wheel units: wrong-direction steps that get through,
    and steps in the main direction that never arrive."""
    wrong = lost = 0
    for direction, samples in recordings:
        result = replay(settings, samples)
        main = 1 if direction > 0 else 0
        wrong += result.delivered[1 - main]
        lost += max(0, result.recorded[main] - result.delivered[main])
    return wrong, lost


def better(a, b):
    """Fewer wrong steps first: they are what makes the page jump. With as
    many, fewer lost steps: WheelFix also gets back to your direction sooner
    after a fault it could not block."""
    return a[0] < b[0] or (a[0] == b[0] and a[1] < b[1])


def same_filter(a, b):
    """calibrate changes only these three options."""
    return (a.response, a.confirm_ms, a.groups) == (b.response, b.confirm_ms, b.groups)


def copy_settings(settings):
    copy = Settings()
    copy.__dict__.update(settings.__dict__)
    return copy


def print_score(settings, score, note):
    print(f"  {settings.response:<9}{settings.confirm_ms:>11}{settings.groups:>8}"
          f"{steps(score[0]):>8}{steps(score[1]):>7}{note}")


# ---- Shared helpers ----

def as_root(args):
    """Runs this command again with sudo if it is not running as root."""
    if os.geteuid() != 0:
        os.execvp("sudo", ["sudo", sys.executable, os.path.abspath(__file__)] + list(args))


def exit_on_signals():
    """A closed terminal (SIGHUP) or a stop request (SIGTERM) end the program
    like Ctrl+C, so that its cleanup runs."""
    def stop(signum, frame):
        raise KeyboardInterrupt
    signal.signal(signal.SIGHUP, stop)
    signal.signal(signal.SIGTERM, stop)


def give_to_user(path):
    """Files written under sudo belong to root; give them to the user."""
    uid, gid = os.environ.get("SUDO_UID"), os.environ.get("SUDO_GID")
    if uid and gid:
        os.chown(path, int(uid), int(gid))


def systemctl(*args):
    return subprocess.run(["systemctl"] + list(args)).returncode


def load_settings():
    """The settings in /etc/wheelfix.ini, or the defaults if it is missing."""
    if not os.path.exists(SETTINGS_PATH):
        return Settings()
    try:
        return Settings.load(SETTINGS_PATH)
    except SettingsError as error:
        raise SettingsError(f"{error} Fix it (sudoedit {SETTINGS_PATH}) "
                            "or run: wheelfix-tools restore") from None


def save_options(*pairs):
    """Changes options in /etc/wheelfix.ini and keeps everything else in the
    file, comments included."""
    if os.path.exists(SETTINGS_PATH):
        with open(SETTINGS_PATH, encoding="utf-8-sig") as file:
            lines = file.read().splitlines()
    else:
        with open(DEFAULT_SETTINGS, encoding="utf-8") as file:
            lines = file.read().splitlines()
    for name, value in pairs:
        setting = f"{name} = {value}"
        found = False
        for index, line in enumerate(lines):
            comment = min([position for position in (line.find(";"), line.find("#"))
                           if position >= 0], default=-1)
            body = line[:comment] if comment >= 0 else line
            key, equals, _ = body.partition("=")
            if equals and key.strip().lower() == name:
                lines[index] = setting + (" " + line[comment:] if comment >= 0 else "")
                found = True
        if not found:
            lines.append(setting)
    write_settings(lines)


def write_settings(lines):
    """Writes /etc/wheelfix.ini through a temporary file, so the service never
    reads a half-written file."""
    temporary = SETTINGS_PATH + ".new"
    with open(temporary, "w", encoding="utf-8") as file:
        file.write("\n".join(lines) + "\n")
    os.chmod(temporary, 0o644)
    os.replace(temporary, SETTINGS_PATH)


def yes(answer):
    return answer.strip().lower() in ("y", "yes")


def steps(units):
    """Wheel units as steps, without a useless .0."""
    value = round(units / 120, 1)
    return str(int(value)) if value == int(value) else f"{value:.1f}"


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
