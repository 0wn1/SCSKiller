namespace SCSKiller.Core.App;

/// <summary>When the app tells about compiled games that have pipelines to compile again: their recording (this PC's or
/// the community database's) or a newer planner found ones the last warm didn't compile. Driver-stale games are the
/// driver-update notification's.</summary>
public static class NewShaders
{
    public static long Count(GameState s) => (s.NewPipelines ?? 0) + s.RecordedSinceWarm;

    /// <summary>What a notification about the game says, as the notified store keeps it; null when there is nothing to tell.</summary>
    public static string? Key(GameState s, string driverVersion) =>
        s is { Status: GameStatus.Warmed or GameStatus.Stale, AntiCheat: AntiCheat.None, WarmedAt: { } at } && s.WarmedDriverVersion == driverVersion
        && Count(s) is var n and > 0 ? $"{at.UtcTicks}|{n}" : null;

    /// <summary>The games to notify about now and the notified store to keep. A running game waits for its exit, a game
    /// queued for a compile isn't told about, and one already told about is told again only when its count or warm changes.</summary>
    public static (IReadOnlyList<GameState> Due, Dictionary<string, string> Notified) Due(IEnumerable<GameState> games,
        IEnumerable<QueueItem> queue, IReadOnlyDictionary<string, string> notified, string driverVersion)
    {
        var queued = queue.Where(q => !q.PlanCheck && q.Stage is not (QueueStage.Done or QueueStage.Failed or QueueStage.Stopped))
            .Select(q => q.GameId).ToHashSet();
        var due = new List<GameState>();
        var keep = new Dictionary<string, string>();
        foreach (var s in games)
        {
            if (Key(s, driverVersion) is not { } key) continue;
            if (notified.GetValueOrDefault(s.Game.Id) == key) keep[s.Game.Id] = key;
            else if (!s.Playing && !queued.Contains(s.Game.Id))
            {
                due.Add(s);
                keep[s.Game.Id] = key;
            }
        }
        return (due, keep);
    }
}
