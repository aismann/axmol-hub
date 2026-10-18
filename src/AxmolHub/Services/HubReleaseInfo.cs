namespace AxmolHub;

internal static class HubReleaseInfo
{
#if HUB_PRERELEASE_BUILD
    public static bool IsPrereleaseBuild => true;
#else
    public static bool IsPrereleaseBuild => false;
#endif

    /// <summary>
    /// The version as the UI writes it, read from the assembly — the same shape the title bar and the settings
    /// page spell as "v" + version. One product version lives in Directory.Build.props; nothing here restates it.
    /// </summary>
    internal static string DisplayVersion
        => typeof(HubReleaseInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
