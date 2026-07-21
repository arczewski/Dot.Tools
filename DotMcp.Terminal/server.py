"""FastMCP cross-platform terminal tools."""

from __future__ import annotations

import asyncio
import os
import shutil
import signal
import subprocess
import sys
from dataclasses import dataclass

from fastmcp import FastMCP

mcp = FastMCP("DotMcp.Terminal")


@dataclass(frozen=True)
class ShellInfo:
    display_name: str
    executable: str
    argument_flag: str
    platform: str


def detect_shell() -> ShellInfo:
    """Choose the same OS-native non-interactive shell as the original MCP server."""
    if sys.platform == "darwin":
        return ShellInfo("macOS zsh", "zsh", "-c", "macOS")
    if sys.platform.startswith("linux"):
        return ShellInfo("Linux bash", "bash", "-c", "Linux")

    executable = shutil.which("pwsh.exe") or shutil.which("pwsh")
    if executable:
        return ShellInfo("Windows PowerShell Core (pwsh)", "pwsh.exe", "-Command", "Windows")
    return ShellInfo("Windows PowerShell", "powershell.exe", "-Command", "Windows")


CURRENT_SHELL = detect_shell()


def _build_output(stdout: str, stderr: str, exit_code: int, max_lines: int) -> str:
    stdout_lines = stdout.split(os.linesep)
    stderr_lines = stderr.split(os.linesep)
    total = len(stdout_lines) + len(stderr_lines)
    truncated = max_lines >= 0 and total > max_lines

    if truncated:
        if len(stdout_lines) >= max_lines:
            stdout_lines = stdout_lines[:max_lines]
            stderr_lines = []
        else:
            stderr_lines = stderr_lines[: max_lines - len(stdout_lines)]

    output = os.linesep.join(stdout_lines) if stdout_lines else ""
    if stderr_lines:
        if output:
            output += os.linesep
        output += "[stderr]" + os.linesep + os.linesep.join(stderr_lines)
    if truncated:
        output += os.linesep + (
            f"[Output truncated to {max_lines} lines. Use OS tools or redirect to file for full output.]"
        )
    if exit_code != 0:
        output += os.linesep + f"[Exit code: {exit_code}]"
    return output.strip() or "(no output)"


async def _terminate_process_tree(process: asyncio.subprocess.Process) -> None:
    if CURRENT_SHELL.platform == "Windows":
        terminator = await asyncio.create_subprocess_exec(
            "taskkill", "/F", "/T", "/PID", str(process.pid), stdout=asyncio.subprocess.DEVNULL,
            stderr=asyncio.subprocess.DEVNULL,
        )
        await terminator.wait()
        return

    try:
        os.killpg(process.pid, signal.SIGKILL)
    except ProcessLookupError:
        pass


async def _run_command(
    command: str,
    working_directory: str | None,
    timeout_seconds: int,
    max_output_lines: int,
) -> str:
    if working_directory is not None and not os.path.isdir(working_directory):
        return f"Error: Working directory not found: {working_directory}"

    if CURRENT_SHELL.platform == "Windows":
        arguments = [
            CURRENT_SHELL.executable,
            "-NoProfile",
            "-NonInteractive",
            CURRENT_SHELL.argument_flag,
            command,
        ]
    else:
        arguments = [CURRENT_SHELL.executable, CURRENT_SHELL.argument_flag, command]

    try:
        process_options: dict[str, object] = {}
        if CURRENT_SHELL.platform == "Windows":
            process_options["creationflags"] = subprocess.CREATE_NEW_PROCESS_GROUP
        else:
            process_options["start_new_session"] = True

        process = await asyncio.create_subprocess_exec(
            *arguments,
            cwd=working_directory,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
            **process_options,
        )
        try:
            if timeout_seconds > 0:
                stdout, stderr = await asyncio.wait_for(process.communicate(), timeout_seconds)
            else:
                stdout, stderr = await process.communicate()
        except asyncio.TimeoutError:
            await _terminate_process_tree(process)
            await process.communicate()
            return f"Error: Command timed out after {timeout_seconds} second(s)."

        return _build_output(
            stdout.decode("utf-8", errors="replace"),
            stderr.decode("utf-8", errors="replace"),
            process.returncode or 0,
            max_output_lines,
        )
    except Exception as error:  # pragma: no cover - host shell/process failure
        return f"Error: {error}"


@mcp.tool(name="RunCommand")
async def run_command(
    command: str,
    workingDirectory: str | None = None,
    timeoutSeconds: int = 30,
    maxOutputLines: int = 500,
) -> str:
    """Execute one command through the OS-native shell and return bounded stdout/stderr output."""
    return await _run_command(command, workingDirectory, timeoutSeconds, maxOutputLines)


@mcp.tool(name="RunCommands")
async def run_commands(
    commands: list[str],
    workingDirectory: str | None = None,
    timeoutSecondsEach: int = 30,
    continueOnError: bool = False,
    maxOutputLines: int = 500,
) -> str:
    """Run commands sequentially, stopping after the first error unless continueOnError is enabled."""
    output: list[str] = []
    for index, command in enumerate(commands, 1):
        output.append(f"── Command {index}: {command}")
        result = await _run_command(command, workingDirectory, timeoutSecondsEach, maxOutputLines)
        output.append(result)
        output.append("")
        if not continueOnError and result.startswith("Error:"):
            break
    return os.linesep.join(output).rstrip()


@mcp.tool(name="GetShellInfo")
def get_shell_info() -> str:
    """Return the detected platform, shell executable, and command argument flag."""
    return (
        f"Platform  : {CURRENT_SHELL.platform}\n"
        f"Shell     : {CURRENT_SHELL.display_name}\n"
        f"Executable: {CURRENT_SHELL.executable}\n"
        f"Flag      : {CURRENT_SHELL.argument_flag}"
    )


if __name__ == "__main__":
    mcp.run()
