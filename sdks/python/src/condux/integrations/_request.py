"""Shaping the request for the wire, shared by the WSGI and ASGI integrations.

Only the shaping is shared. Extracting the values is protocol specific (WSGI reads PATH_INFO from an
environ, ASGI reads path from a scope and decodes the query bytes), so each integration does its own
extraction and hands the result here. Keeping the shaping in one place means the field names and the
drop-blanks rule cannot drift between the two, which is exactly what would happen the next time a field
is added and only one file is remembered.

Deliberately no headers, in either protocol: they carry cookies and authorization, and while the relay
scrubs sensitive keys at ingest, not sending credentials at all is the stronger guarantee.
"""

from __future__ import annotations

from typing import Dict, Optional


def request_fields(url: Optional[str], method: Optional[str], query_string: Optional[str]) -> Dict[str, str]:
    """The request in the Sentry store shape the relay parses, with blank parts omitted.

    Blanks are dropped rather than sent as empty strings, so a partially known request does not leave the
    relay to decide what an empty url means.
    """
    fields = {"url": url or "", "method": method or "", "query_string": query_string or ""}
    return {key: value for key, value in fields.items() if value}
