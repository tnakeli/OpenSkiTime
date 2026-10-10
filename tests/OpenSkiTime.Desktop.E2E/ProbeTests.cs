using System.Text;
using FlaUI.Core.AutomationElements;

namespace OpenSkiTime.Desktop.E2E;

public sealed class ProbeTests
{
    // Diagnostic: writes the UI Automation tree of the main window (OPENSKITIME_DESKTOP_E2E_PROBE=<file>).
    [DesktopE2EFact]
    public void DumpAutomationTree()
    {
        var output = Environment.GetEnvironmentVariable("OPENSKITIME_DESKTOP_E2E_PROBE");
        if (string.IsNullOrWhiteSpace(output)) { return; }
        var root = Path.Combine(Path.GetTempPath(), "openskitime-e2e-probe", Guid.NewGuid().ToString("N"));
        using var app = DesktopApp.Launch(root);
        var text = new StringBuilder();
        void Walk(AutomationElement element, int depth)
        {
            if (depth > 30) { return; }
            string Safe(Func<string> read) { try { return read(); } catch (Exception ex) when (ex is not OutOfMemoryException) { return "?"; } }
            text.Append(' ', depth * 2).Append(Safe(() => element.ControlType.ToString())).Append(" name='").Append(Safe(() => element.Name))
                .Append("' id='").Append(Safe(() => element.AutomationId)).Append("' class='").Append(Safe(() => element.ClassName)).Append('\'').AppendLine();
            foreach (var child in element.FindAllChildren()) { Walk(child, depth + 1); }
        }
        Walk(app.Window, 0);
        File.WriteAllText(output, text.ToString());
    }
}
