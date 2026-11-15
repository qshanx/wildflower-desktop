using Velopack;

namespace WildflowerDesktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        ApplicationConfiguration.Initialize();
        using var openRequest = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\WildflowerDesktop.OpenLibrary");
        using var singleInstance = new Mutex(true, "Local\\WildflowerDesktop", out var first);
        if (!first) { openRequest.Set(); return; }
        using var manager = new ManagerForm(args.Contains("--diagnostics"), args.Contains("--empty-host"));
        _ = manager.Handle;
        var listener = ThreadPool.RegisterWaitForSingleObject(openRequest, (_, _) =>
        {
            try { manager.BeginInvoke(manager.OpenLibrary); }
            catch (InvalidOperationException) { /* The window is shutting down. */ }
        }, null, Timeout.Infinite, false);
        try { Application.Run(manager); }
        finally { listener.Unregister(null); }
    }
}
