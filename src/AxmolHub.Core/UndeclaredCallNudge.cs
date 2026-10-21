namespace AxmolHub.Core;

/// <summary>
/// The one correction Hub sends to a model that asked for a tool this request never declared.
///
/// <para><b>The failure this exists for.</b> The endpoint answers with a well-formed call for a name that is not in
/// the request — a shortened one (<c>write</c> for <c>file_write</c>), a name out of the transcript, or an invented
/// one. The invoking client refuses to run an undeclared name and ends its loop there, so nothing executes, no tool
/// result is written, and the turn arrives with no prose at all: the person sees 「模型只返回了思考过程」 about a
/// response that was, in fact, asking to do work. Measured 2026-10-21 in three shapes on one session, one of them a
/// complete <c>write</c> call whose arguments closed properly.</para>
///
/// <para><b>Why the correction rides the run and not a retry.</b> Nothing reaches the transcript — there is no call
/// turn, because no call ran — so sending the same history again replays exactly the request that produced the wrong
/// name. Without a sentence carried by the next request, the model has nothing to learn from and the person's only
/// recourse is to rephrase. One chance per run, like <see cref="ToolCallNudge"/>: a model that repeats the same name
/// after being told twice has nothing more to gain from a third request, and the notice stands as the fallback.</para>
/// </summary>
public static class UndeclaredCallNudge
{
    /// <summary>
    /// What the model is told on its second chance. Model-facing and therefore English, like
    /// <see cref="ToolCallNudge.Sentence"/>: this is transcript text, not UI text.
    ///
    /// <para>Three things it answers: that nothing happened (so a silent turn is not read as success), which name
    /// went unanswered (so a near miss is visible), and that the task continues rather than restarts — the failure
    /// cost the turn's work once already.</para>
    /// </summary>
    public static string Sentence(IReadOnlyList<string> asked)
    {
        // The name verbatim in backticks: whatever the endpoint emitted is what has to be recognised as wrong.
        var names = asked.Count == 1
            ? $"`{asked[0]}`"
            : string.Join(", ", asked.Select(name => $"`{name}`"));
        return $"A tool call was not run: this request declares no tool named {names}, so nothing happened and there "
            + "is no result to read. Call one of the tools declared on this request, spelled exactly as declared, and "
            + "carry on with the task from where it stands — do not start over, and do not hand the work to the "
            + "person.";
    }
}
