using System.Numerics;
using System.Runtime.InteropServices;

namespace JazzHands.Render.Color.Aces;

/// <summary>
/// What Aces.hlsli reads for one output transform: its constants, in the cbuffer's layout, and its
/// hue tables as a 362 by 2 float4 texture's texels (row 0 each hue's cusp J, M, the hue and the
/// upper hull's 1/gamma; row 1 the reach gamut's M).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct AcesConstants
{
    /// <summary>AP0 to CAM16.</summary>
    public Vector4 InRgbToCam0, InRgbToCam1, InRgbToCam2;

    /// <summary>CAM16 to AP0.</summary>
    public Vector4 InCamToRgb0, InCamToRgb1, InCamToRgb2;

    /// <summary>The limiting primaries to CAM16.</summary>
    public Vector4 LimRgbToCam0, LimRgbToCam1, LimRgbToCam2;

    /// <summary>CAM16 to the limiting primaries.</summary>
    public Vector4 LimCamToRgb0, LimCamToRgb1, LimCamToRgb2;

    /// <summary>Compressed cone responses to Aab.</summary>
    public Vector4 ConeToAab0, ConeToAab1, ConeToAab2;

    /// <summary>Aab to compressed cone responses.</summary>
    public Vector4 AabToCone0, AabToCone1, AabToCone2;

    /// <summary>Limiting RGB to the display's encoding primaries.</summary>
    public Vector4 LimitToDisplay0, LimitToDisplay1, LimitToDisplay2;

    /// <summary>The display's encoding primaries to limiting RGB.</summary>
    public Vector4 DisplayToLimit0, DisplayToLimit1, DisplayToLimit2;

    /// <summary>AP0 to AP1.</summary>
    public Vector4 Ap0ToAp10, Ap0ToAp11, Ap0ToAp12;

    /// <summary>AP1 to AP0.</summary>
    public Vector4 Ap1ToAp00, Ap1ToAp01, Ap1ToAp02;

    /// <summary>x F_L_n, y cz, z 1/cz, w A_w_J.</summary>
    public Vector4 CamScalars;

    /// <summary>x peak nits, y g, z t_1, w s_2.</summary>
    public Vector4 ToneA;

    /// <summary>x u_2, y m_2, z forward limit, w 1/A_w_J.</summary>
    public Vector4 ToneB;

    /// <summary>x limit J max, y 1/model gamma, z sat, w sat threshold.</summary>
    public Vector4 Shape;

    /// <summary>x compr, y chroma compress scale, z mid J, w focus distance.</summary>
    public Vector4 Shape2;

    /// <summary>x lower hull 1/gamma, y peak / 100, z encoding (0 BT.1886, 1 PQ).</summary>
    public Vector4 Shape3;

    /// <summary>The hue interval search range: low, high.</summary>
    public int SearchLow, SearchHigh, SearchPad0, SearchPad1;
}

/// <summary>Builds <see cref="AcesConstants"/> and the table for an <see cref="AcesOutputTransform"/>.</summary>
public static class AcesGpu
{
    /// <summary>The table texture's width.</summary>
    public const int TableWidth = AcesOutputTransform.TotalTableSize;

    /// <summary>The table texture's height.</summary>
    public const int TableHeight = 2;

    /// <summary>The constants for a transform.</summary>
    public static AcesConstants Constants(AcesOutputTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);

        static (Vector4, Vector4, Vector4) Rows(M33 m) =>
            (Row(m.Row0), Row(m.Row1), Row(m.Row2));

        static Vector4 Row(D3 row) => new((float)row.X, (float)row.Y, (float)row.Z, 0);

        AcesOutputTransform.JMhParams input = transform.Input;
        AcesOutputTransform.JMhParams limit = transform.Limit;
        var c = new AcesConstants();
        (c.InRgbToCam0, c.InRgbToCam1, c.InRgbToCam2) = Rows(input.RgbToCam16C);
        (c.InCamToRgb0, c.InCamToRgb1, c.InCamToRgb2) = Rows(input.Cam16CToRgb);
        (c.LimRgbToCam0, c.LimRgbToCam1, c.LimRgbToCam2) = Rows(limit.RgbToCam16C);
        (c.LimCamToRgb0, c.LimCamToRgb1, c.LimCamToRgb2) = Rows(limit.Cam16CToRgb);
        (c.ConeToAab0, c.ConeToAab1, c.ConeToAab2) = Rows(input.ConeResponseToAab);
        (c.AabToCone0, c.AabToCone1, c.AabToCone2) = Rows(input.AabToConeResponse);
        (c.LimitToDisplay0, c.LimitToDisplay1, c.LimitToDisplay2) = Rows(transform.LimitToDisplay);
        (c.DisplayToLimit0, c.DisplayToLimit1, c.DisplayToLimit2) = Rows(transform.DisplayToLimit);
        (c.Ap0ToAp10, c.Ap0ToAp11, c.Ap0ToAp12) = Rows(AcesInput.Ap0ToAp1);
        (c.Ap1ToAp00, c.Ap1ToAp01, c.Ap1ToAp02) = Rows(AcesInput.Ap1ToAp0);

        Tonescale tone = transform.Tone;
        c.CamScalars = new Vector4((float)input.FLN, (float)input.Cz, (float)input.InvCz, (float)input.AwJ);
        c.ToneA = new Vector4((float)tone.N, (float)tone.G, (float)tone.T1, (float)tone.S2);
        c.ToneB = new Vector4((float)tone.U2, (float)tone.M2, (float)tone.ForwardLimit, (float)input.InvAwJ);
        c.Shape = new Vector4((float)transform.LimitJMax, (float)transform.ModelGammaInv, (float)transform.Sat, (float)transform.SatThr);
        c.Shape2 = new Vector4((float)transform.Compr, (float)transform.ChromaCompressScale, (float)transform.MidJ, (float)transform.FocusDist);
        c.Shape3 = new Vector4(
            (float)transform.LowerHullGammaInv,
            (float)(transform.PeakLuminance / AcesOutputTransform.ReferenceLuminance),
            transform.Encoding == DisplayEncoding.Pq ? 1 : 0,
            0);
        (c.SearchLow, c.SearchHigh) = transform.HueSearchRange;
        return c;
    }

    /// <summary>The table's texels, row 0 then row 1, four floats each.</summary>
    public static float[] Table(AcesOutputTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);

        var texels = new float[TableWidth * TableHeight * 4];
        for (int i = 0; i < TableWidth; i++)
        {
            texels[(i * 4) + 0] = (float)transform.GamutCusps[i].X;
            texels[(i * 4) + 1] = (float)transform.GamutCusps[i].Y;
            texels[(i * 4) + 2] = (float)transform.GamutCusps[i].Z;
            texels[(i * 4) + 3] = (float)transform.UpperHullGammaInv[i];

            int row1 = (TableWidth + i) * 4;
            texels[row1] = (float)transform.ReachM[i];
        }

        return texels;
    }
}
