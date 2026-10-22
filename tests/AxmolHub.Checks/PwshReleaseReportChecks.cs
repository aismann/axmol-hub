using AxmolHub.Core;
using static CheckSupport;

internal static class PwshReleaseReportChecks
{
    internal static async Task RunAsync(string root, string[] args)
    {
        using var reportClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        var reportRunner = new ProcessRunner(Console.WriteLine);
        var reportInstaller = new HostPowerShellInstaller(reportClient, reportRunner, new DownloadManager(reportClient, Console.WriteLine), root, root);
        var reported = HostPowerShell.Probe();
        Console.WriteLine($"host       : {BuildTargets.Host}/{BuildTargets.HostArch}");
        Console.WriteLine($"probe      : {reported.State} / {reported.Executable ?? "-"} / {reported.Version ?? "version unconfirmed"}");
        Console.WriteLine($"plan       : {reportInstaller.Plan()}");
        Console.WriteLine($"winget     : {(HostPowerShellInstaller.WingetOnPath() ? CommandShells.Resolve("winget") : "not on PATH")}");
        Console.WriteLine($"arguments  : {string.Join(' ', HostPowerShellInstaller.WingetArguments)}");
        Console.WriteLine($"bootstrap  : {HostPowerShellInstaller.BootstrapCommand}");
        if (!OperatingSystem.IsWindows())
        {
            var terminal = HostPowerShellInstaller.TerminalLaunch(reportInstaller.ScriptPath);
            Console.WriteLine($"terminal   : {(terminal is null ? "none found: the UI hands the command above back to you" : string.Join(' ', [terminal.Value.Executable, ..terminal.Value.Arguments]))}");
            Console.WriteLine("needs sudo   : pwshi.sh calls sudo, which Hub's TTY-less child process cannot answer (see HostPowerShellInstaller)");
            Console.WriteLine($"{count} checks passed.");
            return;
        }

        var resolved = await HostPowerShellInstaller.ResolveWindowsPackageAsync(reportClient, BuildTargets.HostArch, CancellationToken.None);
        Console.WriteLine($"latest     : {resolved.Version}");
        Console.WriteLine($"msi        : {resolved.MsiUrl ?? "(none: a package without a digest is never downloaded)"}");
        Console.WriteLine($"sha256     : {resolved.Sha256 ?? "not resolved"}");
        foreach (var problem in resolved.Problems)
            Console.WriteLine($"problem    : {problem}");
        Console.WriteLine($"elevated   : msiexec /i <msi> /quiet /norestart /log <logs>/pwsh-msi-<utc>.log(one UAC prompt; /log is required because an elevated process has no stdout)");
        Console.WriteLine($"{count} checks passed. This was read-only: nothing was installed.");
        return;
    }
}
