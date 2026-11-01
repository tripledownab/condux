#!/usr/bin/env python3
# Refuses a rendered compose configuration whose Redpanda could take the host's memory. Both deploys run
# it on their render before any container changes, and CI runs it on the base and production renders.
#
# Redpanda in dev-container mode passes --reserve-memory=0M and no --memory, so Seastar budgets all the
# memory it can see: the whole host with no container limit, the whole limit with one. So the broker
# needs a --memory budget AND a container limit above it, with no swap behind the limit. The base file
# carries a small budget for the dev stack; an overlay sets its own by restating the base command,
# because Compose replaces a command whole, so the restated command must be the base command with only
# its --memory changed: anything else means the base changed and the overlay kept running the old one.
#
#   docker compose ... config --format json > render.json
#   python3 check-redpanda-budget.py render.json
import json
import pathlib
import re
import subprocess
import sys

# Seastar's own spelling: a lowercase k or any of K, M, G, T, all powers of 1024. Nothing else, and not a
# lowercase m or g, which Seastar refuses at startup and which would pass a case-insensitive check.
UNITS = {"": 1, "k": 1024, "K": 1024, "M": 1024**2, "G": 1024**3, "T": 1024**4}


def to_bytes(size):
    match = re.fullmatch(r"(\d+)([kKMGT]?)", str(size))
    if not match or int(match.group(1)) == 0:
        raise SystemExit(f"refusing to deploy, redpanda's --memory is not a size: {size}")
    return int(match.group(1)) * UNITS[match.group(2)]


def to_base_command():
    render = subprocess.run(
        ["docker", "compose", "--env-file", "/dev/null", "-f", "docker-compose.yml", "config", "--format", "json"],
        cwd=pathlib.Path(__file__).resolve().parent,
        check=True,
        capture_output=True,
        text=True,
    ).stdout
    return [arg for arg in json.loads(render)["services"]["redpanda"]["command"] if not arg.startswith("--memory=")]


def main(render_path):
    redpanda = json.load(open(render_path))["services"]["redpanda"]
    command = redpanda.get("command") or []
    budget = [arg for arg in command if arg.startswith("--memory=")]
    if len(budget) != 1 or [arg for arg in command if arg not in budget] != to_base_command():
        raise SystemExit("refusing to deploy, redpanda's command is not the base command with one --memory")

    limit = redpanda.get("deploy", {}).get("resources", {}).get("limits", {}).get("memory")
    if not limit:
        raise SystemExit("refusing to deploy, redpanda has no container memory limit")
    if int(redpanda.get("memswap_limit") or 0) != int(limit):
        raise SystemExit("refusing to deploy, redpanda may swap past its limit")
    if to_bytes(budget[0].split("=", 1)[1]) >= int(limit):
        raise SystemExit("refusing to deploy, redpanda's --memory leaves no room under its container limit")

    print(f"redpanda check: budget {budget[0].split('=', 1)[1]} inside a {int(limit)} byte limit, no swap")


if __name__ == "__main__":
    main(sys.argv[1])
