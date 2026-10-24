namespace AxmolHub.Core;

public enum DiffHunkAction { Stage, Unstage, Revert, Accept, Reject }

public sealed record DiffHunkActionResult(bool Applied, string Detail, bool RefreshRequired = false);
