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
dotnet pack -c Release -o ReleaseBuilds Mycelium.Model.Forge/Mycelium.Model.Forge.csproj
```

The release helpers live in `.github/scripts/release-tools.cs`, a **.NET 10 file-based app** — one `.cs` file, no `.csproj`, no packages:

```
dotnet run --file .github/scripts/release-tools.cs -- stamp-xmi <xmi-path> <version> <package-id> <release-date>
dotnet run --file .github/scripts/release-tools.cs -- check-deps <nupkg-path>
```

Use the explicit `--file` form: `dotnet run`'s first-argument form only applies when there is no project in the current directory, and the repo root has `mycelium-model.sln`. The file's `#:property TargetFramework=net10.0` directive overrides the repo-root `Directory.Build.props`, which otherwise imposes `netstandard2.0` and leaves the app with no host. The .NET 10 SDK is the only prerequisite — there is no Python dependency.

The packed version comes from the `<Version>` element in the model's own `.csproj`. **Do not add `-p:Version` to a release pack** — `Version` is an MSBuild global property, so it propagates into the `ProjectReference` build of CommonPrimitives and NuGet writes *that* value into Forge's/Fabric's nuspec `<dependency>`, pinning consumers to a CommonPrimitives version that was never published. Adding it to a throwaway local pack is fine as long as you don't trust the resulting dependency version.

There are no unit tests in this repo. To sanity-check a packaging change, `dotnet pack` a single model project and inspect the resulting `.nupkg` (it's a zip) for the `model/`, `build/`, and `buildTransitive/` contents and the nuspec `<dependencies>` block.

Each model project packs independently — always pass a specific `.csproj` path to `dotnet pack`, not the solution.

## Architecture

### Content-only NuGet packaging

Every model project (`Mycelium.Model.{Forge,Fabric,CommonPrimitives}.csproj`) is a `netstandard2.0` SDK project with `IncludeBuildOutput=false` — there's no code, just an `.xmi` packed under `model/` plus two MSBuild props files packed under `build/` and `buildTransitive/`. Those props files expose an absolute path to the packed `.xmi` as an MSBuild property (e.g. `$(MyceliumModelForgeXmiPath)`) to any project that adds a `PackageReference` — no copying or extra restore step needed. Shared repo-wide settings live in `Directory.Build.props` (imported at the top of every project) and `Directory.Build.targets` (imported at the bottom); per-model values (`PackageId`, `Version`, `Description`, etc.) stay in each model's own `.csproj` so release lifecycles stay fully independent.

### Cross-model dependency: Forge/Fabric → CommonPrimitives

Both Forge and Fabric carry a `<ProjectReference>` to `Mycelium.Model.CommonPrimitives.csproj`. Since neither project has real code, this reference compiles nothing — its only purpose is to make `dotnet pack` emit a `<dependency>` on `Mycelium.Model.CommonPrimitives` in the nuspec, with CommonPrimitives' *current* `<Version>` at pack time. **Never add `ReferenceOutputAssembly="false"`** to these references — it silently drops the dependency instead of versioning it (verified empirically). Because CommonPrimitives' props are exposed via `buildTransitive/` (not just `build/`), consumers of Forge/Fabric get `$(MyceliumModelCommonPrimitivesXmiPath)` automatically without referencing CommonPrimitives directly.

"Current `<Version>` at pack time" means the element on disk — **unless the pack passes `-p:Version`, which overrides it.** `Version` is an MSBuild global property and propagates into the referenced project's build, so `dotnet pack -p:Version=9.9.9` on Forge emits `<dependency id="Mycelium.Model.CommonPrimitives" version="9.9.9" />` rather than CommonPrimitives' real `0.2.0` (verified empirically). The release workflow packs without the flag for exactly this reason.

This creates one real ordering constraint on an otherwise fully independent release cadence: whatever CommonPrimitives version Forge/Fabric depend on **must already be published to NuGet.org** before/alongside a Forge/Fabric release, or downstream restores fail. `release-tools.cs check-deps` enforces this — the workflow runs it on the packed `.nupkg` before `dotnet nuget push` and fails the release if any dependency version isn't live.

The `-p:Version` bug shipped real broken packages before it was found: `Mycelium.Model.Forge` 0.1.0 and 0.3.0 on NuGet.org depend on CommonPrimitives 0.1.0 and 0.3.0 respectively, neither of which was ever published (only 0.0.1 and 0.2.0 exist), so neither can be restored. 0.2.0 works only because the two versions happened to coincide.

### XMI version tagging at release time

The release version is carried in the `.xmi` as three OMG MOF extension tags, injected by
`release-tools.cs stamp-xmi` and attached to the model's top-level `uml:Package`:

```xml
<mofext:Tag xmi:id="mycelium-release-version"   name="eu.stariongroup.mycelium.version"     value="1.2.3" element="EAPK_…" />
<mofext:Tag xmi:id="mycelium-release-packageId" name="eu.stariongroup.mycelium.packageId"   value="Mycelium.Model.Forge" element="EAPK_…" />
<mofext:Tag xmi:id="mycelium-release-date"      name="eu.stariongroup.mycelium.releaseDate" value="2026-08-20" element="EAPK_…" />
```

The release workflow runs the script immediately before `dotnet pack` — it must precede pack, since NuGet stages `<None … Pack="true">` content during `Build`, which `Pack` depends on — and then **commits the updated `.xmi` back to the default branch** alongside the `.csproj` and `CITATION.cff` stamps. So the `.xmi` tracked here always reflects the last released version, and a local `dotnet pack` packages that last-released value rather than whatever `-p:Version` it was given.

The script is **add-or-update**: a tag that isn't in the file yet is appended before `</xmi:XMI>`; one that's already there is rewritten where it stands. So the first release adds four lines (the `xmlns:mofext` declaration plus three tags) and every release after that touches only the `value` attributes that actually changed. Re-rendering the whole element rather than patching `value` also refreshes `element` if an EA re-export gave the top-level package a fresh GUID.

Two details in the script are load-bearing:

- It does **text substitution on a `latin-1` round-trip**, not an `XDocument` parse/serialise. EA's exporter writes files that declare `encoding="utf-8"` while the bytes may be anything, so no validating decoder can be trusted; `latin-1` maps all 256 byte values one-to-one and round-trips losslessly whatever the file actually holds (windows-1252 leaves five undefined, utf-8 rejects invalid sequences). Avoiding a re-serialise keeps EA's tab indentation, attribute order and self-closing tags byte-identical. The injected content is pure ASCII. Note `mycelium-commonprimitives.xmi` carries a stray `EF BF BD` (U+FFFD) in the `byte` datatype's comment — a replacement character from some earlier tool's lossy decode; the round-trip preserves it rather than compounding it, but it is real data loss worth repairing in EA.
- The `element` idref is **derived, not configured** — it's the `xmi:id` of the first element typed `xmi:type="uml:Package"`, which resolves to the top-level package for both shapes shipped here (a `packagedElement` under `uml:Model` for Forge/Fabric, a root `uml:Package` for CommonPrimitives). Nothing needs updating after an EA re-export assigns fresh GUIDs. If no such element exists the script exits non-zero and fails the release loudly.

This replaced an earlier MSBuild task that stamped `version` on the `<project>`/`<packageproperties>` elements inside `<xmi:Extension extender="Enterprise Architect">`. That mechanism was unusable for `mycelium-commonprimitives.xmi`, which is hand-authored and has no EA extension block at all, so every `commonprimitives` release failed at pack. `mofext:Tag` is plain OMG XMI and works regardless of where a model was authored.

### Releasing (`.github/workflows/nuget-release.yml`)

Releases are manual (`workflow_dispatch`) with two inputs: `model` (`forge`/`fabric`/`commonprimitives`) and `version` (SemVer). A run, for the selected model only:

1. Validates the version is SemVer, sets prerelease flag if it has a `-` suffix.
2. Updates `CITATION.cff`'s top-level and `preferred-citation` `version`/`date-released` fields.
3. Stamps `<Version>` into the model's `.csproj`, failing the run if the substitution didn't take — that element is the sole source of the published version.
4. Adds or updates the `mofext:Tag` release tags in the model's `.xmi` (see above), then packs the selected model's `.csproj` — **without `-p:Version`** (see the cross-model dependency section) — and asserts the produced file is `<PackageId>.<version>.nupkg`.
5. Commits the stamped `.xmi` + the `.csproj` `<Version>` stamp + the `CITATION.cff` change directly to the default branch and pushes.
6. Verifies every nuspec `<dependency>` version is already live on NuGet.org, then pushes the `.nupkg` to NuGet.org.
7. Tags the commit `<model>/<version>` (e.g. `forge/1.0.0`) and pushes the tag.
8. Drafts a GitHub release (title `<PackageId> <version>`) with the `.nupkg` attached.

Releasing one model never touches another's version, tag, or package. **Concurrency caveat:** the direct-push-to-default-branch step means two releases triggered near-simultaneously can hit a non-fast-forward conflict and need re-running — sequential releases (the normal case) are fine.

`CITATION.cff` is a single shared file across all three models, so its `version` always reflects whichever model released *most recently* — not any one model's version. Use git tags/package versions for a specific model's version.

### Replacing a placeholder xmi with the real model

Fabric and CommonPrimitives currently ship bare-bones placeholder `.xmi` files (a `Model` package with one empty top-level package) so the packaging/release plumbing works ahead of real modeling. To swap in the real EA export:

1. Export from EA and overwrite the placeholder `.xmi` in place.
2. Nothing else needs to change (`PackageId`, props files, solution reference, workflow's `model` option already work). The release tags' `element` idref is derived from the file itself, so the fresh GUIDs EA assigns need no follow-up edit; the next release re-adds the tags the export dropped.

### Adding a fourth+ model

1. Copy an existing model's `.csproj`/folder layout as a template; add the new `.xmi`.
2. Add `build/Mycelium.Model.<Name>.props` and `buildTransitive/Mycelium.Model.<Name>.props` exposing `$(MyceliumModel<Name>XmiPath)`.
3. `dotnet sln mycelium-model.sln add Mycelium.Model.<Name>/Mycelium.Model.<Name>.csproj`.
4. Add a lowercase `<name>` case to the `model` selector in `.github/workflows/nuget-release.yml`.

## Contribution workflow

Branch from `development` (never work on or PR directly from `master`/`development`); rebase onto `development` before sending a PR so history stays linear. Full details in `.github/CONTRIBUTING.md`.
