# GnOuGo repository instructions

- Use English.
- Keep components independently publishable, testable and deployable; use stable contracts and avoid circular dependencies.
- Preserve tenant isolation, encrypted sensitive data, approvals, workspace confinement and permission enforcement.
- Keep changes scoped, update affected documentation and report validation results and limitations.
- Keep builds warning-free; fix causes instead of blanket-disabling checks.

## Skills

Apply every relevant skill, including for related tests and documentation. Use `$skill-name` where supported; otherwise read the linked `SKILL.md` directly.

| Work | Required skill |
| --- | --- |
| Planning, TaskPlan, compilation, validation or scoped repair | [gnougo-planning](.agents/skills/gnougo-planning/SKILL.md) |
| C#, .NET services/libraries, MCP servers, persistence or packaging | [gnougo-dotnet](.agents/skills/gnougo-dotnet/SKILL.md) |
| ClientApp, UI, TypeScript, styles or Razor/Blazor views | [gnougo-frontend](.agents/skills/gnougo-frontend/SKILL.md) |

Use both technical skills for changes spanning backend and UI. Keep domain rules in skills, not duplicated here.
