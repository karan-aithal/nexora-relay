using System.Text;
using OpenForecourt.Adapters.InProc;
using OpenForecourt.Emv.Terminal;
using OpenForecourt.VirtualCard;
using Xunit;

namespace OpenForecourt.Emv.UnitTests;

/// <summary>
/// Pins the exact command/response APDU sequence of a full in-process transaction. The
/// unpredictable number, clock and amount are fixed, and the card's cryptogram is canned,
/// so the whole trace is deterministic. Run with <c>UPDATE_GOLDEN=1</c> to regenerate the
/// golden file after an intentional protocol change.
/// </summary>
public sealed class ApduTraceGoldenTest
{
    [Fact]
    public async Task Contactless_transaction_matches_golden_apdu_trace()
    {
        var reader = new InProcCardReader(new VirtualCard.VirtualCard(
            CardProfile.Load(Path.Combine(AppContext.BaseDirectory, "cards", "contactless-mc.json"))));
        var terminal = new EmvTerminal(
            reader,
            new TerminalConfig(),
            new FakeClock(new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero)),
            unpredictableNumber: [0xDE, 0xAD, 0xBE, 0xEF]);

        var result = await terminal.RunAsync(amountMinor: 2500, CancellationToken.None);
        Assert.True(result.IsSuccess, result.IsError ? result.Error.ToString() : "");

        var sb = new StringBuilder();
        foreach (var x in terminal.Trace)
        {
            sb.Append("C: ").AppendLine(Convert.ToHexString(x.Command));
            sb.Append("R: ").AppendLine(Convert.ToHexString(x.Response));
        }

        string actual = sb.ToString().ReplaceLineEndings("\n");
        string goldenPath = Path.Combine(AppContext.BaseDirectory, "Golden", "contactless-trace.txt");

        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1" || !File.Exists(goldenPath))
        {
            // Write into the source tree, not just the build output, so it can be committed.
            string srcPath = Path.Combine(FindProjectDir(), "Golden", "contactless-trace.txt");
            File.WriteAllText(srcPath, actual);
            File.WriteAllText(goldenPath, actual);
        }

        string expected = File.ReadAllText(goldenPath).ReplaceLineEndings("\n");
        Assert.Equal(expected, actual);
    }

    private static string FindProjectDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OpenForecourt.Emv.UnitTests.csproj")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("test project directory not found.");
    }
}
