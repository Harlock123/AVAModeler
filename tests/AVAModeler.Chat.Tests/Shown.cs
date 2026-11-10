namespace AVAModeler.Chat.Tests;

internal static class ShownExtensions
{
    /// <summary>The chat's lines but the usage tags after each turn (tests about what was said read these).</summary>
    public static IReadOnlyList<ChatEntry> Shown(this ChatSession s) => [.. s.Entries.Where(e => e is not UsageEntry)];
}
