using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace OpenSkiTime.Desktop.E2E;

// Launches the real, built OpenSkiTime desktop executable in the interactive Windows session and drives it through
// UI Automation. Local data (FIS cache, preferences, recent files) is redirected to a private directory.
public sealed class DesktopApp : IDisposable
{
    public const string EnableVariable = "OPENSKITIME_DESKTOP_E2E";
    private readonly FlaUI.Core.Application _application;
    public UIA3Automation Automation { get; } = new();
    public string LocalData { get; }
    public string Root { get; }
    public Window Window { get; private set; }
    public ConditionFactory By => Automation.ConditionFactory;

    private DesktopApp(FlaUI.Core.Application application, Window window, string root, string localData)
    {
        _application = application; Window = window; Root = root; LocalData = localData;
    }

    public static string Executable()
    {
        var configured = Environment.GetEnvironmentVariable("OPENSKITIME_DESKTOP_EXE");
        if (!string.IsNullOrWhiteSpace(configured)) { return Path.GetFullPath(configured); }
        var repository = RepositoryRoot();
        foreach (var configuration in new[] { "Release", "Debug" })
        {
            var path = Path.Combine(repository, "src", "OpenSkiTime.Desktop", "bin", configuration, "net10.0", "OpenSkiTime.Desktop.exe");
            if (File.Exists(path)) { return path; }
        }
        throw new FileNotFoundException("Build src/OpenSkiTime.Desktop first (dotnet build -c Release) or set OPENSKITIME_DESKTOP_EXE.");
    }

    public static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OpenSkiTime.slnx"))) { directory = directory.Parent; }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    public static DesktopApp Launch(string root, Action<string>? prepareLocalData = null, int width = 1920, int height = 1080)
    {
        PrepareInteractiveDesktop();
        var localData = Path.Combine(root, "local-data");
        Directory.CreateDirectory(localData);
        prepareLocalData?.Invoke(localData);
        var start = new ProcessStartInfo(Executable()) { UseShellExecute = false, WorkingDirectory = root };
        start.Environment["OPENSKITIME_LOCAL_DATA"] = localData;
        var application = FlaUI.Core.Application.Launch(start);
        var automation = new UIA3Automation();
        var window = Retry(() => application.GetMainWindow(automation, TimeSpan.FromSeconds(2)), TimeSpan.FromSeconds(60))
            ?? throw new TimeoutException("OpenSkiTime main window did not appear.");
        automation.Dispose();
        var app = new DesktopApp(application, window, root, localData);
        app.Window = app.Retry(() => application.GetMainWindow(app.Automation, TimeSpan.FromSeconds(2)));
        app.Place(width, height);
        return app;
    }

    // Maximized on the primary screen (the work area above the taskbar), as an operator runs the race office.
    public void Maximize()
    {
        var handle = Window.Properties.NativeWindowHandle.Value;
        ShowWindow(handle, 3); // SW_MAXIMIZE
        SetForegroundWindow(handle);
        Thread.Sleep(600);
    }

    // Normal (not maximized) window at the requested outer size in the top-left corner of the primary screen.
    public void Place(int width, int height)
    {
        var handle = Window.Properties.NativeWindowHandle.Value;
        ShowWindow(handle, 9); // SW_RESTORE
        SetWindowPos(handle, IntPtr.Zero, 0, 0, width, height, 0x0040); // SWP_SHOWWINDOW
        SetForegroundWindow(handle);
        Thread.Sleep(400);
    }

    public T Retry<T>(Func<T?> find, TimeSpan? timeout = null) where T : class
        => Retry(find, timeout ?? TimeSpan.FromSeconds(15)) ?? throw new TimeoutException("UI element not found in time.");

    private static T? Retry<T>(Func<T?> find, TimeSpan timeout) where T : class
    {
        var watch = Stopwatch.StartNew();
        Exception? last = null;
        while (watch.Elapsed < timeout)
        {
            try { if (find() is { } found) { return found; } }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or TimeoutException) { last = ex; }
            Thread.Sleep(150);
        }
        return last is null ? null : throw new TimeoutException("UI element not found in time.", last);
    }

    public void WaitUntil(Func<bool> condition, string description, TimeSpan? timeout = null)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < (timeout ?? TimeSpan.FromSeconds(20)))
        {
            try { if (condition()) { return; } }
            catch (COMException) { }
            Thread.Sleep(100);
        }
        throw new TimeoutException("Timed out waiting for: " + description);
    }

    public AutomationElement ById(string automationId, AutomationElement? scope = null)
        => Retry(() => (scope ?? Window).FindFirstDescendant(By.ByAutomationId(automationId)));

    public AutomationElement ByName(string name, ControlType? type = null, AutomationElement? scope = null)
        => Retry(() => (scope ?? Window).FindFirstDescendant(type is { } t ? By.ByName(name).And(By.ByControlType(t)) : By.ByName(name)));

    public AutomationElement? TryByName(string name, ControlType? type = null, AutomationElement? scope = null)
        => (scope ?? Window).FindFirstDescendant(type is { } t ? By.ByName(name).And(By.ByControlType(t)) : By.ByName(name));

    public Button Button(string name, AutomationElement? scope = null) => ByName(name, ControlType.Button, scope).AsButton();

    public void Click(string buttonName, AutomationElement? scope = null)
    {
        var button = Retry(() => (scope ?? Window).FindAllDescendants(By.ByName(buttonName).And(By.ByControlType(ControlType.Button)))
            .FirstOrDefault(x => x.IsEnabled && !x.IsOffscreen));
        Invoke(button);
    }

    // True when this session receives input (the interactive desktop is shown). While a screen saver or lock screen
    // owns the input desktop, Windows rejects synthetic mouse/keyboard input; the driver then uses UI Automation
    // patterns (Invoke, Value, Toggle, SelectionItem), which act on the same controls without physical input.
    public static bool PhysicalInput { get; private set; }

    // Keeps the display awake for the duration of the run (a per-process power request, like presentation software)
    // and ends a running non-secure screen saver, which any key press would end too. A secure (password) screen saver
    // or a locked session is never touched: the run then continues through UI Automation patterns only.
    private static void PrepareInteractiveDesktop()
    {
        _ = SetThreadExecutionState(0x80000000 | 0x00000002 | 0x00000001); // ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED
        var secure = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "ScreenSaverIsSecure", "0") as string;
        if (InputDesktopName() == "Screen-saver" && secure != "1")
        {
            foreach (var saver in Process.GetProcesses().Where(x => x.ProcessName.EndsWith(".scr", StringComparison.OrdinalIgnoreCase)))
            {
                using (saver) { try { saver.Kill(); saver.WaitForExit(3000); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } }
            }
            for (var i = 0; i < 20 && InputDesktopName() != "Default"; i++) { Thread.Sleep(100); }
        }
        PhysicalInput = InputDesktopName() == "Default" && Environment.GetEnvironmentVariable("OPENSKITIME_DESKTOP_E2E_PATTERNS") != "1";
    }

    // Moves the real mouse to the control and clicks it, so recordings show the operator's pointer.
    public static void Invoke(AutomationElement element)
    {
        if (!PhysicalInput)
        {
            if (element.Patterns.Invoke.IsSupported) { element.Patterns.Invoke.Pattern.Invoke(); }
            else if (element.Patterns.Toggle.IsSupported) { element.Patterns.Toggle.Pattern.Toggle(); }
            else if (element.Patterns.SelectionItem.IsSupported) { element.Patterns.SelectionItem.Pattern.Select(); }
            else if (element.Patterns.ExpandCollapse.IsSupported) { element.Patterns.ExpandCollapse.Pattern.Expand(); }
            else { element.Focus(); }
            Thread.Sleep(150);
            return;
        }
        element.Focus();
        var point = element.GetClickablePoint();
        Mouse.MoveTo(point);
        Mouse.Click(point);
        Thread.Sleep(120);
    }

    public void SetText(AutomationElement box, string text)
    {
        if (!PhysicalInput && box.Patterns.Value.IsSupported)
        {
            box.Focus();
            box.Patterns.Value.Pattern.SetValue(text);
            Thread.Sleep(60);
            return;
        }
        Invoke(box);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type(text);
        Thread.Sleep(60);
    }

    private static string InputDesktopName()
    {
        var desktop = OpenInputDesktop(0, false, 0x0001);
        if (desktop == IntPtr.Zero) { return string.Empty; }
        try
        {
            var name = new char[256];
            return GetUserObjectInformation(desktop, 2, name, name.Length * 2, out var needed)
                ? new string(name, 0, Math.Max(0, needed / 2 - 1)) : string.Empty;
        }
        finally { CloseDesktop(desktop); }
    }

    public void Press(VirtualKeyShort key) { Keyboard.Press(key); Keyboard.Release(key); Thread.Sleep(80); }

    // The window as rendered by the application itself (PrintWindow with full content), so screenshots are real-window
    // renderings even when the physical screen is unavailable; with an interactive screen the visible pixels are taken.
    public string Screenshot(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        if (PhysicalInput)
        {
            using var image = Capture.Element(Window);
            image.ToFile(path);
            return path;
        }
        using var bitmap = RenderWindow();
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        return path;
    }

    public System.Drawing.Bitmap RenderWindow()
    {
        var handle = Window.Properties.NativeWindowHandle.Value;
        GetWindowRect(handle, out var rect);
        var bitmap = new System.Drawing.Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        var hdc = graphics.GetHdc();
        try { PrintWindow(handle, hdc, 2); } // PW_RENDERFULLCONTENT
        finally { graphics.ReleaseHdc(hdc); }
        return bitmap;
    }

    // The input that follows a visible label (Avalonia exposes form labels as Text siblings of their inputs).
    public AutomationElement Field(string label, AutomationElement? scope = null, ControlType? type = null)
    {
        return Retry(() =>
        {
            var all = (scope ?? Window).FindAllDescendants();
            var index = Array.FindIndex(all, x => x.ControlType == ControlType.Text && x.Name == label && !x.IsOffscreen);
            if (index < 0) { return null; }
            return all.Skip(index + 1).FirstOrDefault(x => type is { } t ? x.ControlType == t
                : x.ControlType is ControlType.Edit or ControlType.ComboBox or ControlType.Spinner);
        });
    }

    public string Tree(AutomationElement? scope = null)
    {
        var text = new System.Text.StringBuilder();
        void Walk(AutomationElement element, int depth)
        {
            if (depth > 40) { return; }
            string Safe(Func<string> read) { try { return read(); } catch (Exception ex) when (ex is not OutOfMemoryException) { return "?"; } }
            text.Append(' ', depth * 2).Append(Safe(() => element.ControlType.ToString())).Append(" name='").Append(Safe(() => element.Name))
                .Append("' id='").Append(Safe(() => element.AutomationId)).Append("' class='").Append(Safe(() => element.ClassName)).Append('\'').AppendLine();
            foreach (var child in element.FindAllChildren()) { Walk(child, depth + 1); }
        }
        Walk(scope ?? Window, 0);
        return text.ToString();
    }

    public void Dispose()
    {
        try
        {
            if (!_application.HasExited)
            {
                _application.Close();
                using var process = Process.GetProcessById(_application.ProcessId);
                if (!process.WaitForExit(5000)) { process.Kill(entireProcessTree: true); }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or COMException) { }
        _application.Dispose();
        Automation.Dispose();
        _ = SetThreadExecutionState(0x80000000); // release the display request
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, [Out] char[] info, int length, out int needed);
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
}
