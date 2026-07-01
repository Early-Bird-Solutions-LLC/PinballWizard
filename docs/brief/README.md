# Architecture & capability brief

A client-facing one-pager summarizing what PinballWizard is and what it
demonstrates — built with the EarlyBird document design system, for sending to
prospective clients alongside (or instead of) a walk through the repository.

| File | Role |
| --- | --- |
| `pinballwizard-architecture-brief.pdf` | The deliverable — send this. |
| `pinballwizard-architecture-brief.html` | The editable source. |
| `earlybird-design.css` | The design-system stylesheet, shipped beside the HTML. |

The stylesheet's canonical home is the workspace `brand/` design-system repo
(link it, don't fork it). The copy here travels with the deliverable per the
design system's "ship the HTML with `earlybird-design.css` beside it" convention,
so the HTML renders standalone without the workspace layout.

## Regenerate the PDF

Edit the HTML, then export with headless Chrome — it honors the print stylesheet
(page margins, colour, and the page-break rules that keep cards/panels whole):

```bash
"/c/Program Files/Google/Chrome/Application/chrome.exe" \
  --headless=new --disable-gpu --no-pdf-header-footer --virtual-time-budget=10000 \
  --print-to-pdf="<abs>/pinballwizard-architecture-brief.pdf" \
  "file:///<abs>/pinballwizard-architecture-brief.html"
```

`--virtual-time-budget` gives the web fonts time to load; Edge works identically
(swap the executable path). The small per-document `@media print` block in the
HTML keeps the 3-up stat grid from collapsing at print page width — content and
tokens otherwise come entirely from the linked stylesheet.
