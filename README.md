# Codex NodeREPL Proxy

Windows wrapper for Codex Browser/Computer Use `node_repl.exe`.

## Why

The Browser Use child runtime may not inherit the desktop proxy environment. Network initialization then fails with `nodeRepl.fetch request failed` or `transport closed`.

This wrapper resolves the current Codex runtime from `NODE_REPL_NODE_PATH`, injects the local proxy into the real child process, forwards MCP stdin/stdout/stderr byte-for-byte, preserves the exit code, and cleans up the child with a Windows Job Object.

## Install

1. Start Clash or another local HTTP proxy. Edit `proxy-url.txt` if the local port is not `7897`.
2. Copy this directory to `%USERPROFILE%\.codex\runtime-proxy`.
3. If needed, rebuild the executable with the installed .NET Framework compiler:

```powershell
$dir = Join-Path $env:USERPROFILE '.codex\runtime-proxy'
$csc = "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /platform:x64 /target:exe `
  "/out:$dir\node_repl-proxy.exe" "$dir\RuntimeProxy.cs"
```

4. Point Codex's NodeREPL override at the wrapper:

```powershell
$dir = Join-Path $env:USERPROFILE '.codex\runtime-proxy'
[Environment]::SetEnvironmentVariable(
  'CODEX_NODE_REPL_PATH',
  (Join-Path $dir 'node_repl-proxy.exe'),
  'User'
)
```

5. If `%USERPROFILE%\.codex\config.toml` has an explicit `mcp_servers.node_repl` entry, set only its command to the wrapper and preserve the generated runtime environment:

```toml
[mcp_servers.node_repl]
args = []
command = "C:/Users/<user>/.codex/runtime-proxy/node_repl-proxy.exe"
startup_timeout_sec = 120
```

Fully quit and restart Codex after changing the user environment. Do not copy machine-specific `NODE_REPL_NODE_PATH`, module directories, native-pipe paths, auth, or the whole `config.toml` from another computer.

## Environment injected into the child

```text
HTTP_PROXY=http://127.0.0.1:7897
HTTPS_PROXY=http://127.0.0.1:7897
ALL_PROXY=http://127.0.0.1:7897
NO_PROXY=localhost,127.0.0.1,::1
NODE_USE_ENV_PROXY=1
BROWSER_USE_DISABLE_AMBIENT_NETWORK=0
```

The proxy file must contain an HTTP(S) proxy origin without credentials, query, or fragment.

## Verify

```powershell
python .\test_runtime_proxy.py
codex doctor
```

An HTTP `403` or `405` from `https://ab.chatgpt.com/` is sufficient for the proxy connectivity check; it is not an authentication test.

## Roll back

Fully quit Codex, then remove the user override:

```powershell
[Environment]::SetEnvironmentVariable('CODEX_NODE_REPL_PATH', $null, 'User')
```

Restore the original `mcp_servers.node_repl.command` if it was edited, then restart Codex.
