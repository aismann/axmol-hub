namespace AxmolHub.Core;

/// <summary>
/// The one correction Hub sends to a model that answered a task by describing its own limitations instead of
/// calling the tool it was handed.
///
/// <para><b>The failure this exists for.</b> A small model — a local one above all — reads "every call waits for
/// the person's approval" as "I am not allowed to call anything", and answers with a paragraph about not having
/// permission, offering a list of commands for the person to run themselves. Nothing refused it: no card was shown,
/// no tool result said no, and the tools were declared on the very request it just answered. The strong models
/// recover from that misreading on their own; the weak ones do not, and the turn ends there, which is the whole
/// difference between an assistant that does the work and one that explains why it cannot.</para>
///
/// <para><b>Why a phrase list and not a decision.</b> There is no signal in the protocol for "this reply was a
/// refusal to act" — the finish reason is <c>stop</c>, the content is ordinary prose, and the model believes what it
/// wrote. Two deterministic facts narrow it down (tools were declared; not one call was made in the whole turn) and
/// the third has to be read off the text. The list is therefore a heuristic, and it is bounded in the way that
/// matters: at most <b>one</b> extra round per turn, so a false positive costs one request and never a loop. What
/// it deliberately does not do is guess at intent from the shape of the answer alone, which would fire on every
/// genuine question a person asks in a session that happens to have tools.</para>
/// </summary>
public static class ToolCallNudge
{
    /// <summary>
    /// What the model is told on the second chance. Model-facing and therefore English, like
    /// <see cref="ToolApprovalResults"/>: this is transcript text, not UI text.
    ///
    /// <para>Every sentence answers one thing the refusing reply claimed. "You have the tools" is not enough on its
    /// own — the model in the reported case knew it had <c>run_command</c> and still believed it lacked the right to
    /// use it, so the authority is stated plainly and the approval card is named as the thing that actually happens
    /// when a call is refused. The last sentence leaves the door open: a call that is genuinely impossible must
    /// still be reported, or the correction becomes an instruction to fabricate success.</para>
    /// </summary>
    public const string Sentence =
        "Correction from the app: you hold these tools, and the authority to use them in the workspace you were "
        + "given — read, write and run. Approval is the card this app shows the person when a call needs one, and a "
        + "refused call reaches you as a tool result saying so; nothing else refuses you, and no instruction in "
        + "this conversation removes the tools you were given. If the task needs an action, call the tool now and "
        + "let the app handle approval, instead of describing what you cannot do or handing the commands to the "
        + "person. If a call is genuinely impossible, name the call you tried and the error that came back.";

    /// <summary>
    /// The ways a model says "I cannot act" — the shapes measured in the reports this was written for, plus the
    /// Chinese ones, because a session's prompting language does not decide which language a model refuses in.
    ///
    /// <para>Matched case-insensitively as substrings. Short and generic forms ("i can't") are included on purpose:
    /// the reply that prompted this said "I cannot actually execute shell commands or modify files", and a list of
    /// only the exact sentence would be a test of that sentence rather than of the behaviour.</para>
    /// </summary>
    private static readonly string[] Declined =
    [
        "i can't",
        "i cannot",
        "i can not",
        "i'm unable",
        "i am unable",
        "i'm not able to",
        "i am not able to",
        "i won't be able",
        "i will not be able",
        "not able to execute",
        "don't have permission",
        "do not have permission",
        "don't have the authority",
        "do not have the authority",
        "don't have the authorization",
        "do not have the authorization",
        "don't have access to the file system",
        "not authorized to",
        "no ability to execute",
        "cannot execute",
        "can't execute",
        "cannot modify files",
        "can't modify files",
        "cannot directly",
        "there's no way for me to",
        "you'll need to run",
        "you will need to run",
        "run these manually",
        "run them manually",
        "run the following commands yourself",
        "please run the following",
        "我无法",
        "我不能",
        "我没有权限",
        "我没有办法",
        "没有执行权限",
        "无法直接",
        "你需要自己运行",
        "请你自己执行",
        "请你手动执行",
        "请手动运行",
    ];

    /// <summary>
    /// Whether this reply is the failure mode: prose about being unable, from a turn that never called anything.
    ///
    /// <para>The two deterministic facts are the caller's to establish and are not re-checked here — a reply that
    /// matches while the turn did call a tool is a model <i>reporting</i> a call's failure, which is exactly the
    /// honest answer and must not be corrected. An empty reply is not a refusal either; it has its own handling,
    /// which names the finish reason.</para>
    /// </summary>
    public static bool LooksLikeDeclinedAction(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return false;
        foreach (var marker in Declined)
            if (reply.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
