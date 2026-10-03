targetScope = 'subscription'

@description('Monthly budget in the subscription billing currency. Alerts do not stop spending.')
@minValue(1)
param amount int = 20
param resourceGroupName string = 'openskitime-production'
param contactEmails array
param startDate string
param endDate string

resource budget 'Microsoft.Consumption/budgets@2024-08-01' = {
  name: 'openskitime-monthly'
  properties: {
    category: 'Cost'
    amount: amount
    timeGrain: 'Monthly'
    timePeriod: { startDate: startDate, endDate: endDate }
    filter: { dimensions: { name: 'ResourceGroupName', operator: 'In', values: [resourceGroupName] } }
    notifications: {
      half: { enabled: true, operator: 'GreaterThanOrEqualTo', threshold: 50, thresholdType: 'Actual', contactEmails: contactEmails }
      full: { enabled: true, operator: 'GreaterThanOrEqualTo', threshold: 100, thresholdType: 'Actual', contactEmails: contactEmails }
      forecast: { enabled: true, operator: 'GreaterThanOrEqualTo', threshold: 100, thresholdType: 'Forecasted', contactEmails: contactEmails }
    }
  }
}
