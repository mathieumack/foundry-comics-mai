// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// 
// This solution is provided for testing, learning, and evaluation purposes only.
// It is not intended for production use. Microsoft provides no support or guarantees.
// Use at your own risk.
@description('Tags to apply to all resources')
param tags object = {}

@description('Virtual Network ID to link to the DNS zones')
param virtualNetworkId string

// Define all private DNS zones used across the infrastructure.
// IMPORTANT: The array order is fixed. Outputs reference zones by index — do NOT
// reorder entries without updating the corresponding output lines below.
//   [0]  cognitiveServices  → privatelink.cognitiveservices.azure.com
//   [1]  openai             → privatelink.openai.azure.com
//   [2]  aiServices         → privatelink.services.ai.azure.com
//   [3]  cosmosDb           → privatelink.documents.azure.com
//   [4]  search             → privatelink.search.windows.net
//   [5]  blob               → privatelink.blob.core.windows.net
//   [6]  table              → privatelink.table.core.windows.net
//   [7]  file               → privatelink.file.core.windows.net
//   [8]  acr                → privatelink.azurecr.io
//   [9]  serviceBus         → privatelink.servicebus.windows.net
var zones = [
  { name: 'privatelink-cognitiveservices-azure-com', zone: 'privatelink.cognitiveservices.azure.com' }   // [0]
  { name: 'privatelink-openai-azure-com',            zone: 'privatelink.openai.azure.com' }               // [1]
  { name: 'privatelink-services-ai-azure-com',       zone: 'privatelink.services.ai.azure.com' }          // [2]
  { name: 'privatelink-documents-azure-com',         zone: 'privatelink.documents.azure.com' }            // [3]
  { name: 'privatelink-search-windows-net',          zone: 'privatelink.search.windows.net' }             // [4]
  { name: 'privatelink-blob-core-windows-net',       zone: 'privatelink.blob.${environment().suffixes.storage}' }  // [5]
  { name: 'privatelink-table-core-windows-net',      zone: 'privatelink.table.${environment().suffixes.storage}' } // [6]
  { name: 'privatelink-file-core-windows-net',       zone: 'privatelink.file.${environment().suffixes.storage}' }  // [7]
  { name: 'privatelink-azurecr-io',                  zone: 'privatelink.azurecr.io' }                     // [8]
  { name: 'privatelink-servicebus-windows-net',      zone: 'privatelink.servicebus.windows.net' }         // [9]
]

resource privateDnsZones 'Microsoft.Network/privateDnsZones@2020-06-01' = [for zone in zones: {
  name: zone.zone
  location: 'global'
  tags: tags
}]

@batchSize(1)
resource virtualNetworkLinks 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = [for (zone, index) in zones: {
  parent: privateDnsZones[index]
  name: '${zone.name}-link'
  location: 'global'
  tags: tags
  properties: {
    virtualNetwork: {
      id: virtualNetworkId
    }
    registrationEnabled: false
  }
}]

// Output individual zone IDs for consumption by other modules
output cognitiveServicesZoneId string = privateDnsZones[0].id
output openaiZoneId string = privateDnsZones[1].id
output aiServicesZoneId string = privateDnsZones[2].id
output cosmosDbZoneId string = privateDnsZones[3].id
output searchZoneId string = privateDnsZones[4].id
output blobZoneId string = privateDnsZones[5].id
output tableZoneId string = privateDnsZones[6].id
output fileZoneId string = privateDnsZones[7].id
output acrZoneId string = privateDnsZones[8].id
output serviceBusZoneId string = privateDnsZones[9].id
