using System.Buffers.Binary;
using System.Threading.Channels;
using OpenForecourt.Abstractions.Domain;
using OpenForecourt.Abstractions.Ports;

namespace OpenForecourt.PumpManager;

/// <summary>How a command exchange ended.</summary>
public enum CommandStatus
{
    /// <summary>The pump ACK'd the command.</summary>
    Ack,

    /// <summary>The pump refused the command (see <see cref="CommandResponse.Error"/>).</summary>
    Nak,

    /// <summary>No response after the configured retries — the pump is unreachable or silent.</summary>
    Timeout,
}

/// <summary>The outcome of one command exchange.</summary>
/// <param name="Status">ACK, NAK or timeout.</param>
/// <param name="Error">The NAK reason when <see cref="CommandStatus.Nak"/>.</param>
/// <param name="Payload">The ACK response payload (STATUS/TOTALS carry data); otherwise empty.</param>
public readonly record struct CommandResponse(CommandStatus Status, PumpError Error, ReadOnlyMemory<byte> Payload);

/// <summary>Parsed STATUS response (<c>docs §3.3</c>).</summary>
/// <param name="State">The reported dispenser state.</param>
/// <param name="Volume">Current dispense volume.</param>
/// <param name="Value">Current dispense value.</param>
public readonly record struct PumpStatus(DispenserState State, Volume Volume, Money Value);

/// <summary>Parsed TOTALS response — the lifetime totalizer (<c>docs §3.4</c>).</summary>
/// <param name="Volume">Lifetime volume dispensed.</param>
/// <param name="Value">Lifetime value dispensed.</param>
public readonly record struct PumpTotals(Volume Volume, Money Value);

/// <summary>An unsolicited pump observation surfaced to the manager's consumers.</summary>
/// <param name="PumpId">The pump this came from.</param>
/// <param name="Event">The event code.</param>
/// <param name="State">The mirrored dispenser state after applying the event.</param>
/// <param name="Volume">Dispense volume for FLOW_UPDATE / DISPENSE_COMPLETE.</param>
/// <param name="Value">Dispense value for FLOW_UPDATE / DISPENSE_COMPLETE.</param>
/// <param name="Fault">The fault code for FAULT.</param>
/// <param name="ProtocolViolation">True if the firmware reported a transition the mirrored FSM forbids.</param>
/// <param name="Detail">Human-readable context (e.g. the violation).</param>
public readonly record struct PumpNotification(
    int PumpId,
    PumpEvent Event,
    DispenserState State,
    Volume Volume,
    Money Value,
    FaultCode Fault,
    bool ProtocolViolation,
    string? Detail);

/// <summary>Tuning for a pump session; every timeout is measured on <see cref="IClock"/>.</summary>
public sealed record PumpSessionConfig
{
    /// <summary>How long to wait for an ACK/NAK before retransmitting (<c>docs §4.2</c>).</summary>
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromMilliseconds(1000);

    /// <summary>Maximum retransmissions before a command fails (<c>docs §4.2</c>).</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>Currency for value amounts on this forecourt.</summary>
    public string CurrencyCode { get; init; } = "GBP";
}

/// <summary>
/// One logical session with a single pump. A single-threaded actor owns all mutable state:
/// the outbound-command channel and inbound-frame channel are drained by one processing loop,
/// so there are <b>no locks</b> around the pump state (CLAUDE.md Phase 4). The loop correlates
/// each command with its ACK/NAK by sequence number, retransmits on timeout, and advances a
/// mirrored copy of the dispenser FSM so any illegal transition the firmware reports is flagged
/// as a protocol violation.
/// </summary>
public sealed class PumpSession
{
    private readonly IPumpTransport _transport;
    private readonly IClock _clock;
    private readonly PumpSessionConfig _cfg;
    private readonly Action<string>? _log;

    // One channel per direction (CLAUDE.md Phase 4). Single writer / single reader each.
    private readonly Channel<OutboundCommand> _outbound =
        Channel.CreateUnbounded<OutboundCommand>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<OfpMessage> _inbound =
        Channel.CreateUnbounded<OfpMessage>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private Pending? _pending;
    private byte _nextSeq;
    private int _lastEventSeq = -1;
    private byte _lastEventCode;
    private volatile bool _resyncQueued;

    /// <summary>Creates a session. Nothing runs until <see cref="RunAsync"/>.</summary>
    public PumpSession(int id, IPumpTransport transport, IClock clock, PumpSessionConfig? config = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(clock);
        Id = id;
        _transport = transport;
        _clock = clock;
        _cfg = config ?? new PumpSessionConfig();
        _log = log;
        _transport.StateChanged += OnTransportStateChanged;
    }

    /// <summary>The pump identifier.</summary>
    public int Id { get; }

    /// <summary>The mirrored dispenser state. Updated only by the actor loop; read elsewhere for display.</summary>
    public DispenserState State { get; private set; } = DispenserState.Idle;

    /// <summary>Raised for each unsolicited pump event (and protocol violations).</summary>
    public event EventHandler<PumpNotification>? Observed;

    /// <summary>Authorises a fuelling, optionally with a preset limit.</summary>
    public Task<CommandResponse> AuthoriseAsync(PresetMode mode, uint limit, CancellationToken cancellationToken)
    {
        var payload = new byte[5];
        payload[0] = (byte)mode;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(1), limit);
        return SubmitAsync(PumpCommand.Authorise, payload, cancellationToken);
    }

    /// <summary>Cancels an authorisation or acknowledges settlement (clears COMPLETE/FAULT).</summary>
    public Task<CommandResponse> CancelAsync(CancellationToken cancellationToken) =>
        SubmitAsync(PumpCommand.CancelAuth, [], cancellationToken);

    /// <summary>Pauses an in-progress dispense.</summary>
    public Task<CommandResponse> SuspendAsync(CancellationToken cancellationToken) =>
        SubmitAsync(PumpCommand.Suspend, [], cancellationToken);

    /// <summary>Resumes a paused dispense.</summary>
    public Task<CommandResponse> ResumeAsync(CancellationToken cancellationToken) =>
        SubmitAsync(PumpCommand.Resume, [], cancellationToken);

    /// <summary>Requests current state and dispense counters.</summary>
    public async Task<PumpStatus?> StatusAsync(CancellationToken cancellationToken)
    {
        var r = await SubmitAsync(PumpCommand.StatusReq, [], cancellationToken).ConfigureAwait(false);
        if (r.Status != CommandStatus.Ack || r.Payload.Length < 9)
        {
            return null;
        }

        var s = r.Payload.Span;
        return new PumpStatus((DispenserState)s[0], new Volume(BinaryPrimitives.ReadUInt32BigEndian(s[1..])),
            new Money(BinaryPrimitives.ReadUInt32BigEndian(s[5..]), _cfg.CurrencyCode));
    }

    /// <summary>Requests the lifetime totalizer.</summary>
    public async Task<PumpTotals?> TotalsAsync(CancellationToken cancellationToken)
    {
        var r = await SubmitAsync(PumpCommand.TotalsReq, [], cancellationToken).ConfigureAwait(false);
        if (r.Status != CommandStatus.Ack || r.Payload.Length < 16)
        {
            return null;
        }

        var s = r.Payload.Span;
        return new PumpTotals(new Volume((long)BinaryPrimitives.ReadUInt64BigEndian(s)),
            new Money((long)BinaryPrimitives.ReadUInt64BigEndian(s[8..]), _cfg.CurrencyCode));
    }

    /// <summary>Runs the session's read and processing loops until cancelled. Never throws on cancellation.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var read = ReadLoopAsync(cancellationToken);
        var process = ProcessLoopAsync(cancellationToken);
        try
        {
            await Task.WhenAll(read, process).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Clean shutdown.
        }
        finally
        {
            _transport.StateChanged -= OnTransportStateChanged;
            FailAllOutstanding();
        }
    }

    private async Task<CommandResponse> SubmitAsync(PumpCommand command, byte[] payload, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<CommandResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _outbound.Writer.WriteAsync(new OutboundCommand(command, payload, tcs), cancellationToken).ConfigureAwait(false);
        await using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)).ConfigureAwait(false))
        {
            return await tcs.Task.ConfigureAwait(false);
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in _transport.ReceiveFrames(cancellationToken).ConfigureAwait(false))
            {
                if (frame.Payload.Length >= 2)
                {
                    _inbound.Writer.TryWrite(OfpMessage.Parse(frame.Payload));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        finally
        {
            _inbound.Writer.TryComplete();
        }
    }

    private async Task ProcessLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            while (_inbound.Reader.TryRead(out var msg))
            {
                HandleInbound(msg);
            }

            if (_pending is null && _outbound.Reader.TryRead(out var cmd))
            {
                await StartCommandAsync(cmd, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (_pending is null)
            {
                var inReady = _inbound.Reader.WaitToReadAsync(cancellationToken).AsTask();
                var outReady = _outbound.Reader.WaitToReadAsync(cancellationToken).AsTask();
                await Task.WhenAny(inReady, outReady).ConfigureAwait(false);
            }
            else
            {
                var remaining = _pending.Deadline - _clock.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    await OnTimeoutAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var inReady = _inbound.Reader.WaitToReadAsync(cancellationToken).AsTask();
                var deadline = _clock.Delay(remaining, cancellationToken);
                await Task.WhenAny(inReady, deadline).ConfigureAwait(false);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task StartCommandAsync(OutboundCommand cmd, CancellationToken cancellationToken)
    {
        byte seq = _nextSeq++;
        var body = new OfpMessage(seq, (byte)cmd.Cmd, cmd.Payload).ToBody();
        _pending = new Pending(cmd, seq, body, _cfg.MaxRetries, _clock.UtcNow + _cfg.ResponseTimeout);
        await SendAsync(body, cancellationToken).ConfigureAwait(false);
    }

    private async Task OnTimeoutAsync(CancellationToken cancellationToken)
    {
        var pending = _pending!;
        if (pending.RetriesLeft > 0)
        {
            pending.RetriesLeft--;
            pending.Deadline = _clock.UtcNow + _cfg.ResponseTimeout;
            Log($"pump {Id}: {pending.Command.Cmd} seq {pending.Seq} timed out, retransmit ({pending.RetriesLeft} left)");
            await SendAsync(pending.Body, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            Log($"pump {Id}: {pending.Command.Cmd} seq {pending.Seq} failed after retries — link down");
            CompletePending(new CommandResponse(CommandStatus.Timeout, PumpError.None, default));
        }
    }

    private async Task SendAsync(byte[] body, CancellationToken cancellationToken)
    {
        try
        {
            await _transport.SendFrameAsync(new PumpFrame(body), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Link is down mid-send; the deadline will drive a retransmit once reconnected.
            Log($"pump {Id}: send failed ({ex.GetType().Name}); will retry on timeout");
        }
    }

    private void HandleInbound(OfpMessage msg)
    {
        byte code = msg.Code;
        if (code >= OfpProtocol.AckMask)
        {
            HandleAck(msg, (byte)(code & ~OfpProtocol.AckMask));
        }
        else if (code == OfpProtocol.Nak)
        {
            if (_pending is not null && msg.Seq == _pending.Seq)
            {
                var err = msg.Payload.Length > 0 ? (PumpError)msg.Payload.Span[0] : PumpError.None;
                CompletePending(new CommandResponse(CommandStatus.Nak, err, default));
            }
        }
        else
        {
            HandleEvent(msg);
        }
    }

    private void HandleAck(OfpMessage msg, byte command)
    {
        if (_pending is null || msg.Seq != _pending.Seq || command != (byte)_pending.Command.Cmd)
        {
            return; // stray or late ACK for an already-resolved command
        }

        // Advance the mirrored FSM for the acked command; STATUS is authoritative resync (§8).
        switch ((PumpCommand)command)
        {
            case PumpCommand.Authorise: ApplyFsm(DispenserFsm.Trigger.Authorise, "AUTHORISE ack"); break;
            case PumpCommand.CancelAuth: ApplyFsm(DispenserFsm.Trigger.Cancel, "CANCEL ack"); break;
            case PumpCommand.Suspend: ApplyFsm(DispenserFsm.Trigger.Suspend, "SUSPEND ack"); break;
            case PumpCommand.Resume: ApplyFsm(DispenserFsm.Trigger.Resume, "RESUME ack"); break;
            case PumpCommand.StatusReq when msg.Payload.Length >= 1:
                State = (DispenserState)msg.Payload.Span[0];
                break;
            default:
                break;
        }

        CompletePending(new CommandResponse(CommandStatus.Ack, PumpError.None, msg.Payload.ToArray()));
    }

    private void HandleEvent(OfpMessage msg)
    {
        // Duplicate suppression: DISPENSE_COMPLETE re-emits the same event SEQ until acked (§4.4).
        if (msg.Seq == _lastEventSeq && msg.Code == _lastEventCode)
        {
            return;
        }

        _lastEventSeq = msg.Seq;
        _lastEventCode = msg.Code;

        var ev = (PumpEvent)msg.Code;
        var volume = Volume.Zero;
        var value = Money.Zero(_cfg.CurrencyCode);
        var fault = FaultCode.None;
        bool violation = false;
        string? detail = null;

        switch (ev)
        {
            case PumpEvent.NozzleUp:
                violation = !ApplyFsm(DispenserFsm.Trigger.NozzleUp, "NOZZLE_UP");
                break;
            case PumpEvent.NozzleDown:
                violation = !ApplyFsm(DispenserFsm.Trigger.NozzleDown, "NOZZLE_DOWN");
                break;
            case PumpEvent.FlowUpdate:
                (volume, value) = ParseVolumeValue(msg.Payload.Span);
                violation = !ApplyFsm(DispenserFsm.Trigger.Flow, "FLOW_UPDATE");
                break;
            case PumpEvent.DispenseComplete:
                (volume, value) = ParseVolumeValue(msg.Payload.Span);
                // Auto-stop at preset fires LIMIT; a nozzle-down completion already moved the
                // mirror to Complete, so only advance when still Dispensing.
                if (State == DispenserState.Dispensing)
                {
                    violation = !ApplyFsm(DispenserFsm.Trigger.Limit, "DISPENSE_COMPLETE");
                }
                else if (State != DispenserState.Complete)
                {
                    violation = true;
                    detail = $"DISPENSE_COMPLETE in state {State}";
                }
                break;
            case PumpEvent.Fault:
                fault = msg.Payload.Length > 0 ? (FaultCode)msg.Payload.Span[0] : FaultCode.None;
                violation = !ApplyFsm(DispenserFsm.Trigger.Fault, "FAULT");
                break;
            default:
                violation = true;
                detail = $"unknown event 0x{msg.Code:X2}";
                break;
        }

        if (violation && detail is null)
        {
            detail = $"{ev} illegal in state {State}";
        }

        if (violation)
        {
            Log($"pump {Id}: PROTOCOL VIOLATION — {detail}");
        }

        Observed?.Invoke(this, new PumpNotification(Id, ev, State, volume, value, fault, violation, detail));
    }

    // Applies an event to the mirrored FSM. Returns false if the firmware reported an illegal
    // transition (a protocol violation); the mirror is not advanced in that case.
    private bool ApplyFsm(DispenserFsm.Trigger ev, string label)
    {
        var r = DispenserFsm.Next(State, ev);
        if (r.Outcome == DispenserFsm.Outcome.Illegal)
        {
            Log($"pump {Id}: {label} illegal in state {State}");
            return false;
        }

        if (r.Outcome == DispenserFsm.Outcome.Legal)
        {
            State = r.Next;
        }

        return true;
    }

    private (Volume, Money) ParseVolumeValue(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8)
        {
            return (Volume.Zero, Money.Zero(_cfg.CurrencyCode));
        }

        return (new Volume(BinaryPrimitives.ReadUInt32BigEndian(payload)),
            new Money(BinaryPrimitives.ReadUInt32BigEndian(payload[4..]), _cfg.CurrencyCode));
    }

    private void CompletePending(CommandResponse response)
    {
        var pending = _pending;
        _pending = null;
        pending?.Command.Tcs.TrySetResult(response);
    }

    private void OnTransportStateChanged(object? sender, PumpConnectionState state)
    {
        // On reconnect, resynchronise the mirror with an authoritative STATUS read (§8).
        if (state == PumpConnectionState.Connected && !_resyncQueued)
        {
            _resyncQueued = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    await StatusAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Shutting down.
                }
                finally
                {
                    _resyncQueued = false;
                }
            });
        }
    }

    private void FailAllOutstanding()
    {
        _pending?.Command.Tcs.TrySetResult(new CommandResponse(CommandStatus.Timeout, PumpError.None, default));
        _pending = null;
        while (_outbound.Reader.TryRead(out var cmd))
        {
            cmd.Tcs.TrySetResult(new CommandResponse(CommandStatus.Timeout, PumpError.None, default));
        }
    }

    private void Log(string message) => _log?.Invoke(message);

    private sealed record OutboundCommand(PumpCommand Cmd, byte[] Payload, TaskCompletionSource<CommandResponse> Tcs);

    private sealed class Pending(OutboundCommand command, byte seq, byte[] body, int retriesLeft, DateTimeOffset deadline)
    {
        public OutboundCommand Command { get; } = command;
        public byte Seq { get; } = seq;
        public byte[] Body { get; } = body;
        public int RetriesLeft { get; set; } = retriesLeft;
        public DateTimeOffset Deadline { get; set; } = deadline;
    }
}
