using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Sound;
using System;
using System.Linq;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   public class SoundTests : BaseViewModelTestClass {
      [Fact]
      public void Dpcm_EncodeDecode_StaysClose() {
         var samples = new sbyte[300];
         for (int i = 0; i < samples.Length; i++) samples[i] = (sbyte)(Math.Sin(i / 7.0) * 100);

         var encoded = GbaSample.EncodeCompressed(samples);
         var decoded = GbaSample.Decode(encoded, 0, samples.Length, true);

         Assert.Equal(GbaSample.CompressedLength(samples.Length), encoded.Length);
         Assert.Equal(samples.Length, decoded.Length);
         Assert.Equal(samples[0], decoded[0]);
         Assert.Equal(samples[64], decoded[64]); // each 64-sample block restarts from an exact sample
         var error = Math.Sqrt(samples.Select((s, i) => (double)(s - decoded[i]) * (s - decoded[i])).Average());
         Assert.True(error < 20, $"rms error {error}");
      }

      [Fact]
      public void CompressedLength_MatchesWav2agbNoPad() {
         // 8184 samples: 127 full blocks of 33 bytes + a 56 sample block of 29 bytes = 4220
         Assert.Equal(4220, GbaSample.CompressedLength(8184));
         Assert.Equal(68, GbaSample.CompressedLength(128)); // 2 blocks of 33, aligned to 4
         Assert.Equal(4, GbaSample.CompressedLength(1));
      }

      [Fact]
      public void Sample_BuildAndRead_RoundTrip() {
         var samples = new sbyte[100];
         for (int i = 0; i < samples.Length; i++) samples[i] = (sbyte)(i - 50);
         var bytes = GbaSample.Build(samples, 10512, compressed: false);
         for (int i = 0; i < bytes.Length; i++) Model[0x40 + i] = bytes[i];

         Assert.True(GbaSample.TryRead(Model, 0x40, out var sample));
         Assert.False(sample.IsCompressed);
         Assert.Equal(100, sample.SampleCount);
         Assert.Equal(10512, sample.SampleRate, 3);
         Assert.Equal(samples, sample.Decode(Model));
      }

      [Fact]
      public void Wav_WriteAndRead_RoundTrip() {
         var samples = new sbyte[50];
         for (int i = 0; i < samples.Length; i++) samples[i] = (sbyte)(i * 2 - 50);
         var wav = GbaSample.ToWav(samples, 13379);

         Assert.True(GbaSample.TryReadWav(wav, out var read, out var rate, out var error), error);
         Assert.Equal(13379, rate);
         Assert.Equal(samples, GbaSample.Quantize(read));
      }

      [Fact]
      public void Resample_HalvesLength() {
         var samples = Enumerable.Range(0, 100).Select(i => i / 100.0).ToArray();
         var result = GbaSample.Resample(samples, 44100, 22050);
         Assert.Equal(50, result.Length);
         Assert.Equal(0, result[0], 6);
         Assert.Equal(0.5, result[25], 6);
      }

      private const string Song = @"
	.include ""MPlayDef.s""

	.equ	test_grp, voicegroup000
	.equ	test_pri, 0
	.equ	test_rev, reverb_set+50
	.equ	test_mvl, 100
	.equ	test_key, 0
	.equ	test_tbs, 1

	.section .rodata
	.global	test
	.align	2

test_1:
	.byte	KEYSH , test_key+0
	.byte	TEMPO , 120*test_tbs/2
	.byte		VOICE , 48
	.byte		PAN   , c_v-16
	.byte		VOL   , 47*test_mvl/mxv
test_1_B1:
	.byte		N12   , Cn4 , v112
	.byte	W12
	.byte		        En4 , v096
	.byte	W12
	.byte	GOTO
	 .word	test_1_B1
	.byte	FINE

	.align	2
test:
	.byte	1	@ NumTrks
	.byte	0	@ NumBlks
	.byte	test_pri	@ Priority
	.byte	test_rev	@ Reverb.

	.word	test_grp

	.word	test_1

	.end
";

      [Fact]
      public void Assembler_ReportsUndefinedVoicegroup() {
         var result = SongAssembler.Assemble(Song, 0x08100000);
         Assert.False(result.Success);
         Assert.Equal(new[] { "voicegroup000" }, result.UndefinedSymbols);
      }

      [Fact]
      public void Assembler_BuildsHeaderAndTrack() {
         var result = SongAssembler.Assemble(Song, 0x08100000, new System.Collections.Generic.Dictionary<string, int> { ["voicegroup000"] = 0x08200000 });
         Assert.True(result.Success, result.Error);
         Assert.Equal("test", result.SongName);
         var bytes = result.Bytes;
         Assert.Equal(new byte[] { 0xBC, 0, 0xBB, 60, 0xBD, 48, 0xBF, 0x30, 0xBE, 37 }, bytes.Take(10));
         Assert.Equal(new byte[] { 0xDB, 72, 112, 0x8C, 76, 96, 0x8C, 0xB2 }, bytes.Skip(10).Take(8));
         Assert.Equal(0x08100000 + 10, BitConverter.ToInt32(bytes, 18)); // GOTO test_1_B1
         Assert.Equal(0xB1, bytes[22]);
         Assert.Equal(24, result.HeaderOffset); // aligned to 4
         Assert.Equal(new byte[] { 1, 0, 0, 0x80 + 50 }, bytes.Skip(24).Take(4));
         Assert.Equal(0x08200000, BitConverter.ToInt32(bytes, 28));
         Assert.Equal(0x08100000, BitConverter.ToInt32(bytes, 32));
      }

      [Fact]
      public void Disassembler_RoundTrips() {
         var result = SongAssembler.Assemble(Song, 0x08000100, new System.Collections.Generic.Dictionary<string, int> { ["voicegroup000"] = 0x08000180 });
         for (int i = 0; i < result.Bytes.Length; i++) Model[0x100 + i] = result.Bytes[i];
         Assert.True(SongHeader.TryRead(Model, 0x100 + result.HeaderOffset, out var header));
         Assert.Equal(1, header.TrackCount);
         Assert.Equal(0x180, header.Voicegroup);

         var text = new SongDisassembler(Model).Disassemble(header, "again");
         Assert.Contains("KEYSH", text);
         Assert.Contains("GOTO", text);
         var again = SongAssembler.Assemble(text, 0x08000100);
         Assert.True(again.Success, again.Error);
         Assert.Equal(result.Bytes, again.Bytes);
      }

      [Fact]
      public void Renderer_PlaysSquareAndSampleNotes_ThenGoesQuiet() {
         // voicegroup at 0x100: voice 0 = square 1 (50% duty, instant attack, no decay), voice 1 = a direct-sound sine sample at 0x200
         int voicegroup = 0x100;
         Model[voicegroup] = 1; Model[voicegroup + 1] = 60; Model[voicegroup + 4] = 2; Model[voicegroup + 8] = 0; Model[voicegroup + 9] = 0; Model[voicegroup + 10] = 15; Model[voicegroup + 11] = 0;
         var sine = new sbyte[2000];
         for (int i = 0; i < sine.Length; i++) sine[i] = (sbyte)(Math.Sin(i * 2 * Math.PI * 220 / 13379) * 100);
         var sample = GbaSample.Build(sine, 13379, compressed: false, loopStart: 0);
         for (int i = 0; i < sample.Length; i++) Model[0x200 + i] = sample[i];
         Model[voicegroup + 12] = 0; Model[voicegroup + 13] = 60; Model.WritePointer(Token, voicegroup + 16, 0x200);
         Model[voicegroup + 20] = 255; Model[voicegroup + 21] = 0; Model[voicegroup + 22] = 255; Model[voicegroup + 23] = 0;

         // the song: tempo 120, voice 0 Cn3 for a quarter note, then voice 1 En3 for a quarter note, then FINE
         var song = SongAssembler.Assemble(@"
	.include ""MPlayDef.s""
	.equ	t_grp, voicegroup000
t_1:
	.byte	TEMPO , 120*1/2
	.byte	VOICE , 0
	.byte	VOL , 127
	.byte	N24 , Cn3 , v127
	.byte	W24
	.byte	VOICE , 1
	.byte	N24 , En3 , v127
	.byte	W24
	.byte	W24
	.byte	FINE
t:
	.byte	1
	.byte	0
	.byte	0
	.byte	0
	.word	t_grp
	.word	t_1
	.end
", 0x08000400, new System.Collections.Generic.Dictionary<string, int> { ["voicegroup000"] = 0x08000000 + voicegroup });
         Assert.True(song.Success, song.Error);
         for (int i = 0; i < song.Bytes.Length; i++) Model[0x400 + i] = song.Bytes[i];
         Assert.True(SongHeader.TryRead(Model, 0x400 + song.HeaderOffset, out var header));

         var (left, right) = new M4aRenderer(Model).Render(header);

         // 120 bpm: a quarter note is half a second. The song is 1.5 seconds long, then everything stops.
         double Rms(int fromSecondsTenths, int toSecondsTenths) {
            int from = fromSecondsTenths * M4aRenderer.OutputRate / 10, to = Math.Min(left.Length, toSecondsTenths * M4aRenderer.OutputRate / 10);
            if (to <= from) return 0;
            double sum = 0;
            for (int i = from; i < to; i++) sum += left[i] * left[i] + right[i] * right[i];
            return Math.Sqrt(sum / (to - from) / 2);
         }
         Assert.True(left.Length > 1.4 * M4aRenderer.OutputRate, $"only {left.Length} samples");
         Assert.True(Rms(1, 4) > 0.05, "the square wave note should be audible");
         Assert.True(Rms(6, 9) > 0.05, "the sampled note should be audible");
         Assert.True(Rms(12, 15) < 0.01, "nothing should play after the notes end");
         Assert.True(left.Length < 4 * M4aRenderer.OutputRate, "the render should stop soon after FINE");
      }
   }
}
