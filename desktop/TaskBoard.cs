// One board for every Claude and ChatGPT task on this PC.
//
// The apps mostly do not say when a task finishes, so this collects it from
// four places and shows it in one list, with a Windows notification when a
// task finishes or needs you:
//
//   Claude Code hooks   POST /hook/claude-code/{start|stop|needs}
//   Codex (terminal)    POST /hook/codex
//   Brave extension     POST /event
//   the desktop apps    watched here, through UI Automation, by their Stop button
//
// The receiver listens on 127.0.0.1 only, never on the network, and every
// request must carry the header X-Prompt-Advisor: 1. A web page cannot send a
// custom header to another origin without a CORS preflight this never
// answers, so a site you visit cannot post fake tasks to it. A site that
// rebinds its own name to 127.0.0.1 gets past that, so the Host must also be
// 127.0.0.1 or localhost, and any Origin must be the extension.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Forms;

public class TaskItem
{
    public string Key, Source, Title, State, Url, App;
    public DateTime Started, Updated;
}

public class TaskBoard
{
    public const int Port = 47823;
    // Hook input carries a prompt and a little more; a report is never near this.
    const int MaxBody = 1000000;

    readonly Dictionary<string, TaskItem> items = new Dictionary<string, TaskItem>();
    readonly object gate = new object();
    readonly NotifyIcon tray;
    readonly Control ui;
    readonly JavaScriptSerializer json = new JavaScriptSerializer();
    readonly string store;
    readonly Dictionary<string, string> sessionTitles = new Dictionary<string, string>();
    TcpListener listener;
    BoardForm form;
    volatile bool running = true;

    // For the desk pet. Both are raised on the board's own threads.
    public event Action Changed;
    public event Action<TaskItem> Alerted;
    // The task list was opened, from the tray, a notification or a second launch.
    public event Action Shown;

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    public TaskBoard(NotifyIcon tray, Control ui)
    {
        this.tray = tray;
        this.ui = ui;
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptAdvisor");
        Directory.CreateDirectory(dir);
        store = Path.Combine(dir, "tasks.json");
        Load();

        Thread t = new Thread(Listen);
        t.IsBackground = true;
        t.Start();
        Thread w = new Thread(WatchApps);
        w.IsBackground = true;
        w.Start();
    }

    public void Stop()
    {
        running = false;
        try { if (listener != null) listener.Stop(); } catch { }
    }

    /* -------------------------------------------------------------- *
     * Tasks
     * -------------------------------------------------------------- */

    // Create or update a task. A move into done or needs raises a
    // notification, unless quiet says you are already looking at it.
    void Upsert(string key, string source, string title, string state, string url, string app, bool quiet)
    {
        TaskItem it;
        bool changed;
        lock (gate)
        {
            if (!items.TryGetValue(key, out it))
            {
                it = new TaskItem();
                it.Key = key; it.Source = source; it.Started = DateTime.Now; it.App = app;
                items[key] = it;
            }
            changed = it.State != state;
            // A session or thread is one row, so a second request for your
            // approval in it is not a change of state. It is still news when
            // it asks about something else, or comes after a pause.
            if (!changed && state == "needs"
                && ((!string.IsNullOrEmpty(title) && title != it.Title) || (DateTime.Now - it.Updated).TotalSeconds > 10))
                changed = true;
            // A Claude Code session is one row across all its turns, so each
            // new turn restarts the clock rather than counting from the first.
            if (changed && state == "working" && it.State != "hidden") it.Started = DateTime.Now;
            it.State = state;
            if (!string.IsNullOrEmpty(source)) it.Source = source;
            if (!string.IsNullOrEmpty(title)) it.Title = title;
            if (string.IsNullOrEmpty(it.Title)) it.Title = source;
            if (!string.IsNullOrEmpty(url)) it.Url = url;
            it.Updated = DateTime.Now;
            Trim();
        }
        Save();
        Refresh();
        Action c = Changed;
        if (c != null) c();
        if (changed && !quiet && (state == "done" || state == "needs"))
        {
            string head = (state == "done" ? "Done \u00B7 " : "Needs you \u00B7 ") + source;
            string body = it.Title;
            ui.BeginInvoke((Action)delegate { tray.ShowBalloonTip(6000, head, body, ToolTipIcon.None); });
            Action<TaskItem> a = Alerted;
            if (a != null) a(it);
        }
    }

    void Trim()
    {
        if (items.Count <= 60) return;
        List<TaskItem> all = new List<TaskItem>(items.Values);
        all.Sort(delegate (TaskItem a, TaskItem b) { return a.Updated.CompareTo(b.Updated); });
        for (int i = 0; i < all.Count - 60; i++) items.Remove(all[i].Key);
    }

    public List<TaskItem> Snapshot()
    {
        lock (gate)
        {
            // A web tab that stopped reporting mid-task was closed or crashed.
            foreach (TaskItem it in items.Values)
                if ((it.State == "working" && it.Source.EndsWith("in Brave") && (DateTime.Now - it.Updated).TotalMinutes > 3)
                    || (it.State == "hidden" && (DateTime.Now - it.Updated).TotalHours > 3))
                    it.State = "lost";
            List<TaskItem> all = new List<TaskItem>(items.Values);
            all.Sort(delegate (TaskItem a, TaskItem b) { return b.Updated.CompareTo(a.Updated); });
            return all;
        }
    }

    public void ClearFinished()
    {
        lock (gate)
        {
            List<string> gone = new List<string>();
            foreach (TaskItem it in items.Values) if (it.State != "working" && it.State != "needs" && it.State != "hidden") gone.Add(it.Key);
            foreach (string k in gone) items.Remove(k);
        }
        Save();
        Refresh();
        Action c = Changed;
        if (c != null) c();
    }

    void Save()
    {
        // Written beside the store and swapped in, so a companion stopped
        // mid-write leaves the old list rather than a truncated one.
        try
        {
            lock (gate)
            {
                string tmp = store + ".tmp";
                File.WriteAllText(tmp, json.Serialize(new List<TaskItem>(items.Values)));
                if (File.Exists(store)) File.Replace(tmp, store, null); else File.Move(tmp, store);
            }
        }
        catch { }
    }

    void Load()
    {
        try
        {
            if (!File.Exists(store)) return;
            foreach (TaskItem it in json.Deserialize<List<TaskItem>>(File.ReadAllText(store)))
            {
                // Anything still "working" from before a restart is unknown now.
                if (it.State == "working" || it.State == "hidden") it.State = "lost";
                // The serializer stores UTC and hands it back as UTC.
                it.Started = it.Started.ToLocalTime();
                it.Updated = it.Updated.ToLocalTime();
                items[it.Key] = it;
            }
        }
        catch { }
    }

    /* -------------------------------------------------------------- *
     * The window
     * -------------------------------------------------------------- */

    // Somewhere the list must keep clear of, such as the desk pet while it
    // is on screen. Empty when there is nothing to avoid.
    public Func<Rectangle> Avoid;

    public void Show() { Show(null); }

    // at: where the click that opened it landed, on the taskbar button or
    // the tray icon, so the list opens just above it.
    public void Show(Point? at)
    {
        ui.BeginInvoke((Action)delegate
        {
            if (form == null || form.IsDisposed) form = new BoardForm(this);
            form.Anchor_ = at;
            form.Fill(Snapshot());
            form.Show();
            form.WindowState = FormWindowState.Normal;
            form.Activate();
            Action s = Shown;
            if (s != null) s();
        });
    }

    void Refresh()
    {
        if (form == null) return;
        ui.BeginInvoke((Action)delegate { if (form != null && !form.IsDisposed && form.Visible) form.Fill(Snapshot()); });
    }

    // Bring forward the app a task belongs to.
    public static string ProcessFor(string app)
    {
        return app == "claude" ? "claude" : app == "chatgpt" ? "ChatGPT" : app == "brave" ? "brave" : null;
    }

    // Open the conversation a task belongs to, as closely as each app allows.
    //
    //   Brave        the window whose tab shows that chat, or else the chat's
    //                address opened in Brave
    //   Codex, Work  the thread itself, through the app's codex://threads/ link
    //   apps         the app's window, started if it was closed to the tray;
    //                a plain ChatGPT chat or a Claude app chat cannot be named,
    //                because neither app says which one it was
    public void Open(TaskItem it)
    {
        if (it == null) return;
        string url = it.Url ?? "";
        try
        {
            if (it.App == "brave")
            {
                IntPtr w = WindowTitled("brave", it.Title);
                if (w != IntPtr.Zero) { Front(w); return; }
                string brave = BravePath();
                string safe = ChatAddress(url);
                // "--" ends Brave's switches, so nothing in the address can be
                // read as one; the address itself is a checked, encoded URI.
                if (safe != null && brave != null) { Process.Start(brave, "-- \"" + safe + "\""); return; }
            }
            else if (CodexLink.IsMatch(url))
            {
                Process.Start(url);
                return;
            }
        }
        catch (Exception e) { Log("open " + it.Key + ": " + e.Message); }

        string proc = ProcessFor(it.App);
        if (proc == null) return;
        // A process can exit between being listed and being asked for its
        // window; that must not take the companion down with it.
        foreach (Process p in Process.GetProcessesByName(proc))
        {
            try
            {
                if (p.MainWindowHandle == IntPtr.Zero) continue;
                Front(p.MainWindowHandle);
                return;
            }
            catch { }
        }
        // No window: closed to the tray. Each app's own link brings it back.
        try
        {
            if (proc == "ChatGPT") Process.Start("codex://launch");
            else if (proc == "claude") Process.Start("claude://claude.ai/new");
        }
        catch { }
    }

    // A task's address reaches the board from outside it, so before it goes
    // anywhere near a command line it must be an https address on one of the
    // two chat sites, in the encoded form Uri gives it, with no quotes or
    // spaces left in it.
    static readonly string[] ChatHosts = { "claude.ai", "chatgpt.com", "chat.openai.com" };
    static readonly Regex CodexLink = new Regex(@"^codex://threads/[0-9a-fA-F-]{36}$");

    static string ChatAddress(string url)
    {
        Uri u;
        if (!Uri.TryCreate(url, UriKind.Absolute, out u) || u.Scheme != Uri.UriSchemeHttps) return null;
        if (Array.IndexOf(ChatHosts, u.Host.ToLowerInvariant()) < 0 || !u.IsDefaultPort) return null;
        string s = u.AbsoluteUri;
        foreach (char c in s) if (c == '"' || char.IsWhiteSpace(c) || char.IsControl(c)) return null;
        return s;
    }

    static void Front(IntPtr w)
    {
        if (IsIconic(w)) ShowWindow(w, 9);
        SetForegroundWindow(w);
    }

    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);

    // A top-level window of the app whose title begins with the chat's name,
    // which in a browser means the chat is that window's open tab.
    static IntPtr WindowTitled(string proc, string title)
    {
        if (string.IsNullOrEmpty(title)) return IntPtr.Zero;
        // A long title was cut short with an ellipsis; match on what is left.
        title = title.TrimEnd('\u2026').TrimEnd();
        if (title.Length < 4) return IntPtr.Zero;
        HashSet<uint> pids = new HashSet<uint>();
        foreach (Process p in Process.GetProcessesByName(proc)) pids.Add((uint)p.Id);
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate (IntPtr h, IntPtr l)
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (!pids.Contains(pid) || !IsWindowVisible(h)) return true;
            StringBuilder s = new StringBuilder(512);
            GetWindowText(h, s, 512);
            if (s.ToString().StartsWith(title, StringComparison.OrdinalIgnoreCase)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static string BravePath()
    {
        try
        {
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\brave.exe"))
                if (k != null)
                {
                    string p = k.GetValue("") as string;
                    if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
                }
        }
        catch { }
        string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"BraveSoftware\Brave-Browser\Application\brave.exe");
        return File.Exists(fallback) ? fallback : null;
    }

    /* -------------------------------------------------------------- *
     * The receiver: plain HTTP on loopback, read by hand so no URL
     * reservation or admin rights are needed.
     * -------------------------------------------------------------- */

    void Listen()
    {
        // A copy that is still closing, after a restart or a crash, can hold
        // the port for a few seconds. Keep trying for fifteen before giving up.
        for (int tries = 1; ; tries++)
        {
            try
            {
                listener = new TcpListener(IPAddress.Loopback, Port);
                listener.Start();
                break;
            }
            catch (Exception e)
            {
                if (!running) return;
                if (tries >= 15)
                {
                    Log("board could not listen on 127.0.0.1:" + Port + ": " + e.Message);
                    return;
                }
                Thread.Sleep(1000);
            }
        }
        while (running)
        {
            TcpClient c;
            try { c = listener.AcceptTcpClient(); } catch { if (!running) return; continue; }
            ThreadPool.QueueUserWorkItem(delegate { Serve(c); });
        }
    }

    void Serve(TcpClient c)
    {
        try
        {
            using (c)
            using (NetworkStream s = c.GetStream())
            {
                s.ReadTimeout = 3000;
                byte[] buf = new byte[65536];
                int got = 0, headEnd = -1;
                while (headEnd < 0 && got < buf.Length)
                {
                    int n = s.Read(buf, got, buf.Length - got);
                    if (n <= 0) break;
                    got += n;
                    headEnd = IndexOf(buf, got, "\r\n\r\n");
                }
                if (headEnd < 0) return;
                string head = Encoding.ASCII.GetString(buf, 0, headEnd);
                string[] lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
                string[] first = lines[0].Split(' ');
                string method = first[0], path = first.Length > 1 ? first[1] : "/";
                int length = 0; bool ours = false;
                string host = "", origin = "";
                foreach (string l in lines)
                {
                    int i = l.IndexOf(':');
                    if (i < 0) continue;
                    string k = l.Substring(0, i).Trim().ToLowerInvariant(), v = l.Substring(i + 1).Trim();
                    if (k == "content-length") int.TryParse(v, out length);
                    if (k == "x-prompt-advisor" && v == "1") ours = true;
                    if (k == "host") host = v.ToLowerInvariant();
                    if (k == "origin") origin = v.ToLowerInvariant();
                }
                // Checked before any body is read: a request that is not ours,
                // or larger than any real report, gets no memory at all.
                bool localHost = host == "127.0.0.1:" + Port || host == "localhost:" + Port;
                bool okOrigin = origin.Length == 0 || origin.StartsWith("chrome-extension://");
                if (!localHost || !okOrigin) ours = false;
                if (length < 0 || length > MaxBody || (method == "POST" && !ours))
                {
                    string refused = length > MaxBody ? "413 Payload Too Large" : "403 Forbidden";
                    BoardLog(method + " " + path + " host=" + host + " origin=" + (origin.Length > 0 ? origin : "-") + " length=" + length + " -> " + refused);
                    byte[] no = Encoding.ASCII.GetBytes("HTTP/1.1 " + refused + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    s.Write(no, 0, no.Length);
                    return;
                }
                int bodyStart = headEnd + 4;
                byte[] body = new byte[Math.Max(0, length)];
                int have = Math.Min(length, got - bodyStart);
                if (have > 0) Array.Copy(buf, bodyStart, body, 0, have);
                while (have < length)
                {
                    int n = s.Read(body, have, length - have);
                    if (n <= 0) break;
                    have += n;
                }
                // DNS rebinding: a site can point its own name at 127.0.0.1,
                // and then its pages count as same-origin and may send the
                // custom header. Such a request still names the site in Host
                // and Origin, so only this machine's own names are accepted,
                // and only the extension, or no browser at all, as the origin.
                // (Checked above, before the body.)
                string status = "204 No Content", answer = "";
                if (method == "GET" && path == "/ping") status = localHost ? "200 OK" : "403 Forbidden";
                else if (method == "GET" && path == "/claude-state" && ours) { status = "200 OK"; answer = ClaudeState(); }
                else if (method == "POST" && path == "/show" && ours) Show();
                else if (method != "POST" || !ours) status = "403 Forbidden";
                else Route(path, Encoding.UTF8.GetString(body, 0, have));
                // One line per request, so a report that never shows up can be
                // traced: no line means it never reached the board. The
                // extension's half-minute question is left out.
                if (path != "/claude-state" || status != "200 OK")
                    BoardLog(method + " " + path + " host=" + host + " origin=" + (origin.Length > 0 ? origin : "-") + " -> " + status);
                byte[] bytes = Encoding.UTF8.GetBytes(answer);
                byte[] reply = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\n"
                    + (bytes.Length > 0 ? "Content-Type: application/json\r\n" : "")
                    + "Content-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n");
                s.Write(reply, 0, reply.Length);
                if (bytes.Length > 0) s.Write(bytes, 0, bytes.Length);
            }
        }
        catch { }
    }

    static int IndexOf(byte[] b, int len, string marker)
    {
        byte[] m = Encoding.ASCII.GetBytes(marker);
        for (int i = 0; i + m.Length <= len; i++)
        {
            bool hit = true;
            for (int j = 0; j < m.Length && hit; j++) hit = b[i + j] == m[j];
            if (hit) return i;
        }
        return -1;
    }

    static string Str(Dictionary<string, object> d, string k)
    {
        object v;
        return d != null && d.TryGetValue(k, out v) && v != null ? v.ToString() : "";
    }

    static string Short(string s, int n)
    {
        s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        return s.Length > n ? s.Substring(0, n - 1) + "\u2026" : s;
    }

    void Route(string path, string body)
    {
        Dictionary<string, object> d = null;
        try { d = json.Deserialize<Dictionary<string, object>>(body); } catch { }
        if (d == null) d = new Dictionary<string, object>();

        if (path == "/event")
        {
            // From the Brave extension: { source, id, title, state, url, visible }
            string source = Str(d, "source");
            // claude.ai's own view of a chat, from the extension's background
            // check. It is matched against the screen watcher's rows, so it is
            // handled on the watcher's thread.
            if (source == "claude-account") { accountQueue.Enqueue(d); return; }
            string label = source == "claude-web" ? "Claude in Brave" : source == "chatgpt-web" ? "ChatGPT in Brave" : source;
            bool visible = Str(d, "visible").ToLowerInvariant() == "true";
            Upsert("web:" + Str(d, "id"), label, Short(Str(d, "title"), 70), Str(d, "state"), Str(d, "url"), "brave", visible);
            return;
        }

        if (path.StartsWith("/hook/claude-code/"))
        {
            // Claude Code hook input: session_id, cwd, prompt (on submit),
            // message (on notification).
            string what = path.Substring("/hook/claude-code/".Length);
            string sid = Str(d, "session_id");
            if (sid.Length == 0) return;
            string title;
            // The project folder goes beside the source, so two sessions
            // asked the same thing in different projects can be told apart.
            // The desktop app's own scratch folders mean no project at all.
            string folder = Path.GetFileName(Str(d, "cwd").TrimEnd('\\', '/'));
            string source = folder.Length > 0 && !folder.StartsWith("scratch-") ? "Claude Code \u00B7 " + folder : "Claude Code";
            lock (gate)
            {
                string asked = what == "start" ? CleanPrompt(Str(d, "prompt")) : "";
                if (asked.Length > 0) sessionTitles[sid] = Short(asked, 70);
                if (!sessionTitles.TryGetValue(sid, out title)) title = folder.Length > 0 ? folder : "Session";
            }
            string state = what == "start" ? "working" : what == "needs" ? "needs" : "done";
            if (what == "needs" && Str(d, "message").Length > 0) title = title + " \u00B7 " + Short(Str(d, "message"), 50);
            Upsert("cc:" + sid, source, title, state, "", "claude", false);
            if (what != "start") claudeCodeSaidAt = DateTime.Now;
            string transcript = TranscriptPath(Str(d, "transcript_path"));
            if (transcript != null) lock (gate) transcripts["cc:" + sid] = new Transcript(transcript);
            return;
        }

        if (path == "/hook/codex")
        {
            // Codex notify: type agent-turn-complete, thread-id, input-messages.
            string tid = Str(d, "thread-id");
            string title = "";
            object msgs;
            if (d.TryGetValue("input-messages", out msgs) && msgs is System.Collections.ArrayList)
            {
                System.Collections.ArrayList list = (System.Collections.ArrayList)msgs;
                if (list.Count > 0 && list[0] != null) title = Short(list[0].ToString(), 70);
            }
            Upsert("codex:" + (tid.Length > 0 ? tid : DateTime.Now.Ticks.ToString()), "Codex", title, "done", "", "chatgpt", false);
        }
    }

    /* -------------------------------------------------------------- *
     * The desktop apps: a Stop button on screen means a reply is
     * running. Its disappearing means the reply finished.
     *
     * Both apps are Chromium inside, and Chromium stops updating what it
     * exposes while its window is minimized or hidden behind others: the
     * Stop button stays frozen in place, or the page drops out of the tree.
     * So a run is only judged while the app can be seen. Hidden mid-run,
     * it is marked "hidden" until the window comes back.
     * -------------------------------------------------------------- */

    class AppWatch { public string Key; public bool Running, Blind, Away; public DateTime Since, BackAt, LastScan; }
    readonly Dictionary<string, AppWatch> watches = new Dictionary<string, AppWatch>();

    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);

    void WatchApps()
    {
        while (running)
        {
            try { if (Due("claude")) WatchOne("claude", "Claude app"); } catch { }
            try { ScanClaudeToasts(); } catch (Exception e) { Log("claude toasts: " + e.Message); }
            try { Dictionary<string, object> d; while (accountQueue.TryDequeue(out d)) AccountEvent(d); } catch (Exception e) { Log("claude account: " + e.Message); }
            // Codex before ChatGPT, so a Codex or Work run the files already
            // know about is not listed a second time from the app's screen.
            try { ScanCodex(); } catch (Exception e) { Log("codex watch: " + e.Message); }
            try { if (Due("ChatGPT")) WatchOne("ChatGPT", "ChatGPT app"); } catch { }
            try { CheckTranscripts(); } catch (Exception e) { Log("transcripts: " + e.Message); }
            Thread.Sleep(2000);
        }
    }

    /* -------------------------------------------------------------- *
     * Claude Code turns stopped with Esc. Claude Code sends no hook when
     * you interrupt a turn, so its row would stay "working" until your
     * next prompt. A turn that is working keeps writing to its transcript,
     * so a transcript that has not grown in 20 minutes means the turn was
     * stopped, or the session closed.
     * -------------------------------------------------------------- */

    class Transcript
    {
        public readonly string Path;
        public long Length = -1;
        public DateTime GrewAt = DateTime.UtcNow;
        public Transcript(string path) { Path = path; }
    }

    readonly Dictionary<string, Transcript> transcripts = new Dictionary<string, Transcript>();

    // The path arrives in a request, so it is only ever opened when it is a
    // .jsonl file on a local drive inside a folder Claude Code writes its
    // transcripts to. A network path such as \\server\share would make
    // Windows offer your sign-in to that server just by opening it.
    static string TranscriptPath(string raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.StartsWith(@"\\") || raw.StartsWith("//")) return null;
        string full;
        try
        {
            if (!Path.IsPathRooted(raw) || raw.IndexOf(':') != 1) return null;
            full = Path.GetFullPath(raw);
        }
        catch { return null; }
        if (full.StartsWith(@"\\") || !full.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return null;
        string[] roots =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude") + @"\",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Claude-Data") + @"\",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude") + @"\"
        };
        foreach (string root in roots)
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return full;
        return null;
    }
    DateTime transcriptsCheckedAt = DateTime.MinValue;

    void CheckTranscripts()
    {
        if ((DateTime.UtcNow - transcriptsCheckedAt).TotalSeconds < 30) return;
        transcriptsCheckedAt = DateTime.UtcNow;
        List<KeyValuePair<string, Transcript>> open = new List<KeyValuePair<string, Transcript>>();
        lock (gate)
            foreach (KeyValuePair<string, Transcript> t in transcripts)
            {
                TaskItem it;
                if (items.TryGetValue(t.Key, out it) && it.State == "working") open.Add(t);
            }
        foreach (KeyValuePair<string, Transcript> t in open)
        {
            long len;
            try
            {
                // Opened, not just listed: Windows is slow to update a size
                // it lists for a file another program holds open.
                using (FileStream s = new FileStream(t.Value.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    len = s.Length;
            }
            catch { len = -2; }
            if (len != t.Value.Length) { t.Value.Length = len; t.Value.GrewAt = DateTime.UtcNow; continue; }
            if ((DateTime.UtcNow - t.Value.GrewAt).TotalMinutes < 20) continue;
            TaskItem row;
            lock (gate) items.TryGetValue(t.Key, out row);
            if (row != null && row.State == "working") Upsert(t.Key, null, null, "lost", "", "claude", true);
        }
    }

    // Reading an app's screen costs it and us a little each time, so it is
    // read every 2 seconds only while a run is going or you are in that app,
    // which is when one starts; otherwise every 6.
    bool Due(string proc)
    {
        AppWatch w;
        if (!watches.TryGetValue(proc, out w)) { w = new AppWatch(); watches[proc] = w; }
        bool due = w.Running || w.Blind || InFront(proc) || (DateTime.Now - w.LastScan).TotalSeconds >= 6;
        if (due) w.LastScan = DateTime.Now;
        return due;
    }

    void WatchOne(string proc, string label)
    {
        Process p = null;
        foreach (Process x in Process.GetProcessesByName(proc))
            if (x.MainWindowHandle != IntPtr.Zero) { p = x; break; }
        if (p == null)
        {
            // The window went away mid-run: closed, or to the tray. A Claude
            // run can still finish on claude.ai, which the extension sees, so
            // it waits as hidden. A ChatGPT chat leaves no trace, so it ends.
            AppWatch gone;
            if (watches.TryGetValue(proc, out gone) && gone.Running && gone.Key != null)
            {
                if (proc == "claude")
                {
                    if (!gone.Blind) { gone.Blind = true; Upsert(gone.Key, label, null, "hidden", "", "claude", true); }
                }
                else
                {
                    gone.Running = false; gone.Blind = false;
                    Upsert(gone.Key, label, null, "lost", "", "chatgpt", true);
                }
            }
            return;
        }
        AutomationElement win = AutomationElement.FromHandle(p.MainWindowHandle);
        string app = proc == "claude" ? "claude" : "chatgpt";

        bool stop = false, codeTab = false, composer = false;
        string mode = "", chat = "";
        Condition isButtonOrEdit = new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
        foreach (AutomationElement e in win.FindAll(TreeScope.Descendants, isButtonOrEdit))
        {
            string n = e.Current.Name ?? "";
            if (e.Current.ControlType == ControlType.Document)
            {
                // The page's title is the open conversation's name, as in
                // "Plan the BCA timetable - Claude". The app's own name alone
                // means a page with no conversation open.
                if (chat.Length == 0) chat = ChatName(n);
                continue;
            }
            if (n == "Stop" || n == "Stop response" || n == "Stop streaming" || n == "Stop generating")
            {
                if (!e.Current.IsOffscreen) stop = true;
            }
            else if (proc == "claude" && n == "Prompt" && e.Current.ControlType == ControlType.Edit && !e.Current.IsOffscreen) codeTab = true;
            else if (proc == "ChatGPT" && n.StartsWith("Switch mode, current mode: ")) mode = n.Substring("Switch mode, current mode: ".Length);
            if (e.Current.ControlType == ControlType.Edit && !e.Current.IsOffscreen) composer = true;
        }
        // The Code tab is Claude Code, which reports through its own hooks
        // with real titles. Codex mode in the ChatGPT app reports through its
        // session files. Watching either here too would list it twice.
        if (codeTab || mode == "Codex") stop = false;

        AppWatch w;
        if (!watches.TryGetValue(proc, out w)) { w = new AppWatch(); watches[proc] = w; }

        // In ChatGPT a hidden page also loses its text box, which is how a
        // window covered by others shows itself. The Claude app's Code tab
        // and the ChatGPT app's Codex mode are other views of the same
        // window: a chat left running behind them is out of sight, not
        // finished, so it waits as hidden until you come back to it.
        bool seen = !IsIconic(p.MainWindowHandle) && (proc != "ChatGPT" || composer) && !codeTab && mode != "Codex";
        if (proc == "claude") claudeChatShown = seen;
        if (!seen)
        {
            w.Away = true;
            if (w.Running && !w.Blind && w.Key != null)
            {
                w.Blind = true;
                Upsert(w.Key, label, null, "hidden", "", app, true);
            }
            return;
        }
        if (w.Away) { w.Away = false; w.BackAt = DateTime.Now; }
        if (w.Blind)
        {
            w.Blind = false;
            if (stop && w.Key != null) Upsert(w.Key, label, null, "working", "", app, true);
        }

        // A window coming back can show the Stop button it froze with for a
        // moment before it catches up. A start seen in that moment is not one.
        if (stop && !w.Running && (DateTime.Now - w.BackAt).TotalSeconds < 4) return;

        if (stop && !w.Running)
        {
            w.Running = true;
            w.Since = DateTime.Now;
            w.Key = null;
            // A Work run in ChatGPT shows the same Stop button; its session
            // file has already said so.
            if (proc == "ChatGPT" && CodexBusy()) return;
            w.Key = "app:" + proc + ":" + DateTime.Now.Ticks;
            string title = chat.Length > 0 ? chat
                : (mode.Length > 0 ? mode + " · " : "New chat · ") + "started " + DateTime.Now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            Upsert(w.Key, label, title, "working", "", app, false);
        }
        else if (!stop && w.Running)
        {
            w.Running = false;
            // A new chat is named by the app once its first reply is in, so
            // the name is read again at the end. Quiet when you are already
            // looking at that app.
            if (w.Key != null) Upsert(w.Key, label, chat.Length > 0 ? chat : null, "done", "", app, proc == "claude" ? ChatInView() : InFront(proc));
            if (proc == "claude") claudeSeenDone = DateTime.Now;
        }
    }

    static string ChatName(string page)
    {
        foreach (string tail in new[] { " - Claude Code", " - Claude", " - ChatGPT", " | ChatGPT" })
            if (page.EndsWith(tail)) { page = page.Substring(0, page.Length - tail.Length); break; }
        page = page.Trim();
        return page == "Claude" || page == "ChatGPT" || page == "New chat" || page == "Claude Code" ? "" : Short(page, 70);
    }

    static bool InFront(string proc)
    {
        try
        {
            uint pid;
            GetWindowThreadProcessId(GetForegroundWindow(), out pid);
            return string.Equals(Process.GetProcessById((int)pid).ProcessName, proc, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /* -------------------------------------------------------------- *
     * Codex and Work in the ChatGPT app, and the Codex CLI. Each run is
     * written to ~/.codex/sessions as it happens: a task_started line when
     * it begins and task_complete when it ends. The files keep growing
     * while the app's window is minimized, when its screen does not.
     * -------------------------------------------------------------- */

    class Follow { public long Offset; public string Id = "", Label = "Codex", Key; public bool Running; public DateTime StartedAt = DateTime.MinValue; }
    readonly Dictionary<string, Follow> follows = new Dictionary<string, Follow>(StringComparer.OrdinalIgnoreCase);
    bool codexPrimed;
    static readonly Regex EventType = new Regex("\"type\":\"event_msg\",\"payload\":\\{\"type\":\"([a-z_]+)\"");

    void ScanCodex()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        string sessions = Path.Combine(root, "sessions");
        if (!Directory.Exists(sessions)) return;

        // The app keeps a lock file for each thread it writes. A thread
        // resumed from days ago is followed through its lock, because
        // Windows can be slow to move a file's write time on.
        HashSet<string> locked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string locks = Path.Combine(root, "thread-writer-locks");
        if (Directory.Exists(locks))
            foreach (string l in Directory.GetFiles(locks, "*.lock")) locked.Add(Path.GetFileNameWithoutExtension(l));

        DateTime since = DateTime.Now.AddDays(-2);
        foreach (string f in Directory.GetFiles(sessions, "rollout-*.jsonl", SearchOption.AllDirectories))
        {
            Follow fo;
            if (!follows.TryGetValue(f, out fo))
            {
                string name = Path.GetFileNameWithoutExtension(f);
                string id = name.Length > 36 ? name.Substring(name.Length - 36) : "";
                if (File.GetLastWriteTime(f) < since && !locked.Contains(id)) continue;
                fo = new Follow();
                follows[f] = fo;
            }
            ReadNew(root, f, fo);
        }

        if (!codexPrimed)
        {
            // First pass after a start: runs that ended before now are not
            // news, but one still going is.
            codexPrimed = true;
            foreach (Follow fo in follows.Values)
                if (fo.Running && fo.Key != null && (DateTime.Now - fo.StartedAt).TotalHours < 3)
                    Upsert(fo.Key, fo.Label, CodexTitle(root, fo), "working", "codex://threads/" + fo.Id, "chatgpt", true);
        }
    }

    void ReadNew(string root, string f, Follow fo)
    {
        using (FileStream s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            long len = s.Length;
            if (len <= fo.Offset) return;
            s.Seek(fo.Offset, SeekOrigin.Begin);
            byte[] buf = new byte[len - fo.Offset];
            int got = 0;
            while (got < buf.Length)
            {
                int n = s.Read(buf, got, buf.Length - got);
                if (n <= 0) break;
                got += n;
            }
            if (got == 0) return;
            // Only whole lines; a half-written one waits for the next pass.
            int end = Array.LastIndexOf(buf, (byte)'\n', got - 1);
            if (end < 0) return;
            fo.Offset += end + 1;
            foreach (string line in Encoding.UTF8.GetString(buf, 0, end + 1).Split('\n'))
                HandleCodexLine(root, fo, line);
        }
    }

    void HandleCodexLine(string root, Follow fo, string line)
    {
        if (line.Length == 0) return;
        if (fo.Id.Length == 0 && line.Contains("\"type\":\"session_meta\""))
        {
            fo.Id = JsonField(line, "id");
            string origin = JsonField(line, "originator");
            fo.Label = origin == "codex_work_desktop" ? "ChatGPT Work" : origin == "Codex Desktop" ? "Codex" : "Codex CLI";
            return;
        }
        Match m = EventType.Match(line);
        if (!m.Success) return;
        string type = m.Groups[1].Value;
        DateTime at;
        if (!DateTime.TryParse(JsonField(line, "timestamp"), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out at))
            at = DateTime.UtcNow;
        at = DateTime.SpecifyKind(at, DateTimeKind.Utc).ToLocalTime();

        if (type == "task_started")
        {
            fo.Running = true;
            fo.StartedAt = at;
            fo.Key = "cx:" + fo.Id + ":" + at.Ticks;
            if (!codexPrimed) return;
            AdoptChatGptRun();
            Upsert(fo.Key, fo.Label, CodexTitle(root, fo), "working", "codex://threads/" + fo.Id, "chatgpt", false);
        }
        else if (type == "task_complete" || type == "turn_aborted")
        {
            fo.Running = false;
            // A run you stopped yourself is not news.
            if (codexPrimed && fo.Key != null)
                Upsert(fo.Key, fo.Label, CodexTitle(root, fo), "done", "codex://threads/" + fo.Id, "chatgpt", type == "turn_aborted" || InFront("ChatGPT"));
        }
        else if (type.Contains("approval_request") || type.StartsWith("request_user_input") || type.Contains("elicitation"))
        {
            if (codexPrimed && fo.Key != null)
                Upsert(fo.Key, fo.Label, CodexTitle(root, fo), "needs", "codex://threads/" + fo.Id, "chatgpt", InFront("ChatGPT"));
        }
    }

    bool CodexBusy()
    {
        foreach (Follow fo in follows.Values)
            if (fo.Running || (DateTime.Now - fo.StartedAt).TotalSeconds < 15) return true;
        return false;
    }

    // The app's screen saw a Stop button before the session file said a
    // Work run had begun: the file's entry, with its real title, replaces it.
    void AdoptChatGptRun()
    {
        AppWatch w;
        if (!watches.TryGetValue("ChatGPT", out w) || !w.Running || w.Key == null) return;
        if ((DateTime.Now - w.Since).TotalSeconds > 20) return;
        lock (gate) items.Remove(w.Key);
        w.Key = null;
        Save();
        Refresh();
    }

    // The thread's name from session_index.jsonl, which the app writes a
    // few seconds into a new thread; until then, when it started.
    string CodexTitle(string root, Follow fo)
    {
        try
        {
            string index = Path.Combine(root, "session_index.jsonl");
            string marker = "\"id\":\"" + fo.Id + "\"";
            string found = null;
            using (FileStream s = new FileStream(index, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader r = new StreamReader(s, Encoding.UTF8))
            {
                string line;
                while ((line = r.ReadLine()) != null) if (line.Contains(marker)) found = line;
            }
            if (found != null)
            {
                Dictionary<string, object> d = json.Deserialize<Dictionary<string, object>>(found);
                string name = Str(d, "thread_name");
                if (name.Length > 0) return Short(name, 70);
            }
        }
        catch { }
        return fo.Label + " · started " + fo.StartedAt.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    }

    /* -------------------------------------------------------------- *
     * Claude app chats that finish out of sight. The app's screen
     * freezes while its window is minimized or covered, but it still
     * posts a Windows notification named after the chat, and Windows
     * keeps those in wpndatabase.db. That is read with winsqlite3.dll,
     * the SQLite that ships inside Windows, read-only.
     * -------------------------------------------------------------- */

    static class Sqlite
    {
        [DllImport("winsqlite3.dll")] public static extern int sqlite3_open_v2(byte[] file, out IntPtr db, int flags, IntPtr vfs);
        [DllImport("winsqlite3.dll")] public static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport("winsqlite3.dll")] public static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr stmt, IntPtr tail);
        [DllImport("winsqlite3.dll")] public static extern int sqlite3_step(IntPtr stmt);
        [DllImport("winsqlite3.dll")] public static extern long sqlite3_column_int64(IntPtr stmt, int col);
        [DllImport("winsqlite3.dll")] public static extern IntPtr sqlite3_column_blob(IntPtr stmt, int col);
        [DllImport("winsqlite3.dll")] public static extern int sqlite3_column_bytes(IntPtr stmt, int col);
        [DllImport("winsqlite3.dll")] public static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport("winsqlite3.dll")] public static extern int sqlite3_close(IntPtr db);

        static byte[] Z(string s) { return Encoding.UTF8.GetBytes(s + "\0"); }

        // Each row's first column as a number and second as text.
        public static List<KeyValuePair<long, string>> Rows(string file, string sql)
        {
            List<KeyValuePair<long, string>> rows = new List<KeyValuePair<long, string>>();
            IntPtr db, stmt;
            if (sqlite3_open_v2(Z(file), out db, 1 /* SQLITE_OPEN_READONLY */, IntPtr.Zero) != 0)
            {
                sqlite3_close(db);
                throw new IOException("could not open " + file);
            }
            try
            {
                sqlite3_busy_timeout(db, 1000);
                if (sqlite3_prepare_v2(db, Z(sql), -1, out stmt, IntPtr.Zero) != 0) throw new IOException("bad query");
                try
                {
                    while (sqlite3_step(stmt) == 100 /* SQLITE_ROW */)
                    {
                        int n = sqlite3_column_bytes(stmt, 1);
                        byte[] b = new byte[n];
                        if (n > 0) Marshal.Copy(sqlite3_column_blob(stmt, 1), b, 0, n);
                        rows.Add(new KeyValuePair<long, string>(sqlite3_column_int64(stmt, 0), Encoding.UTF8.GetString(b)));
                    }
                }
                finally { sqlite3_finalize(stmt); }
            }
            finally { sqlite3_close(db); }
            return rows;
        }
    }

    long toastSeen = -1;
    DateTime claudeSeenDone = DateTime.MinValue;
    // When a Claude Code hook last reported a turn ending or needing you.
    volatile object claudeCodeSaidAtBox = DateTime.MinValue;
    DateTime claudeCodeSaidAt { get { return (DateTime)claudeCodeSaidAtBox; } set { claudeCodeSaidAtBox = value; } }
    // Session notifications wait a few seconds for a hook to claim them.
    readonly List<KeyValuePair<DateTime, KeyValuePair<long, string>>> heldToasts = new List<KeyValuePair<DateTime, KeyValuePair<long, string>>>();
    static readonly Regex ToastText = new Regex("<text[^>]*>([^<]*)</text>");

    void ScanClaudeToasts()
    {
        string db = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\Windows\Notifications\wpndatabase.db");
        if (!File.Exists(db)) return;
        List<KeyValuePair<long, string>> rows = Sqlite.Rows(db,
            "select n.Id, n.Payload from Notification n join NotificationHandler h on h.RecordId = n.HandlerId " +
            "where h.PrimaryId like 'Claude%!Claude' and n.Type = 'toast' and n.Id > " + Math.Max(0, toastSeen) + " order by n.Id");

        // First pass after a start: what is already there is not news.
        bool primed = toastSeen >= 0;
        foreach (KeyValuePair<long, string> r in rows) toastSeen = Math.Max(toastSeen, r.Key);
        if (toastSeen < 0) toastSeen = 0;
        if (!primed) return;

        // A notification with a session header comes from a Code session or
        // a Cowork task, which look the same in the store. Code sessions
        // also report through their hooks; Cowork tasks do not. So each is
        // held for five seconds, and dropped if a hook spoke in that time.
        DateTime now = DateTime.Now;
        foreach (KeyValuePair<long, string> r in rows)
            if (r.Value.Contains("<header id=\"session-")) heldToasts.Add(new KeyValuePair<DateTime, KeyValuePair<long, string>>(now, r));
        List<KeyValuePair<long, string>> ready = new List<KeyValuePair<long, string>>();
        foreach (KeyValuePair<long, string> r in rows)
            if (!r.Value.Contains("<header id=\"session-")) ready.Add(r);
        for (int i = heldToasts.Count - 1; i >= 0; i--)
        {
            DateTime at = heldToasts[i].Key;
            if ((now - at).TotalSeconds < 5) continue;
            if (Math.Abs((claudeCodeSaidAt - at).TotalSeconds) > 20) ready.Add(heldToasts[i].Value);
            heldToasts.RemoveAt(i);
        }

        foreach (KeyValuePair<long, string> r in ready)
        {
            bool cowork = r.Value.Contains("<header id=\"session-");
            Match m = ToastText.Match(r.Value);
            string title = m.Success ? Short(WebUtility.HtmlDecode(m.Groups[1].Value), 70) : "";
            if (title.Length == 0) title = "Claude app · chat";

            // The screen watcher may have seen this run start, or already
            // seen it end in front of you.
            AppWatch w;
            string key = "toast:claude:" + r.Key;
            if (watches.TryGetValue("claude", out w) && w.Running && w.Key != null)
            {
                key = w.Key;
                w.Running = false;
                w.Blind = false;
            }
            else if ((DateTime.Now - claudeSeenDone).TotalSeconds < 30) continue;
            Upsert(key, cowork ? "Claude Cowork" : "Claude app", title, "done", "", "claude", InFront("claude"));
        }
    }

    /* -------------------------------------------------------------- *
     * Claude chats and Cowork tasks as claude.ai sees them, reported by
     * the extension's background check every 30 seconds. They are the
     * only sign of a run that finishes while the desktop app is out of
     * sight, but a run seen on screen or in a Brave tab is reported there
     * first, and sooner, so the account's report folds into that row.
     * -------------------------------------------------------------- */

    // What the extension asks before it checks claude.ai: is the Claude app
    // open, and is any Claude run going or out of sight. Closed and idle, it
    // does not check at all; open and idle, every two minutes; busy, every
    // half minute.
    string ClaudeState()
    {
        bool app = false, busy = false;
        foreach (Process p in Process.GetProcessesByName("claude"))
            if (p.MainWindowHandle != IntPtr.Zero) { app = true; break; }
        AppWatch w;
        if (watches.TryGetValue("claude", out w) && w.Running) busy = true;
        lock (gate)
            foreach (TaskItem it in items.Values)
                if (it.App == "claude" && !(it.Source ?? "").StartsWith("Claude Code")
                    && (it.State == "working" || it.State == "hidden" || it.State == "needs")
                    && (DateTime.Now - it.Updated).TotalHours < 3)
                { busy = true; break; }
        return "{\"app\":" + (app ? "true" : "false") + ",\"busy\":" + (busy ? "true" : "false") + "}";
    }

    readonly System.Collections.Concurrent.ConcurrentQueue<Dictionary<string, object>> accountQueue =
        new System.Collections.Concurrent.ConcurrentQueue<Dictionary<string, object>>();

    // Whether you can see a Claude chat right now: the Claude app in front
    // and showing a chat, not its Code tab. A finish there needs no alert.
    volatile bool claudeChatShown;
    public bool ChatInView() { return claudeChatShown && InFront("claude"); }

    void AccountEvent(Dictionary<string, object> d)
    {
        string key = "acct:" + Str(d, "id");
        string title = Short(Str(d, "title"), 70);
        string state = Str(d, "state");
        if (state != "working" && state != "needs" && state != "done") return;

        // The run the Claude app's screen watcher is following, if any.
        AppWatch w;
        TaskItem twin = null;
        if (watches.TryGetValue("claude", out w) && w.Running && w.Key != null)
            lock (gate) items.TryGetValue(w.Key, out twin);
        // Or a Brave tab or app row with the same chat name, recently.
        if (twin == null)
            lock (gate)
                foreach (TaskItem it in items.Values)
                    if (!it.Key.StartsWith("acct:") && (it.Source == "Claude app" || it.Source == "Claude in Brave")
                        && string.Equals(it.Title, title, StringComparison.OrdinalIgnoreCase)
                        && (it.State == "working" || it.State == "hidden" || (DateTime.Now - it.Updated).TotalMinutes < 3))
                    { twin = it; break; }

        if (twin != null)
        {
            // Out of sight on screen, finished on claude.ai: this is the news.
            // Hidden means you were not looking at it, so it always alerts.
            if (state == "done" && (twin.State == "working" || twin.State == "hidden"))
            {
                bool quiet = twin.State != "hidden" && ChatInView();
                if (w != null && w.Key == twin.Key) { w.Running = false; w.Blind = false; }
                Upsert(twin.Key, twin.Source, title, "done", "", twin.App, quiet);
            }
            // claude.ai says it is still going, or going again, though the
            // screen had it as finished: believe claude.ai.
            else if ((state == "working" || state == "needs") && twin.State != "working" && twin.State != "hidden" && twin.State != state)
                Upsert(twin.Key, twin.Source, title, state, "", twin.App, state == "working");
            // Either way the twin carries it; a row of the account's own,
            // made before the twin appeared, goes.
            bool dropped;
            lock (gate) dropped = items.Remove(key);
            if (dropped) { Save(); Refresh(); }
            return;
        }

        // Finished on screen in the last minute and a half, in front of you.
        if (state == "done" && (DateTime.Now - claudeSeenDone).TotalSeconds < 90) return;
        Upsert(key, "Claude", title, state, Str(d, "url"), "claude", state == "done" && ChatInView());
    }

    // What you typed, without what the Claude desktop app wraps around it:
    // <system-reminder> context, pasted blocks, and slash-command tags, of
    // which only the command itself is kept.
    static readonly Regex Wrapped = new Regex(
        @"<(?<t>system-reminder|pasted_content|local-command-[a-z-]+|command-message|command-args)\b[^>]*>[\s\S]*?(</\k<t>>|$)",
        RegexOptions.IgnoreCase);

    static string CleanPrompt(string p)
    {
        p = p ?? "";
        Match cmd = Regex.Match(p, @"<command-name>\s*([^<]+?)\s*</command-name>");
        p = Wrapped.Replace(p, " ");
        p = Regex.Replace(p, @"</?[a-zA-Z][\w-]*(\s[^>]*)?>", " ");
        p = Regex.Replace(p, @"\s+", " ").Trim();
        if (p.Length == 0 && cmd.Success) p = cmd.Groups[1].Value;
        return p;
    }

    // The first "name":"value" in a JSON line, without parsing the rest,
    // which in a session's first line runs to tens of kilobytes.
    static string JsonField(string line, string name)
    {
        Match m = Regex.Match(line, "\"" + Regex.Escape(name) + "\":\"((?:[^\"\\\\]|\\\\.)*)\"");
        return m.Success ? Regex.Unescape(m.Groups[1].Value) : "";
    }

    // %TEMP%\PromptAdvisor-board.log, kept under 200 KB.
    static void BoardLog(string line)
    {
        try
        {
            string f = Path.Combine(Path.GetTempPath(), "PromptAdvisor-board.log");
            if (File.Exists(f) && new FileInfo(f).Length > 200000) File.Delete(f);
            File.AppendAllText(f, DateTime.Now.ToString("HH:mm:ss") + "  " + line + Environment.NewLine);
        }
        catch { }
    }

    static void Log(string line)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "PromptAdvisor.log"), DateTime.Now.ToString("s") + "  " + line + Environment.NewLine); } catch { }
    }
}

/* ---------------------------------------------------------------------- *
 * The list: a borderless flyout above the tray, drawn by hand so it can
 * group tasks by what they need from you, turn a live spinner for the ones
 * running, and open a task on a single click. It hides when you click
 * anywhere else, like Windows' own tray flyouts.
 * ---------------------------------------------------------------------- */

public class BoardForm : Form
{
    static readonly Color Bg = Color.FromArgb(24, 24, 27);
    static readonly Color Hover = Color.FromArgb(38, 38, 43);
    static readonly Color Line = Color.FromArgb(46, 46, 52);
    static readonly Color Ink = Color.FromArgb(236, 234, 229);
    static readonly Color Muted = Color.FromArgb(150, 147, 141);
    static readonly Color Faint = Color.FromArgb(108, 106, 101);
    static readonly Color Blue = Color.FromArgb(91, 157, 187);
    static readonly Color Green = Color.FromArgb(127, 163, 127);
    static readonly Color Orange = Color.FromArgb(217, 119, 87);

    // Layout in pixels at 100% scaling.
    const int Wd = 400, MaxHt = 500, MinHt = 240, Head = 76, Foot = 48, RowH = 58, SecH = 32;
    // As tall as its tasks need, between MinHt and MaxHt.
    int Ht = MaxHt;
    // Where the click that opened it landed, or none.
    public Point? Anchor_;

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

    readonly TaskBoard board;
    readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer();
    readonly Stopwatch clock = Stopwatch.StartNew();
    // Sizes in pixels at 100%, so the screen's scaling is applied once, by
    // the transform in OnPaint, and not a second time through point sizes.
    readonly Font title = new Font("Segoe UI Semibold", 17.5f, GraphicsUnit.Pixel);
    readonly Font sub = new Font("Segoe UI", 12f, GraphicsUnit.Pixel);
    readonly Font name = new Font("Segoe UI", 13.5f, GraphicsUnit.Pixel);
    readonly Font meta = new Font("Segoe UI", 11.5f, GraphicsUnit.Pixel);
    readonly Font label = new Font("Segoe UI Semibold", 11.5f, GraphicsUnit.Pixel);

    List<TaskItem> items = new List<TaskItem>();
    readonly List<RectangleF> rowRects = new List<RectangleF>();
    readonly List<TaskItem> rowItems = new List<TaskItem>();
    readonly List<KeyValuePair<float, string>> sections = new List<KeyValuePair<float, string>>();
    float scale = 1f, scroll, contentH;
    int hover = -1;
    bool overClose, overClear;
    int ticks;

    public BoardForm(TaskBoard board)
    {
        this.board = board;
        Text = "Prompt Advisor · Tasks";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        BackColor = Bg;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

        tick.Interval = 50;
        tick.Tick += delegate
        {
            // The spinner turns every frame; times like "4 min ago" move every few seconds.
            ticks++;
            if (Opacity < 1) Opacity = Math.Min(1, Opacity + 0.2);
            if (items.Exists(delegate (TaskItem it) { return it.State == "working"; }) || ticks % 100 == 0) Invalidate();
        };

        Deactivate += delegate { Hide(); };
        FormClosing += delegate (object s, FormClosingEventArgs e) { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Windows 11: rounded corners and a hairline border in the list's own
        // colour. Earlier Windows ignores both and keeps square corners.
        try
        {
            int round = 2;
            DwmSetWindowAttribute(Handle, 33, ref round, 4);
            int border = Line.R | (Line.G << 8) | (Line.B << 16);
            DwmSetWindowAttribute(Handle, 34, ref border, 4);
        }
        catch { }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) { tick.Enabled = false; return; }
        try { scale = Math.Max(1f, Native.GetDpiForWindow(Handle) / 96f); } catch { scale = 1f; }
        Place();
        scroll = 0;
        hover = -1;
        Opacity = 0.2;
        tick.Enabled = true;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keys)
    {
        if (keys == Keys.Escape) { Hide(); return true; }
        return base.ProcessCmdKey(ref msg, keys);
    }

    public void Fill(List<TaskItem> list)
    {
        items = list;
        Arrange();
        Invalidate();
    }

    // Needs you first, then what is running, then everything that ended.
    void Arrange()
    {
        rowRects.Clear(); rowItems.Clear(); sections.Clear();
        float y = 8;
        string[][] groups =
        {
            new[] { "Needs you", "needs" },
            new[] { "Working", "working", "hidden" },
            new[] { "Earlier", "done", "lost" }
        };
        foreach (string[] grp in groups)
        {
            List<TaskItem> these = items.FindAll(delegate (TaskItem it) { return Array.IndexOf(grp, it.State, 1) > 0; });
            if (these.Count == 0) continue;
            sections.Add(new KeyValuePair<float, string>(y, grp[0] + "  ·  " + these.Count));
            y += SecH;
            foreach (TaskItem it in these)
            {
                rowRects.Add(new RectangleF(8, y, Wd - 16, RowH - 4));
                rowItems.Add(it);
                y += RowH;
            }
            y += 4;
        }
        contentH = y;
        Ht = Math.Max(MinHt, Math.Min(MaxHt, (int)Math.Ceiling(Head + contentH + 4 + Foot)));
        scroll = Math.Max(0, Math.Min(scroll, MaxScroll()));
        if (Visible) Place();
    }

    // Just above the taskbar, centred on the button or icon that was
    // clicked, the way Windows opens its own menus from the taskbar. Opened
    // any other way, it sits at the right, above the clock. It keeps clear
    // of the desk pet when the pet is on screen.
    void Place()
    {
        Size = new Size((int)(Wd * scale), (int)(Ht * scale));
        Point at = Anchor_ ?? Cursor.Position;
        Rectangle wa = Screen.FromPoint(at).WorkingArea;
        int gap = (int)(12 * scale);
        bool onTaskbar = Anchor_.HasValue && at.Y >= wa.Bottom;
        int x = onTaskbar ? at.X - Width / 2 : wa.Right - Width - gap;
        x = Math.Max(wa.Left + gap, Math.Min(x, wa.Right - Width - gap));
        int y = wa.Bottom - Height - gap;
        Func<Rectangle> avoid = board.Avoid;
        Rectangle keep = avoid != null ? avoid() : Rectangle.Empty;
        if (!keep.IsEmpty && new Rectangle(x, y, Width, Height).IntersectsWith(keep)) y = keep.Top - Height - gap;
        Location = new Point(x, Math.Max(wa.Top + gap, y));
    }

    float ViewH { get { return Ht - Head - Foot; } }
    float MaxScroll() { return Math.Max(0, contentH - ViewH); }

    /* -------------------------------------------------------------- *
     * Drawing
     * -------------------------------------------------------------- */

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.ScaleTransform(scale, scale);

        DrawHeader(g);

        GraphicsState saved = g.Save();
        g.SetClip(new RectangleF(0, Head, Wd, ViewH));
        g.TranslateTransform(0, Head - scroll);
        if (rowItems.Count == 0) DrawEmpty(g);
        else DrawRows(g);
        g.Restore(saved);

        if (MaxScroll() > 0)
        {
            float track = ViewH - 16, thumb = Math.Max(28, track * ViewH / contentH);
            float at = Head + 8 + (track - thumb) * scroll / MaxScroll();
            using (SolidBrush b = new SolidBrush(Line))
            using (GraphicsPath p = Pill(new RectangleF(Wd - 7, at, 3.5f, thumb)))
                g.FillPath(b, p);
        }

        DrawFooter(g);
    }

    void DrawHeader(Graphics g)
    {
        using (SolidBrush b = new SolidBrush(Ink)) g.DrawString("Tasks", title, b, 18, 16);

        int needs = 0, run = 0, done = 0;
        foreach (TaskItem it in items)
        {
            if (it.State == "needs") needs++;
            else if (it.State == "working") run++;
            else if (it.State == "done" && it.Updated.Date == DateTime.Today) done++;
        }
        List<string> parts = new List<string>();
        if (needs > 0) parts.Add(needs + " need" + (needs == 1 ? "s" : "") + " you");
        if (run > 0) parts.Add(run + " working");
        parts.Add(done + " done today");
        using (SolidBrush b = new SolidBrush(Muted)) g.DrawString(string.Join("  ·  ", parts.ToArray()), sub, b, 19, 44);

        RectangleF x = CloseRect();
        if (overClose) using (SolidBrush b = new SolidBrush(Hover)) g.FillEllipse(b, x);
        using (Pen p = new Pen(overClose ? Ink : Muted, 1.4f))
        {
            float c = x.X + x.Width / 2, m = x.Y + x.Height / 2;
            g.DrawLine(p, c - 4.5f, m - 4.5f, c + 4.5f, m + 4.5f);
            g.DrawLine(p, c + 4.5f, m - 4.5f, c - 4.5f, m + 4.5f);
        }
        using (Pen p = new Pen(Line, 1f)) g.DrawLine(p, 0, Head - 0.5f, Wd, Head - 0.5f);
    }

    void DrawRows(Graphics g)
    {
        using (SolidBrush b = new SolidBrush(Faint))
            foreach (KeyValuePair<float, string> s in sections)
                g.DrawString(s.Value, label, b, 18, s.Key + 10);

        StringFormat cut = new StringFormat(StringFormatFlags.NoWrap);
        cut.Trimming = StringTrimming.EllipsisCharacter;
        StringFormat right = new StringFormat(StringFormatFlags.NoWrap);
        right.Alignment = StringAlignment.Far;

        for (int i = 0; i < rowItems.Count; i++)
        {
            RectangleF r = rowRects[i];
            if (r.Bottom < scroll || r.Top > scroll + ViewH) continue;
            TaskItem it = rowItems[i];
            if (i == hover)
                using (SolidBrush b = new SolidBrush(Hover))
                using (GraphicsPath p = Rounded(r, 8))
                    g.FillPath(b, p);

            DrawState(g, it.State, r.X + 22, r.Y + r.Height / 2);

            float tx = r.X + 44, tw = r.Width - 44 - 78;
            using (SolidBrush b = new SolidBrush(it.State == "lost" ? Muted : Ink))
                g.DrawString(it.Title ?? "", name, b, new RectangleF(tx, r.Y + 8, tw + 60, 20), cut);
            using (SolidBrush b = new SolidBrush(Muted))
                g.DrawString(it.Source ?? "", meta, b, new RectangleF(tx, r.Y + 29, tw, 18), cut);
            using (SolidBrush b = new SolidBrush(it.State == "needs" ? Orange : Faint))
                g.DrawString(When(it), meta, b, new RectangleF(r.Right - 110, r.Y + 29, 100, 18), right);
        }
    }

    // The mark at the left of each row: a turning arc while it runs, a tick
    // when done, an exclamation when it needs you, a dash when it went quiet.
    void DrawState(Graphics g, string state, float cx, float cy)
    {
        RectangleF disc = new RectangleF(cx - 11, cy - 11, 22, 22);
        if (state == "working")
        {
            using (Pen track = new Pen(Color.FromArgb(60, Blue), 2.2f)) g.DrawEllipse(track, cx - 8, cy - 8, 16, 16);
            using (Pen arc = new Pen(Blue, 2.2f))
            {
                arc.StartCap = arc.EndCap = LineCap.Round;
                g.DrawArc(arc, cx - 8, cy - 8, 16, 16, (float)(clock.Elapsed.TotalSeconds * 300 % 360), 100);
            }
            return;
        }
        if (state == "hidden")
        {
            // Still running, as far as anyone knows, but out of sight: a
            // broken ring, standing still.
            using (Pen ring = new Pen(Faint, 2f))
            {
                ring.DashPattern = new float[] { 2f, 1.6f };
                g.DrawEllipse(ring, cx - 8, cy - 8, 16, 16);
            }
            return;
        }
        Color c = state == "done" ? Green : state == "needs" ? Orange : Faint;
        using (SolidBrush b = new SolidBrush(Color.FromArgb(state == "lost" ? 0 : 46, c))) g.FillEllipse(b, disc);
        using (Pen p = new Pen(c, 1.8f))
        {
            p.StartCap = p.EndCap = LineCap.Round;
            p.LineJoin = LineJoin.Round;
            if (state == "done")
                g.DrawLines(p, new PointF[] { new PointF(cx - 4.5f, cy + 0.2f), new PointF(cx - 1.2f, cy + 3.5f), new PointF(cx + 5, cy - 3.5f) });
            else if (state == "needs")
            {
                g.DrawLine(p, cx, cy - 5, cx, cy + 1);
                using (SolidBrush b = new SolidBrush(c)) g.FillEllipse(b, cx - 1.2f, cy + 3.2f, 2.4f, 2.4f);
            }
            else
            {
                g.DrawEllipse(p, disc.X + 1, disc.Y + 1, disc.Width - 2, disc.Height - 2);
                g.DrawLine(p, cx - 4, cy, cx + 4, cy);
            }
        }
    }

    void DrawEmpty(Graphics g)
    {
        float cy = ViewH / 2 - 24;
        using (Pen p = new Pen(Line, 2f)) g.DrawEllipse(p, Wd / 2f - 16, cy - 16, 32, 32);
        using (Pen p = new Pen(Faint, 2f)) g.DrawLine(p, Wd / 2f - 6, cy, Wd / 2f + 6, cy);
        StringFormat mid = new StringFormat();
        mid.Alignment = StringAlignment.Center;
        using (SolidBrush b = new SolidBrush(Ink)) g.DrawString("No tasks yet", name, b, new RectangleF(0, cy + 28, Wd, 22), mid);
        using (SolidBrush b = new SolidBrush(Muted))
            g.DrawString("Tasks from Claude Code, the Claude and ChatGPT apps,\nand Claude or ChatGPT in Brave show up here.", meta, b, new RectangleF(24, cy + 52, Wd - 48, 40), mid);
    }

    void DrawFooter(Graphics g)
    {
        float y = Ht - Foot;
        using (Pen p = new Pen(Line, 1f)) g.DrawLine(p, 0, y + 0.5f, Wd, y + 0.5f);
        bool any = items.Exists(delegate (TaskItem it) { return it.State != "working" && it.State != "needs" && it.State != "hidden"; });
        RectangleF c = ClearRect();
        if (any && overClear)
            using (SolidBrush b = new SolidBrush(Hover))
            using (GraphicsPath p = Rounded(c, 6))
                g.FillPath(b, p);
        using (SolidBrush b = new SolidBrush(any ? (overClear ? Ink : Muted) : Faint))
            g.DrawString("Clear finished", sub, b, c.X + 8, c.Y + 5);
        StringFormat right = new StringFormat();
        right.Alignment = StringAlignment.Far;
        using (SolidBrush b = new SolidBrush(Faint))
            g.DrawString("Click a task to open its app", meta, b, new RectangleF(0, y + 16, Wd - 18, 18), right);
    }

    string When(TaskItem it)
    {
        System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
        if (it.State == "working")
        {
            int m = (int)(DateTime.Now - it.Started).TotalMinutes;
            return m < 1 ? "Started just now" : "Running " + m + " min";
        }
        if (it.State == "hidden") return "Minimized, open to check";
        TimeSpan ago = DateTime.Now - it.Updated;
        if (ago.TotalMinutes < 1) return "Just now";
        if (ago.TotalMinutes < 60) return (int)ago.TotalMinutes + " min ago";
        if (it.Updated.Date == DateTime.Today) return it.Updated.ToString("HH:mm", inv);
        if (it.Updated.Date == DateTime.Today.AddDays(-1)) return "Yesterday";
        return it.Updated.ToString("d MMM", inv);
    }

    RectangleF CloseRect() { return new RectangleF(Wd - 46, 18, 28, 28); }
    RectangleF ClearRect() { return new RectangleF(10, Ht - Foot + 10, 110, 28); }

    static GraphicsPath Rounded(RectangleF r, float rad)
    {
        GraphicsPath p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static GraphicsPath Pill(RectangleF r) { return Rounded(r, r.Width / 2); }

    /* -------------------------------------------------------------- *
     * Mouse
     * -------------------------------------------------------------- */

    PointF Logical(Point p) { return new PointF(p.X / scale, p.Y / scale); }

    int RowAt(PointF p)
    {
        if (p.Y < Head || p.Y > Ht - Foot) return -1;
        PointF q = new PointF(p.X, p.Y - Head + scroll);
        for (int i = 0; i < rowRects.Count; i++) if (rowRects[i].Contains(q)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        PointF p = Logical(e.Location);
        int h = RowAt(p);
        bool c = CloseRect().Contains(p), k = ClearRect().Contains(p);
        if (h != hover || c != overClose || k != overClear)
        {
            hover = h; overClose = c; overClear = k;
            Cursor = h >= 0 || c || k ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hover = -1; overClose = overClear = false;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        PointF p = Logical(e.Location);
        if (CloseRect().Contains(p)) { Hide(); return; }
        if (ClearRect().Contains(p)) { board.ClearFinished(); return; }
        int i = RowAt(p);
        if (i < 0) return;
        Hide();
        board.Open(rowItems[i]);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        scroll = Math.Max(0, Math.Min(MaxScroll(), scroll - e.Delta / 120f * RowH));
        hover = RowAt(Logical(e.Location));
        Invalidate();
    }
}
