from __future__ import annotations

from .shared import *  # noqa: F401,F403
from gnougo_flow_core.runtime import _extract_usage_telemetry


@dataclass(slots=True)
class _CapabilityCatalogEntry:
    catalog_id: str
    kind: str
    method: str
    description: str = ""
    server: str | None = None
    input_schema: Any = None
    output_schema: Any = None
    meta: Any = None
    request_bindings: list[dict[str, Any]] = field(default_factory=list)


@dataclass(slots=True)
class _LockedCapability:
    operation_id: str
    description: str
    required: bool
    entries: list[_CapabilityCatalogEntry]
    match_status: str = "matched"
    execution_kind: str = "external_effect"
    external_effect_kind: str = "none"


@dataclass(slots=True)
class _CapabilityPreflightState:
    mode: str
    catalog: list[_CapabilityCatalogEntry] = field(default_factory=list)
    locked: list[_LockedCapability] = field(default_factory=list)
    constraints: list[dict[str, Any]] = field(default_factory=list)
    inventory: dict[str, Any] | None = None


class _WorkflowPlanCapabilityPreflightMixin:
    _CAPABILITY_MAX_SELECTOR_DEPTH = 4
    _CAPABILITY_MAX_SELECTOR_VALUES = 64
    _CAPABILITY_MAX_DESCRIPTION_LENGTH = 512
    _CAPABILITY_MAX_PAGE_CHARS = 64_000
    _CAPABILITY_MAX_PAGES = 64
    _CAPABILITY_MAX_CANDIDATES = 24
    _CAPABILITY_MAX_EXPANDED_CHARS = 256_000

    async def _run_capability_preflight_async(
        self,
        ctx: StepExecutionContext,
        input_obj: dict[str, Any],
        instruction: str,
        provider: str | None,
        model: str,
        reasoning: str | None,
    ) -> _CapabilityPreflightState:
        config = input_obj.get("capability_preflight")
        if config is None:
            return _CapabilityPreflightState("off")
        if not isinstance(config, dict):
            raise WorkflowRuntimeException(
                ErrorCodes.INPUT_VALIDATION,
                "workflow.plan capability_preflight must be an object",
            )
        mode = str(config.get("mode", "off")).strip().lower()
        if mode not in {"off", "explicit", "infer"}:
            raise WorkflowRuntimeException(
                ErrorCodes.INPUT_VALIDATION,
                "workflow.plan capability_preflight.mode must be off, infer, or explicit",
            )
        state = _CapabilityPreflightState(mode)
        if mode == "off":
            return state

        state.catalog = await self._discover_capability_catalog_async(ctx)
        if mode == "explicit":
            self._resolve_explicit_capabilities(config, state)
        else:
            await self._infer_capabilities_async(
                ctx, config, state, instruction, provider, model, reasoning
            )
        self._raise_for_unavailable_capabilities(state)
        return state

    async def _discover_capability_catalog_async(
        self, ctx: StepExecutionContext
    ) -> list[_CapabilityCatalogEntry]:
        entries: list[_CapabilityCatalogEntry] = []
        factory = ctx.engine.mcp_client_factory
        counter = 1
        if factory is not None:
            metadata = list(getattr(factory, "server_metadata", []) or [])
            for server in metadata:
                server_name = getattr(server, "name", None) if not isinstance(server, dict) else server.get("name")
                if not isinstance(server_name, str) or not server_name.strip():
                    continue
                try:
                    session = await factory.get_client_async(server_name)
                    tools = await session.list_tools_async()
                except Exception as exc:
                    raise WorkflowRuntimeException(
                        ErrorCodes.CAPABILITY_PREFLIGHT_DISCOVERY_FAILED,
                        f"Capability discovery failed for MCP server '{server_name}': {exc}",
                        retryable=False,
                        details={"server": server_name},
                    ) from exc
                try:
                    prompts = await session.list_prompts_async()
                except Exception as exc:
                    message = str(exc).lower()
                    if not any(
                        marker in message
                        for marker in ("method not found", "not implemented", "not supported", "no handler")
                    ):
                        raise WorkflowRuntimeException(
                            ErrorCodes.CAPABILITY_PREFLIGHT_DISCOVERY_FAILED,
                            f"Capability discovery failed for MCP server '{server_name}': {exc}",
                            retryable=False,
                            details={"server": server_name},
                        ) from exc
                    prompts = []
                for kind, capabilities in (("tool", tools or []), ("prompt", prompts or [])):
                    for capability in capabilities:
                        description = str(getattr(capability, "description", None) or "")[
                            : self._CAPABILITY_MAX_DESCRIPTION_LENGTH
                        ]
                        base = _CapabilityCatalogEntry(
                            catalog_id=f"cap_{counter:06d}",
                            kind=kind,
                            server=server_name,
                            method=str(getattr(capability, "name", "")),
                            description=description,
                            input_schema=copy.deepcopy(getattr(capability, "input_schema", None)),
                            output_schema=copy.deepcopy(getattr(capability, "output_schema", None)),
                            meta=copy.deepcopy(getattr(capability, "meta", None)),
                        )
                        entries.append(base)
                        counter += 1
                        if kind == "tool":
                            variants = self._expand_selector_variants(base)
                            for bindings in variants:
                                entries.append(
                                    _CapabilityCatalogEntry(
                                        catalog_id=f"cap_{counter:06d}",
                                        kind=kind,
                                        server=server_name,
                                        method=base.method,
                                        description=description,
                                        input_schema=copy.deepcopy(base.input_schema),
                                        output_schema=copy.deepcopy(base.output_schema),
                                        meta=copy.deepcopy(base.meta),
                                        request_bindings=bindings,
                                    )
                                )
                                counter += 1

        for step_type in sorted(ctx.engine.registry.get_dsl_snippet_map()):
            entries.append(
                _CapabilityCatalogEntry(
                    catalog_id=f"cap_{counter:06d}",
                    kind="native",
                    method=step_type,
                    description=f"Native GnOuGo.Flow step {step_type}",
                )
            )
            counter += 1

        expanded_length = sum(len(self._format_catalog_entry(entry)) for entry in entries)
        if expanded_length > self._CAPABILITY_MAX_EXPANDED_CHARS:
            raise WorkflowRuntimeException(
                ErrorCodes.CAPABILITY_PREFLIGHT_INFERENCE_FAILED,
                f"Expanded capability catalog exceeds {self._CAPABILITY_MAX_EXPANDED_CHARS} characters.",
            )
        return entries

    def _expand_selector_variants(
        self, entry: _CapabilityCatalogEntry
    ) -> list[list[dict[str, Any]]]:
        values: list[tuple[str, Any]] = []

        def visit(schema: Any, pointer: str, depth: int) -> None:
            if not isinstance(schema, dict) or depth > self._CAPABILITY_MAX_SELECTOR_DEPTH:
                return
            if "const" in schema and isinstance(schema.get("const"), (str, int, float, bool)):
                values.append((pointer or "/", copy.deepcopy(schema["const"])))
            enum_values = schema.get("enum")
            if isinstance(enum_values, list):
                scalars = [item for item in enum_values if isinstance(item, (str, int, float, bool)) or item is None]
                if len(values) + len(scalars) > self._CAPABILITY_MAX_SELECTOR_VALUES:
                    raise WorkflowRuntimeException(
                        ErrorCodes.CAPABILITY_PREFLIGHT_INFERENCE_FAILED,
                        f"Capability selector expansion exceeds {self._CAPABILITY_MAX_SELECTOR_VALUES} values.",
                    )
                values.extend((pointer or "/", copy.deepcopy(item)) for item in scalars)
            properties = schema.get("properties")
            if isinstance(properties, dict):
                for name, child in properties.items():
                    escaped = str(name).replace("~", "~0").replace("/", "~1")
                    visit(child, f"{pointer}/{escaped}", depth + 1)

        visit(entry.input_schema, "", 0)
        unique: list[list[dict[str, Any]]] = []
        seen: set[str] = set()
        for path, value in values:
            fingerprint = f"{path}\x1f{json.dumps(value, sort_keys=True, default=str)}"
            if fingerprint in seen:
                continue
            seen.add(fingerprint)
            unique.append([{"path": path, "value": value}])
        return unique

    def _resolve_explicit_capabilities(
        self,
        config: dict[str, Any],
        state: _CapabilityPreflightState,
    ) -> None:
        requirements = config.get("requirements", [])
        constraints = config.get("constraints", [])
        if not isinstance(requirements, list) or not isinstance(constraints, list):
            raise WorkflowRuntimeException(
                ErrorCodes.INPUT_VALIDATION,
                "capability_preflight requirements and constraints must be arrays",
            )
        state.constraints = copy.deepcopy(constraints)
        denied = self._resolve_denied_catalog_ids(constraints, state.catalog)
        identifiers: set[str] = set()
        for index, requirement in enumerate(requirements):
            if not isinstance(requirement, dict):
                raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "Capability requirements must be objects")
            operation_id = str(requirement.get("id", "")).strip()
            description = str(requirement.get("description", "")).strip()
            if not operation_id or operation_id in identifiers or not description:
                raise WorkflowRuntimeException(
                    ErrorCodes.INPUT_VALIDATION,
                    "Capability requirement ids must be unique and descriptions must be non-empty",
                )
            identifiers.add(operation_id)
            alternatives = requirement.get("alternatives", [])
            if not isinstance(alternatives, list) or not alternatives:
                raise WorkflowRuntimeException(
                    ErrorCodes.INPUT_VALIDATION,
                    f"Capability requirement '{operation_id}' requires alternatives",
                )
            matches: list[_CapabilityCatalogEntry] = []
            for alternative in alternatives:
                matches.extend(self._match_explicit_alternative(alternative, state.catalog))
            matches = [entry for entry in matches if entry.catalog_id not in denied]
            selected = matches[:1]
            required = bool(requirement.get("required", True))
            state.locked.append(
                _LockedCapability(
                    operation_id,
                    description,
                    required,
                    selected,
                    "matched" if selected else "unavailable",
                )
            )

    def _resolve_denied_catalog_ids(
        self, constraints: list[Any], catalog: list[_CapabilityCatalogEntry]
    ) -> set[str]:
        denied: set[str] = set()
        for constraint in constraints:
            if not isinstance(constraint, dict):
                continue
            alternatives = constraint.get("denied_alternatives", [])
            if not isinstance(alternatives, list):
                continue
            for alternative in alternatives:
                denied.update(entry.catalog_id for entry in self._match_explicit_alternative(alternative, catalog))
        return denied

    def _match_explicit_alternative(
        self,
        alternative: Any,
        catalog: list[_CapabilityCatalogEntry],
    ) -> list[_CapabilityCatalogEntry]:
        if not isinstance(alternative, dict):
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "Capability alternatives must be objects")
        server = alternative.get("server")
        kind = str(alternative.get("kind", "tool")).lower()
        if kind == "local":
            kind = "native"
        method = alternative.get("method") or alternative.get("name")
        bindings = self._parse_request_bindings(alternative.get("request_bindings"))
        matches = [
            entry for entry in catalog
            if entry.kind == kind
            and entry.method == method
            and (server is None or entry.server == server)
        ]
        if bindings:
            base = next((entry for entry in matches if not entry.request_bindings), None)
            if base is not None:
                for binding in bindings:
                    if not self._schema_documents_binding(base.input_schema, binding):
                        raise WorkflowRuntimeException(
                            ErrorCodes.INPUT_VALIDATION,
                            f"Capability alternative for '{server}/{method}' uses request binding {binding['path']}={binding['value']!r} outside documented scalar selectors.",
                        )
            matches = [entry for entry in matches if entry.request_bindings == bindings]
        else:
            matches = [entry for entry in matches if not entry.request_bindings]
        return matches

    @staticmethod
    def _parse_request_bindings(value: Any) -> list[dict[str, Any]]:
        if value is None:
            return []
        if not isinstance(value, list):
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "request_bindings must be an array")
        bindings: list[dict[str, Any]] = []
        for item in value:
            if not isinstance(item, dict) or not isinstance(item.get("path"), str) or not item["path"].startswith("/"):
                raise WorkflowRuntimeException(
                    ErrorCodes.INPUT_VALIDATION,
                    "Capability request binding paths must use RFC 6901 JSON Pointer syntax",
                )
            bindings.append({"path": item["path"], "value": copy.deepcopy(item.get("value"))})
        return bindings

    @staticmethod
    def _schema_documents_binding(schema: Any, binding: dict[str, Any]) -> bool:
        current = schema
        for raw in binding["path"].lstrip("/").split("/"):
            name = raw.replace("~1", "/").replace("~0", "~")
            if not isinstance(current, dict):
                return False
            properties = current.get("properties")
            if not isinstance(properties, dict) or name not in properties:
                return False
            current = properties[name]
        if not isinstance(current, dict):
            return False
        return current.get("const") == binding["value"] or (
            isinstance(current.get("enum"), list) and binding["value"] in current["enum"]
        )

    async def _infer_capabilities_async(
        self,
        ctx: StepExecutionContext,
        config: dict[str, Any],
        state: _CapabilityPreflightState,
        instruction: str,
        provider: str | None,
        model: str,
        reasoning: str | None,
    ) -> None:
        inventory_prompt = self._build_capability_inventory_prompt(instruction)
        inventory_response = await self._call_capability_inference_phase(
            ctx,
            inventory_prompt,
            self._capability_inventory_schema(),
            "capability_inventory",
            provider,
            model,
            reasoning,
        )
        try:
            inventory = self._parse_and_validate_inventory_response(inventory_response)
        except WorkflowRuntimeException as exc:
            repair_prompt = self._build_capability_repair_prompt(
                inventory_prompt, inventory_response.text, exc
            )
            repaired = await self._call_capability_inference_phase(
                ctx,
                repair_prompt,
                self._capability_inventory_schema(),
                "capability_inventory_repair",
                provider,
                model,
                reasoning,
            )
            inventory = self._parse_and_validate_inventory_response(repaired)
        self._apply_default_write_confirmation(inventory)
        state.inventory = copy.deepcopy(inventory)

        pages = self._page_capability_catalog(state.catalog)
        if len(pages) > self._CAPABILITY_MAX_PAGES:
            raise WorkflowRuntimeException(
                ErrorCodes.CAPABILITY_PREFLIGHT_INFERENCE_FAILED,
                f"Capability catalog requires more than {self._CAPABILITY_MAX_PAGES} pages.",
            )
        matcher_prompt = self._build_capability_matcher_prompt(inventory, pages)
        match_response = await self._call_capability_inference_phase(
            ctx,
            matcher_prompt,
            self._capability_match_schema(),
            "capability_candidate_matching",
            provider,
            model,
            reasoning,
        )
        try:
            matches = self._parse_and_validate_match_response(match_response, inventory, state.catalog)
        except WorkflowRuntimeException as exc:
            repair_prompt = self._build_capability_repair_prompt(
                matcher_prompt, match_response.text, exc
            )
            repaired = await self._call_capability_inference_phase(
                ctx,
                repair_prompt,
                self._capability_match_schema(),
                "capability_candidate_repair",
                provider,
                model,
                reasoning,
            )
            matches = self._parse_and_validate_match_response(repaired, inventory, state.catalog)
        by_id = {entry.catalog_id: entry for entry in state.catalog}
        match_by_operation = {
            str(item.get("operation_id")): item
            for item in matches["operation_matches"]
            if isinstance(item, dict)
        }
        for operation in inventory.get("operations", []):
            operation_id = str(operation.get("id"))
            match = match_by_operation.get(operation_id, {})
            status = str(match.get("status", "unavailable")).lower()
            catalog_ids = match.get("catalog_ids", [])
            selected = [by_id[item] for item in catalog_ids if item in by_id] if isinstance(catalog_ids, list) else []
            if status == "local":
                local_method = (
                    "human.input"
                    if operation.get("execution_kind") == "human_interaction"
                    else "set"
                )
                selected = [
                    entry for entry in state.catalog
                    if entry.kind == "native" and entry.method == local_method
                ][:1]
            if status in {"matched", "composed"} and not selected:
                status = "unavailable"
            state.locked.append(
                _LockedCapability(
                    operation_id=operation_id,
                    description=str(operation.get("description", "")),
                    required=bool(operation.get("required", True)),
                    entries=selected,
                    match_status=status,
                    execution_kind=str(operation.get("execution_kind", "external_effect")),
                    external_effect_kind=str(operation.get("external_effect_kind", "none")),
                )
            )
        state.constraints = copy.deepcopy(inventory.get("constraints", []))

    async def _call_capability_inference_phase(
        self,
        ctx: StepExecutionContext,
        prompt: str,
        schema: dict[str, Any],
        phase: str,
        provider: str | None,
        model: str,
        reasoning: str | None,
    ) -> LLMResponse:
        with ctx.begin_telemetry_span(
            f"workflow.plan.{phase}",
            phase,
            [
                ("gen_ai.operation.name", "chat"),
                ("gen_ai.system", provider or "unknown"),
                ("gen_ai.request.model", model),
            ],
        ) as span:
            try:
                response = await ctx.engine.call_llm_async(
                    LLMRequest(
                        provider=provider,
                        model=model,
                        reasoning=reasoning,
                        prompt=prompt,
                        use_background_mode=True,
                        structured_output_strict=True,
                        structured_output_schema=schema,
                    )
                )
            except asyncio.CancelledError:
                raise
            except WorkflowRuntimeException:
                raise
            except Exception as exc:
                raise WorkflowRuntimeException(
                    ErrorCodes.CAPABILITY_PREFLIGHT_INFERENCE_FAILED,
                    "Capability inference returned an invalid or incomplete contract.",
                    details={
                        "phase": "capability_inference",
                        "inference_phase": f"{phase}_call",
                        "inference_error": str(exc)[:1000],
                        "reason": type(exc).__name__,
                    },
                ) from exc
            self._add_usage_attributes(span, response.usage, model, provider, ctx.engine.llm_options)
            _extract_usage_telemetry(ctx, response.usage, model, provider)
            self._record_strict_planner_response_evidence(
                ctx, provider, model, response.json_payload, schema
            )
            return response

    def _parse_and_validate_inventory_response(self, response: LLMResponse) -> dict[str, Any]:
        inventory = response.json_payload or self._try_parse_json(response.text)
        if not isinstance(inventory, dict) or inventory.get("complete") is not True:
            raise WorkflowRuntimeException(
                ErrorCodes.CAPABILITY_PREFLIGHT_INFERENCE_FAILED,
                "Capability inventory was incomplete or invalid.",
            )
        self._validate_inventory(inventory)
        return inventory

    def _parse_and_validate_match_response(
        self,
        response: LLMResponse,
        inventory: dict[str, Any],
        catalog: list[_CapabilityCatalogEntry],
    ) -> dict[str, Any]:
        matches = response.json_payload or self._try_parse_json(response.text)
        if not isinstance(matches, dict) or not isinstance(matches.get("operation_matches"), list):
            raise WorkflowRuntimeException(
                ErrorCodes.CAPABILITY_PREFLIGHT_INFERENCE_FAILED,
                "Capability matcher returned an invalid response.",
            )
        expected = [str(item.get("id")) for item in inventory.get("operations", [])]
        items = matches["operation_matches"]
        received = [str(item.get("operation_id")) for item in items if isinstance(item, dict)]
        catalog_ids = {entry.catalog_id for entry in catalog}
        invalid_catalog_ids = [
            catalog_id
            for item in items
            if isinstance(item, dict) and isinstance(item.get("catalog_ids"), list)
            for catalog_id in item["catalog_ids"]
            if catalog_id not in catalog_ids
        ]
        too_many_candidates = any(
            isinstance(item, dict)
            and isinstance(item.get("catalog_ids"), list)
            and len(item["catalog_ids"]) > self._CAPABILITY_MAX_CANDIDATES
            for item in items
        )
        if sorted(received) != sorted(expected) or len(received) != len(set(received)):
            raise WorkflowRuntimeException(
                ErrorCodes.CAPABILITY_PREFLIGHT_INFERENCE_FAILED,
                "Capability matcher must return every inventory operation exactly once.",
            )
        if invalid_catalog_ids or too_many_candidates:
            raise WorkflowRuntimeException(
                ErrorCodes.CAPABILITY_PREFLIGHT_INFERENCE_FAILED,
                "Capability matcher returned unknown catalog IDs or exceeded the candidate bound.",
            )
        return matches

    @staticmethod
    def _build_capability_repair_prompt(
        original_prompt: str,
        response_text: str | None,
        error: WorkflowRuntimeException,
    ) -> str:
        return (
            original_prompt.rstrip()
            + "\n\nThe previous structured response was invalid. Return one corrected complete response."
            + f"\n<invalid_response>\n{response_text or ''}\n</invalid_response>"
            + f"\n<validation_error>\n{error}\n</validation_error>"
        )

    @staticmethod
    def _try_parse_json(text: str | None) -> Any:
        try:
            return json.loads(text or "")
        except Exception:
            return None

    @staticmethod
    def _validate_inventory(inventory: dict[str, Any]) -> None:
        operations = inventory.get("operations")
        constraints = inventory.get("constraints")
        if not isinstance(operations, list) or not isinstance(constraints, list):
            raise WorkflowRuntimeException(
                ErrorCodes.CAPABILITY_PREFLIGHT_INFERENCE_FAILED,
                "Capability inventory operations and constraints must be arrays.",
            )
        identifiers: set[str] = set()
        for item in [*operations, *constraints]:
            if not isinstance(item, dict):
                raise WorkflowRuntimeException(ErrorCodes.CAPABILITY_PREFLIGHT_INFERENCE_FAILED, "Capability inventory items must be objects.")
            item_id = str(item.get("id", "")).strip()
            description = str(item.get("description", "")).strip()
            if not item_id or item_id in identifiers or not description:
                raise WorkflowRuntimeException(
                    ErrorCodes.CAPABILITY_PREFLIGHT_INFERENCE_FAILED,
                    "Capability inventory ids must be unique and descriptions non-empty.",
                )
            identifiers.add(item_id)

    @staticmethod
    def _apply_default_write_confirmation(inventory: dict[str, Any]) -> None:
        policy = str(inventory.get("external_write_confirmation_policy", "unspecified")).strip().lower()
        if policy == "forbidden":
            return
        operations = inventory.get("operations", [])
        has_write = any(
            isinstance(item, dict)
            and item.get("execution_kind") == "external_effect"
            and item.get("external_effect_kind") == "write"
            for item in operations
        )
        if has_write:
            operations.append(
                {
                    "id": "platform_confirm_external_write",
                    "description": "Require explicit human confirmation immediately before the first external write.",
                    "required": True,
                    "execution_kind": "human_interaction",
                    "external_effect_kind": "none",
                }
            )
            inventory.setdefault("constraints", []).append(
                {
                    "id": "platform_external_write_after_confirmation",
                    "description": "No external write may execute before explicit human confirmation.",
                    "required": True,
                }
            )

    @staticmethod
    def _build_capability_inventory_prompt(instruction: str) -> str:
        return (
            "You are a domain-neutral workflow runtime analyst. Inventory every distinct positive runtime operation "
            "and every constraint. Never copy, paraphrase, or restate host configuration chores as operations.\n"
            "Return structured JSON with complete, incomplete_reasons, operations, and constraints. "
            "Also return external_write_confirmation_policy as required, forbidden, or unspecified. "
            "Use required or forbidden only when the request explicitly establishes that policy; otherwise use unspecified. "
            "Each operation has id, description, required, execution_kind "
            "(external_effect|human_interaction|local_processing), and external_effect_kind "
            "(read|write|execute|lifecycle|none).\n\n<user_instruction>\n"
            f"{instruction}\n</user_instruction>"
        )

    def _build_capability_matcher_prompt(
        self, inventory: dict[str, Any], pages: list[str]
    ) -> str:
        return (
            "You are a domain-neutral capability matcher. Match each operation to exact catalog IDs. "
            "Return every operation exactly once. Select at most "
            f"{self._CAPABILITY_MAX_CANDIDATES} candidates per inventory item and the smallest sufficient composition.\n"
            "Use matched, composed, local, ambiguous, or unavailable. Constraints use enforced or policy_only.\n\n"
            f"<inventory>\n{json.dumps(inventory, ensure_ascii=False)}\n</inventory>\n"
            + "\n".join(f"<catalog_page index=\"{index + 1}\">\n{page}\n</catalog_page>" for index, page in enumerate(pages))
        )

    def _page_capability_catalog(self, catalog: list[_CapabilityCatalogEntry]) -> list[str]:
        pages: list[str] = []
        current: list[str] = []
        length = 0
        for entry in catalog:
            line = self._format_catalog_entry(entry)
            if current and length + len(line) + 1 > self._CAPABILITY_MAX_PAGE_CHARS:
                pages.append("\n".join(current))
                current = []
                length = 0
            current.append(line)
            length += len(line) + 1
        if current:
            pages.append("\n".join(current))
        return pages

    @staticmethod
    def _format_catalog_entry(entry: _CapabilityCatalogEntry) -> str:
        bindings = ""
        if entry.request_bindings:
            values = ",".join(
                f"{binding['path']}={json.dumps(binding['value'], ensure_ascii=False)}"
                for binding in entry.request_bindings
            )
            bindings = f" request_bindings=[{values}]"
        schema_notes = _WorkflowPlanCapabilityPreflightMixin._format_schema_notes(entry.input_schema)
        return (
            f"{entry.catalog_id} kind={entry.kind} server={entry.server or '-'} method={entry.method}"
            f"{bindings} description={entry.description}{schema_notes}"
        )

    @staticmethod
    def _format_schema_notes(schema: Any) -> str:
        if not isinstance(schema, dict):
            return ""
        notes: list[str] = []
        properties = schema.get("properties")
        if isinstance(properties, dict):
            for name, definition in properties.items():
                if isinstance(definition, dict) and definition.get("description"):
                    notes.append(f" /{name} description={definition['description']}")
        dependent = schema.get("dependentRequired")
        if isinstance(dependent, dict):
            for trigger, dependencies in dependent.items():
                notes.append(f" when /{trigger} is present require " + ",".join(f"/{item}" for item in dependencies))
        condition = schema.get("if")
        then = schema.get("then")
        if isinstance(condition, dict) and isinstance(then, dict):
            condition_properties = condition.get("properties")
            then_required = then.get("required")
            if isinstance(condition_properties, dict) and isinstance(then_required, list):
                for name, definition in condition_properties.items():
                    if isinstance(definition, dict):
                        value = definition.get("const")
                        if value is not None:
                            notes.append(
                                f" when /{name}={json.dumps(value, ensure_ascii=False)} require "
                                + ",".join(f"/{item}" for item in then_required)
                            )
        return "".join(notes)

    @staticmethod
    def _capability_inventory_schema() -> dict[str, Any]:
        operation = {
            "type": "object",
            "additionalProperties": False,
            "properties": {
                "id": {"type": "string"},
                "description": {"type": "string"},
                "required": {"type": "boolean"},
                "execution_kind": {
                    "type": "string",
                    "enum": ["external_effect", "human_interaction", "local_processing"],
                },
                "external_effect_kind": {
                    "type": "string",
                    "enum": ["read", "write", "execute", "lifecycle", "none"],
                },
            },
            "required": ["id", "description", "required", "execution_kind", "external_effect_kind"],
        }
        constraint = {
            "type": "object",
            "additionalProperties": False,
            "properties": {
                "id": {"type": "string"},
                "description": {"type": "string"},
                "required": {"type": "boolean"},
            },
            "required": ["id", "description", "required"],
        }
        return {
            "type": "object",
            "additionalProperties": False,
            "properties": {
                "complete": {"type": "boolean"},
                "external_write_confirmation_policy": {
                    "type": "string",
                    "enum": ["required", "forbidden", "unspecified"],
                },
                "incomplete_reasons": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "additionalProperties": False,
                        "properties": {
                            "id": {"type": "string"},
                            "description": {"type": "string"},
                        },
                        "required": ["id", "description"],
                    },
                },
                "operations": {"type": "array", "items": operation},
                "constraints": {"type": "array", "items": constraint},
            },
            "required": [
                "complete",
                "external_write_confirmation_policy",
                "incomplete_reasons",
                "operations",
                "constraints",
            ],
        }

    @staticmethod
    def _capability_match_schema() -> dict[str, Any]:
        operation_match = {
            "type": "object",
            "additionalProperties": False,
            "properties": {
                "operation_id": {"type": "string"},
                "status": {
                    "type": "string",
                    "enum": ["matched", "composed", "local", "ambiguous", "unavailable"],
                },
                "catalog_ids": {"type": "array", "items": {"type": "string"}},
                "candidate_catalog_ids": {"type": "array", "items": {"type": "string"}},
                "reason": {"type": "string"},
            },
            "required": ["operation_id", "status", "catalog_ids", "candidate_catalog_ids", "reason"],
        }
        constraint_match = {
            "type": "object",
            "additionalProperties": False,
            "properties": {
                "constraint_id": {"type": "string"},
                "status": {"type": "string", "enum": ["enforced", "policy_only", "ambiguous"]},
                "denied_catalog_ids": {"type": "array", "items": {"type": "string"}},
                "candidate_catalog_ids": {"type": "array", "items": {"type": "string"}},
                "reason": {"type": "string"},
            },
            "required": ["constraint_id", "status", "denied_catalog_ids", "candidate_catalog_ids", "reason"],
        }
        return {
            "type": "object",
            "additionalProperties": False,
            "properties": {
                "operation_matches": {"type": "array", "items": operation_match},
                "constraint_matches": {"type": "array", "items": constraint_match},
            },
            "required": ["operation_matches", "constraint_matches"],
        }

    @staticmethod
    def _raise_for_unavailable_capabilities(state: _CapabilityPreflightState) -> None:
        unavailable = [item for item in state.locked if item.required and not item.entries]
        if not unavailable:
            return
        details = {
            "unavailable_capabilities": [
                {
                    "id": item.operation_id,
                    "description": item.description,
                    "match_status": item.match_status,
                }
                for item in unavailable
            ]
        }
        raise WorkflowRuntimeException(
            ErrorCodes.CAPABILITY_PREFLIGHT_UNAVAILABLE,
            "Required capabilities are unavailable: "
            + ", ".join(item.operation_id for item in unavailable),
            details=details,
        )

    @staticmethod
    def _build_locked_capability_prompt(state: _CapabilityPreflightState) -> str:
        if state.mode == "off":
            return ""
        lines = [
            "<locked_capabilities>",
            "The generated workflow must invoke every required locked capability occurrence exactly as listed.",
        ]
        for lock in state.locked:
            if not lock.entries:
                continue
            for entry in lock.entries:
                bindings = " ".join(
                    f"request {binding['path']}={json.dumps(binding['value'], ensure_ascii=False)}"
                    for binding in entry.request_bindings
                )
                lines.append(
                    f"- occurrence={lock.operation_id}::{entry.catalog_id} kind={entry.kind} "
                    f"server={entry.server or '-'} method={entry.method} {bindings}".rstrip()
                )
        for constraint in state.constraints:
            if isinstance(constraint, dict):
                lines.append(f"- constraint {constraint.get('id')}: {constraint.get('description')}")
        lines.append("</locked_capabilities>")
        return "\n".join(lines)

    def _validate_locked_capabilities(
        self, doc: WorkflowDocument, state: _CapabilityPreflightState
    ) -> None:
        if state.mode == "off":
            return
        invocations: list[tuple[str, str | None, str, Any, StepDef]] = []

        def walk(steps: list[StepDef]) -> None:
            for step in steps:
                if step.type == "mcp.call" and isinstance(step.input, dict):
                    methods = step.input.get("methods")
                    if isinstance(methods, list):
                        for method in methods:
                            invocations.append(("tool", step.input.get("server"), str(method), step.input.get("request"), step))
                    elif step.input.get("method") is not None:
                        invocations.append(
                            (
                                str(step.input.get("kind", "tool")),
                                step.input.get("server"),
                                str(step.input.get("method")),
                                step.input.get("request"),
                                step,
                            )
                        )
                else:
                    invocations.append(("native", None, step.type, step.input, step))
                walk(step.steps or [])
                for branch in step.branches or []:
                    walk(branch.steps)
                for case in step.cases or []:
                    walk(case.steps)
                walk(step.default or [])

        for workflow in doc.workflows.values():
            walk(workflow.steps)
            walk(workflow.finally_)

        remaining = list(invocations)
        omitted: list[str] = []
        for lock in state.locked:
            if not lock.required:
                continue
            for entry in lock.entries:
                index = next(
                    (
                        index
                        for index, invocation in enumerate(remaining)
                        if invocation[0] == entry.kind
                        and invocation[1] == entry.server
                        and invocation[2] == entry.method
                        and self._request_has_bindings(invocation[3], entry.request_bindings)
                    ),
                    None,
                )
                if index is None:
                    omitted.append(lock.operation_id)
                else:
                    remaining.pop(index)
        if omitted:
            raise WorkflowRuntimeException(
                ErrorCodes.CAPABILITY_PREFLIGHT_UNAVAILABLE,
                "Generated workflow omitted locked capability occurrences: " + ", ".join(omitted),
                details={"unavailable_capabilities": omitted},
            )

        confirmations = [
            index for index, invocation in enumerate(invocations)
            if invocation[4].type == "human.input"
            and isinstance(invocation[4].input, dict)
            and str(invocation[4].input.get("mode", "")).lower() == "confirm"
        ]
        write_entries = {
            (entry.server, entry.method)
            for lock in state.locked
            if lock.external_effect_kind == "write"
            for entry in lock.entries
        }
        writes = [
            index for index, invocation in enumerate(invocations)
            if (invocation[1], invocation[2]) in write_entries
        ]
        confirmation_policy = str(
            (state.inventory or {}).get("external_write_confirmation_policy", "unspecified")
        ).strip().lower()
        if confirmation_policy != "forbidden" and writes and not any(index < writes[0] for index in confirmations):
            raise WorkflowRuntimeException(
                ErrorCodes.CAPABILITY_PREFLIGHT_UNAVAILABLE,
                "Generated workflow omitted mandatory human confirmation before the first external write.",
            )
        self._validate_artifact_provenance(invocations, state)

    def _validate_artifact_provenance(
        self,
        invocations: list[tuple[str, str | None, str, Any, StepDef]],
        state: _CapabilityPreflightState,
    ) -> None:
        selected_entries = [entry for lock in state.locked for entry in lock.entries]
        contracts: dict[tuple[str | None, str], dict[str, Any]] = {}
        producers_by_kind: dict[str, list[_CapabilityCatalogEntry]] = {}
        consumers_by_kind: dict[str, list[_CapabilityCatalogEntry]] = {}
        for entry in selected_entries:
            contract = self._read_artifact_contract(entry.meta)
            if contract is None:
                continue
            contracts[(entry.server, entry.method)] = contract
            for producer in contract.get("produces", []) if isinstance(contract.get("produces"), list) else []:
                if isinstance(producer, dict) and isinstance(producer.get("kind"), str):
                    producers_by_kind.setdefault(producer["kind"], []).append(entry)
            for consumer in contract.get("consumes", []) if isinstance(contract.get("consumes"), list) else []:
                if isinstance(consumer, dict) and isinstance(consumer.get("kind"), str):
                    consumers_by_kind.setdefault(consumer["kind"], []).append(entry)

        redundant = [
            kind for kind, producers in producers_by_kind.items()
            if len(producers) > 1 and kind in consumers_by_kind
        ]
        if redundant:
            raise WorkflowRuntimeException(
                ErrorCodes.CAPABILITY_PREFLIGHT_REDUNDANT_ARTIFACT_PRODUCER,
                "Locked capability set contains redundant artifact producers: " + ", ".join(redundant),
                details={"artifact_kinds": redundant},
            )

        produced_steps: dict[str, list[tuple[int, str, str]]] = {}
        aliases: dict[str, str] = {}
        for index, invocation in enumerate(invocations):
            step = invocation[4]
            if step.type == "set" and isinstance(step.input, dict):
                for name, value in step.input.items():
                    if isinstance(value, str):
                        aliases[f"{step.id}.{name}"] = value
            contract = contracts.get((invocation[1], invocation[2]))
            if contract is None:
                continue
            for producer in contract.get("produces", []) if isinstance(contract.get("produces"), list) else []:
                if not isinstance(producer, dict) or not isinstance(producer.get("kind"), str):
                    continue
                pointer = str(producer.get("pointer", ""))
                path = ".".join(self._pointer_parts(pointer))
                produced_steps.setdefault(producer["kind"], []).append((index, step.id, path))
            for consumer in contract.get("consumes", []) if isinstance(contract.get("consumes"), list) else []:
                if not isinstance(consumer, dict) or not bool(consumer.get("required", True)):
                    continue
                kind = consumer.get("kind")
                pointer = consumer.get("pointer")
                if not isinstance(kind, str) or not isinstance(pointer, str):
                    continue
                value = self._read_pointer(invocation[3], pointer)
                sources = [source for source in produced_steps.get(kind, []) if source[0] < index]
                if not self._artifact_value_has_provenance(value, sources, aliases):
                    raise WorkflowRuntimeException(
                        ErrorCodes.CAPABILITY_PREFLIGHT_UNAVAILABLE,
                        f"Artifact consumer '{step.id}' has no proven upstream producer for '{kind}'.",
                        details={
                            "workflow": "main",
                            "step_id": step.id,
                            "artifact_kind": kind,
                            "request_pointer": pointer,
                        },
                    )

    @staticmethod
    def _read_artifact_contract(meta: Any) -> dict[str, Any] | None:
        if not isinstance(meta, dict):
            return None
        gnougo = meta.get("gnougo")
        if isinstance(gnougo, str):
            try:
                gnougo = json.loads(gnougo)
            except Exception:
                return None
        artifacts = gnougo.get("artifacts") if isinstance(gnougo, dict) else None
        return artifacts if isinstance(artifacts, dict) else None

    @staticmethod
    def _pointer_parts(pointer: str) -> list[str]:
        if not pointer.startswith("/"):
            return []
        return [part.replace("~1", "/").replace("~0", "~") for part in pointer[1:].split("/")]

    @classmethod
    def _read_pointer(cls, value: Any, pointer: str) -> Any:
        current = value
        for part in cls._pointer_parts(pointer):
            if not isinstance(current, dict) or part not in current:
                return None
            current = current[part]
        return current

    @staticmethod
    def _artifact_value_has_provenance(
        value: Any,
        sources: list[tuple[int, str, str]],
        aliases: dict[str, str],
    ) -> bool:
        if not isinstance(value, str):
            return False
        current = value
        visited: set[str] = set()
        while True:
            match = re.fullmatch(
                r"\$\{\s*data\.steps\.([A-Za-z_][A-Za-z0-9_-]*)(?:\.([A-Za-z_][A-Za-z0-9_.-]*))?\s*\}",
                current,
            )
            if not match:
                return False
            step_id = match.group(1)
            path = match.group(2) or ""
            for _index, producer_id, producer_path in sources:
                expected = f"response.{producer_path}" if producer_path else "response"
                if step_id == producer_id and path == expected:
                    return True
            alias_key = f"{step_id}.{path}"
            if alias_key in visited or alias_key not in aliases:
                return False
            visited.add(alias_key)
            current = aliases[alias_key]

    @staticmethod
    def _request_has_bindings(request: Any, bindings: list[dict[str, Any]]) -> bool:
        for binding in bindings:
            current = request
            for raw in binding["path"].lstrip("/").split("/"):
                name = raw.replace("~1", "/").replace("~0", "~")
                if not isinstance(current, dict) or name not in current:
                    return False
                current = current[name]
            if current != binding["value"]:
                return False
        return True

    @staticmethod
    def _attach_pipeline_capability_ownership(
        extraction: _WorkflowPipelineExtraction,
        state: _CapabilityPreflightState,
    ) -> None:
        """Attach deterministic preflight identities to the leaf owning each direct MCP call."""
        pending_locks = [lock for lock in state.locked if lock.entries]
        for spec in extraction.subworkflows:
            spec.catalog_ids = []
            spec.locked_operations = []
            for tool in spec.planned_tools:
                tool.catalog_ids = []
                tool.locked_operation_ids = []
                lock_index = next(
                    (
                        index
                        for index, lock in enumerate(pending_locks)
                        if any(
                            entry.kind == tool.kind
                            and entry.server == tool.server
                            and entry.method == tool.method
                            for entry in lock.entries
                        )
                    ),
                    None,
                )
                if lock_index is not None:
                    lock = pending_locks.pop(lock_index)
                    matching_entries = [
                        entry
                        for entry in lock.entries
                        if entry.kind == tool.kind
                        and entry.server == tool.server
                        and entry.method == tool.method
                    ]
                    tool.catalog_ids.extend(entry.catalog_id for entry in matching_entries)
                    tool.locked_operation_ids.append(lock.operation_id)
                elif state.mode != "off":
                    base = next(
                        (
                            entry
                            for entry in state.catalog
                            if entry.kind == tool.kind
                            and entry.server == tool.server
                            and entry.method == tool.method
                            and not entry.request_bindings
                        ),
                        None,
                    )
                    if base is not None:
                        tool.catalog_ids.append(base.catalog_id)

                for catalog_id in tool.catalog_ids:
                    if catalog_id not in spec.catalog_ids:
                        spec.catalog_ids.append(catalog_id)
                for operation_id in tool.locked_operation_ids:
                    if operation_id not in spec.locked_operations:
                        spec.locked_operations.append(operation_id)

    @staticmethod
    def _capability_preflight_metadata(state: _CapabilityPreflightState) -> dict[str, Any]:
        return {
            "mode": state.mode,
            "requirements": [
                {
                    "id": item.operation_id,
                    "description": item.description,
                    "required": item.required,
                    "match_status": item.match_status,
                    "catalog_ids": [entry.catalog_id for entry in item.entries],
                }
                for item in state.locked
            ],
            "constraints": copy.deepcopy(state.constraints),
            "catalog_count": len(state.catalog),
        }
