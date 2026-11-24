"""Fail closed on an invalid advisory report; report lower-severity findings."""
import json
import sys
from collections import Counter
from pathlib import Path

mode, filename = sys.argv[1:]
report = json.loads(Path(filename).read_text(encoding="utf-8"))
severities = {"info", "low", "moderate", "high", "critical"}

if mode == "nuget":
    projects = report.get("projects")
    if not isinstance(projects, list) or not projects:
        raise SystemExit("NuGet report missing projects")
    if report.get("problems"):
        raise SystemExit(f"NuGet audit problems: {report['problems']}")
    unique = set()
    for project in projects:
        for framework in project.get("frameworks", []):
            for kind in ("topLevelPackages", "transitivePackages"):
                for package in framework.get(kind, []):
                    for v in package.get("vulnerabilities", []):
                        severity = str(v["severity"]).lower()
                        if severity not in severities:
                            raise SystemExit(f"Unknown NuGet severity: {severity}")
                        url = v.get("advisoryurl") or v.get("advisoryUrl") or ""
                        unique.add((package["id"], package["resolvedVersion"], severity, url))
    counts = Counter(v[2] for v in unique)
    for name, version, severity, url in sorted(unique):
        print(f"ADVISORY {severity} {name} {version} {url}")
elif mode in ("npm", "npm-report"):
    if report.get("error") or not isinstance(report.get("vulnerabilities"), dict):
        raise SystemExit("npm advisory service returned invalid/error report")
    counts = report.get("metadata", {}).get("vulnerabilities")
    if not isinstance(counts, dict) or any(
        not isinstance(counts.get(level), int) or counts[level] < 0
        for level in severities
    ):
        raise SystemExit("npm advisory report lacks valid severity counts")
    for name, details in sorted(report["vulnerabilities"].items()):
        print(f"ADVISORY {details.get('severity')} {name} {details.get('range')}")
else:
    raise SystemExit("Unknown audit report mode")
summary = {level: counts.get(level, 0) for level in sorted(severities)}
print("IDK_DEPENDENCY_AUDIT " + json.dumps({"scope": mode, **summary}))
if mode != "npm-report" and (summary["high"] or summary["critical"]):
    raise SystemExit("High/critical advisory requires triage before release")
