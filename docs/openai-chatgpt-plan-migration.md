# ChatGPT plan integration migration notes

This document records protocol requirements and the status of Hub's in-progress migration. The product sign-in entry now uses dynamic ChatGPT registration; selected plan credentials use the plan models endpoint and Responses transport through the OpenAI .NET SDK. Credentials saved by the earlier Codex flow are kept as records but never sent anywhere: the provider reports that sign-in is required, and a successful sign-in replaces that record in place. The Codex transport, refresh and model-catalog code has been removed, so no compatibility route remains to fall back to. The migration is not release-ready: real-account acceptance, provenance review and eligibility confirmation are still required, and the passing offline checks in this document are not evidence of real-service interoperability.

## Applicability

OpenAI describes this flow for open-source and locally hosted apps; its overview directs paid or remotely hosted applications to the commercial interest form. Hub's README describes a locally running desktop app and its source is MIT-licensed. This supports assessing the open-source desktop distribution against that flow, but it is not an OpenAI eligibility determination for every distribution or future hosted offering.

## Implementation scope and provenance

The goal is to replace the authentication path and provide the model discovery, token lifecycle and inference support required to use the resulting credential. Saved-account selection and legacy compatibility protect existing credentials; they are not evidence that all migration or account-lifecycle requirements are complete.

The Sign in with ChatGPT DevKit repository has a noncommercial license. Do not copy or depend on its implementation or assets for a commercial distribution without appropriate rights. Hub's C# OAuth implementation is based on the published protocol and does not declare a DevKit package dependency; this observation is not a complete code-provenance audit. The separate OpenAI .NET API SDK has its own license. Before release, review the provenance of code, fixtures and assets and obtain legal advice where necessary. Independent implementation does not itself grant eligibility to use the service in a particular product or distribution.

## Registration and credentials

- Persist one opaque host ID for the host lifecycle before sign-in. A UUIDv4 URI is supported; a JWK thumbprint URI is preferred but optional.
- Register using `dynamic_agent_client`, `agent_name_hint=Axmol Hub`, and the stable `ext_agent_host_id`. Generate independent state, nonce and PKCE verifier for each attempt. Use `http://127.0.0.1:<port>/auth/callback` consistently within that attempt.
- Request `openid profile email offline_access resource.invoke chatgpt.tokens.use.direct`, with resource `https://api.openai.com/v1`. Validate callback state before errors or code. The initial callback must provide the issued client ID; never use `dynamic_agent_client` in the exchange. Reject a different callback client ID when reauthorizing an existing registration.
- Exchange at `https://auth.openai.com/api/accounts/oauth/token` with the issued client ID, code, verifier, exact redirect URI and resource. Validate the ID token signature against OpenAI JWKS and verify issuer, issued-client audience, expiry, nonce and subject before accepting identity. Verify the previous subject on reauthorization.
- Keep each issued client ID and verified account identity separate, even when emails match. Store tokens securely, along with granted scopes and expiry; retain the ID token for optional reauthorization hints. Do not log tokens or authorization URLs containing `id_token_hint`.
- Check the token response's granted scopes before inference. A valid identity without `chatgpt.tokens.use.direct` can be retained, but plan usage must remain disabled and the user offered a way to enable it or use another billing path.
- Refresh with the issued client ID, refresh token and resource, omitting scope. Persist rotating token replacements together and serialize refreshes for the same session, including across processes. On sign-out attempt refresh-token revocation; tell the user if remote revocation could not be confirmed, while retaining the account/client mapping and host ID.

## Inference and user experience

- Fetch the selected account's catalog from `https://api.openai.com/v1/models`; the documented response has a `models` array, with `visibility: list`, `slug`, and `display_name`. Refresh on account changes.
- Send plan-usage inference only to `POST https://api.openai.com/v1/responses`, not ChatGPT backend-api. HTTP requests require `store: false`, `stream: true`, and an input array with the necessary history. Treat only `response.completed` as successful; handle failed, incomplete and interrupted streams.
- The preview excludes several request fields, including `previous_response_id`, `max_output_tokens`, `temperature` and `top_p`, and disallows several hosted tools. Function/custom tools must be grouped in namespaces or supplied through `additional_tools` input items. Consult the linked preview limitations when implementing request conversion rather than assuming the regular API supports the same shape.
- The UI guidelines call for a first-use confirmation, a visible indication that requests use the ChatGPT plan, and a link to ChatGPT usage settings. Usage-limit failures should offer that link, not silently change billing methods or assert a reset time.

## Offline verification

`tests/AxmolHub.Checks --check-openai-oauth` (with and without `--loopback`) and `--verify-shell` cover, against fixtures only: registration reuse and rejection, callback state and issued client ID checks, ID-token/JWKS validation, one exchange per callback, the occupied-port fallback to manual entry, concurrent and two-process refresh rotation, terminal refresh, persistence failure, cancellation, deletion during refresh, endpoint and credential refusals, catalog parsing, request adaptation, streaming completion and the legacy-credential sign-in prompt. None of these checks contact OpenAI, so they say nothing about account eligibility, service availability or the current preview schema.

## Verification still required during implementation

- Validate the exact currently supported response and tool schemas against the current preview documentation and tests, especially multi-turn tool calls and streamed errors.
- Review implementation, test and asset provenance against the DevKit license; confirm the complete distribution and product context qualifies for the open-source flow with OpenAI. Hosted or paid offerings need separate review.
- Confirm structured errors and usage-limit recovery against the real service. Offline handling distinguishes terminal refresh errors from invalid client configuration; Plan consent can be requested explicitly. A recognized HTTP or streaming usage-limit error pauses further requests for that in-memory account until the user resumes them in provider settings; it does not switch billing methods or claim a reset time. The pause does not survive a restart.
- Sign-out persists the verified account/client mapping, clears local tokens before remote revocation, and reports unconfirmed revocation. A credential written by the pre-registration flow is retained but refused: the provider reads as needing sign-in, creates no inference client and is never used for a request, and a successful sign-in switches that same record over rather than adding a second one — a failed save leaves the record, the selection and the prompt as they were. Login persists the mapping before the token, so a crash between the two can leave a signed-out account, not an unreferenced newly issued token. Credential metadata merges unrelated changes and rejects conflicts under a cross-process mutex; provider selection has separate conflict protection. Offline checks cover two actual metadata-writer processes and two processes rotating one token, but secret-store operations, metadata and provider settings are not one crash-atomic transaction; platform-specific crash and concurrent lifecycle tests remain release acceptance work.
- Exercise the full flow with a consenting eligible account; documentation review and offline fixtures cannot establish service availability, account eligibility, or production interoperability.

## Real-account acceptance procedure

Use a consenting account and distribution confirmed eligible for this preview. Do not paste tokens, authorization URLs, ID-token hints or account identifiers into a report.

1. With a clean data root, sign in through the browser, grant plan usage and confirm the expected account and model catalog appear. Send a simple request and a multi-turn request that invokes a Hub tool; confirm both complete.
2. Restart Hub and repeat a request. Sign out, confirm the UI reports whether revocation succeeded, restart and confirm no request can use the old session. Select the retained account, sign in again, and verify it remains the same account and registration.
3. With a separate account or suitable test setup, decline plan usage, confirm inference is disabled, then use the enable-plan action and grant it. Test a known usage limit if available: requests should pause, offer the usage-settings link, and resume only after explicit user action. Do not infer or alter the service reset time.
4. If possible, test refresh after expiry, two Hub processes sharing a data root, network interruptions during login and logout, and platform-specific secret-store lock/failure behavior. Confirm no unintended API-key billing occurs. Record observed failures and sanitized request IDs.
5. Separately complete the provenance/license review and obtain the appropriate OpenAI service-eligibility and commercial-use confirmation for the exact distribution.

## Sources

- https://developers.openai.com/siwc/token-sharing-open-source
- https://developers.openai.com/siwc/token-sharing-open-source/sign-in
- https://developers.openai.com/siwc/token-sharing-open-source/profiles-and-sessions
- https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference
- https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations
- https://developers.openai.com/siwc/token-sharing-open-source/errors-and-recovery
- https://developers.openai.com/siwc/ui-ux-guidelines
- https://developers.openai.com/siwc/website
- https://github.com/openai/sign-in-with-chatgpt-devkit/blob/main/LICENSE
- https://github.com/openai/openai-dotnet/blob/main/LICENSE
