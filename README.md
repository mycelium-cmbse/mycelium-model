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

## Releasing

Releases are cut manually via the `Nuget-Release` GitHub Actions workflow (`workflow_dispatch`), which takes:

- **model** — `forge`, `fabric`, or `commonprimitives`. Selects which model's project gets packed and released; the other two are untouched.
- **version** — a SemVer string for that model's next release (e.g. `1.2.3` or `1.2.3-beta.1`).

A run packs only the selected model's project, stamps that version into the model's own `.xmi` (its EA package "Version" field — see `Directory.Build.targets`), commits that stamp back to the default branch, pushes the single resulting `.nupkg` to NuGet.org, tags the commit, and drafts a GitHub release with the `.nupkg` attached. Because each model is packed and tagged independently, releasing Forge never touches Fabric's or CommonPrimitives' version, tag, or package.

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
