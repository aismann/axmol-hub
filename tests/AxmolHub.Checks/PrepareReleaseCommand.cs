using AxmolHub.Core;
using static CheckSupport;

internal static class PrepareReleaseCommand
{
    internal static async Task RunAsync(string root, string[] args)
    {
        var stateEntry = new StateStore(root);
        var hubEntry = stateEntry.Load();
        var selectedEngine = hubEntry.Engines.Single();
        var toolsEntry = Path.Combine(root, "tools");
        var messagesEntry = new List<string>();
        var runnerEntry = new ProcessRunner(message =>
        {
            messagesEntry.Add(message);
            Console.WriteLine(message);
        });
        var environmentEntry = new PlatformBuildService(runnerEntry).CreateEnvironment(selectedEngine, BuildTargets.Get("android-arm64"));
        foreach (var name in new[]
        {
            "HOME",
            "TEMP"
        }

        )
            Directory.CreateDirectory(environmentEntry[name]);
        var serviceEntry = new ProjectService(runnerEntry, EngineCli(runnerEntry), new EnginePrebuiltState(root));
        var entry = hubEntry.Projects.FirstOrDefault(p => p.Name == "HelloAndroidRelease") ?? await serviceEntry.CreateAsync("HelloAndroidRelease", Path.Combine(root, "projects"), selectedEngine);
        var settings = new AndroidReleaseSettings
        {
            ApplicationId = "com.axmolhub.releasecheck",
            VersionCode = 7,
            VersionName = "0.1.3",
            KeyAlias = "releasecheck",
            KeystorePath = Path.Combine(root, "cache/android/release-check.jks")
        };
        var passwords = new AndroidSigningPasswords("Store-" + Guid.NewGuid().ToString("N"), "Key-" + Guid.NewGuid().ToString("N"));
        var signerEntry = new AndroidSigningService(runnerEntry, toolsEntry);
        await signerEntry.CreateAsync(settings, passwords, environmentEntry);
        var certificate = await signerEntry.ValidateAsync(settings, passwords, environmentEntry);
        async Task MustReject(Func<Task> action, string name)
        {
            try
            {
                await action();
            }
            catch (Exception ex)when (ex is InvalidOperationException or InvalidDataException or IOException)
            {
                Console.WriteLine("PASS: " + name);
                return;
            }

            throw new Exception("FAILED: " + name);
        }

        await MustReject(() => signerEntry.CreateAsync(settings, passwords, environmentEntry), "Existing keystore is never overwritten");
        await MustReject(() => signerEntry.ValidateAsync(settings, new("Incorrect-store-password", passwords.KeyPassword), environmentEntry), "Wrong keystore password is rejected");
        await MustReject(() => signerEntry.ValidateAsync(settings, new(passwords.StorePassword, "Incorrect-key-password"), environmentEntry), "Wrong private key password is rejected");
        var missingAlias = new AndroidReleaseSettings
        {
            ApplicationId = settings.ApplicationId,
            KeyAlias = "missing",
            KeystorePath = settings.KeystorePath
        };
        await MustReject(() => signerEntry.ValidateAsync(missingAlias, passwords, environmentEntry), "Missing alias is rejected");
        settings.Save(entry);
        BuildTargets.Select(entry, "android-arm64", "Release");
        stateEntry.SaveProject(entry);
        var probe = await runnerEntry.RunAsync(WindowsShell.PowerShell, ["-NoProfile", "-Command", "[Console]::WriteLine($env:HUB_ANDROID_STORE_PASSWORD); [Console]::Error.WriteLine($env:HUB_ANDROID_KEY_PASSWORD)"], root, passwords.Environment(environmentEntry), sensitiveValues: passwords.SensitiveValues);
        if (probe.Output.Contains(passwords.StorePassword) || probe.Error.Contains(passwords.KeyPassword) || messagesEntry.Any(line => line.Contains(passwords.StorePassword) || line.Contains(passwords.KeyPassword)) || File.ReadAllText(AndroidReleaseSettings.PathFor(entry)).Contains(passwords.StorePassword))
            throw new Exception("Signing secret escaped redaction.");
        // Only this maintainer acceptance path writes a throwaway test credential; the product never stores a password,
        // and the file lives in the ignored artifacts store.
        StateStore.WriteJson(Path.Combine(root, "cache/android/release-check-passwords.json"), new { passwords.StorePassword, passwords.KeyPassword });
        Console.WriteLine("PASS: New release key, private-key unlock, certificate SHA256, non-secret config persistence and stdout/stderr redaction. Certificate: " + certificate);
        return;
    }
}
