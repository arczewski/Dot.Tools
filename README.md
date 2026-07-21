# Dot.Tools MCP servers

`Dot.Tools` provides four local stdio [Model Context Protocol (MCP)] servers and one header-authenticated CalDAV HTTP connector:

| Server | Purpose | Runtime |
| --- | --- | --- |
| `dotmcp-assembly` | Inspect .NET assemblies and provide Roslyn-based IntelliSense tools. | .NET 10 SDK |
| `dotmcp-io` | List, read, search, and edit files. | Python / FastMCP |
| `dotmcp-fetch` | Fetch web content and query a SearXNG instance. | Python / FastMCP |
| `dotmcp-terminal` | Execute commands through the host-native shell. | Python / FastMCP |
| `dotmcp-caldav` | Connect to arbitrary CalDAV servers using credentials supplied in MCP HTTP headers. | Python / FastMCP |

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

Load the repository-root [`mcp.json`](./mcp.json) in your MCP harness. It defines four local stdio servers plus a Streamable HTTP connection to `dotmcp-caldav` at `http://127.0.0.1:8000/mcp`.

The paths in `mcp.json` are relative to the repository root and apply to the four stdio entries. Configure the harness to start those entries from the cloned repository directory, or replace `cwd` and the relative paths with that clone’s absolute path if the harness does not resolve configuration-relative paths.

The Assembly entry uses `dotnet run`, so its first launch restores and builds the .NET project. The three local Python entries run through `.venv/bin/python`, which is created by `install.sh`. Launch the CalDAV HTTP server separately as described below.

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

`dotmcp-caldav` is a general connector for CalDAV servers. It reads connection data from these incoming MCP HTTP headers on every tool call, creates a client for that call, and closes it afterward. Credentials are never exposed as tool parameters, read from files, or persisted by the server:

| Header | Value |
| --- | --- |
| `X-CalDAV-URL` | CalDAV server URL. |
| `X-CalDAV-Username` | CalDAV username. |
| `X-CalDAV-Password` | CalDAV password or app password. |

The repository [`mcp.json`](./mcp.json) is a client configuration template: render or replace `{{CALDAV_URL}}`, `{{CALDAV_USERNAME}}`, and `{{CALDAV_PASSWORD}}` with your MCP harness's supported secret-substitution mechanism before loading it. If the URL placeholder remains unresolved, the connector rejects it before creating a network client. The template targets a local Streamable HTTP server at `http://127.0.0.1:8000/mcp`; launch it before connecting with `MCP_TRANSPORT=streamable-http .venv/bin/python DotMcp.Caldav/server.py`.

The default server binding is loopback-only. Do not expose this credential-bearing HTTP endpoint directly on a network. For a remote deployment, terminate HTTPS at a trusted reverse proxy, preserve the three `X-CalDAV-*` headers only on the protected hop, and configure `MCP_HOST`, `MCP_PORT`, and `MCP_PATH` to match that deployment.

Start with `CheckCalDAVConnection`, use `ListCalendars` to obtain a calendar URL, then search, retrieve, create, update, or delete VEVENT, VTODO, and VJOURNAL objects. Calendar and object URLs must use the same origin as the `X-CalDAV-URL` value; use the server's canonical endpoint because redirects are rejected.

HTTPS CalDAV endpoints are required by default. TLS certificate verification is enabled by default through `verifySsl`; disable it only for a known self-signed server. For a trusted legacy or local HTTP endpoint, explicitly set `allowInsecureHttp` to `true` on every relevant tool call. The optional server environment variables control non-secret limits:

| Variable | Default | Purpose |
| --- | --- | --- |
| `CALDAV_REQUEST_TIMEOUT` | `30` | Per-request timeout in seconds. |
| `CALDAV_MAX_RESULTS` | `100` | Maximum results returned by `SearchCalendarObjects`. |
| `CALDAV_MAX_SEARCH_DAYS` | `366` | Maximum closed date range accepted by `SearchCalendarObjects`. |
| `CALDAV_SEARCH_WINDOW_DAYS` | `7` | Largest date window fetched per search request; searching stops once `maxResults` is reached. |

## Notes

- The Terminal server executes commands with the privileges of the MCP harness process. Only enable it for trusted MCP clients and repositories.
- The Assembly server’s Docker usage is documented in [`DotMcp.Assembly/README.md`](./DotMcp.Assembly/README.md).
- `mcp.json` contains four local stdio servers that invoke `.venv/bin/python`; the CalDAV entry is an HTTP client configuration for `http://127.0.0.1:8000/mcp`. Windows users can create a Windows-specific config using `.venv\\Scripts\\python.exe` for the local Python entries.
