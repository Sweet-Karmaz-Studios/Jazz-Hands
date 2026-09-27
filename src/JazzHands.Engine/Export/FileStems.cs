using JazzHands.Core.Export;
using JazzHands.Core.Model;
using JazzHands.Media.Encode;

namespace JazzHands.Engine.Export;

/// <summary>
/// Stems written into the exported file itself: one more sound stream each, after the mix, named
/// for its stem, encoded as the mix is and pulled through the same mix with only its tracks reaching
/// the master, in step with the picture. No loudness gain: that is the mix's.
/// </summary>
internal sealed class FileStems : IDisposable
{
    private readonly List<(ExportStem Stem, AudioEncoder Encoder, ExportSound Sound, int Stream)> _stems = [];

    private FileStems()
    {
    }

    /// <summary>The metadata that names the mix when stems follow it.</summary>
    public static IReadOnlyDictionary<string, string> MixName { get; } = Named("Mix");

    /// <summary>Opens an encoder and a mix for each in-file stem of a plan and adds its stream; null when there are none.</summary>
    public static FileStems? Open(ExportPlan plan, Project project, string projectPath, ExportAudio audio, Muxer muxer)
    {
        ExportStem[] stems = [.. plan.Stems.Where(stem => stem.InFile)];
        if (stems.Length == 0)
        {
            return null;
        }

        Sequence sequence = project.Sequence(plan.SequenceId)!;
        var opened = new FileStems();
        try
        {
            foreach (ExportStem stem in stems)
            {
                AudioEncoder encoder = AudioEncoder.Open(new AudioEncoderSettings(stem.Encoder, audio.SampleRate, audio.Channels, stem.Bitrate), muxer.NeedsGlobalHeader);
                ExportSound sound;
                try
                {
                    sound = new ExportSound(project, sequence, projectPath, [.. plan.Ranges], audio.SampleRate, audio.Channels, new HashSet<string>(stem.TrackIds, StringComparer.Ordinal));
                }
                catch
                {
                    encoder.Dispose();
                    throw;
                }

                opened._stems.Add((stem, encoder, sound, muxer.AddStream(encoder, Named(stem.Name))));
            }

            return opened;
        }
        catch
        {
            opened.Dispose();
            throw;
        }
    }

    /// <summary>Writes each stem's sound up to a sample.</summary>
    public void WriteUpTo(long samples, Muxer muxer)
    {
        foreach ((_, AudioEncoder encoder, ExportSound sound, int stream) in _stems)
        {
            sound.WriteUpTo(Math.Min(samples, sound.Total), encoder, muxer, stream);
        }
    }

    /// <summary>Writes the rest of each stem and drains its encoder.</summary>
    public void Finish(Muxer muxer)
    {
        foreach ((_, AudioEncoder encoder, ExportSound sound, int stream) in _stems)
        {
            sound.WriteUpTo(sound.Total, encoder, muxer, stream);
            encoder.Flush(muxer, stream);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach ((_, AudioEncoder encoder, ExportSound sound, _) in _stems)
        {
            sound.Dispose();
            encoder.Dispose();
        }

        _stems.Clear();
    }

    // MP4 players read a track's name from its handler, Matroska and MOV from its title.
    private static Dictionary<string, string> Named(string name) => new(StringComparer.Ordinal) { ["title"] = name, ["handler_name"] = name };
}
