# mycelium-model

The UML 2.5.1 models for Mycelium: CommonPrimitives, Fabric and Forge.

Each model lives in its own project folder (`mycelium-model.sln`) and is distributed as its own independently-versioned NuGet package, so each model's release lifecycle (version, release notes, release cadence) is fully independent of the others:

| Model | Project | Package | Status |
|---|---|---|---|
| Forge | `Mycelium.Model.Forge/` | `Mycelium.Model.Forge` | modeled — real content in `mycelium-forge.xmi` |
| Fabric | `Mycelium.Model.Fabric/` | `Mycelium.Model.Fabric` | scaffolding only — bare-bones placeholder `mycelium-fabric.xmi` |
| CommonPrimitives | `Mycelium.Model.CommonPrimitives/` | `Mycelium.Model.CommonPrimitives` | scaffolding only — bare-bones placeholder `mycelium-commonprimitives.xmi` |

Fabric and CommonPrimitives are fully wired up (csproj, packaging, version stamping, workflow release option) ahead of any real modeling, so dropping in the real EA export is a two-step swap — see "Replacing a placeholder xmi with the real model" below.

## Consuming a model package

Each package ships its `.xmi` file under a `model/` folder inside the package and contains no compiled code — it's a data-only package meant to be consumed by a `uml4net`-based code-generation project.

Adding a `PackageReference` automatically imports an MSBuild property pointing at the `.xmi` file's absolute path inside the local NuGet package cache. No copying or extra restore step is required — the property is available immediately after `dotnet restore`.

```xml
<PackageReference Include="Mycelium.Model.Forge" Version="1.0.0" />

<Target Name="GenerateFromModel" BeforeTargets="CoreCompile">
  <Exec Command="dotnet run --project ../CodeGen -- --model &quot;$(MyceliumModelForgeXmiPath)&quot; --out $(IntermediateOutputPath)generated" />
</Target>
```

Available properties:

| Model | Package | MSBuild property |
|---|---|---|
| Forge | `Mycelium.Model.Forge` | `$(MyceliumModelForgeXmiPath)` |
| Fabric | `Mycelium.Model.Fabric` | `$(MyceliumModelFabricXmiPath)` |
| CommonPrimitives | `Mycelium.Model.CommonPrimitives` | `$(MyceliumModelCommonPrimitivesXmiPath)` |

## Model dependencies

Forge and Fabric both depend on CommonPrimitives. That's expressed as a real NuGet package dependency, not just a UML cross-reference: `Mycelium.Model.Forge.csproj` and `Mycelium.Model.Fabric.csproj` each carry a `<ProjectReference>` to `Mycelium.Model.CommonPrimitives.csproj`, and `dotnet pack` turns that into a `<dependency>` entry in the resulting nuspec.

- A consumer that adds `<PackageReference Include="Mycelium.Model.Forge" ... />` automatically gets `Mycelium.Model.CommonPrimitives` restored transitively too — and, because CommonPrimitives' MSBuild property comes from its `buildTransitive/` props file (not just `build/`), `$(MyceliumModelCommonPrimitivesXmiPath)` is available to that consumer without them ever referencing CommonPrimitives directly. That's what lets a uml4net-based generator resolve cross-model references between Forge's/Fabric's model and CommonPrimitives'.
- The dependency version written into Forge's/Fabric's nuspec is CommonPrimitives' `<Version>` **as it stands in `Mycelium.Model.CommonPrimitives.csproj` on disk** at the moment they're packed. This is why the release workflow stamps each model's own `<Version>` element back into its `.csproj` on every release (see "Releasing" below) — without that, CommonPrimitives' csproj could go stale after a real release and a later Forge/Fabric release would bake in an outdated (understated) dependency constraint.
- **Never pack a release with `-p:Version`.** `Version` is an MSBuild *global* property, so it propagates into the `ProjectReference` build of CommonPrimitives and NuGet resolves the nuspec `<dependency>` against *that* value instead of CommonPrimitives' own `<Version>`. Verified empirically: `dotnet pack -p:Version=9.9.9` on Forge emits `<dependency id="Mycelium.Model.CommonPrimitives" version="9.9.9" />`, while the same pack without the flag correctly emits `version="0.2.0"`. The release workflow therefore packs with no `-p:Version` and takes the version from the `.csproj` element it stamped moments earlier. This bug shipped in real packages — `Mycelium.Model.Forge` 0.1.0 and 0.3.0 on NuGet.org each depend on a CommonPrimitives version that was never published, and cannot be restored.
- Every release now runs `.github/scripts/release-tools.cs check-deps` against the packed `.nupkg` before `dotnet nuget push`, failing the run if any nuspec `<dependency>` names a version that isn't on NuGet.org. The ordering constraint in the next bullet is therefore enforced, not merely documented.
- **That exact version of `Mycelium.Model.CommonPrimitives` must already be published to NuGet.org** before (or in the same release batch as) a Forge/Fabric release that references it — otherwise consumers restoring Forge/Fabric get an unresolved-dependency restore failure. This is the one real ordering constraint on an otherwise independent release cadence: Forge/Fabric can still bump their own version freely without touching CommonPrimitives, but bumping *which* CommonPrimitives version they depend on requires CommonPrimitives to have shipped that version first.
- Do **not** add `ReferenceOutputAssembly="false"` to these `<ProjectReference>` items — it silently suppresses nuspec dependency generation entirely (verified empirically: NuGet just drops the reference instead of turning it into a versioned dependency). The reference is otherwise harmless either way — neither project has any code to compile, so no real assembly gets linked.

## Releasing

Releases are cut manually via the `Nuget-Release` GitHub Actions workflow (`workflow_dispatch`), which takes:

- **model** — `forge`, `fabric`, or `commonprimitives`. Selects which model's project gets packed and released; the other two are untouched.
- **version** — a SemVer string for that model's next release (e.g. `1.2.3` or `1.2.3-beta.1`).

A run packs only the selected model's project, stamps that version into the model's own `.xmi` (as OMG MOF extension tags — see "Version tags in the xmi" below) **and** into the `<Version>` element of the model's own `.csproj`, commits both stamps back to the default branch, pushes the single resulting `.nupkg` to NuGet.org, tags the commit, and drafts a GitHub release with the `.nupkg` attached. Because each model is packed and tagged independently, releasing Forge never touches Fabric's or CommonPrimitives' version, tag, or package — releasing different models is safe to do independently, on independent schedules.

### Version tags in the xmi

`.github/scripts/release-tools.cs stamp-xmi` writes three `mofext:Tag` elements onto the model's top-level `uml:Package`, declaring `xmlns:mofext="http://www.omg.org/spec/MOF/20131001"` on the root `<xmi:XMI>` if it isn't there already:

```xml
<mofext:Tag xmi:id="mycelium-release-version"   name="eu.stariongroup.mycelium.version"     value="1.2.3" element="EAPK_…" />
<mofext:Tag xmi:id="mycelium-release-packageId" name="eu.stariongroup.mycelium.packageId"   value="Mycelium.Model.Forge" element="EAPK_…" />
<mofext:Tag xmi:id="mycelium-release-date"      name="eu.stariongroup.mycelium.releaseDate" value="2026-08-20" element="EAPK_…" />
```

Missing tags are appended; tags already present are updated in place, so the first release adds four lines and later ones change only the values that moved. The script runs just before `dotnet pack` (NuGet stages the packaged `.xmi` during `Build`, so a later edit would miss the package), and the result is committed — the `.xmi` in this repo always reflects the last released version.

The `element` idref is derived from the file: the `xmi:id` of the first element typed `xmi:type="uml:Package"`. Nothing needs configuring per model, and an EA re-export that assigns fresh GUIDs needs no follow-up edit. If no such element exists the script exits non-zero and fails the release loudly.

This replaced an MSBuild task that stamped the `version` attribute inside `<xmi:Extension extender="Enterprise Architect">`. `mycelium-commonprimitives.xmi` is hand-authored and has no EA extension block, so that mechanism made every `commonprimitives` release fail at pack; `mofext:Tag` is plain OMG XMI and works for any model regardless of authoring tool.

The `.csproj`'s own `<Version>` element is the single source of the published version — the workflow packs without `-p:Version` deliberately, and fails the run if the stamp didn't take or if the resulting `.nupkg` isn't named for the requested version. It's also the value any *other* model's `<ProjectReference>` resolves against when computing its nuspec dependency version, which is exactly why `-p:Version` must not be used — see "Model dependencies" above.

**Concurrency caveat:** the "commit version stamps" step pushes directly to the default branch. If two release runs (e.g. `forge` and `fabric`) are triggered within moments of each other, the second one's push can hit a non-fast-forward conflict against the first's commit and need to be re-run. This isn't a design flaw — sequential independent releases (the normal case) work cleanly — just don't expect two releases to succeed from truly simultaneous runs.

**`CITATION.cff`:** a run also stamps the released `version` and `date-released` into `CITATION.cff` (both the top-level fields and the `preferred-citation` block) and commits that alongside the xmi version stamp. Since `CITATION.cff` is one shared file across three independently-versioned models, its `version` always reflects whichever model was released *most recently*, not any single model's version — the same convention `uml4net` uses for its own multi-package `CITATION.cff`. If you need to know a specific model's version, use its own tag/package version, not this file.

### Tagging convention

Tags are namespaced per model as `<model>/<version>`, using the same lowercase `model` value as the workflow's selector — e.g. `forge/1.0.0`, `fabric/0.2.0`, `commonprimitives/0.1.0`. This keeps each model's release history independently listable (`git tag -l 'forge/*'`) and avoids the ambiguity of a single shared tag standing in for three independently-versioned packages. The GitHub release title uses the full package id instead, e.g. `Mycelium.Model.Forge 1.0.0`.

## Replacing a placeholder xmi with the real model

Fabric and CommonPrimitives currently ship a bare-bones placeholder `.xmi` (just a `Model` package containing one empty top-level package) so the packaging/release plumbing can be exercised before any real modeling happens. To drop in the real model once it's ready in Enterprise Architect:

1. Export the real model from EA and overwrite the placeholder file in place (e.g. `Mycelium.Model.Fabric/mycelium-fabric.xmi`).
2. Everything else — the project's `PackageId`, the `build`/`buildTransitive` props, the solution reference, the workflow's model option — already works unchanged. The release tags' `element` idref is derived from the file, so the fresh GUID EA assigns needs no follow-up edit, and the next release re-adds the tags the export overwrote.

## Adding an entirely new (fourth+) model

1. Create `Mycelium.Model.<Name>/Mycelium.Model.<Name>.csproj` (copy an existing model's `.csproj` as a template) and add its `.xmi` alongside it.
2. Add `build/Mycelium.Model.<Name>.props` and `buildTransitive/Mycelium.Model.<Name>.props` exposing `$(MyceliumModel<Name>XmiPath)`.
3. Add the project to `mycelium-model.sln` (`dotnet sln mycelium-model.sln add Mycelium.Model.<Name>/Mycelium.Model.<Name>.csproj`).
4. Add a `<name>` case (lowercase) to the `model` selector in `.github/workflows/nuget-release.yml`.
