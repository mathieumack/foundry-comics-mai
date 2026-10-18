using 'main.bicep'

// Keep mappings and defaults aligned with main.parameters.json for azd pipeline config.
param environmentName = readEnvironmentVariable('AZURE_ENV_NAME', 'demo')
param location = readEnvironmentVariable('AZURE_LOCATION', 'swedencentral')

param apiContainerImage = readEnvironmentVariable(
	'SERVICE_API_IMAGE_NAME',
	'mcr.microsoft.com/dotnet/samples:aspnetapp@sha256:b8e94412a96b19ac12c78a7064af1bb0a6f584e752b9de7cc31ca4d45ead36b0'
)
param generationJobContainerImage = readEnvironmentVariable(
	'SERVICE_GENERATIONJOB_IMAGE_NAME',
	'mcr.microsoft.com/dotnet/samples:dotnetapp'
)
param bookPlanGenerationJobContainerImage = readEnvironmentVariable(
	'SERVICE_BOOKPLANJOB_IMAGE_NAME',
	'mcr.microsoft.com/dotnet/samples:dotnetapp'
)

// Deploy the Foundry BYO connections and Agents capability host.
// This value is auto-managed by the 'set-caphost-idempotency-flag' preprovision hook
// (scripts/set-caphost-idempotency-flag.ps1): it is set to 'true' on the first deployment
// and automatically flipped to 'false' on subsequent re-provisions once the capability host
// exists (the connections become locked once owned by the capability host). This makes
// 'azd provision' idempotent. You can still override it manually if needed:
//   azd env set DEPLOY_FOUNDRY_CAPABILITY_HOST false
param deployFoundryCapabilityHost = toLower(readEnvironmentVariable('DEPLOY_FOUNDRY_CAPABILITY_HOST', 'true')) == 'true'
