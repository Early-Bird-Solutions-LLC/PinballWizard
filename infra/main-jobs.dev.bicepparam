// =============================================================================
// pinwiz.ai — ACA Jobs workload stack, dev environment parameters (ADR-0060)
//
// Image-only merges deploy this template via az stack group create
// (pinwiz-jobs-dev). Platform resources are looked up by naming convention
// against rg-pinwiz-shared-dev — never recreated here.
// =============================================================================

using 'main-jobs.bicep'

param environment = 'dev'
param location = 'eastus2'
param namePrefix = 'pinwiz'
param deployAiSearch = true

// Placeholder — Deploy-WorkloadJobs.ps1 / deploy.yml always override with the
// real ACR pinwiz-cli:<sha>. Leaving a non-empty default keeps `bicep build`
// happy; never apply the placeholder to a live stack.
param cliImageTag = 'mcr.microsoft.com/k8se/quickstart:latest'

param enableAzureSdkDiagnostics = false
param useSternPlaywrightWorkspace = true
param playwrightServiceUrl = ''
