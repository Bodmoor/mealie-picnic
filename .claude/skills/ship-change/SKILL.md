---
name: ship-change
description: The mealie-picnic change loop - ticket, branch off main, fix, push, PR. Use whenever the user reports a bug, asks for a feature, says "create a ticket", "branch", "fix", "push", "pr", or describes something odd in the app or its logs.
---

# Shipping a change to mealie-picnic

Every change lands the same way. Do the whole loop unless the user names a shorter one ("just a ticket", "no PR").

## 1. Ticket first

`gh issue create`. The body is for a human reading it cold in six months: what is wrong, **why** it is wrong, and what the fix has to preserve. Name the evidence — a log line, a file and line, a measured number — rather than describing a suspicion.

Ground it before writing it. Read the code the ticket is about; run the thing if you can. A ticket that reasons from a plausible-sounding guess costs a whole build-and-deploy cycle to disprove.

Done when: the ticket names the cause, not just the symptom.

## 2. Branch off `main`, freshly pulled

```
git checkout main && git pull --ff-only && git checkout -b <issue>-<slug>
```

Off `main` every time, unless the user says to stack on the previous branch.

## 3. Fix, and **pin** it

A test that would pass with the bug still present is decoration. Pin the fix: revert it (copy the file aside, `git checkout --` it, splice the old block back), watch the new test fail, restore. **Never `git stash` for this** — a stash/pop cycle silently dropped two edits in the session that wrote this skill. Copy files aside instead.

Two of this repo's tests were written before that discipline and passed either way: one asserted `no hx- in this tag` against a constant written in the same commit. Assert the property that matters, not the markup you just typed.

Where the trap is repo-specific — a Razor gotcha, a CI-only failure, a shared static in the tests — read [`TRAPS.md`](TRAPS.md) before spending a cycle rediscovering it.

Done when: `dotnet test` passes, **and** you have watched the new test fail without the fix.

## 4. Documentation lives with the change

The README carries the architecture and a bullet per test class. If the change alters either, it changes in the same commit. A README describing behaviour the code no longer has is worse than one that says nothing — this repo shipped a README claiming a log scope worked while it recorded every request as anonymous.

## 5. Commit, push, PR

The commit message and the PR body are for people, in ordinary prose. Say what was wrong, what changed, and which decisions were judgement calls rather than facts — especially the ones you would defend if challenged. Name what you deliberately did **not** do.

```
git push -u origin <branch>
gh pr create --base main --head <branch> --title "..." --body "..."
```

Close the ticket from the body (`Closes #N`).

Then watch CI: `gh pr checks <pr>`. Green is part of the loop, not an afterthought — CI in this repo has caught two failures that passed locally, both of them environment-dependent (see [`TRAPS.md`](TRAPS.md)).

Done when: both checks pass, or you have fixed what they caught.

## Reporting back

State plainly what changed and what it cost. If you left something out, say so and why. If a claim is unverified — a UI change you have not seen rendered, a fix you could not reproduce locally — say that too, in the PR body as well as to the user.
