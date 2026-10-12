(() => {
  const valuesSelection = ((data.steps.n_792f841d2de3fb2a.json.nomId !== null))
  ? (() => {
    const observedInputs_7_0 = {
      task_ce4897bd04af61af1d5e751c: data.steps.n_3c64eb58e6504189.value.candidates,
      task_2426d88694f095546b08c036: data.steps.n_792f841d2de3fb2a.json.nomId,
      task_a7734218180f964bbd34154b: data.steps.n_351e8c8fdc59fd92.pageRecords_2.pageRecords
    };
    const candidates_7_0 = observedInputs_7_0.task_ce4897bd04af61af1d5e751c;
    const nomId_7_0 = observedInputs_7_0.task_2426d88694f095546b08c036;
    const flattenedCandidates_7_0 = candidates_7_0.flatMap(item => item);
    const nomIdList_7_0 = [nomId_7_0];
    const requiredCondition_7_0 = m.lookup(flattenedCandidates_7_0, nomIdList_7_0, "reference") !== null;
    const pageRecords_7_0 = observedInputs_7_0.task_a7734218180f964bbd34154b;
    const flattenedPageRecords_7_0 = pageRecords_7_0.flatMap(item => item);
    const nomIdList2_7_0 = [nomId_7_0];
    const matchedRecords_7_0 = m.lookup(flattenedPageRecords_7_0, nomIdList2_7_0, "reference");
    const items_7_0 = (matchedRecords_7_0 => ({
      results: matchedRecords_7_0.map((item, index) => item.text),
      count: matchedRecords_7_0.length
    }))(matchedRecords_7_0);
    const text_7_0 = items_7_0.results.map(item => item);
    const nomId7PageRecords = {
      values: text_7_0
    };
    return {
      values: nomId7PageRecords.values
    };
  })()
  : (() => {
    const observedInputs_7_1 = {
    };
    const projectedValues_7_1_1 = [null];
    const nomId7SelectedValue = {
      values: projectedValues_7_1_1
    };
    return {
      values: nomId7SelectedValue.values
    };
  })();
  const values = (valuesSelection).values;
  const values_2 = values;
  const valuesSelection_2 = ((data.steps.n_792f841d2de3fb2a.json.descriptionId !== null))
  ? (() => {
    const observedInputs_8_0 = {
      task_ce4897bd04af61af1d5e751c: data.steps.n_3c64eb58e6504189.value.candidates,
      task_ef35a60c0385c825c28da011: data.steps.n_792f841d2de3fb2a.json.descriptionId,
      task_a7734218180f964bbd34154b: data.steps.n_351e8c8fdc59fd92.pageRecords_2.pageRecords
    };
    const candidates_8_0 = observedInputs_8_0.task_ce4897bd04af61af1d5e751c;
    const descriptionId_8_0 = observedInputs_8_0.task_ef35a60c0385c825c28da011;
    const flattenedCandidates_8_0 = candidates_8_0.flatMap(item => item);
    const descriptionIdList_8_0 = [descriptionId_8_0];
    const requiredCondition_8_0 = m.lookup(flattenedCandidates_8_0, descriptionIdList_8_0, "reference") !== null;
    const pageRecords_8_0 = observedInputs_8_0.task_a7734218180f964bbd34154b;
    const flattenedPageRecords_8_0 = pageRecords_8_0.flatMap(item => item);
    const descriptionIdList2_8_0 = [descriptionId_8_0];
    const matchedRecords_8_0 = m.lookup(flattenedPageRecords_8_0, descriptionIdList2_8_0, "reference");
    const items_8_0 = (matchedRecords_8_0 => ({
      results: matchedRecords_8_0.map((item, index) => item.text),
      count: matchedRecords_8_0.length
    }))(matchedRecords_8_0);
    const text_8_0 = items_8_0.results.map(item => item);
    const descriptionId8PageRecords = {
      values: text_8_0
    };
    return {
      values: descriptionId8PageRecords.values
    };
  })()
  : (() => {
    const observedInputs_8_1 = {
    };
    const projectedValues_8_1_1 = [null];
    const descriptionId8SelectedValue = {
      values: projectedValues_8_1_1
    };
    return {
      values: descriptionId8SelectedValue.values
    };
  })();
  const values_3 = (valuesSelection_2).values;
  const values_4 = values_3;
  const valuesSelection_3 = ((data.steps.n_792f841d2de3fb2a.json.prixId !== null))
  ? (() => {
    const observedInputs_9_0 = {
      task_ce4897bd04af61af1d5e751c: data.steps.n_3c64eb58e6504189.value.candidates,
      task_7e20439768a0559d3b535776: data.steps.n_792f841d2de3fb2a.json.prixId,
      task_a7734218180f964bbd34154b: data.steps.n_351e8c8fdc59fd92.pageRecords_2.pageRecords
    };
    const candidates_9_0 = observedInputs_9_0.task_ce4897bd04af61af1d5e751c;
    const prixId_9_0 = observedInputs_9_0.task_7e20439768a0559d3b535776;
    const flattenedCandidates_9_0 = candidates_9_0.flatMap(item => item);
    const prixIdList_9_0 = [prixId_9_0];
    const requiredCondition_9_0 = m.lookup(flattenedCandidates_9_0, prixIdList_9_0, "reference") !== null;
    const pageRecords_9_0 = observedInputs_9_0.task_a7734218180f964bbd34154b;
    const flattenedPageRecords_9_0 = pageRecords_9_0.flatMap(item => item);
    const prixIdList2_9_0 = [prixId_9_0];
    const matchedRecords_9_0 = m.lookup(flattenedPageRecords_9_0, prixIdList2_9_0, "reference");
    const items_9_0 = (matchedRecords_9_0 => ({
      results: matchedRecords_9_0.map((item, index) => item.text),
      count: matchedRecords_9_0.length
    }))(matchedRecords_9_0);
    const text_9_0 = items_9_0.results.map(item => item);
    const prixId9PageRecords = {
      values: text_9_0
    };
    return {
      values: prixId9PageRecords.values
    };
  })()
  : (() => {
    const observedInputs_9_1 = {
    };
    const projectedValues_9_1_1 = [null];
    const prixId9SelectedValue = {
      values: projectedValues_9_1_1
    };
    return {
      values: prixId9SelectedValue.values
    };
  })();
  const values_5 = (valuesSelection_3).values;
  const url = (url => ({
    url: (url).href
  }))(data.inputs.task_4c61cbac0e00e1e9bdfe6a04);
  const values_7 = (values_2 !== null) && (values_4 !== null);
  const values_8 = values_2;
  return {
    __items: values_8,
    task_3f2631bc3b8fddf63032ee8c: values_4,
    task_47667bfc66e29c7ccf3db9bf: values_5,
    task_23c6ba04d46662d45f1b229d: url.url,
    task_a1e1eaf22ffbbaf655a19006: data.steps.n_5989b578adf4e097.response
  };
})()
