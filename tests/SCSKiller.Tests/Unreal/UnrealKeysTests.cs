using System.Diagnostics;
using SCSKiller.Core;
using SCSKiller.Core.Games;
using SCSKiller.Core.Unreal;
using SCSKiller.Tests.Planning;
using Xunit.Abstractions;

namespace SCSKiller.Tests.Unreal;

/// <summary>Encrypted games on the dev machine (skipped when absent). Keys go to temp data dirs; no test prints a key.</summary>
[Trait("Needs", "Game")]
public class UnrealKeysTests(ITestOutputHelper output)
{
    static Game? Installed(string name)
    {
        try { return new SteamSource().Discover().FirstOrDefault(g => g.Name.Contains(name)); }
        catch (Exception) { return null; }
    }

    /// <summary>Windrose Demo (UE 5.6): the key is in its exe as 8 x mov dword, imm32; with it the files open, and they show
    /// the shaders live inside the materials (bShareMaterialShaderCode=False): indexable, see InlineShadersTests.</summary>
    [Fact]
    public void KeyFoundStaticallyStoredAndReused()
    {
        if (Installed("Windrose Demo") is not { } game) return;
        Ff7.Codecs();
        var data = Ff7.TempDir("keys-windrose");
        var sw = Stopwatch.StartNew();
        var e = new UnrealReader(data).Detect(game, out var notes)!;
        output.WriteLine($"{sw.Elapsed.TotalSeconds:F1} s: {e}\n{notes}");
        Assert.Contains("AES key: found in the exe", notes);
        Assert.False(e.Encrypted);
        Assert.Null(e.Unsupported);
        Assert.Equal("5.6", e.Version); // TOC version 8 says 5.5+; the package header layout says 5.6
        var keyFile = Path.Combine(data, "games", "steam_4291770", "aes.key");
        var key = File.ReadAllText(keyFile);
        Assert.DoesNotContain(key.Replace("0x", ""), notes, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("AES key: stored key", (new UnrealReader(data).Detect(game, out notes), notes).notes);

        // manual fallback: a wrong key is refused, the right one is stored
        var other = new UnrealReader(Ff7.TempDir("keys-manual"));
        Assert.False(other.SetKey(game, "0x" + new string('7', 64)));
        Assert.False(other.SetKey(game, "not a key"));
        Assert.True(other.SetKey(game, key));
        Assert.Contains("AES key: stored key", (other.Detect(game, out notes), notes).notes);
    }

    /// <summary>Mafia: The Old Country (UE 5.3): its exe keeps its code outside .text (a protector), so the static scan finds
    /// no key; the verdict is remembered per exe build.</summary>
    [Fact]
    public void ProtectedExeIsReportedAndNotRescanned()
    {
        if (Installed("Mafia: The Old Country") is not { } game) return;
        Ff7.Codecs();
        var data = Ff7.TempDir("keys-mafia");
        var e = new UnrealReader(data).Detect(game, out var notes)!;
        output.WriteLine($"{e}\n{notes}");
        Assert.True(e.Encrypted);
        Assert.Contains("looks protected", notes);
        var sw = Stopwatch.StartNew();
        new UnrealReader(data).Detect(game, out var again);
        output.WriteLine($"again: {sw.Elapsed.TotalSeconds:F1} s");
        Assert.Contains("looks protected", again);
        Assert.True(File.Exists(Path.Combine(data, "games", "steam_1941540", "aes.scan")));
    }
}
