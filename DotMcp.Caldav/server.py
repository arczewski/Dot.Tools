"""FastMCP tools for connecting to arbitrary CalDAV servers."""

from __future__ import annotations

import os
from collections.abc import Iterator
from contextlib import contextmanager
from dataclasses import dataclass
from datetime import date, datetime, timedelta
from typing import Any
from urllib.parse import urlparse

import caldav
from caldav.calendarobjectresource import CalendarObjectResource
from caldav.collection import Calendar
from fastmcp import FastMCP
from icalendar import Calendar as ICalendar

mcp = FastMCP("DotMcp.Caldav")


def _int_env(name: str, default: int) -> int:
    try:
        return int(os.getenv(name, str(default)))
    except ValueError:
        return default


REQUEST_TIMEOUT = _int_env("CALDAV_REQUEST_TIMEOUT", 30)
MAX_RESULTS = _int_env("CALDAV_MAX_RESULTS", 100)
MAX_SEARCH_DAYS = _int_env("CALDAV_MAX_SEARCH_DAYS", 366)
SEARCH_WINDOW_DAYS = _int_env("CALDAV_SEARCH_WINDOW_DAYS", 7)
_COMPONENT_METHODS = {
    "VEVENT": "add_event",
    "VTODO": "add_todo",
    "VJOURNAL": "add_journal",
}
_CALDAV_URL_ENV = "CALDAV_URL"
_CALDAV_USERNAME_ENV = "CALDAV_USERNAME"
_CALDAV_PASSWORD_ENV = "CALDAV_PASSWORD"


@dataclass(frozen=True)
class CalDAVConnection:
    """CalDAV connection data read from the MCP server process environment."""

    server_url: str
    username: str
    password: str


def _connection_from_environment() -> CalDAVConnection:
    values = {
        _CALDAV_URL_ENV: os.getenv(_CALDAV_URL_ENV),
        _CALDAV_USERNAME_ENV: os.getenv(_CALDAV_USERNAME_ENV),
        _CALDAV_PASSWORD_ENV: os.getenv(_CALDAV_PASSWORD_ENV),
    }
    missing_variables = [name for name, value in values.items() if not value]
    if missing_variables:
        raise ValueError(
            f"Missing required CalDAV environment variable(s): {', '.join(missing_variables)}."
        )

    return CalDAVConnection(
        server_url=values[_CALDAV_URL_ENV],
        username=values[_CALDAV_USERNAME_ENV],
        password=values[_CALDAV_PASSWORD_ENV],
    )


def _validate_url(value: str, parameter_name: str, allow_insecure_http: bool = False) -> None:
    parsed = urlparse(value)
    if parsed.scheme not in {"http", "https"} or not parsed.netloc:
        raise ValueError(f"{parameter_name} must be an absolute HTTP(S) URL.")
    if parsed.scheme == "http" and not allow_insecure_http:
        raise ValueError(
            f"{parameter_name} must use HTTPS unless allowInsecureHttp is explicitly enabled."
        )
    if parsed.username or parsed.password:
        raise ValueError(
            f"{parameter_name} must not contain credentials; set CALDAV_USERNAME and CALDAV_PASSWORD separately."
        )
    try:
        _ = parsed.port
    except ValueError as error:
        raise ValueError(f"{parameter_name} contains an invalid port.") from error


def _origin(url: str) -> tuple[str, str, int]:
    parsed = urlparse(url)
    return (
        parsed.scheme.casefold(),
        (parsed.hostname or "").casefold(),
        parsed.port or (443 if parsed.scheme == "https" else 80),
    )


def _validate_resource_url(
    value: str,
    parameter_name: str,
    server_url: str,
    allow_insecure_http: bool,
) -> None:
    _validate_url(value, parameter_name, allow_insecure_http)
    if _origin(value) != _origin(server_url):
        raise ValueError(f"{parameter_name} must use the same origin as the CalDAV server URL.")


def _parse_date_or_datetime(value: str | None, parameter_name: str) -> date | datetime | None:
    if value is None:
        return None

    normalized = value.strip()
    if not normalized:
        return None
    if normalized.endswith(("Z", "z")):
        normalized = f"{normalized[:-1]}+00:00"

    try:
        if "T" not in normalized and " " not in normalized:
            return date.fromisoformat(normalized)
        return datetime.fromisoformat(normalized)
    except ValueError as error:
        raise ValueError(
            f"{parameter_name} must be an ISO 8601 date or date-time, for example "
            "2026-07-21 or 2026-07-21T09:30:00+00:00."
        ) from error


def _component_type(value: str) -> str:
    component_type = value.upper()
    if component_type not in _COMPONENT_METHODS:
        supported = ", ".join(_COMPONENT_METHODS)
        raise ValueError(f"componentType must be one of: {supported}.")
    return component_type


def _validate_icalendar_data(
    icalendar_data: str,
    component_type: str | None = None,
) -> str:
    if not icalendar_data.strip():
        raise ValueError("icalendarData is required.")
    try:
        calendar = ICalendar.from_ical(icalendar_data)
    except (TypeError, ValueError) as error:
        raise ValueError("icalendarData must be a valid VCALENDAR payload.") from error

    if calendar.name != "VCALENDAR" or not calendar.get("VERSION"):
        raise ValueError("icalendarData must contain a VCALENDAR with a VERSION property.")

    non_timezone_components = [
        component for component in calendar.subcomponents if component.name != "VTIMEZONE"
    ]
    if not non_timezone_components or any(
        component.name not in _COMPONENT_METHODS for component in non_timezone_components
    ):
        raise ValueError("icalendarData may contain only VEVENT, VTODO, VJOURNAL, and VTIMEZONE components.")

    actual_component_type = non_timezone_components[0].name
    if any(component.name != actual_component_type for component in non_timezone_components):
        raise ValueError("icalendarData must not mix VEVENT, VTODO, and VJOURNAL components.")
    if component_type is not None and actual_component_type != component_type:
        raise ValueError(f"icalendarData does not match componentType {component_type}.")

    uids = {str(component.get("UID", "")).strip() for component in non_timezone_components}
    if "" in uids or len(uids) != 1:
        raise ValueError("Every calendar component must have the same non-empty UID.")
    if any(not component.get("DTSTAMP") for component in non_timezone_components):
        raise ValueError("Every calendar component must include a DTSTAMP property.")
    return actual_component_type


def _ensure_success(response: Any, operation: str) -> None:
    status = getattr(response, "status", None)
    if not isinstance(status, int) or not 200 <= status < 300:
        rendered_status = str(status) if status is not None else "unknown"
        raise RuntimeError(f"{operation} failed with HTTP status {rendered_status}.")


def _error_result(error: Exception, connection: CalDAVConnection | None = None) -> dict[str, str]:
    """Return a useful failure without reflecting environment-supplied credentials."""
    detail = str(error).strip()
    if connection is not None:
        for secret in (connection.password, connection.username):
            if secret:
                detail = detail.replace(secret, "[REDACTED]")
    if not detail:
        detail = "The CalDAV server did not provide an error message."
    return {"error": f"{type(error).__name__}: {detail[:500]}"}


@contextmanager
def _client(
    server_url: str,
    username: str,
    password: str,
    verify_ssl: bool,
    allow_insecure_http: bool,
) -> Iterator[caldav.DAVClient]:
    _validate_url(server_url, _CALDAV_URL_ENV, allow_insecure_http)
    if not username:
        raise ValueError("username is required.")
    if not password:
        raise ValueError("password is required.")

    client = caldav.DAVClient(
        url=server_url,
        username=username,
        password=password,
        timeout=REQUEST_TIMEOUT,
        ssl_verify_cert=verify_ssl,
    )
    client.session.trust_env = False
    client.session.max_redirects = 0

    original_session_request = client.session.request

    def guarded_session_request(method: str, url: str, *args: Any, **kwargs: Any) -> Any:
        _validate_resource_url(str(url), "CalDAV request URL", server_url, allow_insecure_http)
        return original_session_request(method, url, *args, **kwargs)

    client.session.request = guarded_session_request
    original_client_request = client.request

    def guarded_client_request(
        url: str,
        method: str = "GET",
        body: str = "",
        headers: dict[str, str] | None = None,
        rate_limit_time_slept: int = 0,
    ) -> Any:
        _validate_resource_url(str(url), "CalDAV request URL", server_url, allow_insecure_http)
        response = original_client_request(url, method, body, headers, rate_limit_time_slept)
        _ensure_success(response, f"CalDAV {method} request")
        return response

    client.request = guarded_client_request
    try:
        yield client
    finally:
        client.close()


def _calendar(
    client: caldav.DAVClient,
    server_url: str,
    calendar_url: str,
    allow_insecure_http: bool,
) -> Calendar:
    _validate_resource_url(calendar_url, "calendarUrl", server_url, allow_insecure_http)
    return Calendar(client=client, url=calendar_url)


def _calendar_name(calendar: Calendar) -> str | None:
    name = getattr(calendar, "name", None)
    if name:
        return str(name)
    try:
        return str(calendar.get_display_name())
    except Exception:  # pragma: no cover - server-specific calendar metadata
        return None


def _search_in_windows(
    calendar: Calendar,
    search_args: dict[str, Any],
    start_value: date | datetime,
    end_value: date | datetime,
    max_results: int,
) -> list[CalendarObjectResource]:
    if SEARCH_WINDOW_DAYS < 1:
        raise ValueError("CALDAV_SEARCH_WINDOW_DAYS must be at least 1.")

    results: list[CalendarObjectResource] = []
    seen_urls: set[str] = set()
    expand_recurring = bool(search_args.get("expand"))
    window_start = start_value
    while window_start < end_value:
        window_end = min(window_start + timedelta(days=SEARCH_WINDOW_DAYS), end_value)
        for calendar_object in calendar.search(
            **search_args,
            start=window_start,
            end=window_end,
        ):
            object_url = str(calendar_object.url)
            if not expand_recurring and object_url in seen_urls:
                continue
            seen_urls.add(object_url)
            results.append(calendar_object)
            if len(results) >= max_results:
                return results
        window_start = window_end
    return results


def _object_summary(calendar_object: CalendarObjectResource, include_data: bool) -> dict[str, Any]:
    result: dict[str, Any] = {
        "url": str(calendar_object.url),
        "uid": calendar_object.id,
    }
    try:
        component = calendar_object.get_icalendar_component()
        result["componentType"] = component.name
        for source, destination in (("SUMMARY", "summary"), ("DTSTART", "start"), ("DTEND", "end"), ("DUE", "due")):
            value = component.get(source)
            if value is not None:
                result[destination] = str(value)
    except Exception:  # pragma: no cover - malformed calendar data from a remote server
        result["componentType"] = None

    if include_data:
        result["icalendarData"] = calendar_object.get_data()
    return result


@mcp.tool(name="CheckCalDAVConnection")
def check_caldav_connection(
    verifySsl: bool = True,
    allowInsecureHttp: bool = False,
) -> dict[str, Any]:
    """Verify the CalDAV login supplied through environment variables.

    Requires CALDAV_URL, CALDAV_USERNAME, and CALDAV_PASSWORD. Values are read from the MCP server
    process environment and are never stored by this MCP server.
    """
    connection: CalDAVConnection | None = None
    try:
        connection = _connection_from_environment()
        with _client(
            connection.server_url,
            connection.username,
            connection.password,
            verifySsl,
            allowInsecureHttp,
        ) as client:
            return {
                "dav": client.supports_dav(),
                "caldav": client.supports_caldav(),
                "scheduling": client.supports_scheduling(),
            }
    except Exception as error:  # pragma: no cover - network and remote server dependent
        return _error_result(error, connection)


@mcp.tool(name="ListCalendars")
def list_calendars(
    verifySsl: bool = True,
    allowInsecureHttp: bool = False,
) -> list[dict[str, Any]] | dict[str, str]:
    """List calendars for the CalDAV login supplied through environment variables.

    Requires CALDAV_URL, CALDAV_USERNAME, and CALDAV_PASSWORD. Values are read from the MCP server
    process environment and are never stored by this MCP server.
    """
    connection: CalDAVConnection | None = None
    try:
        connection = _connection_from_environment()
        with _client(
            connection.server_url,
            connection.username,
            connection.password,
            verifySsl,
            allowInsecureHttp,
        ) as client:
            return [
                {"url": str(calendar.url), "name": _calendar_name(calendar)}
                for calendar in client.get_calendars()
            ]
    except Exception as error:  # pragma: no cover - network and remote server dependent
        return _error_result(error, connection)


@mcp.tool(name="CreateCalendar")
def create_calendar(
    name: str,
    supportedComponents: list[str] | None = None,
    verifySsl: bool = True,
    allowInsecureHttp: bool = False,
) -> dict[str, Any]:
    """Create a calendar owned by the user identified through environment variables.

    Set supportedComponents, such as ["VTODO"], only when a server needs an explicit component set.
    Requires CALDAV_URL, CALDAV_USERNAME, and CALDAV_PASSWORD.
    """
    connection: CalDAVConnection | None = None
    try:
        connection = _connection_from_environment()
        with _client(
            connection.server_url,
            connection.username,
            connection.password,
            verifySsl,
            allowInsecureHttp,
        ) as client:
            principal = client.get_principal()
            calendar = principal.make_calendar(
                name=name,
                supported_calendar_component_set=supportedComponents,
            )
            return {"url": str(calendar.url), "name": _calendar_name(calendar)}
    except Exception as error:  # pragma: no cover - network and remote server dependent
        return _error_result(error, connection)


@mcp.tool(name="SearchCalendarObjects")
def search_calendar_objects(
    calendarUrl: str,
    componentType: str = "VEVENT",
    start: str | None = None,
    end: str | None = None,
    summaryContains: str | None = None,
    expandRecurrences: bool = False,
    includeCalendarData: bool = False,
    maxResults: int = 50,
    verifySsl: bool = True,
    allowInsecureHttp: bool = False,
) -> list[dict[str, Any]] | dict[str, str]:
    """Search VEVENT, VTODO, or VJOURNAL objects using the CalDAV environment configuration.

    Requires CALDAV_URL, CALDAV_USERNAME, and CALDAV_PASSWORD. start and end are required ISO 8601
    dates or date-times and must fit within the configured range.
    """
    connection: CalDAVConnection | None = None
    try:
        connection = _connection_from_environment()
        component_type = _component_type(componentType)
        if maxResults < 1:
            raise ValueError("maxResults must be at least 1.")
        if maxResults > MAX_RESULTS:
            raise ValueError(f"maxResults cannot exceed the configured maximum of {MAX_RESULTS}.")

        start_value = _parse_date_or_datetime(start, "start")
        end_value = _parse_date_or_datetime(end, "end")
        if start_value is None or end_value is None:
            raise ValueError("SearchCalendarObjects requires both start and end.")
        if isinstance(start_value, datetime) != isinstance(end_value, datetime):
            raise ValueError("start and end must both be dates or both be date-times.")
        if end_value <= start_value:
            raise ValueError("end must be after start.")
        start_date = start_value.date() if isinstance(start_value, datetime) else start_value
        end_date = end_value.date() if isinstance(end_value, datetime) else end_value
        if MAX_SEARCH_DAYS < 1:
            raise ValueError("CALDAV_MAX_SEARCH_DAYS must be at least 1.")
        if (end_date - start_date).days > MAX_SEARCH_DAYS:
            raise ValueError(
                f"The requested range exceeds the configured maximum of {MAX_SEARCH_DAYS} day(s)."
            )

        search_args: dict[str, Any] = {
            "event": component_type == "VEVENT",
            "todo": component_type == "VTODO",
            "journal": component_type == "VJOURNAL",
            "expand": expandRecurrences,
        }
        if summaryContains:
            search_args["summary"] = summaryContains

        with _client(
            connection.server_url,
            connection.username,
            connection.password,
            verifySsl,
            allowInsecureHttp,
        ) as client:
            calendar = _calendar(client, connection.server_url, calendarUrl, allowInsecureHttp)
            results = _search_in_windows(calendar, search_args, start_value, end_value, maxResults)
            return [_object_summary(result, includeCalendarData) for result in results]
    except Exception as error:  # pragma: no cover - network and remote server dependent
        return _error_result(error, connection)


@mcp.tool(name="GetCalendarObject")
def get_calendar_object(
    objectUrl: str,
    verifySsl: bool = True,
    allowInsecureHttp: bool = False,
) -> dict[str, Any]:
    """Retrieve one calendar object by URL using the CalDAV environment configuration.

    Requires CALDAV_URL, CALDAV_USERNAME, and CALDAV_PASSWORD.
    """
    connection: CalDAVConnection | None = None
    try:
        connection = _connection_from_environment()
        _validate_resource_url(objectUrl, "objectUrl", connection.server_url, allowInsecureHttp)
        with _client(
            connection.server_url,
            connection.username,
            connection.password,
            verifySsl,
            allowInsecureHttp,
        ) as client:
            calendar_object = CalendarObjectResource(client=client, url=objectUrl)
            calendar_object.load()
            result = _object_summary(calendar_object, include_data=True)
            return result
    except Exception as error:  # pragma: no cover - network and remote server dependent
        return _error_result(error, connection)


@mcp.tool(name="CreateCalendarObject")
def create_calendar_object(
    calendarUrl: str,
    icalendarData: str,
    componentType: str = "VEVENT",
    verifySsl: bool = True,
    allowInsecureHttp: bool = False,
) -> dict[str, Any]:
    """Create a VEVENT, VTODO, or VJOURNAL using the CalDAV environment configuration.

    All non-timezone calendar components must match componentType and share one UID. Requires
    CALDAV_URL, CALDAV_USERNAME, and CALDAV_PASSWORD.
    """
    connection: CalDAVConnection | None = None
    try:
        connection = _connection_from_environment()
        component_type = _component_type(componentType)
        _validate_icalendar_data(icalendarData, component_type)

        with _client(
            connection.server_url,
            connection.username,
            connection.password,
            verifySsl,
            allowInsecureHttp,
        ) as client:
            calendar = _calendar(client, connection.server_url, calendarUrl, allowInsecureHttp)
            method = getattr(calendar, _COMPONENT_METHODS[component_type])
            created = method(icalendarData)
            return _object_summary(created, include_data=False)
    except Exception as error:  # pragma: no cover - network and remote server dependent
        return _error_result(error, connection)


@mcp.tool(name="UpdateCalendarObject")
def update_calendar_object(
    objectUrl: str,
    icalendarData: str,
    verifySsl: bool = True,
    allowInsecureHttp: bool = False,
) -> dict[str, str]:
    """Replace one calendar object's raw iCalendar data using the CalDAV environment configuration.

    Preserve the calendar object's UID and component type when updating. Requires CALDAV_URL,
    CALDAV_USERNAME, and CALDAV_PASSWORD.
    """
    connection: CalDAVConnection | None = None
    try:
        connection = _connection_from_environment()
        _validate_resource_url(objectUrl, "objectUrl", connection.server_url, allowInsecureHttp)
        _validate_icalendar_data(icalendarData)

        with _client(
            connection.server_url,
            connection.username,
            connection.password,
            verifySsl,
            allowInsecureHttp,
        ) as client:
            response = client.put(
                objectUrl,
                icalendarData,
                headers={"Content-Type": "text/calendar; charset=utf-8"},
            )
            _ensure_success(response, "UpdateCalendarObject")
            return {"url": objectUrl, "status": "updated"}
    except Exception as error:  # pragma: no cover - network and remote server dependent
        return _error_result(error, connection)


@mcp.tool(name="DeleteCalendarObject")
def delete_calendar_object(
    objectUrl: str,
    verifySsl: bool = True,
    allowInsecureHttp: bool = False,
) -> dict[str, str]:
    """Delete one calendar object by URL using the CalDAV environment configuration.

    Requires CALDAV_URL, CALDAV_USERNAME, and CALDAV_PASSWORD.
    """
    connection: CalDAVConnection | None = None
    try:
        connection = _connection_from_environment()
        _validate_resource_url(objectUrl, "objectUrl", connection.server_url, allowInsecureHttp)
        with _client(
            connection.server_url,
            connection.username,
            connection.password,
            verifySsl,
            allowInsecureHttp,
        ) as client:
            response = client.delete(objectUrl)
            _ensure_success(response, "DeleteCalendarObject")
            return {"url": objectUrl, "status": "deleted"}
    except Exception as error:  # pragma: no cover - network and remote server dependent
        return _error_result(error, connection)


if __name__ == "__main__":
    mcp.run()
