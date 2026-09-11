# CodeReview preparation regression

`CodeReviewMatchingCatalog.json` contains the 42 public catalog cards in the
saved CodeReview operation `op9` candidate boundary. It contains no user intent,
private execution responses, workspace paths, credentials, or encrypted records.
The test reconstructs every original card after context sharing and checks the
reduction using the production input estimator.

The full private comparison remains in the benchmark tenant's encrypted evidence
session `codereview-bc72dd6`; the source session was only read. Its original
revision was 117. Source snapshot fingerprint:
`3393151ba9df4d24ccb6d583d0a297b0d9746daa8972d0ac85955101f25a4d71`.
Discovery fingerprint:
`792eebc5c700c51dd30fa69fd4680dee8b65fb8b8bc35ed9611bd2a61762fc43`.

At the same matching checkpoint, `bc72dd6` required 12,810 estimated input
tokens. Lossless sharing and UTF-8 transport reduced it to 11,820. Restricting
matching evidence to intrinsic capability requirements reduced it to 11,721;
compiler-owned structure evidence remains in the governing behavior contract.
Factoring repeated exact catalog fragments reduced the same request to 11,438.
That intermediate request omitted governing policies from matching. The current
request retains required policies and distinguishes workflow evidence from intrinsic
capability requirements; it fit at 11,837 tokens without losing that evidence.
Sharing the required-policy classification once, while retaining each policy ID
and exact description, reduces the current request to **11,410 tokens**.
The candidate IDs and the 2,644-character response schema are unchanged.
These are preparation checkpoint measurements, not end-to-end call savings.
