namespace Desk.Domain.Enums;

/// <summary>PSA / service-desk providers, in the development priority order from the Integration Plan.</summary>
public enum ProviderType
{
    // Wave 1 — reference implementations
    ConnectWisePsa = 1,
    AutotaskPsa = 2,
    // Wave 2+
    HaloPsa = 10,
    Syncro = 11,
    SuperOps = 12,
    Atera = 13,
    KaseyaBms = 14,
    NableMspManager = 15,
    DeskDay = 16,
    ServiceNow = 20,
    Freshservice = 21,
    JiraServiceManagement = 22,
    ManageEngineServiceDeskPlus = 23,
    Zendesk = 24,
    ZohoDesk = 25,
}

public enum ConnectionStatus
{
    Disabled = 0,
    Pending = 1,
    Healthy = 2,
    Degraded = 3,
    Failed = 4,
}

/// <summary>
/// Where a connection stands, as one word a person can act on. Worked out from the connection's
/// own fields and whether a sync is running (<c>ConnectionStates</c>); it is not stored, so it can
/// never disagree with them.
/// </summary>
public enum ConnectionState
{
    /// <summary>Created, not yet switched on. Its credentials have not been accepted by the PSA.</summary>
    Setup = 0,
    /// <summary>The PSA has accepted the credentials; nothing has been synced yet.</summary>
    Connected = 1,
    /// <summary>A sync is running now.</summary>
    Syncing = 2,
    Healthy = 3,
    /// <summary>The last sync failed; the next one may not.</summary>
    Degraded = 4,
    /// <summary>The PSA rejected the credentials. Nothing will work until they are entered again.</summary>
    AuthRequired = 5,
    /// <summary>Inbound sync is paused by an administrator.</summary>
    Paused = 6,
    /// <summary>A test of the connection failed.</summary>
    Error = 7,
    Disabled = 8,
    Archived = 9,
}

/// <summary>Provider readiness for the final supported-provider matrix.</summary>
public enum ProviderReadiness
{
    Planned = 0,
    ApiResearchComplete = 1,
    ConnectorFoundationComplete = 2,
    DevelopmentInProgress = 3,
    ReadyForIntegrationTesting = 4,
    ReadyForQa = 5,
    LimitedAvailability = 6,
    ProductionReady = 7,
    BlockedByProvider = 8,
    UnsupportedApiFeature = 9,
}

public enum TicketSyncStatus
{
    PendingCreate = 0,
    Synced = 1,
    PendingUpdate = 2,
    Conflict = 3,
    Error = 4,
}

/// <summary>Direction a mapped field synchronizes.</summary>
public enum MappingDirection
{
    /// <summary>Portal is authoritative; value pushed to PSA only.</summary>
    PortalToProvider = 1,
    /// <summary>PSA is authoritative; value pulled to portal only.</summary>
    ProviderToPortal = 2,
    Bidirectional = 3,
    /// <summary>Read-only field — never written in either direction.</summary>
    ReadOnly = 4,
}

/// <summary>
/// Scope at which a field-mapping rule applies. Resolution walks from the most
/// specific present rule to the least specific (Conditional wins over Platform).
/// </summary>
public enum MappingScope
{
    PlatformDefault = 0,
    ProviderDefault = 1,
    ConnectionOverride = 2,
    ClientCompanyOverride = 3,
    QueueOrBoardOverride = 4,
    TicketTypeOverride = 5,
    CustomField = 6,
    Conditional = 7,
}

/// <summary>Built-in role templates. Effective access is always evaluated via permission claims.</summary>
public enum RoleType
{
    PlatformSuperAdministrator = 1,
    MspAdministrator = 2,
    Manager = 3,
    Technician = 4,
    ClientAdministrator = 5,
    ClientUser = 6,
    Auditor = 7,
}

public enum AttachmentScanStatus
{
    Pending = 0,
    Clean = 1,
    Quarantined = 2,
    Rejected = 3,
}

public enum BackgroundJobStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    DeadLettered = 4,
}

/// <summary>
/// Where a ticket came from, which decides three things nothing else can answer: whether the portal
/// pushes it to a PSA, whether a client may ever see it, and which side of the reporting split it
/// falls on (client work versus the team's own work).
/// </summary>
public enum TicketOrigin
{
    /// <summary>Imported from, or created in, a PSA. The PSA is its system of record.</summary>
    Psa = 0,

    /// <summary>Raised by the team for the team. Never pushed anywhere, never visible to a client.</summary>
    Internal = 1,

    /// <summary>Opened automatically by a monitoring tool (RMM). Lives on its own board.</summary>
    Rmm = 2,
}

/// <summary>What a board is for. A board's kind fixes the rules its tickets follow.</summary>
public enum BoardKind
{
    /// <summary>The team's own work. Staff only, always.</summary>
    Internal = 0,

    /// <summary>Alerts from a monitoring tool. May be shown to the client it concerns, if enabled.</summary>
    Rmm = 1,
}
