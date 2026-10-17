# Workflow artifact for explicit review

Run: `approvedlive20261005a-amazon-1`; revision **5**.
Artifact hash: `e7fcd7d58f7c72481ced3432001224df54a35e4e2be506d0eeee461517808e4d`.
Candidate: `34bfd6040181cf789a5472506821c5a7f5e2e552`.
No execution has started. Approval is separate from successful compilation.

## Accepted requirements

- `search_amazon_fr`: Amazon.fr est utilisé pour rechercher l’article fourni par l’entrée query.
- `collect_up_to_three`: Au maximum les trois premiers produits trouvés sont listés et visités individuellement.
- `extract_product_fields`: Pour chaque produit visité, le nom, la description et le prix sont extraits lorsqu’ils sont observables; les CAPTCHA, prix ou données absents sont consignés explicitement sans invention.
- `save_xlsx`: Un classeur Excel est sauvegardé à workflows/schema-portability-20261002/approvedlive20261005a-amazon-1/products.xlsx.
- `close_browser`: Le navigateur est fermé même en cas d’échec.

## Actual TaskPlan

Page loops are sequential and bounded to 100 pages. Product visits are sequential and bounded to three. Each cursor must belong to the current complete snapshot; navigation invalidates it.

- `open_amazon_home_manifest` — GnOuGo.Browser.Mcp/browser_get_content: Ouvrir https://www.amazon.fr/ et créer un manifeste d’observation borné de la page initiale, sans cliquer automatiquement sur les bannières, dialogues ou contrôles.
  Input `url` ← "https://www.amazon.fr/"
  Input `format` ← "observation_pages"
  Input `selector` ← "body"
  Input `waitUntil` ← "domcontentloaded"
  Input `maxRecords` ← 200
  Input `timeoutMs` ← 20000
- `consume_home_observation_pages` — foreach: Consommer toutes les pages du manifeste d’observation initial avant toute interaction afin de disposer des contrôles réellement observés.
  Dependencies: ["open_amazon_home_manifest"]
  Required guard: and(equal(output:open_amazon_home_manifest.observationManifest.captureTruncated, false), equal(output:open_amazon_home_manifest.observationManifest.manifestTruncated, false))
  Iterate: output:open_amazon_home_manifest.observationManifest.pages
  body:
  - `read_home_observation_page` — GnOuGo.Browser.Mcp/browser_get_content: Lire une page gelée du manifeste initial avec le format d’origine observation_pages, sans navigation ni modification de limite.
    Input `format` ← "observation_pages"
    Input `cursor` ← item().cursor
  - Export `homeObservationPage` ← output:read_home_observation_page
- `interpret_home_controls` — transform / interpret: Interpréter seulement les enregistrements observés: détecter CAPTCHA/blocage, identifier un éventuel contrôle de consentement cookies autorisé avec son sélecteur observé, et identifier le champ de recherche avec son sélecteur observé. Ne pas inventer de sélecteur ou de libellé absent.
  Dependencies: ["consume_home_observation_pages"]
  Input `homeManifest` ← output:open_amazon_home_manifest.observationManifest
  Input `homeObservationPages` ← output:consume_home_observation_pages
  Result contract: `{"kind":"object","nullable":false,"items":null,"fields":[{"name":"captchaVisible","type":{"kind":"boolean","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"cookieConsentVisible","type":{"kind":"boolean","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"cookieConsentButtonSelector","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"cookieConsentButtonText","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"searchSelector","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"readyToSearchWithoutConsent","type":{"kind":"boolean","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"status","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null}]}`
- `accept_observed_cookie_dialog` — conditional: Si un dialogue cookies visible et un bouton d’acceptation autorisé ont été observés, cliquer ce contrôle observé puis réobserver; sinon ne rien faire. Aucun libellé ou sélecteur n’est codé en dur.
  Dependencies: ["interpret_home_controls"]
  Required guard: equal(output:interpret_home_controls.captchaVisible, false)
  Branch condition: equal(output:interpret_home_controls.cookieConsentVisible, true)
  body:
  - `click_observed_cookie_accept` — GnOuGo.Browser.Mcp/browser_click: Cliquer uniquement le bouton de consentement cookies précédemment observé et autorisé, en utilisant son sélecteur réel observé.
    Input `selector` ← output:interpret_home_controls.cookieConsentButtonSelector
    Input `waitUntil` ← "domcontentloaded"
    Input `timeoutMs` ← 8000
  - `post_cookie_home_manifest` — GnOuGo.Browser.Mcp/browser_get_content: Après le clic de consentement, créer un nouveau manifeste d’observation frais de la page courante; les curseurs précédents ne seront plus réutilisés.
    Dependencies: ["click_observed_cookie_accept"]
    Input `format` ← "observation_pages"
    Input `selector` ← "body"
    Input `maxRecords` ← 200
    Input `timeoutMs` ← 10000
  - `consume_post_cookie_home_pages` — foreach: Consommer toutes les pages du nouveau manifeste d’observation après consentement avant de rechercher.
    Dependencies: ["post_cookie_home_manifest"]
    Required guard: and(equal(output:post_cookie_home_manifest.observationManifest.captureTruncated, false), equal(output:post_cookie_home_manifest.observationManifest.manifestTruncated, false))
    Iterate: output:post_cookie_home_manifest.observationManifest.pages
    body:
    - `read_post_cookie_home_page` — GnOuGo.Browser.Mcp/browser_get_content: Lire une page gelée du nouveau manifeste après consentement avec son format d’origine observation_pages.
      Input `format` ← "observation_pages"
      Input `cursor` ← item().cursor
    - Export `postCookieHomeObservationPage` ← output:read_post_cookie_home_page
  otherwise:
- `observe_current_home_manifest` — GnOuGo.Browser.Mcp/browser_get_content: Observer fraîchement la page courante avant la recherche, que le consentement ait été cliqué ou non, afin d’utiliser uniquement des sélecteurs valides du DOM courant.
  Dependencies: ["accept_observed_cookie_dialog"]
  Input `format` ← "observation_pages"
  Input `selector` ← "body"
  Input `maxRecords` ← 200
  Input `timeoutMs` ← 10000
- `consume_current_home_pages` — foreach: Consommer entièrement le manifeste frais de la page courante avant de choisir le champ de recherche.
  Dependencies: ["observe_current_home_manifest"]
  Required guard: and(equal(output:observe_current_home_manifest.observationManifest.captureTruncated, false), equal(output:observe_current_home_manifest.observationManifest.manifestTruncated, false))
  Iterate: output:observe_current_home_manifest.observationManifest.pages
  body:
  - `read_current_home_page` — GnOuGo.Browser.Mcp/browser_get_content: Lire une page gelée du manifeste courant avec le format d’origine observation_pages.
    Input `format` ← "observation_pages"
    Input `cursor` ← item().cursor
  - Export `currentHomeObservationPage` ← output:read_current_home_page
- `extract_current_search_control` — transform / interpret: Extraire depuis l’observation fraîche le sélecteur réellement observé du champ de recherche Amazon.fr et un statut de blocage éventuel. Ne pas utiliser de sélecteur supposé.
  Dependencies: ["consume_current_home_pages"]
  Input `currentManifest` ← output:observe_current_home_manifest.observationManifest
  Input `currentHomeObservationPages` ← output:consume_current_home_pages
  Result contract: `{"kind":"object","nullable":false,"items":null,"fields":[{"name":"searchSelector","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"captchaVisible","type":{"kind":"boolean","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"readyToSearch","type":{"kind":"boolean","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"status","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null}]}`
- `fill_and_submit_search` — GnOuGo.Browser.Mcp/browser_fill: Saisir exactement l’entrée publique query dans le champ de recherche observé et lancer la recherche.
  Dependencies: ["extract_current_search_control"]
  Required guard: and(equal(output:extract_current_search_control.readyToSearch, true), equal(output:extract_current_search_control.captchaVisible, false))
  Input `selector` ← output:extract_current_search_control.searchSelector
  Input `value` ← input:query
  Input `submit` ← true
  Input `timeoutMs` ← 15000
- `wait_for_search_results` — GnOuGo.Browser.Mcp/browser_wait: Attendre brièvement le rendu de la page de résultats après soumission, sans interaction cachée.
  Dependencies: ["fill_and_submit_search"]
  Input `delayMs` ← 2000
  Input `timeoutMs` ← 15000
- `search_results_manifest` — GnOuGo.Browser.Mcp/browser_get_content: Créer un manifeste borné d’observation de la page de résultats complète visible, sans supposer de conteneur CSS spécifique.
  Dependencies: ["wait_for_search_results"]
  Input `format` ← "observation_pages"
  Input `selector` ← "body"
  Input `maxRecords` ← 200
  Input `timeoutMs` ← 15000
- `consume_search_observation_pages` — foreach: Consommer chaque curseur du manifeste de résultats avec le format d’origine observation_pages avant toute navigation produit.
  Dependencies: ["search_results_manifest"]
  Required guard: and(equal(output:search_results_manifest.observationManifest.captureTruncated, false), equal(output:search_results_manifest.observationManifest.manifestTruncated, false))
  Iterate: output:search_results_manifest.observationManifest.pages
  body:
  - `read_search_observation_page` — GnOuGo.Browser.Mcp/browser_get_content: Lire une page gelée du manifeste de résultats avec son format d’origine observation_pages, sans URL, sélecteur ou limite remplacée.
    Input `format` ← "observation_pages"
    Input `cursor` ← item().cursor
  - Export `searchObservationPage` ← output:read_search_observation_page
- `extract_first_three_product_links` — transform / interpret: À partir des enregistrements observés de résultats consommés, extraire au maximum les trois premiers liens produits Amazon.fr dans l’ordre observé, avec nom observé et URL résolue. Signaler CAPTCHA, blocage, absence de résultats ou incomplétude sans inventer de lien.
  Dependencies: ["consume_search_observation_pages"]
  Input `query` ← input:query
  Input `searchManifest` ← output:search_results_manifest.observationManifest
  Input `searchObservationPages` ← output:consume_search_observation_pages
  Input `searchPageUrl` ← output:search_results_manifest.url
  Result contract: `{"kind":"object","nullable":false,"items":null,"fields":[{"name":"readyToVisitProducts","type":{"kind":"boolean","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"captchaOrBlocker","type":{"kind":"boolean","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"searchStatus","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"productLinks","type":{"kind":"array","nullable":false,"items":{"kind":"object","nullable":false,"items":null,"fields":[{"name":"productUrl","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"observedName","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"position","type":{"kind":"integer","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"status","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null}]},"fields":[]},"required":true,"default":null}]}`
- `visit_product_pages` — foreach: Visiter individuellement chaque URL des trois premiers produits au maximum, dans l’ordre, puis consommer intégralement l’observation de chaque page produit avant extraction.
  Dependencies: ["extract_first_three_product_links"]
  Required guard: and(equal(output:extract_first_three_product_links.readyToVisitProducts, true), equal(output:extract_first_three_product_links.captchaOrBlocker, false))
  Iterate: output:extract_first_three_product_links.productLinks
  body:
  - `open_product_manifest` — GnOuGo.Browser.Mcp/browser_get_content: Ouvrir l’URL produit observée dans les résultats et créer un manifeste d’observation borné de la page produit.
    Input `url` ← item().productUrl
    Input `format` ← "observation_pages"
    Input `selector` ← "body"
    Input `waitUntil` ← "domcontentloaded"
    Input `maxRecords` ← 200
    Input `timeoutMs` ← 20000
  - `consume_product_observation_pages` — foreach: Consommer toutes les pages du manifeste produit avant d’extraire les champs du produit visité.
    Dependencies: ["open_product_manifest"]
    Required guard: and(equal(output:open_product_manifest.observationManifest.captureTruncated, false), equal(output:open_product_manifest.observationManifest.manifestTruncated, false))
    Iterate: output:open_product_manifest.observationManifest.pages
    body:
    - `read_product_observation_page` — GnOuGo.Browser.Mcp/browser_get_content: Lire une page gelée du manifeste produit avec le format d’origine observation_pages, sans navigation ni limite remplacée.
      Input `format` ← "observation_pages"
      Input `cursor` ← item().cursor
    - Export `productObservationPage` ← output:read_product_observation_page
  - `extract_one_product_compact` — transform / interpret: Extraire indépendamment, depuis les enregistrements observés complets de cette page produit, le nom, la description et le prix réellement observables. Détecter CAPTCHA ou champ requis absent/incomplet; ne jamais inventer de valeur ni créer de ligne produit de remplacement.
    Dependencies: ["consume_product_observation_pages"]
    Input `searchItem` ← item()
    Input `productManifest` ← output:open_product_manifest.observationManifest
    Input `productObservationPages` ← output:consume_product_observation_pages
    Input `resolvedProductUrl` ← output:open_product_manifest.url
    Input `pageTitle` ← output:open_product_manifest.title
    Result contract: `{"kind":"object","nullable":false,"items":null,"fields":[{"name":"name","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"description","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"price","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"status","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"completeForPublication","type":{"kind":"boolean","nullable":false,"items":null,"fields":[]},"required":true,"default":null}]}`
  - Export `product` ← output:extract_one_product_compact
- `assemble_products_and_tsv` — transform / interpret: Assembler uniquement les produits visités dont les champs requis nom, description et prix sont complets et observés; si CAPTCHA, blocage, observation incomplète ou champ requis absent est détecté, définir readyToPublish=false et expliquer le statut, sans publier de ligne inventée. Produire un TSV normalisé pour XLSX seulement quand tous les produits visités sont publiables.
  Dependencies: ["visit_product_pages"]
  Input `query` ← input:query
  Input `searchExtraction` ← output:extract_first_three_product_links
  Input `visitedProducts` ← output:visit_product_pages
  Input `targetPath` ← "workflows/schema-portability-20261002/approvedlive20261005a-amazon-1/products.xlsx"
  Result contract: `{"kind":"object","nullable":false,"items":null,"fields":[{"name":"readyToPublish","type":{"kind":"boolean","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"publicationStatus","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"products","type":{"kind":"array","nullable":false,"items":{"kind":"object","nullable":false,"items":null,"fields":[{"name":"name","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"description","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"price","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"status","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null}]},"fields":[]},"required":true,"default":null},{"name":"tsv","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"filePath","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null}]}`
- `write_products_xlsx` — GnOuGo.Document.Mcp/document_write: Créer le classeur Excel demandé à l’emplacement relatif autorisé uniquement si les données obligatoires observées sont complètes et publiables.
  Dependencies: ["assemble_products_and_tsv"]
  Required guard: equal(output:assemble_products_and_tsv.readyToPublish, true)
  Input `filePath` ← "workflows/schema-portability-20261002/approvedlive20261005a-amazon-1/products.xlsx"
  Input `content` ← output:assemble_products_and_tsv.tsv
  Input `append` ← false
  Input `encoding` ← "utf-8"
- `verify_products_xlsx` — GnOuGo.Document.Mcp/document_read: Relire le classeur sauvegardé pour vérifier que l’artifact existe et reste lisible avant publication des sorties.
  Dependencies: ["write_products_xlsx"]
  Required guard: output:write_products_xlsx.success
  Input `filePath` ← "workflows/schema-portability-20261002/approvedlive20261005a-amazon-1/products.xlsx"
  Input `format` ← "plain"
- FINALIZER `close_browser_always` — GnOuGo.Browser.Mcp/browser_close: Fermer la page et le contexte du navigateur même si une étape précédente échoue.
- Export `filePath` ← output:write_products_xlsx.filePath
- Export `products` ← output:assemble_products_and_tsv.products

## Review boundary

This proposal uses explicit interpret-mode transforms for control classification, product selection, field extraction and TSV assembly. It does not use mapping.dynamic for those steps. They receive complete compact observation collections subject to the unchanged inference admission limit; a request exceeding that limit must fail, not truncate data. These model outputs do not prove source grounding or completeness; the independent oracle still checks actual product visits and observed XLSX values.

All runtime inference, permissions and budgets remain bounded. Unknown external completion blocks replay and cleanup. The independent execution oracle must verify actual observations, commands and durable artifacts; model claims and compilation alone do not establish success.

## Disposition

The user explicitly acknowledged all five requirements and approved this exact revision/hash before execution. The execution then failed the unchanged input admission gate; see [retained execution](amazon-new-execution.json). This document is the review snapshot, not a success claim.
