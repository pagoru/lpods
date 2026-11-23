# Stereo mic on Linux: feasibility

**Verdict: feasible, about 2 to 3 days with H2 AirPods on the desk. Not started, because none of it can be verified without them.**

## What the feature is

On Windows, an app that opens the AirPods mic makes Windows switch them to the Hands-Free profile, and the sound drops
to mono 16 kHz. WPods avoids that: it keeps the AirPods on A2DP (stereo, high quality) and gets the mic through Apple's
own channel instead. The AirPods send the mic as AAC-ELD frames over AAP (opcode `0x58`, after a `START` packet), WPods
decodes them and feeds a virtual microphone.

Linux has the same problem: WirePlumber switches the card to `headset-head-unit` when a recording app opens the mic.
Only H2 models have the `0x58` stream (AirPods 4, AirPods Pro 2 and later); older ones keep the Hands-Free mic.

## What already works unchanged

- The AAP link is the same L2CAP channel LPods already uses, so `START` (`0x04 00 04 00 58 ...`) and the incoming `0x58`
  packets arrive through `AapSession.WriteAsync` and `AapSession.PacketReceived`. Nothing to port.
- Upstream's `MicPipeline` documents the stream: 48 kHz, 480-sample AAC-ELD frames, AudioSpecificConfig `F8 E6 30 00`.

## What has to be written

1. **A `LinuxMic` class** (about 300 lines) replacing `MicPipeline`. Upstream's is 1,684 lines, mostly Windows
   Hands-Free polling, the voice link of the Windows service and the ONNX noise-reduction stages, none of which apply.
   It sends `START` while something records, `STOP` (`0x04 00 04 00 58 00 00 00 02 00 03 01`) when nothing does or
   before a detach, and reconnects the stream like upstream does.
2. **A PipeWire source.** `pactl load-module module-pipe-source source_name=lpods_mic file=$XDG_RUNTIME_DIR/lpods-mic.fifo
   format=s16le rate=48000 channels=1` gives a normal source named "AirPods (LPods)" that every app lists. The decoded PCM
   is written to the FIFO. "Is something recording" comes from the source's state (`pactl list sources`, `RUNNING`), polled
   after a `pactl subscribe` event, the same way `DefaultOutputWatcher` follows the sink. `lpods mic default` would make it
   the default source.
3. **The decoder.** This is the one real risk, see below.
4. The page: remove `noStereoMic` from the capabilities that `linux.js` adds for H2 models, so the Microphone section
   shows the live state and the "Hands-Free" toggle goes back to meaning something (keep A2DP, mic through LPods).

## The risk: AAC-ELD decoding

Upstream binds FFmpeg 7.1 (`avcodec-61`) and reads `AVCodecContext` fields at hard-coded x64 offsets. That cannot be
copied to Fedora as is:

- Fedora 43's `ffmpeg-free` is 7.1 (`libavcodec.so.61`), but Fedora 44 and later move to FFmpeg 8 (`.so.62`), where the
  offsets differ. A wrong offset is memory corruption, not an error.
- Whether `ffmpeg-free` keeps the native `aac` decoder with ER AAC-ELD support has to be checked on a Fedora box
  (`ffmpeg -decoders | grep aac`). RPM Fusion's full `ffmpeg` has it.

Options, in order of preference:

1. FFmpeg through its public API only: set the options by name (`av_opt_set*`) and read results through
   `avcodec_receive_frame`, with no struct offsets. Works with any FFmpeg 6 to 8 that has the decoder.
2. `libfdk-aac` (RPM Fusion nonfree): the reference ER AAC-ELD decoder, small stable C API. A hard dependency on a nonfree repo.
3. A GStreamer pipeline (`avdec_aac`): same codec as option 1 behind a heavier dependency.

## Plan

1. `lpods mic-test`: START, decode 10 s to a WAV, STOP. Proves the decoder and the packets on real AirPods. This is the
   milestone that decides everything else.
2. `LinuxMic` plus the PipeWire source, behind a setting that is off by default.
3. Turn on the page changes, document the FFmpeg requirement in the spec (`Requires: libavcodec`).

Noise reduction (upstream's mic modes and enhancement) stays out: it needs ONNX models and is a separate decision.
