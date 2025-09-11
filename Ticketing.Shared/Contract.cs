namespace Ticketing.Shared;

public static class Queues
{
    // Durable queue for reservation requests (v1 schema)
    public const string Reservations = "reservations.v1";
}

// What the client posts to start a reservation
public sealed record ReservationRequest(
    string EventId,
    int Quantity,
    string? UserId // optional for now
);

// What the API returns immediately (Accepted)
public sealed record ReservationAccepted(string TrackingId);

// Status values the client can poll for
public enum ReservationStatusKind
{
    Pending,   // Enqueued, not processed yet
    Reserved,  // Held successfully (awaiting payment)
    SoldOut,   // Not enough stock to reserve
    Expired,   // Hold expired before payment
    Failed,     // Processing error (transient or permanent)
    Confirmed   // NEW
}

// Polled by client to see current state
public sealed record ReservationStatus(
    string TrackingId,
    ReservationStatusKind Status,
    long? ExpiresAtEpoch = null, // unix seconds when hold expires
    int? Quantity = null,
    string? EventId = null,
    string? Message = null
);

// Message placed on the queue for the worker
public sealed record ReservationMessage(
    string TrackingId,
    string EventId,
    int Quantity,
    string? UserId
);
