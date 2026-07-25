using OpenForecourt.PumpManager;
using Xunit;

namespace OpenForecourt.PumpManager.UnitTests;

/// <summary>
/// The C# mirrored FSM checked against an INDEPENDENT expected table restated from
/// <c>docs/protocol-pump.md §6.1</c> — the same table the C test
/// (<c>firmware/pump/test/test_fsm.c</c>) checks. If the two languages' tables ever diverge,
/// one of these three (C table, C# table, this restatement) disagrees and the test fails.
/// </summary>
public class DispenserFsmParityTests
{
    private enum E { Illegal, Legal, Ignored }

    private static (E outcome, DispenserState next)[,] Expected()
    {
        (E, DispenserState) L(DispenserState s) => (E.Legal, s);
        (E, DispenserState) Ill() => (E.Illegal, DispenserState.Idle);
        (E, DispenserState) Ign() => (E.Ignored, DispenserState.Idle);

        // Cols: Authorise, Cancel, Suspend, Resume, NozzleUp, NozzleDown, Flow, Limit, Fault.
        return new (E, DispenserState)[6, 9]
        {
            { L(DispenserState.Authorised), L(DispenserState.Idle), Ill(), Ill(), L(DispenserState.Fault), Ill(), L(DispenserState.Fault), Ill(), L(DispenserState.Fault) },
            { Ill(), L(DispenserState.Idle), Ill(), Ill(), L(DispenserState.Dispensing), Ill(), Ill(), Ill(), L(DispenserState.Fault) },
            { Ill(), Ill(), L(DispenserState.Suspended), Ill(), Ill(), L(DispenserState.Complete), L(DispenserState.Dispensing), L(DispenserState.Complete), L(DispenserState.Fault) },
            { L(DispenserState.Authorised), L(DispenserState.Idle), Ill(), Ill(), Ill(), Ill(), Ill(), Ill(), L(DispenserState.Fault) },
            { Ill(), L(DispenserState.Idle), Ill(), L(DispenserState.Dispensing), Ill(), L(DispenserState.Complete), Ign(), Ill(), L(DispenserState.Fault) },
            { Ill(), L(DispenserState.Idle), Ill(), Ill(), Ill(), Ill(), Ign(), Ill(), Ign() },
        };
    }

    [Fact]
    public void Every_cell_matches_the_documented_table()
    {
        var expected = Expected();
        for (int s = 0; s <= (int)DispenserState.Fault; s++)
        {
            for (int e = 0; e < 9; e++)
            {
                var r = DispenserFsm.Next((DispenserState)s, (DispenserFsm.Trigger)e);
                var (outcome, next) = expected[s, e];

                Assert.Equal(outcome switch
                {
                    E.Legal => DispenserFsm.Outcome.Legal,
                    E.Ignored => DispenserFsm.Outcome.Ignored,
                    _ => DispenserFsm.Outcome.Illegal,
                }, r.Outcome);

                if (outcome == E.Legal)
                {
                    Assert.Equal(next, r.Next);
                }
            }
        }
    }

    [Fact]
    public void Out_of_range_inputs_are_illegal()
    {
        Assert.Equal(DispenserFsm.Outcome.Illegal, DispenserFsm.Next((DispenserState)99, DispenserFsm.Trigger.Cancel).Outcome);
    }
}
