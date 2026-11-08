using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Glint.Tuning;

/// <summary>Where Claude Code was found: a Windows install, or inside WSL.</summary>
public sealed record ClaudeCli(string Kind, string Path);

/// <summary>
/// "Tune with AI": runs Claude Code headless (<c>claude -p</c>, the user's own login, no API key in Glint) against Glint's
/// own MCP server. Claude gets no built-in tools at all (no shell, files or web) and only the tuning tools, so it can read
/// the hardware, run stress tests, apply and save results, and nothing else. Every safety rail lives in
/// <see cref="TuningService"/>, so a confused or wrong plan can only fail tests, never cross a limit.
/// Works with a Windows install of Claude Code or one inside WSL (Glint's MCP server is reached through WSL interop).
/// One run at a time; the transcript is kept for the dashboard.
/// </summary>
public sealed class AiTuner : IDisposable
{
    public static readonly string[] Tools =
    {
        "glint_tuning_status", "glint_tuning_gpu_test", "glint_tuning_wait", "glint_tuning_stop",
        "glint_tuning_gpu_apply", "glint_tuning_gpu_save", "glint_tuning_gpu_stock", "glint_tuning_crashes",
        "glint_tuning_cpu_status", "glint_tuning_cpu_test", "glint_tuning_cpu_save",
    };
    const int MaxTranscript = 300;

    readonly Func<ClaudeCli?> _find;
    readonly string _glintExe;
    readonly object _gate = new();
    readonly List<string> _transcript = new();
    Process? _proc;
    public bool Running { get { lock (_gate) return _proc is { HasExited: false }; } }
    public string? Error { get; private set; }
    public string? Summary { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public string? Goal { get; private set; }
    /// <summary>Raised for every transcript line (the dashboard shows them live).</summary>
    public event Action<string>? Line;
    /// <summary>Called when a run ends (the service puts the card back to stock if a test was still running).</summary>
    public event Action? Ended;

    /// <param name="find">Tests pass a fake; the default looks for Claude Code on Windows, then in WSL.</param>
    public AiTuner(string glintExe, Func<ClaudeCli?>? find = null)
    {
        _glintExe = glintExe;
        _find = find ?? FindClaude;
    }

    public object Status()
    {
        lock (_gate)
            return new { running = Running, goal = Goal, startedAt = StartedAt, error = Error, summary = Summary,
                         cli = Cached?.Kind, cliPath = Cached?.Path, transcript = _transcript.TakeLast(80).ToList() };
    }

    ClaudeCli? _cached; bool _looked;
    ClaudeCli? Cached { get { if (!_looked) { _looked = true; try { _cached = _find(); } catch { _cached = null; } } return _cached; } }

    /// <summary>Start a run. goal: "gpu", "cpu" or "all"; budgetMinutes caps how long Claude may test.</summary>
    public object Start(string goal, int budgetMinutes)
    {
        lock (_gate)
        {
            if (Running) throw new InvalidOperationException("an AI tuning run is already going");
            _looked = false;
            var cli = Cached ?? throw new InvalidOperationException("Claude Code isn't installed (Windows or WSL). Install it from claude.ai/code and sign in once with 'claude', then try again.");
            goal = goal is "cpu" or "all" ? goal : "gpu";
            budgetMinutes = Math.Clamp(budgetMinutes, 5, 240);
            var psi = BuildStart(cli, goal, budgetMinutes);
            _transcript.Clear(); Error = null; Summary = null; Goal = goal; StartedAt = DateTime.Now;
            Add($"Starting Claude Code ({cli.Kind}) to tune: {goal}, up to {budgetMinutes} min");
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => { if (e.Data != null) OnStream(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) OnError(e.Data); };
            p.Exited += (_, _) => Finish(p);
            p.Start();
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            p.StandardInput.Write(Prompt(goal, budgetMinutes));
            p.StandardInput.Close();
            _proc = p;
            return new { started = true, cli = cli.Kind, goal, budgetMinutes };
        }
    }

    public object Stop()
    {
        Process? p;
        lock (_gate) p = _proc;
        if (p is { HasExited: false }) { try { p.Kill(entireProcessTree: true); } catch { } Add("Stopped by the user"); }
        return new { stopped = true };
    }

    // ------------------------------------------------------------------ process

    /// <summary>The exact command line (public for tests): no built-in tools, only Glint's tuning tools, no other MCP
    /// servers, no user/project settings or hooks, nothing saved, JSON stream out, prompt on stdin.</summary>
    public ProcessStartInfo BuildStart(ClaudeCli cli, string goal, int budgetMinutes)
    {
        bool wsl = cli.Kind == "wsl";
        string glint = wsl ? WslPath(_glintExe) : _glintExe;
        var mcp = new JsonObject { ["mcpServers"] = new JsonObject { ["glint"] = new JsonObject { ["type"] = "stdio", ["command"] = glint, ["args"] = new JsonArray("mcp", "--only", "tuning") } } };
        var args = new List<string>
        {
            "-p", "--output-format", "stream-json", "--verbose",
            "--mcp-config", mcp.ToJsonString(), "--strict-mcp-config",
            "--tools", "",
            "--allowedTools", string.Join(",", Tools.Select(t => "mcp__glint__" + t)),
            "--permission-mode", "dontAsk",
            "--setting-sources", "",
            "--no-session-persistence",
            "--append-system-prompt", SystemPrompt,
        };
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        if (wsl)
        {
            psi.FileName = Path.Combine(Environment.SystemDirectory, "wsl.exe");
            psi.ArgumentList.Add("-e"); psi.ArgumentList.Add(cli.Path);
        }
        else psi.FileName = cli.Path;
        foreach (var a in args) psi.ArgumentList.Add(a);
        // the user's own login, never a key or base URL Glint might have inherited
        foreach (var v in new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL" }) psi.Environment.Remove(v);
        return psi;
    }

    /// <summary>Feed one stream-json line (public for tests).</summary>
    public void OnStream(string line)
    {
        JsonNode? n;
        try { n = JsonNode.Parse(line); } catch { return; }
        switch ((string?)n?["type"])
        {
            case "assistant":
                foreach (var b in n!["message"]?["content"]?.AsArray() ?? new JsonArray())
                {
                    var t = (string?)b?["type"];
                    if (t == "text" && (string?)b!["text"] is { Length: > 0 } txt) Add(txt.Trim());
                    else if (t == "tool_use") Add("→ " + Short((string?)b!["name"]) + " " + Compact(b["input"]));
                }
                break;
            case "result":
                Summary = (string?)n!["result"];
                if ((bool?)n["is_error"] == true) Error = Summary ?? "Claude Code reported an error";
                break;
        }
    }

    void OnError(string line)
    {
        if (line.Contains("login", StringComparison.OrdinalIgnoreCase) || line.Contains("/login") || line.Contains("Invalid API key", StringComparison.OrdinalIgnoreCase))
            Error = "Claude Code isn't signed in: run 'claude' once and sign in, then try again.";
        Add("! " + line.Trim());
    }

    void Finish(Process p)
    {
        lock (_gate) if (_proc != p) return;
        if (p.ExitCode != 0 && Error == null) Error = $"Claude Code exited with code {p.ExitCode}";
        Add(Error != null ? "Ended: " + Error : "Finished");
        Ended?.Invoke();
    }

    void Add(string s)
    {
        if (s.Length > 2000) s = s[..2000] + "…";
        lock (_gate)
        {
            _transcript.Add(s);
            if (_transcript.Count > MaxTranscript) _transcript.RemoveRange(0, _transcript.Count - MaxTranscript);
        }
        Line?.Invoke(s);
    }

    static string Short(string? tool) => (tool ?? "?").Replace("mcp__glint__glint_", "").Replace('_', '.');
    static string Compact(JsonNode? n) { var s = n?.ToJsonString() ?? ""; return s.Length > 160 ? s[..160] + "…" : s; }

    // ------------------------------------------------------------------ finding Claude Code

    /// <summary>A Windows install first (native installer, npm, winget), then WSL's default distribution.</summary>
    public static ClaudeCli? FindClaude()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var p in new[] { Path.Combine(home, ".local", "bin", "claude.exe"), Path.Combine(local, "Programs", "claude", "claude.exe"),
                                  Path.Combine(appData, "npm", "claude.cmd") })
            if (File.Exists(p)) return new("windows", p);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { var p = Path.Combine(dir.Trim(), "claude.exe"); if (File.Exists(p)) return new("windows", p); } catch { }
        }
        var wsl = Path.Combine(Environment.SystemDirectory, "wsl.exe");
        if (!File.Exists(wsl)) return null;
        // login shell first (~/.profile puts ~/.local/bin on PATH); then the user's own interactive shell (zsh users set PATH in .zshrc)
        foreach (var cmd in new[] { "command -v claude", "exec \"${SHELL:-bash}\" -lic 'command -v claude' 2>/dev/null" })
        {
            var found = Run(wsl, new[] { "-e", "sh", "-lc", cmd }, 15000)?.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('/'));
            if (found != null) return new("wsl", found);
        }
        return null;
    }

    static string WslPath(string windowsPath)
    {
        var wsl = Path.Combine(Environment.SystemDirectory, "wsl.exe");
        var p = Run(wsl, new[] { "-e", "wslpath", "-u", windowsPath }, 15000)?.Trim();
        if (string.IsNullOrEmpty(p)) throw new InvalidOperationException("couldn't map Glint's path into WSL");
        return p;
    }

    static string? Run(string exe, IEnumerable<string> args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8 };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return null; }
            return o.Result;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ prompts

    public const string SystemPrompt = """
You are Glint's overclocking assistant. You tune this PC through Glint's tuning tools only. Glint enforces every
safety limit itself (offset caps from the card's driver range, temperature cut-offs, refusing unproven settings,
back to stock after every test), so focus on finding the best STABLE result efficiently.
Rules:
- Start with glint_tuning_status. If tuning is off, blocked, or there's no GPU, say why in one sentence and stop.
- Never retry a setting the history already shows as failed (including "the PC crashed or froze during this test"); stay below it.
- Tests run as jobs: after glint_tuning_gpu_test call glint_tuning_wait repeatedly until busy=false, then read the result.
- Prefer fewer, decisive tests. Respect the time budget you are given.
- Finish by saving and applying the result with a safety margin, then write a short plain summary: the result, the
  measured gain (clock under load, VRAM bandwidth), temperatures, and anything the user should know.
""";

    static string Prompt(string goal, int budget) => goal switch
    {
        "cpu" => $"Tune the CPU's per-core Curve Optimizer with the CPU tools (start with glint_tuning_cpu_status; if CPU tuning isn't available here, say why and stop). Time budget: {budget} minutes.",
        _ => $"""
Tune the GPU. Time budget: {budget} minutes of testing.
Suggested method (adapt it to the history and the card's limits):
1. Core: test core +100 / memory 0 for 45 s. Step up by 50 MHz while tests pass, then go back to the last pass and
   step up by 15 MHz to find the edge. Then confirm the best core with a light test (light=true, 60 s): big offsets
   often fail at low load where boost clocks are highest; if it fails, go down 25 MHz until a light test passes.
2. Memory (at the confirmed core): step up by 250 Afterburner units with 45 s tests. Stop at the first failure, or when
   VRAM bandwidth (GBps in the result) stops improving for two steps (error retries), and keep the best-bandwidth step.
3. Soak the combination for 180 s. If it fails, lower core by 30 and memory by 250 and soak again.
4. Save and apply the final setting with a margin: core 30 MHz and memory 250 below the best passing values.
{(goal == "all" ? "Then tune the CPU the same way with the CPU tools (glint_tuning_cpu_status first)." : "")}
""",
    };

    public void Dispose() => Stop();
}
