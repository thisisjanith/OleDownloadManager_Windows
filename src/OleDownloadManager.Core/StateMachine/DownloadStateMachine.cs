using OleDownloadManager.Core.Enums;

namespace OleDownloadManager.Core.StateMachine;

public sealed class InvalidStateTransitionException(DownloadStatus from, DownloadStatus to)
    : InvalidOperationException($"A download cannot move from {from} to {to}.")
{
    public DownloadStatus From { get; } = from;
    public DownloadStatus To { get; } = to;
}

/// <summary>
/// Validates download state transitions. Completed and Canceled downloads can only be
/// restarted through an explicit user action.
/// </summary>
public static class DownloadStateMachine
{
    private static readonly Dictionary<DownloadStatus, DownloadStatus[]> Allowed = new()
    {
        [DownloadStatus.Queued] =
        [
            DownloadStatus.Connecting, DownloadStatus.Paused, DownloadStatus.Canceled,
            DownloadStatus.WaitingForDestination, DownloadStatus.Failed, DownloadStatus.Interrupted,
        ],
        [DownloadStatus.Connecting] =
        [
            DownloadStatus.Downloading, DownloadStatus.Verifying, DownloadStatus.Paused,
            DownloadStatus.Failed, DownloadStatus.Retrying, DownloadStatus.Interrupted,
            DownloadStatus.WaitingForDestination, DownloadStatus.Canceled, DownloadStatus.Completed,
        ],
        [DownloadStatus.Downloading] =
        [
            DownloadStatus.Verifying, DownloadStatus.Paused, DownloadStatus.Interrupted,
            DownloadStatus.Failed, DownloadStatus.Retrying, DownloadStatus.WaitingForDestination,
            DownloadStatus.Canceled, DownloadStatus.Connecting,
        ],
        [DownloadStatus.Retrying] =
        [
            DownloadStatus.Connecting, DownloadStatus.Paused, DownloadStatus.Failed,
            DownloadStatus.Canceled, DownloadStatus.Interrupted, DownloadStatus.WaitingForDestination,
        ],
        [DownloadStatus.Verifying] =
        [
            DownloadStatus.Completed, DownloadStatus.Failed, DownloadStatus.Canceled,
            DownloadStatus.Interrupted, DownloadStatus.WaitingForDestination, DownloadStatus.Paused,
            DownloadStatus.Connecting,
        ],
        [DownloadStatus.Paused] =
        [
            DownloadStatus.Queued, DownloadStatus.Connecting, DownloadStatus.Canceled,
            DownloadStatus.WaitingForDestination,
        ],
        [DownloadStatus.Interrupted] =
        [
            DownloadStatus.Queued, DownloadStatus.Connecting, DownloadStatus.Paused,
            DownloadStatus.Canceled, DownloadStatus.Failed, DownloadStatus.WaitingForDestination,
        ],
        [DownloadStatus.WaitingForDestination] =
        [
            DownloadStatus.Queued, DownloadStatus.Connecting, DownloadStatus.Paused,
            DownloadStatus.Canceled, DownloadStatus.Interrupted,
        ],
        [DownloadStatus.Failed] = [DownloadStatus.Queued, DownloadStatus.Canceled],
        [DownloadStatus.Completed] = [],
        [DownloadStatus.Canceled] = [],
    };

    /// <summary>Transitions only reachable via an explicit user request such as "Download again".</summary>
    private static readonly Dictionary<DownloadStatus, DownloadStatus[]> ExplicitOnly = new()
    {
        [DownloadStatus.Completed] = [DownloadStatus.Queued],
        [DownloadStatus.Canceled] = [DownloadStatus.Queued],
    };

    public static bool CanTransition(DownloadStatus from, DownloadStatus to, bool explicitUserAction = false)
    {
        if (from == to)
        {
            return false;
        }

        if (Allowed.TryGetValue(from, out var targets) && Array.IndexOf(targets, to) >= 0)
        {
            return true;
        }

        return explicitUserAction && ExplicitOnly.TryGetValue(from, out var explicitTargets) && Array.IndexOf(explicitTargets, to) >= 0;
    }

    public static void EnsureCanTransition(DownloadStatus from, DownloadStatus to, bool explicitUserAction = false)
    {
        if (!CanTransition(from, to, explicitUserAction))
        {
            throw new InvalidStateTransitionException(from, to);
        }
    }

    public static bool CanStart(DownloadStatus status) => status is
        DownloadStatus.Queued or DownloadStatus.Paused or DownloadStatus.Interrupted or
        DownloadStatus.WaitingForDestination;

    public static bool CanPause(DownloadStatus status) => status is
        DownloadStatus.Queued or DownloadStatus.Connecting or DownloadStatus.Downloading or
        DownloadStatus.Retrying or DownloadStatus.Interrupted or DownloadStatus.WaitingForDestination;

    public static bool CanRetry(DownloadStatus status) => status is DownloadStatus.Failed;

    public static bool CanCancel(DownloadStatus status) => !status.IsTerminal() && status != DownloadStatus.Verifying;
}
