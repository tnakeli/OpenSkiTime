@description('Existing Azure Container Apps managed environment resource ID.')
param environmentId string
param location string = resourceGroup().location
param appName string = 'openskitime-live'
@description('Zero enables scale-to-zero between races. One can be used during rehearsals or a planned race window.')
@minValue(0)
@maxValue(1)
param minReplicas int = 0
@description('Immutable image reference; provision registry pull permissions separately for a private registry.')
param image string
@description('HTTPS public origin, without a trailing slash.')
param publicBaseUrl string
@secure()
@description('Base64 encoding of at least 32 cryptographically random bytes. Keep stable across ordinary restarts.')
param signingKey string
@secure()
@description('Publisher key entries as name:sha256hex separated by semicolons. Hashes only; keys stay with publishers.')
param publisherKeys string

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: appName
  location: location
  properties: {
    managedEnvironmentId: environmentId
    configuration: {
      activeRevisionsMode: 'Single'
      secrets: [
        { name: 'live-signing-key', value: signingKey }
        { name: 'live-publisher-keys', value: publisherKeys }
      ]
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
        traffic: [{ latestRevision: true, weight: 100 }]
      }
    }
    template: {
      containers: [{
        name: 'live'
        image: image
        resources: { cpu: json('0.5'), memory: '1Gi' }
        env: [
          { name: 'ASPNETCORE_HTTP_PORTS', value: '8080' }
          { name: 'LiveTiming__SigningKey', secretRef: 'live-signing-key' }
          { name: 'LiveTiming__PublisherKeys', secretRef: 'live-publisher-keys' }
          { name: 'LiveTiming__TrustForwardedFor', value: 'true' }
          { name: 'LiveTiming__PublicBaseUrl', value: publicBaseUrl }
          { name: 'LiveTiming__TokenDays', value: '14' }
          { name: 'LiveTiming__MaxSessions', value: '100' }
          { name: 'LiveTiming__MaxBodyBytes', value: '2097152' }
          { name: 'LiveTiming__CreationPerMinute', value: '5' }
          { name: 'LiveTiming__RequestsPerMinute', value: '3000' }
          { name: 'LiveTiming__MaxConnections', value: '1000' }
          { name: 'Logging__LogLevel__Default', value: 'Warning' }
        ]
        probes: [
          { type: 'Startup', httpGet: { path: '/health', port: 8080 }, initialDelaySeconds: 1, periodSeconds: 2, failureThreshold: 30 }
          { type: 'Readiness', httpGet: { path: '/health', port: 8080 }, periodSeconds: 5, failureThreshold: 3 }
          { type: 'Liveness', httpGet: { path: '/health', port: 8080 }, periodSeconds: 10, failureThreshold: 3 }
        ]
      }]
      scale: {
        minReplicas: minReplicas
        maxReplicas: 1
        rules: [{ name: 'http', http: { metadata: { concurrentRequests: '10' } } }]
      }
    }
  }
}
output fqdn string = app.properties.configuration.ingress.fqdn
