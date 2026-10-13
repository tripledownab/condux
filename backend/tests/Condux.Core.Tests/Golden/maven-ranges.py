"""Regenerates maven-ranges.tsv, the fixture MavenVulnerableRangesTests reads.

Every range is a real GitHub advisory range for the maven ecosystem, verbatim, at most 8 of each
shape (the range with its numbers written as N), so a shape repeated across hundreds of advisories
does not crowd out the rarer ones. The versions tried
against each are placed on and around its bounds, with the qualifiers real artifacts carry, plus every
real release of six artifacts that shares a major and minor with a bound of one of their own
advisories. The expected answer comes from Maven itself: MavenRangeOracle.java, beside this file, asks
the ComparableVersion of Maven 3.9.16 and of 4.0.0-rc-7, downloaded from Maven Central and checked
against Central's own SHA-1 files.

The answer recorded as claimed is both Mavens admitting the version, for a version the matcher reads
at all (ASCII letters, digits, '.', '-' and '_', starting with a digit); an unreadable one is recorded
unclaimed whatever Maven says. The matcher decides less than this, by design: it reads only the
leading numbers and two qualifiers, which is where Maven 3, Maven 4 and Dependabot agree, so the test
holds it to never confirming an unclaimed version and to confirming a floor of the claimed ones.

Run: python3 maven-ranges.py > maven-ranges.tsv   (needs `gh` signed in, network, a JDK)
"""

import hashlib
import os
import re
import subprocess
import sys
import tempfile
import urllib.request
import xml.etree.ElementTree as ElementTree

MAVENS = ["3.9.16", "4.0.0-rc-7"]
NAMED = [
    "com.fasterxml.jackson.core:jackson-databind",
    "org.apache.logging.log4j:log4j-core",
    "org.springframework:spring-core",
    "io.netty:netty-codec-http",
    "com.google.guava:guava",
    "org.eclipse.jetty:jetty-server",
]
SHAPE_SAMPLE = 8
CENTRAL = "https://repo1.maven.org/maven2"
QUALIFIERS = [".Final", ".RELEASE", ".GA", "-jre", "-android", "-sp1", ".v20230217", "-1", ".1", ".0",
              "u1", ".redhat-00001", "-fp1", "-M1", "-rc1", "-SNAPSHOT", "-alpha", "_1", ".ea", "-ga1"]


def fetch_advisories(query):
    out = subprocess.run(
        ["gh", "api", "--paginate", f"/advisories?ecosystem=maven&per_page=100{query}",
         "--jq", '.[] | .ghsa_id as $g | .vulnerabilities[] | select(.package.ecosystem=="maven")'
                 ' | [$g, .package.name, .vulnerable_version_range] | @tsv'],
        check=True, capture_output=True, text=True).stdout
    return [tuple(line.split("\t")) for line in out.splitlines() if line]


def fetch_maven(version, directory):
    """Maven's ComparableVersion jar, refused unless it matches the SHA-1 Central publishes for it."""
    url = f"{CENTRAL}/org/apache/maven/maven-artifact/{version}/maven-artifact-{version}.jar"
    with urllib.request.urlopen(url) as response:
        jar = response.read()
    with urllib.request.urlopen(url + ".sha1") as response:
        expected = response.read().decode().split()[0]
    if hashlib.sha1(jar).hexdigest() != expected:
        raise SystemExit(f"{url} does not match its published SHA-1")
    path = os.path.join(directory, f"maven-artifact-{version}.jar")
    with open(path, "wb") as file:
        file.write(jar)
    return path


def fetch_releases(coordinate):
    group, artifact = coordinate.split(":")
    url = f"{CENTRAL}/{group.replace('.', '/')}/{artifact}/maven-metadata.xml"
    with urllib.request.urlopen(url) as response:
        return [v.text for v in ElementTree.parse(response).getroot().iter("version")]


def to_shape(text):
    """The range with each run of digits written as N, so ranges of one form share a shape."""
    return re.sub(r"\d+", "N", text)


def read_bounds(range_text):
    return [p.strip().lstrip("<>=").strip() for p in range_text.split(",")]


def list_around(bound):
    """Versions on and beside a bound, with the qualifiers real artifacts carry."""
    found = {bound, bound.upper()}
    match = re.match(r"^(\d+(?:\.\d+)*)", bound)
    if not match:
        return found
    numbers = [int(n) for n in match.group(1).split(".")]
    rest = bound[match.end():]
    base = ".".join(map(str, numbers))
    found |= {base + q for q in QUALIFIERS} | {bound + ".1", bound + "-1"}
    for i in range(len(numbers)):
        up = numbers[: i + 1]
        up[i] += 1
        found.add(".".join(map(str, up)))
        if numbers[i] > 0:
            down = numbers[:]
            down[i] -= 1
            found |= {".".join(map(str, down)), ".".join(map(str, down)) + ".99",
                      ".".join(map(str, down)) + rest}
    found.add(base + ".1")
    return found


def to_series(version):
    match = re.match(r"^(\d+)(?:\.(\d+))?", version)
    return match.groups() if match else None


def is_near(range_text, version):
    """Whether a release shares its major and minor with a bound, which is where the answers turn."""
    return to_series(version) in {to_series(bound) for bound in read_bounds(range_text)}


def is_readable(version):
    """Whether the matcher reads this running version at all: ASCII, starting with a digit."""
    return re.fullmatch(r"[0-9][0-9A-Za-z._\-]*", version) is not None


def main():
    rows = {}
    ranges = sorted(set(fetch_advisories("")))
    seen = set()
    shapes = {}
    for ghsa, package, range_text in ranges:
        shape = to_shape(range_text)
        if range_text in seen or shapes.get(shape, 0) >= SHAPE_SAMPLE:
            continue
        shapes[shape] = shapes.get(shape, 0) + 1
        seen.add(range_text)
        for bound in read_bounds(range_text):
            for version in list_around(bound):
                rows[(ghsa, package, range_text, version)] = None

    for coordinate in NAMED:
        versions = fetch_releases(coordinate)
        named = {}
        for ghsa, name, range_text in sorted(set(fetch_advisories(f"&affects={coordinate}"))):
            # An advisory names every package it affects; only this artifact's own ranges apply.
            if name == coordinate:
                named.setdefault(range_text, (ghsa, name))
        for range_text, (ghsa, name) in named.items():
            for version in versions:
                if is_near(range_text, version):
                    rows[(ghsa, name, range_text, version)] = None

    keys = sorted(rows)
    with tempfile.TemporaryDirectory() as directory:
        jars = [fetch_maven(version, directory) for version in MAVENS]
        oracle = os.path.join(os.path.dirname(os.path.abspath(__file__)), "MavenRangeOracle.java")
        subprocess.run(["javac", "-d", directory, oracle], check=True)
        answers = subprocess.run(
            ["java", "-cp", directory, "MavenRangeOracle", *jars],
            input="".join(f"{k[2]}\t{k[3]}\n" for k in keys), check=True, capture_output=True, text=True,
        ).stdout.splitlines()

    grouped = {}
    for (ghsa, package, range_text, version), answer in zip(keys, answers):
        verdict = all(a == "1" for a in answer.split("\t")) and is_readable(version)
        grouped.setdefault((ghsa, package, range_text), ([], []))[0 if verdict else 1].append(version)
    out = sys.stdout
    out.write(f"# Generated by maven-ranges.py with Maven {' and '.join(MAVENS)}. Do not edit.\n")
    out.write("# The ghsa, package and range columns are excerpted from the GitHub Advisory Database\n")
    out.write("# (https://github.com/advisories; each row's record is https://github.com/advisories/<ghsa>),\n")
    out.write("# licensed CC BY 4.0 (https://creativecommons.org/licenses/by/4.0/). The version lists are added.\n")
    out.write("# ghsa\tpackage\trange\tclaimed versions\tunclaimed versions (each space separated)\n")
    for (ghsa, package, range_text), (claimed, unclaimed) in sorted(grouped.items()):
        out.write(f"{ghsa}\t{package}\t{range_text}\t{' '.join(sorted(claimed))}\t{' '.join(sorted(unclaimed))}\n")


if __name__ == "__main__":
    main()
