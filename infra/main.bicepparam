using './main.bicep'

param env = 'prod'
param prefix = 'pw'
param storagePrefix = 'pw'
param acrName = 'pwacr'
param imageTag = 'latest'
param customDomain = 'pinwiz.ai'

// Scaling
param scraperCpu = '0.5'
param scraperMemory = '1Gi'
param processorCpu = '1.0'
param processorMemory = '2Gi'
param processorMaxReplicas = 3
param webCpu = '0.5'
param webMemory = '1Gi'
param webMinReplicas = 1
param webMaxReplicas = 5

// SKUs
param searchSku = 'basic'
param docIntelSku = 'S0'
param speechSku = 'S0'
param acrSku = 'Basic'
