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
// nothing has waited on you for 30 seconds. "Always" keeps it on screen and
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
    enum Mood { Resting, Hidden, Thinking, Working, Done, Needs }

    // The canvas, in pixels at 100% scaling. The core sits bottom right and
    // the bubble above it, so the window can hug the screen corner.
    const int W = 300, H = 200, Span = 110;
    // Voidling is drawn this much larger than its geometry below.
    const float Zoom = 1.3f;

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
    const double IdleHide = 30;
    double lastBusy = -1000;
    float fade = 1f;
    // How much of the pet shows while a task quietly runs: full at the
    // start, about a quarter once the run settles, full again for a
    // finish, a request or the pointer coming near.
    const float QuietAlpha = 0.25f;
    float presence = 1f;
    Mood drawn = Mood.Resting;
    bool leaving, snoozed;
    Canvas canvas;

    // The bubble's close cross, in canvas pixels; empty with no bubble.
    RectangleF closeBox = RectangleF.Empty;
    // The bubble itself, in canvas pixels; empty with no bubble.
    RectangleF bubbleBox = RectangleF.Empty;
    bool overClose;
    int lastWorking, hiddenRuns;
    double workingSince = -100;
    // When the latest task started, for the bright start of each run.
    double runStart = -100;
    // Battery saver on: half the motes and ten frames a second.
    bool battery;
    // Windows' "Animation effects" off: slower, gentler motion, never fewer frames.
    bool still;

    [StructLayout(LayoutKind.Sequential)]
    struct PowerStatus { public byte AC, Battery, Percent, SaverFlag; public int Life, Full; }
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out PowerStatus s);
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(int action, int param, out bool value, int ini);

    // Read now and then, not every frame: both change rarely.
    void ReadSettings()
    {
        PowerStatus ps;
        battery = GetSystemPowerStatus(out ps) && (ps.SaverFlag & 1) == 1;
        bool animate;
        still = SystemParametersInfo(0x1042 /* SPI_GETCLIENTAREAANIMATION */, 0, out animate, 0) && !animate;
    }
    // Alerts put away by hand, as key@time, so a request that is still open
    // on the board does not come straight back; a newer one does.
    readonly HashSet<string> dismissed = new HashSet<string>();
    int ticks, faults;

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
        menu.MenuItems.Add("Dismiss all", delegate { DismissAll(); });
        MenuItem chime = new MenuItem("Sound");
        chime.Click += delegate { sound = !sound; Save(); };
        menu.MenuItems.Add(chime);
        // Away until the next task starts or finishes, whatever the mode.
        menu.MenuItems.Add("Hide until the next task", delegate { DismissAll(); snoozed = true; Hide(); });
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
            else if (closeBox.Contains(e.X / scale, e.Y / scale)) { DismissShown(); overClose = false; }
            else Clicked();
        };
        MouseLeave += delegate { overClose = false; };

        board.Changed += delegate { if (IsHandleCreated) BeginInvoke((Action)Sync); };
        board.Alerted += delegate (TaskItem it) { if (IsHandleCreated) BeginInvoke((Action)delegate { Alert(it); }); };

        // Held back over a full-screen app: look every 2 seconds for the
        // screen to be yours again, then show what waited.
        holdCheck.Interval = 2000;
        holdCheck.Tick += delegate
        {
            if (ScreenBusy()) return;
            holdCheck.Enabled = false;
            held = false;
            Sync();
        };

        // The timer runs the pet's logic: fading, reading the board,
        // passing clicks. Frames are drawn by FrameLoop, in step with the screen.
        tick.Interval = 50;
        tick.Tick += delegate
        {
            // The board's state also changes with time alone: a run silent
            // for hours stops counting. Read it again about once a second.
            if (++ticks % 10 == 0) { Sync(); ClearSeen(); }
            if (ticks % 50 == 1) ReadSettings();
            Fade();
            if (Visible) PassClicks();
        };
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

    protected override void Dispose(bool disposing)
    {
        framesOn = false;
        if (disposing)
        {
            trayTick.Dispose();
            if (button != null) button.Dispose();
            if (trayHandle != IntPtr.Zero) { DestroyIcon(trayHandle); trayHandle = IntPtr.Zero; }
            if (canvas != null) canvas.Dispose();
            foreach (Bitmap b in glows.Values) b.Dispose();
            glows.Clear();
            foreach (Bitmap[] h in hazes.Values) { h[0].Dispose(); h[1].Dispose(); }
            hazes.Clear();
        }
        base.Dispose(disposing);
    }

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
        if (Visible && frames == null)
        {
            framesOn = true;
            frames = new System.Threading.Thread(FrameLoop);
            frames.IsBackground = true;
            frames.Start();
        }
        else if (!Visible) { framesOn = false; frames = null; }
    }

    /* -------------------------------------------------------------- *
     * Frames, one per screen refresh. A WinForms timer ticks in 15.6 ms
     * steps and drifts, so motion drawn on it judders however fast it is
     * set. DwmFlush returns as Windows finishes composing each refresh, so
     * a frame drawn after it lands once per refresh, evenly: 60 a second on
     * a 60 Hz screen, every other refresh on a faster one. Battery saver
     * halves that. Windows' animations setting never lowers the frame rate,
     * which only makes motion look broken; it calms the motion instead.
     * -------------------------------------------------------------- */

    [DllImport("dwmapi.dll")] static extern int DwmFlush();
    volatile bool framesOn;
    System.Threading.Thread frames;

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern int GetDeviceCaps(IntPtr dc, int index);

    // Refreshes to skip between frames, so a fast screen is not drawn for
    // at its full rate: 60 Hz draws every refresh, 120 and 144 Hz every
    // other one, an even 60 or 72 a second.
    static int RefreshStep()
    {
        int hz = 60;
        try { IntPtr dc = GetDC(IntPtr.Zero); hz = GetDeviceCaps(dc, 116 /* VREFRESH */); ReleaseDC(IntPtr.Zero, dc); } catch { }
        if (hz < 30 || hz > 500) hz = 60;
        return Math.Max(1, (int)Math.Round(hz / 60.0));
    }

    void FrameLoop()
    {
        int n = 0, drawn = 0, step = RefreshStep();
        System.Diagnostics.Stopwatch since = System.Diagnostics.Stopwatch.StartNew();
        bool reported = false;
        while (framesOn)
        {
            // Once, ten seconds after it appears: the rate it actually drew at.
            if (!reported && since.Elapsed.TotalSeconds >= 10)
            {
                reported = true;
                LogNote("pet drew " + drawn + " frames in 10 s (" + (drawn / 10) + " a second)");
            }
            if (DwmFlush() != 0) System.Threading.Thread.Sleep(16);
            // Battery saver: half as many frames, still evenly spaced.
            if (++n % (battery ? step * 2 : step) != 0) continue;
            try
            {
                if (!framesOn || IsDisposed) break;
                // Drawn on the window's own thread, and waited for, so frames
                // never queue up behind each other.
                Invoke((Action)delegate { if (Visible) Render(); });
                drawn++;
            }
            catch { break; }
        }
    }

    static void LogNote(string line)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "PromptAdvisor.log"), DateTime.Now.ToString("s") + "  " + line + Environment.NewLine); } catch { }
    }

    public void SetMode(string mode)
    {
        Mode = mode;
        Save();
        if (mode == "off") Hide();
        else if (mode == "always" || Busy()) Present();
        else if (Visible) leaving = true;
    }

    // Anything still going keeps the pet up, seen or not.
    bool Busy() { return working > 0 || hiddenRuns > 0 || alerts.Count > 0 || flash != null; }

    // Motes and glow drift out past the creature, and a layered window
    // catches the mouse wherever it has drawn anything, so a faint mote over
    // another app would take your click. Only the creature and its bubble
    // take clicks; elsewhere the window lets them through to what is below.
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int index, int value);
    const int ExStyle = -20, ClickThrough = 0x20;
    bool passing;

    void PassClicks()
    {
        Point c = PointToClient(Cursor.Position);
        float x = c.X / scale, y = c.Y / scale;
        float cx = W - 8 - Span / 2f, cy = H - 8 - Span / 2f;
        bool onIt = pressed || bubbleBox.Contains(x, y) || (x - cx) * (x - cx) + (y - cy) * (y - cy) < 50 * 50;
        if (onIt == !passing) return;
        passing = !onIt;
        int ex = GetWindowLong(Handle, ExStyle);
        SetWindowLong(Handle, ExStyle, passing ? ex | ClickThrough : ex & ~ClickThrough);
    }

    bool Lively()
    {
        return leaving || fade < 1 || flash != null
            || clock.Elapsed.TotalSeconds - alertAt < 3.5
            || alerts.Exists(delegate (TaskItem a) { return a.State == "needs"; });
    }

    // Fade in, from wherever it was.
    void Present()
    {
        if (Mode == "off") return;
        // Over a full-screen app or a presentation, hold everything back;
        // it shows once the screen is yours again.
        if (ScreenBusy()) { held = true; holdCheck.Enabled = true; if (Visible) Hide(); return; }
        leaving = false;
        if (!Visible) { fade = 0; Show(); }
        if (!tick.Enabled) { faults = 0; tick.Enabled = true; }
        Render();
    }

    // QUNS_BUSY, QUNS_RUNNING_D3D_FULL_SCREEN, QUNS_PRESENTATION_MODE: a
    // full-screen app, a game or video, or presentation settings switched on.
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    readonly Timer holdCheck = new Timer();
    bool held;

    static bool ScreenBusy()
    {
        int st;
        try { if (SHQueryUserNotificationState(out st) != 0) return false; } catch { return false; }
        return st == 2 || st == 3 || st == 4;
    }

    // Each tick: in "auto", start leaving once idle for 30 seconds; then
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
        int was = working, hidden = 0;
        working = 0;
        foreach (TaskItem it in all)
        {
            byKey[it.Key] = it;
            // A Claude Code turn you interrupted never sends its stop, so a
            // task silent for three hours no longer counts as running.
            if (it.State == "working" && (DateTime.Now - it.Updated).TotalHours < 3) working++;
            // A Claude chat out of sight, behind the Code tab or minimized, is
            // still working as far as you are concerned: claude.ai will say
            // when it finishes. Only a run nothing can follow, like a ChatGPT
            // chat in a minimized window, shows the faint hidden look.
            if (it.State == "hidden" && (DateTime.Now - it.Updated).TotalHours < 3)
            {
                if (it.App == "claude") working++; else hidden++;
            }
        }
        // Gone from the board, or started again, means you have been back to it.
        alerts.RemoveAll(delegate (TaskItem a)
        {
            TaskItem now;
            return !byKey.TryGetValue(a.Key, out now) || now.State == "working";
        });
        // A request that still waits on you is shown, even if its alert was
        // lost to a restart, unless you put it away yourself.
        foreach (TaskItem it in all)
            if (it.State == "needs" && (DateTime.Now - it.Updated).TotalHours < 3
                && !dismissed.Contains(Stamp(it)) && !alerts.Exists(delegate (TaskItem a) { return a.Key == it.Key; }))
                alerts.Add(it);
        hiddenRuns = hidden;
        Orbit(all, byKey, clock.Elapsed.TotalSeconds);
        if (working > 0 && was == 0) workingSince = clock.Elapsed.TotalSeconds;
        if (working > was) runStart = clock.Elapsed.TotalSeconds;
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
        // The board already decided this is news. It cannot tell for Claude
        // Code, whose hooks do not say whether you are looking, so a finish
        // there while the Claude app is in front only flashes.
        if ((it.Source ?? "").StartsWith("Claude Code") && InFront(it))
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
        if (sound && Mode != "off" && !held)
        {
            if (it.State == "needs") SystemSounds.Exclamation.Play();
            else SystemSounds.Asterisk.Play();
        }
    }

    // A finish you have gone and looked at is not news any more. Once its
    // app has been in front for about two seconds, after the alert came,
    // the finish is put away as if you had clicked it. A request for your
    // approval stays until it is answered.
    readonly Dictionary<string, int> lookedAt = new Dictionary<string, int>();

    void ClearSeen()
    {
        for (int i = alerts.Count - 1; i >= 0; i--)
        {
            TaskItem a = alerts[i];
            if (a.State == "needs") continue;
            int n;
            lookedAt.TryGetValue(a.Key, out n);
            n = LookingAt(a) ? n + 1 : 0;
            if (n >= 4)
            {
                dismissed.Add(Stamp(a));
                alerts.RemoveAt(i);
                lookedAt.Remove(a.Key);
            }
            else lookedAt[a.Key] = n;
        }
    }

    // Whether you can see the task an alert is about. A Claude chat lives in
    // the same window as Claude Code, so the Claude app being in front is not
    // enough: the chat must be showing, not the Code tab. Anything else
    // counts as seen when its app is in front.
    bool LookingAt(TaskItem it)
    {
        bool chat = it.App == "claude" && !(it.Source ?? "").StartsWith("Claude Code");
        if (!chat) return InFront(it);
        try { return board.ChatInView(); } catch { return false; }
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

    // A click opens the task in the bubble and puts away only that one;
    // others still waiting take its place.
    void Clicked()
    {
        if (alerts.Count == 0) { board.Show(); return; }
        TaskItem last = alerts[alerts.Count - 1];
        DismissShown();
        board.Open(last);
    }

    static string Stamp(TaskItem it) { return it.Key + "@" + it.Updated.Ticks; }

    void DismissShown()
    {
        flash = null;
        if (alerts.Count == 0) return;
        TaskItem last = alerts[alerts.Count - 1];
        dismissed.Add(Stamp(last));
        alerts.RemoveAt(alerts.Count - 1);
    }

    void DismissAll()
    {
        foreach (TaskItem a in alerts) dismissed.Add(Stamp(a));
        alerts.Clear();
        flash = null;
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
            if (canvas == null || canvas.Width != Width || canvas.Height != Height)
            {
                if (canvas != null) canvas.Dispose();
                canvas = new Canvas(Width, Height);
            }
            using (Graphics g = Graphics.FromImage(canvas.Image))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                g.ScaleTransform(scale, scale);
                Point m = PointToClient(Cursor.Position);
                UpdateGaze(m.X / scale, m.Y / scale, clock.Elapsed.TotalSeconds);
                Draw(g, clock.Elapsed.TotalSeconds);
            }
            float want = Quiet(clock.Elapsed.TotalSeconds);
            presence += (want - presence) * (want > presence ? 0.12f : 0.04f);
            canvas.Push(this, (byte)(255 * fade * presence));
            faults = 0;
        }
        catch (Exception e)
        {
            // Never let a drawing fault take the board down with it. One bad
            // frame is skipped. Only a long run of them stops the drawing, and
            // the next time the pet is shown it tries again.
            if (++faults == 1 || faults % 100 == 0) LogFault(e);
            if (faults >= 200) tick.Enabled = false;
        }
    }

    void LogFault(Exception e)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "PromptAdvisor.log"), DateTime.Now.ToString("s") + "  pet could not draw (" + faults + " in a row): " + e.Message + Environment.NewLine); } catch { }
    }

    // What Voidling looks like now, and which alert its bubble shows.
    Mood PickMood(double t, out TaskItem shown)
    {
        if (flash != null && t >= flashUntil) flash = null;
        shown = alerts.Count > 0 ? alerts[alerts.Count - 1] : flash;
        bool needs = alerts.Count > 0
            ? alerts.Exists(delegate (TaskItem a) { return a.State == "needs"; })
            : flash != null && flash.State == "needs";
        // Something running shows over a finish that is still waiting, once
        // the finish has had its few seconds of flare: the bubble keeps
        // saying what finished, the body says what is going on now.
        bool flaring = shown != null && t - alertAt < 3;
        Mood mood = needs ? Mood.Needs
            : shown != null && (flaring || working == 0) ? Mood.Done
            : working > 0 ? (t - workingSince < 4 ? Mood.Thinking : Mood.Working)
            : hiddenRuns > 0 ? Mood.Hidden : Mood.Resting;
        return mood;
    }

    void Draw(Graphics g, double t)
    {
        closeBox = RectangleF.Empty;
        bubbleBox = RectangleF.Empty;
        TaskItem shown;
        Mood mood = PickMood(t, out shown);
        drawn = mood;
        Mood said = shown == null ? mood : shown.State == "needs" ? Mood.Needs : Mood.Done;

        float cx = W - 8 - Span / 2f, cy = H - 8 - Span / 2f;
        GraphicsState saved = g.Save();
        g.TranslateTransform(cx, cy);
        DrawHole(g, t, mood, t - alertAt);
        g.Restore(saved);

        float top = cy - 34 * Zoom;
        // Past five planets, the rest are a small count by the disk's tip.
        int more = extraPlanets;
        if (more > 0)
            using (Font f = new Font("Segoe UI", 10, FontStyle.Bold, GraphicsUnit.Pixel))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(220, Paper)))
                g.DrawString("+" + more, f, b, cx - Span / 2f + 2, cy + 20);
        if (shown != null) DrawBubble(g, said, shown, cx, top);
    }

    // Voidling, centred on the origin: a tiny black hole with a tilted
    // accretion ring, a few orbiting motes and two slits of light for eyes.
    // The far half of the ring and of the motes is drawn before the core and
    // the near half after it, so the void sits inside its own ring.
    //
    //   resting    slow drift, a dim indigo ring, a gentle bob
    //   hidden     dimmer and slower still, eyes shut: a run you cannot see
    //   thinking   the first seconds of a run: ring spins fast, core tightens
    //   working    cyan ring, livelier motes, small energy arcs
    //   done       a starlight ripple and sparkles, then a calm cyan glow
    //   needs      a magenta ring that flickers and warps, eyes narrowed
    static readonly Color Void = Color.FromArgb(5, 4, 12);
    static readonly Color Cyan = Color.FromArgb(96, 226, 255);
    static readonly Color Violet = Color.FromArgb(150, 112, 255);
    static readonly Color Indigo = Color.FromArgb(80, 70, 205);
    static readonly Color Ember = Color.FromArgb(240, 84, 58);
    static readonly Color Starlight = Color.FromArgb(242, 247, 255);

    // Each mote: orbit size (times the ring), phase, speed, size, brightness.
    static readonly float[][] Motes = MakeMotes(22);

    static float[][] MakeMotes(int n)
    {
        Random r = new Random(7);
        float[][] m = new float[n][];
        for (int i = 0; i < n; i++)
            m[i] = new float[] { 0.78f + (float)r.NextDouble() * 0.6f, (float)(r.NextDouble() * Math.PI * 2),
                0.6f + (float)r.NextDouble() * 0.9f, 1f + (float)r.NextDouble() * 1.4f, 0.4f + (float)r.NextDouble() * 0.6f };
        return m;
    }

    // Whether the pointer is near: Voidling turns its ring toward you and
    // brightens a little, eased so it never snaps.
    bool noticed;
    float attend, attendX;

    void UpdateGaze(float px, float py, double t)
    {
        float cx = W - 8 - Span / 2f, cy = H - 8 - Span / 2f;
        float dx = px - cx, dy = py - cy;
        noticed = dx * dx + dy * dy < 90 * 90;
        attend += ((noticed ? 1f : 0f) - attend) * 0.06f;
        float tx = noticed ? Math.Max(-1f, Math.Min(1f, dx / 70f)) : 0f;
        attendX += (tx - attendX) * 0.06f;
    }

    // Voidling, centred on the origin: a tiny black hole with a tilted
    // accretion ring and a few orbiting motes. No face: its mood is in how
    // the ring turns, tightens and glows, and how the void breathes.
    //
    //   resting    slow indigo drift; after a long quiet, slower and dimmer
    //   hidden     dimmer still, nearly still: a run you cannot see
    //   thinking   the first seconds of a run: ring races, void tightens
    //   working    cyan ring, livelier motes, a current of energy
    //   done       a ripple of starlight, a rise, and the ring flips once
    //   needs      a deep red ring that warps slowly and breathes
    void DrawVoidling(Graphics g, double t, Mood mood, double since)
    {
        bool burst = mood == Mood.Done && since < 3;
        bool deep = mood == Mood.Resting && t - lastBusy > 25 && !noticed;
        float spin, core, ringR, energy, glow, wobble = 0, flicker = 1, lift = 0, squash = 0;
        Color ring, halo;
        switch (mood)
        {
            case Mood.Hidden:
                spin = 0.22f; core = 16; ringR = 33; energy = 0.12f; ring = Blend(Indigo, Void, 0.4f); halo = Indigo; glow = 0.1f; break;
            case Mood.Thinking:
                spin = 2.6f; core = 14.5f; ringR = 31; energy = 0.8f; ring = Violet; halo = Violet; glow = 0.45f; break;
            case Mood.Working:
                spin = 1.6f; core = 15.5f; ringR = 35; energy = 1f; ring = Cyan; halo = Blend(Cyan, Indigo, 0.4f);
                glow = 0.46f + 0.06f * (float)Math.Sin(t * 1.8); break;
            case Mood.Done:
                spin = burst ? 2f : 0.7f; core = 16; ringR = 35; energy = 0.7f; ring = Blend(Cyan, Starlight, burst ? 0.5f : 0.15f);
                halo = Cyan; glow = burst ? 0.55f : 0.4f + 0.04f * (float)Math.Sin(t * 1.5);
                // A rise and settle, eased at both ends.
                if (since < 1.4) { float u = (float)(since / 1.4); lift = 7 * (float)Math.Sin(u * Math.PI); squash = -0.05f * (float)Math.Sin(u * Math.PI); }
                break;
            case Mood.Needs:
                spin = 1.1f; core = 16; ringR = 34; energy = 0.75f; ring = Ember; halo = Ember;
                // Uneasy, never glitchy: a slow warp, a slow breathing of light.
                wobble = 2f * (float)Math.Sin(t * 2.2) * (float)Math.Sin(t * 0.9);
                flicker = 0.82f + 0.18f * (float)Math.Sin(t * 3.4);
                glow = 0.42f + 0.22f * (float)(0.5 + 0.5 * Math.Sin(t * 2.4)); break;
            default:
                spin = deep ? 0.28f : 0.45f; core = 16; ringR = 34; energy = deep ? 0.18f : 0.32f;
                ring = Blend(Indigo, Violet, 0.3f); halo = Indigo; glow = (deep ? 0.2f : 0.26f) + 0.04f * (float)Math.Sin(t * 1.1);
                break;
        }
        // Noticing you: a little brighter, the ring turned your way.
        glow += 0.08f * attend; energy += 0.15f * attend;

        // A snack: now and then a mote spirals in and the void swells as it
        // swallows it.
        double snack = mood == Mood.Resting || mood == Mood.Working ? (t % 13) / 13 : 0;
        float gulp = snack > 0.965 ? (float)Math.Sin((snack - 0.965) / 0.035 * Math.PI) : 0;

        if (still) { spin *= 0.4f; wobble *= 0.4f; lift *= 0.4f; }
        float bob = (float)Math.Sin(t * 1.05) * (mood == Mood.Resting || mood == Mood.Hidden ? 2f : 1.1f) * (still ? 0.3f : 1f);
        float breath = (float)Math.Sin(t * (deep || mood == Mood.Hidden ? 0.8 : 1.4)) * 0.022f;
        float turn = -16 + wobble + attendX * 9;
        g.TranslateTransform(0, bob - lift);

        Bitmap h = Glow(halo, glow);
        g.DrawImage(h, -GlowR, -GlowR, 2 * GlowR, 2 * GlowR);

        float a0 = (float)(t * spin);
        float flip = burst && since < 1.4 ? (float)Math.Cos(since / 1.4 * Math.PI * 2) : 1;
        float rx = ringR, ry = (ringR * 0.34f + wobble * 0.3f) * flip;
        GraphicsState level = g.Save();
        g.RotateTransform(turn);
        DrawRing(g, a0, rx, ry, ring, flicker, true);
        DrawMotes(g, t, a0, rx, ry, ring, energy, flicker, true);
        g.Restore(level);

        // The void, breathing and swelling as it swallows.
        float sx = core * (1 + squash + breath + 0.07f * gulp), sy = core * (1 - squash - breath * 0.6f + 0.05f * gulp);
        using (SolidBrush b = new SolidBrush(Void)) g.FillEllipse(b, -sx, -sy, 2 * sx, 2 * sy);
        using (Pen rim = new Pen(Color.FromArgb((int)(225 * flicker), Blend(ring, Starlight, 0.35f)), 1.6f))
            g.DrawEllipse(rim, -sx - 0.6f, -sy - 0.6f, 2 * sx + 1.2f, 2 * sy + 1.2f);
        using (Pen lens = new Pen(Color.FromArgb(38, Starlight), 1f))
            g.DrawEllipse(lens, -sx - 4, -sy - 4, 2 * sx + 8, 2 * sy + 8);
        // The far side of the ring, its light bent up and over the void.
        using (Pen bent = new Pen(Color.FromArgb((int)(150 * flicker), Blend(ring, Starlight, 0.25f)), 2.2f))
        {
            bent.StartCap = bent.EndCap = LineCap.Round;
            g.DrawArc(bent, -sx - 2.6f, -sy - 2.6f, 2 * sx + 5.2f, 2 * sy + 5.2f, 196, 148);
        }

        level = g.Save();
        g.RotateTransform(turn);
        DrawRing(g, a0, rx, ry, ring, flicker, false);
        DrawMotes(g, t, a0, rx, ry, ring, energy, flicker, false);
        if (mood == Mood.Working || mood == Mood.Thinking) DrawArcs(g, t, rx, ry, core, ring);
        g.Restore(level);

        if (snack > 0.84 && snack < 0.965)
        {
            float k = (float)((snack - 0.84) / 0.125), r = ringR * (1 - k) + core * 0.4f * k;
            double a = k * Math.PI * 3.2;
            using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(230 * (1 - k * 0.6f)), Starlight)))
                g.FillEllipse(b, (float)Math.Cos(a) * r - 1.5f, (float)Math.Sin(a) * r * 0.45f - 1.5f, 3f, 3f);
        }

        if (burst) DrawStarlight(g, t, since, core);
    }

    // Voidling as a lensed black hole, centred on the origin, drawn from how
    // one looks rather than from any picture of one:
    //
    //   - a flat, streaky disk of hot gas seen nearly edge-on, its far half
    //     behind the void and its near half crossing in front of it;
    //   - the far half's light bent up and over the top of the void, and a
    //     thinner image of it bent under the bottom;
    //   - a thin, very bright photon ring hugging the void;
    //   - the side of the disk spinning toward you brighter than the other.
    //
    // Mood is in brightness, colour and how fast the gas flows, never in a
    // face: dim amber at rest, white-gold while working, a blue-white flare
    // on a finish, a slow crimson pulse when something needs you.
    static readonly Color HotWhite = Color.FromArgb(255, 247, 234);
    static readonly Color Gold2 = Color.FromArgb(255, 192, 112);
    static readonly Color Amber = Color.FromArgb(214, 118, 58);
    static readonly Color BlueWhite = Color.FromArgb(206, 228, 255);
    static readonly Color Crimson = Color.FromArgb(226, 46, 52);

    void DrawHole(Graphics g, double t, Mood mood, double since)
    {
        bool burst = mood == Mood.Done && since < 3;
        bool deep = mood == Mood.Resting && t - lastBusy > 25 && !noticed;
        float core = 14.5f, bright, flow, lift = 0;
        Color hot, mid, outer;
        switch (mood)
        {
            case Mood.Hidden:
                bright = 0.32f; flow = 0.15f; hot = Gold2; mid = Amber; outer = Blend(Amber, Void, 0.4f); break;
            case Mood.Thinking:
                bright = 1.1f; flow = 1.6f; core = 13.5f; hot = HotWhite; mid = Gold2; outer = Amber; break;
            case Mood.Working:
                // A run starts at full glow and settles, over about twenty
                // seconds, to a calmer one while it works. The gas keeps its
                // pace and the hot spot keeps racing, so it still reads as
                // working. A new task starting lights it up again.
                bright = Settle(t - runStart) + 0.04f * (float)Math.Sin(t * 2); flow = 1f; hot = HotWhite; mid = Gold2; outer = Amber; break;
            case Mood.Done:
                // Finished: the gas turns cool blue-white and all but stops,
                // and stays that way until you look. Still and blue means
                // done; moving and gold means working; red means it needs you.
                hot = Color.FromArgb(236, 244, 255); mid = BlueWhite; outer = Color.FromArgb(112, 150, 226);
                if (burst)
                {
                    // The flare: a rush of light that settles into stillness.
                    float k = (float)Math.Min(1, since / 3);
                    bright = 1.2f - 0.15f * k; flow = 1.2f - 1.08f * k;
                    if (since < 1.4) lift = 5 * (float)Math.Sin(since / 1.4 * Math.PI);
                }
                // And it keeps that full glow until you look.
                else { bright = 1.05f + 0.06f * (float)Math.Sin(t * 1.1); flow = 0.12f; }
                break;
            case Mood.Needs:
                // The gas cools to crimson and its light breathes slowly.
                bright = 0.6f + 0.35f * (float)(0.5 + 0.5 * Math.Sin(t * 2.6));
                flow = 0.7f; hot = Color.FromArgb(255, 196, 186); mid = Crimson; outer = Color.FromArgb(130, 20, 34); break;
            default:
                bright = deep ? 0.42f : 0.55f; flow = deep ? 0.22f : 0.35f; hot = Gold2; mid = Amber; outer = Color.FromArgb(150, 70, 36); break;
        }
        if (still) flow *= 0.4f;
        bright += 0.12f * attend;
        // A planet reaching the void lights the gas for a moment.
        Planet[] orbit = Planets();
        bright += 0.4f * Swallow(orbit, t);

        float bob = (float)Math.Sin(t * 1.05) * (still ? 0.6f : 1.6f);
        float breath = (float)Math.Sin(t * 1.2) * 0.02f;
        g.TranslateTransform(0, bob - lift);
        g.ScaleTransform(Zoom, Zoom);

        // A wide, faint glow of scattered light.
        Blit(g, Glow(mid, 0.42f * bright), -GlowR, -GlowR, 2 * GlowR, 2 * GlowR);

        double phase = t * flow;          // how far the gas has turned
        float rx = 42, ry = 6.5f;         // the disk, nearly edge-on
        float tilt = -9 + attendX * 6;    // turned a little toward you when noticed
        float c = core * (1 + breath);

        GraphicsState level = g.Save();
        g.RotateTransform(tilt);

        // The soft glow of the disk and arches, which does not move, comes
        // from a cache; only the streaks of gas are drawn each frame.
        Bitmap[] haze = Haze(mid, bright, rx, ry, core);
        GraphicsState flat = g.Save();
        g.RotateTransform(-tilt);
        Blit(g, haze[0], -HazeW / 2, -HazeTop, HazeW, HazeH);
        g.Restore(flat);

        // 1. The far half of the disk, behind everything.
        DiskBand(g, phase, rx, ry, hot, mid, outer, bright * 0.8f, true, false);
        bool racing = mood == Mood.Working || mood == Mood.Thinking;
        double lap = t * (mood == Mood.Thinking ? 4.2 : 2.6) * (still ? 0.5 : 1);
        if (racing) HotSpot(g, lap, rx, ry, true);
        // The planets on the far side of their orbit, which the void hides.
        DrawPlanets(g, orbit, t, tilt, c, true);

        // 2. Its light bent over the top of the void: the arch.
        DiskArch(g, phase, c, hot, mid, outer, bright, true, false);
        // 3. And a thinner image bent under the bottom.
        DiskArch(g, phase, c, hot, mid, outer, bright * 0.55f, false, false);

        // 4. The void, and the photon ring right at its edge.
        using (SolidBrush b = new SolidBrush(Void)) g.FillEllipse(b, -c, -c, 2 * c, 2 * c);
        using (Pen ring = new Pen(Color.FromArgb(Clamp(255 * Math.Min(1, bright + 0.15)), hot), 1.5f))
            g.DrawEllipse(ring, -c - 0.4f, -c - 0.4f, 2 * c + 0.8f, 2 * c + 0.8f);
        using (Pen soft = new Pen(Color.FromArgb(Clamp(90 * bright), mid), 3.2f))
            g.DrawEllipse(soft, -c - 1.8f, -c - 1.8f, 2 * c + 3.6f, 2 * c + 3.6f);

        // 5. The near half of the disk, crossing in front of the void.
        flat = g.Save();
        g.RotateTransform(-tilt);
        Blit(g, haze[1], -HazeW / 2, -HazeTop, HazeW, HazeH);
        g.Restore(flat);
        DiskBand(g, phase, rx, ry, hot, mid, outer, bright, false, false);
        if (racing) HotSpot(g, lap, rx, ry, false);
        // 6. The planets on the near side, crossing in front of it all.
        DrawPlanets(g, orbit, t, tilt, c, false);
        g.Restore(level);

        // A finish: a ripple of light rolling out once the planet is in,
        // then again every few seconds for as long as the finish waits.
        if (mood == Mood.Done && (burst ? since >= Fall : (since % 4.5) < 1.8))
        {
            double ph = burst ? Math.Min(1, (since - Fall) / 1.6) : (since % 4.5) / 1.8;
            float r = c + 6 + (float)ph * 30;
            using (Pen p = new Pen(Color.FromArgb(Clamp(150 * (1 - ph) * (1 - ph)), BlueWhite), 1.3f))
                g.DrawEllipse(p, -r, -r * 0.8f, 2 * r, 1.6f * r);
        }

        // A snack: now and then a mote of gas falls in along the disk.
        double snack = mood == Mood.Resting || mood == Mood.Working ? (t % 13) / 13 : 0;
        if (snack > 0.84 && snack < 0.965)
        {
            float k = (float)((snack - 0.84) / 0.125);
            double a = Math.PI * (1.1 + 2.6 * k);
            float r = rx * (1 - k) + c * k;
            g.RotateTransform(tilt);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(Clamp(230 * (1 - 0.7 * k)), HotWhite)))
                g.FillEllipse(b, (float)Math.Cos(a) * r - 1.4f, (float)Math.Sin(a) * r * (ry / rx) * 3 - 1.4f, 2.8f, 2.8f);
        }
    }

    // The opacity the pet should have now. A run you are not waiting on
    // fades with its glow, from full at six seconds to a quarter by about
    // twenty, so it is just there. Anything that wants you brings it back.
    float Quiet(double t)
    {
        if (alerts.Count > 0 || flash != null) return 1f;
        double k;
        if (drawn == Mood.Working) k = SettleK(t - runStart);
        else if (drawn == Mood.Hidden) k = 1;
        else return 1f;
        float quiet = 1f - (1f - QuietAlpha) * (float)k;
        return quiet + (1f - quiet) * attend;
    }

    static double SettleK(double since)
    {
        double k = Math.Max(0, Math.Min(1, (since - 6) / 14));
        return k * k * (3 - 2 * k);
    }

    // The glow of a run, from its start: full for six seconds, then easing
    // down to a calm level by about twenty.
    static float Settle(double since)
    {
        return (float)(1.08 - 0.46 * SettleK(since));
    }

    static int Clamp(double v) { return v < 0 ? 0 : v > 255 ? 255 : (int)v; }

    // While it works, one bright knot of gas races round the disk with a
    // short tail: unmistakably busy, even from across the room. The far
    // half of its lap is drawn before the void, which hides it as it passes
    // behind; the near half after.
    static void HotSpot(Graphics g, double lap, float rx, float ry, bool back)
    {
        float r = rx * 0.66f, rr = ry * 0.8f;
        for (int i = 5; i >= 0; i--)
        {
            double a = lap - i * 0.16;
            if ((Math.Sin(a) < 0) != back) continue;
            float x = (float)Math.Cos(a) * r, y = (float)Math.Sin(a) * rr;
            float fade = 1 - i / 6f, size = 1.6f + 2.4f * fade;
            using (SolidBrush halo = new SolidBrush(Color.FromArgb(Clamp(80 * fade), Gold2)))
                g.FillEllipse(halo, x - size * 1.4f, y - size * 1.4f, size * 2.8f, size * 2.8f);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(Clamp(255 * fade), HotWhite)))
                g.FillEllipse(b, x - size / 2, y - size / 2, size, size);
        }
    }

    // A cached image drawn at its own pixel size: no smoothing pass needed.
    static void Blit(Graphics g, Bitmap b, float x, float y, float w, float h)
    {
        InterpolationMode im = g.InterpolationMode;
        PixelOffsetMode pm = g.PixelOffsetMode;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(b, x, y, w, h);
        g.InterpolationMode = im;
        g.PixelOffsetMode = pm;
    }

    // The cached soft layers: [0] behind the void (far half of the disk,
    // both arches), [1] in front of it (near half). One pair per colour,
    // core size and one of 40 brightnesses, at this screen's scale.
    const float HazeW = 100, HazeH = 58, HazeTop = 36;
    readonly Dictionary<long, Bitmap[]> hazes = new Dictionary<long, Bitmap[]>();

    Bitmap[] Haze(Color mid, float bright, float rx, float ry, float core)
    {
        int level = Math.Max(0, Math.Min(48, (int)Math.Round(bright * 40)));
        long key = ((long)(mid.ToArgb() & 0xFFFFFF) * 64 + level) * 64 + (int)(core * 2);
        Bitmap[] pair;
        if (hazes.TryGetValue(key, out pair)) return pair;
        if (hazes.Count > 160) { foreach (Bitmap[] old in hazes.Values) { old[0].Dispose(); old[1].Dispose(); } hazes.Clear(); }
        float b = level / 40f;
        pair = new Bitmap[2];
        for (int k = 0; k < 2; k++)
        {
            int w = (int)Math.Ceiling(HazeW * scale * Zoom), h = (int)Math.Ceiling(HazeH * scale * Zoom);
            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            bmp.SetResolution(96, 96);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.ScaleTransform(w / HazeW, h / HazeH);
                g.TranslateTransform(HazeW / 2, HazeTop);
                g.RotateTransform(-9);
                if (k == 0)
                {
                    DiskBand(g, 0, rx, ry, mid, mid, mid, b * 0.8f, true, true);
                    DiskArch(g, 0, core, mid, mid, mid, b, true, true);
                    DiskArch(g, 0, core, mid, mid, mid, b * 0.55f, false, true);
                }
                else DiskBand(g, 0, rx, ry, mid, mid, mid, b, false, true);
            }
            pair[k] = bmp;
        }
        hazes[key] = pair;
        return pair;
    }

    // Brighter on the side spinning toward you (the left), dimmer on the
    // other: what makes a real disk look lopsided.
    static double Beaming(double a) { return 0.45 + 0.55 * (0.5 - 0.5 * Math.Cos(a)); }

    // Streaks in the gas that drift round with it, so the disk visibly flows.
    static double Streak(double a, double phase, int lane)
    {
        double s = Math.Sin(a * 5 + phase * 2.2 + lane * 1.7) * 0.5 + Math.Sin(a * 11 - phase * 3.1 + lane * 0.9) * 0.3;
        return 0.62 + 0.38 * s;
    }

    // The flat disk as a few thin lanes of gas, from hot near the void to
    // cool at the rim. back: the far half, which the void then covers.
    static void DiskBand(Graphics g, double phase, float rx, float ry, Color hot, Color mid, Color outer, float bright, bool back, bool soft)
    {
        const int n = 40;
        // The sheet: a broad soft stroke the lanes sit on, so they merge.
        if (soft) using (Pen sheet = new Pen(mid, ry * 1.3f))
        {
            sheet.StartCap = sheet.EndCap = LineCap.Round;
            for (int i = 0; i < n; i += 2)
            {
                double a = 2 * Math.PI * i / n, b = 2 * Math.PI * (i + 2) / n;
                if ((Math.Sin(a) < 0) != back) continue;
                sheet.Color = Color.FromArgb(Clamp(70 * bright * Beaming(a)), mid);
                g.DrawLine(sheet, (float)(Math.Cos(a) * rx * 0.7f), (float)(Math.Sin(a) * ry * 0.75f), (float)(Math.Cos(b) * rx * 0.7f), (float)(Math.Sin(b) * ry * 0.75f));
            }
        }
        if (soft) return;
        float[] lanes = { 0.38f, 0.52f, 0.7f, 0.92f };
        for (int l = 0; l < lanes.Length; l++)
        {
            float k = lanes[l], lrx = rx * k, lry = ry * (0.55f + 0.45f * k);
            float heat = 1 - (float)l / (lanes.Length - 1);
            Color col = heat > 0.5f ? Blend(mid, hot, (heat - 0.5f) * 2) : Blend(outer, mid, heat * 2);
            float v = bright * (0.45f + 0.55f * heat);
            DrawLane(g, phase, l, -lrx, -lry, 2 * lrx, 2 * lry, back ? 180 : 0, 180, col, v, 1.4f + heat * 0.8f, false);
        }
    }

    // One lane of gas as a single arc: a gradient across it, brighter on
    // the side turning toward you, and dashes that slide along it as the gas
    // flows. One call per lane, where drawing it in pieces took dozens.
    static void DrawLane(Graphics g, double phase, int lane, float x, float y, float w, float h, float start, float sweep,
        Color col, float v, float width, bool fadeEnds)
    {
        if (w < 1 || h < 0.5f) return;
        RectangleF box = new RectangleF(x - 1, y - 1, w + 2, h + 2);
        using (LinearGradientBrush brush = new LinearGradientBrush(box, col, col, LinearGradientMode.Horizontal))
        {
            ColorBlend blend = new ColorBlend(3);
            if (fadeEnds)
            {
                blend.Positions = new[] { 0f, 0.2f, 0.45f, 0.75f, 1f };
                blend.Colors = new[] { Color.FromArgb(0, col), Color.FromArgb(Clamp(235 * v), col), Color.FromArgb(Clamp(255 * v), col),
                    Color.FromArgb(Clamp(170 * v), col), Color.FromArgb(0, col) };
            }
            else
            {
                // Left, turning toward you, brightest; right, turning away, dimmest.
                blend.Positions = new[] { 0f, 0.35f, 1f };
                blend.Colors = new[] { Color.FromArgb(Clamp(255 * v), col), Color.FromArgb(Clamp(190 * v), col), Color.FromArgb(Clamp(80 * v), col) };
            }
            brush.InterpolationColors = blend;
            using (Pen p = new Pen(brush, width))
            {
                // Streaks: uneven dashes, each lane its own rhythm, drifting along.
                p.DashPattern = lane % 2 == 0 ? new[] { 9f, 0.55f, 4.5f, 0.45f } : new[] { 6.5f, 0.5f, 11f, 0.6f };
                p.DashOffset = (float)(phase * (2.4 + 0.5 * lane) % 100);
                p.DashCap = DashCap.Flat;
                g.DrawArc(p, x, y, w, h, start, sweep);
            }
        }
    }

    // The disk's light bent round the void: an arch over the top, or a thin
    // band under the bottom. Brightest straight above or below the void.
    static void DiskArch(Graphics g, double phase, float c, Color hot, Color mid, Color outer, float bright, bool top, bool soft)
    {
        int lanes = top ? 4 : 2;
        float squash = top ? 0.98f : 0.82f;
        const int n = 28;
        // A soft glow first, so the arch reads as one band of light.
        if (soft) using (Pen glowPen = new Pen(mid, top ? 7f : 3.5f))
        {
            Pen soft2 = glowPen;
            soft2.StartCap = soft2.EndCap = LineCap.Round;
            float r = c + (top ? 6.5f : 3.2f);
            for (int i = 0; i < n; i += 2)
            {
                double u = (double)i / n, v2 = (double)(i + 2) / n;
                double a = top ? Math.PI * (1.02 + 0.96 * u) : Math.PI * (0.04 + 0.92 * u);
                double b = top ? Math.PI * (1.02 + 0.96 * v2) : Math.PI * (0.04 + 0.92 * v2);
                soft2.Color = Color.FromArgb(Clamp(85 * bright * Math.Pow(Math.Sin(Math.PI * u), 0.6)), mid);
                g.DrawLine(soft2, (float)(Math.Cos(a) * r), (float)(Math.Sin(a) * r * squash), (float)(Math.Cos(b) * r), (float)(Math.Sin(b) * r * squash));
            }
        }
        if (soft) return;
        for (int l = 0; l < lanes; l++)
        {
            float r = c + (top ? 2.3f + l * 2.7f : 1.9f + l * 1.6f);
            float heat = 1 - (float)l / lanes;
            Color col = heat > 0.5f ? Blend(mid, hot, (heat - 0.5f) * 2) : Blend(outer, mid, heat * 2);
            float v = bright * (0.4f + 0.6f * heat);
            DrawLane(g, phase, l + 4, -r, -r * squash, 2 * r, 2 * r * squash, top ? 184 : 8, top ? 172 : 164, col, v, 1.5f + heat * 0.8f, true);
        }
    }

    // The accretion ring as short strokes round a tilted ellipse, brighter
    // on the side turning toward you. back: the far half, behind the void.
    static void DrawRing(Graphics g, float a0, float rx, float ry, Color c, float flicker, bool back)
    {
        const int n = 72;
        using (Pen soft = new Pen(Color.FromArgb((int)(60 * flicker), c), 5f))
        {
            soft.StartCap = soft.EndCap = LineCap.Round;
            for (int i = 0; i < n; i += 2)
            {
                double a = 2 * Math.PI * i / n, b = 2 * Math.PI * (i + 2) / n;
                if ((Math.Sin(a) < 0) != back) continue;
                g.DrawLine(soft, (float)(Math.Cos(a) * rx), (float)(Math.Sin(a) * ry), (float)(Math.Cos(b) * rx), (float)(Math.Sin(b) * ry));
            }
        }
        using (Pen p = new Pen(c, 2f))
        {
            p.StartCap = p.EndCap = LineCap.Round;
            for (int i = 0; i < n; i++)
            {
                double a = 2 * Math.PI * i / n, b = 2 * Math.PI * (i + 1) / n;
                if ((Math.Sin(a) < 0) != back) continue;
                double lit = 0.35 + 0.65 * (0.5 - 0.5 * Math.Cos(a + a0 * 0.15));
                // Light trails: a few brighter streaks that run round the ring.
                double streak = Math.Max(0, Math.Cos(3 * a - a0 * 2));
                // Far side dimmer, eased across the ends so the halves never show a seam.
                double near = 0.55 + 0.45 * Math.Max(0, Math.Min(1, (Math.Sin(a) + 0.3) / 0.6));
                int alpha = (int)(255 * flicker * Math.Min(1, near * (lit + 0.35 * streak * streak)));
                p.Color = Color.FromArgb(Math.Max(0, Math.Min(255, alpha)), c);
                g.DrawLine(p, (float)(Math.Cos(a) * rx), (float)(Math.Sin(a) * ry), (float)(Math.Cos(b) * rx), (float)(Math.Sin(b) * ry));
            }
        }
    }

    // Motes in orbit, pulled round with the ring; livelier as energy rises.
    void DrawMotes(Graphics g, double t, float a0, float rx, float ry, Color c, float energy, float flicker, bool back)
    {
        int n = battery ? Motes.Length / 2 : Motes.Length;
        for (int i = 0; i < n; i++)
        {
            float[] m = Motes[i];
            double a = m[1] + a0 * m[2] * (0.6 + energy);
            if ((Math.Sin(a) < 0) != back) continue;
            float x = (float)(Math.Cos(a) * rx * m[0]), y = (float)(Math.Sin(a) * ry * m[0]);
            float s = m[3] * (0.8f + 0.4f * energy);
            double near = 0.5 + 0.5 * Math.Max(0, Math.Min(1, (Math.Sin(a) + 0.3) / 0.6));
            int alpha = (int)(255 * flicker * m[4] * (0.25 + 0.75 * energy) * near);
            Color tint = i % 4 == 0 ? Starlight : c;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(Math.Max(0, Math.Min(255, alpha)), tint)))
                g.FillEllipse(b, x - s / 2, y - s / 2, s, s);
        }
    }

    // Two small energy arcs from the ring toward the void while it works.
    // They ride round with the ring and ripple smoothly, so they read as a
    // current, not as sparks jumping from place to place.
    static void DrawArcs(Graphics g, double t, float rx, float ry, float core, Color c)
    {
        for (int k = 0; k < 2; k++)
        {
            double a = t * 0.9 + k * Math.PI;
            float life = (float)(0.5 + 0.5 * Math.Sin(t * 2.4 + k * 1.7));
            PointF from = new PointF((float)(Math.Cos(a) * rx), (float)(Math.Sin(a) * ry));
            PointF to = new PointF(from.X * (core + 3) / rx, from.Y * 0.4f);
            PointF[] pts = new PointF[5];
            for (int i = 0; i < 5; i++)
            {
                float u = i / 4f;
                float bend = (float)Math.Sin(u * Math.PI) * (float)Math.Sin(t * 5 + k * 2 + i) * 2.2f;
                pts[i] = new PointF(from.X + (to.X - from.X) * u, from.Y + (to.Y - from.Y) * u + bend);
            }
            using (Pen p = new Pen(Color.FromArgb((int)(60 + 150 * life), Blend(c, Starlight, 0.4f)), 1.1f))
            {
                p.StartCap = p.EndCap = LineCap.Round;
                g.DrawCurve(p, pts, 0.5f);
            }
        }
    }

    // The finish: two rippling rings of starlight and a few four-point stars.
    static void DrawStarlight(Graphics g, double t, double since, float core)
    {
        for (int w = 0; w < 2; w++)
        {
            double ph = (since * 0.9 + w * 0.5) % 1;
            float r = core + 5 + (float)ph * 26;
            using (Pen p = new Pen(Color.FromArgb((int)(130 * (1 - ph) * (1 - ph)), Blend(Cyan, Starlight, 0.6f)), 1.2f))
                g.DrawEllipse(p, -r, -r, 2 * r, 2 * r);
        }
        Random rnd = new Random(11);
        for (int k = 0; k < 6; k++)
        {
            double a = rnd.NextDouble() * Math.PI * 2, d = 20 + rnd.NextDouble() * 18;
            float twinkle = (float)Math.Max(0, Math.Sin(t * 6 + k * 1.3));
            float x = (float)(Math.Cos(a) * d), y = (float)(Math.Sin(a) * d * 0.8), s = 2 + 3.5f * twinkle;
            using (Pen p = new Pen(Color.FromArgb((int)(230 * twinkle), Starlight), 1f))
            {
                g.DrawLine(p, x - s, y, x + s, y);
                g.DrawLine(p, x, y - s, x, y + s);
            }
        }
    }

    const float GlowR = 24 + 6 * 3.4f;
    readonly Dictionary<long, Bitmap> glows = new Dictionary<long, Bitmap>();

    // The glow for one colour at one of 64 strengths, at this screen's scale.
    Bitmap Glow(Color c, float glow)
    {
        int level = Math.Max(0, Math.Min(64, (int)Math.Round(glow * 64)));
        long key = (long)(c.ToArgb() & 0xFFFFFF) * 128 + level;
        Bitmap b;
        if (glows.TryGetValue(key, out b)) return b;
        if (glows.Count > 200) { foreach (Bitmap old in glows.Values) old.Dispose(); glows.Clear(); }

        int px = (int)Math.Ceiling(2 * GlowR * scale * Zoom);
        b = new Bitmap(px, px, PixelFormat.Format32bppPArgb);
        b.SetResolution(96, 96);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.ScaleTransform(px / (2 * GlowR), px / (2 * GlowR));
            g.TranslateTransform(GlowR, GlowR);
            float strength = level / 64f;
            for (int i = 6; i >= 0; i--)
                using (SolidBrush brush = new SolidBrush(Color.FromArgb((int)(strength * (14 + (6 - i) * 6)), c)))
                {
                    float r = 24 + i * 3.4f;
                    g.FillEllipse(brush, -r, -r, 2 * r, 2 * r);
                }
        }
        glows[key] = b;
        return b;
    }

    static Color Blend(Color a, Color b, float k)
    {
        return Color.FromArgb(a.A, (int)(a.R + (b.R - a.R) * k), (int)(a.G + (b.G - a.G) * k), (int)(a.B + (b.B - a.B) * k));
    }

    /* -------------------------------------------------------------- *
     * The tray icon: Voidling in miniature, in the taskbar's corner
     * -------------------------------------------------------------- */

    // The icon by the clock shows what the pet would: dim amber at rest,
    // gold with a bright spot circling while a task runs, steady blue once
    // one finishes and waits for you, crimson and pulsing when one needs
    // your approval. A small number counts the tasks when there is more
    // than one. With the pet set to the taskbar icon only, this is all
    // there is on screen, and a click opens the task list.
    NotifyIcon trayIcon;
    string trayBase = "Prompt Advisor";
    IntPtr trayHandle = IntPtr.Zero;
    Icon trayShown;
    string trayKey = "";
    volatile int liveTasks;
    volatile string runTitle = "";
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    // The tray keeps its own timer, since the pet's stops while it is
    // hidden. With the pet away it also reads the board once a second and
    // puts away finishes you have gone and looked at, as the pet would.
    readonly Timer trayTick = new Timer();
    int trayTicks, trayFaults;

    // The icon shows only while there is something to show: from the
    // moment a task starts until 30 seconds after the last one has ended
    // and nothing waits on you, as the pet does. Opening the task list,
    // by launching Start.cmd again, brings it back for a minute so the
    // menu can be reached. With the pet set to Always show, so is it.
    const double TrayIdle = 30, TrayWake = 60;
    double trayBusyAt = -1000, trayWakeAt = -1000;
    double hoverSeenAt = -1;
    bool quietWake;

    // With the finishes put away and nothing running or waiting on you,
    // the icon and button leave now instead of lingering.
    void Settle()
    {
        if (Busy()) return;
        trayBusyAt = -1000;
        trayWakeAt = -1000;
        UpdateTray();
    }

    public void AttachTray(NotifyIcon tray)
    {
        trayIcon = tray;
        trayBase = tray.Text;
        trayStatic = tray.Icon;
        trayWakeAt = clock.Elapsed.TotalSeconds;
        board.Shown += delegate
        {
            // A second launch brings the button back for a minute; opening
            // it from the button itself does not.
            if (quietWake) quietWake = false; else trayWakeAt = clock.Elapsed.TotalSeconds;
            UpdateTray();
        };
        button = new TaskbarButton();
        // Opening the list from the button is looking: finishes are put away
        // and, with nothing else going on, the button leaves at once.
        button.Clicked += delegate { OpenFromTaskbar(Cursor.Position); };
        board.Avoid = delegate { return Visible ? Bounds : Rectangle.Empty; };
        // Resting on it shows the card; two seconds later the finishes it
        // named count as seen.
        button.Hovered += delegate { if (alerts.Exists(delegate (TaskItem a) { return a.State != "needs"; })) hoverSeenAt = clock.Elapsed.TotalSeconds + 2; };
        button.Card = DrawCard;
        trayTick.Interval = 100;
        trayTick.Tick += delegate
        {
            // A fault here is logged and skipped, never thrown: the icon must
            // not take the companion down, or raise a dialog nobody asked for.
            try
            {
                if (!Visible && ++trayTicks % 10 == 0) { Sync(); ClearSeen(); }
                UpdateTray();
            }
            catch (Exception e)
            {
                if (++trayFaults == 1 || trayFaults % 600 == 0) LogNote("taskbar icon skipped a tick (" + trayFaults + "): " + e.Message);
            }
        };
        trayTick.Enabled = true;
        UpdateTray();
    }

    // A click on the taskbar button or the tray icon. With just one task to
    // show, running, finished or waiting on you, it opens that task's app
    // or conversation straight away. With more, or none, the list opens
    // above the click. Either way finishes count as seen, and with nothing
    // else going on the button leaves.
    public void OpenFromTaskbar(Point at)
    {
        TaskItem one = OnlyTask();
        if (one != null) board.Open(one);
        else { quietWake = true; board.Show(at); }
        TrayOpened();
        Settle();
    }

    // The one task the hover card would show, or null when it would show
    // more than one or none: needs you and running, then finishes not yet
    // seen, each task once.
    TaskItem OnlyTask()
    {
        Dictionary<string, TaskItem> shown = new Dictionary<string, TaskItem>();
        foreach (TaskItem it in board.Snapshot())
        {
            bool live = it.State == "needs" || it.State == "working" || it.State == "hidden";
            if (live && (DateTime.Now - it.Updated).TotalHours < 3) shown[it.Key] = it;
            if (shown.Count > 1) return null;
        }
        foreach (TaskItem a in alerts) if (!shown.ContainsKey(a.Key)) shown[a.Key] = a;
        if (shown.Count != 1) return null;
        foreach (TaskItem it in shown.Values) return it;
        return null;
    }

    // Opening the list from the tray counts as looking: finishes are put
    // away, and a request for your approval stays until it is answered.
    public void TrayOpened()
    {
        foreach (TaskItem a in alerts) if (a.State != "needs") dismissed.Add(Stamp(a));
        alerts.RemoveAll(delegate (TaskItem a) { return a.State != "needs"; });
        flash = null;
        UpdateTray();
    }

    // The app's own icon, which the tray keeps while the taskbar button
    // carries the state, so its menu can always be reached.
    Icon trayStatic;
    bool trayPlain;

    void UpdateTray()
    {
        if (trayIcon == null) return;
        double t = clock.Elapsed.TotalSeconds;
        if (hoverSeenAt > 0 && t >= hoverSeenAt) { hoverSeenAt = -1; TrayOpened(); Settle(); return; }
        if (Busy()) trayBusyAt = t;
        bool wanted = Mode == "always" || t - trayBusyAt < TrayIdle || t - trayWakeAt < TrayWake;
        TaskItem shown;
        Mood mood = PickMood(t, out shown);
        UpdateButton(wanted && ButtonOn, mood, t);
        string tip = trayBase + TrayTip();
        if (tip.Length > 63) tip = tip.Substring(0, 63);
        if (ButtonOn)
        {
            if (!trayPlain)
            {
                trayIcon.Icon = trayStatic;
                if (trayShown != null) { trayShown.Dispose(); trayShown = null; }
                if (trayHandle != IntPtr.Zero) { DestroyIcon(trayHandle); trayHandle = IntPtr.Zero; }
                trayPlain = true;
                trayKey = "";
            }
            if (!trayIcon.Visible) trayIcon.Visible = true;
            if (trayIcon.Text != tip) trayIcon.Text = tip;
            return;
        }
        trayPlain = false;
        if (trayIcon.Visible != wanted) trayIcon.Visible = wanted;
        if (!wanted) return;
        bool moving = mood == Mood.Working || mood == Mood.Thinking || mood == Mood.Needs;
        int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        int count = liveTasks;
        // Ten frames a second while something moves; otherwise only on a change.
        string key = mood + "|" + count + "|" + size + "|" + (moving ? ((long)(t * 10)).ToString() : "");
        if (key == trayKey) return;
        trayKey = key;
        try
        {
            IntPtr h;
            using (Bitmap b = TrayGlyph(size, mood, t, count)) h = b.GetHicon();
            Icon next = Icon.FromHandle(h);
            trayIcon.Icon = next;
            if (trayShown != null) trayShown.Dispose();
            if (trayHandle != IntPtr.Zero) DestroyIcon(trayHandle);
            trayShown = next;
            trayHandle = h;
            trayIcon.Text = tip;
        }
        catch (Exception e) { LogNote("tray icon could not draw: " + e.Message); }
    }

    // The taskbar button, with the other apps: the one place the tasks show.
    // Its icon is Voidling, redrawn ten times a second while something
    // moves; a badge in its corner counts the tasks running or waiting on
    // you; its label starts with the state; it flashes once when a task
    // finishes or needs you; a click opens the task list.
    TaskbarButton button;
    public bool ButtonOn = true;
    string buttonGlyph = "", buttonBadge = "", cardSays = "";
    volatile string[][] cardRows = new string[0][];

    public void SetButton(bool on) { ButtonOn = on; Save(); UpdateTray(); }

    void UpdateButton(bool on, Mood mood, double t)
    {
        if (button == null) return;
        try
        {
            button.Present(on);
            if (!on) { buttonGlyph = buttonBadge = ""; return; }
            TaskItem need = alerts.Find(delegate (TaskItem a) { return a.State == "needs"; });
            List<TaskItem> done = alerts.FindAll(delegate (TaskItem a) { return a.State != "needs"; });
            int runs = working + hiddenRuns;
            string label;
            if (need != null) label = "Needs you · " + need.Title;
            else if (runs > 0)
            {
                label = runs == 1 ? "Running · " + runTitle : runs + " running";
                if (done.Count > 0) label += " · " + done.Count + " done";
            }
            else if (done.Count > 0) label = "Done · " + done[done.Count - 1].Title + (done.Count > 1 ? " +" + (done.Count - 1) : "");
            else label = "Quiet · Claude tasks";
            if (label != cardSays) { cardSays = label; button.CardChanged(); }

            bool moving = mood == Mood.Working || mood == Mood.Thinking || mood == Mood.Needs;
            int size = Math.Max(32, SystemInformation.IconSize.Width);
            string key = mood + "|" + size + "|" + (moving ? ((long)(t * 10)).ToString() : "");
            Bitmap glyph = null;
            if (key != buttonGlyph) { buttonGlyph = key; glyph = TrayGlyph(size, mood, t, 0); }

            int count = liveTasks;
            string badgeKey = count + "|" + (need != null) + "|" + (runs > 0);
            bool badgeChanged = badgeKey != buttonBadge;
            Bitmap badge = null;
            string badgeText = "";
            if (badgeChanged)
            {
                buttonBadge = badgeKey;
                if (count > 0)
                {
                    badge = BadgeGlyph(Math.Max(16, SystemInformation.SmallIconSize.Width), count, need != null);
                    badgeText = count + (count == 1 ? " task" : " tasks");
                }
            }
            using (glyph)
            using (badge)
                button.Set("", alerts.Count > 0, glyph, badgeChanged, badge, badgeText);
        }
        catch (Exception e) { LogNote("taskbar button: " + e.Message); }
    }

    // The count in the corner of the button: gold for tasks running, red
    // when one of them waits on you.
    static Bitmap BadgeGlyph(int s, int count, bool needs)
    {
        Bitmap b = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            Color fill = needs ? Color.FromArgb(226, 46, 52) : Gold2;
            using (SolidBrush f = new SolidBrush(fill)) g.FillEllipse(f, 0.5f, 0.5f, s - 1, s - 1);
            using (Pen o = new Pen(Color.FromArgb(200, Void), 1f)) g.DrawEllipse(o, 0.5f, 0.5f, s - 1, s - 1);
            string n = count > 9 ? "9+" : count.ToString();
            using (Font font = new Font("Segoe UI", s * (n.Length > 1 ? 0.5f : 0.66f), FontStyle.Bold, GraphicsUnit.Pixel))
            using (SolidBrush ink = new SolidBrush(needs ? Color.White : Void))
                g.DrawString(n, font, ink, new RectangleF(-2, -1, s + 4, s + 2), Centre);
        }
        return b;
    }

    // The hover card: a short list of what is going on, each line led by a
    // dot in its colour. Needs you first, then running, then finished.
    Bitmap DrawCard(int maxW, int maxH)
    {
        List<string[]> rows = new List<string[]>(cardRows);
        foreach (TaskItem a in alerts)
            if (a.State != "needs") rows.Add(new[] { "done", a.Title ?? a.Source ?? "" });
        rows.Sort(delegate (string[] x, string[] y) { return Rank(x[0]).CompareTo(Rank(y[0])); });
        if (rows.Count == 0) rows.Add(new[] { "quiet", "Nothing running" });

        float k = Math.Max(1f, scale);
        int lineH = (int)(22 * k), pad = (int)(10 * k);
        int shown = Math.Min(rows.Count, Math.Max(1, (maxH - 2 * pad) / lineH));
        int w = Math.Min(maxW, (int)(260 * k)), h = Math.Min(maxH, 2 * pad + shown * lineH);
        Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(b))
        using (Font f = new Font("Segoe UI", 12.5f * k, GraphicsUnit.Pixel))
        using (SolidBrush ink = new SolidBrush(Paper))
        {
            g.Clear(Color.FromArgb(255, 32, 32, 36));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            StringFormat one = new StringFormat(StringFormatFlags.NoWrap);
            one.Trimming = StringTrimming.EllipsisCharacter;
            one.LineAlignment = StringAlignment.Center;
            for (int i = 0; i < shown; i++)
            {
                string[] r = rows[i];
                bool last = i == shown - 1 && rows.Count > shown;
                string text = last ? "+" + (rows.Count - shown + 1) + " more" : r[1];
                Color dot = r[0] == "needs" ? Crimson : r[0] == "done" ? BlueWhite : r[0] == "run" ? Gold2 : Dim;
                float y = pad + i * lineH;
                float d = 8 * k;
                using (SolidBrush db = new SolidBrush(dot)) g.FillEllipse(db, pad, y + (lineH - d) / 2, d, d);
                g.DrawString(text, f, ink, new RectangleF(pad + d + 8 * k, y, w - pad * 2 - d - 8 * k, lineH), one);
            }
        }
        return b;
    }

    static int Rank(string state) { return state == "needs" ? 0 : state == "run" ? 1 : state == "done" ? 2 : 3; }

    string TrayTip()
    {
        int done = alerts.FindAll(delegate (TaskItem a) { return a.State != "needs"; }).Count;
        int needs = alerts.Count - done;
        int runs = working + hiddenRuns;
        List<string> parts = new List<string>();
        if (needs > 0) parts.Add(needs + " needs you");
        if (runs > 0) parts.Add(runs + " running");
        if (done > 0) parts.Add(done + " finished");
        return parts.Count == 0 ? "" : "\n" + string.Join(", ", parts.ToArray());
    }

    // The icon, size pixels square: an origami crane in profile, drawn on a
    // 100-unit grid. While a task runs its wing flaps; once one finishes it
    // folds its wing and turns blue; when one needs you it turns crimson and
    // pulses; at rest it sits folded in dim amber. A thin dark edge keeps it
    // readable on a light taskbar, and the count goes in the corner.
    static Bitmap TrayGlyph(int s, Mood mood, double t, int count)
    {
        Bitmap b = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            Color body, wing;
            float lit = 1;
            bool flapping = false;
            double beat = 0.9;              // seconds a wingbeat takes
            switch (mood)
            {
                case Mood.Thinking: body = CraneGold; wing = CraneGoldDeep; flapping = true; beat = 0.65; break;
                case Mood.Working: body = CraneGold; wing = CraneGoldDeep; flapping = true; break;
                case Mood.Done: body = CraneBlue; wing = CraneBlueDeep; break;
                case Mood.Needs:
                    body = CraneRed; wing = Crimson;
                    lit = 0.55f + 0.45f * (float)(0.5 + 0.5 * Math.Sin(t * 4));
                    break;
                case Mood.Hidden: body = Blend(CraneGold, Void, 0.4f); wing = Blend(CraneGoldDeep, Void, 0.4f); break;
                default: body = Color.FromArgb(214, 160, 104); wing = Color.FromArgb(176, 112, 58); lit = 0.85f; break;
            }
            bool badge = count > 1;
            float k = s / 100f * (badge ? 0.86f : 1f);
            // Centred on its own outline, which sits a little low on the grid.
            float ox = badge ? -s * 0.02f : 0, oy = (badge ? -s * 0.04f : 0) - s * 0.06f;
            Func<float, float, PointF> P = delegate (float x, float y) { return new PointF(ox + x * k, oy + y * k); };

            // The classic folded crane: a diamond body, the tail rising to
            // the left and the neck to the right in a V, the head bent down.
            PointF[] bodyPts = { P(30, 64), P(50, 56), P(70, 64), P(50, 76) };
            PointF[] tail = { P(6, 32), P(46, 60), P(30, 70) };
            PointF[] neck = { P(54, 70), P(72, 60), P(90, 22) };
            PointF[] beak = { P(90, 22), P(98, 31), P(85, 29) };
            // The wing: a tall fold above the back, beating while it works,
            // folded down along the body otherwise.
            PointF[] wingPts;
            if (flapping)
            {
                // Down fast, up slower, the way a wingbeat looks; the tip
                // swings from high above the back to below the body.
                double ph = (t / beat) % 1.0;
                double c = ph < 0.4 ? Math.Cos(ph / 0.4 * Math.PI) : -Math.Cos((ph - 0.4) / 0.6 * Math.PI);
                float lift = (float)(0.15 + 0.85 * c);         // 1 up, -0.7 down
                float hinge = 60;
                wingPts = new[] { P(38, 61), P(50 - 4 * lift, hinge + (12 - hinge) * lift), P(62, 60) };
            }
            else wingPts = new[] { P(36, 62), P(58, 40), P(64, 62) };
            PointF[][] paper = { tail, neck, beak, bodyPts };

            float edge = Math.Max(1f, s / 22f);
            using (Pen shade = new Pen(Color.FromArgb(120, Void), edge * 1.6f))
            {
                shade.LineJoin = LineJoin.Round;
                foreach (PointF[] part in paper) g.DrawPolygon(shade, part);
                g.DrawPolygon(shade, wingPts);
            }
            using (SolidBrush bb = new SolidBrush(Color.FromArgb(Clamp(255 * lit), body)))
                foreach (PointF[] part in paper) g.FillPolygon(bb, part);
            using (SolidBrush wb = new SolidBrush(Color.FromArgb(Clamp(255 * lit), wing))) g.FillPolygon(wb, wingPts);
            // A crease down the body, the fold that makes it read as paper.
            using (Pen crease = new Pen(Color.FromArgb(Clamp(110 * lit), wing), Math.Max(0.8f, s / 40f)))
                g.DrawLine(crease, P(30, 64), P(70, 64));

            if (badge)
            {
                string n = count > 9 ? "9+" : count.ToString();
                float br = s * 0.27f, bx = s - br, by = s - br;
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(240, 20, 18, 26))) g.FillEllipse(bg, bx - br, by - br, 2 * br, 2 * br);
                using (Pen o = new Pen(Color.FromArgb(Clamp(255 * lit), body), 1f)) g.DrawEllipse(o, bx - br + 0.5f, by - br + 0.5f, 2 * br - 1, 2 * br - 1);
                using (Font f = new Font("Segoe UI", s * (n.Length > 1 ? 0.3f : 0.4f), FontStyle.Bold, GraphicsUnit.Pixel))
                using (SolidBrush ink = new SolidBrush(Color.White))
                    g.DrawString(n, f, ink, new RectangleF(bx - br - 2, by - br - 1, 2 * br + 4, 2 * br + 2), Centre);
            }
        }
        return b;
    }

    static readonly Color CraneGold = Color.FromArgb(250, 199, 117);
    static readonly Color CraneGoldDeep = Color.FromArgb(239, 159, 39);
    static readonly Color CraneBlue = Color.FromArgb(133, 183, 235);
    static readonly Color CraneBlueDeep = Color.FromArgb(55, 138, 221);
    static readonly Color CraneRed = Color.FromArgb(240, 149, 149);

    /* -------------------------------------------------------------- *
     * Planets: one for each task that is running or waits on you
     * -------------------------------------------------------------- */

    // Each task still going is a small planet on a slow orbit, numbered as
    // its row in the task list. When the task finishes, its planet spirals
    // into the void, heated and stretched thin by the pull, and the gas
    // flares as it goes in. A task that ends any other way (closed, lost,
    // gone from the board) just fades, so a swallow always means a finish.
    sealed class Planet
    {
        public string Key, State = "working";
        public int Number;
        public bool Faint, Fell, Shown = true;
        public double Born, Appear, Gone = -1;
        public float Off, OffFrom;
        public double OffAt = -100;
        public Planet Copy() { return (Planet)MemberwiseClone(); }
    }

    const int MaxPlanets = 5;
    const double Fall = 1.2, FadeOut = 0.6, Grow = 0.6, Ease = 1.0;
    const double Omega = 2 * Math.PI / 24;      // one lap in 24 seconds
    const float OrbitR = 31, OrbitRy = 10.5f, Incline = 15, PlanetR = 6.4f;
    static readonly StringFormat Centre = MakeCentre();
    readonly List<Planet> planets = new List<Planet>();
    readonly object planetGate = new object();
    volatile int extraPlanets;

    static StringFormat MakeCentre()
    {
        StringFormat f = new StringFormat();
        f.Alignment = f.LineAlignment = StringAlignment.Center;
        return f;
    }

    // Brings the planets in line with the board: one per row the list shows
    // under Needs you and Working, numbered in the list's order.
    void Orbit(List<TaskItem> all, Dictionary<string, TaskItem> byKey, double now)
    {
        List<TaskItem> live = new List<TaskItem>();
        for (int pass = 0; pass < 2; pass++)
            foreach (TaskItem it in all)
            {
                if ((DateTime.Now - it.Updated).TotalHours >= 3) continue;
                bool fits = pass == 0 ? it.State == "needs" : it.State == "working" || it.State == "hidden";
                if (fits) live.Add(it);
            }
        lock (planetGate)
        {
            Dictionary<string, Planet> alive = new Dictionary<string, Planet>();
            foreach (Planet p in planets) if (p.Gone < 0) alive[p.Key] = p;
            HashSet<string> keep = new HashSet<string>();
            for (int i = 0; i < live.Count; i++)
            {
                TaskItem it = live[i];
                Planet p;
                if (!alive.TryGetValue(it.Key, out p))
                {
                    p = new Planet();
                    p.Key = it.Key; p.Born = p.Appear = now; p.Shown = false;
                    planets.Add(p);
                }
                bool shown = i < MaxPlanets;
                if (shown && !p.Shown) p.Appear = now;
                p.Shown = shown;
                p.State = it.State;
                p.Faint = it.State == "hidden" && it.App != "claude";
                p.Number = i + 1;
                keep.Add(it.Key);
            }
            foreach (Planet p in planets)
            {
                if (p.Gone >= 0 || keep.Contains(p.Key)) continue;
                TaskItem ended;
                p.Gone = now;
                p.Fell = p.Shown && byKey.TryGetValue(p.Key, out ended) && ended.State == "done";
            }
            planets.RemoveAll(delegate (Planet p) { return p.Gone >= 0 && now - p.Gone > (p.Fell ? Fall + 0.5 : FadeOut); });

            // The planets shown share the orbit evenly, in the order they
            // came, and ease to their new places when one comes or goes.
            List<Planet> ring = planets.FindAll(delegate (Planet p) { return p.Gone < 0 && p.Shown; });
            ring.Sort(delegate (Planet a, Planet b) { return a.Born.CompareTo(b.Born); });
            for (int i = 0; i < ring.Count; i++)
            {
                Planet p = ring[i];
                float want = (float)(2 * Math.PI * i / ring.Count);
                if (p.OffAt < -50) { p.Off = p.OffFrom = want; p.OffAt = now - Ease; continue; }
                if (Math.Abs(Wrap(want - p.Off)) < 1e-3) continue;
                p.OffFrom = OffNow(p, now);
                p.Off = want;
                p.OffAt = now;
            }
            extraPlanets = Math.Max(0, live.Count - MaxPlanets);
            liveTasks = live.Count;
            TaskItem run = live.Find(delegate (TaskItem it) { return it.State != "needs"; });
            List<string[]> rows = new List<string[]>();
            foreach (TaskItem it in live) rows.Add(new[] { it.State == "needs" ? "needs" : "run", it.Title ?? it.Source ?? "" });
            cardRows = rows.ToArray();
            runTitle = run == null ? "" : run.Title ?? "";
        }
    }

    // A copy for the frame being drawn, so a Sync on the other thread
    // never changes a planet halfway through.
    Planet[] Planets()
    {
        lock (planetGate) return planets.ConvertAll(delegate (Planet p) { return p.Copy(); }).ToArray();
    }

    static float Wrap(double a)
    {
        while (a > Math.PI) a -= 2 * Math.PI;
        while (a <= -Math.PI) a += 2 * Math.PI;
        return (float)a;
    }

    static float OffNow(Planet p, double t)
    {
        double k = Math.Max(0, Math.Min(1, (t - p.OffAt) / Ease));
        return p.OffFrom + Wrap(p.Off - p.OffFrom) * (float)(k * k * (3 - 2 * k));
    }

    // How much a planet arriving at the void lights the gas, 0 to 1.
    static float Swallow(Planet[] orbit, double t)
    {
        float most = 0;
        foreach (Planet p in orbit)
        {
            if (!p.Fell) continue;
            double k = (t - p.Gone) / Fall;
            double v = k < 0.6 ? 0 : k < 1 ? Math.Pow((k - 0.6) / 0.4, 2) : k < 1.4 ? 1 - (k - 1) / 0.4 : 0;
            most = Math.Max(most, (float)v);
        }
        return most;
    }

    // Draws the planets on one side of their orbit: back, the far side,
    // before the void so it hides them; otherwise the near side, last.
    // The orbit is tipped a little off the disk so the two read apart.
    void DrawPlanets(Graphics g, Planet[] orbit, double t, float tilt, float c, bool back)
    {
        if (orbit.Length == 0) return;
        GraphicsState flat = g.Save();
        g.RotateTransform(-tilt);           // upright, so the numbers read level
        double turn = (tilt + Incline) * Math.PI / 180;
        float cosT = (float)Math.Cos(turn), sinT = (float)Math.Sin(turn);
        using (Font f = new Font("Segoe UI", 8.5f, FontStyle.Bold, GraphicsUnit.Pixel))
            foreach (Planet p in orbit)
            {
                if (!p.Shown) continue;
                double a = t * Omega + OffNow(p, t);
                float reach = 1, alpha = 1, size = 1, stretch = 0;
                if (p.Gone >= 0)
                {
                    double k = (t - p.Gone) / (p.Fell ? Fall : FadeOut);
                    if (k >= 1) continue;
                    if (p.Fell)
                    {
                        // Spiralling in, faster the closer it gets, as an
                        // orbit does, and pulled long toward the void.
                        a = p.Gone * Omega + OffNow(p, p.Gone) + 2.4 * Math.PI * k * k;
                        reach = (float)(1 - 0.85 * k * k);
                        stretch = (float)k;
                        size = (float)(1 - 0.5 * k);
                        alpha = (float)(k < 0.7 ? 1 : 1 - (k - 0.7) / 0.3);
                    }
                    else alpha = (float)(1 - k);
                }
                else
                {
                    double k = Math.Min(1, (t - p.Appear) / Grow);
                    size = (float)(0.3 + 0.7 * k * (2 - k));
                    alpha = (float)k;
                }
                double sin = Math.Sin(a);
                if ((sin < 0) != back) continue;
                float x0 = (float)Math.Cos(a) * OrbitR * reach, y0 = (float)sin * OrbitRy * reach;
                float x = x0 * cosT - y0 * sinT, y = x0 * sinT + y0 * cosT;
                float depth = 1 + 0.12f * (float)sin;   // nearer is a little larger
                DrawPlanet(g, f, p, x, y, PlanetR * size * depth, alpha * (back ? 0.85f : 1f), stretch, t);
            }
        g.Restore(flat);
    }

    // One planet: lit on the side that faces the hot gas round the void,
    // dark on the far side, with its number on the lit face.
    void DrawPlanet(Graphics g, Font f, Planet p, float x, float y, float r, float alpha, float stretch, double t)
    {
        if (alpha <= 0.01f || r < 0.5f) return;
        Color body, ink;
        float glow = 1;
        if (p.State == "needs")
        {
            body = Crimson; ink = Color.White;
            glow = 0.65f + 0.35f * (float)(0.5 + 0.5 * Math.Sin(t * 2.6));
        }
        else if (p.Faint) { body = Blend(Gold2, Void, 0.5f); ink = Paper; glow = 0.7f; }
        else { body = Gold2; ink = Void; }
        if (stretch > 0) body = Blend(body, HotWhite, Math.Min(1, stretch * 1.4f));

        GraphicsState s = g.Save();
        g.TranslateTransform(x, y);
        double toward = Math.Atan2(-y, -x);
        if (stretch > 0)
        {
            float deg = (float)(toward * 180 / Math.PI);
            g.RotateTransform(deg);
            g.ScaleTransform(1 + 2.2f * stretch, 1 - 0.45f * stretch);
            g.RotateTransform(-deg);
        }
        using (SolidBrush h = new SolidBrush(Color.FromArgb(Clamp(55 * alpha * glow), body)))
            g.FillEllipse(h, -r - 1.6f, -r - 1.6f, 2 * r + 3.2f, 2 * r + 3.2f);
        using (SolidBrush night = new SolidBrush(Color.FromArgb(Clamp(255 * alpha), Blend(body, Void, 0.55f))))
            g.FillEllipse(night, -r, -r, 2 * r, 2 * r);
        using (GraphicsPath disc = new GraphicsPath())
        {
            disc.AddEllipse(-r, -r, 2 * r, 2 * r);
            g.SetClip(disc, CombineMode.Intersect);
            float lx = (float)Math.Cos(toward) * r * 0.32f, ly = (float)Math.Sin(toward) * r * 0.32f;
            using (SolidBrush day = new SolidBrush(Color.FromArgb(Clamp(255 * alpha * glow), body)))
                g.FillEllipse(day, lx - 0.95f * r, ly - 0.95f * r, 1.9f * r, 1.9f * r);
            g.ResetClip();
        }
        if (stretch <= 0 && alpha > 0.3f)
            using (SolidBrush b = new SolidBrush(Color.FromArgb(Clamp(255 * alpha), ink)))
                g.DrawString(p.Number.ToString(), f, b, new RectangleF(-r - 3, -r - 3, 2 * r + 6, 2 * r + 6), Centre);
        g.Restore(s);
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
            float h = hint.Length > 0 ? 66 : 48;
            float right = W - 8, bottomY = Math.Max(4 + h, top - 8);
            RectangleF r = new RectangleF(right - w, bottomY - h, w, h);
            bubbleBox = r;

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
            using (SolidBrush b = new SolidBrush(edge)) g.DrawString(head, fh, b, new RectangleF(r.X + 10, r.Y + 6, r.Width - 36, 19), cut);
            using (SolidBrush b = new SolidBrush(Paper)) g.DrawString(body, fb, b, new RectangleF(r.X + 10, r.Y + 25, r.Width - 20, 19), cut);
            if (hint.Length > 0)
                using (SolidBrush b = new SolidBrush(Dim)) g.DrawString(hint, fs, b, new RectangleF(r.X + 10, r.Y + 45, r.Width - 20, 17), cut);
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
                if (kv.ContainsKey("button")) ButtonOn = kv["button"] != "0";
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
            File.WriteAllText(settings, "x=" + Left + "\r\ny=" + Top + "\r\nsound=" + (sound ? 1 : 0) + "\r\nmode=" + Mode + "\r\nbutton=" + (ButtonOn ? 1 : 0) + "\r\n");
        }
        catch { }
    }

    // The window's pixels: a DIB section that GDI+ draws into directly and
    // UpdateLayeredWindow reads from, so a frame needs no new bitmap and no
    // conversion. Made again only when the window's size changes.
    sealed class Canvas : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte Op, Flags, Alpha, Format; }
        [StructLayout(LayoutKind.Sequential)] struct BITMAPINFOHEADER
        {
            public int Size, Width, Height;
            public short Planes, BitCount;
            public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
        }

        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dst, ref POINT at, ref SIZE size, IntPtr src, ref POINT from, int key, ref BLEND blend, int flags);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool GdiFlush();
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);

        public readonly int Width, Height;
        public readonly Bitmap Image;
        readonly IntPtr dc, dib, old;

        public Canvas(int w, int h)
        {
            Width = w; Height = h;
            dc = CreateCompatibleDC(IntPtr.Zero);
            BITMAPINFOHEADER info = new BITMAPINFOHEADER();
            info.Size = 40; info.Width = w; info.Height = -h; // top-down, as GDI+ lays rows out
            info.Planes = 1; info.BitCount = 32;
            IntPtr bits;
            dib = CreateDIBSection(dc, ref info, 0, out bits, IntPtr.Zero, 0);
            old = SelectObject(dc, dib);
            // Premultiplied alpha is what UpdateLayeredWindow expects.
            Image = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits);
            // Font sizes are read against the image's own resolution, which
            // follows the screen's scaling. Pinned at 96, the text is scaled
            // once, by the drawing transform, not twice.
            Image.SetResolution(96, 96);
        }

        public void Push(Form f, byte alpha)
        {
            GdiFlush();
            SIZE size = new SIZE(); size.cx = Width; size.cy = Height;
            POINT from = new POINT(), at = new POINT(); at.x = f.Left; at.y = f.Top;
            BLEND blend = new BLEND(); blend.Alpha = alpha; blend.Format = 1; // AC_SRC_ALPHA
            UpdateLayeredWindow(f.Handle, IntPtr.Zero, ref at, ref size, dc, ref from, 0, ref blend, 2); // ULW_ALPHA
        }

        public void Dispose()
        {
            Image.Dispose();
            SelectObject(dc, old);
            DeleteObject(dib);
            DeleteDC(dc);
        }
    }
}

/* ---------------------------------------------------------------------- *
 * The taskbar button: the tasks, with the other apps on the taskbar
 * ---------------------------------------------------------------------- */

// A window kept minimized, so it has a button on the taskbar like any app,
// and never shows itself. The button's label says what is going on, its
// progress bar moves green while a task runs, fills green when one has
// finished and red when one needs you, and it glows orange until you look.
// A click opens the task list instead of restoring the window.
public class TaskbarButton : Form
{
    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr h);
        void DeleteTab(IntPtr h);
        void ActivateTab(IntPtr h);
        void SetActiveAlt(IntPtr h);
        void MarkFullscreenWindow(IntPtr h, [MarshalAs(UnmanagedType.Bool)] bool full);
        void SetProgressValue(IntPtr h, ulong done, ulong total);
        void SetProgressState(IntPtr h, int flags);
        void RegisterTab(IntPtr tab, IntPtr main);
        void UnregisterTab(IntPtr tab);
        void SetTabOrder(IntPtr tab, IntPtr before);
        void SetTabActive(IntPtr tab, IntPtr main, uint reserved);
        void ThumbBarAddButtons(IntPtr h, uint count, IntPtr buttons);
        void ThumbBarUpdateButtons(IntPtr h, uint count, IntPtr buttons);
        void ThumbBarSetImageList(IntPtr h, IntPtr list);
        void SetOverlayIcon(IntPtr h, IntPtr icon, [MarshalAs(UnmanagedType.LPWStr)] string description);
    }
    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")] class TaskbarListClass { }

    [StructLayout(LayoutKind.Sequential)]
    struct FLASHWINFO { public int cbSize; public IntPtr hwnd; public int dwFlags; public int uCount; public int dwTimeout; }
    [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FLASHWINFO f);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    static readonly int ButtonCreated = RegisterWindowMessage("TaskbarButtonCreated");

    ITaskbarList3 bar;
    bool ready;
    bool flashing;
    IntPtr badgeHandle = IntPtr.Zero;
    string badgeLabel = "";
    bool badgeSent = true;
    IntPtr iconHandle = IntPtr.Zero;
    Icon icon;

    // A click on the button.
    public event Action Clicked;
    // The pointer rests on the button and Windows asks for its preview.
    public event Action Hovered;
    // Draws the hover card at most this size; the pet supplies it.
    public Func<int, int, Bitmap> Card;

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmSetIconicThumbnail(IntPtr h, IntPtr bitmap, int flags);
    [DllImport("dwmapi.dll")] static extern int DwmSetIconicLivePreviewBitmap(IntPtr h, IntPtr bitmap, IntPtr offset, int flags);
    [DllImport("dwmapi.dll")] static extern int DwmInvalidateIconicBitmaps(IntPtr h);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

    public TaskbarButton()
    {
        // No words on the button: the icon says it, and the hover card names
        // the tasks.
        Text = "";
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        Size = new Size(200, 60);
        try { bar = (ITaskbarList3)new TaskbarListClass(); bar.HrInit(); } catch { bar = null; }
    }

    // Puts the button on the taskbar or takes it off, without ever
    // showing the window or taking the focus.
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Hovering shows a card this window draws, not a picture of the
        // window, and there is no full-screen peek behind it.
        int on = 1;
        DwmSetWindowAttribute(Handle, 7, ref on, 4);    // DWMWA_FORCE_ICONIC_REPRESENTATION
        DwmSetWindowAttribute(Handle, 10, ref on, 4);   // DWMWA_HAS_ICONIC_BITMAP
        DwmSetWindowAttribute(Handle, 11, ref on, 4);   // DWMWA_DISALLOW_PEEK
    }

    // What the card would say has changed: Windows asks for it again on
    // the next hover.
    public void CardChanged()
    {
        try { DwmInvalidateIconicBitmaps(Handle); } catch { }
    }

    public void Present(bool on)
    {
        IntPtr h = Handle;
        if (on == IsWindowVisible(h)) return;
        if (on) ShowWindow(h, 7);       // SW_SHOWMINNOACTIVE
        else { ShowWindow(h, 0); ready = false; badgeSent = false; flashing = false; }
    }

    // The button's look: its label, its icon (when given) and the badge in
    // its corner (when changed; none clears it). It flashes once when
    // flash turns on. The thin progress line Windows 11 draws is left off.
    public void Set(string label, bool flash, Bitmap glyph, bool badgeChanged, Bitmap badge, string badgeText)
    {
        if (Text != label) Text = label;
        if (glyph != null)
        {
            IntPtr h = glyph.GetHicon();
            Icon next = Icon.FromHandle(h);
            Icon = next;
            if (icon != null) icon.Dispose();
            if (iconHandle != IntPtr.Zero) DestroyIcon(iconHandle);
            icon = next; iconHandle = h;
        }
        if (badgeChanged)
        {
            IntPtr h = badge != null ? badge.GetHicon() : IntPtr.Zero;
            if (badgeHandle != IntPtr.Zero) DestroyIcon(badgeHandle);
            badgeHandle = h;
            badgeLabel = badgeText ?? "";
            badgeSent = false;
        }
        if (ready && bar != null && !badgeSent)
        {
            try { bar.SetOverlayIcon(Handle, badgeHandle, badgeLabel); badgeSent = true; } catch { }
        }
        if (flash != flashing && IsWindowVisible(Handle))
        {
            if (flash)
            {
                FLASHWINFO f = new FLASHWINFO();
                f.cbSize = Marshal.SizeOf(f);
                f.hwnd = Handle;
                f.dwFlags = 2;      // FLASHW_TRAY
                f.uCount = 3;
                FlashWindowEx(ref f);
            }
            flashing = flash;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0323)   // WM_DWMSENDICONICTHUMBNAIL: lParam holds the largest size
        {
            int w = (int)(((long)m.LParam >> 16) & 0xFFFF), h = (int)((long)m.LParam & 0xFFFF);
            Func<int, int, Bitmap> card = Card;
            if (card != null && w > 0 && h > 0)
            {
                try
                {
                    using (Bitmap b = card(w, h))
                    {
                        IntPtr hb = b.GetHbitmap(Color.FromArgb(0));
                        DwmSetIconicThumbnail(Handle, hb, 0);
                        DeleteObject(hb);
                    }
                }
                catch { }
            }
            Action a = Hovered;
            if (a != null) a();
            return;
        }
        if (m.Msg == 0x0326)   // WM_DWMSENDICONICLIVEPREVIEWBITMAP: no peek, nothing to send
        {
            return;
        }
        if (m.Msg == ButtonCreated)
        {
            ready = true;
            badgeSent = false;
            Set(Text, flashing, null, false, null, null);
        }
        // A click asks the minimized window to come back. It stays down and
        // opens the task list instead.
        if (m.Msg == 0x0112 && ((int)m.WParam & 0xFFF0) == 0xF120)  // WM_SYSCOMMAND, SC_RESTORE
        {
            Action c = Clicked;
            if (c != null) c();
            return;
        }
        // Never let it be shown full size, from Alt+Tab or anywhere else.
        if (m.Msg == 0x0112 && ((int)m.WParam & 0xFFF0) == 0xF030) return;   // SC_MAXIMIZE
        base.WndProc(ref m);
    }

    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        // Brought up some other way, from Alt+Tab say: back down it goes,
        // and the list opens.
        if (!IsIconic(Handle))
        {
            ShowWindow(Handle, 7);
            Action c = Clicked;
            if (c != null) c();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && icon != null) icon.Dispose();
        if (iconHandle != IntPtr.Zero) { DestroyIcon(iconHandle); iconHandle = IntPtr.Zero; }
        if (badgeHandle != IntPtr.Zero) { DestroyIcon(badgeHandle); badgeHandle = IntPtr.Zero; }
        base.Dispose(disposing);
    }
}
