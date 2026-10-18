# Axmol Hub

<img src="src/AxmolHub/Assets/hub-icon.png" alt="Axmol Hub icon" width="96" />

**Manage Axmol engines, projects, and toolchains — and build software with an AI coding agent.**

The AI assistant is a general-purpose programming and debugging agent. It can map a workspace, inspect and edit files, run commands and tests, diagnose failures, and iterate on a change — including changes to Axmol Hub itself. Approval modes and workspace safeguards govern tool use. Axmol-specific project and toolchain context adds to these general capabilities; it does not limit what the assistant can help with.

[![Powered by OrcaRouter](https://img.shields.io/badge/Powered_by-OrcaRouter-2563eb)](https://www.orcarouter.ai/ref/ref_e3b6a1445aa97ab3c359)

See [Releases](https://github.com/axmolengine/axmol-hub/releases) for published builds. The GUI is C# / .NET 8 / **Avalonia** (`net8.0`); Windows and Linux are validated, while macOS GUIs are not yet verified.

![Axmol Hub main window](docs/images/hub.png)

## What it does

- **Engines and projects** — download or import engines; create C++ or C++ + Lua projects through Axmol's CLI.
- **Build and toolchains** — detect tools using the engine's `1k/build.profiles`; delegate setup to its `setup.ps1` and build, run, and deploy to `axmol build/run/deploy`. See [ADR 0002](docs/adr/0002-toolchain-and-build-delegated-to-engine-cmdline.md).
- **AI coding agent** — Ask, Plan, and Agent modes; workspace mapping, file editing, command execution, and iterative verification, with approval controls. It can work on Hub's own codebase as well as other projects.
- **Settings and logs** — configure language, data and project directories, editor, and providers; view build/run logs in-app and in the data directory.

Platform/architecture support is expressed by a single build-target model (`BuildTargets`), constrained by host OS and engine major version (v3 adds `windows-arm64`, `linux-arm64`, `wasm64`).

## Platform status

**Game target platforms and Hub's own runtime platform are different things.**.

| Game target | Required host | Status |
| --- | --- | --- |
| Windows x64 | Windows | Managed MSVC / SDK; Debug and Release build and Hello World run verified |
| Android ARM64 / x64 | Windows, Linux, macOS | ARM64/x64 Debug and ARM64 Release APK/AAB build, signing, and alignment verified on Windows; on-device pending |
| WebAssembly wasm32 | Windows, Linux, macOS | Real build and local HTTP preview verified on Windows; browser WebGL scene pending |
| Linux x64 | Linux | Build and run entry points wired; native host pending |
| macOS ARM64 / x64 | macOS | Xcode build entry points wired; native host pending |
| iOS / tvOS, device and simulator | macOS | Build plan and unsigned entry points wired; signing, deploy, and on-device run pending |
| UWP / Xbox x64 | Windows | Target management and build plan; isolated toolchain and packaging not done, execution blocked |

The CLI cross-publishes `win-x64`, `linux-x64`, `osx-x64`, and `osx-arm64`; only Windows has been exercised. Android Debug / Release APK and AAB builds are supported; Release uses the project key. See [Android release signing](docs/android-release-signing.md). Installer packages are unsigned, and clean-install verification on Windows 10 / 11 is pending.

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

## Repository layout

```text
src/
  AxmolHub/   Desktop GUI (net8.0, three platforms) and icon
  AxmolHub.Core/  Engine, toolchain, download, state, build, and deploy logic
    Scripts/      Runtime PowerShell wrappers, copied into each client's output
  AxmolHub.Cli/   Host CLI entry point
tests/
  AxmolHub.Checks/  Behavior checks with no external test framework
manifests/        Fixed tool versions, download URLs, and SHA-256
installer/        Velopack packaging, icon conversion, and install-check scripts
licenses/         Third-party license texts
docs/             Design decisions (ADR) and user-facing guides (Chinese)
docs/images/      README screenshots
```

Engines, SDKs, projects, caches, and Git history live outside this source tree. `artifacts/`, `data/`, `bin/`, and `obj/` are excluded by `.gitignore`.

## Reporting issues and contributing

For bug reports, include Hub and engine versions, OS, target, configuration, reproduction steps, and relevant logs. Redact private paths, device serials, and credentials.

Keep changes scoped: UI in `AxmolHub`, shared logic in `Core`, AI capabilities in `AxmolHub.Agent`, and CLI commands reusing `Core`. Put runtime PowerShell scripts in `Core/Scripts/`; provide both Chinese and English for new UI strings. Engine toolchain versions and setup remain owned by Axmol's `1k/build.profiles` and `setup.ps1` (see [ADR 0002](docs/adr/0002-toolchain-and-build-delegated-to-engine-cmdline.md)).

## Privacy policy

Axmol Hub collects no telemetry or personal information. Network access fetches engine/toolchain metadata and selected downloads, and contacts the configured AI provider for sign-in, model lists, and your requests. When enabled, `web_fetch` accesses only the URL shown for approval (HTTPS, up to 1 MiB); scripts and styles are stripped before page text reaches the model. Disabling it in Settings also withholds server-tool declarations. Configured provider-side tools may independently access pages; no built-in provider currently declares one. Troubleshooting logs stay on your machine.

## Code signing policy

Installers are unsigned. Windows may show a SmartScreen warning; macOS may require removing quarantine or notarization, for example: `sudo xattr -r -d com.apple.quarantine axmol-hub-0.12.3-osx-arm64.pkg`.

- Committers and reviewers: [@halx99](https://github.com/halx99)
- Approvers: [@halx99](https://github.com/halx99)

## License

Axmol Hub's code is [MIT](LICENSE). Third-party files and tools retain their own licenses; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
