# Dot.Tools MCP servers

`Dot.Tools` provides five local stdio [Model Context Protocol (MCP)] servers:

| Server | Purpose | Runtime |
| --- | --- | --- |
| `dotmcp-assembly` | Inspect .NET assemblies and provide Roslyn-based IntelliSense tools. | .NET 10 SDK |
| `dotmcp-io` | List, read, search, and edit files. | Python / FastMCP |
| `dotmcp-fetch` | Fetch web content and query a SearXNG instance. | Python / FastMCP |
| `dotmcp-terminal` | Execute commands through the host-native shell. | Python / FastMCP |
| `dotmcp-caldav` | Connect to arbitrary CalDAV servers using `CALDAV_*` environment variables. | Python / FastMCP |

## Quick start (Linux/macOS)

Prerequisites:

- .NET 10 SDK (`dotnet`)
- Python 3.10 or later (`python3`)
- Git

Clone the repository and run the bootstrap script from its root:

```bash
git clone https://github.com/arczewski/Dot.Tools.git
cd Dot.Tools
bash ./install.sh
```

`install.sh` creates `.venv` in the repository root and installs every pinned Python dependency from:

- `DotMcp.IO/requirements.txt`
- `DotMcp.Fetch/requirements.txt`
- `DotMcp.Terminal/requirements.txt`
- `DotMcp.Caldav/requirements.txt`

Set `PYTHON_BIN` when Python 3.10+ is not exposed as one of the usual `python3` commands:

```bash
PYTHON_BIN=/path/to/python3.12 bash ./install.sh
```

## Configure your MCP harness

Load the repository-root [`mcp.json`](./mcp.json) in your MCP harness. It defines all five local stdio servers and runs them over standard input/output.

The paths in `mcp.json` are relative to the repository root, and each entry sets `cwd` to `.`. Configure the harness to start these entries from the cloned repository directory, or replace `cwd` and the relative paths with that clone’s absolute path if the harness does not resolve configuration-relative paths.

The Assembly entry uses `dotnet run`, so its first launch restores and builds the .NET project. The four Python entries run through `.venv/bin/python`, which is created by `install.sh`.

## Fetch server configuration

`dotmcp-fetch` works without configuration for direct URL fetching. Its `Search` tool expects a SearXNG instance at `http://127.0.0.1:8888` by default. Set environment variables in the harness configuration to override its endpoints or limits:

| Variable | Default | Purpose |
| --- | --- | --- |
| `JINA_API_URL` | `https://r.jina.ai/{url}` | Markdown-fetch endpoint; `{url}` is replaced with the encoded target URL. |
| `JINA_API_TOKEN` | empty | Optional bearer token for the fetch endpoint. |
| `SEARXNG_URL` | `http://127.0.0.1:8888` | SearXNG base URL used by `Search`. |
| `REQUEST_TIMEOUT` | `10` | Per-request timeout in seconds. |
| `MAX_URLS` | `3` | Maximum URLs accepted by one `Fetch` call. |
| `DELAY_BETWEEN_REQUESTS` | `0.5` | Delay between sequential fetches, in seconds. |
| `MAX_SEARCH_RESULTS` | `10` | Upper bound for results returned by `Search`. |

## CalDAV server configuration

`dotmcp-caldav` is a general connector for CalDAV servers. It reads connection data from the MCP server process environment when each tool runs, creates a client for that call, and closes it afterward. Credentials are never exposed as tool parameters or persisted by the server.

| Variable | Purpose |
| --- | --- |
| `CALDAV_URL` | CalDAV server URL. |
| `CALDAV_USERNAME` | CalDAV username. |
| `CALDAV_PASSWORD` | CalDAV password or app password. |

The repository [`mcp.json`](./mcp.json) defines these variables on the `dotmcp-caldav` process with the requested template values: `{{CALDAV_URL}}`, `{{CALDAV_USERNAME}}`, and `{{CALDAV_PASSWORD}}`. Render or replace these templates using your MCP harness's supported secret-substitution mechanism before loading the configuration. The server rejects missing variables and unresolved or invalid URLs before creating a network client.

Start with `CheckCalDAVConnection`, use `ListCalendars` to obtain a calendar URL, then search, retrieve, create, update, or delete VEVENT, VTODO, and VJOURNAL objects. Calendar and object URLs must use the same origin as `CALDAV_URL`; use the server's canonical endpoint because redirects are rejected.

HTTPS CalDAV endpoints are required by default. TLS certificate verification is enabled by default through `verifySsl`; disable it only for a known self-signed server. For a trusted legacy or local HTTP endpoint, explicitly set `allowInsecureHttp` to `true` on every relevant tool call. The optional non-secret server environment variables control limits:

| Variable | Default | Purpose |
| --- | --- | --- |
| `CALDAV_REQUEST_TIMEOUT` | `30` | Per-request timeout in seconds. |
| `CALDAV_MAX_RESULTS` | `100` | Maximum results returned by `SearchCalendarObjects`. |
| `CALDAV_MAX_SEARCH_DAYS` | `366` | Maximum closed date range accepted by `SearchCalendarObjects`. |
| `CALDAV_SEARCH_WINDOW_DAYS` | `7` | Largest date window fetched per search request; searching stops once `maxResults` is reached. |

## Notes

- The Terminal server executes commands with the privileges of the MCP harness process. Only enable it for trusted MCP clients and repositories.
- The Assembly server’s Docker usage is documented in [`DotMcp.Assembly/README.md`](./DotMcp.Assembly/README.md).
- `mcp.json` invokes `.venv/bin/python` for all four Python servers, including CalDAV. Windows users can create a Windows-specific config using `.venv\\Scripts\\python.exe`.
