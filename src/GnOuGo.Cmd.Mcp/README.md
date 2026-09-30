# GnOuGo.Cmd.Mcp

`GnOuGo.Cmd.Mcp` is a **stdio** MCP server for everyday workspace file and directory operations **via a strict allowlist**.

## MCP protocol compatibility

This stdio server uses the C# MCP SDK pinned in its project file with automatic protocol negotiation: clients prefer `2026-07-28` discovery and can initialize with `2025-11-25`. Launch the built apphost, or use `dotnet GnOuGo.Cmd.Mcp.dll`; do not put `dotnet run` on an MCP stdio transport because CLI output can corrupt the JSONL stream. MCP tool names, request parameter names, and structured result formats are unchanged; discovery reflects the effective command catalog.

## Objective

This server does **not** accept arbitrary command lines.
It only executes **preconfigured command aliases** defined in `appsettings.json`.

This is intentionally a **deny-by-default** design to limit risks:

- explicitly allowed shells (`powershell`, `sh`, `cmd`)
- working directories bounded to allowed roots
- bounded timeout
- bounded max stdout/stderr size
- inherited environment transmitted via allowlist
- optional named parameters validated by regex
- optional path parameters normalized inside the workspace and rejected on traversal attempts
- no raw free shell execution

By default, the server now resolves its writable workspace to `Desktop/GnOuGo` for the current user and creates that directory automatically on startup if it does not already exist.

The `.GnOuGo/` subtree is reserved for GnOuGo-managed databases and internal temporary state. Working directories and all bundled path-bearing parameters reject that subtree before shell execution. Workflow-owned files belong below visible paths, with materialized workflow workspaces conventionally placed under `workflows/<purpose-specific-name>`. Bundled recursive search and listing aliases omit `.GnOuGo/`.

## Cross-Platform Support

The server is designed to work on **Windows**, **Linux**, and **macOS** out of the box.

| Shell        | Windows              | Linux / macOS             |
|--------------|----------------------|---------------------------|
| `powershell` | `powershell.exe`, `pwsh.exe` | `pwsh`, `powershell` |
| `sh`         | _(not available)_    | `/bin/sh`                 |
| `cmd`        | `cmd.exe`            | _(not available)_         |

Shell availability is auto-detected at runtime. The `cmd_get_policy` tool reports which shells are actually available on the current host.

Windows PowerShell needs an initialized module search path in the filtered child
environment. Unless `PSModulePath` is explicitly allowlisted and present, Cmd sets
and reapplies it to the selected shell installation's `Modules` directory before
command loading, preventing startup expansion from adding unwanted module searches. It does not inherit
user module paths. Commands still receive private stdin closed immediately to EOF;
timeouts, output capture and process-tree termination remain enforced. See the
[native Windows diagnosis](../../docs/windows-cmd-startup.md).

## Packaged filesystem commands

The default catalog contains exactly 18 commands:

| Purpose | Commands |
|---|---|
| Workspace navigation | `print_working_directory`, `list_root`, `list_relative_path`, `tree` |
| File inspection | `cat_file`, `head_file`, `tail_file`, `wc`, `find_files`, `grep_recursive` |
| Creation and deletion | `create_directory`, `delete_directory`, `write_file`, `delete_file` |
| Copy and move | `copy_file`, `copy_directory`, `move_file`, `move_directory` |

`write_file` accepts UTF-8 base64 content, including Markdown. It retains its existing overwrite behavior. `delete_file` retains its existing behavior: a missing file is an error.

`delete_directory` is idempotent by default: an absent path succeeds without creating anything, an existing directory is removed recursively, and an existing file is rejected. Success reports `absent: <path>`. Permission and deletion failures remain errors. An explicit `MustExist: true` configuration still rejects absent targets before dispatch.

Copy and move take `source` and `destination` string parameters. The source must exist with the declared type. The destination is the exact new path; it must not exist and its parent must already exist. There is no overwrite option, directory merging or implicit move into an existing directory. Move also performs rename. Directory copies include nested files, empty directories and hidden entries. A destination inside its source is rejected. Filesystem links and reparse points within operand paths or source directory trees are unsupported; they are rejected rather than traversed. Move across filesystems may fail, depending on the host operation.

Failures are reported without silently discarding the source. Interrupted or failed copies can leave a partial destination; inspect it before explicitly removing it or choosing a new destination. Existing destination data is never intentionally overwritten. These commands do not isolate the workspace from simultaneous changes by unrelated external processes.

Example rename:

```json
{
  "commandName": "move_file",
  "parameters": {"source": "notes/draft.md", "destination": "notes/final.md"}
}
```

The packaged catalog no longer includes `dotnet_build`, `dotnet_restore`, `dotnet_test`, `pnpm_install`, `pnpm_run`, `os_info`, `which`, `env`, `rm` or `write_markdown_file`. There are no compatibility aliases. Custom allowlists remain supported; this is not a blacklist of command names. Development-tool execution belongs to an explicitly configured integration. Real Copilot command edit/test execution remains unverified under the available sandbox policy; removing development aliases does not resolve that limitation.

After deployment, restart the bundled MCP process and discover its current metadata. Existing workflows using removed aliases must be revised/regenerated and explicitly approved; use `delete_directory`/`delete_file` and `write_file` where appropriate. Saved workflows, approvals, execution journals and explicit configurations are not rewritten automatically.

## Exposed MCP Tools

| Tool                         | Description |
|------------------------------|-------------|
| `cmd_list_allowed_commands`  | Returns the allowlist as structured data for clients, diagnostics, and workflows that need to inspect aliases programmatically. |
| `cmd_get_policy`             | Returns the active policy: shells, roots, timeouts, limits, **and** OS/architecture/available shells. |
| `cmd_run`                    | Executes an allowlisted alias. Its advertised tool description includes the configured command aliases and accepted parameter names so planners can choose valid `commandName` values without a separate discovery step. |

## Tool Discovery

`cmd_run` is the primary execution tool. During MCP `tools/list`, its description is enriched from the active `AllowedCommands` configuration with:

- allowed `commandName` aliases
- each alias description
- exact `parameters` object fields (string values only)
- required/optional markers and workspace-path hints

The advertised input schema also contains one `oneOf` selector branch per alias. Each
branch fixes `commandName` with `const` and carries the configured, provider-neutral
operation description plus its parameter contract. Consumers can therefore retain the
exact semantic contract for a selected alias without inspecting its name or executable
script.

Each parameter object is closed: undeclared fields, Boolean values and implicit argument aliases are rejected. Required strings must be nonblank and obey declared patterns and bounds. Optional values retain their existing omission behavior.

**Breaking change:** replace encoded `parametersJson` strings with a structured `parameters` object. There is no compatibility alias or implicit `args` mapping; explicitly configured parameter names remain supported. Refresh discovery, revise or regenerate existing workflows and approve them again. Scripts, permissions and deletion semantics are unchanged.

The packaged allowlist includes both read aliases and guarded workspace write aliases such as directory creation, file writing, copy/move, and recursive deletion. These capabilities are discoverable by default, but `cmd_run` still accepts only named aliases and validates every workspace path and parameter before execution. Discovery descriptions and command selectors are generated from the effective configuration; no planner changes or additional discovery calls are required.

`cmd_get_policy` and `cmd_list_allowed_commands` are also enriched during MCP `tools/list`.
Their descriptions include the returned JSON shape plus the current policy roots, limits, shell availability, and allowlisted command aliases.
This gives `workflow.plan` enough context to generate frozen `mcp.call` requests without first adding discovery calls to the generated workflow.

`cmd_list_allowed_commands` is still kept as a structured introspection endpoint. Use it when a client or workflow needs machine-readable command metadata at runtime rather than prose embedded in the discovery metadata.

## Structured Error Handling

`cmd_run` **never throws to the MCP client**. All outcomes are returned as a `CmdRunResult` with:

| Field          | Type     | Description |
|----------------|----------|-------------|
| `success`      | `bool`   | `true` only if exit code is 0 and no timeout. |
| `exitCode`     | `int`    | Process exit code, or `-1` on error/timeout. |
| `timedOut`     | `bool`   | `true` if the process was killed after the timeout. |
| `ok`           | `bool`   | Mirrors `success` for the shared GnOuGo MCP error contract. |
| `error_code`   | `string?`| Machine-readable error category (see below). |
| `error_message`| `string?`| Human-readable explanation. |

Error codes:

| Code                   | Cause |
|------------------------|-------|
| `INVALID_INPUT`        | Unknown command, bad parameters, shell not allowed, directory outside roots. |
| `PROCESS_SETUP_FAILED` | Could not configure the process (e.g., missing environment). |
| `PROCESS_START_FAILED` | Shell executable not found or access denied. |
| `TIMEOUT`              | Command exceeded the allowed timeout and was killed. |
| `NON_ZERO_EXIT`        | Process exited with a non-zero exit code. |
| `CANCELLED`            | The MCP client cancelled the request. |
| `INTERNAL_ERROR`       | Unexpected server-side exception. |

## Configuration

The server loads its config from `appsettings.json`, section `Cmd`.

Example:

```json
{
  "Cmd": {
    "DefaultWorkingDirectory": "GnOuGo",
    "DefaultTimeoutMs": 10000,
    "MaxTimeoutMs": 30000,
    "MaxOutputCharacters": 12000,
    "AllowedShells": ["powershell", "sh"],
    "AllowedWorkingRoots": [],
    "AllowedCommands": {
      "print_working_directory": {
        "Shell": "powershell",
        "Script": "Get-Location | Select-Object -ExpandProperty Path"
      }
    }
  }
}
```

`DefaultWorkingDirectory` behaves as follows:

- if it is a **relative path** such as `GnOuGo` or `GnOuGo/notes`, it is resolved under the current user's Desktop
- if it is an **absolute path**, that absolute path is used instead
- the resolved directory is automatically created at startup
- the resolved directory is automatically included in the server's allowed working roots

With the default value `GnOuGo`, the writable workspace typically resolves to:

- **Windows**: `C:/Users/<user>/Desktop/GnOuGo`
- **macOS**: `/Users/<user>/Desktop/GnOuGo`
- **Linux**: `/home/<user>/Desktop/GnOuGo`

These are the usual default paths. Internally, the server first asks the OS for the current user's Desktop directory and then falls back to `UserProfile/Desktop` or `HOME/Desktop` if needed.

This means that, out of the box, commands without an explicit `WorkingDirectory` run inside a writable user-owned workspace instead of the repository directory.

## Secure Parameters

An alias can contain `{{name}}` placeholders.
Values are:

- validated by regex
- bounded in length
- escaped according to the target shell
- optionally resolved to an absolute path inside the allowed workspace roots

For path-bearing parameters, enable the built-in workspace guardrails on the parameter itself:

- `IsWorkspacePath`: treat the value as a workspace-relative or fully-qualified absolute path restricted to allowed roots
- `AllowAbsolutePath`: retained for older configs; safe absolute paths no longer require this opt-in
- `MustExist`: require the referenced file or directory to already exist
- `PathKind`: `Any`, `File`, or `Directory`

When `IsWorkspacePath` is enabled, the server additionally rejects:

- `..` parent traversal segments
- drive-relative paths such as `C:temp\\note.md`
- `~` home-directory shortcuts
- wildcard characters such as `*` and `?`
- any resolved path outside the configured allowed workspace roots
- `.GnOuGo` or any descendant of that reserved internal subtree
- symbolic links or reparse points below an allowed workspace root, including links to another location inside the workspace

Example:

```json
{
  "list_relative_path": {
    "Shell": "powershell",
    "Script": "Get-ChildItem -Name {{path}}",
    "Parameters": {
      "path": {
        "Required": true,
        "Pattern": "^(?![\\/])(?!.*(?:^|[\\/])\\.\\.(?:[\\/]|$))[A-Za-z0-9_.\\/-]{1,240}$",
        "MaxLength": 240,
        "IsWorkspacePath": true,
        "MustExist": true
      }
    }
  }
}
```

Then on the MCP side:

```json
{
  "commandName": "list_relative_path",
  "parameters": {"path": "src/GnOuGo.Cmd.Mcp"}
}
```

## Start the Server

```powershell
dotnet build .\src\GnOuGo.Cmd.Mcp\GnOuGo.Cmd.Mcp.csproj

.\src\GnOuGo.Cmd.Mcp\bin\Debug\net10.0\GnOuGo.Cmd.Mcp.exe
```

## Native AOT Publish (win-x64)

```powershell
dotnet publish .\src\GnOuGo.Cmd.Mcp\GnOuGo.Cmd.Mcp.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=true -p:PublishTrimmed=true
```

## Writable Workspace Examples

The sample `appsettings.json` includes allowlisted commands for writable operations inside the default workspace:

- `create_directory`
- `write_file`

Example payload for `write_file`:

```json
{
  "commandName": "write_file",
  "parameters": {"path": "notes/today.md", "contentBase64": "IyBUb2RheQoKLSBFeGFtcGxlIG5vdGUK"}
}
```

The sample above writes the following Markdown content after decoding the UTF-8 base64 payload:

```markdown
# Today

- Example note
```

## CLI Example

A GnOuGo.Flow example is provided in:

- `src/GnOuGo.Flow.Cli/examples/mcp-cmd-demo.yaml`

Run it:

```powershell
dotnet run --project src/GnOuGo.Flow.Cli/GnOuGo.Flow.Cli.csproj -- run src/GnOuGo.Flow.Cli/examples/mcp-cmd-demo.yaml
```

## Security Notes

- If an allowed shell is not available (`sh` on Windows, `cmd` on Linux), the alias fails gracefully with an `INVALID_INPUT` error
- The `cmd_run` tool never executes a raw command provided by the caller
- All errors are returned as structured results — the MCP client always gets a valid JSON response
- To allow a new command, it must be explicitly added in `src/GnOuGo.Cmd.Mcp/appsettings.json`
- Workspace path parameters can be hardened centrally with `IsWorkspacePath` so values such as `../secret.txt` are rejected before the process starts

- The traversal guardrails apply to the effective working directory and to user-supplied parameters marked with `IsWorkspacePath`; allowlisted scripts themselves should still avoid hardcoded paths outside the intended workspace

Command subprocesses receive declared parameters and a closed standard-input stream. They cannot read the MCP transport or prompt through the host console. Timeouts and cancellation still apply.

## Validation

```bash
dotnet test tests/GnOuGo.Cmd.Mcp.Tests -m:1 -warnaserror
dotnet publish src/GnOuGo.Cmd.Mcp -c Release -r osx-arm64 --self-contained true -warnaserror -o artifacts/cmd-aot
GNOU_GO_CMD_MCP_TEST_EXECUTABLE="$PWD/artifacts/cmd-aot/GnOuGo.Cmd.Mcp" dotnet test tests/GnOuGo.Cmd.Mcp.Tests --no-build --filter FullyQualifiedName~McpProtocolNegotiationTests
```

Use the corresponding RID and executable suffix on other platforms. The executable override is test-only. Tests execute packaged commands in isolated temporary workspaces and verify real filesystem results. Representative Linux, Windows and macOS desktop CI targets run the complete Cmd suite and protocol checks against their published Native AOT executable. No model call or external repository operation is required.
