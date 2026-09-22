namespace ARandomizedReborn;

public enum ProgressStepKind
{
    /// <summary>An on/off condition.</summary>
    Flag,

    /// <summary>A running count that must reach <see cref="CheckStepDefinition.Target"/>.</summary>
    Counter,
}

/// <param name="Target">For <see cref="ProgressStepKind.Counter"/>, how high the count must go. Ignored for flags.</param>
/// <param name="Automatic">True when the plugin updates this step by watching the game; false when the player reports progress themselves.</param>
public sealed record CheckStepDefinition(string Id, string Label, ProgressStepKind Kind, int Target = 1, bool Automatic = false);
