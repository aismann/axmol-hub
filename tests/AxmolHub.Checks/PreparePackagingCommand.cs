using AxmolHub.Core;

internal static class PreparePackagingCommand
{
    internal static async Task RunAsync(string root, string[] args)
    {
        var manifest = PackageManifest.Read(Path.GetFullPath("installer/packaging-manifest.json")).Packages.Single();
        using var clientEntry = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        var runnerEntry = new ProcessRunner(Console.WriteLine);
        // DownloadManager only returns a path when the digest matches, so what is installed below is verified content.
        var archive = await new DownloadManager(clientEntry, Console.WriteLine).DownloadAsync(new Uri(manifest.Url), manifest.Sha256, Path.Combine(root, "cache"));
        // A NuGet folder source requires the <id>.<version>.nupkg naming, while the cache names files by digest.
        var feed = Path.Combine(root, "feed");
        Directory.CreateDirectory(feed);
        var package = Path.Combine(feed, manifest.Id + "." + manifest.Version + ".nupkg");
        File.Copy(archive, package, overwrite: true);
        var destination = Path.Combine(root, "vpk");
        // dotnet tool install errors out when the tool already exists, so clear it first to keep the prepare step
        // repeatable.
        if (Directory.Exists(destination))
            Directory.Delete(destination, true);
        var install = await runnerEntry.RunAsync("dotnet", ["tool", "install", manifest.Id, "--tool-path", destination, "--version", manifest.Version, "--add-source", feed], root);
        var tool = Path.Combine(destination, OperatingSystem.IsWindows() ? "vpk.exe" : "vpk");
        if (install.ExitCode != 0 || !File.Exists(tool))
            throw new IOException("Private packaging tool preparation failed.");
        StateStore.WriteJson(Path.Combine(destination, ".hub-install.json"), new { manifest.Id, manifest.Version, manifest.Url, manifest.Sha256 });
        Console.WriteLine("Packaging tool ready: " + tool);
        return;
    }
}
