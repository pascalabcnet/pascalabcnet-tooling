# Changelog

All notable changes to PascalABC.NET Tooling will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Editor-neutral compiler controller and isolated compiler worker for .NET Framework 4.7.2 and .NET 10.
- Reproducible compiler-host build script and end-to-end controller smoke tests.
- Documentation for the JSON Lines controller protocol and worker lifecycle.
- Optional `runtimeModule` compile field using PascalABC.NET standard-module injection for IDE runtime services.
- End-to-end .NET 10 coverage for the existing `__RedirectIOMode` stream protocol.
- Optional per-request `sourceFiles` snapshots for unsaved main programs and units.
- Cross-target regression coverage for virtual sources, disk fallback, stale PCUs,
  module diagnostics, and `runtimeModule` combined with an in-memory snapshot.

### Changed

- Compiler-host ownership moved from the VS Code extension to the shared Tooling repository.
- Canonical compiler-host projects, smoke tests, build scripts, and protocol
  documentation moved to the PascalABC.NET submodule; Tooling now consumes
  them without keeping source copies.
- Replaced the internal Controller-to-Worker loopback NetMQ transport with
  JSON Lines over redirected standard input/output while preserving the public
  controller protocol and command line.
- Worker stdout is now reserved for protocol traffic; diagnostics use stderr.

### Removed

- NetMQ and its unused AsyncIO and NaCl transport dependencies.

### Fixed

- Added bounded recovery coverage for worker crashes and hangs, large Cyrillic
  requests, stderr isolation, and coordinated Controller/Worker shutdown.
- Increased the .NET Framework JSON size limit so large document snapshots are
  handled consistently with .NET 10.

### Planned

- Diagnostics, definition, references, and additional LSP capabilities.
- Packaging and integration with the PascalABC.NET VS Code extension.

## [0.1.2] - 2026-08-11

### Added

- Windows GitHub Actions CI for the complete Release build and both smoke-test projects.
- Regression coverage for implementation-only dependencies, document close recovery, and simultaneous virtual documents.

### Fixed

- Dependency refresh now includes units referenced from `implementation uses` and propagates those changes transitively.
- Closing a document now removes its unsaved global semantic cache entry, restores the disk model when available, and refreshes open dependents.
- Non-file document URIs now receive stable unique synthetic Pascal file names instead of sharing `Untitled.pas`.

## [0.1.1] - 2026-08-11

### Added

- Dependency-aware semantic refresh for directly and transitively dependent open units.
- Debounced document analysis with stale/generation tracking and preservation of the last successful semantic model.
- Headless and end-to-end LSP regression coverage for dependency changes, burst updates, stale versions, and incremental ranges.

### Changed

- Replaced full LSP document synchronization with incremental range synchronization.
- Semantic requests now ensure queued document versions are analyzed before returning results.

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

[Unreleased]: https://github.com/pascalabcnet/pascalabcnet-tooling/compare/v0.1.2...HEAD
[0.1.2]: https://github.com/pascalabcnet/pascalabcnet-tooling/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/pascalabcnet/pascalabcnet-tooling/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/pascalabcnet/pascalabcnet-tooling/releases/tag/v0.1.0
