using System.Security.Cryptography;
using System.Text;
using AxmolHub.Core;
using static CheckSupport;

internal static class DownloadChecks
{
    internal static async Task RunAsync(string root, string[] args)
    {
        var downloadBytes = Encoding.UTF8.GetBytes("verified download fixture with enough content to exercise the streaming path");
        var downloadHandler = new FixtureHandler(downloadBytes);
        using var downloadClient = new HttpClient(downloadHandler);
        var downloadManager = new DownloadManager(downloadClient, _ =>
        {
        });
        var downloadCache = Path.Combine(root, "cache-" + Guid.NewGuid().ToString("N"));
        var downloadSha = Convert.ToHexString(SHA256.HashData(downloadBytes));
        var downloadFile = await downloadManager.DownloadAsync(new Uri("https://fixture.test/file.zip"), downloadSha, downloadCache);
        Check(File.ReadAllBytes(downloadFile).SequenceEqual(downloadBytes), "SHA-256 download and atomic final file");
        await downloadManager.DownloadAsync(new Uri("https://fixture.test/file.zip"), downloadSha, downloadCache);
        Check(downloadHandler.Requests == 1, "Verified cache reused");
        await Reject<InvalidDataException>(() => downloadManager.DownloadAsync(new Uri("https://fixture.test/file.zip"), new string ('0', 64), downloadCache), "Bad hash rejected");
        Check(!Directory.EnumerateFiles(downloadCache, "*.partial").Any() && !File.Exists(Path.Combine(downloadCache, new string ('0', 64) + ".zip")), "Failed downloads leave no installed/cache artifact");
        await Reject<ArgumentException>(() => downloadManager.DownloadAsync(new Uri("http://fixture.test/file.zip"), downloadSha, downloadCache), "HTTP package rejected");
        // Resume: the first response is truncated mid-transfer, the retry uses Range to continue from the breakpoint,
        // and the final sha256 is correct.
        var resumeBytes = Encoding.UTF8.GetBytes("resumable download fixture with enough bytes to split across two requests");
        var resumeSha = Convert.ToHexString(SHA256.HashData(resumeBytes));
        var resumeCache = Path.Combine(root, "cache-resume-" + Guid.NewGuid().ToString("N"));
        var resumeHandler = new FixtureHandler(resumeBytes)
        {
            TruncateFirstResponseTo = resumeBytes.Length / 2
        };
        using var resumeClient = new HttpClient(resumeHandler);
        var resumeDownloads = new DownloadManager(resumeClient, _ =>
        {
        });
        var resumeFile = await resumeDownloads.DownloadAsync(new Uri("https://fixture.test/resume.zip"), resumeSha, resumeCache);
        Check(File.ReadAllBytes(resumeFile).SequenceEqual(resumeBytes), "Interrupted download resumes from the breakpoint and verifies the whole-file hash");
        Check(resumeHandler.Requests >= 2 && resumeHandler.SawRange, "Resume issues a Range request after interruption");
        // The server does not support Range (returns 200): rewrite from 0 and land the file correctly.
        var noRangeBytes = Encoding.UTF8.GetBytes("server ignores range and returns the full body");
        var noRangeSha = Convert.ToHexString(SHA256.HashData(noRangeBytes));
        var noRangeCache = Path.Combine(root, "cache-norange-" + Guid.NewGuid().ToString("N"));
        var noRangeHandler = new FixtureHandler(noRangeBytes)
        {
            IgnoreRange = true
        };
        using var noRangeClient = new HttpClient(noRangeHandler);
        var noRangeDownloads = new DownloadManager(noRangeClient, _ =>
        {
        });
        var noRangeFile = await noRangeDownloads.DownloadAsync(new Uri("https://fixture.test/norange.zip"), noRangeSha, noRangeCache);
        Check(File.ReadAllBytes(noRangeFile).SequenceEqual(noRangeBytes), "Server without Range support falls back to a clean full download");
        // A bad partial (sha256 mismatch after resuming) → delete and re-download → correct.
        var corruptBytes = Encoding.UTF8.GetBytes("corrupted resume content that will not match its digest");
        var corruptSha = Convert.ToHexString(SHA256.HashData(corruptBytes));
        var corruptCache = Path.Combine(root, "cache-corrupt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(corruptCache);
        var corruptTarget = Path.Combine(corruptCache, corruptSha.ToLowerInvariant() + ".zip");
        File.WriteAllBytes(corruptTarget + ".partial", Encoding.UTF8.GetBytes("leftover garbage that is not the right prefix"));
        var corruptHandler = new FixtureHandler(corruptBytes);
        using var corruptClient = new HttpClient(corruptHandler);
        var corruptDownloads = new DownloadManager(corruptClient, _ =>
        {
        });
        var corruptFile = await corruptDownloads.DownloadAsync(new Uri("https://fixture.test/corrupt.zip"), corruptSha, corruptCache);
        Check(File.ReadAllBytes(corruptFile).SequenceEqual(corruptBytes), "Corrupted resume content is discarded and re-downloaded cleanly");
        Console.WriteLine($"{count} checks passed.");
        return;
    }
}
