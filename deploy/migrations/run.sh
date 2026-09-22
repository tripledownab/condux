#!/usr/bin/env bash
# Apply the Condux database migrations (baked into the image from the repo). Idempotent, safe to
# re-run on every install/upgrade. Run by the Helm migrations hook Job; config comes from the same
# env the services use (CONDUX_POSTGRES, CONDUX_CLICKHOUSE_URL/USER/PASSWORD).
#
# Both stores are reached by a different client than the services use, so each connection setting is
# translated rather than passed through, and a setting that governs how the connection is secured is
# REFUSED when it cannot be translated. Skipping one would run every migration, and send the
# password that authorises them, over whatever the network offered, while reporting success.
set -euo pipefail

# The image bakes the migration SQL here. Overridable so this script can be driven against a
# fixture tree without an image build.
MIGRATIONS_DIR="${MIGRATIONS_DIR:-/migrations}"

# Npgsql writes an enum value in C# casing and libpq compares its own spelling byte for byte, so a
# value that names a MODE has to be folded, while a path, a host or a name must be passed through
# untouched. libpq refuses an unfolded mode at connect time; nothing refuses a folded path, which is
# the direction to be careful in.
fold_mode() { printf '%s' "$1" | tr '[:upper:]' '[:lower:]' | tr -d ' -'; }

# True when this splitter cannot parse the value, recording the refusal as it goes. A value that
# opens with a quote is a quoted value, which is how the .NET parser Npgsql builds on carries a ';'
# inside one. The loop below splits on ';' and cuts through it, so psql would be handed a truncated
# credential and would fail authentication with nothing naming the cause, while the services read
# the same string correctly. A quote anywhere else is literal and is left alone. Reads `kv` from the
# loop, so it is only callable from inside it.
refuse_quoted() { # refuse_quoted <raw value from the connection string>
  case "$1" in
    '"'* | "'"*)
      refused+=("${kv%%=*}: a quoted value, which this runner does not parse")
      return 0
      ;;
  esac
  return 1
}

# The one place a value becomes a libpq variable. The quoting rule is asked here rather than beside
# the split, because a quoted value on a key that is skipped anyway changes nothing and must not
# fail an install. An arm that TRANSFORMS its value before calling this has to ask separately: what
# arrives here is the built string, which never opens with the operator's quote.
set_pg() { # set_pg <libpq variable> <value>
  if refuse_quoted "$2"; then return 0; fi
  export "$1=$2"
}

# Translate the Npgsql-style CONDUX_POSTGRES ("Key=Value;...") into libpq PG* env for psql. libpq
# defaults to sslmode=prefer, which falls back to plaintext whenever the server offers it, so a
# dropped SSL Mode does not fail: it connects, applies the schema, and says nothing.
refused=()
IFS=';' read -ra _parts <<< "${CONDUX_POSTGRES}"
for kv in "${_parts[@]}"; do
  key="$(printf '%s' "${kv%%=*}" | tr '[:upper:]' '[:lower:]' | tr -d ' ')"
  val="${kv#*=}"
  case "$key" in
    # A trailing or doubled ';' leaves an empty field.
    "") ;;
    host|server) set_pg PGHOST "$val" ;;
    port) set_pg PGPORT "$val" ;;
    username|userid|uid|user) set_pg PGUSER "$val" ;;
    password|psw|pwd) set_pg PGPASSWORD "$val" ;;
    passfile) set_pg PGPASSFILE "$val" ;;
    database|db) set_pg PGDATABASE "$val" ;;
    # Not a pooled-client setting: it decides which schema unqualified CREATE statements land in, so
    # dropping it would build the schema somewhere the services do not look. libpq spells it as a
    # server command-line option, whose documented form is `-c name=value`, and splits that string on
    # spaces unless they carry a backslash, so a path naming two schemas needs both escaped or it
    # arrives truncated at the first space.
    searchpath)
      # Asked on the RAW value: set_pg is handed the built `-c search_path=...`, which never opens
      # with the operator's quote, so it cannot see one here.
      if ! refuse_quoted "$val"; then
        escaped="${val//\\/\\\\}"
        set_pg PGOPTIONS "-c search_path=${escaped// /\\ }"
      fi
      ;;
    # The other way to say the same thing: it sends server parameters at connection start and may
    # carry its own search_path. Translating it would fight the arm above over one variable, so this
    # runner refuses rather than silently picking a winner.
    options) refused+=("${kv%%=*}: not translated by this runner") ;;
    sslcertificate) set_pg PGSSLCERT "$val" ;;
    sslkey) set_pg PGSSLKEY "$val" ;;
    sslpassword) set_pg PGSSLPASSWORD "$val" ;;
    rootcertificate) set_pg PGSSLROOTCERT "$val" ;;
    # A mode, so it folds. libpq accepts exactly these three spellings and refuses the connection
    # for anything else, which is what passing Npgsql's own `Require` straight through would do.
    channelbinding)
      binding="$(fold_mode "$val")"
      case "$binding" in
        disable|prefer|require) set_pg PGCHANNELBINDING "$binding" ;;
        *) refused+=("${kv%%=*}: not one of the three channel binding modes") ;;
      esac
      ;;
    krbsrvname|kerberosservicename) set_pg PGKRBSRVNAME "$val" ;;
    # Carried by Npgsql 8 as an obsolete no-op ("no longer needed and does nothing"), so there is no
    # posture here to preserve and refusing it would fail an install over a setting that is ignored
    # on the services' own connections. Listed before the refusal below, which its name would match.
    trustservercertificate) ;;
    sslmode)
      # Both libraries name the same six modes. Only the two verify forms are spelled differently.
      mode="$(fold_mode "$val")"
      case "$mode" in
        disable|allow|prefer|require) set_pg PGSSLMODE "$mode" ;;
        verifyca) set_pg PGSSLMODE "verify-ca" ;;
        verifyfull) set_pg PGSSLMODE "verify-full" ;;
        *) refused+=("${kv%%=*}: not one of the six SSL modes") ;;
      esac
      ;;
    # Npgsql names more security settings than the arms above translate, and newer versions keep
    # adding them. Refusing one says so where an operator can act on it; the alternative is a
    # quieter connection than the one they asked for.
    ssl*|*certificate*|gssenc*|requireauth)
      refused+=("${kv%%=*}: not translated by this runner") ;;
    # Everything else shapes a long-lived pooled client: pool sizes, timeouts, buffer sizes, what
    # goes in an exception message. None of them decides where this one-shot run connects, how it
    # authenticates or where it writes. Passfile and Search Path did, which is why they are
    # translated above rather than left to this arm.
    *) ;;
  esac
done

if [ ${#refused[@]} -gt 0 ]; then
  echo "CONDUX_POSTGRES carries settings this runner cannot apply to psql:" >&2
  printf '    %s\n' "${refused[@]}" >&2
  echo "Each line names the setting and the reason. Correct the value, remove the setting, or" >&2
  echo "state the same thing with one this runner does translate. A quoted value has to be given" >&2
  echo "unquoted, so a credential containing a ';' needs a different way in, such as Passfile." >&2
  exit 1
fi

# Both stores' connection settings are resolved BEFORE a single migration is applied. Every
# refusal above and below is input validation, and a migration is not undoable, so validating
# the second store after the first one is migrated would leave a half-applied schema behind a
# message about a typo. Measured: it did.
# ClickHouse via clickhouse-client (native protocol, multi-statement files). The host comes from the
# HTTP URL the services use, and so does the scheme, which is the only thing in that URL saying
# whether the connection is encrypted. The native port is 9000 in the clear and 9440 under --secure;
# whichever the scheme selects must be reachable from this Job.
# A URI scheme is case-insensitive and an implementation should read an uppercase one as the same
# scheme, so it is lowercased before it is matched. Not fold_mode: that also drops hyphens, which a
# scheme is allowed to contain, and neither of the two this accepts has one.
CH_SCHEME="$(printf '%s' "${CONDUX_CLICKHOUSE_URL%%://*}" | tr '[:upper:]' '[:lower:]')"
case "${CH_SCHEME}" in
  https) CH_SECURE="--secure" ;;
  http) CH_SECURE="" ;;
  *)
    # Names the variable and the requirement, never the value. A URL is allowed to carry userinfo,
    # this branch is the one a malformed URL reaches, and a Job's log outlives the install. The
    # refusals above report the key and not the value for the same reason.
    echo "CONDUX_CLICKHOUSE_URL must begin http:// or https://, so that this runner can tell" >&2
    echo "whether to encrypt the connection. Its value is not repeated here: it may carry a" >&2
    echo "credential, and this log outlives the install." >&2
    exit 1
    ;;
esac
# Take the authority apart in the order the URL grammar defines, because cutting at the first ':'
# reads an IPv6 literal as "[" and the user half of a userinfo pair as the host. The brackets stay
# on: that is the form clickhouse-client reports back, and it keeps the port unambiguous.
CH_AUTHORITY="${CONDUX_CLICKHOUSE_URL#*://}"
CH_AUTHORITY="${CH_AUTHORITY%%[/?#]*}"
CH_AUTHORITY="${CH_AUTHORITY##*@}"
case "${CH_AUTHORITY}" in
  "["*) CH_HOST="${CH_AUTHORITY%%]*}]" ;;
  *) CH_HOST="${CH_AUTHORITY%%:*}" ;;
esac

echo "==> Postgres migrations"
applied=0
# A glob rather than `ls`, because pathname expansion is already sorted and an unmatched glob is
# left literal, which the -e test below turns into a loud failure. An empty tree must not read as a
# successful run: this Job is what stands between a fresh install and a schema.
for f in "${MIGRATIONS_DIR}"/postgres/*.sql; do
  [ -e "$f" ] || break
  echo "    $f"
  psql -v ON_ERROR_STOP=1 -f "$f"
  applied=$((applied + 1))
done
if [ "$applied" -eq 0 ]; then
  echo "No Postgres migrations found under ${MIGRATIONS_DIR}/postgres" >&2
  exit 1
fi

echo "==> ClickHouse migrations (host ${CH_HOST})"
applied=0
for f in "${MIGRATIONS_DIR}"/clickhouse/*.sql; do
  [ -e "$f" ] || break
  echo "    $f"
  clickhouse-client ${CH_SECURE:+"$CH_SECURE"} --host "${CH_HOST}" \
    --user "${CONDUX_CLICKHOUSE_USER}" --password "${CONDUX_CLICKHOUSE_PASSWORD}" --multiquery < "$f"
  applied=$((applied + 1))
done
if [ "$applied" -eq 0 ]; then
  echo "No ClickHouse migrations found under ${MIGRATIONS_DIR}/clickhouse" >&2
  exit 1
fi

echo "==> Migrations complete"
