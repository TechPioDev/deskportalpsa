using System.Text.Json.Serialization;

namespace Desk.Connectors.ConnectWise;

/// <summary>ConnectWise Manage API credentials. Resolved from Vault by the factory.</summary>
public sealed record ConnectWiseCredentials(string CompanyId, string PublicKey, string PrivateKey, string ClientId);

public sealed class ConnectWiseConnectorConfig
{
    /// <summary>API base, e.g. https://api-na.myconnectwise.net/v4_6_release/apis/3.0/ (trailing slash).</summary>
    public required string BaseUrl { get; init; }
    public required ConnectWiseCredentials Credentials { get; init; }
    public string WebhookSecret { get; init; } = "";
    public TimeSpan WebhookMaxSkew { get; init; } = TimeSpan.FromMinutes(5);
}

// ---- wire DTOs (subset of the ConnectWise Manage schema). CW nests references as {id, name}. ----

internal sealed class CwRef
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

internal sealed class CwCompany
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("deletedFlag")] public bool DeletedFlag { get; set; }
}

internal sealed class CwContact
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("firstName")] public string? FirstName { get; set; }
    [JsonPropertyName("lastName")] public string? LastName { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("inactiveFlag")] public bool InactiveFlag { get; set; }
}

internal sealed class CwMember
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("firstName")] public string? FirstName { get; set; }
    [JsonPropertyName("lastName")] public string? LastName { get; set; }
    [JsonPropertyName("primaryEmail")] public string? PrimaryEmail { get; set; }
    [JsonPropertyName("inactiveFlag")] public bool InactiveFlag { get; set; }
}

internal sealed class CwTicket
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("summary")] public string? Summary { get; set; }
    [JsonPropertyName("initialDescription")] public string? InitialDescription { get; set; }
    [JsonPropertyName("board")] public CwRef? Board { get; set; }
    [JsonPropertyName("status")] public CwRef? Status { get; set; }
    [JsonPropertyName("priority")] public CwRef? Priority { get; set; }
    [JsonPropertyName("type")] public CwRef? Type { get; set; }
    [JsonPropertyName("company")] public CwRef? Company { get; set; }
    [JsonPropertyName("owner")] public CwRef? Owner { get; set; }
    // The customer contact the ticket is for. CW sends the name and address inline beside the ref.
    [JsonPropertyName("contact")] public CwRef? Contact { get; set; }
    [JsonPropertyName("contactName")] public string? ContactName { get; set; }
    [JsonPropertyName("contactEmailAddress")] public string? ContactEmailAddress { get; set; }
    [JsonPropertyName("lastUpdated")] public DateTimeOffset? LastUpdated { get; set; }
    [JsonPropertyName("dateResolved")] public DateTimeOffset? DateResolved { get; set; }
    // When the ticket was raised. Distinct from the portal's own row-creation timestamp, which is
    // only ever the date this ticket happened to be imported.
    [JsonPropertyName("dateEntered")] public DateTimeOffset? DateEntered { get; set; }
    [JsonPropertyName("closedDate")] public DateTimeOffset? ClosedDate { get; set; }
    // ConnectWise keeps SLA targets on the SLA entity, not the ticket; requiredDate is the dated
    // commitment the ticket itself carries, so it is what "was this late" can be judged against.
    [JsonPropertyName("requiredDate")] public DateTimeOffset? RequiredDate { get; set; }

    /// <summary>
    /// The SLA's resolution target. Distinct from requiredDate, which is a due date somebody typed
    /// in: ConnectWise omits null fields entirely, and across a full board none of the tickets
    /// carried a requiredDate while every one of them carried this. A due date nobody sets is not
    /// the SLA — this is.
    /// </summary>
    [JsonPropertyName("resolutionGoalUTC")] public DateTimeOffset? ResolutionGoalUtc { get; set; }
    // ConnectWise returns housekeeping timestamps in an "_info" sub-object on several entities, and
    // inconsistently also at the top level. Read both and take whichever is present rather than
    // betting on one: a null here is indistinguishable from a ticket with no date, so guessing wrong
    // would look exactly like working correctly.
    [JsonPropertyName("_info")] public CwTicketInfo? Info { get; set; }

    public DateTimeOffset? RaisedAt => DateEntered ?? Info?.DateEntered;
    public DateTimeOffset? ClosedAtAny => ClosedDate ?? Info?.ClosedDate;

    /// <summary>Whichever target the ticket actually has, preferring the one a human set.</summary>
    public DateTimeOffset? SlaTargetAt => RequiredDate ?? ResolutionGoalUtc;
}

internal sealed class CwTicketInfo
{
    [JsonPropertyName("dateEntered")] public DateTimeOffset? DateEntered { get; set; }
    [JsonPropertyName("closedDate")] public DateTimeOffset? ClosedDate { get; set; }
    [JsonPropertyName("lastUpdated")] public DateTimeOffset? LastUpdated { get; set; }
}

internal sealed class CwTicketNote
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("internalAnalysisFlag")] public bool InternalAnalysisFlag { get; set; }
    [JsonPropertyName("detailDescriptionFlag")] public bool DetailDescriptionFlag { get; set; }
    [JsonPropertyName("customerUpdatedFlag")] public bool CustomerUpdatedFlag { get; set; }
    [JsonPropertyName("dateCreated")] public DateTimeOffset? DateCreated { get; set; }
    [JsonPropertyName("member")] public CwRef? Member { get; set; }
    // Set instead of "member" when a customer contact wrote the note (portal/email replies).
    [JsonPropertyName("contact")] public CwRef? Contact { get; set; }
}

internal sealed class CwHoliday
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("date")] public DateTimeOffset? Date { get; set; }
}

internal sealed class CwAgreement
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("type")] public CwRef? Type { get; set; }
    [JsonPropertyName("agreementStatus")] public string? AgreementStatus { get; set; }
    [JsonPropertyName("startDate")] public DateTimeOffset? StartDate { get; set; }
    [JsonPropertyName("endDate")] public DateTimeOffset? EndDate { get; set; }
    [JsonPropertyName("noEndingDateFlag")] public bool NoEndingDateFlag { get; set; }
}

internal sealed class CwDocument
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("fileName")] public string? FileName { get; set; }
    [JsonPropertyName("size")] public long? Size { get; set; }
    [JsonPropertyName("owner")] public string? Owner { get; set; }
    [JsonPropertyName("createdOnDate")] public DateTimeOffset? CreatedOnDate { get; set; }
    [JsonPropertyName("documentType")] public CwRef? DocumentType { get; set; }
}

internal sealed class CwBoardTeam
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("boardId")] public long? BoardId { get; set; }
    // Member ids only — the board-scoped route returns these; the bulk /service/teams route does not.
    [JsonPropertyName("members")] public List<long>? Members { get; set; }
}

internal sealed class CwTimeEntry
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("member")] public CwRef? Member { get; set; }
    [JsonPropertyName("actualHours")] public decimal? ActualHours { get; set; }
    [JsonPropertyName("billableOption")] public string? BillableOption { get; set; }
    [JsonPropertyName("timeStart")] public DateTimeOffset? TimeStart { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }
    [JsonPropertyName("workType")] public CwRef? WorkType { get; set; }
}

internal sealed class CwConfiguration
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("type")] public CwRef? Type { get; set; }
    [JsonPropertyName("status")] public CwRef? Status { get; set; }
    [JsonPropertyName("serialNumber")] public string? SerialNumber { get; set; }
    [JsonPropertyName("tagNumber")] public string? TagNumber { get; set; }
}
