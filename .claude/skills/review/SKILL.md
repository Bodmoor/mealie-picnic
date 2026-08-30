---
name: review
description: Review mealie-picnic - either a sweep of the codebase that ends in tickets, or an independent check of an open PR. Use when the user asks to review the app, review a PR, or asks what state the code is in.
---

# Reviewing

Two branches. Pick by what the user asked for.

## A sweep of the codebase

Measure before judging. LOC per file, test count, CI config, dependency list,
`TODO` markers, `dotnet list package --vulnerable`. State the numbers.

Then look where the risk actually is, which in this app is not evenly spread:

- **`/api/basket` spends real money.** Anything it decides deserves a test.
- **It is internet-facing.** Anonymous endpoints, error bodies, log contents,
  and anything an unauthenticated caller can make the app do.
- **Silent failures.** A swallowed exception, a client-side handler that covers
  one case, a string `Replace` that does nothing when the markup changes — this
  codebase has shipped all three, and none announced itself.

Say plainly what is strong as well as what is not, and where the evidence is
thin, say that instead of padding.

End with tickets — one per finding, each with a concrete refactor rather than a
complaint. Group the small ones. A finding with no suggested change is an
observation, not a review.

## An independent check of a PR

Dispatch a subagent with **no context from this conversation** and tell it so.
The value is that it did not write the code and cannot inherit your reasoning
about why it is fine.

Give it: the PR number, the repo path, a warning not to disturb the working
tree, and — most importantly — instructions to verify the claims rather than
read them. Ask it to run the suite itself, to check whether the new tests can
actually fail, and to be blunt.

Then act on what comes back. It has been right about real defects here, and
about tests that could not fail. Where it is wrong, say so plainly rather than
deferring — but check before deciding it is wrong.
