# mycelium-model

The UML 2.5.1 models for Mycelium: CommonPrimitives, Fabric and Forge.

Each model lives in its own project folder and is distributed as its own independently-versioned NuGet package, so each model's release lifecycle (version, release notes, release cadence) is fully independent of the others:

| Model | Project | Package | Status |
|---|---|---|---|
| Forge | `Mycelium.Model.Forge/` | `Mycelium.Model.Forge` | available |
| Fabric | `Mycelium.Model.Fabric/` | `Mycelium.Model.Fabric` | planned |
| CommonPrimitives | `Mycelium.Model.CommonPrimitives/` | `Mycelium.Model.CommonPrimitives` | planned |

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
| Forge (`mycelium-forge.xmi`) | `Mycelium.Model.Forge` | `$(MyceliumModelForgeXmiPath)` |

As Fabric and CommonPrimitives are added, their own packages will expose `$(MyceliumModelFabricXmiPath)` / `$(MyceliumModelCommonPrimitivesXmiPath)` respectively.

## Releasing

Releases are cut manually via the `Nuget-Release` GitHub Actions workflow (`workflow_dispatch`), which takes a model selection and a SemVer version, packs that model's project only, publishes it to NuGet.org, tags the commit (e.g. `forge/1.0.0`), and creates a draft GitHub release with the `.nupkg` attached. Packing stamps the released version into the model's own `.xmi` (its EA package "Version" field), and that change is committed back to the branch before tagging.

## Adding a new model

1. Create `Mycelium.Model.<Name>/Mycelium.Model.<Name>.csproj` (copy `Mycelium.Model.Forge/Mycelium.Model.Forge.csproj` as a template) and add its `.xmi` alongside it.
2. Add `build/Mycelium.Model.<Name>.props` and `buildTransitive/Mycelium.Model.<Name>.props` exposing `$(MyceliumModel<Name>XmiPath)`.
3. Add the project to `mycelium-model.slnx` (`dotnet sln mycelium-model.slnx add Mycelium.Model.<Name>/Mycelium.Model.<Name>.csproj`).
4. Add a `<Name>` case to the `model` selector in `.github/workflows/nuget-release.yml`.
