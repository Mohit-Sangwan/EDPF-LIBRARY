# ADR-033 — An export is a bulk read, a travelling artefact, and a program

- **Status:** Accepted
- **Date:** 2026-08-02
- **Phase:** 33b — Enterprise Reporting & Export
- **Acts on:** [ADR-031](ADR-031-field-authorization.md)'s revisit trigger, *"a second enforcement point appears — bulk export"*
- **Related:** [ADR-028](ADR-028-profiling-discloses-nothing.md) (artefacts that travel), [ADR-025](ADR-025-metadata-resolved-fields.md), [ADR-026](ADR-026-formula-sandbox.md)

## Context

Reporting is usually treated as a formatting problem. It is three security
problems wearing a formatting problem's clothes.

**An export is a bulk read.** ADR-031 put field-level authorization in the
query compiler and recorded, as an explicit revisit trigger, that any other
path to the data needs the same check or a stated exemption. Reporting is
that other path, and it is the one that reads *everything*.

**The artefact leaves the building.** Everything ADR-028 says about quality
reports applies with more force here: a quality profile *describes* the data,
an export *is* the data. It gets emailed, put on a share, copied to a laptop,
and attached to a ticket.

**The output is a program.** A spreadsheet cell whose value begins `=`, `+`,
`-` or `@` is evaluated when the file is opened (CWE-1236). Excel's `DDE` and
`WEBSERVICE` functions turn that into remote content retrieval and,
historically, command execution. The attack needs no sophistication: someone
types `=cmd|'/c calc'!A1` into a free-text notes field, it sits inertly in the
database for months, then a monthly report exports it and a finance manager
double-clicks the file.

There is a particular irony in shipping ADR-026 — a formula engine with no
scripting host, precisely so user-authored expressions cannot execute — and
then emitting CSV that executes user-authored expressions on someone else's
machine.

## Decision

**1. Cells that would execute are neutralised, and quoting is not relied on.**
A leading `=`, `+`, `-`, `@`, tab or carriage return gets an apostrophe prefix,
which every major spreadsheet treats as "the rest is text". Tab and CR are
included because several importers strip leading whitespace *before* deciding
whether the remainder is a formula.

Quoting is **not** sufficient and is not treated as if it were: a CSV field
written `"=1+1"` is still parsed as a formula, because the quotes are CSV
syntax consumed before the cell value is interpreted.

**2. Neutralisation changes the data, and that cost is accepted explicitly.**
A part number legitimately beginning `-` exports as `'-`. This is a
data-quality problem; an executing cell is code running on the recipient's
machine. Those are different in kind, not degree. The writer exposes a flag so
that a deployment which genuinely round-trips exports can turn it off — as a
reviewable decision, not a convenience.

**3. Field authorization applies at the export point.** This is the second
enforcement point ADR-031 asked for.

**4. An export withholds rather than refuses, unlike the query compiler.** A
report definition is a long-lived artefact edited by one person and run by
many. Failing the whole run because one recipient lacks one column means the
report stops working for most of the organisation — and the response to that
is invariably to grant everyone the permission, which is a worse outcome than
the column being absent. Withheld columns are **recorded**, because a recipient
who does not know a column was removed will read the export as complete.

An **unknown** column still fails: silently dropping it produces a report
missing a column nobody notices is missing.

**5. There is no unlimited export.** The cap is clamped, never honoured as
requested — a cap the caller can raise is not a cap, and BRL-018 makes the same
choice for page size. An unbounded export over a multi-tenant clinical dataset
is an exfiltration channel that looks exactly like a report; the specification
makes the same argument about unbounded GraphQL.

**6. The artefact inherits the highest classification it contains.** A CSV with
one PHI column is a PHI artefact, and its storage, transport and retention must
be governed accordingly. Resolved through the same `IDataProtectionPolicy`
every other subsystem uses, so the file's handling cannot be decided separately
from the data's.

## Consequences

### Accepted costs

- **Exported values are not byte-identical to stored values.** Any consumer
  re-importing an export must strip the text marker, and one that does not will
  see altered strings. This is the trade named in decision 2.
- **Withholding produces silently narrower reports.** A recipient who ignores
  the manifest sees a well-formed export with a column missing. The manifest is
  the mitigation and it depends on someone reading it.
- **The default cap will be too low for someone.** Raising it is a
  configuration change they will make knowingly, which is the point.

### What this does not claim

- **No rendering.** PDF/A, Word and real Excel output are not implemented.
  They need a rendering dependency whose licence must clear ADR-009, and whose
  output can only be verified by opening it. What is delivered is the layer
  where the security properties live.
- **No report definition language, scheduling, or distribution.** A report that
  emails itself to a list is a distribution channel with its own access-control
  questions, and it is not here.
- **Neutralisation is spreadsheet-oriented.** It addresses the formula
  interpretation of `=`, `+`, `-` and `@`. It is not a general sanitiser and
  does nothing for, say, a consumer that executes shell metacharacters.
- **The export audit record is produced, not persisted.** `ExportManifest` is
  the record; wiring it to `IAuditWriter` is the adopter's composition step and
  is not done here.

## Revisit triggers

- **A consumer requires byte-exact round-tripping of exports.** Then the answer
  is a format that is not also a programming language — JSON, Parquet — rather
  than disabling neutralisation on CSV.
- **Withheld columns cause a decision to be made on an incomplete export.**
  The manifest is not being read, and the omission needs to be visible in the
  file itself rather than beside it.
- **A third read path appears** — a streaming API, a data-warehouse feed, a
  direct provider call. ADR-031's trigger fires again, and this is now the
  second instance of it firing.
- **A spreadsheet adds a new leading character with evaluation semantics.**
  The list is empirical, not derived, and it will need to grow.
