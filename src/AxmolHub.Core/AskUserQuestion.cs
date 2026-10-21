using System.Text.Json;

namespace AxmolHub.Core;

/// <summary>
/// One question the assistant asked the person, read out of the arguments of an <c>ask_user</c> call.
///
/// <para>The call has no body to run — the answer <i>is</i> its result — so this class is the whole of what the
/// tool does: it decides whether the model asked something a card can be built from, and it words the answer on
/// its way back. It lives in Core beside <see cref="ToolPreviews"/> so both halves can be asserted without a
/// window, and it stays ignorant of which tool it belongs to in the way <see cref="ToolTrust"/> does: the name
/// <c>ask_user</c> is the app's registry's business, and nothing here decides anything about permissions.</para>
///
/// <para>Refusal is a verdict rather than an exception, because the caller has to tell the model what it got
/// wrong in a form it can correct: a call that cannot become a card is still a call that has to be answered, or
/// the transcript keeps an unanswered call and every later request over it comes back rejected.</para>
/// </summary>
public static class AskUserQuestion
{
    /// <summary>Below this the "question" is a yes/no the assistant should have settled itself, and one option is
    /// not a choice at all.</summary>
    public const int MinimumOptions = 2;

    /// <summary>Above this the card stops being scannable, and the letter keys start competing with the composer
    /// for the same keystrokes. The clients that got here first all landed in the same place: Copilot's elicitation
    /// and OpenClaw allow three, Qoder's own card allows four.</summary>
    public const int MaximumOptions = 4;

    /// <summary>How much question text a card <i>draws</i>. Long past what a decision needs, and short enough that
    /// the question and its options fit the one row above the composer the charter gives a question. Nothing past
    /// it is discarded any more: the parse keeps the whole sentence and the card's tip carries the rest, because a
    /// question is model-authored text and a tooltip is the only place a bounded row can hold all of it.</summary>
    public const int QuestionLimit = 240;

    /// <summary>What one option row draws of its label, and of the detail under it. The same bargain as the
    /// question's own limit: the row draws this much, the tip carries the whole of what was written.</summary>
    public const int LabelLimit = 80;
    public const int DetailLimit = 240;

    /// <summary>One answer the person can pick. <paramref name="letter"/> is the cap drawn on its row and the key
    /// that arms it, assigned by position rather than authored: a model that wrote its own letters would be free
    /// to skip A or name a fifth, and the card's keyboard would no longer match what it draws.
    ///
    /// <para><paramref name="label"/> and <paramref name="detail"/> are the whole of what the model wrote, and the
    /// <c>…Shown</c> pair is the bounded surface's view of them. Keeping both is what lets the row draw a fixed
    /// height and still offer the rest in its tip — and it is why the answer sent back is the whole label rather
    /// than one that ends in an ellipsis the model never typed.</para></summary>
    public sealed record Option(string Letter, string Label, string Detail)
    {
        /// <summary>What the option row draws of the label.</summary>
        public string LabelShown => Cut(Label, LabelLimit);

        /// <summary>What the row draws of the detail under it.</summary>
        public string DetailShown => Cut(Detail, DetailLimit);
    }

    /// <summary>Reads a call's arguments into the question a card shows. Returns false with the sentence the model
    /// gets instead when they cannot become one — which covers unparseable JSON as well as the structural
    /// refusals, because a call whose arguments are not arguments has no question either.</summary>
    public static bool TryParse(
        string? argumentsJson, out string question, out IReadOnlyList<Option> options, out string refusal)
    {
        question = "";
        options = [];
        refusal = "";

        var arguments = Read(argumentsJson);
        if (arguments is null)
        {
            refusal = "ask_user was called with arguments that are not a JSON object, so there was no question "
                      + "to show. Call it again with \"question\" and 2 to 4 \"options\".";
            return false;
        }

        var asked = Text(arguments, "question");
        if (asked.Length == 0)
        {
            refusal = "ask_user was called without a question, so nothing was shown. Give it the one thing only "
                      + "the user can decide, phrased as a question.";
            return false;
        }

        if (!arguments.TryGetValue("options", out var raw) || raw.ValueKind != JsonValueKind.Array)
        {
            refusal = "ask_user needs an \"options\" array of 2 to 4 choices. Without options this is a plain "
                      + "question — ask it in your reply instead of calling the tool.";
            return false;
        }

        var read = new List<Option>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in raw.EnumerateArray())
        {
            var label = entry.ValueKind switch
            {
                JsonValueKind.Object => Text(entry, "label"),
                JsonValueKind.String => entry.GetString() ?? "",
                _ => "",
            };
            // Trimmed for the comparison and for the card: a label that is only whitespace is not a choice, and
            // one that differs from another by a trailing space is the same choice twice.
            label = label.Trim();
            if (label.Length == 0)
            {
                refusal = "One of the ask_user options has no label, so the card would show a blank row the user "
                          + "cannot read. Every option needs its own words.";
                return false;
            }

            if (!seen.Add(label))
            {
                refusal = $"ask_user lists \"{label}\" twice. Two rows saying the same thing teach the user nothing "
                          + "about the choice, so the question was not shown.";
                return false;
            }

            // Whole, not cut: the row draws LabelShown of it and its tip holds the rest, and a label ending in an
            // ellipsis is one the answer text would hand back to the model as an option it never wrote.
            read.Add(new Option("", label, Text(entry, "detail").Trim()));
        }

        if (read.Count < MinimumOptions || read.Count > MaximumOptions)
        {
            refusal = $"ask_user takes {MinimumOptions} to {MaximumOptions} options and this call had "
                      + $"{read.Count}. With one option there is nothing to choose; past {MaximumOptions} the card "
                      + "is a list to read rather than a decision. Split it, or ask in your reply.";
            return false;
        }

        // Letters last, once the count is known to be legal: the card's caps, its keyboard and the answer text all
        // have to name the same row, and deriving them here is what keeps those three from drifting.
        var lettered = new List<Option>(read.Count);
        for (var i = 0; i < read.Count; i++)
            lettered.Add(read[i] with { Letter = ((char)('A' + i)).ToString() });

        // Whole for the same reason the labels are: the card draws QuestionShown of it and its title's tip carries
        // the rest, so the parse is where the sentence stops being shortened.
        question = asked.Trim();
        options = lettered;
        refusal = "";
        return true;
    }

    /// <summary>What the card draws of the question. It wraps, so the bound is not a line — it is the height the
    /// one row above the composer may take before the options under it stop being reachable.</summary>
    public static string QuestionShown(string question) => Cut(question, QuestionLimit);

    /// <summary>What the model reads when a person picks one of its options. The letter rides along with the label
    /// because that is what the person saw, and because a model holding two of these in its history can otherwise
    /// no longer tell which row it had called "B".
    ///
    /// <para>The label is the whole of what the model wrote, not the row's drawn head: a tool result saying
    /// <c>B: The transport layer, which is where the retry…</c> would describe a choice nobody was offered.</para></summary>
    public static string Answered(string question, Option option)
        => $"The user answered \"{option.Letter}: {option.Label}\" to \"{question}\".";

    /// <summary>What the model reads when a person wrote their own answer instead. Said to be the user's own words
    /// because it is the one tool result in this app that is verbatim from the human rather than from a file.</summary>
    public static string AnsweredInOwnWords(string question, string text)
        => $"The user answered \"{question}\" in their own words: \"{text}\".";

    /// <summary>The question's head, for the one line the transcript keeps beside a call that is still waiting.
    /// A preview of a question is not evidence that can decay, so unlike the write and command previews this reads
    /// nothing from disk — it is the same text the card shows, shortened.</summary>
    public static string Preview(string? argumentsJson)
    {
        var arguments = Read(argumentsJson);
        if (arguments is null) return ToolNames.AskUser;
        var asked = Text(arguments, "question").Trim();
        return asked.Length == 0
            ? "ask_user · (no question text)"
            : "ask_user · " + Cut(asked, 80);
    }

    /// <summary>The arguments as a flat object, or null when they are not one. Unlike
    /// <see cref="ToolPreviews"/>'s reader this has to tell a malformed call from an empty one, because for a
    /// question the difference decides whether the model is told what it got wrong.</summary>
    private static IReadOnlyDictionary<string, JsonElement>? Read(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return null;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argumentsJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Text(IReadOnlyDictionary<string, JsonElement> arguments, string name)
        => arguments.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string Text(JsonElement entry, string name)
        => entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    /// <summary>The drawing cut: the head of a longer string, with an ellipsis where it stopped. Nothing is thrown
    /// away by it any more — the parse keeps the whole text and this is what a bounded surface draws of it, with
    /// the rest riding in a tip — so it shortens rather than refuses, and the element that draws it is the one that
    /// decides whether the text it drew was already the whole of it. The cut trims back to the last non-space so an
    /// ellipsis never sits after a gap nobody typed.</summary>
    private static string Cut(string value, int limit)
        => value.Length <= limit
            ? value
            : value.Substring(0, limit - 1).TrimEnd() + "…";
}
