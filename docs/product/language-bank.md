# Language bank

What twelve simulated .NET developers said about the README, in their words. Round two asked what
they liked, what it could solve, and which phrases landed. Use this to write copy; do not paraphrase
the quotes into something smoother.

## Keep, unanimously

**"Not a backup: you pick what comes out, and you can edit it before it goes back in."**

Six of twelve named this line independently as the one that works. One said they would screenshot
it. It is the highest-performing sentence on the page. Do not touch it.

> That's the whole pitch in one line.

> Four words in and I already know what this isn't.

> It forecloses the wrong mental model before the reader builds it.

## Add: the `.bacpac` anchor

`.bacpac` is the reference point almost every reader already has, and the fastest way to place the
product. It currently appears only in a comparison section far down the page. The consultant's
placement call: right after "not a backup", before the rhetorical questions, because the questions
land harder once the anchoring is done.

His draft:

> If you've used bacpac, think of this the same way, except the file it produces is plain SQLite:
> open it in any SQLite tool, edit rows with a normal UPDATE statement, and import it back without
> losing identity values or FK order.

Related, and the single best line produced across both rounds, currently nowhere on the page:

> If you wouldn't do it with a `.bacpac`, don't do it with this.

The healthcare engineer explained why it works: it puts the judgement where the reader already has
twenty years of instinct, instead of implying the tool is doing that thinking for them.

## Add: say it is a library and there is no CLI

The consultant's closing point, and he made this exact mistake himself in round one:

> It never says "this is a library, there is no CLI" in plain words. It implies it ("you call from
> code") but doesn't rule out the CLI a bacpac-literate reader will assume exists.

Once you invoke `.bacpac`, the reader assumes SqlPackage-shaped tooling. One sentence kills it.

## The split: "Nothing is installed on the SQL Server"

Genuinely divided, so do not resolve it by deleting.

Six personas quoted this sentence as the exact place their eyes slide off:

> It answers a question I wasn't asking.

> An infrastructure detail, not a hook.

> True and fine, but it reads like a spec line, not a reason to care.

The consultant called it the best line on the page:

> That answers the security guy's first question before he asks it, in one sentence, with no
> hedging.

Read: it is not a hook, it is an objection-killer. Keep it, but never where a hook belongs.

## The split: "copy of production data on your machine"

Five personas called this the line that stopped them. Repeatedly described as "my actual Tuesday",
"a question I've actually asked out loud in a standup", "the exact 2am feeling".

One dissent, from regulated finance, and it is a real one:

> That's the incident report opener, not the pitch. If that first line gets pulled into a slide in
> a security review, which it will, it reads as an admission. Pull the qualifier into the same
> sentence or lose the word "production" entirely.

Five to one says keep it. His fix is cheap if you ever want it.

## Confirmed: supply chain belongs at the bottom

From the persona who cares about it most, on a restricted network:

> Up top it reads like every other tool's marketing checkbox and I skim past it. Down near the SBOM
> and provenance stuff it reads like evidence, which is what actually moves me.

## Confirmed: the individual-developer framing is right

From the platform lead, who is explicitly not served by it:

> I'm not the primary audience for a v1 README and I shouldn't be. If it pitched golden path at me
> before proving the single-dev case, I'd trust it less.

## Under-sold: CI and test fixtures

Three personas named it and it appears nowhere on the page. The QA engineer was blunt:

> A README this specific about the bug-repro use case and silent about CI is a gap, not a subtlety.

The platform lead pointed out it is the use case that works today with a library and no CLI:

> Arguably a better first foothold than local dev seeding, because it's already "call a library
> from code" and nobody needed a magic startup hook for it.

## Under-sold: the manifest

Nowhere in the README. The restricted-network engineer made the practical case, not the compliance
one:

> One file plus its own inventory is basically a chain-of-custody document. I attach the file to a
> ticket and the manifest tells the next person what's actually in it without them opening SQLite
> to check.

## Dacpac: convenience, not value proposition

Capture and deployment are **opt-in**, both defaulting to `None` (`Options.cs:442`, `Options.cs:557`).
Earlier drafts of this material called it "opt-out", which is wrong and inverts the meaning.

The core value is the slice: export it, edit it, analyse it offline, move it to another environment.
Dacpac adds two specific conveniences on top:

1. Full metadata about the source schema, when you want it.
2. Importing into a blank target and getting a 100% schema match.

That is the whole scope of it. It should not grow into a headline.

The legacy lead, once he understood it was off by default, produced the best statement of what that
buys a reader with an ugly schema:

> Your schema doesn't have to be clean for this to work. Skip schema capture, point it at a database
> that already exists, and it never has to have an opinion about your 2009 decisions.

## Rewrite `ExcludeColumns`

The README currently says "so it is never in the file to forget about later." The healthcare
engineer's version, written on request:

> An excluded column is never read from SQL Server, never written to the file, never sits on your
> disk. Compare that to exporting everything and deleting the column afterward: by then the data
> already crossed the wire and touched disk once, and you're trusting a cleanup step to get it right
> every time.

And, as a scroll-stopper:

> This tool doesn't know what PHI is. You do. Tell it what to leave out and it leaves it out
> completely: not scrubbed, not masked, just never read.

## Their one-liners, unedited

Useful raw material. Nobody was asked to write marketing, they were asked what they would say to a
colleague.

> Stop giving people a restore of prod. Give them a file.

> It's a way to hand someone a bug repro as a file instead of giving them a server.

> It's like a bacpac you can crack open in DB Browser for SQLite and hand-edit before it goes back
> in, that's the entire pitch.

> It's bcp with an undo button and a file you can poke at in DB Browser before it goes anywhere.

> It's like a bacpac that doesn't lie to you about what's in it and doesn't require you to import
> the whole thing just to look at three rows.

> It's like taking a WHERE-clause-shaped photo of prod, dropping it in a file you can hand-edit with
> plain SQL, then loading it wherever.

> It's a seed-data workflow where the data starts real instead of starting fake, and you edit with
> SQL instead of maintaining a generator script.

> Have you ever needed three rows from a 900-million-row table, on your laptop, with the email
> addresses scrubbed, by lunchtime?

> You know that moment where someone asks for a copy of prod and you either wait two days for a DBA
> or hand them your login? This replaces both.

## What they told me to drop

The consultant withdrew his own round-one framing:

> What I'd drop: the "governed subset for a vendor" framing. That's compliance language and the
> corrections are clear this isn't that tool. Fine to say, wrong to call it governance.

The legacy lead on strangler-fig migration, which he had raised himself:

> Doesn't belong on the front page. It's a docs recipe.
