(() => {
  const candidates = data.steps.n_3c64eb58e6504189.value.candidates;
  const requiredCondition = candidates.flatMap(item => item) !== null;
  const offeredFactCandidates = candidates.flatMap(item => item);
  return {
    offeredFactCandidates
  };
})()
