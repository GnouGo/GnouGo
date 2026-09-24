# Flow hybrid planning replacement

Issue: https://github.com/GnouGo/GnouGo/issues/112

The accepted implementation replaces the semantic/grounded planning pipeline with
requirements, progressive capability discovery, one executable graph, deterministic
validation and approval. Adaptive work belongs inside bounded agent stages.
Breaking changes remove schema-8 execution, legacy DTOs and top-level checkpointing.
Old encrypted records remain untouched.

## Parent baseline

Source: `46c2c77fea19c952d3258d744d886fe52ef52d33`.
Captured in a separate detached checkout before implementation:

- Flow.Planning: 269 tests passed.
- Flow.Core: 890 tests passed.
- Both builds completed without warnings.

These deterministic tests are not a live-model reliability measurement.
Live corpus comparison and execution acceptance remain outstanding.

## Implementation sequence

1. Provider-neutral bounded agent contracts and observed-evidence verification.
2. Canonical graph planner and progressive catalog; delete superseded models.
3. Managed Copilot adapter and encrypted durable invocation journal.
4. Host, API, UI, CLI and corpus migration; delete compatibility paths.
5. Failure injection, live comparison, packaging, published smokes and documentation.

The issue contains the complete acceptance checklist. The PR stays draft until
all required implementation and verification is complete.
