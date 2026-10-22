using AxmolHub.Core;

internal static class AndroidVerificationChecks
{
    internal static async Task RunAsync(string root, string[] args)
    {
        var hub = new StateStore(root).Load();
        var entry = hub.Projects.Single(p => p.Name == "HelloAndroid");
        var selectedEngine = hub.Engines.Single(e => e.Version == entry.Version && e.Channel == entry.Channel);
        var packageRunner = new ProcessRunner(Console.WriteLine);
        var environmentEntry = new PlatformBuildService(packageRunner).CreateEnvironment(selectedEngine, BuildTargets.Get(entry.Platform));
        var file = Path.Combine(AndroidPackageService.StageDirectory(entry), "gradle/verification-metadata.xml");
        var original = File.ReadAllText(file);
        try
        {
            var document = System.Xml.Linq.XDocument.Parse(original);
            var artifact = document.Descendants().Single(node => node.Name.LocalName == "component" && (string? )node.Attribute("group") == "com.android.tools.build" && (string? )node.Attribute("name") == "gradle").Elements().Single(node => (string? )node.Attribute("name") == "gradle-8.11.1.jar");
            foreach (var hash in artifact.Elements().Where(node => node.Name.LocalName == "sha256"))
                hash.SetAttributeValue("value", new string ('0', 64));
            document.Save(file);
            var command = new AndroidPackageService(packageRunner, Path.Combine(root, "tools")).GradleCommand(entry, environmentEntry, "--offline", "--dependency-verification=strict", "help");
            var rejected = await packageRunner.RunAsync(command.Executable, command.Arguments, command.WorkingDirectory, environmentEntry, timeout: TimeSpan.FromMinutes(2));
            if (rejected.ExitCode == 0 || !(rejected.Output + rejected.Error).Contains("Dependency verification failed"))
                throw new InvalidOperationException("Gradle did not reject the altered dependency checksum.");
            Console.WriteLine("PASS: Real Gradle rejects an altered AGP SHA-256 before executing build tasks.");
        }
        finally
        {
            File.WriteAllText(file, original);
        }

        return;
    }
}
