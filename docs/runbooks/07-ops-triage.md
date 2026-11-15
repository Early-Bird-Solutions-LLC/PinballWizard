# Ops Triage — Foundry alert diagnosis + remediation

**Trigger:** Azure Monitor fires (especially `pinwiz-alert-aca-job-failure`) and the action-group webhook posts to `/api/ops/alerts/webhook`  
**Alert rule:** any rule on `pinwiz-ops-alerts` once `opsTriageWebhookUri` is set  
**Time budget:** first look &lt; 2 minutes via Admin → Ops Triage  
**Last walked:** 2026-11-15 (design walk — live webhook proof pending first Monitor fire after secrets seed)

---

## What this is (showcase surface)

Ops Triage is the reference-app answer to “who watches the watchers?”:

1. **Foundry `OpsTriage` AIAgent** (code-resource prompt, `AsAIAgent`, `gpt-4-1`) writes a short diagnosis.
2. **Enriched operator email** (ACS Email) carries that diagnosis — not a raw Monitor blob alone.
3. When reasonable, a **CI remediation workflow** opens a PR (failing test → fix → validate). Humans merge.
4. **Admin → Ops Triage** shows every run (admin-only). Monitoring lists the job-failure alert and links here.

See [ADR-0064](../adr/0064-ops-triage-foundry-agent.md).

---

## One-time enablement (before first webhook)

```bash
# 1) Seed Key Vault (required before Api revision that refs these secrets)
az keyvault secret set --vault-name pinwiz-kv-dev \
  --name OpsTriage-WebhookSecret --value "$(openssl rand -hex 32)"
az keyvault secret set --vault-name pinwiz-kv-dev \
  --name OpsTriage-EmailConnectionString --value "<ACS Email connection string>"
az keyvault secret set --vault-name pinwiz-kv-dev \
  --name OpsTriage-GitHubToken --value "<fine-grained PAT: actions write + contents + PRs>"

# 2) Flip enableOpsTriage=true on the shared stack and set:
# opsTriageWebhookUri = https://<api-host>/api/ops/alerts/webhook?code=<same secret>
```

`enableOpsTriage` defaults **false** so merges do not brick the Api before secrets exist. Without ACS / GitHub secrets after enablement, diagnosis still runs; notification / remediation record explicit `Skipped:` reasons (OBS-01).

---

## Operator first look

1. Open **Admin → Ops Triage** (GlobalAdmin). Newest run first.
2. Confirm `Status=Completed`, read `DiagnosisSummary` / `LikelyCause`.
3. If a remediation workflow URL is present, open the Actions run / PR — review and merge.
4. Cross-check **Admin → Jobs** for the named ACA job’s latest execution.

---

## Failure modes

| Symptom | Likely cause | Action |
| --- | --- | --- |
| Webhook 401 | Secret mismatch | Compare KV `OpsTriage-WebhookSecret` to `?code=` |
| Webhook 503 | `OpsTriage:Enabled=false` | Set env / re-deploy Api |
| `NotificationStatus=Skipped:…` | ACS connection string missing | Seed ACS Email + KV secret |
| No remediation URL | Agent said not reasonable, or GitHub token missing | Read `SkipRemediationReason`; seed PAT if needed |
| `Status=Failed` | Foundry / parse error | Open detail → `ErrorMessage`; check Api logs |

---

## Related

- Dead-man's-switch (job never runs): still [#856](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/issues/856)
- Scraper source outage: [`06-source-site-outage.md`](06-source-site-outage.md)
