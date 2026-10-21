// Keyboard/mouse injection, focused text entry and the user-activity guard.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using Rect = System.Windows.Rect;

namespace DesktopCopilot
{
    internal static class Input
    {
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint SendInput(uint nInputs, INPUT[] inputs, int cbSize);
        [DllImport("user32.dll")]
        internal static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")]
        internal static extern uint MapVirtualKey(uint code, uint mapType);

        [StructLayout(LayoutKind.Sequential)]
        internal struct INPUT
        {
            internal int type;
            internal InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        internal struct InputUnion
        {
            [FieldOffset(0)] internal MOUSEINPUT mi;
            [FieldOffset(0)] internal KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MOUSEINPUT
        {
            internal int dx;
            internal int dy;
            internal int mouseData;
            internal uint dwFlags;
            internal uint time;
            internal IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct KEYBDINPUT
        {
            internal ushort wVk;
            internal ushort wScan;
            internal uint dwFlags;
            internal uint time;
            internal IntPtr dwExtraInfo;
        }

        internal const int INPUT_MOUSE = 0;
        internal const int INPUT_KEYBOARD = 1;
        internal const uint MOUSEEVENTF_MOVE = 0x0001;
        internal const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        internal const uint MOUSEEVENTF_LEFTUP = 0x0004;
        internal const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        internal const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        internal const uint MOUSEEVENTF_WHEEL = 0x0800;
        internal const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        internal const uint KEYEVENTF_KEYUP = 0x0002;
        internal const uint KEYEVENTF_UNICODE = 0x0004;

        internal static int LastInjectionTick;
        internal static bool Injecting;

        private static readonly Dictionary<string, ushort> Keys = BuildKeys();

        private static Dictionary<string, ushort> BuildKeys()
        {
            Dictionary<string, ushort> map = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
            for (char c = 'a'; c <= 'z'; c++) map[c.ToString()] = (ushort)(0x41 + (c - 'a'));
            for (char c = '0'; c <= '9'; c++) map[c.ToString()] = (ushort)(0x30 + (c - '0'));
            for (int i = 1; i <= 12; i++) map["f" + i] = (ushort)(0x6F + i);
            map["enter"] = 0x0D; map["return"] = 0x0D; map["tab"] = 0x09; map["esc"] = 0x1B;
            map["escape"] = 0x1B; map["space"] = 0x20; map["backspace"] = 0x08; map["delete"] = 0x2E;
            map["home"] = 0x24; map["end"] = 0x23; map["pageup"] = 0x21; map["pagedown"] = 0x22;
            map["up"] = 0x26; map["down"] = 0x28; map["left"] = 0x25; map["right"] = 0x27;
            map["ctrl"] = 0x11; map["control"] = 0x11; map["shift"] = 0x10; map["alt"] = 0x12; map["win"] = 0x5B;
            map["insert"] = 0x2D; map["capslock"] = 0x14;
            return map;
        }

        private static void Send(INPUT[] inputs)
        {
            Injecting = true;
            try { SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT))); }
            finally { Injecting = false; LastInjectionTick = Environment.TickCount; }
        }

        private static INPUT Mouse(int dx, int dy, int data, uint flags)
        {
            INPUT i = new INPUT();
            i.type = INPUT_MOUSE;
            i.U.mi.dx = dx; i.U.mi.dy = dy; i.U.mi.mouseData = data; i.U.mi.dwFlags = flags;
            return i;
        }

        private static INPUT Key(ushort vk, ushort scan, uint flags)
        {
            INPUT i = new INPUT();
            i.type = INPUT_KEYBOARD;
            i.U.ki.wVk = vk; i.U.ki.wScan = scan; i.U.ki.dwFlags = flags;
            return i;
        }

        internal static uint LastSendCount;
        internal static int LastSendError;
        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool GetTokenInformation(IntPtr token, int infoClass, out int buffer, int length, out int returned);
        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetCurrentProcess();

        internal static int IntegrityRid()
        {
            IntPtr token;
            if (!OpenProcessToken(GetCurrentProcess(), 0x0008, out token)) return -1;
            int rid;
            int returned;
            if (!GetTokenInformation(token, 25, out rid, 4, out returned)) return -2;
            return rid;
        }
        internal static void MoveTo(int x, int y)
        {
            System.Drawing.Rectangle b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            int nx = (int)Math.Round((x - b.Left) * 65535.0 / Math.Max(1, b.Width - 1));
            int ny = (int)Math.Round((y - b.Top) * 65535.0 / Math.Max(1, b.Height - 1));
            LastSendCount = SendInput(1, new INPUT[] { Mouse(nx, ny, 0, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE) }, Marshal.SizeOf(typeof(INPUT)));
            LastSendError = Marshal.GetLastWin32Error();
            Thread.Sleep(30);
        }

        internal static void Click(int x, int y, bool right, int count)
        {
            MoveTo(x, y);
            for (int i = 0; i < Math.Max(1, count); i++)
            {
                Send(new INPUT[] {
                    Mouse(0, 0, 0, right ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN),
                    Mouse(0, 0, 0, right ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_LEFTUP),
                });
                if (count > 1) Thread.Sleep(60);
            }
        }

        internal static void Wheel(int x, int y, int notches)
        {
            MoveTo(x, y);
            // One SendInput per notch: some apps (chat lists) coalesce a single large
            // wheel delta, so a big number in one event scrolls less than asked.
            int count = Math.Min(60, Math.Abs(notches));
            int direction = notches < 0 ? -1 : 1;
            for (int i = 0; i < count; i++)
            {
                Send(new INPUT[] { Mouse(0, 0, direction * 120, MOUSEEVENTF_WHEEL) });
                Thread.Sleep(12);
            }
        }

        /// <summary>Press at the start point, glide across, release at the end point.</summary>
        internal static void Drag(int fromX, int fromY, int toX, int toY)
        {
            MoveTo(fromX, fromY);
            Thread.Sleep(80);
            Send(new INPUT[] { Mouse(0, 0, 0, MOUSEEVENTF_LEFTDOWN) });
            Thread.Sleep(120);
            const int steps = 18;
            for (int i = 1; i <= steps; i++)
            {
                int x = fromX + (int)Math.Round((toX - fromX) * (double)i / steps);
                int y = fromY + (int)Math.Round((toY - fromY) * (double)i / steps);
                MoveTo(x, y);
                Thread.Sleep(25);
            }
            Thread.Sleep(120);
            Send(new INPUT[] { Mouse(0, 0, 0, MOUSEEVENTF_LEFTUP) });
        }

        internal static void TypeUnicode(string text)
        {
            foreach (char c in text)
            {
                Send(new INPUT[] {
                    Key(0, c, KEYEVENTF_UNICODE),
                    Key(0, c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP),
                });
                Thread.Sleep(8);
            }
        }

        internal static void PasteText(string text)
        {
            string backup = null;
            try { backup = System.Windows.Forms.Clipboard.GetText(); } catch { }
            try
            {
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try { System.Windows.Forms.Clipboard.SetText(text); break; }
                    catch { Thread.Sleep(60); }
                }
                Thread.Sleep(80);
                Send(new INPUT[] {
                    Key(0x11, 0, 0), Key(0x56, 0, 0), Key(0x56, 0, KEYEVENTF_KEYUP), Key(0x11, 0, KEYEVENTF_KEYUP),
                });
                Thread.Sleep(200);
            }
            finally
            {
                if (backup != null)
                {
                    try { System.Windows.Forms.Clipboard.SetText(backup); } catch { }
                }
            }
        }

        internal static void TypeText(string text)
        {
            bool ascii = true;
            foreach (char c in text) { if (c < 32 || c > 126) { ascii = false; break; } }
            if (ascii || text.Length <= 12) TypeUnicode(text);
            else PasteText(text);
        }

        internal static bool PressKeys(string combo)
        {
            string[] parts = combo.Split('+');
            List<ushort> codes = new List<ushort>();
            foreach (string raw in parts)
            {
                string k = raw.Trim();
                if (k.Length == 0) continue;
                ushort vk;
                if (!Keys.TryGetValue(k, out vk)) return false;
                codes.Add(vk);
            }
            if (codes.Count == 0) return false;
            List<INPUT> down = new List<INPUT>();
            foreach (ushort vk in codes) down.Add(Key(vk, 0, 0));
            Send(down.ToArray());
            Thread.Sleep(30);
            List<INPUT> up = new List<INPUT>();
            for (int i = codes.Count - 1; i >= 0; i--) up.Add(Key(codes[i], 0, KEYEVENTF_KEYUP));
            Send(up.ToArray());
            Thread.Sleep(30);
            return true;
        }

        private static System.Drawing.Point CenterOf(AutomationElement el)
        {
            Rect r = el.Cached.BoundingRectangle;
            return new System.Drawing.Point((int)Math.Round(r.X + r.Width / 2), (int)Math.Round(r.Y + r.Height / 2));
        }

        internal static string FocusElement(AutomationElement el)
        {
            try
            {
                object pattern;
                if (el.TryGetCurrentPattern(InvokePattern.Pattern, out pattern)) return "invoke";
            }
            catch { }
            try
            {
                el.SetFocus();
                return "focus";
            }
            catch { }
            System.Drawing.Point p = CenterOf(el);
            Click(p.X, p.Y, false, 1);
            return "click";
        }

        internal static void InvokeElement(AutomationElement el)
        {
            object pattern;
            if (el.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
            {
                ((InvokePattern)pattern).Invoke();
                return;
            }
            System.Drawing.Point p = CenterOf(el);
            Click(p.X, p.Y, false, 1);
        }

        /// <summary>True when a human used the keyboard or mouse after the given tick.</summary>
        internal static bool UserActiveSince(int sinceTick, int idleGraceMs)
        {
            if (Environment.TickCount - sinceTick < idleGraceMs) return false;
            Native.LASTINPUTINFO info = new Native.LASTINPUTINFO();
            info.cbSize = (uint)Marshal.SizeOf(typeof(Native.LASTINPUTINFO));
            if (Native.GetLastInputInfo(ref info) == 0) return false;
            int last = (int)info.dwTime;
            if (last <= sinceTick) return false;
            // Input we injected ourselves must not count as the human taking over.
            if (Math.Abs(last - LastInjectionTick) <= 400) return false;
            return Environment.TickCount - last < 1200;
        }
    }
}
