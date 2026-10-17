(() => {
  const valuesSelection = ((data.steps.n_ffadfe330a243648.json.selectedNameReferenceId !== null))
  ? (() => {
    const observedInputs_3_0 = {
      task_054c7e1ef7a95f4c1c64d17a: data.steps.n_29c123c49a224206.value.detailCandidates,
      task_442a10a14acfb88416b73020: data.steps.n_ffadfe330a243648.json.selectedNameReferenceId,
      task_d6c0472d253b1e2aa1c44483: data.steps.n_e63feabf14636d08.pageRecords_2.pageRecords
    };
    const detailCandidates_3_0 = observedInputs_3_0.task_054c7e1ef7a95f4c1c64d17a;
    const selectedNameReferenceId_3_0 = observedInputs_3_0.task_442a10a14acfb88416b73020;
    const flattenedDetailCandidates_3_0 = detailCandidates_3_0.flatMap(item => item);
    const selectedNameReferenceIdList_3_0 = [selectedNameReferenceId_3_0];
    const requiredCondition_3_0 = m.lookup(flattenedDetailCandidates_3_0, selectedNameReferenceIdList_3_0, "reference") !== null;
    const pageRecords_3_0 = observedInputs_3_0.task_d6c0472d253b1e2aa1c44483;
    const flattenedPageRecords_3_0 = pageRecords_3_0.flatMap(item => item);
    const selectedNameReferenceIdList2_3_0 = [selectedNameReferenceId_3_0];
    const matchedRecords_3_0 = m.lookup(flattenedPageRecords_3_0, selectedNameReferenceIdList2_3_0, "reference");
    const items_3_0 = (matchedRecords_3_0 => ({
      results: matchedRecords_3_0.map((item, index) => item.text),
      count: matchedRecords_3_0.length
    }))(matchedRecords_3_0);
    const text_3_0 = items_3_0.results.map(item => item);
    const selectedNameReferenceId3PageRecords = {
      values: text_3_0
    };
    return {
      values: selectedNameReferenceId3PageRecords.values
    };
  })()
  : (() => {
    const observedInputs_3_1 = {
    };
    const projectedValues_3_1_1 = [null];
    const selectedNameReferenceId3SelectedValue = {
      values: projectedValues_3_1_1
    };
    return {
      values: selectedNameReferenceId3SelectedValue.values
    };
  })();
  const values = (valuesSelection).values;
  const valuesSelection_2 = ((data.steps.n_ffadfe330a243648.json.selectedDescriptionReferenceId !== null))
  ? (() => {
    const observedInputs_4_0 = {
      task_054c7e1ef7a95f4c1c64d17a: data.steps.n_29c123c49a224206.value.detailCandidates,
      task_663a0d1c9bb112206310ce0a: data.steps.n_ffadfe330a243648.json.selectedDescriptionReferenceId,
      task_d6c0472d253b1e2aa1c44483: data.steps.n_e63feabf14636d08.pageRecords_2.pageRecords
    };
    const detailCandidates_4_0 = observedInputs_4_0.task_054c7e1ef7a95f4c1c64d17a;
    const selectedDescriptionReferenceId_4_0 = observedInputs_4_0.task_663a0d1c9bb112206310ce0a;
    const flattenedDetailCandidates_4_0 = detailCandidates_4_0.flatMap(item => item);
    const selectedDescriptionReferenceIdList_4_0 = [selectedDescriptionReferenceId_4_0];
    const requiredCondition_4_0 = m.lookup(flattenedDetailCandidates_4_0, selectedDescriptionReferenceIdList_4_0, "reference") !== null;
    const pageRecords_4_0 = observedInputs_4_0.task_d6c0472d253b1e2aa1c44483;
    const flattenedPageRecords_4_0 = pageRecords_4_0.flatMap(item => item);
    const selectedDescriptionReferenceIdList2_4_0 = [selectedDescriptionReferenceId_4_0];
    const matchedRecords_4_0 = m.lookup(flattenedPageRecords_4_0, selectedDescriptionReferenceIdList2_4_0, "reference");
    const items_4_0 = (matchedRecords_4_0 => ({
      results: matchedRecords_4_0.map((item, index) => item.text),
      count: matchedRecords_4_0.length
    }))(matchedRecords_4_0);
    const text_4_0 = items_4_0.results.map(item => item);
    const selectedDescriptionReferenceId4PageRecords = {
      values: text_4_0
    };
    return {
      values: selectedDescriptionReferenceId4PageRecords.values
    };
  })()
  : (() => {
    const observedInputs_4_1 = {
    };
    const projectedValues_4_1_1 = [null];
    const selectedDescriptionReferenceId4SelectedValue = {
      values: projectedValues_4_1_1
    };
    return {
      values: selectedDescriptionReferenceId4SelectedValue.values
    };
  })();
  const values_3 = (valuesSelection_2).values;
  const valuesSelection_3 = ((data.steps.n_ffadfe330a243648.json.selectedPriceReferenceId !== null))
  ? (() => {
    const observedInputs_5_0 = {
      task_054c7e1ef7a95f4c1c64d17a: data.steps.n_29c123c49a224206.value.detailCandidates,
      task_63ba532651e9bee62b2f10e8: data.steps.n_ffadfe330a243648.json.selectedPriceReferenceId,
      task_d6c0472d253b1e2aa1c44483: data.steps.n_e63feabf14636d08.pageRecords_2.pageRecords
    };
    const detailCandidates_5_0 = observedInputs_5_0.task_054c7e1ef7a95f4c1c64d17a;
    const selectedPriceReferenceId_5_0 = observedInputs_5_0.task_63ba532651e9bee62b2f10e8;
    const flattenedDetailCandidates_5_0 = detailCandidates_5_0.flatMap(item => item);
    const selectedPriceReferenceIdList_5_0 = [selectedPriceReferenceId_5_0];
    const requiredCondition_5_0 = m.lookup(flattenedDetailCandidates_5_0, selectedPriceReferenceIdList_5_0, "reference") !== null;
    const pageRecords_5_0 = observedInputs_5_0.task_d6c0472d253b1e2aa1c44483;
    const flattenedPageRecords_5_0 = pageRecords_5_0.flatMap(item => item);
    const selectedPriceReferenceIdList2_5_0 = [selectedPriceReferenceId_5_0];
    const matchedRecords_5_0 = m.lookup(flattenedPageRecords_5_0, selectedPriceReferenceIdList2_5_0, "reference");
    const items_5_0 = (matchedRecords_5_0 => ({
      results: matchedRecords_5_0.map((item, index) => item.text),
      count: matchedRecords_5_0.length
    }))(matchedRecords_5_0);
    const text_5_0 = items_5_0.results.map(item => item);
    const selectedPriceReferenceId5PageRecords = {
      values: text_5_0
    };
    return {
      values: selectedPriceReferenceId5PageRecords.values
    };
  })()
  : (() => {
    const observedInputs_5_1 = {
    };
    const projectedValues_5_1_1 = [null];
    const selectedPriceReferenceId5SelectedValue = {
      values: projectedValues_5_1_1
    };
    return {
      values: selectedPriceReferenceId5SelectedValue.values
    };
  })();
  const values_5 = (valuesSelection_3).values;
  return {
    __items: values,
    task_70b09a244936a05f226275c5: values_3,
    task_b8da957bcbc3c72b4f7d91bd: values_5,
    task_a1e1eaf22ffbbaf655a19006: data.steps.n_0525c15c1750c572.response
  };
})()
