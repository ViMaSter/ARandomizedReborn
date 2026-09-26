using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace ARandomizedReborn;

public sealed class AddonTreeServer : IDisposable
{
    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private TcpListener? listener;
    private CancellationTokenSource? cancellation;
    private Task? serverTask;

    public AddonTreeServer(IFramework framework, IGameGui gameGui, IPluginLog log)
    {
        this.framework = framework;
        this.gameGui = gameGui;
        this.log = log;
    }

    public int? Port { get; private set; }

    public void Start(int port)
    {
        if (this.listener != null)
            return;

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        this.listener = listener;
        this.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        this.cancellation = new CancellationTokenSource();
        this.serverTask = this.AcceptAsync(listener, this.cancellation.Token);
    }

    public void Stop()
    {
        this.cancellation?.Cancel();
        this.listener?.Stop();
        this.listener = null;
        this.Port = null;
        this.cancellation?.Dispose();
        this.cancellation = null;
    }

    public void Dispose() => this.Stop();

    private async Task AcceptAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = this.HandleAsync(client, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception exception)
            {
                this.log.Error(exception, "Addon tree server failed to accept a connection");
            }
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var stream = client.GetStream();
                using var reader = new System.IO.StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var request = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                if (request == null || request.Length > 4096 || !request.StartsWith("GET /addon?", StringComparison.Ordinal))
                {
                    await WriteResponseAsync(stream, 400, new { error = "Use GET /addon?name=Journal" }, timeout.Token).ConfigureAwait(false);
                    return;
                }

                string? line;
                var headerCount = 0;
                do
                {
                    line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                    if (++headerCount > 40 || line?.Length > 4096)
                        return;
                }
                while (!string.IsNullOrEmpty(line));

                var query = request.Split(' ', 3)[1].Split('?', 2)[1];
                var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var pair = part.Split('=', 2);
                    parameters[Uri.UnescapeDataString(pair[0])] = pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : string.Empty;
                }

                if (!parameters.TryGetValue("name", out var name) || name.Length is < 1 or > 32 ||
                    !System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9_]+$") ||
                    (parameters.TryGetValue("agent", out var agentName) && !Enum.TryParse<AgentId>(agentName, true, out _)))
                {
                    await WriteResponseAsync(stream, 400, new { error = "Invalid addon or agent name" }, timeout.Token).ConfigureAwait(false);
                    return;
                }

                var result = await this.framework.RunOnFrameworkThread(() => this.Snapshot(name, parameters)).WaitAsync(timeout.Token).ConfigureAwait(false);
                await WriteResponseAsync(stream, result == null ? 404 : 200, result ?? new { error = "Addon not found or address does not match" }, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                this.log.Error(exception, "Addon tree snapshot failed");
            }
        }
    }

    private unsafe object? Snapshot(string name, Dictionary<string, string> parameters)
    {
        if (name == "Capture")
            return DebugCapture.Snapshot();

        var addon = (AtkUnitBase*)this.gameGui.GetAddonByName(name).Address;
        if (addon == null || !addon->IsReady)
            return null;

        if (parameters.TryGetValue("address", out var address) && !MatchesAddress(address, (nint)addon))
            return null;

        var agentAddress = this.gameGui.FindAgentInterface(name).Address;
        if (parameters.TryGetValue("agent", out var agentName) && Enum.TryParse<AgentId>(agentName, true, out var agentId))
        {
            var module = AgentModule.Instance();
            if (module == null || agentAddress != (nint)module->GetAgentByInternalId(agentId))
                return null;
        }

        if (parameters.TryGetValue("agentAddress", out var requestedAgentAddress) &&
            (agentAddress == 0 || !MatchesAddress(requestedAgentAddress, agentAddress)))
            return null;

        var visited = new HashSet<nint>();
        return new
        {
            name = addon->NameString,
            address = $"0x{(nint)addon:X}",
            agent = agentAddress == 0 ? null : $"0x{agentAddress:X}",
            visible = addon->IsVisible,
            x = addon->X,
            y = addon->Y,
            scale = addon->Scale,
            widgetCount = addon->UldManager.ObjectCount,
            nodeListCount = addon->UldManager.NodeListCount,
            nodeList = SnapshotNodeList(&addon->UldManager),
            atkValues = SnapshotAtkValues(addon->AtkValues, addon->AtkValuesCount),
            root = SnapshotNode(addon->RootNode, visited, 0),
        };
    }

    private static unsafe object[] SnapshotNodeList(AtkUldManager* manager)
    {
        var nodes = new List<object>();
        if (manager->NodeList == null)
            return [];

        for (var index = 0; index < manager->NodeListCount && index < 2048; index++)
        {
            var node = manager->NodeList[index];
            if (node != null)
                nodes.Add(new { index, address = $"0x{(nint)node:X}", nodeId = node->NodeId, type = node->Type.ToString() });
        }

        return nodes.ToArray();
    }

    private static unsafe object? SnapshotNode(AtkResNode* node, HashSet<nint> visited, int depth)
    {
        if (node == null || depth >= 16 || visited.Count >= 2048 || !visited.Add((nint)node))
            return null;

        var children = new List<object>();
        var component = node->Type >= (NodeType)1000 ? ((AtkComponentNode*)node)->Component : null;
        var first = component == null ? node->ChildNode : component->UldManager.RootNode;
        var siblingCount = 0;
        for (var child = first; child != null && siblingCount++ < 256; child = child->PrevSiblingNode)
        {
            var result = SnapshotNode(child, visited, depth + 1);
            if (result != null)
                children.Add(result);
        }

        return new
        {
            address = $"0x{(nint)node:X}",
            nodeId = node->NodeId,
            type = node->Type.ToString(),
            visible = node->IsVisible(),
            x = node->X,
            y = node->Y,
            width = node->Width,
            height = node->Height,
            flags = $"0x{(uint)node->NodeFlags:X}",
            drawFlags = $"0x{node->DrawFlags:X}",
            text = node->Type == NodeType.Text ? ((AtkTextNode*)node)->NodeText.ToString() : null,
            texture = node->Type == NodeType.Image ? DescribeTexture((AtkImageNode*)node) : null,
            componentAddress = component == null ? null : $"0x{(nint)component:X}",
            componentNodeList = component == null ? null : SnapshotNodeList(&component->UldManager),
            treeListItems = component != null && component->GetComponentType() == ComponentType.TreeList ? SnapshotTreeList((AtkComponentTreeList*)component) : null,
            listItemIndex = component != null && component->GetComponentType() == ComponentType.ListItemRenderer ? ((AtkComponentListItemRenderer*)component)->ListItemIndex : (int?)null,
            children,
        };
    }

    private static unsafe string? DescribeTexture(AtkImageNode* image)
    {
        var parts = image->PartsList;
        if (parts == null || parts->Parts == null || image->PartId >= parts->PartCount)
            return null;

        var asset = parts->Parts[image->PartId].UldAsset;
        if (asset == null)
            return null;

        var texture = &asset->AtkTexture;
        if (texture->TextureType != TextureType.Resource || texture->Resource == null)
            return texture->TextureType.ToString();

        var handle = texture->Resource->TexFileResourceHandle;
        var file = handle == null ? string.Empty : handle->ResourceHandle.FileName.ToString();
        return $"icon={texture->Resource->IconId} ready={texture->IsTextureReady()} {file}";
    }

    private static unsafe object[] SnapshotAtkValues(AtkValue* values, int count)
    {
        var result = new List<object>();
        for (var index = 0; values != null && index < count && index < 4096; index++)
        {
            var value = values + index;
            result.Add(new { index, type = value->Type.ToString(), value = value->Type == 0 ? null : value->ToString() });
        }

        return result.ToArray();
    }

    private static unsafe object[] SnapshotTreeList(AtkComponentTreeList* list)
    {
        var result = new List<object>();
        for (var index = 0; index < (int)list->Items.Count && index < 1024; index++)
        {
            var item = list->Items[index].Value;
            if (item == null)
                continue;

            var uints = new List<uint>();
            foreach (var value in item->UIntValues)
                uints.Add(value);
            var strings = new List<string>();
            foreach (var value in item->StringValues)
                strings.Add(value.ToString());
            result.Add(new { index, type = item->Type.ToString(), state = item->State.ToString(), depth = item->Depth, hidden = item->IsHidden, height = item->Height, renderer = $"0x{(nint)item->Renderer:X}", uints, strings });
        }

        return result.ToArray();
    }

    private static bool MatchesAddress(string address, nint value)
    {
        var hex = address.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? address[2..] : address;
        return ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var requested) &&
               requested == (ulong)value;
    }

    private static async Task WriteResponseAsync(System.IO.Stream stream, int status, object value, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { WriteIndented = true });
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }
}