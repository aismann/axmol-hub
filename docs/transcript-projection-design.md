# P1: stable transcript projection

Status: design only. No JSON migration, renderer replacement, storage change, or UI change is part of this step.

## Why a projection

`ChatPanel.RenderMessages` currently rebuilds a visible-turn list and call-name map on every render, checks global approval/display stamps, and derives groups while creating controls. Appending to an open activity group clears and rebuilds its children (`DeriveActivityGroup`). The 30-turn first-paint window and parked controls reduce rendering cost, but neither provides a stable semantic row model.

Introduce a UI-independent `TranscriptProjection` in `AxmolHub.Core`, owned per live conversation by the shell/registry adapter. It is a derived in-memory index, not another transcript or persistence format. Keep synchronous `Changed`/`Reload` behavior: existing checks inspect the panel immediately after switching sessions. This proposal does not defer those notifications.

## Identity and revisions

Use runtime occurrence identities, not timestamps, list positions, record hashes, or tool-call IDs:

- `TurnKey = (conversationId, projectionEpoch, monotonicallyAllocatedOccurrence)`. Each occurrence in `Messages` gets its own key, even if two occurrences are equal or reference the same record. A fork gets its own namespace.
- Append/insert allocates keys; replacement explicitly preserves the target key; move preserves it; deletion retires it. Source index is lookup metadata, never identity. An edit followed by truncation preserves the edited occurrence but retires the removed suffix; regeneration allocates new answer occurrences.
- A `ChatTurn` is an immutable record, but `with` replacement changes its reference. `ApprovalSeen`, approval decisions, undo state, elapsed time, usage and elision therefore must not mint new identities.
- `SourceRevision` advances once per committed mutation batch, not per save or notification. `TurnRevision` advances on an actual changed value. A separate `RowRevision` advances only if that row's semantic presentation or action payload changes. Equal-value replacement is a no-op for projection output. Track display options with a separate options revision; no localization strings or Avalonia state belong in the model.
- Bootstrap allocates keys once from existing JSON, without saving anything. Keys and revisions live in a sidecar, not new public serialized `Conversation` properties. On process restart or genuine conversation reload, allocate a fresh epoch and reset consumers. Cross-process key stability is explicitly not promised.

`Conversation.Messages` is currently a public settable `List<ChatTurn>`, and production code also writes its entries directly. A correct incremental implementation cannot infer arbitrary in-place changes from list length or tail identity. First implement a full-snapshot reconciliation path (linear, correctness-first). Explicit mutation batches are the fast path only after audited writers emit them. Unknown list replacement/ambiguous duplicate reconciliation must issue a documented reset, not silently guess identity. Reference/value equality can help preserve unambiguous occurrences, but cannot identify which identical duplicate was edited. Debug checks should compare incremental output against a fresh projection.

## Model and grouping contract

Expose ordered immutable row snapshots, with nested piece children:

- `MessageRow`, `NoticeRow`, `ThoughtRow`, `CallSurfaceRow` (approval/undo/narrated call), and `ToolExchangeRow` (result plus optional paired call).
- `ActivityPiece`: flat or folded, stable key anchored to its first contributing turn, ordered children, contributing turn keys, and semantic summary data (call count, durations, change counts). Child keys are based on their owning turn occurrence plus facet (`prose`, `thought`, `call-surface`, `result`), not their ordinal in the piece. Result-owned exchange identity survives its call's approval change.
- Rows carry source turn keys and current source-index mappings for actions. Before edit/fork/undo/approve executes, resolve the key against the current revision; never execute a stale cached integer index.

Port existing classification before simplifying it:

1. System turns are omitted from the visible sequence. Despite a grouping comment saying system turns break runs, the current renderer filters them before grouping, so they do **not** break groups today. Preserve this behavior and pin it explicitly.
2. Tool turns and assistant calls are activity; assistant reasoning-only turns are activity only with empty text and no plan review state. Whitespace is not equivalent to empty here. User, notice, ordinary prose and plan review turns break pieces.
3. A narrated call starts a new flat piece only when it has non-whitespace prose and both `ApprovalState` and `UndoName` are null. Its consecutive tool results extend the flat piece. Otherwise silent activity accumulates in a folded piece until a narrated call or non-activity turn.
4. Approval/undo calls retain their functional surface and prose. Silent calls can contribute thinking without an empty prose row. Thinking-only turns produce one thought row, not a fold nested inside another fold.
5. A paired `file_write` result produces no duplicate action row; the call's write record owns that surface. Orphan results remain visible. Do not invent a call for them.

Represent call pairing separately from group membership. Preserve the current two lookup policies initially: global result-name lookup takes the first named assistant call for an ID; within a piece, result detail pairing takes the last assistant call for that ID. These disagree for duplicate IDs, so a single global dictionary would change behavior. Scope keys by conversation and retain call occurrences; test duplicates, empty IDs, unknown tools, orphan results, multiple results and nonadjacent legacy results. Pairing must not reorder stored turns or invoke `RepairToolCallOrdering`; that is the conversation's replay boundary, not rendering's job. A later policy unification requires an explicit behavior change.

When a mutation splits/merges pieces, retain the key of a surviving anchored piece; retire other piece keys and insert new ones deterministically. Stable child keys permit reuse/reparenting even when group membership changes. Fold expansion, detail expansion, Markdown promotion, scroll anchoring and parked controls remain viewer state keyed by row identity, never serialized fields.

## Incremental boundary

Suggested conceptual API:

- `Bootstrap(snapshot, options) -> ProjectionSnapshot`
- `Apply(expectedSourceRevision, mutationBatch) -> ProjectionDelta`
- `SetOptions(options) -> ProjectionDelta`
- `Reconcile(snapshot) -> ProjectionDelta` for unaudited writers and recovery
- `Snapshot / TryResolve(TurnKey)` for consumers and actions

Mutation batches contain `Insert`, `Replace`, `Remove`, `Move` or explicit `Reset`. A delta names its epoch and base/next revisions and contains ordered `InsertRows`, `RemoveRows`, `MoveRows`, `UpdateRow` operations (including parent keys for piece children), plus changed source-index mappings. Revision mismatch requires a fresh snapshot, never best-effort patch application. Apply a batch atomically so observers never see a call updated without its inserted result.

Maintain call occurrence indexes, turn-to-piece membership and dependency edges from calls to results. Append updates the open piece and changed summary/terminal facets only; it must not regenerate all existing children. Inserted results next to earlier calls invalidate that piece and affected dependents, not the entire transcript. A boundary-changing replacement recomputes outward to unchanged piece boundaries; a large repair/reset may deliberately take O(n). Last-answer action eligibility is a dependency too: append can update the former last row. ApprovalSeen alone should not repaint message content, although the attention badge may change. Include all presentation dependencies (plan, images, search, failure, arguments, reasoning, undo, elapsed and burn fields), not only today's approval stamp.

Take a coherent snapshot or collect mutations under `ConversationRegistry`'s per-session gate. Publish immutable deltas after releasing the gate, on the existing UI dispatch path. Do not enumerate a mutable `Messages` list concurrently or invoke UI callbacks under the registry lock. `ConversationRun` owns live buffers and elapsed clocks; its live bubble remains a separate ephemeral tail. Streaming tokens are not persisted turn replacements.

Projection lifetime follows the registry conversation, with explicit disposal on deletion/eviction. Model indexes and delta retention need bounded policies; do not retain an unbounded history of prior snapshots. A consumer missing retained deltas receives a snapshot.

## Mutation audit and adoption

Specific integration sites:

- `src/AxmolHub.Core/Conversation.cs`: `Append`, `AppendFunctionResult`, `CloseUnansweredToolCalls`, and `RepairToolCallOrdering` (insertion and reorder, not append-only).
- `src/AxmolHub/Services/ChatWorkspace.cs`: tool-result elision/list replacement (~1243); ApprovalSeen (~2804); plan decisions (~2874); fork prefix (~3102); approval preview/undo/change stamps (~4185–4214); deny/approve/answer (~4273–4430); undo (~4692); edit truncation/regeneration (~4727–4743); elapsed/usage/plan stamping (~5646–5734). These direct writes must be wrapped or reconciled.
- `src/AxmolHub/Services/ConversationRegistry.cs`: gate, ownership and commit boundary. `TryUpdate` saving is not itself proof that messages changed.
- `src/AxmolHub.Core/ConversationStore.cs`: existing JSON remains authoritative and unchanged.
- `src/AxmolHub/Views/ChatPanel.axaml.cs`: future consumer of row deltas instead of classification/pairing inside `RenderMessages`/`DeriveActivityGroup`.

P1 can ship projection and tests alongside the old renderer, comparing normalized row semantics. Only a later step makes ChatPanel consume it. Keep first-paint windowing separate from complete projection semantics: window slices reference stable rows and pair dependencies rather than assigning identities from the 30-turn cut. Do not silently replace today's bounded `WindowEdge` behavior or parked-row limits during P1.

## Test migration without weakening UI coverage

Add pure projection checks to `tests/AxmolHub.Checks/Program.cs` (or a focused linked check file). Test classification/pairing fixtures and operation sequences: append; insert beside a non-tail batch call; supersede unanswered calls; ordering repair; same-count replacement; list replacement/elision; undo; plan review; truncation; fork; equal replacement; duplicate occurrences; reset and revision mismatch; option changes; mutation while a session is not viewed. After every operation, applying its delta to a mirror must equal a fresh full semantic projection. Separately assert stable keys/revisions, unaffected row reuse, and bounded visited/rederived-row counts on normal appends; wall-clock thresholds are not reliable tests.

Retain existing `--verify-shell` checks, rather than replacing them with model assertions:

- `ShellCheckWindow.axaml.cs`: narrated prose and live handoff (~3180, 4368), fold/detail mechanics and geometry (~4693–4813), growing-group control identity and retained expansion (~4850–5006), reasoning/approval/undo surfaces and chips.
- `ShellCheckWindow.TranscriptCost.cs`: eager Markdown bound/promotion, parked control identity, call/result window edge, ApprovalSeen no-op reopen, 30-turn window and load-earlier behavior.
- `ShellCheckWindow.Compacting.cs` and `ShellCheckWindow.ModelSwitch.cs`: elision/summary and notice boundaries remain integration coverage.

Pure tests explain *which semantic rule* failed; shell checks still prove controls, clipping, selectable/rendered content, synchronous switches, focus, actions and layout. Later UI adoption must preserve their control-identity assertions, not retarget them to projection keys alone.

## Limits and verification

This design establishes no measured speedup. Cold JIT/Markdown cost, disk saves before Reload timing, virtualization, pooling, storage segmentation and UI scheduling are outside P1. Complete bootstrap/reconciliation remains O(n); guaranteed incremental cost requires closing the public-list mutation escape hatch or instrumenting every writer. No evidence here justifies a JSON migration.

The repository's shell command is `/opt/homebrew/bin/dotnet run --project src/AxmolHub -- --data-root ./data --verify-shell <report-path>` on this Mac. Use the repository's data root for every app verification. Documentation-only validation and any commands actually run are reported to the parent session; this document does not claim the proposed API has been compiled or implemented.
