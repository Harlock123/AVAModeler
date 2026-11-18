namespace AVAModeler.Layout;

/// <summary>Some of a scene's items, by name, kept from AI agents' changes (see <see cref="LayoutSession.AgentLocks"/>).</summary>
public sealed record SceneLock(string Name, string Owner, IReadOnlyList<string> Items, string? Note = null);
