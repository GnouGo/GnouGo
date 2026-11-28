# Workflow agent runners

For a bounded code task, the SDK waits up to the approved elapsed-time budget
for the full sequence of model turns and tool decisions. The workflow deadline
also covers session creation and evidence collection.

The workflow engine calls an explicitly selected operation directly. It composes known operations with conditions, loops and validated mappings. For example, once an ordered list of product URLs is known, a loop can visit each URL, write the extracted rows with the document tool and reopen the workbook for verification. A TaskPlan interpret transform compiles to a bounded `llm.call` when the inputs provide everything needed for a semantic decision.

Use `agent.run` when an observation determines the next action. The plan identifies the decision in the objective and binds the inputs, workspace, capabilities, budget, output schema and verification requirements. The approved runner name resolves to a host-configured profile. Generic discovery describes conditional capabilities without starting the SDK. A host selecting a code-only workflow can inspect the managed runtime policy before advertising its capabilities to the planner. At execution the adapter checks the approved subset before dispatch. A failed invocation returns to workflow error handling.

```json
{
  "Flow": {
    "AgentRunners": { "coding": "code", "observe": "business" },
    "AgentTools": { "observe": { "GnOuGo.Browser.Mcp": ["browser_get_content"] } }
  }
}
```

**Business work.** Flow.Core runs the workflow model with the selected MCP contracts. Each decision requests one tool, reads a page of an existing observation, or supplies the final structured output. The host checks the contract digest, arguments, action allowance and response schema, and dispatches through the workflow MCP factory with the original tenant and invocation identity. The journal retains full requests and results. The model receives bounded pages with offsets. The child inference budget charges the parent workflow budget. A missing tool receipt requires reconciliation. Each required `mcp.result` names the pinned capability and checks observed request/result facts.

**Code work.** Review and editing use the same managed Copilot SDK session service. The host sets project roots, permitted extensions and writes. With `project.read`, native `view` reads an absolute project file or lists an absolute project directory through the registered session filesystem. Only file reads qualify for `file.read` evidence. With `project.write`, native `apply_patch` validates each patch destination and routes the changes through the same filesystem. OS-backed `rg` and shell commands require mandatory device isolation. A blocked session stops before inference. The task journal records the initialized session tool list and the individual tool receipts. Code permissions use the workflow human-input provider. SDK inference reservations retain conservative token upper bounds.

The approved elapsed-time limit covers the final SDK turn and managed-session
shutdown. A successful code task waits for observed tool completion and session
closure before it returns verification evidence. If the caller cancels or the
deadline expires during shutdown, the invocation requires reconciliation.

**Results.** Flow verifies the output schema and every requested evidence condition before a dependent step receives the output. A successful return has `status: completed` and a verification entry for each requirement with its ID, a passing result and an optional message. A dependent step can use the typed `output` directly. `file.read` records a successful SDK file read and its arguments, `file.content` records a file hash and change status, and `command.exit` records the observed terminal result. A review can use those facts to establish the files and checks it covered. Anchor validation establishes a location. The reviewer supplies the reasoning behind a finding. Publication requires an explicitly authorized workflow operation.

[Bounded coding example](../GnOuGo.Flow.Cli/examples/bounded-coding.yaml)


For a review, feed the output of the pinned Git comparison into `inputs.review` with `base`, `head` and per-file `files` containing paths, statuses and patches. The `review.diff` evidence subject is the exact head SHA. The code task returns findings, the files it read and limitations. The verifier checks diff anchors, removes duplicate ambiguity and compares claimed coverage with successful SDK read receipts. A review can return zero findings. Publication requires its own authorized workflow operation.
