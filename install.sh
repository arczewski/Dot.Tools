#!/usr/bin/env bash
# Bootstrap the Python FastMCP servers for Linux and macOS.
set -euo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
venv_path="${repository_root}/.venv"

python_candidates=()
if [[ -n "${PYTHON_BIN:-}" ]]; then
  python_candidates+=("${PYTHON_BIN}")
fi
python_candidates+=(python3.13 python3.12 python3.11 python3.10 python3)

python_bin=""
for candidate in "${python_candidates[@]}"; do
  if command -v "${candidate}" >/dev/null 2>&1 \
    && "${candidate}" -c 'import sys; raise SystemExit(sys.version_info < (3, 10))'; then
    python_bin="${candidate}"
    break
  fi
done

if [[ -z "${python_bin}" ]]; then
  echo "Error: Python 3.10 or newer is required. Set PYTHON_BIN to a suitable interpreter if needed." >&2
  exit 1
fi

"${python_bin}" -m venv "${venv_path}"
venv_python="${venv_path}/bin/python"

"${venv_python}" -m pip install --upgrade pip
for requirements_file in \
  "${repository_root}/DotMcp.IO/requirements.txt" \
  "${repository_root}/DotMcp.Fetch/requirements.txt" \
  "${repository_root}/DotMcp.Terminal/requirements.txt" \
  "${repository_root}/DotMcp.Caldav/requirements.txt"; do
  "${venv_python}" -m pip install --requirement "${requirements_file}"
done

echo "Installed FastMCP server dependencies into ${venv_path}."
echo "Load ${repository_root}/mcp.json in an MCP harness from this repository root."
