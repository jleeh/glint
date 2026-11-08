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
        "glint_tuning_status", "glint_tuning_history", "glint_tuning_gpu_test", "glint_tuning_wait", "glint_tuning_stop",
        "glint_tuning_gpu_apply", "glint_tuning_gpu_save", "glint_tuning_gpu_stock", "glint_tuning_crashes",
        "glint_tuning_cpu_status", "glint_tuning_cpu_start", "glint_tuning_cpu_finish", "glint_tuning_cpu_stop",
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
    /// <summary>Raised when a run has started (the tuning monitor window opens on it).</summary>
    public event Action? Started;

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
            try { Started?.Invoke(); } catch { }
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
You are Glint's overclocking assistant. Your goal: the best sustained performance that is still fully stable, for the
GPU and the CPU, using Glint's tuning tools only. Glint enforces safety itself (offset caps from the card's driver range,
the power limit raised only to the card's own maximum, emergency temperature stops, refusing unproven settings, back to
stock after every test), so you decide WHAT to test and which result is best.
How to judge results:
- Stable = ok:true (every result bit-exact, no driver reset, no ECC errors). A failure means that setting is unstable.
- thermalLimited / powerLimited are NOT failures: the card is throttling at its own limits. Under a thermal or power
  limit, higher offsets often stop adding real performance (the card can't hold the clock). Judge performance by the
  measured clock under load (loadMhz), compute throughput (rate) and VRAM bandwidth (gBps), not by the offset.
- Stop climbing when performance stops improving (within ~1% for two steps) even if tests still pass; prefer the
  lowest offset within ~1% of the best performance (cooler, quieter, more margin).
Rules:
- Start with glint_tuning_status. If tuning is off, blocked, or there's no GPU, say why in one sentence and stop.
- Never retry a setting the history shows as failed (including "the PC crashed or froze during this test"); stay below it.
- Tests run as jobs: after glint_tuning_gpu_test call glint_tuning_wait repeatedly until busy=false, then read the result.
- Prefer fewer, decisive tests. Respect the time budget you are given.
- Finish by saving and applying the result, then write a short plain summary: stock vs tuned (clock under load,
  throughput, VRAM bandwidth, power, temperature), whether it is thermal/power-limited, and anything the user should know.
CPU (AMD Ryzen 5000/7000/9000 only):
- glint_tuning_cpu_status says whether this CPU can be tuned (curveOptimizer.tunable / why) and what the search has found.
  An Intel CPU has nothing to tune: report the microcode advisory if there is one and stop.
- The CPU search is not a short test: CoreCycler stress-tests each core alone for HOURS and raises a core's Curve Optimizer
  value by 1 on every error; it resumes by itself after a freeze (the user must press reset) or a reboot. Glint stops the
  stress test at the CPU temperature limit. Start it once with glint_tuning_cpu_start and then END YOUR RUN: do not wait for it
  or poll in a loop. Tell the user plainly that it takes hours, that they can keep using the PC lightly, what to do after a
  freeze, and to ask you again (or press Tune with AI again) when it is done.
- When called again: read glint_tuning_cpu_status. If phase is "searching" and summaryFound is false, report progress (values,
  adjustments, failed cores, WHEA count) and stop. If summaryFound is true (or phase is "stopped" with values the user wants
  to use), call glint_tuning_cpu_finish and present the BIOS sheet it returns exactly as given, plus: Curve Optimizer can't
  be set permanently from Windows, so these values must be typed into the BIOS, then checked for stability under a long load.
- Never call glint_tuning_cpu_finish with force=true unless the user asked for it.
""";

    static string Prompt(string goal, int budget) => goal switch
    {
        "cpu" => """
Tune the CPU's per-core Curve Optimizer with the CPU tools. Start with glint_tuning_cpu_status.
- If curveOptimizer.tunable is false, say why in one sentence (include the microcode advisory for Intel 13th/14th gen) and stop.
- If a search is already running or finished (phase searching / stopped / done), follow the "when called again" rules.
- Otherwise pick a start value for this CPU family from what you know (curveOptimizer.startHint is a sensible default: it
  is only where each core STARTS, errors raise it, so a little too aggressive just costs time; never below minStart),
  call glint_tuning_cpu_start, and explain in a few sentences: it takes several hours, resumes by itself after a freeze
  (press the reset button), Glint stops the test at the temperature limit, and to run Tune with AI again afterwards to get
  the BIOS values. The time budget does not apply to the search itself: it runs after you finish.
""",
        _ => $"""
Tune the GPU for the best stable performance. Time budget: {budget} minutes of testing.
Suggested method (adapt it to the history and the card's limits):
1. Baseline: a 45 s test at stock (core 0 / memory 0), unless the history already has a passing stock test on this driver.
2. Core: test core +100 / memory 0 for 45 s, then step up by 50 MHz while tests pass AND performance (loadMhz, rate)
   still improves; then step by 15 MHz around the edge. If the card is thermal- or power-limited and performance has
   flattened, stop there: more offset only adds heat.
3. Confirm the chosen core with a light test (light=true, 60 s): bursts at low average load reach the top boost clocks,
   where big offsets fail first. If it fails, go down 25 MHz until a light test passes.
4. Memory (at the confirmed core): step up by 250 Afterburner units with 45 s tests while bandwidth (gBps) improves;
   stop at the first failure or when bandwidth stops improving for two steps, and keep the best-bandwidth step.
5. Soak the combination for 180 s. If it fails, lower core by 30 and memory by 250 and soak again.
6. Save and apply the best stable setting (with a small margin if it sat right at the edge: core -15 / memory -250).
{(goal == "all" ? "Then handle the CPU with the CPU tools as described in the CPU rules (glint_tuning_cpu_status first; the CPU search is started, not waited for)." : "")}
""",
    };

    public void Dispose() => Stop();
}
