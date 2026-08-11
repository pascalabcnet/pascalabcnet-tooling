# Repository guardrails

## PascalABC.NET is read-only by default

The `pascalabcnet/` directory is an external Git submodule. Any standalone
PascalABC.NET checkout is also outside the scope of ordinary tooling work.

- Do not edit, format, generate files in, commit in, reset, switch, merge,
  rebase, or push the PascalABC.NET submodule or a standalone PascalABC.NET
  checkout.
- Do not update the submodule gitlink.
- Treat differences from legacy IntelliSense behavior as tooling bugs first.
- A PascalABC.NET change requires explicit user approval for a precisely
  described change in precisely named files. General approval to fix tooling,
  continue work, or run tests is not approval to change PascalABC.NET.
- Before requesting such approval, demonstrate a minimal failure independent
  of tooling and explain why it cannot be fixed in LanguageServices or the
  LanguageServer.
- Commit and push operations in PascalABC.NET require separate explicit user
  approval.

The legacy PascalABC.NET editor is the behavioral reference for completion,
hover, and signature help. Compare the same Pascal text, caret position,
culture, library paths, and document-update sequence.
