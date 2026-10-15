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
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
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
            it.State = state;
            if (!string.IsNullOrEmpty(title)) it.Title = title;
            if (string.IsNullOrEmpty(it.Title)) it.Title = source;
            if (!string.IsNullOrEmpty(url)) it.Url = url;
            it.Updated = DateTime.Now;
            Trim();
        }
        Save();
        Refresh();
        if (changed && !quiet && (state == "done" || state == "needs"))
        {
            string head = (state == "done" ? "Done \u00B7 " : "Needs you \u00B7 ") + source;
            string body = it.Title;
            ui.BeginInvoke((Action)delegate { tray.ShowBalloonTip(6000, head, body, ToolTipIcon.None); });
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
                if (it.State == "working" && it.Source.EndsWith("in Brave") && (DateTime.Now - it.Updated).TotalMinutes > 3)
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
            foreach (TaskItem it in items.Values) if (it.State != "working" && it.State != "needs") gone.Add(it.Key);
            foreach (string k in gone) items.Remove(k);
        }
        Save();
        Refresh();
    }

    void Save()
    {
        try { lock (gate) File.WriteAllText(store, json.Serialize(new List<TaskItem>(items.Values))); } catch { }
    }

    void Load()
    {
        try
        {
            if (!File.Exists(store)) return;
            foreach (TaskItem it in json.Deserialize<List<TaskItem>>(File.ReadAllText(store)))
            {
                // Anything still "working" from before a restart is unknown now.
                if (it.State == "working") it.State = "lost";
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

    public void Show()
    {
        ui.BeginInvoke((Action)delegate
        {
            if (form == null || form.IsDisposed) form = new BoardForm(this);
            form.Fill(Snapshot());
            form.Show();
            form.WindowState = FormWindowState.Normal;
            form.Activate();
        });
    }

    void Refresh()
    {
        if (form == null) return;
        ui.BeginInvoke((Action)delegate { if (form != null && !form.IsDisposed && form.Visible) form.Fill(Snapshot()); });
    }

    // Bring forward the app a task belongs to.
    public void Open(TaskItem it)
    {
        if (it == null) return;
        string proc = it.App == "claude" ? "claude" : it.App == "chatgpt" ? "ChatGPT" : it.App == "brave" ? "brave" : null;
        if (proc == null) return;
        foreach (Process p in Process.GetProcessesByName(proc))
        {
            if (p.MainWindowHandle == IntPtr.Zero) continue;
            ShowWindow(p.MainWindowHandle, 9);
            SetForegroundWindow(p.MainWindowHandle);
            return;
        }
    }

    /* -------------------------------------------------------------- *
     * The receiver: plain HTTP on loopback, read by hand so no URL
     * reservation or admin rights are needed.
     * -------------------------------------------------------------- */

    void Listen()
    {
        try
        {
            listener = new TcpListener(IPAddress.Loopback, Port);
            listener.Start();
        }
        catch (Exception e)
        {
            Log("board could not listen on 127.0.0.1:" + Port + ": " + e.Message);
            return;
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
                bool localHost = host == "127.0.0.1:" + Port || host == "localhost:" + Port;
                bool okOrigin = origin.Length == 0 || origin.StartsWith("chrome-extension://");
                if (!localHost || !okOrigin) ours = false;
                string status = "204 No Content";
                if (method == "GET" && path == "/ping") status = localHost ? "200 OK" : "403 Forbidden";
                else if (method == "POST" && path == "/show" && ours) Show();
                else if (method != "POST" || !ours) status = "403 Forbidden";
                else Route(path, Encoding.UTF8.GetString(body, 0, have));
                byte[] reply = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                s.Write(reply, 0, reply.Length);
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
            lock (gate)
            {
                if (what == "start" && Str(d, "prompt").Length > 0) sessionTitles[sid] = Short(Str(d, "prompt"), 70);
                if (!sessionTitles.TryGetValue(sid, out title))
                {
                    string cwd = Str(d, "cwd");
                    title = cwd.Length > 0 ? Path.GetFileName(cwd.TrimEnd('\\', '/')) : "session";
                }
            }
            string state = what == "start" ? "working" : what == "needs" ? "needs" : "done";
            if (what == "needs" && Str(d, "message").Length > 0) title = title + " \u00B7 " + Short(Str(d, "message"), 50);
            Upsert("cc:" + sid, "Claude Code", title, state, "", "claude", false);
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
     * -------------------------------------------------------------- */

    class AppWatch { public string Key; public bool Running; }
    readonly Dictionary<string, AppWatch> watches = new Dictionary<string, AppWatch>();

    void WatchApps()
    {
        while (running)
        {
            try { WatchOne("claude", "Claude app"); } catch { }
            try { WatchOne("ChatGPT", "ChatGPT app"); } catch { }
            Thread.Sleep(2000);
        }
    }

    void WatchOne(string proc, string label)
    {
        Process p = null;
        foreach (Process x in Process.GetProcessesByName(proc))
            if (x.MainWindowHandle != IntPtr.Zero) { p = x; break; }
        if (p == null) return;
        AutomationElement win = AutomationElement.FromHandle(p.MainWindowHandle);

        bool stop = false, codeTab = false;
        string mode = "";
        Condition isButtonOrEdit = new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        foreach (AutomationElement e in win.FindAll(TreeScope.Descendants, isButtonOrEdit))
        {
            string n = e.Current.Name ?? "";
            if (n == "Stop" || n == "Stop response" || n == "Stop streaming" || n == "Stop generating")
            {
                if (!e.Current.IsOffscreen) stop = true;
            }
            else if (proc == "claude" && n == "Prompt" && e.Current.ControlType == ControlType.Edit && !e.Current.IsOffscreen) codeTab = true;
            else if (proc == "ChatGPT" && n.StartsWith("Switch mode, current mode: ")) mode = n.Substring("Switch mode, current mode: ".Length);
        }
        // The Code tab is Claude Code, which reports through its own hooks
        // with real titles. Watching it here too would list it twice.
        if (codeTab) stop = false;

        AppWatch w;
        if (!watches.TryGetValue(proc, out w)) { w = new AppWatch(); watches[proc] = w; }
        if (stop && !w.Running)
        {
            w.Key = "app:" + proc + ":" + DateTime.Now.Ticks;
            w.Running = true;
            Upsert(w.Key, label, label + (mode.Length > 0 ? " \u00B7 " + mode : "") + " \u00B7 started " + DateTime.Now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), "working", "", proc == "claude" ? "claude" : "chatgpt", false);
        }
        else if (!stop && w.Running)
        {
            w.Running = false;
            // Quiet when you are already looking at that app.
            uint fgPid;
            GetWindowThreadProcessId(GetForegroundWindow(), out fgPid);
            Upsert(w.Key, label, null, "done", "", proc == "claude" ? "claude" : "chatgpt", fgPid == (uint)p.Id);
        }
    }

    static void Log(string line)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "PromptAdvisor.log"), DateTime.Now.ToString("s") + "  " + line + Environment.NewLine); } catch { }
    }
}

/* ---------------------------------------------------------------------- *
 * The list
 * ---------------------------------------------------------------------- */

public class BoardForm : Form
{
    readonly TaskBoard board;
    readonly ListView list = new ListView();

    public BoardForm(TaskBoard board)
    {
        this.board = board;
        Text = "Prompt Advisor \u00B7 Tasks";
        Width = 720; Height = 420;
        StartPosition = FormStartPosition.Manual;
        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
        TopMost = true;
        ShowInTaskbar = true;
        BackColor = Color.FromArgb(32, 31, 30);

        list.Dock = DockStyle.Fill;
        list.View = View.Details;
        list.FullRowSelect = true;
        list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        list.BackColor = Color.FromArgb(32, 31, 30);
        list.ForeColor = Color.FromArgb(230, 228, 223);
        list.BorderStyle = BorderStyle.None;
        list.Font = new Font("Segoe UI", 10f);
        list.Columns.Add("", 110);
        list.Columns.Add("Where", 160);
        list.Columns.Add("Task", 320);
        list.Columns.Add("When", 100);
        list.DoubleClick += delegate { if (list.SelectedItems.Count > 0) board.Open((TaskItem)list.SelectedItems[0].Tag); };

        Panel bar = new Panel();
        bar.Dock = DockStyle.Bottom; bar.Height = 36;
        bar.BackColor = Color.FromArgb(38, 37, 36);
        Button clear = new Button();
        clear.Text = "Clear finished";
        clear.FlatStyle = FlatStyle.Flat;
        clear.ForeColor = Color.FromArgb(230, 228, 223);
        clear.SetBounds(8, 5, 120, 26);
        clear.Click += delegate { board.ClearFinished(); };
        Label hint = new Label();
        hint.Text = "Double-click a task to bring its app forward.";
        hint.ForeColor = Color.FromArgb(150, 147, 141);
        hint.AutoSize = true;
        hint.Location = new Point(140, 10);
        bar.Controls.Add(clear);
        bar.Controls.Add(hint);

        Controls.Add(list);
        Controls.Add(bar);
        FormClosing += delegate (object s, FormClosingEventArgs e) { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }

    public void Fill(List<TaskItem> items)
    {
        list.BeginUpdate();
        list.Items.Clear();
        foreach (TaskItem it in items)
        {
            string state = it.State == "working" ? "\u25CF  Working" : it.State == "done" ? "\u2713  Done"
                : it.State == "needs" ? "!  Needs you" : "?  Stopped reporting";
            DateTime when = it.State == "working" ? it.Started : it.Updated;
            System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
            string time = when.Date == DateTime.Today ? when.ToString("HH:mm", inv) : when.ToString("d MMM HH:mm", inv);
            if (it.State == "working") time = Math.Max(1, (int)(DateTime.Now - it.Started).TotalMinutes) + " min";
            ListViewItem row = new ListViewItem(new[] { state, it.Source, it.Title, time });
            row.Tag = it;
            row.ForeColor = it.State == "working" ? Color.FromArgb(91, 157, 187)
                : it.State == "needs" ? Color.FromArgb(217, 119, 87)
                : it.State == "done" ? Color.FromArgb(127, 163, 127) : Color.FromArgb(150, 147, 141);
            list.Items.Add(row);
        }
        list.EndUpdate();
    }
}
