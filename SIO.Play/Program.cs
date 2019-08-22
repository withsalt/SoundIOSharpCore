using NAudio.Wave;
using SoundIOSharpCore;
using System;
using System.IO;

namespace SIO.Play
{
    class Program
    {
        static SoundIO _soundIO;
        static WaveFileReader _waveFile;
        static ISampleProvider _sampleProvider;
        static int _channels;

        static bool _fileDone;
        static bool _startSilence;
        static double _latencySeconds;
        static int _silentSamplesRemaining;
        static int _silentSamplesAlreadySent;

        static int Usage()
        {
            Console.Error.WriteLine("Usage: {0} [options]\n" +
                "Options:\n" +
                "\t[--backend dummy|alsa|pulseaudio|jack|coreaudio|wasapi]\n" +
                "\t[--device id]\n" +
                "\tFileName",
                "SoundIOPlay");
            return 1;
        }

        public static int Main(string[] args)
        {
            int sampleRate;
            string filename = null;
            SoundIoBackend backend = SoundIoBackend.None;
            string deviceId = null;
            bool isRaw = false;
            SoundIoError err;

            try
            {
                if (args.Length < 1)
                {
                    Usage();
                    return 1;
                }
                for (int i = 0; i < args.Length; i++)
                {
                    string arg = args[i];
                    if (arg.StartsWith("--"))
                    {
                        if (++i > args.Length)
                            return Usage();
                        else if (arg.CompareTo("--backend") == 0)
                            backend = (SoundIoBackend)Enum.Parse(typeof(SoundIoBackend), args[i]);
                        else if (arg.CompareTo("--device") == 0)
                            deviceId = args[i];
                        else
                            return Usage();
                    }
                    else
                    {
                        if (File.Exists(args[i]))
                        {
                            filename = args[i];
                        }
                        else
                        {
                            Usage();
                            return 1;
                        }
                    }
                }
                if (string.IsNullOrEmpty(filename))
                {
                    throw new Exception("Input file name can not null.");
                }
            }
            catch (IndexOutOfRangeException)
            {
                Usage();
                return 1;
            }

            using (_soundIO = new SoundIO())
            {
                using (_waveFile = new WaveFileReader(filename))
                {
                    _channels = _waveFile.WaveFormat.Channels;
                    sampleRate = _waveFile.WaveFormat.SampleRate;
                    _soundIO.Connect();
                    _soundIO.FlushEvents();
                    SoundIODevice device = null;

                    if (deviceId != null)
                    {
                        foreach (var dev in _soundIO)
                        {
                            if (dev.Aim == SoundIoDeviceAim.Output && dev.Id.Equals(deviceId) && dev.IsRaw == isRaw)
                            {
                                device = dev;
                                break;
                            }
                        }

                        if (device == null)
                        {
                            Console.Error.WriteLine("Output device not found.");
                            return 1;
                        }

                        device.AddRef(); // Enumerator cleans up itself on dispose
                    }
                    else
                    {
                        device = _soundIO.GetDefaultOutputDevice();
                    }
                    Console.WriteLine("Device: {0}.", device.Name);
                    if (device.ProbeError != SoundIoError.None)
                    {
                        Console.WriteLine("Cannot probe device: {0}", device.ProbeError);
                        return 1;
                    }
                    Console.WriteLine("Output device: {0}", device.Name);
                    if (device.ProbeError != SoundIoError.None)
                    {
                        Console.WriteLine("Cannot probe device: {0}", device.ProbeError);
                        return 1;
                    }
                    var outstream = new SoundIOOutStream(device)
                    {
                        OnWriteCallback = WriteCallback,
                        OnUnderflowCallback = UnderflowCallback,
                        Name = "sio_play",
                        SampleRate = sampleRate
                    };

                    // look for maching layout for wav file...
                    var foundLayout = false;
                    foreach (var layout in device.Layouts)
                    {
                        if (layout.ChannelCount == _channels)
                        {
                            outstream.Layout = layout;
                            foundLayout = true;
                            break;
                        }
                    }

                    // TODO: may need to look at endian issues and other formats...
                    // when paired with NAudioLite, ISampleProvider the conversion to Float32 is automatic.
                    if (device.SupportsFormat(SoundIoFormats.Float32NE))
                    {
                        outstream.Format = SoundIoFormats.Float32NE;
                    }
                    else if (device.SupportsFormat(SoundIoFormats.Float64NE))
                    {
                        outstream.Format = SoundIoFormats.Float64NE;
                    }
                    else if (device.SupportsFormat(SoundIoFormats.S32NE))
                    {
                        outstream.Format = SoundIoFormats.S32NE;
                    }
                    else if (device.SupportsFormat(SoundIoFormats.S16NE))
                    {
                        outstream.Format = SoundIoFormats.S16NE;
                    }
                    else
                    {
                        Console.WriteLine("No suitable device format available.");
                        return 1;
                    }

                    Console.WriteLine();
                    Console.WriteLine("Playing file: {0}, Format: {1}", Path.GetFullPath(filename), _waveFile.WaveFormat);

                    err = outstream.Open();
                    if (err != SoundIoError.None)
                    {
                        Console.WriteLine($"Unable to open device: {err.GetErrorMessage()}, with sample rate: {outstream.LayoutError}");
                        return 1;
                    }

                    if (outstream.LayoutError != SoundIoError.None)
                    {
                        Console.WriteLine($"Unable to set channel layout: {err.GetErrorMessage()}");
                    }

                    // revisit layout...
                    // if no suitable layout found
                    if (!foundLayout)
                    {
                        Console.WriteLine("No native channel layout found, Device Channels: {0}, Wav File Channels: {1}, requires sampler...", outstream.Layout.ChannelCount, _channels);
                    }

                    // get sample provider that matches outstream.Layout
                    if (outstream.Layout.ChannelCount == 1)
                    { 
                        // mono
                        if (_waveFile.WaveFormat.Channels == 1)
                        {
                            _sampleProvider = _waveFile.ToSampleProvider();
                        }
                        else
                        {
                            _sampleProvider = _waveFile.ToSampleProvider().ToMono();
                        }
                    }
                    else if (outstream.Layout.ChannelCount == 2)
                    { 
                        //stereo
                        if (_waveFile.WaveFormat.Channels == 1)
                        {
                            _sampleProvider = _waveFile.ToSampleProvider().ToStereo();
                        }
                        else
                        {
                            _sampleProvider = _waveFile.ToSampleProvider();
                        }
                    }

                    outstream.Start();
                    _soundIO.OnBackendDisconnect += SoundIo_OnBackendDisconnected;

                    while (!_fileDone)
                    {
                        System.Threading.Thread.Sleep(100);
                        _soundIO.FlushEvents();
                    }

                    System.Threading.Thread.Sleep(500);
                    if (_fileDone && outstream != null)
                    {
                        outstream.Dispose();
                        outstream = null;
                    }

                    Console.WriteLine("End Program");
                    return 0;
                }
            }
        }

        public static void WriteCallback(SoundIOOutStream stream, int frameCountMin, int frameCountMax)
        {
            SoundIoError err;
            DateTime callbackTime = DateTime.Now;
            // loop to utilize as much as the frameCountMax as possible
            while (frameCountMax > 0)
            {
                int frameCount = frameCountMax;
                if ((err = stream.BeginWrite(out SoundIOChannelAreas areas, ref frameCount)) != SoundIoError.None)
                {
                    Console.WriteLine("unrecoverable stream error: {0}", err.GetErrorMessage());
                    _fileDone = true;
                }

                if (frameCount == 0)
                    break;

                var bufferCount = frameCount * stream.Layout.ChannelCount;

                // if buffer is done add silence based on latency to allow stream to complete through
                // audio path before stream is Disposed()
                if (_waveFile.Position >= _waveFile.Length)
                {
                    if (_startSilence)
                    {
                        // windows latency is a little higher (using DateTime to determine the callback time delay)
                        // and needs to be accoutned for...
                        _latencySeconds -= (DateTime.Now - callbackTime).TotalMilliseconds / 1000.0;
                        _silentSamplesRemaining = (int)((stream.SampleRate * stream.Layout.ChannelCount) * _latencySeconds);
                        _silentSamplesRemaining -= _silentSamplesAlreadySent;
                        _startSilence = false;
                    }

                    int silentBufferSize;
                    if (_silentSamplesRemaining > bufferCount)
                    {
                        silentBufferSize = bufferCount;
                        _silentSamplesRemaining -= bufferCount;
                    }
                    else
                    {
                        silentBufferSize = _silentSamplesRemaining;
                        _silentSamplesRemaining = 0;
                    }

                    if (silentBufferSize > 0)
                    {
                        // create a new buffer initialized to 0 and copy to native buffer
                        var silenceBuffer = new float[silentBufferSize];
                        stream.CopyTo(silenceBuffer, 0, areas, silentBufferSize);
                    }
                    if (_silentSamplesRemaining == 0)
                    {
                        _fileDone = true;
                        stream.EndWrite();
                        stream.Pause(true);
                        return;
                    }
                    // if the remaining audioBuffer will only partially fill the frameCount
                    // copy the remaining amount and set the startSilence flag to allow
                    // stream to play to the end.

                }
                else if (_waveFile.Position + (frameCount * _waveFile.WaveFormat.Channels) >= _waveFile.Length)
                {
                    float[] audioBuffer = new float[bufferCount];
                    var actualSamplesRead = _sampleProvider.Read(audioBuffer, 0, bufferCount);
                    stream.CopyTo(audioBuffer, 0, areas, bufferCount);

                    _silentSamplesAlreadySent = bufferCount - actualSamplesRead;
                    _latencySeconds = 0.0;
                    _startSilence = true;
                }
                else
                {
                    // copy audioBuffer data to native buffer and advance the bufferPos
                    float[] audioBuffer = new float[bufferCount];
                    var actualSamplesRead = _sampleProvider.Read(audioBuffer, 0, bufferCount);
                    stream.CopyTo(audioBuffer, 0, areas, actualSamplesRead);

                    if (_waveFile.Position >= _waveFile.Length)
                    {
                        _latencySeconds = 0.0;
                        _startSilence = true;
                    }
                }

                if ((err = stream.EndWrite()) != SoundIoError.None)
                {
                    if (err == SoundIoError.Underflow)
                        return;
                    Console.WriteLine("Unrecoverable stream error: {0}", err.GetErrorMessage());
                    _fileDone = true;
                }

                if (_startSilence)
                {
                    // get actual latency in order to compute number of silent frames
                    stream.GetLatency(out _latencySeconds);
                    callbackTime = DateTime.Now;
                }

                // loop until frameCountMax is used up
                frameCountMax -= frameCount;
            }
            return;
        }


        public static void UnderflowCallback(SoundIOOutStream stream)
        {
            Console.WriteLine("Underflow");
        }

        static void SoundIo_OnDevicesChanged(object sender, EventArgs e)
        {
            Console.WriteLine("OnDevicesChange");
        }

        static void SoundIo_OnBackendDisconnected(object sender, SoundIoError eventArgs)
        {
            Console.WriteLine("OnBackendDisconnect");
        }

        static void SoundIo_OnEvents(object sender, EventArgs e)
        {
            Console.WriteLine("OnEventsSignal");
        }

    }
}
