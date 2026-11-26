using System.Buffers;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace School.Training;

/// <summary>
/// One live share per practical-work id. JPEG frames stay in memory only while a student
/// socket is attached. Encoding on the student machine starts when a teacher subscribes
/// and stops when the last teacher leaves; the handshake is the only database check.
/// </summary>
public sealed class ClassroomLiveShare : IDisposable
{
    public const int MaxFrameLength = 160_000;
    const int RoomLimit = 256;
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    static readonly byte[] StartMessage = """{"type":"start"}"""u8.ToArray();
    static readonly byte[] StopMessage = """{"type":"stop"}"""u8.ToArray();
    readonly Dictionary<string, Room> rooms = new(StringComparer.Ordinal);
    readonly object gate = new();

    public sealed record State(bool AllowAssist);

    public State? Read(string workId)
    {
        lock (gate)
            return rooms.TryGetValue(workId, out var room) && room.Publisher != null
                ? new State(room.AllowAssist)
                : null;
    }

    public async Task<bool> PublishAsync(string workId, WebSocket socket, bool allowAssist)
    {
        List<Viewer> watchers;
        bool start;
        lock (gate)
        {
            if (!rooms.TryGetValue(workId, out var room))
            {
                if (rooms.Count >= RoomLimit) return false;
                room = new Room();
                rooms[workId] = room;
            }
            if (room.Publisher != null) return false;
            room.Publisher = socket;
            room.PublisherSend = new SemaphoreSlim(1, 1);
            room.AllowAssist = allowAssist;
            watchers = room.Viewers.ToList();
            start = watchers.Count > 0;
        }
        if (start) await SendLocked(PublisherSend(workId, socket), socket, StartMessage, WebSocketMessageType.Text);
        var status = Status(sharing: true, allowAssist);
        foreach (var watcher in watchers)
            await SendLocked(watcher.Send, watcher.Socket, status, WebSocketMessageType.Text);
        return true;
    }

    public async Task UnpublishAsync(string workId, WebSocket socket)
    {
        List<Viewer> watchers;
        lock (gate)
        {
            if (!rooms.TryGetValue(workId, out var room) || room.Publisher != socket) return;
            room.Publisher = null;
            room.PublisherSend = null;
            room.AllowAssist = false;
            room.Latest = null;
            watchers = room.Viewers.ToList();
            if (watchers.Count == 0) rooms.Remove(workId);
        }
        var status = Status(sharing: false, allowAssist: false);
        foreach (var watcher in watchers)
            await SendLocked(watcher.Send, watcher.Socket, status, WebSocketMessageType.Text);
    }

    public async Task<bool> WatchAsync(string workId, WebSocket socket)
    {
        Viewer watcher;
        bool sharing;
        bool allowAssist;
        byte[]? latest;
        WebSocket? publisher;
        SemaphoreSlim? publisherSend;
        lock (gate)
        {
            if (!rooms.TryGetValue(workId, out var room))
            {
                if (rooms.Count >= RoomLimit) return false;
                room = new Room();
                rooms[workId] = room;
            }
            watcher = new Viewer(socket, new SemaphoreSlim(1, 1));
            room.Viewers.Add(watcher);
            sharing = room.Publisher != null;
            allowAssist = room.AllowAssist;
            latest = room.Latest;
            publisher = room.Viewers.Count == 1 ? room.Publisher : null;
            publisherSend = room.PublisherSend;
        }
        await SendLocked(watcher.Send, socket, Status(sharing, allowAssist), WebSocketMessageType.Text);
        if (latest != null) await SendLocked(watcher.Send, socket, latest, WebSocketMessageType.Binary);
        if (publisher != null && publisherSend != null)
            await SendLocked(publisherSend, publisher, StartMessage, WebSocketMessageType.Text);
        return true;
    }

    public async Task LeaveAsync(string workId, WebSocket socket)
    {
        WebSocket? publisher;
        SemaphoreSlim? publisherSend;
        lock (gate)
        {
            if (!rooms.TryGetValue(workId, out var room)) return;
            room.Viewers.RemoveAll(item => item.Socket == socket);
            var lastWatcherLeft = room.Publisher != null && room.Viewers.Count == 0;
            publisher = lastWatcherLeft ? room.Publisher : null;
            publisherSend = lastWatcherLeft ? room.PublisherSend : null;
            if (room.Publisher == null && room.Viewers.Count == 0)
            {
                room.Latest = null;
                rooms.Remove(workId);
            }
        }
        if (publisher != null && publisherSend != null)
            await SendLocked(publisherSend, publisher, StopMessage, WebSocketMessageType.Text);
    }

    public async Task BroadcastAsync(string workId, WebSocket source, ReadOnlyMemory<byte> frame)
    {
        if (!IsJpeg(frame.Span)) return;
        List<Viewer> watchers;
        var payload = frame.ToArray();
        lock (gate)
        {
            if (!rooms.TryGetValue(workId, out var room) || room.Publisher != source || room.Viewers.Count == 0) return;
            room.Latest = payload;
            watchers = room.Viewers.ToList();
        }
        foreach (var watcher in watchers)
        {
            if (!await SendLocked(watcher.Send, watcher.Socket, payload, WebSocketMessageType.Binary))
                await LeaveAsync(workId, watcher.Socket);
        }
    }

    public void Revoke(string workId)
    {
        Room? room;
        lock (gate) rooms.Remove(workId, out room);
        if (room == null) return;
        Abort(room.Publisher);
        foreach (var watcher in room.Viewers) Abort(watcher.Socket);
    }

    public void Dispose()
    {
        List<WebSocket?> sockets;
        lock (gate)
        {
            sockets = rooms.Values.SelectMany(room => room.Viewers.Select(item => (WebSocket?)item.Socket).Append(room.Publisher)).ToList();
            rooms.Clear();
        }
        foreach (var socket in sockets) Abort(socket);
    }

    SemaphoreSlim? PublisherSend(string workId, WebSocket socket)
    {
        lock (gate)
            return rooms.TryGetValue(workId, out var room) && room.Publisher == socket ? room.PublisherSend : null;
    }

    static byte[] Status(bool sharing, bool allowAssist)
        => JsonSerializer.SerializeToUtf8Bytes(new { type = "status", sharing, allowAssist }, JsonOptions);

    static bool IsJpeg(ReadOnlySpan<byte> data)
        => data.Length is >= 4 and <= MaxFrameLength
            && data[0] == 0xff && data[1] == 0xd8
            && data[^2] == 0xff && data[^1] == 0xd9;

    static async Task<bool> SendLocked(SemaphoreSlim? gate, WebSocket socket, ReadOnlyMemory<byte> payload, WebSocketMessageType type)
    {
        if (gate == null) return false;
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false)) return false;
        try
        {
            if (socket.State != WebSocketState.Open) return false;
            await socket.SendAsync(payload, type, true, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
            return false;
        }
        finally { gate.Release(); }
    }

    static void Abort(WebSocket? socket)
    {
        if (socket == null) return;
        try { socket.Abort(); }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
    }

    sealed class Room
    {
        public WebSocket? Publisher;
        public SemaphoreSlim? PublisherSend;
        public bool AllowAssist;
        public byte[]? Latest;
        public List<Viewer> Viewers { get; } = [];
    }

    sealed record Viewer(WebSocket Socket, SemaphoreSlim Send);
}

public static class ClassroomLiveShareEndpoints
{
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapClassroomLiveShare(this RouteGroupBuilder api)
    {
        api.MapGet("/classrooms/case-sessions/{id}/live/socket", Handle)
            .RequireAuthorization(policy => policy.RequireRole("Admin", "Teacher", "Student"));
    }

    internal static IQueryable<ClassroomCaseSession> ActiveSessions(SchoolDbContext db)
    {
        var rooms = TeachingLogic.OpenClassrooms(db, DateTime.UtcNow);
        return db.ClassroomCaseSessions.Where(session => session.Status == "active" && session.WorkId != ""
            && rooms.Any(room => room.Id == session.ClassroomId)
            && db.ClassroomStudents.Any(student => student.ClassroomId == session.ClassroomId && student.StudentUserId == session.UserId));
    }

    static IQueryable<ClassroomCaseSession> StudentSessions(SchoolDbContext db, HttpContext ctx)
        => ActiveSessions(db).Where(session => session.UserId == ctx.User.FindFirstValue(ClaimTypes.NameIdentifier));

    internal static IQueryable<ClassroomCaseSession> StaffSessions(SchoolDbContext db, HttpContext ctx)
    {
        var user = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return ctx.User.IsInRole("Admin") ? db.ClassroomCaseSessions
            : db.ClassroomCaseSessions.Where(session => db.Classrooms.Any(room => room.Id == session.ClassroomId && room.TeacherUserId == user));
    }

    static async Task Handle(string id, HttpContext ctx, SchoolDbContext db, ClassroomLiveShare shares)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { await Error(ctx, 400, "请使用 WebSocket 连接画面共享。"); return; }
        var origin = ctx.Request.Headers.Origin.ToString();
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
            || !string.Equals(originUri.Authority, ctx.Request.Host.Value, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(originUri.Scheme, ctx.Request.Scheme, StringComparison.OrdinalIgnoreCase))
        { await Error(ctx, 403, "画面共享只允许来自本系统页面的同源连接。"); return; }

        var student = ctx.User.IsInRole("Student");
        var staff = ctx.User.IsInRole("Admin") || ctx.User.IsInRole("Teacher");
        if (!student && !staff) { await Error(ctx, 403, "当前账号不能连接画面共享。"); return; }
        string? workId;
        if (student)
            workId = await StudentSessions(db, ctx).Where(item => item.Id == id).Select(item => item.WorkId).FirstOrDefaultAsync(ctx.RequestAborted);
        else if (!await StaffSessions(db, ctx).AnyAsync(item => item.Id == id, ctx.RequestAborted))
            workId = null;
        else
            workId = await ActiveSessions(db).Where(item => item.Id == id).Select(item => item.WorkId).FirstOrDefaultAsync(ctx.RequestAborted);
        if (string.IsNullOrEmpty(workId)) { await Error(ctx, 404, "实操共享已结束或当前无权访问"); return; }

        var socket = await ctx.WebSockets.AcceptWebSocketAsync();
        try
        {
            if (student) await RunPublisher(socket, workId, shares, ctx.RequestAborted);
            else await RunViewer(socket, workId, shares, ctx.RequestAborted);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "共享结束", CancellationToken.None); }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { socket.Abort(); }
            }
            socket.Dispose();
        }
    }

    static async Task RunPublisher(WebSocket socket, string workId, ClassroomLiveShare shares, CancellationToken cancellationToken)
    {
        var hello = await ReceiveAsync(socket, cancellationToken);
        if (hello == null || hello.Value.Type != WebSocketMessageType.Text) return;
        if (!TryReadAllowAssist(hello.Value.Payload, out var allowAssist))
        {
            await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, "共享参数无效");
            return;
        }
        if (!await shares.PublishAsync(workId, socket, allowAssist))
        {
            await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, "已有共享连接");
            return;
        }
        try
        {
            while (true)
            {
                var message = await ReceiveAsync(socket, cancellationToken);
                if (message == null) return;
                if (message.Value.Type == WebSocketMessageType.Binary)
                    await shares.BroadcastAsync(workId, socket, message.Value.Payload);
            }
        }
        finally { await shares.UnpublishAsync(workId, socket); }
    }

    static async Task RunViewer(WebSocket socket, string workId, ClassroomLiveShare shares, CancellationToken cancellationToken)
    {
        if (!await shares.WatchAsync(workId, socket))
        {
            await CloseAsync(socket, WebSocketCloseStatus.EndpointUnavailable, "共享名额已满");
            return;
        }
        try
        {
            while (await ReceiveAsync(socket, cancellationToken) != null) { }
        }
        finally { await shares.LeaveAsync(workId, socket); }
    }

    static bool TryReadAllowAssist(byte[] payload, out bool allowAssist)
    {
        allowAssist = false;
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("allowAssist", out var value)
                || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
            allowAssist = value.GetBoolean();
            return true;
        }
        catch (JsonException) { return false; }
    }

    static async Task<(WebSocketMessageType Type, byte[] Payload)?> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ClassroomLiveShare.MaxFrameLength + 1);
        try
        {
            var count = 0;
            ValueWebSocketReceiveResult result;
            do
            {
                var space = buffer.Length - count;
                if (space == 0) { await CloseAsync(socket, WebSocketCloseStatus.MessageTooBig, "共享画面过大"); return null; }
                result = await socket.ReceiveAsync(buffer.AsMemory(count, space), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                count += result.Count;
            } while (!result.EndOfMessage);
            if (count > ClassroomLiveShare.MaxFrameLength)
            {
                await CloseAsync(socket, WebSocketCloseStatus.MessageTooBig, "共享画面过大");
                return null;
            }
            return (result.MessageType, buffer.AsSpan(0, count).ToArray());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string reason)
    {
        if (socket.State != WebSocketState.Open) return;
        try { await socket.CloseAsync(status, reason, CancellationToken.None); }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { socket.Abort(); }
    }

    static async Task Error(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        await ctx.Response.WriteAsJsonAsync(new { message }, JsonOptions);
    }
}
