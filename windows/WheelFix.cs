// WheelFix - tray application. Filters the vertical mouse wheel with
// WheelFilter, using the options in wheelfix.ini next to the executable.
// Compiled by install.bat as a Windows application (no console window), with
// the C# compiler of the .NET Framework: C# 5 only.

using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace WheelFix
{
    internal static class Program
    {
        // Only one filter at a time. Older versions use the same name.
        private const string MutexName = @"Local\WheelFix.MouseHook";
        // Set by "WheelFix.exe --exit" to close the running instance.
        public const string ExitEventName = @"Local\WheelFix.Exit";
        private const int ExitTimeoutMs = 5000;

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--exit")
            {
                return ExitRunningInstance();
            }
            if (args.Length > 0)
            {
                ShowError("WheelFix takes no arguments. Its options are in " + Settings.FileName + ".");
                return 1;
            }

            using (Mutex mutex = new Mutex(false, MutexName))
            {
                if (!Acquire(mutex, 0))
                {
                    ShowError("WheelFix is already running. Look for its icon near the clock.");
                    return 1;
                }
                try
                {
                    using (TrayApp app = new TrayApp())
                    {
                        Application.Run(app);
                    }
                    return 0;
                }
                catch (Exception error)
                {
                    ShowError(error.Message);
                    return 1;
                }
                finally
                {
                    mutex.ReleaseMutex();
                }
            }
        }

        // Used by install.bat, uninstall.bat and wheelfix-tools calibrate.
        // Returns only when no instance is running any more, so that
        // WheelFix.exe can be replaced. Exit codes: 0 done, 1 an instance did
        // not exit in time, 2 an older version without --exit is running.
        private static int ExitRunningInstance()
        {
            Mutex mutex;
            if (Mutex.TryOpenExisting(MutexName, out mutex))
            {
                using (mutex)
                {
                    EventWaitHandle exitEvent;
                    bool asked = EventWaitHandle.TryOpenExisting(ExitEventName, out exitEvent);
                    if (asked)
                    {
                        using (exitEvent)
                        {
                            exitEvent.Set();
                        }
                    }
                    // The running instance releases the mutex when it exits.
                    if (!Acquire(mutex, asked ? ExitTimeoutMs : 0))
                    {
                        return asked ? 1 : 2;
                    }
                    mutex.ReleaseMutex();
                }
            }
            return WaitForOtherProcesses() ? 0 : 1;
        }

        // The mutex is released a moment before the process ends, and
        // Windows keeps WheelFix.exe locked until the process has ended.
        private static bool WaitForOtherProcesses()
        {
            bool ended = true;
            using (Process self = Process.GetCurrentProcess())
            {
                foreach (Process process in Process.GetProcessesByName("WheelFix"))
                {
                    using (process)
                    {
                        try
                        {
                            if (process.Id != self.Id && process.SessionId == self.SessionId &&
                                !process.WaitForExit(ExitTimeoutMs))
                            {
                                ended = false;
                            }
                        }
                        catch (InvalidOperationException)
                        {
                            // It ended meanwhile.
                        }
                        catch (Win32Exception)
                        {
                            // Not accessible, so not started by this user.
                        }
                    }
                }
            }
            return ended;
        }

        private static bool Acquire(Mutex mutex, int timeoutMs)
        {
            try
            {
                return mutex.WaitOne(timeoutMs);
            }
            catch (AbandonedMutexException)
            {
                return true;
            }
        }

        private static void ShowError(string message)
        {
            MessageBox.Show(message, "WheelFix", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // A WinForms ApplicationContext provides the message loop that the
    // mouse hook and the tray icon need, without showing a form. The hook
    // callback, the timer and the menu all run on this one thread.
    internal sealed class TrayApp : ApplicationContext
    {
        private const int TimerIntervalMs = 50;
        private const int SettingsCheckMs = 1000;

        private readonly string folder;
        private readonly string settingsPath;
        private readonly EventWaitHandle exitEvent;
        private NotifyIcon tray;
        private ContextMenuStrip menu;
        private ToolStripMenuItem enabledItem;
        private System.Windows.Forms.Timer timer;
        private WheelHook hook;
        // Created when debug is first turned on.
        private LogWriter log;

        private WheelFilter filter;
        private bool filterEnabled = true;
        private bool debug = false;
        private DateTime settingsStamp = DateTime.MinValue;
        private long nextSettingsCheckMs = 0;
        private bool released = false;

        public TrayApp()
        {
            folder = AppDomain.CurrentDomain.BaseDirectory;
            settingsPath = Path.Combine(folder, Settings.FileName);
            exitEvent = new EventWaitHandle(false, EventResetMode.ManualReset, Program.ExitEventName);
            // If the previous instance is still closing, the event may exist
            // and still be set: that request was not for this instance.
            exitEvent.Reset();

            try
            {
                CreateTray();

                // Start with the defaults, then read wheelfix.ini through the
                // same path used when the file changes later.
                filter = new WheelFilter(new Settings());
                CreateSettingsFileIfMissing();
                CheckSettingsFile(Clock.NowMs());

                hook = new WheelHook(OnWheel);
                Note("WheelFix started.");

                timer = new System.Windows.Forms.Timer();
                timer.Interval = TimerIntervalMs;
                timer.Tick += OnTimer;
                timer.Start();
            }
            catch
            {
                ReleaseResources();
                throw;
            }
        }

        private void CreateTray()
        {
            menu = new ContextMenuStrip();
            enabledItem = new ToolStripMenuItem("Enabled");
            enabledItem.Checked = true;
            enabledItem.Click += OnEnabledClick;
            menu.Items.Add(enabledItem);

            ToolStripMenuItem settingsItem = new ToolStripMenuItem("Settings");
            settingsItem.Click += OnSettingsClick;
            menu.Items.Add(settingsItem);

            ToolStripMenuItem toolsItem = new ToolStripMenuItem("Tools");
            toolsItem.Click += OnToolsClick;
            menu.Items.Add(toolsItem);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += OnExitClick;
            menu.Items.Add(exitItem);

            tray = new NotifyIcon();
            tray.Icon = SystemIcons.Application;
            tray.Text = "WheelFix";
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
        }

        private void ShowWarning(string text)
        {
            tray.ShowBalloonTip(10000, "WheelFix", text, ToolTipIcon.Warning);
        }

        // Writes a note line to wheelfix.log, when debug is on.
        private void Note(string text)
        {
            if (debug)
            {
                log.Write("# " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                          " " + text);
            }
        }

        // ---- Settings ----

        private void CreateSettingsFileIfMissing()
        {
            if (File.Exists(settingsPath))
            {
                return;
            }
            try
            {
                File.WriteAllLines(settingsPath, Settings.DefaultIni);
            }
            catch (IOException)
            {
                // The defaults are in use anyway.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        // Reloads wheelfix.ini when its modification time changes. A file
        // that cannot be read (missing, or being saved) is tried again at
        // the next check; a file with an error keeps the previous settings.
        private void CheckSettingsFile(long now)
        {
            if (now < nextSettingsCheckMs)
            {
                return;
            }
            nextSettingsCheckMs = now + SettingsCheckMs;

            DateTime stamp = File.GetLastWriteTimeUtc(settingsPath);
            if (stamp == settingsStamp)
            {
                return;
            }

            Settings loaded;
            try
            {
                loaded = Settings.Load(settingsPath);
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
            catch (FormatException error)
            {
                settingsStamp = stamp;
                ShowWarning(error.Message + " The previous settings are still in use.");
                return;
            }

            settingsStamp = stamp;
            filter = new WheelFilter(loaded);
            if (debug && !loaded.Debug)
            {
                Note("Logging turned off.");
            }
            if (loaded.Debug && log == null)
            {
                log = new LogWriter(Path.Combine(folder, DecisionLog.FileName));
            }
            debug = loaded.Debug;
            Note("Settings: " + loaded);
        }

        // ---- Filtering ----

        // Called by the hook for each physical wheel event. Returns true to
        // block it.
        private bool OnWheel(int delta)
        {
            if (!filterEnabled)
            {
                return false;
            }

            long now = Clock.NowMs();
            WheelDecision decision = filter.Process(delta, now);
            if (debug)
            {
                log.Write(DecisionLog.Format(now, delta, decision));
            }

            if (decision.PassOriginal)
            {
                // A cancelled change also gives back held steps.
                if (decision.ReplayDelta != 0)
                {
                    SendSteps(decision.ReplayDelta);
                }
                return false;
            }

            // If the replay cannot be sent, let the original event through.
            return decision.ReplayDelta == 0 || SendSteps(decision.ReplayDelta);
        }

        private bool SendSteps(int delta)
        {
            if (WheelHook.Inject(delta))
            {
                return true;
            }
            Note("Windows did not accept the replayed steps.");
            return false;
        }

        private void OnTimer(object sender, EventArgs args)
        {
            if (exitEvent.WaitOne(0))
            {
                ExitThread();
                return;
            }

            long now = Clock.NowMs();
            long pausedMs = hook.Maintain(now);
            if (pausedMs > 0)
            {
                Note("WheelFix could not run for " + pausedMs.ToString(CultureInfo.InvariantCulture) +
                     " ms (busy system or sleep); mouse hook reinstalled.");
            }
            CheckSettingsFile(now);

            if (filterEnabled)
            {
                // A pending change can end without any wheel event: give back
                // the held steps.
                WheelDecision decision = filter.Tick(now);
                if (decision != null)
                {
                    if (debug)
                    {
                        log.Write(DecisionLog.Format(now, 0, decision));
                    }
                    if (decision.ReplayDelta != 0)
                    {
                        SendSteps(decision.ReplayDelta);
                    }
                }
            }
        }

        // ---- Menu ----

        private void OnEnabledClick(object sender, EventArgs args)
        {
            filterEnabled = !filterEnabled;
            filter.Reset();
            enabledItem.Checked = filterEnabled;
            tray.Text = filterEnabled ? "WheelFix" : "WheelFix (paused)";
            Note(filterEnabled ? "Filter resumed." : "Filter paused.");
        }

        private void OnSettingsClick(object sender, EventArgs args)
        {
            CreateSettingsFileIfMissing();
            StartProcess(new ProcessStartInfo("notepad.exe", "\"" + settingsPath + "\""));
        }

        // Opens a console in the WheelFix folder, showing the tools' help.
        private void OnToolsClick(object sender, EventArgs args)
        {
            ProcessStartInfo info = new ProcessStartInfo("cmd.exe", "/k wheelfix-tools.exe");
            info.WorkingDirectory = folder;
            StartProcess(info);
        }

        private void StartProcess(ProcessStartInfo info)
        {
            try
            {
                using (Process.Start(info))
                {
                }
            }
            catch (Win32Exception error)
            {
                ShowWarning("Cannot start " + info.FileName + ": " + error.Message);
            }
        }

        private void OnExitClick(object sender, EventArgs args)
        {
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ReleaseResources();
            }
            base.Dispose(disposing);
        }

        // Also called by the constructor if it fails halfway.
        private void ReleaseResources()
        {
            if (released)
            {
                return;
            }
            released = true;
            if (timer != null)
            {
                timer.Dispose();
            }
            if (hook != null)
            {
                hook.Dispose();
            }
            if (tray != null)
            {
                tray.Visible = false;
                tray.Dispose();
            }
            if (menu != null)
            {
                menu.Dispose();
            }
            if (log != null)
            {
                log.Dispose();
            }
            exitEvent.Dispose();
        }
    }

    // Writes wheelfix.log on a background thread, so the hook callback never
    // waits for the disk. When the file reaches MaxBytes it becomes
    // wheelfix.log.old and a new one is started, so at most about twice
    // MaxBytes are kept. The file can be read while it is written.
    internal sealed class LogWriter : IDisposable
    {
        private const long MaxBytes = 5 * 1024 * 1024;
        private const int Capacity = 10000;

        private readonly string path;
        private readonly BlockingCollection<string> queue = new BlockingCollection<string>(Capacity);
        private readonly Thread thread;

        public LogWriter(string path)
        {
            this.path = path;
            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Name = "WheelFix log";
            thread.Start();
        }

        // Never waits: if the queue is full, the line is dropped.
        public void Write(string line)
        {
            try
            {
                queue.TryAdd(line);
            }
            catch (InvalidOperationException)
            {
                // Dispose() was already called.
            }
        }

        public void Dispose()
        {
            queue.CompleteAdding();
            thread.Join(1000);
        }

        private void Run()
        {
            StreamWriter writer = null;
            try
            {
                foreach (string line in queue.GetConsumingEnumerable())
                {
                    try
                    {
                        if (writer == null)
                        {
                            writer = Open();
                        }
                        writer.WriteLine(line);
                        if (queue.Count == 0)
                        {
                            writer.Flush();
                        }
                        if (writer.BaseStream.Length >= MaxBytes)
                        {
                            writer.Dispose();
                            writer = null;
                            Rotate();
                        }
                    }
                    catch (IOException)
                    {
                        // Disk full or file busy: drop this line, reopen for the next.
                        if (writer != null)
                        {
                            writer.Dispose();
                            writer = null;
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
            finally
            {
                if (writer != null)
                {
                    writer.Dispose();
                }
            }
        }

        private StreamWriter Open()
        {
            FileStream stream = new FileStream(path, FileMode.Append, FileAccess.Write,
                                               FileShare.ReadWrite | FileShare.Delete);
            StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false));
            if (stream.Length == 0)
            {
                writer.WriteLine(DecisionLog.Header);
            }
            return writer;
        }

        private void Rotate()
        {
            string old = path + ".old";
            try
            {
                File.Delete(old);
                File.Move(path, old);
            }
            catch (IOException)
            {
                // Keep writing to the same file.
            }
        }
    }
}
