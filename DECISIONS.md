# Decisions

## Where the spec was unclear, contradictory, or silent

- **Attachment deduplication conflicts with intact families (§3.2 vs §3.5).** The same file can legitimately be attached to two different emails, but one CSV row can have only one `ParentID`. I made the family the unit of retention: all descendants of a retained email are emitted, even when an attachment hash occurs in another retained family. I rejected suppressing or reparenting the second attachment because either option makes an email incomplete. Duplicate attachments are removed when their entire duplicate email family is removed.
- **Batch-wide cross-mailbox deduplication conflicts with self-contained custodian sets (§3.3–3.4 vs §4).** A row has one `Custodian`, so the current output model cannot both emit one global copy and retain that email in every custodian's independently severable set. I implemented the specifically requested batch-wide deduplication and retain the family whose top-level email has the ordinally lowest `DocId`. This is deterministic but loses the other custodians' provenance. Alternatives were retaining one copy per custodian (not batch-wide deduplication) or extending the load format with multi-custodian provenance (forbidden by the fixed contract).
- **“Same email” has no formal identity algorithm.** I compare the UTC instant, case-insensitive trimmed sender, a case-insensitive/order-independent/distinct recipient set, Unicode-NFC subject, and Unicode-NFC body after CRLF/CR-to-LF normalization. I intentionally exclude raw hash, file name, size and custodian because mailbox copies may differ in those fields. Subject and body remain case- and whitespace-sensitive to avoid collapsing distinct messages. The input has no Message-ID, CC, BCC or complete headers, so false positives remain possible.
- **Duplicate email families may contain different attachments.** I retain one complete source family rather than merging families. Merging would create a family that never existed and could attach content to the wrong email. The deterministic lowest-`DocId` representative may not be the most complete copy; product guidance is needed if collection quality differs.
- **Suppression count is not defined for a duplicate family.** I count every omitted document, not only the top-level email, so `documents in = documents out + documents suppressed`.
- **Stable row order is not specified, but byte-identical output is required (§6.1).** I order by top-level `DocId`, then depth, then `DocId`. This keeps parents before descendants and is independent of JSON order. The lowest root `DocId` similarly wins each duplicate group.
- **CSV dialect and encoding are not stated.** I use RFC 4180-style quoting (quote fields containing comma, quote, CR or LF; double embedded quotes), UTF-8 without BOM, LF endings and a final LF. This preserves arbitrary names while retaining the starter's line-ending behavior.
- **Fractional date handling is not stated.** Conversion preserves the represented instant, then formatting omits (effectively truncates) fractional seconds as required by the exact output pattern.
- **Malformed family behavior is not specified.** I fail fast with a descriptive exception for duplicate/empty IDs, missing parents, cycles, unsupported type/parent combinations, missing email identity fields and invalid dates instead of silently emitting corrupt rows.

## What I changed and why

- Replaced document-by-document hash suppression with family-aware processing. This fixes INGEST-4471 by ensuring a retained email always carries every descendant, including nested attachments.
- Added semantic cross-mailbox email deduplication with deterministic representative selection.
- Kept batch-wide SHA-256 deduplication for loose files. Hash comparison is case-insensitive because hexadecimal SHA-256 values are semantically case-insensitive.
- Resolved arbitrary `parentId` chains to the top-level family and inherited that email's date on every descendant.
- Converted dates to UTC in the exact `yyyy-MM-dd'T'HH:mm:ss'Z'` format.
- Added deterministic row ordering, proper CSV escaping and streamed file output to avoid constructing a second full CSV string in normal CLI use.
- Added regression, cross-mailbox, nested-family, determinism, CSV-contract and sample-batch tests.

## What I didn't get to

- The CLI now deserializes directly from the input stream rather than retaining a second full JSON string. The deduper still holds document, family and output indexes in memory. The algorithm is linear apart from deterministic sorting, but memory profiling at the stated two-million-document upper bound is still needed.
- I did not change the fixed load-file schema to preserve provenance for every custodian; that requires a product/ingest contract decision.
- I did not add Message-ID/header-based identity because those fields are absent from the input contract.
- I did not add fuzz/property tests for CSV or email canonicalization, nor performance benchmarks.

## What I'd do next

- Ask product/legal to resolve the global-deduplication versus severable-custodian contradiction and, if needed, define a separate provenance manifest or many-to-many custodian mapping.
- Establish a versioned canonical email identity contract, ideally including Message-ID and normalized address/header parsing rather than raw strings.
- Define policy for conflicting duplicate families (prefer most complete, quarantine, or merge only with provenance) and add fixtures.
- Profile a representative two-million-document batch; then consider streaming JSON ingestion, external sorting/partitioning, and streamed row production.
- Add property-based tests, malformed-input CLI error handling, and operational metrics for duplicate groups and rejected batches.

## If you reimplemented in another language

- Not applicable. I kept .NET 8/C#, the existing CLI contract and the exact fixed load-file columns.

## Working with the AI

- The AI correctly traced the missing attachment to the global per-document hash set, identified the multi-level family/date and CSV-escaping defects, and highlighted the two irreconcilable specification conflicts before code was changed.
- I adopted its recommendation to make email families atomic and to select representatives/order output deterministically. I narrowed the identity normalization so subject/body case and whitespace remain significant; aggressive text normalization would risk false-positive suppression.
- The AI initially inferred expected sample output rather than treating it as authoritative. I verified that inference against `SPEC.md`, the source records and automated tests. A teammate should not accept the identity heuristic or custodian trade-off as settled product policy merely because tests pass.
- The session transcript is in `AI_TRANSCRIPT.md`.
