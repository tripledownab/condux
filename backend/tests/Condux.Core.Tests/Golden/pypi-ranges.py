"""Regenerates pypi-ranges.tsv, the fixture PypiVulnerableRangesTests reads.

Every range is a real GitHub advisory range for the pip ecosystem, verbatim. The versions tried
against each are placed on and around its bounds, where an ordering goes wrong, plus every real
release of four packages that shares a major and minor with a bound of one of their own advisories.
The expected answer comes from Python's packaging library, which pip itself uses, so the fixture
states what pip would decide rather than what the C# side thinks it should.

The answer recorded is what the matcher may CLAIM: packaging's verdict, and false for a version
packaging cannot parse or counts as a pre-release, which the matcher leaves undecided.

Run: python3 pypi-ranges.py > pypi-ranges.tsv   (needs `gh` signed in, network, packaging)
"""

import json
import subprocess
import sys
import urllib.request

import packaging
from packaging.specifiers import SpecifierSet
from packaging.version import InvalidVersion, Version

NAMED = ["requests", "urllib3", "Django", "Jinja2"]
COMMON = {"< N.N.N", ">= N.N.N, < N.N.N", "<= N.N.N", "= N.N.N"}
COMMON_SAMPLE = 150


def fetch_advisories(query):
    out = subprocess.run(
        ["gh", "api", "--paginate", f"/advisories?ecosystem=pip&per_page=100{query}",
         "--jq", '.[] | .ghsa_id as $g | .vulnerabilities[] | select(.package.ecosystem=="pip")'
                 ' | [$g, .package.name, .vulnerable_version_range] | @tsv'],
        check=True, capture_output=True, text=True).stdout
    return [tuple(line.split("\t")) for line in out.splitlines() if line]


def to_shape(text):
    """The range with each run of digits written as N, so ranges of one form share a shape."""
    text = "".join("N" if c.isdigit() else c for c in text)
    while "NN" in text:
        text = text.replace("NN", "N")
    return text


def to_specifier(range_text):
    parts = []
    for part in range_text.split(","):
        part = part.strip()
        op = next(o for o in (">=", "<=", ">", "<", "=") if part.startswith(o))
        parts.append(("==" if op == "=" else op) + part[len(op):].strip())
    return SpecifierSet(",".join(parts))


def read_bounds(range_text):
    return [p.strip().lstrip("<>=").strip() for p in range_text.split(",")]


def list_around(bound):
    """Versions on and beside a bound, including spellings the specification normalises."""
    found = {bound, "v" + bound, bound + ".post1", bound + "-1", bound + "+local.7", bound + ".dev0",
             bound + ".0", bound + "rc1", bound.upper(), bound + ".post-", bound + "r1", bound + "_post1",
             bound + "rev", bound + "+a..b", bound + "+.a", bound + "-", bound + ".post1.dev0"}
    try:
        v = Version(bound)
    except InvalidVersion:
        return found
    base = list(v.release)
    found.add(".".join(map(str, base)))
    for i in range(len(base)):
        up = base[: i + 1]
        up[i] += 1
        found.add(".".join(map(str, up)))
        if base[i] > 0:
            down = base[:]
            down[i] -= 1
            found.add(".".join(map(str, down)))
            found.add(".".join(map(str, down)) + ".99")
    found.add(".".join(map(str, base + [1])))
    # Local builds of the bound's own release and of the versions beside it, which "> V" treats apart.
    release = ".".join(map(str, base))
    found |= {release + "+cu118", release + ".post9+cu118", bound + "+cu118"}
    # A later post-release of a bound that is itself one, which "> V" admits.
    if v.post is not None:
        found |= {f"{release}.post{v.post + 1}", f"{release}.post{v.post + 1}+cu118"}
    if v.epoch == 0:
        found.add("1!" + ".".join(map(str, base)))
    return found


def to_series(version):
    try:
        return Version(version).release[:2]
    except InvalidVersion:
        return None


def is_near(range_text, version):
    """Whether a release shares its major and minor with a bound, which is where the answers turn."""
    return to_series(version) in {to_series(bound) for bound in read_bounds(range_text)}


def fetch_releases(package):
    with urllib.request.urlopen(f"https://pypi.org/pypi/{package}/json") as response:
        return list(json.load(response)["releases"])


def decide_claim(spec, version):
    try:
        parsed = Version(version)
    except InvalidVersion:
        return False
    return not parsed.is_prerelease and spec.contains(parsed, prereleases=True)


def main():
    rows = {}
    ranges = sorted(set(fetch_advisories("")))
    seen = set()
    common = 0
    for ghsa, package, range_text in ranges:
        if range_text in seen:
            continue
        if to_shape(range_text) in COMMON:
            if common >= COMMON_SAMPLE:
                continue
            common += 1
        seen.add(range_text)
        try:
            spec = to_specifier(range_text)
        except Exception:
            # A range packaging cannot read is one the matcher must not confirm either.
            spec = None
        for bound in read_bounds(range_text):
            for version in list_around(bound):
                rows[(ghsa, package, range_text, version)] = spec is not None and decide_claim(spec, version)

    for package in NAMED:
        versions = fetch_releases(package)
        named = {}
        for ghsa, name, range_text in sorted(set(fetch_advisories(f"&affects={package}"))):
            named.setdefault(range_text, (ghsa, name))
        for range_text, (ghsa, name) in named.items():
            spec = to_specifier(range_text)
            for version in versions:
                if is_near(range_text, version):
                    rows[(ghsa, name, range_text, version)] = decide_claim(spec, version)

    grouped = {}
    for (ghsa, package, range_text, version), verdict in rows.items():
        grouped.setdefault((ghsa, package, range_text), ([], []))[0 if verdict else 1].append(version)
    out = sys.stdout
    out.write(f"# Generated by pypi-ranges.py with packaging {packaging.__version__}. Do not edit.\n")
    out.write("# The ghsa, package and range columns are excerpted from the GitHub Advisory Database\n")
    out.write("# (https://github.com/advisories; each row's record is https://github.com/advisories/<ghsa>),\n")
    out.write("# licensed CC BY 4.0 (https://creativecommons.org/licenses/by/4.0/). The version lists are added.\n")
    out.write("# ghsa\tpackage\trange\tclaimed versions\tunclaimed versions (each space separated)\n")
    for (ghsa, package, range_text), (claimed, unclaimed) in sorted(grouped.items()):
        out.write(f"{ghsa}\t{package}\t{range_text}\t{' '.join(sorted(claimed))}\t{' '.join(sorted(unclaimed))}\n")


if __name__ == "__main__":
    main()
