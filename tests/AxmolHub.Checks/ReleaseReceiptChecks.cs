using AxmolHub.Core;

internal static class ReleaseReceiptChecks
{
    internal static async Task RunAsync(string root, string[] args)
    {
        var entry = new StateStore(root).Load().Projects.Single(p => p.Name == "HelloAndroidRelease");
        var config = AndroidReleaseSettings.PathFor(entry);
        var original = File.ReadAllText(config);
        var apk = AndroidPackageService.VerifiedApk(entry);
        try
        {
            var settings = AndroidReleaseSettings.Require(entry);
            settings.VersionCode++;
            settings.Save(entry);
            try
            {
                AndroidPackageService.VerifiedApk(entry);
                throw new Exception("Changed release version was deployable without rebuilding.");
            }
            catch (InvalidDataException)
            {
                Console.WriteLine("PASS: Real signed Release APK refuses deployment after release settings change.");
            }
        }
        finally
        {
            File.WriteAllText(config, original);
        }

        if (AndroidPackageService.VerifiedApk(entry) != apk)
            throw new Exception("Restoring release settings did not restore receipt validation.");
        var passwordFile = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "cache/android/release-check-passwords.json")));
        var secrets = new[]
        {
            passwordFile.RootElement.GetProperty("StorePassword").GetString()!,
            passwordFile.RootElement.GetProperty("KeyPassword").GetString()!
        };
        var stage = AndroidPackageService.StageDirectory(entry);
        var files = new[]
        {
            config,
            Path.Combine(stage, "app/build.gradle"),
            Path.Combine(stage, "gradle.properties"),
            Path.Combine(stage, ".hub-apk.json")
        };
        if (files.Any(file => secrets.Any(secret => File.ReadAllText(file).Contains(secret))))
            throw new Exception("Release password persisted in project or packaging files.");
        Console.WriteLine("PASS: Real release receipt matches selected settings and password values are absent from project, Gradle files and receipts.");
        return;
    }
}
