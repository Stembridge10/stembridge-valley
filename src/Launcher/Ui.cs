namespace StembridgeValley.Launcher;

/// <summary>What the setup steps need from a screen. The window and the test console both implement it.</summary>
internal interface IUi
{
    /// <summary>Main line and an optional smaller line under it.</summary>
    void Status(string text, string? detail = null);
    /// <summary>0-100 fills the bar; -1 shows it as busy; null hides it.</summary>
    void Progress(int? percent);
    /// <summary>Ask for the invite code. Returns null if the player closed the window.</summary>
    Task<string?> AskInvite(string? problem);
    /// <summary>Ask where Stardew is installed. Returns null if cancelled.</summary>
    Task<string?> AskGameFolder(string? problem);
    /// <summary>A yes/no step with a friendly explanation. Returns false if declined or closed.</summary>
    Task<bool> Confirm(string title, string text, string yes);
    /// <summary>Show a problem. Returns true if the player pressed Try again.</summary>
    Task<bool> Problem(string title, string text, bool canRetry);
    /// <summary>Everything is set up: "play" to start, "farm" never returns (the window handles it), null if closed.</summary>
    Task<bool> Ready(FarmClient? farm);
    /// <summary>The game is running.</summary>
    void Playing();
    /// <summary>Everything finished; the window can close.</summary>
    void Done(string text);
}

/// <summary>Plain text version for automated tests (--headless). Never waits for a keyboard.</summary>
internal sealed class ConsoleUi : IUi
{
    private readonly Action<string> say;
    public ConsoleUi(Action<string> say) => this.say = say;

    public void Status(string text, string? detail = null) => say(detail == null ? text : $"{text} ({detail})");
    public void Progress(int? percent) { }
    public Task<string?> AskInvite(string? problem)
    {
        if (problem != null) say(problem);
        say("Paste your invite code. (Get yours by typing /play in the Junimo Hollow Discord. It starts with  sv: )");
        return Task.FromResult(Console.IsInputRedirected ? Console.In.ReadLine() : Console.ReadLine());
    }
    public Task<string?> AskGameFolder(string? problem)
    {
        say(problem ?? "Couldn't find Stardew Valley. Paste its install folder (the one with \"Stardew Valley.exe\"):");
        return Task.FromResult(Console.ReadLine()?.Trim().Trim('"'));
    }
    public Task<bool> Confirm(string title, string text, string yes)
    {
        say($"{title}: {text} -> {yes}");
        return Task.FromResult(true);
    }
    public Task<bool> Problem(string title, string text, bool canRetry)
    {
        say($"{title}: {text}");
        return Task.FromResult(false);
    }
    public Task<bool> Ready(FarmClient? farm) => Task.FromResult(true);
    public void Playing() => say("Have fun!");
    public void Done(string text) => say(text);
}
