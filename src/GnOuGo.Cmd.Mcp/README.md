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
- no raw free shell execution

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
| `cmd_list_allowed_commands`  | Lists allowed command aliases with description, shell, working directory, and parameters. |
| `cmd_get_policy`             | Returns the active policy: shells, roots, timeouts, limits, **and** OS/architecture/available shells. |
| `cmd_run`                    | Executes an allowlisted alias. Returns structured result with stdout, stderr, exit code, and error details. |

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
    "DefaultTimeoutMs": 10000,
    "MaxTimeoutMs": 30000,
    "MaxOutputCharacters": 12000,
    "AllowedShells": ["powershell", "sh"],
    "AllowedWorkingRoots": ["C:/github/GnOuGo.Agent"],
    "AllowedCommands": {
      "git_status": {
        "Shell": "powershell",
        "Script": "git --no-pager status --short",
        "WorkingDirectory": "C:/github/GnOuGo.Agent"
      }
    }
  }
}
```

## Secure Parameters

An alias can contain `{{name}}` placeholders.
Values are:

- validated by regex
- bounded in length
- escaped according to the target shell

Example:

```json
{
  "list_relative_path": {
    "Shell": "powershell",
    "Script": "Get-ChildItem -Name {{path}}",
    "WorkingDirectory": "C:/github/GnOuGo.Agent",
    "Parameters": {
      "path": {
        "Required": true,
        "Pattern": "^[A-Za-z0-9_./\\-]{1,120}$",
        "MaxLength": 120
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
