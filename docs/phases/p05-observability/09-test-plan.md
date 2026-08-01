# Phase 05 — Test plan: the ten adversarial redaction routes

Phase 05 §⑤ requires attempting to log a PHI-bearing object by ten different
routes and asserting zero PHI escapes in all ten. The fixture is a `Patient`
carrying a PII family name and a PHI medical record number, plus a
deliberately helpful `ToString()` — the shape that leaks in real codebases.

| # | Route | Test | Outcome |
|---|---|---|---|
| 1 | Direct object | `Redact_DirectObject_LeaksNothing` | Blocked |
| 2 | Nested inside another object | `Redact_NestedObject_LeaksNothing` | Blocked |
| 3 | Exception message | `Redact_ExceptionMessage_LeaksNothing` | Blocked |
| 3b | Inner exception message | `Redact_InnerExceptionMessage_LeaksNothing` | Blocked |
| 4 | Exception `Data` payload | `Redact_ExceptionDataPayload_LeaksNothing` | Blocked |
| 5 | `ToString()` | `Redact_ToStringOutput_LeaksNothing` | Blocked — the redactor projects members and never calls `ToString()` on a complex type |
| 6 | Structured log property | `Redact_StructuredProperty_LeaksNothing` | Blocked |
| 7 | Logging scope dictionary | `Redact_ScopeDictionary_LeaksNothing` | Blocked |
| 8 | Collection | `Redact_Collection_LeaksNothing` | Blocked |
| 9 | Anonymous-type projection | `Redact_AnonymousProjection_LeaksNothing` | Blocked |
| 10 | `SecretValue`, at any depth | `Redact_SecretValue_LeaksNothing` | Blocked |

## The leak this suite caught

Route 3 failed on first run. The redactor sanitised exception messages —
escaping newlines to prevent log forging — but still emitted their text, and
a domain exception had interpolated the medical record number straight into
its message. The rendered output contained the MRN.

The fix was to the code, not the test: **exception messages are now
surrendered by default**, keeping the exception type (which is what makes the
entry actionable) and relying on the correlation id for investigation. Types
whose messages are contractually code-only opt in via the message-safe
registry, which is how the Phase 18 taxonomy will register itself. That
behaviour is now pinned by `Redact_RegisteredSafeExceptionType_KeepsItsMessage`.

This is the phase working as designed: the adversarial suite exists precisely
because reasoning about redaction is unreliable and testing it is not.

## Supporting properties

Redaction must not be so blunt that logs stop being useful — an unclassified
operational field still comes through (`Redact_SafeMembers_ArePreserved`).
`CarriesClassifiedData` detects PHI both directly and through nesting, which
is the check rule EDPF0005 performs at build time.

**Log injection** is neutralised: newline, carriage-return, tab and control
characters are escaped, so a value cannot forge additional log entries
(`RedactText_ForgedLogEntry_CannotBreakOutOfItsField`).

**Robustness**, because a logging pipeline must never be the thing that takes
a process down: a self-referencing object graph terminates rather than
overflowing the stack; a property getter that throws yields
`[REDACTED:unreadable]` instead of propagating; a 500-element collection is
truncated, because a log line is not a data export.

## The stated boundary

`Redact_ToStringOutput_LeaksNothing` also asserts what the redactor does
**not** do: if a caller has already stringified a domain object, the resulting
string is just a string and no library can un-ring that bell. Rule EDPF0005 —
the analyzer that makes logging a classified type a build error — is what
closes that route. The test asserts the boundary explicitly rather than
implying a guarantee that does not exist.
