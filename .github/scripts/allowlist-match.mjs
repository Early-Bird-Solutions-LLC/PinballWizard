// Usage: node allowlist-match.mjs <allowlist-file> <path>
// Exit 0 if <path> is allowed, 1 if denied. Last matching pattern wins;
// lines starting with ! are denials. Uses minimatch-free glob via a small
// regex compile (no external deps in the runner).
import { readFileSync } from 'node:fs';
const [, , listFile, target] = process.argv;
const lines = readFileSync(listFile, 'utf8').split('\n').map(s => s.trim()).filter(Boolean);
function toRegex(glob) {
  let re = glob.replace(/[.+^${}()|[\]\\]/g, '\\$&')
               .replace(/\*\*/g, ' ')
               .replace(/\*/g, '[^/]*')
               .replace(/ /g, '.*');
  return new RegExp('^' + re + '$');
}
let allowed = false;
for (const line of lines) {
  const deny = line.startsWith('!');
  const pat = deny ? line.slice(1) : line;
  if (toRegex(pat).test(target)) allowed = !deny;
}
process.exit(allowed ? 0 : 1);
