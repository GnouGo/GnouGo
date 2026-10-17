# Review: one fresh Amazon execution

Status: **approved by the user and executed once; failed before search submission**. The unchanged reviewed artifact remains below; see [execution evidence](amazon-execution.json) and [approval](amazon-approval.json).

- Frozen production/harness candidate: `ce58f8da5b2abf0acfa2508ffdc7701a89bd58c6`.
- Campaign/cohort/run: `schema-portability-20261002` / `nullfacts20261010a` / `nullfacts20261010a-amazon-1`.
- Revision: **7**.
- Artifact hash: **`7e4c67f5932426a4e9273c3b8b8301b5f3e518269011f953573840d88ccf66ab`**.
- Actual [TaskPlan](amazon-taskplan-r7.json), [compiled YAML](amazon-r7.yaml), [session inspection](amazon-planning-r7.json), and [frozen hashes](frozen-candidate.json).

## Accepted requirements beside the actual composition

| Requirement to acknowledge | Actual tasks and data dependencies |
|---|---|
| `search_amazon_fr` | `open_amazon_fr_complete` captures the home page. `extract_home_search_controls` processes every complete page into only observed key/label candidates. `select_home_search_control_id` selects one offered ID. Two checked lookups validate membership then reconnect the original Browser reference. `fill_and_submit_query` uses that reference and the public `query`, with `submit: true`. |
| `max_10_products` | After real submission, `read_search_results_complete` captures the results. Independent extraction processes every page. Global selection receives only flattened key/label candidates and returns up to ten IDs in observed order, retaining repetitions. Two checked lookups precede `visit_and_extract_products`, a sequential loop bounded at ten, navigating to original observed hrefs. |
| `xlsx_saved` | Each visited product yields compact facts and a non-null product record. `format_products_tsv` formats at most ten records. `save_products_xlsx` invokes the actual Document writer at `workflows/schema-portability-20261002/nullfacts20261010a-amazon-1/products.xlsx`. `workbookPath` and `saved` are taken directly from that writer's declared result. |
| `explicit_absences` | Every product record is non-null; `nom`, `description`, `prix` and `erreurObservee` each permit null. Extraction preserves observed facts, with literal null allowed only at those nullable paths. No href is supplied as a fallback or as interpretation context. Formatting must keep missing facts explicit without fabrication. |
| `browser_closed` | `close_browser` is in the root `always` scope, after the save attempt or verified failure. Unknown completion still blocks unsafe cleanup/replay. |

## Contract and input review

The source is `observation_complete` throughout. Each extraction's `each.input` names its bound `pages` collection; each page returns one candidate/fact array and the assembled contract is an array of arrays. Explicit one-level flattening supplies global interpretation. Full snapshots and original references/hrefs remain separate; no full snapshot is an interpretation input.

Four checked `m.lookup` calls are present in the compiled YAML. The offered-candidate check executes before the original-record check, even where its returned records are only an independent validation boundary. No selected reference is synthesized. Browser retains final identity/action validation.

The final targeted revision removed invented extraction capability booleans and the unrelated visited URL. It did not add the requested redundant truncation predicates. This was checked against the frozen producer: `observation_complete` throws `OBSERVATION_INCOMPLETE` before publishing a snapshot when either capture or manifest is truncated; published snapshots have both flags false and all pages from one generation. The retained `success == true && observationSnapshot != null` assertion therefore consumes the complete-acquisition contract. This reasoning does not apply to legacy paged/partial formats. No consumer check or producer limit was weakened.

The plan contains real search submission, individual product navigation, actual writing and cleanup. It contains no consent action, deduplication or extra ranking criterion. Two pure projections retain original page records for deterministic reconnection. Selection and per-product reconciliation remain explicit interpretation, and bounded TSV formatting remains an explicit transform. Contract/provenance checks do not prove semantic relevance or successful extraction; the unchanged independent live oracle must verify those outcomes.

## Inference and limits disclosed for approval (retained pre-execution review)

Independent extraction uses the existing adaptive mapping profile: first a generic script, then specialization only for unresolved observations when necessary. Possible per-item inference shares the unchanged 30-minute cumulative runtime inference/execution deadline and campaign monetary admission. It is not a two-call promise. Every item and the complete target validate before publication. Mapping sandbox limits remain 50,000,000 bytes, 10,000 statements and 5,000 ms per invocation, cumulatively across its items/attempts. Request limits remain 96,000 input / 32,768 output tokens, with stricter existing bindings enforced.

Planning used five logical calls, six transport attempts, one automatic repair, two explicit revisions and two discovery reads. Verified usage: 49,468 input / 16,313 output tokens; cost €0.6537669713372970094950749845. Active planning time: 517,725 ms. No workflow execution has started. Campaign committed/reserved upper bound: €127.62158578606425266549552572 / €150. Five historical unknown transport reservations remain unchanged (€6.5016147947905255187922165013).

## Required execution evidence (retained review criteria)

One execution only after approval of this revision/hash and all five requirement IDs. Independently check complete observations, actual visited product pages, source-grounded values, XLSX cells/order/output location and Browser closure. A workflow status alone is insufficient. Preserve every failed invocation; never replay an uncertain one.

## Actual task and binding outline

The following is rendered from revision 7, not a proposed replacement. The JSON/YAML above remain authoritative; value-task lookup declarations are described in the review above.

```text
revision 7 artifact 7e4c67f5932426a4e9273c3b8b8301b5f3e518269011f953573840d88ccf66ab
requirements [('search_amazon_fr', 'Amazon.fr est utilisé pour rechercher la valeur de query.'), ('max_10_products', 'Seuls les 10 premiers produits extraits de la page de résultats sont visités.'), ('xlsx_saved', 'Le fichier est sauvegardé à workflows/schema-portability-20261002/nullfacts20261010a-amazon-1/products.xlsx.'), ('explicit_absences', 'Les prix, descriptions, noms ou erreurs absents/observés restent explicites et ne sont jamais inventés.'), ('browser_closed', 'Le navigateur est fermé dans un bloc always après la tentative de sauvegarde.')]
open_amazon_fr_complete operation   
 Objective: Ouvrir Amazon.fr et acquérir une observation complète typée de la page d’accueil, sans action de consentement ni autre effet non demandé.
 url = https://www.amazon.fr/
 format = observation_complete
 waitUntil = domcontentloaded
 timeoutMs = 30000
 maxRecords = 200
extract_home_search_controls transform extract {'input': 'pages', 'output': 'rows'} 
 Objective: Depuis chaque page complète de l’observation d’accueil, extraire uniquement les contrôles observés capables d’être remplis, avec leur identité compacte fondée sur reference, leur libellé observé et les faits minimaux nécessaires au choix.
 pages = output(open_amazon_fr_complete.observationSnapshot).pages
 requires = and(equal(output(open_amazon_fr_complete.success), True), not_equal(output(open_amazon_fr_complete.observationSnapshot), null()))
 result = object{rows!:array<array<object{key!:string,label!:string?}>[0,200]>[0,100]}
 after = ['open_amazon_fr_complete']
select_home_search_control_id transform interpret  
 Objective: Choisir explicitement un seul ID offert correspondant au champ de recherche Amazon.fr, sans générer de sélecteur ni référence et sans utiliser de HTML.
 offeredControls = flatten(output(extract_home_search_controls.rows))
 result = object{selectedKeys!:array<string>[1,1]}
 after = ['extract_home_search_controls']
project_home_records_by_page foreach   
 Objective: Conserver séparément les enregistrements originaux de chaque page complète d’accueil pour permettre la reconnexion par lookup sans réinterprétation.
 requires = and(equal(output(open_amazon_fr_complete.success), True), not_equal(output(open_amazon_fr_complete.observationSnapshot), null()))
 items = output(open_amazon_fr_complete.observationSnapshot).pages
 after = ['open_amazon_fr_complete']
 body:
   EXPORT records = item().records
lookup_selected_home_candidates value   
 Objective: Valider les IDs sélectionnés contre les candidats offerts avant toute action.
 after = ['select_home_search_control_id']
lookup_home_original_controls value   
 Objective: Reconnecter les IDs validés aux enregistrements originaux observés par reference afin d’utiliser la référence exacte du navigateur.
 after = ['project_home_records_by_page', 'lookup_selected_home_candidates']
submit_query_on_selected_control foreach   
 Objective: Dans une itération bornée, remplir la référence exacte du contrôle de recherche sélectionné avec query et soumettre réellement la recherche.
 items = output(lookup_home_original_controls.controls)
 after = ['lookup_home_original_controls']
 body:
  fill_and_submit_query operation   
   Objective: Remplir le contrôle observé avec l’entrée publique query et soumettre par Entrée, sans sélecteur deviné.
   reference = item().reference
   value = input(query)
   submit = True
   timeoutMs = 20000
   requires = and(not_equal(item().reference, null()), not_equal(input(query), null()))
   EXPORT actions = {success:output(fill_and_submit_query.success), errorCode:output(fill_and_submit_query.error_code), errorMessage:output(fill_and_submit_query.error_message), submitted:output(fill_and_submit_query.submitted)}
wait_after_search_submit operation   
 Objective: Attendre brièvement après la soumission réelle de la recherche avant l’observation complète des résultats.
 selector = body
 state = visible
 delayMs = 1500
 timeoutMs = 20000
 after = ['submit_query_on_selected_control']
read_search_results_complete operation   
 Objective: Acquérir une observation complète typée de la page de résultats après la recherche réelle.
 format = observation_complete
 timeoutMs = 30000
 maxRecords = 200
 after = ['wait_after_search_submit']
extract_search_product_candidates transform extract {'input': 'pages', 'output': 'rows'} 
 Objective: Depuis chaque page complète des résultats, extraire en ordre observé les candidats liens produits avec identité compacte, sans classement, filtre, déduplication ni URL dans l’interprétation globale.
 pages = output(read_search_results_complete.observationSnapshot).pages
 requires = and(equal(output(read_search_results_complete.success), True), not_equal(output(read_search_results_complete.observationSnapshot), null()))
 result = object{rows!:array<array<object{key!:string,label!:string?}>[0,200]>[0,100]}
 after = ['read_search_results_complete']
project_search_records_by_page foreach   
 Objective: Conserver les enregistrements originaux complets de la page de résultats pour les lookups de validation et de reconnexion.
 requires = and(equal(output(read_search_results_complete.success), True), not_equal(output(read_search_results_complete.observationSnapshot), null()))
 items = output(read_search_results_complete.observationSnapshot).pages
 after = ['read_search_results_complete']
 body:
   EXPORT records = item().records
select_first_product_ids transform interpret  
 Objective: Sélectionner explicitement au maximum les 10 premiers IDs produits offerts dans leur ordre observé, répétitions incluses, sans dédupliquer ni ajouter de critère de classement.
 offeredProducts = flatten(output(extract_search_product_candidates.rows))
 result = object{selectedKeys!:array<string>[0,10]}
 after = ['extract_search_product_candidates']
lookup_selected_product_candidates value   
 Objective: Valider les IDs produits sélectionnés contre la liste compacte offerte.
 after = ['select_first_product_ids']
lookup_product_original_links value   
 Objective: Reconnecter les IDs produits validés aux enregistrements originaux observés afin de visiter leurs href exacts.
 after = ['project_search_records_by_page', 'lookup_selected_product_candidates']
visit_and_extract_products foreach   
 Objective: Visiter, dans l’ordre sélectionné et au plus dix fois, chaque href produit original observé puis extraire ses faits complets sans inventer de valeur.
 items = output(lookup_product_original_links.links)
 after = ['lookup_product_original_links']
 body:
  read_product_complete operation   
   Objective: Ouvrir le href exact observé pour le produit courant et acquérir une observation complète typée de la fiche produit.
   url = item().href
   format = observation_complete
   waitUntil = domcontentloaded
   timeoutMs = 30000
   maxRecords = 200
   requires = and(not_equal(item().href, null()), not_equal(item().reference, null()))
  extract_product_page_facts transform extract {'input': 'pages', 'output': 'factsByPage'} 
   Objective: Pour chaque page complète de la fiche produit, extraire un fait compact non nul contenant seulement nom, description, prix et erreur observée, avec champs factuels nullable lorsqu’ils sont indisponibles.
   pages = output(read_product_complete.observationSnapshot).pages
   requires = and(equal(output(read_product_complete.success), True), not_equal(output(read_product_complete.observationSnapshot), null()))
   result = object{factsByPage!:array<array<object{nom!:string?,description!:string?,prix!:string?,erreurObservee!:string?}>[0,10]>[0,100]}
   after = ['read_product_complete']
  assemble_product_record transform interpret  
   Objective: Assembler, depuis les faits compacts complets de la fiche courante, un enregistrement produit non nul à champs nom, description et prix nullable, sans substituer de href ou de placeholder inventé.
   facts = flatten(output(extract_product_page_facts.factsByPage))
   result = object{product!:object{nom!:string?,description!:string?,prix!:string?,erreurObservee!:string?}}
   after = ['extract_product_page_facts']
   EXPORT products = output(assemble_product_record).product
format_products_tsv transform interpret  
 Objective: Transformer au plus dix enregistrements produits compacts, dans leur ordre, en TSV pour le classeur; expliciter seulement à cette étape les champs null/absents sans inventer de faits.
 products = output(visit_and_extract_products.products)
 result = object{tsv!:string}
 after = ['visit_and_extract_products']
save_products_xlsx operation   
 Objective: Sauvegarder le classeur Excel demandé au chemin relatif exact, uniquement après les acquisitions et extractions complètes requises.
 filePath = workflows/schema-portability-20261002/nullfacts20261010a-amazon-1/products.xlsx
 content = output(format_products_tsv).tsv
 append = False
 encoding = utf-8
 after = ['format_products_tsv']
close_browser operation   ALWAYS
 Objective: Fermer le navigateur même en cas d’échec d’une étape précédente.
 EXPORT workbookPath = output(save_products_xlsx.filePath)
 EXPORT saved = output(save_products_xlsx.success)
```
