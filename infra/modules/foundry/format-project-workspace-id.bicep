// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// 
// This solution is provided for testing, learning, and evaluation purposes only.
// It is not intended for production use. Microsoft provides no support or guarantees.
// Use at your own risk.
//
// Converts the Foundry project's internal workspace ID (a 32-char hex string) into the
// dashed GUID form required to scope post-capability-host role assignments.
@description('The Foundry project internal workspace ID (32 hex characters, no dashes).')
param projectWorkspaceId string

var part1 = substring(projectWorkspaceId, 0, 8)
var part2 = substring(projectWorkspaceId, 8, 4)
var part3 = substring(projectWorkspaceId, 12, 4)
var part4 = substring(projectWorkspaceId, 16, 4)
var part5 = substring(projectWorkspaceId, 20, 12)

output projectWorkspaceIdGuid string = '${part1}-${part2}-${part3}-${part4}-${part5}'
