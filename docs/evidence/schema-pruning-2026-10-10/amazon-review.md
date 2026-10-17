# Recovered Amazon artifact — review only

Run: `physicaloutputs20261010a-amazon-1` · revision **6**.
Artifact: `ec011e1deed9ee48dd413d17911351ae2a08c8222a08efb271f6d84771ff86e0`.

The committed fourth response was applied without inference. Schema, TaskPlan,
graph and executable validation pass. This records compilation, not business
completeness or successful execution. No requirement has been acknowledged and
no artifact approval or execution was performed.

- [Recovered TaskPlan](amazon-r6-taskplan.json)
- [Recovered YAML](amazon-r6.yaml)
- [Full structured review](recovered-review.json)

## Accepted requirements

- `search_amazon_fr`: Amazon.fr est utilisé pour rechercher la valeur de query.
- `limit_10_products`: Seuls les 10 premiers produits observés sont traités.
- `extract_fields`: Pour chaque produit, le nom, la description, le prix, l’URL et les erreurs ou données absentes sont conservés explicitement.
- `save_xlsx`: Le fichier est écrit à workflows/schema-portability-20261002/physicaloutputs20261010a-amazon-1/products.xlsx.
- `cleanup`: Le navigateur est fermé dans une section always, indépendamment du succès ou de l’échec.

## Actual tasks and execution order

- `buildSearchUrl` (transform, normal): Construire l’URL Amazon.fr de recherche depuis l’unique entrée publique query, sans inventer de terme de recherche.
- `openSearchPage` (operation, normal): Ouvrir Amazon.fr directement sur la recherche correspondant à query et acquérir une observation complète bornée de la page de résultats.
  Operation: `cap_490d9e16075cbdca5954ec27`.
- `projectSearchRecordsByPage` (foreach, normal): Projeter les enregistrements originaux observés par page en conservant leurs identités de sélecteur, textes, hrefs et actions pour permettre les validations par lookup.
  body:
- `extractCandidateArrays` (transform, normal): Examiner chaque page d’observation de résultats indépendamment et extraire seulement les candidats produits observés en copiant exactement le selector observé vers key et le texte observé utile vers decisionText; un tableau vide signifie que la page a été examinée sans candidat produit.
- `selectTopOfferedCandidateIds` (transform, normal): Interpréter uniquement la liste compacte des candidats offerts et query afin de sélectionner au plus les dix premiers identifiants offerts dans l’ordre original, sans dédupliquer ni fabriquer d’identifiants.
- `projectVerifiedCandidateIds` (foreach, normal): Valider les identifiants sélectionnés par lookup contre les candidats offerts, puis projeter seulement leurs identifiants vérifiés dans l’ordre conservé.
  body:
- `scrapeProductPages` (foreach, normal): Visiter en ordre chacun des enregistrements originaux vérifiés, au maximum dix au total, avec l’href original observé, puis extraire les faits nom/description/prix sans valeurs inventées.
  body:
    - `openProductPage` (operation, normal): Ouvrir la page détail du produit courant avec l’href original observé et non nul.
      Operation: `cap_490d9e16075cbdca5954ec27`.
    - `extractProductFactArrays` (transform, normal): Extraire, page par page, uniquement les faits observés de nom, description et prix; les tableaux vides représentent l’absence observée de faits correspondants.
    - `interpretProductRecord` (transform, normal): Réconcilier les faits compacts exactement observés en un enregistrement produit non nul avec nom, description et prix nullables, l’URL originale et les erreurs explicites issues de l’observation ou des métadonnées producteur.
- `assembleWorkbookTsv` (transform, normal): Assembler un TSV borné pour le classeur .xlsx, avec une ligne par produit visité dans l’ordre, en conservant les valeurs observées et les absences/erreurs explicites.
- `saveWorkbook` (operation, normal): Écrire le classeur Excel au chemin exact demandé avant le nettoyage navigateur.
  Operation: `cap_37c513d225bb1b870053c5d3`.
- `finalErrors` (transform, normal): Conserver les erreurs explicites, y compris une éventuelle erreur d’écriture du classeur, sans déclarer le travail terminé si l’écriture a échoué.
- `closeBrowser` (operation, always): Fermer le navigateur même en cas d’échec d’une étape précédente.
  Operation: `cap_bdb7ba9ab04b18783883fa86`.

## Execution remains unverified

The concrete approval must identify this revision, artifact hash and all five
reviewed requirement IDs. No earlier approval applies. Navigation, per-product
visits, observed extraction, actual workbook cells and Browser cleanup have not
run for this artifact. The PR remains draft.

TaskPlan task count (including nested and finalizer tasks): 14.
