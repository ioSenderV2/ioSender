/*
 * ButtonClickSound.cs - part of CNC Controls library
 *
 * Synthesized click feedback for every AppleStyles-templated button (style guide "Button feel" section).
 * Primary/secondary tone is picked by comparing the clicked Button's Style against the PrimaryButtonStyle
 * resource - everything else (the implicit default Button style) gets the secondary tone. Both tones are
 * generated once at Init() as 16-bit PCM WAV byte buffers (sine wave, 4ms exponential attack, exponential
 * release) rather than shipped as audio assets, so a future tweak to frequency/duration/gain is a one-line
 * change here instead of re-recording a clip - same reasoning as the style guide's color ramp formula.
 *
 */

using System;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace CNC.Controls
{
    public static class ButtonClickSound
    {
        private const int SampleRate = 44100;

        private static SoundPlayer primaryPlayer;
        private static SoundPlayer secondaryPlayer;
        private static bool initialized;

        // Hooked once at app startup (App.xaml.cs). A class handler on ButtonBase.ClickEvent fires for every
        // Button in the app - including ones created after this call, and regardless of which XAML they live
        // in - so no per-control or per-XAML opt-in is needed to get app-wide coverage.
        public static void Init()
        {
            if (initialized)
                return;
            initialized = true;

            primaryPlayer = new SoundPlayer();
            primaryPlayer.Stream = BuildClickWav(frequencyHz: 1180d, durationMs: 70d, peakGain: 0.09d);
            primaryPlayer.Load();

            secondaryPlayer = new SoundPlayer();
            secondaryPlayer.Stream = BuildClickWav(frequencyHz: 620d, durationMs: 50d, peakGain: 0.05d);
            secondaryPlayer.Load();

            EventManager.RegisterClassHandler(typeof(ButtonBase), ButtonBase.ClickEvent, new RoutedEventHandler(OnClick));
        }

        private static void OnClick(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null)
                return;

            bool primary = button.Style != null && ReferenceEquals(button.Style, Application.Current.TryFindResource("PrimaryButtonStyle"));

            try
            {
                (primary ? primaryPlayer : secondaryPlayer)?.Play();
            }
            catch
            {
                // Never let sound playback take down a button click - e.g. no audio device present.
            }
        }

        // Renders a mono 16-bit PCM WAV: a sine tone with a 4ms exponential attack and an exponential
        // release to silence over the remainder of the duration - matches the envelope demonstrated live
        // in the style guide artifact's Web Audio version, ported here to canned PCM since WPF has no
        // built-in oscillator API.
        private static MemoryStream BuildClickWav(double frequencyHz, double durationMs, double peakGain)
        {
            int sampleCount = (int)(SampleRate * durationMs / 1000d);
            const double attackSeconds = 0.004d;
            int attackSamples = Math.Min(sampleCount, (int)(SampleRate * attackSeconds));

            var samples = new short[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                double t = i / (double)SampleRate;
                double envelope;
                if (i < attackSamples)
                    envelope = ExponentialRamp(0.0001d, peakGain, i / (double)attackSamples);
                else
                    envelope = ExponentialRamp(peakGain, 0.0001d, (i - attackSamples) / (double)Math.Max(1, sampleCount - attackSamples));

                double value = Math.Sin(2d * Math.PI * frequencyHz * t) * envelope;
                samples[i] = (short)(Math.Max(-1d, Math.Min(1d, value)) * short.MaxValue);
            }

            return WriteWav(samples);
        }

        // Web Audio's exponentialRampToValueAtTime shape: value(u) = from * (to/from)^u, u in [0,1].
        private static double ExponentialRamp(double from, double to, double u)
        {
            u = Math.Max(0d, Math.Min(1d, u));
            return from * Math.Pow(to / from, u);
        }

        private static MemoryStream WriteWav(short[] samples)
        {
            const short channels = 1;
            const short bitsPerSample = 16;
            int byteRate = SampleRate * channels * (bitsPerSample / 8);
            short blockAlign = (short)(channels * (bitsPerSample / 8));
            int dataLength = samples.Length * (bitsPerSample / 8);

            var stream = new MemoryStream();
            using (var w = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
            {
                w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                w.Write(36 + dataLength);
                w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
                w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
                w.Write(16);
                w.Write((short)1); // PCM
                w.Write(channels);
                w.Write(SampleRate);
                w.Write(byteRate);
                w.Write(blockAlign);
                w.Write(bitsPerSample);
                w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                w.Write(dataLength);
                foreach (short s in samples)
                    w.Write(s);
            }

            stream.Position = 0;
            return stream;
        }
    }
}
