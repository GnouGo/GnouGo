# Amazon workflow for explicit review

Candidate: `34bfd6040181cf789a5472506821c5a7f5e2e552`.
Run: `focusedlive20261005a-amazon-1`; revision **6**.
Artifact hash: `0588d6083951556a1c7fe4cf397b46221944374148c89a0b1d7e07cc6edbb05e`.

No workflow execution has started. Review each accepted requirement against the actual task tree below. Approval permits the existing bounded live evaluation, not permission expansion.

## Accepted requirements

- `search_amazon_fr`: Use Amazon.fr to search for the article provided by the query input.
- `extract_up_to_three_products`: List search results, then navigate to at most the first three product pages and extract product name, description and price. CAPTCHA and absent price/data must remain explicit and must not be invented.
- `save_excel`: Create and save an Excel workbook on disk at workflows/schema-portability-20261002/focusedlive20261005a-amazon-1/products.xlsx containing the extracted product data.
- `cleanup_browser`: Close the browser even in case of failure.

Public input: `query = chaussure geox homme 45`. Output: `workbookPath`.

The workbook path is `workflows/schema-portability-20261002/focusedlive20261005a-amazon-1/products.xlsx`.

## Actual task tree

Every extract transform allows one generation and one repair per invocation; cache hits are revalidated. Explicit interpret transforms still use runtime inference. Page loops are bounded to 100 manifest pages and product visits to three, in sequence. Capture/manifest truncation fails before extraction. Missing required product data must block publication. Browser close is a finalizer. The independent oracle still checks actual visits and observed XLSX cells.

- `build_search_url` — transform/interpret: Construire l’URL Amazon.fr de recherche à partir de l’unique entrée publique query, en encodant query pour une URL HTTP valide sans espaces.
  Inputs: query ← input:query
  Result fields: searchUrl: string
- `open_search_manifest` — operation: Ouvrir Amazon.fr sur la page de recherche de l’article demandé et capturer un manifeste borné d’observation avant toute interaction.
  Dependencies: build_search_url
  Inputs: url ← output:build_search_url.searchUrl; format ← "observation_pages"; waitUntil ← "domcontentloaded"; maxRecords ← 200; timeoutMs ← 30000
- `read_initial_search_pages` — foreach: Consommer toutes les pages figées du manifeste d’observation initial de recherche avant toute navigation ou interaction ultérieure.
  Dependencies: open_search_manifest
  Required guard: and(equal(output:open_search_manifest.observationManifest.captureTruncated, false), equal(output:open_search_manifest.observationManifest.manifestTruncated, false))
  Iterate: output:open_search_manifest.observationManifest.pages; maximum 100
  body:
    - `read_initial_search_page` — operation: Lire une page figée du manifeste initial avec le même format d’observation, sans URL, sélecteur ni limite modifiés.
      Inputs: cursor ← current item.cursor; format ← "observation_pages"
    - Export `pageObservation` ← output:read_initial_search_page
- `compact_initial_search_pages` — transform/extract: Compacter séparément chaque page d’observation initiale de recherche, en copiant seulement les liens, textes et contrôles observés utiles pour consentement, CAPTCHA et résultats, sans inventer d’indicateurs ni de données produit.
  Dependencies: read_initial_search_pages
  Inputs: searchPageObservation ← output:read_initial_search_pages.pageObservation
  Independent extraction: {"input": "searchPageObservation", "output": "searchPageCompacts"}
  Result fields: searchPageCompacts: array
- `detect_initial_consent_state` — transform/interpret: À partir des compacts observés, décider si un contrôle de consentement cookies observé doit être cliqué; détecter aussi un blocage CAPTCHA visible, sans extraire ni inventer de produits.
  Dependencies: compact_initial_search_pages
  Inputs: query ← input:query; initialSearchCompacts ← output:compact_initial_search_pages
  Result fields: hasConsentControl: boolean, consentText: string, hasBlockingCaptcha: boolean, stateNote: string
- `resolve_search_after_optional_consent` — conditional: Si un consentement cookies cliquable a été observé, cliquer explicitement ce contrôle puis refaire une capture complète fraîche; sinon conserver les compacts initiaux.
  Dependencies: detect_initial_consent_state
  Branch condition: equal(output:detect_initial_consent_state.hasConsentControl, true)
  body:
    - `accept_observed_consent` — operation: Cliquer le contrôle de consentement cookies observé et autorisé sur Amazon.fr.
      Inputs: text ← output:detect_initial_consent_state.consentText; exact ← false; waitUntil ← "domcontentloaded"; timeoutMs ← 15000
      Required guard: not_equal(output:detect_initial_consent_state.consentText, "")
    - `post_consent_search_manifest` — operation: Réobserver fraîchement la page de recherche après consentement via un manifeste borné complet avant toute navigation.
      Dependencies: accept_observed_consent
      Inputs: format ← "observation_pages"; maxRecords ← 200; timeoutMs ← 30000
    - `read_post_consent_search_pages` — foreach: Consommer toutes les pages figées du manifeste post-consentement avant toute navigation.
      Dependencies: post_consent_search_manifest
      Required guard: and(equal(output:post_consent_search_manifest.observationManifest.captureTruncated, false), equal(output:post_consent_search_manifest.observationManifest.manifestTruncated, false))
      Iterate: output:post_consent_search_manifest.observationManifest.pages; maximum 100
      body:
        - `read_post_consent_search_page` — operation: Lire une page figée du manifeste post-consentement avec le même format d’observation, sans URL, sélecteur ni limite modifiés.
          Inputs: cursor ← current item.cursor; format ← "observation_pages"
        - Export `pageObservation` ← output:read_post_consent_search_page
    - `compact_post_consent_search_pages` — transform/extract: Compacter séparément chaque page d’observation post-consentement, en copiant seulement les liens, textes et contrôles observés utiles, sans inventer d’indicateurs ni de données produit.
      Dependencies: read_post_consent_search_pages
      Inputs: searchPageObservation ← output:read_post_consent_search_pages.pageObservation
      Independent extraction: {"input": "searchPageObservation", "output": "searchPageCompacts"}
      Result fields: searchPageCompacts: array
    - Export `searchCompacts` ← output:compact_post_consent_search_pages
  otherwise:
    - `keep_initial_search_compacts` — value: Conserver les compacts initiaux lorsqu’aucun consentement cliquable n’est requis.
    - Export `searchCompacts` ← output:keep_initial_search_compacts.searchCompacts
- `extract_search_product_candidates` — transform/extract: Extraire en mode source-grounded, depuis les seuls compacts de recherche, les liens observés qui correspondent à des pages produit Amazon.fr et leur texte observé, en conservant l’ordre et sans inventer d’URL.
  Dependencies: resolve_search_after_optional_consent
  Inputs: query ← input:query; searchCompacts ← output:resolve_search_after_optional_consent.searchCompacts
  Result fields: productCandidates: array, observedCaptchaTexts: array, observedNoResultTexts: array
- `select_first_three_products` — transform/interpret: Sélectionner explicitement au maximum les trois premières URLs produit observées dans l’ordre, et établir si la recherche peut continuer; ne pas remplacer une absence de résultat ou un CAPTCHA par des produits inventés.
  Dependencies: extract_search_product_candidates
  Inputs: searchCandidates ← output:extract_search_product_candidates
  Result fields: productUrls: array, canVisitProducts: boolean, searchIssue: string
- `visit_and_extract_products` — foreach: Visiter séquentiellement chaque URL produit observée sélectionnée, au maximum trois, consommer la capture complète de chaque page avant la navigation suivante, puis produire un résultat ordonné par produit.
  Dependencies: select_first_three_products
  Required guard: equal(output:select_first_three_products.canVisitProducts, true)
  Iterate: output:select_first_three_products.productUrls; maximum 3
  body:
    - `open_product_manifest` — operation: Naviguer vers l’URL produit observée et capturer un manifeste d’observation borné de la page entière, sans sélecteur spécifique au site non observé ni hypothèse de structure de page.
      Inputs: url ← current item; format ← "observation_pages"; waitUntil ← "domcontentloaded"; maxRecords ← 200; timeoutMs ← 30000
    - `read_product_pages` — foreach: Consommer toutes les pages figées du manifeste produit complet avant de passer à une autre navigation.
      Dependencies: open_product_manifest
      Required guard: and(equal(output:open_product_manifest.observationManifest.captureTruncated, false), equal(output:open_product_manifest.observationManifest.manifestTruncated, false))
      Iterate: output:open_product_manifest.observationManifest.pages; maximum 100
      body:
        - `read_product_page` — operation: Lire une page figée du manifeste produit avec le même format d’observation, sans URL, sélecteur ni limite modifiés.
          Inputs: cursor ← current item.cursor; format ← "observation_pages"
        - Export `pageObservation` ← output:read_product_page
    - `compact_product_pages` — transform/extract: Compacter séparément chaque page d’observation produit, en copiant uniquement les textes et liens observés utiles au nom, à la description, au prix et au CAPTCHA, sans inventer de libellés, valeurs ou indicateurs.
      Dependencies: read_product_pages
      Inputs: productPageObservation ← output:read_product_pages.pageObservation
      Independent extraction: {"input": "productPageObservation", "output": "productPageCompacts"}
      Result fields: productPageCompacts: array
    - `extract_product_observed_fields` — transform/extract: Extraire en mode source-grounded, depuis les seuls compacts de cette page produit, les valeurs observées candidates pour URL, nom, description et prix; les champs absents restent des listes vides, sans valeurs inventées.
      Dependencies: compact_product_pages
      Inputs: productUrl ← current item; productPageCompacts ← output:compact_product_pages
      Result fields: productUrl: string, observedNames: array, observedDescriptions: array, observedPrices: array, observedCaptchaTexts: array
    - `select_product_fields_and_status` — transform/interpret: À partir des seules valeurs observées extraites pour ce produit, choisir les cellules nom, description et prix si elles sont toutes présentes; sinon rendre l’échec explicite et marquer le produit incomplet.
      Dependencies: extract_product_observed_fields
      Inputs: observedProductFields ← output:extract_product_observed_fields
      Result fields: url: string, name: string, description: string, price: string, complete: boolean, issue: string
    - Export `product` ← output:select_product_fields_and_status
- `validate_products_for_publication` — transform/interpret: Valider que la recherche et toutes les pages produit visitées sont complètes, sans CAPTCHA ni données requises absentes; toute absence de nom, description, prix, produit ou complétude non résolue rend la publication impossible explicitement.
  Dependencies: visit_and_extract_products, select_first_three_products
  Inputs: searchSelection ← output:select_first_three_products; products ← output:visit_and_extract_products
  Result fields: allRequiredObserved: boolean, failureReason: string, validatedProducts: array
- `build_workbook_tsv` — transform/interpret: Assembler déterministiquement le contenu TSV du classeur Excel avec les colonnes nom, description et prix, seulement si toutes les données requises ont été observées et validées; normaliser les tabulations et retours ligne internes.
  Dependencies: validate_products_for_publication
  Inputs: validatedProducts ← output:validate_products_for_publication.validatedProducts
  Required guard: equal(output:validate_products_for_publication.allRequiredObserved, true)
  Result fields: tsv: string
- `write_products_workbook` — operation: Sauvegarder le classeur Excel demandé sur le disque au chemin relatif exact dans l’espace de travail autorisé.
  Dependencies: build_workbook_tsv
  Inputs: filePath ← "workflows/schema-portability-20261002/focusedlive20261005a-amazon-1/products.xlsx"; content ← output:build_workbook_tsv.tsv; append ← false; encoding ← "utf-8"
  Required guard: equal(output:validate_products_for_publication.allRequiredObserved, true)
- FINALIZER `close_browser_always` — operation: Fermer le navigateur et son contexte même si une étape précédente échoue.
- Export `workbookPath` ← output:write_products_workbook.filePath

## Review boundary

The plan includes model interpretation for URL construction, consent classification, selecting product candidates, assessing observed completeness and TSV formatting. These are visible runtime operations, not deterministic execution proof. The first proposal with guessed selectors and placeholder-cell instructions was rejected and retained. This revision removes those selectors, adds per-page source-grounded extraction and guards workbook publication. No result is counted as successful until the unchanged independent execution oracle passes.
