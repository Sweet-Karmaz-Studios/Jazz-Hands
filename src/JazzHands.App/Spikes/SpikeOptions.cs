using System.Globalization;

namespace JazzHands.App.Spikes;

/// <summary>Which presentation path the S1 spike is measuring.</summary>
public enum SpikeMode
{
    /// <summary>The shared-texture D3DImage bridge. The path Jazz Hands intends to ship.</summary>
    D3DImage,

    /// <summary>GPU readback into a WriteableBitmap. Measured only so the decision is documented.</summary>
    WriteableBitmap,

    /// <summary>A DXGI flip-model swap chain in a child window, bypassing WPF composition.</summary>
    SwapChain,
}

/// <summary>How to run the spike. Parsed from the command line so a run is reproducible.</summary>
public sealed record SpikeOptions
{
    /// <summary>The presentation path to measure.</summary>
    public SpikeMode Mode { get; init; } = SpikeMode.D3DImage;

    /// <summary>Back buffer width. The exit criterion is 3840.</summary>
    public int Width { get; init; } = 3840;

    /// <summary>Back buffer height. The exit criterion is 2160.</summary>
    public int Height { get; init; } = 2160;

    /// <summary>Frames per second the render thread aims for.</summary>
    public int TargetFps { get; init; } = 60;

    /// <summary>How long to measure before writing the report. Zero means run until closed.</summary>
    public double Seconds { get; init; }

    /// <summary>Seconds to run before measurement starts, so the JIT and the driver settle.</summary>
    public double WarmupSeconds { get; init; } = 2.0;

    /// <summary>Where to write the JSON report. Null prints it to the log only.</summary>
    public string? ReportPath { get; init; }

    /// <summary>Recreate the back buffer at the window's pixel size instead of a fixed size.</summary>
    public bool FollowWindowSize { get; init; }

    /// <summary>Rebuild the surface once mid-run to measure recovery, without needing Device Manager.</summary>
    public bool ForceRebuild { get; init; }

    /// <summary>Window client size in device-independent units. Zero means 1600x900.</summary>
    public int WindowWidth { get; init; }

    /// <summary>Window client height in device-independent units. Zero means 1600x900.</summary>
    public int WindowHeight { get; init; }

    /// <summary>Window position in device-independent units, or null to centre.</summary>
    public (double X, double Y)? Position { get; init; }

    /// <summary>
    /// Let the render thread run free instead of waiting for each present to complete. Measures
    /// whether a present hitch stalls the whole pipeline or only costs one preview frame.
    /// </summary>
    public bool NoHandshake { get; init; }

    /// <summary>Keep the Direct2D target bound across frames instead of rebinding each frame.</summary>
    public bool NoRebind { get; init; }

    /// <summary>Resize the window every second during the run, to exercise the resize path.</summary>
    public bool ResizeSweep { get; init; }

    /// <summary>Hide the status overlay, to rule out the overlay as a source of hitches.</summary>
    public bool NoStatus { get; init; }

    /// <summary>Keep the window on top and activated, so the compositor is not throttling it.</summary>
    public bool Topmost { get; init; }

    /// <summary>Show the window maximized, which is how the preview is actually watched.</summary>
    public bool Maximize { get; init; }

    /// <summary>
    /// Present from a dispatcher callback posted by the render thread, which is the shipping
    /// path. Turn it off with --rendering-present to reproduce the slow CompositionTarget route.
    /// </summary>
    public bool PresentOnDispatcher { get; init; } = true;

    /// <summary>Parses the spike arguments out of the application command line.</summary>
    public static SpikeOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var options = new SpikeOptions();
        for (int i = 0; i < args.Count; i++)
        {
            string argument = args[i];
            string? next = i + 1 < args.Count ? args[i + 1] : null;

            switch (argument)
            {
                case "--mode" when next is not null:
                    options = options with
                    {
                        Mode = next.ToLowerInvariant() switch
                        {
                            "writeablebitmap" => SpikeMode.WriteableBitmap,
                            "swapchain" => SpikeMode.SwapChain,
                            _ => SpikeMode.D3DImage,
                        },
                    };
                    i++;
                    break;

                case "--size" when next is not null:
                    string[] parts = next.Split('x', StringSplitOptions.TrimEntries);
                    if (parts.Length == 2 &&
                        int.TryParse(parts[0], CultureInfo.InvariantCulture, out int width) &&
                        int.TryParse(parts[1], CultureInfo.InvariantCulture, out int height))
                    {
                        options = options with { Width = width, Height = height };
                    }

                    i++;
                    break;

                case "--fps" when next is not null && int.TryParse(next, CultureInfo.InvariantCulture, out int fps):
                    options = options with { TargetFps = fps };
                    i++;
                    break;

                case "--seconds" when next is not null && double.TryParse(next, CultureInfo.InvariantCulture, out double seconds):
                    options = options with { Seconds = seconds };
                    i++;
                    break;

                case "--warmup" when next is not null && double.TryParse(next, CultureInfo.InvariantCulture, out double warmup):
                    options = options with { WarmupSeconds = warmup };
                    i++;
                    break;

                case "--out" when next is not null:
                    options = options with { ReportPath = next };
                    i++;
                    break;

                case "--window" when next is not null:
                    string[] window = next.Split('x', StringSplitOptions.TrimEntries);
                    if (window.Length == 2 &&
                        int.TryParse(window[0], CultureInfo.InvariantCulture, out int windowWidth) &&
                        int.TryParse(window[1], CultureInfo.InvariantCulture, out int windowHeight))
                    {
                        options = options with { WindowWidth = windowWidth, WindowHeight = windowHeight };
                    }

                    i++;
                    break;

                case "--rendering-present":
                    options = options with { PresentOnDispatcher = false };
                    break;

                case "--at" when next is not null:
                    string[] at = next.Split(",", StringSplitOptions.TrimEntries);
                    if (at.Length == 2 &&
                        double.TryParse(at[0], CultureInfo.InvariantCulture, out double left) &&
                        double.TryParse(at[1], CultureInfo.InvariantCulture, out double top))
                    {
                        options = options with { Position = (left, top) };
                    }

                    i++;
                    break;

                case "--no-rebind":
                    options = options with { NoRebind = true };
                    break;

                case "--resize-sweep":
                    options = options with { ResizeSweep = true };
                    break;

                case "--no-handshake":
                    options = options with { NoHandshake = true };
                    break;

                case "--no-status":
                    options = options with { NoStatus = true };
                    break;

                case "--topmost":
                    options = options with { Topmost = true };
                    break;

                case "--maximize":
                    options = options with { Maximize = true };
                    break;

                case "--follow-size":
                    options = options with { FollowWindowSize = true };
                    break;

                case "--force-rebuild":
                    options = options with { ForceRebuild = true };
                    break;

                default:
                    break;
            }
        }

        return options;
    }
}
