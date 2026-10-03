using SCSKiller.Core;
using SCSKiller.Core.App;

namespace SCSKiller.Tests.Platform;

public class NewShadersTests
{
    const string Driver = "100.01";
    static readonly DateTimeOffset At = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    static GameState S(string id, long recorded = 0, long? planNew = null, GameStatus status = GameStatus.Stale, AntiCheat antiCheat = AntiCheat.None,
        bool playing = false, string driver = Driver, DateTimeOffset? warmedAt = null) =>
        new GameState(new Game(id, id, Store.Steam, "", ""), null, antiCheat, status, "", null, null, null, null, driver, warmedAt ?? At, null, false, null)
            with { RecordedSinceWarm = recorded, NewPipelines = planNew, Playing = playing };

    static (IReadOnlyList<GameState> Due, Dictionary<string, string> Notified) Due(Dictionary<string, string> notified, params GameState[] games) =>
        NewShaders.Due(games, [], notified, DriverStale(games));

    // as ScsKiller.DriverStaleGames gives them: warmed for another driver
    static HashSet<string> DriverStale(GameState[] games) => games.Where(s => s.WarmedDriverVersion != Driver).Select(s => s.Game.Id).ToHashSet();

    [Fact]
    public void Only_compiled_games_with_something_new_on_this_driver_are_told_about()
    {
        var (due, notified) = Due([],
            S("plan-check-nothing-new", planNew: 0, status: GameStatus.Warmed),
            S("never-compiled", recorded: 5, status: GameStatus.Ready) with { WarmedAt = null },
            S("unsupported", recorded: 5, status: GameStatus.Unsupported),
            S("anti-cheat", recorded: 5, antiCheat: AntiCheat.EasyAntiCheat),
            S("driver-stale", recorded: 5, driver: "99.00"),
            S("recorded", recorded: 3),
            S("plan-check-found-more", planNew: 2));
        Assert.Equal(["recorded", "plan-check-found-more"], due.Select(s => s.Game.Id));
        Assert.Equal(["recorded", "plan-check-found-more"], notified.Keys);
    }

    [Fact]
    public void Several_games_come_in_one_list_and_each_change_is_told_once()
    {
        var (due, notified) = Due([], S("a", recorded: 3), S("b", recorded: 1, planNew: 4));
        Assert.Equal(2, due.Count);
        Assert.Equal(5, NewShaders.Count(due[1]));

        (due, notified) = Due(notified, S("a", recorded: 3), S("b", recorded: 1, planNew: 4));
        Assert.Empty(due);

        (due, notified) = Due(notified, S("a", recorded: 7), S("b", recorded: 1, planNew: 4));
        Assert.Equal("a", Assert.Single(due).Game.Id);

        (due, notified) = Due(notified, S("a", recorded: 7), S("b", warmedAt: At.AddDays(1)));   // b compiled again: nothing new
        Assert.Empty(due);
        Assert.Equal(["a"], notified.Keys);
    }

    [Fact]
    public void A_running_game_waits_for_its_exit()
    {
        var (due, notified) = Due([], S("a", recorded: 3, playing: true));
        Assert.Empty(due);
        Assert.Empty(notified);

        (due, _) = Due(notified, S("a", recorded: 3));
        Assert.Equal("a", Assert.Single(due).Game.Id);
    }

    [Fact]
    public void A_game_queued_for_a_compile_is_not_told_about()
    {
        GameState[] games = [S("waiting", recorded: 1), S("idle", recorded: 1), S("plan-check", recorded: 1), S("done", recorded: 1)];
        QueueItem[] queue =
        [
            new("waiting", QueueStage.Waiting, null, null),
            new("idle", QueueStage.Warming, null, null, "starts when the PC is idle"),
            new("plan-check", QueueStage.Waiting, null, null, PlanCheck: true),   // a plan rebuild compiles nothing
            new("done", QueueStage.Done, null, null),
        ];
        var (due, notified) = NewShaders.Due(games, queue, new Dictionary<string, string>(), DriverStale(games));
        Assert.Equal(["plan-check", "done"], due.Select(s => s.Game.Id));
        Assert.Equal(["plan-check", "done"], notified.Keys);
    }
}
