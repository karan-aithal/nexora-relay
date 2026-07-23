namespace OpenForecourt.Abstractions.Domain;

/// <summary>
/// The lifecycle state of a single fuel pump within a fuelling session.
/// </summary>
/// <remarks>
/// The nominal happy path is
/// <c>Idle → Authorising → Authorised → NozzleLifted → Dispensing → DispenseComplete →
/// Settling → Idle</c>. <see cref="Error"/> and <see cref="OutOfService"/> are reachable
/// from the operational states. State transitions are owned by the pump manager, not by
/// this enum.
/// </remarks>
public enum PumpState
{
    /// <summary>No transaction in progress; the pump is available.</summary>
    Idle,

    /// <summary>A card has been presented and online authorisation is in flight.</summary>
    Authorising,

    /// <summary>Authorisation approved; the customer may lift the nozzle.</summary>
    Authorised,

    /// <summary>The nozzle has been lifted; dispensing may begin.</summary>
    NozzleLifted,

    /// <summary>Fuel is actively being dispensed and the totalizer is counting.</summary>
    Dispensing,

    /// <summary>The nozzle is holstered; the final dispensed volume is known.</summary>
    DispenseComplete,

    /// <summary>The transaction is being finalised (capture/settlement) with the host.</summary>
    Settling,

    /// <summary>A recoverable fault occurred during the session.</summary>
    Error,

    /// <summary>The pump is administratively or physically unavailable.</summary>
    OutOfService,
}
