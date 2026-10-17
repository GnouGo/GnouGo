(() => {
  const success = (data.steps.n_62737a68773ca42a.response).success;
  const selection = ((success === true))
  ? (() => {
    const selection1SelectedValue = (data.steps.n_62737a68773ca42a.response).observationSnapshot;
    const selection1SelectedValue_2 = selection1SelectedValue !== null;
    return {
      selectedValue: selection1SelectedValue_2
    };
  })()
  : (() => {
    const selection1SelectedValue_3 = false;
    return {
      selectedValue: selection1SelectedValue_3
    };
  })();
  const condition = (selection).selectedValue;
  const pages = (data.steps.n_62737a68773ca42a.response).observationSnapshot.pages;
  const observedValue = pages;
  const items = (observedValue => ({
    results: observedValue.map((item, index) => (item).records),
    count: observedValue.length
  }))(observedValue);
  const pageRecords = (items.results).map(item => item);
  return {
    pageRecords_2: {
      pageRecords
    }
  };
})()
