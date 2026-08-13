# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

This is a **data-only** repository: three UML 2.5.1 models (Enterprise Architect `.xmi` exports), each packaged and released independently as a NuGet package for consumption by `uml4net`-based code generators. There is no application code, no compiled logic, and no tests to run — the "source" is the `.xmi` files, and the "build" is packaging plumbing (MSBuild + a GitHub Actions release workflow).

| Model | Project | Package | Status |
|---|---|---|---|
| Forge | `Mycelium.Model.Forge/` | `Mycelium.Model.Forge` | modeled — real content in `mycelium-forge.xmi` |
| Fabric | `Mycelium.Model.Fabric/` | `Mycelium.Model.Fabric` | scaffolding only — placeholder `mycelium-fabric.xmi` |
| CommonPrimitives | `Mycelium.Model.CommonPrimitives/` | `Mycelium.Model.CommonPrimitives` | scaffolding only — placeholder `mycelium-commonprimitives.xmi` |

Each model's `.xmi` is edited exclusively in Enterprise Architect (EA), not by hand. Claude Code's role here is almost entirely about the **packaging/versioning/release plumbing**, not the modeled content itself.

## Commands

```
dotnet restore mycelium-model.sln
dotnet build mycelium-model.sln
dotnet pack -c Release -o ReleaseBuilds -p:Version=1.2.3 Mycelium.Model.Forge/Mycelium.Model.Forge.csproj
```

There are no unit tests in this repo. To sanity-check a packaging change, `dotnet pack` a single model project and inspect the resulting `.nupkg` (it's a zip) for the `model/`, `build/`, and `buildTransitive/` contents and the nuspec `<dependencies>` block.

Each model project packs independently — always pass a specific `.csproj` path to `dotnet pack`, not the solution.

## Architecture

### Content-only NuGet packaging

Every model project (`Mycelium.Model.{Forge,Fabric,CommonPrimitives}.csproj`) is a `netstandard2.0` SDK project with `IncludeBuildOutput=false` — there's no code, just an `.xmi` packed under `model/` plus two MSBuild props files packed under `build/` and `buildTransitive/`. Those props files expose an absolute path to the packed `.xmi` as an MSBuild property (e.g. `$(MyceliumModelForgeXmiPath)`) to any project that adds a `PackageReference` — no copying or extra restore step needed. Shared repo-wide settings live in `Directory.Build.props` (imported at the top of every project) and `Directory.Build.targets` (imported at the bottom); per-model values (`PackageId`, `Version`, `Description`, etc.) stay in each model's own `.csproj` so release lifecycles stay fully independent.

### Cross-model dependency: Forge/Fabric → CommonPrimitives

Both Forge and Fabric carry a `<ProjectReference>` to `Mycelium.Model.CommonPrimitives.csproj`. Since neither project has real code, this reference compiles nothing — its only purpose is to make `dotnet pack` emit a `<dependency>` on `Mycelium.Model.CommonPrimitives` in the nuspec, with CommonPrimitives' *current* `<Version>` at pack time. **Never add `ReferenceOutputAssembly="false"`** to these references — it silently drops the dependency instead of versioning it (verified empirically). Because CommonPrimitives' props are exposed via `buildTransitive/` (not just `build/`), consumers of Forge/Fabric get `$(MyceliumModelCommonPrimitivesXmiPath)` automatically without referencing CommonPrimitives directly.

This creates one real ordering constraint on an otherwise fully independent release cadence: whatever CommonPrimitives version Forge/Fabric depend on **must already be published to NuGet.org** before/alongside a Forge/Fabric release, or downstream restores fail.

### XMI version stamping at pack time

`Directory.Build.targets` defines a `StampModelVersionInXmi` MSBuild task that does a **scoped regex text substitution** on the `.xmi` file (not a full XML load/save) to set the `version` attribute on the `<project>`/`<packageproperties>` element matching a given `xmi:idref`, using `windows-1252` encoding to match EA's own export format byte-for-byte. This runs `BeforeTargets="Build"` (i.e. before NuGet's Pack-dependency chain stages content) so the stamped version makes it into the `.nupkg`.

Each model's `.csproj` supplies a `MyceliumModelVersionTarget` item pairing its `.xmi` path with the specific EA package `xmi:idref` to stamp (e.g. `EAPK_7BB26FE0_2515_41d2_BE26_0C544013AEEA` for Forge). If that `idref` doesn't match anything in the `.xmi`, the pack fails loudly rather than silently mis-stamping or skipping — this matters when swapping a placeholder xmi for a real EA export (see below), since EA assigns a fresh GUID.

### Releasing (`.github/workflows/nuget-release.yml`)

Releases are manual (`workflow_dispatch`) with two inputs: `model` (`forge`/`fabric`/`commonprimitives`) and `version` (SemVer). A run, for the selected model only:

1. Validates the version is SemVer, sets prerelease flag if it has a `-` suffix.
2. Updates `CITATION.cff`'s top-level and `preferred-citation` `version`/`date-released` fields.
3. `dotnet pack -p:Version=<version>` the selected model's `.csproj` (this triggers the XMI stamping above).
4. Commits the stamped `.xmi` + `CITATION.cff` change directly to the default branch and pushes.
5. Pushes the `.nupkg` to NuGet.org.
6. Tags the commit `<model>/<version>` (e.g. `forge/1.0.0`) and pushes the tag.
7. Drafts a GitHub release (title `<PackageId> <version>`) with the `.nupkg` attached.

Releasing one model never touches another's version, tag, or package. **Concurrency caveat:** the direct-push-to-default-branch step means two releases triggered near-simultaneously can hit a non-fast-forward conflict and need re-running — sequential releases (the normal case) are fine.

`CITATION.cff` is a single shared file across all three models, so its `version` always reflects whichever model released *most recently* — not any one model's version. Use git tags/package versions for a specific model's version.

### Replacing a placeholder xmi with the real model

Fabric and CommonPrimitives currently ship bare-bones placeholder `.xmi` files (a `Model` package with one empty top-level package) so the packaging/release plumbing works ahead of real modeling. To swap in the real EA export:

1. Export from EA and overwrite the placeholder `.xmi` in place.
2. Update `PackageIdRef` in that model's `.csproj` (`MyceliumModelVersionTarget` item) to the real top-level package's `xmi:idref` — EA assigns a fresh GUID, so the placeholder's `idref` won't match. Forgetting this makes `dotnet pack` fail loudly rather than mis-stamp.
3. Nothing else needs to change (`PackageId`, props files, solution reference, workflow's `model` option already work).

### Adding a fourth+ model

1. Copy an existing model's `.csproj`/folder layout as a template; add the new `.xmi`.
2. Add `build/Mycelium.Model.<Name>.props` and `buildTransitive/Mycelium.Model.<Name>.props` exposing `$(MyceliumModel<Name>XmiPath)`.
3. `dotnet sln mycelium-model.sln add Mycelium.Model.<Name>/Mycelium.Model.<Name>.csproj`.
4. Add a lowercase `<name>` case to the `model` selector in `.github/workflows/nuget-release.yml`.

## Contribution workflow

Branch from `development` (never work on or PR directly from `master`/`development`); rebase onto `development` before sending a PR so history stays linear. Full details in `.github/CONTRIBUTING.md`.
