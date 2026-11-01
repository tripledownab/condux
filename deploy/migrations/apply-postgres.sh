#!/bin/sh
# Apply the Postgres migrations in one directory, in filename order, skipping what this database has
# already applied. The one copy of that rule: compose's postgres-init job runs this file, and so does the
# Helm job's run.sh once it has translated its connection string into libpq's PG* variables.
#
# Usage: apply-postgres.sh <directory of *.sql files>, with the database named by the PG* environment.
#
# WHY A LEDGER. Every file used to run on every deploy, and twice under compose
# (deploy/apply-migrations.sh, then the `up` that follows), so each one had to be idempotent and was paid
# for every time. Replaying an unchanged file is not free: ALTER TABLE ... ADD COLUMN IF NOT EXISTS takes
# the table's ACCESS EXCLUSIVE lock before it finds the column, so a replay waited behind any transaction
# holding a lock on that table and everything after it queued behind the replay; a backfill UPDATE scanned
# its table again; and a data fix like 0029's ran again over rows written since, which is not what it was
# written to do.
#
# WHAT RUNS. condux_migrations records each file applied with the SHA-256 of its SQL (content_sha). When
# every file not recorded at its current content is new and sorts after every recorded one, only those
# run: replaying the recorded files first would bring the database back to the state it is already in, so
# skipping them changes nothing. Any other difference (a recorded file edited, a new file sorting between recorded ones,
# a recorded file gone) replays every file from the first, which is exactly what every deploy used to do:
# run alone, an edited middle file would see the database without what the files before it re-establish
# on a replay. So every file must still be idempotent.
#
# A FAILURE. A file is recorded only after it applies cleanly. A full replay also writes a marker row
# before its first file and removes it only after its last, so one that fails part way is replayed again
# by the next run, whatever it had recorded so far: without the marker, the files it had passed read as
# recorded and the rest of the replay never ran.
#
# Not guarded: two runs at once against one database. Neither deploy starts one while another runs.
set -eu

dir="${1:?usage: apply-postgres.sh <directory of *.sql files>}"

# A glob rather than `ls`, because pathname expansion is already sorted. An unmatched glob is left
# literal, and an empty directory is a wrong mount rather than a database with nothing to do, so it is
# refused before anything touches the database.
set -- "$dir"/*.sql
if [ ! -e "$1" ]; then
  echo "no migrations found under $dir" >&2
  exit 1
fi

# Names are matched as whole lines below, so a name holding a newline would match the wrong lines and
# could hide an edit. Refused, like the empty directory, before anything touches the database.
newline='
'
for f in "$@"; do
  case "$f" in
    *"$newline"*)
      echo "a migration file name holds a newline, which this script cannot match safely" >&2
      exit 1
      ;;
  esac
done

# -X so no psqlrc changes how a file is read, -q to keep the log to the lines below.
psql_run() { psql -X -q -v ON_ERROR_STOP=1 "$@"; }

psql_run -c "SET client_min_messages = warning;
CREATE TABLE IF NOT EXISTS condux_migrations (
    file       TEXT        PRIMARY KEY,
    sha256     TEXT        NOT NULL,
    applied_at TIMESTAMPTZ NOT NULL DEFAULT now()
)"

# Not a file name, since every file name ends in .sql.
MARKER='(full replay in progress)'

names="$(for f in "$@"; do basename "$f"; done)"

# The hash of what a file does: its lines without whole-line `--` comments, so rewording a comment in a
# recorded file does not replay every file. Blank lines stay in, since inside a string literal they are
# data. A comment after code on the same line still counts as an edit. The one change this misses is to a
# line beginning `--` inside a multi-line string literal or dollar-quoted string that is data rather than
# code, which no file here has. sed rather than grep, because GNU grep, in the Helm image, skips a line
# that is not valid UTF-8 as binary, and busybox grep, in compose, does not.
content_sha() { sed '/^[[:space:]]*--/d' "$1" | sha256sum | cut -d ' ' -f 1; }

# One line per recorded file, "<file> <sha256>", matched whole.
recorded="$(psql_run -A -t -c "SELECT file || ' ' || sha256 FROM condux_migrations")"
recorded_names() { [ -z "$recorded" ] || printf '%s\n' "$recorded" | sed 's/ [^ ]*$//'; }
is_recorded() { printf '%s\n' "$recorded" | grep -Fqx -- "$(basename "$1") $(content_sha "$1")"; }
is_known() { recorded_names | grep -Fqx -- "$(basename "$1")"; }
is_present() { printf '%s\n' "$names" | grep -Fqx -- "$1"; }

# A replay left unfinished, or a recorded file no longer here, means a full replay.
# Read from a here-document rather than a pipe, so the loop runs in this shell and can set full.
full=false
while IFS= read -r name; do
  if [ -n "$name" ] && { [ "$name" = "$MARKER" ] || ! is_present "$name"; }; then
    full=true
  fi
done <<EOF
$(recorded_names)
EOF

# Otherwise the first file to run is the first one not recorded at its content, unless that one was
# recorded with other content (an edit) or a recorded file sorts after it (an insertion), which also mean
# a full replay. Only new files after everything recorded may skip it, since replaying every recorded file
# returns the database to where it is; replaying just the ones before an edited file need not.
first=""
if [ "$full" = false ]; then
  for f in "$@"; do
    if ! is_recorded "$f"; then
      if is_known "$f"; then
        full=true
        break
      fi
      [ -n "$first" ] || first="$f"
    elif [ -n "$first" ]; then
      full=true
      break
    fi
  done
fi
if [ "$full" = true ]; then
  first="$1"
fi

# psql substitutes :'name' only in what it reads as a script, never in -c, so these go through stdin.
record() { # record <file> <sha256>
  printf '%s\n' "INSERT INTO condux_migrations (file, sha256) VALUES (:'file', :'sha')
    ON CONFLICT (file) DO UPDATE SET sha256 = EXCLUDED.sha256, applied_at = now();" |
    psql_run -v file="$1" -v sha="$2" -f -
}

if [ "$full" = true ]; then
  record "$MARKER" pending
fi

applied=0
for f in "$@"; do
  if [ -z "$first" ] || { [ "$applied" -eq 0 ] && [ "$f" != "$first" ]; }; then
    continue
  fi
  file="$(basename "$f")"
  echo "applying $file"
  psql_run -f "$f"
  record "$file" "$(content_sha "$f")"
  applied=$((applied + 1))
done

# Every row naming no file here goes last, in one statement: the marker, which no file is named, and the
# rows of files removed, which a replay of what is here has superseded. Until this commits, the next run
# replays everything again.
if [ "$full" = true ]; then
  printf '%s\n' "DELETE FROM condux_migrations WHERE NOT (file = ANY (string_to_array(:'names', E'\n')));" |
    psql_run -v names="$names" -f -
fi

echo "postgres migrations: $applied applied, $(($# - applied)) skipped"
