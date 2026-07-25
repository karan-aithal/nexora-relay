namespace OpenForecourt.SiteController.Orchestration;

/// <summary>
/// Tracks whether the acquiring host is currently reachable, so authorisation can switch to
/// offline mode the moment the link is lost and switch back when it returns.
/// </summary>
/// <remarks>
/// The flag is moved by two things: the orchestrator marks it down on a no-response, and the
/// availability prober marks it up again once a health ping succeeds. <see cref="CameOnline"/>
/// fires on the down→up edge, which is what triggers offline replay.
/// </remarks>
public sealed class HostAvailability
{
    private readonly Lock _gate = new();
    private bool _isAvailable = true;

    /// <summary>Raised on the transition from unavailable to available.</summary>
    public event EventHandler? CameOnline;

    /// <summary>True while the host is believed reachable.</summary>
    public bool IsAvailable
    {
        get { lock (_gate) { return _isAvailable; } }
    }

    /// <summary>Records that the host is reachable; raises <see cref="CameOnline"/> on the rising edge.</summary>
    public void MarkAvailable()
    {
        bool rising;
        lock (_gate)
        {
            rising = !_isAvailable;
            _isAvailable = true;
        }

        if (rising)
        {
            CameOnline?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Records that the host is unreachable.</summary>
    public void MarkUnavailable()
    {
        lock (_gate)
        {
            _isAvailable = false;
        }
    }
}
