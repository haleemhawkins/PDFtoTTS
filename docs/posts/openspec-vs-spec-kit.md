# My specs were lying to me and the validator said they were fine

I run a spec-driven workflow on a side project — a self-hosted PDF/EPUB reader
that speaks documents aloud and highlights each word as it's spoken. Three Python
GPU workers, a .NET API, a React frontend. Six capability specs, forty-nine
requirements, all written before the code.

Last week I audited every one of those requirements against the code. Seven of
them were wrong. Not vague — wrong.

Then I ran the validator. Ten out of ten passed.

That gap is the whole point of this post, and it's the thing to understand before
you pick between the two spec-driven tools people are actually using: OpenSpec and
GitHub's Spec Kit.

## What was actually wrong

A sample, so this isn't abstract.

One requirement said EPUB extraction used a library called EpubNet with a
CFI-style locator. It uses VersOne.Epub and AngleSharp, and the locator is
`{spineHref}#{ordinal}`. Someone — me — wrote the intent before writing the code,
picked a different library during implementation, and never went back.

One said the SignalR hub let clients "join only sessions they own." There is no
such check. There is no auth in the app at all. That sentence had been sitting in
a spec for two months describing a security control that does not exist. If I ever
put that service on a network, the spec would have told me I was fine.

One said forced alignment receives terminator-free text. It doesn't. The
orchestrator hands the exact same chunk text to the synthesizer and the aligner,
sentence terminators included. That one had been true when it was written, and
quietly stopped being true when the code changed to re-attach terminators so the
voice would get question intonation.

Notice the pattern. One was aspiration that never landed. One was a decision made
during implementation that never flowed back. One was true and rotted. Those are
three different failure modes and no linter catches any of them.

## The structural difference

Both tools do spec → plan → tasks → implement. That's not where they differ.

Spec Kit gives you `specs/001-feature-name/` — a numbered folder per feature, with
a git branch auto-created to match, holding `spec.md`, `plan.md`, `tasks.md`, and
depending on the command, `research.md`, `data-model.md`, `contracts/`, and
`quickstart.md`. It's chronological. Feature 001, then 002, then 003.

OpenSpec splits it in two. `openspec/specs/<capability>/spec.md` holds living
capability specs — mine are document-processing, tts-synthesis, forced-alignment,
reader-backend, reader-frontend, background-audio. Those answer "what does this
system do right now." Separately, `openspec/changes/<name>/` holds a proposed
change as a *delta*: blocks headed `## ADDED Requirements` or `## MODIFIED
Requirements` that apply against those capabilities. When the change ships, you run
`openspec archive`, the deltas fold into the capability specs, and the change moves
to `changes/archive/`.

That difference matters more than it sounds. My iOS background-audio work touched
three capabilities at once — the HLS endpoint in the backend, the media-element
engine in the frontend, and a new background-audio capability. In OpenSpec that's
one change fanning out into three deltas. In Spec Kit it's one feature folder, and
the cross-cutting picture is something you reconstruct by reading history.

After ten features, Spec Kit gives you ten folders and no single document that says
what the system does. OpenSpec gives you six capability specs that always claim to
be current.

*Claim* being the operative word.

## The thing Spec Kit refuses to decide

Here's what surprised me. Spec Kit has a docs page called
[Spec Persistence Models](https://github.com/github/spec-kit/blob/main/docs/concepts/spec-persistence.md)
that names three ways to handle specs after requirements change — flow-forward
(each feature folder is immutable history), living spec (`spec.md` is the contract,
plan and tasks are disposable derivations), and flow-back (edit wherever the
insight lands, reconcile afterward).

Then it says: "None is the default, and none is required by Spec Kit... The model
is a team convention, not a CLI setting."

That's a defensible call. It's also the exact decision most teams will never make
explicitly, and the one that determines whether your specs are worth reading in six
months.

OpenSpec makes the decision for you. Living capability specs, deltas for changes,
an archive command that reconciles them. It's opinionated in the way a tool should
be opinionated: about the thing you'd otherwise get wrong by default.

## Why mine drifted anyway

Because I never ran the archive.

Four changes. Zero archived. One of them had been marked complete since July and
was still sitting in `changes/`. The archive step is the one automated
reconciliation OpenSpec offers, and I'd skipped it every single time — so the
capability specs and the change deltas both sat there, each drifting from the code
independently.

I'd also written eleven `ADDED` requirements against one `MODIFIED`. Almost pure
greenfield accretion. Which means I wasn't using the delta machinery for what it's
good at — the machinery earns its keep when you're *changing* requirements that
already exist, and I'd mostly been piling new ones on.

So the tool was fine. The habit was missing.

## The part that generalizes

`openspec validate` checks structure. Does every requirement have at least one
scenario, is the delta format right, do the headers parse. It does not and cannot
check whether a sentence about your code is true. Spec Kit's `/speckit.analyze` is
closer — it's cross-artifact consistency, spec against plan against tasks — but
that's still artifacts agreeing with each other, not artifacts agreeing with the
repository.

Neither tool verifies specs against code. That audit is manual, and it is the
single highest-value thing I've done to that project's docs. Seven wrong
requirements out of forty-nine, in a project where I wrote every line myself and
would have told you the specs were accurate.

If you're running spec-driven development with an agent, budget for that audit. Not
because the tool is bad — because a spec that has never been checked against the
code is a confident, well-formatted, machine-validated guess.

## What I'd tell you to pick

If your work cuts across subsystems and you want a document that says what the
system does today, OpenSpec. The capability-plus-delta model is the right shape and
you get an archive command that keeps it honest — as long as you actually run it.

If you want the richer per-feature planning artifacts, a constitution file holding
project-wide rules, and support for basically every coding agent that exists, Spec
Kit. Then go read the persistence models page and pick one deliberately, because
nothing in the tool will pick for you.

Either way, put a recurring calendar item on reading your specs against your code.
That's the step neither tool does, and it's the one that was load-bearing.

---

*The project is [PDFtoTTS](https://github.com/haleemhawkins/PDFtoTTS) if you want
to see the specs, including the seven I had to fix.*
