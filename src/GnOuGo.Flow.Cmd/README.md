# GnOuGo.Flow.Cmd

`GnOuGo.Flow.Cmd` is a **stdio** MCP server for executing commands **via a strict allowlist**.

## Objective

This server does **not** accept arbitrary command lines.
It only executes **preconfigured command aliases** defined in `appsettings.json`.

This is intentionally a **deny-by-default** design to limit risks:

- explicitly allowed shells (`powershell`, `sh`)
- working directories bounded to allowed roots
- bounded timeout
- bounded max stdout/stderr size
- environment transmitted via allowlist
- optional named parameters validated by regex
- no raw free shell execution

## Exposed MCP Tools

- `cmd_list_allowed_commands`: lists allowed aliases
- `cmd_get_policy`: returns the active policy (shells, roots, timeouts, sizes)
- `cmd_run`: executes an allowlisted alias

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
  "parametersJson": "{\"path\":\"src/GnOuGo.Flow.Cmd\"}"
}
```

## Start the Server

```powershell
dotnet build .\src\GnOuGo.Flow.Cmd\GnOuGo.Flow.Cmd.csproj

dotnet run --project .\src\GnOuGo.Flow.Cmd\GnOuGo.Flow.Cmd.csproj
```

## CLI Example

A GnOuGo.Flow example is provided in:

- `src/GnOuGo.Flow.Cli/examples/mcp-cmd-demo.yaml`

Run it:

```powershell
dotnet run --project src/GnOuGo.Flow.Cli/GnOuGo.Flow.Cli.csproj -- run src/GnOuGo.Flow.Cli/examples/mcp-cmd-demo.yaml
```

## Security Notes

- If an allowed shell is not available (`sh` on Windows without a Unix environment), the alias fails gracefully
- The `cmd_run` tool never executes a raw command provided by the caller
- To allow a new command, it must be explicitly added in `src/GnOuGo.Flow.Cmd/appsettings.json`
