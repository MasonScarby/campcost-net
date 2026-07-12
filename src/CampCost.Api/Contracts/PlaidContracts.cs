using System.Text.Json.Serialization;

namespace CampCost.Api.Contracts;

// Request bodies for PlaidController.
// WebhookPayload is deliberately minimal — only the two fields the webhook handler
// dispatches on; the full payload is re-verified/parsed elsewhere for security.
public record ExchangeRequest(
    [property: JsonPropertyName("public_token")] string PublicToken,
    [property: JsonPropertyName("institution_name")] string? InstitutionName
);

public record WebhookPayload(
    [property: JsonPropertyName("webhook_type")] string? WebhookType,
    [property: JsonPropertyName("item_id")] string? ItemId
);
