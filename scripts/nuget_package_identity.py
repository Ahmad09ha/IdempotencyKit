"""Source project identities for the package checks (not an MSBuild evaluator).

Our library projects have literal PackageId/AssemblyName overrides or the SDK
default project stem. Fail on duplicate/conditional/dynamic identities rather
than infer them. The actual nuspec and DLL/XML entries are checked separately.
"""
from pathlib import Path
from xml.etree import ElementTree as ET


def literal_property(project: Path, name: str) -> str:
    values = []
    for group in ET.parse(project).getroot().findall("PropertyGroup"):
        for item in group.findall(name):
            value = (item.text or "").strip()
            assert "Condition" not in group.attrib and "Condition" not in item.attrib, (project, name)
            assert value and "$(" not in value and "%(" not in value and "@(" not in value, (project, name)
            values.append(value)
    assert len(values) <= 1, (project, name)
    return values[0] if values else project.stem


def package_id(project: Path) -> str:
    return literal_property(project, "PackageId")


def assembly_name(project: Path) -> str:
    return literal_property(project, "AssemblyName")


def source_projects(root: Path) -> dict[str, Path]:
    files = sorted((root / "dotnet/src").glob("*/*.csproj"))
    projects = {package_id(file): file for file in files}
    assert len(files) == len(projects) == 11, "Expected eleven distinct library package IDs"
    assert "IdempotencyKit.AspNetCore" not in projects, "Do not distribute under the contested ID"
    assert "IdempotencyKit.AspNetCore.Standard" in projects
    assert assembly_name(projects["IdempotencyKit.AspNetCore.Standard"]) == "IdempotencyKit.AspNetCore"
    return projects
