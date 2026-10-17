(() => {
  const success = (data.steps.n_62737a68773ca42a.response).success;
  const checkedRecord = (condition => condition === true || condition === "true")(success === true) ? (() => {
    const predicate1Result1 = {
      value: (data.steps.n_62737a68773ca42a.response).observationSnapshot
    };
    const predicate1Result2 = {
      value: predicate1Result1.value !== null
    };
    return {
      n_92b46a869b246c91: predicate1Result1,
      n_496ac96c9b178654: predicate1Result2
    };
  })() : (() => {
    const predicate1Result3 = {
      value: false
    };
    return {
      n_341937c017c6f5e7: predicate1Result3
    };
  })();
  const condition = {
    value: m.select(checkedRecord, [["n_496ac96c9b178654", "value"], ["n_341937c017c6f5e7", "value"]], false)
  };
  const requiredCondition = condition;
  const pages = (data.steps.n_62737a68773ca42a.response).observationSnapshot.pages;
  const checkedRecord_2 = pages;
  const items = (collection => ({
    results: collection.map((item, index) => (item).records),
    count: collection.length
  }))(checkedRecord_2);
  const pageRecords = (items.results).map(item => item);
  return {
    pageRecords_2: {
      pageRecords
    }
  };
})()
