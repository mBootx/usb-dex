namespace DexStream.App.Services;

/// <summary>Where a streaming session has got to.</summary>
public enum SessionState
{
    /// <summary>No device is plugged in.</summary>
    Disconnected,

    /// <summary>A device is present but streaming has not started.</summary>
    DeviceReady,

    /// <summary>Opening the USB interface and completing the ADB handshake.</summary>
    Connecting,

    /// <summary>Waiting for the user to allow USB debugging on the phone.</summary>
    AwaitingAuthorization,

    /// <summary>Pushing and starting the device agent.</summary>
    StartingAgent,

    /// <summary>Frames are flowing.</summary>
    Streaming,

    /// <summary>The session stopped because of an error.</summary>
    Failed,

    /// <summary>The session was stopped by the user.</summary>
    Stopped,
}

/// <summary>A status update from a session, for display.</summary>
/// <param name="State">The session's state.</param>
/// <param name="Message">A sentence describing what is happening or what went wrong.</param>
/// <param name="Detail">Optional extra context, such as the chosen display or a remedy.</param>
public readonly record struct SessionStatus(SessionState State, string Message, string? Detail = null)
{
    public bool IsError => State == SessionState.Failed;

    /// <summary>True while the session is doing setup work the user should wait for.</summary>
    public bool IsBusy => State is SessionState.Connecting
        or SessionState.AwaitingAuthorization
        or SessionState.StartingAgent;
}
