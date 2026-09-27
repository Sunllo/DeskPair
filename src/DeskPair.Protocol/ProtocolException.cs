namespace DeskPair.Protocol;

/// <summary>The peer violated the wire protocol; the connection must be closed.</summary>
public class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message)
    {
    }

    public ProtocolException(string message, Exception inner) : base(message, inner)
    {
    }
}

public class HandshakeException : ProtocolException
{
    public HandshakeException(string message) : base(message)
    {
    }

    public HandshakeException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
/// The two sides share no protocol version. The host catches this rather than the plain exception
/// because it still owes the controller an answer: <see cref="Refusal"/> is the hello that says which
/// side has to update, and the host sends it before closing.
/// </summary>
public sealed class HandshakeVersionException : HandshakeException
{
    public HandshakeVersionException(string message, Messages.HostHello refusal) : base(message)
    {
        Refusal = refusal;
    }

    public Messages.HostHello Refusal { get; }
}
