// UI Automation snapshot: the element tree the model picks targets from.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using Rect = System.Windows.Rect;

namespace DesktopCopilot
{
    internal sealed class UiaSnapshot
    {
        internal IntPtr Window;
        internal string Title = "";
        internal string Process = "";
        internal int ProcessId;
        internal int X, Y, W, H;
        internal int Total;
        internal readonly Dictionary<int, AutomationElement> Map = new Dictionary<int, AutomationElement>();
        internal readonly List<Dictionary<string, object>> Elements = new List<Dictionary<string, object>>();
    }

    internal static class Uia
    {
        private static readonly HashSet<string> Interactive = new HashSet<string>(StringComparer.Ordinal)
        {
            "Button", "Edit", "ComboBox", "CheckBox", "RadioButton", "ListItem", "MenuItem",
            "TabItem", "Hyperlink", "TreeItem", "Slider", "Spinner", "DataItem", "SplitButton",
            "Text", "Document",
        };

        internal static UiaSnapshot Snapshot(IntPtr hwnd)
        {
            UiaSnapshot snap = new UiaSnapshot();
            snap.Window = hwnd;
            Native.RECT wr;
            Native.GetWindowRect(hwnd, out wr);
            snap.X = wr.Left; snap.Y = wr.Top; snap.W = wr.Right - wr.Left; snap.H = wr.Bottom - wr.Top;

            StringBuilder sb = new StringBuilder(512);
            Native.GetWindowText(hwnd, sb, sb.Capacity);
            snap.Title = sb.ToString();
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            snap.ProcessId = (int)pid;
            try { snap.Process = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { snap.Process = ""; }

            AutomationElement root = AutomationElement.FromHandle(hwnd);
            if (root == null) return snap;

            List<AutomationElement> all = new List<AutomationElement>();
            WalkRaw(root, 0, all, 700);
            // Chromium builds its accessibility tree lazily: the first walk can return a
            // single "Chrome Legacy Window" stub. Retry once and keep the larger tree.
            if (all.Count < 5)
            {
                System.Threading.Thread.Sleep(250);
                List<AutomationElement> retry = new List<AutomationElement>();
                WalkRaw(root, 0, retry, 700);
                if (retry.Count > all.Count) all = retry;
            }
            snap.Total = all.Count;

            List<object[]> collected = new List<object[]>();
            foreach (AutomationElement el in all)
            {
                try
                {
                    string ct = "Unknown";
                    ControlType type = el.Current.ControlType;
                    if (type != null) ct = type.ProgrammaticName.Replace("ControlType.", "");
                    string name = el.Current.Name ?? "";
                    Rect rect = el.Current.BoundingRectangle;
                    bool offscreen = el.Current.IsOffscreen;
                    if (offscreen || rect.IsEmpty || rect.Width < 2 || rect.Height < 2) continue;
                    if (rect.Right < snap.X || rect.Bottom < snap.Y || rect.Left > snap.X + snap.W || rect.Top > snap.Y + snap.H) continue;
                    bool named = name.Trim().Length > 0;
                    if (!Interactive.Contains(ct) && !named) continue;
                    if (ct == "Text" && !named) continue;
                    if (name.Length > 80) name = name.Substring(0, 80);

                    Dictionary<string, object> item = new Dictionary<string, object>();
                    item["ct"] = ct;
                    item["name"] = name;
                    item["x"] = (int)Math.Round(rect.X);
                    item["y"] = (int)Math.Round(rect.Y);
                    item["w"] = (int)Math.Round(rect.Width);
                    item["h"] = (int)Math.Round(rect.Height);
                    item["enabled"] = el.Current.IsEnabled;
                    if (ct == "Edit" || ct == "ComboBox")
                    {
                        try
                        {
                            bool password = el.Current.IsPassword;
                            if (!password)
                            {
                                object value = el.GetCurrentPropertyValue(ValuePattern.ValueProperty);
                                string text = value as string;
                                if (!string.IsNullOrEmpty(text))
                                {
                                    if (text.Length > 80) text = text.Substring(0, 80);
                                    item["value"] = text;
                                }
                            }
                            else item["password"] = true;
                        }
                        catch { }
                    }
                    collected.Add(new object[] { (int)Math.Round(rect.Top * 10000 + rect.Left), el, item });
                }
                catch { }
            }

            collected.Sort(delegate(object[] a, object[] b) { return ((int)a[0]).CompareTo((int)b[0]); });
            int n = 1;
            foreach (object[] pair in collected)
            {
                if (n > 150) break;
                Dictionary<string, object> item = (Dictionary<string, object>)pair[2];
                item["id"] = n;
                snap.Map[n] = (AutomationElement)pair[1];
                snap.Elements.Add(item);
                n++;
            }
            return snap;
        }

        internal static void WalkForDebug(AutomationElement parent, int depth, List<AutomationElement> found, int limit) { WalkRaw(parent, depth, found, limit); }

        private static void WalkRaw(AutomationElement parent, int depth, List<AutomationElement> found, int limit)
        {
            if (depth > 18 || found.Count >= limit) return;
            TreeWalker walker = TreeWalker.RawViewWalker;
            AutomationElement child;
            try { child = walker.GetFirstChild(parent); }
            catch { child = null; }
            while (child != null && found.Count < limit)
            {
                found.Add(child);
                WalkRaw(child, depth + 1, found, limit);
                try { child = walker.GetNextSibling(child); }
                catch { return; }
            }
        }



        internal static AutomationElement Get(UiaSnapshot snap, int id)
        {
            AutomationElement el;
            if (snap != null && snap.Map.TryGetValue(id, out el)) return el;
            return null;
        }

        internal static string Describe(UiaSnapshot snap)
        {
            if (snap == null) return "no snapshot";
            return snap.Title + " (" + snap.Process + ") " + snap.X + "," + snap.Y + " " + snap.W + "x" + snap.H + ", " + snap.Elements.Count + " elements";
        }
    }
}
