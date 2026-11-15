using Velopack;

namespace WildflowerDesktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        VelopackApp.Build().Run();
        ApplicationConfiguration.Initialize();
        using var singleInstance = new Mutex(true, "Local\\WildflowerDesktop", out var first);
        if (!first) return;
        Application.Run(new ManagerForm(args.Contains("--diagnostics"), args.Contains("--empty-host")));
    }
}
