#!/usr/bin/env python3
"""Inject MOF extension tags carrying release metadata into a Mycelium model XMI.

Run by .github/workflows/nuget-release.yml immediately before `dotnet pack`, so the release
version ships inside the .nupkg's model/*.xmi. The workflow then commits the updated .xmi back
to the default branch, so the file tracked in git always reflects the last released version.

Three <mofext:Tag> elements carry the release metadata, each attached to the model's top-level
uml:Package. A tag that is missing gets appended just before the closing </xmi:XMI>; a tag that
is already there is updated in place, so re-releasing changes only the value= attributes:

    <mofext:Tag xmi:id="mycelium-release-version"   name="eu.stariongroup.mycelium.version"     value="1.2.3" element="EAPK_..." />
    <mofext:Tag xmi:id="mycelium-release-packageId" name="eu.stariongroup.mycelium.packageId"   value="Mycelium.Model.Forge" element="EAPK_..." />
    <mofext:Tag xmi:id="mycelium-release-date"      name="eu.stariongroup.mycelium.releaseDate" value="2026-08-20" element="EAPK_..." />

This replaces the earlier EA-specific stamping of <project version>/<packageproperties version>
inside <xmi:Extension extender="Enterprise Architect">: mycelium-commonprimitives.xmi is
hand-authored and has no EA extension block at all, so that mechanism could never work for it.
mofext:Tag is plain OMG XMI and works for every model regardless of where it was authored.

Usage:
    stamp-xmi-version.py <xmi-path> <version> <package-id> <release-date>
"""

import re
import sys
from xml.sax.saxutils import quoteattr

MOF_NS = "http://www.omg.org/spec/MOF/20131001"
TAG_PREFIX = "eu.stariongroup.mycelium"

# latin-1, not cp1252: the .xmi files declare encoding="utf-8" but actually hold windows-1252
# bytes (EA's export format) — e.g. the en dash in "0-255" in mycelium-commonprimitives.xmi is a
# lone 0x96. latin-1 round-trips all 256 byte values losslessly, whereas cp1252 raises on the five
# byte values it leaves undefined. Everything inserted below is pure ASCII, so decoding as latin-1,
# doing string surgery and re-encoding as latin-1 leaves every other byte in the file untouched.
#
# For the same reason this does text substitution rather than an ElementTree parse/serialise round
# trip, which would reflow EA's tab indentation, attribute order and self-closing tags and turn a
# three-line diff into a whole-file rewrite.
CODEC = "latin-1"


def fail(message):
    print("stamp-xmi-version: " + message, file=sys.stderr)
    sys.exit(1)


def find_root_package_id(content, xmi_path):
    """xmi:id of the first element in the document typed uml:Package.

    Derived rather than configured so that re-exporting a model from EA (which assigns fresh
    GUIDs) needs no follow-up edit anywhere. Resolves to the top-level package in both shapes we
    ship: a <packagedElement> under <uml:Model> (Forge, Fabric) and a root <uml:Package>
    (CommonPrimitives).
    """
    for tag in re.finditer(r"<[^!?/][^>]*>", content):
        text = tag.group(0)
        if 'xmi:type="uml:Package"' not in text:
            continue
        identifier = re.search(r'\bxmi:id="([^"]+)"', text)
        if identifier:
            return identifier.group(1)

    fail(
        'no element with xmi:type="uml:Package" and an xmi:id found in {} — cannot determine the '
        "top-level package to attach the release tags to".format(xmi_path)
    )


def declare_mof_namespace(content, xmi_path):
    root = re.search(r"<xmi:XMI\b[^>]*>", content)
    if not root:
        fail("no <xmi:XMI> root element found in {}".format(xmi_path))

    if "xmlns:mofext=" in root.group(0):
        return content

    updated_root = root.group(0)[:-1].rstrip() + ' xmlns:mofext="{}">'.format(MOF_NS)
    return content[: root.start()] + updated_root + content[root.end() :]


def main(argv):
    if len(argv) != 5:
        fail("usage: stamp-xmi-version.py <xmi-path> <version> <package-id> <release-date>")

    xmi_path, version, package_id, release_date = argv[1:]

    with open(xmi_path, "rb") as stream:
        content = stream.read().decode(CODEC)

    element_id = find_root_package_id(content, xmi_path)
    content = declare_mof_namespace(content, xmi_path)

    # Add-or-update, per tag. A tag already in the file is rewritten where it stands, so a re-release
    # produces a three-line diff (the value= attributes) rather than moving the block around; rewriting
    # the whole element rather than patching value= also refreshes element= after an EA re-export
    # assigned the top-level package a fresh GUID. Tags not yet present are appended together.
    appended = ""
    for identifier, name, value in (
        ("mycelium-release-version", "version", version),
        ("mycelium-release-packageId", "packageId", package_id),
        ("mycelium-release-date", "releaseDate", release_date),
    ):
        tag = '<mofext:Tag xmi:id="{}" name="{}.{}" value={} element="{}" />'.format(
            identifier, TAG_PREFIX, name, quoteattr(value), element_id
        )
        existing = re.search(
            r'<mofext:Tag\b[^>]*\bname="{}\.{}"[^>]*/>'.format(
                re.escape(TAG_PREFIX), re.escape(name)
            ),
            content,
        )
        if existing:
            content = content[: existing.start()] + tag + content[existing.end() :]
        else:
            appended += "  " + tag + "\n"

    if appended:
        closing = content.rfind("</xmi:XMI>")
        if closing == -1:
            fail("no closing </xmi:XMI> found in {}".format(xmi_path))

        # Guarantee the tags start on their own line: CommonPrimitives' </xmi:XMI> follows its root
        # package's closing tag directly, while EA's exports put it on a line of its own.
        separator = "" if content[:closing].endswith("\n") else "\n"
        content = content[:closing] + separator + appended + content[closing:]

    with open(xmi_path, "wb") as stream:
        stream.write(content.encode(CODEC))

    print(
        "stamp-xmi-version: {} <- version={} packageId={} releaseDate={} (element={})".format(
            xmi_path, version, package_id, release_date, element_id
        )
    )


if __name__ == "__main__":
    main(sys.argv)
