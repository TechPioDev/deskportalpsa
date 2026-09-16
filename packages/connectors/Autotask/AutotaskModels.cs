using System.Text.Json.Serialization;

namespace Desk.Connectors.Autotask;

/// <summary>Autotask REST credentials. Resolved from Vault by the factory — never persisted in the DB.</summary>
public sealed record AutotaskCredentials(string ApiIntegrationCode, string UserName, string Secret);

/// <summary>Per-connection Autotask settings.</summary>
public sealed class AutotaskConnectorConfig
{
    /// <summary>Zone base URL, e.g. https://webservices2.autotask.net/atservicesrest/ (trailing slash).</summary>
    public required string BaseUrl { get; init; }
    public required AutotaskCredentials Credentials { get; init; }
    public string WebhookSecret { get; init; } = "";
    public TimeSpan WebhookMaxSkew { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>publish value the connector writes for a public (client-visible) note.</summary>
    public int PublicPublishValue { get; init; } = 1;
    /// <summary>publish value used for internal-only notes (never mirrored to the portal).</summary>
    public int InternalPublishValue { get; init; } = 2;

    /// <summary>
    /// Resource that owns time logged from the portal. Autotask rejects its own API-only user here,
    /// and requires a resource on every ticket time entry, so a real technician must be configured
    /// on the connection before time logging works.
    /// </summary>
    public long? DefaultTimeEntryResourceId { get; init; }

    /// <summary>Role to bill the time under. Resolved from the resource's own roles when unset.</summary>
    public long? DefaultTimeEntryRoleId { get; init; }
}

// ---- wire DTOs (subset of the Autotask REST schema the connector uses) ----

internal sealed class AtQueryResult<T>
{
    [JsonPropertyName("items")] public List<T> Items { get; set; } = [];
    [JsonPropertyName("pageDetails")] public AtPageDetails? PageDetails { get; set; }
}

internal sealed class AtPageDetails
{
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("nextPageUrl")] public string? NextPageUrl { get; set; }
}

internal sealed class AtItemResult<T>
{
    [JsonPropertyName("item")] public T? Item { get; set; }
}

internal sealed class AtCreateResult
{
    [JsonPropertyName("itemId")] public long ItemId { get; set; }
}

internal sealed class AtCompany
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("companyName")] public string? CompanyName { get; set; }
    [JsonPropertyName("isActive")] public bool IsActive { get; set; }
}

internal sealed class AtContact
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("companyID")] public long CompanyId { get; set; }
    [JsonPropertyName("emailAddress")] public string? EmailAddress { get; set; }
    [JsonPropertyName("firstName")] public string? FirstName { get; set; }
    [JsonPropertyName("lastName")] public string? LastName { get; set; }
    [JsonPropertyName("isActive")] public bool IsActive { get; set; }
}

internal sealed class AtResource
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("firstName")] public string? FirstName { get; set; }
    [JsonPropertyName("lastName")] public string? LastName { get; set; }
    [JsonPropertyName("isActive")] public bool IsActive { get; set; }
}

internal sealed class AtTicket
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("status")] [JsonConverter(typeof(FlexibleStringConverter))] public string? Status { get; set; }
    [JsonPropertyName("priority")] [JsonConverter(typeof(FlexibleStringConverter))] public string? Priority { get; set; }
    [JsonPropertyName("queueID")] [JsonConverter(typeof(FlexibleStringConverter))] public string? QueueId { get; set; }
    [JsonPropertyName("ticketCategory")] [JsonConverter(typeof(FlexibleStringConverter))] public string? Category { get; set; }
    [JsonPropertyName("companyID")] public long CompanyId { get; set; }
    // The customer contact the ticket is for; only the id rides on the ticket.
    [JsonPropertyName("contactID")] public long? ContactId { get; set; }
    [JsonPropertyName("assignedResourceID")] [JsonConverter(typeof(FlexibleStringConverter))] public string? AssignedResourceId { get; set; }
    [JsonPropertyName("createDate")] public DateTimeOffset? CreateDate { get; set; }
    [JsonPropertyName("lastActivityDate")] public DateTimeOffset? LastActivityDate { get; set; }
    [JsonPropertyName("resolvedDateTime")] public DateTimeOffset? ResolvedDateTime { get; set; }
    // Autotask separates "resolved" from "completed": a ticket can be resolved and stay open for
    // review. completedDate is the closure a throughput metric means.
    [JsonPropertyName("completedDate")] public DateTimeOffset? CompletedDate { get; set; }
    // The SLA resolution target. Autotask also exposes firstResponseDueDateTime and
    // resolutionPlanDueDateTime; this is the one "was it resolved in time" is measured against.
    [JsonPropertyName("resolvedDueDateTime")] public DateTimeOffset? ResolvedDueDateTime { get; set; }
    // Falls back to the ticket's own due date where no SLA applies to it.
    [JsonPropertyName("dueDateTime")] public DateTimeOffset? DueDateTime { get; set; }
}

internal sealed class AtTicketNote
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("ticketID")] public long TicketId { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("publish")] public int Publish { get; set; }
    [JsonPropertyName("createDateTime")] public DateTimeOffset? CreateDateTime { get; set; }
    // Autotask names the note author "creatorResourceID" (Tickets use "creatorResourceID" too);
    // "createdByContactID" is set instead when the author was a customer contact.
    [JsonPropertyName("creatorResourceID")] public long? CreatorResourceId { get; set; }
    [JsonPropertyName("createdByContactID")] public long? CreatedByContactId { get; set; }
}

internal sealed class AtHoliday
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("holidayName")] public string? HolidayName { get; set; }
    [JsonPropertyName("holidayDate")] public DateTimeOffset? HolidayDate { get; set; }
}

internal sealed class AtContract
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("contractName")] public string? ContractName { get; set; }
    [JsonPropertyName("contractType")] public long? ContractType { get; set; }
    [JsonPropertyName("status")] public long? Status { get; set; }
    [JsonPropertyName("startDate")] public DateTimeOffset? StartDate { get; set; }
    [JsonPropertyName("endDate")] public DateTimeOffset? EndDate { get; set; }
}

internal sealed class AtFieldInfoResult
{
    [JsonPropertyName("fields")] public List<AtFieldInfo> Fields { get; set; } = [];
}

internal sealed class AtFieldInfo
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("picklistValues")] public List<AtPicklistValue> PicklistValues { get; set; } = [];
}

internal sealed class AtPicklistValue
{
    [JsonPropertyName("value")] public string? Value { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("isActive")] public bool IsActive { get; set; }
}

internal sealed class AtTicketAttachment
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("parentID")] public long? ParentId { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("fullPath")] public string? FullPath { get; set; }
    [JsonPropertyName("contentType")] public string? ContentType { get; set; }
    // Autotask serialises this as a decimal (e.g. 70.0), so it cannot bind to a long.
    [JsonPropertyName("fileSize")] public double? FileSize { get; set; }
    [JsonPropertyName("attachDate")] public DateTimeOffset? AttachDate { get; set; }
    [JsonPropertyName("attachedByResourceID")] public long? AttachedByResourceId { get; set; }
    [JsonPropertyName("attachedByContactID")] public long? AttachedByContactId { get; set; }
    [JsonPropertyName("ticketNoteID")] public long? TicketNoteId { get; set; }
    // Only returned on a get-by-id; the query projection omits it to keep list reads small.
    [JsonPropertyName("data")] public string? Data { get; set; }
}

internal sealed class AtTimeEntry
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("ticketID")] public long? TicketId { get; set; }
    [JsonPropertyName("resourceID")] public long? ResourceId { get; set; }
    [JsonPropertyName("hoursWorked")] public decimal? HoursWorked { get; set; }
    [JsonPropertyName("hoursToBill")] public decimal? HoursToBill { get; set; }
    [JsonPropertyName("isNonBillable")] public bool? IsNonBillable { get; set; }
    [JsonPropertyName("showOnInvoice")] public bool? ShowOnInvoice { get; set; }
    [JsonPropertyName("billingCodeID")] public long? BillingCodeId { get; set; }
    [JsonPropertyName("roleID")] public long? RoleId { get; set; }
    [JsonPropertyName("dateWorked")] public DateTimeOffset? DateWorked { get; set; }
    [JsonPropertyName("summaryNotes")] public string? SummaryNotes { get; set; }
    [JsonPropertyName("internalNotes")] public string? InternalNotes { get; set; }
}

internal sealed class AtResourceRole
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("resourceID")] public long ResourceId { get; set; }
    [JsonPropertyName("roleID")] public long RoleId { get; set; }
    [JsonPropertyName("queueID")] public long? QueueId { get; set; }
    [JsonPropertyName("isActive")] public bool IsActive { get; set; }
}

internal sealed class AtRole
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("isActive")] public bool IsActive { get; set; }
}

internal sealed class AtBillingCode
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("isActive")] public bool IsActive { get; set; }
    [JsonPropertyName("useType")] public int? UseType { get; set; }
}
