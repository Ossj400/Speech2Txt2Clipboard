using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SherpaOnnx;

namespace SpeechToTextAI;

/// <summary>
/// Voice recognition service using sherpa-onnx streaming transducer model.
/// </summary>
public sealed class VoiceRecognitionService : IDisposable
{
    private readonly string _modelDir;
    private OnlineRecognizer? _recognizer;
    private OnlineStream? _stream;
    private bool _initialized;
    private bool _disposed;

    private const int SampleRate = 16000;

    public VoiceRecognitionService(string modelDirectory)
    {
        _modelDir = modelDirectory;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_modelDir))
            throw new DirectoryNotFoundException($"Model directory not found: {_modelDir}");

        var encoderPath = Path.Combine(_modelDir, "encoder-epoch-99-avg-1-chunk-16-left-128.onnx");
        var decoderPath = Path.Combine(_modelDir, "decoder-epoch-99-avg-1-chunk-16-left-128.onnx");
        var joinerPath = Path.Combine(_modelDir, "joiner-epoch-99-avg-1-chunk-16-left-128.onnx");
        var tokensPath = Path.Combine(_modelDir, "tokens.txt");

        if (!File.Exists(encoderPath))
            throw new FileNotFoundException("Encoder model not found.", encoderPath);
        if (!File.Exists(decoderPath))
            throw new FileNotFoundException("Decoder model not found.", decoderPath);
        if (!File.Exists(joinerPath))
            throw new FileNotFoundException("Joiner model not found.", joinerPath);
        if (!File.Exists(tokensPath))
            throw new FileNotFoundException("Tokens file not found.", tokensPath);

        await Task.Run(() =>
        {
            var config = new OnlineRecognizerConfig
            {
                FeatConfig = new FeatureConfig
                {
                    SampleRate = SampleRate,
                    FeatureDim = 80
                },
                ModelConfig = new OnlineModelConfig
                {
                    Transducer = new OnlineTransducerModelConfig
                    {
                        Encoder = encoderPath,
                        Decoder = decoderPath,
                        Joiner = joinerPath
                    },
                    Tokens = tokensPath,
                    NumThreads = 2,
                    Provider = "cpu",
                    Debug = 0
                },
                DecodingMethod = "greedy_search",
                EnableEndpoint = 1,
                Rule1MinTrailingSilence = 2.4f,
                Rule2MinTrailingSilence = 0.8f,
                Rule3MinUtteranceLength = 20.0f
            };

            _recognizer = new OnlineRecognizer(config);
            _stream = _recognizer.CreateStream();

        }, cancellationToken);

        _initialized = true;
        System.Diagnostics.Debug.WriteLine("Sherpa-ONNX streaming recognizer initialized.");
    }

    /// <summary>
    /// Recognize speech from raw PCM audio data (16-bit, mono, 16kHz).
    /// </summary>
    public async Task<string> RecognizeAsync(byte[] audioData, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_initialized || _recognizer is null || _stream is null)
            throw new InvalidOperationException("Service is not initialized.");

        if (audioData is null || audioData.Length == 0)
            return string.Empty;

        return await Task.Run(() =>
        {
            // Convert PCM bytes to float samples
            var samples = ConvertBytesToFloatSamples(audioData);

            // Accept waveform
            _stream.AcceptWaveform(SampleRate, samples);

            // Decode
            while (_recognizer.IsReady(_stream))
            {
                _recognizer.Decode(_stream);
            }

            // Get result
            var result = _recognizer.GetResult(_stream);
            return result.Text.Trim();

        }, cancellationToken);
    }

    /// <summary>
    /// Process audio stream and return recognized text.
    /// </summary>
    public async Task<string> RecognizeAsync(Stream audioStream, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await audioStream.CopyToAsync(buffer, cancellationToken);
        return await RecognizeAsync(buffer.ToArray(), cancellationToken);
    }

    /// <summary>
    /// Process audio in streaming mode - call this repeatedly with audio chunks.
    /// </summary>
    public void AcceptAudioChunk(float[] samples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_initialized || _recognizer is null || _stream is null)
            throw new InvalidOperationException("Service is not initialized.");

        _stream.AcceptWaveform(SampleRate, samples);
    }

    /// <summary>
    /// Process audio chunk in bytes (16-bit PCM).
    /// </summary>
    public void AcceptAudioChunk(byte[] pcmData)
    {
        var samples = ConvertBytesToFloatSamples(pcmData);
        AcceptAudioChunk(samples);
    }

    /// <summary>
    /// Decode pending audio and return intermediate result.
    /// </summary>
    public string DecodeAndGetResult()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_initialized || _recognizer is null || _stream is null)
            throw new InvalidOperationException("Service is not initialized.");

        while (_recognizer.IsReady(_stream))
        {
            _recognizer.Decode(_stream);
        }

        var result = _recognizer.GetResult(_stream);
        return result.Text;
    }

    /// <summary>
    /// Check if endpoint was detected (pause in speech).
    /// </summary>
    public bool IsEndpoint()
    {
        if (_recognizer is null || _stream is null)
            return false;

        return _recognizer.IsEndpoint(_stream);
    }

    /// <summary>
    /// Reset the stream for new utterance after endpoint detection.
    /// </summary>
    public void Reset()
    {
        if (_recognizer is null || _stream is null)
            return;

        _recognizer.Reset(_stream);
    }

    /// <summary>
    /// Signal that input has finished (for final decoding).
    /// </summary>
    public void InputFinished()
    {
        _stream?.InputFinished();
    }

    /// <summary>
    /// Get final text and reset stream for next utterance.
    /// </summary>
    public string GetFinalResultAndReset()
    {
        var text = DecodeAndGetResult();
        Reset();
        return text.Trim();
    }

    /// <summary>
    /// Recognition result with text and token information.
    /// </summary>
    public record RecognitionResult(string Text, int TokenCount);

    /// <summary>
    /// Recognize speech and return result with confidence information.
    /// Filters out low-quality results based on token count and text length.
    /// </summary>
    public async Task<RecognitionResult> RecognizeWithConfidenceAsync(byte[] audioData, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_initialized || _recognizer is null || _stream is null)
            throw new InvalidOperationException("Service is not initialized.");

        if (audioData is null || audioData.Length == 0)
            return new RecognitionResult(string.Empty, 0);

        return await Task.Run(() =>
        {
            var samples = ConvertBytesToFloatSamples(audioData);
            _stream.AcceptWaveform(SampleRate, samples);

            while (_recognizer.IsReady(_stream))
            {
                _recognizer.Decode(_stream);
            }

            var result = _recognizer.GetResult(_stream);
            var text = result.Text.Trim();
            
            // Get token count from the result
            var tokens = result.Tokens;
            int tokenCount = tokens?.Length ?? 0;

            // Filter criteria:
            // 1. Must have actual text
            // 2. Must have at least 1 token 
            // 3. Text should be at least 2 characters (to filter single character noise)
            if (string.IsNullOrWhiteSpace(text) || tokenCount < 1 || text.Length < 2)
            {
                return new RecognitionResult(string.Empty, 0);
            }

            return new RecognitionResult(text, tokenCount);

        }, cancellationToken);
    }

    private static float[] ConvertBytesToFloatSamples(byte[] audioData)
    {
        int sampleCount = audioData.Length / 2;
        var samples = new float[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            short sample16 = BitConverter.ToInt16(audioData, i * 2);
            samples[i] = sample16 / 32768f;
        }

        return samples;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _stream?.Dispose();
            _recognizer?.Dispose();
            _stream = null;
            _recognizer = null;
            _disposed = true;
        }
    }
}
