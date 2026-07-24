namespace OpenForecourt.Iso8583;

/// <summary>
/// Names for the OFC-87 field numbers, so application code says <c>Fields.Stan</c> rather
/// than <c>11</c>. The numbers themselves are defined by the dialect table, not here.
/// </summary>
public static class Fields
{
    /// <summary>Field 2 — primary account number.</summary>
    public const int Pan = 2;

    /// <summary>Field 3 — processing code.</summary>
    public const int ProcessingCode = 3;

    /// <summary>Field 4 — transaction amount, minor units.</summary>
    public const int Amount = 4;

    /// <summary>Field 7 — transmission date and time, <c>MMDDhhmmss</c>.</summary>
    public const int TransmissionDateTime = 7;

    /// <summary>Field 11 — system trace audit number.</summary>
    public const int Stan = 11;

    /// <summary>Field 12 — local transaction time, <c>hhmmss</c>.</summary>
    public const int LocalTime = 12;

    /// <summary>Field 13 — local transaction date, <c>MMDD</c>.</summary>
    public const int LocalDate = 13;

    /// <summary>Field 22 — point of service entry mode.</summary>
    public const int PosEntryMode = 22;

    /// <summary>Field 37 — retrieval reference number.</summary>
    public const int RetrievalReferenceNumber = 37;

    /// <summary>Field 38 — authorisation identification response (the auth code).</summary>
    public const int AuthorisationCode = 38;

    /// <summary>Field 39 — response code.</summary>
    public const int ResponseCode = 39;

    /// <summary>Field 41 — card acceptor terminal identification.</summary>
    public const int TerminalId = 41;

    /// <summary>Field 42 — card acceptor identification code.</summary>
    public const int MerchantId = 42;

    /// <summary>Field 49 — transaction currency code, ISO 4217 numeric.</summary>
    public const int Currency = 49;

    /// <summary>Field 55 — integrated circuit card data (EMV BER-TLV).</summary>
    public const int IccData = 55;

    /// <summary>Field 70 — network management information code.</summary>
    public const int NetworkManagementCode = 70;

    /// <summary>Field 90 — original data elements, used by reversals.</summary>
    public const int OriginalDataElements = 90;
}
