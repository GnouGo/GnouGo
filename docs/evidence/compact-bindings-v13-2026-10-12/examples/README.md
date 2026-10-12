# Actual compiler examples

These files are complete compiler outputs from the equivalence regressions, not handwritten targets. The excerpts below are taken from their parsed expression strings; contracts remain in the linked YAML. The retained 24-task fixture is available separately as `../before-v12.yaml` and `../after-v13.yaml`.

## Boolean validation on a control

[Complete v12 YAML](control-v12.yaml) · [Complete v13 YAML](control-v13.yaml)

The boolean preparation set disappears. The switch validates its `condition` before recording the selected branch. Its effect/body remains a distinct boundary.

Before:
```javascript
${
{
  value: data.inputs.authorized
}
}
```

After (`expr`):
```javascript
${
(() => {
  const condition = data.inputs.authorized;
  return condition;
})()
}
```

## Reached nullable branch checks

[Complete v12 YAML](nullable-branch-v12.yaml) · [Complete v13 YAML](nullable-branch-v13.yaml)

Two branch sets, their switch and the terminal merge move to the sole checked output. Only the chosen branch validates its value; all four original origins remain in metadata. The first-present helper remains because a present null must not fall through.

After (`outputs.text.expr`):

```javascript
${
(() => {
  const checkedRecord = (condition => condition === true || condition === "true")(data.inputs.authorized) ? (() => {
    const predicate1Result1 = {
      value: data.inputs.label
    };
    return {
      n_6f5a6034e770acbf: predicate1Result1
    };
  })() : (() => {
    const predicate1Result2 = {
      value: null
    };
    return {
      n_5ad38304b535c298: predicate1Result2
    };
  })();
  const checkedRecord_2 = m.select(checkedRecord, [["n_6f5a6034e770acbf", "value"], ["n_5ad38304b535c298", "value"]], false);
  return checkedRecord_2;
})()
}
```

## Four checks on a terminal output

[Complete v12 YAML](output-v12.yaml) · [Complete v13 YAML](output-v13.yaml)

The terminal set disappears; its four ordered checks now execute on the sole output. There is no finalizer, handler, retry, shared output or identity observer. The enclosing result is the durable boundary.

Before (`set.input`):
```javascript
${
(() => {
  const label = data.inputs.label;
  const requiredCondition = data.inputs.authorized;
  return {
    chosen_2: {
      chosen: label
    }
  };
})()
}
```

After (`outputs.chosen.expr`):
```javascript
${
(() => {
  const label = data.inputs.label;
  const requiredCondition = data.inputs.authorized;
  return label;
})()
}
```

## Counterexample: cleanup preserves the set

[v12 YAML](retained-finalizer-v12.yaml) · [v13 YAML](retained-finalizer-v13.yaml). The pre-cleanup label and authorization checks stay in the normal path. Moving them to output evaluation would run cleanup before discovering their failure. No output-inlining rule is applied.

The large comparison keeps physical first-present keys and necessary checked-object envelopes. Names and code are still more verbose than the design sketch; these are documented limitations, not permission to discard intermediate schemas.
