# Axmol Hub

<img src="src/AxmolHub/Assets/hub-icon.png" alt="Axmol Hub icon" width="96" />

**Manage Axmol engines, projects, and toolchains — and use a general-purpose AI agent to build software.**

The assistant can map a workspace, inspect and edit files, run commands, and iterate on code or documentation. Ask, Plan, and Agent modes provide different levels of action, with approval controls and workspace safeguards. Axmol project and toolchain context adds engine-specific help without limiting other programming work.

[![Powered by OrcaRouter](https://img.shields.io/badge/Powered_by-OrcaRouter-2563eb)](https://www.orcarouter.ai/ref/ref_e3b6a1445aa97ab3c359)

See [Releases](https://github.com/axmolengine/axmol-hub/releases) for published builds. The desktop app uses C# / .NET 8 / **Avalonia** (`net8.0`) and is validated on Windows, Linux, and macOS.

![Axmol Hub main window](docs/images/hub.png)

## What it does

- **Engines and projects** — download or import engines; create C++ or C++ + Lua projects through Axmol's CLI.
- **Build and toolchains** — detect tools using the engine's `1k/build.profiles`; delegate setup to its `setup.ps1` and build, run, and deploy to `axmol build/run/deploy`. See [ADR 0002](docs/adr/0002-toolchain-and-build-delegated-to-engine-cmdline.md).
- **AI assistant** — Ask, Plan, and Agent modes for questions, planned changes, and hands-on work across codebases and documents.
- **Settings and logs** — configure language, data and project directories, editor, and providers; view build/run logs in-app and in the data directory.

Available game targets depend on the host OS and Axmol engine version. Axmol v3 adds Windows ARM64, Linux ARM64, and WebAssembly 64-bit targets.

## Game targets

| Game target | Required host | Status |
| --- | --- | --- |
| Windows x64 / ARM64 | Windows | Debug and Release builds; Hello World run verified on x64 |
| Android ARM64 / x64 | Windows, Linux, macOS | APK/AAB builds supported; device deployment requires an authorized device |
| WebAssembly wasm32 / wasm64 | Windows, Linux, macOS | wasm32 build and local HTTP preview verified on Windows |
| Linux x64 / ARM64 | Linux | Build targets available; see Axmol toolchain requirements |
| macOS ARM64 / x64 | macOS | Build targets available; see Axmol toolchain requirements |
| iOS / tvOS | macOS | Build entry points available; signing and device deployment require Apple tooling |
| UWP / Xbox x64 | Windows | Target support is limited; toolchain and packaging work remains |

Android Release builds use the project's signing key. See [Android release signing](docs/android-release-signing.md). Installers are unsigned.

## Quick start

Use a published Windows installer and choose a writable install directory. It bundles the .NET runtime; engines and development tools are downloaded when selected.

1. In Settings, choose the data directory, project directory, and language.
2. Install an engine and prepare its toolchain on the **Toolchains** page.
3. Create a C++ or C++ + Lua project, then choose a target and configuration to build.
4. Open the build output or run it from Hub. Android deployment needs a connected device and explicit serial.

MSVC installation uses Microsoft's installer and may request elevation. Hub does not modify global environment variables or Git config; engine setup runs with `-hub`, though on Windows it may change the current user's PowerShell execution policy and request elevation.

## Building from source

The full guide covers prerequisites, verification commands, the CLI contract, and packaging: [Building from source](docs/building-from-source.md). Use a recent .NET SDK; **.NET 10 SDK is recommended** for the Avalonia source generators. The app targets `net8.0` and can run with .NET 8 or a newer runtime. See [CI details](docs/ci.md) for SDK/runtime policy.

```powershell
dotnet build src/AxmolHub/AxmolHub.csproj -c Release
dotnet run --project src/AxmolHub -- --data-root ./data
```

The `--data-root ./data` option keeps development runs on the repository's local data directory. The CLI's `--json` contract is documented in [docs/cli-json-contract.md](docs/cli-json-contract.md).

Source lives in `src/`; checks, pinned tool versions, installer scripts, licenses, and guides live in `tests/`, `manifests/`, `installer/`, `licenses/`, and `docs/`. Engines, SDKs, projects, and caches live outside the source tree. `artifacts/`, `data/`, `bin/`, and `obj/` are ignored by Git.

## Reporting issues and contributing

For bug reports, include Hub and engine versions, OS, target, configuration, reproduction steps, and relevant logs; redact private paths, device serials, and credentials. Keep UI in `AxmolHub`, shared logic in `Core`, and runtime PowerShell scripts in `Core/Scripts/`. Axmol owns engine toolchain versions and setup through `1k/build.profiles` and `setup.ps1` ([ADR 0002](docs/adr/0002-toolchain-and-build-delegated-to-engine-cmdline.md)).

## Privacy policy

Axmol Hub collects no telemetry. It contacts configured AI providers for sign-in, model lists, and your requests, and downloads engine/toolchain metadata and selected files. Web search is off by default; when enabled, Hub selects the route configured for the provider. DeepSeek Search/Fetch API calls may incur per-request charges, and provider-hosted search may also be billed by that provider. Hub's page-fetch tool is controlled separately, limited to public HTTPS URLs and 1 MiB, and strips scripts and styles before returning page text. Logs stay on your machine.

## Code signing policy

Installers are unsigned. Windows may show a SmartScreen warning, and macOS may show a Gatekeeper warning.

- Committers and reviewers: [@halx99](https://github.com/halx99)
- Approvers: [@halx99](https://github.com/halx99)

## License

Axmol Hub's code is [MIT](LICENSE). Third-party files and tools retain their own licenses; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
