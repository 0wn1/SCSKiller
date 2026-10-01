using SCSKiller.Core;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unreal;
using SCSKiller.Tests.Planning;
using Xunit.Abstractions;

namespace SCSKiller.Tests.Unreal;

public class UnrealRhiTests(ITestOutputHelper output)
{
    static readonly string[] Sm5 = ["PCD3D_SM5"], Sm6 = ["PCD3D_SM6"], Both = ["PCD3D_SM5", "PCD3D_SM6"];

    static Dictionary<string, string> Config(string defaultEngine, string? windowsEngine = null)
    {
        var d = new Dictionary<string, string> { ["Game/Config/DefaultEngine.ini"] = defaultEngine };
        if (windowsEngine != null) d["Game/Config/Windows/WindowsEngine.ini"] = windowsEngine;
        return d;
    }

    static string Rhi(string value) => $"[/Script/WindowsTargetPlatform.WindowsTargetSettings]\nDefaultGraphicsRHI={value}\n";

    /// <summary>A user Saved folder with Config\Windows\GameUserSettings.ini (and optionally a log).</summary>
    static string UserDir(string gameUserSettings, string? log = null)
    {
        var d = Ff7.TempDir("rhi-user-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(d, "Config", "Windows"));
        File.WriteAllText(Path.Combine(d, "Config", "Windows", "GameUserSettings.ini"), gameUserSettings);
        if (log != null)
        {
            Directory.CreateDirectory(Path.Combine(d, "Logs"));
            File.WriteAllText(Path.Combine(d, "Logs", "Game.log"), log);
        }
        return d;
    }

    static string Api(int ue, string[] platforms, Dictionary<string, string> config, string? user = null, string launch = "") =>
        UnrealRhi.Resolve(ue, platforms, config, "Game", user, launch).Api;

    [Fact]
    public void ProjectDefault()
    {
        Assert.Equal("D3D12", Api(4, Sm5, Config(Rhi("DefaultGraphicsRHI_DX12"))));
        Assert.Equal("D3D11", Api(4, Sm5, Config(Rhi("DefaultGraphicsRHI_Default")))); // UE4's default is DX11
        Assert.Equal("D3D11", Api(4, Sm5, Config("[/Script/Engine.RendererSettings]\n")));
        Assert.Equal("D3D12", Api(5, Both, Config(Rhi("DefaultGraphicsRHI_Default")))); // UE5's is DX12
        Assert.Equal("Vulkan", Api(5, Both, Config(Rhi("DefaultGraphicsRHI_Vulkan"))));
        Assert.Equal("D3D11", Api(4, Sm5, Config(Rhi("DefaultGraphicsRHI_DX12"), Rhi("DefaultGraphicsRHI_DX11")))); // the platform ini overrides
        Assert.Equal("D3D12", Api(4, Sm6, Config(Rhi("DefaultGraphicsRHI_DX11")))); // SM6 runs on DX12 only
        Assert.Equal(UnrealRhi.Ambiguous, Api(5, Both, Config(Rhi("DefaultGraphicsRHI_DX11")))); // Palworld: DX12 shaders ship too
    }

    [Fact]
    public void UserSettingsAndLaunchOptions()
    {
        var dx12Pref = UserDir("[D3DRHIPreference]\nbUseD3D12InGame=True\n");
        Assert.Equal("D3D12 (user setting)", Api(4, Sm5, Config(Rhi("DefaultGraphicsRHI_Default")), dx12Pref));
        Assert.Equal("D3D11", Api(4, Sm5, Config(Rhi("DefaultGraphicsRHI_DX11")), dx12Pref)); // UE4: an explicit project default wins
        Assert.Equal("D3D11 (user setting)", Api(5, Both, Config(Rhi("DefaultGraphicsRHI_DX12")), UserDir("[D3DRHIPreference]\nPreferredRHI=dx11\n")));
        Assert.Equal("D3D12 (user setting)", Api(4, Sm5, Config(""), UserDir("[/Script/OakGame.OakGameUserSettings]\nPreferredGraphicsAPI=DX12\n"))); // Gearbox
        Assert.Equal("D3D11 (launch option)", Api(5, Both, Config(Rhi("DefaultGraphicsRHI_DX12")), dx12Pref, "-dx11 -skipintro"));
        Assert.Equal("D3D12", Api(5, Both, Config(Rhi("DefaultGraphicsRHI_DX12")), launch: "-nodx11warning")); // not the flag
    }

    [Fact]
    public void UnreadableConfig()
    {
        Assert.Equal(UnrealRhi.Ambiguous, Api(5, Both, new())); // encrypted paks, no log
        Assert.Equal("D3D12 (last run)", Api(5, Both, new(), UserDir("", "[2026.03.04-18.43.38:621][  0]LogRHI: Using Default RHI: D3D12\n")));
        Assert.Equal("D3D11 (last run)", Api(4, Sm5, new(), UserDir("", "LogD3D11RHI: Chosen D3D11 Adapter:\n")));
    }

    [Fact]
    public void CheckRefusesDirectX11()
    {
        var p = new Planner();
        var ue = new EngineInfo("Unreal", "4.26", null, "D3D11", false, null);
        Assert.Equal(new PlanCheck(Readiness.Unsupported, "runs on DirectX 11"), p.Check(Ff7.Game, ue, null, Ff7.Nvidia with { Profile = "unmeasured" }));
        Assert.Equal(new PlanCheck(Readiness.Unsupported, "runs on DirectX 11 (user setting)"), p.Check(Ff7.Game, ue with { GraphicsApi = "D3D11 (user setting)" }, null, Ff7.Nvidia with { Profile = "unmeasured" }));
        Assert.Equal(new PlanCheck(Readiness.Unsupported, "runs on Vulkan"), p.Check(Ff7.Game, ue with { GraphicsApi = "Vulkan" }, null, Ff7.Nvidia));
        Assert.Equal(Readiness.Ready, p.Check(Ff7.Game, ue with { GraphicsApi = "D3D12 (user setting)" }, null, Ff7.Nvidia).Readiness);
        var both = p.Check(Ff7.Game, ue with { GraphicsApi = UnrealRhi.Ambiguous }, null, Ff7.Nvidia);
        Assert.Equal(Readiness.Ready, both.Readiness);
        Assert.Contains("DirectX 11", both.Reason);
    }

    /// <summary>The installed UE games (dev machine; read-only: paks, the user's saved ini, logs, Steam launch options).
    /// Expectations only where the project's own config or shader formats settle it.</summary>
    [Fact]
    public void InstalledGames()
    {
        Ff7.Codecs();
        var expect = new Dictionary<string, string>
        {
            ["FINAL FANTASY VII REBIRTH"] = "D3D12", ["Orcs Must Die! 3"] = "D3D11", ["Stellar Blade™ Demo"] = "D3D12", ["Palworld"] = UnrealRhi.Ambiguous,
            ["Life is Strange: Reunion"] = "D3D12", ["Darwin's Paradox"] = "D3D12", ["Lost Records: Bloom & Rage"] = "D3D12",
        };
        var reader = new UnrealReader(Ff7.TempDir("rhi-data"));
        IEnumerable<Game> games;
        try { games = new SteamSource().Discover().Concat(new EpicSource().Discover()).ToList(); }
        catch (Exception) { return; } // no stores on this machine
        foreach (var g in games)
        {
            if (reader.Detect(g, out var why) is not { } e) continue;
            output.WriteLine($"{g.Name,-45} UE {e.Version,-4} {e.GraphicsApi,-22} {why}");
            if (expect.TryGetValue(g.Name, out var api)) Assert.Equal(api, e.GraphicsApi);
        }
    }
}
