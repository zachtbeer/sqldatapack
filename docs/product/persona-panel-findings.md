# Persona panel findings

Twelve simulated .NET developers, four experience levels, ten work environments, each shown a
SqlDataPack 2.0.0 spec sheet cold (CLI live, Aspire integration live, row add/edit/delete working)
and asked where they would use it, what would stop them, and what the README failed to tell them.

**Caveat that matters:** the panel reacted to features that do not exist yet. The CLI, the Aspire
integration, and row add/delete were all presented as working. Findings are sorted below into what
applies to the product as it is and what is evidence about what to build next.

## Verified capability gaps this surfaced

Checked against the source, not assumed.

| Finding | Reality | Status |
| --- | --- | --- |
| Self-referencing foreign keys | `ImportPlanner.cs:16` throws. "Exclude the table or prepare the target schema before import." | **Hard failure, undocumented.** Nothing in `website/docs/` mentions it. |
| Foreign-key cycles | `ImportPlanner.cs:29` throws, naming the tables in the cycle. | Same. Good error, no docs. |
| DML triggers during import | `SqlDataPackImporter.cs:212` uses `SqlBulkCopyOptions.KeepIdentity \| KeepNulls \| UseInternalTransaction`. `FireTriggers` is not set, so triggers do not fire. | **Good news, undocumented.** |
| Permissions required to export | Nothing anywhere says whether the login needs `db_datareader` or more, or what dacpac deploy needs. | **Undocumented.** Asked by five of twelve. |
| File size at realistic scale | No number anywhere. | Undocumented. Asked by two. |

Temporal tables, computed columns, `hierarchyid` / `geography` / `geometry` / `sql_variant`, and
extra-target-column rules are all handled and already documented. The panel assumed they were not,
which is a discoverability problem rather than a capability one.

## What the panel got wrong on first read, and why

Eleven of twelve mis-filed the product. "A fancy bacpac." "Backup replacement." "pg_dump for SQL
Server." "Another seed-a-container tool." The 2.8 TB e-commerce engineer said the opening line made
him twitch, because nobody says "exports a SQL Server database" about a database that size unless
they mean a slice.

The cause is the word **database** in the first sentence. It should be **slice**.

Four asked explicitly for the README to say what it is *not*: not a backup, not a masking tool.

## The hook, confirmed

Nine of twelve named the same moment as when it clicked: the file is editable.

> That's the part that makes this more than a fancy bcp wrapper.

> The pitch is framed as "export/import," which undersells the actual product: a database that
> becomes a file a non-DBA can hold, hand-edit, and pass around. That's the headline, not a bullet
> point.

The fastest explainer anyone produced, from the consultant:

> It's bacpac, except the output is a plain SQLite file you can open in any tool, hand-edit, and
> re-import without losing identity or FK order.

`.bacpac` is the reference point almost everyone reached for unprompted. The comparison is currently
a link to a docs page.

## Claims not being led with

Three strong arguments the current copy buries or omits.

**Exclude-at-export is a compliance argument, not a convenience.** From the healthcare ISV:

> Excluding a column at export is materially different from scrubbing it after, because the column
> never crosses the wire, never sits in a temp file, never exists in the package at all.
> Scrub-after means the PHI touched disk somewhere first and I'm trusting a script to clean it up
> correctly, every time, forever. That distinction is the whole ballgame for a BAA conversation.

`ExcludeColumns` is currently a sentence about not forgetting a column later.

**The manifest is an audit artifact.** From the restricted-network engineer, whose transfer review
board has to approve anything crossing networks:

> A single SQLite file with its own built-in manifest is something the review board can actually
> reason about in one sitting instead of auditing forty scripts. That's the difference between a
> same-day approval and a two-week ticket.

**No classifier is a feature to the people who care most.** The healthcare engineer, unprompted:

> I don't trust classifiers on PHI anyway. The one time I evaluated a tool that auto-flagged
> sensitive columns it missed a free-text Notes field where a scheduler had typed a patient's
> diagnosis, and it flagged RoomNumber as PII. I'd rather own the list myself.

The current framing treats "no masking rules and no classifier" as a shortfall to disclose.

## Where it reads as a risk

One dissent worth keeping. The regulated-finance lead read the same mechanism everyone else liked as
the thing that kills it:

> "One file you can hand to someone" is exactly the phrase that shows up in an incident writeup.
> Convenient is how data walks out the door.

What he wanted stated plainly, and what would have let him evaluate it in thirty seconds:

> SqlDataPack does not encrypt, mask, or classify output. All of that is your responsibility. We
> only control what rows and columns get selected.

He also drew a line the docs do not: the CLI is auditable because a human runs it from a logged box.
The library is what his security review objects to, because it can end up embedded in application
code running under a service account with broader access than the person who invoked it.

## Use cases nobody is selling

- **Replacing a folder of seed scripts.** The QA engineer maintains 40 that drift and take 90
  seconds. A curated package file as a checked-in fixture is a direct replacement.
- **Strangler-fig data migration.** Carving a subsystem out of a monolith and moving its historical
  data into the new service's database, once, with FK order and identity preserved. Today that is
  an SSIS package nobody wants to open.
- **Cross-network transfer.** One inspectable file through a gated transfer process.
- **A versioned seed artifact on a platform team's golden path.** `crm-seed-v14`, published to an
  internal feed, restored by every team's AppHost.
- **Handing an offshore QA vendor a governed subset** without giving them network access to
  anything.

## Roadmap evidence

**The CLI is the most requested surface, by a distance.** Eleven of twelve said they would reach for
it first. It is what gets piloted without permission, what a DBA will run, and what fits a runbook.

The strongest argument for it came from the .NET Framework 4.8 lead, and it is not about
convenience:

> net8.0/net10.0 only doesn't scare me, because nothing in my 4.8 monolith needs to reference this
> package directly. I'd run it as a standalone CLI pointed at the database from outside, same way I
> already run sqlpackage.

A CLI removes the target-framework barrier entirely. Every SQL Server shop still below net8.0
becomes addressable, which is a large share of the ones with this problem.

**Aspire matters intensely to one persona and not at all to the rest.** The platform lead called it
the headline and everything else plumbing. Nine others shrugged. That is a real segment, not a
general requirement.

**The consultant's adoption path**, worth designing for: CLI gets tried first because it needs no
permission. The library is the trojan horse, because once someone wires the exporter into a nightly
job it is load-bearing and nobody rips it out. Aspire is the long game.

## Signal the panel wanted that recent decisions reduce

The consultant, on what stops him recommending a dependency:

> One maintainer, no company or foundation behind it, is my first red flag. I check contributor
> count, whether issues get answered inside a week, and whether there's been a release in the last
> quarter.

Noting only because the identity decision went the other way on purpose. Not a recommendation to
revisit it.
