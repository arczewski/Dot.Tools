"""FastMCP web fetch and SearXNG search tools."""

from __future__ import annotations

import asyncio
import os
from urllib.parse import quote

import httpx
from fastmcp import FastMCP

mcp = FastMCP("DotMcp.Fetch")


def _int_env(name: str, default: int) -> int:
    try:
        return int(os.getenv(name, str(default)))
    except ValueError:
        return default


def _float_env(name: str, default: float) -> float:
    try:
        return float(os.getenv(name, str(default)))
    except ValueError:
        return default


JINA_API_URL = os.getenv("JINA_API_URL", "https://r.jina.ai/{url}")
JINA_API_TOKEN = os.getenv("JINA_API_TOKEN", "")
SEARXNG_URL = os.getenv("SEARXNG_URL", "http://127.0.0.1:8888")
REQUEST_TIMEOUT = _int_env("REQUEST_TIMEOUT", 10)
MAX_URLS = _int_env("MAX_URLS", 3)
DELAY_BETWEEN_REQUESTS = _float_env("DELAY_BETWEEN_REQUESTS", 0.5)
MAX_SEARCH_RESULTS = _int_env("MAX_SEARCH_RESULTS", 10)


async def _fetch_single_url(client: httpx.AsyncClient, url: str) -> tuple[str, str, bool]:
    jina_url = JINA_API_URL.replace("{url}", quote(url, safe=""))
    headers = {
        "X-Return-Format": "markdown",
        "X-Timeout": str(REQUEST_TIMEOUT),
    }
    if JINA_API_TOKEN:
        headers["Authorization"] = f"Bearer {JINA_API_TOKEN}"

    try:
        response = await client.get(jina_url, headers=headers)
        response.raise_for_status()
        return response.text, url, False
    except Exception as error:  # pragma: no cover - network dependent
        return f"Error fetching {url}: {error}", url, True


@mcp.tool(name="Fetch")
async def fetch(urls: list[str] | None = None) -> str:
    """Fetch up to MAX_URLS URLs and return each response as markdown using the configured Jina endpoint."""
    if not urls:
        return "No URLs provided."
    if len(urls) > MAX_URLS:
        return f"Too many URLs: {len(urls)}. Maximum allowed is {MAX_URLS}."

    results: list[tuple[str, str, bool]] = []
    timeout = httpx.Timeout(REQUEST_TIMEOUT + 5)
    async with httpx.AsyncClient(timeout=timeout, follow_redirects=True) as client:
        for index, url in enumerate(urls):
            if index:
                await asyncio.sleep(DELAY_BETWEEN_REQUESTS)
            results.append(await _fetch_single_url(client, url))

    output: list[str] = []
    for content, source, is_error in results:
        output.append(f"## URL: {source}")
        output.append(f"*Error: {content}*" if is_error else content)
        output.append("\n---\n")
    return "\n".join(output).rstrip()


@mcp.tool(name="Search")
async def search(
    query: str,
    maxResults: int = 5,
    snippetMaxLength: int = 150,
    engines: list[str] | None = None,
) -> str:
    """Search the configured SearXNG endpoint and return compact title, URL, and snippet results."""
    limit = min(maxResults, MAX_SEARCH_RESULTS)
    parameters: dict[str, str | int] = {
        "q": query,
        "format": "json",
        "num_results": limit,
    }
    if engines:
        parameters["engines"] = ",".join(engines)

    try:
        timeout = httpx.Timeout(REQUEST_TIMEOUT + 5)
        async with httpx.AsyncClient(timeout=timeout, follow_redirects=True) as client:
            response = await client.get(f"{SEARXNG_URL.rstrip('/')}/search", params=parameters)
            response.raise_for_status()
            results = response.json().get("results", [])

        if not results:
            return f"No results found for: {query}"

        output: list[str] = []
        for index, result in enumerate(results[:limit], 1):
            title = result.get("title") or "No title"
            url = result.get("url") or ""
            snippet = result.get("content") or ""
            if len(snippet) > snippetMaxLength:
                snippet = f"{snippet[:snippetMaxLength]}..."
            output.append(f"{index}. {title}")
            output.append(f"   {url}")
            if snippet.strip():
                output.append(f"   {snippet}")
        return "\n".join(output)
    except Exception as error:  # pragma: no cover - network dependent
        return f"Search error: {error}"


@mcp.tool(name="GetFetchInfo")
def get_fetch_info() -> str:
    """Return the effective fetch-server limits and non-secret endpoint configuration."""
    return (
        f"Max URLs: {MAX_URLS}\n"
        f"Delay between requests: {DELAY_BETWEEN_REQUESTS * 1000} ms\n"
        f"SearXNG URL: {SEARXNG_URL}\n"
        f"Jina token configured: {bool(JINA_API_TOKEN)}\n"
        f"Max search results: {MAX_SEARCH_RESULTS}"
    )


if __name__ == "__main__":
    mcp.run()
