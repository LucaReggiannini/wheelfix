// WheelFix - wheelfix-tools.exe, the console tools: calibrate, debug,
// replay and restore. They work on the files in the WheelFix folder
// (wheelfix.ini, wheelfix.log, recordings); only calibrate uses the mouse
// hook, to record. Compiled by install.bat with the C# compiler of the .NET
// Framework: C# 5 only.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace WheelFix
{
    internal static class Tools
    {
        private const int CalibrationSeconds = 30;
        private const int CountdownSeconds = 3;
        private const int MinCalibrationEvents = 30;

        // calibrate tries these from the most responsive to the strictest:
        // hybrid confirms a regular change of direction at once, grouped
        // always waits confirm-ms. Columns: response, confirm-ms, groups.
        private static readonly string[][] Candidates = new string[][] {
            new string[] { "hybrid", "300", "2" },
            new string[] { "hybrid", "450", "2" },
            new string[] { "hybrid", "450", "3" },
            new string[] { "hybrid", "600", "3" },
            new string[] { "hybrid", "800", "4" },
            new string[] { "grouped", "450", "3" },
            new string[] { "grouped", "600", "4" },
            new string[] { "grouped", "800", "4" },
            new string[] { "grouped", "1200", "5" }
        };

        private static readonly string[] Help = new string[] {
            "WheelFix tools",
            "",
            "wheelfix-tools calibrate [down|up|both]",
            "    Records your wheel while you turn it in one direction at a time, finds",
            "    the settings that let the fewest wrong steps through and offers to",
            "    save them. Nothing scrolls while recording. Default: both.",
            "",
            "wheelfix-tools debug [FILE]",
            "    Shows every wheel event and the decision of WheelFix as it happens.",
            "    With FILE, also saves them, to use later with replay.",
            "",
            "wheelfix-tools replay FILE [--option value ...] [--verbose]",
            "    Runs a recording (from calibrate or debug) through the filter and",
            "    shows what WheelFix would deliver with the settings in wheelfix.ini.",
            "    Options change them for this run only, for example",
            "    --response grouped --confirm-ms 600. --verbose shows every decision.",
            "",
            "wheelfix-tools restore",
            "    Writes the default wheelfix.ini. The current one is kept as",
            "    wheelfix.ini.bak."
        };

        private static readonly string Folder = AppDomain.CurrentDomain.BaseDirectory;
        private static readonly string IniPath = Path.Combine(Folder, Settings.FileName);
        private static readonly string LogPath = Path.Combine(Folder, DecisionLog.FileName);
        private static readonly string AppPath = Path.Combine(Folder, "WheelFix.exe");

        // Set by Ctrl+C during debug.
        private static volatile bool stopRequested = false;
        // 1 while calibrate has closed WheelFix.
        private static int appClosed = 0;

        private const int CTRL_C_EVENT = 0;
        private const int CTRL_BREAK_EVENT = 1;
        private const int CTRL_CLOSE_EVENT = 2;

        private delegate bool ConsoleEventHandler(int eventType);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetConsoleCtrlHandler(ConsoleEventHandler handler,
                                                         [MarshalAs(UnmanagedType.Bool)] bool add);

        // Kept in a field: Windows calls it after RunCalibrate has registered it.
        private static ConsoleEventHandler consoleEvent;

        private static int Main(string[] args)
        {
            string command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
            try
            {
                switch (command)
                {
                    case "calibrate":
                        return RunCalibrate(args);
                    case "debug":
                        return RunDebug(args);
                    case "replay":
                        return RunReplay(args);
                    case "restore":
                        return RunRestore(args);
                    case "help":
                    case "--help":
                    case "-h":
                    case "/?":
                        foreach (string line in Help)
                        {
                            Console.WriteLine(line);
                        }
                        return 0;
                    default:
                        throw new ArgumentException("unknown command \"" + args[0] +
                                                    "\". Run wheelfix-tools for help.");
                }
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("Error: " + error.Message);
                return 1;
            }
        }

        // ---- restore ----

        private static int RunRestore(string[] args)
        {
            ExpectAtMost(args, 1, "restore takes no options.");
            bool kept = File.Exists(IniPath);
            if (kept)
            {
                File.Copy(IniPath, IniPath + ".bak", true);
            }
            File.WriteAllLines(IniPath, Settings.DefaultIni);
            Console.WriteLine("wheelfix.ini now has the default settings. WheelFix applies them within a second.");
            if (kept)
            {
                Console.WriteLine("The previous file is kept as wheelfix.ini.bak.");
            }
            return 0;
        }

        // ---- replay ----

        private static int RunReplay(string[] args)
        {
            if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("replay needs a recording, for example: " +
                                            "wheelfix-tools replay calibration_20261017_101500_down.csv");
            }

            Settings settings = LoadSettings();
            bool verbose = false;
            for (int i = 2; i < args.Length; i++)
            {
                string option = args[i].ToLowerInvariant();
                if (option == "--verbose")
                {
                    verbose = true;
                    continue;
                }
                if (!option.StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                {
                    throw new ArgumentException("options are written as --name value, for example --confirm-ms 600.");
                }
                try
                {
                    settings.Set(option.Substring(2), args[++i]);
                }
                catch (FormatException error)
                {
                    throw new ArgumentException(error.Message);
                }
            }

            List<WheelSample> samples = Recordings.Load(args[1]);
            List<string> log = verbose ? new List<string>() : null;
            ReplayResult result = Replay.Run(settings, samples, log);

            if (verbose)
            {
                long originMs = -1;
                foreach (string line in log)
                {
                    Console.WriteLine(Show(line, ref originMs));
                }
                Console.WriteLine();
            }
            long spanMs = samples[samples.Count - 1].TimeMs - samples[0].TimeMs;
            Say("{0}: {1} events in {2:0.0} s", Path.GetFileName(args[1]), samples.Count, spanMs / 1000.0);
            Console.WriteLine("Settings: " + settings);
            Console.WriteLine();
            Say("{0,-24}{1,8}{2,8}", "Steps", "DOWN", "UP");
            Say("{0,-24}{1,8:0.0}{2,8:0.0}", "Recorded", Steps(result.Recorded[0]), Steps(result.Recorded[1]));
            Say("{0,-24}{1,8:0.0}{2,8:0.0}", "Delivered by WheelFix", Steps(result.Delivered[0]), Steps(result.Delivered[1]));
            Console.WriteLine();
            Console.WriteLine("Decisions: " + result.DecisionCounts());
            return 0;
        }

        // ---- debug ----

        private static int RunDebug(string[] args)
        {
            ExpectAtMost(args, 2, "debug takes at most one file name.");
            string savePath = args.Length == 2 ? args[1] : null;

            bool turnedOn = false;
            if (!LoadSettings().Debug)
            {
                Console.Write("Logging is off (debug = false in wheelfix.ini). Turn it on now? [y/N] ");
                if (!Yes(Console.ReadLine()))
                {
                    return 0;
                }
                SaveOptions("debug", "true");
                turnedOn = true;
            }

            ConsoleCancelEventHandler onCancel = delegate(object sender, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                stopRequested = true;
            };
            Console.CancelKeyPress += onCancel;
            StreamWriter save = null;
            try
            {
                if (savePath != null)
                {
                    save = new StreamWriter(savePath, false, new UTF8Encoding(false));
                    save.WriteLine(DecisionLog.Header);
                    Console.WriteLine("Saving to " + Path.GetFullPath(savePath) + ".");
                }
                Console.WriteLine("Showing wheel events as they happen. Press Enter or Ctrl+C to stop.");
                Console.WriteLine("If nothing appears when you scroll, check that WheelFix is running and enabled.");
                Console.WriteLine();
                Follow(save);
            }
            finally
            {
                Console.CancelKeyPress -= onCancel;
                if (save != null)
                {
                    save.Dispose();
                }
                if (turnedOn)
                {
                    SaveOptions("debug", "false");
                    Console.WriteLine("Logging turned off again.");
                }
            }
            return 0;
        }

        // Shows wheelfix.log as it grows, like "tail -f", starting from the
        // lines written from now on. When the log gets too big, WheelFix
        // renames it and starts a new one; then the new file is followed.
        private static void Follow(StreamWriter save)
        {
            FileStream stream = null;
            StreamReader reader = null;
            StringBuilder pending = new StringBuilder();
            char[] buffer = new char[8192];
            bool fromEnd = true;
            long originMs = -1;
            try
            {
                while (!stopRequested && !EnterPressed())
                {
                    if (reader == null && File.Exists(LogPath))
                    {
                        stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read,
                                                FileShare.ReadWrite | FileShare.Delete);
                        if (fromEnd)
                        {
                            stream.Seek(0, SeekOrigin.End);
                            fromEnd = false;
                        }
                        reader = new StreamReader(stream, new UTF8Encoding(false));
                        pending.Length = 0;
                    }

                    if (reader != null)
                    {
                        int count;
                        while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            pending.Append(buffer, 0, count);
                            string text = pending.ToString();
                            int start = 0;
                            int newline;
                            while ((newline = text.IndexOf('\n', start)) >= 0)
                            {
                                ShowLogLine(text.Substring(start, newline - start).TrimEnd('\r'), save, ref originMs);
                                start = newline + 1;
                            }
                            // Keep a line that is not complete yet.
                            pending.Remove(0, start);
                        }

                        // A file shorter than what was read is a new log.
                        if (!File.Exists(LogPath) || new FileInfo(LogPath).Length < stream.Position)
                        {
                            reader.Dispose();
                            reader = null;
                        }
                    }
                    Thread.Sleep(100);
                }
            }
            finally
            {
                if (reader != null)
                {
                    reader.Dispose();
                }
            }
        }

        private static void ShowLogLine(string line, StreamWriter save, ref long originMs)
        {
            if (line.Length == 0 || line == DecisionLog.Header)
            {
                return;
            }
            if (save != null)
            {
                save.WriteLine(line);
                save.Flush();
            }
            Console.WriteLine(line[0] == '#' ? line : Show(line, ref originMs));
        }

        private static bool EnterPressed()
        {
            if (Console.IsInputRedirected)
            {
                return false;
            }
            while (Console.KeyAvailable)
            {
                if (Console.ReadKey(true).Key == ConsoleKey.Enter)
                {
                    return true;
                }
            }
            return false;
        }

        // A wheelfix.log line as text, with times counted from the first line.
        private static string Show(string line, ref long originMs)
        {
            string[] f = line.Split(',');
            long timeMs;
            if (f.Length < 10 ||
                !Int64.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out timeMs))
            {
                return line;
            }
            if (originMs < 0)
            {
                originMs = timeMs;
            }
            return String.Format(CultureInfo.InvariantCulture,
                "[{0,8}ms] {1,-4} {2,5}  {3,-14} accepted={4,-4} lead={5,2} groups={6} fast-run={7} age={8}ms replay={9}",
                timeMs - originMs, f[1].ToUpperInvariant(), f[2], f[3], f[4].ToUpperInvariant(),
                f[5], f[6], f[7], f[8], f[9]);
        }

        // ---- calibrate ----

        private static int RunCalibrate(string[] args)
        {
            ExpectAtMost(args, 2, "calibrate takes one option: down, up or both.");
            string which = args.Length == 2 ? args[1].ToLowerInvariant() : "both";
            if (which != "down" && which != "up" && which != "both")
            {
                throw new ArgumentException("calibrate takes down, up or both.");
            }
            Settings current = LoadSettings();

            Console.WriteLine("Calibration records your wheel while you scroll in one direction at a time.");
            if (File.Exists(AppPath))
            {
                Console.WriteLine("WheelFix is closed meanwhile and started again at the end.");
                CloseApp();
                // Also when calibrate is stopped with Ctrl+C or its window is closed.
                consoleEvent = OnConsoleEvent;
                SetConsoleCtrlHandler(consoleEvent, true);
            }

            try
            {
                List<Recording> recordings = new List<Recording>();
                using (Recorder recorder = new Recorder())
                {
                    if (which != "up")
                    {
                        recordings.Add(Record(recorder, -1));
                    }
                    if (which != "down")
                    {
                        recordings.Add(Record(recorder, 1));
                    }
                }

                Console.WriteLine();
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                foreach (Recording recording in recordings)
                {
                    Console.WriteLine("Recording saved: " + SaveRecording(recording, stamp));
                }
                Console.WriteLine();
                Suggest(current, recordings);
            }
            finally
            {
                StartAppAgain(true);
            }
            return 0;
        }

        private static Recording Record(Recorder recorder, int direction)
        {
            string name = DecisionLog.Direction(direction).ToUpperInvariant();
            Console.WriteLine();
            Console.Write("Get ready to scroll " + name + " only, as you usually do. ");
            for (int i = CountdownSeconds; i > 0; i--)
            {
                Console.Write(i.ToString(CultureInfo.InvariantCulture) + "... ");
                Thread.Sleep(1000);
            }
            Console.WriteLine();
            Console.WriteLine("START: turn the wheel " + name + " now. Nothing scrolls while recording.");

            long endMs = recorder.Begin(CalibrationSeconds * 1000);
            for (long leftMs = endMs - Clock.NowMs(); leftMs > 0; leftMs = endMs - Clock.NowMs())
            {
                Console.Write(String.Format(CultureInfo.InvariantCulture,
                    "\r{0,3} s left, {1} events recorded ", (leftMs + 999) / 1000, recorder.Count));
                Thread.Sleep((int)Math.Min(250, leftMs));
            }
            Recording recording = new Recording(direction, recorder.End());
            Console.WriteLine();
            Console.WriteLine("STOP.");
            return recording;
        }

        private static string SaveRecording(Recording recording, string stamp)
        {
            string path = Path.Combine(Folder, "calibration_" + stamp + "_" +
                                       DecisionLog.Direction(recording.Direction) + ".csv");
            List<string> lines = new List<string>();
            lines.Add("time_ms,direction,delta");
            foreach (WheelSample sample in recording.Samples)
            {
                lines.Add(String.Format(CultureInfo.InvariantCulture, "{0},{1},{2}",
                    sample.TimeMs, DecisionLog.Direction(Math.Sign(sample.Delta)), sample.Delta));
            }
            File.WriteAllLines(path, lines.ToArray());
            return path;
        }

        // Replays the recordings with your settings and with each candidate,
        // and suggests the best candidate (see Better) if it does better than
        // your settings. Among equally good candidates the most responsive,
        // that is the first in the list, is kept.
        private static void Suggest(Settings current, List<Recording> recordings)
        {
            int events = 0;
            long wrongRecorded = 0;
            foreach (Recording recording in recordings)
            {
                long all = 0;
                long wrong = 0;
                foreach (WheelSample sample in recording.Samples)
                {
                    all += Math.Abs(sample.Delta);
                    if (Math.Sign(sample.Delta) != recording.Direction)
                    {
                        wrong += Math.Abs(sample.Delta);
                    }
                }
                Say("{0}: {1:0.#} steps recorded, {2:0.#} of them in the wrong direction.",
                    DecisionLog.Direction(recording.Direction).ToUpperInvariant(), Steps(all), Steps(wrong));
                events += recording.Samples.Count;
                wrongRecorded += wrong;
            }
            Console.WriteLine();
            if (events < MinCalibrationEvents)
            {
                Console.WriteLine("Too few steps to judge. Run calibrate again and keep scrolling until STOP.");
                return;
            }
            if (wrongRecorded == 0)
            {
                Console.WriteLine("Your wheel sent no steps in the wrong direction: there is nothing to tune.");
                Console.WriteLine("If it misfires only now and then, run calibrate again and scroll faster.");
                return;
            }

            Score currentScore = Evaluate(current, recordings);
            Settings best = null;
            Score bestScore = null;
            bool allEqual = true;
            bool currentListed = false;
            List<Settings> tried = new List<Settings>();
            List<Score> scores = new List<Score>();
            foreach (string[] candidate in Candidates)
            {
                Settings settings = current.Copy();
                settings.Set("response", candidate[0]);
                settings.Set("confirm-ms", candidate[1]);
                settings.Set("groups", candidate[2]);
                Score score = Evaluate(settings, recordings);
                tried.Add(settings);
                scores.Add(score);
                if (best == null || Better(score, bestScore))
                {
                    best = settings;
                    bestScore = score;
                }
                allEqual &= score.Wrong == currentScore.Wrong && score.Lost == currentScore.Lost;
                currentListed |= SameFilter(settings, current);
            }
            bool better = Better(bestScore, currentScore);

            // Nothing to compare: say it in one sentence instead of a table.
            if (allEqual)
            {
                Say("Every setting tried, yours included, lets {0:0.#} of the {1:0.#} wrong steps through and loses {2:0.#} steps.",
                    Steps(currentScore.Wrong), Steps(wrongRecorded), Steps(currentScore.Lost));
                Console.WriteLine("This recording cannot tell them apart. Nothing to change.");
                return;
            }

            Say("For each setting: \"wrong\" is how many of the {0:0.#} wrong steps would still reach", Steps(wrongRecorded));
            Console.WriteLine("your programs (the page jumps back); \"lost\" is how many steps in the direction");
            Console.WriteLine("you were scrolling would be held back and never arrive.");
            Console.WriteLine();
            Say("  {0,-9}{1,11}{2,8}{3,8}{4,7}", "response", "confirm-ms", "groups", "wrong", "lost");
            if (!currentListed)
            {
                PrintScore(current, currentScore, "  <- your settings");
            }
            for (int i = 0; i < tried.Count; i++)
            {
                string note = "";
                if (SameFilter(tried[i], current))
                {
                    note = "  <- your settings";
                }
                else if (better && tried[i] == best)
                {
                    note = "  <- suggested";
                }
                PrintScore(tried[i], scores[i], note);
            }
            Console.WriteLine();

            if (!better)
            {
                Console.WriteLine("No setting tried does better than yours. Nothing to change.");
                return;
            }
            Say("Suggested: response = {0}, confirm-ms = {1}, groups = {2}.",
                best.Response, best.ConfirmMs, best.RequiredGroups);
            Say("Wrong steps that get through: {0:0.#} (now {1:0.#}). Steps lost: {2:0.#} (now {3:0.#}).",
                Steps(bestScore.Wrong), Steps(currentScore.Wrong), Steps(bestScore.Lost), Steps(currentScore.Lost));
            Console.Write("Save these settings to wheelfix.ini? [y/N] ");
            if (!Yes(Console.ReadLine()))
            {
                Console.WriteLine("Nothing was changed.");
                return;
            }
            SaveOptions("response", best.Response,
                        "confirm-ms", best.ConfirmMs.ToString(CultureInfo.InvariantCulture),
                        "groups", best.RequiredGroups.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("Saved. You can change them at any time from the WheelFix icon (Settings).");
        }

        private sealed class Score
        {
            // Wheel units; 120 is one step.
            public long Wrong;
            public long Lost;
        }

        private static Score Evaluate(Settings settings, List<Recording> recordings)
        {
            Score score = new Score();
            foreach (Recording recording in recordings)
            {
                ReplayResult result = Replay.Run(settings, recording.Samples, null);
                int main = recording.Direction > 0 ? 1 : 0;
                score.Wrong += result.Delivered[1 - main];
                score.Lost += Math.Max(0, result.Recorded[main] - result.Delivered[main]);
            }
            return score;
        }

        // Fewer wrong steps first: they are what makes the page jump. With as
        // many, fewer lost steps: WheelFix also gets back to your direction
        // sooner after a fault it could not block.
        private static bool Better(Score a, Score b)
        {
            return a.Wrong < b.Wrong || (a.Wrong == b.Wrong && a.Lost < b.Lost);
        }

        // calibrate changes only these three options.
        private static bool SameFilter(Settings a, Settings b)
        {
            return a.Response == b.Response && a.ConfirmMs == b.ConfirmMs &&
                   a.RequiredGroups == b.RequiredGroups;
        }

        private static void PrintScore(Settings settings, Score score, string note)
        {
            Say("  {0,-9}{1,11}{2,8}{3,8:0.#}{4,7:0.#}{5}", settings.Response, settings.ConfirmMs,
                settings.RequiredGroups, Steps(score.Wrong), Steps(score.Lost), note);
        }

        // calibrate closes WheelFix while it records, so its own hook sees
        // every event, and starts it again once at the end (also on Ctrl+C).
        private static void CloseApp()
        {
            using (Process process = Process.Start(AppPath, "--exit"))
            {
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException("WheelFix could not be closed. " +
                        "Exit it from its icon near the clock, then run calibrate again.");
                }
            }
            appClosed = 1;
        }

        // Starts WheelFix if calibrate closed it, only once.
        private static void StartAppAgain(bool report)
        {
            if (Interlocked.Exchange(ref appClosed, 0) == 1)
            {
                using (Process.Start(AppPath))
                {
                }
                if (report)
                {
                    Console.WriteLine("WheelFix started.");
                }
            }
        }

        // Called by Windows on its own thread when calibrate is interrupted.
        // Returning false lets Windows end the program as usual. Nothing is
        // written to the console: it may be closing.
        private static bool OnConsoleEvent(int eventType)
        {
            if (eventType == CTRL_C_EVENT || eventType == CTRL_BREAK_EVENT || eventType == CTRL_CLOSE_EVENT)
            {
                StartAppAgain(false);
            }
            return false;
        }

        // ---- Shared helpers ----

        // The settings in wheelfix.ini, or the defaults if the file is missing.
        private static Settings LoadSettings()
        {
            if (!File.Exists(IniPath))
            {
                return new Settings();
            }
            try
            {
                return Settings.Load(IniPath);
            }
            catch (FormatException error)
            {
                throw new FormatException(error.Message + " Fix it from the WheelFix icon (Settings), " +
                                          "or run: wheelfix-tools restore");
            }
        }

        // Changes options in wheelfix.ini and keeps everything else in the
        // file, comments included. pairs: name, value, name, value...
        private static void SaveOptions(params string[] pairs)
        {
            if (!File.Exists(IniPath))
            {
                File.WriteAllLines(IniPath, Settings.DefaultIni);
            }
            List<string> lines = new List<string>(File.ReadAllLines(IniPath));
            for (int p = 0; p < pairs.Length; p += 2)
            {
                string name = pairs[p];
                string setting = name + " = " + pairs[p + 1];
                bool found = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    string line = lines[i];
                    int comment = line.IndexOfAny(Settings.CommentChars);
                    string body = comment >= 0 ? line.Substring(0, comment) : line;
                    int equals = body.IndexOf('=');
                    if (equals > 0 && body.Substring(0, equals).Trim().ToLowerInvariant() == name)
                    {
                        lines[i] = comment >= 0 ? setting + " " + line.Substring(comment) : setting;
                        found = true;
                    }
                }
                if (!found)
                {
                    lines.Add(setting);
                }
            }
            File.WriteAllLines(IniPath, lines.ToArray());
        }

        private static void ExpectAtMost(string[] args, int count, string message)
        {
            if (args.Length > count)
            {
                throw new ArgumentException(message);
            }
        }

        private static bool Yes(string answer)
        {
            if (answer == null)
            {
                return false;
            }
            answer = answer.Trim().ToLowerInvariant();
            return answer == "y" || answer == "yes";
        }

        private static double Steps(long units)
        {
            return units / 120.0;
        }

        // Writes a formatted line. Use it only with a literal format string.
        private static void Say(string format, params object[] args)
        {
            Console.WriteLine(String.Format(CultureInfo.InvariantCulture, format, args));
        }
    }

    internal sealed class WheelSample
    {
        public long TimeMs;
        public int Delta;
    }

    internal sealed class Recording
    {
        public readonly int Direction;
        public readonly List<WheelSample> Samples;

        public Recording(int direction, List<WheelSample> samples)
        {
            Direction = direction;
            Samples = samples;
        }
    }

    // Reads recordings: the CSV files of calibrate (also those of older
    // versions, with Italian headers and su/giu), wheelfix.log and the files
    // saved by debug. Only the first three columns are used: time_ms,
    // direction, delta. Notes (#), headers and lines without a wheel event
    // are skipped.
    internal static class Recordings
    {
        public static List<WheelSample> Load(string path)
        {
            string[] lines = File.ReadAllLines(path);
            List<WheelSample> samples = new List<WheelSample>();
            long previousMs = Int64.MinValue;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#' || Char.IsLetter(line[0]))
                {
                    continue;
                }

                string[] fields = line.Split(',');
                long timeMs;
                int value;
                if (fields.Length < 3 ||
                    !Int64.TryParse(fields[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out timeMs) ||
                    !Int32.TryParse(fields[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ||
                    value < Int16.MinValue || value > Int16.MaxValue)
                {
                    throw BadLine(path, i, "expected time_ms,direction,delta.");
                }
                if (value == 0)
                {
                    continue;
                }
                if (timeMs < previousMs)
                {
                    throw BadLine(path, i, "time_ms goes back. Replay one recording at a time.");
                }
                previousMs = timeMs;

                int direction = ParseDirection(fields[1]);
                WheelSample sample = new WheelSample();
                sample.TimeMs = timeMs;
                sample.Delta = (direction != 0 ? direction : Math.Sign(value)) * Math.Abs(value);
                samples.Add(sample);
            }

            if (samples.Count == 0)
            {
                throw new FormatException(Path.GetFileName(path) + " contains no wheel events.");
            }
            return samples;
        }

        private static int ParseDirection(string text)
        {
            switch (text.Trim().ToLowerInvariant())
            {
                case "up":
                case "su":
                    return 1;
                case "down":
                case "giu":
                    return -1;
                default:
                    return 0;
            }
        }

        private static FormatException BadLine(string path, int index, string message)
        {
            return new FormatException(String.Format(CultureInfo.InvariantCulture,
                "{0}, line {1}: {2}", Path.GetFileName(path), index + 1, message));
        }
    }

    internal sealed class ReplayResult
    {
        private static readonly string[] ReasonOrder = new string[] {
            "ACCEPT", "ACCEPT_IDLE", "BLOCK", "HOLD", "CONFIRM_FAST", "CONFIRM_GROUPS", "CANCEL", "TIMEOUT"
        };

        // Wheel units per direction: [0] DOWN, [1] UP. 120 is one step.
        public readonly long[] Recorded = new long[2];
        public readonly long[] Delivered = new long[2];
        public readonly Dictionary<string, int> Decisions = new Dictionary<string, int>();

        public string DecisionCounts()
        {
            List<string> parts = new List<string>();
            foreach (string reason in ReasonOrder)
            {
                int count;
                if (Decisions.TryGetValue(reason, out count))
                {
                    parts.Add(reason + "=" + count.ToString(CultureInfo.InvariantCulture));
                }
            }
            return String.Join(", ", parts.ToArray());
        }
    }

    // Runs a recording through WheelFilter as WheelFix.exe does, including
    // its 50 ms timer, which ends pending changes when the wheel goes quiet.
    internal static class Replay
    {
        private const int TickMs = 50;

        // log, if not null, receives one wheelfix.log line per decision, with
        // times counted from the first event.
        public static ReplayResult Run(Settings settings, List<WheelSample> samples, List<string> log)
        {
            WheelFilter filter = new WheelFilter(settings);
            ReplayResult result = new ReplayResult();
            long originMs = samples[0].TimeMs;
            long nextTickMs = originMs + TickMs;

            foreach (WheelSample sample in samples)
            {
                for (; nextTickMs < sample.TimeMs; nextTickMs += TickMs)
                {
                    Apply(result, filter.Tick(nextTickMs), nextTickMs - originMs, 0, log);
                }
                Add(result.Recorded, sample.Delta);
                WheelDecision decision = filter.Process(sample.Delta, sample.TimeMs);
                if (decision.PassOriginal)
                {
                    Add(result.Delivered, sample.Delta);
                }
                Apply(result, decision, sample.TimeMs - originMs, sample.Delta, log);
            }

            // A change still pending at the end finishes as it would live.
            long endMs = samples[samples.Count - 1].TimeMs + settings.IdleMs + TickMs;
            for (; nextTickMs <= endMs; nextTickMs += TickMs)
            {
                Apply(result, filter.Tick(nextTickMs), nextTickMs - originMs, 0, log);
            }
            return result;
        }

        private static void Apply(ReplayResult result, WheelDecision decision, long timeMs, int delta,
                                  List<string> log)
        {
            if (decision == null)
            {
                return;
            }
            Add(result.Delivered, decision.ReplayDelta);
            int count;
            result.Decisions.TryGetValue(decision.Reason, out count);
            result.Decisions[decision.Reason] = count + 1;
            if (log != null)
            {
                log.Add(DecisionLog.Format(timeMs, delta, decision));
            }
        }

        private static void Add(long[] units, int delta)
        {
            if (delta < 0)
            {
                units[0] -= delta;
            }
            else
            {
                units[1] += delta;
            }
        }
    }

    // Records physical wheel events with its own mouse hook, on a separate
    // thread with a message loop, so the console can never delay the hook.
    // While a phase is recorded, the wheel scrolls nothing: otherwise the
    // console would scroll away from the countdown. Outside the phases the
    // wheel works normally.
    internal sealed class Recorder : IDisposable
    {
        private const int MaxSamples = 100000;

        private readonly object sync = new object();
        private readonly Thread thread;
        private readonly ManualResetEvent ready = new ManualResetEvent(false);
        private volatile bool stopping = false;
        private Exception startError;
        private List<WheelSample> samples;
        private long startMs;
        private long endMs;

        public Recorder()
        {
            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.WaitOne();
            if (startError != null)
            {
                throw new InvalidOperationException(startError.Message, startError);
            }
        }

        public int Count
        {
            get
            {
                lock (sync)
                {
                    return samples == null ? 0 : samples.Count;
                }
            }
        }

        // Starts a phase and returns when it ends. The recorder stops by itself
        // at that time, so the wheel is never blocked for longer, even if the
        // console stops this program's main thread (for example while text is
        // selected in it).
        public long Begin(int durationMs)
        {
            lock (sync)
            {
                samples = new List<WheelSample>();
                startMs = Clock.NowMs();
                endMs = startMs + durationMs;
                return endMs;
            }
        }

        public List<WheelSample> End()
        {
            lock (sync)
            {
                List<WheelSample> recorded = samples;
                samples = null;
                return recorded;
            }
        }

        public void Dispose()
        {
            stopping = true;
            thread.Join(2000);
        }

        private void Run()
        {
            WheelHook hook;
            try
            {
                hook = new WheelHook(OnWheel);
            }
            catch (Exception error)
            {
                startError = error;
                ready.Set();
                return;
            }

            using (hook)
            using (System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer())
            {
                ApplicationContext context = new ApplicationContext();
                timer.Interval = 50;
                timer.Tick += delegate
                {
                    hook.Maintain(Clock.NowMs());
                    if (stopping)
                    {
                        context.ExitThread();
                    }
                };
                timer.Start();
                ready.Set();
                Application.Run(context);
            }
        }

        // Returns true while recording: the event is recorded and blocked.
        private bool OnWheel(int delta)
        {
            lock (sync)
            {
                long now = Clock.NowMs();
                if (samples == null || now >= endMs)
                {
                    return false;
                }
                if (samples.Count < MaxSamples)
                {
                    WheelSample sample = new WheelSample();
                    sample.TimeMs = now - startMs;
                    sample.Delta = delta;
                    samples.Add(sample);
                }
                return true;
            }
        }
    }
}
