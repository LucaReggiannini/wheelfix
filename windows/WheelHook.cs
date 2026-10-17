// WheelFix - low-level mouse hook for the vertical wheel, shared by
// WheelFix.exe (which filters) and wheelfix-tools.exe (which records for
// calibrate). Windows calls the hook through the message loop of the thread
// that installed it, so that thread must run one (Application.Run) and call
// Maintain() from a timer about every 50 ms.
// Compiled with the C# compiler of the .NET Framework: C# 5 only.

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WheelFix
{
    internal sealed class WheelHook : IDisposable
    {
        // Receives each physical wheel event as a signed delta (+120 is one
        // step up). Injected events, such as the steps WheelFix replays, are
        // not passed. Returns true to block the event.
        public delegate bool Handler(int delta);

        private const int WH_MOUSE_LL = 14;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const uint LLMHF_INJECTED = 0x00000001;
        private const uint INPUT_MOUSE = 0;
        private const uint MOUSEEVENTF_WHEEL = 0x0800;

        // Windows silently removes a low-level hook whose callback misses
        // LowLevelHooksTimeout (at most 1000 ms on recent Windows), and does
        // not tell the application. The callback can only be late if this
        // thread stopped running for a while, which also delays the timer
        // that calls Maintain(). So a late call means the hook may be gone,
        // and it is reinstalled at once. It is also refreshed regularly as a
        // precaution.
        private const int PauseThresholdMs = 250;
        private const int RefreshMs = 60000;
        private const int RetryMs = 1000;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT Point;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int Dx;
            public int Dy;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public UIntPtr ExtraInfo;
        }

        // INPUT contains a tagged union; only MOUSEINPUT is used here.
        // Sequential layout gives the correct native alignment on x86 and x64.
        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint Type;
            public MOUSEINPUT Mouse;
        }

        private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int kind, HookProc callback,
                                                      IntPtr module, uint threadId);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr handle);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr handle, int code,
                                                   IntPtr message, IntPtr data);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, [In] INPUT[] inputs, int size);

        private readonly Handler handler;
        private readonly HookProc callback;
        private IntPtr hook = IntPtr.Zero;
        private long lastMaintainMs = -1;
        private long nextRefreshMs = 0;

        public WheelHook(Handler handler)
        {
            this.handler = handler;
            // The delegate stays in a field so the garbage collector keeps it.
            callback = OnMouse;
            int error = Install();
            if (error != 0)
            {
                throw new Win32Exception(error, "Could not install the mouse hook.");
            }
            // Refresh at the first Maintain() too: the message loop starts a
            // little later, and a callback that waited for it could already
            // have timed out.
            nextRefreshMs = 0;
        }

        // Returns how long this thread could not run, when the hook had to be
        // reinstalled because of that; otherwise 0.
        public long Maintain(long nowMs)
        {
            long gapMs = lastMaintainMs < 0 ? 0 : nowMs - lastMaintainMs;
            lastMaintainMs = nowMs;
            bool paused = gapMs >= PauseThresholdMs;
            if (!paused && nowMs < nextRefreshMs)
            {
                return 0;
            }
            if (Install() != 0)
            {
                // The previous hook, if Windows kept it, is still installed.
                nextRefreshMs = nowMs + RetryMs;
            }
            return paused ? gapMs : 0;
        }

        // Sends wheel steps as if they came from the mouse. Returns false if
        // Windows refused them.
        public static bool Inject(int delta)
        {
            INPUT input = new INPUT();
            input.Type = INPUT_MOUSE;
            input.Mouse.MouseData = unchecked((uint)delta);
            input.Mouse.Flags = MOUSEEVENTF_WHEEL;
            return SendInput(1, new INPUT[] { input }, Marshal.SizeOf(typeof(INPUT))) == 1;
        }

        public void Dispose()
        {
            if (hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(hook);
                hook = IntPtr.Zero;
            }
        }

        // Installs a new hook first and only then removes the previous one, so
        // the hook is never missing. No callback can run in between, because
        // callbacks are delivered by this thread's message loop.
        // Returns 0, or the Win32 error code.
        private int Install()
        {
            IntPtr newHook = SetWindowsHookEx(WH_MOUSE_LL, callback, GetModuleHandle(null), 0);
            if (newHook == IntPtr.Zero)
            {
                return Marshal.GetLastWin32Error();
            }

            IntPtr oldHook = hook;
            hook = newHook;
            if (oldHook != IntPtr.Zero)
            {
                // Fails harmlessly if Windows has already removed it.
                UnhookWindowsHookEx(oldHook);
            }
            nextRefreshMs = Clock.NowMs() + RefreshMs;
            return 0;
        }

        private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0 && message.ToInt64() == WM_MOUSEWHEEL)
            {
                try
                {
                    MSLLHOOKSTRUCT raw = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(
                        data, typeof(MSLLHOOKSTRUCT));
                    int delta = unchecked((short)(raw.MouseData >> 16));
                    if ((raw.Flags & LLMHF_INJECTED) == 0 && delta != 0 && handler(delta))
                    {
                        return new IntPtr(1);
                    }
                }
                catch (Exception)
                {
                    // Never block the wheel because of an unexpected error.
                }
            }
            return CallNextHookEx(hook, code, message, data);
        }
    }
}
