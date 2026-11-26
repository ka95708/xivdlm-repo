using System;
using System.Numerics;
using Dalamud.Plugin.Ipc;

namespace AetherKit.Game;

/// <summary>
/// vnavmesh 的 IPC。vnavmesh 沒裝或還沒載入時呼叫會丟例外，這裡一律吞掉、當成「不能用」。
/// </summary>
public sealed class Navmesh
{
    private readonly ICallGateSubscriber<bool> isReady;
    private readonly ICallGateSubscriber<bool> pathfindInProgress;
    private readonly ICallGateSubscriber<bool> isRunning;
    private readonly ICallGateSubscriber<Vector3, bool, float, bool> moveCloseTo;
    private readonly ICallGateSubscriber<object> stop;
    private readonly ICallGateSubscriber<object> cancelAll;
    private readonly ICallGateSubscriber<Vector3, bool, float, Vector3?> pointOnFloor;
    private readonly ICallGateSubscriber<Vector3, float, float, Vector3?> nearestPoint;

    public Navmesh()
    {
        var pi = Services.PluginInterface;
        isReady = pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        pathfindInProgress = pi.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        isRunning = pi.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        moveCloseTo = pi.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
        stop = pi.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        cancelAll = pi.GetIpcSubscriber<object>("vnavmesh.Nav.PathfindCancelAll");
        pointOnFloor = pi.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor");
        nearestPoint = pi.GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPoint");
    }

    /// <summary>p 附近（水平 halfExtent 米內）可以降落的地面點；找不到回傳 null。</summary>
    public Vector3? LandingPoint(Vector3 p, float halfExtent)
    {
        try
        {
            return pointOnFloor.InvokeFunc(p, false, halfExtent);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>p 附近（水平 halfExtentXZ、垂直 halfExtentY 米內）最近的導航網格點（不管能不能降落）；找不到回傳 null。</summary>
    public Vector3? NearestPoint(Vector3 p, float halfExtentXZ, float halfExtentY)
    {
        try
        {
            return nearestPoint.InvokeFunc(p, halfExtentXZ, halfExtentY);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>vnavmesh 有載入、這張地圖的導航網格也建好了。</summary>
    public bool Ready => Try(() => isReady.InvokeFunc());

    /// <summary>正在算路徑或沿著路徑走。</summary>
    public bool Busy => Try(() => pathfindInProgress.InvokeFunc() || isRunning.InvokeFunc());

    /// <summary>正沿著路徑走（不含還在算路徑的時候）。</summary>
    public bool Moving => Try(() => isRunning.InvokeFunc());

    /// <summary>走到離 dest 不超過 range 的地方就停。已經在算路徑時 vnavmesh 會拒絕，回傳 false。</summary>
    public bool MoveCloseTo(Vector3 dest, bool fly, float range) =>
        Try(() => moveCloseTo.InvokeFunc(dest, fly, Math.Max(0.5f, range)));

    public void Stop()
    {
        try
        {
            cancelAll.InvokeAction();
            stop.InvokeAction();
        }
        catch
        {
            // vnavmesh 不在就沒有東西要停
        }
    }

    private static bool Try(Func<bool> call)
    {
        try
        {
            return call();
        }
        catch
        {
            return false;
        }
    }
}
