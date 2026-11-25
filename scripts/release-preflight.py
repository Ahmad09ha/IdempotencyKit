"""Read-only release preflight. Never authenticates, stages, tags or publishes."""
import argparse
import hashlib
import json
import re
import tarfile
from datetime import datetime, timezone
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.parse import quote
from urllib.request import Request, urlopen
from xml.etree import ElementTree as ET
from zipfile import ZipFile

ROOT = Path(__file__).resolve().parent.parent
NUGET_IDS = {p.stem for p in (ROOT / "dotnet/src").glob("*/*.csproj")}
NPM_IDS = {json.loads(p.read_text(encoding="utf-8"))["name"]
           for p in (ROOT / "typescript/packages").glob("*/package.json")}
assert len(NUGET_IDS) == 11 and len(NPM_IDS) == 8

def load_json(path):
    return json.loads(path.read_text(encoding="utf-8"))

def emit(path, value):
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")

def inventory(directory, source):
    if not re.fullmatch(r"[0-9a-f]{40}", source or ""):
        raise SystemExit("Source must be the checked-out full commit SHA")
    props = ET.parse(ROOT / "dotnet/src/Directory.Build.props")
    version = props.findtext(".//Version")
    assert version and re.fullmatch(r"\d+\.\d+\.\d+-preview\.\d+", version)
    seen = {"nuget": set(), "npm": set()}
    packages = []
    def record(registry, path, name, packed_version, license_id, private):
        assert name in (NUGET_IDS if registry == "nuget" else NPM_IDS), name
        assert name not in seen[registry], name
        assert packed_version == version, (name, packed_version, version)
        seen[registry].add(name)
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        packages.append({"registry": registry, "id": name, "version": packed_version,
                         "file": path.relative_to(directory).as_posix(), "sha256": digest,
                         "bytes": path.stat().st_size, "license": license_id, "private": private})
    for p in sorted((directory / "nuget").glob("*.nupkg")):
        with ZipFile(p) as archive:
            nuspecs = [n for n in archive.namelist() if n.endswith(".nuspec")]
            assert len(nuspecs) == 1
            meta = ET.fromstring(archive.read(nuspecs[0])).find("{*}metadata")
            license_id = meta.findtext("{*}license")
            record("nuget", p, meta.findtext("{*}id"), meta.findtext("{*}version"), license_id, None)
    for p in sorted((directory / "npm").glob("*.tgz")):
        with tarfile.open(p, "r:gz") as archive:
            meta = json.load(archive.extractfile("package/package.json"))
            record("npm", p, meta["name"], meta["version"], meta.get("license"), meta.get("private", False))
    assert seen["nuget"] == NUGET_IDS and seen["npm"] == NPM_IDS, "Missing or extra package IDs"
    # This preflight may inventory unlicensed private preview artifacts. It does
    # not certify that the family is legally distributable or registry-publishable.
    manifest = {"source_sha": source, "version": version, "purpose": "no-publish-preview",
                "packages": sorted(packages, key=lambda p: (p["registry"], p["id"]))}
    emit(directory / "release-manifest.json", manifest)
    (directory / "SHA256SUMS").write_text("".join(
        f"{p['sha256']}  {p['file']}\n" for p in manifest["packages"]), encoding="utf-8")
    print(f"IDK_RELEASE_INVENTORY {version} {source} {len(packages)} verified archives")

def registry(directory):
    manifest = load_json(directory / "release-manifest.json")
    rows = []
    for package in manifest["packages"]:
        name, version, kind = package["id"], package["version"], package["registry"]
        if kind == "nuget":
            url = "https://api.nuget.org/v3-flatcontainer/" + quote(name.lower(), safe="") + "/index.json"
        else:
            url = "https://registry.npmjs.org/" + quote(name, safe="@")
        row = {"registry": kind, "id": name, "version": version}
        try:
            request = Request(url, headers={"Accept": "application/json",
                                            "User-Agent": "IdempotencyKit-release-preflight"})
            with urlopen(request, timeout=12) as response:
                data = json.load(response)
            versions = data.get("versions")
            expected = list if kind == "nuget" else dict
            if not isinstance(versions, expected):
                raise ValueError("Unexpected registry response")
            row["status"] = "version-exists" if version in versions else "public-name-exists"
        except HTTPError as e:
            row["status"] = "not-publicly-found" if e.code == 404 else "unknown"
            row["http_status"] = e.code
        except (URLError, OSError, ValueError) as e:
            row["status"] = "unknown"
            row["error_type"] = type(e).__name__
        rows.append(row)
        print("IDK_REGISTRY " + json.dumps(row, sort_keys=True))
    emit(directory / "registry-preflight.json", {
        "checked_at_utc": datetime.now(timezone.utc).isoformat(),
        "note": "Public metadata only. 404 does not establish availability, ownership, private-package absence, or permission to publish. No registry mutations.",
        "packages": rows,
    })
    # An existing public name is not automatically a conflict (the maintainer
    # may own it). Unknowns must be resolved by a human before release.
    print("IDK_REGISTRY_SUMMARY " + json.dumps({s: sum(r["status"] == s for r in rows)
        for s in ("not-publicly-found", "public-name-exists", "version-exists", "unknown")}, sort_keys=True))

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("mode", choices=("inventory", "registry"))
parser.add_argument("directory", type=Path)
parser.add_argument("--source")
args = parser.parse_args()
if args.mode == "inventory":
    inventory(args.directory.resolve(), args.source)
else:
    registry(args.directory.resolve())
