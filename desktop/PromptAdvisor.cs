// Prompt Advisor for the Claude and ChatGPT desktop apps.
//
// Both apps are Chromium inside signed Store packages, so nothing can be added
// to them. This reads them from outside instead, through Windows UI
// Automation, the interface screen readers use: the text box you are typing
// in, and the model button beside it. It sends the two to advisor-cli.js,
// which runs the same rules as the browser extension, and draws the answer as
// one line above the text box.
//
// It never types into the apps, never clicks in them, and never touches the
// network.
//
// Why a script and not an exe. Windows 11's Smart App Control blocks any
// unsigned program it has no reputation for, which is every exe built on this
// machine, and it cannot be switched back on once switched off. PowerShell is
// signed by Microsoft, so PromptAdvisor.ps1 compiles this file in memory and
// runs it. Written for the C# 5 compiler that ships with Windows.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Forms;

public static class AdvisorHost
{
    [DllImport("shcore.dll")] static extern int SetProcessDpiAwareness(int value);

    // Called by PromptAdvisor.ps1 on its STA thread. dir is the folder holding
    // advisor-cli.js.
    public static void Run(string dir)
    {
        // Per-monitor aware, so the rectangles UI Automation reports and the
        // position of the hint are in the same pixels on every screen.
        try { SetProcessDpiAwareness(2); } catch { }

        // One copy at a time. A copy that is still shutting down gets a few
        // seconds to let go, so a restart does not quit on its own.
        bool first;
        Mutex one = new Mutex(true, "PromptAdvisorDesktop", out first);
        if (!first)
        {
            try { if (!one.WaitOne(5000)) return; }
            catch (AbandonedMutexException) { }
        }

        Application.EnableVisualStyles();
        Application.Run(new AdvisorContext(dir));
        GC.KeepAlive(one);
    }
}

static class Native
{
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);

    // Store apps refuse Process.MainModule, so the path is asked for with the
    // least access Windows offers.
    public static string ProcessPath(uint pid)
    {
        IntPtr h = OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            StringBuilder sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : "";
        }
        finally { CloseHandle(h); }
    }
}

/* ---------------------------------------------------------------------- *
 * The rules, in a Node process running advisor-cli.js
 * ---------------------------------------------------------------------- */

class Rules
{
    Process node;
    readonly JavaScriptSerializer json = new JavaScriptSerializer();
    readonly string script;

    public Rules(string script) { this.script = script; }

    static string FindNode()
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try
            {
                string p = Path.Combine(dir.Trim(), "node.exe");
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        string fallback = @"C:\Program Files\nodejs\node.exe";
        return File.Exists(fallback) ? fallback : null;
    }

    void Start()
    {
        string exe = FindNode();
        if (exe == null) throw new Exception("Node.js is not installed, and the rules need it.");
        ProcessStartInfo psi = new ProcessStartInfo(exe, "\"" + script + "\"");
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        node = Process.Start(psi);
    }

    public Dictionary<string, object> Ask(string app, string text, string button)
    {
        if (node == null || node.HasExited) Start();
        Dictionary<string, object> req = new Dictionary<string, object>();
        req["app"] = app; req["text"] = text; req["button"] = button;
        byte[] bytes = Encoding.UTF8.GetBytes(json.Serialize(req) + "\n");
        node.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
        node.StandardInput.BaseStream.Flush();
        string line = node.StandardOutput.ReadLine();
        if (line == null) return null;
        return json.Deserialize<Dictionary<string, object>>(line);
    }

    public void Stop()
    {
        try { if (node != null && !node.HasExited) node.Kill(); } catch { }
    }
}

/* ---------------------------------------------------------------------- *
 * The hint
 * ---------------------------------------------------------------------- */

class HintForm : Form
{
    public string Label = "", Verb = "", Why = "", Tone = "ok", Detail = "";
    public float UiScale = 1f;
    readonly ToolTip tip = new ToolTip();

    public HintForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(38, 37, 36);
        Cursor = Cursors.Hand;
        DoubleBuffered = true;
        tip.AutoPopDelay = 20000;
        tip.InitialDelay = 300;
    }

    // Never take focus from the text box you are typing in.
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000008; // NOACTIVATE, TOOLWINDOW, TOPMOST
            return cp;
        }
    }

    Font Bold { get { return new Font("Segoe UI Semibold", 12f * UiScale, GraphicsUnit.Pixel); } }
    Font Plain { get { return new Font("Segoe UI", 12f * UiScale, GraphicsUnit.Pixel); } }

    public void Refit()
    {
        int pad = (int)(9 * UiScale), dot = (int)(7 * UiScale), gap = (int)(8 * UiScale);
        Size a = TextRenderer.MeasureText(Label, Bold, Size.Empty, TextFormatFlags.NoPadding);
        Size b = TextRenderer.MeasureText(Verb, Plain, Size.Empty, TextFormatFlags.NoPadding);
        Size c = TextRenderer.MeasureText(Why, Plain, Size.Empty, TextFormatFlags.NoPadding);
        int w = pad + dot + gap + a.Width + b.Width + gap + c.Width + pad;
        int h = Math.Max(a.Height, c.Height) + (int)(10 * UiScale);
        Size = new Size(w, h);
        Opacity = Tone == "ok" ? 0.72 : 0.97;
        tip.SetToolTip(this, Detail + "\n\nClick to hide for this prompt.");
        Region = new Region(Rounded(new Rectangle(0, 0, w, h), (int)(8 * UiScale)));
        Invalidate();
    }

    static GraphicsPath Rounded(Rectangle r, int radius)
    {
        GraphicsPath p = new GraphicsPath();
        int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int pad = (int)(9 * UiScale), dot = (int)(7 * UiScale), gap = (int)(8 * UiScale);
        Color dotColor = Tone == "down" ? Color.FromArgb(217, 119, 87)
            : Tone == "up" ? Color.FromArgb(91, 157, 187) : Color.FromArgb(127, 163, 127);
        using (SolidBrush br = new SolidBrush(dotColor))
            g.FillEllipse(br, pad, (Height - dot) / 2, dot, dot);
        int x = pad + dot + gap;
        Color ink = Color.FromArgb(230, 228, 223), dim = Color.FromArgb(150, 147, 141);
        Size a = TextRenderer.MeasureText(Label, Bold, Size.Empty, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, Label, Bold, new Point(x, (Height - a.Height) / 2), ink, TextFormatFlags.NoPadding);
        x += a.Width;
        Size b = TextRenderer.MeasureText(Verb, Plain, Size.Empty, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, Verb, Plain, new Point(x, (Height - b.Height) / 2), ink, TextFormatFlags.NoPadding);
        x += b.Width + gap;
        TextRenderer.DrawText(g, Why, Plain, new Point(x, (Height - b.Height) / 2), dim, TextFormatFlags.NoPadding);
    }
}

/* ---------------------------------------------------------------------- *
 * Watching the apps
 * ---------------------------------------------------------------------- */

class AdvisorContext : ApplicationContext
{
    readonly HintForm hint = new HintForm();
    readonly NotifyIcon tray = new NotifyIcon();
    readonly Rules rules;
    readonly Thread worker;
    volatile bool running = true, paused = false;

    // What the worker last saw, so the rules are asked only when it changes.
    IntPtr lastWindow = IntPtr.Zero;
    AutomationElement box, button;
    string lastAsk = "", dismissedFor = null;
    DateTime buttonCheckedAt = DateTime.MinValue;

    public AdvisorContext(string dir)
    {
        rules = new Rules(Path.Combine(dir, "advisor-cli.js"));

        hint.Click += delegate { dismissedFor = lastText; HideHint(); };
        hint.CreateControl();
        IntPtr unused = hint.Handle; // so BeginInvoke works before the first Show

        ContextMenu menu = new ContextMenu();
        MenuItem pause = new MenuItem("Pause");
        pause.Click += delegate { paused = !paused; pause.Text = paused ? "Resume" : "Pause"; if (paused) HideHint(); };
        menu.MenuItems.Add(pause);
        menu.MenuItems.Add("Exit", delegate { Quit(); });
        tray.ContextMenu = menu;
        tray.Text = "Prompt Advisor";
        tray.Icon = LoadIcon(dir);
        tray.Visible = true;

        worker = new Thread(Loop);
        worker.IsBackground = true;
        worker.Start();
    }

    static Icon LoadIcon(string dir)
    {
        try
        {
            string png = Path.Combine(dir, "..", "icons", "icon32.png");
            using (Bitmap bmp = new Bitmap(png)) return Icon.FromHandle(bmp.GetHicon());
        }
        catch { return SystemIcons.Information; }
    }

    void Quit()
    {
        running = false;
        tray.Visible = false;
        rules.Stop();
        ExitThread();
    }

    string lastText = "";

    void HideHint()
    {
        hint.BeginInvoke((Action)delegate { hint.Hide(); });
    }

    static string AppFor(string path)
    {
        if (path.IndexOf(@"\WindowsApps\Claude_", StringComparison.OrdinalIgnoreCase) >= 0) return "claude";
        if (path.IndexOf(@"\WindowsApps\OpenAI.", StringComparison.OrdinalIgnoreCase) >= 0
            && path.EndsWith("ChatGPT.exe", StringComparison.OrdinalIgnoreCase)) return "chatgpt";
        return null;
    }

    static string TextOf(AutomationElement e)
    {
        object p;
        if (e.TryGetCurrentPattern(TextPattern.Pattern, out p))
            return ((TextPattern)p).DocumentRange.GetText(8000);
        if (e.TryGetCurrentPattern(ValuePattern.Pattern, out p))
            return ((ValuePattern)p).Current.Value;
        return "";
    }

    // The model button: "Model: Opus 5.5 High" in Claude, "GPT-5.6 Sol Medium"
    // in ChatGPT. Found once per window and re-read by name after that.
    static AutomationElement FindButton(AutomationElement win, string app)
    {
        Condition isButton = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
        foreach (AutomationElement b in win.FindAll(TreeScope.Descendants, isButton))
        {
            string n = b.Current.Name ?? "";
            if (app == "claude" && n.StartsWith("Model:", StringComparison.OrdinalIgnoreCase)) return b;
            if (app == "chatgpt" && System.Text.RegularExpressions.Regex.IsMatch(n,
                @"^(GPT|o\d).*\s(none|minimal|instant|light|low|medium|high|extra high|max|ultra|persistent)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return b;
        }
        return null;
    }

    void Loop()
    {
        while (running)
        {
            try { Tick(); }
            catch (ElementNotAvailableException) { box = null; button = null; }
            catch (Exception) { }
            Thread.Sleep(350);
        }
    }

    void Tick()
    {
        if (paused) return;
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == hint.Handle) return;
        uint pid;
        Native.GetWindowThreadProcessId(fg, out pid);
        string app = AppFor(Native.ProcessPath(pid));
        if (app == null) { if (hint.Visible) HideHint(); lastWindow = IntPtr.Zero; return; }

        AutomationElement win = AutomationElement.FromHandle(fg);
        if (fg != lastWindow) { lastWindow = fg; box = null; button = null; lastAsk = ""; }

        // The text box is whichever editable field you last focused in this
        // window. Remember it, so opening the model menu does not lose it.
        AutomationElement focused = AutomationElement.FocusedElement;
        if (focused != null && focused.Current.ProcessId == (int)pid
            && focused.Current.ControlType == ControlType.Edit) box = focused;
        if (box == null) { if (hint.Visible) HideHint(); return; }

        if (button == null || (DateTime.Now - buttonCheckedAt).TotalSeconds > 3)
        {
            if (button == null) button = FindButton(win, app);
            buttonCheckedAt = DateTime.Now;
        }

        string text = (TextOf(box) ?? "").Trim();
        string label = button != null ? (button.Current.Name ?? "") : "";
        System.Windows.Rect r = box.Current.BoundingRectangle;
        lastText = text;

        if (dismissedFor != null)
        {
            if (text.Length > 0 && text.StartsWith(dismissedFor.Substring(0, Math.Min(40, dismissedFor.Length)))) return;
            dismissedFor = null;
        }

        string ask = app + "|" + label + "|" + text;
        Dictionary<string, object> res = null;
        if (ask != lastAsk)
        {
            lastAsk = ask;
            res = rules.Ask(app, text, label);
            if (res == null || !(res.ContainsKey("show") && (bool)res["show"])) { HideHint(); return; }
        }

        float scale = Native.GetDpiForWindow(fg) / 96f;
        hint.BeginInvoke((Action)delegate
        {
            if (res != null)
            {
                hint.Label = (string)res["label"];
                hint.Verb = (string)res["verb"];
                hint.Why = (string)res["why"];
                hint.Tone = (string)res["tone"];
                hint.Detail = "You picked " + res["cur"] + "\n" + res["detail"];
                hint.UiScale = scale <= 0 ? 1f : scale;
                hint.Refit();
                if (!hint.Visible) hint.Show();
            }
            if (!hint.Visible) return;
            // Right-aligned: ChatGPT keeps project buttons along the left of
            // the strip above its text box, and the right end is empty.
            int left = (int)r.Right - hint.Width;
            int top = (int)r.Top - hint.Height - (int)(18 * hint.UiScale);
            if (hint.Left != left || hint.Top != top) hint.Location = new Point(left, top);
        });
    }
}
