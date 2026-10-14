using System.ClientModel.Primitives;

namespace AxmolHub.Agent;

/// <summary>
/// The retry policy's wait, reported to whoever owns the run.
///
/// <para><see cref="ClientRetryPolicy"/> does its sleeping inside the client pipeline, below the streaming
/// enumerator, so from Hub's side a provider that answers 429 and a provider that has stopped answering look
/// identical: silence. The app's own inactivity deadline could not tell them apart either, and it usually fired
/// first — a run then reported "the stream stopped" for what was really the provider's <c>Retry-After</c> being
/// obeyed, and the user was sent to check a network that was fine.</para>
///
/// <para><see cref="Wait"/> and <see cref="WaitAsync"/> are the only documented hooks that span the delay, so they
/// are where the notice has to come from. The wait itself is left to the base policy: this class adds no retry
/// decision, it only says when one is being sat out.</para>
/// </summary>
public sealed class IdleAwareRetryPolicy(int maxRetries, Action<int, TimeSpan>? onWait) : ClientRetryPolicy(maxRetries)
{
    /// <summary>How many times the policy has tried to send the request, taken from
    /// <see cref="ClientRetryPolicy.GetNextDelay"/> so the wait can name the attempt that follows it. The base
    /// class passes the delay and the count to separate methods, and only the count arrives first.</summary>
    private int _tried;

    protected override TimeSpan GetNextDelay(PipelineMessage message, int tryCount)
    {
        _tried = tryCount;
        return base.GetNextDelay(message, tryCount);
    }

    protected override void Wait(TimeSpan delay, CancellationToken cancellationToken)
    {
        Notice(delay);
        try
        {
            base.Wait(delay, cancellationToken);
        }
        finally
        {
            Release();
        }
    }

    protected override async Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Notice(delay);
        try
        {
            await base.WaitAsync(delay, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Release();
        }
    }

    private void Notice(TimeSpan delay) => onWait?.Invoke(_tried + 1, delay);

    private void Release() => onWait?.Invoke(0, TimeSpan.Zero);
}
