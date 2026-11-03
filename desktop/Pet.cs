// The desk pet: an energy core in a corner of the screen that shows what
// the task board knows without opening it.
//
// It glows dim grey and barely turns while nothing runs. While any Claude or
// ChatGPT task works it turns blue and spins. When one finishes it bursts
// gold, and when one needs you it pulses red, with a bubble naming the task.
// The bubble stays until you click the core, which brings that task's app
// forward. A tray balloon was the only signal before, and Windows hides those
// under Do not disturb and drops them after a few seconds.
//
// By default it only appears when there is something to show: it fades in
// when a task starts or finishes, and fades out once nothing has run and
// nothing has waited on you for two minutes. "Always" keeps it on screen and
// "Never" keeps it away; the tray menu's Pet item picks between the three.
//
// The window is a layered window drawn with per-pixel alpha, so only the core
// and its bubble catch the mouse and everything around them clicks through.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public class PetForm : Form
{
    enum Mood { Resting, Working, Done, Needs }

    // The canvas, in pixels at 100% scaling. The core sits bottom right and
    // the bubble above it, so the window can hug the screen corner.
    const int W = 300, H = 200, Span = 84;

    static readonly Color Blue = Color.FromArgb(91, 157, 187);
    static readonly Color Gold = Color.FromArgb(255, 200, 70);
    static readonly Color Red = Color.FromArgb(226, 70, 58);
    static readonly Color Green = Color.FromArgb(127, 163, 127);
    static readonly Color Orange = Color.FromArgb(217, 119, 87);
    static readonly Color Paper = Color.FromArgb(230, 228, 223);
    static readonly Color Dim = Color.FromArgb(150, 147, 141);

    readonly TaskBoard board;
    readonly Timer tick = new Timer();
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly List<TaskItem> alerts = new List<TaskItem>();
    readonly string settings;
    float scale = 1f;
    int working;
    double alertAt = -100;
    // A finish in the app you are already looking at gets a short burst and
    // bubble, and is not kept, so a Code session in front of you does not nag.
    TaskItem flash;
    double flashUntil;
    bool sound = true, placed;
    bool pressed, dragging;
    Point downAt, startAt;

    // "auto" shows it only while something is running or waiting on you,
    // "always" keeps it on screen, "off" never shows it.
    public string Mode = "auto";
    const double IdleHide = 120;
    double lastBusy = -1000;
    float fade = 1f;
    bool leaving, snoozed;
    // The bubble's close cross, in canvas pixels; empty with no bubble.
    RectangleF closeBox = RectangleF.Empty;
    bool overClose;
    int lastWorking;

    public PetForm(TaskBoard board)
    {
        this.board = board;
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptAdvisor");
        settings = Path.Combine(dir, "pet.txt");

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Cursor = Cursors.Hand;
        Text = "Prompt Advisor · Pet";

        ContextMenu menu = new ContextMenu();
        menu.MenuItems.Add("Tasks", delegate { board.Show(); });
        menu.MenuItems.Add("Dismiss", delegate { alerts.Clear(); flash = null; });
        MenuItem chime = new MenuItem("Sound");
        chime.Click += delegate { sound = !sound; Save(); };
        menu.MenuItems.Add(chime);
        // Away until the next task starts or finishes, whatever the mode.
        menu.MenuItems.Add("Hide until the next task", delegate { alerts.Clear(); flash = null; snoozed = true; Hide(); });
        menu.Popup += delegate { chime.Checked = sound; };
        ContextMenu = menu;

        MouseDown += delegate (object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            pressed = true; dragging = false;
            downAt = Cursor.Position; startAt = Location;
        };
        MouseMove += delegate (object s, MouseEventArgs e)
        {
            overClose = closeBox.Contains(e.X / scale, e.Y / scale);
            if (!pressed) return;
            Point p = Cursor.Position;
            if (!dragging && Math.Abs(p.X - downAt.X) + Math.Abs(p.Y - downAt.Y) > 4) dragging = true;
            if (dragging) Location = new Point(startAt.X + p.X - downAt.X, startAt.Y + p.Y - downAt.Y);
        };
        MouseUp += delegate (object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || !pressed) return;
            pressed = false;
            if (dragging) { dragging = false; Rescale(); Save(); }
            else if (closeBox.Contains(e.X / scale, e.Y / scale)) { alerts.Clear(); flash = null; overClose = false; }
            else Clicked();
        };
        MouseLeave += delegate { overClose = false; };

        board.Changed += delegate { if (IsHandleCreated) BeginInvoke((Action)Sync); };
        board.Alerted += delegate (TaskItem it) { if (IsHandleCreated) BeginInvoke((Action)delegate { Alert(it); }); };

        tick.Interval = 50;
        tick.Tick += delegate { Fade(); if (Visible) Render(); };
        Load_();
    }

    // Called once at start-up: the window exists from then on, hidden or
    // not, so the board's events always reach it.
    public void Start()
    {
        IntPtr unused = Handle;
        if (Mode == "always") Present();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            // Layered, kept off Alt+Tab, never takes focus from what you type in.
            cp.ExStyle |= 0x80000 | 0x80 | 0x8000000;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Rescale();
        // First run: bottom right, just above the taskbar, at this screen's scale.
        if (!placed)
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - 4, wa.Bottom - Height);
        }
        Sync();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        tick.Enabled = Visible;
    }

    public void SetMode(string mode)
    {
        Mode = mode;
        Save();
        if (mode == "off") Hide();
        else if (mode == "always" || Busy()) Present();
        else if (Visible) leaving = true;
    }

    bool Busy() { return working > 0 || alerts.Count > 0 || flash != null; }

    // Fade in, from wherever it was.
    void Present()
    {
        if (Mode == "off") return;
        leaving = false;
        if (!Visible) { fade = 0; Show(); }
        Render();
    }

    // Each tick: in "auto", start leaving once idle for two minutes; then
    // move the fade a step toward where it is heading.
    void Fade()
    {
        double now = clock.Elapsed.TotalSeconds;
        if (Busy()) { lastBusy = now; leaving = false; }
        else if (Mode == "auto" && now - lastBusy > IdleHide) leaving = true;
        if (leaving)
        {
            fade -= 0.06f;
            if (fade <= 0) { fade = 0; leaving = false; Hide(); }
        }
        else if (fade < 1) fade = Math.Min(1f, fade + 0.12f);
    }

    /* -------------------------------------------------------------- *
     * What the board says
     * -------------------------------------------------------------- */

    void Sync()
    {
        List<TaskItem> all = board.Snapshot();
        Dictionary<string, TaskItem> byKey = new Dictionary<string, TaskItem>();
        working = 0;
        foreach (TaskItem it in all)
        {
            byKey[it.Key] = it;
            // A Claude Code turn you interrupted never sends its stop, so a
            // task silent for three hours no longer counts as running.
            if (it.State == "working" && (DateTime.Now - it.Updated).TotalHours < 3) working++;
        }
        // Gone from the board, or started again, means you have been back to it.
        alerts.RemoveAll(delegate (TaskItem a)
        {
            TaskItem now;
            return !byKey.TryGetValue(a.Key, out now) || now.State == "working";
        });
        // Hidden by hand, it stays away until a task starts that was not
        // already running then.
        if (working > lastWorking) snoozed = false;
        lastWorking = working;
        if (Busy() && !snoozed) { lastBusy = clock.Elapsed.TotalSeconds; Present(); }
    }

    void Alert(TaskItem it)
    {
        Sync();
        double now = clock.Elapsed.TotalSeconds;
        alertAt = now;
        if (InFront(it))
        {
            flash = it;
            flashUntil = now + 4;
            lastBusy = now;
            Present();
            return;
        }
        alerts.RemoveAll(delegate (TaskItem a) { return a.Key == it.Key; });
        alerts.Add(it);
        lastBusy = now;
        snoozed = false;
        Present();
        if (sound && Mode != "off")
        {
            if (it.State == "needs") SystemSounds.Exclamation.Play();
            else SystemSounds.Asterisk.Play();
        }
    }

    static bool InFront(TaskItem it)
    {
        string proc = TaskBoard.ProcessFor(it.App);
        if (proc == null) return false;
        try
        {
            uint pid;
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out pid);
            return string.Equals(Process.GetProcessById((int)pid).ProcessName, proc, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    void Clicked()
    {
        if (alerts.Count == 0) { board.Show(); return; }
        TaskItem last = alerts[alerts.Count - 1];
        alerts.Clear();
        flash = null;
        board.Open(last);
    }

    /* -------------------------------------------------------------- *
     * Drawing
     * -------------------------------------------------------------- */

    void Rescale()
    {
        try { scale = Math.Max(1f, Native.GetDpiForWindow(Handle) / 96f); } catch { scale = 1f; }
        Size = new Size((int)(W * scale), (int)(H * scale));
    }

    void Render()
    {
        try
        {
            using (Bitmap bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAlias;
                    g.ScaleTransform(scale, scale);
                    Draw(g, clock.Elapsed.TotalSeconds);
                }
                Layered.Push(this, bmp, (byte)(255 * fade));
            }
        }
        catch (Exception e)
        {
            // Never let a drawing fault take the board down with it.
            tick.Enabled = false;
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "PromptAdvisor.log"), DateTime.Now.ToString("s") + "  pet stopped drawing: " + e.Message + Environment.NewLine); } catch { }
        }
    }

    void Draw(Graphics g, double t)
    {
        closeBox = RectangleF.Empty;
        if (flash != null && t >= flashUntil) flash = null;
        TaskItem shown = alerts.Count > 0 ? alerts[alerts.Count - 1] : flash;
        bool needs = alerts.Count > 0
            ? alerts.Exists(delegate (TaskItem a) { return a.State == "needs"; })
            : flash != null && flash.State == "needs";
        Mood mood = needs ? Mood.Needs : shown != null ? Mood.Done
            : working > 0 ? Mood.Working : Mood.Resting;

        float cx = W - 8 - Span / 2f, cy = H - 8 - Span / 2f;
        GraphicsState saved = g.Save();
        g.TranslateTransform(cx, cy);
        DrawCore(g, t, mood, t - alertAt);
        g.Restore(saved);

        float top = cy - 34;
        if (mood == Mood.Working && working > 1) DrawCount(g, cx, top);
        if (shown != null) DrawBubble(g, mood, shown, cx, top);
    }

    // The core, centred on the origin: a soft glow, an outer ring of eight
    // segments, two inner arcs turning the other way, and a bright heart.
    // Grey and slow at rest, blue and turning while a task works, a gold
    // burst of rings when one finishes, a red pulse when one needs you.
    void DrawCore(Graphics g, double t, Mood mood, double since)
    {
        Color c = mood == Mood.Working ? Blue : mood == Mood.Done ? Gold : mood == Mood.Needs ? Red : Dim;
        bool burst = mood == Mood.Done && since < 3;

        // Degrees a second for the outer ring; the inner arcs run the other way, faster.
        double speed = mood == Mood.Working ? 70 : burst ? 260 : mood == Mood.Done ? 30 : mood == Mood.Needs ? 110 : 6;
        float spin = (float)(t * speed % 360);

        float glow;
        if (mood == Mood.Resting) glow = 0.25f + 0.1f * (float)Math.Sin(t * 1.2);
        else if (mood == Mood.Working) glow = 0.55f + 0.1f * (float)Math.Sin(t * 3);
        else if (mood == Mood.Done) glow = burst ? 1f : 0.7f + 0.1f * (float)Math.Sin(t * 2);
        else glow = 0.45f + 0.55f * (float)Math.Abs(Math.Sin(t * 4));

        // Glow: rings of falling alpha, since a layered window has no blur.
        for (int i = 6; i >= 0; i--)
            using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(glow * (14 + (6 - i) * 6)), c)))
            {
                float r = 24 + i * 3.4f;
                g.FillEllipse(b, -r, -r, 2 * r, 2 * r);
            }

        // Shock rings: rolling outward for three seconds after a finish, and
        // for as long as something needs you.
        if (burst || mood == Mood.Needs)
            using (Pen p = new Pen(c, 2f))
                for (int w = 0; w < 3; w++)
                {
                    double ph = (t * (burst ? 1.6 : 1.1) + w / 3.0) % 1;
                    float r = 30 + (float)ph * 11;
                    p.Color = Color.FromArgb((int)(220 * (1 - ph)), c);
                    g.DrawEllipse(p, -r, -r, 2 * r, 2 * r);
                }

        Color ring = mood == Mood.Resting ? Color.FromArgb(150, c) : c;
        using (Pen outer = new Pen(ring, 3.2f))
            for (int k = 0; k < 8; k++)
                g.DrawArc(outer, -28, -28, 56, 56, spin + k * 45, 31);

        using (Pen inner = new Pen(Blend(ring, Color.White, 0.2f), 2f))
        {
            inner.StartCap = inner.EndCap = LineCap.Round;
            g.DrawArc(inner, -19, -19, 38, 38, -spin * 1.6f, 110);
            g.DrawArc(inner, -19, -19, 38, 38, -spin * 1.6f + 180, 110);
        }

        float beat = mood == Mood.Needs ? glow : mood == Mood.Resting ? 0.6f + 0.25f * (float)Math.Sin(t * 1.2) : 1f;
        using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(255 * Math.Min(1, beat + 0.15f)), Blend(c, Color.White, 0.3f))))
            g.FillEllipse(b, -11, -11, 22, 22);
        using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(230 * beat), Color.White)))
            g.FillEllipse(b, -5, -5, 10, 10);
    }

    static Color Blend(Color a, Color b, float k)
    {
        return Color.FromArgb(a.A, (int)(a.R + (b.R - a.R) * k), (int)(a.G + (b.G - a.G) * k), (int)(a.B + (b.B - a.B) * k));
    }

    void DrawCount(Graphics g, float cx, float top)
    {
        // More than one task running: say how many.
        using (SolidBrush b = new SolidBrush(Blue))
        using (Font f = new Font("Segoe UI", 8, FontStyle.Bold))
        {
            g.FillEllipse(b, cx + 22, top - 4, 17, 17);
            StringFormat mid = new StringFormat();
            mid.Alignment = mid.LineAlignment = StringAlignment.Center;
            g.DrawString(working.ToString(), f, Brushes.White, new RectangleF(cx + 22, top - 4, 17, 17), mid);
        }
    }

    void DrawBubble(Graphics g, Mood mood, TaskItem it, float cx, float top)
    {
        bool kept = alerts.Count > 0;
        string head = (mood == Mood.Needs ? "Needs you · " : "Done · ") + it.Source;
        string body = it.Title ?? "";
        string hint = !kept ? "" : alerts.Count > 1 ? "+" + (alerts.Count - 1) + " more · click to open" : "Click to open";

        using (Font fh = new Font("Segoe UI", 9, FontStyle.Bold))
        using (Font fb = new Font("Segoe UI", 9))
        using (Font fs = new Font("Segoe UI", 8))
        {
            float tw = Math.Max(g.MeasureString(head, fh).Width, Math.Max(g.MeasureString(body, fb).Width, g.MeasureString(hint, fs).Width));
            float w = Math.Min(W - 16, Math.Max(150, tw + 40));
            float h = hint.Length > 0 ? 62 : 44;
            float right = W - 8, bottomY = Math.Max(4 + h, top - 8);
            RectangleF r = new RectangleF(right - w, bottomY - h, w, h);

            Color edge = mood == Mood.Needs ? Orange : Green;
            using (GraphicsPath path = Rounded(r, 8))
            using (SolidBrush fill = new SolidBrush(Color.FromArgb(242, 32, 31, 30)))
            using (Pen pen = new Pen(edge, 1.5f))
            {
                PointF[] tail = { new PointF(cx - 7, r.Bottom - 1), new PointF(cx + 7, r.Bottom - 1), new PointF(cx + 2, r.Bottom + 8) };
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
                g.FillPolygon(fill, tail);
                g.DrawLine(pen, tail[1], tail[2]);
                g.DrawLine(pen, tail[2], tail[0]);
            }

            // A cross in the corner: dismiss without opening anything.
            RectangleF x = new RectangleF(r.Right - 22, r.Y + 5, 16, 16);
            closeBox = RectangleF.Inflate(x, 3, 3);
            if (overClose)
                using (SolidBrush b = new SolidBrush(Color.FromArgb(60, Paper)))
                    g.FillEllipse(b, x);
            using (Pen p = new Pen(overClose ? Paper : Dim, 1.4f))
            {
                p.StartCap = p.EndCap = LineCap.Round;
                float c = x.X + x.Width / 2, m = x.Y + x.Height / 2;
                g.DrawLine(p, c - 3.5f, m - 3.5f, c + 3.5f, m + 3.5f);
                g.DrawLine(p, c + 3.5f, m - 3.5f, c - 3.5f, m + 3.5f);
            }

            StringFormat cut = new StringFormat(StringFormatFlags.NoWrap);
            cut.Trimming = StringTrimming.EllipsisCharacter;
            using (SolidBrush b = new SolidBrush(edge)) g.DrawString(head, fh, b, new RectangleF(r.X + 10, r.Y + 6, r.Width - 36, 16), cut);
            using (SolidBrush b = new SolidBrush(Paper)) g.DrawString(body, fb, b, new RectangleF(r.X + 10, r.Y + 22, r.Width - 20, 16), cut);
            if (hint.Length > 0)
                using (SolidBrush b = new SolidBrush(Dim)) g.DrawString(hint, fs, b, new RectangleF(r.X + 10, r.Y + 40, r.Width - 20, 17), cut);
        }
    }

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

    /* -------------------------------------------------------------- *
     * Where it sits, kept in %LOCALAPPDATA%\PromptAdvisor\pet.txt
     * -------------------------------------------------------------- */

    void Load_()
    {
        try
        {
            if (File.Exists(settings))
            {
                Dictionary<string, string> kv = new Dictionary<string, string>();
                foreach (string line in File.ReadAllLines(settings))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) kv[line.Substring(0, i)] = line.Substring(i + 1);
                }
                int x, y;
                if (kv.ContainsKey("x") && kv.ContainsKey("y") && int.TryParse(kv["x"], out x) && int.TryParse(kv["y"], out y))
                {
                    // Only if that spot is still on a screen.
                    foreach (Screen s in Screen.AllScreens)
                        if (s.WorkingArea.Contains(x + 200, y + 120)) { Location = new Point(x, y); placed = true; break; }
                }
                if (kv.ContainsKey("sound")) sound = kv["sound"] != "0";
                if (kv.ContainsKey("mode")) Mode = kv["mode"] == "always" || kv["mode"] == "off" ? kv["mode"] : "auto";
                // Before the modes, the pet was simply shown or hidden.
                else if (kv.ContainsKey("shown") && kv["shown"] == "0") Mode = "off";
            }
        }
        catch { }
    }

    void Save()
    {
        try
        {
            File.WriteAllText(settings, "x=" + Left + "\r\ny=" + Top + "\r\nsound=" + (sound ? 1 : 0) + "\r\nmode=" + Mode + "\r\n");
        }
        catch { }
    }

    static class Layered
    {
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte Op, Flags, Alpha, Format; }

        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dst, ref POINT at, ref SIZE size, IntPtr src, ref POINT from, int key, ref BLEND blend, int flags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

        public static void Push(Form f, Bitmap bmp, byte alpha)
        {
            IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen);
            IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mem, hbmp);
            try
            {
                SIZE size = new SIZE(); size.cx = bmp.Width; size.cy = bmp.Height;
                POINT from = new POINT(), at = new POINT(); at.x = f.Left; at.y = f.Top;
                BLEND blend = new BLEND(); blend.Alpha = alpha; blend.Format = 1; // AC_SRC_ALPHA
                UpdateLayeredWindow(f.Handle, screen, ref at, ref size, mem, ref from, 0, ref blend, 2); // ULW_ALPHA
            }
            finally
            {
                SelectObject(mem, old);
                DeleteObject(hbmp);
                DeleteDC(mem);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }
    }
}
