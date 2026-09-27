using System;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace OpenSkiTime.TimyUsbHost;

// No race rules, database access or credentials. ALGE byte events go unchanged over stdout.
// The optional vendor library is loaded at runtime; no vendor binaries are distributed in the repository.
internal static class Program
{
    private static volatile bool s_stopped;
    private static readonly object s_output = new object();
    private static string s_selected = "";

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length < 1) { throw new ArgumentException("SDK directory required."); }
            var folder = Path.GetFullPath(args[0]);
            Environment.SetEnvironmentVariable("PATH", folder + ";" + Environment.GetEnvironmentVariable("PATH"));
            s_selected = args.Length > 1 ? args[1] : "";
            var assembly = Assembly.LoadFrom(Path.Combine(folder, "AlgeTimyUsb.x64.dll"));
            var type = assembly.GetType("Alge.TimyUsb", throwOnError: true)!;
            var reader = new Thread(() => { Console.ReadLine(); s_stopped = true; }) { IsBackground = true };
            reader.Start();
            using (var control = new Control())
            {
                _ = control.Handle;
                var usb = Activator.CreateInstance(type, control)!;
                Subscribe(type, usb, "BytesReceived", (_, e) =>
                {
                    var id = DeviceId(e);
                    if (s_selected == id) { Emit("B", id, Convert.ToBase64String((byte[])Property(e, "Data"))); }
                });
                Subscribe(type, usb, "DeviceConnected", (_, e) =>
                {
                    var id = DeviceId(e);
                    if (s_selected.Length == 0) { s_selected = id; }
                    if (s_selected == id) { Emit("C", id, "Connected"); }
                    else { Emit("S", id, "Additional Timy detected; receiving only Timy " + s_selected); }
                });
                Subscribe(type, usb, "DeviceDisconnected", (_, e) =>
                { if (s_selected == DeviceId(e)) { Emit("D", s_selected, "Disconnected"); } });
                Emit("S", "", "Waiting for Timy USB - check cable and installed ALGE driver");
                type.GetMethod("Start", Type.EmptyTypes)!.Invoke(usb, null);
                try { while (!s_stopped) { Application.DoEvents(); Thread.Sleep(5); } }
                finally { type.GetMethod("Stop", Type.EmptyTypes)!.Invoke(usb, null); }
            }
            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var reason = ex is TargetInvocationException invocation && invocation.InnerException != null ? invocation.InnerException : ex;
            Emit("E", "", "Timy USB host failed (" + reason.GetType().Name + "). Check the ALGE library, native runtimes and USB driver.");
            return 1;
        }
    }

    private static object Property(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value, null)!;
    private static string DeviceId(object e) => Property(Property(e, "Device"), "Id").ToString()!;
    private static void Subscribe(Type type, object instance, string name, Action<object, object> callback)
    {
        var info = type.GetEvent(name)!;
        var handlerType = info.EventHandlerType!;
        var parameters = handlerType.GetMethod("Invoke")!.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
        var invoke = Expression.Invoke(Expression.Constant(callback), parameters.Select(p => Expression.Convert(p, typeof(object))));
        info.AddEventHandler(instance, Expression.Lambda(handlerType, invoke, parameters).Compile());
    }

    private static void Emit(string kind, string id, string text)
    {
        lock (s_output)
        {
            Console.Out.WriteLine(kind + "\t" + id + "\t" + text.Replace("\r", " ").Replace("\n", " "));
            Console.Out.Flush();
        }
    }
}
