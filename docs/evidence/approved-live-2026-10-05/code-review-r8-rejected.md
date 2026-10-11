# Workflow artifact for explicit review

Run: `approvedlive20261005a-code-1`; revision **8**.
Artifact hash: `7b8374605b73261f6c1067b4ebca73ceaf845cad90d3669cdc1759c88869eb38`.
Candidate: `34bfd6040181cf789a5472506821c5a7f5e2e552`.
No execution has started. Approval is separate from successful compilation.

## Accepted requirements

- `clone_unique`: Cloner une seule fois le projet dans workflows/schema-portability-20261002/approvedlive20261005a-code-1/repository et utiliser uniquement ce répertoire pour les opérations projet.
- `versions_checked`: Vérifier explicitement node --version, pnpm --version et python --version avant les checks, avec commandes et codes de sortie observés.
- `checks_declared`: Installer les dépendances et exécuter les linters, tests unitaires et tests d’intégration présents/déclarés par le dépôt, sans présenter une capacité manquante ou un prérequis indisponible comme un succès.
- `local_review`: Réaliser la revue de code demandée sur la PR head f2f6bda0e02a6c5c363e88cd6cb5d4eb7c313488 contre base 8f1779dc25c1843e9ce4d4ec76d02e066ae892c4 avec commentaires de diff locaux uniquement.
- `evidence_report`: Sauvegarder dans workflows/schema-portability-20261002/approvedlive20261005a-code-1/review.json la décision, les findings, les commandes exactes exécutées et leurs codes de sortie observés.
- `cleanup_always`: Supprimer le répertoire cloné quoi qu’il arrive, y compris en cas d’échec, après préservation du rapport de preuves.

## Actual TaskPlan

- `clone_pr_head` — GnOuGo.Git.Mcp/git_clone: Cloner une seule fois le dépôt SmartGuide dans le répertoire imposé, en checkout détaché sur le head de la PR et avec l’historique nécessaire pour comparer avec la base. Utiliser ce répertoire comme unique répertoire projet.
  Required guard: and(equal(input:pullRequestUrl, "https://github.com/AxaFrance/SmartGuide/pull/610"), not_equal(input:reviewText, ""))
  Input `remoteUrl` ← "https://github.com/AxaFrance/SmartGuide.git"
  Input `targetDirectory` ← "workflows/schema-portability-20261002/approvedlive20261005a-code-1/repository"
  Input `branch` ← "f2f6bda0e02a6c5c363e88cd6cb5d4eb7c313488"
  Input `fetchAllBranches` ← true
  Input `historyDepth` ← 0
  Input `tagFetchMode` ← "none"
- `compare_pr_diff_page_1` — GnOuGo.Git.Mcp/git_compare_refs: Obtenir la première page bornée des patches exacts fichier par fichier de la PR head f2f6bda0e02a6c5c363e88cd6cb5d4eb7c313488 contre base 8f1779dc25c1843e9ce4d4ec76d02e066ae892c4. Ne considérer la couverture complète que si cette page n’a pas de continuation.
  Dependencies: ["clone_pr_head"]
  Required guard: output:clone_pr_head.success
  Input `projectRoot` ← output:clone_pr_head.projectRootRelative
  Input `baseRef` ← "8f1779dc25c1843e9ce4d4ec76d02e066ae892c4"
  Input `headRef` ← "f2f6bda0e02a6c5c363e88cd6cb5d4eb7c313488"
  Input `compareFromMergeBase` ← true
  Input `cursor` ← null
  Input `pageSize` ← 100
- `copilot_checks` — GnOuGo.GithubCopilot.Mcp/code_agent_edit: Via GitHub Copilot, dans l’unique répertoire cloné, inspecter les toolchains déclarées, vérifier explicitement node --version, pnpm --version et python --version, installer les dépendances, exécuter les linters, tests unitaires et tests d’intégration présents/déclarés, puis produire un résumé structuré sans publier sur GitHub. Les prérequis ou capacités manquants et les échecs doivent rester explicites et ne jamais être présentés comme des succès.
  Dependencies: ["clone_pr_head"]
  Required guard: output:clone_pr_head.success
  Input `projectRoot` ← output:clone_pr_head.projectRootRelative
  Input `contextFiles` ← null
  Input `task` ← "Travail local uniquement, sans publication GitHub et sans extension de permission/sandbox. Utiliser uniquement ce répertoire projet déjà cloné. 1) Inspecter les fichiers de configuration du dépôt pour identifier les toolchains et checks déclarés. 2) Exécuter explicitement, avant tout check: node --version, pnpm --version, python --version. 3) Installer les dépendances selon les déclarations du dépôt. 4) Exécuter les linters, tests unitaires et tests d’intégration présents/déclarés. 5) Capturer pour chaque commande le texte exact de la commande, le cwd et le code de sortie observé. Si une commande, capacité, outil ou prérequis est indisponible, le mentionner explicitement comme indisponible/échoué, pas comme un succès. Ne pas publier de commentaire ni modifier GitHub. Retourner un JSON textuel contenant commands:[{command,cwd,exitCode}], unavailable:[...], failed:[...], checksSummary et toute observation utile pour la revue."
- `copilot_review_complete_diff` — GnOuGo.GithubCopilot.Mcp/copilot_review: Réaliser une revue de code locale via GitHub Copilot uniquement lorsque la page de diff couvre toute la PR. Utiliser les patches exacts, les résultats de commandes observés, le texte de review demandé et l’interdiction de publication GitHub.
  Dependencies: ["compare_pr_diff_page_1", "copilot_checks"]
  Required guard: and(output:compare_pr_diff_page_1.success, equal(output:compare_pr_diff_page_1.hasMore, false))
  Input `projectRoot` ← output:clone_pr_head.projectRootRelative
  Input `baseSha` ← output:compare_pr_diff_page_1.baseSha
  Input `headSha` ← output:compare_pr_diff_page_1.headSha
  Input `filesJson` ← output:compare_pr_diff_page_1.filesJson
  Input `existingCommentsJson` ← null
  Input `reviewInstructions` ← input:reviewText
  Input `runtimeContextJson` ← json({pullRequestUrl: input:pullRequestUrl, requiredHead: "f2f6bda0e02a6c5c363e88cd6cb5d4eb7c313488", requiredBase: "8f1779dc25c1843e9ce4d4ec76d02e066ae892c4", localOnly: true, checksCopilotOutput: output:copilot_checks.output, checksToolExecutions: output:copilot_checks.toolExecutions})
  Input `maxBatchCharacters` ← 60000
- `assemble_normal_review_json` — transform / interpret: Assembler le rapport final normal depuis les observations complètes disponibles: décision approve seulement si la revue est complète, sans finding bloquant et sans échec/indisponibilité de commande requise; sinon request_changes. Copier les commandes/codes de sortie observés depuis les exécutions Copilot, sans inventer de commandes ni de codes. Inclure explicitement reportPath avec le chemin imposé.
  Dependencies: ["copilot_checks", "copilot_review_complete_diff"]
  Required guard: and(output:copilot_checks.success, output:copilot_review_complete_diff.complete)
  Input `checkOutput` ← output:copilot_checks.output
  Input `checkToolExecutions` ← output:copilot_checks.toolExecutions
  Input `reviewFindings` ← output:copilot_review_complete_diff.findings
  Input `blockingFindingCount` ← output:copilot_review_complete_diff.blockingFindingCount
  Input `reviewComplete` ← output:copilot_review_complete_diff.complete
  Input `reviewCoverage` ← output:copilot_review_complete_diff.coverage
  Input `reviewSummary` ← output:copilot_review_complete_diff.summary
  Input `reportPath` ← "workflows/schema-portability-20261002/approvedlive20261005a-code-1/review.json"
  Result contract: `{"kind":"object","nullable":false,"items":null,"fields":[{"name":"reviewJson","type":{"kind":"object","nullable":false,"items":null,"fields":[{"name":"decision","type":{"kind":"string","nullable":false,"enum":["approve","request_changes"],"items":null,"fields":[]},"required":true,"default":null},{"name":"findings","type":{"kind":"array","nullable":false,"items":{"kind":"object","nullable":false,"items":null,"fields":[{"name":"severity","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"file","type":{"kind":"string","nullable":true,"items":null,"fields":[]},"required":true,"default":null},{"name":"line","type":{"kind":"integer","nullable":true,"items":null,"fields":[]},"required":true,"default":null},{"name":"comment","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null}]},"fields":[]},"required":true,"default":null},{"name":"commands","type":{"kind":"array","nullable":false,"items":{"kind":"object","nullable":false,"items":null,"fields":[{"name":"command","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"exitCode","type":{"kind":"integer","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"cwd","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null}]},"fields":[]},"required":true,"default":null},{"name":"reportPath","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null}]},"required":true,"default":null}]}`
- FINALIZER `finalize_and_cleanup` — sequence: Toujours préserver un rapport review.json véridique à partir des observations disponibles, puis nettoyer le clone dans le nested always. Les branches utilisent des identifiants de producteurs globalement uniques et exportent localement le même nom métier report_for_write.
  body:
  - `branch_use_normal_report` — conditional: Si le rapport normal existe, l’exporter sous le nom métier local report_for_write sans relire de résultat absent.
    Branch condition: present:assemble_normal_review_json
    body:
    - `normal_report_exporter` — value: Exporter le rapport normal déjà assemblé pour l’écriture finale.
    - Export `report_for_write` ← output:normal_report_exporter.report_for_write
    otherwise:
    - Export `report_for_write` ← output:assemble_normal_review_json.reviewJson
  - `branch_clone_failure_report` — conditional: Si le clone n’a pas produit de résultat présent, créer un rapport d’échec local explicite sans lire de ports absents.
    Branch condition: not(present:clone_pr_head)
    body:
    - `clone_absent_failure_report_builder` — value: Assembler un rapport d’échec indiquant que le clone requis n’a pas abouti et qu’aucune commande projet vérifiée n’est disponible.
    - Export `report_for_write` ← output:clone_absent_failure_report_builder.report_for_write
    otherwise:
    - Export `report_for_write` ← {decision: "request_changes", findings: [{severity: "error", file: null, line: null, comment: "Échec de revue: le résultat du clone n'est pas présent; aucun port du clone absent n'a été lu et la revue complète ne peut pas être attestée."}], commands: [], reportPath: "workflows/schema-portability-20261002/approvedlive20261005a-code-1/review.json"}
  - `branch_diff_incomplete_report` — conditional: Si la comparaison de diff est présente mais paginée avec continuation, bloquer la revue complète et produire un rapport d’échec explicite sans prétendre à une couverture complète.
    Branch condition: and(present:compare_pr_diff_page_1, output:compare_pr_diff_page_1.hasMore)
    body:
    - `diff_incomplete_report_builder` — transform / interpret: Assembler un rapport request_changes indiquant que la diff est incomplète à cause d’un nextCursor non consommé, en conservant les observations de checks si présentes et sans inventer de findings de revue complète.
      Input `diffHasMore` ← output:compare_pr_diff_page_1.hasMore
      Input `diffNextCursor` ← output:compare_pr_diff_page_1.nextCursor
      Input `diffTotalFiles` ← output:compare_pr_diff_page_1.totalFiles
      Input `checksPresent` ← present:copilot_checks
      Input `reportPath` ← "workflows/schema-portability-20261002/approvedlive20261005a-code-1/review.json"
      Result contract: `{"kind":"object","nullable":false,"items":null,"fields":[{"name":"report_for_write","type":{"kind":"object","nullable":false,"items":null,"fields":[{"name":"decision","type":{"kind":"string","nullable":false,"enum":["request_changes"],"items":null,"fields":[]},"required":true,"default":null},{"name":"findings","type":{"kind":"array","nullable":false,"items":{"kind":"object","nullable":false,"items":null,"fields":[{"name":"severity","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"file","type":{"kind":"string","nullable":true,"items":null,"fields":[]},"required":true,"default":null},{"name":"line","type":{"kind":"integer","nullable":true,"items":null,"fields":[]},"required":true,"default":null},{"name":"comment","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null}]},"fields":[]},"required":true,"default":null},{"name":"commands","type":{"kind":"array","nullable":false,"items":{"kind":"object","nullable":false,"items":null,"fields":[{"name":"command","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"exitCode","type":{"kind":"integer","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"cwd","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null}]},"fields":[]},"required":true,"default":null},{"name":"reportPath","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null}]},"required":true,"default":null}]}`
    - Export `report_for_write` ← output:diff_incomplete_report_builder.report_for_write
    otherwise:
    - Export `report_for_write` ← {decision: "request_changes", findings: [{severity: "error", file: null, line: null, comment: "Échec de revue: la comparaison de diff indique une continuation de pagination; la couverture complète de la PR n'est pas attestée et la revue locale ne doit pas être présentée comme complète."}], commands: [], reportPath: "workflows/schema-portability-20261002/approvedlive20261005a-code-1/review.json"}
  - `branch_review_or_checks_failure_report` — conditional: Si le clone et la comparaison non paginée existent mais que le rapport normal n’existe pas, produire un rapport d’échec à partir des observations disponibles avant tout nettoyage.
    Branch condition: and(present:clone_pr_head, present:compare_pr_diff_page_1)
    body:
    otherwise:
  - `failure_report_when_clone_and_diff_present` — conditional: Créer un rapport d’échec lorsque clone et première page de diff sont présents, que la diff n’est pas paginée, mais que le rapport normal est absent à cause d’échec de checks, de revue, de capacité ou de complétude vérifiée.
    Branch condition: and(present:clone_pr_head, present:compare_pr_diff_page_1)
    body:
    otherwise:
  - `guard_compare_present_for_failure_report` — conditional: Branche explicite de présence pour les résultats de comparaison avant lecture de ses ports dans le rapport d’échec général.
    Branch condition: present:compare_pr_diff_page_1
    body:
    - `failure_when_no_normal_report_builder` — conditional: Si le rapport normal est absent et qu’il n’y a pas de continuation de diff, assembler un rapport request_changes depuis les observations présentes disponibles.
      Branch condition: and(not(present:assemble_normal_review_json), equal(output:compare_pr_diff_page_1.hasMore, false))
      body:
      - `general_failure_report_builder` — transform / interpret: Assembler un rapport d’échec véridique: request_changes, findings expliquant les étapes non établies, commandes observées seulement si elles sont présentes, et reportPath exact. Ne pas lire de résultat absent hors de cette branche de présence.
        Input `checksPresent` ← present:copilot_checks
        Input `reviewPresent` ← present:copilot_review_complete_diff
        Input `normalReportPresent` ← present:assemble_normal_review_json
        Input `diffTotalFiles` ← output:compare_pr_diff_page_1.totalFiles
        Input `reportPath` ← "workflows/schema-portability-20261002/approvedlive20261005a-code-1/review.json"
        Result contract: `{"kind":"object","nullable":false,"items":null,"fields":[{"name":"report_for_write","type":{"kind":"object","nullable":false,"items":null,"fields":[{"name":"decision","type":{"kind":"string","nullable":false,"enum":["request_changes"],"items":null,"fields":[]},"required":true,"default":null},{"name":"findings","type":{"kind":"array","nullable":false,"items":{"kind":"object","nullable":false,"items":null,"fields":[{"name":"severity","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"file","type":{"kind":"string","nullable":true,"items":null,"fields":[]},"required":true,"default":null},{"name":"line","type":{"kind":"integer","nullable":true,"items":null,"fields":[]},"required":true,"default":null},{"name":"comment","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null}]},"fields":[]},"required":true,"default":null},{"name":"commands","type":{"kind":"array","nullable":false,"items":{"kind":"object","nullable":false,"items":null,"fields":[{"name":"command","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"exitCode","type":{"kind":"integer","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"cwd","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null}]},"fields":[]},"required":true,"default":null},{"name":"reportPath","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null}]},"required":true,"default":null}]}`
      - Export `report_for_write` ← output:general_failure_report_builder.report_for_write
      otherwise:
      - Export `report_for_write` ← {decision: "request_changes", findings: [{severity: "error", file: null, line: null, comment: "Échec de revue: le rapport normal est absent alors que la première page de diff présente n'indique pas de continuation; au moins une étape de checks, de revue, de capacité ou de complétude n'a pas produit de résultat normal vérifié."}], commands: [], reportPath: "workflows/schema-portability-20261002/approvedlive20261005a-code-1/review.json"}
    - Export `report_for_write` ← output:failure_when_no_normal_report_builder.report_for_write
    otherwise:
    - Export `report_for_write` ← {decision: "request_changes", findings: [{severity: "error", file: null, line: null, comment: "Échec de revue: aucun résultat de comparaison de diff n'est présent; les ports de comparaison absents n'ont pas été lus et la couverture de la PR ne peut pas être attestée."}], commands: [], reportPath: "workflows/schema-portability-20261002/approvedlive20261005a-code-1/review.json"}
  - `select_report_for_write` — transform / interpret: Sélectionner le rapport disponible parmi les exports métier report_for_write produits par les branches précédentes, avec priorité au rapport normal, sinon rapport d’échec véridique. Ne pas inventer de données; si aucun rapport n’est disponible, produire un rapport minimal request_changes indiquant l’absence de résultats exploitables.
    Dependencies: ["branch_use_normal_report", "branch_clone_failure_report", "branch_diff_incomplete_report", "guard_compare_present_for_failure_report"]
    Input `normalBranchPresent` ← present:branch_use_normal_report
    Input `cloneFailureBranchPresent` ← present:branch_clone_failure_report
    Input `diffIncompleteBranchPresent` ← present:branch_diff_incomplete_report
    Input `generalFailureBranchPresent` ← present:guard_compare_present_for_failure_report
    Input `reportPath` ← "workflows/schema-portability-20261002/approvedlive20261005a-code-1/review.json"
    Result contract: `{"kind":"object","nullable":false,"items":null,"fields":[{"name":"reviewJson","type":{"kind":"object","nullable":false,"items":null,"fields":[{"name":"decision","type":{"kind":"string","nullable":false,"enum":["approve","request_changes"],"items":null,"fields":[]},"required":true,"default":null},{"name":"findings","type":{"kind":"array","nullable":false,"items":{"kind":"object","nullable":false,"items":null,"fields":[{"name":"severity","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"file","type":{"kind":"string","nullable":true,"items":null,"fields":[]},"required":true,"default":null},{"name":"line","type":{"kind":"integer","nullable":true,"items":null,"fields":[]},"required":true,"default":null},{"name":"comment","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null}]},"fields":[]},"required":true,"default":null},{"name":"commands","type":{"kind":"array","nullable":false,"items":{"kind":"object","nullable":false,"items":null,"fields":[{"name":"command","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"exitCode","type":{"kind":"integer","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"cwd","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null}]},"fields":[]},"required":true,"default":null},{"name":"reportPath","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null}]},"required":true,"default":null}]}`
  - `write_preserved_review_json` — GnOuGo.Document.Mcp/document_write: Écrire le rapport de preuves local review.json à l’emplacement imposé, hors du répertoire cloné afin qu’il survive au nettoyage. Utiliser le contrat d’écriture de document texte/JSON et la sérialisation JSON déterministe; ne pas produire ni inventer de base64.
    Dependencies: ["select_report_for_write"]
    Required guard: present:select_report_for_write
    Input `filePath` ← "workflows/schema-portability-20261002/approvedlive20261005a-code-1/review.json"
    Input `content` ← json(output:select_report_for_write.reviewJson)
    Input `append` ← false
    Input `encoding` ← "utf-8"
  - `read_preserved_review_json` — GnOuGo.Document.Mcp/document_read: Relire le rapport review.json préservé pour établir l’évidence de contenu avant nettoyage du clone.
    Dependencies: ["write_preserved_review_json"]
    Required guard: output:write_preserved_review_json.success
    Input `filePath` ← "workflows/schema-portability-20261002/approvedlive20261005a-code-1/review.json"
    Input `format` ← "plain"
  - FINALIZER `cleanup_clone_after_preservation` — GnOuGo.Cmd.Mcp/cmd_run: Supprimer le répertoire cloné imposé quoi qu’il arrive après la tentative de préservation du rapport, y compris après échec partiel. Le rapport est hors du clone.
    Dependencies: ["write_preserved_review_json"]
    Input `commandName` ← "delete_directory"
    Input `parameters` ← {path: "workflows/schema-portability-20261002/approvedlive20261005a-code-1/repository"}
    Input `timeoutMs` ← 300000
  - Export `reviewJson` ← output:select_report_for_write.reviewJson
- Export `reviewJson` ← output:assemble_normal_review_json.reviewJson

## Review boundary

All runtime inference, permissions and budgets remain bounded. Unknown external completion blocks replay and cleanup. The independent execution oracle must verify actual observations, commands and durable artifacts; model claims and compilation alone do not establish success.

## Disposition

Rejected during business review: report selection had presence flags without report payloads and attempted an absent-result read. This is a retained intermediate proposal, not the current executable artifact.
