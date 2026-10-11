# Workflow artifact for explicit review

Run: `approvedlive20261005a-amazon-1`; revision **3**.
Artifact hash: `8b2c71f08af8cb5d301ba9776dcfe6a2324b0e9596d65de57107c7af04782e42`.
Candidate: `34bfd6040181cf789a5472506821c5a7f5e2e552`.
No execution has started. Approval is separate from successful compilation.

## Accepted requirements

- `search_amazon_fr`: Amazon.fr est utilisé pour rechercher l’article fourni par l’entrée query.
- `collect_up_to_three`: Au maximum les trois premiers produits trouvés sont listés et visités individuellement.
- `extract_product_fields`: Pour chaque produit visité, le nom, la description et le prix sont extraits lorsqu’ils sont observables; les CAPTCHA, prix ou données absents sont consignés explicitement sans invention.
- `save_xlsx`: Un classeur Excel est sauvegardé à workflows/schema-portability-20261002/approvedlive20261005a-amazon-1/products.xlsx.
- `close_browser`: Le navigateur est fermé même en cas d’échec.

## Actual TaskPlan

- `open_amazon_fr` — GnOuGo.Browser.Mcp/browser_get_content: Ouvrir Amazon.fr et observer la page initiale sans cliquer automatiquement sur les bannières ou dialogues.
  Input `url` ← "https://www.amazon.fr/"
  Input `format` ← "html"
  Input `waitUntil` ← "domcontentloaded"
  Input `maxCharacters` ← 50000
- `interpret_home_state` — transform / interpret: Interpréter uniquement les contrôles observés sur Amazon.fr: détecter si une bannière de cookies visible bloque la recherche, identifier le champ de recherche Amazon connu lorsqu’il est présent, et signaler tout CAPTCHA observé. Ne pas inventer de fait absent de l’observation.
  Dependencies: ["open_amazon_fr"]
  Input `homeHtml` ← output:open_amazon_fr.content
  Input `homeUrl` ← output:open_amazon_fr.url
  Result contract: `{"kind":"object","nullable":false,"items":null,"fields":[{"name":"cookieConsentVisible","type":{"kind":"boolean","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"captchaVisible","type":{"kind":"boolean","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"searchSelector","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"status","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null}]}`
- `accept_observed_cookie_dialog` — conditional: Si une bannière de cookies Amazon visible est observée, cliquer sur le bouton d’acceptation afin de débloquer la recherche, puis réobserver la page.
  Dependencies: ["interpret_home_state"]
  Branch condition: equal(output:interpret_home_state.cookieConsentVisible, true)
  body:
  - `click_accept_cookies` — GnOuGo.Browser.Mcp/browser_click_text: Cliquer seulement sur un contrôle de consentement cookies visible dont le libellé contient 'Accepter'.
    Input `text` ← "Accepter"
    Input `exact` ← false
    Input `waitUntil` ← "domcontentloaded"
    Input `timeoutMs` ← 5000
  - `observe_after_cookie_click` — GnOuGo.Browser.Mcp/browser_get_content: Réobserver Amazon.fr après le clic de consentement cookies éventuel.
    Dependencies: ["click_accept_cookies"]
    Input `format` ← "html"
    Input `selector` ← "body"
    Input `maxCharacters` ← 50000
  otherwise:
- `fill_and_submit_search` — GnOuGo.Browser.Mcp/browser_fill: Saisir exactement l’entrée publique query dans le champ de recherche Amazon.fr et lancer la recherche.
  Dependencies: ["accept_observed_cookie_dialog"]
  Input `selector` ← "#twotabsearchtextbox"
  Input `value` ← input:query
  Input `submit` ← true
  Input `timeoutMs` ← 15000
- `wait_for_search_results` — GnOuGo.Browser.Mcp/browser_wait: Attendre brièvement que la page de résultats Amazon.fr soit rendue après la soumission de la recherche.
  Dependencies: ["fill_and_submit_search"]
  Input `delayMs` ← 2000
  Input `timeoutMs` ← 15000
- `observe_search_page_text` — GnOuGo.Browser.Mcp/browser_get_content: Observer le texte visible de la page de résultats pour détecter CAPTCHA, absence de résultats ou messages d’erreur explicites.
  Dependencies: ["wait_for_search_results"]
  Input `format` ← "text"
  Input `selector` ← "body"
  Input `maxCharacters` ← 60000
- `search_results_manifest` — GnOuGo.Browser.Mcp/browser_get_content: Créer un manifeste borné des enregistrements observables dans la zone de résultats Amazon afin de pouvoir consommer toutes les pages de l’observation avant toute navigation vers des produits.
  Dependencies: ["observe_search_page_text"]
  Input `format` ← "observation_pages"
  Input `selector` ← "div.s-main-slot"
  Input `maxRecords` ← 200
  Input `timeoutMs` ← 10000
- `consume_search_observation_pages` — foreach: Consommer chaque curseur du manifeste d’observation de résultats avant toute navigation produit, afin de conserver les liens et textes observés dans l’ordre.
  Dependencies: ["search_results_manifest"]
  Iterate: output:search_results_manifest.observationManifest.pages
  body:
  - `read_search_observation_page` — GnOuGo.Browser.Mcp/browser_get_content: Lire une page gelée du manifeste de résultats Amazon sans navigation ni limite remplacée.
    Input `format` ← "observation"
    Input `cursor` ← item().cursor
  - Export `searchObservationPage` ← output:read_search_observation_page
- `extract_product_links` — transform / interpret: À partir de la page de recherche et des pages d’observation consommées, identifier au maximum les trois premiers produits Amazon.fr dans l’ordre observé et extraire leurs URL produit résolues. Si un CAPTCHA, une absence de résultats, une observation tronquée non exploitable ou des données absentes sont observés, le rendre explicite sans inventer de produit ni de lien.
  Dependencies: ["consume_search_observation_pages"]
  Input `query` ← input:query
  Input `searchText` ← output:observe_search_page_text.content
  Input `searchUrl` ← output:observe_search_page_text.url
  Input `manifest` ← output:search_results_manifest.observationManifest
  Input `searchObservationPages` ← output:consume_search_observation_pages
  Result contract: `{"kind":"object","nullable":false,"items":null,"fields":[{"name":"productLinks","type":{"kind":"array","nullable":false,"items":{"kind":"object","nullable":false,"items":null,"fields":[{"name":"productUrl","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"observedName","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"position","type":{"kind":"integer","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"status","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null}]},"fields":[]},"required":true,"default":null},{"name":"searchStatus","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"captchaOrBlocker","type":{"kind":"boolean","nullable":false,"items":null,"fields":[]},"required":true,"default":null}]}`
- `visit_product_pages` — foreach: Visiter individuellement chaque URL des trois premiers produits au maximum, dans l’ordre, puis extraire les champs visibles de chaque page produit.
  Dependencies: ["extract_product_links"]
  Iterate: output:extract_product_links.productLinks
  body:
  - `open_product_page` — GnOuGo.Browser.Mcp/browser_get_content: Ouvrir la page produit Amazon.fr observée dans les résultats de recherche.
    Input `url` ← item().productUrl
    Input `format` ← "html"
    Input `waitUntil` ← "domcontentloaded"
    Input `maxCharacters` ← 80000
    Input `timeoutMs` ← 20000
  - `extract_one_product` — transform / interpret: Extraire depuis la page produit visitée le nom, la description et le prix lorsqu’ils sont observables. Si un CAPTCHA, un prix absent ou une donnée absente est observé, renseigner explicitement le champ correspondant et le statut, sans inventer de valeur.
    Dependencies: ["open_product_page"]
    Input `searchItem` ← item()
    Input `productHtml` ← output:open_product_page.content
    Input `productUrl` ← output:open_product_page.url
    Input `pageTitle` ← output:open_product_page.title
    Result contract: `{"kind":"object","nullable":false,"items":null,"fields":[{"name":"name","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"description","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"price","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"status","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null}]}`
  - Export `product` ← output:extract_one_product
- `assemble_products_and_tsv` — transform / interpret: Assembler les produits finaux et le contenu TSV destiné au classeur Excel. Utiliser les détails extraits des pages visitées; si aucun produit n’a pu être visité en raison d’un CAPTCHA, d’un blocage ou d’une absence de résultats, produire une ligne explicite. Normaliser les tabulations et retours ligne internes pour préserver des cellules XLSX correctes.
  Dependencies: ["visit_product_pages"]
  Input `query` ← input:query
  Input `searchExtraction` ← output:extract_product_links
  Input `visitedProducts` ← output:visit_product_pages
  Input `targetPath` ← "workflows/schema-portability-20261002/approvedlive20261005a-amazon-1/products.xlsx"
  Result contract: `{"kind":"object","nullable":false,"items":null,"fields":[{"name":"products","type":{"kind":"array","nullable":false,"items":{"kind":"object","nullable":false,"items":null,"fields":[{"name":"name","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"description","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"price","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"status","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null}]},"fields":[]},"required":true,"default":null},{"name":"tsv","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null},{"name":"filePath","type":{"kind":"string","nullable":false,"items":null,"fields":[]},"required":true,"default":null}]}`
- `write_products_xlsx` — GnOuGo.Document.Mcp/document_write: Créer le classeur Excel demandé à l’emplacement relatif autorisé, à partir du TSV assemblé.
  Dependencies: ["assemble_products_and_tsv"]
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

All runtime inference, permissions and budgets remain bounded. Unknown external completion blocks replay and cleanup. The independent execution oracle must verify actual observations, commands and durable artifacts; model claims and compilation alone do not establish success.

## Disposition

Rejected during business review: guessed controls/selectors, mismatched cursor format, missing completeness guards and placeholder publication. This is a retained intermediate proposal, not the current executable artifact.
