#!/usr/bin/env dotnet
#:package Microsoft.Playwright@1.61.0
#:property PublishAot=false

// REPL driver for Cærostris (Blazor WASM app hosted by CaerostrisServer).
// Headless Chromium via Microsoft.Playwright - no display server needed.
// Designed for agents: wrap in tmux, send-keys commands, capture-pane output.
//
// First run: `dotnet run driver.cs -- install` downloads the browser binary.
// Then: `dotnet run driver.cs` starts the REPL ("help" for commands).

using Microsoft.Playwright;

if (args is ["install"])
{
    return Microsoft.Playwright.Program.Main(["install", "chromium"]);
}

var shotDir = Environment.GetEnvironmentVariable("SCREENSHOT_DIR") ?? "/tmp/caerostris-shots";
Directory.CreateDirectory(shotDir);

IPlaywright? playwright = null;
IBrowser? browser = null;
IPage? page = null;
var consoleLog = new List<string>();

async Task Launch(string _)
{
    if (browser is not null) { Console.WriteLine("already launched"); return; }
    playwright = await Playwright.CreateAsync();
    browser = await playwright.Chromium.LaunchAsync(new() { Args = ["--no-sandbox"] });
    page = await browser.NewPageAsync();
    consoleLog.Clear();
    page.Console += (_, msg) => consoleLog.Add($"[{msg.Type}] {msg.Text}");
    page.PageError += (_, err) => consoleLog.Add($"[pageerror] {err}");
    Console.WriteLine("launched.");
}

async Task Nav(string url)
{
    if (page is null) { Console.WriteLine("ERROR: launch first"); return; }
    await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
    Console.WriteLine($"nav {url} → {page.Url}");
}

async Task Ss(string name)
{
    if (page is null) { Console.WriteLine("ERROR: launch first"); return; }
    var file = Path.Combine(shotDir, (string.IsNullOrEmpty(name) ? $"ss-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}" : name) + ".png");
    await page.ScreenshotAsync(new() { Path = file });
    Console.WriteLine($"screenshot: {file}");
}

async Task Click(string sel)
{
    if (page is null) { Console.WriteLine("ERROR: launch first"); return; }
    var r = await page.EvaluateAsync<string>(
        "s => { const el = document.querySelector(s); if (!el) return 'NOT_FOUND'; el.click(); return 'OK'; }", sel);
    Console.WriteLine($"click {sel} → {r}");
}

async Task ClickText(string text)
{
    if (page is null) { Console.WriteLine("ERROR: launch first"); return; }
    var r = await page.EvaluateAsync<string>("""
        t => {
          const els = [...document.querySelectorAll('button, a, [role="button"]')];
          const el = els.find(e => e.textContent?.trim() === t) ?? els.find(e => e.textContent?.includes(t));
          if (!el) return 'NOT_FOUND';
          el.click(); return 'OK: ' + el.tagName;
        }
        """, text);
    Console.WriteLine($"click-text \"{text}\" → {r}");
}

async Task Type(string text)
{
    if (page is null) return;
    await page.Keyboard.TypeAsync(text, new() { Delay = 30 });
}

async Task Press(string key)
{
    if (page is null) return;
    await page.Keyboard.PressAsync(key);
}

async Task Wait(string sel)
{
    if (page is null) { Console.WriteLine("ERROR: launch first"); return; }
    // Blazor WASM cold boot (download + JIT the .NET runtime in-browser)
    // routinely takes 10-15s on first nav - a short timeout here is the
    // #1 way to screenshot the "Loading..." spinner instead of the app.
    try
    {
        await page.WaitForSelectorAsync(sel, new() { Timeout = 20_000 });
        Console.WriteLine($"found: {sel}");
    }
    catch (TimeoutException) { Console.WriteLine($"TIMEOUT: {sel}"); }
}

async Task Eval(string expr)
{
    if (page is null) { Console.WriteLine("ERROR: launch first"); return; }
    try
    {
        var result = await page.EvaluateAsync<object>(expr);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
    }
    catch (Exception e) { Console.WriteLine($"ERROR: {e.Message}"); }
}

async Task Text(string sel)
{
    if (page is null) { Console.WriteLine("ERROR: launch first"); return; }
    var result = await page.EvaluateAsync<string?>(
        "s => (s ? document.querySelector(s) : document.body)?.innerText ?? '(null)'",
        string.IsNullOrEmpty(sel) ? null : sel);
    Console.WriteLine(result);
}

Task ConsoleDump(string filter)
{
    var lines = filter == "--errors"
        ? consoleLog.Where(l => l.StartsWith("[error]") || l.StartsWith("[pageerror]"))
        : consoleLog;
    var any = false;
    foreach (var line in lines) { Console.WriteLine(line); any = true; }
    if (!any) Console.WriteLine("(no console output)");
    return Task.CompletedTask;
}

async Task Quit(string _)
{
    if (browser is not null) { await browser.CloseAsync(); browser = null; page = null; }
    playwright?.Dispose();
    playwright = null;
}

var commands = new Dictionary<string, Func<string, Task>>
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

Console.WriteLine("caerostris driver — \"help\" for commands, \"launch\" to start");
Console.Write("driver> ");
string? line;
while ((line = Console.ReadLine()) is not null)
{
    var parts = line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0) { Console.Write("driver> "); continue; }
    var cmd = parts[0];
    var rest = parts.Length > 1 ? parts[1] : "";

    if (cmd == "help")
    {
        Console.WriteLine("commands: " + string.Join(", ", commands.Keys) + ", help");
    }
    else if (commands.TryGetValue(cmd, out var fn))
    {
        try { await fn(rest); }
        catch (Exception e) { Console.WriteLine($"ERROR: {e.Message}"); }
        if (cmd == "quit") { Console.Write("driver> "); break; }
    }
    else
    {
        Console.WriteLine($"unknown: {cmd} — try: help");
    }
    Console.Write("driver> ");
}

await Quit("");
return 0;
