# Changelog

All notable changes to this project are documented in this file.

## v0.11.2 - 2026-07-03

- fix: workflow.route (release) (56ee336)

## v0.11.1 - 2026-07-03

- fix: python lint (release) (91bd2d5)

## v0.11.0 - 2026-07-03

- feat: workflow.plan validation and pipeline mode (release) (#56) (fa611bf)

## v0.10.5 - 2026-06-16

- fix: enhance reprompt (#55) (release) (b5a27b8)

## v0.10.4 - 2026-06-15

- fix: plan was very slow  (#54) (release) (cb3dd17)

## v0.10.3 - 2026-06-14

- fix: workflow.route loosing telemetry (#53) (release) (183ed03)

## v0.10.2 - 2026-06-09

- feature: workflow route (#52) (release) (bab8e21)

## v0.10.1 - 2026-06-08

- fix: numversion display inside gnougo (#51) (release) (5a5f545)
- fix: doc update workflow.call (1f23345)

## v0.10.0 - 2026-06-07

- feat: agent more local (#50) (release) (fece944)

## v0.9.2 - 2026-06-04

- fix: workflow plan validation input/output (#49) (release) (38cb38a)

## v0.9.1 - 2026-06-03

- fix: enhance workflow.plan (#48) (release) (b682844)

## v0.9.0 - 2026-06-01

- feat: add version (release) (#47) (7551ff7)

## v0.8.8 - 2026-05-31

- fix: copilot more permissive (release) (#46) (16de581)

## v0.8.7 - 2026-05-31

- fix copilot: Update appsettings.json (release) (cbc3445)

## v0.8.6 - 2026-05-31

- chore: gnougo optimozarion (release) (#43) (be63b3e)

## v0.8.5 - 2026-05-28

- fix: python workspace (release) (5d3f2e1)

## v0.8.4 - 2026-05-28

- refactor: centralize workspace project (release) (#42) (a19b1dc)

## v0.8.3 - 2026-05-26

- fix: cmd mcp AOT compilation (release) (5f39dbd)

## v0.8.2 - 2026-05-25

- chore: mcp aot (#41) (release) (9949bf7)

## v0.8.1 - 2026-05-24

- chore: update libs (#40) (release) (1b93be5)
- fix (e587769)

## v0.8.0 - 2026-05-23

- feat(agent): add private_key and rename claude to antropic (#38) (release) (7a1fdad)
- fix: changelog include more history (08160d1)

## v0.7.2 - 2026-05-23

- fix(agent): oidc (#37) (release) (119d62e)

## v0.7.1 - 2026-05-21

- fix(python): package ruff (release) (db2f5d7)

## v0.7.0 - 2026-05-21

- feat(mcp): add claude provider (release) (#33) (d53a468)

## v0.6.7 - 2026-05-18

- Revise README for download metrics and libraries (#36) (9fe93c7)
- Add badges in the READMEs (#35) (4ad2c4d)

## v0.6.6 - 2026-05-11

- fix: copilot agent (release) (bb40a88)

## v0.6.5 - 2026-05-11

- fix(copilot): custom provider (release) (139fe99)

## v0.6.4 - 2026-05-11

- fix(copilot): skip model discovery (release) (4fb2e84)

## v0.6.3 - 2026-05-11

- fix(mcp): configurable call timeout (release) (ee2566f)

## v0.6.2 - 2026-05-11

- fix(gnouflow): add mode to pass proxies timeout (#32) (release) (954647d)

## v0.6.1 - 2026-05-09

- fix(gnougo): traces logs (#31) (release) (6feac06)
- fix: dev other comptuer (08513a0)

## v0.6.0 - 2026-05-08

- feat: GitHub copilot demo (#30) (release) (70b142f)

## v0.5.1 - 2026-05-05

- fix(flow): error expression (#29) (release) (043cd05)
- fix: ottel collector serialization (eb62d13)
- fix: Update README.md to explain how to run GnOuGo For mac (a1fe7d5)

## v0.5.0 - 2026-05-05

- feat(mcp): copilot add telemetry (#28) (release) (4d35eca)

## v0.4.10 - 2026-05-03

- fix: update model reference (release) (f58c7ec)
- fix winget manifest (601cf06)
- fix: install README.md (93eb4d3)

## v0.4.9 - 2026-05-03

- fix: packaging (release) (0aeefe2)

## v0.4.8 - 2026-05-03

- chore: winget and brew (release) (719356a)
- chore: update all NuGet and pnpm dependencies to latest NuGet: - Microsoft.EntityFrameworkCore.* + Microsoft.Data.Sqlite: 10.0.5 -> 10.0.7 - Microsoft.Extensions.*: 10.0.5 -> 10.0.7 - OpenTelemetry.*: 1.15.2 -> 1.15.3 (fixes CVEs GHSA-g94r-2vxg-569j, GHSA-4625-4j76-fww9, GHSA-mr8r-92fq-pj8p) - OpenTelemetry.Instrumentation.AspNetCore: 1.15.1 -> 1.15.2 - OpenTelemetry.Instrumentation.Http: 1.15.0 -> 1.15.1 - YamlDotNet: 17.0.1 -> 17.1.0 - Markdig: 1.1.2 -> 1.1.3 - System.CommandLine: 2.0.5 -> 2.0.7 - Microsoft.NET.Test.Sdk: 18.4.0 -> 18.5.1 - coverlet.collector: 8.0.1 -> 10.0.0 - Grpc.AspNetCore: 2.76.0 -> 2.80.0 - Microsoft.ML.OnnxRuntime: 1.24.4 -> 1.25.1 pnpm (all ClientApps): - vite: 7.3.2 -> 8.0.10 - typescript: 6.0.2 -> 6.0.3 - sass: 1.99.0 (unchanged) (8c8af8f)

## v0.4.7 - 2026-05-03

- Publish gnougo desktop release packages (#27) (release) (3ff74b9)

## v0.4.6 - 2026-05-02

- chore: update all NuGet and pnpm dependencies to latest (#25) (release) (11e4a5d)

## v0.4.5 - 2026-05-02

- fix(mpc): dotnet mcp cannot be trimmed (release) (1f3ce1c)

## v0.4.4 - 2026-05-02

- fix(agent): mcp not found (release) (393048b)

## v0.4.3 - 2026-05-02

- fix(agent): mcp browser not found (release) (6bcd0e3)
- fix(collector): add wwwroot to release (5b92b4d)

## v0.4.2 - 2026-05-02

- fix(agent): browser mcp crash at start (release) (12370a4)

## v0.4.1 - 2026-05-02

- fix(agent): publish browser mcp (#24) (release) (3fcedcb)
- doc: update main readme with mcp servers list (d4d86cd)

## v0.4.0 - 2026-05-02

- feat(models): models configuration (#23) (release) (2b6f4ea)

## v0.3.0 - 2026-05-01

- feat(python): invalid charatere to publish (#22) (release) (728dc2b)

## v0.2.0 - 2026-04-30

- feat(agent): add embedding (#21) (release) (89310e1)

## v0.1.2 - 2026-04-26

- Feat(agent): merge continue (#20) (release) (73e9a4f)

## v0.1.1 - 2026-04-20

- fea: continu to merge (#19) (release) (5e32f36)

## v0.1.0 - 2026-04-14

- feat(otel): add otel exe to github release (#18) (701b1e2)

## v0.0.1 - 2026-04-14

- feat(otel): add otel exe to github release (#17) (release) (71920f2)
- feat: switch to pnpm (#16) (532c55d)
- feat: merge lib together (#14) (dcf3f84)
- feat(mcp): add agent mcp (#13) (380dddd)
- feat(cmd): protection against traversal path (#11) (cc1e00d)
- feat(flow): mcp list multiple servers at once and update to sdk dotnet 10.105 (#10) (00cf1c6)
- feat: integrate OTEL exports into GnouGo.Agent and always load latest llm settings (#7) (eccfda1)
- fix: various (#4) (a9d844b)
- feature(agent): add flow to agent (#3) (1b79ef6)
- feature(flow): add a structured output (#1) (a7e480c)
- first GnouMit (763a5f7)
