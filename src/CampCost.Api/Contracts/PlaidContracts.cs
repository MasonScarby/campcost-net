using System.Text.Json.Serialization;

namespace CampCost.Api.Contracts;

// Request bodies for PlaidController.
// WebhookPayload is deliberately minimal — only the two fields the webhook handler
// dispatches on; the full payload is re-verified/parsed elsewhere for security.
// Optional body for POST /api/plaid/link-token. `days` is the requested transaction-history
// window (14/30/60/90). The controller whitelists it — an out-of-range or missing value falls
// back to the default, so a tampered request can never widen the Plaid fetch beyond the presets.
public record LinkTokenRequest(
    [property: JsonPropertyName("days")] int? Days
);

public record ExchangeRequest(
    [property: JsonPropertyName("public_token")] string PublicToken,
    [property: JsonPropertyName("institution_name")] string? InstitutionName
);

public record WebhookPayload(
    [property: JsonPropertyName("webhook_type")] string? WebhookType,
    [property: JsonPropertyName("item_id")] string? ItemId
);

public record DisconnectRequest(
    [property: JsonPropertyName("item_id")] string ItemId
);
