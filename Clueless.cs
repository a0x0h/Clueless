// Clueless - an invisible meeting / interview copilot. It turns Windows 11 Live Captions
// (any app or site), your own mic (offline Whisper) and Chrome meeting captions (Meet /
// Teams / Zoom, with real names) into a transcript you can send to the Claude desktop app,
// whose replies are read back into this window. The UI is ui/index.html hosted in WebView2;
// this file reads captions, owns the borderless window (stealth, hotkeys) and drives the
// Claude app. Build with build.cmd.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Clueless
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            // One copy only: two copies would fight over the WebView2 profile folder. Starting
            // it again just brings the running copy back into view.
            bool first;
            using (var mutex = new Mutex(true, "Clueless.Instance", out first))
            using (var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Clueless.Show"))
            {
                if (!first) { showSignal.Set(); return; }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(showSignal));
            }
        }
    }

    // ---------------------------------------------------------------- audio meters

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }

    // Samples the speaker output (other people) and the mic (you) so caption text can be
    // attributed to "Me" or "Others" at the moment it shows up.
    class AudioMeters
    {
        const int Window = 40; // 40 x 50ms = last 2 seconds
        readonly float[] mic = new float[Window], spk = new float[Window];
        readonly object gate = new object();
        int n;

        public void Start()
        {
            var t = new Thread(Loop) { IsBackground = true, Name = "meters" };
            t.Start();
        }

        void Loop()
        {
            IAudioMeterInformation micMeter = null, spkMeter = null;
            DateTime refresh = DateTime.MinValue;
            while (true)
            {
                if (DateTime.Now >= refresh)
                {
                    // re-open every few seconds so switching headset/speakers is picked up
                    micMeter = Open(1); spkMeter = Open(0);
                    refresh = DateTime.Now.AddSeconds(5);
                }
                float m = Peak(micMeter), s = Peak(spkMeter);
                lock (gate) { mic[n % Window] = m; spk[n % Window] = s; n++; }
                Thread.Sleep(50);
            }
        }

        static IAudioMeterInformation Open(int dataFlow)
        {
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                IMMDevice dev;
                if (en.GetDefaultAudioEndpoint(dataFlow, 0, out dev) != 0 || dev == null) return null;
                Guid iid = typeof(IAudioMeterInformation).GUID;
                object o;
                if (dev.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out o) != 0) return null;
                return (IAudioMeterInformation)o;
            }
            catch (Exception) { return null; }
        }

        static float Peak(IAudioMeterInformation m)
        {
            if (m == null) return 0;
            try { float p; return m.GetPeakValue(out p) == 0 ? p : 0; }
            catch (Exception) { return 0; }
        }

        // Whether the speakers are playing right now (last ~200 ms).
        public bool SpeakersPlaying()
        {
            lock (gate)
            {
                for (int i = 1; i <= 4; i++) if (spk[((n - i) % Window + Window) % Window] > 0.02f) return true;
                return false;
            }
        }

        // Fraction of the last 2s in which each side was audible.
        public void Activity(out double micFrac, out double spkFrac)
        {
            int mc = 0, sc = 0;
            lock (gate)
            {
                for (int i = 0; i < Window; i++)
                {
                    if (mic[i] > 0.03f) mc++;
                    if (spk[i] > 0.02f) sc++;
                }
            }
            micFrac = mc / (double)Window;
            spkFrac = sc / (double)Window;
        }
    }

    // ---------------------------------------------------------------- Live Captions transcript

    // Live Captions only shows a rolling window of a few lines and keeps revising the last
    // words. Each snapshot is anchored onto the transcript where it overlaps. Every char
    // carries who was audible when it appeared, and when.
    class LcTranscript
    {
        public const byte Me = 1, Others = 2, Hidden = 3;

        public string Full = "";
        public readonly List<byte> Label = new List<byte>();
        public readonly List<long> Time = new List<long>();
        public DateTime LastChange = DateTime.MinValue;
        int prevStart;

        public bool Merge(string snap, byte label, long now)
        {
            snap = Normalize(snap);
            if (snap.Length == 0) return false;
            string before = Full;

            int pos;
            if (Full.Length == 0) pos = 0;
            else
            {
                pos = FindStart(snap);
                if (pos < 0)
                {
                    // The first line may have been revised; try anchoring on the second line.
                    int nl = snap.IndexOf('\n');
                    if (nl > 0 && nl + 1 < snap.Length)
                    {
                        string rest = snap.Substring(nl + 1);
                        int p2 = FindStart(rest);
                        if (p2 >= 0) { pos = p2; snap = rest; }
                    }
                }
                if (pos < 0)
                {
                    // No overlap (captions were reset, or we fell behind): new paragraph.
                    string trimmed = Full.TrimEnd();
                    Resize(trimmed.Length, label, now);
                    Full = trimmed + "\n";
                    Resize(Full.Length, label, now);
                    pos = Full.Length;
                }
            }
            Full = Full.Substring(0, pos) + snap;
            Resize(Full.Length, label, now);
            prevStart = pos;
            if (Full == before) return false;
            LastChange = DateTime.Now;
            return true;
        }

        // Chars that already existed keep their label/time; new chars get the current one.
        void Resize(int len, byte label, long now)
        {
            if (Label.Count > len) { Label.RemoveRange(len, Label.Count - len); Time.RemoveRange(len, Time.Count - len); }
            while (Label.Count < len) { Label.Add(label); Time.Add(now); }
        }

        int FindStart(string snap)
        {
            int avail = Full.Length - prevStart;
            int[] lens = { 48, 24, 12, 6 };
            foreach (int l in lens)
            {
                int len = Math.Min(l, Math.Min(snap.Length, avail));
                if (len <= 0) return -1;
                int idx = Full.IndexOf(snap.Substring(0, len), prevStart, StringComparison.Ordinal);
                if (idx >= 0) return idx;
            }
            return -1;
        }

        static string Normalize(string s)
        {
            if (s == null) return "";
            s = s.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            if (s.StartsWith("Ready to caption", StringComparison.OrdinalIgnoreCase)) return "";
            return s;
        }

        public byte LastLabel()
        {
            for (int i = Label.Count - 1; i >= 0; i--) if (Label[i] != Hidden) return Label[i];
            return Others;
        }
    }

    // ---------------------------------------------------------------- sentences

    class Row
    {
        public string Key, Speaker, Text;
        public long Time;
        public int Sub; // sentence index within one meeting caption block
        public bool Final, Sent, ShowSpeaker;
    }

    // Holds both caption sources and turns them into one ordered list of sentences.
    class Store
    {
        class Utterance { public string Speaker, Text; public long Time; public DateTime Updated; }

        public readonly object Gate = new object();
        LcTranscript lc = new LcTranscript();
        bool hideNextSnapshot;
        readonly Dictionary<string, Utterance> utts = new Dictionary<string, Utterance>();
        readonly Dictionary<string, string> renames = new Dictionary<string, string>();
        readonly Dictionary<string, string> overrides = new Dictionary<string, string>();
        readonly HashSet<string> sent = new HashSet<string>();
        long lastUttTime;
        long clearTime;
        DateTime meetingSeen = DateTime.MinValue;
        string meetingPlatform = "";
        public int Version;
        public DateTime LastChange = DateTime.MinValue;

        public bool MeetingActive { get { lock (Gate) return (DateTime.Now - meetingSeen).TotalSeconds < 6; } }
        public string MeetingPlatform { get { lock (Gate) return meetingPlatform; } }

        public void MergeLiveCaptions(string snap, byte label)
        {
            lock (Gate)
            {
                // whatever Live Captions already shows when a session starts belongs to before it
                if (hideNextSnapshot) { label = LcTranscript.Hidden; hideNextSnapshot = false; }
                if (lc.Merge(snap, label, DateTime.UtcNow.Ticks)) Changed();
            }
        }

        // Start of a new session: forget everything.
        public void Reset()
        {
            lock (Gate)
            {
                lc = new LcTranscript();
                hideNextSnapshot = true;
                utts.Clear(); renames.Clear(); overrides.Clear(); sent.Clear();
                clearTime = 0;
                meetingSeen = DateTime.MinValue;
                Changed();
            }
        }

        public byte LastLcLabel() { lock (Gate) return lc.LastLabel(); }

        public void MeetingHeartbeat(string platform)
        {
            lock (Gate) { meetingSeen = DateTime.Now; meetingPlatform = platform; }
        }

        public void RemoveUtterance(string key)
        {
            lock (Gate) { if (utts.Remove(key)) Changed(); }
        }

        // What Live Captions (system audio) heard in the last few seconds; used to spot the
        // mic picking up the speakers.
        public string RecentSystemText(double seconds)
        {
            lock (Gate)
            {
                long since = DateTime.UtcNow.AddSeconds(-seconds).Ticks;
                int i = lc.Time.Count;
                while (i > 0 && lc.Time[i - 1] >= since) i--;
                return lc.Full.Substring(Math.Min(i, lc.Full.Length));
            }
        }

        public void Upsert(string key, string speaker, string text)
        {
            text = (text ?? "").Trim();
            speaker = string.IsNullOrEmpty(speaker) ? "Speaker" : speaker.Trim();
            if (text.Length == 0) return;
            lock (Gate)
            {
                Utterance u;
                if (!utts.TryGetValue(key, out u))
                {
                    // strictly increasing, so blocks that arrive in one batch keep their order
                    lastUttTime = Math.Max(DateTime.UtcNow.Ticks, lastUttTime + 1);
                    u = new Utterance { Time = lastUttTime };
                    utts[key] = u;
                }
                else if (u.Text == text && u.Speaker == speaker) return;
                u.Text = text; u.Speaker = speaker; u.Updated = DateTime.Now;
                Changed();
            }
        }

        void Changed() { Version++; LastChange = DateTime.Now; }

        public void Clear() { lock (Gate) { clearTime = DateTime.UtcNow.Ticks; Changed(); } }
        public void MarkSent(IEnumerable<Row> rows) { lock (Gate) { foreach (var r in rows) sent.Add(r.Key); Changed(); } }
        public void Rename(string from, string to) { lock (Gate) { renames[from] = to; Changed(); } }
        public void Override(string key, string speaker) { lock (Gate) { overrides[key] = speaker; Changed(); } }

        public List<Row> BuildRows()
        {
            var rows = new List<Row>();
            lock (Gate)
            {
                bool lcLive = (DateTime.Now - lc.LastChange).TotalSeconds < 1.5;
                foreach (var span in Sentences(lc.Full)) AddLiveCaptionRows(rows, span[0], span[1], lcLive);

                foreach (var kv in utts)
                {
                    var u = kv.Value;
                    bool live = (DateTime.Now - u.Updated).TotalSeconds < 1.5;
                    var spans = Sentences(u.Text);
                    for (int i = 0; i < spans.Count; i++)
                    {
                        string t = u.Text.Substring(spans[i][0], spans[i][1] - spans[i][0]).Trim();
                        if (t.Length == 0) continue;
                        rows.Add(new Row
                        {
                            Key = kv.Key + ":" + i, Speaker = u.Speaker, Text = t, Time = u.Time, Sub = i,
                            Final = !(live && i == spans.Count - 1)
                        });
                    }
                }

                foreach (var r in rows)
                {
                    string s;
                    if (overrides.TryGetValue(r.Key, out s)) r.Speaker = s;
                    else if (renames.TryGetValue(r.Speaker, out s)) r.Speaker = s;
                    r.Sent = sent.Contains(r.Key);
                }
                rows = rows.Where(r => r.Time >= clearTime).OrderBy(r => r.Time).ThenBy(r => r.Sub).ToList();
            }
            for (int i = 0; i < rows.Count; i++) rows[i].ShowSpeaker = i == 0 || rows[i].Speaker != rows[i - 1].Speaker;
            return rows;
        }

        // A sentence that changes speaker partway (no punctuation between them) is split
        // where the speaker changed; blips shorter than a few words are ignored.
        void AddLiveCaptionRows(List<Row> rows, int start, int end, bool lcLive)
        {
            var runs = new List<int[]>(); // label, start, end
            for (int k = start; k < end; k++)
            {
                int lab = lc.Label[k];
                if (runs.Count > 0 && runs[runs.Count - 1][0] == lab) runs[runs.Count - 1][2] = k + 1;
                else runs.Add(new[] { lab, k, k + 1 });
            }
            for (int i = 0; i < runs.Count && runs.Count > 1; )
            {
                if (runs[i][2] - runs[i][1] >= 15) { i++; continue; }
                if (i > 0) runs[i - 1][2] = runs[i][2];
                else runs[i + 1][1] = runs[i][1];
                runs.RemoveAt(i);
                for (int j = 1; j < runs.Count; j++)
                    if (runs[j][0] == runs[j - 1][0]) { runs[j - 1][2] = runs[j][2]; runs.RemoveAt(j); j--; }
                i = 0;
            }
            foreach (var run in runs)
            {
                if (run[0] == LcTranscript.Hidden) continue;
                string t = lc.Full.Substring(run[1], run[2] - run[1]).Trim();
                if (t.Length == 0) continue;
                rows.Add(new Row
                {
                    Key = "lc:" + run[1], Speaker = run[0] == LcTranscript.Me ? "Me" : "Others", Text = t,
                    Time = lc.Time[run[1]], Final = !(lcLive && run[2] >= lc.Full.Length)
                });
            }
        }

        static List<int[]> Sentences(string s)
        {
            var spans = new List<int[]>();
            int i = 0;
            while (i < s.Length)
            {
                int j = i;
                while (j < s.Length)
                {
                    char c = s[j++];
                    if (c == '\n') break;
                    if ((c == '.' || c == '?' || c == '!') && (j == s.Length || s[j] == ' ' || s[j] == '\n')) break;
                }
                spans.Add(new[] { i, j });
                i = j;
            }
            return spans;
        }
    }

    // ---------------------------------------------------------------- Claude desktop app

    // Types into the Claude desktop app and reads Claude's reply back through UI Automation.
    // Sends go into the session that's open in the app (a chat or a Claude Code session);
    // if nothing with a prompt box is open, a new chat is started via claude://.
    class ClaudeApp
    {
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

        public class Job
        {
            public int Id;
            public string Text, Intro;
            public bool ReturnFocus;
            public bool IncludeScreen; // attach a screenshot of the screen the user is working on
            public bool NewChat;       // always start a fresh chat
            public bool Warmup;        // the priming message (profile, CV, instructions) of a session
            public string SkipIfChat;  // warm-up only: the open chat that already has this profile
        }

        readonly System.Collections.Concurrent.BlockingCollection<Job> queue = new System.Collections.Concurrent.BlockingCollection<Job>();
        volatile int watchingId;
        public event Action<Dictionary<string, object>> Report;

        public ClaudeApp()
        {
            var t = new Thread(Run) { IsBackground = true, Name = "claude-app" };
            t.SetApartmentState(ApartmentState.STA); // clipboard needs STA
            t.Start();
        }

        public void Send(Job job)
        {
            watchingId = 0; // a new send stops watching the previous reply
            queue.Add(job);
        }

        void Say(int id, string state, string text, string error, string chat = null)
        {
            var h = Report;
            if (h == null) return;
            var m = new Dictionary<string, object> { { "type", "claude" }, { "id", id }, { "state", state } };
            if (text != null) m["text"] = text;
            if (error != null) m["error"] = error;
            if (chat != null) m["chat"] = chat;
            h(m);
        }

        static string ChatName(AutomationElement doc)
        {
            try { return doc.Current.Name ?? ""; } catch (Exception) { return ""; }
        }

        void Run()
        {
            foreach (var job in queue.GetConsumingEnumerable())
            {
                try { DoSend(job); }
                catch (Exception ex) { Say(job.Id, "error", null, "Couldn't send to the Claude app: " + ex.Message); }
            }
        }

        // The Claude window and, if it's showing a session (a chat or a Claude Code session),
        // that session's page and prompt box. Chromium only builds its accessibility tree
        // once something asks for it, so the first look can come back empty: keep asking.
        static bool FindSession(out IntPtr hwnd, out AutomationElement doc, out AutomationElement edit, int waitMs, bool freshOnly = false)
        {
            hwnd = IntPtr.Zero; doc = null; edit = null;
            DateTime until = DateTime.Now.AddMilliseconds(waitMs);
            do
            {
                hwnd = ClaudeWindow();
                if (hwnd != IntPtr.Zero)
                {
                    var win = AutomationElement.FromHandle(hwnd);
                    foreach (AutomationElement d in win.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document)))
                    {
                        // right after asking for a new chat the old page is still there for a moment
                        if (freshOnly && !(d.Current.Name ?? "").StartsWith("New ")) continue;
                        // the prompt box is a ProseMirror editor ("Write your prompt to Claude" in chats,
                        // "Prompt" in Claude Code); the Code terminal's input is a different element
                        foreach (AutomationElement e in d.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)))
                        {
                            if ((e.Current.ClassName ?? "").Contains("ProseMirror")) { doc = d; edit = e; return true; }
                        }
                    }
                }
                Thread.Sleep(250);
            } while (DateTime.Now < until);
            return false;
        }

        // The Claude window, also when it was closed to the tray: then it's shown again (a
        // hidden window has no page to type into), instead of opening a new chat.
        static IntPtr ClaudeWindow()
        {
            var pids = new HashSet<uint>();
            foreach (var p in Process.GetProcessesByName("claude")) { pids.Add((uint)p.Id); p.Dispose(); }
            if (pids.Count == 0) return IntPtr.Zero;
            IntPtr visible = IntPtr.Zero, hidden = IntPtr.Zero;
            var sb = new StringBuilder(256);
            EnumWindows((h, l) =>
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (!pids.Contains(pid)) return true;
                sb.Clear(); GetClassName(h, sb, sb.Capacity);
                if (sb.ToString() != "Chrome_WidgetWin_1") return true;
                sb.Clear(); GetWindowText(h, sb, sb.Capacity);
                if (!sb.ToString().StartsWith("Claude")) return true;
                if (IsWindowVisible(h)) { visible = h; return false; }
                RECT r; // helper windows are tiny; the app window isn't
                if (hidden == IntPtr.Zero && GetWindowRect(h, out r) && r.Right - r.Left > 400 && r.Bottom - r.Top > 300) hidden = h;
                return true;
            }, IntPtr.Zero);
            if (visible != IntPtr.Zero) return visible;
            if (hidden != IntPtr.Zero) ShowWindow(hidden, 4 /* SW_SHOWNOACTIVATE */);
            return hidden;
        }

        static bool IsStreaming(AutomationElement doc)
        {
            try
            {
                return doc.FindFirst(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new OrCondition(new PropertyCondition(AutomationElement.NameProperty, "Stop"),
                                    new PropertyCondition(AutomationElement.NameProperty, "Stop response")))) != null;
            }
            catch (Exception) { return false; }
        }

        void DoSend(Job job)
        {
            Say(job.Id, "sending", null, null);
            IntPtr prev = GetForegroundWindow();
            // screenshot first, while the user's own window is still in front (the overlay
            // excludes itself from capture, so it never shows up in it)
            Bitmap shot = null;
            if (job.IncludeScreen) { try { shot = CaptureScreen(prev); } catch (Exception) { } }

            IntPtr hwnd = IntPtr.Zero; AutomationElement doc = null, edit = null;
            string text = job.Text;
            // the chat that's open in the app is used as is; a new one only when asked for, or
            // when nothing with a prompt box is open
            if (job.NewChat || !FindSession(out hwnd, out doc, out edit, 4000))
            {
                Process.Start("claude://claude.ai/new");
                Thread.Sleep(600);
                if (!FindSession(out hwnd, out doc, out edit, 12000, true))
                {
                    SetClipboard(text);
                    Say(job.Id, "error", null, "Couldn't open a chat in the Claude app. The text is on your clipboard.");
                    return;
                }
                // a fresh chat knows nothing yet: lead with the profile
                if (!job.Warmup && !string.IsNullOrEmpty(job.Intro)) text = job.Intro + "\n\n" + text;
            }
            else if (job.Warmup && !string.IsNullOrEmpty(job.SkipIfChat) && ChatName(doc) == job.SkipIfChat)
            {
                Say(job.Id, "skipped", null, null, job.SkipIfChat); // this chat already has the profile
                return;
            }

            // typing while the previous reply is still streaming would be lost (chats) or queued
            for (int i = 0; i < 300 && IsStreaming(doc); i++) Thread.Sleep(200);

            int before = LastMessageNumber(doc);
            string oldClip = null;
            try { if (Clipboard.ContainsText()) oldClip = Clipboard.GetText(); } catch (Exception) { }

            if (IsIconic(hwnd)) ShowWindow(hwnd, 9 /* SW_RESTORE */);
            ForceForeground(hwnd);
            try { edit.SetFocus(); } catch (Exception) { }
            Thread.Sleep(150);
            // Never type unless the Claude prompt box really has focus, or the keys would go
            // to whatever app is in front (a meeting chat, a document…).
            bool focused = false;
            for (int i = 0; i < 10 && !focused; i++)
            {
                try { focused = GetForegroundWindow() == hwnd && edit.Current.HasKeyboardFocus; } catch (Exception) { }
                if (!focused) { ForceForeground(hwnd); try { edit.SetFocus(); } catch (Exception) { } Thread.Sleep(100); }
            }
            if (!focused)
            {
                SetClipboard(text);
                Say(job.Id, "error", null, "Couldn't focus the Claude app. The text is on your clipboard.");
                return;
            }
            var composer = Ancestor(edit, 5); // the prompt box with its attachments and send button
            if (shot != null)
            {
                int attachments = CountAttachments(composer);
                if (SetClipboardImage(shot))
                {
                    Key(0x11, 0x56); // Ctrl+V the screenshot
                    for (int i = 0; i < 30 && CountAttachments(composer) <= attachments; i++) Thread.Sleep(100);
                }
                shot.Dispose();
            }
            if (!SetClipboard(text)) { Say(job.Id, "error", null, "Couldn't use the clipboard. Try again."); return; }
            Key(0x11, 0x56); // Ctrl+V the text
            Thread.Sleep(150);
            WaitUntilSendable(composer, job.IncludeScreen ? 10000 : 1500); // an image may still be uploading
            Key(0, 0x0D);    // Enter
            Thread.Sleep(250);
            if (job.ReturnFocus && prev != IntPtr.Zero && prev != hwnd) ForceForeground(prev);
            if (oldClip != null) { Thread.Sleep(300); SetClipboard(oldClip); }

            Say(job.Id, "waiting", null, null);
            WatchReply(job.Id, doc, before, text, job.Warmup);
        }

        // Polls the session until Claude's reply stops streaming. While it streams, only the
        // answer's own text is shown (thinking and tool steps are left out); once it's done,
        // the reply is taken from Claude's Copy button, as clean Markdown.
        void WatchReply(int id, AutomationElement doc, int before, string sent, bool warmup)
        {
            watchingId = id;
            string last = null;
            AutomationElement message = null;
            DateTime start = DateTime.Now, lastChange = DateTime.Now;
            string sentStart = Norm(sent).Substring(0, Math.Min(40, Norm(sent).Length));
            while (watchingId == id && (DateTime.Now - start).TotalMinutes < 10)
            {
                Thread.Sleep(400);
                // a newer send is waiting (it waits for this reply to finish before typing);
                // the warm-up is always followed to the end so its chat can be remembered
                if (queue.Count > 0 && !warmup) return;
                string text; bool streaming; AutomationElement target;
                try { if (!ReadLastReply(doc, before, sentStart, out text, out streaming, out target)) continue; }
                catch (Exception) { continue; }
                message = target;
                if (text != last) { last = text; lastChange = DateTime.Now; Say(id, "streaming", text, null); }
                // finished = nothing streaming and the text has settled (Code sessions pause between tool calls)
                if (!streaming && (DateTime.Now - lastChange).TotalSeconds > 1.5) break;
            }
            if (last == null) return;
            string clean = null;
            if (message != null) { try { clean = CopyAsMarkdown(message); } catch (Exception) { } }
            Say(id, "done", string.IsNullOrWhiteSpace(clean) ? last : clean.Replace("\r", "").Trim(), null, ChatName(doc));
        }

        // The message's own Copy button gives the reply exactly as Claude wrote it (Markdown),
        // without the thinking and tool steps. The user's clipboard text is put back after.
        static string CopyAsMarkdown(AutomationElement message)
        {
            AutomationElement copy = null; // code blocks have Copy buttons too; the message's own comes last
            foreach (AutomationElement b in message.FindAll(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, "Copy")))) copy = b;
            object pattern;
            if (copy == null || !copy.TryGetCurrentPattern(InvokePattern.Pattern, out pattern)) return null;
            string old = null;
            try { if (Clipboard.ContainsText()) old = Clipboard.GetText(); } catch (Exception) { }
            const string marker = "⁣clueless⁣";
            if (!SetClipboard(marker)) return null;
            string got = null;
            try
            {
                ((InvokePattern)pattern).Invoke();
                for (int i = 0; i < 20 && got == null; i++)
                {
                    Thread.Sleep(100);
                    try { string t = Clipboard.ContainsText() ? Clipboard.GetText() : null; if (t != null && t != marker) got = t; }
                    catch (Exception) { }
                }
            }
            finally
            {
                if (old != null) SetClipboard(old);
                else try { Clipboard.Clear(); } catch (Exception) { }
            }
            return got;
        }

        static string Norm(string s) { return System.Text.RegularExpressions.Regex.Replace(s ?? "", @"\s+", " ").Trim(); }

        static AutomationElement ChatList(AutomationElement doc)
        {
            return doc.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group),
                new PropertyCondition(AutomationElement.NameProperty, "Chat messages")));
        }

        // Messages are named "Message 7" (Code) or "Message 7 of 9" (chats); long sessions only
        // keep the latest ones in the tree, so use the highest number rather than a count.
        static int MessageNumber(string name)
        {
            var m = System.Text.RegularExpressions.Regex.Match(name ?? "", @"^Message (\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : -1;
        }

        static int LastMessageNumber(AutomationElement doc)
        {
            var list = ChatList(doc);
            if (list == null) return 0;
            int max = 0;
            foreach (AutomationElement c in list.FindAll(TreeScope.Children, Condition.TrueCondition))
                max = Math.Max(max, MessageNumber(c.Current.Name));
            return max;
        }

        static readonly string[] ToolbarLabels = { "Read aloud", "Retry", "Good response", "Fork from here", "Rewind to here", "Pin as chapter" };

        static bool ReadLastReply(AutomationElement doc, int before, string sentStart, out string text, out bool streaming, out AutomationElement target)
        {
            text = null; streaming = false; target = null;
            var list = ChatList(doc);
            if (list == null) return false;
            AutomationElement live = null;
            int max = before;
            foreach (AutomationElement c in list.FindAll(TreeScope.Children, Condition.TrueCondition))
            {
                string name = c.Current.Name ?? "";
                if (name == "Currently streaming message") live = c;
                int n = MessageNumber(name);
                if (n > max) { max = n; target = c; }
            }
            streaming = live != null || IsStreaming(doc);
            if (live != null) target = live;
            if (target == null) return false;

            var tp = (TextPattern)doc.GetCurrentPattern(TextPattern.Pattern);
            string raw = WithoutSteps(tp, target, tp.RangeFromChild(target).GetText(-1).Replace("￼", "").Replace("\r", ""));
            var lines = raw.Split('\n').ToList();
            // chats start with a screen-reader summary line
            if (lines.Count > 0 && lines[0].StartsWith("You said:")) return false;
            if (lines.Count > 0 && lines[0].StartsWith("Claude responded:")) lines.RemoveAt(0);
            // the message toolbar (Copy, Read aloud, …) comes after the content
            int copy = lines.LastIndexOf("Copy");
            if (copy >= 0 && lines.Skip(copy).Any(l => ToolbarLabels.Contains(l))) lines = lines.Take(copy).ToList();
            text = System.Text.RegularExpressions.Regex.Replace(string.Join("\n", lines), @"\n{3,}", "\n\n").Trim();
            if (sentStart.Length > 0 && Norm(text).StartsWith(sentStart)) return false; // that's our own message
            return true; // empty while Claude is still thinking: the panel shows it's working
        }

        // Thinking and tool steps are collapsible rows ("Thought process", "Ran 2 commands",
        // "Searched the web") between the answer's paragraphs; their text and the
        // screen-reader status lines are cut out so only the answer itself is left.
        static string WithoutSteps(TextPattern tp, AutomationElement message, string raw)
        {
            var steps = new List<string>();
            try
            {
                foreach (AutomationElement e in message.FindAll(TreeScope.Descendants, new OrCondition(
                    new PropertyCondition(AutomationElement.IsExpandCollapsePatternAvailableProperty, true),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.StatusBar))))
                {
                    var row = e.Current.ControlType == ControlType.StatusBar ? e : (TreeWalker.ControlViewWalker.GetParent(e) ?? e);
                    if (row == message) row = e;
                    try { steps.Add(tp.RangeFromChild(row).GetText(-1).Replace("￼", "").Replace("\r", "").Trim()); } catch (Exception) { }
                }
            }
            catch (Exception) { }
            // step rows are short headlines; anything long is a container that holds the answer too
            foreach (var s in steps.Where(s => s.Length > 0 && s.Length <= 300).OrderByDescending(s => s.Length))
            {
                int i = raw.IndexOf(s, StringComparison.Ordinal);
                if (i >= 0) raw = raw.Remove(i, s.Length);
            }
            return raw;
        }

        // The screen the user is working on: the one with their foreground window, or under the
        // mouse. Scaled down to 1920px wide at most so it uploads fast.
        static Bitmap CaptureScreen(IntPtr foreground)
        {
            Screen screen = foreground != IntPtr.Zero ? Screen.FromHandle(foreground) : Screen.FromPoint(Cursor.Position);
            Rectangle b = screen.Bounds;
            var full = new Bitmap(b.Width, b.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(full)) g.CopyFromScreen(b.Location, Point.Empty, b.Size);
            if (b.Width <= 1920) return full;
            int h = b.Height * 1920 / b.Width;
            var small = new Bitmap(1920, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(full, 0, 0, 1920, h);
            }
            full.Dispose();
            return small;
        }

        // Offer the image both as a bitmap and as PNG; Chromium-based apps prefer PNG.
        static bool SetClipboardImage(Bitmap bmp)
        {
            var png = new MemoryStream();
            bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            var data = new DataObject();
            data.SetImage(bmp);
            data.SetData("PNG", false, png);
            for (int i = 0; i < 8; i++)
            {
                try { Clipboard.SetDataObject(data, true); return true; }
                catch (ExternalException) { Thread.Sleep(60); }
            }
            return false;
        }

        static AutomationElement Ancestor(AutomationElement e, int levels)
        {
            var walker = TreeWalker.ControlViewWalker;
            for (int i = 0; i < levels; i++)
            {
                var p = walker.GetParent(e);
                if (p == null) break;
                e = p;
            }
            return e;
        }

        static int CountAttachments(AutomationElement composer)
        {
            int n = 0;
            try
            {
                foreach (AutomationElement e in composer.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                {
                    string name = (e.Current.Name ?? "").ToLowerInvariant();
                    if (name.Contains("attached") || name.Contains("image") || name.EndsWith(".png")) n++;
                }
            }
            catch (Exception) { }
            return n;
        }

        // Pressing Enter while an attachment is still uploading does nothing, so wait for the
        // send button to come alive.
        static void WaitUntilSendable(AutomationElement composer, int waitMs)
        {
            DateTime until = DateTime.Now.AddMilliseconds(waitMs);
            var cond = new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new OrCondition(new PropertyCondition(AutomationElement.NameProperty, "Send message"),
                                new PropertyCondition(AutomationElement.NameProperty, "Send"),
                                new PropertyCondition(AutomationElement.NameProperty, "Submit")));
            AutomationElement send = null;
            try { send = composer.FindFirst(TreeScope.Descendants, cond); } catch (Exception) { }
            if (send == null) { Thread.Sleep(waitMs > 2000 ? 1200 : 0); return; }
            while (DateTime.Now < until)
            {
                try { if (send.Current.IsEnabled) return; } catch (Exception) { return; }
                Thread.Sleep(150);
            }
        }

        static void Key(byte modifier, byte vk)
        {
            if (modifier != 0) keybd_event(modifier, 0, 0, UIntPtr.Zero);
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            keybd_event(vk, 0, 2 /* KEYUP */, UIntPtr.Zero);
            if (modifier != 0) keybd_event(modifier, 0, 2, UIntPtr.Zero);
        }

        // Windows only lets the foreground app hand focus around; borrowing its input queue
        // lets us move focus even when the send was triggered by a timer (auto-send).
        static void ForceForeground(IntPtr h)
        {
            if (GetForegroundWindow() == h) return;
            uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
            uint me = GetCurrentThreadId();
            bool attached = fgThread != me && AttachThreadInput(me, fgThread, true);
            SetForegroundWindow(h);
            BringWindowToTop(h);
            if (attached) AttachThreadInput(me, fgThread, false);
        }

        public static bool SetClipboard(string text)
        {
            for (int i = 0; i < 8; i++)
            {
                try { Clipboard.SetText(text.Replace("\r\n", "\n").Replace("\n", "\r\n")); return true; }
                catch (ExternalException) { Thread.Sleep(60); }
            }
            return false;
        }
    }

    // ---------------------------------------------------------------- microphone ("You")

    // Your own voice gets its own channel, fully offline: the mic is recorded here, a voice
    // detector cuts it into sentences, and a local Whisper model (whisper.cpp's server, kept
    // loaded) transcribes each one. Live Captions only hears system audio, so the two channels
    // never have to be told apart by guessing.
    class LocalMic
    {
        [StructLayout(LayoutKind.Sequential)]
        struct WAVEFORMATEX { public short wFormatTag, nChannels; public int nSamplesPerSec, nAvgBytesPerSec; public short nBlockAlign, wBitsPerSample, cbSize; }
        [StructLayout(LayoutKind.Sequential)]
        struct WAVEHDR { public IntPtr lpData; public int dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public int dwFlags, dwLoops; public IntPtr lpNext, reserved; }
        [DllImport("winmm.dll")] static extern int waveInOpen(out IntPtr h, int device, ref WAVEFORMATEX fmt, IntPtr callback, IntPtr inst, int flags);
        [DllImport("winmm.dll")] static extern int waveInPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveInUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveInAddBuffer(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveInStart(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveInReset(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveInClose(IntPtr h);

        const int Rate = 16000, FrameSamples = 320; // 20 ms frames
        const int BufferBytes = 3200, Buffers = 8;  // 100 ms buffers
        const int WHDR_DONE = 1, CALLBACK_EVENT = 0x50000, WAVE_MAPPER = -1;

        public event Action<string, string, bool, double> Heard; // key, text, final, share of time the speakers were playing
        public volatile string Problem;  // null = working
        public volatile bool Ready;      // recording and the speech engine is loaded
        public volatile bool Speaking;   // voice detected right now
        public Func<bool> SpeakersPlaying = () => false;

        // what the mic channel did, for troubleshooting (kept small)
        static readonly string LogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clueless", "mic.log");
        public static void Log(string line)
        {
            try
            {
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line + Environment.NewLine);
            }
            catch (Exception) { }
        }

        readonly string engineDir;
        volatile bool wanted;
        volatile string modelFile = "ggml-base.en-q5_1.bin";
        Process server;
        int port;
        string session = "";
        int segment;
        readonly System.Collections.Concurrent.BlockingCollection<Job> jobs = new System.Collections.Concurrent.BlockingCollection<Job>();
        Job pendingPartial;
        readonly object partialGate = new object();

        class Job { public string Key; public byte[] Pcm; public bool Final; public double SpeakerShare; }

        public LocalMic(string engineDir)
        {
            this.engineDir = engineDir;
            KillOrphans();
            new Thread(RecordLoop) { IsBackground = true, Name = "mic-record" }.Start();
            new Thread(TranscribeLoop) { IsBackground = true, Name = "mic-whisper" }.Start();
        }

        public void SetModel(bool accurate)
        {
            string m = accurate ? "ggml-small.en-q5_1.bin" : "ggml-base.en-q5_1.bin";
            if (!File.Exists(Path.Combine(engineDir, "models", m))) m = "ggml-base.en-q5_1.bin";
            if (m == modelFile) return;
            modelFile = m;
            StopServer(); // restarts with the new model on the next sentence
        }

        public void Start() { session = DateTime.Now.Ticks.ToString(); segment = 0; wanted = true; }
        public void Stop() { wanted = false; Ready = false; Speaking = false; }
        public void Shutdown() { wanted = false; StopServer(); }

        // ---- recording + voice detection ----

        void RecordLoop()
        {
            while (true)
            {
                if (!wanted) { Thread.Sleep(200); continue; }
                try { Record(); }
                catch (Exception ex) { Problem = ex.Message; }
                Thread.Sleep(2000); // device gone / failed: try again
            }
        }

        void Record()
        {
            var fmt = new WAVEFORMATEX { wFormatTag = 1, nChannels = 1, nSamplesPerSec = Rate, wBitsPerSample = 16, nBlockAlign = 2, nAvgBytesPerSec = Rate * 2 };
            var signal = new AutoResetEvent(false);
            IntPtr h;
            int err = waveInOpen(out h, WAVE_MAPPER, ref fmt, signal.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CALLBACK_EVENT);
            if (err != 0) { Problem = "No microphone found (error " + err + ")."; Log(Problem); return; }
            Log("microphone open");
            int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));
            int flagsAt = (int)Marshal.OffsetOf(typeof(WAVEHDR), "dwFlags"), recordedAt = (int)Marshal.OffsetOf(typeof(WAVEHDR), "dwBytesRecorded");
            var hdrs = new IntPtr[Buffers];
            try
            {
                for (int i = 0; i < Buffers; i++)
                {
                    hdrs[i] = Marshal.AllocHGlobal(hdrSize);
                    var hdr = new WAVEHDR { lpData = Marshal.AllocHGlobal(BufferBytes), dwBufferLength = BufferBytes };
                    Marshal.StructureToPtr(hdr, hdrs[i], false);
                    waveInPrepareHeader(h, hdrs[i], hdrSize);
                    waveInAddBuffer(h, hdrs[i], hdrSize);
                }
                waveInStart(h);
                EnsureServer();
                var vad = new Vad(this);
                byte[] chunk = new byte[BufferBytes];
                // test hook: CLUELESS_FEED_WAV=<16 kHz mono wav> plays a recording through the same
                // detector + transcription path instead of the live mic
                string feed = Environment.GetEnvironmentVariable("CLUELESS_FEED_WAV");
                if (!string.IsNullOrEmpty(feed) && File.Exists(feed))
                {
                    byte[] wav = File.ReadAllBytes(feed);
                    var pcm = new List<byte>(wav.Skip(44));
                    pcm.AddRange(new byte[Rate * 2]); // a second of silence so the sentence ends
                    for (int i = 0; i < pcm.Count && wanted; i += BufferBytes)
                    {
                        int n = Math.Min(BufferBytes, pcm.Count - i);
                        pcm.CopyTo(i, chunk, 0, n);
                        vad.Feed(chunk, n);
                        Thread.Sleep(100);
                    }
                }
                while (wanted)
                {
                    signal.WaitOne(250);
                    foreach (var p in hdrs)
                    {
                        if ((Marshal.ReadInt32(p, flagsAt) & WHDR_DONE) == 0) continue;
                        int n = Marshal.ReadInt32(p, recordedAt);
                        Marshal.Copy(Marshal.ReadIntPtr(p), chunk, 0, n);
                        waveInAddBuffer(h, p, hdrSize); // hand the buffer back to the driver
                        vad.Feed(chunk, n);
                    }
                    if (server == null || server.HasExited) EnsureServer();
                }
                vad.Flush();
            }
            finally
            {
                waveInReset(h);
                foreach (var p in hdrs)
                {
                    if (p == IntPtr.Zero) continue;
                    waveInUnprepareHeader(h, p, hdrSize);
                    Marshal.FreeHGlobal(Marshal.ReadIntPtr(p));
                    Marshal.FreeHGlobal(p);
                }
                waveInClose(h);
                Ready = false;
            }
        }

        // Energy-based voice detector with an adaptive noise floor. A sentence starts after
        // 80 ms of voice (keeping 300 ms before it) and ends after 700 ms of silence. Laptop
        // array mics can be very quiet and output exact silence between words (their noise
        // suppression), so the floor ignores silent frames and the minimum threshold is low.
        class Vad
        {
            readonly LocalMic mic;
            readonly List<byte> seg = new List<byte>();
            readonly Queue<byte[]> preroll = new Queue<byte[]>();
            double noise = 0.0005;
            bool speaking;
            int loud, quiet, voiced, frames, speakerFrames, sincePartial;
            byte[] frameBuf = new byte[FrameSamples * 2];
            int frameFill;
            double levelMax; int levelFrames;

            public Vad(LocalMic mic) { this.mic = mic; }

            public void Feed(byte[] data, int n)
            {
                for (int i = 0; i < n; )
                {
                    int take = Math.Min(n - i, frameBuf.Length - frameFill);
                    Buffer.BlockCopy(data, i, frameBuf, frameFill, take);
                    frameFill += take; i += take;
                    if (frameFill == frameBuf.Length) { Frame(frameBuf); frameBuf = new byte[FrameSamples * 2]; frameFill = 0; }
                }
            }

            void Frame(byte[] f)
            {
                double sum = 0;
                for (int i = 0; i < f.Length; i += 2) { double s = (short)(f[i] | f[i + 1] << 8) / 32768.0; sum += s * s; }
                double rms = Math.Sqrt(sum / FrameSamples);
                double threshold = Math.Max(noise * 3.0, 0.0015);
                bool voice = rms > threshold;
                // level report every 5 s, for the troubleshooting log
                levelMax = Math.Max(levelMax, rms);
                if (++levelFrames >= 250) { Log("mic level: peak " + levelMax.ToString("0.0000") + ", noise floor " + noise.ToString("0.0000") + ", threshold " + threshold.ToString("0.0000")); levelMax = 0; levelFrames = 0; }

                if (!speaking)
                {
                    // the floor follows quiet levels quickly and loud ones slowly
                    if (rms > 0.00002) noise = rms < noise ? noise * 0.8 + rms * 0.2 : noise * 0.995 + rms * 0.005;
                    preroll.Enqueue(f);
                    if (preroll.Count > 15) preroll.Dequeue();
                    loud = voice ? loud + 1 : 0;
                    if (loud >= 4)
                    {
                        speaking = mic.Speaking = true;
                        Log("voice started (noise floor " + noise.ToString("0.0000") + ")");
                        seg.Clear();
                        foreach (var p in preroll) seg.AddRange(p);
                        preroll.Clear();
                        quiet = 0; voiced = loud; frames = 15; speakerFrames = 0; sincePartial = 0;
                    }
                    return;
                }

                seg.AddRange(f);
                frames++; sincePartial++;
                if (mic.SpeakersPlaying()) speakerFrames++;
                if (voice) { voiced++; quiet = 0; } else quiet++;
                if (quiet >= 35 || frames >= 750) End(); // 700 ms of silence, or 15 s max
                else if (sincePartial >= 75 && voiced >= 25) { sincePartial = 0; mic.Submit(seg.ToArray(), false, Share()); } // live text every 1.5 s
            }

            double Share() { return frames == 0 ? 0 : speakerFrames / (double)frames; }

            void End()
            {
                speaking = mic.Speaking = false;
                Log("voice ended: " + frames * 20 + " ms, voiced " + voiced * 20 + " ms, speakers playing " + (Share() * 100).ToString("0") + "%");
                if (voiced >= 12) mic.Submit(seg.ToArray(), true, Share()); // at least 240 ms of actual voice
                else mic.Discard();
                seg.Clear(); loud = 0;
            }

            public void Flush() { if (speaking) End(); }
        }

        void Submit(byte[] pcm, bool final, double speakerShare)
        {
            pcm = Normalize(pcm);
            var job = new Job { Key = "mic:" + session + ":" + segment, Pcm = pcm, Final = final, SpeakerShare = speakerShare };
            if (final)
            {
                segment++;
                lock (partialGate) pendingPartial = null;
                jobs.Add(job);
            }
            else lock (partialGate) pendingPartial = job; // only the newest live update matters
        }

        void Discard() { lock (partialGate) pendingPartial = null; }

        // Quiet mics: bring the loudest sample up to ~90% (at most 30x) before Whisper hears it.
        static byte[] Normalize(byte[] pcm)
        {
            int peak = 1;
            for (int i = 0; i + 1 < pcm.Length; i += 2) peak = Math.Max(peak, Math.Abs((int)(short)(pcm[i] | pcm[i + 1] << 8)));
            double gain = Math.Min(30.0, 0.9 * 32767 / peak);
            if (gain <= 1.05) return pcm;
            var o = new byte[pcm.Length];
            for (int i = 0; i + 1 < pcm.Length; i += 2)
            {
                int v = (int)Math.Round((short)(pcm[i] | pcm[i + 1] << 8) * gain);
                v = Math.Max(-32768, Math.Min(32767, v));
                o[i] = (byte)v; o[i + 1] = (byte)(v >> 8);
            }
            return o;
        }

        // ---- transcription ----

        void TranscribeLoop()
        {
            while (true)
            {
                Job job;
                if (!jobs.TryTake(out job, 100))
                {
                    lock (partialGate) { job = pendingPartial; pendingPartial = null; }
                    if (job == null) continue;
                }
                try
                {
                    var sw = Stopwatch.StartNew();
                    string text = Clean(Transcribe(job.Pcm));
                    if (job.Final) Log("transcribed in " + sw.ElapsedMilliseconds + " ms: \"" + text + "\"");
                    var h = Heard;
                    if (h != null && (text.Length > 0 || job.Final)) h(job.Key, text, job.Final, job.SpeakerShare);
                }
                catch (Exception ex) { Log("transcribe failed: " + ex.Message); if (job.Final) Problem = "Speech engine error: " + ex.Message; }
            }
        }

        string Transcribe(byte[] pcm)
        {
            if (!EnsureServer()) throw new Exception(Problem ?? "speech engine not running");
            string boundary = "----c2c" + DateTime.Now.Ticks.ToString("x");
            var body = new MemoryStream();
            Action<string> write = s => { var b = Encoding.UTF8.GetBytes(s); body.Write(b, 0, b.Length); };
            write("--" + boundary + "\r\nContent-Disposition: form-data; name=\"file\"; filename=\"speech.wav\"\r\nContent-Type: audio/wav\r\n\r\n");
            WriteWav(body, pcm);
            write("\r\n--" + boundary + "\r\nContent-Disposition: form-data; name=\"response_format\"\r\n\r\njson");
            write("\r\n--" + boundary + "\r\nContent-Disposition: form-data; name=\"temperature\"\r\n\r\n0.0");
            write("\r\n--" + boundary + "--\r\n");

            var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/inference");
            req.Method = "POST";
            req.ContentType = "multipart/form-data; boundary=" + boundary;
            req.Timeout = 30000;
            req.Proxy = null;
            byte[] data = body.ToArray();
            req.ContentLength = data.Length;
            using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
            using (var resp = req.GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                var obj = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(r.ReadToEnd());
                object t;
                return obj != null && obj.TryGetValue("text", out t) ? Convert.ToString(t) : "";
            }
        }

        static void WriteWav(Stream s, byte[] pcm)
        {
            var w = new BinaryWriter(s);
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + pcm.Length); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(pcm.Length); w.Write(pcm);
            w.Flush();
        }

        // Whisper invents text for noise ("Thank you.", "[BLANK_AUDIO]", "(music)"); drop those.
        static readonly HashSet<string> Phantoms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "thank you.", "thank you", "thanks for watching!", "thanks for watching.", "you", "bye.", ".", "okay.", "so" };

        static string Clean(string text)
        {
            text = System.Text.RegularExpressions.Regex.Replace(text ?? "", @"\s+", " ").Trim();
            text = System.Text.RegularExpressions.Regex.Replace(text, @"[\[\(][^\]\)]*[\]\)]", "").Trim();
            return Phantoms.Contains(text) ? "" : text;
        }

        // ---- the local Whisper server ----

        bool EnsureServer()
        {
            if (server != null && !server.HasExited && Ready) return true;
            lock (this)
            {
                if (server != null && !server.HasExited && Ready) return true;
                string exe = Path.Combine(engineDir, "whisper-server.exe"), model = Path.Combine(engineDir, "models", modelFile);
                if (!File.Exists(exe) || !File.Exists(model)) { Problem = "The speech engine is missing. Run build.cmd again."; return false; }
                if (server == null || server.HasExited)
                {
                    port = FreePort();
                    var psi = new ProcessStartInfo(exe, "-m \"" + model + "\" -t " + Math.Max(2, Environment.ProcessorCount / 2) + " --host 127.0.0.1 --port " + port)
                    { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = engineDir, RedirectStandardOutput = true, RedirectStandardError = true };
                    server = Process.Start(psi);
                    server.OutputDataReceived += delegate { };
                    server.ErrorDataReceived += delegate { };
                    server.BeginOutputReadLine();
                    server.BeginErrorReadLine();
                }
                Problem = null;
                for (int i = 0; i < 80 && !server.HasExited; i++)
                {
                    try
                    {
                        var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/");
                        req.Timeout = 500; req.Proxy = null;
                        using (req.GetResponse()) { }
                        Ready = true;
                        return true;
                    }
                    catch (WebException ex) { if (ex.Response != null) { ex.Response.Close(); Ready = true; return true; } }
                    Thread.Sleep(250);
                }
                Problem = "The speech engine didn't start.";
                return false;
            }
        }

        void StopServer()
        {
            Ready = false;
            try { if (server != null && !server.HasExited) server.Kill(); } catch (Exception) { }
            server = null;
        }

        static int FreePort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        // a crashed earlier run can leave its server behind
        void KillOrphans()
        {
            foreach (var p in Process.GetProcessesByName("whisper-server"))
            {
                try { if (p.MainModule.FileName.StartsWith(engineDir, StringComparison.OrdinalIgnoreCase)) p.Kill(); } catch (Exception) { }
                p.Dispose();
            }
        }
    }

    // ---------------------------------------------------------------- Live Captions window

    // Live Captions is the speech engine, so it has to run, but its own caption bar doesn't
    // need to be seen. Hiding it stops its text from being readable, so instead it's made
    // fully transparent, click-through and taken off the taskbar. Put back on exit.
    class LiveCaptionsWindow
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
        [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
        [DllImport("user32.dll")] static extern bool GetLayeredWindowAttributes(IntPtr h, out uint key, out byte alpha, out uint flags);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);

        const int GWL_EXSTYLE = -20;
        const long WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
        const uint LWA_ALPHA = 2;

        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

        delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);

        readonly object gate = new object();
        WinEventProc hookProc; // kept alive for as long as the hook is
        IntPtr hwnd;
        bool cloaked;
        DateTime lastLaunch = DateTime.MinValue;
        // the Live Captions process this app started (kept across restarts), so it's closed
        // on exit; one the user started themselves is only made visible again
        readonly string pidFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clueless", "livecaptions.pid");

        public bool Launching { get { return (DateTime.Now - lastLaunch).TotalSeconds < 15; } }

        public IntPtr Find()
        {
            lock (gate) return hwnd = FindWindow("LiveCaptionsDesktopWindow", null);
        }

        public void Launch()
        {
            if (Launching) return;
            lastLaunch = DateTime.Now;
            try
            {
                Process.Start(Path.Combine(Environment.SystemDirectory, "LiveCaptions.exe"));
                for (int i = 0; i < 30 && Find() == IntPtr.Zero; i++) Thread.Sleep(100);
                uint pid;
                if (hwnd != IntPtr.Zero && GetWindowThreadProcessId(hwnd, out pid) != 0)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(pidFile));
                    File.WriteAllText(pidFile, pid.ToString());
                }
            }
            catch (Exception) { }
        }

        bool LaunchedByUs()
        {
            uint pid;
            try { return hwnd != IntPtr.Zero && GetWindowThreadProcessId(hwnd, out pid) != 0 && File.Exists(pidFile) && File.ReadAllText(pidFile).Trim() == pid.ToString(); }
            catch (Exception) { return false; }
        }

        // Called on every poll: Live Captions may reset its own window styles. Only touches the
        // window when something actually changed: re-applying styles on every poll made the
        // caption bar flash.
        public void Cloak(bool on)
        {
            lock (gate)
            {
                if (hwnd == IntPtr.Zero) return;
                long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
                uint key, flags; byte alpha;
                bool styled = (ex & (WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW)) == (WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW);
                // a window can be layered without reporting an alpha; then trust what was set
                bool clear = GetLayeredWindowAttributes(hwnd, out key, out alpha, out flags) ? (flags & LWA_ALPHA) != 0 && alpha == 0 : cloaked;
                if (on == (styled && clear)) { cloaked = on; return; }
                bool taskbarChange = ((ex & WS_EX_TOOLWINDOW) != 0) != on;
                ex = on ? (ex | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW
                        : ex & ~(WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW);
                SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
                SetLayeredWindowAttributes(hwnd, 0, on ? (byte)0 : (byte)255, LWA_ALPHA);
                if (taskbarChange && IsWindowVisible(hwnd))
                {
                    // the taskbar only notices the style change when the window is shown again
                    // (it's already fully transparent by now, so this doesn't show)
                    ShowWindow(hwnd, 0 /* SW_HIDE */);
                    ShowWindow(hwnd, 4 /* SW_SHOWNOACTIVATE */);
                }
                cloaked = on;
            }
        }

        // Live Captions can show up between two polls (right after it starts, or when it
        // re-creates its window); a window-event hook cloaks it the moment it appears.
        // Must be called on a thread with a message loop.
        public void Watch(Func<bool> wanted)
        {
            hookProc = (hook, ev, h, obj, child, thread, time) =>
            {
                if (obj != 0 /* OBJID_WINDOW */ || h == IntPtr.Zero || !wanted()) return;
                var sb = new StringBuilder(64);
                GetClassName(h, sb, sb.Capacity);
                if (sb.ToString() != "LiveCaptionsDesktopWindow") return;
                lock (gate) hwnd = h;
                Cloak(true);
            };
            SetWinEventHook(0x8000 /* OBJECT_CREATE */, 0x8002 /* OBJECT_SHOW */, IntPtr.Zero, hookProc, 0, 0, 2 /* OUTOFCONTEXT | SKIPOWNPROCESS */);
        }

        public void Release()
        {
            if (Find() == IntPtr.Zero) return;
            if (LaunchedByUs())
            {
                // Live Captions ignores WM_CLOSE; it's our copy, so end it
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                try { using (var p = Process.GetProcessById((int)pid)) p.Kill(); } catch (Exception) { }
                try { File.Delete(pidFile); } catch (Exception) { }
            }
            else { cloaked = true; Cloak(false); }
        }
    }

    // ---------------------------------------------------------------- main window

    // A see-through window: the page draws a floating pill and a panel on a transparent
    // background, and the window's region is cut to those shapes so clicks elsewhere go to
    // whatever is underneath.
    class MainForm : Form
    {
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int value, int size);
        [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS m);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point pt, uint flags);
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr hWnd, IntPtr rgn, bool redraw);
        [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
        [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
        [DllImport("gdi32.dll")] static extern int CombineRgn(IntPtr dest, IntPtr a, IntPtr b, int mode);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct MARGINS { public int Left, Right, Top, Bottom; }

        public const int Port = 47321;
        const int WM_HOTKEY = 0x0312, WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 2, WM_DPICHANGED = 0x02E0;
        const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_NOREPEAT = 0x4000;
        const int HK_ASSIST = 1, HK_SAY = 2, HK_TOGGLE = 3;
        const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;
        const int GWL_EXSTYLE = -20, GWLP_HWNDPARENT = -8, WS_EX_APPWINDOW = 0x40000;
        // window size in 96-DPI units; the page lays out the pill and panel inside it
        const int BaseWidth = 680, BaseHeight = 760;

        readonly Store store = new Store();
        readonly AudioMeters meters = new AudioMeters();
        readonly ClaudeApp claude = new ClaudeApp();
        readonly LiveCaptionsWindow liveCaptions = new LiveCaptionsWindow();
        readonly LocalMic mic = new LocalMic(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "whisper"));
        readonly SessionLog log = new SessionLog();
        ArrayList lastShapes; // CSS-pixel shapes from the page, re-applied when the DPI changes
        readonly System.Windows.Forms.Timer dpiRefresh = new System.Windows.Forms.Timer { Interval = 120 };
        readonly WebView2 web = new WebView2();
        readonly System.Windows.Forms.Timer uiTimer = new System.Windows.Forms.Timer();
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 };
        readonly string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clueless");
        readonly bool forceVisible = Environment.GetCommandLineArgs().Contains("--no-stealth");
        readonly NativeWindow hiddenOwner = new NativeWindow();

        Dictionary<string, object> settings = new Dictionary<string, object>();
        List<string> sentRowSigs = new List<string>();
        volatile bool running = true;
        volatile bool inSession;
        volatile string pollState = "";
        volatile string hearing = "";
        AutomationElement captionEl;
        bool pageReady, stealthApplied, sentInSession;
        // the warm-up (profile, CV, instructions) of this session, and the chat it went to
        int warmupId;
        string warmupHash = "";
        int shownVersion = -1;
        string lastStatusJson = "";
        string hotkeyNote = "", serverNote = "";

        public MainForm(EventWaitHandle showSignal)
        {
            Text = "Clueless";
            FormBorderStyle = FormBorderStyle.None;
            BackColor = Color.Black; // black + extended frame = see-through where the page is transparent
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

            web.Dock = DockStyle.Fill;
            web.DefaultBackgroundColor = Color.Transparent;
            Controls.Add(web);

            LoadSettings();
            TopMost = Setting("onTop", true);
            uiTimer.Interval = 250;
            uiTimer.Tick += delegate { OnUiTick(); };
            claude.Report += m =>
            {
                log.OnClaude(m);
                try { BeginInvoke((Action)delegate { RememberWarmChat(m); Post(m); }); } catch (Exception) { }
            };
            mic.Heard += OnMicHeard;
            mic.SpeakersPlaying = meters.SpeakersPlaying;
            mic.SetModel(Setting("micAccurate", false));
            dpiRefresh.Tick += delegate
            {
                // wait until the mouse is released if the window is being dragged
                if ((MouseButtons & MouseButtons.Left) != 0) return;
                dpiRefresh.Stop();
                web.Visible = false;
                web.Visible = true;
            };

            // a second copy was started: show this one instead
            new Thread(() =>
            {
                while (showSignal.WaitOne())
                    try { BeginInvoke((Action)delegate { Show(); Activate(); }); } catch (Exception) { }
            }) { IsBackground = true }.Start();
        }

        bool Setting(string key, bool def)
        {
            object v;
            return settings.TryGetValue(key, out v) && v is bool ? (bool)v : def;
        }

        string Setting(string key, string def)
        {
            object v;
            return settings.TryGetValue(key, out v) && v is string && ((string)v).Length > 0 ? (string)v : def;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(Handle, ref margins);
            int noRound = 1 /* DWMWCP_DONOTROUND */, noBorder = unchecked((int)0xFFFFFFFE) /* DWMWA_COLOR_NONE */;
            DwmSetWindowAttribute(Handle, 33 /* WINDOW_CORNER_PREFERENCE */, ref noRound, 4);
            DwmSetWindowAttribute(Handle, 34 /* BORDER_COLOR */, ref noBorder, 4);

            // per-monitor DPI aware (app.manifest): size for the primary screen, top-center
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            uint dx, dy;
            int dpi = GetDpiForMonitor(MonitorFromPoint(new Point(wa.Left + 1, wa.Top + 1), 1), 0, out dx, out dy) == 0 ? (int)dx : 96;
            Size = new Size(BaseWidth * dpi / 96, BaseHeight * dpi / 96);
            Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + 6 * dpi / 96);
            // nothing clickable until the page reports its shapes
            SetWindowRgn(Handle, CreateRectRgn(0, 0, 0, 0), true);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DPICHANGED)
            {
                // moved to a screen with different scaling: take the size Windows suggests (same
                // proportions), re-cut the click region for the new scale right away, and repaint
                // so nothing from the old size is left behind
                var rc = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                SetWindowPos(Handle, IntPtr.Zero, rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top, 0x0014 /* NOZORDER|NOACTIVATE */);
                if (lastShapes != null) SetShapes(lastShapes, (m.WParam.ToInt32() & 0xFFFF) / 96.0);
                Invalidate(true);
                Update();
                // the browser view keeps a stale layer at the old size behind the new one;
                // re-showing it once the resize settles makes it rebuild cleanly
                dpiRefresh.Stop();
                dpiRefresh.Start();
                m.Result = IntPtr.Zero;
                return;
            }
            if (m.Msg == WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == HK_TOGGLE) { if (Visible) Hide(); else { Show(); Activate(); } }
                else Post(new Dictionary<string, object> { { "type", "hotkey" }, { "name", id == HK_ASSIST ? "assist" : "say" } });
                return;
            }
            base.WndProc(ref m);
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ApplyStealth(Setting("stealth", true) && !forceVisible);
            if (!RegisterHotKey(Handle, HK_ASSIST, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)Keys.A)) hotkeyNote += " Ctrl+Alt+A is taken.";
            if (!RegisterHotKey(Handle, HK_SAY, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)Keys.S)) hotkeyNote += " Ctrl+Alt+S is taken.";
            if (!RegisterHotKey(Handle, HK_TOGGLE, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)Keys.H)) hotkeyNote += " Ctrl+Alt+H is taken.";
            meters.Start();
            liveCaptions.Watch(() => inSession && Setting("hideLiveCaptions", true));
            new Thread(PollLoop) { IsBackground = true, Name = "captions" }.Start();
            new Thread(ServeExtension) { IsBackground = true, Name = "extension" }.Start();

            string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clueless", "WebView2");
            for (int attempt = 0; ; attempt++)
            {
                Exception failure = null;
                try
                {
                    var env = await CoreWebView2Environment.CreateAsync(null, profile);
                    await web.EnsureCoreWebView2Async(env);
                    break;
                }
                catch (Exception ex) { failure = ex; }
                // a closing copy can still hold the profile for a moment; then use a spare one
                if (attempt == 0) { await Task.Delay(1500); continue; }
                if (attempt == 1) { profile += "-" + Process.GetCurrentProcess().Id; continue; }
                SetWindowRgn(Handle, IntPtr.Zero, true);
                MessageBox.Show(this, "Couldn't start the WebView2 runtime:\n" + failure.Message, Text);
                return;
            }
            var core = web.CoreWebView2;
            bool dev = Environment.GetCommandLineArgs().Contains("--dev");
            core.Settings.AreDefaultContextMenusEnabled = dev;
            core.Settings.AreDevToolsEnabled = dev;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.SetVirtualHostNameToFolderMapping("app.local",
                Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "ui"), CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += OnWebMessage;
            core.NewWindowRequested += (s, a) => { a.Handled = true; };
            core.Navigate("https://app.local/index.html");
            uiTimer.Start();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (inSession) StopSession();
            running = false;
            uiTimer.Stop();
            UnregisterHotKey(Handle, HK_ASSIST);
            UnregisterHotKey(Handle, HK_SAY);
            UnregisterHotKey(Handle, HK_TOGGLE);
            liveCaptions.Release();
            mic.Shutdown();
            base.OnFormClosing(e);
        }

        // Excluded windows show up as nothing in screen shares / recordings / screenshots,
        // and are taken off the taskbar so a full-screen share doesn't show the icon.
        void ApplyStealth(bool on)
        {
            SetWindowDisplayAffinity(Handle, on ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
            if (on == stealthApplied) return;
            stealthApplied = on;
            if (hiddenOwner.Handle == IntPtr.Zero) hiddenOwner.CreateHandle(new CreateParams());
            bool wasVisible = Visible;
            if (wasVisible) ShowWindow(Handle, 0 /* SW_HIDE */);
            long ex = GetWindowLongPtr(Handle, GWL_EXSTYLE).ToInt64();
            ex = on ? ex & ~WS_EX_APPWINDOW : ex | WS_EX_APPWINDOW;
            SetWindowLongPtr(Handle, GWL_EXSTYLE, new IntPtr(ex));
            SetWindowLongPtr(Handle, GWLP_HWNDPARENT, on ? hiddenOwner.Handle : IntPtr.Zero);
            if (wasVisible) ShowWindow(Handle, 5 /* SW_SHOW */);
        }

        // The page reports where its pill / panel / menus are; only those areas take clicks.
        void SetShapes(ArrayList rects, double dpr)
        {
            lastShapes = rects;
            IntPtr region = CreateRectRgn(0, 0, 0, 0);
            foreach (object o in rects)
            {
                var r = o as Dictionary<string, object>;
                if (r == null) continue;
                int x = (int)Math.Floor(Convert.ToDouble(r["x"]) * dpr), y = (int)Math.Floor(Convert.ToDouble(r["y"]) * dpr);
                int w = (int)Math.Ceiling(Convert.ToDouble(r["w"]) * dpr), h = (int)Math.Ceiling(Convert.ToDouble(r["h"]) * dpr);
                int rad = (int)Math.Round(Convert.ToDouble(r["r"]) * dpr * 2);
                IntPtr part = CreateRoundRectRgn(x, y, x + w + 1, y + h + 1, rad, rad);
                CombineRgn(region, region, part, 2 /* RGN_OR */);
                DeleteObject(part);
            }
            SetWindowRgn(Handle, region, true); // the window owns the region from here on
        }

        // ---- sessions ----

        void StartSession()
        {
            store.Reset();
            sentRowSigs = new List<string>();
            shownVersion = -1;
            sentInSession = false;
            warmupId = 0;
            log.Start(Setting("sessionsFolder", DefaultSessionsFolder()));
            inSession = true;
            mic.SetModel(Setting("micAccurate", false));
            mic.Start();
        }

        void StopSession()
        {
            inSession = false;
            mic.Shutdown(); // frees the speech engine's memory between sessions
            captionEl = null;
            log.Save(store.BuildRows(), Setting("meName", "You"));
            log.Stop();
            liveCaptions.Release();
            Post(new Dictionary<string, object> { { "type", "saved" }, { "path", log.LastPath ?? "" } });
        }

        static string DefaultSessionsFolder()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Clueless Sessions");
        }

        // ---- Live Captions (any app / site) ----

        void PollLoop()
        {
            while (running)
            {
                if (!inSession) { pollState = ""; Thread.Sleep(300); continue; }
                try
                {
                    string state;
                    string snap = ReadCaptions(out state);
                    pollState = state;
                    if (snap != null) store.MergeLiveCaptions(snap, CurrentLabel());
                }
                catch (Exception)
                {
                    captionEl = null; // window closed or element went stale
                    pollState = "Reconnecting…";
                }
                Thread.Sleep(liveCaptions.Launching ? 100 : 300); // cloak a freshly started Live Captions quickly
            }
        }

        // Live Captions text is other people (system audio). If its own "include microphone"
        // option is on it also hears you; while the mic channel covers your voice, that copy is
        // hidden. Without a working mic channel, fall back to telling you apart by the meters.
        byte CurrentLabel()
        {
            if (store.MeetingActive) { hearing = ""; return LcTranscript.Hidden; }
            double micFrac, spkFrac;
            meters.Activity(out micFrac, out spkFrac);
            hearing = spkFrac >= 0.1 ? "Others" : micFrac >= 0.1 ? "Me" : "";
            if (spkFrac >= 0.1) return LcTranscript.Others;
            if (micFrac >= 0.1) return mic.Ready ? LcTranscript.Hidden : LcTranscript.Me;
            return store.LastLcLabel();
        }

        // Your mic, as its own channel. Without headphones it also picks up the speakers: a
        // sentence spoken while they were playing is only shown once it's final and doesn't
        // match what Live Captions heard from system audio (that would be an echo).
        void OnMicHeard(string key, string text, bool final, double speakerShare)
        {
            if (!inSession) return;
            if (text.Length == 0) { if (final) store.RemoveUtterance(key); return; }
            if (speakerShare > 0.2)
            {
                if (!final) return;
                Thread.Sleep(1200); // let Live Captions catch up with the same audio
                if (IsEcho(text, store.RecentSystemText(30))) { LocalMic.Log("dropped as echo of system audio"); store.RemoveUtterance(key); return; }
            }
            store.Upsert(key, "Me", final && text.Length > 1 ? char.ToUpper(text[0]) + text.Substring(1) : text);
        }

        static bool IsEcho(string mine, string system)
        {
            var words = System.Text.RegularExpressions.Regex.Matches(mine.ToLowerInvariant(), @"[\p{L}\p{N}']+").Cast<System.Text.RegularExpressions.Match>().Select(x => x.Value).ToList();
            if (words.Count < 3) return false;
            var heard = new HashSet<string>(System.Text.RegularExpressions.Regex.Matches(system.ToLowerInvariant(), @"[\p{L}\p{N}']+").Cast<System.Text.RegularExpressions.Match>().Select(x => x.Value));
            return words.Count(w => heard.Contains(w)) >= words.Count * 0.6;
        }

        string ReadCaptions(out string state)
        {
            IntPtr h = liveCaptions.Find();
            if (h == IntPtr.Zero)
            {
                captionEl = null;
                liveCaptions.Launch(); // Live Captions is the speech engine; keep it running during a session
                state = "Starting…";
                return null;
            }
            liveCaptions.Cloak(Setting("hideLiveCaptions", true));
            if (captionEl == null)
            {
                captionEl = AutomationElement.FromHandle(h).FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "CaptionsTextBlock"));
                if (captionEl == null) { state = "Starting…"; return null; }
            }
            state = "Listening";
            return captionEl.Current.Name;
        }

        // ---- Chrome extension (meeting captions with names) ----

        void ServeExtension()
        {
            var listener = new HttpListener();
            listener.Prefixes.Add("http://localhost:" + Port + "/");
            try { listener.Start(); }
            catch (Exception ex) { serverNote = " Meeting link off: " + ex.Message; return; }
            var js = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
            while (running)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); }
                catch (Exception) { return; }
                try
                {
                    ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");
                    ctx.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type");
                    if (ctx.Request.HttpMethod == "POST" && inSession)
                    {
                        string body;
                        using (var r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = r.ReadToEnd();
                        var msg = js.Deserialize<Dictionary<string, object>>(body);
                        string platform = msg.ContainsKey("platform") ? Convert.ToString(msg["platform"]) : "meeting";
                        store.MeetingHeartbeat(platform);
                        object items;
                        if (msg.TryGetValue("items", out items) && items is ArrayList)
                        {
                            foreach (object o in (ArrayList)items)
                            {
                                var it = o as Dictionary<string, object>;
                                if (it == null) continue;
                                store.Upsert(Convert.ToString(it["key"]), Convert.ToString(it["speaker"]), Convert.ToString(it["text"]));
                            }
                        }
                    }
                    byte[] ok = Encoding.UTF8.GetBytes("ok");
                    ctx.Response.ContentType = "text/plain";
                    ctx.Response.OutputStream.Write(ok, 0, ok.Length);
                }
                catch (Exception) { ctx.Response.StatusCode = 400; }
                finally { try { ctx.Response.Close(); } catch (Exception) { } }
            }
        }

        // ---- page bridge ----

        void Post(Dictionary<string, object> msg)
        {
            if (!pageReady || web.CoreWebView2 == null) return;
            web.CoreWebView2.PostWebMessageAsJson(json.Serialize(msg));
        }

        static readonly long Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

        void OnUiTick()
        {
            if (!pageReady) return;
            // rows: only send from the first row that changed (captions only change at the tail)
            if (store.Version != shownVersion || (DateTime.Now - store.LastChange).TotalSeconds < 2)
            {
                shownVersion = store.Version;
                var rows = store.BuildRows();
                if (inSession) log.SaveSoon(rows, Setting("meName", "You"));
                var sigs = rows.Select(r => r.Key + "\u0001" + r.Speaker + "\u0001" + r.Text + "\u0001" + r.Final + r.Sent).ToList();
                int from = 0;
                while (from < sigs.Count && from < sentRowSigs.Count && sigs[from] == sentRowSigs[from]) from++;
                if (from < sigs.Count || sigs.Count != sentRowSigs.Count)
                {
                    var list = new ArrayList();
                    for (int i = from; i < rows.Count; i++)
                    {
                        var r = rows[i];
                        list.Add(new Dictionary<string, object> {
                            { "k", r.Key }, { "s", r.Speaker }, { "t", r.Text }, { "ts", (r.Time - Epoch) / 10000 },
                            { "f", r.Final }, { "sent", r.Sent } });
                    }
                    Post(new Dictionary<string, object> { { "type", "rows" }, { "from", from }, { "rows", list } });
                    sentRowSigs = sigs;
                }
            }
            else if (inSession) log.SaveSoon(null, Setting("meName", "You")); // replies may have changed

            string mode, text;
            if (!inSession) { mode = "idle"; text = ""; }
            else if (store.MeetingActive) { mode = "meeting"; text = "Meeting · " + store.MeetingPlatform; }
            else if (pollState == "Listening") { mode = "live"; text = "Listening"; }
            else { mode = "starting"; text = pollState; }
            string micState = !inSession ? "off" : mic.Problem != null ? "error" : mic.Ready ? "ok" : "starting";
            if (inSession && mic.Speaking) hearing = "Me";
            var status = new Dictionary<string, object> {
                { "type", "status" }, { "session", inSession }, { "mode", mode }, { "text", text }, { "hearing", hearing },
                { "mic", micState }, { "micError", mic.Problem ?? "" },
                { "started", inSession ? (log.Started.ToUniversalTime().Ticks - Epoch) / 10000 : 0 },
                { "note", (hotkeyNote + serverNote).Trim() } };
            string sj = json.Serialize(status);
            if (sj != lastStatusJson) { lastStatusJson = sj; Post(status); }
        }

        void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            Dictionary<string, object> m;
            try { m = json.Deserialize<Dictionary<string, object>>(e.WebMessageAsJson); }
            catch (Exception) { return; }
            switch (Str(m, "type"))
            {
                case "ready":
                    pageReady = true;
                    sentRowSigs = new List<string>();
                    shownVersion = -1;
                    lastStatusJson = "";
                    var s = new Dictionary<string, object>(settings);
                    s["sessionsFolder"] = Setting("sessionsFolder", DefaultSessionsFolder());
                    Post(new Dictionary<string, object> { { "type", "settings" }, { "settings", s } });
                    break;
                case "settings":
                    var incoming = m["settings"] as Dictionary<string, object>;
                    if (incoming == null) break;
                    settings = incoming;
                    SaveSettings();
                    TopMost = Setting("onTop", true);
                    ApplyStealth(Setting("stealth", true) && !forceVisible);
                    mic.SetModel(Setting("micAccurate", false));
                    break;
                case "shapes":
                    var rects = m["rects"] as ArrayList;
                    if (rects != null) SetShapes(rects, m.ContainsKey("dpr") ? Convert.ToDouble(m["dpr"]) : 1.0);
                    break;
                case "start": if (!inSession) StartSession(); break;
                case "stop": if (inSession) StopSession(); break;
                case "warmup":
                    // Primes Claude with who you are once per session. With "new chat per
                    // session" off, the open chat is reused, and skipped if it already has
                    // this exact profile.
                    warmupId = m.ContainsKey("id") ? Convert.ToInt32(m["id"]) : 0;
                    warmupHash = Str(m, "hash");
                    bool fresh = Setting("newChat", false);
                    var warm = LoadWarm();
                    claude.Send(new ClaudeApp.Job
                    {
                        Id = warmupId,
                        Text = Str(m, "text"),
                        Warmup = true,
                        NewChat = fresh,
                        SkipIfChat = !fresh && !(m.ContainsKey("force") && m["force"] is bool && (bool)m["force"]) && Str(warm, "hash") == warmupHash ? Str(warm, "chat") : null,
                        ReturnFocus = Setting("returnFocus", true),
                    });
                    if (inSession) sentInSession = true;
                    break;
                case "send":
                    MarkSent(m);
                    var job = new ClaudeApp.Job
                    {
                        Id = m.ContainsKey("id") ? Convert.ToInt32(m["id"]) : 0,
                        Text = Str(m, "text"),
                        Intro = Str(m, "intro"),
                        ReturnFocus = Setting("returnFocus", true),
                        IncludeScreen = m.ContainsKey("screen") && m["screen"] is bool && (bool)m["screen"],
                        NewChat = inSession && !sentInSession && Setting("newChat", false),
                    };
                    if (inSession) sentInSession = true;
                    log.OnAsk(job.Id, Str(m, "label"), job.IncludeScreen);
                    claude.Send(job);
                    break;
                case "copy":
                    Toast(ClaudeApp.SetClipboard(Str(m, "text")) ? "Copied" : "Couldn't access the clipboard");
                    break;
                case "rename": store.Rename(Str(m, "from"), Str(m, "to")); break;
                case "override": store.Override(Str(m, "key"), Str(m, "speaker")); break;
                case "openPath":
                    string path = Str(m, "path");
                    if (path.Length > 0 && (File.Exists(path) || Directory.Exists(path))) Process.Start(path);
                    break;
                case "openSessions":
                    string dir = Setting("sessionsFolder", DefaultSessionsFolder());
                    Directory.CreateDirectory(dir);
                    Process.Start(dir);
                    break;
                case "drag":
                    // the message can arrive after the button is already up; starting a move
                    // then would make the window follow the cursor
                    if ((MouseButtons & MouseButtons.Left) == 0) break;
                    ReleaseCapture();
                    SendMessage(Handle, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
                    break;
                case "pickFile": PickFile(Str(m, "target")); break;
                case "close": Close(); break;
            }
        }

        static string Str(Dictionary<string, object> m, string key)
        {
            object v;
            return m.TryGetValue(key, out v) && v != null ? Convert.ToString(v) : "";
        }

        void MarkSent(Dictionary<string, object> m)
        {
            object keys;
            if (!m.TryGetValue("keys", out keys) || !(keys is ArrayList)) return;
            store.MarkSent(((ArrayList)keys).Cast<object>().Select(k => new Row { Key = Convert.ToString(k) }));
        }

        void Toast(string text)
        {
            Post(new Dictionary<string, object> { { "type", "toast" }, { "text", text } });
        }

        // ---- warm-up: which Claude chat already knows the profile ----

        string WarmPath { get { return Path.Combine(dataDir, "warm.json"); } }

        Dictionary<string, object> LoadWarm()
        {
            try { if (File.Exists(WarmPath)) return json.Deserialize<Dictionary<string, object>>(File.ReadAllText(WarmPath)) ?? new Dictionary<string, object>(); }
            catch (Exception) { }
            return new Dictionary<string, object>();
        }

        // The warm-up's chat is remembered by its title, so the next session can reuse it
        // without priming it again. A new chat only gets its real title after the first
        // exchange, so the title is refreshed by the session's next reply.
        void RememberWarmChat(Dictionary<string, object> m)
        {
            if (warmupId == 0 || Str(m, "chat").Length == 0) return;
            string state = Str(m, "state");
            if (state != "done" && state != "skipped") return;
            var known = LoadWarm();
            bool titlePending = Str(known, "hash") == warmupHash && (Str(known, "chat").Length == 0 || Str(known, "chat").StartsWith("New "));
            if (Convert.ToInt32(m["id"]) != warmupId && !titlePending) return;
            try
            {
                Directory.CreateDirectory(dataDir);
                File.WriteAllText(WarmPath, json.Serialize(new Dictionary<string, object> { { "chat", Str(m, "chat") }, { "hash", warmupHash } }));
            }
            catch (Exception) { }
        }

        // ---- files for the profile (CV) ----

        void PickFile(string target)
        {
            using (var dlg = new OpenFileDialog { Title = "Choose your CV / résumé", Filter = "Text or Word (*.txt;*.md;*.docx)|*.txt;*.md;*.docx|All files (*.*)|*.*" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string text;
                try
                {
                    text = dlg.FileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) ? ReadDocx(dlg.FileName) : File.ReadAllText(dlg.FileName);
                }
                catch (Exception ex) { Toast("Couldn't read that file: " + ex.Message); return; }
                Post(new Dictionary<string, object> { { "type", "file" }, { "target", target }, { "name", Path.GetFileName(dlg.FileName) }, { "text", text.Trim() } });
            }
        }

        // A .docx is a zip; its text is in word/document.xml, one <w:p> per paragraph.
        static string ReadDocx(string path)
        {
            using (var zip = System.IO.Compression.ZipFile.OpenRead(path))
            {
                var entry = zip.GetEntry("word/document.xml");
                if (entry == null) throw new Exception("not a Word document");
                var xml = new System.Xml.XmlDocument();
                using (var s = entry.Open()) xml.Load(s);
                var ns = new System.Xml.XmlNamespaceManager(xml.NameTable);
                ns.AddNamespace("w", "http://schemas.openxmlformats.org/wordprocessingml/2006/main");
                var sb = new StringBuilder();
                foreach (System.Xml.XmlNode p in xml.SelectNodes("//w:body//w:p", ns))
                {
                    foreach (System.Xml.XmlNode t in p.SelectNodes(".//w:t|.//w:tab", ns)) sb.Append(t.LocalName == "tab" ? "\t" : t.InnerText);
                    sb.Append('\n');
                }
                return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\n{3,}", "\n\n");
            }
        }

        // ---- settings (the page owns their shape) ----

        string SettingsPath { get { return Path.Combine(dataDir, "settings.json"); } }

        void LoadSettings()
        {
            try
            {
                // carried over from the app's old name
                string old = Path.Combine(Path.GetDirectoryName(dataDir), "CaptionToClaude", "settings.json");
                if (!File.Exists(SettingsPath) && File.Exists(old)) { Directory.CreateDirectory(dataDir); File.Copy(old, SettingsPath); }
                if (File.Exists(SettingsPath))
                    settings = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(SettingsPath)) ?? settings;
            }
            catch (Exception) { }
        }

        void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(dataDir);
                File.WriteAllText(SettingsPath, json.Serialize(settings));
            }
            catch (Exception) { }
        }
    }

    // ---------------------------------------------------------------- session file

    // Writes the session (transcript + Claude's answers, in time order) to a Markdown file,
    // rewritten every few seconds so nothing is lost if the app or PC goes down.
    class SessionLog
    {
        class Ask { public int Id; public DateTime Time; public string Label, Reply, Error; public bool Screen; }

        readonly object gate = new object();
        readonly List<Ask> asks = new List<Ask>();
        List<Row> lastRows = new List<Row>();
        string path;
        bool dirty;
        DateTime lastWrite = DateTime.MinValue;
        public DateTime Started { get; private set; }
        public string LastPath { get; private set; }

        public void Start(string folder)
        {
            lock (gate)
            {
                Started = DateTime.Now;
                asks.Clear();
                lastRows = new List<Row>();
                Directory.CreateDirectory(folder);
                path = Path.Combine(folder, Started.ToString("yyyy-MM-dd HH.mm") + ".md");
                LastPath = path;
                dirty = false;
            }
        }

        public void Stop() { lock (gate) path = null; }

        public void OnAsk(int id, string label, bool screen)
        {
            lock (gate)
            {
                if (path == null) return;
                asks.Add(new Ask { Id = id, Time = DateTime.Now, Label = label, Screen = screen });
                dirty = true;
            }
        }

        public void OnClaude(Dictionary<string, object> m)
        {
            lock (gate)
            {
                int id = Convert.ToInt32(m["id"]);
                var a = asks.FirstOrDefault(x => x.Id == id);
                if (a == null) return;
                object v;
                if (m.TryGetValue("text", out v) && v != null) a.Reply = Convert.ToString(v);
                if (m.TryGetValue("error", out v) && v != null) a.Error = Convert.ToString(v);
                dirty = true;
            }
        }

        public void SaveSoon(List<Row> rows, string meName)
        {
            lock (gate)
            {
                if (rows != null) { lastRows = rows; dirty = true; }
                if (!dirty || path == null || (DateTime.Now - lastWrite).TotalSeconds < 3) return;
            }
            Save(null, meName);
        }

        public void Save(List<Row> rows, string meName)
        {
            string text, target;
            lock (gate)
            {
                if (path == null) return;
                if (rows != null) lastRows = rows;
                text = Render(meName);
                target = path;
                dirty = false;
                lastWrite = DateTime.Now;
            }
            try
            {
                string tmp = target + ".tmp";
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                if (File.Exists(target)) File.Replace(tmp, target, null); else File.Move(tmp, target);
            }
            catch (Exception) { }
        }

        string Render(string meName)
        {
            var sb = new StringBuilder();
            sb.Append("# Session · ").Append(Started.ToString("dddd, d MMMM yyyy · HH:mm"))
              .Append(" – ").Append(DateTime.Now.ToString("HH:mm")).Append("\n\n");
            var items = new List<KeyValuePair<DateTime, string>>();
            // consecutive lines by one speaker become one paragraph
            string speaker = null; DateTime start = DateTime.MinValue, last = DateTime.MinValue; var para = new StringBuilder();
            Action flush = () =>
            {
                if (para.Length > 0) items.Add(new KeyValuePair<DateTime, string>(start,
                    "**" + start.ToString("HH:mm") + " · " + speaker + ":** " + para.ToString().Trim()));
                para.Clear();
            };
            foreach (var r in lastRows)
            {
                var t = new DateTime(r.Time, DateTimeKind.Utc).ToLocalTime();
                string who = r.Speaker == "Me" ? meName : r.Speaker;
                if (who != speaker || (t - last).TotalSeconds > 60) { flush(); speaker = who; start = t; }
                para.Append(r.Text).Append(' ');
                last = t;
            }
            flush();
            foreach (var a in asks)
            {
                var q = new StringBuilder();
                q.Append("> **Claude · ").Append(a.Time.ToString("HH:mm")).Append(" · ").Append(a.Label)
                 .Append(a.Screen ? " (with screen)" : "").Append("**\n>\n");
                string body = a.Error ?? a.Reply ?? "(no reply captured)";
                foreach (var line in body.Replace("\r", "").Split('\n')) q.Append("> ").Append(line).Append('\n');
                items.Add(new KeyValuePair<DateTime, string>(a.Time, q.ToString().TrimEnd()));
            }
            foreach (var it in items.OrderBy(i => i.Key)) sb.Append(it.Value).Append("\n\n");
            return sb.ToString();
        }
    }
}
