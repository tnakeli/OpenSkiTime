using System.Runtime.InteropServices;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;

namespace OpenSkiTime.Desktop.E2E;

// Records a narrated tutorial from the real running application (docs/tutorial-videos.md). The narration exists before
// the recording: timing.json, written by scripts/tutorial-video/narrate.py, gives each step's length and the moment of
// every cue word. The driver moves the real pointer so that it clicks when the word is spoken. Nothing is drawn into the
// recording; what the composer needs (step starts, pointer track, clicks, camera targets, areas to cover and waits to
// shorten) is written to recording.json in Unix seconds, the time that also stamps every captured frame.
internal sealed class TutorialRecorder : IDisposable
{
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };
    private sealed record Cue(double Seconds, Dictionary<string, double> Cues);
    private readonly DesktopApp _app;
    private readonly string _output;
    private readonly Dictionary<string, Cue> _timing = [];
    private readonly List<object> _steps = [];
    private readonly List<object> _clicks = [];
    private readonly List<object> _events = [];
    private readonly List<object> _camera = [];
    private readonly List<object> _waits = [];
    private readonly List<object> _covers = [];
    private readonly List<object> _freezes = [];
    private readonly List<double[]> _pointer = [];
    private readonly CancellationTokenSource _stop = new();
    private Thread? _pointerThread;
    private DemoVideoTests.ScreenRecorder? _recorder;
    private string _step = "";
    private double _stepStart;
    private double _lastClick;

    public TutorialRecorder(DesktopApp app, string timingFile, string output)
    {
        _app = app; _output = output;
        Directory.CreateDirectory(output);
        using var json = JsonDocument.Parse(File.ReadAllText(timingFile));
        foreach (var step in json.RootElement.GetProperty("steps").EnumerateArray())
        {
            _timing.Add(step.GetProperty("id").GetString()!, new(step.GetProperty("seconds").GetDouble(),
                step.GetProperty("cues").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetDouble())));
        }
    }

    public static double Now => (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;

    public void Start()
    {
        _pointerThread = new Thread(() =>
        {
            while (!_stop.IsCancellationRequested)
            {
                GetCursorPos(out var p);
                lock (_pointer) { _pointer.Add([Math.Round(Now, 4), p.X, p.Y]); }
                Thread.Sleep(12);
            }
        }) { IsBackground = true };
        _pointerThread.Start();
        _recorder = DemoVideoTests.ScreenRecorder.Start(Path.Combine(_output, "raw.mkv"), wallClock: true, drawMouse: false);
        Thread.Sleep(500);
    }

    // One narrated step: the actions run while its sentence is spoken. The next step starts after both have ended. Time
    // that only waits for the application after the speech ended is marked, and the composer shortens it.
    public void Step(string id, Action act, double gap = 0.45)
    {
        var timing = _timing[id];
        _step = id;
        _stepStart = Now;
        _steps.Add(new { id, start = _stepStart });
        if (Rehearsal) { Rehearse(id, act); } else { act(); }
        var spoken = _stepStart + timing.Seconds;
        var done = Now;
        if (done > spoken + 1.2) { _waits.Add(new { from = spoken + 0.3, to = done - 0.4 }); }
        Until(spoken + gap);
    }

    // A rehearsal goes on after a failed step, so one run shows every problem. It keeps the picture and the control tree
    // of a failed step (which can hold names) beside the recording.
    public static bool Rehearsal => Environment.GetEnvironmentVariable("OPENSKITIME_TUTORIAL_REHEARSAL") == "1";

    private void Rehearse(string id, Action act)
    {
        var result = "ok";
        try { act(); }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or COMException or ArgumentException
            or IndexOutOfRangeException or KeyNotFoundException or FlaUI.Core.Exceptions.NoClickablePointException)
        {
            result = "FAILED " + ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex.StackTrace;
            _app.Screenshot(_output, "failed-" + id + ".png");
            File.WriteAllText(Path.Combine(_output, "failed-" + id + ".txt"), _app.Tree());
        }
        File.AppendAllText(Path.Combine(_output, "rehearsal.log"), $"{id}: {result}{Environment.NewLine}");
    }

    // Waiting for the application while the sentence goes on; only the part after the speech is shortened.
    public void Wait(Func<bool> condition, string description, int seconds = 90)
        => _app.WaitUntil(condition, description, TimeSpan.FromSeconds(seconds));

    private double CueTime(string cue) => _stepStart + _timing[_step].Cues[cue];

    public static void Until(double time)
    {
        var left = time - Now;
        if (left > 0) { Thread.Sleep((int)(left * 1000)); }
    }

    public void At(string cue, double offset = 0) => Until(CueTime(cue) + offset);

    // Moves to the element and clicks it when the cue word starts. The element is looked up before the wait.
    public void Click(AutomationElement element, string? cue = null, bool right = false)
    {
        Move(Center(element), cue);
        if (cue is not null) { At(cue); }
        Clicked(cue);
        Mouse.Click(right ? MouseButton.Right : MouseButton.Left);
        Thread.Sleep(120);
    }

    public void ClickAt(System.Drawing.Point point, string? cue = null, bool right = false)
    {
        Move(point, cue);
        if (cue is not null) { At(cue); }
        Clicked(cue);
        Mouse.Click(right ? MouseButton.Right : MouseButton.Left);
        Thread.Sleep(120);
    }

    // late: how long after the beginning of its word the click came.
    private void Clicked(string? cue)
    {
        _lastClick = Now;
        _clicks.Add(new { at = _lastClick, step = _step, cue, late = cue is null ? (double?)null : Math.Round(_lastClick - CueTime(cue), 3) });
    }

    // The screen areas to cover from now on (names of people), replacing the previous set.
    // Each area is one cell: the composer fills it with its background colour and draws a bar of fixed shape in it, so
    // nothing of the text is left, not even its length.
    public void Cover(IEnumerable<System.Drawing.Rectangle> areas)
        => _covers.Add(new { at = Now, areas = areas.Select(x => new[] { x.X, x.Y, x.Width, x.Height }).ToArray() });

    // An action that shows people's names or moves them on screen. The composer keeps the last picture before the action
    // until the new areas to cover have been measured, so no frame ever shows an uncovered name.
    public void Reveal(Action act, Func<bool>? ready, string description, Func<IEnumerable<System.Drawing.Rectangle>> measure, int settle = 450)
    {
        var from = Now;
        act();
        // act() may wait for its cue first; the click itself is what changes the picture.
        from = Math.Max(from, _lastClick);
        if (ready is not null) { Wait(ready, description); }
        Thread.Sleep(settle);
        var areas = measure().ToArray();
        Cover(areas);
        _freezes.Add(new { from = from - 0.04, to = Now + 0.12 });
        Thread.Sleep(150);
    }

    // Something that happens without the operator, such as an impulse of the timing device, at the cue word. It is an
    // anchor like a click, but no click is drawn.
    public void Fire(Action act, string cue)
    {
        At(cue);
        Event(cue);
        act();
    }

    private void Event(string cue) => _events.Add(new { at = Now, step = _step, cue, late = Math.Round(Now - CueTime(cue), 3) });

    // Presses at one point when the first word is spoken and releases at the other when the second one is.
    public void Drag(System.Drawing.Point from, System.Drawing.Point to, string grab, string drop, int hold)
    {
        Press(from, grab);
        Glide(to, Math.Clamp(CueTime(drop) - Now - hold / 1000.0, 0.8, 2.2));
        Settle();
        Until(Math.Max(CueTime(drop), Now + hold / 1000.0));
        Event(drop);
        Mouse.Up(MouseButton.Left);
        Thread.Sleep(250);
    }

    // A drag that begins at the cue word and ends as soon as it is done.
    public void DragTo(System.Drawing.Point from, System.Drawing.Point to, string grab)
    {
        Press(from, grab);
        Glide(to, 1.4);
        Settle();
        Thread.Sleep(700);
        Mouse.Up(MouseButton.Left);
        Thread.Sleep(250);
    }

    // The first small movements start the drag; from then on Windows reports every position to the target below.
    private void Press(System.Drawing.Point from, string cue)
    {
        Move(from, cue);
        At(cue);
        Clicked(cue);
        Mouse.Down(MouseButton.Left);
        Thread.Sleep(150);
        for (var i = 0; i < 4; i++) { mouse_event(0x0001, 3, 1, 0, 0); Thread.Sleep(40); }
    }

    private static void Settle()
    {
        for (var i = 0; i < 4; i++) { mouse_event(0x0001, i % 2 == 0 ? 1 : -1, 0, 0, 0); Thread.Sleep(60); }
    }

    public void DoubleClickAt(System.Drawing.Point point, string? cue = null)
    {
        Move(point, cue);
        if (cue is not null) { At(cue); }
        Clicked(cue);
        Mouse.DoubleClick(MouseButton.Left);
        Thread.Sleep(200);
    }

    public void Keys(string cue, params FlaUI.Core.WindowsAPI.VirtualKeyShort[] chord)
    {
        At(cue);
        Event(cue);
        Keyboard.TypeSimultaneously(chord);
        Thread.Sleep(200);
    }

    public void Hover(AutomationElement element, string? cue = null) => Move(Center(element), cue);

    // Arrives at the point when the cue word starts (or as soon as possible), with a smooth, operator-like movement.
    public void Move(System.Drawing.Point target, string? cue = null)
    {
        GetCursorPos(out var from);
        var distance = Math.Sqrt(Math.Pow(target.X - from.X, 2) + Math.Pow(target.Y - from.Y, 2));
        var seconds = Math.Clamp(0.35 + distance / 1500, 0.4, 1.1);
        if (cue is not null) { Until(CueTime(cue) - seconds - 0.1); }
        Glide(target, seconds);
    }

    private static void Glide(System.Drawing.Point target, double seconds)
    {
        GetCursorPos(out var from);
        var start = Now;
        while (Now - start < seconds)
        {
            var t = (Now - start) / seconds;
            var s = t * t * t * (t * (t * 6 - 15) + 10);
            SetCursorPos((int)Math.Round(from.X + (target.X - from.X) * s), (int)Math.Round(from.Y + (target.Y - from.Y) * s));
            Thread.Sleep(8);
        }
        SetCursorPos(target.X, target.Y);
        Thread.Sleep(60);
    }

    public void Type(string text)
    {
        foreach (var c in text) { Keyboard.Type(c); Thread.Sleep(90); }
    }

    // The camera frames the area (with a margin) until the next Camera or Overview call.
    public void Camera(System.Drawing.Rectangle area, int margin = 60)
        => _camera.Add(new { at = Now, area = new[] { area.X - margin, area.Y - margin, area.Width + 2 * margin, area.Height + 2 * margin } });

    public void Camera(params AutomationElement[] elements)
        => Camera(elements.Select(x => x.BoundingRectangle).Aggregate(System.Drawing.Rectangle.Union));

    public void Overview() => _camera.Add(new { at = Now, area = (int[]?)null });

    public static System.Drawing.Point Center(AutomationElement element)
    {
        var b = element.BoundingRectangle;
        return new(b.Left + b.Width / 2, b.Top + b.Height / 2);
    }

    private readonly Dictionary<string, string> _facts = [];
    public void Fact(string name, string value) => _facts[name] = value;

    public void Finish(IReadOnlyDictionary<string, string> facts)
    {
        foreach (var (name, value) in facts) { _facts[name] = value; }
        _recorder?.Stop();
        _stop.Cancel();
        _pointerThread?.Join(2000);
        File.WriteAllText(Path.Combine(_output, "recording.json"), JsonSerializer.Serialize(new
        { steps = _steps, clicks = _clicks, events = _events, camera = _camera, waits = _waits, covers = _covers, freezes = _freezes, facts = _facts }, s_json));
        File.WriteAllText(Path.Combine(_output, "pointer.json"), JsonSerializer.Serialize(_pointer));
    }

    public void Dispose()
    {
        _stop.Cancel();
        _recorder?.Dispose();
        _stop.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, uint data, nuint extra);
}
