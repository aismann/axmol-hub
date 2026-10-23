using System.Text;
using System.Text.Json;
using AxmolHub.Core;
using Microsoft.Extensions.AI;

internal static class AiToolsChecks
{
    internal static async Task RunAsync(string root, string[] args)
    {
        // The tools are the part that touches the disk, so they are asserted here rather than through a window.
        // Every refusal is model-facing: one that does not say what to do instead becomes a retry loop nobody sees.
        var toolRoot = Path.Combine(root, "tools");
        if (Directory.Exists(toolRoot))
            Directory.Delete(toolRoot, recursive: true);
        var workspace = Path.Combine(toolRoot, "ws");
        var dataRoot = Path.Combine(toolRoot, "data");
        var toolEngineRoot = Path.Combine(toolRoot, "engine");
        Directory.CreateDirectory(Path.Combine(workspace, "src"));
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(toolEngineRoot);
        File.WriteAllText(Path.Combine(workspace, "src", "main.cpp"), "int main()\n{\n    return 0;\n}\n");
        File.WriteAllBytes(Path.Combine(workspace, "src", "logo.png"), [0x89, 0x50, 0x4E, 0x47, 0xFF, 0xFE, 0xFF]);
        File.WriteAllText(Path.Combine(workspace, "src", "long.txt"), string.Concat(Enumerable.Range(1, 50).Select(index => $"line-{index:D2}\n")));
        var guards = new WorkspaceGuards(dataRoot, [toolEngineRoot]);
        var log = new HubLog(Path.Combine(toolRoot, "log"));
        var scope = new WorkspaceToolScope(workspace, guards, dataRoot, "conversation-1", ["SUPERSECRET"], log, null);
        var tools = new WorkspaceTools(scope);
        var homeless = new WorkspaceTools(new WorkspaceToolScope(null, guards, dataRoot, "c", [], null, null));
        // ── read_file: a window, no line numbers, and one sentence per way it can fail ──
        var whole = tools.ReadFile("src/main.cpp");
        if (!whole.Contains("lines 1-4 of 4 (UTF-8)") || !whole.Contains("int main()") || whole.Contains("\n1\t", StringComparison.Ordinal) || whole.Contains("\n1|", StringComparison.Ordinal))
            throw new Exception($"read_file did not return the file as a plain window:{Environment.NewLine}{whole}");
        var window = tools.ReadFile("src/long.txt", 10, 5);
        if (!window.Contains("lines 10-14 of 50") || !window.Contains("line-10") || !window.Contains("line-14") || window.Contains("line-15") || !window.Contains("offset=15"))
            throw new Exception($"The read window was wrong:{Environment.NewLine}{window}");
        if (!homeless.ReadFile("a.txt").Contains("no workspace directory", StringComparison.Ordinal))
            throw new Exception("read_file without a workspace did not say so.");
        if (!tools.ReadFile("../outside.txt").Contains("Refused", StringComparison.Ordinal) || !tools.ReadFile("src/logo.png").Contains("not UTF-8", StringComparison.Ordinal))
            throw new Exception("read_file let an escaping path or a binary through.");
        File.WriteAllText(Path.Combine(workspace, "src", "huge.txt"), new string ('x', WorkspaceTools.MaxReadFileBytes + 16));
        // Over the cap the file is streamed as a window rather than refused: the cap bounds what comes back, and the
        // hint at the end is the refusal's replacement — one line bigger than the cap on its own is the case no
        // window can serve, so that is what the sentence has to say.
        var huge = tools.ReadFile("src/huge.txt");
        if (!huge.Contains("on its own", StringComparison.Ordinal) || !huge.Contains("run_command", StringComparison.Ordinal) || huge.Contains("xxxx", StringComparison.Ordinal))
            throw new Exception($"An oversized file was not streamed as a capped window:{Environment.NewLine}{huge}");
        Console.WriteLine("PASS: read_file returns a window of text only, and says what to do in every refusal.");
        // ── file_write: the five verdicts, the pre-image, and the same guards ──
        var edited = tools.FileWrite("src/main.cpp", "    return 0;", "    return 1;");
        if (!edited.Contains("Edited") || !File.ReadAllText(Path.Combine(workspace, "src", "main.cpp")).Contains("return 1;"))
            throw new Exception($"An anchored edit did not land:{Environment.NewLine}{edited}");
        // The pre-image used to be a copy written beside the file and named in the result; it is the "before" half of a
        // change record now, addressed by the id the result carries, and that record is what the undo row restores from.
        var revisionId = ChatChangeStore.IdFromResult(edited);
        ChatFileSnapshot? preImage = revisionId is null ? null : ChatChangeStore.ReadSnapshot(dataRoot, revisionId, out _);
        if (revisionId is null || preImage is not { Before: { } before } || !before.Contains("return 0;", StringComparison.Ordinal))
            throw new Exception($"The pre-image was not kept before the write (id='{revisionId ?? "none"}').");
        var created = tools.FileWrite("src/notes.md", "", "# 记录\n");
        if (!created.Contains("Created"))
            throw new Exception("An empty anchor did not create the file.");
        // The guard used to read a missing ancestor as a link, so every new file in a new folder was refused; the tool
        // is where that shows up, because the verdict alone said "ReparsePoint" about a directory that is not there.
        var nested = tools.FileWrite("platform/ios/App.entitlements", "", "<plist/>");
        if (!nested.Contains("Created") || !File.Exists(Path.Combine(workspace, "platform", "ios", "App.entitlements")))
            throw new Exception($"A new file in a directory the project has not grown was refused:{Environment.NewLine}{nested}");
        if (!tools.FileWrite("src/main.cpp", "不存在的锚点", "x").Contains("was not found"))
            throw new Exception("A missing anchor was not reported as NotFound.");
        File.WriteAllText(Path.Combine(workspace, "src", "twice.txt"), "dup\ndup\n");
        if (!tools.FileWrite("src/twice.txt", "dup", "one").Contains("matched 2 places"))
            throw new Exception("An anchor matching twice was not reported as Ambiguous.");
        if (!tools.FileWrite("src/main.cpp", "    return 1;", "    return 1;").Contains("identical"))
            throw new Exception("A no-op edit was not reported as Unchanged.");
        if (!tools.FileWrite("src/main.cpp", "", "whole file").Contains("already exists"))
            throw new Exception("An empty anchor against an existing file did not refuse to overwrite it.");
        if (!tools.FileWrite("src/tool.exe", "", "MZ").Contains("not a text file") || !tools.FileWrite("../escape.cpp", "a", "b").Contains("Refused"))
            throw new Exception("file_write ignored the write allowlist or the sandbox.");
        if (File.ReadAllText(Path.Combine(workspace, "src", "twice.txt")) != "dup\ndup\n")
            throw new Exception("A refused edit changed the file anyway.");
        // A CRLF file comes back LF — the whole file, not only the replaced span, since a file holding both newline
        // styles makes every later anchor guess. A no-op stays a refusal to touch bytes, so the same fixture asked
        // to change nothing must still be the CRLF it started as.
        var crlfTarget = Path.Combine(workspace, "src", "crlf.cpp");
        File.WriteAllText(crlfTarget, "int main() {\r\n    return 0;\r\n}\r\n");
        if (!tools.FileWrite("src/crlf.cpp", "    return 0;", "    return 2;").Contains("Edited", StringComparison.Ordinal))
            throw new Exception("An LF anchor did not edit a CRLF file through file_write.");
        if (File.ReadAllText(crlfTarget).Contains('\r'))
            throw new Exception("file_write edited a CRLF file and left the rest of it CRLF.");
        var crlfNoop = Path.Combine(workspace, "src", "noop.cpp");
        File.WriteAllText(crlfNoop, "a\r\nb\r\n");
        var noopBefore = File.ReadAllBytes(crlfNoop);
        if (!tools.FileWrite("src/noop.cpp", "b", "b").Contains("identical", StringComparison.Ordinal) || !File.ReadAllBytes(crlfNoop).SequenceEqual(noopBefore))
            throw new Exception("A no-op edit normalized a CRLF file it was only supposed to refuse.");
        // The log folds at the sink as well, because a message can arrive with the host's newline already inside it:
        // a command's output, a reply echoed in. Disk gets LF, and the break the message carried is kept.
        log.Write("two\r\nlines");
        var logBytes = File.ReadAllBytes(log.FilePath);
        if (logBytes.Contains((byte)13))
            throw new Exception($"The hub log wrote the host's newline into the log ({logBytes.Count(b => b == 13)} CR bytes).");
        if (!File.ReadAllText(log.FilePath).Contains("two\nlines", StringComparison.Ordinal))
            throw new Exception("A log line folded to LF lost the break it was written with.");
        Console.WriteLine("PASS: the hub log keeps LF newlines whatever the message arrived with.");
        // ── the line tally a write hands back: only for a change that landed, and never capped ──
        // The transcript cannot answer "how far did this move the file" by itself. The only diff a session keeps is the
        // one frozen for a card — which does not exist for a write the mode allowed without asking, and stops at 60
        // output lines when it does. The write is the one moment both sides of it are in memory.
        var tallies = new List<ChangeLineCounts>();
        var tallyTools = new WorkspaceTools(new WorkspaceToolScope(workspace, guards, dataRoot, "tally-1", [], log, RecordChangeLines: tallies.Add));
        Directory.CreateDirectory(Path.Combine(workspace, "tally"));
        File.WriteAllText(Path.Combine(workspace, "tally", "a.txt"), "one\ntwo\nthree\n");
        File.WriteAllText(Path.Combine(workspace, "tally", "twice.txt"), "a\nb\na\n");
        tallyTools.FileWrite("tally/a.txt", "two", "TWO");
        tallyTools.FileWrite("tally/new.txt", "", "x\ny\n");
        tallyTools.FileWrite("tally/twice.txt", "a", "z", true);
        // Three of the six ways out of the tool moved something and report it. The other three changed nothing and must
        // report nothing: a number queued for an edit that never landed is a number the next call would be stamped with.
        tallyTools.FileWrite("tally/a.txt", "no-such-anchor", "x");
        tallyTools.FileWrite("../outside.txt", "a", "b");
        tallyTools.FileWrite("tally/a.txt", "TWO", "TWO");
        if (tallies.Count != 3)
            throw new Exception($"A write that changed nothing reported a tally — {tallies.Count} reports for three landed edits.");
        if (tallies[0] is not { Added: 1, Removed: 1 } || tallies[1] is not { Added: 2, Removed: 0 })
            throw new Exception($"One line replaced counted {tallies[0].Added}+/{tallies[0].Removed}- and a two-line file " + $"created counted {tallies[1].Added}+/{tallies[1].Removed}-; the fixture fixes both.");
        if (tallies[2] is not { Added: 2, Removed: 2 })
            throw new Exception($"replace_all over \"a\\nb\\na\\n\" counted {tallies[2].Added}+/{tallies[2].Removed}- " + "instead of both occurrences. The multiplicity is only in the applied edit, so an implementation that " + "diffs old_string against new_string rather than the whole file gets this wrong silently.");
        Console.WriteLine("PASS: a write reports its line tally exactly when it landed, and replace_all counts every occurrence.");
        // The cap belongs to the card, not to the change. This is the whole reason the number is recorded rather than
        // read back: an implementation built on the frozen preview fails here and nowhere else.
        var tallyFew = string.Concat(Enumerable.Range(1, 50).Select(index => $"old-{index}\n"));
        var tallyMany = string.Concat(Enumerable.Range(1, 200).Select(index => $"new-{index}\n"));
        var rewrite = FileDiff.CountChangedLines(tallyFew, tallyMany);
        if (rewrite is not { Added: 200, Removed: 50 })
            throw new Exception($"A 200-line rewrite of a 50-line file counted {rewrite.Added}+/{rewrite.Removed}- " + "instead of the whole of it.");
        var rewriteRendered = FileDiff.Unified(tallyFew, tallyMany, "big.txt");
        var rewriteFromText = ChatChanges.CountChanges(rewriteRendered);
        if (rewriteFromText.Added + rewriteFromText.Removed >= 250 || !rewriteRendered.Contains("more changed lines"))
            throw new Exception($"The rendered preview was supposed to be the capped one — it counted " + $"{rewriteFromText.Added}+/{rewriteFromText.Removed}- and " + (rewriteRendered.Contains("more changed lines") ? "said so" : "hid the shortfall"));
        Console.WriteLine("PASS: the tally counts the whole edit; the rendered preview still stops short and says so.");
        // Where the rendering is not capped the two must agree, or the number beside a file and the diff under it are
        // two claims about one change.
        foreach (var(tallyBefore, tallyAfter)in new[]
        {
            ("a\nb\nc\n", "a\nX\nc\n"),
            ("", "only\n"),
            ("one\ntwo\n", ""),
            ("x\n", "x\ny\nz\n"),
        }

        )
        {
            var counted = FileDiff.CountChangedLines(tallyBefore, tallyAfter);
            var fromText = ChatChanges.CountChanges(FileDiff.Unified(tallyBefore, tallyAfter, "p.txt"));
            if (counted.Added != fromText.Added || counted.Removed != fromText.Removed)
                throw new Exception($"The script counted {counted.Added}+/{counted.Removed}- for a change the rendering " + $"counts {fromText.Added}+/{fromText.Removed}- (\"{tallyBefore}\" → \"{tallyAfter}\").");
        }

        // Why the tally may be taken from the applied edit instead of re-derived from the file: trimming the common
        // prefix and suffix means the context around an edit cannot move its count. This is the property the rejected
        // recompute-from-arguments design rested on; it is pinned here so the choice stays argued rather than remembered.
        var tallyHead = string.Concat(Enumerable.Range(1, 30).Select(index => $"head-{index}\n"));
        var tallyTail = string.Concat(Enumerable.Range(1, 30).Select(index => $"tail-{index}\n"));
        var blockAlone = FileDiff.CountChangedLines("alpha\nbeta\ngamma\n", "alpha\nBETA\ngamma\ndelta\n");
        var blockWrapped = FileDiff.CountChangedLines(tallyHead + "alpha\nbeta\ngamma\n" + tallyTail, tallyHead + "alpha\nBETA\ngamma\ndelta\n" + tallyTail);
        if (blockAlone is not { Added: 2, Removed: 1 } || blockWrapped.Added != blockAlone.Added || blockWrapped.Removed != blockAlone.Removed)
            throw new Exception($"The same block counted {blockAlone.Added}+/{blockAlone.Removed}- on its own but " + $"{blockWrapped.Added}+/{blockWrapped.Removed}- inside sixty lines of context.");
        Console.WriteLine("PASS: the tally matches the rendered diff wherever that diff is not capped, and ignores the " + "context wrapped around the edit.");
        // ── the one ladder every surface reads, and the session file that has to keep loading ──
        var ladderPreview = FileDiff.Unified(tallyFew, tallyMany, "big.txt");
        var ladderBoth = new ChatTurn(ChatRoles.Assistant, "", DateTimeOffset.UnixEpoch)
        {
            ToolName = "file_write",
            ToolCallId = "c-both",
            ChangeLines = new ChangeLineCounts(200, 50),
            ApprovalPreview = ladderPreview,
        };
        if (ChatChanges.LinesOf(ladderBoth)is not { Added: 200, Removed: 50 })
            throw new Exception("A turn that knows its own tally was still read off its capped preview.");
        var ladderPreviewOnly = ChatChanges.LinesOf(ladderBoth with { ChangeLines = null });
        if (ladderPreviewOnly.Added <= 0)
            throw new Exception("With no recorded tally the preview is the only thing left, and it counted " + $"{ladderPreviewOnly.Added}+ — a session written before this field existed would lose its numbers entirely.");
        if (ChatChanges.LinesOf(new ChatTurn(ChatRoles.Assistant, "", DateTimeOffset.UnixEpoch) { ToolName = "read_file", ToolCallId = "r-1" })is not { Added: 0, Removed: 0 })
            throw new Exception("A turn that wrote no lines claimed some.");
        var ladderSession = new Conversation
        {
            Id = "tally-session",
            Title = "tally"
        };
        var ladderArgs = """{"path":"src/p.cpp","old_string":"a","new_string":"b"}""";
        ladderSession.Append(new ChatTurn(ChatRoles.Assistant, "", DateTimeOffset.UnixEpoch) { ToolName = "file_write", ToolCallId = "w-1", ToolArguments = ladderArgs, ChangeLines = new ChangeLineCounts(1, 1), });
        ladderSession.Append(new ChatTurn(ChatRoles.Tool, "Edited src/p.cpp.", DateTimeOffset.UnixEpoch) { ToolCallId = "w-1" });
        ladderSession.Append(new ChatTurn(ChatRoles.Assistant, "", DateTimeOffset.UnixEpoch) { ToolName = "file_write", ToolCallId = "w-2", ToolArguments = ladderArgs, ChangeLines = new ChangeLineCounts(5, 2), });
        ladderSession.Append(new ChatTurn(ChatRoles.Tool, "Edited src/p.cpp.", DateTimeOffset.UnixEpoch) { ToolCallId = "w-2" });
        // Two writes to one path are one row in the pane the tally opens, so the total is the row's: the newest write.
        var ladderSum = ChatChanges.Summary(ladderSession);
        if (ladderSum is not (1, 5, 2))
            throw new Exception($"Two writes to one file summed to {ladderSum.Files} files " + $"{ladderSum.Added}+/{ladderSum.Removed}- — the pane lists one row, so the pill must not total both.");
        if (ChatChanges.Summary(null)is not (0, 0, 0))
            throw new Exception("A session with no conversation reported a change.");
        Console.WriteLine("PASS: the tally prefers the recorded count, falls back to a frozen preview, and sums the " + "newest write per path — which is the row the click opens.");
        var tallyStoreRoot = Path.Combine(toolRoot, "tally-store");
        Directory.CreateDirectory(Path.Combine(tallyStoreRoot, "ai", "sessions"));
        var tallyStored = new Conversation
        {
            Id = "tally-stored",
            Title = "t"
        };
        tallyStored.Append(new ChatTurn(ChatRoles.Assistant, "", DateTimeOffset.UnixEpoch) { ToolName = "file_write", ToolCallId = "s-1", ToolArguments = ladderArgs, ChangeLines = new ChangeLineCounts(7, 3) });
        new ConversationStore(tallyStoreRoot).Save(tallyStored);
        if (new ConversationStore(tallyStoreRoot).Load("tally-stored")?.Messages[0].ChangeLines is not { Added: 7, Removed: 3 })
            throw new Exception("A recorded tally did not survive the session file, so it would vanish on restart.");
        // An older session file has no such key at all. It has to load, and it must not be handed a tally it never earned.
        var tallyLegacyFile = Path.Combine(tallyStoreRoot, "ai", "sessions", "tally-legacy.json");
        File.WriteAllText(tallyLegacyFile, "{\"Id\":\"tally-legacy\",\"Title\":\"旧会话\",\"Messages\":[{\"Role\":\"assistant\",\"Text\":\"\"," + "\"At\":\"2026-01-01T00:00:00+08:00\",\"ToolName\":\"file_write\",\"ToolCallId\":\"l-1\"}]}");
        var tallyLegacy = new ConversationStore(tallyStoreRoot).Load("tally-legacy");
        if (tallyLegacy is null || tallyLegacy.Messages.Count != 1 || tallyLegacy.Messages[0].ChangeLines is not null)
            throw new Exception("A session file written before the tally existed either did not load or was given one.");
        if (ChatChanges.LinesOf(tallyLegacy!.Messages[0])is not { Added: 0, Removed: 0 })
            throw new Exception("An old session with no preview and no tally invented line counts.");
        Directory.Delete(tallyStoreRoot, recursive: true);
        Console.WriteLine("PASS: a recorded tally round-trips through the session file, and a session written before it " + "existed loads unchanged and claims nothing.");
        // ── the undo: one pre-image goes back over the one write it was made for, and over nothing else ──
        var undoRoot = ChatUndoStore.DirectoryFor(dataRoot, "conversation-1")!;
        var writeArgs = """{"path":"src/main.cpp","old_string":"    return 0;","new_string":"    return 1;"}""";
        // The write path no longer leaves a copy beside the file: its result carries a change id and the pre-image is a
        // ChatChangeStore snapshot (asserted above). The revert button still offers this reader for turns recorded before
        // that change, so the fixtures below build the copy that path expects instead of waiting for today's write to
        // make one — Store() is the call the old write path made, with the same text, for a file that has since moved on.
        if (ChatUndoStore.NameFromResult(edited)is not null || ChatUndoStore.NameFromResult(created)is not null)
            throw new Exception("A current write still advertises a legacy copy beside its change id.");
        var copyName = ChatUndoStore.Store(dataRoot, "conversation-1", "src/main.cpp", "int main()\n{\n    return 0;\n}\n")is { Stored: true, Path: { } copyPath } ? Path.GetFileName(copyPath) : throw new Exception("The legacy pre-image for src/main.cpp was not kept.");
        var reverted = ChatUndoStore.Restore(dataRoot, "conversation-1", copyName, workspace, guards, writeArgs, out var revertedTo);
        if (reverted != UndoVerdict.Reverted || revertedTo != "src/main.cpp")
            throw new Exception($"Reverting an untouched file refused ({reverted}, {revertedTo}).");
        if (!File.ReadAllText(Path.Combine(workspace, "src", "main.cpp")).Contains("return 0;"))
            throw new Exception("A revert reported success but left the assistant's edit in the file.");
        if (File.Exists(Path.Combine(undoRoot, copyName)))
            throw new Exception("A revert kept the copy it just spent, so the same undo could be taken twice.");
        if (ChatUndoStore.Restore(dataRoot, "conversation-1", copyName, workspace, guards, writeArgs, out _) != UndoVerdict.CopyMissing)
            throw new Exception("Reverting a spent copy was not reported as having no copy.");
        // The guard is the reason a revert is a command rather than a file copy: somebody else may have edited the
        // file after the assistant, and their work is worth more than the regret.
        var secondCopy = ChatUndoStore.Store(dataRoot, "conversation-1", "src/main.cpp", "int main()\n{\n    return 0;\n}\n")is { Stored: true, Path: { } secondPath } ? Path.GetFileName(secondPath) : throw new Exception("The second legacy pre-image was not kept.");
        if (ChatUndoStore.NameFromResult(tools.FileWrite("src/main.cpp", "    return 0;", "    return 1;"))is not null)
            throw new Exception("A current write still advertises a legacy copy beside its change id.");
        File.AppendAllText(Path.Combine(workspace, "src", "main.cpp"), "// edited by hand\n");
        if (ChatUndoStore.Restore(dataRoot, "conversation-1", secondCopy, workspace, guards, writeArgs, out _) != UndoVerdict.ChangedSince)
            throw new Exception("A revert overwrote a change made after the write.");
        if (!File.ReadAllText(Path.Combine(workspace, "src", "main.cpp")).Contains("// edited by hand"))
            throw new Exception("A refused revert still touched the file.");
        if (!File.Exists(Path.Combine(undoRoot, secondCopy)))
            throw new Exception("A revert that refused spent the copy, so the undo is gone with the hand edit kept.");
        // A pre-image whose file was CRLF replays to LF, because that is what a write produces now. So when the disk
        // holds the assistant's edit in CRLF again — a checkout that re-materialized the newline, or a record made
        // before this policy — the replay and the file differ in nothing but newline, and that is not "somebody else
        // changed this file". The bytes handed back are the pre-image's own CRLF, not a normalization of them.
        var eolTarget = Path.Combine(workspace, "src", "eol.cpp");
        const string eolBefore = "int main() {\r\n    return 0;\r\n}\r\n";
        const string eolAfterCrlf = "int main() {\r\n    return 3;\r\n}\r\n";
        File.WriteAllText(eolTarget, eolBefore);
        var eolCopy = ChatUndoStore.Store(dataRoot, "conversation-1", "src/eol.cpp", eolBefore)is { Stored: true, Path: { } eolPath } ? Path.GetFileName(eolPath) : throw new Exception("The newline-style legacy pre-image was not kept.");
        if (!tools.FileWrite("src/eol.cpp", "    return 0;", "    return 3;").Contains("Edited", StringComparison.Ordinal))
            throw new Exception("The newline-style write did not land.");
        if (File.ReadAllText(eolTarget) != "int main() {\n    return 3;\n}\n")
            throw new Exception("file_write left the edited CRLF file in a newline other than LF.");
        File.WriteAllText(eolTarget, eolAfterCrlf);
        var eolArgs = """{"path":"src/eol.cpp","old_string":"    return 0;","new_string":"    return 3;"}""";
        var eolRevert = ChatUndoStore.Restore(dataRoot, "conversation-1", eolCopy, workspace, guards, eolArgs, out _);
        if (eolRevert != UndoVerdict.Reverted)
            throw new Exception($"A revert refused over a file whose newline style, not its text, had moved ({eolRevert}).");
        if (File.ReadAllText(eolTarget) != eolBefore)
            throw new Exception("A revert restored LF bytes where its own pre-image was CRLF.");
        File.WriteAllBytes(Path.Combine(workspace, "src", "main.cpp"), [0x89, 0x50, 0x4E, 0x47, 0xFF, 0xFE, 0xFF]);
        if (ChatUndoStore.Restore(dataRoot, "conversation-1", secondCopy, workspace, guards, writeArgs, out _) != UndoVerdict.NotText)
            throw new Exception("A binary at the recorded path was reverted with a text pre-image.");
        File.Delete(Path.Combine(workspace, "src", "main.cpp"));
        if (ChatUndoStore.Restore(dataRoot, "conversation-1", secondCopy, workspace, guards, writeArgs, out _) != UndoVerdict.TargetMissing)
            throw new Exception("A revert recreated a file the user deleted.");
        if (ChatUndoStore.Restore(dataRoot, "conversation-1", Path.Combine("src", secondCopy), workspace, guards, writeArgs, out _) != UndoVerdict.RefusedPath)
            throw new Exception("An undo name carrying a separator was resolved outside its conversation's directory.");
        if (ChatUndoStore.Restore(dataRoot, "conversation-1", secondCopy, toolEngineRoot, guards, writeArgs, out _) != UndoVerdict.RefusedPath)
            throw new Exception("A revert followed its recorded path into a protected engine root.");
        if (File.Exists(Path.Combine(toolEngineRoot, "src", "main.cpp")))
            throw new Exception("The refused revert wrote into the engine root anyway.");
        File.WriteAllText(Path.Combine(workspace, "src", "main.cpp"), "int main()\n{\n    return 1;\n}\n");
        // The button lives or dies by this field, so it has to survive the session file — and a call that never
        // wrote anything must come back without one.
        var undoStore = new ConversationStore(dataRoot);
        var undoSession = Conversation.Create("orcarouter");
        undoSession.Append(ChatTurn.User("改一下 main.cpp"));
        undoSession.Append(ChatTurn.FunctionCall("call-1", "file_write", writeArgs)with { UndoName = secondCopy });
        undoSession.Append(ChatTurn.FunctionCall("call-2", "read_file", """{"path":"src/main.cpp"}"""));
        undoStore.Save(undoSession);
        var reopened = undoStore.Load(undoSession.Id) ?? throw new Exception("The undo session did not reload.");
        if (reopened.Messages[1].UndoName != secondCopy)
            throw new Exception("A recorded undo copy did not survive the session file.");
        if (reopened.Messages[2].UndoName is not null)
            throw new Exception("A read-only call came back with a revert to offer.");
        File.WriteAllText(Path.Combine(dataRoot, "ai", "sessions", "legacy-no-undo.json"), """{"Id":"legacy-no-undo","Title":"legacy","ProviderId":"orcarouter","Messages":[{"Role":"assistant","Text":"","ToolCallId":"c1","ToolName":"file_write"}]}""");
        var legacyUndo = undoStore.Load("legacy-no-undo") ?? throw new Exception("A session file written before the undo stopped loading.");
        if (legacyUndo.Messages[0].UndoName is not null)
            throw new Exception("A write from before the undo existed invented a copy to revert to.");
        Console.WriteLine("PASS: the undo restores only the write its copy was made for, and refuses everything else by name.");
        for (var index = 0; index < ChatUndoStore.MaxFiles + 10; index++)
            ChatUndoStore.Store(dataRoot, "conversation-1", $"file-{index:D3}.txt", "content");
        if (Directory.GetFiles(undoRoot).Length > ChatUndoStore.MaxFiles)
            throw new Exception($"The undo directory grew past its bound ({Directory.GetFiles(undoRoot).Length} files).");
        if (ChatUndoStore.Store(null, "conversation-1", "a.txt", "x").Stored || ChatUndoStore.Store(dataRoot, "", "a.txt", "x").Stored)
            throw new Exception("An undo copy was written with no data root or no conversation.");
        Console.WriteLine("PASS: file_write edits once, keeps the pre-image, and leaves the file alone on every refusal.");
        // ── run_command: the header, redaction, both ends of a long output, and the idle timeout ──
        var echoed = await tools.RunCommand("echo key=SUPERSECRET");
        if (!echoed.Contains("cwd: ") || !echoed.Contains(workspace) || !echoed.Contains("exit: 0"))
            throw new Exception($"The command result did not name its shell, directory and exit code:{Environment.NewLine}{echoed}");
        if (echoed.Contains("SUPERSECRET", StringComparison.Ordinal) || !echoed.Contains("[REDACTED]", StringComparison.Ordinal))
            throw new Exception("A secret reached the transcript instead of being redacted.");
        if (!echoed.Contains("Hub's run log at", StringComparison.Ordinal) || !echoed.Contains(log.FilePath, StringComparison.Ordinal) || !echoed.Contains("the assistant cannot read", StringComparison.Ordinal))
            throw new Exception($"A truncated result did not name the log and who can open it:{Environment.NewLine}{echoed}");
        File.WriteAllText(Path.Combine(workspace, "src", "big.txt"), string.Concat(Enumerable.Range(1, 4000).Select(index => $"row-{index:D4} padding padding padding\n")));
        var verbose = await tools.RunCommand(OperatingSystem.IsWindows() ? "Get-Content src/big.txt" : "cat src/big.txt");
        if (!verbose.Contains("characters of output omitted") || !verbose.Contains("row-0001") || !verbose.Contains("row-4000"))
            throw new Exception($"A long output was not shaped to both ends:{Environment.NewLine}{verbose[..Math.Min(400, verbose.Length)]}");
        // The whole point of the idle timeout being about *silence*: a command that printed three lines and then hung
        // has said something, and the answer must carry those lines. "Without producing any output" about a build that
        // printed the error it stopped on sends the model to retry a command whose diagnosis it never got to read.
        // Three seconds, not one, so the shell's own startup is never what trips the timer before it can print.
        var stalled = await tools.RunCommand(OperatingSystem.IsWindows() ? "Write-Output 'failing at step 3'; Write-Output 'error C2065'; Start-Sleep -Seconds 20" : "echo 'failing at step 3'; echo 'error C2065'; sleep 20", 3);
        if (!stalled.Contains("killed after 3s", StringComparison.Ordinal))
            throw new Exception($"An idle command was not killed by its timeout:{Environment.NewLine}{stalled}");
        if (!stalled.Contains("error C2065", StringComparison.Ordinal) || !stalled.Contains("before going silent", StringComparison.Ordinal))
            throw new Exception($"A timeout threw away the output the command had already produced:{Environment.NewLine}{stalled}");
        if (stalled.Contains("without producing any output", StringComparison.Ordinal))
            throw new Exception("A stalled command was still reported as having produced nothing.");
        // The same stall, read as what it is when the command was a build. The old advice — "do not retry the same
        // command unchanged" — told a model that a changed flag had earned it another run, and the second compile is
        // the exact thing this rule exists to stop: killing the process tree does not reach the detached MSBuild nodes,
        // VBCSCompiler or the dotnet build server, so the first build may still be writing while the retry starts. The
        // fixture is a build that says one line and then goes quiet: `Remove-Item build` errors on the missing
        // directory (so the stall has output to keep, which is the other half of the cell) and grades as a cache break.
        var stalledBuild = await tools.RunCommand(OperatingSystem.IsWindows() ? "Remove-Item build -ErrorAction SilentlyContinue; Write-Output 'Configuring done'; Start-Sleep -Seconds 20" : "rm -rf build 2>/dev/null; echo 'Configuring done'; sleep 20", 3);
        if (!stalledBuild.Contains("This was a build", StringComparison.Ordinal) || !stalledBuild.Contains("ask how to proceed", StringComparison.Ordinal))
            throw new Exception($"A stalled build was given the ordinary timeout advice:{Environment.NewLine}{stalledBuild}");
        if (stalledBuild.Contains("Do not retry the same command unchanged", StringComparison.Ordinal))
            throw new Exception("A stalled build was still told to retry it changed, which is the side door this closes.");
        if (!stalledBuild.Contains("Configuring done", StringComparison.Ordinal))
            throw new Exception("The stalled build lost the line it printed before going silent.");
        // A build that exits non-zero can leave the tree half new, and nothing in the exit code says how far it got. The
        // note it gets is about the one thing that cannot be undone from here — clearing the directory — and deliberately
        // not about the retry: reading the compiler, editing the code and building again is the work, and a sentence
        // telling the model to go ask permission for that would be the over-asking the user ruled back.
        var failedBuild = await tools.RunCommand("dotnet build AxmolHub-NoSuch.csproj", 120);
        if (!failedBuild.Contains("exit: 1", StringComparison.Ordinal) || !failedBuild.Contains("The build failed", StringComparison.Ordinal) || !failedBuild.Contains("build again as usual", StringComparison.Ordinal) || failedBuild.Contains("ask the user before running another build", StringComparison.Ordinal))
            throw new Exception($"A failed build came back with the wrong advice:{Environment.NewLine}{failedBuild}");
        var failedRead = await tools.RunCommand(OperatingSystem.IsWindows() ? "Get-Content no-such-file.txt" : "cat no-such-file.txt", 30);
        if (failedRead.Contains("The build failed", StringComparison.Ordinal))
            throw new Exception("An ordinary failing read was lectured about build caches.");
        Console.WriteLine("PASS: a failed build is told to fix the code and build again, and to leave the directory " + "alone; nothing else gets a note about caches at all.");
        // ── a command that goes quiet while it is still running ──
        // The seam is the whole rule. With nobody to ask, a stall ends the process — every cell above this line is that
        // road and none of them moved. With somebody to ask, the process is handed over alive and what happens next is
        // their answer, so the cells below drive each of the four answers against a command that prints one line, goes
        // silent for long past its own timeout, and finishes on its own a few seconds later.
        var quietCommand = OperatingSystem.IsWindows() ? "Write-Output 'first line'; Start-Sleep -Seconds 4; Write-Output 'second line'" : "echo 'first line'; sleep 4; echo 'second line'";
        var stallAnswers = new Queue<StalledProcessFate>();
        var stallAsked = 0;
        var stallGateTaken = 0;
        var stallGateReleased = 0;
        var stallTools = new WorkspaceTools(new WorkspaceToolScope(workspace, guards, dataRoot, "stalled", [], log, // The one-change gate rides along on purpose: every road out of a stalled command — waited out, walked away
        // from, stopped, or finished on its own — has to hand it back, or Hub reports itself busy with a process
        // nobody is holding.
        TakeChangeGate: _ =>
        {
            stallGateTaken++;
            return new GateHandoff(() => stallGateReleased++);
        }, AskAboutStalledProcess: (stalled, _) =>
        {
            stallAsked++;
            var fate = stallAnswers.Dequeue();
            // The host's own road for a command that finishes while the question is open: wait for the exit, take
            // the question away, answer with the fact rather than a choice. Driven here so the timing under test is
            // the tool's, not the window's.
            return fate == StalledProcessFate.Exited ? stalled.Exited.ContinueWith(_ => StalledProcessFate.Exited) : Task.FromResult(fate);
        }));
        // A: nothing is ended, the wait is the answer, and the result is the whole command — including the line it
        // printed after it went quiet, which the kill-on-silence rule threw away along with the process.
        stallAnswers.Enqueue(StalledProcessFate.KeepWaiting);
        var waitedOut = await stallTools.RunCommand(quietCommand, 1);
        if (!waitedOut.Contains("exit: 0", StringComparison.Ordinal) || !waitedOut.Contains("second line", StringComparison.Ordinal) || !waitedOut.Contains("chose to keep waiting", StringComparison.Ordinal))
            throw new Exception($"Answer A did not wait the command out:{Environment.NewLine}{waitedOut}");
        // B: nothing is ended either, but Hub stops watching — and says so, including the part that bites: the change
        // gate goes with the watch, so the next build is no longer held behind a command nobody is tracking.
        stallAnswers.Enqueue(StalledProcessFate.LeaveItRunning);
        var walkedAway = await stallTools.RunCommand(quietCommand, 1);
        if (!walkedAway.Contains("first line", StringComparison.Ordinal) || !walkedAway.Contains("no longer watching", StringComparison.Ordinal) || !walkedAway.Contains("still running", StringComparison.Ordinal) || walkedAway.Contains("second line", StringComparison.Ordinal))
            throw new Exception($"Answer B did not hand the command back unsupervised:{Environment.NewLine}{walkedAway}");
        // C: the one answer that ends a process, and then runs the command again — once. A second stall asks again
        // rather than looping on its own, which the fixture proves by answering B the second time.
        stallAnswers.Enqueue(StalledProcessFate.KillAndRetry);
        stallAnswers.Enqueue(StalledProcessFate.LeaveItRunning);
        var askedBefore = stallAsked;
        var killedThenRun = await stallTools.RunCommand(quietCommand, 1);
        if (!killedThenRun.Contains("stop the silent command and run it again", StringComparison.Ordinal) || !killedThenRun.Contains("earlier run was ended first", StringComparison.Ordinal) || !killedThenRun.Contains("no longer watching", StringComparison.Ordinal) || stallAsked - askedBefore != 2)
            throw new Exception($"Answer C did not stop the first run and re-run it exactly once:{Environment.NewLine}{killedThenRun}");
        // The withdrawal: a command that finishes while the question is open is no longer a question. What the body
        // receives is the process's own answer — exit code and all output, including what came after the silence — and
        // no sentence may claim somebody chose to wait for it.
        stallAnswers.Enqueue(StalledProcessFate.Exited);
        var finishedAlone = await stallTools.RunCommand(quietCommand, 1);
        if (!finishedAlone.Contains("exit: 0", StringComparison.Ordinal) || !finishedAlone.Contains("second line", StringComparison.Ordinal) || !finishedAlone.Contains("finished on its own", StringComparison.Ordinal) || finishedAlone.Contains("chose to keep waiting", StringComparison.Ordinal))
            throw new Exception($"A command that finished while being asked about was not closed as itself:{Environment.NewLine}{finishedAlone}");
        // The gate on every one of those four roads: taken once per command — once even for C, whose retry is the same
        // call running again rather than a second command — and handed back when the call ends, or Hub reports itself
        // busy with a process nobody is holding.
        if (stallGateTaken != stallGateReleased || stallGateTaken != 4)
            throw new Exception($"A stalled command left the change gate standing (took {stallGateTaken}, released " + $"{stallGateReleased} across {stallAsked} questions).");
        Console.WriteLine("PASS: a quiet command is asked about instead of killed, and each of the four answers does " + "exactly what it says — including the one where the command answers for itself by finishing.");
        // A process that survives its own kill must not wedge the tool, so every wait after the kill is bounded. No
        // real process can be made unkillable on purpose, which is what the StopProcess seam is for: it reports "it
        // did not die" while the fixture sleeps twelve seconds by itself. Eight seconds is the bound that turns a
        // hang into a red assertion instead of a suite that never comes back — and the idle timeout is three seconds,
        // so the shell has certainly printed before it goes quiet. The directory is the temp folder rather than the
        // sandbox because a survivor keeps its working directory locked, and this group ends by deleting that tree;
        // the sandbox is the tool layer's business, and ProcessRunner has no say in it.
        var stallShell = CommandShells.ForCurrent();
        // The shell a host uses is not a function of the OS: CommandShells prefers PowerShell 7 wherever it is
        // installed, and bare `echo last words` prints one line in sh and two in pwsh. The survivor fixture reads that
        // line back, so the command has to speak whichever shell is actually going to run it.
        var powershellShell = stallShell.Executable.Contains("pwsh", StringComparison.OrdinalIgnoreCase) || stallShell.Executable.Contains("powershell", StringComparison.OrdinalIgnoreCase);
        var abandoning = new ProcessRunner(line => log.Write(line))
        {
            StopProcess = _ => false
        };
        var gaveUpAt = Environment.TickCount64;
        IdleTimeoutException? abandoned = null;
        try
        {
            await abandoning.RunAsync(stallShell.Executable, stallShell.ArgumentsFor(powershellShell ? "Write-Output 'last words'; Start-Sleep -Seconds 12" : "echo 'last words'; sleep 12"), Path.GetTempPath(), timeout: TimeSpan.FromSeconds(3));
        }
        catch (IdleTimeoutException stalledAgain)
        {
            abandoned = stalledAgain;
        }

        var waited = Environment.TickCount64 - gaveUpAt;
        if (abandoned is null or { SurvivedKill: false } || !abandoned.Output.Contains("last words", StringComparison.Ordinal))
            throw new Exception($"A stalled command did not come back as an idle timeout that says it survived, with " + $"its output (type={abandoned?.GetType().Name ?? "none"}, " + $"survived={abandoned?.SurvivedKill}, output=「{(abandoned?.Output ?? "").Replace(Environment.NewLine, " ⏎ ")}」).");
        if (waited > 8_000)
            throw new Exception($"A process that survived its kill held the tool for {waited} ms.");
        // CJK is where an encoding mismatch shows up first: PowerShell answers in the machine's OEM codepage while
        // the pipe is decoded as UTF-8, so a Chinese Windows turns 中文 into replacement characters.
        var cjk = await tools.RunCommand(powershellShell ? "Write-Output '中文测试'" : "printf '中文测试'");
        if (!cjk.Contains("中文测试", StringComparison.Ordinal) || cjk.Contains('\uFFFD'))
            throw new Exception($"A CJK line did not survive the shell:{Environment.NewLine}{cjk}");
        if (!(await homeless.RunCommand("echo hi")).Contains("no workspace directory", StringComparison.Ordinal))
            throw new Exception("run_command without a workspace did not say so.");
        Console.WriteLine("PASS: run_command names its sandbox, redacts secrets, keeps both ends, keeps what a stall " + "already printed, comes back from a process that survives its kill, and speaks UTF-8.");
        // ── run_command holds Hub's one-change-at-a-time gate ──
        // The assistant and the build button used to be mutually invisible: an AI `cmake --build` and a UI build could
        // both start against one output directory, each believing the other's busy signal was somebody else's business.
        // The tool now asks the host for the gate first, and a command that could not be given it is refused rather than
        // queued — a wait reads to the model as a hang, and a build that looks hung is what makes an agent start a second
        // one. The scope above passes no gate at all, which is the self-check road: commands simply are not serialised.
        var gateMarker = Path.Combine(workspace, "src", "gate-marker.txt").Replace('\\', '/');
        var gateTaken = 0;
        var gateReleased = 0;
        var refusedTools = new WorkspaceTools(new WorkspaceToolScope(workspace, guards, dataRoot, "gated", [], log, TakeChangeGate: _ => null));
        var gateRefused = await refusedTools.RunCommand($"echo marker > '{gateMarker}'");
        if (gateRefused != ToolApprovalResults.Busy || File.Exists(gateMarker))
            throw new Exception($"A command refused the gate either did not say so or ran anyway:{Environment.NewLine}{gateRefused}");
        if (!gateRefused.Contains("Do not retry", StringComparison.Ordinal))
            throw new Exception("The busy refusal did not tell the model what not to do, which is a retry loop waiting to happen.");
        var openTools = new WorkspaceTools(new WorkspaceToolScope(workspace, guards, dataRoot, "gated", [], log, TakeChangeGate: _ =>
        {
            gateTaken++;
            return new GateHandoff(() => gateReleased++);
        }));
        var gateRan = await openTools.RunCommand("echo gate-open");
        if (gateTaken != 1 || gateReleased != 1 || !gateRan.Contains("exit: 0", StringComparison.Ordinal))
            throw new Exception($"A command that got the gate did not run and hand it back (took {gateTaken}, released {gateReleased}).");
        // The road the person takes when they press the ✕ on the reply: the tool body throws rather than returning, so
        // a release that only happened on the success path would leave Hub reporting itself busy forever with nothing
        // running. A pre-cancelled token is exactly that road, and it throws before the process even starts.
        using var stoppedGate = new CancellationTokenSource();
        stoppedGate.Cancel();
        gateTaken = 0;
        gateReleased = 0;
        var threwOnStop = false;
        try
        {
            _ = await openTools.RunCommand("echo never", cancellationToken: stoppedGate.Token);
        }
        catch (OperationCanceledException)
        {
            threwOnStop = true;
        }

        if (!threwOnStop || gateTaken != 1 || gateReleased != 1)
            throw new Exception($"A stopped run left the gate held (took {gateTaken}, released {gateReleased}, threw {threwOnStop}).");
        // A git *read* is exempt, and this is the cell that keeps the exemption honest: the tier already says it asks
        // nothing, the model reads the repository constantly, and `git status` refreshes the index rather than a build
        // tree. With a host that hands out no gate at all, the read still reaches the shell.
        var gitGateAsked = 0;
        var gitTools = new WorkspaceTools(new WorkspaceToolScope(workspace, guards, dataRoot, "gated", [], log, TakeChangeGate: _ =>
        {
            gitGateAsked++;
            return null;
        }));
        var gitRead = await gitTools.RunCommand("git status");
        if (gitGateAsked != 0 || !gitRead.Contains("shell:", StringComparison.Ordinal) || gitRead.Contains("already running a change", StringComparison.Ordinal))
            throw new Exception($"A git read was treated as a change (gate asked {gitGateAsked} times):{Environment.NewLine}{gitRead}");
        Console.WriteLine("PASS: a changing command takes the gate, refuses out loud when it cannot, gives it back on every " + "road out including a stop, and a git read never asks for it.");
        // ── the directory classifier: whose directory a card is talking about ──
        // The tiers it used to decide went with the tool that moved the sandbox, but a grant card's own facts are still
        // read through it, so the rule is asserted here where no window is involved. The three answers answer one
        // question — has a person already agreed to this directory? — and a path this build cannot read falls to the
        // strictest of them rather than to a guess.
        var nestedTarget = Path.Combine(workspace, "deeper");
        var knownProject = Path.Combine(toolRoot, "registered-project");
        var elsewhere = Path.Combine(toolRoot, "somewhere-else");
        if (WorkspacePaths.ClassifyWorkspaceTarget(workspace, workspace, null, guards) != WorkspaceTarget.SameAsSandbox || WorkspacePaths.ClassifyWorkspaceTarget(nestedTarget, workspace, null, guards) != WorkspaceTarget.InsideSandbox || WorkspacePaths.ClassifyWorkspaceTarget(knownProject, workspace, [knownProject], guards) != WorkspaceTarget.RegisteredProject || WorkspacePaths.ClassifyWorkspaceTarget(elsewhere, workspace, [knownProject], guards) != WorkspaceTarget.Other // Spelling must not change the answer: a trailing separator and a `.`/`..` detour name the same directory
        // the session is already working in. Windows folds case for the same reason the grouping keys do.
        || WorkspacePaths.ClassifyWorkspaceTarget(workspace + Path.DirectorySeparatorChar, workspace, null, guards) != WorkspaceTarget.SameAsSandbox || WorkspacePaths.ClassifyWorkspaceTarget(Path.Combine(workspace, ".", "deeper", ".."), workspace, null, guards) != WorkspaceTarget.SameAsSandbox || WorkspacePaths.ClassifyWorkspaceTarget(workspace + "/deeper", workspace, null, guards) != WorkspaceTarget.InsideSandbox // The fold that decides "same directory" is the one the filesystem already makes on Windows, and a path
        // that spells its drive the other way is the case where a byte-for-byte compare reads two sandboxes where
        // the user sees one.
        || WorkspacePaths.ClassifyWorkspaceTarget(OperatingSystem.IsWindows() ? nestedTarget.ToUpperInvariant() : nestedTarget, workspace, null, guards) != WorkspaceTarget.InsideSandbox // A session with no sandbox yet has nothing to be inside of, so even a directory it will move to next is
        // judged only by whether somebody pointed Hub at it beforehand.
        || WorkspacePaths.ClassifyWorkspaceTarget(nestedTarget, null, [nestedTarget], guards) != WorkspaceTarget.RegisteredProject || WorkspacePaths.ClassifyWorkspaceTarget(nestedTarget, null, null, guards) != WorkspaceTarget.Other || WorkspacePaths.ClassifyWorkspaceTarget(toolEngineRoot, workspace, null, guards) != WorkspaceTarget.Unreadable || WorkspacePaths.ClassifyWorkspaceTarget(null, workspace, null, guards) != WorkspaceTarget.Unreadable || WorkspacePaths.ClassifyWorkspaceTarget("relative/path", workspace, null, guards) != WorkspaceTarget.Unreadable || WorkspacePaths.ClassifyWorkspaceTarget("   ", workspace, null, guards) != WorkspaceTarget.Unreadable)
            throw new Exception("The directory classifier answered with something other than who chose that directory.");
        Console.WriteLine("PASS: the directory classifier still answers by who chose that directory, and fails closed on " + "one it cannot read.");
        // ── granted directories: a session may hold more than its workspace, one card at a time ──
        // One sentence is the whole rule — a relative path is the workspace, an absolute path is graded against every
        // root the session holds — and the rest of this block is what follows from it: the refusal that names the way
        // out, the spelling every consumer keys on, the floor under the grant, and the pre-image that outlives it.
        var grantedRoot = Path.Combine(toolRoot, "granted-project");
        var outsiderRoot = Path.Combine(toolRoot, "outside-cwd");
        Directory.CreateDirectory(Path.Combine(grantedRoot, "src"));
        Directory.CreateDirectory(Path.Combine(grantedRoot, ".git"));
        Directory.CreateDirectory(outsiderRoot);
        var grantedFile = Path.Combine(grantedRoot, "src", "lib.cpp");
        File.WriteAllText(grantedFile, "int lib() { return 1; }\n");
        if (WorkspacePaths.ResolveRead(workspace, grantedFile, guards).Verdict != WorkspacePathVerdict.OutsideRoots || !WorkspacePaths.ResultFor(WorkspacePathVerdict.OutsideRoots, grantedFile).Contains("request_directory", StringComparison.Ordinal))
            throw new Exception("An absolute path outside the workspace was not refused as outside every root, or its " + "refusal did not name request_directory.");
        var grantBox = new WorkspaceSandbox(workspace, [grantedRoot]);
        var insideGrant = WorkspacePaths.ResolveRead(grantBox, grantedFile, guards);
        if (!insideGrant.IsAllowed || !insideGrant.Granted || insideGrant.Full != grantedFile || insideGrant.Root != grantedRoot)
            throw new Exception($"An absolute path inside a granted directory was not resolved as one ({insideGrant.Verdict}).");
        // The spelling every consumer keys on: a granted path is shown absolutely, and the workspace keeps the relative
        // spelling it had before grants existed. That line is what keeps an old transcript, an old pre-image and an old
        // diff row meaning what they meant.
        var insideWorkspace = WorkspacePaths.ResolveRead(grantBox, "src/main.cpp", guards);
        if (!insideWorkspace.IsAllowed || insideWorkspace.Granted || insideWorkspace.Display != Path.Combine("src", "main.cpp"))
            throw new Exception("A workspace-relative path stopped being relative once a directory was granted.");
        if (WorkspacePaths.ResolveRead(grantBox, Path.Combine(workspace, "src", "main.cpp"), guards).Display != Path.Combine("src", "main.cpp"))
            throw new Exception("An absolute path inside the workspace lost the relative spelling older records use.");
        // A relative path is the workspace and only the workspace: after a grant it must not start resolving against
        // the second root, or "src/lib.cpp" would name two different files.
        if (WorkspacePaths.ResolveRead(grantBox, "src/lib.cpp", guards).Verdict != WorkspacePathVerdict.NotAFile)
            throw new Exception("A relative path resolved against a granted directory.");
        // The write floor under every grant: a protected location inside a directory a person granted is still
        // refused to every writing verb, because the grant says "this folder" and the guard says "never these,
        // whatever anybody approved". What a card *does* open is the location it named, and that is the pair after
        // this one — reading on, writing still off, and a grant made above the location opening neither.
        if (WorkspacePaths.ResolveWrite(grantBox, Path.Combine(grantedRoot, ".git", "config"), guards).Verdict != WorkspacePathVerdict.ProtectedRoot || WorkspacePaths.ResolveWrite(new WorkspaceSandbox(workspace, [dataRoot]), Path.Combine(dataRoot, "state.json"), guards).Verdict != WorkspacePathVerdict.ProtectedRoot)
            throw new Exception("A grant on a directory above a protected location opened it.");
        // One card on the location itself, and the same place answers two ways. The anchor rule is what the last
        // clause pins: a grant *above* the location opens nothing in it, so pointing the assistant at its home
        // directory does not hand it Hub's records — the card has to name the place.
        var dataFile = Path.Combine(dataRoot, "state.json");
        File.WriteAllText(dataFile, "{\"one\":1}\n");
        var readOnlyBox = new WorkspaceSandbox(workspace, [], [dataRoot]);
        var aboveBox = new WorkspaceSandbox(workspace, [toolRoot], []);
        if (WorkspacePaths.ResolveRead(readOnlyBox, dataFile, guards).Verdict != WorkspacePathVerdict.Allowed
            || WorkspacePaths.ResolveDirectory(readOnlyBox, dataRoot, guards).Verdict != WorkspacePathVerdict.Allowed
            || WorkspacePaths.ResolveWrite(readOnlyBox, dataFile, guards).Verdict != WorkspacePathVerdict.ReadOnlyRoot
            || WorkspacePaths.VerifyCommandRoot(readOnlyBox, dataRoot, guards) != WorkspacePathVerdict.ReadOnlyRoot
            || WorkspacePaths.ResolveRead(aboveBox, dataFile, guards).Verdict != WorkspacePathVerdict.ProtectedRoot
            || WorkspacePaths.ResolveWrite(aboveBox, dataFile, guards).Verdict != WorkspacePathVerdict.ProtectedRoot)
            throw new Exception("A read-only grant did not read-only: the data directory was writable, or a grant above it opened it.");
        // A command's directory goes through the same resolution, so "this call stays inside a directory the person
        // allowed" and "this call is exempt from a card" are one expression rather than two that can drift apart.
        if (WorkspacePaths.VerifyCommandRoot(grantBox, grantedRoot, guards) != WorkspacePathVerdict.Allowed || WorkspacePaths.VerifyCommandRoot(grantBox, "", guards) != WorkspacePathVerdict.Allowed || WorkspacePaths.VerifyCommandRoot(grantBox, outsiderRoot, guards) != WorkspacePathVerdict.OutsideRoots || WorkspacePaths.VerifyCommandRoot(grantBox, dataRoot, guards) != WorkspacePathVerdict.ProtectedRoot)
            throw new Exception("A command's directory was not graded against every root the session holds.");
        // ── request_directory: the card is the mechanism, so the body's own answers are what it gives — and a
        // protected location is answered with a read-only grant rather than a refusal ──
        var protectedGrantAnswer = await tools.RequestDirectory(dataRoot);
        if (!protectedGrantAnswer.Contains("Granted read-only", StringComparison.Ordinal) || !protectedGrantAnswer.Contains("absolute path", StringComparison.Ordinal) || !WorkspacePaths.ResultFor(WorkspacePathVerdict.ProtectedRoot, dataFile).Contains("request_directory", StringComparison.Ordinal))
            throw new Exception($"A protected location was not granted read-only, or its refusal stopped naming the door:{Environment.NewLine}{protectedGrantAnswer}");
        if (!(await tools.RequestDirectory("relative/path")).Contains("not an absolute path", StringComparison.Ordinal) || !(await tools.RequestDirectory(Path.Combine(toolRoot, "no-such-folder"))).Contains("does not exist", StringComparison.Ordinal) || !(await tools.RequestDirectory(Path.Combine(workspace, "src"))).Contains("inside the session workspace", StringComparison.Ordinal) || !(await tools.RequestDirectory(toolRoot)).Contains("contains the session workspace", StringComparison.Ordinal))
            throw new Exception("request_directory accepted a relative, missing, internal or enclosing directory.");
        // The description is the only sentence the model reads before it decides whether asking is even allowed, and
        // the card *is* the permission: a model that waits to be authorized in prose parks the task forever, which is
        // exactly what one did while this line was missing.
        var grantMethod = typeof(WorkspaceTools).GetMethod(nameof(WorkspaceTools.RequestDirectory));
        var grantDescription = (Attribute.GetCustomAttribute(grantMethod!, typeof(System.ComponentModel.DescriptionAttribute)) as System.ComponentModel.DescriptionAttribute)?.Description ?? "";
        if (!grantDescription.Contains("Never ask the user for permission in prose first", StringComparison.Ordinal) || !grantDescription.Contains("the card it shows *is* the approval", StringComparison.Ordinal))
            throw new Exception("request_directory's description does not say the card is the permission.");
        // One ruling for both doors — the model's card and the workspace menu's own row — so the two cannot start
        // accepting directories the other refuses. The nesting pair needs directories of its own: every folder above a
        // grant inside toolRoot is also above the workspace there, and that question is answered first.
        static GrantVerdict GradeGrant(WorkspaceSandbox box, WorkspaceGuards g, string path) => WorkspacePaths.ClassifyGrant(box, g, path).Verdict;
        var grantParent = Path.Combine(root, "grant-parent");
        var grantLeaf = Path.Combine(grantParent, "leaf");
        Directory.CreateDirectory(grantLeaf);
        if (GradeGrant(WorkspaceSandbox.For(workspace), guards, "relative/path") != GrantVerdict.NotAbsolute || GradeGrant(WorkspaceSandbox.For(workspace), guards, "") != GrantVerdict.NoPath || GradeGrant(WorkspaceSandbox.For(workspace), guards, Path.Combine(toolRoot, "no-such-folder")) != GrantVerdict.Missing || GradeGrant(WorkspaceSandbox.For(workspace), guards, dataRoot) != GrantVerdict.GrantedReadOnly || GradeGrant(WorkspaceSandbox.For(workspace), guards, Path.Combine(grantedRoot, ".git")) != GrantVerdict.GrantedReadOnly || GradeGrant(new WorkspaceSandbox(workspace, [], [dataRoot]), guards, dataRoot) != GrantVerdict.AlreadyGranted || GradeGrant(WorkspaceSandbox.For(workspace), guards, workspace) != GrantVerdict.SameAsWorkspace || GradeGrant(WorkspaceSandbox.For(workspace), guards, Path.Combine(workspace, "src")) != GrantVerdict.InsideWorkspace || GradeGrant(WorkspaceSandbox.For(workspace), guards, toolRoot) != GrantVerdict.ContainsWorkspace || GradeGrant(WorkspaceSandbox.For(workspace), guards, outsiderRoot) != GrantVerdict.Granted || GradeGrant(new WorkspaceSandbox(workspace, [grantedRoot]), guards, grantedRoot) != GrantVerdict.AlreadyGranted || GradeGrant(new WorkspaceSandbox(workspace, [grantedRoot]), guards, Path.Combine(grantedRoot, "src")) != GrantVerdict.InsideGrant || GradeGrant(new WorkspaceSandbox(outsiderRoot, [grantLeaf]), guards, grantParent) != GrantVerdict.ContainsGrant)
            throw new Exception("ClassifyGrant disagreed with the answers request_directory gives.");
        var holdingTools = new WorkspaceTools(new WorkspaceToolScope(workspace, guards, dataRoot, "grant-ask", [], log) { Sandbox = grantBox });
        if (!(await holdingTools.RequestDirectory(grantedRoot)).Contains("already granted", StringComparison.Ordinal) || !(await holdingTools.RequestDirectory(Path.Combine(grantedRoot, "src"))).Contains("already inside", StringComparison.Ordinal))
            throw new Exception("request_directory asked again for a directory the session already holds.");
        string? askedGrant = null;
        var freshGrant = new WorkspaceTools(new WorkspaceToolScope(workspace, guards, dataRoot, "grant-fresh", [], log, ApplyGrantedRoot: path =>
        {
            askedGrant = path;
            return Task.FromResult($"Granted: {path}.");
        }));
        var grantedAnswer = await freshGrant.RequestDirectory(grantedRoot);
        if (askedGrant != grantedRoot || !grantedAnswer.Contains($"Granted: {grantedRoot}", StringComparison.Ordinal))
            throw new Exception($"A directory nobody holds was not handed to the caller to persist:{Environment.NewLine}" + grantedAnswer);
        // ── a write in a granted directory is a write: same pre-image, same revert, and the revert dies with the grant ──
        var grantId = "grant-undo";
        var grantTools = new WorkspaceTools(new WorkspaceToolScope(workspace, guards, dataRoot, grantId, [], log) { Sandbox = grantBox });
        var grantWrite = grantTools.FileWrite(grantedFile, "return 1", "return 2");
        if (!grantWrite.StartsWith("Edited ", StringComparison.Ordinal) || !grantWrite.Contains(grantedFile, StringComparison.Ordinal) || WorkspaceTools.ReadAll(grantedFile)is not { } grantText || !grantText.Contains("return 2"))
            throw new Exception($"A write in a granted directory did not land:{Environment.NewLine}{grantWrite}");
        var grantChange = ChatChangeStore.IdFromResult(grantWrite);
        if (grantChange is null || ChatChangeStore.Restore(dataRoot, grantId, grantChange, grantBox, guards, out _, out _) != UndoVerdict.Reverted || WorkspaceTools.ReadAll(grantedFile)is not { } grantReverted || grantReverted.Contains("return 2"))
            throw new Exception("A write in a granted directory kept no pre-image, or its revert did not go back.");
        var revokedFile = Path.Combine(grantedRoot, "src", "gone.cpp");
        File.WriteAllText(revokedFile, "one\n");
        var revokedWrite = grantTools.FileWrite(revokedFile, "one", "two");
        var revokedChange = ChatChangeStore.IdFromResult(revokedWrite);
        if (revokedChange is null || ChatChangeStore.Restore(dataRoot, grantId, revokedChange, WorkspaceSandbox.For(workspace), guards, out _, out _) != UndoVerdict.RefusedPath)
            throw new Exception("A revert of a write in a directory no longer granted walked the recorded path anyway.");
        // ── a command can be pointed at a granted directory, and the header says where it actually ran ──
        var grantRun = await grantTools.RunCommand("echo mark > cwd-marker.txt", cwd: grantedRoot);
        if (!grantRun.Contains($"cwd: {Path.GetFullPath(grantedRoot)}", StringComparison.Ordinal) || !File.Exists(Path.Combine(grantedRoot, "cwd-marker.txt")))
            throw new Exception($"A command aimed at a granted directory did not run there:{Environment.NewLine}{grantRun}");
        var refusedRun = await grantTools.RunCommand("echo mark > cwd-marker.txt", cwd: outsiderRoot);
        if (!refusedRun.Contains("outside every directory", StringComparison.Ordinal) || File.Exists(Path.Combine(outsiderRoot, "cwd-marker.txt")))
            throw new Exception($"A command aimed outside every root ran anyway:{Environment.NewLine}{refusedRun}");
        // The line the decision is made on: the directory, and what a yes would allow — read and write, this session.
        var cardScope = new WorkspaceToolScope(workspace, guards, dataRoot, "grant-card", [], log);
        var grantCard = ToolPreviews.PreviewFor("request_directory", JsonSerializer.Serialize(new { path = grantedRoot }), cardScope);
        if (!grantCard.StartsWith("request_directory · ", StringComparison.Ordinal) || !grantCard.Contains("exists", StringComparison.Ordinal) || !grantCard.Contains("read and write", StringComparison.Ordinal) || !grantCard.Contains("this session only", StringComparison.Ordinal))
            throw new Exception($"A grant card did not say what approving it allows:{Environment.NewLine}{grantCard}");
        // A protected directory is what a person can now say yes to, so its card has to say both halves: which of
        // the three places it is — "Hub's own data" is the part the path does not tell them — and that the yes is a
        // reading one. "read and write" on this card would be the sentence that makes the next refusal look wrong.
        var protectedCard = ToolPreviews.PreviewFor("request_directory", JsonSerializer.Serialize(new { path = dataRoot }), cardScope);
        if (!protectedCard.Contains("protected location", StringComparison.Ordinal)
            || !protectedCard.Contains("Hub's own data", StringComparison.Ordinal)
            || !protectedCard.Contains("read only", StringComparison.Ordinal)
            || protectedCard.Contains("read and write", StringComparison.Ordinal))
            throw new Exception($"A protected directory's card did not say what approving it allows:{Environment.NewLine}{protectedCard}");
        if (!ToolPreviews.PreviewFor("run_command", JsonSerializer.Serialize(new { command = "ls", cwd = grantedRoot }), new WorkspaceToolScope(workspace, guards, dataRoot, "grant-card", [], log) { Sandbox = grantBox }).Contains(Path.GetFullPath(grantedRoot), StringComparison.Ordinal))
            throw new Exception("A command card did not name the directory the command would run in.");
        // ── the session file is user-editable data, so its grants come back through the same rules ──
        var grantStoreRoot = Path.Combine(toolRoot, "grant-data");
        var grantStore = new ConversationStore(grantStoreRoot);
        var grantSession = Conversation.Create("provider");
        // The store's own data root is written into the *writable* half on purpose: a session file is user-editable
        // data, so a protected location found there has to be demoted to the read-only half rather than read as a
        // write grant — and rather than dropped, which would lose the reading the card gave.
        grantSession.GrantedRoots = [grantedRoot, grantedRoot, "", "   ", grantStoreRoot, Path.Combine(toolRoot, "gone")];
        grantStore.Save(grantSession);
        var reloaded = grantStore.Load(grantSession.Id);
        var reloadedGrants = reloaded?.GrantedRoots;
        var reloadedReadOnly = reloaded?.ReadOnlyRoots;
        if (reloadedGrants is null || reloadedGrants.Count != 1 || !string.Equals(reloadedGrants[0], grantedRoot, StringComparison.Ordinal))
            throw new Exception($"A session file's granted directories were not normalized ({reloadedGrants?.Count} kept); " + "a duplicate, a blank and a missing folder all survive this road.");
        if (reloadedReadOnly is null || reloadedReadOnly.Count != 1 || !string.Equals(reloadedReadOnly[0], grantStoreRoot, StringComparison.Ordinal))
            throw new Exception($"A protected location in a session file was not demoted to the read-only side ({reloadedReadOnly?.Count} kept).");
        // …and the cap is the other half of the same rule: a loop that keeps asking cannot grow the file without bound.
        var manyGrants = WorkspacePaths.NormalizeRoots(Enumerable.Range(0, WorkspacePaths.MaxGrantedRoots + 4).Select(index =>
        {
            var folder = Path.Combine(toolRoot, $"grant-{index}");
            Directory.CreateDirectory(folder);
            return folder;
        }));
        if (manyGrants.Count != WorkspacePaths.MaxGrantedRoots)
            throw new Exception($"The granted-directory list grew past its cap ({manyGrants.Count}).");
        // The dedupe key folds case on the host that does, so one directory spelled two ways is one revoke button.
        if (OperatingSystem.IsWindows() && WorkspacePaths.NormalizeRoots([grantedRoot, grantedRoot.ToUpperInvariant()]).Count != 1)
            throw new Exception("One granted directory spelled two ways survived normalization.");
        Console.WriteLine("PASS: a granted directory is read and written beside the workspace, its refusal names " + "request_directory, and the list is bounded, deduped and read back through the same rules.");
        // ── memory through the same tool surface ──
        if (!tools.MemoryWrite("project", "build-rules.md", "构建走 1kiss.ps1。", "构建约定", "改构建前先看", "project").Contains("Saved topics/build-rules.md"))
            throw new Exception("A project memory write was refused.");
        if (tools.MemoryRead("project", "build-rules.md") != "构建走 1kiss.ps1。")
            throw new Exception("Reading back a memory topic did not return its body alone.");
        if (!tools.MemoryWrite("project", "build-rules.md", "补充一行。", "", "", "", "append").Contains("Saved") || tools.MemoryRead("project", "build-rules.md") != "构建走 1kiss.ps1。\n\n补充一行。")
            throw new Exception("Appending to a memory topic did not keep what was there.");
        if (!tools.MemoryRead("project", "MEMORY.md").Contains("构建约定"))
            throw new Exception("The shared project index was not readable through the tool.");
        // One directory, two spellings. `project` stays accepted because sessions on disk carry it inside their stored
        // arguments, and both have to name the same file: a body reading the scope differently from the gate would send
        // workspace notes to global memory while the card said it was a note.
        if (!tools.MemoryWrite("workspace", "build-rules.md", "构建用 MSBuild。", "构建约定", "改构建前先看", "note", "replace").Contains("Saved topics/build-rules.md") || tools.MemoryRead("project", "build-rules.md") != "构建用 MSBuild。" || tools.MemoryRead("WORKSPACE", "build-rules.md") != "构建用 MSBuild。")
            throw new Exception("The workspace and project spellings of the scope did not name the same memory.");
        if (!tools.MemoryWrite("global", "prefers-chinese.md", "回复用中文。", "语言偏好", "always", "user").Contains("Saved"))
            throw new Exception("A global memory write was refused with no workspace bound.");
        if (!tools.MemoryRead("neither", "x.md").Contains("neither workspace nor global") || !tools.MemoryWrite("project", "../evil.md", "x").Contains("not a memory topic name") || !tools.MemoryWrite("project", "agents.md", "x").Contains("not a memory topic name") || !homeless.MemoryWrite("project", "x.md", "内容").Contains("no workspace directory", StringComparison.Ordinal))
            throw new Exception("The memory tools accepted a bad scope, a charter name, a traversing name or a missing workspace.");
        Console.WriteLine("PASS: the memory tools write both scopes, append, and refuse a bad name or scope.");
        // The scope spelling list is one function now, read by the gate and by the body. Its table is asserted here
        // because the pair above can pass while one reader still falls back the other way — an unrecognised scope has
        // to answer null and let each caller keep its own refusal, not silently pick a side.
        if (MemoryScopes.Of("workspace") != MemoryScope.Workspace || MemoryScopes.Of("project") != MemoryScope.Workspace || MemoryScopes.Of("PROJECT") != MemoryScope.Workspace || MemoryScopes.Of(" global ") != MemoryScope.Global || MemoryScopes.Of(null)is not null || MemoryScopes.Of("team")is not null)
            throw new Exception("The scope spelling list is not one table: workspace/project alias, global, and " + "everything else unrecognised.");
        Console.WriteLine("PASS: one scope reading — workspace with project as its permanent alias, global, and null for " + "anything else, which is what lets the gate ask while the body refuses.");
        // ── Read-only look-around: the same sandbox, but through no shell and needing no approval ──
        // search_text / list_directory / find_files exist so that looking around costs the model nothing. Without
        // them every `ls` and every grep is a run_command — and run_command asks for a person's approval in the
        // ask and auto tiers, which turns exploration into a stack of cards.
        var probe = Path.Combine(workspace, "probe");
        Directory.CreateDirectory(Path.Combine(probe, "deep", "deeper"));
        Directory.CreateDirectory(Path.Combine(probe, "build"));
        File.WriteAllText(Path.Combine(probe, "app.cpp"), "int main()\n{\n    return App::go();\n}\n");
        File.WriteAllText(Path.Combine(probe, "deep", "note.md"), "see App::go in app.cpp\n");
        File.WriteAllText(Path.Combine(probe, "deep", "deeper", "other.md"), "two directories down\n");
        File.WriteAllText(Path.Combine(probe, "build", "app.cpp"), "App::go in a build directory\n");
        File.WriteAllText(Path.Combine(probe, "raw.bin"), "App::go is not text\n");
        // An .svg is source, not only a picture: it is XML, and its markup is the one form of it a model can be
        // handed. A folder of vector art the look-around tools cannot read is a blind spot in a workspace.
        File.WriteAllText(Path.Combine(probe, "icon.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\">\n  <path id=\"probe-svg-marker\"/>\n</svg>\n");
        var search = tools.SearchText("App::go", path: "probe");
        if (!search.Contains("probe/app.cpp:3:") || !search.Contains("probe/deep/note.md:1:"))
            throw new Exception($"search_text missed a line or numbered it wrong:{Environment.NewLine}{search}");
        if (search.Contains("build/app.cpp", StringComparison.Ordinal))
            throw new Exception("search_text walked into a build directory.");
        if (search.Contains("raw.bin", StringComparison.Ordinal))
            throw new Exception("search_text opened a file the reader would refuse as binary.");
        // The other side of the same rule: an .svg is XML, so it is read where a .bin is skipped. Its markup is the
        // only form of it a model can be handed, and a workspace's vector art is source the assistant has to reach.
        var vector = tools.SearchText("probe-svg-marker", path: "probe");
        if (!vector.Contains("probe/icon.svg:2:"))
            throw new Exception($"search_text skipped an .svg, which is a text source file:{Environment.NewLine}{vector}");
        if (!search.Contains("under 'probe/'") || !search.Contains("2 match(es) in 2 file(s)"))
            throw new Exception($"search_text did not report its scope or its counts:{Environment.NewLine}{search}");
        if (!tools.SearchText("app::GO", path: "probe").Contains("No match", StringComparison.Ordinal) || !tools.SearchText("app::GO", path: "probe", ignore_case: true).Contains("probe/app.cpp:3:"))
            throw new Exception("ignore_case did not do what its name says.");
        var cppOnly = tools.SearchText("App::go", glob: "*.cpp", path: "probe");
        if (!cppOnly.Contains("probe/app.cpp:3:") || cppOnly.Contains("note.md:", StringComparison.Ordinal))
            throw new Exception($"A name glob did not filter by name:{Environment.NewLine}{cppOnly}");
        var deepOnly = tools.SearchText("App::go", glob: "deep/*.md", path: "probe");
        if (!deepOnly.Contains("probe/deep/note.md:1:") || deepOnly.Contains("probe/app.cpp:", StringComparison.Ordinal))
            throw new Exception($"A path glob did not anchor to that directory:{Environment.NewLine}{deepOnly}");
        var crossed = tools.SearchText("App::go", glob: "**/*.md", path: "probe");
        if (!crossed.Contains("probe/deep/note.md:1:"))
            throw new Exception($"** did not cross a directory:{Environment.NewLine}{crossed}");
        var capped = tools.SearchText("App::go", path: "probe", max_matches: 1);
        if (!capped.Contains("cut at 1 matches"))
            throw new Exception($"A capped search did not say it was cut:{Environment.NewLine}{capped}");
        if (!tools.SearchText("(", path: "probe").Contains("does not compile", StringComparison.Ordinal) || !tools.SearchText("", path: "probe").Contains("needs a pattern", StringComparison.Ordinal))
            throw new Exception("search_text took a pattern it cannot compile, or none at all, without refusing.");
        foreach (var refusal in new[]
        {
            homeless.SearchText("x"),
            tools.SearchText("x", path: "../outside"),
            homeless.ListDirectory(),
            tools.ListDirectory(path: "probe/app.cpp"),
            homeless.FindFiles("*.cpp"),
            tools.FindFiles("*.cpp", path: "../outside"),
        }

        )
            if (!refusal.Contains("Refused", StringComparison.Ordinal) && !refusal.Contains("no workspace directory", StringComparison.Ordinal))
                throw new Exception($"A read-only lookup did not refuse in words the model can act on:{Environment.NewLine}{refusal}");
        var listed = tools.ListDirectory("probe");
        if (!listed.Contains("probe/deep/") || !listed.Contains("probe/app.cpp") || listed.Contains("note.md", StringComparison.Ordinal))
            throw new Exception($"list_directory at depth 1 leaked a deeper file, missed a directory, or printed a path " + $"read_file cannot take:{Environment.NewLine}{listed}");
        if (!listed.Contains("1 build/vendored dir(s) not entered"))
            throw new Exception("list_directory entered a build directory silently.");
        if (!tools.ListDirectory("probe", 2).Contains("probe/deep/note.md", StringComparison.Ordinal))
            throw new Exception("list_directory at depth 2 did not go one level down with a usable path.");
        var names = tools.FindFiles("*.cpp", "probe");
        if (!names.Contains("probe/app.cpp", StringComparison.Ordinal) || names.Contains("build/app.cpp", StringComparison.Ordinal))
            throw new Exception($"find_files listed a build copy or missed the real file:{Environment.NewLine}{names}");
        var markdown = tools.FindFiles("**/*.md", "probe");
        if (!markdown.Contains("probe/deep/note.md", StringComparison.Ordinal) || !markdown.Contains("probe/deep/deeper/other.md", StringComparison.Ordinal))
            throw new Exception($"** did not cross more than one directory:{Environment.NewLine}{markdown}");
        if (!tools.FindFiles("nothing-here-*.xyz", "probe").Contains("No file matched", StringComparison.Ordinal))
            throw new Exception("find_files found nothing and said something else.");
        // A log is text whatever it is called. The list a search uses is its own and not the write allowlist — "0
        // files searched" over a directory of hub-*.log was the answer to a real question about a real directory,
        // and it is the shape of the request that produced the read-only grant in the first place.
        File.WriteAllText(Path.Combine(probe, "hub-1.log"), "[1] probe-log-marker from the log\n");
        File.WriteAllText(Path.Combine(probe, "hub-2.log"), "[2] nothing here\n");
        var logs = tools.SearchText("probe-log-marker", path: "probe");
        if (!logs.Contains("probe/hub-1.log:1:") || logs.Contains("hub-2.log:", StringComparison.Ordinal))
            throw new Exception($"search_text did not read a .log file, or read one with no match:{Environment.NewLine}{logs}");
        // One file as the scope: how one log is grepped, and the only way in when the file is past any budget.
        var oneLog = tools.SearchText("probe-log-marker", path: Path.Combine(probe, "hub-1.log"));
        if (!oneLog.Contains("probe/hub-1.log:1:") || !oneLog.Contains("in probe/hub-1.log", StringComparison.Ordinal))
            throw new Exception($"search_text refused a single file, or searched something else:{Environment.NewLine}{oneLog}");
        // …and the write allowlist did not move with it: reading a log is not being allowed to edit one.
        var logWrite = tools.FileWrite(Path.Combine(probe, "hub-1.log"), "probe-log-marker", "x");
        if (!logWrite.Contains("not a text file", StringComparison.Ordinal))
            throw new Exception($"A .log became writable when the search learned to read it:{Environment.NewLine}{logWrite}");
        // A file past the per-file cap is named rather than skipped in silence: an answer that quietly ignored the
        // sixteen-megabyte log is an answer whose "no match" the model cannot act on.
        File.WriteAllText(Path.Combine(probe, "huge.log"),
            new string('y', (int)WorkspaceTools.MaxSearchFileBytes + 16));
        var skippedBig = tools.SearchText("probe-log-marker", path: "probe");
        if (!skippedBig.Contains("bigger than 8 MiB", StringComparison.Ordinal)
            || !skippedBig.Contains("probe/hub-1.log:1:", StringComparison.Ordinal))
            throw new Exception($"A file past the per-file cap was skipped in silence:{Environment.NewLine}{skippedBig}");
        Console.WriteLine("PASS: search_text, list_directory and find_files look around without a shell and refuse in sentences.");
        // ── workspace_map: what a directory IS, before anything is read out of it ──
        // The tools above answer questions about contents. None of them answers "what is this tree", and a model that
        // has to grep its way there spends the whole turn doing it. What is asserted here is the shape it reports, the
        // two claims it must not confuse (a plain folder is not a defect; an unreadable repository is not "no
        // repository"), and that it counts source rather than build output.
        var mapTree = Path.Combine(workspace, "map-tree");
        Directory.CreateDirectory(Path.Combine(mapTree, "src", "Core"));
        Directory.CreateDirectory(Path.Combine(mapTree, "src", "App"));
        Directory.CreateDirectory(Path.Combine(mapTree, "bin", "Debug"));
        File.WriteAllText(Path.Combine(mapTree, "Tree.sln"), "Microsoft Visual Studio Solution File\n");
        File.WriteAllText(Path.Combine(mapTree, "src", "Core", "Core.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(mapTree, "src", "App", "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0-windows</TargetFramework>" + "</PropertyGroup><ItemGroup><ProjectReference Include=\"..\\Core\\Core.csproj\" /></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(mapTree, "src", "App", "Program.cs"), "internal static class Program { }\n");
        File.WriteAllText(Path.Combine(mapTree, "bin", "Debug", "App.dll"), "not source at all\n");
        var mapped = await tools.MapWorkspace("map-tree");
        // The solution first: it is the answer to "what is this tree", and the projects below it are the answer after.
        if (!mapped.Contains("Tree.sln [solution]", StringComparison.Ordinal) || !mapped.Contains("App.csproj [csproj]", StringComparison.Ordinal) || !mapped.Contains("net8.0-windows", StringComparison.Ordinal) || !mapped.Contains("refs: ..\\Core\\Core.csproj", StringComparison.Ordinal) || !mapped.Contains("Core.csproj [csproj]", StringComparison.Ordinal))
            throw new Exception($"A .NET tree did not map to its units and references:{Environment.NewLine}{mapped}");
        // Build output is not part of what a person wrote. Pinned three ways because the obvious needle is vacuous: a
        // dll never becomes a build unit, so "App.dll is not named" would pass with the skip list deleted. What a walk
        // into bin\ actually changes is the file count, the honest sentence about what was left out, and a directory
        // row that would rank a compiled artifact above the manifests it came from. The fixture lives under an ignored
        // path, so this is the walk branch; git's branch is pinned on a real repository further down.
        if (!mapped.Contains("Files: 4 files", StringComparison.Ordinal))
            throw new Exception($"The map counted build output among the files:{Environment.NewLine}{mapped}");
        if (!mapped.Contains("left out", StringComparison.Ordinal))
            throw new Exception($"The map did not say which source bounded its file list:{Environment.NewLine}{mapped}");
        if (mapped.Contains("\n  bin:", StringComparison.Ordinal))
            throw new Exception($"bin/ was reported as a directory of the tree:{Environment.NewLine}{mapped}");
        if (!mapped.Contains("Program.cs", StringComparison.Ordinal))
            throw new Exception($"The map missed the entry point it can name:{Environment.NewLine}{mapped}");
        // Which side of the git line is correct depends on where this checkout lives — anything inside the repository
        // really is a repository — so the expectation is asked of git rather than assumed. That is the only way this
        // cell says something on both a developer's machine and a runner's.
        var gitSaysRepo = CommandShells.Resolve("git")is { } gitTool && (await new ProcessRunner(_ =>
        {
        }).RunAsync(gitTool, ["rev-parse", "--is-inside-work-tree"], Path.GetFullPath(mapTree), timeout: TimeSpan.FromSeconds(20))).ExitCode == 0;
        if (gitSaysRepo != mapped.Contains("Git: a repository", StringComparison.Ordinal))
            throw new Exception($"The map's git line disagreed with git itself (git answered inside-a-work-tree: {gitSaysRepo})." + $"{Environment.NewLine}{mapped}");
        // The other source, on a repository this check builds rather than the one its own scratch directory happens to
        // sit inside. Two facts only git's list can give: what a repository ignores stays out even though it is on
        // disk, and what is new since the last commit stays in even though no commit has heard of it. A tracked file
        // that has been deleted is the one path both sources must not count, and has to say why it is missing.
        if (CommandShells.Resolve("git")is { } listGit)
        {
            // Built in the OS temp directory, not under this group's own scratch root: git's object files are
            // read-only, and a red run that leaves a repository behind makes the *group's* cleanup throw before anyone
            // reads the assertion that failed. A directory outside the workspace has to be mapped through its own
            // scope, because a tool may not climb out of the workspace it was given.
            var repoTree = Path.Combine(Path.GetTempPath(), $"hub-map-repo-{Guid.NewGuid():N}");
            static void WipeRepository(string directory)
            {
                foreach (var leftover in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                    File.SetAttributes(leftover, FileAttributes.Normal);
                Directory.Delete(directory, recursive: true);
            }

            try
            {
                Directory.CreateDirectory(Path.Combine(repoTree, "src", "Util"));
                Directory.CreateDirectory(Path.Combine(repoTree, "bin", "Debug"));
                File.WriteAllText(Path.Combine(repoTree, ".gitignore"), "bin/\n");
                File.WriteAllText(Path.Combine(repoTree, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
                File.WriteAllText(Path.Combine(repoTree, "src", "Util", "Util.csproj"), "<Project/>\n");
                File.WriteAllText(Path.Combine(repoTree, "Gone.csproj"), "<Project/>\n");
                File.WriteAllText(Path.Combine(repoTree, "bin", "Debug", "App.dll"), "ignored, and on disk\n");
                var repoRunner = new ProcessRunner(_ =>
                {
                });
                async Task RepoGit(params string[] arguments)
                {
                    var answer = await repoRunner.RunAsync(listGit, ["-c", "core.quotepath=false", "--no-optional-locks", "--no-pager", ..arguments], repoTree, timeout: TimeSpan.FromSeconds(20));
                    if (answer.ExitCode != 0)
                        throw new Exception($"git {string.Join(" ", arguments)} failed in the map fixture: {answer.Error}");
                }

                await RepoGit("init", "-q");
                await RepoGit("add", "-A");
                // Named identities, as the repository fixture in the workspace group does: a check that commits must
                // not depend on whose laptop it ran on.
                await RepoGit("-c", "user.name=hub-check", "-c", "user.email=hub@example.invalid", "commit", "-q", "-m", "fixture: baseline");
                File.Delete(Path.Combine(repoTree, "Gone.csproj"));
                File.WriteAllText(Path.Combine(repoTree, "Program.cs"), "// written after the commit, never committed\n");
                var repoMap = await new WorkspaceTools(new WorkspaceToolScope(repoTree, guards, dataRoot, "map-repo", [], null, null)).MapWorkspace("");
                // Four: the two committed projects, the ignore file, and the new entry point. Not three, which is what
                // a list of tracked files alone would give, and not five, which is what a walk of the disk would give.
                if (!repoMap.Contains("git's list", StringComparison.Ordinal))
                    throw new Exception($"A repository's own file list was not the map's source:{Environment.NewLine}{repoMap}");
                if (!repoMap.Contains("Files: 4 files", StringComparison.Ordinal))
                    throw new Exception($"A repository map counted the wrong files:{Environment.NewLine}{repoMap}");
                if (!repoMap.Contains("Probable entry points: Program.cs", StringComparison.Ordinal))
                    throw new Exception($"A file newer than the last commit was not on the map:{Environment.NewLine}{repoMap}");
                if (!repoMap.Contains("Gone.csproj", StringComparison.Ordinal) || !repoMap.Contains("1 path(s) git lists but does not have on disk", StringComparison.Ordinal))
                    throw new Exception($"A deleted-but-tracked project was counted, or its absence went unexplained:" + $"{Environment.NewLine}{repoMap}");
                if (repoMap.Contains("\n  bin:", StringComparison.Ordinal))
                    throw new Exception($"An ignored bin/ still reached a git-sourced map:{Environment.NewLine}{repoMap}");
            }
            finally
            {
                if (Directory.Exists(repoTree))
                    WipeRepository(repoTree);
            }
        }
        else
        {
            Console.WriteLine("SKIP: no git on PATH — the map's git-sourced branch cannot be shown on this host.");
        }

        // One text-level rule and no more: references are what a manifest states about itself. An Axmol game project is
        // a CMake tree like any other, which is why the generic scanner needs no engine-shaped branch.
        var mapGame = Path.Combine(workspace, "map-game");
        Directory.CreateDirectory(Path.Combine(mapGame, "Classes"));
        File.WriteAllText(Path.Combine(mapGame, "CMakeLists.txt"), "cmake_minimum_required(VERSION 3.4)\nproject(MyGame)\nadd_subdirectory(Classes)\n");
        File.WriteAllText(Path.Combine(mapGame, "Classes", "AppDelegate.cpp"), "// entry\n");
        var gameMap = await tools.MapWorkspace("map-game");
        if (!gameMap.Contains("[cmake]", StringComparison.Ordinal) || !gameMap.Contains("MyGame", StringComparison.Ordinal) || !gameMap.Contains("refs: Classes", StringComparison.Ordinal))
            throw new Exception($"A CMake tree did not map its project or its subdirectory:{Environment.NewLine}{gameMap}");
        // The Hub-specific attribute, said as an attribute of the same directory. And a workspace that is not a
        // registered project gets no such line: an ordinary folder is a complete answer, not a missing one.
        var attributed = await new WorkspaceTools(new WorkspaceToolScope(workspace, guards, dataRoot, "map-project", [], null, null, Projects: [new WorkspaceProject(mapTree, "MapTree", "3.0.0", "windows-x64", "Debug")])).MapWorkspace("map-tree");
        if (!attributed.Contains("Also a Hub project: MapTree, engine 3.0.0, target windows-x64/Debug", StringComparison.Ordinal))
            throw new Exception($"A registered project was not stated as an attribute of its workspace:{Environment.NewLine}{attributed}");
        var unattributed = await new WorkspaceTools(new WorkspaceToolScope(workspace, guards, dataRoot, "map-other", [], null, null, Projects: [new WorkspaceProject(mapTree, "MapTree", "3.0.0", "windows-x64", "Debug")])).MapWorkspace("map-game");
        if (unattributed.Contains("Also a Hub project", StringComparison.Ordinal))
            throw new Exception("A folder that is not a registered project was labelled one anyway.");
        // No manifest, no units — and the sentence has to stop short of telling the model the tree does not build,
        // because a script it did not look for may say otherwise.
        var naked = Path.Combine(workspace, "map-naked");
        Directory.CreateDirectory(naked);
        File.WriteAllText(Path.Combine(naked, "notes.txt"), "nothing here is a manifest\n");
        var nakedMap = await tools.MapWorkspace("map-naked");
        if (!nakedMap.Contains("Build units: none found by manifest", StringComparison.Ordinal) || !nakedMap.Contains("read the directory list", StringComparison.Ordinal))
            throw new Exception($"A folder with no manifest was reported as a defect:{Environment.NewLine}{nakedMap}");
        // The list is bounded and says so. Thirty projects is a real monorepo, and a map that truncates in silence is
        // the one thing a model cannot plan around.
        var many = Path.Combine(workspace, "map-many");
        for (var unit = 0; unit < 30; unit++)
            Directory.CreateDirectory(Path.Combine(many, $"p{unit:D2}"));
        for (var unit = 0; unit < 30; unit++)
            File.WriteAllText(Path.Combine(many, $"p{unit:D2}", $"Unit{unit:D2}.csproj"), "<Project/>\n");
        var manyMap = await tools.MapWorkspace("map-many");
        if (!manyMap.Contains("Build units (30)", StringComparison.Ordinal) || !manyMap.Contains("more manifests not listed", StringComparison.Ordinal))
            throw new Exception($"Thirty units were not counted and cut in words:{Environment.NewLine}{manyMap}");
        Console.WriteLine("PASS: workspace_map says what a directory is — units and their references, the project and " + "git attributes of the same workspace, and where its own scan stopped.");
        // ── the frozen card text for every tool ──
        string Preview(string name, string json) => ToolPreviews.PreviewFor(name, json, scope, [workspace]);
        var writePreview = Preview("file_write", """{"path":"src/main.cpp","old_string":"    return 1;","new_string":"    return 2;"}""");
        if (!writePreview.Contains("--- a/src/main.cpp") || !writePreview.Contains("+    return 2;") || !writePreview.Contains("-    return 1;"))
            throw new Exception($"A write did not preview as a diff:{Environment.NewLine}{writePreview}");
        if (!Preview("file_write", """{"path":"../escape.cpp","old_string":"a","new_string":"b"}""").Contains("Refused"))
            throw new Exception("A write outside the workspace previewed as something other than a refusal.");
        if (!Preview("file_write", """{"path":"src/twice.txt","old_string":" absent ","new_string":"x"}""").Contains("was not found"))
            throw new Exception("A write that would change nothing previewed as a diff.");
        var commandPreview = Preview("run_command", """{"command":"cmake --build build","timeout_seconds":60}""");
        if (!commandPreview.Contains("cmake --build build") || !commandPreview.Contains(workspace) || !commandPreview.Contains("Hub asks after 60s of silence"))
            throw new Exception($"A command did not preview with its shell, directory and silence limit:{Environment.NewLine}{commandPreview}");
        // The command card says which of the two ladders it is answering for: somebody looking at a command that parked
        // under 询问审批 needs to read that it moves the repository, and a `git status` card in the same tier must not
        // claim the same thing. (Under 自动审批 only the verbs that move real state still park, and they say so on the
        // same line — the local writes that stopped parking are the ones this card is no longer drawn for.)
        var gitWriteCard = Preview("run_command", """{"command":"git commit -m x"}""");
        if (!gitWriteCard.Contains("git write · moves the index", StringComparison.Ordinal) || !gitWriteCard.Contains("git commit -m x", StringComparison.Ordinal))
            throw new Exception($"A git write did not preview as one:{Environment.NewLine}{gitWriteCard}");
        if (!Preview("run_command", """{"command":"git lfs install"}""").Contains("does not recognise", StringComparison.Ordinal))
            throw new Exception("An unrecognised git subcommand previewed as a recognised one.");
        if (Preview("run_command", """{"command":"git status"}""").Contains("git write", StringComparison.Ordinal) || Preview("run_command", """{"command":"git --no-pager diff HEAD"}""").Contains("git write", StringComparison.Ordinal) || commandPreview.Contains("git write", StringComparison.Ordinal))
            throw new Exception("A command that moves nothing in git carried the git flag anyway.");
        // The two build flags, kept visibly apart because the tiers are apart. A compile says what it spends (a slow
        // minute in the sandbox); a removal says what it destroys (output nobody can regenerate), and only the second
        // one is a card under 完全访问. A card that used one word for both would be the card that argues the tier is a
        // bug — which is exactly what the git flag above exists to prevent for the reach tier.
        if (!commandPreview.Contains("rebuild · compiles into this directory", StringComparison.Ordinal) || commandPreview.Contains("cache break ·", StringComparison.Ordinal))
            throw new Exception($"A build card overstated what it asks about:{Environment.NewLine}{commandPreview}");
        var cacheBreakCard = Preview("run_command", """{"command":"rm -rf build"}""");
        if (!cacheBreakCard.Contains("cache break · removes what a build already wrote", StringComparison.Ordinal))
            throw new Exception($"A cache break previewed as an ordinary file deletion:{Environment.NewLine}{cacheBreakCard}");
        // The mixed line names its worst half and only that half — one line per card, chosen the way the tier is chosen.
        // Asserted as a negative too, because "the card lists everything" is the shape that quietly returns: two flags on
        // one card is a paragraph, and a paragraph on a card is how a person stops reading the part that mattered.
        var mixedCard = Preview("run_command", """{"command":"cmake --build build && rm -rf build"}""");
        if (!mixedCard.Contains("cache break ·", StringComparison.Ordinal) || mixedCard.Contains("rebuild ·", StringComparison.Ordinal))
            throw new Exception("A line that compiles and deleted named the wrong half, or named both." + $"{Environment.NewLine}{mixedCard}");
        if (Preview("run_command", """{"command":"git status"}""").Contains("rebuild ·", StringComparison.Ordinal) || Preview("run_command", """{"command":"ls build"}""").Contains("rebuild ·", StringComparison.Ordinal) || Preview("run_command", """{"command":"ls build"}""").Contains("cache break ·", StringComparison.Ordinal))
            throw new Exception("A command that touches no build output carried a build flag.");
        Console.WriteLine("PASS: a command that writes to git says so on its card, and a git read does not; a compile and " + "a cache break say their own different things, and a read that only names the build directory says neither.");
        // The facts a grant card prints are the ones a person decides on, so they are asserted on the tool that still
        // draws one: the directory is there or it is not, and a path this build refuses says so before anybody clicks.
        var grantFacts = Preview("request_directory", JsonSerializer.Serialize(new { path = workspace }));
        if (!grantFacts.Contains("exists") || !grantFacts.Contains("already inside the session workspace", StringComparison.Ordinal))
            throw new Exception($"A grant preview did not state the facts:{Environment.NewLine}{grantFacts}");
        // The card's "registered Hub project" fact is decided by the same classifier the card's other words come from,
        // so it is asserted against a project the session is *not* already inside. Naming the session's own directory a
        // registered project would be a fact invented for the card: the honest answer there is "you are already
        // working here".
        var otherProject = Path.Combine(toolRoot, "registered-elsewhere");
        Directory.CreateDirectory(otherProject);
        var projectPreview = ToolPreviews.PreviewFor("request_directory", JsonSerializer.Serialize(new { path = otherProject }), scope, [workspace, otherProject]);
        if (!projectPreview.Contains("registered Hub project", StringComparison.Ordinal))
            throw new Exception($"A registered project did not preview as one:{Environment.NewLine}{projectPreview}");
        // The three protected kinds say which one they are, because "an engine installation" and "Hub's own data"
        // are different places to agree to open — and both cards say the yes is a reading one.
        var engineCard = Preview("request_directory", JsonSerializer.Serialize(new { path = toolEngineRoot }));
        if (!engineCard.Contains("protected location — an engine installation", StringComparison.Ordinal)
            || !engineCard.Contains("read only", StringComparison.Ordinal)
            || engineCard.Contains("read and write", StringComparison.Ordinal))
            throw new Exception($"A protected directory did not preview as the place it is:{Environment.NewLine}{engineCard}");
        if (!Preview("memory_write", """{"scope":"project","name":"a.md","mode":"append","content":"内容"}""").Contains("memory_append · project · a.md"))
            throw new Exception("A memory write did not preview with its scope, name and mode.");
        if (Preview("read_file", """{"path":"src/main.cpp"}""") != "read_file · src/main.cpp" || Preview("memory_read", """{"scope":"global","name":"MEMORY.md"}""") != "memory_read · global · MEMORY.md" || Preview("没登记过的工具", "{}") != "没登记过的工具")
            throw new Exception("A read-only or unknown call did not preview as itself.");
        if (!Preview("search_text", """{"pattern":"App","glob":"*.cpp","path":"src"}""").Contains("search_text · App · *.cpp · in src", StringComparison.Ordinal) || !Preview("search_text", """{"pattern":"App"}""").Contains("any text file · in (the whole workspace)", StringComparison.Ordinal) || !Preview("list_directory", """{"path":"src","depth":2}""").Contains("list_directory · src · depth 2", StringComparison.Ordinal) || !Preview("find_files", """{"glob":"*.h"}""").Contains("find_files · *.h · in (the whole workspace)", StringComparison.Ordinal))
            throw new Exception("A read-only lookup did not say what it looks for and where.");
        if (ToolPreviews.PreviewFor("file_write", "{ not json", scope)is not { Length: > 0 })
            throw new Exception("Arguments that do not parse produced no card text at all.");
        Console.WriteLine("PASS: every tool previews what approving it would actually do.");
        // ── the wire shape: every body binds, and every parameter name is the one the model is told to send ──
        // AIFunctionFactory exports parameters by name and applies no naming policy to them, so a serializer-level
        // snake_case policy renames the schema without renaming what the invoker looks for. The mismatch is invisible
        // at build time and arrives at run time as "missing required parameter", which reads like a model bug.
        var bound = new Dictionary<string, AIFunction>(StringComparer.Ordinal);
        foreach (var(wireName, body)in new (string, Delegate)[]
        {
            ("read_file", (Delegate)tools.ReadFile),
            ("file_write", tools.FileWrite),
            ("run_command", tools.RunCommand),
            ("memory_read", tools.MemoryRead),
            ("memory_write", tools.MemoryWrite),
            ("search_text", tools.SearchText),
            ("list_directory", tools.ListDirectory),
            ("find_files", tools.FindFiles),
            ("capture_screen", tools.CaptureScreen),
            ("workspace_map", tools.MapWorkspace),
            // web_fetch binds here too, but against a host that answers from memory: the eleven bodies this
            // loop binds are the ones asserted without a network, and the page-fetch tool belongs to that set
            // for exactly the reason the capture tool does — the decision is Core's, the socket is somebody
            // else's, and the stub is what makes the branches reachable at all.
            ("web_fetch", tools.FetchWebPage),
        }

        )
            bound[wireName] = AIFunctionFactory.Create(body, new AIFunctionFactoryOptions { Name = wireName });
        var schema = string.Join("\n", bound.Values.Select(function => function.JsonSchema.GetRawText()));
        foreach (var expected in new[]
        {
            "old_string",
            "new_string",
            "replace_all",
            "timeout_seconds",
            "ignore_case",
            "max_matches",
            "max_characters"
        }

        )
            if (!schema.Contains(expected, StringComparison.Ordinal))
                throw new Exception($"The wire schema does not advertise '{expected}'.");
        foreach (var leaked in new[]
        {
            "oldString",
            "newString",
            "replaceAll",
            "timeoutSeconds",
            "ignoreCase",
            "maxMatches"
        }

        )
            if (schema.Contains(leaked, StringComparison.Ordinal))
                throw new Exception($"The wire schema advertises the C# spelling '{leaked}' instead of snake_case.");
        // And the names the schema advertises are the names an invocation actually accepts — the two halves of the
        // same contract, asserted together because only one of them fails loudly.
        var boundEdit = await bound["file_write"].InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["path"] = "src/twice.txt", ["old_string"] = "dup", ["new_string"] = "single", ["replace_all"] = true, }));
        if (!Convert.ToString(boundEdit)!.Contains("Edited") || File.ReadAllText(Path.Combine(workspace, "src", "twice.txt")) != "single\nsingle\n")
            throw new Exception($"A call using the schema's own parameter names did not bind ({boundEdit}).");
        var boundSearch = await bound["search_text"].InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["pattern"] = "App::go", ["path"] = "probe", ["ignore_case"] = true, ["max_matches"] = 5, }));
        if (!Convert.ToString(boundSearch)!.Contains("probe/app.cpp:3:", StringComparison.Ordinal))
            throw new Exception($"search_text did not bind the parameter names its schema advertises ({boundSearch}).");
        // The newest tool in the set is bound the same way, because a name the schema advertises and the invocation
        // rejects is exactly the failure that reads like a model bug rather than a registration one.
        var boundMap = await bound["workspace_map"].InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["path"] = "map-tree", }));
        if (!Convert.ToString(boundMap)!.Contains("Build units", StringComparison.Ordinal))
            throw new Exception($"workspace_map did not bind the parameter names its schema advertises ({boundMap}).");
        Console.WriteLine("PASS: all twelve tools bind, and the schema names are the names a call is accepted by.");
        // ── capture_screen: target matching, black frames not sent, normal frames landing in the session directory ──
        // The tool's every branch is asserted against a host that draws nothing, because the decision — which window,
        // is this frame worth sending, where do the bytes go — is Core's, and only the pixels are the host's.
        var captureSchema = bound["capture_screen"].JsonSchema.GetRawText();
        if (!captureSchema.Contains("fullscreen", StringComparison.Ordinal) || !captureSchema.Contains("target", StringComparison.Ordinal) || !captureSchema.Contains("process_id", StringComparison.Ordinal) // The camelCase needle is the point, not decoration: AIFunctionFactory exports the C# parameter name with
        // no naming policy, so spelling the parameter `processId` would reach the model in camelCase and every
        // call it made would arrive with an argument the body never reads.
        || captureSchema.Contains("processId", StringComparison.Ordinal))
            throw new Exception("capture_screen's schema hides an argument the model has to send, or exports it in camelCase.");
        // A scope with no capture host is the honest case on a host this build has not taught: the model is told the
        // capture is not wired in, and is told not to retry it. A black frame would read as a working channel.
        var unwired = tools.CaptureScreen("Axmol Demo");
        if (!unwired.Contains("no capture host", StringComparison.OrdinalIgnoreCase) || !unwired.Contains("do not retry", StringComparison.OrdinalIgnoreCase))
            throw new Exception($"A build without a capture host answered like a capture that worked: {unwired}");
        var shotRoot = Path.Combine(toolRoot, "shots");
        var shots = new ConversationStore(shotRoot);
        var session = Conversation.Create("orcarouter");
        var landed = new List<ChatImage>();
        var pngBytes = new byte[32];
        new byte[]
        {
            0x89,
            0x50,
            0x4E,
            0x47,
            0x0D,
            0x0A,
            0x1A,
            0x0A
        }.CopyTo(pngBytes, 0);
        var asked = new List<CapturableWindow?>
        {
            null
        };
        var blank = false;
        var drawing = false;
        var host = new ScreenCaptureBridge(() => [new CapturableWindow(1, "Axmol Demo", 100, 800, 600), new CapturableWindow(2, "Axmol Hub — 助手", 200, 1200, 800), new CapturableWindow(3, "Outlook", 300, 1000, 700)], window =>
        {
            asked.Add(window);
            return drawing ? null : new CapturedFrame(pngBytes, window?.Width ?? 3840, window?.Height ?? 2160, blank ? new FrameStats(window?.Width ?? 3840, window?.Height ?? 2160, 1, 0) : new FrameStats(window?.Width ?? 3840, window?.Height ?? 2160, 240, 182.4));
        });
        var grabbing = new WorkspaceTools(new WorkspaceToolScope(workspace, guards, dataRoot, session.Id, [], log, shots, Screen: host, RecordFrames: frames => landed.AddRange(frames)));
        // One target, several hits: the refusal lists the candidates and the host is never asked to draw. Guessing
        // between two windows would send back a frame the model cannot tell apart from the one it asked for.
        var ambiguous = grabbing.CaptureScreen("Axmol");
        if (!ambiguous.Contains("matches 2 windows", StringComparison.Ordinal) || !ambiguous.Contains("Axmol Hub", StringComparison.Ordinal) || ambiguous.Contains("Outlook", StringComparison.Ordinal) || asked.Count != 1)
            throw new Exception($"An ambiguous target was not refused as ambiguous: {ambiguous}");
        var noTarget = grabbing.CaptureScreen("", false);
        if (!noTarget.Contains("needs a window title", StringComparison.Ordinal))
            throw new Exception($"A call with neither target nor fullscreen answered: {noTarget}");
        // The only cell that may draw the display, and it must not be handed a window.
        asked.Clear();
        var display = grabbing.CaptureScreen("", fullscreen: true);
        if (!display.Contains("the whole display", StringComparison.Ordinal) || asked.Single()is not null || landed.Count != 1 || display.Contains("blank", StringComparison.OrdinalIgnoreCase))
            throw new Exception($"A fullscreen capture did not land as one stored frame: {display}");
        // The blank rule: nothing is stored and no picture is attached. This is the branch that decides whether the
        // channel is trustworthy, because a black PNG is a valid file, a valid header and a plausible byte count.
        var storedWith = landed.Count;
        blank = true;
        var black = grabbing.CaptureScreen("Outlook");
        if (!black.Contains("blank frame", StringComparison.Ordinal) || !black.Contains("Outlook", StringComparison.Ordinal) || landed.Count != storedWith || !black.Contains("do not retry", StringComparison.OrdinalIgnoreCase) || !black.Contains("screenshot", StringComparison.OrdinalIgnoreCase))
            throw new Exception($"A blank frame was sent on as a capture: {black}");
        // A host that draws nothing at all — the window closed, the display is locked — gets its own sentence rather
        // than the blank-frame one, because the fix is different.
        drawing = true;
        blank = false;
        var emptyHand = grabbing.CaptureScreen("Outlook");
        if (!emptyHand.Contains("no frame", StringComparison.Ordinal))
            throw new Exception($"A host that drew nothing answered: {emptyHand}");
        drawing = false;
        // The card says which window is about to leave the machine, in the same words the tool will act on.
        var card = ToolPreviews.PreviewFor("capture_screen", """{"target":"Outlook"}""", new WorkspaceToolScope(workspace, guards, dataRoot, session.Id, [], null, Screen: host));
        if (!card.Contains("window \"Outlook\"", StringComparison.Ordinal) || !card.Contains("pid 300", StringComparison.Ordinal) || !card.Contains("1000×700", StringComparison.Ordinal))
            throw new Exception($"The approval card does not name the window: {card}");
        var both = ToolPreviews.PreviewFor("capture_screen", """{"target":"Demo","fullscreen":true}""", new WorkspaceToolScope(workspace, guards, dataRoot, session.Id, [], null, Screen: host));
        if (!both.Contains("window \"Axmol Demo\"", StringComparison.Ordinal))
            throw new Exception($"Fullscreen did not defer to the named target: {both}");
        // A refusal that lists nothing leaves the model to invent a title, and it does so with confidence.
        var bare = ScreenCapture.Find([], "Notes");
        if (bare.Ok || !bare.Refusal!.Contains("no visible window title", StringComparison.Ordinal))
            throw new Exception("An empty window list was not refused as an empty window list.");
        var where = Path.Combine(shotRoot, "ai", "sessions", session.Id);
        if (Directory.GetFiles(where).Length != 1 || landed.Single()is not { File: "1.png", MediaType: "image/png" })
            throw new Exception($"The frame Hub stored is not the one the turn names: {landed.Single()}");
        // The case a process id exists for: two Hub windows with byte-identical titles — the installed app and the dev
        // build made from the same tree, both titled from the assembly version. A title cannot pick one, and the
        // refusal has to say that and name the way out; a pid can, and neither half is ever allowed to guess.
        var twinRoot = Path.Combine(toolRoot, "twin-shots");
        var twinShots = new ConversationStore(twinRoot);
        var twinSession = Conversation.Create("orcarouter");
        var twinAsked = new List<CapturableWindow?>();
        var twinLanded = new List<ChatImage>();
        var twins = new ScreenCaptureBridge(() => [new CapturableWindow(11, "Axmol Hub v0.9.3", 200, 1200, 800), new CapturableWindow(12, "Axmol Hub v0.9.3", 201, 1280, 820)], window =>
        {
            twinAsked.Add(window);
            return new CapturedFrame(pngBytes, window?.Width ?? 1, window?.Height ?? 1, new FrameStats(window?.Width ?? 1, window?.Height ?? 1, 240, 182.4));
        });
        var twinHost = new WorkspaceTools(new WorkspaceToolScope(workspace, guards, dataRoot, twinSession.Id, [], log, twinShots, Screen: twins, RecordFrames: frames => twinLanded.AddRange(frames)));
        var twinRefusal = twinHost.CaptureScreen("Axmol Hub");
        if (!twinRefusal.Contains("matches 2 windows", StringComparison.Ordinal) || !twinRefusal.Contains("process_id", StringComparison.Ordinal) || twinAsked.Count != 0)
            throw new Exception($"Two windows sharing a title were not refused by title: {twinRefusal}");
        var picked = twinHost.CaptureScreen("Axmol Hub", false, 201);
        if (!picked.Contains("pid 201", StringComparison.Ordinal) || picked.Contains("pid 200", StringComparison.Ordinal) || twinAsked.Single()is not { } chosen || chosen.Handle != 12 || twinLanded.Count != 1)
            throw new Exception($"The process id did not pick the window it named: {picked}");
        // A pid narrows the owner, never the window: one process with two windows is still two answers.
        var twinPid = ScreenCapture.Find([new CapturableWindow(21, "助手页", 700, 1, 1), new CapturableWindow(22, "设置页", 700, 1, 1)], "", 700);
        if (twinPid.Ok || !twinPid.Refusal!.Contains("matches 2 windows", StringComparison.Ordinal))
            throw new Exception($"A process owning two windows was picked anyway: {twinPid.Refusal}");
        // The list a refusal prints is what the model reads next, so this Hub's own windows are marked — an assistant
        // developing Hub is looking for Hub's window, and there are usually two of them on the screen.
        var marked = ScreenCapture.Find([new CapturableWindow(31, "Axmol Hub v0.9.3", Environment.ProcessId, 1, 1), new CapturableWindow(32, "Axmol Hub v0.9.3", Environment.ProcessId + 1, 1, 1)], "Axmol Hub");
        if (marked.Ok || !marked.Refusal!.Contains("(this Hub)", StringComparison.Ordinal))
            throw new Exception($"The refusal did not say which of those windows this Hub owns: {marked.Refusal}");
        // A card that shows a bare number is a card nobody can approve: the pid is how the model addresses the window,
        // the title is what the person is agreeing to let leave the machine.
        var pidCard = ToolPreviews.PreviewFor("capture_screen", """{"process_id":201}""", new WorkspaceToolScope(workspace, guards, dataRoot, twinSession.Id, [], null, Screen: twins));
        if (!pidCard.Contains("window \"Axmol Hub v0.9.3\"", StringComparison.Ordinal) || !pidCard.Contains("pid 201", StringComparison.Ordinal))
            throw new Exception($"A capture by process id alone put no window on the card: {pidCard}");
        Console.WriteLine("PASS: capture_screen refuses an ambiguous target, refuses a blank frame, narrows by process id " + "without ever picking, and stores only a frame worth sending.");
        // ── web_fetch: the network tool that never makes a single real request here ──
        // Every branch of a page fetch is a decision over a response somebody hands back, so the host here is a
        // delegate with a script. The one thing no cell below can prove is that a real server answers — that hop is
        // human-run, the same as the image channel's, and --check-webfetch is the procedure written for it.
        var pages = new List<Func<FetchRequest, FetchResponse>>();
        var sent = new List<Uri>();
        var allowed = true;
        var web = new WebBridge(() => allowed, (request, _) =>
        {
            sent.Add(request.Target);
            if (pages.Count == 0)
                throw new InvalidOperationException("the fetch stub ran out of scripted responses");
            var scripted = pages[0];
            pages.RemoveAt(0);
            return Task.FromResult(scripted(request));
        });
        var browsing = new WorkspaceTools(new WorkspaceToolScope(workspace, guards, dataRoot, "fetch-1", ["SUPERSECRET"], log, Web: web));
        // A page as the stub serves it: the body arrives as bytes, which is also what makes the byte-cap cell
        // something the host cannot cheat by returning a short string.
        void QueuePage(int status, string type, string body, Uri? redirect = null) => pages.Add(_ => new FetchResponse(status, type, redirect, new MemoryStream(Encoding.UTF8.GetBytes(body))));
        // A hop that dies before a response exists — a socket that closed, or one that never got an answer.
        void QueueFailure(Exception problem) => pages.Add(_ => throw problem);
        // The gate runs before the socket, in an order that is the model's rather than the implementer's: a call the
        // switch refuses must not comment on URL syntax, because the user has to be told where the switch is.
        allowed = false;
        var switchedOff = await browsing.FetchWebPage("https://example.com/docs");
        if (!switchedOff.Contains("outbound fetching turned off", StringComparison.Ordinal) || !switchedOff.Contains("do not retry", StringComparison.OrdinalIgnoreCase))
            throw new Exception($"A fetch the setting refuses answered another way: {switchedOff}");
        allowed = true;
        var noHost = await new WorkspaceTools(new WorkspaceToolScope(workspace, guards, dataRoot, "fetch-2", [], log, null, null, null, null, null, null)).FetchWebPage("https://example.com/docs");
        if (!noHost.Contains("no fetch host", StringComparison.OrdinalIgnoreCase) || !noHost.Contains("do not retry", StringComparison.OrdinalIgnoreCase))
            throw new Exception($"A build without a fetch host answered like a fetch that worked: {noHost}");
        // An empty scope is the shape the self-check claims is unwired, so it has to be unwired about this too.
        if (WorkspaceToolScope.Empty.Web is not null)
            throw new Exception("WorkspaceToolScope.Empty carries a fetch host; the zero-network claim is then false.");
        var refusals = new List<string>();
        foreach (var bad in new[]
        {
            "example.com/docs",
            "http://example.com/docs",
            "https://127.0.0.1:8080/api",
            "https://localhost:5173/",
            "https://169.254.169.254/latest/meta-data",
            "https://192.168.1.1/",
            "https://[::1]/",
            "file:///etc/hosts"
        }

        )
            refusals.Add(await browsing.FetchWebPage(bad));
        if (refusals.Any(line => !line.Contains("Refused", StringComparison.Ordinal)) || refusals.Any(line => !line.Contains("do not retry", StringComparison.OrdinalIgnoreCase)) || !refusals[0].Contains("not an absolute https URL", StringComparison.Ordinal) || !refusals[1].Contains("is not https", StringComparison.Ordinal) || !refusals[2].Contains("loopback, link-local or a private address", StringComparison.Ordinal) || !refusals[4].Contains("loopback, link-local or a private address", StringComparison.Ordinal) || sent.Count != 0)
            throw new Exception($"The address policy let something through or talked when it had already refused:{Environment.NewLine}" + string.Join(Environment.NewLine, refusals));
        Console.WriteLine("PASS: web_fetch refuses the setting, the missing host and every unfetchable address without " + "opening a socket.");
        QueuePage(200, "text/html; charset=utf-8", "<html><head><title>Manual 5.4</title><script>var secret = 1;</script><style>a{color:red}</style></head>" + "<body><nav>Home API</nav><h1>Readme</h1><p>lua &amp; C, &copy; 2026</p><p>Second <b>bold</b> bit</p>" + "<ul><li>one</li><li>two</li></ul><!-- hidden --></body></html>");
        var page = await browsing.FetchWebPage("https://www.lua.org/manual/5.4/readme.html");
        if (!page.Contains("HTTP 200", StringComparison.Ordinal) || !page.StartsWith("https://www.lua.org/", StringComparison.Ordinal) || !page.Contains("Manual 5.4", StringComparison.Ordinal) || !page.Contains("Readme", StringComparison.Ordinal) || !page.Contains("lua & C", StringComparison.Ordinal) || !page.Contains("© 2026", StringComparison.Ordinal) || page.Contains("var secret = 1", StringComparison.Ordinal) || page.Contains("color:red", StringComparison.Ordinal) || page.Contains("<p>", StringComparison.Ordinal) || page.Contains("hidden", StringComparison.Ordinal) || !page.Contains("one") || !page.Contains("two"))
            throw new Exception($"A page did not come back as page text:{Environment.NewLine}{page}");
        if (page.IndexOf("Manual 5.4", StringComparison.Ordinal) > page.IndexOf("Readme", StringComparison.Ordinal))
            throw new Exception("The title was not hoisted above the body.");
        Console.WriteLine("PASS: web_fetch hands back the title and the prose, and not the scripts, styles, markup or comments.");
        // "There was nothing there" and "we could not ask" are two different sentences, and a 404 with a body is an
        // answer with a body in it.
        QueuePage(404, "text/html", "<h1>Not found</h1><p>No such manual page.</p>");
        var missing = await browsing.FetchWebPage("https://example.com/gone");
        QueuePage(200, "text/html", "<html><body><script>x=1;</script></body></html>");
        var empty = await browsing.FetchWebPage("https://example.com/blank");
        if (!missing.Contains("HTTP 404", StringComparison.Ordinal) || !missing.Contains("No such manual page", StringComparison.Ordinal) || !empty.Contains("no readable text", StringComparison.Ordinal) || !empty.Contains("not a network problem", StringComparison.Ordinal))
            throw new Exception($"An empty or absent page was reported as a failure, or a failure as an empty page:{Environment.NewLine}" + $"{missing}{Environment.NewLine}{empty}");
        Console.WriteLine("PASS: a 404 keeps its body, an empty page is an answer, and neither is dressed up as a timeout.");
        QueuePage(200, "application/pdf", "%PDF-1.4 ...");
        var binary = await browsing.FetchWebPage("https://example.com/manual.pdf");
        QueuePage(200, "text/plain", new string ('x', WebFetch.MaxBytes + 64));
        var oversized = await browsing.FetchWebPage("https://example.com/huge.txt");
        if (!binary.Contains("application/pdf", StringComparison.Ordinal) || !binary.Contains("which is not text", StringComparison.Ordinal) || !binary.Contains("run_command", StringComparison.Ordinal) || !oversized.Contains($"larger than {WebFetch.MaxBytes / 1024 / 1024} MiB", StringComparison.Ordinal) || !oversized.Contains("do not retry", StringComparison.OrdinalIgnoreCase))
            throw new Exception($"A binary or an over-cap body was buffered instead of refused:{Environment.NewLine}" + $"{binary}{Environment.NewLine}{oversized}");
        Console.WriteLine("PASS: web_fetch reads at most a megabyte and only text, and points at run_command for the rest.");
        // Redirects are where a host policy gets walked past, so each shape is asserted on what was *asked*, not only
        // on what was said.
        sent.Clear();
        QueuePage(302, "text/html", "", new Uri("http://internal.example.com/x"));
        var downgraded = await browsing.FetchWebPage("https://example.com/start");
        if (!downgraded.Contains("is not https", StringComparison.Ordinal) || sent.Count != 1)
            throw new Exception($"A redirect left https and something still asked the second host: {downgraded}");
        QueuePage(302, "text/html", "", new Uri("https://127.0.0.1:9/"));
        var inward = await browsing.FetchWebPage("https://example.com/start");
        if (!inward.Contains("loopback, link-local or a private address", StringComparison.Ordinal) || sent.Count != 2)
            throw new Exception($"A redirect to a private address was followed: {inward}");
        QueuePage(302, "text/html", "", new Uri("/next.html", UriKind.Relative));
        QueuePage(200, "text/html", "<h1>Arrived</h1>");
        var hopped = await browsing.FetchWebPage("https://example.com/start");
        if (!hopped.Contains("https://example.com/next.html", StringComparison.Ordinal) || !hopped.Contains("after 1 redirect", StringComparison.Ordinal) || !hopped.Contains("Arrived", StringComparison.Ordinal))
            throw new Exception($"A relative Location was not resolved against the hop that sent it: {hopped}");
        sent.Clear();
        for (var spin = 0; spin < 6; spin++)
            QueuePage(302, "text/html", "", new Uri("https://example.com/round"));
        var loop = await browsing.FetchWebPage("https://example.com/start");
        if (!loop.Contains("kept redirecting", StringComparison.Ordinal) || !loop.Contains("do not retry", StringComparison.OrdinalIgnoreCase) || sent.Count > WebFetch.MaxRedirects + 1)
            throw new Exception($"A redirect loop was chased {sent.Count} hops before it was refused: {loop}");
        Console.WriteLine("PASS: a redirect cannot cross out of https or inward, a relative one is resolved, and a loop is cut.");
        QueueFailure(new TaskCanceledException("the request was cancelled"));
        var timedOut = await browsing.FetchWebPage("https://example.com/slow");
        QueueFailure(new HttpRequestException("no such host"));
        var unreachable = await browsing.FetchWebPage("https://does-not-exist.example");
        if (!timedOut.Contains("Could not reach", StringComparison.Ordinal) || !timedOut.Contains($"within {(int)WebFetch.Timeout.TotalSeconds}s", StringComparison.Ordinal) || !unreachable.Contains("Could not reach", StringComparison.Ordinal) || unreachable.Contains("no readable text", StringComparison.Ordinal))
            throw new Exception($"A timeout or a dead host was reported as an empty page:{Environment.NewLine}" + $"{timedOut}{Environment.NewLine}{unreachable}");
        Console.WriteLine("PASS: a timeout and a socket failure both say could-not-reach, never there-is-nothing-there.");
        // The window: what the model asked for is what it gets, what it must not see is gone, and the one place a
        // token hides is never the activity line.
        QueuePage(200, "text/plain", string.Concat(Enumerable.Range(1, 400).Select(index => $"line {index}\n")));
        var cut = await browsing.FetchWebPage("https://example.com/long.txt", max_characters: 60);
        if (!cut.Contains("line 1", StringComparison.Ordinal) || !cut.Contains("more characters not shown", StringComparison.Ordinal) || !cut.Contains("max_characters", StringComparison.Ordinal) || cut.Contains("line 400", StringComparison.Ordinal))
            throw new Exception($"max_characters did not bound the result the way its own name says: {cut}");
        QueuePage(200, "text/html", "<p>token=SUPERSECRET leaked</p>");
        var bled = await browsing.FetchWebPage("https://example.com/account?token=SUPERSECRET");
        if (bled.Contains("SUPERSECRET", StringComparison.Ordinal) || !bled.Contains("[REDACTED]", StringComparison.Ordinal))
            throw new Exception($"A stored secret came back through a page unhidden:{Environment.NewLine}{bled}");
        var query = WebFetch.Shown("https://api.example.com/v1/items?token=SUPERSECRET");
        if (query != "api.example.com/v1/items")
            throw new Exception($"An activity row must show host and path only, and it showed '{query}'.");
        Console.WriteLine("PASS: max_characters cuts where it says, a stored secret is hidden in the page and in the row.");
        var cardFetch = ToolPreviews.PreviewFor("web_fetch", """{"url":"https://docs.example.com/build?token=SUPERSECRET"}""", new WorkspaceToolScope(workspace, guards, dataRoot, "fetch-5", ["SUPERSECRET"], null, Web: web));
        if (!cardFetch.Contains("docs.example.com/build", StringComparison.Ordinal) || !cardFetch.Contains("token=[REDACTED]", StringComparison.Ordinal) || cardFetch.Contains("SUPERSECRET", StringComparison.Ordinal) || !cardFetch.Contains("scheme: https", StringComparison.Ordinal))
            throw new Exception($"The fetch card does not name the host, cap the read or hide the token:{Environment.NewLine}{cardFetch}");
        allowed = false;
        var cardOff = ToolPreviews.PreviewFor("web_fetch", """{"url":"https://docs.example.com/build"}""", new WorkspaceToolScope(workspace, guards, dataRoot, "fetch-6", [], null, Web: web));
        allowed = true;
        var cardBad = ToolPreviews.PreviewFor("web_fetch", """{"url":"http://docs.example.com/build"}""", new WorkspaceToolScope(workspace, guards, dataRoot, "fetch-7", [], null, Web: web));
        if (!cardOff.Contains("outbound fetching turned off", StringComparison.Ordinal) || !cardBad.Contains("is not https", StringComparison.Ordinal))
            throw new Exception($"A card promised an action the tool would refuse:{Environment.NewLine}{cardOff}{Environment.NewLine}{cardBad}");
        Console.WriteLine("PASS: the fetch card names the host, and says out loud when the call is going to be refused.");
        // The two switches are independent, and both ship on. Page fetch is bounded and free; search is neither, yet a
        // capability nobody is offered is one nobody uses — its cost is stated where the switch is and its calls are
        // counted in the session that spent them, so the default is on and the switch is the way to stop.
        var webPreferences = Path.Combine(toolRoot, "webfetch-settings.json");
        File.WriteAllText(webPreferences, "{}");
        var defaults = new PreferencesStore(webPreferences).Load();
        if (!defaults.AllowOutboundWebFetch || !defaults.AllowHostedWebSearch || !defaults.AllowDeepSeekWebApi)
            throw new Exception("A fresh install must offer named-page fetch and web search.");
        File.WriteAllText(webPreferences, """{"AllowOutboundWebFetch":false}""");
        var fetchOff = new PreferencesStore(webPreferences).Load();
        if (fetchOff.AllowOutboundWebFetch || !fetchOff.AllowHostedWebSearch)
            throw new Exception("An explicit fetch off was ignored, or it turned hosted search off with it.");
        File.WriteAllText(webPreferences, """{"AllowOutboundWebFetch":true}""");
        var fetchOn = new PreferencesStore(webPreferences).Load();
        if (!fetchOn.AllowOutboundWebFetch || !fetchOn.AllowHostedWebSearch)
            throw new Exception("Enabling page fetch changed the independent hosted-search switch.");
        File.WriteAllText(webPreferences, """{"AllowOutboundWebFetch":false,"AllowHostedWebSearch":false}""");
        var searchOff = new PreferencesStore(webPreferences).Load();
        if (searchOff.AllowOutboundWebFetch || searchOff.AllowHostedWebSearch)
            throw new Exception("The hosted-search switch did not round-trip independently of page fetch.");
        Console.WriteLine("PASS: page fetching and web search both default on, and the two permissions round-trip independently.");
        log.Write("self-check finished");
        Directory.Delete(toolRoot, recursive: true);
        return;
    }
}
