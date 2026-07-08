# scrape_shield.tf
#
# Cloudflare Scrape Shield zone settings.
#
# Email Address Obfuscation is DISABLED. Cloudflare's obfuscation rewrites any
# email-looking text in a proxied HTML response into
#   <a class="__cf_email__" data-cfemail="…">[email protected]</a>
# and relies on Cloudflare's injected `email-decode.min.js` to restore the real
# address in the browser. Our enforced Content-Security-Policy (see waf.tf) does
# not allow that script, so on server-rendered (static) admin pages the
# "Signed in as <email>" identity in the app bar renders as a broken/empty
# placeholder. Interactive admin pages happen to self-heal because Blazor
# re-renders the app bar over the SignalR circuit after hydration, overwriting
# Cloudflare's rewrite — which is why the bug appears only on static pages.
#
# The admin surface sits behind Cloudflare Access, so its email addresses are
# not a public scraping target; obfuscation provides no benefit here and
# actively breaks the identity display. Turning it off is the correct fix and
# restores the address uniformly across all render modes.
resource "cloudflare_zone_setting" "email_obfuscation" {
  zone_id    = var.zone_id
  setting_id = "email_obfuscation"
  value      = "off"
}
