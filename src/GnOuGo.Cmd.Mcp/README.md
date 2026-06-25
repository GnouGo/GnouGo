# GnOuGo.Cmd.Mcp

`GnOuGo.Cmd.Mcp` is a **stdio** MCP server for executing commands **via a strict allowlist**.

## Objective

This server does **not** accept arbitrary command lines.
It only executes **preconfigured command aliases** defined in `appsettings.json`.

This is intentionally a **deny-by-default** design to limit risks:

- explicitly allowed shells (`powershell`, `sh`, `cmd`)
- working directories bounded to allowed roots
- bounded timeout
- bounded max stdout/stderr size
- environment transmitted via allowlist
- optional named parameters validated by regex
- optional path parameters normalized inside the workspace and rejected on traversal attempts
- no raw free shell execution

By default, the server now resolves its writable workspace to `Desktop/GnOuGo` for the current user and creates that directory automatically on startup if it does not already exist.

## Cross-Platform Support

The server is designed to work on **Windows**, **Linux**, and **macOS** out of the box.

| Shell        | Windows              | Linux / macOS             |
|--------------|----------------------|---------------------------|
| `powershell` | `powershell.exe`, `pwsh.exe` | `pwsh`, `powershell` |
| `sh`         | _(not available)_    | `/bin/sh`                 |
| `cmd`        | `cmd.exe`            | _(not available)_         |

Shell availability is auto-detected at runtime. The `cmd_get_environment` tool reports which shells are actually available on the current host.

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
- accepted `parametersJson` field names
- required/optional markers and workspace-path hints

This is intended to help planning workflows generate valid `cmd_run` calls directly.

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
| `errorCode`    | `string?`| Machine-readable error category (see below). |
| `errorMessage` | `string?`| Human-readable explanation. |

Error codes:

| Code                   | Cause |
|------------------------|-------|
| `POLICY_VIOLATION`     | Unknown command, bad parameters, shell not allowed, directory outside roots. |
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

- `IsWorkspacePath`: treat the value as a workspace-relative or explicitly allowed absolute path
- `AllowAbsolutePath`: allow fully-qualified absolute paths (still restricted to allowed roots)
- `MustExist`: require the referenced file or directory to already exist
- `PathKind`: `Any`, `File`, or `Directory`

When `IsWorkspacePath` is enabled, the server additionally rejects:

- `..` parent traversal segments
- drive-relative paths such as `C:temp\\note.md`
- `~` home-directory shortcuts
- wildcard characters such as `*` and `?`
- any resolved path outside the configured allowed workspace roots

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
  "parametersJson": "{\"path\":\"src/GnOuGo.Cmd.Mcp\"}"
}
```

## Start the Server

```powershell
dotnet build .\src\GnOuGo.Cmd.Mcp\GnOuGo.Cmd.Mcp.csproj

dotnet run --project .\src\GnOuGo.Cmd.Mcp\GnOuGo.Cmd.Mcp.csproj
```

## Native AOT Publish (win-x64)

```powershell
dotnet publish .\src\GnOuGo.Cmd.Mcp\GnOuGo.Cmd.Mcp.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=true -p:PublishTrimmed=true
```

## Writable Workspace Examples

The sample `appsettings.json` includes allowlisted commands for writable operations inside the default workspace:

- `create_directory`
- `write_markdown_file`

Example payload for `write_markdown_file`:

```json
{
  "commandName": "write_markdown_file",
  "parametersJson": "{\"path\":\"notes/today.md\",\"contentBase64\":\"IyBUb2RheQoKLSBFeGFtcGxlIG5vdGUK\"}"
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

- If an allowed shell is not available (`sh` on Windows, `cmd` on Linux), the alias fails gracefully with a `POLICY_VIOLATION` error
- The `cmd_run` tool never executes a raw command provided by the caller
- All errors are returned as structured results — the MCP client always gets a valid JSON response
- To allow a new command, it must be explicitly added in `src/GnOuGo.Cmd.Mcp/appsettings.json`
- Workspace path parameters can be hardened centrally with `IsWorkspacePath` so values such as `../secret.txt` are rejected before the process starts
- The traversal guardrails apply to the effective working directory and to user-supplied parameters marked with `IsWorkspacePath`; allowlisted scripts themselves should still avoid hardcoded paths outside the intended workspace
