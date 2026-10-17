(() => {
  const rows = data.steps.n_057d824b69e5f524.outputs.rows !== null;
  const rows_2 = data.steps.n_057d824b69e5f524.outputs.rows;
  const flattenedMatchedRecords = rows_2.flatMap(item => item);
  const items = {
    results: flattenedMatchedRecords.map((item, index) => ({
      nom: (item).nom,
      description: (item).description,
      prix: (item).prix,
      url: (item).url,
      erreur: (item).erreur
    })),
    count: flattenedMatchedRecords.length
  };
  const products = items.results;
  return {
    products_2: {
      products
    }
  };
})()
