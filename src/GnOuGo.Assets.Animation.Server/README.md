# GnOuGo.Assets.Animation.Server

Standalone .NET 10 and React demonstration host for autonomous GnOuGo team
animations. The server validates a lightweight workflow preview, generates a
base SVG, and streams synthetic timer-driven events as NDJSON. It has no
database, persisted sessions, or dependency on any `GnOuGo.Flow` project.

The React client displays a Mermaid-inspired, top-down canvas with one vertical
swimlane per visible workflow invocation. Long-running LLM/MCP tasks use clean
isometric desks with open laptops, animated keyboards, and chairs. Useful
forks, joins, loops, decisions, and returns use isometric signposts; composites
without long work are collapsed. Wide curved roads use semi-transparent
asphalt, subtle wear, and deterministic roadside stones; walking actors follow
those exact paths. The full SVG has dynamic dimensions and is viewed through a
fixed-height, two-axis viewport with zoom, fit-width, centering, drag
navigation, and optional active-actor auto-follow.

Its frame-by-frame SVG motion engine makes actors visibly walk down the graph
with alternating arm and leg steps, breathing, independent ear motion, head
bobbing, blinking, and directional eyes. At desks they type with alternating
hands and fingers while keyboard keys and monitors react. Calls use synchronized
give/receive poses across workflow lanes; matrix branches split and merge;
waiting, delivery, celebration, and failure have distinct poses.

Idle actors and long-running poses share a deliberately slow ambient life
cycle: breathing, occasional blinks, small mouth changes, independent ear
twitches, and rare deterministic yawns. Purposeful actions remain dominant,
and reduced-motion preferences disable ambient cycling.

Demo work alternates deterministically between quick, steady, and deep-focus
laptop sessions. One evolving project parcel falls from the sky, follows the active GnOuGo,
collects colored completion stamps, crosses handoffs, and is finally sealed and
launched skyward from the delivery dock. Failure turns that same parcel red.

> Preview validation is not `GnOuGo.Flow.Core` validation. No workflow step,
> expression, LLM, MCP tool, process, or remote service is executed.

## Run

```bash
corepack pnpm install
dotnet run --project src/GnOuGo.Assets.Animation.Server
```

Open `http://localhost:5500`. A normal .NET build also compiles the Vite client
into `wwwroot`; set `-p:SkipClientBuild=true` only for backend-only iteration.

For frontend development with hot reload:

```bash
corepack pnpm --dir src/GnOuGo.Assets.Animation.Server/ClientApp dev
```

The Vite development server runs at `http://localhost:5501` and proxies API
requests to the .NET server on port 5500.

## API

- `GET /health` reports host health.
- `POST /api/simulations/validate` returns preview diagnostics, the resolved
  entrypoint, workflow summaries, and selectable synthetic failure targets.
- `POST /api/simulations/stream` validates before opening a
  `application/x-ndjson` response. Its first line is `simulation.prepared` and
  contains the full-canvas SVG plus its canvas width/height, lane count, and
  node count. Later lines contain flat timed events.

Example request:

```json
{
  "workflow": "version: 1\nentrypoint: main\nworkflows:\n  main:\n    steps:\n      - id: work\n        type: llm.call",
  "inputs": { "topic": "A friendly demo" },
  "seed": 42,
  "scene": "Office",
  "speed": 2,
  "failAt": { "workflowName": "main", "stepId": "work" }
}
```

Scenes are `Random`, `Office`, `Meadow`, and `Kitchen`. Supported speeds are
`0.5`, `1`, `2`, and `4`. Omitting the seed generates a new random seed.

## Preview YAML

Version 1 accepts a document name, entrypoint, workflows, and steps. It
understands `sequence`, `parallel`, `loop.sequential`, `loop.parallel`,
`switch`, defaults, and static local `workflow.call` references. The event
stream records every non-empty atomic step type, but the canvas intentionally
creates desks and character work only for `llm.*` and `mcp.*` tasks. Composite
signposts and static call handoffs are collapsed when their subtree contains no
visible LLM/MCP work. All task types remain preview-only and are never executed.

```yaml
version: 1
name: team-preview
entrypoint: main
workflows:
  main:
    steps:
      - id: split
        type: parallel
        branches:
          - name: ideas
            steps:
              - id: draft
                type: llm.call
          - name: tools
            steps:
              - id: inspect
                type: mcp.call
      - id: delegate
        type: workflow.call
        input:
          ref: { kind: local, name: helper }
  helper:
    steps:
      - id: format
        type: llm.call
```

Literal loop counts and arrays are previewed up to five iterations. Runtime
guards, routes, and switches use the seed and produce visible warnings.

## Safety and limits

- workflow YAML: 1 MiB
- input JSON: 256 KiB
- simulated step occurrences: 200
- workflow actor instances: 32
- visual clones at one fork: 16

Hard-limit failures return HTTP `422`. Disconnecting the client cancels pending
timer delays. `X-Tenant-Id`, when supplied, is propagated only as an
OpenTelemetry activity tag; YAML and input content are not recorded.

## Build and test

```bash
dotnet build src/GnOuGo.Assets.Animation.Server/GnOuGo.Assets.Animation.Server.csproj
dotnet test tests/GnOuGo.Assets.Animation.Server.Tests/GnOuGo.Assets.Animation.Server.Tests.csproj
```
