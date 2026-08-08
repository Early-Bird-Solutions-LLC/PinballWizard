using '../main.bicep'

param env = 'dev'
param prefix = 'pw'
param storagePrefix = 'pw'
param acrName = 'pwacr'
param imageTag = 'latest'
param customDomain = ''

// Scaling — smaller for dev
param scraperCpu = '0.25'
param scraperMemory = '0.5Gi'
param processorCpu = '0.5'
param processorMemory = '1Gi'
param processorMaxReplicas = 1
param webCpu = '0.25'
param webMemory = '0.5Gi'
param webMinReplicas = 0
param webMaxReplicas = 1

// SKUs — free/smaller tiers for dev where possible
param searchSku = 'basic'
param docIntelSku = 'F0'
param speechSku = 'F0'
param acrSku = 'Basic'
