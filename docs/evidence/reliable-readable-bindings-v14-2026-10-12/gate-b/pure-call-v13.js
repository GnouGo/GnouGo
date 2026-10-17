(() => {
  const success = (data.steps.n_db5a1e8bb35c43c6.response).success;
  const checkedRecord = (condition => condition === true || condition === "true")(success === true) ? (() => {
    const predicate6Result2 = (() => {
      const predicate6Values1Inputs = {
      };
      const predicate6Values1Value1 = {
      };
      const predicate6Values1Result = "success";
      return {
        outputs: {
          completion_status: predicate6Values1Result
        }
      };
    })();
    return {
      n_19b165bf364539b8: predicate6Result2
    };
  })() : (() => {
    const predicate6Result4 = (() => {
      const predicate6Values3Inputs = {
      };
      const predicate6Values3Value1 = {
      };
      const predicate6Values3Result = "failed";
      return {
        outputs: {
          completion_status: predicate6Values3Result
        }
      };
    })();
    return {
      n_6a6b5f8258190f00: predicate6Result4
    };
  })();
  const completionStatus = m.select(checkedRecord, [["n_19b165bf364539b8", "outputs", "completion_status"], ["n_6a6b5f8258190f00", "outputs", "completion_status"]], false);
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
