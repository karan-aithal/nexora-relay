using System.Runtime.InteropServices;

namespace OpenForecourt.Adapters.Pcsc;

/// <summary>
/// Direct P/Invoke declarations for the Windows smart-card API (<c>winscard.dll</c>).
/// </summary>
/// <remarks>
/// These are the genuine production entry points — no wrapper library (phase-2 spec). The
/// ANSI (<c>*A</c>) variants are used throughout because reader names and the multi-string
/// group/reader lists are ASCII. Return values are the <c>SCARD_*</c> status longs, checked
/// by the caller against <see cref="SCARD_S_SUCCESS"/>.
/// </remarks>
internal static class WinScard
{
    public const int SCARD_S_SUCCESS = 0;
    public const int SCARD_E_NO_READERS_AVAILABLE = unchecked((int)0x8010002E);
    public const int SCARD_E_TIMEOUT = unchecked((int)0x8010000A);
    public const int SCARD_E_CANCELLED = unchecked((int)0x80100002);

    public const int SCARD_SCOPE_SYSTEM = 2;

    public const int SCARD_SHARE_SHARED = 2;

    public const int SCARD_PROTOCOL_T0 = 1;
    public const int SCARD_PROTOCOL_T1 = 2;

    public const int SCARD_LEAVE_CARD = 0;

    public const int SCARD_STATE_UNAWARE = 0x0000;
    public const int SCARD_STATE_CHANGED = 0x0002;
    public const int SCARD_STATE_PRESENT = 0x0020;

    /// <summary>The auto-allocate sentinel length for the double-call reader-list pattern.</summary>
    public const int SCARD_AUTOALLOCATE = -1;

    [StructLayout(LayoutKind.Sequential)]
    public struct SCARD_IO_REQUEST
    {
        public int dwProtocol;
        public int cbPciLength;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct SCARD_READERSTATE
    {
        [MarshalAs(UnmanagedType.LPStr)]
        public string szReader;
        public nint pvUserData;
        public int dwCurrentState;
        public int dwEventState;
        public int cbAtr;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 36)]
        public byte[] rgbAtr;
    }

    [DllImport("winscard.dll", SetLastError = false)]
    public static extern int SCardEstablishContext(
        int dwScope, nint pvReserved1, nint pvReserved2, out nint phContext);

    [DllImport("winscard.dll", SetLastError = false)]
    public static extern int SCardReleaseContext(nint hContext);

    [DllImport("winscard.dll", CharSet = CharSet.Ansi, EntryPoint = "SCardListReadersA",
        BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = false)]
    public static extern int SCardListReaders(
        nint hContext, string? mszGroups, byte[]? mszReaders, ref int pcchReaders);

    [DllImport("winscard.dll", CharSet = CharSet.Ansi, EntryPoint = "SCardConnectA",
        BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = false)]
    public static extern int SCardConnect(
        nint hContext, string szReader, int dwShareMode, int dwPreferredProtocols,
        out nint phCard, out int pdwActiveProtocol);

    [DllImport("winscard.dll", SetLastError = false)]
    public static extern int SCardTransmit(
        nint hCard, in SCARD_IO_REQUEST pioSendPci, byte[] pbSendBuffer, int cbSendLength,
        nint pioRecvPci, byte[] pbRecvBuffer, ref int pcbRecvLength);

    [DllImport("winscard.dll", CharSet = CharSet.Ansi, EntryPoint = "SCardGetStatusChangeA",
        BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = false)]
    public static extern int SCardGetStatusChange(
        nint hContext, int dwTimeout, [In, Out] SCARD_READERSTATE[] rgReaderStates, int cReaders);

    [DllImport("winscard.dll", SetLastError = false)]
    public static extern int SCardCancel(nint hContext);

    [DllImport("winscard.dll", SetLastError = false)]
    public static extern int SCardDisconnect(nint hCard, int dwDisposition);
}
