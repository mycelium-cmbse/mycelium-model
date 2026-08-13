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
- The dependency version written into Forge's/Fabric's nuspec is CommonPrimitives' `<Version>` at the moment they're packed. **That exact version of `Mycelium.Model.CommonPrimitives` must already be published to NuGet.org** before (or in the same release batch as) a Forge/Fabric release that references it — otherwise consumers restoring Forge/Fabric get an unresolved-dependency restore failure. This is the one real ordering constraint on an otherwise independent release cadence: Forge/Fabric can still bump their own version freely without touching CommonPrimitives, but bumping *which* CommonPrimitives version they depend on requires CommonPrimitives to have shipped that version first.
- Do **not** add `ReferenceOutputAssembly="false"` to these `<ProjectReference>` items — it silently suppresses nuspec dependency generation entirely (verified empirically: NuGet just drops the reference instead of turning it into a versioned dependency). The reference is otherwise harmless either way — neither project has any code to compile, so no real assembly gets linked.

## Releasing

Releases are cut manually via the `Nuget-Release` GitHub Actions workflow (`workflow_dispatch`), which takes:

- **model** — `forge`, `fabric`, or `commonprimitives`. Selects which model's project gets packed and released; the other two are untouched.
- **version** — a SemVer string for that model's next release (e.g. `1.2.3` or `1.2.3-beta.1`).

A run packs only the selected model's project, stamps that version into the model's own `.xmi` (its EA package "Version" field — see `Directory.Build.targets`), commits that stamp back to the default branch, pushes the single resulting `.nupkg` to NuGet.org, tags the commit, and drafts a GitHub release with the `.nupkg` attached. Because each model is packed and tagged independently, releasing Forge never touches Fabric's or CommonPrimitives' version, tag, or package — releasing different models is safe to do independently, on independent schedules.

**Concurrency caveat:** the "commit XMI version stamp" step pushes directly to the default branch. If two release runs (e.g. `forge` and `fabric`) are triggered within moments of each other, the second one's push can hit a non-fast-forward conflict against the first's commit and need to be re-run. This isn't a design flaw — sequential independent releases (the normal case) work cleanly — just don't expect two releases to succeed from truly simultaneous runs.

**`CITATION.cff`:** a run also stamps the released `version` and `date-released` into `CITATION.cff` (both the top-level fields and the `preferred-citation` block) and commits that alongside the xmi version stamp. Since `CITATION.cff` is one shared file across three independently-versioned models, its `version` always reflects whichever model was released *most recently*, not any single model's version — the same convention `uml4net` uses for its own multi-package `CITATION.cff`. If you need to know a specific model's version, use its own tag/package version, not this file.

### Tagging convention

Tags are namespaced per model as `<model>/<version>`, using the same lowercase `model` value as the workflow's selector — e.g. `forge/1.0.0`, `fabric/0.2.0`, `commonprimitives/0.1.0`. This keeps each model's release history independently listable (`git tag -l 'forge/*'`) and avoids the ambiguity of a single shared tag standing in for three independently-versioned packages. The GitHub release title uses the full package id instead, e.g. `Mycelium.Model.Forge 1.0.0`.

## Replacing a placeholder xmi with the real model

Fabric and CommonPrimitives currently ship a bare-bones placeholder `.xmi` (just a `Model` package containing one empty top-level package) so the packaging/release plumbing can be exercised before any real modeling happens. To drop in the real model once it's ready in Enterprise Architect:

1. Export the real model from EA and overwrite the placeholder file in place (e.g. `Mycelium.Model.Fabric/mycelium-fabric.xmi`).
2. Update `PackageIdRef` in that model's `.csproj` (`<MyceliumModelVersionTarget>` item) to match the real top-level package's `xmi:idref` — EA will have assigned it a fresh GUID, different from the placeholder's. If you forget this, `dotnet pack` fails loudly (it can't find the version attribute to stamp) rather than silently shipping an unstamped or wrongly-stamped package.
3. Everything else — the project's `PackageId`, the `build`/`buildTransitive` props, the solution reference, the workflow's model option — already works unchanged.

## Adding an entirely new (fourth+) model

1. Create `Mycelium.Model.<Name>/Mycelium.Model.<Name>.csproj` (copy an existing model's `.csproj` as a template) and add its `.xmi` alongside it.
2. Add `build/Mycelium.Model.<Name>.props` and `buildTransitive/Mycelium.Model.<Name>.props` exposing `$(MyceliumModel<Name>XmiPath)`.
3. Add the project to `mycelium-model.sln` (`dotnet sln mycelium-model.sln add Mycelium.Model.<Name>/Mycelium.Model.<Name>.csproj`).
4. Add a `<name>` case (lowercase) to the `model` selector in `.github/workflows/nuget-release.yml`.
