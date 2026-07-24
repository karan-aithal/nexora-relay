using System.Globalization;
using OpenForecourt.Adapters.InProc;
using OpenForecourt.Emv.BerTlv;
using OpenForecourt.Emv.Terminal;
using OpenForecourt.VirtualCard;

// Phase 2 demo entry point: run a full EMV online-authorisation flow against the virtual
// card, in-process, and print the complete APDU trace decoded by tag name plus the
// assembled ISO 8583 field 55. No hardware, no sockets — the same ICardReader the physical
// PC/SC reader implements, here backed by InProcCardReader.

string cardsDir = ArgValue("--cards") ?? DefaultCardsDir();
long amount = long.Parse(ArgValue("--amount") ?? "2500", CultureInfo.InvariantCulture);
string[] cards = ArgValue("--card") is { } one
    ? [one]
    : ["contact-visa-credit.json", "contactless-mc.json", "decline-card.json"];

foreach (string card in cards)
{
    await RunAsync(Path.Combine(cardsDir, card), amount);
}

return 0;

async Task RunAsync(string profilePath, long amountMinor)
{
    var profile = CardProfile.Load(profilePath);
    var reader = new InProcCardReader(new VirtualCard(profile));
    var terminal = new EmvTerminal(reader, new TerminalConfig(), SystemClock.Instance);

    Console.WriteLine(new string('=', 78));
    Console.WriteLine($" Card: {profile.Name}   ({profile.Kind})");
    Console.WriteLine($" Amount: {amountMinor} minor units");
    Console.WriteLine(new string('=', 78));

    var result = await terminal.RunAsync(amountMinor, CancellationToken.None);

    Console.WriteLine();
    Console.WriteLine("APDU trace (command -> response, response TLV decoded by name).");
    Console.WriteLine("Raw hex is the literal card<->terminal exchange INSIDE the OPT / P2PE");
    Console.WriteLine("boundary; test PANs only. The decoded view masks the PAN (first 6 + last 4).");
    Console.WriteLine();
    int step = 1;
    foreach (var x in terminal.Trace)
    {
        Console.WriteLine($"[{step,2}] {Label(x.Command)}");
        Console.WriteLine($"     C: {Convert.ToHexString(x.Command)}");
        Console.WriteLine($"     R: {Convert.ToHexString(x.Response)}  (SW {x.StatusWord:X4})");
        PrintTlv(x.Response.AsSpan(0, Math.Max(0, x.Response.Length - 2)), "        ");
        step++;
    }

    Console.WriteLine();
    if (result.IsError)
    {
        Console.WriteLine($"Flow stopped: {result.Error}");
        Console.WriteLine();
        return;
    }

    var o = result.Value;
    Console.WriteLine($"Selected AID : {o.SelectedAid}");
    Console.WriteLine($"PAN (masked) : {o.Pan}");
    Console.WriteLine($"CVM          : {o.Cvm}");
    Console.WriteLine($"TVR          : {Convert.ToHexString(o.Tvr)}");
    Console.WriteLine($"Requested    : {o.Requested}");
    Console.WriteLine($"Decision     : {o.Decision}");
    Console.WriteLine();
    Console.WriteLine($"ISO 8583 field 55 ({o.Field55.Length} bytes): {Convert.ToHexString(o.Field55)}");
    PrintTlv(o.Field55, "  ");
    Console.WriteLine();
}

static void PrintTlv(ReadOnlySpan<byte> data, string indent)
{
    if (data.IsEmpty)
    {
        return;
    }

    var parsed = BerTlvCodec.TryParse(data);
    if (parsed.IsError)
    {
        Console.WriteLine($"{indent}(not TLV: {parsed.Error})");
        return;
    }

    foreach (string line in EmvTags.Describe(parsed.Value).TrimEnd('\n').Split('\n'))
    {
        Console.WriteLine($"{indent}{line}");
    }
}

static string Label(byte[] command)
{
    if (command.Length < 2)
    {
        return "(short APDU)";
    }

    return command[1] switch
    {
        0xA4 => "SELECT",
        0xA8 => "GET PROCESSING OPTIONS",
        0xB2 => "READ RECORD",
        0xCA => "GET DATA",
        0xAE => "GENERATE AC",
        _ => $"INS {command[1]:X2}",
    };
}

static string? ArgValue(string name)
{
    var argv = Environment.GetCommandLineArgs();
    int i = Array.IndexOf(argv, name);
    return i >= 0 && i + 1 < argv.Length ? argv[i + 1] : null;
}

static string DefaultCardsDir()
{
    // Walk up to the repo root and use tests/testdata/cards.
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "testdata", "cards")))
    {
        dir = dir.Parent;
    }

    return dir is not null
        ? Path.Combine(dir.FullName, "tests", "testdata", "cards")
        : Path.Combine(AppContext.BaseDirectory, "cards");
}
