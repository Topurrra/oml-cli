using System.Diagnostics;
using System.Net;
using System.Text;

namespace OmlCli;

// `oml preview --serve`: one live preview page at http://127.0.0.1:<random port>/. It serves only that page ("/") and the
// module file's version ("/v", which the page polls once a second to reload after the file changed). Read-only: it never
// writes a file. Requests are handled one at a time on this thread, so the model is used by one thread only.
// Security: http.sys binds 127.0.0.1 only; the Host header must be 127.0.0.1:<port> or localhost:<port> (DNS rebinding);
// only GET; the page keeps the preview's no-network policy, except fetches to this server for the version.
static class PreviewServer
{
    public static int Run(string module, string element, IReadOnlyDictionary<string, string> overrides, string theme, int width, List<Preview.Step> steps, bool open)
    {
        string? version = null, page = null;
        string Version() { var f = new FileInfo(module); return f.Exists ? $"{f.LastWriteTimeUtc.Ticks}-{f.Length}" : "missing"; }
        string Render(string v)
        {
            using var borrowed = Model.Borrow(module);
            return Live(Preview.Render(borrowed.Module, element, overrides, theme, width, steps).Html, v);
        }
        version = Version();
        page = Render(version); // an unknown element or a bad file fails here, with the usual exit code

        var (listener, port) = Listen();
        var url = $"http://127.0.0.1:{port}/";
        TerminalOutput.PrintObject(new { url, module, element });
        Console.WriteLine($"oml preview is serving {element} at {url}. The page reloads when {Path.GetFileName(module)} changes. Press Ctrl+C to stop.");
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; try { listener.Stop(); } catch (ObjectDisposedException) { } };
        if (open)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            { Console.WriteLine("Could not open the browser. Open the address above yourself."); }
        }

        while (listener.IsListening)
        {
            HttpListenerContext context;
            try { context = listener.GetContext(); }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { break; }
            var (request, response) = (context.Request, context.Response);
            try
            {
                response.Headers["X-Content-Type-Options"] = "nosniff";
                response.Headers["Referrer-Policy"] = "no-referrer";
                response.Headers["Cache-Control"] = "no-store";
                response.Headers["X-Frame-Options"] = "DENY";
                var host = request.Headers["Host"] ?? "";
                var path = request.Url?.AbsolutePath ?? "";
                if (host != $"127.0.0.1:{port}" && host != $"localhost:{port}") Send(response, 403, "text/plain; charset=utf-8", "Forbidden.", request.HttpMethod == "HEAD");
                else if (request.HttpMethod != "GET") { response.Headers["Allow"] = "GET"; Send(response, 405, "text/plain; charset=utf-8", "Only GET.", request.HttpMethod == "HEAD"); }
                else if (path == "/v") Send(response, 200, "text/plain; charset=utf-8", Version());
                else if (path == "/")
                {
                    var now = Version();
                    if (now != version)
                    {
                        // Any failure (a file that is being written, an element that was renamed) shows on the page, and the
                        // page keeps polling, so the next change renders again. The server must not stop for it.
                        try { page = Render(now); Console.WriteLine($"Rendered again at {DateTime.Now:HH:mm:ss}."); }
                        catch (Exception e) { page = Live($"<!doctype html><meta charset=\"utf-8\"><title>Preview error</title><pre>{WebUtility.HtmlEncode(e.Message)}</pre>", now); Console.WriteLine("Render failed: " + e.Message); }
                        version = now;
                    }
                    response.Headers["Content-Security-Policy"] = LiveCsp;
                    Send(response, 200, "text/html; charset=utf-8", page);
                }
                else Send(response, 404, "text/plain; charset=utf-8", "Not found.");
            }
            // One bad request (a client that went away, a method that cannot carry a body) must never stop the server.
            catch (Exception e)
            {
                Console.WriteLine($"Request {request.HttpMethod} {request.Url?.AbsolutePath} failed: {e.Message}");
                try { response.Abort(); } catch (Exception) { }
            }
        }
        listener.Close();
        Console.WriteLine("oml preview stopped.");
        return 0;
    }

    // frame-ancestors works only as a header, so it is here and not in the page's meta tag.
    static readonly string LiveCsp = Preview.Csp.Replace("connect-src 'none'", "connect-src 'self'") + "; frame-ancestors 'none'";

    // The served page may fetch /v from this server, and reloads when the version differs from the one it was rendered with.
    static string Live(string html, string version)
    {
        // __omlReload lets this reload past the page's navigation guard, which cancels reloads that page code starts.
        var poll = "<script>window.__omlLiveReload=function(){window.__omlReload=true;location.reload();};(function(){var v=" + System.Text.Json.JsonSerializer.Serialize(version)
            + ";setInterval(function(){fetch('/v',{cache:'no-store'}).then(function(r){return r.text();}).then(function(t){if(t!==v)window.__omlLiveReload();}).catch(function(){});},1000);})();</script>\n";
        html = html.Replace("connect-src 'none'", "connect-src 'self'");
        var end = html.LastIndexOf("</body>", StringComparison.Ordinal);
        return end < 0 ? html + poll : html.Insert(end, poll);
    }

    // A HEAD response has the headers only: http.sys refuses a body for it.
    static void Send(HttpListenerResponse response, int status, string type, string body, bool head = false)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        response.StatusCode = status;
        response.ContentType = type;
        response.ContentLength64 = bytes.Length;
        if (!head) response.OutputStream.Write(bytes);
        response.Close();
    }

    // A free port from the system, then given to http.sys; another program can take it in between, so try a few times.
    static (HttpListener, int) Listen()
    {
        for (var attempt = 0; ; attempt++)
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/"); // an IP literal: http.sys binds this address only, never the LAN
            try { listener.Start(); return (listener, port); }
            catch (HttpListenerException) when (attempt < 10) { listener.Close(); }
            catch (HttpListenerException e) { throw new CliError("Cannot listen on 127.0.0.1: " + e.Message, 2); }
        }
    }
}
