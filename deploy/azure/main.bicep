targetScope = 'resourceGroup'

@description('Race publication is hosted in Sweden. Static assets are distributed globally by Static Web Apps.')
param location string = 'swedencentral'
@description('Static Web Apps supports a smaller set of control-plane regions. There is no Functions backend.')
param websiteLocation string = 'westeurope'
param environmentName string = 'openskitime-environment'
param websiteName string = 'openskitime-website'

resource environment 'Microsoft.App/managedEnvironments@2025-01-01' = {
  name: environmentName
  location: location
  tags: { project: 'OpenSkiTime', purpose: 'live-timing' }
  properties: {
    appLogsConfiguration: { destination: 'none' }
    workloadProfiles: [{ name: 'Consumption', workloadProfileType: 'Consumption' }]
    zoneRedundant: false
  }
}

resource website 'Microsoft.Web/staticSites@2024-11-01' = {
  name: websiteName
  location: websiteLocation
  tags: { project: 'OpenSkiTime', purpose: 'website' }
  sku: { name: 'Free', tier: 'Free' }
  properties: {
    provider: 'Custom'
    stagingEnvironmentPolicy: 'Disabled'
    allowConfigFileUpdates: true
  }
}

output environmentId string = environment.id
output websiteId string = website.id
output websiteHostname string = website.properties.defaultHostname
