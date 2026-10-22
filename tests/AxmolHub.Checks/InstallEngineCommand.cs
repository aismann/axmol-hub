using AxmolHub.Core;
using static CheckSupport;

internal static class InstallEngineCommand
{
    internal static async Task RunAsync(string root, string[] args)
    {
        using var http = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        // The manifest now lists multiple versions, so Single() is no longer allowed here; `--engine-version <v>` can
        // select one, and without it the newest LTS is taken — the same default semantics as the UI's Install button.
        var releases = new EngineReleases(root, Path.GetFullPath("manifests"));
        var requested = Array.IndexOf(args, "--engine-version");
        var release = requested >= 0 && requested + 1 < args.Length ? releases.Find(args[requested + 1]) ?? throw new ArgumentException("No such engine release in the manifest: " + args[requested + 1]) : releases.LatestLts();
        var package = release.Package;
        var installer = new PackageInstaller(new DownloadManager(http, Console.WriteLine), root, Console.WriteLine);
        var path = await installer.InstallAsync(package);
        var engineEntry = StateStore.ValidateEngine(path, package.Channel);
        var processRunner = new ProcessRunner(Console.WriteLine);
        var projectService = new ProjectService(processRunner, EngineCli(processRunner), new EnginePrebuiltState(root));
        var created = await projectService.CreateAsync("HelloAxmol", Path.Combine(root, "projects"), engineEntry);
        new StateStore(root).Save(new HubState { Engines = [engineEntry], Projects = [created], DefaultEnginePath = path });
        Console.WriteLine($"Axmol {release.Version} verified and project created. Run the engine's setup.ps1 to prepare its toolchain, then Build/Run.");
        return;
    }
}
