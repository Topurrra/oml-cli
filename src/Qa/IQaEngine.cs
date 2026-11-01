using System.Text.Json;

namespace OmlCli.Qa;

// What the runner needs from a browser. Two engines:
//   cdp  Chrome DevTools Protocol: reads the DOM, the network (OutSystems calls, rows, server exceptions) and
//        JavaScript errors. Needs remote debugging, which a RemoteDebuggingAllowed=0 policy blocks.
//   uia  Windows UI Automation over a normal browser window: no DevTools, so it works under that policy, but it
//        sees only what the accessibility tree shows (no network, no JavaScript errors, no keyboard).
interface IQaEngine : IDisposable
{
    string Mode { get; }
    // False once the browser exited or the connection to it closed (a session then stops).
    bool Alive { get; }
    QaNetwork Network { get; }
    Task Navigate(string url);
    Task<long> WaitReady(int timeoutMs);
    // The page helper (see QaScript): locate, focusLast, valueOfLast, selectLast, feedback, invalid, text, table, summary.
    Task<JsonElement> Helper(string method, params object?[] args);
    // Click the element the last locate found (at is that locate's answer).
    Task Activate(JsonElement at);
    // Replace the text of the field the last locate found.
    Task Type(string text);
    Task Press(string key);
    Task<string> Screenshot(string file);
    Task<string> Url();
    Task<QaNetwork.CallResult> ReadCall(QaNetwork.Request request);
    (List<string> Exceptions, List<string> Console) ErrorsSince(int exceptionCount, int consoleCount);
    (int Exceptions, int Console) ErrorCounts();
    // For `qa record`: the next click a person makes in the window, as the page helper's recordAt answers it (target,
    // text, tag, field, and the inputs as they were just before the click). Null when the window was closed.
    Task<JsonElement?> NextClick(CancellationToken cancel);
}
