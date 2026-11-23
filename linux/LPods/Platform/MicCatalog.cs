using WPods.Core;

namespace WPods.Audio;

/// <summary>One entry of a mic picker (upstream shape, without the processing stage).</summary>
internal sealed record MicOption(string Id, string Name);

/// <summary>
/// Linux stand-in for upstream's MicCatalog: the same model ids, so settings.json round-trips between platforms, but
/// no processing. Mic modes are not ported yet.
/// </summary>
internal static class MicCatalog
{
    public static readonly MicOption[] Standard = [new("gtcrn-vd", "GTCRN-vd 16k + HB gain"), new("gtcrn-hbpass", "GTCRN 16k + HB passthrough")];

    public static readonly MicOption[] VoiceIsolation =
    [
        new("dpdfnet8-t4", "DPDFNet-8 48k, 4 threads"), new("dpdfnet8", "DPDFNet-8 48k"), new("dpdfnet2", "DPDFNet-2 48k"),
        new("nsnet2", "NSNet2 48k"), new("fastenhancer-l", "FastEnhancer-L 48k"), new("fastenhancer-m", "FastEnhancer-M 48k"),
        new("fastenhancer-s", "FastEnhancer-S 48k"), new("fastenhancer-b", "FastEnhancer-B 48k"), new("fastenhancer-t", "FastEnhancer-T 48k"),
        new("dfn3-residual", "DFN3 + residual filter"),
    ];

    public static readonly MicOption[] WideSpectrum = [new("webrtc-ns", "WebRTC HPF + NS very high"), new("wiener", "Residual filter (Wiener)"), new("raw", "Raw")];

    public static readonly MicOption[] Enhancements =
    [
        new("appleInspired", "Apple-inspired"), new("clearAuto", "Clear voice, auto level"), new("clearFull", "Clear voice, full-range level"),
    ];

    public static string DefaultModel(MicMode mode) => mode switch
    {
        MicMode.Standard => "gtcrn-vd",
        MicMode.VoiceIsolation => "dpdfnet8",
        _ => "webrtc-ns",
    };

    /// <summary>Any id is kept: upstream's list changes between versions and the Linux build does not use it.</summary>
    public static MicOption? FindModel(MicMode mode, string id) =>
        (mode switch { MicMode.Standard => Standard, MicMode.VoiceIsolation => VoiceIsolation, _ => WideSpectrum })
            .FirstOrDefault(o => o.Id == id) ?? new MicOption(id, id);

    public static MicOption? FindEnhancement(string id) => Enhancements.FirstOrDefault(o => o.Id == id) ?? new MicOption(id, id);
}
