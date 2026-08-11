# Changelog

All notable changes to PascalABC.NET Tooling will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Dependency-aware semantic refresh for directly and transitively dependent open units.
- Debounced document analysis with stale/generation tracking and preservation of the last successful semantic model.
- Headless and end-to-end LSP regression coverage for dependency changes, burst updates, stale versions, and incremental ranges.

### Changed

- Replaced full LSP document synchronization with incremental range synchronization.
- Semantic requests now ensure queued document versions are analyzed before returning results.

### Planned

- Diagnostics, definition, references, and additional LSP capabilities.
- Packaging and integration with the PascalABC.NET VS Code extension.

## [0.1.0] - 2026-08-10

### Added

- Editor-neutral `PascalABCNet.LanguageServices` layer.
- Headless document storage and Pascal semantic-service adapter.
- Serialized access to the process-global PascalABC.NET IntelliSense APIs.
- `PascalABCNet.LanguageServer` with LSP over stdio.
- Full document synchronization, completion after dot, hover, and signature help.
- Headless semantic smoke-test.
- End-to-end LSP smoke-test using a separately launched server process.
- PascalABC.NET compiler source as a Git submodule.
- .NET 10 solution for building the complete tooling backend.

### Changed

- Replaced the OmniSharp LSP implementation with StreamJsonRpc and Microsoft LSP protocol DTOs.
- Separated protocol handling from semantic services and document state.

### Removed

- Copied PascalABC.NET monolith sources from the tooling repository.
- Dependencies on VisualPascalABC.NET, WinForms, `ICSharpCode.TextEditor`, `IDocument`, and `TextArea`.
- SPython-specific tooling code.
- Named-pipe transport.

[Unreleased]: https://github.com/pascalabcnet/pascalabcnet-tooling/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/pascalabcnet/pascalabcnet-tooling/releases/tag/v0.1.0
