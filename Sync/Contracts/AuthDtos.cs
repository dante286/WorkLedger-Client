// Vendored verbatim from WorkLedger-API, src/WorkLedger.Contracts/Auth/AuthDtos.cs @ 2065662.
// The server is the source of truth for these shapes - see Sync/Contracts/README.md before editing.
using System;

namespace WorkLedger.Contracts.Auth;

/// <summary>DeviceId is the client's per-install GUID string (see Sync.SyncChangeDto.OriginDeviceId).</summary>
public sealed record RegisterDeviceRequest(string DeviceId, string? Label);

public sealed record RegisterDeviceResponse(string Token, DateTime ExpiresAtUtc);

public sealed record WhoAmIResponse(int AccountId, string DeviceId);
