using AxmolHub.Core;
using static CheckSupport;

internal static class GameCommands
{
    internal static async Task RunAsync(string root, string[] args)
    {
        var log = new HubLog(Path.Combine(root, "logs"));
        void Write(string line)
        {
            Console.WriteLine(line);
            log.Write(line);
        }

        var runnerEntry = new ProcessRunner(Write);
        var storeEntry = new StateStore(root);
        var hubState = storeEntry.Load();
        var projectEntry = hubState.Projects.Single(p => p.Name == "HelloAxmol");
        var engineEntry = hubState.Engines.Single(e => e.Version == projectEntry.Version && e.Channel == projectEntry.Channel);
        var serviceEntry = new ProjectService(runnerEntry, EngineCli(runnerEntry), new EnginePrebuiltState(root));
        if (args.Contains("--build-game"))
        {
            projectEntry.BuildStatus = "Building";
            storeEntry.Save(hubState);
            try
            {
                await serviceEntry.BuildAsync(projectEntry, engineEntry);
                Write($"Built executable: {serviceEntry.FindExecutable(projectEntry)}");
                projectEntry.BuildStatus = "Succeeded";
            }
            catch
            {
                projectEntry.BuildStatus = "Failed";
                throw;
            }
            finally
            {
                storeEntry.Save(hubState);
            }
        }
        else
        {
            var resultEntry = await serviceEntry.RunAsync(projectEntry, engineEntry);
            Write($"Actual game exited: {resultEntry.ExitCode}");
            if (resultEntry.ExitCode != 0)
                throw new Exception("Game exited with error.");
        }

        return;
    }
}
