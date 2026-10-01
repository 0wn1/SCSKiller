using System.Runtime.InteropServices;
using System.Text;

namespace SCSKiller.Tests.Carved;

/// <summary>Real DXBC containers for the carver tests: d3dcompiler_47 (System32), SM5.1 with [RootSignature] so every
/// shader carries an RTS0 part, and rootsig_1_0 for RTS0-only containers.</summary>
static class Hlsl
{
    public const string Rs1 = "RootFlags(ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT), CBV(b0), DescriptorTable(SRV(t0)), StaticSampler(s0)";
    public const string Rs2 = "RootFlags(ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT), DescriptorTable(SRV(t0)), CBV(b0), StaticSampler(s0)";

    public static byte[] Vs(int seed, string rs) => Compile($$"""
        cbuffer C : register(b0) { float4 k; };
        [RootSignature("{{rs}}")]
        float4 main(float3 p : POSITION, float2 uv : TEXCOORD0, out float2 ouv : TEXCOORD0) : SV_Position { ouv = uv; return float4(p * {{seed}}.0, 1) + k; }
        """, "main", "vs_5_1");

    public static byte[] Ps(int seed, string rs) => Compile($$"""
        cbuffer C : register(b0) { float4 k; };
        Texture2D t : register(t0); SamplerState s : register(s0);
        [RootSignature("{{rs}}")]
        float4 main(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target { return t.Sample(s, uv) * {{seed}}.0 + k; }
        """, "main", "ps_5_1");

    public static byte[] RootSignature(string rs) => Compile($"#define RS \"{rs}\"\n", "RS", "rootsig_1_1"); // SM5.1's [RootSignature] is 1.1 too

    public static byte[] Compile(string src, string entry, string target)
    {
        var bytes = Encoding.ASCII.GetBytes(src);
        var hr = D3DCompile(bytes, bytes.Length, null, 0, 0, entry, target, 0, 0, out var code, out var errors);
        if (hr < 0) throw new InvalidOperationException($"D3DCompile {target} 0x{hr:x8}: {(errors != 0 ? Encoding.ASCII.GetString(Blob(errors)) : "")}");
        if (errors != 0) Blob(errors);
        return Blob(code);
    }

    delegate nint GetPointer(nint self);
    delegate nuint GetSize(nint self);

    static byte[] Blob(nint blob) // ID3DBlob: GetBufferPointer = slot 3, GetBufferSize = slot 4
    {
        var vt = Marshal.ReadIntPtr(blob);
        var p = Marshal.GetDelegateForFunctionPointer<GetPointer>(Marshal.ReadIntPtr(vt, 3 * IntPtr.Size))(blob);
        var n = Marshal.GetDelegateForFunctionPointer<GetSize>(Marshal.ReadIntPtr(vt, 4 * IntPtr.Size))(blob);
        var b = new byte[(int)n];
        Marshal.Copy(p, b, 0, b.Length);
        Marshal.Release(blob);
        return b;
    }

    [DllImport("d3dcompiler_47.dll")]
    static extern int D3DCompile(byte[] src, nint size, [MarshalAs(UnmanagedType.LPStr)] string? name, nint defines, nint include,
        [MarshalAs(UnmanagedType.LPStr)] string entry, [MarshalAs(UnmanagedType.LPStr)] string target, uint flags1, uint flags2, out nint code, out nint errors);
}
