namespace OpenSkiTime.LiveTiming.Radio.Providers;

public sealed record LlmRequest(string System, string User, int MaxOutputTokens, double Temperature);
/// <summary>Model text and the token usage the provider reported (or an estimate when it reported none).</summary>
public sealed record LlmReply(string Text, long InputTokens, long OutputTokens);
public sealed record TtsAudio(byte[] Data, string ContentType, int DurationMs);

public interface IRadioLlm
{
    string Provider { get; }
    string Model { get; }
    Task<LlmReply> CompleteAsync(LlmRequest request, CancellationToken ct);
}
public interface IRadioTts
{
    string Provider { get; }
    Task<TtsAudio> SynthesizeAsync(string text, RadioVoice voice, CancellationToken ct);
}

/// <summary>A provider failure. Messages are written by OpenSkiTime and never echo response bodies, keys or text sent.</summary>
public sealed class RadioProviderException(string message) : Exception(message);

/// <summary>Durations from the audio itself, used for pauses, status and token-billed speech cost.</summary>
public static class AudioDuration
{
    public static int Milliseconds(byte[] data, string contentType)
    {
        ArgumentNullException.ThrowIfNull(data);
        return contentType == "audio/wav" ? Wav(data) : Mp3(data);
    }
    private static int Wav(byte[] data)
    {
        if (data.Length < 44) { return 0; }
        var byteRate = BitConverter.ToInt32(data, 28);
        return byteRate <= 0 ? 0 : (int)((long)(data.Length - 44) * 1000 / byteRate);
    }
    private static readonly int[] Mpeg1Bitrates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static readonly int[] Mpeg2Bitrates = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
    private static readonly int[][] SampleRates = [[11025, 12000, 8000], [0, 0, 0], [22050, 24000, 16000], [44100, 48000, 32000]];
    // Counts MPEG layer III frames; works for constant and variable bit rates without a decoder.
    private static int Mp3(byte[] data)
    {
        var i = 0;
        if (data.Length > 10 && data[0] == 'I' && data[1] == 'D' && data[2] == '3')
        { i = 10 + ((data[6] & 0x7F) << 21 | (data[7] & 0x7F) << 14 | (data[8] & 0x7F) << 7 | (data[9] & 0x7F)); }
        long samples = 0; var rate = 0;
        while (i + 4 <= data.Length)
        {
            if (data[i] != 0xFF || (data[i + 1] & 0xE0) != 0xE0) { i++; continue; }
            var version = (data[i + 1] >> 3) & 3; var layer = (data[i + 1] >> 1) & 3;
            var bitrateIndex = data[i + 2] >> 4; var rateIndex = (data[i + 2] >> 2) & 3; var padding = (data[i + 2] >> 1) & 1;
            if (version == 1 || layer != 1 || bitrateIndex is 0 or 15 || rateIndex == 3) { i++; continue; }
            var bitrate = (version == 3 ? Mpeg1Bitrates : Mpeg2Bitrates)[bitrateIndex] * 1000;
            rate = SampleRates[version][rateIndex];
            var perFrame = version == 3 ? 1152 : 576;
            var length = perFrame / 8 * bitrate / rate + padding;
            if (length <= 4) { i++; continue; }
            samples += perFrame; i += length;
        }
        return rate == 0 ? 0 : (int)(samples * 1000 / rate);
    }
}
