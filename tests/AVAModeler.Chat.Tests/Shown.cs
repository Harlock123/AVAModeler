namespace AVAModeler.Chat.Tests;

internal static class ShownExtensions
{
    /// <summary>
    /// The chat's lines but the usage tags after each turn and the Undo this reply buttons (tests about what was said
    /// read these; <see cref="UndoReplyTests"/> reads the buttons).
    /// </summary>
    public static IReadOnlyList<ChatEntry> Shown(this ChatSession s) => [.. s.Entries.Where(e => e is not (UsageEntry or ReplyUndoEntry))];
}
