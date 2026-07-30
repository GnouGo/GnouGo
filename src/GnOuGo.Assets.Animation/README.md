# GnOuGo.Assets.Animation

Autonomous, deterministic SVG team animation library for GnOuGo. It parses a
small Flow-shaped preview YAML format, validates only its visual structure,
plans synthetic team activity, renders a semantic SVG scene, and produces a
timer-ready event schedule.

It does **not** reference or execute `GnOuGo.Flow.Core`. Preview validation is
not authoritative Flow validation. LLM, MCP, scripts, expressions, and workflow
steps are never executed.

## Usage

```csharp
using GnOuGo.Assets.Animation;
using GnOuGo.Assets.Animation.Preview;

var validation = WorkflowPreviewValidator.ParseAndValidate(yaml);
if (!validation.IsValid)
    return;

var plan = GnouGnouAnimationPlanner.Build(validation, new GnouGnouAnimationOptions
{
    Seed = 42,
    Scene = AnimationSceneKind.Office
});
var svg = GnouGnouAnimationSvgRenderer.Render(plan).Svg;
var events = WorkflowSimulationScheduler.Schedule(plan, speed: 2);
```

Supported structural concepts are entrypoints, workflows, atomic steps,
sequences, parallel branches, sequential/parallel loops, switches, and static
local workflow calls. Runtime-dependent choices are selected deterministically
from the seed and reported as preview warnings.

The public API is intentionally flat and serialization-friendly:

- `WorkflowPreviewParser.Parse(...)` parses version 1 preview YAML.
- `WorkflowPreviewValidator.Validate(...)` checks its visual structure.
- `GnouGnouAnimationPlanner.Build(...)` creates actors, workflow lanes, graph
  nodes and edges, roundabout stations, the project parcel, diagnostics, and stable
  timeline cues.
- `GnouGnouAnimationPlanner.BuildLive(...)` creates the same deterministic
  scene graph with no synthetic scheduled events.
- `GnouGnouAnimationSvgRenderer.Render(...)` produces a script-free,
  dynamically sized white-canvas SVG (minimum 1600x900). Scene enum values are
  retained as deterministic metadata for compatibility.
- `WorkflowSimulationScheduler.Schedule(...)` scales cues to 0.5x, 1x, 2x, or
  4x playback.
- `WorkflowLiveAnimationSession.Apply(...)` maps real, neutral execution
  signals to immediate scene patches and animation events without depending on
  a workflow engine.

The plan is laid out from top to bottom like a Mermaid workflow. Every visible
workflow invocation receives its own vertical swimlane and actor. LLM and MCP
tasks receive a clean road roundabout with a semantic glyph. `human.*` tasks
receive a visible blocking roundabout with persistent
waiting and resume events.
Dynamic orchestration stays visible as well: `workflow.plan`, `workflow.route`,
and `workflow.execute` use distinct planning, routing, and handoff roundabout
glyphs. A child workflow discovered by planning or routing emits
`workflow.discovered`. Runtime-added children join a reusable stage slot
instead of extending the diagram. The bearded Master carries the parcel to an
original paired blue GnOuGo transit portals. A short horizontal source portal
appears beside the calling routing roundabout. The Master and parcel move
right-to-left through it while fading out; the parent scene then swaps while
they are invisible. An identical destination portal appears beside the
dynamic workflow Start, where both continue right-to-left and progressively
reappear. Only the portal pair carrying work is active; parallel selections
receive independently keyed temporary parcel copies. On completion, the
specialist follows the child road down to Return and the same portal sequence
runs from Return back to the original routing roundabout while the child scene
disappears and the preserved parent scene returns. The returned parent portal
then remains beside its routing roundabout as a quiet, inactive blue landmark.
Statically known workflow
lanes and their handoffs retain the original multi-lane presentation without
transit portals.
Parallel clone parcels are attached to their workflow scene and hidden during
scene swaps, then removed when their merge event completes.
Scheduled events that share a parallel timeline offset are presented as one
batch, so every cloned GnOuGo starts and traverses its own branch concurrently
instead of waiting in a visual FIFO. While a parallel cohort is active, Follow
tracks the frame-by-frame centroid of all its visible GnOuGos and converges on
the join as they merge. Completed branches walk downward to the actual
Parallel Join and wait there; the parent reaches the same marker before the
clones recombine.
Parallel, foreach, decision, and handoff traffic markers are retained only when their
subtree contains that visible long-running work. Smooth curved roads use a
single centered dashed line with no shadow, texture filter, or roadside
decoration, and actors follow the same SVG route geometry while walking. The
background is always plain white. Short bookkeeping steps in an otherwise
visible workflow stay in the event feed without adding roundabouts, roads,
pauses, or poses.

Synthetic work intentionally varies by task: MCP work can be quick or
steady, while LLM work uses a steady or longer deep-focus duration. These
timings remain deterministic for a seed.

One evolving `task-root` project parcel represents the work. It falls from the
sky, travels with the active actor, crosses workflow handoffs, gains completion
stamps at atomic steps, returns to the caller, and is launched from the
master's delivery dock. Parallel branches use temporary parcel copies that
merge at their join. An optional `SimulationFailureTarget` turns the parcel red
and propagates it through the same return and delivery route.

The rendered GnOuGos use the opt-in Bears animation rig. Body parts are exposed
as semantic SVG groups while the document remains script-free. The standalone
web client drives the rig with `requestAnimationFrame`: alternating arm and leg
footfalls, breathing, independent ear twitches, head bobbing, blinking and
directional pupils, alternating keyboard hands and fingers, pickup and handoff
reaches, matrix duplication, waiting, delivery, celebration, and failure.
At rest, actors are balanced across looking-around, side-sway, stretching,
toe-tapping, pondering, and little-wave personalities. Each GnOuGo receives a
different seeded clock offset and gesture tempo, preventing synchronized idle
movement even when several runtime workflows appear together.

## Live execution integration

Engine integrations create a `WorkflowLiveAnimationSession` from a prepared
plan and feed it flat `AnimationExecutionSignal` records. Workflow and step
instance IDs must remain stable for one execution. The session returns
`AnimationLiveUpdate` values containing either a `SimulationEvent` or an
`AnimationScenePatch` for a workflow discovered at runtime.
Runtime YAML is parsed server-side to add only sanitized visual nodes. When a
generated workflow contains no LLM, MCP, human, or orchestration step, up to
eight compact leaf stations keep short `set`, template, and custom work
visible. If source is unavailable, the first runtime step reuses the generic
roundabout and later leaf steps arrive as bounded incremental scene patches.

The package includes
`Runtime/gnougnou-workflow-animation-controller.ts`. It owns actor movement,
route following, roundabouts, parcel state, preserved workflow scene layers,
branching transit pipes, scene patches, reduced motion, and delegates
articulated character poses to the Bears controller. Its viewport camera keeps
the initial visual scale, follows the active plan or pipe transfer, and still
exposes explicit fit and drag navigation. Workflow source
text is used only server-side to build safe visual models and is never included
in live browser events.

Real-telemetry consumers can use `enqueueEvent(...)` to preserve a short visual
presentation gap when execution events arrive too quickly to see. Autonomous
timer-driven consumers continue to use `applyEvent(...)` directly. Persistent
live `step.started` and human-waiting signals are rendered as calm repeated
action cycles and stop only when the corresponding completion, resume,
cancellation, or failure event arrives.

The controller continues draining queued telemetry after a recoverable visual
error and exposes `data-animation-event-count`, `data-animation-last-event`,
`data-animation-queued-events`, and `data-animation-error` on its host. Exact
ID lookup includes a compatibility fallback for embedded webviews that do not
provide `CSS.escape`.

Default safety limits are 200 simulated step occurrences, 32 workflow actors,
16 clones per fork, and five loop iterations. Callers can lower these limits
through `GnouGnouAnimationOptions`.

## Build and test

```bash
dotnet build src/GnOuGo.Assets.Animation/GnOuGo.Assets.Animation.csproj
dotnet test tests/GnOuGo.Assets.Animation.Tests/GnOuGo.Assets.Animation.Tests.csproj
dotnet pack src/GnOuGo.Assets.Animation/GnOuGo.Assets.Animation.csproj -c Release
```
