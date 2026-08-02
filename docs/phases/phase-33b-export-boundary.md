# Phase 33b — Enterprise Reporting & Export

**Status:** Security boundary complete; rendering, definition language and distribution deferred
**Gate contribution:** G8 (Adoptable)
**ADR produced:** [ADR-033 — An export is a bulk read, a travelling artefact, and a program](../adr/ADR-033-export-is-a-security-boundary.md)

## The framing

Reporting is usually treated as a formatting problem. It is **three security
problems wearing a formatting problem's clothes**, and this phase builds the
three.

| File | Contents |
| --- | --- |
| [`DelimitedTextWriter.cs`](../../src/Edpf.Reporting/DelimitedTextWriter.cs) | Formula-injection neutralisation and correct CSV quoting |
| [`ExportGuard.cs`](../../src/Edpf.Reporting/ExportGuard.cs) | Field authorization at the export point, mandatory caps, artefact classification |

## 1. The output is a program

A spreadsheet cell whose value begins `=`, `+`, `-` or `@` is **evaluated when
the file is opened** (CWE-1236). Excel's `DDE` and `WEBSERVICE` functions turn
that into remote content retrieval and, historically, command execution.

The attack needs no sophistication: someone types `=cmd|'/c calc'!A1` into a
free-text notes field, it sits inertly in the database for months, then a
monthly report exports it and a finance manager double-clicks the file.

There is a particular irony available here. EDPF ships ADR-026 — a formula
engine with **no scripting host, ever**, precisely so user-authored expressions
cannot execute — and would then have emitted CSV that executes user-authored
expressions on someone else's machine.

**Quoting is not sufficient and is not treated as if it were.** A CSV field
written `"=1+1"` is still parsed as a formula: the quotes are CSV syntax,
consumed before the cell value is interpreted. Neutralisation has to change the
value, so a leading dangerous character gets an apostrophe prefix.

Tab and carriage return are in the list because several importers strip leading
whitespace *before* deciding whether the remainder is a formula, which puts
`\t=cmd` straight back into the dangerous case.

**This changes the data, and the cost is stated rather than hidden.** A part
number legitimately beginning `-` exports as `'-`. That is a data-quality
problem; an executing cell is code running on the recipient's machine. Those
differ in kind, not degree.

## 2. An export is a bulk read

ADR-031 put field authorization in the query compiler and named *"a second
enforcement point appears — bulk export"* as its own revisit trigger. **This is
that trigger firing**, and ADR-031 now records that it fired and how.

One deliberate difference from the query compiler: an export **withholds** an
unreadable column and records it, rather than refusing the whole request. A
report definition is a long-lived artefact edited by one person and run by
many; failing the entire run because one recipient lacks one column means the
report stops working for most of the organisation — and the response to that is
invariably to grant everyone the permission, which is worse than the column
being absent.

An **unknown** column still fails, because silently dropping it produces a
report missing a column nobody notices is missing.

Withheld columns appear in the manifest for the same reason `ValuesWithheld`
appears on a quality profile (ADR-028): a recipient who does not know a column
was removed will read the export as complete, and act on it.

## 3. The artefact leaves the building

Everything ADR-028 says about quality reports applies with more force here — a
quality profile *describes* the data, an export *is* the data.

So: **there is no unlimited export.** The requested cap is clamped, never
honoured — a cap the caller can raise is not a cap, and BRL-018 makes the same
choice for page size. An unbounded export over a multi-tenant clinical dataset
is an exfiltration channel that looks exactly like a report; the specification
makes precisely this argument about unbounded GraphQL.

And **the artefact inherits the highest classification it contains.** A CSV
with one PHI column is a PHI artefact. Resolved through the same
`IDataProtectionPolicy` every other subsystem consults, so the file's handling
cannot drift from the data's.

## Exit criteria

| Criterion | Status |
| --- | --- |
| Export cannot emit executable cells | Met |
| Field authorization enforced at the export point | Met — ADR-031's trigger acted on |
| Mandatory, non-raisable row cap | Met |
| Export artefact carries its classification and an audit manifest | Met |
| Correct delimited-text quoting | Met |
| PDF/A, Word, Excel rendering | **Deferred** |
| Report definition language, scheduling, distribution | **Deferred** |

## Deferred, with reasons

**Rendering.** PDF/A, Word and real Excel output need a rendering dependency
whose licence must clear the ADR-009 gate, and whose output can only be
verified by opening it. What is delivered is the layer where the security
properties live — the same split Phase 17c made between barcode *encoding* and
barcode *printing*.

**Report definition language, scheduling and distribution.** A report that
emails itself to a list is a distribution channel with its own access-control
questions — who may subscribe, what happens when a recipient's permissions
change, whether a scheduled export re-evaluates authorization at each run. Those
are real questions and answering them in passing would be worse than leaving
them open.

**Persisting the audit record.** `ExportManifest` is produced; wiring it to
`IAuditWriter` is a composition step the adopter performs, and doing it here
would presume an audit sink that has not been configured.

## What this does not claim

Neutralisation is **spreadsheet-oriented**. It addresses the formula
interpretation of four leading characters plus two whitespace forms. It is not
a general-purpose sanitiser, and it does nothing for a consumer that executes
shell metacharacters or interprets the file some other way. The list is
empirical rather than derived, and ADR-033 records that it will need to grow.
