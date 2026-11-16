using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Quality;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Platform;

namespace SeedRestart;

// Used only as RunManager.NetService during its synchronous CleanUp call.
// RunLobby and every disposed synchronizer still reference the real service, so their
// normal handlers are removed. The single disconnect at the end of CleanUp is suppressed.
internal sealed class PreservedConnection(INetGameService inner) : INetGameService
{
    public ulong NetId => inner.NetId;
    public bool IsConnected => inner.IsConnected;
    public bool IsGameLoading => inner.IsGameLoading;
    public NetGameType Type => inner.Type;
    public PlatformType Platform => inner.Platform;
    public PeerVersionInfo LocalVersion => inner.LocalVersion;
    public event Action<NetErrorInfo>? Disconnected { add => inner.Disconnected += value; remove => inner.Disconnected -= value; }
    public void SendMessage<T>(T message, ulong playerId) where T : INetMessage => inner.SendMessage(message, playerId);
    public void SendMessage<T>(T message) where T : INetMessage => inner.SendMessage(message);
    public void RegisterMessageHandler<T>(MessageHandlerDelegate<T> handler) where T : INetMessage => inner.RegisterMessageHandler(handler);
    public void UnregisterMessageHandler<T>(MessageHandlerDelegate<T> handler) where T : INetMessage => inner.UnregisterMessageHandler(handler);
    public void Update() => inner.Update();
    public void Disconnect(NetError reason, bool now = false) { }
    public ConnectionStats? GetStatsForPeer(ulong peerId) => inner.GetStatsForPeer(peerId);
    public void SetGameLoading(bool loading) => inner.SetGameLoading(loading);
    public void SetBufferMessages(bool buffer) => inner.SetBufferMessages(buffer);
    public string? GetRawLobbyIdentifier() => inner.GetRawLobbyIdentifier();
}
