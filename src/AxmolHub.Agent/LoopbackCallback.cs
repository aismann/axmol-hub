using System.Net;
using System.Text;

namespace AxmolHub.Agent;

/// <summary>The mechanics both loopback sign-ins need: take one GET, answer the browser, get out of the way.
///
/// <para>OpenAI's fixed-port flow and OrcaRouter's ephemeral-port flow had each written this by hand — the accept
/// with a timeout, the observation of a request abandoned when the listener stops, the courtesy page — and had
/// drifted: one let a broken response pipe end the wait, the other swallowed it. Policy stays with each flow
/// (what counts as accepted, what the page says, whether a mismatched request ends the wait); only the mechanics
/// are here.</para>
/// </summary>
internal static class LoopbackCallback
{
    /// <summary>
    /// The one request this callback answers. Requests are served in turn, so a browser's favicon probe arrives
    /// as its own context and rejecting it is the caller's decision.
    ///
    /// <para>A listener stopped underneath a pending accept faults the request still outstanding; that task is
    /// observed here rather than left to surface later as an unexplained rethrow. A null
    /// <paramref name="timeout"/> waits on <paramref name="token"/> alone — the fixed-port flow budgets the whole
    /// sign-in with one deadline instead of timing each step.</para>
    /// </summary>
    public static async Task<HttpListenerContext> AcceptAsync(
        HttpListener listener, TimeSpan? timeout, CancellationToken token)
    {
        var contextTask = listener.GetContextAsync();
        if (timeout is not { } limit)
        {
            return await contextTask.WaitAsync(token).ConfigureAwait(false);
        }

        var completed = await Task.WhenAny(contextTask, Task.Delay(limit, token)).ConfigureAwait(false);
        if (completed == contextTask)
        {
            return await contextTask.ConfigureAwait(false);
        }

        token.ThrowIfCancellationRequested();
        _ = contextTask.ContinueWith(static abandoned => _ = abandoned.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        throw new TimeoutException("The sign-in was not completed in time.");
    }

    /// <summary>
    /// Leaves the tab on a line a person can read, and never lets that courtesy mask the sign-in's real outcome:
    /// a browser that already closed its pipe is ignored, and the response is released either way.
    /// </summary>
    public static async Task AnswerAsync(HttpListenerResponse response, int status, string message)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            response.StatusCode = status;
            response.ContentType = "text/plain; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The browser may already have gone away; a failed courtesy page must not mask the outcome.
        }
        finally
        {
            response.Close();
        }
    }
}
