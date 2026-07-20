using System.Text.Json.Serialization;

namespace CampCost.Api.Contracts;

// Request bodies for AccountController. Extracted from the controller so the Api
// layer keeps controllers to routing/gating only (per CLAUDE.md architecture rule).
public record SetPlanRequest([property: JsonPropertyName("plan")] string Plan);
