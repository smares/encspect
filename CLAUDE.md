# Encspect

Audio file analyzer with a GUI and a CLI. Read `docs/PLAN.md` for goals, architecture, scope and rollout before making design decisions; this file only holds the rules that apply to every change.

## Stack

C# on .NET, Avalonia UI (GUI), console app (CLI), one shared core library, one project per format. No playback, no runtime-loadable plugins, no bundled native libraries.

## Architecture rules

- Parsers depend only on `Core.Abstractions` (parser interface, result model, byte and bit readers). They never reference each other and never reference a decoder.
- Tag parsers (ID3, APE, Lyrics3) are separate from format parsers. The host orchestrates detection and assembles one report.
- The result model is a node tree; every node carries a name, value, byte offset and length. Findings carry a severity and point to a node.
- Malformed or damaged input produces findings, never an unhandled exception and never a hang. Files are read as streams, not loaded whole.
- Tag detection uses position, structural validation and byte-range ownership, never a signature match alone. APE is evaluated before Lyrics3, and Lyrics3 before ID3v1. See "Tag detection and false positives" in the plan.
- A frame header only counts as MPEG sync when a chain of consistent frames follows it.

## Code style

- Comments only for non-obvious decisions and constraints, or to cite the spec section a field implements. Do not comment what the code already says.
- Booleans that appear in the GUI or in JSON output read as booleans (`IsVbr`, `HasLameTag`), not as nouns.
- American English everywhere, including comments, docs and prose (-ize, not -ise). Write "ID" capitalized in comments when it means the word.

## Verification

- Run the checks the CI workflow runs, locally, before calling a change done. Mirror the pipeline instead of picking a subset. If the .NET SDK is not available in the environment, say so and rely on the CI run.
- Parsers are verified against known-input fixtures, cross-checks with other tools, golden reports and corrupt-file tests, as described in the plan. A parser change without a fixture or golden-report update is incomplete.
- After each milestone, an independent review agent that has not seen the author's conclusions reviews the code against the spec.

## Git

- Never push unless explicitly told to for that specific push. Commit locally and stop.
- Nothing merges with a red pipeline.
