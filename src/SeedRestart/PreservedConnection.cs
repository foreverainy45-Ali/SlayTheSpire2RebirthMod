using System.Reflection;
using System.Runtime.ExceptionServices;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace SeedRestart;

// Used only as RunManager.NetService during its synchronous CleanUp call.
// RunLobby and every disposed synchronizer still reference the real service, so their
// normal handlers are removed. The single disconnect at the end of CleanUp is suppressed.
internal static class PreservedConnection
{
    public static INetGameService Create(INetGameService inner)
    {
        var service = DispatchProxy.Create<INetGameService, ForwardingProxy>();
        ((ForwardingProxy)(object)service).Inner = inner;
        return service;
    }

    private class ForwardingProxy : DispatchProxy
    {
        internal INetGameService Inner { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod == null) throw new MissingMethodException("INetGameService method is missing");
            if (targetMethod.Name == nameof(INetGameService.Disconnect)) return null;
            try { return targetMethod.Invoke(Inner, args); }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }
    }
}
