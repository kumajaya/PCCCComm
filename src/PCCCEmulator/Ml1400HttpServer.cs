// SPDX-License-Identifier: GPL-3.0-or-later
// 
// PCCCEmulator - PCCC Engine and Transports for .NET
// Copyright (c) 2026 Ketut Kumajaya
// 
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

// =============================================================================
// Ml1400HttpServer
//
// Minimal HTTP server that serves /filelist.xml for MicroLogix 1400 emulation.
//
// Real ML1400 exposes http://<host>/filelist.xml for data file enumeration.
// PCCCComm.GetDataMemory() fetches this URL for ML1400 via EIP transport.
//
// Uses HttpListener. On Windows, requires URL ACL for non-admin use:
//   netsh http add urlacl url=http://+:8080/ user=Everyone
// On Linux/macOS, no setup is needed for non-privileged ports (>= 1024).
// =============================================================================

using System;
using System.Net;
using System.Text;
using System.Threading;

/// <summary>
/// Minimal HTTP server that serves /filelist.xml for MicroLogix 1400 emulation.
/// </summary>
public sealed class Ml1400HttpServer : IDisposable
{
    private readonly HttpListener    _listener;
    private readonly PlcMemoryConfig _config;
    private readonly Thread          _thread;
    private volatile bool            _running;

    /// <summary>HTTP port (default 8080).</summary>
    public int Port { get; }

    public Ml1400HttpServer(PlcMemoryConfig config, int port = 8080)
    {
        _config   = config;
        Port      = port;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{port}/");

        _thread = new Thread(ListenerLoop)
        {
            IsBackground = true,
            Name         = "ML1400-HttpServer",
        };
    }

    public void Start()
    {
        try
        {
            _listener.Start();
            _running = true;
            _thread.Start();
            Logger.Always(this,
                $"ML1400 HTTP server listening on port {Port} (serves /filelist.xml)");
        }
        catch (HttpListenerException ex)
        {
            Logger.Always(this,
                $"ML1400 HTTP server could not start on port {Port}: {ex.Message}. " +
                $"On Windows run once as admin: " +
                $"netsh http add urlacl url=http://+:{Port}/ user=Everyone");
        }
    }

    public void Stop()
    {
        _running = false;
        try { _listener.Stop(); } catch { }
    }

    public void Dispose()
    {
        Stop();
        try { _listener.Close(); } catch { }
    }

    // ─── Listener loop ───────────────────────────────────────────────────────

    private void ListenerLoop()
    {
        while (_running)
        {
            try
            {
                var ctx = _listener.GetContext();
                ThreadPool.QueueUserWorkItem(_ => HandleRequest(ctx));
            }
            catch (HttpListenerException) when (!_running)
            {
                break;
            }
            catch (Exception ex)
            {
                if (_running)
                    Logger.Always(this, $"HTTP listener error: {ex.Message}");
            }
        }
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        try
        {
            string path = ctx.Request.Url?.AbsolutePath ?? "/";

            if (path.Equals("/filelist.xml", StringComparison.OrdinalIgnoreCase))
            {
                byte[] body = Encoding.UTF8.GetBytes(BuildFileListXml());
                ctx.Response.StatusCode      = 200;
                ctx.Response.ContentType     = "text/xml; charset=utf-8";
                ctx.Response.ContentLength64 = body.Length;
                ctx.Response.OutputStream.Write(body, 0, body.Length);
                Logger.Always(this,
                    $"Served /filelist.xml ({body.Length} bytes) to {ctx.Request.RemoteEndPoint}");
            }
            else
            {
                ctx.Response.StatusCode = 404;
                Logger.Always(this, $"HTTP 404: {path}");
            }
        }
        catch (Exception ex)
        {
            Logger.Always(this, $"HTTP request error: {ex.Message}");
        }
        finally
        {
            try { ctx.Response.OutputStream.Close(); } catch { }
        }
    }

    // ─── XML generation ──────────────────────────────────────────────────────

    private string BuildFileListXml()
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"ISO-8859-1\"?>");
        sb.AppendLine("<C>");

        foreach (var f in _config.DataFiles)
        {
            int elemCount = f.ElemSize > 0 ? f.SizeBytes / f.ElemSize : 0;
            sb.Append("<CD>");
            sb.Append($"<T2>{f.FileType}</T2>");
            sb.Append($"<T3>{f.FileNumber}</T3>");
            sb.Append($"<T4>{elemCount}</T4>");
            sb.Append("<T5>0</T5>");
            sb.AppendLine("</CD>");
        }

        sb.AppendLine("</C>");
        return sb.ToString();
    }
}
