# mycelium-model
The UML 2.5.1 models for Bloom, Fabric and Forge

## Consuming this package

The models are distributed as the `Mycelium.Model` NuGet package. It ships the `.xmi` files under a `model/` folder inside the package and does not contain any compiled code — it's a data-only package meant to be consumed by a `uml4net`-based code-generation project.

Adding a `PackageReference` automatically imports an MSBuild property for each model, pointing at the `.xmi` file's absolute path inside the local NuGet package cache. No copying or extra restore step is required — the property is available immediately after `dotnet restore`.

```xml
<PackageReference Include="Mycelium.Model" Version="1.0.0" />

<Target Name="GenerateFromModel" BeforeTargets="CoreCompile">
  <Exec Command="dotnet run --project ../CodeGen -- --model &quot;$(MyceliumModelForgeXmiPath)&quot; --out $(IntermediateOutputPath)generated" />
</Target>
```

Available properties:

| Model | MSBuild property |
|---|---|
| Forge (`mycelium-forge.xmi`) | `$(MyceliumModelForgeXmiPath)` |

As Bloom and Fabric models are added, a corresponding `$(MyceliumModelBloomXmiPath)` / `$(MyceliumModelFabricXmiPath)` property will be added here.

## Releasing

Releases are cut manually via the `Nuget-Release` GitHub Actions workflow (`workflow_dispatch`), which takes a SemVer version, packs `Mycelium.Model.csproj`, publishes it to NuGet.org, tags the commit, and creates a draft GitHub release with the `.nupkg` attached.
