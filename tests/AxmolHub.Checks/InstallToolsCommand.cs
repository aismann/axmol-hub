using AxmolHub.Core;
using static CheckSupport;

internal static class InstallToolsCommand
{
    internal static async Task RunAsync(string root, string[] args)
    {
        var hub = new StateStore(root).Load();
        var engineEntry = hub.Engines.FirstOrDefault(candidate => candidate.Path == hub.DefaultEnginePath) ?? hub.Engines.FirstOrDefault() ?? throw new InvalidOperationException("Import an Axmol engine first.");
        var commandLine = EngineCli(new ProcessRunner(Console.WriteLine));
        var platform = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : "win32";
        var outcome = await new EngineSetupService(commandLine).RunAsync(engineEntry, new SetupOptions(platform));
        Console.WriteLine($"{engineEntry}: {outcome.Describe()}");
        if (!outcome.Succeeded)
            throw new InvalidOperationException(outcome.Describe());
        return;
    }
}
