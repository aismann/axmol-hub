using AxmolHub.Core;

internal static class ReleaseDebugKeyChecks
{
    internal static async Task RunAsync(string root, string[] args)
    {
        var stateEntry = new StateStore(root).Load();
        var toolsEntry = Path.Combine(root, "tools");
        var selectedEngine = stateEntry.Engines.Single();
        var runnerEntry = new ProcessRunner(Console.WriteLine);
        var environmentEntry = new PlatformBuildService(runnerEntry).CreateEnvironment(selectedEngine, BuildTargets.Get("android-arm64"));
        var clone = Path.Combine(root, "cache/android/debug-rejection-check.jks");
        File.Copy(Path.Combine(root, "cache/android/debug.keystore"), clone, overwrite: false);
        var passwords = new AndroidSigningPasswords("android", "android");
        var renamed = await runnerEntry.RunAsync(Path.Combine(toolsEntry, "jdk/bin/keytool.exe"), ["-changealias", "-keystore", clone, "-alias", "androiddebugkey", "-destalias", "pretend-upload", "-storepass:env", "HUB_ANDROID_STORE_PASSWORD", "-keypass:env", "HUB_ANDROID_KEY_PASSWORD"], root, passwords.Environment(environmentEntry));
        if (renamed.ExitCode != 0)
            throw new Exception("Debug certificate fixture could not be prepared.");
        try
        {
            await new AndroidSigningService(runnerEntry, toolsEntry).ValidateAsync(new() { ApplicationId = "com.axmolhub.fixture", KeyAlias = "pretend-upload", KeystorePath = clone }, passwords, environmentEntry);
            throw new Exception("Renamed debug certificate was accepted for Release.");
        }
        catch (InvalidDataException ex)when (ex.Message.Contains("debug certificate"))
        {
            Console.WriteLine("PASS: Renaming the debug alias cannot bypass release certificate validation.");
        }
        finally
        {
            File.Delete(clone);
        }

        return;
    }
}
