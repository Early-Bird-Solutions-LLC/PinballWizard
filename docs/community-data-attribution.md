# Community Data — How We Use It, How We Credit You

> One-page plain-language explainer for community-resource operators (pricing aggregators, marketplaces, machine databases, news sites, etc.) we've reached out to about using their data inside PinballWizard. If you got an email from Jim Keeley at Earlybird Solutions and you're wondering "what would saying yes actually mean?" — this is for you.

## In one sentence

If you say yes, the Wizard surfaces your data with full attribution and a click-through link back to your site for every piece of information it shows — and we promise never to compete with you for the user's attention or their click.

## What "yes" would look like in practice

Imagine a user asks the Wizard: *"What's a Godzilla Premium worth?"*

If you've granted permission, the answer might say:

> Recent **PinballPrices.com** data suggests around $9,200–$10,400 for a 2021 Godzilla Premium in good condition.
>
> **[See full sales history on PinballPrices.com →]**

The bolded source name is your site, with a click-through link directly to the relevant page. The user's next click is to your site — never to a Wizard-internal "marketplace" feature, because we're not building one.

## What we promise

- **Attribution always.** Every value we show carries "source: [your site]" with a click-through link to the specific page on your site.
- **Polite-by-construction.** Once-daily polite scrape, identifying user agent so you can recognize us in your logs, respects any rate limits or directives you specify.
- **Freshness honesty.** The UI displays "as of [date]" so users know the data isn't real-time.
- **Purpose-bound use.** The data we show users is *purely a router signal* to send them to your site for the actual user action (purchase, browse, deep-research). The Wizard doesn't facilitate transactions, doesn't try to be a marketplace, doesn't keep users from visiting you.
- **You can change your mind.** If you say yes today and want us to stop tomorrow, we stop. No drama, no negotiation.

## What we'll never do

- Claim your data as our own.
- Strip attribution to make our answers look "smarter."
- Use your data to train AI models, sell ads, or any other use beyond directly answering user questions and routing them to you.
- Compete with you for the user's purchase moment. The Wizard's whole point is to *route users out* — to your site or whichever community venue best serves them.
- Wall your data behind a paywall, account, or subscription. The Wizard is free; users always get back to you for free.

## What we ask in return

Just permission. Specifically, one of:

1. **Permission to do a once-daily polite scrape** of your sitemap-listed pages. We'll identify ourselves with a clear user agent (`PinballWizard/1.x — Earlybird Solutions / jim@earlybirdsolutions.com`), respect any rate limits or directives you specify, and stop immediately if you ever ask us to.
2. **An affiliate / referrer arrangement** instead of scraping — if you have an affiliate program, we'll use referrer-tagged URLs so the click-throughs from Wizard answers preserve your existing revenue model.
3. **An API or data feed** if you have one — happy to use whatever you've already built rather than scraping.

## The honest fallback

If you'd prefer we stay link-only — meaning the Wizard just routes users to your site without showing any data from it directly — that's exactly what we're doing today, and it's genuinely fine. Most of the project's posture is built on "route traffic outward, never capture users." Link-only is fully consistent with that. You won't hurt the project by saying no.

If you want to think about it for a while and respond later, that's also fine. There's no deadline.

## How to respond

Just reply to the email Jim sent. Anything from "yes, here are my conditions" to "no, link-only is fine" to "I have questions — can you explain X?" gets a real answer.

## More context

PinballWizard is a customer-facing showcase / reference application by Earlybird Solutions. Jim Keeley (the project's author) is a professional software engineer building it as both a reference / showcase for his consulting work and because he's an avid pinball enthusiast. The full project is open source on GitHub: <https://github.com/Early-Bird-Solutions-LLC/PinballWizard>
