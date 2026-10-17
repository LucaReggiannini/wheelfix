// WheelFix - filter and settings, shared by the WheelFix executables.
// Compiled by install.bat with the C# compiler of the .NET Framework, which
// accepts C# 5 only: no string interpolation, no ?. operator, no => members.
// Nothing in this file uses Windows APIs.

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace WheelFix
{
    public static class Clock
    {
        // Milliseconds since Windows started. It never goes back, also when
        // the system time changes.
        public static long NowMs()
        {
            return (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency));
        }
    }

    // Values read from wheelfix.ini. A missing line keeps its default.
    public sealed class Settings
    {
        public const string FileName = "wheelfix.ini";
        public static readonly char[] CommentChars = new char[] { ';', '#' };

        public string Response = "hybrid";
        public int ConfirmMs = 450;
        public int RequiredGroups = 3;
        public int GroupGapMs = 90;
        public int IdleMs = 1100;
        public bool Debug = false;

        // Written when wheelfix.ini does not exist. It is also the manual for
        // the options, so keep it in sync with Set() below.
        public static readonly string[] DefaultIni = new string[] {
            "; WheelFix settings",
            "; Save this file and WheelFix applies the changes within a second.",
            "; Delete a line to use its default value; the defaults are shown below.",
            "",
            "; How a change of scrolling direction is confirmed:",
            ";   hybrid   after 4 regular steps in the new direction, or by the",
            ";            grouped rule below",
            ";   grouped  by the grouped rule only (slower, stricter)",
            "; If the page still jumps the wrong way, try grouped.",
            "response = hybrid",
            "",
            "; Grouped rule. A change of direction is confirmed when both are true:",
            "; - at least confirm-ms milliseconds have passed since the first step",
            ";   in the new direction (100-3000);",
            "; - those steps came in at least \"groups\" separate groups (2-20). A step",
            ";   starts a new group if it arrives at least group-gap-ms milliseconds",
            ";   after the start of the previous group (10-1500).",
            "; If the page still jumps the wrong way, raise confirm-ms or groups.",
            "; If changing direction feels slow, lower them.",
            "confirm-ms = 450",
            "groups = 3",
            "group-gap-ms = 90",
            "",
            "; After a pause of at least idle-ms milliseconds, the next step is",
            "; accepted at once, whatever its direction (100-10000). A short pause is",
            "; the quickest way to change direction. If the first movement after a",
            "; short pause feels held back, lower it, for example to 500.",
            "idle-ms = 1100",
            "",
            "; Writes every wheel event and the decision taken to wheelfix.log in",
            "; this folder (true or false). To watch it live: wheelfix-tools debug",
            "debug = false"
        };

        public Settings Copy()
        {
            return (Settings)MemberwiseClone();
        }

        // The options in wheelfix.ini syntax, for messages and logs.
        public override string ToString()
        {
            return String.Format(CultureInfo.InvariantCulture,
                "response = {0}, confirm-ms = {1}, groups = {2}, group-gap-ms = {3}, idle-ms = {4}, debug = {5}",
                Response, ConfirmMs, RequiredGroups, GroupGapMs, IdleMs, Debug ? "true" : "false");
        }

        // Reads a settings file. Throws FormatException with the line number
        // for invalid content, and IOException if the file cannot be read.
        public static Settings Load(string path)
        {
            Settings settings = new Settings();
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int comment = line.IndexOfAny(CommentChars);
                if (comment >= 0)
                {
                    line = line.Substring(0, comment);
                }
                line = line.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                string where = String.Format(CultureInfo.InvariantCulture,
                    "{0}, line {1}: ", Path.GetFileName(path), i + 1);
                int equals = line.IndexOf('=');
                if (equals <= 0)
                {
                    throw new FormatException(where + "expected name = value.");
                }
                try
                {
                    settings.Set(line.Substring(0, equals).Trim(), line.Substring(equals + 1).Trim());
                }
                catch (FormatException error)
                {
                    throw new FormatException(where + error.Message);
                }
            }
            return settings;
        }

        // Sets one option by its name in wheelfix.ini.
        public void Set(string name, string value)
        {
            name = name.ToLowerInvariant();
            value = value.ToLowerInvariant();
            switch (name)
            {
                case "response":
                    if (value != "hybrid" && value != "grouped")
                    {
                        throw new FormatException("response must be hybrid or grouped.");
                    }
                    Response = value;
                    break;
                case "confirm-ms":
                    ConfirmMs = ReadNumber(name, value, 100, 3000);
                    break;
                case "groups":
                    RequiredGroups = ReadNumber(name, value, 2, 20);
                    break;
                case "group-gap-ms":
                    GroupGapMs = ReadNumber(name, value, 10, 1500);
                    break;
                case "idle-ms":
                    IdleMs = ReadNumber(name, value, 100, 10000);
                    break;
                case "debug":
                    if (value != "true" && value != "false")
                    {
                        throw new FormatException("debug must be true or false.");
                    }
                    Debug = value == "true";
                    break;
                default:
                    throw new FormatException("unknown option \"" + name + "\".");
            }
        }

        private static int ReadNumber(string name, string value, int minimum, int maximum)
        {
            int number;
            if (!Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ||
                number < minimum || number > maximum)
            {
                throw new FormatException(String.Format(CultureInfo.InvariantCulture,
                    "{0} must be a whole number from {1} to {2}.", name, minimum, maximum));
            }
            return number;
        }
    }

    // One line per decision, written by WheelFix.exe to wheelfix.log when
    // debug = true, and read by wheelfix-tools (debug, replay). The first
    // three columns are those of the calibration recordings, so a log can be
    // replayed too. Lines starting with # are notes.
    public static class DecisionLog
    {
        public const string FileName = "wheelfix.log";
        public const string Header =
            "time_ms,direction,delta,decision,accepted,lead,groups,fast_run,age_ms,replay";

        public static string Format(long timeMs, int delta, WheelDecision decision)
        {
            return String.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3},{4},{5},{6},{7},{8},{9}",
                timeMs, Direction(Math.Sign(delta)), delta, decision.Reason,
                Direction(decision.AcceptedDirection), decision.Lead, decision.Groups,
                decision.FastRun, decision.AgeMs, decision.ReplayDelta);
        }

        public static string Direction(int direction)
        {
            if (direction > 0)
            {
                return "up";
            }
            return direction < 0 ? "down" : "none";
        }
    }

    public sealed class WheelDecision
    {
        public bool PassOriginal;
        public int ReplayDelta;
        public string Reason;
        public int AcceptedDirection;
        public int Lead;
        public int Groups;
        public int FastRun;
        public long AgeMs;
    }

    // The filter always uses signed wheel deltas: a physical step is +120 for
    // UP and -120 for DOWN on a typical mouse.
    //
    // Terms:
    // - accepted direction: events in this direction normally pass at once.
    // - pending change (the "candidate" fields): events in the other direction
    //   were seen; WheelFix is deciding whether they are faults or a real
    //   change of direction. They are blocked meanwhile.
    // - lead: while a change is pending, +1 for each event in the new
    //   direction and -1 for each event in the accepted direction.
    public sealed class WheelFilter
    {
        // A pending change ends if no event in the new direction arrives
        // for this long.
        private const int CandidateTimeoutMs = 850;
        private const int MaxReplaySteps = 3;

        // If the timer notices a finished change only this late (for example
        // after the PC slept), the held steps are dropped instead of replayed.
        private const int LateTickMs = 1000;

        // A pending change must lead by MinLead before it can be confirmed.
        // From the same lead on, events in the accepted direction are treated
        // as faults and held. Without this, a wheel that also misfires while
        // the user reverses would reset every pending change and the page
        // would move against the user until the next pause.
        private const int MinLead = 2;

        // The lead never exceeds MaxLead, so if the user goes back to the
        // accepted direction, at most two of those steps are held (and later
        // replayed) before events pass normally again.
        private const int MaxLead = 3;

        // Recorded faulty bursts contained several events at the same
        // timestamp. Events in the same direction closer than this count once
        // in the lead, so a single burst is weak evidence.
        private const int SameImpulseMs = 12;

        // Four distinct, evenly spaced events in the new direction strongly
        // suggest a fast manual reversal. Duplicate/near-zero gaps are
        // ignored. This is a heuristic: another broken wheel may need the
        // grouped response.
        private const int FastMinEvents = 4;
        private const int FastMinSpanMs = 60;
        private const int FastMinGapMs = 12;
        private const int FastMaxGapMs = 90;

        private readonly Settings settings;
        private int acceptedDirection = 0;
        private long lastInputMs = -1;
        private int candidateDirection = 0;
        private long candidateFirstMs = -1;
        private long candidateLastMs = -1;
        private long lastGroupMs = -1;
        private int candidateGroups = 0;
        private int candidateUnits = 0;
        private int fastRun = 0;
        private long fastRunStartMs = -1;
        private int lead = 0;
        private long lastNewImpulseMs = -1;
        private long lastOldImpulseMs = -1;
        private int heldUnits = 0;

        public WheelFilter(Settings settings)
        {
            this.settings = settings;
        }

        public void Reset()
        {
            acceptedDirection = 0;
            lastInputMs = -1;
            ClearCandidate();
        }

        private void ClearCandidate()
        {
            candidateDirection = 0;
            candidateFirstMs = -1;
            candidateLastMs = -1;
            lastGroupMs = -1;
            candidateGroups = 0;
            candidateUnits = 0;
            fastRun = 0;
            fastRunStartMs = -1;
            lead = 0;
            lastNewImpulseMs = -1;
            lastOldImpulseMs = -1;
            heldUnits = 0;
        }

        private void StartCandidate(int direction, int delta, int units, long nowMs)
        {
            ClearCandidate();
            candidateDirection = direction;
            candidateFirstMs = nowMs;
            candidateLastMs = nowMs;
            lastGroupMs = nowMs;
            candidateGroups = 1;
            candidateUnits = units;
            fastRun = Math.Abs(delta) == 120 ? 1 : 0;
            fastRunStartMs = nowMs;
            lead = 1;
            lastNewImpulseMs = nowMs;
        }

        // Captures the current state, so call it before ClearCandidate().
        private WheelDecision Result(bool passOriginal, int replayDelta, string reason, long ageMs)
        {
            WheelDecision result = new WheelDecision();
            result.PassOriginal = passOriginal;
            result.ReplayDelta = replayDelta;
            result.Reason = reason;
            result.AcceptedDirection = acceptedDirection;
            result.Lead = lead;
            result.Groups = candidateGroups;
            result.FastRun = fastRun;
            result.AgeMs = ageMs;
            return result;
        }

        public WheelDecision Process(int delta, long nowMs)
        {
            int direction = Math.Sign(delta);
            if (direction == 0)
            {
                return Result(true, 0, "ZERO", 0);
            }

            int units = Math.Max(1, Math.Min(20,
                (int)Math.Round(Math.Abs((double)delta) / 120.0,
                                MidpointRounding.AwayFromZero)));
            long pauseMs = lastInputMs < 0 ? Int64.MaxValue : nowMs - lastInputMs;
            lastInputMs = nowMs;

            // After a pause, the next direction is accepted immediately.
            // Tick() has normally ended any pending change before this.
            if (acceptedDirection == 0 || pauseMs >= settings.IdleMs)
            {
                acceptedDirection = direction;
                ClearCandidate();
                return Result(true, 0, "ACCEPT_IDLE", 0);
            }

            if (direction == acceptedDirection)
            {
                return ProcessAcceptedDirection(units, nowMs);
            }

            // An event in the other direction starts a pending change, or
            // restarts one that has been quiet for too long.
            if (candidateDirection != direction || candidateLastMs < 0 ||
                nowMs - candidateLastMs > CandidateTimeoutMs)
            {
                StartCandidate(direction, delta, units, nowMs);
                return Result(false, 0, "BLOCK", 0);
            }

            long pulseGapMs = nowMs - candidateLastMs;
            candidateLastMs = nowMs;
            candidateUnits += units;
            if (nowMs - lastNewImpulseMs >= SameImpulseMs)
            {
                lead = Math.Min(MaxLead, lead + 1);
                lastNewImpulseMs = nowMs;
            }
            if (nowMs - lastGroupMs >= settings.GroupGapMs)
            {
                candidateGroups++;
                lastGroupMs = nowMs;
            }

            // A fast reversal must have four separate physical events with
            // plausible hand-driven spacing. Duplicate timestamps and long
            // gaps break the run; a single bounce burst cannot pass this test
            // merely by reporting many wheel ticks in the same millisecond.
            if (Math.Abs(delta) == 120 && pulseGapMs >= FastMinGapMs &&
                pulseGapMs <= FastMaxGapMs && fastRun > 0)
            {
                fastRun++;
            }
            else
            {
                fastRun = Math.Abs(delta) == 120 ? 1 : 0;
                fastRunStartMs = nowMs;
            }

            long ageMs = nowMs - candidateFirstMs;
            bool leading = lead >= MinLead;
            bool fastConfirmation = leading && settings.Response == "hybrid" &&
                fastRun >= FastMinEvents &&
                nowMs - fastRunStartMs >= FastMinSpanMs;
            bool groupConfirmation = leading &&
                candidateGroups >= settings.RequiredGroups &&
                ageMs >= settings.ConfirmMs;

            if (fastConfirmation || groupConfirmation)
            {
                // Limit the catch-up movement so a change is not a large jump.
                int replayDelta = direction * Math.Min(MaxReplaySteps, candidateUnits) * 120;
                acceptedDirection = direction;
                WheelDecision confirmed = Result(false, replayDelta,
                    fastConfirmation ? "CONFIRM_FAST" : "CONFIRM_GROUPS", ageMs);
                ClearCandidate();
                return confirmed;
            }

            return Result(false, 0, "BLOCK", ageMs);
        }

        // Called regularly by the host (every 50 ms), also while no wheel
        // event arrives. A pending change ends when no event in the new
        // direction has arrived for CandidateTimeoutMs, or no event at all
        // for IdleMs; the held steps are then given back (at most three).
        // Returns null when nothing happened.
        public WheelDecision Tick(long nowMs)
        {
            if (candidateDirection == 0)
            {
                return null;
            }

            bool newDirectionQuiet = nowMs - candidateLastMs > CandidateTimeoutMs;
            bool idle = nowMs - lastInputMs >= settings.IdleMs;
            if (!newDirectionQuiet && !idle)
            {
                return null;
            }

            long endedMs = Math.Min(candidateLastMs + CandidateTimeoutMs,
                                    lastInputMs + settings.IdleMs);
            if (nowMs - endedMs > LateTickMs)
            {
                // Scrolling now would surprise the user.
                WheelDecision dropped = Result(false, 0, "TIMEOUT", nowMs - candidateFirstMs);
                ClearCandidate();
                return dropped;
            }

            return CancelCandidate(false, "TIMEOUT", nowMs - candidateFirstMs);
        }

        // An event in the accepted direction.
        private WheelDecision ProcessAcceptedDirection(int units, long nowMs)
        {
            if (candidateDirection == 0)
            {
                return Result(true, 0, "ACCEPT", 0);
            }

            long ageMs = nowMs - candidateFirstMs;

            // A pending change with no new-direction events for a while ends.
            if (nowMs - candidateLastMs > CandidateTimeoutMs)
            {
                return CancelCandidate(true, "CANCEL", ageMs);
            }

            bool hold = lead >= MinLead;
            if (lastOldImpulseMs < 0 || nowMs - lastOldImpulseMs >= SameImpulseMs)
            {
                lead--;
                lastOldImpulseMs = nowMs;
            }

            // The accepted direction is ahead again: no change after all.
            if (lead < 0)
            {
                return CancelCandidate(true, "CANCEL", ageMs);
            }

            // The new direction is clearly ahead, so this event is probably a
            // fault. Hold it; it is replayed if the change is cancelled.
            if (hold)
            {
                heldUnits += units;
                return Result(false, 0, "HOLD", ageMs);
            }

            // Not enough evidence for a change: let the event through. It
            // interrupts the fast pattern, which needs consecutive events.
            fastRun = 0;
            fastRunStartMs = -1;
            return Result(true, 0, "ACCEPT", ageMs);
        }

        // Ends a pending change in favor of the accepted direction and gives
        // back the held steps (at most three). passOriginal tells the host
        // whether the current event passes; Tick() has no current event.
        private WheelDecision CancelCandidate(bool passOriginal, string reason, long ageMs)
        {
            int replayDelta = acceptedDirection * Math.Min(MaxReplaySteps, heldUnits) * 120;
            WheelDecision cancelled = Result(passOriginal, replayDelta, reason, ageMs);
            ClearCandidate();
            return cancelled;
        }
    }
}
