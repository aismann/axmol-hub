using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("AxmolHub")]
// The verification entry points live in the app and in the checks project; both report through Core's internal
// CheckReport, so "the exit code is the failure count" has one definition instead of one per mode.
[assembly: InternalsVisibleTo("AxmolHub.Checks")]
