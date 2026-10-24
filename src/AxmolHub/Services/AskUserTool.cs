using System.Text.Json;
using AxmolHub.Core;
using Microsoft.Extensions.AI;

namespace AxmolHub;

/// <summary>
/// The tool that lets the assistant stop and ask the person a question.
///
/// <para>It is a hand-written <see cref="AIFunction"/> rather than an <see cref="AIFunctionFactory"/> wrapper
/// around a method for two reasons, and both are about the same fact: this tool has no work to do. Its result is
/// typed by a human, so the only thing its body can usefully return is the sentence for a call that never became
/// a card. A generated signature would deserialize the arguments before the body saw them, which replaces the
/// refusals <see cref="AskUserQuestion"/> writes — "you gave me one option", "this label is blank" — with a
/// binder's exception the model cannot act on. Reading the raw arguments keeps the diagnosis in the tool that
/// knows what a question has to look like.</para>
///
/// <para>The second reason is the schema. <c>minItems</c> and <c>maxItems</c> on the option list are the part of
/// the contract a model reads before it writes anything, and <c>AIFunctionFactoryOptions</c> in this version has
/// no hook for them. It is also where the per-property descriptions go, which is what <c>detail</c> needs: an
/// unlabeled <c>detail</c> is a field models fill with a restatement of the label.</para>
///
/// <para>Whether the call parks is the gate's decision, made from <see cref="ToolRisk.AskUser"/>; nothing here
/// reasons about permissions. The session's own choice about being asked at all is decided one level up, by
/// whether this tool is declared — which is why the class carries no flag for it, and why the body below only
/// ever speaks for a call that could not become a card.</para>
/// </summary>
internal sealed class AskUserTool : AIFunction
{
    public const string ToolName = "ask_user";

    /// <summary>The whole policy of when to spend a person's interruption on it, in the place a model actually
    /// reads it. The clients that shipped this converged on the same three rules — put the answer you recommend
    /// first, do not author your own "other" choice, and never use it to ask permission to proceed — and the last
    /// one matters most here because Hub already has a card for that, with a risk tier and an audit line behind
    /// it. A question that asks "may I run this" would otherwise arrive as a multiple choice with no evidence
    /// about what running it does.</summary>
    public const string Policy =
        "Ask the user one question they can answer and you cannot work out. Use it when the repository, the "
        + "files and your own judgement all fail to settle something the task turns on — which requirement is "
        + "meant, which of two designs to build, which name to use — not to ask permission to proceed, which is "
        + "what the approval card is for, and not to confirm a plan you have already written. Put the answer you "
        + "recommend first and end its label with \" (Recommended)\". Give 2 to 4 options; each needs its own "
        + "words, and none of them may be an \"other\" or \"type your own\" choice, because the card always "
        + "offers a free-text line beside them. If there is no real choice to present, ask in your reply "
        + "instead of calling this. One question per call. If the user skips a question, do not ask it again.";

    private static readonly JsonElement Schema = JsonDocument.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "question": {
              "type": "string",
              "description": "The one thing to decide, phrased so it reads on its own without this conversation."
            },
            "options": {
              "type": "array",
              "minItems": {{AskUserQuestion.MinimumOptions}},
              "maxItems": {{AskUserQuestion.MaximumOptions}},
              "description": "The choices, best guess first. Two at minimum; a single option is a statement.",
              "items": {
                "type": "object",
                "properties": {
                  "label": {
                    "type": "string",
                    "description": "The answer itself, in a few words. This is what the user picks."
                  },
                  "detail": {
                    "type": "string",
                    "description": "What picking this costs or implies, in one short sentence. Say something the label cannot carry, or leave it out — do not restate the label."
                  }
                },
                "required": [ "label" ],
                "additionalProperties": false
              }
            }
          },
          "required": [ "question", "options" ],
          "additionalProperties": false
        }
        """).RootElement;

    public override string Name => ToolName;

    public override string Description => Policy;

    public override JsonElement JsonSchema => Schema;

    /// <summary>Answers a call that never reached a card. A well-formed question parks before this runs, so the
    /// live path here is a malformed one — and the model has to be told which kind of malformed, because an
    /// unanswered call in the transcript makes every later request over it come back rejected.</summary>
    protected override ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(arguments);
        if (AskUserQuestion.TryParse(json, out _, out _, out var refusal))
        {
            // Reachable only if the gate stops parking a question that parses, which is what the AskUser tier
            // exists to prevent. Left as a sentence rather than an empty result because the alternative is the
            // model reading "asked, nobody replied" for a question nobody ever saw.
            return new ValueTask<object?>(
                "The question could not be shown, so it was not answered. Decide it yourself and say which "
                + "option you took.");
        }

        return new ValueTask<object?>(refusal);
    }
}
