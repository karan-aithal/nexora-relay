namespace OpenForecourt.PumpManager;

/// <summary>
/// OFP-1 protocol constants and code enums — the C# side of <c>docs/protocol-pump.md</c> and
/// the exact mirror of <c>firmware/pump/src/protocol.h</c>. Every value here must equal its C
/// counterpart; the golden-frame test is the cross-language guard against drift.
/// </summary>
public static class OfpProtocol
{
    /// <summary>Start-of-frame delimiter.</summary>
    public const byte Stx = 0x02;

    /// <summary>End-of-frame delimiter.</summary>
    public const byte Etx = 0x03;

    /// <summary>Byte-stuffing escape (DLE).</summary>
    public const byte Esc = 0x10;

    /// <summary>XOR applied to a reserved byte when stuffing it.</summary>
    public const byte StuffXor = 0x20;

    /// <summary>ACK response code = command code OR this mask (<c>docs §2</c>).</summary>
    public const byte AckMask = 0x80;

    /// <summary>Negative-acknowledgement command code; payload is one <see cref="PumpError"/>.</summary>
    public const byte Nak = 0x7F;

    /// <summary>Largest payload the protocol carries (TOTALS response is 16 bytes).</summary>
    public const int MaxPayload = 32;
}

/// <summary>Manager-to-pump command codes (<c>docs §2</c>).</summary>
public enum PumpCommand : byte
{
    /// <summary>Authorise a fuelling, optionally with a volume/value preset.</summary>
    Authorise = 0x01,

    /// <summary>Cancel an authorisation or acknowledge settlement (clears COMPLETE/FAULT).</summary>
    CancelAuth = 0x02,

    /// <summary>Request current state and dispense counters.</summary>
    StatusReq = 0x03,

    /// <summary>Request the lifetime totalizer.</summary>
    TotalsReq = 0x04,

    /// <summary>Pause an in-progress dispense.</summary>
    Suspend = 0x05,

    /// <summary>Resume a paused dispense.</summary>
    Resume = 0x06,
}

/// <summary>Unsolicited pump-to-manager event codes (<c>docs §2</c>).</summary>
public enum PumpEvent : byte
{
    /// <summary>The nozzle was lifted.</summary>
    NozzleUp = 0x20,

    /// <summary>The nozzle was holstered.</summary>
    NozzleDown = 0x21,

    /// <summary>A flow-meter update: cumulative volume and value this dispense.</summary>
    FlowUpdate = 0x22,

    /// <summary>Dispense finished: final volume and value. Re-emitted until acked (<c>docs §4.4</c>).</summary>
    DispenseComplete = 0x23,

    /// <summary>A fault latched; payload is one <see cref="FaultCode"/>.</summary>
    Fault = 0x24,
}

/// <summary>Command error codes carried in a NAK payload (<c>docs §5</c>).</summary>
public enum PumpError : byte
{
    /// <summary>No error (not used in a NAK).</summary>
    None = 0x00,

    /// <summary>CRC check failed (diagnostic only; corrupt frames are dropped, not NAK'd).</summary>
    BadCrc = 0x01,

    /// <summary>Framing/length error (diagnostic only).</summary>
    BadFrame = 0x02,

    /// <summary>The command is not valid in the current dispenser state.</summary>
    IllegalState = 0x03,

    /// <summary>The command code is unrecognised.</summary>
    UnknownCmd = 0x04,

    /// <summary>The AUTHORISE preset mode or limit is invalid.</summary>
    PresetInvalid = 0x05,
}

/// <summary>Latched fault codes carried in a FAULT event payload (<c>docs §5</c>).</summary>
public enum FaultCode : byte
{
    /// <summary>No fault.</summary>
    None = 0x00,

    /// <summary>Nozzle lifted with no authorisation.</summary>
    NozzleUnexpected = 0x10,

    /// <summary>Flow pulses with no active dispense.</summary>
    FlowWhileIdle = 0x11,

    /// <summary>Run-loop watchdog not kicked in time.</summary>
    Watchdog = 0x12,

    /// <summary>Dispensing but no flow pulses for the stall timeout.</summary>
    MeterStall = 0x13,
}

/// <summary>Dispenser FSM state codes (<c>docs §6</c>). Mirrors the firmware exactly.</summary>
public enum DispenserState : byte
{
    /// <summary>Available; no authorisation.</summary>
    Idle = 0,

    /// <summary>Preset loaded; waiting for nozzle lift.</summary>
    Authorised = 1,

    /// <summary>Nozzle up; fuel flowing; totalizer counting.</summary>
    Dispensing = 2,

    /// <summary>Nozzle holstered / limit reached; final totals held.</summary>
    Complete = 3,

    /// <summary>Dispensing paused by the controller.</summary>
    Suspended = 4,

    /// <summary>Latched fault; awaits clear.</summary>
    Fault = 5,
}

/// <summary>AUTHORISE preset modes (<c>docs §3.1</c>).</summary>
public enum PresetMode : byte
{
    /// <summary>No limit (full tank).</summary>
    None = 0,

    /// <summary>Stop at a volume in millilitres.</summary>
    Volume = 1,

    /// <summary>Stop at a value in currency minor units.</summary>
    Value = 2,
}
