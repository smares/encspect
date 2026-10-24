# Using Encspect from other programs

Encspect ships two programs, the command line tool `encspect` and the GUI `encspect-gui`. The analysis behind them (Core.Abstractions, the host and the format and tag parsers) is kept a library that any front end could use, as the [plan](PLAN.md) requires under "A library others could use", but it is not offered as a library yet. This page lists the ways another program can use the analysis, now and later.

## Today: the command line and its JSON

- Any program, in any language, can run `encspect` and read its JSON output (see [cli.md](cli.md)). This is how tools such as MediaInfo or ffprobe are usually used from other languages.
- The JSON carries `schemaVersion`. Its version changes when a field changes meaning or type or goes away, not when one is added, so a reader that ignores unknown fields keeps working.
- The CSV output serves spreadsheets and simple scripts.
- Nothing more is needed on the caller's side than starting a process and parsing JSON.

## Later: NuGet packages for .NET

A .NET library is shipped as NuGet packages: DLLs of platform-independent code, so one build serves Windows, macOS and Linux on x64 and ARM64. Offering them would take:

- **A deliberate public API.** Today much of the code is public because the front ends and tests use it; the API would be trimmed to what is promised: `Analyzer`, `Report`, the node tree and stream model, the writers and the topic library.
- **Semantic versioning,** with an API-compatibility check in CI so that a release cannot break callers by accident.
- **A stable contract.** The result model still grows with each format phase (category series, decoding verification, the work budget), so packages are best published once the format phases stop extending it, at the earliest after Gate 3.
- **Packages per part:** the core abstractions, the host, and the format and tag parsers, either as one package or one per format. One package is simpler; per-format packages let a caller take only what it needs.

Not planned yet; the plan names it as the way to go when it is.

## Later: a native library for other languages

- .NET's NativeAOT compiles the library to an ordinary native library (`.dll` on Windows, `.so` on Linux, `.dylib` on macOS) with a C interface, which C, C++, Python, Rust, Go and others can load like any C library.
- It works because the analysis has no native dependencies of its own.
- It needs a small C-style API on top: open a file, run the analysis, get the report as JSON or walk the tree through handles, free what was returned. It also needs a build per platform and architecture.
- The JSON route above gives most of the same with no extra work, so this only pays off for callers that cannot start a process or need the analysis in their own process.

## Later: WebAssembly

- A WebAssembly build would let a web page analyze a file in the browser, without uploading it anywhere.
- The analysis reads files as streams and has no native dependencies, so it can run there; the work is in the hosting page and in feeding it a file the browser opened.

## What stays the same in every option

- The rules of `CLAUDE.md` that keep the analysis a library already apply: no UI types, settings, caches or console output below the front ends, and the front ends reach the analysis only through the host's public API.
- Encspect is MIT-licensed, and the shipped code carries only permissive licenses (see [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)), so any of these options can be used from programs under any license.
