// Internal markdown link checker for the documentation surface.
//
// Fails (exit 1) if any relative link in a scanned .md file points at a path
// that doesn't exist on disk. This is the guard that would have caught the
// docs/README.md -> glossary.md dead link by hand-review alone.
//
// Scope: README.md + everything under docs/ EXCEPT docs/superpowers/ (internal
// working notes — specs/plans reference scratch paths that need not resolve).
// External links (http/https/mailto) and pure in-page anchors (#foo) are not
// checked here. Line/section anchors on a real file (file.cs#L42, doc.md#head)
// are validated by the file part only.
//
// Usage: node tools/docs/check-links.mjs   (run from repo root)

import fs from 'node:fs';
import path from 'node:path';

const ROOT = process.cwd();

// Docs with known, deferred link rot — excluded so they don't block NEW rot
// from being caught. Keep this list SHORT and shrinking; each entry is a
// tracked cleanup, not a permanent exemption:
//   - metadata-audit.md: a point-in-time audit pinned to the pre-rename
//     PinballWizard.Scraper source layout (project is now .Infrastructure).
//   - self-healing-agent-and-ai-roadmap.md: forward-references ADRs not yet written.
const SKIP_FILES = new Set([
  'docs/metadata-audit.md',
  'docs/self-healing-agent-and-ai-roadmap.md',
]);

function walk(dir, acc) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.name === 'superpowers') continue; // internal working notes
    const p = path.join(dir, entry.name);
    if (entry.isDirectory()) walk(p, acc);
    else if (entry.name.endsWith('.md')) acc.push(p);
  }
}

const files = [];
const readme = path.join(ROOT, 'README.md');
if (fs.existsSync(readme)) files.push(readme);
const contributing = path.join(ROOT, 'CONTRIBUTING.md');
if (fs.existsSync(contributing)) files.push(contributing);
walk(path.join(ROOT, 'docs'), files);

// Inline markdown links: [text](target). Reference-style and autolinks are rare
// in this doc set and out of scope. Fenced code blocks are stripped first so
// example links inside ``` don't produce false positives.
const linkRe = /\[[^\]]*\]\(([^)]+)\)/g;
const fenceRe = /```[\s\S]*?```/g;

const broken = [];
let linkCount = 0;

for (const file of files) {
  if (SKIP_FILES.has(path.relative(ROOT, file).split(path.sep).join('/'))) continue;
  const raw = fs.readFileSync(file, 'utf8').replace(fenceRe, '');
  let m;
  while ((m = linkRe.exec(raw)) !== null) {
    // Drop an optional link title: [t](path "title")
    let target = m[1].trim().split(/\s+/)[0];
    if (!target) continue;
    if (/^(https?:|mailto:|tel:|data:)/i.test(target)) continue; // external
    if (target.startsWith('#')) continue;                        // in-page anchor
    // Intentional pointers to Claude memory / config, which live OUTSIDE the
    // repo (~/.claude, per CLAUDE.md) — not resolvable in a checkout by design.
    if (/\.claude[\\/]/.test(target) || /[\\/]memory[\\/]/.test(target)) continue;
    const relPath = target.split('#')[0];                        // strip #anchor
    if (!relPath) continue;
    linkCount++;
    const resolved = path.resolve(path.dirname(file), decodeURIComponent(relPath));
    if (!fs.existsSync(resolved)) {
      broken.push(`${path.relative(ROOT, file)}  →  ${m[1]}`);
    }
  }
}

if (broken.length > 0) {
  console.error(`✗ ${broken.length} broken internal link(s):\n`);
  for (const b of broken) console.error('  ' + b);
  console.error(`\nChecked ${linkCount} internal links across ${files.length} files.`);
  process.exit(1);
}

// ── Engineering manifest: assert every sourcePath exists on disk ─────────────
// Belt-and-braces with the xUnit conformance test, but runs in the docs-only
// CI lane where .NET tests don't execute. Each entry in docs[] must have a
// sourcePath that resolves to a real file relative to the repo root.
const manifestPath = path.join(ROOT, 'docs', 'engineering-manifest.json');
if (fs.existsSync(manifestPath)) {
  const manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
  const entries = (manifest.docs ?? []).filter(e => e.sourcePath);
  const missingPaths = entries.filter(e => !fs.existsSync(path.resolve(ROOT, e.sourcePath)));
  if (missingPaths.length > 0) {
    console.error(`✗ ${missingPaths.length} engineering-manifest.json sourcePath(s) missing on disk:\n`);
    for (const e of missingPaths) console.error(`  ${e.sourcePath}  (slug: ${e.slug})`);
    process.exit(1);
  }
  console.log(`✓ all ${entries.length} engineering-manifest.json sourcePaths resolve.`);
}

console.log(`✓ all ${linkCount} internal links resolve across ${files.length} markdown files.`);
