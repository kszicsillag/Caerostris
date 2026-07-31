#!/usr/bin/env dotnet
#:sdk Microsoft.NET.Sdk.Web
#:package Microsoft.Playwright@1.61.0
#:property PublishAot=false

// HTTP command server for driving Cærostris (Blazor WASM app hosted by
// CaerostrisServer) via headless Chromium (Microsoft.Playwright). Runs as a
// plain background process; commands are sent over HTTP instead of an
// interactive REPL, so there's no terminal/pty to wrap (no tmux needed).
//
// First run: `dotnet run --file driver.cs -- install` downloads the browser
// binary and the OS-level shared libraries it needs (apt, via sudo).
// Then start it backgrounded (e.g. `dotnet run --file driver.cs &`), poll
// `GET /health` until it responds, then drive it with
// `POST /cmd` (body: "<command> [args]", same verbs as before).

using Microsoft.Playwright;

if (args is ["install"])
{
    var depsResult = Microsoft.Playwright.Program.Main(["install-deps", "chromium"]);
    if (depsResult != 0)
        return depsResult;

    return Microsoft.Playwright.Program.Main(["install", "chromium"]);
}

var shotDir = Environment.GetEnvironmentVariable("SCREENSHOT_DIR") ?? "/tmp/caerostris-shots";
Directory.CreateDirectory(shotDir);

var port = Environment.GetEnvironmentVariable("DRIVER_PORT") ?? "5299";

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders(); // keep stdout free of Kestrel noise; responses carry the real output
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
var app = builder.Build();

IPlaywright? playwright = null;
IBrowser? browser = null;
IPage? page = null;
var consoleLog = new List<string>();

async Task<string> Launch(string _)
{
    if (browser is not null) return "already launched";
    playwright = await Playwright.CreateAsync();
    browser = await playwright.Chromium.LaunchAsync(new() { Args = ["--no-sandbox"] });
    page = await browser.NewPageAsync();
    consoleLog.Clear();
    page.Console += (_, msg) => consoleLog.Add($"[{msg.Type}] {msg.Text}");
    page.PageError += (_, err) => consoleLog.Add($"[pageerror] {err}");
    return "launched.";
}

async Task<string> Nav(string url)
{
    if (page is null) return "ERROR: launch first";
    await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
    return $"nav {url} -> {page.Url}";
}

async Task<string> Ss(string name)
{
    if (page is null) return "ERROR: launch first";
    var file = Path.Combine(shotDir, (string.IsNullOrEmpty(name) ? $"ss-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}" : name) + ".png");
    await page.ScreenshotAsync(new() { Path = file });
    return $"screenshot: {file}";
}

async Task<string> Click(string sel)
{
    if (page is null) return "ERROR: launch first";
    var r = await page.EvaluateAsync<string>(
        "s => { const el = document.querySelector(s); if (!el) return 'NOT_FOUND'; el.click(); return 'OK'; }", sel);
    return $"click {sel} -> {r}";
}

async Task<string> ClickText(string text)
{
    if (page is null) return "ERROR: launch first";
    var r = await page.EvaluateAsync<string>("""
        t => {
          const els = [...document.querySelectorAll('button, a, [role="button"]')];
          const el = els.find(e => e.textContent?.trim() === t) ?? els.find(e => e.textContent?.includes(t));
          if (!el) return 'NOT_FOUND';
          el.click(); return 'OK: ' + el.tagName;
        }
        """, text);
    return $"click-text \"{text}\" -> {r}";
}

async Task<string> Type(string text)
{
    if (page is null) return "ERROR: launch first";
    await page.Keyboard.TypeAsync(text, new() { Delay = 30 });
    return "OK";
}

async Task<string> Press(string key)
{
    if (page is null) return "ERROR: launch first";
    await page.Keyboard.PressAsync(key);
    return "OK";
}

async Task<string> Wait(string sel)
{
    if (page is null) return "ERROR: launch first";
    // Blazor WASM cold boot (download + JIT the .NET runtime in-browser)
    // routinely takes 10-15s on first nav - a short timeout here is the
    // #1 way to screenshot the "Loading..." spinner instead of the app.
    try
    {
        await page.WaitForSelectorAsync(sel, new() { Timeout = 20_000 });
        return $"found: {sel}";
    }
    catch (TimeoutException) { return $"TIMEOUT: {sel}"; }
}

async Task<string> Eval(string expr)
{
    if (page is null) return "ERROR: launch first";
    try
    {
        var result = await page.EvaluateAsync<object>(expr);
        return System.Text.Json.JsonSerializer.Serialize(result);
    }
    catch (Exception e) { return $"ERROR: {e.Message}"; }
}

async Task<string> Text(string sel)
{
    if (page is null) return "ERROR: launch first";
    var result = await page.EvaluateAsync<string?>(
        "s => (s ? document.querySelector(s) : document.body)?.innerText ?? '(null)'",
        string.IsNullOrEmpty(sel) ? null : sel);
    return result ?? "(null)";
}

Task<string> ConsoleDump(string filter)
{
    var lines = filter == "--errors"
        ? consoleLog.Where(l => l.StartsWith("[error]") || l.StartsWith("[pageerror]"))
        : consoleLog;
    var joined = string.Join('\n', lines);
    return Task.FromResult(string.IsNullOrEmpty(joined) ? "(no console output)" : joined);
}

async Task<string> Quit(string _)
{
    if (browser is not null) { await browser.CloseAsync(); browser = null; page = null; }
    playwright?.Dispose();
    playwright = null;
    return "quit.";
}

var commands = new Dictionary<string, Func<string, Task<string>>>
{
    ["launch"] = Launch,
    ["nav"] = Nav,
    ["ss"] = Ss,
    ["click"] = Click,
    ["click-text"] = ClickText,
    ["type"] = Type,
    ["press"] = Press,
    ["wait"] = Wait,
    ["eval"] = Eval,
    ["text"] = Text,
    ["console"] = ConsoleDump,
    ["quit"] = Quit,
};

app.MapGet("/health", () => "ok");

app.MapPost("/cmd", async (HttpRequest req) =>
{
    using var reader = new StreamReader(req.Body);
    var line = (await reader.ReadToEndAsync()).Trim();
    var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0) return Results.Text("ERROR: empty command");

    var cmd = parts[0];
    var rest = parts.Length > 1 ? parts[1] : "";

    if (cmd == "help")
        return Results.Text("commands: " + string.Join(", ", commands.Keys) + ", help, shutdown");

    if (cmd == "shutdown")
    {
        _ = Task.Run(async () => { await Task.Delay(200); await app.StopAsync(); });
        return Results.Text("shutting down.");
    }

    if (!commands.TryGetValue(cmd, out var fn))
        return Results.Text($"unknown: {cmd} - try: help");

    string output;
    try { output = await fn(rest); }
    catch (Exception e) { output = $"ERROR: {e.Message}"; }

    return Results.Text(output);
});

await app.RunAsync();
return 0;
