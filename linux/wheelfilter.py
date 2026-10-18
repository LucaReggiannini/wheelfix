# WheelFix for Linux - filter and settings, shared by the WheelFix programs.
# The same algorithm as windows/WheelFilter.cs: keep the two in step.
# Python standard library only; nothing here touches devices.

import math

SETTINGS_PATH = "/etc/wheelfix.ini"


class SettingsError(ValueError):
    pass


class Settings:
    """The values of /etc/wheelfix.ini. A missing line keeps its default."""

    def __init__(self):
        self.response = "hybrid"
        self.confirm_ms = 450
        self.groups = 3
        self.group_gap_ms = 90
        self.idle_ms = 1100
        self.debug = False

    @classmethod
    def load(cls, path):
        """Reads a settings file. Raises SettingsError with the line number
        for invalid content, and OSError if the file cannot be read."""
        settings = cls()
        with open(path, encoding="utf-8-sig") as file:
            lines = file.read().splitlines()
        for number, line in enumerate(lines, 1):
            line = strip_comment(line).strip()
            if not line:
                continue
            name, equals, value = line.partition("=")
            if not equals or not name.strip():
                raise SettingsError(f"{path}, line {number}: expected name = value.")
            try:
                settings.set(name.strip(), value.strip())
            except SettingsError as error:
                raise SettingsError(f"{path}, line {number}: {error}") from None
        return settings

    def set(self, name, value):
        """Sets one option by its name in wheelfix.ini."""
        name = name.lower()
        value = value.lower()
        if name == "response":
            if value not in ("hybrid", "grouped"):
                raise SettingsError("response must be hybrid or grouped.")
            self.response = value
        elif name == "confirm-ms":
            self.confirm_ms = read_number(name, value, 100, 3000)
        elif name == "groups":
            self.groups = read_number(name, value, 2, 20)
        elif name == "group-gap-ms":
            self.group_gap_ms = read_number(name, value, 10, 1500)
        elif name == "idle-ms":
            self.idle_ms = read_number(name, value, 100, 10000)
        elif name == "debug":
            if value not in ("true", "false"):
                raise SettingsError("debug must be true or false.")
            self.debug = value == "true"
        else:
            raise SettingsError(f'unknown option "{name}".')

    def __str__(self):
        return (f"response = {self.response}, confirm-ms = {self.confirm_ms}, "
                f"groups = {self.groups}, group-gap-ms = {self.group_gap_ms}, "
                f"idle-ms = {self.idle_ms}, debug = {'true' if self.debug else 'false'}")


def strip_comment(line):
    """Removes a ; or # comment."""
    for mark in ";#":
        position = line.find(mark)
        if position >= 0:
            line = line[:position]
    return line


def read_number(name, value, minimum, maximum):
    try:
        number = int(value, 10)
    except ValueError:
        number = None
    if number is None or not minimum <= number <= maximum:
        raise SettingsError(f"{name} must be a whole number from {minimum} to {maximum}.")
    return number


class Decision:
    __slots__ = ("pass_original", "replay_delta", "reason", "accepted_direction",
                 "lead", "groups", "fast_run", "age_ms")


# One line per decision, written to the system journal when debug = true.
# The columns are those of windows/WheelFilter.cs (DecisionLog). Lines
# starting with # are notes.
LOG_HEADER = "time_ms,direction,delta,decision,accepted,lead,groups,fast_run,age_ms,replay"


def format_decision(time_ms, delta, decision):
    return (f"{time_ms},{direction_name(sign(delta))},{delta},{decision.reason},"
            f"{direction_name(decision.accepted_direction)},{decision.lead},{decision.groups},"
            f"{decision.fast_run},{decision.age_ms},{decision.replay_delta}")


def direction_name(direction):
    if direction > 0:
        return "up"
    return "down" if direction < 0 else "none"


def sign(value):
    return (value > 0) - (value < 0)


class WheelFilter:
    """Decides which wheel events pass. A physical step is +120 for UP and
    -120 for DOWN.

    Terms:
    - accepted direction: events in this direction normally pass at once.
    - pending change (the "candidate" fields): events in the other direction
      were seen; WheelFix is deciding whether they are faults or a real
      change of direction. They are blocked meanwhile.
    - lead: while a change is pending, +1 for each event in the new
      direction and -1 for each event in the accepted direction.
    """

    # A pending change ends if no event in the new direction arrives for this long.
    CANDIDATE_TIMEOUT_MS = 850
    MAX_REPLAY_STEPS = 3

    # If the timer notices a finished change only this late (for example
    # after the PC slept), the held steps are dropped instead of replayed.
    LATE_TICK_MS = 1000

    # A pending change must lead by MIN_LEAD before it can be confirmed.
    # From the same lead on, events in the accepted direction are treated as
    # faults and held. Without this, a wheel that also misfires while the user
    # reverses would reset every pending change.
    MIN_LEAD = 2

    # The lead never exceeds MAX_LEAD, so if the user goes back to the
    # accepted direction, at most two of those steps are held.
    MAX_LEAD = 3

    # Faulty bursts contain several events at the same instant: events in
    # the same direction closer than this count once in the lead.
    SAME_IMPULSE_MS = 12

    # Four distinct, evenly spaced events in the new direction strongly
    # suggest a fast manual reversal.
    FAST_MIN_EVENTS = 4
    FAST_MIN_SPAN_MS = 60
    FAST_MIN_GAP_MS = 12
    FAST_MAX_GAP_MS = 90

    def __init__(self, settings):
        self.settings = settings
        self.reset()

    def reset(self):
        self.accepted_direction = 0
        self.last_input_ms = -1
        self._clear_candidate()

    def _clear_candidate(self):
        self.candidate_direction = 0
        self.candidate_first_ms = -1
        self.candidate_last_ms = -1
        self.last_group_ms = -1
        self.candidate_groups = 0
        self.candidate_units = 0
        self.fast_run = 0
        self.fast_run_start_ms = -1
        self.lead = 0
        self.last_new_impulse_ms = -1
        self.last_old_impulse_ms = -1
        self.held_units = 0

    def _start_candidate(self, direction, delta, units, now_ms):
        self._clear_candidate()
        self.candidate_direction = direction
        self.candidate_first_ms = now_ms
        self.candidate_last_ms = now_ms
        self.last_group_ms = now_ms
        self.candidate_groups = 1
        self.candidate_units = units
        self.fast_run = 1 if abs(delta) == 120 else 0
        self.fast_run_start_ms = now_ms
        self.lead = 1
        self.last_new_impulse_ms = now_ms

    def _result(self, pass_original, replay_delta, reason, age_ms):
        """Captures the current state, so call it before _clear_candidate()."""
        result = Decision()
        result.pass_original = pass_original
        result.replay_delta = replay_delta
        result.reason = reason
        result.accepted_direction = self.accepted_direction
        result.lead = self.lead
        result.groups = self.candidate_groups
        result.fast_run = self.fast_run
        result.age_ms = age_ms
        return result

    def process(self, delta, now_ms):
        direction = sign(delta)
        if direction == 0:
            return self._result(True, 0, "ZERO", 0)

        units = max(1, min(20, int(math.floor(abs(delta) / 120.0 + 0.5))))
        pause_ms = math.inf if self.last_input_ms < 0 else now_ms - self.last_input_ms
        self.last_input_ms = now_ms

        # After a pause, the next direction is accepted immediately.
        # tick() has normally ended any pending change before this.
        if self.accepted_direction == 0 or pause_ms >= self.settings.idle_ms:
            self.accepted_direction = direction
            self._clear_candidate()
            return self._result(True, 0, "ACCEPT_IDLE", 0)

        if direction == self.accepted_direction:
            return self._process_accepted_direction(units, now_ms)

        # An event in the other direction starts a pending change, or
        # restarts one that has been quiet for too long.
        if (self.candidate_direction != direction or self.candidate_last_ms < 0 or
                now_ms - self.candidate_last_ms > self.CANDIDATE_TIMEOUT_MS):
            self._start_candidate(direction, delta, units, now_ms)
            return self._result(False, 0, "BLOCK", 0)

        pulse_gap_ms = now_ms - self.candidate_last_ms
        self.candidate_last_ms = now_ms
        self.candidate_units += units
        if now_ms - self.last_new_impulse_ms >= self.SAME_IMPULSE_MS:
            self.lead = min(self.MAX_LEAD, self.lead + 1)
            self.last_new_impulse_ms = now_ms
        if now_ms - self.last_group_ms >= self.settings.group_gap_ms:
            self.candidate_groups += 1
            self.last_group_ms = now_ms

        # A fast reversal must have four separate physical events with
        # plausible hand-driven spacing. Duplicate timestamps and long gaps
        # break the run.
        if (abs(delta) == 120 and self.FAST_MIN_GAP_MS <= pulse_gap_ms <= self.FAST_MAX_GAP_MS
                and self.fast_run > 0):
            self.fast_run += 1
        else:
            self.fast_run = 1 if abs(delta) == 120 else 0
            self.fast_run_start_ms = now_ms

        age_ms = now_ms - self.candidate_first_ms
        leading = self.lead >= self.MIN_LEAD
        fast_confirmation = (leading and self.settings.response == "hybrid" and
                             self.fast_run >= self.FAST_MIN_EVENTS and
                             now_ms - self.fast_run_start_ms >= self.FAST_MIN_SPAN_MS)
        group_confirmation = (leading and self.candidate_groups >= self.settings.groups and
                              age_ms >= self.settings.confirm_ms)

        if fast_confirmation or group_confirmation:
            # Limit the catch-up movement so a change is not a large jump.
            replay_delta = direction * min(self.MAX_REPLAY_STEPS, self.candidate_units) * 120
            self.accepted_direction = direction
            confirmed = self._result(False, replay_delta,
                                     "CONFIRM_FAST" if fast_confirmation else "CONFIRM_GROUPS",
                                     age_ms)
            self._clear_candidate()
            return confirmed

        return self._result(False, 0, "BLOCK", age_ms)

    def tick(self, now_ms):
        """Called every 50 ms, also while no wheel event arrives. A pending
        change ends when no event in the new direction has arrived for
        CANDIDATE_TIMEOUT_MS, or no event at all for idle-ms; the held steps
        are then given back (at most three). Returns None when nothing
        happened."""
        if self.candidate_direction == 0:
            return None

        new_direction_quiet = now_ms - self.candidate_last_ms > self.CANDIDATE_TIMEOUT_MS
        idle = now_ms - self.last_input_ms >= self.settings.idle_ms
        if not new_direction_quiet and not idle:
            return None

        ended_ms = min(self.candidate_last_ms + self.CANDIDATE_TIMEOUT_MS,
                       self.last_input_ms + self.settings.idle_ms)
        if now_ms - ended_ms > self.LATE_TICK_MS:
            # Scrolling now would surprise the user.
            dropped = self._result(False, 0, "TIMEOUT", now_ms - self.candidate_first_ms)
            self._clear_candidate()
            return dropped

        return self._cancel_candidate(False, "TIMEOUT", now_ms - self.candidate_first_ms)

    def _process_accepted_direction(self, units, now_ms):
        if self.candidate_direction == 0:
            return self._result(True, 0, "ACCEPT", 0)

        age_ms = now_ms - self.candidate_first_ms

        # A pending change with no new-direction events for a while ends.
        if now_ms - self.candidate_last_ms > self.CANDIDATE_TIMEOUT_MS:
            return self._cancel_candidate(True, "CANCEL", age_ms)

        hold = self.lead >= self.MIN_LEAD
        if self.last_old_impulse_ms < 0 or now_ms - self.last_old_impulse_ms >= self.SAME_IMPULSE_MS:
            self.lead -= 1
            self.last_old_impulse_ms = now_ms

        # The accepted direction is ahead again: no change after all.
        if self.lead < 0:
            return self._cancel_candidate(True, "CANCEL", age_ms)

        # The new direction is clearly ahead, so this event is probably a
        # fault. Hold it; it is replayed if the change is cancelled.
        if hold:
            self.held_units += units
            return self._result(False, 0, "HOLD", age_ms)

        # Not enough evidence for a change: let the event through. It
        # interrupts the fast pattern, which needs consecutive events.
        self.fast_run = 0
        self.fast_run_start_ms = -1
        return self._result(True, 0, "ACCEPT", age_ms)

    def _cancel_candidate(self, pass_original, reason, age_ms):
        """Ends a pending change in favor of the accepted direction and gives
        back the held steps (at most three). pass_original tells the caller
        whether the current event passes; tick() has no current event."""
        replay_delta = self.accepted_direction * min(self.MAX_REPLAY_STEPS, self.held_units) * 120
        cancelled = self._result(pass_original, replay_delta, reason, age_ms)
        self._clear_candidate()
        return cancelled
