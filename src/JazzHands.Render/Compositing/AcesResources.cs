using System.Runtime.CompilerServices;
using JazzHands.Core.Model;
using JazzHands.Render.Color.Aces;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace JazzHands.Render.Compositing;

/// <summary>
/// The GPU side of the ACES 2.0 output transforms (Phase 44): for each display, its constants as an
/// immutable buffer and its hue tables as a 362 by 2 float texture, made the first time a pass
/// needs them and kept for the device's life. Aces.hlsli reads them at b1 and t1.
/// </summary>
internal sealed class AcesResources : IDisposable
{
    private readonly RenderDevice _device;
    private readonly Dictionary<AcesOutput, (ID3D11Buffer Constants, ID3D11Texture2D Table, ID3D11ShaderResourceView View)> _made = [];

    public AcesResources(RenderDevice device) => _device = device;

    /// <summary>The output transform for a display.</summary>
    public static AcesOutputTransform Transform(AcesOutput output) =>
        output == AcesOutput.Hdr10 ? AcesOutputTransform.Hdr10 : AcesOutputTransform.Rec709;

    /// <summary>A display's constants and table, made on first use.</summary>
    public (ID3D11Buffer Constants, ID3D11ShaderResourceView Table) For(AcesOutput output)
    {
        if (!_made.TryGetValue(output, out (ID3D11Buffer Constants, ID3D11Texture2D Table, ID3D11ShaderResourceView View) made))
        {
            made = Make(Transform(output));
            _made[output] = made;
        }

        return (made.Constants, made.View);
    }

    public void Dispose()
    {
        foreach ((ID3D11Buffer constants, ID3D11Texture2D table, ID3D11ShaderResourceView view) in _made.Values)
        {
            view.Dispose();
            table.Dispose();
            constants.Dispose();
        }

        _made.Clear();
    }

    private unsafe (ID3D11Buffer, ID3D11Texture2D, ID3D11ShaderResourceView) Make(AcesOutputTransform transform)
    {
        AcesConstants constants = AcesGpu.Constants(transform);
        ID3D11Buffer buffer = _device.Device.CreateBuffer(
            new BufferDescription
            {
                ByteWidth = (uint)((Unsafe.SizeOf<AcesConstants>() + 15) / 16 * 16),
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ConstantBuffer,
            },
            new SubresourceData((IntPtr)Unsafe.AsPointer(ref constants)));

        float[] texels = AcesGpu.Table(transform);
        var description = new Texture2DDescription
        {
            Width = AcesGpu.TableWidth,
            Height = AcesGpu.TableHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R32G32B32A32_Float,
            SampleDescription = new SampleDescription(1, 0),
            BindFlags = BindFlags.ShaderResource,
            Usage = ResourceUsage.Immutable,
        };

        fixed (float* data = texels)
        {
            ID3D11Texture2D table = _device.Device.CreateTexture2D(description, [new SubresourceData((IntPtr)data, AcesGpu.TableWidth * 4 * sizeof(float))]);
            return (buffer, table, _device.Device.CreateShaderResourceView(table));
        }
    }
}
