#:property TargetFramework=net10.0

// Release helpers for .github/workflows/nuget-release.yml, as a .NET 10 file-based app: one .cs file,
// no .csproj, no packages, run by the SDK the workflow already installs.
//
//   dotnet run --file .github/scripts/release-tools.cs -- stamp-xmi     <xmi-path> <version> <package-id> <release-date>
//   dotnet run --file .github/scripts/release-tools.cs -- stamp-openapi <json-path> <version> <package-id> <release-date>
//   dotnet run --file .github/scripts/release-tools.cs -- check-deps    <nupkg-path>
//
// Use the explicit --file form: the workflow runs from the repository root, which contains
// mycelium-model.sln, and `dotnet run`'s first-argument form only applies when there is no project in the
// current directory. The TargetFramework directive above overrides the repo-root Directory.Build.props,
// which sets netstandard2.0 for the model packages and would otherwise leave this app with no host.
//
// Both verbs exit non-zero on any failure, which is what stops a release mid-run.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;

internal static class ReleaseTools
{
    private const string MofNamespace = "http://www.omg.org/spec/MOF/20131001";
    private const string TagPrefix = "eu.stariongroup.mycelium";
    private const string FlatContainer = "https://api.nuget.org/v3-flatcontainer/{0}/index.json";

    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(30);

    // latin-1, not windows-1252 and not utf-8: EA's exporter writes files that declare encoding="utf-8"
    // while the bytes may be anything, so no decoder that validates can be trusted here. Latin-1 maps all
    // 256 byte values one-to-one and round-trips losslessly whatever the file actually holds, whereas
    // windows-1252 has five undefined byte values and utf-8 rejects invalid sequences. Everything
    // inserted below is pure ASCII, so decoding as Latin-1, doing string surgery and re-encoding leaves
    // every other byte in the file untouched — including mycelium-commonprimitives.xmi's stray EF BF BD
    // (U+FFFD), a replacement character left by some earlier tool's lossy decode, which this preserves
    // rather than compounding.
    //
    // For the same reason stamp-xmi does text substitution rather than an XDocument parse/serialise round
    // trip, which would reflow EA's tab indentation, attribute order and self-closing tags and turn a
    // three-line diff into a whole-file rewrite.
    private static readonly Encoding XmiEncoding = Encoding.Latin1;

    // JSON is required to be UTF-8, and the OpenAPI documents here are tool-generated rather than
    // hand-edited, so a strict decoder is right: a file that is not valid UTF-8 is a defect to fail on,
    // not something to round-trip. No BOM on the way out, which is what the generator writes.
    private static readonly Encoding OpenApiEncoding = new UTF8Encoding(false, true);

    private static readonly JsonSerializerOptions OpenApiFormat = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Usage();
        }

        switch (args[0])
        {
            case "stamp-xmi":
                if (args.Length != 5)
                {
                    Fail("usage: stamp-xmi <xmi-path> <version> <package-id> <release-date>");
                }

                StampXmi(args[1], args[2], args[3], args[4]);
                return 0;

            case "stamp-openapi":
                if (args.Length != 5)
                {
                    Fail("usage: stamp-openapi <json-path> <version> <package-id> <release-date>");
                }

                StampOpenApi(args[1], args[2], args[3], args[4]);
                return 0;

            case "check-deps":
                if (args.Length != 2)
                {
                    Fail("usage: check-deps <nupkg-path>");
                }

                await CheckDependenciesAsync(args[1]);
                return 0;

            default:
                Fail(
                    $"unknown command '{args[0]}'; expected 'stamp-xmi', 'stamp-openapi' or 'check-deps'");
                return 1;
        }
    }

    [DoesNotReturn]
    private static void Usage()
    {
        Fail(
            "usage:\n"
            + "  stamp-xmi     <xmi-path> <version> <package-id> <release-date>\n"
            + "  stamp-openapi <json-path> <version> <package-id> <release-date>\n"
            + "  check-deps    <nupkg-path>");
    }

    [DoesNotReturn]
    private static void Fail(string message)
    {
        Console.Error.WriteLine("release-tools: " + message);
        Environment.Exit(1);
        throw new InvalidOperationException("unreachable");
    }

    // ---------------------------------------------------------------------------------------------
    // stamp-xmi
    // ---------------------------------------------------------------------------------------------

    // Injects MOF extension tags carrying release metadata into a Mycelium model XMI.
    //
    // Run immediately before `dotnet pack`, so the release version ships inside the .nupkg's model/*.xmi.
    // The workflow then commits the updated .xmi back to the default branch, so the file tracked in git
    // always reflects the last released version.
    //
    // Three <mofext:Tag> elements carry the metadata, each attached to the model's top-level uml:Package.
    // A tag that is missing gets appended just before the closing </xmi:XMI>; a tag that is already there
    // is updated in place, so re-releasing changes only the value= attributes:
    //
    //   <mofext:Tag xmi:id="mycelium-release-version"   name="eu.stariongroup.mycelium.version"     value="1.2.3" element="EAPK_..." />
    //   <mofext:Tag xmi:id="mycelium-release-packageId" name="eu.stariongroup.mycelium.packageId"   value="Mycelium.Model.Forge" element="EAPK_..." />
    //   <mofext:Tag xmi:id="mycelium-release-date"      name="eu.stariongroup.mycelium.releaseDate" value="2026-08-20" element="EAPK_..." />
    //
    // This replaces an earlier EA-specific stamping of <project version>/<packageproperties version>
    // inside <xmi:Extension extender="Enterprise Architect">: mycelium-commonprimitives.xmi is
    // hand-authored and has no EA extension block at all, so that mechanism could never work for it.
    // mofext:Tag is plain OMG XMI and works for every model regardless of where it was authored.
    private static void StampXmi(string xmiPath, string version, string packageId, string releaseDate)
    {
        string content;
        try
        {
            content = XmiEncoding.GetString(File.ReadAllBytes(xmiPath));
        }
        catch (IOException error)
        {
            Fail($"could not read {xmiPath}: {error.Message}");
            return;
        }

        var elementId = FindRootPackageId(content, xmiPath);
        content = DeclareMofNamespace(content, xmiPath);

        // Add-or-update, per tag. A tag already in the file is rewritten where it stands, so a re-release
        // produces a three-line diff (the value= attributes) rather than moving the block around; rewriting
        // the whole element rather than patching value= also refreshes element= after an EA re-export
        // assigned the top-level package a fresh GUID. Tags not yet present are appended together.
        var appended = new StringBuilder();

        foreach (var (identifier, name, value) in new[]
                 {
                     ("mycelium-release-version", "version", version),
                     ("mycelium-release-packageId", "packageId", packageId),
                     ("mycelium-release-date", "releaseDate", releaseDate),
                 })
        {
            var tag =
                $"<mofext:Tag xmi:id=\"{identifier}\" name=\"{TagPrefix}.{name}\" "
                + $"value=\"{SecurityElement.Escape(value)}\" element=\"{elementId}\" />";

            var existing = Regex.Match(
                content,
                $"<mofext:Tag\\b[^>]*\\bname=\"{Regex.Escape(TagPrefix)}\\.{Regex.Escape(name)}\"[^>]*/>");

            if (existing.Success)
            {
                content = content[..existing.Index] + tag + content[(existing.Index + existing.Length)..];
            }
            else
            {
                appended.Append("  ").Append(tag).Append('\n');
            }
        }

        if (appended.Length > 0)
        {
            var closing = content.LastIndexOf("</xmi:XMI>", StringComparison.Ordinal);
            if (closing == -1)
            {
                Fail($"no closing </xmi:XMI> found in {xmiPath}");
            }

            // Guarantee the tags start on their own line: CommonPrimitives' </xmi:XMI> follows its root
            // package's closing tag directly, while EA's exports put it on a line of its own.
            var separator = closing > 0 && content[closing - 1] == '\n' ? string.Empty : "\n";
            content = content[..closing] + separator + appended + content[closing..];
        }

        File.WriteAllBytes(xmiPath, XmiEncoding.GetBytes(content));

        Console.WriteLine(
            $"release-tools: {xmiPath} <- version={version} packageId={packageId} "
            + $"releaseDate={releaseDate} (element={elementId})");
    }

    /// xmi:id of the first element in the document typed uml:Package.
    ///
    /// Derived rather than configured so that re-exporting a model from EA (which assigns fresh GUIDs)
    /// needs no follow-up edit anywhere. Resolves to the top-level package in both shapes shipped here:
    /// a <packagedElement> under <uml:Model> (Forge, Fabric) and a root <uml:Package> (CommonPrimitives).
    private static string FindRootPackageId(string content, string xmiPath)
    {
        foreach (Match tag in Regex.Matches(content, "<[^!?/][^>]*>"))
        {
            if (!tag.Value.Contains("xmi:type=\"uml:Package\"", StringComparison.Ordinal))
            {
                continue;
            }

            var identifier = Regex.Match(tag.Value, "\\bxmi:id=\"([^\"]+)\"");
            if (identifier.Success)
            {
                return identifier.Groups[1].Value;
            }
        }

        Fail(
            $"no element with xmi:type=\"uml:Package\" and an xmi:id found in {xmiPath} — cannot determine "
            + "the top-level package to attach the release tags to");
        return string.Empty;
    }

    private static string DeclareMofNamespace(string content, string xmiPath)
    {
        var root = Regex.Match(content, "<xmi:XMI\\b[^>]*>");
        if (!root.Success)
        {
            Fail($"no <xmi:XMI> root element found in {xmiPath}");
        }

        if (root.Value.Contains("xmlns:mofext=", StringComparison.Ordinal))
        {
            return content;
        }

        var updatedRoot = root.Value[..^1].TrimEnd() + $" xmlns:mofext=\"{MofNamespace}\">";
        return content[..root.Index] + updatedRoot + content[(root.Index + root.Length)..];
    }

    // ---------------------------------------------------------------------------------------------
    // stamp-openapi
    // ---------------------------------------------------------------------------------------------

    // Writes the release metadata into a Mycelium ICD OpenAPI document's `info` object.
    //
    // The counterpart of stamp-xmi for the ICD packages, run at the same point in the release: just
    // before `dotnet pack`, with the result committed back to the default branch.
    //
    //   "info": {
    //     "version": "1.2.3",
    //     "x-mycelium-version": "1.2.3",
    //     "x-mycelium-packageId": "Mycelium.ICD.FunctionalDataService",
    //     "x-mycelium-releaseDate": "2026-08-20"
    //   }
    //
    // `info.version` is overwritten, so the document's own version always equals the published package
    // version; the `x-mycelium-*` extension keys carry the rest, and are add-or-update like the
    // mofext:Tag elements are. Unlike stamp-xmi this parses and re-serialises rather than doing text
    // surgery: JSON has no significant formatting, and the document is generated rather than
    // hand-maintained, so reproducing its layout (two-space indent, unescaped non-ASCII, the source
    // file's newline style) is enough to keep a release diff down to the stamped keys.
    private static void StampOpenApi(string jsonPath, string version, string packageId, string releaseDate)
    {
        string content;
        try
        {
            content = OpenApiEncoding.GetString(File.ReadAllBytes(jsonPath));
        }
        catch (Exception error) when (error is IOException or DecoderFallbackException)
        {
            Fail($"could not read {jsonPath}: {error.Message}");
            return;
        }

        JsonNode? document;
        try
        {
            document = JsonNode.Parse(content);
        }
        catch (JsonException error)
        {
            Fail($"{jsonPath} is not valid JSON: {error.Message}");
            return;
        }

        if (document?["info"] is not JsonObject info)
        {
            Fail($"no `info` object found in {jsonPath} — not an OpenAPI document");
            return;
        }

        info["version"] = version;
        info["x-mycelium-version"] = version;
        info["x-mycelium-packageId"] = packageId;
        info["x-mycelium-releaseDate"] = releaseDate;

        // The writer indents with Environment.NewLine, so normalise before adopting the source file's
        // newline style — otherwise a CRLF file gains a second carriage return per line on Windows.
        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var stamped = document.ToJsonString(OpenApiFormat).Replace("\r\n", "\n").Replace("\n", newline);

        if (content.EndsWith('\n'))
        {
            stamped += newline;
        }

        File.WriteAllBytes(jsonPath, OpenApiEncoding.GetBytes(stamped));

        Console.WriteLine(
            $"release-tools: {jsonPath} <- version={version} packageId={packageId} "
            + $"releaseDate={releaseDate}");
    }

    // ---------------------------------------------------------------------------------------------
    // check-deps
    // ---------------------------------------------------------------------------------------------

    // Verifies every dependency declared in a .nupkg's nuspec is already published on NuGet.org.
    //
    // Run immediately before `dotnet nuget push`, so a release that would pin consumers to an unpublished
    // dependency fails before anything reaches NuGet.org rather than after it is too late to take back.
    //
    // This enforces the one real ordering constraint on an otherwise independent release cadence: Forge
    // and Fabric each carry a <ProjectReference> to CommonPrimitives, which `dotnet pack` turns into a
    // nuspec <dependency> at CommonPrimitives' current <Version>. That exact version must already be on
    // NuGet.org, or restoring Forge/Fabric fails for every consumer. README.md states the rule; this
    // checks it.
    //
    // Every dependency is checked rather than special-casing CommonPrimitives, so adding a fourth model
    // needs no change here. A package with no dependencies (CommonPrimitives itself) is a no-op.
    private static async Task CheckDependenciesAsync(string nupkgPath)
    {
        var dependencies = ReadDependencies(nupkgPath);

        if (dependencies.Count == 0)
        {
            Console.WriteLine($"release-tools: {nupkgPath} declares no dependencies, nothing to check");
            return;
        }

        using var client = new HttpClient { Timeout = HttpTimeout };
        var unpublished = new List<string>();

        foreach (var (identifier, version) in dependencies)
        {
            var available = await PublishedVersionsAsync(client, identifier);

            if (available is null)
            {
                unpublished.Add($"{identifier} {version} - package id has never been published");
            }
            else if (!available.Contains(version.Trim().ToLowerInvariant()))
            {
                var newest = string.Join(", ", available.TakeLast(5));
                unpublished.Add(
                    $"{identifier} {version} - not published; newest available: "
                    + (newest.Length > 0 ? newest : "(none)"));
            }
            else
            {
                Console.WriteLine($"release-tools: OK  {identifier} {version}");
            }
        }

        if (unpublished.Count > 0)
        {
            Fail(
                $"{nupkgPath} depends on versions that are not on NuGet.org, so consumers could not "
                + "restore it:\n  " + string.Join("\n  ", unpublished));
        }
    }

    private static List<(string Id, string Version)> ReadDependencies(string nupkgPath)
    {
        XDocument nuspec;
        try
        {
            using var package = ZipFile.OpenRead(nupkgPath);

            var entries = package.Entries
                .Where(e => e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (entries.Count != 1)
            {
                Fail($"expected exactly one .nuspec in {nupkgPath}, found {entries.Count}");
            }

            using var stream = entries[0].Open();
            nuspec = XDocument.Load(stream);
        }
        catch (InvalidDataException error)
        {
            Fail($"{nupkgPath} is not a valid .nupkg: {error.Message}");
            return [];
        }
        catch (IOException error)
        {
            Fail($"could not read {nupkgPath}: {error.Message}");
            return [];
        }

        // Match on the local name: the nuspec namespace varies by schema version.
        return nuspec.Descendants()
            .Where(e => e.Name.LocalName == "dependency")
            .Select(e => (Id: (string?)e.Attribute("id"), Version: (string?)e.Attribute("version")))
            .Where(d => !string.IsNullOrEmpty(d.Id) && !string.IsNullOrEmpty(d.Version))
            .Select(d => (d.Id!, d.Version!))
            .ToList();
    }

    /// Versions of `identifier` on NuGet.org, or null if the package id was never published.
    private static async Task<List<string>?> PublishedVersionsAsync(HttpClient client, string identifier)
    {
        var url = string.Format(FlatContainer, identifier.ToLowerInvariant());

        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(url);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            // Network trouble stops the release rather than being assumed benign — publishing a package
            // whose dependencies were never confirmed is exactly what this step exists to prevent.
            Fail($"could not reach {url}: {error.Message}");
            return null;
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                Fail($"NuGet.org returned HTTP {(int)response.StatusCode} for {url}");
            }

            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            // The flat container returns NuGet-normalised, lowercased versions.
            return payload.RootElement.GetProperty("versions")
                .EnumerateArray()
                .Select(v => (v.GetString() ?? string.Empty).Trim().ToLowerInvariant())
                .ToList();
        }
    }
}
