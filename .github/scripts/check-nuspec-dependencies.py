#!/usr/bin/env python3
"""Verify every dependency declared in a .nupkg's nuspec is already published on NuGet.org.

Run by .github/workflows/nuget-release.yml immediately before `dotnet nuget push`, so a release that
would pin consumers to an unpublished dependency fails before anything reaches NuGet.org rather than
after it is too late to take back.

This enforces the one real ordering constraint on an otherwise independent release cadence: Forge and
Fabric each carry a <ProjectReference> to CommonPrimitives, which `dotnet pack` turns into a nuspec
<dependency> at CommonPrimitives' current <Version>. That exact version must already be on NuGet.org,
or restoring Forge/Fabric fails for every consumer. README.md states the rule; this checks it.

Every dependency is checked rather than special-casing CommonPrimitives, so adding a fourth model needs
no change here. A package with no dependencies (CommonPrimitives itself) is a no-op.

Usage:
    check-nuspec-dependencies.py <nupkg-path>
"""

import json
import sys
import zipfile
from urllib.error import HTTPError, URLError
from urllib.request import urlopen
from xml.etree import ElementTree

FLAT_CONTAINER = "https://api.nuget.org/v3-flatcontainer/{}/index.json"
TIMEOUT_SECONDS = 30


def fail(message):
    print("check-nuspec-dependencies: " + message, file=sys.stderr)
    sys.exit(1)


def local_name(tag):
    """Strip the XML namespace: the nuspec namespace varies by schema version, so matching on the
    local name keeps this working across all of them."""
    return tag.rsplit("}", 1)[-1]


def read_dependencies(nupkg_path):
    try:
        with zipfile.ZipFile(nupkg_path) as package:
            nuspecs = [n for n in package.namelist() if n.lower().endswith(".nuspec")]
            if len(nuspecs) != 1:
                fail("expected exactly one .nuspec in {}, found {}".format(nupkg_path, len(nuspecs)))
            root = ElementTree.fromstring(package.read(nuspecs[0]))
    except zipfile.BadZipFile as error:
        fail("{} is not a valid .nupkg: {}".format(nupkg_path, error))
    except (IOError, OSError) as error:
        fail("could not read {}: {}".format(nupkg_path, error))

    dependencies = []
    for element in root.iter():
        if local_name(element.tag) != "dependency":
            continue
        identifier = element.get("id")
        version = element.get("version")
        if identifier and version:
            dependencies.append((identifier, version))

    return dependencies


def published_versions(identifier):
    """Versions of `identifier` on NuGet.org, or None if the package id was never published."""
    url = FLAT_CONTAINER.format(identifier.lower())
    try:
        response = urlopen(url, timeout=TIMEOUT_SECONDS)
    except HTTPError as error:
        if error.code == 404:
            return None
        fail("NuGet.org returned HTTP {} for {}".format(error.code, url))
    except URLError as error:
        # Network trouble stops the release rather than being assumed benign — publishing a package
        # whose dependencies were never confirmed is exactly what this step exists to prevent.
        fail("could not reach {}: {}".format(url, error))

    with response:
        payload = json.loads(response.read().decode("utf-8"))

    # The flat container returns NuGet-normalised, lowercased versions.
    return [str(v).strip().lower() for v in payload.get("versions", [])]


def main(argv):
    if len(argv) != 2:
        fail("usage: check-nuspec-dependencies.py <nupkg-path>")

    nupkg_path = argv[1]
    dependencies = read_dependencies(nupkg_path)

    if not dependencies:
        print(
            "check-nuspec-dependencies: {} declares no dependencies, nothing to check".format(
                nupkg_path
            )
        )
        return

    unpublished = []
    for identifier, version in dependencies:
        available = published_versions(identifier)

        if available is None:
            unpublished.append(
                "{} {} - package id has never been published".format(identifier, version)
            )
        elif version.strip().lower() not in available:
            unpublished.append(
                "{} {} - not published; newest available: {}".format(
                    identifier, version, ", ".join(available[-5:]) or "(none)"
                )
            )
        else:
            print("check-nuspec-dependencies: OK  {} {}".format(identifier, version))

    if unpublished:
        fail(
            "{} depends on versions that are not on NuGet.org, so consumers could not restore it:\n  {}".format(
                nupkg_path, "\n  ".join(unpublished)
            )
        )


if __name__ == "__main__":
    main(sys.argv)
