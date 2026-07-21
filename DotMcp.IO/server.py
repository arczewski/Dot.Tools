"""FastMCP filesystem discovery, search, and editing tools."""

from __future__ import annotations

import fnmatch
import os
import re
from pathlib import Path
from typing import Annotated

from fastmcp import FastMCP
from pydantic import BaseModel, Field

mcp = FastMCP("DotMcp.IO")

_DEFAULT_EXCLUDED_FOLDERS = {
    "obj",
    "bin",
    "node_modules",
    ".git",
    ".vs",
    ".idea",
    "dist",
    ".next",
}


class LineEdit(BaseModel):
    """A one-based, inclusive line edit used by BulkReplaceAndInsertLines."""

    startIndex: int = Field(description="One-based first line of the edit.")
    endIndex: int = Field(description="One-based inclusive last line of the edit.")
    lines: list[str] = Field(description="Replacement lines; use an empty array to delete.")


def _directory(path: str) -> Path:
    return Path(path) if path else Path.cwd()


def _iter_child_directories(directory: Path) -> list[Path]:
    return [child for child in directory.iterdir() if child.is_dir()]


def _iter_child_files(directory: Path, pattern: str) -> list[Path]:
    return [
        child
        for child in directory.iterdir()
        if child.is_file() and fnmatch.fnmatchcase(child.name, pattern)
    ]


def _read_lines(path: Path) -> list[str]:
    return _read_text(path).splitlines()


def _read_text(path: Path) -> str:
    with path.open("r", encoding="utf-8", newline="") as file:
        return file.read()


def _write_text(path: Path, content: str) -> None:
    with path.open("w", encoding="utf-8", newline="") as file:
        file.write(content)


def _write_lines(path: Path, lines: list[str]) -> None:
    content = os.linesep.join(lines)
    if lines:
        content += os.linesep
    _write_text(path, content)


def _line_and_column(content: str, index: int) -> tuple[int, int]:
    line = content.count("\n", 0, index) + 1
    last_newline = content.rfind("\n", 0, index) if index > 0 else -1
    column = index + 1 if last_newline == -1 else index - last_newline
    return line, column


@mcp.tool(name="ListDirectories")
def list_directories(path: str, searchPattern: str = "*", maxDepth: int = 0) -> list[str]:
    """List subdirectories matching a wildcard pattern; negative depth means unlimited recursion."""
    root = _directory(path)

    if maxDepth < 0:
        result: list[str] = []
        for current, directories, _ in os.walk(root, onerror=lambda _: None):
            result.extend(
                str(Path(current) / directory)
                for directory in directories
                if fnmatch.fnmatchcase(directory, searchPattern)
            )
        return result

    result: list[str] = []

    def visit(current: Path, depth: int) -> None:
        if depth > maxDepth:
            return
        children = _iter_child_directories(current)
        result.extend(
            str(child) for child in children if fnmatch.fnmatchcase(child.name, searchPattern)
        )
        if depth == maxDepth:
            return
        for child in children:
            visit(child, depth + 1)

    visit(root, 0)
    return result


@mcp.tool(name="ListFiles")
def list_files(
    path: str,
    searchPattern: str = "*",
    maxDepth: int = 0,
    excludeFolders: list[str] | None = None,
) -> list[str]:
    """List files matching a wildcard pattern while excluding common build folders by default."""
    root = _directory(path)
    excluded = set(excludeFolders) if excludeFolders is not None else _DEFAULT_EXCLUDED_FOLDERS
    excluded = {folder.casefold() for folder in excluded}

    if maxDepth == 0:
        return [str(file) for file in _iter_child_files(root, searchPattern)]

    result: list[str] = []

    def visit(current: Path, depth: int) -> None:
        if maxDepth >= 0 and depth > maxDepth:
            return
        try:
            result.extend(str(file) for file in _iter_child_files(current, searchPattern))
            children = _iter_child_directories(current)
        except OSError:
            return
        if maxDepth >= 0 and depth == maxDepth:
            return
        for child in children:
            if child.name.casefold() not in excluded:
                visit(child, depth + 1)

    visit(root, 0)
    return result


@mcp.tool(name="ReadAllFileLines")
def read_all_file_lines(path: str, lineNumbers: bool = False) -> str:
    """Read an entire file, optionally prefixing each line with its one-based number."""
    content = _read_text(Path(path))
    if not lineNumbers:
        return content
    return os.linesep.join(f"{index}: {line}" for index, line in enumerate(content.splitlines(), 1))


@mcp.tool(name="ReadLinesFromFile")
def read_lines_from_file(
    path: str,
    from_: Annotated[int, Field(alias="from")],
    count: int,
    lineNumbers: bool = False,
) -> str:
    """Read a zero-based range of lines from a file."""
    lines = _read_lines(Path(path))[from_ : from_ + count]
    if lineNumbers:
        return os.linesep.join(f"{index}: {line}" for index, line in enumerate(lines, from_ + 1))
    return os.linesep.join(lines)


@mcp.tool(name="SearchInFile")
def search_in_file(path: str, searchPattern: str) -> str:
    """Search one file with a regular expression and return one-based line and column positions."""
    file = Path(path)
    if not file.is_file():
        return "Error: File not found."

    try:
        content = _read_text(file)
        matches = list(re.finditer(searchPattern, content))
        if not matches:
            return "No matches found."
        return os.linesep.join(
            f"({line}, {column}) - {match.group(0)}"
            for match in matches
            for line, column in [_line_and_column(content, match.start())]
        )
    except re.error as error:
        return f"Error: Invalid regex pattern. {error}"
    except Exception as error:  # pragma: no cover - platform/filesystem failure
        return f"Error: {error}"


def _walk_files(
    root: Path,
    excluded: set[str],
    extensions: set[str] | None,
    max_depth: int,
):
    stack: list[tuple[Path, int]] = [(root, 0)]
    while stack:
        directory, depth = stack.pop()
        try:
            children = list(directory.iterdir())
        except OSError:
            continue
        for child in children:
            if child.is_file() and (extensions is None or child.suffix.casefold() in extensions):
                yield child
        if max_depth >= 0 and depth >= max_depth:
            continue
        stack.extend(
            (child, depth + 1)
            for child in children
            if child.is_dir() and child.name.casefold() not in excluded
        )


@mcp.tool(name="SearchInDirectory")
def search_in_directory(
    directoryPath: str,
    searchPattern: str,
    ignoreFolders: list[str] | None = None,
    includeExtensions: list[str] | None = None,
    maxDepth: int = -1,
) -> str:
    """Search a directory tree using a regular expression, with folder and extension filters."""
    root = _directory(directoryPath)
    if not root.is_dir():
        return f"Error: Directory not found: {directoryPath}"

    excluded = {
        folder.casefold()
        for folder in (ignoreFolders if ignoreFolders is not None else _DEFAULT_EXCLUDED_FOLDERS)
    }
    extensions = (
        {(extension if extension.startswith(".") else f".{extension}").casefold() for extension in includeExtensions}
        if includeExtensions is not None
        else None
    )

    try:
        expression = re.compile(searchPattern)
    except re.error as error:
        return f"Error: Invalid regex pattern. {error}"

    try:
        output: list[str] = []
        for file in _walk_files(root, excluded, extensions, maxDepth):
            try:
                content = _read_text(file)
            except (OSError, UnicodeDecodeError):
                continue
            matches = list(expression.finditer(content))
            if not matches:
                continue
            output.append(f"File: {file}")
            output.extend(
                f"  -> ({line}, {column})"
                for match in matches
                for line, column in [_line_and_column(content, match.start())]
            )
            output.append("")
        return os.linesep.join(output).rstrip() if output else "No matches found."
    except Exception as error:  # pragma: no cover - platform/filesystem failure
        return f"Error: {error}"


@mcp.tool(name="WriteFile")
def write_file(path: str, content: str, returnFullFileContentInResponse: bool = False) -> str:
    """Write complete file contents, overwriting an existing file when present."""
    try:
        file = Path(path)
        _write_text(file, content)
        response = f"Successfully wrote {len(content)} characters to {path}{os.linesep}"
        return response + (_read_text(file) if returnFullFileContentInResponse else "")
    except Exception as error:  # pragma: no cover - platform/filesystem failure
        return f"Error: {error}"


@mcp.tool(name="InsertLinesAt")
def insert_lines_at(
    path: str,
    beforeLine: int,
    lines: list[str],
    returnFullFileContentInResponse: bool = False,
) -> str:
    """Insert lines before a one-based line number, appending when the number is beyond the file."""
    try:
        file = Path(path)
        if not file.is_file():
            return "Error: File not found."
        file_lines = _read_lines(file)
        insert_at = max(0, min(beforeLine - 1, len(file_lines)))
        file_lines[insert_at:insert_at] = lines
        _write_lines(file, file_lines)
        response = f"Successfully inserted {len(lines)} line(s) before line {beforeLine} in {path}{os.linesep}"
        return response + (_read_text(file) if returnFullFileContentInResponse else "")
    except Exception as error:  # pragma: no cover - platform/filesystem failure
        return f"Error: {error}"


@mcp.tool(name="BulkReplaceAndInsertLines")
def bulk_replace_and_insert_lines(
    path: str,
    edits: list[LineEdit],
    returnFullFileContentInResponse: bool = False,
) -> str:
    """Apply one-based replacement, deletion, and insertion edits atomically from bottom to top."""
    try:
        file = Path(path)
        if not file.is_file():
            return "Error: File not found."
        file_lines = _read_lines(file)
        for edit in sorted(edits, key=lambda item: item.startIndex, reverse=True):
            start = edit.startIndex - 1
            end = edit.endIndex - 1
            if start < 0:
                continue
            if end < start:
                insert_at = min(start, len(file_lines))
                file_lines[insert_at:insert_at] = edit.lines
                continue
            if start >= len(file_lines):
                continue
            count = min(end - start + 1, len(file_lines) - start)
            file_lines[start : start + count] = edit.lines
        _write_lines(file, file_lines)
        response = f"Successfully applied {len(edits)} edits to {path}{os.linesep}"
        return response + (_read_text(file) if returnFullFileContentInResponse else "")
    except Exception as error:  # pragma: no cover - platform/filesystem failure
        return f"Error: {error}"


@mcp.tool(name="RemoveLines")
def remove_lines(path: str, lineIndexes: list[int], returnFullFileContentInResponse: bool = False) -> str:
    """Remove one-based line indexes; indexes beyond the end of the file are ignored."""
    try:
        file = Path(path)
        file_lines = _read_lines(file) if file.is_file() else []
        for line_index in sorted(lineIndexes, reverse=True):
            if 0 < line_index <= len(file_lines):
                file_lines.pop(line_index - 1)
        _write_lines(file, file_lines)
        response = f"Successfully removed {len(lineIndexes)} lines{os.linesep}"
        return response + (_read_text(file) if returnFullFileContentInResponse else "")
    except Exception as error:  # pragma: no cover - platform/filesystem failure
        return f"Error: {error}"


@mcp.tool(name="ReplaceText")
def replace_text(
    path: str,
    oldText: str,
    newText: str,
    returnFullFileContentInResponse: bool = False,
) -> str:
    """Replace one exact, unique text match in a file without relying on line numbers."""
    try:
        file = Path(path)
        if not file.is_file():
            return "Error: File not found."
        content = _read_text(file)
        index = content.find(oldText)
        if index == -1:
            return (
                "Error: oldText not found in file. Make sure it matches exactly including whitespace and line endings. "
                f"File length: {len(content)} chars. Searched for {len(oldText)} chars."
            )
        second_index = content.find(oldText, index + 1)
        if second_index != -1:
            return (
                f"Error: oldText appears multiple times in file (at least at positions {index} and {second_index}). "
                "Include more surrounding context to make the match unique."
            )
        result = content[:index] + newText + content[index + len(oldText) :]
        _write_text(file, result)
        line_number = content.count("\n", 0, index) + 1
        response = f"Successfully replaced text at line {line_number} in {path}{os.linesep}"
        return response + (_read_text(file) if returnFullFileContentInResponse else "")
    except Exception as error:  # pragma: no cover - platform/filesystem failure
        return f"Error: {error}"


if __name__ == "__main__":
    mcp.run()
