namespace OpenForecourt.PumpManager;

/// <summary>
/// A mirror of the firmware dispenser FSM (<c>firmware/pump/src/fsm.c</c>, <c>docs §6.1</c>),
/// held on the manager side so that a state transition the firmware reports which the table
/// forbids is caught as a <b>protocol violation</b> rather than silently trusted. Same table,
/// two languages: the exhaustive test asserts they agree cell for cell.
/// </summary>
public static class DispenserFsm
{
    /// <summary>The events that drive the FSM (order matches the firmware enum).</summary>
    public enum Trigger
    {
        /// <summary>AUTHORISE received / acked.</summary>
        Authorise = 0,

        /// <summary>CANCEL_AUTH received / acked.</summary>
        Cancel = 1,

        /// <summary>SUSPEND received / acked.</summary>
        Suspend = 2,

        /// <summary>RESUME received / acked.</summary>
        Resume = 3,

        /// <summary>Nozzle lifted.</summary>
        NozzleUp = 4,

        /// <summary>Nozzle holstered.</summary>
        NozzleDown = 5,

        /// <summary>Flow pulse batch.</summary>
        Flow = 6,

        /// <summary>Preset limit reached.</summary>
        Limit = 7,

        /// <summary>Fault detector fired.</summary>
        Fault = 8,
    }

    /// <summary>Whether an event is permitted in a state.</summary>
    public enum Outcome
    {
        /// <summary>Must not occur; on the manager side this is a protocol violation.</summary>
        Illegal,

        /// <summary>Transitions to the next state.</summary>
        Legal,

        /// <summary>Accepted, no state change.</summary>
        Ignored,
    }

    /// <summary>The result of a table lookup.</summary>
    /// <param name="Outcome">Legal, illegal or ignored.</param>
    /// <param name="Next">The next state; meaningful only when <see cref="Outcome.Legal"/>.</param>
    public readonly record struct Result(Outcome Outcome, DispenserState Next);

    private const int EventCount = 9;

    // Rows: Idle, Authorised, Dispensing, Complete, Suspended, Fault.
    // Cols: Authorise, Cancel, Suspend, Resume, NozzleUp, NozzleDown, Flow, Limit, Fault.
    private static readonly Result[,] Table = BuildTable();

    /// <summary>Looks up one cell of the transition table. Out-of-range inputs are illegal.</summary>
    public static Result Next(DispenserState state, Trigger ev)
    {
        int s = (int)state;
        int e = (int)ev;
        if (s < 0 || s > (int)DispenserState.Fault || e < 0 || e >= EventCount)
        {
            return new Result(Outcome.Illegal, state);
        }

        return Table[s, e];
    }

    private static Result[,] BuildTable()
    {
        Result Ill() => new(Outcome.Illegal, DispenserState.Idle);
        Result Ign() => new(Outcome.Ignored, DispenserState.Idle);
        Result Go(DispenserState next) => new(Outcome.Legal, next);

        var t = new Result[6, EventCount];
        void Row(DispenserState from, Result a, Result c, Result su, Result re, Result nu,
                 Result nd, Result fl, Result li, Result fa)
        {
            int s = (int)from;
            t[s, 0] = a; t[s, 1] = c; t[s, 2] = su; t[s, 3] = re; t[s, 4] = nu;
            t[s, 5] = nd; t[s, 6] = fl; t[s, 7] = li; t[s, 8] = fa;
        }

        // IDLE
        Row(DispenserState.Idle, Go(DispenserState.Authorised), Go(DispenserState.Idle), Ill(), Ill(),
            Go(DispenserState.Fault), Ill(), Go(DispenserState.Fault), Ill(), Go(DispenserState.Fault));
        // AUTHORISED
        Row(DispenserState.Authorised, Ill(), Go(DispenserState.Idle), Ill(), Ill(),
            Go(DispenserState.Dispensing), Ill(), Ill(), Ill(), Go(DispenserState.Fault));
        // DISPENSING
        Row(DispenserState.Dispensing, Ill(), Ill(), Go(DispenserState.Suspended), Ill(),
            Ill(), Go(DispenserState.Complete), Go(DispenserState.Dispensing), Go(DispenserState.Complete),
            Go(DispenserState.Fault));
        // COMPLETE
        Row(DispenserState.Complete, Go(DispenserState.Authorised), Go(DispenserState.Idle), Ill(), Ill(),
            Ill(), Ill(), Ill(), Ill(), Go(DispenserState.Fault));
        // SUSPENDED
        Row(DispenserState.Suspended, Ill(), Go(DispenserState.Idle), Ill(), Go(DispenserState.Dispensing),
            Ill(), Go(DispenserState.Complete), Ign(), Ill(), Go(DispenserState.Fault));
        // FAULT
        Row(DispenserState.Fault, Ill(), Go(DispenserState.Idle), Ill(), Ill(),
            Ill(), Ill(), Ign(), Ill(), Ign());

        return t;
    }
}
