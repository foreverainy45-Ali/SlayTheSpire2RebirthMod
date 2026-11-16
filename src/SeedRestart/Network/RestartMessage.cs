using System.Text.Json;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

namespace SeedRestart.Network;

public enum MessageKind { Request, Ballot, Vote, Prepare, Ready, Commit, SetupReady, Start, Loaded, Complete, Cancel, Fail, RequestRejected }

public sealed record Envelope
{
    public int Protocol { get; init; } = 1;
    public MessageKind Kind { get; init; }
    public string RequestId { get; init; } = "";
    public string RunToken { get; init; } = "";
    public string Seed { get; init; } = "";
    public ulong Initiator { get; init; }
    public ulong[] Members { get; init; } = [];
    public ulong[] Votes { get; init; } = [];
    public bool Approve { get; init; }
    public string Detail { get; init; } = "";
    public string[] Acts { get; init; } = [];
}

// A unique concrete type lets the game's mod loader register the protocol normally.
// Never broadcast client messages automatically; the host validates and relays state.
public struct RestartMessage : INetMessage
{
    public string Json;
    public readonly bool ShouldBroadcast => false;
    public readonly NetTransferMode Mode => NetTransferMode.Reliable;
    public readonly LogLevel LogLevel => LogLevel.VeryDebug;
    public readonly bool ShouldBuffer => false;
    public void Serialize(PacketWriter writer) => writer.WriteString(Json);
    public void Deserialize(PacketReader reader) => Json = reader.ReadString();
    public static RestartMessage From(Envelope envelope) => new() { Json = JsonSerializer.Serialize(envelope) };
    public readonly Envelope Parse()
    {
        if (Json is null || Json.Length > 16000) throw new InvalidDataException("Invalid restart packet size");
        var value = JsonSerializer.Deserialize<Envelope>(Json) ?? throw new InvalidDataException("Empty packet");
        if (value.Protocol != 1 || value.Members.Length > 4 || value.Votes.Length > 4 ||
            value.RequestId.Length > 64 || value.RunToken.Length > 128 || value.Detail.Length > 1000 ||
            value.Seed.Length > 32 || value.Acts.Length > 8)
            throw new InvalidDataException("Incompatible or invalid restart packet");
        return value;
    }
}
