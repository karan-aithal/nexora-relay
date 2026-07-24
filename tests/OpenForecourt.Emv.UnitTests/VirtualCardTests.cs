using System.Text;
using OpenForecourt.Emv.BerTlv;
using OpenForecourt.VirtualCard;
using Xunit;

namespace OpenForecourt.Emv.UnitTests;

public sealed class VirtualCardTests
{
    private static VirtualCard.VirtualCard Load(string file) =>
        new(CardProfile.Load(Path.Combine(AppContext.BaseDirectory, "cards", file)));

    private static byte[] SelectName(string name)
    {
        byte[] df = Encoding.ASCII.GetBytes(name);
        return [0x00, 0xA4, 0x04, 0x00, (byte)df.Length, .. df, 0x00];
    }

    private static byte[] SelectAid(string aidHex)
    {
        byte[] aid = Convert.FromHexString(aidHex);
        return [0x00, 0xA4, 0x04, 0x00, (byte)aid.Length, .. aid, 0x00];
    }

    private static ushort Sw(byte[] r) => (ushort)((r[^2] << 8) | r[^1]);

    [Fact]
    public void Select_ppse_returns_fci_listing_the_application()
    {
        var card = Load("contactless-mc.json");
        byte[] r = card.Process(SelectName(VirtualCard.VirtualCard.PpseName));

        Assert.Equal(0x9000, Sw(r));
        var fci = BerTlvCodec.TryParse(r.AsSpan(0, r.Length - 2)).Value.Single();
        Assert.Equal("A0000000041010", Convert.ToHexString(fci.Find(Tag.Parse("4F"))!.Value.Span));
    }

    [Fact]
    public void Select_unknown_aid_returns_6A82()
    {
        var card = Load("contactless-mc.json");
        Assert.Equal(0x6A82, Sw(card.Process(SelectAid("A0000000999999"))));
    }

    [Fact]
    public void Select_with_wrong_p1_returns_6A86()
    {
        var card = Load("contactless-mc.json");
        byte[] apdu = [0x00, 0xA4, 0x00, 0x00, 0x02, 0x3F, 0x00, 0x00];
        Assert.Equal(0x6A86, Sw(card.Process(apdu)));
    }

    [Fact]
    public void Gpo_returns_aip_and_afl_after_selection()
    {
        var card = Load("contactless-mc.json");
        card.Process(SelectAid("A0000000041010"));
        byte[] gpo = [0x80, 0xA8, 0x00, 0x00, 0x02, 0x83, 0x00, 0x00];

        byte[] r = card.Process(gpo);
        Assert.Equal(0x9000, Sw(r));
        var t = BerTlvCodec.TryParse(r.AsSpan(0, r.Length - 2)).Value.Single();
        Assert.NotNull(t.Find(Tag.Parse("82"))); // AIP
        Assert.NotNull(t.Find(Tag.Parse("94"))); // AFL
    }

    [Fact]
    public void Generate_ac_before_gpo_returns_6985()
    {
        var card = Load("contactless-mc.json");
        card.Process(SelectAid("A0000000041010"));
        byte[] genAc = [0x80, 0xAE, 0x80, 0x00, 0x01, 0x00, 0x00];
        Assert.Equal(0x6985, Sw(card.Process(genAc)));
    }

    [Fact]
    public void Read_record_out_of_range_returns_6A83()
    {
        var card = Load("contactless-mc.json");
        card.Process(SelectAid("A0000000041010"));
        byte[] read = [0x00, 0xB2, 0x09, (0x01 << 3) | 0x04, 0x00]; // SFI 1, record 9
        Assert.Equal(0x6A83, Sw(card.Process(read)));
    }

    [Fact]
    public void Get_data_atc_returns_counter_unknown_tag_returns_6A88()
    {
        var card = Load("contactless-mc.json");
        card.Process(SelectAid("A0000000041010"));

        byte[] atc = card.Process([0x80, 0xCA, 0x9F, 0x36, 0x00]);
        Assert.Equal(0x9000, Sw(atc));
        Assert.Equal("9F36", BerTlvCodec.TryParse(atc.AsSpan(0, atc.Length - 2)).Value.Single().Tag.ToString());

        Assert.Equal(0x6A88, Sw(card.Process([0x80, 0xCA, 0x9F, 0x99, 0x00])));
    }

    [Fact]
    public void Malformed_apdu_returns_6700()
    {
        var card = Load("contactless-mc.json");
        Assert.Equal(0x6700, Sw(card.Process([0x00, 0xA4]))); // too short for a header
    }

    [Fact]
    public void Atc_increments_at_gpo()
    {
        var card = Load("contactless-mc.json");
        card.Process(SelectAid("A0000000041010"));
        byte[] before = card.Process([0x80, 0xCA, 0x9F, 0x36, 0x00]);
        ushort atcBefore = ReadAtc(before);

        card.Process([0x80, 0xA8, 0x00, 0x00, 0x02, 0x83, 0x00, 0x00]);
        byte[] after = card.Process([0x80, 0xCA, 0x9F, 0x36, 0x00]);
        ushort atcAfter = ReadAtc(after);

        Assert.Equal(atcBefore + 1, atcAfter);
    }

    private static ushort ReadAtc(byte[] response)
    {
        var atc = BerTlvCodec.TryParse(response.AsSpan(0, response.Length - 2)).Value.Single().Value.Span;
        return (ushort)((atc[0] << 8) | atc[1]);
    }
}
