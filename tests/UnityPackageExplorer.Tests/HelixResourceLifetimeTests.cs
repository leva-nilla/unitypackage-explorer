using System.Reflection;
using System.Runtime.InteropServices;
using HelixToolkit.SharpDX.Core;
using HelixToolkit.SharpDX.Render;
using HelixToolkit.SharpDX.Utilities;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;

namespace UnityPackageExplorer.Tests;

public sealed class HelixResourceLifetimeTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void MorphDetachReleasesBothViewsAndAllowsReattach()
    {
        using var device = new Device(DriverType.Warp, DeviceCreationFlags.BgraSupport);
        var type = typeof(RenderCore).Assembly.GetType("HelixToolkit.SharpDX.Core.MorphTargetUploaderCore", true)!;
        using var core = (RenderCore)Activator.CreateInstance(type, nonPublic: true)!;

        for (var cycle = 0; cycle < 2; cycle++)
        {
            core.Attach(null);
            Assert.True(core.IsAttached);
            using var deltas = CreateTarget(device);
            using var offsets = CreateTarget(device);
            type.GetField("mtDeltasSRV", PrivateInstance)!.SetValue(core, deltas);
            type.GetField("mtOffsetsSRV", PrivateInstance)!.SetValue(core, offsets);

            core.Detach();
            core.Detach();

            Assert.True(deltas.IsDisposed);
            Assert.True(offsets.IsDisposed);
            Assert.Null(type.GetField("mtDeltasSRV", PrivateInstance)!.GetValue(core));
            Assert.Null(type.GetField("mtOffsetsSRV", PrivateInstance)!.GetValue(core));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void DepthPeelingBackgroundCopyDoesNotAccumulateComReferences(int samples)
    {
        using var device = new Device(DriverType.Warp, DeviceCreationFlags.BgraSupport);
        Assert.True(device.CheckMultisampleQualityLevels(Format.B8G8R8A8_UNorm, samples) > 0);
        using var device1 = device.QueryInterface<SharpDX.Direct3D11.Device1>();
        using var context = new DeviceContextProxy(device1.ImmediateContext1, device1);
        using var input = CreateTarget(device, samples);
        using var front = CreateTarget(device);
        using var back = CreateTarget(device);
        using var minMax = CreateTarget(device);
        using var core = new OITDepthPeeling();
        var type = typeof(OITDepthPeeling);
        type.GetField("frontBlendingTarget", PrivateInstance)!.SetValue(core, front);
        type.GetField("backBlendingTarget", PrivateInstance)!.SetValue(core, back);
        var targets = (ShaderResourceViewProxy?[])type.GetField("minMaxZTargets", PrivateInstance)!.GetValue(core)!;
        targets[0] = minMax;
        core.ExternRenderParameter = new RenderParameter
        {
            RenderTargetView = [input.RenderTargetView!],
            IsMSAATexture = samples > 1
        };
        var copy = type.GetMethod("InitializeMinMaxRenderTarget", PrivateInstance)!;
        var pointer = input.Resource!.NativePointer;
        var before = GetReferenceCount(pointer);

        for (var frame = 0; frame < 16; frame++) copy.Invoke(core, [context]);

        Assert.Equal(before, GetReferenceCount(pointer));
    }

    private static ShaderResourceViewProxy CreateTarget(Device device, int samples = 1)
    {
        var target = new ShaderResourceViewProxy(device, new Texture2DDescription
        {
            Width = 16,
            Height = 16,
            ArraySize = 1,
            MipLevels = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(samples, 0),
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            Usage = ResourceUsage.Default
        });
        target.CreateRenderTargetView();
        target.CreateTextureView();
        return target;
    }

    private static int GetReferenceCount(nint pointer)
    {
        Marshal.AddRef(pointer);
        return Marshal.Release(pointer);
    }
}
