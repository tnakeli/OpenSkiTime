using Xunit;

namespace OpenSkiTime.Desktop.E2E;

// Real-window tests need an interactive Windows desktop and take over mouse and keyboard; run them only on request.
[AttributeUsage(AttributeTargets.Method)]
public sealed class DesktopE2EFactAttribute : FactAttribute
{
    public DesktopE2EFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable(DesktopApp.EnableVariable) != "1")
        { Skip = $"Set {DesktopApp.EnableVariable}=1 on an interactive Windows desktop to run real-window tests."; }
    }
}
