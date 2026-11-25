"""Verify nupkg contents and restore a consumer without workspace references."""
import argparse
import subprocess
import tempfile
from pathlib import Path
from xml.etree import ElementTree as ET
from zipfile import ZipFile

from nuget_package_identity import assembly_name, package_id, source_projects

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("feed", type=Path, help="Directory made by dotnet pack.")
args = parser.parse_args()
feed = args.feed.resolve()
root = Path(__file__).resolve().parent.parent
projects = source_projects(root)
if len(projects) != 11:
    raise SystemExit(f"Expected eleven source library projects, found {len(projects)}.")
versions = set()
descriptions = set()
metadata = {}
for file in sorted(feed.glob("*.nupkg")):
    with ZipFile(file) as archive:
        assets = archive.namelist()
        nuspecs = [name for name in assets if name.endswith(".nuspec")]
        assert len(nuspecs) == 1, f"{file.name}: expected one .nuspec."
        package = ET.fromstring(archive.read(nuspecs[0]))
        meta = package.find("{*}metadata")
        name = meta.findtext("{*}id")
        version = meta.findtext("{*}version")
        description = meta.findtext("{*}description")
        assert name in projects and name not in metadata, f"Unexpected package {name}."
        assert version and description, f"{name}: missing version or description."
        license_element = meta.find("{*}license")
        assert license_element is not None and license_element.attrib.get("type") == "expression" and license_element.text == "MIT", f"{name}: expected MIT SPDX expression."
        assert "LICENSE" in assets and archive.read("LICENSE") == (root / "LICENSE").read_bytes(), f"{name}: missing or mismatched MIT text."
        versions.add(version)
        descriptions.add(description)
        metadata[name] = version
        dependencies = {
            dep.attrib["id"] for dep in meta.findall(".//{*}dependency")
        }
        references = {
            package_id((projects[name].parent / ref.attrib["Include"]).resolve())
            for ref in ET.parse(projects[name]).findall(".//ProjectReference")
        }
        assert dependencies >= references, f"{name} dropped an internal package dependency."
        assert not any(dep.casefold() == "idempotencykit.aspnetcore" for dep in dependencies), f"{name}: old contested dependency."
        assembly = assembly_name(projects[name])
        for asset in (f"lib/net10.0/{assembly}.dll", f"lib/net10.0/{assembly}.xml", "README.md"):
            assert asset in assets, f"{name}: missing {asset}."
        if name in ("IdempotencyKit.Postgres", "IdempotencyKit.SqlServer", "IdempotencyKit.Sqlite"):
            migration = f"sql/{name.split('.')[-1].lower()}/001_idempotencykit_records.sql"
            assert migration in assets, f"{name}: missing migration."
assert metadata.keys() == projects.keys(), f"Missing: {projects.keys() - metadata.keys()}"
assert len(versions) == 1, "Expected matching internal package versions."
assert len(descriptions) == len(projects), "Expected specific package descriptions."

with tempfile.TemporaryDirectory(prefix="idk-nuget-consumer-") as directory:
    cwd = Path(directory)
    project = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
    properties = ET.SubElement(project, "PropertyGroup")
    for name, value in (
        ("TargetFramework", "net10.0"),
        ("OutputType", "Exe"),
        ("ImplicitUsings", "enable"),
    ):
        ET.SubElement(properties, name).text = value
    refs = ET.SubElement(project, "ItemGroup")
    for name, version in sorted(metadata.items()):
        ET.SubElement(refs, "PackageReference", Include=name, Version=version)
    ET.ElementTree(project).write(cwd / "Smoke.csproj")
    # An isolated package cache + source mapping forces internal packages to come
    # from these exact artifacts, not another local build or a later registry release.
    config = ET.Element("configuration")
    sources = ET.SubElement(config, "packageSources")
    ET.SubElement(sources, "clear")
    ET.SubElement(sources, "add", key="preview", value=str(feed))
    ET.SubElement(sources, "add", key="nuget.org", value="https://api.nuget.org/v3/index.json")
    mapping = ET.SubElement(config, "packageSourceMapping")
    ET.SubElement(ET.SubElement(mapping, "packageSource", key="preview"), "package", pattern="IdempotencyKit.*")
    ET.SubElement(ET.SubElement(mapping, "packageSource", key="nuget.org"), "package", pattern="*")
    ET.ElementTree(config).write(cwd / "NuGet.Config")
    (cwd / "Program.cs").write_text("""
using IdempotencyKit.Core;
using IdempotencyKit.Sqlite;
using IdempotencyKit.AspNetCore;
if (typeof(IdempotentAttribute).Assembly.GetName().Name != "IdempotencyKit.AspNetCore")
    throw new Exception("Standard package changed its existing assembly identity.");
var path = Path.GetFullPath("smoke.db");
await SqliteIdempotencyDatabase.InitializeAsync(path);
var store = new SqliteIdempotencyStore(path);
var claim = await store.TryClaimAsync("v1:" + new string('a', 64), "v1:" + new string('b', 64), TimeSpan.FromSeconds(5));
if (claim.Status != IdempotencyClaimStatus.Acquired)
    throw new Exception("SQLite smoke claim was not acquired.");
""", encoding="utf-8")
    subprocess.run(["dotnet", "restore", "--configfile", "NuGet.Config", "--packages", "packages"],
                   cwd=cwd, check=True)
    subprocess.run(["dotnet", "run", "--configuration", "Release", "--no-restore"],
                   cwd=cwd, check=True)
print("11 NuGet artifacts passed standalone consumer restore and SQLite smoke.")
