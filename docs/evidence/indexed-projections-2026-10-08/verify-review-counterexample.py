"""Verify the retained review counterexample. No model, browser or workflow execution."""
import json
from pathlib import Path

plan = json.loads((Path(__file__).parent / "task-plan-r12.json").read_text())
branch = next(t for t in plan["root"]["tasks"] if t["id"] == "accept_cookie_if_present")
click = next(t for t in branch["body"]["tasks"] if t["id"] == "click_cookie_consent")
values = {"interpret_initial_page": {"blocked": False, "observationComplete": True,
    "consentReference": None, "consentAction": None}}

def value(binding):
    kind = binding["kind"]
    if kind == "output":
        return values[binding["source"]][binding["port"]]
    if kind == "null":
        return None
    if kind == "boolean":
        return binding["boolean"]
    if kind == "string":
        return binding["text"]
    if kind == "predicate":
        items = [value(v) for v in binding["items"]]
        if binding["predicate"] == "and":
            return all(items)
        if binding["predicate"] == "equal":
            return items[0] == items[1]
        if binding["predicate"] == "not_equal":
            return items[0] != items[1]
    raise ValueError("Unsupported retained guard")

assert value(branch["condition"]) is True
assert value(click["requires"]) is False
print("Confirmed: absent optional control enters its branch, then fails the mandatory click requirement.")
