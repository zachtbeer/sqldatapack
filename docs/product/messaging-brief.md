# SqlDataPack messaging brief

Source: interview with the maintainer, 2026-08-23. This is the input for the README, the NuGet
description, and the docs site landing copy. When any of them disagree with this, this wins.

## Who it is for

A .NET developer on a product team. They own the application, they already hold the connection
string, there is no DBA on call, and they cannot install anything on the SQL Server. Everyone else
(DBA, consultant, agent user) is a secondary reader who should not shape the lead.

## The job

Get a small, realistic, safe slice of production data into a dev or test environment. Second, and
closely related: get a bug repro that someone else can open without credentials.

Not the wedge: handing data to an AI coding agent. It stays as a supported use, it never leads.

## What they do today

Some painful combination of all of these:

- Restore a full backup to a scratch instance, then delete and scrub by hand.
- A hand-rolled export script that someone wrote once, that goes stale fast and gets unwieldy.
- Seed data that does not reproduce the bug.
- Nothing. The slice is too much hassle, so they work around the missing data.

The real competitors are the stale script and inertia, not `bcp` or SqlPackage. Name the stale
script in the comparison section, not in the lead.

## The one thing to remember

There is a stage in the middle. Between extract and load you get a real, local, queryable database
you can change before anything lands.

## Tone

Plainer, less rhetorical setup than the current README. Open with what it does. Engineer to
engineer, unglamorous, closer to internal docs for a colleague than to a pitch. Keep the restraint,
drop the writerly turns.

## What it must never claim

- That it is a backup tool, or a substitute for one.
- That it masks data for you. There is no classifier, no PII detection, no built-in rules. You
  write the `UPDATE` statements.

The "it is not a consistent snapshot" caveat comes off the README entirely (maintainer's call,
2026-08-23): a developer doing table-by-table extraction already understands what that implies, and
it does not belong next to the core premise or in the packed nuget.org README. It stays in full on
the comparison docs page and in `known-limitations.md`, including the "Consistent snapshot: No" row
in the table and the advice to export from a restored copy or a readable secondary. Placement
change only. Nothing else gets softened.

## Shape

Target roughly 100 lines, down from 207.

- Cut the ASCII diagram.
- Move the full comparison table to its own docs page. README gets two sentences and a link.
- Supply chain and security collapse to a single line item plus links to dedicated docs pages.
- First code block is the filtered slice: selected tables, `ExcludeColumns`, a `WHERE` clause. Not
  the bare two-line export/import.

## Plain language

The rule the first rewrite missed. It cost two rounds, so it is written down.

- Name the category in the first sentence. "SqlDataPack is a .NET library that..." Not the
  scenario, not the outcome, not a rhetorical setup.
- Let the reader recognise their own problem before selling. A direct question ("Have you ever
  needed a copy of production on your laptop, but only a few tables?") beats a described persona.
- Headings say what the section does. `Export`, `Edit it`, `Import`. Not `Take the slice`.
- Every code example gets a lead-in describing it in words a cold reader understands.
- No caveat section on the front page. Limits go where the reader hits them, or in the docs.
- Do not narrow the audience with an example. Multi-tenant is one use, not the framing.
- Do not write an essay about release status. A prerelease badge and one line at the install
  command is what .NET library READMEs do, and it is enough.

## Position and support

Commercially undecided. The README should not commit to a paid tier, services, or a hosted
anything, and should not hint at one.

Actively developed by one person who intends to keep building on it. Say that, and invite requests.

## Identity

Ships as `zachtbeer`, the person. `LICENSE`, `SECURITY.md`, the Docusaurus footer, and the README
all match the csproj. **Zachtbeer Labs B.V. appears nowhere.** The only surviving trace is the
`security@zachtbeerlabs.nl` contact address, which is a domain rather than the entity name.

## Release shape

The first published version is `1.0.0-preview.1`, not a stable 1.0.0 and not an RC. Install lines
carry `--prerelease`, the NuGet badge uses `vpre`, and Project Status says the preview window exists
to find out where the intended 1.0 shape is wrong while changing it is still cheap. Do not carry
release-candidate strength promises into preview copy.

## Known gap

Adding and removing rows in the package fails the import row-count check (#13, #18). It is committed
to before the 1.0.0 tag and will probably not be in `1.0.0-preview.1`.

Maintainer call: the README is written for 1.0.0, so it says `UPDATE`, `DELETE`, and `INSERT`
without qualification. The docs site is written for what is shipped, so `editing-the-package.md`,
`comparison.md`, and the FAQ say adding and removing rows is not supported yet and point at #18.
When #18 lands, those three qualifiers come out and nothing in the README changes.

The wedge is unaffected. Filtering happens on the way out with `WHERE` and `ExcludeColumns`, not by
deleting rows in the package, so this is one honest sentence about editing rather than a caveat
against the pitch.
