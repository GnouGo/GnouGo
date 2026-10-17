(() => {
  const success = (data.steps.n_db5a1e8bb35c43c6.response).success;
  const completionStatusSelection = ((success === true))
  ? (() => {
    const observedInputs_6_0 = {
    };
    const projectedValues_6_0_1 = {
    };
    const completionStatus_6_0 = "success";
    const selection6SelectedValue = {
      completion_status: completionStatus_6_0
    };
    return {
      completionStatus: selection6SelectedValue.completion_status
    };
  })()
  : (() => {
    const observedInputs_6_1 = {
    };
    const projectedValues_6_1_1 = {
    };
    const completionStatus_6_1 = "failed";
    const selection6SelectedValue_2 = {
      completion_status: completionStatus_6_1
    };
    return {
      completionStatus: selection6SelectedValue_2.completion_status
    };
  })();
  const completionStatus = (completionStatusSelection).completionStatus;
  const filePath = (data.steps.n_db5a1e8bb35c43c6.response).filePath;
  const homeErrors = (data.steps.n_d2b08649ef8b1e39.json).home_errors;
  const searchErrors = (data.steps.n_b23b3d2c5a6a7e42.json).search_errors;
  const productError = [homeErrors, searchErrors, data.steps.n_057d824b69e5f524.outputs.product_error];
  const productsXlsxPath = {
    products_xlsx_path: filePath,
    observed_errors: productError.flatMap(item => item)
  };
  return {
    completionStatus_2: {
      completion_status: completionStatus
    },
    productsXlsxPath
  };
})()
