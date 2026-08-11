# PascalABC.NET Tooling

Editor-neutral language tooling for PascalABC.NET.

The repository contains two production projects:

- `PascalABCNet.LanguageServices` - document storage and an editor-neutral adapter over the PascalABC.NET semantic and code-completion APIs;
- `PascalABCNet.LanguageServer` - an LSP server using StreamJsonRpc and standard input/output transport.

The PascalABC.NET compiler is included as the `pascalabcnet` Git submodule. The tooling repository does not contain a copied compiler source tree.

## Prerequisites

- .NET 10 SDK;
- Git with submodule support.

## Clone

```sh
git clone --recurse-submodules https://github.com/pascalabcnet/pascalabcnet-tooling.git
```

For an existing clone:

```sh
git submodule update --init --recursive
```

## Build

```sh
dotnet build PascalABCNet.Tooling.slnx
```

## Smoke tests

The headless test exercises the PascalABC.NET semantic APIs without an editor or LSP:

```sh
dotnet run --project HeadlessSmokeTest/HeadlessSmokeTest.csproj
```

The LSP test starts the server as a separate process and verifies initialize, incremental document synchronization, dependency refresh, completion, hover, signature help, shutdown, and exit over stdio:

```sh
dotnet run --project LanguageServerSmokeTest/LanguageServerSmokeTest.csproj
```

## Continuous integration

GitHub Actions checks every pull request and every push to `main`. The workflow checks out the PascalABC.NET submodule, builds the complete solution on Windows with .NET 10, and runs both smoke-test projects.

## Run the language server

```sh
dotnet run --project PascalABCNet.LanguageServer/PascalABCNet.LanguageServer.csproj -- --stdio --documentation-language en
```

`--documentation-language` accepts `en` or `ru`. Stdio is the primary transport and is suitable for VS Code and other editors that can launch an LSP process.

## Architecture

```text
Editor or IDE
    | LSP over stdio
    v
PascalABCNet.LanguageServer
    v
PascalABCNet.LanguageServices
    v
PascalABC.NET (Git submodule)
```

The language-service layer has no dependency on VS Code, LSP DTOs, WinForms, `ICSharpCode.TextEditor`, or the legacy PascalABC.NET workbench. IntelliSense operations are serialized because the underlying PascalABC.NET semantic services are process-global and are not safe for parallel execution.

## Project status

The current foundation provides incremental document synchronization, debounced semantic analysis, dependency-aware refresh of open units, completion after dot, hover, and signature help. The last successful semantic model remains available while the current text is invalid. Diagnostics, definition, references, and editor packaging are planned separately. See [CHANGELOG.md](CHANGELOG.md) for completed milestones.

## License

PascalABC.NET Tooling is licensed under the [GNU Lesser General Public License v3.0](LICENSE) (`LGPL-3.0-only`). PascalABC.NET itself is included as a Git submodule and is distributed under its own license terms.
