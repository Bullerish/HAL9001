namespace HAL9001;

/// <summary>
/// What kind of thing a peer message is. Every message on the wire is tagged with one of
/// these (a single byte, see <see cref="PeerNode"/>'s framing) so the receiver can treat a
/// peer's QUESTION differently from an ordinary chat line.
///
/// Sub-step A uses <see cref="Question"/> only to LABEL the message on screen.
/// Sub-step B will use it to route an incoming question into the agent loop.
/// </summary>
public enum PeerMessageKind : byte
{
    Chat = 0,      // plain text — what the Step 2 chat demo sends
    Question = 1,  // a follow-up question produced by the peer's agent
    Answer = 2,    // an answer to a question the peer sent us (sub-step B)
    Hello = 3,     // swarm handshake: a dialer announcing its listen identity (rung 1)
}

/// <summary>One message received from a peer: its kind plus its text.</summary>
public sealed record PeerMessage(PeerMessageKind Kind, string Text);
