# Wave 9 — Completion report (toward Gate G9: Release)

**Phases:** p36–p37 · **Date:** 2026-08-01 · **Squad:** B + external firms

## What was built

Wave 9 is, by design, "largely external" — the playbook says so of Phase 36
in its own header. Two deliverables are genuinely producible here, and one of
them is arguably the most important customer-facing artifact in the
programme.

| Phase | Delivered | Location |
|---|---|---|
| **36** | Shared-responsibility model, with per-regulation split and evidence links | [`docs/compliance/shared-responsibility-model.md`](../../compliance/shared-responsibility-model.md) |
| **37** | This release-readiness assessment | this document |

## Verification

**792 automated tests green**, five TFMs building clean with warnings as
errors. No change to the code base this wave; the deliverables are documents,
and the tests they cite are the ones already passing.

## The shared-responsibility model

Phase 36 describes it as *"the document that prevents a customer from
believing the framework made them compliant"*, and that framing shaped how it
was written.

Three choices worth noting:

**Every control claim carries an evidence link to a named test.** An auditor
can be shown the mechanism rather than a claim. Controls that exist only as
contracts are **not listed** — they live in the per-wave "carried forward"
sections instead. A matrix that mixes intentions with evidence is worse than
one listing only evidence, because it teaches the reader to stop checking.

**The document states plainly that three of the four responsibility layers
are the customer's.** That ratio is not a limitation of EDPF; it is what
compliance consists of. Softening it would produce exactly the
misunderstanding the document exists to prevent.

**It ends with five questions a buyer should ask any framework vendor, and
answers them for EDPF — including the two EDPF currently fails.** The
independent cryptographic review is outstanding, and so is the LTS
commitment. A shared-responsibility model that only listed EDPF's strengths
would be marketing wearing a compliance document's clothes.

## Gate G9 — the honest assessment

Gate G9 requires: a clean pen-test report; all NFRs met and evidenced; design
partners in production; signed 1.0.0 packages published; support model
operational.

**None of the five is satisfied, and none can be satisfied from here.**

| Criterion | Status | Why it cannot be self-satisfied |
|---|---|---|
| Clean third-party pen-test report | Not started | Requires an external firm and a production-representative deployment |
| Independent cryptographic design review | Not started | Requires, by definition, someone who did not write the code |
| Independent architecture review | Not started | Same |
| Red-team exercise on tenant isolation and PHI exfiltration | Not started | Requires a red team. The adversarial suite is *my* attempt at the same attacks, which is not the same thing |
| SOC 2 Type I readiness, HIPAA gap assessment | Not started | Assessment activities performed by assessors |
| All NFRs met and evidenced | Not measured | Needs the load harness, pinned hardware and a target environment (Gate G7) |
| 30-day soak | Not started | Needs 30 days and an environment |
| Design partners in production | Not started | Needs design partners, who are a Phase 00 sponsor deliverable |
| Signed 1.0.0 packages | Not possible | Needs signing certificates and the licence text executed by counsel |
| Support model operational | Not started | Needs a support organization |

## On the red-team criterion specifically

The adversarial isolation suite covers twelve routes and passes. It would be
easy — and wrong — to present that as satisfying the red-team requirement.

It does not, for a structural reason: I wrote both the defences and the
attacks. A suite built by the person who built the boundary tests the attacks
that person thought of, and the entire value of a red team is the attacks
they did not. The suite is genuine evidence that twelve *known* routes are
closed. It is not evidence that a thirteenth does not exist.

The same reasoning applies to the independent cryptographic and architecture
reviews, and to Gate G8's external usability test. Four of the nine gates
have at least one criterion of this shape, and no amount of further work in
this session moves any of them.

## What a sponsor should take from this

The engineering that could be done without infrastructure, external parties
or elapsed time **has been done**, to the standard the document sets: 792
tests, 23 ADRs, five target frameworks, and a set of safety properties that
are structural rather than disciplinary.

What remains is not more of the same work. It is a different kind: standing
up infrastructure, engaging external firms, recruiting design partners,
executing legal text, and letting time pass. Those are sponsor and programme
activities, and the honest position is that they gate the release rather than
being gated by it.

**Gate G9: NOT passed.** The v0.9 engineering baseline is complete; the
release is not.

---

## Addendum — 2026-08-02

The figures above (792 tests, 23 ADRs) were accurate when this report was
written and are **left unchanged deliberately**: it is a record of what Wave 9
found, not a live dashboard. Editing them would misrepresent what was known at
the time.

Since then, eleven further phases from Appendices H and I have been worked —
**05b, 08b, 08c, 17c, 23d, 24b, 24f, 26f, 33b, 34b, 35b** — adding nine
packages and eleven ADRs. Current figures live in
[PROGRAMME-STATUS.md](../../PROGRAMME-STATUS.md), which is the document that is
kept current.

**None of it changes the Gate G9 assessment.** Every one of those phases was
constrained the same way this report describes: what could be built and
verified without infrastructure, external parties or elapsed time was built;
what could not was deferred with a stated reason. The four criteria that
cannot be self-satisfied — independent cryptographic review, independent
security and architecture review, a red-team exercise, and an external-team
usability test — are untouched by eleven more phases of engineering, and would
be untouched by eleven more after that.

Two of those phases are worth a sponsor's attention because they changed what
is *claimable* rather than adding capability:

- **Phase 05b** found that no metadata repository existed in `src/` at all,
  which the master document's own Appendix I.0 predicted as an ordering defect.
  The dynamic-query safety model had only test doubles to resolve against.
- **Phase 08b** found `IFieldMetadata.RequiredScope` declared, stored,
  published on the API surface — and read by nothing. A field-level access
  control that a reviewer would reasonably have believed was working.

Both were defects in work this report had already called complete. That is the
argument for the four outstanding review criteria stated more sharply than the
report itself puts it: **the person who wrote the code is the person least
able to find what they did not write.**
